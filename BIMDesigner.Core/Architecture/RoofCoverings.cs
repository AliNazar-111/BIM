using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How near the boundary a roof's covering may be, as Approved Document B Volume 1 (12.3, Table 12.1) has it, by its external fire designation
/// (BS EN 13501-5) - its outside layer's material's, its maker's or the one its name has without testing (<see cref="RoofDesignation.Typical"/>):
/// BROOF(t4) anywhere; CROOF(t4) 6 m or more from the relevant boundary; DROOF(t4) and EROOF(t4) 6 m to 12 m off only in parts of 3 m² at most
/// (note 3), 12 m off and further not on a terrace of three or more houses or a building of more than 1500 m³ (note 2) - EROOF(t4) not even 20 m
/// off on those; FROOF(t4) only 20 m or more off, and then only in small parts on a small building. The distance is the least from the roof's
/// outline in plan to the site's property line; nothing is asked without one. Not under the US's codes.
/// </summary>
public static class RoofCoverings
{
    /// <summary>Table 12.1's distances from the relevant boundary, mm.</summary>
    public const double Near = 6000, Middle = 12000, Far = 20000;

    /// <summary>Note 3's largest part of a roof, m²; note 2's largest building, m³.</summary>
    public const double SmallPart = 3, SmallBuilding = 1500;

    /// <summary>What a roof's covering falls short of: what, and the rule.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Roof roof)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || roof.Boundary.Count < 3 || document.FindType<SlabType>(roof.TypeId) is not { } type) yield break;
        var lines = document.All<PropertyLine>().Where(line => line.Boundary.Count >= 2).ToList();
        if (lines.Count == 0) yield break;
        var covering = type.Structure.Layers.Where(layer => layer.Function != LayerFunction.Membrane && layer.Thickness > 0)
            .Select(layer => (Layer: layer, Material: document.FindMaterial(layer.MaterialId))).FirstOrDefault(entry => entry.Material is not null);
        if (covering.Material is not { } material) yield break;

        var distance = lines.Min(line => Distance(roof.Boundary, line.Boundary));
        var band = distance < Near - 0.5 ? 0 : distance < Middle - 0.5 ? 1 : distance < Far - 0.5 ? 2 : 3;
        var off = band switch { 0 => "within 6 m of", 1 => "6 m to 12 m from", 2 => "12 m to 20 m from", _ => "20 m or more from" };
        var at = $", {Units.Metres(distance, "0.0")} from the boundary";
        if (RoofDesignation.Of(material, covering.Layer.Thickness) is not { } known)
        {
            if (band < 3)
                yield return ($"its covering, the {material.Name}, has no external fire designation given{at}.",
                    $"Approved Document B (12.3, Table 12.1) limits a roof's covering {off} the relevant boundary by its designation - {(band == 0 ? "BROOF(t4) only" : "BROOF(t4) or CROOF(t4), others only in part")}; give its maker's.");
            yield break;
        }

        var designation = known.Designation;
        var said = $"its covering, the {material.Name}, is {designation}{at}.";

        // Table 12.1, row by row: not at all; with note 2 - not on a terrace of three or more houses or a building over 1500 m³; with note 3 too -
        // only in parts of 3 m² at most.
        var (allowed, note2, note3) = (designation.Rank, band) switch
        {
            (0, _) => (true, false, false),
            (1, 0) => (false, false, false),
            (1, _) => (true, false, false),
            (2, 0) or (3, 0) => (false, false, false),
            (2, 1) or (3, 1) => (true, true, true),
            (2, 2) or (3, 2) => (true, true, false),
            (2, 3) => (true, false, false),
            (3, 3) => (true, true, false),
            (4, 3) => (true, true, true),
            _ => (false, false, false)
        };

        if (!allowed)
        {
            yield return (said, $"Approved Document B (12.3, Table 12.1) does not allow {Article(designation)} {designation} covering {off} the relevant boundary{(band == 0 ? " - BROOF(t4) only" : string.Empty)}.");
            yield break;
        }

        if (note2 && Large(document) is { } large)
            yield return (said, $"Approved Document B (Table 12.1, note 2) does not allow {Article(designation)} {designation} covering {off} the relevant boundary on {large}.");
        else if (note3 && roof.SlopingArea(document) / 1e6 > SmallPart + 1e-6)
            yield return (said, $"Approved Document B (Table 12.1, note 3) allows {Article(designation)} {designation} covering {off} the relevant boundary only in parts of 3 m² at most, 1.5 m apart, the roof between them class A2-s3, d2 or better - this roof is {Units.OfSquareMetres(roof.SlopingArea(document) / 1e6, "0.0")}.");
    }

    /// <summary>"an" before EROOF(t4) and FROOF(t4), said with a vowel; "a" before the rest.</summary>
    private static string Article(RoofDesignation designation) => designation.Rank >= 3 ? "an" : "a";

    /// <summary>Why a building is one note 2 keeps the lower coverings off: a terrace of three or more houses, or more than 1500 m³ by its rooms; null where neither.</summary>
    public static string? Large(BimDocument document)
    {
        var homes = document.All<Room>().Select(Dwellings.Of).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (homes >= 3 && FireDesign.UseOf(document) == BuildingUse.House) return $"a terrace of three or more houses - this one {homes}";
        var capacity = CubicCapacity(document);
        return capacity > SmallBuilding + 1e-6 ? $"a building of more than 1500 m³ - this one {capacity:0} m³ by its rooms" : null;
    }

    /// <summary>A building's cubic capacity, m³ (Diagram D2), taken from its rooms: each one's floor area by its height - walls and lofts left out.</summary>
    public static double CubicCapacity(BimDocument document) =>
        document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed).Sum(entry => entry.Boundary.Area / 1e6 * entry.Room.Height / 1000);

    /// <summary>The least distance in plan between an outline and a line round a site, mm: none where they cross.</summary>
    internal static double Distance(IReadOnlyList<Point2D> outline, IReadOnlyList<Point2D> line)
    {
        var least = double.PositiveInfinity;
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            for (var j = 0; j < line.Count; j++)
            {
                var (c, d) = (line[j], line[(j + 1) % line.Count]);
                if (Crosses(a, b, c, d)) return 0;
                least = Math.Min(least, new[] { Line2D.DistanceFromSegment(a, c, d), Line2D.DistanceFromSegment(b, c, d), Line2D.DistanceFromSegment(c, a, b), Line2D.DistanceFromSegment(d, a, b) }.Min());
            }
        }

        return least;
    }

    private static bool Crosses(Point2D p, Point2D q, Point2D r, Point2D s)
    {
        static double Cross(Point2D o, Point2D a, Point2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var (d1, d2, d3, d4) = (Cross(r, s, p), Cross(r, s, q), Cross(p, q, r), Cross(p, q, s));
        return (d1 > 1e-6 && d2 < -1e-6 || d1 < -1e-6 && d2 > 1e-6) && (d3 > 1e-6 && d4 < -1e-6 || d3 < -1e-6 && d4 > 1e-6);
    }
}
