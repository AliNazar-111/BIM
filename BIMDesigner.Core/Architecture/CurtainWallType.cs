using BIMDesigner.Core.Documents;
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

/// <summary>
/// What a glazed panel of a curtain wall is made of. A shopfront is mostly clear glass; tinted
/// and frosted are chosen against glare and for privacy; laminated is two panes bonded together,
/// so it is thicker; a spandrel panel is opaque glass hiding the floor structure behind it.
/// </summary>
public enum CurtainGlass
{
    Clear,
    Tinted,
    Frosted,
    Laminated,
    Spandrel
}

/// <summary>How each kind of glass reads: its colour, how far it is seen through, and how thick.</summary>
public static class CurtainGlassLook
{
    /// <summary>The colour it is drawn in; clear glass keeps whatever its material says.</summary>
    public static ColourRgb ColourOf(CurtainGlass glass, ColourRgb clear) => glass switch
    {
        CurtainGlass.Tinted => new ColourRgb(0x46, 0x66, 0x70),
        CurtainGlass.Frosted => new ColourRgb(0xCA, 0xDA, 0xE0),
        CurtainGlass.Laminated => new ColourRgb(0x84, 0xBC, 0xC2),
        CurtainGlass.Spandrel => new ColourRgb(0x2C, 0x36, 0x3E),
        _ => clear
    };

    /// <summary>How solid it looks in 3D: clear glass is barely there, a spandrel panel not at all.</summary>
    public static double OpacityOf(CurtainGlass glass) => glass switch
    {
        CurtainGlass.Tinted => 0.55,
        CurtainGlass.Frosted => 0.75,
        CurtainGlass.Laminated => 0.42,
        CurtainGlass.Spandrel => 1,
        _ => 0.35
    };

    /// <summary>How thick it is against a single pane: laminated glass is two panes and an interlayer.</summary>
    public static double ThicknessOf(CurtainGlass glass, double pane) =>
        glass == CurtainGlass.Laminated ? pane * 1.6 : pane;

    /// <summary>What it is called, which is what the 3D view and schedules show.</summary>
    public static string NameOf(CurtainGlass glass) => glass switch
    {
        CurtainGlass.Tinted => "Glass, Tinted",
        CurtainGlass.Frosted => "Glass, Frosted",
        CurtainGlass.Laminated => "Glass, Laminated",
        CurtainGlass.Spandrel => "Glass, Spandrel",
        _ => "Glass, Clear"
    };
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

    // The mullion types on each line - Revit's Interior, Border 1 and Border 2 types, each way:
    // none set, the type's own mullion above; Guid.Empty, none there; otherwise a mullion type.

    /// <summary>The vertical mullions between panels.</summary>
    public Guid? InteriorVertical { get; set; }

    /// <summary>The vertical mullion at the wall's start, and at its end.</summary>
    public Guid? BorderVerticalStart { get; set; }

    public Guid? BorderVerticalEnd { get; set; }

    /// <summary>The transoms between panels.</summary>
    public Guid? InteriorHorizontal { get; set; }

    /// <summary>The transom along the wall's bottom, and along its top.</summary>
    public Guid? BorderHorizontalBottom { get; set; }

    public Guid? BorderHorizontalTop { get; set; }

    /// <summary>Which run through where they meet: the verticals, the transoms stopping at them - or the transoms.</summary>
    public CurtainJoin JoinCondition { get; set; } = CurtainJoin.VerticalContinuous;

    /// <summary>Whether each vertical mullion is anchored to the floors it passes, its base and its head - brackets drawn and their loads worked out.</summary>
    public bool MullionAnchors { get; set; }

    /// <summary>Where the vertical mullions are spliced: nowhere, one length the whole height; above every floor; above every other.</summary>
    public MullionSplices Splices { get; set; } = MullionSplices.None;

    /// <summary>How far above the floor a splice is, mm: just over the anchor there.</summary>
    public double SpliceAbove { get; set; } = 300;

    /// <summary>The stack joint at each splice, mm: the gap the length above stands off the one below by, for them to move in.</summary>
    public double StackJoint { get; set; } = 20;

    /// <summary>What one anchor is good for, kN, from its maker; none given at 0, and its loads are only said.</summary>
    public double AnchorCapacity { get; set; }

    private MullionType? _own;
    private (MullionProfile, double, double, Guid)? _ownKey;

