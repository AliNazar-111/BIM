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
    Boiler,

    // The services' equipment: a hot water cylinder, the stop valve on the incoming water main,
    // and the gas meter - what the systems run from.
    HotWaterCylinder,
    StopValve,
    GasMeter,

    // The drainage: where the foul drain meets the sewer, where the rainwater goes - the surface
    // water sewer or a soakaway - the access points along the drains, and an air admittance valve
    // closing a stack inside the building.
    FoulOutfall,
    SurfaceWaterOutfall,
    Soakaway,
    DrainAccess,
    AirAdmittanceValve,

    // The heating again: an air source heat pump's unit outside, and an underfloor heating loop -
    // its pipes laid under the whole floor of the room it is put in.
    HeatPump,
    UnderfloorHeating,

    // The ventilation: a heat recovery unit - mechanical ventilation with heat recovery - the valves
    // in the ceilings it supplies air through and extracts it by, and a terminal on the outside wall
    // its air comes in and goes out at.
    HeatRecoveryUnit,
    SupplyTerminal,
    ExtractTerminal,
    OutsideTerminal,

    // The ventilation again: a central extract unit - continuous mechanical extract - a background
    // ventilator through the wall, and a smoke detector in a duct, sampling its air for the fire and
    // smoke dampers it closes.
    ExtractUnit,
    BackgroundVentilator,
    DuctSmokeDetector,

    // A gas fire against the wall on its hearth - a fixed gas appliance, as a boiler is, that a
    // carbon monoxide alarm is put up by; in the US a gas fireplace.
    GasFire,

    // Fire sprinklers: a residential pendent sprinkler hanging from the ceiling, and the valve set where
    // the sprinklers' supply comes in - its stop valve, check valve, flow switch and test and drain valve.
    Sprinkler,
    SprinklerValve
}

/// <summary>
/// The four kinds of access point to a drain, as Approved Document H (2.48) has them: a rodding
/// eye - a capped extension of the pipe; an access fitting - a small chamber on the pipe with no
/// open channel; an inspection chamber - working space at ground level; a manhole - working space
/// down at the drain.
/// </summary>
public enum DrainAccessKind
{
    RoddingEye,
    AccessFitting,
    InspectionChamber,
    Manhole
}

/// <summary>
/// Where a boiler's hot water comes from: heated as it is drawn, by a combi - or stored, in a
/// cylinder the boiler heats through its coil.
/// </summary>
public enum BoilerHotWater
{
    Combi,
    Cylinder
}

/// <summary>
/// How a light spreads its light, for the share of it that reaches the working plane: a downlight's
/// down in a cone, a diffuse fitting's - a pendant, a flush ceiling light, a batten - all round.
/// </summary>
public enum LightDistribution
{
    Diffuse,
    Downlight
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
            or ComponentForm.WallHungToilet or ComponentForm.TowelRail or ComponentForm.Boiler or ComponentForm.HeatPump or ComponentForm.GasFire;

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

    /// <summary>For a boiler, whether it heats the hot water as it is drawn - a combi - or a cylinder's.</summary>
    public BoilerHotWater HotWater { get; set; }

    /// <summary>
    /// For a basin's or a sink's taps or a shower, its full flow, litres a minute - what Approved Document G's
    /// water efficiency calculator counts; for a sprinkler, the least flow its listing asks at its coverage.
    /// </summary>
    public double FlowRate { get; set; }

    /// <summary>For a sprinkler, the side of the square its listing lets it cover, mm: 4.9 m (16 ft) for a residential pendent's usual one.</summary>
    public double Coverage { get; set; }

    /// <summary>For a sprinkler, its K-factor, l/min a bar to the half: what it discharges goes as the square root of the pressure at it.</summary>
    public double KFactor { get; set; }

    /// <summary>For a sprinkler, the temperature it opens at, °C: 68 ordinary, 79 intermediate.</summary>
    public double Temperature { get; set; }

    /// <summary>
    /// For a sprinkler valve set, the pressure its supply gives it at the sprinklers' design flow, bar - a pump
    /// set's duty, or the main's - none where not known; a US project's water pressure is taken then.
    /// </summary>
    public double SupplyPressure { get; set; }

    /// <summary>For a WC, its flush, litres - its full flush where it has two.</summary>
    public double FlushVolume { get; set; }

    /// <summary>For a dual-flush WC, its part flush, litres; none for a single flush.</summary>
    public double PartFlushVolume { get; set; }

