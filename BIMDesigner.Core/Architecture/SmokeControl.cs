using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Smoke control (round 93), as Approved Document B Volume 1 sets it out for a block of flats whose top storey is more than 4.5 m up (3.25):
///
/// The common corridor or lobby next to each stair, on each storey with flats, a smoke vent on an outside wall of 1.5 m² free area at least,
/// its top at least as high as the top of the door to the stair (3.50, 3.51a); the stair's top storey a vent to the outside of 1 m² (3.52);
/// with only one stair, the vents opened by smoke detectors in the common parts (3.53). A small single stair building (3.28, Diagram 3.9)
/// instead an openable vent of 1 m² at high level at each storey, or one at the head of the stair opened from the fire and rescue service's
/// access level (3.28e); and where a flat opens straight onto its stair, an automatic opening vent of 1 m² at its top, opened on detecting
/// smoke at any storey in it (Diagram 3.9, note 3).
///
/// A basement storey (Section 16) - more than 200 m², or its floor more than 3 m below the ground (16.3), and no room in it with a door or
/// window to the open air (16.2) - smoke outlets to the open air: in each room of it, or a door from it to a room with one, a place of
/// special fire hazard its own (16.2, 16.7); at high level (16.5a), at an outside wall or under no room of the storey above (16.3); their
/// clear areas together 1/40 of its floor's (16.6). Strong rooms need none (16.4).
///
/// A vent is a window or roof window whose type says it is a smoke vent; its free area by Appendix D5. Smoke shafts (3.51b) and mechanical
/// smoke control (3.54, 16.11) are not modelled.
/// </summary>
public static partial class Escape
{
    /// <summary>The least free area of a smoke vent from a corridor or lobby next to a stair (3.51a), and of one at a stair's head or on its storeys (3.52, 3.28e, Diagram 3.9 note 3), m².</summary>
    public const double CorridorVent = 1.5, StairVent = 1.0;

    /// <summary>A basement storey needs no smoke outlets of 200 m² or less with its floor 3 m (mm) or less below the ground (16.3); outlets' clear area together 1/40 of its floor's (16.6).</summary>
    public const double SmallBasementArea = 200, ShallowBasement = 3000, OutletShare = 1.0 / 40;

    /// <summary>A small single stair building's top storey 11 m up at most, and no more than three storeys over the ground storey (3.28a, b).</summary>
    private static bool SmallBuilding(Plan plan) =>
        plan.Outlines.Keys.Max(plan.Above) <= SmallTop + 0.5 && plan.Outlines.Keys.Where(room => plan.Above(room) > 0.5).Select(room => room.LevelId).Distinct().Count() <= 3;

