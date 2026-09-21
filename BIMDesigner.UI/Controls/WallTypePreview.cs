using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A short length of the wall being edited, drawn in plan: exterior at the top, each layer in
/// its material's cut colour and to scale, with the core boundaries marked. It is what the
/// wall will look like on the drawing, so a layer in the wrong place is obvious before it is
/// applied to every wall of the type.
/// </summary>
public sealed class WallTypePreview : FrameworkElement
{
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    private static readonly Pen LayerPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x12, 0x16))), 1));
    private static readonly Pen OutlinePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE3))), 1.5));
    private static readonly Pen CorePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0xAB, 0xFF))), 1.2)
    {
        DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0)
    });

    private IReadOnlyList<(double Thickness, Brush Fill)> _layers = Array.Empty<(double, Brush)>();
    private (int Start, int End) _core = (-1, -1);

    /// <summary>The layers from exterior to interior, and the indices of the first and last core layer.</summary>
    public void Show(IReadOnlyList<(double Thickness, Brush Fill)> layers, int coreStart, int coreEnd)
    {
        _layers = layers;
        _core = (coreStart, coreEnd);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var usable = _layers.Where(l => l.Thickness > 0 && !double.IsNaN(l.Thickness)).ToList();
        var total = usable.Sum(l => l.Thickness);
        if (total <= 0) return;

        const double labelWidth = 70;
        var left = labelWidth;
        var right = ActualWidth - 16;
        var top = 14.0;
        var height = ActualHeight - 34;
        if (right <= left || height <= 0) return;

        var scale = height / total;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var y = top;
        var boundaries = new List<double>();

        for (var i = 0; i < _layers.Count; i++)
        {
            var (thickness, fill) = _layers[i];
            if (!(thickness > 0)) continue;

            var band = new Rect(left, y, right - left, thickness * scale);
            dc.DrawRectangle(fill, LayerPen, band);

            if (i == _core.Start) boundaries.Add(y);
            y += thickness * scale;
            if (i == _core.End) boundaries.Add(y);
        }

        dc.DrawRectangle(null, OutlinePen, new Rect(left, top, right - left, y - top));

        // The core boundaries run past the wall, as they do in the type editors of the tools
        // this follows, so they read as datums rather than as more layer lines.
        foreach (var boundary in boundaries)
            dc.DrawLine(CorePen, new Point(left - 12, boundary), new Point(right + 8, boundary));

        Label(dc, "EXTERIOR", top, dpi);
        Label(dc, "INTERIOR", y - 12, dpi);

        var width = new FormattedText(
            $"{total:0.#} mm", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, TextBrush, dpi);
        dc.DrawText(width, new Point(right - width.Width, y + 4));
    }

    private static void Label(DrawingContext dc, string text, double y, double dpi)
    {
        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10, TextBrush, dpi);

        dc.DrawText(formatted, new Point(4, y));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
