using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a chimney is built.</summary>
public enum ChimneyConstruction
{
    /// <summary>Site-built masonry: brick or stone, with clay flue liners, an oversailing course, a cap and pots.</summary>
    Masonry,

    /// <summary>A system chimney of precast pumice or concrete blocks round a ceramic liner, with a precast cap.</summary>
    PrecastBlock,

    /// <summary>Factory-built twin-wall stainless steel, insulated between the walls - the all-fuel flue.</summary>
    TwinWallSteel,

    /// <summary>Factory-built metal, cooled by air rising between its walls.</summary>
    AirCooledMetal,

    /// <summary>Type B gas vent: light double-wall metal for low-temperature gas appliances.</summary>
    TypeBVent,

    /// <summary>A concentric flue: combustion air drawn down the outer annulus, the exhaust out of the core - a balanced flue.</summary>
    ConcentricFlue,

    /// <summary>A self-supporting steel stack, standing on its own foundation.</summary>
    SteelStack,

    /// <summary>A slender steel stack held up by guy wires.</summary>
    GuyedStack,

    /// <summary>An industrial stack: a concrete windshield round several independent steel flues.</summary>
    IndustrialMultiFlue,

    /// <summary>A solar chimney: a glazed stack the sun heats, drawing air up through the building to ventilate it.</summary>
    SolarChimney,

    /// <summary>A framed chase: a timber or steel stud box, clad, round a metal flue, with a metal chase cover.</summary>
    FramedChase
}

/// <summary>What there is at a chimney's foot, in the room.</summary>
public enum ChimneyFireplace
{
    None,

    /// <summary>An open fireplace in the front of the breast, on a hearth, under a mantel beam.</summary>
    Open,

    /// <summary>A stove: in the fireplace of a masonry breast, or standing at the foot of a metal flue.</summary>
    Stove
}

/// <summary>
/// A chimney's type: how it is built and how big, its flues, and the ratings a flue is specified
/// by - EN 1443's temperature, pressure, condensate, corrosion and soot-fire classes, and the
/// distance it must keep from anything that burns.
/// </summary>
public sealed class ChimneyType : ElementType
{
    public ChimneyType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Chimneys;

    public ChimneyConstruction Construction { get; set; } = ChimneyConstruction.Masonry;

    /// <summary>A stack's width along it, or a round one's outside diameter, mm.</summary>
    public double Width { get; set; } = 450;

    /// <summary>A stack's depth across it, mm. A round one has only its diameter.</summary>
    public double Depth { get; set; } = 450;

    /// <summary>The inside diameter of each flue, mm.</summary>
    public double FlueDiameter { get; set; } = 185;

    /// <summary>How many flues it carries.</summary>
    public int Flues { get; set; } = 1;

    /// <summary>The least height it stands to from its foot, mm - an industrial stack's own height; none for a house chimney, whose roof sets it.</summary>
    public double MinimumHeight { get; set; }

    /// <summary>What it usually has at its foot.</summary>
    public ChimneyFireplace Fireplace { get; set; } = ChimneyFireplace.None;

    /// <summary>EN 1443 temperature class: the highest flue temperature it is rated for, °C - T600 is 600.</summary>
    public int TemperatureClass { get; set; } = 600;

    /// <summary>EN 1443 pressure class: N1, N2 for negative pressure; P1, P2, H1, H2 for positive.</summary>
    public string PressureClass { get; set; } = "N1";

    /// <summary>Whether it takes condensate - W, wet - or only dry flue gases - D.</summary>
    public bool Wet { get; set; }

    /// <summary>EN 1443 corrosion resistance class, 1 to 3.</summary>
    public int CorrosionClass { get; set; } = 3;

    /// <summary>Whether it is tested to withstand a soot fire (G), or not (O).</summary>
    public bool SootFireResistant { get; set; } = true;

    /// <summary>The distance it must keep from combustible material, mm: what the hole through a roof or floor is opened up by.</summary>
    public double ClearanceToCombustibles { get; set; } = 40;

    /// <summary>How long it holds a fire back, as rated: "2 hours".</summary>
    public string FireRating { get; set; } = string.Empty;

    /// <summary>Whether it is round - a pipe or a stack - rather than square.</summary>
    public bool IsRound => Construction is ChimneyConstruction.TwinWallSteel or ChimneyConstruction.AirCooledMetal or ChimneyConstruction.TypeBVent
        or ChimneyConstruction.ConcentricFlue or ChimneyConstruction.SteelStack or ChimneyConstruction.GuyedStack or ChimneyConstruction.IndustrialMultiFlue;

    /// <summary>Whether it carries flue gas at all: a solar chimney carries air.</summary>
    public bool HasFlue => Construction != ChimneyConstruction.SolarChimney;

