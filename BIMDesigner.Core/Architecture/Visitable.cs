using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A house's entrance and the storey visitors use, as Approved Document M Volume 1 has them (round 148): the principal private entrance - the
/// door a visitor not familiar with the house would approach, its front door (Appendix A) - taken as the outside door an approach path comes
/// to, or with none drawn, the outside door into a hall; the entrance storey, its level; and the principal storey, where the main living space
/// is, where the entrance storey has no habitable room.
///
/// A visitable dwelling, M4(1): a room with a WC - a cloakroom or a bathroom - on the entrance storey, or on the principal storey where the
/// entrance storey has no habitable room (1.17a), its door opening outwards (1.17d); 775 clear through the principal private entrance (1.14a);
/// 750 at least through the doors to the habitable rooms and the room with the WC on that storey (1.15a, Table 1.1). An accessible and
/// adaptable one, M4(2): a room on the entrance storey with a WC and a basin (2.27a), its door opening outwards (2.27d).
///
/// Not done: a block of flats - a flat's entrance off the common parts, and its own entrance storey - and two or more houses drawn in one
/// model, which are taken together; the hall widths of Table 1.1 and localised obstructions (1.15b); thresholds and a step at the door (1.14b, c).
/// An extension is left out, as Volume 1 is (0.2).
/// </summary>
public static class Visitable
{
    /// <summary>Whether these rules reach the project: a house in England, checked to M4(1) or M4(2), not an extension.</summary>
    public static bool Applies(BimDocument document)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        if (standard is not (ClearanceStandard.ApprovedDocumentM1 or ClearanceStandard.ApprovedDocumentM)) return false;
        if (FireDesign.UseOf(document) != BuildingUse.House || !Domestic.Rules(document)) return false;
        return !document.ProjectInformation.BuildingType.Contains("extension", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The principal private entrance: of the outside doors - not a garage's - the one an approach path comes to, or with none, one into a
    /// hall; then the lowest, the widest, the first drawn. Null with no outside door.
    /// </summary>
    public static Door? Entrance(BimDocument document) => Kept<Door?>(document, "entrance", () =>
    {
        var outside = document.All<Door>()
            .Select((door, order) => (Door: door, Order: order, Type: document.FindType<DoorType>(door.TypeId)))
            .Where(entry => entry.Type is { CurtainPanel: false } type && type.Operation != DoorOperation.Overhead && Doors.External(document, entry.Door, type)
                            && document.Find<Wall>(entry.Door.HostWallId) is not null
                            && !IntoGarage(document, entry.Door))
            .ToList();
        if (outside.Count == 0) return null;

        bool IntoHall(Door door) => Doors.RoomsBeside(document, door) is var (swing, other)
                                    && new[] { swing, other }.Any(room => room is not null && room.GetBoundary(document) is { IsEnclosed: true } boundary
                                                                          && Lighting.KindOf(document, room, boundary.Polygon) == LitRoom.Hall);

        return outside
            .OrderByDescending(entry => Approaches.IsApproachedEntrance(document, entry.Door))
            .ThenByDescending(entry => IntoHall(entry.Door))
            .ThenBy(entry => document.FindLevel(document.Find<Wall>(entry.Door.HostWallId)!.LevelId)?.Elevation ?? 0)
            .ThenByDescending(entry => Math.Round(Doors.ClearWidth(entry.Type!)))
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Door)
            .First();
    });

    /// <summary>The entrance storey: the level the principal private entrance is on (Appendix A).</summary>
    public static Guid? EntranceStorey(BimDocument document) =>
        Entrance(document) is { } door ? document.Find<Wall>(door.HostWallId)?.LevelId : null;

