using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How a component is put into the model, which is what decides where it can be moved to.
/// </summary>
public enum ComponentPlacement
{
    /// <summary>
    /// Stands on its level wherever it is put, held up by nothing: a table, a bed, a tree.
    /// It is moved by dragging it, and it changes storey by changing its Level.
    /// </summary>
    Freestanding,

    /// <summary>
    /// Fixed to the face of a wall and carried by it: a wall light, a radiator, a basin. It is
    /// deleted with its wall, and Pick New Host moves it onto another one.
    /// </summary>
    FaceBased,

    /// <summary>
    /// Fixed to a work plane, which is either a level or the face of a wall. It can be moved
    /// from one to the other, so it is the only kind that may or may not have a host.
    /// </summary>
    WorkPlaneBased
}

/// <summary>
/// What a component is shaped like. A loadable family in Revit carries its own geometry; here
/// a type names one of a set of forms and gives its size, which is enough to tell a table from
/// a basin in a plan and in 3D, and to schedule either of them.
/// </summary>
public enum ComponentForm
{
    Box,
    Table,
    Desk,
    Chair,
    Sofa,
    Bed,
    Counter,
    Shelving,
    Basin,
    Toilet,
    Bath,
    Tree,
    WallLight
}

/// <summary>
/// One type of a loadable component family (specification section 16): what it is shaped like,
/// how big it is, what it is made of, and how it is put into the model.
///
/// The categories are the ones Revit places components in - furniture, casework, plumbing
/// fixtures and so on - because the category is what a schedule, a filter and a visibility
/// setting all work from, not the family name.
/// </summary>
public sealed class ComponentType : ElementType
{
    public ComponentType(string name, BuiltInCategory category, ComponentForm form, double width, double depth, double height)
        : base(name)
    {
        Kind = category;
        Form = form;
        Width = width;
        Depth = depth;
        Height = height;
    }

    public override BuiltInCategory Category => Kind;

    /// <summary>Which category this family belongs to: Furniture, Plumbing Fixtures, Casework...</summary>
    public BuiltInCategory Kind { get; set; }

    public ComponentForm Form { get; set; }

    /// <summary>Across the component, left to right as it faces you. Millimetres.</summary>
    public double Width { get; set; }

    /// <summary>Front to back. Millimetres.</summary>
    public double Depth { get; set; }

    /// <summary>Floor to top, or out from the face for something fixed to a wall. Millimetres.</summary>
    public double Height { get; set; }

    public ComponentPlacement Placement { get; set; } = ComponentPlacement.Freestanding;

    /// <summary>
    /// How high above its level a face-based one sits by default - a wall light at 2 m, a
    /// basin at 800. Freestanding components stand on the floor and ignore it.
    /// </summary>
    public double DefaultElevation { get; set; }

    public Guid MaterialId { get; set; }

    /// <summary>What it reads as before any material is set: a mid timber.</summary>
    public ColourRgb Colour { get; set; } = new(0xB4, 0x9A, 0x74);

    /// <summary>Whether it can be hosted by a wall, which is what Pick New Host needs.</summary>
    public bool IsHosted => Placement is ComponentPlacement.FaceBased or ComponentPlacement.WorkPlaneBased;

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(
            ComponentTypeParameters.Category,
            () => EnumText.Humanise(Kind),
            v => { if (EnumText.TryParse<BuiltInCategory>(v, out var c)) Kind = c; },
            ComponentCategories.All.Select(category => EnumText.Humanise(category)).ToArray());

        yield return ParameterValue.BindChoice(
            ComponentTypeParameters.Form,
            () => EnumText.Humanise(Form),
            v => { if (EnumText.TryParse<ComponentForm>(v, out var f)) Form = f; },
            EnumText.Choices<ComponentForm>());

        yield return ParameterValue.BindValidated(ComponentTypeParameters.Width, () => Width, v => Positive(v, x => Width = x));
        yield return ParameterValue.BindValidated(ComponentTypeParameters.Depth, () => Depth, v => Positive(v, x => Depth = x));
        yield return ParameterValue.BindValidated(ComponentTypeParameters.Height, () => Height, v => Positive(v, x => Height = x));

