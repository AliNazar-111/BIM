using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>A material change of use into homes, as regulation 5 of the Building Regulations 2010 has it (round 145).</summary>
public enum MaterialChange
{
    /// <summary>A new building, or no change of use.</summary>
    None,

    /// <summary>Regulation 5(a): used as a dwelling, where previously it was not - a barn, an office, a chapel made a house.</summary>
    ToDwelling,

    /// <summary>Regulation 5(b): contains a flat, where previously it did not - offices made flats.</summary>
    ToFlats,

    /// <summary>Regulation 5(g): contains more or fewer dwellings than it did - a house made flats.</summary>
    MoreOrFewerDwellings
}

/// <summary>
/// A building converted into homes in England - a material change of use under regulation 5(a), (b) or (g) of the Building Regulations 2010
/// (round 145), read from the regulations' and the approved documents' own text.
///
/// <b>What applies</b> (regulation 6(1)): in all cases B1, B2, B3, B4(2), B5, C2(c), F1, G1, G3(1) to (3), G4, G5, G6, H1, H6, J1 to J4, L1,
/// P1, S2 and T1 (a); B4(1) over 11 m (c); C1(2) (d); C2 (e); E1 to E3 (f); G2 and G3(4) (h); Q1 (j). Not A (structure), K (protection from
/// falling, collision and impact), M (access) or O (overheating) - nor B4(1) at 11 m or less. What already stands - an element of the Existing
/// phase, not demolished - is retained; anything of the New phase is building work, and meets the requirements for it (regulation 4) - a new
/// stair Part K's.
///
/// <b>Energy</b> (Approved Document L Volume 1, 11.5 to 11.7): a retained wall, roof or floor whose U-value is over Table 4.3's threshold -
/// 0.70, 0.35, 0.70 - upgraded to its improved value - 0.30 (0.55 filling a cavity), 0.16, 0.25 - or as near as is feasible with a 15 year
/// payback, 0.7 at worst (4.13); a retained window, roof window or door over 3.3, a rooflight over 3.8, replaced; a new or replacement element
/// to Table 4.2 - wall 0.18, roof 0.15, floor 0.18, window and door 1.4, rooflight 2.2; and its openings no more than 25% of the floor area
/// (11.7c).
///
/// <b>Sound</b> (Approved Document E, Section 4, Table 0.1a): walls and floors between homes formed by a change of use to DnT,w + Ctr 43 dB
/// and L'nT,w 64 dB - an existing wall like one of Section 2's as it is, or with wall treatment 1, independent panels of two layers of
/// plasterboard, 20 kg/m², 35 of mineral wool behind (4.22 to 4.24), on one side only of masonry 100 thick plastered both faces.
///
/// <b>Charging</b> (Approved Document S, S2, regulation 44E): a charge point for each dwelling with associated parking, no cable routes.
///
/// Not done: change of use of part of a building (6(2)); floor and stair treatments 1 and 2 (4.26 to 4.40); regulation 44E's conditions on the
/// car park's works and the incoming supply; the 15-year payback itself; C1(2)'s radon and contaminants.
/// </summary>
public static class Conversion
{
    /// <summary>Whether a project converts a building into homes in England.</summary>
    public static bool Applies(BimDocument document) =>
        !document.ProjectInformation.ClearanceStandard.IsUs() && document.ProjectInformation.MaterialChange != MaterialChange.None;

    /// <summary>Whether an element is retained in the conversion: of the Existing phase and not demolished.</summary>
    public static bool Retained(BimDocument document, Element element) =>
        Applies(document) && element.PhaseCreated == DesignPhase.Existing && element.PhaseDemolished is null;

    /// <summary>The case of regulation 5: "5(a)".</summary>
    public static string Case(MaterialChange change) => change switch
    {
        MaterialChange.ToFlats => "5(b)",
        MaterialChange.MoreOrFewerDwellings => "5(g)",
        _ => "5(a)"
    };

    /// <summary>How the change reads: "Used as a dwelling where it was not (5(a))".</summary>
    public static string Said(MaterialChange change) => change switch
    {
        MaterialChange.None => "None - a new building",
        MaterialChange.ToDwelling => "To a dwelling (regulation 5(a))",
        MaterialChange.ToFlats => "To flats (regulation 5(b))",
        _ => "More or fewer dwellings (regulation 5(g))"
    };

