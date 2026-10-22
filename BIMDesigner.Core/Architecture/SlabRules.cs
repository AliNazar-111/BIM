using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What the rules ask of a floor and a roof.
///
/// The heat they let out: Approved Document L limits a new dwelling's ground floor to a U-value of
/// 0.18 W/m²K and its roof to 0.16 (Table 4.1). A ground floor's is worked out as BS EN ISO 13370
/// works it, the ground under it counted - by the floor's area against its exposed perimeter and
/// its own build-up's resistance; a roof's from its layers and those of any ceiling under it with
/// insulation laid on it, as a cold loft's is. Between storeys, a floor keeps sound in to Rw 40
/// (Approved Document E, Table 0.2) and resists fire for 30 minutes (Approved Document B, Table B2).
/// A pitched roof is pitched as its covering needs: natural slate 20 degrees at the least, plain
/// tiles 35, interlocking tiles 15 (BS 5534).
/// </summary>
public static class SlabRules
{
    /// <summary>The ground's conductivity under a floor, W/mK: clay or silt, as BS EN ISO 13370 takes it where it is not known.</summary>
    public const double Ground = 1.5;

    /// <summary>The resistance of a build-up's layers, m²K/W, without its surfaces'; null where a material's conductivity is not known.</summary>
    public static double? LayerResistance(BimDocument document, Materials.CompoundStructure structure) =>
        Thermal.UValue(document, structure, Thermal.Flow.Downward) is { } u ? 1 / u - 0.17 - 0.04 : null;

    /// <summary>
    /// A ground floor's U-value, W/m²K, by BS EN ISO 13370: its area (m²) against half its exposed
    /// perimeter (m) - its characteristic dimension B' - and its equivalent thickness, the walls'
    /// 300 and the ground's conductivity times its resistances.
    /// </summary>
    public static double GroundFloorU(double layers, double area, double perimeter, double wall = 0.3)
    {
        if (area <= 0 || perimeter <= 0) return double.NaN;

        var characteristic = area / (0.5 * perimeter);
        var equivalent = wall + Ground * (0.17 + layers + 0.04);
        return equivalent < characteristic
            ? 2 * Ground / (Math.PI * characteristic + equivalent) * Math.Log(Math.PI * characteristic / equivalent + 1)
            : Ground / (0.457 * characteristic + equivalent);
    }

    /// <summary>Whether a floor is on the ground: on the lowest storey.</summary>
    public static bool OnTheGround(BimDocument document, Slab slab) =>
        slab is Floor && document.Levels.Count > 0 && document.FindLevel(slab.LevelId) is { } level
        && level.Elevation <= document.Levels.Min(other => other.Elevation) + 1;

    /// <summary>A floor's or roof's U-value, as the rules work it out; null where its layers' materials are not known.</summary>
    public static double? UValue(BimDocument document, Slab slab)
    {
        if (document.FindType<SlabType>(slab.TypeId) is not { } type) return null;

        if (slab is Floor)
            return OnTheGround(document, slab) && LayerResistance(document, type.Structure) is { } layers
                ? GroundFloorU(layers, slab.Area / 1e6, slab.Perimeter / 1000) is var u && double.IsFinite(u) ? u : null
                : null;

        if (slab is not Roof roof || Thermal.UValue(document, type.Structure, Thermal.Flow.Upward) is not { } own) return null;

        // A ceiling under it, its insulation on it: the loft's ceiling is what keeps the heat in.
        var middle = Polygon2D.Centroid(roof.Boundary, roof.Boundary[0]);
        var under = document.All<Ceiling>()
            .Where(ceiling => ceiling.Contains(middle) && ceiling.GetBottomElevation(document) < roof.GetBottomElevation(document) + 1)
            .Select(ceiling => document.FindType<SlabType>(ceiling.TypeId))
            .Select(ceilingType => ceilingType is null ? null : Thermal.UValue(document, ceilingType.Structure, Thermal.Flow.Upward) is { } u ? 1 / u - 0.10 - 0.04 : (double?)null)
            .Max();
        return 1 / (1 / own + (under ?? 0));
    }

