using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Views;

/// <summary>
/// Cutting a 3D solid with a section's plane: where the plane slices it, as closed outlines in
/// the section's own coordinates - distance along the cut line, and height. What is built in
/// 3D - a chimney with its flues and pots, a piece of furniture, a downpipe, a wall's studs - is
/// cut as it is built, so a section shows it as it is without a second description of its shape.
/// </summary>
public static class MeshSections
{
    /// <summary>How near the plane a corner may be before it is taken as just in front of it, mm: keeps a slice from catching on its own edges.</summary>
    private const double OnPlane = 1e-7;

    /// <summary>How close two ends of a slice are to be the same point, mm.</summary>
    private const double Join = 0.01;

    /// <summary>
    /// Where the cut plane slices a mesh, between the ends of the cut line: each piece an
    /// outline without holes - a void the plane goes through is cut round - with stacked
    /// rectangles, as a stack built in courses slices into, run together into one.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(double X, double Y)>> Cut(Mesh3D mesh, SectionMarker marker)
    {
        var positions = mesh.Positions;
        var indices = mesh.Indices;
        if (positions.Count == 0 || marker.Length <= 0) return Array.Empty<IReadOnlyList<(double, double)>>();

        double Depth(Point3D p)
        {
            var depth = marker.DepthOf(new Point2D(p.X, p.Y));
            return Math.Abs(depth) < OnPlane ? OnPlane : depth;
        }

        // Nothing either side of the plane, or nowhere along the line: nothing to cut.
        var depths = positions.Select(Depth).ToList();
        if (depths.Min() > 0 || depths.Max() < 0) return Array.Empty<IReadOnlyList<(double, double)>>();

        // Each triangle the plane passes through leaves a short line across it.
        var segments = new List<(Point2D A, Point2D B)>();
        var crossing = new List<Point2D>(2);
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            crossing.Clear();
            for (var edge = 0; edge < 3; edge++)
            {
                var (a, b) = (indices[i + edge], indices[i + (edge + 1) % 3]);
                var (da, db) = (depths[a], depths[b]);
                if (da > 0 == db > 0) continue;

                var (p, q) = (positions[a], positions[b]);
                var t = da / (da - db);
                var plan = new Point2D(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t);
                crossing.Add(new Point2D(marker.DistanceAlong(plan), p.Z + (q.Z - p.Z) * t));
            }

            if (crossing.Count == 2 && crossing[0].DistanceTo(crossing[1]) > Join) segments.Add((crossing[0], crossing[1]));
        }

        var loops = Chain(segments);
        if (loops.Count == 0) return Array.Empty<IReadOnlyList<(double, double)>>();

        // A loop inside an odd number of others is a void in the one round it.
        var regions = new List<PolygonBoolean.Region>();
        foreach (var loop in loops)
        {
            var round = loops.Where(other => !ReferenceEquals(other, loop) && Polygon2D.Area(other) > Polygon2D.Area(loop) && Polygon2D.Contains(other, loop[0])).ToList();
            if (round.Count % 2 == 1) continue;

            var holes = loops.Where(other => !ReferenceEquals(other, loop) && Polygon2D.Area(other) < Polygon2D.Area(loop) && Polygon2D.Contains(loop, other[0]) &&
                                             loops.Count(between => !ReferenceEquals(between, loop) && !ReferenceEquals(between, other) &&
                                                                     Polygon2D.Contains(loop, between[0]) && Polygon2D.Contains(between, other[0])) == 0)
                .ToList();
            regions.Add(new PolygonBoolean.Region(loop, holes));
        }

        // Only what lies along the cut line, between its ends.
        var low = loops.SelectMany(loop => loop).Min(point => point.Y) - 1;
        var high = loops.SelectMany(loop => loop).Max(point => point.Y) + 1;
        var line = new[] { new Point2D(0, low), new Point2D(marker.Length, low), new Point2D(marker.Length, high), new Point2D(0, high) };

        var pieces = new List<IReadOnlyList<Point2D>>();
        foreach (var region in regions)
        {
            // Already between the ends of the line, as nearly everything is: nothing to trim.
            if (region.Outer.Min(point => point.X) >= 0 && region.Outer.Max(point => point.X) <= marker.Length)
            {
                pieces.AddRange(region.Holes.Count == 0 ? new[] { region.Outer } : PolygonBoolean.WithoutHoles(region));
                continue;
            }

            var shape = new List<IReadOnlyList<Point2D>> { region.Outer };
            shape.AddRange(region.Holes);
            foreach (var kept in PolygonBoolean.Combine(shape, new[] { line }, BooleanOperation.Intersection))
                pieces.AddRange(kept.Holes.Count == 0 ? new[] { kept.Outer } : PolygonBoolean.WithoutHoles(kept));
        }

