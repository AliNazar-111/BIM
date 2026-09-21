using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a door leaf moves (specification section 3.5).</summary>
public enum DoorOperation
{
    Swing,
    DoubleSwing,
    Sliding,
    Folding,
    Revolving,
    Overhead
}

/// <summary>How a window opens (specification section 3.5).</summary>
public enum WindowOperation
{
    Fixed,
    Casement,
    Awning,
    Sliding,
    TiltAndTurn,
    Bay
}

/// <summary>
/// What doors and windows have in common: a hole of a given size, with a frame in it.
///
/// Width and height live on the type rather than the instance, because they are what makes
/// one door type different from another - a schedule of "1000 x 2100 single leaf" counts
/// instances, and every one of them is that size by definition (specification section 13.2).
/// </summary>
public abstract class OpeningType : ElementType
{
    protected OpeningType(string name, double width, double height) : base(name)
    {
        Width = width;
        Height = height;
    }

    /// <summary>Structural opening width in millimetres.</summary>
    public double Width { get; set; }

    /// <summary>Structural opening height in millimetres.</summary>
    public double Height { get; set; }

    /// <summary>Frame depth through the wall, in millimetres.</summary>
    public double Thickness { get; set; } = 100;

    public string FrameMaterial { get; set; } = string.Empty;

    public string FireRating { get; set; } = string.Empty;

    /// <summary>Weighted sound reduction index Rw, in decibels.</summary>
    public double AcousticRating { get; set; }

    /// <summary>Heat transfer coefficient U, in W/m²K.</summary>
    public double HeatTransferCoefficient { get; set; }

    /// <summary>Area of the opening, in mm² - the quantity a schedule reports.</summary>
    public double Area => Width * Height;

    protected IEnumerable<ParameterValue> GetOpeningTypeParameters()
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.Bind(OpeningTypeParameters.Width, () => Width, v => { if (v > 0) Width = v; });
        yield return ParameterValue.Bind(OpeningTypeParameters.Height, () => Height, v => { if (v > 0) Height = v; });
        yield return ParameterValue.Bind(OpeningTypeParameters.Thickness, () => Thickness, v => Thickness = v);
        yield return ParameterValue.ReadOnly(OpeningTypeParameters.Area, () => Area);
        yield return ParameterValue.Bind(OpeningTypeParameters.FrameMaterial, () => FrameMaterial, v => FrameMaterial = v);
        yield return ParameterValue.Bind(OpeningTypeParameters.FireRating, () => FireRating, v => FireRating = v);
        yield return ParameterValue.Bind(OpeningTypeParameters.AcousticRating, () => AcousticRating, v => AcousticRating = v);
        yield return ParameterValue.Bind(OpeningTypeParameters.HeatTransferCoefficient,
            () => HeatTransferCoefficient, v => HeatTransferCoefficient = v);
    }
}

/// <summary>A door type (specification section 13.2, type parameters).</summary>
public sealed class DoorType : OpeningType
{
    public DoorType(string name, double width, double height) : base(name, width, height) { }

    public override BuiltInCategory Category => BuiltInCategory.Doors;

    public DoorOperation Operation { get; set; } = DoorOperation.Swing;

    public int LeafCount { get; set; } = 1;

    public string PanelMaterial { get; set; } = "Timber";

    public string HardwareSet { get; set; } = string.Empty;

    public override IEnumerable<ParameterValue> GetTypeParameters()
    {
        foreach (var parameter in GetOpeningTypeParameters()) yield return parameter;

        yield return ParameterValue.BindChoice(
            DoorTypeParameters.Operation,
            () => EnumText.Humanise(Operation),
            v => { if (EnumText.TryParse<DoorOperation>(v, out var o)) Operation = o; },
            EnumText.Choices<DoorOperation>());

        yield return ParameterValue.Bind(DoorTypeParameters.LeafCount, () => LeafCount, v => { if (v > 0) LeafCount = v; });
        yield return ParameterValue.Bind(DoorTypeParameters.PanelMaterial, () => PanelMaterial, v => PanelMaterial = v);
        yield return ParameterValue.Bind(DoorTypeParameters.HardwareSet, () => HardwareSet, v => HardwareSet = v);
    }
}

/// <summary>A window type (specification section 13.3, type parameters).</summary>
public sealed class WindowType : OpeningType
{
    public WindowType(string name, double width, double height) : base(name, width, height) { }

    public override BuiltInCategory Category => BuiltInCategory.Windows;

    public WindowOperation Operation { get; set; } = WindowOperation.Casement;

    public string GlazingType { get; set; } = "Double glazed";

    /// <summary>Solar heat gain coefficient, 0 to 1.</summary>
    public double SolarHeatGainCoefficient { get; set; } = 0.6;

    public override IEnumerable<ParameterValue> GetTypeParameters()
    {
        foreach (var parameter in GetOpeningTypeParameters()) yield return parameter;

        yield return ParameterValue.BindChoice(
            WindowTypeParameters.Operation,
            () => EnumText.Humanise(Operation),
            v => { if (EnumText.TryParse<WindowOperation>(v, out var o)) Operation = o; },
            EnumText.Choices<WindowOperation>());

        yield return ParameterValue.Bind(WindowTypeParameters.GlazingType, () => GlazingType, v => GlazingType = v);
        yield return ParameterValue.Bind(WindowTypeParameters.SolarHeatGain,
            () => SolarHeatGainCoefficient, v => SolarHeatGainCoefficient = v);
    }
}

public static class OpeningTypeParameters
{
    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Thickness =
        new("Frame Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Area =
        new("Opening Area", ParameterDataType.Area, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition FrameMaterial =
        new("Frame Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition FireRating =
        new("Fire Rating", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition AcousticRating =
        new("Acoustic Rating (Rw)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);

    public static readonly ParameterDefinition HeatTransferCoefficient =
        new("Heat Transfer Coefficient (U)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);
}

public static class DoorTypeParameters
{
    public static readonly ParameterDefinition Operation =
        new("Operation", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition LeafCount =
        new("Leaf Count", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition PanelMaterial =
        new("Panel Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition HardwareSet =
        new("Hardware Set", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
}

public static class WindowTypeParameters
{
    public static readonly ParameterDefinition Operation =
        new("Operation", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition GlazingType =
        new("Glazing", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition SolarHeatGain =
        new("Solar Heat Gain (SHGC)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);
}
