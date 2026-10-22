using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How a ramp is laid out in plan: one straight run; two at a quarter turn on a landing; two
/// doubling back on a landing, side by side - a switchback; or drawn by clicking along its
/// walking line, landing wherever it was clicked to turn.
/// </summary>
public enum RampShape
{
    Straight,
    QuarterTurn,
    HalfTurn,
    Custom
}

/// <summary>
/// How a ramp is built: solid - concrete on fill, down to the ground it rises from - or a slab
/// of its own thickness under its surface: a concrete slab, or a timber deck.
/// </summary>
public enum RampConstruction
{
    Solid,
    Slab
}

/// <summary>What fills a ramp's guarding under its top rail, where its side drops too far to be left open.</summary>
public enum RampGuarding
{
    Balusters,
    Glass
}

/// <summary>Which end of a ramp stays where it was put when it is laid out afresh: its foot, or its head - at the door or terrace it comes up to.</summary>
public enum RampAnchor
{
    Foot,
    Head
}

/// <summary>
/// A ramp type: how it is built - solid, or a slab or deck of its own thickness - the kerb along
/// its open sides, and its handrails: how high over its surface, how thick, how far they run on
/// past its ends, a second lower rail, and what fills its guarding where it is high off the ground.
/// </summary>
public sealed class RampType : ElementType
{
    public RampType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Ramps;

    public RampConstruction Construction { get; set; } = RampConstruction.Solid;

    /// <summary>A slab's or deck's depth under its surface; a solid ramp's depth under its foot, mm.</summary>
    public double Thickness { get; set; } = 150;

    /// <summary>How high the kerb along its open sides stands over its surface, mm: none for 0.</summary>
    public double KerbHeight { get; set; } = 100;

    /// <summary>How wide its kerb - or, with none, the edge its handrail posts stand on - is, outside its clear width, mm.</summary>
    public double KerbWidth { get; set; } = 100;

    /// <summary>Its handrails' tops over its surface - over the slope of a flight, measured straight up, mm.</summary>
    public double HandrailHeight { get; set; } = 900;

    /// <summary>A handrail's diameter, mm: 32 to 50 grips well.</summary>
    public double HandrailDiameter { get; set; } = 42;

    /// <summary>How far its handrails run on, level, past its foot and its head, mm.</summary>
    public double HandrailExtension { get; set; } = 300;

    /// <summary>Whether it has a second handrail 600 over its surface, for children and people of short stature.</summary>
    public bool LowerRail { get; set; }

    /// <summary>What fills its guarding under the top rail where a side drops too far to be left open.</summary>
    public RampGuarding Guarding { get; set; } = RampGuarding.Balusters;

    /// <summary>Its handrail posts' spacing at most, mm.</summary>
    public double PostSpacing { get; set; } = 1500;

    /// <summary>The longest its flights may go between landings, mm - shorter than its rules allow, as a client or a local rule asks; none for as the rules allow.</summary>
    public double LongestFlight { get; set; }

    /// <summary>Its colour when no material is given: concrete grey, or a deck's timber.</summary>
    public ColourRgb Colour { get; set; } = new(0xB5, 0xB3, 0xAE);

    public Guid MaterialId { get; set; }

    public Guid RailMaterialId { get; set; }

