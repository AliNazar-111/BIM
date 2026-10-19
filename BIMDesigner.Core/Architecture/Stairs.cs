using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How a stair is laid out in plan: one straight flight; two with a quarter or half turn
/// between; a spiral round a column; a curve round an open well; one wide flight up to a
/// landing that splits into two going off either way; three flights turning a quarter twice;
/// or drawn by clicking along its walking line, turning wherever it was clicked.
/// </summary>
public enum StairShape
{
    Straight,
    QuarterTurn,
    HalfTurn,
    Spiral,
    Curved,
    Bifurcated,
    ThreeQuarterTurn,
    Custom,
    AlternatingTread,
    Ladder
}

/// <summary>
/// What a stair is for, as Approved Document K sorts stairs, each with its own limits: a private
/// stair, within one dwelling; a utility stair, for escape, access for maintenance, or purposes
/// other than as the usual way between levels; or a general access stair, the usual way between
/// levels for anyone in a building other than a dwelling.
/// </summary>
public enum StairUse
{
    Private,
    Utility,
    GeneralAccess
}

/// <summary>A stair's bottom step: plain, or a bullnose - its end rounded out past the string - at one end or both.</summary>
public enum BottomStep
{
    Plain,
    BullnoseLeft,
    BullnoseRight,
    BullnoseBoth
}

/// <summary>The front edge of a timber tread: square, rounded over - a bullnose - or chamfered.</summary>
public enum NosingProfile
{
    Square,
    Rounded,
    Chamfered
}

/// <summary>
/// What a turning stair turns on: a landing; winders round a newel post - three for a quarter
/// turn, six round two for a half; or balanced winders, with no newel, the turn's taper shared
/// out among the treads either side of it.
/// </summary>
public enum StairCorner
{
    Landing,
    Winders,
    BalancedWinders
}

/// <summary>How a stair is built: timber treads and risers housed in strings, or concrete cast as one.</summary>
public enum StairConstruction
{
    Timber,
    Concrete
}

/// <summary>
/// What carries a timber stair's treads: strings either side they are housed in; a cut string on
/// an open side, its top cut to the steps so the treads sit on it; a single steel spine up the
/// middle; steel plates either side; or nothing - each tread built into the wall beside it.
/// </summary>
public enum StairSupport
{
    ClosedStrings,
    CutStrings,
    MonoStringer,
    SteelStringers,
    Cantilevered
}

/// <summary>What fills a balustrade under its handrail: balusters, or toughened glass.</summary>
public enum StairBalustrade
{
    Balusters,
    Glass
}

/// <summary>A handrail's section: round, the grip a hand closes round, or rectangular.</summary>
public enum HandrailProfile
{
    Round,
    Rectangular
}

/// <summary>
/// How a handrail on a wall finishes at each end: turned back into the wall, turned down to the
/// floor, stopped on a post, or cut off square - which catches a sleeve or a bag strap.
/// </summary>
public enum HandrailEnds
{
    ReturnToWall,
    ReturnToFloor,
    Post,
    Cut
}

/// <summary>Which way the Stair tool draws a stair from where it is clicked.</summary>
public enum StairDirection
{
    /// <summary>Down off the edge of a floor, deck or terrace over open space; up otherwise.</summary>
    Auto,
    Up,
    Down
}

/// <summary>Which sides of a stair have a handrail - worked out from the walls beside it, or said.</summary>
public enum StairRailings
{
    Auto,
    BothSides,
    LeftSide,
    RightSide,
    None
}

/// <summary>
/// A stair type: how it is built and set out - the largest rise it takes and its going, its
/// nosings and treads, open or closed risers, and what carries them - and its handrail's
/// height, and the balusters or glass under it.
/// </summary>
public sealed class StairType : ElementType
{
    public StairType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Stairs;

    public StairConstruction Construction { get; set; } = StairConstruction.Timber;

    /// <summary>The largest each rise may be: the storey's height is shared equally among as few risers as keep within it, mm.</summary>
    public double MaxRiser { get; set; } = 200;

    /// <summary>Each tread's going, nosing to nosing, mm.</summary>
    public double Going { get; set; } = 225;

    /// <summary>How far a tread's front stands over the riser below, mm.</summary>
    public double Nosing { get; set; } = 20;

    public double TreadThickness { get; set; } = 30;

    /// <summary>Whether its risers are open - no boards between the treads.</summary>
    public bool OpenRisers { get; set; }

    /// <summary>Whether each open riser has a bar across it halfway up, so a 100 mm sphere cannot pass between the treads.</summary>
    public bool RiserBars { get; set; }

    /// <summary>What carries its treads, if it is timber: a concrete stair is cast as one, and a spiral's treads are fixed to its column.</summary>
    public StairSupport Support { get; set; } = StairSupport.ClosedStrings;

    public StairBalustrade Balustrade { get; set; } = StairBalustrade.Balusters;

    /// <summary>Its handrail's top above the pitch line - the line through the nosings - mm.</summary>
    public double HandrailHeight { get; set; } = 900;

    /// <summary>Its balusters' spacing, centre to centre, mm: 32 mm balusters at 120 leave 88 between, short of the 100 a child's head goes through.</summary>
    public double BalusterSpacing { get; set; } = 120;

    /// <summary>Its handrails' section: round - a 44 mopstick - or rectangular.</summary>
    public HandrailProfile HandrailProfile { get; set; } = HandrailProfile.Round;

    /// <summary>A round handrail's diameter, or a rectangular one's width, mm.</summary>
    public double HandrailWidth { get; set; } = 44;

    /// <summary>A rectangular handrail's depth, mm.</summary>
    public double HandrailDepth { get; set; } = 39;

    /// <summary>The gap between a handrail on a wall and the wall, for the hand, mm.</summary>
    public double WallClearance { get; set; } = 60;

    /// <summary>How far a handrail on a wall runs on, level, past its top and bottom risers, mm: 300 in a building other than a house.</summary>
    public double HandrailExtension { get; set; }

    public HandrailEnds HandrailEnds { get; set; } = HandrailEnds.ReturnToWall;

    /// <summary>
    /// How high its guarding stands where it is level - along a landing, round the stairwell on
    /// the floor above - mm: 900 in a house, 36 in (914) under the IRC; 920 meets both.
    /// </summary>
    public double GuardHeight { get; set; } = 920;

    /// <summary>Whether it has a second handrail at 600, under the first, for children.</summary>
    public bool LowerRail { get; set; }

    /// <summary>The front edge of its timber treads.</summary>
    public NosingProfile NosingProfile { get; set; } = NosingProfile.Square;

    /// <summary>What its risers, strings, handrails and balusters are made of, each; none for how they are usually finished.</summary>
    public Guid RiserMaterialId { get; set; }
    public Guid StringMaterialId { get; set; }
    public Guid RailMaterialId { get; set; }
    public Guid BalusterMaterialId { get; set; }

    /// <summary>What it reads as with no material: a light oak.</summary>
    public ColourRgb Colour { get; set; } = new(0xC8, 0xA8, 0x7A);

    public Guid MaterialId { get; set; }

