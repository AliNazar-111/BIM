using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>What fills a railing between its posts, under its top rail.</summary>
public enum RailingInfill
{
    /// <summary>Upright bars, close enough that a child's head does not pass between them.</summary>
    Balusters,

    /// <summary>Toughened, laminated glass panels between the posts.</summary>
    Glass,

    /// <summary>Rails running along between the posts - a ladder a child can climb.</summary>
    HorizontalBars,

    /// <summary>
    /// Glass alone, standing in a channel fixed to the floor - no posts - its panes side by side
    /// with a joint between, with or without a rail along their tops.
    /// </summary>
    FramelessGlass,

    /// <summary>Solid panels between the posts: boards, sheet metal, a close-boarded fence.</summary>
    Panels,

    /// <summary>Panels of mesh or perforated sheet between the posts, too fine to climb.</summary>
    Mesh,

    /// <summary>Taut wires running along between the posts - as climbable as rails along.</summary>
    Cables
}

/// <summary>What a railing is for: guarding a drop, or a fence round a garden, which guards nothing.</summary>
public enum RailingUse
{
    Guarding,
    Fence
}

/// <summary>Which side of a railing a handrail runs along, going along it from its first point: its left - the inside of one Guard Edges puts up - its right, or both.</summary>
public enum RailingHandrail
{
    None,
    Left,
    Right,
    Both
}

/// <summary>The building a railing guards in: a dwelling, or any other - which asks more of it.</summary>
public enum RailingBuilding
{
    Dwelling,
    Other
}

/// <summary>
/// A railing type: how high it stands, its posts and how far apart they may be, its top rail,
/// and what fills it under the rail - balusters, glass or rails along.
/// </summary>
public sealed class RailingType : ElementType
{
    public RailingType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Railings;

    /// <summary>The top of its top rail above what it stands on, mm.</summary>
    public double Height { get; set; } = 1100;

    public RailingInfill Infill { get; set; } = RailingInfill.Balusters;

    /// <summary>Its top rail's section: round, or rectangular - a capping.</summary>
    public HandrailProfile TopRailProfile { get; set; } = HandrailProfile.Rectangular;

    /// <summary>A round top rail's diameter, or a rectangular one's width, mm.</summary>
    public double TopRailWidth { get; set; } = 60;

    /// <summary>A rectangular top rail's depth, mm.</summary>
    public double TopRailDepth { get; set; } = 40;

    /// <summary>Its posts, square, mm.</summary>
    public double PostSize { get; set; } = 50;

    /// <summary>The most its posts may be apart, centre to centre, mm: each straight length is shared equally among as few bays as keep within it.</summary>
    public double PostSpacing { get; set; } = 1500;

    /// <summary>Its balusters, square, mm.</summary>
    public double BalusterSize { get; set; } = 16;

    /// <summary>The most its balusters may be apart, centre to centre, mm.</summary>
    public double BalusterSpacing { get; set; } = 110;

    /// <summary>The gap under its bottom rail or its glass, above what it stands on, mm.</summary>
    public double BottomGap { get; set; } = 50;

    /// <summary>Its glass's thickness, mm: two sheets of 8 toughened glass laminated together.</summary>
    public double GlassThickness { get; set; } = 17.5;

    /// <summary>What it reads as with no material: steel, painted dark grey.</summary>
    public ColourRgb Colour { get; set; } = new(0x3C, 0x3F, 0x44);

    /// <summary>What its posts and rails are made of; none for its colour.</summary>
    public Guid MaterialId { get; set; }

    /// <summary>What its balusters or rails along are made of; none for as its posts.</summary>
    public Guid InfillMaterialId { get; set; }

    /// <summary>Guarding a drop, or a fence: a fence is held to none of guarding's rules.</summary>
    public RailingUse Use { get; set; } = RailingUse.Guarding;

    /// <summary>Whether it has a rail along its top: frameless glass may stand without one.</summary>
    public bool HasTopRail { get; set; } = true;

    /// <summary>
    /// Whether its glass is toughened and laminated - two sheets with an interlayer that holds them
    /// in place when one breaks - rather than one toughened sheet, which falls out when it does.
    /// </summary>
    public bool GlassLaminated { get; set; } = true;

    /// <summary>The joint between panes of frameless glass, mm.</summary>
    public double GlassJoint { get; set; } = 10;

    /// <summary>How high the channel frameless glass stands in is above the floor, mm: none, where it is let into the floor.</summary>
    public double ChannelHeight { get; set; } = 100;

    /// <summary>How thick its panels or mesh are, mm.</summary>
    public double PanelThickness { get; set; } = 20;

    /// <summary>Rails along across its balusters or panels, between its bottom rail and its top - Revit's rail structure.</summary>
    public int IntermediateRails { get; set; }

    /// <summary>Which side a handrail runs along it, if one does.</summary>
    public RailingHandrail Handrail { get; set; } = RailingHandrail.None;

    /// <summary>The top of its handrail above what it stands on, mm.</summary>
    public double HandrailHeight { get; set; } = 900;

    /// <summary>Its handrail's diameter, mm: 32 to 50 is what a hand can grip.</summary>
    public double HandrailDiameter { get; set; } = 42;

    /// <summary>The clear gap between its handrail and the face it is fixed to, mm: room for a hand.</summary>
    public double HandrailClearance { get; set; } = 50;

    /// <summary>How far its handrail runs on, level, past each end, mm - where its ends are not stopped on a post.</summary>
    public double HandrailExtension { get; set; } = 300;

    /// <summary>How its handrail finishes at each end: turned into the wall there, down to the floor, stopped on its end post, or cut off.</summary>
    public HandrailEnds HandrailEnds { get; set; } = HandrailEnds.Post;

    /// <summary>The line load, along its top, it is made to take, kN/m.</summary>
    public double LineLoad { get; set; } = 0.74;

    /// <summary>The load at a point on its top it is made to take, kN.</summary>
    public double PointLoad { get; set; } = 0.5;

