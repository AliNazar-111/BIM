using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>One of a wall's two long faces.</summary>
public enum WallFace
{
    Exterior,
    Interior
}

/// <summary>
/// A region of one face of a wall - the whole face, or a part split off it with Split Face -
/// and the material painted on it, if any. Its bounds are along the wall from its start and
/// up from its base; a bound left empty runs to the edge of the face, so a band painted from
/// end to end follows the face as the wall is lengthened or its corners change.
/// </summary>
public sealed record WallFaceRegion(WallFace Face, double? From, double? To, double? Bottom, double? Top, Guid? MaterialId)
{
    /// <summary>Whether it is the face entire, rather than a part of it.</summary>
    public bool IsWhole => From is null && To is null && Bottom is null && Top is null;

    public bool Contains(double along, double height) =>
        along >= (From ?? double.NegativeInfinity) - 1e-6 && along <= (To ?? double.PositiveInfinity) + 1e-6 &&
        height >= (Bottom ?? double.NegativeInfinity) - 1e-6 && height <= (Top ?? double.PositiveInfinity) + 1e-6;

    /// <summary>Its outline on the face, as along and up; an open bound reaches well past the face.</summary>
    public IReadOnlyList<Point2D> Rectangle()
    {
        const double Far = 1e7;
        var (left, right, bottom, top) = (From ?? -Far, To ?? Far, Bottom ?? -Far, Top ?? Far);
        return new[] { new Point2D(left, bottom), new Point2D(right, bottom), new Point2D(right, top), new Point2D(left, top) };
    }
}

/// <summary>
/// Paint and Split Face on walls (Revit's Paint and Split Face): a material put on a face, or on
/// a part of it split off - a tiled band, a feature colour - without adding a lining wall and
/// without changing the wall's type. The paint has no thickness; the wall is unchanged under it.
/// Later regions lie over earlier ones.
/// </summary>
public static class WallPaint
{
    /// <summary>How far proud of the face the paint is shown, so it never flickers against it.</summary>
    public const double ShownProud = 0.8;

    /// <summary>The finest along a curved face a painted piece is built in, mm.</summary>
    private const double CurveStep = 300;

    /// <summary>The topmost region of a face at a point on it, or -1.</summary>
    public static int RegionAt(Wall wall, WallFace face, double along, double height)
    {
        for (var i = wall.FaceRegions.Count - 1; i >= 0; i--)
            if (wall.FaceRegions[i].Face == face && wall.FaceRegions[i].Contains(along, height)) return i;

        return -1;
    }

    /// <summary>What is painted on a face at a point, if anything.</summary>
    public static Guid? PaintAt(Wall wall, WallFace face, double along, double height) =>
        RegionAt(wall, face, along, height) is var index and >= 0 ? wall.FaceRegions[index].MaterialId : null;

    /// <summary>
    /// Where a point on a wall in 3D is on its faces: which face, how far along and how high up -
    /// or null for a point on its top, its end or inside it.
    /// </summary>
    public static (WallFace Face, double Along, double Height)? Locate(BimDocument document, Wall wall, Point3D point)
    {
        if (document.GetWallType(wall) is not { } type || type.Width <= 0) return null;

        var (along, across) = wall.Locate(type.Structure, new Point2D(point.X, point.Y));
        if (Math.Abs(Math.Abs(across) - type.Width / 2) > Math.Max(5, type.Width * 0.05)) return null;

        return (across > 0 ? WallFace.Exterior : WallFace.Interior, along, point.Z - wall.GetBaseElevation(document));
    }

    /// <summary>
    /// Paints a face where it is clicked: the region there, or the whole face where it has none.
    /// A null material takes the paint off - and with it a whole-face region, which is nothing
    /// without its paint; a part split off stays split. Null when there is nothing to change.
    /// </summary>
    public static IUndoableCommand? Paint(Wall wall, WallFace face, double along, double height, Guid? materialId)
    {
        var regions = wall.FaceRegions.ToList();
        var index = RegionAt(wall, face, along, height);

        if (index < 0)
        {
            if (materialId is null) return null;
            regions.Insert(0, new WallFaceRegion(face, null, null, null, null, materialId));
        }
        else
        {
            var region = regions[index];
            if (region.MaterialId == materialId) return null;

            if (materialId is null && region.IsWhole) regions.RemoveAt(index);
            else regions[index] = region with { MaterialId = materialId };
        }

        return new SetWallFaceRegionsCommand(wall, regions, materialId is null ? "Remove Paint" : "Paint");
    }

