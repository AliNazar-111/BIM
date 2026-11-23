using System.Text.RegularExpressions;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Fire separation in a block of flats (round 94), as Approved Document B Volume 1 sets it out - Section 7, Section 3, Tables B3 and B4
/// and Appendix C's Table C1, read off the pages themselves.
///
/// Walls (Table B3): one separating a flat from any other part of the building a compartment wall (7.1b, 3.34; item 6a), REI 60 or Table
/// B4's period where that is less; one round a common stair - a protected shaft, the floors of a block of flats being compartment floors
/// (7.1a, 7.21, 7.22; item 8c) - Table B4's; round a protected corridor or lobby, where the flats' top storey is over 4.5 m (3.25, 3.34;
/// item 13), and round a place of special fire hazard (7.2; item 15a), REI 30.
///
/// Floors: every floor but one within a flat a compartment floor (7.1a; item 3c), Table B4's period - over a basement the higher of the
/// basement's and the storeys' (note *); one within a flat 30 minutes (note +).
///
/// Doors (Table C1, C5): a flat's entrance door from the common parts (item 2a), a door into a common stair (2b), and - over 4.5 m - one
/// across the common corridors (3.35, 3.36) or into a room off them (5a) E 30 Sa, FD 30S; one to a place of special fire hazard E 30 (10a);
/// each self-closing but a cupboard's or a duct's (C5 a, b). A common stair that is the only one, in a building past 3.28's, not opening
/// into a boiler room or fuel store (3.73).
///
/// A block of flats is rooms named into dwellings with common parts a dwelling's door opens into, or one dwelling over another. Not under
/// the US's codes.
/// </summary>
public static class FireSeparation
{
    /// <summary>What the checks of a block share: its rooms and their outlines, its common parts and stairs, Table B4's period and its height.</summary>
    internal sealed record Block(
        IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> Outlines, HashSet<Room> Common, HashSet<Room> Stairs, int StairCount,
        FirePeriod Period, double Ground, double Top, bool High, bool Small, int BasementPeriod, List<List<Room>> Stacks);

    /// <summary>The block of flats the model is - null where it is none, or under the US's codes.</summary>
    internal static Block? Of(BimDocument document) =>
        ModelCache.Of(document) is { } cache ? cache.Keep("fire separation: block", document, null, null, () => Work(document)) : Work(document);

