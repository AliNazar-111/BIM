using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The way into a home, as Approved Document M Volume 1 has it (round 163): a block of flats' principal communal entrance, the other communal
/// doors along its approach route and its lifts; and each dwelling's principal private entrance - a flat's door off the common parts, a house's
/// front door.
///
/// A block is a project with rooms named into two dwellings or more - one, in a building of flats. Its common parts are its rooms named into no
/// dwelling - the homes' part, in a building of mixed use - but plant rooms, risers, ducts and cupboards. Its principal communal entrance is the
/// outside door into them an approach path comes to, or with none, one into a hall, lobby or corridor; then the lowest, the widest, the first
/// drawn. Its other communal doors are those between rooms of the common parts - not a lift's landing doors - and those out of them that an
/// approach path comes to.
///
/// The principal communal entrance: under M4(1) 775 clear (1.9a) and an accessible threshold, 15 high at most (b); under M4(2) a level landing
/// 1500 square outside it, clear of its swing, covered 1200 wide and 900 deep (2.14a, b - in Approaches), lit by a light switched on by a dusk
/// to dawn timer or by detecting motion (c), 850 clear through its main leaf (d, e), a 300 nib beside its leading edge, the width it makes kept
/// 1200 out from it (f), the reveal on that side 200 deep at most (g), an accessible threshold (h), and where there is a lobby or porch its
/// doors 1500 apart and 1500 between their swings (i); under M4(3) its landing 1500 square covered 1200 by 1200 (3.14a, b), lit (c), a 1500
/// turning circle inside it (d), 850 (e, f), the 300 nib kept 1800 (g), a 200 nib beside its following edge kept 1800 (h), the reveal (i), the
/// threshold (j), the lobby (k). Every other communal door: 850, the nibs, the reveal (2.15, 3.15) - M4(1) asks nothing of them.
///
/// A dwelling's principal private entrance: under M4(1) a flat's 775 clear (1.14a) - a house's is Visitable's; under M4(2) lit where it is
/// outside (2.20c), 850 (d, e), the 300 nib kept 1200 (f), the reveal (g), an accessible threshold where it is outside (h), a lobby or porch
/// (i); under M4(3) lit (3.22c), a 1500 turning circle inside it in front of the door (d), the 300 nib kept 1800 (e), 850 (f), the 200 nib
/// beside its following edge kept 1500 (g), the reveal (h), the threshold (i), a lobby or porch (j). A flat's entrance storey is the level of
/// its door off the common parts (2.22d's nibs on every door there).
///
/// A block's lifts (Lifts): a car 900 by 1250 under M4(1) (1.11d), 1100 by 1400 - a type 2 lift's - under M4(2) and M4(3) (2.16b, c, 3.16b,
/// c); doors 800 clear (1.11c, 2.16d, 3.16d); 1500 square clear in front of the lift door at every floor (1.11a, 2.16a, 3.16a). Under M4(2) and
/// M4(3) a flat's entrance on a storey other than the communal entrance's with a lift stopping there - the approach step-free whatever storey
/// the dwelling is on (2.6, 3.7); M4(1) lets a stair do (1.10). The common stair is a general access stair already (round 150; 1.12, 2.17, 3.17).
///
/// Not done: door entry controls (2.14k, 3.14n, 3.22k) and the fused spur for a door opener (3.22l), there being no such fittings; opening
/// forces and power assisted opening (3.14l); a lift's load, controls, signals and dwell time (1.11b, e to k, 2.16e, f, 3.16e, f); lobbies
/// further in than the entrance; a flat with its own door out, and a flat off an open access deck, whose landing, cover and light are not
/// asked; the ground surface (1.9c, 2.14j, 3.14m); parking and drop-off (2.12, 2.13, 3.12, 3.13); the approach route's widths inside the
/// common parts (2.9c, 3.9b); the approach to communal facilities (2.4, 3.4); a flat's own WC and doors on its entrance storey under M4(1)
/// (1.15 to 1.17), which Visitable gives a house alone.
/// </summary>
public static class Entrances
{
    /// <summary>What a door is to the way into a home.</summary>
    public enum Role
    {
        None,
        /// <summary>A block's principal communal entrance.</summary>
        CommunalEntrance,
        /// <summary>Another communal door along a block's approach route.</summary>
        CommunalDoor,
        /// <summary>A dwelling's principal private entrance: a flat's door off the common parts, a house's front door.</summary>
        PrivateEntrance
    }

