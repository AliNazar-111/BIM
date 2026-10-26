using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// Which of the building services a drawing is of: the building itself, or one discipline of
/// its services - mechanical (heating, ventilation, gas), electrical (power, lighting, alarms)
/// or plumbing (water and drainage). A services plan draws the building faint under its own.
/// </summary>
public enum Discipline
{
    Architectural,
    Mechanical,
    Electrical,
    Plumbing
}

/// <summary>
/// What a system carries, as IFC4's IfcDistributionSystemEnum sorts systems: cold and hot
/// water, the heating's water, foul water, gas, air drawn out, power, and the link between
/// smoke alarms that sets them all off.
/// </summary>
public enum SystemKind
{
    ColdWater,
    HotWater,
    Heating,
    Foul,

    /// <summary>Rainwater from the roofs, down the downpipes and away in drains of its own.</summary>
    Rainwater,
    Gas,
    ExtractAir,
    Power,
    FireAlarm,

    /// <summary>Fresh air a heat recovery unit supplies to the habitable rooms.</summary>
    SupplyAir,

    /// <summary>The air a heat recovery unit draws in from outside and lets out again, through its terminals on the outside wall or roof.</summary>
    OutsideAir,

    /// <summary>Lights and the switches that turn them on and off - two or more switches for two-way switching, at the top and foot of a stair.</summary>
    Switching
}

/// <summary>What a connection is made with: a pipe, a duct, or a cable.</summary>
public enum ConnectorDomain
{
    Pipe,
    Duct,
    Electrical
}

/// <summary>Which way what is carried goes through a connection: into the fitting, out of it, or neither way in particular.</summary>
public enum ConnectorFlow
{
    In,
    Out,
    Both
}

/// <summary>
/// What a circuit from a consumer unit is for, as circuits in a home are: a ring of sockets, a
/// radial, the lights, the cooker, the shower, the immersion heater, the boiler, the alarms.
/// </summary>
public enum CircuitUse
{
    RingFinal,
    Radial,
    Lighting,
    Cooker,
    Shower,
    ImmersionHeater,
    Heating,
    Alarms,
    Other
}

public static class SystemKinds
{
    public static string Title(this SystemKind kind) => kind switch
    {
        SystemKind.ColdWater => "Cold Water",
        SystemKind.HotWater => "Hot Water",
        SystemKind.Heating => "Heating",
        SystemKind.Foul => "Foul Water",
        SystemKind.Rainwater => "Rainwater",
        SystemKind.Gas => "Gas",
        SystemKind.ExtractAir => "Extract Air",
        SystemKind.Power => "Power",
        SystemKind.SupplyAir => "Supply Air",
        SystemKind.OutsideAir => "Outside Air",
        SystemKind.Switching => "Switching",
        _ => "Fire Alarm"
    };

    /// <summary>What a new system of the kind is called, before its number: "Cold Water 1", "Heating Zone 2", "Circuit 3".</summary>
    public static string NamePrefix(this SystemKind kind) => kind switch
    {
        SystemKind.Heating => "Heating Zone",
        SystemKind.ExtractAir => "Extract",
        SystemKind.SupplyAir => "Supply",
        SystemKind.Power => "Circuit",
        SystemKind.FireAlarm => "Alarm Interlink",
        _ => kind.Title()
    };

    public static Discipline Discipline(this SystemKind kind) => kind switch
    {
        SystemKind.ColdWater or SystemKind.HotWater or SystemKind.Foul or SystemKind.Rainwater => Mep.Discipline.Plumbing,
        SystemKind.Heating or SystemKind.Gas or SystemKind.ExtractAir or SystemKind.SupplyAir or SystemKind.OutsideAir => Mep.Discipline.Mechanical,
        _ => Mep.Discipline.Electrical
    };

    public static ConnectorDomain Domain(this SystemKind kind) => kind switch
    {
        SystemKind.ExtractAir or SystemKind.SupplyAir or SystemKind.OutsideAir => ConnectorDomain.Duct,
        SystemKind.Power or SystemKind.FireAlarm or SystemKind.Switching => ConnectorDomain.Electrical,
        _ => ConnectorDomain.Pipe
    };

    /// <summary>The category a system of the kind is scheduled and filtered under: piping, duct or electrical systems.</summary>
    public static BuiltInCategory Category(this SystemKind kind) => kind.Domain() switch
    {
        ConnectorDomain.Pipe => BuiltInCategory.PipingSystems,
        ConnectorDomain.Duct => BuiltInCategory.DuctSystems,
        _ => BuiltInCategory.ElectricalCircuits
    };

    /// <summary>
    /// What feeds a system of the kind - the equipment it runs from - or null for one that runs
    /// from nothing: foul water runs away to a drain, air is drawn out by its own fans, and
    /// interlinked alarms set each other off.
    /// </summary>
    public static string? Fed(this SystemKind kind) => kind switch
    {
        SystemKind.ColdWater => "the stop valve on the incoming main",
        SystemKind.HotWater => "a combi boiler or a hot water cylinder",
        SystemKind.Heating => "a boiler",
        SystemKind.Gas => "the gas meter",
        SystemKind.Power => "a consumer unit",
        SystemKind.Foul => "an outfall to the foul sewer",
        SystemKind.Rainwater => "an outfall - a soakaway, or the surface water sewer",
        SystemKind.SupplyAir or SystemKind.OutsideAir => "a heat recovery unit",
        _ => null
    };

    /// <summary>The colour the kind is drawn in on a services plan: cold water blue, hot water red, gas yellow.</summary>
    public static ColourRgb Colour(this SystemKind kind) => kind switch
    {
        SystemKind.ColdWater => new ColourRgb(0x2F, 0x7F, 0xE0),
        SystemKind.HotWater => new ColourRgb(0xE0, 0x45, 0x3A),
        SystemKind.Heating => new ColourRgb(0xE8, 0x86, 0x2A),
        SystemKind.Foul => new ColourRgb(0x8B, 0x5A, 0x2B),
        SystemKind.Rainwater => new ColourRgb(0x2B, 0xA8, 0x9E),
        SystemKind.Gas => new ColourRgb(0xD4, 0xB0, 0x00),
        SystemKind.ExtractAir => new ColourRgb(0x8E, 0x6C, 0xC9),
        SystemKind.SupplyAir => new ColourRgb(0x3C, 0xA8, 0xD8),
        SystemKind.OutsideAir => new ColourRgb(0x6F, 0x9E, 0x55),
        SystemKind.Power => new ColourRgb(0xD0, 0x36, 0x8F),
        SystemKind.Switching => new ColourRgb(0xE8, 0x9A, 0x1C),
        _ => new ColourRgb(0xE0, 0x1F, 0x1F)
    };

    /// <summary>The kinds a discipline's plan is of.</summary>
    public static IEnumerable<SystemKind> Of(Discipline discipline) => Enum.GetValues<SystemKind>().Where(kind => kind.Discipline() == discipline);

    public static string Title(this Discipline discipline) => discipline.ToString();

    public static string Title(this CircuitUse use) => use switch
    {
        CircuitUse.RingFinal => "Ring Final",
        CircuitUse.ImmersionHeater => "Immersion Heater",
        _ => use.ToString()
    };

    /// <summary>A circuit's use as the US has it: a range rather than a cooker, receptacles rather than a radial, a water heater.</summary>
    public static string Title(this CircuitUse use, bool us) => !us ? use.Title() : use switch
    {
        CircuitUse.Radial or CircuitUse.RingFinal => "Receptacles",
        CircuitUse.Cooker => "Range",
        CircuitUse.ImmersionHeater => "Water Heater",
        _ => use.Title()
    };
}
