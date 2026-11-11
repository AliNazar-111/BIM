using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>Whether a floor carries load or is finish over something that does (§3.2).</summary>
public enum SlabFunction
{
    Architectural,
    Structural
}

/// <summary>
/// A horizontal layered assembly: a floor, a ceiling or a flat roof.
///
/// It uses the same <see cref="CompoundStructure"/> as a wall, deliberately. A floor build-up
/// is the same kind of thing as a wall build-up - ordered layers, each with a material,
/// a thickness and a function - and sharing it means material takeoff, cost and thermal
/// figures work the same way for both without a second implementation to keep in step.
///
/// For a floor the layers run from the top surface down; for a ceiling and a roof, from the
/// upper surface down likewise, so "first layer" always means the one you would meet coming
/// from above.
/// </summary>
public abstract class SlabType : ElementType
{
    protected SlabType(string name, CompoundStructure structure) : base(name)
    {
        Structure = structure;
    }

    /// <summary>The layers, ordered from the upper surface downward.</summary>
    public CompoundStructure Structure { get; }

    /// <summary>Total build-up thickness in millimetres, derived from the layers.</summary>
    public double Thickness => Structure.TotalWidth;

    public SlabFunction Function { get; set; } = SlabFunction.Architectural;

    public string FireRating { get; set; } = string.Empty;

    /// <summary>Whether it separates one dwelling from another - a compartment floor between flats: a duct through it keeps it fire-separating (Approved Document B 9.12).</summary>
    public bool Separating { get; set; }

    /// <summary>Weighted sound reduction index Rw, in decibels.</summary>
    public double AcousticRating { get; set; }

    /// <summary>Heat transfer coefficient U, in W/m²K.</summary>
    public double HeatTransferCoefficient { get; set; }

    /// <summary>Colour used when a view draws the slab as one body rather than its layers.</summary>
    public ColourRgb CoarseScaleFillColour { get; set; } = new(0x7A, 0x82, 0x8E);

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.ReadOnly(SlabTypeParameters.Thickness, () => Thickness);
        yield return ParameterValue.ReadOnly(SlabTypeParameters.LayerCount, () => Structure.Layers.Count);

        yield return ParameterValue.BindChoice(
            SlabTypeParameters.Function,
            () => EnumText.Humanise(Function),
            v => { if (EnumText.TryParse<SlabFunction>(v, out var f)) Function = f; },
            EnumText.Choices<SlabFunction>());

        yield return ParameterValue.Bind(SlabTypeParameters.FireRating, () => FireRating, v => FireRating = v);
        if (this is FloorType) yield return ParameterValue.Bind(SlabTypeParameters.Separating, () => Separating, v => Separating = v);
        yield return ParameterValue.Bind(SlabTypeParameters.AcousticRating, () => AcousticRating, v => AcousticRating = v);
        yield return ParameterValue.BindScaled(SlabTypeParameters.HeatTransferCoefficient, OpeningTypeParameters.UFactor, Units.UFactorPerUValue, 3,
            () => HeatTransferCoefficient, v => { HeatTransferCoefficient = v; return true; });
    }
}

public sealed class FloorType : SlabType
{
    public FloorType(string name, CompoundStructure structure) : base(name, structure) { }

    public override BuiltInCategory Category => BuiltInCategory.Floors;
}

public sealed class CeilingType : SlabType
{
    public CeilingType(string name, CompoundStructure structure) : base(name, structure) { }

    public override BuiltInCategory Category => BuiltInCategory.Ceilings;
}

public sealed class RoofType : SlabType
{
    public RoofType(string name, CompoundStructure structure) : base(name, structure) { }

    public override BuiltInCategory Category => BuiltInCategory.Roofs;

    /// <summary>
    /// Sloped glazing: the roof laid in glass panes between glazing bars on each of its faces, as a
    /// curtain wall is on a wall's face, rather than built up as a covering. Its layers are the glass;
    /// the bars run up each face, spaced along its eave, and across it, spaced up the slope.
    /// </summary>
    public bool SlopedGlazing { get; set; }

    /// <summary>How far apart the bars running up the slope stand, along the eave, mm: the panes' width.</summary>
    public double UpBarSpacing { get; set; } = 1000;

    /// <summary>How far apart the bars running across the slope stand, measured up the face, mm: the panes' length.</summary>
    public double CrossBarSpacing { get; set; } = 2000;

    /// <summary>How wide a glazing bar's cap is, on the face, mm.</summary>
    public double GlazingBarWidth { get; set; } = 60;

    /// <summary>How deep a glazing bar is, square to the face - its cap standing proud of the glass, the rest under it - mm.</summary>
    public double GlazingBarDepth { get; set; } = 150;

