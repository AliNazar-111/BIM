using System.Globalization;
using System.Text.RegularExpressions;

namespace BIMDesigner.Core;

/// <summary>The units a project is shown and typed in: metric, or US feet and inches.</summary>
public enum UnitSystem
{
    /// <summary>Millimetres and metres, square and cubic metres.</summary>
    Metric,

    /// <summary>Feet and inches to the nearest sixteenth - 12'-6 1/2" - square and cubic feet.</summary>
    Imperial
}

/// <summary>
/// Every length inside the model is millimetres, every area mm² and every volume mm³,
/// stored as a double. Conversions live here only - never convert ad-hoc elsewhere.
///
/// What is shown and typed is in the project's units: metric, or feet and inches. The model
/// itself never changes - a wall 3658 long is 12'-0" in a US project and 3.66 m in an English
/// one - so a project can be shown either way, and the rules it is checked against stay in
/// their own units underneath.
/// </summary>
public static class Units
{
    public const double MillimetresPerMetre = 1000.0;
    public const double SquareMillimetresPerSquareMetre = 1_000_000.0;
    public const double CubicMillimetresPerCubicMetre = 1_000_000_000.0;

    public const double MillimetresPerInch = 25.4;
    public const double MillimetresPerFoot = 304.8;
    public const double SquareMillimetresPerSquareFoot = MillimetresPerFoot * MillimetresPerFoot;
    public const double CubicMillimetresPerCubicFoot = SquareMillimetresPerSquareFoot * MillimetresPerFoot;

    [ThreadStatic] private static UnitSystem? _system;

    /// <summary>
    /// The units shown and typed now: the open project's, set when it opens. Held for the
    /// thread the project is worked on, so a test - or another window's thread - is not
    /// changed by it.
    /// </summary>
    public static UnitSystem System
    {
        get => _system ?? UnitSystem.Metric;
        set => _system = value;
    }

    public static bool Imperial => System == UnitSystem.Imperial;

