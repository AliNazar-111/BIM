using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Means of escape by storey (round 87), as Approved Document B Volume 1 sets it out: a house with a storey more than 7.5 m above the
/// ground (2.6) - and a flat with its own entrance at the ground, planned as a house (3.20); a flat over more than one storey entered from the
/// common parts (3.21, 3.22); basements with habitable rooms (2.16, 3.9); and galleries (2.15, 3.13).
/// </summary>
public static partial class Escape
{
    /// <summary>The highest a storey of a house may be above the ground without an alternative escape route or sprinklers (2.6), mm.</summary>
    public const double Higher = 7500;

    /// <summary>How far below the ground a storey's floor is a basement's (Appendix A: more than 1200 mm below the ground beside it), mm.</summary>
    public const double Basement = 1200;

    /// <summary>A small single stair building's top storey at most so far above the ground (3.28a), mm.</summary>
    public const double SmallTop = 11000;

    /// <summary>Diagram 2.6 (3.1): every point of a gallery without an alternative exit within 7.5 m of the head of its stair, and its stair's foot within 3 m of the room's exit, mm.</summary>
    public const double GalleryRun = 7500, GalleryFoot = 3000;

    /// <summary>What the checks of a model's escape share: its rooms' outlines, its doors' sides, the stairs between its rooms, and how its rooms are told.</summary>
    private sealed record Plan(BimDocument Document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> Outlines, List<Link> Links, List<Flight> Flights,
        Func<Room, bool> Circulation, Func<Room, double> Above);

    /// <summary>A stair walked: the room at its foot and where its walk starts, the room at its head and where it ends - none where that is in no room - and how long it is walked, rise and all.</summary>
    internal sealed record Flight(Stair Stair, Room? Foot, Point2D FootAt, Room? Head, Point2D HeadAt, double Length);

    /// <summary>The ground floor's level, taken as the ground's: the lowest level not a basement - not more than 1.2 m below the project's base.</summary>
    public static double Ground(BimDocument document) =>
        document.Levels.Where(level => level.Elevation >= -Basement - 0.5).Select(level => level.Elevation).DefaultIfEmpty(0).Min();

