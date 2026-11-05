using System.Runtime.CompilerServices;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>A point of a flat roof's top raised - or lowered - from where it is built: a corner of it, or one added inside it.</summary>
public sealed record RoofShapePoint(Point2D At, double Rise);

/// <summary>A line across a flat roof's top between two of its points: a ridge or valley its faces are made to meet along.</summary>
public sealed record RoofSplitLine(Point2D From, Point2D To);

/// <summary>
/// Shape editing of a flat roof (Revit's Modify Sub Elements, Add Point, Add Split Line, Reset Shape),
/// for the falls a flat roof is laid to by hand rather than by its drains: its corners and points added
/// inside it raised or lowered, lines added between them where its faces should meet. The top is the
/// surface through those points - every corner not raised staying where it is built - in flat triangles,
/// each split line an edge of them; the layer the falls are made in, the insulation, is thicker or
/// thinner to match, the deck under it flat, as tapered insulation is laid. Shaped, a roof's falls are
/// its own and its drains no longer set them.
/// </summary>
public static class RoofShapeEditing
{
    /// <summary>How near a point must be to a corner to be it, mm.</summary>
    public const double Tolerance = 5;

    /// <summary>Whether a roof's shape has been edited: a point of it raised or lowered.</summary>
    public static bool Shaped(Roof roof) => roof.ShapePoints.Any(point => Math.Abs(point.Rise) > 1e-6);

    /// <summary>Whether a roof can be shape edited: a flat one drawn by footprint.</summary>
    public static bool CanShape(BimDocument document, Roof roof) => RoofDrainage.CanDrain(document, roof);

    /// <summary>The points the top is made through: every corner, at its rise or none, then the points added inside, with their rises.</summary>
    public static IReadOnlyList<RoofShapePoint> Vertices(Roof roof)
    {
        var vertices = new List<RoofShapePoint>();
        double RiseAt(Point2D corner) => roof.ShapePoints.FirstOrDefault(point => point.At.DistanceTo(corner) <= Tolerance)?.Rise ?? 0;
        foreach (var corner in roof.Boundary.Concat(roof.Holes.SelectMany(hole => hole)))
            vertices.Add(new RoofShapePoint(corner, RiseAt(corner)));
        foreach (var point in roof.ShapePoints)
            if (!vertices.Any(vertex => vertex.At.DistanceTo(point.At) <= Tolerance) && (roof.Contains(point.At) || OnOutline(roof, point.At)))
                vertices.Add(point);
        return vertices;
    }

    /// <summary>Whether a point is on the roof's outline or a hole's - along an edge, not only at a corner.</summary>
    public static bool OnOutline(Roof roof, Point2D at) =>
        new[] { roof.Boundary }.Concat(roof.Holes).Any(loop => Enumerable.Range(0, loop.Count).Any(i => Line2D.DistanceFromSegment(at, loop[i], loop[(i + 1) % loop.Count]) <= Tolerance));

    private static readonly ConditionalWeakTable<Roof, Tuple<string, IReadOnlyList<Face>>> Kept = new();

    /// <summary>One flat face of a shaped top: its outline, and its rise over the built top, rise = A x + B y + C.</summary>
    public sealed record Face(IReadOnlyList<Point2D> Outline, double A, double B, double C)
    {
        public double Rise(Point2D point) => A * point.X + B * point.Y + C;

        /// <summary>The way down it in plan; none where it is level.</summary>
        public Vector2D Downhill => new Vector2D(-A, -B).NormalisedOrDefault(new Vector2D(0, 0));
    }

