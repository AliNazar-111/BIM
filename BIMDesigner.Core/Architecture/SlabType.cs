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

    /// <summary>Weighted sound reduction index Rw, in decibels.</summary>
    public double AcousticRating { get; set; }

    /// <summary>Heat transfer coefficient U, in W/m²K.</summary>
    public double HeatTransferCoefficient { get; set; }

    /// <summary>Colour used when a view draws the slab as one body rather than its layers.</summary>
    public ColourRgb CoarseScaleFillColour { get; set; } = new(0x7A, 0x82, 0x8E);

    public override IEnumerable<ParameterValue> GetTypeParameters()
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
        yield return ParameterValue.Bind(SlabTypeParameters.AcousticRating, () => AcousticRating, v => AcousticRating = v);
        yield return ParameterValue.Bind(SlabTypeParameters.HeatTransferCoefficient,
            () => HeatTransferCoefficient, v => HeatTransferCoefficient = v);
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
}

public static class SlabTypeParameters
{
    public static readonly ParameterDefinition Thickness =
        new("Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition LayerCount =
        new("Layers", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Function =
        new("Function", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FireRating =
        new("Fire Rating", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition AcousticRating =
        new("Acoustic Rating (Rw)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);

    public static readonly ParameterDefinition HeatTransferCoefficient =
        new("Heat Transfer Coefficient (U)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);
}
