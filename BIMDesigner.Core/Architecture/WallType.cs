using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>Where a wall sits in the building (specification section 3.1).</summary>
public enum WallFunction
{
    Interior,
    Exterior,
    Retaining,
    Foundation,
    Soffit,
    CoreShaft
}

/// <summary>
/// A wall type: the layered assembly and the properties shared by every wall built from it
/// (specification section 13.1, type parameters).
///
/// Change the layers here and every wall of this type in the project changes with it.
/// </summary>
public sealed class WallType : ElementType
{
    public WallType(string name, CompoundStructure structure) : base(name)
    {
        Structure = structure;
    }

    public override BuiltInCategory Category => BuiltInCategory.Walls;

    /// <summary>The layers, ordered exterior face to interior face.</summary>
    public CompoundStructure Structure { get; }

    /// <summary>
    /// A new type with this one's build-up and settings, and its own copies of them, so it can
    /// be changed without touching any wall of this type. How new types are made.
    /// </summary>
    public WallType Duplicate(string name) => new(name, Structure.Clone())
    {
        TypeMark = TypeMark,
        AssemblyCode = AssemblyCode,
        Keynote = Keynote,
        Manufacturer = Manufacturer,
        Url = Url,
        Description = Description,
        Cost = Cost,
        Function = Function,
        FireRating = FireRating,
        AcousticRating = AcousticRating,
        ThermalResistance = ThermalResistance,
        HeatTransferCoefficient = HeatTransferCoefficient,
        WrapAtInserts = WrapAtInserts,
        WrapAtEnds = WrapAtEnds,
        CoarseScaleFillColour = CoarseScaleFillColour
    };

    /// <summary>Total thickness in millimetres, derived from the layers.</summary>
    public double Width => Structure.TotalWidth;

    public WallFunction Function { get; set; } = WallFunction.Interior;

    /// <summary>Fire resistance period, e.g. "60 min" or "2 hr". Free text, as on drawings.</summary>
    public string FireRating { get; set; } = string.Empty;

    /// <summary>Weighted sound reduction index Rw, in decibels.</summary>
    public double AcousticRating { get; set; }

    /// <summary>Thermal resistance R, in m²K/W.</summary>
    public double ThermalResistance { get; set; }

    /// <summary>Heat transfer coefficient U, in W/m²K.</summary>
    public double HeatTransferCoefficient { get; set; }

    /// <summary>Which layers return into door and window openings (section 3.1).</summary>
    public WallWrapping WrapAtInserts { get; set; } = WallWrapping.Both;

    /// <summary>Which layers turn round an exposed end. Never both: an end has one way round.</summary>
    public WallWrapping WrapAtEnds
    {
        get => _wrapAtEnds;
        set => _wrapAtEnds = value == WallWrapping.Both ? WallWrapping.Exterior : value;
    }

    private WallWrapping _wrapAtEnds = WallWrapping.None;

    /// <summary>Colour used to fill the wall when a plan is drawn at coarse detail.</summary>
    public ColourRgb CoarseScaleFillColour { get; set; } = new(0x8A, 0x93, 0xA1);

    public override IEnumerable<ParameterValue> GetTypeParameters()
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.ReadOnly(WallTypeParameters.Width, () => Width);
        yield return ParameterValue.ReadOnly(WallTypeParameters.LayerCount, () => Structure.Layers.Count);
        yield return ParameterValue.BindChoice(
            WallTypeParameters.Function,
            () => EnumText.Humanise(Function),
            v => { if (EnumText.TryParse<WallFunction>(v, out var f)) Function = f; },
            EnumText.Choices<WallFunction>());
        yield return ParameterValue.Bind(WallTypeParameters.FireRating, () => FireRating, v => FireRating = v);
        yield return ParameterValue.Bind(WallTypeParameters.AcousticRating, () => AcousticRating, v => AcousticRating = v);
        yield return ParameterValue.Bind(WallTypeParameters.ThermalResistance, () => ThermalResistance, v => ThermalResistance = v);
        yield return ParameterValue.Bind(WallTypeParameters.HeatTransferCoefficient, () => HeatTransferCoefficient, v => HeatTransferCoefficient = v);
        yield return ParameterValue.BindChoice(
            WallTypeParameters.WrapAtInserts,
            () => EnumText.Humanise(WrapAtInserts),
            v => { if (EnumText.TryParse<WallWrapping>(v, out var w)) WrapAtInserts = w; },
            EnumText.Choices<WallWrapping>());
        yield return ParameterValue.BindChoice(
            WallTypeParameters.WrapAtEnds,
            () => EnumText.Humanise(WrapAtEnds),
            v => { if (EnumText.TryParse<WallWrapping>(v, out var w)) WrapAtEnds = w; },
            new[] { WallWrapping.None, WallWrapping.Exterior, WallWrapping.Interior }.Select(w => EnumText.Humanise(w)).ToArray());
    }
}

public static class WallTypeParameters
{
    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition LayerCount =
        new("Layers", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Function =
        new("Function", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition FireRating =
        new("Fire Rating", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition AcousticRating =
        new("Acoustic Rating (Rw)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);

    public static readonly ParameterDefinition ThermalResistance =
        new("Thermal Resistance (R)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);

    public static readonly ParameterDefinition HeatTransferCoefficient =
        new("Heat Transfer Coefficient (U)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);

    public static readonly ParameterDefinition WrapAtInserts =
        new("Wrapping at Inserts", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition WrapAtEnds =
        new("Wrapping at Ends", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
}
