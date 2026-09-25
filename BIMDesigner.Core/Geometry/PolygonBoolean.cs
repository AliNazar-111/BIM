namespace BIMDesigner.Core.Geometry;

/// <summary>Which way two shapes are combined.</summary>
public enum BooleanOperation
{
    /// <summary>Everything in either shape: A + B.</summary>
    Union,

    /// <summary>What is left of the first once the second is taken out of it: A - B.</summary>
    Difference,

    /// <summary>Only what is in both: A n B.</summary>
    Intersection
}

/// <summary>
/// Combining flat shapes: union, difference and intersection (specification section 16).
///
/// This is the engine a profile editor is built on. Drawing a shape and cutting another out of
/// it is one operation, not a special case: a notch in a column, a hole through it, and two
/// shapes joined into one are all the same arithmetic on the same outlines. Doing it here, on
/// plain outlines, means the same engine serves a column profile today and a sweep profile or
/// an edited wall elevation later, rather than each growing its own.
///
/// It works by cutting both outlines at every crossing, then keeping the pieces the operation
/// asks for - the pieces of A outside B, or inside it, and the same for B - and stitching those
/// pieces back into closed loops. Each loop that comes out clockwise is a hole in the one that
/// contains it, which is what lets a cut in the middle of a shape stay a hole rather than
/// becoming a separate shape.
/// </summary>
public static class PolygonBoolean
{
    /// <summary>Points closer than this are the same point, in millimetres.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>A result: one outer loop and the holes in it.</summary>
    public readonly record struct Region(IReadOnlyList<Point2D> Outer, IReadOnlyList<IReadOnlyList<Point2D>> Holes)
    {
        public double Area => Polygon2D.Area(Outer) - Holes.Sum(Polygon2D.Area);
    }

    /// <summary>
    /// Combines two shapes. Each is given as its loops - an outer one and any holes in it - and
    /// the result comes back the same way, as however many separate regions it takes.
    /// </summary>
    public static IReadOnlyList<Region> Combine(
        IReadOnlyList<IReadOnlyList<Point2D>> a,
        IReadOnlyList<IReadOnlyList<Point2D>> b,
        BooleanOperation operation)
    {
        var left = Wound(a);
        var right = Wound(b);

        if (left.Count == 0)
            return operation == BooleanOperation.Union ? AsRegions(right) : Array.Empty<Region>();

        if (right.Count == 0)
            return operation == BooleanOperation.Intersection ? Array.Empty<Region>() : AsRegions(left);

        // Every edge of each shape, cut wherever the other shape's edges cross it, so from here
        // on no edge is partly in and partly out: each one is wholly one or the other.
        var leftEdges = Split(left, right);
        var rightEdges = Split(right, left);

        var kept = new List<(Point2D From, Point2D To)>();

        // Edges the two shapes have exactly in common. Both were cut at the same crossings, so
        // a shared stretch comes out as the very same segment in each - which is what makes it
        // findable. Asking whether such an edge is "inside" the other shape has no answer, so
        // they are decided by which way each runs instead, before anything else is looked at.
        var onRight = new HashSet<(Ends, Ends)>();
        foreach (var edge in rightEdges) onRight.Add((Key(edge.From), Key(edge.To)));

        var settled = new HashSet<(Ends, Ends)>();

        foreach (var edge in leftEdges)
        {
            var forward = (Key(edge.From), Key(edge.To));
            var backward = (Key(edge.To), Key(edge.From));

            // Running the same way: the two shapes lie on the same side of it, so the edge is
            // on the boundary of both and one copy of it is kept - except for a difference,
            // where the second shape has covered the first along there.
            if (onRight.Contains(forward))
            {
                settled.Add(forward);
                if (operation != BooleanOperation.Difference) kept.Add(edge);
                continue;
            }

            // Running opposite ways: the shapes only touch along it, from either side. It is
            // inside a union and outside an intersection, and for a difference it is still the
            // first shape's own edge.
            if (onRight.Contains(backward))
            {
                settled.Add(backward);
                if (operation == BooleanOperation.Difference) kept.Add(edge);
                continue;
            }

            var inside = Inside(right, Middle(edge));
            var wanted = operation != BooleanOperation.Intersection ? !inside : inside;
            if (wanted) kept.Add(edge);
        }

        foreach (var edge in rightEdges)
        {
            // Already decided with its twin on the other shape.
            if (settled.Contains((Key(edge.From), Key(edge.To)))) continue;

            var inside = Inside(left, Middle(edge));

            switch (operation)
            {
                case BooleanOperation.Union when !inside:
                    kept.Add(edge);
                    break;

                // The second shape's edges inside the first become the walls of the hole it
                // leaves, so they run the other way round.
                case BooleanOperation.Difference when inside:
                    kept.Add((edge.To, edge.From));
                    break;

                case BooleanOperation.Intersection when inside:
                    kept.Add(edge);
                    break;
            }
        }

        return AsRegions(Stitch(kept));
    }