    public RampType Duplicate(string name) => WithCommonOf(new RampType(name)
    {
        Construction = Construction, Thickness = Thickness, KerbHeight = KerbHeight, KerbWidth = KerbWidth,
        HandrailHeight = HandrailHeight, HandrailDiameter = HandrailDiameter, HandrailExtension = HandrailExtension,
        LowerRail = LowerRail, Guarding = Guarding, PostSpacing = PostSpacing, LongestFlight = LongestFlight, Colour = Colour, MaterialId = MaterialId,
        RailMaterialId = RailMaterialId
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(RampTypeParameters.Construction,
            () => EnumText.Humanise(Construction),
            v => { if (EnumText.TryParse<RampConstruction>(v, out var construction)) Construction = construction; },
            EnumText.Choices<RampConstruction>());
        yield return Size(RampTypeParameters.Thickness, () => Thickness, v => Thickness = v, 50, 1000);
        yield return Size(RampTypeParameters.KerbHeight, () => KerbHeight, v => KerbHeight = v, 0, 300);
        yield return Size(RampTypeParameters.KerbWidth, () => KerbWidth, v => KerbWidth = v, 50, 500);
        yield return Size(RampTypeParameters.HandrailHeight, () => HandrailHeight, v => HandrailHeight = v, 600, 1200);
        yield return Size(RampTypeParameters.HandrailDiameter, () => HandrailDiameter, v => HandrailDiameter = v, 20, 80);
        yield return Size(RampTypeParameters.HandrailExtension, () => HandrailExtension, v => HandrailExtension = v, 0, 1000);
        yield return ParameterValue.Bind(RampTypeParameters.LowerRail, () => LowerRail, v => LowerRail = v);
        yield return ParameterValue.BindChoice(RampTypeParameters.Guarding,
            () => EnumText.Humanise(Guarding),
            v => { if (EnumText.TryParse<RampGuarding>(v, out var guarding)) Guarding = guarding; },
            EnumText.Choices<RampGuarding>());
        yield return Size(RampTypeParameters.PostSpacing, () => PostSpacing, v => PostSpacing = v, 300, 3000);
        yield return ParameterValue.BindValidated(RampTypeParameters.LongestFlight, () => LongestFlight, (double v) =>
        {
            if (v != 0 && (v < 500 || v > 50000)) return false;
            LongestFlight = v;
            return true;
        });

        if (document is not null)
        {
            var names = document.Materials.Select(material => material.Name).OrderBy(name => name).ToArray();
            ParameterValue Material(ParameterDefinition definition, string usual, Func<Guid> get, Action<Guid> set) =>
                ParameterValue.BindChoice(definition,
                    () => document.FindMaterial(get())?.Name ?? usual,
                    v => set(document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty),
                    names.Prepend(usual).ToArray());

            yield return Material(RampTypeParameters.Material, ComponentType.ByColour, () => MaterialId, id => MaterialId = id);
            yield return Material(RampTypeParameters.RailMaterial, "Stainless steel", () => RailMaterialId, id => RailMaterialId = id);
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
    /// The ramp types a project starts with: solid concrete on fill; a concrete slab; a timber
    /// deck; and concrete with glass guarding where it stands high.
    /// </summary>
    public static IReadOnlyList<RampType> Library() => new[]
    {
        new RampType("Ramp - Concrete") { TypeMark = "RA1", Cost = 2800m },
        new RampType("Ramp - Concrete Slab") { Construction = RampConstruction.Slab, Thickness = 200, TypeMark = "RA2", Cost = 3600m },
        new RampType("Ramp - Timber Deck")
        {
            Construction = RampConstruction.Slab, Thickness = 150, Colour = new ColourRgb(0x9C, 0x7A, 0x55), TypeMark = "RA3", Cost = 2400m
        },
        new RampType("Ramp - Concrete, Glass Guarding") { Guarding = RampGuarding.Glass, TypeMark = "RA4", Cost = 5200m }
    };
}

public static class RampTypeParameters
{
    public static readonly ParameterDefinition Construction = new("Construction", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Thickness = new("Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition KerbHeight = new("Kerb Height (0 for none)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition KerbWidth = new("Kerb Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailHeight = new("Handrail Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailDiameter = new("Handrail Diameter", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition HandrailExtension = new("Handrail Extension at Ends", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition LowerRail = new("Second Handrail at 600", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Guarding = new("Guarding", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition PostSpacing = new("Post Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition LongestFlight = new("Longest Flight (0 for the rules')", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition RailMaterial = new("Handrail Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
}

/// <summary>
/// A ramp from one height to another: where its foot is - or its head, at the door or terrace it
/// comes up to - and which way is up there; how wide it is between its kerbs; how steep; and
/// whether it runs straight, turns a quarter or doubles back on a landing, or follows a line
/// drawn. Its flights and landings it lays out itself from how high it rises, as the rules it
/// is checked against have them: no flight longer than its gradient allows, a landing between
/// each and wherever it turns, and one at its head. So it re-plans itself when either end moves.
/// </summary>
public sealed class Ramp : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Ramps;

    /// <summary>The middle of its foot - or, anchored by its head, of its head.</summary>
    public Point2D Start { get; set; }

    /// <summary>Which way is up there, anticlockwise from the X axis, degrees.</summary>
    public double Angle { get; set; } = 90;

    /// <summary>Which end stays put as it is laid out afresh.</summary>
    public RampAnchor Anchor { get; set; }

    public RampShape Shape { get; set; }

    /// <summary>Whether it turns left going up; otherwise right.</summary>
    public bool TurnsLeft { get; set; }

    /// <summary>Its clear width, between its kerbs or its handrails, mm.</summary>
    public double Width { get; set; } = 1200;

    /// <summary>The gap between the two runs of a switchback, mm.</summary>
    public double Gap { get; set; } = 300;

    /// <summary>
    /// A drawn ramp's walking line, as it was clicked: how long each stretch of it is, from its
    /// foot to its first turn and turn to turn - the last runs on as far as it needs - mm...
    /// </summary>
    public List<double> PathLengths { get; set; } = new();

    /// <summary>...and how far it turns at each, degrees, right going up - mirrored for one that turns left.</summary>
    public List<double> PathTurns { get; set; } = new();

    /// <summary>How steep it is, as one in so many: 15 for 1:15.</summary>
    public double Gradient { get; set; } = 15;

    public double BaseOffset { get; set; }

    /// <summary>The level its head reaches - its own, for one up from the ground to its floor; none for a ramp of its own rise.</summary>
    public Guid? TopLevelId { get; set; }

    public double TopOffset { get; set; }

    /// <summary>How high it rises with no top level, mm.</summary>
    public double UnconnectedHeight { get; set; } = 450;

    /// <summary>How long the level landing at its head is, mm: none where it comes up onto a floor or a terrace.</summary>
    public double TopLanding { get; set; } = 1200;

    /// <summary>How long the level landing at its foot is, mm: none where it starts from the ground or a floor.</summary>
    public double BottomLanding { get; set; }

    /// <summary>The building it serves: a dwelling - Approved Document M's Volume 1 - or any other, Volume 2, which asks more of it.</summary>
    public RailingBuilding Building { get; set; } = RailingBuilding.Dwelling;

    /// <summary>Whether it is outside the building. Set when it is put down outside the walls.</summary>
    public bool Outside { get; set; } = true;

    /// <summary>Which sides have a handrail - as its rules ask, or said.</summary>
    public StairRailings Railings { get; set; } = StairRailings.Auto;

    /// <summary>Whether one built as a slab or a deck is closed underneath - its sides taken down to the ground - so no one walks in under it.</summary>
    public bool ClosedUnderneath { get; set; }

    /// <summary>Which way is up at its anchored end.</summary>
    public Vector2D Up => new(Math.Cos(Angle * Math.PI / 180), Math.Sin(Angle * Math.PI / 180));

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var levels = document.Levels.Select(level => level.Name).ToArray();
        const string unconnected = "Unconnected";

        // A ramp goes up: no base level or offset that would put its foot at or above its head.
        bool Rises(Guid baseId, double baseOffset, Guid? topId, double topOffset) =>
            topId is not { } top || document.FindLevel(top) is not { } upper || document.FindLevel(baseId) is not { } lower
            || upper.Elevation + topOffset - (lower.Elevation + baseOffset) >= Ramps.MinimumRise - 0.5;

        yield return ParameterValue.BindChoiceValidated(RampParameters.BaseLevel,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v =>
            {
                if (document.Levels.FirstOrDefault(level => level.Name == v) is not { } level || !Rises(level.Id, BaseOffset, TopLevelId, TopOffset)) return false;
                LevelId = level.Id;
                return true;
            },
            levels);
        yield return ParameterValue.BindValidated(RampParameters.BaseOffset, () => BaseOffset, (double v) =>
        {
            if (!double.IsFinite(v) || !Rises(LevelId, v, TopLevelId, TopOffset)) return false;
            BaseOffset = v;
            return true;
        });
        yield return ParameterValue.BindChoiceValidated(RampParameters.TopLevel,
            () => TopLevelId is { } top ? document.FindLevel(top)?.Name ?? unconnected : unconnected,
            v =>
            {
                var level = document.Levels.FirstOrDefault(candidate => candidate.Name == v);
                if (level is not null && !Rises(LevelId, BaseOffset, level.Id, TopOffset)) return false;
                if (level is null && TopLevelId is not null) UnconnectedHeight = Math.Max(Ramps.MinimumRise, Ramps.Top(document, this) - Ramps.Bottom(document, this));
                TopLevelId = level?.Id;
                return true;
            },
            levels.Append(unconnected).ToArray());
        yield return ParameterValue.BindValidated(RampParameters.TopOffset, () => TopOffset, (double v) =>
        {
            if (!double.IsFinite(v) || !Rises(LevelId, BaseOffset, TopLevelId, v)) return false;
            TopOffset = v;
            return true;
        });
        yield return ParameterValue.BindValidated(RampParameters.UnconnectedHeight, () => UnconnectedHeight, (double v) =>
        {
            if (!double.IsFinite(v) || v < Ramps.MinimumRise || v > 10000) return false;
            UnconnectedHeight = v;
            return true;
        });

        yield return ParameterValue.BindChoice(RampParameters.Shape,
            () => EnumText.Humanise(Shape),
            v => { if (EnumText.TryParse<RampShape>(v, out var shape)) Shape = shape; },
            EnumText.Choices<RampShape>());
        yield return ParameterValue.BindChoice(RampParameters.Turns,
            () => TurnsLeft ? "Left" : "Right",
            v => TurnsLeft = v == "Left",
            new[] { "Left", "Right" });
        yield return ParameterValue.BindValidated(RampParameters.Width, () => Width, (double v) =>
        {
            if (v < 600 || v > 6000) return false;
            Width = v;
            return true;
        });
        yield return ParameterValue.BindValidated(RampParameters.Gap, () => Gap, (double v) =>
        {
            if (v < 0 || v > 5000) return false;
            Gap = v;
            return true;
        });
        yield return ParameterValue.BindValidated(RampParameters.Gradient, () => Gradient, (double v) =>
        {
            if (!double.IsFinite(v) || v < 4 || v > 100) return false;
            Gradient = v;
            return true;
        });
        yield return ParameterValue.BindValidated(RampParameters.TopLanding, () => TopLanding, (double v) =>
        {
            if (v < 0 || v > 10000) return false;
            TopLanding = v;
            return true;
        });
        yield return ParameterValue.BindValidated(RampParameters.BottomLanding, () => BottomLanding, (double v) =>
        {
            if (v < 0 || v > 10000) return false;
            BottomLanding = v;
            return true;
        });
        yield return ParameterValue.BindValidated(RampParameters.Angle, () => Angle, (double v) => { Angle = ((v % 360) + 360) % 360; return true; });
        yield return ParameterValue.BindChoice(RampParameters.Anchor,
            () => Anchor == RampAnchor.Head ? "Its head" : "Its foot",
            v =>
            {
                var anchor = v == "Its head" ? RampAnchor.Head : RampAnchor.Foot;
                if (anchor != Anchor) Ramps.Reanchor(document, this, anchor);
            },
            new[] { "Its foot", "Its head" });
        yield return ParameterValue.BindChoice(RampParameters.Railings,
            () => EnumText.Humanise(Railings),
            v => { if (EnumText.TryParse<StairRailings>(v, out var railings)) Railings = railings; },
            EnumText.Choices<StairRailings>());
        yield return ParameterValue.BindChoice(RampParameters.Building,
            () => EnumText.Humanise(Building),
            v => { if (EnumText.TryParse<RailingBuilding>(v, out var building)) Building = building; },
            EnumText.Choices<RailingBuilding>());
        yield return ParameterValue.Bind(RampParameters.Outside, () => Outside, v => Outside = v);
        yield return ParameterValue.Bind(RampParameters.ClosedUnderneath, () => ClosedUnderneath, v => ClosedUnderneath = v);

        // What it works out to, and whether that meets the regulations.
        var layout = Ramps.Layout(document, this);
        yield return ParameterValue.ReadOnly(RampParameters.Rise, () => Math.Round(layout.Rise, 1));
        yield return ParameterValue.ReadOnly(RampParameters.Flights, () => layout.FlightCount);
        yield return ParameterValue.ReadOnly(RampParameters.Run, () => Math.Round(layout.Run));
        yield return ParameterValue.ReadOnly(RampParameters.Length, () => Math.Round(layout.Length));
        yield return ParameterValue.ReadOnly(RampParameters.Summary, () => Ramps.Summary(document, this));
        yield return ParameterValue.ReadOnly(RampParameters.Rule, () => Ramps.Verdict(document, this));
        yield return ParameterValue.BindFlagCommand(RampParameters.MakeItPass, () => false,
            (bool make, out string? message) =>
            {
                message = null;
                return make ? RampFixes.MakeItPass(document, this, out message) : null;
            });

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class RampParameters
{
    public static readonly ParameterDefinition BaseLevel = new("Base Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BaseOffset = new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition TopLevel = new("Top Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition TopOffset = new("Top Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition UnconnectedHeight = new("Unconnected Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Shape = new("Shape", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Turns = new("Turns", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Gap = new("Gap Between Runs", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Gradient = new("Gradient (1 in)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition TopLanding = new("Landing at Head", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BottomLanding = new("Landing at Foot", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Angle = new("Direction", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Anchor = new("Stays Put By", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Railings = new("Handrails", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Building = new("Building", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Outside = new("Outside", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition ClosedUnderneath = new("Closed Underneath", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition MakeItPass = new("Make It Pass", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Rise = new("Rise", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Flights = new("Flights", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Run = new("Going of Its Flights", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Summary = new("Ramp Summary", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Rule = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>What a piece of a ramp is: a flight sloping up, or a level landing.</summary>
public enum RampPieceKind
{
    Flight,
    Landing
}

/// <summary>A point along one side of a ramp, with the height of its surface there.</summary>
public readonly record struct RampEdgePoint(Point2D At, double Height);

/// <summary>
/// A side of a ramp, left or right going up: its points, with its surface's height there, and for
/// each length between one and the next how far out from its clear width a wall's face stands
/// beside it - none where it is open.
/// </summary>
public sealed record RampSide(IReadOnlyList<RampEdgePoint> Points, IReadOnlyList<double?> Walls);

/// <summary>
/// One piece of a ramp as it is laid out: a flight or a landing; its outline in plan; where it
/// starts and ends along the walking line, and the walking line's points there; how high it is
/// at its foot and its head - the same, for a landing - and its left and right edges going up.
/// </summary>
public sealed record RampPiece(
    RampPieceKind Kind, IReadOnlyList<Point2D> Outline, double From, double To, double Low, double High,
    Point2D Start, Point2D End, IReadOnlyList<RampEdgePoint> Left, IReadOnlyList<RampEdgePoint> Right)
{
    /// <summary>How far it runs along the walking line: a flight's going.</summary>
    public double Going => To - From;

    public double Rise => High - Low;

    /// <summary>Its surface's height over a point: level on a landing; along a flight, as far up it as the point is.</summary>
    public double HeightAt(Point2D at)
    {
        if (Kind == RampPieceKind.Landing) return Low;
        var way = End - Start;
        var length = way.Dot(way);
        return length <= 1e-9 ? Low : Low + (High - Low) * Math.Clamp((at - Start).Dot(way) / length, 0, 1);
    }
}

/// <summary>
/// What a ramp's rules ask of how it is laid out: the longest a flight may go at its gradient,
/// how long a landing between flights and at a turn must be, and how wide a landing between
/// flights - a passing place - must be, if wider than the ramp.
/// </summary>
public sealed record RampRules(double MaxGoing, double MidLanding, double TurnLanding, double LandingWidth)
{
    /// <summary>The shortest stretch it slopes along: anything shorter between two landings is landing too.</summary>
    public double MinFlight { get; init; } = 500;
}

/// <summary>
/// A ramp worked out: how high it rises and how steep it is; its flights and landings in plan,
/// one after another up it, with their heights; the line walked up it; its two sides, left and
/// right going up, with the height of its surface along them; and where its foot and head are.
/// </summary>
public sealed class RampLayout
{
    public double Bottom { get; init; }
    public double Top { get; init; }
    public double Rise => Top - Bottom;

    /// <summary>How steep its flights are, as one in so many.</summary>
    public double Gradient { get; init; }

    public double Width { get; init; }

    /// <summary>How far its flights go in all, mm.</summary>
    public double Run { get; init; }

    /// <summary>How far it is from its foot to its head along the line walked, mm.</summary>
    public double Length { get; init; }

    /// <summary>A turning landing's size - square, at least as long as its rules ask.</summary>
    public double TurnSize { get; init; }

    public IReadOnlyList<RampPiece> Pieces { get; init; } = Array.Empty<RampPiece>();

    /// <summary>The line walked up it, foot to head.</summary>
    public IReadOnlyList<Point2D> Walk { get; init; } = Array.Empty<Point2D>();

    /// <summary>Its left side going up, at its clear width, with its surface's height along it.</summary>
    public IReadOnlyList<RampEdgePoint> Left { get; init; } = Array.Empty<RampEdgePoint>();

    public IReadOnlyList<RampEdgePoint> Right { get; init; } = Array.Empty<RampEdgePoint>();

    public Point2D Foot { get; init; }
    public Vector2D FootUp { get; init; }
    public Point2D Head { get; init; }
    public Vector2D HeadUp { get; init; }

    public IReadOnlyList<RampPiece> Flights => Pieces.Where(piece => piece.Kind == RampPieceKind.Flight).ToList();

    public int FlightCount => Pieces.Count(piece => piece.Kind == RampPieceKind.Flight);

    /// <summary>Where it comes up at its head: across its end, right to left.</summary>
    public (Point2D From, Point2D To) HeadEdge => Left.Count == 0 ? (Head, Head) : (Right[^1].At, Left[^1].At);

    /// <summary>Where it starts at its foot: across its end, left to right.</summary>
    public (Point2D From, Point2D To) FootEdge => Left.Count == 0 ? (Foot, Foot) : (Left[0].At, Right[0].At);

    /// <summary>Its outline in plan, at its clear width: up its left side and back down its right.</summary>
    public IReadOnlyList<Point2D> Footprint => Left.Select(point => point.At).Concat(Right.Select(point => point.At).Reverse()).ToList();

    /// <summary>The piece of it over a point in plan, if any.</summary>
    public RampPiece? PieceAt(Point2D at) => Pieces.FirstOrDefault(piece => piece.Outline.Count >= 3 && Polygon2D.Contains(piece.Outline, at));

    /// <summary>
    /// A ramp laid out from one height to another under its rules. Its walking line runs from its
    /// foot - leg by leg, turning where its shape or its drawing says, the last leg running on as
    /// far as it must. Up it, the flights in each stretch between turns are each as long as the
    /// rules let them be at its gradient, a landing between them; each turn is a landing, square
    /// round the corner; and at its head a landing as long as it is told. Anchored by its head, it
    /// is laid out so and then moved, its head where it is put, going up the way it is told - but a
    /// drawn ramp hung by its head, drawn down from a door, is laid out from its head down the line
    /// clicked, its last leg running on as far as it must to its foot, and then turned round.
    /// </summary>
    public static RampLayout Of(Ramp ramp, double bottom, double top, RampRules rules)
    {
        var width = Math.Max(300, ramp.Width);
        var half = width / 2;
        var rise = Math.Max(0, top - bottom);
        var x = Math.Max(1, ramp.Gradient);
        var run = rise * x;
        var going = Math.Max(rules.MinFlight, rules.MaxGoing);
        var mid = Math.Max(0, rules.MidLanding);
        var size = Math.Max(width, rules.TurnLanding);
        var mirror = ramp.TurnsLeft ? -1.0 : 1.0;
        var footLanding = Math.Max(0, ramp.BottomLanding);
        var headLanding = Math.Max(0, ramp.TopLanding);
        var flights = run <= 1e-6 ? 0 : Math.Max(1, (int)Math.Ceiling(run / going - 1e-9));

        // Laid out from its head down, drawn so: its head's landing first, its foot's last.
        var down = ramp.Anchor == RampAnchor.Head && ramp.Shape == RampShape.Custom;
        var (startLanding, endLanding) = down ? (headLanding, footLanding) : (footLanding, headLanding);

        // Its walking line: so long to each turn, turning so far there - right going up, positive.
        var lengths = new List<double>();
        var turns = new List<double>();
        if (ramp.Shape is RampShape.QuarterTurn or RampShape.HalfTurn && run > 1e-6)
        {
            // Its flights shared between its runs, the first taking the odd one - or, hung by its
            // head from a door, the last, so a switchback's foot comes out short of its head.
            flights = Math.Max(flights, 2);
            var first = ramp.Anchor == RampAnchor.Head ? flights / 2 : (flights + 1) / 2;
            lengths.Add(footLanding + first * (run / flights) + (first - 1) * mid + (size - half));
            turns.Add(90 * mirror);
            if (ramp.Shape == RampShape.HalfTurn)
            {
                lengths.Add(width + Math.Max(0, ramp.Gap));
                turns.Add(90 * mirror);
            }
        }
        else if (ramp.Shape == RampShape.Custom)
            for (var i = 0; i < ramp.PathTurns.Count && i < ramp.PathLengths.Count; i++)
            {
                lengths.Add(double.IsFinite(ramp.PathLengths[i]) ? Math.Max(0, ramp.PathLengths[i]) : 0);
                turns.Add((double.IsFinite(ramp.PathTurns[i]) ? Math.Clamp(ramp.PathTurns[i], -90, 90) : 0) * mirror);
            }

        // Where along it each turn is, and how far the landing round it reaches either side.
        var corners = new List<double>();
        var reached = 0.0;
        foreach (var length in lengths) corners.Add(reached += length);
        double Reach(double turn) =>
            Math.Abs(Math.Abs(turn) - 90) < 1 ? size - half
            : Math.Abs(turn) < 1 ? mid / 2
            : Math.Max(rules.TurnLanding / 2, half * Math.Tan(Math.Abs(turn) * Math.PI / 360));
        var blocks = new List<(double From, double To)>();
        if (startLanding > 0) blocks.Add((0, startLanding));
        for (var j = 0; j < corners.Count; j++) blocks.Add((Math.Max(0, corners[j] - Reach(turns[j])), corners[j] + Reach(turns[j])));
        blocks = Merged(blocks, rules.MinFlight);

        // Up it, stretch by stretch: flights as long as they may be with landings between, until it is as high as it goes.
        var spans = new List<(bool Flight, double From, double To, double Low, double High)>();
        var (at, done) = (0.0, 0.0);
        double Height() => run <= 1e-9 ? (down ? bottom + rise : bottom) : bottom + rise * (down ? 1 - Math.Min(1, done / run) : Math.Min(1, done / run));
        void Level(double from, double to)
        {
            if (to - from <= 1e-6) return;
            if (spans.Count > 0 && !spans[^1].Flight && Math.Abs(spans[^1].To - from) < 1e-6) spans[^1] = (false, spans[^1].From, to, spans[^1].Low, spans[^1].High);
            else spans.Add((false, from, to, Height(), Height()));
        }

        void Place(int count, double each)
        {
            for (var k = 0; k < count; k++)
            {
                if (k > 0)
                {
                    Level(at, at + mid);
                    at += mid;
                }

                var low = Height();
                done += each;
                spans.Add((true, at, at + each, low, Height()));
                at += each;
            }
        }

        foreach (var (from, to) in blocks.Append((double.PositiveInfinity, double.PositiveInfinity)))
        {
            var remaining = run - done;
            if (remaining > 1e-6)
            {
                var available = from - at;
                var rest = Math.Max(1, (int)Math.Ceiling(remaining / going - 1e-9));
                if (remaining + (rest - 1) * mid <= available + 1e-6) Place(rest, remaining / rest);
                else if (available > 0)
                {
                    var count = Math.Max(1, (int)Math.Ceiling((available + mid) / (going + mid) - 1e-6));
                    var each = (available - (count - 1) * mid) / count;
                    if (each >= rules.MinFlight) Place(count, each);
                }
            }

            if (run - done <= 1e-6 || double.IsInfinity(from)) break;

            // The landing here - and whatever was too short to slope before it.
            Level(at, to);
            at = to;
        }

        // The landing at its far end - its head, or laid out down, its foot - round any turn it reaches into.
        var end = at + endLanding;
        foreach (var (from, to) in blocks)
            if (to > at + 1e-6 && from < end - 1e-6) end = Math.Max(end, to);
        Level(at, end);
        var total = Math.Max(end, 10);

        // Its legs as far as it goes, laid out from its foot at the origin going along X.
        var used = corners.Count(corner => corner < total - 1e-6);
        var vertices = new List<Point2D> { new(0, 0) };
        var headings = new List<double> { 0 };
        for (var j = 0; j < used; j++)
        {
            vertices.Add(vertices[^1] + Way(headings[^1]) * lengths[j]);
            headings.Add(headings[^1] - turns[j] * Math.PI / 180);
        }

        (Point2D At, Vector2D Way) On(double s, bool after)
        {
            var leg = 0;
            for (var j = 0; j < used; j++)
                if (after ? corners[j] <= s + 1e-9 : corners[j] < s - 1e-9) leg = j + 1;
            var start = leg == 0 ? 0 : corners[leg - 1];
            var way = Way(headings[leg]);
            return (vertices[leg] + way * (s - start), way);
        }

        // Where it is in the building: from its foot - or moved so its head is where it is put, going up the way it is told.
        var headLocal = On(total, false);
        var turn = down ? ramp.Angle * Math.PI / 180 + Math.PI : ramp.Angle * Math.PI / 180 - (ramp.Anchor == RampAnchor.Head ? headings[^1] : 0);
        var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
        Vector2D Turned(Vector2D v) => new(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        var zero = new Point2D(0, 0);
        var origin = ramp.Anchor == RampAnchor.Head && !down ? ramp.Start - Turned(headLocal.At - zero) : ramp.Start;
        Point2D Placed(Point2D p) => origin + Turned(p - zero);

        // Each piece: its edges either side, and its outline between them.
        var pieces = new List<RampPiece>();
        for (var i = 0; i < spans.Count; i++)
        {
            var (flight, from, to, low, high) = spans[i];
            var (a, wayA) = On(from, true);
            var (b, wayB) = On(to, false);
            List<RampEdgePoint> Edge(double side)
            {
                var points = new List<Point2D> { a + wayA.PerpendicularLeft() * (side * half) };
                if (!flight)
                {
                    // A passing place between flights, wider than the ramp.
                    var between = i > 0 && i < spans.Count - 1;
                    var inside = Enumerable.Range(0, used).Where(j => corners[j] > from + 1e-6 && corners[j] < to - 1e-6).ToList();
                    var wide = Math.Max(half, rules.LandingWidth / 2);
                    if (between && inside.Count == 0 && wide > half + 1)
                    {
                        points.Add(a + wayA.PerpendicularLeft() * (side * wide));
                        points.Add(b + wayB.PerpendicularLeft() * (side * wide));
                    }

                    foreach (var j in inside)
                    {
                        var corner = vertices[j + 1];
                        var (wayIn, wayOut) = (Way(headings[j]), Way(headings[j + 1]));
                        var (normalIn, normalOut) = (wayIn.PerpendicularLeft(), wayOut.PerpendicularLeft());
                        var inner = -turns[j] * side > 0;
                        if (inner && Math.Abs(Math.Abs(turns[j]) - 90) < 1)
                            points.Add(corner + wayIn * (half - size) + normalIn * (side * (size - half)));
                        else
                            points.Add(corner + (normalIn + normalOut) * (side * half / Math.Max(0.3, 1 + normalIn.Dot(normalOut))));
                    }
                }

                points.Add(b + wayB.PerpendicularLeft() * (side * half));
                var tidy = flight ? points : Tidied(points);
                return tidy.Select((point, k) => new RampEdgePoint(Placed(point), flight ? (k == 0 ? low : high) : low)).ToList();
            }

            var left = Edge(1);
            var right = Edge(-1);
            var outline = left.Select(point => point.At).Concat(right.Select(point => point.At).Reverse()).ToList();
            pieces.Add(new RampPiece(flight ? RampPieceKind.Flight : RampPieceKind.Landing, outline, from, to, low, high, Placed(a), Placed(b), left, right));
        }

        static List<RampEdgePoint> Joined(IEnumerable<IReadOnlyList<RampEdgePoint>> edges)
        {
            var joined = new List<RampEdgePoint>();
            foreach (var point in edges.SelectMany(edge => edge))
                if (joined.Count == 0 || joined[^1].At.DistanceTo(point.At) > 0.5 || Math.Abs(joined[^1].Height - point.Height) > 0.5) joined.Add(point);
            return joined;
        }

        var walk = vertices.Take(used + 1).Append(headLocal.At).Select(Placed).ToList();
        var (foot, footUp, head, headUp) = (Placed(zero), Turned(Way(0)), Placed(headLocal.At), Turned(Way(headings[^1])));
        if (down)
        {
            // Laid out down from its head: turned round to run up from its foot, its sides changing over.
            pieces = pieces.Select(piece => piece with
            {
                From = total - piece.To, To = total - piece.From, Low = piece.High, High = piece.Low, Start = piece.End, End = piece.Start,
                Left = piece.Right.Reverse().ToList(), Right = piece.Left.Reverse().ToList()
            }).Reverse().ToList();
            walk.Reverse();
            (foot, footUp, head, headUp) = (head, headUp * -1, foot, footUp * -1);
        }

        return new RampLayout
        {
            Bottom = bottom,
            Top = bottom + rise,
            Gradient = x,
            Width = width,
            Run = run,
            Length = total,
            TurnSize = size,
            Pieces = pieces,
            Walk = walk.Where((point, k) => k == 0 || point.DistanceTo(walk[k - 1]) > 0.5).ToList(),
            Left = Joined(pieces.Select(piece => piece.Left)),
            Right = Joined(pieces.Select(piece => piece.Right)),
            Foot = foot,
            FootUp = footUp,
            Head = head,
            HeadUp = headUp
        };
    }

    private static Vector2D Way(double heading) => new(Math.Cos(heading), Math.Sin(heading));

    /// <summary>Landings that overlap, or come so close that what is between is too short to slope, as one.</summary>
    private static List<(double From, double To)> Merged(IEnumerable<(double From, double To)> blocks, double least)
    {
        var merged = new List<(double From, double To)>();
        foreach (var block in blocks.OrderBy(block => block.From))
            if (merged.Count > 0 && block.From < merged[^1].To + least) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, block.To));
            else merged.Add(block);
        return merged;
    }

    /// <summary>An edge with its doubled points taken out, and those on a straight line - or back along one - between their neighbours.</summary>
    private static List<Point2D> Tidied(List<Point2D> points)
    {
        var tidy = new List<Point2D>();
        foreach (var point in points)
            if (tidy.Count == 0 || tidy[^1].DistanceTo(point) > 0.5) tidy.Add(point);
        for (var changed = true; changed && tidy.Count > 2;)
        {
            changed = false;
            for (var i = 1; i + 1 < tidy.Count; i++)
            {
                var (before, after) = (tidy[i] - tidy[i - 1], tidy[i + 1] - tidy[i]);
                if (Math.Abs(before.Cross(after)) > 1e-3 * Math.Max(1, before.Length * after.Length)) continue;
                tidy.RemoveAt(i);
                changed = true;
                break;
            }
        }

        return tidy;
    }
}

/// <summary>
/// Ramps: how they are laid out under their rules, and whether they meet them. Approved Document M
/// (Volume 1, a dwelling's approach; Volume 2, any other building's ramped access): no steeper
/// than 1:12; each flight no longer than its gradient allows - 2 m at 1:12, 5 m at 1:15, 10 m at
/// 1:20, and between pro rata - nor rising more than 500; landings at the foot and head 1200 long
/// clear of any door swing, between flights 1200 in a dwelling and 1500 elsewhere - 1800 square,
/// as passing places, where there are three flights or more; 1500 wide between kerbs in a
/// building other than a dwelling, 900 in a dwelling's private approach; handrails 900 to 1000
/// over its surface, both sides, running on 300 past its ends; a kerb 100 high on its open sides;
/// and steps as well, or a lift, past 300 or 2 m of rise. The ADA (405): 1:12 at most, 30 in of
/// rise to a run, landings 60 in long, 36 in clear, handrails both sides past 6 in of rise and
/// edge protection. Steeper than 1:20 it is a ramp; gentler, a sloping path, and none of this asked.
/// </summary>
public static class Ramps
{
    private static readonly RampType Fallback = new("Ramp");

    /// <summary>The least a ramp may rise, mm.</summary>
    public const double MinimumRise = 10;

    public static RampType TypeOf(BimDocument document, Ramp ramp) => document.FindType<RampType>(ramp.TypeId) ?? Fallback;

    private static bool Us(BimDocument document) => document.ProjectInformation.ClearanceStandard.IsUs();

    /// <summary>Whether it is a home's ramp in the US: held to the IRC (R311.8) rather than the ADA.</summary>
    private static bool Irc(BimDocument document, Ramp ramp) => Us(document) && ramp.Building == RailingBuilding.Dwelling;

    /// <summary>Whether it turns: no one at one end can see the other, so in a building other than a dwelling its landings are passing places.</summary>
    private static bool Turns(Ramp ramp) =>
        ramp.Shape is RampShape.QuarterTurn or RampShape.HalfTurn || ramp.Shape == RampShape.Custom && ramp.PathTurns.Any(turn => Math.Abs(turn) >= 1);

    /// <summary>The height of its foot.</summary>
    public static double Bottom(BimDocument document, Ramp ramp) => (document.FindLevel(ramp.LevelId)?.Elevation ?? 0) + ramp.BaseOffset;

    /// <summary>The height of its head: its top level's, offset - or its foot's and its own rise.</summary>
    public static double Top(BimDocument document, Ramp ramp) =>
        ramp.TopLevelId is { } top && document.FindLevel(top) is { } level
            ? level.Elevation + ramp.TopOffset
            : Bottom(document, ramp) + ramp.UnconnectedHeight;

    /// <summary>
    /// The steepest it may be, as 1 in so many: 1:12 (Approved Document M, the ADA); a wheelchair
    /// user's dwelling's ramp 1:15 (M4(3) 3.10a); under the IRC, a home's ramp up to the door it is
    /// left by 1:12, any other 1:8 (R311.8.1).
    /// </summary>
    public static double SteepestAllowed(BimDocument document, Ramp ramp) =>
        Irc(document, ramp) ? (UpToADoorOut(document, ramp, Layout(document, ramp)) ? 12 : 8)
        : !Us(document) && ramp.Building == RailingBuilding.Dwelling && document.ProjectInformation.ClearanceStandard == ClearanceStandard.ApprovedDocumentM3 ? 15
        : 12;

    /// <summary>Whether its gradient makes it a ramp: steeper than 1:20. Gentler, it is a sloping path.</summary>
    public static bool IsRamp(Ramp ramp) => ramp.Gradient < 20 - 1e-9;

    /// <summary>
    /// What its rules ask of its layout at its gradient. Approved Document M (Table 1 of Volume 2,
    /// Diagram 2.1 of Volume 1): a flight goes 2 m at 1:12, 5 m at 1:15 and 10 m at 1:20, and
    /// between pro rata - 1:14 for 4 m; gentler than 1:20, a building other than a dwelling still
    /// lands each 500 of rise. Its landings: 1200 between flights in a dwelling, 1500 elsewhere -
    /// 1800 square, passing places, where there are three flights or more or one end cannot be
    /// seen from the other round a turn - and never shorter than the ramp is wide (Approved
    /// Document K, Diagram 2.2). The ADA: 30 in (760) of rise to a run, landings 60 in (1525). The
    /// IRC, a home's: no limit to a run, landings 36 in (915) where it turns. Its type may set a
    /// shorter longest flight.
    /// </summary>
    public static RampRules Rules(BimDocument document, Ramp ramp, int flights = 0)
    {
        var x = Math.Max(1, ramp.Gradient);
        var width = Math.Max(300, ramp.Width);
        var longest = TypeOf(document, ramp).LongestFlight;
        RampRules Limited(RampRules rules) => longest > 0 ? rules with { MaxGoing = Math.Min(rules.MaxGoing, longest) } : rules;

        if (Irc(document, ramp)) return Limited(new RampRules(double.PositiveInfinity, 915, 915, 0));
        if (Us(document)) return Limited(new RampRules(IsRamp(ramp) ? 760 * x : double.PositiveInfinity, 1525, 1525, 0));

        var going = IsRamp(ramp)
            ? (Math.Clamp(x, 12, 20) - 10) * 1000
            : ramp.Building == RailingBuilding.Other ? 500 * x : double.PositiveInfinity;
        if (ramp.Building == RailingBuilding.Dwelling) return Limited(new RampRules(going, Math.Max(1200, width), Math.Max(1200, width), 0));
        var passing = flights >= 3 || Turns(ramp);
        var landing = Math.Max(passing ? 1800 : 1500, width);
        return Limited(new RampRules(going, landing, landing, passing ? 1800 : 0));
    }

    /// <summary>
    /// How long its landings at its foot and head must be: 1200, Approved Document M says - and as
    /// long as the ramp is wide, Approved Document K; the ADA 60 in (1525); the IRC 36 in (915).
    /// </summary>
    public static double EndLanding(BimDocument document, Ramp ramp) =>
        Irc(document, ramp) ? 915 : Us(document) ? 1525 : Math.Max(1200, Math.Max(300, ramp.Width));

    /// <summary>The narrowest it may be between its kerbs or handrails, and why - none under the IRC, which asks no width of a home's ramp.</summary>
    public static (double Width, string Rule) LeastWidth(BimDocument document, Ramp ramp) =>
        Irc(document, ramp) ? (0, "")
        : Us(document) ? (915, "the ADA asks for 36 in (915) clear between its handrails")
        : ramp.Building == RailingBuilding.Other ? (1500, "Approved Document M asks for 1.5 m between its kerbs")
        : document.ProjectInformation.ClearanceStandard == ClearanceStandard.ApprovedDocumentM3 ? (1200, "Approved Document M asks a wheelchair user's ramp for 1200 clear (M4(3) 3.10c)")
        : (900, "Approved Document M asks a dwelling's ramp for 900 clear - 1200 on a communal approach");

    /// <summary>The headroom it must have over its surface, and why: 2 m, Approved Document K; 80 in (2030) under the ADA and the IRC.</summary>
    public static (double Height, string Rule) HeadroomAsked(BimDocument document) =>
        Us(document) ? (2030, "80 in (2030) of headroom is asked over it") : (2000, "Approved Document K asks for 2 m of headroom over it");

    /// <summary>
    /// Laid out under its rules: in a building other than a dwelling, those of three flights or
    /// more are laid out again with their landings between as passing places.
    /// </summary>
    public static RampLayout Layout(BimDocument document, Ramp ramp)
    {
        var (bottom, top) = (Bottom(document, ramp), Top(document, ramp));
        var layout = RampLayout.Of(ramp, bottom, top, Rules(document, ramp));
        if (!Us(document) && ramp.Building == RailingBuilding.Other && layout.FlightCount >= 3 && !Turns(ramp))
            layout = RampLayout.Of(ramp, bottom, top, Rules(document, ramp, layout.FlightCount));
        return layout;
    }

    /// <summary>
    /// Which sides its rules ask a handrail on: Approved Document M, both, in a building other than
    /// a dwelling; in a dwelling, Approved Document K, none up to 600 of rise, one side of a ramp
    /// narrower than 1 m, both of one wider. The ADA, both past 6 in (150) of rise. None on a sloping path.
    /// </summary>
    public static (bool Left, bool Right, string Rule) HandrailsAsked(BimDocument document, Ramp ramp, RampLayout layout)
    {
        if (!IsRamp(ramp)) return (false, false, "a path no steeper than 1:20 is not a ramp");
        if (Irc(document, ramp))
            return ramp.Gradient < 12 - 1e-9
                ? (true, false, "the IRC asks for a handrail on at least one side of a ramp steeper than 1:12")
                : (false, false, "the IRC asks for none on a ramp of 1:12 or gentler");
        if (Us(document))
            return layout.Rise > 150
                ? (true, true, "the ADA asks for handrails on both sides of a ramp rising more than 6 in")
                : (false, false, "the ADA asks for none on a ramp rising 6 in or less");
        if (ramp.Building == RailingBuilding.Other) return (true, true, "Approved Document M asks for a handrail on both sides");
        if (layout.Rise <= 600) return (false, false, "Approved Document K asks for none on a ramp rising 600 or less");
        return layout.Width >= 1000
            ? (true, true, "Approved Document K asks for handrails on both sides of a ramp 1 m wide or more")
            : (true, false, "Approved Document K asks for a handrail on one side of a ramp narrower than 1 m");
    }

    /// <summary>Which sides have a handrail: as its rules ask, or as it is told.</summary>
    public static (bool Left, bool Right) Handrails(BimDocument document, Ramp ramp, RampLayout layout)
    {
        var asked = HandrailsAsked(document, ramp, layout);
        return ramp.Railings switch
        {
            StairRailings.BothSides => (true, true),
            StairRailings.LeftSide => (true, false),
            StairRailings.RightSide => (false, true),
            StairRailings.None => (false, false),
            _ => (asked.Left, asked.Right)
        };
    }

    /// <summary>
    /// How high its guarding must stand where its side drops too far, and why. Approved Document K:
    /// 900 in a dwelling; elsewhere 900 along a flight and 1100 round a landing. The IRC's 36 in
    /// (914) round a home; the IBC's 42 in (1067) elsewhere.
    /// </summary>
    public static (double Height, string Rule) GuardHeight(BimDocument document, Ramp ramp, bool landing)
    {
        if (Us(document))
            return ramp.Building == RailingBuilding.Dwelling ? (914, "the IRC asks for 36 in (914)") : (1067, "the IBC asks for 42 in (1067)");
        if (ramp.Building == RailingBuilding.Dwelling) return (900, "Approved Document K asks for 900 in a dwelling");
        return landing ? (1100, "Approved Document K asks for 1100 round a landing") : (900, "Approved Document K asks for 900 along a flight");
    }

    /// <summary>
    /// How far a point beside it is below a height: down to the floor or the ground there, or -
    /// with neither modelled - to the height of its foot, where the ground is taken to be.
    /// </summary>
    public static double DropBeside(BimDocument document, RampLayout layout, Point2D beside, double height)
    {
        // The ground there, however high - a bank higher than it is no drop - and a floor or a ramp under it, or just over it:
        // the other run of a switchback beside it.
        var under = new List<double>();
        if (Terrains.GroundAt(document, beside) is { } ground) under.Add(ground);
        foreach (var floor in document.All<Slab>().Where(slab => slab.Boundary.Count >= 3 && slab.Contains(beside) && FloorSurfaces.IsStoodOn(document, slab)))
        {
            var top = FloorSurfaces.TopAt(document, floor, beside);
            if (top <= height + Stairs.FloorReach) under.Add(top);
        }

        if (SurfaceAt(document, beside, height, Terrains.Reach) is { } ramp && ramp <= height + Stairs.FloorReach) under.Add(ramp);
        return height - (under.Count > 0 ? under.Max() : GroundUnder(document, beside));
    }

    /// <summary>The surface of a ramp over a point within reach of a height - the highest, where one passes over another - if any.</summary>
    public static double? SurfaceAt(BimDocument document, Point2D at, double height, double reach)
    {
        double? highest = null;
        foreach (var ramp in document.All<Ramp>())
        {
            if (Layout(document, ramp).PieceAt(at) is not { } piece) continue;
            var top = piece.HeightAt(at);
            if (Math.Abs(top - height) <= reach && (highest is null || top > highest)) highest = top;
        }

        return highest;
    }

    /// <summary>The drop beside its sides at its worst, left and right going up - none along a wall.</summary>
    public static (double Left, double Right) Drops(BimDocument document, Ramp ramp, RampLayout layout)
    {
        var reach = TypeOf(document, ramp).KerbWidth + 200;
        var sides = Sides(document, layout);
        double Worst(RampSide side, double outward)
        {
            var worst = 0.0;
            for (var i = 0; i + 1 < side.Points.Count; i++)
            {
                if (side.Walls[i] is not null) continue;
                var (a, b) = (side.Points[i], side.Points[i + 1]);
                var across = (b.At - a.At).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (outward * reach);
                foreach (var (at, height) in new[] { (a.At, a.Height), (a.At.MidpointTo(b.At), (a.Height + b.Height) / 2), (b.At, b.Height) })
                    worst = Math.Max(worst, DropBeside(document, layout, at + across, height));
            }

            return worst;
        }

        return (Worst(sides.Left, 1), Worst(sides.Right, -1));
    }

    /// <summary>How far out from its clear width a wall beside it may stand and still be the side of it, mm.</summary>
    public const double WallReach = 300;

    /// <summary>
    /// Its two sides, left and right going up, and where walls stand along them: each side's points
    /// - split where a wall beside it starts or stops - with its surface's height there, and for
    /// each length between them how far out from its clear width the face of the wall beside it is:
    /// a wall as high as a handrail over it, standing no higher than a floor's reach above it. None
    /// where the side is open. Along a wall it needs no kerb, and its handrail is on the wall.
    /// </summary>
    public static (RampSide Left, RampSide Right) Sides(BimDocument document, RampLayout layout)
    {
        // Only the walls near it.
        var footprint = layout.Footprint;
        var bands = footprint.Count == 0 ? new List<StairModel.WallBand>() : StairModel.WallBands(document).Where(band =>
            band.Max.X >= footprint.Min(point => point.X) - WallReach - 100 && band.Min.X <= footprint.Max(point => point.X) + WallReach + 100
            && band.Max.Y >= footprint.Min(point => point.Y) - WallReach - 100 && band.Min.Y <= footprint.Max(point => point.Y) + WallReach + 100).ToList();

        RampSide Side(IReadOnlyList<RampEdgePoint> side, double outward)
        {
            if (bands.Count == 0 || side.Count < 2) return new RampSide(side, new double?[Math.Max(0, side.Count - 1)]);

            var points = new List<RampEdgePoint> { side[0] };
            var walls = new List<double?>();
            for (var i = 0; i + 1 < side.Count; i++)
            {
                var (a, b) = (side[i], side[i + 1]);
                var length = a.At.DistanceTo(b.At);
                if (length < 1)
                {
                    points.Add(b);
                    walls.Add(null);
                    continue;
                }

                var outwards = (b.At - a.At).PerpendicularLeft() / length * outward;
                RampEdgePoint At(double t) => new(a.At + (b.At - a.At) * t, a.Height + (b.Height - a.Height) * t);
                double? Face(RampEdgePoint sample)
                {
                    for (var reach = 0.0; reach <= WallReach + 1e-9; reach += 25)
                    {
                        var probe = sample.At + outwards * reach;
                        if (bands.Any(band => band.Base <= sample.Height + Stairs.FloorReach && band.Top >= sample.Height + 900 && band.Holds(probe))) return reach;
                    }

                    return null;
                }

                // Every 100 or so along it, the wall beside it there or none - and the length split where that changes.
                var count = Math.Max(1, (int)Math.Ceiling(length / 100));
                var faces = Enumerable.Range(0, count).Select(k => Face(At((k + 0.5) / count))).ToList();
                for (var k = 0; k < count;)
                {
                    var walled = faces[k] is not null;
                    var next = k;
                    while (next < count && faces[next] is not null == walled) next++;
                    points.Add(next == count ? b : At((double)next / count));
                    walls.Add(walled ? faces.Skip(k).Take(next - k).Max() : null);
                    k = next;
                }
            }

            return new RampSide(points, walls);
        }

        return (Side(layout.Left, 1), Side(layout.Right, -1));
    }

    /// <summary>The ground at a point: the ground modelled there, or - with none - the lowest level's, which the building stands on.</summary>
    public static double GroundUnder(BimDocument document, Point2D at) =>
        Terrains.GroundAt(document, at) ?? (document.Levels.Count == 0 ? 0 : document.Levels.Min(level => level.Elevation));

    /// <summary>The surface of a ramp on a storey over a point - the highest, where one passes over another - if any.</summary>
    public static double? SurfaceOn(BimDocument document, Guid levelId, Point2D at)
    {
        double? highest = null;
        foreach (var ramp in document.All<Ramp>().Where(ramp => ramp.LevelId == levelId))
            if (Layout(document, ramp).PieceAt(at) is { } piece && (highest is null || piece.HeightAt(at) > highest)) highest = piece.HeightAt(at);
        return highest;
    }

    /// <summary>
    /// The least headroom over it, and what gives it: under the floors, flat roofs and ceilings
    /// over it as they are built - their holes left open - the beams and joists over it, and a
    /// pitched roof coming down over it; measured round each of its flights and landings and at
    /// their middles. None with nothing over it.
    /// </summary>
    public static (double Clear, Element By)? LeastHeadroom(BimDocument document, Ramp ramp, RampLayout layout)
    {
        var footprint = layout.Pieces.SelectMany(piece => piece.Outline).ToList();
        if (footprint.Count < 3) return null;
        var (low, high) = (new Point2D(footprint.Min(point => point.X) - 100, footprint.Min(point => point.Y) - 100),
            new Point2D(footprint.Max(point => point.X) + 100, footprint.Max(point => point.Y) + 100));
        bool Over(IEnumerable<Point2D> outline) => outline.Any() && outline.Max(point => point.X) >= low.X && outline.Min(point => point.X) <= high.X
                                                   && outline.Max(point => point.Y) >= low.Y && outline.Min(point => point.Y) <= high.Y;

        // Where it is measured: round each piece every 300 or so, and its middle, with its surface there.
        var samples = new List<(Point2D At, double Surface)>();
        foreach (var piece in layout.Pieces)
        {
            for (var i = 0; i < piece.Outline.Count; i++)
            {
                var (a, b) = (piece.Outline[i], piece.Outline[(i + 1) % piece.Outline.Count]);
                var count = Math.Max(1, (int)Math.Ceiling((b - a).Length / 300));
                for (var k = 0; k < count; k++)
                {
                    // A little in from the edge, so what stops at its edge is not taken to be over it.
                    var at = a + (b - a) * ((double)k / count);
                    var middle = Polygon2D.Centroid(piece.Outline, piece.Start);
                    samples.Add((at + (middle - at).NormalisedOrDefault(Vector2D.UnitX) * 20, piece.HeightAt(at)));
                }
            }

            var centre = Polygon2D.Centroid(piece.Outline, piece.Start);
            samples.Add((centre, piece.HeightAt(centre)));
        }

        (double Clear, Element By)? least = null;
        void Consider(double underside, double surface, Element by)
        {
            var clear = underside - surface;
            if (clear > 0 && (least is not { } known || clear < known.Clear)) least = (clear, by);
        }

        foreach (var slab in document.All<Slab>().Where(slab => slab.Boundary.Count >= 3 && Over(slab.Boundary) && Stairs.IsFloorLike(document, slab)))
        {
            var underside = slab.GetBottomElevation(document);
            if (underside <= layout.Bottom + 1) continue;
            var regions = SlabEdges.Regions(document, slab);
            foreach (var (at, surface) in samples.Where(sample => underside > sample.Surface && slab.Contains(sample.At)))
                if (regions.Any(region => Polygon2D.Contains(region.Outer, at) && !region.Holes.Any(hole => Polygon2D.Contains(hole, at))))
                    Consider(underside, surface, slab);
        }

        var framing = document.All<Beam>().Where(beam => Over(new[] { beam.Start, beam.End })).Select(beam => (Beam: beam, By: (Element)beam))
            .Concat(document.All<BeamSystem>().Where(system => Over(system.Boundary))
                .SelectMany(system => BeamSystems.Members(document, system).Select(joist => (Beam: joist, By: (Element)system))));
        foreach (var (beam, by) in framing)
        foreach (var piece in layout.Pieces)
            if (Beams.LowestOver(document, beam, piece.Outline) is { } underside && underside > piece.High)
                Consider(underside, piece.High, by);

        foreach (var roof in document.All<Roof>().Where(roof => roof.Boundary.Count >= 3 && Over(roof.Boundary) && !Stairs.IsFloorLike(document, roof)))
        {
            var surface = roof.Surface(document);
            var openings = RoofJoin.Openings(document, roof);
            foreach (var (at, height) in samples.Where(sample => roof.Contains(sample.At) && !openings.Any(opening => Polygon2D.Contains(opening, sample.At))))
                Consider(surface.HeightAt(at), height, roof);
        }

        return least;
    }

    /// <summary>
    /// How low the space under a ramp built as a slab or a deck is at its lowest that someone could
    /// walk into - its underside over the floor or the ground under it, where that is more than 100
    /// and less than 2 m - and none for a solid one, or one closed underneath.
    /// </summary>
    public static double? LowSpaceUnder(BimDocument document, Ramp ramp, RampLayout layout)
    {
        var type = TypeOf(document, ramp);
        if (type.Construction == RampConstruction.Solid || ramp.ClosedUnderneath) return null;
        double? lowest = null;
        foreach (var piece in layout.Pieces)
        {
            var middle = Polygon2D.Centroid(piece.Outline, piece.Start);
            foreach (var at in piece.Outline.Select(corner => corner + (middle - corner) * 0.1).Append(middle))
            {
                var underside = piece.HeightAt(at) - type.Thickness;
                var floor = new List<double>();
                if (Terrains.GroundAt(document, at) is { } ground) floor.Add(ground);
                floor.AddRange(document.All<Slab>()
                    .Where(slab => slab.Boundary.Count >= 3 && slab.Contains(at) && FloorSurfaces.IsStoodOn(document, slab))
                    .Select(slab => FloorSurfaces.TopAt(document, slab, at)).Where(top => top < underside));
                var space = underside - (floor.Count > 0 ? floor.Max() : GroundUnder(document, at));
                if (space > 100 && space < 2000 && (lowest is null || space > lowest)) lowest = space;
            }
        }

        return lowest;
    }

    /// <summary>Points along a side - each of its points, and midway along each length - with which way is out to its left there.</summary>
    internal static IEnumerable<(Point2D At, double Height, Vector2D Out)> SideSamples(IReadOnlyList<RampEdgePoint> side)
    {
        for (var i = 0; i + 1 < side.Count; i++)
        {
            var (a, b) = (side[i], side[i + 1]);
            var left = (b.At - a.At).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
            yield return (a.At, a.Height, left);
            yield return (a.At.MidpointTo(b.At), (a.Height + b.Height) / 2, left);
            if (i + 2 == side.Count) yield return (b.At, b.Height, left);
        }
    }

    /// <summary>Whether it stands outside the walls: its middle not in a room closed round by them.</summary>
    public static bool StandsOutside(BimDocument document, Ramp ramp)
    {
        if (!document.Walls.Any(wall => wall.LevelId == ramp.LevelId)) return true;
        var layout = Layout(document, ramp);
        var first = layout.Pieces.FirstOrDefault();
        var at = first is null ? ramp.Start : Polygon2D.Centroid(first.Outline, ramp.Start);
        return !RoomBoundary.Trace(document, ramp.LevelId, at).IsEnclosed;
    }

    /// <summary>
    /// A drawn ramp's walking line set from the points clicked along it, its foot first - or, drawn
    /// down from a door, its head first, hung by it: each turn to the nearest 15°, a right angle at
    /// most, the next stretch measured from the way it then goes.
    /// </summary>
    public static void SetPath(Ramp ramp, IReadOnlyList<Point2D> points, bool down = false)
    {
        var kept = new List<Point2D>();
        foreach (var point in points)
            if (kept.Count == 0 || (point - kept[^1]).Length > 10) kept.Add(point);
        if (kept.Count < 2) return;

        var mirror = ramp.TurnsLeft ? -1.0 : 1.0;
        var first = kept[1] - kept[0];
        var heading = Math.Atan2(first.Y, first.X);
        var (lengths, turns) = (new List<double> { first.Length }, new List<double>());
        for (var j = 1; j + 1 < kept.Count; j++)
        {
            var stretch = kept[j + 1] - kept[j];
            var bent = Math.Atan2(stretch.Y, stretch.X) - heading;
            bent = Math.Atan2(Math.Sin(bent), Math.Cos(bent));
            var turn = Math.Clamp(Math.Round(-bent * 180 / Math.PI / 15) * 15, -90, 90);
            heading -= turn * Math.PI / 180;
            turns.Add(turn * mirror);
            lengths.Add(stretch.Length);
        }

        ramp.Start = kept[0];
        var up = down ? first * -1 : first;
        ramp.Angle = (Math.Atan2(up.Y, up.X) * 180 / Math.PI % 360 + 360) % 360;
        ramp.Anchor = down ? RampAnchor.Head : RampAnchor.Foot;
        (ramp.PathLengths, ramp.PathTurns, ramp.Shape) = (lengths, turns, RampShape.Custom);
    }

    /// <summary>Made to stay put by its other end - its foot or its head - where that end is now, going up the way it goes there.</summary>
    public static void Reanchor(BimDocument document, Ramp ramp, RampAnchor anchor)
    {
        if (ramp.Anchor == anchor) return;
        var layout = Layout(document, ramp);

        // A drawn one along the line it now runs: from its head down, or from its foot up.
        if (ramp.Shape == RampShape.Custom && layout.Walk.Count >= 2)
        {
            SetPath(ramp, anchor == RampAnchor.Head ? layout.Walk.Reverse().ToList() : layout.Walk, anchor == RampAnchor.Head);
            return;
        }

        var (at, up) = anchor == RampAnchor.Head ? (layout.Head, layout.HeadUp) : (layout.Foot, layout.FootUp);
        ramp.Start = at;
        ramp.Angle = (Math.Atan2(up.Y, up.X) * 180 / Math.PI % 360 + 360) % 360;
        ramp.Anchor = anchor;
    }

    /// <summary>
    /// Whether a ramp comes up to a door out of the building at its head - the way a home is left
    /// by, which the IRC holds to 1:12 where any other ramp may be 1:8.
    /// </summary>
    public static bool UpToADoorOut(BimDocument document, Ramp ramp, RampLayout layout)
    {
        foreach (var door in document.All<Door>())
        {
            if (document.FindType<DoorType>(door.TypeId) is not { } type || !Doors.External(document, door, type)) continue;
            if (document.Find<Wall>(door.HostWallId) is not { } wall) continue;
            if (Math.Abs(wall.GetBaseElevation(document) + door.SillHeight - layout.Top) > 300) continue;
            if ((wall.Start + wall.Direction * door.DistanceAlongWall).DistanceTo(layout.Head) <= Math.Max(ramp.TopLanding, 0) + 1500) return true;
        }

        return false;
    }

    /// <summary>
    /// What about a ramp falls short of its rules - each said, with what is asked - or nothing.
    /// Its flights and landings it lays out to them itself, so what is left is what it is told:
    /// its gradient and width, how high it rises in all, its landings at either end, its
    /// handrails, kerbs and guarding, and steps beside it.
    /// </summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Ramp ramp)
    {
        var problems = new List<string>();
        var layout = Layout(document, ramp);
        var type = TypeOf(document, ramp);
        var us = Us(document);
        var irc = Irc(document, ramp);
        var other = ramp.Building == RailingBuilding.Other;
        var said = $"1:{ramp.Gradient:0.#}";

        if (layout.Rise < MinimumRise)
        {
            problems.Add("It does not rise: its head is no higher than its foot.");
            return problems;
        }

        if (irc && ramp.Gradient < 8 - 1e-9)
            problems.Add($"At {said} it is steeper than the 1:8 the IRC allows any ramp (R311.8.1).");
        else if (irc && ramp.Gradient < 12 - 1e-9 && UpToADoorOut(document, ramp, layout))
            problems.Add($"At {said} it is steeper than 1:12: the IRC holds a ramp up to the door a home is left by to 1:12, any other to 1:8 (R311.8.1).");
        else if (!irc && ramp.Gradient < SteepestAllowed(document, ramp) - 1e-9)
            problems.Add(us
                ? $"At {said} it is steeper than the 1:12 the ADA allows a ramp (405.2)."
                : SteepestAllowed(document, ramp) > 12
                    ? $"At {said} it is steeper than the 1:15 Approved Document M allows a wheelchair user's ramp (M4(3) 3.10a)."
                    : $"At {said} it is steeper than the 1:12 Approved Document M allows a ramp.");

        var (least, widthRule) = LeastWidth(document, ramp);
        if (least > 0 && layout.Width < least - 0.5) problems.Add($"It is {layout.Width:0} wide: {widthRule}.");

        if (!us && other && layout.Rise > 2000)
            problems.Add($"It rises {layout.Rise:0} in all: past 2 m, Approved Document M asks for a lift as well.");
        if (!us && other && IsRamp(ramp) && layout.Rise > 300 && !StepsBeside(document, ramp, layout))
            problems.Add($"It rises {layout.Rise:0}: past 300, Approved Document M asks for clearly signposted steps as well.");

        // A level landing at either end, as long as asked, clear of any door swinging over it.
        var end = EndLanding(document, ramp);
        var endRule = irc ? "the IRC asks for 36 in (915)"
            : us ? "the ADA asks for 60 in (1525)"
            : end > 1200 + 0.5 ? $"Approved Document K asks for one as long as the ramp is wide, {end:0}"
            : "Approved Document M asks for 1200";
        if (IsRamp(ramp) && ramp.TopLanding < end - 0.5 && !LandsOn(document, layout.Head + layout.HeadUp * (end / 2), layout.Top))
            problems.Add($"Its head comes up onto {(ramp.TopLanding > 0.5 ? $"a landing {ramp.TopLanding:0} long" : "no landing")} and no floor beyond: {endRule}.");
        // Its foot: on the floor or the ground in front of it - with none modelled, the ground the building stands on, at the lowest level.
        var before = layout.Foot - layout.FootUp * (end / 2);
        var floorThere = FloorSurfaces.StoodOnNear(document, layout.Bottom, before, Stairs.FloorReach)?.Top;
        var under = floorThere ?? GroundUnder(document, before);
        if (under > layout.Bottom + 20)
            problems.Add($"Its foot is {under - layout.Bottom:0} below the {(floorThere is null ? "ground" : "floor")} in front of it: it starts underground. Model the ground there, or raise its foot.");
        else if (IsRamp(ramp) && ramp.BottomLanding < end - 0.5 && layout.Bottom - under > 50)
            problems.Add($"Its foot starts from {(ramp.BottomLanding > 0.5 ? $"a landing {ramp.BottomLanding:0} long" : "no landing")}, {layout.Bottom - under:0} off the floor or ground in front of it: {endRule}.");
        if (!us && IsRamp(ramp) && layout.Pieces.LastOrDefault() is { Kind: RampPieceKind.Landing } head
            && Stairs.DoorSwings(document, layout.Top).Any(swing => PolygonBoolean.Combine(swing, head.Outline, BooleanOperation.Intersection).Sum(piece => piece.Area) > 10000))
            problems.Add("A door swings over the landing at its head: Approved Document M asks for 1200 clear of any door swing.");

        // Its handrails, as its rules ask, and how they are made.
        var asked = HandrailsAsked(document, ramp, layout);
        var (left, right) = Handrails(document, ramp, layout);
        if ((asked.Left && asked.Right && !(left && right)) || ((asked.Left || asked.Right) && !left && !right))
            problems.Add($"It has handrails {Sides(left, right)}: {asked.Rule}.");
        if (left || right)
        {
            var (low, high, heightRule) = us ? (865, 965, "the ADA asks for 34 in to 38 in (865 to 965)") : (900, 1000, "Approved Document M asks for 900 to 1000");
            if (type.HandrailHeight < low - 0.5 || type.HandrailHeight > high + 0.5)
                problems.Add($"Its handrails stand {type.HandrailHeight:0} over its surface: {heightRule}.");
            if (IsRamp(ramp) && !irc && (us || other) && type.HandrailExtension < (us ? 304.8 : 300) - 0.5)
                problems.Add(us
                    ? $"Its handrails run on {type.HandrailExtension:0} past its ends: the ADA asks for 12 in (305) (505.10)."
                    : $"Its handrails run on {type.HandrailExtension:0} past its ends: Approved Document M asks for 300.");
            if ((us || other) && (type.HandrailDiameter < 32 - 0.5 || type.HandrailDiameter > (us ? 51 : 50) + 0.5))
                problems.Add(us
                    ? $"Its handrails are {type.HandrailDiameter:0} thick: the ADA asks for 1¼ in to 2 in (32 to 51) round (505.7)."
                    : $"Its handrails are {type.HandrailDiameter:0} thick: Approved Document M asks for 32 to 50 round.");
        }

        // A kerb along its open sides: Approved Document M's 100; the ADA's edge protection.
        if (IsRamp(ramp) && !us && other && type.KerbHeight < 100 - 0.5)
            problems.Add($"Its kerb is {(type.KerbHeight > 0.5 ? $"{type.KerbHeight:0} high" : "missing")}: Approved Document M asks for one 100 high on its open sides.");
        if (IsRamp(ramp) && us && !irc && layout.Rise > 150 && type.KerbHeight < 100 - 0.5 && type.KerbWidth < 305 - 0.5)
            problems.Add("Its sides have no edge protection: the ADA asks for a curb 4 in (100) high, or its surface 12 in past its handrails (405.9).");

        // Headroom over it, and the space under one built as a slab or a deck.
        var (headroom, headroomRule) = HeadroomAsked(document);
        if (LeastHeadroom(document, ramp, layout) is { } over && over.Clear < headroom - 0.5)
            problems.Add($"{Describe(over.By)} is only {over.Clear:0} over it: {headroomRule}.");
        if (!us && other && LowSpaceUnder(document, ramp, layout) is { } space)
            problems.Add($"The space under it is {space:0} high in places: under 2 m, Approved Document K asks for it to be guarded off, with something low enough for a cane to find - or close it underneath.");

        // Guarding where a side drops too far - none put up, told to have no handrails.
        var limit = Railings.GuardedDrop(document, ramp.Building);
        var drops = Drops(document, ramp, layout);
        if (ramp.Railings == StairRailings.None && Math.Max(drops.Left, drops.Right) > limit + 0.5)
            problems.Add($"Its side drops {Math.Max(drops.Left, drops.Right):0} with nothing along it: guarding is asked past {limit:0}.");

        return problems;
    }

    /// <summary>What something over a ramp is, in words: "A floor", "A beam".</summary>
    private static string Describe(Element element) => element switch
    {
        Ceiling => "A ceiling",
        Roof => "A roof",
        Floor => "A floor",
        Beam => "A beam",
        BeamSystem => "A joist",
        _ => "Something"
    };

    private static string Sides(bool left, bool right) => (left, right) switch
    {
        (true, true) => "on both sides",
        (true, false) => "on its left side only",
        (false, true) => "on its right side only",
        _ => "on neither side"
    };

    /// <summary>Whether something level is stood on at a point near a height - a floor, a deck, the ground.</summary>
    private static bool LandsOn(BimDocument document, Point2D at, double height) =>
        FloorSurfaces.StoodOnNear(document, height, at, 50) is not null || Terrains.GroundNear(document, height, at, 50) is not null;

    /// <summary>Whether there are steps near it - a stair on its storey whose foot or head is within 20 m of its own.</summary>
    private static bool StepsBeside(BimDocument document, Ramp ramp, RampLayout layout) =>
        document.All<Stair>().Any(stair =>
            (stair.LevelId == ramp.LevelId || stair.TopLevelId == ramp.LevelId)
            && Math.Min(stair.Start.DistanceTo(layout.Foot), stair.Start.DistanceTo(layout.Head)) < 20000);

    public static string Verdict(BimDocument document, Ramp ramp)
    {
        var problems = Problems(document, ramp);
        if (problems.Count > 0) return string.Join(" ", problems);
        if (!IsRamp(ramp)) return "Gently sloping - 1:20 or gentler - so not a ramp";
        return Us(document) ? "Meets the ADA" : ramp.Building == RailingBuilding.Other ? "Meets Approved Document M, Volume 2" : "Meets Approved Document M, Volume 1";
    }

    /// <summary>What it works out to, in a line: "1:15, rising 450 in 2 flights over 6.75 m - 9.15 m long, 1200 wide".</summary>
    public static string Summary(BimDocument document, Ramp ramp)
    {
        var layout = Layout(document, ramp);
        var flights = layout.FlightCount;
        return $"1:{ramp.Gradient:0.#}, rising {layout.Rise:0} in {flights} flight{(flights == 1 ? "" : "s")} over {layout.Run / 1000:0.00} m - {layout.Length / 1000:0.00} m long, {layout.Width:0} wide";
    }
}
