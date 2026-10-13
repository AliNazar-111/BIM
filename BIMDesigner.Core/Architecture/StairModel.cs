using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One run of handrail along a side of a stair: its points with the pitch line's height there,
/// whether it is fixed to a wall - a rail on brackets - or a balustrade on an open side, with
/// newels and balusters under it, and which way the stair is from it.
/// </summary>
public sealed record StairRailing(IReadOnlyList<StairSidePoint> Points, bool OnWall, bool LeftSide);

/// <summary>
/// A stair as it is built, for the 3D view, sections and IFC: its treads - housed in strings,
/// with risers between unless they are open - or cast in concrete down to a soffit; its
/// landings; the newel a winding corner turns on; and its handrails - a balustrade with newels
/// and balusters along an open side, a rail on a wall - as its handrail setting and the walls
/// beside it say.
/// </summary>
public static class StairModel
{
    private static readonly ColourRgb Paint = new(0xF2, 0xF2, 0xEE);
    private static readonly ColourRgb Steel = new(0xBF, 0xC4, 0xC9);

    private const double StringThickness = 32;
    private const double RiserThickness = 18;
    private const double LandingDepth = 200;
    private const double HandrailSize = 50;
    private const double BalusterSize = 32;
    private const double NewelSize = 90;

    /// <summary>
    /// The runs of handrail a stair has. Open sides are guarded with a balustrade; a side against
    /// a wall has a rail on the wall where a handrail is wanted there - both sides of a stair
    /// 1000 wide or more, as Approved Document K asks, or the left of one between two walls.
    /// Said otherwise, the sides it is told.
    /// </summary>
    public static IReadOnlyList<StairRailing> Railings(BimDocument document, Stair stair, StairLayout layout)
    {
        if (stair.Railings == StairRailings.None) return Array.Empty<StairRailing>();

        var sides = new[] { (Points: layout.Left, Left: true), (Points: layout.Right, Left: false) }
            .Select(side => (side.Left, Segments: Segments(document, stair, layout, side.Points, side.Left)))
            .ToList();
        var anyOpen = sides.Any(side => side.Segments.Any(segment => !segment.OnWall));
        var both = stair.Railings == StairRailings.BothSides || layout.Width >= 1000;

        var railings = new List<StairRailing>();
        foreach (var (left, segments) in sides)
        {
            var wanted = stair.Railings switch
            {
                StairRailings.LeftSide => left,
                StairRailings.RightSide => !left,
                _ => true
            };
            if (!wanted) continue;

            // On the wall too where it is wanted there: told to, both sides wanted, or the only handrail it would have.
            var onWall = stair.Railings != StairRailings.Auto || both || (!anyOpen && left);

            var run = new List<StairSidePoint>();
            bool? runOnWall = null;
            foreach (var (from, to, wall) in segments)
            {
                var keep = !wall || onWall;
                if (!keep || (runOnWall is { } kind && kind != wall))
                {
                    if (run.Count >= 2) railings.Add(new StairRailing(run, runOnWall ?? false, left));
                    run = new List<StairSidePoint>();
                    runOnWall = null;
                }

                if (!keep) continue;
                if (run.Count == 0) run.Add(from);
                run.Add(to);
                runOnWall = wall;
            }

            if (run.Count >= 2) railings.Add(new StairRailing(run, runOnWall ?? false, left));
        }

        return railings;
    }

    /// <summary>A side's lengths, each with whether there is a wall right against it - a step up in place riding with the length before it.</summary>
    private static List<(StairSidePoint From, StairSidePoint To, bool OnWall)> Segments(
        BimDocument document, Stair stair, StairLayout layout, IReadOnlyList<StairSidePoint> points, bool left)
    {
        var walls = document.Walls
            .Where(wall => document.GetWallType(wall) is { } type && type.Structure.TotalWidth > 0 && wall.Length > 1)
            .Select(wall => (Wall: wall, Band: CurtainGeometry.Band(wall, document.GetWallType(wall)!, 0, wall.Length,
                document.GetWallType(wall)!.Structure.TotalWidth / 2, -document.GetWallType(wall)!.Structure.TotalWidth / 2)))
            .ToList();

        var segments = new List<(StairSidePoint, StairSidePoint, bool)>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var along = b.At - a.At;
            if (along.Length < 1)
            {
                // A step up in place: part of the run it is in, a newel standing at it.
                segments.Add((a, b, segments.Count > 0 && segments[^1].Item3));
                continue;
            }

            // A probe just outside the side, at its middle and its ends: against a wall if most are in one.
            var outward = Inward(along, left) * -1;
            var height = (a.Height + b.Height) / 2;
            var probes = new[] { 0.15, 0.5, 0.85 }.Select(t => a.At + along * t + outward * 60).ToList();
            var walled = probes.Count(probe => walls.Any(entry =>
                entry.Wall.GetBaseElevation(document) <= height && entry.Wall.GetTopElevation(document) >= height + 300 &&
                Polygon2D.Contains(entry.Band, probe))) >= 2;
            segments.Add((a, b, walled));
        }

