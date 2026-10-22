using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A room separation line: a line on a storey that bounds rooms where no wall does - between a
/// kitchen and the living room it opens onto, across the mouth of an alcove, between an open
/// office and the corridor along it. Rooms are traced to it as to a wall, with no thickness to
/// take off either side. It is drawn on the plan and nowhere else, and is not built.
/// </summary>
public sealed class RoomSeparator : Element
{
    public override BuiltInCategory Category => BuiltInCategory.RoomSeparationLines;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    public double Length => Start.DistanceTo(End);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(RoomSeparatorParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.ReadOnly(RoomSeparatorParameters.Length, () => Length);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class RoomSeparatorParameters
{
    public static readonly ParameterDefinition Level = new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
