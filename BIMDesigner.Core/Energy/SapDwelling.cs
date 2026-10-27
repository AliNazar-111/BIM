using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Energy;

/// <summary>What a surface of the home is, as SAP's worksheet counts it.</summary>
public enum SapSurface
{
    Wall,
    PartyWall,
    Roof,
    Floor,

    /// <summary>A window, a door more than 60% glass, or curtain walling: glazing, its sun counted.</summary>
    Window,

    /// <summary>A window in a pitched roof, its sun counted at the roof's pitch.</summary>
    RoofWindow,

    /// <summary>A rooflight on a flat roof, taken as level.</summary>
    Rooflight,

    /// <summary>A door solid or up to 60% glass: no sun counted through it.</summary>
    Door
}

/// <summary>How much the windows are shaded by what is round the home (Table 6d).</summary>
public enum SapOvershading
{
    Heavy,
    MoreThanAverage,
    AverageOrUnknown,
    VeryLittle
}

/// <summary>How much of the sky to the south a solar array has blocked (Table M1).</summary>
public enum PvOvershading
{
    NoneOrVeryLittle,
    Modest,
    Significant,
    Heavy,
    Severe
}

/// <summary>How the home is ventilated, as SAP's section 2 has the systems.</summary>
public enum SapVentilation
{
    /// <summary>Background ventilators and intermittent extract fans.</summary>
    Natural,

    /// <summary>Mechanical extract running continuously - a central unit, or fans in the wet rooms (dMEV).</summary>
    Extract,

    /// <summary>Balanced supply and extract with no heat recovery.</summary>
    Balanced,

    /// <summary>Balanced with heat recovery (MVHR).</summary>
    HeatRecovery
}

/// <summary>What heats the home.</summary>
public enum SapHeatSource
{
    /// <summary>Direct-acting electric heaters: what SAP takes for a new home with no heating given (9.1).</summary>
    DirectElectric,
    GasBoiler,
    HeatPump
}

/// <summary>What heats its hot water.</summary>
public enum SapWaterHeater
{
    /// <summary>The main heating: a boiler's coil in a cylinder, a combi, a heat pump's cylinder.</summary>
    MainSystem,

    /// <summary>An electric immersion heater in the cylinder.</summary>
    Immersion,

    /// <summary>Electric heaters at each tap, no store and no pipes between: none lost on the way.</summary>
    PointOfUse
}

/// <summary>
/// One surface of the home: its area, m², and its U-value, W/m²K - a window's or a door's as its
/// maker rates it, a wall's or a roof's from its layers with any unheated space beyond allowed for.
/// A window's way it faces (degrees clockwise from true north), its tilt from the level, its glass's
/// total solar energy transmittance at normal incidence, the share of it glazed, and its light
/// transmittance.
/// </summary>
public sealed record SapElement(string Name, SapSurface Kind, double Area, double U)
{
    public double Orientation { get; init; } = 180;
    public double Tilt { get; init; } = 90;
    public double SolarTransmittance { get; init; } = 0.63;
    public double FrameFactor { get; init; } = 0.7;
    public double LightTransmittance { get; init; } = 0.8;

    /// <summary>Curtain walling: glazing in the actual home, glazing at 1.3 in the notional (Table R1).</summary>
    public bool CurtainWall { get; init; }

    /// <summary>Triple glazing, for a roof window's allowance for its slope (Table 6e).</summary>
    public bool Triple { get; init; }

    public Element? Source { get; init; }

    /// <summary>Whether its sun is counted - glazing of any kind.</summary>
    public bool Glazed => Kind is SapSurface.Window or SapSurface.RoofWindow or SapSurface.Rooflight;

    /// <summary>Whether it is an opening - a window, a roof window, a rooflight or a door - that the notional home holds to a quarter of the floor area.</summary>
    public bool Opening => Glazed || Kind == SapSurface.Door;
}

/// <summary>A junction where the heat finds a way round the insulation: its kind (Table K1's reference), its length, m, and its ψ, W/m·K.</summary>
public sealed record SapJunction(string Code, string What, double Length, double Psi);

/// <summary>A solar array: its peak power, kWp; the way it faces and its pitch, degrees; its overshading factor (Table M1).</summary>
public sealed record SapPhotovoltaic(string Name, double PeakPower, double Orientation, double Tilt, double Shading)
{
    public Element? Source { get; init; }
}

/// <summary>A shower: its flow, l/min, as Approved Document G's calculation takes it - or for an electric shower its power, kW.</summary>
public sealed record SapShower(double FlowRate, double ElectricPower = 0)
{
    public bool Electric => ElectricPower > 0;
}

/// <summary>
/// The heating: what it is, its efficiencies heating the home and its hot water, % - a boiler's winter
/// and summer seasonal efficiencies, a heat pump's as the PCDB lists it before SAP's in-use factor -
/// its output at the design conditions, W, how quickly its emitters answer (Table 4d) and its controls
/// (Table 4e): their type in Table 9 and any allowance on the temperature.
/// </summary>
public sealed record SapHeating(SapHeatSource Source, string Name)
{
    public double SpaceEfficiency { get; init; } = 100;
    public double WaterEfficiency { get; init; } = 100;
    public bool Combi { get; init; }
    public double Output { get; init; }
    public double Responsiveness { get; init; } = 1;
    public int ControlType { get; init; } = 2;
    public double TemperatureAdjustment { get; init; }
    public string Controls { get; init; } = "Programmer, room thermostat and TRVs";

