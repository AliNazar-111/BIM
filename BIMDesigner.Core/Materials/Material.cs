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

    // --- Identity ---------------------------------------------------------------

    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>Uniclass / OmniClass / MasterFormat code (section 7).</summary>
    public string ClassificationCode { get; set; } = string.Empty;

    /// <summary>Cost per cubic metre, used by quantity takeoff.</summary>
    public decimal CostPerCubicMetre { get; set; }

    public override string ToString() => Name;
}