    /// <summary>The units in force until what is returned is disposed: for a piece of work in another project's units.</summary>
    public static IDisposable Use(UnitSystem system)
    {
        var was = _system;
        _system = system;
        return new Restore(() => _system = was);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public static double MetresToMm(double metres) => metres * MillimetresPerMetre;

    public static double MmToMetres(double mm) => mm / MillimetresPerMetre;

    public static double SquareMmToSquareMetres(double mm2) => mm2 / SquareMillimetresPerSquareMetre;

    public static double CubicMmToCubicMetres(double mm3) => mm3 / CubicMillimetresPerCubicMetre;

    /// <summary>Formats a stored mm value in the project's units: 5250 -> "5.25 m", 200 -> "200 mm"; or 12'-6 1/2", 6".</summary>
    public static string FormatLength(double mm) =>
        Imperial ? FeetAndInches(mm)
        : Math.Abs(mm) >= MillimetresPerMetre
            ? $"{MmToMetres(mm):0.00} m"
            : $"{mm:0.##} mm";

    /// <summary>Formats a stored mm² value: square metres, or square feet in a US project.</summary>
    public static string FormatArea(double mm2) =>
        Imperial ? $"{mm2 / SquareMillimetresPerSquareFoot:#,0.#} sq ft" : $"{SquareMmToSquareMetres(mm2):0.00} m²";

    /// <summary>Formats a stored mm³ value: cubic metres, or cubic feet in a US project.</summary>
    public static string FormatVolume(double mm3) =>
        Imperial ? $"{mm3 / CubicMillimetresPerCubicFoot:#,0.#} cu ft" : $"{CubicMmToCubicMetres(mm3):0.000} m³";

    /// <summary>A size as a UK drawing says it, in millimetres - 1100 mm - or as a US one does, 3'-7 5/16".</summary>
    public static string FormatMm(double mm) => Millimetres(mm, "0");

    // What is said in a message, in the words it was always said in for a metric project - so
    // many places of millimetres or metres, square or cubic metres - and in feet and inches,
    // square and cubic feet, in a US one.

    /// <summary>A US rule's figure in millimetres put after it in brackets - 6 in (152) - where the project is metric; nothing where it is in feet and inches already.</summary>
    public static string MetricAside(string mm) => Imperial ? string.Empty : $" ({mm})";

    /// <summary>A rule's figure in millimetres with its inches in brackets - 196 (7¾ in) - where the project is metric; the inches alone, 7¾ in, where it is in feet and inches.</summary>
    public static string Figure(string mm, string inches) => Imperial ? inches : $"{mm} ({inches})";

    /// <summary>A size written bare, as a UK drawing does - 150, or 187.5 to "0.#" - or in feet and inches, 6"; signed where the format signs it, "+0;-0".</summary>
    public static string Bare(double mm, string format = "0") =>
        Imperial ? (format.StartsWith('+') && FeetAndInches(mm) is var written && !written.StartsWith('-') && written != "0\"" ? "+" + written : FeetAndInches(mm))
        : mm.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>A small area said in mm² to a format - "2,800 mm²" - or in square inches, "4.34 sq in".</summary>
    public static string SquareMillimetres(double mm2, string format) =>
        Imperial ? $"{mm2 / (MillimetresPerInch * MillimetresPerInch):#,0.##} sq in" : $"{mm2.ToString(format, CultureInfo.CurrentCulture)} mm²";

    /// <summary>An opening's area along a length - a soffit's ventilation - "25,000 mm² of air a metre", or in square inches a foot.</summary>
    public static string AreaPerLength(double mm2PerMetre, string what = "") =>
        Imperial
            ? $"{mm2PerMetre / (MillimetresPerInch * MillimetresPerInch) * MillimetresPerFoot / MillimetresPerMetre:#,0.#} sq in{what} a foot"
            : $"{mm2PerMetre:#,0} mm²{what} a metre";

    /// <summary>
    /// A level, signed: in metres to three places - "+0.000", "-0.450" - or in feet and inches as a
    /// US drawing writes an elevation, "+0'-0"", "-0'-6"", "+10'-1 1/8"".
    /// </summary>
    public static string Elevation(double mm)
    {
        if (!Imperial)
        {
            var rounded = Math.Round(mm / MillimetresPerMetre, 3);
            return $"{(rounded < 0 ? "-" : "+")}{Math.Abs(rounded):0.000}";
        }

        var written = FeetAndInches(Math.Abs(mm));
        var sign = mm < 0 && written != "0\"" ? "-" : "+";
        return sign + (written.Contains('\'') ? written : $"0'-{written}");
    }

    /// <summary>A length said in millimetres to a format - "1100 mm" - or in feet and inches.</summary>
    public static string Millimetres(double mm, string format) =>
        Imperial ? FeetAndInches(mm) : $"{mm.ToString(format, CultureInfo.CurrentCulture)} mm";

    /// <summary>A length in mm said in metres to a format - "2.40 m" - or in feet and inches.</summary>
    public static string Metres(double mm, string format) =>
        Imperial ? FeetAndInches(mm) : $"{(mm / MillimetresPerMetre).ToString(format, CultureInfo.CurrentCulture)} m";

    /// <summary>An area in mm² said in square metres to a format - "12.5 m²" - or in square feet.</summary>
    public static string SquareMetres(double mm2, string format) =>
        Imperial ? $"{mm2 / SquareMillimetresPerSquareFoot:#,0.#} sq ft" : $"{(mm2 / SquareMillimetresPerSquareMetre).ToString(format, CultureInfo.CurrentCulture)} m²";

    /// <summary>An area already in square metres, said to a format - "12.5 m²" - or in square feet.</summary>
    public static string OfSquareMetres(double squareMetres, string format) => SquareMetres(squareMetres * SquareMillimetresPerSquareMetre, format);

    /// <summary>A volume in mm³ said in cubic metres to a format - "3.2 m³" - or in cubic feet.</summary>
    public static string CubicMetres(double mm3, string format) =>
        Imperial ? $"{mm3 / CubicMillimetresPerCubicFoot:#,0.#} cu ft" : $"{(mm3 / CubicMillimetresPerCubicMetre).ToString(format, CultureInfo.CurrentCulture)} m³";

    /// <summary>A volume already in cubic metres, said to a format - "3.2 m³" - or in cubic feet.</summary>
    public static string OfCubicMetres(double cubicMetres, string format) => CubicMetres(cubicMetres * CubicMillimetresPerCubicMetre, format);

    /// <summary>Pounds a square foot in a kilopascal.</summary>
    public const double PoundsPerSquareFootPerKilopascal = 20.8854;

    /// <summary>A pressure in kPa said to a format - "0.85 kPa" - or in pounds a square foot, "17.8 psf".</summary>
    public static string Kilopascals(double kPa, string format) =>
        Imperial ? $"{kPa * PoundsPerSquareFootPerKilopascal:0.#} psf" : $"{kPa.ToString(format, CultureInfo.CurrentCulture)} kPa";

    /// <summary>Litres a second in a cubic foot a minute.</summary>
    public const double LitresPerSecondPerCfm = 0.4719474;

    /// <summary>Pascals in an inch of water column.</summary>
    public const double PascalsPerInchOfWater = 249.0889;

    /// <summary>Btu an hour in a watt.</summary>
    public const double BtuPerHourPerWatt = 3.412142;

    /// <summary>Pounds force in a kilonewton, and pounds a foot in a kilonewton a metre.</summary>
    public const double PoundsPerKilonewton = 224.8089, PoundsPerFootPerKilonewtonPerMetre = 68.52177;

    /// <summary>A U-value in Btu/h·ft²·°F for one in W/m²K.</summary>
    public const double UFactorPerUValue = 0.1761102;

    /// <summary>Litres in a US gallon.</summary>
    public const double LitresPerGallon = 3.785412;

    /// <summary>An airflow in l/s said to a format - "15 l/s" - or in cubic feet a minute, "32 cfm".</summary>
    public static string Airflow(double litresPerSecond, string format) =>
        Imperial ? $"{litresPerSecond / LitresPerSecondPerCfm:#,0} cfm" : $"{litresPerSecond.ToString(format, CultureInfo.CurrentCulture)} l/s";

    /// <summary>An air pressure in Pa said to a format - "60 Pa" - or in inches of water, "0.24 in. w.c.".</summary>
    public static string Pascals(double pascals, string format) =>
        Imperial ? $"{pascals / PascalsPerInchOfWater:0.00} in. w.c." : $"{pascals.ToString(format, CultureInfo.CurrentCulture)} Pa";

    /// <summary>Heat in W said to a format - "1,250 W" - or in Btu an hour, "4,265 Btu/h".</summary>
    public static string Watts(double watts, string format) =>
        Imperial ? $"{watts * BtuPerHourPerWatt:#,0} Btu/h" : $"{watts.ToString(format, CultureInfo.CurrentCulture)} W";

    /// <summary>Heat a square metre said to a format - "70 W/m²" - or a square foot, "22 Btu/h·sq ft".</summary>
    public static string WattsPerSquareMetre(double watts, string format) =>
        Imperial ? $"{watts * BtuPerHourPerWatt / 10.76391:0} Btu/h·sq ft" : $"{watts.ToString(format, CultureInfo.CurrentCulture)} W/m²";

    /// <summary>A temperature in °C said to a format - "55 °C" - or in °F, "131 °F".</summary>
    public static string Celsius(double celsius, string format) =>
        Imperial ? $"{celsius * 1.8 + 32:0} °F" : $"{celsius.ToString(format, CultureInfo.CurrentCulture)} °C";

    /// <summary>A heating system's flow and return - "55/45 °C" - or in °F, "131/113 °F".</summary>
    public static string FlowAndReturn(double flow, double back) =>
        Imperial ? $"{flow * 1.8 + 32:0}/{back * 1.8 + 32:0} °F" : $"{flow:0}/{back:0} °C";

    /// <summary>A difference of temperature in K said to a format - "9 K" - or in °F, "16 °F".</summary>
    public static string Kelvins(double kelvins, string format) =>
        Imperial ? $"{kelvins * 1.8:0.#} °F" : $"{kelvins.ToString(format, CultureInfo.CurrentCulture)} K";

    /// <summary>A U-value in W/m²K said to a format - "U 0.18 W/m²K" - or as a US U-factor, "U-factor 0.032".</summary>
    public static string UValue(double u, string format) =>
        Imperial ? $"U-factor {u * UFactorPerUValue:0.000}" : $"U {u.ToString(format, CultureInfo.CurrentCulture)} W/m²K";

    /// <summary>A force in kN said to a format - "2.4 kN" - or in pounds, "540 lb".</summary>
    public static string Kilonewtons(double kN, string format) =>
        Imperial ? $"{kN * PoundsPerKilonewton:#,0} lb" : $"{kN.ToString(format, CultureInfo.CurrentCulture)} kN";

    /// <summary>A load along a length in kN/m said to a format - "12.5 kN/m" - or in pounds a foot, "857 lb/ft".</summary>
    public static string KilonewtonsPerMetre(double kNm, string format) =>
        Imperial ? $"{kNm * PoundsPerFootPerKilonewtonPerMetre:#,0} lb/ft" : $"{kNm.ToString(format, CultureInfo.CurrentCulture)} kN/m";

    /// <summary>A speed in m/s said to a format - "0.8 m/s" - or in feet a second, "2.6 ft/s".</summary>
    public static string MetresPerSecond(double speed, string format) =>
        Imperial ? $"{speed / 0.3048:0.0} ft/s" : $"{speed.ToString(format, CultureInfo.CurrentCulture)} m/s";

    /// <summary>A pipe's pressure loss in Pa a metre said to a format - "300 Pa/m" - or in feet of head a 100 ft, "3.1 ft/100 ft".</summary>
    public static string PascalsPerMetre(double pascals, string format) =>
        Imperial ? $"{pascals / 9806.65 * 100:0.0} ft/100 ft" : $"{pascals.ToString(format, CultureInfo.CurrentCulture)} Pa/m";

    /// <summary>Water's flow in l/h said to a format - "430 l/h" - or in US gallons a minute, "1.9 gpm".</summary>
    public static string LitresPerHour(double litres, string format) =>
        Imperial ? $"{litres / LitresPerGallon / 60:0.0} gpm" : $"{litres.ToString(format, CultureInfo.CurrentCulture)} l/h";

    /// <summary>A fall in mm a metre said to a format - "12 mm a metre" - or in inches a foot, "1/8" a foot".</summary>
    public static string Fall(double mmPerMetre, string format) =>
        Imperial ? FormatFall(mmPerMetre) : $"{mmPerMetre.ToString(format, CultureInfo.CurrentCulture)} mm a metre";

    /// <summary>A long run in mm said in metres to a format - "85 m" - or in whole feet, "279 ft".</summary>
    public static string Run(double mm, string format) =>
        Imperial ? $"{mm / MillimetresPerFoot:#,0} ft" : $"{(mm / MillimetresPerMetre).ToString(format, CultureInfo.CurrentCulture)} m";

    /// <summary>A pipe's fall, held as mm a metre: "20 mm a metre", or in a US project "1/4" a foot".</summary>
    public static string FormatFall(double mmPerMetre) =>
        Imperial ? $"{FallText(mmPerMetre)} a foot" : $"{mmPerMetre:0.#} mm a metre";

    /// <summary>A fall as a box shows it: mm a metre, 20; or inches a foot, 1/4".</summary>
    public static string FallText(double mmPerMetre) =>
        Imperial ? FeetAndInches(mmPerMetre / MillimetresPerMetre * MillimetresPerFoot) : $"{mmPerMetre:0.#}";

    /// <summary>A fall as typed, in mm a metre: in metric "20"; in a US project inches a foot - 1/4, 1/4", 0.25.</summary>
    public static bool TryParseFall(string? text, out double mmPerMetre)
    {
        mmPerMetre = 0;
        text = text?.Trim() ?? string.Empty;
        if (!Imperial)
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out mmPerMetre)
                   || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out mmPerMetre);

