using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>Something the fire and rescue service's access finds wanting: what, and why.</summary>
public sealed record AccessProblem(Element Element, string Said);

/// <summary>
/// Vehicle access for the fire and rescue service, as Approved Document B Volume 1 (Section 13, B5) has it: a pumping appliance able to get
/// within 45 m of every point inside a house (13.1), or inside each flat of a block (13.2a), measured along the route of the hose; a door
/// 750 mm wide at least into the building in every elevation it gets to (13.1, 13.2a) - in a block of flats no more than 60 m apart; and its
/// access route as Table 13.1 gives it - 3.7 m between kerbs, gateways 3.1 m, 3.7 m clear overhead - with somewhere to turn on a dead end
/// longer than 20 m (13.4, Diagram 13.1).
///
/// The appliance stands on a drive - a region of the ground laid as a driveway - come into from the road where it is nearest the site's
/// property line, or with none drawn at its end furthest from the building; the hose runs from the nearest of it straight to an outside door
/// 750 mm wide or more, then through the rooms as they are walked, through their doors and round their corners, and up and down the stairs
/// along their walking lines.
///
/// A block of flats may have a dry fire main instead (13.2b): its inlet on the face of the building, the appliance within 18 m of it and in
/// sight of it (13.5); its outlets in the stair's enclosure (14.4), the hose taken from them 45 m at most to the furthest point of each flat
/// (14.3). Checked only once a drive is drawn; not under the US's codes.
/// </summary>
public static partial class FireAccess
{
    /// <summary>How far the hose may run from the appliance, or from a fire main's outlet, to any point inside, mm (13.1, 13.2a, 14.3).</summary>
    public const double Hose = 45000;

    /// <summary>The least width of a door the hose goes in by, mm (13.1).</summary>
    public const double DoorWidth = 750;

    /// <summary>The least width of a pumping appliance's access route between kerbs, mm (Table 13.1).</summary>
    public const double RoadWidth = 3700;

    /// <summary>Table 13.1 for a pump: gateways 3.1 m wide, a turning circle 16.8 m between kerbs, 3.7 m clear overhead, mm.</summary>
    public const double Gateway = 3100, TurningCircle = 16800, Clearance = 3700;

    /// <summary>The furthest a pumping appliance should reverse from the end of a dead end, mm (13.4, Diagram 13.1).</summary>
    public const double Reverse = 20000;

    /// <summary>The most between doors along an elevation of a block of flats, or from one to its end, mm (13.2a).</summary>
    public const double DoorSpacing = 60000;

    /// <summary>How near a pumping appliance gets to a dry fire main's inlet, mm (13.5a).</summary>
    public const double InletReach = 18000;

    /// <summary>What the fire and rescue service's access finds wanting.</summary>
    public static IReadOnlyList<AccessProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return Array.Empty<AccessProblem>();
        var drives = document.All<GroundRegion>().Where(region => region.Surface == GroundSurface.Driveway && region.Boundary.Count >= 3).ToList();
        if (drives.Count == 0) return Array.Empty<AccessProblem>();

        var problems = new List<AccessProblem>();
        var ground = Escape.Ground(document);
        var groundLevels = document.Levels.Where(level => Math.Abs(level.Elevation - ground) < 0.5).Select(level => level.Id).ToHashSet();
        // A building other than a dwelling: Volume 2's Section 15 - a high reach appliance's route where its top storey is over 11 m (round 115).
        var v2 = Volume2(document);
        var high = v2 && Table151(document).HighReach;
        foreach (var drive in drives)
        {
            if (Narrowest(drive.Boundary) is var width && width < RoadWidth - 1)
                problems.Add(new AccessProblem(drive, v2
                    ? $"it is {Units.Metres(width, "0.0")} wide - Approved Document B Volume 2 (Table 15.2) gives a fire appliance's access route 3.7 m between kerbs"
                    : $"it is {Units.Metres(width, "0.0")} wide - Approved Document B (Table 13.1) gives a pumping appliance's access route 3.7 m between kerbs"));
            Turning(document, drive, groundLevels, problems, v2, high);
            Gateways(document, drive, groundLevels, problems, v2);
            Overhead(document, drive, ground, problems, v2, high);
        }

