using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>What a shape of walls is measured to: the walls' centres, or the faces inside or outside the room.</summary>
public enum RectangleMeasure
{
    WallCentres,
    InsideFaces,
    OutsideFaces
}

/// <summary>
/// Walls joined end to end all the way round a room, to be put into an exact shape.
///
/// Walls drawn by eye come out a few millimetres or a degree off: 21.50 m on one side, 20.70
/// m opposite, one corner not quite square. A shape takes the walls, finds how they run round
/// the room, and puts their corners where the true shape's are - the same middle, turned the
/// way the walls mostly run (square to the page when they nearly are), the size asked for -
/// measured between the walls' centres, or between the faces inside the room or outside it,
/// whatever each wall's thickness and location line. Walls not among them that end at one of
/// the corners go with the corner.
/// </summary>
public abstract class WallLoopShape
{
    private readonly BimDocument _document;

    protected WallLoopShape(BimDocument document, List<Point2D> corners, List<(Wall Wall, bool Forward)> sides)
    {
        _document = document;
        Corners = corners;
        Sides = sides;
    }

    /// <summary>The corners of the walls' location lines, anticlockwise round the room.</summary>
    public IReadOnlyList<Point2D> Corners { get; }

    /// <summary>
    /// The walls in order round the room: side i runs from corner i to corner i + 1, and
    /// Forward says whether the wall was drawn that way.
    /// </summary>
    public IReadOnlyList<(Wall Wall, bool Forward)> Sides { get; }

    /// <summary>How many walls go round the room.</summary>
    public int Count => Sides.Count;

    /// <summary>
    /// The walls as a room - straight, on one level, each meeting the next end to end all the
    /// way round - with their corners anticlockwise; or false, with the reason.
    /// </summary>
    protected static bool TryLoop(
        IReadOnlyList<Wall> walls, out List<Point2D> corners, out List<(Wall Wall, bool Forward)> sides, out string? problem)
    {
        corners = new List<Point2D>();
        sides = new List<(Wall, bool)>();
        problem = null;

        if (walls.Any(wall => wall.IsCurved))
        {
            problem = "Only straight walls can be put into a shape. Straighten the curved one first.";
            return false;
        }

        if (walls.Select(wall => wall.LevelId).Distinct().Count() > 1)
        {
            problem = "The walls must all be on one level.";
            return false;
        }

        var tolerance = WallJoins.JoinTolerance;
        corners.Add(walls[0].Start);
        corners.Add(walls[0].End);
        sides.Add((walls[0], true));
        var remaining = walls.Skip(1).ToList();

        while (remaining.Count > 0)
        {
            var at = corners[^1];
            var next = remaining.FirstOrDefault(wall => wall.Start.DistanceTo(at) <= tolerance || wall.End.DistanceTo(at) <= tolerance);
            if (next is null)
            {
                problem = "The walls do not meet end to end all the way round. Join their corners first, then give them a shape.";
                return false;
            }

            var forward = next.Start.DistanceTo(at) <= tolerance;
            sides.Add((next, forward));
            corners.Add(forward ? next.End : next.Start);
            remaining.Remove(next);
        }

        if (corners[^1].DistanceTo(corners[0]) > tolerance)
        {
            problem = "The walls do not close round a room. Join their corners first, then give them a shape.";
            return false;
        }

        corners.RemoveAt(corners.Count - 1);

        // Anticlockwise, so the room is always to the left of each side: the same corners the
        // other way round, and each wall run the other way.
        if (Polygon2D.SignedArea(corners) < 0)
        {
            var (points, runs) = (corners, sides);
            var count = points.Count;
            corners = Enumerable.Range(0, count).Select(i => points[(count - i) % count]).ToList();
            sides = Enumerable.Range(0, count).Select(i => runs[count - 1 - i]).Select(side => (side.Wall, !side.Forward)).ToList();
        }

        return true;
    }

    /// <summary>Square to a side, into the room.</summary>
    protected Vector2D Inward(int side) =>
        (Corners[(side + 1) % Count] - Corners[side]).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();

