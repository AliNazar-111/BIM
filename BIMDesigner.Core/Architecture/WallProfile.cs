using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One vertical strip of a wall's elevation: between two distances along the wall, from a
/// straight bottom edge to a straight top edge. Heights are above the wall's base.
/// </summary>
public readonly record struct ProfileStrip(double From, double To, double BottomFrom, double BottomTo, double TopFrom, double TopTo)
{
    /// <summary>The bottom edge's height this far along.</summary>
    public double Bottom(double along) => Lerp(BottomFrom, BottomTo, along);

    /// <summary>The top edge's height this far along.</summary>
    public double Top(double along) => Lerp(TopFrom, TopTo, along);

    public double Area => (To - From) * (TopFrom - BottomFrom + TopTo - BottomTo) / 2;

    private double Lerp(double atFrom, double atTo, double along) =>
        To - From <= 0 ? atFrom : atFrom + (atTo - atFrom) * (along - From) / (To - From);
}

/// <summary>
/// A wall's edited elevation (specification section 3.1, "edit profile"): the outline of the
/// wall seen square-on, in place of the rectangle its length and height would give - a gable,
/// a stepped parapet, a wall that dips under a stair.
///
/// The outline is held as points in the wall's own elevation: X is the distance along the
/// location line from the start, Y the height above the wall's base. Only a straight, upright
/// wall of a single construction takes one; a wall that is curved, leans or is stacked keeps
/// its rectangle, and its profile waits until it is one again.
///
/// Everything that builds or draws the wall reads the outline as vertical strips, split at
/// every corner and at the edges of every door and window, so each strip has a straight top
/// and bottom. A strip then builds exactly like an ordinary piece of wall, joins and layers
/// and all, only with a sloping top.
/// </summary>
public static class WallProfile
{
    private const double Tolerance = 1e-6;

    /// <summary>The smallest outline area worth building, mm².</summary>
    private const double MinimumArea = 1;

    /// <summary>Whether a wall can take an edited profile: straight, upright and one construction.</summary>
    public static bool CanHave(BimDocument document, Wall wall) =>
        !wall.IsCurved &&
        wall.CrossSection == WallCrossSection.Vertical &&
        document.FindType<StackedWallType>(wall.TypeId) is null &&
        document.FindType<CurtainWallType>(wall.TypeId) is null;

    /// <summary>
    /// The outline in force for a wall, or null when it has none or cannot take one now.
    ///
    /// Corners drawn at the wall's far end stay there when the wall is lengthened or shortened,
    /// as the end of a sketch stays on the end of the wall; everything else keeps its distance
    /// from the start.
    /// </summary>
    public static IReadOnlyList<Point2D>? Of(BimDocument document, Wall wall)
    {
        if (!CanHave(document, wall)) return null;

        // An outline drawn by hand is what the wall is, whatever it is attached to.
        if (wall.Profile is not { Count: >= 3 } profile) return UnderRoof(document, wall);

        var length = wall.Length;
        if (Math.Abs(length - wall.ProfileLength) <= Tolerance) return profile;

        return profile
            .Select(p => p.X >= wall.ProfileLength - Tolerance ? new Point2D(length, p.Y) : p)
            .ToList();
    }

