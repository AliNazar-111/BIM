using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Whether a door or window in a slanted wall stands upright or leans with the wall. A door
/// placed in a slanted wall leans with it; one that was already in the wall when the wall was
/// slanted stays upright, and the gap it leaves has to be dealt with - as Revit does it.
/// </summary>
public enum OpeningOrientation
{
    Vertical,
    Slanted
}

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

    /// <summary>Whether it stands upright or leans with a slanted wall. See <see cref="OpeningOrientation"/>.</summary>
    public OpeningOrientation Orientation { get; set; } = OpeningOrientation.Vertical;

    /// <summary>
    /// Whether it is drawn standing open. A door drawn shut says nothing about the room it
    /// serves; one drawn open shows the space its leaf takes and where it lands, which is what
    /// a plan gets checked for.
    /// </summary>
    public bool IsOpen { get; set; }

    /// <summary>The centre of the opening on the wall's location line.</summary>
    public Point2D GetCentre(Wall wall) => wall.LocationCurve.PointAt(DistanceAlongWall);

    /// <summary>
    /// This opening's own width and height, when it is not the size its type says. Null follows
    /// the type, so most doors of a type stay the same size and changing the type still moves
    /// them all - but one opening can be made wider or taller without a type of its own.
    /// </summary>
    public double? WidthOverride { get; set; }

    public double? HeightOverride { get; set; }

    /// <summary>How wide this opening actually is: its own width if it has one, else its type's.</summary>
    public double WidthOf(OpeningType? type) => WidthOverride ?? type?.Width ?? 0;

    /// <summary>How tall this opening actually is.</summary>
    public double HeightOf(OpeningType? type) => HeightOverride ?? type?.Height ?? 0;

    /// <summary>
    /// A position held to what the host will take. An ordinary wall takes an opening anywhere
    /// along it. A curtain wall does not: its glass is held in a grid, so an opening stays in
    /// the panel it is in rather than drifting across a mullion into the next bay - or over the
    /// door - which is not something that could be built.
    /// </summary>
    public (double Along, double Sill) KeptInPanel(BimDocument document, OpeningType? type, double along, double sill)
    {
        if (type is null) return (along, sill);
        if (document.Walls.FirstOrDefault(w => w.Id == HostWallId) is not { } host) return (along, sill);
        if (CurtainLayout.Of(document, host) is not { } layout) return (along, sill);

        var width = WidthOf(type);
        var height = HeightOf(type);

        // The panel it is in now, which is the one it stays in.
        var middle = SillHeight + height / 2;
        if (layout.Cells.FirstOrDefault(c =>
                DistanceAlongWall >= c.From && DistanceAlongWall <= c.To && middle >= c.Bottom && middle <= c.Top) is not { } cell)
            return (along, sill);

        var (low, high) = (cell.ClearFrom + width / 2, cell.ClearTo - width / 2);
        var (bottom, top) = (cell.ClearBottom, cell.ClearTop - height);

        return (high < low ? (cell.ClearFrom + cell.ClearTo) / 2 : Math.Clamp(along, low, high),
            top < bottom ? bottom : Math.Clamp(sill, bottom, top));
    }

    /// <summary>Where the opening starts and ends, as distances along the wall.</summary>
    public (double From, double To) GetSpan(OpeningType type) =>
        (DistanceAlongWall - WidthOf(type) / 2, DistanceAlongWall + WidthOf(type) / 2);

    /// <summary>Whether the opening fits entirely within its host.</summary>
    public bool FitsWithin(Wall wall, OpeningType type)
    {
        var (from, to) = GetSpan(type);
        return from >= -WallJoins.JoinTolerance && to <= wall.Length + WallJoins.JoinTolerance;
    }

    /// <summary>
    /// Where it actually is in its wall: where it was put - brought down, and made shorter if
    /// need be, wherever the wall is not as tall there as it needs, as a dormer's gable or a
    /// wall under a slope is not. What it was given is kept, so it goes back up if the wall
    /// grows again, and follows a dormer as the dormer is changed. See <see cref="WallOpenings.Fit"/>.
    ///
    /// Everything that draws, cuts or reports it goes by this, not by its sill and height as set.
    /// </summary>
    public (double Sill, double Height) Placed(BimDocument document, OpeningType? type, Wall? wall = null, IReadOnlyList<Point2D>? outline = null)
    {
        var asSet = (SillHeight, HeightOf(type));
        if (type is null) return asSet;

        wall ??= document.Walls.FirstOrDefault(w => w.Id == HostWallId);
        return wall is null ? asSet : PlacedIn(document, wall, type, DistanceAlongWall, outline) ?? asSet;
    }

    /// <summary>
    /// Its sill and height as they would be at a place along a wall, or null when there is no
    /// room for it there. A curtain wall holds its openings in panels instead, so there it is
    /// as it is.
    /// </summary>
    public (double Sill, double Height)? PlacedIn(BimDocument document, Wall wall, OpeningType type, double along, IReadOnlyList<Point2D>? outline = null)
    {
        var (width, height) = (WidthOf(type), HeightOf(type));
        if (document.FindType<CurtainWallType>(wall.TypeId) is not null) return (SillHeight, height);

        return WallOpenings.Fit(document, wall, along - width / 2, along + width / 2, SillHeight, height, this is Door, outline);
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

        // Where it actually is, which a wall lower than it brings down - see Placed. Set, it
        // is where it is wanted, and goes there as far as the wall has room.
        yield return ParameterValue.Bind(OpeningParameters.SillHeight, () => Placed(document, type).Sill, v => SillHeight = KeptInPanel(document, type, DistanceAlongWall, v).Sill);
        // The head is the sill plus the type's height: set, it moves the opening, not its size.
        yield return type is null
            ? ParameterValue.ReadOnly(OpeningParameters.HeadHeight, () => SillHeight)
            : ParameterValue.Bind(OpeningParameters.HeadHeight, () => Placed(document, type) is var (sill, height) ? sill + height : 0,
                v => SillHeight = KeptInPanel(document, type, DistanceAlongWall, v - HeightOf(type)).Sill);

        // This opening's own size. Set back to the type's, it follows the type again rather
        // than freezing at whatever the type happened to be.
        yield return ParameterValue.Bind(
            OpeningParameters.Width,
            () => WidthOf(type),
            v =>
            {
                if (v <= 0) return;
                WidthOverride = type is not null && Math.Abs(v - type.Width) < 1e-9 ? null : v;

                // Wider, it may no longer fit where it sits: it moves along rather than
                // growing out of its panel.
                (DistanceAlongWall, SillHeight) = KeptInPanel(document, type, DistanceAlongWall, SillHeight);
            });

        yield return ParameterValue.Bind(
            OpeningParameters.Height,
            () => Placed(document, type).Height,
            v =>
            {
                if (v <= 0) return;
                HeightOverride = type is not null && Math.Abs(v - type.Height) < 1e-9 ? null : v;
                (DistanceAlongWall, SillHeight) = KeptInPanel(document, type, DistanceAlongWall, SillHeight);
            });

        yield return ParameterValue.ReadOnly(OpeningParameters.Area, () => WidthOf(type) * Placed(document, type).Height);

        yield return ParameterValue.Bind(
            OpeningParameters.DistanceAlongWall,
            () => DistanceAlongWall,
            v => { if (v >= 0) DistanceAlongWall = Math.Max(0, KeptInPanel(document, type, v, SillHeight).Along); });

        yield return ParameterValue.Bind(OpeningParameters.IsOpen, () => IsOpen, v => IsOpen = v);
        yield return ParameterValue.Bind(OpeningParameters.FlipFacing, () => FlipFacing, v => FlipFacing = v);
        yield return ParameterValue.Bind(OpeningParameters.FlipHand, () => FlipHand, v => FlipHand = v);

        // Only a slanted wall can hold a leaning door, so elsewhere this says so rather than
        // offering a choice that would do nothing.
        yield return document.Walls.FirstOrDefault(w => w.Id == HostWallId) is { } host && WallLean.Leans(host, document.GetWallType(host)!)
            ? ParameterValue.BindChoice(
                OpeningParameters.Orientation,
                () => EnumText.Humanise(Orientation),
                v => { if (EnumText.TryParse<OpeningOrientation>(v, out var o)) Orientation = o; },
                EnumText.Choices<OpeningOrientation>())
            : ParameterValue.ReadOnly(OpeningParameters.Orientation, () => EnumText.Humanise(OpeningOrientation.Vertical));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>A door (specification sections 3.5 and 13.2).</summary>
public sealed class Door : Opening
{
    public override BuiltInCategory Category => BuiltInCategory.Doors;

    /// <summary>How far the leaf is drawn open in plan, in degrees.</summary>
    public double SwingAngle { get; set; } = 90;

    /// <summary>The kind of frame round this door, as it is ordered: "Timber lining", "Steel frame".</summary>
    public string FrameType { get; set; } = string.Empty;

    /// <summary>What this door's frame is made of; empty means the type's own frame material.</summary>
    public string FrameMaterial { get; set; } = string.Empty;

    /// <summary>The finish on the frame and leaf of this door.</summary>
    public string Finish { get; set; } = string.Empty;

    /// <summary>The frame material this door is built in: its own if it has one, else its type's.</summary>
    public string FrameMaterialOr(DoorType? type) =>
        FrameMaterial.Length > 0 ? FrameMaterial : type?.FrameMaterial ?? string.Empty;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<DoorType>(TypeId);

        foreach (var parameter in GetOpeningParameters(document, type)) yield return parameter;

        yield return ParameterValue.Bind(
            DoorParameters.SwingAngle,
            () => SwingAngle,
            v => { if (v is > 0 and <= 180) SwingAngle = v; });

        yield return ParameterValue.Bind(DoorParameters.FrameType, () => FrameType, v => FrameType = v ?? string.Empty);
        yield return ParameterValue.Bind(
            DoorParameters.FrameMaterial,
            () => FrameMaterialOr(type),
            v => FrameMaterial = string.Equals(v, type?.FrameMaterial, StringComparison.Ordinal) ? string.Empty : v ?? string.Empty);
        yield return ParameterValue.Bind(DoorParameters.Finish, () => Finish, v => Finish = v ?? string.Empty);
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

    public static readonly ParameterDefinition IsOpen =
        new("Open", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition FlipFacing =
        new("Flip Facing", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition FlipHand =
        new("Flip Hand", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Area =
        new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Orientation =
        new("Orientation", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
}

public static class DoorParameters
{
    public static readonly ParameterDefinition SwingAngle =
        new("Swing Angle", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition FrameType =
        new("Frame Type", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition FrameMaterial =
        new("Frame Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition Finish =
        new("Finish", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);
}
