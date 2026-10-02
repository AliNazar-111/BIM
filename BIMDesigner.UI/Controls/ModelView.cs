using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

// Core has a Point3D of its own, in millimetres; this file draws with WPF's, in metres.
using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;
using MediaPoint3D = System.Windows.Media.Media3D.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A drag in the 3D view: what is being dragged, and the line of sight through the cursor, as
/// the eye and a point further along it, in millimetres.
/// </summary>
public readonly record struct ModelDrag(Guid Id, CorePoint3D From, CorePoint3D Through);

/// <summary>How the 3D view colours what it draws (specification section 6.2).</summary>
public enum VisualStyle
{
    /// <summary>Lit by the sun and the sky: the building as it looks.</summary>
    Shaded,

    /// <summary>Every surface its own colour, unlit: what each thing is made of, at a glance.</summary>
    FlatColours,

    /// <summary>Everything see-through, so what is behind and inside can be read.</summary>
    XRay
}

/// <summary>
/// The 3D viewport (specification section 6.1).
///
/// It draws meshes built by <see cref="ModelMeshBuilder"/> from the model on demand, and keeps
/// nothing of its own but the camera. Like the plan and the section it cannot show a building
/// the model does not describe - it is one more view of the same walls.
///
/// Model millimetres are drawn as metres. WPF's 3D pipeline works in single-precision floats,
/// and a building expressed in millimetres runs out of precision fast enough to make surfaces
/// shimmer against each other.
/// </summary>
public partial class ModelView : Border
{
    private const double MillimetresPerUnit = 1000;
    private const double FieldOfView = 45;

    /// <summary>Movement, in pixels, below which a press-and-release is a click rather than a drag.</summary>
    private const double ClickSlop = 4;

