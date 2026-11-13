using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>Something the fire and rescue service's access finds wanting: what, and why.</summary>
public sealed record AccessProblem(Element Element, string Said);

/// <summary>
/// Vehicle access for the fire and rescue service, as Approved Document B Volume 1 (Section 13, B5) has it: a pumping appliance able to get
/// within 45 m of every point inside a house (13.1), or inside each flat of a block (13.2a), measured along the route of the hose; a door
/// 750 mm wide at least into the building where it gets to; and its access route 3.7 m wide between kerbs (Table 13.1).
///
/// The appliance stands on a drive - a region of the ground laid as a driveway - taken as reached from the road; the hose runs from the
/// nearest of it straight to an outside door 750 mm wide or more, then through the rooms as they are walked, through their doors and round
/// their corners, and up and down the stairs along their walking lines. Checked only once a drive is drawn; not under the US's codes.
/// </summary>
public static class FireAccess
{
    /// <summary>How far the hose may run from the appliance to any point inside, mm (13.1, 13.2a).</summary>
    public const double Hose = 45000;

    /// <summary>The least width of a door the hose goes in by, mm (13.1).</summary>
    public const double DoorWidth = 750;

    /// <summary>The least width of a pumping appliance's access route between kerbs, mm (Table 13.1).</summary>
    public const double RoadWidth = 3700;

    /// <summary>What the fire and rescue service's access finds wanting.</summary>
    public static IReadOnlyList<AccessProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return Array.Empty<AccessProblem>();
        var drives = document.All<GroundRegion>().Where(region => region.Surface == GroundSurface.Driveway && region.Boundary.Count >= 3).ToList();
        if (drives.Count == 0) return Array.Empty<AccessProblem>();

        var problems = new List<AccessProblem>();
        foreach (var drive in drives)
            if (Narrowest(drive.Boundary) is var width && width < RoadWidth - 1)
                problems.Add(new AccessProblem(drive, $"it is {Units.Metres(width, "0.0")} wide - Approved Document B (Table 13.1) gives a pumping appliance's access route 3.7 m between kerbs"));

        using var outlines = new RoomsOf.Outlines();
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => entry.Boundary.Polygon);
        if (rooms.Count == 0) return problems;

        // The doors in: outside doors 750 mm wide at least, the hose run to each from the nearest of a drive.
        var links = Escape.Links(document, rooms.Keys.ToHashSet());
        var ways = links.Where(link => link.Outside && document.FindType<DoorType>(link.Door.TypeId) is { } type && type.Width >= DoorWidth - 1).ToList();
        if (ways.Count == 0)
        {
            problems.Add(new AccessProblem(drives[0], "no door into the building 750 mm wide at least - Approved Document B (13.1) asks one where a pumping appliance gets to"));
            return problems;
        }

        var sources = ways.Select(link => (link.Room, link.Inside, Nearest(drives, link.Beyond) + link.Inside.DistanceTo(link.Beyond))).ToList();
        var walk = new Escape.Walk(rooms, links, Stairways(document, rooms));
        var from = walk.From(sources);

        // Every point inside the house - or each flat, where the rooms are named into flats - within 45 m along the hose.
        var named = rooms.Keys.Any(room => Dwellings.Of(room).Length > 0);
        foreach (var (room, outline) in rooms.Where(entry => !named || Dwellings.Of(entry.Key).Length > 0))
        {
            var furthest = Escape.Points(outline).Select(point => from(room, point)).OfType<double>().DefaultIfEmpty(double.NaN).Max();
            if (double.IsNaN(furthest) || furthest <= Hose + 0.5) continue;
            problems.Add(new AccessProblem(room, named
                ? $"its furthest point is {Units.Metres(furthest, "0.0")} along the hose's route from the drive - Approved Document B (13.2a) asks access for a pumping appliance within 45 m of all points inside each flat, or fire mains"
                : $"its furthest point is {Units.Metres(furthest, "0.0")} along the hose's route from the drive - Approved Document B (13.1) asks access for a pumping appliance within 45 m of all points inside the house"));
        }

        return problems;
    }

    /// <summary>The stairs between rooms: the room at each one's foot and the point it starts at, the room at its head and the point it ends at, and how long it is walked, rise and all.</summary>
    private static List<(Room Foot, Point2D FootAt, Room Head, Point2D HeadAt, double Length)> Stairways(BimDocument document, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> rooms)
    {
        var stairways = new List<(Room, Point2D, Room, Point2D, double)>();
        foreach (var stair in document.All<Stair>().Where(stair => stair.TopLevelId is not null))
        {
            StairLayout layout;
            try { layout = Stairs.Layout(document, stair); }
            catch (Exception) { continue; }
            var line = layout.WalkingLine;
            if (line.Count < 2) continue;
            Room? In(Guid level, Point2D at) => rooms.FirstOrDefault(entry => entry.Key.LevelId == level && Polygon2D.Contains(entry.Value, at)).Key;
            if (In(stair.LevelId, line[0]) is not { } foot || In(stair.TopLevelId!.Value, line[^1]) is not { } head) continue;
            var plan = line.Zip(line.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
            var rise = (document.FindLevel(stair.TopLevelId.Value)?.Elevation ?? 0) - (document.FindLevel(stair.LevelId)?.Elevation ?? 0);
            stairways.Add((foot, line[0], head, line[^1], Math.Sqrt(plan * plan + rise * rise)));
        }

        return stairways;
    }

    /// <summary>How far a point is from the nearest drive: none inside one, else to the nearest of its edges.</summary>
    private static double Nearest(IReadOnlyList<GroundRegion> drives, Point2D at) =>
        drives.Min(drive => Polygon2D.Contains(drive.Boundary, at)
            ? 0
            : drive.Boundary.Select((point, i) => Line2D.DistanceFromSegment(at, point, drive.Boundary[(i + 1) % drive.Boundary.Count])).Min());

    /// <summary>How narrow an outline is: the least of its extents square to each of its sides - a strip's width.</summary>
    public static double Narrowest(IReadOnlyList<Point2D> outline)
    {
        var least = double.PositiveInfinity;
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var along = b - a;
            if (along.Length < 1) continue;
            var across = new Vector2D(-along.Y, along.X) * (1 / along.Length);
            var spans = outline.Select(point => (point - a).Dot(across)).ToList();
            least = Math.Min(least, spans.Max() - spans.Min());
        }

        return double.IsPositiveInfinity(least) ? 0 : least;
    }
}
