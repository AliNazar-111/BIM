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
    HorizontalBars
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

    public RailingType Duplicate(string name) => WithCommonOf(new RailingType(name)
    {
        Height = Height, Infill = Infill, TopRailProfile = TopRailProfile, TopRailWidth = TopRailWidth, TopRailDepth = TopRailDepth,
        PostSize = PostSize, PostSpacing = PostSpacing, BalusterSize = BalusterSize, BalusterSpacing = BalusterSpacing,
        BottomGap = BottomGap, GlassThickness = GlassThickness, Colour = Colour, MaterialId = MaterialId, InfillMaterialId = InfillMaterialId
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return Size(RailingTypeParameters.Height, () => Height, v => Height = v, 300, 2000);
        yield return ParameterValue.BindChoice(RailingTypeParameters.Infill,
            () => EnumText.Humanise(Infill),
            v => { if (EnumText.TryParse<RailingInfill>(v, out var infill)) Infill = infill; },
            EnumText.Choices<RailingInfill>());
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
    /// for a floor's edge inside a house, with balusters; 1100 of glass; 1100 of rails along; and
    /// the IRC's 36 in and the IBC's 42 in. Their balusters are close enough that a 100 mm
    /// sphere - 4 in under the IRC - does not pass.
    /// </summary>
    public static IReadOnlyList<RailingType> Library() => new[]
    {
        new RailingType("Guard 1100 - Steel Balusters") { TypeMark = "GR1", Cost = 220m },
        new RailingType("Guard 900 - Timber Balusters")
        {
            Height = 900, PostSize = 90, PostSpacing = 1800, TopRailDepth = 50, TopRailWidth = 70, BalusterSize = 32, BalusterSpacing = 120,
            Colour = new ColourRgb(0xC8, 0xA8, 0x7A), TypeMark = "GR2", Cost = 160m
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
            BottomGap = 75, Colour = new ColourRgb(0xA8, 0x86, 0x5C), TypeMark = "GR5", Cost = 150m
        },
        new RailingType("Guard 42 in - Glass Panel")
        {
            Height = 1067, Infill = RailingInfill.Glass, TopRailProfile = HandrailProfile.Round, TopRailWidth = 42, PostSpacing = 1200,
            BottomGap = 60, Colour = new ColourRgb(0xBF, 0xC4, 0xC9), TypeMark = "GR6", Cost = 450m
        }
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

    public override BuiltInCategory Category => BuiltInCategory.Railings;

    /// <summary>The line it runs along, its rail's middle, in plan: two points at least.</summary>
    public IReadOnlyList<Point2D> Path => _path;

    public void SetPath(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        _path.Clear();
        _path.AddRange(replacement);
    }

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

    /// <summary>Its lengths, end to end - round to its first point again, closed.</summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Lengths(Railing railing)
    {
        var path = railing.Path;
        var lengths = new List<(Point2D, Point2D)>();
        for (var i = 0; i + 1 < path.Count; i++) lengths.Add((path[i], path[i + 1]));
        if (railing.Closed && path.Count >= 3) lengths.Add((path[^1], path[0]));
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
    public static IReadOnlyList<Point2D> Posts(Railing railing, RailingType type)
    {
        var posts = new List<Point2D>();
        foreach (var (from, to) in Bays(railing, type))
        {
            if (!posts.Any(post => post.DistanceTo(from) < 1)) posts.Add(from);
            if (!posts.Any(post => post.DistanceTo(to) < 1)) posts.Add(to);
        }

        return posts;
    }

    /// <summary>
    /// What it stands on at a point: the top of the floor or flat roof there near its level - a
    /// balcony set down, a terrace with its falls - or its level; and its offset.
    /// </summary>
    public static double Base(BimDocument document, Railing railing, Point2D at)
    {
        var level = document.FindLevel(railing.LevelId)?.Elevation ?? 0;
        return (FloorSurfaces.StoodOnNear(document, level, at, Stairs.FloorReach)?.Top ?? Terrains.GroundNear(document, level, at) ?? level) + railing.BaseOffset;
    }

    /// <summary>
    /// The height its rules ask of it where it is, and why. Approved Document K (Diagram 3.1):
    /// in a dwelling, 900 at a floor's edge inside and 1100 round a balcony or a roof; in any
    /// other building, 1100. The IRC: 36 in (914) round a home; the IBC: 42 in (1067) elsewhere.
    /// </summary>
    public static (double Height, string Rule) Required(BimDocument document, Railing railing)
    {
        if (document.ProjectInformation.ClearanceStandard == ClearanceStandard.Ada)
            return railing.Building == RailingBuilding.Dwelling
                ? (914, "the IRC asks for 914 (36 in) round a home")
                : (1067, "the IBC asks for 1067 (42 in) in a building other than a home");

        if (railing.Building == RailingBuilding.Other) return (1100, "Approved Document K asks for 1100 in a building other than a dwelling");
        return railing.Outside
            ? (1100, "Approved Document K asks for 1100 round a balcony or a roof")
            : (900, "Approved Document K asks for 900 at the edge of a floor inside a home");
    }

    /// <summary>What about a railing falls short of its rules - each said, with what is asked - or nothing.</summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Railing railing)
    {
        var problems = new List<string>();
        var type = TypeOf(document, railing);
        var us = document.ProjectInformation.ClearanceStandard == ClearanceStandard.Ada;
        if (Length(railing) < 100)
        {
            problems.Add("It has no length: draw it along two points at least.");
            return problems;
        }

        var (height, rule) = Required(document, railing);
        if (type.Height < height - 0.5) problems.Add($"It is {type.Height:0} high: {rule}.");

        // Nothing a child's head goes through: 100 mm, the IRC's 4 in (102) - between balusters, and under it.
        var (sphere, said) = us ? (102.0, "the IRC asks for less than 102 (4 in)") : (100.0, "Approved Document K asks for less than 100");
        if (type.Infill == RailingInfill.Balusters && Clear(type) >= sphere)
            problems.Add($"Its balusters are {Clear(type):0} apart: {said}, so a child's head cannot pass.");
        if (type.Infill == RailingInfill.HorizontalBars && BarGap(type) >= sphere)
            problems.Add($"Its rails are {BarGap(type):0} apart: {said}, so a child's head cannot pass.");
        if (type.BottomGap >= sphere) problems.Add($"It stands {type.BottomGap:0} clear of the floor: {said} under it.");

        // Rails along are a ladder: not for a home, where children are.
        if (!us && type.Infill == RailingInfill.HorizontalBars && railing.Building == RailingBuilding.Dwelling)
            problems.Add("Its rails run along, which a child can climb: Approved Document K asks for guarding in a home not to be easily climbable.");

        return problems;
    }

    /// <summary>Whether it meets its rules, as the property panel says it.</summary>
    public static string Verdict(BimDocument document, Railing railing)
    {
        var problems = Problems(document, railing);
        var rules = document.ProjectInformation.ClearanceStandard == ClearanceStandard.Ada ? "the IRC" : "Approved Document K";
        return problems.Count == 0 ? $"Meets {rules}" : $"{problems.Count} short of {rules}: {string.Join(" ", problems)}";
    }

    /// <summary>The clear gap between its balusters, mm.</summary>
    public static double Clear(RailingType type) => type.BalusterSpacing - type.BalusterSize;

    /// <summary>How many rails along it has between its bottom rail and its top: as few as leave them no further apart than its baluster spacing says.</summary>
    public static int Bars(RailingType type)
    {
        var (bottom, top) = (type.BottomGap + BottomRail, type.Height - RailDepth(type));
        return Math.Max(0, (int)Math.Ceiling((top - bottom) / Math.Max(40, type.BalusterSpacing) - 1e-9) - 1);
    }

    /// <summary>The clear gap between its rails along, mm.</summary>
    public static double BarGap(RailingType type)
    {
        var (bottom, top) = (type.BottomGap + BottomRail, type.Height - RailDepth(type));
        return (top - bottom - Bars(type) * BarSize) / (Bars(type) + 1);
    }

    /// <summary>Its bottom rail's depth, and a rail along's, mm.</summary>
    public const double BottomRail = 40, BarSize = 20;

    /// <summary>How deep its top rail is: a round one as deep as it is wide.</summary>
    public static double RailDepth(RailingType type) => type.TopRailProfile == HandrailProfile.Round ? type.TopRailWidth : type.TopRailDepth;

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
        document.ProjectInformation.ClearanceStandard == ClearanceStandard.Ada ? 762 : building == RailingBuilding.Dwelling ? 600 : 380;

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
            .ToList();
        var railings = document.Elements.OfType<Railing>().ToList();
        var others = document.Elements.OfType<Slab>().Where(other => !ReferenceEquals(other, slab) && other.Boundary.Count >= 3 && FloorSurfaces.IsStoodOn(document, other)).ToList();

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

                // Beyond it: the highest floor or ground below, or a floor carrying on near its height.
                var beyond = middle + outward * 60;
                var below = others.Where(other => other.Contains(beyond)).Select(other => FloorSurfaces.TopAt(document, other, beyond))
                    .Where(height => height <= top + Stairs.FloorReach).DefaultIfEmpty(Terrains.GroundAt(document, beyond) ?? lowest).Max();
                var drop = top - below;

                bool Wall() => walls.Any(wall => wall.Base <= top + 100 && wall.Top >= top + 900 && (wall.Holds(middle) || wall.Holds(beyond) || wall.Holds(middle - outward * 30)));
                bool Stair() => arrivals.Any(arrival => Math.Abs(arrival.Top - top) < Stairs.FloorReach && Across(middle, arrival.From, arrival.To));
                bool Railed() => railings.Any(railing => Math.Abs(Base(document, railing, middle) - top) < Stairs.FloorReach
                    && Lengths(railing).Any(run => Line2D.DistanceFromSegment(middle, run.From, run.To) < 300));

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
        document.TypesOf<RailingType>().Where(type => type.Height >= height - 0.5 && type.Infill != RailingInfill.HorizontalBars).OrderBy(type => type.Height).FirstOrDefault()
        ?? document.TypesOf<RailingType>().OrderByDescending(type => type.Height).FirstOrDefault();

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
            made.Add(railing);
        }

        string Metres(double mm) => $"{mm / 1000:0.0} m";
        var left = new List<string>();
        if (walled > 0) left.Add($"{Metres(walled)} against walls");
        if (joined > 0) left.Add($"{Metres(joined)} where a floor carries on");
        if (atStairs > 0) left.Add($"{Metres(atStairs)} where a stair comes up");
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
        var asked = standard == ClearanceStandard.Ada ? "the IRC asks for a guard 36 in (914) high" : slab is Roof || !Within(document, slab.LevelId, Polygon2D.Centroid(slab.Boundary, slab.Boundary[0]))
            ? "Approved Document K asks for guarding 1100 high round a balcony or a roof"
            : "Approved Document K asks for guarding 900 high at a floor's edge inside a home";
        return $"{length / 1000:0.0} m of its edge drops {drop:0} with no guarding: {asked} - Guard Edges puts it up.";
    }
}