    /// <summary>What regulation 6(1) asks of the conversion, said for the project's Properties.</summary>
    public static string Requirements(BimDocument document)
    {
        if (!Applies(document)) return document.ProjectInformation.ClearanceStandard.IsUs() ? "Not under the US's codes" : "A new building: all of Schedule 1";
        var tall = FireDesign.HeightOf(document).TopFloor * 1000 + 3000 > 11000;
        return $"Regulation 6(1), a material change of use under regulation {Case(document.ProjectInformation.MaterialChange)}" +
               $"{(document.ProjectInformation.PreviousUse.Length > 0 ? $" from {document.ProjectInformation.PreviousUse.ToLowerInvariant()}" : string.Empty)}: " +
               $"B1, B2, B3, B4(2), B5{(tall ? ", B4(1) - over 11 m" : string.Empty)}, C1(2), C2, E1 to E3 (43 dB, 64 dB, Approved Document E Table 0.1a), F1, G1 to G6, H1, H6, " +
               "J1 to J4, L1 (Approved Document L 11.7), P1, Q1, S2, T1. Not A, K, M or O - what is retained needs no work for them; new work meets them as building work";
    }

    // --- Energy -----------------------------------------------------------------------------------

    /// <summary>A fabric element's kind, for Approved Document L Tables 4.2 and 4.3.</summary>
    public enum Fabric { Wall, Roof, Floor, Window, Door, Rooflight }

    /// <summary>
    /// What a conversion's fabric element with a U-value falls short of (11.7): retained, over Table 4.3's threshold or the 3.3 and 3.8 of a
    /// window, door or rooflight; new or replaced, over Table 4.2 - said and the rule; null where it meets it.
    /// </summary>
    public static (string Said, string Rule)? Energy(BimDocument document, Element element, Fabric fabric, double u)
    {
        if (!Applies(document)) return null;
        var what = fabric switch { Fabric.Wall => "wall", Fabric.Roof => "roof", Fabric.Floor => "floor", Fabric.Window => "window", Fabric.Door => "door", _ => "rooflight" };
        if (Retained(document, element))
        {
            var (threshold, improved, how) = fabric switch
            {
                Fabric.Wall => (0.70, 0.30, "upgraded to 0.30 W/m²K - 0.55 where its cavity is filled"),
                Fabric.Roof => (0.35, 0.16, "upgraded to 0.16 W/m²K"),
                Fabric.Floor => (0.70, 0.25, "upgraded to 0.25 W/m²K"),
                Fabric.Rooflight => (3.8, 2.2, "replaced by one of 2.2 W/m²K (Table 4.2)"),
                _ => (3.3, 1.4, "replaced by one of 1.4 W/m²K (Table 4.2)")
            };
            if (u <= threshold + 0.005) return null;
            return fabric is Fabric.Wall or Fabric.Roof or Fabric.Floor
                ? ($"it is a retained {what} of U-value {u:0.00}, over Table 4.3's threshold of {threshold:0.00} W/m²K.",
                    $"Approved Document L (11.7a, 4.12b, Table 4.3) asks a thermal element retained in a material change of use with a U-value over the threshold to be {how} - or to the lowest that is technically and functionally feasible with a simple payback of 15 years, 0.7 at worst (4.13).")
                : ($"it is a retained {what} of U-value {u:0.0}, over {threshold:0.0} W/m²K.",
                    $"Approved Document L (11.7b) asks a retained {what} between the heated space and outside with a U-value over {threshold:0.0} W/m²K, in a material change of use, to be {how}.");
        }

        var limit = fabric switch { Fabric.Wall => 0.18, Fabric.Roof => 0.15, Fabric.Floor => 0.18, Fabric.Rooflight => 2.2, _ => 1.4 };
        if (u <= limit + 0.005) return null;
        return ($"it is a new {what} of U-value {u:0.00}.",
            $"Approved Document L (11.7, Table 4.2) limits a new or replacement {what} in a dwelling formed by a material change of use to a U-value of {limit:0.00} W/m²K.");
    }

