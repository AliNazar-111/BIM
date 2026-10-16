using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What a door gives and what the rules ask of it: its clear opening width - measured, as
/// Approved Document M and the IRC measure it, from the stop on one jamb to the face of the leaf
/// opened to 90 degrees on the other - and whether it is wide enough, fire-resisting enough,
/// glazed safely enough and set clear of the next door's swing.
/// </summary>
public static class Doors
{
    /// <summary>How wide a door's frame is on each jamb, mm - its lining and stop.</summary>
    public const double Frame = 50;

    /// <summary>How thick a leaf is, mm: what stands in the opening when it is open at 90 degrees.</summary>
    public const double Leaf = 40;

    /// <summary>Whether a door's leaves are glazed.</summary>
    public static bool Glazed(DoorType type) =>
        type.LeafDesign is DoorLeafDesign.Glazed or DoorLeafDesign.FrenchGlazed or DoorLeafDesign.HalfGlazed or DoorLeafDesign.FullGlass or DoorLeafDesign.ArchedTopLight;

    /// <summary>
    /// The clear width a door gives when open: between its frame's stops, less the leaf open in the
    /// way - for a pair, its main leaf's; for a slider, the opening its leaf runs clear of; for a
    /// bi-fold, less its leaves folded back.
    /// </summary>
    public static double ClearWidth(DoorType type)
    {
        var inside = Math.Max(0, type.Width - 2 * Frame);
        return Math.Max(0, type.Operation switch
        {
            DoorOperation.Swing when type.LeafCount < 2 => inside - Leaf,
            DoorOperation.Swing or DoorOperation.DoubleSwing => inside / 2 - Leaf,
            DoorOperation.Sliding when type.LeafCount >= 2 => inside / 2,
            DoorOperation.Sliding or DoorOperation.Overhead => inside,
            DoorOperation.Folding => inside - Math.Max(4, type.LeafCount * 2) * Math.Max(20, Math.Min(type.Thickness, 60)) / 2,
            _ => inside - Leaf
        });
    }

    /// <summary>A rating's minutes, as it is written: "30 min", "FD30", "E 30 Sa" all 30; none, 0.</summary>
    public static int FireMinutes(string rating)
    {
        var digits = new string(rating.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var minutes) ? minutes : 0;
    }

    /// <summary>Whether a door leads outside: an exterior door's type, or in an outside wall.</summary>
    public static bool External(BimDocument document, Door door, DoorType type) =>
        type.Function == DoorFunction.Exterior
        || document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is { } wall && document.GetWallType(wall) is { Function: WallFunction.Exterior };

