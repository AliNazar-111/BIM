namespace BIMDesigner.Core.Geometry;

/// <summary>
/// The shape of a spline wall: a smooth curve through points clicked between the wall's two
/// ends (specification section 3.1, "spline and freeform walls").
///
/// Like <see cref="WallEllipse"/> it is held relative to the wall's ends rather than in model
/// space. The points are in a frame where the whole spline runs from (0, 0) to (1, 0), and the
/// wall's ends place, turn and scale that frame. So moving, rotating or scaling a wall never
/// has to touch this, and mirroring it just negates the points' second coordinate.
///
/// The curve through the points is a Hermite spline whose tangent at each point follows the
/// circle through it and its two neighbours, and whose tangent at each end is turned so the end
/// piece bends as a circle would. Points on a circle give that circle, however they are spaced.
/// Tangents are scaled by the piece they start or finish, so unevenly spaced points do not
/// make the curve overshoot. Every step of that is unchanged by moving, turning or scaling
/// the points, which is what lets the frame be relative.
///
/// A wall made by splitting a spline wall is a part of the same spline: <see cref="From"/> and
/// <see cref="To"/> say which part, as a parameter from 0 at the first end to
/// <see cref="Segments"/> at the last, each whole number being one of the points.
/// </summary>
public sealed class WallSpline : IEquatable<WallSpline>
{
    private readonly Point2D[] _through;
    private readonly Point2D[] _knots;
    private readonly Vector2D[] _tangents;

    public WallSpline(IEnumerable<Point2D> through, double? from = null, double? to = null)
    {
        _through = through.ToArray();
        From = from ?? 0;
        To = to ?? Segments;

        var knots = new List<Point2D>(_through.Length + 2) { new(0, 0) };
        knots.AddRange(_through);
        knots.Add(new Point2D(1, 0));
        _knots = knots.ToArray();
        _tangents = Tangents(_knots);
    }

    /// <summary>The points between the ends, in the spline's own frame.</summary>
    public IReadOnlyList<Point2D> Through => _through;

    /// <summary>Where on the spline this wall starts: 0 at its first end.</summary>
    public double From { get; }

    /// <summary>Where on the spline this wall ends: <see cref="Segments"/> at its last end.</summary>
    public double To { get; }

    /// <summary>How many pieces the spline has between its points and ends.</summary>
    public int Segments => _through.Length + 1;

