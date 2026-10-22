using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;
using PartFinish = BIMDesigner.Core.Views.ComponentModel.PartFinish;
using PartShape = BIMDesigner.Core.Views.ComponentModel.PartShape;

namespace BIMDesigner.Core.Architecture;

/// <summary>The simple solids a family of your own is drawn with.</summary>
public enum SolidShape
{
    Box,

    /// <summary>A cylinder standing up.</summary>
    Cylinder,

    /// <summary>A cylinder lying front to back: a wheel, a dial, a porthole.</summary>
    Disc,

    Sphere,

    /// <summary>A cone standing on its base, coming to a point at the top.</summary>
    Cone,

    /// <summary>A box sloping from its full height at the back down to nothing at the front: a ramp, a desk's slope.</summary>
    Wedge
}

/// <summary>
/// One solid a family of your own is drawn with: its shape, what it is finished in, and where it
/// is in the family's box - as fractions of the family's width, depth and height, across from
/// -0.5 to 0.5, back to front from -0.5 to 0.5, and up from 0 to 1 - so the family stretches
/// with its size, a table 1200 long made 1800 long with its legs still at its corners.
/// </summary>
public sealed class ComponentSolid
{
    public string Name { get; set; } = string.Empty;

    public SolidShape Shape { get; set; }

    public PartFinish Finish { get; set; }

    public double From { get; set; } = -0.25;
    public double To { get; set; } = 0.25;
    public double Back { get; set; } = -0.25;
    public double Front { get; set; } = 0.25;
    public double Bottom { get; set; }
    public double Top { get; set; } = 0.5;

    public ComponentSolid Copy() => (ComponentSolid)MemberwiseClone();

    /// <summary>Its box in the family's own frame, millimetres, for a family this size.</summary>
    public ComponentModel.Part Part(double width, double depth, double height) => new(
        From * width, To * width, Back * depth, Front * depth, Bottom * height, Top * height,
        Finish,
        Shape switch
        {
            SolidShape.Cylinder => PartShape.Upright,
            SolidShape.Disc => PartShape.Facing,
            SolidShape.Sphere => PartShape.Sphere,
            SolidShape.Cone => PartShape.Cone,
            SolidShape.Wedge => PartShape.Wedge,
            _ => PartShape.Box
        });
}

/// <summary>
/// A 3D model loaded into a family: its triangles in the family's box - as solids are, across
/// from -0.5 to 0.5 of its width, front to back from -0.5 to 0.5 of its depth with the front +,
/// up from 0 to 1 of its height - so it stretches with the family's size. With the edges a
/// drawing shows, the creases and open borders, and its outline seen from above.
/// </summary>
public sealed class ComponentMesh
{
    public ComponentMesh(IReadOnlyList<Point3D> positions, IReadOnlyList<int> triangles, IReadOnlyList<int> edges, string source)
    {
        Positions = positions;
        Triangles = triangles;
        Edges = edges;
        Source = source;
    }

    public IReadOnlyList<Point3D> Positions { get; }

    /// <summary>Three corners to a triangle, by position.</summary>
    public IReadOnlyList<int> Triangles { get; }

    /// <summary>Two ends to an edge, by position: the creases and the open borders.</summary>
    public IReadOnlyList<int> Edges { get; }

    /// <summary>The file it was loaded from.</summary>
    public string Source { get; }

    public int TriangleCount => Triangles.Count / 3;

    private IReadOnlyList<Point2D>? _hull;

    /// <summary>Its outline seen from above, in the family's box: across, and front to back.</summary>
    public IReadOnlyList<Point2D> PlanHull => _hull ??= ComponentMeshes.ConvexHull(Positions.Select(point => new Point2D(point.X, point.Y)).ToList());
}

/// <summary>The units a model file was drawn in.</summary>
public enum ModelUnits
{
    /// <summary>Guessed from its size: what a piece of furniture or a fitting would be.</summary>
    Auto,
    Millimetres,
    Centimetres,
    Metres,
    Inches,
    Feet
}

/// <summary>
/// A model file's triangles made into a family's 3D model: brought to millimetres and stood up
/// the right way, its shared corners welded, thinned where it has far more triangles than a
/// family wants, its creases found for the edges a drawing shows, and fitted into the family's box.
/// </summary>
public static class ComponentMeshes
{
    /// <summary>The most triangles a family's model keeps: enough for a chair or a car, few enough for a room of them.</summary>
    public const int Budget = 60_000;

