using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
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

    /// <summary>The name shown for a wall with no top level.</summary>
    public const string Unconnected = "Unconnected";

    /// <summary>The top constraint as shown in the property panel.</summary>
    public string TopConstraintName(BimDocument document) =>
        TopLevelId is { } id && document.FindLevel(id) is { } level ? $"Up to level: {level.Name}" : Unconnected;

    /// <summary>
    /// What the top can be set to: unconnected, or up to any level above the base. A level
    /// at or below the base would give the wall no height, so it is not offered.
    /// </summary>
    public IReadOnlyList<string> TopConstraintChoices(BimDocument document)
    {
        var baseElevation = document.FindLevel(LevelId)?.Elevation ?? double.NegativeInfinity;

        var choices = new List<string> { Unconnected };
        choices.AddRange(document.Levels
            .Where(level => level.Elevation > baseElevation || level.Id == TopLevelId)
            .Select(level => $"Up to level: {level.Name}"));

        return choices;
    }

    /// <summary>
    /// The change that sets the top constraint to one of <see cref="TopConstraintChoices"/>,
    /// or null if that would leave the wall with no height.
    ///
    /// Letting go of the top level keeps the wall the height it is now, rather than jumping
    /// back to whatever unconnected height it had before it was attached.
    /// </summary>
    public IUndoableCommand? SetTopConstraint(BimDocument document, string choice)
    {
        if (choice == Unconnected)
            return new SetWallTopCommand(this, null, TopOffset, GetHeight(document));

        var level = document.Levels.FirstOrDefault(l => $"Up to level: {l.Name}" == choice);
        if (level is null || !KeepsHeight(document, LevelId, BaseOffset, level.Id, TopOffset)) return null;

        return new SetWallTopCommand(this, level.Id, TopOffset, UnconnectedHeight);
    }

    /// <summary>Whether these constraints leave the wall with a positive height.</summary>
    public static bool KeepsHeight(
        BimDocument document, Guid baseLevelId, double baseOffset, Guid? topLevelId, double topOffset)
    {
        if (topLevelId is not { } topId) return true;

        var baseLevel = document.FindLevel(baseLevelId);
        var topLevel = document.FindLevel(topId);
        if (baseLevel is null || topLevel is null) return true;

        return topLevel.Elevation + topOffset - (baseLevel.Elevation + baseOffset) > 0;
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
        yield return ParameterValue.BindChoiceValidated(
            WallParameters.BaseConstraint,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            v =>
            {
                var level = document.Levels.FirstOrDefault(l => l.Name == v);
                if (level is null || !KeepsHeight(document, level.Id, BaseOffset, TopLevelId, TopOffset)) return false;
                LevelId = level.Id;
                return true;
            },
            document.Levels.Select(l => l.Name).ToArray());
        yield return ParameterValue.BindValidated(
            WallParameters.BaseOffset,
            () => BaseOffset,
            v =>
            {
                if (!KeepsHeight(document, LevelId, v, TopLevelId, TopOffset)) return false;
                BaseOffset = v;
                return true;
            });
        yield return ParameterValue.BindChoiceCommand(
            WallParameters.TopConstraint,
            () => TopConstraintName(document),
            v => SetTopConstraint(document, v),
            TopConstraintChoices(document));

        // The top offset only means something against a level, and the unconnected height
        // only when there is none - each is shown read-only while the other is in charge.
        if (TopLevelId is null)
        {
            yield return ParameterValue.ReadOnly(WallParameters.TopOffset, () => TopOffset);
            yield return ParameterValue.BindValidated(
                WallParameters.UnconnectedHeight,
                () => UnconnectedHeight,
                v =>
                {
                    if (v <= 0) return false;
                    UnconnectedHeight = v;
                    return true;
                });
        }
        else
        {
            yield return ParameterValue.BindValidated(
                WallParameters.TopOffset,
                () => TopOffset,
                v =>
                {
                    if (!KeepsHeight(document, LevelId, BaseOffset, TopLevelId, v)) return false;
                    TopOffset = v;
                    return true;
                });
            yield return ParameterValue.ReadOnly(WallParameters.UnconnectedHeight, () => UnconnectedHeight);
        }

        yield return ParameterValue.BindChoiceCommand(
            WallParameters.LocationLine,
            () => EnumText.Humanise(LocationLine),
            v => EnumText.TryParse<WallLocationLine>(v, out var line)
                ? new ChangeLocationLineCommand(document, this, line)
                : null,
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

    public static readonly ParameterDefinition TopOffset =
        new("Top Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

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
