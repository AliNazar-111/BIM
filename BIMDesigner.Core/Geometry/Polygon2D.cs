namespace BIMDesigner.Core.Geometry;

/// <summary>
/// Measurements on a closed outline. Rooms, floors, ceilings and roofs are all "a polygon
/// with data attached", so the arithmetic that turns an outline into an area lives once
/// here rather than being rewritten - and rounded differently - in each of them.
/// </summary>
public static class Polygon2D
{
    /// <summary>
    /// Shoelace area: positive when the points run anticlockwise, negative when clockwise.
    /// The sign is what tells a boundary trace whether it went round the inside of a room
    /// or the outside of the building.
    /// </summary>
    public static double SignedArea(IReadOnlyList<Point2D> polygon)
    {
        if (polygon.Count < 3) return 0;

        var total = 0.0;

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            total += a.X * b.Y - b.X * a.Y;
        }

        return total / 2;
    }

    /// <summary>Area regardless of which way round the points run.</summary>
    public static double Area(IReadOnlyList<Point2D> polygon) => Math.Abs(SignedArea(polygon));

    public static double Perimeter(IReadOnlyList<Point2D> polygon)
    {
        if (polygon.Count < 2) return 0;

        var total = 0.0;

        for (var i = 0; i < polygon.Count; i++)
            total += polygon[i].DistanceTo(polygon[(i + 1) % polygon.Count]);

        return total;
    }

    /// <summary>
    /// Area-weighted centre, which is where a tag belongs. The average of the corners would
    /// drift toward whichever end has more of them, and can land outside an L-shaped plan
    /// altogether.
    /// </summary>
    public static Point2D Centroid(IReadOnlyList<Point2D> polygon, Point2D fallback)
    {
        var area = SignedArea(polygon);
        if (Math.Abs(area) < 1e-6) return fallback;

        double x = 0, y = 0;

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var cross = a.X * b.Y - b.X * a.Y;

            x += (a.X + b.X) * cross;
            y += (a.Y + b.Y) * cross;
        }

        return new Point2D(x / (6 * area), y / (6 * area));
    }

    /// <summary>
    /// Splits a simple polygon into triangles by ear clipping, returned as index triples into
    /// the outline.
    ///
    /// Needed wherever an outline becomes a surface - the top of a floor slab, the cap of an
    /// extruded wall. It handles concave outlines, which a fan from the first corner would
    /// not: an L-shaped floor triangulated as a fan grows a triangle across the missing corner.
    /// </summary>
    public static IReadOnlyList<(int A, int B, int C)> Triangulate(IReadOnlyList<Point2D> polygon)
    {
        var triangles = new List<(int, int, int)>();
        if (polygon.Count < 3) return triangles;

        // Work anticlockwise, so "convex corner" always means a left turn.
        var indices = Enumerable.Range(0, polygon.Count).ToList();
        if (SignedArea(polygon) < 0) indices.Reverse();

        var guard = indices.Count * indices.Count;

        while (indices.Count > 3 && guard-- > 0)
        {
            var clipped = false;

            for (var i = 0; i < indices.Count; i++)
            {
                var previous = indices[(i + indices.Count - 1) % indices.Count];
                var current = indices[i];
                var next = indices[(i + 1) % indices.Count];

                if (!IsEar(polygon, indices, previous, current, next)) continue;

                triangles.Add((previous, current, next));
                indices.RemoveAt(i);
                clipped = true;
                break;
            }

            // A degenerate or self-touching outline can leave no clean ear. Rather than give up
            // and leave a hole in the surface, fan what remains: slightly wrong beats missing.
            if (!clipped) break;
        }

        for (var i = 1; i + 1 < indices.Count; i++)
            triangles.Add((indices[0], indices[i], indices[i + 1]));

        return triangles;
    }

    private static bool IsEar(IReadOnlyList<Point2D> polygon, List<int> indices, int a, int b, int c)
    {
        var pa = polygon[a];
        var pb = polygon[b];
        var pc = polygon[c];

        // A reflex or flat corner cannot be clipped off.
        if ((pb - pa).Cross(pc - pb) <= 1e-9) return false;

        foreach (var other in indices)
        {
            if (other == a || other == b || other == c) continue;
            if (InsideTriangle(polygon[other], pa, pb, pc)) return false;
        }

        return true;
    }

    private static bool InsideTriangle(Point2D p, Point2D a, Point2D b, Point2D c)
    {
        var d1 = (b - a).Cross(p - a);
        var d2 = (c - b).Cross(p - b);
        var d3 = (a - c).Cross(p - c);

        return d1 >= 0 && d2 >= 0 && d3 >= 0;
    }

    /// <summary>
    /// Whether the point falls inside the outline, by crossing count: a ray from the point
    /// crosses the outline an odd number of times exactly when the point is inside it.
    /// A point exactly on an edge may go either way, which is why picking a space traces
    /// the enclosure rather than asking this question.
    /// </summary>
    public static bool Contains(IReadOnlyList<Point2D> polygon, Point2D point)
    {
        if (polygon.Count < 3) return false;

        var inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];

            if (a.Y > point.Y != b.Y > point.Y &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
