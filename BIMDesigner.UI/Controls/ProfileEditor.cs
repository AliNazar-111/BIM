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

    private List<Point2D> _corners = new();
    private IReadOnlyList<(double From, double To, double Sill, double Head)> _holes = Array.Empty<(double, double, double, double)>();
    private double _length = 1;
    private double _height = 1;
    private int _dragging = -1;

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

        for (var i = 0; i < _corners.Count; i++)
        {
            var centre = ToScreen(_corners[i]);
            dc.DrawRectangle(i == SelectedIndex ? SelectedBrush : HandleBrush, null,
                new Rect(centre.X - HandleRadius, centre.Y - HandleRadius, 2 * HandleRadius, 2 * HandleRadius));
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var hint = SelectedIndex >= 0 && SelectedIndex < _corners.Count
            ? $"Corner {SelectedIndex + 1}: {Units.FormatLength(_corners[SelectedIndex].X)} along, {Units.FormatLength(_corners[SelectedIndex].Y)} high"
            : "Drag a corner. Double-click an edge to add one; Delete removes the selected corner.";
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

        _corners[_dragging] = Snapped(ToModel(e.GetPosition(this)));
        Changed();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging < 0) return;

        _dragging = -1;
        ReleaseMouseCapture();
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