    /// <summary>The smoke vents of the common corridors and lobbies next to the common stairs, and at the stairs' heads (3.28e, 3.50 to 3.53, Diagram 3.9).</summary>
    private static IEnumerable<EscapeProblem> SmokeControl(Plan plan, List<(string Name, HashSet<Room> Rooms)> homes)
    {
        var (document, links) = (plan.Document, plan.Links);
        if (!homes.Any(home => home.Rooms.Max(plan.Above) > High + 0.5)) yield break;
        var common = plan.Outlines.Keys.Where(room => MixedUse.Common(plan.Document, room)).ToHashSet();
        var flights = plan.Flights.Where(flight => flight.Foot is { } foot && common.Contains(foot) && flight.Head is { } head && common.Contains(head)).ToList();
        if (flights.Count == 0) yield break;

        var stairs = Stacks(flights);
        var stairRooms = stairs.SelectMany(stack => stack).ToHashSet();
        var single = stairs.Count == 1;
        var withFlats = homes.SelectMany(home => home.Rooms).Select(room => room.LevelId).ToHashSet();
        double Floor(Room room) => document.FindLevel(room.LevelId)?.Elevation ?? 0;
        string Opened(SmokeVentKind kind) => kind switch { SmokeVentKind.Manual => "by hand", SmokeVentKind.Remote => "from a switch", _ => "on smoke" };

        // Beside each stair room, on the storeys with flats: the common corridors and lobbies its doors open into, and how high the doors' tops are.
        var beside = links.Where(link => stairRooms.Contains(link.Room) && link.Other is { } other && common.Contains(other) && !stairRooms.Contains(other) && plan.Circulation(other)
                                         && withFlats.Contains(other.LevelId))
            .GroupBy(link => link.Other!)
            .ToDictionary(group => group.Key, group => group.Max(link => link.Door.HeightOf(document.FindType<DoorType>(link.Door.TypeId))));

        if (single && SmallBuilding(plan))
        {
            // A small single stair building (Diagram 3.9): flats straight onto the stair want an automatic vent at its top (note 3); and
            // either an openable vent of 1 m² at high level on each storey, or one at the stair's head opened remotely (3.28e).
            var stack = stairs[0];
            var top = stack.MaxBy(Floor)!;
            var head = VentsOf(plan, top, roof: true);
            var onto = links.Where(link => stack.Contains(link.Room) && link.Other is { } flat && Dwellings.Of(flat).Length > 0).Select(link => link.Room).Distinct().ToList();
            if (onto.Count > 0)
            {
                if (!head.Any(vent => vent.FreeArea >= StairVent - 0.005 && vent.Kind == SmokeVentKind.Automatic))
                    yield return new EscapeProblem(top, $"flats open straight onto the stair, no lobby between, and {HeadSaid(head, vent => vent.Kind == SmokeVentKind.Automatic, Opened)} - Approved Document B (Diagram 3.9, note 3) then asks an automatic opening vent of 1 m² free area at least at the top of the stair, opened on detecting smoke at any storey in it");
                foreach (var room in onto.Where(room => !AlarmIn(plan, room, ComponentForm.SmokeAlarm)).OrderBy(Floor))
                    yield return new EscapeProblem(room, "a flat opens onto the stair here, and no smoke detector in it opens the vent at the stair's top - Approved Document B (Diagram 3.9, note 3) asks the vent opened on detecting smoke at any storey in the stair");
            }

            if (head.Any(vent => vent.FreeArea >= StairVent - 0.005 && SmokeVents.Remotely(vent.Kind))) yield break;
            var lacking = new List<Room>();
            foreach (var room in stack.OrderBy(Floor))
            {
                // On its storey: a vent in the stair, or in a corridor or lobby beside it, of 1 m² with its top as high as the door to the stair.
                var doorTop = links.Where(link => link.Room == room).Select(link => link.Door.HeightOf(document.FindType<DoorType>(link.Door.TypeId))).DefaultIfEmpty(0).Max();
                var near = beside.Keys.Where(other => other.LevelId == room.LevelId && links.Any(link => link.Room == room && link.Other == other)).Prepend(room);
                if (!near.SelectMany(each => VentsOf(plan, each, roof: each == top)).Any(vent => vent.FreeArea >= StairVent - 0.005 && vent.Top - Floor(room) >= doorTop - 0.5))
                    lacking.Add(room);
            }

            if (lacking.Count > 0)
                yield return new EscapeProblem(top, $"the stair has no vent opened from the fire and rescue service's access level at its head, and {(lacking.Count == stack.Count ? "no storey of it" : $"{string.Join(", ", lacking.Select(room => document.FindLevel(room.LevelId)?.Name ?? room.Name))} of it")} an openable vent at high level - Approved Document B (3.28e) asks a small single stair building one of them, each vent 1 m² of free area at least");
            yield break;
        }

        // The corridor or lobby next to each stair (3.50, 3.51a; with one stair, 3.53).
        foreach (var (corridor, doorTop) in beside.OrderBy(pair => Floor(pair.Key)).ThenBy(pair => pair.Key.Name, NaturalOrder.Instance))
        {
            var floor = Floor(corridor);
            var vents = VentsOf(plan, corridor, roof: false);
            if (vents.Count == 0)
            {
                yield return new EscapeProblem(corridor, $"it is next to a common stair with no smoke vent - Approved Document B (3.50, 3.51a) asks the corridor or lobby next to each stair a smoke vent on an outside wall of 1.5 m² free area at least, its top as high as the top of the door to the stair{(single ? ", and with only one stair opened by smoke detectors in the common parts (3.53)" : string.Empty)}: a window whose type is a smoke vent - or a smoke shaft (3.51b), or mechanical smoke control (3.54)");
                continue;
            }

            List<string> Short(SmokeVents.Vent vent)
            {
                var wanting = new List<string>();
                if (vent.FreeArea < CorridorVent - 0.005) wanting.Add($"{vent.FreeArea:0.00} m² of free area, where 3.51a asks 1.5 m²");
                if (vent.Top - floor < doorTop - 0.5) wanting.Add($"its top {vent.Top - floor:0} mm up, under the top of the door to the stair at {doorTop:0} mm (3.50b)");
                if (single && vent.Kind != SmokeVentKind.Automatic) wanting.Add($"it is opened {Opened(vent.Kind)}, where with only one stair 3.53 asks it opened by smoke detectors in the common parts");
                return wanting;
            }

            var best = vents.OrderBy(vent => Short(vent).Count).ThenByDescending(vent => vent.FreeArea).First();
            var shortfalls = Short(best);
            if (shortfalls.Count > 0)
                yield return new EscapeProblem(corridor, $"its smoke vent, {best.Name}, falls short of Approved Document B: {string.Join("; ", shortfalls)}");
            else if (single && !AlarmIn(plan, corridor, ComponentForm.SmokeAlarm))
                yield return new EscapeProblem(corridor, "its smoke vent opens on smoke, but no smoke detector in it - Approved Document B (3.53) asks the vents of a single stair building opened by smoke detectors in the common parts");
        }

        // The top storey of each stair: a vent to the outside of 1 m² (3.52), with one stair opened on smoke (3.53).
        foreach (var stack in stairs)
        {
            var top = stack.MaxBy(Floor)!;
            var head = VentsOf(plan, top, roof: true);
            if (head.Any(vent => vent.FreeArea >= StairVent - 0.005 && (!single || vent.Kind == SmokeVentKind.Automatic))) continue;
            yield return new EscapeProblem(top, $"it is the top storey of a common stair, and {HeadSaid(head, vent => !single || vent.Kind == SmokeVentKind.Automatic, Opened)} - Approved Document B (3.52) asks a vent to the outside of 1 m² free area at least from it{(single ? ", with only one stair opened by smoke detectors in the common parts (3.53)" : string.Empty)}");
        }
    }

