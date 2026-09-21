using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Annotation;

/// <summary>What a dimension's end is measuring to.</summary>
public enum DimensionAnchor
{
    /// <summary>A bare point, tied to nothing.</summary>
    Point,

    WallStart,
    WallEnd,

    /// <summary>The nearest point on a gridline.</summary>
    Grid
}

/// <summary>
/// One end of a dimension: what it measures to, and where that is now.
///
/// A dimension that stored two coordinates would be a drawing of a measurement rather than a
/// measurement. Holding a reference instead means moving the wall moves the dimension, which
/// is the difference between a model and a picture of one. The point is kept as well, so a
/// dimension whose reference has been deleted degrades to where it last was instead of
/// vanishing or throwing.
/// </summary>
public sealed class DimensionReference
{
    public Guid ElementId { get; set; }

    public DimensionAnchor Anchor { get; set; } = DimensionAnchor.Point;

    /// <summary>Where this end was when the dimension was drawn.</summary>
    public Point2D FallbackPoint { get; set; }

    /// <summary>Whether the reference still resolves to something in the model.</summary>
    public bool IsAttached(BimDocument document) =>
        Anchor != DimensionAnchor.Point && Find(document) is not null;

    public Point2D Resolve(BimDocument document)
    {
        var element = Find(document);

        return (Anchor, element) switch
        {
            (DimensionAnchor.WallStart, Wall wall) => wall.Start,
            (DimensionAnchor.WallEnd, Wall wall) => wall.End,
            (DimensionAnchor.Grid, Grid grid) => grid.Line.ClosestPointTo(FallbackPoint),
            _ => FallbackPoint
        };
    }

    private Element? Find(BimDocument document) =>
        ElementId == Guid.Empty
            ? null
            : document.Elements.FirstOrDefault(element => element.Id == ElementId);

    public static DimensionReference ToPoint(Point2D point) => new() { FallbackPoint = point };

    public static DimensionReference ToWall(Wall wall, bool atStart) => new()
    {
        ElementId = wall.Id,
        Anchor = atStart ? DimensionAnchor.WallStart : DimensionAnchor.WallEnd,
        FallbackPoint = atStart ? wall.Start : wall.End
    };

    public static DimensionReference ToGrid(Grid grid, Point2D near) => new()
    {
        ElementId = grid.Id,
        Anchor = DimensionAnchor.Grid,
        FallbackPoint = grid.Line.ClosestPointTo(near)
    };
}

/// <summary>
/// An aligned dimension (specification section 6.3).
///
/// Its value is never stored. It is measured from the model every time it is drawn or
/// scheduled, so a dimension cannot disagree with the thing it dimensions - which is the
/// single most expensive kind of error a drawing set can carry onto a site.
/// </summary>
public sealed class Dimension : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Dimensions;

    public DimensionReference Start { get; set; } = DimensionReference.ToPoint(default);

    public DimensionReference End { get; set; } = DimensionReference.ToPoint(default);

    /// <summary>
    /// How far the dimension line sits off the line being measured, in millimetres. Positive
    /// is to the left of start-to-end.
    /// </summary>
    public double Offset { get; set; } = 600;

    /// <summary>
    /// Text shown in place of the measurement, for the rare case a drawing needs one - "VARIES",
    /// or a nominal size. Empty means show the real measurement, which is almost always right.
    /// </summary>
    public string Override { get; set; } = string.Empty;

    public Point2D StartPoint(BimDocument document) => Start.Resolve(document);

    public Point2D EndPoint(BimDocument document) => End.Resolve(document);

    /// <summary>The measurement, in millimetres, taken from the model as it stands.</summary>
    public double Measure(BimDocument document) => StartPoint(document).DistanceTo(EndPoint(document));

    /// <summary>Whether both ends still follow something, rather than floating free.</summary>
    public bool IsAssociative(BimDocument document) =>
        Start.IsAttached(document) && End.IsAttached(document);

    /// <summary>The two ends of the dimension line itself, offset from what is measured.</summary>
    public (Point2D From, Point2D To) GetDimensionLine(BimDocument document)
    {
        var from = StartPoint(document);
        var to = EndPoint(document);
        var normal = (to - from).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();

        return (from + normal * Offset, to + normal * Offset);
    }

    public string DisplayText(BimDocument document) =>
        string.IsNullOrWhiteSpace(Override) ? Units.FormatLength(Measure(document)) : Override;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(DimensionParameters.Value, () => Measure(document));
        yield return ParameterValue.ReadOnly(DimensionParameters.Associative, () => IsAssociative(document));
        yield return ParameterValue.Bind(DimensionParameters.Offset, () => Offset, v => Offset = v);
        yield return ParameterValue.Bind(DimensionParameters.Override, () => Override, v => Override = v);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => "Dimension";
}

public static class DimensionParameters
{
    public static readonly ParameterDefinition Value =
        new("Value", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Associative =
        new("Follows Model", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Offset =
        new("Line Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Override =
        new("Text Override", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}
