using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>The section of the logs a log wall is built of.</summary>
public enum LogShape
{
    /// <summary>Round both sides: a log as it comes from the tree, peeled.</summary>
    Round,

    /// <summary>Round outside, flat inside: a D-log, plain to the room.</summary>
    DLog,

    /// <summary>Squared all round, its arrises taken off.</summary>
    Square
}

/// <summary>
/// How a log wall type is built (Archicad's log wall): of logs laid one course on another, as
/// thick as the wall, each course this high; at a corner they run on past it by the overhang,
/// crossing the logs of the other wall.
/// </summary>
public sealed record LogWall(LogShape Shape, double CourseHeight, double Overhang)
{
    public static LogWall Default { get; } = new(LogShape.Round, 200, 300);
}

/// <summary>Log walls: built as courses of logs rather than layers.</summary>
public static class LogWalls
{
    /// <summary>A log's section, as wide as the wall and as high as its course: across toward the outside, and up.</summary>
    public static IReadOnlyList<Point2D> Profile(LogShape shape, double width, double height)
    {
        var (a, b) = (width / 2, height / 2);
        const int Steps = 20;

        switch (shape)
        {
            case LogShape.Round:
                return Enumerable.Range(0, Steps).Select(i => 2 * Math.PI * i / Steps)
                    .Select(t => new Point2D(a * Math.Cos(t), b * Math.Sin(t))).ToList();

            case LogShape.DLog:
            {
                // Flat to the room, round to the weather.
                var points = new List<Point2D> { new(-a, -b) };
                points.AddRange(Enumerable.Range(0, Steps / 2 + 1).Select(i => -Math.PI / 2 + Math.PI * i / (Steps / 2))
                    .Select(t => new Point2D(a * Math.Cos(t), b * Math.Sin(t))));
                points.Add(new Point2D(-a, b));
                return points;
            }

            default:
            {
                var c = Math.Min(15, Math.Min(a, b) / 3);
                return new[]
                {
                    new Point2D(-a + c, -b), new Point2D(a - c, -b), new Point2D(a, -b + c), new Point2D(a, b - c),
                    new Point2D(a - c, b), new Point2D(-a + c, b), new Point2D(-a, b - c), new Point2D(-a, -b + c)
                };
            }
        }
    }

    /// <summary>
    /// Whether a wall's courses sit half a course up: those running across the plan's Y axis do,
    /// so the logs of two walls meeting at a corner cross one over the other.
    /// </summary>
    public static bool Raised(Wall wall) => Math.Abs(wall.Direction.Y) > Math.Abs(wall.Direction.X);

    /// <summary>How far a wall's logs run on past an end of it: past a corner, by the overhang beyond the wall crossing it.</summary>
    public static double RunOn(WallType type, WallCut cut) =>
        type.Log is { } log && cut.Condition is WallEndCondition.Mitre or WallEndCondition.Butt or WallEndCondition.RunsThrough
            or WallEndCondition.Shared or WallEndCondition.Overlap
            ? type.Width / 2 + log.Overhang
            : 0;

    /// <summary>
    /// A log wall built as its logs: course on course from its bottom to its top, each the length
    /// of the wall and on past its corners, broken by its doors and windows. Null for a wall not
    /// of logs, or a curved one, which is built as its layers.
    /// </summary>
    public static Mesh3D? Mesh(BimDocument document, Wall wall, WallType type, double bottom, double top)
    {
        if (type.Log is not { } log || wall.IsCurved || log.CourseHeight <= 0 || top <= bottom) return null;

        var structure = type.Structure;
        var width = structure.TotalWidth;
        if (width <= 0) return null;

        var (bodyStart, bodyEnd) = wall.GetBodyCentreline(structure);
        var direction = (bodyEnd - bodyStart).NormalisedOrDefault(Vector2D.UnitX);
        var outward = wall.ExteriorNormal;
        var length = bodyStart.DistanceTo(bodyEnd);
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var (from, to) = (-RunOn(type, startCut), length + RunOn(type, endCut));

        var material = document.FindMaterial(structure.Layers.FirstOrDefault()?.MaterialId ?? Guid.Empty);
        var mesh = new Mesh3D(wall.Id, wall.LevelId, MeshKind.Wall, material?.SurfaceColour ?? new ColourRgb(0x9C, 0x74, 0x4C), material?.Name ?? "Logs");
        var openings = WallPaint.Openings(document, wall).ToList();
        var base0 = wall.GetBaseElevation(document);
        var raise = Raised(wall) ? log.CourseHeight / 2 : 0;

        for (var z = bottom - raise; z < top - 1; z += log.CourseHeight)
        {
            var (low, high) = (Math.Max(z, bottom), Math.Min(z + log.CourseHeight, top));
            if (high - low < 10) continue;

            // The course along the wall, less where a door or window is at its height.
            var runs = new List<(double From, double To)> { (from, to) };
            foreach (var (openFrom, openTo, sill, head) in openings.Where(o => base0 + o.Sill < high - 1 && base0 + o.Head > low + 1))
                runs = runs.SelectMany(run => openTo <= run.From || openFrom >= run.To
                        ? new[] { run }
                        : new[] { (run.From, openFrom), (openTo, run.To) })
                    .Where(run => run.Item2 - run.Item1 > 10).ToList();

            var profile = Profile(log.Shape, width, high - low);
            var middle = (low + high) / 2;
            foreach (var (runFrom, runTo) in runs)
            {
                var (a, b) = (bodyStart + direction * runFrom, bodyStart + direction * runTo);
                RoofEdgeSweeps.AddRun(mesh, new RoofEdgeRun(new[]
                {
                    new RoofEdgeSegment(new Vector3(a.X, a.Y, middle), new Vector3(b.X, b.Y, middle),
                        new Vector3(outward.X, outward.Y, 0), new Vector3(0, 0, 1), profile)
                }, false));
            }
        }

        return mesh;
    }

    /// <summary>The ends of a log wall's logs past its corners, in plan, as the plan shows them crossing.</summary>
    public static IEnumerable<IReadOnlyList<Point2D>> CornerEnds(BimDocument document, Wall wall, WallType type)
    {
        if (type.Log is null || wall.IsCurved) yield break;

        var structure = type.Structure;
        var half = structure.TotalWidth / 2;
        var (bodyStart, bodyEnd) = wall.GetBodyCentreline(structure);
        var direction = (bodyEnd - bodyStart).NormalisedOrDefault(Vector2D.UnitX);
        var across = direction.PerpendicularLeft() * half;
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);

        foreach (var (at, way, runOn) in new[] { (bodyStart, -direction, RunOn(type, startCut)), (bodyEnd, direction, RunOn(type, endCut)) })
        {
            if (runOn <= 0) continue;
            var (near, far) = (at + way * half, at + way * runOn);
            yield return new[] { near - across, far - across, far + across, near + across };
        }
    }
}