    /// <summary>What the glazing bars are made of.</summary>
    public Guid GlazingBarMaterialId { get; set; }

    /// <summary>What a metre of glazing bar weighs, its cap and pressure plate with it, kg: its maker's figure; 4 kg a typical thermally broken aluminium bar's.</summary>
    public double GlazingBarMass { get; set; } = 4;

    /// <summary>The glass's total solar energy transmittance g (BS EN 410): the share of the sun on it that comes through as heat.</summary>
    public double SolarHeatGainCoefficient { get; set; } = 0.45;

    /// <summary>Why glazing of these sizes could not be laid, or null where it can.</summary>
    public static string? Unlaid(double up, double cross, double width, double depth) =>
        width is < 20 or > 200 ? "A glazing bar is between 20 and 200 mm wide."
        : depth is < 40 or > 500 ? "A glazing bar is between 40 and 500 mm deep."
        : Math.Min(up, cross) < Math.Max(300, 4 * width) ? $"The bars are at least {Math.Max(300, 4 * width):0} mm apart, or the glass between them is lost in them."
        : null;

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in base.GetTypeParameters(document)) yield return parameter;

        yield return ParameterValue.Bind(GlazedRoofParameters.SlopedGlazing, () => SlopedGlazing, v => SlopedGlazing = v);
        if (!SlopedGlazing) yield break;

        yield return ParameterValue.BindValidated(GlazedRoofParameters.UpBarSpacing, () => UpBarSpacing,
            (double v) => Set(Unlaid(v, CrossBarSpacing, GlazingBarWidth, GlazingBarDepth) is null, () => UpBarSpacing = v));
        yield return ParameterValue.BindValidated(GlazedRoofParameters.CrossBarSpacing, () => CrossBarSpacing,
            (double v) => Set(Unlaid(UpBarSpacing, v, GlazingBarWidth, GlazingBarDepth) is null, () => CrossBarSpacing = v));
        yield return ParameterValue.BindValidated(GlazedRoofParameters.BarWidth, () => GlazingBarWidth,
            (double v) => Set(Unlaid(UpBarSpacing, CrossBarSpacing, v, GlazingBarDepth) is null, () => GlazingBarWidth = v));
        yield return ParameterValue.BindValidated(GlazedRoofParameters.BarDepth, () => GlazingBarDepth,
            (double v) => Set(Unlaid(UpBarSpacing, CrossBarSpacing, GlazingBarWidth, v) is null, () => GlazingBarDepth = v));
        yield return ParameterValue.BindValidated(GlazedRoofParameters.BarMass, () => GlazingBarMass, (double v) => Set(v is > 0 and <= 50, () => GlazingBarMass = v));
        yield return ParameterValue.BindValidated(WindowTypeParameters.SolarHeatGain, () => SolarHeatGainCoefficient,
            (double v) => Set(v is >= 0 and <= 1, () => SolarHeatGainCoefficient = v));
        if (document is not null)
            yield return ParameterValue.BindChoice(
                GlazedRoofParameters.BarMaterial,
                () => document.FindMaterial(GlazingBarMaterialId)?.Name ?? "<by category>",
                v => GlazingBarMaterialId = document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty,
                new[] { "<by category>" }.Concat(document.Materials.Select(material => material.Name).OrderBy(name => name)).ToArray());
    }

    private static bool Set(bool valid, Action apply)
    {
        if (valid) apply();
        return valid;
    }
}

public static class GlazedRoofParameters
{
    public static readonly ParameterDefinition SlopedGlazing =
        new("Sloped Glazing", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition UpBarSpacing =
        new("Bars Up the Slope - Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition CrossBarSpacing =
        new("Bars Across the Slope - Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition BarWidth =
        new("Glazing Bar Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition BarDepth =
        new("Glazing Bar Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition BarMass =
        new("Glazing Bar Mass (kg/m)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Structural);

    public static readonly ParameterDefinition BarMaterial =
        new("Glazing Bar Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition GlazedArea =
        new("Glazed Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition BarLength =
        new("Glazing Bar Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Panes =
        new("Panes", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
}

public static class SlabTypeParameters
{
    public static readonly ParameterDefinition Thickness =
        new("Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition LayerCount =
        new("Layers", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Function =
        new("Function", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Separating =
        new("Separates Dwellings (Compartment Floor)", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FireRating =
        new("Fire Rating", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition AcousticRating =
        new("Acoustic Rating (Rw)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);

    public static readonly ParameterDefinition HeatTransferCoefficient =
        new("Heat Transfer Coefficient (U)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);
}