    /// <summary>For a bath, how much it holds to its overflow, litres; for a hot water cylinder, how much it holds.</summary>
    public double Capacity { get; set; }

    /// <summary>
    /// For a boiler or a heat pump, its seasonal efficiency heating the home, %: a boiler's winter seasonal
    /// efficiency, a heat pump's space heating efficiency - its SCOP as a percentage - from the Product
    /// Characteristics Database or its maker. None where not known: SAP's default for its kind is taken.
    /// </summary>
    public double SpaceEfficiency { get; set; }

    /// <summary>For a boiler or a heat pump, its efficiency heating the hot water, %: a boiler's summer seasonal efficiency, a heat pump's water heating efficiency; none where not known.</summary>
    public double WaterEfficiency { get; set; }

    /// <summary>For a hot water cylinder, its maker's declared heat loss, kWh a day; none where not known, and its loss is worked out from its volume and 50 mm of factory insulation.</summary>
    public double StandingLoss { get; set; }

    /// <summary>
    /// For a heat recovery unit, a central extract unit or an extract fan running continuously, its
    /// specific fan power in use, W/(l/s) - the PCDB's, with its in-use factor; none where not known.
    /// </summary>
    public double SpecificFanPower { get; set; }

    /// <summary>For a dishwasher, the water it uses a place setting; for a washing machine, a kilo of dry load - litres, from its energy label.</summary>
    public double WaterPerUse { get; set; }

    /// <summary>For an access point on a drain, which of the four kinds it is.</summary>
    public DrainAccessKind Access { get; set; }

    /// <summary>
    /// How much heat it gives, W: a radiator's or a towel rail's at a mean water temperature 50 K
    /// over the room's - 75/65/20 °C, as BS EN 442 rates them; a boiler's or a heat pump's at its
    /// design conditions; a cylinder's coil's. None where not known.
    /// </summary>
    public double HeatOutput { get; set; }

    /// <summary>How much a heat pump cools, W - its total capacity at the summer design conditions, from its maker's data. None where it only heats.</summary>
    public double CoolingOutput { get; set; }

    /// <summary>
    /// For an appliance that burns gas, the most heat it takes in as gas, W gross - its maker's maximum
    /// heat input, a combi boiler's on hot water: what its gas pipes are sized for. A cooker, a water
    /// heater or a dryer with one burns gas rather than running on electricity; a boiler or a gas fire
    /// with none is taken at an estimate for its kind.
    /// </summary>
    public double GasInput { get; set; }

    /// <summary>Whether it burns gas: a boiler or a gas fire always, a cooker, a water heater or a dryer with a gas input.</summary>
    public bool BurnsGas => Form is ComponentForm.Boiler or ComponentForm.GasFire
                            || GasInput > 0 && Form is ComponentForm.Cooker or ComponentForm.HotWaterCylinder or ComponentForm.TumbleDryer;

    /// <summary>
    /// For a radiator or a towel rail, how its output falls with the water's temperature: its
    /// output goes as the difference over the room's to this power - its n, from its BS EN 442 test.
    /// </summary>
    public double HeatExponent { get; set; } = 1.3;

    /// <summary>
    /// For underfloor heating, what its floor gives a square metre for each kelvin its mean water
    /// temperature is over the room's, W/m²K - its maker's, at its pipe spacing, screed and floor covering.
    /// </summary>
    public double FloorOutput { get; set; }

    /// <summary>For underfloor heating, the most its floor gives in an occupied area, W/m²: its maker's, at BS EN 1264's limit to the floor's warmth.</summary>
    public double FloorOutputLimit { get; set; }

    /// <summary>For underfloor heating, how far apart its pipes are laid, mm - what says how much pipe a floor takes.</summary>
    public double PipeSpacing { get; set; }

    /// <summary>For a heat recovery unit, the most air it moves, l/s, against its available pressure.</summary>
    public double Airflow { get; set; }

    /// <summary>For a heat recovery unit, the pressure it has for its ducts and terminals at that airflow, Pa.</summary>
    public double AvailablePressure { get; set; }

    /// <summary>For a heat recovery unit, the share of the heat in the air it extracts that it gives to the air it supplies: its efficiency, as tested.</summary>
    public double HeatRecovery { get; set; }

    /// <summary>For a valve or a terminal, the pressure it loses, Pa, at its rated airflow (l/s) - from its maker's data; it goes as the square of the air through it.</summary>
    public double PressureLoss { get; set; }

