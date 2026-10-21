using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Views;

/// <summary>
/// Walking through the model at eye height: standing on whatever floor, stair, ramp, terrace or
/// ground is underfoot, climbing a step or a stair as it comes, falling down the open side of
/// one - and stopped by walls, glass and railings, though not by doors, which are walked through
/// as if open.
/// </summary>
public static class Walking
{
    /// <summary>The highest step taken in one stride without climbing, mm: a stair's riser, a kerb, comfortably.</summary>
    public const double StepUp = 450;

    /// <summary>How far in front of the eye the body is stopped short of a wall, mm.</summary>
    public const double Reach = 300;

    /// <summary>What can be stood on.</summary>
    private static readonly HashSet<MeshKind> Ground = new() { MeshKind.Floor, MeshKind.Stair, MeshKind.Ramp, MeshKind.Terrain, MeshKind.Roof };

    /// <summary>What stops a walker: doors are not among them, and neither is what is stood on.</summary>
    private static readonly HashSet<MeshKind> Solid = new() { MeshKind.Wall, MeshKind.Glazing, MeshKind.Mullion, MeshKind.Railing, MeshKind.Beam };

    /// <summary>
    /// The top of what is underfoot at a point - the highest floor, tread, ramp or ground no more
    /// than a step above the feet - or null over nothing at all.
    /// </summary>
    public static double? FloorUnder(IReadOnlyList<Mesh3D> meshes, Point2D at, double feet, double stepUp = StepUp)
    {
        double? best = null;
        var reach = feet + stepUp;
        foreach (var mesh in meshes)
        {
            if (!Ground.Contains(mesh.Kind) || mesh.Bounds() is not { } bounds) continue;
            if (at.X < bounds.Min.X || at.X > bounds.Max.X || at.Y < bounds.Min.Y || at.Y > bounds.Max.Y || bounds.Min.Z > reach) continue;

            var (positions, indices) = (mesh.Positions, mesh.Indices);
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var (a, b, c) = (positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]);
                if (HeightIn(a, b, c, at) is not { } z || z > reach || z <= best) continue;

                // Only the tops of things: a face turned up.
                var normalZ = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (Math.Abs(normalZ) < 1e-6) continue;
                best = z;
            }
        }

        return best;
    }

    /// <summary>Where a vertical line through a point meets a triangle, or null where it misses.</summary>
    private static double? HeightIn(Point3D a, Point3D b, Point3D c, Point2D at)
    {
        var det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(det) < 1e-9) return null;

        var l1 = ((b.Y - c.Y) * (at.X - c.X) + (c.X - b.X) * (at.Y - c.Y)) / det;
        var l2 = ((c.Y - a.Y) * (at.X - c.X) + (a.X - c.X) * (at.Y - c.Y)) / det;
        var l3 = 1 - l1 - l2;
        const double edge = -1e-9;
        if (l1 < edge || l2 < edge || l3 < edge) return null;
        return l1 * a.Z + l2 * b.Z + l3 * c.Z;
    }

    /// <summary>
    /// Whether a stride from one point to another, the feet at a height, runs into something -
    /// tested at the knee, the chest and the eye, and a body's reach ahead.
    /// </summary>
    public static bool Blocked(IReadOnlyList<Mesh3D> meshes, Point2D from, Point2D to, double feet, double eyeHeight, double width = 0)
    {
        var stride = to - from;
        if (stride.Length < 1e-6) return false;
        var way = stride.NormalisedOrDefault(Vector2D.UnitX);
        var ahead = to + way * Reach;

        // Down the middle - and, for someone with a width, down each side as well.
        var sides = width > 0 ? new[] { 0.0, width / 2, -width / 2 } : new[] { 0.0 };
        foreach (var side in sides)
        foreach (var height in new[] { Math.Min(StepUp + 50, Math.Max(300, eyeHeight * 0.3)), eyeHeight * 0.75, eyeHeight })
        {
            var offset = way.PerpendicularLeft() * side;
            var (p, q) = (Point3D.On(from + offset, feet + height), Point3D.On(ahead + offset, feet + height));
            foreach (var mesh in meshes)
            {
                if (!Solid.Contains(mesh.Kind) || mesh.Bounds() is not { } bounds) continue;
                if (Math.Max(p.X, q.X) < bounds.Min.X || Math.Min(p.X, q.X) > bounds.Max.X || Math.Max(p.Y, q.Y) < bounds.Min.Y ||
                    Math.Min(p.Y, q.Y) > bounds.Max.Y || p.Z < bounds.Min.Z || p.Z > bounds.Max.Z) continue;

                var (positions, indices) = (mesh.Positions, mesh.Indices);
                for (var i = 0; i + 2 < indices.Count; i += 3)
                    if (Crosses(p, q, positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]])) return true;
            }
        }

        return false;
    }

    /// <summary>Whether a line between two points passes through a triangle (Möller and Trumbore).</summary>
    private static bool Crosses(Point3D p, Point3D q, Point3D a, Point3D b, Point3D c)
    {
        var dir = Camera3D.Minus(q, p);
        var (e1, e2) = (Camera3D.Minus(b, a), Camera3D.Minus(c, a));
        var h = Camera3D.Cross(dir, e2);
        var det = Camera3D.Dot(e1, h);
        if (Math.Abs(det) < 1e-9) return false;

        var s = Camera3D.Minus(p, a);
        var u = Camera3D.Dot(s, h) / det;
        if (u < 0 || u > 1) return false;
        var qv = Camera3D.Cross(s, e1);
        var v = Camera3D.Dot(dir, qv) / det;
        if (v < 0 || u + v > 1) return false;
        var t = Camera3D.Dot(e2, qv) / det;
        return t >= 0 && t <= 1;
    }
}

