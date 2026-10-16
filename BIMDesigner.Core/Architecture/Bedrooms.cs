using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Whether a room is big enough for the beds in it. As the Nationally Described Space Standard
/// asks of a bedroom: a single 7.5 m² and 2.15 m wide, a double or twin 11.5 m² - one of them,
/// the principal bedroom, 2.75 m wide, the others 2.55. As Approved Document M asks of a
/// wheelchair user's dwelling (M4(3) 3.35): the principal bedroom 13.5 m² and 3 m wide, every
/// other double or twin 12.5 m² and 3 m, a single 8.5 m² and 2.4 m. Or as the IRC asks of any
/// habitable room (R304): 70 sq ft - 6.5 m² - and 7 ft (2134) across every way.
///
/// A room is a bedroom by what is in it: a double bed or two singles - a bunk bed is two - make
/// it a double or twin, one single bed a single; the principal is the one the principal bed is in.
/// </summary>
public static class Bedrooms
{
    /// <summary>What a bedroom is, by its beds.</summary>
    public enum Kind { Single, Double, Principal }

    /// <summary>What a room is as a bedroom, or null for one with no bed in it.</summary>
    public static Kind? Of(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var beds = document.Elements.OfType<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(outline, piece.Location))
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type?.Form is ComponentForm.Bed or ComponentForm.BunkBed)
            .ToList();
        if (beds.Count == 0) return null;

        var principal = Clearances.PrincipalBed(document);
        if (beds.Any(entry => entry.Piece == principal)) return Kind.Principal;

        var bedspaces = beds.Sum(entry => entry.Type!.Form == ComponentForm.BunkBed || entry.Type.Width >= 1200 ? 2 : 1);
        return bedspaces >= 2 ? Kind.Double : Kind.Single;
    }

    /// <summary>The least floor area and width a bedroom of a kind needs, m² and mm, and whose rule asks for them.</summary>
    public static (double Area, double Width, string Rule) Needs(Kind kind, ClearanceStandard standard) => standard switch
    {
        _ when standard.IsUs() => (6.5, 2134, "IRC R304 asks for 70 sq ft (6.5 m²) and 7 ft (2134) across every way"),
        ClearanceStandard.ApprovedDocumentM3 => kind switch
        {
            Kind.Principal => (13.5, 3000, "Approved Document M (M4(3)) 3.35d asks for 13.5 m² and 3 m wide for the principal bedroom"),
            Kind.Double => (12.5, 3000, "Approved Document M (M4(3)) 3.35f asks for 12.5 m² and 3 m wide for every other double or twin bedroom"),
            _ => (8.5, 2400, "Approved Document M (M4(3)) 3.35i asks for 8.5 m² and 2.4 m wide for a single bedroom")
        },
        _ => kind switch
        {
            Kind.Principal => (11.5, 2750, "The Nationally Described Space Standard asks for 11.5 m² for a double bedroom, and 2.75 m wide for one of them"),
            Kind.Double => (11.5, 2550, "The Nationally Described Space Standard asks for 11.5 m² and 2.55 m wide for a double or twin bedroom"),
            _ => (7.5, 2150, "The Nationally Described Space Standard asks for 7.5 m² and 2.15 m wide for a single bedroom")
        }
    };

    /// <summary>
    /// What a room comes to as a bedroom - "Double bedroom: 12.6 m², 3.10 m wide - big enough" or
    /// what it is short of - or null for a room with no bed in it, or not enclosed.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;
        if (Of(document, room, boundary.Polygon) is not { } kind) return null;

        var standard = document.ProjectInformation.ClearanceStandard;
        var (area, width, rule) = Needs(kind, standard);
        var (hasArea, hasWidth) = (boundary.Area / 1e6, Narrowest(boundary.Polygon));
        var what = kind switch { Kind.Principal => "Principal bedroom", Kind.Double => "Double bedroom", _ => "Single bedroom" };
        var measured = $"{what}: {hasArea:0.0} m², {hasWidth / 1000:0.00} m wide";

        var shortOf = new List<string>();
        if (hasArea < area - 0.005) shortOf.Add($"{area - hasArea:0.0} m² too small");
        if (hasWidth < width - 0.5) shortOf.Add($"{(width - hasWidth) / 1000:0.00} m too narrow");
        return shortOf.Count == 0 ? $"{measured} - big enough" : $"{measured} - {string.Join(" and ", shortOf)}: {rule}.";
    }

    /// <summary>
    /// How wide a room is at its narrowest, as its outline is measured across: the least distance
    /// between two parallel lines either side of it.
    /// </summary>
    public static double Narrowest(IReadOnlyList<Point2D> outline)
    {
        var hull = ComponentMeshes.ConvexHull(outline);
        if (hull.Count < 3) return 0;

        var narrowest = double.MaxValue;
        for (var i = 0; i < hull.Count; i++)
        {
            var (a, b) = (hull[i], hull[(i + 1) % hull.Count]);
            var edge = b - a;
            if (edge.Length < 1e-6) continue;
            var normal = edge.PerpendicularLeft() / edge.Length;
            narrowest = Math.Min(narrowest, hull.Max(point => Math.Abs((point - a).Dot(normal))));
        }

        return narrowest;
    }
}