    /// <summary>Its EN 1443 designation: "EN 1443 T600 N1 D 3 G50".</summary>
    public string Designation => HasFlue
        ? $"EN 1443 T{TemperatureClass:000} {PressureClass} {(Wet ? "W" : "D")} {CorrosionClass} {(SootFireResistant ? $"G{ClearanceToCombustibles:0}" : $"O{ClearanceToCombustibles:0}")}"
        : "Ventilation only";

    public ChimneyType Duplicate(string name) => new(name)
    {
        Construction = Construction, Width = Width, Depth = Depth, FlueDiameter = FlueDiameter, Flues = Flues, MinimumHeight = MinimumHeight,
        Fireplace = Fireplace, TemperatureClass = TemperatureClass, PressureClass = PressureClass, Wet = Wet, CorrosionClass = CorrosionClass,
        SootFireResistant = SootFireResistant, ClearanceToCombustibles = ClearanceToCombustibles, FireRating = FireRating,
        Description = Description, Cost = Cost
    };

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.BindChoice(ChimneyTypeParameters.Construction,
            () => EnumText.Humanise(Construction),
            v => { if (EnumText.TryParse<ChimneyConstruction>(v, out var construction)) Construction = construction; },
            EnumText.Choices<ChimneyConstruction>());
        yield return Size(ChimneyTypeParameters.Width, () => Width, v => Width = v, 60, 20000);
        yield return Size(ChimneyTypeParameters.Depth, () => Depth, v => Depth = v, 60, 20000);
        yield return Size(ChimneyTypeParameters.FlueDiameter, () => FlueDiameter, v => FlueDiameter = v, 40, 5000);
        yield return ParameterValue.BindValidated(ChimneyTypeParameters.Flues, () => Flues, (int v) => Set(v is >= 1 and <= 8, () => Flues = v));
        yield return Size(ChimneyTypeParameters.MinimumHeight, () => MinimumHeight, v => MinimumHeight = v, 0, 300000);
        yield return ParameterValue.BindChoice(ChimneyTypeParameters.Fireplace,
            () => EnumText.Humanise(Fireplace),
            v => { if (EnumText.TryParse<ChimneyFireplace>(v, out var fireplace)) Fireplace = fireplace; },
            EnumText.Choices<ChimneyFireplace>());

