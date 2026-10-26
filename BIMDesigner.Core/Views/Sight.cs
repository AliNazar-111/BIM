using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Views;

/// <summary>Who walks the model: a grown-up on foot, a child, or a wheelchair user.</summary>
public enum WalkerKind
{
    Adult,
    Child,
    WheelchairUser
}

/// <summary>
/// Someone walking the model: how high their eyes are, the highest step they take, the steepest
/// slope they can go up or down, and how wide they are.
///
/// On foot: a step up to a stair's riser and more, any slope. In a wheelchair: eyes at 1200, as
/// BS EN 17037 has a seated eye (BS 8300 gives a wheelchair user's as 960 to 1250); no step higher
/// than a level threshold allows - 15 mm in England (Approved Document M, a door's threshold
/// upstand), 1/2 in in the US (ADA 303.2 and 303.3) - nothing steeper than a ramp's 1:12 (Approved
/// Document M; ADA 405.2), and 700 mm wide, so a doorway narrower than that will not let them through.
/// </summary>
public sealed record Walker(WalkerKind Kind, string Name, double EyeHeight, double StepUp, double Steepest, double Width)
{
    /// <summary>The walker of a kind, by the project's standard.</summary>
    public static Walker For(WalkerKind kind, bool us) => kind switch
    {
        WalkerKind.WheelchairUser => new(kind, "a wheelchair user", 1200, us ? 12.7 : 15, 1.0 / 12, 700),
        WalkerKind.Child => new(kind, "a child", 1100, 300, double.PositiveInfinity, 380),
        _ => new(kind, "an adult", Views3D.EyeHeight, Walking.StepUp, double.PositiveInfinity, 0)
    };

    /// <summary>
    /// The walker a person placed in the model is: a wheelchair user, or someone on foot whose
    /// eyes are about 0.93 of their height up - a child under 1500 tall.
    /// </summary>
    public static Walker Of(ComponentType type, bool us)
    {
        if (type.Form == ComponentForm.WheelchairUser) return For(WalkerKind.WheelchairUser, us);
        var child = type.Height < 1500;
        return For(child ? WalkerKind.Child : WalkerKind.Adult, us) with { EyeHeight = Math.Round(type.Height * 0.93) };
    }

    /// <summary>Whether a component is a person - a walker of their own.</summary>
    public static bool IsPerson(ComponentType? type) => type?.Form is ComponentForm.Person or ComponentForm.WheelchairUser;
}

/// <summary>A clear line of sight, or what is in the way.</summary>
public readonly record struct SightLine(bool Clear, Guid? Blocker, Point3D From, Point3D To);

/// <summary>
/// What can be seen from where someone stands. Sight is stopped by walls, columns, closed doors,
/// furniture and anything else solid at the height of the eye - not by glass, which is seen through.
///
/// The area seen - an isovist, as Benedikt named it: every point on the storey a line from the
/// eye reaches before something stops it, out to a distance - is worked out on the plan cut at the
/// height of the eye, by lines sent out all round.
/// </summary>
public static class Sight
{
    /// <summary>What is not in the way of seeing: glass, and what is stood on.</summary>
    private static readonly HashSet<MeshKind> SeenThrough = new() { MeshKind.Glazing, MeshKind.Terrain };

    /// <summary>
    /// The plan of what stops sight at a height: where every solid is cut by the level plane there
    /// - each a stretch of line, with the element it belongs to.
    /// </summary>
    public static IReadOnlyList<(Point2D A, Point2D B, Guid Element)> SliceAt(IReadOnlyList<Mesh3D> meshes, double z)
    {
        var cut = new List<(Point2D, Point2D, Guid)>();
        foreach (var mesh in meshes)
        {
            if (SeenThrough.Contains(mesh.Kind) || mesh.Bounds() is not { } bounds || bounds.Min.Z > z || bounds.Max.Z < z) continue;
            var (positions, indices) = (mesh.Positions, mesh.Indices);
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var corners = new[] { positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]] };
                var crossing = new List<Point2D>(2);
                for (var k = 0; k < 3; k++)
                {
                    var (p, q) = (corners[k], corners[(k + 1) % 3]);
                    if (p.Z > z == q.Z > z || Math.Abs(q.Z - p.Z) < 1e-9) continue;
                    var t = (z - p.Z) / (q.Z - p.Z);
                    crossing.Add(new Point2D(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t));
                }