    public double RatedAirflow { get; set; }

    /// <summary>For an extract fan running continuously - decentralised mechanical extract - its continuous rate, l/s; its extract rate is then its high rate. None for an intermittent fan.</summary>
    public double ContinuousRate { get; set; }

    /// <summary>For a background ventilator, its equivalent area, mm² (BS EN 13141-1) - what Approved Document F sizes them by.</summary>
    public double VentArea { get; set; }

    /// <summary>
    /// What it draws from its circuit, W - its maker's rating: a light's circuit-watts, a cooker's, an
    /// electric shower's (a shower with none is fed hot water instead). None where not known: a
    /// typical figure for its kind is taken.
    /// </summary>
    public double Load { get; set; }

    /// <summary>For a light, the light it gives, lumens - the fitting's own, from its maker's data or its photometric file.</summary>
    public double Lumens { get; set; }

    /// <summary>For a light, how it spreads its light.</summary>
    public LightDistribution Distribution { get; set; }

    /// <summary>For a light, its maker's photometric data - its light in each direction - read from an IES or EULUMDAT file; none where not loaded.</summary>
    public Mep.Photometry? Photometry { get; set; }

    /// <summary>For a consumer unit, the ways it has for circuits' breakers.</summary>
    public int Ways { get; set; }

    /// <summary>For a consumer unit, the supply it is on, A: the main fuse in the UK, the service's rating in the US.</summary>
    public double SupplyRating { get; set; }

    /// <summary>For a consumer unit, the earth fault loop impedance external to the installation, Ω - Ze: 0.35 for TN-C-S, 0.8 for TN-S at most, as the electricity networks declare.</summary>
    public double ExternalImpedance { get; set; }

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
        Enclosure = Enclosure, Mesh = Mesh, ExtractRate = ExtractRate, Efficacy = Efficacy, WaterDemand = WaterDemand, StemDiameter = StemDiameter,
        HotWater = HotWater, FlowRate = FlowRate, FlushVolume = FlushVolume, PartFlushVolume = PartFlushVolume, Capacity = Capacity, WaterPerUse = WaterPerUse,
        SpaceEfficiency = SpaceEfficiency, WaterEfficiency = WaterEfficiency, StandingLoss = StandingLoss, SpecificFanPower = SpecificFanPower,
        Access = Access, HeatOutput = HeatOutput, CoolingOutput = CoolingOutput, GasInput = GasInput, Coverage = Coverage, KFactor = KFactor, Temperature = Temperature, SupplyPressure = SupplyPressure, HeatExponent = HeatExponent, FloorOutput = FloorOutput, FloorOutputLimit = FloorOutputLimit, PipeSpacing = PipeSpacing,
        Airflow = Airflow, AvailablePressure = AvailablePressure, HeatRecovery = HeatRecovery, PressureLoss = PressureLoss, RatedAirflow = RatedAirflow,
        ContinuousRate = ContinuousRate, VentArea = VentArea, Load = Load, Lumens = Lumens, Distribution = Distribution, Photometry = Photometry,
        Ways = Ways, SupplyRating = SupplyRating, ExternalImpedance = ExternalImpedance
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
            yield return ParameterValue.BindScaled(ComponentTypeParameters.ExtractRate, ComponentTypeParameters.ExtractRateCfm, ComponentTypeParameters.CfmPerLitreSecond, 0,
                () => ExtractRate, v => { if (v < 0) return false; ExtractRate = v; return true; });

        if (Kind == BuiltInCategory.LightingFixtures)
        {
            yield return ParameterValue.BindValidated(ComponentTypeParameters.Efficacy, () => Efficacy, v => { if (v < 0) return false; Efficacy = v; return true; });
            yield return ParameterValue.BindValidated(ComponentTypeParameters.Lumens, () => Lumens, v => { if (v < 0) return false; Lumens = v; return true; });
            yield return ParameterValue.BindChoice(
                ComponentTypeParameters.Distribution,
                () => EnumText.Humanise(Distribution),
                v => { if (EnumText.TryParse<LightDistribution>(v, out var spread)) Distribution = spread; },
                EnumText.Choices<LightDistribution>());
            yield return ParameterValue.ReadOnly(ComponentTypeParameters.PhotometricData, () => Photometry?.Describe() ?? "None loaded");
        }

