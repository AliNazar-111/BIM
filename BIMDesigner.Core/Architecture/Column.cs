using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What happens where an attached column meets what it is attached to.
/// </summary>
public enum ColumnAttachmentStyle
{
    /// <summary>
    /// The column is cut by the target: it stops at the face it meets - the underside of the
    /// slab over it, the top of the floor under it. What a column butting a soffit does.
    /// </summary>
    CutColumn,

    /// <summary>
    /// Neither is cut: the column carries on past the face and through the target, which is
    /// what a column doing the carrying does.
    /// </summary>
    DoNotCut
}

/// <summary>What a column is shaped like in plan.</summary>
public enum ColumnShape
{
    Rectangular,
    Round,

    /// <summary>A quarter cut out of a square, for a column turning the corner of a room.</summary>
    LShaped,

    Triangular,
    Hexagonal,
    Octagonal,

    /// <summary>A section drawn in the column editor rather than named here.</summary>
    Custom
}

/// <summary>
/// One type of architectural column (specification section 3.2): its shape, its size, and what
/// it is made of.
///
/// An architectural column is the box-out round a structural column, or a column that is there
/// to be looked at. It is not the structure - a structural column is a different category with
/// a different schedule - which is why its type carries a finish rather than a section.
/// </summary>
public sealed class ColumnType : ElementType
{
    public ColumnType(string name, ColumnShape shape, double width, double depth) : base(name)
    {
        Shape = shape;
        Width = width;
        Depth = depth;
    }

    public override BuiltInCategory Category => BuiltInCategory.Columns;

    public ColumnShape Shape { get; set; }

    /// <summary>Across the column. A round one is this across, and its depth is ignored.</summary>
    public double Width { get; set; }

    public double Depth { get; set; }

    public Guid MaterialId { get; set; }

    /// <summary>How it is filled in plan at coarse detail, as a wall type has a fill of its own.</summary>
    public ColourRgb CoarseScaleFillColour { get; set; } = new(0x8A, 0x8F, 0x96);

    /// <summary>
    /// How far the base of every column of this type sits below its base level, and the top
    /// above its top level. These belong to the type rather than the instance: a column family
    /// that always stands on a 50 mm plinth says so once, and every one placed from it does it.
    /// </summary>
    public double OffsetBase { get; set; }

    public double OffsetTop { get; set; }

    /// <summary>
    /// A section drawn in the column editor, kept about the column's own centre. Null while the
    /// type is one of the named shapes, which are generated from the width and depth instead.
    /// </summary>
    public ColumnProfile? CustomProfile { get; set; }

    /// <summary>
    /// What the column does between its base and its top: taper, twist, lean, fluting, and
    /// whether it is built with a base and a capital.
    /// </summary>
    public ColumnShaping Shaping { get; set; } = new();

    /// <summary>
    /// The section this type is built from, whichever way it was arrived at. Everything that
    /// draws, measures or exports a column reads this, so a drawn section is not a special case.
    /// </summary>
    public ColumnProfile Profile =>
        Shape == ColumnShape.Custom && CustomProfile is { IsEmpty: false } drawn
            ? drawn
            : ColumnProfile.Of(Shape, Width, Depth);