    /// <summary>The paragraphs a door's role asks by, under one standard: null where the standard asks nothing of it there.</summary>
    private sealed record Asks(string What, double Clear, string Width, string? Threshold, string? Light, string? Lead, double LeadKept,
        string? Follow, double FollowKept, string? Reveal, string? Lobby, string? Turning);

    private static bool Checked(ClearanceStandard standard) =>
        standard is ClearanceStandard.ApprovedDocumentM1 or ClearanceStandard.ApprovedDocumentM or ClearanceStandard.ApprovedDocumentM3;

    /// <summary>Whether the project is a block of flats in England checked to M4: rooms named into two dwellings or more - one, in a building of flats.</summary>
    public static bool Block(BimDocument document) =>
        Checked(document.ProjectInformation.ClearanceStandard) && Kept(document, "block", () =>
        {
            var named = document.All<Room>().Select(Dwellings.Of).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return named >= 2 || named == 1 && FireDesign.UseOf(document) == BuildingUse.Flats;
        });

    /// <summary>Whether the project is a house in England checked to M4 - not an extension, which Volume 1 leaves out (0.2).</summary>
    private static bool House(BimDocument document) =>
        Checked(document.ProjectInformation.ClearanceStandard) && FireDesign.UseOf(document) == BuildingUse.House && !Block(document)
        && !document.ProjectInformation.BuildingType.Contains("extension", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ServiceWords = { "plant", "riser", "duct", "cupboard", "meter", "cleaner", "shaft", "switch room", "tank room", "sprinkler" };

    /// <summary>
    /// A room of a block's common parts that people pass through or go to: named into no dwelling - the homes' part, in a building of mixed
    /// use - and not a plant room, a riser, a duct or a cupboard.
    /// </summary>
    public static bool Common(BimDocument document, Room room) =>
        MixedUse.Common(document, room) && !ServiceWords.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The room inside an outside door: the one either side of it that is not outside - with rooms both sides, the one away from the wall's outer face.</summary>
    private static Room? Inside(BimDocument document, Door door)
    {
        var (swing, other) = Doors.RoomsBeside(document, door);
        if (swing is null || other is null) return swing ?? other;
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } type) return swing;
        var outward = SpaceSeparation.Outward(document, wall);
        var probe = wall.PointAt(type.Structure, door.DistanceAlongWall, 0) - outward * (type.Structure.TotalWidth / 2 + 150);
        return RoomsOf.Containing(document, wall.LevelId, probe) ?? swing;
    }

    /// <summary>The room a flat's door off the common parts opens from - the side named into a dwelling.</summary>
    private static Room? DwellingSide(BimDocument document, Door door)
    {
        var (swing, other) = Doors.RoomsBeside(document, door);
        return swing is not null && Dwellings.Of(swing).Length > 0 ? swing : other is not null && Dwellings.Of(other).Length > 0 ? other : null;
    }

    /// <summary>
    /// A block's principal communal entrance: of the outside doors into its common parts, the one an approach path comes to, or with none, one
    /// into a hall, lobby or corridor; then the lowest, the widest, the first drawn. Null in a house, or with no such door.
    /// </summary>
    public static Door? CommunalEntrance(BimDocument document) => !Block(document) ? null : Kept<Door?>(document, "communal entrance", () =>
    {
        var outside = document.All<Door>()
            .Select((door, order) => (Door: door, Order: order, Type: document.FindType<DoorType>(door.TypeId)))
            .Where(entry => entry.Type is { CurtainPanel: false } type && type.Operation != DoorOperation.Overhead && Doors.External(document, entry.Door, type)
                            && document.Find<Wall>(entry.Door.HostWallId) is not null
                            && Inside(document, entry.Door) is { } room && Common(document, room))
            .ToList();
        if (outside.Count == 0) return null;

        bool IntoHall(Door door) => Inside(document, door) is { } room
                                    && (new[] { "lobby", "entrance", "foyer", "hall", "corridor", "porch", "vestibule" }.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                                        || room.GetBoundary(document) is { IsEnclosed: true } boundary && Lighting.KindOf(document, room, boundary.Polygon) == LitRoom.Hall);

        return outside
            .OrderByDescending(entry => Approaches.IsApproachedEntrance(document, entry.Door))
            .ThenByDescending(entry => IntoHall(entry.Door))
            .ThenBy(entry => document.FindLevel(document.Find<Wall>(entry.Door.HostWallId)!.LevelId)?.Elevation ?? 0)
            .ThenByDescending(entry => Math.Round(Doors.ClearWidth(entry.Type!)))
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Door)
            .First();
    });

    /// <summary>
    /// Whether a door is a dwelling's principal private entrance: in a block, of a flat's doors between a room named into it and the common
    /// parts, the one on its lowest storey - a maisonette's door from its upper floor onto the common stair is a way out, not its entrance -
    /// then the widest, the first drawn; in a house, its front door (Visitable).
    /// </summary>
    public static bool PrivateEntrance(BimDocument document, Door door)
    {
        if (!Block(document)) return House(document) && door == Visitable.Entrance(document);
        return OntoCommon(document, door) && DwellingSide(document, door) is { } room && Principal(document).TryGetValue(Dwellings.Of(room), out var principal) && principal == door;
    }

    /// <summary>Whether a door is between a room named into a dwelling and the common parts.</summary>
    private static bool OntoCommon(BimDocument document, Door door)
    {
        var (swing, other) = Doors.RoomsBeside(document, door);
        if (swing is null || other is null) return false;
        var (one, two) = (Dwellings.Of(swing), Dwellings.Of(other));
        return one.Length > 0 && two.Length == 0 && Common(document, other) || two.Length > 0 && one.Length == 0 && Common(document, swing);
    }

    /// <summary>In a block, each flat's principal private entrance, by its name: of its doors onto the common parts, the lowest, the widest, the first drawn.</summary>
    private static IReadOnlyDictionary<string, Door> Principal(BimDocument document) => Kept<IReadOnlyDictionary<string, Door>>(document, "principal", () =>
        document.All<Door>()
            .Select((door, order) => (Door: door, Order: order))
            .Where(entry => document.FindType<DoorType>(entry.Door.TypeId) is { CurtainPanel: false } && OntoCommon(document, entry.Door))
            .Select(entry => (entry.Door, entry.Order, Name: DwellingSide(document, entry.Door) is { } room ? Dwellings.Of(room) : string.Empty,
                Elevation: document.FindLevel(document.Find<Wall>(entry.Door.HostWallId)?.LevelId ?? Guid.Empty)?.Elevation ?? 0))
            .Where(entry => entry.Name.Length > 0)
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(entry => entry.Elevation)
                .ThenByDescending(entry => Math.Round(Doors.ClearWidth(document.FindType<DoorType>(entry.Door.TypeId)!)))
                .ThenBy(entry => entry.Order)
                .First().Door, StringComparer.OrdinalIgnoreCase));

    /// <summary>What a door is to the way into a home - none where the project is not a block or a house checked to M4.</summary>
    public static Role RoleOf(BimDocument document, Door door)
    {
        if (document.FindType<DoorType>(door.TypeId) is not { CurtainPanel: false } type || type.Operation == DoorOperation.Overhead) return Role.None;
        if (!Block(document)) return House(document) && door == Visitable.Entrance(document) ? Role.PrivateEntrance : Role.None;
        if (door == CommunalEntrance(document)) return Role.CommunalEntrance;
        if (PrivateEntrance(document, door)) return Role.PrivateEntrance;
        if (Lifts.IsLanding(document, door)) return Role.None;

        // Between rooms of the common parts; or out of them, where an approach path comes to it.
        var (swing, other) = Doors.RoomsBeside(document, door);
        if (swing is not null && other is not null) return Common(document, swing) && Common(document, other) ? Role.CommunalDoor : Role.None;
        return (swing ?? other) is { } room && Common(document, room) && Doors.External(document, door, type) && Approaches.IsApproachedEntrance(document, door)
            ? Role.CommunalDoor : Role.None;
    }

    /// <summary>
    /// Whether a door is in a flat of a block, on that flat's entrance storey - the level of its principal private entrance - where M4(2) asks
    /// a nib beside every door (2.22d).
    /// </summary>
    public static bool OnFlatsEntranceStorey(BimDocument document, Door door)
    {
        if (!Block(document) || document.Find<Wall>(door.HostWallId) is not { } wall) return false;
        var (swing, other) = Doors.RoomsBeside(document, door);
        return new[] { swing, other }.Any(room => room is not null && Dwellings.Of(room) is { Length: > 0 } name
                                                   && Principal(document).TryGetValue(name, out var principal)
                                                   && document.Find<Wall>(principal.HostWallId)?.LevelId == wall.LevelId);
    }

    /// <summary>The paragraphs a role asks by under a standard; null where it asks nothing of the door.</summary>
    private static Asks? AsksOf(Role role, ClearanceStandard standard, bool block)
    {
        var (m1, m3) = (standard == ClearanceStandard.ApprovedDocumentM1, standard == ClearanceStandard.ApprovedDocumentM3);
        return role switch
        {
            Role.CommunalEntrance when m1 => new Asks("the principal communal entrance", 775, "M4(1) 1.9a, Diagram 1.1", "M4(1) 1.9b", null, null, 0, null, 0, null, null, null),
            Role.CommunalEntrance when m3 => new Asks("the principal communal entrance", 850, "M4(3) 3.14e, f", "M4(3) 3.14j", "M4(3) 3.14c", "M4(3) 3.14g", 1800,
                "M4(3) 3.14h", 1800, "M4(3) 3.14i", "M4(3) 3.14k", "M4(3) 3.14d"),
            Role.CommunalEntrance => new Asks("the principal communal entrance", 850, "M4(2) 2.14d, e", "M4(2) 2.14h", "M4(2) 2.14c", "M4(2) 2.14f", 1200,
                null, 0, "M4(2) 2.14g", "M4(2) 2.14i", null),
            Role.CommunalDoor when m1 => null,
            Role.CommunalDoor when m3 => new Asks("a communal door along the approach route", 850, "M4(3) 3.15, 3.14e, f", null, null, "M4(3) 3.15, 3.14g", 1800,
                "M4(3) 3.15, 3.14h", 1800, "M4(3) 3.15, 3.14i", null, null),
            Role.CommunalDoor => new Asks("a communal door along the approach route", 850, "M4(2) 2.15, 2.14d, e", null, null, "M4(2) 2.15, 2.14f", 1200,
                null, 0, "M4(2) 2.15, 2.14g", null, null),
            // A house's front door under M4(1) is Visitable's (1.14).
            Role.PrivateEntrance when m1 => block ? new Asks("the principal private entrance", 775, "M4(1) 1.14a, Diagram 1.1", null, null, null, 0, null, 0, null, null, null) : null,
            Role.PrivateEntrance when m3 => new Asks("the principal private entrance", 850, "M4(3) 3.22f, g", "M4(3) 3.22i", "M4(3) 3.22c", "M4(3) 3.22e", 1800,
                "M4(3) 3.22g", 1500, "M4(3) 3.22h", "M4(3) 3.22j", "M4(3) 3.22d"),
            Role.PrivateEntrance => new Asks("the principal private entrance", 850, "M4(2) 2.20d, e", "M4(2) 2.20h", "M4(2) 2.20c", "M4(2) 2.20f", 1200,
                null, 0, "M4(2) 2.20g", "M4(2) 2.20i", null),
            _ => null
        };
    }

    /// <summary>
    /// What a door falls short of as a home's way in - a block's principal communal entrance, another communal door, a dwelling's principal
    /// private entrance: its width, threshold, light, nibs and the width they make kept beyond them, reveal, lobby and the turning circle inside it.
    /// </summary>
    public static IReadOnlyList<(string Said, string Rule)> DoorProblems(BimDocument document, Door door, DoorType type, ClearanceStandard standard)
    {
        if (standard != document.ProjectInformation.ClearanceStandard || type.CurtainPanel) return Array.Empty<(string, string)>();
        var role = RoleOf(document, door);
        var block = Block(document);
        if (role == Role.None || AsksOf(role, standard, block) is not { } asks) return Array.Empty<(string, string)>();
        var problems = new List<(string, string)>();
        var clear = Doors.ClearWidth(type);
        var pair = type.Operation == DoorOperation.DoubleSwing || type.LeafCount >= 2;
        var swings = type.Operation is DoorOperation.Swing or DoorOperation.DoubleSwing;
        var external = Doors.External(document, door, type);
        var what = asks.What;

        // Wide enough: a pair's main leaf.
        if (clear < asks.Clear - 0.5)
            problems.Add(($"it gives {clear:0} clear{(pair ? " through its main leaf" : string.Empty)}.",
                $"Approved Document M ({asks.Width}) asks for {asks.Clear:0} clear through {what} - where there are double doors, through the main leaf."));

        // An accessible threshold, where it is outside: 15 high at most.
        if (external && asks.Threshold is { } threshold && Visitable.StepUp(document, door) is { } rise && rise > 15 + 0.5)
            problems.Add(($"it is {rise:0} up from the ground outside it.",
                $"Approved Document M ({threshold}) asks {what} for an accessible threshold - 15 high at most, the approach to it step-free."));

        // Lit from outside by a light switched on by itself.
        if (external && asks.Light is { } light && !Lit(document, door))
            problems.Add(("no light outside it is switched on by itself.",
                $"Approved Document M ({light}) asks for lighting at {what} by fully diffused luminaires switched on automatically by a dusk to dawn timer or by detecting motion - put up the Wall Light - Outdoor, Dusk to Dawn by it, or tick Dusk to Dawn or Motion on its light's type."));

        // A nib beside its leading edge, and the width it makes kept beyond it; under M4(3) the same beside its following edge.
        if (swings && !pair && asks.Lead is { } lead)
        {
            var rule = $"Approved Document M ({lead}) asks for a 300 nib beside the leading edge of {what}, on the side it opens to, and the width it makes kept for {asks.LeadKept:0} beyond it.";
            if (Doors.Nib(document, door, type) is { } nib && nib < 300 - 0.5)
                problems.Add(($"its leading edge is {nib:0} from the wall beside it.", rule));
            else if (Doors.NibKept(document, door, type, 300, asks.LeadKept) is { } kept)
                problems.Add(($"the 300 beside its leading edge runs {kept:0} out from it before a wall.", rule));
        }

        if (swings && !pair && asks.Follow is { } follow)
        {
            var rule = $"Approved Document M ({follow}) asks for a 200 nib beside the following edge of {what} - its hinge side, on the side it opens away from - and the width it makes kept for {asks.FollowKept:0} beyond it.";
            if (Doors.Nib(document, door, type, following: true) is { } tail && tail < 200 - 0.5)
                problems.Add(($"its following edge is {tail:0} from the wall beside it.", rule));
            else if (Doors.NibKept(document, door, type, 200, asks.FollowKept, following: true) is { } kept)
                problems.Add(($"the 200 beside its following edge runs {kept:0} out from it before a wall.", rule));
        }

        // The reveal on the side it opens to: 200 deep at most.
        if (swings && asks.Reveal is { } reveal && Doors.Reveal(document, door, type) is { } deep && deep > 200 + 0.5)
            problems.Add(($"its reveal on the side it opens to is {deep:0} deep, its frame set in the middle of the wall.",
                $"Approved Document M ({reveal}) asks the reveal on the leading side of {what} - usually the inside - 200 deep at most, from the door's face closed to the wall's face."));

        // The room it opens into: a lobby or porch's doors apart; under M4(3) a turning circle inside.
        var inside = role == Role.PrivateEntrance && block ? DwellingSide(document, door) : Inside(document, door);
        if (inside is not null && asks.Lobby is { } lobby && Lobby(document, door, inside, $"Approved Document M ({lobby}) asks, where there is a lobby or porch at {what}, for its doors 1500 apart at least and 1500 between their swings.") is { } lobbied)
            problems.Add(lobbied);
        if (inside is not null && asks.Turning is { } turning && !Doors.TurningRoom(document, door, type, inside))
            problems.Add(($"there is no room inside it, in the {inside.Name.ToLowerInvariant()}, for a 1500 turning circle.",
                $"Approved Document M ({turning}) asks for a 1500 clear turning circle inside {what}, in the entrance area in front of the door when closed - room in the {inside.Name.ToLowerInvariant()} 750 clear of its sides, or the Turning Circle component put down there."));

        return problems;
    }

    /// <summary>Whether a light switched on by itself stands outside a door, within 2.5 m of its outer face - not in a room.</summary>
    private static bool Lit(BimDocument document, Door door)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return true;
        var outward = SpaceSeparation.Outward(document, wall);
        var face = wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0) + outward * (wallType.Structure.TotalWidth / 2);
        return document.All<Component>().Any(piece => piece.LevelId == wall.LevelId
                                                       && document.FindType<ComponentType>(piece.TypeId) is { Kind: BuiltInCategory.LightingFixtures, AutoSwitched: true }
                                                       && piece.Location.DistanceTo(face) <= 2500 && RoomsOf.Containing(document, wall.LevelId, piece.Location) is null);
    }

    /// <summary>
    /// What a lobby or porch a door opens into falls short of: its other doors 1500 from it at least, and 1500 between their swings in it (2.14i,
    /// 2.20i, 3.14k, 3.22j) - said of the nearest that falls short; none where the room is not a lobby or porch, or meets it.
    /// </summary>
    private static (string Said, string Rule)? Lobby(BimDocument document, Door door, Room room, string rule)
    {
        if (!new[] { "lobby", "porch", "vestibule" }.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase))) return null;
        if (Face(document, door, room) is not { } face) return null;
        foreach (var other in document.All<Door>())
        {
            if (other == door || document.Find<Wall>(other.HostWallId)?.LevelId != room.LevelId) continue;
            if (Doors.RoomsBeside(document, other) is var (swing, beside) && swing != room && beside != room) continue;
            if (Face(document, other, room) is not { } otherFace) continue;
            var name = document.FindType<DoorType>(other.TypeId)?.Name ?? "its other door";
            var apart = Apart(face, otherFace);
            if (apart < 1500 - 0.5)
                return ($"it is {apart:0} from {name} across the {room.Name.ToLowerInvariant()}.", rule);
            var between = Apart(Swept(document, door, room, face), Swept(document, other, room, otherFace));
            if (between < 1500 - 0.5)
                return ($"there is {between:0} between its swing and that of {name} in the {room.Name.ToLowerInvariant()}.", rule);
        }

        return null;
    }

    /// <summary>A door's opening on its wall's face towards a room, as a line across it; null where the room is on neither side.</summary>
    private static IReadOnlyList<Point2D>? Face(BimDocument document, Door door, Room room)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType || document.FindType<DoorType>(door.TypeId) is not { } type) return null;
        if (room.GetBoundary(document) is not { IsEnclosed: true } boundary || boundary.Polygon.Count < 3) return null;
        var half = wallType.Structure.TotalWidth / 2;
        var (from, to) = door.GetSpan(type);
        foreach (var side in new[] { 1.0, -1.0 })
            if (Polygon2D.Contains(boundary.Polygon, wall.PointAt(wallType.Structure, door.DistanceAlongWall, side * (half + 150))))
                return new[] { wall.PointAt(wallType.Structure, from, side * half), wall.PointAt(wallType.Structure, to, side * half) };
        return null;
    }

    /// <summary>What a door sweeps in a room as it opens - its leaves' arcs there - or, opening the other way, its face on the room's side.</summary>
    private static IReadOnlyList<Point2D> Swept(BimDocument document, Door door, Room room, IReadOnlyList<Point2D> face)
    {
        if (room.GetBoundary(document) is not { IsEnclosed: true } boundary) return face;
        var sweeps = DoorSwing.Sweeps(document, door)
            .Where(sweep => sweep.Count >= 3 && Polygon2D.Contains(boundary.Polygon, new Point2D(sweep.Average(point => point.X), sweep.Average(point => point.Y))))
            .ToList();
        return sweeps.Count == 0 ? face : sweeps.SelectMany(sweep => sweep).ToList();
    }

    /// <summary>How far apart two shapes are - lines or outlines, by their points and edges - 0 where one reaches into the other, mm.</summary>
    private static double Apart(IReadOnlyList<Point2D> one, IReadOnlyList<Point2D> two)
    {
        static IEnumerable<(Point2D, Point2D)> Edges(IReadOnlyList<Point2D> shape) =>
            shape.Count == 2 ? new[] { (shape[0], shape[1]) } : Enumerable.Range(0, shape.Count).Select(i => (shape[i], shape[(i + 1) % shape.Count]));
        if (one.Count >= 3 && two.Any(point => Polygon2D.Contains(one, point)) || two.Count >= 3 && one.Any(point => Polygon2D.Contains(two, point))) return 0;
        var near = one.Min(point => Edges(two).Min(edge => Line2D.DistanceFromSegment(point, edge.Item1, edge.Item2)));
        return Math.Min(near, two.Min(point => Edges(one).Min(edge => Line2D.DistanceFromSegment(point, edge.Item1, edge.Item2))));
    }

    /// <summary>
    /// What a block falls short of as a whole: under M4(2) and M4(3), a flat entered on a storey other than the communal entrance's that no lift
    /// stops at - the approach to it not step-free (2.6, 3.7) - said of the first flat's door there.
    /// </summary>
    public static IReadOnlyList<(Element Element, string Said)> Problems(BimDocument document)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        if (!Block(document) || standard is not (ClearanceStandard.ApprovedDocumentM or ClearanceStandard.ApprovedDocumentM3)) return Array.Empty<(Element, string)>();
        if (CommunalEntrance(document) is not { } entrance || document.Find<Wall>(entrance.HostWallId) is not { } entranceWall) return Array.Empty<(Element, string)>();
        var lifts = Lifts.Of(document);
        var (paragraph, lift) = standard == ClearanceStandard.ApprovedDocumentM3 ? ("M4(3) 3.7", "3.16") : ("M4(2) 2.6", "2.16");
        var found = new List<(Element, string)>();
        foreach (var storey in document.All<Door>().Where(door => PrivateEntrance(document, door))
                     .GroupBy(door => document.Find<Wall>(door.HostWallId)?.LevelId)
                     .Where(group => group.Key is { } level && level != entranceWall.LevelId)
                     .OrderBy(group => document.FindLevel(group.Key!.Value)?.Elevation ?? 0))
        {
            if (lifts.Any(each => each.Landings.Any(landing => landing.Level.Id == storey.Key))) continue;
            var door = storey.First();
            var flat = DwellingSide(document, door) is { } room ? Dwellings.Of(room) : "a flat";
            var level = document.FindLevel(storey.Key!.Value)?.Name ?? "another storey";
            found.Add((door, $"{flat}'s entrance is on the {level}, where no lift stops: Approved Document M ({paragraph}) asks the approach route to a dwelling to be step-free whatever storey it is on - a passenger lift ({lift}) with a landing door there."));
        }

        return found;
    }

    /// <summary>A fact about the whole project, found once while the model is unchanged.</summary>
    private static T Kept<T>(BimDocument document, string what, Func<T> work) =>
        ModelCache.Of(document) is { } cache ? cache.Keep($"entrances: {what}", document, null, null, work) : work();
}
