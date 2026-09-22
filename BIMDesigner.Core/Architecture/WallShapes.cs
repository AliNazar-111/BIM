using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>The shapes the wall tool can draw in one go (specification section 3.1, "draw tools").</summary>
public enum WallShape
{
    Line,
    Arc,
    Rectangle,
    Polygon,
    Circle,
    Oval,

    /// <summary>A whole ellipse filling a box, as two half-ellipse walls.</summary>
    Ellipse,

    /// <summary>Half an ellipse on the line between two clicks, bowed out through a third, one wall at a time.</summary>
    PartialEllipse,

    /// <summary>A wall along an existing line - a gridline - picked with one click.</summary>
    Pick,

    /// <summary>A smooth curve through clicked points, finished with Enter or a double click.</summary>
    Spline,

    /// <summary>A smooth curve following a stroke dragged with the mouse.</summary>
    Freehand,

    /// <summary>A wall along one face of a wall already there, on the side clicked.</summary>
    BySegment,

    /// <summary>Walls round every face of a room, clicked inside it.</summary>
    ByRoom
}

/// <summary>One wall of a shape: where it runs, and how far it bows - as an arc, a piece of an ellipse or a spline.</summary>
public readonly record struct WallPiece(Point2D Start, Point2D End, double Bulge, WallEllipse? Ellipse = null, WallSpline? Spline = null)
{
    public WallCurve Curve => WallCurve.Of(Start, End, Bulge, Ellipse, Spline);
}

/// <summary>
/// Closed shapes of walls: rectangles, regular polygons, circles, rounded ovals and ellipses, each
/// placed with two clicks as a set of walls that meet exactly and join cleanly.
///
/// Every shape runs clockwise. A wall's exterior is on the left of the way it runs, and on a
/// clockwise loop left is outside, so the walls of a shape face out without being told to.
/// </summary>
public static class WallShapes
{
    /// <summary>Pieces shorter than this are left out rather than built as slivers.</summary>
    private const double MinimumPiece = 1.0;

    /// <summary>The fewest and most sides a polygon may have.</summary>
    public const int MinSides = 3;
    public const int MaxSides = 32;

    /// <summary>A rectangle between two opposite corners.</summary>
    public static IReadOnlyList<WallPiece> Rectangle(Point2D corner, Point2D opposite)
    {
        var (minX, maxX) = (Math.Min(corner.X, opposite.X), Math.Max(corner.X, opposite.X));
        var (minY, maxY) = (Math.Min(corner.Y, opposite.Y), Math.Max(corner.Y, opposite.Y));
        if (maxX - minX < MinimumPiece || maxY - minY < MinimumPiece) return Array.Empty<WallPiece>();

        return Loop(
            new Point2D(minX, maxY), new Point2D(maxX, maxY),
            new Point2D(maxX, minY), new Point2D(minX, minY));
    }

