using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The cube in the corner of the 3D view that shows which way the model is being looked at,
/// and turns it: a face for a straight elevation or plan, an edge for halfway between two, a
/// corner for a three-quarter view. Dragging it orbits the model; the house above it goes back
/// to the standard view. As Revit's, FRONT is the south face and TOP looks straight down with
/// north up.
///
/// It is drawn in two dimensions from the camera's own direction, so it cannot fall out of step
/// with the model: there is no second 3D scene to keep turning alongside the first.
/// </summary>
public sealed class ViewCube : FrameworkElement
{
    /// <summary>Half the cube's size on screen, in pixels.</summary>
    private const double Half = 30;

    /// <summary>The share of a face, from each edge, that picks the edge or corner rather than the face.</summary>
    private const double Rim = 1.0 / 3;

    /// <summary>Text is laid out on each face at this many units a side, then scaled onto it.</summary>
    private const double TextSpace = 100;

    private const double ClickSlop = 3;

    private static readonly (string Label, Vector3D Normal, Vector3D Across, Vector3D Up)[] Faces =
    {
        ("TOP", new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)),
        ("BOTTOM", new(0, 0, -1), new(1, 0, 0), new(0, -1, 0)),
        ("FRONT", new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),
        ("BACK", new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1)),
        ("RIGHT", new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)),
        ("LEFT", new(-1, 0, 0), new(0, -1, 0), new(0, 0, 1))
    };

    private static readonly Brush FaceBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xEE)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9C, 0xC9, 0xF2)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3C, 0x41, 0x48)));
    private static readonly Pen EdgePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x6E, 0x74, 0x7C)), 1));
    private static readonly Pen RingPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x8F, 0x96, 0x9E)), 5));
    private static readonly Brush HomeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8F, 0x96, 0x9E)));

    private ModelView? _model;
    private (int Face, int S, int T)? _hover;
    private Point? _pressedAt;
    private Point _last;
    private bool _dragged;

    public ViewCube()
    {
        Width = 130;
        Height = 130;
        Cursor = Cursors.Hand;
        ToolTip = "Click a face, edge or corner to look from there. Drag to orbit. The house goes back to the standard view.";
    }

    /// <summary>The 3D view this cube turns and follows.</summary>
    public ModelView? Model
    {
        get => _model;
        set
        {
            if (_model is not null) _model.CameraChanged -= OnCameraChanged;
            _model = value;
            if (_model is not null) _model.CameraChanged += OnCameraChanged;
            InvalidateVisual();
        }
    }

    private void OnCameraChanged(object? sender, EventArgs e) => InvalidateVisual();

    private Point Centre => new(ActualWidth / 2, ActualHeight / 2 + 8);

    private Rect HomeButton => new(6, 4, 18, 16);

    // ---- the camera's frame -----------------------------------------------------------

    /// <summary>Toward the camera, the screen's right and the screen's up, in the model's axes.</summary>
    private (Vector3D Back, Vector3D Right, Vector3D Up) Frame()
    {
        var yaw = (_model?.Yaw ?? -135) * Math.PI / 180;
        var pitch = (_model?.Pitch ?? 28) * Math.PI / 180;

        var back = new Vector3D(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
        var right = Vector3D.CrossProduct(-back, new Vector3D(0, 0, 1));
        right.Normalize();
        var up = Vector3D.CrossProduct(right, -back);
        return (back, right, up);
    }

    /// <summary>A direction in the model as a direction on screen, y down, a unit of the cube being <see cref="Half"/> pixels.</summary>
    private static Vector Project(Vector3D v, Vector3D right, Vector3D up) =>
        new(Vector3D.DotProduct(v, right) * Half, -Vector3D.DotProduct(v, up) * Half);

    /// <summary>
    /// Where a face's own square lands on screen: (s, t) from -1 to 1 across it, t downward,
    /// taken to the screen.
    /// </summary>
    private Matrix FaceMatrix(int face, Vector3D right, Vector3D up)
    {
        var (_, normal, across, faceUp) = Faces[face];
        var a = Project(across, right, up);
        var b = Project(-faceUp, right, up);
        var centre = Centre + Project(normal, right, up);
        return new Matrix(a.X, a.Y, b.X, b.Y, centre.X, centre.Y);
    }

    private IEnumerable<int> VisibleFaces(Vector3D back) =>
        Enumerable.Range(0, Faces.Length).Where(i => Vector3D.DotProduct(Faces[i].Normal, back) > 0.02);

    // ---- drawing ----------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var (back, right, up) = Frame();

        // The compass round the base: north marked, as the cube turns with the camera.
        var ring = new StreamGeometry();
        using (var ctx = ring.Open())
        {
            for (var i = 0; i <= 64; i++)
            {
                var angle = 2 * Math.PI * i / 64;
                var point = Centre + Project(new Vector3D(1.55 * Math.Cos(angle), 1.55 * Math.Sin(angle), -1), right, up);
                if (i == 0) ctx.BeginFigure(point, false, true);
                else ctx.LineTo(point, true, false);
            }
        }

        ring.Freeze();
        dc.DrawGeometry(null, RingPen, ring);

        foreach (var (letter, direction) in new[] { ("N", new Vector3D(0, 1.85, -1)), ("E", new Vector3D(1.85, 0, -1)), ("S", new Vector3D(0, -1.85, -1)), ("W", new Vector3D(-1.85, 0, -1)) })
        {
            var text = Text(letter, 10, HomeBrush);
            var at = Centre + Project(direction, right, up);
            dc.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
        }

        foreach (var face in VisibleFaces(back))
        {
            var matrix = FaceMatrix(face, right, up);
            dc.DrawGeometry(FaceBrush, EdgePen, Square(matrix, -1, 1, -1, 1));

            if (_hover is { } hover && hover.Face == face)
            {
                var (s0, s1) = Band(hover.S);
                var (t0, t1) = Band(hover.T);
                dc.DrawGeometry(HoverBrush, null, Square(matrix, s0, s1, t0, t1));
            }

            // The label lies on the face, turned and foreshortened with it.
            var label = Text(Faces[face].Label, 23, TextBrush);
            var onFace = new Matrix(matrix.M11 / (TextSpace / 2), matrix.M12 / (TextSpace / 2),
                                    matrix.M21 / (TextSpace / 2), matrix.M22 / (TextSpace / 2), matrix.OffsetX, matrix.OffsetY);
            dc.PushTransform(new MatrixTransform(onFace));
            dc.DrawText(label, new Point(-label.Width / 2, -label.Height / 2));
            dc.Pop();
        }

        DrawHome(dc);
    }

    private void DrawHome(DrawingContext dc)
    {
        var box = HomeButton;
        var house = new StreamGeometry();
        using (var ctx = house.Open())
        {
            ctx.BeginFigure(new Point(box.Left + box.Width / 2, box.Top), true, true);
            ctx.PolyLineTo(new[]
            {
                new Point(box.Right, box.Top + box.Height * 0.5), new Point(box.Right - 3, box.Top + box.Height * 0.5),
                new Point(box.Right - 3, box.Bottom), new Point(box.Left + 3, box.Bottom),
                new Point(box.Left + 3, box.Top + box.Height * 0.5), new Point(box.Left, box.Top + box.Height * 0.5)
            }, true, false);
        }

        house.Freeze();
        dc.DrawGeometry(HomeBrush, null, house);
    }

    private static Geometry Square(Matrix matrix, double s0, double s1, double t0, double t1)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(matrix.Transform(new Point(s0, t0)), true, true);
            ctx.PolyLineTo(new[] { matrix.Transform(new Point(s1, t0)), matrix.Transform(new Point(s1, t1)), matrix.Transform(new Point(s0, t1)) }, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The stretch of a face a third of it covers: -1 the near rim, 0 the middle, 1 the far rim.</summary>
    private static (double From, double To) Band(int zone) => zone switch
    {
        < 0 => (-1, -1 + 2 * Rim),
        > 0 => (1 - 2 * Rim, 1),
        _ => (-1 + 2 * Rim, 1 - 2 * Rim)
    };

    private FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---- picking ----------------------------------------------------------------------

    /// <summary>The face under a point, and which ninth of it: the middle, an edge or a corner.</summary>
    private (int Face, int S, int T)? Pick(Point point)
    {
        var (back, right, up) = Frame();

        foreach (var face in VisibleFaces(back))
        {
            var matrix = FaceMatrix(face, right, up);
            if (!matrix.HasInverse) continue;

            matrix.Invert();
            var local = matrix.Transform(point);
            if (Math.Abs(local.X) > 1 || Math.Abs(local.Y) > 1) continue;

            static int Zone(double value) => value < -1 + 2 * Rim ? -1 : value > 1 - 2 * Rim ? 1 : 0;
            return (face, Zone(local.X), Zone(local.Y));
        }

        return null;
    }

    /// <summary>Looks from the face, edge or corner picked: the directions of every face it touches, added.</summary>
    private void LookFrom((int Face, int S, int T) pick)
    {
        if (_model is null) return;

        var (_, normal, across, faceUp) = Faces[pick.Face];
        var direction = normal + across * pick.S - faceUp * pick.T;

        // Straight down or up has no direction round: north stays at the top of the screen.
        var yaw = Math.Abs(direction.X) < 1e-9 && Math.Abs(direction.Y) < 1e-9
            ? -90
            : Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
        var pitch = Math.Asin(direction.Z / direction.Length) * 180 / Math.PI;

        _model.LookFrom(yaw, pitch);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        if (_pressedAt is { } pressed && e.LeftButton == MouseButtonState.Pressed)
        {
            if ((point - pressed).Length > ClickSlop) _dragged = true;
            if (_dragged)
            {
                var delta = point - _last;
                _model?.Orbit(-delta.X * 0.5, delta.Y * 0.5);
            }

            _last = point;
            return;
        }

        var hover = Pick(point);
        if (hover == _hover) return;
        _hover = hover;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressedAt = _last = e.GetPosition(this);
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var point = e.GetPosition(this);
        var wasClick = _pressedAt is not null && !_dragged;
        _pressedAt = null;
        ReleaseMouseCapture();
        e.Handled = true;

        if (!wasClick) return;

        if (HomeButton.Contains(point))
        {
            _model?.ResetView();
            return;
        }

        if (Pick(point) is { } pick) LookFrom(pick);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
