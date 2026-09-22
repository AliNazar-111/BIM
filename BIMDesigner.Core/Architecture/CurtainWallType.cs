using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>How the grid lines one way across a curtain wall are set out.</summary>
public enum CurtainGridLayout
{
    /// <summary>No lines: one panel the whole way.</summary>
    None,

    /// <summary>Lines a set distance apart, the leftover split as the justification says.</summary>
    FixedDistance,

    /// <summary>A set number of equal panels, whatever the size of the wall.</summary>
    FixedNumber,

    /// <summary>As few equal panels as keep each no wider than the spacing.</summary>
    MaximumSpacing
}

/// <summary>Where a fixed-distance grid starts from, which is where its odd-sized panels end up.</summary>
public enum CurtainGridJustification
{
    Beginning,
    Centre,
    End
}

/// <summary>What fills one cell of a curtain wall's grid.</summary>
public enum CurtainPanelKind
{
    Glazed,
    Solid,
    Empty,
    Door
}

public enum MullionProfile
{
    Rectangular,
    Circular
}

/// <summary>
/// A curtain wall type (specification section 3.1, "curtain walls"): a wall that is a grid of
/// panels held in mullions, rather than a build-up of layers.
///
/// A curtain wall is still a <see cref="Wall"/> - drawn with the wall tool, joined, bounding
/// rooms - and for all of that it behaves as a thin wall the depth of its mullions, which
/// <see cref="Body"/> describes. Everything that draws or exports it then builds the grid,
/// panels and mullions from <see cref="CurtainLayout"/> instead of that body.
/// </summary>
public sealed class CurtainWallType : ElementType
{
    private WallType? _body;
    private (Guid, double, WallFunction, ColourRgb, string)? _bodyKey;

    public CurtainWallType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Walls;

    public WallFunction Function { get; set; } = WallFunction.Exterior;

    // Grid: vertical lines divide the wall along its length, horizontal ones up its height.
    public CurtainGridLayout VerticalLayout { get; set; } = CurtainGridLayout.FixedDistance;
    public double VerticalSpacing { get; set; } = 1500;
    public int VerticalCount { get; set; } = 4;
    public CurtainGridJustification VerticalJustification { get; set; } = CurtainGridJustification.Centre;

    public CurtainGridLayout HorizontalLayout { get; set; } = CurtainGridLayout.FixedDistance;
    public double HorizontalSpacing { get; set; } = 1500;
    public int HorizontalCount { get; set; } = 2;
    public CurtainGridJustification HorizontalJustification { get; set; } = CurtainGridJustification.Beginning;

    // Panels
    public double PanelThickness { get; set; } = 25;
    public Guid GlassMaterialId { get; set; }
    public Guid SolidMaterialId { get; set; }

    // Mullions
    public MullionProfile MullionProfile { get; set; } = MullionProfile.Rectangular;

    /// <summary>How wide a mullion is seen face-on; the diameter of a round one. Zero for none.</summary>
    public double MullionWidth { get; set; } = 50;

    /// <summary>How deep a rectangular mullion is through the wall.</summary>
    public double MullionDepth { get; set; } = 150;

    public Guid MullionMaterialId { get; set; }

    /// <summary>Whether the edges of the wall are framed as well as the lines between panels.</summary>
    public bool BorderMullions { get; set; } = true;

    /// <summary>
    /// Whether a wall of this type drawn inside another wall cuts itself an opening there, as a
    /// shopfront set into a brick wall does.
    /// </summary>
    public bool AutomaticallyEmbed { get; set; }

    /// <summary>How deep the mullions reach through the wall: the thickness of a round one.</summary>
    public double MullionThickness => MullionProfile == MullionProfile.Circular ? MullionWidth : MullionDepth;

    /// <summary>How thick the wall is for joining and bounding rooms: its mullions or its panels, whichever is more.</summary>
    public double Width => Math.Max(MullionWidth > 0 ? MullionThickness : 0, Math.Max(PanelThickness, 1));

    /// <summary>
    /// The wall this curtain wall behaves as for joins, rooms and hosting: one layer of glass
    /// its full width. Kept, and rebuilt only when what it depends on changes, so every wall of
    /// this type sees the same one.
    /// </summary>
    public WallType Body
    {
        get
        {
            var key = (GlassMaterialId, Width, Function, CoarseScaleFillColour, Name);
            if (_body is null || _bodyKey != key)
            {
                _body = new WallType(Name, CompoundStructure.Single(GlassMaterialId, Width))
                {
                    Id = Id,
                    Function = Function,
                    CoarseScaleFillColour = CoarseScaleFillColour,
                    WrapAtInserts = WallWrapping.None,
                    WrapAtEnds = WallWrapping.None
                };
                _bodyKey = key;
            }

            return _body;
        }
    }

    /// <summary>The fill colour of the wall's outline when drawn coarse.</summary>
    public ColourRgb CoarseScaleFillColour { get; set; } = ColourRgb.FromHex("7FB3D0");