    /// <summary>How sharp a fold is before it is drawn as an edge, degrees.</summary>
    public const double Crease = 30;

    /// <summary>Millimetres to one of a file's units; for Auto, what its size suggests.</summary>
    public static double ToMillimetres(ModelUnits units, double extent) => units switch
    {
        ModelUnits.Millimetres => 1,
        ModelUnits.Centimetres => 10,
        ModelUnits.Metres => 1000,
        ModelUnits.Inches => 25.4,
        ModelUnits.Feet => 304.8,

        // A thing ten units across or less is in metres, up to four hundred centimetres, more than that millimetres.
        _ => extent <= 10 ? 1000 : extent <= 400 ? 10 : 1
    };

    /// <summary>
    /// A family's model from a file's corners and triangles, and the size the family is to be:
    /// in the file's units, with Y up if <paramref name="yUp"/> (as OBJ files are) or Z up (as
    /// STL files usually are). Null where there is nothing to make one of.
    /// </summary>
    public static (ComponentMesh Mesh, double Width, double Depth, double Height)? From(
        IReadOnlyList<Point3D> corners, IReadOnlyList<int> triangles, ModelUnits units, bool yUp, string source)
    {
        if (corners.Count < 3 || triangles.Count < 3) return null;

        var extent = Math.Max(corners.Max(p => p.X) - corners.Min(p => p.X),
            Math.Max(corners.Max(p => p.Y) - corners.Min(p => p.Y), corners.Max(p => p.Z) - corners.Min(p => p.Z)));
        if (!(extent > 0)) return null;

        // In millimetres, Z up, its front toward -Y as a model is drawn facing you.
        var scale = ToMillimetres(units, extent);
        var points = corners.Select(p => yUp ? new Point3D(p.X * scale, -p.Z * scale, p.Y * scale) : new Point3D(p.X * scale, p.Y * scale, p.Z * scale)).ToList();

        var (welded, kept) = Weld(points, triangles, extent * scale * 1e-5);
        if (kept.Count / 3 > Budget) (welded, kept) = Thin(welded, kept);
        if (kept.Count < 3) return null;

        return Fitted(welded, kept, Creases(welded, kept), source);
    }

    /// <summary>A model fitted into a family's box from real corners: its size, and its corners as fractions of it.</summary>
    private static (ComponentMesh, double, double, double) Fitted(IReadOnlyList<Point3D> points, IReadOnlyList<int> triangles, IReadOnlyList<int> edges, string source)
    {
        var (minX, maxX) = (points.Min(p => p.X), points.Max(p => p.X));
        var (minY, maxY) = (points.Min(p => p.Y), points.Max(p => p.Y));
        var (minZ, maxZ) = (points.Min(p => p.Z), points.Max(p => p.Z));
        var width = Math.Max(1, maxX - minX);
        var depth = Math.Max(1, maxY - minY);
        var height = Math.Max(1, maxZ - minZ);
        var (middleX, middleY) = ((minX + maxX) / 2, (minY + maxY) / 2);

        // Its front, toward -Y, is the family's front.
        var fitted = points.Select(p => new Point3D((p.X - middleX) / width, -(p.Y - middleY) / depth, (p.Z - minZ) / height)).ToList();
        return (new ComponentMesh(fitted, triangles, edges, source), width, depth, height);
    }

    /// <summary>
    /// A family's model turned a quarter: stood up about its width - for one that came in lying
    /// on its back - or turned round about the vertical, for one facing the wrong way. With the
    /// family's size as it is now, and the size it is after.
    /// </summary>
    public static (ComponentMesh Mesh, double Width, double Depth, double Height) Turned(ComponentMesh mesh, double width, double depth, double height, bool standUp)
    {
        // Back to millimetres in a right-handed frame - across, back (as the file had it), up - turned there, and fitted again.
        var real = mesh.Positions.Select(p => new Point3D(p.X * width, -p.Y * depth, p.Z * height));
        var turned = real.Select(p => standUp ? new Point3D(p.X, -p.Z, p.Y) : new Point3D(-p.Y, p.X, p.Z)).ToList();
        var (fitted, w, d, h) = Fitted(turned, mesh.Triangles, mesh.Edges, mesh.Source);
        return (fitted, w, d, h);
    }

