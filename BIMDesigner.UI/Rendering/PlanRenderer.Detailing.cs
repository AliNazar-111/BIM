using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Detail lines and filled regions as the view draws them: each line at its weight on the paper -
/// thin, medium or wide - dashed or chained as its style asks; each region solid, or hatched with
/// lines kept the same distance apart on the paper whatever the view's scale, cut to its outline
/// so it prints, plots and goes to a DWG the same; and its edge, where it has one. In the ink the
/// building is drawn in.
/// </summary>
public sealed partial class PlanRenderer
{
    /// <summary>Whether a drafting view is being drawn, not a storey's plan: no gridlines, nothing of the model.</summary>
    private bool Drafting => Document?.FindElement(ActiveLevelId) is DraftingView;

    /// <summary>A detail line's weight on the paper, mm.</summary>
    private static double Weight(DetailLineStyle style) => style switch
    {
        DetailLineStyle.Thin => 0.13,
        DetailLineStyle.Wide => 0.5,
        _ => 0.25
    };

    /// <summary>A pen for a detail line: its weight on the paper, and its dashes in paper millimetres.</summary>
    private Pen DetailPen(DetailLineStyle style, Color colour)
    {
        var width = Math.Max(0.6, Weight(style) * PixelsPerPaperMm);
        var pen = new Pen(new SolidColorBrush(colour), width) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
        double[]? dashes = style switch
        {
            DetailLineStyle.Hidden => new[] { 2.0, 1.0 },
            DetailLineStyle.Centre => new[] { 6.0, 1.0, 1.0, 1.0 },
            DetailLineStyle.Overhead => new[] { 6.0, 2.0 },
            _ => null
        };
        if (dashes is not null) pen.DashStyle = new DashStyle(dashes.Select(length => length * PixelsPerPaperMm / width), 0);
        pen.Freeze();
        return pen;
    }

    private void DrawDetailLine(DrawingContext dc, DetailLine line)
    {
        var pen = IsSelected(line) ? _selectedPen : DetailPen(line.Style, _ink.WallOutline);
        dc.DrawLine(pen, ModelToScreen(line.Start), ModelToScreen(line.End));
    }

    private void DrawFilledRegion(DrawingContext dc, FilledRegion region)
    {
        if (region.Boundary.Count < 3) return;
        var ink = _ink.WallOutline;
        var outline = BuildOutline(region.Boundary);

        switch (region.Pattern)
        {
            case FillPattern.SolidBlack:
                dc.DrawGeometry(Brush(ink), null, outline);
                break;
            case FillPattern.SolidGrey:
                dc.DrawGeometry(Brush(Color.FromArgb(0x60, ink.R, ink.G, ink.B)), null, outline);
                break;
            default:
                // Hatched: each set of lines its spacing apart on the paper, cut to the outline.
                var hatch = DetailPen(DetailLineStyle.Thin, ink);
                foreach (var (degrees, spacing) in Detailing.Lines(region.Pattern))
                    foreach (var (from, to) in Detailing.Hatch(region.Boundary, degrees, OnPaperAsModel(spacing)))
                        dc.DrawLine(hatch, ModelToScreen(from), ModelToScreen(to));
                break;
        }

        if (region.Outlined || IsSelected(region))
            dc.DrawGeometry(null, IsSelected(region) ? _selectedPen : DetailPen(DetailLineStyle.Medium, ink), outline);
    }

    private static Brush Brush(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
