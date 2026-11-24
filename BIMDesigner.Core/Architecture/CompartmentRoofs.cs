using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Where a compartment wall meets the roof (round 134's leftover), by Approved Document B Volume 1 5.10 to 5.15 and Diagram 5.2, and Volume 2
/// 8.18 to 8.21 and 8.25 to 8.29 and Diagram 8.2.
///
/// A compartment wall here: a wall common to two buildings - a party wall, its type Separating; one separating a flat from the rest of a block
/// (Volume 1 7.1); in a building other than a dwelling, one between separate occupancies (8.11e) or round a protected shaft (8.6). In the top
/// storey under a roof, the roof 1500 either side of it covered BROOF(t4) - its covering's designation as declared, or as its name has it
/// without testing - on a substrate or deck of class A2-s3, d2 or better: the layer under the covering (5.12, 8.26). A combustible substrate
/// and timber battens bedded over the wall are allowed in a building 15 m high at most (5.13) - in Volume 2 only of residential (other),
/// office or assembly use (8.27) - the building's height its highest roof's, midway between eaves and ridge, above the ground. Or instead the
/// wall carried up through the roof, 375 above the covering, 200 where the roofs either side differ by 375 or more or both are BROOF(t4) (5.15,
/// 8.29).
///
/// Not done: double-skinned insulated sheeting's band of A2-s3, d2 300 wide over the wall (5.14, 8.28), the roof's layers not saying a sheet
/// is double-skinned; the wall continued across the eaves (5.11b, 8.25b); roof members through the wall protected 1500 either side (8.16).
/// </summary>
public static class CompartmentRoofs
{
    /// <summary>The zone either side of the wall, the wall carried up through the roof and where the roofs differ, the building's height for 5.13 and 8.27, mm.</summary>
    public const double Zone = 1500, Upstand = 375, LowUpstand = 200, Step = 375, Low = 15000;

    /// <summary>Why a wall is a compartment wall that meets the roof: said; null where it is none.</summary>
    public static string? Why(BimDocument document, Wall wall)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || document.GetWallType(wall) is not { } type) return null;
        if (type.Separating) return "a wall common to two buildings";
        if (FireSeparation.Compartment(document, wall)) return "a compartment wall separating a flat";
        if (!HorizontalEscape.Applies(document) || type.Function != WallFunction.Interior) return null;
        var (one, other) = WallRules.RoomsBeside(document, wall);
        if (one is null || other is null || one == other) return null;
        if (FireCompartments.Separate(document, one, other)) return "a compartment wall between separate occupancies";
        if (!FireCompartments.ShaftLike(document, one) && !FireCompartments.ShaftLike(document, other)) return null;
        var shafts = FireCompartments.Shafts(document);
        return shafts.ContainsKey(one) != shafts.ContainsKey(other) ? "the enclosure of a protected shaft" : null;
    }

    /// <summary>A building's height for 5.13 and 8.27, mm: its highest roof's, midway between its lowest and highest points, above the ground.</summary>
    public static double Height(BimDocument document)
    {
        var ground = Escape.Ground(document);
        var roofs = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3).ToList();
        if (roofs.Count == 0) return FireDesign.HeightOf(document).TopFloor * 1000 + 3000;
        return roofs.Max(roof =>
        {
            var points = roof.Boundary.Append(new Point2D(roof.Boundary.Average(point => point.X), roof.Boundary.Average(point => point.Y))).ToList();
            var tops = points.Select(point => roof.TopAt(document, point)).ToList();
            return (tops.Min() + tops.Max()) / 2;
        }) - ground;
    }

    /// <summary>
    /// How high a wall is carried: its own top, or the top of the walls on its line standing on it - a parapet the Wall tool splits off where it
    /// runs up through a roof stands on the roof's top - mm.
    /// </summary>
    private static double Carried(BimDocument document, Wall wall)
    {
        var top = wall.GetTopElevation(document);
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var length = wall.Start.DistanceTo(wall.End);
        var line = document.Walls.Where(other => other != wall && Math.Abs(way.Cross((other.End - other.Start).NormalisedOrDefault(way))) < 0.02
                                                 && Math.Abs(way.Cross(other.Start.MidpointTo(other.End) - wall.Start)) < 50
                                                 && (other.Start.MidpointTo(other.End) - wall.Start).Dot(way) is var along && along > 0 && along < length).ToList();
        for (var round = 0; round < 10; round++)
        {
            var higher = line.Where(other => other.GetBaseElevation(document) <= top + 600 && other.GetTopElevation(document) > top + 1).Select(other => other.GetTopElevation(document)).DefaultIfEmpty(top).Max();
            if (higher <= top + 1) break;
            top = higher;
        }

        return top;
    }

    /// <summary>A roof type's covering and the layer under it - its substrate or deck - each with its material; nulls where there are none.</summary>
    private static (MaterialLayer Layer, Material Material)? Layer(BimDocument document, Roof roof, int which)
    {
        if (document.FindType<SlabType>(roof.TypeId) is not { } type) return null;
        var layers = type.Structure.Layers.Where(layer => layer.Function != LayerFunction.Membrane && layer.Thickness > 0)
            .Select(layer => (Layer: layer, Material: document.FindMaterial(layer.MaterialId))).Where(entry => entry.Material is not null).ToList();
        return layers.Count > which ? (layers[which].Layer, layers[which].Material!) : null;
    }

    /// <summary>What the roof over a compartment wall falls short of where they meet (5.12 to 5.15, 8.26 to 8.29), said.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (Why(document, wall) is not { } why) yield break;
        var volume2 = HorizontalEscape.Applies(document);
        var floor = document.FindLevel(wall.LevelId)?.Elevation ?? 0;
        var top = Carried(document, wall);
        var half = type.Structure.TotalWidth / 2;
        var middle = wall.Start.MidpointTo(wall.End);
        var across = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();

        // The roof over each side within the zone - the storey's own, its underside over the wall's floor.
        (Roof Roof, double Top)? Over(Point2D at)
        {
            var roofs = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3 && roof.Contains(at)).Select(roof => (Roof: roof, Under: roof.UndersideAt(document, at)))
                .Where(entry => entry.Under > floor + 1 && entry.Under < floor + 8000).OrderBy(entry => entry.Under).ToList();
            return roofs.Count == 0 ? null : (roofs[0].Roof, roofs[0].Roof.TopAt(document, at));
        }

        var sides = new[] { middle + across * (half + Zone / 2), middle - across * (half + Zone / 2) }.Select(Over).ToList();
        var roofed = sides.OfType<(Roof Roof, double Top)>().ToList();
        if (roofed.Count == 0) yield break;
        if (document.All<Floor>().Any(slab => slab.Boundary.Count >= 3 && slab.Contains(middle) && slab.GetBottomElevation(document) > floor + 1 && slab.GetBottomElevation(document) < roofed.Min(side => side.Top))) yield break;

        // Carried up through the roof: 375 over the covering, 200 where the roofs differ by 375 or both are BROOF(t4) (5.15, 8.29).
        RoofDesignation? Designation(Roof roof) => Layer(document, roof, 0) is { } entry && RoofDesignation.Of(entry.Material, entry.Layer.Thickness) is { } known ? known.Designation : null;
        var bothB = roofed.Count == 2 && roofed.All(side => Designation(side.Roof) is { Rank: 0 });
        var step = roofed.Count == 2 ? Math.Abs(roofed[0].Top - roofed[1].Top) : 0;
        var upstand = top - roofed.Max(side => side.Top);
        var asked = step >= Step - 0.5 || bothB ? LowUpstand : Upstand;
        if (upstand >= asked - 0.5) yield break;

        // Else the roof 1500 either side: BROOF(t4) on a substrate or deck of A2-s3, d2 - a combustible one allowed in a low building (5.13, 8.27).
        var group = volume2 ? HorizontalEscape.Purpose(document) : PurposeGroup.ResidentialOther;
        var height = Height(document);
        var exempt = height <= Low + 0.5 && (!volume2 || group is PurposeGroup.ResidentialOther or PurposeGroup.Office or PurposeGroup.AssemblyAndRecreation);
        var wanting = new List<string>();
        foreach (var (roof, _) in roofed.DistinctBy(side => side.Roof))
        {
            if (Layer(document, roof, 0) is not { } coveringEntry)
            {
                wanting.Add($"the {document.FindType<SlabType>(roof.TypeId)?.Name ?? "roof"} has no covering given");
                continue;
            }

            var covering = coveringEntry.Material;
            if (RoofDesignation.Of(covering, coveringEntry.Layer.Thickness) is not { } known)
                wanting.Add($"its covering, the {covering.Name}, has no external fire designation given");
            else if (known.Designation.Rank > 0)
                wanting.Add($"its covering, the {covering.Name}, is {known.Designation}");
            if (exempt) continue;
            if (Layer(document, roof, 1) is not { } under)
                continue;
            var substrate = under.Material;
            if (Euroclass.Of(substrate) is not { } fire)
                wanting.Add($"the {substrate.Name} under the covering is of no reaction to fire class given");
            else if (!fire.Class.AtLeast(Euroclass.A2s3d2))
                wanting.Add($"the {substrate.Name} under the covering is class {fire.Class}");
        }

        if (wanting.Count == 0) yield break;
        var highNote = exempt ? string.Empty : volume2 && height <= Low + 0.5 ? $", {HorizontalEscape.Said(group)} not being allowed a combustible substrate" : $", the building {height / 1000:0.0} m high";
        yield return ($"it is {why} under the roof, and {string.Join("; ", wanting.Distinct())}{highNote}.", volume2
            ? "Approved Document B Volume 2 (8.26 to 8.29, Diagram 8.2) asks the roof 1500 either side of a compartment wall covered BROOF(t4) on a substrate or deck of class A2-s3, d2 or better - a combustible substrate and timber battens bedded over the wall only in a building 15 m high at most of residential (other), office or assembly and recreation use (8.27) - or the wall carried up through the roof 375 above the covering, 200 where the roofs differ by 375 or more or both are BROOF(t4) (8.29)."
            : "Approved Document B (5.12 to 5.15, Diagram 5.2) asks the roof 1500 either side of a compartment wall covered BROOF(t4) on a substrate or deck of class A2-s3, d2 or better - a combustible substrate and timber battens bedded over the wall in a building 15 m high at most (5.13) - or the wall carried up through the roof 375 above the covering, 200 where the roofs differ by 375 or more or both are BROOF(t4) (5.15).");
    }
}
