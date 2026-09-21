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

    /// <summary>A wall along an existing line - a gridline - picked with one click.</summary>
    Pick
}

/// <summary>One wall of a shape: where it runs, and how far it bows.</summary>
public readonly record struct WallPiece(Point2D Start, Point2D End, double Bulge);

/// <summary>
/// Closed shapes of walls: rectangles, regular polygons, circles and rounded ovals, each
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
    /// The shape moved out by an offset - or in, for a negative one - so that its walls are
    /// built that far from the clicked outline, toward their exterior. Straight sides meet
    /// again at new corners; arcs grow or shrink about their own centres, and still meet the
    /// sides they were tangent to.
    /// </summary>
    public static IReadOnlyList<WallPiece> Offset(IReadOnlyList<WallPiece> pieces, double offset)
    {
        if (offset == 0 || pieces.Count == 0) return pieces;

        var curves = pieces.Select(p => WallCurve.Of(p.Start, p.End, p.Bulge)).ToList();

        // Left of travel is outside, so the offset curve is to the left.
        var starts = curves.Select(c => c.At(0, offset)).ToArray();
        var ends = curves.Select(c => c.At(c.Length, offset)).ToArray();

        for (var i = 0; i < curves.Count; i++)
        {
            var next = (i + 1) % curves.Count;
            if (curves[i].IsArc || curves[next].IsArc) continue;

            var here = Line2D.Through(starts[i], ends[i]);
            var there = Line2D.Through(starts[next], ends[next]);
            if (!Line2D.TryIntersect(here, there, out var corner)) continue;

            ends[i] = corner;
            starts[next] = corner;
        }

        return pieces.Select((piece, i) => new WallPiece(starts[i], ends[i], piece.Bulge)).ToList();
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
                TypeId = typeId,
                LevelId = levelId,
                LocationLine = locationLine,
                Flipped = flipped
            })
            .ToList();

    private static IReadOnlyList<WallPiece> Loop(params Point2D[] corners) =>
        corners.Select((corner, i) => new WallPiece(corner, corners[(i + 1) % corners.Length], 0)).ToList();
}
