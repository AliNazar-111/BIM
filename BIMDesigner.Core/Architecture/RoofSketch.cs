using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One line of a roof sketch: where it runs, and what the roof does there.
///
/// A sketch is Revit's way of making anything whose shape cannot be guessed - a roof, above
/// all, which is rarely the shape of one room: it covers several, overhangs the walls, stops
/// at a parapet. The lines live outside the model while they are drawn, and only become a roof
/// when the sketch is finished and found to close.
/// </summary>
public sealed class RoofSketchLine
{
    public RoofSketchLine(Point2D start, Point2D end, RoofEdge edge)
    {
        Start = start;
        End = end;
        Edge = edge;
    }

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    /// <summary>What the roof does along this line: its slope, its plate, the wall it follows.</summary>
    public RoofEdge Edge { get; }

    public double Length => Start.DistanceTo(End);

    public RoofSketchLine Copy() => new(Start, End, Edge.Copy());
}

/// <summary>What finishing a sketch came to: the roof's outline and edges, or why it cannot be one.</summary>
public sealed class RoofSketchCheck
{
    private RoofSketchCheck(
        IReadOnlyList<Point2D> boundary, IReadOnlyList<RoofEdge> edges,
        string? problem, IReadOnlyList<RoofSketchLine> culprits)
    {
        Boundary = boundary;
        Edges = edges;
        Problem = problem;
        Culprits = culprits;
    }

    /// <summary>The closed outline, anticlockwise.</summary>
    public IReadOnlyList<Point2D> Boundary { get; }

    /// <summary>One per outline segment, in the outline's order.</summary>
    public IReadOnlyList<RoofEdge> Edges { get; }

    /// <summary>What is wrong, in words that say what to do about it; null when the sketch is good.</summary>
    public string? Problem { get; }

    /// <summary>The lines to point at, so the user sees where the problem is rather than hunting for it.</summary>
    public IReadOnlyList<RoofSketchLine> Culprits { get; }

    public bool IsValid => Problem is null;

    internal static RoofSketchCheck Loop(IReadOnlyList<Point2D> boundary, IReadOnlyList<RoofEdge> edges) =>
        new(boundary, edges, null, Array.Empty<RoofSketchLine>());

    internal static RoofSketchCheck Refused(string problem, params RoofSketchLine[] culprits) =>
        new(Array.Empty<Point2D>(), Array.Empty<RoofEdge>(), problem, culprits);
}

/// <summary>
/// Roof sketches (specification section 3.3; Revit's Roof by Footprint): putting a line on a
/// wall, closing corners between lines, checking that a sketch closes into a roof, and keeping
/// a finished roof on the walls it was picked from.
/// </summary>
public static class RoofSketch
{
    /// <summary>Line ends this close are the same corner, in millimetres.</summary>
    public const double JoinTolerance = 1;

    // ---- picking walls ------------------------------------------------------------

    /// <summary>
    /// Which face of a wall a cursor is nearer: the one to the left of the wall's drawn
    /// direction, or the right. Revit puts a picked line on the side the cursor is on, so
    /// hovering outside a wall gives the outside face - which is the one a roof wants.
    /// </summary>
    public static bool IsLeftOf(Wall wall, Point2D cursor)
    {
        var (_, left) = wall.LocationCurve.Locate(cursor);
        return left >= 0;
    }

    /// <summary>
    /// Where a roof line picked from a wall runs: along the wall's face on the chosen side -
    /// or its core face - and out from it by the overhang. Null for a wall that cannot carry
    /// one: a curved wall (roof outlines are straight for now) or one with no type.
    /// </summary>
    public static (Point2D Start, Point2D End)? LineOnWall(
        BimDocument document, Wall wall, bool onLeft, double overhang, bool extendToCore)
    {
        if (wall.IsCurved || wall.Length <= WallJoins.JoinTolerance) return null;
        if (document.GetWallType(wall) is not { } type) return null;

        var structure = type.Structure;
        var half = structure.TotalWidth / 2;

        // Across the wall body, positive toward its exterior. The left face is the exterior
        // unless the wall has been flipped.
        var exteriorOnLeft = !wall.Flipped;
        var toExterior = onLeft == exteriorOnLeft;

        var face = toExterior ? half : -half;
        if (extendToCore) face = toExterior ? half - structure.ExteriorWidth : -half + structure.InteriorWidth;

        var across = face + (toExterior ? overhang : -overhang);

        return (wall.PointAt(structure, 0, across), wall.PointAt(structure, wall.Length, across));
    }

