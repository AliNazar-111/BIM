using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;
using Mesh3D = BIMDesigner.Core.Geometry.Mesh3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A handle on something selected, in the 3D view: what it is, the element it is on, where it
/// is in the model, whether it is dragged up and down rather than across, and the mark drawn on it.
/// </summary>
public readonly record struct ModelGrip(string Key, Guid Element, CorePoint3D At, bool Vertical, string Glyph);

/// <summary>A handle being dragged: which, and the line of sight through the cursor - the eye and a point along it, mm.</summary>
public readonly record struct ModelGripDrag(string Key, CorePoint3D From, CorePoint3D Through);

/// <summary>
/// Handles in the 3D view, drawn over the model where they are and picked up with the mouse
/// before anything under them - so a stair is reshaped in 3D as it is in plan.
/// </summary>
public partial class ModelView
{
    private const double GripReach = 10;

    private GripOverlay? _gripOverlay;
    private string? _grip;
    private IReadOnlyList<ModelGrip>? _gripCache;

    // An element being dragged, shown as it is now in place of what the model was built with.
    private Guid? _previewId;
    private IReadOnlyList<Mesh3D> _previewMeshes = Array.Empty<Mesh3D>();
    private readonly Model3DGroup _previewGroup = new();
    private Model3DGroup? _solidGroup;
    private Model3DGroup? _clearGroup;
    private GeometryModel3D? _selectedEdges;

    /// <summary>The handles to show, for whatever is selected; none when null.</summary>
    public Func<IReadOnlyList<ModelGrip>>? Grips { get; set; }

    public event EventHandler<ModelGripDrag>? GripDragged;
    public event EventHandler<string>? GripDragEnded;
    public event EventHandler<string>? GripClicked;

    /// <summary>Asks for the handles again, and draws them: the selection or the model changed.</summary>
    public void RefreshGrips()
    {
        _gripCache = null;
        _gripOverlay?.InvalidateVisual();
    }

    /// <summary>The handles shown: asked for once each time something changes, not each time the view turns - and only on what is shown.</summary>
    private IReadOnlyList<ModelGrip> CurrentGrips() =>
        _gripCache ??= (Grips?.Invoke() ?? Array.Empty<ModelGrip>()).Where(grip => IsShown(grip.Element)).ToList();

    private bool IsShown(Guid element) =>
        (element == _previewId ? _previewMeshes : _meshes.Where(mesh => mesh.ElementId == element))
        .Any(mesh => !_hiddenLevels.Contains(mesh.LevelId) && !IsHiddenKind(mesh.Kind));

    /// <summary>
    /// Shows one element as it is now, in place of what the model was last built with, leaving
    /// everything else as it is: what a drag shows as it goes, where building the whole model
    /// again on every move of the mouse would be too slow to follow it. Its faces and its edges
    /// are all that are made again. The next rebuild puts the model back as the one source.
    /// </summary>
    public void PreviewElement(Guid id, IReadOnlyList<Mesh3D> meshes)
    {
        if (_previewId != id)
        {
            // Its own faces lifted out of the scene, the stand-in to follow the drag.
            _previewId = id;
            foreach (var model in _meshOf.Where(entry => entry.Value.ElementId == id).Select(entry => entry.Key).ToList())
            {
                _solidGroup?.Children.Remove(model);
                _clearGroup?.Children.Remove(model);
                _meshOf.Remove(model);
            }
        }

        foreach (var model in _previewGroup.Children.OfType<GeometryModel3D>()) _meshOf.Remove(model);
        _previewMeshes = meshes;
        ShowPreview();

        // Only the selection's edges are drawn again: everything else's are as they were.
        if (_showEdges)
        {
            if (_selectedEdges is not null) _edgeGroup.Children.Remove(_selectedEdges);
            _selectedEdges = AddEdgeModel(SelectedEdges(), Color.FromRgb(0x4A, 0x9B, 0xFF));
        }

        RefreshGrips();
    }

    /// <summary>The element a drag is showing, made into faces in its own group.</summary>
    private void ShowPreview()
    {
        _previewGroup.Children.Clear();
        foreach (var mesh in _previewMeshes)
        {
            if (_hiddenLevels.Contains(mesh.LevelId) || IsHiddenKind(mesh.Kind)) continue;
            var material = MaterialFor(mesh);
            var model = new GeometryModel3D(ToGeometry(mesh), material) { BackMaterial = material };
            _previewGroup.Children.Add(model);
            _meshOf[model] = mesh;
        }
    }

    /// <summary>The handle under a point of the view, nearest first; none where there is none.</summary>
    private ModelGrip? GripNear(Point point)
    {
        ModelGrip? best = null;
        var nearest = GripReach;
        foreach (var grip in CurrentGrips())
        {
            if (ScreenOf(grip.At) is not { } at) continue;
            var off = (at - point).Length;
            if (off > nearest) continue;
            (best, nearest) = (grip, off);
        }

        return best;
    }

    /// <summary>Where a point of the model is in the view's pixels - in perspective or on an elevation - or null behind the eye.</summary>
    public Point? ScreenOf(CorePoint3D point)
    {
        if (_elevation is not null) return ToScreen(point);

        var width = Math.Max(_viewport.ActualWidth, 1);
        var height = Math.Max(_viewport.ActualHeight, 1);

        var look = _camera.LookDirection;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, _camera.UpDirection);
        if (right.Length < 1e-6) right = new Vector3D(1, 0, 0);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        up.Normalize();

        var d = new System.Windows.Media.Media3D.Point3D(point.X / MillimetresPerUnit, point.Y / MillimetresPerUnit, point.Z / MillimetresPerUnit) - _camera.Position;
        var depth = Vector3D.DotProduct(d, look);
        if (depth <= 1e-6) return null;

        // As the line of sight through a pixel is worked out, backwards.
        var half = Math.Tan(_camera.FieldOfView / 2 * Math.PI / 180);
        var across = Vector3D.DotProduct(d, right) / depth;
        var upward = Vector3D.DotProduct(d, up) / depth;
        return new Point((across / half + 1) * width / 2, (1 - upward / (half * height / width)) * height / 2);
    }

    private sealed class GripOverlay : FrameworkElement
    {
        private readonly ModelView _view;
        private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xD1)));
        private static readonly Pen Ring = Frozen(new Pen(Ink, 1.6));
        private static readonly Pen Stem = Frozen(new Pen(Ink, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) });

        public GripOverlay(ModelView view)
        {
            _view = view;
            IsHitTestVisible = false;
        }

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var grip in _view.CurrentGrips())
            {
                if (_view.ScreenOf(grip.At) is not { } at) continue;

                // Up and down handles stand on a short stem, so it reads which way they go.
                if (grip.Vertical && _view.ScreenOf(grip.At with { Z = grip.At.Z - 400 }) is { } below)
                    dc.DrawLine(Stem, at, below);

                dc.DrawEllipse(Fill, Ring, at, 8, 8);
                var glyph = new FormattedText(grip.Glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Symbol"), 10, Ink, dpi);
                dc.DrawText(glyph, new Point(at.X - glyph.Width / 2, at.Y - glyph.Height / 2));
            }
        }
    }
}