    /// <summary>
    /// The second corner of a square: as far from the first as the cursor is in its longer
    /// direction, on the cursor's side in both.
    /// </summary>
    public static Point2D SquareCorner(Point2D corner, Point2D cursor)
    {
        var dx = cursor.X - corner.X;
        var dy = cursor.Y - corner.Y;
        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));

        return new Point2D(corner.X + side * (dx < 0 ? -1 : 1), corner.Y + side * (dy < 0 ? -1 : 1));
    }

    /// <summary>
    /// A regular polygon about a centre. Inscribed, the click is a corner; circumscribed, it is
    /// the middle of a side - the two ways of saying how big a polygon is.
    /// </summary>
    public static IReadOnlyList<WallPiece> Polygon(Point2D centre, Point2D click, int sides, bool inscribed)
    {
        sides = Math.Clamp(sides, MinSides, MaxSides);

        var reach = centre.DistanceTo(click);
        if (reach < MinimumPiece) return Array.Empty<WallPiece>();

        var toClick = Math.Atan2(click.Y - centre.Y, click.X - centre.X);
        var step = 2 * Math.PI / sides;

        var radius = inscribed ? reach : reach / Math.Cos(step / 2);
        var first = inscribed ? toClick : toClick + step / 2;

        // Clockwise: each corner a step further round the other way.
        var corners = Enumerable.Range(0, sides)
            .Select(i => first - i * step)
            .Select(angle => new Point2D(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle)))
            .ToArray();

        return Loop(corners);
    }

    /// <summary>
    /// A circle about a centre through a point. A wall needs two ends, so it is two half
    /// circles, joined where they meet at the click and opposite it.
    /// </summary>
    public static IReadOnlyList<WallPiece> Circle(Point2D centre, Point2D click)
    {
        if (centre.DistanceTo(click) < MinimumPiece) return Array.Empty<WallPiece>();

        var opposite = new Point2D(2 * centre.X - click.X, 2 * centre.Y - click.Y);

        // A bulge of -1 is a half circle turning right: clockwise.
        return new[] { new WallPiece(click, opposite, -1), new WallPiece(opposite, click, -1) };
    }

    /// <summary>
    /// A rounded oval filling a box: straight along its long sides, a half circle across each
    /// short end. A square box gives a circle.
    /// </summary>
    public static IReadOnlyList<WallPiece> Oval(Point2D corner, Point2D opposite)
    {
        var (minX, maxX) = (Math.Min(corner.X, opposite.X), Math.Max(corner.X, opposite.X));
        var (minY, maxY) = (Math.Min(corner.Y, opposite.Y), Math.Max(corner.Y, opposite.Y));
        var width = maxX - minX;
        var height = maxY - minY;
        if (width < MinimumPiece || height < MinimumPiece) return Array.Empty<WallPiece>();

        var pieces = new List<WallPiece>();

        void Straight(Point2D from, Point2D to)
        {
            if (from.DistanceTo(to) >= MinimumPiece) pieces.Add(new WallPiece(from, to, 0));
        }

        if (width >= height)
        {
            var r = height / 2;
            var topLeft = new Point2D(minX + r, maxY);
            var topRight = new Point2D(maxX - r, maxY);
            var bottomRight = new Point2D(maxX - r, minY);
            var bottomLeft = new Point2D(minX + r, minY);

            Straight(topLeft, topRight);
            pieces.Add(new WallPiece(topRight, bottomRight, -1));
            Straight(bottomRight, bottomLeft);
            pieces.Add(new WallPiece(bottomLeft, topLeft, -1));
        }
        else
        {
            var r = width / 2;
            var rightTop = new Point2D(maxX, maxY - r);
            var rightBottom = new Point2D(maxX, minY + r);
            var leftBottom = new Point2D(minX, minY + r);
            var leftTop = new Point2D(minX, maxY - r);

            Straight(rightTop, rightBottom);
            pieces.Add(new WallPiece(rightBottom, leftBottom, -1));
            Straight(leftBottom, leftTop);
            pieces.Add(new WallPiece(leftTop, rightTop, -1));
        }

        return pieces;
    }

    /// <summary>
    /// An ellipse filling a box, its axes along the box's sides. Two half-ellipse walls, meeting
    /// at the ends of the horizontal axis. A square box gives a circle.
    /// </summary>
    public static IReadOnlyList<WallPiece> Ellipse(Point2D corner, Point2D opposite)
    {
        var (minX, maxX) = (Math.Min(corner.X, opposite.X), Math.Max(corner.X, opposite.X));
        var (minY, maxY) = (Math.Min(corner.Y, opposite.Y), Math.Max(corner.Y, opposite.Y));
        var width = maxX - minX;
        var height = maxY - minY;
        if (width < MinimumPiece || height < MinimumPiece) return Array.Empty<WallPiece>();

        var middle = (minY + maxY) / 2;
        var right = new Point2D(maxX, middle);
        var left = new Point2D(minX, middle);

        // Right to left under the middle, then back over the top: each half bows to the left of
        // its travel, which on this loop is clockwise, so the walls face out.
        var half = WallEllipse.Half(height / width, toLeft: true);
        return new[] { new WallPiece(right, left, 0, half), new WallPiece(left, right, 0, half) };
    }

    /// <summary>
    /// Half an ellipse with one axis on the line between two points, its other semi-axis set so
    /// it passes through a third - the elliptical counterpart of an arc through three points.
    /// Null when the third point is on that line, or the first two coincide.
    /// </summary>
    public static WallEllipse? PartialEllipse(Point2D start, Point2D end, Point2D through)
    {
        var chord = end - start;
        var semi = chord.Length / 2;
        if (semi < MinimumPiece / 2) return null;

        var direction = chord / chord.Length;
        var fromMiddle = through - start.MidpointTo(end);
        var along = fromMiddle.Dot(direction);
        var left = fromMiddle.Dot(direction.PerpendicularLeft());
        if (Math.Abs(left) < MinimumPiece) return null;

        // On the ellipse, (along/a)² + (left/b)² = 1. Beyond the ends of the axis no ellipse
        // passes through, so the point then just says how far it bows.
        var share = along / semi;
        var other = Math.Abs(share) < 0.99 ? Math.Abs(left) / Math.Sqrt(1 - share * share) : Math.Abs(left);

        return WallEllipse.Half(other / semi, toLeft: left > 0);
    }

    /// <summary>
    /// The shape moved out by an offset - or in, for a negative one - so that its walls are
    /// built that far from the clicked outline, toward their exterior. Straight sides meet
    /// again at new corners; arcs grow or shrink about their own centres, and still meet the
    /// sides they were tangent to.
    /// </summary>
    public static IReadOnlyList<WallPiece> Offset(IReadOnlyList<WallPiece> pieces, double offset)
    {
        if (offset == 0 || pieces.Count == 0) return pieces;

        var curves = pieces.Select(p => p.Curve).ToList();

        // An elliptical piece grows or shrinks as a whole, keeping its ends at its own axes.
        var grown = curves.Select(c => c.IsElliptical || c.IsSpline ? c.Offset(offset) : null).ToArray();

        // Left of travel is outside, so the offset curve is to the left.
        var starts = curves.Select((c, i) => grown[i]?.Start ?? c.At(0, offset)).ToArray();
        var ends = curves.Select((c, i) => grown[i]?.End ?? c.At(c.Length, offset)).ToArray();

        for (var i = 0; i < curves.Count; i++)
        {
            var next = (i + 1) % curves.Count;
            if (curves[i].IsCurved || curves[next].IsCurved) continue;

            var here = Line2D.Through(starts[i], ends[i]);
            var there = Line2D.Through(starts[next], ends[next]);
            if (!Line2D.TryIntersect(here, there, out var corner)) continue;

            ends[i] = corner;
            starts[next] = corner;
        }

        return pieces.Select((piece, i) => new WallPiece(starts[i], ends[i], piece.Bulge, grown[i]?.Ellipse ?? piece.Ellipse, grown[i] is { } g ? g.Spline : piece.Spline)).ToList();
    }

    /// <summary>
    /// Walls along a smooth curve through clicked points (specification section 3.1, "spline
    /// walls"). Open, it is one wall from the first point to the last, passing all the others.
    ///
    /// Closed, it comes back round to the first point without a corner, as two walls - a wall
    /// has two ends, and a loop has none. Both are stretches of one spline taken on round the
    /// loop a little past its start, so each point, the two joins included, has neighbours on
    /// both sides and the curve runs smoothly through all of them. Like every other shape it
    /// runs clockwise, so the walls face out.
    /// </summary>
    public static IReadOnlyList<WallPiece> Spline(IReadOnlyList<Point2D> points, bool closed)
    {
        // Double clicks, and a stroke that dwelt in one place, leave points on top of each other.
        var distinct = new List<Point2D>();
        foreach (var point in points)
            if (distinct.Count == 0 || distinct[^1].DistanceTo(point) >= MinimumPiece) distinct.Add(point);

        if (closed && distinct.Count > 1 && distinct[^1].DistanceTo(distinct[0]) < MinimumPiece) distinct.RemoveAt(distinct.Count - 1);

        if (!closed || distinct.Count < 3)
        {
            if (distinct.Count < 2) return Array.Empty<WallPiece>();
            if (distinct.Count == 2) return new[] { new WallPiece(distinct[0], distinct[1], 0) };

            var open = WallSpline.Between(distinct[0], distinct[^1], distinct.Skip(1).SkipLast(1));
            return open is { IsValid: true }
                ? new[] { new WallPiece(distinct[0], distinct[^1], 0, null, open) }
                : new[] { new WallPiece(distinct[0], distinct[^1], 0) };
        }

        if (Polygon2D.SignedArea(distinct) > 0)
        {
            // Anticlockwise: turned round, from the same first point.
            distinct.Reverse(1, distinct.Count - 1);
        }

        // P0 ... Pn-1, then round again through P0, P1 and P2, so the loop from P1 back to P1
        // is inside the spline with a point either side of both its ends.
        var n = distinct.Count;
        var knots = distinct.Concat(new[] { distinct[0], distinct[1], distinct[2] }).ToList();
        if (WallSpline.Between(knots[0], knots[^1], knots.Skip(1).SkipLast(1)) is not { } whole) return Array.Empty<WallPiece>();

        var half = Math.Max(1, n / 2);
        var middle = distinct[(1 + half) % n];
        return new[]
        {
            new WallPiece(distinct[1], middle, 0, null, whole.Part(1, 1 + half)),
            new WallPiece(middle, distinct[1], 0, null, whole.Part(1 + half, n + 1))
        };
    }

    /// <summary>
    /// The points of a stroke drawn freehand that its shape depends on: every point further
    /// than the tolerance from the line between those kept either side of it, and none of the
    /// wobble in between (the Douglas-Peucker method). A spline through them is the freeform
    /// wall the stroke meant.
    /// </summary>
    public static IReadOnlyList<Point2D> Simplify(IReadOnlyList<Point2D> stroke, double tolerance)
    {
        if (stroke.Count <= 2) return stroke.ToList();

        var keep = new bool[stroke.Count];
        keep[0] = keep[^1] = true;

        var spans = new Stack<(int From, int To)>();
        spans.Push((0, stroke.Count - 1));
        while (spans.Count > 0)
        {
            var (from, to) = spans.Pop();
            var (furthest, distance) = (-1, 0.0);
            for (var i = from + 1; i < to; i++)
            {
                var d = Line2D.DistanceFromSegment(stroke[i], stroke[from], stroke[to]);
                if (d > distance) (furthest, distance) = (i, d);
            }

            if (furthest < 0 || distance <= tolerance) continue;

            keep[furthest] = true;
            spans.Push((from, furthest));
            spans.Push((furthest, to));
        }

        return stroke.Where((_, i) => keep[i]).ToList();
    }

    /// <summary>The walls of a shape, as they will be built.</summary>
    public static IReadOnlyList<Wall> Walls(
        IReadOnlyList<WallPiece> pieces, Guid typeId, Guid levelId, WallLocationLine locationLine, bool flipped) =>
        pieces
            .Where(piece => piece.Start.DistanceTo(piece.End) >= MinimumPiece)
            .Select(piece => new Wall
            {
                Start = piece.Start,
                End = piece.End,
                Bulge = piece.Bulge,
                Ellipse = piece.Ellipse,
                Spline = piece.Spline,
                TypeId = typeId,
                LevelId = levelId,
                LocationLine = locationLine,
                Flipped = flipped
            })
            .ToList();

    private static IReadOnlyList<WallPiece> Loop(params Point2D[] corners) =>
        corners.Select((corner, i) => new WallPiece(corner, corners[(i + 1) % corners.Length], 0)).ToList();
}
