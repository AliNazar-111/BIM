using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Core.Architecture;

/// <summary>A room's means of escape found wanting: the room, and what is wrong.</summary>
public sealed record EscapeProblem(Room Room, string Said);

/// <summary>
/// Means of escape within a home and from a flat, as Approved Document B Volume 1 (2019, incorporating the 2020 and 2022 amendments)
/// sets them out - in England, not under the US's codes:
///
/// Inner rooms (2.11, 2.12; for flats 3.7, 3.8): a room whose only way out is through another room - not a hall, a landing, a corridor,
/// a lobby or a stair - is allowed only as a kitchen, a laundry or utility room, a dressing room, a bathroom, WC or shower room, or on a
/// storey 4.5 m or less above the ground with an escape window; one reached only through an inner room besides that only where each room
/// it is reached through has a smoke alarm, and none is a kitchen.
///
/// A flat on one storey more than 4.5 m above the ground (3.18): every point of every habitable room within 9 m of the flat's entrance
/// door, measured as it is walked - through the doors and round the corners - unless its entrance hall is protected, the doors off it to
/// the habitable rooms fire doors (FD20 at least; its walls' REI 30 taken as built), or it has another way out.
///
/// The common parts of a block whose flats are more than 4.5 m up (3.25, Table 3.1): from each flat's entrance door to the nearest common
/// stair, as it is walked through the common parts, 30 m at most where escape is in more than one direction - routes to two stairs leaving
/// the door 45 degrees or more apart, as Volume 2 (2.10, Diagram 2.2) tells them - and 7.5 m where it is in one direction only, to the only
/// stair or out of a dead end (3.27b); and no flat opening straight onto the only stair (3.27a, Diagram 3.7).
///
/// Storeys (round 87): a house with a storey more than 7.5 m above the ground - and a flat with its own entrance at the ground (3.20) - an
/// alternative escape route from each, or sprinklers throughout (2.6); a flat over more than one storey entered from the common parts, a
/// storey of it more than 4.5 m up, one of the four approaches of 3.21; a basement with habitable rooms, an escape window or door out of it, or
/// a protected stairway up to a final exit (2.16, 3.9); a gallery, an alternative exit, an escape window, or Diagram 2.6's conditions (2.15,
/// 3.13).
///
/// A flat is the rooms named into one dwelling (round 77); its entrance door, a door from them into rooms of no dwelling - the common parts -
/// or outside. A house - no dwellings named - is all the rooms, its ways out its outside doors and, upstairs, its stair.
/// </summary>
public static partial class Escape
{
    /// <summary>The highest a storey may be above the ground before the rules for higher storeys apply, mm.</summary>
    public const double High = 4500;

    /// <summary>The longest walk from a flat's entrance door to any point in a habitable room, without a protected hall (3.18), mm.</summary>
    public const double InFlat = 9000;

    /// <summary>Table 3.1: from a flat's entrance door to a common stair, escape in one direction only, and in more than one, mm.</summary>
    public const double OneWay = 7500, MoreWays = 30000;

    /// <summary>A room's means of escape as its Properties say it.</summary>
    public static string Describe(BimDocument document, Room room)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return "Not checked under the US's codes";

        // A storey walked once for all that is said here, not again for each rule that asks it - while nothing changes (round 116).
        using var kept = ModelCache.Begin(document);