    /// <summary>Corners within a tolerance made one, so triangles that meet share them - as an STL file never does.</summary>
    private static (List<Point3D> Points, List<int> Triangles) Weld(IReadOnlyList<Point3D> points, IReadOnlyList<int> triangles, double tolerance)
    {
        tolerance = Math.Max(tolerance, 1e-6);
        var index = new Dictionary<(long, long, long), int>();
        var welded = new List<Point3D>();
        var map = new int[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var key = ((long)Math.Round(p.X / tolerance), (long)Math.Round(p.Y / tolerance), (long)Math.Round(p.Z / tolerance));
            if (!index.TryGetValue(key, out var at))
            {
                at = welded.Count;
                welded.Add(p);
                index[key] = at;
            }

            map[i] = at;
        }

        var kept = new List<int>(triangles.Count);
        for (var t = 0; t + 2 < triangles.Count; t += 3)
        {
            if (triangles[t] < 0 || triangles[t] >= map.Length || triangles[t + 1] < 0 || triangles[t + 1] >= map.Length || triangles[t + 2] < 0 || triangles[t + 2] >= map.Length) continue;
            var (a, b, c) = (map[triangles[t]], map[triangles[t + 1]], map[triangles[t + 2]]);
            if (a == b || b == c || a == c) continue;
            kept.Add(a);
            kept.Add(b);
            kept.Add(c);
        }

        return (welded, kept);
    }

    /// <summary>
    /// A model with too many triangles thinned to the budget: its corners gathered into the cells
    /// of a grid, each cell one corner where they were, coarser until few enough are left.
    /// </summary>
    private static (List<Point3D> Points, List<int> Triangles) Thin(List<Point3D> points, List<int> triangles)
    {
        var (minX, minY, minZ) = (points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
        var extent = Math.Max(points.Max(p => p.X) - minX, Math.Max(points.Max(p => p.Y) - minY, points.Max(p => p.Z) - minZ));

        var (best, bestTriangles) = (points, triangles);
        for (var cells = 400.0; cells >= 8; cells *= 0.8)
        {
            var size = extent / cells;
            var cellOf = new Dictionary<(long, long, long), int>();
            var sums = new List<(double X, double Y, double Z, int Count)>();
            var map = new int[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                var key = ((long)((p.X - minX) / size), (long)((p.Y - minY) / size), (long)((p.Z - minZ) / size));
                if (!cellOf.TryGetValue(key, out var cell))
                {
                    cell = sums.Count;
                    sums.Add((0, 0, 0, 0));
                    cellOf[key] = cell;
                }

                var sum = sums[cell];
                sums[cell] = (sum.X + p.X, sum.Y + p.Y, sum.Z + p.Z, sum.Count + 1);
                map[i] = cell;
            }

            var seen = new HashSet<(int, int, int)>();
            var kept = new List<int>();
            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                var (a, b, c) = (map[triangles[t]], map[triangles[t + 1]], map[triangles[t + 2]]);
                if (a == b || b == c || a == c) continue;

                var sorted = new[] { a, b, c };
                Array.Sort(sorted);
                if (!seen.Add((sorted[0], sorted[1], sorted[2]))) continue;
                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
            }

            best = sums.Select(sum => new Point3D(sum.X / sum.Count, sum.Y / sum.Count, sum.Z / sum.Count)).ToList();
            bestTriangles = kept;
            if (kept.Count / 3 <= Budget) break;
        }

        return (best, bestTriangles);
    }