    /// <summary>The type's own mullion, as its profile, width, depth and material say it: a box or a tube centred on its line. None at no width.</summary>
    public MullionType? OwnMullion
    {
        get
        {
            if (MullionWidth <= 0) return null;
            var key = (MullionProfile, MullionWidth, MullionDepth, MullionMaterialId);
            if (_own is null || _ownKey != key)
            {
                _own = new MullionType($"{Name} mullion")
                {
                    Shape = MullionProfile == MullionProfile.Circular ? MullionShape.Circular : MullionShape.Rectangular,
                    Width1 = MullionWidth / 2, Width2 = MullionWidth / 2, Thickness = MullionDepth, MaterialId = MullionMaterialId
                };
                _ownKey = key;
            }

            return _own;
        }
    }

    /// <summary>
    /// The mullion type a slot comes to: the type's own mullion where none is set - at a border only
    /// with border mullions - none where it says none, and the project's type of that id otherwise.
    /// With no project to look in, its own.
    /// </summary>
    public MullionType? Resolve(BimDocument? document, Guid? slot, bool border) => slot switch
    {
        null => border && !BorderMullions ? null : OwnMullion,
        { } id when id == Guid.Empty => null,
        { } id => document?.FindType<MullionType>(id) ?? (border && !BorderMullions ? null : OwnMullion)
    };

    /// <summary>Every mullion type its slots come to, in a project.</summary>
    public IEnumerable<MullionType> MullionTypes(BimDocument? document) =>
        new[] { (InteriorVertical, false), (BorderVerticalStart, true), (BorderVerticalEnd, true), (InteriorHorizontal, false), (BorderHorizontalBottom, true), (BorderHorizontalTop, true) }
            .Select(slot => Resolve(document, slot.Item1, slot.Item2))
            .OfType<MullionType>();

    /// <summary>
    /// Whether a wall of this type drawn inside another wall cuts itself an opening there, as a
    /// shopfront set into a brick wall does.
    /// </summary>
    public bool AutomaticallyEmbed { get; set; }

    /// <summary>How deep the mullions reach through the wall: the thickness of a round one - or of the deepest mullion type in its slots.</summary>
    public double MullionThickness => Math.Max(MullionWidth > 0 ? (MullionProfile == MullionProfile.Circular ? MullionWidth : MullionDepth) : 0, _slotDepth);

    private double _slotDepth;

    /// <summary>Takes the depth of the mullion types in its slots, so the wall is as thick as its deepest - called as a project is built or read, and when its types change.</summary>
    public void TakeDepth(BimDocument document) =>
        _slotDepth = MullionTypes(document).Select(type => type.Depth + 2 * Math.Abs(type.Offset)).DefaultIfEmpty(0).Max();

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

    /// <summary>Its U-value as a whole, glass and frame, W/m²K: 1.6 at most, Approved Document L asks of curtain walling (Table 4.1).</summary>
    public double HeatTransferCoefficient { get; set; } = 1.4;

