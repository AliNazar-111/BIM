using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A building other than a home with homes in it - flats over a shop or offices (round 121). Volume 2 asks Volume 1 to be read with it for the
/// homes (0.23), and the more onerous of the two where in doubt (Volume 1, 0.16): the rooms named into dwellings, and the common parts serving
/// only them - the halls, landings, corridors and lobbies, refuse stores and cupboards of a storey of homes - are Volume 1's; the rest of the
/// building Volume 2's. A stair may serve both (Volume 1, 3.79, 3.80): up to three storeys above the ground storey, separated from each
/// occupancy - each flat, and the other use - by a protected lobby, REI 30, at each storey; with more, only where the flat is ancillary to the
/// main use (not judged here), has an escape route independent of the stair, the stair is separated by a protected lobby at each storey of the
/// other use below, its enclosure stands the building's Table B2 period, and a fire detection and alarm system in the main part of the
/// building covers the flats too. Volume 2's 2.5 and Volume 1's 3.5 ask the same separate escape of residential storeys.
///
/// Not done: a flat's entrance hall on a storey of the other use (it stays Volume 2's); the stair's security (3.80f); whether a flat is
/// ancillary (3.80a, 0.15).
/// </summary>
public static class MixedUse
{
    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a room is a stair's: one a flight starts or ends in, or so named.</summary>
    public static bool Stair(BimDocument document, Room room) => Doors.StairRooms(document).Contains(room) || Named(room, "stair");

    /// <summary>Whether a room serves the rest: circulation, a refuse store, a cupboard, a riser or meters - not somewhere people stay.</summary>
    private static bool Serving(Room room) =>
        FireSeparation.Circulation(room) || FireSeparation.Refuse(room) || Named(room, "foyer", "passage", "cupboard", "riser", "meter", "duct", "cleaner");

    /// <summary>Whether a building is one: Volume 2's by its use, with rooms named into dwellings.</summary>
    public static bool Is(BimDocument document) =>
        HorizontalEscape.Applies(document) && document.All<Room>().Any(room => Dwellings.Of(room).Length > 0);