    /// <summary>
    /// A roof line for a wall, carrying the settings it was picked with. The wall is
    /// remembered, so the finished roof edge follows it.
    /// </summary>
    public static RoofSketchLine? PickWall(
        BimDocument document, Wall wall, Point2D cursor,
        double overhang, bool extendToCore, bool definesSlope, double slopeDegrees)
    {
        var onLeft = IsLeftOf(wall, cursor);
        if (LineOnWall(document, wall, onLeft, overhang, extendToCore) is not var (start, end)) return null;

        return new RoofSketchLine(start, end, new RoofEdge
        {
            DefinesSlope = definesSlope,
            SlopeDegrees = slopeDegrees,
            WallId = wall.Id,
            OnLeftOfWall = onLeft,
            Overhang = overhang,
            ExtendToCore = extendToCore
        });
    }

    // ---- closing corners ----------------------------------------------------------

    /// <summary>
    /// Runs a new line's ends out, or cuts them back, to meet the lines already in the sketch
    /// at the corners they are making - what Revit does when the walls picked are joined.
    ///
    /// Lines picked off the faces of two walls meeting at a corner stop short of each other or
    /// run past each other by the wall's thickness and the overhang; left like that the sketch
    /// would not close, and the user would have to trim every corner by hand. Each end is
    /// joined to the nearest end of another line within reach, at the point where the two
    /// lines cross, so a box of four picked walls closes into a loop by itself.
    /// </summary>
    public static void CloseCorners(IList<RoofSketchLine> lines, RoofSketchLine added, double reach)
    {
        foreach (var atStart in new[] { true, false })
        {
            var end = atStart ? added.Start : added.End;

            RoofSketchLine? partner = null;
            var partnerAtStart = false;
            var nearest = reach;

            foreach (var other in lines)
            {
                if (ReferenceEquals(other, added)) continue;

                foreach (var otherAtStart in new[] { true, false })
                {
                    var point = otherAtStart ? other.Start : other.End;
                    var distance = point.DistanceTo(end);
                    if (distance >= nearest) continue;

                    // An end already joined to a third line is a finished corner: leave it.
                    if (IsJoined(lines, other, otherAtStart, added)) continue;

                    nearest = distance;
                    partner = other;
                    partnerAtStart = otherAtStart;
                }
            }

            if (partner is null) continue;

            var corner = Crossing(added.Start, added.End, partner.Start, partner.End)
                         ?? (partnerAtStart ? partner.Start : partner.End);

            if (atStart) added.Start = corner;
            else added.End = corner;

            if (partnerAtStart) partner.Start = corner;
            else partner.End = corner;
        }
    }

    private static bool IsJoined(IList<RoofSketchLine> lines, RoofSketchLine line, bool atStart, RoofSketchLine except)
    {
        var point = atStart ? line.Start : line.End;

        return lines.Any(other => !ReferenceEquals(other, line) && !ReferenceEquals(other, except) &&
                                  (other.Start.DistanceTo(point) <= JoinTolerance ||
                                   other.End.DistanceTo(point) <= JoinTolerance));
    }

    /// <summary>Where two lines, taken as running on for ever, cross; null when they are parallel.</summary>
    private static Point2D? Crossing(Point2D a0, Point2D a1, Point2D b0, Point2D b1)
    {
        var a = a1 - a0;
        var b = b1 - b0;
        var denominator = a.Cross(b);
        if (Math.Abs(denominator) <= a.Length * b.Length * 1e-9) return null;

        var t = (b0 - a0).Cross(b) / denominator;
        return a0 + a * t;
    }

    // ---- checking a sketch --------------------------------------------------------