    /// <summary>The area of its section, which is what a volume is worked out from.</summary>
    public double SectionArea => Profile.Area;

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(
            ColumnTypeParameters.Shape,
            () => EnumText.Humanise(Shape),
            v => { if (EnumText.TryParse<ColumnShape>(v, out var shape)) Shape = shape; },
            EnumText.Choices<ColumnShape>());

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.Width,
            () => Width,
            v => { if (v <= 0) return false; Width = v; return true; });

        // A round column is the same both ways, so its depth is not a question worth asking.
        if (Shape == ColumnShape.Round)
            yield return ParameterValue.ReadOnly(ColumnTypeParameters.Depth, () => Width);
        else
            yield return ParameterValue.BindValidated(
                ColumnTypeParameters.Depth,
                () => Depth,
                v => { if (v <= 0) return false; Depth = v; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.OffsetBase, () => OffsetBase, v => { OffsetBase = v; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.OffsetTop, () => OffsetTop, v => { OffsetTop = v; return true; });

        yield return ParameterValue.ReadOnly(ColumnTypeParameters.SectionArea, () => SectionArea);

        // What it is made of, chosen from the project's materials - the same list the wall
        // types are built from, so a column can be the concrete the frame is.
        if (document is not null)
            yield return ParameterValue.BindChoice(
                ColumnTypeParameters.Material,
                () => document.FindMaterial(MaterialId)?.Name ?? "<by category>",
                v => MaterialId = document.Materials.FirstOrDefault(m => m.Name == v)?.Id ?? Guid.Empty,
                new[] { "<by category>" }.Concat(document.Materials.Select(m => m.Name).OrderBy(name => name)).ToArray());

        yield return ParameterValue.Bind(
            ColumnTypeParameters.CoarseScaleFillColour,
            () => CoarseScaleFillColour.ToString(),
            v => { if (ColourRgb.TryParse(v, out var colour)) CoarseScaleFillColour = colour; });

        // What it does on the way up. Each is a number the geometry is regenerated from, so a
        // column changes shape when its height changes rather than keeping a stale mesh.
        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.TopScale,
            () => Shaping.TopScale * 100,
            v => { if (v is <= 0 or > 400) return false; Shaping.TopScale = v / 100; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.Twist, () => Shaping.Twist, v => { Shaping.Twist = v; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.SlantAcross, () => Shaping.SlantAcross, v => { Shaping.SlantAcross = v; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.SlantAlong, () => Shaping.SlantAlong, v => { Shaping.SlantAlong = v; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.Flutes,
            () => Shaping.Flutes,
            (int v) => { if (v is < 0 or > 96) return false; Shaping.Flutes = v; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.FluteDepth,
            () => Shaping.FluteDepth,
            v => { if (v < 0) return false; Shaping.FluteDepth = v; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.BaseHeight,
            () => Shaping.BaseHeight,
            v => { if (v < 0) return false; Shaping.BaseHeight = v; return true; });

        yield return ParameterValue.BindValidated(
            ColumnTypeParameters.CapitalHeight,
            () => Shaping.CapitalHeight,
            v => { if (v < 0) return false; Shaping.CapitalHeight = v; return true; });

        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }
}

/// <summary>
/// An architectural column standing in the model (specification section 3.2).
///
/// Its height is not a number it carries but the distance between what it stands on and what it
/// reaches: a base level and a top level with their offsets, or whatever it has been attached
/// to. So a column follows the roof it meets when the roof moves, which is the whole reason for
/// constraining it rather than typing a height - and it is the same rule a wall is held by, on
/// purpose, because they are the same question.
/// </summary>
public sealed class Column : Element
{
    /// <summary>How far a chain of attachments is followed before it is taken as circular.</summary>
    private const int MaxAttachmentChain = 8;

    public override BuiltInCategory Category => BuiltInCategory.Columns;

    /// <summary>Where it stands: the middle of its section.</summary>
    public Point2D Location { get; set; }

    /// <summary>How far it has been turned, anticlockwise from square to the plan, in degrees.</summary>
    public double Rotation { get; set; }

    /// <summary>Millimetres above its base level, before any attachment.</summary>
    public double BaseOffset { get; set; }

    /// <summary>Top constraint. Null leaves it standing <see cref="UnconnectedHeight"/> high.</summary>
    public Guid? TopLevelId { get; set; }

    public double TopOffset { get; set; }

    private double _unconnectedHeight = 3000;

    /// <summary>How high it stands when nothing above constrains it.</summary>
    public double UnconnectedHeight
    {
        get => _unconnectedHeight;
        set => _unconnectedHeight = Math.Max(1, value);
    }

    /// <summary>The slab its top is cut to, if it has been attached to one.</summary>
    public Guid? TopAttachedTo { get; set; }

    /// <summary>The slab its base stands on, if it has been attached to one.</summary>
    public Guid? BaseAttachedTo { get; set; }

    /// <summary>
    /// Whether it comes along when the grid it stands on is moved. On by default, because a
    /// column on a grid is there because the grid put it there; turned off for one that has
    /// been moved off the setting out on purpose.
    /// </summary>
    public bool MovesWithGrids { get; set; } = true;

    /// <summary>
    /// Whether it takes its area out of the room it stands in. On by default: a column in a
    /// room is floor the room has not got, and an area schedule that counts it is wrong.
    /// </summary>
    public bool RoomBounding { get; set; } = true;

    /// <summary>
    /// How it is meant to sit against the walls round it, which is what puts it back where it
    /// belongs when one of them moves.
    /// </summary>
    public ColumnPlacement Placement { get; set; } = ColumnPlacement.Freestanding;

    /// <summary>Which face of the wall its placement is measured from.</summary>
    public ColumnJoins.Face PlacementFace { get; set; } = ColumnJoins.Face.Interior;

    /// <summary>
    /// Whether the walls it is buried in are taken out of it. On by default: a column and a
    /// wall in the same place is two solids in one place, which is wrong everywhere it is
    /// counted. A column that goes right through a wall is a pier, and the wall gives way to
    /// that one instead - this decides only what happens to a column standing part way in.
    /// </summary>
    public bool CutByWalls { get; set; } = true;

    /// <summary>
    /// How far its top stops short of what it is attached to, and its base off what it stands
    /// on. Positive pulls the column back from the face, which is how a column is held clear of
    /// a slab it should not be carrying.
    /// </summary>
    public double OffsetFromAttachmentAtTop { get; set; }

    public double OffsetFromAttachmentAtBase { get; set; }

    /// <summary>What happens where the top meets what it is attached to, and where the base does.</summary>
    public ColumnAttachmentStyle TopAttachmentStyle { get; set; } = ColumnAttachmentStyle.CutColumn;

    public ColumnAttachmentStyle BaseAttachmentStyle { get; set; } = ColumnAttachmentStyle.CutColumn;

    /// <summary>Whether something above is holding its top, which is what Revit reports read-only.</summary>
    public bool TopIsAttached => TopAttachedTo is not null;

    public bool BaseIsAttached => BaseAttachedTo is not null;

    /// <summary>Its width axis in plan.</summary>
    public Vector2D Across
    {
        get
        {
            var radians = Rotation * Math.PI / 180;
            return new Vector2D(Math.Cos(radians), Math.Sin(radians));
        }
    }

    public double GetBaseElevation(BimDocument document) => GetBaseElevation(document, 0);

    private double GetBaseElevation(BimDocument document, int depth)
    {
        var type = document.FindType<ColumnType>(TypeId);

        // Standing on what it is attached to, or running down through it.
        if (BaseAttachedTo is { } id && FindSlab(document, id) is { } slab)
            return (BaseAttachmentStyle == ColumnAttachmentStyle.CutColumn
                ? slab.GetTopElevation(document)
                : slab.GetBottomElevation(document)) + OffsetFromAttachmentAtBase;

        return (document.FindLevel(LevelId)?.Elevation ?? 0) + BaseOffset - (type?.OffsetBase ?? 0);
    }

    public double GetTopElevation(BimDocument document) => GetTopElevation(document, 0);

    private double GetTopElevation(BimDocument document, int depth)
    {
        var bottom = GetBaseElevation(document, depth + 1);
        var type = document.FindType<ColumnType>(TypeId);

        // Attached, it stops at the face it meets, or carries on through to the far side of it,
        // less any offset holding it clear of that face.
        if (TopAttachedTo is { } id && FindSlab(document, id) is { } slab)
        {
            var met = TopAttachmentStyle == ColumnAttachmentStyle.CutColumn
                ? slab.GetBottomElevation(document)
                : slab.GetTopElevation(document);

            return Math.Max(bottom + 1, met - OffsetFromAttachmentAtTop);
        }

        if (TopLevelId is { } topId && document.FindLevel(topId) is { } level)
            return Math.Max(bottom + 1, level.Elevation + TopOffset + (type?.OffsetTop ?? 0));

        return bottom + UnconnectedHeight;
    }

    public double GetHeight(BimDocument document) => GetTopElevation(document) - GetBaseElevation(document);

    private static Slab? FindSlab(BimDocument document, Guid id) =>
        document.Elements.OfType<Slab>().FirstOrDefault(slab => slab.Id == id);

    /// <summary>What its top is held by, as Properties says it.</summary>
    public string DescribeTop(BimDocument document) =>
        TopAttachedTo is { } id && FindSlab(document, id) is { } slab
            ? $"Attached to {slab.Category}"
            : TopLevelId is { } topId && document.FindLevel(topId) is { } level
                ? $"Up to level: {level.Name}"
                : "Unconnected";

    /// <summary>
    /// Its section where it actually stands: the type's profile moved to where the column is
    /// and turned the way it faces, holes and all.
    /// </summary>
    public ColumnProfile SectionAt(ColumnType type) => type.Profile.PlacedAt(Location, Across);

    /// <summary>Its outline in plan, turned the way it stands. Holes in it are not in this.</summary>
    public IReadOnlyList<Point2D> Outline(ColumnType type) => SectionAt(type).Outer;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<ColumnType>(TypeId);

        yield return ParameterValue.BindChoice(
            ColumnParameters.BaseLevel,
            () => document.Levels.FirstOrDefault(l => l.Id == LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(l => l.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(l => l.Name).ToArray());

        yield return ParameterValue.BindValidated(
            ColumnParameters.BaseOffset, () => BaseOffset, v => { BaseOffset = v; return true; });

        // "Unconnected" and a named level, exactly as a wall's top constraint reads.
        yield return ParameterValue.BindChoice(
            ColumnParameters.TopLevel,
            () => TopLevelId is { } id && document.FindLevel(id) is { } level ? level.Name : "Unconnected",
            v => TopLevelId = document.Levels.FirstOrDefault(l => l.Name == v)?.Id,
            new[] { "Unconnected" }.Concat(document.Levels.Select(l => l.Name)).ToArray());

        yield return ParameterValue.BindValidated(
            ColumnParameters.TopOffset, () => TopOffset, v => { TopOffset = v; return true; });

        // Only a column standing free has a height of its own to be set.
        if (TopLevelId is null && TopAttachedTo is null)
            yield return ParameterValue.BindValidated(
                ColumnParameters.UnconnectedHeight,
                () => UnconnectedHeight,
                v => { if (v <= 0) return false; UnconnectedHeight = v; return true; });
        else
            yield return ParameterValue.ReadOnly(ColumnParameters.UnconnectedHeight, () => GetHeight(document));

        yield return ParameterValue.ReadOnly(ColumnParameters.TopIs, () => DescribeTop(document));
        yield return ParameterValue.ReadOnly(ColumnParameters.TopIsAttached, () => TopIsAttached);
        yield return ParameterValue.ReadOnly(ColumnParameters.BaseIsAttached, () => BaseIsAttached);

        // Only worth asking about while something is actually holding that end.
        if (TopIsAttached)
        {
            yield return ParameterValue.BindChoice(
                ColumnParameters.TopAttachmentStyle,
                () => EnumText.Humanise(TopAttachmentStyle),
                v => { if (EnumText.TryParse<ColumnAttachmentStyle>(v, out var style)) TopAttachmentStyle = style; },
                EnumText.Choices<ColumnAttachmentStyle>());

            yield return ParameterValue.BindValidated(
                ColumnParameters.OffsetFromAttachmentAtTop,
                () => OffsetFromAttachmentAtTop,
                v => { OffsetFromAttachmentAtTop = v; return true; });
        }

        if (BaseIsAttached)
        {
            yield return ParameterValue.BindChoice(
                ColumnParameters.BaseAttachmentStyle,
                () => EnumText.Humanise(BaseAttachmentStyle),
                v => { if (EnumText.TryParse<ColumnAttachmentStyle>(v, out var style)) BaseAttachmentStyle = style; },
                EnumText.Choices<ColumnAttachmentStyle>());

            yield return ParameterValue.BindValidated(
                ColumnParameters.OffsetFromAttachmentAtBase,
                () => OffsetFromAttachmentAtBase,
                v => { OffsetFromAttachmentAtBase = v; return true; });
        }

        yield return ParameterValue.Bind(
            ColumnParameters.RoomBounding, () => RoomBounding, v => RoomBounding = v);

        yield return ParameterValue.Bind(
            ColumnParameters.CutByWalls, () => CutByWalls, v => CutByWalls = v);

        // How it sits against the walls. Changing it moves the column to suit, there and then.
        yield return ParameterValue.BindChoice(
            ColumnParameters.Placement,
            () => EnumText.Humanise(Placement),
            v =>
            {
                if (!EnumText.TryParse<ColumnPlacement>(v, out var placement)) return;

                Placement = placement;
                if (document.FindType<ColumnType>(TypeId) is { } columnType &&
                    ColumnWallPlacement.PositionFor(document, this, columnType, placement, PlacementFace) is { } where)
                {
                    Location = where;
                }
            },
            EnumText.Choices<ColumnPlacement>());

        if (Placement is ColumnPlacement.AgainstWall or ColumnPlacement.EmbeddedInWall)
            yield return ParameterValue.BindChoice(
                ColumnParameters.PlacementFace,
                () => EnumText.Humanise(PlacementFace),
                v =>
                {
                    if (!EnumText.TryParse<ColumnJoins.Face>(v, out var face)) return;

                    PlacementFace = face;
                    if (document.FindType<ColumnType>(TypeId) is { } columnType &&
                        ColumnWallPlacement.PositionFor(document, this, columnType, Placement, face) is { } where)
                    {
                        Location = where;
                    }
                },
                EnumText.Choices<ColumnJoins.Face>());

        yield return ParameterValue.BindValidated(
            ColumnParameters.Rotation,
            () => Rotation,
            v => { Rotation = ((v % 360) + 360) % 360; return true; });

        yield return ParameterValue.Bind(
            ColumnParameters.MovesWithGrids, () => MovesWithGrids, v => MovesWithGrids = v);

        yield return ParameterValue.ReadOnly(ColumnParameters.Volume,
            () => (type?.SectionArea ?? 0) * Math.Max(0, GetHeight(document)));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class ColumnTypeParameters
{
    public static readonly ParameterDefinition Shape =
        new("Shape", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Depth =
        new("Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition OffsetBase =
        new("Offset Base", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition OffsetTop =
        new("Offset Top", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition SectionArea =
        new("Section Area", ParameterDataType.Area, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Material =
        new("Material", ParameterDataType.Material, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition CoarseScaleFillColour =
        new("Coarse Scale Fill Colour", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Graphics);

    public static readonly ParameterDefinition TopScale =
        new("Top Size", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Twist =
        new("Twist", ParameterDataType.Angle, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition SlantAcross =
        new("Slant Across", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition SlantAlong =
        new("Slant Along", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Flutes =
        new("Flutes", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FluteDepth =
        new("Flute Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition BaseHeight =
        new("Base Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition CapitalHeight =
        new("Capital Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);
}

public static class ColumnParameters
{
    public static readonly ParameterDefinition BaseLevel =
        new("Base Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition BaseOffset =
        new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopLevel =
        new("Top Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopOffset =
        new("Top Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition UnconnectedHeight =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopIs =
        new("Top Is", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Rotation =
        new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopIsAttached =
        new("Top is Attached", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition BaseIsAttached =
        new("Base is Attached", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition TopAttachmentStyle =
        new("Attachment Style At Top", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition BaseAttachmentStyle =
        new("Attachment Style At Base", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition OffsetFromAttachmentAtTop =
        new("Offset From Attachment At Top", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition OffsetFromAttachmentAtBase =
        new("Offset From Attachment At Base", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Placement =
        new("Wall Placement", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition PlacementFace =
        new("Placement Face", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition CutByWalls =
        new("Cut By Walls", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition RoomBounding =
        new("Room Bounding", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition MovesWithGrids =
        new("Moves With Grids", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Volume =
        new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
