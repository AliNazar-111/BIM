namespace BIMDesigner.Core.Geometry;

/// <summary>
/// An infinite line, used for the construction geometry behind wall joins, trimming and
/// extending. Walls are finite, but the lines their faces lie on are not, and mitring a
/// corner means intersecting those lines well beyond where either wall actually stops.
/// </summary>
public readonly record struct Line2D(Point2D Origin, Vector2D Direction)
{
    /// <summary>Below this, two directions count as parallel and do not intersect.</summary>
    private const double ParallelTolerance = 1e-9;

    public static Line2D Through(Point2D from, Point2D to) =>
        new(from, (to - from).NormalisedOrDefault(Vector2D.UnitX));

    /// <summary>
    /// Finds where two lines cross. Returns false when they are parallel, which callers
    /// must handle rather than ignore: it is the ordinary case for two walls in a straight
    /// run, where there is no corner to mitre.
    /// </summary>
    public static bool TryIntersect(Line2D a, Line2D b, out Point2D point)
    {
        point = default;

        var denominator = a.Direction.Cross(b.Direction);
        if (Math.Abs(denominator) < ParallelTolerance) return false;

        var t = (b.Origin - a.Origin).Cross(b.Direction) / denominator;
        point = a.Origin + a.Direction * t;
        return true;
    }

    /// <summary>
    /// Distance from a point to a finite segment, as opposed to the infinite line through
    /// it. Used to tell whether one wall ends against another's side rather than its end.
    /// </summary>
    public static double DistanceFromSegment(Point2D point, Point2D a, Point2D b)
    {
        var ab = b - a;
        var lengthSquared = ab.Dot(ab);
        if (lengthSquared <= 0) return point.DistanceTo(a);

        var t = Math.Clamp((point - a).Dot(ab) / lengthSquared, 0, 1);
        return point.DistanceTo(a + ab * t);
    }

    /// <summary>The point on this line nearest to <paramref name="point"/>.</summary>
    public Point2D ClosestPointTo(Point2D point)
    {
        var lengthSquared = Direction.Dot(Direction);
        if (lengthSquared <= 0) return Origin;

        return Origin + Direction * ((point - Origin).Dot(Direction) / lengthSquared);
    }
}