    /// <summary>
    /// The outline of a wall whose top is attached to a pitched roof: up to the roof's
    /// underside all along it (specification section 3.1, "attach top to roofs"; Revit's
    /// Attach Top).
    ///
    /// This is what closes a gable. A wall under the end of a gable roof rises to a point
    /// under the ridge; one under a hip or an eave stays at the plate. The outline is worked
    /// out from the roof every time it is asked for, never stored, so a steeper pitch, a
    /// moved ridge or a new footprint reaches the walls at once - and because it is an
    /// outline, everything that already draws a wall with an edited profile draws this one:
    /// its solid, its cut in plan and section, its doors and windows, its quantities.
    ///
    /// The roof's underside is found on both faces of the wall and the higher taken. Under an
    /// eave the roof rises across the wall's thickness, and a top level with the outer face
    /// would leave a wedge of air along the inside of every eave wall; level with the inner
    /// face instead, the extra sits inside the roof's own build-up where nothing sees it. It
    /// is kept from going through the top of the roof all the same.
    /// </summary>
    private static IReadOnlyList<Point2D>? UnderRoof(BimDocument document, Wall wall)
    {
        if (wall.AttachedRoof(document) is not { } roof || roof.Surface(document).IsFlat) return null;
        if (document.GetWallType(wall) is not { } type) return null;

        var length = wall.Length;
        if (length <= Tolerance) return null;

        var structure = type.Structure;
        var half = structure.TotalWidth / 2;
        var bottom = wall.GetBaseElevation(document);
        var unattached = wall.GetUnattachedTopElevation(document);
        var surface = roof.Surface(document);
        var thickness = document.FindType<SlabType>(roof.TypeId)?.Thickness ?? 0;

        var faces = new[] { half, -half };

        // Every point along the wall where the roof's underside changes direction under either
        // face: where a face crosses the edge of a roof face. Between two of them each face is
        // under one plane, so the top is straight - and the outline is exact.
        var stations = new List<double> { 0, length };
        foreach (var across in faces)
        {
            var from = wall.PointAt(structure, 0, across);
            var to = wall.PointAt(structure, length, across);

            foreach (var facet in surface.Facets)
            {
                for (var i = 0; i < facet.Outline.Count; i++)
                {
                    if (Crossing(from, to, facet.Outline[i], facet.Outline[(i + 1) % facet.Outline.Count]) is { } t)
                        stations.Add(t * length);
                }
            }
        }

        var along = stations
            .Select(station => Math.Clamp(station, 0, length))
            .OrderBy(station => station)
            .Aggregate(new List<double>(), (kept, station) =>
            {
                if (kept.Count == 0 || station - kept[^1] > 1) kept.Add(station);
                return kept;
            });

        if (along[^1] < length - 1) along.Add(length);
        else along[^1] = length;

        double TopAt(double station)
        {
            var heights = faces.Select(across =>
            {
                var point = wall.PointAt(structure, station, across);
                return Covered(roof, point) ? surface.HeightAt(point) : unattached;
            }).ToList();

            // The inner face's height, but never through the top of the roof.
            var top = Math.Min(heights.Max(), heights.Min() + thickness);
            return Math.Max(top - bottom, 1);
        }

        // Where the wall runs out from under the roof its top steps down there, rather than
        // sloping down all the way to the next corner: the height just before each station
        // and just after it are both kept when they differ.
        var tops = new List<Point2D>();
        foreach (var station in along)
        {
            var before = station > 0 ? TopAt(Math.Max(0, station - 0.5)) : TopAt(station);
            var after = station < length ? TopAt(Math.Min(length, station + 0.5)) : TopAt(station);

            if (Math.Abs(before - after) > 1 && station > 0 && station < length)
            {
                tops.Add(new Point2D(station, before));
                tops.Add(new Point2D(station, after));
            }
            else
            {
                tops.Add(new Point2D(station, TopAt(station)));
            }
        }

        // A roof that turns out level over the whole wall leaves it a plain rectangle.
        if (tops.All(point => Math.Abs(point.Y - tops[0].Y) <= Tolerance) &&
            Math.Abs(bottom + tops[0].Y - wall.GetTopElevation(document)) <= 1)
        {
            return null;
        }

        var outline = new List<Point2D> { new(0, 0), new(length, 0) };
        outline.AddRange(tops.AsEnumerable().Reverse());

        return Problem(outline, length) is null ? outline : null;
    }

    /// <summary>Whether a roof is over a point - inside its footprint, or on its edge.</summary>
    private static bool Covered(Roof roof, Point2D point)
    {
        if (roof.Contains(point)) return true;

        var boundary = roof.Boundary;
        for (var i = 0; i < boundary.Count; i++)
            if (Line2D.DistanceFromSegment(point, boundary[i], boundary[(i + 1) % boundary.Count]) <= 1) return true;

        return false;
    }

    /// <summary>How far along the first segment the second crosses it, 0 to 1; null when they do not cross.</summary>
    private static double? Crossing(Point2D a0, Point2D a1, Point2D b0, Point2D b1)
    {
        var a = a1 - a0;
        var b = b1 - b0;
        var denominator = a.Cross(b);
        if (Math.Abs(denominator) <= a.Length * b.Length * 1e-9) return null;

        var offset = b0 - a0;
        var t = offset.Cross(b) / denominator;
        var u = offset.Cross(a) / denominator;

        return t > 0 && t < 1 && u >= -1e-9 && u <= 1 + 1e-9 ? t : null;
    }

    /// <summary>The rectangle a wall has before its profile is edited.</summary>
    public static IReadOnlyList<Point2D> Rectangle(double length, double height) => new[]
    {
        new Point2D(0, 0), new Point2D(length, 0), new Point2D(length, height), new Point2D(0, height)
    };

