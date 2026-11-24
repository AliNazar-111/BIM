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

    /// <summary>
    /// Whether Approved Document E's E2 reaches the building's internal walls and floors: a dwelling-house, flats, or a building with rooms for
    /// residential purposes - beds in it, a hotel's or a hostel's; not an office's or a shop's toilets (round 101).
    /// </summary>
    public static bool Residential(BimDocument document) =>
        FireDesign.UseOf(document) is BuildingUse.House or BuildingUse.Flats
        || document.All<Component>().Any(piece => document.FindType<ComponentType>(piece.TypeId)?.Form is ComponentForm.Bed or ComponentForm.BunkBed);

    /// <summary>Whether a room is a bedroom or has a WC in it: one whose walls keep sound in.</summary>
    private static bool Quiet(BimDocument document, Room room) =>
        Fittings(document, room).Any(type => Bathrooms.IsWc(type) || type.Form is ComponentForm.Bed or ComponentForm.BunkBed);

    /// <summary>The types of the components in a room.</summary>
    private static IEnumerable<ComponentType> Fittings(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary
            ? document.All<Component>().Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location))
                .Select(piece => document.FindType<ComponentType>(piece.TypeId)).OfType<ComponentType>()
            : Enumerable.Empty<ComponentType>();

    /// <summary>The rooms each room's doors open into - none, outside - kept while the model does not change.</summary>
    private static IReadOnlyDictionary<Room, List<Room?>> Openings(BimDocument document)
    {
        IReadOnlyDictionary<Room, List<Room?>> Work()
        {
            var openings = new Dictionary<Room, List<Room?>>();
            void Add(Room room, Room? other)
            {
                if (!openings.TryGetValue(room, out var list)) openings[room] = list = new List<Room?>();
                list.Add(other);
            }

            foreach (var door in document.All<Door>())
            {
                var (a, b) = Doors.RoomsBeside(document, door);
                if (a == b) continue;
                if (a is not null) Add(a, b);
                if (b is not null) Add(b, a);
            }

            return openings;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("walls: openings", document, null, null, Work) : Work();
    }

    /// <summary>
    /// Whether Regulation E2 leaves an internal wall alone by its own exceptions: one with a door in it (a), or the wall between an en-suite toilet
    /// - a room with a WC whose doors all open into a bedroom - and the bedroom it belongs to (b).
    /// </summary>
    internal static bool Unasked(BimDocument document, Wall wall, Room one, Room other)
    {
        if (document.All<Door>().Any(door => door.HostWallId == wall.Id)) return true;
        var openings = Openings(document);
        bool EnSuite(Room toilet, Room bedroom) =>
            openings.TryGetValue(toilet, out var leads) && leads.Count > 0 && leads.All(room => room == bedroom) && Fittings(document, toilet).Any(Bathrooms.IsWc)
            && (bedroom.Name.Contains("bedroom", StringComparison.OrdinalIgnoreCase) || Fittings(document, bedroom).Any(type => type.Form is ComponentForm.Bed or ComponentForm.BunkBed));
        return EnSuite(one, other) || EnSuite(other, one);
    }

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
            yield return Problem($"its U-value is {type.HeatTransferCoefficient:0.0#}.", Domestic.Rules(document)
                ? "Approved Document L (Table 4.1) limits curtain walling in a new dwelling to a U-value of 1.6 W/m²K."
                : "Approved Document L Volume 2 (Table 4.1) limits curtain walling of a new building other than a dwelling to a U-value of 1.6 W/m²K.");

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
                // A building other than a dwelling: Approved Document M Volume 2's Table 2, as its other outside doors are (round 118).
                _ when !Domestic.Rules(document) => Circulation.Public(document)
                    ? (1000.0, "Approved Document M Volume 2 (2.13b, Table 2) asks 1000 effective clear width through an outside door of a building the general public use.")
                    : (800.0, "Approved Document M Volume 2 (2.13b, Table 2) asks 800 effective clear width through an outside door of a new building approached straight on."),
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

        var (one, other) = RoomsBeside(document, wall);
        if (!us && type.Function == WallFunction.Exterior && UValue(document, type) is { } u && u > Thermal.WallLimit + 0.005)
            Add($"its U-value is {u:0.00}.", Domestic.Rules(document, one, other)
                ? "Approved Document L (Table 4.1) limits an outside wall of a new dwelling to a U-value of 0.26 W/m²K."
                : "Approved Document L Volume 2 (Table 4.1) limits a wall of a new building other than a dwelling to a U-value of 0.26 W/m²K.");

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

            if (!us && !garage && type.Function == WallFunction.Interior && Residential(document) && (!Toilets.Applies(document) || Lodging.WithinRoom(document, one, other))
                && (Quiet(document, one) || Quiet(document, other)) && type.AcousticRating < 40 - 0.05 && !Unasked(document, wall, one, other))
                Add($"it keeps sound in to Rw {type.AcousticRating:0} dB, round a bedroom or a room with a WC.",
                    "Approved Document E (E2, Table 0.2) asks for Rw 40 dB from an internal wall round a bedroom or a room with a WC - not one with a door in it, nor the wall between an en-suite toilet and its bedroom (E2 a, b).");
        }

        // A party wall: a compartment wall between the homes, 60 minutes, up to the roof.
        if (!us && type.Separating)
        {
            if (Doors.FireMinutes(type.FireRating) < 60 && !FireSeparation.Covers(document))
                Add($"it separates two homes, {(Doors.FireMinutes(type.FireRating) is > 0 and var rated ? $"rated {rated} minutes" : "not fire-rated")}.",
                    "Approved Document B (5.8, Table B4) asks for a wall separating dwellings to be a compartment wall resisting fire for 60 minutes (REI 60).");
            if (ShortOfRoof(document, wall) is { } gap)
                Add($"it separates two homes and stops {gap:0} below the roof over it.",
                    "Approved Document B (5.11) asks a compartment wall to meet the underside of the roof covering, fire-stopped - attach its top to the roof.");
        }

        // Fire spreading over the property line: how near it the wall stands, and how much of its side is open.
        foreach (var (said, rule) in SpaceSeparation.Problems(document, wall, standard)) Add(said, rule);

        // Fire spreading over it: its outside face, its insulation, and in a relevant building every material (Section 10, Regulation 7(2)).
        foreach (var (said, rule) in Combustibility.Problems(document, wall, standard)) Add(said, rule);

        // In a block of flats: compartment walls, the common stair's protected shaft, protected corridors, places of special fire hazard (Table B3, round 94).
        if (!us)
            foreach (var (said, rule) in FireSeparation.Problems(document, wall, type)) Add(said, rule);

        // Round a place of special fire hazard in a building other than a dwelling: REI 30 (Approved Document B Volume 2 8.7, round 112).
        if (FireCompartments.Problem(document, wall, type) is var (hazard, hazardRule)) Add(hazard, hazardRule);

        // Between separate occupancies, and round a protected shaft, in a building other than a dwelling (Approved Document B Volume 2 8.11e, 8.33, round 134).
        if (!us)
            foreach (var (said, rule) in FireCompartments.Problems(document, wall, type)) Add(said, rule);

        // Round a refuse store, or a shop's walk-in store room: REI 30 or 60 (Approved Document B Volume 2 5.43a, 5.46, round 114).
        if (EscapeRoutes.Problem(document, wall, type) is var (enclosed, enclosedRule)) Add(enclosed, enclosedRule);

        // Between dwellings: one of Approved Document E's separating walls, and its junctions (round 99).
        foreach (var (said, rule) in SoundInsulation.Problems(document, wall, type)) Add(said, rule);

        // A care home's bedroom or ancillary accommodation enclosed in REI 30 (Approved Document B Volume 2 2.42, 2.44, round 129).
        foreach (var (said, rule) in CareHomes.Problems(document, wall, type)) Add(said, rule);

        // Its cavity closed at its edges, and the cavity over it where it stops short of the floor or roof (Approved Document B 5.18, 8.5, 9.4, 9.5, round 127).
        foreach (var (said, rule) in Cavities.Problems(document, wall, type)) Add(said, rule);

        // Its doors, no more than a quarter of a compartment wall (C8, round 96).
        if (!us)
            foreach (var (said, rule) in FireGlazing.Problems(document, wall)) Add(said, rule);

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
        var u = document.FindType<CurtainWallType>(wall.TypeId) is { } curtain ? $"{Units.UValue(curtain.HeatTransferCoefficient, "0.0#")}{(curtain.SafetyGlazing ? ", safety glass" : string.Empty)}"
            : type.Function == WallFunction.Exterior && UValue(document, type) is { } value ? Units.UValue(value, "0.00") : $"Rw {type.AcousticRating:0} dB";
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