    /// <summary>Whether a room thermostat switches it - a boiler without one loses 5% (Table 4c) and its pump runs 30% longer (Table 4f).</summary>
    public bool RoomThermostat { get; init; } = true;
}

/// <summary>
/// The hot water: what heats it, its cylinder's volume, litres (none for a combi or water heated at the
/// tap), its maker's declared loss, kWh/day, or else its factory insulation, mm; and whether it has a
/// thermostat, its own time control, insulated primary pipes and stands where the home is heated.
/// </summary>
public sealed record SapHotWater(SapWaterHeater Heater, double CylinderVolume)
{
    public double? DeclaredLoss { get; init; }
    public double Insulation { get; init; } = 50;
    public bool Cylinderstat { get; init; } = true;
    public bool SeparateTiming { get; init; } = true;
    public bool InsulatedPrimary { get; init; } = true;

    /// <summary>A heat pump's in-use factor for its water heating: 0.95 with a store as its PCDB record has it, 0.60 with one not (Table N8).</summary>
    public double HeatPumpInUse { get; init; } = 0.95;
}

/// <summary>
/// A home as SAP 10.3 takes it: its floor area and volume, its surfaces and junctions, how airtight
/// and how ventilated, what heats it and its water, its showers and baths, its lights and its solar
/// arrays - what the worksheet is worked from, the actual home's or the notional one's.
/// </summary>
public sealed record SapDwelling
{
    /// <summary>Its total floor area, m² (box 4), and volume, m³ (box 5).</summary>
    public double FloorArea { get; init; }
    public double Volume { get; init; }

    /// <summary>How many storeys it has; and whether it is a flat.</summary>
    public int Storeys { get; init; } = 1;
    public bool Flat { get; init; }

    /// <summary>The floor area of its living room, m² - the lounge, or the biggest room for sitting in (section 7).</summary>
    public double LivingArea { get; init; }

    /// <summary>
    /// Its ground floor's area, m², with any unheated spaces in it, measured to the inside of its outside
    /// walls - what Approved Document L's solar output (5.73) and the notional home's array are sized by.
    /// </summary>
    public double GroundFloorArea { get; init; }

    public IReadOnlyList<SapElement> Elements { get; init; } = Array.Empty<SapElement>();

    /// <summary>Its junctions with their ψ; none, and <see cref="YValue"/> is taken on the area of what faces outside.</summary>
    public IReadOnlyList<SapJunction> Junctions { get; init; } = Array.Empty<SapJunction>();
    public double YValue { get; init; } = 0.20;

    /// <summary>Its thermal mass parameter, kJ/m²K (box 35): the heat its surfaces hold, a square metre of floor.</summary>
    public double ThermalMass { get; init; } = 250;

    public int ShelteredSides { get; init; } = 2;
    public SapOvershading Overshading { get; init; } = SapOvershading.AverageOrUnknown;

    /// <summary>Air permeability at 50 Pa, m³/h·m².</summary>
    public double AirPermeability { get; init; } = 5;

    public SapVentilation Ventilation { get; init; } = SapVentilation.Natural;

    /// <summary>The fans' specific fan power in use, W/(l/s); and a heat recovery unit's efficiency in use, 0 to 1.</summary>
    public double SpecificFanPower { get; init; }
    public double HeatRecovery { get; init; }

    /// <summary>Whether its extract is a central unit's rather than fans in the wet rooms (Table 5.6).</summary>
    public bool CentralExtract { get; init; }

    /// <summary>Openings the air goes up or out by (boxes 6a to 7c): chimneys, flues, intermittent fans, passive vents, flueless fires.</summary>
    public int OpenChimneys { get; init; }
    public int OpenFlues { get; init; }
    public int IntermittentFans { get; init; }
    public int PassiveVents { get; init; }
    public int FluelessFires { get; init; }

    public SapHeating Heating { get; init; } = new(SapHeatSource.DirectElectric, "Direct-acting electric heaters");
    public SapHotWater HotWater { get; init; } = new(SapWaterHeater.Immersion, 0);

    public IReadOnlyList<SapShower> Showers { get; init; } = Array.Empty<SapShower>();
    public int Baths { get; init; }

    /// <summary>Whether it is designed to use no more than 125 litres of water a person a day (Approved Document G).</summary>
    public bool WaterTarget { get; init; } = true;

    /// <summary>Waste water heat recovery on its mixer showers, instantaneous: its efficiency and utilisation, 0 to 1; none at 0.</summary>
    public double WasteWaterRecovery { get; init; }
    public double WasteWaterUtilisation { get; init; } = 0.98;

    /// <summary>Its fixed lights: their light, lm, and their power, W; none, and SAP's allowance for a home without (L5b).</summary>
    public double LightingLumens { get; init; }
    public double LightingWatts { get; init; }

    public IReadOnlyList<SapPhotovoltaic> Photovoltaics { get; init; } = Array.Empty<SapPhotovoltaic>();

    /// <summary>A battery's usable capacity, kWh; and whether the meter can export.</summary>
    public double Battery { get; init; }
    public bool Export { get; init; } = true;

    /// <summary>Fixed cooling: the share of the floor it cools, and its seasonal energy efficiency ratio; none at 0.</summary>
    public double CooledFraction { get; init; }
    public double Seer { get; init; }

    /// <summary>How many people SAP takes to live in it (box 42), by its floor area.</summary>
    public double Occupancy => FloorArea > 13.9
        ? 1 + 1.76 * (1 - Math.Exp(-0.000349 * (FloorArea - 13.9) * (FloorArea - 13.9))) + 0.0013 * (FloorArea - 13.9)
        : 1;
}