    /// <summary>What is wrong with an outline for a wall this long, or null if it will do.</summary>
    public static string? Problem(IReadOnlyList<Point2D> profile, double length)
    {
        if (profile.Count < 3) return "A profile needs at least three corners.";
        if (profile.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return "Every corner needs a distance along and a height.";
        if (profile.Any(p => p.X < -Tolerance || p.X > length + Tolerance))
            return $"Every corner must be between the ends of the wall: 0 to {Units.FormatLength(length)} along.";
        if (Math.Abs(Polygon2D.SignedArea(profile)) < MinimumArea) return "The profile encloses no area.";
        if (Crosses(profile)) return "The profile crosses itself. Move the corners so its edges only meet at corners.";
        return null;
    }

    /// <summary>
    /// The outline as vertical strips between 0 and the wall's length, with the holes given -
    /// doors and windows, as distances along and heights above the base - taken out.
    /// </summary>
    public static IReadOnlyList<ProfileStrip> Strips(
        IReadOnlyList<Point2D> profile, double length,
        IEnumerable<(double From, double To, double Sill, double Head)>? holes = null)
    {
        var cuts = holes?.Where(h => h.To > h.From && h.Head > h.Sill).ToList() ?? new();
        var edges = Edges(profile).ToList();

        // Every place a strip must end: each corner, each side of a hole, and wherever an edge
        // crosses a sill or head, so that within a strip nothing changes which is higher.
        var breaks = new List<double> { 0, length };
        breaks.AddRange(profile.Select(p => p.X));
        foreach (var (from, to, sill, head) in cuts)
        {
            breaks.Add(from);
            breaks.Add(to);
            foreach (var (a, b) in edges)
            foreach (var height in new[] { sill, head })
            {
                if ((a.Y - height) * (b.Y - height) < 0)
                    breaks.Add(a.X + (b.X - a.X) * (height - a.Y) / (b.Y - a.Y));
            }
        }

        var xs = breaks.Where(x => x >= -Tolerance && x <= length + Tolerance)
            .Select(x => Math.Clamp(x, 0, length))
            .OrderBy(x => x)
            .ToList();

        var strips = new List<ProfileStrip>();
        for (var i = 0; i + 1 < xs.Count; i++)
        {
            var (x0, x1) = (xs[i], xs[i + 1]);
            if (x1 - x0 <= Tolerance) continue;
            var middle = (x0 + x1) / 2;

            // The edges running across this strip, lowest first; between the first and second
            // is inside the outline, between the second and third outside, and so on.
            var across = edges
                .Where(e => Math.Min(e.A.X, e.B.X) < middle && Math.Max(e.A.X, e.B.X) > middle)
                .Select(e => (At0: HeightOn(e, x0), At1: HeightOn(e, x1), Middle: HeightOn(e, middle)))
                .OrderBy(e => e.Middle)
                .ToList();

            var hole = cuts.Where(h => h.From <= x0 + Tolerance && h.To >= x1 - Tolerance).ToList();

            for (var k = 0; k + 1 < across.Count; k += 2)
            {
                var strip = new ProfileStrip(x0, x1, across[k].At0, across[k].At1, across[k + 1].At0, across[k + 1].At1);
                foreach (var piece in Subtract(strip, hole)) strips.Add(piece);
            }
        }

        return strips;
    }

    /// <summary>The spans of height the outline covers this far along the wall, lowest first.</summary>
    public static IReadOnlyList<(double Bottom, double Top)> HeightsAt(IReadOnlyList<Point2D> profile, double along)
    {
        var heights = Edges(profile)
            .Where(e => Math.Min(e.A.X, e.B.X) < along && Math.Max(e.A.X, e.B.X) >= along)
            .Select(e => HeightOn(e, along))
            .OrderBy(y => y)
            .ToList();

        var spans = new List<(double, double)>();
        for (var k = 0; k + 1 < heights.Count; k += 2)
            if (heights[k + 1] - heights[k] > Tolerance) spans.Add((heights[k], heights[k + 1]));
        return spans;
    }

    /// <summary>The stretches along the wall, between its ends, where the outline covers a height.</summary>
    public static IReadOnlyList<(double From, double To)> RunsAt(IReadOnlyList<Point2D> profile, double height, double length)
    {
        var xs = Edges(profile)
            .Where(e => Math.Min(e.A.Y, e.B.Y) < height && Math.Max(e.A.Y, e.B.Y) >= height)
            .Select(e => e.A.X + (e.B.X - e.A.X) * (height - e.A.Y) / (e.B.Y - e.A.Y))
            .OrderBy(x => x)
            .ToList();

        var runs = new List<(double, double)>();
        for (var k = 0; k + 1 < xs.Count; k += 2)
        {
            var (from, to) = (Math.Max(0, xs[k]), Math.Min(length, xs[k + 1]));
            if (to - from > Tolerance) runs.Add((from, to));
        }

        return runs;
    }

    /// <summary>The highest point of the outline above the base.</summary>
    public static double Top(IReadOnlyList<Point2D> profile) => profile.Max(p => p.Y);

    /// <summary>The lowest point of the outline relative to the base.</summary>
    public static double Bottom(IReadOnlyList<Point2D> profile) => profile.Min(p => p.Y);

    /// <summary>
    /// The outline cut at a distance along and split into the part before and the part after,
    /// the second measured from the cut - for splitting a wall in two.
    /// </summary>
    public static (IReadOnlyList<Point2D> Before, IReadOnlyList<Point2D> After) Split(IReadOnlyList<Point2D> profile, double at)
    {
        var before = ClipX(profile, at, keepBelow: true);
        var after = ClipX(profile, at, keepBelow: false).Select(p => new Point2D(p.X - at, p.Y)).ToList();
        return (before, after);
    }

    /// <summary>Keeps the part of an outline on one side of a vertical line (Sutherland-Hodgman).</summary>
    private static List<Point2D> ClipX(IReadOnlyList<Point2D> profile, double x, bool keepBelow)
    {
        bool Inside(Point2D p) => keepBelow ? p.X <= x : p.X >= x;
        var result = new List<Point2D>();

        for (var i = 0; i < profile.Count; i++)
        {
            var current = profile[i];
            var previous = profile[(i + profile.Count - 1) % profile.Count];

            if (Inside(current))
            {
                if (!Inside(previous)) result.Add(OnLine(previous, current));
                result.Add(current);
            }
            else if (Inside(previous))
            {
                result.Add(OnLine(previous, current));
            }
        }

        return result;

        Point2D OnLine(Point2D a, Point2D b) => new(x, a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X));
    }