        // What it draws from its circuit; a consumer unit's ways, supply and earth loop.
        if (Mep.Circuits.TakesLoad(this))
            yield return ParameterValue.BindValidated(Form == ComponentForm.Shower ? ComponentTypeParameters.ShowerLoad : ComponentTypeParameters.Load,
                () => Load, v => { if (v < 0) return false; Load = v; return true; });
        if (Form == ComponentForm.ConsumerUnit)
        {
            yield return ParameterValue.BindValidated(ComponentTypeParameters.Ways, () => (double)Ways, v => { if (v is < 1 or > 100) return false; Ways = (int)Math.Round(v); return true; });
            yield return ParameterValue.BindValidated(ComponentTypeParameters.SupplyRating, () => SupplyRating, v => { if (v < 0) return false; SupplyRating = v; return true; });
            yield return ParameterValue.BindValidated(ComponentTypeParameters.ExternalImpedance, () => ExternalImpedance, v => { if (v < 0) return false; ExternalImpedance = v; return true; });
        }

        if (Form is ComponentForm.Tree or ComponentForm.Conifer or ComponentForm.Shrub)
        {
            yield return ParameterValue.BindChoice(
                ComponentTypeParameters.WaterDemand,
                () => EnumText.Humanise(WaterDemand),
                v => { if (EnumText.TryParse<TreeWaterDemand>(v, out var demand)) WaterDemand = demand; },
                EnumText.Choices<TreeWaterDemand>());
            yield return ParameterValue.BindValidated(ComponentTypeParameters.StemDiameter, () => StemDiameter, v => { if (v < 0) return false; StemDiameter = v; return true; });
        }

        if (Form == ComponentForm.Boiler)
            yield return ParameterValue.BindChoice(
                ComponentTypeParameters.HotWater,
                () => EnumText.Humanise(HotWater),
                v => { if (EnumText.TryParse<BoilerHotWater>(v, out var hot)) HotWater = hot; },
                EnumText.Choices<BoilerHotWater>());

        // What Approved Document G's water efficiency calculator counts of it.
        bool NotNegative(double v, Action<double> set) { if (v < 0) return false; set(v); return true; }
        if (Form is ComponentForm.Basin or ComponentForm.Sink or ComponentForm.Shower)
            yield return ParameterValue.BindScaled(ComponentTypeParameters.FlowRate, ComponentTypeParameters.FlowRateGpm, ComponentTypeParameters.GallonsPerLitre, 2,
                () => FlowRate, v => NotNegative(v, x => FlowRate = x));
        if (Form is ComponentForm.Toilet or ComponentForm.WallHungToilet)
        {
            yield return ParameterValue.BindScaled(ComponentTypeParameters.FlushVolume, ComponentTypeParameters.FlushVolumeGallons, ComponentTypeParameters.GallonsPerLitre, 2,
                () => FlushVolume, v => NotNegative(v, x => FlushVolume = x));
            yield return ParameterValue.BindScaled(ComponentTypeParameters.PartFlushVolume, ComponentTypeParameters.PartFlushVolumeGallons, ComponentTypeParameters.GallonsPerLitre, 2,
                () => PartFlushVolume, v => NotNegative(v, x => PartFlushVolume = x));
        }
        if (Form == ComponentForm.Bath)
            yield return ParameterValue.BindScaled(ComponentTypeParameters.Capacity, ComponentTypeParameters.CapacityGallons, ComponentTypeParameters.GallonsPerLitre, 1,
                () => Capacity, v => NotNegative(v, x => Capacity = x));
        if (Form is ComponentForm.Dishwasher or ComponentForm.WashingMachine)
            yield return ParameterValue.BindValidated(Form == ComponentForm.Dishwasher ? ComponentTypeParameters.DishwasherWater : ComponentTypeParameters.WasherWater,
                () => WaterPerUse, v => NotNegative(v, x => WaterPerUse = x));