    /// <summary>Whether this describes a real curve: at least one point between the ends, all finite, and a stretch of it to follow.</summary>
    public bool IsValid =>
        _through.Length >= 1 &&
        _through.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)) &&
        double.IsFinite(From) && double.IsFinite(To) &&
        From >= -1e-9 && To <= Segments + 1e-9 && To - From > 1e-6 &&
        LocalAt(From).DistanceTo(LocalAt(To)) > 1e-9;

    /// <summary>
    /// A spline from one end to another through points given in model space, as a wall
    /// between those ends holds it.
    /// </summary>
    public static WallSpline? Between(Point2D start, Point2D end, IEnumerable<Point2D> through)
    {
        var chord = end - start;
        if (chord.Length < 1e-9) return null;

        var local = through.Select(point => Divide(point - start, chord)).Select(v => new Point2D(v.X, v.Y)).ToList();
        return local.Count == 0 ? null : new WallSpline(local);
    }

    /// <summary>
    /// The same spline with one of its points moved to a place on the plan, for a wall running
    /// between these ends along the whole of it.
    /// </summary>
    public WallSpline WithPointAt(int index, Point2D point, Point2D start, Point2D end)
    {
        var local = Divide(point - start, end - start);
        var through = _through.ToArray();
        through[index] = new Point2D(local.X, local.Y);
        return new WallSpline(through);
    }

    /// <summary>The same curve seen in a mirror.</summary>
    public WallSpline Mirrored() => new(_through.Select(p => new Point2D(p.X, -p.Y)), From, To);

    /// <summary>A stretch of the same spline, by parameter.</summary>
    public WallSpline Part(double from, double to) => new(_through, from, to);

    /// <summary>The whole spline's knots in its own frame: the first end, the points, the last end.</summary>
    public IReadOnlyList<Point2D> Knots() => _knots;

    /// <summary>The point at a parameter, in the spline's frame. Past either end it runs on straight along the end's tangent.</summary>
    public Point2D LocalAt(double s)
    {
        var knots = _knots;
        if (s < 0) return knots[0] + _tangents[0] * (Length(knots, 0) * s);
        if (s > Segments) return knots[^1] + _tangents[^1] * (Length(knots, Segments - 1) * (s - Segments));

        var (i, u) = Piece(s);
        var (p0, p1, m0, m1) = Hermite(knots, i);
        double u2 = u * u, u3 = u2 * u;
        var h00 = 2 * u3 - 3 * u2 + 1;
        var h10 = u3 - 2 * u2 + u;
        var h01 = -2 * u3 + 3 * u2;
        var h11 = u3 - u2;
        return new Point2D(
            h00 * p0.X + h10 * m0.X + h01 * p1.X + h11 * m1.X,
            h00 * p0.Y + h10 * m0.Y + h01 * p1.Y + h11 * m1.Y);
    }

    /// <summary>The rate of change of <see cref="LocalAt"/> with the parameter.</summary>
    public Vector2D LocalDerivative(double s)
    {
        var knots = _knots;
        if (s < 0) return _tangents[0] * Length(knots, 0);
        if (s > Segments) return _tangents[^1] * Length(knots, Segments - 1);

        var (i, u) = Piece(s);
        var (p0, p1, m0, m1) = Hermite(knots, i);
        var u2 = u * u;
        var d00 = 6 * u2 - 6 * u;
        var d10 = 3 * u2 - 4 * u + 1;
        var d01 = -6 * u2 + 6 * u;
        var d11 = 3 * u2 - 2 * u;
        return new Vector2D(
            d00 * p0.X + d10 * m0.X + d01 * p1.X + d11 * m1.X,
            d00 * p0.Y + d10 * m0.Y + d01 * p1.Y + d11 * m1.Y);
    }

    /// <summary>The rate of change of <see cref="LocalDerivative"/>: none past the ends, where the curve runs straight.</summary>
    public Vector2D LocalSecondDerivative(double s)
    {
        if (s < 0 || s > Segments) return new Vector2D(0, 0);

        var knots = _knots;
        var (i, u) = Piece(s);
        var (p0, p1, m0, m1) = Hermite(knots, i);
        var e00 = 12 * u - 6;
        var e10 = 6 * u - 4;
        var e01 = -12 * u + 6;
        var e11 = 6 * u - 2;
        return new Vector2D(
            e00 * p0.X + e10 * m0.X + e01 * p1.X + e11 * m1.X,
            e00 * p0.Y + e10 * m0.Y + e01 * p1.Y + e11 * m1.Y);
    }

    /// <summary>Which piece a parameter falls in, and how far through it.</summary>
    private (int Index, double U) Piece(double s)
    {
        var i = Math.Clamp((int)Math.Floor(s), 0, Segments - 1);
        return (i, s - i);
    }

    private static double Length(IReadOnlyList<Point2D> knots, int piece) => knots[piece].DistanceTo(knots[piece + 1]);

    /// <summary>The two ends of a piece and their tangents, each scaled by the piece's own length.</summary>
    private (Point2D P0, Point2D P1, Vector2D M0, Vector2D M1) Hermite(IReadOnlyList<Point2D> knots, int i)
    {
        var length = Length(knots, i);
        return (knots[i], knots[i + 1], _tangents[i] * length, _tangents[i + 1] * length);
    }

    /// <summary>
    /// The direction of the curve at each knot: along the line joining its neighbours, and at
    /// the ends the next knot's direction reflected in the end piece, which is how a circle
    /// through three points leaves its first one.
    /// </summary>
    private static Vector2D[] Tangents(IReadOnlyList<Point2D> knots)
    {
        var n = knots.Count;
        var tangents = new Vector2D[n];
        for (var i = 1; i < n - 1; i++)
        {
            // Along the circle through the point and its two neighbours: each chord weighted by
            // the square of the other. Evenly spaced, that is the line joining the neighbours;
            // unevenly, it still follows a circle exactly, so an arc given points stays an arc.
            var before = knots[i] - knots[i - 1];
            var after = knots[i + 1] - knots[i];
            var direction = after * before.Dot(before) + before * after.Dot(after);
            tangents[i] = direction.NormalisedOrDefault(after.NormalisedOrDefault(Vector2D.UnitX));
        }

        tangents[0] = Reflect(tangents[1], (knots[1] - knots[0]).NormalisedOrDefault(Vector2D.UnitX));
        tangents[n - 1] = Reflect(tangents[n - 2], (knots[n - 1] - knots[n - 2]).NormalisedOrDefault(Vector2D.UnitX));
        return tangents;
    }

    /// <summary>A direction mirrored in a line along <paramref name="axis"/>.</summary>
    private static Vector2D Reflect(Vector2D direction, Vector2D axis) =>
        (axis * (2 * direction.Dot(axis)) - direction).NormalisedOrDefault(axis);

    /// <summary>a / b, taking both as complex numbers: the point in the frame b spans.</summary>
    internal static Vector2D Divide(Vector2D a, Vector2D b)
    {
        var squared = b.X * b.X + b.Y * b.Y;
        return new Vector2D((a.X * b.X + a.Y * b.Y) / squared, (a.Y * b.X - a.X * b.Y) / squared);
    }

    /// <summary>a · b, taking both as complex numbers: a turned and scaled by b.</summary>
    internal static Vector2D Multiply(Vector2D a, Vector2D b) => new(a.X * b.X - a.Y * b.Y, a.X * b.Y + a.Y * b.X);

    public bool Equals(WallSpline? other) =>
        other is not null && From == other.From && To == other.To && _through.AsSpan().SequenceEqual(other._through);

    public override bool Equals(object? obj) => Equals(obj as WallSpline);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(From);
        hash.Add(To);
        foreach (var point in _through) hash.Add(point);
        return hash.ToHashCode();
    }
}
