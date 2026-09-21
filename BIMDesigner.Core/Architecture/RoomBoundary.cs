using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>The enclosing loop found around a point, and the quantities that follow from it.</summary>
public sealed class RoomBoundaryResult
{
    public required bool IsEnclosed { get; init; }

    /// <summary>
    /// The room's outline, on the inward faces of the bounding walls. Empty when the point
    /// is not enclosed.
    /// </summary>
    public required IReadOnlyList<Point2D> Polygon { get; init; }

    /// <summary>The walls that bound the room, in order around it.</summary>
    public required IReadOnlyList<Wall> BoundingWalls { get; init; }

    /// <summary>Millimetres squared.</summary>
    public required double Area { get; init; }

    /// <summary>Millimetres.</summary>
    public required double Perimeter { get; init; }

    /// <summary>
    /// The area-weighted centre of the room, which is where its tag belongs. The average of
    /// the corners would drift toward whichever end has more of them, and land outside the
    /// room altogether on an L-shaped plan.
    /// </summary>
    public required Point2D Centroid { get; init; }

    public static RoomBoundaryResult NotEnclosed { get; } = new()
    {
        IsEnclosed = false,
        Polygon = Array.Empty<Point2D>(),
        BoundingWalls = Array.Empty<Wall>(),
        Area = 0,
        Perimeter = 0,
        Centroid = default
    };
}

/// <summary>
/// Finds the room enclosing a point (specification section 3.7, "rooms auto-detect bounding
/// elements").
///
/// A room is not drawn; it is discovered. The user clicks inside an enclosure and the walls
/// around it are traced, so moving a wall changes the room's area without anyone editing
/// the room. That is the whole difference between a room and a rectangle labelled "room".
///
/// The method is the standard planar face traversal: cut every wall centreline at its
/// crossings to get a graph, shoot a ray from the point to find one edge of the enclosing
/// face, then walk that face by always taking the sharpest available turn. Walking it with
/// the interior kept on the left means the walk closes exactly when the room closes.
/// </summary>
public static class RoomBoundary
{
    /// <summary>Points closer than this are the same node in the graph. Millimetres.</summary>
    private const double NodeTolerance = 0.5;

    /// <summary>Guards against a malformed graph looping forever.</summary>
    private const int MaxTraversalSteps = 4096;

    private sealed record Segment(Wall Wall, Point2D Start, Point2D End)
    {
        public Vector2D Direction => (End - Start).NormalisedOrDefault(Vector2D.UnitX);
        public double Length => Start.DistanceTo(End);
    }

    /// <summary>A directed use of a segment: the same wall walked one way or the other.</summary>
    private sealed record HalfEdge(Segment Segment, bool Forward)
    {
        public Point2D From => Forward ? Segment.Start : Segment.End;
        public Point2D To => Forward ? Segment.End : Segment.Start;
        public Vector2D Direction => (To - From).NormalisedOrDefault(Vector2D.UnitX);
    }

    /// <summary>Two traces landing this close describe the same space. Millimetres.</summary>
    private const double SameSpaceTolerance = 1.0;

    /// <summary>
    /// The room already occupying the space around this point, or null when it is free.
    ///
    /// Two rooms in one space is always a mistake: their areas both count, so every schedule
    /// double-counts the floor. The test is whether the point traces to the same enclosure
    /// as an existing room, not whether it falls inside that room's outline - a point right
    /// on a boundary is ambiguous for the second question and never for the first.
    /// </summary>
    public static Room? FindRoomEnclosing(BimDocument document, Guid levelId, Point2D point)
    {
        var boundary = Trace(document, levelId, point);
        if (!boundary.IsEnclosed) return null;

        foreach (var room in document.Elements.OfType<Room>())
        {
            if (room.LevelId != levelId) continue;

            var existing = room.GetBoundary(document);
            if (!existing.IsEnclosed) continue;

            if (existing.Centroid.DistanceTo(boundary.Centroid) <= SameSpaceTolerance) return room;
        }

        return null;
    }

    public static RoomBoundaryResult Trace(BimDocument document, Guid levelId, Point2D seed)
    {
        var segments = SplitAtCrossings(Collect(document, levelId));
        if (segments.Count == 0) return RoomBoundaryResult.NotEnclosed;

        var start = FindEdgeFacing(segments, seed);
        if (start is null) return RoomBoundaryResult.NotEnclosed;

        var loop = WalkFace(segments, start);
        if (loop is null || loop.Count < 3) return RoomBoundaryResult.NotEnclosed;

        // The walk keeps the interior on the left, so an enclosing loop comes back
        // anticlockwise. A clockwise one means the point was outside the building and the
        // walk went round the outside instead.
        var centreline = loop.Select(edge => edge.From).ToList();
        if (Polygon2D.SignedArea(centreline) <= 0) return RoomBoundaryResult.NotEnclosed;

        var polygon = InsetToWallFaces(document, loop);
        if (polygon.Count < 3) return RoomBoundaryResult.NotEnclosed;

        return new RoomBoundaryResult
        {
            IsEnclosed = true,
            Polygon = polygon,
            BoundingWalls = loop.Select(edge => edge.Segment.Wall).Distinct().ToList(),
            Area = Polygon2D.Area(polygon),
            Perimeter = Polygon2D.Perimeter(polygon),
            Centroid = Polygon2D.Centroid(polygon, seed)
        };
    }