        // Upright rectangles - what a stack built in courses slices into - made one outline on a
        // grid; anything else as it is.
        var boxes = pieces.Select(Box).Where(box => box is not null).Select(box => box!.Value).ToList();
        var others = pieces.Where(piece => Box(piece) is null);
        return BoxUnion(boxes).Concat(others)
            .Select(piece => (IReadOnlyList<(double X, double Y)>)piece.Select(point => (point.X, point.Y)).ToList())
            .ToList();
    }

    /// <summary>
    /// Upright rectangles that touch made one outline each - quickly, however many there are: as
    /// cells of a grid through all their sides, the outline traced round the cells they fill.
    /// One round a void is cut across it again to have no hole.
    /// </summary>
    private static List<IReadOnlyList<Point2D>> BoxUnion(List<(double Left, double Bottom, double Right, double Top)> boxes)
    {
        if (boxes.Count == 0) return new List<IReadOnlyList<Point2D>>();

        static List<double> Lines(IEnumerable<double> values)
        {
            var lines = new List<double>();
            foreach (var value in values.OrderBy(value => value))
                if (lines.Count == 0 || value - lines[^1] > 0.5) lines.Add(value);
            return lines;
        }

        static int At(List<double> lines, double value)
        {
            var best = 0;
            for (var i = 1; i < lines.Count; i++)
                if (Math.Abs(lines[i] - value) < Math.Abs(lines[best] - value)) best = i;
            return best;
        }

        var xs = Lines(boxes.SelectMany(box => new[] { box.Left, box.Right }));
        var ys = Lines(boxes.SelectMany(box => new[] { box.Bottom, box.Top }));
        var (columns, rows) = (xs.Count - 1, ys.Count - 1);
        if (columns < 1 || rows < 1) return new List<IReadOnlyList<Point2D>>();

        var filled = new bool[columns, rows];
        foreach (var box in boxes)
            for (var i = At(xs, box.Left); i < At(xs, box.Right); i++)
            for (var j = At(ys, box.Bottom); j < At(ys, box.Top); j++)
                filled[i, j] = true;

        bool Filled(int i, int j) => i >= 0 && j >= 0 && i < columns && j < rows && filled[i, j];

        // The sides of the filled cells with an empty one beyond, each run with the filled cell
        // on its left, so the outline goes anticlockwise round the solid and clockwise round a void.
        var next = new Dictionary<(int, int), List<(int, int)>>();
        void Side((int, int) from, (int, int) to) => (next.TryGetValue(from, out var list) ? list : next[from] = new List<(int, int)>()).Add(to);
        for (var i = 0; i < columns; i++)
        for (var j = 0; j < rows; j++)
        {
            if (!filled[i, j]) continue;
            if (!Filled(i, j - 1)) Side((i, j), (i + 1, j));
            if (!Filled(i + 1, j)) Side((i + 1, j), (i + 1, j + 1));
            if (!Filled(i, j + 1)) Side((i + 1, j + 1), (i, j + 1));
            if (!Filled(i - 1, j)) Side((i, j + 1), (i, j));
        }

        var loops = new List<List<Point2D>>();
        while (next.Count > 0)
        {
            var start = next.Keys.First();
            var loop = new List<Point2D>();
            var at = start;
            while (next.TryGetValue(at, out var ways) && ways.Count > 0)
            {
                loop.Add(new Point2D(xs[at.Item1], ys[at.Item2]));
                var to = ways[0];
                ways.RemoveAt(0);
                if (ways.Count == 0) next.Remove(at);
                at = to;
                if (at == start) break;
            }

            var outline = Simplified(loop);
            if (outline.Count >= 3) loops.Add(outline);
        }

        // Each void inside the solid round it.
        var outers = loops.Where(loop => Polygon2D.SignedArea(loop) > 0).ToList();
        var voids = loops.Where(loop => Polygon2D.SignedArea(loop) < 0).ToList();
        var result = new List<IReadOnlyList<Point2D>>();
        foreach (var outer in outers)
        {
            var holes = voids.Where(hole => Polygon2D.Contains(outer, hole[0]) &&
                                            !outers.Any(inner => !ReferenceEquals(inner, outer) && Polygon2D.Area(inner) < Polygon2D.Area(outer) &&
                                                                 Polygon2D.Contains(outer, inner[0]) && Polygon2D.Contains(inner, hole[0])))
                .Select(hole => (IReadOnlyList<Point2D>)hole).ToList();
            result.AddRange(holes.Count == 0 ? new[] { outer } : PolygonBoolean.WithoutHoles(new PolygonBoolean.Region(outer, holes)));
        }

        return result;
    }

    /// <summary>The short lines across the triangles joined end to end into closed loops.</summary>
    private static List<IReadOnlyList<Point2D>> Chain(List<(Point2D A, Point2D B)> segments)
    {
        // Ends that are the same point, found through a grid of millimetre cells.
        var points = new List<Point2D>();
        var cells = new Dictionary<(long, long), List<int>>();
        int Index(Point2D point)
        {
            var (cx, cy) = ((long)Math.Floor(point.X), (long)Math.Floor(point.Y));
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (cells.TryGetValue((cx + dx, cy + dy), out var near))
                    foreach (var at in near)
                        if (points[at].DistanceTo(point) <= Join) return at;

            points.Add(point);
            if (!cells.TryGetValue((cx, cy), out var list)) cells[(cx, cy)] = list = new List<int>();
            list.Add(points.Count - 1);
            return points.Count - 1;
        }

        var links = new Dictionary<int, List<int>>();
        var unused = new HashSet<(int, int)>();
        foreach (var (a, b) in segments)
        {
            var (i, j) = (Index(a), Index(b));
            if (i == j || !unused.Add((Math.Min(i, j), Math.Max(i, j)))) continue;
            (links.TryGetValue(i, out var li) ? li : links[i] = new List<int>()).Add(j);
            (links.TryGetValue(j, out var lj) ? lj : links[j] = new List<int>()).Add(i);
        }

        var loops = new List<IReadOnlyList<Point2D>>();
        while (unused.Count > 0)
        {
            var (first, second) = unused.First();
            unused.Remove((first, second));
            var loop = new List<int> { first, second };
            var (previous, current) = (first, second);
            while (current != first)
            {
                var next = links[current].FirstOrDefault(candidate => candidate != previous && unused.Contains((Math.Min(current, candidate), Math.Max(current, candidate))), -1);
                if (next < 0) break;
                unused.Remove((Math.Min(current, next), Math.Max(current, next)));
                if (next == first) break;
                loop.Add(next);
                (previous, current) = (current, next);
            }

            if (loop.Count >= 3)
            {
                var outline = Simplified(loop.Select(index => points[index]).ToList());
                if (outline.Count >= 3 && Polygon2D.Area(outline) > 1) loops.Add(outline);
            }
        }

        return loops;
    }

    /// <summary>A loop without the corners on a straight run, which a slice through a band's many triangles leaves.</summary>
    private static List<Point2D> Simplified(List<Point2D> loop)
    {
        var kept = new List<Point2D>();
        for (var i = 0; i < loop.Count; i++)
        {
            var (into, outOf) = (loop[i] - loop[(i + loop.Count - 1) % loop.Count], loop[(i + 1) % loop.Count] - loop[i]);
            if (into.Length < Join || outOf.Length < Join) continue;
            if (Math.Abs(into.Cross(outOf)) > 1e-6 * into.Length * outOf.Length || into.Dot(outOf) < 0) kept.Add(loop[i]);
        }

        return kept;
    }

    /// <summary>Whether a piece is an upright rectangle: its left, bottom, right and top.</summary>
    private static (double Left, double Bottom, double Right, double Top)? Box(IReadOnlyList<Point2D> piece)
    {
        if (piece.Count != 4) return null;
        var (left, right) = (piece.Min(p => p.X), piece.Max(p => p.X));
        var (bottom, top) = (piece.Min(p => p.Y), piece.Max(p => p.Y));
        return piece.All(p => (Math.Abs(p.X - left) < Join || Math.Abs(p.X - right) < Join) && (Math.Abs(p.Y - bottom) < Join || Math.Abs(p.Y - top) < Join))
            ? (left, bottom, right, top)
            : null;
    }
}