    /// <summary>
    /// The homes' part of a building of mixed use: the rooms named into dwellings, and on a storey of homes the rooms serving them but its
    /// stairs; empty where the building is not one.
    /// </summary>
    public static IReadOnlySet<Room> Homes(BimDocument document)
    {
        HashSet<Room> Work()
        {
            var homes = new HashSet<Room>();
            if (!Is(document)) return homes;
            var rooms = document.All<Room>().ToList();
            homes.UnionWith(rooms.Where(room => Dwellings.Of(room).Length > 0));
            foreach (var storey in rooms.GroupBy(room => room.LevelId))
            {
                var rest = storey.Where(room => Dwellings.Of(room).Length == 0 && !Stair(document, room)).ToList();
                if (storey.Any(room => Dwellings.Of(room).Length > 0) && rest.All(Serving)) homes.UnionWith(rest);
            }

            return homes;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("mixed use: homes", document, null, null, Work) : Work();
    }

    /// <summary>Whether a room is the homes' in a building of mixed use - Volume 1's, left out of Volume 2's walks.</summary>
    public static bool Home(BimDocument document, Room room) => Homes(document).Contains(room);

    /// <summary>
    /// Whether a room is in the common parts of the homes as Volume 1 walks them: not in a dwelling, and in a building of mixed use one of the
    /// homes' part or a stair - not the shop or office.
    /// </summary>
    public static bool Common(BimDocument document, Room room) =>
        Dwellings.Of(room).Length == 0 && (!Is(document) || Home(document, room) || Stair(document, room));

    /// <summary>What a stair serving both flats and the other use falls short of (Volume 1, 3.79, 3.80): where, and said.</summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!Is(document)) yield break;
        var outlines = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
        var flights = Escape.Flights(document, outlines).Where(flight => flight.Foot is not null && flight.Head is not null).ToList();
        if (flights.Count == 0) yield break;
        var stacks = Escape.Stacks(flights);
        var links = Escape.Links(document, outlines.Keys.ToHashSet());
        var ground = Escape.Ground(document);
        var above = outlines.Keys.Where(room => (document.FindLevel(room.LevelId)?.Elevation ?? 0) > ground + 100).Select(room => room.LevelId).Distinct().Count();
        var many = above > 3;
        var period = Math.Max(30, FireDesign.PeriodOf(document).Minutes);
        var stairRooms = stacks.SelectMany(stack => stack).ToHashSet();

        // The rooms reached from a room on its storey without going through a stair.
        HashSet<Room> Region(Room from)
        {
            var reached = new HashSet<Room> { from };
            var queue = new Queue<Room>(reached);
            while (queue.Count > 0)
            {
                var room = queue.Dequeue();
                foreach (var link in links.Where(link => link.Room == room && link.Other is { } next && !stairRooms.Contains(next) && next.LevelId == room.LevelId))
                    if (reached.Add(link.Other!)) queue.Enqueue(link.Other!);
            }

            return reached;
        }

        bool Lobby(Room room) => Circulation.IsLobby(room);
        bool Homes(HashSet<Room> region) => region.Any(room => Dwellings.Of(room).Length > 0);
        bool Other(HashSet<Room> region) => region.Any(room => !Home(document, room) && !Serving(room) && !Stair(document, room));

        foreach (var stack in stacks)
        {
            var own = stack.ToHashSet();
            var doors = links.Where(link => own.Contains(link.Room) && link.Other is { } other && !own.Contains(other))
                .Select(link => (Link: link, Region: Region(link.Other!))).ToList();
            if (!doors.Any(entry => Homes(entry.Region)) || !doors.Any(entry => Other(entry.Region))) continue;
            var name = stack.OrderBy(room => document.FindLevel(room.LevelId)?.Elevation ?? 0).First().Name;
            var both = many
                ? "Approved Document B (3.80) lets a stair serve flats and other uses in a building of more than three storeys above the ground storey only where the flat is ancillary to the main use"
                : "Approved Document B (3.79) lets a stair serve flats and other uses in a building of three storeys at most above the ground storey";

            // A protected lobby between it and each occupancy at each storey (3.79) - with more storeys, at each storey of the other use (3.80c).
            foreach (var (link, region) in doors)
            {
                var beyond = link.Other!;
                var homes = Homes(region);
                if (!homes && !Other(region) || many && !Other(region)) continue;
                var level = document.FindLevel(link.Room.LevelId)?.Name ?? "storey";
                var whose = homes && Other(region) ? "flats and the other use alike" : homes ? "flats" : "the other use";
                if (!Lobby(beyond))
                {
                    yield return new EscapeProblem(link.Room, $"on the {level} it opens straight into the {beyond.Name}, serving {whose} - {both}, separated from {(many ? "the other use at each of its storeys" : "each occupancy at each storey")} by a protected lobby, REI 30{(many ? " (3.80c)" : string.Empty)}");
                    continue;
                }

                var weak = links.Where(each => each.Room == beyond && document.FindType<DoorType>(each.Door.TypeId) is { } type && Doors.FireMinutes(type.FireRating) < 30).Select(each => each.Other?.Name ?? "outside").ToList();
                var walls = beyond.GetBoundary(document).BoundingWalls.Where(wall => document.GetWallType(wall) is { Function: not WallFunction.Exterior } type && !document.IsCurtainWall(wall) && Doors.FireMinutes(type.FireRating) < 30).ToList();
                if (weak.Count > 0 || walls.Count > 0)
                    yield return new EscapeProblem(beyond, $"it is the lobby on the {level} between the {link.Room.Name} and {whose}, {(weak.Count > 0 ? $"its door{(weak.Count == 1 ? string.Empty : "s")} to the {string.Join(", ", weak.Distinct())} not a 30 minute fire door" : $"{walls.Count} of its walls under 30 minutes")} - {both}, separated by a protected lobby, REI 30");
            }

            if (!many) continue;

            // 3.80b: each flat an escape route independent of the stair - a door out, or its storey reaching another stair.
            foreach (var flat in doors.SelectMany(entry => entry.Region).Where(room => Dwellings.Of(room).Length > 0).GroupBy(Dwellings.Of, StringComparer.OrdinalIgnoreCase))
            {
                var rooms = flat.ToList();
                var reached = rooms.SelectMany(room => Region(room)).ToHashSet();
                var outside = links.Any(link => reached.Contains(link.Room) && link.Other is null && link.Outside);
                var others = links.Any(link => reached.Contains(link.Room) && link.Other is { } other && stairRooms.Contains(other) && !own.Contains(other));
                if (!outside && !others)
                    yield return new EscapeProblem(rooms.OrderBy(room => room.Name, StringComparer.Ordinal).First(),
                        $"{flat.Key} is reached only by the {name}, which serves the other use too - {both}, with an escape route independent of the stair (3.80b)");
            }

            // 3.80d: the enclosure the building's period.
            var short_ = own.SelectMany(room => room.GetBoundary(document).BoundingWalls).Distinct()
                .Where(wall => document.GetWallType(wall) is { Function: not WallFunction.Exterior } type && !document.IsCurtainWall(wall) && Doors.FireMinutes(type.FireRating) < period).ToList();
            if (short_.Count > 0)
                yield return new EscapeProblem(stack[0], $"{short_.Count} wall{(short_.Count == 1 ? " of its enclosure stands" : "s of its enclosure stand")} less than {period} minutes - {both}, its enclosure at least as fire resisting as the building's structure by Table B2 (3.80d)");

            // 3.80e: a fire alarm system in the main part covering the flats.
            var parts = document.All<Component>().Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId))).Where(entry => entry.Type is not null && Alarms.SystemPart(entry.Type)).ToList();
            if (parts.Count == 0) continue;
            foreach (var flat in outlines.Keys.Where(room => Dwellings.Of(room).Length > 0).GroupBy(Dwellings.Of, StringComparer.OrdinalIgnoreCase))
            {
                var rooms = flat.ToList();
                if (parts.Any(entry => Alarms.Detector(entry.Type!) && rooms.Any(room => room.LevelId == entry.Piece.LevelId && Polygon2D.Contains(outlines[room], entry.Piece.Location)))) continue;
                yield return new EscapeProblem(rooms.OrderBy(room => room.Name, StringComparer.Ordinal).First(),
                    $"{flat.Key} has no detector of the building's fire alarm system - {both}, any fire detection and alarm system in the main part covering all the flats too (3.80e)");
            }
        }
    }
}