        // What it heats with: a radiator's rated output and its exponent, a floor's output, a heat source's.
        if (Form is ComponentForm.Radiator or ComponentForm.TowelRail)
        {
            yield return ParameterValue.BindScaled(ComponentTypeParameters.RatedOutput, ComponentTypeParameters.RatedOutputBtu, Units.BtuPerHourPerWatt, 0,
                () => HeatOutput, v => NotNegative(v, x => HeatOutput = x));
            yield return ParameterValue.BindValidated(ComponentTypeParameters.HeatExponent, () => HeatExponent, v => { if (v is < 1 or > 2) return false; HeatExponent = v; return true; });
        }
        if (Form == ComponentForm.UnderfloorHeating)
        {
            yield return ParameterValue.BindScaled(ComponentTypeParameters.FloorOutput, ComponentTypeParameters.FloorOutputIp, Units.UFactorPerUValue, 3,
                () => FloorOutput, v => NotNegative(v, x => FloorOutput = x));
            yield return ParameterValue.BindScaled(ComponentTypeParameters.FloorOutputLimit, ComponentTypeParameters.FloorOutputLimitIp, Units.BtuPerHourPerWatt / 10.76391, 1,
                () => FloorOutputLimit, v => NotNegative(v, x => FloorOutputLimit = x));
            yield return ParameterValue.BindValidated(ComponentTypeParameters.PipeSpacing, () => PipeSpacing, v => NotNegative(v, x => PipeSpacing = x));
        }
        if (Form is ComponentForm.Boiler or ComponentForm.HeatPump)
        {
            yield return ParameterValue.BindScaled(ComponentTypeParameters.HeatOutput, ComponentTypeParameters.HeatOutputBtu, Units.BtuPerHourPerWatt, 0,
                () => HeatOutput, v => NotNegative(v, x => HeatOutput = x));

            // What the energy rating takes of it: its seasonal efficiencies.
            yield return ParameterValue.BindValidated(ComponentTypeParameters.SpaceEfficiency, () => SpaceEfficiency, v => { if (v is < 0 or > 1000) return false; SpaceEfficiency = v; return true; });
            yield return ParameterValue.BindValidated(ComponentTypeParameters.WaterEfficiency, () => WaterEfficiency, v => { if (v is < 0 or > 1000) return false; WaterEfficiency = v; return true; });
        }
        // What it burns: the gas its pipes are sized for, in kW gross - in the US, Btu/h.
        if (Form is ComponentForm.Boiler or ComponentForm.GasFire or ComponentForm.Cooker or ComponentForm.HotWaterCylinder or ComponentForm.TumbleDryer)
            yield return ParameterValue.BindScaled(ComponentTypeParameters.GasInput, ComponentTypeParameters.GasInputBtu, Units.BtuPerHourPerWatt * 1000, 0,
                () => GasInput / 1000, v => NotNegative(v, x => GasInput = x * 1000));

