using System.Text.RegularExpressions;

namespace BIMDesigner.Core.Materials;

/// <summary>
/// A roof covering's external fire performance by BS EN 13501-5, tested to CEN/TS 1187 test 4: BROOF(t4) the best, then CROOF(t4), DROOF(t4),
/// EROOF(t4) and FROOF(t4) - what Approved Document B (12.3, Table 12.1) keeps from the relevant boundary.
/// </summary>
public readonly partial record struct RoofDesignation(int Rank)
{
    public static readonly RoofDesignation B = new(0), C = new(1), D = new(2), E = new(3), F = new(4);

    public override string ToString() => $"{"BCDEF"[Rank]}ROOF(t4)";

    [GeneratedRegex(@"^([BCDEF])(?:ROOF)?(?:\(?T4\)?)?$")]
    private static partial Regex Pattern();

    /// <summary>Reads a designation as it is written - "BROOF(t4)", "Broof (t4)", "B".</summary>
    public static bool TryParse(string? text, out RoofDesignation value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Pattern().Match(text.Replace(" ", string.Empty).Replace("-", string.Empty).ToUpperInvariant());
        if (!match.Success) return false;
        value = new RoofDesignation("BCDEF".IndexOf(match.Groups[1].Value[0]));
        return true;
    }

    /// <summary>
    /// A covering's designation taken from its material's name where Approved Document B gives it without testing - and by what: slates, tiles,
    /// fibre cement slates, metal sheet and concrete BROOF(t4), as Commission Decision 2000/553/EC has them (12.3); unwired glass 4 mm thick at
    /// least BROOF(t4) (12.8); thatch and wood shingles EROOF(t4) unless shown better (12.9). Null for anything else - a felt, a membrane, a
    /// plastic sheet - whose maker's designation is to be given.
    /// </summary>
    public static (RoofDesignation Designation, string By)? Typical(string name, double thickness)
    {
        bool Has(params string[] words) => words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("thatch", "shingle")) return (E, "thatch or wood shingles, Approved Document B 12.9");
        if (Has("felt", "membrane", "bitumen", "pvc", "plastic", "polycarbonate", "epdm", "tpo", "grp", "rubber", "plaster", "render")) return null;
        if (Has("glass")) return thickness >= 4 - 1e-6 ? (B, "unwired glass 4 mm thick at least, Approved Document B 12.8") : null;
        if (Has("slate", "tile", "fibre cement", "copper", "zinc", "steel", "aluminium", "concrete"))
            return (B, "Commission Decision 2000/553/EC, without testing");
        return null;
    }

    /// <summary>A covering material's designation: the one its maker declares and the project gives it, else the one its name has it by; null where neither.</summary>
    public static (RoofDesignation Designation, string By)? Of(Material material, double thickness) =>
        TryParse(material.RoofCovering, out var declared) ? (declared, "as declared") : Typical(material.Name, thickness);
}
