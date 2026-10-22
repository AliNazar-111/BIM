using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Pipes in plan, each in its system's colour - cold water blue, hot water red, the heating
/// orange, foul water brown, gas yellow: a run along the storey as a line as wide as the pipe, or
/// two pixels where that is less; one rising or dropping as its circle, a stroke across it where it
/// drops.
/// </summary>
public sealed partial class PlanRenderer
{
    private void DrawPipes(DrawingContext dc)
    {
        foreach (var pipe in OnActiveLevel<Pipe>()) Styled(dc, pipe, d => DrawPipe(d, pipe));
    }

    private void DrawPipe(DrawingContext dc, Pipe pipe)
    {
        var colour = pipe.Kind.Colour();
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        var selected = IsSelected(pipe);

        if (pipe.Upright)
        {
            var at = ModelToScreen(pipe.Start);
            var radius = Math.Max(Page(3), pipe.Diameter / 2 * PixelsPerMm);
            var ring = new Pen(selected ? _selectedPen.Brush : brush, Page(1.2));
            dc.DrawEllipse(null, ring, at, radius, radius);
            if (pipe.EndOffset < pipe.StartOffset)
                dc.DrawLine(ring, new Point(at.X - radius * 0.7, at.Y + radius * 0.7), new Point(at.X + radius * 0.7, at.Y - radius * 0.7));
            return;
        }

        var pen = new Pen(brush, Math.Max(Page(2), pipe.Diameter * PixelsPerMm)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var (start, end) = (ModelToScreen(pipe.Start), ModelToScreen(pipe.End));
        dc.DrawLine(pen, start, end);
        if (selected) dc.DrawLine(_selectedPen, start, end);
    }
}
