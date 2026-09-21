using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A stacked wall seen in elevation, drawn to scale for a wall of a sample height: each tier in
/// its type's colour, labelled with its type and how high it runs. The variable tier is the one
/// that would stretch if the wall were taller.
/// </summary>
public sealed class StackedWallPreview : FrameworkElement
{
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    private static readonly Pen OutlinePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE3))), 1.2));

    private BimDocument? _document;
    private IReadOnlyList<StackTier> _tiers = Array.Empty<StackTier>();

    /// <summary>The height the preview draws the wall at.</summary>
    public const double SampleHeight = 3000;

    public void Show(BimDocument document, IReadOnlyList<StackTier> tiers)
    {
        _document = document;
        _tiers = tiers;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_document is null || _tiers.Count == 0) return;

        var probe = new StackedWallType("preview");
        probe.Tiers.AddRange(_tiers);
        var layout = probe.Layout(_document, SampleHeight);
        if (layout.Count == 0) return;

        const double left = 12;
        const double wallWidth = 90;
        var top = 10.0;
        var height = ActualHeight - 20;
        if (height <= 0) return;

        var scale = height / SampleHeight;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var variableType = _tiers.FirstOrDefault(t => t.IsVariable)?.WallTypeId;

        foreach (var (type, bottom, tierTop) in layout)
        {
            var y = top + (SampleHeight - tierTop) * scale;
            var band = new Rect(left, y, wallWidth, (tierTop - bottom) * scale);

            var colour = type.CoarseScaleFillColour;
            var fill = Frozen(new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B)));
            dc.DrawRectangle(fill, OutlinePen, band);

            var label = new FormattedText(
                type.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, TextBrush, dpi);
            var detail = new FormattedText(
                $"{bottom:0} to {tierTop:0} mm{(type.Id == variableType ? "  - variable" : string.Empty)}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, MutedBrush, dpi);

            var middle = band.Top + band.Height / 2;
            dc.DrawText(label, new Point(left + wallWidth + 12, middle - label.Height));
            dc.DrawText(detail, new Point(left + wallWidth + 12, middle));
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
