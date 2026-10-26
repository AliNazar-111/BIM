using BIMDesigner.Core.Documents;
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

/// <summary>
/// What a door leaf looks like (specification section 3.5, "door types"): the designs door
/// families come in - a plain flush leaf, raised panels, one pane of glass, glass divided by
/// glazing bars as a French door, glass over panels, louvres over a panel as a bi-fold closet
/// door has, or panels under an arched, sunburst top light as a front door has.
/// </summary>
public enum DoorLeafDesign
{
    Flush,
    Panelled,
    Glazed,
    FrenchGlazed,
    HalfGlazed,
    Louvred,
    ArchedTopLight,

    /// <summary>One clear pane in a slim frame, as a shopfront or curtain wall door is made.</summary>
    FullGlass
}

/// <summary>Whether a door is inside the building or on its envelope, which is what schedules and energy analysis ask.</summary>
public enum DoorFunction
{
    Interior,
    Exterior
}

/// <summary>How a window opens (specification section 3.5).</summary>
public enum WindowOperation
{
    Fixed,
    Casement,
    Awning,
    Sliding,
    TiltAndTurn,
    Bay,

    /// <summary>Two sashes one above the other, both sliding up and down.</summary>
    DoubleHung,

    /// <summary>Hinged at the bottom and opening inward, as a basement or bathroom light is.</summary>
    Hopper,

    /// <summary>Glass slats turning together in the frame, for air without a view.</summary>
    Louvred
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

    /// <summary>
    /// Whether its glass is safety glass - toughened or laminated, to BS EN 12600 - as glass in a
    /// door, beside one or low in a wall must be (Approved Document K section 4; IRC R308).
    /// </summary>
    public bool SafetyGlazing { get; set; }

    /// <summary>Gives a copy of this type what every door and window has: its frame's depth and material, and its ratings.</summary>
    protected T WithOpeningOf<T>(T copy) where T : OpeningType
    {
        copy.Thickness = Thickness;
        copy.FrameMaterial = FrameMaterial;
        copy.FireRating = FireRating;
        copy.AcousticRating = AcousticRating;
        copy.HeatTransferCoefficient = HeatTransferCoefficient;
        copy.SafetyGlazing = SafetyGlazing;
        return copy;
    }

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
        yield return ParameterValue.Bind(OpeningTypeParameters.SafetyGlazing, () => SafetyGlazing, v => SafetyGlazing = v);
    }
}

/// <summary>A door type (specification section 13.2, type parameters).</summary>
public sealed class DoorType : OpeningType
{
    public DoorType(string name, double width, double height) : base(name, width, height) { }

    /// <summary>A new door type starting as this one is: its size, swing, leaves, glazing and trim.</summary>
    public DoorType Duplicate(string name) => WithCommonOf(WithOpeningOf(new DoorType(name, Width, Height)
    {
        Operation = Operation, LeafCount = LeafCount, PanelMaterial = PanelMaterial, HardwareSet = HardwareSet, LeafDesign = LeafDesign, SelfClosing = SelfClosing,
        GlazingRows = GlazingRows, GlazingColumns = GlazingColumns, Function = Function, CurtainPanel = CurtainPanel,
        TrimWidth = TrimWidth, TrimProjectionExterior = TrimProjectionExterior, TrimProjectionInterior = TrimProjectionInterior
    }));

    public override BuiltInCategory Category => BuiltInCategory.Doors;

    public DoorOperation Operation { get; set; } = DoorOperation.Swing;

    public int LeafCount { get; set; } = 1;

    public string PanelMaterial { get; set; } = "Timber";

    public string HardwareSet { get; set; } = string.Empty;

    /// <summary>Whether it closes itself - a closer on it - as a fire door between a house and its garage must.</summary>
    public bool SelfClosing { get; set; }

    /// <summary>What the leaves look like. See <see cref="DoorLeafDesign"/>.</summary>
    public DoorLeafDesign LeafDesign { get; set; } = DoorLeafDesign.Panelled;

    /// <summary>For glass divided by bars: panes up and across each leaf.</summary>
    public int GlazingRows { get; set; } = 4;

    public int GlazingColumns { get; set; } = 1;

    public DoorFunction Function { get; set; } = DoorFunction.Interior;

    /// <summary>
    /// Whether this is a curtain wall door: one that replaces a panel of a curtain wall rather
    /// than filling a hole cut in a solid one. As in Revit, only these can be put into a
    /// curtain wall, and they take the size of the panel they replace.
    /// </summary>
    public bool CurtainPanel { get; set; }

    /// <summary>The architrave round the opening on each face: how wide, and how far it stands off the wall.</summary>
    public double TrimWidth { get; set; } = 70;

    public double TrimProjectionExterior { get; set; } = 20;

