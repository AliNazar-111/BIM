using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A rectangular hole cut through a wall with nothing in it (specification section 3.1, "wall
/// opening"): a pass-through, a serving hatch, a gap for services. It is the way to open up a
/// curved wall, whose profile cannot be edited, and any wall where a door or window would be
/// the wrong thing to put.
///
/// Like a door it is placed along its wall rather than in the project, so it moves with the
/// wall, and it goes when the wall is deleted.
/// </summary>
public sealed class WallOpening : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.WallOpenings;

    /// <summary>The wall it is cut through.</summary>
    public Guid HostWallId { get; set; }

    public Guid HostId => HostWallId;

    /// <summary>Distance from the wall's start to the middle of the opening, along its location line.</summary>
    public double DistanceAlongWall { get; set; }

    public double Width { get; set; } = 1000;

    public double Height { get; set; } = 1000;

    /// <summary>Height of its underside above the wall's base.</summary>
    public double SillHeight { get; set; } = 900;

    /// <summary>Where it runs along its wall, and between which heights above the wall's base.</summary>
    public WallHole Hole(Wall wall) =>
        new(Math.Max(0, DistanceAlongWall - Width / 2), Math.Min(wall.Length, DistanceAlongWall + Width / 2), SillHeight, SillHeight + Height);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindValidated(WallOpeningParameters.Width, () => Width, v => Set(v > 0, () => Width = v));
        yield return ParameterValue.BindValidated(WallOpeningParameters.Height, () => Height, v => Set(v > 0, () => Height = v));
        yield return ParameterValue.BindValidated(WallOpeningParameters.SillHeight, () => SillHeight, v => Set(double.IsFinite(v), () => SillHeight = v));
        yield return ParameterValue.BindValidated(WallOpeningParameters.Distance, () => DistanceAlongWall, v => Set(v >= 0, () => DistanceAlongWall = v));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    private static bool Set(bool valid, Action set)
    {
        if (!valid) return false;
        set();
        return true;
    }
}

public static class WallOpeningParameters
{
    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition SillHeight =
        new("Sill Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Distance =
        new("Distance Along Wall", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
}
