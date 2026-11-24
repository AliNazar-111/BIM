using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Escape over flat roofs (round 136), by Approved Document B Volume 2 2.30 to 2.32 and 3.33, and Volume 1 2.13, 2.14, 3.10, 3.11, 3.34 and
/// 3.72.
///
/// A route over a flat roof: a door in an outside wall of a storey above the ground opening onto a flat roof - one not over 10 degrees, its top
/// within 600 below the floor - that leads across the roof to an external escape stair whose head is on it, or to a door from it into a stair
/// on the same roof: a storey exit or an external escape route (2.32b, 2.13b). Such a door is a way out of its storey in the walk.
///
/// Asked of one: in a building other than a dwelling, not in a residential (institutional) building, nor from a part the public use - a shop's
/// or an assembly building's, its room not an office, staff room, kitchen, store, plant or workshop by its name - and not the only way out of
/// its storey (2.31; 3.34 likewise for flats' common parts); the roof REI 30 (2.32c, 2.13c, 3.10c, 3.34c); every window, door and rooflight
/// within 3 m of the route but its own two doors of 30 minutes (the same); its edges within 2 m of the route guarded - a railing along them, or
/// a wall standing 1100 over the roof (2.32d, 3.34d, Approved Document K by 2.14 and 3.11); and in a building other than a dwelling the outside
/// walls within 1800 of the route fire-resisting, 30 minutes (2.30). A house's only from a storey more than 4.5 m up, where its means of
/// escape is more than a window.
///
/// Not done: a roof route from an escape window; the route's line other than straight across; balconies (2.14, 3.11) other than roofs.
/// </summary>
public static class RoofEscape
{
    /// <summary>The step down onto a roof, its openings' distance from the route, its edges' and walls' distance, and guarding's height, mm; the steepest roof walked, degrees.</summary>
    public const double Step = 600, Openings = 3000, Edge = 2000, Beside = 1800, Guard = 1100, Steepest = 10;

    /// <summary>A route over a flat roof: the door onto it, the room it leads out of, its storey, the roof, where it starts and ends, and what it leads to.</summary>
    public sealed record Route(Door Door, Room? Room, Level Level, Roof Roof, double Top, Point2D From, Point2D To, string Leads, ExternalStairs.Placed? Stair, Door? Into);

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The building's routes over flat roofs, kept while the model does not change.</summary>
    public static IReadOnlyList<Route> Of(BimDocument document) =>
        ModelCache.Of(document) is { } cache ? cache.Keep("roof escape", document, null, null, () => Find(document)) : Find(document);

    /// <summary>The doors onto routes over flat roofs.</summary>
    public static IReadOnlySet<Door> DoorsOnto(BimDocument document) => Of(document).Select(route => route.Door).ToHashSet();

    private static double DistanceTo(IReadOnlyList<Point2D> outline, Point2D point)
    {
        if (outline.Count >= 3 && Polygon2D.Contains(outline, point)) return 0;
        var least = double.PositiveInfinity;
        for (var i = 0; i < outline.Count; i++) least = Math.Min(least, Line2D.DistanceFromSegment(point, outline[i], outline[(i + 1) % outline.Count]));
        return least;
    }

    private static double Between(Point2D a0, Point2D a1, Point2D b0, Point2D b1) =>
        new[] { Line2D.DistanceFromSegment(a0, b0, b1), Line2D.DistanceFromSegment(a1, b0, b1), Line2D.DistanceFromSegment(b0, a0, a1), Line2D.DistanceFromSegment(b1, a0, a1) }.Min();

    private static IReadOnlyList<Route> Find(BimDocument document)
    {
        var routes = new List<Route>();
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return routes;
        var roofs = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3 && (roof.Form == RoofForm.Flat || roof.SlopeDegrees <= Steepest)).ToList();
        if (roofs.Count == 0) return routes;
        var stairs = ExternalStairs.Of(document);
        var onto = stairs.SelectMany(stair => stair.Doors).Select(each => each.Door).ToHashSet();
        var stairRooms = Doors.StairRooms(document);

        var outs = new List<(Door Door, Level Level, Point2D At, Roof Roof, double Top, Room? Room)>();
        foreach (var door in document.All<Door>())
        {
            if (onto.Contains(door) || document.Find<Wall>(door.HostWallId) is not { } wall || document.FindLevel(wall.LevelId) is not { } level
                || document.FindType<DoorType>(door.TypeId) is not { } type || !Doors.External(document, door, type) || ExternalStairs.AtGrade(document, door)
                || ExternalStairs.Out(document, door) is not { } at) continue;
            var under = roofs.Where(roof => roof.Contains(at)).Select(roof => (Roof: roof, Top: roof.TopAt(document, at)))
                .Where(entry => entry.Top <= level.Elevation + 50 && entry.Top >= level.Elevation - Step).OrderByDescending(entry => entry.Top).FirstOrDefault();
            if (under.Roof is null) continue;
            var (one, other) = Doors.RoomsBeside(document, door);
            outs.Add((door, level, at, under.Roof, under.Top, one ?? other));
        }