    /// <summary>
    /// How far into the room from a wall's location line the line it is measured to lies: its
    /// centre, or the face inside or outside the room.
    /// </summary>
    protected double Inset(int side, RectangleMeasure measure)
    {
        var wall = Sides[side].Wall;
        if (_document.GetWallType(wall) is not { } type) return 0;

        var half = type.Structure.TotalWidth / 2;
        var centre = (wall.PointAt(type.Structure, 0, 0) - wall.Start).Dot(Inward(side));

        return measure switch
        {
            RectangleMeasure.InsideFaces => centre + half,
            RectangleMeasure.OutsideFaces => centre - half,
            _ => centre
        };
    }

    /// <summary>The corners of the lines the walls are measured to, in the same order as <see cref="Corners"/>.</summary>
    protected List<Point2D> MeasuredCorners(RectangleMeasure measure)
    {
        var points = new List<Point2D>();

        for (var i = 0; i < Count; i++)
        {
            var before = (i + Count - 1) % Count;
            var a0 = Corners[before] + Inward(before) * Inset(before, measure);
            var a1 = Corners[i] + Inward(before) * Inset(before, measure);
            var b0 = Corners[i] + Inward(i) * Inset(i, measure);
            var b1 = Corners[(i + 1) % Count] + Inward(i) * Inset(i, measure);

            points.Add(Crossing(a0, a1, b0, b1) ?? b0);
        }

        return points;
    }

    /// <summary>The middle of the room as it is now, measured as asked.</summary>
    protected Point2D Middle(RectangleMeasure measure)
    {
        var corners = MeasuredCorners(measure);
        return new Point2D(corners.Average(point => point.X), corners.Average(point => point.Y));
    }

    private static Point2D? Crossing(Point2D a0, Point2D a1, Point2D b0, Point2D b1)
    {
        var a = a1 - a0;
        var b = b1 - b0;
        var denominator = a.Cross(b);
        if (Math.Abs(denominator) <= a.Length * b.Length * 1e-9) return null;

        return a0 + a * ((b0 - a0).Cross(b) / denominator);
    }

    /// <summary>
    /// Moves every wall to its new corners - and the walls ending at a corner with it - as one
    /// step to undo. Null, with the reason, when a wall would be left with no length.
    /// </summary>
    protected IUndoableCommand? MoveTo(IReadOnlyList<Point2D> corners, string name, out string? problem)
    {
        problem = null;

        for (var i = 0; i < Count; i++)
        {
            if (corners[i].DistanceTo(corners[(i + 1) % Count]) <= WallJoins.JoinTolerance)
            {
                problem = "That is too small: the walls' own thickness takes up all of it.";
                return null;
            }
        }

        var commands = new List<IUndoableCommand>();

        for (var i = 0; i < Count; i++)
        {
            var (wall, forward) = Sides[i];
            var (from, to) = (corners[i], corners[(i + 1) % Count]);
            commands.Add(new MoveWallCommand(wall, wall.Start, wall.End, forward ? from : to, forward ? to : from, name));
        }

        // Walls ending at a corner - a partition run up to it, say - go with the corner.
        var own = Sides.Select(side => side.Wall).ToHashSet();
        for (var i = 0; i < Count; i++)
        {
            foreach (var (other, atStart) in WallCorners.At(_document, Sides[0].Wall.LevelId, Corners[i]))
            {
                if (own.Contains(other)) continue;

                var (start, end) = atStart ? (corners[i], other.End) : (other.Start, corners[i]);
                if (start.DistanceTo(end) <= WallJoins.JoinTolerance) continue;

                commands.Add(new MoveWallCommand(other, other.Start, other.End, start, end, name));
            }
        }

        return new CompositeCommand(name, commands);
    }

    /// <summary>
    /// How far an angle is from square to the page: from the nearest of east, north, west or
    /// south.
    /// </summary>
    protected static double OffSquare(double angle)
    {
        const double quarter = Math.PI / 2;
        return angle - Math.Round(angle / quarter) * quarter;
    }

    /// <summary>Within this of square to the page, a room drawn by eye was meant to be square to it.</summary>
    protected const double SquareToPage = 10 * Math.PI / 180;
}

/// <summary>
/// Four walls round a room squared into an exact rectangle - or a square - of a given width
/// and depth. The width runs whichever way is most nearly across the page.
/// </summary>
public sealed class WallRectangle : WallLoopShape
{
    private WallRectangle(BimDocument document, List<Point2D> corners, List<(Wall Wall, bool Forward)> sides)
        : base(document, corners, sides)
    {
        (Direction, Across) = Axes();
    }