                if (crossing.Count == 2 && crossing[0].DistanceTo(crossing[1]) > 1e-6) cut.Add((crossing[0], crossing[1], mesh.OwnerId));
            }
        }

        return cut;
    }

    /// <summary>
    /// The area seen from an eye on the plan cut at its height: out to a distance, all round - or,
    /// facing a way, within a field of view - as a polygon, the eye a corner of it when it looks one way.
    /// </summary>
    public static IReadOnlyList<Point2D> VisibleArea(IReadOnlyList<(Point2D A, Point2D B, Guid Element)> slice, Point2D eye, double reach = 30000,
        double? facing = null, double field = 360, int rays = 720)
    {
        var near = slice.Where(segment => DistanceToSegment(eye, segment.A, segment.B) < reach).ToList();
        var whole = facing is null || field >= 360;
        var start = whole ? 0 : facing!.Value - field / 2;
        var sweep = whole ? 360 : field;
        var count = Math.Max(8, (int)Math.Round(rays * sweep / 360));
        var outline = new List<Point2D>(count + 2);
        if (!whole) outline.Add(eye);
        for (var i = 0; i <= count - (whole ? 1 : 0); i++)
        {
            var angle = (start + sweep * i / count) * Math.PI / 180;
            var direction = new Vector2D(Math.Cos(angle), Math.Sin(angle));
            outline.Add(eye + direction * Reach(near, eye, direction, reach));
        }

        return outline;
    }

    /// <summary>How far a line from a point goes before it meets something, up to a distance.</summary>
    public static double Reach(IReadOnlyList<(Point2D A, Point2D B, Guid Element)> slice, Point2D from, Vector2D direction, double reach)
    {
        var best = reach;
        foreach (var (a, b, _) in slice)
        {
            var edge = b - a;
            var denominator = direction.Cross(edge);
            if (Math.Abs(denominator) < 1e-12) continue;
            var offset = a - from;
            var t = offset.Cross(edge) / denominator;
            var u = offset.Cross(direction) / denominator;
            if (t > 1e-6 && t < best && u >= -1e-9 && u <= 1 + 1e-9) best = t;
        }

        return best;
    }

    private static double DistanceToSegment(Point2D p, Point2D a, Point2D b)
    {
        var ab = b - a;
        var length = ab.Dot(ab);
        var t = length < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / length, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>A polygon's area, m².</summary>
    public static double Area(IReadOnlyList<Point2D> polygon) => Math.Abs(Polygon2D.SignedArea(polygon)) / 1e6;

    /// <summary>
    /// Whether one point can see another: the line between them clear of every solid but glass -
    /// and but the elements asked to be left out, the one looked at and the one looking among them.
    /// </summary>
    /// <param name="bounds">Each mesh's bounds, worked out once where many lines are looked along.</param>
    public static SightLine Line(IReadOnlyList<Mesh3D> meshes, Point3D from, Point3D to, ICollection<Guid>? ignore = null, IReadOnlyList<(Point3D Min, Point3D Max)?>? bounds = null)
    {
        var (minX, maxX) = (Math.Min(from.X, to.X), Math.Max(from.X, to.X));
        var (minY, maxY) = (Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y));
        var (minZ, maxZ) = (Math.Min(from.Z, to.Z), Math.Max(from.Z, to.Z));
        for (var m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m];
            if (SeenThrough.Contains(mesh.Kind) || ignore?.Contains(mesh.OwnerId) == true || ignore?.Contains(mesh.ElementId) == true) continue;
            if ((bounds is null ? mesh.Bounds() : bounds[m]) is not { } box || box.Max.X < minX || box.Min.X > maxX || box.Max.Y < minY || box.Min.Y > maxY || box.Max.Z < minZ || box.Min.Z > maxZ) continue;
            var (positions, indices) = (mesh.Positions, mesh.Indices);
            for (var i = 0; i + 2 < indices.Count; i += 3)
                if (Hits(from, to, positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]) is { } t && t > 1e-4 && t < 1 - 1e-4)
                    return new SightLine(false, mesh.OwnerId, from, to);
        }

        return new SightLine(true, null, from, to);
    }

    /// <summary>How far along a line from one point to another it meets a triangle, 0 to 1, or null where it misses.</summary>
    public static double? Hits(Point3D p, Point3D q, Point3D a, Point3D b, Point3D c)
    {
        var dir = Camera3D.Minus(q, p);
        var (e1, e2) = (Camera3D.Minus(b, a), Camera3D.Minus(c, a));
        var h = Camera3D.Cross(dir, e2);
        var det = Camera3D.Dot(e1, h);
        if (Math.Abs(det) < 1e-9) return null;
        var s = Camera3D.Minus(p, a);
        var u = Camera3D.Dot(s, h) / det;
        if (u < 0 || u > 1) return null;
        var qv = Camera3D.Cross(s, e1);
        var v = Camera3D.Dot(dir, qv) / det;
        if (v < 0 || u + v > 1) return null;
        var t = Camera3D.Dot(e2, qv) / det;
        return t is >= 0 and <= 1 ? t : null;
    }

    /// <summary>
    /// What a person sees from where they stand, at their own eye height: the area seen all round,
    /// out to 30 m, its size, the rooms whose middles are in it and the doors and windows whose
    /// middles they can see.
    /// </summary>
    public static (IReadOnlyList<Point2D> Area, double SquareMetres, IReadOnlyList<string> Rooms, IReadOnlyList<Element> Openings) Of(
        BimDocument document, Component person, IReadOnlyList<Mesh3D>? meshes = null)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var type = document.FindType<ComponentType>(person.TypeId);
        var walker = type is null ? Walker.For(WalkerKind.Adult, us) : Walker.Of(type, us);
        meshes ??= ModelMeshBuilder.Build(document, element => element.Id != person.Id);
        var feet = type is null ? document.FindLevel(person.LevelId)?.Elevation ?? 0 : ComponentHosting.BaseElevation(document, person, type);
        var eye = feet + walker.EyeHeight;
        var area = VisibleArea(SliceAt(meshes, eye), person.Location);

        var rooms = document.All<Room>()
            .Where(room => room.LevelId == person.LevelId && room.GetBoundary(document) is { IsEnclosed: true } boundary &&
                           Polygon2D.Contains(area, Polygon2D.Centroid(boundary.Polygon, boundary.Polygon[0])))
            .Select(RoomsOf.Label).ToList();

        var openings = new List<Element>();
        foreach (var opening in document.Openings.Where(opening => opening.LevelId == person.LevelId || document.Walls.Any(wall => wall.Id == opening.HostWallId && wall.LevelId == person.LevelId)))
        {
            if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType || document.Find<Wall>(opening.HostWallId) is not { } host) continue;
            var middle = host.LocationCurve.PointAt(Math.Clamp(opening.DistanceAlongWall, 0, host.Length));
            var height = host.GetBaseElevation(document) + opening.SillHeight + openingType.Height / 2;
            if (person.Location.DistanceTo(middle) > 30000) continue;
            if (Line(meshes, new Point3D(person.Location.X, person.Location.Y, eye), new Point3D(middle.X, middle.Y, height), new HashSet<Guid> { opening.Id, host.Id }).Clear)
                openings.Add(opening);
        }

        return (area, Area(area), rooms, openings);
    }
}

