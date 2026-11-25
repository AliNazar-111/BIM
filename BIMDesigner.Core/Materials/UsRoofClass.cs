using System.Text.RegularExpressions;

namespace BIMDesigner.Core.Materials;

/// <summary>
/// A roof covering's fire classification in the US: Class A, B or C by ASTM E108 or UL 790, Class A the best (IRC R902.1) - what a townhouse's
/// roof is asked beside a common wall it does not carry a parapet over (R302.2.4). A material gives its maker's class in the same place as a
/// European designation, "Class A"; without one, R902.1's exceptions class a covering Class A without testing - brick, masonry or an exposed
/// concrete deck; slate; and metal sheets and shingles, copper, or clay or concrete tile on a noncombustible deck.
/// </summary>
public readonly partial record struct UsRoofClass(int Rank)
{
    public static readonly UsRoofClass A = new(0), B = new(1), C = new(2);

    public override string ToString() => $"Class {"ABC"[Rank]}";

    [GeneratedRegex(@"^(?:CLASS)?([ABC])(?:\(?(?:ASTME108|UL790)\)?)?$")]
    private static partial Regex Pattern();

    /// <summary>Reads a class as it is written - "Class A", "class b", "C (ASTM E108)".</summary>
    public static bool TryParse(string? text, out UsRoofClass value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Pattern().Match(text.Replace(" ", string.Empty).Replace("-", string.Empty).ToUpperInvariant());
        if (!match.Success) return false;
        value = new UsRoofClass("ABC".IndexOf(match.Groups[1].Value[0]));
        return true;
    }

    /// <summary>
    /// A covering's class from its material's name where R902.1's exceptions give it without testing: brick, masonry or concrete Class A; slate
    /// Class A, over underlayment on any deck; metal, copper, clay or concrete tile Class A only on a noncombustible deck. Null for anything else -
    /// asphalt shingles, wood shakes, a membrane - whose listing's class is to be given.
    /// </summary>
    public static (UsRoofClass Class, string By)? Typical(string name, bool noncombustibleDeck)
    {
        bool Has(params string[] words) => words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("shingle", "shake", "felt", "membrane", "bitumen", "asphalt", "pvc", "plastic", "epdm", "tpo", "polycarbonate", "thatch")) return null;
        if (Has("slate")) return (A, "slate, IRC R902.1 exception 4");
        if (Has("brick", "masonry", "concrete deck")) return (A, "brick, masonry or concrete, IRC R902.1 exception 1");
        if (noncombustibleDeck && Has("tile", "metal", "steel", "copper", "zinc", "aluminum", "aluminium"))
            return (A, "metal, copper, clay or concrete tile on a noncombustible deck, IRC R902.1 exception 2");
        return null;
    }

    /// <summary>A covering material's class: its listing's as the project gives it, else the one its name has it by; null where neither.</summary>
    public static (UsRoofClass Class, string By)? Of(Material material, bool noncombustibleDeck) =>
        TryParse(material.RoofCovering, out var declared) ? (declared, "as listed") : Typical(material.Name, noncombustibleDeck);
}