    /// <summary>
    /// Turns a sketch into a roof outline, or says why it cannot be one.
    ///
    /// A roof needs one closed loop: every line end meeting exactly one other, no line
    /// crossing another. Revit also takes loops inside the outline as openings; those are
    /// refused for now, with a message saying so rather than a roof that quietly ignores them.
    /// </summary>
    public static RoofSketchCheck Check(IReadOnlyList<RoofSketchLine> sketch)
    {
        var lines = sketch.Where(line => line.Length > JoinTolerance).ToList();

        if (lines.Count < 3)
            return RoofSketchCheck.Refused(
                "A roof needs at least three lines closing round its outline. Pick the walls it sits on, or draw its edges.");

        // Every end must meet exactly one other end.
        foreach (var line in lines)
        {
            foreach (var point in new[] { line.Start, line.End })
            {
                var meeting = lines.Count(other => !ReferenceEquals(other, line) &&
                                                   (other.Start.DistanceTo(point) <= JoinTolerance ||
                                                    other.End.DistanceTo(point) <= JoinTolerance));

                if (meeting == 0)
                    return RoofSketchCheck.Refused(
                        "The outline is open at the end of the highlighted line. Join it to the next line - draw the missing piece, or trim the two to meet.",
                        line);

                if (meeting > 1)
                    return RoofSketchCheck.Refused(
                        "More than two lines meet at the end of the highlighted line. A roof outline meets itself only once at each corner; delete the extra line.",
                        line);
            }
        }

        // No two lines may cross - an outline that crosses itself has no inside.
        for (var i = 0; i < lines.Count; i++)
        for (var j = i + 1; j < lines.Count; j++)
        {
            if (Crosses(lines[i], lines[j]))
                return RoofSketchCheck.Refused(
                    "The highlighted lines cross. A roof outline cannot cross itself: trim them to meet at a corner instead.",
                    lines[i], lines[j]);
        }

        // Walk round from the first line, one corner at a time.
        var loop = new List<(RoofSketchLine Line, bool Forward)>();
        var used = new HashSet<RoofSketchLine>();
        var current = lines[0];
        var forward = true;

        while (current is not null && used.Add(current))
        {
            loop.Add((current, forward));

            var at = forward ? current.End : current.Start;
            RoofSketchLine? next = null;

            foreach (var other in lines)
            {
                if (ReferenceEquals(other, current) || used.Contains(other) && !ReferenceEquals(other, lines[0])) continue;

                if (other.Start.DistanceTo(at) <= JoinTolerance)
                {
                    next = other;
                    forward = true;
                    break;
                }

                if (other.End.DistanceTo(at) <= JoinTolerance)
                {
                    next = other;
                    forward = false;
                    break;
                }
            }

            if (next is null || ReferenceEquals(next, lines[0])) break;
            current = next;
        }

        if (loop.Count < lines.Count)
        {
            var left = lines.Where(line => !used.Contains(line)).ToArray();
            return RoofSketchCheck.Refused(
                "The sketch makes more than one loop. Openings and separate roofs in one sketch are not built yet: " +
                "keep one outline, and make a second roof for the other part.",
                left);
        }

        var points = loop.Select(step => step.Forward ? step.Line.Start : step.Line.End).ToList();
        var edges = loop.Select(step => step.Line.Edge.Copy()).ToList();

        if (Polygon2D.Area(points) <= JoinTolerance)
            return RoofSketchCheck.Refused("The outline encloses no area.");

        // Anticlockwise, the way every slab is stored. Reversing the points turns segment i
        // into segment (n - 2 - i), so the edges are turned round with them.
        if (Polygon2D.SignedArea(points) < 0)
        {
            points.Reverse();

            var count = edges.Count;
            edges = Enumerable.Range(0, count).Select(i => edges[((count - 2 - i) % count + count) % count]).ToList();
        }

        return RoofSketchCheck.Loop(points, edges);
    }

    private static bool Crosses(RoofSketchLine a, RoofSketchLine b)
    {
        // Lines sharing a corner meet there, which is not crossing.
        var shareCorner = a.Start.DistanceTo(b.Start) <= JoinTolerance || a.Start.DistanceTo(b.End) <= JoinTolerance ||
                          a.End.DistanceTo(b.Start) <= JoinTolerance || a.End.DistanceTo(b.End) <= JoinTolerance;
        if (shareCorner) return false;

        var d1 = a.End - a.Start;
        var d2 = b.End - b.Start;
        var denominator = d1.Cross(d2);
        if (Math.Abs(denominator) <= d1.Length * d2.Length * 1e-9) return false;

        var offset = b.Start - a.Start;
        var t = offset.Cross(d2) / denominator;
        var u = offset.Cross(d1) / denominator;

        return t > 1e-9 && t < 1 - 1e-9 && u > 1e-9 && u < 1 - 1e-9;
    }