    /// <summary>
    /// Splits a part off a face: the rectangle between two corners, along and up. A side that
    /// reaches the face's edge - within a hand's breadth of it - is left open, so a band drawn
    /// end to end runs the face's whole length however it is joined. Null when it is too small
    /// to be a part.
    /// </summary>
    public static IUndoableCommand? Split(BimDocument document, Wall wall, WallFace face, Point2D corner, Point2D other, double snap = 50)
    {
        var (from, to) = (Math.Min(corner.X, other.X), Math.Max(corner.X, other.X));
        var (bottom, top) = (Math.Min(corner.Y, other.Y), Math.Max(corner.Y, other.Y));
        if (to - from < 10 || top - bottom < 10) return null;

        var (faceFrom, faceTo) = document.GetWallType(wall) is { } type ? FaceSpan(document, wall, type, face) : (0, wall.Length);
        var height = Height(document, wall);

        var region = new WallFaceRegion(face,
            from <= faceFrom + snap ? null : from,
            to >= faceTo - snap ? null : to,
            bottom <= snap ? null : bottom,
            top >= height - snap ? null : top,
            null);
        if (region.IsWhole) return null;

        return new SetWallFaceRegionsCommand(wall, wall.FaceRegions.Append(region).ToList(), "Split Face");
    }

    /// <summary>How high a wall's faces reach above its base, at their highest.</summary>
    public static double Height(BimDocument document, Wall wall) =>
        WallProfile.Of(document, wall) is { } profile ? WallProfile.Top(profile) : wall.GetHeight(document);

    /// <summary>
    /// How far along the wall a face runs, from end to end: past the wall's own ends where a
    /// corner carries it on, short of them where it stops against another wall.
    /// </summary>
    public static (double From, double To) FaceSpan(BimDocument document, Wall wall, WallType type, WallFace face)
    {
        var across = face == WallFace.Exterior ? type.Width / 2 : -type.Width / 2;
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var start = WallJoins.EndPoints(wall, type, across, across, startCut.Unwrapped(), atStart: true)[0];
        var end = WallJoins.EndPoints(wall, type, across, across, endCut.Unwrapped(), atStart: false)[0];
        return (wall.Locate(type.Structure, start).Along, wall.Locate(type.Structure, end).Along);
    }

    /// <summary>
    /// The face as drawn on itself, along and up: its span, up to its height or the outline an
    /// edited profile gives it, less its doors, windows and openings.
    /// </summary>
    public static IReadOnlyList<PolygonBoolean.Region> FaceShape(BimDocument document, Wall wall, WallType type, WallFace face)
    {
        var (from, to) = FaceSpan(document, wall, type, face);
        IReadOnlyList<Point2D> outline = WallProfile.Of(document, wall) is { } profile
            ? profile.Select(p => p.X <= 1e-6 ? new Point2D(from, p.Y) : p.X >= wall.Length - 1e-6 ? new Point2D(to, p.Y) : p).ToList()
            : new[] { new Point2D(from, 0), new Point2D(to, 0), new Point2D(to, Height(document, wall)), new Point2D(from, Height(document, wall)) };

        var holes = Openings(document, wall).Select(hole => (IReadOnlyList<Point2D>)new[]
        {
            new Point2D(hole.From, hole.Sill <= 1e-6 ? -1 : hole.Sill), new Point2D(hole.To, hole.Sill <= 1e-6 ? -1 : hole.Sill),
            new Point2D(hole.To, hole.Head), new Point2D(hole.From, hole.Head)
        }).ToList();

        IReadOnlyList<PolygonBoolean.Region> shape = new[] { new PolygonBoolean.Region(outline, Array.Empty<IReadOnlyList<Point2D>>()) };
        foreach (var hole in holes) shape = Subtract(shape, new[] { hole });
        return shape;
    }