/// <summary>How good a view out is, as BS EN 17037 grades it.</summary>
public enum ViewLevel
{
    None,
    Minimum,
    Medium,
    High
}

/// <summary>A view out from a point: how wide the windows seen are, how far the view goes, which of sky, landscape and ground it takes in, and so its level.</summary>
public sealed record ViewOutResult(double SightAngle, double OutsideDistance, bool Sky, bool Landscape, bool Ground, int Windows, ViewLevel Level)
{
    public int Layers => (Sky ? 1 : 0) + (Landscape ? 1 : 0) + (Ground ? 1 : 0);

    public string Said => Windows == 0
        ? "No window seen"
        : $"{Level}: {SightAngle:0}° of window, the view going {(OutsideDistance >= ViewOut.Far ? "over 1 km" : $"{OutsideDistance / 1000:0.#} m")} out, " +
          $"{string.Join(", ", new[] { Sky ? "sky" : null, Landscape ? "landscape" : null, Ground ? "ground" : null }.OfType<string>().DefaultIfEmpty("nothing outside"))}";
}

/// <summary>
/// The view out of a room, as BS EN 17037 (Daylight in buildings, 5.2) grades it from a point at a
/// seated eye height of 1.2 m - or a standing one of 1.7 m: the horizontal angle the windows seen
/// take up, the distance from the windows to what stands in the way outside, and the layers seen
/// - sky, landscape and ground, found by lines from the eye over the head and sill of each window
/// carried on outside until they meet the ground or an obstruction. Minimum: 14°, 6 m and the
/// landscape; medium: 28°, 20 m and landscape with sky or ground; high: 54°, 50 m and all three -
/// the view's level the lowest the three reach.
/// </summary>
public static class ViewOut
{
    public const double Seated = 1200;
    public const double Standing = 1700;