        yield return ParameterValue.BindChoice(
            ComponentTypeParameters.Placement,
            () => EnumText.Humanise(Placement),
            v => { if (EnumText.TryParse<ComponentPlacement>(v, out var p)) Placement = p; },
            EnumText.Choices<ComponentPlacement>());

        yield return ParameterValue.BindValidated(
            ComponentTypeParameters.DefaultElevation,
            () => DefaultElevation,
            v => { if (v >= 0) { DefaultElevation = v; return true; } return false; });

        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }

    private static bool Positive(double value, Action<double> set)
    {
        if (value <= 0) return false;
        set(value);
        return true;
    }
}

/// <summary>The categories a component family can be placed in, as Revit lists them.</summary>
public static class ComponentCategories
{
    public static readonly IReadOnlyList<BuiltInCategory> All = new[]
    {
        BuiltInCategory.Furniture,
        BuiltInCategory.Casework,
        BuiltInCategory.PlumbingFixtures,
        BuiltInCategory.SpecialtyEquipment,
        BuiltInCategory.LightingFixtures,
        BuiltInCategory.Planting,
        BuiltInCategory.Entourage,
        BuiltInCategory.GenericModels
    };
}

/// <summary>
/// One component placed in the model (specification section 2.1): an instance of a loadable
/// family, standing on a level or fixed to the face of a wall.
///
/// It stores where it is and which way it faces, and nothing about its shape - the shape is
/// the type's, so changing the type changes every one of them at once. A hosted one keeps the
/// wall it is on, so it goes when the wall goes and follows it when the wall moves.
/// </summary>
public sealed class Component : Element, IHostedElement
{
    public override BuiltInCategory Category =>
        TypeKind ?? BuiltInCategory.Furniture;

    /// <summary>
    /// The category of the type this was placed from, kept here so the category is known
    /// without the document. Set when the component is placed or loaded.
    /// </summary>
    public BuiltInCategory? TypeKind { get; set; }

    /// <summary>Where it stands in plan: the middle of its footprint.</summary>
    public Point2D Location { get; set; }

    /// <summary>
    /// How far it has been turned, anticlockwise, in degrees. At nothing it sits square to the
    /// plan with its width across the page and its front toward the bottom of it, which is how
    /// a furniture family is drawn.
    /// </summary>
    public double Rotation { get; set; }

    /// <summary>Millimetres above its level: the underside of a freestanding one, the fixing height of a hosted one.</summary>
    public double Elevation { get; set; }

    /// <summary>
    /// The face it is fixed to: a wall, or a floor, ceiling or roof. Empty for one standing on
    /// its level, which is a work plane rather than a face.
    /// </summary>
    public Guid HostId { get; set; }

    /// <summary>
    /// Which side of its host's face it is on: the far face of a wall, or the underside of a
    /// slab rather than the top of it.
    /// </summary>
    public bool FlipFacing { get; set; }

    /// <summary>Whether a face is carrying it at the moment.</summary>
    public bool IsHosted => HostId != Guid.Empty;

    /// <summary>Its width axis in plan: left to right as it faces you.</summary>
    public Vector2D Across
    {
        get
        {
            var radians = Rotation * Math.PI / 180;
            return new Vector2D(Math.Cos(radians), Math.Sin(radians));
        }
    }

    /// <summary>The way it faces, square to its width - the front of a desk, the open side of a sofa.</summary>
    public Vector2D Facing => new(Across.Y, -Across.X);

    /// <summary>Turns it to face this way, which is how a host or a mirror decides its rotation.</summary>
    public void FaceToward(Vector2D facing)
    {
        if (facing.Length <= 1e-9) return;
        Rotation = Math.Atan2(facing.X, -facing.Y) * 180 / Math.PI;
    }

    /// <summary>
    /// Puts this component on a wall, at the point on it nearest where it is: against the face
    /// it was put on, facing into the room. Refused when the wall cannot take it.
    /// </summary>
    public bool HostOn(BimDocument document, Wall wall, Point2D at)
    {
        if (document.GetWallType(wall) is not { } type) return false;

        var (along, across) = wall.Locate(type.Structure, at);
        if (along < 0 || along > wall.Length) along = Math.Clamp(along, 0, wall.Length);

        HostId = wall.Id;
        LevelId = wall.LevelId;
        FlipFacing = across < 0;
        SitAgainst(document, wall, type, along);
        return true;
    }

