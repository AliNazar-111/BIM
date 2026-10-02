using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BIMDesigner.Core;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Views;

using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;
using MediaPoint3D = System.Windows.Media.Media3D.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The 3D view as an elevation: the model straight on from one side with no perspective - an
/// orthographic camera - with the levels drawn across it and the gridlines that face it, as an
/// elevation is drawn. It pans and zooms but does not turn.
/// </summary>
public partial class ModelView
{
    /// <summary>How far back from what it looks at an elevation's camera stands, in metres: behind anything in the model.</summary>
    private const double ElevationBack = 2000;

    private readonly OrthographicCamera _ortho = new() { UpDirection = new Vector3D(0, 0, 1) };
    private readonly ElevationOverlay _overlay;
    private ElevationSide? _elevation;
    private CorePoint3D? _pendingCorner;

    /// <summary>The first corner of a part being split off a face: the part is drawn from it to the cursor.</summary>
    public CorePoint3D? PendingCorner
    {
        get => _pendingCorner;
        set
        {
            _pendingCorner = value;
            _overlay.InvalidateVisual();
        }
    }

    /// <summary>Which elevation the view shows, or null for the 3D view that orbits.</summary>
    public ElevationSide? Elevation
    {
        get => _elevation;
        set
        {
            if (_elevation == value) return;

            _elevation = value;
            _viewport.Camera = value is null ? _camera : _ortho;
            if (value is { } side)
            {
                var outward = Elevations.Outward(side);
                _yaw = Math.Atan2(outward.Y, outward.X) * 180 / Math.PI;
                _pitch = 0;
            }

            ZoomToFit();
            _overlay.InvalidateVisual();
        }
    }

    /// <summary>How wide the elevation shows, in metres: as wide as the perspective view sees at the same distance.</summary>
    private double OrthoWidth => 2 * _distance * Math.Tan(FieldOfView / 2 * Math.PI / 180);

    private void UpdateElevationCamera()
    {
        if (_elevation is null) return;

        var look = _camera.LookDirection;
        look.Normalize();
        _ortho.Position = _target - look * ElevationBack;
        _ortho.LookDirection = look;
        _ortho.Width = OrthoWidth;
        _ortho.NearPlaneDistance = 1;
        _ortho.FarPlaneDistance = ElevationBack * 2 + 1000;
        _overlay.InvalidateVisual();
    }

    /// <summary>Frames the whole model, seen from this side.</summary>
    private void ElevationZoomToFit()
    {
        var visible = _meshes.Where(mesh => !_hiddenLevels.Contains(mesh.LevelId));
        if (ModelMeshBuilder.Bounds(visible) is not { } bounds) return;

        _target = new MediaPoint3D(
            (bounds.Min.X + bounds.Max.X) / 2 / MillimetresPerUnit,
            (bounds.Min.Y + bounds.Max.Y) / 2 / MillimetresPerUnit,
            (bounds.Min.Z + bounds.Max.Z) / 2 / MillimetresPerUnit);

        var outward = Elevations.Outward(_elevation!.Value);
        var across = Math.Abs(outward.X) > 0.5 ? bounds.Max.Y - bounds.Min.Y : bounds.Max.X - bounds.Min.X;
        var height = bounds.Max.Z - bounds.Min.Z;
        var aspect = _viewport.ActualHeight > 0 ? _viewport.ActualWidth / _viewport.ActualHeight : 1.6;

        // Room either side for the level heads.
        var width = Math.Max(across * 1.25, height * aspect * 1.2) / MillimetresPerUnit;
        _distance = Math.Max(width / (2 * Math.Tan(FieldOfView / 2 * Math.PI / 180)), 0.5);
        UpdateCamera();
    }

    /// <summary>Where a point of the model is on the elevation, in the view's pixels. Null in the 3D view.</summary>
    public Point? ToScreen(CorePoint3D point)
    {
        if (_elevation is null) return null;

        var width = Math.Max(_viewport.ActualWidth, 1);
        var height = Math.Max(_viewport.ActualHeight, 1);
        var (right, _) = ElevationAxes();
        var perUnit = width / OrthoWidth;
        var d = new Vector3D(point.X / MillimetresPerUnit - _target.X, point.Y / MillimetresPerUnit - _target.Y, point.Z / MillimetresPerUnit - _target.Z);

        return new Point(width / 2 + Vector3D.DotProduct(d, right) * perUnit, height / 2 - d.Z * perUnit);
    }