        using var outlines = new RoomsOf.Outlines();
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => entry.Boundary.Polygon);
        var named = rooms.Keys.Any(room => Dwellings.Of(room).Length > 0);
        var flights = Escape.Flights(document, rooms);

        // A dry fire main, where there is one: in a block of flats, the hose taken from its outlets instead (13.2b).
        var inlets = Of(document, ComponentForm.FireMainInlet);
        var outlets = Of(document, ComponentForm.FireMainOutlet);
        if (inlets.Count + outlets.Count > 0) FireMains(document, drives, rooms, flights, inlets, outlets, problems, v2);

        // A building other than a dwelling: a door in every elevation reached, Table 15.1's share of its perimeter or fire mains, hydrants (round 115).
        if (v2)
        {
            Elevations(document, drives, rooms, groundLevels, true, problems, true);
            if (inlets.Count + outlets.Count == 0) VehicleAccess(document, drives, groundLevels, problems);
            else Mains(document, rooms, Escape.Links(document, rooms.Keys.ToHashSet()), flights, inlets, outlets, problems);
            Hydrants(document, drives, groundLevels, inlets, problems);
            return problems;
        }

        var mains = named && outlets.Count > 0;

        // Each elevation the drive runs along with a door in; in a block of flats without a fire main, 60 m apart at most.
        if (!mains) Elevations(document, drives, rooms, groundLevels, named, problems);
        if (rooms.Count == 0) return problems;

        // The doors in: outside doors 750 mm wide at least, the hose run to each from the nearest of a drive.
        var links = Escape.Links(document, rooms.Keys.ToHashSet());
        var ways = links.Where(link => link.Outside && document.FindType<DoorType>(link.Door.TypeId) is { } type && type.Width >= DoorWidth - 1).ToList();
        if (ways.Count == 0 && !mains)
        {
            problems.Add(new AccessProblem(drives[0], "no door into the building 750 mm wide at least - Approved Document B (13.1) asks one where a pumping appliance gets to"));
            return problems;
        }

        var sources = ways.Select(link => (link.Room, link.Inside, Nearest(drives, link.Beyond) + link.Inside.DistanceTo(link.Beyond))).ToList();
        if (mains)
            foreach (var outlet in outlets)
                if (rooms.FirstOrDefault(entry => entry.Key.LevelId == outlet.LevelId && Polygon2D.Contains(entry.Value, outlet.Location)).Key is { } room)
                    sources.Add((room, outlet.Location, 0));
        var walk = new Escape.Walk(rooms, links, flights.Where(flight => flight.Foot is not null && flight.Head is not null)
            .Select(flight => (flight.Foot!, flight.FootAt, flight.Head!, flight.HeadAt, flight.Length)));
        var from = walk.From(sources);

        // Every point inside the house - or each flat, where the rooms are named into flats - within 45 m along the hose.
        foreach (var (room, outline) in rooms.Where(entry => !named || Dwellings.Of(entry.Key).Length > 0))
        {
            var furthest = Escape.Points(outline).Select(point => from(room, point)).OfType<double>().DefaultIfEmpty(double.NaN).Max();
            if (double.IsNaN(furthest) || furthest <= Hose + 0.5) continue;
            problems.Add(new AccessProblem(room, mains
                ? $"its furthest point is {Units.Metres(furthest, "0.0")} along the hose's route from the drive or the nearest fire main outlet - Approved Document B (13.2, 14.3) asks 45 m at most to all points inside each flat"
                : named
                    ? $"its furthest point is {Units.Metres(furthest, "0.0")} along the hose's route from the drive - Approved Document B (13.2a) asks access for a pumping appliance within 45 m of all points inside each flat, or fire mains"
                    : $"its furthest point is {Units.Metres(furthest, "0.0")} along the hose's route from the drive - Approved Document B (13.1) asks access for a pumping appliance within 45 m of all points inside the house"));
        }

        return problems;
    }

    private static List<Component> Of(BimDocument document, ComponentForm form) =>
        document.All<Component>().Where(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == form).ToList();

    /// <summary>
    /// A dead end longer than 20 m with nowhere to turn (13.4, Diagram 13.1): the drive walked from where it is come into to its far end;
    /// past 20 m, a place a turning circle 16.8 m between kerbs fits in it (Table 13.1) within 20 m of the far end - the appliance reversing
    /// no further to it.
    /// </summary>
    private static void Turning(BimDocument document, GroundRegion drive, HashSet<Guid> groundLevels, List<AccessProblem> problems, bool v2 = false, bool high = false)
    {
        var circle = high ? HighTurningCircle : TurningCircle;
        var appliance = high ? "a high reach appliance" : "a pumping appliance";
        var outline = drive.Boundary;
        var points = Escape.Points(outline).ToList();
        if (points.Count == 0) return;
        var fromEntry = Across(outline, Entry(document, points, groundLevels));
        var reached = points.Select(point => (Point: point, Length: fromEntry(point))).Where(entry => entry.Length is not null).ToList();
        if (reached.Count == 0) return;
        var (far, length) = reached.MaxBy(entry => entry.Length!.Value);
        if (length <= Reverse + 0.5) return;

        var places = TurningPlaces(outline, circle);
        if (places.Count == 0)
        {
            problems.Add(new AccessProblem(drive, v2
                ? $"it runs {Units.Metres(length!.Value, "0.0")} from where it is come into, a dead end with nowhere {appliance} can turn - Approved Document B Volume 2 (15.10, Diagram 15.3) asks a turning circle {circle / 1000:0.0} m between kerbs (Table 15.2), a hammerhead or another place to turn on a dead end longer than 20 m"
                : $"it runs {Units.Metres(length!.Value, "0.0")} from where it is come into, a dead end with nowhere a pumping appliance can turn - Approved Document B (13.4, Diagram 13.1) asks a turning circle 16.8 m between kerbs (Table 13.1), a hammerhead or another place to turn on a dead end longer than 20 m"));
            return;
        }

        var fromFar = Across(outline, far);
        var reverse = places.Select(place => fromFar(place)).OfType<double>().Select(distance => Math.Max(0, distance - circle / 2)).DefaultIfEmpty(double.PositiveInfinity).Min();
        if (reverse > Reverse + 0.5)
            problems.Add(new AccessProblem(drive, v2
                ? $"its far end is {Units.Metres(reverse, "0.0")} from where {appliance} can turn - Approved Document B Volume 2 (15.10, Diagram 15.3) asks it reverse no more than 20 m"
                : $"its far end is {Units.Metres(reverse, "0.0")} from where a pumping appliance can turn - Approved Document B (13.4, Diagram 13.1) asks it reverse no more than 20 m"));
    }

    /// <summary>Where a drive is come into from the road: its point nearest the site's property line, where one is drawn; else its point furthest from the walls of the ground floor.</summary>
    private static Point2D Entry(BimDocument document, List<Point2D> points, HashSet<Guid> groundLevels)
    {
        var lines = document.All<PropertyLine>().Where(line => line.Boundary.Count >= 2).ToList();
        if (lines.Count > 0)
            return points.MinBy(point => lines.Min(line => Enumerable.Range(0, line.Boundary.Count)
                .Min(i => Line2D.DistanceFromSegment(point, line.Boundary[i], line.Boundary[(i + 1) % line.Boundary.Count]))));
        var walls = document.Walls.Where(wall => groundLevels.Contains(wall.LevelId)).ToList();
        return walls.Count == 0 ? points[0] : points.MaxBy(point => walls.Min(wall => Line2D.DistanceFromSegment(point, wall.Start, wall.End)));
    }

    /// <summary>How far each point of an outline is from a point in it, walked within it - round its inside corners.</summary>
    private static Func<Point2D, double?> Across(IReadOnlyList<Point2D> outline, Point2D from)
    {
        var area = new Room();
        var to = new Escape.Walk(new Dictionary<Room, IReadOnlyList<Point2D>> { [area] = outline }, Array.Empty<Escape.Link>()).From(area, from);
        return point => to(area, point);
    }

    /// <summary>
    /// The middles of the turning circles 16.8 m between kerbs that fit in an outline: points 8.4 m at least from all its sides - found on a
    /// grid every 500 mm, and looked for again every 25 mm round each grid point that could be within 354 mm of one.
    /// </summary>
    public static List<Point2D> TurningPlaces(IReadOnlyList<Point2D> outline, double circle = TurningCircle)
    {
        double Clear(Point2D at) => outline.Select((point, i) => Line2D.DistanceFromSegment(at, point, outline[(i + 1) % outline.Count])).Min();
        var half = circle / 2;
        var places = new List<Point2D>();
        foreach (var point in Energy.Daylight.Grid(outline, 500))
        {
            var clear = Clear(point);
            if (clear >= half - 0.5)
            {
                places.Add(point);
                continue;
            }

            if (clear < half - 360) continue;
            Point2D? found = null;
            for (var dx = -250.0; dx <= 250 && found is null; dx += 25)
                for (var dy = -250.0; dy <= 250 && found is null; dy += 25)
                    if (new Point2D(point.X + dx, point.Y + dy) is var near && Polygon2D.Contains(outline, near) && Clear(near) >= half - 20)
                        found = near;
            if (found is { } place) places.Add(place);
        }

        return places;
    }

    /// <summary>
    /// Walls and fences on the ground the drive passes through - running across it, the drive going on more than half a metre either side of
    /// them - and the widest way left through them across it, between their ends and through the gates in them: 3.1 m at least (Table 13.1).
    /// </summary>
    private static void Gateways(BimDocument document, GroundRegion drive, HashSet<Guid> groundLevels, List<AccessProblem> problems, bool v2 = false)
    {
        var outline = drive.Boundary;
        var grid = Escape.Points(outline).ToList();

        // Each straight piece of wall or fence, and the gates in it - doors - as spans along it from its start.
        var pieces = new List<(Element Element, Point2D A, Point2D B, List<(double From, double To)> Open)>();
        foreach (var wall in document.Walls.Where(wall => groundLevels.Contains(wall.LevelId) && wall.Length > 1))
        {
            var open = new List<(double, double)>();
            foreach (var door in document.All<Door>().Where(door => door.HostWallId == wall.Id))
                if (document.FindType<DoorType>(door.TypeId) is { } type)
                    open.Add(door.GetSpan(type));
            pieces.Add((wall, wall.Start, wall.End, open));
        }

        foreach (var railing in document.All<Railing>().Where(railing => groundLevels.Contains(railing.LevelId)))
            for (var i = 0; i + 1 < railing.Path.Count; i++)
                if (railing.Path[i].DistanceTo(railing.Path[i + 1]) > 1)
                    pieces.Add((railing, railing.Path[i], railing.Path[i + 1], new List<(double, double)>()));

        var done = new HashSet<int>();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (!done.Add(i)) continue;
            var (element, a, b, _) = pieces[i];
            var along = (b - a).NormalisedOrDefault(Vector2D.UnitX);
            var across = new Vector2D(-along.Y, along.X);
            bool OnLine(Point2D point) => Math.Abs((point - a).Dot(across)) < 100;
            var line = Enumerable.Range(0, pieces.Count).Where(k => OnLine(pieces[k].A) && OnLine(pieces[k].B)).ToList();
            foreach (var k in line) done.Add(k);

            // Where the drive's sides cross the line: the spans of it across the drive.
            var hits = new List<double>();
            for (var e = 0; e < outline.Count; e++)
            {
                var (p, q) = (outline[e], outline[(e + 1) % outline.Count]);
                var (sp, sq) = ((p - a).Dot(across), (q - a).Dot(across));
                if (sp * sq < 0) hits.Add((p + (q - p) * (sp / (sp - sq)) - a).Dot(along));
            }

            hits.Sort();
            for (var h = 0; h + 1 < hits.Count; h += 2)
            {
                var (lo, hi) = (hits[h], hits[h + 1]);
                bool Beyond(double side) => grid.Any(point => (point - a).Dot(across) * side > 500 && (point - a).Dot(along) is var t && t > lo && t < hi);
                if (!Beyond(1) || !Beyond(-1)) continue;

                // What stands across it: each piece on the line, less its gates.
                var blocked = new List<(double From, double To)>();
                foreach (var k in line)
                {
                    var (_, pa, pb, open) = pieces[k];
                    var (ta, tb) = ((pa - a).Dot(along), (pb - a).Dot(along));
                    var sign = tb >= ta ? 1 : -1;
                    var at = Math.Min(ta, tb);
                    foreach (var (c0, c1) in open.Select(span => (ta + sign * span.From, ta + sign * span.To)).Select(span => (Math.Min(span.Item1, span.Item2), Math.Max(span.Item1, span.Item2))).OrderBy(span => span.Item1))
                    {
                        if (c0 > at) blocked.Add((at, c0));
                        at = Math.Max(at, c1);
                    }

                    if (Math.Max(ta, tb) > at) blocked.Add((at, Math.Max(ta, tb)));
                }

                var across_ = blocked.Select(span => (From: Math.Max(span.From, lo), To: Math.Min(span.To, hi))).Where(span => span.To > span.From).OrderBy(span => span.From).ToList();
                if (across_.Count == 0) continue;
                var (widest, from) = (0.0, lo);
                foreach (var (c0, c1) in across_)
                {
                    widest = Math.Max(widest, c0 - from);
                    from = Math.Max(from, c1);
                }

                widest = Math.Max(widest, hi - from);
                if (widest >= Gateway - 0.5) continue;
                var rule = v2 ? "Approved Document B Volume 2 (Table 15.2) asks gateways 3.1 m wide on a fire appliance's route" : "Approved Document B (Table 13.1) asks gateways 3.1 m wide on a pumping appliance's route";
                problems.Add(new AccessProblem(element, widest < 1
                    ? $"it runs across the drive with no way through - {rule}"
                    : $"the drive passes through a gateway {Units.Metres(widest, "0.0")} wide in it - {rule}"));
            }
        }
    }

    /// <summary>A floor or roof over the drive less than 3.7 m above the ground under it - Table 13.1's clearance height - the lowest.</summary>
    private static void Overhead(BimDocument document, GroundRegion drive, double ground, List<AccessProblem> problems, bool v2 = false, bool high = false)
    {
        var clearance = high ? HighClearance : Clearance;
        var grid = Escape.Points(drive.Boundary).ToList();
        (Slab Slab, double Clear)? lowest = null;
        foreach (var slab in document.All<Slab>().Where(slab => (slab is Floor || slab is Roof { IsExtrusion: false }) && slab.Boundary.Count >= 3))
            foreach (var point in grid.Where(slab.Contains))
            {
                var under = slab is Roof roof ? roof.UndersideAt(document, point) : slab.GetBottomElevation(document);
                var clear = under - (Terrains.GroundAt(document, point) ?? ground);
                if (clear > 500 && clear < clearance - 0.5 && (lowest is null || clear < lowest.Value.Clear)) lowest = (slab, clear);
            }

        if (lowest is { } low)
            problems.Add(new AccessProblem(drive, $"it passes under {(low.Slab is Roof ? "a roof" : "a floor")} {Units.Metres(low.Clear, "0.0")} above it - {(v2 ? $"Approved Document B Volume 2 (Table 15.2) asks {clearance / 1000:0.0} m clear over {(high ? "a high reach" : "a pump")} appliance's route" : "Approved Document B (Table 13.1) asks 3.7 m clear over a pumping appliance's route")}"));
    }

    /// <summary>
    /// Each elevation the drive runs along - the outside walls of the ground floor in a line, a room behind them, the drive in front of them and alongside - a
    /// door 750 mm wide at least into the building in it (13.1, 13.2a); in a block of flats, no more than 60 m between its doors or from one to
    /// its end (13.2a).
    /// </summary>
    private static void Elevations(BimDocument document, List<GroundRegion> drives, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> rooms, HashSet<Guid> groundLevels,
        bool flats, List<AccessProblem> problems, bool v2 = false)
    {
        // The building's outside walls - a room behind them - not a wall round the garden.
        bool Building(Wall wall) => document.GetWallType(wall) is { Function: WallFunction.Exterior } && Combustibility.BoundsARoom(document, wall);
        var walls = document.Walls.Where(wall => groundLevels.Contains(wall.LevelId) && wall.Length > 1 && Building(wall)).ToList();
        var points = drives.SelectMany(drive => Escape.Points(drive.Boundary)).ToList();
        var done = new HashSet<Wall>();
        foreach (var wall in walls)
        {
            if (done.Contains(wall)) continue;
            var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
            var across = new Vector2D(-along.Y, along.X);
            bool OnLine(Point2D point) => Math.Abs((point - wall.Start).Dot(across)) < 100;
            var line = walls.Where(other => OnLine(other.Start) && OnLine(other.End)).ToList();
            foreach (var other in line) done.Add(other);

            var outward = SpaceSeparation.Outward(document, wall);
            var spans = line.SelectMany(other => new[] { (other.Start - wall.Start).Dot(along), (other.End - wall.Start).Dot(along) }).ToList();
            var (t0, t1) = (spans.Min(), spans.Max());
            if (!points.Any(point => (point - wall.Start).Dot(outward) > 0 && (point - wall.Start).Dot(along) is var t && t >= t0 && t <= t1)) continue;

            var doors = new List<double>();
            foreach (var other in line)
                foreach (var door in document.All<Door>().Where(door => door.HostWallId == other.Id))
                    if (document.FindType<DoorType>(door.TypeId) is { } type && type.Width >= DoorWidth - 1 && Doors.External(document, door, type))
                    {
                        var (from, to) = door.GetSpan(type);
                        var middle = other.Start + (other.End - other.Start).NormalisedOrDefault(Vector2D.UnitX) * ((from + to) / 2);
                        doors.Add((middle - wall.Start).Dot(along));
                    }

            if (doors.Count == 0)
            {
                problems.Add(new AccessProblem(wall, v2
                    ? "the drive runs along this elevation, and no door 750 mm wide at least goes into the building from it - Approved Document B Volume 2 (15.3) asks one in every elevation vehicle access is provided to"
                    : "the drive runs along this elevation, and no door 750 mm wide at least goes into the building from it - Approved Document B (13.1, 13.2a) asks one in every elevation a pumping appliance gets to"));
                continue;
            }

            if (!flats) continue;
            var stops = doors.Append(t0).Append(t1).OrderBy(t => t).ToList();
            var gap = stops.Zip(stops.Skip(1), (x, y) => y - x).Max();
            if (gap > DoorSpacing + 0.5)
                problems.Add(new AccessProblem(wall, $"along this elevation its doors in are {Units.Metres(gap, "0.0")} apart, or from its end - Approved Document B {(v2 ? "Volume 2 (15.3)" : "(13.2a)")} asks no more than 60 m between doors or to the end of the elevation"));
        }
    }

    /// <summary>
    /// A dry fire main (13.5, 14.3, 14.4): each inlet on the face of the building, a pumping appliance on the drive within 18 m of it and in
    /// sight of it - no wall between; each outlet in a stair's enclosure; and an inlet to feed the outlets.
    /// </summary>
    private static void FireMains(BimDocument document, List<GroundRegion> drives, IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> rooms, List<Escape.Flight> flights,
        List<Component> inlets, List<Component> outlets, List<AccessProblem> problems, bool v2 = false)
    {
        string Cite(string one, string two) => v2 ? $"Approved Document B Volume 2 ({two})" : $"Approved Document B ({one})";
        if (inlets.Count == 0)
            problems.Add(new AccessProblem(outlets[0], $"a fire main's outlet with no inlet to feed it - {Cite("13.5", "15.4")} asks one on the face of the building, a pumping appliance within 18 m of it"));

        foreach (var inlet in inlets)
        {
            var at = inlet.Location;
            var reach = Nearest(drives, at);
            if (reach > InletReach + 0.5)
                problems.Add(new AccessProblem(inlet, $"it is {Units.Metres(reach, "0.0")} from the drive - {Cite("13.5a", "15.4a")} asks access for a pumping appliance within 18 m of each fire main inlet"));

            var inside = rooms.Any(entry => entry.Key.LevelId == inlet.LevelId && Polygon2D.Contains(entry.Value, at));
            var face = document.Walls.Where(wall => wall.LevelId == inlet.LevelId && document.GetWallType(wall) is { Function: WallFunction.Exterior })
                .Any(wall => Line2D.DistanceFromSegment(at, wall.Start, wall.End) <= document.GetWallType(wall)!.Structure.TotalWidth / 2 + 500);
            if (inside || !face)
            {
                problems.Add(new AccessProblem(inlet, $"it is not on the face of the building - {Cite("13.5a", "15.4a")} asks fire main inlets there"));
                continue;
            }

            var parked = drives.Select(drive => Polygon2D.Contains(drive.Boundary, at)
                    ? at
                    : drive.Boundary.Select((point, i) => Line2D.ClosestPointOnSegment(at, point, drive.Boundary[(i + 1) % drive.Boundary.Count])).MinBy(point => point.DistanceTo(at)))
                .MinBy(point => point.DistanceTo(at));
            if (document.Walls.Where(wall => wall.LevelId == inlet.LevelId).Any(wall => Crosses(parked, at, wall.Start, wall.End)))
                problems.Add(new AccessProblem(inlet, $"a wall stands between it and the drive - {Cite("13.5b", "15.4b")} asks it visible from where the pumping appliance parks"));
        }

        foreach (var outlet in outlets)
        {
            var room = rooms.FirstOrDefault(entry => entry.Key.LevelId == outlet.LevelId && Polygon2D.Contains(entry.Value, outlet.Location)).Key;
            if (room is not null && (flights.Any(flight => flight.Foot == room || flight.Head == room) || room.Name.Contains("stair", StringComparison.OrdinalIgnoreCase) || v2 && Circulation.IsLobby(room))) continue;
            var asked = v2 ? "Approved Document B Volume 2 (16.4) asks fire main outlets in the protected stairway or protected lobby" : "Approved Document B (14.4) asks fire main outlets in the protected stairway";
            problems.Add(new AccessProblem(outlet, room is null
                ? $"it is in no room - {asked}"
                : $"it is in the {room.Name}, not a stair's enclosure{(v2 ? " or lobby" : string.Empty)} - {asked}"));
        }
    }

    /// <summary>Whether two segments cross, each through the other.</summary>
    private static bool Crosses(Point2D p, Point2D q, Point2D r, Point2D s)
    {
        static double Cross(Point2D o, Point2D a, Point2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var (d1, d2, d3, d4) = (Cross(r, s, p), Cross(r, s, q), Cross(p, q, r), Cross(p, q, s));
        return (d1 > 1e-6 && d2 < -1e-6 || d1 < -1e-6 && d2 > 1e-6) && (d3 > 1e-6 && d4 < -1e-6 || d3 < -1e-6 && d4 > 1e-6);
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