    /// <summary>A finished roof's outline, as sketch lines again - what Edit Footprint opens.</summary>
    public static List<RoofSketchLine> LinesOf(Roof roof)
    {
        var lines = new List<RoofSketchLine>();

        for (var i = 0; i < roof.Boundary.Count; i++)
        {
            var edge = i < roof.Edges.Count ? roof.Edges[i].Copy() : new RoofEdge();
            lines.Add(new RoofSketchLine(roof.Boundary[i], roof.Boundary[(i + 1) % roof.Boundary.Count], edge));
        }

        return lines;
    }

    // ---- following walls ----------------------------------------------------------

    /// <summary>
    /// Puts every roof edge picked from a wall back on that wall, for walls that have moved,
    /// turned or changed thickness. Returns whether any roof changed.
    ///
    /// The roof is worked out from its walls each time rather than moved with them by the
    /// commands that move walls. That way every change reaches it - a drag, a typed length, a
    /// new wall type, an undo - and an undo cannot leave the roof behind the wall it follows,
    /// since the roof is simply put back where the wall now says.
    /// </summary>
    public static bool FollowWalls(BimDocument document)
    {
        var changed = false;

        foreach (var roof in document.Elements.OfType<Roof>())
            changed |= Follow(document, roof);

        return changed;
    }

    private static bool Follow(BimDocument document, Roof roof)
    {
        var count = roof.Boundary.Count;
        if (count < 3 || roof.Edges.Count != count || roof.Edges.All(edge => edge.WallId is null)) return false;

        // Each edge's line: from its wall where it has one still standing, or where it is.
        var lines = new (Point2D From, Point2D To)[count];

        for (var i = 0; i < count; i++)
        {
            var edge = roof.Edges[i];
            var drawn = (roof.Boundary[i], roof.Boundary[(i + 1) % count]);

            lines[i] = edge.WallId is { } id &&
                       document.Walls.FirstOrDefault(candidate => candidate.Id == id) is { } wall &&
                       LineOnWall(document, wall, edge.OnLeftOfWall, edge.Overhang, edge.ExtendToCore) is { } placed
                ? placed
                : drawn;
        }

        // Each corner is where the lines either side of it cross; where they run parallel the
        // corner stays put.
        var corners = new List<Point2D>(count);
        for (var i = 0; i < count; i++)
        {
            var before = lines[(i - 1 + count) % count];
            var after = lines[i];
            corners.Add(Crossing(before.From, before.To, after.From, after.To) ?? roof.Boundary[i]);
        }

        var moved = corners.Where((corner, i) => corner.DistanceTo(roof.Boundary[i]) > 1e-6).Any();
        if (!moved) return false;

        // Only take it if it is still a proper outline the same way round; a wall dragged
        // through the roof would otherwise turn it inside out.
        if (Polygon2D.SignedArea(corners) <= 0) return false;

        var edges = roof.Edges.Select(edge => edge.Copy()).ToList();
        roof.SetBoundary(corners);
        roof.SetEdges(edges);
        return true;
    }
}

/// <summary>
/// Replaces a roof's sketch - its outline and every edge's settings - as one undoable step.
/// What Finish does when the roof being sketched already exists.
/// </summary>
public sealed class SetRoofSketchCommand : IUndoableCommand
{
    private readonly Roof _roof;
    private readonly List<Point2D> _oldBoundary;
    private readonly List<RoofEdge> _oldEdges;
    private readonly List<Point2D> _newBoundary;
    private readonly List<RoofEdge> _newEdges;

    public SetRoofSketchCommand(Roof roof, IEnumerable<Point2D> boundary, IEnumerable<RoofEdge> edges, string name = "Edit Footprint")
    {
        _roof = roof;
        _oldBoundary = roof.Boundary.ToList();
        _oldEdges = roof.Edges.Select(edge => edge.Copy()).ToList();
        _newBoundary = boundary.ToList();
        _newEdges = edges.Select(edge => edge.Copy()).ToList();
        Name = name;
    }

    public string Name { get; }

    public void Redo() => Apply(_newBoundary, _newEdges);

    public void Undo() => Apply(_oldBoundary, _oldEdges);

    private void Apply(List<Point2D> boundary, List<RoofEdge> edges)
    {
        _roof.SetBoundary(boundary);
        _roof.SetEdges(edges.Select(edge => edge.Copy()));
    }
}