    /// <summary>The faces of a shaped roof's top: flat triangles through its points, its split lines among their edges.</summary>
    public static IReadOnlyList<Face> Faces(Roof roof)
    {
        if (!Shaped(roof) || roof.Boundary.Count < 3) return Array.Empty<Face>();

        var key = string.Join(";", roof.Boundary.Concat(roof.Holes.SelectMany(hole => hole)).Select(point => $"{point.X:0.#},{point.Y:0.#}")) + "|" +
                  string.Join(";", roof.ShapePoints.Select(point => $"{point.At.X:0.#},{point.At.Y:0.#},{point.Rise:0.##}")) + "|" +
                  string.Join(";", roof.SplitLines.Select(line => $"{line.From.X:0.#},{line.From.Y:0.#},{line.To.X:0.#},{line.To.Y:0.#}"));
        if (Kept.TryGetValue(roof, out var kept) && kept.Item1 == key) return kept.Item2;

        var faces = Work(roof);
        Kept.AddOrUpdate(roof, Tuple.Create(key, faces));
        return faces;
    }

    private static IReadOnlyList<Face> Work(Roof roof)
    {
        var vertices = Vertices(roof);
        var points = vertices.Select(vertex => vertex.At).ToList();
        int Index(Point2D at) => points.FindIndex(point => point.DistanceTo(at) <= Tolerance);

        // What must be an edge: the outline's and holes' sides, and each split line.
        var constraints = new List<(int, int)>();
        // Each side of a loop, broken at the points put on it.
        void Loop(IReadOnlyList<Point2D> loop)
        {
            for (var i = 0; i < loop.Count; i++)
            {
                var (from, to) = (loop[i], loop[(i + 1) % loop.Count]);
                var along = points.Where(point => point.DistanceTo(from) > Tolerance && point.DistanceTo(to) > Tolerance && Line2D.DistanceFromSegment(point, from, to) <= Tolerance)
                    .OrderBy(point => point.DistanceTo(from)).Prepend(from).Append(to).ToList();
                for (var k = 0; k + 1 < along.Count; k++)
                    if (Index(along[k]) is var a and >= 0 && Index(along[k + 1]) is var b and >= 0 && a != b)
                        constraints.Add((a, b));
            }
        }

        Loop(roof.Boundary);
        foreach (var hole in roof.Holes) Loop(hole);
        foreach (var line in roof.SplitLines)
            if (Index(line.From) is var a and >= 0 && Index(line.To) is var b and >= 0 && a != b)
                constraints.Add((a, b));

        var faces = new List<Face>();
        foreach (var (a, b, c) in Triangulation.Of(points, constraints))
        {
            var outline = new[] { points[a], points[b], points[c] };
            var centre = new Point2D(outline.Average(point => point.X), outline.Average(point => point.Y));
            if (!roof.Contains(centre) || Math.Abs(Polygon2D.SignedArea(outline)) < 1) continue;
            if (Plane(outline[0], vertices[a].Rise, outline[1], vertices[b].Rise, outline[2], vertices[c].Rise) is not var (pa, pb, pc)) continue;
            faces.Add(new Face(Polygon2D.SignedArea(outline) >= 0 ? outline : outline.Reverse().ToArray(), pa, pb, pc));
        }

        return faces;
    }

    /// <summary>The plane through three points and their rises.</summary>
    private static (double A, double B, double C)? Plane(Point2D p, double zp, Point2D q, double zq, Point2D r, double zr)
    {
        var (u, v) = (q - p, r - p);
        var det = u.X * v.Y - u.Y * v.X;
        if (Math.Abs(det) < 1e-9) return null;
        var (du, dv) = (zq - zp, zr - zp);
        var a = (du * v.Y - dv * u.Y) / det;
        var b = (u.X * dv - v.X * du) / det;
        return (a, b, zp - a * p.X - b * p.Y);
    }

    /// <summary>How far a shaped roof's top is raised over a point: by the face it is in; none off it.</summary>
    public static double RiseAt(Roof roof, Point2D at)
    {
        foreach (var face in Faces(roof))
            if (Polygon2D.Contains(face.Outline, at) || face.Outline.Any(corner => corner.DistanceTo(at) <= Tolerance))
                return face.Rise(at);

        // On an edge between faces, or just outside the roof: the nearest face's.
        var nearest = Faces(roof).OrderBy(face => Enumerable.Range(0, 3).Min(i => Line2D.DistanceFromSegment(at, face.Outline[i], face.Outline[(i + 1) % 3]))).FirstOrDefault();
        return nearest?.Rise(at) ?? 0;
    }

