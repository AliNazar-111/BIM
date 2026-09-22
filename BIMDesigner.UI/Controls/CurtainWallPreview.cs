using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A curtain wall type seen face-on, drawn to scale for a sample wall: its panels and the
/// mullions framing them, so the grid a type sets out can be seen before it is applied.
/// </summary>
public sealed class CurtainWallPreview : FrameworkElement
{
    public const double SampleLength = 6000;
    public const double SampleHeight = 3000;

    private static readonly Brush GlassBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x8C, 0xC4, 0xE0)));
    private static readonly Brush FrameBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC2)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    private static readonly Pen EdgePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x60, 0x68, 0x74))), 1));

    private CurtainLayout? _layout;

    public void Show(CurtainWallType? design)
    {
        _layout = design is null || design.Problem() is not null
            ? null
            : CurtainLayout.Build(design, SampleLength, SampleHeight, null, null);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_layout is null || ActualWidth < 40 || ActualHeight < 40) return;

        const double margin = 10;
        var labelRoom = 18.0;
        var scale = Math.Min((ActualWidth - 2 * margin) / SampleLength, (ActualHeight - 2 * margin - labelRoom) / SampleHeight);
        var left = (ActualWidth - SampleLength * scale) / 2;
        var bottom = margin + SampleHeight * scale;

        Rect Box(double from, double to, double low, double high) =>
            new(left + from * scale, bottom - high * scale, Math.Max(0, (to - from) * scale), Math.Max(0, (high - low) * scale));

        foreach (var cell in _layout.Cells.Where(c => c.Kind != CurtainPanelKind.Empty))
            dc.DrawRectangle(GlassBrush, null, Box(cell.ClearFrom, cell.ClearTo, cell.ClearBottom, cell.ClearTop));

        foreach (var mullion in _layout.Mullions)
            dc.DrawRectangle(FrameBrush, EdgePen, Box(mullion.From, mullion.To, mullion.Bottom, mullion.Top));

        dc.DrawRectangle(null, EdgePen, Box(0, SampleLength, 0, SampleHeight));

        var text = new FormattedText(
            $"A {SampleLength / 1000:0} m by {SampleHeight / 1000:0} m wall: {_layout.Columns} x {_layout.Rows} panels",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, MutedBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(left, bottom + 4));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