        foreach (var (door, level, at, roof, top, room) in outs)
        {
            var candidates = new List<(Point2D To, string Leads, ExternalStairs.Placed? Stair, Door? Into)>();
            foreach (var stair in stairs)
                if (Math.Abs(stair.Top - top) <= Step && DistanceTo(roof.Boundary, stair.Head) <= 1500)
                    candidates.Add((stair.Head, "the external stair", stair, null));
            foreach (var other in outs.Where(other => other.Door != door && other.Roof == roof && other.Room is { } inside && stairRooms.Contains(inside)))
                candidates.Add((other.At, $"the door into the {other.Room!.Name}", null, other.Door));
            if (candidates.Count == 0) continue;
            var best = candidates.MinBy(candidate => candidate.To.DistanceTo(at));
            routes.Add(new Route(door, room, level, roof, top, at, best.To, best.Leads, best.Stair, best.Into));
        }

        return routes;
    }

    /// <summary>What a door onto a route over a flat roof falls short of, said.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Door door, DoorType type)
    {
        if (Of(document).FirstOrDefault(route => route.Door == door) is not { } route) yield break;
        var volume2 = HorizontalEscape.Applies(document);
        var flats = !volume2 && FireDesign.UseOf(document) == BuildingUse.Flats;
        var ground = Escape.Ground(document);
        if (!volume2 && !flats && route.Level.Elevation - ground <= 4500 + 0.5) yield break;
        var common = flats && route.Room is { } from && Dwellings.Of(from).Length == 0;
        string Cite(string two, string flat, string house) => volume2 ? $"Approved Document B Volume 2 ({two})" : flats ? $"Approved Document B ({flat})" : $"Approved Document B ({house})";
        var said = $"it opens onto a route over the flat roof to {route.Leads}";

        // Who may use it, and not the only way out (2.31, 3.34).
        if (volume2)
        {
            var group = HorizontalEscape.Purpose(document);
            if (group == PurposeGroup.ResidentialInstitutional)
                yield return ($"{said}, in a residential (institutional) building.", "Approved Document B Volume 2 (2.31) allows an escape route over a flat roof only where the building is not a residential (institutional) one, such as a care home or hospital.");
            else if (group is PurposeGroup.ShopAndCommercial or PurposeGroup.AssemblyAndRecreation && route.Room is { } room
                     && !Named(room, "office", "staff", "kitchen", "store", "plant", "workshop", "dressing", "back"))
                yield return ($"{said}, from the {room.Name}, a part the public use, as read by its name.", "Approved Document B Volume 2 (2.31) allows an escape route over a flat roof only from a part of a building not intended for use by members of the public.");
            if (HorizontalEscape.Storeys(document).TryGetValue(route.Level.Id, out var storey) && !storey.Exits.Any(exit => exit.Door is { } other && other != door && !DoorsOnto(document).Contains(other)))
                yield return ($"{said}, the only way out of the {route.Level.Name}.", "Approved Document B Volume 2 (2.31) allows one escape route over a flat roof where a storey has more than one, not as its only one.");
        }

        // The roof REI 30 (2.32c, 2.13c, 3.10c, 3.34c).
        var cite = Cite("2.32c", common ? "3.34c" : "3.10c", "2.13c");
        if (document.FindType<SlabType>(route.Roof.TypeId) is { } roofType && Doors.FireMinutes(roofType.FireRating) < 30)
            yield return ($"{said}, the {roofType.Name} {(Doors.FireMinutes(roofType.FireRating) is > 0 and var rated ? $"rated {rated} minutes" : "not fire-rated")}.",
                $"{cite} asks the part of a flat roof forming an escape route, with its supporting structure, fire-resisting: REI 30.");

        // Openings within 3 m of the route of 30 minutes, but its own doors.
        var openings = new List<string>();
        foreach (var wall in document.Walls)
        {
            if (document.GetWallType(wall) is not { Function: WallFunction.Exterior } wallType || document.FindLevel(wall.LevelId) is not { } level) continue;
            if (level.Elevation + wall.UnconnectedHeight <= route.Top + 1 || level.Elevation > route.Top + Openings) continue;
            foreach (var window in document.All<Window>().Where(each => each.HostWallId == wall.Id))
                if (document.FindType<WindowType>(window.TypeId) is { } windowType && Doors.FireMinutes(windowType.FireRating) < 30
                    && Between(wall.PointAt(wallType.Structure, window.DistanceAlongWall - windowType.Width / 2, 0), wall.PointAt(wallType.Structure, window.DistanceAlongWall + windowType.Width / 2, 0), route.From, route.To) <= Openings + 0.5)
                    openings.Add($"the {windowType.Name} window on the {level.Name}");
            foreach (var other in document.All<Door>().Where(each => each.HostWallId == wall.Id && each != door && each != route.Into))
                if (document.FindType<DoorType>(other.TypeId) is { } doorType && Doors.FireMinutes(doorType.FireRating) < 30
                    && Between(wall.PointAt(wallType.Structure, other.DistanceAlongWall - doorType.Width / 2, 0), wall.PointAt(wallType.Structure, other.DistanceAlongWall + doorType.Width / 2, 0), route.From, route.To) <= Openings + 0.5)
                    openings.Add($"the {doorType.Name} door on the {level.Name}");
        }

        foreach (var light in document.All<RoofWindow>())
            if (RoofWindows.Frame(document, light) is { } frame && frame.Roof == route.Roof && Line2D.DistanceFromSegment(frame.Centre, route.From, route.To) <= Openings + 0.5)
                openings.Add($"the {frame.Type.Name} rooflight");
        if (openings.Count > 0)
            yield return ($"{said}, and {string.Join(", ", openings.Distinct())} within 3 m of it {(openings.Count == 1 ? "is" : "are")} not fire-resisting.",
                $"{cite} asks any opening within 3 m of an escape route over a flat roof fire-resisting, 30 minutes.");

        // Its edges guarded (2.32d, 3.34d; Approved Document K by 2.14 and 3.11).
        var boundary = route.Roof.Boundary;
        var open = 0;
        for (var i = 0; i < boundary.Count; i++)
        {
            var (a, b) = (boundary[i], boundary[(i + 1) % boundary.Count]);
            if (Between(a, b, route.From, route.To) > Edge + 0.5 || a.DistanceTo(b) < 1) continue;
            // The stretch of the edge within 2 m of the route, but the metre either side of it where the route goes on off the roof, guarded all along:
            // each point of it within 600 of a railing or a wall standing 1100 over the roof.
            var guards = document.All<Railing>().SelectMany(railing => Enumerable.Range(0, Math.Max(0, railing.Path.Count - 1)).Select(k => (railing.Path[k], railing.Path[k + 1])))
                .Concat(document.Walls.Where(wall => wall.GetTopElevation(document) >= route.Top + Guard - 0.5 && wall.GetBaseElevation(document) <= route.Top + 600).Select(wall => (wall.Start, wall.End))).ToList();
            var steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / 250));
            var bare = Enumerable.Range(0, steps + 1).Select(k => a + (b - a) * (k / (double)steps))
                .Where(point => Line2D.DistanceFromSegment(point, route.From, route.To) is var off && off <= Edge + 0.5 && off > 1000)
                .Any(point => !guards.Any(guard => Line2D.DistanceFromSegment(point, guard.Item1, guard.Item2) <= 600));
            if (bare) open++;
        }

        if (open > 0)
            yield return ($"{said}, {open} of the roof's edges within 2 m of it unguarded.", volume2 || common
                ? $"{Cite("2.32d", "3.34d", "2.14")} asks an escape route over a flat roof clearly defined and guarded by walls or protective barriers against falling - a railing along each edge near it, or a wall standing 1100 over the roof."
                : $"{Cite("2.32d", "3.11", "2.14")} asks a flat roof forming part of an escape route guarded as Approved Document K asks - a railing along each edge near it, or a wall standing 1100 over the roof.");

        // The outside walls beside it fire-resisting within 1800 (2.30).
        if (volume2)
        {
            var walls = document.Walls.Where(wall => document.GetWallType(wall) is { Function: WallFunction.Exterior } wallType && Doors.FireMinutes(wallType.FireRating) < 30
                                                     && document.FindLevel(wall.LevelId) is { } level && level.Elevation <= route.Top + 1100 && level.Elevation + wall.UnconnectedHeight > route.Top + 1
                                                     && Between(wall.Start, wall.End, route.From, route.To) - wallType.Structure.TotalWidth / 2 <= Beside + 0.5)
                .Select(wall => document.GetWallType(wall)!.Name).Distinct().ToList();
            if (walls.Count > 0)
                yield return ($"{said}, the outside wall{(walls.Count == 1 ? string.Empty : "s")} beside it - {string.Join(", ", walls)} - not fire-resisting.",
                    "Approved Document B Volume 2 (2.30, Table B1 item 5c) asks the outside wall within 1800 of an external escape route, up to 1100 above it, fire-resisting: RE 30.");
        }
    }
}