    /// <summary>The point of a shaped roof nearest a point - a corner or one added - within reach, or null.</summary>
    public static RoofShapePoint? PointNear(Roof roof, Point2D at, double reach) =>
        Vertices(roof).Where(vertex => vertex.At.DistanceTo(at) <= reach).OrderBy(vertex => vertex.At.DistanceTo(at)).FirstOrDefault();

    /// <summary>A roof's shape - its points and split lines - changed as one step to undo.</summary>
    public static IUndoableCommand Change(Roof roof, string name, IEnumerable<RoofShapePoint> points, IEnumerable<RoofSplitLine> lines)
    {
        var (oldPoints, oldLines) = (roof.ShapePoints.ToList(), roof.SplitLines.ToList());
        var (newPoints, newLines) = (points.ToList(), lines.ToList());
        return new Views.DelegateCommand(name, () => Set(roof, newPoints, newLines), () => Set(roof, oldPoints, oldLines));
    }

    private static void Set(Roof roof, List<RoofShapePoint> points, List<RoofSplitLine> lines)
    {
        roof.ShapePoints.Clear();
        roof.ShapePoints.AddRange(points);
        roof.SplitLines.Clear();
        roof.SplitLines.AddRange(lines);
    }

    /// <summary>A point of the roof - a corner, or one inside it, added where it is not one - raised to a rise, as one step.</summary>
    public static IUndoableCommand? Raise(Roof roof, Point2D at, double rise)
    {
        if (!roof.Contains(at) && !OnOutline(roof, at)) return null;
        var points = roof.ShapePoints.Where(point => point.At.DistanceTo(at) > Tolerance).Append(new RoofShapePoint(at, rise)).ToList();
        return Change(roof, rise == 0 ? "Reset Point" : "Raise Point", points, roof.SplitLines);
    }

    /// <summary>A split line between two of the roof's points, as one step; null where either end is not one, or it is there already.</summary>
    public static IUndoableCommand? Split(Roof roof, Point2D from, Point2D to)
    {
        // Each end one of the roof's points, or a new one anywhere on it at the height the roof is there - so the line changes nothing until it is raised.
        var vertices = Vertices(roof);
        RoofShapePoint? End(Point2D at) => vertices.FirstOrDefault(vertex => vertex.At.DistanceTo(at) <= Tolerance)
                                           ?? (roof.Contains(at) || OnOutline(roof, at) ? new RoofShapePoint(at, Shaped(roof) ? RiseAt(roof, at) : 0) : null);
        var a = End(from);
        var b = End(to);
        if (a is null || b is null || a.At.DistanceTo(b.At) <= Tolerance) return null;
        if (roof.SplitLines.Any(line => line.From.DistanceTo(a.At) <= Tolerance && line.To.DistanceTo(b.At) <= Tolerance ||
                                        line.From.DistanceTo(b.At) <= Tolerance && line.To.DistanceTo(a.At) <= Tolerance)) return null;

        // A point split to that is not yet one of the roof's own is kept, level, so the line stays.
        var points = roof.ShapePoints.ToList();
        foreach (var end in new[] { a, b })
            if (!points.Any(point => point.At.DistanceTo(end.At) <= Tolerance) && !roof.Boundary.Any(corner => corner.DistanceTo(end.At) <= Tolerance))
                points.Add(end);
        return Change(roof, "Add Split Line", points, roof.SplitLines.Append(new RoofSplitLine(a.At, b.At)));
    }

    /// <summary>The roof's top put back as built: no point raised, no split line - Reset Shape.</summary>
    public static IUndoableCommand? Reset(Roof roof) =>
        roof.ShapePoints.Count == 0 && roof.SplitLines.Count == 0 ? null : Change(roof, "Reset Shape", Array.Empty<RoofShapePoint>(), Array.Empty<RoofSplitLine>());
}