    /// <summary>
    /// Whether a roof is a cold roof: its own build-up keeping little heat in, and a ceiling under
    /// it with its insulation on it - the loft between to be aired.
    /// </summary>
    public static bool Cold(BimDocument document, Roof roof) =>
        document.FindType<SlabType>(roof.TypeId) is { } type
        && Thermal.UValue(document, type.Structure, Thermal.Flow.Upward) is { } own && own > 0.5
        && UValue(document, roof) is { } whole && whole < own / 2;

    /// <summary>How far a roof spans between its eaves, mm: its outline's width across its own square.</summary>
    public static double Span(Roof roof)
    {
        if (roof.Boundary.Count < 3) return 0;
        var (along, across) = RoofDrainage.Axes(roof);
        var widths = new[] { along, across }.Select(way => roof.Boundary.Max(point => (point - roof.Boundary[0]).Dot(way)) - roof.Boundary.Min(point => (point - roof.Boundary[0]).Dot(way)));
        return widths.Min();
    }

    /// <summary>The least pitch a roof's covering is laid at, degrees, by what its top layer is made of; null for one not known.</summary>
    public static (double Pitch, string Covering)? CoveringPitch(BimDocument document, RoofType type)
    {
        if (type.Structure.Layers.Count == 0 || document.FindMaterial(type.Structure.Layers[0].MaterialId)?.Name is not { } name) return null;
        if (name.Contains("slate", StringComparison.OrdinalIgnoreCase)) return (20, "natural slate");
        if (name.Contains("tile", StringComparison.OrdinalIgnoreCase) && name.Contains("plain", StringComparison.OrdinalIgnoreCase)) return (35, "plain tiles");
        if (name.Contains("tile", StringComparison.OrdinalIgnoreCase)) return (15, "interlocking tiles");
        return null;
    }