    private static Block? Work(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || document.Levels.Count == 0) return null;
        var outlines = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => entry.Boundary.Polygon);
        if (!outlines.Keys.Any(room => Dwellings.Of(room).Length > 0)) return null;

        // Common parts a flat's door opens into, or one dwelling over another.
        var common = outlines.Keys.Where(room => MixedUse.Common(document, room)).ToHashSet();
        var links = Escape.Links(document, outlines.Keys.ToHashSet());
        var entered = links.Any(link => Dwellings.Of(link.Room).Length > 0 && link.Other is { } other && common.Contains(other));
        double Floor(Room room) => document.FindLevel(room.LevelId)?.Elevation ?? 0;
        var homes = outlines.Keys.Where(room => Dwellings.Of(room).Length > 0).ToList();
        var stacked = homes.Any(upper => homes.Any(lower => Floor(lower) < Floor(upper) - 1 && !Dwellings.Same(lower, upper) && Polygon2D.Contains(outlines[lower], upper.Location)));
        if (!entered && !stacked) return null;

        var flights = Escape.Flights(document, outlines).Where(flight => flight.Foot is { } foot && common.Contains(foot) && flight.Head is { } head && common.Contains(head)).ToList();
        var stairs = flights.SelectMany(flight => new[] { flight.Foot!, flight.Head! }).ToHashSet();
        var ground = Escape.Ground(document);
        double Above(Room room) => Floor(room) - ground;
        var top = Math.Max(0, outlines.Keys.Max(Above));
        var storeys = outlines.Keys.Where(room => Above(room) >= -0.5).Select(room => room.LevelId).Distinct().Count();
        var period = FireDesign.Period(BuildingUse.Flats, storeys, top / 1000, document.ProjectInformation.Sprinklered);
        var deepest = outlines.Keys.Min(Above);
        var basement = deepest < -Escape.Basement - 0.5 ? (deepest < -10000 ? 90 : 60) : 0;
        var small = top <= Escape.SmallTop + 0.5 && outlines.Keys.Where(room => Above(room) > 0.5).Select(room => room.LevelId).Distinct().Count() <= 3;
        var stacks = flights.Count == 0 ? new List<List<Room>>() : Escape.Stacks(flights);
        return new Block(outlines, common, stairs, stacks.Count, period, ground, top, homes.Max(Above) > Escape.High + 0.5, small, basement, stacks);
    }

    /// <summary>Whether a rating says it holds smoke back at ambient temperature: FD 30S, E 30 Sa.</summary>
    public static bool Smoke(string rating) => Regex.IsMatch(rating, @"FD\s*\d+\s*S(?![a-z])|E\s*\d+\s*S\s*a|\bSa\b", RegexOptions.IgnoreCase);

    /// <summary>A rating as it is said: so many minutes, with or without smoke seals; none.</summary>
    private static string Rated(string rating, bool smoke)
    {
        var minutes = Doors.FireMinutes(rating);
        return minutes == 0 ? "not fire-rated" : smoke && !Smoke(rating) ? $"rated {minutes} minutes without smoke seals (no S)" : $"rated {minutes} minutes";
    }

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static bool Circulation(Room room) => Named(room, "hall", "landing", "corridor", "lobby", "stair", "entrance", "porch");

    /// <summary>A room for storing refuse, or a refuse chute's: a bin store, a refuse room (3.55 to 3.58).</summary>
    internal static bool Refuse(Room room) => Named(room, "refuse", "bin store", "bin room", "bins", "waste");

    /// <summary>A place of special fire hazard (Appendix A): a boiler room, an oil-filled transformer or switch gear room, fuel stored, an engine.</summary>
    internal static bool Special(Room room) => Named(room, "boiler", "transformer", "switch", "fuel", "generator", "engine");

    /// <summary>A cupboard or a duct kept locked, whose fire door needs no closer (C5 a, b).</summary>
    private static bool Cupboard(Room room) => Named(room, "cupboard", "riser", "duct", "meter");

    private static string Flat(Room room) => Dwellings.Of(room);

    /// <summary>What a door of a block of flats falls short of: its fire resistance, smoke seals and closer by where it is.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Door door, DoorType type)
    {
        if (Of(document) is not { } block) yield break;
        var (a, b) = Doors.RoomsBeside(document, door);
        if (a is null || b is null || a == b || !block.Outlines.ContainsKey(a) || !block.Outlines.ContainsKey(b)) yield break;

        string? what = null, rule = null;
        var (minutes, smoke, closer) = (30, true, true);
        var (flatA, flatB) = (Flat(a).Length > 0, Flat(b).Length > 0);
        if (flatA || flatB)
        {
            if (flatA && flatB && Dwellings.Same(a, b)) yield break;
            var (flat, other) = flatA ? (a, b) : (b, a);
            what = $"{Flat(flat)}'s entrance door from {(Flat(other).Length > 0 ? Flat(other) : $"the {other.Name}")}";
            rule = "Approved Document B (Table C1 item 2a, C5) asks a door in the compartment wall between a flat and the rest of the building for E 30 Sa - FD 30S - and self-closing.";
        }
        else if (block.Stairs.Contains(a) != block.Stairs.Contains(b))
        {
            var other = block.Stairs.Contains(a) ? b : a;
            what = $"the door into the common stair from the {other.Name}";
            rule = "Approved Document B (7.22, Table C1 item 2b, C5) asks a door into the protected stairway of a block of flats for E 30 Sa - FD 30S - and self-closing.";
            if (Special(other) && block.StairCount == 1 && !block.Small)
                yield return ($"the only common stair opens into the {other.Name}, a place of special fire hazard.",
                    "Approved Document B (3.73) asks a common stair forming the only escape route from the flats not to serve a boiler room, a fuel store or the like - unless the building is a small single stair one (3.28).");
        }
        else if (Special(a) || Special(b))
        {
            var special = Special(a) ? a : b;
            (smoke, closer) = (false, !Cupboard(special));
            what = $"the door to the {special.Name}, a place of special fire hazard";
            rule = "Approved Document B (7.2, Table C1 item 10a, C5) asks a door in the enclosure of a place of special fire hazard for E 30 - FD 30 - and self-closing.";
        }
        else if (block.High && Circulation(a) && Circulation(b))
        {
            what = $"a door across the common parts, between the {a.Name} and the {b.Name}";
            rule = "Approved Document B (3.35, 3.36, Diagrams 3.7 and 3.8) asks a door dividing the common corridors, or closing off a dead end or a stair's lobby, for E 30 Sa and self-closing - Table C1 item 7 gives E 20 Sa; 3.35's 30 minutes taken.";
        }
        else if (block.High && Circulation(a) != Circulation(b))
        {
            var (corridor, room) = Circulation(a) ? (a, b) : (b, a);
            closer = !Cupboard(room);
            what = $"the door to the {room.Name} off the {corridor.Name}";
            rule = "Approved Document B (3.34, Table C1 item 5a, C5) asks a door in the enclosure of a protected corridor or lobby for E 30 Sa - FD 30S - and self-closing, but a cupboard's or a duct's.";
        }

        if (what is null) yield break;
        var shortOf = Doors.FireMinutes(type.FireRating) < minutes || smoke && !Smoke(type.FireRating);
        var unclosed = closer && !type.SelfClosing;
        if (shortOf || unclosed)
            yield return ($"it is {what}, {(shortOf ? $"{Rated(type.FireRating, smoke)}{(unclosed ? " and with no closer" : string.Empty)}" : "with no closer")}.", rule!);
    }

    /// <summary>The rooms either side of a wall along its length, on its storey: pairs of them, each once.</summary>
    private static IEnumerable<(Room? Left, Room? Right)> Sides(BimDocument document, Block block, Wall wall, WallType type)
    {
        var reach = type.Structure.TotalWidth / 2 + 150;
        var seen = new HashSet<(Room?, Room?)>();
        Room? At(Point2D point) => block.Outlines.FirstOrDefault(entry => entry.Key.LevelId == wall.LevelId && Polygon2D.Contains(entry.Value, point)).Key;
        var length = wall.Length;
        var count = Math.Max(1, (int)Math.Ceiling(length / 500));
        for (var i = 0; i < count; i++)
        {
            var along = length * (i + 0.5) / count;
            var pair = (At(wall.PointAt(type.Structure, along, reach)), At(wall.PointAt(type.Structure, along, -reach)));
            if (seen.Add(pair)) yield return pair;
        }
    }

    /// <summary>What a wall of a block of flats falls short of: the fire resistance Table B3 asks of what it separates.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (Of(document) is not { } block || document.IsCurtainWall(wall)) yield break;
        (int Minutes, string Said, string Rule)? most = null;
        void Ask(int minutes, string said, string rule)
        {
            if (most is null || minutes > most.Value.Minutes) most = (minutes, said, rule);
        }

        var period = block.Period.Minutes;
        foreach (var (left, right) in Sides(document, block, wall, type))
        {
            if (left is null || right is null || left == right) continue;
            var (stairL, stairR) = (block.Stairs.Contains(left), block.Stairs.Contains(right));
            var (flatL, flatR) = (Flat(left).Length > 0, Flat(right).Length > 0);
            if (stairL != stairR && period > 0)
                Ask(period, $"it encloses the common stair, beside the {(stairL ? right : left).Name}",
                    $"Approved Document B (7.21, 7.22, Table B3 item 8c) asks the walls of a protected shaft - a common stair, the floors of a block of flats being compartment floors - for Table B4's period: {block.Period.Said}.");
            if ((flatL || flatR) && !(flatL && flatR && Dwellings.Same(left, right)))
            {
                var flat = flatL ? left : right;
                var other = flatL ? right : left;
                var minutes = period > 0 ? Math.Min(60, period) : 60;
                Ask(minutes, $"it separates {Flat(flat)} from {(Flat(other).Length > 0 ? Flat(other) : $"the {other.Name}")}",
                    $"Approved Document B (7.1b, 3.34, Table B3 item 6a) asks a wall separating a flat from any other part of the building to be a compartment wall, REI 60 or Table B4's period where that is less - here {minutes} minutes.");
            }

            if (Refuse(left) != Refuse(right) && !flatL && !flatR)
                Ask(block.Top <= 5000 + 0.5 ? 30 : 60, $"it encloses the {(Refuse(left) ? left : right).Name}, a refuse store",
                    "Approved Document B (3.56a) asks a refuse store or chute separated from the rest of the building by REI 30 where the top storey is 5 m up at most, else REI 60.");
            if (Special(left) != Special(right))
                Ask(30, $"it encloses the {(Special(left) ? left : right).Name}, a place of special fire hazard",
                    "Approved Document B (7.2, Table B3 item 15a) asks the construction enclosing a place of special fire hazard for REI 30.");
            if (block.High && !flatL && !flatR && !stairL && !stairR && Circulation(left) != Circulation(right))
                Ask(30, $"it encloses the common {(Circulation(left) ? left : right).Name}, a protected corridor or lobby",
                    "Approved Document B (3.34, Table B3 item 13) asks the enclosure of a protected corridor or lobby for REI 30.");
        }

        if (most is not { } asked) yield break;
        var rating = Doors.FireMinutes(type.FireRating);
        if (rating < asked.Minutes)
            yield return ($"{asked.Said}, {(rating > 0 ? $"rated {rating} minutes" : "not fire-rated")}.", asked.Rule);

        // A compartment wall the full height of its storey, and in the top storey on through the roof space (7.10, 7.11) - a party wall's
        // own type is followed to the roof by 5.11 already.
        if (!type.Separating && Compartment(document, wall) && Reaches(document, wall) is var (gap, roofOver))
            yield return roofOver
                ? ($"it separates a flat from the rest of the building in the top storey and stops {gap:0} below the roof.", "Approved Document B (7.11, 5.11) asks a compartment wall in a top storey under a roof to go on through the roof space to the underside of the roof covering, fire-stopped - attach its top to the roof.")
                : ($"it separates a flat from the rest of the building and stops {gap:0} below the floor over it.", "Approved Document B (7.10) asks a compartment wall to run the full height of its storey.");
    }

    /// <summary>Whether a wall of a block of flats is a compartment wall: one separating a flat from any other part of the building (7.1b).</summary>
    internal static bool Compartment(BimDocument document, Wall wall)
    {
        if (Of(document) is not { } block || document.GetWallType(wall) is not { } type) return false;
        return Sides(document, block, wall, type).Any(pair => pair.Left is { } left && pair.Right is { } right && left != right
                                                              && (Flat(left).Length > 0 || Flat(right).Length > 0) && !Dwellings.Same(left, right));
    }

    /// <summary>
    /// How far short a wall stops of what is over it: the underside of the floor of the storey above, where one is over its middle; else - in the
    /// top storey, none of the walls in line with it going higher - the underside of the roof over it. Null where it reaches it, or neither is there.
    /// </summary>
    private static (double Gap, bool Roof)? Reaches(BimDocument document, Wall wall)
    {
        var middle = wall.Start.MidpointTo(wall.End);
        var top = wall.GetTopElevation(document);
        var floor = document.All<Floor>().Where(slab => slab.Boundary.Count >= 3 && slab.Contains(middle) && slab.GetBottomElevation(document) > top - 1 && slab.GetBottomElevation(document) < top + 6000)
            .Select(slab => slab.GetBottomElevation(document)).DefaultIfEmpty(double.NaN).Min();
        if (!double.IsNaN(floor)) return floor - top > 50 ? (floor - top, false) : null;

        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var above = document.Walls.Any(other => other != wall && other.GetTopElevation(document) > top + 1
            && Math.Abs(way.Cross(other.Start.MidpointTo(other.End) - wall.Start)) < 50 && Math.Abs(way.Cross((other.End - other.Start).NormalisedOrDefault(way))) < 0.02);
        if (above) return null;
        var roofs = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Contains(middle)).ToList();
        if (roofs.Count == 0) return null;
        var underside = roofs.Max(roof => roof.UndersideAt(document, middle));
        return underside - top > 50 ? (underside - top, true) : null;
    }

    /// <summary>
    /// A block of flats with a top storey more than 11 m up sprinklered throughout - in each flat, the common parts where fire sterile left out
    /// (7.4, Table B4 note 2): the project's Sprinklers Throughout, or a sprinkler in a room of every flat. Said on the top flat's largest room.
    /// </summary>
    internal static IEnumerable<EscapeProblem> Sprinklers(BimDocument document)
    {
        if (Of(document) is not { } block || block.Top <= SprinklersOver + 0.5 || document.ProjectInformation.Sprinklered) yield break;
        var sprinklers = document.All<Component>().Where(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Sprinkler).ToList();
        var flats = block.Outlines.Keys.Where(room => Flat(room).Length > 0).GroupBy(Flat, StringComparer.OrdinalIgnoreCase).ToList();
        var without = flats.Where(flat => !flat.Any(room => sprinklers.Any(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(block.Outlines[room], piece.Location))))
            .Select(flat => flat.Key).OrderBy(name => name, NaturalOrder.Instance).ToList();
        if (without.Count == 0) yield break;
        var top = block.Outlines.Keys.Where(room => Flat(room).Length > 0).OrderByDescending(room => document.FindLevel(room.LevelId)?.Elevation ?? 0)
            .ThenByDescending(room => Math.Abs(Polygon2D.SignedArea(block.Outlines[room]))).First();
        yield return new EscapeProblem(top, $"the block's top storey is {Units.Metres(block.Top, "0.0")} up and {(without.Count == flats.Count ? "no flat" : string.Join(", ", without))} has sprinklers - Approved Document B (7.4, Table B4 note 2) asks a block of flats with a top storey over 11 m a sprinkler system throughout, in each flat (Appendix E): tick Sprinklers Throughout, or put sprinklers in every flat");
    }

    /// <summary>
    /// A block's common parts room by room: a refuse store not off the stair or a lobby of it, and come to from the open air or through a lobby
    /// of its own (3.56b, 3.57); each common stair reaching a final exit at the ground - out of its own door, or along the common corridors
    /// (3.81); and an only stair, past a small building's (3.28), not going on down to the basement (3.71).
    /// </summary>
    internal static IEnumerable<EscapeProblem> CommonParts(BimDocument document)
    {
        if (Of(document) is not { } block) yield break;
        var links = Escape.Links(document, block.Outlines.Keys.ToHashSet());
        double Above(Room room) => (document.FindLevel(room.LevelId)?.Elevation ?? 0) - block.Ground;

        foreach (var store in block.Common.Where(Refuse).OrderBy(room => room.Name, NaturalOrder.Instance))
        {
            var doors = links.Where(link => link.Room == store).ToList();
            if (doors.Count == 0 || doors.Any(link => link.Other is null && link.Outside)) continue;
            var off = doors.Where(link => link.Other is { } other && (block.Stairs.Contains(other) || Named(other, "lobby") && links.Any(each => each.Room == other && each.Other is { } beyond && block.Stairs.Contains(beyond))))
                .Select(link => link.Other!).FirstOrDefault();
            if (off is not null)
            {
                yield return new EscapeProblem(store, $"it is a refuse store off the {off.Name}, {(block.Stairs.Contains(off) ? "the common stair" : "a lobby of the common stair")} - Approved Document B (3.56b) keeps a refuse store out of a protected stairway or protected lobby");
                continue;
            }

            var from = doors.Where(link => link.Other is { } other && !Named(other, "lobby")).Select(link => link.Other!).FirstOrDefault();
            if (from is not null)
                yield return new EscapeProblem(store, $"it is a refuse store come to from the {from.Name} - Approved Document B (3.57) asks a refuse store come to from the open air, or through a protected lobby with 0.2 m² of permanent ventilation");
        }

        foreach (var stack in block.Stacks)
        {
            // Its storey at the ground: out of its own door, or along the common corridors and lobbies to one (3.81).
            if (stack.FirstOrDefault(room => Math.Abs(Above(room)) <= 0.5) is { } foot)
            {
                var reached = new HashSet<Room> { foot };
                var queue = new Queue<Room>(reached);
                var exit = false;
                while (queue.Count > 0 && !exit)
                {
                    var room = queue.Dequeue();
                    foreach (var link in links.Where(link => link.Room == room))
                    {
                        if (link.Other is null && link.Outside) exit = true;
                        else if (link.Other is { } next && block.Common.Contains(next) && Circulation(next) && reached.Add(next)) queue.Enqueue(next);
                    }
                }

                if (!exit)
                    yield return new EscapeProblem(foot, "it is the common stair at the ground with no final exit, out of its own door or along the common corridors - Approved Document B (3.81) asks every protected stairway to lead to a final exit, directly or through a protected exit passageway");
            }

            // An only stair on down to a basement (3.71).
            if (block.StairCount == 1 && !block.Small && stack.FirstOrDefault(room => Above(room) < -Escape.Basement - 0.5) is { } down && stack.Any(room => Above(room) > 0.5))
                yield return new EscapeProblem(down, "the only common stair goes on down to the basement - Approved Document B (3.71) asks a building past a small single stair one (3.28) a separate stair for its basement, the only escape route from the storeys over it not serving it");
        }
    }

    /// <summary>The height of a block's top storey over which it is sprinklered (7.4), mm.</summary>
    public const double SprinklersOver = 11000;

    /// <summary>Whether a block's own rules cover a wall separating dwellings, in place of a house's party wall (5.8).</summary>
    public static bool Covers(BimDocument document) => Of(document) is not null;

    /// <summary>
    /// What a floor of a block of flats falls short of: every floor a compartment floor of Table B4's period, but one within a flat - its
    /// rooms over and under it one dwelling's - 30 minutes; null where it is no floor between storeys of the block.
    /// </summary>
    internal static (string Said, string Rule)? Problem(BimDocument document, Floor floor, SlabType type)
    {
        if (Of(document) is not { } block || floor.Boundary.Count < 3) return null;
        var level = document.FindLevel(floor.LevelId)?.Elevation ?? 0;
        var over = block.Outlines.Keys.Where(room => room.LevelId == floor.LevelId && Polygon2D.Contains(floor.Boundary, room.Location)).ToList();
        var below = block.Outlines.Keys.Where(room => (document.FindLevel(room.LevelId)?.Elevation ?? 0) < level - 1).GroupBy(room => document.FindLevel(room.LevelId)?.Elevation ?? 0)
            .OrderByDescending(group => group.Key).FirstOrDefault();
        var under = below?.Where(room => Polygon2D.Contains(floor.Boundary, room.Location)).ToList() ?? new List<Room>();
        if (over.Count == 0 || under.Count == 0) return null;

        var rooms = over.Concat(under).ToList();
        var within = Flat(rooms[0]).Length > 0 && rooms.All(room => Dwellings.Same(room, rooms[0]));
        int minutes;
        string rule;
        if (within)
        {
            minutes = 30;
            rule = "Approved Document B (7.1a, Table B4 note +) asks a floor within a flat of more than one storey, not holding up the building, for 30 minutes.";
        }
        else
        {
            var basement = below!.Key - block.Ground < -Escape.Basement - 0.5;
            minutes = Math.Max(block.Period.Minutes, basement ? block.BasementPeriod : 0);
            if (minutes == 0) return null;
            rule = $"Approved Document B (7.1a, Table B3 item 3c, Table B4) asks every floor of a block of flats but one within a flat to be a compartment floor: {block.Period.Said}{(basement ? $"; over a basement, the higher of that and the basement's {block.BasementPeriod} minutes (note *)" : string.Empty)}.";
        }

        var rating = Doors.FireMinutes(type.FireRating);
        return rating >= minutes ? null
            : ($"it is {(within ? "a floor within a flat" : "a compartment floor")}, {(rating > 0 ? $"rated {rating} minutes" : "not fire-rated")}.", rule);
    }
}