    /// <summary>How far a view is looked along for what stands in its way, mm: a kilometre.</summary>
    public const double Far = 1_000_000;

    /// <summary>One window or glazed panel to see out of: its glass's two sides in plan, the way out, its bottom and top, and the elements it is part of.</summary>
    private sealed record Pane(Point2D A, Point2D B, Vector2D Out, double Sill, double Head, Guid Id, Guid Host);

    /// <summary>The panes of glass to the outside on a storey: windows and glazed doors in outside walls, and the glass of outside curtain walls.</summary>
    private static List<Pane> Panes(BimDocument document, Guid levelId)
    {
        var panes = new List<Pane>();
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == levelId && wall.Length > 1))
        {
            var curtain = document.FindType<CurtainWallType>(wall.TypeId);
            var outside = curtain is not null ? curtain.Function != WallFunction.Interior : document.GetWallType(wall)?.Function is WallFunction.Exterior;
            if (!outside || document.GetWallType(wall) is not { } body) continue;
            var bottom = wall.GetBaseElevation(document);
            Vector2D OutAt(double along) => wall.ExteriorNormalAt(Math.Clamp(along, 0, wall.Length));

            if (CurtainLayout.Of(document, wall) is { } layout)
            {
                foreach (var cell in layout.Cells.Where(cell => cell.Kind == CurtainPanelKind.Glazed))
                    panes.Add(new Pane(wall.PointAt(body.Structure, cell.ClearFrom, 0), wall.PointAt(body.Structure, cell.ClearTo, 0), OutAt((cell.ClearFrom + cell.ClearTo) / 2),
                        bottom + cell.ClearBottom, bottom + cell.ClearTop, wall.Id, wall.Id));
                continue;
            }

            foreach (var opening in WallOpenings.Of(document, wall))
            {
                if (document.FindType<OpeningType>(opening.TypeId) is not { } type) continue;
                if (type is DoorType door && !Architecture.Doors.Glazed(door)) continue;
                var (from, to) = opening.GetSpan(type);
                var frame = WindowRules.Frame;
                panes.Add(new Pane(wall.PointAt(body.Structure, from + frame, 0), wall.PointAt(body.Structure, to - frame, 0), OutAt((from + to) / 2),
                    bottom + opening.SillHeight + (type is DoorType ? 300 : frame), bottom + opening.SillHeight + type.Height - frame, opening.Id, wall.Id));
            }
        }