    /// <summary>Convenience for the common case: two plain outlines with no holes in either.</summary>
    public static IReadOnlyList<Region> Combine(
        IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b, BooleanOperation operation) =>
        Combine(new[] { a }, new[] { b }, operation);

    // ---- cutting the edges -----------------------------------------------------

    /// <summary>
    /// Every edge of one shape, cut at each point where an edge of the other crosses it. The
    /// crossings are collected first and sorted along the edge, so an edge crossed several
    /// times comes out as several pieces in order.
    /// </summary>
    private static List<(Point2D From, Point2D To)> Split(
        IReadOnlyList<IReadOnlyList<Point2D>> shape, IReadOnlyList<IReadOnlyList<Point2D>> other)
    {
        var pieces = new List<(Point2D, Point2D)>();

        foreach (var loop in shape)
        for (var i = 0; i < loop.Count; i++)
        {
            var from = loop[i];
            var to = loop[(i + 1) % loop.Count];
            var run = to - from;
            var length = run.Length;
            if (length <= Tolerance) continue;

            var cuts = new List<double>();

            foreach (var otherLoop in other)
            for (var j = 0; j < otherLoop.Count; j++)
            {
                if (Crossing(from, to, otherLoop[j], otherLoop[(j + 1) % otherLoop.Count]) is { } at)
                    cuts.Add(at);

                // A corner of the other shape sitting on this edge is a cut too, even though
                // nothing crosses there. Two edges that run along each other for part of their
                // length meet at no crossing at all, and without this they come out as one long
                // edge against two short ones - lengths that no longer match, so the stretch
                // they share cannot be recognised as shared. It is then decided by asking
                // whether the middle of an edge lying exactly on the other shape's boundary is
                // inside it, a question with no answer, and a face of the result goes missing.
                var corner = otherLoop[j];
                if (Line2D.DistanceFromSegment(corner, from, to) > Tolerance) continue;

                var along = (corner - from).Dot(run) / (length * length);
                if (along > 0 && along < 1) cuts.Add(along);
            }

            cuts.Add(0);
            cuts.Add(1);
            cuts.Sort();

            for (var k = 0; k + 1 < cuts.Count; k++)
            {
                if ((cuts[k + 1] - cuts[k]) * length <= Tolerance) continue;
                pieces.Add((from + run * cuts[k], from + run * cuts[k + 1]));
            }
        }

        return pieces;
    }

    /// <summary>How far along the first segment the two cross, or null where they do not.</summary>
    private static double? Crossing(Point2D a0, Point2D a1, Point2D b0, Point2D b1)
    {
        var a = a1 - a0;
        var b = b1 - b0;
        var denominator = a.Cross(b);

        // Parallel, or as near as makes no difference. Two edges running along each other have
        // no one crossing point, and a denominator this small invents one: dividing by it puts
        // the "crossing" at an arbitrary place along the edge, which then gets cut there. That
        // is how a face of a roof went missing - an edge cut at two different invented points,
        // leaving a chain with a gap in it that could not be closed. What such a pair really
        // shares is a stretch, and that is dealt with by cutting each at the other's corners.
        //
        // The test is against the lengths rather than against zero, because the cross product
        // of two long edges is large even when the angle between them is nothing at all.
        if (Math.Abs(denominator) <= a.Length * b.Length * 1e-9) return null;

        var offset = b0 - a0;
        var t = offset.Cross(b) / denominator;
        var u = offset.Cross(a) / denominator;

        // Strictly inside the first segment, and anywhere on the second: a crossing at an end
        // of this edge is already a corner, and cutting there would leave a zero-length piece.
        return t > 1e-9 && t < 1 - 1e-9 && u >= -1e-9 && u <= 1 + 1e-9 ? t : null;
    }

    private static Point2D Middle((Point2D From, Point2D To) edge) =>
        edge.From + (edge.To - edge.From) * 0.5;

    // ---- putting the pieces back together --------------------------------------

