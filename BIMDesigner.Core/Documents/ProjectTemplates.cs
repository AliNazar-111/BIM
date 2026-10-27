using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Core.Documents;

/// <summary>What a new project starts from: the rules it is drawn and checked to.</summary>
public enum ProjectTemplate
{
    /// <summary>A home in England: the Approved Documents, an accessible and adaptable dwelling (M4(2)), BS 7671, CIBSE's climate.</summary>
    EnglandHouse,

    /// <summary>A wheelchair user dwelling in England: Approved Document M's M4(3) on top of the rest.</summary>
    EnglandWheelchairHouse,

    /// <summary>A house in the United States: the International Residential Code, the NEC, the jurisdiction's design temperature.</summary>
    UsHouse
}

/// <summary>
/// Everything the New Project steps ask: the template, the project's particulars, where it is
/// and the ground it stands on, and how many storeys it has and how tall they are.
/// </summary>
public sealed record NewProjectSpec
{
    public ProjectTemplate Template { get; init; } = ProjectTemplate.EnglandHouse;

    public string Name { get; init; } = "Untitled Project";

    public string Number { get; init; } = string.Empty;

    public string Client { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string BuildingType { get; init; } = string.Empty;

    /// <summary>In England, the nearest of CIBSE Guide A's stations, and how high the site is over the sea, m.</summary>
    public HeatingLocation HeatingLocation { get; init; } = HeatingLocation.London;

    public double SiteAltitude { get; init; }

    public GroundType Ground { get; init; } = GroundType.FirmClay;

    /// <summary>In the US, the jurisdiction's winter design temperature (°F, Table R301.2), ground snow load (psf) and frost line depth (mm).</summary>
    public double? WinterDesignTemperature { get; init; }

    public double GroundSnowLoad { get; init; } = 20;

    public double FrostDepth { get; init; }

    /// <summary>The storeys above the ground, their height floor to floor (mm), and whether there is a basement under them.</summary>
    public int Storeys { get; init; } = 2;

    public double StoreyHeight { get; init; } = 2700;

    public bool Basement { get; init; }
}

/// <summary>New projects made from a template and the answers to the New Project steps.</summary>
public static class ProjectTemplates
{
    /// <summary>The most storeys the steps offer.</summary>
    public const int MostStoreys = 6;

    public static string Title(this ProjectTemplate template) => template switch
    {
        ProjectTemplate.EnglandWheelchairHouse => "Wheelchair user home - England",
        ProjectTemplate.UsHouse => "House - United States",
        _ => "House - England"
    };

    public static string Description(this ProjectTemplate template) => template switch
    {
        ProjectTemplate.EnglandWheelchairHouse =>
            "Approved Document M's wheelchair user dwelling (M4(3)) - wider doors, turning circles and clear spaces - with the rest of the Approved Documents, BS 7671 and CIBSE's climate.",
        ProjectTemplate.UsHouse =>
            "The International Residential Code, the National Electrical Code and ASHRAE 62.2 - clearances, egress, receptacles and the jurisdiction's design temperature.",
        _ => "The Approved Documents - an accessible and adaptable home (M4(2)) - with BS 7671 for the electrics, BS EN 12831 for the heat loss and CIBSE's climate."
    };

    /// <summary>The standard a template checks clearances, rooms and services to.</summary>
    public static ClearanceStandard StandardOf(ProjectTemplate template) => template switch
    {
        ProjectTemplate.EnglandWheelchairHouse => ClearanceStandard.ApprovedDocumentM3,
        ProjectTemplate.UsHouse => ClearanceStandard.Irc,
        _ => ClearanceStandard.ApprovedDocumentM
    };

    // The starter types made for one country's homes, where the other has its own: a load center,
    // duplex receptacles and a tank water heater for the US, its stock doors, beds, tub and shower in
    // inches, its bath fans in cfm, its pipes and ducts in inches, its parking space in feet, its
    // footings in inches; a consumer unit, BS 1363 sockets, a shaver socket and an unvented cylinder
    // for England, its doors, beds, baths, fans, pipes and ducts in metric sizes, its parking bays
    // and footings in millimetres.
    private static readonly HashSet<string> UsTypes = new(StringComparer.Ordinal)
    {
        "Load Center - 200 A 40 Space", "Receptacle - Duplex 15 A", "Receptacle - Duplex 20 A Countertop", "Switch - Single Pole", "Water Heater - Electric 50 gal",
        "Exterior Door - 36 x 80 in", "Interior Door - 30 x 80 in", "Interior Door - 32 x 80 in", "Garage Entry Door - 36 x 80 in, 20 min", "Double Hung Twin - 60 x 60 in",
        "Double Hung - 36 x 60 in", "Casement - 24 x 48 in",
        "Bed - Twin 38 x 75 in", "Bed - Full 54 x 75 in", "Bed - Queen 60 x 80 in", "Bed - King 76 x 80 in", "Bed - California King 72 x 84 in",
        "Bathtub - 60 x 30 in", "Shower - 36 x 36 in", "Exhaust Fan - Bath 50 cfm", "Exhaust Fan - Bath 80 cfm", "HRV - 150 cfm", "Heat Pump - Air Source 3 ton",
        "Round - Galvanized Steel (in)", "Flexible - Insulated (in)", "Rectangular - Galvanized Steel (in)", "PVC DWV - Schedule 40 (ASTM D2665)",
        "Copper - Type L (ASTM B88)", "PEX - CTS SDR 9 (ASTM F876)",
        "Parking Space - 9 x 18 ft", "Parking Space - Accessible 96 + 60 in", "Turning Circle - 60 in",
        "Footing - 16 x 8 in", "Footing - 24 x 10 in", "Pad Footing - 24 x 24 x 12 in"
    };

