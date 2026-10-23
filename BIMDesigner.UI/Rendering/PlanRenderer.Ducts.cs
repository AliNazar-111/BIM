using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Ducts in plan, each in its system's colour - supply blue, extract violet, outside air green: a run
/// along the storey as its two sides, as wide as it is, washed in its colour; one rising or dropping
/// as its outline with a cross through it; flexible duct dashed. Where a duct keeps a fire-separating
/// wall with a damper, the damper is marked across it in the wall.
/// </summary>
public sealed partial class PlanRenderer
{
    private void DrawDucts(DrawingContext dc)
    {
        foreach (var duct in OnActiveLevel<Duct>()) Styled(dc, duct, d => DrawDuct(d, duct));
    }

    private void DrawDuct(DrawingContext dc, Duct duct)
    {
        var colour = duct.Kind.Colour();
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        var wash = new SolidColorBrush(Color.FromArgb(0x40, colour.R, colour.G, colour.B));
        wash.Freeze();
        var selected = IsSelected(duct);
        var edge = new Pen(selected ? _selectedPen.Brush : brush, Page(1.2));
        if (Document!.FindType<DuctType>(duct.TypeId)?.Material == DuctMaterial.Flexible) edge.DashStyle = DashStyles.Dash;
        var size = duct.Size(Document!);
        var half = size.Width / 2;

        if (duct.Upright)
        {
            var centre = duct.Start;
            var outline = size.Shape == DuctShape.Round
                ? Enumerable.Range(0, 24).Select(i => centre + new Vector2D(Math.Cos(i * Math.PI / 12), Math.Sin(i * Math.PI / 12)) * half).ToList()
                : new List<Point2D> { centre + new Vector2D(-half, -size.Height / 2), centre + new Vector2D(half, -size.Height / 2), centre + new Vector2D(half, size.Height / 2), centre + new Vector2D(-half, size.Height / 2) };
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(ModelToScreen(outline[0]), true, true);
                context.PolyLineTo(outline.Skip(1).Select(ModelToScreen).ToList(), true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(wash, edge, geometry);
            dc.DrawLine(edge, ModelToScreen(centre + new Vector2D(-half, -half) * 0.7), ModelToScreen(centre + new Vector2D(half, half) * 0.7));
            dc.DrawLine(edge, ModelToScreen(centre + new Vector2D(-half, half) * 0.7), ModelToScreen(centre + new Vector2D(half, -half) * 0.7));
            return;
        }

        var along = (duct.End - duct.Start).NormalisedOrDefault(Vector2D.UnitX);
        var across = new Vector2D(-along.Y, along.X) * half;
        var corners = new[] { duct.Start + across, duct.End + across, duct.End - across, duct.Start - across };
        var band = new StreamGeometry();
        using (var context = band.Open())
        {
            context.BeginFigure(ModelToScreen(corners[0]), true, true);
            context.PolyLineTo(corners.Skip(1).Select(ModelToScreen).ToList(), false, false);
        }

        band.Freeze();
        dc.DrawGeometry(wash, null, band);
        dc.DrawLine(edge, ModelToScreen(corners[0]), ModelToScreen(corners[1]));
        dc.DrawLine(edge, ModelToScreen(corners[3]), ModelToScreen(corners[2]));

        // Its insulation, a dashed line round it as thick as it is.
        if (duct.Insulation > 0)
        {
            var wrap = new Vector2D(-along.Y, along.X) * (half + duct.Insulation);
            var lagging = new Pen(brush, Page(0.8)) { DashStyle = DashStyles.Dash };
            dc.DrawLine(lagging, ModelToScreen(duct.Start + wrap), ModelToScreen(duct.End + wrap));
            dc.DrawLine(lagging, ModelToScreen(duct.Start - wrap), ModelToScreen(duct.End - wrap));
        }

        // A damper where it passes a fire-separating wall: a line across it and a ring.
        if (duct.FireProtection is FireProtection.FireDamper or FireProtection.FireAndSmokeDamper)
            foreach (var (_, at) in DuctNetworks.Crossings(Document!, duct).Where(crossing => Document!.GetWallType(crossing.Wall)?.Separating == true))
            {
                var mark = new Pen(brush, Page(1.6));
                dc.DrawLine(mark, ModelToScreen(at + across * 1.2), ModelToScreen(at - across * 1.2));
                dc.DrawEllipse(null, mark, ModelToScreen(at), Math.Max(Page(3), half * 0.5 * PixelsPerMm), Math.Max(Page(3), half * 0.5 * PixelsPerMm));
            }
    }
}