    /// <summary>
    /// Joins the kept edges into closed loops, following each one to whichever edge starts
    /// where it ended. An edge whose end meets nothing is dropped rather than left dangling.
    /// </summary>
    private static List<IReadOnlyList<Point2D>> Stitch(List<(Point2D From, Point2D To)> edges)
    {
        var starts = new Dictionary<Ends, List<int>>();
        var used = new bool[edges.Count];

        for (var i = 0; i < edges.Count; i++)
        {
            var key = Key(edges[i].From);
            if (!starts.TryGetValue(key, out var list)) starts[key] = list = new List<int>();
            list.Add(i);
        }

        var loops = new List<IReadOnlyList<Point2D>>();

        for (var i = 0; i < edges.Count; i++)
        {
            if (used[i]) continue;

            var loop = new List<Point2D>();
            var current = i;

            while (current >= 0 && !used[current])
            {
                used[current] = true;
                loop.Add(edges[current].From);

                var next = -1;
                if (starts.TryGetValue(Key(edges[current].To), out var candidates))
                    next = candidates.FirstOrDefault(index => !used[index], -1);

                // Closed: back where the loop began.
                if (next == i || (next < 0 && Key(edges[current].To) == Key(edges[i].From))) break;
                current = next;
            }

            if (loop.Count >= 3 && Polygon2D.Area(loop) > Tolerance) loops.Add(Clean(loop));
        }

        return loops;
    }

    /// <summary>A point reduced to a key, so two ends that meet are the same key.</summary>
    private readonly record struct Ends(long X, long Y);

    private static Ends Key(Point2D point) =>
        new Ends((long)Math.Round(point.X * 1e4), (long)Math.Round(point.Y * 1e4));

    /// <summary>Drops points that repeat and ones that lie on the line between their neighbours.</summary>
    private static IReadOnlyList<Point2D> Clean(IReadOnlyList<Point2D> loop)
    {
        var result = new List<Point2D>(loop.Count);

        foreach (var point in loop)
        {
            if (result.Count > 0 && result[^1].DistanceTo(point) <= Tolerance) continue;
            result.Add(point);
        }

        while (result.Count > 1 && result[0].DistanceTo(result[^1]) <= Tolerance) result.RemoveAt(result.Count - 1);

        // Straight-through corners carry no shape and only make later work harder.
        for (var i = result.Count - 1; i >= 0 && result.Count > 3; i--)
        {
            var before = result[(i - 1 + result.Count) % result.Count];
            var after = result[(i + 1) % result.Count];
            if (Math.Abs((result[i] - before).Cross(after - result[i])) < 1e-7) result.RemoveAt(i);
        }

        return result;
    }

    // ---- loops, holes and winding ----------------------------------------------

    /// <summary>
    /// The loops as regions: each anticlockwise loop is a shape, and each clockwise loop is a
    /// hole in the smallest shape that contains it - which is what a cut in the middle leaves.
    /// </summary>
    private static IReadOnlyList<Region> AsRegions(IReadOnlyList<IReadOnlyList<Point2D>> loops)
    {
        var outers = loops.Where(loop => Polygon2D.SignedArea(loop) > 0).ToList();
        var holes = loops.Where(loop => Polygon2D.SignedArea(loop) < 0).ToList();

        var regions = outers
            .OrderBy(Polygon2D.Area)
            .Select(outer => (Outer: outer, Holes: new List<IReadOnlyList<Point2D>>()))
            .ToList();

        foreach (var hole in holes)
        {
            var middle = hole[0];

            // The smallest shape it sits in owns it, so a hole inside an island inside a hole
            // belongs to the island.
            foreach (var region in regions)
            {
                if (!Polygon2D.Contains(region.Outer, middle)) continue;

                region.Holes.Add(hole);
                break;
            }
        }

        return regions
            .OrderByDescending(region => Polygon2D.Area(region.Outer))
            .Select(region => new Region(region.Outer, region.Holes))
            .ToList();
    }

    /// <summary>
    /// The shape's loops wound the way the engine expects: the outer one anticlockwise and its
    /// holes clockwise, so which side is inside never depends on how they were drawn.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<Point2D>> Wound(IReadOnlyList<IReadOnlyList<Point2D>> shape)
    {
        var loops = shape
            .Select(Clean)
            .Where(loop => loop.Count >= 3 && Polygon2D.Area(loop) > Tolerance)
            .ToList();

        if (loops.Count == 0) return loops;

        var biggest = loops.OrderByDescending(Polygon2D.Area).First();

        return loops
            .Select(loop =>
            {
                var anticlockwise = ReferenceEquals(loop, biggest) || !Polygon2D.Contains(biggest, loop[0]);
                var isAnticlockwise = Polygon2D.SignedArea(loop) > 0;
                return anticlockwise == isAnticlockwise ? loop : loop.Reverse().ToList();
            })
            .ToList();
    }

    /// <summary>Whether a point is inside a shape: in its outer loop and in none of its holes.</summary>
    public static bool Inside(IReadOnlyList<IReadOnlyList<Point2D>> shape, Point2D point)
    {
        var inside = false;

        foreach (var loop in shape)
        {
            if (!Polygon2D.Contains(loop, point)) continue;

            // An odd number of loops containing it means inside, which handles a hole inside a
            // hole without having to know which loop is which.
            inside = !inside;
        }

        return inside;
    }
}