    /// <summary>
    /// A dwelling's openings against its floor (11.7c): windows, roof windows and outside doors, by their types' sizes, more than a quarter of
    /// its rooms' floor area - each dwelling named, or the building as one - said on its largest room.
    /// </summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!Applies(document)) yield break;
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document))).Where(entry => entry.Boundary.IsEnclosed).ToList();
        if (rooms.Count == 0) yield break;
        var homes = rooms.GroupBy(entry => Dwellings.Of(entry.Room).Length > 0 ? Dwellings.Of(entry.Room).ToLowerInvariant() : string.Empty).ToList();
        if (homes.Any(home => home.Key.Length > 0)) homes = homes.Where(home => home.Key.Length > 0).ToList();
        foreach (var home in homes)
        {
            var floor = home.Sum(entry => entry.Boundary.Area);
            if (floor <= 0) continue;
            double openings = 0;
            foreach (var (room, boundary) in home)
            {
                var (windows, doors) = WindowRules.Of(document, room, boundary.Polygon);
                openings += windows.Sum(entry => entry.Type.Width * entry.Type.Height);
                openings += doors.Where(entry => Doors.External(document, entry.Door, entry.Type)).Sum(entry => entry.Type.Width * entry.Type.Height);
                openings += WindowRules.RoofWindowsOver(document, room, boundary.Polygon).Sum(frame => frame.Type.Width * frame.Type.Height);
            }

            if (openings > floor * 0.25 + 1)
            {
                var largest = home.MaxBy(entry => entry.Boundary.Area).Room;
                yield return new EscapeProblem(largest, $"{(home.Key.Length > 0 ? $"its dwelling's" : "the dwelling's")} openings are {openings / floor:P0} of its floor, {Units.OfSquareMetres(openings / 1e6, "0.0")} of {Units.OfSquareMetres(floor / 1e6, "0.0")} - Approved Document L (11.7c) keeps the openings of a dwelling formed by a material change of use to 25% of its floor area at most, more in a building of several dwellings by SAP for them all (11.8)");
            }
        }
    }

    // --- Sound ------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a wall type carries Approved Document E's wall treatment 1 (4.22 to 4.24): on a face, two layers of plasterboard or more of
    /// 20 kg/m² at least, with mineral wool 35 thick at least behind them - on both faces, or on one where the core is masonry 100 thick plastered
    /// on both faces.
    /// </summary>
    public static bool TreatmentOne(BimDocument document, WallType type)
    {
        var layers = type.Structure.Layers.Where(layer => layer.Thickness > 0).Select(layer => (Layer: layer, Material: document.FindMaterial(layer.MaterialId))).ToList();
        bool Board(Material? material) => material?.Name is { } name && (name.Contains("plasterboard", StringComparison.OrdinalIgnoreCase) || name.Contains("gypsum board", StringComparison.OrdinalIgnoreCase));
        bool Wool(Material? material) => material?.Name is { } name && name.Contains("wool", StringComparison.OrdinalIgnoreCase);

        // A face's panel: its boards from the face in, then wool behind them.
        bool Panel(IEnumerable<(MaterialLayer Layer, Material? Material)> fromFace)
        {
            var boards = fromFace.TakeWhile(entry => Board(entry.Material)).ToList();
            var mass = boards.Sum(entry => (entry.Material?.Density ?? 0) * entry.Layer.Thickness / 1000);
            var behind = fromFace.Skip(boards.Count).FirstOrDefault();
            return boards.Count >= 2 && mass >= 20 - 0.05 && Wool(behind.Material) && behind.Layer.Thickness >= 35 - 0.5;
        }

        var (outer, inner) = (Panel(layers), Panel(Enumerable.Reverse(layers)));
        if (outer && inner) return true;
        if (!outer && !inner) return false;

        // One side only: the core masonry 100 thick at least, plastered both faces.
        var core = layers.Where(entry => entry.Material is { } material && AnalyticalModel.MaterialOf(document, material).Kind == AnalyticalMaterialKind.Masonry).Sum(entry => entry.Layer.Thickness);
        var plastered = layers.Count(entry => entry.Material?.Name.Contains("plaster", StringComparison.OrdinalIgnoreCase) == true && !Board(entry.Material));
        return core >= 100 - 0.5 && plastered >= 2;
    }

    // --- What does not apply ----------------------------------------------------------------------

    /// <summary>
    /// Whether a warning is about a requirement regulation 6 leaves out of the conversion: Approved Document M's, Approved Document O's, or
    /// Approved Document K's of a retained element.
    /// </summary>
    public static bool LeftOut(BimDocument document, Warning warning) =>
        Applies(document) && warning.Kind == WarningKind.Regulations
        && (warning.Said.Contains("Approved Document M", StringComparison.Ordinal) || warning.Said.Contains("Approved Document O", StringComparison.Ordinal)
            || warning.Said.Contains("Approved Document K", StringComparison.Ordinal) && warning.Elements.Any(element => Retained(document, element)));
}
