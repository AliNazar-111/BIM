using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Mep;

/// <summary>Where a UK building is, for its heat loss: the nearest of the places CIBSE Guide A gives design temperatures for.</summary>
public enum HeatingLocation
{
    Belfast,
    Birmingham,
    Cardiff,
    Edinburgh,
    Glasgow,
    Leeds,
    London,
    Manchester,
    Newcastle,
    Norwich,
    Nottingham,
    Plymouth,
    Southampton,
    Swindon
}

/// <summary>
/// How cold it is outside, for a heat loss.
///
/// In the UK, the external design temperature is the 99.6% one of the weather station nearest the
/// building - CIBSE Guide A (2015) Table 2.5, as MCS MIS 3005-D (Table 2, column B) and MCS's heat
/// load calculator take it, column B needing no uplift for intermittent heating - 0.6 °C colder for
/// every whole 100 m the site is higher than the station. The ground under a solid floor is taken at
/// the local annual mean air temperature (MIS 3005-D 5.5.1 c), from the station of the regional
/// table nearest the location - the 1981-2010 means MCS's calculator gives. In the US, the winter
/// design temperature is the jurisdiction's, from IRC Table R301.2, set for the project.
/// </summary>
public static class Climate
{
    /// <summary>A location's station, its height above the sea (m), its 99.6% design temperature and the annual mean of its nearest regional station (°C).</summary>
    public sealed record Station(string Name, double Altitude, double Design, string MeanStation, double AnnualMean);

    /// <summary>The design temperatures and annual means, by location.</summary>
    public static Station Of(HeatingLocation location) => location switch
    {
        HeatingLocation.Belfast => new("Aldergrove", 63, -3.2, "Aldergrove", 9.4),
        HeatingLocation.Birmingham => new("Coleshill", 96, -5.1, "Elmdon", 9.8),
        HeatingLocation.Cardiff => new("St Athan", 49, -3.1, "Filton", 10.6),
        HeatingLocation.Edinburgh => new("Gogarbank", 57, -5.4, "Leuchars", 8.8),
        HeatingLocation.Glasgow => new("Bishopton", 59, -5.6, "Abbotsinch", 9.1),
        HeatingLocation.Leeds => new("Church Fenton", 8, -3.3, "Finningley", 10.0),
        HeatingLocation.London => new("Heathrow", 25, -3.0, "Heathrow", 11.3),
        HeatingLocation.Manchester => new("Woodford", 88, -4.5, "Ringway", 10.0),
        HeatingLocation.Newcastle => new("Albemarle", 142, -3.7, "Boulmer", 9.0),
        HeatingLocation.Norwich => new("Marham", 21, -4.6, "Honington", 10.1),
        HeatingLocation.Nottingham => new("Watnall", 117, -3.9, "Finningley", 10.0),
        HeatingLocation.Plymouth => new("Mountbatten", 50, -1.5, "Plymouth", 11.0),
        HeatingLocation.Southampton => new("Hurn", 10, -4.8, "Hurn", 10.4),
        _ => new("Brize Norton", 82, -4.6, "Filton", 10.6)
    };

    /// <summary>The temperature outside the heat loss is worked to, °C; null for a US project with none set.</summary>
    public static double? DesignOutside(BimDocument document)
    {
        var project = document.ProjectInformation;
        if (project.ClearanceStandard.IsUs())
            return project.WinterDesignTemperature is { } fahrenheit ? (fahrenheit - 32) * 5 / 9 : null;

        var station = Of(project.HeatingLocation);
        var above = Math.Floor((project.SiteAltitude - station.Altitude) / 100);
        return station.Design - 0.6 * Math.Max(0, above);
    }

    /// <summary>
    /// What the ground under a solid floor is taken at, °C: the annual mean in the UK; in the US, with
    /// no mean given, the design temperature outside.
    /// </summary>
    public static double? Ground(BimDocument document) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? DesignOutside(document) : Of(document.ProjectInformation.HeatingLocation).AnnualMean;

    /// <summary>What the heat loss is worked to, in words.</summary>
    public static string Describe(BimDocument document)
    {
        var project = document.ProjectInformation;
        if (project.ClearanceStandard.IsUs())
            return project.WinterDesignTemperature is { } fahrenheit
                ? $"{fahrenheit:0} °F outside, the winter design temperature of IRC Table R301.2 set for the project; rooms at 68 °F (R303.10)"
                : "No winter design temperature set - the project's from IRC Table R301.2 is needed";

        var station = Of(project.HeatingLocation);
        var outside = DesignOutside(document)!.Value;
        var altitude = outside < station.Design - 0.01 ? $", {project.SiteAltitude:0} m up" : string.Empty;
        return $"{outside:0.0} °C outside - {station.Name}'s 99.6% design temperature (CIBSE Guide A Table 2.5){altitude}; the ground under a solid floor at {station.AnnualMean:0.0} °C, {station.MeanStation}'s annual mean";
    }
}