        yield return ParameterValue.BindValidated(ChimneyTypeParameters.TemperatureClass, () => TemperatureClass, (int v) => Set(v is >= 80 and <= 1000, () => TemperatureClass = v));
        yield return ParameterValue.BindChoice(ChimneyTypeParameters.PressureClass, () => PressureClass, v => PressureClass = v,
            new[] { "N1", "N2", "P1", "P2", "H1", "H2" });
        yield return ParameterValue.Bind(ChimneyTypeParameters.Wet, () => Wet, v => Wet = v);
        yield return ParameterValue.BindValidated(ChimneyTypeParameters.CorrosionClass, () => CorrosionClass, (int v) => Set(v is >= 1 and <= 3, () => CorrosionClass = v));
        yield return ParameterValue.Bind(ChimneyTypeParameters.SootFire, () => SootFireResistant, v => SootFireResistant = v);
        yield return Size(ChimneyTypeParameters.Clearance, () => ClearanceToCombustibles, v => ClearanceToCombustibles = v, 0, 1000);
        yield return ParameterValue.Bind(ChimneyTypeParameters.FireRating, () => FireRating, v => FireRating = v);
        yield return ParameterValue.ReadOnly(ChimneyTypeParameters.Designation, () => Designation);
    }

    private static ParameterValue Size(ParameterDefinition definition, Func<double> get, Action<double> set, double least, double most) =>
        ParameterValue.BindValidated(definition, get, (double v) => Set(v >= least && v <= most, () => set(v)));

    private static bool Set(bool valid, Action apply)
    {
        if (valid) apply();
        return valid;
    }

    /// <summary>The chimney types a project starts with: one of each way a chimney is built.</summary>
    public static IReadOnlyList<ChimneyType> Library() => new[]
    {
        new ChimneyType("Chimney - Masonry, Brick, Clay Liners")
        {
            Construction = ChimneyConstruction.Masonry, Width = 450, Depth = 450, FlueDiameter = 185, Fireplace = ChimneyFireplace.Open,
            TemperatureClass = 600, PressureClass = "N1", CorrosionClass = 3, ClearanceToCombustibles = 40, FireRating = "2 hours", TypeMark = "CH1", Cost = 2400m
        },
        new ChimneyType("Chimney - Precast Pumice Block System")
        {
            Construction = ChimneyConstruction.PrecastBlock, Width = 360, Depth = 360, FlueDiameter = 150, Fireplace = ChimneyFireplace.Stove,
            TemperatureClass = 600, PressureClass = "N1", CorrosionClass = 3, ClearanceToCombustibles = 50, FireRating = "90 minutes", TypeMark = "CH2", Cost = 1600m
        },
        new ChimneyType("Chimney - Twin-Wall Insulated Stainless 150")
        {
            Construction = ChimneyConstruction.TwinWallSteel, Width = 200, Depth = 200, FlueDiameter = 150, Fireplace = ChimneyFireplace.Stove,
            TemperatureClass = 600, PressureClass = "N1", CorrosionClass = 3, ClearanceToCombustibles = 50, TypeMark = "CH3", Cost = 900m
        },
        new ChimneyType("Chimney - Air-Cooled Triple-Wall Metal 150")
        {
            Construction = ChimneyConstruction.AirCooledMetal, Width = 250, Depth = 250, FlueDiameter = 150, Fireplace = ChimneyFireplace.Stove,
            TemperatureClass = 600, PressureClass = "N1", CorrosionClass = 2, ClearanceToCombustibles = 50, TypeMark = "CH4", Cost = 850m
        },
        new ChimneyType("Gas Vent - Type B 100")
        {
            Construction = ChimneyConstruction.TypeBVent, Width = 125, Depth = 125, FlueDiameter = 100,
            TemperatureClass = 250, PressureClass = "N1", Wet = false, CorrosionClass = 1, SootFireResistant = false, ClearanceToCombustibles = 25, TypeMark = "CH5", Cost = 300m
        },
        new ChimneyType("Flue - Concentric 60/100")
        {
            Construction = ChimneyConstruction.ConcentricFlue, Width = 100, Depth = 100, FlueDiameter = 60,
            TemperatureClass = 120, PressureClass = "P1", Wet = true, CorrosionClass = 1, SootFireResistant = false, ClearanceToCombustibles = 0, TypeMark = "CH6", Cost = 180m
        },
        new ChimneyType("Stack - Self-Supporting Steel 1200")
        {
            Construction = ChimneyConstruction.SteelStack, Width = 1200, Depth = 1200, FlueDiameter = 1150, MinimumHeight = 15000,
            TemperatureClass = 450, PressureClass = "N1", CorrosionClass = 2, ClearanceToCombustibles = 0, TypeMark = "ST1", Cost = 45000m
        },
        new ChimneyType("Stack - Guyed Steel 600")
        {
            Construction = ChimneyConstruction.GuyedStack, Width = 600, Depth = 600, FlueDiameter = 570, MinimumHeight = 12000,
            TemperatureClass = 450, PressureClass = "N1", CorrosionClass = 2, ClearanceToCombustibles = 0, TypeMark = "ST2", Cost = 18000m
        },
        new ChimneyType("Stack - Industrial Multi-Flue, Concrete Windshield 4000")
        {
            Construction = ChimneyConstruction.IndustrialMultiFlue, Width = 4000, Depth = 4000, FlueDiameter = 800, Flues = 3, MinimumHeight = 30000,
            TemperatureClass = 400, PressureClass = "N1", Wet = true, CorrosionClass = 3, ClearanceToCombustibles = 0, FireRating = "4 hours", TypeMark = "ST3", Cost = 900000m
        },
        new ChimneyType("Solar Chimney 1200 x 600")
        {
            Construction = ChimneyConstruction.SolarChimney, Width = 1200, Depth = 600, FlueDiameter = 0, Flues = 1,
            SootFireResistant = false, ClearanceToCombustibles = 0, TypeMark = "SC1", Cost = 6000m
        },
        new ChimneyType("Chimney - Framed Chase 600 x 600, Twin-Wall 150")
        {
            Construction = ChimneyConstruction.FramedChase, Width = 600, Depth = 600, FlueDiameter = 150, Fireplace = ChimneyFireplace.Stove,
            TemperatureClass = 600, PressureClass = "N1", CorrosionClass = 3, ClearanceToCombustibles = 50, FireRating = "1 hour", TypeMark = "CH7", Cost = 2200m
        }
    };
}

public static class ChimneyTypeParameters
{
    public static readonly ParameterDefinition Construction = new("Construction", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Width = new("Width (or Diameter)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Depth = new("Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FlueDiameter = new("Flue Internal Diameter", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Flues = new("Flues", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition MinimumHeight = new("Stack Height (least)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Fireplace = new("At Its Foot", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition TemperatureClass = new("Temperature Class (T, °C)", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition PressureClass = new("Pressure Class", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Wet = new("Condensate (Wet)", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition CorrosionClass = new("Corrosion Class", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition SootFire = new("Soot-Fire Resistant", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Clearance = new("Clearance to Combustibles", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition FireRating = new("Fire Rating", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Designation = new("EN 1443 Designation", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
}