    /// <summary>The doors, windows and openings through a wall, along it and up from its base.</summary>
    internal static IEnumerable<(double From, double To, double Sill, double Head)> Openings(BimDocument document, Wall wall)
    {
        var profile = WallProfile.Of(document, wall);
        foreach (var opening in WallOpenings.Of(document, wall))
        {
            if (document.FindType<OpeningType>(opening.TypeId) is not { } type) continue;

            var (from, to) = opening.GetSpan(type);
            var (sill, height) = opening.Placed(document, type, wall, profile);
            if (to - from > 1e-6 && height > 1e-6) yield return (from, to, sill, sill + height);
        }

        foreach (var hole in WallHoles.Of(document, wall))
            if (hole.To - hole.From > 1e-6 && hole.Head - hole.Sill > 1e-6) yield return (hole.From, hole.To, hole.Sill, hole.Head);
    }

    /// <summary>
    /// What shows of each region of a wall's faces: its rectangle on the face, less the openings,
    /// and less every region over it - each with its paint, or none for a part split off and
    /// left as the wall is.
    /// </summary>
    public static IReadOnlyList<(WallFaceRegion Region, IReadOnlyList<PolygonBoolean.Region> Shape)> Visible(BimDocument document, Wall wall, WallType type)
    {
        var visible = new List<(WallFaceRegion, IReadOnlyList<PolygonBoolean.Region>)>();
        if (wall.FaceRegions.Count == 0) return visible;

        foreach (var face in new[] { WallFace.Exterior, WallFace.Interior })
        {
            var regions = wall.FaceRegions.Where(region => region.Face == face).ToList();
            if (regions.Count == 0) continue;

            var shape = FaceShape(document, wall, type, face);
            for (var i = 0; i < regions.Count; i++)
            {
                var part = Intersect(shape, regions[i].Rectangle());
                foreach (var over in regions.Skip(i + 1)) part = Subtract(part, new[] { over.Rectangle() });
                if (part.Count > 0) visible.Add((regions[i], part));
            }
        }

        return visible;
    }

    /// <summary>The area painted with each material, over a wall's two faces, mm².</summary>
    public static IReadOnlyList<(Guid MaterialId, double Area)> PaintedAreas(BimDocument document, Wall wall)
    {
        if (wall.FaceRegions.Count == 0 || document.GetWallType(wall) is not { } type) return Array.Empty<(Guid, double)>();

        return Visible(document, wall, type)
            .Where(entry => entry.Region.MaterialId is not null)
            .GroupBy(entry => entry.Region.MaterialId!.Value)
            .Select(group => (group.Key, group.Sum(entry => entry.Shape.Sum(region => region.Area))))
            .ToList();
    }

