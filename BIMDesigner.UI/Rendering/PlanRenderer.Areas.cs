using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// An area plan as the view draws it: the storey's measured outline filled in the scheme's colour
/// and edged heavily, what is left out hatched over it with why, what is said beside it dashed
/// where it stops, and the storey's area on it - over the plan, which stays as it is under it.
/// </summary>
public sealed partial class PlanRenderer
{
    /// <summary>The scheme an area plan is measured in; null on any other plan.</summary>
    private AreaScheme? AreaShown => Drafting ? null : AreaPlans.Of(View);

    private static Color AreaColour(AreaScheme scheme) => scheme switch
    {
        AreaScheme.GrossExternal => Color.FromRgb(0xE0, 0x8A, 0x2E),
        AreaScheme.GrossInternal => Color.FromRgb(0x2E, 0x7D, 0xD1),
        AreaScheme.NetInternal => Color.FromRgb(0x3A, 0xA1, 0x5A),
        _ => Color.FromRgb(0x7A, 0x5C, 0xC8)
    };

    private void DrawAreas(DrawingContext dc, AreaScheme scheme)
    {
        if (Document is null) return;
        var measure = AreaPlans.Measure(Document, ActiveLevelId, scheme);
        var colour = AreaColour(scheme);
        var edge = new Pen(Brush(colour), Math.Max(1.2, 0.5 * PixelsPerPaperMm)) { LineJoin = PenLineJoin.Miter };
        edge.Freeze();

        foreach (var outline in measure.Outlines.Where(outline => outline.Count >= 3))
            dc.DrawGeometry(Brush(Color.FromArgb(0x38, colour.R, colour.G, colour.B)), edge, BuildOutline(outline));

        // What is left out, hatched over the fill and outlined; what is said beside it, dashed where it stops.
        var ink = _ink.WallOutline;
        var hatch = DetailPen(DetailLineStyle.Thin, Color.FromArgb(0xB0, ink.R, ink.G, ink.B));
        var dashed = DetailPen(DetailLineStyle.Hidden, colour);
        foreach (var part in measure.Parts)
        {
            if (part.Outline is { Count: >= 3 } outline)
            {
                var geometry = BuildOutline(outline);
                dc.DrawGeometry(Brush(Color.FromArgb(part.Kind == AreaPartKind.Excluded ? (byte)0xB0 : (byte)0x40, 0xFF, 0xFF, 0xFF)), null, geometry);
                foreach (var (from, to) in Detailing.Hatch(outline, 45, OnPaperAsModel(2)))
                    dc.DrawLine(hatch, ModelToScreen(from), ModelToScreen(to));
                dc.DrawGeometry(null, DetailPen(DetailLineStyle.Thin, ink), geometry);
                Label(dc, Polygon2D.Centroid(outline, outline[0]), part.Label.Length > 0 ? part.Label : Short(part.Said), AreaPlans.Said(scheme, part.Area), Brush(ink), bold: false);
            }

            foreach (var (from, to) in part.Lines) dc.DrawLine(dashed, ModelToScreen(from), ModelToScreen(to));
        }

        // The storey's area, on its largest outline - in the largest room in it that counts, clear of the walls between them.
        if (measure.Outlines.OrderByDescending(Polygon2D.Area).FirstOrDefault() is { Count: >= 3 } largest)
        {
            var excluded = measure.Parts.Where(part => part.Outline is { Count: >= 3 }).Select(part => part.Outline!).ToList();
            var at = Document.All<Core.Architecture.Room>()
                .Where(room => room.LevelId == ActiveLevelId && Polygon2D.Contains(largest, room.Location) && !excluded.Any(part => Polygon2D.Contains(part, room.Location)))
                .Select(room => room.GetBoundary(Document))
                .Where(boundary => boundary.IsEnclosed)
                .OrderByDescending(boundary => boundary.Area)
                .Select(boundary => (Point2D?)boundary.Centroid)
                .FirstOrDefault() ?? Polygon2D.Centroid(largest, largest[0]);
            var level = Document.FindLevel(ActiveLevelId)?.Name ?? string.Empty;
            Label(dc, at, $"{level} {AreaPlans.Short(Document, scheme)}", AreaPlans.Said(scheme, measure.Area) + (measure.BelowGrade && scheme == AreaScheme.Finished ? ", below grade" : string.Empty), Brush(colour), bold: true);
        }
    }

    /// <summary>A part's reason, short enough to sit in it: before its clause, and without the room it is in.</summary>
    private static string Short(string said)
    {
        var reason = said.Contains(": ") ? said[(said.IndexOf(": ", StringComparison.Ordinal) + 2)..] : said;
        var clause = reason.IndexOf(" (", StringComparison.Ordinal);
        var cut = clause > 0 ? reason[..clause] : reason;
        return cut.Length > 0 ? char.ToUpper(cut[0]) + cut[1..] : cut;
    }

    /// <summary>Two lines on a backdrop, centred on a point: what it is, and its area.</summary>
    private void Label(DrawingContext dc, Point2D at, string heading, string detail, Brush brush, bool bold)
    {
        var top = Text(heading, Page(bold ? 13 : 10), brush, bold);
        var bottom = Text(detail, Page(bold ? 12 : 10), brush, bold);
        var centre = ModelToScreen(at);
        var width = Math.Max(top.Width, bottom.Width);
        var height = top.Height + bottom.Height;
        dc.DrawRoundedRectangle(_labelBackdrop, null, new Rect(centre.X - width / 2 - Page(5), centre.Y - height / 2 - Page(3), width + Page(10), height + Page(6)), Page(3), Page(3));
        dc.DrawText(top, new Point(centre.X - top.Width / 2, centre.Y - height / 2));
        dc.DrawText(bottom, new Point(centre.X - bottom.Width / 2, centre.Y - height / 2 + top.Height));
    }
}
