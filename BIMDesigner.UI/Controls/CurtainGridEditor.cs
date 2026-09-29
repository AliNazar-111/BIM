using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// One curtain wall seen face-on, edited by hand: click a panel to fill it with the chosen
/// kind, click a grid line to select the stretch of it between the lines crossing it - Delete
/// takes out that stretch alone, making the panels either side one, as Revit's Add/Remove
/// Segments does - double-click a line for all of it, drag to move it, and double-click
/// elsewhere to add a vertical line (with Shift, a horizontal one) where you click.
///
/// Panel choices are kept by cell, so when a line is added the cell it splits passes its
/// choice to both halves, and when one is taken away the two cells either side become one.
/// </summary>
public sealed class CurtainGridEditor : FrameworkElement
{
    /// <summary>Lines snap to this, in millimetres.</summary>
    public const double Snap = 10;

    /// <summary>The narrowest a panel may be made by moving or adding a line.</summary>
    public const double MinimumPanel = 100;

    private const double Inset = 24;
    private const double HitPixels = 6;

    private static readonly Brush GlassBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x8C, 0xC4, 0xE0)));

    /// <summary>The fill for a pane: clear glass as it has always looked, the rest as themselves.</summary>
    private static Brush GlazingBrush(CurtainGlass glass)
    {
        if (glass == CurtainGlass.Clear) return GlassBrush;

        var colour = CurtainGlassLook.ColourOf(glass, new BIMDesigner.Core.Materials.ColourRgb(0x8C, 0xC4, 0xE0));
        return Frozen(new SolidColorBrush(Color.FromArgb(0xD0, colour.R, colour.G, colour.B)));
    }

    private static readonly Brush SolidBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0x5B, 0x66)));
    private static readonly Brush DoorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA8, 0x84, 0x5C)));
    private static readonly Brush FrameBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC2)));
    private static readonly Brush SelectedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xA3, 0xFF)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    private static readonly Pen EdgePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x60, 0x68, 0x74))), 1));
    private static readonly Pen EmptyPen = Frozen(new Pen(MutedBrush, 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen SelectedPen = Frozen(new Pen(SelectedBrush, 2.5));
    private static readonly Pen RemovedPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x48))), 1.6) { DashStyle = DashStyles.Dash });
    private static readonly Pen SelectedRemovedPen = Frozen(new Pen(SelectedBrush, 2.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen FramePen = Frozen(new Pen(FrameBrush, 2.5));

    private CurtainWallType _type = new("preview");

    /// <summary>The windows cut into the wall, drawn over the panels they cross.</summary>
    private List<(double From, double To, double Sill, double Head)> _windows = new();
    private double _length = 1;
    private double _height = 1;
    private List<double> _verticals = new();
    private List<double> _horizontals = new();
    private List<CurtainSegment> _removed = new();
    private Dictionary<(int Column, int Row), CurtainPanelOverride> _panels = new();

    private (bool Vertical, int Index)? _dragging;

    public CurtainGridEditor()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    /// <summary>Raised whenever a line or panel is changed here.</summary>
    public event EventHandler? Edited;

    /// <summary>Raised with a sentence for the user when something asked for cannot be done.</summary>
    public event EventHandler<string>? Refused;

    /// <summary>What clicking a panel fills it with.</summary>
    public CurtainPanelKind FillWith { get; set; } = CurtainPanelKind.Glazed;

    /// <summary>Which door or window type a panel filled with one becomes.</summary>
    public Guid? OpeningTypeId { get; set; }

    /// <summary>What a panel filled with glass is glazed with.</summary>
    public CurtainGlass FillGlass { get; set; } = CurtainGlass.Clear;

    /// <summary>
    /// The window type a click puts into the wall, or null to fill panels instead. A window is
    /// cut into the wall rather than filling a panel, so it is added as an element of its own
    /// rather than as a panel choice.
    /// </summary>
    public OpeningType? AddWindow { get; set; }

    /// <summary>Where in the panel clicked the window goes, from the middle of it.</summary>
    public double WindowAlong { get; set; }

    public double WindowUp { get; set; }

    /// <summary>The windows added here, for the caller to put into the wall.</summary>
    public IReadOnlyList<(Guid TypeId, double DistanceAlongWall, double SillHeight)> AddedWindows => _added;

    private readonly List<(Guid TypeId, double DistanceAlongWall, double SillHeight)> _added = new();

    /// <summary>
    /// The line selected, if any: vertical or horizontal, by its place among the inner lines -
    /// and which stretch of it, between the lines crossing it, or null for all of it.
    /// </summary>
    public (bool Vertical, int Index, int? Span)? Selected { get; private set; }

    /// <summary>The stretches taken out of the lines.</summary>
    public IReadOnlyList<CurtainSegment> Removed => _removed;

    /// <summary>Whether the lines have been changed from what was shown.</summary>
    public bool GridEdited { get; private set; }

    public IReadOnlyList<double> Verticals => _verticals;

    public IReadOnlyList<double> Horizontals => _horizontals;

    public IReadOnlyList<CurtainPanelOverride> Panels =>
        _panels.Where(p => p.Value.Kind != CurtainPanelKind.Glazed || p.Value.Glass != CurtainGlass.Clear)
            .Select(p => p.Value with { Column = p.Key.Column, Row = p.Key.Row })
            .OrderBy(p => p.Column).ThenBy(p => p.Row)
            .ToList();

    /// <summary>
    /// The wall to edit: its type, size, inner grid lines and panel choices - and its top where it
    /// slopes, under a pitched roof, so a glazed gable is edited as the shape it is.
    /// </summary>
    public void Show(CurtainWallType type, double length, double height,
        IEnumerable<double> verticals, IEnumerable<double> horizontals, IEnumerable<CurtainPanelOverride> panels,
        IEnumerable<(double From, double To, double Sill, double Head)>? windows = null,
        IReadOnlyList<Point2D>? topLine = null, IEnumerable<CurtainSegment>? removed = null)
    {
        _type = type;
        _topLine = topLine;
        _removed = removed?.ToList() ?? new List<CurtainSegment>();
        _windows = windows?.ToList() ?? new List<(double, double, double, double)>();
        _length = Math.Max(length, 1);
        _height = Math.Max(height, 1);
        _verticals = verticals.OrderBy(x => x).ToList();
        _horizontals = horizontals.OrderBy(y => y).ToList();
        _panels = panels.GroupBy(p => (p.Column, p.Row)).ToDictionary(g => g.Key, g => g.Last());
        Selected = null;
        GridEdited = false;
        InvalidateVisual();
    }

    /// <summary>Adds a line: vertical at a distance along the wall, or horizontal at a height.</summary>
    public bool AddLine(bool vertical, double at)
    {
        var lines = vertical ? _verticals : _horizontals;
        var extent = vertical ? _length : _height;
        at = Math.Round(at / Snap) * Snap;

        if (at < MinimumPanel || at > extent - MinimumPanel || lines.Any(x => Math.Abs(x - at) < MinimumPanel))
        {
            Refused?.Invoke(this, $"A line there would leave a panel narrower than {MinimumPanel:0} mm.");
            return false;
        }

        // The cell the new line splits hands its choice to both halves; those beyond move up one.
        var split = lines.Count(x => x < at);
        _panels = _panels
            .SelectMany(p =>
            {
                var index = vertical ? p.Key.Column : p.Key.Row;
                var moved = index > split ? index + 1 : index;
                var keys = index == split ? new[] { index, index + 1 } : new[] { moved };
                return keys.Select(k => (Key: vertical ? (k, p.Key.Row) : (p.Key.Column, k), p.Value));
            })
            .Where(p => p.Value.Kind != CurtainPanelKind.Door || p.Key.Item2 == 0)
            .ToDictionary(p => p.Key, p => p.Value);

        lines.Insert(split, at);
        Selected = (vertical, split, null);
        Changed(grid: true);
        return true;
    }

    /// <summary>
    /// Selects a line - one stretch of it, between the lines crossing it, or all of it with no
    /// stretch given - as a click on it does. Public so it can be driven from tests.
    /// </summary>
    public void Select(bool vertical, int index, int? span)
    {
        Selected = (vertical, index, span);
        InvalidateVisual();
    }

    /// <summary>
    /// Takes away what is selected: a stretch of a line - the panels either side of it become
    /// one - or the whole line, joining the cells either side of it into one. A stretch already
    /// taken out is put back.
    /// </summary>
    public void RemoveSelected()
    {
        if (Selected is not var (vertical, index, span)) return;

        var lines = vertical ? _verticals : _horizontals;
        if (index < 0 || index >= lines.Count) return;

        if (span is { } stretch)
        {
            ToggleStretch(vertical, index, stretch);
            return;
        }

        var line = lines[index];
        _removed.RemoveAll(segment => segment.Vertical == vertical && Math.Abs(segment.Line - line) < 0.5);

        // The line between cell index and index + 1: the second goes, the first takes its place.
        _panels = _panels
            .Where(p => (vertical ? p.Key.Column : p.Key.Row) != index + 1)
            .ToDictionary(
                p =>
                {
                    var i = vertical ? p.Key.Column : p.Key.Row;
                    var moved = i > index + 1 ? i - 1 : i;
                    return vertical ? (moved, p.Key.Row) : (p.Key.Column, moved);
                },
                p => p.Value);

        lines.RemoveAt(index);
        Selected = null;
        Changed(grid: true);
    }

    /// <summary>The lines crossing a line, both edges of the wall included: vertical lines are crossed by the horizontal ones.</summary>
    private List<double> Crossing(bool vertical) =>
        (vertical ? _horizontals : _verticals).Prepend(0).Append(vertical ? _height : _length).ToList();

    /// <summary>Which stretch of a line a point along it falls in, between the lines crossing it.</summary>
    private int StretchAt(bool vertical, double at)
    {
        var crossing = Crossing(vertical);
        for (var i = 0; i + 1 < crossing.Count; i++)
            if (at < crossing[i + 1]) return i;
        return crossing.Count - 2;
    }

    private (double From, double To) Stretch(bool vertical, int span)
    {
        var crossing = Crossing(vertical);
        var i = Math.Clamp(span, 0, crossing.Count - 2);
        return (crossing[i], crossing[i + 1]);
    }

    private bool IsRemoved(bool vertical, double line, int span)
    {
        var (from, to) = Stretch(vertical, span);
        return _removed.Any(segment => segment.Covers(vertical, line, (from + to) / 2));
    }

    /// <summary>
    /// Takes a stretch of a line out, making the panels either side of it one - or puts one back.
    /// Only where the panel made is a rectangle, as a panel has to be.
    /// </summary>
    private void ToggleStretch(bool vertical, int index, int span)
    {
        var line = (vertical ? _verticals : _horizontals)[index];
        var (from, to) = Stretch(vertical, span);
        var middle = (from + to) / 2;

        var existing = _removed.FindIndex(segment => segment.Covers(vertical, line, middle));
        if (existing >= 0)
        {
            _removed.RemoveAt(existing);
            Changed(grid: true);
            return;
        }

        var segment = new CurtainSegment(vertical, line, from, to);
        _removed.Add(segment);

        // The bays either side of it: one panel now, or the stretch cannot come out.
        var layout = Layout();
        CurtainCell? At(double along, double up) =>
            layout.Cells.FirstOrDefault(cell => along > cell.From && along < cell.To && up > cell.Bottom && up < cell.Top);
        var (a, b) = vertical ? (At(line - 1, middle), At(line + 1, middle)) : (At(middle, line - 1), At(middle, line + 1));
        if (a is null || b is null || a.Column != b.Column || a.Row != b.Row)
        {
            _removed.Remove(segment);
            Refused?.Invoke(this, "Taking that stretch out would leave a panel that is not a rectangle. Take out the stretch beside it first, or double-click the line and take it all away.");
            InvalidateVisual();
            return;
        }

        Changed(grid: true);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= 2 * Inset || ActualHeight <= 2 * Inset) return;

        var layout = Layout();

        foreach (var cell in layout.Cells)
        {
            var box = Box(cell.ClearFrom, cell.ClearTo, cell.ClearBottom, cell.ClearTop);
            switch (cell.Kind)
            {
                case CurtainPanelKind.Empty:
                    foreach (var piece in layout.ClearPieces(cell)) dc.DrawGeometry(null, EmptyPen, Shape(piece));
                    break;
                case CurtainPanelKind.Door:
                    dc.DrawRectangle(DoorBrush, EdgePen, box);
                    dc.DrawLine(EdgePen, new Point(box.Left + box.Width * 0.85, box.Top + box.Height * 0.5),
                        new Point(box.Left + box.Width * 0.85, box.Top + box.Height * 0.56));
                    break;

                default:
                    // Under a sloping top, the pane is the shape it is cut to.
                    var fill = cell.Kind == CurtainPanelKind.Solid ? SolidBrush : GlazingBrush(cell.Glass);
                    foreach (var piece in layout.ClearPieces(cell)) dc.DrawGeometry(fill, null, Shape(piece));
                    break;
            }
        }

        foreach (var mullion in layout.Mullions)
            dc.DrawGeometry(FrameBrush, EdgePen, Shape((mullion.From, mullion.To, mullion.Bottom, mullion.Top, mullion.TopAt(mullion.To)), mullion.BottomAt(mullion.To)));

        // The windows cut into the wall, which belong to no one panel: drawn over the glass
        // where they are, so the elevation shows what the wall actually is.
        foreach (var (from, to, sill, head) in _windows)
        {
            var opening = Box(from, to, sill, head);
            if (opening.Width <= 2 || opening.Height <= 2) continue;

            dc.DrawRectangle(GlassBrush, FramePen, opening);
            dc.DrawLine(FramePen, new Point(opening.Left + opening.Width / 2, opening.Top),
                new Point(opening.Left + opening.Width / 2, opening.Bottom));
        }

        // The wall's outline: a rectangle, or up to the rake of a gable.
        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            context.BeginFigure(ToScreen(0, 0), false, true);
            context.LineTo(ToScreen(_length, 0), true, false);
            foreach (var point in (_topLine ?? new[] { new Point2D(0, _height), new Point2D(_length, _height) }).Reverse())
                context.LineTo(ToScreen(point.X, point.Y), true, false);
        }

        outline.Freeze();
        dc.DrawGeometry(null, EdgePen, outline);

        // Every inner line, stretch by stretch, thin, so lines with no mullion on them can still
        // be found - the stretches taken out dotted - up to the top of the wall where it is.
        foreach (var upright in new[] { true, false })
        {
            var lines = upright ? _verticals : _horizontals;
            var crossing = Crossing(upright);
            for (var i = 0; i < lines.Count; i++)
            for (var part = 0; part + 1 < crossing.Count; part++)
            {
                var (from, to) = (crossing[part], crossing[part + 1]);
                if (upright) to = Math.Min(to, layout.TopAt(lines[i]));
                if (to - from <= 1e-6) continue;

                var removed = IsRemoved(upright, lines[i], part);
                var selected = Selected is var (onUpright, onIndex, onStretch) && onUpright == upright && onIndex == i && (onStretch is null || onStretch == part);
                var pen = selected ? removed ? SelectedRemovedPen : SelectedPen : removed ? RemovedPen : EmptyPen;
                dc.DrawLine(pen, upright ? ToScreen(lines[i], from) : ToScreen(from, lines[i]), upright ? ToScreen(lines[i], to) : ToScreen(to, lines[i]));
            }
        }

        var hint = Selected is var (vertical, index, span)
            ? (vertical ? $"Vertical line {Units.FormatLength(_verticals[index])} along" : $"Horizontal line {Units.FormatLength(_horizontals[index])} up") +
              (span is { } stretch
                  ? IsRemoved(vertical, (vertical ? _verticals : _horizontals)[index], stretch)
                      ? ", this stretch taken out. Delete puts it back."
                      : ", the stretch between the lines crossing it. Delete takes out this stretch - the panels either side become one. Double-click the line for all of it; drag to move it."
                  : ", all of it. Drag to move, Delete to take it away.")
            : "Click a panel to fill it. Click a line for the stretch between the lines crossing it, double-click for the whole line. Double-click elsewhere to add a vertical line, Shift for horizontal.";
        dc.DrawText(new FormattedText(hint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, Selected is null ? MutedBrush : TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(8, 4));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        e.Handled = true;

        var position = e.GetPosition(this);
        var (along, up) = ToModel(position);

        if (e.ClickCount == 2)
        {
            // On a line, all of it; anywhere else, a new line there.
            if (LineAt(position) is var (onVertical, onIndex))
            {
                Selected = (onVertical, onIndex, null);
                InvalidateVisual();
                return;
            }

            if (along > 0 && along < _length && up > 0 && up < _height)
                AddLine(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? up : along);
            return;
        }

        if (LineAt(position) is var (vertical, index))
        {
            Selected = (vertical, index, StretchAt(vertical, vertical ? up : along));
            _dragging = (vertical, index);
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        Selected = null;
        if (Layout().Cells.FirstOrDefault(c => along >= c.From && along <= c.To && up >= c.Bottom && up <= c.Top) is { } cell)
            Fill(cell);

        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is not var (vertical, index) || e.LeftButton != MouseButtonState.Pressed) return;

        var (along, up) = ToModel(e.GetPosition(this));
        var lines = vertical ? _verticals : _horizontals;
        var extent = vertical ? _length : _height;

        // Between its neighbours, so cells keep their order and their panel choices.
        var low = (index > 0 ? lines[index - 1] : 0) + MinimumPanel;
        var high = (index < lines.Count - 1 ? lines[index + 1] : extent) - MinimumPanel;
        if (high < low) return;

        var was = lines[index];
        lines[index] = Math.Clamp(Math.Round((vertical ? along : up) / Snap) * Snap, low, high);
        if (Math.Abs(lines[index] - was) < 1e-9) return;

        // Its stretches taken out go with it.
        for (var k = 0; k < _removed.Count; k++)
            if (_removed[k].Vertical == vertical && Math.Abs(_removed[k].Line - was) < 0.5)
                _removed[k] = _removed[k] with { Line = lines[index] };

        Changed(grid: true);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging is null) return;

        _dragging = null;
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back && Selected is not null)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void Fill(CurtainCell cell) => Fill(cell.Column, cell.Row);

    /// <summary>Fills one panel with whatever is chosen, or puts a window in it. Says whether it took.</summary>
    public bool Fill(int column, int row)
    {
        // A window is cut into the wall, so it is added where the panel is rather than
        // becoming the panel.
        if (AddWindow is { } window)
        {
            if (Layout().Cells.FirstOrDefault(c => c.Column == column && c.Row == row) is not { } at) return false;

            // Held inside the panel clicked: a window that wandered across a mullion into the
            // next bay is not something the wall could be built to.
            var halfWide = Math.Min(window.Width, at.ClearTo - at.ClearFrom) / 2;
            var halfTall = Math.Min(window.Height, at.ClearTop - at.ClearBottom) / 2;

            var along = Math.Clamp((at.ClearFrom + at.ClearTo) / 2 + WindowAlong, at.ClearFrom + halfWide, at.ClearTo - halfWide);
            var middle = Math.Clamp((at.ClearBottom + at.ClearTop) / 2 + WindowUp, at.ClearBottom + halfTall, at.ClearTop - halfTall);

            _added.Add((window.Id, Math.Max(0, along), Math.Max(0, middle - window.Height / 2)));
            _windows.Add((along - window.Width / 2, along + window.Width / 2, middle - window.Height / 2, middle + window.Height / 2));
            Changed(grid: false);
            return true;
        }

        // A door stands on the floor; a window sits in its panel, so it can go anywhere.
        if (FillWith == CurtainPanelKind.Door && row != 0)
        {
            Refused?.Invoke(this, "A door goes in a bottom panel, standing on the floor.");
            return false;
        }

        if (FillWith == CurtainPanelKind.Glazed && FillGlass == CurtainGlass.Clear) _panels.Remove((column, row));
        else
            _panels[(column, row)] = new CurtainPanelOverride(
                column, row, FillWith,
                FillWith == CurtainPanelKind.Door ? OpeningTypeId : null,
                FillWith == CurtainPanelKind.Glazed ? FillGlass : CurtainGlass.Clear);

        Changed(grid: false);
        return true;
    }

    private void Changed(bool grid)
    {
        if (grid) GridEdited = true;
        Edited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private CurtainLayout Layout() =>
        CurtainLayout.Build(_type, _length, _height, new CurtainGrid(_verticals, _horizontals, _removed), Panels, topLine: _topLine);

    private IReadOnlyList<Point2D>? _topLine;

    /// <summary>A piece of the elevation with a level bottom and a straight top, as drawn.</summary>
    private Geometry Shape((double From, double To, double Bottom, double TopFrom, double TopTo) piece, double? bottomAtTo = null)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToScreen(piece.From, piece.Bottom), true, true);
            context.LineTo(ToScreen(piece.To, bottomAtTo ?? piece.Bottom), true, false);
            context.LineTo(ToScreen(piece.To, piece.TopTo), true, false);
            context.LineTo(ToScreen(piece.From, piece.TopFrom), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private double Scale => Math.Min((ActualWidth - 2 * Inset) / _length, (ActualHeight - 2 * Inset - 14) / _height);

    private Point ToScreen(double along, double up)
    {
        var scale = Scale;
        var left = (ActualWidth - _length * scale) / 2;
        var bottom = ActualHeight - (ActualHeight - 14 - _height * scale) / 2;
        return new Point(left + along * scale, bottom - up * scale);
    }

    private (double Along, double Up) ToModel(Point point)
    {
        var scale = Scale;
        var left = (ActualWidth - _length * scale) / 2;
        var bottom = ActualHeight - (ActualHeight - 14 - _height * scale) / 2;
        return ((point.X - left) / scale, (bottom - point.Y) / scale);
    }

    private Rect Box(double from, double to, double low, double high) =>
        new(ToScreen(Math.Min(from, to), Math.Max(low, high)), ToScreen(Math.Max(from, to), Math.Min(low, high)));

    private (bool Vertical, int Index)? LineAt(Point position)
    {
        var (along, up) = ToModel(position);
        var reach = HitPixels / Math.Max(Scale, 1e-9);
        if (up < -reach || up > _height + reach || along < -reach || along > _length + reach) return null;

        var vertical = _verticals.Select((x, i) => (Distance: Math.Abs(x - along), i)).Where(v => v.Distance <= reach).OrderBy(v => v.Distance).FirstOrDefault();
        var horizontal = _horizontals.Select((y, i) => (Distance: Math.Abs(y - up), i)).Where(h => h.Distance <= reach).OrderBy(h => h.Distance).FirstOrDefault();

        var hasVertical = _verticals.Count > 0 && _verticals.Any(x => Math.Abs(x - along) <= reach);
        var hasHorizontal = _horizontals.Count > 0 && _horizontals.Any(y => Math.Abs(y - up) <= reach);
        if (hasVertical && (!hasHorizontal || vertical.Distance <= horizontal.Distance)) return (true, vertical.i);
        if (hasHorizontal) return (false, horizontal.i);
        return null;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