    private static readonly HashSet<string> EnglishTypes = new(StringComparer.Ordinal)
    {
        "Consumer Unit - 12 Way", "Socket - Double", "Switch - One Gang", "Shaver Socket", "Hot Water Cylinder - Unvented 210 l",
        "Single - 900 x 2100", "Single - 1000 x 2100", "Front Door - 1000 x 2100", "Fire Door FD30S - 900 x 2100",
        "Double Hung - 900 x 1500", "Casement - 1200 x 1200",
        "Bed - Single 900 x 1900", "Bed - Small Double 1200 x 1900", "Bed - Double 1350 x 1900", "Bed - King 1500 x 2000", "Bed - Super King 1800 x 2000",
        "Bath - 1700 x 700", "Bath - 1800 x 800", "Shower Tray - 900 x 900", "Extract Fan - 100 (15 l/s)", "Extract Fan - 150 (60 l/s)",
        "Heat Recovery Unit - 75 l/s", "Background Ventilator - 5000 mm²", "Heat Pump - Air Source 7 kW",
        "Round - Galvanised Spiral", "Round - PVC Rigid", "Flat Channel - PVC", "Flexible - Insulated", "Rectangular - Galvanised Steel",
        "PVC-U Soil and Waste - BS EN 1329", "PVC-U Drain - BS EN 1401", "Copper - BS EN 1057", "PE-X",
        "Parking Bay - 2400 x 4800", "Parking Bay - Accessible 3600 x 6000", "Turning Circle - 1500",
        "Strip Footing - 450 x 225", "Strip Footing - 600 x 225", "Strip Footing - 750 x 300", "Strip Footing - 900 x 300", "Strip Footing - 1100 x 400",
        "Pad Footing - 600 x 600 x 300", "Pad Footing - 900 x 900 x 350", "Pad Footing - 1200 x 1200 x 450", "Pad Footing - 1500 x 1500 x 600"
    };

    /// <summary>
    /// Whether a type is one the project's country is offered: a US project is not offered a
    /// consumer unit, a 900 x 2100 door or a parking bay in millimetres, nor an English one a
    /// load center, a queen bed or a parking space in feet. A type made or brought in by hand is
    /// offered to both.
    /// </summary>
    public static bool Fits(BimDocument document, Elements.ElementType type) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? !EnglishTypes.Contains(type.Name) : !UsTypes.Contains(type.Name);

    /// <summary>What a storey is called, counting up from the ground: the UK's ground and first floors, the US's first and second.</summary>
    public static string StoreyName(ProjectTemplate template, int index)
    {
        string[] ordinals = { "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh" };
        if (template == ProjectTemplate.UsHouse) return $"{ordinals[Math.Min(index, ordinals.Length - 1)]} Floor";
        return index == 0 ? "Ground Floor" : $"{ordinals[Math.Min(index - 1, ordinals.Length - 1)]} Floor";
    }

    /// <summary>
    /// A new project as the steps set it out: the template's types and settings, the project's
    /// particulars, its site and ground, and a level for each storey - a basement under them if
    /// asked, and the roof's over the top one.
    /// </summary>
    public static BimDocument Create(NewProjectSpec spec)
    {
        var document = BimDocument.CreateDefault();
        var info = document.ProjectInformation;
        info.Name = string.IsNullOrWhiteSpace(spec.Name) ? "Untitled Project" : spec.Name.Trim();
        info.Number = spec.Number.Trim();
        info.Client = spec.Client.Trim();
        info.Address = spec.Address.Trim();
        info.BuildingType = spec.BuildingType.Trim();
        info.ClearanceStandard = StandardOf(spec.Template);
        info.Units = spec.Template == ProjectTemplate.UsHouse ? UnitSystem.Imperial : UnitSystem.Metric;
        info.Ground = spec.Ground;
        info.SiteAltitude = Math.Max(0, spec.SiteAltitude);
        if (spec.Template == ProjectTemplate.UsHouse)
        {
            info.WinterDesignTemperature = spec.WinterDesignTemperature;
            info.GroundSnowLoad = Math.Max(0, spec.GroundSnowLoad);
            info.FrostDepth = Math.Max(0, spec.FrostDepth);
        }
        else info.HeatingLocation = spec.HeatingLocation;

        // A level for each storey from the ground up, and the roof's over the top one - the template's two levels the first of them.
        var storeys = Math.Clamp(spec.Storeys, 1, MostStoreys);
        var height = Math.Clamp(spec.StoreyHeight, 2000, 6000);
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        for (var i = 0; i <= storeys; i++)
        {
            var level = i < levels.Count ? levels[i] : new Level();
            level.Name = i == storeys ? "Roof" : StoreyName(spec.Template, i);
            level.Elevation = i * height;
            if (i >= levels.Count) document.AddLevel(level);
        }

        if (spec.Basement) document.AddLevel(new Level { Name = "Basement", Elevation = -height });
        return document;
    }
}