    private readonly Viewport3D _viewport = new() { ClipToBounds = true };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = FieldOfView, UpDirection = new Vector3D(0, 0, 1) };
    private readonly ModelVisual3D _scene = new();

    private readonly Dictionary<GeometryModel3D, Mesh3D> _meshOf = new();
    private readonly Dictionary<Mesh3D, MeshGeometry3D> _geometry = new();

    /// <summary>
    /// The edge lines, kept in their own group so zooming can resize them without rebuilding
    /// the whole scene.
    /// </summary>
    private readonly Model3DGroup _edgeGroup = new();

    /// <summary>Camera distance when the edges were last sized, so we know when to redo them.</summary>
    private double _edgeDistance;
    private readonly HashSet<Guid> _hiddenLevels = new();
    private HashSet<Guid> _selected = new();

    private BimDocument? _document;
    private IReadOnlyList<Mesh3D> _meshes = Array.Empty<Mesh3D>();
    private VisualStyle _style = VisualStyle.Shaded;
    private bool _hasFitted;

    // Orbit camera: a point looked at, and where the eye sits around it.
    private MediaPoint3D _target = new(0, 0, 0);
    private double _yaw = -135;
    private double _pitch = 28;
    private double _distance = 30;

    private enum Gesture { None, Orbit, Pan, Drag }

    private Gesture _gesture = Gesture.None;

    /// <summary>What a left drag that began on it is moving, rather than orbiting the view.</summary>
    private Guid _dragged;
    private Point _gestureStart;
    private Point _gestureLast;
    private bool _movedBeyondClick;

    public ModelView()
    {
        Focusable = true;

        // A gradient rather than a flat fill: it reads as a horizon, which gives the eye
        // something to orient against when the model is small in the view.
        Background = new LinearGradientBrush(
            AppTheme.Pick(Color.FromRgb(0xC4, 0xD6, 0xE8), Color.FromRgb(0x25, 0x2B, 0x36)),
            AppTheme.Pick(Color.FromRgb(0xF4, 0xF6, 0xF8), Color.FromRgb(0x11, 0x14, 0x1A)),
            new Point(0.5, 0),
            new Point(0.5, 1));

        _viewport.Camera = _camera;
        _viewport.Children.Add(_scene);

        // The levels and gridlines of an elevation are drawn over the model, not in it.
        _overlay = new ElevationOverlay(this);
        var layers = new Grid();
        layers.Children.Add(_viewport);
        layers.Children.Add(_overlay);
        Child = layers;

        UpdateCamera();
    }

    public BimDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;

            _document = value;
            _hiddenLevels.Clear();
            _hasFitted = false;
            Rebuild();
        }
    }

    public VisualStyle RenderStyle
    {
        get => _style;
        set
        {
            if (_style == value) return;

            _style = value;
            Compose();
        }
    }

    /// <summary>Raised with the element clicked on and the point on it, or null for empty space.</summary>
    public event EventHandler<(Guid Id, CorePoint3D At)?>? ElementClicked;

    /// <summary>Raised with the element right-clicked on, to ask what should be done with it.</summary>
    public event EventHandler<Guid>? ElementMenuRequested;

    /// <summary>
    /// Whether a selected element can be dragged across the model - a roof window along its
    /// roof. A left drag that begins on one moves it; anywhere else it orbits the view.
    /// </summary>
    public Func<Guid, bool>? CanDrag { get; set; }

    /// <summary>Raised as a selected element is dragged, with the line of sight through the cursor.</summary>
    public event EventHandler<ModelDrag>? ElementDragged;

    /// <summary>Raised when a drag of an element ends, so the move can be recorded as one step.</summary>
    public event EventHandler<Guid>? ElementDragEnded;

    // ---- content ----------------------------------------------------------------

    /// <summary>Rebuilds every mesh from the model. Cheap enough to do after every edit.</summary>
    public void Rebuild()
    {
        _meshes = _document is null ? Array.Empty<Mesh3D>() : ModelMeshBuilder.Build(_document, _document.ViewSettings.FilterFor(_document, ViewReference.Model3D));

        // Converted once per rebuild. Selecting something or changing the style only swaps
        // materials, and should not pay to copy every vertex again.
        _geometry.Clear();
        foreach (var mesh in _meshes) _geometry[mesh] = ToGeometry(mesh);

        Compose();

        // The camera is fitted once, when there is first something to fit. Refitting after
        // every edit would yank the view away from whatever was being looked at.
        if (!_hasFitted && _meshes.Count > 0 && ActualWidth > 0)
        {
            ZoomToFit();
            _hasFitted = true;
        }
    }

    private readonly HashSet<MeshKind> _hiddenKinds = new();
    private bool _showGround = true;
    private bool _showEdges = true;

    /// <summary>
    /// Whether the model's edges are drawn over the shading.
    ///
    /// This is the difference between a drawing and a lump. Two faces of a similar tone have
    /// nothing between them without a line, so a shaded-only building reads as one mass - and
    /// the storey junctions, wall corners and layer boundaries that are all genuinely there
    /// simply cannot be seen.
    /// </summary>
    public bool ShowEdges
    {
        get => _showEdges;
        set
        {
            if (_showEdges == value) return;

            _showEdges = value;
            BuildEdges();
        }
    }

    /// <summary>
    /// Shows or hides a whole kind of thing - every roof, every ceiling. A building with a roof
    /// on is a closed box from outside, and lifting the roof off is how anyone looks in.
    /// </summary>
    public void SetKindVisible(MeshKind kind, bool visible)
    {
        var changed = visible ? _hiddenKinds.Remove(kind) : _hiddenKinds.Add(kind);
        if (changed) Compose();
    }

    public bool IsKindVisible(MeshKind kind) => !_hiddenKinds.Contains(kind);

    /// <summary>
    /// Whether the ground is drawn. Turning it off shows what is below ground level - the
    /// ground-floor slab, and any basement.
    /// </summary>
    public bool ShowGround
    {
        get => _showGround;
        set
        {
            if (_showGround == value) return;

            _showGround = value;
            Compose();
        }
    }

    public void SetLevelVisible(Guid levelId, bool visible)
    {
        var changed = visible ? _hiddenLevels.Remove(levelId) : _hiddenLevels.Add(levelId);
        if (changed) Compose();
    }

    public bool IsLevelVisible(Guid levelId) => !_hiddenLevels.Contains(levelId);

    public void SetSelection(IEnumerable<Guid> elementIds)
    {
        var next = elementIds.ToHashSet();
        if (next.SetEquals(_selected)) return;

        _selected = next;
        Compose();
    }

    /// <summary>
    /// Puts the meshes into the scene with the current style, visibility and selection.
    ///
    /// Anything see-through is added after everything solid. WPF draws in the order it is
    /// given and does not sort, so glass added first would hide the walls behind it rather
    /// than showing them through it.
    /// </summary>
    private void Compose()
    {
        _meshOf.Clear();

        var solid = new Model3DGroup();
        var clear = new Model3DGroup();

        foreach (var mesh in _meshes)
        {
            if (_hiddenLevels.Contains(mesh.LevelId)) continue;
            if (IsHiddenKind(mesh.Kind)) continue;

            if (!_geometry.TryGetValue(mesh, out var geometry)) continue;
            var material = MaterialFor(mesh);

            var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
            _meshOf[model] = mesh;

            var seeThrough = mesh.Opacity < 1 || _style == VisualStyle.XRay;
            (seeThrough ? clear : solid).Children.Add(model);
        }

        var root = new Model3DGroup();
        AddLights(root);
        AddGround(root);
        root.Children.Add(solid);
        root.Children.Add(_edgeGroup);
        root.Children.Add(clear);

        _scene.Content = root;

        BuildEdges();
    }

    // ---- edges ------------------------------------------------------------------

    /// <summary>
    /// Builds the edge lines at a width suited to the current camera distance.
    ///
    /// WPF's 3D pipeline has no line primitive, so each edge becomes a thin cross of two
    /// quads standing along it. A cross rather than a single quad because a flat ribbon
    /// disappears when you orbit round to look at it edge-on, and a line that vanishes at
    /// certain angles is worse than no line at all.
    /// </summary>
    private void BuildEdges()
    {
        _edgeGroup.Children.Clear();
        _edgeDistance = _distance;

        if (!_showEdges) return;

        // Scaled with distance so the lines stay about a pixel wide however far out you are.
        // Fixed world thickness would be hairline across a site and a fence post up close.
        var half = Math.Max(_distance * 0.0009, 0.0015);

        var plain = new EdgeMesh();
        var selected = new EdgeMesh();

        foreach (var mesh in _meshes)
        {
            if (_hiddenLevels.Contains(mesh.LevelId)) continue;
            if (IsHiddenKind(mesh.Kind)) continue;

            // Glass has no edges worth drawing: outlining every pane makes a window read as a
            // solid panel, which is the opposite of what it is.
            if (mesh.Kind == MeshKind.Glazing) continue;

            var target = _selected.Contains(mesh.ElementId) || _selected.Contains(mesh.OwnerId) ? selected : plain;
            var width = ReferenceEquals(target, selected) ? half * 2.2 : half;

            foreach (var (from, to) in mesh.Edges) target.Add(ToUnits(from), ToUnits(to), width);
        }

        AddEdgeModel(plain, AppTheme.Pick(Color.FromRgb(0x3A, 0x3E, 0x44), Color.FromRgb(0x14, 0x17, 0x1C)));
        AddEdgeModel(selected, Color.FromRgb(0x4A, 0x9B, 0xFF));
    }

    private void AddEdgeModel(EdgeMesh edges, Color colour)
    {
        if (edges.IsEmpty) return;

        // Unlit: a black diffuse base with the colour emitted over it, so a line is the same
        // weight on a face turned towards the light as on one turned away.
        var brush = new SolidColorBrush(colour);
        brush.Freeze();

        var material = new MaterialGroup
        {
            Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) }
        };
        material.Freeze();

        _edgeGroup.Children.Add(new GeometryModel3D(edges.ToGeometry(), material) { BackMaterial = material });
    }

    private static MediaPoint3D ToUnits(CorePoint3D point) =>
        new(point.X / MillimetresPerUnit, point.Y / MillimetresPerUnit, point.Z / MillimetresPerUnit);

    /// <summary>Accumulates edge crosses into one mesh, so the whole model's lines cost one model.</summary>
    private sealed class EdgeMesh
    {
        private readonly Point3DCollection _positions = new();
        private readonly Int32Collection _indices = new();

        public bool IsEmpty => _indices.Count == 0;

        public void Add(MediaPoint3D from, MediaPoint3D to, double half)
        {
            var along = to - from;
            if (along.Length < 1e-9) return;

            along.Normalize();

            // Any two directions across the line will do; the cross looks the same either way.
            var reference = Math.Abs(along.Z) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 0, 1);

            var across = Vector3D.CrossProduct(along, reference);
            across.Normalize();

            var other = Vector3D.CrossProduct(along, across);
            other.Normalize();

            AddQuad(from, to, across * half);
            AddQuad(from, to, other * half);
        }

        private void AddQuad(MediaPoint3D from, MediaPoint3D to, Vector3D offset)
        {
            var start = _positions.Count;

            _positions.Add(from + offset);
            _positions.Add(to + offset);
            _positions.Add(to - offset);
            _positions.Add(from - offset);

            foreach (var index in new[] { 0, 1, 2, 0, 2, 3 }) _indices.Add(start + index);
        }

        public MeshGeometry3D ToGeometry()
        {
            var geometry = new MeshGeometry3D { Positions = _positions, TriangleIndices = _indices };
            geometry.Freeze();
            return geometry;
        }
    }

    /// <summary>A door leaf and a pane go with the walls they sit in, not a kind of their own.</summary>
    private bool IsHiddenKind(MeshKind kind) =>
        _hiddenKinds.Contains(kind is MeshKind.Glazing or MeshKind.DoorLeaf or MeshKind.Mullion ? MeshKind.Wall : kind);

    private static MeshGeometry3D ToGeometry(Mesh3D mesh)
    {
        var positions = new Point3DCollection(mesh.Positions.Count);
        foreach (var p in mesh.Positions)
            positions.Add(new MediaPoint3D(p.X / MillimetresPerUnit, p.Y / MillimetresPerUnit, p.Z / MillimetresPerUnit));

        var indices = new Int32Collection(mesh.Indices);

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };

        // A curved surface built flat says which way it faces at each point, so it is shaded
        // as the curve; otherwise the faces' own directions are used.
        if (mesh.Normals is { } normals && normals.Count == mesh.Positions.Count)
        {
            var directions = new Vector3DCollection(normals.Count);
            foreach (var n in normals) directions.Add(new Vector3D(n.X, n.Y, n.Z));
            geometry.Normals = directions;
        }

        geometry.Freeze();
        return geometry;
    }

    private Material MaterialFor(Mesh3D mesh)
    {
        var colour = Color.FromRgb(mesh.Colour.R, mesh.Colour.G, mesh.Colour.B);
        var isSelected = _selected.Contains(mesh.ElementId) || _selected.Contains(mesh.OwnerId);

        var opacity = _style == VisualStyle.XRay ? Math.Min(mesh.Opacity, 0.28) : mesh.Opacity;
        colour.A = (byte)Math.Round(opacity * 255);

        var brush = new SolidColorBrush(colour);
        brush.Freeze();

        Material material = _style switch
        {
            // Unlit: a black diffuse base, so only the emissive colour shows and no face is
            // darker than another because of which way it points.
            VisualStyle.FlatColours => new MaterialGroup
            {
                Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) }
            },
            _ => new DiffuseMaterial(brush)
        };

        if (isSelected)
        {
            // A glow rather than a recolour, so a selected wall still shows what it is made of.
            var highlight = new SolidColorBrush(Color.FromArgb(0x90, 0x2E, 0x7F, 0xE0));
            highlight.Freeze();

            material = new MaterialGroup { Children = { material, new EmissiveMaterial(highlight) } };
        }

        material.Freeze();
        return material;
    }

    /// <summary>
    /// A sun, a cool fill from the opposite side, and a dim bounce from below.
    ///
    /// The ambient light is deliberately low. Ambient light flattens everything equally, so
    /// the more of it there is the less the faces differ - and telling one face of a building
    /// from another is the whole job. One sun alone would be the other extreme: the faces
    /// turned away from it go black, and a black wall reads as a hole.
    /// </summary>
    private static void AddLights(Model3DGroup root)
    {
        root.Children.Add(new AmbientLight(AppTheme.Pick(Color.FromRgb(0x5C, 0x5E, 0x62), Color.FromRgb(0x33, 0x36, 0x3C))));

        // The sun, from over the viewer's left shoulder - the convention that makes a
        // three-quarter view read as solid.
        root.Children.Add(new DirectionalLight(Color.FromRgb(0xF2, 0xEC, 0xDE), new Vector3D(-0.45, 0.75, -1.0)));

        // Cool sky fill on the shaded side, so it is dark but still legible.
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x5C, 0x70), new Vector3D(0.85, -0.35, -0.25)));

        // Light back off the ground, which stops undersides going solid black: enough that a
        // soffit under the eaves, a balcony's underside, still shows its colour.
        root.Children.Add(new DirectionalLight(Color.FromRgb(0x74, 0x74, 0x6C), new Vector3D(0.1, 0.1, 1.0)));
    }

    /// <summary>
    /// Where the ground surface is, in millimetres: just under the project's zero, which is
    /// ground level.
    ///
    /// It used to go under the lowest thing in the model. That is the bottom of the ground-floor
    /// slab, which hangs below its level by its whole build-up - so the slab's edge showed as a
    /// band under the walls and the building looked as if it were sitting in a tray. A ground
    /// slab is below ground; so is a basement. Drawing the ground where the ground is hides them
    /// the way the real ground does, and the Ground tick box shows them again.
    ///
    /// The small drop keeps it from flickering against a floor whose top is exactly at zero.
    /// </summary>
    private const double GroundElevation = -10;

    /// <summary>
    /// A faint ground plane at ground level. Without it a building floats in a void and it is
    /// surprisingly hard to tell which way is down once you have orbited.
    /// </summary>
    private void AddGround(Model3DGroup root)
    {
        if (!_showGround) return;
        if (ModelMeshBuilder.Bounds(_meshes) is not { } bounds) return;

        var margin = Math.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Y - bounds.Min.Y) * 0.4 + 2000;
        var z = GroundElevation / MillimetresPerUnit;

        var minX = (bounds.Min.X - margin) / MillimetresPerUnit;
        var maxX = (bounds.Max.X + margin) / MillimetresPerUnit;
        var minY = (bounds.Min.Y - margin) / MillimetresPerUnit;
        var maxY = (bounds.Max.Y + margin) / MillimetresPerUnit;

        var ground = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(minX, minY, z), new(maxX, minY, z), new(maxX, maxY, z), new(minX, maxY, z)
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };
        ground.Freeze();

        var brush = new SolidColorBrush(AppTheme.Pick(Color.FromRgb(0xC8, 0xCC, 0xC4), Color.FromRgb(0x26, 0x2B, 0x33)));
        brush.Freeze();

        var material = new MaterialGroup
        {
            Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) }
        };
        material.Freeze();

        // Front face only, and it faces up. Seen from underneath it simply is not there, so
        // orbiting below ground shows the foundations instead of the underside of a sheet.
        root.Children.Add(new GeometryModel3D(ground, material));
    }

    // ---- camera -----------------------------------------------------------------

    /// <summary>Frames everything visible, from wherever the camera is currently looking.</summary>
    public void ZoomToFit()
    {
        if (_elevation is not null)
        {
            ElevationZoomToFit();
            return;
        }

        var visible = _meshes.Where(mesh => !_hiddenLevels.Contains(mesh.LevelId));

        if (ModelMeshBuilder.Bounds(visible) is not { } bounds)
        {
            _target = new MediaPoint3D(0, 0, 0);
            _distance = 30;
            UpdateCamera();
            return;
        }

        _target = new MediaPoint3D(
            (bounds.Min.X + bounds.Max.X) / 2 / MillimetresPerUnit,
            (bounds.Min.Y + bounds.Max.Y) / 2 / MillimetresPerUnit,
            (bounds.Min.Z + bounds.Max.Z) / 2 / MillimetresPerUnit);

        var dx = (bounds.Max.X - bounds.Min.X) / MillimetresPerUnit;
        var dy = (bounds.Max.Y - bounds.Min.Y) / MillimetresPerUnit;
        var dz = (bounds.Max.Z - bounds.Min.Z) / MillimetresPerUnit;

        // Far enough back that a sphere round the whole model fits the narrower field of view.
        var radius = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2;
        var aspect = ActualHeight > 0 ? ActualWidth / ActualHeight : 1;
        var halfAngle = FieldOfView / 2 * Math.PI / 180;
        var narrowest = aspect >= 1 ? halfAngle : Math.Atan(Math.Tan(halfAngle) * aspect);

        _distance = Math.Max(radius / Math.Sin(narrowest) * 1.08, 1);
        UpdateCamera();
    }

    /// <summary>Which way the camera looks from, in degrees: round from east, anticlockwise.</summary>
    public double Yaw => _yaw;

    /// <summary>How far above the horizon the camera is, in degrees.</summary>
    public double Pitch => _pitch;

    /// <summary>Raised whenever the camera moves, so the ViewCube can turn with it.</summary>
    public event EventHandler? CameraChanged;

    /// <summary>The steepest the camera looks down or up: just short of straight, where "up" is lost.</summary>
    public const double MaxPitch = 89.5;

    // A turn to a new direction, eased over a moment rather than jumped, so it is plain which
    // way the model turned.
    private (double Yaw, double Pitch, double ToYaw, double ToPitch, DateTime Started)? _turn;
    private static readonly TimeSpan TurnTime = TimeSpan.FromMilliseconds(320);

    /// <summary>Turns the camera to look from a direction, round the same point and at the same distance.</summary>
    public void LookFrom(double yaw, double pitch)
    {
        if (_elevation is not null) return;
        pitch = Math.Clamp(pitch, -MaxPitch, MaxPitch);

        // The short way round.
        var turn = ((yaw - _yaw) % 360 + 540) % 360 - 180;
        if (_turn is null) CompositionTarget.Rendering += OnTurnFrame;
        _turn = (_yaw, _pitch, _yaw + turn, pitch, DateTime.Now);
    }

    private void OnTurnFrame(object? sender, EventArgs e)
    {
        if (_turn is not { } turn) return;

        var t = Math.Min(1, (DateTime.Now - turn.Started).TotalMilliseconds / TurnTime.TotalMilliseconds);
        var eased = 1 - Math.Pow(1 - t, 3);
        _yaw = turn.Yaw + (turn.ToYaw - turn.Yaw) * eased;
        _pitch = turn.Pitch + (turn.ToPitch - turn.Pitch) * eased;
        UpdateCamera();

        if (t < 1) return;
        _turn = null;
        CompositionTarget.Rendering -= OnTurnFrame;
    }

    /// <summary>Turns the camera round the model by this much, as dragging does.</summary>
    public void Orbit(double yawDegrees, double pitchDegrees)
    {
        if (_elevation is not null) return;
        _yaw += yawDegrees;
        _pitch = Math.Clamp(_pitch + pitchDegrees, -MaxPitch, MaxPitch);
        UpdateCamera();
    }

    /// <summary>Moves the camera in toward what it looks at, or out: below 1 is in.</summary>
    public void Zoom(double factor)
    {
        _distance = Math.Clamp(_distance * factor, 0.5, 5000);
        UpdateCamera();
    }

    /// <summary>Back to the standard three-quarter view from the south-west.</summary>
    public void ResetView()
    {
        if (_elevation is null)
        {
            _yaw = -135;
            _pitch = 28;
        }

        ZoomToFit();
    }

    private void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180;
        var pitch = _pitch * Math.PI / 180;

        var offset = new Vector3D(
            Math.Cos(pitch) * Math.Cos(yaw),
            Math.Cos(pitch) * Math.Sin(yaw),
            Math.Sin(pitch)) * _distance;

        _camera.Position = _target + offset;
        _camera.LookDirection = -offset;

        // Clipping planes follow the distance, so a close look inside a room is not cut off
        // and a whole-site view does not lose the far corner.
        // The far plane is kept no further than it needs to be: the depth buffer's precision is
        // spread between the two, and a far plane miles away is what makes coplanar surfaces
        // flicker against each other.
        _camera.NearPlaneDistance = Math.Max(0.01, _distance * 0.005);
        _camera.FarPlaneDistance = _distance * 12 + 200;
        UpdateElevationCamera();
        CameraChanged?.Invoke(this, EventArgs.Empty);

        // Edge width follows the camera, but rebuilding on every wheel notch would be wasted
        // work - a quarter of the distance either way is the point at which it starts to show.
        if (_showEdges && _edgeDistance > 0 && Math.Abs(Math.Log(_distance / _edgeDistance)) > 0.22)
            BuildEdges();
    }

    // ---- input ------------------------------------------------------------------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        _gesture = e.ChangedButton == MouseButton.Left ? Gesture.Orbit : Gesture.Pan;

        // A left press on something selected that can be dragged picks it up rather than
        // turning the view.
        if (_gesture == Gesture.Orbit && CanDrag is not null &&
            HitTest(e.GetPosition(_viewport)) is { } pressed &&
            _selected.Contains(pressed.Id) && CanDrag(pressed.Id))
        {
            _gesture = Gesture.Drag;
            _dragged = pressed.Id;
        }

        _gestureStart = e.GetPosition(this);
        _gestureLast = _gestureStart;
        _movedBeyondClick = false;

        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pendingCorner is not null) _overlay.InvalidateVisual();
        if (_gesture == Gesture.None) return;

        var now = e.GetPosition(this);
        var delta = now - _gestureLast;
        _gestureLast = now;

        if ((now - _gestureStart).Length > ClickSlop) _movedBeyondClick = true;
        if (!_movedBeyondClick) return;

        if (_gesture == Gesture.Drag)
        {
            var (from, through) = RayThrough(e.GetPosition(_viewport));
            ElementDragged?.Invoke(this, new ModelDrag(_dragged, from, through));
            Cursor = Cursors.SizeAll;
            return;
        }

        // An elevation does not turn: a drag slides it.
        if (_gesture == Gesture.Orbit && _elevation is null)
        {
            _yaw -= delta.X * 0.4;

            // Stop short of straight up and straight down, where "up" stops meaning anything
            // and the view would flip.
            _pitch = Math.Clamp(_pitch + delta.Y * 0.4, -MaxPitch, MaxPitch);
        }
        else
        {
            Pan(delta);
        }

        UpdateCamera();
        Cursor = Cursors.SizeAll;
    }

    /// <summary>Slides the camera and its target sideways and up, in the plane of the screen.</summary>
    private void Pan(Vector delta)
    {
        var look = _camera.LookDirection;
        look.Normalize();

        var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1));
        if (right.Length < 1e-6) right = new Vector3D(1, 0, 0);
        right.Normalize();

        var up = Vector3D.CrossProduct(right, look);
        up.Normalize();

        // Scaled so the model under the cursor moves with it at the target's distance.
        var perPixel = 2 * _distance * Math.Tan(FieldOfView / 2 * Math.PI / 180) / Math.Max(ActualWidth, 1);

        _target += (-right * delta.X + up * delta.Y) * perPixel;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        // A right drag pans; a right click asks what to do with what is under it.
        if (e.ChangedButton == MouseButton.Right && !_movedBeyondClick &&
            HitTest(e.GetPosition(_viewport)) is { } picked)
        {
            _gesture = Gesture.None;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            ElementMenuRequested?.Invoke(this, picked.Id);
            return;
        }

        // A press on something draggable that never moved is still a click on it.
        var wasClick = _gesture is Gesture.Orbit or Gesture.Drag && !_movedBeyondClick;
        var wasDrag = _gesture == Gesture.Drag && _movedBeyondClick;

        _gesture = Gesture.None;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (wasClick) ElementClicked?.Invoke(this, HitTest(e.GetPosition(_viewport)));
        if (wasDrag) ElementDragEnded?.Invoke(this, _dragged);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture lost mid-drag - another window, Alt+Tab - ends it where it got to.
        var wasDrag = _gesture == Gesture.Drag && _movedBeyondClick;
        _gesture = Gesture.None;
        Cursor = Cursors.Arrow;

        if (wasDrag) ElementDragEnded?.Invoke(this, _dragged);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        _distance = Math.Clamp(_distance * Math.Pow(0.999, e.Delta), 0.5, 5000);
        UpdateCamera();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.F:
                ZoomToFit();
                e.Handled = true;
                break;

            case Key.Home:
                ResetView();
                e.Handled = true;
                break;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (!_hasFitted && _meshes.Count > 0 && ActualWidth > 0)
        {
            ZoomToFit();
            _hasFitted = true;
        }
    }

    /// <summary>
    /// The line of sight through a point of the view: the eye, and a point a metre along the
    /// line from it, in millimetres. What a drag follows across a surface - worked out from the
    /// camera rather than from what is under the cursor, so it still finds the roof where the
    /// thing being dragged has left a hole in it.
    /// </summary>
    private (CorePoint3D From, CorePoint3D Through) RayThrough(Point point)
    {
        if (_elevation is not null) return ElevationRay(point);

        var width = Math.Max(_viewport.ActualWidth, 1);
        var height = Math.Max(_viewport.ActualHeight, 1);

        var look = _camera.LookDirection;
        look.Normalize();

        var right = Vector3D.CrossProduct(look, _camera.UpDirection);
        if (right.Length < 1e-6) right = new Vector3D(1, 0, 0);
        right.Normalize();

        var up = Vector3D.CrossProduct(right, look);
        up.Normalize();

        // The field of view is across the width; up the height it is narrower by the aspect.
        var half = Math.Tan(_camera.FieldOfView / 2 * Math.PI / 180);
        var across = (2 * point.X / width - 1) * half;
        var upward = (1 - 2 * point.Y / height) * half * height / width;

        var eye = _camera.Position;
        var along = eye + look + right * across + up * upward;

        return (
            new CorePoint3D(eye.X * MillimetresPerUnit, eye.Y * MillimetresPerUnit, eye.Z * MillimetresPerUnit),
            new CorePoint3D(along.X * MillimetresPerUnit, along.Y * MillimetresPerUnit, along.Z * MillimetresPerUnit));
    }

    /// <summary>
    /// The nearest element under a point, and where on it the click landed - which is what
    /// lets a window be put into the wall at the place it was clicked. The ground is not an
    /// element, so a click on it passes through to nothing and clears the selection.
    /// </summary>
    private (Guid Id, CorePoint3D At)? HitTest(Point point)
    {
        (Guid Id, CorePoint3D At)? hit = null;

        // Results arrive nearest first, so the first one that belongs to an element is it.
        VisualTreeHelper.HitTest(_viewport, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult { ModelHit: GeometryModel3D model } ray &&
                _meshOf.TryGetValue(model, out var mesh))
            {
                var on = ray.PointHit;
                hit = (mesh.ElementId, new CorePoint3D(on.X * MillimetresPerUnit, on.Y * MillimetresPerUnit, on.Z * MillimetresPerUnit));
                return HitTestResultBehavior.Stop;
            }

            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));

        return hit;
    }
}