    public RailingType Duplicate(string name) => WithCommonOf(new RailingType(name)
    {
        Height = Height, Infill = Infill, TopRailProfile = TopRailProfile, TopRailWidth = TopRailWidth, TopRailDepth = TopRailDepth,
        PostSize = PostSize, PostSpacing = PostSpacing, BalusterSize = BalusterSize, BalusterSpacing = BalusterSpacing,
        BottomGap = BottomGap, GlassThickness = GlassThickness, Colour = Colour, MaterialId = MaterialId, InfillMaterialId = InfillMaterialId,
        Use = Use, HasTopRail = HasTopRail, GlassLaminated = GlassLaminated, GlassJoint = GlassJoint, ChannelHeight = ChannelHeight,
        PanelThickness = PanelThickness, IntermediateRails = IntermediateRails, Handrail = Handrail, HandrailHeight = HandrailHeight,
        HandrailDiameter = HandrailDiameter, HandrailClearance = HandrailClearance, HandrailExtension = HandrailExtension, HandrailEnds = HandrailEnds,
        LineLoad = LineLoad, PointLoad = PointLoad
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(RailingTypeParameters.Use,
            () => EnumText.Humanise(Use),
            v => { if (EnumText.TryParse<RailingUse>(v, out var use)) Use = use; },
            EnumText.Choices<RailingUse>());
        yield return Size(RailingTypeParameters.Height, () => Height, v => Height = v, 300, 2500);
        yield return ParameterValue.BindChoice(RailingTypeParameters.Infill,
            () => EnumText.Humanise(Infill),
            v => { if (EnumText.TryParse<RailingInfill>(v, out var infill)) Infill = infill; },
            EnumText.Choices<RailingInfill>());
        yield return ParameterValue.Bind(RailingTypeParameters.HasTopRail, () => HasTopRail, v => HasTopRail = v);
        yield return ParameterValue.BindChoice(RailingTypeParameters.TopRailProfile,
            () => EnumText.Humanise(TopRailProfile),
            v => { if (EnumText.TryParse<HandrailProfile>(v, out var profile)) TopRailProfile = profile; },
            EnumText.Choices<HandrailProfile>());
        yield return Size(RailingTypeParameters.TopRailWidth, () => TopRailWidth, v => TopRailWidth = v, 20, 200);
        yield return Size(RailingTypeParameters.TopRailDepth, () => TopRailDepth, v => TopRailDepth = v, 15, 200);
        yield return Size(RailingTypeParameters.PostSize, () => PostSize, v => PostSize = v, 20, 200);
        yield return Size(RailingTypeParameters.PostSpacing, () => PostSpacing, v => PostSpacing = v, 300, 3000);
        yield return Size(RailingTypeParameters.BalusterSize, () => BalusterSize, v => BalusterSize = v, 8, 100);
        yield return Size(RailingTypeParameters.BalusterSpacing, () => BalusterSpacing, v => BalusterSpacing = v, 40, 400);
        yield return Size(RailingTypeParameters.BottomGap, () => BottomGap, v => BottomGap = v, 0, 300);
        yield return Size(RailingTypeParameters.GlassThickness, () => GlassThickness, v => GlassThickness = v, 6, 60);
        yield return ParameterValue.Bind(RailingTypeParameters.GlassLaminated, () => GlassLaminated, v => GlassLaminated = v);
        yield return Size(RailingTypeParameters.GlassJoint, () => GlassJoint, v => GlassJoint = v, 0, 200);
        yield return Size(RailingTypeParameters.ChannelHeight, () => ChannelHeight, v => ChannelHeight = v, 0, 300);
        yield return Size(RailingTypeParameters.PanelThickness, () => PanelThickness, v => PanelThickness = v, 2, 100);
        yield return ParameterValue.BindValidated(RailingTypeParameters.IntermediateRails, () => IntermediateRails, (int v) =>
        {
            if (v < 0 || v > 10) return false;
            IntermediateRails = v;
            return true;
        });
        yield return ParameterValue.BindChoice(RailingTypeParameters.Handrail,
            () => EnumText.Humanise(Handrail),
            v => { if (EnumText.TryParse<RailingHandrail>(v, out var handrail)) Handrail = handrail; },
            EnumText.Choices<RailingHandrail>());
        yield return Size(RailingTypeParameters.HandrailHeight, () => HandrailHeight, v => HandrailHeight = v, 300, 1500);
        yield return Size(RailingTypeParameters.HandrailDiameter, () => HandrailDiameter, v => HandrailDiameter = v, 20, 100);
        yield return Size(RailingTypeParameters.HandrailClearance, () => HandrailClearance, v => HandrailClearance = v, 0, 200);
        yield return Size(RailingTypeParameters.HandrailExtension, () => HandrailExtension, v => HandrailExtension = v, 0, 1000);
        yield return ParameterValue.BindChoice(RailingTypeParameters.HandrailEnds,
            () => EnumText.Humanise(HandrailEnds),
            v => { if (EnumText.TryParse<HandrailEnds>(v, out var ends)) HandrailEnds = ends; },
            EnumText.Choices<HandrailEnds>());
        yield return Size(RailingTypeParameters.LineLoad, () => LineLoad, v => LineLoad = v, 0, 10);
        yield return Size(RailingTypeParameters.PointLoad, () => PointLoad, v => PointLoad = v, 0, 10);

        if (document is not null)
        {
            var names = document.Materials.Select(material => material.Name).OrderBy(name => name).ToArray();
            ParameterValue Material(ParameterDefinition definition, string usual, Func<Guid> get, Action<Guid> set) =>
                ParameterValue.BindChoice(definition,
                    () => document.FindMaterial(get())?.Name ?? usual,
                    v => set(document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty),
                    names.Prepend(usual).ToArray());

            yield return Material(RailingTypeParameters.Material, ComponentType.ByColour, () => MaterialId, id => MaterialId = id);
            yield return Material(RailingTypeParameters.InfillMaterial, "As usual", () => InfillMaterialId, id => InfillMaterialId = id);
        }

        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }

    private static ParameterValue Size(ParameterDefinition definition, Func<double> get, Action<double> set, double least, double most) =>
        ParameterValue.BindValidated(definition, get, (double v) =>
        {
            if (v < least || v > most) return false;
            set(v);
            return true;
        });

    /// <summary>
    /// The railings a project starts with: 1100 guarding for a balcony or a roof terrace, and 900
    /// for a floor's edge inside a house, with balusters; 1100 of glass between posts, and of
    /// frameless glass in a channel; 1100 of rails along, of mesh and of cables; a glass Juliet
    /// balcony; glass with a handrail; the IRC's 36 in and the IBC's 42 in; and three fences.
    /// Their balusters are close enough that a 100 mm sphere - 4 in under the IRC - does not
    /// pass, and each is made for the loads its kind of place asks.
    /// </summary>
    public static IReadOnlyList<RailingType> Library() => new[]
    {
        new RailingType("Guard 1100 - Steel Balusters") { TypeMark = "GR1", Cost = 220m },
        new RailingType("Guard 900 - Timber Balusters")
        {
            Height = 900, PostSize = 90, PostSpacing = 1800, TopRailDepth = 50, TopRailWidth = 70, BalusterSize = 32, BalusterSpacing = 120,
            Colour = new ColourRgb(0xC8, 0xA8, 0x7A), LineLoad = 0.36, PointLoad = 0.25, TypeMark = "GR2", Cost = 160m
        },
        new RailingType("Guard 1100 - Glass Panel")
        {
            Infill = RailingInfill.Glass, TopRailProfile = HandrailProfile.Round, TopRailWidth = 42, PostSize = 50, PostSpacing = 1200,
            BottomGap = 60, Colour = new ColourRgb(0xBF, 0xC4, 0xC9), TypeMark = "GR3", Cost = 420m
        },
        new RailingType("Guard 1100 - Steel Rails") { Infill = RailingInfill.HorizontalBars, TypeMark = "GR4", Cost = 180m },
        new RailingType("Guard 36 in - Wood Balusters")
        {
            Height = 914, PostSize = 89, PostSpacing = 1830, TopRailWidth = 89, TopRailDepth = 38, BalusterSize = 38, BalusterSpacing = 135,
            BottomGap = 75, Colour = new ColourRgb(0xA8, 0x86, 0x5C), LineLoad = 0.73, PointLoad = 0.89, TypeMark = "GR5", Cost = 150m
        },
        new RailingType("Guard 42 in - Glass Panel")
        {
            Height = 1067, Infill = RailingInfill.Glass, TopRailProfile = HandrailProfile.Round, TopRailWidth = 42, PostSpacing = 1200,
            BottomGap = 60, Colour = new ColourRgb(0xBF, 0xC4, 0xC9), LineLoad = 0.73, PointLoad = 0.89, TypeMark = "GR6", Cost = 450m
        },
        new RailingType("Guard 1100 - Frameless Glass")
        {
            Infill = RailingInfill.FramelessGlass, HasTopRail = false, GlassThickness = 21.5, PostSpacing = 1200, BottomGap = 0,
            Colour = new ColourRgb(0xBF, 0xC4, 0xC9), LineLoad = 1.5, PointLoad = 1.5, TypeMark = "GR7", Cost = 520m
        },
        new RailingType("Guard 1100 - Glass with Handrail")
        {
            Infill = RailingInfill.Glass, TopRailProfile = HandrailProfile.Round, TopRailWidth = 42, PostSpacing = 1200, BottomGap = 60,
            Handrail = RailingHandrail.Left, HandrailHeight = 900, Colour = new ColourRgb(0xBF, 0xC4, 0xC9), TypeMark = "GR8", Cost = 480m
        },
        new RailingType("Guard 1100 - Steel Mesh")
        {
            Infill = RailingInfill.Mesh, PanelThickness = 4, PostSpacing = 1500, TypeMark = "GR9", Cost = 200m
        },
        new RailingType("Guard 1100 - Stainless Cables")
        {
            Infill = RailingInfill.Cables, TopRailProfile = HandrailProfile.Round, TopRailWidth = 42, BalusterSpacing = 80, PostSpacing = 1200,
            Colour = new ColourRgb(0xBF, 0xC4, 0xC9), TypeMark = "GR10", Cost = 260m
        },
        new RailingType("Juliet 1100 - Glass")
        {
            Infill = RailingInfill.Glass, TopRailProfile = HandrailProfile.Round, TopRailWidth = 42, PostSize = 40, PostSpacing = 2400,
            BottomGap = 60, Colour = new ColourRgb(0xBF, 0xC4, 0xC9), TypeMark = "JB1", Cost = 380m
        },
        new RailingType("Fence 1800 - Close Board Timber")
        {
            Use = RailingUse.Fence, Height = 1800, Infill = RailingInfill.Panels, PostSize = 100, PostSpacing = 1800, BottomGap = 150,
            TopRailWidth = 100, TopRailDepth = 25, PanelThickness = 22, Colour = new ColourRgb(0x9C, 0x7A, 0x52), LineLoad = 0, PointLoad = 0,
            TypeMark = "F1", Cost = 70m
        },
        new RailingType("Fence 1200 - Timber Picket")
        {
            Use = RailingUse.Fence, Height = 1200, PostSize = 90, PostSpacing = 1800, BalusterSize = 70, BalusterSpacing = 140, BottomGap = 50,
            TopRailWidth = 70, TopRailDepth = 20, Colour = new ColourRgb(0xEE, 0xEA, 0xE0), LineLoad = 0, PointLoad = 0, TypeMark = "F2", Cost = 55m
        },
        new RailingType("Fence 1800 - Steel Mesh")
        {
            Use = RailingUse.Fence, Height = 1800, Infill = RailingInfill.Mesh, PostSize = 60, PostSpacing = 2500, BottomGap = 50, PanelThickness = 5,
            Colour = new ColourRgb(0x2F, 0x4F, 0x3A), LineLoad = 0, PointLoad = 0, TypeMark = "F3", Cost = 45m
        }
    };

    /// <summary>The types added to the library since projects were first saved with railings: given to a project that has none of the same name.</summary>
    public static readonly IReadOnlySet<string> Added = new HashSet<string>
    {
        "Guard 1100 - Frameless Glass", "Guard 1100 - Glass with Handrail", "Guard 1100 - Steel Mesh", "Guard 1100 - Stainless Cables",
        "Juliet 1100 - Glass", "Fence 1800 - Close Board Timber", "Fence 1200 - Timber Picket", "Fence 1800 - Steel Mesh"
    };
}

public static class RailingTypeParameters
{
    public static readonly ParameterDefinition Height = new("Railing Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Infill = new("Infill", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition TopRailProfile = new("Top Rail Profile", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition TopRailWidth = new("Top Rail Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition TopRailDepth = new("Top Rail Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition PostSize = new("Post Size", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition PostSpacing = new("Post Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BalusterSize = new("Baluster Size", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BalusterSpacing = new("Baluster Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BottomGap = new("Gap Under", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition GlassThickness = new("Glass Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition InfillMaterial = new("Infill Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Use = new("Use", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition HasTopRail = new("Top Rail", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition GlassLaminated = new("Laminated Glass", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition GlassJoint = new("Gap Between Panes", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition ChannelHeight = new("Base Channel Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition PanelThickness = new("Panel Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition IntermediateRails = new("Intermediate Rails", ParameterDataType.Integer, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Handrail = new("Handrail", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition HandrailHeight = new("Handrail Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailDiameter = new("Handrail Diameter", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailClearance = new("Handrail Clearance", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailExtension = new("Handrail Extension at Ends", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailEnds = new("Handrail Ends", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition LineLoad = new("Line Load (kN/m)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);
    public static readonly ParameterDefinition PointLoad = new("Point Load (kN)", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Analytical);
}

/// <summary>
/// A railing: guarding along a path drawn on a level - the edge of a balcony, a deck, a roof
/// terrace or a floor open to the storey below. It stands on whatever floor or flat roof is
/// under it, following a terrace's falls, with posts at its ends and corners and as often
/// between as its type says.
/// </summary>
public sealed class Railing : Element
{
    private readonly List<Point2D> _path = new();
    private readonly List<double> _bulges = new();

    public override BuiltInCategory Category => BuiltInCategory.Railings;

    /// <summary>The line it runs along, its rail's middle, in plan: two points at least.</summary>
    public IReadOnlyList<Point2D> Path => _path;

    /// <summary>
    /// How far each length of it bows - from each point to the next, and round from its last to
    /// its first where it is closed: zero for a straight length, otherwise the tangent of a
    /// quarter of the angle its arc turns through, positive turning left, as a wall's.
    /// </summary>
    public IReadOnlyList<double> Bulges => _bulges;

    /// <summary>Its path, every length of it straight - or, given as many points as before, keeping how each bows.</summary>
    public void SetPath(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        var kept = replacement.Count == _path.Count ? _bulges.ToList() : new List<double>();
        SetPath(replacement, kept);
    }

    /// <summary>Its path, and how far each length of it bows: none given, or too few, straight.</summary>
    public void SetPath(IEnumerable<Point2D> points, IEnumerable<double> bulges)
    {
        var replacement = points.ToList();
        var bows = bulges.ToList();
        _path.Clear();
        _path.AddRange(replacement);
        _bulges.Clear();
        _bulges.AddRange(Enumerable.Range(0, replacement.Count).Select(i => i < bows.Count ? Bounded(bows[i]) : 0));
    }

    /// <summary>How far the length from a point of it to the next bows.</summary>
    public double BulgeAt(int index) => index >= 0 && index < _bulges.Count ? _bulges[index] : 0;

    /// <summary>Makes the length from a point of it to the next bow so far.</summary>
    public void SetBulge(int index, double bulge)
    {
        while (_bulges.Count < _path.Count) _bulges.Add(0);
        if (index >= 0 && index < _bulges.Count) _bulges[index] = Bounded(bulge);
    }

    /// <summary>A bulge as one can be: none for one that is not a number, and no more than all but a degree of a circle.</summary>
    private static double Bounded(double bulge) => double.IsFinite(bulge) ? Math.Clamp(bulge, -WallCurve.MostBulge, WallCurve.MostBulge) : 0;

    /// <summary>
    /// The door or window it guards, as a Juliet balcony across its opening, if it is one: it
    /// stands from the floor inside, and is measured from it.
    /// </summary>
    public Guid? HostOpeningId { get; set; }

    /// <summary>Whether it runs on from its last point back to its first, all the way round.</summary>
    public bool Closed { get; set; }

    /// <summary>How far above what it stands on its foot is, mm.</summary>
    public double BaseOffset { get; set; }

    /// <summary>The building it guards in: a dwelling, or another, which asks 1100 of it.</summary>
    public RailingBuilding Building { get; set; } = RailingBuilding.Dwelling;

    /// <summary>
    /// Whether it is outside - round a balcony, a deck or a roof terrace - where a house's
    /// guarding is 1100 high, not the 900 of a floor's edge inside. Set when it is put up.
    /// </summary>
    public bool Outside { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(RailingParameters.BaseLevel,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.BindValidated(RailingParameters.BaseOffset, () => BaseOffset, (double v) =>
        {
            if (Math.Abs(v) > 3000) return false;
            BaseOffset = v;
            return true;
        });
        yield return ParameterValue.ReadOnly(RailingParameters.Length, () => Railings.Length(this));
        yield return ParameterValue.ReadOnly(RailingParameters.Height, () => Railings.TypeOf(document, this).Height);
        yield return ParameterValue.ReadOnly(RailingParameters.GuardingHeight, () => Math.Round(Railings.EffectiveHeight(document, this)));
        if (HostOpeningId is { } host)
            yield return ParameterValue.ReadOnly(RailingParameters.Guards, () => document.FindElement(host) switch
            {
                Door => "A door, as a Juliet balcony",
                Window => "A window, as a Juliet balcony",
                _ => "<none>"
            });
        yield return ParameterValue.BindChoice(RailingParameters.Building,
            () => EnumText.Humanise(Building),
            v => { if (EnumText.TryParse<RailingBuilding>(v, out var building)) Building = building; },
            EnumText.Choices<RailingBuilding>());
        yield return ParameterValue.Bind(RailingParameters.Outside, () => Outside, v => Outside = v);
        yield return ParameterValue.ReadOnly(RailingParameters.Rule, () => Railings.Verdict(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class RailingParameters
{
    public static readonly ParameterDefinition BaseLevel = new("Base Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BaseOffset = new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Outside = new("Outside", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Building = new("Building", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition GuardingHeight = new("Height Above the Floor Guarded", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Guards = new("Guards", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Rule = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>
/// What a railing is: where its posts and bays are, how high it stands at each, whether it is
/// as high and as closed as the rules ask where it is - and which edges of a floor, balcony or
/// terrace want one, for the Guard Edges command to put up.
/// </summary>
public static class Railings
{
    /// <summary>How far in from a slab's edge guarding put up along it runs - its rail's middle, mm.</summary>
    public const double EdgeInset = 50;

    private static readonly RailingType Fallback = RailingType.Library()[0];

    public static RailingType TypeOf(BimDocument document, Railing railing) => document.FindType<RailingType>(railing.TypeId) ?? Fallback;

    /// <summary>The most an arc of a railing turns through between two posts, and the longest straight piece of one, as it is built: in facets, each a bay.</summary>
    private const double FacetTurn = Math.PI / 12, FacetLength = 1200;

    /// <summary>Each length of its path, from a point to the next - round to its first again, closed - as the line or arc it is.</summary>
    public static IReadOnlyList<WallCurve> Curves(Railing railing)
    {
        var path = railing.Path;
        var curves = new List<WallCurve>();
        for (var i = 0; i + 1 < path.Count; i++) curves.Add(WallCurve.Of(path[i], path[i + 1], railing.BulgeAt(i)));
        if (railing.Closed && path.Count >= 3) curves.Add(WallCurve.Of(path[^1], path[0], railing.BulgeAt(path.Count - 1)));
        return curves.Where(curve => curve.Length >= 1).ToList();
    }

    /// <summary>
    /// Its lengths, end to end - round to its first point again, closed - each straight: an arc
    /// in the straight facets it is built in, a post at each, as a curved balustrade of glass
    /// panes or straight rails is.
    /// </summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Lengths(Railing railing)
    {
        var lengths = new List<(Point2D, Point2D)>();
        foreach (var curve in Curves(railing))
        {
            if (!curve.IsArc)
            {
                lengths.Add((curve.Start, curve.End));
                continue;
            }

            var pieces = (int)Math.Clamp(Math.Max(Math.Ceiling(Math.Abs(curve.Sweep) / FacetTurn - 1e-9), Math.Ceiling(curve.Length / FacetLength - 1e-9)), 2, 1000);
            var points = Enumerable.Range(0, pieces + 1).Select(k => curve.PointAt(curve.Length * k / pieces)).ToList();
            lengths.AddRange(points.Zip(points.Skip(1)));
        }

        return lengths.Where(length => (length.Item2 - length.Item1).Length >= 1).ToList();
    }

    public static double Length(Railing railing) => Lengths(railing).Sum(length => (length.To - length.From).Length);

    /// <summary>
    /// Its bays, post to post: each straight length shared equally among as few as keep within
    /// its type's post spacing - a post at each end, at each corner and between.
    /// </summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Bays(Railing railing, RailingType type)
    {
        var bays = new List<(Point2D, Point2D)>();
        foreach (var (from, to) in Lengths(railing))
        {
            var count = Math.Max(1, (int)Math.Ceiling((to - from).Length / Math.Max(300, type.PostSpacing) - 1e-9));
            for (var k = 0; k < count; k++) bays.Add((from + (to - from) * ((double)k / count), from + (to - from) * ((double)(k + 1) / count)));
        }

        return bays;
    }

    /// <summary>Where its posts stand: at the ends of its bays, once each.</summary>
    public static IReadOnlyList<Point2D> Posts(Railing railing, RailingType type) => PostsOf(Bays(railing, type));

    private static IReadOnlyList<Point2D> PostsOf(IEnumerable<(Point2D From, Point2D To)> bays)
    {
        var posts = new List<Point2D>();
        foreach (var (from, to) in bays)
        {
            if (!posts.Any(post => post.DistanceTo(from) < 1)) posts.Add(from);
            if (!posts.Any(post => post.DistanceTo(to) < 1)) posts.Add(to);
        }

        return posts;
    }

    /// <summary>
    /// Where a stair comes up through the line a railing runs along: across each place one comes
    /// up at its height, as wide as the stair, from over its top steps out onto the floor - left
    /// open, so the railing stops either side of it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> StairGaps(BimDocument document, Railing railing)
    {
        var gaps = new List<IReadOnlyList<Point2D>>();
        var lengths = Lengths(railing);
        if (lengths.Count == 0) return gaps;
        foreach (var stair in document.Elements.OfType<Stair>())
        foreach (var flight in Stairs.Flights(document, stair))
        foreach (var (from, to, off) in SteppedOffAt(flight.Layout))
        {
            var middle = from.MidpointTo(to);
            if (!lengths.Any(length => Line2D.DistanceFromSegment(middle, length.From, length.To) < 600)) continue;

            // As high as the floor it is stepped off onto - just past its edge, not on it, where a floor's own edge may be taken as outside it.
            if (Math.Abs(Base(document, railing, middle + off * 150) - railing.BaseOffset - flight.Layout.Top) > 300) continue;
            var across = (to - from).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * 600;
            gaps.Add(new[] { from - across, to - across, to + across, from + across });
        }

        return gaps;
    }

    /// <summary>
    /// Where a stair is stepped off at its head: the edges it comes up at - or, for a spiral, as
    /// wide as the stair, where the middle of its landing's outside edge is, square to the way off.
    /// </summary>
    private static IReadOnlyList<(Point2D From, Point2D To, Vector2D Off)> SteppedOffAt(StairLayout layout)
    {
        if (layout.Shape != StairShape.Spiral || Stairs.HeadOf(layout) is not { } head)
            return layout.Arrivals.Select(arrival => (arrival.From, arrival.To, Stairs.Off(layout, arrival))).ToList();
        var across = head.Up.PerpendicularLeft() * (layout.Width / 2);
        return new[] { (head.At - across, head.At + across, head.Up) };
    }

    /// <summary>The lengths it is built along: its own, less where a stair comes up through it.</summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Segments(BimDocument document, Railing railing)
    {
        IReadOnlyList<(Point2D From, Point2D To)> pieces = Lengths(railing);
        foreach (var gap in StairGaps(document, railing))
            pieces = pieces.SelectMany(piece => Polygon2D.Clip(piece.From, piece.To, gap, inside: false)).ToList();
        return pieces.Where(piece => piece.From.DistanceTo(piece.To) >= 1).ToList();
    }

    /// <summary>Its bays as it is built: each length it is built along shared among as few as keep within its post spacing.</summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Bays(BimDocument document, Railing railing, RailingType type) =>
        Segments(document, railing).SelectMany(length => Divided(length.From, length.To, type)).ToList();

    /// <summary>Where its posts stand as it is built: none for frameless glass, which stands in its channel.</summary>
    public static IReadOnlyList<Point2D> Posts(BimDocument document, Railing railing, RailingType type) =>
        type.Infill == RailingInfill.FramelessGlass ? Array.Empty<Point2D>() : PostsOf(Bays(document, railing, type));

    private static IEnumerable<(Point2D From, Point2D To)> Divided(Point2D from, Point2D to, RailingType type)
    {
        var count = Math.Max(1, (int)Math.Ceiling((to - from).Length / Math.Max(300, type.PostSpacing) - 1e-9));
        for (var k = 0; k < count; k++) yield return (from + (to - from) * ((double)k / count), from + (to - from) * ((double)(k + 1) / count));
    }

    /// <summary>
    /// What it stands on at a point: the top of the floor or flat roof there near its level - a
    /// balcony set down, a terrace with its falls - the top of a low wall it runs along, as a
    /// retaining wall's, the ground there, or its level; and its offset.
    /// </summary>
    public static double Base(BimDocument document, Railing railing, Point2D at)
    {
        // A Juliet balcony: from the floor inside the opening it guards.
        if (Host(document, railing) is { } host) return host.Floor + railing.BaseOffset;

        var level = document.FindLevel(railing.LevelId)?.Elevation ?? 0;
        var floor = FloorSurfaces.StoodOnNear(document, level, at, Stairs.FloorReach)?.Top;

        // On a ramp on its storey: on its surface, rising with it.
        if (Ramps.SurfaceOn(document, railing.LevelId, at) is { } ramp && (floor is null || ramp > floor.Value)) floor = ramp;

        // On a parapet, a dwarf wall or a retaining wall along its line: on its top, over the floor under it.
        if (WallTopNear(document, level, at) is { } wall && (floor is null || wall > floor.Value + 1)) return wall + railing.BaseOffset;
        return (floor ?? Terrains.GroundNear(document, level, at) ?? level) + railing.BaseOffset;
    }

    /// <summary>The opening a Juliet balcony guards, its wall, and the floor inside it - the wall's foot.</summary>
    public static (Opening Opening, Wall Wall, double Floor)? Host(BimDocument document, Railing railing) =>
        railing.HostOpeningId is { } id && document.FindElement(id) is Opening opening && document.Walls.FirstOrDefault(wall => wall.Id == opening.HostWallId) is { } wall
            ? (opening, wall, wall.GetBaseElevation(document))
            : null;

    /// <summary>
    /// The floor a railing guards at a point, that its height is measured from: the floor, deck,
    /// terrace or ground it stands on - and, standing on a wall, the higher of what is either
    /// side of it, as a parapet's height counts with the railing on it, but a retaining wall's
    /// does not, the ground it holds back being level with its top. Approved Document K
    /// measures guarding from the floor it guards.
    /// </summary>
    public static double GuardedFrom(BimDocument document, Railing railing, Point2D at)
    {
        if (Host(document, railing) is { } host) return host.Floor;

        var level = document.FindLevel(railing.LevelId)?.Elevation ?? 0;
        var stood = FloorSurfaces.StoodOnNear(document, level, at, Stairs.FloorReach)?.Top;
        if (Ramps.SurfaceOn(document, railing.LevelId, at) is { } ramp && (stood is null || ramp > stood.Value)) return ramp;
        if (stood is { } floor) return floor;
        if (WallTopNear(document, level, at) is not { } top) return Terrains.GroundNear(document, level, at) ?? level;

        // Either side of the wall it is on: the higher, floor or ground.
        var lengths = Lengths(railing);
        if (lengths.Count == 0) return top;
        var nearest = lengths.MinBy(length => Line2D.DistanceFromSegment(at, length.From, length.To));
        var across = (nearest.To - nearest.From).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var sides = new[] { at + across * 400, at - across * 400 }
            .Select(side => FloorSurfaces.StoodOnNear(document, level, side, Stairs.FloorReach)?.Top ?? Terrains.GroundAt(document, side))
            .OfType<double>()
            .ToList();
        return sides.Count == 0 ? top : Math.Min(top, sides.Max());
    }

    /// <summary>
    /// How high it stands over the floor it guards, at its lowest: from each post - or each
    /// pane's end, frameless - its top over the floor, deck, terrace or ground there, a parapet
    /// under it counted in.
    /// </summary>
    public static double EffectiveHeight(BimDocument document, Railing railing)
    {
        var type = TypeOf(document, railing);
        var points = Bays(document, railing, type).SelectMany(bay => new[] { bay.From, bay.To }).ToList();
        if (points.Count == 0) points = railing.Path.ToList();
        return points.Count == 0 ? type.Height : points.Min(point => Base(document, railing, point) + type.Height - GuardedFrom(document, railing, point));
    }

    /// <summary>The top of a wall a point is on, where it is low enough to stand a railing on - within reach of a level: a retaining wall, a parapet, a dwarf wall.</summary>
    private static double? WallTopNear(BimDocument document, double level, Point2D at)
    {
        double? highest = null;
        foreach (var wall in document.Walls)
        {
            // Walls nowhere near the point, or too high to stand on, passed over before their outline is worked out.
            if (wall.Length <= 1 || document.GetWallType(wall) is not { } type || type.Structure.TotalWidth <= 0) continue;
            var reach = type.Structure.TotalWidth + (wall.IsCurved ? wall.Length / 2 : 0);
            if (at.X < Math.Min(wall.Start.X, wall.End.X) - reach || at.X > Math.Max(wall.Start.X, wall.End.X) + reach
                || at.Y < Math.Min(wall.Start.Y, wall.End.Y) - reach || at.Y > Math.Max(wall.Start.Y, wall.End.Y) + reach) continue;
            var top = wall.GetTopElevation(document);
            if (top <= level || top - level > Terrains.Reach || top <= (highest ?? double.NegativeInfinity)) continue;

            var half = type.Structure.TotalWidth / 2;
            var band = CurtainGeometry.Band(wall, type, 0, wall.Length, half, -half);
            if (band.Count >= 3 && Polygon2D.Contains(band, at)) highest = top;
        }

        return highest;
    }

    /// <summary>
    /// The height its rules ask of it where it is, and why. Approved Document K (Diagram 3.1):
    /// in a dwelling, 900 at a floor's edge inside and 1100 round a balcony or a roof; in any
    /// other building, 1100. The IRC: 36 in (914) round a home; the IBC: 42 in (1067) elsewhere.
    /// </summary>
    public static (double Height, string Rule) Required(BimDocument document, Railing railing)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();

        // Across a window: Approved Document K's 800 from the floor; under the IRC, the 24 in a window's own sill would give.
        if (Host(document, railing) is { Opening: Window })
            return us ? (610, "the IRC asks for 610 (24 in) across a window from the floor") : (800, "Approved Document K asks for 800 across a window from the floor");

        if (us)
            return railing.Building == RailingBuilding.Dwelling
                ? (914, "the IRC asks for 914 (36 in) round a home")
                : (1067, "the IBC asks for 1067 (42 in) in a building other than a home");

        if (railing.Building == RailingBuilding.Other) return (1100, "Approved Document K asks for 1100 in a building other than a dwelling");
        return railing.Outside
            ? (1100, "Approved Document K asks for 1100 round a balcony or a roof")
            : (900, "Approved Document K asks for 900 at the edge of a floor inside a home");
    }

    /// <summary>
    /// The loads its rules ask it to take along its top and at any point on it, and why. The UK
    /// National Annex to BS EN 1991-1-1 (Table NA.8), which Approved Document K points to: 0.36
    /// kN/m and 0.25 kN inside a home, 0.74 kN/m and 0.5 kN round its balconies and roofs, and
    /// 1.5 kN/m and 1.5 kN where people gather, in other buildings. The IRC asks a home's guard
    /// for 200 lb at a point; the IBC 50 lb/ft along it as well. A fence is asked for none.
    /// </summary>
    public static (double Line, double Point, string Rule) RequiredLoad(BimDocument document, Railing railing)
    {
        if (TypeOf(document, railing).Use == RailingUse.Fence) return (0, 0, "a fence guards no drop");
        if (document.ProjectInformation.ClearanceStandard.IsUs())
            return railing.Building == RailingBuilding.Dwelling
                ? (0, 0.89, "the IRC asks a guard round a home to take 200 lb (0.89 kN) at any point on its top")
                : (0.73, 0.89, "the IBC asks a guard to take 50 lb/ft (0.73 kN/m) along its top and 200 lb (0.89 kN) at any point on it");

        const string annex = "BS EN 1991-1-1's UK National Annex asks";
        if (railing.Building == RailingBuilding.Other) return (1.5, 1.5, $"{annex} 1.5 kN/m along its top and 1.5 kN at a point where people gather");
        return railing.Outside || Host(document, railing) is not null
            ? (0.74, 0.5, $"{annex} 0.74 kN/m along its top and 0.5 kN at a point round a home's balcony or roof")
            : (0.36, 0.25, $"{annex} 0.36 kN/m along its top and 0.25 kN at a point inside a home");
    }

    /// <summary>What about a railing falls short of its rules - each said, with what is asked - or nothing. A fence is held to none of guarding's.</summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Railing railing)
    {
        var problems = new List<string>();
        var type = TypeOf(document, railing);
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        if (Length(railing) < 100)
        {
            problems.Add("It has no length: draw it along two points at least.");
            return problems;
        }

        if (type.Use == RailingUse.Fence) return problems;

        // As high as asked, over the floor it guards - a parapet it stands on counted in.
        var (height, rule) = Required(document, railing);
        var stands = EffectiveHeight(document, railing);
        if (stands < height - 0.5)
            problems.Add(Math.Abs(stands - type.Height) < 1
                ? $"It is {type.Height:0} high: {rule}."
                : $"It stands {stands:0} over the floor it guards, at its lowest: {rule}.");

        // Nothing a child's head goes through: 100 mm, the IRC's 4 in (102) - between balusters, rails, panes, under it and at its ends.
        var (sphere, said) = us ? (102.0, "the IRC asks for less than 102 (4 in)") : (100.0, "Approved Document K asks for less than 100");
        if (type.Infill == RailingInfill.Balusters && Clear(type) >= sphere)
            problems.Add($"Its balusters are {Clear(type):0} apart: {said}, so a child's head cannot pass.");
        if (type.Infill is RailingInfill.HorizontalBars or RailingInfill.Cables && BarGap(type) >= sphere)
            problems.Add($"Its {(type.Infill == RailingInfill.Cables ? "cables" : "rails")} are {BarGap(type):0} apart: {said}, so a child's head cannot pass.");
        if (type.Infill == RailingInfill.FramelessGlass && type.GlassJoint >= sphere)
            problems.Add($"Its panes are {type.GlassJoint:0} apart: {said}, so a child's head cannot pass.");
        if (type.Infill != RailingInfill.FramelessGlass && type.BottomGap >= sphere) problems.Add($"It stands {type.BottomGap:0} clear of the floor: {said} under it.");
        foreach (var (_, gap, what) in EndGaps(document, railing))
            problems.Add($"Its end stands {gap:0} clear of {what}: {said} there too.");

        // Rails along are a ladder: not for a home, where children are.
        if (!us && railing.Building == RailingBuilding.Dwelling)
        {
            if (type.Infill is RailingInfill.HorizontalBars or RailingInfill.Cables)
                problems.Add($"Its {(type.Infill == RailingInfill.Cables ? "cables" : "rails")} run along, which a child can climb: Approved Document K asks for guarding in a home not to be easily climbable.");
            else if (type.IntermediateRails > 0 && type.Infill != RailingInfill.FramelessGlass)
                problems.Add("Its intermediate rails are footholds a child can climb by: Approved Document K asks for guarding in a home not to be easily climbable.");
        }

        // Glass: laminated in a guard (IBC 2407.1, IRC R308.4.4.1); frameless, without a rail on top, only where it stays in place broken (BS 6180, 8.5.2).
        if (type.Infill is RailingInfill.Glass or RailingInfill.FramelessGlass && !type.GlassLaminated)
        {
            if (us) problems.Add("Its glass is toughened, not laminated: the IBC asks for laminated glass in a guard.");
            else if (type.Infill == RailingInfill.FramelessGlass && !type.HasTopRail)
                problems.Add("Its glass stands frameless with no rail along its top, and is toughened, not laminated: BS 6180 asks for a rail linking the panes unless the glass is laminated, so the guarding stays when a pane breaks.");
        }

        // Made for the loads its place asks.
        var (line, point, loads) = RequiredLoad(document, railing);
        if (type.LineLoad < line - 0.005 || type.PointLoad < point - 0.005)
            problems.Add($"It is made for {type.LineLoad:0.##} kN/m along its top and {type.PointLoad:0.##} kN at a point: {loads}.");

        return problems;
    }

    /// <summary>Whether it meets its rules, as the property panel says it.</summary>
    public static string Verdict(BimDocument document, Railing railing)
    {
        if (TypeOf(document, railing).Use == RailingUse.Fence && Length(railing) >= 100) return "A fence: it guards no drop";
        var problems = Problems(document, railing);
        var rules = document.ProjectInformation.ClearanceStandard.IsUs() ? "the IRC" : "Approved Document K";
        return problems.Count == 0 ? $"Meets {rules}" : $"{problems.Count} short of {rules}: {string.Join(" ", problems)}";
    }

    /// <summary>
    /// The gaps at its open ends: between each end - its end post, or the edge of its glass - and
    /// the wall or the railing it stops short of, close ahead of it, where a child's head would go
    /// through. Each with how wide it is and what it is against.
    /// </summary>
    public static IReadOnlyList<(Point2D At, double Gap, string Against)> EndGaps(BimDocument document, Railing railing)
    {
        var gaps = new List<(Point2D, double, string)>();
        var lengths = Lengths(railing);
        if (railing.Closed || lengths.Count == 0) return gaps;

        var type = TypeOf(document, railing);
        var sphere = document.ProjectInformation.ClearanceStandard.IsUs() ? 102.0 : 100.0;
        var end = type.Infill == RailingInfill.FramelessGlass ? type.GlassJoint / 2 : type.PostSize / 2;
        var walls = StairModel.WallBands(document);

        // The other railings close enough to either end to be what it stops at.
        var ends = new[] { lengths[0].From, lengths[^1].To };
        var near = document.Elements.OfType<Railing>()
            .Where(other => !ReferenceEquals(other, railing))
            .Select(other => (Railing: other, Lengths: Lengths(other), Type: TypeOf(document, other)))
            .Where(other => other.Lengths.Any(length => ends.Any(end => Line2D.DistanceFromSegment(end, length.From, length.To) < 400)))
            .ToList();
        foreach (var (at, from) in new[] { (lengths[0].From, lengths[0].To), (lengths[^1].To, lengths[^1].From) })
        {
            var way = (at - from).NormalisedOrDefault(Vector2D.UnitX);
            var bottom = Base(document, railing, at);
            var top = bottom + type.Height;
            string? Against(double ahead)
            {
                // Near it in plan first, its height only then: working out what a railing stands on is the costly part.
                var point = at + way * ahead;
                return walls.Any(wall => wall.Holds(point) && wall.Base < top && wall.Top > bottom + 100) ? "the wall"
                    : near.Any(other => other.Lengths.Any(length => Line2D.DistanceFromSegment(point, length.From, length.To) <= other.Type.PostSize / 2)
                                        && Base(document, other.Railing, point) is var foot && foot < top && foot + other.Type.Height > bottom + 100)
                        ? "the railing there" : null;
            }

            for (var ahead = 0.0; ahead <= 300; ahead += 5)
            {
                if (Against(ahead) is not { } against) continue;

                // The face it meets, found to within a millimetre.
                var (clear, met) = (Math.Max(0, ahead - 5), ahead);
                while (met - clear > 0.5)
                {
                    var middle = (clear + met) / 2;
                    if (Against(middle) is null) clear = middle;
                    else met = middle;
                }

                if (met - end >= sphere) gaps.Add((at, met - end, against));
                break;
            }
        }

        return gaps;
    }

    /// <summary>
    /// A run's open ends brought up to the face of a wall close by ahead of either, so its end post
    /// stands 10 clear of it, as guarding is fixed tight to the wall it meets - not short of it.
    /// </summary>
    public static IReadOnlyList<Point2D> Tight(BimDocument document, IReadOnlyList<Point2D> points, double half, double height)
    {
        if (points.Count < 2) return points;
        var walls = StairModel.WallBands(document).Where(wall => wall.Base <= height + 100 && wall.Top >= height + 900).ToList();
        var tight = points.ToList();
        foreach (var (at, next) in new[] { (0, 1), (tight.Count - 1, tight.Count - 2) })
        {
            var way = (tight[at] - tight[next]).NormalisedOrDefault(Vector2D.UnitX);
            var room = (tight[at] - tight[next]).Length - 2 * half;
            double? best = null;
            for (var by = -300.0; by <= 300; by += 5)
            {
                var point = tight[at] + way * by;
                if (by < -room || walls.Any(wall => wall.Holds(point))) continue;
                if (!walls.Any(wall => wall.Holds(point + way * (half + 10)))) continue;
                if (best is null || Math.Abs(by) < Math.Abs(best.Value)) best = by;
            }

            if (best is { } move) tight[at] += way * move;
        }

        return tight;
    }

    /// <summary>The clear gap between its balusters, mm.</summary>
    public static double Clear(RailingType type) => type.BalusterSpacing - type.BalusterSize;

    /// <summary>How many rails or cables along it has between its bottom rail and its top: as few as leave them no further apart than its baluster spacing says.</summary>
    public static int Bars(RailingType type)
    {
        var (bottom, top) = (type.BottomGap + BottomRail, type.Height - RailDepth(type));
        return Math.Max(0, (int)Math.Ceiling((top - bottom) / Math.Max(40, type.BalusterSpacing) - 1e-9) - 1);
    }

    /// <summary>The clear gap between its rails or cables along, mm.</summary>
    public static double BarGap(RailingType type)
    {
        var (bottom, top) = (type.BottomGap + BottomRail, type.Height - RailDepth(type));
        return (top - bottom - Bars(type) * BarSizeOf(type)) / (Bars(type) + 1);
    }

    /// <summary>Its bottom rail's depth, and a rail along's, mm.</summary>
    public const double BottomRail = 40, BarSize = 20;

    /// <summary>A cable's diameter, mm.</summary>
    public const double CableSize = 6;

    /// <summary>How deep one of its rails or cables along is.</summary>
    public static double BarSizeOf(RailingType type) => type.Infill == RailingInfill.Cables ? CableSize : BarSize;

    /// <summary>How deep its top rail is: a round one as deep as it is wide; none where it has none.</summary>
    public static double RailDepth(RailingType type) =>
        !type.HasTopRail && type.Infill == RailingInfill.FramelessGlass ? 0 : type.TopRailProfile == HandrailProfile.Round ? type.TopRailWidth : type.TopRailDepth;

    /// <summary>
    /// A Juliet balcony across a door or window in an outside wall: a railing across its opening
    /// in front of the wall, from jamb to jamb, standing from its sill and measured from the
    /// floor inside - in the type given, or the first Juliet type, or one as high as asked. Not
    /// added: the caller adds it. Null for an opening in no wall.
    /// </summary>
    public static Railing? Juliet(BimDocument document, Opening opening, Guid? typeId = null)
    {
        if (document.Walls.FirstOrDefault(wall => wall.Id == opening.HostWallId) is not { } wall || document.GetWallType(wall) is not { } body) return null;
        var size = document.FindType<OpeningType>(opening.TypeId);
        var (from, to) = (opening.DistanceAlongWall - opening.WidthOf(size) / 2, opening.DistanceAlongWall + opening.WidthOf(size) / 2);

        // On the side it drops: outside, whichever way round the wall was drawn.
        var across = OpeningGuarding.DropSide(document, wall, opening.DistanceAlongWall).Side * (body.Structure.TotalWidth / 2 + 60);

        var railing = new Railing { LevelId = wall.LevelId, HostOpeningId = opening.Id, Outside = true, BaseOffset = Math.Max(0, opening.SillHeight) };
        railing.SetPath(new[] { wall.PointAt(body.Structure, from, across), wall.PointAt(body.Structure, to, across) });
        var type = typeId is { } given && document.FindType<RailingType>(given) is { } chosen ? chosen
            : document.TypesOf<RailingType>().FirstOrDefault(candidate => candidate.Name.StartsWith("Juliet", StringComparison.OrdinalIgnoreCase))
              ?? TypeFor(document, Required(document, railing).Height);
        railing.TypeId = type?.Id ?? Guid.Empty;
        return railing;
    }

    /// <summary>
    /// Whether a railing is outside: on a roof, or not within the walls of its level - round a
    /// balcony, a deck or a terrace.
    /// </summary>
    public static bool StandsOutside(BimDocument document, Railing railing)
    {
        var lengths = Lengths(railing);
        if (lengths.Count == 0) return false;
        var (from, to) = lengths.OrderByDescending(length => (length.To - length.From).Length).First();
        var middle = from.MidpointTo(to);
        var level = document.FindLevel(railing.LevelId)?.Elevation ?? 0;
        if (FloorSurfaces.StoodOnNear(document, level, middle, Stairs.FloorReach) is { Slab: Roof }) return true;
        return !Within(document, railing.LevelId, middle);
    }

    /// <summary>
    /// Whether a point is within the building on a level: inside the walls standing on it - or,
    /// where none are drawn on it yet, those of the storey below, the building's footprint.
    /// </summary>
    private static bool Within(BimDocument document, Guid levelId, Point2D at)
    {
        if (!document.Walls.Any(wall => wall.LevelId == levelId) && Datums.Levels.Below(document, levelId) is { } below) levelId = below.Id;
        return RoomBoundary.Trace(document, levelId, at).IsEnclosed;
    }

    /// <summary>
    /// The drop over which a floor's edge wants guarding, mm: more than 600 in a dwelling,
    /// Approved Document K says, and in other buildings 380; more than 30 in (762) under the IRC.
    /// </summary>
    public static double GuardedDrop(BimDocument document, RailingBuilding building) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? 762 : building == RailingBuilding.Dwelling ? 600 : 380;

    /// <summary>A length of a slab's edge that drops too far, with no wall, floor, stair or railing there: how far it drops at most.</summary>
    public sealed record OpenEdge(IReadOnlyList<Point2D> Points, bool Closed, double Drop);

    /// <summary>
    /// The edges of a floor, balcony or flat roof stood on that drop too far to be left open -
    /// beyond them nothing, or a floor or the ground well below - and are not against a wall
    /// standing on them, nor at a floor carrying on beside them, nor where a stair comes up onto
    /// them, nor guarded already. With what was left open, and why.
    /// </summary>
    public static (IReadOnlyList<OpenEdge> Open, double Walled, double Joined, double AtStairs, double Low, double Guarded) Edges(BimDocument document, Slab slab, RailingBuilding building)
    {
        var open = new List<OpenEdge>();
        if (slab.Boundary.Count < 3 || !FloorSurfaces.IsStoodOn(document, slab)) return (open, 0, 0, 0, 0, 0);

        var walls = StairModel.WallBands(document);
        var limit = GuardedDrop(document, building);
        var lowest = document.Levels.Count == 0 ? 0 : document.Levels.Min(level => level.Elevation);
        var arrivals = document.Elements.OfType<Stair>()
            .SelectMany(stair => Stairs.Flights(document, stair))
            .SelectMany(flight => flight.Layout.Arrivals.Select(arrival => (arrival.From, arrival.To, flight.Layout.Top)))
            .Concat(document.Elements.OfType<Ramp>().Select(ramp => Ramps.Layout(document, ramp)).Select(layout => (layout.HeadEdge.From, layout.HeadEdge.To, layout.Top)))
            .ToList();
        var railings = document.Elements.OfType<Railing>().ToList();
        var others = document.Elements.OfType<Slab>().Where(other => !ReferenceEquals(other, slab) && other.Boundary.Count >= 3 && FloorSurfaces.IsStoodOn(document, other)).ToList();

        // How far out the drop is looked for: just past the edge - under the IRC, anywhere within 36 in of it (R312.1.1).
        var reaches = document.ProjectInformation.ClearanceStandard.IsUs() ? new[] { 60.0, 305, 610, 914 } : new[] { 60.0 };

        // Its edge in short pieces, anticlockwise, so the slab is on the left of each going round.
        var boundary = slab.Boundary;
        var pieces = new List<(Point2D From, Point2D To, double Drop, bool Wanted)>();
        double walled = 0, joined = 0, atStairs = 0, low = 0, guarded = 0;
        for (var i = 0; i < boundary.Count; i++)
        {
            var (a, b) = (boundary[i], boundary[(i + 1) % boundary.Count]);
            var count = Math.Max(1, (int)Math.Ceiling((b - a).Length / 100));
            var outward = (b - a).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * -1;
            for (var k = 0; k < count; k++)
            {
                var (from, to) = (a + (b - a) * ((double)k / count), a + (b - a) * ((double)(k + 1) / count));
                var middle = from.MidpointTo(to);
                var length = (to - from).Length;
                var top = FloorSurfaces.TopAt(document, slab, middle - outward * 20);

                // Beyond it: the highest floor or ground below, or a floor carrying on near its height - the lowest of them within reach.
                var beyond = middle + outward * 60;
                double Below(Point2D at) => others.Where(other => other.Contains(at)).Select(other => FloorSurfaces.TopAt(document, other, at))
                    .Where(height => height <= top + Stairs.FloorReach).DefaultIfEmpty(Terrains.GroundAt(document, at) ?? lowest).Max();
                var below = Below(beyond);
                if (below < top - 1)
                    foreach (var reach in reaches.Skip(1)) below = Math.Min(below, Below(middle + outward * reach));
                var drop = top - below;

                bool Wall() => walls.Any(wall => wall.Base <= top + 100 && wall.Top >= top + 900 && (wall.Holds(middle) || wall.Holds(beyond) || wall.Holds(middle - outward * 30)));
                bool Stair() => arrivals.Any(arrival => Math.Abs(arrival.Top - top) < Stairs.FloorReach && Across(middle, arrival.From, arrival.To));
                bool Railed() => railings.Any(railing => TypeOf(document, railing).Use == RailingUse.Guarding
                    && Lengths(railing).Any(run => Line2D.DistanceFromSegment(middle, run.From, run.To) < 300)
                    && Math.Abs(Base(document, railing, middle) - top) < Stairs.FloorReach);

                var wanted = false;
                if (drop <= 0) joined += length;
                else if (drop <= limit) low += length;
                else if (Wall()) walled += length;
                else if (Stair()) atStairs += length;
                else if (Railed()) guarded += length;
                else wanted = true;
                pieces.Add((from, to, drop, wanted));
            }
        }

        // The pieces wanting guarding joined into runs: all the way round, where nothing is left open.
        if (pieces.All(piece => piece.Wanted))
        {
            open.Add(new OpenEdge(Simplified(boundary), true, pieces.Max(piece => piece.Drop)));
            return (open, walled, joined, atStairs, low, guarded);
        }

        var start = pieces.FindIndex(piece => !piece.Wanted);
        var run = new List<(Point2D From, Point2D To, double Drop, bool Wanted)>();
        for (var k = 1; k <= pieces.Count; k++)
        {
            var piece = pieces[(start + k) % pieces.Count];
            if (piece.Wanted)
            {
                run.Add(piece);
                continue;
            }

            if (run.Count > 0) open.Add(new OpenEdge(Simplified(run.Select(p => p.From).Append(run[^1].To).ToList()), false, run.Max(p => p.Drop)));
            run.Clear();
        }

        return (open, walled, joined, atStairs, low, guarded);
    }

    /// <summary>Whether a point of a slab's edge is where a stair comes up: on the line it comes up at, between its sides.</summary>
    private static bool Across(Point2D at, Point2D from, Point2D to)
    {
        var way = to - from;
        var length = way.Length;
        if (length < 1) return false;
        var along = (at - from).Dot(way) / length;
        return along >= -1 && along <= length + 1 && Math.Abs((at - from).Cross(way) / length) < 120;
    }

    /// <summary>A line through points with the ones it runs straight on through taken out.</summary>
    private static IReadOnlyList<Point2D> Simplified(IReadOnlyList<Point2D> points)
    {
        var kept = new List<Point2D>();
        foreach (var point in points)
        {
            if (kept.Count > 0 && kept[^1].DistanceTo(point) < 1) continue;
            if (kept.Count >= 2 && Math.Abs((kept[^1] - kept[^2]).NormalisedOrDefault(Vector2D.UnitX).Cross((point - kept[^1]).NormalisedOrDefault(Vector2D.UnitX))) < 1e-6
                && (kept[^1] - kept[^2]).Dot(point - kept[^1]) > 0)
                kept[^1] = point;
            else kept.Add(point);
        }

        return kept;
    }

    /// <summary>
    /// A line along a slab's edge set in from it so far - toward the slab, on the left going
    /// along - each corner mitred; all the way round, where it is closed.
    /// </summary>
    public static IReadOnlyList<Point2D> SetIn(IReadOnlyList<Point2D> points, bool closed, double by)
    {
        var count = points.Count;
        var set = new List<Point2D>();
        Vector2D Inward(int i) => (points[(i + 1) % count] - points[i]).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        for (var i = 0; i < count; i++)
        {
            var hasBefore = closed || i > 0;
            var hasAfter = closed || i < count - 1;
            var before = hasBefore ? Inward((i - 1 + count) % count) : Inward(i);
            var after = hasAfter ? Inward(i) : before;
            var way = (before + after).NormalisedOrDefault(after);
            var cos = Math.Max(0.3, way.Dot(after));
            set.Add(points[i] + way * (by / cos));
        }

        return set;
    }

    /// <summary>
    /// A run along an edge brought in at each end so far - its end posts standing on the slab,
    /// clear of the wall, the floor or the stair it stops at. A closed run has no ends.
    /// </summary>
    private static IReadOnlyList<Point2D> Shortened(IReadOnlyList<Point2D> points, bool closed, double by)
    {
        if (closed || points.Count < 2) return points;
        var shortened = points.ToList();
        if ((shortened[1] - shortened[0]).Length > 2 * by) shortened[0] += (shortened[1] - shortened[0]).NormalisedOrDefault(Vector2D.UnitX) * by;
        if ((shortened[^2] - shortened[^1]).Length > 2 * by) shortened[^1] += (shortened[^2] - shortened[^1]).NormalisedOrDefault(Vector2D.UnitX) * by;
        return shortened;
    }

    /// <summary>
    /// The railing type a slab's edge wants: the lowest at least as high as asked, with balusters
    /// or glass - not rails a child can climb - or the highest there is.
    /// </summary>
    public static RailingType? TypeFor(BimDocument document, double height) =>
        document.TypesOf<RailingType>()
            .Where(type => type.Use == RailingUse.Guarding && type.Height >= height - 0.5 && type.Infill is not (RailingInfill.HorizontalBars or RailingInfill.Cables)
                           && type.IntermediateRails == 0 && !type.Name.StartsWith("Juliet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(type => type.Height).FirstOrDefault()
        ?? document.TypesOf<RailingType>().Where(type => type.Use == RailingUse.Guarding).OrderByDescending(type => type.Height).FirstOrDefault();

    /// <summary>
    /// Railings along the open edges of a floor, balcony or flat roof, set in from its edge, each
    /// as high as its rules ask there - in a type of its own choosing, or that given - and what
    /// was done, said. Not added: the caller adds them as one step to undo.
    /// </summary>
    public static IReadOnlyList<Railing> GuardEdges(BimDocument document, Slab slab, RailingBuilding building, Guid? typeId, out string message)
    {
        var (open, walled, joined, atStairs, low, guarded) = Edges(document, slab, building);
        var made = new List<Railing>();
        foreach (var edge in open)
        {
            var railing = new Railing { LevelId = slab.LevelId, Building = building, Closed = edge.Closed };
            railing.SetPath(Shortened(SetIn(edge.Points, edge.Closed, EdgeInset), edge.Closed, EdgeInset));
            railing.LevelId = LevelOf(document, slab, railing);
            railing.Outside = StandsOutside(document, railing);
            var asked = Required(document, railing).Height;
            railing.TypeId = (typeId is { } chosen && document.FindType<RailingType>(chosen) is { } given ? given : TypeFor(document, asked))?.Id ?? Guid.Empty;

            // Its ends tight to the walls they stop at: no gap there a child's head goes through.
            if (!edge.Closed)
                railing.SetPath(Tight(document, railing.Path, TypeOf(document, railing).PostSize / 2, slab.GetTopElevation(document)));
            made.Add(railing);
        }

        string Metres(double mm) => $"{mm / 1000:0.0} m";
        var left = new List<string>();
        if (walled > 0) left.Add($"{Metres(walled)} against walls");
        if (joined > 0) left.Add($"{Metres(joined)} where a floor carries on");
        if (atStairs > 0) left.Add($"{Metres(atStairs)} where a stair or a ramp comes up");
        if (guarded > 0) left.Add($"{Metres(guarded)} guarded already");
        if (low > 0) left.Add($"{Metres(low)} dropping {GuardedDrop(document, building):0} or less");
        var lengths = made.Sum(Length);
        message = made.Count == 0
            ? $"Nothing to guard: no edge of it drops more than {GuardedDrop(document, building):0} with nothing there{(left.Count > 0 ? $" ({string.Join(", ", left)})" : "")}."
            : $"Guarded {Metres(lengths)} of its edge, {string.Join(", ", made.Select(railing => TypeOf(document, railing).Height).Distinct().Select(height => $"{height:0}"))} high{(left.Count > 0 ? $"; left open {string.Join(", ", left)}" : "")}.";
        return made;
    }

    /// <summary>The level a railing along a slab's edge goes on: the one whose floor the slab is, near its top.</summary>
    private static Guid LevelOf(BimDocument document, Slab slab, Railing railing)
    {
        var top = slab.GetTopElevation(document);
        return document.Levels.OrderBy(level => Math.Abs(level.Elevation - top)).Select(level => (Guid?)level.Id).FirstOrDefault() ?? slab.LevelId;
    }

    /// <summary>
    /// Whether a floor or flat roof is walked on, and so guarded round its edge where it drops: a
    /// floor always; a flat roof where it is reached - a stair comes up onto it, a door opens onto
    /// it - or is a floor itself, with walls standing on it, or has a railing on it already. A
    /// roof reached for its upkeep only is not guarded (Approved Document K, paragraph 3.4).
    /// </summary>
    public static bool WalkedOn(BimDocument document, Slab slab)
    {
        if (slab is not Roof) return true;
        var top = slab.GetTopElevation(document);
        bool Near(double height) => Math.Abs(height - top) < Stairs.FloorReach;

        // A stair coming up onto it.
        foreach (var stair in document.Elements.OfType<Stair>())
        foreach (var flight in Stairs.Flights(document, stair))
            if (Near(flight.Layout.Top) && Stairs.SteppedOff(flight.Layout) is { } off && slab.Contains(off)) return true;

        // A storey's walls standing on it, as on its floor - not a dormer's cheeks, nor a parapet; a door out onto it.
        var dormerWalls = document.Elements.OfType<Roof>().SelectMany(roof => roof.DormerWalls).ToHashSet();
        foreach (var wall in document.Walls)
        {
            if (dormerWalls.Contains(wall.Id) || wall.GetTopElevation(document) - wall.GetBaseElevation(document) < 2000) continue;
            var middle = wall.LocationCurve.PointAt(wall.Length / 2);
            if (Near(wall.GetBaseElevation(document)) && slab.Contains(middle)) return true;
        }

        foreach (var door in document.Elements.OfType<Door>())
        {
            if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } type) continue;
            if (!Near(wall.GetBaseElevation(document) + door.SillHeight)) continue;
            var half = type.Structure.TotalWidth / 2 + 300;
            if (slab.Contains(wall.PointAt(type.Structure, door.DistanceAlongWall, half)) || slab.Contains(wall.PointAt(type.Structure, door.DistanceAlongWall, -half))) return true;
        }

        // A railing on it already.
        return document.Elements.OfType<Railing>().Any(railing => railing.Path.Any(slab.Contains) && Near(Base(document, railing, railing.Path[0]) - railing.BaseOffset));
    }

    /// <summary>
    /// How guarded a floor, balcony or flat roof is round its edge, as its property panel says:
    /// how much of its edge drops too far with nothing there, and what its rules ask.
    /// </summary>
    public static string EdgeVerdict(BimDocument document, Slab slab)
    {
        if (!FloorSurfaces.IsStoodOn(document, slab)) return "Not walked on";
        if (!WalkedOn(document, slab))
            return "A roof reached for its upkeep only: Approved Document K asks for no guarding round it - a door or a stair onto it would make it a terrace, to be guarded 1100 high.";
        var open = Edges(document, slab, RailingBuilding.Dwelling).Open;
        if (open.Count == 0) return "Its edges are guarded, or drop too little to need it";
        var length = open.Sum(edge => Polygon2D.Perimeter(edge.Points) - (edge.Closed ? 0 : edge.Points[^1].DistanceTo(edge.Points[0])));
        var drop = open.Max(edge => edge.Drop);
        var standard = document.ProjectInformation.ClearanceStandard;
        var asked = standard.IsUs() ? "the IRC asks for a guard 36 in (914) high" : slab is Roof || !Within(document, slab.LevelId, Polygon2D.Centroid(slab.Boundary, slab.Boundary[0]))
            ? "Approved Document K asks for guarding 1100 high round a balcony or a roof"
            : "Approved Document K asks for guarding 900 high at a floor's edge inside a home";
        return $"{length / 1000:0.0} m of its edge drops {drop:0} with no guarding: {asked} - Guard Edges puts it up.";
    }
}
