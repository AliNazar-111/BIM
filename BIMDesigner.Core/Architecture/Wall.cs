using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Which line of the assembly the user actually draws (specification section 3.1).
///
/// This matters in practice: an architect drawing to a grid wants the core face on the
/// grid, while a draughtsman setting out a plot boundary wants the finish face. The stored
/// end points are always on the location line; the wall body is offset from it.
/// </summary>
public enum WallLocationLine
{
    WallCentreline,
    CoreCentreline,
    FinishFaceExterior,
    FinishFaceInterior,
    CoreFaceExterior,
    CoreFaceInterior
}

/// <summary>How a wall participates in the structure (specification section 3.1).</summary>
public enum StructuralUsage
{
    NonBearing,
    Bearing,
    Shear,
    StructuralCombined
}

/// <summary>
/// A wall (specification sections 3.1 and 13.1).
///
/// Instance parameters live here - where this wall is, how tall, which level. Everything
/// shared by walls of the same construction - layers, fire rating, cost - lives on
/// <see cref="WallType"/>. Quantities are computed from the two together and are never
/// stored, so they cannot drift out of date.
/// </summary>
public sealed class Wall : Element
{
    private double _unconnectedHeight = 3000;

    public override BuiltInCategory Category => BuiltInCategory.Walls;

    /// <summary>Start of the location line, in millimetres.</summary>
    public Point2D Start { get; set; }

    /// <summary>End of the location line, in millimetres.</summary>
    public Point2D End { get; set; }

    public WallLocationLine LocationLine { get; set; } = WallLocationLine.WallCentreline;

    /// <summary>Swaps which side of the wall counts as exterior.</summary>
    public bool Flipped { get; set; }

    /// <summary>Millimetres above the base level. Positive lifts the wall.</summary>
    public double BaseOffset { get; set; }

    /// <summary>
    /// Top constraint. Null means the wall uses <see cref="UnconnectedHeight"/>; otherwise
    /// its height follows that level and changes when the level moves.
    /// </summary>
    public Guid? TopLevelId { get; set; }

    /// <summary>Millimetres relative to the top level. Negative drops below it.</summary>
    public double TopOffset { get; set; }

