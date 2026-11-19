using System.Globalization;
using System.Text.RegularExpressions;

namespace BIMDesigner.Core.Materials;

/// <summary>
/// A sound absorber's class by BS EN ISO 11654, by its weighted sound absorption coefficient αw: A 0.90 to 1.00, B 0.80 and 0.85, C 0.60 to
/// 0.75, D 0.30 to 0.55, E 0.15 to 0.25 - below that not classified. What Approved Document E's Method A asks the common parts of a block of
/// flats to be lined with (7.10, 7.11).
/// </summary>
public enum AbsorptionClass
{
    A,
    B,
    C,
    D,
    E
}

public static partial class SoundAbsorption
{
    /// <summary>How a class is written: "Class C".</summary>
    public static string Said(AbsorptionClass value) => $"Class {value}";

    [GeneratedRegex(@"^(?:CLASS)?([A-E])$")]
    private static partial Regex Letter();

    [GeneratedRegex(@"^(?:[αΑ]W|AW|ALPHAW)?=?(0?\.\d+|1(?:\.0+)?|0)$")]
    private static partial Regex Coefficient();

    /// <summary>
    /// Reads a class as it is written - "Class C", "C" - or from αw, the weighted sound absorption coefficient - "0.70", "αw 0.7": A from 0.90,
    /// B from 0.80, C from 0.60, D from 0.30, E from 0.15. False below 0.15, not classified, and for anything else.
    /// </summary>
    public static bool TryParse(string? text, out AbsorptionClass value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var plain = text.Replace(" ", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
        if (Letter().Match(plain) is { Success: true } letter)
        {
            value = Enum.Parse<AbsorptionClass>(letter.Groups[1].Value);
            return true;
        }

        if (Coefficient().Match(plain) is not { Success: true } match
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha) || alpha < 0.15 - 1e-9) return false;
        value = alpha >= 0.90 - 1e-9 ? AbsorptionClass.A : alpha >= 0.80 - 1e-9 ? AbsorptionClass.B : alpha >= 0.60 - 1e-9 ? AbsorptionClass.C
            : alpha >= 0.30 - 1e-9 ? AbsorptionClass.D : AbsorptionClass.E;
        return true;
    }

    /// <summary>A material's class as the project gives it; null where none is - plaster, concrete, plasterboard and glass absorbing too little to be classed.</summary>
    public static AbsorptionClass? Of(Material material) => TryParse(material.SoundAbsorption, out var value) ? value : null;

    /// <summary>Whether a class is the one asked or better: A better than B, and so on.</summary>
    public static bool AtLeast(AbsorptionClass value, AbsorptionClass asked) => value <= asked;
}
