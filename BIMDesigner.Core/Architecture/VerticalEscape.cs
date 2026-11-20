using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Escape stairs in a building other than a dwelling (round 111), as Approved Document B Volume 2 sets them out in Section 3: a stair is the
/// rooms its flights join, storey above storey.
///
/// One escape stair only where 3.3 allows it - no storey more than 11 m up and each storey allowed a single route (60 people at most, every
/// point within the limit for one direction) (d); an office of five storeys above the ground at most, within the limit for one direction on each
/// storey, none more than 11 m up (e); a factory of normal risk one storey above the ground (f ii). Each stair's width: Table 3.1's least - 1000
/// in a residential (institutional) building, 1100 serving an area for assembly, 800 otherwise; as wide as the exits into it (3.10a); and wide
/// enough for the people of the upper storeys it serves, shared among the stairs with one of them left out where there are two or more (3.14,
/// 3.15b), by Table 3.2 - 150 people on one floor for 1000, 40 more a floor - and 3.18's P = 200W + 50(W - 0.3)(N - 1) from 1100, simultaneous
/// evacuation. Each a protected stairway, its inside walls REI 30, its doors E 30 Sa self-closing (3.23, Tables B1, C1); approached through a
/// protected lobby or corridor at every storey above the ground but the top where it is the only stair and serves more than one storey above the
/// ground, or serves a storey 18 m up or more (3.34a, b); a refuge 900 by 1400 - a wheelchair space - in it or in a lobby or corridor opening
/// into it at every storey but the ground (3.4 to 3.6); leading to a final exit at its foot, straight out or through one room (3.36); and an only
/// stair not carried down to a basement (3.40).
///
/// Not done: phased evacuation (3.19 to 3.22, Table 3.3); basement stairs' widths; stairs over 30 m and central handrails (3.10d, 3.11); final
/// exits shared with a storey exit (2.23); the stair's materials (3.24); external escape stairs (3.29 to 3.33); places of special fire hazard's
/// lobbies (3.35); what may be in a protected stairway (3.38); small premises (Section 4); evacuation lifts and EVC (3.7, 5.32).
/// </summary>
public static class VerticalEscape
{
    /// <summary>The highest a storey may be for a single stair, and from which lobbies are asked, mm.</summary>
    public const double SingleStair = 11000, LobbyHeight = 18000;

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>How many people a stair of a width takes serving so many floors, evacuating at once (Table 3.2, 3.18); 50 under 1000, from 800 (Table 3.1 item 2).</summary>
    public static int Capacity(double width, int floors)
    {
        var n = Math.Max(1, floors);
        if (width >= 1100 - 0.5) return (int)Math.Floor(200 * width / 1000 + 50 * (width / 1000 - 0.3) * (n - 1) + 1e-6);
        if (width >= 1000 - 0.5) return 150 + 40 * (n - 1);
        return width >= 800 - 0.5 ? 50 : 0;
    }

    /// <summary>A stair as found: its rooms storey by storey, its flights, its narrowest width, the levels above the ground it serves, and its foot.</summary>
    public sealed record StairStack(IReadOnlyList<Room> Rooms, double Width, IReadOnlyList<Guid> Upper, Room Foot, bool Basement);