    /// <summary>The rooms either side of a door, where there are rooms there: on its swing side first.</summary>
    public static (Room? Swing, Room? Other) RoomsBeside(BimDocument document, Door door)
    {
        if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } type) return (null, null);

        var reach = type.Structure.TotalWidth / 2 + 150;
        var side = door.FlipFacing ? -1.0 : 1.0;
        return (RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, door.DistanceAlongWall, side * reach)),
            RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, door.DistanceAlongWall, -side * reach)));
    }

    /// <summary>Whether a room is a garage: called one, or with a car in it.</summary>
    public static bool Garage(BimDocument document, Room room) =>
        room.Name.Contains("garage", StringComparison.OrdinalIgnoreCase)
        || room.GetBoundary(document) is { IsEnclosed: true } boundary && document.Elements.OfType<Component>()
            .Any(piece => piece.LevelId == room.LevelId && document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Car && Polygon2D.Contains(boundary.Polygon, piece.Location));

    /// <summary>Whether a room is a bathroom or a WC - not a habitable room.</summary>
    private static bool Wet(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary && document.Elements.OfType<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location))
            .Select(piece => document.FindType<ComponentType>(piece.TypeId))
            .Any(type => type is not null && (Bathrooms.IsWc(type) || type.Form is ComponentForm.Bath or ComponentForm.Shower));

    /// <summary>
    /// The rooms a stair rises through - the one at its foot and the one at its head - which in
    /// a house with a storey more than 4.5 m up are its protected stairway.
    /// </summary>
    public static HashSet<Room> StairRooms(BimDocument document)
    {
        var rooms = new HashSet<Room>();
        foreach (var stair in document.Elements.OfType<Stair>())
        {
            var walk = Stairs.Layout(document, stair).WalkingLine;
            if (walk.Count < 2) continue;
            if (RoomsOf.Containing(document, stair.LevelId, walk[0]) is { } foot) rooms.Add(foot);
            if (stair.TopLevelId is { } top && RoomsOf.Containing(document, top, walk[^1]) is { } head) rooms.Add(head);
        }

        return rooms;
    }

    /// <summary>Whether a dwelling has a storey whose floor is more than 4.5 m above the ground: one that wants a protected stair.</summary>
    public static bool HasHighStorey(BimDocument document)
    {
        var withRooms = document.Levels.Where(level => document.Elements.OfType<Room>().Any(room => room.LevelId == level.Id)).ToList();
        if (withRooms.Count == 0) return false;
        var ground = document.Levels.Min(level => level.Elevation);
        return withRooms.Any(level => level.Elevation - ground > 4500 + 0.5);
    }

    /// <summary>
    /// How much wall there is beside a door's leading edge, on the side it opens to: along the
    /// face from its latch jamb to the wall that returns there; null where none returns within 1 m.
    /// </summary>
    public static double? Nib(BimDocument document, Door door, DoorType type)
    {
        if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } host || document.GetWallType(host) is not { } hostType) return null;

        var others = document.Walls
            .Where(wall => wall != host && wall.LevelId == host.LevelId && document.GetWallType(wall) is { } t && t.Structure.TotalWidth > 0)
            .Select(wall => CurtainGeometry.Band(wall, document.GetWallType(wall)!, 0, wall.Length, document.GetWallType(wall)!.Structure.TotalWidth / 2, -document.GetWallType(wall)!.Structure.TotalWidth / 2))
            .ToList();

        var (from, to) = door.GetSpan(type);
        var (latch, away) = door.FlipHand ? (from, -1.0) : (to, 1.0);
        var face = (door.FlipFacing ? -1 : 1) * (hostType.Structure.TotalWidth / 2 + 10);
        for (var reach = 0.0; reach <= 1000; reach += 5)
            if (others.Any(band => Polygon2D.Contains(band, host.PointAt(hostType.Structure, latch + away * reach, face))))
                return reach;

        return null;
    }

    /// <summary>What a door falls short of, as the project's standard asks: its width, its nib, its fire rating and closer, its glass, its U-value.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Door door, ClearanceStandard standard)
    {
        if (document.FindType<DoorType>(door.TypeId) is not { } type || type.CurtainPanel) return Array.Empty<PlacementProblem>();
        if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType)
            return Array.Empty<PlacementProblem>();

        var (from, to) = door.GetSpan(type);
        var half = wallType.Structure.TotalWidth / 2;
        var outline = CurtainGeometry.Band(wall, wallType, from, to, half + 100, -half - 100);
        var problems = new List<PlacementProblem>();
        void Add(string said, string rule) => problems.Add(new PlacementProblem(door, said, rule, outline));

        var clear = ClearWidth(type);
        var external = External(document, door, type);
        var swings = type.Operation is DoorOperation.Swing or DoorOperation.DoubleSwing;
        var pair = type.Operation == DoorOperation.DoubleSwing || type.LeafCount >= 2;

        // Wide enough.
        switch (standard)
        {
            case ClearanceStandard.ApprovedDocumentM when external && clear < 850 - 0.5:
                Add($"it gives {clear:0} clear{(pair ? " through its main leaf" : string.Empty)}.",
                    "Approved Document M (M4(2) 2.20d-e, 2.21) asks for 850 clear through an external door - a pair's main leaf.");
                break;
            case ClearanceStandard.ApprovedDocumentM when !external && clear < 750 - 0.5:
                Add($"it gives {clear:0} clear.", "Approved Document M (M4(2) Table 2.1) asks for 750 clear through a door at the least - more off a narrow hall.");
                break;
            case ClearanceStandard.ApprovedDocumentM3 when clear < 850 - 0.5:
                Add($"it gives {clear:0} clear{(pair ? " through its main leaf" : string.Empty)}.",
                    "Approved Document M (M4(3) 3.22, 3.24) asks for 850 clear through every door, for a wheelchair user.");
                break;
            case ClearanceStandard.Ada when clear < 815 - 0.5:
                Add($"it gives {clear:0} clear.", "ADA 404.2.3 asks for 32 in (815) clear through a door.");
                break;
        }

        // The US's egress door: one at least, side-hinged, 32 in by 78 in clear.
        if (standard.IsUs() && external && swings && !document.Elements.OfType<Door>().Any(other =>
                document.FindType<DoorType>(other.TypeId) is { Operation: DoorOperation.Swing or DoorOperation.DoubleSwing } otherType
                && External(document, other, otherType) && ClearWidth(otherType) >= 813 - 0.5 && otherType.Height - Frame >= 1981 - 0.5))
            Add("no door out of the dwelling is 32 in (813) clear and 78 in (1981) high.",
                "IRC R311.2 asks for one side-hinged egress door 32 in clear, 78 in high, at the least.");

        // A nib beside its leading edge, on the side it opens to: every external door, and every door on the entrance storey under M4(2); every door under M4(3).
        var entranceStorey = document.Elements.OfType<Door>()
            .Where(other => document.FindType<DoorType>(other.TypeId) is { } otherType && External(document, other, otherType))
            .Select(other => document.Walls.FirstOrDefault(candidate => candidate.Id == other.HostWallId)?.LevelId)
            .FirstOrDefault();
        var wantsNib = standard == ClearanceStandard.ApprovedDocumentM3 || standard == ClearanceStandard.ApprovedDocumentM && (external || wall.LevelId == entranceStorey);
        if (wantsNib && swings && !pair && Nib(document, door, type) is { } nib && nib < 300 - 0.5)
            Add($"its leading edge is {nib:0} from the wall beside it.",
                standard == ClearanceStandard.ApprovedDocumentM3
                    ? "Approved Document M (M4(3) 3.22, 3.24) asks for a 300 nib beside every door's leading edge, on the side it opens to."
                    : "Approved Document M (M4(2) 2.20f, 2.22) asks for a 300 nib beside the leading edge of an external door, and of every door on the entrance storey, on the side it opens to.");

        // Fire doors: into an integral garage, and onto the stair of a house with a storey over 4.5 m.
        var (swingRoom, otherRoom) = RoomsBeside(document, door);
        var garage = swingRoom is not null && otherRoom is not null && Garage(document, swingRoom) != Garage(document, otherRoom);
        var minutes = FireMinutes(type.FireRating);
        if (garage && (minutes < (standard.IsUs() ? 20 : 30) || !type.SelfClosing))
            Add($"it opens into the garage, {(minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated")}{(type.SelfClosing ? string.Empty : " and with no closer")}.",
                standard.IsUs()
                    ? "IRC R302.5.1 asks for a door between a garage and the house to be solid, or rated 20 minutes, and self-closing and self-latching."
                    : "Approved Document B (5.6, Table C1 item 11a, C5) asks for a door between a house and its garage of FD30S - 30 minutes - and self-closing.");

        if (!standard.IsUs() && !garage && swingRoom is not null && otherRoom is not null && HasHighStorey(document) && minutes < 20)
        {
            var stair = StairRooms(document);
            var onto = stair.Contains(swingRoom) ? otherRoom : stair.Contains(otherRoom) ? swingRoom : null;
            if (onto is not null && !stair.Contains(onto) && !Wet(document, onto))
                Add($"it opens off the stair, which a storey more than 4.5 m up makes a protected stairway, {(minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated")}.",
                    "Approved Document B (2.5a, Table C1 item 11b) asks for the doors from habitable rooms onto a house's protected stair to be fire doors, FD20.");
        }

        // Its glass, and how much heat it lets out.
        if (Glazed(type) && !type.SafetyGlazing)
            Add("its glass is not safety glass.",
                standard.IsUs()
                    ? "IRC R308.4.1 makes all glass in a door a hazardous location: tempered or laminated safety glazing."
                    : "Approved Document K (5.1, Diagram 5.1) makes glass in a door below 1500 a critical location: safety glass to BS EN 12600, class 3 at least.");

        if (!standard.IsUs() && external && type.HeatTransferCoefficient > 1.6 + 0.005)
            Add($"its U-value is {type.HeatTransferCoefficient:0.0#}.",
                "Approved Document L (Table 4.1) limits an external door in a new dwelling to a U-value of 1.6 W/m²K.");

        return problems;
    }

    /// <summary>What a door comes to as its rules see it - its clear width, and what it falls short of.</summary>
    public static string Verdict(BimDocument document, Door door)
    {
        if (document.FindType<DoorType>(door.TypeId) is not { } type) return string.Empty;
        var problems = Problems(document, door, document.ProjectInformation.ClearanceStandard);
        var clear = $"{ClearWidth(type):0} clear";
        return problems.Count == 0 ? $"{clear} - meets {Clearances.Label(document.ProjectInformation.ClearanceStandard)}"
            : $"{clear} - {string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"))}";
    }

    /// <summary>Every door's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.Elements.OfType<Door>()
            .Where(door => document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId)?.LevelId == levelId)
            .SelectMany(door => Problems(document, door, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(door, "its place", problem.Outline, problem.Rule), string.Empty,
                    $"{document.FindType<DoorType>(door.TypeId)?.Name ?? "A door"}{(string.IsNullOrWhiteSpace(door.Mark) ? string.Empty : $" ({door.Mark})")}", problem.Said)))
            .ToList();
}