    /// <summary>The way the rectangle's width runs.</summary>
    public Vector2D Direction { get; }

    /// <summary>The way its depth runs, square to the width.</summary>
    public Vector2D Across { get; }

    /// <summary>The four walls as a room, or null with the reason when they are not one.</summary>
    public static WallRectangle? Find(BimDocument document, IReadOnlyList<Wall> walls, out string? problem)
    {
        if (walls.Count != 4)
        {
            problem = "Select the four walls of a room - just those four - to make them a rectangle or a square.";
            return null;
        }

        if (!TryLoop(walls, out var corners, out var sides, out problem)) return null;

        var rectangle = new WallRectangle(document, corners, sides);

        // Each side has to run the other way from the one before: a quadrilateral with two
        // sides next to each other running the same way is too far from a rectangle to guess.
        for (var i = 0; i < 4; i++)
        {
            if (rectangle.RunsAlong(i) == rectangle.RunsAlong((i + 1) % 4))
            {
                problem = "The walls are too far from a rectangle to square up: two of them next to each other run the same way.";
                return null;
            }
        }

        return rectangle;
    }

    /// <summary>
    /// The way the rectangle is turned: the walls' own directions, each taken round to the
    /// nearest quarter turn from the first and averaged - and square to the page when that is
    /// nearly so, which is what a room drawn by eye nearly always meant.
    /// </summary>
    private (Vector2D Direction, Vector2D Across) Axes()
    {
        const double quarter = Math.PI / 2;

        double AngleOf(int side)
        {
            var run = Corners[(side + 1) % 4] - Corners[side];
            return Math.Atan2(run.Y, run.X);
        }

        var reference = AngleOf(0);
        var turn = Enumerable.Range(0, 4)
            .Select(side => AngleOf(side) - reference)
            .Select(delta => delta - Math.Round(delta / quarter) * quarter)
            .Average();

        var angle = reference + turn;
        if (Math.Abs(OffSquare(angle)) < SquareToPage) angle -= OffSquare(angle);

        // The width is whichever way runs most nearly across the page, left to right, so it
        // means the same thing whichever wall was drawn first.
        angle -= Math.Round(angle / quarter) * quarter;
        var direction = new Vector2D(Math.Cos(angle), Math.Sin(angle));
        return (direction, direction.PerpendicularLeft());
    }

    /// <summary>Whether a side runs along the width (true) or the depth (false).</summary>
    private bool RunsAlong(int side)
    {
        var run = (Corners[(side + 1) % 4] - Corners[side]).NormalisedOrDefault(Vector2D.UnitX);
        return Math.Abs(run.Dot(Direction)) >= Math.Abs(run.Dot(Across));
    }

    /// <summary>
    /// The size the walls come to now, measured as asked: the width the average of the two
    /// sides running along it, the depth of the two running across.
    /// </summary>
    public (double Width, double Depth) Measure(RectangleMeasure measure)
    {
        var corners = MeasuredCorners(measure);
        var along = new List<double>();
        var across = new List<double>();

        for (var i = 0; i < 4; i++)
        {
            var length = corners[i].DistanceTo(corners[(i + 1) % 4]);
            (RunsAlong(i) ? along : across).Add(length);
        }

        return (along.DefaultIfEmpty(0).Average(), across.DefaultIfEmpty(0).Average());
    }

    /// <summary>
    /// Squares the walls into a rectangle of this width and depth, measured as asked, about
    /// the middle of the room as it is now - one step to undo. Null, with the reason, when
    /// the size leaves no room.
    /// </summary>
    public IUndoableCommand? Make(double width, double depth, RectangleMeasure measure, out string? problem)
    {
        if (width <= 0 || depth <= 0)
        {
            problem = "The width and depth need to be more than nothing.";
            return null;
        }

        var centre = Middle(measure);

        // Each side's location line: the edge of the rectangle it is measured to, moved out by
        // however far its location line is from what it is measured to.
        var outward = new Vector2D[4];
        var reach = new double[4];

        for (var i = 0; i < 4; i++)
        {
            var axis = RunsAlong(i) ? Across : Direction;
            outward[i] = (-Inward(i)).Dot(axis) >= 0 ? axis : -axis;
            reach[i] = (RunsAlong(i) ? depth : width) / 2 + Inset(i, measure);
        }

        var corners = Enumerable.Range(0, 4)
            .Select(i => centre + outward[(i + 3) % 4] * reach[(i + 3) % 4] + outward[i] * reach[i])
            .ToList();

        return MoveTo(corners, Math.Abs(width - depth) < 1e-6 ? "Make Square" : "Make Rectangle", out problem);
    }
}

