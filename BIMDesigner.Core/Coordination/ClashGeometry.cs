using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Coordination;

/// <summary>A point or direction in the model, mm, with the arithmetic a clash check needs.</summary>
internal readonly record struct V(double X, double Y, double Z)
{
    public static V Of(Point3D point) => new(point.X, point.Y, point.Z);

    public Point3D ToPoint() => new(X, Y, Z);

    public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static V operator *(V a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    public double Dot(V b) => X * b.X + Y * b.Y + Z * b.Z;

    public V Cross(V b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public double Length => Math.Sqrt(Dot(this));
}

/// <summary>
/// An element's shape as a clash check sees it: its triangles, each with its own box, and the
/// box round them all.
/// </summary>
internal sealed class ClashSolid
{
    private readonly V[] _a, _b, _c;
    private readonly (V Min, V Max)[] _boxes;

    public ClashSolid(IEnumerable<Mesh3D> meshes)
    {
        var (a, b, c) = (new List<V>(), new List<V>(), new List<V>());
        foreach (var mesh in meshes)
        {
            var points = mesh.Positions;
            var indices = mesh.Indices;
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var (p, q, r) = (V.Of(points[indices[i]]), V.Of(points[indices[i + 1]]), V.Of(points[indices[i + 2]]));
                if ((q - p).Cross(r - p).Length < 1e-9) continue;
                a.Add(p);
                b.Add(q);
                c.Add(r);
            }
        }

        (_a, _b, _c) = (a.ToArray(), b.ToArray(), c.ToArray());
        _boxes = new (V, V)[_a.Length];
        var (min, max) = (new V(double.MaxValue, double.MaxValue, double.MaxValue), new V(double.MinValue, double.MinValue, double.MinValue));
        for (var i = 0; i < _a.Length; i++)
        {
            var low = new V(Math.Min(_a[i].X, Math.Min(_b[i].X, _c[i].X)), Math.Min(_a[i].Y, Math.Min(_b[i].Y, _c[i].Y)), Math.Min(_a[i].Z, Math.Min(_b[i].Z, _c[i].Z)));
            var high = new V(Math.Max(_a[i].X, Math.Max(_b[i].X, _c[i].X)), Math.Max(_a[i].Y, Math.Max(_b[i].Y, _c[i].Y)), Math.Max(_a[i].Z, Math.Max(_b[i].Z, _c[i].Z)));
            _boxes[i] = (low, high);
            min = new V(Math.Min(min.X, low.X), Math.Min(min.Y, low.Y), Math.Min(min.Z, low.Z));
            max = new V(Math.Max(max.X, high.X), Math.Max(max.Y, high.Y), Math.Max(max.Z, high.Z));
        }

        (Min, Max) = (min, max);
    }

    public int Count => _a.Length;

    public V Min { get; }

    public V Max { get; }

    public bool IsEmpty => _a.Length == 0;

    /// <summary>Every corner of its triangles, once each.</summary>
    public IEnumerable<V> Vertices => _a.Concat(_b).Concat(_c).Distinct();

    /// <summary>How far apart two boxes are - none where they overlap.</summary>
    public static double BoxGap((V Min, V Max) one, (V Min, V Max) other)
    {
        var dx = Math.Max(0, Math.Max(one.Min.X - other.Max.X, other.Min.X - one.Max.X));
        var dy = Math.Max(0, Math.Max(one.Min.Y - other.Max.Y, other.Min.Y - one.Max.Y));
        var dz = Math.Max(0, Math.Max(one.Min.Z - other.Max.Z, other.Min.Z - one.Max.Z));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public double Gap(ClashSolid other) => BoxGap((Min, Max), (other.Min, other.Max));

    /// <summary>Whether a point is inside it: a ray from it crosses its surface an odd number of times.</summary>
    public bool Inside(V point)
    {
        if (point.X < Min.X || point.X > Max.X || point.Y < Min.Y || point.Y > Max.Y || point.Z < Min.Z || point.Z > Max.Z) return false;

        // A direction no face of a building lines up with.
        var direction = new V(0.5773, 0.5779, 0.5769);
        var crossings = 0;
        for (var i = 0; i < _a.Length; i++)
        {
            var (e1, e2) = (_b[i] - _a[i], _c[i] - _a[i]);
            var p = direction.Cross(e2);
            var det = e1.Dot(p);
            if (Math.Abs(det) < 1e-12) continue;
            var inv = 1 / det;
            var s = point - _a[i];
            var u = s.Dot(p) * inv;
            if (u < 0 || u > 1) continue;
            var q = s.Cross(e1);
            var v = direction.Dot(q) * inv;
            if (v < 0 || u + v > 1) continue;
            if (e2.Dot(q) * inv > 1e-9) crossings++;
        }

        return crossings % 2 == 1;
    }

    /// <summary>How far a point is from its surface.</summary>
    public double Distance(V point)
    {
        var best = double.MaxValue;
        for (var i = 0; i < _a.Length; i++)
        {
            if (BoxGap(_boxes[i], (point, point)) >= best) continue;
            best = Math.Min(best, (Closest(point, _a[i], _b[i], _c[i]) - point).Length);
        }

        return best;
    }

    /// <summary>
    /// Where its surface passes through another's - each triangle crossing one of the other's,
    /// both cut through, not merely touching within so much: the ends of each line they cross on.
    /// </summary>
    public List<V> Crossings(ClashSolid other, double touch)
    {
        var found = new List<V>();
        for (var i = 0; i < _a.Length; i++)
        {
            if (BoxGap(_boxes[i], (other.Min, other.Max)) > touch) continue;
            for (var j = 0; j < other._a.Length; j++)
            {
                if (BoxGap(_boxes[i], other._boxes[j]) > 0) continue;
                if (Cross(_a[i], _b[i], _c[i], other._a[j], other._b[j], other._c[j], touch) is { } segment)
                {
                    found.Add(segment.From);
                    found.Add(segment.To);
                }
            }
        }

        return found;
    }

    /// <summary>The least distance between its surface and another's, looked for no further than so far.</summary>
    public double Distance(ClashSolid other, double within)
    {
        var best = within;
        for (var i = 0; i < _a.Length; i++)
        {
            if (BoxGap(_boxes[i], (other.Min, other.Max)) >= best) continue;
            for (var j = 0; j < other._a.Length; j++)
            {
                if (BoxGap(_boxes[i], other._boxes[j]) >= best) continue;
                best = Math.Min(best, TriangleDistance(_a[i], _b[i], _c[i], other._a[j], other._b[j], other._c[j]));
                if (best <= 0) return 0;
            }
        }

        return best;
    }

    // ---- triangles --------------------------------------------------------------

    /// <summary>
    /// Where two triangles cut through each other: each straddling the other's plane by more than
    /// so much either side, and the two stretches on the line they share overlapping by more than
    /// that - a face lying on a face, or an edge resting on one, is not a crossing.
    /// </summary>
    private static (V From, V To)? Cross(V a0, V a1, V a2, V b0, V b1, V b2, double touch)
    {
        var na = (a1 - a0).Cross(a2 - a0);
        var nb = (b1 - b0).Cross(b2 - b0);
        var (la, lb) = (na.Length, nb.Length);
        if (la < 1e-12 || lb < 1e-12) return null;
        na *= 1 / la;
        nb *= 1 / lb;

        // Each one's corners either side of the other's plane, by more than the touch.
        var (db0, db1, db2) = (na.Dot(b0 - a0), na.Dot(b1 - a0), na.Dot(b2 - a0));
        if (Math.Max(db0, Math.Max(db1, db2)) <= touch || Math.Min(db0, Math.Min(db1, db2)) >= -touch) return null;
        var (da0, da1, da2) = (nb.Dot(a0 - b0), nb.Dot(a1 - b0), nb.Dot(a2 - b0));
        if (Math.Max(da0, Math.Max(da1, da2)) <= touch || Math.Min(da0, Math.Min(da1, da2)) >= -touch) return null;

        var line = na.Cross(nb);
        if (line.Length < 1e-9) return null;
        if (Crossing(a0, a1, a2, da0, da1, da2) is not var (p0, p1)) return null;
        if (Crossing(b0, b1, b2, db0, db1, db2) is not var (q0, q1)) return null;

        double At(V point) => line.Dot(point);
        var (s0, s1) = (At(p0), At(p1));
        var (t0, t1) = (At(q0), At(q1));
        if (s0 > s1) (s0, s1, p0, p1) = (s1, s0, p1, p0);
        if (t0 > t1) (t0, t1) = (t1, t0);
        var (from, to) = (Math.Max(s0, t0), Math.Min(s1, t1));
        if ((to - from) / line.Length <= touch) return null;

        V On(double s) => s1 - s0 < 1e-12 ? p0 : p0 + (p1 - p0) * ((s - s0) / (s1 - s0));
        return (On(from), On(to));
    }

    /// <summary>Where a triangle's edges cross a plane its corners lie either side of, by their distances from it.</summary>
    private static (V, V)? Crossing(V p0, V p1, V p2, double d0, double d1, double d2)
    {
        var points = new List<V>(2);
        void Edge(V a, V b, double da, double db)
        {
            if (da > 0 == db > 0 || Math.Abs(da - db) < 1e-12) return;
            points.Add(a + (b - a) * (da / (da - db)));
        }

        Edge(p0, p1, d0, d1);
        Edge(p1, p2, d1, d2);
        Edge(p2, p0, d2, d0);
        return points.Count >= 2 ? (points[0], points[1]) : null;
    }

    /// <summary>The point of a triangle nearest a point.</summary>
    private static V Closest(V p, V a, V b, V c)
    {
        var (ab, ac, ap) = (b - a, c - a, p - a);
        var (d1, d2) = (ab.Dot(ap), ac.Dot(ap));
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        var (d3, d4) = (ab.Dot(bp), ac.Dot(bp));
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        var (d5, d6) = (ab.Dot(cp), ac.Dot(cp));
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        var denominator = 1 / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }

    /// <summary>The least distance between two segments.</summary>
    private static double SegmentDistance(V p1, V q1, V p2, V q2)
    {
        var (d1, d2, r) = (q1 - p1, q2 - p2, p1 - p2);
        var (a, e, f) = (d1.Dot(d1), d2.Dot(d2), d2.Dot(r));
        double s, t;
        if (a <= 1e-12 && e <= 1e-12) return r.Length;
        if (a <= 1e-12)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            var c = d1.Dot(r);
            if (e <= 1e-12)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                var b = d1.Dot(d2);
                var denom = a * e - b * b;
                s = denom > 1e-12 ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((b - c) / a, 0, 1);
                }
            }
        }

        return (p1 + d1 * s - (p2 + d2 * t)).Length;
    }

    /// <summary>The least distance between two triangles: none where they cross.</summary>
    private static double TriangleDistance(V a0, V a1, V a2, V b0, V b1, V b2)
    {
        if (Cross(a0, a1, a2, b0, b1, b2, 0) is not null) return 0;
        var best = double.MaxValue;
        foreach (var p in new[] { a0, a1, a2 }) best = Math.Min(best, (Closest(p, b0, b1, b2) - p).Length);
        foreach (var p in new[] { b0, b1, b2 }) best = Math.Min(best, (Closest(p, a0, a1, a2) - p).Length);
        var (ea, eb) = (new[] { (a0, a1), (a1, a2), (a2, a0) }, new[] { (b0, b1), (b1, b2), (b2, b0) });
        foreach (var (p, q) in ea)
        foreach (var (r, s) in eb)
            best = Math.Min(best, SegmentDistance(p, q, r, s));
        return best;
    }
}