        if (!text.EndsWith('"') && !text.EndsWith("in", StringComparison.OrdinalIgnoreCase)) text += "\"";
        if (!TryParseLength(text, out var perFoot)) return false;
        mmPerMetre = perFoot / MillimetresPerFoot * MillimetresPerMetre;
        return true;
    }

    /// <summary>
    /// A length in feet and inches as a US drawing writes it, to the nearest sixteenth of an
    /// inch: 12'-6 1/2", 12'-0", 6 1/2", 1/4". Under a foot, inches alone.
    /// </summary>
    public static string FeetAndInches(double mm)
    {
        var sixteenths = (long)Math.Round(Math.Abs(mm) / MillimetresPerInch * 16, MidpointRounding.AwayFromZero);
        var sign = mm < 0 && sixteenths > 0 ? "-" : string.Empty;
        var feet = sixteenths / (12 * 16);
        var inches = sixteenths % (12 * 16) / 16;
        var fraction = (int)(sixteenths % 16);
        var part = Fraction(fraction);
        var inchText = part.Length == 0 ? $"{inches}\"" : inches == 0 && feet == 0 ? $"{part}\"" : $"{inches} {part}\"";
        return feet > 0 ? $"{sign}{feet}'-{inchText}" : sign + inchText;
    }

    /// <summary>So many sixteenths in lowest terms: 8 -> "1/2", 4 -> "1/4", 0 -> "".</summary>
    private static string Fraction(int sixteenths)
    {
        if (sixteenths == 0) return string.Empty;
        var (top, bottom) = (sixteenths, 16);
        while (top % 2 == 0)
        {
            top /= 2;
            bottom /= 2;
        }

        return $"{top}/{bottom}";
    }

    /// <summary>
    /// A length as typed, in mm: in metric "2400", "2.4 m", "240 cm"; in feet and inches -
    /// either way, whatever the project's units - 12'-6 1/2", 12' 6", 12'6", 12', 6", 6 1/2",
    /// 1/2", 12.5', 150", "12 ft 6 in". A bare number is millimetres in a metric project and
    /// feet in a US one, as a US drawing's are. False where it cannot be read.
    /// </summary>
    public static bool TryParseLength(string? text, out double mm)
    {
        mm = 0;
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0) return false;

        // Metric, with its unit.
        foreach (var (suffix, factor) in new[] { ("mm", 1.0), ("cm", 10.0), ("m", MillimetresPerMetre) })
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && !text.EndsWith("in", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(text[..^suffix.Length].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var metric)) return false;
                mm = metric * factor;
                return true;
            }

        // Feet and inches, with their marks or words.
        var match = FeetInches.Match(text);
        if (match.Success && (match.Groups["feet"].Success || match.Groups["inches"].Success))
        {
            var sign = match.Groups["minus"].Success ? -1 : 1;
            var feet = match.Groups["feet"].Success ? Number(match.Groups["feet"].Value) : 0;
            var inches = match.Groups["inches"].Success ? Inches(match.Groups["inches"].Value) : 0;
            if (double.IsNaN(feet) || double.IsNaN(inches)) return false;
            mm = sign * (feet * MillimetresPerFoot + inches * MillimetresPerInch);
            return true;
        }

        // A bare number: the project's own unit.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) return false;
        mm = Imperial ? bare * MillimetresPerFoot : bare;
        return true;
    }

    /// <summary>An area as typed, in mm²: "12.5 m²", "1200 sq ft", "1200 SF"; bare, the project's own unit.</summary>
    public static bool TryParseArea(string? text, out double mm2) =>
        TryParseScaled(text, new[]
        {
            ("mm²", 1.0), ("mm2", 1.0), ("m²", SquareMillimetresPerSquareMetre), ("m2", SquareMillimetresPerSquareMetre),
            ("sq ft", SquareMillimetresPerSquareFoot), ("ft²", SquareMillimetresPerSquareFoot), ("ft2", SquareMillimetresPerSquareFoot), ("sf", SquareMillimetresPerSquareFoot)
        }, Imperial ? SquareMillimetresPerSquareFoot : 1, out mm2);

    /// <summary>A volume as typed, in mm³: "3 m³", "100 cu ft", "4 cu yd"; bare, the project's own unit.</summary>
    public static bool TryParseVolume(string? text, out double mm3) =>
        TryParseScaled(text, new[]
        {
            ("mm³", 1.0), ("mm3", 1.0), ("m³", CubicMillimetresPerCubicMetre), ("m3", CubicMillimetresPerCubicMetre),
            ("cu ft", CubicMillimetresPerCubicFoot), ("ft³", CubicMillimetresPerCubicFoot), ("ft3", CubicMillimetresPerCubicFoot), ("cf", CubicMillimetresPerCubicFoot),
            ("cu yd", 27 * CubicMillimetresPerCubicFoot), ("yd³", 27 * CubicMillimetresPerCubicFoot), ("yd3", 27 * CubicMillimetresPerCubicFoot)
        }, Imperial ? CubicMillimetresPerCubicFoot : 1, out mm3);

    private static bool TryParseScaled(string? text, (string Suffix, double Factor)[] units, double bare, out double result)
    {
        result = 0;
        text = text?.Trim() ?? string.Empty;
        var factor = bare;

        // Longest suffix first, so "mm²" is matched before "m²".
        foreach (var (suffix, unitFactor) in units.OrderByDescending(unit => unit.Suffix.Length))
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            factor = unitFactor;
            text = text[..^suffix.Length].Trim();
            break;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        result = number * factor;
        return true;
    }

    // 12'-6 1/2", 12' 6", 12'6", 12', 6", 6 1/2", 1/2", 12.5', 150", 12 ft 6 in, 12ft, 6in, -1'-6"
    private static readonly Regex FeetInches = new(
        @"^\s*(?<minus>-)?\s*(?:(?<feet>\d+(?:\.\d+)?)\s*(?:'|ft|feet|foot)\s*-?\s*)?(?:(?<inches>(?:\d+(?:\.\d+)?)?(?:\s*\d+/\d+)?)\s*(?:""|in|inch|inches)\s*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    /// <summary>Inches as written: "6", "6.5", "6 1/2", "1/2".</summary>
    private static double Inches(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return 0;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var total = 0.0;
        foreach (var part in parts)
        {
            if (part.Split('/') is [var top, var bottom])
            {
                var (n, d) = (Number(top), Number(bottom));
                if (double.IsNaN(n) || double.IsNaN(d) || d == 0) return double.NaN;
                total += n / d;
            }
            else
            {
                var whole = Number(part);
                if (double.IsNaN(whole)) return double.NaN;
                total += whole;
            }
        }

        return total;
    }

    /// <summary>Rounds a mm value onto the nearest grid step, used for snapping.</summary>
    public static double SnapToGrid(double mm, double stepMm) =>
        stepMm <= 0 ? mm : Math.Round(mm / stepMm) * stepMm;
}