    /// <summary>Whether its glass is safety glass - toughened or laminated - as glass low in a wall must be (Approved Document K; IRC R308).</summary>
    public bool SafetyGlazing { get; set; } = true;

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
        InteriorVertical = InteriorVertical,
        BorderVerticalStart = BorderVerticalStart,
        BorderVerticalEnd = BorderVerticalEnd,
        InteriorHorizontal = InteriorHorizontal,
        BorderHorizontalBottom = BorderHorizontalBottom,
        BorderHorizontalTop = BorderHorizontalTop,
        JoinCondition = JoinCondition,
        MullionAnchors = MullionAnchors,
        Splices = Splices,
        SpliceAbove = SpliceAbove,
        StackJoint = StackJoint,
        AnchorCapacity = AnchorCapacity,
        _slotDepth = _slotDepth,
        AutomaticallyEmbed = AutomaticallyEmbed,
        CoarseScaleFillColour = CoarseScaleFillColour,
        HeatTransferCoefficient = HeatTransferCoefficient,
        SafetyGlazing = SafetyGlazing
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
        InteriorVertical = other.InteriorVertical;
        BorderVerticalStart = other.BorderVerticalStart;
        BorderVerticalEnd = other.BorderVerticalEnd;
        InteriorHorizontal = other.InteriorHorizontal;
        BorderHorizontalBottom = other.BorderHorizontalBottom;
        BorderHorizontalTop = other.BorderHorizontalTop;
        JoinCondition = other.JoinCondition;
        MullionAnchors = other.MullionAnchors;
        Splices = other.Splices;
        SpliceAbove = other.SpliceAbove;
        StackJoint = other.StackJoint;
        AnchorCapacity = other.AnchorCapacity;
        _slotDepth = other._slotDepth;
        AutomaticallyEmbed = other.AutomaticallyEmbed;
        HeatTransferCoefficient = other.HeatTransferCoefficient;
        SafetyGlazing = other.SafetyGlazing;
    }

    /// <summary>What is wrong with these settings, or null if a wall can be built from them.</summary>
    public string? Problem()
    {
        if (VerticalLayout is CurtainGridLayout.FixedDistance or CurtainGridLayout.MaximumSpacing && !(VerticalSpacing >= 100))
            return $"The vertical grid spacing must be at least {Units.Millimetres(100, "0")}.";
        if (HorizontalLayout is CurtainGridLayout.FixedDistance or CurtainGridLayout.MaximumSpacing && !(HorizontalSpacing >= 100))
            return $"The horizontal grid spacing must be at least {Units.Millimetres(100, "0")}.";
        if (VerticalLayout == CurtainGridLayout.FixedNumber && VerticalCount is < 1 or > 200)
            return "The number of panels along the wall must be between 1 and 200.";
        if (HorizontalLayout == CurtainGridLayout.FixedNumber && HorizontalCount is < 1 or > 200)
            return "The number of panels up the wall must be between 1 and 200.";
        if (!(PanelThickness >= 1 && PanelThickness <= 500)) return $"Panels must be between {Units.Bare(1)} and {Units.Millimetres(500, "0")} thick.";
        if (!(MullionWidth >= 0 && MullionWidth <= 1000)) return $"Mullions must be between {Units.Bare(0)} and {Units.Millimetres(1000, "0")} wide.";
        if (MullionProfile == MullionProfile.Rectangular && MullionWidth > 0 && !(MullionDepth >= 1 && MullionDepth <= 2000))
            return $"Mullions must be between {Units.Bare(1)} and {Units.Millimetres(2000, "0")} deep.";
        return null;
    }

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.ReadOnly(WallTypeParameters.Width, () => Width);
        yield return ParameterValue.ReadOnly(WallTypeParameters.Function, () => EnumText.Humanise(Function));
        yield return ParameterValue.ReadOnly(CurtainWallTypeParameters.VerticalGrid, () => GridText(VerticalLayout, VerticalSpacing, VerticalCount));
        yield return ParameterValue.ReadOnly(CurtainWallTypeParameters.HorizontalGrid, () => GridText(HorizontalLayout, HorizontalSpacing, HorizontalCount));
        yield return ParameterValue.ReadOnly(CurtainWallTypeParameters.Mullions, () => MullionWidth <= 0
            ? "None"
            : MullionProfile == MullionProfile.Circular
                ? $"Round, {Units.Millimetres(MullionWidth, "0")}"
                : $"{Units.Bare(MullionWidth)} x {Units.Millimetres(MullionDepth, "0")}");
        // Each line's mullion type, chosen from the project's: its own, none, or a type.
        if (document is not null)
        {
            var own = OwnMullion is { } mine ? $"<its own: {(mine.Shape == MullionShape.Circular ? $"round {Units.Bare(MullionWidth)}" : $"{Units.Bare(MullionWidth)} x {Units.Bare(MullionDepth)}")}>" : "<its own: none>";
            var choices = new[] { own, "None" }.Concat(document.TypesOf<MullionType>().Select(type => type.Name).OrderBy(name => name)).ToArray();
            ParameterValue Slot(ParameterDefinition definition, Func<Guid?> get, Action<Guid?> set) => ParameterValue.BindChoice(definition,
                () => get() switch { null => own, { } id when id == Guid.Empty => "None", { } id => document.FindType<MullionType>(id)?.Name ?? own },
                v =>
                {
                    set(v == own ? null : v == "None" ? Guid.Empty : document.TypesOf<MullionType>().FirstOrDefault(type => type.Name == v)?.Id);
                    TakeDepth(document);
                },
                choices);
            yield return Slot(CurtainWallTypeParameters.InteriorVertical, () => InteriorVertical, v => InteriorVertical = v);
            yield return Slot(CurtainWallTypeParameters.BorderVerticalStart, () => BorderVerticalStart, v => BorderVerticalStart = v);
            yield return Slot(CurtainWallTypeParameters.BorderVerticalEnd, () => BorderVerticalEnd, v => BorderVerticalEnd = v);
            yield return Slot(CurtainWallTypeParameters.InteriorHorizontal, () => InteriorHorizontal, v => InteriorHorizontal = v);
            yield return Slot(CurtainWallTypeParameters.BorderHorizontalBottom, () => BorderHorizontalBottom, v => BorderHorizontalBottom = v);
            yield return Slot(CurtainWallTypeParameters.BorderHorizontalTop, () => BorderHorizontalTop, v => BorderHorizontalTop = v);
            yield return ParameterValue.BindChoice(CurtainWallTypeParameters.JoinCondition, () => EnumText.Humanise(JoinCondition),
                v => { if (EnumText.TryParse<CurtainJoin>(v, out var join)) JoinCondition = join; }, EnumText.Choices<CurtainJoin>());
        }

        // How the mullions are fixed to the building and joined up its height.
        yield return ParameterValue.Bind(CurtainWallTypeParameters.MullionAnchors, () => MullionAnchors, v => MullionAnchors = v);
        yield return ParameterValue.BindChoice(CurtainWallTypeParameters.Splices, () => EnumText.Humanise(Splices),
            v => { if (EnumText.TryParse<MullionSplices>(v, out var splices)) Splices = splices; }, EnumText.Choices<MullionSplices>());
        if (Splices != MullionSplices.None)
        {
            yield return ParameterValue.BindValidated(CurtainWallTypeParameters.SpliceAbove, () => SpliceAbove, v => { if (v < 0 || v > 2000) return false; SpliceAbove = v; return true; });
            yield return ParameterValue.BindValidated(CurtainWallTypeParameters.StackJoint, () => StackJoint, v => { if (v < 1 || v > 100) return false; StackJoint = v; return true; });
        }

        if (MullionAnchors)
            yield return ParameterValue.BindScaled(CurtainWallTypeParameters.AnchorCapacity, CurtainWallTypeParameters.AnchorCapacityPounds, Units.PoundsPerKilonewton, 0,
                () => AnchorCapacity, v => { if (v < 0 || v > 500) return false; AnchorCapacity = v; return true; });

        yield return ParameterValue.ReadOnly(CurtainWallTypeParameters.AutomaticallyEmbed, () => AutomaticallyEmbed);
        yield return ParameterValue.BindScaled(WallTypeParameters.HeatTransferCoefficient, OpeningTypeParameters.UFactor, Units.UFactorPerUValue, 3,
            () => HeatTransferCoefficient, v => { if (v < 0) return false; HeatTransferCoefficient = v; return true; });
        yield return ParameterValue.Bind(Units.Imperial ? OpeningTypeParameters.SafetyGlazingUs : OpeningTypeParameters.SafetyGlazing, () => SafetyGlazing, v => SafetyGlazing = v);
    }

    private static string GridText(CurtainGridLayout layout, double spacing, int count) => layout switch
    {
        CurtainGridLayout.None => "None",
        CurtainGridLayout.FixedNumber => $"{count} panels",
        CurtainGridLayout.MaximumSpacing => $"At most {Units.Millimetres(spacing, "0")}",
        _ => $"Every {Units.Millimetres(spacing, "0")}"
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

    public static readonly ParameterDefinition InteriorVertical = new("Vertical Mullions: Interior Type", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition BorderVerticalStart = new("Vertical Mullions: Border 1 Type", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition BorderVerticalEnd = new("Vertical Mullions: Border 2 Type", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition InteriorHorizontal = new("Horizontal Mullions: Interior Type", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition BorderHorizontalBottom = new("Horizontal Mullions: Border 1 Type", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition BorderHorizontalTop = new("Horizontal Mullions: Border 2 Type", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition JoinCondition = new("Join Condition", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition MullionAnchors = new("Mullions Anchored at Floors", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Splices = new("Mullion Splices", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition SpliceAbove = new("Splice Above Floor", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition StackJoint = new("Stack Joint", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition AnchorCapacity = new("Anchor Capacity (kN, 0 = not checked)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition AnchorCapacityPounds = new("Anchor Capacity (lb, 0 = not checked)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Construction);
}
