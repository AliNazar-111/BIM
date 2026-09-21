using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A room (specification sections 3.7 and 13.4).
///
/// A room stores where it is, not what shape it is. Its outline, area, perimeter and volume
/// are found from the walls around it every time they are asked for, so moving a wall
/// changes the room without anyone editing the room. Storing the shape would make every
/// schedule a snapshot of the building as it was when someone last remembered to refresh.
///
/// When the walls no longer enclose the point, the room is not deleted - it is reported as
/// unenclosed, which is the thing a model-health check needs to see.
/// </summary>
public sealed class Room : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Rooms;

    /// <summary>The point the room was placed at, and where its boundary is traced from.</summary>
    public Point2D Location { get; set; }

    public string Name { get; set; } = "Room";

    public string Number { get; set; } = string.Empty;

    public string Department { get; set; } = string.Empty;

    public string Occupancy { get; set; } = string.Empty;

    public int OccupantCount { get; set; }

    /// <summary>Millimetres above the level. Together with the limit this gives the volume.</summary>
    public double BaseOffset { get; set; }

    /// <summary>Millimetres above the level to the top of the room's volume.</summary>
    public double UpperLimitOffset { get; set; } = 3000;

    public string FloorFinish { get; set; } = string.Empty;

    public string WallFinish { get; set; } = string.Empty;

    public string CeilingFinish { get; set; } = string.Empty;

    public string BaseFinish { get; set; } = string.Empty;

    /// <summary>Height of the room's volume, in millimetres.</summary>
    public double Height => Math.Max(0, UpperLimitOffset - BaseOffset);

    /// <summary>
    /// Traces the walls around this room as they stand now. Every quantity below is taken
    /// from the result, so none of them can be stale.
    /// </summary>
    public RoomBoundaryResult GetBoundary(BimDocument document) =>
        RoomBoundary.Trace(document, LevelId, Location);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        // Traced once so every quantity below agrees with the others.
        var boundary = GetBoundary(document);

        // Identity data - what appears on plans, schedules and colour fills (section 13.4)
        yield return ParameterValue.Bind(RoomParameters.Name, () => Name, v => Name = v);
        yield return ParameterValue.Bind(RoomParameters.Number, () => Number, v => Number = v);
        yield return ParameterValue.Bind(RoomParameters.Department, () => Department, v => Department = v);
        yield return ParameterValue.Bind(RoomParameters.Occupancy, () => Occupancy, v => Occupancy = v);
        yield return ParameterValue.Bind(
            RoomParameters.OccupantCount,
            () => OccupantCount,
            v => { if (v >= 0) OccupantCount = v; });

        // Constraints - the vertical extent the volume is computed over
        yield return ParameterValue.BindChoice(
            RoomParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            v =>
            {
                var level = document.Levels.FirstOrDefault(l => l.Name == v);
                if (level is not null) LevelId = level.Id;
            },
            document.Levels.Select(l => l.Name).ToArray());

        yield return ParameterValue.Bind(RoomParameters.BaseOffset, () => BaseOffset, v => BaseOffset = v);
        yield return ParameterValue.Bind(
            RoomParameters.UpperLimit,
            () => UpperLimitOffset,
            v => { if (v > BaseOffset) UpperLimitOffset = v; });

        // Dimensions - all traced from the bounding walls, never stored.
        //
        // When the walls do not enclose the point there is no area, and saying so is the whole
        // point: a room that quietly reported 0.00 m² would put a real-looking number in an
        // area schedule, and that is how a wrong area reaches a client. No value at all cannot
        // be mistaken for a measurement.
        yield return ParameterValue.ReadOnly(RoomParameters.Enclosed, () => boundary.IsEnclosed);

        yield return ParameterValue.ReadOnly(
            RoomParameters.Area, () => boundary.IsEnclosed ? boundary.Area : (double?)null);

        yield return ParameterValue.ReadOnly(
            RoomParameters.Perimeter, () => boundary.IsEnclosed ? boundary.Perimeter : (double?)null);

        yield return ParameterValue.ReadOnly(
            RoomParameters.Volume, () => boundary.IsEnclosed ? boundary.Area * Height : (double?)null);
        yield return ParameterValue.ReadOnly(RoomParameters.Height, () => Height);
        yield return ParameterValue.ReadOnly(RoomParameters.BoundingWalls, () => boundary.BoundingWalls.Count);

        // Finishes - what a finishes schedule reports
        yield return ParameterValue.Bind(RoomParameters.FloorFinish, () => FloorFinish, v => FloorFinish = v);
        yield return ParameterValue.Bind(RoomParameters.WallFinish, () => WallFinish, v => WallFinish = v);
        yield return ParameterValue.Bind(RoomParameters.CeilingFinish, () => CeilingFinish, v => CeilingFinish = v);
        yield return ParameterValue.Bind(RoomParameters.BaseFinish, () => BaseFinish, v => BaseFinish = v);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Number) ? Name : $"{Number} {Name}";
}

public static class RoomParameters
{
    public static readonly ParameterDefinition Name =
        new("Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Number =
        new("Number", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Department =
        new("Department", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Occupancy =
        new("Occupancy", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition OccupantCount =
        new("Occupant Count", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Level =
        new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition BaseOffset =
        new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition UpperLimit =
        new("Upper Limit", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Enclosed =
        new("Enclosed", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Area =
        new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Perimeter =
        new("Perimeter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Volume =
        new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Unbounded Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition BoundingWalls =
        new("Bounding Walls", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition FloorFinish =
        new("Floor Finish", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition WallFinish =
        new("Wall Finish", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition CeilingFinish =
        new("Ceiling Finish", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition BaseFinish =
        new("Base Finish", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);
}
