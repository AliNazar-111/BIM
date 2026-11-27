using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Views;

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
/// Round 157: the hall in front of each such door wide enough for it by Table 1.1 - approached head on, 900; from the side, 1200 for a 750
/// door, 1050 for a 775, 900 for an 800 - and M4(2)'s every door off a hall or landing by Table 2.1, the same, a hall 900 at a door at least
/// (2.22a, c), what stands in it taken off (1.15b, 2.22b); a step up into the principal private entrance 150 at most (1.14c).
///
/// Round 158: a hall's or landing's narrowest width between its doors and stairs - the widest one can pass from each to every other - its
/// walls, guards and steps alone 900 at least under M4(2) (2.22a), and what stands in it, a radiator or a fitting, leaving 750 under M4(1) on
/// the entrance storey and M4(2) on every storey (1.15b, 2.22b).
///
/// Round 159: M4(3)'s halls 1050, 900 past a localised obstruction (3.24a, c, Diagram 3.4); and in all three, an obstruction no longer than
/// 2 m, not opposite or close to a doorway - over the 1500 of hall centred on it, Diagrams 2.3 and 3.4 shading it (1.15b, 2.22b, 3.24c).
///
/// Round 162: nor at a change of direction - within 300 of an inside corner where the hall turns, as Diagram 2.3 shades it.
///
/// Not done: a block of flats - a flat's entrance off the common parts, and its own entrance storey - and two or more houses drawn in one
/// model, which are taken together; the threshold itself (1.14b), not drawn. An extension is left out, as Volume 1 is (0.2).
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
        // A wheelchair user dwelling: its storage and transfer space on the entrance storey (3.25a, round 160).
        if (document.ProjectInformation.ClearanceStandard == ClearanceStandard.ApprovedDocumentM3)
            return WheelchairStore(document) is { } wanting ? new[] { wanting } : Array.Empty<(Element, string)>();
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

            // The step up into it: 150 at most, at the threshold's outside face (1.14c, round 157).
            if (external && door == Entrance(document) && StepUp(document, door) is { } rise && rise > 150 + 0.5)
                problems.Add(($"it is {rise:0} up from the ground outside it.",
                    "Approved Document M (M4(1) 1.14b, c) asks the principal private entrance for an accessible threshold - level, or 15 high at most - and, where a step into the dwelling cannot be avoided, a rise of 150 at most, at the threshold's outside face."));

            // A door to a habitable room or to the room with the WC, on the storey visitors use: 750 at least, and as Table 1.1 asks off the
            // hall it opens off - 900 wide approached head on; from the side 1200 for 750, 1050 for 775, 900 for 800 (1.15a, round 157).
            var reached = !external && Storeys(document).Contains(wall.LevelId)
                && new[] { swing, other }.Any(room => room is not null && (Habitable(document, room) || IsWcRoom(document, room, standard)));
            const string table = "Approved Document M (M4(1) 1.15a, Table 1.1) asks a door to a habitable room or to the room with the WC on the entrance storey for 750 clear at least, off a hall 900 wide approached head on; approached from the side, 750 off a hall 1200 wide, 775 off 1050, 800 off 900 - a radiator or fitting not opposite it (1.15b).";
            if (reached && clear < 750 - 0.5)
                problems.Add(($"it gives {clear:0} clear.", table));
            else if (reached && HallProblem(document, door, type, table) is { } narrow)
                problems.Add(narrow);
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

    // ---- the hall in front of a door (Table 1.1, Table 2.1, round 157) ---------------------------------------------------------------

    /// <summary>
    /// The hall a door is approached from - the room either side of it that is a hall, landing, corridor or passage - and how: head on, the
    /// hall running on ahead of the door further than it is wide; or from the side. Its clear width there, mm: across the hall a little way
    /// in front of a door approached head on; straight out from a door approached from the side, to the far wall or whatever stands in the
    /// way - a radiator, a fitting, a stair's steps (1.15b, 2.22b). None where neither side is a hall.
    /// </summary>
    public static (Room Hall, bool HeadOn, double Width)? HallAt(BimDocument document, Door door)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return null;
        var (swing, other) = Doors.RoomsBeside(document, door);
        var hall = new[] { swing, other }.FirstOrDefault(room => room is not null && FireSeparation.Circulation(room) && !room.Name.Contains("stair", StringComparison.OrdinalIgnoreCase));
        if (hall is null || hall.GetBoundary(document) is not { IsEnclosed: true } boundary || boundary.Polygon.Count < 3) return null;
        var outline = boundary.Polygon;

        var half = wallType.Structure.TotalWidth / 2;
        var middle = wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0);
        var into = wall.PointAt(wallType.Structure, door.DistanceAlongWall, half + 150);
        var normal = (into - middle).NormalisedOrDefault(Vector2D.UnitX);
        if (!Polygon2D.Contains(outline, into))
        {
            normal = new Vector2D(-normal.X, -normal.Y);
            if (!Polygon2D.Contains(outline, middle + normal * (half + 150))) return null;
        }

        var along = new Vector2D(-normal.Y, normal.X);
        var face = middle + normal * half;

        // The hall's sides and what stands in it: fittings low enough to be in the way, columns, the steps of a stair on the storey.
        var edges = Enumerable.Range(0, outline.Count).Select(i => (outline[i], outline[(i + 1) % outline.Count])).ToList();
        void Add(IReadOnlyList<Point2D> shape) => edges.AddRange(Enumerable.Range(0, shape.Count).Select(i => (shape[i], shape[(i + 1) % shape.Count])));
        var floor = document.FindLevel(hall.LevelId)?.Elevation ?? 0;
        foreach (var piece in document.All<Component>().Where(piece => piece.LevelId == hall.LevelId && Polygon2D.Contains(outline, piece.Location)))
            if (document.FindType<ComponentType>(piece.TypeId) is { Depth: >= 50 } pieceType && ComponentHosting.BaseElevation(document, piece, pieceType) - floor < 2000)
                Add(ComponentModel.Footprint(pieceType, ComponentModel.FrameOf(piece, 0)));
        foreach (var column in document.All<Column>().Where(column => column.LevelId == hall.LevelId))
            if (document.FindType<ColumnType>(column.TypeId) is { } columnType && column.Outline(columnType) is { Count: >= 3 } shape && shape.Any(point => Polygon2D.Contains(outline, point)))
                Add(shape);
        foreach (var stair in document.All<Stair>())
        foreach (var flight in Stairs.Flights(document, stair).Where(flight => flight.LevelId == hall.LevelId))
        foreach (var step in flight.Layout.Steps.Where(step => step.Outline.Count >= 3 && step.Outline.Any(point => Polygon2D.Contains(outline, point))))
            Add(step.Outline);

        double Ray(Point2D from, Vector2D way)
        {
            var best = 20000.0;
            foreach (var (p, q) in edges)
            {
                var edge = q - p;
                var denominator = way.Cross(edge);
                if (Math.Abs(denominator) < 1e-9) continue;
                var t = (p - from).Cross(edge) / denominator;
                var u = (p - from).Cross(way) / denominator;
                if (t > 0.5 && u >= -1e-9 && u <= 1 + 1e-9) best = Math.Min(best, t);
            }

            return best;
        }

        var forward = Ray(face, normal);
        var probe = face + normal * Math.Min(600, forward / 2);
        var lateral = Ray(probe, along) + Ray(probe, new Vector2D(-along.X, -along.Y));
        return forward > lateral + 1 ? (hall, true, lateral) : (hall, false, forward);
    }

    // ---- a hall's narrowest width (1.15b, 2.22a, b, round 158) ------------------------------------------------------------------------

    /// <summary>Whether a room is a hall in a home's sense: a hall, landing, corridor, passage or lobby - not a stair, a porch, or a bathroom off one.</summary>
    public static bool IsHall(Room room) =>
        new[] { "hall", "landing", "corridor", "passage", "lobby" }.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
        && !new[] { "stair", "porch" }.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase)) && !Ventilation.WetByName(room.Name);

    /// <summary>
    /// The narrowest a hall is where one passes through it - the widest one can go from each of its doors, and the foot or head of each stair
    /// in it, to every other - mm: between its walls, guards and steps alone; and with what stands in it too, fittings under 2000 and columns.
    /// The floor's holes - a stairwell - are no way through. None where fewer than two ways lead into it.
    /// </summary>
    public static (double Walls, double Clear)? Narrowest(BimDocument document, Room hall) =>
        Kept(document, $"narrowest {hall.Id}", () => Measure(document, hall));

    private static (double Walls, double Clear)? Measure(BimDocument document, Room hall)
    {
        if (hall.GetBoundary(document) is not { IsEnclosed: true } boundary || boundary.Polygon.Count < 3) return null;
        var outline = boundary.Polygon;
        var floorAt = document.FindLevel(hall.LevelId)?.Elevation ?? 0;

        // What fixes its width: its sides, guards, and the steps of stairs on its floor; what stands in it as well.
        var fixedEdges = Enumerable.Range(0, outline.Count).Select(i => (outline[i], outline[(i + 1) % outline.Count])).ToList();
        var solids = new List<IReadOnlyList<Point2D>>();
        foreach (var railing in document.All<Railing>().Where(railing => railing.LevelId == hall.LevelId && railing.Path.Count >= 2))
            for (var i = 0; i + 1 < railing.Path.Count; i++) fixedEdges.Add((railing.Path[i], railing.Path[i + 1]));
        var targets = new List<Point2D>();
        foreach (var stair in document.All<Stair>())
        foreach (var flight in Stairs.Flights(document, stair))
        {
            var steps = flight.Layout.Steps.Where(step => step.Outline.Count >= 3).ToList();
            if (steps.Count == 0) continue;
            if (flight.LevelId == hall.LevelId)
            {
                foreach (var step in steps.Where(step => step.Outline.Any(point => Polygon2D.Contains(outline, point))))
                {
                    solids.Add(step.Outline);
                    fixedEdges.AddRange(Enumerable.Range(0, step.Outline.Count).Select(i => (step.Outline[i], step.Outline[(i + 1) % step.Outline.Count])));
                }

                // Its foot: in front of its first riser.
                var first = steps[0];
                var foot = first.RiserFrom.MidpointTo(first.RiserTo) - first.Up * 450;
                if (Polygon2D.Contains(outline, foot)) targets.Add(foot);
            }

            // Its head on this floor: beyond its top step.
            if (flight.TopLevelId == hall.LevelId)
            {
                var last = steps[^1];
                var far = last.Outline.OrderByDescending(point => (point - last.RiserFrom).Dot(last.Up)).Take(2).ToList();
                var head = far[0].MidpointTo(far[1]) + last.Up * 450;
                if (Polygon2D.Contains(outline, head)) targets.Add(head);
            }
        }

        var standing = new List<IReadOnlyList<Point2D>>();
        foreach (var piece in document.All<Component>().Where(piece => piece.LevelId == hall.LevelId && Polygon2D.Contains(outline, piece.Location)))
            if (document.FindType<ComponentType>(piece.TypeId) is { Depth: >= 50 } pieceType && ComponentHosting.BaseElevation(document, piece, pieceType) - floorAt < 2000)
                standing.Add(ComponentModel.Footprint(pieceType, ComponentModel.FrameOf(piece, 0)));
        foreach (var column in document.All<Column>().Where(column => column.LevelId == hall.LevelId))
            if (document.FindType<ColumnType>(column.TypeId) is { } columnType && column.Outline(columnType) is { Count: >= 3 } shape && shape.Any(point => Polygon2D.Contains(outline, point)))
                standing.Add(shape);
        var standingEdges = standing.SelectMany(shape => Enumerable.Range(0, shape.Count).Select(i => (shape[i], shape[(i + 1) % shape.Count]))).ToList();

        // Its doors: a point in front of each, as far in as half the hall there, up to 450.
        foreach (var door in document.All<Door>())
        {
            if (document.Find<Wall>(door.HostWallId) is not { } wall || wall.LevelId != hall.LevelId || document.GetWallType(wall) is not { } wallType) continue;
            var (swing, other) = Doors.RoomsBeside(document, door);
            if (swing != hall && other != hall) continue;
            var half = wallType.Structure.TotalWidth / 2;
            var middle = wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0);
            var normal = (wall.PointAt(wallType.Structure, door.DistanceAlongWall, half + 150) - middle).NormalisedOrDefault(Vector2D.UnitX);
            if (!Polygon2D.Contains(outline, middle + normal * (half + 150))) normal = new Vector2D(-normal.X, -normal.Y);
            targets.Add(middle + normal * (half + 150));
        }

        if (targets.Count < 2) return null;

        // The floor under it, its holes no way through.
        var floors = document.All<Floor>().Where(floor => floor.LevelId == hall.LevelId && floor.Boundary.Count >= 3).ToList();
        bool Walkable(Point2D point) => Polygon2D.Contains(outline, point) && (floors.Count == 0 || floors.Any(floor => floor.Contains(point)))
                                        && !solids.Any(shape => Polygon2D.Contains(shape, point));

        const double cell = 25;
        var (x0, x1) = (outline.Min(point => point.X), outline.Max(point => point.X));
        var (y0, y1) = (outline.Min(point => point.Y), outline.Max(point => point.Y));
        var (nx, ny) = ((int)Math.Ceiling((x1 - x0) / cell), (int)Math.Ceiling((y1 - y0) / cell));
        if (nx < 1 || ny < 1 || (long)nx * ny > 400_000) return null;
        Point2D At(int i, int j) => new(x0 + (i + 0.5) * cell, y0 + (j + 0.5) * cell);
        static double Nearest(Point2D point, List<(Point2D, Point2D)> edges) =>
            edges.Count == 0 ? double.MaxValue : edges.Min(edge => Line2D.DistanceFromSegment(point, edge.Item1, edge.Item2));

        var walls = new double[nx, ny];
        var clear = new double[nx, ny];
        for (var i = 0; i < nx; i++)
        for (var j = 0; j < ny; j++)
        {
            var point = At(i, j);
            if (!Walkable(point)) { walls[i, j] = clear[i, j] = -1; continue; }
            walls[i, j] = Nearest(point, fixedEdges);
            clear[i, j] = standing.Any(shape => Polygon2D.Contains(shape, point)) ? -1 : Math.Min(walls[i, j], Nearest(point, standingEdges));
        }

        double Bottleneck(double[,] room)
        {
            // Each way in: the roomiest cell within 600 of it.
            var ends = new List<int>();
            foreach (var target in targets)
            {
                var (best, value) = (-1, 0.0);
                for (var i = 0; i < nx; i++)
                for (var j = 0; j < ny; j++)
                    if (room[i, j] > value && At(i, j).DistanceTo(target) <= 600) (best, value) = (i * ny + j, room[i, j]);
                if (best >= 0) ends.Add(best);
            }

            if (ends.Count < 2) return double.MaxValue;
            var parent = Enumerable.Range(0, nx * ny).ToArray();
            int Root(int k) { while (parent[k] != k) k = parent[k] = parent[parent[k]]; return k; }
            var on = new bool[nx * ny];
            foreach (var k in Enumerable.Range(0, nx * ny).Where(k => room[k / ny, k % ny] >= 0).OrderByDescending(k => room[k / ny, k % ny]))
            {
                on[k] = true;
                var (i, j) = (k / ny, k % ny);
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var (a, b) = (i + di, j + dj);
                    if (a < 0 || b < 0 || a >= nx || b >= ny || !on[a * ny + b]) continue;
                    parent[Root(a * ny + b)] = Root(k);
                }

                if (ends.All(end => on[end]) && ends.Select(Root).Distinct().Count() == 1) return 2 * room[i, j];
            }

            return 0;
        }

        // Widths to the nearest 10: a cell's middle lies within half a cell of the hall's middle line, so twice its clearance is short of the
        // width by a cell at most - given back, the width is read no narrower than it is, and no more than a cell wider.
        double Width(double measured) => measured >= double.MaxValue / 2 ? double.MaxValue : Math.Round((measured + cell) / 10) * 10;
        return (Width(Bottleneck(walls)), Width(Bottleneck(clear)));
    }

    /// <summary>
    /// What a home's hall comes to - "Hall: 1020 clear at its narrowest, 910 past what stands in it" - with what Approved Document M asks of it
    /// that it falls short of: under M4(2), 900 between its walls (2.22a) and 750 past a radiator or fitting (2.22b), on every storey; under
    /// M4(1), 750 past what stands in it on the entrance storey (1.15b). Null for any other room, or where the rules do not reach it.
    /// </summary>
    public static string? HallVerdict(BimDocument document, Room room)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        if (standard is not (ClearanceStandard.ApprovedDocumentM or ClearanceStandard.ApprovedDocumentM1 or ClearanceStandard.ApprovedDocumentM3) || !IsHall(room) || !Domestic.Rules(document, room)) return null;
        if (standard == ClearanceStandard.ApprovedDocumentM1 && (!Applies(document) || !Storeys(document).Contains(room.LevelId))) return null;
        if (Narrowest(document, room) is not var (walls, clear) || walls >= double.MaxValue / 2) return null;

        // Its widths, by the category: M4(1) asks no hall width but 750 past what stands in it; M4(2) 900 and 750; M4(3) 1050 and 900.
        var (paragraph, wide, past) = standard switch
        {
            ClearanceStandard.ApprovedDocumentM1 => ("M4(1) 1.15b", 0.0, 750.0),
            ClearanceStandard.ApprovedDocumentM => ("M4(2) 2.22", 900.0, 750.0),
            _ => ("M4(3) 3.24", 1050.0, 900.0)
        };
        var said = new List<string> { $"{walls:0} clear at its narrowest{(clear < walls - 5 ? $", {clear:0} past what stands in it" : string.Empty)}" };
        if (wide > 0 && walls < wide - 0.5)
            said.Add($"Approved Document M ({paragraph}a) asks every hall and landing {wide:0} clear - it is {walls:0} between its walls at its narrowest");
        if (clear < past - 0.5 && !(walls < past - 0.5))
            said.Add($"Approved Document M ({paragraph}{(standard == ClearanceStandard.ApprovedDocumentM1 ? string.Empty : standard == ClearanceStandard.ApprovedDocumentM ? "b" : "c, Diagram 3.4")}) asks a hall never narrower than {past:0} past a radiator or fitting in it - it is {clear:0} past what stands in it");

        // What stands in it: no longer than 2 m, and not opposite or close to a doorway - over the 1500 of hall centred on it (Diagrams 2.3, 3.4).
        var rule = standard == ClearanceStandard.ApprovedDocumentM1 ? "M4(1) 1.15b" : standard == ClearanceStandard.ApprovedDocumentM ? "M4(2) 2.22b, Diagram 2.3" : "M4(3) 3.24c, Diagram 3.4";
        foreach (var (name, footprint) in Obstructions(document, room))
        {
            var longest = Enumerable.Range(0, footprint.Count).Max(i => footprint[i].DistanceTo(footprint[(i + 1) % footprint.Count]));
            if (longest > 2000 + 0.5)
                said.Add($"Approved Document M ({rule}) asks a localised obstruction no longer than 2 m - {name} is {longest:0} long");
            if (DoorwayNear(document, room, footprint) is { } doorway)
                said.Add($"Approved Document M ({rule}) asks no localised obstruction opposite or close to a doorway - {name} stands in front of {doorway}");
            else if (AtTurn(document, room, footprint) is { } apart)
                said.Add($"Approved Document M ({rule}) asks no localised obstruction at a change of direction - {name} stands {apart:0} from the inside corner where the hall turns");
        }

        return $"Hall: {string.Join("; ", said)}";
    }

    /// <summary>
    /// What stands in a hall as a localised obstruction, a radiator's like: fittings under 2000 and 100 deep or more - not a socket's or a
    /// network box's few centimetres - by name and footprint.
    /// </summary>
    private static IEnumerable<(string Name, IReadOnlyList<Point2D> Footprint)> Obstructions(BimDocument document, Room hall)
    {
        if (hall.GetBoundary(document) is not { IsEnclosed: true } boundary || boundary.Polygon.Count < 3) yield break;
        var floorAt = document.FindLevel(hall.LevelId)?.Elevation ?? 0;
        foreach (var piece in document.All<Component>().Where(piece => piece.LevelId == hall.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location)))
            if (document.FindType<ComponentType>(piece.TypeId) is { Depth: >= 100 } type && ComponentHosting.BaseElevation(document, piece, type) - floorAt < 2000)
                yield return ($"the {type.Name.Split(" - ")[0].ToLowerInvariant()}", ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0)));
    }

    /// <summary>
    /// How close something standing in a hall comes to an inside corner where the hall turns - a corner of its outline turning back into it,
    /// both its sides 300 long at least, not a jog where walls of two thicknesses meet - when within 300 of it, as Diagram 2.3 shades the
    /// corners of a change of direction (round 162); none where it stands clear of them.
    /// </summary>
    private static double? AtTurn(BimDocument document, Room hall, IReadOnlyList<Point2D> footprint)
    {
        if (hall.GetBoundary(document) is not { IsEnclosed: true } boundary || boundary.Polygon.Count < 4) return null;
        var outline = boundary.Polygon;
        var turning = Math.Sign(Polygon2D.SignedArea(outline));
        double? closest = null;
        for (var i = 0; i < outline.Count; i++)
        {
            var (before, at, after) = (outline[(i + outline.Count - 1) % outline.Count], outline[i], outline[(i + 1) % outline.Count]);
            if (before.DistanceTo(at) < 300 || at.DistanceTo(after) < 300) continue;
            var cross = (at - before).Cross(after - at);
            if (Math.Sign(cross) == turning || Math.Abs(cross) < 1e-6) continue;

            var apart = Polygon2D.Contains(footprint, at)
                ? 0
                : Enumerable.Range(0, footprint.Count).Min(j => Line2D.DistanceFromSegment(at, footprint[j], footprint[(j + 1) % footprint.Count]));
            if (apart <= 300 + 0.5 && (closest is null || apart < closest)) closest = apart;
        }

        return closest;
    }

    /// <summary>
    /// The door of a hall something stands in front of, as Diagrams 2.3 and 3.4 shade it: a door approached from the side, the 1500 of hall
    /// centred on it from its face across to the far wall - the wall opposite, and beside its jambs; a door approached head on, 300 beyond each
    /// jamb, 300 out from its wall - "the living room's door"; none where it stands clear of every door.
    /// </summary>
    private static string? DoorwayNear(BimDocument document, Room hall, IReadOnlyList<Point2D> footprint)
    {
        foreach (var door in document.All<Door>())
        {
            if (document.Find<Wall>(door.HostWallId) is not { } wall || wall.LevelId != hall.LevelId || document.GetWallType(wall) is not { } wallType) continue;
            var (swing, other) = Doors.RoomsBeside(document, door);
            if (swing != hall && other != hall) continue;
            if (HallAt(document, door) is not var (_, headOn, width)) continue;
            var half = wallType.Structure.TotalWidth / 2;
            var middle = wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0);
            var normal = (wall.PointAt(wallType.Structure, door.DistanceAlongWall, half + 150) - middle).NormalisedOrDefault(Vector2D.UnitX);
            if (!(hall.GetBoundary(document).Polygon is { Count: >= 3 } outline && Polygon2D.Contains(outline, middle + normal * (half + 150)))) normal = new Vector2D(-normal.X, -normal.Y);
            var along = new Vector2D(-normal.Y, normal.X);
            var (side, reach) = headOn ? (door.WidthOf(document.FindType<DoorType>(door.TypeId)) / 2 + 300, 300.0) : (750.0, width + 1);
            var zone = new[] { middle + along * side + normal * half, middle - along * side + normal * half, middle - along * side + normal * (half + reach), middle + along * side + normal * (half + reach) };
            if (footprint.Any(point => Polygon2D.Contains(zone, point)) || zone.Any(point => Polygon2D.Contains(footprint, point)))
            {
                var beyond = swing == hall ? other : swing;
                return beyond is null ? "a doorway" : $"the {beyond.Name.ToLowerInvariant()}'s door";
            }
        }

        return null;
    }

    /// <summary>The clear width Table 1.1 and Table 2.1 ask of a door off a hall so wide, approached so; none where the hall is under 900.</summary>
    public static double? Asked(bool headOn, double hall) =>
        hall < 900 - 0.5 ? null : headOn ? 750 : hall >= 1200 - 0.5 ? 750 : hall >= 1050 - 0.5 ? 775 : 800;

    /// <summary>What a door off a hall falls short of by the table: the hall's width at it, or the door's for the hall - said, and why.</summary>
    public static (string Said, string Rule)? HallProblem(BimDocument document, Door door, DoorType type, string rule)
    {
        if (HallAt(document, door) is not var (hall, headOn, width)) return null;
        var clear = Doors.ClearWidth(type);
        var how = headOn ? "approached head on" : "approached from the side";
        if (Asked(headOn, width) is not { } asked)
            return ($"the {hall.Name.ToLowerInvariant()} in front of it is {width:0} clear, {how}.", rule);
        return clear < asked - 0.5 ? ($"it gives {clear:0} clear off a {hall.Name.ToLowerInvariant()} {width:0} wide, {how}.", rule) : null;
    }

    /// <summary>The step up from the ground outside a door to the floor inside it, mm; none where no ground is drawn there.</summary>
    public static double? StepUp(BimDocument document, Door door)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return null;
        var outward = SpaceSeparation.Outward(document, wall);
        var face = wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0) + outward * (wallType.Structure.TotalWidth / 2 + 300);
        if (Terrains.GroundAt(document, face) is not { } ground) return null;
        var floor = (document.FindLevel(wall.LevelId)?.Elevation ?? 0) + door.SillHeight;
        return floor - ground;
    }

    /// <summary>
    /// A wheelchair user dwelling with no storage and transfer space on its entrance storey - the storey its principal private entrance is
    /// on - said of that entrance (Approved Document M 3.25a): a house in England checked to M4(3), not an extension.
    /// </summary>
    private static (Element Element, string Said)? WheelchairStore(BimDocument document)
    {
        if (FireDesign.UseOf(document) != BuildingUse.House || !Domestic.Rules(document) || document.ProjectInformation.BuildingType.Contains("extension", StringComparison.OrdinalIgnoreCase)) return null;
        if (Entrance(document) is not { } entrance || document.Find<Wall>(entrance.HostWallId) is not { } wall) return null;
        if (document.All<Component>().Any(piece => piece.LevelId == wall.LevelId && document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.WheelchairStorage)) return null;
        return (entrance, $"no wheelchair storage and transfer space on {document.FindLevel(wall.LevelId)?.Name ?? "the entrance storey"}: Approved Document M (M4(3) 3.25a, Diagram 3.6) asks for one 1700 wide and 1100 deep on the entrance storey, preferably close to the principal private entrance - put down the Wheelchair Storage and Transfer Space component, a power socket in it.");
    }

    /// <summary>A fact about the whole house, found once while the model is unchanged.</summary>
    private static T Kept<T>(BimDocument document, string what, Func<T> work) =>
        ModelCache.Of(document) is { } cache ? cache.Keep($"visitable: {what}", document, null, null, work) : work();
}