/// <summary>
/// A triangulation of points in plan with edges it must have: Delaunay's by Bowyer and Watson, then
/// each edge it must have but lacks recovered by flipping the edges that cross it (Sloan, 1993).
/// </summary>
public static class Triangulation
{
    public static IReadOnlyList<(int A, int B, int C)> Of(IReadOnlyList<Point2D> points, IReadOnlyList<(int A, int B)> constraints)
    {
        var n = points.Count;
        if (n < 3) return Array.Empty<(int, int, int)>();

        // A triangle round them all to start from.
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var span = Math.Max(points.Max(p => p.X) - minX, points.Max(p => p.Y) - minY) + 1;
        var all = points.ToList();
        all.Add(new Point2D(minX - 20 * span, minY - 20 * span));
        all.Add(new Point2D(minX + 40 * span, minY - 20 * span));
        all.Add(new Point2D(minX + span / 2, minY + 40 * span));
        var triangles = new List<int[]> { new[] { n, n + 1, n + 2 } };

        for (var i = 0; i < n; i++)
        {
            var p = all[i];
            var bad = triangles.Where(t => InCircle(all[t[0]], all[t[1]], all[t[2]], p)).ToList();
            var edges = new List<(int, int)>();
            foreach (var t in bad)
                for (var k = 0; k < 3; k++)
                {
                    var (a, b) = (t[k], t[(k + 1) % 3]);
                    var shared = bad.Any(other => other != t && HasEdge(other, a, b));
                    if (!shared) edges.Add((a, b));
                }

            triangles.RemoveAll(t => bad.Contains(t));
            foreach (var (a, b) in edges) triangles.Add(Anticlockwise(all, a, b, i));
        }

        // Each edge it must have, recovered.
        foreach (var (a, b) in constraints)
            for (var guard = 0; guard < 500 && !triangles.Any(t => HasEdge(t, a, b)); guard++)
            {
                var flipped = false;
                foreach (var t in triangles.ToList())
                {
                    for (var k = 0; k < 3 && !flipped; k++)
                    {
                        var (u, v) = (t[k], t[(k + 1) % 3]);
                        if (u == a || u == b || v == a || v == b || !Crosses(all[a], all[b], all[u], all[v])) continue;
                        var other = triangles.FirstOrDefault(o => o != t && HasEdge(o, u, v));
                        if (other is null) continue;
                        var w = t.First(x => x != u && x != v);
                        var z = other.First(x => x != u && x != v);
                        if (!Convex(all[u], all[w], all[v], all[z])) continue;
                        triangles.Remove(t);
                        triangles.Remove(other);
                        triangles.Add(Anticlockwise(all, w, z, u));
                        triangles.Add(Anticlockwise(all, z, w, v));
                        flipped = true;
                    }

                    if (flipped) break;
                }

                if (!flipped) break;
            }

        return triangles.Where(t => t.All(index => index < n)).Select(t => (t[0], t[1], t[2])).ToList();
    }

    private static bool HasEdge(int[] t, int a, int b) => t.Contains(a) && t.Contains(b);

    private static int[] Anticlockwise(IReadOnlyList<Point2D> all, int a, int b, int c) =>
        (all[b] - all[a]).Cross(all[c] - all[a]) >= 0 ? new[] { a, b, c } : new[] { a, c, b };

    private static bool InCircle(Point2D a, Point2D b, Point2D c, Point2D p)
    {
        if ((b - a).Cross(c - a) < 0) (b, c) = (c, b);
        double ax = a.X - p.X, ay = a.Y - p.Y, bx = b.X - p.X, by = b.Y - p.Y, cx = c.X - p.X, cy = c.Y - p.Y;
        return (ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay) + (cx * cx + cy * cy) * (ax * by - bx * ay) > 1e-9;
    }

    private static bool Crosses(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        double Side(Point2D p, Point2D q, Point2D r) => (q - p).Cross(r - p);
        return Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;
    }

    /// <summary>Whether the four points, in order round, make a convex quadrilateral - so its diagonal can be flipped.</summary>
    private static bool Convex(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        var s = new[] { (b - a).Cross(c - b), (c - b).Cross(d - c), (d - c).Cross(a - d), (a - d).Cross(b - a) };
        return s.All(x => x > 1e-9) || s.All(x => x < -1e-9);
    }
}
