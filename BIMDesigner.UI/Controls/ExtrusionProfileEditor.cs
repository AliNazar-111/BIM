using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The profile of a roof by extrusion, drawn square-on: an open line of points over the span
/// it roofs, above the base it sits on (specification section 3.3; Revit's Edit Profile).
///
/// Points are dragged to where they should be; a double click on a line puts a new point
/// there, a right click on a point takes it out. The diamond in the middle of each line bends
/// it into an arc, dragged out as far as the arc should bow - and back onto the line to
/// straighten it. The width clicked in plan is shaded along the base, so the profile is drawn
/// against the building rather than in empty space; every line shows its pitch and every arc
/// its radius. A line that stands upright or runs back under the one before - the two things a
/// roof cannot do - is drawn in red.
///
/// The view stays where it is while points are moved; it is fitted again only when asked,
/// because a board that rescales under the cursor while something is being dragged is a board
/// nothing can be placed on.
/// </summary>
public sealed class ExtrusionProfileEditor : FrameworkElement
{
    private List<Point2D> _profile = new();
    private List<double> _sagittas = new();
    private int _dragging = -1;
    private int _bending = -1;
    private double _scale = 0.05;
    private Point2D _centre = new(3000, 1500);

    private static readonly Pen LinePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0x2E, 0xC4))), 2.2));
    private static readonly Pen BadPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E))), 3));
    private static readonly Pen PointPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0x2E, 0xC4))), 1.4));
    private static readonly Pen BasePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x9A, 0xA4, 0xB0))), 1.2));
    private static readonly Pen LevelPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0x9A, 0xA4, 0xB0))), 1)
    {
        DashStyle = new DashStyle(new[] { 8.0, 4.0 }, 0)
    });
    private static readonly Pen ChordPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xD6, 0x2E, 0xC4))), 1)
    {
        DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0)
    });
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x9A, 0xA4, 0xB0))), 1));
    private static readonly Brush SpanBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x9A, 0xA4, 0xB0)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC)));
    private static readonly Brush PointBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush BendBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0x2E, 0xC4)));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public ExtrusionProfileEditor()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Where the building the roof spans starts, along the profile's line.</summary>
    public double SpanStart { get; set; }

    /// <summary>The width clicked in plan - the building the roof spans - shaded along the base.</summary>
    public double Span { get; set; } = 6000;

    private IReadOnlyList<(string Name, double Height)> _levels = Array.Empty<(string, double)>();

    /// <summary>Other levels, as heights above the roof's base, drawn as dashed lines to set out against.</summary>
    public IReadOnlyList<(string Name, double Height)> Levels
    {
        get => _levels;
        set
        {
            _levels = value;
            InvalidateVisual();
        }
    }

    /// <summary>The profile, left to right: distance along, height above the base, and how far each line bows.</summary>
    public RoofProfile Profile
    {
        get => new(_profile.ToList(), _sagittas.ToList());
        set
        {
            _profile = value.Points.ToList();
            _sagittas = Enumerable.Range(0, Math.Max(0, _profile.Count - 1))
                .Select(i => i < value.Sagittas.Count ? value.Sagittas[i] : 0)
                .ToList();
            InvalidateVisual();
        }
    }

    public event EventHandler? ProfileChanged;

    /// <summary>A line of the profile as it is drawn: straight, or the arc it bows into.</summary>
    private IReadOnlyList<Point2D> Curve(int segment) =>
        RoofExtrusion.ArcPoints(_profile[segment], _profile[segment + 1], _sagittas[segment]);

    /// <summary>Where the handle that bends a line sits: the middle of the line, or of its arc.</summary>
    private Point2D BendHandle(int segment) =>
        RoofExtrusion.ArcMiddle(_profile[segment], _profile[segment + 1], _sagittas[segment]);

    /// <summary>Frames the profile and the span with a margin all round.</summary>
    public void Fit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var drawn = Enumerable.Range(0, Math.Max(0, _profile.Count - 1)).SelectMany(Curve).Concat(_profile).ToList();
        var xs = drawn.Select(point => point.X).Append(SpanStart).Append(SpanStart + Span).ToList();
        var ys = drawn.Select(point => point.Y).Append(0).Append(Span / 4).ToList();

        var width = Math.Max(xs.Max() - xs.Min(), 100);
        var height = Math.Max(ys.Max() - ys.Min(), 100);

        _scale = Math.Min(ActualWidth * 0.8 / width, ActualHeight * 0.7 / height);
        _centre = new Point2D((xs.Max() + xs.Min()) / 2, (ys.Max() + ys.Min()) / 2);
        InvalidateVisual();
    }

    private Point ToScreen(Point2D point) =>
        new(ActualWidth / 2 + (point.X - _centre.X) * _scale, ActualHeight / 2 - (point.Y - _centre.Y) * _scale);

    private Point2D ToProfile(Point screen) =>
        new(_centre.X + (screen.X - ActualWidth / 2) / _scale, _centre.Y - (screen.Y - ActualHeight / 2) / _scale);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Fit();
    }

    // ---- editing ----------------------------------------------------------------------

    private int PointAt(Point screen)
    {
        for (var i = 0; i < _profile.Count; i++)
            if ((ToScreen(_profile[i]) - screen).Length <= 7) return i;

        return -1;
    }

    private int BendHandleAt(Point screen)
    {
        for (var i = 0; i + 1 < _profile.Count; i++)
            if ((ToScreen(BendHandle(i)) - screen).Length <= 7) return i;

        return -1;
    }

    private int SegmentAt(Point screen)
    {
        var at = ToProfile(screen);
        var reach = 6 / _scale;

        for (var i = 0; i + 1 < _profile.Count; i++)
        {
            var curve = Curve(i);
            for (var k = 0; k + 1 < curve.Count; k++)
                if (Line2D.DistanceFromSegment(at, curve[k], curve[k + 1]) <= reach) return i;
        }

        return -1;
    }

    /// <summary>Distances round to the nearest 10 mm, which is as fine as a roof is set out.</summary>
    private static Point2D Snap(Point2D point) => new(Math.Round(point.X / 10) * 10, Math.Round(point.Y / 10) * 10);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var screen = e.GetPosition(this);

        // A double click on a line puts a new point on it, where it was clicked - on an arc,
        // on the arc, the two halves keeping its curve.
        if (e.ClickCount == 2 && PointAt(screen) < 0 && SegmentAt(screen) is var segment and >= 0)
        {
            var (a, b) = (_profile[segment], _profile[segment + 1]);

            if (_sagittas[segment] == 0)
            {
                _profile.Insert(segment + 1, Snap(ToProfile(screen)));
                _sagittas.Insert(segment + 1, 0);
            }
            else
            {
                var (at, first, second) = RoofExtrusion.SplitArc(a, b, _sagittas[segment], ToProfile(screen));
                _profile.Insert(segment + 1, at);
                _sagittas[segment] = first;
                _sagittas.Insert(segment + 1, second);
            }

            Changed();
            return;
        }

        _dragging = PointAt(screen);
        _bending = _dragging < 0 ? BendHandleAt(screen) : -1;
        if (_dragging >= 0 || _bending >= 0) CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var screen = e.GetPosition(this);
        Cursor = PointAt(screen) >= 0 || BendHandleAt(screen) >= 0 || _dragging >= 0 || _bending >= 0
            ? Cursors.SizeAll
            : Cursors.Arrow;

        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (_dragging >= 0)
        {
            _profile[_dragging] = Snap(ToProfile(screen));
            Changed();
        }
        else if (_bending >= 0)
        {
            // How far the arc bows is how far the handle is dragged off the straight line,
            // square to it. Back within a whisker of the line, it is straight again.
            var (a, b) = (_profile[_bending], _profile[_bending + 1]);
            var normal = (b - a).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
            var bow = Math.Round((ToProfile(screen) - (a + (b - a) / 2)).Dot(normal) / 10) * 10;

            _sagittas[_bending] = Math.Abs(bow) < 20 ? 0 : bow;
            Changed();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = -1;
        _bending = -1;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        // A right click on a point takes it out - but a roof needs two to be a line at all.
        var point = PointAt(e.GetPosition(this));
        if (point < 0 || _profile.Count <= 2) return;

        if (point == 0 || point == _profile.Count - 1)
        {
            _sagittas.RemoveAt(point == 0 ? 0 : point - 1);
        }
        else
        {
            // Between two arcs, the line that replaces them is the arc through all three points,
            // so an arc that was split joins up again unchanged; otherwise it is straight.
            var bothArcs = _sagittas[point - 1] != 0 && _sagittas[point] != 0;
            var joined = bothArcs ? RoofExtrusion.SagittaThrough(_profile[point - 1], _profile[point], _profile[point + 1]) : 0;

            _sagittas[point - 1] = joined;
            _sagittas.RemoveAt(point);
        }

        _profile.RemoveAt(point);
        Changed();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // Zoom about the cursor, so what is under it stays under it.
        var screen = e.GetPosition(this);
        var before = ToProfile(screen);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.002, 2);
        var after = ToProfile(screen);
        _centre = new Point2D(_centre.X + before.X - after.X, _centre.Y + before.Y - after.Y);
        InvalidateVisual();
    }

    private void Changed()
    {
        ProfileChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ---- drawing ----------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        DrawGrid(dc);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // The base the roof sits on, and the width of the building under it.
        var baseY = ToScreen(new Point2D(0, 0)).Y;
        dc.DrawLine(BasePen, new Point(0, baseY), new Point(ActualWidth, baseY));

        var spanFrom = ToScreen(new Point2D(SpanStart, 0));
        var spanTo = ToScreen(new Point2D(SpanStart + Span, 0));
        dc.DrawRectangle(SpanBrush, null, new Rect(new Point(spanFrom.X, baseY), new Point(spanTo.X, baseY + 10)));
        DrawText(dc, $"Span {Units.FormatLength(Span)}", new Point((spanFrom.X + spanTo.X) / 2, baseY + 14), dpi, centred: true);

        // A level the roof is based on is named on the base line, not drawn over it.
        var atBase = Levels.Where(level => Math.Abs(level.Height) < 1).Select(level => level.Name).ToList();
        DrawText(dc, atBase.Count > 0 ? $"Base ({string.Join(", ", atBase)})" : "Base", new Point(6, baseY - 16), dpi);

        foreach (var (name, height) in Levels)
        {
            if (Math.Abs(height) < 1) continue;

            var y = ToScreen(new Point2D(0, height)).Y;
            dc.DrawLine(LevelPen, new Point(0, y), new Point(ActualWidth, y));
            DrawText(dc, name, new Point(6, y - 16), dpi);
        }

        // The profile, a line at a time, with the pitch of each line and the radius of each arc.
        for (var i = 0; i + 1 < _profile.Count; i++)
        {
            var (a, b) = (_profile[i], _profile[i + 1]);
            var curve = Curve(i);
            var isArc = _sagittas[i] != 0;
            var faulty = Enumerable.Range(0, curve.Count - 1).Any(k => curve[k + 1].X - curve[k].X <= 1)
                         || Math.Abs(_sagittas[i]) > a.DistanceTo(b) / 2 + 1e-6;

            var pen = faulty ? BadPen : LinePen;
            for (var k = 0; k + 1 < curve.Count; k++) dc.DrawLine(pen, ToScreen(curve[k]), ToScreen(curve[k + 1]));

            var from = ToScreen(a);
            var to = ToScreen(b);
            if (isArc) dc.DrawLine(ChordPen, from, to);

            if (!faulty)
            {
                var text = isArc && RoofExtrusion.Circle(a, b, _sagittas[i]) is { } circle
                    ? $"R {Units.FormatLength(Math.Round(circle.Radius / 10) * 10)}"
                    : $"{Math.Abs(Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI):0.#}°";

                // Off the top side of the line - or the bow of the arc - far enough out that a
                // steep line does not run through it.
                var label = Text(text, dpi);
                var along = to - from;
                along.Normalize();
                var outward = new Vector(along.Y, -along.X);
                if (_sagittas[i] < 0) outward = -outward;

                var clear = 14 + Math.Abs(outward.X) * label.Width / 2 + Math.Abs(outward.Y) * label.Height / 2;
                var centre = ToScreen(BendHandle(i)) + outward * clear;
                dc.DrawText(label, new Point(centre.X - label.Width / 2, centre.Y - label.Height / 2));
            }

            // The handle that bends it: a small diamond at its middle.
            var handle = ToScreen(BendHandle(i));
            var diamond = new StreamGeometry();
            using (var context = diamond.Open())
            {
                context.BeginFigure(new Point(handle.X, handle.Y - 5), isFilled: true, isClosed: true);
                context.LineTo(new Point(handle.X + 5, handle.Y), true, false);
                context.LineTo(new Point(handle.X, handle.Y + 5), true, false);
                context.LineTo(new Point(handle.X - 5, handle.Y), true, false);
            }

            diamond.Freeze();
            dc.DrawGeometry(isArc ? BendBrush : PointBrush, PointPen, diamond);
        }

        foreach (var point in _profile)
        {
            var at = ToScreen(point);
            dc.DrawRectangle(PointBrush, PointPen, new Rect(at.X - 4, at.Y - 4, 8, 8));
        }
    }

    private void DrawGrid(DrawingContext dc)
    {
        // A line every metre, or every ten when zoomed well out.
        var step = _scale * 1000 >= 12 ? 1000.0 : 10000.0;
        var topLeft = ToProfile(new Point(0, 0));
        var bottomRight = ToProfile(new Point(ActualWidth, ActualHeight));

        for (var x = Math.Floor(topLeft.X / step) * step; x <= bottomRight.X; x += step)
        {
            var sx = ToScreen(new Point2D(x, 0)).X;
            dc.DrawLine(GridPen, new Point(sx, 0), new Point(sx, ActualHeight));
        }

        for (var y = Math.Floor(bottomRight.Y / step) * step; y <= topLeft.Y; y += step)
        {
            var sy = ToScreen(new Point2D(0, y)).Y;
            dc.DrawLine(GridPen, new Point(0, sy), new Point(ActualWidth, sy));
        }
    }

    private static FormattedText Text(string text, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, TextBrush, dpi);

    private static void DrawText(DrawingContext dc, string text, Point at, double dpi, bool centred = false)
    {
        var formatted = Text(text, dpi);
        dc.DrawText(formatted, centred ? new Point(at.X - formatted.Width / 2, at.Y) : at);
    }
}
