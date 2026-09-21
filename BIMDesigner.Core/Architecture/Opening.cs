using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A door or window, hosted in a wall (specification sections 2.5 and 3.5).
///
/// Its position is stored as a distance along the host wall, not as a point in the project.
/// That is what makes an opening behave like part of the wall: move the wall, drag its end,
/// change its location line, and every door in it follows without anything having to notice
/// and update them. A stored coordinate would drift out of the wall the first time the wall
/// moved.
/// </summary>
public abstract class Opening : Element, IHostedElement
{
    private double _distanceAlongWall;

    /// <summary>The wall this opening is cut into.</summary>
    public Guid HostWallId { get; set; }

    public Guid HostId => HostWallId;

    /// <summary>
    /// Distance from the host wall's start to the centre of the opening, in millimetres,
    /// measured along the wall's location line.
    /// </summary>
    public double DistanceAlongWall
    {
        get => _distanceAlongWall;
        set => _distanceAlongWall = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "An opening cannot sit before the wall's start.");
    }

    /// <summary>Height of the opening's underside above the wall's base, in millimetres.</summary>
    public double SillHeight { get; set; }

    /// <summary>Swaps which side of the wall the opening faces.</summary>
    public bool FlipFacing { get; set; }

    /// <summary>Swaps which end the door is hinged from.</summary>
    public bool FlipHand { get; set; }

    /// <summary>The centre of the opening on the wall's location line.</summary>
    public Point2D GetCentre(Wall wall) => wall.LocationCurve.PointAt(DistanceAlongWall);

    /// <summary>Where the opening starts and ends, as distances along the wall.</summary>
    public (double From, double To) GetSpan(OpeningType type) =>
        (DistanceAlongWall - type.Width / 2, DistanceAlongWall + type.Width / 2);

    /// <summary>Whether the opening fits entirely within its host.</summary>
    public bool FitsWithin(Wall wall, OpeningType type)
    {
        var (from, to) = GetSpan(type);
        return from >= -WallJoins.JoinTolerance && to <= wall.Length + WallJoins.JoinTolerance;
    }

    protected IEnumerable<ParameterValue> GetOpeningParameters(BimDocument document, OpeningType? type)
    {
        // Constraints - specification sections 13.2 and 13.3
        yield return ParameterValue.BindChoice(
            OpeningParameters.BaseConstraint,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            v =>
            {
                var level = document.Levels.FirstOrDefault(l => l.Name == v);
                if (level is not null) LevelId = level.Id;
            },
            document.Levels.Select(l => l.Name).ToArray());

        yield return ParameterValue.ReadOnly(
            OpeningParameters.HostWall,
            () => document.Walls.FirstOrDefault(w => w.Id == HostWallId) is { } host
                ? $"{host.Category} : {document.GetWallType(host)?.Name ?? "?"}"
                : "<none>");

        yield return ParameterValue.Bind(OpeningParameters.SillHeight, () => SillHeight, v => SillHeight = v);
        yield return ParameterValue.ReadOnly(
            OpeningParameters.HeadHeight,
            () => SillHeight + (type?.Height ?? 0));

        yield return ParameterValue.Bind(
            OpeningParameters.DistanceAlongWall,
            () => DistanceAlongWall,
            v => { if (v >= 0) DistanceAlongWall = v; });

        yield return ParameterValue.Bind(OpeningParameters.FlipFacing, () => FlipFacing, v => FlipFacing = v);
        yield return ParameterValue.Bind(OpeningParameters.FlipHand, () => FlipHand, v => FlipHand = v);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>A door (specification sections 3.5 and 13.2).</summary>
public sealed class Door : Opening
{
    public override BuiltInCategory Category => BuiltInCategory.Doors;

    /// <summary>How far the leaf is drawn open in plan, in degrees.</summary>
    public double SwingAngle { get; set; } = 90;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<DoorType>(TypeId);

        foreach (var parameter in GetOpeningParameters(document, type)) yield return parameter;

        yield return ParameterValue.Bind(
            DoorParameters.SwingAngle,
            () => SwingAngle,
            v => { if (v is > 0 and <= 180) SwingAngle = v; });
    }
}

/// <summary>A window (specification sections 3.5 and 13.3).</summary>
public sealed class Window : Opening
{
    public override BuiltInCategory Category => BuiltInCategory.Windows;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document) =>
        GetOpeningParameters(document, document.FindType<WindowType>(TypeId));
}

public static class OpeningParameters
{
    public static readonly ParameterDefinition BaseConstraint =
        new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition HostWall =
        new("Host", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition SillHeight =
        new("Sill Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition HeadHeight =
        new("Head Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition DistanceAlongWall =
        new("Distance Along Wall", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition FlipFacing =
        new("Flip Facing", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition FlipHand =
        new("Flip Hand", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
}

public static class DoorParameters
{
    public static readonly ParameterDefinition SwingAngle =
        new("Swing Angle", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Construction);
}
