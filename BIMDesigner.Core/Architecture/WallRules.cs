using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What the rules ask of a wall. An outside wall lets out no more heat than Approved Document L
/// allows a new dwelling's - a U-value of 0.26 W/m²K (Table 4.1). A wall round a bedroom or a room
/// with a WC keeps sound in to Rw 40 dB (Approved Document E, E2, Table 0.2). A wall between a
/// house and its integral garage resists fire for 30 minutes (Approved Document B 5.6) - under the
/// IRC, has 1/2 in gypsum board on its garage side (R302.6).
/// </summary>
public static class WallRules
{
    /// <summary>A wall's U-value: worked out from its layers where their materials are known, else what its type says.</summary>
    public static double? UValue(BimDocument document, WallType type) =>
        type.Log is null && Thermal.UValue(document, type.Structure) is { } worked ? worked
        : type.HeatTransferCoefficient > 0 ? type.HeatTransferCoefficient : null;

    /// <summary>The rooms either side of a wall, at its middle.</summary>
    public static (Room? One, Room? Other) RoomsBeside(BimDocument document, Wall wall)
    {
        if (document.GetWallType(wall) is not { } type || wall.Length < 1) return (null, null);
        var reach = type.Structure.TotalWidth / 2 + 150;
        return (RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, wall.Length / 2, reach)),
            RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, wall.Length / 2, -reach)));
    }

    /// <summary>Whether a room is a bedroom or has a WC in it: one whose walls keep sound in.</summary>
    private static bool Quiet(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary && document.Elements.OfType<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location))
            .Select(piece => document.FindType<ComponentType>(piece.TypeId))
            .Any(type => type is not null && (Bathrooms.IsWc(type) || type.Form is ComponentForm.Bed or ComponentForm.BunkBed));

    /// <summary>
    /// What a curtain wall falls short of: its U-value (Approved Document L's 1.6 for curtain
    /// walling, Table 4.1); its glass, low in it, where it is not safety glass (Approved Document
    /// K Diagram 5.1 - below 800; IRC R308.4.3 - a pane over 9 sq ft coming below 18 in); and the
    /// clear width its doors give, as any external door's (Approved Document M 2.20, 2.21, 3.22;
    /// ADA 404.2.3).
    /// </summary>
    private static IEnumerable<PlacementProblem> CurtainProblems(BimDocument document, Wall wall, ClearanceStandard standard)
    {
        if (document.FindType<CurtainWallType>(wall.TypeId) is not { } type || CurtainLayout.Of(document, wall) is not { } layout) yield break;

        var us = standard.IsUs();
        var body = type.Body;
        var half = body.Structure.TotalWidth / 2 + 100;
        var outline = CurtainGeometry.Band(wall, body, 0, wall.Length, half, -half);
        PlacementProblem Problem(string said, string rule) => new(wall, said, rule, outline);

        if (!us && type.Function == WallFunction.Exterior && type.HeatTransferCoefficient > 1.6 + 0.005)
            yield return Problem($"its U-value is {type.HeatTransferCoefficient:0.0#}.", "Approved Document L (Table 4.1) limits curtain walling in a new dwelling to a U-value of 1.6 W/m²K.");

        var glass = layout.Cells.Where(cell => cell.Kind is CurtainPanelKind.Glazed or CurtainPanelKind.Door).ToList();
        if (!type.SafetyGlazing && glass.Count > 0)
        {
            var lowest = glass.Min(cell => cell.ClearBottom);
            if (!us && lowest < 800)
                yield return Problem($"its glass comes down to {lowest:0} off the floor, not safety glass.", "Approved Document K (Diagram 5.1) makes glass below 800 a critical location: safety glass to BS EN 12600.");
            else if (us && glass.Any(cell => (cell.ClearTo - cell.ClearFrom) * (cell.ClearTop - cell.ClearBottom) > 836000 && cell.ClearBottom < 457 && cell.ClearTop > 914))
                yield return Problem("a pane over 9 sq ft comes down below 18 in (457), not safety glass.", "IRC R308.4.3 makes such a pane a hazardous location: safety glazing.");
        }

        if (type.Function != WallFunction.Exterior) yield break;
        foreach (var cell in layout.Cells.Where(cell => cell.Kind == CurtainPanelKind.Door))
        {
            var door = cell.OpeningTypeId is { } typeId ? document.FindType<DoorType>(typeId) : null;
            var inside = cell.ClearTo - cell.ClearFrom;
            var clear = door?.Operation switch
            {
                DoorOperation.DoubleSwing => inside / 2 - Doors.Leaf,
                DoorOperation.Sliding when door.LeafCount >= 2 => inside / 2,
                DoorOperation.Sliding => inside,
                _ => inside - Doors.Leaf
            };
            var (wanted, rule) = standard switch
            {
                ClearanceStandard.Ada => (815.0, "ADA 404.2.3 asks for 32 in (815) clear through a door."),
                ClearanceStandard.Irc => (0.0, string.Empty),
                ClearanceStandard.ApprovedDocumentM3 => (850.0, "Approved Document M (M4(3) 3.22) asks for 850 clear through every door, for a wheelchair user."),
                _ => (850.0, "Approved Document M (M4(2) 2.20d-e, 2.21) asks for 850 clear through an external door - a pair's main leaf.")
            };
            if (clear < wanted - 0.5)
                yield return Problem($"its door gives {clear:0} clear.", rule);
        }
    }

    /// <summary>What a wall falls short of, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Wall wall, ClearanceStandard standard)
    {
        if (document.IsCurtainWall(wall)) return wall.Length < 1 ? Array.Empty<PlacementProblem>() : CurtainProblems(document, wall, standard).ToList();
        if (document.GetWallType(wall) is not { } type || wall.Length < 1) return Array.Empty<PlacementProblem>();

        var us = standard.IsUs();
        var half = type.Structure.TotalWidth / 2;
        var outline = CurtainGeometry.Band(wall, type, 0, wall.Length, half, -half);
        var problems = new List<PlacementProblem>();
        void Add(string said, string rule) => problems.Add(new PlacementProblem(wall, said, rule, outline));

        if (!us && type.Function == WallFunction.Exterior && UValue(document, type) is { } u && u > Thermal.WallLimit + 0.005)
            Add($"its U-value is {u:0.00}.", "Approved Document L (Table 4.1) limits an outside wall of a new dwelling to a U-value of 0.26 W/m²K.");

        var (one, other) = RoomsBeside(document, wall);
        if (one is not null && other is not null && one != other)
        {
            var garage = Doors.Garage(document, one) != Doors.Garage(document, other);
            if (!us && garage && Doors.FireMinutes(type.FireRating) < 30)
                Add($"it stands between the house and its garage, {(Doors.FireMinutes(type.FireRating) is > 0 and var minutes ? $"rated {minutes} minutes" : "not fire-rated")}.",
                    "Approved Document B (5.6) asks for the wall between a house and its integral garage to resist fire for 30 minutes (REI 30).");
            if (us && garage && !type.Structure.Layers.Any(layer => document.FindMaterial(layer.MaterialId)?.Name is { } name
                    && (name.Contains("plasterboard", StringComparison.OrdinalIgnoreCase) || name.Contains("gypsum", StringComparison.OrdinalIgnoreCase)) && layer.Thickness >= 12.5))
                Add("it stands between the house and its garage, with no gypsum board on it.",
                    "IRC R302.6 asks for 1/2 in (12.7) gypsum board on the garage side of the wall between a garage and the house.");

            if (!us && !garage && type.Function == WallFunction.Interior && (Quiet(document, one) || Quiet(document, other)) && type.AcousticRating < 40 - 0.05)
                Add($"it keeps sound in to Rw {type.AcousticRating:0} dB, round a bedroom or a room with a WC.",
                    "Approved Document E (E2, Table 0.2) asks for Rw 40 dB from an internal wall round a bedroom or a room with a WC.");
        }

        return problems;
    }

    /// <summary>What a wall comes to as its rules see it - its U-value outside, what it falls short of.</summary>
    public static string Verdict(BimDocument document, Wall wall)
    {
        if (document.GetWallType(wall) is not { } type) return string.Empty;
        var problems = Problems(document, wall, document.ProjectInformation.ClearanceStandard);
        var u = document.FindType<CurtainWallType>(wall.TypeId) is { } curtain ? $"U {curtain.HeatTransferCoefficient:0.0#} W/m²K{(curtain.SafetyGlazing ? ", safety glass" : string.Empty)}"
            : type.Function == WallFunction.Exterior && UValue(document, type) is { } value ? $"U {value:0.00} W/m²K" : $"Rw {type.AcousticRating:0} dB";
        return problems.Count == 0 ? u : $"{u} - {string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"))}";
    }

    /// <summary>Every wall's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.Walls
            .Where(wall => wall.LevelId == levelId)
            .SelectMany(wall => Problems(document, wall, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(wall, "its build-up", problem.Outline, problem.Rule), string.Empty,
                    $"{document.GetWallType(wall)?.Name ?? "A wall"}{(string.IsNullOrWhiteSpace(wall.Mark) ? string.Empty : $" ({wall.Mark})")}", problem.Said)))
            .ToList();
}
