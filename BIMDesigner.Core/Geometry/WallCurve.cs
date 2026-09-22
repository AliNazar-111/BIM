namespace BIMDesigner.Core.Geometry;

/// <summary>
/// The line a wall is drawn along: straight, an arc of a circle, or a piece of an ellipse
/// (specification section 3.1, "straight, arc and curved walls", "elliptical walls").
///
/// An arc is given by its two ends and a <em>bulge</em>, the tangent of a quarter of the angle
/// it turns through - the convention CAD has used for decades. Zero is straight, positive
/// turns left (anticlockwise) from start to end, and 1 is a half circle. Keeping the ends as
/// the defining points means everything that works on wall ends - joins, snapping, grips,
/// trimming - works on curved walls unchanged, and moving or mirroring a wall never has to
/// reconstruct its centre.
///
/// An elliptical piece is held the same way, by its ends and a <see cref="WallEllipse"/>, and
/// a spline by its ends and a <see cref="WallSpline"/>. Both are measured by sampling a
/// parameter along them, and share the code that does it.
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

    /// <summary>How many pieces an elliptical curve is measured in to find distances along it.</summary>
    private const int EllipseSamples = 1024;

    // An elliptical curve: its centre, axes and parameter range, and the distance along it at
    // each of the evenly spaced parameters it is measured at.
    private readonly Point2D _ellipseCentre;
    private readonly Vector2D _axisU, _axisV;
    private readonly double _semiU, _semiV, _t0, _t1;
    private readonly double[]? _lengths;

    // A spline: the map from its own frame to the plan, as a point it is pinned at and a turn
    // and scale taken as a complex number.
    private readonly Point2D _splineOrigin;
    private readonly Vector2D _splineFrame;

    private WallCurve(Point2D start, Point2D end, double bulge, WallEllipse? ellipse = null, WallSpline? spline = null)
    {
        Start = start;
        End = end;

        var chord = end - start;
        var chordLength = chord.Length;

        if (spline is { IsValid: true } curve && chordLength >= 1e-9)
        {
            Spline = curve;
            _t0 = curve.From;
            _t1 = curve.To;

            // The spline's own frame is turned and scaled so its two ends land on the wall's.
            var from = curve.LocalAt(_t0);
            _splineOrigin = from;
            _splineFrame = WallSpline.Divide(chord, curve.LocalAt(_t1) - from);

            _lengths = MeasureEllipse();
            Length = _lengths[^1];
            return;
        }

        if (ellipse is { IsValid: true } shape && chordLength >= 1e-9)
        {
            Ellipse = shape;
            _t0 = shape.From;
            _t1 = shape.To;

            // The line between the ends in the ellipse's own frame, for an ellipse with a unit
            // first semi-axis. Its length gives the size, its direction the rotation.
            var local = new Vector2D(Math.Cos(_t1) - Math.Cos(_t0), shape.Ratio * (Math.Sin(_t1) - Math.Sin(_t0)));
            if (local.Length > 1e-9)
            {
                _semiU = chordLength / local.Length;
                _semiV = _semiU * shape.Ratio;

                var rotation = Math.Atan2(chord.Y, chord.X) - Math.Atan2(local.Y, local.X);
                _axisU = new Vector2D(Math.Cos(rotation), Math.Sin(rotation));
                _axisV = _axisU.PerpendicularLeft();
                _ellipseCentre = start - _axisU * (_semiU * Math.Cos(_t0)) - _axisV * (_semiV * Math.Sin(_t0));

                _lengths = MeasureEllipse();
                Length = _lengths[^1];
                return;
            }

            Ellipse = null;
        }

        Bulge = Math.Abs(bulge) < StraightBulge || chordLength < 1e-9 ? 0 : bulge;

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

    /// <summary>A curve that is a piece of an ellipse when one is given, otherwise a line or arc.</summary>
    public static WallCurve Of(Point2D start, Point2D end, double bulge, WallEllipse? ellipse) => new(start, end, bulge, ellipse);

    /// <summary>A spline curve when one is given, else a piece of an ellipse, else a line or arc.</summary>
    public static WallCurve Of(Point2D start, Point2D end, double bulge, WallEllipse? ellipse, WallSpline? spline) => new(start, end, bulge, ellipse, spline);

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

    /// <summary>Whether this is an arc of a circle.</summary>
    public bool IsArc => Bulge != 0;

    /// <summary>The ellipse this is a piece of, or null for a line or an arc.</summary>
    public WallEllipse? Ellipse { get; }

    public bool IsElliptical => Ellipse is not null;

    /// <summary>The spline this is a stretch of, or null.</summary>
    public WallSpline? Spline { get; }

    public bool IsSpline => Spline is not null;

    /// <summary>Whether this is measured by sampling a parameter - an ellipse or a spline - rather than worked out exactly.</summary>
    private bool IsSampled => IsElliptical || IsSpline;

    /// <summary>Whether this is anything but a straight line.</summary>
    public bool IsCurved => IsArc || IsSampled;

    /// <summary>
    /// The tightest the curve bends: the radius of an arc, the smallest radius of curvature of
    /// an elliptical piece, infinite for a line.
    /// </summary>
    public double MinRadius
    {
        get
        {
            if (IsArc) return Radius;
            if (!IsSampled) return double.PositiveInfinity;

            var smallest = double.PositiveInfinity;
            for (var i = 0; i <= EllipseSamples; i++) smallest = Math.Min(smallest, CurvatureRadius(ParameterOf(i)));
            return smallest;
        }
    }

    /// <summary>Length along the curve, in millimetres.</summary>
    public double Length { get; }

    public Point2D Centre { get; }

    public double Radius { get; }

    /// <summary>The angle turned through from start to end, in radians. Positive is anticlockwise.</summary>
    public double Sweep { get; }

    public double StartAngle { get; }

    /// <summary>
    /// Where a spline's points between its ends are on the plan, each with its place in the
    /// spline's list. Only the ones this stretch of it passes; none for any other curve.
    /// </summary>
    public IReadOnlyList<(int Index, Point2D Point)> SplinePoints()
    {
        if (Spline is not { } spline) return Array.Empty<(int, Point2D)>();

        return spline.Through
            .Select((local, i) => (Index: i, Knot: i + 1.0, Local: local))
            .Where(p => p.Knot > _t0 + 1e-9 && p.Knot < _t1 - 1e-9)
            .Select(p => (p.Index, Start + WallSpline.Multiply(p.Local - _splineOrigin, _splineFrame)))
            .ToList();
    }

    /// <summary>The point this far along the curve.</summary>
    public Point2D PointAt(double along) => At(along, 0);

    /// <summary>The point this far along, and this far to the left of the direction of travel.</summary>
    public Point2D At(double along, double left)
    {
        if (IsSampled)
        {
            var t = ParameterAt(along);
            return EllipsePoint(t) + EllipseTangent(t).PerpendicularLeft() * left;
        }

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
        if (IsSampled) return EllipseTangent(ParameterAt(along));

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
        if (IsSampled) return LocateOnEllipse(point);

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
        if (!IsCurved) return Line2D.DistanceFromSegment(point, Start, End);

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
        if (IsSampled) return IntersectEllipse(line, left, near);

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

        if (IsSampled || other.IsSampled)
        {
            // Walked along whichever is elliptical, watching for the other one's side to change.
            var (walked, against) = IsSampled ? (this, other) : (other, this);
            return walked.CrossingsWith(against).Where(OnBoth).ToList();
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
        if (IsSampled)
        {
            foreach (var along in EllipseBetween(from, to, left)) yield return along;
            yield break;
        }

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
    public WallCurve Offset(double left) =>
        IsSpline ? OffsetSpline(left) : IsElliptical ? OffsetEllipse(left) : new(At(0, left), At(Length, left), Bulge);

    /// <summary>The part of the curve between two distances along it.</summary>
    public WallCurve Part(double from, double to)
    {
        if (IsSpline) return new WallCurve(PointAt(from), PointAt(to), 0, null, Spline!.Part(ParameterAt(from), ParameterAt(to)));
        if (IsElliptical) return new WallCurve(PointAt(from), PointAt(to), 0, Ellipse!.Value with { From = ParameterAt(from), To = ParameterAt(to) });

        if (!IsArc) return new WallCurve(PointAt(from), PointAt(to), 0);

        var sweep = (to - from) / Radius * Math.Sign(Sweep);
        return new WallCurve(PointAt(from), PointAt(to), Math.Tan(sweep / 4));
    }

    private double AngleAt(double along) => StartAngle + Math.Sign(Sweep) * along / Radius;

    // ----- Elliptical pieces -----
    //
    // An ellipse has no closed form for distance along it, so the curve is measured once at
    // evenly spaced parameters when it is made, and a distance along is turned back into a
    // parameter by looking it up. Everything else - points, tangents, offsets - is exact at
    // that parameter.

    /// <summary>+1 when the parameter rises from start to end (turning left), -1 when it falls.</summary>
    private double Turn => Math.Sign(_t1 - _t0);

    private double ParameterOf(int sample) => _t0 + (_t1 - _t0) * sample / EllipseSamples;

    // The same parameter machinery serves a spline: its point, first and second derivatives
    // are the spline's own, carried from its frame onto the plan.

    private Point2D EllipsePoint(double t) =>
        IsSpline
            ? Start + WallSpline.Multiply(Spline!.LocalAt(t) - _splineOrigin, _splineFrame)
            : _ellipseCentre + _axisU * (_semiU * Math.Cos(t)) + _axisV * (_semiV * Math.Sin(t));

    private Vector2D EllipseDerivative(double t) =>
        IsSpline
            ? WallSpline.Multiply(Spline!.LocalDerivative(t), _splineFrame)
            : _axisU * (-_semiU * Math.Sin(t)) + _axisV * (_semiV * Math.Cos(t));

    private Vector2D EllipseSecondDerivative(double t) =>
        IsSpline
            ? WallSpline.Multiply(Spline!.LocalSecondDerivative(t), _splineFrame)
            : _axisU * (-_semiU * Math.Cos(t)) + _axisV * (-_semiV * Math.Sin(t));

    private double Speed(double t) => EllipseDerivative(t).Length;

    private Vector2D EllipseTangent(double t) => (EllipseDerivative(t) * Turn).NormalisedOrDefault(Vector2D.UnitX);

    /// <summary>Radius of curvature at a parameter: speed cubed over the cross product of the derivatives. Infinite where it runs straight.</summary>
    private double CurvatureRadius(double t)
    {
        var speed = Speed(t);
        var cross = Math.Abs(EllipseDerivative(t).Cross(EllipseSecondDerivative(t)));
        return cross < 1e-12 ? double.PositiveInfinity : speed * speed * speed / cross;
    }

    /// <summary>+1 where the curve bends left in the direction of travel, -1 where it bends right.</summary>
    private double BendAt(double t)
    {
        var bend = Math.Sign(EllipseDerivative(t).Cross(EllipseSecondDerivative(t))) * Turn;
        return bend == 0 ? 1 : bend;
    }

    /// <summary>Length of the curve over a stretch of parameter, by three-point Gauss-Legendre.</summary>
    private double StretchLength(double t, double span)
    {
        // A spline bends differently either side of each of its points, which the rule below
        // cannot see across, so a stretch over one is measured as two.
        if (IsSpline)
        {
            var (low, high) = span >= 0 ? (t, t + span) : (t + span, t);
            var knot = Math.Floor(high - 1e-12);
            if (knot > low + 1e-12 && knot < high - 1e-12)
                return GaussLength(low, knot - low) + GaussLength(knot, high - knot);
        }

        return GaussLength(t, span);
    }

    private double GaussLength(double t, double span)
    {
        const double node = 0.7745966692414834;
        var half = span / 2;
        var middle = t + half;
        return Math.Abs(half) * (8.0 / 9 * Speed(middle) + 5.0 / 9 * (Speed(middle - half * node) + Speed(middle + half * node)));
    }

    private double[] MeasureEllipse()
    {
        var lengths = new double[EllipseSamples + 1];
        var span = (_t1 - _t0) / EllipseSamples;
        for (var i = 0; i < EllipseSamples; i++) lengths[i + 1] = lengths[i] + StretchLength(ParameterOf(i), span);
        return lengths;
    }

    /// <summary>The parameter this far along. Past either end the curve runs on along its end tangent's pace.</summary>
    private double ParameterAt(double along)
    {
        var lengths = _lengths!;
        if (along <= 0) return _t0 + Turn * along / Math.Max(Speed(_t0), 1e-9);
        if (along >= Length) return _t1 + Turn * (along - Length) / Math.Max(Speed(_t1), 1e-9);

        var index = Array.BinarySearch(lengths, along);
        if (index >= 0) return ParameterOf(index);

        var i = Math.Clamp(~index - 1, 0, EllipseSamples - 1);
        var piece = lengths[i + 1] - lengths[i];
        var fraction = piece > 0 ? (along - lengths[i]) / piece : 0;
        var t = ParameterOf(i) + (_t1 - _t0) / EllipseSamples * fraction;

        // The pace along the curve changes within a piece, so the guess is polished by Newton's
        // method until the distance along it gives back is the one asked for.
        for (var k = 0; k < 3; k++) t += Turn * (along - AlongAt(t)) / Math.Max(Speed(t), 1e-9);
        return t;
    }

    /// <summary>The distance along at a parameter between the ends.</summary>
    private double AlongAt(double t)
    {
        var position = (t - _t0) / (_t1 - _t0) * EllipseSamples;
        var i = Math.Clamp((int)Math.Floor(position), 0, EllipseSamples - 1);
        var from = ParameterOf(i);
        return _lengths![i] + StretchLength(from, t - from);
    }

    private (double Along, double Left) LocateOnEllipse(Point2D point)
    {
        // The nearest of a coarse set of points, then narrowed down between its neighbours.
        var coarse = IsSpline ? Math.Min(128 * Spline!.Segments, 4096) : 128;
        var best = 0;
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i <= coarse; i++)
        {
            var distance = (EllipsePoint(_t0 + (_t1 - _t0) * i / coarse) - point).Length;
            if (distance < bestDistance) (best, bestDistance) = (i, distance);
        }

        var step = (_t1 - _t0) / coarse;
        var (low, high) = (_t0 + step * Math.Max(best - 1, 0), _t0 + step * Math.Min(best + 1, coarse));
        if (low > high) (low, high) = (high, low);

        for (var k = 0; k < 80; k++)
        {
            var a = low + (high - low) / 3;
            var b = high - (high - low) / 3;
            if ((EllipsePoint(a) - point).Length < (EllipsePoint(b) - point).Length) high = b;
            else low = a;
        }

        var t = (low + high) / 2;

        // Past an end the point is measured along that end's tangent, as for a line.
        var startTangent = EllipseTangent(_t0);
        var beforeStart = (point - Start).Dot(startTangent);
        if (Math.Abs(t - _t0) < 1e-9 && beforeStart < 0)
            return (beforeStart, (point - Start).Dot(startTangent.PerpendicularLeft()));

        var endTangent = EllipseTangent(_t1);
        var pastEnd = (point - End).Dot(endTangent);
        if (Math.Abs(t - _t1) < 1e-9 && pastEnd > 0)
            return (Length + pastEnd, (point - End).Dot(endTangent.PerpendicularLeft()));

        return (AlongAt(t), (point - EllipsePoint(t)).Dot(EllipseTangent(t).PerpendicularLeft()));
    }

    /// <summary>Where a line crosses the whole ellipse, offset this far left, nearest a point.</summary>
    private Point2D? IntersectEllipse(Line2D line, double left, Point2D near)
    {
        var across = line.Direction.NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        Point2D Offset(double t) => EllipsePoint(t) + EllipseTangent(t).PerpendicularLeft() * left;
        double Side(double t) => (Offset(t) - line.Origin).Dot(across);

        // A crossing that lands on a sample - as it does at the ends of an axis - can come out a
        // hair either side of zero, so near enough counts as on the line.
        // The whole ellipse, all the way round; for a spline its whole length and a piece beyond
        // each end, where it runs on straight - a wall end is often cut just past its curve.
        var steps = IsSpline ? 240 * (Spline!.Segments + 2) : 720;
        var (first, span) = IsSpline ? (-1.0, Spline!.Segments + 2.0) : (_t0, Turn * 2 * Math.PI);
        const double onLine = 1e-7;
        Point2D? nearest = null;

        void Consider(Point2D point)
        {
            if (nearest is null || point.DistanceTo(near) < nearest.Value.DistanceTo(near)) nearest = point;
        }

        for (var i = 0; i < steps; i++)
        {
            var a = first + span * i / steps;
            var b = first + span * (i + 1) / steps;
            var (sa, sb) = (Side(a), Side(b));

            if (Math.Abs(sa) < onLine)
            {
                Consider(Offset(a));
                continue;
            }

            if (Math.Abs(sb) < onLine || sa * sb > 0) continue;

            for (var k = 0; k < 60; k++)
            {
                var m = (a + b) / 2;
                var sm = Side(m);
                if (sa * sm <= 0) b = m;
                else (a, sa) = (m, sm);
            }

            Consider(Offset((a + b) / 2));
        }

        return nearest;
    }

    /// <summary>Points where this elliptical piece passes from one side of another curve to the other.</summary>
    private IEnumerable<Point2D> CrossingsWith(WallCurve other)
    {
        var positions = new List<double> { 0 };
        positions.AddRange(Between(0, Length));
        positions.Add(Length);

        double Side(double along) => other.Locate(PointAt(along)).Left;

        var found = new List<Point2D>();
        void Add(Point2D point)
        {
            if (found.All(p => p.DistanceTo(point) > 1e-6)) found.Add(point);
        }

        for (var i = 0; i + 1 < positions.Count; i++)
        {
            double a = positions[i], b = positions[i + 1];
            double sa = Side(a), sb = Side(b);
            const double onLine = 1e-7;
            if (Math.Abs(sa) < onLine) Add(PointAt(a));
            if (Math.Abs(sb) < onLine) Add(PointAt(b));
            if (Math.Abs(sa) < onLine || Math.Abs(sb) < onLine || sa * sb > 0) continue;

            for (var k = 0; k < 60; k++)
            {
                var m = (a + b) / 2;
                var sm = Side(m);
                if (sa * sm <= 0) b = m;
                else (a, sa) = (m, sm);
            }

            Add(PointAt((a + b) / 2));
        }

        return found;
    }

    private IEnumerable<double> EllipseBetween(double from, double to, double left)
    {
        if (Math.Abs(to - from) < 1e-9) return Array.Empty<double>();

        var (low, high) = from < to ? (from, to) : (to, from);
        var positions = new List<double>();
        var along = low;

        // Each piece turns through no more than the tolerance allows at the curvature where it
        // starts, measured on the edge being drawn rather than the location line.
        while (true)
        {
            var t = ParameterAt(along);
            var radius = CurvatureRadius(t);
            var edgeRadius = Math.Max(radius - BendAt(t) * left, 1);
            var toleranceStep = 2 * Math.Acos(Math.Clamp(1 - ChordTolerance / edgeRadius, -1, 1));
            var step = Math.Min(MaxStepRadians, Math.Max(toleranceStep, 1e-3)) * radius;

            // A spline runs nearly straight in places, where the step would jump right over the
            // next bend; it is kept short enough to see each piece between its points.
            if (IsSpline) step = Math.Min(step, Length / (8.0 * Spline!.Segments));
            along += step;

            if (along >= high - 1e-6) break;
            positions.Add(along);
        }

        if (from > to) positions.Reverse();
        return positions;
    }

    /// <summary>
    /// The piece moved sideways: an ellipse with both semi-axes grown or shrunk by the offset.
    /// The true offset of an ellipse is not an ellipse; this one matches it exactly at the four
    /// ends of the axes and stays within a hair of it between them for any ordinary wall.
    /// </summary>
    private WallCurve OffsetSpline(double left)
    {
        // The true offset of a spline is not a spline either. This one passes through the
        // offset of its ends and of points close along it, and follows the same bends between.
        var spline = Spline!;
        // Every 200 mm or so, so the new curve cannot wander from the true offset where the old
        // one bends tightly - within a tenth of a millimetre or two for any wall drawn by hand.
        var count = Math.Clamp((int)Math.Ceiling(Length / 200), 8, 96);
        var through = Enumerable.Range(1, count - 1).Select(i => At(Length * i / count, left)).ToList();

        var (start, end) = (At(0, left), At(Length, left));
        return WallSpline.Between(start, end, through) is { IsValid: true } shifted
            ? new WallCurve(start, end, 0, null, shifted)
            : new WallCurve(start, end, 0);
    }

    private WallCurve OffsetEllipse(double left)
    {
        var growth = -Turn * left;
        var (u, v) = (_semiU + growth, _semiV + growth);
        if (u <= 1e-6 || v <= 1e-6) return new WallCurve(At(0, left), At(Length, left), 0, Ellipse);

        Point2D Grown(double t) => _ellipseCentre + _axisU * (u * Math.Cos(t)) + _axisV * (v * Math.Sin(t));
        return new WallCurve(Grown(_t0), Grown(_t1), 0, Ellipse!.Value with { Ratio = v / u });
    }

    private static double Normalise(double angle)
    {
        angle %= 2 * Math.PI;
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }
}