    /// <summary>A strip less the doors and windows through it: what is left below each sill and above each head.</summary>
    private static IEnumerable<ProfileStrip> Subtract(ProfileStrip strip, List<(double From, double To, double Sill, double Head)> holes)
    {
        var pieces = new List<ProfileStrip> { strip };
        var middle = (strip.From + strip.To) / 2;

        foreach (var (_, _, sill, head) in holes)
        {
            var next = new List<ProfileStrip>();
            foreach (var piece in pieces)
            {
                var (bottom, top) = (piece.Bottom(middle), piece.Top(middle));

                // Nothing crosses a sill or head inside a strip, so its middle says it all.
                if (top <= sill + Tolerance || bottom >= head - Tolerance)
                {
                    next.Add(piece);
                    continue;
                }

                if (bottom < sill - Tolerance) next.Add(piece with { TopFrom = sill, TopTo = sill });
                if (top > head + Tolerance) next.Add(piece with { BottomFrom = head, BottomTo = head });
            }

            pieces = next;
        }

        return pieces;
    }

    private static IEnumerable<(Point2D A, Point2D B)> Edges(IReadOnlyList<Point2D> profile) =>
        profile.Select((p, i) => (p, profile[(i + 1) % profile.Count])).Where(e => e.Item1.DistanceTo(e.Item2) > Tolerance);

    private static double HeightOn((Point2D A, Point2D B) edge, double x) =>
        Math.Abs(edge.B.X - edge.A.X) < 1e-12
            ? Math.Min(edge.A.Y, edge.B.Y)
            : edge.A.Y + (edge.B.Y - edge.A.Y) * (x - edge.A.X) / (edge.B.X - edge.A.X);

    /// <summary>Whether any two edges that are not neighbours touch.</summary>
    private static bool Crosses(IReadOnlyList<Point2D> profile)
    {
        var n = profile.Count;
        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            if (j == i + 1 || (i == 0 && j == n - 1)) continue;

            var (a, b) = (profile[i], profile[(i + 1) % n]);
            var (c, d) = (profile[j], profile[(j + 1) % n]);
            if (SegmentsTouch(a, b, c, d)) return true;
        }

        return false;
    }

    private static bool SegmentsTouch(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        double Side(Point2D p, Point2D q, Point2D r) => (q - p).Cross(r - p);
        bool Within(Point2D p, Point2D q, Point2D r) =>
            Math.Min(p.X, q.X) - Tolerance <= r.X && r.X <= Math.Max(p.X, q.X) + Tolerance &&
            Math.Min(p.Y, q.Y) - Tolerance <= r.Y && r.Y <= Math.Max(p.Y, q.Y) + Tolerance;

        var (d1, d2, d3, d4) = (Side(c, d, a), Side(c, d, b), Side(a, b, c), Side(a, b, d));
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;

        return (Math.Abs(d1) < Tolerance && Within(c, d, a)) || (Math.Abs(d2) < Tolerance && Within(c, d, b)) ||
               (Math.Abs(d3) < Tolerance && Within(a, b, c)) || (Math.Abs(d4) < Tolerance && Within(a, b, d));
    }
}
