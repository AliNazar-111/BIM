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
        // Its lowest or highest point set: the whole of it moved up or down so far, its shape kept - the ground as it was recorded left as it was.
        yield return ParameterValue.BindValidated(TerrainParameters.Lowest, () => Points.Count == 0 ? 0 : Points.Min(point => point.Z), (double v) => MovedTo(v, Points.Min(point => point.Z)));
        yield return ParameterValue.BindValidated(TerrainParameters.Highest, () => Points.Count == 0 ? 0 : Points.Max(point => point.Z), (double v) => MovedTo(v, Points.Max(point => point.Z)));
        yield return ParameterValue.ReadOnly(TerrainParameters.Existing, () => ExistingPoints.Count >= 3 ? $"Recorded, {ExistingPoints.Count} points" : "As it is");
        yield return ParameterValue.ReadOnly(TerrainParameters.Cut, () => Terrains.CutAndFill(document, this).Cut);
        yield return ParameterValue.ReadOnly(TerrainParameters.Fill, () => Terrains.CutAndFill(document, this).Fill);
        yield return ParameterValue.ReadOnly(TerrainParameters.Net, () => Terrains.CutAndFill(document, this) is var (cut, fill) ? cut - fill : 0);
        yield return ParameterValue.ReadOnly(TerrainParameters.Rule, () => Terrains.Verdict(document, this));
        yield return ParameterValue.BindFlagCommand(TerrainParameters.Simplify, () => false,
            (bool simplify, out string? message) =>
            {
                message = null;
                if (!simplify) return null;
                var kept = Terrains.Simplified(Points, Terrains.SimplifyWithin);
                if (kept.Count >= Points.Count)
                {
                    message = $"Nothing to take out: each of its {Points.Count} spot levels shapes it by more than {Units.Bare(Terrains.SimplifyWithin)}.";
                    return null;
                }

                message = $"From {Points.Count} spot levels to {kept.Count}: the ground keeps its shape within {Units.Bare(Terrains.SimplifyWithin)}.";
                return new SetToposurfacePointsCommand(this, kept, "Simplify Surface");
            });

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    /// <summary>Every point moved up or down by as much as takes one height to another: false where there is nothing to move, or the height is no number.</summary>
    private bool MovedTo(double height, double from)
    {
        if (Points.Count == 0 || !double.IsFinite(height) || Math.Abs(height) > 1_000_000) return false;
        var by = height - from;
        SetPoints(Points.Select(point => point with { Z = point.Z + by }).ToList());
        return true;
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
    public static readonly ParameterDefinition Simplify = new("Simplify Surface", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Dimensions);
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
    /// <summary>
    /// The ground as it is worked: its points and triangles, the walls stepping it, the points the
    /// grading put in or moved, and how far apart the existing ground was sampled - so it is known
    /// where the working reached.
    /// </summary>
    private sealed record Model(string Signature, IReadOnlyList<Point3D> Points, IReadOnlyList<(int A, int B, int C)> Triangles, IReadOnlyList<IReadOnlyList<Point2D>> Walls,
        IReadOnlyList<Point2D> Reshaped, double Spacing);

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

    /// <summary>
    /// Its height over a point in plan, stepped where retaining walls stand in it and graded to
    /// the pads in it - raised or sunk where a region laid on it is; none beyond it, nor in a wall.
    /// </summary>
    public static double? HeightAt(BimDocument document, Toposurface surface, Point2D at) =>
        HeightIn(Graded(document, surface), at) is { } height ? height + RegionOffset(document, surface, at) : null;

    /// <summary>How far a region laid on it is raised over it, or sunk into it, at a point: a planting bed, a sunken patio.</summary>
    private static double RegionOffset(BimDocument document, Toposurface surface, Point2D at)
    {
        foreach (var region in document.All<GroundRegion>())
            if (region.Offset != 0 && region.LevelId == surface.LevelId && region.Boundary.Count >= 3 && Polygon2D.Contains(region.Boundary, at))
                return region.Offset;
        return 0;
    }

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
        var pads = SitePads.On(document, surface.LevelId);
        var signature = $"{surface.Version}|" + string.Join(";", walls.Select(pair =>
            $"{pair.Wall.Id}:{pair.Wall.Start}:{pair.Wall.End}:{pair.Wall.Bulge}:{pair.Wall.Ellipse}:{pair.Wall.Spline?.GetHashCode()}:{pair.Wall.LocationLine}:" +
            $"{pair.Wall.Flipped}:{pair.Wall.AcrossOffset}:{pair.Wall.GetBaseElevation(document):0}:{pair.Wall.GetTopElevation(document):0}:{pair.Type.Structure.TotalWidth:0}"))
            + "|" + string.Join(";", pads.Select(pad => $"{pad.Id}:{string.Join(",", pad.Boundary)}:{pad.HeightOffset}:{pad.Fall}:{pad.FallDirection}:{pad.Batter}"))
            + $"|{document.FindLevel(surface.LevelId)?.Elevation}";
        if (Models.TryGetValue(surface, out var known) && known.Signature == signature) return known;

        var points = surface.Points.ToList();
        var footprints = new List<IReadOnlyList<Point2D>>();

        // The ground as it was, sampled on a grid first, where anything is to work it: the points
        // a pad or a wall puts in then reshape the ground about them only - not every triangle a
        // few spot levels span, out to the far side of the site.
        var spacing = SampleSpacing(surface);
        if (walls.Count > 0 || pads.Count > 0) points.AddRange(Samples(surface, spacing));
        var unworked = points.ToHashSet();
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

        // Its pads, graded in: level - or falling - at their own height inside, the ground battered back to them round about.
        foreach (var pad in pads) Grade(document, surface, pad, points);

        var plan = points.Select(point => new Point2D(point.X, point.Y)).ToList();
        var triangles = Delaunay.Triangulate(plan)
            .Where(t => !footprints.Any(wall => Polygon2D.Contains(wall, new Point2D((plan[t.A].X + plan[t.B].X + plan[t.C].X) / 3, (plan[t.A].Y + plan[t.B].Y + plan[t.C].Y) / 3))))
            .ToList();
        var reshaped = points.Where(point => !unworked.Contains(point)).Select(point => new Point2D(point.X, point.Y)).ToList();
        var model = new Model(signature, points, triangles, footprints, reshaped, spacing);
        Models.AddOrUpdate(surface, model);
        return model;
    }

    /// <summary>How far apart the existing ground is sampled for working it: a metre, or more on a large site - 4,000 samples at most.</summary>
    private static double SampleSpacing(Toposurface surface)
    {
        if (surface.Points.Count < 3) return 1000;
        var (width, depth) = (surface.Points.Max(p => p.X) - surface.Points.Min(p => p.X), surface.Points.Max(p => p.Y) - surface.Points.Min(p => p.Y));
        return Math.Max(1000, Math.Sqrt(width * depth / 4000));
    }

    /// <summary>The ground as surveyed, read off at a grid's points across it - those not by a spot level already.</summary>
    private static IEnumerable<Point3D> Samples(Toposurface surface, double spacing)
    {
        if (surface.Points.Count < 3) yield break;
        var (minX, maxX) = (surface.Points.Min(p => p.X), surface.Points.Max(p => p.X));
        var (minY, maxY) = (surface.Points.Min(p => p.Y), surface.Points.Max(p => p.Y));
        var near = spacing / 4;
        for (var x = minX + spacing / 2; x < maxX; x += spacing)
        for (var y = minY + spacing / 2; y < maxY; y += spacing)
        {
            if (surface.Points.Any(point => Math.Abs(point.X - x) < near && Math.Abs(point.Y - y) < near)) continue;
            if (HeightAt(surface, new Point2D(x, y)) is { } height) yield return new Point3D(x, y, height);
        }
    }

    /// <summary>
    /// A pad graded into the ground: the spot levels it covers taken out, its edge laid in every
    /// 500 at its own height, and round it a batter - so many along to one up - from its edge out
    /// to where it meets the ground, the spot levels inside that brought onto it: the ground cut
    /// down to it where it is higher, made up to it where it is lower. A pad with no batter is
    /// sheer at its edge, as a retaining wall holds it.
    /// </summary>
    private static void Grade(BimDocument document, Toposurface surface, SitePad pad, List<Point3D> points)
    {
        var outline = pad.Boundary;
        double Pad(Point2D at) => SitePads.HeightAt(document, pad, at);
        (Point2D Edge, double Distance) Nearest(Point2D at)
        {
            var best = (Edge: outline[0], Distance: double.PositiveInfinity);
            for (var i = 0; i < outline.Count; i++)
            {
                var on = Line2D.ClosestPointOnSegment(at, outline[i], outline[(i + 1) % outline.Count]);
                if (on.DistanceTo(at) < best.Distance) best = (on, on.DistanceTo(at));
            }

            return best;
        }

        for (var i = points.Count - 1; i >= 0; i--)
        {
            var at = new Point2D(points[i].X, points[i].Y);
            if (Polygon2D.Contains(outline, at))
            {
                points.RemoveAt(i);
                continue;
            }

            if (pad.Batter <= 0) continue;
            var (edge, distance) = Nearest(at);
            var height = Pad(edge);
            var rise = points[i].Z - height;
            if (Math.Abs(rise) * pad.Batter > distance) points[i] = points[i] with { Z = height + Math.Sign(rise) * distance / pad.Batter };
        }

        for (var e = 0; e < outline.Count; e++)
        {
            var (a, b) = (outline[e], outline[(e + 1) % outline.Count]);
            var length = a.DistanceTo(b);
            if (length < 1) continue;

            // Anticlockwise round it: outside is on the right going along each side.
            var outward = ((b - a) / length).PerpendicularLeft() * -1;
            var before = outline[(e - 1 + outline.Count) % outline.Count];
            var outwardBefore = (a - before).NormalisedOrDefault(b - a).PerpendicularLeft() * -1;
            var count = Math.Max(1, (int)Math.Ceiling(length / 500));
            for (var k = 0; k < count; k++)
            {
                var at = a + (b - a) * ((double)k / count);
                var height = Pad(at);
                points.Add(new Point3D(at.X, at.Y, height));
                var ways = k == 0 ? new[] { outwardBefore, (outwardBefore + outward).NormalisedOrDefault(outward), outward } : new[] { outward };

                // Sheer: the ground just outside left as it is, a step at its edge.
                if (pad.Batter <= 0)
                {
                    foreach (var way in ways)
                        if (HeightAt(surface, at + way * 10) is { } outside) points.Add(new Point3D(at.X + way.X * 10, at.Y + way.Y * 10, outside));
                    continue;
                }

                foreach (var way in ways)
                    if (Toe(surface, at, way, height, pad.Batter) is { } toe) points.Add(toe);
            }
        }
    }

    /// <summary>Where a batter from a pad's edge, out one way, meets the ground: within 30 m, and on it.</summary>
    private static Point3D? Toe(Toposurface surface, Point2D from, Vector2D way, double height, double batter)
    {
        double Gap(double distance) => HeightAt(surface, from + way * distance) is { } ground ? Math.Abs(ground - height) - distance / batter : double.NaN;
        var (inside, outside) = (0.0, double.NaN);
        for (var distance = 250.0; distance <= 30000; distance += 250)
        {
            var gap = Gap(distance);
            if (double.IsNaN(gap)) return null;
            if (gap <= 0)
            {
                outside = distance;
                break;
            }

            inside = distance;
        }

        if (double.IsNaN(outside)) return null;
        for (var pass = 0; pass < 8; pass++)
        {
            var middle = (inside + outside) / 2;
            if (Gap(middle) > 0) inside = middle;
            else outside = middle;
        }

        var at = from + way * outside;
        return HeightAt(surface, at) is { } met ? new Point3D(at.X, at.Y, met) : null;
    }

    /// <summary>Its faces as it is worked: each triangle's corners, in the model.</summary>
    public static IEnumerable<(Point3D A, Point3D B, Point3D C)> Faces(BimDocument document, Toposurface surface)
    {
        var model = Graded(document, surface);
        return model.Triangles.Select(t => (model.Points[t.A], model.Points[t.B], model.Points[t.C]));
    }

    /// <summary>The ground over a point in plan near a height - the highest, where surfaces lie one over another; none where there is no ground there near it.</summary>
    public static double? GroundNear(BimDocument document, double height, Point2D at, double reach = Reach) =>
        document.All<Toposurface>()
            .Select(surface => HeightAt(document, surface, at))
            .Where(ground => ground is { } value && Math.Abs(value - height) <= reach)
            .Max();

    /// <summary>The ground over a point in plan, wherever it is; none where there is none.</summary>
    public static double? GroundAt(BimDocument document, Point2D at) =>
        document.All<Toposurface>().Select(surface => HeightAt(document, surface, at)).Where(ground => ground is not null).Max();

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
        document.All<Floor>().Where(floor => floor.LevelId == surface.LevelId && floor.Boundary.Count >= 3).ToList();

    private static bool InPad(IReadOnlyList<IReadOnlyList<Point2D>> pads, Point2D at) => pads.Any(pad => Polygon2D.Contains(pad, at));

    /// <summary>The regions laid on it: lawns, paths, paving, gravel, planting.</summary>
    public static IReadOnlyList<GroundRegion> Regions(BimDocument document, Toposurface surface) =>
        document.All<GroundRegion>().Where(region => region.Boundary.Count >= 3 && ReferenceEquals(GroundRegions.Host(document, region), surface)).ToList();

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

        // Its points, and the pads', the building's and the regions' edges every 500 laid onto it.
        var pads = Pads(document, surface);
        var regions = Regions(document, surface);
        var graded = SitePads.On(document, surface.LevelId);
        var all = model.Points.ToList();
        foreach (var outline in pads.Concat(regions.Select(region => region.Boundary)).Concat(graded.Select(pad => pad.Boundary)))
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

        // Each pad in its own surface.
        var paved = graded.ToDictionary(pad => pad, pad =>
        {
            var made = document.FindMaterial(pad.MaterialId);
            return new Mesh3D(pad.Id, pad.LevelId, MeshKind.Terrain, made?.SurfaceColour ?? GroundRegions.Colour(pad.Surface), made?.Name ?? EnumText.Humanise(pad.Surface));
        });

        var plan = all.Select(point => new Point2D(point.X, point.Y)).ToList();
        foreach (var (a, b, c) in Delaunay.Triangulate(plan))
        {
            var middle = new Point2D((plan[a].X + plan[b].X + plan[c].X) / 3, (plan[a].Y + plan[b].Y + plan[c].Y) / 3);
            if (InPad(pads, middle) || HeightAt(document, surface, middle) is null) continue;
            if (graded.FirstOrDefault(pad => Polygon2D.Contains(pad.Boundary, middle)) is { } onPad)
            {
                paved[onPad].AddTriangle(all[a], all[b], all[c]);
                continue;
            }

            // A region raised or sunk: its surface so far over the ground.
            var region = regions.FirstOrDefault(candidate => Polygon2D.Contains(candidate.Boundary, middle));
            if (region is null) ground.AddTriangle(all[a], all[b], all[c]);
            else laid[region].AddTriangle(all[a] with { Z = all[a].Z + region.Offset }, all[b] with { Z = all[b].Z + region.Offset }, all[c] with { Z = all[c].Z + region.Offset });
        }

        // A raised region's sides down to the ground round it; a sunk one's down into it.
        foreach (var region in regions.Where(region => region.Offset != 0))
        {
            var outline = region.Boundary;
            for (var i = 0; i < outline.Count; i++)
            {
                var (from, to) = (outline[i], outline[(i + 1) % outline.Count]);
                var count = Math.Max(1, (int)Math.Ceiling((to - from).Length / 500));
                for (var k = 0; k < count; k++)
                {
                    var (p, q) = (from + (to - from) * ((double)k / count), from + (to - from) * ((double)(k + 1) / count));
                    if (HeightAt(document, surface, p) is not { } hp || HeightAt(document, surface, q) is not { } hq) continue;
                    var (pLow, qLow, pHigh, qHigh) = (new Point3D(p.X, p.Y, hp), new Point3D(q.X, q.Y, hq), new Point3D(p.X, p.Y, hp + region.Offset), new Point3D(q.X, q.Y, hq + region.Offset));
                    if (region.Offset > 0) laid[region].AddQuad(pLow, qLow, qHigh, pHigh);
                    else laid[region].AddQuad(qHigh, pHigh, pLow, qLow);
                }
            }
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

        return new[] { ground }.Concat(laid.Values).Concat(paved.Values).Where(mesh => !mesh.IsEmpty).ToList();
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

        // A basement's floor under the storey the ground is on: dug down to its underside.
        var storey = document.FindLevel(surface.LevelId)?.Elevation ?? 0;
        var basements = document.All<Floor>()
            .Where(floor => floor.Boundary.Count >= 3 && (document.FindLevel(floor.LevelId)?.Elevation ?? 0) < storey - 1)
            .Select(floor => (floor.Boundary, Formation: floor.GetBottomElevation(document))).ToList();

        // Worked out again only when the ground, its walls or the floors set into it change.
        var key = $"{model.Signature}|" + string.Join(";", pads.Concat(basements).Select(pad => $"{pad.Formation:0}:{string.Join(",", pad.Boundary)}"));
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

        // Only where the ground is worked: under its pads, about the points the grading put in or
        // moved, and under the building - elsewhere it is as it was, however its triangles happen
        // to have been drawn. Against the ground as recorded before, everywhere it has changed.
        var worked = new bool[across, up];
        if (surface.ExistingPoints.Count >= 3)
            for (var i = 0; i < across; i++)
            for (var j = 0; j < up; j++)
                worked[i, j] = true;
        foreach (var pad in SitePads.On(document, surface.LevelId).Where(pad => pad.Boundary.Count >= 3))
            Over(pad.Boundary, (i, j) => worked[i, j] = true);
        var reach = (int)Math.Ceiling(2 * model.Spacing / cell);
        foreach (var point in model.Reshaped)
        {
            var (ci, cj) = ((int)Math.Floor((point.X - minX) / cell), (int)Math.Floor((point.Y - minY) / cell));
            for (var i = Math.Max(0, ci - reach); i <= Math.Min(across - 1, ci + reach); i++)
            for (var j = Math.Max(0, cj - reach); j <= Math.Min(up - 1, cj + reach); j++)
                worked[i, j] = true;
        }

        foreach (var wall in model.Walls) Over(wall, (i, j) => (after[i, j], worked[i, j]) = (null, true));
        foreach (var (boundary, formation) in pads) Over(boundary, (i, j) => (after[i, j], worked[i, j]) = (formation, true));
        foreach (var (boundary, formation) in basements) Over(boundary, (i, j) => (after[i, j], worked[i, j]) = (after[i, j] is { } above ? Math.Min(above, formation) : formation, true));

        double cut = 0, fill = 0;
        for (var i = 0; i < across; i++)
        for (var j = 0; j < up; j++)
        {
            if (!worked[i, j] || before[i, j] is not { } was || after[i, j] is not { } to) continue;
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
    /// in the first 10 ft (3048) away from the walls - a hard surface there 2% (R401.3, and the
    /// IBC's 1804.4). Timber framing in a wall kept clear of it: 8 in (203) under the IRC (R317.1)
    /// unless treated, over the damp-proof course here. The building kept back from a slope steeper
    /// than 1 in 3: from the top of one falling away by a third of its height, up to 40 ft (12192),
    /// and from the foot of one rising by half, up to 15 ft (4572) (R403.1.7). And a retaining wall
    /// holding back more than 600 - 30 in under the IRC - with nothing along its top: the drop off
    /// it is to be guarded.
    /// </summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Toposurface surface)
    {
        var problems = new List<string>();
        if (surface.Points.Count < 3) return problems;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var pads = Pads(document, surface);
        var level = document.FindLevel(surface.LevelId)?.Elevation ?? 0;
        double? worstKept = null;
        (double Short, double Falls, bool Hard)? worstFall = null;
        (double Gap, double Asked, bool Descending, double Height)? worstSetback = null;
        double? worstTimber = null;

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
                    var floor = document.All<Floor>().Where(candidate => candidate.LevelId == surface.LevelId && candidate.Contains(inside))
                        .Select(candidate => candidate.GetTopElevation(document)).DefaultIfEmpty(level).Max();
                    var kept = floor - ground;
                    if (worstKept is not { } known || kept < known) worstKept = kept;

                    if (us && HeightAt(document, surface, wall.PointAt(type.Structure, along, side * (half + 3048))) is { } away)
                    {
                        // A hard surface against the walls - paving, a path, a paved pad - need only fall 2%.
                        var falls = ground - away;
                        var hard = Hard(document, surface, near);
                        var asked = hard ? 3048 / 50.0 : 152;
                        if (asked - falls > (worstFall?.Short ?? 0.5)) worstFall = (asked - falls, falls, hard);
                    }

                    // Timber framing in the wall, its foot near the ground outside.
                    if (document.All<WallFraming>().Any(framing => framing.HostId == wall.Id && framing.Material == FramingMaterial.Timber))
                    {
                        var clear = wall.GetBaseElevation(document) - ground;
                        if (worstTimber is not { } closest || clear < closest) worstTimber = clear;
                    }

                    // A slope steeper than 1 in 3 near it, falling away or rising: the building kept back from it.
                    if (us && Setback(document, surface, wall.PointAt(type.Structure, along, side * (half + 1)), (wall.PointAt(type.Structure, along, side * (half + 100)) - wall.PointAt(type.Structure, along, side * half)).NormalisedOrDefault(Vector2D.UnitX)) is { } setback
                        && setback.Gap < setback.Asked - 0.5 && (worstSetback is not { } worst || setback.Asked - setback.Gap > worst.Asked - worst.Gap))
                        worstSetback = setback;
                }
            }
        }

        if (worstKept is { } least && least < 150 - 0.5)
            problems.Add(us
                ? $"The ground comes up to {Units.Bare(least)} below the floor beside its walls: the IRC asks for 6 in{Units.MetricAside("152")} of foundation above the finished grade."
                : $"The ground comes up to {least:0} below the floor beside its walls: Approved Document C asks for the damp-proof course 150 above the ground at least.");
        if (worstFall is { } fall)
            problems.Add(fall.Hard
                ? Math.Round(fall.Falls) < 0
                    ? $"A hard surface rises {Units.Bare(-fall.Falls)} toward its walls over the first 10 ft{Units.MetricAside("3048")}: the IRC and the IBC ask for it to fall 2% away from them."
                    : $"A hard surface falls only {Units.Bare(Math.Round(fall.Falls) + 0.0)} in the first 10 ft{Units.MetricAside("3048")} away from its walls: the IRC and the IBC ask for 2%{Units.MetricAside("61")}."
                : Math.Round(fall.Falls) < 0
                    ? $"The ground rises {Units.Bare(-fall.Falls)} toward its walls over the first 10 ft{Units.MetricAside("3048")}: the IRC asks for it to fall 6 in{Units.MetricAside("152")} away from them, or 5%, so water runs off."
                    : $"The ground falls only {Units.Bare(Math.Round(fall.Falls) + 0.0)} in the first 10 ft{Units.MetricAside("3048")} away from its walls: the IRC asks for 6 in{Units.MetricAside("152")}, or 5%, so water runs off.");
        if (worstTimber is { } timber && timber < (us ? 203 : 150) - 0.5)
            problems.Add(us
                ? $"Timber framing in its walls is {Units.Bare(Math.Max(0, timber))} over the ground outside: the IRC asks for 8 in{Units.MetricAside("203")}, or treated or naturally durable wood (R317.1)."
                : $"Timber framing in its walls is {Math.Max(0, timber):0} over the ground outside: Approved Document C asks for it on the damp-proof course, 150 above the ground.");
        if (worstSetback is { } slope)
            problems.Add(slope.Descending
                ? $"The ground falls away {Units.Metres(slope.Height, "0.0")} steeper than 1 in 3 just {Units.Metres(slope.Gap, "0.0")} from its walls: the IRC asks for the building to be set back {Units.Metres(slope.Asked, "0.0")} from the top of such a slope - a third of its height, 40 ft at most (R403.1.7) - or an engineered foundation."
                : $"The ground rises {Units.Metres(slope.Height, "0.0")} steeper than 1 in 3 just {Units.Metres(slope.Gap, "0.0")} from its walls: the IRC asks for {Units.Metres(slope.Asked, "0.0")} clear of the foot of such a slope - half its height, 15 ft at most (R403.1.7).");

        // A retaining wall's top: a drop off it, guarded by a railing along it, or not.
        var limit = us ? 762 : 600;
        foreach (var (wall, type) in RetainingWalls(document, surface))
        {
            var held = wall.GetTopElevation(document) - wall.GetBaseElevation(document);
            if (held <= limit + 0.5) continue;
            var middle = wall.LocationCurve.PointAt(wall.Length / 2);
            var reach = type.Structure.TotalWidth / 2 + 600;
            if (document.All<Railing>().Any(railing => Railings.Lengths(railing).Any(length => Line2D.DistanceFromSegment(middle, length.From, length.To) < reach))) continue;
            problems.Add(us
                ? $"Its retaining wall ({type.Name}) holds back {Units.Bare(held)}, with nothing along its top: the IRC asks for a guard where it drops more than 30 in{Units.MetricAside("762")}."
                : $"Its retaining wall ({type.Name}) holds back {held:0}, with nothing along its top: Approved Document K asks for guarding where it drops more than 600.");
        }

        return problems;
    }

    public static string Verdict(BimDocument document, Toposurface surface)
    {
        var problems = Problems(document, surface);
        return problems.Count == 0 ? "Clear of the walls, and falling away" : string.Join(" ", problems);
    }

    /// <summary>Whether the ground at a point is laid hard: paving or a path, a region or a pad.</summary>
    private static bool Hard(BimDocument document, Toposurface surface, Point2D at) =>
        document.All<GroundRegion>().Any(region => region.LevelId == surface.LevelId && region.Surface is GroundSurface.Paving or GroundSurface.Path
                                                               && region.Boundary.Count >= 3 && Polygon2D.Contains(region.Boundary, at))
        || SitePads.On(document, surface.LevelId).Any(pad => pad.Surface is GroundSurface.Paving or GroundSurface.Path && Polygon2D.Contains(pad.Boundary, at));

    /// <summary>
    /// A slope steeper than 1 in 3 out from a wall, within 40 ft (12192): how far off it starts,
    /// how high it is, which way it goes, and how far the IRC asks the building to be kept from it -
    /// a third of its height from the top of one falling away, up to 40 ft; half its height from
    /// the foot of one rising, up to 15 ft. None where there is none that high, or no ground.
    /// </summary>
    private static (double Gap, double Asked, bool Descending, double Height)? Setback(BimDocument document, Toposurface surface, Point2D from, Vector2D away)
    {
        const double step = 300;
        double? Ground(double distance) => HeightAt(document, surface, from + away * distance);
        for (var start = 0.0; start + step <= 12192; start += step)
        {
            if (Ground(start) is not { } here || Ground(start + step) is not { } next || Math.Abs(next - here) <= step / 3) continue;

            // The steep stretch from here, as far as it goes on steep the same way.
            var descending = next < here;
            var end = start + step;
            while (end + step <= 15000 && Ground(end) is { } a && Ground(end + step) is { } b && (descending ? a - b : b - a) > step / 3) end += step;
            var height = Math.Abs((Ground(end) ?? here) - here);
            if (height < 600) continue;
            var asked = descending ? Math.Min(height / 3, 12192) : Math.Min(height / 2, 4572);
            return (start, asked, descending, height);
        }

        return null;
    }

    /// <summary>How near the ground a spot level may be to the surface the others make without it, and be taken out to simplify it, mm.</summary>
    public const double SimplifyWithin = 25;

    /// <summary>
    /// Spot levels simplified - as many taken out as can be while the ground keeps its shape: pass
    /// by pass, each that its neighbours round it put within so far of its height without it, none
    /// next to another taken out in the same pass, those round its edge kept.
    /// </summary>
    public static IReadOnlyList<Point3D> Simplified(IReadOnlyList<Point3D> points, double within)
    {
        var kept = points.ToList();
        for (var pass = 0; pass < 30 && kept.Count > 3; pass++)
        {
            var plan = kept.Select(point => new Point2D(point.X, point.Y)).ToList();
            var triangles = Delaunay.Triangulate(plan);
            var around = Enumerable.Range(0, kept.Count).Select(_ => new HashSet<int>()).ToList();
            var edges = new Dictionary<(int, int), int>();
            foreach (var (a, b, c) in triangles)
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                {
                    around[p].Add(q);
                    around[q].Add(p);
                    var key = p < q ? (p, q) : (q, p);
                    edges[key] = edges.TryGetValue(key, out var seen) ? seen + 1 : 1;
                }

            var edge = new HashSet<int>(edges.Where(pair => pair.Value == 1).SelectMany(pair => new[] { pair.Key.Item1, pair.Key.Item2 }));

            // How far each is off the height its neighbours give it - nearer ones counting more.
            var off = new double[kept.Count];
            for (var i = 0; i < kept.Count; i++)
            {
                if (edge.Contains(i) || around[i].Count < 3)
                {
                    off[i] = double.PositiveInfinity;
                    continue;
                }

                var (sum, weights) = (0.0, 0.0);
                foreach (var j in around[i])
                {
                    var weight = 1 / Math.Max(1, plan[i].DistanceTo(plan[j]));
                    sum += kept[j].Z * weight;
                    weights += weight;
                }

                off[i] = Math.Abs(kept[i].Z - sum / weights);
            }

            var gone = new HashSet<int>();
            foreach (var i in Enumerable.Range(0, kept.Count).Where(i => off[i] <= within).OrderBy(i => off[i]))
                if (!around[i].Any(gone.Contains)) gone.Add(i);
            if (gone.Count == 0 || kept.Count - gone.Count < 3) break;
            kept = kept.Where((_, i) => !gone.Contains(i)).ToList();
        }

        return kept;
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

/// <summary>How the ground is coloured in plan: plainly, or by how steep it is, how high, or how much it is cut or filled.</summary>
public enum GroundColouring
{
    None,
    Slope,
    Height,
    CutAndFill
}

/// <summary>
/// The ground coloured to read it as a landscape architect does: by slope - level, gently sloping,
/// steep as a ramp, too steep to mow, steeper than earth stands - by height in bands between its
/// lowest and highest, or by what is dug out of it and made up on it against the ground as it was.
/// </summary>
public static class GroundColours
{
    /// <summary>One band of a colouring: what it means, and its colour.</summary>
    public sealed record Band(string Label, ColourRgb Colour);

    /// <summary>The slopes that matter: 1:60 is level for a landing, 1:20 gently sloping, 1:12 the steepest ramp, 1:3 the steepest lawn mown safely, 1:1 the steepest earth stands.</summary>
    private static readonly (double Steepest, Band Band)[] Slopes =
    {
        (1 / 60.0, new Band("Level - 1:60 or flatter", new ColourRgb(0xB8, 0xD8, 0xA8))),
        (1 / 20.0, new Band("Gently sloping - to 1:20", new ColourRgb(0x7F, 0xC0, 0x6A))),
        (1 / 12.0, new Band("As steep as a ramp - to 1:12", new ColourRgb(0xE8, 0xD8, 0x5A))),
        (1 / 3.0, new Band("Steep - to 1:3, mown with care", new ColourRgb(0xF0, 0xA0, 0x48))),
        (1.0, new Band("Very steep - to 1:1", new ColourRgb(0xE0, 0x5A, 0x40))),
        (double.PositiveInfinity, new Band("Steeper than earth stands", new ColourRgb(0x9A, 0x48, 0xA8)))
    };

    /// <summary>What is dug out (red) and made up (blue), mm deep: in metres and millimetres, or in a US project feet and inches.</summary>
    private static (double Below, Band Band)[] Changes => Units.Imperial ? UsChanges : MetricChanges;

    private static readonly (double Below, Band Band)[] UsChanges =
    {
        (-914.4, new Band("Cut over 3 ft", new ColourRgb(0xA0, 0x28, 0x28))),
        (-304.8, new Band("Cut 1 to 3 ft", new ColourRgb(0xD8, 0x58, 0x50))),
        (-50.8, new Band("Cut 2 in to 1 ft", new ColourRgb(0xF0, 0xA8, 0x98))),
        (50.8, new Band("Within 2 in of as it was", new ColourRgb(0xD8, 0xD8, 0xD0))),
        (304.8, new Band("Filled 2 in to 1 ft", new ColourRgb(0xA8, 0xC8, 0xF0))),
        (914.4, new Band("Filled 1 to 3 ft", new ColourRgb(0x58, 0x88, 0xD8))),
        (double.PositiveInfinity, new Band("Filled over 3 ft", new ColourRgb(0x28, 0x48, 0xA0)))
    };

    private static readonly (double Below, Band Band)[] MetricChanges =
    {
        (-1000, new Band("Cut over 1 m", new ColourRgb(0xA0, 0x28, 0x28))),
        (-300, new Band("Cut 300 to 1000", new ColourRgb(0xD8, 0x58, 0x50))),
        (-50, new Band("Cut 50 to 300", new ColourRgb(0xF0, 0xA8, 0x98))),
        (50, new Band("Within 50 of as it was", new ColourRgb(0xD8, 0xD8, 0xD0))),
        (300, new Band("Filled 50 to 300", new ColourRgb(0xA8, 0xC8, 0xF0))),
        (1000, new Band("Filled 300 to 1000", new ColourRgb(0x58, 0x88, 0xD8))),
        (double.PositiveInfinity, new Band("Filled over 1 m", new ColourRgb(0x28, 0x48, 0xA0)))
    };

    private static readonly ColourRgb[] Heights =
    {
        new(0x3A, 0x6E, 0xA5), new(0x5A, 0x9C, 0xB0), new(0x8C, 0xC0, 0x8A), new(0xD8, 0xD0, 0x7A), new(0xC8, 0x94, 0x5A), new(0x8E, 0x5E, 0x3C)
    };

    /// <summary>The bands a colouring of a surface has, in order: its legend.</summary>
    public static IReadOnlyList<Band> Bands(BimDocument document, Toposurface surface, GroundColouring colouring)
    {
        switch (colouring)
        {
            case GroundColouring.Slope: return Slopes.Select(slope => slope.Band).ToList();
            case GroundColouring.CutAndFill: return Changes.Select(change => change.Band).ToList();
            case GroundColouring.Height:
            {
                var (low, high) = HeightRange(document, surface);
                var step = (high - low) / Heights.Length;
                return Heights.Select((colour, i) => new Band($"{Units.FormatLength(low + step * i)} to {Units.FormatLength(low + step * (i + 1))}", colour)).ToList();
            }
            default: return Array.Empty<Band>();
        }
    }

    private static (double Low, double High) HeightRange(BimDocument document, Toposurface surface)
    {
        var heights = Terrains.Faces(document, surface).SelectMany(face => new[] { face.A.Z, face.B.Z, face.C.Z }).ToList();
        if (heights.Count == 0) return (0, 1);
        var (low, high) = (heights.Min(), heights.Max());
        return high - low < 1 ? (low - 0.5, high + 0.5) : (low, high);
    }

    /// <summary>
    /// The ground's triangles as it is worked, each with the band it falls in - by its slope, the
    /// height of its middle, or how far its middle is cut or filled - those under the building left out.
    /// </summary>
    public static IReadOnlyList<(IReadOnlyList<Point2D> Triangle, int Band)> Shade(BimDocument document, Toposurface surface, GroundColouring colouring)
    {
        var shaded = new List<(IReadOnlyList<Point2D>, int)>();
        if (colouring == GroundColouring.None) return shaded;
        var pads = Terrains.Pads(document, surface);
        var (low, high) = HeightRange(document, surface);
        foreach (var (a, b, c) in Terrains.Faces(document, surface))
        {
            var triangle = new[] { new Point2D(a.X, a.Y), new Point2D(b.X, b.Y), new Point2D(c.X, c.Y) };
            var middle = new Point2D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3);
            if (pads.Any(pad => Polygon2D.Contains(pad, middle))) continue;
            var height = (a.Z + b.Z + c.Z) / 3;
            int band;
            switch (colouring)
            {
                case GroundColouring.Slope:
                {
                    var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                    var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                    var (nx, ny, nz) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
                    if (Math.Abs(nz) < 1e-9) continue;
                    var slope = Math.Sqrt(nx * nx + ny * ny) / Math.Abs(nz);
                    band = Array.FindIndex(Slopes, entry => slope <= entry.Steepest + 1e-9);
                    break;
                }
                case GroundColouring.Height:
                    band = Math.Clamp((int)Math.Floor((height - low) / (high - low) * Heights.Length), 0, Heights.Length - 1);
                    break;
                default:
                {
                    if (Terrains.ExistingAt(surface, middle) is not { } was) continue;
                    var change = height - was;
                    band = Array.FindIndex(Changes, entry => change < entry.Below);
                    break;
                }
            }

            shaded.Add((triangle, band));
        }

        return shaded;
    }
}