    /// <summary>
    /// Puts this component on the face of a floor, ceiling or roof, where it was clicked: on
    /// top of it, or hung from its underside for a ceiling, which is where a light goes. It
    /// keeps the way it faces, because a horizontal face does not decide that.
    /// </summary>
    public bool HostOn(BimDocument document, Slab slab, Point2D at, bool underneath)
    {
        if (!slab.Contains(at)) return false;

        HostId = slab.Id;
        LevelId = slab.LevelId;
        FlipFacing = underneath;
        Location = at;
        return true;
    }

    /// <summary>Stands it against its host's face this far along, facing out of the wall.</summary>
    public void SitAgainst(BimDocument document, Wall wall, WallType type, double along)
    {
        var side = FlipFacing ? -1.0 : 1.0;
        var half = document.FindType<ComponentType>(TypeId)?.Depth / 2 ?? 0;

        Location = wall.PointAt(type.Structure, along, side * (type.Width / 2 + half));

        // Facing out of the face it is fixed to: a basin stands with its back to the wall.
        FaceToward(wall.ExteriorNormal * side);
    }

    /// <summary>Follows its host when the wall has moved, keeping where it sits along it.</summary>
    public void FollowHost(BimDocument document)
    {
        if (!IsHosted) return;
        if (document.Walls.FirstOrDefault(w => w.Id == HostId) is not { } wall) return;
        if (document.GetWallType(wall) is not { } type) return;

        var (along, _) = wall.Locate(type.Structure, Location);
        SitAgainst(document, wall, type, Math.Clamp(along, 0, wall.Length));
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<ComponentType>(TypeId);

        yield return ParameterValue.ReadOnly(
            ComponentParameters.Family,
            () => type is null ? "<none>" : $"{EnumText.Humanise(type.Kind)} : {type.Name}");

        // Which storey it belongs to. Changing it is how a component is moved to another
        // level, floor or surface - the whole of that job for a freestanding one.
        yield return ParameterValue.BindChoice(
            ComponentParameters.Level,
            () => document.Levels.FirstOrDefault(l => l.Id == LevelId)?.Name ?? "<none>",
            v =>
            {
                if (document.Levels.FirstOrDefault(l => l.Name == v) is { } level && !IsHosted) LevelId = level.Id;
            },
            document.Levels.Select(l => l.Name).ToArray());

        yield return ParameterValue.BindValidated(
            ComponentParameters.Elevation,
            () => Elevation,
            v => { Elevation = v; return true; });

        yield return ParameterValue.BindValidated(
            ComponentParameters.Rotation,
            () => Rotation,
            v => { Rotation = ((v % 360) + 360) % 360; return true; });

        yield return ParameterValue.ReadOnly(
            ComponentParameters.Host,
            () => ComponentHosting.Describe(document, this, type));

        // Only something fixed to a face has a side to be turned to.
        if (type?.IsHosted == true && IsHosted)
            yield return ParameterValue.Bind(
                ComponentParameters.FlipFacing,
                () => FlipFacing,
                v =>
                {
                    FlipFacing = v;
                    FollowHost(document);
                });

        yield return ParameterValue.ReadOnly(ComponentParameters.Width, () => type?.Width ?? 0);
        yield return ParameterValue.ReadOnly(ComponentParameters.Depth, () => type?.Depth ?? 0);
        yield return ParameterValue.ReadOnly(ComponentParameters.Height, () => type?.Height ?? 0);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class ComponentTypeParameters
{
    public static readonly ParameterDefinition Category =
        new("Category", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Form =
        new("Form", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Depth =
        new("Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Placement =
        new("Placement", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition DefaultElevation =
        new("Default Elevation", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Constraints);
}

public static class ComponentParameters
{
    public static readonly ParameterDefinition Family =
        new("Family and Type", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Level =
        new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Elevation =
        new("Elevation", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Rotation =
        new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Host =
        new("Host", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition FlipFacing =
        new("Flip Facing", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Depth =
        new("Depth", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
