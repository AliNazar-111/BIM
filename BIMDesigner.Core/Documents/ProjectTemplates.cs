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

    // The starter fittings made for one country's homes: a load center, duplex receptacles and a
    // tank water heater for the US; a consumer unit, BS 1363 sockets, a shaver socket and an
    // unvented cylinder for England.
    private static readonly HashSet<string> UsFittings = new(StringComparer.Ordinal)
    {
        "Load Center - 200 A 40 Space", "Receptacle - Duplex 15 A", "Receptacle - Duplex 20 A Countertop", "Switch - Single Pole", "Water Heater - Electric 50 gal"
    };

    private static readonly HashSet<string> EnglishFittings = new(StringComparer.Ordinal)
    {
        "Consumer Unit - 12 Way", "Socket - Double", "Switch - One Gang", "Shaver Socket", "Hot Water Cylinder - Unvented 210 l"
    };

    /// <summary>
    /// Whether a fitting is one the project's country fits: a US project is not offered a consumer
    /// unit or a shaver socket, nor an English one a load center. A fitting made or brought in by
    /// hand is offered to both.
    /// </summary>
    public static bool Fits(BimDocument document, ComponentType type) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? !EnglishFittings.Contains(type.Name) : !UsFittings.Contains(type.Name);

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
