using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The geometry of drawing walls with an offset (specification section 3.1, "offset while
/// placing").
///
/// The user clicks one line and the wall is built on another, parallel to it. A positive
/// offset moves the wall toward its exterior, so flipping the wall being drawn with the
/// spacebar also swaps which side the offset goes. Walls drawn one after another meet where
/// their offset lines cross, not where the clicks were, or every corner of an offset chain
/// would come out broken.
/// </summary>
public static class WallDrawing
{
    /// <summary>
    /// How far a corner may stray from the click it belongs to before the two lines are
    /// treated as not meeting. Nearly parallel segments cross miles away.
    /// </summary>
    private const double CornerReachPerOffset = 10;

    /// <summary>The exterior side of a wall drawn from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static Vector2D ExteriorOf(Point2D from, Point2D to, bool flipped)
    {
        var normal = (to - from).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        return flipped ? -normal : normal;
    }

    /// <summary>The location line of a wall clicked from one point to another, with the offset applied.</summary>
    public static (Point2D Start, Point2D End) OffsetSegment(Point2D from, Point2D to, double offset, bool flipped)
    {
        if (offset == 0) return (from, to);

        var shift = ExteriorOf(from, to, flipped) * offset;
        return (from + shift, to + shift);
    }

    /// <summary>
    /// Where two consecutive offset segments meet, or null when they run in line or so
    /// nearly parallel that the crossing is nowhere near the click they share.
    /// </summary>
    public static Point2D? Corner(
        (Point2D Start, Point2D End) previous, (Point2D Start, Point2D End) next, Point2D click, double offset)
    {
        var a = Line2D.Through(previous.Start, previous.End);
        var b = Line2D.Through(next.Start, next.End);

        if (!Line2D.TryIntersect(a, b, out var corner)) return null;
        if (corner.DistanceTo(click) > CornerReachPerOffset * Math.Abs(offset)) return null;

        return corner;
    }
}
