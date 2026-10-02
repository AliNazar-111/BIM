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
    Grid,

    /// <summary>A wall's centreline: the middle of its whole thickness.</summary>
    WallCentreline,

    /// <summary>A wall's finish face on its exterior side.</summary>
    WallExteriorFace,

    /// <summary>A wall's finish face on its interior side.</summary>
    WallInteriorFace,

    /// <summary>The middle of a wall's core - its structure - which need not be the middle of the wall.</summary>
    WallCoreCentre,

    /// <summary>The exterior face of a wall's core.</summary>
    WallCoreExterior,

    /// <summary>The interior face of a wall's core.</summary>
    WallCoreInterior
}

/// <summary>Which line of a wall a dimension measures to when a wall is clicked: Revit's "Prefer" on the options bar.</summary>
public enum DimensionPreference
{
    WallCentrelines,
    WallFaces,
    CentreOfCore,
    FacesOfCore
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
            _ when Line(document) is { } line => line.Point,
            _ => FallbackPoint
        };
    }

    /// <summary>Whether this end measures to a line of a wall rather than a point.</summary>
    public bool IsWallLine => Anchor is DimensionAnchor.WallCentreline or DimensionAnchor.WallExteriorFace or DimensionAnchor.WallInteriorFace
        or DimensionAnchor.WallCoreCentre or DimensionAnchor.WallCoreExterior or DimensionAnchor.WallCoreInterior;

    /// <summary>
    /// The line this end measures to - a gridline, or a wall's centreline, face or core face,
    /// at the point along it nearest where it was picked, and which way it runs there - or
    /// null for an end that is a point.
    /// </summary>
    public (Point2D Point, Vector2D Direction)? Line(BimDocument document)
    {
        switch (Find(document))
        {
            case Grid grid when Anchor == DimensionAnchor.Grid:
                return (grid.Line.ClosestPointTo(FallbackPoint), (grid.End - grid.Start).NormalisedOrDefault(Vector2D.UnitX));

            case Wall wall when IsWallLine && document.GetWallType(wall) is { } type:
            {
                var across = Across(type.Structure, Anchor);
                var along = Math.Clamp(wall.Locate(type.Structure, FallbackPoint).Along, 0, wall.Length);
                return (wall.PointAt(type.Structure, along, across), wall.TangentAt(along));
            }

            default:
                return null;
        }
    }

    /// <summary>A wall line's two ends, for showing which line a dimension end is on; null for a point.</summary>
    public (Point2D From, Point2D To)? WallLineSpan(BimDocument document)
    {
        if (!IsWallLine || Find(document) is not Wall wall || document.GetWallType(wall) is not { } type) return null;

        var across = Across(type.Structure, Anchor);
        return (wall.PointAt(type.Structure, 0, across), wall.PointAt(type.Structure, wall.Length, across));
    }

    /// <summary>How far a wall line lies from the middle of the wall's thickness, toward its exterior.</summary>
    private static double Across(Materials.CompoundStructure structure, DimensionAnchor anchor)
    {
        var half = structure.TotalWidth / 2;
        var coreExterior = half - structure.ExteriorWidth;
        var coreInterior = -half + structure.InteriorWidth;

        return anchor switch
        {
            DimensionAnchor.WallExteriorFace => half,
            DimensionAnchor.WallInteriorFace => -half,
            DimensionAnchor.WallCoreExterior => coreExterior,
            DimensionAnchor.WallCoreInterior => coreInterior,
            DimensionAnchor.WallCoreCentre => (coreExterior + coreInterior) / 2,
            _ => 0
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

    /// <summary>A line of a wall - its centreline, a face, its core's middle or a core face - near a point picked on it.</summary>
    public static DimensionReference ToWallLine(Wall wall, DimensionAnchor anchor, Point2D near) => new()
    {
        ElementId = wall.Id,
        Anchor = anchor,
        FallbackPoint = near
    };

    /// <summary>
    /// The line of a wall a click on it measures to, under a preference: its centreline or its
    /// core's middle; or, for faces, whichever face - or core face - is nearer the click.
    /// </summary>
    public static DimensionReference ToWall(BimDocument document, Wall wall, Point2D near, DimensionPreference prefer)
    {
        var anchor = prefer switch
        {
            DimensionPreference.WallCentrelines => DimensionAnchor.WallCentreline,
            DimensionPreference.CentreOfCore => DimensionAnchor.WallCoreCentre,
            _ when document.GetWallType(wall) is not { } type => DimensionAnchor.WallCentreline,
            DimensionPreference.WallFaces => Nearer(DimensionAnchor.WallExteriorFace, DimensionAnchor.WallInteriorFace, document.GetWallType(wall)!.Structure),
            _ => Nearer(DimensionAnchor.WallCoreExterior, DimensionAnchor.WallCoreInterior, document.GetWallType(wall)!.Structure)
        };

        return ToWallLine(wall, anchor, near);

        DimensionAnchor Nearer(DimensionAnchor exterior, DimensionAnchor interior, Materials.CompoundStructure structure)
        {
            var across = wall.Locate(structure, near).Across;
            return Math.Abs(across - Across(structure, exterior)) <= Math.Abs(across - Across(structure, interior)) ? exterior : interior;
        }
    }
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

    public Point2D StartPoint(BimDocument document) => Ends(document).From;

    public Point2D EndPoint(BimDocument document) => Ends(document).To;

    /// <summary>
    /// What is measured between. To a line - a wall's face, a gridline - it is measured square to
    /// it, as Revit's aligned dimension does: between two parallel lines, straight across from
    /// one to the other wherever they were picked; from a point to a line, to the foot of the
    /// square from the point. Between points, point to point.
    /// </summary>
    public (Point2D From, Point2D To) Ends(BimDocument document)
    {
        var (from, to) = (Start.Resolve(document), End.Resolve(document));
        var (startLine, endLine) = (Start.Line(document), End.Line(document));

        static Point2D Foot(Point2D point, (Point2D Point, Vector2D Direction) line) =>
            line.Point + line.Direction * (point - line.Point).Dot(line.Direction);

        return (startLine, endLine) switch
        {
            ({ } a, { } b) when Math.Abs(a.Direction.Cross(b.Direction)) < 0.01 => (from, Foot(from, b)),
            ({ } a, null) => (Foot(to, a), to),
            (null, { } b) => (from, Foot(from, b)),
            _ => (from, to)
        };
    }

    /// <summary>The measurement, in millimetres, taken from the model as it stands.</summary>
    public double Measure(BimDocument document) => StartPoint(document).DistanceTo(EndPoint(document));

    /// <summary>Whether both ends still follow something, rather than floating free.</summary>
    public bool IsAssociative(BimDocument document) =>
        Start.IsAttached(document) && End.IsAttached(document);

    /// <summary>The two ends of the dimension line itself, offset from what is measured.</summary>
    public (Point2D From, Point2D To) GetDimensionLine(BimDocument document)
    {
        var (from, to) = Ends(document);
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
