using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>How a 3D view colours what it draws (specification section 6.2).</summary>
public enum VisualStyle
{
    /// <summary>Lit by the sun and the sky: the building as it looks.</summary>
    Shaded,

    /// <summary>Every surface its own colour, unlit: what each thing is made of, at a glance.</summary>
    FlatColours,

    /// <summary>Everything see-through, so what is behind and inside can be read.</summary>
    XRay,

    /// <summary>Only the edges, every one of them - those behind as well as those in front.</summary>
    Wireframe,

    /// <summary>White faces and black edges, those behind hidden: the building as a line drawing.</summary>
    HiddenLine
}

/// <summary>
/// Where a 3D view looks from and to, in millimetres, and whether it is drawn in perspective -
/// as the eye sees - or orthographic, every line its true direction, as an axonometric is drawn.
/// </summary>
public sealed record Camera3D(Point3D Eye, Point3D Target, bool Perspective = true)
{
    /// <summary>How wide the eye sees, degrees across the view.</summary>
    public double FieldOfView { get; init; } = 45;

    /// <summary>How far the eye is from what it looks at.</summary>
    public double Distance => Length(Minus(Target, Eye));

    /// <summary>The way it looks, a unit vector.</summary>
    public Point3D Look => Unit(Minus(Target, Eye), new Point3D(0, 1, 0));

    /// <summary>Across the view, left to right: level, square to the look.</summary>
    public Point3D Right
    {
        get
        {
            var look = Look;
            var right = new Point3D(look.Y, -look.X, 0);
            return Length(right) < 1e-9 ? new Point3D(1, 0, 0) : Unit(right, new Point3D(1, 0, 0));
        }
    }

    /// <summary>Up the view, square to the look and across.</summary>
    public Point3D Up => Cross(Right, Look);

    /// <summary>
    /// How wide the view is where it looks, mm: what an orthographic view shows across, and the
    /// width of the picture plane through the target that a perspective is drawn on.
    /// </summary>
    public double Width => 2 * Math.Max(Distance, 1) * Math.Tan(FieldOfView / 2 * Math.PI / 180);

    /// <summary>
    /// Where a point of the model is on the view, mm across and up from its middle on the
    /// picture plane through the target, and how far in front of the eye it is. A point behind
    /// the eye of a perspective has a depth of zero or less.
    /// </summary>
    public (double X, double Y, double Depth) Project(Point3D point)
    {
        var d = Minus(point, Eye);
        var depth = Dot(d, Look);
        var (x, y) = (Dot(d, Right), Dot(d, Up));
        if (!Perspective) return (x, y, depth);
        if (depth <= 1e-6) return (0, 0, depth);

        var scale = Distance / depth;
        return (x * scale, y * scale, depth);
    }

    /// <summary>Looking the other way round, from the eye: the yaw (round from east) and pitch (up from level) of the line from the target back to the eye, degrees.</summary>
    public (double Yaw, double Pitch) Orbit()
    {
        var back = Minus(Eye, Target);
        var level = Math.Sqrt(back.X * back.X + back.Y * back.Y);
        return (Math.Atan2(back.Y, back.X) * 180 / Math.PI, Math.Atan2(back.Z, level) * 180 / Math.PI);
    }

    /// <summary>The camera round a target at a distance, from a yaw and pitch as <see cref="Orbit"/> gives them.</summary>
    public static Camera3D FromOrbit(Point3D target, double yaw, double pitch, double distance, bool perspective, double fieldOfView = 45)
    {
        var (y, p) = (yaw * Math.PI / 180, pitch * Math.PI / 180);
        var eye = new Point3D(
            target.X + Math.Cos(p) * Math.Cos(y) * distance,
            target.Y + Math.Cos(p) * Math.Sin(y) * distance,
            target.Z + Math.Sin(p) * distance);
        return new Camera3D(eye, target, perspective) { FieldOfView = fieldOfView };
    }