    /// <summary>A new type with this one's settings, to change without touching any wall of this type.</summary>
    public CurtainWallType Duplicate(string name) => new(name)
    {
        TypeMark = TypeMark,
        AssemblyCode = AssemblyCode,
        Keynote = Keynote,
        Manufacturer = Manufacturer,
        Url = Url,
        Description = Description,
        Cost = Cost,
        Function = Function,
        VerticalLayout = VerticalLayout,
        VerticalSpacing = VerticalSpacing,
        VerticalCount = VerticalCount,
        VerticalJustification = VerticalJustification,
        HorizontalLayout = HorizontalLayout,
        HorizontalSpacing = HorizontalSpacing,
        HorizontalCount = HorizontalCount,
        HorizontalJustification = HorizontalJustification,
        PanelThickness = PanelThickness,
        GlassMaterialId = GlassMaterialId,
        SolidMaterialId = SolidMaterialId,
        MullionProfile = MullionProfile,
        MullionWidth = MullionWidth,
        MullionDepth = MullionDepth,
        MullionMaterialId = MullionMaterialId,
        BorderMullions = BorderMullions,
        AutomaticallyEmbed = AutomaticallyEmbed,
        CoarseScaleFillColour = CoarseScaleFillColour
    };

    /// <summary>Copies every setting of another type onto this one, keeping its own id. For undo.</summary>
    public void CopyFrom(CurtainWallType other)
    {
        Name = other.Name;
        TypeMark = other.TypeMark;
        Function = other.Function;
        VerticalLayout = other.VerticalLayout;
        VerticalSpacing = other.VerticalSpacing;
        VerticalCount = other.VerticalCount;
        VerticalJustification = other.VerticalJustification;
        HorizontalLayout = other.HorizontalLayout;
        HorizontalSpacing = other.HorizontalSpacing;
        HorizontalCount = other.HorizontalCount;
        HorizontalJustification = other.HorizontalJustification;
        PanelThickness = other.PanelThickness;
        GlassMaterialId = other.GlassMaterialId;
        SolidMaterialId = other.SolidMaterialId;
        MullionProfile = other.MullionProfile;
        MullionWidth = other.MullionWidth;
        MullionDepth = other.MullionDepth;
        MullionMaterialId = other.MullionMaterialId;
        BorderMullions = other.BorderMullions;
        AutomaticallyEmbed = other.AutomaticallyEmbed;
    }

    /// <summary>What is wrong with these settings, or null if a wall can be built from them.</summary>
    public string? Problem()
    {
        if (VerticalLayout is CurtainGridLayout.FixedDistance or CurtainGridLayout.MaximumSpacing && !(VerticalSpacing >= 100))
            return "The vertical grid spacing must be at least 100 mm.";
        if (HorizontalLayout is CurtainGridLayout.FixedDistance or CurtainGridLayout.MaximumSpacing && !(HorizontalSpacing >= 100))
            return "The horizontal grid spacing must be at least 100 mm.";
        if (VerticalLayout == CurtainGridLayout.FixedNumber && VerticalCount is < 1 or > 200)
            return "The number of panels along the wall must be between 1 and 200.";
        if (HorizontalLayout == CurtainGridLayout.FixedNumber && HorizontalCount is < 1 or > 200)
            return "The number of panels up the wall must be between 1 and 200.";
        if (!(PanelThickness >= 1 && PanelThickness <= 500)) return "Panels must be between 1 and 500 mm thick.";
        if (!(MullionWidth >= 0 && MullionWidth <= 1000)) return "Mullions must be between 0 and 1000 mm wide.";
        if (MullionProfile == MullionProfile.Rectangular && MullionWidth > 0 && !(MullionDepth >= 1 && MullionDepth <= 2000))
            return "Mullions must be between 1 and 2000 mm deep.";
        return null;
    }

    public override IEnumerable<ParameterValue> GetTypeParameters()
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.ReadOnly(WallTypeParameters.Width, () => Width);
        yield return ParameterValue.ReadOnly(WallTypeParameters.Function, () => EnumText.Humanise(Function));
        yield return ParameterValue.ReadOnly(CurtainWallTypeParameters.VerticalGrid, () => GridText(VerticalLayout, VerticalSpacing, VerticalCount));
        yield return ParameterValue.ReadOnly(CurtainWallTypeParameters.HorizontalGrid, () => GridText(HorizontalLayout, HorizontalSpacing, HorizontalCount));
        yield return ParameterValue.ReadOnly(CurtainWallTypeParameters.Mullions, () => MullionWidth <= 0
            ? "None"
            : MullionProfile == MullionProfile.Circular
                ? $"Round, {MullionWidth:0} mm"
                : $"{MullionWidth:0} x {MullionDepth:0} mm");
        yield return ParameterValue.ReadOnly(CurtainWallTypeParameters.AutomaticallyEmbed, () => AutomaticallyEmbed);
    }

    private static string GridText(CurtainGridLayout layout, double spacing, int count) => layout switch
    {
        CurtainGridLayout.None => "None",
        CurtainGridLayout.FixedNumber => $"{count} panels",
        CurtainGridLayout.MaximumSpacing => $"At most {spacing:0} mm",
        _ => $"Every {spacing:0} mm"
    };
}

public static class CurtainWallTypeParameters
{
    public static readonly ParameterDefinition VerticalGrid =
        new("Vertical Grid", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition HorizontalGrid =
        new("Horizontal Grid", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Mullions =
        new("Mullions", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition AutomaticallyEmbed =
        new("Automatically Embed", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
}