    /// <summary>The building's stairs, each its rooms joined by flights, kept while the model does not change.</summary>
    public static IReadOnlyList<StairStack> Stairs(BimDocument document)
    {
        IReadOnlyList<StairStack> Work()
        {
            var outlines = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
                .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
            var flights = Escape.Flights(document, outlines).Where(flight => flight.Foot is not null && flight.Head is not null).ToList();
            if (flights.Count == 0) return Array.Empty<StairStack>();
            var ground = Escape.Ground(document);
            double Elevation(Room room) => document.FindLevel(room.LevelId)?.Elevation ?? 0;
            return Escape.Stacks(flights).Select(rooms =>
            {
                var ordered = rooms.OrderBy(Elevation).ToList();
                var width = flights.Where(flight => rooms.Contains(flight.Foot!)).Select(flight => flight.Stair.Width).DefaultIfEmpty(0).Min();
                var upper = ordered.Where(room => Elevation(room) > ground + 100).Select(room => room.LevelId).Distinct().ToList();
                var foot = ordered.FirstOrDefault(room => Math.Abs(Elevation(room) - ground) <= 100) ?? ordered[0];
                return new StairStack(ordered, width, upper, foot, ordered.Any(room => Elevation(room) < ground - Escape.Basement - 0.5));
            }).ToList();
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("vertical escape", document, null, null, Work) : Work();
    }

    /// <summary>How many people are on a storey, by Appendix D.</summary>
    private static int People(BimDocument document, Guid level, PurposeGroup group) =>
        HorizontalEscape.Storeys(document).TryGetValue(level, out var storey)
            ? storey.Rooms.Keys.Sum(room => room.GetBoundary(document) is { IsEnclosed: true } boundary && HorizontalEscape.Occupants(document, room, boundary.Polygon, group) is var (count, _) ? count : 0)
            : 0;

    /// <summary>What a stair falls short of, each said.</summary>
    public static IReadOnlyList<string> Problems(BimDocument document, StairStack stair)
    {
        var wanting = new List<string>();
        if (!HorizontalEscape.Applies(document)) return wanting;
        var group = HorizontalEscape.Purpose(document);
        var ground = Escape.Ground(document);
        var stairs = Stairs(document);
        var links = Escape.Links(document, document.All<Room>().ToHashSet());
        double Above(Guid level) => (document.FindLevel(level)?.Elevation ?? 0) - ground;
        string Called(Guid level) => document.FindLevel(level)?.Name ?? "storey";
        var name = stair.Foot.Name;

        // The upper storeys it serves with people on them, and the stairs that serve them.
        var upper = stair.Upper.Where(level => People(document, level, group) > 0).ToList();
        var serving = stairs.Where(other => other.Upper.Intersect(upper).Any()).ToList();
        var only = serving.Count == 1;

        // One stair only where 3.3 allows it.
        if (only && upper.Count > 0)
        {
            var storeys = HorizontalEscape.Storeys(document);
            bool OneWay(Guid level) => storeys.TryGetValue(level, out var storey) && storey.Rooms.Values.All(travel => travel.Reached && travel.Nearest <= travel.One + 0.5);
            var top = upper.Max(Above);
            var single = upper.All(level => People(document, level, group) <= 60 && OneWay(level)) && top <= SingleStair + 0.5;
            var office = group == PurposeGroup.Office && upper.Count <= 5 && upper.All(OneWay) && top <= SingleStair + 0.5;
            var factory = group == PurposeGroup.Industrial && upper.Count <= 1 && upper.All(OneWay);
            if (!single && !office && !factory)
                wanting.Add($"it is the only escape stair from {upper.Count} upper storey{(upper.Count == 1 ? string.Empty : "s")}, the top {top / 1000:0.0} m up - Approved Document B Volume 2 (3.3) allows one only where no storey is more than 11 m up and each has 60 people at most within the travel distance for one direction (d), or in an office of five storeys above the ground at most within it (e), or a factory of one (f): give it a second stair");
            if (stair.Basement)
                wanting.Add("it is the only escape stair from the upper storeys and goes on down to the basement - Approved Document B Volume 2 (3.40) asks the basement its own stair");
        }

        // Its width: Table 3.1's least, as wide as the exits into it, and enough for the upper storeys' people, one stair left out (3.14).
        var least = group == PurposeGroup.ResidentialInstitutional ? 1000.0 : group == PurposeGroup.AssemblyAndRecreation ? 1100 : 800;
        if (stair.Width < least - 0.5)
            wanting.Add($"it is {stair.Width:0} wide - Approved Document B Volume 2 (3.10b, Table 3.1) asks {least:0} at least {(least >= 1100 ? "serving an area for assembly" : least >= 1000 ? "in a residential (institutional) building" : "of any escape stair")}");
        var into = links.Where(link => link.Other is { } other && stair.Rooms.Contains(other) && !stair.Rooms.Contains(link.Room))
            .Select(link => document.FindType<DoorType>(link.Door.TypeId) is { } type ? Doors.EscapeWidth(type) : 0).DefaultIfEmpty(0).Max();
        if (into > stair.Width + 0.5)
            wanting.Add($"it is {stair.Width:0} wide where a door into it gives {into:0} - Approved Document B Volume 2 (3.10a) asks a stair as wide as any exit into it");
        var share = 0;
        if (upper.Count > 0)
        {
            var people = upper.Sum(level => People(document, level, group));
            var lobbied = upper.Where(level => Above(level) < upper.Max(Above) - 1).All(level => Lobbied(document, stair, level, links));
            var available = serving.Count >= 2 && !lobbied ? serving.Count - 1 : serving.Count;
            share = (int)Math.Ceiling(people / (double)Math.Max(1, available));
            var takes = Capacity(stair.Width, upper.Count);
            if (takes < share)
                wanting.Add($"it is {stair.Width:0} wide, taking {takes} people from {upper.Count} storey{(upper.Count == 1 ? string.Empty : "s")} where {share} of the {people} on them may use it{(serving.Count >= 2 && !lobbied ? ", one stair left out" : string.Empty)} - Approved Document B Volume 2 (3.13, 3.14, 3.17, 3.18, Table 3.2) asks it wide enough for them evacuating at once: 150 people a floor for 1000, from 1100 P = 200W + 50(W - 0.3)(N - 1)");
        }

        // A protected stairway: its walls REI 30, its doors E 30 Sa with closers (3.23).
        var walls = document.Walls.Where(wall => document.GetWallType(wall) is { Function: WallFunction.Interior } type && Doors.FireMinutes(type.FireRating) < 30
                                                 && WallRules.RoomsBeside(document, wall) is var (one, other) && one is not null && other is not null && one != other && (stair.Rooms.Contains(one) != stair.Rooms.Contains(other)))
            .Select(wall => document.GetWallType(wall)!.Name).Distinct().ToList();
        var doors = links.Where(link => stair.Rooms.Contains(link.Room) && link.Other is { } beyond && !stair.Rooms.Contains(beyond))
            .Where(link => document.FindType<DoorType>(link.Door.TypeId) is not { } type || Doors.FireMinutes(type.FireRating) < 30 || !type.SelfClosing)
            .Select(link => document.FindType<DoorType>(link.Door.TypeId)?.Name ?? "a door").Distinct().ToList();
        if (walls.Count > 0 || doors.Count > 0)
            wanting.Add($"it is not a protected stairway: {string.Join(" and ", new[] { walls.Count > 0 ? $"its walls {string.Join(", ", walls)} less than 30 minutes" : null, doors.Count > 0 ? $"its doors {string.Join(", ", doors)} not E 30 Sa with closers" : null }.OfType<string>())} - Approved Document B Volume 2 (3.23, Tables B1, C1) asks every internal escape stair enclosed in fire-resisting construction, REI 30, its doors E 30 Sa fire doorsets, self-closing");

        // Protected lobbies or corridors at every storey above the ground but the top, where it is the only stair over more than one storey or one 18 m up (3.34).
        if (upper.Count > 0 && (only && upper.Count > 1 || upper.Max(Above) >= LobbyHeight - 0.5))
        {
            var top = upper.Max(Above);
            var open = upper.Where(level => Above(level) < top - 1 && !Lobbied(document, stair, level, links)).Select(Called).ToList();
            if (open.Count > 0)
                wanting.Add($"it opens straight into the accommodation on the {string.Join(", the ", open)} - Approved Document B Volume 2 (3.34{(only && upper.Count > 1 ? "a" : "b")}) asks a protected lobby or protected corridor between it and each storey above the ground but the top, {(only ? "it being the only stair over more than one storey" : "it serving a storey 18 m up or more")}");
        }

        // A refuge at every storey it serves but the ground: a wheelchair space 900 by 1400 in it, or in a lobby or corridor opening into it (3.4 to 3.6).
        var without = stair.Rooms.Where(room => !ReferenceEquals(room, stair.Foot) && Math.Abs(Above(room.LevelId)) > 100 && People(document, room.LevelId, group) > 0 && !Refuge(document, stair, room, links))
            .Select(room => Called(room.LevelId)).Distinct().ToList();
        if (without.Count > 0)
            wanting.Add($"no refuge by it on the {string.Join(", the ", without)} - Approved Document B Volume 2 (3.4 to 3.6) asks one at every storey of each protected stairway that is an exit from it, 900 by 1400, reachable in a wheelchair, in it or in a protected lobby or corridor opening into it, with its emergency voice communication (3.7): place a wheelchair space there");

        // To a final exit at its foot (3.36).
        IEnumerable<Door> Out(Room room) => links.Where(link => link.Room == room && link.Other is null && link.Outside).Select(link => link.Door);
        var finals = Out(stair.Foot).Concat(links.Where(link => link.Room == stair.Foot && link.Other is { } next && !stair.Rooms.Contains(next)).SelectMany(link => Out(link.Other!))).Distinct().ToList();
        if (finals.Count == 0)
            wanting.Add($"its foot, the {stair.Foot.Name}, has no final exit - Approved Document B Volume 2 (3.36) asks every protected stairway to lead to a final exit, straight out or through an exit passageway");
        else
        {
            // As wide as the stair needs to be: Table 3.1's least, or Table 3.2's for its share of the people above (5.20, round 114).
            var needed = Needed(least, upper.Count, share);
            var widest = finals.Max(door => document.FindType<DoorType>(door.TypeId) is { } type ? Doors.EscapeWidth(type) : 0);
            if (widest < needed - 0.5)
                wanting.Add($"its final exit gives {widest:0} where the stair needs to be {needed:0} wide{(share > 0 && needed > least + 0.5 ? $" for {share} people from {upper.Count} storey{(upper.Count == 1 ? string.Empty : "s")}" : string.Empty)} - Approved Document B Volume 2 (5.20) asks a final exit at least as wide as the escape route it serves needs to be");
        }

        return wanting;
    }

    /// <summary>The width a stair needs to be: Table 3.1's least, or the least that takes its share of the people above by Table 3.2 and 3.18, to 10 mm.</summary>
    public static double Needed(double least, int floors, int share)
    {
        var width = least;
        while (share > 0 && Capacity(width, floors) < share && width < 10000) width += 10;
        return width;
    }

    /// <summary>Whether a stair is reached on a storey only through a lobby or a corridor - not straight from the accommodation.</summary>
    private static bool Lobbied(BimDocument document, StairStack stair, Guid level, List<Escape.Link> links) =>
        links.Where(link => link.Room.LevelId == level && stair.Rooms.Contains(link.Room) && link.Other is { } beyond && !stair.Rooms.Contains(beyond))
            .All(link => Named(link.Other!, "lobby") || link.Other!.GetBoundary(document) is { IsEnclosed: true } boundary && Circulation.IsCorridor(link.Other!, boundary.Polygon));

    /// <summary>Whether there is a wheelchair space - a refuge - in a stair's room on a storey, or in a lobby or corridor opening into it.</summary>
    private static bool Refuge(BimDocument document, StairStack stair, Room room, List<Escape.Link> links)
    {
        var near = new List<Room> { room };
        near.AddRange(links.Where(link => link.Room == room && link.Other is { } beyond && !stair.Rooms.Contains(beyond) && FireSeparation.Circulation(beyond)).Select(link => link.Other!));
        return near.Any(each => each.GetBoundary(document) is { IsEnclosed: true } boundary && document.All<Component>().Any(piece => piece.LevelId == each.LevelId
            && Polygon2D.Contains(boundary.Polygon, piece.Location) && document.FindType<ComponentType>(piece.TypeId) is { Form: ComponentForm.WheelchairSpace, Width: >= 900 - 0.5, Depth: >= 1400 - 0.5 }));
    }

    /// <summary>A stair room's verdict - "Escape stair, 1100 wide, serving 3 storeys above the ground; ..." - or null for a room that is no stair's.</summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        if (!HorizontalEscape.Applies(document) || Stairs(document).FirstOrDefault(stair => stair.Rooms.Contains(room)) is not { } stair) return null;
        var said = $"Escape stair, {stair.Width:0} wide, serving {stair.Upper.Count} storey{(stair.Upper.Count == 1 ? string.Empty : "s")} above the ground";
        var wanting = Problems(document, stair);
        return wanting.Count == 0 ? said : $"{said}; {string.Join("; ", wanting)}";
    }

    /// <summary>Every stair's problems, said on its foot, for the Warnings window.</summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!HorizontalEscape.Applies(document)) yield break;
        foreach (var stair in Stairs(document))
        foreach (var said in Problems(document, stair))
            yield return new EscapeProblem(stair.Foot, said);
    }
}
