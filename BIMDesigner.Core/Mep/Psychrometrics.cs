namespace BIMDesigner.Core.Mep;

/// <summary>
/// Moist air as ASHRAE's Handbook - Fundamentals (chapter 1, Psychrometrics) works it: the
/// atmosphere's pressure at an altitude (eq. 3), water's saturation pressure over liquid water - the
/// Magnus form, within 0.1% of Hyland and Wexler's between 0 and 50 °C - and the humidity ratio of
/// air from its dry and wet bulbs (eq. 33) or its relative humidity (eqs. 22 and 24).
/// </summary>
public static class Psychrometrics
{
    /// <summary>What a kilogram of water vapour weighs against a kilogram of dry air, for its share of the pressure (eq. 22).</summary>
    private const double Ratio = 0.621945;

    /// <summary>The atmosphere's pressure at an altitude, kPa (eq. 3).</summary>
    public static double Pressure(double metres) => 101.325 * Math.Pow(1 - 2.25577e-5 * metres, 5.2559);

    /// <summary>Water vapour's saturation pressure at a temperature, kPa.</summary>
    public static double Saturation(double celsius) => 0.61094 * Math.Exp(17.625 * celsius / (celsius + 243.04));

    /// <summary>The humidity ratio of air at a relative humidity (0 to 1) and temperature, kg of water a kg of dry air.</summary>
    public static double HumidityRatio(double celsius, double relative, double pressure)
    {
        var vapour = relative * Saturation(celsius);
        return Ratio * vapour / (pressure - vapour);
    }

    /// <summary>The humidity ratio of air from its dry and wet bulbs, °C, at a pressure, kPa (eq. 33, the wet bulb above freezing).</summary>
    public static double FromWetBulb(double dry, double wet, double pressure)
    {
        var saturated = Ratio * Saturation(wet) / (pressure - Saturation(wet));
        return Math.Max(0, ((2501 - 2.326 * wet) * saturated - 1.006 * (dry - wet)) / (2501 + 1.86 * dry - 4.186 * wet));
    }
}
