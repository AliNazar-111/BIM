using System.Text.RegularExpressions;

namespace BIMDesigner.Core.Materials;

/// <summary>
/// A reaction to fire class by BS EN 13501-1 - A1, A2, B, C, D, E or F - with its production of smoke, s1 to s3, and of flaming droplets,
/// d0 to d2, where it has them: what Approved Document B's linings (Table 4.1) and outside walls (Table 10.1, Regulation 7(2)) are judged by.
/// A1 is the best on every count; E has no smoke class, and is taken as s3; F is the worst.
/// </summary>
public readonly partial record struct Euroclass(int Rank, int Smoke, int Droplets)
{
    private static readonly string[] Letters = { "A1", "A2", "B", "C", "D", "E", "F" };

    public static readonly Euroclass A1 = new(0, 1, 0);
    public static readonly Euroclass A2s1d0 = new(1, 1, 0);
    public static readonly Euroclass A2s3d2 = new(1, 3, 2);
    public static readonly Euroclass Bs3d2 = new(2, 3, 2);
    public static readonly Euroclass Cs3d2 = new(3, 3, 2);
    public static readonly Euroclass Ds3d2 = new(4, 3, 2);

    /// <summary>Whether it is as good as another class or better on every count: its letter, its smoke and its droplets.</summary>
    public bool AtLeast(Euroclass asked) => Rank <= asked.Rank && Smoke <= asked.Smoke && Droplets <= asked.Droplets;

    /// <summary>As BS EN 13501-1 writes it: "A1", "A2-s1, d0", "B-s3, d2", "E", "E-d2", "F".</summary>
    public override string ToString() => Rank switch
    {
        0 => "A1",
        5 => Droplets == 2 ? "E-d2" : "E",
        6 => "F",
        _ => $"{Letters[Rank]}-s{Smoke}, d{Droplets}"
    };

    [GeneratedRegex(@"^(A1|A2|B|C|D|E|F)(?:-?S([123]))?(?:,?-?D([012]))?$")]
    private static partial Regex Pattern();

    /// <summary>Reads a class as it is written - "A2-s1, d0", "b-s3,d2", "E" - its smoke and droplets the worst where a class that has them leaves them out.</summary>
    public static bool TryParse(string? text, out Euroclass value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Pattern().Match(text.Replace(" ", string.Empty).ToUpperInvariant());
        if (!match.Success) return false;
        var rank = Array.IndexOf(Letters, match.Groups[1].Value);
        var smoke = match.Groups[2].Success ? match.Groups[2].Value[0] - '0' : 3;
        var droplets = match.Groups[3].Success ? match.Groups[3].Value[0] - '0' : 2;
        value = rank switch
        {
            0 => A1,
            5 => new Euroclass(5, 3, match.Groups[3].Success ? droplets : 0),
            6 => new Euroclass(6, 3, 2),
            _ => new Euroclass(rank, smoke, droplets)
        };
        return true;
    }

    /// <summary>
    /// A material's class taken from its name where it is classed without testing - what, and by which Commission Decision: concrete, brick,
    /// block, stone and slate, plasters and mortars, clay and concrete tiles, glass and metals A1 (96/603/EC); gypsum plasterboard A2-s1, d0
    /// (2006/673/EC); structural timber D-s2, d0 (2005/610/EC); solid wood cladding D-s2, d2 (2006/213/EC, the worst of its mountings).
    /// Null for anything else - plastics, insulation boards, coated metals, fibre cement - whose maker's declared class is to be given.
    /// </summary>
    public static (Euroclass Class, string By)? Typical(string name)
    {
        bool Has(params string[] words) => words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("air cavity", "pir", "polyisocyanurate", "polyurethane", "polystyrene", "eps", "xps", "phenolic", "pvc", "plastic", "composite", "insulation",
                "insulated", "fibre cement", "coated", "membrane", "felt", "bitumen", "spandrel", "carpet", "vinyl", "wool"))
            return null;
        if (Has("plasterboard", "gypsum board"))
            return (A2s1d0, "gypsum plasterboard, Commission Decision 2006/673/EC");
        if (Has("cedar", "larch", "cladding", "weatherboard"))
            return (new Euroclass(4, 2, 2), "solid wood cladding, Commission Decision 2006/213/EC");
        if (Has("timber", "softwood", "hardwood", "glulam", "oak", "wood"))
            return (new Euroclass(4, 2, 0), "structural timber, Commission Decision 2005/610/EC");
        if (Has("concrete", "brick", "block", "aircrete", "stone", "slate", "granite", "limestone", "sandstone", "marble", "plaster", "render", "mortar",
                "screed", "cement", "clay", "ceramic", "tile", "glass", "steel", "iron", "aluminium", "copper", "zinc", "lead"))
            return (A1, "Commission Decision 96/603/EC, classed A1 without testing");
        return null;
    }

    /// <summary>A material's class: the one its maker declares and the project gives it, else the one its name has it by; null where neither.</summary>
    public static (Euroclass Class, string By)? Of(Material material) =>
        TryParse(material.ReactionToFire, out var declared) ? (declared, "as declared") : Typical(material.Name);
}
