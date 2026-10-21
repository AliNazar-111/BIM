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
    WallLight,

    // The kitchen: a fridge or fridge-freezer, a cooker - an oven under a hob - a dishwasher and
    // a washing machine under the worktop, and a sink set into a unit's worktop.
    Fridge,
    Cooker,
    Dishwasher,
    WashingMachine,
    Sink,

    // The bedroom and the living room: a wardrobe with hinged doors, an armchair, a coffee table
    // with a shelf under it, and a television unit with the television on it.
    Wardrobe,
    Armchair,
    CoffeeTable,
    TelevisionUnit,

    // The bathroom: a shower tray, with or without a glass enclosure round it.
    Shower,

    // The services: a panel radiator on its valves, a ceiling light - flush, or a pendant on
    // its flex - and a socket and a light switch on the wall.
    Radiator,
    CeilingLight,
    Socket,
    Switch,

    // Outside: a car, a person, a conifer and a shrub - and the broadleaf tree above.
    Car,
    Person,
    Conifer,
    Shrub,

    // A family of your own: drawn in the family editor from simple solids, or loaded from a 3D model.
    Custom,

    // The bedroom and the dining room again: a chest of drawers - a bedside table, a chest, a
    // sideboard - a corner sofa turning an L, and a round table on its pedestal.
    Drawers,
    CornerSofa,
    RoundTable,

    // The children's room: a bunk bed, its upper bed guarded and a ladder up to it, and a cot.
    BunkBed,
    Cot,

    // The kitchen again: a wall unit with its doors, a tall larder unit, a tall housing with an
    // oven and a microwave at eye level, a chimney cooker hood, and a tumble dryer under the worktop.
    WallUnit,
    TallUnit,
    OvenHousing,
    CookerHood,
    TumbleDryer,

    // The bathroom again: a WC hung on the wall, its cistern hidden in it behind a flush plate.
    WallHungToilet,

    // The services again: a heated towel rail, an extract fan, a smoke, a heat and a carbon
    // monoxide alarm on the ceiling, a shaver socket, a consumer unit and a room thermostat.
    TowelRail,
    ExtractFan,
    SmokeAlarm,
    HeatAlarm,
    CarbonMonoxideAlarm,
    ShaverSocket,
    ConsumerUnit,
    Thermostat,

    // Outside again: a parking bay marked out - and an accessible one, with its zones to get in
    // and out - a person in a wheelchair, and a turning circle to try a room with.
    ParkingBay,
    AccessibleParkingBay,
    WheelchairUser,
    TurningCircle,

    // A gas boiler on the wall: what a carbon monoxide alarm is put up by.
    Boiler
}

/// <summary>
/// How much water a tree draws from the ground, as NHBC Standards' Table 3 sorts the species - which
/// says how far round it a shrinkable clay is dried out. One not known is taken as high.
/// </summary>
public enum TreeWaterDemand
{
    Unknown,
    Low,
    Moderate,
    High
}

/// <summary>
/// Where a shower has glass round its tray: none - a tray on its own, or a wet room's - a sliding
/// door across the front of an alcove, the front and one side in a corner, or a fixed panel
/// part way along the front for a walk-in.
/// </summary>
public enum ShowerEnclosure
{
    None,
    Alcove,
    Corner,
    WalkIn
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

