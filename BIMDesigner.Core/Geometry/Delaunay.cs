namespace BIMDesigner.Core.Geometry;

/// <summary>
/// A Delaunay triangulation of points in plan (Bowyer-Watson): triangles none of whose
/// circumcircles holds another point - the fattest triangles the points allow, as a ground
/// surface is drawn between its spot levels. Points closer than a millimetre count once.
/// </summary>
public static class Delaunay
{
    public static IReadOnlyList<(int A, int B, int C)> Triangulate(IReadOnlyList<Point2D> points)
    {
        var count = points.Count;
        if (count < 3) return Array.Empty<(int, int, int)>();

        // A triangle round them all, far out, whose corners are taken away at the end.
        var (minX, minY) = (points.Min(point => point.X), points.Min(point => point.Y));
        var (maxX, maxY) = (points.Max(point => point.X), points.Max(point => point.Y));
        var size = Math.Max(maxX - minX, maxY - minY) * 20 + 1;
        var (midX, midY) = ((minX + maxX) / 2, (minY + maxY) / 2);
        var all = points.ToList();
        all.Add(new Point2D(midX - size, midY - size));
        all.Add(new Point2D(midX + size, midY - size));
        all.Add(new Point2D(midX, midY + size));

        var triangles = new List<(int A, int B, int C)> { (count, count + 1, count + 2) };
        var kept = new List<Point2D>();
        for (var i = 0; i < count; i++)
        {
            var point = all[i];
            if (kept.Any(other => other.DistanceTo(point) < 1)) continue;
            kept.Add(point);

            // The triangles whose circumcircle holds the point, taken out; the hole's edges joined to it.
            var bad = triangles.Where(triangle => InCircle(all[triangle.A], all[triangle.B], all[triangle.C], point)).ToList();
            var edges = new Dictionary<(int, int), int>();
            foreach (var (a, b, c) in bad)
                foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) })
                {
                    var key = from < to ? (from, to) : (to, from);
                    edges[key] = edges.TryGetValue(key, out var seen) ? seen + 1 : 1;
                }

            triangles.RemoveAll(bad.Contains);
            foreach (var ((from, to), times) in edges)
            {
                if (times != 1) continue;
                triangles.Add(Anticlockwise(all, from, to, i));
            }
        }

        return triangles.Where(triangle => triangle.A < count && triangle.B < count && triangle.C < count
                                           && Math.Abs(Cross(all[triangle.A], all[triangle.B], all[triangle.C])) > 1e-6).ToList();
    }

    private static (int, int, int) Anticlockwise(IReadOnlyList<Point2D> points, int a, int b, int c) =>
        Cross(points[a], points[b], points[c]) >= 0 ? (a, b, c) : (a, c, b);

    private static double Cross(Point2D a, Point2D b, Point2D c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    /// <summary>Whether a point is inside the circle through a triangle's corners.</summary>
    private static bool InCircle(Point2D a, Point2D b, Point2D c, Point2D p)
    {
        if (Cross(a, b, c) < 0) (b, c) = (c, b);
        var (ax, ay, bx, by, cx, cy) = (a.X - p.X, a.Y - p.Y, b.X - p.X, b.Y - p.Y, c.X - p.X, c.Y - p.Y);
        var determinant = (ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay) + (cx * cx + cy * cy) * (ax * by - bx * ay);
        return determinant > 1e-9;
    }
}
