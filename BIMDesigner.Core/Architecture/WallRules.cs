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
        room.GetBoundary(document) is { IsEnclosed: true } boundary && document.All<Component>()
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
                yield return Problem($"a pane over 9 sq ft comes down below 18 in{Units.MetricAside("457")}, not safety glass.", "IRC R308.4.3 makes such a pane a hazardous location: safety glazing.");
        }

        // Its mullions and transoms: how far the wind bends them, and the glass on a transom - and how they are fixed and spliced.
        foreach (var (said, rule) in MullionRules.Problems(document, wall, us).Concat(MullionFixings.Problems(document, wall)))
            yield return Problem(said, rule);

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
                ClearanceStandard.Ada => (815.0, $"ADA 404.2.3 asks for 32 in{Units.MetricAside("815")} clear through a door."),
                ClearanceStandard.Irc => (0.0, string.Empty),
                ClearanceStandard.ApprovedDocumentM3 => (850.0, "Approved Document M (M4(3) 3.22) asks for 850 clear through every door, for a wheelchair user."),
                _ => (850.0, "Approved Document M (M4(2) 2.20d-e, 2.21) asks for 850 clear through an external door - a pair's main leaf.")
            };
            if (clear < wanted - 0.5)
                yield return Problem($"its door gives {Units.Bare(clear)} clear.", rule);
        }
    }

    /// <summary>What a wall falls short of, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Wall wall, ClearanceStandard standard)
    {
        if (document.IsCurtainWall(wall))
        {
            if (wall.Length < 1) return Array.Empty<PlacementProblem>();
            var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
            var side = along.PerpendicularLeft() * 100;
            var band = new[] { wall.Start - side, wall.End - side, wall.End + side, wall.Start + side };
            return CurtainProblems(document, wall, standard)
                .Concat(SpaceSeparation.Problems(document, wall, standard).Select(problem => new PlacementProblem(wall, problem.Said, problem.Rule, band)))
                .ToList();
        }
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
                    $"IRC R302.6 asks for 1/2 in{Units.MetricAside("12.7")} gypsum board on the garage side of the wall between a garage and the house.");

            if (!us && !garage && type.Function == WallFunction.Interior && (Quiet(document, one) || Quiet(document, other)) && type.AcousticRating < 40 - 0.05)
                Add($"it keeps sound in to Rw {type.AcousticRating:0} dB, round a bedroom or a room with a WC.",
                    "Approved Document E (E2, Table 0.2) asks for Rw 40 dB from an internal wall round a bedroom or a room with a WC.");
        }

        // A party wall: a compartment wall between the homes, 60 minutes, up to the roof.
        if (!us && type.Separating)
        {
            if (Doors.FireMinutes(type.FireRating) < 60)
                Add($"it separates two homes, {(Doors.FireMinutes(type.FireRating) is > 0 and var rated ? $"rated {rated} minutes" : "not fire-rated")}.",
                    "Approved Document B (5.8, Table B4) asks for a wall separating dwellings to be a compartment wall resisting fire for 60 minutes (REI 60).");
            if (ShortOfRoof(document, wall) is { } gap)
                Add($"it separates two homes and stops {gap:0} below the roof over it.",
                    "Approved Document B (5.11) asks a compartment wall to meet the underside of the roof covering, fire-stopped - attach its top to the roof.");
        }

        // Fire spreading over the property line: how near it the wall stands, and how much of its side is open.
        foreach (var (said, rule) in SpaceSeparation.Problems(document, wall, standard)) Add(said, rule);

        return problems;
    }

    /// <summary>
    /// How far short of the roof over it a party wall stops, mm - for the topmost of the party walls
    /// stacked up that line, where a roof is over it; null where it reaches it, or none is over it.
    /// </summary>
    public static double? ShortOfRoof(BimDocument document, Wall wall)
    {
        var middle = wall.Start.MidpointTo(wall.End);
        var roofs = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Contains(middle)).ToList();
        if (roofs.Count == 0) return null;

        // The party walls on the same line, storey above storey: the highest of them is the one to reach the roof.
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var stack = document.Walls.Where(other => document.GetWallType(other) is { Separating: true }
            && Math.Abs(way.Cross(other.Start.MidpointTo(other.End) - wall.Start)) < 50 && Math.Abs(way.Cross((other.End - other.Start).NormalisedOrDefault(way))) < 0.02).ToList();
        var top = wall.GetTopElevation(document);
        if (stack.Any(other => other.GetTopElevation(document) > top + 1)) return null;

        var underside = roofs.Max(roof => roof.UndersideAt(document, middle));
        return underside - top > 50 ? underside - top : null;
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
