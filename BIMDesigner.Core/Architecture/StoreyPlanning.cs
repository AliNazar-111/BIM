using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How a storey of a building other than a home is planned for escape (round 130), by Approved Document B Volume 2 2.12 to 2.17, from its own
/// text - each said on a room, walked on round 109's grid:
///
/// <b>A central core</b> (2.12, Diagram 2.4): no two storey exits into different stairs approached from the same lift hall, lobby or corridor.
/// <b>Openings between floors</b> (2.13, Diagram 2.5): no point whose every way to a storey exit comes within 4.5 m of an opening between floors
/// - a hole in the storey's floor or the one over it, not over an enclosed stair, lift or shaft - getting nearer it. <b>Stairs</b>: no stair
/// reached only through another (2.14), a protected lobby passed through allowed; a stair that is the only way between two parts of a storey is
/// part of the circulation, its doors held open on an automatic release (2.15). <b>Eating and drinking</b> in a building of another main use
/// (2.16): two escape routes from each such room, not through a kitchen. <b>Separate occupancies</b> - rooms' Occupancy (2.17): no occupancy's
/// escape through another's; a common corridor on their escape routes protected - walls REI 30, doors E 20 - or the storey covered by automatic
/// fire detection.
///
/// <b>Small premises</b> (Section 4, round 131): highly flammable materials sold, stored or used make none (4.1, in `HorizontalEscape.SmallPremises`);
/// a kitchen's or a cooker's place at the extremity of a dead end, no escape passing it (4.2b); the floor undivided but for kitchens, ancillary offices
/// and stores (4.4); exits in effective alternative directions from every point, taken as 45 degrees (4.6); three storeys with an open stair serving
/// one, the other's stair enclosed at the ground storey with a final exit of its own (4.9). Small premises leave 2.12 and 2.14 to Section 4. Not done: exits "remote from one another" beyond the shared approach (2.12, 2.6's 45°
/// is round 109's); inner rooms meeting 2.11 among eating areas (2.16a), said with the rest; escalators themselves; whether a stair's doors are
/// the primary circulation by day rather than its only way (2.15).
/// </summary>
public static class StoreyPlanning
{
    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool Refreshment(Room room)
    {
        var words = Toilets.WordsOf(room.Name);
        return words.Overlaps(new[] { "restaurant", "cafe", "café", "canteen", "bar", "dining", "refectory", "bistro", "coffee" }) || words.Contains("food") && words.Contains("court");
    }

    private static bool Kitchen(Room room) => Named(room, "kitchen");

    private static string Called(Room room) => $"the {room.Name}{(room.Number.Length > 0 ? $" {room.Number}" : string.Empty)}";

    private static Room First(IEnumerable<Room> rooms) => rooms.OrderBy(room => room.Number, NaturalOrder.Instance).ThenBy(room => room.Name, StringComparer.Ordinal).First();

    /// <summary>What the storeys of a building other than a home fall short of in their planning for escape, each said on a room; kept while the model does not change.</summary>
    public static IReadOnlyList<EscapeProblem> Problems(BimDocument document) =>
        ModelCache.Of(document) is { } cache ? cache.Keep("storey planning", document, null, null, () => Work(document)) : Work(document);

