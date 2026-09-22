using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A wall's elevation outline, drawn to scale and edited by hand: drag a corner to move it,
/// double-click an edge to add a corner there, Delete to remove the selected one. The wall's
/// plain rectangle and its doors and windows are shown behind for reference.
/// </summary>
public sealed class ProfileEditor : FrameworkElement
{
    /// <summary>Corners snap to this, in millimetres.</summary>
    public const double Snap = 10;

    private const double Inset = 28;
    private const double HandleRadius = 5;
    private const double HitPixels = 8;

    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    private static readonly Brush FillBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xB0, 0x7A, 0x5C)));
    private static readonly Brush HoleBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0x8C, 0xC4, 0xE0)));
    private static readonly Brush HandleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)));
    private static readonly Brush SelectedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xA3, 0xFF)));
    private static readonly Pen OutlinePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5))), 1.6));
    private static readonly Pen GroundPen = Frozen(new Pen(MutedBrush, 1));
    private static readonly Pen GhostPen = Frozen(new Pen(MutedBrush, 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen GuidePen = Frozen(new Pen(SelectedBrush, 1) { DashStyle = DashStyles.Dash });

    private List<Point2D> _corners = new();
    private IReadOnlyList<(double From, double To, double Sill, double Head)> _holes = Array.Empty<(double, double, double, double)>();
    private double _length = 1;
    private double _height = 1;
    private int _dragging = -1;

    // The lines the dragged corner is held to, drawn as guides through it.
    private IReadOnlyList<SnapGuide> _guides = Array.Empty<SnapGuide>();

    // The model area on screen, kept still while a corner is dragged so the view does not
    // slide under the cursor; refitted when the drag ends.
    private Rect _frame = new(0, 0, 1, 1);

    public ProfileEditor()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    /// <summary>Raised whenever a corner is moved, added or removed here.</summary>
    public event EventHandler? Edited;

    /// <summary>Raised when a different corner is selected.</summary>
    public event EventHandler? SelectionChanged;

    public IReadOnlyList<Point2D> Corners => _corners;

    /// <summary>The corner selected, or -1.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>The wall to edit: its outline, its length and plain height, and the holes its doors and windows make.</summary>
    public void Show(IReadOnlyList<Point2D> corners, double length, double height,
        IReadOnlyList<(double From, double To, double Sill, double Head)> holes)
    {
        _corners = corners.ToList();
        _length = Math.Max(length, 1);
        _height = Math.Max(height, 1);
        _holes = holes;
        SelectedIndex = -1;
        Refit();
    }

    /// <summary>Replaces the outline, from the corners table or a preset.</summary>
    public void SetCorners(IEnumerable<Point2D> corners)
    {
        _corners = corners.ToList();
        if (SelectedIndex >= _corners.Count) Select(-1);
        Refit();
    }

    public void Select(int index)
    {
        if (index == SelectedIndex) return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>A new corner halfway along the edge after the selected corner, or the last edge.</summary>
    public void AddCorner()
    {
        if (_corners.Count < 2) return;

        var at = SelectedIndex >= 0 ? SelectedIndex : _corners.Count - 1;
        var a = _corners[at];
        var b = _corners[(at + 1) % _corners.Count];
        _corners.Insert(at + 1, Snapped(a.MidpointTo(b)));
        Changed();
        Select(at + 1);
    }

    /// <summary>Removes the selected corner, as long as three are left.</summary>
    public void RemoveCorner()
    {
        if (SelectedIndex < 0 || _corners.Count <= 3) return;

        _corners.RemoveAt(SelectedIndex);
        Changed();
        Select(Math.Min(SelectedIndex, _corners.Count - 1));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= 2 * Inset || ActualHeight <= 2 * Inset) return;

        // The ground, and the rectangle the wall would be without a profile.
        dc.DrawLine(GroundPen, ToScreen(new Point2D(_frame.Left, 0)), ToScreen(new Point2D(_frame.Right, 0)));
        dc.DrawRectangle(null, GhostPen, new Rect(ToScreen(new Point2D(0, _height)), ToScreen(new Point2D(_length, 0))));

        if (_corners.Count >= 3)
        {
            var outline = new StreamGeometry();
            using (var ctx = outline.Open())
            {
                ctx.BeginFigure(ToScreen(_corners[0]), true, true);
                ctx.PolyLineTo(_corners.Skip(1).Select(ToScreen).ToList(), true, false);
            }

            outline.Freeze();
            dc.DrawGeometry(FillBrush, OutlinePen, outline);
        }

        foreach (var (from, to, sill, head) in _holes)
            dc.DrawRectangle(HoleBrush, null, new Rect(ToScreen(new Point2D(from, head)), ToScreen(new Point2D(to, sill))));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // The lines a dragged corner is held to: an angle from a neighbour, labelled with it, or
        // level with or plumb over another corner, with that corner ringed.
        if (_dragging >= 0)
        {
            var reach = 2 * Math.Max(_frame.Width, _frame.Height);
            var corner = _corners[_dragging];
            foreach (var guide in _guides)
            {
                dc.DrawLine(GuidePen, ToScreen(guide.Anchor - guide.Direction * reach), ToScreen(guide.Anchor + guide.Direction * reach));

                if (guide.IsAngle)
                {
                    var label = new FormattedText($"{AngleBetween(guide.Anchor, corner):0.#}°", CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, SelectedBrush, dpi);
                    var at = ToScreen(guide.Anchor.MidpointTo(corner));
                    dc.DrawText(label, new Point(at.X + 6, at.Y - label.Height - 2));
                }
                else
                {
                    dc.DrawEllipse(null, GuidePen, ToScreen(guide.Anchor), HandleRadius + 4, HandleRadius + 4);
                }
            }
        }

        for (var i = 0; i < _corners.Count; i++)
        {
            var centre = ToScreen(_corners[i]);
            dc.DrawRectangle(i == SelectedIndex ? SelectedBrush : HandleBrush, null,
                new Rect(centre.X - HandleRadius, centre.Y - HandleRadius, 2 * HandleRadius, 2 * HandleRadius));
        }

        var hint = SelectedIndex >= 0 && SelectedIndex < _corners.Count
            ? $"Corner {SelectedIndex + 1}: {Units.FormatLength(_corners[SelectedIndex].X)} along, {Units.FormatLength(_corners[SelectedIndex].Y)} high   ·   {EdgeText()}"
            : "Drag a corner: it locks to the snap angle from its neighbours and lines up with other corners; Shift holds an angle. Double-click an edge to add a corner; Delete removes one.";
        dc.DrawText(new FormattedText(hint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, SelectedIndex >= 0 ? TextBrush : MutedBrush, dpi), new Point(8, 6));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Refit();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var position = e.GetPosition(this);
        var corner = CornerAt(position);

        if (corner >= 0)
        {
            Select(corner);
            _dragging = corner;
            CaptureMouse();
        }
        else if (e.ClickCount == 2 && EdgeAt(position) is { } edge)
        {
            _corners.Insert(edge + 1, Snapped(ToModel(position)));
            Changed();
            Select(edge + 1);
        }
        else
        {
            Select(-1);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging < 0 || e.LeftButton != MouseButtonState.Pressed) return;

        var (corner, guides) = SnapCorner(_dragging, ToModel(e.GetPosition(this)), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _corners[_dragging] = corner;
        _guides = guides;
        Changed();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging < 0) return;

        _dragging = -1;
        ReleaseMouseCapture();
        _guides = Array.Empty<SnapGuide>();
        Refit();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back)
        {
            RemoveCorner();
            e.Handled = true;
        }
    }

    private void Changed()
    {
        Edited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// The step angles snap to, in degrees: an edge being dragged locks to a multiple of it
    /// from the corner at either end. Zero turns angle snapping off.
    /// </summary>
    public double AngleStep { get; set; } = 45;

    /// <summary>How close, on screen, the cursor must come to an angle for the corner to lock to it.</summary>
    private const double AngleSnapPixels = 10;

    /// <summary>A line a dragged corner is held to, drawn as a guide through it.</summary>
    private readonly record struct SnapGuide(Point2D Anchor, Vector2D Direction, bool IsAngle);

    /// <summary>
    /// Where a dragged corner goes. It can be held to lines: from each neighbouring corner at the
    /// nearest step angle, and level with or plumb over every other corner. The nearest line
    /// within reach takes the corner; if a second line crosses it near the cursor, the corner
    /// goes to the crossing, so it can be both at 45° from one corner and level with another.
    /// Shift keeps an angle lock however far off the cursor is. With nothing in reach the
    /// corner snaps to the grid.
    /// </summary>
    private (Point2D Corner, IReadOnlyList<SnapGuide> Guides) SnapCorner(int index, Point2D raw, bool force)
    {
        if (_corners.Count < 3) return (Snapped(raw), Array.Empty<SnapGuide>());

        var tolerance = AngleSnapPixels / Math.Max(Scale, 1e-9);
        var count = _corners.Count;
        var lines = new List<(SnapGuide Guide, bool Forced)>();

        // Angles from the two corners it is joined to.
        if (AngleStep > 0)
        {
            foreach (var neighbour in new[] { _corners[(index + count - 1) % count], _corners[(index + 1) % count] })
            {
                var toward = raw - neighbour;
                if (toward.Length < 1e-9) continue;

                var step = AngleStep * Math.PI / 180;
                var angle = Math.Round(Math.Atan2(toward.Y, toward.X) / step) * step;
                lines.Add((new SnapGuide(neighbour, new Vector2D(Math.Cos(angle), Math.Sin(angle)), true), force));
            }
        }

        // Level with, or straight above or below, any other corner.
        for (var i = 0; i < count; i++)
        {
            if (i == index) continue;
            lines.Add((new SnapGuide(_corners[i], Vector2D.UnitX, false), false));
            lines.Add((new SnapGuide(_corners[i], Vector2D.UnitY, false), false));
        }

        Point2D Foot(SnapGuide g) => g.Anchor + g.Direction * (raw - g.Anchor).Dot(g.Direction);

        var near = lines
            .Select(l => (l.Guide, l.Forced, Off: Foot(l.Guide).DistanceTo(raw)))
            .Where(l => l.Forced || l.Off <= tolerance)
            .OrderBy(l => l.Off)
            .ToList();
        if (near.Count == 0) return (Snapped(raw), Array.Empty<SnapGuide>());

        var first = near[0].Guide;

        // A second line across the first, crossing it close to the cursor.
        var second = near.Skip(1)
            .Where(l => Math.Abs(l.Guide.Direction.Cross(first.Direction)) > 1e-6)
            .Select(l => (l.Guide, Crossing: Line2D.TryIntersect(new Line2D(first.Anchor, first.Direction), new Line2D(l.Guide.Anchor, l.Guide.Direction), out var p) ? p : (Point2D?)null))
            .Where(l => l.Crossing is { } p && p.DistanceTo(raw) <= tolerance * 1.5)
            .OrderBy(l => l.Crossing!.Value.DistanceTo(raw))
            .FirstOrDefault();

        if (second.Crossing is { } crossing)
            return (Clamped(crossing), new[] { first, second.Guide });

        // On the one line, a whole number of snap steps from where it starts.
        var distance = Math.Round((Foot(first) - first.Anchor).Dot(first.Direction) / Snap) * Snap;
        return (Clamped(first.Anchor + first.Direction * distance), new[] { first });
    }

    /// <summary>
    /// Puts the selected corner an exact distance and angle from the corner before it. The
    /// angle is measured anticlockwise from the direction along the wall: 0° runs toward the
    /// wall's end, 90° straight up.
    /// </summary>
    public bool PlaceFromPrevious(double length, double angleDegrees)
    {
        if (SelectedIndex < 0 || _corners.Count < 3) return false;

        var previous = _corners[(SelectedIndex + _corners.Count - 1) % _corners.Count];
        var angle = angleDegrees * Math.PI / 180;
        _corners[SelectedIndex] = Clamped(previous + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * length);
        Changed();
        Refit();
        return true;
    }

    /// <summary>The angle from one corner to another, anticlockwise from along the wall, 0 to 360.</summary>
    public static double AngleBetween(Point2D from, Point2D to)
    {
        var degrees = Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI;
        degrees = Math.Round(degrees, 6);
        return degrees < 0 ? degrees + 360 : degrees;
    }

    /// <summary>The edge from the previous corner to the selected one: its length and angle.</summary>
    public string EdgeText()
    {
        if (SelectedIndex < 0 || _corners.Count < 3) return string.Empty;

        var previous = _corners[(SelectedIndex + _corners.Count - 1) % _corners.Count];
        var corner = _corners[SelectedIndex];
        var number = (SelectedIndex + _corners.Count - 1) % _corners.Count + 1;
        return $"from corner {number}: {Units.FormatLength(previous.DistanceTo(corner))} at {AngleBetween(previous, corner):0.##}°";
    }

    /// <summary>Kept between the ends of the wall, without rounding.</summary>
    private Point2D Clamped(Point2D point) => new(Math.Clamp(point.X, 0, _length), point.Y);

    /// <summary>Rounded to the snap, and kept between the ends of the wall.</summary>
    private Point2D Snapped(Point2D point) =>
        new(Math.Clamp(Math.Round(point.X / Snap) * Snap, 0, _length), Math.Round(point.Y / Snap) * Snap);

    /// <summary>Frames the wall, its plain rectangle and every corner, with room round them.</summary>
    private void Refit()
    {
        var xs = _corners.Select(c => c.X).Append(0).Append(_length).ToList();
        var ys = _corners.Select(c => c.Y).Append(0).Append(_height).ToList();
        var width = Math.Max(xs.Max() - xs.Min(), 1);
        var height = Math.Max(ys.Max() - ys.Min(), 1);

        // Some headroom above, so a corner can be dragged higher than the wall is now.
        _frame = new Rect(xs.Min(), ys.Min(), width, height * 1.25);
        InvalidateVisual();
    }

    private double Scale =>
        Math.Min((ActualWidth - 2 * Inset) / _frame.Width, (ActualHeight - 2 * Inset) / _frame.Height);

    private Point ToScreen(Point2D point)
    {
        var scale = Scale;
        var left = (ActualWidth - _frame.Width * scale) / 2;
        var bottom = ActualHeight - (ActualHeight - _frame.Height * scale) / 2;
        return new Point(left + (point.X - _frame.Left) * scale, bottom - (point.Y - _frame.Top) * scale);
    }

    private Point2D ToModel(Point point)
    {
        var scale = Scale;
        var left = (ActualWidth - _frame.Width * scale) / 2;
        var bottom = ActualHeight - (ActualHeight - _frame.Height * scale) / 2;
        return new Point2D(_frame.Left + (point.X - left) / scale, _frame.Top + (bottom - point.Y) / scale);
    }

    private int CornerAt(Point position)
    {
        for (var i = _corners.Count - 1; i >= 0; i--)
            if ((ToScreen(_corners[i]) - position).Length <= HitPixels) return i;
        return -1;
    }

    /// <summary>The edge under the cursor, by the index of the corner it starts at.</summary>
    private int? EdgeAt(Point position)
    {
        for (var i = 0; i < _corners.Count; i++)
        {
            var a = ToScreen(_corners[i]);
            var b = ToScreen(_corners[(i + 1) % _corners.Count]);
            var ab = b - a;
            var t = ab.LengthSquared <= 0 ? 0 : Math.Clamp(Vector.Multiply(position - a, ab) / ab.LengthSquared, 0, 1);
            if ((a + ab * t - position).Length <= HitPixels) return i;
        }

        return null;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
