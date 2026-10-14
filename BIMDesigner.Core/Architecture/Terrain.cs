using System.Globalization;
using System.Runtime.CompilerServices;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The ground: a surface through spot levels - points in plan, each at its height - drawn
/// between them as the fattest triangles they allow. Contoured in plan, cut away under the
/// floors of the storey it is on as a building pad is, and what stands outside on it stands on
/// it there: a stair's foot, a railing, the drop a balcony's guarding is measured to.
/// </summary>
public sealed class Toposurface : Element
{
    private readonly List<Point3D> _points = new();

    public override BuiltInCategory Category => BuiltInCategory.Topography;

    /// <summary>Its spot levels: where each is in plan, and its height in the project, mm.</summary>
    public IReadOnlyList<Point3D> Points => _points;

    /// <summary>Changed each time its points are, so what is worked out from them is worked out again.</summary>
    public int Version { get; private set; }

    public void SetPoints(IEnumerable<Point3D> points)
    {
        var replacement = points.ToList();
        _points.Clear();
        _points.AddRange(replacement);
        Version++;
    }

    /// <summary>A material of the project's for it; none for grass.</summary>
    public Guid MaterialId { get; set; }

    /// <summary>How far apart its contours are in height, mm; every fifth is a major one, labelled.</summary>
    public double ContourInterval { get; set; } = 250;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(TerrainParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        var names = document.Materials.Select(material => material.Name).OrderBy(name => name).ToArray();
        yield return ParameterValue.BindChoice(TerrainParameters.Material,
            () => document.FindMaterial(MaterialId)?.Name ?? "Grass",
            v => MaterialId = document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty,
            names.Prepend("Grass").ToArray());
        yield return ParameterValue.BindValidated(TerrainParameters.ContourInterval, () => ContourInterval, (double v) =>
        {
            if (!double.IsFinite(v) || v < 50 || v > 10000) return false;
            ContourInterval = v;
            return true;
        });
        yield return ParameterValue.ReadOnly(TerrainParameters.Points, () => Points.Count);
        yield return ParameterValue.ReadOnly(TerrainParameters.Area, () => Terrains.Area(this));
        yield return ParameterValue.ReadOnly(TerrainParameters.Lowest, () => Points.Count == 0 ? 0 : Points.Min(point => point.Z));
        yield return ParameterValue.ReadOnly(TerrainParameters.Highest, () => Points.Count == 0 ? 0 : Points.Max(point => point.Z));
        yield return ParameterValue.ReadOnly(TerrainParameters.Rule, () => Terrains.Verdict(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class TerrainParameters
{
    public static readonly ParameterDefinition Level = new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition ContourInterval = new("Contour Interval", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Points = new("Points", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Area = new("Projected Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Lowest = new("Lowest Point", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Highest = new("Highest Point", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rule = new("Ground at the Walls", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>
/// What the ground is: its triangles, its height at any point, its contours, where the building
/// is set into it, and whether it is kept down and falls away from the walls.
/// </summary>
public static class Terrains
{
    /// <summary>Grass, as the ground reads with no material.</summary>
    public static readonly ColourRgb Grass = new(0x7E, 0x9C, 0x5B);

    /// <summary>How far below its lowest point its sides go, so it reads as ground, not a sheet, mm.</summary>
    public const double Depth = 1000;

    /// <summary>How far above or below a level the ground may be and still be what something on that level stands on outside, mm.</summary>
    public const double Reach = 1500;

    private sealed record Worked(int Version, IReadOnlyList<(int A, int B, int C)> Triangles);

    private static readonly ConditionalWeakTable<Toposurface, Worked> Cache = new();

    /// <summary>Its triangles, by its points' indices - worked out once each time its points change.</summary>
    public static IReadOnlyList<(int A, int B, int C)> Triangles(Toposurface surface)
    {
        if (Cache.TryGetValue(surface, out var worked) && worked.Version == surface.Version) return worked.Triangles;
        var triangles = Delaunay.Triangulate(surface.Points.Select(point => new Point2D(point.X, point.Y)).ToList());
        Cache.AddOrUpdate(surface, new Worked(surface.Version, triangles));
        return triangles;
    }

    /// <summary>Its height over a point in plan, between the three spot levels round it; none beyond it.</summary>
    public static double? HeightAt(Toposurface surface, Point2D at)
    {
        var points = surface.Points;
        foreach (var (a, b, c) in Triangles(surface))
        {
            var (pa, pb, pc) = (points[a], points[b], points[c]);
            var area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            if (Math.Abs(area) < 1e-9) continue;
            var u = ((pb.X - at.X) * (pc.Y - at.Y) - (pb.Y - at.Y) * (pc.X - at.X)) / area;
            var v = ((pc.X - at.X) * (pa.Y - at.Y) - (pc.Y - at.Y) * (pa.X - at.X)) / area;
            var w = 1 - u - v;
            if (u < -1e-9 || v < -1e-9 || w < -1e-9) continue;
            return u * pa.Z + v * pb.Z + w * pc.Z;
        }

        return null;
    }

    /// <summary>The ground over a point in plan near a height - the highest, where surfaces lie one over another; none where there is no ground there near it.</summary>
    public static double? GroundNear(BimDocument document, double height, Point2D at, double reach = Reach) =>
        document.Elements.OfType<Toposurface>()
            .Select(surface => HeightAt(surface, at))
            .Where(ground => ground is { } value && Math.Abs(value - height) <= reach)
            .Max();

    /// <summary>The ground over a point in plan, wherever it is; none where there is none.</summary>
    public static double? GroundAt(BimDocument document, Point2D at) =>
        document.Elements.OfType<Toposurface>().Select(surface => HeightAt(surface, at)).Where(ground => ground is not null).Max();

    /// <summary>Its area in plan: its triangles'.</summary>
    public static double Area(Toposurface surface)
    {
        var points = surface.Points;
        return Triangles(surface).Sum(t => Math.Abs((points[t.B].X - points[t.A].X) * (points[t.C].Y - points[t.A].Y) - (points[t.B].Y - points[t.A].Y) * (points[t.C].X - points[t.A].X)) / 2);
    }

    /// <summary>Where the building is set into it: the floors of the storey it is on, each cut out of it as a building pad.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Pads(BimDocument document, Toposurface surface) =>
        document.Elements.OfType<Floor>().Where(floor => floor.LevelId == surface.LevelId && floor.Boundary.Count >= 3).Select(floor => floor.Boundary).ToList();

    private static bool InPad(IReadOnlyList<IReadOnlyList<Point2D>> pads, Point2D at) => pads.Any(pad => Polygon2D.Contains(pad, at));

    /// <summary>
    /// Its contours: at each multiple of its interval between its lowest and highest points, the
    /// lines where it crosses that height, triangle by triangle - every fifth a major one - left
    /// out where the building is set into it.
    /// </summary>
    public static IReadOnlyList<(double Height, Point2D From, Point2D To, bool Major)> Contours(BimDocument document, Toposurface surface)
    {
        var contours = new List<(double, Point2D, Point2D, bool)>();
        var points = surface.Points;
        if (points.Count < 3) return contours;
        var interval = Math.Max(50, surface.ContourInterval);
        var pads = Pads(document, surface);
        var (low, high) = (points.Min(point => point.Z), points.Max(point => point.Z));
        if ((high - low) / interval > 400) interval = (high - low) / 400;

        foreach (var (a, b, c) in Triangles(surface))
        {
            var corners = new[] { points[a], points[b], points[c] };
            var (least, most) = (corners.Min(point => point.Z), corners.Max(point => point.Z));
            for (var k = Math.Ceiling(least / interval); k * interval <= most; k++)
            {
                var height = k * interval;
                if (height <= least || height > most) continue;
                var crossings = new List<Point2D>();
                for (var i = 0; i < 3; i++)
                {
                    var (p, q) = (corners[i], corners[(i + 1) % 3]);
                    if ((p.Z - height) * (q.Z - height) > 0 || Math.Abs(q.Z - p.Z) < 1e-9) continue;
                    var t = (height - p.Z) / (q.Z - p.Z);
                    if (t < 0 || t > 1) continue;
                    crossings.Add(new Point2D(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t));
                }

                var distinct = crossings.Distinct().ToList();
                if (distinct.Count < 2 || InPad(pads, distinct[0].MidpointTo(distinct[1]))) continue;
                contours.Add((height, distinct[0], distinct[1], Math.Abs(Math.IEEERemainder(height, interval * 5)) < 1e-6));
            }
        }

        return contours;
    }

    /// <summary>
    /// The ground in 3D: its surface between its points, cut away where the building is set into
    /// it - the pads' edges laid into it so the cut follows them - with sides down below its
    /// lowest point, so it reads as earth.
    /// </summary>
    public static Mesh3D? Mesh(BimDocument document, Toposurface surface)
    {
        var points = surface.Points;
        if (points.Count < 3 || Triangles(surface).Count == 0) return null;
        var material = document.FindMaterial(surface.MaterialId);
        var mesh = new Mesh3D(surface.Id, surface.LevelId, MeshKind.Terrain, material?.SurfaceColour ?? Grass, material?.Name ?? "Grass");

        // Its points, and the pads' edges every 500 laid onto it.
        var pads = Pads(document, surface);
        var all = points.ToList();
        foreach (var pad in pads)
            for (var i = 0; i < pad.Count; i++)
            {
                var (a, b) = (pad[i], pad[(i + 1) % pad.Count]);
                var count = Math.Max(1, (int)Math.Ceiling((b - a).Length / 500));
                for (var k = 0; k < count; k++)
                {
                    var at = a + (b - a) * ((double)k / count);
                    if (HeightAt(surface, at) is { } height) all.Add(new Point3D(at.X, at.Y, height));
                }
            }

        var plan = all.Select(point => new Point2D(point.X, point.Y)).ToList();
        foreach (var (a, b, c) in Delaunay.Triangulate(plan))
        {
            var middle = new Point2D((plan[a].X + plan[b].X + plan[c].X) / 3, (plan[a].Y + plan[b].Y + plan[c].Y) / 3);
            if (InPad(pads, middle)) continue;
            mesh.AddTriangle(all[a], all[b], all[c]);
        }

        // Its sides, round the edge of its own points' triangles, down below its lowest.
        var bottom = points.Min(point => point.Z) - Depth;
        var edges = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in Triangles(surface))
            foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = from < to ? (from, to) : (to, from);
                edges[key] = edges.TryGetValue(key, out var seen) ? seen + 1 : 1;
            }

        foreach (var (a, b, c) in Triangles(surface))
            foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) })
            {
                if (edges[from < to ? (from, to) : (to, from)] != 1) continue;
                var (p, q) = (points[from], points[to]);
                mesh.AddQuad(p, new Point3D(p.X, p.Y, bottom), new Point3D(q.X, q.Y, bottom), q);
                mesh.AddEdge(p, q);
            }

        return mesh.IsEmpty ? null : mesh;
    }

    /// <summary>
    /// What about the ground against the building falls short. Approved Document C: the damp-proof
    /// course 150 above the ground beside the walls - the ground 150 below the floor at least; the
    /// IRC: 6 in (152) of foundation above the finished grade, and the ground falling 6 in (152)
    /// in the first 10 ft (3048) away from the walls.
    /// </summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Toposurface surface)
    {
        var problems = new List<string>();
        if (surface.Points.Count < 3) return problems;
        var us = document.ProjectInformation.ClearanceStandard == ClearanceStandard.Ada;
        var pads = Pads(document, surface);
        var level = document.FindLevel(surface.LevelId)?.Elevation ?? 0;
        double? worstKept = null, worstFall = null;

        foreach (var wall in document.Walls.Where(wall => wall.LevelId == surface.LevelId))
        {
            if (document.GetWallType(wall) is not { } type || type.Function != WallFunction.Exterior || wall.Length < 1) continue;
            var half = type.Structure.TotalWidth / 2;
            var count = Math.Max(1, (int)Math.Floor(wall.Length / 1000));
            for (var k = 0; k <= count; k++)
            {
                var along = wall.Length * k / count;
                foreach (var side in new[] { 1.0, -1.0 })
                {
                    var near = wall.PointAt(type.Structure, along, side * (half + 100));
                    if (InPad(pads, near) || HeightAt(surface, near) is not { } ground) continue;

                    // The floor inside, across the wall: the pad's top there, or the level.
                    var inside = wall.PointAt(type.Structure, along, -side * (half + 300));
                    var floor = document.Elements.OfType<Floor>().Where(candidate => candidate.LevelId == surface.LevelId && candidate.Contains(inside))
                        .Select(candidate => candidate.GetTopElevation(document)).DefaultIfEmpty(level).Max();
                    var kept = floor - ground;
                    if (worstKept is not { } known || kept < known) worstKept = kept;

                    if (us && HeightAt(surface, wall.PointAt(type.Structure, along, side * (half + 3048))) is { } away)
                    {
                        var falls = ground - away;
                        if (worstFall is not { } fell || falls < fell) worstFall = falls;
                    }
                }
            }
        }

        if (worstKept is { } least && least < 150 - 0.5)
            problems.Add(us
                ? $"The ground comes up to {least:0} below the floor beside its walls: the IRC asks for 6 in (152) of foundation above the finished grade."
                : $"The ground comes up to {least:0} below the floor beside its walls: Approved Document C asks for the damp-proof course 150 above the ground at least.");
        if (worstFall is { } fall && fall < 152 - 0.5)
            problems.Add(Math.Round(fall) < 0
                ? $"The ground rises {-fall:0} toward its walls over the first 10 ft (3048): the IRC asks for it to fall 6 in (152) away from them, or 5%, so water runs off."
                : $"The ground falls only {Math.Round(fall) + 0.0:0} in the first 10 ft (3048) away from its walls: the IRC asks for 6 in (152), or 5%, so water runs off.");
        return problems;
    }

    public static string Verdict(BimDocument document, Toposurface surface)
    {
        var problems = Problems(document, surface);
        return problems.Count == 0 ? "Clear of the walls, and falling away" : string.Join(" ", problems);
    }

    /// <summary>
    /// Spot levels read from a points file: a line to each - its x, its y and its height, the
    /// first three numbers on it, comma, semicolon, tab or space between - in millimetres, or
    /// metres at a scale of 1000. Where two fall on the same place, the higher is kept.
    /// </summary>
    public static IReadOnlyList<Point3D> ReadPoints(string text, double scale = 1)
    {
        var points = new Dictionary<(long, long), Point3D>();
        foreach (var line in text.Split('\n'))
        {
            var numbers = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(field => double.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null)
                .TakeWhile(value => value is not null).Select(value => value!.Value * scale).Take(3).ToList();
            if (numbers.Count < 3 || !numbers.All(double.IsFinite)) continue;
            var key = ((long)Math.Round(numbers[0]), (long)Math.Round(numbers[1]));
            if (!points.TryGetValue(key, out var seen) || numbers[2] > seen.Z) points[key] = new Point3D(numbers[0], numbers[1], numbers[2]);
        }

        return points.Values.ToList();
    }
}

/// <summary>The ground's spot levels changed - one added, moved, taken away, or read in - and changed back.</summary>
public sealed class SetToposurfacePointsCommand : Documents.Commands.IUndoableCommand
{
    private readonly Toposurface _surface;
    private readonly IReadOnlyList<Point3D> _before;
    private readonly IReadOnlyList<Point3D> _after;

    public SetToposurfacePointsCommand(Toposurface surface, IEnumerable<Point3D> after, string name = "Ground Points")
    {
        _surface = surface;
        _before = surface.Points.ToList();
        _after = after.ToList();
        Name = name;
    }

    public string Name { get; }

    public void Redo() => _surface.SetPoints(_after);

    public void Undo() => _surface.SetPoints(_before);
}
