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

        // Split by hand into areas, each says its own; the storey's is in the schedule.
        DrawHandAreas(dc, scheme);
        if (Areas.On(Document, ActiveLevelId, scheme).Any()) return;

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

    /// <summary>The colour an area drawn by hand is filled in: by its use on a rentable plan, by its number otherwise.</summary>
    private static Color HandAreaColour(Area area, int index) => area.Use switch
    {
        AreaUse.Office => Color.FromRgb(0x4C, 0x9F, 0xE0),
        AreaUse.Retail => Color.FromRgb(0x2F, 0xB5, 0xA4),
        AreaUse.TenantStorage => Color.FromRgb(0x8E, 0x9B, 0xAE),
        AreaUse.Amenity => Color.FromRgb(0xE5, 0xA9, 0x3B),
        AreaUse.Lobby or AreaUse.Corridor => Color.FromRgb(0xF0, 0xD4, 0x5A),
        AreaUse.Plant => Color.FromRgb(0xC2, 0x6B, 0x4A),
        AreaUse.Toilets => Color.FromRgb(0x9B, 0x7B, 0xD4),
        AreaUse.Stair or AreaUse.Lift or AreaUse.Shaft => Color.FromRgb(0x6F, 0x6F, 0x6F),
        AreaUse.Parking => Color.FromRgb(0xA8, 0xB8, 0x6C),
        _ => Palette[index % Palette.Length]
    };

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4C, 0x9F, 0xE0), Color.FromRgb(0xE5, 0x8E, 0x3B), Color.FromRgb(0x5B, 0xB5, 0x6A), Color.FromRgb(0xC5, 0x6B, 0xB8),
        Color.FromRgb(0xD9, 0xC2, 0x3E), Color.FromRgb(0x4F, 0xB8, 0xC0), Color.FromRgb(0xD8, 0x6B, 0x6B), Color.FromRgb(0x8C, 0x7B, 0xD4)
    };

    /// <summary>The colour area boundaries are drawn in: violet, apart from the walls and the scheme's edge.</summary>
    private static readonly Color BoundaryColour = Color.FromRgb(0x8E, 0x3F, 0xC4);

    /// <summary>
    /// The areas a plan is split into by hand, each filled in its colour and labelled with its name,
    /// number, use and area - one nothing closes round marked where it was put, and why - and the
    /// area boundaries that split it over them.
    /// </summary>
    private void DrawHandAreas(DrawingContext dc, AreaScheme scheme)
    {
        if (Document is null) return;
        var areas = Areas.On(Document, ActiveLevelId, scheme).OrderBy(area => area.Number, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < areas.Count; i++)
        {
            var (area, index) = (areas[i], i);
            CadLayers.Draw(dc, area, d => DrawHandArea(d, area, index, scheme));
        }

        var pen = new Pen(Brush(BoundaryColour), Math.Max(1.4, 0.45 * PixelsPerPaperMm));
        pen.Freeze();
        foreach (var boundary in Areas.Boundaries(Document, ActiveLevelId, scheme))
            CadLayers.Draw(dc, boundary, d => d.DrawLine(IsSelected(boundary) ? _selectedPen : pen, ModelToScreen(boundary.Start), ModelToScreen(boundary.End)));
    }

    private void DrawHandArea(DrawingContext dc, Area area, int index, AreaScheme scheme)
    {
        var shape = Areas.Shape(Document!, area);
        var colour = HandAreaColour(area, index);
        var measured = shape.Polygon is { Count: >= 3 } && shape.Shared is null;
        if (measured)
            dc.DrawGeometry(Brush(Color.FromArgb(0x50, colour.R, colour.G, colour.B)), IsSelected(area) ? _selectedPen : null, BuildOutline(shape.Polygon!));

        var detail = measured
            ? AreaPlans.Said(scheme, shape.Area) + (scheme == AreaScheme.Rentable && area.Use != AreaUse.None ? $", {Areas.Name(area.Use).ToLowerInvariant()}" : string.Empty)
            : "Not enclosed";
        Label(dc, area.Location, Areas.Said(area), detail, Brush(measured ? _ink.WallOutline : Color.FromRgb(0xC0, 0x30, 0x30)), bold: false);
        if (IsSelected(area)) dc.DrawEllipse(null, _selectedPen, ModelToScreen(area.Location), Page(4), Page(4));
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
