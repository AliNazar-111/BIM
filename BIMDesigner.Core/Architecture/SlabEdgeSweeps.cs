using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>The section a slab edge runs along a floor's edge as.</summary>
public enum SlabEdgeShape
{
    /// <summary>An edge beam: square, as deep as it says, flush with the floor's edge and under it.</summary>
    EdgeBeam,

    /// <summary>A thickened edge: flush with the edge, its inner face battered back from wide at the slab to narrower at its foot.</summary>
    Thickened,

    /// <summary>An upstand: a kerb standing up out of the floor along its edge.</summary>
    Upstand
}

/// <summary>A slab edge's type: its section, its size and what it is made of - Revit's Slab Edge types.</summary>
public sealed class SlabEdgeType : ElementType
{
    public SlabEdgeType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.SlabEdges;

    public SlabEdgeShape Shape { get; set; } = SlabEdgeShape.Thickened;

    /// <summary>How far in under the floor from its edge it reaches - at the slab, for a thickened edge; or how wide an upstand is.</summary>
    public double Width { get; set; } = 450;

    /// <summary>How deep it goes down from the floor's top - or how high an upstand stands above it.</summary>
    public double Depth { get; set; } = 450;

    /// <summary>A thickened edge's width at its foot.</summary>
    public double FootWidth { get; set; } = 250;

    public Guid MaterialId { get; set; }

    public SlabEdgeType Duplicate(string name) => new(name)
    {
        Shape = Shape, Width = Width, Depth = Depth, FootWidth = FootWidth, MaterialId = MaterialId,
        Description = Description, Cost = Cost, TypeMark = TypeMark
    };

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.BindChoice(SlabEdgeParameters.Shape,
            () => EnumText.Humanise(Shape),
            v => { if (EnumText.TryParse<SlabEdgeShape>(v, out var shape)) Shape = shape; },
            EnumText.Choices<SlabEdgeShape>());
        yield return ParameterValue.BindValidated(SlabEdgeParameters.Width, () => Width, (double v) =>
        {
            if (!double.IsFinite(v) || v < 20 || v > 5000) return false;
            Width = v;
            return true;
        });
        yield return ParameterValue.BindValidated(SlabEdgeParameters.Depth, () => Depth, (double v) =>
        {
            if (!double.IsFinite(v) || v < 20 || v > 5000) return false;
            Depth = v;
            return true;
        });
        yield return ParameterValue.BindValidated(SlabEdgeParameters.FootWidth, () => FootWidth, (double v) =>
        {
            if (!double.IsFinite(v) || v < 20 || v > 5000) return false;
            FootWidth = v;
            return true;
        });
        if (document is not null)
        {
            var names = document.Materials.Select(material => material.Name).OrderBy(name => name).ToArray();
            yield return ParameterValue.BindChoice(SlabEdgeParameters.Material,
                () => document.FindMaterial(MaterialId)?.Name ?? string.Empty,
                v => MaterialId = document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? MaterialId,
                names);
        }
    }
}