        return segments;
    }

    /// <summary>Which way the stair is from a length of its side: right of it for the left side, left of it for the right.</summary>
    public static Vector2D Inward(Vector2D along, bool left)
    {
        var normal = along.NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        return left ? normal * -1 : normal;
    }

    /// <summary>A strip along a length of side, from so far in from it to so far: a string, a handrail.</summary>
    private static IReadOnlyList<Point2D> Strip(Point2D from, Point2D to, Vector2D inward, double near, double far) =>
        new[] { from + inward * near, to + inward * near, to + inward * far, from + inward * far };

    /// <summary>The pitch line's height at a point along a length of side, between the heights at its ends.</summary>
    private static Func<Point2D, double> Along(StairSidePoint a, StairSidePoint b)
    {
        var along = b.At - a.At;
        var length = Math.Max(1e-9, along.Length);
        var direction = along * (1 / length);
        return point => a.Height + (b.Height - a.Height) * Math.Clamp((point - a.At).Dot(direction) / length, 0, 1);
    }

    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Stair stair)
    {
        var (steps, railings) = Build(document, stair);
        return steps.Concat(railings).ToList();
    }

    /// <summary>A stair's solids, as the stair itself and as its handrails and balustrades - which IFC has as railings of their own.</summary>
    public static (IReadOnlyList<Mesh3D> Stair, IReadOnlyList<Mesh3D> Railings) Build(BimDocument document, Stair stair)
    {
        var type = Stairs.TypeOf(document, stair);
        var layout = Stairs.Layout(document, stair);
        var timber = type.Construction == StairConstruction.Timber;

        var material = document.FindMaterial(type.MaterialId);
        var body = new Mesh3D(stair.Id, stair.LevelId, MeshKind.Stair, material?.SurfaceColour ?? type.Colour, material?.Name ?? type.Name);
        var painted = new Mesh3D(stair.Id, stair.LevelId, MeshKind.Stair, timber ? Paint : Steel, timber ? "Paint, White" : "Stainless Steel");

        // A timber stair's handrails and newels are its timber, its balusters painted; a concrete one's all steel.
        var rails = timber
            ? new Mesh3D(stair.Id, stair.LevelId, MeshKind.Stair, body.Colour, body.Description)
            : new Mesh3D(stair.Id, stair.LevelId, MeshKind.Stair, Steel, "Stainless Steel");

        var tops = layout.Steps.ToDictionary(step => step.Number, step => step.Top);
        var slope = layout.Rise / layout.Going;
        foreach (var step in layout.Steps)
        {
            if (step.Kind == StairStepKind.Landing)
            {
                body.AddExtrusion(step.Outline, step.Top - LandingDepth, step.Top);
            }
            else if (timber)
            {
                body.AddExtrusion(step.Outline, step.Top - type.TreadThickness, step.Top);
            }
            else
            {
                // Cast as one: each step down to a soffit parallel to the pitch, 150 under the steps' roots.
                double Pitch(Point2D point) => step.Kind == StairStepKind.Tread ? step.Top + (point - step.RiserFrom).Dot(step.Up) * slope : step.Top;
                body.AddExtrusion(step.Outline, point => Math.Max(layout.Bottom, Pitch(point) - layout.Rise - 150), _ => step.Top);
            }

            // The riser under its nosing, from the step below to the underside of its tread.
            if (timber && !type.OpenRisers)
            {
                var below = tops.TryGetValue(step.Number - 1, out var lower) ? lower : layout.Bottom;
                var board = new[] { step.RiserFrom, step.RiserTo, step.RiserTo + step.Up * RiserThickness, step.RiserFrom + step.Up * RiserThickness };
                painted.AddExtrusion(board, below, step.Top - (step.Kind == StairStepKind.Landing ? LandingDepth : type.TreadThickness));
            }
        }

        // The strings up each side the treads are housed in, following the pitch line.
        if (timber)
            foreach (var (points, left) in new[] { (layout.Left, true), (layout.Right, false) })
                for (var i = 0; i + 1 < points.Count; i++)
                {
                    var (a, b) = (points[i], points[i + 1]);
                    if ((b.At - a.At).Length < 1) continue;
                    var height = Along(a, b);
                    body.AddExtrusion(Strip(a.At, b.At, Inward(b.At - a.At, left), 0, StringThickness),
                        point => Math.Max(layout.Bottom, height(point) - 220), point => height(point) + 75);
                }

        // The newel a winding corner turns on, from the floor to above the handrail.
        if (layout.Newel is { } newel)
        {
            var highest = layout.Left.Concat(layout.Right).Where(point => (point.At - newel[0]).Length < 1).Select(point => point.Height).DefaultIfEmpty(layout.Top).Max();
            rails.AddExtrusion(newel, layout.Bottom, highest + type.HandrailHeight + 100);
        }

        // The balusters on their own, shaded but not outlined: so many, so thin, their edges would blacken them.
        var balusters = new Mesh3D(stair.Id, stair.LevelId, MeshKind.Stair, painted.Colour, painted.Description);
        foreach (var railing in Railings(document, stair, layout).Concat(Guarding(document, stair, layout)))
            AddRailing(railing, type, layout, rails, balusters);
        balusters.DropEdges();

        return (new[] { body, painted }.Where(mesh => !mesh.IsEmpty).ToList(), new[] { rails, balusters }.Where(mesh => !mesh.IsEmpty).ToList());
    }

    /// <summary>
    /// The balustrade round the stairwell on the floor above: along every edge of the hole the
    /// stair cuts but the one it comes up at, and those against a wall - so the floor's edge is
    /// guarded where it drops into the stairwell. None for a stair told it has no handrails.
    /// </summary>
    public static IReadOnlyList<StairRailing> Guarding(BimDocument document, Stair stair, StairLayout layout)
    {
        if (stair.Railings == StairRailings.None) return Array.Empty<StairRailing>();

        var walls = document.Walls
            .Where(wall => document.GetWallType(wall) is { } type && type.Structure.TotalWidth > 0 && wall.Length > 1)
            .Select(wall => (Wall: wall, Band: CurtainGeometry.Band(wall, document.GetWallType(wall)!, 0, wall.Length,
                document.GetWallType(wall)!.Structure.TotalWidth / 2, -document.GetWallType(wall)!.Structure.TotalWidth / 2)))
            .ToList();
        var (entryFrom, entryTo) = layout.TopEdge;

        var guarding = new List<StairRailing>();
        var seen = new HashSet<string>();
        foreach (var (floor, hole) in Stairs.Stairwells(document, stair, layout))
        {
            // Two floors laid one on the other at the same height have the one stairwell: guarded once.
            var key = $"{floor.GetTopElevation(document):0}:" + string.Join(";", hole.Select(point => $"{point.X:0},{point.Y:0}"));
            if (!seen.Add(key)) continue;

            // Anticlockwise, so the floor is on the right of each edge going round.
            var loop = Polygon2D.SignedArea(hole) >= 0 ? hole : hole.Reverse().ToList();
            var height = floor.GetTopElevation(document);

            bool Guarded(Point2D a, Point2D b)
            {
                var middle = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                if (Line2D.DistanceFromSegment(middle, entryFrom, entryTo) < 5) return false;
                var outward = Inward(b - a, true);
                var probe = middle + outward * 60;
                return !walls.Any(entry => entry.Wall.GetBaseElevation(document) <= height + 100 && entry.Wall.GetTopElevation(document) >= height + 900
                    && Polygon2D.Contains(entry.Band, probe));
            }

            // Runs of guarded edges, going round from just after an edge that is not.
            var start = Enumerable.Range(0, loop.Count).FirstOrDefault(i => !Guarded(loop[i], loop[(i + 1) % loop.Count]), -1);
            var run = new List<StairSidePoint>();
            for (var k = 0; k < loop.Count; k++)
            {
                var i = (start + 1 + k) % loop.Count;
                var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
                if ((b - a).Length < 1) continue;
                if (!Guarded(a, b))
                {
                    if (run.Count >= 2) guarding.Add(new StairRailing(run, false, true));
                    run = new List<StairSidePoint>();
                    continue;
                }

                if (run.Count == 0) run.Add(new StairSidePoint(a, height));
                run.Add(new StairSidePoint(b, height));
            }

            if (run.Count >= 2) guarding.Add(new StairRailing(run, false, true));
        }

        return guarding;
    }

    /// <summary>
    /// A run of handrail: the rail along it at its height over the pitch line; on an open side,
    /// balusters under it no further apart than its type says, and newels at its ends and corners.
    /// </summary>
    private static void AddRailing(StairRailing railing, StairType type, StairLayout layout, Mesh3D rails, Mesh3D balusters)
    {
        var points = railing.Points;
        var handrail = type.HandrailHeight;
        var (near, far) = railing.OnWall ? (40.0, 40 + HandrailSize) : (0.0, HandrailSize);

        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var along = b.At - a.At;
            if (along.Length < 1) continue;

            var inward = Inward(along, railing.LeftSide);
            var height = Along(a, b);
            rails.AddExtrusion(Strip(a.At, b.At, inward, near, far), point => height(point) + handrail - HandrailSize, point => height(point) + handrail);
            if (railing.OnWall) continue;

            foreach (var (middle, foot, top) in BalustersAlong(a, b, railing.LeftSide, type, layout))
                balusters.AddExtrusion(Square(middle, BalusterSize / 2), foot, top);
        }

        if (railing.OnWall) return;

        // Newels where it starts and ends, and wherever it turns or steps up.
        for (var i = 0; i < points.Count; i++)
        {
            var at = points[i];
            var turns = i == 0 || i == points.Count - 1 || (points[i + 1].At - at.At).Length < 1 || (at.At - points[i - 1].At).Length < 1
                        || Math.Abs((points[i + 1].At - at.At).NormalisedOrDefault(Vector2D.UnitX).Dot((at.At - points[i - 1].At).NormalisedOrDefault(Vector2D.UnitX))) < 0.9;
            if (!turns) continue;
            if (i > 0 && (at.At - points[i - 1].At).Length < 1 && i < points.Count - 1) continue;
            if (layout.Newel is { } corner && Polygon2D.Contains(corner, at.At + new Vector2D(1, 1))) continue;

            var low = points.Where(p => (p.At - at.At).Length < 1).Min(p => p.Height);
            var high = points.Where(p => (p.At - at.At).Length < 1).Max(p => p.Height);
            var direction = i + 1 < points.Count && (points[i + 1].At - at.At).Length >= 1 ? points[i + 1].At - at.At : at.At - points[Math.Max(0, i - 1)].At;
            var centre = at.At + Inward(direction, railing.LeftSide) * (HandrailSize / 2);
            rails.AddExtrusion(Square(centre, NewelSize / 2), Math.Max(layout.Bottom, low - 300), high + handrail + 100);
        }
    }

    /// <summary>
    /// The balusters along a length of a balustrade: where each stands, what it stands on, and
    /// up to the handrail's underside. On a timber stair they stand on the string, 75 over the
    /// pitch line; on a concrete one, with no string, on the tread under each; on a landing or
    /// the floor above, on that.
    /// </summary>
    public static IEnumerable<(Point2D At, double Foot, double Top)> BalustersAlong(
        StairSidePoint a, StairSidePoint b, bool leftSide, StairType type, StairLayout layout)
    {
        var along = b.At - a.At;
        if (along.Length < 1) yield break;

        var inward = Inward(along, leftSide);
        var height = Along(a, b);
        var level = Math.Abs(a.Height - b.Height) < 1;
        var onString = type.Construction == StairConstruction.Timber;
        var count = Math.Max(1, (int)Math.Ceiling(along.Length / Math.Max(40, type.BalusterSpacing)));
        for (var k = 0; k < count; k++)
        {
            var middle = a.At + along * ((k + 0.5) / count) + inward * (HandrailSize / 2);
            var pitch = height(middle);
            var foot = level ? pitch
                : onString ? pitch + 75
                : layout.Steps.Where(step => Polygon2D.Contains(step.Outline, middle)).Select(step => (double?)step.Top).Max() ?? pitch - layout.Rise;
            yield return (middle, foot, pitch + type.HandrailHeight - HandrailSize);
        }
    }

    private static IReadOnlyList<Point2D> Square(Point2D centre, double half) => new[]
    {
        new Point2D(centre.X - half, centre.Y - half), new Point2D(centre.X + half, centre.Y - half),
        new Point2D(centre.X + half, centre.Y + half), new Point2D(centre.X - half, centre.Y + half)
    };
}