    /// <summary>The way across the elevation, left to right, and the way it looks.</summary>
    private (Vector3D Right, Vector3D Look) ElevationAxes()
    {
        var look = _camera.LookDirection;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1));
        right.Normalize();
        return (right, look);
    }

    /// <summary>A line of sight through a point of an elevation: square to it, from in front of everything.</summary>
    private (CorePoint3D From, CorePoint3D Through) ElevationRay(Point point)
    {
        var width = Math.Max(_viewport.ActualWidth, 1);
        var height = Math.Max(_viewport.ActualHeight, 1);
        var (right, look) = ElevationAxes();
        var perUnit = width / OrthoWidth;

        var onPlane = _target + right * ((point.X - width / 2) / perUnit) + new Vector3D(0, 0, (height / 2 - point.Y) / perUnit);
        var from = onPlane - look * ElevationBack;
        var through = from + look;

        return (
            new CorePoint3D(from.X * MillimetresPerUnit, from.Y * MillimetresPerUnit, from.Z * MillimetresPerUnit),
            new CorePoint3D(through.X * MillimetresPerUnit, through.Y * MillimetresPerUnit, through.Z * MillimetresPerUnit));
    }

    /// <summary>
    /// The levels across an elevation, each with its head and name and height at the right; and
    /// the gridlines that face it, upright, with their bubbles at the top.
    /// </summary>
    private sealed class ElevationOverlay : FrameworkElement
    {
        private readonly ModelView _view;
        private static readonly Pen LevelPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x5B, 0x8F, 0xD9))), 1)
            { DashStyle = new DashStyle(new double[] { 12, 3, 2, 3 }, 0) });
        private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2))), 1)
            { DashStyle = new DashStyle(new double[] { 14, 4, 3, 4 }, 0) });
        private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5B, 0x8F, 0xD9)));
        private static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC4, 0xCC, 0xD6)));
        private static readonly Brush HeadFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x21, 0x28)));
        private static readonly Pen PartPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0x2E, 0xC4))), 1.6)
            { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) });
        private static readonly Brush PartFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xD6, 0x2E, 0xC4)));

        public ElevationOverlay(ModelView view)
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
            if (_view._elevation is null || _view._document is not { } document) return;

            // The part being split off a face, from its first corner to the cursor.
            if (_view._pendingCorner is { } corner && _view.ToScreen(corner) is { } from)
            {
                var to = System.Windows.Input.Mouse.GetPosition(this);
                dc.DrawRectangle(PartFill, PartPen, new Rect(from, to));
            }

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText Text(string text, Brush brush, double size = 11) =>
                new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);

            // The gridlines facing the elevation: their planes run toward the eye.
            var (right, look) = _view.ElevationAxes();
            foreach (var grid in document.Elements.OfType<Grid>())
            {
                var direction = (grid.End - grid.Start).NormalisedOrDefault(Core.Geometry.Vector2D.UnitX);
                if (Math.Abs(direction.X * look.X + direction.Y * look.Y) < 0.94) continue;
                if (_view.ToScreen(new CorePoint3D(grid.Start.X, grid.Start.Y, 0)) is not { } at) continue;

                dc.DrawLine(GridPen, new Point(at.X, 44), new Point(at.X, ActualHeight - 8));
                dc.DrawEllipse(HeadFill, GridPen, new Point(at.X, 30), 13, 13);
                var name = Text(grid.Name, GridBrush, 12);
                dc.DrawText(name, new Point(at.X - name.Width / 2, 30 - name.Height / 2));
            }

            foreach (var level in document.Levels)
            {
                var middle = new CorePoint3D(_view._target.X * MillimetresPerUnit, _view._target.Y * MillimetresPerUnit, level.Elevation);
                if (_view.ToScreen(middle) is not { Y: var y } || y < -20 || y > ActualHeight + 20) continue;

                var label = Text($"{level.Name}   {Units.FormatLength(level.Elevation)}", LabelBrush);
                var headX = ActualWidth - label.Width - 30;
                dc.DrawLine(LevelPen, new Point(8, y), new Point(headX - 8, y));

                // The level head: a circle quartered, as on a drawing.
                var head = new Point(headX - 2, y);
                dc.DrawEllipse(HeadFill, LevelPen, head, 6, 6);
                dc.DrawText(label, new Point(headX + 8, y - label.Height - 1));
            }
        }
    }
}
