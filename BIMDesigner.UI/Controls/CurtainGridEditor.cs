using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// One curtain wall seen face-on, edited by hand: click a panel to fill it with the chosen
/// kind, click a grid line to select it and drag to move it, Delete to take it away, and
/// double-click to add a vertical line (with Shift, a horizontal one) where you click.
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

    private CurtainWallType _type = new("preview");
    private double _length = 1;
    private double _height = 1;
    private List<double> _verticals = new();
    private List<double> _horizontals = new();
    private Dictionary<(int Column, int Row), (CurtainPanelKind Kind, Guid? OpeningTypeId, CurtainGlass Glass)> _panels = new();

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

    /// <summary>Which door type a panel filled with a door becomes; null for the plain storefront leaf.</summary>
    public Guid? OpeningTypeId { get; set; }

    /// <summary>What a panel filled with glass is glazed with.</summary>
    public CurtainGlass FillGlass { get; set; } = CurtainGlass.Clear;

    /// <summary>The line selected, if any: vertical or horizontal, by its place among the inner lines.</summary>
    public (bool Vertical, int Index)? Selected { get; private set; }

    /// <summary>Whether the lines have been changed from what was shown.</summary>
    public bool GridEdited { get; private set; }

    public IReadOnlyList<double> Verticals => _verticals;

    public IReadOnlyList<double> Horizontals => _horizontals;

    public IReadOnlyList<CurtainPanelOverride> Panels =>
        _panels.Where(p => p.Value.Kind != CurtainPanelKind.Glazed || p.Value.Glass != CurtainGlass.Clear)
            .Select(p => new CurtainPanelOverride(p.Key.Column, p.Key.Row, p.Value.Kind, p.Value.OpeningTypeId, p.Value.Glass))
            .OrderBy(p => p.Column).ThenBy(p => p.Row)
            .ToList();

    /// <summary>The wall to edit: its type, size, inner grid lines and panel choices.</summary>
    public void Show(CurtainWallType type, double length, double height,
        IEnumerable<double> verticals, IEnumerable<double> horizontals, IEnumerable<CurtainPanelOverride> panels)
    {
        _type = type;
        _length = Math.Max(length, 1);
        _height = Math.Max(height, 1);
        _verticals = verticals.OrderBy(x => x).ToList();
        _horizontals = horizontals.OrderBy(y => y).ToList();
        _panels = panels.GroupBy(p => (p.Column, p.Row)).ToDictionary(g => g.Key, g => (g.Last().Kind, g.Last().OpeningTypeId, g.Last().Glass));
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
        Selected = (vertical, split);
        Changed(grid: true);
        return true;
    }

    /// <summary>Takes away the selected line, joining the cells either side of it into one.</summary>
    public void RemoveSelected()
    {
        if (Selected is not var (vertical, index)) return;

        var lines = vertical ? _verticals : _horizontals;
        if (index < 0 || index >= lines.Count) return;

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
                    dc.DrawRectangle(null, EmptyPen, box);
                    break;
                case CurtainPanelKind.Door:
                    dc.DrawRectangle(DoorBrush, EdgePen, box);
                    dc.DrawLine(EdgePen, new Point(box.Left + box.Width * 0.85, box.Top + box.Height * 0.5),
                        new Point(box.Left + box.Width * 0.85, box.Top + box.Height * 0.56));
                    break;
                default:
                    dc.DrawRectangle(
                        cell.Kind == CurtainPanelKind.Solid ? SolidBrush : GlazingBrush(cell.Glass), null, box);
                    break;
            }
        }

        foreach (var mullion in layout.Mullions)
            dc.DrawRectangle(FrameBrush, EdgePen, Box(mullion.From, mullion.To, mullion.Bottom, mullion.Top));

        dc.DrawRectangle(null, EdgePen, Box(0, _length, 0, _height));

        // Every inner line, thin, so lines with no mullion on them can still be found.
        for (var i = 0; i < _verticals.Count; i++)
            dc.DrawLine(Selected == (true, i) ? SelectedPen : EmptyPen, ToScreen(_verticals[i], 0), ToScreen(_verticals[i], _height));
        for (var i = 0; i < _horizontals.Count; i++)
            dc.DrawLine(Selected == (false, i) ? SelectedPen : EmptyPen, ToScreen(0, _horizontals[i]), ToScreen(_length, _horizontals[i]));

        var hint = Selected is var (vertical, index)
            ? vertical
                ? $"Vertical line {Units.FormatLength(_verticals[index])} along. Drag to move, Delete to take it away."
                : $"Horizontal line {Units.FormatLength(_horizontals[index])} up. Drag to move, Delete to take it away."
            : "Click a panel to fill it. Click a line to move or delete it. Double-click to add a vertical line, Shift for horizontal.";
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
            if (along > 0 && along < _length && up > 0 && up < _height)
                AddLine(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? up : along);
            return;
        }

        if (LineAt(position) is { } line)
        {
            Selected = line;
            _dragging = line;
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

        lines[index] = Math.Clamp(Math.Round((vertical ? along : up) / Snap) * Snap, low, high);
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

    private void Fill(CurtainCell cell)
    {
        // A door stands on the floor.
        if (FillWith == CurtainPanelKind.Door && cell.Row != 0)
        {
            Refused?.Invoke(this, "A door goes in a bottom panel, standing on the floor.");
            return;
        }

        if (FillWith == CurtainPanelKind.Glazed && FillGlass == CurtainGlass.Clear) _panels.Remove((cell.Column, cell.Row));
        else
            _panels[(cell.Column, cell.Row)] = (
                FillWith,
                FillWith == CurtainPanelKind.Door ? OpeningTypeId : null,
                FillWith == CurtainPanelKind.Glazed ? FillGlass : CurtainGlass.Clear);
        Changed(grid: false);
    }

    private void Changed(bool grid)
    {
        if (grid) GridEdited = true;
        Edited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private CurtainLayout Layout() =>
        CurtainLayout.Build(_type, _length, _height, new CurtainGrid(_verticals, _horizontals), Panels);

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