        // A building other than a dwelling, no dwelling in it: Volume 2's occupants, exits and travel distances (round 109).
        if (HorizontalEscape.Applies(document) && !document.All<Room>().Any(each => Dwellings.Of(each).Length > 0))
        {
            var verdict = HorizontalEscape.Verdict(document, room) ?? VerticalEscape.Verdict(document, room) ?? "Not walked: a stair or a lift shaft, or not enclosed";
            var wanting = EscapeRoutes.RoomWanting(document, room).Concat(FirefightingShafts.Problems(document).Where(problem => problem.Room == room).Select(problem => problem.Said)).ToList();
            return wanting.Count == 0 ? verdict : $"{verdict}; {string.Join("; ", wanting)}";
        }
        var said = Problems(document).Where(problem => problem.Room == room).Select(problem => problem.Said).ToList();
        return said.Count == 0 ? "Nothing wanting" : string.Join("; ", said);
    }

    /// <summary>Everything wanting in the means of escape.</summary>
    public static IReadOnlyList<EscapeProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || document.Levels.Count == 0) return Array.Empty<EscapeProblem>();
        using var outlines = new RoomsOf.Outlines();
        var ground = Ground(document);
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => entry.Boundary.Polygon);
        if (rooms.Count == 0) return Array.Empty<EscapeProblem>();

        var stairRooms = Doors.StairRooms(document);
        var links = Links(document, rooms.Keys.ToHashSet());
        var problems = new List<EscapeProblem>();
        double Above(Room room) => (document.FindLevel(room.LevelId)?.Elevation ?? 0) - ground;
        bool Circulation(Room room) => stairRooms.Contains(room) || Lighting.KindOf(document, room, rooms[room]) is LitRoom.Hall or LitRoom.Stairs
                                      || Named(room, "hall", "landing", "corridor", "lobby", "stair", "entrance", "porch");
        var plan = new Plan(document, rooms, links, Flights(document, rooms), Circulation, Above);

        // The homes: each dwelling named, or - none named - the house; their rooms storey by storey. A building other than a dwelling with no
        // dwelling in it is Volume 2's (HorizontalEscape, round 109), not a house.
        var named = rooms.Keys.Any(room => Dwellings.Of(room).Length > 0);
        if (!named && HorizontalEscape.Applies(document)) return problems;
        var homes = named
            ? rooms.Keys.Where(room => Dwellings.Of(room).Length > 0).GroupBy(Dwellings.Of, StringComparer.OrdinalIgnoreCase).Select(group => (Name: group.Key, Rooms: group.ToHashSet())).ToList()
            : new List<(string Name, HashSet<Room> Rooms)> { (string.Empty, rooms.Keys.ToHashSet()) };

        foreach (var (name, own) in homes)
        {
            // Its ways out: an outside door, a door into rooms not its own - a flat's entrance door - and its stair.
            var exits = own.Where(room => stairRooms.Contains(room) || links.Any(link => link.Room == room && (link.Other is null ? link.Outside : !own.Contains(link.Other)))).ToHashSet();
            foreach (var storey in own.GroupBy(room => room.LevelId))
            {
                var here = storey.ToHashSet();
                problems.AddRange(InnerRooms(document, rooms, links, here, exits, Circulation, Above));
            }

            // Its storeys: more than 7.5 m up in a house, or a flat with its own entrance at the ground; a flat over storeys, entered from
            // the common parts; and basements.
            if (!named || OwnEntrance(plan, own)) problems.AddRange(HighStoreys(plan, own, name));
            else if (MultiStorey(plan, own, name) is { } storeys) problems.Add(storeys);
            problems.AddRange(Basements(plan, own, name));

            // A flat on one storey more than 4.5 m up: 9 m from its entrance door, unless its hall is protected or it has another way out.
            if (named && own.Select(room => room.LevelId).Distinct().Count() == 1 && own.Max(Above) > High + 0.5
                && FlatTravel(document, rooms, links, own, name, Circulation) is { } far)
                problems.Add(far);
        }

        // The common parts of a block whose flats are more than 4.5 m up: from each entrance door to a common stair.
        problems.AddRange(Galleries(plan, named));
        if (named)
        {
            problems.AddRange(CommonParts(plan, homes));

            // Smoke control of the common parts, and smoke outlets from basements (round 93).
            problems.AddRange(SmokeControl(plan, homes));
            problems.AddRange(BasementSmoke(plan));

            // Sprinklers in a block over 11 m (round 95).
            problems.AddRange(FireSeparation.Sprinklers(document));

            // Refuse stores, final exits from the stairs, an only stair down to a basement (round 96).
            problems.AddRange(FireSeparation.CommonParts(document));
        }
        return problems;
    }

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A door and the room on one side of it - the other room, or none - and where its opening is, a little into each side.</summary>
    internal sealed record Link(Door Door, Room Room, Room? Other, bool Outside, Point2D Inside, Point2D Beyond);

    /// <summary>Each door's two sides, as links from the room on each.</summary>
    internal static List<Link> Links(BimDocument document, HashSet<Room> rooms)
    {
        var links = new List<Link>();
        foreach (var door in document.All<Door>())
        {
            if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } type || document.FindType<DoorType>(door.TypeId) is not { } doorType) continue;
            var (swing, other) = Doors.RoomsBeside(document, door);
            var reach = type.Structure.TotalWidth / 2 + 100;
            var side = door.FlipFacing ? -1.0 : 1.0;
            var a = wall.PointAt(type.Structure, door.DistanceAlongWall, side * reach);
            var b = wall.PointAt(type.Structure, door.DistanceAlongWall, -side * reach);
            var outside = Doors.External(document, door, doorType);
            if (swing is not null && rooms.Contains(swing)) links.Add(new Link(door, swing, other is not null && rooms.Contains(other) ? other : null, outside && other is null, a, b));
            if (other is not null && rooms.Contains(other)) links.Add(new Link(door, other, swing is not null && rooms.Contains(swing) ? swing : null, outside && swing is null, b, a));
        }

        return links;
    }

    /// <summary>Inner rooms on a storey of a home, and those reached only through inner rooms (2.11, 2.12; 3.7, 3.8).</summary>
    private static IEnumerable<EscapeProblem> InnerRooms(BimDocument document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> outlines, List<Link> links,
        HashSet<Room> here, HashSet<Room> exits, Func<Room, bool> circulation, Func<Room, double> above)
    {
        // Not inner: a room with a way out, a hall or landing joined by halls and landings to one, and any room opening off those.
        var open = here.Where(exits.Contains).ToHashSet();
        var halls = new Queue<Room>(open.Where(circulation));
        var reached = halls.ToHashSet();
        while (halls.Count > 0)
        {
            var hall = halls.Dequeue();
            foreach (var next in Next(links, hall, here))
            {
                open.Add(next);
                if (circulation(next) && reached.Add(next)) halls.Enqueue(next);
            }
        }

        if (open.Count == 0) yield break;

        // The rest, from the rooms they are reached through: each, the rooms it is reached through on the way out.
        var through = new Dictionary<Room, Room>();
        var queue = new Queue<Room>(open);
        var seen = open.ToHashSet();
        while (queue.Count > 0)
        {
            var room = queue.Dequeue();
            foreach (var next in Next(links, room, here).Where(next => seen.Add(next)))
            {
                through[next] = room;
                queue.Enqueue(next);
            }
        }

        foreach (var (room, access) in through)
        {
            if (Excused(room)) continue;
            var chain = new List<Room>();
            for (var at = access; at is not null; at = through.GetValueOrDefault(at)) chain.Add(at);
            var names = string.Join(" and ", chain.Select(each => $"the {each.Name}"));
            if (!Allowed(document, outlines, room, above))
            {
                yield return new EscapeProblem(room, $"it is an inner room, reached only through {names} - Approved Document B (2.11, 3.7) allows one only as a kitchen, a utility room, a dressing room, a bathroom or WC, or on a storey 4.5 m or less above the ground with an escape window");
                continue;
            }

            if (chain.Count < 2) continue;
            if (chain.FirstOrDefault(each => Lighting.KindOf(document, each, outlines[each]) == LitRoom.Kitchen || Named(each, "kitchen")) is { } kitchen)
                yield return new EscapeProblem(room, $"it is reached only through inner rooms, one the {kitchen.Name} - Approved Document B (2.12, 3.8) allows that only where none of them is a kitchen");
            else if (chain.FirstOrDefault(each => !Alarmed(document, outlines[each], each)) is { } quiet)
                yield return new EscapeProblem(room, $"it is reached only through inner rooms, and the {quiet.Name} has no smoke alarm - Approved Document B (2.12, 3.8) asks one in each room it is reached through");
        }
    }

    /// <summary>The rooms of a storey a room opens onto through its doors.</summary>
    private static IEnumerable<Room> Next(List<Link> links, Room room, HashSet<Room> here) =>
        links.Where(link => link.Room == room && link.Other is { } other && here.Contains(other)).Select(link => link.Other!).Distinct();

    /// <summary>A space not counted as a room for escape: a cupboard, a store, a wardrobe.</summary>
    private static bool Excused(Room room) => Named(room, "cupboard", "store", "wardrobe", "airing", "cylinder", "plant", "void");

    /// <summary>Whether a room may be an inner room (2.11, 3.7).</summary>
    private static bool Allowed(BimDocument document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> outlines, Room room, Func<Room, double> above)
    {
        var kind = Lighting.KindOf(document, room, outlines[room]);
        if (kind is LitRoom.Kitchen or LitRoom.Utility or LitRoom.Bathroom or LitRoom.Hall or LitRoom.Stairs || Named(room, "kitchen", "utility", "laundry", "dressing", "bath", "shower", "wc", "toilet", "cloak")) return true;
        if (above(room) > High + 0.5) return false;
        var (windows, _) = WindowRules.Of(document, room, outlines[room]);
        var ground = above(room) <= 1;
        return windows.Any(entry => WindowRules.Escapes(entry.Type, entry.Sill, false, ground));
    }

    /// <summary>Whether a room has a smoke alarm in it.</summary>
    private static bool Alarmed(BimDocument document, IReadOnlyList<Point2D> outline, Room room) =>
        document.All<Component>().Any(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(outline, piece.Location)
                                               && document.FindType<ComponentType>(piece.TypeId)?.Form is ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm);

    /// <summary>A single-storey flat more than 4.5 m up whose furthest point is more than 9 m from its entrance door - without a protected hall or another way out (3.18).</summary>
    private static EscapeProblem? FlatTravel(BimDocument document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> outlines, List<Link> links,
        HashSet<Room> own, string name, Func<Room, bool> circulation)
    {
        var entrances = links.Where(link => own.Contains(link.Room) && (link.Other is { } other ? !own.Contains(other) : link.Outside)).ToList();
        if (entrances.Count != 1) return null;
        var entrance = entrances[0];

        if (HallProtected(document, outlines, links, own, entrance.Room, circulation)) return null;

        var walk = new Walk(outlines, links.Where(link => own.Contains(link.Room) && link.Other is { } other && own.Contains(other)).ToList());
        var from = walk.From(entrance.Room, entrance.Inside);
        (Room Room, double Distance)? furthest = null;
        foreach (var room in own.Where(room => Habitable(document, outlines, room)))
            foreach (var point in Points(outlines[room]))
                if (from(room, point) is { } distance && (furthest is null || distance > furthest.Value.Distance))
                    furthest = (room, distance);
        if (furthest is not { } far || far.Distance <= InFlat + 0.5) return null;
        return new EscapeProblem(far.Room, $"{name}: its furthest point is {Units.Metres(far.Distance, "0.0")} from the flat's entrance door, walked - Approved Document B (3.18) keeps every point of a habitable room within 9 m of it in a flat more than 4.5 m above the ground, unless its entrance hall is protected (REI 30, its doors FD20) or it has another way out");
    }

    /// <summary>A habitable room: a living room, kitchen, dining room, study or bedroom - not a hall, a bathroom, a utility room or a cupboard.</summary>
    private static bool Habitable(BimDocument document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> outlines, Room room) =>
        !Excused(room) && Lighting.KindOf(document, room, outlines[room]) is not (LitRoom.Hall or LitRoom.Stairs or LitRoom.Bathroom or LitRoom.Utility or LitRoom.Garage);

    /// <summary>
    /// From each flat's entrance door into the common parts, to the nearest common stair on that storey (Table 3.1): 30 m where escape is in
    /// more than one direction - the routes to two stairs leaving the door 45 degrees or more apart (Volume 2, 2.10, Diagram 2.2) - else 7.5 m,
    /// to the only stair or out of a dead end (3.27b); and, with only one stair, no flat's door opening straight onto it (3.27a).
    /// </summary>
    private static IEnumerable<EscapeProblem> CommonParts(Plan plan, List<(string Name, HashSet<Room> Rooms)> homes)
    {
        var (document, outlines, links) = (plan.Document, plan.Outlines, plan.Links);
        var common = outlines.Keys.Where(room => Dwellings.Of(room).Length == 0).ToHashSet();
        if (common.Count == 0) yield break;
        var walk = new Walk(outlines, links.Where(link => common.Contains(link.Room) && link.Other is { } other && common.Contains(other)).ToList());
        var toStair = new Dictionary<(Room, Point2D), Func<Room, Point2D, double?>>();

        // A small single stair building (3.28): its top storey 11 m up at most, no more than three storeys over the ground storey.
        var small = SmallBuilding(plan);

        foreach (var (name, own) in homes.Where(home => home.Rooms.Max(plan.Above) > High + 0.5))
        foreach (var entrance in links.Where(link => common.Contains(link.Room) && link.Other is { } flat && own.Contains(flat)))
        {
            // The common stairs on its storey: where each stair's walk starts or ends there, in the common parts.
            var level = entrance.Room.LevelId;
            var stairs = new List<(Room Room, Point2D At)>();
            foreach (var flight in plan.Flights)
            {
                var (room, at) = flight.Stair.LevelId == level ? (flight.Foot, flight.FootAt) : flight.Stair.TopLevelId == level ? (flight.Head, flight.HeadAt) : (null, default);
                if (room is not null && common.Contains(room)) stairs.Add((room, at));
            }

            if (stairs.Count == 0) continue;
            var from = walk.From(entrance.Room, entrance.Inside);
            var reached = stairs.Select(stair => (Stair: stair, Distance: from(stair.Room, stair.At))).Where(entry => entry.Distance is not null).ToList();
            if (reached.Count == 0) continue;

            // With only one stair, a protected lobby or corridor between it and each flat - in a small building of two flats a storey, or
            // their entrance halls protected (Diagram 3.9b).
            if (reached.Count == 1 && reached[0].Stair.Room == entrance.Room)
            {
                var two = small && homes.Count(home => home.Rooms.Any(room => room.LevelId == level)) <= 2;
                if (!(two && HallProtected(document, outlines, links, own, entrance.Other!, plan.Circulation)))
                    yield return new EscapeProblem(entrance.Room, $"{name}'s entrance door opens straight onto the only common stair - Approved Document B (3.27a, Diagram 3.7) asks a protected lobby or protected corridor between each flat and it{(two ? ", or - in a small single stair building of two flats a storey - the flat's entrance hall protected (Diagram 3.9b)" : string.Empty)}");
            }

            // In more than one direction: the first steps toward two of the stairs 45 degrees or more apart.
            var headings = reached.Select(entry =>
            {
                if (!toStair.TryGetValue(entry.Stair, out var to)) toStair[entry.Stair] = to = walk.From(entry.Stair.Room, entry.Stair.At);
                return Heading(to, entrance.Room, entrance.Inside, outlines[entrance.Room]);
            }).OfType<Vector2D>().ToList();
            var ways = headings.Any(one => headings.Any(other => one.Dot(other) <= Math.Cos(Math.PI / 4) + 1e-9));
            var nearest = reached.Min(entry => entry.Distance!.Value);
            var limit = ways ? MoreWays : OneWay;
            if (nearest > limit + 0.5)
                yield return new EscapeProblem(entrance.Room, $"{name}'s entrance door is {Units.Metres(nearest, "0.0")} from the common stair, walked - Approved Document B (Table 3.1) allows {Units.Metres(limit, "0.0")} where escape is {(ways ? "in more than one direction" : reached.Count > 1 ? "in one direction only, out of a dead end (3.27b)" : "in one direction only")}");
        }
    }

    /// <summary>Which way the walk to somewhere sets off from a point: of the points 300 mm round it, the one nearest it, walked; null where none is nearer.</summary>
    private static Vector2D? Heading(Func<Room, Point2D, double?> to, Room room, Point2D at, IReadOnlyList<Point2D> outline)
    {
        if (to(room, at) is not { } here) return null;
        Vector2D? best = null;
        for (var k = 0; k < 72; k++)
        {
            var way = new Vector2D(Math.Cos(k * Math.PI / 36), Math.Sin(k * Math.PI / 36));
            var probe = at + way * 300;
            if (Polygon2D.Contains(outline, probe) && to(room, probe) is { } there && there < here - 1e-6)
                (here, best) = (there, way);
        }

        return best;
    }

    /// <summary>Points of a room to measure to: a grid every 500 mm, and its corners a little inside it.</summary>
    internal static IEnumerable<Point2D> Points(IReadOnlyList<Point2D> outline)
    {
        foreach (var point in Energy.Daylight.Grid(outline, 500)) yield return point;
        foreach (var corner in Corners(outline, 100)) yield return corner;
    }

    /// <summary>A polygon's corners moved a little into it along their bisectors - those that land inside it.</summary>
    private static IEnumerable<Point2D> Corners(IReadOnlyList<Point2D> outline, double inset, bool reflexOnly = false)
    {
        var turn = Polygon2D.SignedArea(outline) >= 0 ? 1 : -1;
        for (var i = 0; i < outline.Count; i++)
        {
            var (u, v, w) = (outline[(i - 1 + outline.Count) % outline.Count], outline[i], outline[(i + 1) % outline.Count]);
            var (a, b) = ((v - u).NormalisedOrDefault(Vector2D.UnitX), (w - v).NormalisedOrDefault(Vector2D.UnitX));
            var reflex = (a.X * b.Y - a.Y * b.X) * turn < -1e-9;
            if (reflexOnly && !reflex) continue;
            foreach (var candidate in new[] { v + (b - a).NormalisedOrDefault(new Vector2D(-a.Y, a.X)) * inset, v - (b - a).NormalisedOrDefault(new Vector2D(-a.Y, a.X)) * inset })
                if (Polygon2D.Contains(outline, candidate))
                {
                    yield return candidate;
                    break;
                }
        }
    }

    /// <summary>
    /// Walking through some rooms: the shortest way from a point in one to a point in another, through the doors between them and round
    /// their corners - a room's own outline walked straight where nothing is in the way, else from corner to corner.
    /// </summary>
    internal sealed class Walk
    {
        private readonly IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> _outlines;
        private readonly Dictionary<Room, List<Point2D>> _turns = new();

        // The points the walk goes between - each door's opening on each side, each stair's foot and head - the room each is in, and the jumps
        // between them: through a door, up or down a stair.
        private readonly List<(Room Room, Point2D At)> _nodes = new();
        private readonly List<List<(int To, double Length)>> _jumps = new();
        private readonly Dictionary<Room, List<int>> _inRoom = new();

        /// <param name="links">The doors between the rooms walked through, from either side.</param>
        /// <param name="stairs">The stairs between them: the room at the foot and where, the room at the head and where, and how long, walked.</param>
        public Walk(IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> outlines, IEnumerable<Link> links, IEnumerable<(Room Foot, Point2D FootAt, Room Head, Point2D HeadAt, double Length)>? stairs = null)
        {
            _outlines = outlines;
            var doors = new HashSet<Door>();
            foreach (var link in links.Where(link => link.Other is not null && doors.Add(link.Door)))
                Jump(Add(link.Room, link.Inside), Add(link.Other!, link.Beyond), link.Inside.DistanceTo(link.Beyond));
            foreach (var (foot, footAt, head, headAt, length) in stairs ?? Enumerable.Empty<(Room, Point2D, Room, Point2D, double)>())
                Jump(Add(foot, footAt), Add(head, headAt), length);
        }

        private int Add(Room room, Point2D at)
        {
            _nodes.Add((room, at));
            _jumps.Add(new List<(int, double)>());
            if (!_inRoom.TryGetValue(room, out var here)) _inRoom[room] = here = new List<int>();
            here.Add(_nodes.Count - 1);
            return _nodes.Count - 1;
        }

        private void Jump(int a, int b, double length)
        {
            _jumps[a].Add((b, length));
            _jumps[b].Add((a, length));
        }

        /// <summary>How far each point of each room is from a point in a room, walked; null where it cannot be reached.</summary>
        public Func<Room, Point2D, double?> From(Room start, Point2D at) => From(new[] { (start, at, 0.0) });

        /// <summary>How far each point of each room is from the nearest of some points, each already so far along; null where none reaches it.</summary>
        public Func<Room, Point2D, double?> From(IReadOnlyCollection<(Room Room, Point2D At, double Start)> sources)
        {
            var best = Enumerable.Repeat(double.PositiveInfinity, _nodes.Count).ToArray();
            var queue = new PriorityQueue<int, double>();
            void Reach(int node, double distance)
            {
                if (distance >= best[node] - 1e-6) return;
                best[node] = distance;
                queue.Enqueue(node, distance);
            }

            foreach (var (room, at, start) in sources)
                foreach (var node in _inRoom.GetValueOrDefault(room) ?? new List<int>())
                    if (Inside(room, at, _nodes[node].At) is { } walked)
                        Reach(node, start + walked);

            while (queue.TryDequeue(out var node, out var distance))
            {
                if (distance > best[node] + 1e-6) continue;
                var (room, at) = _nodes[node];
                foreach (var other in _inRoom[room].Where(other => other != node))
                    if (Inside(room, at, _nodes[other].At) is { } walked)
                        Reach(other, distance + walked);
                foreach (var (to, length) in _jumps[node]) Reach(to, distance + length);
            }

            return (room, point) =>
            {
                double? shortest = null;
                foreach (var (from, at, start) in sources.Where(source => source.Room == room))
                    if (Inside(room, at, point) is { } walked && (shortest is null || start + walked < shortest))
                        shortest = start + walked;
                foreach (var node in _inRoom.GetValueOrDefault(room) ?? new List<int>())
                    if (!double.IsPositiveInfinity(best[node]) && Inside(room, _nodes[node].At, point) is { } walked && (shortest is null || best[node] + walked < shortest))
                        shortest = best[node] + walked;
                return shortest;
            };
        }

        /// <summary>The way from one point to another within a room's outline: straight, or round its inside corners.</summary>
        private double? Inside(Room room, Point2D from, Point2D to)
        {
            var outline = _outlines[room];
            if (Clear(outline, from, to)) return from.DistanceTo(to);
            if (!_turns.TryGetValue(room, out var turns)) _turns[room] = turns = Corners(outline, 20, reflexOnly: true).ToList();
            var nodes = new List<Point2D> { from, to };
            nodes.AddRange(turns);
            var best = Enumerable.Repeat(double.PositiveInfinity, nodes.Count).ToArray();
            best[0] = 0;
            var done = new bool[nodes.Count];
            for (var round = 0; round < nodes.Count; round++)
            {
                var i = -1;
                for (var k = 0; k < nodes.Count; k++)
                    if (!done[k] && (i < 0 || best[k] < best[i])) i = k;
                if (i < 0 || double.IsPositiveInfinity(best[i])) break;
                if (i == 1) return best[1];
                done[i] = true;
                for (var k = 0; k < nodes.Count; k++)
                    if (!done[k] && Clear(outline, nodes[i], nodes[k]) && best[i] + nodes[i].DistanceTo(nodes[k]) < best[k])
                        best[k] = best[i] + nodes[i].DistanceTo(nodes[k]);
            }

            return double.IsPositiveInfinity(best[1]) ? null : best[1];
        }

        /// <summary>Whether a straight walk between two points stays inside an outline: crossing none of its sides, its middle inside.</summary>
        private static bool Clear(IReadOnlyList<Point2D> outline, Point2D a, Point2D b)
        {
            for (var i = 0; i < outline.Count; i++)
                if (Crosses(a, b, outline[i], outline[(i + 1) % outline.Count])) return false;
            return Polygon2D.Contains(outline, new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2));
        }

        private static bool Crosses(Point2D p, Point2D q, Point2D r, Point2D s)
        {
            static double Cross(Point2D o, Point2D a, Point2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var (d1, d2, d3, d4) = (Cross(r, s, p), Cross(r, s, q), Cross(p, q, r), Cross(p, q, s));
            return (d1 > 1e-6 && d2 < -1e-6 || d1 < -1e-6 && d2 > 1e-6) && (d3 > 1e-6 && d4 < -1e-6 || d3 < -1e-6 && d4 > 1e-6);
        }
    }
}