    /// <summary>
    /// The storeys Section 1B reaches (1.13): the entrance storey - and, where it has no habitable room, the principal storey, the level with
    /// the main living space, the biggest living room, or failing one the biggest habitable room.
    /// </summary>
    public static IReadOnlyList<Guid> Storeys(BimDocument document) => Kept<IReadOnlyList<Guid>>(document, "storeys", () =>
    {
        if (EntranceStorey(document) is not { } entrance) return Array.Empty<Guid>();
        var rooms = document.All<Room>()
            .Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3)
            .Select(entry => (entry.Room, entry.Boundary.Area, Kind: Lighting.KindOf(document, entry.Room, entry.Boundary.Polygon)))
            .Where(entry => Habitable(entry.Kind))
            .ToList();
        if (rooms.Count == 0 || rooms.Any(entry => entry.Room.LevelId == entrance)) return new[] { entrance };

        var principal = rooms
            .OrderByDescending(entry => entry.Kind is LitRoom.Living or LitRoom.Family)
            .ThenByDescending(entry => entry.Area)
            .First().Room.LevelId;
        return new[] { entrance, principal };
    });

    /// <summary>
    /// The storey visitors use, where Section 1B's WC and doors are asked for: the entrance storey, or the principal storey where the entrance
    /// storey has no habitable room. Null with no outside door.
    /// </summary>
    public static Guid? Storey(BimDocument document) => Storeys(document) is { Count: > 0 } storeys ? storeys[^1] : null;

    /// <summary>A habitable room, as Appendix A has one: a room for living in, a kitchen too - not a bathroom, a utility room, a hall, a stair or a garage.</summary>
    public static bool Habitable(LitRoom kind) => kind is LitRoom.Living or LitRoom.Family or LitRoom.Dining or LitRoom.Kitchen or LitRoom.Study or LitRoom.Bedroom;

    /// <summary>Whether a room is habitable, as Appendix A has it.</summary>
    public static bool Habitable(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary && boundary.Polygon.Count >= 3 && Habitable(Lighting.KindOf(document, room, boundary.Polygon));

    /// <summary>The fittings standing in a room.</summary>
    private static IEnumerable<ComponentType> Fittings(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary && boundary.Polygon.Count >= 3
            ? document.All<Component>()
                .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location))
                .Select(piece => document.FindType<ComponentType>(piece.TypeId))
                .OfType<ComponentType>()
            : Enumerable.Empty<ComponentType>();

    /// <summary>
    /// Whether a room is the one the standard asks for on the storey visitors use: one with a WC - under M4(2), with a WC and a basin.
    /// </summary>
    public static bool IsWcRoom(BimDocument document, Room room, ClearanceStandard standard)
    {
        var fittings = Fittings(document, room).ToList();
        return fittings.Any(Bathrooms.IsWc) && (standard == ClearanceStandard.ApprovedDocumentM1 || fittings.Any(type => type.Form == ComponentForm.Basin));
    }

    /// <summary>The storeys the WC room may be on: under M4(1) the entrance storey or the principal storey (1.17a); under M4(2) the entrance storey (2.27a).</summary>
    private static IReadOnlyList<Guid> WcStoreys(BimDocument document, ClearanceStandard standard) =>
        standard == ClearanceStandard.ApprovedDocumentM1 ? Storeys(document)
        : EntranceStorey(document) is { } entrance ? new[] { entrance } : Array.Empty<Guid>();

    /// <summary>The rooms with a WC - under M4(2), a WC and a basin - on the storeys it may be on.</summary>
    private static IReadOnlyList<Room> WcRooms(BimDocument document, ClearanceStandard standard) => Kept<IReadOnlyList<Room>>(document, $"wc rooms {standard}", () =>
    {
        var storeys = WcStoreys(document, standard);
        return document.All<Room>().Where(room => storeys.Contains(room.LevelId) && IsWcRoom(document, room, standard)).ToList();
    });

    /// <summary>The doors into a room, and whether each opens into it.</summary>
    private static IEnumerable<(Door Door, bool Into)> DoorsOf(BimDocument document, Room room) =>
        document.All<Door>()
            .Where(door => document.Find<Wall>(door.HostWallId)?.LevelId == room.LevelId)
            .Select(door => (Door: door, Rooms: Doors.RoomsBeside(document, door)))
            .Where(entry => entry.Rooms.Swing == room || entry.Rooms.Other == room)
            .Select(entry => (entry.Door, entry.Rooms.Swing == room));

    /// <summary>Whether a door is into a garage, either side.</summary>
    private static bool IntoGarage(BimDocument document, Door door) =>
        Doors.RoomsBeside(document, door) is var (swing, other) && new[] { swing, other }.Any(room => room is not null && Doors.Garage(document, room));

    /// <summary>
    /// What the house as a whole falls short of: no room with a WC on the storey visitors use - under M4(2), with a WC and a basin on the
    /// entrance storey - said of the principal private entrance.
    /// </summary>
    public static IReadOnlyList<(Element Element, string Said)> Problems(BimDocument document)
    {
        if (!Applies(document) || Entrance(document) is not { } entrance) return Array.Empty<(Element, string)>();
        var standard = document.ProjectInformation.ClearanceStandard;
        var storeys = WcStoreys(document, standard);
        if (!document.All<Room>().Any(room => storeys.Contains(room.LevelId)) || WcRooms(document, standard).Count > 0) return Array.Empty<(Element, string)>();

        var names = string.Join(" or ", storeys.Select(id => document.FindLevel(id)?.Name ?? "the storey").Distinct());
        return new (Element, string)[]
        {
            (entrance, standard == ClearanceStandard.ApprovedDocumentM1
                ? $"no room on {names} has a WC: Approved Document M (M4(1) 1.17a) asks for a room with a WC - a cloakroom or a bathroom - on the entrance storey, or on the principal storey where the entrance storey has no habitable room."
                : $"no room on {names} has a WC and a basin: Approved Document M (M4(2) 2.27a) asks for a room on the entrance storey with a WC and a basin - a cloakroom or a bathroom.")
        };
    }

    /// <summary>
    /// What a door falls short of as the visitable dwelling's - the principal private entrance's width, a habitable room's or the WC room's
    /// door's width on the storey visitors use - and, under M4(1) or M4(2), whether the WC room's door opens outwards.
    /// </summary>
    public static IReadOnlyList<(string Said, string Rule)> DoorProblems(BimDocument document, Door door, DoorType type, ClearanceStandard standard)
    {
        if (!Applies(document) || standard != document.ProjectInformation.ClearanceStandard) return Array.Empty<(string, string)>();
        if (document.Find<Wall>(door.HostWallId) is not { } wall) return Array.Empty<(string, string)>();
        var problems = new List<(string, string)>();
        var clear = Doors.ClearWidth(type);
        var external = Doors.External(document, door, type);
        var pair = type.Operation == DoorOperation.DoubleSwing || type.LeafCount >= 2;
        var (swing, other) = Doors.RoomsBeside(document, door);

        if (standard == ClearanceStandard.ApprovedDocumentM1)
        {
            // The front door: 775 clear (1.14a, Diagram 1.1) - a standard 826 leaf up to 44 thick is taken to give it.
            if (external && door == Entrance(document) && clear < 775 - 0.5)
                problems.Add(($"it gives {clear:0} clear{(pair ? " through its main leaf" : string.Empty)}.",
                    "Approved Document M (M4(1) 1.14a, Diagram 1.1) asks for 775 clear through the principal private entrance - a standard 826 leaf up to 44 thick is taken to give it."));

            // A door to a habitable room or to the room with the WC, on the storey visitors use: 750 at least (1.15a, Table 1.1).
            if (!external && Storeys(document).Contains(wall.LevelId) && clear < 750 - 0.5
                && new[] { swing, other }.Any(room => room is not null && (Habitable(document, room) || IsWcRoom(document, room, standard))))
                problems.Add(($"it gives {clear:0} clear.",
                    "Approved Document M (M4(1) 1.15a, Table 1.1) asks for 750 clear at least through a door to a habitable room or to the room with the WC on the entrance storey - 775 off a hall narrower than 1200 and 800 off one narrower than 1050, the door approached from the side."));
        }

        // The WC room's door opens outwards (1.17d, 2.27d) - unless another room with a WC on the storeys it may be on has only doors opening out of it.
        if (!external && swing is not null && WcRooms(document, standard).Contains(swing)
            && !WcRooms(document, standard).Any(room => room != swing && DoorsOf(document, room).All(entry => !entry.Into) && DoorsOf(document, room).Any()))
            problems.Add(($"it opens into {swing.Name}, the room with the WC{(standard == ClearanceStandard.ApprovedDocumentM ? " and basin" : string.Empty)} on {document.FindLevel(swing.LevelId)?.Name ?? "the entrance storey"}.",
                standard == ClearanceStandard.ApprovedDocumentM1
                    ? "Approved Document M (M4(1) 1.17d) asks the door to the room with the WC to open outwards - Flip Facing in Properties."
                    : "Approved Document M (M4(2) 2.27d) asks the door to the entrance storey's room with the WC and basin to open outwards - Flip Facing in Properties."));
        return problems;
    }

    /// <summary>A fact about the whole house, found once while the model is unchanged.</summary>
    private static T Kept<T>(BimDocument document, string what, Func<T> work) =>
        ModelCache.Of(document) is { } cache ? cache.Keep($"visitable: {what}", document, null, null, work) : work();
}
