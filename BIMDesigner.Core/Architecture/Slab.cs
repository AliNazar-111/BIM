using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A horizontal element with a sketched outline: a floor, a ceiling or a flat roof
/// (specification sections 3.2, 3.3 and 3.4).
///
/// Unlike a room, a slab's outline is <em>stored</em>. A room is a space that exists because
/// walls enclose it; a floor is a thing someone decided to build, and it does not vanish or
/// change shape because a wall moved. Picking an enclosed space is offered as a convenient
/// way to sketch that outline, not as a live link to it.
/// </summary>
public abstract class Slab : Element
{
    private readonly List<Point2D> _boundary = new();

    /// <summary>The sketched outline, anticlockwise. Millimetres.</summary>
    public IReadOnlyList<Point2D> Boundary => _boundary;

    /// <summary>
    /// Height of the slab's reference surface above its level, in millimetres. A floor sits
    /// at its level, so 0; a ceiling sits up at the underside of the storey above.
    /// </summary>
    public double HeightOffset { get; set; }

    public void SetBoundary(IEnumerable<Point2D> points)
    {
        _boundary.Clear();
        _boundary.AddRange(points);

        // Stored anticlockwise so area is positive and every slab winds the same way.
        if (Polygon2D.SignedArea(_boundary) < 0) _boundary.Reverse();
    }

    /// <summary>Plan area in mm². The quantity a finishes or takeoff schedule reports.</summary>
    public double Area => Polygon2D.Area(_boundary);

    public double Perimeter => Polygon2D.Perimeter(_boundary);

    public Point2D Centroid => Polygon2D.Centroid(_boundary, default);

    public bool Contains(Point2D point) => Polygon2D.Contains(_boundary, point);

    /// <summary>Volume in mm³, from the plan area and the type's build-up thickness.</summary>
    public double GetVolume(BimDocument document) =>
        Area * (document.FindType<SlabType>(TypeId)?.Thickness ?? 0);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<SlabType>(TypeId);

        yield return ParameterValue.BindChoice(
            SlabParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            v =>
            {
                var level = document.Levels.FirstOrDefault(l => l.Name == v);
                if (level is not null) LevelId = level.Id;
            },
            document.Levels.Select(l => l.Name).ToArray());

        yield return ParameterValue.Bind(
            SlabParameters.HeightOffset, () => HeightOffset, v => HeightOffset = v);

        // Dimensions, all from the sketched outline and the type - never stored.
        yield return ParameterValue.ReadOnly(SlabParameters.Area, () => Area);
        yield return ParameterValue.ReadOnly(SlabParameters.Perimeter, () => Perimeter);
        yield return ParameterValue.ReadOnly(SlabParameters.Thickness, () => type?.Thickness ?? 0);
        yield return ParameterValue.ReadOnly(SlabParameters.Volume, () => GetVolume(document));
        yield return ParameterValue.ReadOnly(SlabParameters.Vertices, () => Boundary.Count);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>A floor slab (specification section 3.2).</summary>
public sealed class Floor : Slab
{
    public override BuiltInCategory Category => BuiltInCategory.Floors;
}

/// <summary>A ceiling (specification section 3.4).</summary>
public sealed class Ceiling : Slab
{
    public override BuiltInCategory Category => BuiltInCategory.Ceilings;
}

/// <summary>
/// A flat roof (specification section 3.3).
///
/// Pitched roofs - slopes per edge, hips, valleys, dormers - are a different problem and are
/// not here. A flat roof is genuinely a slab; calling a pitched one a slab would be a lie
/// that every quantity downstream would inherit.
/// </summary>
public sealed class Roof : Slab
{
    public override BuiltInCategory Category => BuiltInCategory.Roofs;
}

public static class SlabParameters
{
    public static readonly ParameterDefinition Level =
        new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition HeightOffset =
        new("Height Offset From Level", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Area =
        new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Perimeter =
        new("Perimeter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Thickness =
        new("Thickness", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Volume =
        new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Vertices =
        new("Boundary Points", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