/// <summary>
/// A slab edge: a section run along edges of a floor - a thickened edge or an edge beam under
/// it, a kerb standing up along it - mitred round the corners between them: Revit's Floor: Slab
/// Edge. It is the floor's: it goes with it when it is moved, turned, mirrored, copied or deleted.
/// Each edge is kept by where it runs; an edge the floor's outline no longer has is let go.
/// </summary>
public sealed class SlabEdge : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.SlabEdges;

    /// <summary>The floor whose edges it runs along.</summary>
    public Guid FloorId { get; set; }

    public Guid HostId => FloorId;

    /// <summary>The floor's edges it runs along, each from one corner to the next.</summary>
    public List<(Point2D From, Point2D To)> Edges { get; } = new();

    /// <summary>How far out past the floor's edge it is moved.</summary>
    public double HorizontalOffset { get; set; }

    /// <summary>How far up it is moved from the floor's top; down is negative.</summary>
    public double VerticalOffset { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(SlabEdgeParameters.Floor,
            () => SlabEdgeRuns.FloorOf(document, this) is { } floor ? document.FindType<SlabType>(floor.TypeId)?.Name ?? "Floor" : "<none>");
        yield return ParameterValue.ReadOnly(SlabEdgeParameters.EdgeCount, () => SlabEdgeRuns.Runs(document, this).Sum(run => run.Points.Count - (run.Closed ? 0 : 1)));
        yield return ParameterValue.Bind(SlabEdgeParameters.HorizontalOffset, () => HorizontalOffset, v => { if (double.IsFinite(v)) HorizontalOffset = v; });
        yield return ParameterValue.Bind(SlabEdgeParameters.VerticalOffset, () => VerticalOffset, v => { if (double.IsFinite(v)) VerticalOffset = v; });
        yield return ParameterValue.ReadOnly(SlabEdgeParameters.Length, () => SlabEdgeRuns.Length(document, this));
        yield return ParameterValue.ReadOnly(SlabEdgeParameters.Volume, () => SlabEdgeRuns.Volume(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class SlabEdgeParameters
{
    public static readonly ParameterDefinition Shape = new("Shape", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Depth = new("Depth or Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FootWidth = new("Width at Foot (Thickened)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Floor = new("Floor", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition EdgeCount = new("Edges", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition HorizontalOffset = new("Horizontal Profile Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition VerticalOffset = new("Vertical Profile Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Volume = new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
}

/// <summary>The floor edges a slab edge runs along, changed as one step to undo - as the Slab Edge tool picks them.</summary>
public sealed class SetSlabEdgeEdgesCommand : Documents.Commands.IUndoableCommand
{
    private readonly SlabEdge _edge;
    private readonly List<(Point2D From, Point2D To)> _before, _after;

    public SetSlabEdgeEdgesCommand(SlabEdge edge, IEnumerable<(Point2D From, Point2D To)> edges)
    {
        _edge = edge;
        _before = edge.Edges.ToList();
        _after = edges.ToList();
    }

    public string Name => "Slab Edge";

    public void Redo() => Set(_after);

    public void Undo() => Set(_before);

    private void Set(List<(Point2D From, Point2D To)> edges)
    {
        _edge.Edges.Clear();
        _edge.Edges.AddRange(edges);
    }
}

/// <summary>Where slab edges run along their floors, the section they run as, and the solid they make.</summary>
public static class SlabEdgeRuns
{
    /// <summary>How near a kept edge's ends must be to the floor's corners to still be that edge, mm.</summary>
    public const double Tolerance = 1;

    public static Floor? FloorOf(BimDocument document, SlabEdge edge) =>
        document.Elements.OfType<Floor>().FirstOrDefault(floor => floor.Id == edge.FloorId);

    public static SlabEdgeType? TypeOf(BimDocument document, SlabEdge edge) => document.FindType<SlabEdgeType>(edge.TypeId);

    /// <summary>The index of the floor's edge running from one point to another, either way round, or -1.</summary>
    public static int EdgeIndex(Slab floor, Point2D from, Point2D to)
    {
        var boundary = floor.Boundary;
        for (var i = 0; i < boundary.Count; i++)
        {
            var (a, b) = (boundary[i], boundary[(i + 1) % boundary.Count]);
            if (a.DistanceTo(from) <= Tolerance && b.DistanceTo(to) <= Tolerance || a.DistanceTo(to) <= Tolerance && b.DistanceTo(from) <= Tolerance)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The runs a slab edge makes: the floor's edges it is on, in order round the floor, joined
    /// where they follow on from each other - closed if it goes all the way round.
    /// </summary>
    public static IReadOnlyList<(IReadOnlyList<Point2D> Points, bool Closed)> Runs(BimDocument document, SlabEdge edge)
    {
        if (FloorOf(document, edge) is not { Boundary.Count: >= 3 } floor) return Array.Empty<(IReadOnlyList<Point2D>, bool)>();
        var boundary = floor.Boundary;
        var count = boundary.Count;
        var on = new bool[count];
        foreach (var (from, to) in edge.Edges)
            if (EdgeIndex(floor, from, to) is var index and >= 0) on[index] = true;
        if (!on.Any(picked => picked)) return Array.Empty<(IReadOnlyList<Point2D>, bool)>();
        if (on.All(picked => picked)) return new[] { ((IReadOnlyList<Point2D>)boundary.ToList(), true) };

        var runs = new List<(IReadOnlyList<Point2D>, bool)>();
        for (var start = 0; start < count; start++)
        {
            if (!on[start] || on[(start - 1 + count) % count]) continue;
            var points = new List<Point2D> { boundary[start] };
            for (var i = start; on[i % count] && points.Count <= count; i++) points.Add(boundary[(i + 1) % count]);
            runs.Add((points, false));
        }

        return runs;
    }

    /// <summary>
    /// The section, as points (in, up): in from the floor's edge under the floor, and up from its
    /// top - so a thickened edge hangs below, an upstand stands above - moved by the offsets.
    /// </summary>
    public static IReadOnlyList<Point2D> Profile(SlabEdgeType type, SlabEdge edge)
    {
        var (w, d) = (Math.Max(20, type.Width), Math.Max(20, type.Depth));
        IReadOnlyList<Point2D> section = type.Shape switch
        {
            SlabEdgeShape.Upstand => new[] { new Point2D(0, 0), new Point2D(w, 0), new Point2D(w, d), new Point2D(0, d) },
            SlabEdgeShape.Thickened => new[] { new Point2D(0, 0), new Point2D(w, 0), new Point2D(Math.Clamp(type.FootWidth, 20, w), -d), new Point2D(0, -d) },
            _ => new[] { new Point2D(0, 0), new Point2D(w, 0), new Point2D(w, -d), new Point2D(0, -d) }
        };
        return section.Select(point => new Point2D(point.X - edge.HorizontalOffset, point.Y + edge.VerticalOffset)).ToList();
    }

    /// <summary>
    /// A run's line moved in from the floor's edge by a distance - toward the inside, the left of
    /// a run round an anticlockwise outline - mitred at its corners, square at an open run's ends.
    /// </summary>
    public static IReadOnlyList<Point2D> Inset(IReadOnlyList<Point2D> run, bool closed, double distance)
    {
        var n = run.Count;
        var segments = closed ? n : n - 1;
        var normals = Enumerable.Range(0, segments)
            .Select(i => (run[(i + 1) % n] - run[i]).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft())
            .ToList();
        var points = new List<Point2D>();
        for (var j = 0; j < n; j++)
        {
            var (before, after) = closed
                ? (normals[(j - 1 + segments) % segments], normals[j % segments])
                : (normals[Math.Max(0, j - 1)], normals[Math.Min(segments - 1, j)]);
            var mitre = (before + after).NormalisedOrDefault(after);
            var cos = mitre.Dot(after);
            points.Add(run[j] + mitre * (distance / Math.Max(0.25, cos)));
        }

        return points;
    }

    public static double Length(BimDocument document, SlabEdge edge) =>
        Runs(document, edge).Sum(run => Enumerable.Range(0, run.Points.Count - (run.Closed ? 0 : 1))
            .Sum(i => run.Points[i].DistanceTo(run.Points[(i + 1) % run.Points.Count])));

    /// <summary>Its volume: its section's area along its length, mm³.</summary>
    public static double Volume(BimDocument document, SlabEdge edge) =>
        TypeOf(document, edge) is { } type ? Polygon2D.Area(Profile(type, edge)) * Length(document, edge) : 0;

    /// <summary>Its footprint in plan, run by run: between the nearest and furthest in its section reaches.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Outlines(BimDocument document, SlabEdge edge)
    {
        if (TypeOf(document, edge) is not { } type) return Array.Empty<IReadOnlyList<Point2D>>();
        var profile = Profile(type, edge);
        var (near, far) = (profile.Min(point => point.X), profile.Max(point => point.X));
        var outlines = new List<IReadOnlyList<Point2D>>();
        foreach (var (points, closed) in Runs(document, edge))
        {
            var outer = Inset(points, closed, near);
            var inner = Inset(points, closed, far);
            if (closed)
            {
                outlines.Add(outer);
                outlines.Add(inner);
            }
            else outlines.Add(outer.Concat(inner.Reverse()).ToList());
        }

        return outlines;
    }

    /// <summary>The slab edge as a solid: its section swept along each run, mitred at the corners, capped at an open run's ends.</summary>
    public static Mesh3D? Mesh(BimDocument document, SlabEdge edge)
    {
        if (TypeOf(document, edge) is not { } type || FloorOf(document, edge) is not { } floor) return null;
        var top = floor.GetTopElevation(document);
        var profile = Profile(type, edge);
        var colour = document.FindMaterial(type.MaterialId)?.SurfaceColour ?? new ColourRgb(0xB8, 0xB4, 0xAC);
        var mesh = new Mesh3D(edge.Id, edge.LevelId, MeshKind.Floor, colour, "Slab Edge");

        foreach (var (points, closed) in Runs(document, edge))
        {
            // One line along the run for each corner of the section, at its height.
            var lines = profile.Select(corner => Inset(points, closed, corner.X).Select(at => Point3D.On(at, top + corner.Y)).ToList()).ToList();
            var segments = closed ? points.Count : points.Count - 1;
            for (var k = 0; k < profile.Count; k++)
            {
                var (a, b) = (lines[k], lines[(k + 1) % profile.Count]);
                for (var i = 0; i < segments; i++)
                {
                    var next = (i + 1) % points.Count;
                    mesh.AddQuad(a[i], a[next], b[next], b[i]);
                }

                for (var i = 0; i < points.Count; i++) mesh.AddEdge(a[i], a[(i + 1) % points.Count]);
            }

            if (closed) continue;

            // The ends square across, the section filled.
            foreach (var end in new[] { 0, points.Count - 1 })
            {
                var cap = lines.Select(line => line[end]).ToList();
                for (var k = 1; k + 1 < cap.Count; k++)
                    if (end == 0) mesh.AddTriangle(cap[0], cap[k + 1], cap[k]);
                    else mesh.AddTriangle(cap[0], cap[k], cap[k + 1]);
                for (var k = 0; k < cap.Count; k++) mesh.AddEdge(cap[k], cap[(k + 1) % cap.Count]);
            }
        }

        return mesh.IsEmpty ? null : mesh;
    }

    /// <summary>The slab edges on floors being moved, turned or mirrored: they go too.</summary>
    public static IReadOnlyList<Element> Following(BimDocument document, IReadOnlyList<Element> moving)
    {
        var floors = moving.OfType<Floor>().Select(floor => floor.Id).ToHashSet();
        return document.Elements.OfType<SlabEdge>().Where(edge => floors.Contains(edge.FloorId) && !moving.Contains(edge)).Cast<Element>().ToList();
    }

    /// <summary>A slab edge's own points moved the way its floor is.</summary>
    public static void Transform(SlabEdge edge, Func<Point2D, Point2D> map)
    {
        var moved = edge.Edges.Select(pair => (map(pair.From), map(pair.To))).ToList();
        edge.Edges.Clear();
        edge.Edges.AddRange(moved);
    }
}
