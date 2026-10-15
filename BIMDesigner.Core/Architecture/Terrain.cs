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
/// floors of the storey it is on as a building pad is, stepped at the retaining walls standing
/// in it, and what stands outside on it stands on it there: a stair's foot, a railing, the drop
/// a balcony's guarding is measured to. With the ground as it was recorded, what is cut and filled.
/// </summary>
public sealed class Toposurface : Element
{
    private readonly List<Point3D> _points = new();
    private readonly List<Point3D> _existing = new();

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

    /// <summary>
    /// The ground as it was when it was recorded, before it was graded - what is cut and filled
    /// is measured from it; none where it has not been recorded, and the ground is as it is.
    /// </summary>
    public IReadOnlyList<Point3D> ExistingPoints => _existing;

    public void SetExistingPoints(IEnumerable<Point3D> points)
    {
        var replacement = points.ToList();
        _existing.Clear();
        _existing.AddRange(replacement);
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
        yield return ParameterValue.ReadOnly(TerrainParameters.Existing, () => ExistingPoints.Count >= 3 ? $"Recorded, {ExistingPoints.Count} points" : "As it is");
        yield return ParameterValue.ReadOnly(TerrainParameters.Cut, () => Terrains.CutAndFill(document, this).Cut);
        yield return ParameterValue.ReadOnly(TerrainParameters.Fill, () => Terrains.CutAndFill(document, this).Fill);
        yield return ParameterValue.ReadOnly(TerrainParameters.Net, () => Terrains.CutAndFill(document, this) is var (cut, fill) ? cut - fill : 0);
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
    public static readonly ParameterDefinition Existing = new("Existing Ground", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Phasing);
    public static readonly ParameterDefinition Cut = new("Cut", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Fill = new("Fill", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Net = new("Net Cut/Fill", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rule = new("Ground at the Walls", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>
/// What the ground is: its triangles, its height at any point, its contours, where the building
/// is set into it, where retaining walls step it, the regions laid on it, what is cut and filled,
/// and whether it is kept down and falls away from the walls.
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
    private static readonly ConditionalWeakTable<Toposurface, Worked> ExistingCache = new();

    /// <summary>The ground as it is worked: its points with the retaining walls' faces laid in, its triangles, and the walls' footprints, where there is none.</summary>
    private sealed record Model(string Signature, IReadOnlyList<Point3D> Points, IReadOnlyList<(int A, int B, int C)> Triangles, IReadOnlyList<IReadOnlyList<Point2D>> Walls);

    private static readonly ConditionalWeakTable<Toposurface, Model> Models = new();

    /// <summary>Its triangles, by its points' indices - worked out once each time its points change.</summary>
    public static IReadOnlyList<(int A, int B, int C)> Triangles(Toposurface surface)
    {
        if (Cache.TryGetValue(surface, out var worked) && worked.Version == surface.Version) return worked.Triangles;
        var triangles = Delaunay.Triangulate(surface.Points.Select(point => new Point2D(point.X, point.Y)).ToList());
        Cache.AddOrUpdate(surface, new Worked(surface.Version, triangles));
        return triangles;
    }

    /// <summary>Its height over a point in plan, between the three spot levels round it - as surveyed, with nothing stepping it; none beyond it.</summary>
    public static double? HeightAt(Toposurface surface, Point2D at) => Interpolate(surface.Points, Triangles(surface), at);

    /// <summary>Its height over a point in plan, stepped where retaining walls stand in it; none beyond it, nor in a wall.</summary>
    public static double? HeightAt(BimDocument document, Toposurface surface, Point2D at) => HeightIn(Graded(document, surface), at);

    private static double? HeightIn(Model model, Point2D at) =>
        model.Walls.Any(wall => Polygon2D.Contains(wall, at)) ? null : Interpolate(model.Points, model.Triangles, at);

    private static double? Interpolate(IReadOnlyList<Point3D> points, IReadOnlyList<(int A, int B, int C)> triangles, Point2D at)
    {
        foreach (var (a, b, c) in triangles)
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

    /// <summary>The retaining walls standing in it: walls of that function on its level, over it.</summary>
    public static IReadOnlyList<(Wall Wall, WallType Type)> RetainingWalls(BimDocument document, Toposurface surface) =>
        document.Walls
            .Where(wall => wall.LevelId == surface.LevelId && wall.Length > 1)
            .Select(wall => (Wall: wall, Type: document.GetWallType(wall)))
            .Where(pair => pair.Type is { Function: WallFunction.Retaining } && HeightAt(surface, pair.Wall.LocationCurve.PointAt(pair.Wall.Length / 2)) is not null)
            .Select(pair => (pair.Wall, pair.Type!))
            .ToList();

    /// <summary>
    /// The ground as it is worked, built again when its points or its retaining walls change. A
    /// retaining wall steps it: along the face of the side the ground is higher, it is held level
    /// with the wall's top - the earth the wall holds back - and along the other at its foot; under
    /// the wall there is none.
    /// </summary>
    private static Model Graded(BimDocument document, Toposurface surface)
    {
        var walls = RetainingWalls(document, surface);
        var signature = $"{surface.Version}|" + string.Join(";", walls.Select(pair =>
            $"{pair.Wall.Id}:{pair.Wall.Start}:{pair.Wall.End}:{pair.Wall.Bulge}:{pair.Wall.Ellipse}:{pair.Wall.Spline?.GetHashCode()}:{pair.Wall.LocationLine}:" +
            $"{pair.Wall.Flipped}:{pair.Wall.AcrossOffset}:{pair.Wall.GetBaseElevation(document):0}:{pair.Wall.GetTopElevation(document):0}:{pair.Type.Structure.TotalWidth:0}"));
        if (Models.TryGetValue(surface, out var known) && known.Signature == signature) return known;

        var points = surface.Points.ToList();
        var footprints = new List<IReadOnlyList<Point2D>>();
        foreach (var (wall, type) in walls)
        {
            var structure = type.Structure;
            var half = structure.TotalWidth / 2;
            var (bottom, top) = (wall.GetBaseElevation(document), wall.GetTopElevation(document));

            // The side it holds back: where the ground is higher, a metre off it.
            double Side(double side) => Enumerable.Range(0, 5)
                .Select(k => HeightAt(surface, wall.PointAt(structure, wall.Length * (k + 0.5) / 5, side * (half + 1000))))
                .Where(height => height is not null).Select(height => height!.Value).DefaultIfEmpty(double.NegativeInfinity).Average();
            var high = Side(1) >= Side(-1) ? 1.0 : -1.0;

            var count = Math.Max(1, (int)Math.Ceiling(wall.Length / 500));
            for (var k = 0; k <= count; k++)
            {
                var along = wall.Length * k / count;
                foreach (var side in new[] { 1.0, -1.0 })
                {
                    var at = wall.PointAt(structure, along, side * (half + 5));
                    if (HeightAt(surface, at) is null) continue;
                    points.Add(new Point3D(at.X, at.Y, side == high ? top : bottom));
                }
            }

            footprints.Add(CurtainGeometry.Band(wall, type, 0, wall.Length, half, -half));
        }

        var plan = points.Select(point => new Point2D(point.X, point.Y)).ToList();
        var triangles = Delaunay.Triangulate(plan)
            .Where(t => !footprints.Any(wall => Polygon2D.Contains(wall, new Point2D((plan[t.A].X + plan[t.B].X + plan[t.C].X) / 3, (plan[t.A].Y + plan[t.B].Y + plan[t.C].Y) / 3))))
            .ToList();
        var model = new Model(signature, points, triangles, footprints);
        Models.AddOrUpdate(surface, model);
        return model;
    }

    /// <summary>Its faces as it is worked: each triangle's corners, in the model.</summary>
    public static IEnumerable<(Point3D A, Point3D B, Point3D C)> Faces(BimDocument document, Toposurface surface)
    {
        var model = Graded(document, surface);
        return model.Triangles.Select(t => (model.Points[t.A], model.Points[t.B], model.Points[t.C]));
    }

    /// <summary>The ground over a point in plan near a height - the highest, where surfaces lie one over another; none where there is no ground there near it.</summary>
    public static double? GroundNear(BimDocument document, double height, Point2D at, double reach = Reach) =>
        document.Elements.OfType<Toposurface>()
            .Select(surface => HeightAt(document, surface, at))
            .Where(ground => ground is { } value && Math.Abs(value - height) <= reach)
            .Max();

    /// <summary>The ground over a point in plan, wherever it is; none where there is none.</summary>
    public static double? GroundAt(BimDocument document, Point2D at) =>
        document.Elements.OfType<Toposurface>().Select(surface => HeightAt(document, surface, at)).Where(ground => ground is not null).Max();

    /// <summary>Its area in plan: its triangles'.</summary>
    public static double Area(Toposurface surface)
    {
        var points = surface.Points;
        return Triangles(surface).Sum(t => Math.Abs((points[t.B].X - points[t.A].X) * (points[t.C].Y - points[t.A].Y) - (points[t.B].Y - points[t.A].Y) * (points[t.C].X - points[t.A].X)) / 2);
    }

    /// <summary>Where the building is set into it: the floors of the storey it is on, each cut out of it as a building pad.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Pads(BimDocument document, Toposurface surface) =>
        PadFloors(document, surface).Select(floor => floor.Boundary).ToList();

    private static IReadOnlyList<Floor> PadFloors(BimDocument document, Toposurface surface) =>
        document.Elements.OfType<Floor>().Where(floor => floor.LevelId == surface.LevelId && floor.Boundary.Count >= 3).ToList();

    private static bool InPad(IReadOnlyList<IReadOnlyList<Point2D>> pads, Point2D at) => pads.Any(pad => Polygon2D.Contains(pad, at));

    /// <summary>The regions laid on it: lawns, paths, paving, gravel, planting.</summary>
    public static IReadOnlyList<GroundRegion> Regions(BimDocument document, Toposurface surface) =>
        document.Elements.OfType<GroundRegion>().Where(region => region.Boundary.Count >= 3 && ReferenceEquals(GroundRegions.Host(document, region), surface)).ToList();

    /// <summary>
    /// Its contours: at each multiple of its interval between its lowest and highest points, the
    /// lines where it crosses that height, triangle by triangle - every fifth a major one - left
    /// out where the building is set into it. As it was recorded, before it was graded, where it
    /// was: the existing ground's.
    /// </summary>
    public static IReadOnlyList<(double Height, Point2D From, Point2D To, bool Major)> Contours(BimDocument document, Toposurface surface, bool existing = false)
    {
        var contours = new List<(double, Point2D, Point2D, bool)>();
        IReadOnlyList<Point3D> points;
        IReadOnlyList<(int A, int B, int C)> triangles;
        if (existing)
        {
            if (surface.ExistingPoints.Count < 3) return contours;
            (points, triangles) = (surface.ExistingPoints, ExistingTriangles(surface));
        }
        else
        {
            var model = Graded(document, surface);
            (points, triangles) = (model.Points, model.Triangles);
        }

        if (points.Count < 3) return contours;
        var interval = Math.Max(50, surface.ContourInterval);
        var pads = Pads(document, surface);
        var (low, high) = (points.Min(point => point.Z), points.Max(point => point.Z));
        if ((high - low) / interval > 400) interval = (high - low) / 400;

        foreach (var (a, b, c) in triangles)
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

    /// <summary>The ground in 3D, as <see cref="Meshes"/> builds it: the ground itself, without the regions laid on it.</summary>
    public static Mesh3D? Mesh(BimDocument document, Toposurface surface) =>
        Meshes(document, surface).FirstOrDefault(mesh => mesh.ElementId == surface.Id);

    /// <summary>
    /// The ground in 3D: its surface between its points, stepped at its retaining walls and cut
    /// away where the building is set into it - the pads' and the regions' edges laid into it so
    /// the cut and the regions follow them - each region in its own colour on it, and sides down
    /// below its lowest point, so it reads as earth.
    /// </summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Toposurface surface)
    {
        var points = surface.Points;
        if (points.Count < 3 || Triangles(surface).Count == 0) return Array.Empty<Mesh3D>();
        var material = document.FindMaterial(surface.MaterialId);
        var ground = new Mesh3D(surface.Id, surface.LevelId, MeshKind.Terrain, material?.SurfaceColour ?? Grass, material?.Name ?? "Grass");
        var model = Graded(document, surface);
        double? HeightAt(BimDocument _, Toposurface __, Point2D at) => HeightIn(model, at);

        // Its points, and the pads' and the regions' edges every 500 laid onto it.
        var pads = Pads(document, surface);
        var regions = Regions(document, surface);
        var all = model.Points.ToList();
        foreach (var outline in pads.Concat(regions.Select(region => region.Boundary)))
            for (var i = 0; i < outline.Count; i++)
            {
                var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
                var count = Math.Max(1, (int)Math.Ceiling((b - a).Length / 500));
                for (var k = 0; k < count; k++)
                {
                    var at = a + (b - a) * ((double)k / count);
                    if (HeightAt(document, surface, at) is { } height) all.Add(new Point3D(at.X, at.Y, height));
                }
            }

        var laid = regions.ToDictionary(region => region, region =>
        {
            var made = document.FindMaterial(region.MaterialId);
            return new Mesh3D(region.Id, region.LevelId, MeshKind.Terrain, made?.SurfaceColour ?? GroundRegions.Colour(region.Surface), made?.Name ?? EnumText.Humanise(region.Surface));
        });

        var plan = all.Select(point => new Point2D(point.X, point.Y)).ToList();
        foreach (var (a, b, c) in Delaunay.Triangulate(plan))
        {
            var middle = new Point2D((plan[a].X + plan[b].X + plan[c].X) / 3, (plan[a].Y + plan[b].Y + plan[c].Y) / 3);
            if (InPad(pads, middle) || HeightAt(document, surface, middle) is null) continue;
            var region = regions.FirstOrDefault(candidate => Polygon2D.Contains(candidate.Boundary, middle));
            (region is null ? ground : laid[region]).AddTriangle(all[a], all[b], all[c]);
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
                ground.AddQuad(p, new Point3D(p.X, p.Y, bottom), new Point3D(q.X, q.Y, bottom), q);
                ground.AddEdge(p, q);
            }

        return new[] { ground }.Concat(laid.Values).Where(mesh => !mesh.IsEmpty).ToList();
    }

    /// <summary>The recorded existing ground's triangles.</summary>
    private static IReadOnlyList<(int A, int B, int C)> ExistingTriangles(Toposurface surface)
    {
        if (ExistingCache.TryGetValue(surface, out var worked) && worked.Version == surface.Version) return worked.Triangles;
        var triangles = Delaunay.Triangulate(surface.ExistingPoints.Select(point => new Point2D(point.X, point.Y)).ToList());
        ExistingCache.AddOrUpdate(surface, new Worked(surface.Version, triangles));
        return triangles;
    }

    /// <summary>The ground as it was before it was graded, at a point: the recorded existing ground, or, none recorded, its own spot levels.</summary>
    public static double? ExistingAt(Toposurface surface, Point2D at) =>
        surface.ExistingPoints.Count >= 3 ? Interpolate(surface.ExistingPoints, ExistingTriangles(surface), at) : HeightAt(surface, at);

    /// <summary>
    /// What is dug out and what is made up, mm³: between the ground as it was - recorded before
    /// it was graded, or its spot levels - and as it is to be: graded, stepped at its retaining
    /// walls, and dug down under the building to the underside of its ground floor. Measured over
    /// a grid of as many as 40,000 squares.
    /// </summary>
    public static (double Cut, double Fill) CutAndFill(BimDocument document, Toposurface surface)
    {
        if (surface.Points.Count < 3) return (0, 0);
        var model = Graded(document, surface);
        var pads = PadFloors(document, surface).Select(floor => (floor.Boundary, Formation: floor.GetBottomElevation(document))).ToList();

        // Worked out again only when the ground, its walls or the floors set into it change.
        var key = $"{model.Signature}|" + string.Join(";", pads.Select(pad => $"{pad.Formation:0}:{string.Join(",", pad.Boundary)}"));
        if (Volumes.TryGetValue(surface, out var known) && known.Key == key) return known.Volumes;

        var (minX, maxX) = (surface.Points.Min(point => point.X), surface.Points.Max(point => point.X));
        var (minY, maxY) = (surface.Points.Min(point => point.Y), surface.Points.Max(point => point.Y));
        var cell = Math.Max(100, Math.Sqrt((maxX - minX) * (maxY - minY) / 40000));
        var (across, up) = (0, 0);
        while (minX + (across + 0.5) * cell < maxX) across++;
        while (minY + (up + 0.5) * cell < maxY) up++;
        Point2D Centre(int i, int j) => new(minX + (i + 0.5) * cell, minY + (j + 0.5) * cell);

        // The ground as it was, and as it is to be, over each square: each triangle filling the squares it covers.
        var before = Raster(surface.ExistingPoints.Count >= 3 ? surface.ExistingPoints : surface.Points,
            surface.ExistingPoints.Count >= 3 ? ExistingTriangles(surface) : Triangles(surface), Centre, cell, minX, minY, across, up);
        var after = Raster(model.Points, model.Triangles, Centre, cell, minX, minY, across, up);

        // Under the walls stepping it, none; under the building, dug down to its ground floor's underside.
        void Over(IReadOnlyList<Point2D> outline, Action<int, int> act)
        {
            var (i0, i1) = (Math.Max(0, (int)Math.Floor((outline.Min(p => p.X) - minX) / cell)), Math.Min(across - 1, (int)Math.Ceiling((outline.Max(p => p.X) - minX) / cell)));
            var (j0, j1) = (Math.Max(0, (int)Math.Floor((outline.Min(p => p.Y) - minY) / cell)), Math.Min(up - 1, (int)Math.Ceiling((outline.Max(p => p.Y) - minY) / cell)));
            for (var i = i0; i <= i1; i++)
            for (var j = j0; j <= j1; j++)
                if (Polygon2D.Contains(outline, Centre(i, j))) act(i, j);
        }

        foreach (var wall in model.Walls) Over(wall, (i, j) => after[i, j] = null);
        foreach (var (boundary, formation) in pads) Over(boundary, (i, j) => after[i, j] = formation);

        double cut = 0, fill = 0;
        for (var i = 0; i < across; i++)
        for (var j = 0; j < up; j++)
        {
            if (before[i, j] is not { } was || after[i, j] is not { } to) continue;
            var change = to - was;
            if (change > 0) fill += change;
            else cut -= change;
        }

        var volumes = (cut * cell * cell, fill * cell * cell);
        Volumes.AddOrUpdate(surface, new Measured(key, volumes));
        return volumes;
    }

    private sealed record Measured(string Key, (double Cut, double Fill) Volumes);

    private static readonly ConditionalWeakTable<Toposurface, Measured> Volumes = new();

    /// <summary>A surface's height over the middle of each square of a grid, triangle by triangle: none where it does not reach.</summary>
    private static double?[,] Raster(IReadOnlyList<Point3D> points, IReadOnlyList<(int A, int B, int C)> triangles, Func<int, int, Point2D> centre,
        double cell, double minX, double minY, int across, int up)
    {
        var heights = new double?[across, up];
        foreach (var (a, b, c) in triangles)
        {
            var (pa, pb, pc) = (points[a], points[b], points[c]);
            var area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            if (Math.Abs(area) < 1e-9) continue;

            var (i0, i1) = (Math.Max(0, (int)Math.Floor((Math.Min(pa.X, Math.Min(pb.X, pc.X)) - minX) / cell - 0.5)), Math.Min(across - 1, (int)Math.Ceiling((Math.Max(pa.X, Math.Max(pb.X, pc.X)) - minX) / cell - 0.5)));
            var (j0, j1) = (Math.Max(0, (int)Math.Floor((Math.Min(pa.Y, Math.Min(pb.Y, pc.Y)) - minY) / cell - 0.5)), Math.Min(up - 1, (int)Math.Ceiling((Math.Max(pa.Y, Math.Max(pb.Y, pc.Y)) - minY) / cell - 0.5)));
            for (var i = i0; i <= i1; i++)
            for (var j = j0; j <= j1; j++)
            {
                if (heights[i, j] is not null) continue;
                var at = centre(i, j);
                var u = ((pb.X - at.X) * (pc.Y - at.Y) - (pb.Y - at.Y) * (pc.X - at.X)) / area;
                var v = ((pc.X - at.X) * (pa.Y - at.Y) - (pc.Y - at.Y) * (pa.X - at.X)) / area;
                var w = 1 - u - v;
                if (u < -1e-9 || v < -1e-9 || w < -1e-9) continue;
                heights[i, j] = u * pa.Z + v * pb.Z + w * pc.Z;
            }
        }

        return heights;
    }

    /// <summary>
    /// What about the ground against the building falls short. Approved Document C: the damp-proof
    /// course 150 above the ground beside the walls - the ground 150 below the floor at least; the
    /// IRC: 6 in (152) of foundation above the finished grade, and the ground falling 6 in (152)
    /// in the first 10 ft (3048) away from the walls. And a retaining wall holding back more than
    /// 600 - 30 in under the IRC - with nothing along its top: the drop off it is to be guarded.
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
                    if (InPad(pads, near) || HeightAt(document, surface, near) is not { } ground) continue;

                    // The floor inside, across the wall: the pad's top there, or the level.
                    var inside = wall.PointAt(type.Structure, along, -side * (half + 300));
                    var floor = document.Elements.OfType<Floor>().Where(candidate => candidate.LevelId == surface.LevelId && candidate.Contains(inside))
                        .Select(candidate => candidate.GetTopElevation(document)).DefaultIfEmpty(level).Max();
                    var kept = floor - ground;
                    if (worstKept is not { } known || kept < known) worstKept = kept;

                    if (us && HeightAt(document, surface, wall.PointAt(type.Structure, along, side * (half + 3048))) is { } away)
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

        // A retaining wall's top: a drop off it, guarded by a railing along it, or not.
        var limit = us ? 762 : 600;
        foreach (var (wall, type) in RetainingWalls(document, surface))
        {
            var held = wall.GetTopElevation(document) - wall.GetBaseElevation(document);
            if (held <= limit + 0.5) continue;
            var middle = wall.LocationCurve.PointAt(wall.Length / 2);
            var reach = type.Structure.TotalWidth / 2 + 600;
            if (document.Elements.OfType<Railing>().Any(railing => Railings.Lengths(railing).Any(length => Line2D.DistanceFromSegment(middle, length.From, length.To) < reach))) continue;
            problems.Add(us
                ? $"Its retaining wall ({type.Name}) holds back {held:0}, with nothing along its top: the IRC asks for a guard where it drops more than 30 in (762)."
                : $"Its retaining wall ({type.Name}) holds back {held:0}, with nothing along its top: Approved Document K asks for guarding where it drops more than 600.");
        }

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

/// <summary>
/// The ground as it is now recorded as the existing ground, before it is graded - or the record
/// let go - and undone.
/// </summary>
public sealed class SetExistingGroundCommand : Documents.Commands.IUndoableCommand
{
    private readonly Toposurface _surface;
    private readonly IReadOnlyList<Point3D> _before;
    private readonly IReadOnlyList<Point3D> _after;

    public SetExistingGroundCommand(Toposurface surface, bool record)
    {
        _surface = surface;
        _before = surface.ExistingPoints.ToList();
        _after = record ? surface.Points.ToList() : Array.Empty<Point3D>();
        Name = record ? "Record Existing Ground" : "Let Go of Existing Ground";
    }

    public string Name { get; }

    public void Redo() => _surface.SetExistingPoints(_after);

    public void Undo() => _surface.SetExistingPoints(_before);
}
