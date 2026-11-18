using System.Globalization;
using System.Text.RegularExpressions;

namespace BIMDesigner.Core.Materials;

/// <summary>
/// A thermoplastic's class for Approved Document B's requirements B2 and B4 (Appendix B, B11 to B13): TP(a) rigid - rigid solid uPVC sheet,
/// solid polycarbonate 3 mm thick at least, or multi-skinned uPVC or polycarbonate class 1 to BS 476-7, or any rigid one passing BS 2782-0
/// Method 508A; TP(a) flexible - flexible products 1 mm thick at most passing BS 5867-2 Type C; TP(b) - rigid solid polycarbonate under 3 mm,
/// or multi-skinned polycarbonate not TP(a) by test.
/// </summary>
public enum ThermoplasticClass
{
    TpaRigid,
    TpaFlexible,
    Tpb
}

public static partial class Thermoplastic
{
    /// <summary>How a class is written: "TP(a) rigid", "TP(a) flexible", "TP(b)".</summary>
    public static string Said(ThermoplasticClass value) => value switch
    {
        ThermoplasticClass.TpaRigid => "TP(a) rigid",
        ThermoplasticClass.TpaFlexible => "TP(a) flexible",
        _ => "TP(b)"
    };

    [GeneratedRegex(@"^TP\(?([AB])\)?(RIGID|FLEXIBLE)?$")]
    private static partial Regex Pattern();

    /// <summary>Reads a class as it is written - "TP(a) rigid", "TPa flexible", "TP(b)", "tpb"; "TP(a)" alone is taken as rigid, the kind a rooflight is.</summary>
    public static bool TryParse(string? text, out ThermoplasticClass value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Pattern().Match(text.Replace(" ", string.Empty).Replace("-", string.Empty).ToUpperInvariant());
        if (!match.Success) return false;
        if (match.Groups[1].Value == "B")
        {
            if (match.Groups[2].Success) return false;
            value = ThermoplasticClass.Tpb;
            return true;
        }

        value = match.Groups[2].Value == "FLEXIBLE" ? ThermoplasticClass.TpaFlexible : ThermoplasticClass.TpaRigid;
        return true;
    }

    /// <summary>Whether a material is a plastic by its name - polycarbonate, PVC, acrylic, GRP and the like.</summary>
    public static bool Plastic(string name) =>
        new[] { "polycarbonate", "pvc", "acrylic", "pmma", "plastic", "grp", "perspex", "polyester", "etfe", "ptfe" }.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*mm", RegexOptions.IgnoreCase)]
    private static partial Regex Thickness();

    /// <summary>
    /// A material's class taken from its name as B13 sets it out - and by what: uPVC sheet TP(a) rigid (B13 a i); polycarbonate multi-skinned
    /// TP(b) (B13 c i, untested), solid TP(a) rigid where its name gives it 3 mm thick or more (B13 a ii), TP(b) where less or not given. Null for
    /// any other material, whose class is to be given.
    /// </summary>
    public static (ThermoplasticClass Class, string By)? Typical(string name)
    {
        bool Has(params string[] words) => words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("polycarbonate"))
        {
            if (Has("multiwall", "multi-wall", "twinwall", "twin-wall", "triplewall", "multi-skin", "multiskin", "twin skin", "double skin", "cellular"))
                return (ThermoplasticClass.Tpb, "multi-skinned polycarbonate, not shown TP(a) by test - Approved Document B, B13 c i");
            var thick = Thickness().Match(name) is { Success: true } match && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) ? mm : (double?)null;
            return thick >= 3 - 1e-9
                ? (ThermoplasticClass.TpaRigid, $"solid polycarbonate {thick:0.#} mm thick, 3 mm at least - Approved Document B, B13 a ii")
                : (ThermoplasticClass.Tpb, $"solid polycarbonate {(thick is { } given ? $"{given:0.#} mm thick, under 3 mm" : "of no thickness given, taken as under 3 mm")} - Approved Document B, B13 c i");
        }

        if (Has("upvc", "pvc-u", "pvc")) return (ThermoplasticClass.TpaRigid, "rigid solid uPVC sheet - Approved Document B, B13 a i");
        return null;
    }

    /// <summary>A material's class: the one the project gives it, else the one its name has it by; null where neither.</summary>
    public static (ThermoplasticClass Class, string By)? Of(Material material) =>
        TryParse(material.Thermoplastic, out var declared) ? (declared, "as given") : Typical(material.Name);
}