    /// <summary>What a stair's head vents fall short of: none at all, too small, or not opened as asked.</summary>
    private static string HeadSaid(List<SmokeVents.Vent> head, Func<SmokeVents.Vent, bool> opened, Func<SmokeVentKind, string> how)
    {
        if (head.Count == 0) return "no smoke vent - a window or roof window whose type is one";
        var best = head.OrderByDescending(vent => vent.FreeArea >= StairVent - 0.005 && opened(vent)).ThenByDescending(vent => vent.FreeArea).First();
        return best.FreeArea < StairVent - 0.005 ? $"its smoke vent, {best.Name}, has {best.FreeArea:0.00} m² of free area" : $"its smoke vent, {best.Name}, is opened {how(best.Kind)}";
    }

    /// <summary>The stairs: the stair rooms their flights join, those joined by flights one stair.</summary>
    internal static List<List<Room>> Stacks(List<Flight> flights)
    {
        var parent = new Dictionary<Room, Room>();
        Room Find(Room room)
        {
            while (parent[room] != room) room = parent[room] = parent[parent[room]];
            return room;
        }

        foreach (var flight in flights)
        {
            parent.TryAdd(flight.Foot!, flight.Foot!);
            parent.TryAdd(flight.Head!, flight.Head!);
            var (a, b) = (Find(flight.Foot!), Find(flight.Head!));
            if (a != b) parent[a] = b;
        }

        return parent.Keys.GroupBy(Find).Select(group => group.ToList()).ToList();
    }

