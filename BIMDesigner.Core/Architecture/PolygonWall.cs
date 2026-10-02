using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a shaped wall is set out in plan.</summary>
public enum PolygonWallKind
{
    /// <summary>Any outline clicked round, closed: a wall of any shape in plan.</summary>
    Polygon,

    /// <summary>Along a line, thicker at one end than the other: a trapezoid in plan.</summary>
    Trapezoid
}

/// <summary>Which face of a trapezoid wall runs straight along its line, the other splaying.</summary>
public enum TrapezoidSide
{
    /// <summary>Both faces splay, evenly about the line.</summary>
    Centre,

    /// <summary>The face on the left of the line, start to end, is straight.</summary>
    Left,

    /// <summary>The face on the right is straight.</summary>
    Right
}

/// <summary>
/// A wall shaped in plan rather than drawn along a line at one thickness, as Archicad's polygon
/// and trapezoid walls are: of one material through, standing from its level to its height or the
/// level it is taken up to. A trapezoid wall is thicker at one end than the other - a retaining
/// wall thinning as it rises in plan, a splayed reveal; a polygon wall is any outline at all.
/// </summary>
public sealed class PolygonWall : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Walls;

    public PolygonWallKind Kind { get; set; } = PolygonWallKind.Polygon;

    private List<Point2D> _outline = new();

    /// <summary>Its outline in plan, anticlockwise.</summary>
    public IReadOnlyList<Point2D> Outline
    {
        get => _outline;
        set => _outline = Polygon2D.SignedArea(value) < 0 ? value.Reverse().ToList() : value.ToList();
    }

    /// <summary>A trapezoid wall's line, from its start to its end.</summary>
    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    /// <summary>A trapezoid wall's thickness at its start and at its end, mm.</summary>
    public double StartThickness { get; set; } = 300;

    public double EndThickness { get; set; } = 150;

    public TrapezoidSide StraightSide { get; set; } = TrapezoidSide.Centre;

    /// <summary>What it is made of, through.</summary>
    public Guid MaterialId { get; set; }

    public double BaseOffset { get; set; }

    /// <summary>The level its top is taken up to, or none to stand to its own height.</summary>
    public Guid? TopLevelId { get; set; }

    public double TopOffset { get; set; }

    public double UnconnectedHeight { get; set; } = 3000;

    /// <summary>Sets a trapezoid wall's outline from its line, thicknesses and straight side.</summary>
    public void Shape()
    {
        if (Kind != PolygonWallKind.Trapezoid) return;
        if (PolygonWalls.Trapezoid(Start, End, StartThickness, EndThickness, StraightSide) is { } outline) Outline = outline;
    }

    public double GetBaseElevation(BimDocument document) => (document.FindLevel(LevelId)?.Elevation ?? 0) + BaseOffset;

    public double GetTopElevation(BimDocument document) =>
        TopLevelId is { } top && document.FindLevel(top) is { } level ? level.Elevation + TopOffset : GetBaseElevation(document) + UnconnectedHeight;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(PolygonWallParameters.Kind, () => Kind == PolygonWallKind.Trapezoid ? "Trapezoid" : "Polygon");
        yield return ParameterValue.BindChoice(PolygonWallParameters.BaseLevel,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            name => { if (document.Levels.FirstOrDefault(level => level.Name == name) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.Bind(PolygonWallParameters.BaseOffset, () => BaseOffset, value => BaseOffset = value);
        yield return ParameterValue.BindChoice(PolygonWallParameters.TopConstraint,
            () => TopLevelId is { } top && document.FindLevel(top) is { } level ? $"Up to level: {level.Name}" : "Unconnected",
            choice => TopLevelId = document.Levels.FirstOrDefault(level => $"Up to level: {level.Name}" == choice)?.Id,
            document.Levels.Select(level => $"Up to level: {level.Name}").Prepend("Unconnected").ToArray());
        yield return ParameterValue.Bind(PolygonWallParameters.TopOffset, () => TopOffset, value => TopOffset = value);
        yield return ParameterValue.BindValidated(PolygonWallParameters.UnconnectedHeight, () => UnconnectedHeight, (double value) =>
        {
            if (value <= 0) return false;
            UnconnectedHeight = value;
            return true;
        });

        if (Kind == PolygonWallKind.Trapezoid)
        {
            yield return Thickness(PolygonWallParameters.StartThickness, () => StartThickness, value => StartThickness = value);
            yield return Thickness(PolygonWallParameters.EndThickness, () => EndThickness, value => EndThickness = value);
            yield return ParameterValue.BindChoice(PolygonWallParameters.StraightSide,
                () => EnumText.Humanise(StraightSide),
                value => { if (EnumText.TryParse<TrapezoidSide>(value, out var side)) { StraightSide = side; Shape(); } },
                EnumText.Choices<TrapezoidSide>());
            yield return ParameterValue.ReadOnly(PolygonWallParameters.Length, () => Start.DistanceTo(End));
        }

        yield return ParameterValue.BindChoice(PolygonWallParameters.Material,
            () => document.FindMaterial(MaterialId)?.Name ?? string.Empty,
            name => { if (document.Materials.FirstOrDefault(m => m.Name == name) is { } material) MaterialId = material.Id; },
            document.Materials.OrderBy(m => m.Name).Select(m => m.Name).ToArray());
        yield return ParameterValue.ReadOnly(PolygonWallParameters.Height, () => GetTopElevation(document) - GetBaseElevation(document));
        yield return ParameterValue.ReadOnly(PolygonWallParameters.FootprintArea, () => Polygon2D.Area(Outline));
        yield return ParameterValue.ReadOnly(PolygonWallParameters.Volume, () => PolygonWalls.Volume(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    private ParameterValue Thickness(ParameterDefinition definition, Func<double> get, Action<double> set) =>
        ParameterValue.BindValidated(definition, get, (double value) =>
        {
            if (value < 10 || value > 10000) return false;
            set(value);
            Shape();
            return true;
        });
}

public static class PolygonWallParameters
{
    public static readonly ParameterDefinition Kind = new("Wall Shape", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition BaseLevel = new("Base Constraint", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BaseOffset = new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition TopConstraint = new("Top Constraint", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition TopOffset = new("Top Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition UnconnectedHeight = new("Unconnected Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition StartThickness = new("Thickness at Start", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition EndThickness = new("Thickness at End", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition StraightSide = new("Straight Face", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Height = new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FootprintArea = new("Footprint Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Volume = new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
}

/// <summary>Setting out and building polygon and trapezoid walls.</summary>
public static class PolygonWalls
{
    /// <summary>
    /// A trapezoid wall's outline: along its line, as thick as its start thickness at the start
    /// and its end thickness at the end, one face straight along the line or both splayed evenly.
    /// Null for a line with no length.
    /// </summary>
    public static IReadOnlyList<Point2D>? Trapezoid(Point2D start, Point2D end, double startThickness, double endThickness, TrapezoidSide side)
    {
        if (start.DistanceTo(end) < 1 || startThickness <= 0 || endThickness <= 0) return null;

        var left = (end - start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        (double Left, double Right) Spread(double thickness) => side switch
        {
            TrapezoidSide.Left => (0, thickness),
            TrapezoidSide.Right => (thickness, 0),
            _ => (thickness / 2, thickness / 2)
        };

        var (startLeft, startRight) = Spread(startThickness);
        var (endLeft, endRight) = Spread(endThickness);
        return new[] { start - left * startRight, end - left * endRight, end + left * endLeft, start + left * startLeft };
    }

    /// <summary>A closed outline clicked round: refused, with why, where it is too small or crosses itself.</summary>
    public static string? OutlineProblem(IReadOnlyList<Point2D> outline)
    {
        if (outline.Count < 3) return "A polygon wall needs at least three corners.";

        for (var i = 0; i < outline.Count; i++)
        for (var j = i + 2; j < outline.Count; j++)
        {
            if (i == 0 && j == outline.Count - 1) continue;
            if (Cross(outline[i], outline[(i + 1) % outline.Count], outline[j], outline[(j + 1) % outline.Count]))
                return "That outline crosses itself.";
        }

        if (Polygon2D.Area(outline) < 10000) return "That outline is too small to be a wall.";
        return null;

        // Whether two segments cross each other properly, each passing to both sides of the other.
        static bool Cross(Point2D a, Point2D b, Point2D c, Point2D d)
        {
            static double Side(Point2D p, Point2D q, Point2D r) => (q - p).Cross(r - p);
            return Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;
        }
    }

    public static double Volume(BimDocument document, PolygonWall wall) =>
        Polygon2D.Area(wall.Outline) * Math.Max(0, wall.GetTopElevation(document) - wall.GetBaseElevation(document));

    /// <summary>The wall as the 3D view draws it: its outline stood up between its base and top.</summary>
    public static Mesh3D? Mesh(BimDocument document, PolygonWall wall)
    {
        var (bottom, top) = (wall.GetBaseElevation(document), wall.GetTopElevation(document));
        if (wall.Outline.Count < 3 || top <= bottom) return null;

        var material = document.FindMaterial(wall.MaterialId);
        var mesh = new Mesh3D(wall.Id, wall.LevelId, MeshKind.Wall, material?.SurfaceColour ?? new ColourRgb(0xB8, 0xB4, 0xAC), material?.Name ?? "Wall");
        mesh.AddExtrusion(wall.Outline, bottom, top);
        return mesh;
    }
}
