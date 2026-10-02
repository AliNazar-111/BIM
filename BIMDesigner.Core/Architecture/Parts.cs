using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A part of a wall (Revit's Parts): one of its layers as a piece of its own - all of it, or a
/// panel of it divided off - which can be given its own material, left out, and scheduled for
/// building: the cladding as the boards that are ordered, the plasterboard as the sheets. It is
/// the wall's layer, between distances along the wall and heights up it; where it was divided,
/// it stops short of its neighbour by half the joint. A wall with parts is shown as them.
/// </summary>
public sealed class Part : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.Parts;

    /// <summary>The wall it is a part of.</summary>
    public Guid HostId { get; set; }

    /// <summary>Which of the wall's layers it is, counted from the exterior.</summary>
    public int Layer { get; set; }

    /// <summary>Where it starts and ends along the wall, mm; empty runs to the wall's end, however it is joined.</summary>
    public double? From { get; set; }

    public double? To { get; set; }

    /// <summary>How far up the wall it starts and stops, mm; empty runs to the wall's bottom or top.</summary>
    public double? Bottom { get; set; }

    public double? Top { get; set; }

    /// <summary>The joint between it and the parts it was divided from, mm: it stops half this short of each.</summary>
    public double Gap { get; set; }

    /// <summary>Its own material, or none for its layer's.</summary>
    public Guid? MaterialId { get; set; }

    /// <summary>Left out: not built, shown or scheduled - a panel taken out of the cladding.</summary>
    public bool Excluded { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var wall = Parts.Host(document, this);
        var type = wall is null ? null : document.GetWallType(wall);
        var layer = type is not null && Layer >= 0 && Layer < type.Structure.Layers.Count ? type.Structure.Layers[Layer] : null;

        yield return ParameterValue.BindChoice(PartParameters.Material,
            () => document.FindMaterial(Parts.MaterialOf(document, this))?.Name ?? string.Empty,
            name => { if (document.Materials.FirstOrDefault(m => m.Name == name) is { } material) MaterialId = material.Id; },
            document.Materials.OrderBy(m => m.Name).Select(m => m.Name).ToArray());
        yield return ParameterValue.ReadOnly(PartParameters.OriginalCategory, () => "Walls");
        yield return ParameterValue.ReadOnly(PartParameters.OriginalType, () => type?.Name ?? string.Empty);
        yield return ParameterValue.ReadOnly(PartParameters.LayerName, () => layer is null ? string.Empty : LayerFunctions.Label(layer.Function));
        yield return ParameterValue.ReadOnly(PartParameters.Thickness, () => layer?.Thickness ?? 0);
        yield return ParameterValue.ReadOnly(PartParameters.Length, () => Parts.Size(document, this).Length);
        yield return ParameterValue.ReadOnly(PartParameters.Height, () => Parts.Size(document, this).Height);
        yield return ParameterValue.ReadOnly(PartParameters.Area, () => Parts.Area(document, this));
        yield return ParameterValue.ReadOnly(PartParameters.Volume, () => Parts.Volume(document, this));
        yield return ParameterValue.Bind(PartParameters.Excluded, () => Excluded, value => Excluded = value);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class PartParameters
{
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition OriginalCategory = new("Original Category", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition OriginalType = new("Original Type", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition LayerName = new("Construction", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Thickness = new("Thickness", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Area = new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Volume = new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Excluded = new("Excluded", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
}

/// <summary>Making, dividing and building the parts of walls.</summary>
public static class Parts
{
    /// <summary>A bound further than this is open: it runs to the edge.</summary>
    private const double Far = 1e6;

    public static Wall? Host(BimDocument document, Part part) => document.Walls.FirstOrDefault(wall => wall.Id == part.HostId);

    /// <summary>The parts a wall is shown as - none for a wall shown whole.</summary>
    public static IReadOnlyList<Part> Of(BimDocument document, Wall wall) =>
        document.Elements.OfType<Part>().Where(part => part.HostId == wall.Id).ToList();

    /// <summary>Why a wall cannot be made into parts, or null: a layered wall of one type, without an edited outline.</summary>
    public static string? WhyNot(BimDocument document, Wall wall)
    {
        if (CurtainLayout.Of(document, wall) is not null) return "A curtain wall is panels and mullions already, not layers.";
        if (document.FindType<StackedWallType>(wall.TypeId) is not null) return "A stacked wall is made of other walls: make parts of a wall of one type.";
        if (document.GetWallType(wall) is not { } type || type.Structure.Layers.All(layer => layer.Thickness <= 0)) return "That wall has no layers to make parts of.";
        if (WallProfile.Of(document, wall) is not null) return "A wall with an edited outline cannot be made into parts yet.";
        if (Of(document, wall).Count > 0) return "That wall is in parts already.";
        return null;
    }

    /// <summary>Makes walls into parts, one for each layer with a thickness; null where none can be.</summary>
    public static IUndoableCommand? Create(BimDocument document, IEnumerable<Wall> walls)
    {
        var parts = new List<Element>();
        foreach (var wall in walls.Where(wall => WhyNot(document, wall) is null))
        {
            var type = document.GetWallType(wall)!;
            for (var i = 0; i < type.Structure.Layers.Count; i++)
                if (type.Structure.Layers[i].Thickness > 0)
                    parts.Add(new Part { HostId = wall.Id, LevelId = wall.LevelId, Layer = i });
        }

        return parts.Count == 0 ? null : new AddElementsCommand(document, parts, "Create Parts");
    }

    /// <summary>
    /// Divides parts into panels: along the wall every so far, and up it every so high where a
    /// height is given, with a joint between them. Each panel is a part of its own; the part
    /// divided goes. Null where nothing would be divided.
    /// </summary>
    public static IUndoableCommand? Divide(BimDocument document, IEnumerable<Part> parts, double width, double? height, double gap)
    {
        if (width <= 0 || height is <= 0 || gap < 0) return null;

        var removed = new List<Element>();
        var added = new List<Element>();
        foreach (var part in parts.Distinct())
        {
            if (Host(document, part) is not { } wall) continue;

            var (from, to) = Span(document, wall, part);
            var (bottom, top) = (part.Bottom ?? 0, part.Top ?? wall.GetHeight(document));
            var columns = Cuts(from, to, width);
            var rows = height is { } h ? Cuts(bottom, top, h) : new[] { bottom, top };
            if (columns.Length <= 2 && rows.Length <= 2) continue;

            removed.Add(part);
            for (var i = 0; i + 1 < columns.Length; i++)
            for (var j = 0; j + 1 < rows.Length; j++)
            {
                added.Add(new Part
                {
                    HostId = part.HostId, LevelId = part.LevelId, Layer = part.Layer, MaterialId = part.MaterialId, Gap = gap,
                    From = i == 0 ? part.From : columns[i],
                    To = i + 1 == columns.Length - 1 ? part.To : columns[i + 1],
                    Bottom = j == 0 ? part.Bottom : rows[j],
                    Top = j + 1 == rows.Length - 1 ? part.Top : rows[j + 1]
                });
            }
        }

        if (removed.Count == 0) return null;
        return new CompositeCommand("Divide Parts", new IUndoableCommand[]
        {
            new DeleteElementsCommand(document, removed),
            new AddElementsCommand(document, added, "Divide Parts")
        });
    }

    /// <summary>Where a range is cut every so far, from its start: the last panel takes what is left.</summary>
    private static double[] Cuts(double from, double to, double every)
    {
        var cuts = new List<double> { from };
        for (var at = from + every; at < to - Math.Min(every * 0.1, 10); at += every) cuts.Add(at);
        cuts.Add(to);
        return cuts.ToArray();
    }

    /// <summary>How far along the wall a part runs, its open ends taken to the wall's layer as it is cut.</summary>
    private static (double From, double To) Span(BimDocument document, Wall wall, Part part)
    {
        var solids = Solids(document, part, ignoreGap: true).ToList();
        if (document.GetWallType(wall) is not { } type || solids.Count == 0) return (part.From ?? 0, part.To ?? wall.Length);

        var alongs = solids.SelectMany(solid => solid.Outline).Select(point => wall.Locate(type.Structure, point).Along).ToList();
        return (part.From ?? alongs.Min(), part.To ?? alongs.Max());
    }

    /// <summary>What a part is made of: its own material, or its layer's at its middle height.</summary>
    public static Guid MaterialOf(BimDocument document, Part part)
    {
        if (part.MaterialId is { } own) return own;
        if (Host(document, part) is not { } wall || document.GetWallType(wall) is not { } type) return Guid.Empty;

        var middle = ((part.Bottom ?? 0) + (part.Top ?? wall.GetHeight(document))) / 2;
        return WallBands.MaterialAt(type, part.Layer, middle);
    }

    /// <summary>
    /// The solids a part is built as: its layer of the wall in plan, cut to its stretch along the
    /// wall, standing between heights - broken round the doors and windows in it.
    /// </summary>
    public static IEnumerable<(IReadOnlyList<Point2D> Outline, double Bottom, double Top)> Solids(BimDocument document, Part part, bool ignoreGap = false)
    {
        if (Host(document, part) is not { } wall || document.GetWallType(wall) is not { } type) yield break;
        if (part.Layer < 0 || part.Layer >= type.Structure.Layers.Count) yield break;

        var structure = type.Structure;
        var (layer, start, end) = structure.GetLayerOffsets().ElementAt(part.Layer);
        if (layer.Thickness <= 0) yield break;

        var half = structure.TotalWidth / 2;
        var (outer, inner) = (half - start, half - end);
        var height = wall.GetHeight(document);
        var baseElevation = wall.GetBaseElevation(document);
        var joint = ignoreGap ? 0 : part.Gap / 2;

        // On the wall's face, along and up: the part's rectangle, less the doors and windows.
        var (left, right) = (part.From is { } f ? f + joint : -Far, part.To is { } t ? t - joint : Far);
        var (low, high) = (part.Bottom is { } lowest ? lowest + joint : 0, part.Top is { } highest ? highest - joint : height);
        if (right - left <= 1 || high - low <= 1) yield break;

        IReadOnlyList<PolygonBoolean.Region> face = new[]
        {
            new PolygonBoolean.Region(new[] { new Point2D(left, low), new Point2D(right, low), new Point2D(right, high), new Point2D(left, high) },
                Array.Empty<IReadOnlyList<Point2D>>())
        };
        foreach (var (from, to, sill, head) in WallPaint.Openings(document, wall))
        {
            var hole = new[] { new Point2D(from, sill), new Point2D(to, sill), new Point2D(to, head), new Point2D(from, head) };
            face = face.SelectMany(region => PolygonBoolean.Combine(region.Holes.Prepend(region.Outer).ToList(), new[] { hole }, BooleanOperation.Difference))
                .Where(region => region.Area > 1).ToList();
        }

        // In upright strips between every corner, each of whole heights.
        var xs = face.SelectMany(region => region.Outer).Select(p => p.X).Distinct().OrderBy(x => x).ToList();
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var bands = WallJoins.LayerPieces(wall, type, outer, inner, startCut, endCut, WallJoins.Intrusions(document, wall, type));
        double Along(Point2D point) => wall.Locate(structure, point).Along;

        for (var i = 0; i + 1 < xs.Count; i++)
        {
            var (a, b) = (xs[i], xs[i + 1]);
            if (b - a <= 1e-6) continue;

            var strip = new[] { new Point2D(a, -Far), new Point2D(b, -Far), new Point2D(b, Far), new Point2D(a, Far) };
            foreach (var piece in face.SelectMany(region => PolygonBoolean.Combine(region.Holes.Prepend(region.Outer).ToList(), new[] { strip }, BooleanOperation.Intersection)))
            {
                var (bottom, top) = (piece.Outer.Min(p => p.Y), piece.Outer.Max(p => p.Y));
                if (top - bottom <= 1) continue;

                foreach (var band in bands)
                {
                    var outline = ModelMeshBuilder.ClipAlong(band, Along, a <= -Far / 2 ? double.NegativeInfinity : a, b >= Far / 2 ? double.PositiveInfinity : b);
                    if (outline.Count >= 3 && Math.Abs(Polygon2D.SignedArea(outline)) > 1)
                        yield return (outline, baseElevation + bottom, baseElevation + top);
                }
            }
        }
    }

    /// <summary>How long and how high a part is, from its solids.</summary>
    public static (double Length, double Height) Size(BimDocument document, Part part)
    {
        if (Host(document, part) is not { } wall || document.GetWallType(wall) is not { } type) return (0, 0);

        var solids = Solids(document, part).ToList();
        if (solids.Count == 0) return (0, 0);

        var alongs = solids.SelectMany(solid => solid.Outline).Select(point => wall.Locate(type.Structure, point).Along).ToList();
        return (alongs.Max() - alongs.Min(), solids.Max(solid => solid.Top) - solids.Min(solid => solid.Bottom));
    }

    /// <summary>The face area of a part - what a board or sheet is ordered by - less its openings, mm².</summary>
    public static double Area(BimDocument document, Part part)
    {
        if (Host(document, part) is not { } wall || document.GetWallType(wall) is not { } type) return 0;

        var thickness = type.Structure.Layers.ElementAtOrDefault(part.Layer)?.Thickness ?? 0;
        return thickness <= 0 ? 0 : Volume(document, part) / thickness;
    }

    /// <summary>A part's volume, mm³.</summary>
    public static double Volume(BimDocument document, Part part) =>
        Solids(document, part).Sum(solid => Math.Abs(Polygon2D.SignedArea(solid.Outline)) * (solid.Top - solid.Bottom));

    /// <summary>A part as the 3D view draws it, in its material.</summary>
    public static Mesh3D? Mesh(BimDocument document, Part part)
    {
        if (part.Excluded) return null;

        var material = document.FindMaterial(MaterialOf(document, part));
        var mesh = new Mesh3D(part.Id, part.LevelId, MeshKind.Wall, material?.SurfaceColour ?? new Materials.ColourRgb(0xB0, 0xB0, 0xB0),
            material?.Name ?? "Part");
        foreach (var (outline, bottom, top) in Solids(document, part)) mesh.AddExtrusion(outline, bottom, top);
        return mesh.IsEmpty ? null : mesh;
    }

    /// <summary>A part where a plan cuts it, at an elevation: its outlines, or none where it is not there.</summary>
    public static IEnumerable<IReadOnlyList<Point2D>> PlanAt(BimDocument document, Part part, double elevation) =>
        part.Excluded
            ? Enumerable.Empty<IReadOnlyList<Point2D>>()
            : Solids(document, part).Where(solid => solid.Bottom <= elevation && solid.Top >= elevation).Select(solid => solid.Outline);
}

/// <summary>Takes parts out, or puts them back, as one step.</summary>
public sealed class ExcludePartsCommand : IUndoableCommand
{
    private readonly List<(Part Part, bool Was)> _parts;
    private readonly bool _excluded;

    public ExcludePartsCommand(IEnumerable<Part> parts, bool excluded)
    {
        _parts = parts.Distinct().Where(part => part.Excluded != excluded).Select(part => (part, part.Excluded)).ToList();
        _excluded = excluded;
        Name = excluded ? "Exclude Parts" : "Restore Parts";
    }

    public string Name { get; }

    public bool IsEmpty => _parts.Count == 0;

    public void Redo()
    {
        foreach (var (part, _) in _parts) part.Excluded = _excluded;
    }

    public void Undo()
    {
        foreach (var (part, was) in _parts) part.Excluded = was;
    }
}
