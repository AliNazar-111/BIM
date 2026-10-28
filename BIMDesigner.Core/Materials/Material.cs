namespace BIMDesigner.Core.Materials;

/// <summary>A colour, kept as plain data so Core stays free of any UI framework.</summary>
public readonly record struct ColourRgb(byte R, byte G, byte B)
{
    public static ColourRgb FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) throw new FormatException($"Expected a six-digit hex colour, got '{hex}'.");

        return new ColourRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    /// <summary>Reads a six-digit hex colour, with or without its hash, without throwing on rubbish.</summary>
    public static bool TryParse(string? text, out ColourRgb colour)
    {
        colour = default;
        if (text is null) return false;

        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) return false;

        colour = FromHex(hex);
        return true;
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// A material (specification section 2.1), carrying the four kinds of data the spec calls
/// for: physical, graphical, rendering and identity.
///
/// Physical data feeds energy analysis (6D), identity data feeds cost (5D) and handover
/// (7D), graphical data drives how the material is drawn when a view cuts through it.
/// </summary>
public sealed class Material
{
    public Material(string name)
    {
        Name = name;
    }

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; }

    // --- Physical ---------------------------------------------------------------

    /// <summary>kg/m³.</summary>
    public double Density { get; set; }

    /// <summary>W/mK.</summary>
    public double ThermalConductivity { get; set; }

    // --- Graphical --------------------------------------------------------------

    /// <summary>Colour of the surface seen in elevation and 3D.</summary>
    public ColourRgb SurfaceColour { get; set; } = new(0x9A, 0x9A, 0x9A);

    /// <summary>Colour used where a view cuts through the material, as in a floor plan.</summary>
    public ColourRgb CutColour { get; set; } = new(0x7A, 0x7A, 0x7A);

    // --- Rendering --------------------------------------------------------------

    /// <summary>What its face is laid as - brick in stretcher bond, boards, slates - drawn on it in a realistic view and a rendering.</summary>
    public SurfacePattern Pattern { get; set; }

    /// <summary>One unit of the pattern, mm: a brick's length and height, a board's length and width, a slate's width and gauge.</summary>
    public double UnitLength { get; set; }

    public double UnitHeight { get; set; }

    /// <summary>The joint between the units, mm, and its colour - mortar, grout, the gap between boards.</summary>
    public double JointWidth { get; set; }

    public ColourRgb JointColour { get; set; } = new(0xC8, 0xC2, 0xB4);

    /// <summary>How much of the light it reflects as a mirror would, looked at square on, 0 to 1: matt brick none, glass 0.04 rising at a glancing angle, polished metal more.</summary>
    public double Reflectance { get; set; }

    // --- Identity ---------------------------------------------------------------

    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>Uniclass / OmniClass / MasterFormat code (section 7).</summary>
    public string ClassificationCode { get; set; } = string.Empty;

    /// <summary>Its key in the project's keynotes: what a material keynote pointing at it reads.</summary>
    public string Keynote { get; set; } = string.Empty;

    /// <summary>Cost per cubic metre, used by quantity takeoff.</summary>
    public decimal CostPerCubicMetre { get; set; }

    public override string ToString() => Name;

    /// <summary>The look of its face - its pattern, colours and how it reflects - as one value to copy, compare and put back.</summary>
    public SurfaceFinish Finish
    {
        get => new(SurfaceColour, Pattern, UnitLength, UnitHeight, JointWidth, JointColour, Reflectance);
        set => (SurfaceColour, Pattern, UnitLength, UnitHeight, JointWidth, JointColour, Reflectance) =
            (value.Colour, value.Pattern, value.UnitLength, value.UnitHeight, value.JointWidth, value.JointColour, value.Reflectance);
    }
}

/// <summary>What a material's face is laid as, so it can be drawn so.</summary>
public enum SurfacePattern
{
    /// <summary>Its colour alone: paint, plastic, glass, what is never seen.</summary>
    None,
    Brick,
    Block,
    Stone,
    Render,
    Concrete,
    Boards,
    Tiles,
    Slate,
    PlainTiles,
    InterlockingTiles,
    StandingSeam,
    Metal,
    Grass,
    Gravel,
    Paving,
    Planting
}

/// <summary>A face's look: its colour, pattern, units and joints, and its reflection.</summary>
public readonly record struct SurfaceFinish(
    ColourRgb Colour, SurfacePattern Pattern, double UnitLength, double UnitHeight, double JointWidth, ColourRgb JointColour, double Reflectance);
