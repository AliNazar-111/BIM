namespace BIMDesigner.Core.Geometry;

/// <summary>
/// The line a wall is drawn along: straight, or an arc of a circle (specification section 3.1,
/// "straight, arc and curved walls").
///
/// An arc is given by its two ends and a <em>bulge</em>, the tangent of a quarter of the angle
/// it turns through - the convention CAD has used for decades. Zero is straight, positive
/// turns left (anticlockwise) from start to end, and 1 is a half circle. Keeping the ends as
/// the defining points means everything that works on wall ends - joins, snapping, grips,
/// trimming - works on curved walls unchanged, and moving or mirroring a wall never has to
/// reconstruct its centre.
///
/// Positions along the curve are distances along it from the start. Positions across it are
/// offsets to the left of the direction of travel.
/// </summary>
public sealed class WallCurve
{
    /// <summary>Below this a bulge is a straight line.</summary>
    public const double StraightBulge = 1e-9;

    /// <summary>The most a tessellated arc may stray from the true one, in millimetres.</summary>
    public const double ChordTolerance = 0.5;

    /// <summary>The largest angle one straight piece of a drawn arc may turn through.</summary>
    private const double MaxStepRadians = 5 * Math.PI / 180;

    private WallCurve(Point2D start, Point2D end, double bulge)
    {
        Start = start;
        End = end;
        Bulge = Math.Abs(bulge) < StraightBulge || start.DistanceTo(end) < 1e-9 ? 0 : bulge;

        var chord = end - start;
        var chordLength = chord.Length;

        if (!IsArc)
        {
            Length = chordLength;
            return;
        }

        // Sweep is four times the angle whose tangent is the bulge; the radius follows from
        // the chord, and the centre sits on the chord's perpendicular bisector.
        Sweep = 4 * Math.Atan(Bulge);
        Radius = chordLength / (2 * Math.Abs(Math.Sin(Sweep / 2)));

        var midpoint = start.MidpointTo(end);
        var towardCentre = chord.NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var distance = Radius * Math.Cos(Sweep / 2);

        // For an anticlockwise arc the centre is to the left of the chord.
        Centre = midpoint + towardCentre * (Math.Sign(Sweep) * distance);
        StartAngle = Math.Atan2(start.Y - Centre.Y, start.X - Centre.X);
        Length = Radius * Math.Abs(Sweep);
    }

    public static WallCurve Of(Point2D start, Point2D end, double bulge) => new(start, end, bulge);

    /// <summary>The bulge of the arc from start to end that passes through a third point.</summary>
    public static double BulgeThrough(Point2D start, Point2D end, Point2D through)
    {
        var chord = end - start;
        if (chord.Length < 1e-9) return 0;

        // The angle at the third point is half the arc's remaining angle, which gives the
        // sweep directly: the inscribed angle theorem.
        var a = start - through;
        var b = end - through;
        var cross = a.Cross(b);
        if (Math.Abs(cross) < 1e-9 * a.Length * b.Length) return 0;

        var inscribed = Math.Atan2(Math.Abs(cross), a.Dot(b));
        var sweep = 2 * (Math.PI - inscribed);

        // Which way it turns: the third point on the right of the chord means anticlockwise.
        var side = chord.Cross(through - start);
        return Math.Tan(sweep / 4) * (side < 0 ? 1 : -1);
    }

    public Point2D Start { get; }

    public Point2D End { get; }

    public double Bulge { get; }

    public bool IsArc => Bulge != 0;

    /// <summary>Length along the curve, in millimetres.</summary>
    public double Length { get; }

    public Point2D Centre { get; }

    public double Radius { get; }

    /// <summary>The angle turned through from start to end, in radians. Positive is anticlockwise.</summary>
    public double Sweep { get; }

    public double StartAngle { get; }

    /// <summary>The point this far along the curve.</summary>
    public Point2D PointAt(double along) => At(along, 0);