/// <summary>
/// Walls round a room - three or more - made a regular polygon: every side the same length
/// and every corner the same angle. Three walls make an equilateral triangle, five a
/// pentagon, six a hexagon.
/// </summary>
public sealed class WallPolygon : WallLoopShape
{
    private WallPolygon(BimDocument document, List<Point2D> corners, List<(Wall Wall, bool Forward)> sides)
        : base(document, corners, sides)
    {
    }

    /// <summary>What the shape is called, by how many sides it has.</summary>
    public string Name => NameFor(Count);

    public static string NameFor(int sides) => sides switch
    {
        3 => "Triangle",
        4 => "Square",
        5 => "Pentagon",
        6 => "Hexagon",
        7 => "Heptagon",
        8 => "Octagon",
        _ => $"{sides}-sided polygon"
    };

    /// <summary>The walls as a room of three or more, or null with the reason when they are not one.</summary>
    public static WallPolygon? Find(BimDocument document, IReadOnlyList<Wall> walls, out string? problem)
    {
        if (walls.Count < 3)
        {
            problem = "Select the walls of a room - three or more, joined end to end all the way round - to make them a regular polygon.";
            return null;
        }

        return TryLoop(walls, out var corners, out var sides, out problem) ? new WallPolygon(document, corners, sides) : null;
    }

    /// <summary>How long a side is now, measured as asked - the average of them all.</summary>
    public double Measure(RectangleMeasure measure)
    {
        var corners = MeasuredCorners(measure);
        return Enumerable.Range(0, Count).Average(i => corners[i].DistanceTo(corners[(i + 1) % Count]));
    }

    /// <summary>
    /// Makes the walls a regular polygon with sides this long, measured as asked, about the
    /// middle of the room as it is now and turned as it mostly is - with a side square to the
    /// page if one nearly is. One step to undo.
    /// </summary>
    public IUndoableCommand? Make(double side, RectangleMeasure measure, out string? problem)
    {
        if (side <= 0)
        {
            problem = "The side needs to be more than nothing.";
            return null;
        }

        var count = Count;
        var step = 2 * Math.PI / count;
        var centre = Middle(measure);

        // Every side's outward direction now, taken back by its place round the room: averaged,
        // that is how the whole shape is turned.
        double Outward(int i)
        {
            var away = -Inward(i);
            return Math.Atan2(away.Y, away.X) - i * step;
        }

        var turn = Math.Atan2(
            Enumerable.Range(0, count).Sum(i => Math.Sin(Outward(i))),
            Enumerable.Range(0, count).Sum(i => Math.Cos(Outward(i))));

        // A side nearly square to the page is put square to it.
        var nearest = Enumerable.Range(0, count).Select(i => OffSquare(turn + i * step)).OrderBy(Math.Abs).First();
        if (Math.Abs(nearest) < SquareToPage) turn -= nearest;

        // The measured sides stand the apothem out from the middle; each wall's line stands out
        // from that by however far it is from what it is measured to.
        var apothem = side / (2 * Math.Tan(Math.PI / count));
        var normals = Enumerable.Range(0, count).Select(i => new Vector2D(Math.Cos(turn + i * step), Math.Sin(turn + i * step))).ToList();
        var reach = Enumerable.Range(0, count).Select(i => apothem + Inset(i, measure)).ToList();

        var corners = new List<Point2D>();
        for (var i = 0; i < count; i++)
        {
            var before = (i + count - 1) % count;
            var (a, b) = (normals[before], normals[i]);
            var determinant = a.X * b.Y - a.Y * b.X;

            corners.Add(centre + new Vector2D(
                (reach[before] * b.Y - a.Y * reach[i]) / determinant,
                (a.X * reach[i] - reach[before] * b.X) / determinant));
        }

        return MoveTo(corners, $"Make {Name}", out problem);
    }
}