    public StairType Duplicate(string name) => WithCommonOf(new StairType(name)
    {
        Construction = Construction, MaxRiser = MaxRiser, Going = Going, Nosing = Nosing, TreadThickness = TreadThickness,
        OpenRisers = OpenRisers, RiserBars = RiserBars, Support = Support, Balustrade = Balustrade,
        HandrailHeight = HandrailHeight, BalusterSpacing = BalusterSpacing, Colour = Colour, MaterialId = MaterialId,
        HandrailProfile = HandrailProfile, HandrailWidth = HandrailWidth, HandrailDepth = HandrailDepth, WallClearance = WallClearance,
        HandrailExtension = HandrailExtension, HandrailEnds = HandrailEnds, GuardHeight = GuardHeight, LowerRail = LowerRail,
        NosingProfile = NosingProfile, RiserMaterialId = RiserMaterialId, StringMaterialId = StringMaterialId,
        RailMaterialId = RailMaterialId, BalusterMaterialId = BalusterMaterialId
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(StairTypeParameters.Construction,
            () => EnumText.Humanise(Construction),
            v => { if (EnumText.TryParse<StairConstruction>(v, out var construction)) Construction = construction; },
            EnumText.Choices<StairConstruction>());
        yield return Relaying(document, StairTypeParameters.MaxRiser, () => MaxRiser, v => MaxRiser = v, 100, 300);
        yield return Relaying(document, StairTypeParameters.Going, () => Going, v => Going = v, 150, 500);
        yield return Size(StairTypeParameters.Nosing, () => Nosing, v => Nosing = v, 0, 60);
        yield return Size(StairTypeParameters.TreadThickness, () => TreadThickness, v => TreadThickness = v, 15, 100);
        yield return ParameterValue.BindChoice(StairTypeParameters.Support,
            () => EnumText.Humanise(Support),
            v => { if (EnumText.TryParse<StairSupport>(v, out var support)) Support = support; },
            EnumText.Choices<StairSupport>());
        yield return ParameterValue.Bind(StairTypeParameters.OpenRisers, () => OpenRisers, v => OpenRisers = v);
        yield return ParameterValue.Bind(StairTypeParameters.RiserBars, () => RiserBars, v => RiserBars = v);
        yield return ParameterValue.BindChoice(StairTypeParameters.Balustrade,
            () => EnumText.Humanise(Balustrade),
            v => { if (EnumText.TryParse<StairBalustrade>(v, out var balustrade)) Balustrade = balustrade; },
            EnumText.Choices<StairBalustrade>());
        yield return Size(StairTypeParameters.HandrailHeight, () => HandrailHeight, v => HandrailHeight = v, 700, 1200);
        yield return Size(StairTypeParameters.BalusterSpacing, () => BalusterSpacing, v => BalusterSpacing = v, 60, 300);
        yield return ParameterValue.BindChoice(StairTypeParameters.HandrailProfile,
            () => EnumText.Humanise(HandrailProfile),
            v => { if (EnumText.TryParse<HandrailProfile>(v, out var profile)) HandrailProfile = profile; },
            EnumText.Choices<HandrailProfile>());
        yield return Size(StairTypeParameters.HandrailWidth, () => HandrailWidth, v => HandrailWidth = v, 20, 150);
        yield return Size(StairTypeParameters.HandrailDepth, () => HandrailDepth, v => HandrailDepth = v, 20, 150);
        yield return Size(StairTypeParameters.WallClearance, () => WallClearance, v => WallClearance = v, 20, 150);
        yield return Size(StairTypeParameters.HandrailExtension, () => HandrailExtension, v => HandrailExtension = v, 0, 1000);
        yield return ParameterValue.BindChoice(StairTypeParameters.HandrailEnds,
            () => EnumText.Humanise(HandrailEnds),
            v => { if (EnumText.TryParse<HandrailEnds>(v, out var ends)) HandrailEnds = ends; },
            EnumText.Choices<HandrailEnds>());
        yield return Size(StairTypeParameters.GuardHeight, () => GuardHeight, v => GuardHeight = v, 700, 1500);
        yield return ParameterValue.Bind(StairTypeParameters.LowerRail, () => LowerRail, v => LowerRail = v);

        yield return ParameterValue.BindChoice(StairTypeParameters.NosingProfile,
            () => EnumText.Humanise(NosingProfile),
            v => { if (EnumText.TryParse<NosingProfile>(v, out var profile)) NosingProfile = profile; },
            EnumText.Choices<NosingProfile>());

        if (document is not null)
        {
            var names = document.Materials.Select(material => material.Name).OrderBy(name => name).ToArray();
            ParameterValue Material(ParameterDefinition definition, string usual, Func<Guid> get, Action<Guid> set) =>
                ParameterValue.BindChoice(definition,
                    () => document.FindMaterial(get())?.Name ?? usual,
                    v => set(document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty),
                    names.Prepend(usual).ToArray());

            yield return Material(StairTypeParameters.Material, ComponentType.ByColour, () => MaterialId, id => MaterialId = id);
            yield return Material(StairTypeParameters.RiserMaterial, "As usual", () => RiserMaterialId, id => RiserMaterialId = id);
            yield return Material(StairTypeParameters.StringMaterial, "As usual", () => StringMaterialId, id => StringMaterialId = id);
            yield return Material(StairTypeParameters.RailMaterial, "As usual", () => RailMaterialId, id => RailMaterialId = id);
            yield return Material(StairTypeParameters.BalusterMaterial, "As usual", () => BalusterMaterialId, id => BalusterMaterialId = id);
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
    /// A size that lays out every stair of the type afresh, as one step to undo: those hung by
    /// their heads from a terrace or a balcony stay hung there.
    /// </summary>
    private ParameterValue Relaying(BimDocument? document, ParameterDefinition definition, Func<double> get, Action<double> set, double least, double most)
    {
        if (document is null) return Size(definition, get, set, least, most);

        Func<double, IUndoableCommand?> change = v =>
        {
            if (v < least || v > most) return null;
            var before = get();
            return new StairRelaidCommand(document, document.Elements.OfType<Stair>().Where(stair => stair.TypeId == Id),
                new SettingCommand($"Change {definition.Name}", () => set(v), () => set(before)));
        };
        return ParameterValue.BindCommand(definition, get, change);
    }

    /// <summary>
    /// The stair types a project starts with: timber with closed or open risers, or a cut string;
    /// concrete; and oak treads on a steel spine, on steel stringers, or cantilevered from a wall.
    /// Those with open risers have a bar across each, as a house's stair needs.
    /// </summary>
    public static IReadOnlyList<StairType> Library() => new[]
    {
        new StairType("Stair - Timber, Closed Risers") { TypeMark = "ST1", Cost = 1800m },
        new StairType("Stair - Timber, Open Risers") { OpenRisers = true, RiserBars = true, TypeMark = "ST2", Cost = 2100m },
        new StairType("Stair - Concrete")
        {
            Construction = StairConstruction.Concrete, MaxRiser = 175, Going = 250, Nosing = 0, Colour = new ColourRgb(0xB5, 0xB3, 0xAE),
            TypeMark = "ST3", Cost = 3200m
        },
        new StairType("Stair - Timber, Cut String") { Support = StairSupport.CutStrings, TypeMark = "ST4", Cost = 2300m },
        new StairType("Stair - Oak Treads on a Steel Spine")
        {
            Support = StairSupport.MonoStringer, OpenRisers = true, RiserBars = true, TreadThickness = 60, Balustrade = StairBalustrade.Glass,
            TypeMark = "ST5", Cost = 6500m
        },
        new StairType("Stair - Oak Treads on Steel Stringers")
        {
            Support = StairSupport.SteelStringers, OpenRisers = true, RiserBars = true, TreadThickness = 40, TypeMark = "ST6", Cost = 4200m
        },
        new StairType("Stair - Floating Oak Treads")
        {
            Support = StairSupport.Cantilevered, OpenRisers = true, RiserBars = true, TreadThickness = 80, Balustrade = StairBalustrade.Glass,
            TypeMark = "ST7", Cost = 7500m
        }
    };
}

public static class StairTypeParameters
{
    public static readonly ParameterDefinition Construction = new("Construction", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition MaxRiser = new("Maximum Riser Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Going = new("Going", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Nosing = new("Nosing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition TreadThickness = new("Tread Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition OpenRisers = new("Open Risers", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition RiserBars = new("Bars Across Open Risers", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Support = new("Support", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Balustrade = new("Balustrade", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition HandrailHeight = new("Handrail Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BalusterSpacing = new("Baluster Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailProfile = new("Handrail Profile", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition HandrailWidth = new("Handrail Diameter or Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailDepth = new("Handrail Depth (Rectangular)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition WallClearance = new("Handrail Clearance from Wall", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailExtension = new("Handrail Extension at Ends", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailEnds = new("Handrail Ends", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition GuardHeight = new("Guarding Height on Landings", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition LowerRail = new("Second Handrail at 600", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition NosingProfile = new("Nosing Profile", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition RiserMaterial = new("Riser Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition StringMaterial = new("String Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition RailMaterial = new("Handrail Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition BalusterMaterial = new("Baluster Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
}

/// <summary>
/// A stair from one level to another: where its first riser is and which way it goes up, how
/// wide it is, and whether it runs straight or turns - a quarter or a half, on a landing or on
/// winders - spirals, curves, or splits in two. How many risers it has, and how high each, it works out from the storey's
/// height - from the floor it stands on to the floor it reaches - so it re-plans itself when
/// either moves. It cuts its own opening in the floor above it, as long as headroom asks.
/// </summary>
public sealed class Stair : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Stairs;

    /// <summary>The middle of its first riser, at its foot.</summary>
    public Point2D Start { get; set; }

    /// <summary>Which way its first flight goes up, anticlockwise from the X axis, degrees.</summary>
    public double Angle { get; set; } = 90;

    public StairShape Shape { get; set; }

    public StairCorner Corner { get; set; }

    /// <summary>Whether it turns left going up; otherwise right.</summary>
    public bool TurnsLeft { get; set; }

    /// <summary>Its clear width, between its strings, mm.</summary>
    public double Width { get; set; } = 900;

    /// <summary>How many risers before the turn - none for halfway.</summary>
    public int? RisersBeforeTurn { get; set; }

    /// <summary>The gap between the two flights of a half-turn stair, mm.</summary>
    public double Well { get; set; } = 100;

    /// <summary>
    /// How far a spiral or curved stair's inside edge is from its centre, mm: a spiral's column,
    /// a curved stair's well. None for the usual - a 120 column, or a curve 1500 in.
    /// </summary>
    public double InnerRadius { get; set; }

    /// <summary>
    /// A drawn stair's walking line, as it was clicked: how long each stretch of it is, from its
    /// foot to its first turn, turn to turn, and on to its head, mm...
    /// </summary>
    public List<double> PathLengths { get; set; } = new();

    /// <summary>
    /// ...and how far it turns at each turn, degrees, right going up - left for one that turns
    /// left, mirrored. A straight on is a landing in a straight run.
    /// </summary>
    public List<double> PathTurns { get; set; } = new();

    /// <summary>
    /// The level it repeats up to, storey on storey, each laid out from that storey's floor to
    /// the next - the same stair all the way up a stairwell; none for a stair of one storey.
    /// </summary>
    public Guid? RepeatToLevelId { get; set; }

    /// <summary>Whether its plan numbers its risers, one by one from its foot.</summary>
    public bool ShowRiserNumbers { get; set; }

    /// <summary>Whether its plan dimensions each straight flight: so many treads at its going, and how far that is.</summary>
    public bool ShowFlightDimensions { get; set; }

    /// <summary>What it is for, as Approved Document K sorts stairs - which sets its limits.</summary>
    public StairUse Use { get; set; } = StairUse.Private;

    /// <summary>
    /// Whether it goes up to a room made in a loft: where 2000 headroom cannot be had, Approved
    /// Document K accepts 1900 at the middle of the stair falling to 1800 at its side - and only
    /// there allows alternating treads or a fixed ladder.
    /// </summary>
    public bool LoftConversion { get; set; }

    /// <summary>
    /// Whether it is outside the building - up to a balcony, a deck or a roof terrace, or down
    /// to the garden: a house's stair outside wants goings of 280 at least (Approved Document K,
    /// Table 1.1). Set when it is put down outside the walls.
    /// </summary>
    public bool Outside { get; set; }

    public BottomStep BottomStep { get; set; } = BottomStep.Plain;

    public double BaseOffset { get; set; }

    /// <summary>The level it goes up to - the one above, usually; none for a stair of its own height.</summary>
    public Guid? TopLevelId { get; set; }

    public double TopOffset { get; set; }

    /// <summary>How high it goes with no top level, mm.</summary>
    public double UnconnectedHeight { get; set; } = 2600;

    public StairRailings Railings { get; set; } = StairRailings.Auto;

    /// <summary>Whether it cuts the floor over it for headroom.</summary>
    public bool CutsFloorAbove { get; set; } = true;

    /// <summary>
    /// Whether it cuts a pitched roof it comes up into - under the eaves outside, up to an attic -
    /// for headroom, as it does a floor; told not to, its headroom under the roof is said instead.
    /// </summary>
    public bool CutsRoof { get; set; } = true;

    /// <summary>Which way is up its first flight.</summary>
    public Vector2D Up => new(Math.Cos(Angle * Math.PI / 180), Math.Sin(Angle * Math.PI / 180));

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var levels = document.Levels.Select(level => level.Name).ToArray();
        const string unconnected = "Unconnected";

        // A stair goes up: no base level or offset that would put its foot at or above its head, nor a top level at or below its foot.
        bool Rises(Guid baseId, double baseOffset, Guid? topId, double topOffset) =>
            topId is not { } top || document.FindLevel(top) is not { } upper || document.FindLevel(baseId) is not { } lower
            || upper.Elevation + topOffset - (lower.Elevation + baseOffset) >= Stairs.MinimumRise - 0.5;
        var baseElevation = document.FindLevel(LevelId)?.Elevation ?? double.NegativeInfinity;

        // Laid out afresh, as one step to undo: hung by its head from a terrace or a balcony, it stays hung there.
        IUndoableCommand Relaid<T>(ParameterDefinition definition, T before, T after, Action<T> set) =>
            new StairRelaidCommand(document, new[] { this }, new SettingCommand($"Change {definition.Name}", () => set(after), () => set(before)));

        yield return ParameterValue.BindChoiceCommand(StairParameters.BaseLevel,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => document.Levels.FirstOrDefault(level => level.Name == v) is { } level && Rises(level.Id, BaseOffset, TopLevelId, TopOffset)
                ? Relaid(StairParameters.BaseLevel, LevelId, level.Id, id => LevelId = id)
                : null,
            levels);
        yield return ParameterValue.BindCommand(StairParameters.BaseOffset, () => BaseOffset, (double v) =>
            Rises(LevelId, v, TopLevelId, TopOffset) ? Relaid(StairParameters.BaseOffset, BaseOffset, v, offset => BaseOffset = offset) : null);
        yield return ParameterValue.BindChoiceValidated(StairParameters.TopLevel,
            () => TopLevelId is { } top ? document.FindLevel(top)?.Name ?? unconnected : unconnected,
            v =>
            {
                var level = document.Levels.FirstOrDefault(candidate => candidate.Name == v);
                if (level is not null && !Rises(LevelId, BaseOffset, level.Id, TopOffset)) return false;
                TopLevelId = level?.Id;
                return true;
            },
            document.Levels.Where(level => level.Elevation > baseElevation || level.Id == TopLevelId).Select(level => level.Name).Append(unconnected).ToArray());
        yield return ParameterValue.BindValidated(StairParameters.TopOffset, () => TopOffset, (double v) =>
        {
            if (!Rises(LevelId, BaseOffset, TopLevelId, v)) return false;
            TopOffset = v;
            return true;
        });
        yield return ParameterValue.BindValidated(StairParameters.UnconnectedHeight, () => UnconnectedHeight, (double v) =>
        {
            if (v < Stairs.MinimumRise) return false;
            UnconnectedHeight = v;
            return true;
        });

        // Up to any level above the one it reaches.
        const string once = "No";
        var reached = TopLevelId is { } topId ? document.FindLevel(topId) : null;
        var higher = reached is null ? Array.Empty<string>() : document.Levels.Where(level => level.Elevation > reached.Elevation).OrderBy(level => level.Elevation).Select(level => level.Name).ToArray();
        yield return ParameterValue.BindChoice(StairParameters.RepeatTo,
            () => RepeatToLevelId is { } repeat ? document.FindLevel(repeat)?.Name ?? once : once,
            v => RepeatToLevelId = higher.Contains(v) ? document.Levels.FirstOrDefault(level => level.Name == v)?.Id : null,
            higher.Prepend(once).ToArray());

        yield return ParameterValue.BindChoiceCommand(StairParameters.Shape,
            () => EnumText.Humanise(Shape),
            v => EnumText.TryParse<StairShape>(v, out var shape) ? Relaid(StairParameters.Shape, Shape, shape, s => Shape = s) : null,
            EnumText.Choices<StairShape>());
        yield return ParameterValue.BindChoiceCommand(StairParameters.Corner,
            () => EnumText.Humanise(Corner),
            v => EnumText.TryParse<StairCorner>(v, out var corner) ? Relaid(StairParameters.Corner, Corner, corner, c => Corner = c) : null,
            EnumText.Choices<StairCorner>());
        yield return ParameterValue.BindChoiceCommand(StairParameters.Turns,
            () => TurnsLeft ? "Left" : "Right",
            v => Relaid(StairParameters.Turns, TurnsLeft, v == "Left", left => TurnsLeft = left),
            new[] { "Left", "Right" });
        yield return ParameterValue.BindCommand(StairParameters.Width, () => Width, (double v) =>
            v < 500 || v > 5000 ? null : Relaid(StairParameters.Width, Width, v, width => Width = width));
        yield return ParameterValue.BindCommand(StairParameters.RisersBeforeTurn, () => RisersBeforeTurn ?? 0, (int v) =>
            v < 0 ? null : Relaid(StairParameters.RisersBeforeTurn, RisersBeforeTurn, v == 0 ? null : v, risers => RisersBeforeTurn = risers));
        yield return ParameterValue.BindCommand(StairParameters.Well, () => Well, (double v) =>
            v < 0 || v > 3000 ? null : Relaid(StairParameters.Well, Well, v, well => Well = well));
        yield return ParameterValue.BindCommand(StairParameters.InnerRadius, () => InnerRadius, (double v) =>
            v < 0 || v > 10000 ? null : Relaid(StairParameters.InnerRadius, InnerRadius, v, radius => InnerRadius = radius));
        yield return ParameterValue.BindValidated(StairParameters.Angle, () => Angle, (double v) => { Angle = ((v % 360) + 360) % 360; return true; });
        yield return ParameterValue.BindChoice(StairParameters.Railings,
            () => EnumText.Humanise(Railings),
            v => { if (EnumText.TryParse<StairRailings>(v, out var railings)) Railings = railings; },
            EnumText.Choices<StairRailings>());
        yield return ParameterValue.Bind(StairParameters.CutsFloorAbove, () => CutsFloorAbove, v => CutsFloorAbove = v);
        yield return ParameterValue.Bind(StairParameters.CutsRoof, () => CutsRoof, v => CutsRoof = v);
        yield return ParameterValue.ReadOnly(StairParameters.Cuts, () => Stairs.CutsThrough(document, this));
        yield return ParameterValue.BindChoice(StairParameters.Use,
            () => EnumText.Humanise(Use),
            v => { if (EnumText.TryParse<StairUse>(v, out var use)) Use = use; },
            EnumText.Choices<StairUse>());
        yield return ParameterValue.Bind(StairParameters.LoftConversion, () => LoftConversion, v => LoftConversion = v);
        yield return ParameterValue.Bind(StairParameters.Outside, () => Outside, v => Outside = v);
        yield return ParameterValue.BindChoice(StairParameters.BottomStep,
            () => EnumText.Humanise(BottomStep),
            v => { if (EnumText.TryParse<BottomStep>(v, out var step)) BottomStep = step; },
            EnumText.Choices<BottomStep>());
        yield return ParameterValue.Bind(StairParameters.ShowRiserNumbers, () => ShowRiserNumbers, v => ShowRiserNumbers = v);
        yield return ParameterValue.Bind(StairParameters.ShowFlightDimensions, () => ShowFlightDimensions, v => ShowFlightDimensions = v);

        // What it works out to, and whether that meets the regulations.
        var layout = Stairs.Layout(document, this);
        yield return ParameterValue.ReadOnly(StairParameters.Risers, () => layout.Risers);
        yield return ParameterValue.ReadOnly(StairParameters.RiserHeight, () => layout.Rise);
        yield return ParameterValue.ReadOnly(StairParameters.ActualGoing, () => layout.Going);
        yield return ParameterValue.ReadOnly(StairParameters.Pitch, () => Math.Round(layout.Pitch, 1));
        yield return ParameterValue.ReadOnly(StairParameters.Rule, () => Stairs.Verdict(document, this));
        yield return ParameterValue.ReadOnly(StairParameters.Summary, () => Stairs.Summary(document, this));
        yield return ParameterValue.BindFlagCommand(StairParameters.MakeItPass, () => false,
            (bool make, out string? message) =>
            {
                message = null;
                return make ? StairFixes.MakeItPass(document, this, out message) : null;
            });

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class StairParameters
{
    public static readonly ParameterDefinition BaseLevel = new("Base Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BaseOffset = new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition TopLevel = new("Top Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition TopOffset = new("Top Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition UnconnectedHeight = new("Unconnected Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Shape = new("Shape", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Corner = new("Turns On", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Turns = new("Turns", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition RisersBeforeTurn = new("Risers Before the Turn (0 for halfway)", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Well = new("Well Between Flights", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition InnerRadius = new("Inside Radius (0 for the usual)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition RepeatTo = new("Repeat Up To", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition ShowRiserNumbers = new("Number the Risers", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition ShowFlightDimensions = new("Dimension the Flights", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Summary = new("Stair Summary", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Use = new("Use", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition LoftConversion = new("Loft Conversion", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Outside = new("Outside", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BottomStep = new("Bottom Step", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition MakeItPass = new("Make It Pass", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Angle = new("Direction", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Railings = new("Handrails", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition CutsFloorAbove = new("Cut the Floor Above", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition CutsRoof = new("Cut the Roof", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Cuts = new("Cuts Through", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Risers = new("Risers", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition RiserHeight = new("Riser Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition ActualGoing = new("Actual Going", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Pitch = new("Pitch", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rule = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>One storey of a stair: the level it goes up from, the one it reaches, and how it is laid out between.</summary>
public sealed record StairFlight(Guid LevelId, Guid? TopLevelId, StairLayout Layout);

/// <summary>What one step of a stair is: a tread, a landing, or a winder turning a corner.</summary>
public enum StairStepKind
{
    Tread,
    Landing,
    Winder
}

/// <summary>
/// One step of a stair as it is laid out: its number up from the bottom, its outline in plan,
/// the height of its top, and its riser line - the line it is stepped up onto at - with which way is up across it.
/// </summary>
public sealed record StairStep(int Number, StairStepKind Kind, IReadOnlyList<Point2D> Outline, double Top, Point2D RiserFrom, Point2D RiserTo, Vector2D Up);

/// <summary>A point along one side of a stair, with the height of its pitch line there.</summary>
public readonly record struct StairSidePoint(Point2D At, double Height);

/// <summary>Which side of a stair a side is, going up it: its left, its right, or another - the back of a split stair's landing.</summary>
public enum StairSideRole
{
    Left,
    Right,
    Other
}

/// <summary>
/// One side of a stair: its points in order, with the pitch line's height at each; which side it
/// is; whether the stair is on its right going along it - as it is of a left side, going up; and
/// whether it is a spiral stair's central column, which has no handrail.
/// </summary>
public sealed record StairSide(IReadOnlyList<StairSidePoint> Points, StairSideRole Role, bool StairOnRight, bool AgainstPole = false);

/// <summary>A newel post - or a spiral stair's column - as an outline in plan, and how high its top is.</summary>
public sealed record StairNewel(IReadOnlyList<Point2D> Outline, double Top);

/// <summary>
/// A stair worked out: how many risers and how high, its steps in plan with their heights, the
/// lines walked up it, its sides with the pitch line along them, the newels it turns on, and
/// the edges where it reaches the floor - two, for a split stair.
/// </summary>
public sealed class StairLayout
{
    public StairShape Shape { get; init; }
    public double Bottom { get; init; }
    public double Top { get; init; }
    public int Risers { get; init; }
    public double Rise { get; init; }

    /// <summary>Its going: a flight's, or a curved or spiral stair's along its middle.</summary>
    public double Going { get; init; }

    public double Width { get; init; }

    /// <summary>Its widest flight: a split stair's first, half as wide again as the two it divides into.</summary>
    public double WidestFlight { get; init; }

    public IReadOnlyList<StairStep> Steps { get; init; } = Array.Empty<StairStep>();

    /// <summary>The lines walked up it: one, or for a split stair one up each way.</summary>
    public IReadOnlyList<IReadOnlyList<Point2D>> Walks { get; init; } = Array.Empty<IReadOnlyList<Point2D>>();

    public IReadOnlyList<Point2D> WalkingLine => Walks.Count > 0 ? Walks[0] : Array.Empty<Point2D>();

    /// <summary>Its sides: its left and right going up, and for a split stair the back of its landing.</summary>
    public IReadOnlyList<StairSide> Sides { get; init; } = Array.Empty<StairSide>();

    /// <summary>The left side going up, and the right: each the points it turns or steps at, with the pitch line's height there.</summary>
    public IReadOnlyList<StairSidePoint> Left => Sides.FirstOrDefault(side => side.Role == StairSideRole.Left)?.Points ?? Array.Empty<StairSidePoint>();
    public IReadOnlyList<StairSidePoint> Right => Sides.FirstOrDefault(side => side.Role == StairSideRole.Right)?.Points ?? Array.Empty<StairSidePoint>();

    /// <summary>The newels its winding corners turn on, or a spiral stair's column.</summary>
    public IReadOnlyList<StairNewel> Newels { get; init; } = Array.Empty<StairNewel>();

    public IReadOnlyList<Point2D>? Newel => Newels.Count > 0 ? Newels[0].Outline : null;

    /// <summary>A drawn stair's walking line as it is laid out: its foot, each turn, and its head; none for the others.</summary>
    public IReadOnlyList<Point2D> PathPoints { get; init; } = Array.Empty<Point2D>();

    /// <summary>The narrowest end of a winder, mm; none without winders.</summary>
    public double? WinderNarrowEnd { get; init; }

    /// <summary>A winder's going along the middle of the stair, mm; none without winders.</summary>
    public double? WinderGoing { get; init; }

    /// <summary>
    /// For tapered treads - winders, or a curved or spiral stair's - the going so far in from the
    /// narrow side, mm: where the rules measure it. None for straight treads.
    /// </summary>
    public Func<double, double>? GoingAt { get; init; }

    /// <summary>A curved or spiral stair's inside and outside radii, mm.</summary>
    public double InnerRadius { get; init; }
    public double OuterRadius { get; init; }

    /// <summary>How many treads a spiral or curved stair takes to go right round; none for the others.</summary>
    public double? TreadsPerTurn { get; init; }

    /// <summary>The centre a spiral or curved stair turns round; none for the others.</summary>
    public Point2D? Centre { get; init; }

    /// <summary>
    /// The pitch line's height over a point of a step: rising across a tread from its riser -
    /// round the centre, for a tread that curves - and level over a landing or a winder.
    /// </summary>
    public double PitchOver(StairStep step, Point2D point)
    {
        if (step.Kind != StairStepKind.Tread) return step.Top;
        if (Centre is not { } centre || TreadsPerTurn is not { } perTurn) return step.Top + (point - step.RiserFrom).Dot(step.Up) * Rise / Going;

        var (from, to) = (step.RiserTo - centre, point - centre);
        var way = Math.Sign(from.Cross(step.Up));
        var turned = Math.Atan2(from.Cross(to), from.Dot(to)) * way;
        return step.Top + turned / (2 * Math.PI / perTurn) * Rise;
    }

    /// <summary>Its pitch, degrees: how steep the line through its nosings is - along the middle of one that curves.</summary>
    public double Pitch => Math.Atan2(Rise, Going) * 180 / Math.PI;

    /// <summary>Where it comes out at the top: the edges of the floor it reaches - two, for a split stair.</summary>
    public IReadOnlyList<(Point2D From, Point2D To)> Arrivals { get; init; } = Array.Empty<(Point2D, Point2D)>();

    public (Point2D From, Point2D To) TopEdge => Arrivals.Count > 0 ? Arrivals[0] : default;

    /// <summary>Its first riser's middle, and the way up its first flight: what distances up it are measured from.</summary>
    public Point2D Origin { get; init; }
    public Vector2D Axis { get; init; }

    /// <summary>
    /// For a half turn, the well between its two flights from so far up the first one to the
    /// landing - so an opening over the flights takes the well with them, and leaves no sliver of
    /// floor between them; none for any other stair, or one with no well.
    /// </summary>
    public Func<double, IReadOnlyList<Point2D>?>? WellFrom { get; init; }

    /// <summary>Where a turning stair's first flight ends on the line walked, and how many risers come before it; none for a straight stair.</summary>
    public Point2D? TurnAt { get; init; }
    public int? TurnRisers { get; init; }

    /// <summary>A half turn's second flight, halfway along the line walked, and the way out across it from the first.</summary>
    public Point2D? WellAt { get; init; }
    public Vector2D? WellAxis { get; init; }

    /// <summary>
    /// A stair laid out from its foot: the storey's height shared among as few risers as keep each
    /// within the type's largest, its treads at the type's going, turning as it is told to.
    /// </summary>
    public static StairLayout Of(Stair stair, StairType type, double bottom, double top)
    {
        var height = Math.Max(50, top - bottom);
        var risers = Math.Max(2, (int)Math.Ceiling(height / Math.Max(50, type.MaxRiser) - 1e-9));
        var rise = height / risers;
        var g = Math.Max(100, type.Going);
        var w = Math.Max(300, stair.Width);
        var nosing = Math.Max(0, type.Nosing);
        var handrail = type.HandrailHeight;
        double Height(int step) => bottom + step * rise;

        if (stair.Shape is StairShape.Spiral or StairShape.Curved)
            return Circular(stair, type, bottom, top, risers, rise, g, w, nosing);

        if (stair.Shape is StairShape.Custom or StairShape.ThreeQuarterTurn)
            return Path(stair, type, bottom, top, risers, rise, g, w, nosing);

        if (stair.Shape == StairShape.AlternatingTread)
            return Alternating(stair, bottom, top, risers, rise, g, w, nosing);

        // A ladder: a straight flight as steep as a ship's ladder - 127 at least to a tread, 65% of the rise - each tread reaching back 90 past the next.
        var ladder = stair.Shape == StairShape.Ladder;
        if (ladder) (g, nosing) = (Math.Clamp(Math.Round(rise * 0.65), 127, Math.Max(127, g)), Math.Max(nosing, 90));

        // Balanced winders where there are treads enough either side of the turn to share it; otherwise winders round a newel.
        if (stair.Corner == StairCorner.BalancedWinders && stair.Shape is StairShape.QuarterTurn or StairShape.HalfTurn
            && Balanced(stair, bottom, top, risers, rise, g, w, nosing) is { } balanced)
            return balanced;

        // Laid out as if it turned right - up the first flight along u, the second toward -v -
        // then mirrored across its first flight for one that turns left. A split stair goes both ways.
        var up = stair.Up;
        var left = up.PerpendicularLeft();
        var mirror = stair.TurnsLeft && stair.Shape != StairShape.Bifurcated ? -1.0 : 1.0;
        Point2D P(double u, double v) => stair.Start + up * u + left * (v * mirror);
        Vector2D D(double u, double v) => up * u + left * (v * mirror);

        var steps = new List<StairStep>();

        // A flight of treads from a riser line, going one way, its sides either side of it.
        void Flight(double startU, double startV, double dirU, double dirV, int first, int count, double width)
        {
            var (acrossU, acrossV) = (-dirV, dirU);
            for (var i = 0; i < count; i++)
            {
                double U(double along, double across) => startU + dirU * along + acrossU * across;
                double V(double along, double across) => startV + dirV * along + acrossV * across;
                var (a0, a1) = (i * g - nosing, (i + 1) * g);
                var outline = new[]
                {
                    P(U(a0, -width / 2), V(a0, -width / 2)), P(U(a1, -width / 2), V(a1, -width / 2)),
                    P(U(a1, width / 2), V(a1, width / 2)), P(U(a0, width / 2), V(a0, width / 2))
                };
                steps.Add(new StairStep(first + i, StairStepKind.Tread, outline, Height(first + i),
                    P(U(i * g, -width / 2), V(i * g, -width / 2)), P(U(i * g, width / 2), V(i * g, width / 2)), D(dirU, dirV)));
            }
        }

        StairStep Landing(int number, double u0, double u1, double v0, double v1, double riserFrom, double riserTo) =>
            new(number, StairStepKind.Landing, new[] { P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1) }, Height(number),
                P(u0, riserFrom), P(u0, riserTo), D(1, 0));

        // A winder's corners, any the same as the one before - the two newels of a half turn with no well - taken once.
        StairStep Winder(int number, Point2D[] outline, (double U, double V) from, Point2D to, Vector2D upward) =>
            new(number, StairStepKind.Winder, outline.Where((point, i) => i == 0 || (point - outline[i - 1]).Length > 0.5).ToList(),
                Height(number), P(from.U, from.V), to, upward);

        var shape = ladder ? StairShape.Straight : stair.Shape;
        var winders = stair.Corner is StairCorner.Winders or StairCorner.BalancedWinders;
        var turnSteps = shape switch
        {
            StairShape.Straight => 0,
            StairShape.QuarterTurn => winders ? 3 : 1,
            StairShape.HalfTurn => winders ? 6 : 1,
            _ => 1
        };
        if (risers - 1 < turnSteps + 1) (shape, turnSteps) = (StairShape.Straight, 0);

        // Risers up to the turn: as asked, or halfway; at least one, leaving the second flight its treads.
        var beforeTurn = Math.Clamp(stair.RisersBeforeTurn ?? (risers - turnSteps + 1) / 2, 1, Math.Max(1, risers - turnSteps));
        var c1 = shape == StairShape.Straight ? risers - 1 : beforeTurn - 1;
        var l1 = c1 * g;
        var c2 = shape == StairShape.Straight ? 0 : risers - 1 - (beforeTurn - 1) - turnSteps;
        var l2 = c2 * g;
        var h1 = Height(1);

        // A split stair's first flight is half as wide again as the two it divides into.
        var main = shape == StairShape.Bifurcated ? w * 1.5 : w;
        Flight(0, 0, 1, 0, 1, c1, main);

        var walks = new List<IReadOnlyList<Point2D>>();
        List<StairSidePoint> outer, inner;
        List<StairSidePoint>? back = null;
        var newels = new List<StairNewel>();
        var arrivals = new List<(Point2D, Point2D)>();
        Func<double, IReadOnlyList<Point2D>?>? wellFrom = null;
        Func<double, double>? goingAt = null;
        double? narrowEnd = null, winderGoing = null;
        StairSidePoint S(double u, double v, double h) => new(P(u, v), h);

        // A winding corner's three winders: round a 100 newel, their risers fanning out from it at 30°
        // apart - each narrow end on the newel's faces, 58 at the least.
        const double post = 100;
        var t = Math.Tan(Math.PI / 6);
        var (cos, sin) = (Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6));
        StairNewel Newel(double u, double v, double du, double dv, double highest) =>
            new(new[] { P(u, v), P(u + du, v), P(u + du, v + dv), P(u, v + dv) }, highest + handrail + 100);
        IEnumerable<Point2D> Arc(double u, double v, double fromDegrees, double toDegrees) =>
            Enumerable.Range(0, 7).Select(k => (fromDegrees + (toDegrees - fromDegrees) * k / 6.0) * Math.PI / 180)
                .Select(a => P(u + w / 2 * Math.Cos(a), v + w / 2 * Math.Sin(a)));

        switch (shape)
        {
            case StairShape.QuarterTurn when winders:
            {
                var c = (U: l1, V: -w / 2);
                var n = beforeTurn;
                steps.Add(Winder(n, new[] { P(c.U, c.V), P(l1, w / 2), P(l1 + t * w, w / 2) }, c, P(l1, w / 2), D(1, 0)));
                steps.Add(Winder(n + 1, new[] { P(c.U, c.V), P(l1 + t * w, w / 2), P(l1 + w, w / 2), P(l1 + w, -w / 2 + t * w) }, c, P(l1 + t * w, w / 2), D(cos, -sin)));
                steps.Add(Winder(n + 2, new[] { P(c.U, c.V), P(l1 + w, -w / 2 + t * w), P(l1 + w, -w / 2) }, c, P(l1 + w, -w / 2 + t * w), D(sin, -cos)));
                Flight(l1 + w / 2, -w / 2, 0, -1, n + 3, c2, w);

                newels.Add(Newel(c.U, c.V, post, post, Height(n + 3)));
                walks.Add(new[] { P(0, 0) }.Concat(Arc(c.U, c.V, 90, 0)).Append(P(l1 + w / 2, -w / 2 - l2)).ToList());

                // The outer side rises steadily round the corner, the inner steps up at the newel.
                outer = new List<StairSidePoint>
                {
                    S(0, w / 2, h1), S(l1, w / 2, Height(n)), S(l1 + t * w, w / 2, Height(n + 1)), S(l1 + w, w / 2, Height(n + 1) + rise / 2),
                    S(l1 + w, -w / 2 + t * w, Height(n + 2)), S(l1 + w, -w / 2, Height(n + 3)), S(l1 + w, -w / 2 - l2, top)
                };
                inner = new List<StairSidePoint> { S(0, -w / 2, h1), S(c.U, c.V, Height(n)), S(c.U, c.V, Height(n + 3)), S(l1, -w / 2 - l2, top) };
                arrivals.Add((P(l1, -w / 2 - l2), P(l1 + w, -w / 2 - l2)));
                break;
            }

            case StairShape.QuarterTurn:
            {
                var n = beforeTurn;
                steps.Add(Landing(n, l1, l1 + w, -w / 2, w / 2, -w / 2, w / 2));
                Flight(l1 + w / 2, -w / 2, 0, -1, n + 1, c2, w);
                walks.Add(new[] { P(0, 0), P(l1 + w / 2, 0), P(l1 + w / 2, -w / 2 - l2) });
                outer = new List<StairSidePoint>
                {
                    S(0, w / 2, h1), S(l1, w / 2, Height(n)), S(l1 + w, w / 2, Height(n)), S(l1 + w, -w / 2, Height(n)),
                    S(l1 + w, -w / 2, Height(n + 1)), S(l1 + w, -w / 2 - l2, top)
                };
                inner = new List<StairSidePoint> { S(0, -w / 2, h1), S(l1, -w / 2, Height(n)), S(l1, -w / 2, Height(n + 1)), S(l1, -w / 2 - l2, top) };
                arrivals.Add((P(l1, -w / 2 - l2), P(l1 + w, -w / 2 - l2)));
                break;
            }

            case StairShape.HalfTurn:
            {
                var n = beforeTurn;
                var well = Math.Max(0, stair.Well);
                var far = -w / 2 - well - w;
                var near = -w / 2 - well;

                if (winders)
                {
                    // Two winding corners, three winders round a newel at each end of the well; the
                    // third winder runs on across the well to the second corner.
                    var a = (U: l1, V: -w / 2);
                    var b = (U: l1, V: near);
                    steps.Add(Winder(n, new[] { P(a.U, a.V), P(l1, w / 2), P(l1 + t * w, w / 2) }, a, P(l1, w / 2), D(1, 0)));
                    steps.Add(Winder(n + 1, new[] { P(a.U, a.V), P(l1 + t * w, w / 2), P(l1 + w, w / 2), P(l1 + w, -w / 2 + t * w) }, a, P(l1 + t * w, w / 2), D(cos, -sin)));
                    steps.Add(Winder(n + 2, new[] { P(a.U, a.V), P(l1 + w, -w / 2 + t * w), P(l1 + w, near), P(b.U, b.V) }, a, P(l1 + w, -w / 2 + t * w), D(sin, -cos)));
                    steps.Add(Winder(n + 3, new[] { P(b.U, b.V), P(l1 + w, near), P(l1 + w, near - t * w) }, b, P(l1 + w, near), D(0, -1)));
                    steps.Add(Winder(n + 4, new[] { P(b.U, b.V), P(l1 + w, near - t * w), P(l1 + w, far), P(l1 + t * w, far) }, b, P(l1 + w, near - t * w), D(-sin, -cos)));
                    steps.Add(Winder(n + 5, new[] { P(b.U, b.V), P(l1 + t * w, far), P(l1, far) }, b, P(l1 + t * w, far), D(-cos, -sin)));
                    Flight(l1, -w - well, -1, 0, n + 6, c2, w);

                    newels.Add(Newel(a.U, a.V, post, post, Height(n + 2)));
                    newels.Add(Newel(b.U, b.V, post, -post, Height(n + 6)));
                    walks.Add(new[] { P(0, 0) }.Concat(Arc(a.U, a.V, 90, 0)).Concat(Arc(b.U, b.V, 0, -90)).Append(P(l1 - l2, -w - well)).ToList());

                    outer = new List<StairSidePoint>
                    {
                        S(0, w / 2, h1), S(l1, w / 2, Height(n)), S(l1 + t * w, w / 2, Height(n + 1)), S(l1 + w, w / 2, Height(n + 1) + rise / 2),
                        S(l1 + w, -w / 2 + t * w, Height(n + 2)), S(l1 + w, near, Height(n + 3)), S(l1 + w, near - t * w, Height(n + 4)),
                        S(l1 + w, far, Height(n + 4) + rise / 2), S(l1 + t * w, far, Height(n + 5)), S(l1, far, Height(n + 6)), S(l1 - l2, far, top)
                    };
                    inner = new List<StairSidePoint>
                    {
                        S(0, -w / 2, h1), S(l1, -w / 2, Height(n)), S(l1, -w / 2, Height(n + 2)), S(l1, near, Height(n + 2)),
                        S(l1, near, Height(n + 6)), S(l1 - l2, near, top)
                    };
                }
                else
                {
                    steps.Add(Landing(n, l1, l1 + w, far, w / 2, -w / 2, w / 2));
                    Flight(l1, -w - well, -1, 0, n + 1, c2, w);
                    walks.Add(new[] { P(0, 0), P(l1 + w / 2, 0), P(l1 + w / 2, -w - well), P(l1 - l2, -w - well) });
                    outer = new List<StairSidePoint>
                    {
                        S(0, w / 2, h1), S(l1, w / 2, Height(n)), S(l1 + w, w / 2, Height(n)), S(l1 + w, far, Height(n)), S(l1, far, Height(n)),
                        S(l1, far, Height(n + 1)), S(l1 - l2, far, top)
                    };
                    inner = new List<StairSidePoint>
                    {
                        S(0, -w / 2, h1), S(l1, -w / 2, Height(n)), S(l1, near, Height(n)), S(l1, near, Height(n + 1)), S(l1 - l2, near, top)
                    };
                }

                arrivals.Add((P(l1 - l2, near), P(l1 - l2, far)));

                // The well, a little into each flight so it joins them.
                if (well > 1)
                    wellFrom = from =>
                    {
                        var start = Math.Max(Math.Max(0, l1 - l2), from);
                        return start >= l1 - 1 ? null : new[] { P(start, -w / 2 + 1), P(l1, -w / 2 + 1), P(l1, near - 1), P(start, near - 1) };
                    };
                break;
            }

            case StairShape.Bifurcated:
            {
                // One wide flight up to a landing; from either end of the landing a flight the
                // stair's width goes on up, one each way.
                var n = beforeTurn;
                var half = main / 2;
                steps.Add(Landing(n, l1, l1 + w, -half, half, -half, half));
                Flight(l1 + w / 2, -half, 0, -1, n + 1, c2, w);
                Flight(l1 + w / 2, half, 0, 1, n + 1, c2, w);

                walks.Add(new[] { P(0, 0), P(l1 + w / 2, 0), P(l1 + w / 2, -half - l2) });
                walks.Add(new[] { P(l1 + w / 2, 0), P(l1 + w / 2, half + l2) });

                outer = new List<StairSidePoint> { S(0, half, h1), S(l1, half, Height(n)), S(l1, half, Height(n + 1)), S(l1, half + l2, top) };
                inner = new List<StairSidePoint> { S(0, -half, h1), S(l1, -half, Height(n)), S(l1, -half, Height(n + 1)), S(l1, -half - l2, top) };
                back = new List<StairSidePoint>
                {
                    S(l1 + w, -half - l2, top), S(l1 + w, -half, Height(n + 1)), S(l1 + w, -half, Height(n)),
                    S(l1 + w, half, Height(n)), S(l1 + w, half, Height(n + 1)), S(l1 + w, half + l2, top)
                };
                arrivals.Add((P(l1, -half - l2), P(l1 + w, -half - l2)));
                arrivals.Add((P(l1, half + l2), P(l1 + w, half + l2)));
                break;
            }

            default:
                walks.Add(new[] { P(0, 0), P(l1, 0) });
                outer = new List<StairSidePoint> { S(0, w / 2, h1), S(l1, w / 2, top) };
                inner = new List<StairSidePoint> { S(0, -w / 2, h1), S(l1, -w / 2, top) };
                arrivals.Add((P(l1, -w / 2), P(l1, w / 2)));
                break;
        }

        if (winders && shape is StairShape.QuarterTurn or StairShape.HalfTurn)
        {
            narrowEnd = post * t;
            winderGoing = w / 2 * Math.PI / 6;
            goingAt = fromNarrow => fromNarrow * Math.PI / 6;
        }

        Bullnose(steps, stair.BottomStep);

        // Turning right, the outer side is on the left going up; turning left, mirrored, on the right.
        var (leftSide, rightSide) = mirror < 0 ? (inner, outer) : (outer, inner);
        var sides = new List<StairSide> { new(leftSide, StairSideRole.Left, true), new(rightSide, StairSideRole.Right, false) };
        if (back is not null) sides.Add(new StairSide(back, StairSideRole.Other, false));

        return new StairLayout
        {
            Shape = ladder ? StairShape.Ladder : shape, Bottom = bottom, Top = top, Risers = risers, Rise = rise, Going = g, Width = w, WidestFlight = main,
            Steps = steps, Walks = walks, Sides = sides, Newels = newels, Arrivals = arrivals,
            WinderNarrowEnd = narrowEnd, WinderGoing = winderGoing, GoingAt = goingAt,
            Origin = stair.Start, Axis = up, WellFrom = wellFrom,
            TurnAt = shape == StairShape.Straight ? null : P(l1, 0), TurnRisers = shape == StairShape.Straight ? null : beforeTurn,
            WellAt = shape == StairShape.HalfTurn ? P(l1 - l2 / 2, -w - Math.Max(0, stair.Well)) : null,
            WellAxis = shape == StairShape.HalfTurn ? D(0, -1) : null
        };
    }

    /// <summary>
    /// A quarter or half turn on balanced winders: every tread the type's going along the middle
    /// of the stair, round the turn as much as up the flights, with no newel to fan them round.
    /// The inside of the turn is shorter than the middle by a quarter circle at each corner, and
    /// that is shared out among the treads either side - the most from those at the corner, less
    /// and less from those further off, up to four away - so none is narrower at its inside end
    /// than it need be. Each riser runs from its point up the inside, through its point on the
    /// line walked, on to the outside. None where there are too few treads either side of the
    /// turn to share it among.
    /// </summary>
    private static StairLayout? Balanced(Stair stair, double bottom, double top, int risers, double rise, double g, double w, double nosing)
    {
        var half = stair.Shape == StairShape.HalfTurn;
        var well = half ? Math.Max(0, stair.Well) : 0;
        var treads = risers - 1;
        double Height(int step) => bottom + step * rise;

        var up = stair.Up;
        var left = up.PerpendicularLeft();
        var mirror = stair.TurnsLeft ? -1.0 : 1.0;
        Point2D P((double U, double V) local) => stair.Start + up * local.U + left * (local.V * mirror);
        Vector2D D(double u, double v) => up * u + left * (v * mirror);

        // A quarter circle round each inside corner, along the middle, as twelve straight lengths.
        const int pieces = 12;
        var arc = pieces * w * Math.Sin(Math.PI / 4 / pieces);
        var turn = half ? 2 * arc + well : arc;
        var length = treads * g;

        // Its first corner in the middle of a tread: the one after the risers asked for, or as near halfway up as that comes.
        var before = stair.RisersBeforeTurn ?? (int)Math.Round(((length - turn) / 2 + arc / 2) / g + 0.5);
        var a1 = (before - 0.5) * g - arc / 2;
        var a2 = length - a1 - turn;
        if (a1 < 0 || a2 < 0) return null;

        // Laid out as if it turned right: the line walked, up the inside, up the outside.
        var (near, far) = (-w / 2 - well, -w / 2 - well - w);
        var walk = new List<(double U, double V)> { (0, 0) };
        void Round(double u, double v, double from)
        {
            for (var i = 0; i <= pieces; i++)
            {
                var angle = (from - 90.0 * i / pieces) * Math.PI / 180;
                walk.Add((u + w / 2 * Math.Cos(angle), v + w / 2 * Math.Sin(angle)));
            }
        }

        Round(a1, -w / 2, 90);
        List<(double U, double V)> inside, outside;
        if (half)
        {
            Round(a1, near, 0);
            walk.Add((a1 - a2, -w - well));
            inside = new() { (0, -w / 2), (a1, -w / 2), (a1, near), (a1 - a2, near) };
            outside = new() { (0, w / 2), (a1 + w, w / 2), (a1 + w, far), (a1 - a2, far) };
        }
        else
        {
            walk.Add((a1 + w / 2, -w / 2 - a2));
            inside = new() { (0, -w / 2), (a1, -w / 2), (a1, -w / 2 - a2) };
            outside = new() { (0, w / 2), (a1 + w, w / 2), (a1 + w, -w / 2 - a2) };
        }

        static double[] Along(List<(double U, double V)> path)
        {
            var lengths = new double[path.Count];
            for (var i = 1; i < path.Count; i++)
                lengths[i] = lengths[i - 1] + Math.Sqrt(Math.Pow(path[i].U - path[i - 1].U, 2) + Math.Pow(path[i].V - path[i - 1].V, 2));
            return lengths;
        }

        static (double U, double V) At(List<(double U, double V)> path, double[] lengths, double s)
        {
            for (var i = 1; i < path.Count; i++)
            {
                if (s > lengths[i] + 1e-9 && i < path.Count - 1) continue;
                var piece = lengths[i] - lengths[i - 1];
                var t = piece < 1e-9 ? 0 : Math.Clamp((s - lengths[i - 1]) / piece, 0, 1);
                return (path[i - 1].U + (path[i].U - path[i - 1].U) * t, path[i - 1].V + (path[i].V - path[i - 1].V) * t);
            }

            return path[^1];
        }

        var (walkAlong, insideAlong, outsideAlong) = (Along(walk), Along(inside), Along(outside));

        // The taper shared out: the corner treads give most, those either side less and less - as
        // few either side as leave every tread 75 at its inside end, or as many as there are.
        var deficit = walkAlong[^1] - insideAlong[^1];
        var first = (int)Math.Floor((a1 + arc / 2) / g);
        var last = half ? (int)Math.Floor((a1 + arc + well + arc / 2) / g) : first;
        List<double>? goings = null;
        for (var ramp = 1; ramp <= Math.Min(first, treads - 1 - last); ramp++)
        {
            if ((first - ramp) * g > a1 + 1e-6 || (last + ramp + 1) * g < a1 + turn - 1e-6) continue;
            var reach = ramp;
            double Share(int j) =>
                j < first ? Math.Max(0, 1 - (first - j) / (reach + 1.0))
                : j > last ? Math.Max(0, 1 - (j - last) / (reach + 1.0))
                : 1;
            var shares = Enumerable.Range(0, treads).Select(Share).ToList();
            var each = deficit / shares.Sum();
            goings = shares.Select(share => g - each * share).ToList();
            if (goings.Min() >= 75) break;
        }

        if (goings is null || goings.Min() < 1) return null;

        // Each riser: up the inside so far, through the line walked, on to the outside.
        var risersAt = new List<((double U, double V) In, (double U, double V) Out, double InAt, double OutAt, (double U, double V) Walk)>();
        var inAt = 0.0;
        for (var k = 0; k <= treads; k++)
        {
            var (i, m) = (At(inside, insideAlong, inAt), At(walk, walkAlong, k * g));
            (double U, double V, double Reach, double At)? hit = null;
            for (var s = 0; s + 1 < outside.Count; s++)
            {
                var (a, b) = (outside[s], outside[s + 1]);
                var (du, dv, eu, ev, fu, fv) = (m.U - i.U, m.V - i.V, b.U - a.U, b.V - a.V, a.U - i.U, a.V - i.V);
                var det = du * ev - dv * eu;
                if (Math.Abs(det) < 1e-12) continue;
                var (reach, across) = ((fu * ev - fv * eu) / det, (fu * dv - fv * du) / det);
                if (across < -1e-9 || across > 1 + 1e-9 || reach < 1 - 1e-9 || (hit is { } best && reach >= best.Reach)) continue;
                hit = (a.U + eu * across, a.V + ev * across, reach, outsideAlong[s] + across * (outsideAlong[s + 1] - outsideAlong[s]));
            }

            if (hit is not { } o) return null;
            risersAt.Add((i, (o.U, o.V), inAt, o.At, m));
            if (k < treads) inAt += goings[k];
        }

        if (risersAt.Zip(risersAt.Skip(1)).Any(pair => pair.Second.OutAt <= pair.First.OutAt)) return null;

        // The treads between, each round any corner it takes in; straight ones with their nosings.
        var steps = new List<StairStep>();
        for (var k = 0; k < treads; k++)
        {
            var (from, to) = (risersAt[k], risersAt[k + 1]);
            var tapered = Math.Abs(goings[k] - g) > 0.5;
            var (ru, rv) = (from.Out.U - from.In.U, from.Out.V - from.In.V);
            var span = Math.Sqrt(ru * ru + rv * rv);
            var (nu, nv) = (rv / span, -ru / span);
            if (nu * (to.Walk.U - from.Walk.U) + nv * (to.Walk.V - from.Walk.V) < 0) (nu, nv) = (-nu, -nv);
            var back = tapered ? 0 : nosing;

            var outline = new List<Point2D> { P((from.In.U - nu * back, from.In.V - nv * back)) };
            outline.AddRange(Enumerable.Range(0, inside.Count).Where(i => insideAlong[i] > from.InAt + 1e-6 && insideAlong[i] < to.InAt - 1e-6).Select(i => P(inside[i])));
            outline.Add(P(to.In));
            outline.Add(P(to.Out));
            outline.AddRange(Enumerable.Range(0, outside.Count).Where(i => outsideAlong[i] > from.OutAt + 1e-6 && outsideAlong[i] < to.OutAt - 1e-6).Reverse().Select(i => P(outside[i])));
            outline.Add(P((from.Out.U - nu * back, from.Out.V - nv * back)));

            steps.Add(new StairStep(k + 1, tapered ? StairStepKind.Winder : StairStepKind.Tread, outline, Height(k + 1), P(from.In), P(from.Out), D(nu, nv)));
        }

        Bullnose(steps, stair.BottomStep);

        // Each side rising with the steps, through the corners it turns.
        List<StairSidePoint> Side(List<(double U, double V)> path, double[] lengths, Func<int, double> at)
        {
            var points = new List<StairSidePoint>();
            for (var k = 0; k <= treads; k++)
            {
                points.Add(new StairSidePoint(P(At(path, lengths, at(k))), Height(k + 1)));
                if (k == treads) break;
                var (s0, s1) = (at(k), at(k + 1));
                points.AddRange(Enumerable.Range(0, path.Count).Where(i => lengths[i] > s0 + 1e-6 && lengths[i] < s1 - 1e-6)
                    .Select(i => new StairSidePoint(P(path[i]), Height(k + 1) + (lengths[i] - s0) / (s1 - s0) * rise)));
            }

            return points;
        }

        var inner = Side(inside, insideAlong, k => risersAt[k].InAt);
        var outer = Side(outside, outsideAlong, k => risersAt[k].OutAt);
        var (leftSide, rightSide) = mirror < 0 ? (inner, outer) : (outer, inner);

        var narrow = goings.Min();
        Func<double, IReadOnlyList<Point2D>?>? wellFrom = null;
        if (half && well > 1)
            wellFrom = from =>
            {
                var start = Math.Max(Math.Max(0, a1 - a2), from);
                return start >= a1 - 1 ? null : new[] { P((start, -w / 2 + 1)), P((a1, -w / 2 + 1)), P((a1, near - 1)), P((start, near - 1)) };
            };

        return new StairLayout
        {
            Shape = stair.Shape, Bottom = bottom, Top = top, Risers = risers, Rise = rise, Going = g, Width = w, WidestFlight = w,
            Steps = steps, Walks = new[] { (IReadOnlyList<Point2D>)walk.Select(P).ToList() },
            Sides = new List<StairSide> { new(leftSide, StairSideRole.Left, true), new(rightSide, StairSideRole.Right, false) },
            Arrivals = new[] { (P(risersAt[^1].In), P(risersAt[^1].Out)) },
            WinderNarrowEnd = narrow, WinderGoing = g, GoingAt = fromNarrow => narrow + (g - narrow) * fromNarrow / (w / 2),
            Origin = stair.Start, Axis = up, WellFrom = wellFrom,
            TurnAt = P(At(walk, walkAlong, a1 + arc / 2)), TurnRisers = (int)Math.Round((a1 + arc / 2) / g + 0.5),
            WellAt = half ? P((a1 - a2 / 2, -w - well)) : null, WellAxis = half ? D(0, -1) : null
        };
    }

    /// <summary>
    /// A stair's bottom step given a bullnose, as it says: the end of its first tread rounded out
    /// past the side - a half circle as deep as the tread - at its left going up, its right, or both.
    /// </summary>
    private static void Bullnose(List<StairStep> steps, BottomStep bottom)
    {
        if (bottom == BottomStep.Plain) return;
        var index = steps.FindIndex(step => step.Number == 1 && step.Kind == StairStepKind.Tread && step.Outline.Count == 4);
        if (index < 0) return;

        // Its outline goes front, back, along the far side to the back, and to the front: its sides the first and third lengths.
        var step = steps[index];
        var o = step.Outline;
        var leftward = step.Up.PerpendicularLeft();
        var thirdIsLeft = (o[2].MidpointTo(o[3]) - o[0].MidpointTo(o[1])).Dot(leftward) > 0;
        bool Rounds(bool left) => bottom == BottomStep.BullnoseBoth || (left ? bottom == BottomStep.BullnoseLeft : bottom == BottomStep.BullnoseRight);

        IEnumerable<Point2D> Round(Point2D from, Point2D to, Vector2D outward)
        {
            var centre = from.MidpointTo(to);
            var radius = (to - from).Length / 2;
            return Enumerable.Range(1, 11).Select(i => i * Math.PI / 12).Select(angle => centre + (from - centre) * Math.Cos(angle) + outward * (radius * Math.Sin(angle)));
        }

        var outline = new List<Point2D> { o[0] };
        var firstSide = thirdIsLeft ? leftward * -1 : leftward;
        if (Rounds(!thirdIsLeft)) outline.AddRange(Round(o[0], o[1], firstSide));
        outline.AddRange(new[] { o[1], o[2] });
        if (Rounds(thirdIsLeft)) outline.AddRange(Round(o[2], o[3], firstSide * -1));
        outline.Add(o[3]);
        steps[index] = step with { Outline = outline };
    }

    /// <summary>
    /// Alternating treads: a steep straight flight of paddle treads, each half the stair's width,
    /// left and right in turn - a foot on each - a tread every half its going, so the going
    /// between alternate nosings, on the same side, is the type's. The last stops where the floor
    /// at its head begins.
    /// </summary>
    private static StairLayout Alternating(Stair stair, double bottom, double top, int risers, double rise, double g, double w, double nosing)
    {
        double Height(int step) => bottom + step * rise;
        var up = stair.Up;
        var left = up.PerpendicularLeft();
        Point2D P(double u, double v) => stair.Start + up * u + left * v;

        var (spacing, treads) = (g / 2, risers - 1);
        var head = treads * spacing;
        var steps = new List<StairStep>();
        for (var k = 1; k <= treads; k++)
        {
            var at = (k - 1) * spacing;
            var (from, to) = k % 2 == 1 ? (0.0, w / 2) : (-w / 2, 0.0);
            var back = Math.Min(at + g, head);
            steps.Add(new StairStep(k, StairStepKind.Tread, new[] { P(at - nosing, from), P(back, from), P(back, to), P(at - nosing, to) }, Height(k), P(at, from), P(at, to), up));
        }

        return new StairLayout
        {
            Shape = StairShape.AlternatingTread, Bottom = bottom, Top = top, Risers = risers, Rise = rise, Going = spacing, Width = w, WidestFlight = w,
            Steps = steps, Walks = new[] { (IReadOnlyList<Point2D>)new[] { P(0, 0), P(head, 0) } },
            Sides = new List<StairSide>
            {
                new(new[] { new StairSidePoint(P(0, w / 2), Height(1)), new StairSidePoint(P(head, w / 2), top) }, StairSideRole.Left, true),
                new(new[] { new StairSidePoint(P(0, -w / 2), Height(1)), new StairSidePoint(P(head, -w / 2), top) }, StairSideRole.Right, false)
            },
            Arrivals = new[] { (P(head, -w / 2), P(head, w / 2)) },
            Origin = stair.Start, Axis = up
        };
    }

    /// <summary>
    /// A stair laid out along a walking line - drawn by clicking it, or a three-quarter turn's -
    /// a flight along each stretch of it, and at each turn a landing as wide as the stair round
    /// the corner, or at a right-angled turn on winders, three winders round a newel. The treads
    /// are shared among the flights as the stretches were long, so it turns about where it was
    /// clicked; its head is where its treads end. Turns are up to a right angle either way, and
    /// none - a straight on - is a landing part way up a straight run.
    /// </summary>
    private static StairLayout Path(Stair stair, StairType type, double bottom, double top, int risers, double rise, double g, double w, double nosing)
    {
        double Height(int step) => bottom + step * rise;
        var mirror = stair.TurnsLeft ? -1.0 : 1.0;

        // Its stretches and turns: as drawn, or a three-quarter turn's three equal flights.
        List<double> legs, turns;
        if (stair.Shape == StairShape.ThreeQuarterTurn)
            (legs, turns) = (new List<double> { w / 2 + 1000, w + 1000, w / 2 + 1000 }, new List<double> { 90, 90 });
        else
        {
            legs = stair.PathLengths.Select(length => double.IsFinite(length) ? Math.Max(0, length) : 0).ToList();
            if (legs.Count == 0) legs.Add(1000);
            turns = stair.PathTurns.Take(legs.Count - 1).Select(turn => double.IsFinite(turn) ? Math.Clamp(turn, -90, 90) : 0).ToList();
            while (turns.Count < legs.Count - 1) turns.Add(0);
        }

        // At each turn a landing, a step of its own - or three winders at a right angle on winders.
        var winding = turns.Select(turn => stair.Corner != StairCorner.Landing && Math.Abs(Math.Abs(turn) - 90) < 0.5).ToList();
        var treads = risers - 1 - winding.Sum(winds => winds ? 3 : 1);
        if (treads < 0) (legs, turns, winding, treads) = (new List<double> { legs.Sum() }, new List<double>(), new List<bool>(), risers - 1);

        // The treads shared among the flights as their stretches were long, less half a landing at each end that turns.
        var k = legs.Count;
        var room = Enumerable.Range(0, k).Select(j => Math.Max(0, legs[j] - (j > 0 ? w / 2 : 0) - (j < k - 1 ? w / 2 : 0)) + 1e-6).ToList();
        var share = room.Select(r => treads * r / room.Sum()).ToList();
        var counts = share.Select(s => (int)Math.Floor(s)).ToList();
        foreach (var j in Enumerable.Range(0, k).OrderByDescending(j => share[j] - counts[j]).Take(treads - counts.Sum())) counts[j]++;

        static Vector2D Turned(Vector2D way, double radians) =>
            new(way.X * Math.Cos(radians) - way.Y * Math.Sin(radians), way.X * Math.Sin(radians) + way.Y * Math.Cos(radians));
        static Point2D Meet(Point2D p, Vector2D d, Point2D q, Vector2D e) => p + d * ((q - p).Cross(e) / d.Cross(e));

        var steps = new List<StairStep>();
        var walk = new List<Point2D> { stair.Start };
        var corners = new List<Point2D> { stair.Start };
        var newels = new List<StairNewel>();
        var (pos, dir) = (stair.Start, stair.Up);
        var left = new List<StairSidePoint> { new(pos + dir.PerpendicularLeft() * (w / 2), Height(1)) };
        var right = new List<StairSidePoint> { new(pos - dir.PerpendicularLeft() * (w / 2), Height(1)) };
        double? narrowEnd = null, winderGoing = null;
        Func<double, double>? goingAt = null;
        (Point2D From, Point2D To) arrival = default;
        Point2D? firstTurn = null;
        var number = 1;
        var t = Math.Tan(Math.PI / 6);
        var (cos, sin) = (Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6));

        for (var j = 0; j < k; j++)
        {
            var across = dir.PerpendicularLeft();
            for (var i = 0; i < counts[j]; i++)
            {
                var (a0, a1) = (i * g - nosing, (i + 1) * g);
                var outline = new[] { pos + dir * a0 - across * (w / 2), pos + dir * a1 - across * (w / 2), pos + dir * a1 + across * (w / 2), pos + dir * a0 + across * (w / 2) };
                steps.Add(new StairStep(number + i, StairStepKind.Tread, outline, Height(number + i), pos + dir * (i * g) - across * (w / 2), pos + dir * (i * g) + across * (w / 2), dir));
            }

            var end = pos + dir * (counts[j] * g);
            number += counts[j];
            if (j == k - 1)
            {
                walk.Add(end);
                corners.Add(end);
                left.Add(new StairSidePoint(end + across * (w / 2), top));
                right.Add(new StairSidePoint(end - across * (w / 2), top));
                arrival = (end - across * (w / 2), end + across * (w / 2));
                break;
            }

            // The turn: right for a positive one, left for a negative - mirrored for a stair that turns left.
            var turn = turns[j] * mirror * Math.PI / 180;
            var next = Turned(dir, -turn);
            var vertex = end + dir * (w / 2);
            var start = vertex + next * (w / 2);
            corners.Add(vertex);
            firstTurn ??= end;
            var m = number;

            if (winding[j])
            {
                // Three winders round a newel at the inside corner, as a quarter turn's.
                var s = turn > 0 ? 1.0 : -1.0;
                Point2D L(double u, double v) => end + dir * u + across * (v * s);
                Vector2D D(double u, double v) => dir * u + across * (v * s);
                var c = L(0, -w / 2);
                steps.Add(new StairStep(m, StairStepKind.Winder, new[] { c, L(0, w / 2), L(t * w, w / 2) }, Height(m), c, L(0, w / 2), D(1, 0)));
                steps.Add(new StairStep(m + 1, StairStepKind.Winder, new[] { c, L(t * w, w / 2), L(w, w / 2), L(w, -w / 2 + t * w) }, Height(m + 1), c, L(t * w, w / 2), D(cos, -sin)));
                steps.Add(new StairStep(m + 2, StairStepKind.Winder, new[] { c, L(w, -w / 2 + t * w), L(w, -w / 2) }, Height(m + 2), c, L(w, -w / 2 + t * w), D(sin, -cos)));
                newels.Add(new StairNewel(new[] { L(0, -w / 2), L(100, -w / 2), L(100, -w / 2 + 100), L(0, -w / 2 + 100) }, Height(m + 3) + type.HandrailHeight + 100));
                walk.AddRange(Enumerable.Range(0, 7).Select(q => (90 - 15.0 * q) * Math.PI / 180).Select(a => L(w / 2 * Math.Cos(a), -w / 2 + w / 2 * Math.Sin(a))));

                // The outside rises steadily round the corner, the inside steps up at the newel.
                var outside = new[]
                {
                    new StairSidePoint(L(0, w / 2), Height(m)), new StairSidePoint(L(t * w, w / 2), Height(m + 1)),
                    new StairSidePoint(L(w, w / 2), Height(m + 1) + rise / 2), new StairSidePoint(L(w, -w / 2 + t * w), Height(m + 2)),
                    new StairSidePoint(L(w, -w / 2), Height(m + 3))
                };
                var inside = new[] { new StairSidePoint(c, Height(m)), new StairSidePoint(c, Height(m + 3)) };
                (s > 0 ? left : right).AddRange(outside);
                (s > 0 ? right : left).AddRange(inside);
                (narrowEnd, winderGoing) = (100 * t, w / 2 * Math.PI / 6);
                goingAt = fromNarrow => fromNarrow * Math.PI / 6;
                number = m + 3;
            }
            else
            {
                // A landing as wide as the stair, round the outside of the corner.
                var nextAcross = next.PerpendicularLeft();
                var (fl, fr, sl, sr) = (end + across * (w / 2), end - across * (w / 2), start + nextAcross * (w / 2), start - nextAcross * (w / 2));
                Point2D? corner = Math.Abs(turn) < 1e-6 ? null : turn > 0 ? Meet(fl, dir, sl, next) : Meet(fr, dir, sr, next);
                var outline = new List<Point2D> { fl };
                if (turn > 0 && corner is { } outerLeft) outline.Add(outerLeft);
                outline.AddRange(new[] { sl, sr });
                if (turn < 0 && corner is { } outerRight) outline.Add(outerRight);
                outline.Add(fr);
                outline = outline.Where((point, q) => (point - outline[(q + outline.Count - 1) % outline.Count]).Length > 0.5).ToList();
                steps.Add(new StairStep(m, StairStepKind.Landing, outline, Height(m), fr, fl, dir));

                left.Add(new StairSidePoint(fl, Height(m)));
                if (turn > 0 && corner is { } l) left.Add(new StairSidePoint(l, Height(m)));
                left.AddRange(new[] { new StairSidePoint(sl, Height(m)), new StairSidePoint(sl, Height(m + 1)) });
                right.Add(new StairSidePoint(fr, Height(m)));
                if (turn < 0 && corner is { } r) right.Add(new StairSidePoint(r, Height(m)));
                right.AddRange(new[] { new StairSidePoint(sr, Height(m)), new StairSidePoint(sr, Height(m + 1)) });

                walk.AddRange(new[] { end, vertex, start });
                number = m + 1;
            }

            (pos, dir) = (start, next);
        }

        Bullnose(steps, stair.BottomStep);
        return new StairLayout
        {
            Shape = stair.Shape, Bottom = bottom, Top = top, Risers = risers, Rise = rise, Going = g, Width = w, WidestFlight = w,
            Steps = steps, Walks = new[] { (IReadOnlyList<Point2D>)walk.Where((point, q) => q == 0 || (point - walk[q - 1]).Length > 0.5).ToList() },
            Sides = new List<StairSide> { new(left, StairSideRole.Left, true), new(right, StairSideRole.Right, false) },
            Newels = newels, Arrivals = new[] { arrival },
            WinderNarrowEnd = narrowEnd, WinderGoing = winderGoing, GoingAt = goingAt,
            Origin = stair.Start, Axis = stair.Up, TurnAt = firstTurn, PathPoints = corners
        };
    }

    /// <summary>
    /// A spiral stair round a central column, or a curved one round an open well: its treads
    /// fanned round a centre - to its right going up, or its left for one turning left - each
    /// turning as far as makes its going along the middle of the stair the type's; its sides
    /// helices rising with it. The column of a spiral stands from its foot to a handrail's height
    /// above its head.
    /// </summary>
    private static StairLayout Circular(Stair stair, StairType type, double bottom, double top, int risers, double rise, double g, double w, double nosing)
    {
        var spiral = stair.Shape == StairShape.Spiral;
        var inner = stair.InnerRadius > 0 ? stair.InnerRadius : spiral ? Stairs.SpiralColumnRadius : Stairs.CurvedInnerRadius;
        var outer = inner + w;
        var middle = inner + w / 2;
        var turnEach = g / middle;
        double Height(int step) => bottom + step * rise;

        var up = stair.Up;
        var left = up.PerpendicularLeft();
        var mirror = stair.TurnsLeft ? -1.0 : 1.0;
        Point2D P(double u, double v) => stair.Start + up * u + left * (v * mirror);
        Vector2D D(double u, double v) => up * u + left * (v * mirror);

        // So far round its centre, at a radius; and which way it is going there.
        Point2D Q(double radius, double angle) => P(radius * Math.Sin(angle), -middle + radius * Math.Cos(angle));
        Vector2D Along(double angle) => D(Math.Cos(angle), -Math.Sin(angle));
        IEnumerable<double> Between(double from, double to, int pieces) => Enumerable.Range(0, pieces + 1).Select(i => from + (to - from) * i / pieces);
        int Pieces(double angle, double degrees) => Math.Max(2, (int)Math.Ceiling(Math.Abs(angle) * 180 / Math.PI / degrees));

        var steps = new List<StairStep>();
        var nose = nosing / middle;
        var lap = 1 / middle;
        for (var k = 1; k < risers; k++)
        {
            // Each a little over the next, so they make one outline; the last stopping where the floor starts.
            var (from, to) = ((k - 1) * turnEach, k * turnEach + (k == risers - 1 ? 0 : lap));
            var outline = Between(from - nose, to, Pieces(turnEach, 5)).Select(angle => Q(outer, angle))
                .Concat(Between(to, from - nose, Pieces(turnEach, 5)).Select(angle => Q(inner, angle)))
                .ToList();
            steps.Add(new StairStep(k, StairStepKind.Tread, outline, Height(k), Q(inner, from), Q(outer, from), Along(from)));
        }

        var turn = (risers - 1) * turnEach;
        var walk = Between(0, turn, Pieces(turn, 5)).Select(angle => Q(middle, angle)).ToList();

        // Its sides rise with it, through the nosings: a helix each side.
        var angles = Between(0, turn, Pieces(turn, 6)).ToList();
        StairSidePoint On(double radius, double angle) => new(Q(radius, angle), Height(1) + angle / turnEach * rise);
        var outerSide = angles.Select(angle => On(outer, angle)).ToList();
        var innerSide = angles.Select(angle => On(inner, angle)).ToList();
        var (leftSide, rightSide) = mirror < 0 ? (innerSide, outerSide) : (outerSide, innerSide);
        var sides = new List<StairSide>
        {
            new(leftSide, StairSideRole.Left, true, AgainstPole: spiral && mirror < 0),
            new(rightSide, StairSideRole.Right, false, AgainstPole: spiral && mirror > 0)
        };

        var newels = new List<StairNewel>();
        if (spiral)
        {
            var centre = P(0, -middle);
            var column = Enumerable.Range(0, 24).Select(i => 2 * Math.PI * i / 24)
                .Select(angle => new Point2D(centre.X + inner * Math.Cos(angle), centre.Y + inner * Math.Sin(angle))).ToList();
            newels.Add(new StairNewel(column, top + type.HandrailHeight));
        }

        // A spiral's landing at its head: a quarter turn of platform level with the floor it reaches,
        // stepped off across its outside edge - where the guarding round the stairwell is left open -
        // and guarded along its far edge, where it drops to the stair below.
        var arrivals = new List<(Point2D From, Point2D To)> { (Q(inner, turn), Q(outer, turn)) };
        if (spiral)
        {
            var end = turn + Math.PI / 2;
            var edge = Between(turn, end, Pieces(Math.PI / 2, 5)).Select(angle => Q(outer, angle)).ToList();
            var platform = edge.Concat(Between(end, turn, Pieces(Math.PI / 2, 5)).Select(angle => Q(inner, angle))).ToList();
            steps.Add(new StairStep(risers, StairStepKind.Landing, platform, top, Q(inner, turn), Q(outer, turn), Along(turn)));
            arrivals.AddRange(edge.Zip(edge.Skip(1)));

            var (from, to) = (Q(inner, end), Q(outer, end));
            var onRight = (to - from).Cross(Q(middle, end - 0.2) - from) < 0;
            sides.Add(new StairSide(new[] { new StairSidePoint(from, top), new StairSidePoint(to, top) }, StairSideRole.Other, onRight));
        }

        return new StairLayout
        {
            Shape = stair.Shape, Bottom = bottom, Top = top, Risers = risers, Rise = rise, Going = g, Width = w, WidestFlight = w,
            Steps = steps, Walks = new[] { (IReadOnlyList<Point2D>)walk }, Sides = sides, Newels = newels,
            Arrivals = arrivals,
            GoingAt = fromNarrow => turnEach * (inner + fromNarrow),
            InnerRadius = inner, OuterRadius = outer, TreadsPerTurn = 2 * Math.PI / turnEach, Centre = P(0, -middle),
            Origin = stair.Start, Axis = up
        };
    }
}

/// <summary>
/// Stairs in the model: how high they go - floor to floor - how they lay out, the openings they
/// cut in the floors over them, and whether they meet Approved Document K (England's stairs) -
/// with BS 5395-2, which it points to for spiral stairs - or the IRC's R311.7, whichever the
/// project is checked against.
/// </summary>
public static class Stairs
{
    private static readonly StairType Fallback = StairType.Library()[0];

    /// <summary>A spiral stair's column, unless it is told otherwise: 120 across, its treads fixed round it.</summary>
    public const double SpiralColumnRadius = 60;

    /// <summary>How far a curved stair's inside edge is from its centre, unless it is told otherwise, mm.</summary>
    public const double CurvedInnerRadius = 1500;

    public static StairType TypeOf(BimDocument document, Stair stair) => document.FindType<StairType>(stair.TypeId) ?? Fallback;

    /// <summary>How far above or below its level a floor, deck or terrace may be and still be the one a stair stands on or comes up to there, mm.</summary>
    public const double FloorReach = 600;

    /// <summary>The least a stair rises from its foot to its head, mm: its height, unconnected, can be no less.</summary>
    public const double MinimumRise = 300;

    public static StairLayout Layout(BimDocument document, Stair stair) =>
        LaidOut(document, stair, TypeOf(document, stair), stair.LevelId, stair.TopLevelId);

    /// <summary>
    /// A stair laid out from one level to another. Its foot is on whatever it stands on in front
    /// of its first riser - a floor, a patio, a deck; its head on whatever it comes up onto,
    /// measured where it is stepped off: a stair outside comes up to the balcony, deck or roof
    /// terrace it reaches, set down below the storey's floor or raised above it as that is.
    /// Where nothing is there, the floor over its foot, or the level. With no level to go up to,
    /// its own height above its foot.
    /// </summary>
    private static StairLayout LaidOut(BimDocument document, Stair stair, StairType type, Guid baseLevelId, Guid? topLevelId)
    {
        // In front of its first riser: a floor, a patio, a deck - or, outside, the ground, as it slopes there.
        var approach = stair.Start - stair.Up * 150;
        var bottom = (SurfaceAt(document, baseLevelId, approach)
                      ?? Terrains.GroundNear(document, document.FindLevel(baseLevelId)?.Elevation ?? 0, approach)
                      ?? FloorTop(document, baseLevelId, stair.Start)) + stair.BaseOffset;
        if (topLevelId is not { } topId || document.FindLevel(topId) is null)
            return StairLayout.Of(stair, type, bottom, bottom + stair.UnconnectedHeight);

        // Laid out to the floor over its foot first; then to what it comes up onto, again if that moves where it does.
        var layout = StairLayout.Of(stair, type, bottom, FloorTop(document, topId, stair.Start) + stair.TopOffset);
        for (var pass = 0; pass < 2; pass++)
        {
            if (SteppedOff(layout) is not { } off) break;
            if (SurfaceAt(document, topId, off) is not { } surface)
            {
                // Coming up short of a deck higher than it reckoned, or past one lower: laid out to that deck instead, where it then reaches it.
                if (Nearest(document, topId, off) is not { } near || Math.Abs(near.Top + stair.TopOffset - layout.Top) < 0.5) break;
                var tried = StairLayout.Of(stair, type, bottom, near.Top + stair.TopOffset);
                if (SteppedOff(tried) is { } reached && SurfaceAt(document, topId, reached) is { } there && Math.Abs(there - near.Top) < 0.5) layout = tried;
                break;
            }

            var top = surface + stair.TopOffset;
            if (Math.Abs(top - layout.Top) < 0.5) break;
            layout = StairLayout.Of(stair, type, bottom, top);
        }

        return layout;
    }

    /// <summary>
    /// The floor or flat roof stood on at a level nearest a point, within 1500 of it: how far off
    /// it is, and its top where it is nearest.
    /// </summary>
    private static (double Gap, double Top)? Nearest(BimDocument document, Guid levelId, Point2D at)
    {
        var level = document.FindLevel(levelId)?.Elevation ?? 0;
        (double Gap, double Top)? nearest = null;
        foreach (var slab in document.Elements.OfType<Slab>())
        {
            if (slab.Boundary.Count < 3 || !FloorSurfaces.IsStoodOn(document, slab)) continue;
            var boundary = slab.Boundary;
            var closest = Enumerable.Range(0, boundary.Count).Select(i => Closest(boundary[i], boundary[(i + 1) % boundary.Count])).MinBy(point => point.DistanceTo(at));
            var gap = closest.DistanceTo(at);
            var top = FloorSurfaces.TopAt(document, slab, closest);
            if (gap <= 1500 && Math.Abs(top - level) <= FloorReach && (nearest is null || gap < nearest.Value.Gap)) nearest = (gap, top);
        }

        return nearest;

        Point2D Closest(Point2D a, Point2D b)
        {
            var ab = b - a;
            var length = ab.Dot(ab);
            return length <= 0 ? a : a + ab * Math.Clamp((at - a).Dot(ab) / length, 0, 1);
        }
    }

    /// <summary>The top of the floor at a level at a point: whatever is stood on there near the level, or the level itself.</summary>
    private static double FloorTop(BimDocument document, Guid levelId, Point2D at) =>
        SurfaceAt(document, levelId, at) ?? document.FindLevel(levelId)?.Elevation ?? 0;

    /// <summary>
    /// The top of whatever floor or flat roof is stood on at a point within <see cref="FloorReach"/>
    /// of a level - the highest, where one lies on another; null where there is none.
    /// </summary>
    private static double? SurfaceAt(BimDocument document, Guid levelId, Point2D at) => StoodOnAt(document, levelId, at)?.Top;

    /// <summary>The floor or flat roof stood on at a point within <see cref="FloorReach"/> of a level, and its top there - the highest, where one lies on another.</summary>
    private static (Slab Slab, double Top)? StoodOnAt(BimDocument document, Guid levelId, Point2D at) =>
        FloorSurfaces.StoodOnNear(document, document.FindLevel(levelId)?.Elevation ?? 0, at, FloorReach);

    /// <summary>
    /// How far a stair coming up from outside a floor, balcony or deck runs in past its edge - its
    /// foot not under it, its head cutting into it - and which way to set it back; none where it
    /// comes up at the edge, or up through the floor as a stair inside does.
    /// </summary>
    public static (double By, Vector2D Back)? Overrun(BimDocument document, Stair stair, StairLayout layout, Guid? topLevelId)
    {
        if (topLevelId is not { } topLevel || layout.Arrivals.Count == 0 || SteppedOff(layout) is not { } off || StoodOnAt(document, topLevel, off) is not { } arrived) return null;
        var first = layout.Steps.OrderBy(step => step.Number).FirstOrDefault();
        if (first is null || first.Outline.Any(point => arrived.Slab.Contains(point))) return null;
        if (Openings(document, stair, arrived.Slab, layout).Count == 0) return null;

        // From where it is stepped off - a spiral's landing's outside edge - back the way it came up.
        if (HeadOf(layout) is not { } head) return null;
        var boundary = arrived.Slab.Boundary;
        var by = Enumerable.Range(0, boundary.Count).Min(i => Line2D.DistanceFromSegment(head.At, boundary[i], boundary[(i + 1) % boundary.Count]));
        return by < 20 ? null : (by, head.Up * -1);
    }

    /// <summary>
    /// What is said of its left going up said of its right, and the other way: its handrail on one
    /// side, its bullnose - as a mirror image, or a straight stair turned to go up the other way,
    /// keeps them where they were.
    /// </summary>
    public static void SwapSides(Stair stair)
    {
        stair.Railings = stair.Railings switch { StairRailings.LeftSide => StairRailings.RightSide, StairRailings.RightSide => StairRailings.LeftSide, var same => same };
        stair.BottomStep = stair.BottomStep switch { BottomStep.BullnoseLeft => BottomStep.BullnoseRight, BottomStep.BullnoseRight => BottomStep.BullnoseLeft, var same => same };
    }

    /// <summary>
    /// Whether a stair stands outside the building: there are walls on its level, and the middle
    /// of its first step is not within them.
    /// </summary>
    public static bool StandsOutside(BimDocument document, Stair stair)
    {
        if (!document.Walls.Any(wall => wall.LevelId == stair.LevelId)) return false;
        var first = Layout(document, stair).Steps.OrderBy(step => step.Number).FirstOrDefault();
        var at = first is null ? stair.Start : Polygon2D.Centroid(first.Outline, stair.Start);
        return !RoomBoundary.Trace(document, stair.LevelId, at).IsEnclosed;
    }

    /// <summary>Where a stair is stepped off at its head: just past the middle of where it comes up, away from its steps.</summary>
    public static Point2D? SteppedOff(StairLayout layout) =>
        layout.Arrivals.Count == 0 ? null
        : layout.Shape == StairShape.Spiral && HeadOf(layout) is { } head ? head.At + head.Up * 150
        : layout.Arrivals[0].From.MidpointTo(layout.Arrivals[0].To) + Away(layout, layout.Arrivals[0]) * 150;

    /// <summary>The way off a stair at an edge where it comes up: square to it, away from its steps.</summary>
    private static Vector2D Away(StairLayout layout, (Point2D From, Point2D To) arrival)
    {
        var (from, to) = arrival;
        var away = (to - from).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var middle = from.MidpointTo(to);
        return layout.Steps.Any(step => Polygon2D.Contains(step.Outline, middle + away * 20)) ? away * -1 : away;
    }

    /// <summary>The rules a stair is checked against, by name.</summary>
    public static string Label(ClearanceStandard standard) =>
        standard.IsUs() ? "IRC R311.7" : "Approved Document K";

    /// <summary>The clear height over a stair its rules ask for, mm: 2000, or 6 ft 8 in - 6 ft 6 in over a spiral stair.</summary>
    public static double Headroom(ClearanceStandard standard, StairShape shape = StairShape.Straight) =>
        !standard.IsUs() ? 2000 : shape == StairShape.Spiral ? 1982 : 2032;

    /// <summary>
    /// The holes stairs cut in a slab over them: over each the steps that would not have the
    /// headroom under it - which, steps rising as they go, is its top end - as one outline, or
    /// one over each flight of a split stair whose landing is low enough to keep its floor.
    /// The floor a stair stands on, and those well above it, are not cut.
    /// </summary>
    public static IEnumerable<IReadOnlyList<Point2D>> Holes(BimDocument document, Slab slab)
    {
        foreach (var stair in document.Elements.OfType<Stair>())
        foreach (var flight in Flights(document, stair))
        foreach (var hole in Openings(document, stair, slab, flight.Layout))
            yield return hole;
    }

    /// <summary>
    /// A stair storey by storey: from the level it stands on to the one it reaches, and - for one
    /// repeated up a stairwell - from each to the next as far as the level it repeats to, each
    /// laid out from that storey's floor to the next.
    /// </summary>
    public static IReadOnlyList<StairFlight> Flights(BimDocument document, Stair stair)
    {
        var flights = new List<StairFlight> { new(stair.LevelId, stair.TopLevelId, Layout(document, stair)) };
        if (stair.RepeatToLevelId is not { } last || stair.TopLevelId is not { } from || document.FindLevel(last) is not { } highest) return flights;

        var type = TypeOf(document, stair);
        while (flights.Count < 60 && document.FindLevel(from) is { } level && level.Elevation < highest.Elevation - 1 && Levels.Above(document, from) is { } next)
        {
            flights.Add(new StairFlight(from, next.Id, LaidOut(document, stair, type, from, next.Id)));
            from = next.Id;
        }

        return flights;
    }

    /// <summary>
    /// Whether a slab is one a stair passes through rather than stops under: a floor, a ceiling,
    /// or a flat roof that is a floor - a deck the storey above stands on. A pitched roof is not cut.
    /// </summary>
    public static bool IsFloorLike(BimDocument document, Slab slab) =>
        slab is Floor or Ceiling || slab is Roof && FloorSurfaces.IsStoodOn(document, slab);

    /// <summary>The holes one stair cuts in one slab over it: usually one, none where it passes clear.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Openings(BimDocument document, Stair stair, Slab slab, StairLayout layout)
    {
        if (slab.Boundary.Count < 3) return Array.Empty<IReadOnlyList<Point2D>>();

        // A loft conversion's stair cuts as far as 1900 over the middle of it asks.
        var headroom = stair.LoftConversion && !document.ProjectInformation.ClearanceStandard.IsUs()
            ? 1900 : Headroom(document.ProjectInformation.ClearanceStandard, layout.Shape);

        // A pitched roof it comes up into: cut over each step it comes down into the headroom of, under the eaves or in an attic.
        // Not a roof by extrusion - a vault - which is not cut for anything: its headroom is said instead.
        if (slab is Roof { IsExtrusion: false } pitched && !IsFloorLike(document, pitched))
            return stair.CutsRoof ? RoofOpenings(document, pitched, layout, headroom) : Array.Empty<IReadOnlyList<Point2D>>();

        if (!stair.CutsFloorAbove || !IsFloorLike(document, slab)) return Array.Empty<IReadOnlyList<Point2D>>();
        var (underside, top) = (slab.GetBottomElevation(document), slab.GetTopElevation(document));
        if (top <= layout.Bottom + 1 || underside >= layout.Top + headroom) return Array.Empty<IReadOnlyList<Point2D>>();

        var steps = layout.Steps.Where(step => step.Top + headroom > underside).ToList();
        var opened = steps.Select(step => step.Outline).ToList();

        // A spiral goes up through a round hole its own size, its landing filling the part it is stepped off from.
        if (layout.Shape == StairShape.Spiral && layout.Centre is { } centre && steps.Count > 0)
        {
            var disc = Enumerable.Range(0, 72).Select(i => centre + new Vector2D(Math.Cos(i * Math.PI / 36), Math.Sin(i * Math.PI / 36)) * layout.OuterRadius).ToList();
            return PolygonBoolean.Combine(slab.Boundary, disc, BooleanOperation.Intersection).Sum(region => region.Area) > 1000
                ? new[] { (IReadOnlyList<Point2D>)disc }
                : Array.Empty<IReadOnlyList<Point2D>>();
        }

        // A half turn's well, beside as much of its flights as is opened: no floor left between them.
        if (layout.WellFrom is { } well && steps.Any(step => step.Kind == StairStepKind.Tread)
            && well(steps.Where(step => step.Kind == StairStepKind.Tread).SelectMany(step => step.Outline).Min(point => (point - layout.Origin).Dot(layout.Axis))) is { } gap)
            opened.Add(gap);

        return Regions(opened)
            .Where(hole => hole.Count >= 3 && PolygonBoolean.Combine(slab.Boundary, hole, BooleanOperation.Intersection).Sum(region => region.Area) > 1000)
            .ToList();
    }

    /// <summary>
    /// Where a stair hangs from by its head, if it does: the middle of its top edge, and the way
    /// it comes up onto what it reaches - the edge of a floor, a terrace, a balcony or a deck at
    /// its head's height, come up to from outside it, as a stair up to a terrace comes, or one
    /// down from it to the garden goes. Null for a stair coming up through a floor into its
    /// opening: that one keeps its foot.
    /// </summary>
    public static (Point2D Head, Vector2D Up)? HungFrom(BimDocument document, Stair stair)
    {
        var layout = Layout(document, stair);
        if (HeadOf(layout) is not { } head) return null;
        if (FloorSurfaces.StoodOnNear(document, layout.Top, head.At + head.Up * 150, FloorReach) is not { } onto) return null;
        return onto.Slab.Contains(head.At - head.Up * 300) ? null : (head.At, head.Up);
    }

    /// <summary>
    /// A stair turned and moved, as it is laid out now, until its head is at a point and it is
    /// come up off that way: laid out back from its head, as one drawn down from a terrace is.
    /// </summary>
    public static void HangFrom(BimDocument document, Stair stair, Point2D head, Vector2D up)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            // Turned to be come up off that way...
            if (HeadOf(Layout(document, stair)) is not { } now) return;
            stair.Angle = ((stair.Angle + Math.Atan2(now.Up.Cross(up), now.Up.Dot(up)) * 180 / Math.PI) % 360 + 360) % 360;

            // ...and moved till its head is there.
            if (HeadOf(Layout(document, stair)) is not { } turned || turned.At.DistanceTo(head) < 0.5) return;
            stair.Start += head - turned.At;
        }
    }

    /// <summary>
    /// Where a stair is come up off at its head, and which way: the middle of its top edge, the
    /// way up its last flight - or, for a spiral, the middle of its landing's outside edge,
    /// stepped off across outwards, as a spiral beside a deck is.
    /// </summary>
    public static (Point2D At, Vector2D Up)? HeadOf(StairLayout layout)
    {
        if (layout.Arrivals.Count == 0) return null;
        if (layout.Shape == StairShape.Spiral && layout.Centre is { } centre && layout.Arrivals.Count > 1)
        {
            var (first, last) = ((layout.Arrivals[1].From - centre).NormalisedOrDefault(Vector2D.UnitX), (layout.Arrivals[^1].To - centre).NormalisedOrDefault(Vector2D.UnitX));
            var outward = (first + last).NormalisedOrDefault(first);
            return (centre + outward * layout.OuterRadius, outward);
        }

        // Square off the edge it comes up at, away from its steps - not the way up its top step,
        // which a curve's turns from, and a landing's or a winder's runs across.
        return layout.Steps.Count > 0 ? (layout.TopEdge.From.MidpointTo(layout.TopEdge.To), Away(layout, layout.TopEdge)) : null;
    }

    /// <summary>The way off a stair at an edge where it comes up: square to it, away from its steps.</summary>
    public static Vector2D Off(StairLayout layout, (Point2D From, Point2D To) arrival) => Away(layout, arrival);

    /// <summary>
    /// What a stair cuts its openings in, as its Properties say: each floor, flat roof and pitched
    /// roof it comes up through, with the level it is on - or nothing, and why.
    /// </summary>
    public static string CutsThrough(BimDocument document, Stair stair)
    {
        var flights = Flights(document, stair);
        var cut = document.Elements.OfType<Slab>()
            .Where(slab => flights.Any(flight => Openings(document, stair, slab, flight.Layout).Count > 0))
            .Select(slab => $"{(slab is Roof && !IsFloorLike(document, slab) ? "Roof" : slab is Roof ? "Flat roof" : slab.Category == BuiltInCategory.Ceilings ? "Ceiling" : "Floor")} on {document.FindLevel(slab.LevelId)?.Name}")
            .ToList();
        if (cut.Count > 0) return string.Join(", ", cut);
        if (!stair.CutsFloorAbove && !stair.CutsRoof) return "Nothing: told not to cut";

        // Told not to cut the one that does come down into its headroom: said so.
        var floorOver = !stair.CutsFloorAbove && LeastHeadroom(document, stair, Layout(document, stair)) is { By: Slab over } && IsFloorLike(document, over);
        var roofOver = !stair.CutsRoof && LeastHeadroom(document, stair, Layout(document, stair)) is { By: Roof roof } && !IsFloorLike(document, roof);
        return floorOver ? "Nothing: Cut the Floor Above is off" : roofOver ? "Nothing: Cut the Roof is off" : "Nothing: nothing over it comes down into its headroom";
    }

    /// <summary>
    /// The holes a stair cuts in a pitched roof: over the steps whose headroom the roof's underside
    /// comes down into, where it is over them - one outline round those that meet.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<Point2D>> RoofOpenings(BimDocument document, Roof roof, StairLayout layout, double headroom)
    {
        if (roof.GetBottomElevation(document) >= layout.Top + headroom) return Array.Empty<IReadOnlyList<Point2D>>();

        var surface = roof.Surface(document);
        var opened = layout.Steps.Where(step =>
        {
            // Over the step - its top above the step's own depth under its tread - and come down into its headroom; not a roof the stair passes over.
            var under = Round(step.Outline).Where(roof.Contains).Select(point => surface.HeightAt(point)).DefaultIfEmpty(double.PositiveInfinity).Min();
            return under < step.Top + headroom && roof.TopAt(document, Polygon2D.Centroid(step.Outline, step.Outline[0])) > step.Top - 250;
        }).Select(step => step.Outline).ToList();

        return Regions(opened)
            .Where(hole => hole.Count >= 3 && PolygonBoolean.Combine(roof.Boundary, hole, BooleanOperation.Intersection).Sum(region => region.Area) > 1000)
            .ToList();
    }

    /// <summary>
    /// The floors a stair comes up through at its top, with each hole it cuts in each: what is
    /// guarded round the stairwell on the floor above.
    /// </summary>
    public static IEnumerable<(Slab Floor, IReadOnlyList<Point2D> Hole)> Stairwells(BimDocument document, Stair stair, StairLayout layout) =>
        document.Elements.OfType<Slab>()
            .Where(slab => slab is not Ceiling && IsFloorLike(document, slab) && Math.Abs(slab.GetTopElevation(document) - layout.Top) < 600)
            .SelectMany(floor => Openings(document, stair, floor, layout).Select(hole => (Floor: floor, Hole: hole)));

    /// <summary>The outlines round steps that meet: each added to whichever it touches, those apart kept apart.</summary>
    private static IReadOnlyList<IReadOnlyList<Point2D>> Regions(IReadOnlyList<IReadOnlyList<Point2D>> outlines)
    {
        var regions = new List<IReadOnlyList<Point2D>>();
        foreach (var outline in outlines)
        {
            var merged = outline;
            for (var i = regions.Count - 1; i >= 0; i--)
            {
                var joined = PolygonBoolean.Combine(regions[i], merged, BooleanOperation.Union);
                if (joined.Count != 1) continue;
                merged = joined[0].Outer;
                regions.RemoveAt(i);
            }

            regions.Add(merged);
        }

        return regions;
    }

    /// <summary>The whole of a stair in plan: its steps as one outline - the largest, where they come apart.</summary>
    public static IReadOnlyList<Point2D> Footprint(StairLayout layout) =>
        Regions(layout.Steps.Select(step => step.Outline).ToList()).OrderByDescending(Polygon2D.Area).FirstOrDefault() ?? Array.Empty<Point2D>();

    /// <summary>
    /// The least headroom over a stair's steps - from each step up to the underside of whatever
    /// floor or ceiling is over it, its own opening taken out, or of a pitched roof coming down
    /// over it, as over a stair to an attic or one outside under the eaves - and over which
    /// step; none where nothing is over it.
    /// </summary>
    public static (double Clear, int Step, Element By)? LeastHeadroom(BimDocument document, Stair stair, StairLayout layout, bool middleOnly = false)
    {
        (double Clear, int Step, Element By)? least = null;

        // The middle half of a step across the stair, where a loft conversion's headroom is measured at its highest.
        static IReadOnlyList<Point2D> Middle(StairStep step) =>
            step.Outline.Count != 4 ? step.Outline : new[]
            {
                step.Outline[0] + (step.Outline[3] - step.Outline[0]) * 0.25, step.Outline[1] + (step.Outline[2] - step.Outline[1]) * 0.25,
                step.Outline[1] + (step.Outline[2] - step.Outline[1]) * 0.75, step.Outline[0] + (step.Outline[3] - step.Outline[0]) * 0.75
            };

        // Only what is over it in plan: the floors, beams, joist layouts and roofs anywhere else not worked out at all.
        var points = layout.Steps.SelectMany(step => step.Outline).ToList();
        var (low, high) = points.Count == 0 ? (default(Point2D), default(Point2D))
            : (new Point2D(points.Min(point => point.X) - 500, points.Min(point => point.Y) - 500), new Point2D(points.Max(point => point.X) + 500, points.Max(point => point.Y) + 500));
        bool Over(IEnumerable<Point2D> outline) => points.Count > 0 && outline.Any() && outline.Max(point => point.X) >= low.X && outline.Min(point => point.X) <= high.X
                                                   && outline.Max(point => point.Y) >= low.Y && outline.Min(point => point.Y) <= high.Y;

        // The steps a slab's outline is over at all, under its underside: the slab as built - cut and opened - worked out only for those.
        bool Under(Slab slab, StairStep step) =>
            PolygonBoolean.Combine(slab.Boundary, step.Outline, BooleanOperation.Intersection).Sum(piece => piece.Area) >= 1000;

        foreach (var slab in document.Elements.OfType<Slab>().Where(slab => slab.Boundary.Count >= 3 && Over(slab.Boundary) && IsFloorLike(document, slab)))
        {
            if (slab.GetTopElevation(document) <= layout.Bottom + 1) continue;
            var underside = slab.GetBottomElevation(document);
            var under = layout.Steps.Where(step => underside > step.Top && Under(slab, step)).ToList();
            if (under.Count == 0) continue;
            var regions = SlabEdges.Regions(document, slab);
            foreach (var step in under)
            {
                var outline = middleOnly ? Middle(step) : step.Outline;
                var over = regions.Sum(region => PolygonBoolean.Combine(Shafts.Loops(region), new[] { outline }, BooleanOperation.Intersection).Sum(piece => piece.Area));
                if (over < 1000) continue;
                var clear = underside - step.Top;
                if (least is not { } known || clear < known.Clear) least = (clear, step.Number, slab);
            }
        }

        // A beam over it - or a joist of a layout - measured to its underside where it passes over each step, where it crosses the stair, or runs along over it.
        var framing = document.Elements.OfType<Beam>().Where(beam => Over(new[] { beam.Start, beam.End })).Select(beam => (Beam: beam, By: (Element)beam))
            .Concat(document.Elements.OfType<BeamSystem>().Where(system => Over(system.Boundary))
                .SelectMany(system => BeamSystems.Members(document, system).Select(joist => (Beam: joist, By: (Element)system))));
        foreach (var (beam, by) in framing)
        foreach (var step in layout.Steps)
        {
            if (Beams.LowestOver(document, beam, middleOnly ? Middle(step) : step.Outline) is not { } underside || underside <= step.Top) continue;
            var clear = underside - step.Top;
            if (least is not { } known || clear < known.Clear) least = (clear, step.Number, by);
        }

        // A pitched roof is not cut for it: measured round the edge of each step - where a roof's underside comes lowest - wherever the roof is over it and not opened.
        foreach (var roof in document.Elements.OfType<Roof>().Where(roof => roof.Boundary.Count >= 3 && Over(roof.Boundary) && !IsFloorLike(document, roof)))
        {
            if (!layout.Steps.Any(step => Under(roof, step))) continue;
            var surface = roof.Surface(document);
            var openings = RoofJoin.Openings(document, roof);
            var own = Openings(document, stair, roof, layout);
            foreach (var step in layout.Steps)
            {
                // A step under the hole cut in the roof for it has none over it there; under any other, only where the hole is.
                if (own.Any(opening => Polygon2D.Contains(opening, Polygon2D.Centroid(step.Outline, step.Outline[0])))) continue;
                var outline = middleOnly ? Middle(step) : step.Outline;
                foreach (var point in Round(outline))
                {
                    if (!roof.Contains(point) || openings.Any(opening => Polygon2D.Contains(opening, point))) continue;
                    var clear = surface.HeightAt(point) - step.Top;
                    if (clear <= 0) continue;
                    if (least is not { } known || clear < known.Clear) least = (clear, step.Number, roof);
                }
            }
        }

        return least;
    }

    /// <summary>Points round an outline, its corners and every 100 or so between them.</summary>
    private static IEnumerable<Point2D> Round(IReadOnlyList<Point2D> outline)
    {
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var pieces = Math.Max(1, (int)Math.Ceiling((b - a).Length / 100));
            for (var k = 0; k < pieces; k++) yield return a + (b - a) * ((double)k / pieces);
        }
    }

    /// <summary>
    /// Where a stair's going is measured, and what it is there: for straight treads, the going;
    /// for a curved stair's tapered treads, along the middle of one under 1000 wide and 270 in
    /// from each side of a wider one, as Approved Document K says - or at the IRC's walkline,
    /// 305 (12 in) from the narrow side.
    /// </summary>
    private static IReadOnlyList<(double Going, string Where)> MeasuredGoings(StairLayout layout, ClearanceStandard standard)
    {
        if (layout.Shape != StairShape.Curved || layout.GoingAt is not { } at) return new[] { (layout.Going, "") };
        if (standard.IsUs()) return new[] { (at(Math.Min(305, layout.Width / 2)), "at the walkline 305 (12 in) from the inside") };
        return layout.Width < 1000
            ? new[] { (at(layout.Width / 2), "along the middle") }
            : new[] { (at(270), "270 in from the inside"), (at(layout.Width - 270), "270 in from the outside") };
    }

    /// <summary>
    /// Whether a stair comes up to an outside door, and every outside door of the building is
    /// reached by a stair outside - none by a level way or a ramp.
    /// </summary>
    private static bool StepsToEveryDoor(BimDocument document, StairLayout layout)
    {
        var outside = document.Elements.OfType<Door>()
            .Where(door => document.FindType<DoorType>(door.TypeId) is { } type && Doors.External(document, door, type))
            .ToList();
        if (outside.Count == 0) return false;

        var arrivals = document.Elements.OfType<Stair>().Where(other => other.Outside)
            .SelectMany(other => Flights(document, other))
            .SelectMany(flight => flight.Layout.Arrivals.Select(arrival => (Middle: arrival.From.MidpointTo(arrival.To), flight.Layout.Top)))
            .ToList();

        bool Stepped(Door door)
        {
            if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } wall) return false;
            var at = wall.PointAt(document.GetWallType(wall)?.Structure ?? new Materials.CompoundStructure(), door.DistanceAlongWall, 0);
            var sill = wall.GetBaseElevation(document) + door.SillHeight;
            return arrivals.Any(arrival => Math.Abs(arrival.Top - sill) < 150 && arrival.Middle.DistanceTo(at) < 2000);
        }

        var mine = layout.Arrivals.Select(arrival => arrival.From.MidpointTo(arrival.To)).ToList();
        var servesOne = outside.Any(door => Stepped(door) && document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is { } wall
            && mine.Any(middle => middle.DistanceTo(wall.PointAt(document.GetWallType(wall)?.Structure ?? new Materials.CompoundStructure(), door.DistanceAlongWall, 0)) < 2000));
        return servesOne && outside.All(Stepped);
    }

    /// <summary>
    /// What about a stair falls short of its rules - each said, with what is asked - or nothing.
    /// A stair repeated up a stairwell is checked storey by storey: what is wrong further up and
    /// not at its foot is said with the storey it goes up from.
    /// </summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Stair stair)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        var type = TypeOf(document, stair);
        var flights = Flights(document, stair);
        var problems = FlightProblems(document, stair, type, flights[0], standard);
        var said = problems.ToHashSet();
        foreach (var flight in flights.Skip(1))
        foreach (var problem in FlightProblems(document, stair, type, flight, standard).Where(said.Add))
            problems.Add($"Up from {document.FindLevel(flight.LevelId)?.Name}: {problem}");

        return problems;
    }

    /// <summary>What about one storey's flight of a stair falls short of its rules.</summary>
    private static List<string> FlightProblems(BimDocument document, Stair stair, StairType type, StairFlight flight, ClearanceStandard standard)
    {
        var layout = flight.Layout;
        var problems = new List<string>();
        var rise = layout.Rise;
        var narrowest = layout.GoingAt?.Invoke(0);

        // Its head at or below its foot - its top level moved down, or its base up: nothing else about it means anything.
        var height = layout.Top - layout.Bottom;
        if (height < MinimumRise - 0.5)
        {
            problems.Add(height <= 0
                ? $"Its head is {-height:0} below its foot: a stair goes up from its base level - set its top level above it."
                : $"It rises only {height:0}: set its top level above its base level, or its height to {MinimumRise:0} at least.");
            return problems;
        }

        if (layout.Shape is StairShape.AlternatingTread or StairShape.Ladder)
            problems.AddRange(SteepProblems(stair, type, layout, standard));
        else if (layout.Shape == StairShape.Spiral)
            problems.AddRange(SpiralProblems(layout, standard));
        else if (standard == ClearanceStandard.Ada && stair.Use != StairUse.Private)
        {
            // A stair in a building other than a home, as the ADA has it (504, 505) - and the IBC as wide as an exit stair.
            if (rise > 178 + 0.5) problems.Add($"Each riser is {rise:0}: the ADA allows 178 (7 in) at most (504.2).");
            if (rise < 102 - 0.5) problems.Add($"Each riser is {rise:0}: the ADA asks for 102 (4 in) at least (504.2).");
            foreach (var (going, where) in MeasuredGoings(layout, standard))
                if (going < 279 - 0.5) problems.Add(where == "" ? $"Each tread is {going:0}: the ADA asks for 279 (11 in) at least (504.2)." : $"Its treads are {going:0} {where}: the ADA asks for 279 (11 in) at least (504.2).");
            if (layout.Width < 1118 - 0.5) problems.Add($"It is {layout.Width:0} wide: the IBC asks for 1118 (44 in) of an exit stair (1011.2).");
            if (layout.WinderGoing is not null || layout.Shape == StairShape.Curved && narrowest is < 279)
                problems.Add("Its treads taper: the ADA asks for treads all the same, and the IBC allows winders only within a home.");
            if (type.OpenRisers) problems.Add("Its risers are open: the ADA does not allow open risers (504.3).");
            if (type.Nosing > 38 + 0.5) problems.Add($"Its nosings stand {type.Nosing:0} out: the ADA allows 38 (1½ in) at most (504.5).");
            if (stair.Railings is StairRailings.LeftSide or StairRailings.RightSide or StairRailings.None)
                problems.Add("The ADA asks for handrails on both sides (505.2).");
            else if (type.HandrailExtension < Math.Max(305, layout.Going) - 0.5)
                problems.Add($"Its handrails run on {type.HandrailExtension:0} past its ends: the ADA asks for 305 (12 in) past its top riser and a tread's depth, {layout.Going:0}, past its bottom one (505.10).");
        }
        else if (standard.IsUs())
        {
            if (rise > 196 + 0.5) problems.Add($"Each riser is {rise:0}: the IRC allows 196 (7¾ in) at most.");
            foreach (var (going, where) in MeasuredGoings(layout, standard))
                if (going < 254 - 0.5) problems.Add(where == "" ? $"Each tread is {going:0}: the IRC asks for 254 (10 in) at least." : $"Its treads are {going:0} {where}: the IRC asks for 254 (10 in) at least.");
            if (layout.Width < 914 - 0.5) problems.Add($"It is {layout.Width:0} wide: the IRC asks for 914 (36 in) at least.");

            // At its handrails, what they leave clear: 31½ in with one, 27 in with two, a wall rail standing 4½ in out at most (R311.7.1).
            var projection = type.WallClearance + type.HandrailWidth;
            var sides = StairModel.Railings(document, stair, layout)
                .GroupBy(railing => railing.LeftSide)
                .Select(side => side.Any(railing => railing.OnWall) ? projection : type.HandrailWidth / 2)
                .ToList();
            if (sides.Count > 0)
            {
                var (least, said) = sides.Count >= 2 ? (686.0, "686 (27 in) with handrails both sides") : (800.0, "800 (31½ in) with a handrail on one side");
                var clear = layout.Width - sides.Sum();
                if (clear < least - 0.5) problems.Add($"It is {clear:0} clear at its handrails: the IRC asks for {said} (R311.7.1).");
                if (sides.Any(side => side == projection) && projection > 114 + 0.5)
                    problems.Add($"Its wall handrails stand {projection:0} out from the wall: the IRC allows 114 (4½ in) at most (R311.7.1).");
            }

            if (layout.WinderNarrowEnd is { } narrow && narrow < 152) problems.Add($"Its winders are {narrow:0} at their narrow end: the IRC asks for 152 (6 in).");
            if (layout.WinderGoing is not null && layout.GoingAt is { } goingAt && goingAt(305) < 254) problems.Add("Its winders are too shallow at the walkline 305 (12 in) from their narrow end: the IRC asks for 254 (10 in).");
            if (layout.Shape == StairShape.Curved && narrowest is { } inside && inside < 152 - 0.5) problems.Add($"Its treads are {inside:0} at their narrow end: the IRC asks for 152 (6 in).");
        }
        else
        {
            // As Approved Document K's Table 1.1 has it for what the stair is for.
            var (use, (riseLeast, riseMost, goingLeast, goingMost)) = (UseName(stair.Use), RiseAndGoing(stair.Use));
            if (rise > riseMost + 0.5) problems.Add($"Each riser is {rise:0}: Approved Document K allows {riseMost:0} at most on {use}.");
            if (rise < riseLeast - 0.5) problems.Add($"Each riser is {rise:0}: Approved Document K asks for {riseLeast:0} at least on {use}.");
            // A house's stair outside: goings of 280 at least.
            var outside = stair.Outside && stair.Use == StairUse.Private;
            foreach (var (going, where) in MeasuredGoings(layout, standard))
            {
                var said = where == "" ? $"Each going is {going:0}" : $"Its going {where} is {going:0}";
                var pitch = Math.Atan2(rise, going) * 180 / Math.PI;
                if (outside && going < 280 - 0.5) problems.Add($"{said}: Approved Document K asks for 280 at least on a house's stair outside.");
                else if (going < goingLeast - 0.5) problems.Add($"{said}: Approved Document K asks for {goingLeast:0} at least on {use}.");
                if (going > goingMost + 0.5) problems.Add($"{said}: Approved Document K allows {goingMost:0} at most on {use}.");
                if (stair.Use == StairUse.Private && pitch > 42 + 0.05) problems.Add($"It is pitched at {pitch:0.0}°{(where == "" ? "" : " " + where)}: Approved Document K allows 42° at most on a private stair.");
                var twice = 2 * rise + going;
                if (twice < 550 - 0.5 || twice > 700 + 0.5) problems.Add($"Twice the rise plus the going{(where == "" ? "" : " " + where)} is {twice:0}: Approved Document K asks for 550 to 700.");
            }

            if (layout.WinderNarrowEnd is { } narrow && narrow < 50) problems.Add($"Its winders are {narrow:0} at their narrow end: Approved Document K asks for 50.");
            if (layout.WinderGoing is { } winder && winder < 220 - 0.5) problems.Add($"Its winders' going along the middle is {winder:0}: Approved Document K asks for the flight's 220 there.");
            if (layout.Shape == StairShape.Curved && narrowest is { } inside && inside < 50 - 0.5) problems.Add($"Its treads are {inside:0} at their narrow end: Approved Document K asks for 50.");
            if (layout.WidestFlight >= 1000 && stair.Railings is StairRailings.LeftSide or StairRailings.RightSide or StairRailings.None)
                problems.Add($"{(layout.WidestFlight > layout.Width ? "Its first flight is" : "It is")} 1000 wide or more: Approved Document K asks for a handrail on both sides.");
            if (stair.Use != StairUse.Private) problems.AddRange(PublicProblems(document, stair, type, layout));
        }

        if (standard.IsUs() && layout.Shape is StairShape.AlternatingTread or StairShape.Ladder)
        {
            if (type.HandrailHeight < 762 - 0.5 || type.HandrailHeight > 864 + 0.5) problems.Add($"Its handrail is {type.HandrailHeight:0} high: the IRC asks for 762 to 864 (30 to 34 in) on {(layout.Shape == StairShape.Ladder ? "a ship's ladder" : "alternating treads")}.");
        }
        else if (standard.IsUs())
        {
            if (type.HandrailHeight < 864 - 0.5 || type.HandrailHeight > 965 + 0.5) problems.Add($"Its handrail is {type.HandrailHeight:0} high: the IRC asks for 864 to 965 (34 to 38 in).");
        }
        else if (type.HandrailHeight < 900 - 0.5 || type.HandrailHeight > 1000 + 0.5)
            problems.Add($"Its handrail is {type.HandrailHeight:0} high: Approved Document K asks for 900 to 1000.");

        // A handrail on one side at least: under the IRC where it has 4 risers or more (R311.7.8); in a house, 3 or more - as Approved
        // Document K asks of a stepped change of level in the entrance storey (1.37) and Approved Document M of steps up to a door (1.8f, 2.11f); elsewhere, any.
        var wantsHandrail = standard.IsUs() ? layout.Risers >= 4 : stair.Use != StairUse.Private || layout.Risers >= 3;
        if (stair.Railings == StairRailings.None && wantsHandrail)
            problems.Add(standard.IsUs()
                ? "It has no handrail: the IRC asks for one on at least one side of 4 risers or more."
                : stair.Use == StairUse.Private
                    ? "It has no handrail: Approved Document K asks for one on at least one side (1.34) - of a flight of three risers or more in a house (1.37, and Approved Document M 1.8f, 2.11f)."
                    : "It has no handrail: Approved Document K asks for one on each side (1.35).");
        problems.AddRange(BuildProblems(document, stair, type, layout, standard));

        // Open sides it is told to leave without a railing: guarded where they drop more than 600 in a dwelling, beside two risers
        // or more elsewhere - or more than 30 in (762) up, under the IRC.
        foreach (var (role, highest) in StairModel.Unguarded(document, stair, layout))
        {
            var drop = highest - layout.Bottom;
            var (limit, rule) = standard.IsUs() ? (762.0, "the IRC asks for a guard where it is more than 762 (30 in) up")
                : stair.Use == StairUse.Private ? (600.0, "Approved Document K asks for guarding where it drops more than 600")
                : (2 * rise - 1.5, "Approved Document K asks for guarding beside two risers or more");
            var side = role switch { StairSideRole.Left => "left side", StairSideRole.Right => "right side", _ => "landing's open edge" };
            if (drop > limit + 0.5) problems.Add($"Its {side} is open, {drop:0} up at its highest, with no railing: {rule}.");
        }

        // Steps up to the house's doors, under Approved Document M: the approach is to be step-free to one entrance at least.
        if (standard is ClearanceStandard.ApprovedDocumentM or ClearanceStandard.ApprovedDocumentM3 && stair.Outside && StepsToEveryDoor(document, layout))
            problems.Add("Its steps are the only way up to the house's outside doors: Approved Document M asks for a step-free approach to the entrance, " +
                         "or to another entrance (M4(2) 2.6, 2.7; M4(3) 3.7, 3.8) - a ramp beside it, or another door reached without steps.");

        // A wall standing across its steps.
        foreach (var (wall, step) in StairModel.WallsThrough(document, layout))
            problems.Add($"It runs through a wall ({document.GetWallType(wall)?.Name ?? "Wall"}) at step {step}: move it clear of the wall, or the wall clear of it.");

        var needed = Headroom(standard, layout.Shape);
        if (stair.LoftConversion && !standard.IsUs())
        {
            // In a loft conversion: 1900 at the middle of the stair, 1800 at its sides.
            if (LeastHeadroom(document, stair, layout, middleOnly: true) is { } middle && middle.Clear < 1900 - 0.5)
                problems.Add($"Its headroom over the middle of step {middle.Step} is {middle.Clear:0}{Under(middle.By)}: in a loft conversion Approved Document K accepts 1900 there, at the least{Remedy(middle.By)}.");
            if (LeastHeadroom(document, stair, layout) is { } side && side.Clear < 1800 - 0.5)
                problems.Add($"Its headroom over step {side.Step} is {side.Clear:0}{Under(side.By)}: in a loft conversion Approved Document K accepts 1800 at the side of the stair, at the least{Remedy(side.By)}.");
        }
        else if (LeastHeadroom(document, stair, layout) is { } least && least.Clear < needed - 0.5)
            problems.Add($"Its headroom over step {least.Step} is {least.Clear:0}{Under(least.By)}: {Label(standard)} asks for {needed:0}{Remedy(least.By)}.");

        // A pitched roof is not cut for a stair as a floor is: said, with what would give it the height.
        string Under(Element by) => by switch
        {
            Roof roof when !IsFloorLike(document, roof) => " under the roof",
            Beam => " under a beam",
            BeamSystem => " under a joist",
            _ => ""
        };
        string Remedy(Element by) => by switch
        {
            Roof roof when !IsFloorLike(document, roof) => " - a dormer over it, or the stair where the roof is higher",
            Beam => " - the beam raised, or the stair moved clear of it",
            BeamSystem => " - its joists trimmed round the stairwell, or the stair moved clear of them",
            _ => ""
        };

        // Going right round, a step comes over the one a turn below it: as clear over it as under a floor.
        if (layout.TreadsPerTurn is { } perTurn && perTurn < layout.Risers - 1)
        {
            var clear = perTurn * rise - (type.Construction == StairConstruction.Timber ? type.TreadThickness : rise + 150);
            if (clear < needed - 0.5) problems.Add($"It comes round over itself with {clear:0} headroom: {Label(standard)} asks for {needed:0}.");
        }

        // Its head running on in past the edge of the deck it comes up onto, cutting into it.
        if (Overrun(document, stair, layout, flight.TopLevelId) is { } overrun)
            problems.Add($"It runs {overrun.By:0} in past the edge of the floor it comes up onto, cutting into it: set its foot back {overrun.By:0}, and it comes up at the edge.");

        // Its head meeting nothing: a floor, deck or terrace at the level it goes up to close by, but not reached.
        if (flight.TopLevelId is { } topLevel && Math.Abs(stair.TopOffset) < FloorReach && SteppedOff(layout) is { } off
            && SurfaceAt(document, topLevel, off) is null && Nearest(document, topLevel, HeadOf(layout)?.At ?? layout.TopEdge.From.MidpointTo(layout.TopEdge.To)) is { } near)
            problems.Add($"It comes up {near.Gap:0} short of the floor on {document.FindLevel(topLevel)?.Name}: nothing to step off onto at its head - move it, or the floor, to meet it.");

        problems.AddRange(LandingProblems(document, stair, layout, flight, standard));
        return problems;
    }

    /// <summary>What a stair is for, in words: "a private stair", "a utility stair", "a general access stair".</summary>
    private static string UseName(StairUse use) => use switch
    {
        StairUse.Utility => "a utility stair",
        StairUse.GeneralAccess => "a general access stair",
        _ => "a private stair"
    };

    /// <summary>Approved Document K's Table 1.1: the rise and going a stair may have, as it is for.</summary>
    private static (double RiseLeast, double RiseMost, double GoingLeast, double GoingMost) RiseAndGoing(StairUse use) => use switch
    {
        StairUse.Utility => (150, 190, 250, 400),
        StairUse.GeneralAccess => (150, 170, 250, 400),
        _ => (150, 220, 220, 300)
    };

    /// <summary>
    /// What Approved Document K asks more of a stair in a building other than a dwelling: closed
    /// risers, nosings standing out 25 at most, handrails both sides running on 300 past its top
    /// and bottom, 1000 between them, and landings 1200 clear.
    /// </summary>
    private static IEnumerable<string> PublicProblems(BimDocument document, Stair stair, StairType type, StairLayout layout)
    {
        var use = UseName(stair.Use);
        if (type.OpenRisers) yield return $"Its risers are open: Approved Document K asks for closed risers on {use}.";
        if (type.Nosing > 25 + 0.5) yield return $"Its nosings are {type.Nosing:0}: Approved Document K allows 25 at most on {use}.";
        if (stair.Railings is StairRailings.LeftSide or StairRailings.RightSide) yield return $"Approved Document K asks for handrails on both sides of {use}.";
        if (stair.Railings != StairRailings.None && type.HandrailExtension < 300 - 0.5)
            yield return $"Its handrails do not run on 300 past its top and bottom: Approved Document K asks for that on {use}.";

        var walls = StairModel.Railings(document, stair, layout).Count(railing => railing.OnWall);
        var between = layout.Width - walls * (type.WallClearance + type.HandrailWidth);
        if (between < 1000 - 0.5) yield return $"It is {between:0} between handrails: Approved Document K asks for 1000 on {use}.";
        if (layout.Steps.Any(step => step.Kind == StairStepKind.Landing) && layout.Width < 1200 - 0.5)
            yield return $"Its landings are {layout.Width:0} long: Approved Document K asks for 1200 clear on {use}.";
    }

    /// <summary>
    /// The rules for alternating treads and a fixed ladder. Approved Document K allows either only
    /// in a loft conversion, up to one habitable room, where there is no room for a stair: with
    /// handrails both sides, rises of 220 at most - alternating treads, a going of 220 at least
    /// between alternate nosings. The IRC's alternating tread device and ship's ladder: 20 in
    /// (508) between handrails, treads 5 in (127) deep and 8½ in (216) with their nosing - an
    /// alternating tread 7 in (178) wide - rises of 9½ in (241) at most, handrails both sides;
    /// only to a loft of 200 sq ft at most, not as a way out.
    /// </summary>
    private static IEnumerable<string> SteepProblems(Stair stair, StairType type, StairLayout layout, ClearanceStandard standard)
    {
        var ladder = layout.Shape == StairShape.Ladder;
        var both = stair.Railings == StairRailings.BothSides;
        if (!standard.IsUs())
        {
            var what = ladder ? "a fixed ladder" : "alternating treads";
            if (!stair.LoftConversion) yield return $"Approved Document K allows {what} only in a loft conversion, up to one habitable room, where there is no room for a stair.";
            if (!both) yield return $"Approved Document K asks for a handrail on both sides of {what}.";
            if (layout.Rise > 220 + 0.5) yield return $"Each riser is {layout.Rise:0}: Approved Document K allows 220 at most on {what}.";
            if (!ladder && type.Going < 220 - 0.5) yield return $"Its going between alternate nosings is {type.Going:0}: Approved Document K asks for 220 at least.";
            yield break;
        }

        var device = ladder ? "a ship's ladder" : "an alternating tread device";
        if (!stair.LoftConversion) yield return $"The IRC allows {device} only up to a loft of 200 sq ft (18.6 m²), not as a way out.";
        if (!both) yield return $"The IRC asks for a handrail on both sides of {device}.";
        if (layout.Rise > 241 + 0.5) yield return $"Each riser is {layout.Rise:0}: the IRC allows 241 (9½ in) at most on {device}.";
        if (layout.Going < 127 - 0.5) yield return $"Its treads are {layout.Going:0} deep: the IRC asks for 127 (5 in) on {device}.";
        var nosing = ladder ? Math.Max(type.Nosing, 90) : type.Nosing;
        if (layout.Going + nosing < 216 - 0.5) yield return $"Its treads project {layout.Going + nosing:0} with their nosing: the IRC asks for 216 (8½ in) on {device}.";
        if (layout.Width < 508 - 0.5) yield return $"It is {layout.Width:0} wide: the IRC asks for 508 (20 in) between handrails on {device}.";
        if (!ladder && layout.Width / 2 < 178 - 0.5) yield return $"Its treads are {layout.Width / 2:0} wide: the IRC asks for 178 (7 in) on {device}.";
    }

    /// <summary>
    /// What about its landings falls short. The IRC allows 151 in (3835) at most between floors
    /// and landings; Approved Document K asks for a change of direction of 30° or more after 36
    /// risers. And the doors round it: Approved Document K lets one swing across the landing at
    /// its foot only if it leaves 400 clear across the stair, and none over the landing at its
    /// head; no door may swing over its steps.
    /// </summary>
    private static IEnumerable<string> LandingProblems(BimDocument document, Stair stair, StairLayout layout, StairFlight flight, ClearanceStandard standard)
    {
        // A split stair's two ways up have the same steps, by number: each counted once.
        var steps = layout.Steps.GroupBy(step => step.Number).Select(group => group.First()).OrderBy(step => step.Number).ToList();
        if (steps.Count == 0) yield break;

        // Risers between landings: from the floor, each onto its step; a landing ends a run, the floor at its head the last.
        var (run, most) = (0, 0);
        foreach (var step in steps)
        {
            run++;
            if (step.Kind != StairStepKind.Landing) continue;
            (most, run) = (Math.Max(most, run), 0);
        }

        most = Math.Max(most, run + 1);
        if (standard.IsUs() && most * layout.Rise > 3835 + 0.5)
            yield return $"It rises {most * layout.Rise:0} with no landing: the IRC allows 3835 (151 in) between floors and landings.";

        // A building other than a dwelling: 16 risers a flight on a utility stair, 12 on a general access one.
        var flightMost = stair.Use switch { StairUse.Utility => 16, StairUse.GeneralAccess => 12, _ => int.MaxValue };
        if (!standard.IsUs() && most > flightMost)
            yield return $"It climbs {most} risers in a flight: Approved Document K allows {flightMost} on {UseName(stair.Use)}.";

        // Risers going the same way, give or take 30°: a run that turns is a fresh one.
        if (!standard.IsUs())
        {
            var (count, longest) = (0, 0);
            Vector2D? way = null;
            foreach (var step in steps)
            {
                if (way is { } heading && heading.Dot(step.Up) < Math.Cos(Math.PI / 6) - 1e-9) (count, way) = (0, null);
                way ??= step.Up;
                longest = Math.Max(longest, ++count);
            }

            if (longest + 1 > 36)
                yield return $"It climbs {longest + 1} risers without turning: Approved Document K asks for a change of direction of 30° or more after 36.";
        }

        static bool Overlap(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b) =>
            PolygonBoolean.Combine(a, b, BooleanOperation.Intersection).Sum(region => region.Area) > 100;

        // The doors that open at its foot and at its head: those whose sill is at the floor there - a balcony door up a wall two storeys high among them.
        var reach = Math.Min(FloorReach, (layout.Top - layout.Bottom) / 2);

        // At its foot: in a dwelling, a door may swing across the landing leaving 400 clear across the stair; elsewhere, across none of it.
        var below = DoorSwings(document, layout.Bottom, reach);
        var first = steps[0];
        var clear = stair.Use == StairUse.Private ? 400 : Math.Max(1200, layout.Width);
        var foot = new[] { first.RiserFrom, first.RiserTo, first.RiserTo - first.Up * clear, first.RiserFrom - first.Up * clear };
        if (!standard.IsUs() && below.Any(swing => Overlap(swing, foot)))
            yield return stair.Use == StairUse.Private
                ? "A door swings within 400 of its bottom step: Approved Document K asks for 400 clear across the stair."
                : $"A door swings across the landing at its foot: Approved Document K asks for landings clear of doors on {UseName(stair.Use)}.";
        if (below.Any(swing => steps.Any(step => step.Top - layout.Bottom < 2100 && Overlap(swing, step.Outline))))
            yield return "A door swings over its steps: it must open clear of the stair.";

        // At its head: no door may swing out over its top steps - those in its stairwell, below the door's own height.
        var above = DoorSwings(document, layout.Top, reach);
        if (above.Any(swing => steps.Any(step => layout.Top - step.Top < 2100 && Overlap(swing, step.Outline))))
            yield return "A door at its head swings out over its steps: it must open onto a landing, clear of the stair.";
        if (standard.IsUs()) yield break;

        // The landing at its head: out from where it comes up as far as it is wide.
        foreach (var (from, to) in layout.Arrivals)
        {
            var away = Away(layout, (from, to));
            var landing = new[] { from, to, to + away * layout.Width, from + away * layout.Width };
            if (above.Any(swing => Overlap(swing, landing)))
            {
                yield return "A door swings over the landing at its head: Approved Document K asks for its landings to be clear of doors.";
                yield break;
            }
        }
    }

    /// <summary>
    /// The ground swinging doors sweep as they open at a floor: those whose sill is within reach
    /// of its height, whatever level their wall stands on - each leaf a quarter circle round its hinge.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> DoorSwings(BimDocument document, double floor, double reach = FloorReach)
    {
        var swings = new List<IReadOnlyList<Point2D>>();
        foreach (var door in document.Elements.OfType<Door>())
        {
            if (document.FindType<DoorType>(door.TypeId) is not { Operation: DoorOperation.Swing or DoorOperation.DoubleSwing } type) continue;
            if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } wall) continue;
            if (Math.Abs(wall.GetBaseElevation(document) + door.SillHeight - floor) > reach) continue;
            if (document.GetWallType(wall) is not { } wallType) continue;

            var (from, to) = door.GetSpan(type);
            var face = wallType.Structure.TotalWidth / 2;
            var side = door.FlipFacing ? -1.0 : 1.0;
            Point2D At(double along, double off) => wall.PointAt(wallType.Structure, along, side * off);
            IReadOnlyList<Point2D> Leaf(double hinge, double free)
            {
                var (h, shut, open) = (At(hinge, face), At(free, face), At(hinge, face + Math.Abs(free - hinge)));
                return Enumerable.Range(0, 7).Select(i => i * Math.PI / 12).Select(a => h + (shut - h) * Math.Cos(a) + (open - h) * Math.Sin(a)).Prepend(h).ToList();
            }

            if (type.Operation == DoorOperation.DoubleSwing)
            {
                swings.Add(Leaf(from, (from + to) / 2));
                swings.Add(Leaf(to, (from + to) / 2));
            }
            else swings.Add(door.FlipHand ? Leaf(to, from) : Leaf(from, to));
        }

        return swings;
    }

    /// <summary>
    /// What about how a stair is built falls short. Open risers: Approved Document K asks that no
    /// 100 mm sphere pass between the treads, and that they overlap by 16; the IRC, no 4 in
    /// (102) sphere through a gap more than 30 in (763) over the floor below (R311.7.5.1), and
    /// none at all on a spiral. Closed risers under the IRC: nosings of ¾ to 1¼ in (19
    /// to 32) where treads are under 11 in (279). Balusters no further apart than lets a 100
    /// sphere through - 4⅜ in (111) up an IRC stair. And treads cantilevered from a wall need one.
    /// </summary>
    private static IEnumerable<string> BuildProblems(BimDocument document, Stair stair, StairType type, StairLayout layout, ClearanceStandard standard)
    {
        var timber = type.Construction == StairConstruction.Timber;
        var us = standard.IsUs();

        if (timber && type.OpenRisers)
        {
            var gap = layout.Rise - type.TreadThickness;
            if (type.RiserBars) gap = (gap - StairModel.RiserBarSize) / 2;
            var sphere = us ? 102 : 100;
            // Under the IRC, only a gap whose bottom - the tread under it - is more than 30 in over the floor at the foot.
            var high = !us || (layout.Risers - 1) * layout.Rise > 763;
            if (gap >= sphere && high && !(us && layout.Shape == StairShape.Spiral))
                yield return $"Its open risers leave {gap:0} between the treads: {(us ? "the IRC asks for less than 102 (4 in) where they are more than 30 in (763) up" : "Approved Document K asks for less than 100")} - thicker treads, or a bar across each.";
            if (!us && type.Nosing < 16 - 0.5)
                yield return $"Its treads overlap by {type.Nosing:0}: Approved Document K asks for 16 at least where the risers are open.";
        }
        else if (us && layout.Shape is not (StairShape.AlternatingTread or StairShape.Ladder) && layout.Going < 279 - 0.5 && (type.Nosing < 19 - 0.5 || type.Nosing > 32 + 0.5))
            yield return $"Its nosings are {type.Nosing:0}: the IRC asks for 19 to 32 (¾ to 1¼ in) where treads are under 279 (11 in).";

        if (type.Balustrade == StairBalustrade.Balusters && stair.Railings != StairRailings.None)
        {
            var clear = type.BalusterSpacing - StairModel.BalusterSize;
            if (us ? clear >= 111 : clear >= 100)
                yield return $"Its balusters are {clear:0} apart: {(us ? "the IRC asks for less than 111 (4⅜ in) up a stair" : "Approved Document K asks for less than 100")}.";
        }

        if (timber && type.Support == StairSupport.Cantilevered && layout.Shape != StairShape.Spiral && !StairModel.AgainstAWall(document, stair, layout))
            yield return "Its treads are cantilevered, but no side of it runs along a wall to build them into.";

        foreach (var problem in RailingProblems(document, stair, type, layout, standard)) yield return problem;
    }

    /// <summary>
    /// What about its handrails and guarding falls short. A handrail is to be one a hand can grip:
    /// round, 32 to 50 across, or no more than 50 wide and 39 deep (Approved Document K); round,
    /// 1¼ to 2 in (32 to 51), or one 4 to 6¼ in (102 to 159) round its edge and 2¼ in (57) across
    /// at most (the IRC's type I). On a wall, 50 to 75 clear of it - the IRC, 1½ in (38) at
    /// least - its ends returned or ending on a post, under the IRC. Guarding where it is level:
    /// 900 high in a house, 36 in (914) under the IRC.
    /// </summary>
    private static IEnumerable<string> RailingProblems(BimDocument document, Stair stair, StairType type, StairLayout layout, ClearanceStandard standard)
    {
        if (stair.Railings == StairRailings.None) yield break;
        var us = standard.IsUs();
        var (width, depth) = (type.HandrailWidth, type.HandrailProfile == HandrailProfile.Round ? type.HandrailWidth : type.HandrailDepth);

        if (type.HandrailProfile == HandrailProfile.Round)
        {
            var (least, most) = us ? (32.0, 51.0) : (32.0, 50.0);
            if (width < least - 0.5 || width > most + 0.5)
                yield return $"Its handrail is {width:0} across: {(us ? "the IRC asks for 1¼ to 2 in (32 to 51)" : "Approved Document K asks for 32 to 50")} for a round one.";
        }
        else if (us)
        {
            var round = 2 * (width + depth);
            if (round < 102 - 0.5 || round > 159 + 0.5 || Math.Max(width, depth) > 57 + 0.5)
                yield return $"Its handrail is {width:0} by {depth:0}, {round:0} round: the IRC asks for 4 to 6¼ in (102 to 159) round and 2¼ in (57) across at most.";
        }
        else if (width > 50 + 0.5 || depth > 39 + 0.5)
            yield return $"Its handrail is {width:0} by {depth:0}: Approved Document K asks for one no more than 50 wide and 39 deep, or round.";

        if (StairModel.Railings(document, stair, layout).Any(railing => railing.OnWall))
        {
            var clear = type.WallClearance;
            if (us ? clear < 38 - 0.5 : clear < 50 - 0.5 || clear > 75 + 0.5)
                yield return $"Its handrail is {clear:0} off the wall: {(us ? "the IRC asks for 1½ in (38) at least" : "Approved Document K asks for 50 to 75")}.";
            if (us && type.HandrailEnds == HandrailEnds.Cut)
                yield return "Its handrail's ends are cut off: the IRC asks for them to return to the wall or the floor, or end on a post.";
        }

        // Guarding where it is level: in a dwelling 900, elsewhere 1100 off the flights.
        // The IBC's 42 in outside a home, under the ADA.
        var ibc = standard == ClearanceStandard.Ada && stair.Use != StairUse.Private;
        var guard = ibc ? 1067 : us ? 914 : stair.Use == StairUse.Private ? 900 : 1100;
        if (type.GuardHeight < guard - 0.5)
            yield return $"Its guarding is {type.GuardHeight:0} high where it is level: {(ibc ? "the IBC asks for 42 in (1067) outside a home (1015.3)" : us ? "the IRC asks for 36 in (914)" : $"Approved Document K asks for {guard}")} at least.";
    }

    /// <summary>
    /// A spiral stair's rules. England's Approved Document K points to BS 5395-2, whose stair
    /// for a house's main stair (its category B) is 800 wide, with a going of 190 along the
    /// middle, 120 at least 270 in from the column and 350 at most 270 in from the outside, and
    /// rises of 170 to 220. The IRC asks for 660 (26 in) wide, treads 171 (6¾ in) deep at the
    /// walkline 305 (12 in) from the column, that walkline 622 (24½ in) at most from the centre,
    /// and rises of 241 (9½ in) at most.
    /// </summary>
    private static IEnumerable<string> SpiralProblems(StairLayout layout, ClearanceStandard standard)
    {
        var (rise, width) = (layout.Rise, layout.Width);
        var going = layout.GoingAt ?? (_ => layout.Going);

        if (standard.IsUs())
        {
            if (width < 660 - 0.5) yield return $"It is {width:0} wide: the IRC asks for 660 (26 in) at least on a spiral stair.";
            var walkline = going(Math.Min(305, width));
            if (walkline < 171 - 0.5) yield return $"Its treads are {walkline:0} deep at the walkline 305 (12 in) from the column: the IRC asks for 171 (6¾ in) on a spiral stair.";
            var radius = layout.InnerRadius + Math.Min(305, width);
            if (radius > 622 + 0.5) yield return $"Its walkline is {radius:0} from the centre: the IRC allows 622 (24½ in) at most on a spiral stair.";
            if (rise > 241 + 0.5) yield return $"Each riser is {rise:0}: the IRC allows 241 (9½ in) at most on a spiral stair.";
            yield break;
        }

        if (width < 800 - 0.5) yield return $"It is {width:0} wide: BS 5395-2 asks for 800 on a house's main spiral stair, 600 on one to a single room.";
        var middle = going(width / 2);
        if (middle < 190 - 0.5) yield return $"Its going along the middle is {middle:0}: BS 5395-2 asks for 190 at least.";
        var inside = going(Math.Min(270, width / 2));
        if (inside < 120 - 0.5) yield return $"Its going 270 in from the column is {inside:0}: BS 5395-2 asks for 120 at least.";
        var outside = going(Math.Max(width - 270, width / 2));
        if (outside > 350 + 0.5) yield return $"Its going 270 in from the outside is {outside:0}: BS 5395-2 allows 350 at most.";
        if (rise < 170 - 0.5 || rise > 220 + 0.5) yield return $"Each riser is {rise:0}: BS 5395-2 asks for 170 to 220.";
    }

    /// <summary>
    /// What a stair is, in a line, as a tag reads it: its type's mark, its risers and their
    /// height, its treads and their going - "ST1: 15 R @ 200, 14 T @ 225".
    /// </summary>
    public static string Summary(BimDocument document, Stair stair)
    {
        var layout = Layout(document, stair);
        var mark = TypeOf(document, stair).TypeMark;
        var treads = layout.Steps.Where(step => step.Kind != StairStepKind.Landing).Select(step => step.Number).Distinct().Count();
        return $"{(string.IsNullOrWhiteSpace(mark) ? "" : mark + ": ")}{layout.Risers} R @ {layout.Rise:0.#}, {treads} T @ {layout.Going:0}";
    }

    /// <summary>Whether a stair meets its rules, as the property panel says it.</summary>
    public static string Verdict(BimDocument document, Stair stair)
    {
        var problems = Problems(document, stair);
        var rules = Label(document.ProjectInformation.ClearanceStandard);
        return problems.Count == 0 ? $"Meets {rules}" : $"{problems.Count} short of {rules}: {string.Join(" ", problems)}";
    }

    /// <summary>A new stair's height, its level's to the one above's, for the tool to show before it is put down.</summary>
    public static Guid? LevelAbove(BimDocument document, Guid levelId) => Levels.Above(document, levelId)?.Id;
}
