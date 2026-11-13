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
        foreach (var valve in OnActiveLevel<PipeValve>()) Styled(dc, valve, d => DrawValve(d, valve));
    }

    /// <summary>
    /// A valve on its pipe as drawings show one: two triangles point to point across the pipe - open for a servicing valve, filled
    /// for a stopvalve, one filled for a double check valve, and a spout off it for a drain-off tap; on an upright pipe, its circle crossed.
    /// </summary>
    private void DrawValve(DrawingContext dc, PipeValve valve)
    {
        if (Document is null || Document.Find<Pipe>(valve.PipeId) is not { } pipe || PipeValves.At(Document, valve) is not { } at) return;
        var colour = pipe.Kind.Colour();
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        var ink = IsSelected(valve) ? _selectedPen.Brush : brush;
        var pen = new Pen(ink, Page(1.2));
        var centre = ModelToScreen(new Core.Geometry.Point2D(at.X, at.Y));
        var size = Math.Max(Page(7), pipe.Diameter * 1.6 * PixelsPerMm);

        if (pipe.Upright)
        {
            dc.DrawEllipse(Brushes.Transparent, pen, centre, size, size);
            dc.DrawLine(pen, new Point(centre.X - size * 0.7, centre.Y - size * 0.7), new Point(centre.X + size * 0.7, centre.Y + size * 0.7));
            dc.DrawLine(pen, new Point(centre.X - size * 0.7, centre.Y + size * 0.7), new Point(centre.X + size * 0.7, centre.Y - size * 0.7));
            return;
        }

        var (start, end) = (ModelToScreen(pipe.Start), ModelToScreen(pipe.End));
        var along = end - start;
        if (along.Length < 1e-6) return;
        along.Normalize();
        var across = new Vector(-along.Y, along.X);
        Point P(double a, double c) => centre + along * a + across * c;
        StreamGeometry Triangle(double side)
        {
            var triangle = new StreamGeometry();
            using (var g = triangle.Open())
            {
                g.BeginFigure(centre, true, true);
                g.LineTo(P(side * size, size * 0.7), true, false);
                g.LineTo(P(side * size, -size * 0.7), true, false);
            }

            triangle.Freeze();
            return triangle;
        }

        var paper = new SolidColorBrush(_ink.LabelBackdrop);
        paper.Freeze();
        dc.DrawGeometry(valve.Kind is ValveKind.Stop or ValveKind.DoubleCheck ? ink : paper, pen, Triangle(-1));
        dc.DrawGeometry(valve.Kind == ValveKind.Stop ? ink : paper, pen, Triangle(1));
        if (valve.Kind == ValveKind.DrainOff)
            dc.DrawLine(pen, centre, P(0, size * 1.6));
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

        // A heating return dashed, as drawings tell it from the flow.
        var pen = new Pen(brush, Math.Max(Page(2), pipe.Diameter * PixelsPerMm)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (pipe.Kind == SystemKind.Heating && pipe.Return) pen.DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0);
        var (start, end) = (ModelToScreen(pipe.Start), ModelToScreen(pipe.End));
        dc.DrawLine(pen, start, end);
        if (selected) dc.DrawLine(_selectedPen, start, end);
    }
}
