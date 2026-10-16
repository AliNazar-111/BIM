using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How much heat a built-up wall, floor or roof lets through: its U-value worked out from its
/// layers as BS EN ISO 6946 works it - each layer's thickness over its material's conductivity,
/// an unventilated air gap 0.18 m²K/W at 25 mm or more and less below, and the surfaces' own
/// resistances - and Approved Document L's limits for a new dwelling's (Table 4.1).
/// </summary>
public static class Thermal
{
    /// <summary>Which way heat flows through it, which sets its surfaces' resistances: across a wall, up through a roof, down through a floor.</summary>
    public enum Flow { Horizontal, Upward, Downward }

    /// <summary>The inside and outside surfaces' resistances, m²K/W (BS EN ISO 6946 Table 7).</summary>
    public static (double Inside, double Outside) Surfaces(Flow flow) => flow switch
    {
        Flow.Upward => (0.10, 0.04),
        Flow.Downward => (0.17, 0.04),
        _ => (0.13, 0.04)
    };

    /// <summary>An unventilated air gap's resistance by its thickness (BS EN ISO 6946 Table 8, heat flowing across it).</summary>
    public static double AirGap(double thickness) => thickness switch
    {
        >= 25 => 0.18,
        >= 15 => 0.17,
        >= 10 => 0.15,
        >= 7 => 0.13,
        >= 5 => 0.11,
        _ => 0
    };

    /// <summary>Whether a material is still air - a cavity - rather than a solid: as light as air.</summary>
    private static bool Air(Material material) => material.Density < 5;

    /// <summary>
    /// Its U-value, W/m²K, from its layers; null where one of them is made of a material whose
    /// conductivity is not known, or it has no layers.
    /// </summary>
    public static double? UValue(BimDocument document, CompoundStructure structure, Flow flow = Flow.Horizontal)
    {
        if (structure.Layers.Count == 0) return null;

        var (inside, outside) = Surfaces(flow);
        var resistance = inside + outside;
        foreach (var layer in structure.Layers.Where(layer => layer.Thickness > 0))
        {
            if (document.FindMaterial(layer.MaterialId) is not { } material) return null;
            if (Air(material)) resistance += AirGap(layer.Thickness);
            else if (material.ThermalConductivity > 0) resistance += layer.Thickness / 1000 / material.ThermalConductivity;
            else return null;
        }

        return 1 / resistance;
    }

    /// <summary>Approved Document L's limit for a new dwelling's wall, roof and floor, W/m²K (Table 4.1).</summary>
    public const double WallLimit = 0.26, RoofLimit = 0.16, FloorLimit = 0.18;
}
