using System.Globalization;

namespace BIMDesigner.Core.Parameters;

/// <summary>
/// Turns stored parameter values into display text and back again.
///
/// The model always stores millimetres, but people type and read metres - or feet and inches
/// in a US project - so every length field accepts "5250", "5250 mm", "5.25 m" and "525 cm",
/// and 12'-6", 12' 6 1/2", 150", and shows the friendliest form in the project's units.
/// </summary>
public static class ParameterFormatter
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Shown where a quantity has no value at all, as distinct from a value of zero.
    ///
    /// A blank cell reads as "nobody filled this in" and a zero reads as a measurement. An
    /// unmeasurable quantity is neither, and in a schedule the difference matters: zero square
    /// metres is a claim about a room, and a dash is not.
    /// </summary>
    public const string NoValue = "—";

    public static string Format(ParameterDataType dataType, object? value) => value switch
    {
        null => dataType
            is ParameterDataType.Length
            or ParameterDataType.Area
            or ParameterDataType.Volume
            or ParameterDataType.Angle
            or ParameterDataType.Number
            or ParameterDataType.Integer
            or ParameterDataType.Currency
            or ParameterDataType.Percentage
                ? NoValue
                : string.Empty,
        _ => dataType switch
        {
            ParameterDataType.Length => Units.FormatLength(Convert.ToDouble(value, Culture)),
            ParameterDataType.Area => Units.FormatArea(Convert.ToDouble(value, Culture)),
            ParameterDataType.Volume => Units.FormatVolume(Convert.ToDouble(value, Culture)),
            ParameterDataType.Angle => Convert.ToDouble(value, Culture).ToString("0.##", Culture) + "°",
            ParameterDataType.Number => Convert.ToDouble(value, Culture).ToString("0.###", Culture),
            ParameterDataType.Integer => Convert.ToInt32(value, Culture).ToString(Culture),
            ParameterDataType.Currency => Convert.ToDecimal(value, Culture).ToString("0.00", Culture),
            ParameterDataType.YesNo => Convert.ToBoolean(value, Culture) ? "Yes" : "No",
            ParameterDataType.Percentage => Convert.ToDouble(value, Culture).ToString("0.##", Culture) + "%",
            _ => value.ToString() ?? string.Empty
        }
    };

    /// <summary>
    /// Parses display text back into a stored value. Returns false and leaves
    /// <paramref name="value"/> null when the text cannot be read, so the caller can keep
    /// the previous value rather than corrupting the model.
    /// </summary>
    public static bool TryParse(ParameterDataType dataType, string? text, out object? value)
    {
        value = null;
        text = text?.Trim() ?? string.Empty;

        switch (dataType)
        {
            case ParameterDataType.Length:
                if (!Units.TryParseLength(text, out var mm)) return false;
                value = mm;
                return true;

            case ParameterDataType.Area:
                if (!Units.TryParseArea(text, out var mm2)) return false;
                value = mm2;
                return true;

            case ParameterDataType.Volume:
                if (!Units.TryParseVolume(text, out var mm3)) return false;
                value = mm3;
                return true;

            case ParameterDataType.Angle:
                if (!double.TryParse(text.TrimEnd('°').Trim(), NumberStyles.Float, Culture, out var deg))
                    return false;
                value = deg;
                return true;

            case ParameterDataType.Number:
                if (!double.TryParse(text, NumberStyles.Float, Culture, out var number)) return false;
                value = number;
                return true;

            case ParameterDataType.Integer:
                if (!int.TryParse(text, NumberStyles.Integer, Culture, out var integer)) return false;
                value = integer;
                return true;

            case ParameterDataType.Currency:
                if (!decimal.TryParse(text, NumberStyles.Currency, Culture, out var money)) return false;
                value = money;
                return true;

            case ParameterDataType.Percentage:
                if (!double.TryParse(text.TrimEnd('%').Trim(), NumberStyles.Float, Culture, out var percent)) return false;
                value = percent;
                return true;

            case ParameterDataType.YesNo:
                switch (text.ToLowerInvariant())
                {
                    case "yes" or "true" or "1": value = true; return true;
                    case "no" or "false" or "0": value = false; return true;
                    default: return false;
                }

            case ParameterDataType.Text:
            case ParameterDataType.MultilineText:
            case ParameterDataType.Url:
            case ParameterDataType.Image:
                value = text;
                return true;

            default:
                return false;
        }
    }

}