    /// <summary>Height used when the wall has no top constraint. Millimetres.</summary>
    public double UnconnectedHeight
    {
        get => _unconnectedHeight;
        set => _unconnectedHeight = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "A wall must have a positive height.");
    }

    /// <summary>Whether this wall bounds rooms (specification section 2.5).</summary>
    public bool RoomBounding { get; set; } = true;

    public StructuralUsage StructuralUsage { get; set; } = StructuralUsage.NonBearing;

    /// <summary>Length of the location line, in millimetres.</summary>
    public double Length => Start.DistanceTo(End);

    /// <summary>Direction from start to end, or the X axis for a degenerate wall.</summary>
    public Vector2D Direction => (End - Start).NormalisedOrDefault(Vector2D.UnitX);

    /// <summary>
    /// Unit vector pointing to the exterior side of the wall. Layer 0 of the assembly faces
    /// this way.
    /// </summary>
    public Vector2D ExteriorNormal
    {
        get
        {
            var normal = Direction.PerpendicularLeft();
            return Flipped ? -normal : normal;
        }
    }

    /// <summary>
    /// Resolves the wall's height, following the top level when it is constrained to one.
    /// </summary>
    public double GetHeight(BimDocument document)
    {
        if (TopLevelId is not { } topId) return UnconnectedHeight;

        var baseLevel = document.FindLevel(LevelId);
        var topLevel = document.FindLevel(topId);
        if (baseLevel is null || topLevel is null) return UnconnectedHeight;

        var height = topLevel.Elevation + TopOffset - (baseLevel.Elevation + BaseOffset);
        return height > 0 ? height : UnconnectedHeight;
    }

    /// <summary>Elevation area of one wall face, in mm². The quantity used for finishes.</summary>
    public double GetArea(BimDocument document) => Length * GetHeight(document);

    /// <summary>Gross volume, in mm³. The quantity used for concrete and masonry takeoff.</summary>
    public double GetVolume(BimDocument document)
    {
        var type = document.FindType<WallType>(TypeId);
        return type is null ? 0 : GetArea(document) * type.Width;
    }

    /// <summary>
    /// Signed distance from the wall centreline to the location line, positive toward the
    /// exterior. The wall body is drawn by shifting the stored line back by this amount.
    /// </summary>
    public double GetLocationLineOffset(CompoundStructure structure)
    {
        var half = structure.TotalWidth / 2;

        return LocationLine switch
        {
            WallLocationLine.WallCentreline => 0,
            WallLocationLine.FinishFaceExterior => half,
            WallLocationLine.FinishFaceInterior => -half,
            WallLocationLine.CoreFaceExterior => half - structure.ExteriorWidth,
            WallLocationLine.CoreFaceInterior => -half + structure.InteriorWidth,
            WallLocationLine.CoreCentreline => half - structure.ExteriorWidth - structure.CoreWidth / 2,
            _ => 0
        };
    }

    /// <summary>
    /// The centreline of the wall body, which is the drawn line moved back off the location
    /// line. Plan rendering, joins and 3D extrusion all start from this.
    /// </summary>
    public (Point2D Start, Point2D End) GetBodyCentreline(CompoundStructure structure)
    {
        var offset = GetLocationLineOffset(structure);
        if (offset == 0) return (Start, End);

        var shift = ExteriorNormal * -offset;
        return (Start + shift, End + shift);
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        // Constraints - specification section 13.1
        yield return ParameterValue.BindChoice(
            WallParameters.BaseConstraint,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            v =>
            {
                var level = document.Levels.FirstOrDefault(l => l.Name == v);
                if (level is not null) LevelId = level.Id;
            },
            document.Levels.Select(l => l.Name).ToArray());
        yield return ParameterValue.Bind(WallParameters.BaseOffset, () => BaseOffset, v => BaseOffset = v);
        yield return ParameterValue.ReadOnly(
            WallParameters.TopConstraint,
            () => TopLevelId is { } id ? document.FindLevel(id)?.Name ?? "Unconnected" : "Unconnected");
        yield return ParameterValue.BindValidated(
            WallParameters.UnconnectedHeight,
            () => UnconnectedHeight,
            v =>
            {
                if (v <= 0) return false;
                UnconnectedHeight = v;
                return true;
            });
        yield return ParameterValue.BindChoice(
            WallParameters.LocationLine,
            () => EnumText.Humanise(LocationLine),
            v => { if (EnumText.TryParse<WallLocationLine>(v, out var l)) LocationLine = l; },
            EnumText.Choices<WallLocationLine>());
        yield return ParameterValue.Bind(WallParameters.RoomBounding, () => RoomBounding, v => RoomBounding = v);

        // Dimensions - all computed, never stored
        yield return ParameterValue.ReadOnly(WallParameters.Length, () => Length);
        yield return ParameterValue.ReadOnly(WallParameters.Height, () => GetHeight(document));
        yield return ParameterValue.ReadOnly(WallParameters.Area, () => GetArea(document));
        yield return ParameterValue.ReadOnly(WallParameters.Volume, () => GetVolume(document));

        // Analytical
        yield return ParameterValue.BindChoice(
            WallParameters.StructuralUsage,
            () => EnumText.Humanise(StructuralUsage),
            v => { if (EnumText.TryParse<StructuralUsage>(v, out var u)) StructuralUsage = u; },
            EnumText.Choices<StructuralUsage>());

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class WallParameters
{
    public static readonly ParameterDefinition BaseConstraint =
        new("Base Constraint", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition BaseOffset =
        new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopConstraint =
        new("Top Constraint", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition UnconnectedHeight =
        new("Unconnected Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition LocationLine =
        new("Location Line", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition RoomBounding =
        new("Room Bounding", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Length =
        new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Area =
        new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Volume =
        new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition StructuralUsage =
        new("Structural Usage", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
}