    private static IReadOnlyList<EscapeProblem> Work(BimDocument document)
    {
        var found = new List<EscapeProblem>();
        if (!HorizontalEscape.Applies(document)) return found;
        var small = HorizontalEscape.SmallPremises(document).Small;
        var stairRooms = Doors.StairRooms(document);
        bool Stair(Room room) => (stairRooms.Contains(room) || Named(room, "stair")) && !HorizontalEscape.OpenStairRoom(document, room);
        var eatery = Toilets.WordsOf(document.ProjectInformation.BuildingType ?? string.Empty).Overlaps(new[] { "restaurant", "cafe", "café", "bar", "pub", "canteen" });
        var detector = document.All<Component>().Where(piece => document.FindType<ComponentType>(piece.TypeId) is { } type && Alarms.Detector(type)).ToList();

        if (small) found.AddRange(ThreeStoreys(document));
        foreach (var (level, storey) in HorizontalEscape.Storeys(document).OrderBy(entry => document.FindLevel(entry.Key)?.Elevation ?? 0))
        {
            var rooms = document.All<Room>().Where(room => room.LevelId == level && !MixedUse.Home(document, room) && room.GetBoundary(document) is { IsEnclosed: true }).ToList();
            var links = Escape.Links(document, rooms.ToHashSet());
            if (small) found.AddRange(Small(document, level, storey, rooms));

            // 2.12: two storey exits into different stairs approached from the same lift hall, lobby or corridor.
            if (!small)
                foreach (var approach in storey.Exits.Where(exit => exit.Door is not null).GroupBy(exit => exit.Room))
                {
                    if (!FireSeparation.Circulation(approach.Key) && !Named(approach.Key, "lift")) continue;
                    var into = approach.Select(exit => links.FirstOrDefault(link => link.Door == exit.Door && link.Room == exit.Room)?.Other).OfType<Room>().Where(Stair).Distinct().ToList();
                    if (into.Count >= 2)
                        found.Add(new EscapeProblem(approach.Key, $"storey exits into {Called(into[0])} and {Called(into[1])} are both approached from it - Approved Document B Volume 2 (2.12, Diagram 2.4) asks storey exits remote from one another, no two approached from the same lift hall, common lobby or undivided corridor"));
                }

            // 2.13: openings between floors.
            foreach (var (room, gap) in storey.Near.OrderBy(entry => entry.Key.Number, NaturalOrder.Instance))
                found.Add(new EscapeProblem(room, $"every way out of part of it passes {gap / 1000:0.0} m from an opening between floors, getting nearer it - Approved Document B Volume 2 (2.13, Diagram 2.5) asks escape routes not within 4.5 m of an opening between floors unless the direction of travel is away from it, or another escape route keeps 4.5 m from it"));

            // 2.14 and 2.15: the storey's parts joined without its stairs, and the stairs each part reaches.
            var parts = Parts(rooms.Where(room => !Stair(room)).ToList(), links);
            var stairs = rooms.Where(Stair).ToList();
            var touches = stairs.ToDictionary(stair => stair, stair => parts.Where(part => links.Any(link => link.Room == stair && link.Other is { } other && part.Contains(other)
                                                                                                  || part.Contains(link.Room) && link.Other == stair)).ToList());
            if (!small && stairs.Count >= 2)
                foreach (var part in parts)
                {
                    var own = stairs.Where(stair => touches[stair].Contains(part)).ToList();
                    if (own.Count == 0) continue;
                    var reached = new HashSet<Room>(own);
                    var queue = new Queue<Room>(own);
                    while (queue.Count > 0)
                        foreach (var beyond in touches[queue.Dequeue()].SelectMany(other => stairs.Where(stair => touches[stair].Contains(other))))
                            if (reached.Add(beyond)) queue.Enqueue(beyond);
                    if (reached.Except(own).FirstOrDefault() is { } through)
                        found.Add(new EscapeProblem(First(part), $"{Called(through)} is reached from here only through {Called(own[0])} - Approved Document B Volume 2 (2.14) asks a storey with more than one escape stair planned so that it is not necessary to pass through one stair to reach another; passing through a stair's protected lobby is allowed"));
                }

            foreach (var stair in stairs.Where(stair => touches[stair].Count >= 2))
            {
                var doors = links.Where(link => link.Room == stair && link.Other is not null || link.Other == stair).Select(link => link.Door).Distinct().ToList();
                if (doors.All(door => document.FindType<DoorType>(door.TypeId) is { AutomaticRelease: true })) continue;
                var (a, b) = (First(touches[stair][0]), First(touches[stair][1]));
                found.Add(new EscapeProblem(stair, $"it is the only way on this storey between {Called(a)} and {Called(b)}, its doors not held open on an automatic release - Approved Document B Volume 2 (2.15) asks the self-closing fire doors of a protected stairway that is part of the primary circulation fitted with an automatic release, or the stair kept out of the circulation between parts of the building at the same level: tick Held Open, Released by Alarm on the doors' type"));
            }

            // 2.16: eating and drinking in a building of another main use.
            if (!eatery)
                foreach (var room in rooms.Where(room => Refreshment(room) && storey.Rooms.ContainsKey(room)))
                {
                    var exits = HorizontalEscape.ExitsOf(document, room, links).Count;
                    if (exits < 2)
                        found.Add(new EscapeProblem(room, $"{(exits == 0 ? "no way" : "one way")} out of it, an area for eating or drinking - Approved Document B Volume 2 (2.16a) asks two escape routes at least from each such area of a storey in a building of another main use, but from an inner room meeting 2.11"));
                    if (storey.Passes.GetValueOrDefault(room)?.FirstOrDefault(Kitchen) is { } kitchen)
                        found.Add(new EscapeProblem(room, $"its escape passes through {Called(kitchen)} - Approved Document B Volume 2 (2.16b) asks the escape routes from an area for eating or drinking to lead straight to a storey exit without entering a kitchen or similar area of high fire hazard"));
                }

            // 2.17: separate occupancies.
            string Of(Room room) => room.Occupancy.Trim();
            var occupancies = rooms.Select(Of).Where(each => each.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (occupancies.Count < 2) continue;
            var common = new HashSet<Room>();
            foreach (var room in rooms.Where(room => Of(room).Length > 0 && storey.Passes.ContainsKey(room)))
            {
                var passed = storey.Passes[room];
                if (passed.FirstOrDefault(other => Of(other).Length > 0 && !string.Equals(Of(other), Of(room), StringComparison.OrdinalIgnoreCase)) is { } other)
                    found.Add(new EscapeProblem(room, $"its escape, {Of(room)}'s, passes through {Called(other)}, {Of(other)}'s - Approved Document B Volume 2 (2.17a) asks the means of escape from each occupancy of a storey under separate ownership or tenancy not to pass through any other occupancy"));
                foreach (var shared in passed.Where(each => Of(each).Length == 0 && (FireSeparation.Circulation(each) || each.GetBoundary(document) is { IsEnclosed: true } outline && Circulation.IsCorridor(each, outline.Polygon))))
                    common.Add(shared);
            }

            bool Detected(Room room) => room.GetBoundary(document) is { IsEnclosed: true } outline
                                        && (CareHomes.Sanitary(room) || Polygon2D.Area(outline.Polygon) < 1e6
                                            || detector.Any(piece => piece.LevelId == level && Polygon2D.Contains(outline.Polygon, piece.Location)));
            if (rooms.All(Detected)) continue;
            foreach (var corridor in common.Where(corridor => !Protected(document, corridor)).OrderBy(room => room.Number, NaturalOrder.Instance))
                found.Add(new EscapeProblem(corridor, $"a common corridor on the escape routes of the storey's separate occupancies - {string.Join(", ", occupancies)} - neither a protected corridor nor covered by automatic fire detection throughout the storey - Approved Document B Volume 2 (2.17b) asks one or the other: its walls REI 30 and doors E 20 (Table C1 item 5b), or detectors in every room of the storey"));
        }

        return found;
    }

    // ---- small premises (Section 4, round 131) -----------------------------------------------

    /// <summary>The rooms 4.4 leaves divided off a small premises' floor: kitchens, ancillary offices, stores - and its stairs, lobbies, WCs and plant.</summary>
    private static bool Aside(BimDocument document, Room room) =>
        Named(room, "kitchen", "store", "storage", "stock", "cupboard", "wc", "toilet", "washroom", "shower", "cloak", "changing", "stair", "lift", "lobby", "plant", "riser", "duct")
        || Named(room, "office") && HorizontalEscape.Purpose(document) != PurposeGroup.Office;

    /// <summary>
    /// What a storey of small premises falls short of in Section 4: cooking - a kitchen, or a cooker standing in the open - not at the extremity of
    /// a dead end, the escape from elsewhere passing it (4.2b); its floor divided but for kitchens, ancillary offices and stores (4.4); with two
    /// exits or more, a point without them in effective alternative directions - taken as Diagram 2.2's 45 degrees (4.6).
    /// </summary>
    private static IEnumerable<EscapeProblem> Small(BimDocument document, Guid level, HorizontalEscape.Storey storey, List<Room> rooms)
    {
        // 4.2b: a kitchen the escape from another room passes through.
        foreach (var kitchen in rooms.Where(Kitchen))
            if (rooms.FirstOrDefault(room => room != kitchen && storey.Passes.GetValueOrDefault(room)?.Contains(kitchen) == true) is { } room)
                yield return new EscapeProblem(kitchen, $"the escape from {Called(room)} passes through it, a kitchen - Approved Document B Volume 2 (4.2b) asks any kitchen or open cooking arrangement of small premises at the extremity of a dead end remote from the exits");

        // 4.2b: a cooker in the open - not in a kitchen - that the escape from points farther from the exits passes within 2 m of.
        var points = storey.Points;
        foreach (var cooker in document.All<Component>().Where(piece => piece.LevelId == level && document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Cooker))
        {
            if (RoomsOf.Containing(document, level, cooker.Location) is not { } room || Kitchen(room) || points.Count == 0) continue;
            var at = Enumerable.Range(0, points.Count).Where(i => points[i].Room == room && !double.IsPositiveInfinity(storey.Nearest[i]))
                .OrderBy(i => points[i].At.DistanceTo(cooker.Location)).DefaultIfEmpty(-1).First();
            if (at < 0) continue;
            var own = storey.Nearest[at];
            var passes = Enumerable.Range(0, points.Count).Any(i =>
            {
                if (storey.Nearest[i] <= own + 1000 || double.IsPositiveInfinity(storey.Nearest[i])) return false;
                var steps = 0;
                for (var next = i; next >= 0 && steps++ < points.Count; next = storey.Toward[next])
                    if (points[next].At.DistanceTo(cooker.Location) <= 2000) return true;
                return false;
            });
            if (passes)
                yield return new EscapeProblem(room, $"the escape from farther in passes the cooker in it, an open cooking arrangement - Approved Document B Volume 2 (4.2b) asks any kitchen or open cooking arrangement of small premises at the extremity of a dead end remote from the exits - taken as no escape route passing within 2 m of it");
        }

        // 4.4: the floor undivided but for kitchens, ancillary offices and stores.
        var main = rooms.Where(room => !Aside(document, room)).OrderByDescending(room => room.GetBoundary(document).Area).ThenBy(room => room.Number, NaturalOrder.Instance).ToList();
        foreach (var room in main.Skip(1))
            yield return new EscapeProblem(room, $"it divides the storey's floor from {Called(main[0])} - Approved Document B Volume 2 (4.4) asks the floor areas of small premises undivided, but for kitchens, ancillary offices and stores, so that the exits are seen from all parts");

        // 4.6: with two exits or more, effective alternative directions from every point.
        if (storey.Exits.Count < 2) yield break;
        foreach (var room in rooms.Where(room => storey.Rooms.ContainsKey(room)))
            // An exit's own doorway, and the points within half a metre of one, are out already.
            if (Enumerable.Range(0, points.Count).Any(i => points[i].Room == room && !storey.TwoWay.Contains(i) && storey.Nearest[i] > 500 && !double.IsPositiveInfinity(storey.Nearest[i])))
                yield return new EscapeProblem(room, $"from part of it the storey's {storey.Exits.Count} exits lie within 45° of one way - Approved Document B Volume 2 (4.6) asks the exits of small premises sited to give effective alternative directions of travel from any point - taken as Diagram 2.2 has them, 45° or more apart");
    }

    /// <summary>
    /// Three-storey small premises with a single open stair serving the top or bottom storey: the stair serving the other enclosed at the ground storey
    /// and discharging to a final exit of its own, not through the ground storey (4.9, Diagram 4.3).
    /// </summary>
    private static IEnumerable<EscapeProblem> ThreeStoreys(BimDocument document)
    {
        var outlines = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document))).Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3)
            .ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
        var ground = Escape.Ground(document);
        double Elevation(Guid level) => document.FindLevel(level)?.Elevation ?? 0;
        var levels = outlines.Keys.Select(room => room.LevelId).Distinct().ToList();
        if (!levels.Any(level => Elevation(level) > ground + 100) || !levels.Any(level => Elevation(level) < ground - 100)) yield break;
        var flights = Escape.Flights(document, outlines.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<Point2D>)entry.Value));
        // Each stair's room at the ground storey, going up and going down.
        var up = flights.Where(flight => flight.Foot is { } foot && Math.Abs(Elevation(foot.LevelId) - ground) <= 100 && flight.Head is { } head && Elevation(head.LevelId) > ground + 100).Select(flight => flight.Foot!).Distinct().ToList();
        var down = flights.Where(flight => flight.Head is { } head && Math.Abs(Elevation(head.LevelId) - ground) <= 100 && flight.Foot is { } foot && Elevation(foot.LevelId) < ground - 100).Select(flight => flight.Head!).Distinct().ToList();
        if (up.Count != 1 || down.Count != 1 || up[0] == down[0]) yield break;
        var (upOpen, downOpen) = (HorizontalEscape.OpenStairRoom(document, up[0]), HorizontalEscape.OpenStairRoom(document, down[0]));
        if (upOpen == downOpen) yield break;
        var (other, open, served) = upOpen ? (down[0], up[0], "the basement") : (up[0], down[0], "the first storey");
        var links = Escape.Links(document, outlines.Keys.Where(room => room.LevelId == other.LevelId).ToHashSet());
        var own = links.Any(link => link.Room == other && link.Other is null && link.Outside);
        if (!HorizontalEscape.OpenStairRoom(document, other) && own) yield break;
        yield return new EscapeProblem(other, $"the premises are three storeys, {(upOpen ? "the first storey" : "the basement")} served by the open stair in {Called(open)}, and the stair to {served} {(HorizontalEscape.OpenStairRoom(document, other) ? "stands open at the ground storey" : "has no final exit of its own at the ground storey")} - Approved Document B Volume 2 (4.9, Diagram 4.3) asks the stair serving the other storey enclosed in fire-resisting construction at the ground storey and discharging to a final exit independent of the ground storey");
    }

    /// <summary>The parts of a storey its rooms make, joined through doors - its stairs left out.</summary>
    private static List<List<Room>> Parts(List<Room> rooms, List<Escape.Link> links)
    {
        var parent = rooms.ToDictionary(room => room, room => room);
        Room Find(Room room)
        {
            while (parent[room] != room) room = parent[room] = parent[parent[room]];
            return room;
        }

        foreach (var link in links)
            if (link.Other is { } other && parent.ContainsKey(link.Room) && parent.ContainsKey(other))
                parent[Find(link.Room)] = Find(other);
        return rooms.GroupBy(Find).Select(group => group.ToList()).ToList();
    }

    /// <summary>Whether a corridor is a protected corridor as built: every wall round it inside the building REI 30, every door in them E 20 (2.24, Table C1 item 5b).</summary>
    private static bool Protected(BimDocument document, Room corridor)
    {
        var walls = corridor.GetBoundary(document).BoundingWalls.Where(wall => document.GetWallType(wall) is { Function: not WallFunction.Exterior }).ToList();
        if (walls.Any(wall => Doors.FireMinutes(document.GetWallType(wall)!.FireRating) < 30)) return false;
        return document.All<Door>().Where(door => walls.Any(wall => wall.Id == door.HostWallId))
            .All(door => Doors.FireMinutes(document.FindType<DoorType>(door.TypeId)?.FireRating ?? string.Empty) >= 20);
    }
}