    /// <summary>The point this far along, and this far to the left of the direction of travel.</summary>
    public Point2D At(double along, double left)
    {
        if (!IsArc)
        {
            var direction = (End - Start).NormalisedOrDefault(Vector2D.UnitX);
            return Start + direction * along + direction.PerpendicularLeft() * left;
        }

        var angle = AngleAt(along);
        var radial = new Vector2D(Math.Cos(angle), Math.Sin(angle));

        // Left of travel points toward the centre on an anticlockwise arc and away from it on
        // a clockwise one.
        return Centre + radial * (Radius - Math.Sign(Sweep) * left);
    }

    /// <summary>The direction of travel at this distance along.</summary>
    public Vector2D TangentAt(double along)
    {
        if (!IsArc) return (End - Start).NormalisedOrDefault(Vector2D.UnitX);

        var angle = AngleAt(along);
        var tangent = new Vector2D(-Math.Sin(angle), Math.Cos(angle));
        return Sweep > 0 ? tangent : -tangent;
    }

    /// <summary>Unit vector to the left of the direction of travel at this distance along.</summary>
    public Vector2D LeftAt(double along) => TangentAt(along).PerpendicularLeft();

    /// <summary>
    /// Where a point is relative to the curve: how far along its foot is, and how far to the
    /// left it lies. Along is not clamped, so points past either end come out below zero or
    /// beyond the length.
    /// </summary>
    public (double Along, double Left) Locate(Point2D point)
    {
        if (!IsArc)
        {
            var direction = (End - Start).NormalisedOrDefault(Vector2D.UnitX);
            var offset = point - Start;
            return (offset.Dot(direction), offset.Dot(direction.PerpendicularLeft()));
        }

        var fromCentre = point - Centre;
        var angle = Math.Atan2(fromCentre.Y, fromCentre.X);

        // Measured from the start in the direction of the sweep, and brought as close as it
        // can be to the arc itself so points just past either end are not a full turn away.
        var turned = Normalise((angle - StartAngle) * Math.Sign(Sweep));
        if (turned > Math.Abs(Sweep) / 2 + Math.PI) turned -= 2 * Math.PI;

        return (turned * Radius, (Radius - fromCentre.Length) * Math.Sign(Sweep));
    }

    /// <summary>Distance from a point to the curve itself, between its ends.</summary>
    public double DistanceTo(Point2D point)
    {
        if (!IsArc) return Line2D.DistanceFromSegment(point, Start, End);

        var (along, _) = Locate(point);
        return along < 0 || along > Length
            ? Math.Min(point.DistanceTo(Start), point.DistanceTo(End))
            : PointAt(along).DistanceTo(point);
    }

    /// <summary>
    /// Where a line crosses the curve offset this far to the left, taking the crossing nearest
    /// to a given point when a line cuts a circle twice. Null when they do not meet.
    /// </summary>
    public Point2D? Intersect(Line2D line, double left, Point2D near)
    {
        if (!IsArc)
        {
            var edge = new Line2D(At(0, left), TangentAt(0));
            return Line2D.TryIntersect(edge, line, out var point) ? point : null;
        }

        var radius = Radius - Math.Sign(Sweep) * left;
        if (radius <= 0) return null;

        // Solve |origin + t d - centre| = radius for t.
        var d = line.Direction.NormalisedOrDefault(Vector2D.UnitX);
        var m = line.Origin - Centre;
        var b = m.Dot(d);
        var c = m.Dot(m) - radius * radius;
        var discriminant = b * b - c;
        if (discriminant < 0) return null;

        var root = Math.Sqrt(discriminant);
        var first = line.Origin + d * (-b - root);
        var second = line.Origin + d * (-b + root);

        return first.DistanceTo(near) <= second.DistanceTo(near) ? first : second;
    }