/// <summary>
/// A walkthrough (specification section 6.1): a path drawn on a storey that a camera follows at
/// eye height, looking the way it goes - played in the 3D window, frame by frame. Revit's
/// defaults: 300 frames at 15 a second, twenty seconds, at an even speed.
/// </summary>
public sealed class Walkthrough : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Views;

    public string Name { get; set; } = "Walkthrough 1";

    private readonly List<Point2D> _path = new();

    /// <summary>The points the camera passes through, in plan, on its storey.</summary>
    public IReadOnlyList<Point2D> Path => _path;

    public void SetPath(IEnumerable<Point2D> points)
    {
        _path.Clear();
        _path.AddRange(points);
    }

    /// <summary>How high the eye is above the storey, mm.</summary>
    public double EyeHeight { get; set; } = Views3D.EyeHeight;

    public int Frames { get; set; } = 300;

    public double FramesPerSecond { get; set; } = 15;

    public double Length => Enumerable.Range(0, Math.Max(_path.Count - 1, 0)).Sum(i => _path[i].DistanceTo(_path[i + 1]));

    /// <summary>How long it takes to play, seconds.</summary>
    public double Duration => Frames / Math.Max(FramesPerSecond, 1);

    /// <summary>The point so far along the path, and the way it is going there.</summary>
    public (Point2D At, Vector2D Heading) Along(double distance)
    {
        if (_path.Count == 0) return (new Point2D(0, 0), Vector2D.UnitY);
        if (_path.Count == 1) return (_path[0], Vector2D.UnitY);

        distance = Math.Clamp(distance, 0, Length);
        for (var i = 0; i + 1 < _path.Count; i++)
        {
            var leg = _path[i].DistanceTo(_path[i + 1]);
            if (distance <= leg || i + 2 == _path.Count)
            {
                var heading = (_path[i + 1] - _path[i]).NormalisedOrDefault(Vector2D.UnitY);
                return (_path[i] + heading * Math.Min(distance, leg), heading);
            }

            distance -= leg;
        }

        return (_path[^1], Vector2D.UnitY);
    }

    /// <summary>
    /// The camera at a frame, counted from 0: on the path at an even speed, at eye height, looking
    /// a few paces ahead along it - so it turns into a corner as it comes, rather than snapping round.
    /// </summary>
    public Camera3D FrameAt(BimDocument document, int frame)
    {
        var floor = document.FindLevel(LevelId)?.Elevation ?? 0;
        var share = Frames <= 1 ? 0 : Math.Clamp(frame, 0, Frames - 1) / (double)(Frames - 1);
        var distance = share * Length;
        var (at, heading) = Along(distance);
        var ahead = distance + 1500 <= Length ? Along(distance + 1500).At : at + heading * 1500;
        if (ahead.DistanceTo(at) < 1) ahead = at + heading * 1500;

        return new Camera3D(Point3D.On(at, floor + EyeHeight), Point3D.On(ahead, floor + EyeHeight)) { FieldOfView = 50 };
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(View3DParameters.ViewName, () => Name, value => { if (!string.IsNullOrWhiteSpace(value)) Name = value.Trim(); });
        yield return ParameterValue.BindValidated(WalkthroughParameters.Frames, () => Frames, value =>
        {
            if (value is < 2 or > 100000) return false;
            Frames = value;
            return true;
        });
        yield return ParameterValue.BindValidated(WalkthroughParameters.FramesPerSecond, () => FramesPerSecond, value =>
        {
            if (!double.IsFinite(value) || value is < 1 or > 120) return false;
            FramesPerSecond = value;
            return true;
        });
        yield return ParameterValue.BindValidated(View3DParameters.EyeElevation, () => EyeHeight, value =>
        {
            if (!double.IsFinite(value) || value is < 100 or > 100000) return false;
            EyeHeight = value;
            return true;
        });
        yield return ParameterValue.ReadOnly(WalkthroughParameters.PathLength, () => Length);
        yield return ParameterValue.ReadOnly(WalkthroughParameters.Duration, () => $"{Duration:0.#} s");
        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

public static class WalkthroughParameters
{
    public static readonly ParameterDefinition Frames = new("Total Frames", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Other);
    public static readonly ParameterDefinition FramesPerSecond = new("Frames per Second", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Other);
    public static readonly ParameterDefinition PathLength = new("Path Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Duration = new("Duration", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Other);
}