    public double TrimProjectionInterior { get; set; } = 20;

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
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
        yield return ParameterValue.Bind(DoorTypeParameters.SelfClosing, () => SelfClosing, v => SelfClosing = v);
        yield return ParameterValue.ReadOnly(DoorTypeParameters.ClearWidth, () => Doors.ClearWidth(this));

        yield return ParameterValue.BindChoice(
            DoorTypeParameters.LeafDesign,
            () => EnumText.Humanise(LeafDesign),
            v => { if (EnumText.TryParse<DoorLeafDesign>(v, out var d)) LeafDesign = d; },
            EnumText.Choices<DoorLeafDesign>());
        yield return ParameterValue.Bind(DoorTypeParameters.GlazingRows, () => GlazingRows, v => { if (v is > 0 and <= 12) GlazingRows = v; });
        yield return ParameterValue.Bind(DoorTypeParameters.GlazingColumns, () => GlazingColumns, v => { if (v is > 0 and <= 6) GlazingColumns = v; });
        yield return ParameterValue.BindChoice(
            DoorTypeParameters.Function,
            () => EnumText.Humanise(Function),
            v => { if (EnumText.TryParse<DoorFunction>(v, out var f)) Function = f; },
            EnumText.Choices<DoorFunction>());
        yield return ParameterValue.Bind(DoorTypeParameters.CurtainPanel, () => CurtainPanel, v => CurtainPanel = v);
        yield return ParameterValue.Bind(DoorTypeParameters.TrimWidth, () => TrimWidth, v => { if (v >= 0) TrimWidth = v; });
        yield return ParameterValue.Bind(DoorTypeParameters.TrimProjectionExterior, () => TrimProjectionExterior, v => { if (v >= 0) TrimProjectionExterior = v; });
        yield return ParameterValue.Bind(DoorTypeParameters.TrimProjectionInterior, () => TrimProjectionInterior, v => { if (v >= 0) TrimProjectionInterior = v; });

        // The hole the wall is left with, for ordering and export: the opening, as it is here.
        yield return ParameterValue.ReadOnly(DoorTypeParameters.RoughWidth, () => Width);
        yield return ParameterValue.ReadOnly(DoorTypeParameters.RoughHeight, () => Height);
    }
}

/// <summary>A window type (specification section 13.3, type parameters).</summary>
public sealed class WindowType : OpeningType
{
    public WindowType(string name, double width, double height) : base(name, width, height) { }

    /// <summary>A new window type starting as this one is: its size, opening, glazing and ratings.</summary>
    public WindowType Duplicate(string name) => WithCommonOf(WithOpeningOf(new WindowType(name, Width, Height)
    {
        Operation = Operation, GlazingType = GlazingType, GlazingRows = GlazingRows, GlazingColumns = GlazingColumns,
        SolarHeatGainCoefficient = SolarHeatGainCoefficient, LightTransmittance = LightTransmittance, TrickleVent = TrickleVent,
        Lights = Lights, LightPattern = LightPattern, TransomHeight = TransomHeight, Fanlight = Fanlight, MullionWidth = MullionWidth
    }));

    /// <summary>Its trickle ventilator's equivalent area, mm² - a background ventilator in its frame (Approved Document F, Table 1.7); none at 0.</summary>
    public double TrickleVent { get; set; }

    public override BuiltInCategory Category => BuiltInCategory.Windows;

    public WindowOperation Operation { get; set; } = WindowOperation.Casement;

    public string GlazingType { get; set; } = "Double glazed";

    /// <summary>Panes up and across each light: 1 by 1 is a single sheet, 3 by 2 a Georgian sash.</summary>
    public int GlazingRows { get; set; } = 1;

    public int GlazingColumns { get; set; } = 1;

    /// <summary>Solar heat gain coefficient, 0 to 1.</summary>
    public double SolarHeatGainCoefficient { get; set; } = 0.6;

    /// <summary>The share of daylight its glazing lets through, 0 to 1 - its maker's figure; 0.7 is usual for double glazing with a low-emissivity coating.</summary>
    public double LightTransmittance { get; set; } = 0.7;

    /// <summary>How many lights it has side by side, mullions between them; one is a window undivided.</summary>
    public int Lights { get; set; } = 1;

    /// <summary>Which lights open, one letter each across - O opens as the window does, F is fixed: "OFO". Left empty, the usual way.</summary>
    public string LightPattern { get; set; } = string.Empty;

    /// <summary>Where the transom's middle is, up from the window's bottom, mm, fanlights over it; none at 0.</summary>
    public double TransomHeight { get; set; }

    /// <summary>How the fanlights over the transom open.</summary>
    public FanlightOperation Fanlight { get; set; } = FanlightOperation.Fixed;