        return panes;
    }

    /// <summary>The view out from a point on a storey, at an eye height over its floor.</summary>
    public static ViewOutResult From(BimDocument document, Guid levelId, Point2D at, double eyeHeight, IReadOnlyList<Mesh3D>? meshes = null, ICollection<Guid>? ignore = null)
    {
        var alone = ModelCache.Of(document) is null;
        using var cache = ModelCache.Begin(document);

        // A room's view out is through its own windows: those in the walls round it - where the point is in no room, any.
        var walls = RoomsOf.Containing(document, levelId, at)?.GetBoundary(document) is { IsEnclosed: true } room ? room.BoundingWalls.Select(wall => wall.Id).ToHashSet() : null;
        var panes = Panes(document, levelId).Where(pane => walls is null || walls.Contains(pane.Host)).ToList();
        if (panes.Count == 0) return new ViewOutResult(0, 0, false, false, false, 0, ViewLevel.None);

        // Asked alone, only what could be in the way is built; in a schedule of rooms, the whole model once for all of them.
        meshes ??= alone
            ? ModelMeshBuilder.Build(document, element => InSight(document, element, at, panes))
            : ModelCache.Of(document)!.Keep("model meshes", document, null, null, () => ModelMeshBuilder.Build(document));
        var bounds = meshes.Select(mesh => mesh.Bounds()).ToList();
        var floor = Architecture.FloorSurfaces.FloorUnder(document, levelId, at) is { } slab ? slab.GetTopElevation(document) : document.FindLevel(levelId)?.Elevation ?? 0;
        var eyeZ = floor + eyeHeight;
        var eye = new Point3D(at.X, at.Y, eyeZ);
        var ground = Wind.Ground(document);
        var angles = new List<(double From, double To)>();
        var (distance, sky, landscape, groundSeen, seen) = (0.0, false, false, false, 0);

        // Inside is over a floor of the storey; outside, past the glass, is not.
        var floors = document.All<Floor>().Where(floor => floor.LevelId == levelId && floor.Boundary.Count >= 3).Select(floor => floor.Boundary).ToList();
        bool Inside(Point2D point) => floors.Any(outline => Polygon2D.Contains(outline, point));

        foreach (var found in panes)
        {
            var middle = found.A.MidpointTo(found.B);

            // Looked through away from the eye - out, where what lies past it is not over a floor - its middle in sight.
            var pane = (middle - at).Dot(found.Out) >= 0 ? found : found with { Out = -found.Out };
            if (floors.Count > 0 && Inside(middle + pane.Out * 800)) continue;
            var centre = new Point3D(middle.X, middle.Y, (pane.Sill + pane.Head) / 2);
            var blockers = new HashSet<Guid>(ignore ?? Array.Empty<Guid>()) { pane.Id, pane.Host };
            if (!Sight.Line(meshes, eye, centre, blockers, bounds).Clear) continue;
            seen++;

            var (a1, a2) = (Math.Atan2(pane.A.Y - at.Y, pane.A.X - at.X) * 180 / Math.PI, Math.Atan2(pane.B.Y - at.Y, pane.B.X - at.X) * 180 / Math.PI);
            var span = ((a2 - a1) % 360 + 540) % 360 - 180;
            angles.Add(span >= 0 ? (a1, a1 + span) : (a1 + span, a1));

            // Out from the glass, square to it, at its middle height: how far before something is in the way, and how high that is.
            var (reach, top) = Obstruction(meshes, bounds, centre, pane.Out, blockers);
            distance = Math.Max(distance, reach);

            // The lines from the eye over the head and the sill, carried on outside.
            var r = Math.Max(1, (middle - at).Dot(pane.Out));
            var (up, down) = ((pane.Head - eyeZ) / r, (pane.Sill - eyeZ) / r);
            var far = r + reach;
            sky |= reach >= Far ? up > 0 : eyeZ + up * far > top;
            groundSeen |= down < 0 && ((eyeZ - ground) / -down < far || reach >= Far);
            landscape |= reach >= Far
                ? down <= 0 && up >= 0
                : Math.Max(eyeZ + down * far, ground) < Math.Min(eyeZ + up * far, top);
        }

        var sight = Union(angles);
        var level = Grade(sight, distance, sky, landscape, groundSeen);
        return new ViewOutResult(sight, distance, sky, landscape, groundSeen, seen, seen == 0 ? ViewLevel.None : level);
    }

    /// <summary>
    /// Whether something could be in the way of the view out from a point: near in plan to the line
    /// from it to a pane, or to the line out from a pane either way - or of a kind not placed so
    /// simply, which is always taken. The ground is never in the way.
    /// </summary>
    private static bool InSight(BimDocument document, Element element, Point2D at, IReadOnlyList<Pane> panes)
    {
        if (element is Toposurface or GroundRegion or SitePad) return false;
        if (PlanBox(document, element) is not { } box) return true;
        box = box.Expand(500);
        foreach (var pane in panes)
        {
            var middle = pane.A.MidpointTo(pane.B);
            if (Passes(at, middle - at, box) || Passes(middle, pane.Out * Far, box) || Passes(middle, pane.Out * -Far, box)) return true;
        }

        return false;
    }

    /// <summary>Where in plan a thing stands, for telling whether a line could pass it; null for what is not placed so simply.</summary>
    private static BoundingBox2D? PlanBox(BimDocument document, Element element) => element switch
    {
        Wall { IsCurved: false } wall => BoundingBox2D.Around(new[] { wall.Start, wall.End }).Expand((document.GetWallType(wall)?.Width ?? 0) / 2),
        Slab slab when slab.Boundary.Count > 0 => BoundingBox2D.Around(slab.Boundary),
        Component component when document.FindType<ComponentType>(component.TypeId) is { } type =>
            BoundingBox2D.Around(new[] { component.Location }).Expand(Math.Max(type.Width, type.Depth)),
        Column column => BoundingBox2D.Around(new[] { column.Location }).Expand(1000),
        Mep.Pipe pipe => BoundingBox2D.Around(new[] { pipe.Start, pipe.End }).Expand(pipe.Diameter),
        Mep.Duct duct => BoundingBox2D.Around(new[] { duct.Start, duct.End }).Expand(Math.Max(duct.Width, duct.Height)),
        _ => null
    };

    /// <summary>Whether a line from a point, so far along a step, passes over a box in plan.</summary>
    private static bool Passes(Point2D from, Vector2D step, BoundingBox2D box)
    {
        var (near, far) = (0.0, 1.0);
        foreach (var (origin, along, low, high) in new[] { (from.X, step.X, box.MinX, box.MaxX), (from.Y, step.Y, box.MinY, box.MaxY) })
        {
            if (Math.Abs(along) < 1e-12)
            {
                if (origin < low || origin > high) return false;
                continue;
            }

            var (t1, t2) = ((low - origin) / along, (high - origin) / along);
            if (t1 > t2) (t1, t2) = (t2, t1);
            (near, far) = (Math.Max(near, t1), Math.Min(far, t2));
            if (near > far) return false;
        }

        return true;
    }

    /// <summary>The level a view reaches: the lowest its angle, distance and layers reach.</summary>
    public static ViewLevel Grade(double angle, double distance, bool sky, bool landscape, bool ground)
    {
        var layers = (sky ? 1 : 0) + (ground ? 1 : 0);
        if (!landscape) return ViewLevel.None;
        if (angle >= 54 && distance >= 50000 && layers == 2) return ViewLevel.High;
        if (angle >= 28 && distance >= 20000 && layers >= 1) return ViewLevel.Medium;
        if (angle >= 14 && distance >= 6000) return ViewLevel.Minimum;
        return ViewLevel.None;
    }

    /// <summary>How far a line out from a point goes before it meets something solid, and the top of what it meets.</summary>
    private static (double Distance, double Top) Obstruction(IReadOnlyList<Mesh3D> meshes, IReadOnlyList<(Point3D Min, Point3D Max)?> bounds, Point3D from, Vector2D way, ICollection<Guid> ignore)
    {
        var to = new Point3D(from.X + way.X * Far, from.Y + way.Y * Far, from.Z);
        var (best, top) = (Far, double.NegativeInfinity);

        // Whether the line out, as far as anything found so far, passes over a box in plan at all.
        bool Crosses((Point3D Min, Point3D Max) box)
        {
            var (near, far) = (0.0, best);
            foreach (var (origin, step, low, high) in new[] { (from.X, way.X, box.Min.X, box.Max.X), (from.Y, way.Y, box.Min.Y, box.Max.Y) })
            {
                if (Math.Abs(step) < 1e-12)
                {
                    if (origin < low || origin > high) return false;
                    continue;
                }

                var (t1, t2) = ((low - origin) / step, (high - origin) / step);
                if (t1 > t2) (t1, t2) = (t2, t1);
                (near, far) = (Math.Max(near, t1), Math.Min(far, t2));
                if (near > far) return false;
            }

            return true;
        }

        for (var m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m];
            if (mesh.Kind is MeshKind.Glazing or MeshKind.Terrain || ignore.Contains(mesh.OwnerId) || ignore.Contains(mesh.ElementId)) continue;
            if (bounds[m] is not { } box || box.Min.Z > from.Z || box.Max.Z < from.Z || !Crosses(box)) continue;
            var (positions, indices) = (mesh.Positions, mesh.Indices);
            for (var i = 0; i + 2 < indices.Count; i += 3)
                if (Sight.Hits(from, to, positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]) is { } t && t * Far > 50 && t * Far < best)
                    (best, top) = (t * Far, box.Max.Z);
        }

        return (best, top);
    }

    /// <summary>How many degrees a set of arcs covers, overlaps counted once.</summary>
    private static double Union(List<(double From, double To)> arcs)
    {
        if (arcs.Count == 0) return 0;
        var normalised = arcs.Select(arc =>
        {
            var from = (arc.From % 360 + 360) % 360;
            return (From: from, To: from + (arc.To - arc.From));
        }).SelectMany(arc => arc.To > 360 ? new[] { (arc.From, 360.0), (0.0, arc.To - 360) } : new[] { arc }).OrderBy(arc => arc.Item1).ToList();
        double total = 0, start = normalised[0].Item1, end = normalised[0].Item2;
        foreach (var (from, to) in normalised.Skip(1))
        {
            if (from > end)
            {
                total += end - start;
                (start, end) = (from, to);
            }
            else end = Math.Max(end, to);
        }

        return total + end - start;
    }

    /// <summary>
    /// A room's view out, seated in its middle: BS EN 17037's 1.2 m eye, at the point of the room
    /// furthest in from its walls - or null for a room not enclosed.
    /// </summary>
    public static ViewOutResult? OfRoom(BimDocument document, Room room, IReadOnlyList<Mesh3D>? meshes = null)
    {
        if (room.GetBoundary(document) is not { IsEnclosed: true } boundary || boundary.Polygon.Count < 3) return null;
        var middle = Polygon2D.Centroid(boundary.Polygon, boundary.Polygon[0]);
        if (!Polygon2D.Contains(boundary.Polygon, middle)) middle = boundary.Polygon.Aggregate((a, b) => a.MidpointTo(b));
        return From(document, room.LevelId, middle, Seated, meshes);
    }
}