    /// <summary>
    /// Where this curve and another cross, between their ends: line with line, line with arc,
    /// or arc with arc.
    /// </summary>
    public IReadOnlyList<Point2D> Crossings(WallCurve other)
    {
        const double slack = 1e-6;

        bool OnBoth(Point2D point)
        {
            var (a, left) = Locate(point);
            var (b, otherLeft) = other.Locate(point);
            return Math.Abs(left) < 1e-3 && Math.Abs(otherLeft) < 1e-3 &&
                   a >= -slack && a <= Length + slack && b >= -slack && b <= other.Length + slack;
        }

        var candidates = new List<Point2D>();

        if (!IsArc && !other.IsArc)
        {
            if (Line2D.TryIntersect(Line2D.Through(Start, End), Line2D.Through(other.Start, other.End), out var point))
                candidates.Add(point);
        }
        else if (IsArc && other.IsArc)
        {
            // Two circles: the chord through their crossings is perpendicular to the line of
            // centres, at a distance found from the two radii.
            var between = other.Centre - Centre;
            var d = between.Length;
            if (d > 1e-9 && d <= Radius + other.Radius && d >= Math.Abs(Radius - other.Radius))
            {
                var a = (Radius * Radius - other.Radius * other.Radius + d * d) / (2 * d);
                var h = Math.Sqrt(Math.Max(0, Radius * Radius - a * a));
                var unit = between * (1 / d);
                var foot = Centre + unit * a;
                candidates.Add(foot + unit.PerpendicularLeft() * h);
                candidates.Add(foot - unit.PerpendicularLeft() * h);
            }
        }
        else
        {
            var (line, arc) = IsArc ? (other, this) : (this, other);
            var d = (line.End - line.Start).NormalisedOrDefault(Vector2D.UnitX);
            var m = line.Start - arc.Centre;
            var b = m.Dot(d);
            var c = m.Dot(m) - arc.Radius * arc.Radius;
            var discriminant = b * b - c;

            if (discriminant >= 0)
            {
                var root = Math.Sqrt(discriminant);
                candidates.Add(line.Start + d * (-b - root));
                candidates.Add(line.Start + d * (-b + root));
            }
        }

        return candidates.Where(OnBoth).ToList();
    }

    /// <summary>
    /// Positions strictly between two distances along, close enough together that straight
    /// pieces between them follow the arc to within <see cref="ChordTolerance"/>. None for a
    /// straight line, which needs no points in between.
    /// </summary>
    public IEnumerable<double> Between(double from, double to, double left = 0)
    {
        if (!IsArc || Math.Abs(to - from) < 1e-9) yield break;

        var radius = Math.Max(Radius - Math.Sign(Sweep) * left, 1);
        var toleranceStep = 2 * Math.Acos(Math.Clamp(1 - ChordTolerance / radius, -1, 1));
        var step = Math.Min(MaxStepRadians, Math.Max(toleranceStep, 1e-3));

        var turned = Math.Abs(to - from) / Radius;
        var pieces = (int)Math.Ceiling(turned / step);

        for (var i = 1; i < pieces; i++)
            yield return from + (to - from) * i / pieces;
    }

    /// <summary>The curve as a series of points, ends included.</summary>
    public IReadOnlyList<Point2D> Points(double left = 0)
    {
        var points = new List<Point2D> { At(0, left) };
        points.AddRange(Between(0, Length, left).Select(along => At(along, left)));
        points.Add(At(Length, left));
        return points;
    }

    /// <summary>The same curve moved sideways by this much, as a new curve with the same bulge.</summary>
    public WallCurve Offset(double left) => new(At(0, left), At(Length, left), Bulge);

    /// <summary>The part of the curve between two distances along it.</summary>
    public WallCurve Part(double from, double to)
    {
        if (!IsArc) return new WallCurve(PointAt(from), PointAt(to), 0);

        var sweep = (to - from) / Radius * Math.Sign(Sweep);
        return new WallCurve(PointAt(from), PointAt(to), Math.Tan(sweep / 4));
    }

    private double AngleAt(double along) => StartAngle + Math.Sign(Sweep) * along / Radius;

    private static double Normalise(double angle)
    {
        angle %= 2 * Math.PI;
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }
}