    /// <summary>
    /// The least slope the IRC lays a roof's covering at, as a rise in 12 and in degrees, by its top
    /// layer: slate 4 in 12 (R905.6.2), clay or concrete tile 2½ (R905.3.2), wood shingles and
    /// shakes 3 (R905.7.1, R905.8.1), asphalt shingles 2 (R905.2.2).
    /// </summary>
    public static (double Rise, string Covering, string Section)? IrcCoveringSlope(BimDocument document, RoofType type)
    {
        if (type.Structure.Layers.Count == 0 || document.FindMaterial(type.Structure.Layers[0].MaterialId)?.Name is not { } name) return null;
        bool Has(string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (Has("slate")) return (4, "slate", "R905.6.2");
        if (Has("tile")) return (2.5, "clay or concrete tile", "R905.3.2");
        if (Has("shake") || Has("shingle") && (Has("wood") || Has("cedar"))) return (3, "wood shingles or shakes", "R905.7.1");
        if (Has("shingle")) return (2, "asphalt shingles", "R905.2.2");
        return null;
    }

    /// <summary>What a floor or roof falls short of, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Slab slab, ClearanceStandard standard)
    {
        if (slab is Ceiling || document.FindType<SlabType>(slab.TypeId) is not { } type || slab.Boundary.Count < 3) return Array.Empty<PlacementProblem>();

        var problems = new List<PlacementProblem>();
        void Add(string said, string rule) => problems.Add(new PlacementProblem(slab, said, rule, slab.Boundary));

        if (slab is Roof walledIn && RoofDrainage.OverflowProblem(document, walledIn, standard.IsUs()) is { } overflow)
            Add("its parapets shut it in, with one drain and no way out through its edge for the water if that drain blocks.", overflow);

        if (standard.IsUs())
        {
            if (slab is Roof { SlopeDegrees: > 0.5 } sloped && document.FindType<RoofType>(sloped.TypeId) is { } usType && IrcCoveringSlope(document, usType) is var (rise, usCovering, section)
                && Math.Tan(sloped.SlopeDegrees * Math.PI / 180) * 12 < rise - 0.01)
                Add($"it is pitched {Math.Tan(sloped.SlopeDegrees * Math.PI / 180) * 12:0.#} in 12, too flat for {usCovering}.", $"The IRC ({section}) lays {usCovering} at {rise:0.#} in 12 at the least.");
            return problems;
        }

        if (slab is Floor && OnTheGround(document, slab) && UValue(document, slab) is { } floorU && floorU > Thermal.FloorLimit + 0.005)
            Add($"its U-value is {floorU:0.00}, the ground under it counted.", "Approved Document L (Table 4.1) limits a new dwelling's ground floor to a U-value of 0.18 W/m²K.");

        if (slab is Floor && !OnTheGround(document, slab))
        {
            if (type.AcousticRating > 0 && type.AcousticRating < 40 - 0.05)
                Add($"it keeps sound in to Rw {type.AcousticRating:0} dB.", "Approved Document E (Table 0.2) asks for Rw 40 dB from a floor between the storeys of a house.");
            if (Doors.FireMinutes(type.FireRating) < 30)
                Add($"it resists fire for {(Doors.FireMinutes(type.FireRating) is > 0 and var minutes ? $"{minutes} minutes" : "no time stated")}.",
                    "Approved Document B (Table B2) asks for 30 minutes from the floors of a house.");
        }

        if (slab is Roof roof)
        {
            if (UValue(document, roof) is { } roofU && roofU > Thermal.RoofLimit + 0.005)
                Add($"its U-value is {roofU:0.00}{(document.All<Ceiling>().Any() ? ", with the ceiling under it" : string.Empty)}.",
                    "Approved Document L (Table 4.1) limits a new dwelling's roof to a U-value of 0.16 W/m²K - with a cold loft, its ceiling's insulation counted.");

            // A cold roof pitched steeply or spanning far is aired high up as well as at its eaves.
            if (Cold(document, roof) && !roof.HighLevelVents && (roof.SlopeDegrees > 35 || Span(roof) > 10000))
                Add($"it is a cold roof {(roof.SlopeDegrees > 35 ? $"pitched {roof.SlopeDegrees:0.#}°" : $"spanning {Span(roof) / 1000:0.0} m")}, aired at its eaves only.",
                    "BS 5250 asks a cold roof pitched over 35° or spanning more than 10 m for air high up as well - 5,000 mm² a metre at the ridge; tick Vented at the Ridge.");

            if (roof.SlopeDegrees > 0.5 && document.FindType<RoofType>(roof.TypeId) is { } roofType && CoveringPitch(document, roofType) is var (least, covering) && roof.SlopeDegrees < least - 0.05)
                Add($"it is pitched {roof.SlopeDegrees:0.#}°, too flat for {covering}.", $"BS 5534 lays {covering} at {least:0}° at the least, to shed the rain.");
        }

        return problems;
    }

    /// <summary>What a floor or roof comes to as its rules see it.</summary>
    public static string Verdict(BimDocument document, Slab slab)
    {
        var problems = Problems(document, slab, document.ProjectInformation.ClearanceStandard);
        var u = UValue(document, slab) is { } value ? $"U {value:0.00} W/m²K" : slab is Floor ? "Between storeys" : "U not known";
        return problems.Count == 0 ? u : $"{u} - {string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"))}";
    }

    /// <summary>Every floor's and roof's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Slab>()
            .Where(slab => slab.LevelId == levelId && slab is Floor or Roof)
            .SelectMany(slab => Problems(document, slab, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(slab, "its build-up", problem.Outline, problem.Rule), string.Empty,
                    document.FindType<SlabType>(slab.TypeId)?.Name ?? "A floor", problem.Said)))
            .ToList();
}