    internal static Point3D Minus(Point3D a, Point3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    internal static double Dot(Point3D a, Point3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    internal static double Length(Point3D a) => Math.Sqrt(Dot(a, a));

    internal static Point3D Cross(Point3D a, Point3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    internal static Point3D Unit(Point3D a, Point3D fallback)
    {
        var length = Length(a);
        return length < 1e-12 ? fallback : new Point3D(a.X / length, a.Y / length, a.Z / length);
    }
}

/// <summary>
/// A 3D view kept by name (specification section 6.1): the default {3D} view, or one made from a
/// camera. It remembers where it looks from, how it is drawn, the section box round the part of
/// the model it shows, and what it leaves out - so the view placed on a sheet is the view as it
/// was left, not whatever the 3D window happens to be showing.
/// </summary>
public sealed class View3D : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Views;

    public string Name { get; set; } = "{3D}";

    /// <summary>Where it looks from and to; null until it is first looked through, when it is fitted round the model.</summary>
    public Camera3D? Camera { get; set; }

    /// <summary>A camera's view: in perspective, made with the Camera tool. The {3D} view and copies of it are orthographic unless set otherwise.</summary>
    public bool Perspective { get; set; } = true;

    public VisualStyle Style { get; set; } = VisualStyle.Shaded;

    /// <summary>The box round the part of the model shown, when the section box is on.</summary>
    public Box3D? SectionBox { get; set; }

    /// <summary>Whether the section box crops the view. Turned off, its box is kept for when it is turned on again.</summary>
    public bool SectionBoxOn { get; set; }

    /// <summary>The storeys taken off, to see into the one below.</summary>
    public HashSet<Guid> HiddenLevels { get; } = new();

    /// <summary>The kinds of thing left out: the roofs lifted off, the ceilings taken down.</summary>
    public HashSet<MeshKind> HiddenKinds { get; } = new();

    public bool ShowGround { get; set; } = true;

    public bool ShowEdges { get; set; } = true;

    /// <summary>The section box when it is on: what the view is cropped to.</summary>
    public Box3D? ActiveBox => SectionBoxOn ? SectionBox : null;

    /// <summary>Whether a mesh is drawn in this view: its storey shown and its kind not left out.</summary>
    public bool Shows(Mesh3D mesh) =>
        !HiddenLevels.Contains(mesh.LevelId) &&
        !HiddenKinds.Contains(mesh.Kind is MeshKind.Glazing or MeshKind.DoorLeaf or MeshKind.Mullion ? MeshKind.Wall : mesh.Kind);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(View3DParameters.ViewName, () => Name, value => { if (!string.IsNullOrWhiteSpace(value)) Name = value.Trim(); });
        yield return ParameterValue.BindChoice(View3DParameters.Style, () => EnumText.Humanise(Style),
            value => { if (EnumText.TryParse<VisualStyle>(value, out var style)) Style = style; }, EnumText.Choices<VisualStyle>());
        yield return ParameterValue.Bind(View3DParameters.Perspective, () => Perspective, value => Perspective = value);
        yield return ParameterValue.Bind(View3DParameters.SectionBox, () => SectionBoxOn, value => SectionBoxOn = value);
        if (Camera is { } camera)
        {
            var level = document.Levels.OrderBy(l => l.Elevation).LastOrDefault(l => l.Elevation <= camera.Eye.Z + 1) ?? document.Levels.FirstOrDefault();
            var floor = level?.Elevation ?? 0;
            yield return ParameterValue.Bind(View3DParameters.EyeElevation, () => camera.Eye.Z - floor,
                value => { if (double.IsFinite(value) && Camera is { } now) Camera = now with { Eye = now.Eye with { Z = floor + value } }; });
            yield return ParameterValue.Bind(View3DParameters.TargetElevation, () => camera.Target.Z - floor,
                value => { if (double.IsFinite(value) && Camera is { } now) Camera = now with { Target = now.Target with { Z = floor + value } }; });
            yield return ParameterValue.BindValidated(View3DParameters.FieldOfView, () => camera.FieldOfView,
                value =>
                {
                    if (!double.IsFinite(value) || value < 10 || value > 120 || Camera is not { } now) return false;
                    Camera = now with { FieldOfView = value };
                    return true;
                });
        }

        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

public static class View3DParameters
{
    public static readonly ParameterDefinition ViewName = new("View Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Style = new("Visual Style", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Perspective = new("Perspective", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition SectionBox = new("Section Box", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Extents);
    public static readonly ParameterDefinition EyeElevation = new("Eye Elevation", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Camera);
    public static readonly ParameterDefinition TargetElevation = new("Target Elevation", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Camera);
    public static readonly ParameterDefinition FieldOfView = new("Field of View", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Camera);
}

/// <summary>The project's 3D views: the {3D} view it always has, and those made from cameras or duplicated.</summary>
public static class Views3D
{
    /// <summary>How high a camera's eye is put above the floor it is placed on, mm: a standing adult's eye.</summary>
    public const double EyeHeight = 1750;

    /// <summary>The 3D view a reference is to: the {3D} view for the plain 3D reference, or the named view.</summary>
    public static View3D? Find(BimDocument document, ViewReference reference)
    {
        if (reference.Kind != ViewKind.Model3D) return null;
        var id = reference.TargetId;
        return id == Guid.Empty ? document.Default3D : document.All<View3D>().FirstOrDefault(view => view.Id == id);
    }

    /// <summary>The reference to a 3D view: the plain 3D reference for the {3D} view.</summary>
    public static ViewReference Reference(BimDocument document, View3D view) =>
        ReferenceEquals(view, document.Default3D) ? ViewReference.Model3D : ViewReference.View3D(view.Id);

    /// <summary>Every 3D view, the {3D} view first.</summary>
    public static IEnumerable<View3D> All(BimDocument document) =>
        new[] { document.Default3D }.Concat(document.All<View3D>().OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>The next free name of a kind: "3D View 1", "3D View 2" ...</summary>
    public static string NextName(BimDocument document, string stem = "3D View")
    {
        var names = All(document).Select(view => view.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
            if (!names.Contains($"{stem} {i}")) return $"{stem} {i}";
    }

    /// <summary>
    /// A camera's view, from an eye clicked in plan toward a target, both at a standing eye's
    /// height above the storey: in perspective, named after the next free number.
    /// </summary>
    public static View3D Camera(BimDocument document, Guid levelId, Point2D eye, Point2D target)
    {
        var floor = document.FindLevel(levelId)?.Elevation ?? 0;
        if (eye.DistanceTo(target) < 1) target = eye + new Vector2D(0, 1000);

        return new View3D
        {
            Name = NextName(document),
            Perspective = true,
            Camera = new Camera3D(Point3D.On(eye, floor + EyeHeight), Point3D.On(target, floor + EyeHeight)) { FieldOfView = 50 }
        };
    }

    /// <summary>A copy of a view, kept by its own name: Revit's Duplicate View.</summary>
    public static View3D Duplicate(BimDocument document, View3D view)
    {
        var copy = new View3D
        {
            Name = NextName(document, view.Name.Trim('{', '}') == "3D" ? "3D View" : view.Name),
            Camera = view.Camera, Perspective = view.Perspective, Style = view.Style,
            SectionBox = view.SectionBox, SectionBoxOn = view.SectionBoxOn,
            ShowGround = view.ShowGround, ShowEdges = view.ShowEdges
        };
        copy.HiddenLevels.UnionWith(view.HiddenLevels);
        copy.HiddenKinds.UnionWith(view.HiddenKinds);
        return copy;
    }
}

/// <summary>
/// A section box: the part of the model inside it shown, the rest cut away, and the solids it
/// cuts closed again on its faces in a darker shade of themselves - so a cut through a building
/// reads as solid walls and floors, not as hollow shells.
/// </summary>
public static class SectionBoxes
{
    /// <summary>How far a new section box stands off everything in the model, mm.</summary>
    public const double Margin = 300;

    /// <summary>The smallest a section box is let be, mm each way.</summary>
    public const double Smallest = 100;

    /// <summary>The box round the meshes, with room to spare: where a section box starts.</summary>
    public static Box3D? Around(IEnumerable<Mesh3D> meshes)
    {
        if (ModelMeshBuilder.Bounds(meshes) is not { } bounds) return null;
        return new Box3D(bounds.Min, bounds.Max).Expand(Margin);
    }

    /// <summary>The meshes inside the box, cut where they leave it, and closed where they are cut.</summary>
    public static IReadOnlyList<Mesh3D> Clip(IReadOnlyList<Mesh3D> meshes, Box3D box, Func<int, bool>? closes = null)
    {
        var kept = new List<Mesh3D>();
        foreach (var mesh in meshes)
        {
            if (mesh.ClippedTo(box) is not { } inside) continue;
            kept.Add(inside);

            // A solid cut open: its cut faces, where the box's faces pass through it. Glass is left open, as it is drawn without a cut.
            if (!ReferenceEquals(inside, mesh) && mesh.Kind != MeshKind.Glazing && mesh.Opacity >= 1 && Caps(mesh, box, closes) is { } caps)
                kept.Add(caps);
        }

        return kept;
    }

    /// <summary>
    /// Where each face of the box slices a solid, inside the face: closed outlines, made into
    /// faces looking out of the box, outlined.
    /// </summary>
    public static Mesh3D? Caps(Mesh3D mesh, Box3D box, Func<int, bool>? closes = null)
    {
        if (mesh.Bounds() is not { } bounds) return null;

        var colour = new Materials.ColourRgb((byte)(mesh.Colour.R * 0.55), (byte)(mesh.Colour.G * 0.55), (byte)(mesh.Colour.B * 0.55));
        var caps = new Mesh3D(mesh.ElementId, mesh.LevelId, mesh.Kind, colour, mesh.Description) { OwnerId = mesh.OwnerId, Cut = true };

        for (var face = 0; face < 6; face++)
        {
            var axis = face / 2;
            var high = face % 2 == 1;
            if (closes is not null && !closes(face)) continue;
            var at = Box3D.Along(high ? box.Max : box.Min, axis);
            if (Box3D.Along(bounds.Min, axis) >= at || Box3D.Along(bounds.Max, axis) <= at) continue;

            // The face's own two directions, and its rectangle in them.
            var (u, v) = ((axis + 1) % 3, (axis + 2) % 3);
            Point2D OnFace(Point3D p) => new(Box3D.Along(p, u), Box3D.Along(p, v));
            Point3D Back(Point2D p)
            {
                var point = Box3D.With(new Point3D(0, 0, 0), axis, at);
                point = Box3D.With(point, u, p.X);
                return Box3D.With(point, v, p.Y);
            }

            var rectangle = new[]
            {
                new Point2D(Box3D.Along(box.Min, u), Box3D.Along(box.Min, v)), new Point2D(Box3D.Along(box.Max, u), Box3D.Along(box.Min, v)),
                new Point2D(Box3D.Along(box.Max, u), Box3D.Along(box.Max, v)), new Point2D(Box3D.Along(box.Min, u), Box3D.Along(box.Max, v))
            };

            var regions = MeshSections.Slice(mesh, p => Box3D.Along(p, axis) - at, OnFace);
            var outward = Box3D.With(box.Centre, axis, at + (high ? 1e6 : -1e6));
            foreach (var region in regions)
            {
                var shape = new List<IReadOnlyList<Point2D>> { region.Outer };
                shape.AddRange(region.Holes);
                foreach (var piece in PolygonBoolean.Combine(shape, new[] { (IReadOnlyList<Point2D>)rectangle }, BooleanOperation.Intersection))
                {
                    foreach (var loop in new[] { piece.Outer }.Concat(piece.Holes))
                        for (var i = 0; i < loop.Count; i++)
                            caps.AddEdge(Back(loop[i]), Back(loop[(i + 1) % loop.Count]));

                    foreach (var outline in piece.Holes.Count == 0 ? new[] { piece.Outer } : PolygonBoolean.WithoutHoles(piece))
                        caps.AddFace(outline.Select(Back).ToList(), Polygon2D.Triangulate(outline), outward);
                }
            }
        }

        return caps.IsEmpty ? null : caps;
    }
}

/// <summary>Everything a 3D view is, kept whole: what a change to it is put back to.</summary>
public sealed class View3DState
{
    private readonly string _name;
    private readonly Camera3D? _camera;
    private readonly bool _perspective;
    private readonly VisualStyle _style;
    private readonly Box3D? _sectionBox;
    private readonly bool _sectionBoxOn;
    private readonly HashSet<Guid> _hiddenLevels;
    private readonly HashSet<MeshKind> _hiddenKinds;
    private readonly bool _showGround;
    private readonly bool _showEdges;

    private View3DState(View3D view)
    {
        (_name, _camera, _perspective, _style, _sectionBox, _sectionBoxOn, _showGround, _showEdges) =
            (view.Name, view.Camera, view.Perspective, view.Style, view.SectionBox, view.SectionBoxOn, view.ShowGround, view.ShowEdges);
        _hiddenLevels = view.HiddenLevels.ToHashSet();
        _hiddenKinds = view.HiddenKinds.ToHashSet();
    }

    public static View3DState Of(View3D view) => new(view);

    /// <summary>Puts the view back as it was - its camera only when asked, as the camera moves with every look round and is not undone with a change of style.</summary>
    public void Apply(View3D view, bool camera = true)
    {
        if (camera) view.Camera = _camera;
        (view.Name, view.Perspective, view.Style, view.SectionBox, view.SectionBoxOn, view.ShowGround, view.ShowEdges) =
            (_name, _perspective, _style, _sectionBox, _sectionBoxOn, _showGround, _showEdges);
        view.HiddenLevels.Clear();
        view.HiddenLevels.UnionWith(_hiddenLevels);
        view.HiddenKinds.Clear();
        view.HiddenKinds.UnionWith(_hiddenKinds);
    }

    /// <summary>Whether two states are the same view, camera and all.</summary>
    public bool SameAs(View3DState other) =>
        _name == other._name && _perspective == other._perspective && _style == other._style && _sectionBox == other._sectionBox &&
        _sectionBoxOn == other._sectionBoxOn && _showGround == other._showGround && _showEdges == other._showEdges &&
        _hiddenLevels.SetEquals(other._hiddenLevels) && _hiddenKinds.SetEquals(other._hiddenKinds) && SameCamera(other);

    public bool SameCamera(View3DState other) => _camera == other._camera;
}

/// <summary>
/// Changes a 3D view - its section box, style, what it shows, its camera - as one step to undo.
/// </summary>
public sealed class SetView3DCommand : IUndoableCommand
{
    private readonly View3D _view;
    private readonly View3DState _before;
    private readonly View3DState _after;

    /// <summary>The change is tried on the view to see what it comes to, then taken back until the command is run.</summary>
    public SetView3DCommand(View3D view, Action<View3D> change, string name)
    {
        (_view, Name) = (view, name);
        _before = View3DState.Of(view);
        change(view);
        _after = View3DState.Of(view);
        _before.Apply(view);
        _camera = !_before.SameCamera(_after);
    }

    /// <summary>Whether the change moves the camera: only then is the camera put back with it.</summary>
    private readonly bool _camera;

    /// <summary>A change already made to the view as it went - a section box dragged - from the state it was in before.</summary>
    public SetView3DCommand(View3D view, View3DState before, string name)
    {
        (_view, Name) = (view, name);
        _before = before;
        _after = View3DState.Of(view);
        _camera = !_before.SameCamera(_after);
    }

    public string Name { get; }

    /// <summary>Whether it changes anything at all.</summary>
    public bool Changes => !_before.SameAs(_after);

    public void Redo() => _after.Apply(_view, _camera);

    public void Undo() => _before.Apply(_view, _camera);
}
