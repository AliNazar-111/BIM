namespace BIMDesigner.Core.Rendering;

/// <summary>When clocks go forward an hour for the summer.</summary>
public enum SummerTime
{
    None,

    /// <summary>The UK and the rest of Europe: from 01:00 UTC on the last Sunday of March to the last Sunday of October.</summary>
    European,

    /// <summary>The US and Canada: from 2 am on the second Sunday of March to the first Sunday of November.</summary>
    NorthAmerican
}

/// <summary>
/// Where the sun is in the sky at a place and a time, by the Astronomical Almanac's low-precision
/// formulas - good to a hundredth of a degree this century: its altitude above the horizon and its
/// azimuth round from true north, clockwise.
/// </summary>
public static class SunPosition
{
    public static (double Altitude, double Azimuth) Of(double latitude, double longitude, DateTime utc)
    {
        const double Rad = Math.PI / 180;
        var n = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;

        // The sun's mean longitude and anomaly, its ecliptic longitude, the obliquity of the ecliptic.
        var meanLongitude = Normalise(280.460 + 0.9856474 * n);
        var anomaly = Normalise(357.528 + 0.9856003 * n) * Rad;
        var lambda = (meanLongitude + 1.915 * Math.Sin(anomaly) + 0.020 * Math.Sin(2 * anomaly)) * Rad;
        var epsilon = (23.439 - 0.0000004 * n) * Rad;

        // Its right ascension and declination; the hour angle by the local sidereal time.
        var rightAscension = Math.Atan2(Math.Cos(epsilon) * Math.Sin(lambda), Math.Cos(lambda)) / Rad;
        var declination = Math.Asin(Math.Sin(epsilon) * Math.Sin(lambda));
        var sidereal = Normalise((18.697374558 + 24.06570982441908 * n) * 15 + longitude);
        var hour = (sidereal - rightAscension) * Rad;

        var phi = latitude * Rad;
        var altitude = Math.Asin(Math.Sin(phi) * Math.Sin(declination) + Math.Cos(phi) * Math.Cos(declination) * Math.Cos(hour));
        var azimuth = Math.Atan2(-Math.Sin(hour), Math.Tan(declination) * Math.Cos(phi) - Math.Sin(phi) * Math.Cos(hour));
        return (altitude / Rad, Normalise(azimuth / Rad));
    }

    /// <summary>A local clock time as the time it is at Greenwich, by the place's offset and its summer time.</summary>
    public static DateTime ToUtc(DateOnly date, TimeOnly time, double utcOffset, SummerTime summer)
    {
        var local = date.ToDateTime(time);
        var standard = local.AddHours(-utcOffset);
        var forward = summer switch
        {
            SummerTime.European => standard >= LastSunday(date.Year, 3).ToDateTime(new TimeOnly(1, 0)) && standard < LastSunday(date.Year, 10).ToDateTime(new TimeOnly(1, 0)),
            SummerTime.NorthAmerican => local >= NthSunday(date.Year, 3, 2).ToDateTime(new TimeOnly(2, 0)) && local < NthSunday(date.Year, 11, 1).ToDateTime(new TimeOnly(2, 0)),
            _ => false
        };
        return DateTime.SpecifyKind(forward ? standard.AddHours(-1) : standard, DateTimeKind.Utc);
    }

    /// <summary>Whether the clocks are forward on a day.</summary>
    public static bool IsSummerTime(DateOnly date, SummerTime summer) =>
        ToUtc(date, new TimeOnly(12, 0), 0, summer) != DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc);

    /// <summary>The way to the sun in the model, a unit vector: east along the plan as it is turned from true north, up.</summary>
    public static (double X, double Y, double Z) Direction(double altitude, double azimuth, double trueNorth)
    {
        const double Rad = Math.PI / 180;
        var bearing = (azimuth + trueNorth) * Rad;
        var (alt, level) = (altitude * Rad, Math.Cos(altitude * Rad));
        return (level * Math.Sin(bearing), level * Math.Cos(bearing), Math.Sin(alt));
    }

    private static double Normalise(double degrees) => (degrees % 360 + 360) % 360;

    private static DateOnly LastSunday(int year, int month)
    {
        var day = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (day.DayOfWeek != DayOfWeek.Sunday) day = day.AddDays(-1);
        return day;
    }

    private static DateOnly NthSunday(int year, int month, int nth)
    {
        var day = new DateOnly(year, month, 1);
        while (day.DayOfWeek != DayOfWeek.Sunday) day = day.AddDays(1);
        return day.AddDays(7 * (nth - 1));
    }
}