        // A sprinkler's listing: what it covers, the flow it asks there, its K-factor and the temperature it opens at;
        // a valve set's supply.
        if (Form == ComponentForm.Sprinkler)
        {
            yield return ParameterValue.BindValidated(ComponentTypeParameters.Coverage, () => Coverage, v => NotNegative(v, x => Coverage = x));
            yield return ParameterValue.BindScaled(ComponentTypeParameters.ListedFlow, ComponentTypeParameters.ListedFlowGpm, ComponentTypeParameters.GallonsPerLitre, 1,
                () => FlowRate, v => NotNegative(v, x => FlowRate = x));
            yield return ParameterValue.BindScaled(ComponentTypeParameters.KFactor, ComponentTypeParameters.KFactorUs, 1 / Mep.Sprinklers.KPerUsK, 2,
                () => KFactor, v => NotNegative(v, x => KFactor = x));
            yield return Units.Imperial
                ? ParameterValue.BindValidated<double>(ComponentTypeParameters.TemperatureF, () => Math.Round(Temperature * 1.8 + 32), v => NotNegative(v, x => Temperature = (x - 32) / 1.8))
                : ParameterValue.BindValidated(ComponentTypeParameters.Temperature, () => Temperature, v => NotNegative(v, x => Temperature = x));
        }
        if (Form == ComponentForm.SprinklerValve)
            yield return ParameterValue.BindScaled(ComponentTypeParameters.SupplyPressure, ComponentTypeParameters.SupplyPressurePsi, Mep.Sprinklers.PsiPerBar, 1,
                () => SupplyPressure, v => NotNegative(v, x => SupplyPressure = x));
        if (Form == ComponentForm.HeatPump)
            yield return ParameterValue.BindScaled(ComponentTypeParameters.CoolingOutput, ComponentTypeParameters.CoolingOutputBtu, Units.BtuPerHourPerWatt, 0,
                () => CoolingOutput, v => NotNegative(v, x => CoolingOutput = x));
        if (Form == ComponentForm.HotWaterCylinder)
        {
            yield return ParameterValue.BindScaled(ComponentTypeParameters.CoilOutput, ComponentTypeParameters.CoilOutputBtu, Units.BtuPerHourPerWatt, 0,
                () => HeatOutput, v => NotNegative(v, x => HeatOutput = x));
            yield return ParameterValue.BindScaled(ComponentTypeParameters.CylinderVolume, ComponentTypeParameters.CylinderVolumeGallons, ComponentTypeParameters.GallonsPerLitre, 1,
                () => Capacity, v => NotNegative(v, x => Capacity = x));
            yield return ParameterValue.BindValidated(ComponentTypeParameters.StandingLoss, () => StandingLoss, v => NotNegative(v, x => StandingLoss = x));
        }
        if (Form is ComponentForm.HeatRecoveryUnit or ComponentForm.ExtractUnit)
        {
            yield return ParameterValue.BindScaled(ComponentTypeParameters.Airflow, ComponentTypeParameters.AirflowCfm, ComponentTypeParameters.CfmPerLitreSecond, 0,
                () => Airflow, v => NotNegative(v, x => Airflow = x));
            yield return ParameterValue.BindScaled(ComponentTypeParameters.AvailablePressure, ComponentTypeParameters.AvailablePressureWc, 1 / Units.PascalsPerInchOfWater, 2,
                () => AvailablePressure, v => NotNegative(v, x => AvailablePressure = x));
        }
        if (Form == ComponentForm.HeatRecoveryUnit)
            yield return ParameterValue.BindValidated(ComponentTypeParameters.HeatRecovery, () => HeatRecovery * 100, v => { if (v is < 0 or > 100) return false; HeatRecovery = v / 100; return true; });
        if (Form is ComponentForm.HeatRecoveryUnit or ComponentForm.ExtractUnit or ComponentForm.ExtractFan)
            yield return ParameterValue.BindValidated(ComponentTypeParameters.SpecificFanPower, () => SpecificFanPower, v => { if (v is < 0 or > 10) return false; SpecificFanPower = v; return true; });
        if (Form is ComponentForm.SupplyTerminal or ComponentForm.ExtractTerminal or ComponentForm.OutsideTerminal)
        {
            yield return ParameterValue.BindScaled(ComponentTypeParameters.PressureLoss, ComponentTypeParameters.PressureLossWc, 1 / Units.PascalsPerInchOfWater, 3,
                () => PressureLoss, v => NotNegative(v, x => PressureLoss = x));
            yield return ParameterValue.BindScaled(ComponentTypeParameters.RatedAirflow, ComponentTypeParameters.RatedAirflowCfm, ComponentTypeParameters.CfmPerLitreSecond, 0,
                () => RatedAirflow, v => NotNegative(v, x => RatedAirflow = x));
        }
        if (Form == ComponentForm.ExtractFan)
            yield return ParameterValue.BindScaled(ComponentTypeParameters.ContinuousRate, ComponentTypeParameters.ContinuousRateCfm, ComponentTypeParameters.CfmPerLitreSecond, 0,
                () => ContinuousRate, v => NotNegative(v, x => ContinuousRate = x));
        if (Form == ComponentForm.BackgroundVentilator)
            yield return ParameterValue.BindScaled(ComponentTypeParameters.VentArea, ComponentTypeParameters.VentAreaSquareInches, 1 / (Units.MillimetresPerInch * Units.MillimetresPerInch), 1,
                () => VentArea, v => NotNegative(v, x => VentArea = x));