    /// <summary>How wide its mullions and transom are seen face-on, mm: the frame's own section, through its depth.</summary>
    public double MullionWidth { get; set; } = 70;

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetOpeningTypeParameters()) yield return parameter;

        // Its lights: side by side between mullions, and fanlights over a transom.
        if (Operation != WindowOperation.Bay)
        {
            yield return ParameterValue.BindValidated(WindowTypeParameters.Lights, () => Lights, v =>
            {
                if (v < 1 || v > 8) return false;
                var was = Lights;
                Lights = v;
                if (v == 1 || WindowLights.MostLights(this) >= v) return true;
                Lights = was;
                return false;
            });
            if (Lights > 1)
                yield return ParameterValue.BindValidated(WindowTypeParameters.LightPattern, () => WindowLights.Pattern(this), v =>
                {
                    var letters = (v ?? string.Empty).Trim().ToUpperInvariant();
                    if (letters.Length > 0 && (letters.Length != Lights || letters.Any(letter => letter is not ('O' or 'F')))) return false;
                    LightPattern = letters;
                    return true;
                });
            yield return ParameterValue.BindValidated(WindowTypeParameters.TransomHeight, () => TransomHeight, v =>
            {
                if (v < 0 || v >= Height) return false;
                TransomHeight = v;
                return true;
            });
            if (WindowLights.TransomAt(this) is not null)
                yield return ParameterValue.BindChoice(WindowTypeParameters.Fanlight, () => EnumText.Humanise(Fanlight),
                    v => { if (EnumText.TryParse<FanlightOperation>(v, out var fanlight)) Fanlight = fanlight; }, EnumText.Choices<FanlightOperation>());
            if (WindowLights.Divided(this))
                yield return ParameterValue.BindValidated(WindowTypeParameters.MullionWidth, () => MullionWidth, v =>
                {
                    if (v < 20 || v > 250) return false;
                    MullionWidth = v;
                    return true;
                });
        }

        yield return ParameterValue.Bind(WindowTypeParameters.GlazingRows, () => GlazingRows, v => { if (v is > 0 and <= 12) GlazingRows = v; });
        yield return ParameterValue.Bind(WindowTypeParameters.GlazingColumns, () => GlazingColumns, v => { if (v is > 0 and <= 12) GlazingColumns = v; });

        yield return ParameterValue.BindChoice(
            WindowTypeParameters.Operation,
            () => EnumText.Humanise(Operation),
            v => { if (EnumText.TryParse<WindowOperation>(v, out var o)) Operation = o; },
            EnumText.Choices<WindowOperation>());

        yield return ParameterValue.Bind(WindowTypeParameters.GlazingType, () => GlazingType, v => GlazingType = v);
        yield return ParameterValue.Bind(WindowTypeParameters.SolarHeatGain,
            () => SolarHeatGainCoefficient, v => SolarHeatGainCoefficient = v);
        yield return ParameterValue.BindValidated(WindowTypeParameters.LightTransmittance, () => LightTransmittance, v =>
        {
            if (v is < 0 or > 1) return false;
            LightTransmittance = v;
            return true;
        });
        yield return ParameterValue.BindValidated(OpeningTypeParameters.TrickleVent, () => TrickleVent, v => { if (v < 0) return false; TrickleVent = v; return true; });
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

    public static readonly ParameterDefinition TrickleVent =
        new("Trickle Vent (mm² equivalent area)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition SafetyGlazing =
        new("Safety Glazing (BS EN 12600)", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
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

    public static readonly ParameterDefinition SelfClosing =
        new("Self-Closing", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition ClearWidth =
        new("Clear Opening Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition LeafDesign =
        new("Leaf Design", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition GlazingRows =
        new("Glazing Rows", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition GlazingColumns =
        new("Glazing Columns", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Function =
        new("Function", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition CurtainPanel =
        new("Curtain Wall Door", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition TrimWidth =
        new("Trim Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition TrimProjectionExterior =
        new("Trim Projection Ext", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition TrimProjectionInterior =
        new("Trim Projection Int", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition RoughWidth =
        new("Rough Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition RoughHeight =
        new("Rough Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
}

public static class WindowTypeParameters
{
    public static readonly ParameterDefinition GlazingRows =
        new("Glazing Rows", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition GlazingColumns =
        new("Glazing Columns", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Operation =
        new("Operation", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition GlazingType =
        new("Glazing", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition SolarHeatGain =
        new("Solar Heat Gain (SHGC)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);

    public static readonly ParameterDefinition LightTransmittance =
        new("Light Transmittance", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);

    public static readonly ParameterDefinition Lights =
        new("Lights Across", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition LightPattern =
        new("Opening Lights (O opens, F fixed)", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition TransomHeight =
        new("Transom Height (0 = none)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Fanlight =
        new("Fanlights", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition MullionWidth =
        new("Mullion and Transom Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
}