    /// <summary>A room's smoke vents: the windows in its walls that open to no room beyond, and - asked for - the roof windows over it.</summary>
    private static List<SmokeVents.Vent> VentsOf(Plan plan, Room room, bool roof)
    {
        var document = plan.Document;
        var vents = OutsideWindows(plan, room).Select(window => SmokeVents.Of(document, window)).OfType<SmokeVents.Vent>().ToList();
        if (!roof) return vents;

        var outline = plan.Outlines[room];
        var floor = document.FindLevel(room.LevelId)?.Elevation ?? 0;
        var next = document.Levels.Where(level => level.Elevation > floor + 1).Select(level => level.Elevation).DefaultIfEmpty(double.MaxValue).Min();
        foreach (var window in document.All<RoofWindow>())
            if (RoofWindows.Frame(document, window) is { } frame && Polygon2D.Contains(outline, frame.Centre) && frame.Corners.Min(frame.Top) is var low && low > floor + 1 && low < next
                && SmokeVents.Of(document, window) is { } vent)
                vents.Add(vent);
        return vents;
    }

    /// <summary>The windows in a room's walls with no room beyond them on its storey - to the open air, or a light well.</summary>
    private static IEnumerable<Window> OutsideWindows(Plan plan, Room room)
    {
        var (document, outline) = (plan.Document, plan.Outlines[room]);
        foreach (var window in document.All<Window>())
        {
            if (document.Find<Wall>(window.HostWallId) is not { } wall || wall.LevelId != room.LevelId || document.GetWallType(wall) is not { } type) continue;
            var reach = type.Structure.TotalWidth / 2 + 150;
            var (a, b) = (wall.PointAt(type.Structure, window.DistanceAlongWall, reach), wall.PointAt(type.Structure, window.DistanceAlongWall, -reach));
            var beyond = Polygon2D.Contains(outline, a) ? b : Polygon2D.Contains(outline, b) ? a : (Point2D?)null;
            if (beyond is { } there && !plan.Outlines.Any(entry => entry.Key.LevelId == room.LevelId && Polygon2D.Contains(entry.Value, there)))
                yield return window;
        }
    }