        if (Form == ComponentForm.DrainAccess)
            yield return ParameterValue.BindChoice(
                ComponentTypeParameters.Access,
                () => EnumText.Humanise(Access),
                v => { if (EnumText.TryParse<DrainAccessKind>(v, out var access)) Access = access; },
                EnumText.Choices<DrainAccessKind>());

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
        BuiltInCategory.Drainage,
        BuiltInCategory.AirTerminals,
        BuiltInCategory.Sprinklers,
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
        if (document.Find<Wall>(HostId) is not { } wall) return;
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
                return $"{Units.OfSquareMetres(area, "0")} in sight{(rooms.Count > 0 ? $"; {string.Join(", ", rooms)}" : string.Empty)}{(named.Count > 0 ? $"; {named.Count} door{(named.Count == 1 ? "" : "s")} and windows: {string.Join(", ", named.Take(12))}" : string.Empty)}";
            });
            yield return ParameterValue.ReadOnly(ComponentParameters.ViewOut, () =>
                Views.ViewOut.From(document, LevelId, Location, Views.Walker.Of(type!, us).EyeHeight, null, new HashSet<Guid> { Id }).Said);
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

        // A fitting of the services: the systems it is on, and what it is not connected to yet.
        if (type is not null && Mep.Connectors.Of(type).Count > 0)
        {
            yield return ParameterValue.ReadOnly(Mep.MepSystemParameters.Systems, () =>
                Mep.MepSystems.SystemsOf(document, this).Select(system => system.Name).ToList() is { Count: > 0 } names ? string.Join(", ", names) : "<none>");
            yield return ParameterValue.ReadOnly(Mep.MepSystemParameters.NotConnected, () =>
                Mep.MepSystems.OpenKinds(document, this).Select(kind => Mep.SystemKinds.Title(kind)).ToList() is { Count: > 0 } open ? string.Join(", ", open) : "Nothing");

            // A radiator's or a floor's output at its zone's temperatures, in its room.
            if (Mep.Heating.IsEmitter(type.Form))
                yield return ParameterValue.ReadOnly(Mep.MepSystemParameters.DesignOutput, () => Mep.Heating.OutputOf(document, this) switch
                {
                    null => "Not in a heated room",
                    { Floor: true } floor => $"{Units.Watts(floor.Watts, "#,0")} at {Units.FlowAndReturn(floor.Flow, floor.Return)} - {Units.WattsPerSquareMetre(floor.Flux, "0")} over {Units.OfSquareMetres(floor.FloorArea, "0.0")}, the floor at {Units.Celsius(floor.SurfaceTemperature, "0.0")}",
                    var output => $"{Units.Watts(output.Watts, "#,0")} at {Units.FlowAndReturn(output.Flow, output.Return)}"
                });

            // An outfall's levels: its cover, and the invert of the drain it takes.
            if (type.Form is ComponentForm.FoulOutfall or ComponentForm.SurfaceWaterOutfall or ComponentForm.Soakaway)
                foreach (var level in DrainLevels(document)) yield return level;

            // How far its hot water comes along the pipes from what heats it - what is drawn off cold before it runs hot.
            if (Mep.MepSystems.MemberOf(document, this, Mep.SystemKind.HotWater) is { } hot && Mep.PipeNetworks.PipesOf(document, hot).Count > 0)
                yield return ParameterValue.ReadOnly(Mep.MepSystemParameters.HotWaterRun, () =>
                    Mep.PipeNetworks.Trace(document, hot).RunTo.TryGetValue(this, out var run)
                        ? $"{Units.Metres(run, "0.0")} of pipe from {document.FindType<ComponentType>(Mep.MepSystems.SourceOf(document, hot)?.TypeId ?? Guid.Empty)?.Name ?? "its source"}"
                        : "No pipe reaches it yet");
        }

        // An access point to a drain: its cover, the drain's invert under it, how deep that is - and which of Table 13's kinds that makes it.
        if (type?.Form == ComponentForm.DrainAccess)
            foreach (var level in DrainLevels(document)) yield return level;

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    private IEnumerable<ParameterValue> DrainLevels(BimDocument document)
    {
        var levels = new Lazy<(double Cover, double Invert)?>(() => Mep.Drainage.Levels(document, this));
        yield return ParameterValue.ReadOnly(ComponentParameters.CoverLevel, () => levels.Value is { } at ? Units.FormatLength(at.Cover) : "<no drain under it>");
        yield return ParameterValue.ReadOnly(ComponentParameters.InvertLevel, () => levels.Value is { } at ? Units.FormatLength(at.Invert) : "<no drain under it>");
        yield return ParameterValue.ReadOnly(ComponentParameters.DepthToInvert, () => levels.Value is { } at ? Units.FormatLength(at.Cover - at.Invert) : string.Empty);
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

    // The same in a project in feet and inches: given in US units, kept in metric.
    public const double CfmPerLitreSecond = 1 / Units.LitresPerSecondPerCfm, GallonsPerLitre = 1 / Units.LitresPerGallon;
    public static readonly ParameterDefinition ExtractRateCfm = new("Extract Rate (cfm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition FlowRateGpm = new("Flow Rate (gpm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition FlushVolumeGallons = new("Flush Volume (gal)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition PartFlushVolumeGallons = new("Part Flush Volume (gal, 0 = single flush)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition CapacityGallons = new("Capacity to Overflow (gal)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition RatedOutputBtu = new("Heat Output at ΔT50 (Btu/h)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition FloorOutputIp = new("Floor Output (Btu/h·sq ft·°F over the room)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition FloorOutputLimitIp = new("Most Floor Output (Btu/h·sq ft)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition HeatOutputBtu = new("Heat Output (Btu/h)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition CoolingOutput = new("Cooling Output (W, 0 = heats only)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition CoolingOutputBtu = new("Cooling Output (Btu/h, 0 = heats only)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition CoilOutputBtu = new("Coil Output (Btu/h)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition GasInput = new("Gas Input (kW gross)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition GasInputBtu = new("Gas Input (Btu/h)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Coverage = new("Coverage (square side)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition ListedFlow = new("Listed Flow (l/min)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition ListedFlowGpm = new("Listed Flow (gpm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition KFactor = new("K-Factor (l/min/bar½)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition KFactorUs = new("K-Factor (gpm/psi½)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Temperature = new("Temperature Rating (°C)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition TemperatureF = new("Temperature Rating (°F)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition SupplyPressure = new("Supply Pressure (bar, 0 = not known)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition SupplyPressurePsi = new("Supply Pressure (psi, 0 = the project's)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition AirflowCfm = new("Most Airflow (cfm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition AvailablePressureWc = new("Available Pressure (in. w.c.)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition PressureLossWc = new("Pressure Loss (in. w.c.)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition RatedAirflowCfm = new("At Airflow (cfm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition ContinuousRateCfm = new("Continuous Rate (cfm, 0 = intermittent)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition VentAreaSquareInches = new("Equivalent Area (sq in)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Efficacy =
        new("Efficacy (lm/W)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition WaterDemand =
        new("Water Demand", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition StemDiameter =
        new("Stem Diameter", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition HotWater =
        new("Hot Water", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Access =
        new("Access Point", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FlowRate =
        new("Flow Rate (l/min)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FlushVolume =
        new("Flush Volume (l)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition PartFlushVolume =
        new("Part Flush Volume (l, 0 = single flush)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Capacity =
        new("Capacity to Overflow (l)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition DishwasherWater =
        new("Water Use (l/place setting)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition WasherWater =
        new("Water Use (l/kg dry load)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition RatedOutput =
        new("Heat Output at ΔT50 (W)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition HeatExponent =
        new("Output Exponent n", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FloorOutput =
        new("Floor Output (W/m²K over the room)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FloorOutputLimit =
        new("Most Floor Output (W/m²)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition PipeSpacing =
        new("Pipe Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition HeatOutput =
        new("Heat Output (W)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition CoilOutput =
        new("Coil Output (W)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Airflow =
        new("Most Airflow (l/s)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition AvailablePressure =
        new("Available Pressure (Pa)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition HeatRecovery =
        new("Heat Recovery Efficiency (%)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition SpecificFanPower =
        new("Specific Fan Power (W/(l/s), 0 = not known)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition SpaceEfficiency =
        new("Seasonal Efficiency, Heating (%, 0 = not known)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition WaterEfficiency =
        new("Seasonal Efficiency, Hot Water (%, 0 = not known)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition CylinderVolume =
        new("Volume (l)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition CylinderVolumeGallons =
        new("Volume (gal)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition StandingLoss =
        new("Declared Heat Loss (kWh/day, 0 = not known)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition PressureLoss =
        new("Pressure Loss (Pa)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition RatedAirflow =
        new("At Airflow (l/s)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition ContinuousRate =
        new("Continuous Rate (l/s, 0 = intermittent)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition VentArea =
        new("Equivalent Area (mm²)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Load =
        new("Rated Load (W)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition ShowerLoad =
        new("Electric Shower Rating (W, 0 = fed hot water)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Lumens =
        new("Light Output (lm)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Distribution =
        new("Light Distribution", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition PhotometricData =
        new("Photometric Data", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Ways =
        new("Ways", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition SupplyRating =
        new("Supply (A)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition ExternalImpedance =
        new("External Earth Loop Impedance Ze (Ω)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
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

    public static readonly ParameterDefinition CoverLevel =
        new("Cover Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition InvertLevel =
        new("Invert Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition DepthToInvert =
        new("Depth to Invert", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
}