    /// <summary>The stairs that rise from one level to another, walked along their walking lines, and the rooms at their feet and heads.</summary>
    internal static List<Flight> Flights(BimDocument document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> rooms)
    {
        var flights = new List<Flight>();
        foreach (var stair in document.All<Stair>().Where(stair => stair.TopLevelId is not null))
        {
            StairLayout layout;
            try { layout = Stairs.Layout(document, stair); }
            catch (Exception) { continue; }
            var line = layout.WalkingLine;
            if (line.Count < 2) continue;
            Room? In(Guid level, Point2D at) => rooms.FirstOrDefault(entry => entry.Key.LevelId == level && Polygon2D.Contains(entry.Value, at)).Key;
            var plan = line.Zip(line.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
            var rise = (document.FindLevel(stair.TopLevelId!.Value)?.Elevation ?? 0) - (document.FindLevel(stair.LevelId)?.Elevation ?? 0);
            flights.Add(new Flight(stair, In(stair.LevelId, line[0]), line[0], In(stair.TopLevelId.Value, line[^1]), line[^1], Math.Sqrt(plan * plan + rise * rise)));
        }

        return flights;
    }

    /// <summary>Whether a home has a way in of its own from outside at the ground - a flat planned as a house (3.20).</summary>
    private static bool OwnEntrance(Plan plan, HashSet<Room> own) =>
        plan.Links.Any(link => own.Contains(link.Room) && link.Other is null && link.Outside && Math.Abs(plan.Above(link.Room)) <= Basement + 0.5);

    /// <summary>
    /// A house's storeys more than 7.5 m above the ground (2.6, and 3.20 for a flat of its own at the ground): each with an alternative
    /// escape route - a second stair down into another room of it, or a door out to a stair outside rising to it, or one of those on a storey
    /// above it, reached up its stair - or the house sprinklered throughout.
    /// </summary>
    private static IEnumerable<EscapeProblem> HighStoreys(Plan plan, HashSet<Room> own, string name)
    {
        var storeys = own.Where(room => !Excused(room)).GroupBy(room => room.LevelId).Where(storey => plan.Above(storey.First()) > Higher + 0.5)
            .OrderBy(storey => plan.Above(storey.First())).Select(storey => storey.ToHashSet()).ToList();
        if (storeys.Count == 0 || Sprinklered(plan, own)) yield break;

        var alternative = storeys.Select(storey => Alternative(plan, own, storey)).ToList();
        for (var i = 0; i < storeys.Count; i++)
        {
            if (alternative.Skip(i).Any(each => each)) continue;
            var storey = storeys[i];
            var at = plan.Flights.FirstOrDefault(flight => flight.Head is { } head && storey.Contains(head))?.Head ?? storey.OrderBy(room => room.Name, NaturalOrder.Instance).First();
            var level = plan.Document.FindLevel(at.LevelId)?.Name ?? "a storey";
            yield return new EscapeProblem(at, $"{(name.Length > 0 ? $"{name}: " : string.Empty)}{level} is {Units.Metres(plan.Above(at), "0.0")} above the ground, with no alternative escape route from it - Approved Document B ({(name.Length > 0 ? "2.6, 3.20" : "2.6")}) asks one from each storey more than 7.5 m up - a second stair down, or a door out to an escape stair - or sprinklers throughout (BS 9251)");
        }
    }

    /// <summary>Whether a storey has a second way down: two stairs arriving in different rooms of it, or a door out of it and a stair outside rising to its level.</summary>
    private static bool Alternative(Plan plan, HashSet<Room> own, HashSet<Room> storey)
    {
        var level = storey.First().LevelId;
        var down = plan.Flights.Where(flight => flight.Head is { } head && storey.Contains(head) && flight.Foot is { } foot && own.Contains(foot)).Select(flight => flight.Head!).Distinct().Count();
        if (down >= 2) return true;
        return plan.Links.Any(link => storey.Contains(link.Room) && link.Other is null && link.Outside)
               && plan.Flights.Any(flight => flight.Head is null && flight.Stair.TopLevelId == level);
    }

    /// <summary>Whether a home is sprinklered throughout: the project said to be, or a sprinkler in each of its rooms BS 9251 asks one in.</summary>
    private static bool Sprinklered(Plan plan, IEnumerable<Room> own)
    {
        if (plan.Document.ProjectInformation.Sprinklered) return true;
        var heads = Sprinklers.All(plan.Document);
        return heads.Count > 0 && own.All(room => Sprinklers.Exempt(plan.Document, room, plan.Outlines[room]) is not null
                                                  || heads.Any(head => head.LevelId == room.LevelId && Polygon2D.Contains(plan.Outlines[room], head.Location)));
    }

    /// <summary>
    /// Whether a stair is a protected stairway (2.5a, 3.16b, Diagram 2.2), and if not what is wanting: the rooms it rises through - and the
    /// halls and landings joined to them, and the stairs on from them - none a habitable room, entered from the home's habitable rooms only
    /// by fire doors, FD20 at least (bathrooms excepted, the enclosure's REI 30 taken as built), and reaching a way out: an outside door, or
    /// for a flat's own stair its entrance door. Null where it is protected.
    /// </summary>
    private static string? Unprotected(Plan plan, Flight flight, HashSet<Room> own, bool finalExit, bool onward)
    {
        var enclosure = new HashSet<Room>();
        var queue = new Queue<Room>();
        void Take(Room? room)
        {
            if (room is not null && own.Contains(room) && enclosure.Add(room)) queue.Enqueue(room);
        }

        Take(flight.Foot);
        Take(flight.Head);
        while (queue.Count > 0)
        {
            var room = queue.Dequeue();
            foreach (var link in plan.Links.Where(link => link.Room == room && link.Other is { } other && plan.Circulation(other))) Take(link.Other);
            if (!onward) continue;
            foreach (var next in plan.Flights.Where(next => next.Foot == room || next.Head == room))
            {
                Take(next.Foot);
                Take(next.Head);
            }
        }

        if (enclosure.FirstOrDefault(room => Habitable(plan.Document, plan.Outlines, room) && !Named(room, "hall", "landing", "corridor", "lobby", "stair", "entrance", "porch")) is { } open)
            return $"it rises through the {open.Name}";
        foreach (var link in plan.Links.Where(link => enclosure.Contains(link.Room) && link.Other is { } other && !enclosure.Contains(other) && own.Contains(other)
                                                     && Habitable(plan.Document, plan.Outlines, other)))
            if (plan.Document.FindType<DoorType>(link.Door.TypeId) is not { } type || Doors.FireMinutes(type.FireRating) < 20)
                return $"the {link.Other!.Name}'s door onto it is not a fire door";
        var out_ = plan.Links.Any(link => enclosure.Contains(link.Room) && (link.Other is { } other ? !finalExit && !own.Contains(other) : link.Outside));
        return out_ ? null : finalExit ? "it reaches no door out" : "it reaches no entrance door of the flat";
    }

    /// <summary>Whether a flat's entrance hall is protected (3.18a): the room its entrance door opens into a hall, and every door off it to a habitable room a fire door, FD20 at least.</summary>
    private static bool HallProtected(BimDocument document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> outlines, List<Link> links, HashSet<Room> own, Room hall, Func<Room, bool> circulation) =>
        circulation(hall) && links.Where(link => link.Room == hall && link.Other is { } other && own.Contains(other) && Habitable(document, outlines, other))
            .All(link => document.FindType<DoorType>(link.Door.TypeId) is { } type && Doors.FireMinutes(type.FireRating) >= 20);

    /// <summary>
    /// A flat over more than one storey, entered from the common parts, a storey of it more than 4.5 m above the ground (3.21): one of
    /// an alternative exit from each habitable room off its entrance storey (Diagram 3.5); one from each storey off it, its habitable rooms
    /// opening onto a protected landing (Diagram 3.6); a protected stairway and sprinklers; or - each storey within 7.5 m of the entrance
    /// storey - a protected stairway, smoke alarms in its habitable rooms and a heat alarm in its kitchen. An alternative exit a door out of
    /// the flat other than its main entrance - to the common parts, or outside (3.22).
    /// </summary>
    private static EscapeProblem? MultiStorey(Plan plan, HashSet<Room> own, string name)
    {
        var levels = own.Select(room => room.LevelId).Distinct().Count();
        if (levels < 2 || own.Max(plan.Above) <= High + 0.5) return null;
        var entrances = plan.Links.Where(link => own.Contains(link.Room) && link.Other is { } other && !own.Contains(other)).OrderBy(link => plan.Above(link.Room)).ToList();
        if (entrances.Count == 0) return null;
        var main = entrances[0];
        var entrance = plan.Above(main.Room);
        var document = plan.Document;
        bool Habitable(Room room) => Escape.Habitable(document, plan.Outlines, room);
        bool Exit(Room room) => plan.Links.Any(link => link.Room == room && link.Door != main.Door && (link.Other is { } other ? !own.Contains(other) : link.Outside));
        var off = own.Where(room => room.LevelId != main.Room.LevelId).ToList();

        // Approach 1: an alternative exit from each habitable room off the entrance storey.
        if (off.Where(Habitable).All(Exit)) return null;

        // Approach 2: one from each storey off it, and its habitable rooms opening onto a protected landing.
        bool Landed(Room room)
        {
            var onto = plan.Links.Where(link => link.Room == room && link.Other is { } other && own.Contains(other) && plan.Circulation(other) && !Habitable(other)).ToList();
            return onto.Count > 0 && onto.All(link => document.FindType<DoorType>(link.Door.TypeId) is { } type && Doors.FireMinutes(type.FireRating) >= 20);
        }

        if (off.GroupBy(room => room.LevelId).All(storey => storey.Any(Exit) && storey.Where(Habitable).All(Landed))) return null;

        // Approaches 3 and 4: a protected stairway, and sprinklers - or, within 7.5 m of the entrance storey, alarms.
        var stair = plan.Flights.FirstOrDefault(flight => flight.Foot is { } foot && own.Contains(foot) && flight.Head is { } head && own.Contains(head));
        var unprotected = stair is null ? "it has no stair of its own" : Unprotected(plan, stair, own, finalExit: false, onward: true);
        if (unprotected is null && Sprinklered(plan, own)) return null;
        var near = own.All(room => Math.Abs(plan.Above(room) - entrance) <= Higher + 0.5);
        var unalarmed = own.Where(Habitable).OrderBy(room => room.Name, NaturalOrder.Instance)
            .Select(room => (Room: room, Kitchen: Lighting.KindOf(document, room, plan.Outlines[room]) == LitRoom.Kitchen || Named(room, "kitchen")))
            .FirstOrDefault(entry => !AlarmIn(plan, entry.Room, entry.Kitchen ? ComponentForm.HeatAlarm : ComponentForm.SmokeAlarm));
        if (near && unprotected is null && unalarmed.Room is null) return null;

        var why = unprotected is not null ? $"its stair is not protected - {unprotected}"
            : !near ? "it has no sprinklers, and a storey more than 7.5 m from its entrance storey"
            : $"there is no {(unalarmed.Kitchen ? "heat" : "smoke")} alarm in the {unalarmed.Room!.Name}";
        return new EscapeProblem(main.Room, $"{name}: a flat on {levels} storeys, entered from the common parts, a storey of it more than 4.5 m up - Approved Document B (3.21) asks an alternative exit from each habitable room off its entrance storey, or from each storey off it with its habitable rooms on a protected landing, or a protected stairway and sprinklers, or - each storey within 7.5 m of its entrance storey - a protected stairway, smoke alarms in its habitable rooms and a heat alarm in its kitchen; {why}");
    }

    /// <summary>Whether a room has an alarm of a kind in it.</summary>
    private static bool AlarmIn(Plan plan, Room room, ComponentForm form) =>
        plan.Document.All<Component>().Any(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(plan.Outlines[room], piece.Location)
                                                    && plan.Document.FindType<ComponentType>(piece.TypeId)?.Form == form);

    /// <summary>
    /// A home's basement storeys with habitable rooms (2.16, 3.9): each with an escape window (2.10) or a door out of it, or a protected
    /// stairway from it to a final exit - its stair's rooms entered from habitable rooms only by fire doors, and an outside door off them.
    /// </summary>
    private static IEnumerable<EscapeProblem> Basements(Plan plan, HashSet<Room> own, string name)
    {
        foreach (var storey in own.Where(room => plan.Above(room) < -Basement - 0.5).GroupBy(room => room.LevelId))
        {
            var here = storey.ToHashSet();
            var habitable = here.Where(room => Habitable(plan.Document, plan.Outlines, room)).OrderBy(room => room.Name, NaturalOrder.Instance).ToList();
            if (habitable.Count == 0) continue;
            if (plan.Links.Any(link => here.Contains(link.Room) && link.Other is null && link.Outside)) continue;
            if (here.Any(room => WindowRules.Of(plan.Document, room, plan.Outlines[room]).Windows.Any(entry => WindowRules.Escapes(entry.Type, entry.Sill, false, false)))) continue;

            var stairs = plan.Flights.Where(flight => flight.Foot is { } foot && here.Contains(foot) && flight.Head is { } head && own.Contains(head)).ToList();
            var why = stairs.Select(flight => Unprotected(plan, flight, own, finalExit: true, onward: false)).ToList();
            if (why.Any(each => each is null)) continue;
            var detail = stairs.Count == 0 ? "no stair up out of it" : $"its stair is not a protected stairway - {why[0]}";
            yield return new EscapeProblem(habitable[0], $"{(name.Length > 0 ? $"{name}: " : string.Empty)}it is in a basement, with no escape window or door out of it, and {detail} - Approved Document B ({(name.Length > 0 ? "3.9" : "2.16")}) asks one of them of a basement with habitable rooms");
        }
    }

    /// <summary>
    /// Galleries (2.15, 3.13): a room on a storey over part of a room below that rises as high as it does - open to it, a floor not across
    /// the whole of it (Appendix A). Each with an alternative exit - a door out of it - or an escape window where its floor is 4.5 m or less
    /// above the ground, or Diagram 2.6's conditions: half the room below at least left open to view from it, every point of it within 7.5 m
    /// of the head of its stair, and the stair's foot within 3 m of the way out of the room below.
    /// </summary>
    private static IEnumerable<EscapeProblem> Galleries(Plan plan, bool named)
    {
        var document = plan.Document;
        double Floor(Room room) => document.FindLevel(room.LevelId)?.Elevation ?? 0;
        foreach (var (gallery, outline) in plan.Outlines)
        {
            var floor = Floor(gallery);
            var top = floor + gallery.UpperLimitOffset;
            var under = plan.Outlines.Where(entry => Floor(entry.Key) < floor - 0.5 && Floor(entry.Key) + entry.Key.UpperLimitOffset >= top - 0.5 && Polygon2D.Contains(entry.Value, gallery.Location))
                .OrderByDescending(entry => Floor(entry.Key)).Select(entry => entry.Key).FirstOrDefault();
            if (under is null) continue;
            if (plan.Links.Any(link => link.Room == gallery && (link.Other is not null || link.Outside))) continue;
            var above = plan.Above(gallery);
            if (above <= High + 0.5 && WindowRules.Of(document, gallery, outline).Windows.Any(entry => WindowRules.Escapes(entry.Type, entry.Sill, false, false))) continue;

            var wanting = new List<string>();
            var (area, below) = (Math.Abs(Polygon2D.SignedArea(outline)), Math.Abs(Polygon2D.SignedArea(plan.Outlines[under])));
            if (area > below / 2 + 1) wanting.Add($"half the {under.Name} at least left open to view from it - it covers {100 * area / below:0}% of it");
            var stair = plan.Flights.FirstOrDefault(flight => flight.Foot == under && flight.Head == gallery);
            if (stair is null) wanting.Add($"a stair down from it into the {under.Name}");
            else
            {
                var walk = new Walk(plan.Outlines, Array.Empty<Link>());
                var fromHead = walk.From(gallery, stair.HeadAt);
                var furthest = Points(outline).Select(point => fromHead(gallery, point)).OfType<double>().DefaultIfEmpty(0).Max();
                if (furthest > GalleryRun + 0.5) wanting.Add($"every point of it within 7.5 m of the head of its stair - its furthest is {Units.Metres(furthest, "0.0")}");
                var fromFoot = walk.From(under, stair.FootAt);
                var exit = plan.Links.Where(link => link.Room == under && (link.Other is not null || link.Outside)).Select(link => fromFoot(under, link.Inside)).OfType<double>().DefaultIfEmpty(double.NaN).Min();
                if (double.IsNaN(exit)) wanting.Add($"a way out of the {under.Name}");
                else if (exit > GalleryFoot + 0.5) wanting.Add($"the foot of its stair within 3 m of the way out of the {under.Name} - it is {Units.Metres(exit, "0.0")}");
            }

            if (wanting.Count == 0) continue;
            yield return new EscapeProblem(gallery, $"it is a gallery over the {under.Name}, with no alternative exit{(above <= High + 0.5 ? " or escape window" : string.Empty)} - Approved Document B ({(named ? "3.13, Diagram 3.1" : "2.15, Diagram 2.6")}) then asks {string.Join(", and ", wanting)}");
        }
    }
}