        AgainstWall = BacksOntoWallByDefault(form);
    }

    /// <summary>Whether a shape stands against a wall in a room: a bed's head, a sofa's back, a unit, a WC, a bath.</summary>
    public static bool BacksOntoWallByDefault(ComponentForm form) =>
        form is ComponentForm.Counter or ComponentForm.Shelving or ComponentForm.Basin or ComponentForm.Toilet
            or ComponentForm.Bath or ComponentForm.Bed or ComponentForm.Sofa or ComponentForm.Desk
            or ComponentForm.Fridge or ComponentForm.Cooker or ComponentForm.Dishwasher or ComponentForm.WashingMachine or ComponentForm.Sink
            or ComponentForm.Wardrobe or ComponentForm.TelevisionUnit or ComponentForm.Shower
            or ComponentForm.Drawers or ComponentForm.CornerSofa or ComponentForm.BunkBed
            or ComponentForm.WallUnit or ComponentForm.TallUnit or ComponentForm.OvenHousing or ComponentForm.CookerHood or ComponentForm.TumbleDryer
            or ComponentForm.WallHungToilet or ComponentForm.TowelRail or ComponentForm.Boiler;

    /// <summary>
    /// Whether one put down near a wall backs onto it and faces the room: a bed, a wardrobe, a
    /// WC, a kitchen unit. Off for a table or a chair, which stand where they are put - and for a
    /// kitchen island, made from a unit type with it turned off.
    /// </summary>
    public bool AgainstWall { get; set; }

    public override BuiltInCategory Category => Kind;

    /// <summary>Which category this family belongs to: Furniture, Plumbing Fixtures, Casework...</summary>
    public BuiltInCategory Kind { get; set; }

    public ComponentForm Form { get; set; }

    /// <summary>For a shower, where its glass is: none, a door across an alcove, a corner, a walk-in panel.</summary>
    public ShowerEnclosure Enclosure { get; set; }

    /// <summary>For a family of your own, the solids it is drawn with, in its box - so it stretches with its size.</summary>
    public List<ComponentSolid> Solids { get; } = new();

    /// <summary>For a family of your own, the 3D model loaded into it, in its box likewise; or none.</summary>
    public ComponentMesh? Mesh { get; set; }

    /// <summary>
    /// For an extract fan or a cooker hood, how much air it draws out, l/s: what Approved
    /// Document F's rates for a kitchen, a bathroom or a WC are met with.
    /// </summary>
    public double ExtractRate { get; set; }

    /// <summary>For a light, its light source's efficacy, lumens per circuit-watt: 75 at least, Approved Document L asks (6.58).</summary>
    public double Efficacy { get; set; }

    /// <summary>For a tree or a shrub, how much water it draws - its height being taken as its mature height.</summary>
    public TreeWaterDemand WaterDemand { get; set; }

    /// <summary>For a tree, its stem's diameter 1.5 m up, mm - what its root protection area is measured from; none where not known.</summary>
    public double StemDiameter { get; set; }

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

    /// <summary>A new type starting as this one is - to be made another size or material without touching the pieces of this one.</summary>
    public ComponentType Duplicate(string name) => WithCommonOf(new ComponentType(name, Kind, Form, Width, Depth, Height)
    {
        Placement = Placement, DefaultElevation = DefaultElevation, MaterialId = MaterialId, Colour = Colour, AgainstWall = AgainstWall,
        Enclosure = Enclosure, Mesh = Mesh, ExtractRate = ExtractRate, Efficacy = Efficacy, WaterDemand = WaterDemand, StemDiameter = StemDiameter
    }.WithSolids(Solids));

    /// <summary>This type, drawn with these solids as well as any it has.</summary>
    public ComponentType WithSolids(IEnumerable<ComponentSolid> solids)
    {
        Solids.AddRange(solids.Select(solid => solid.Copy()));
        return this;
    }

    /// <summary>What a component type is made of when it has no material: its own colour.</summary>
    public const string ByColour = "<by its colour>";

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

        yield return ParameterValue.Bind(ComponentTypeParameters.AgainstWall, () => AgainstWall, v => AgainstWall = v);

        // What a family of your own is drawn with - changed in the family editor.
        if (Form == ComponentForm.Custom)
            yield return ParameterValue.ReadOnly(ComponentTypeParameters.DrawnWith, () =>
                string.Join(" and ", new[]
                {
                    Solids.Count switch { 0 => null, 1 => "1 solid", var n => $"{n} solids" },
                    Mesh is null ? null : $"{Mesh.Source} ({Mesh.TriangleCount:N0} triangles)"
                }.Where(said => said is not null)) is { Length: > 0 } said ? said : "Nothing yet");

        if (Form is ComponentForm.ExtractFan or ComponentForm.CookerHood)
            yield return ParameterValue.BindValidated(ComponentTypeParameters.ExtractRate, () => ExtractRate, v => { if (v < 0) return false; ExtractRate = v; return true; });

        if (Kind == BuiltInCategory.LightingFixtures)
            yield return ParameterValue.BindValidated(ComponentTypeParameters.Efficacy, () => Efficacy, v => { if (v < 0) return false; Efficacy = v; return true; });

        if (Form is ComponentForm.Tree or ComponentForm.Conifer or ComponentForm.Shrub)
        {
            yield return ParameterValue.BindChoice(
                ComponentTypeParameters.WaterDemand,
                () => EnumText.Humanise(WaterDemand),
                v => { if (EnumText.TryParse<TreeWaterDemand>(v, out var demand)) WaterDemand = demand; },
                EnumText.Choices<TreeWaterDemand>());
            yield return ParameterValue.BindValidated(ComponentTypeParameters.StemDiameter, () => StemDiameter, v => { if (v < 0) return false; StemDiameter = v; return true; });
        }

        if (Form == ComponentForm.Shower)
            yield return ParameterValue.BindChoice(
                ComponentTypeParameters.Enclosure,
                () => EnumText.Humanise(Enclosure),
                v => { if (EnumText.TryParse<ShowerEnclosure>(v, out var e)) Enclosure = e; },
                EnumText.Choices<ShowerEnclosure>());

        // What it is made of: one of the project's materials - its colour in 3D - or its own colour.
        if (document is not null)
            yield return ParameterValue.BindChoice(
                ComponentTypeParameters.Material,
                () => document.FindMaterial(MaterialId)?.Name ?? ByColour,
                v => MaterialId = document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty,
                new[] { ByColour }.Concat(document.Materials.Select(material => material.Name).OrderBy(name => name)).ToArray());

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
        BuiltInCategory.MechanicalEquipment,
        BuiltInCategory.LightingFixtures,
        BuiltInCategory.ElectricalFixtures,
        BuiltInCategory.LightingDevices,
        BuiltInCategory.FireAlarmDevices,
        BuiltInCategory.ElectricalEquipment,
        BuiltInCategory.Planting,
        BuiltInCategory.Parking,
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

    /// <summary>
    /// Millimetres above its level - or above the top of the floor it stands on, where it stands
    /// on one: the underside of a freestanding one, the fixing height of a hosted one.
    /// </summary>
    public double Elevation { get; set; }

    /// <summary>
    /// The floor or flat roof it stands on, where its storey's floor is built up above the level:
    /// its elevation is then measured from that slab's top, wherever the top is - so it follows
    /// the slab however thick it is made, or wherever it is moved. Null for one measured from its level.
    /// </summary>
    public Guid? StandsOn { get; set; }

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

    /// <summary>
    /// Whether it is the mirror image of its family, left for right: a corner shower with its
    /// glass on the other side, a sink with its bowl on the right. Mirroring one turns it over.
    /// </summary>
    public bool FlipHand { get; set; }

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
                if (document.Levels.FirstOrDefault(l => l.Name == v) is not { } level || IsHosted || level.Id == LevelId) return;

                // On another storey it stands on that storey's floor, if that is built up.
                LevelId = level.Id;
                StandsOn = FloorSurfaces.FloorUnder(document, level.Id, Location)?.Id;
            },
            document.Levels.Select(l => l.Name).ToArray());

        yield return ParameterValue.BindValidated(
            ComponentParameters.Elevation,
            () => Elevation,
            v => { Elevation = v; return true; });

        // The room it is in - what a schedule lists it under.
        yield return ParameterValue.ReadOnly(ComponentParameters.Room,
            () => RoomsOf.Containing(document, LevelId, Location) is { } room ? RoomsOf.Label(room) : string.Empty);

        // A person: their eyes, what they see from where they stand, and their view out.
        if (Views.Walker.IsPerson(type))
        {
            var us = document.ProjectInformation.ClearanceStandard.IsUs();
            yield return ParameterValue.ReadOnly(ComponentParameters.EyeHeight, () => Views.Walker.Of(type!, us).EyeHeight);
            yield return ParameterValue.ReadOnly(ComponentParameters.Sees, () =>
            {
                var (_, area, rooms, openings) = Views.Sight.Of(document, this);
                var named = openings.Select(opening => string.IsNullOrWhiteSpace(opening.Mark) ? opening.Category == BuiltInCategory.Doors ? "a door" : "a window" : opening.Mark).ToList();
                return $"{area:0} m² in sight{(rooms.Count > 0 ? $"; {string.Join(", ", rooms)}" : string.Empty)}{(named.Count > 0 ? $"; {named.Count} door{(named.Count == 1 ? "" : "s")} and windows: {string.Join(", ", named.Take(12))}" : string.Empty)}";
            });
            yield return ParameterValue.ReadOnly(ComponentParameters.ViewOut, () =>
            {
                var meshes = Views.ModelMeshBuilder.Build(document, element => element.Id != Id);
                return Views.ViewOut.From(document, LevelId, Location, Views.Walker.Of(type!, us).EyeHeight, meshes, new HashSet<Guid> { Id }).Said;
            });
        }

        // What its elevation is measured from: the top of the floor it stands on, or its level.
        yield return ParameterValue.ReadOnly(ComponentParameters.StandsOn, () =>
            StandsOn is { } floor && document.Elements.FirstOrDefault(element => element.Id == floor) is { } slab
                ? $"Top of {document.FindType<ElementType>(slab.TypeId)?.Name ?? "the floor"}"
                : "Its level");

        yield return ParameterValue.BindValidated(
            ComponentParameters.Rotation,
            () => Rotation,
            v => { Rotation = ((v % 360) + 360) % 360; return true; });

        yield return ParameterValue.Bind(ComponentParameters.FlipHand, () => FlipHand, v => FlipHand = v);

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

    public static readonly ParameterDefinition Material =
        new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition AgainstWall =
        new("Backs Onto a Wall", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Enclosure =
        new("Enclosure", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition DrawnWith =
        new("Drawn With", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition DefaultElevation =
        new("Default Elevation", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Constraints);

    public static readonly ParameterDefinition ExtractRate =
        new("Extract Rate (l/s)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Efficacy =
        new("Efficacy (lm/W)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition WaterDemand =
        new("Water Demand", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition StemDiameter =
        new("Stem Diameter", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
}

public static class ComponentParameters
{
    /// <summary>A person's eyes, over what they stand on.</summary>
    public static readonly ParameterDefinition EyeHeight =
        new("Eye Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    /// <summary>What a person sees from where they stand: the area in sight, the rooms, the doors and windows.</summary>
    public static readonly ParameterDefinition Sees =
        new("What They See", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);

    /// <summary>A person's view out from where they stand, as BS EN 17037 grades it.</summary>
    public static readonly ParameterDefinition ViewOut =
        new("View Out (BS EN 17037)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);

    public static readonly ParameterDefinition Family =
        new("Family and Type", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Level =
        new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Elevation =
        new("Elevation", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Room =
        new("Room", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition StandsOn =
        new("Elevation From", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Rotation =
        new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Host =
        new("Host", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition FlipFacing =
        new("Flip Facing", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition FlipHand =
        new("Flip Hand", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Depth =
        new("Depth", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
