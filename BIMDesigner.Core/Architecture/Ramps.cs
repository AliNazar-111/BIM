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

    /// <summary>Its colour when no material is given: concrete grey, or a deck's timber.</summary>
    public ColourRgb Colour { get; set; } = new(0xB5, 0xB3, 0xAE);

    public Guid MaterialId { get; set; }

    public Guid RailMaterialId { get; set; }

    public RampType Duplicate(string name) => WithCommonOf(new RampType(name)
    {
        Construction = Construction, Thickness = Thickness, KerbHeight = KerbHeight, KerbWidth = KerbWidth,
        HandrailHeight = HandrailHeight, HandrailDiameter = HandrailDiameter, HandrailExtension = HandrailExtension,
        LowerRail = LowerRail, Guarding = Guarding, PostSpacing = PostSpacing, Colour = Colour, MaterialId = MaterialId,
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

        // What it works out to, and whether that meets the regulations.
        var layout = Ramps.Layout(document, this);
        yield return ParameterValue.ReadOnly(RampParameters.Rise, () => Math.Round(layout.Rise, 1));
        yield return ParameterValue.ReadOnly(RampParameters.Flights, () => layout.FlightCount);
        yield return ParameterValue.ReadOnly(RampParameters.Run, () => Math.Round(layout.Run));
        yield return ParameterValue.ReadOnly(RampParameters.Length, () => Math.Round(layout.Length));
        yield return ParameterValue.ReadOnly(RampParameters.Summary, () => Ramps.Summary(document, this));
        yield return ParameterValue.ReadOnly(RampParameters.Rule, () => Ramps.Verdict(document, this));

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
    /// is laid out so and then moved, its head where it is put, going up the way it is told.
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
        if (footLanding > 0) blocks.Add((0, footLanding));
        for (var j = 0; j < corners.Count; j++) blocks.Add((Math.Max(0, corners[j] - Reach(turns[j])), corners[j] + Reach(turns[j])));
        blocks = Merged(blocks, rules.MinFlight);

        // Up it, stretch by stretch: flights as long as they may be with landings between, until it is as high as it goes.
        var spans = new List<(bool Flight, double From, double To, double Low, double High)>();
        var (at, done) = (0.0, 0.0);
        double Height() => run <= 1e-9 ? bottom : bottom + rise * Math.Min(1, done / run);
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

        // The landing at its head, round any turn it reaches into.
        var end = at + headLanding;
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
        var turn = ramp.Angle * Math.PI / 180 - (ramp.Anchor == RampAnchor.Head ? headings[^1] : 0);
        var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
        Vector2D Turned(Vector2D v) => new(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        var zero = new Point2D(0, 0);
        var origin = ramp.Anchor == RampAnchor.Head ? ramp.Start - Turned(headLocal.At - zero) : ramp.Start;
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
            Foot = Placed(new Point2D(0, 0)),
            FootUp = Turned(Way(0)),
            Head = Placed(headLocal.At),
            HeadUp = Turned(Way(headings[^1]))
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

    private static bool Us(BimDocument document) => document.ProjectInformation.ClearanceStandard == ClearanceStandard.Ada;

    /// <summary>The height of its foot.</summary>
    public static double Bottom(BimDocument document, Ramp ramp) => (document.FindLevel(ramp.LevelId)?.Elevation ?? 0) + ramp.BaseOffset;

    /// <summary>The height of its head: its top level's, offset - or its foot's and its own rise.</summary>
    public static double Top(BimDocument document, Ramp ramp) =>
        ramp.TopLevelId is { } top && document.FindLevel(top) is { } level
            ? level.Elevation + ramp.TopOffset
            : Bottom(document, ramp) + ramp.UnconnectedHeight;

    /// <summary>Whether its gradient makes it a ramp: steeper than 1:20. Gentler, it is a sloping path.</summary>
    public static bool IsRamp(Ramp ramp) => ramp.Gradient < 20 - 1e-9;

    /// <summary>
    /// What its rules ask of its layout at its gradient. Approved Document M (Table 1 of Volume 2,
    /// Diagram 2.1 of Volume 1): a flight goes 2 m at 1:12, 5 m at 1:15 and 10 m at 1:20, and
    /// between pro rata - 1:14 for 4 m; gentler than 1:20, a building other than a dwelling still
    /// lands each 500 of rise. Its landings: 1200 between flights in a dwelling, 1500 elsewhere -
    /// 1800 square, passing places, where there are three flights or more. The ADA: 30 in (760) of
    /// rise to a run, landings 60 in (1525).
    /// </summary>
    public static RampRules Rules(BimDocument document, Ramp ramp, int flights = 0)
    {
        var x = Math.Max(1, ramp.Gradient);
        if (Us(document)) return new RampRules(IsRamp(ramp) ? 760 * x : double.PositiveInfinity, 1525, 1525, 0);

        var going = IsRamp(ramp)
            ? (Math.Clamp(x, 12, 20) - 10) * 1000
            : ramp.Building == RailingBuilding.Other ? 500 * x : double.PositiveInfinity;
        if (ramp.Building == RailingBuilding.Dwelling) return new RampRules(going, 1200, 1200, 0);
        var passing = flights >= 3;
        return new RampRules(going, passing ? 1800 : 1500, passing ? 1800 : 1500, passing ? 1800 : 0);
    }

    /// <summary>How long its landings at its foot and head must be: 1200, Approved Document M says; the ADA 60 in (1525).</summary>
    public static double EndLanding(BimDocument document) => Us(document) ? 1525 : 1200;

    /// <summary>The narrowest it may be between its kerbs or handrails, and why.</summary>
    public static (double Width, string Rule) LeastWidth(BimDocument document, Ramp ramp) =>
        Us(document) ? (915, "the ADA asks for 36 in (915) clear between its handrails")
        : ramp.Building == RailingBuilding.Other ? (1500, "Approved Document M asks for 1.5 m between its kerbs")
        : (900, "Approved Document M asks a dwelling's ramp for 900 clear - 1200 on a communal approach");

    /// <summary>
    /// Laid out under its rules: in a building other than a dwelling, those of three flights or
    /// more are laid out again with their landings between as passing places.
    /// </summary>
    public static RampLayout Layout(BimDocument document, Ramp ramp)
    {
        var (bottom, top) = (Bottom(document, ramp), Top(document, ramp));
        var layout = RampLayout.Of(ramp, bottom, top, Rules(document, ramp));
        if (!Us(document) && ramp.Building == RailingBuilding.Other && layout.FlightCount >= 3)
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
        foreach (var floor in document.Elements.OfType<Slab>().Where(slab => slab.Boundary.Count >= 3 && slab.Contains(beside) && FloorSurfaces.IsStoodOn(document, slab)))
        {
            var top = FloorSurfaces.TopAt(document, floor, beside);
            if (top <= height + Stairs.FloorReach) under.Add(top);
        }

        if (SurfaceAt(document, beside, height, Terrains.Reach) is { } ramp && ramp <= height + Stairs.FloorReach) under.Add(ramp);
        return height - (under.Count > 0 ? under.Max() : layout.Bottom);
    }

    /// <summary>The surface of a ramp over a point within reach of a height - the highest, where one passes over another - if any.</summary>
    public static double? SurfaceAt(BimDocument document, Point2D at, double height, double reach)
    {
        double? highest = null;
        foreach (var ramp in document.Elements.OfType<Ramp>())
        {
            if (Layout(document, ramp).PieceAt(at) is not { } piece) continue;
            var top = piece.HeightAt(at);
            if (Math.Abs(top - height) <= reach && (highest is null || top > highest)) highest = top;
        }

        return highest;
    }

    /// <summary>The drop beside its sides at its worst, left and right going up.</summary>
    public static (double Left, double Right) Drops(BimDocument document, Ramp ramp, RampLayout layout)
    {
        var reach = TypeOf(document, ramp).KerbWidth + 200;
        double Worst(IReadOnlyList<RampEdgePoint> side, double outward) =>
            side.Count < 2 ? 0 : SideSamples(side).Max(sample => DropBeside(document, layout, sample.At + sample.Out * (outward * reach), sample.Height));
        return (Worst(layout.Left, 1), Worst(layout.Right, -1));
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
    /// A drawn ramp's walking line set from the points clicked along it, its foot first: each turn
    /// to the nearest 15°, a right angle at most, the next stretch measured from the way it then goes.
    /// </summary>
    public static void SetPath(Ramp ramp, IReadOnlyList<Point2D> points)
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
        ramp.Angle = (Math.Atan2(first.Y, first.X) * 180 / Math.PI % 360 + 360) % 360;
        ramp.Anchor = RampAnchor.Foot;
        (ramp.PathLengths, ramp.PathTurns, ramp.Shape) = (lengths, turns, RampShape.Custom);
    }

    /// <summary>Made to stay put by its other end - its foot or its head - where that end is now, going up the way it goes there.</summary>
    public static void Reanchor(BimDocument document, Ramp ramp, RampAnchor anchor)
    {
        if (ramp.Anchor == anchor) return;
        var layout = Layout(document, ramp);
        var (at, up) = anchor == RampAnchor.Head ? (layout.Head, layout.HeadUp) : (layout.Foot, layout.FootUp);
        ramp.Start = at;
        ramp.Angle = (Math.Atan2(up.Y, up.X) * 180 / Math.PI % 360 + 360) % 360;
        ramp.Anchor = anchor;
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
        var other = ramp.Building == RailingBuilding.Other;
        var said = $"1:{ramp.Gradient:0.#}";

        if (layout.Rise < MinimumRise)
        {
            problems.Add("It does not rise: its head is no higher than its foot.");
            return problems;
        }

        if (ramp.Gradient < 12 - 1e-9)
            problems.Add(us
                ? $"At {said} it is steeper than the 1:12 the ADA allows a ramp (405.2)."
                : $"At {said} it is steeper than the 1:12 Approved Document M allows a ramp.");

        var (least, widthRule) = LeastWidth(document, ramp);
        if (layout.Width < least - 0.5) problems.Add($"It is {layout.Width:0} wide: {widthRule}.");

        if (!us && other && layout.Rise > 2000)
            problems.Add($"It rises {layout.Rise:0} in all: past 2 m, Approved Document M asks for a lift as well.");
        if (!us && other && IsRamp(ramp) && layout.Rise > 300 && !StepsBeside(document, ramp, layout))
            problems.Add($"It rises {layout.Rise:0}: past 300, Approved Document M asks for clearly signposted steps as well.");

        // A level landing at either end, as long as asked, clear of any door swinging over it.
        var end = EndLanding(document);
        var endRule = us ? "the ADA asks for 60 in (1525)" : "Approved Document M asks for 1200";
        if (IsRamp(ramp) && ramp.TopLanding < end - 0.5 && !LandsOn(document, layout.Head + layout.HeadUp * (end / 2), layout.Top))
            problems.Add($"Its head comes up onto {(ramp.TopLanding > 0.5 ? $"a landing {ramp.TopLanding:0} long" : "no landing")} and no floor beyond: {endRule}.");
        // Its foot: on the ground or a floor in front of it - only where one is modelled there and its foot is off it; with nothing modelled, the ground is taken to be at its foot.
        var before = layout.Foot - layout.FootUp * (end / 2);
        var modelled = Terrains.GroundAt(document, before) is not null || document.Elements.OfType<Slab>().Any(slab => slab.Boundary.Count >= 3 && slab is not Ceiling && slab.Contains(before));
        if (IsRamp(ramp) && ramp.BottomLanding < end - 0.5 && modelled && !LandsOn(document, before, layout.Bottom))
            problems.Add($"Its foot starts from {(ramp.BottomLanding > 0.5 ? $"a landing {ramp.BottomLanding:0} long" : "no landing")}, off the floor or ground in front of it: {endRule}.");
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
            if (IsRamp(ramp) && (us || other) && type.HandrailExtension < (us ? 304.8 : 300) - 0.5)
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
        if (IsRamp(ramp) && us && layout.Rise > 150 && type.KerbHeight < 100 - 0.5 && type.KerbWidth < 305 - 0.5)
            problems.Add("Its sides have no edge protection: the ADA asks for a curb 4 in (100) high, or its surface 12 in past its handrails (405.9).");

        // Guarding where a side drops too far - none put up, told to have no handrails.
        var limit = Railings.GuardedDrop(document, ramp.Building);
        var drops = Drops(document, ramp, layout);
        if (ramp.Railings == StairRailings.None && Math.Max(drops.Left, drops.Right) > limit + 0.5)
            problems.Add($"Its side drops {Math.Max(drops.Left, drops.Right):0} with nothing along it: guarding is asked past {limit:0}.");

        return problems;
    }

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
        document.Elements.OfType<Stair>().Any(stair =>
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