    // ---- building the graph ----------------------------------------------------

    private static List<Segment> Collect(BimDocument document, Guid levelId) =>
        document.Walls
            .Where(wall => wall.LevelId == levelId && wall.RoomBounding && wall.Length > NodeTolerance)
            .SelectMany(Pieces)
            .ToList();

    /// <summary>
    /// A wall as the walk sees it: one segment, or for a curved wall a chain of short ones
    /// following the arc. The points between them join only the wall's own pieces, so the walk
    /// passes straight through them and the room takes the shape of the curve.
    /// </summary>
    private static IEnumerable<Segment> Pieces(Wall wall)
    {
        if (!wall.IsCurved) return new[] { new Segment(wall, wall.Start, wall.End) };

        var points = wall.LocationCurve.Points();
        return points.Zip(points.Skip(1), (a, b) => new Segment(wall, a, b));
    }

    /// <summary>
    /// Cuts every segment where another crosses it, so that walls meeting part-way along
    /// each other become proper nodes. Without this a T-junction is invisible to the walk
    /// and the trace runs straight past the wall that should have turned it.
    /// </summary>
    private static List<Segment> SplitAtCrossings(List<Segment> segments)
    {
        var result = new List<Segment>();

        foreach (var segment in segments)
        {
            var cuts = new List<double> { 0, 1 };

            foreach (var other in segments)
            {
                if (ReferenceEquals(other, segment)) continue;

                foreach (var t in CrossingParameters(segment, other))
                    if (t > 0 && t < 1) cuts.Add(t);
            }

            cuts.Sort();

            for (var i = 0; i < cuts.Count - 1; i++)
            {
                var from = Lerp(segment, cuts[i]);
                var to = Lerp(segment, cuts[i + 1]);
                if (from.DistanceTo(to) > NodeTolerance)
                    result.Add(new Segment(segment.Wall, from, to));
            }
        }

        return result;
    }

    /// <summary>Where along <paramref name="segment"/> the other one touches or crosses it.</summary>
    private static IEnumerable<double> CrossingParameters(Segment segment, Segment other)
    {
        var d1 = segment.End - segment.Start;
        var d2 = other.End - other.Start;
        var denominator = d1.Cross(d2);

        if (Math.Abs(denominator) > 1e-9)
        {
            var delta = other.Start - segment.Start;
            var t = delta.Cross(d2) / denominator;
            var u = delta.Cross(d1) / denominator;

            var slackT = NodeTolerance / Math.Max(segment.Length, NodeTolerance);
            var slackU = NodeTolerance / Math.Max(other.Length, NodeTolerance);

            if (t >= -slackT && t <= 1 + slackT && u >= -slackU && u <= 1 + slackU)
                yield return Math.Clamp(t, 0, 1);

            yield break;
        }

        // Parallel: an overlapping run still contributes its endpoints as nodes.
        foreach (var point in new[] { other.Start, other.End })
        {
            var t = ParameterOf(segment, point);
            if (t is >= 0 and <= 1 && Lerp(segment, t).DistanceTo(point) <= NodeTolerance)
                yield return t;
        }
    }

    private static double ParameterOf(Segment segment, Point2D point)
    {
        var d = segment.End - segment.Start;
        var lengthSquared = d.Dot(d);
        return lengthSquared <= 0 ? 0 : (point - segment.Start).Dot(d) / lengthSquared;
    }

    private static Point2D Lerp(Segment segment, double t) =>
        segment.Start + (segment.End - segment.Start) * t;

    // ---- finding a starting edge -----------------------------------------------

    /// <summary>
    /// Shoots a ray from the seed and returns the nearest edge it hits, oriented so that the
    /// seed lies to its left - which is the direction the face walk needs.
    ///
    /// The ray is deliberately not axis-aligned. A vertical or horizontal ray in a building
    /// full of vertical and horizontal walls hits corners and runs along walls constantly;
    /// a slightly skew one effectively never does.
    /// </summary>
    private static HalfEdge? FindEdgeFacing(List<Segment> segments, Point2D seed)
    {
        var ray = new Vector2D(0.0137, -1).NormalisedOrDefault(new Vector2D(0, -1));

        Segment? nearest = null;
        var nearestDistance = double.MaxValue;
        var hit = seed;

        foreach (var segment in segments)
        {
            var d = segment.End - segment.Start;
            var denominator = ray.Cross(d);
            if (Math.Abs(denominator) < 1e-12) continue;

            var delta = segment.Start - seed;
            var distance = delta.Cross(d) / denominator;     // along the ray
            var u = delta.Cross(ray) / denominator;          // along the segment

            if (distance <= NodeTolerance || u < 0 || u > 1) continue;
            if (distance >= nearestDistance) continue;

            nearest = segment;
            nearestDistance = distance;
            hit = seed + ray * distance;
        }

        if (nearest is null) return null;

        // Orient the edge so the seed is on its left.
        var forward = new HalfEdge(nearest, Forward: true);
        return forward.Direction.PerpendicularLeft().Dot(seed - hit) > 0
            ? forward
            : new HalfEdge(nearest, Forward: false);
    }

