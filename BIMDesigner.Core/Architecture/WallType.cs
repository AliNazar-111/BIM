using BIMDesigner.Core.Documents;
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
    public WallType Duplicate(string name)
    {
        var copy = DuplicateBody(name);
        copy.Sweeps.AddRange(Sweeps);
        copy.Bands.AddRange(Bands);
        return copy;
    }

    /// <summary>
    /// Bands of its layers made of other materials between heights - a tile band at the foot of
    /// the plaster: Revit's vertically compound wall. Later bands lie over earlier ones.
    /// </summary>
    public List<WallBand> Bands { get; } = new();

    private WallType DuplicateBody(string name) => new(name, Structure.Clone())
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
        ExteriorTaperAngle = ExteriorTaperAngle,
        InteriorTaperAngle = InteriorTaperAngle,
        CoarseScaleFillColour = CoarseScaleFillColour,
        Log = Log
    };

    /// <summary>Built of logs laid in courses rather than of its layers - a log wall - or null for a layered wall.</summary>
    public LogWall? Log { get; set; }

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

    /// <summary>Profiles run along the faces of every wall of this type: skirtings, plinths, grooves.</summary>
    public List<WallSweep> Sweeps { get; } = new();

    /// <summary>How far a tapered wall of this type leans in on its exterior face, in degrees.</summary>
    public double ExteriorTaperAngle { get; set; }

    /// <summary>How far a tapered wall of this type leans in on its interior face, in degrees.</summary>
    public double InteriorTaperAngle { get; set; }

    /// <summary>Colour used to fill the wall when a plan is drawn at coarse detail.</summary>
    public ColourRgb CoarseScaleFillColour { get; set; } = new(0x8A, 0x93, 0xA1);

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
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
        yield return ParameterValue.Bind(WallTypeParameters.ExteriorTaperAngle, () => ExteriorTaperAngle, v => ExteriorTaperAngle = Math.Clamp(v, -WallLean.MaxAngle, WallLean.MaxAngle));
        yield return ParameterValue.Bind(WallTypeParameters.InteriorTaperAngle, () => InteriorTaperAngle, v => InteriorTaperAngle = Math.Clamp(v, -WallLean.MaxAngle, WallLean.MaxAngle));
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

        // Built of logs instead of its layers, and how.
        yield return ParameterValue.BindChoice(WallTypeParameters.BuiltAs,
            () => Log is null ? "Layers" : "Logs",
            v => Log = v == "Logs" ? Log ?? LogWall.Default : null,
            new[] { "Layers", "Logs" });

        if (Log is { } log)
        {
            yield return ParameterValue.BindChoice(WallTypeParameters.LogShape,
                () => EnumText.Humanise(log.Shape),
                v => { if (EnumText.TryParse<LogShape>(v, out var shape)) Log = log with { Shape = shape }; },
                EnumText.Choices<LogShape>());
            yield return ParameterValue.BindValidated(WallTypeParameters.LogCourse, () => log.CourseHeight, (double v) =>
            {
                if (v < 50 || v > 1000) return false;
                Log = log with { CourseHeight = v };
                return true;
            });
            yield return ParameterValue.BindValidated(WallTypeParameters.LogOverhang, () => log.Overhang, (double v) =>
            {
                if (v < 0 || v > 2000) return false;
                Log = log with { Overhang = v };
                return true;
            });
        }
    }
}

public static class WallTypeParameters
{
    public static readonly ParameterDefinition BuiltAs =
        new("Built As", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition LogShape =
        new("Log Section", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition LogCourse =
        new("Log Course Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition LogOverhang =
        new("Log Corner Overhang", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

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

    public static readonly ParameterDefinition ExteriorTaperAngle =
        new("Exterior Taper Angle", ParameterDataType.Angle, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition InteriorTaperAngle =
        new("Interior Taper Angle", ParameterDataType.Angle, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition WrapAtInserts =
        new("Wrapping at Inserts", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition WrapAtEnds =
        new("Wrapping at Ends", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
}