    /// <summary>A basement storey's smoke outlets (Section 16), in a block of flats.</summary>
    private static IEnumerable<EscapeProblem> BasementSmoke(Plan plan)
    {
        var (document, outlines, links) = (plan.Document, plan.Outlines, plan.Links);
        var stairRooms = plan.Flights.SelectMany(flight => new[] { flight.Foot, flight.Head }).OfType<Room>().ToHashSet();
        var outlets = document.All<Component>().Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type?.Form == ComponentForm.SmokeOutlet).Select(entry => (entry.Piece, Type: entry.Type!)).ToList();
        foreach (var storey in outlines.Keys.Where(room => plan.Above(room) < -Basement - 0.5).GroupBy(room => room.LevelId))
        {
            var rooms = storey.OrderBy(room => room.Name, NaturalOrder.Instance).ToList();
            var area = rooms.Sum(room => Math.Abs(Polygon2D.SignedArea(outlines[room]))) / 1e6;
            var depth = -plan.Above(rooms[0]);
            if (area <= SmallBasementArea + 1e-6 && depth <= ShallowBasement + 0.5) continue;
            if (rooms.Any(room => links.Any(link => link.Room == room && link.Other is null && link.Outside) || OutsideWindows(plan, room).Any())) continue;

            // Its outlets, each in a room of it: at high level, and to the open air - at an outside wall, or under no room of the storey above.
            var level = document.FindLevel(storey.Key);
            var above = document.Levels.Where(each => each.Elevation > (level?.Elevation ?? 0) + 1 && outlines.Keys.Any(room => room.LevelId == each.Id))
                .OrderBy(each => each.Elevation).FirstOrDefault();
            var vented = new Dictionary<Room, double>();
            foreach (var (piece, type) in outlets.Where(entry => entry.Piece.LevelId == storey.Key))
            {
                if (rooms.FirstOrDefault(room => Polygon2D.Contains(outlines[room], piece.Location)) is not { } room) continue;
                var wanting = new List<string>();
                if (type.Capacity <= 0) wanting.Add("no clear area given in its type");
                if (piece.Elevation < room.Height / 2 - 0.5) wanting.Add($"it is low, its bottom {piece.Elevation:0} mm up a room {room.Height:0} mm high - taken as at high level in its upper half (16.5a)");
                var underOpen = above is null || !outlines.Any(entry => entry.Key.LevelId == above.Id && Polygon2D.Contains(entry.Value, piece.Location));
                if (!underOpen && !AtOutsideWall(plan, room, piece.Location, type.Depth / 2 + 300))
                    wanting.Add("it reaches no open air - neither at an outside wall nor under no room of the storey above (16.3)");
                if (wanting.Count > 0)
                    yield return new EscapeProblem(room, $"its smoke outlet, the {type.Name}: {string.Join("; ", wanting)} - Approved Document B (Section 16)");
                else vented[room] = vented.GetValueOrDefault(room) + type.Capacity;
            }

            // Each room of it an outlet, or a door into a room with one - a place of special fire hazard its own (16.2, 16.7).
            foreach (var room in rooms.Where(room => !stairRooms.Contains(room) && !Named(room, "strong room") && !vented.ContainsKey(room)))
            {
                var special = Named(room, "transformer", "switch", "boiler", "fuel", "generator", "engine");
                if (!special && links.Any(link => link.Room == room && link.Other is { } next && vented.ContainsKey(next))) continue;
                yield return new EscapeProblem(room, special
                    ? "it is a place of special fire hazard in a basement with no smoke outlet of its own - Approved Document B (16.7) asks it separate outlets"
                    : "it is in a basement with no smoke outlet in it, nor a door into a room with one - Approved Document B (16.2) asks each basement space one or more, or the spaces at its edge vented and these through their doors");
            }

            var clear = vented.Values.Sum();
            var asked = area * OutletShare;
            if (clear < asked - 0.005)
                yield return new EscapeProblem(rooms.MaxBy(room => Math.Abs(Polygon2D.SignedArea(outlines[room])))!,
                    $"the basement storey's smoke outlets give {clear:0.00} m² clear between them - Approved Document B (16.6) asks 1/40 of its {area:0} m² floor, {asked:0.00} m²{(depth <= ShallowBasement + 0.5 ? string.Empty : $", its floor {Units.Metres(depth, "0.0")} below the ground")}");
        }
    }

    /// <summary>Whether a point in a room is within so far of an edge of it with no room beyond on its storey - an outside wall.</summary>
    private static bool AtOutsideWall(Plan plan, Room room, Point2D at, double within)
    {
        var outline = plan.Outlines[room];
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var near = Line2D.ClosestPointOnSegment(at, a, b);
            if (near.DistanceTo(at) > within + 0.5 || a.DistanceTo(b) < 1) continue;
            var along = (b - a) / a.DistanceTo(b);
            var outward = new Vector2D(along.Y, -along.X);
            if (Polygon2D.Contains(outline, near + outward * 50)) outward = -outward;
            var beyond = near + outward * 600;
            if (!plan.Outlines.Any(entry => entry.Key.LevelId == room.LevelId && Polygon2D.Contains(entry.Value, beyond))) return true;
        }

        return false;
    }
}