    // ---- walking the face ------------------------------------------------------

    /// <summary>
    /// Follows the face with the interior on the left. At each node it takes the sharpest
    /// clockwise turn available, which is what hugs the face rather than wandering off into
    /// the next one.
    /// </summary>
    private static List<HalfEdge>? WalkFace(List<Segment> segments, HalfEdge start)
    {
        var leaving = BuildAdjacency(segments);
        var loop = new List<HalfEdge> { start };
        var current = start;

        for (var step = 0; step < MaxTraversalSteps; step++)
        {
            var next = NextEdge(leaving, current);
            if (next is null) return null;

            if (Same(next.From, start.From) && Same(next.To, start.To)) return loop;

            loop.Add(next);
            current = next;
        }

        return null;
    }

    private static Dictionary<(long, long), List<HalfEdge>> BuildAdjacency(List<Segment> segments)
    {
        var leaving = new Dictionary<(long, long), List<HalfEdge>>();

        foreach (var segment in segments)
        {
            foreach (var edge in new[]
                     {
                         new HalfEdge(segment, Forward: true),
                         new HalfEdge(segment, Forward: false)
                     })
            {
                var key = Key(edge.From);
                if (!leaving.TryGetValue(key, out var list))
                    leaving[key] = list = new List<HalfEdge>();

                list.Add(edge);
            }
        }

        return leaving;
    }

    /// <summary>
    /// The next edge around the face: of everything leaving this node, the one that turns
    /// most sharply clockwise from the way we came in. Turning back the way we came is the
    /// last resort, which is what lets the walk escape a dead-end spur.
    /// </summary>
    private static HalfEdge? NextEdge(Dictionary<(long, long), List<HalfEdge>> leaving, HalfEdge incoming)
    {
        if (!leaving.TryGetValue(Key(incoming.To), out var candidates)) return null;

        var back = -incoming.Direction;

        HalfEdge? best = null;
        var bestTurn = double.MaxValue;

        foreach (var candidate in candidates)
        {
            // Never take the identical edge straight back on itself unless nothing else
            // exists; that is handled by giving it the largest possible turn.
            var isReverse = Same(candidate.To, incoming.From) && Same(candidate.From, incoming.To);
            var turn = isReverse ? double.MaxValue - 1 : ClockwiseAngle(back, candidate.Direction);

            if (turn >= bestTurn) continue;

            best = candidate;
            bestTurn = turn;
        }

        return best;
    }

    /// <summary>Angle from <paramref name="from"/> to <paramref name="to"/>, measured clockwise, in [0, 2π).</summary>
    private static double ClockwiseAngle(Vector2D from, Vector2D to)
    {
        var angle = Math.Atan2(from.Cross(to), from.Dot(to));
        // Atan2 gives the anticlockwise angle in (-π, π]; flip and normalise.
        var clockwise = -angle;
        return clockwise < 0 ? clockwise + 2 * Math.PI : clockwise;
    }

    // ---- turning the loop into a room ------------------------------------------

    /// <summary>
    /// Moves each edge of the loop from the wall's centreline to the face that looks into
    /// the room, then meets the moved edges at their corners.
    ///
    /// A room measured to wall centrelines would count half of every wall as floor area,
    /// which is not what anyone builds, sells or heats.
    /// </summary>
    private static List<Point2D> InsetToWallFaces(BimDocument document, List<HalfEdge> loop)
    {
        var lines = new List<Line2D>(loop.Count);

        foreach (var edge in loop)
        {
            var type = document.GetWallType(edge.Segment.Wall);
            var half = (type?.Width ?? 0) / 2;

            // The interior is on the left of the walk, so the inward face is the left one.
            var inward = edge.Direction.PerpendicularLeft() * half;
            lines.Add(new Line2D(edge.From + inward, edge.Direction));
        }

        var polygon = new List<Point2D>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var current = lines[i];
            var next = lines[(i + 1) % lines.Count];

            // Parallel neighbours have no corner; the shared endpoint stands in for one.
            polygon.Add(Line2D.TryIntersect(current, next, out var corner)
                ? corner
                : loop[(i + 1) % loop.Count].From);
        }

        return polygon;
    }

    // ---- node identity ---------------------------------------------------------

    private static (long, long) Key(Point2D point) => (
        (long)Math.Round(point.X / NodeTolerance),
        (long)Math.Round(point.Y / NodeTolerance));

    private static bool Same(Point2D a, Point2D b) => a.DistanceTo(b) <= NodeTolerance;
}