    /// <summary>The edges a drawing of it shows: where its surface folds by more than a crease, and where it is open.</summary>
    private static IReadOnlyList<int> Creases(IReadOnlyList<Point3D> points, IReadOnlyList<int> triangles)
    {
        var normals = new (double X, double Y, double Z)[triangles.Count / 3];
        var sides = new Dictionary<(int, int), List<int>>();
        for (var t = 0; t < normals.Length; t++)
        {
            var (a, b, c) = (points[triangles[3 * t]], points[triangles[3 * t + 1]], points[triangles[3 * t + 2]]);
            var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var (nx, ny, nz) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
            var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            normals[t] = length > 0 ? (nx / length, ny / length, nz / length) : (0, 0, 0);

            for (var k = 0; k < 3; k++)
            {
                var (i, j) = (triangles[3 * t + k], triangles[3 * t + (k + 1) % 3]);
                var key = i < j ? (i, j) : (j, i);
                if (!sides.TryGetValue(key, out var list)) sides[key] = list = new List<int>(2);
                list.Add(t);
            }
        }

        var folded = Math.Cos(Crease * Math.PI / 180);
        var edges = new List<int>();
        foreach (var ((i, j), faces) in sides)
        {
            var drawn = faces.Count != 2 || Math.Abs(Dot(normals[faces[0]], normals[faces[1]])) < folded;
            if (!drawn) continue;
            edges.Add(i);
            edges.Add(j);
        }

        return edges;

        static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    /// <summary>The convex hull of points in plan, anticlockwise.</summary>
    public static IReadOnlyList<Point2D> ConvexHull(IReadOnlyList<Point2D> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;

        static double Cross(Point2D o, Point2D a, Point2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new List<Point2D>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var point in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }
}

/// <summary>Families of your own: what is edited, and drawing one from what a family already is.</summary>
public static class Families
{
    /// <summary>
    /// The solids a family is drawn with now, as fractions of its size: a family of your own's,
    /// or - for one the program draws, a chair or a wardrobe - the boxes and cylinders it is
    /// drawn with, to start from.
    /// </summary>
    public static List<ComponentSolid> SolidsOf(ComponentType type)
    {
        if (type.Form == ComponentForm.Custom) return type.Solids.Select(solid => solid.Copy()).ToList();

        var (w, d, h) = (Math.Max(1, type.Width), Math.Max(1, type.Depth), Math.Max(1, type.Height));
        return ComponentModel.Parts(type)
            .Select((part, i) => new ComponentSolid
            {
                Name = $"Part {i + 1}",
                Shape = part.Shape switch
                {
                    PartShape.Upright => SolidShape.Cylinder,
                    PartShape.Facing => SolidShape.Disc,
                    PartShape.Sphere => SolidShape.Sphere,
                    PartShape.Cone => SolidShape.Cone,
                    PartShape.Wedge => SolidShape.Wedge,
                    _ => SolidShape.Box
                },
                Finish = part.Finish,
                From = part.From / w, To = part.To / w,
                Back = part.Back / d, Front = part.Front / d,
                Bottom = part.Bottom / h, Top = part.Top / h
            })
            .ToList();
    }

    /// <summary>
    /// The family's size made the size of what it is drawn with - its solids and its model - so
    /// its box is snug round them, nothing moving: each re-expressed as fractions of the new size.
    /// </summary>
    public static void FitSize(FamilyDefinition family)
    {
        var parts = family.Solids.Select(solid => solid.Part(family.Width, family.Depth, family.Height)).ToList();
        var boxes = parts.Select(part => (part.From, part.To, part.Back, part.Front, part.Bottom, part.Top)).ToList();
        if (family.Mesh is { } mesh)
            boxes.Add((mesh.Positions.Min(p => p.X) * family.Width, mesh.Positions.Max(p => p.X) * family.Width,
                mesh.Positions.Min(p => p.Y) * family.Depth, mesh.Positions.Max(p => p.Y) * family.Depth,
                mesh.Positions.Min(p => p.Z) * family.Height, mesh.Positions.Max(p => p.Z) * family.Height));
        if (boxes.Count == 0) return;

        var (from, to) = (boxes.Min(b => b.From), boxes.Max(b => b.To));
        var (back, front) = (boxes.Min(b => b.Back), boxes.Max(b => b.Front));
        var (bottom, top) = (Math.Min(0, boxes.Min(b => b.Bottom)), boxes.Max(b => b.Top));
        var width = Math.Max(1, to - from);
        var depth = Math.Max(1, front - back);
        var height = Math.Max(1, top - bottom);
        var (shiftAcross, shiftForward) = ((from + to) / 2, (back + front) / 2);

        foreach (var (solid, part) in family.Solids.Zip(parts))
        {
            solid.From = (part.From - shiftAcross) / width;
            solid.To = (part.To - shiftAcross) / width;
            solid.Back = (part.Back - shiftForward) / depth;
            solid.Front = (part.Front - shiftForward) / depth;
            solid.Bottom = (part.Bottom - bottom) / height;
            solid.Top = (part.Top - bottom) / height;
        }

        if (family.Mesh is { } model)
            family.Mesh = new ComponentMesh(
                model.Positions.Select(p => new Point3D((p.X * family.Width - shiftAcross) / width, (p.Y * family.Depth - shiftForward) / depth, (p.Z * family.Height - bottom) / height)).ToList(),
                model.Triangles, model.Edges, model.Source);

        (family.Width, family.Depth, family.Height) = (width, depth, height);
    }
}

/// <summary>
/// Everything about a family the family editor changes: what it is called and in, its size, how
/// it is put in, its finish, and what it is drawn with. Taken from a type, edited, and given back.
/// </summary>
public sealed class FamilyDefinition
{
    public string Name { get; set; } = string.Empty;
    public BuiltInCategory Kind { get; set; } = BuiltInCategory.Furniture;
    public ComponentForm Form { get; set; } = ComponentForm.Custom;
    public double Width { get; set; } = 600;
    public double Depth { get; set; } = 600;
    public double Height { get; set; } = 600;
    public ComponentPlacement Placement { get; set; }
    public double DefaultElevation { get; set; }
    public bool AgainstWall { get; set; }
    public ColourRgb Colour { get; set; } = new(0xB4, 0x9A, 0x74);
    public Guid MaterialId { get; set; }
    public List<ComponentSolid> Solids { get; set; } = new();
    public ComponentMesh? Mesh { get; set; }

    /// <summary>What a type is now - its solids copied, so editing them leaves it alone.</summary>
    public static FamilyDefinition Of(ComponentType type) => new()
    {
        Name = type.Name, Kind = type.Kind, Form = type.Form,
        Width = type.Width, Depth = type.Depth, Height = type.Height,
        Placement = type.Placement, DefaultElevation = type.DefaultElevation, AgainstWall = type.AgainstWall,
        Colour = type.Colour, MaterialId = type.MaterialId,
        Solids = type.Solids.Select(solid => solid.Copy()).ToList(),
        Mesh = type.Mesh
    };

    /// <summary>A copy to edit, its solids its own.</summary>
    public FamilyDefinition Copy()
    {
        var copy = (FamilyDefinition)MemberwiseClone();
        copy.Solids = Solids.Select(solid => solid.Copy()).ToList();
        return copy;
    }

    /// <summary>Makes a type this, and the pieces placed from it its category.</summary>
    public void ApplyTo(ComponentType type, BimDocument document)
    {
        type.Name = Name;
        type.Kind = Kind;
        type.Form = Form;
        (type.Width, type.Depth, type.Height) = (Width, Depth, Height);
        type.Placement = Placement;
        type.DefaultElevation = DefaultElevation;
        type.AgainstWall = AgainstWall;
        type.Colour = Colour;
        type.MaterialId = MaterialId;
        type.Solids.Clear();
        type.Solids.AddRange(Solids.Select(solid => solid.Copy()));
        type.Mesh = Mesh;

        foreach (var component in document.All<Component>().Where(component => component.TypeId == type.Id))
            component.TypeKind = Kind;
    }

    /// <summary>A new type made as this is.</summary>
    public ComponentType NewType(BimDocument document)
    {
        var type = new ComponentType(Name, Kind, Form, Width, Depth, Height);
        ApplyTo(type, document);
        return type;
    }
}

/// <summary>A family changed in the family editor, as one step.</summary>
public sealed class EditFamilyCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly ComponentType _type;
    private readonly FamilyDefinition _before;
    private readonly FamilyDefinition _after;

    public EditFamilyCommand(BimDocument document, ComponentType type, FamilyDefinition after)
    {
        _document = document;
        _type = type;
        _before = FamilyDefinition.Of(type);
        _after = after.Copy();
    }

    public string Name => $"Edit Family {_after.Name}";

    public void Redo() => _after.ApplyTo(_type, _document);

    public void Undo() => _before.ApplyTo(_type, _document);
}