    /// <summary>
    /// The paint on a wall's faces as it is shown in 3D: each region's visible shape set just proud
    /// of the face, in its material - or, for a part split off and not painted, the face's own -
    /// with its edges drawn, so the split shows.
    /// </summary>
    public static IEnumerable<Mesh3D> Meshes(BimDocument document, Wall wall, WallType type)
    {
        var structure = type.Structure;
        var bottom = wall.GetBaseElevation(document);

        foreach (var (region, shape) in Visible(document, wall, type))
        {
            var face = region.Face;
            var materialId = region.MaterialId ?? (face == WallFace.Exterior ? structure.Layers.FirstOrDefault()?.MaterialId : structure.Layers.LastOrDefault()?.MaterialId);
            var material = materialId is { } id ? document.FindMaterial(id) : null;
            var mesh = new Mesh3D(wall.Id, wall.LevelId, MeshKind.Wall, material?.SurfaceColour ?? new Materials.ColourRgb(0xC8, 0xC8, 0xC8),
                region.MaterialId is null ? "Split Face" : material?.Name ?? "Paint");

            var across = (face == WallFace.Exterior ? 1 : -1) * (type.Width / 2 + ShownProud);
            var outward = (face == WallFace.Exterior ? 1 : -1);

            Point3D On(Point2D p)
            {
                var plan = wall.PointAt(structure, p.X, across);
                if (wall.CrossSection != WallCrossSection.Vertical) plan = WallLean.Move(wall, type, plan, p.Y);
                return new Point3D(plan.X, plan.Y, bottom + p.Y);
            }

            foreach (var part in wall.IsCurved ? Strips(shape) : shape)
            {
                var outline = part.Holes.Count == 0 ? part.Outer : Polygon2D.BridgeHoles(part.Outer, part.Holes);
                var triangles = Polygon2D.Triangulate(outline);
                var middle = wall.PointAt(structure, part.Outer.Average(p => p.X), 0);
                var normal = wall.PointAt(structure, part.Outer.Average(p => p.X), outward * 1000) - middle;
                mesh.AddFace(outline.Select(On).ToList(), triangles, new Point3D(normal.X, normal.Y, 0));
            }

            // Its edges, round the outside and round every opening in it.
            foreach (var part in shape)
            foreach (var loop in part.Holes.Prepend(part.Outer))
                for (var i = 0; i < loop.Count; i++)
                    mesh.AddEdge(On(loop[i]), On(loop[(i + 1) % loop.Count]));

            if (!mesh.IsEmpty) yield return mesh;
        }
    }

    /// <summary>A shape cut into upright strips, so a curved face is followed rather than cut across.</summary>
    private static IEnumerable<PolygonBoolean.Region> Strips(IReadOnlyList<PolygonBoolean.Region> shape)
    {
        if (shape.Count == 0) yield break;

        var (left, right) = (shape.Min(r => r.Outer.Min(p => p.X)), shape.Max(r => r.Outer.Max(p => p.X)));
        var (low, high) = (shape.Min(r => r.Outer.Min(p => p.Y)) - 1, shape.Max(r => r.Outer.Max(p => p.Y)) + 1);
        var count = Math.Max(1, (int)Math.Ceiling((right - left) / CurveStep));
        for (var i = 0; i < count; i++)
        {
            var (a, b) = (left + (right - left) * i / count, left + (right - left) * (i + 1) / count);
            var strip = new[] { new Point2D(a, low), new Point2D(b, low), new Point2D(b, high), new Point2D(a, high) };
            foreach (var piece in Intersect(shape, strip)) yield return piece;
        }
    }

    private static IReadOnlyList<PolygonBoolean.Region> Intersect(IReadOnlyList<PolygonBoolean.Region> shape, IReadOnlyList<Point2D> by) =>
        shape.SelectMany(region => PolygonBoolean.Combine(Loops(region), new[] { by }, BooleanOperation.Intersection))
            .Where(region => region.Area > 1).ToList();

    private static IReadOnlyList<PolygonBoolean.Region> Subtract(IReadOnlyList<PolygonBoolean.Region> shape, IReadOnlyList<IReadOnlyList<Point2D>> by) =>
        shape.SelectMany(region => PolygonBoolean.Combine(Loops(region), by, BooleanOperation.Difference))
            .Where(region => region.Area > 1).ToList();

    private static IReadOnlyList<IReadOnlyList<Point2D>> Loops(PolygonBoolean.Region region) => region.Holes.Prepend(region.Outer).ToList();
}

/// <summary>Changes the regions on a wall's faces - painting, taking paint off, splitting a part off - as one step.</summary>
public sealed class SetWallFaceRegionsCommand : IUndoableCommand
{
    private readonly Wall _wall;
    private readonly IReadOnlyList<WallFaceRegion> _old;
    private readonly IReadOnlyList<WallFaceRegion> _new;

    public SetWallFaceRegionsCommand(Wall wall, IReadOnlyList<WallFaceRegion> regions, string name)
    {
        _wall = wall;
        _old = wall.FaceRegions;
        _new = regions;
        Name = name;
    }

    public string Name { get; }

    public void Redo() => _wall.FaceRegions = _new;

    public void Undo() => _wall.FaceRegions = _old;
}
