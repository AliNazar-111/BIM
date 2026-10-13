using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a stair is laid out in plan: one straight flight, or two with a quarter or half turn between.</summary>
public enum StairShape
{
    Straight,
    QuarterTurn,
    HalfTurn
}

/// <summary>What a quarter-turn stair turns on: a square landing, or three winders round a newel post.</summary>
public enum StairCorner
{
    Landing,
    Winders
}

/// <summary>How a stair is built: timber treads and risers housed in strings, or concrete cast as one.</summary>
public enum StairConstruction
{
    Timber,
    Concrete
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
/// nosings and treads, open or closed risers - and its handrail's height and balusters' spacing.
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

    /// <summary>Its handrail's top above the pitch line - the line through the nosings - mm.</summary>
    public double HandrailHeight { get; set; } = 900;

    /// <summary>Its balusters' spacing, centre to centre, mm: 32 mm balusters at 120 leave 88 between, short of the 100 a child's head goes through.</summary>
    public double BalusterSpacing { get; set; } = 120;

    /// <summary>What it reads as with no material: a light oak.</summary>
    public ColourRgb Colour { get; set; } = new(0xC8, 0xA8, 0x7A);

    public Guid MaterialId { get; set; }

    public StairType Duplicate(string name) => WithCommonOf(new StairType(name)
    {
        Construction = Construction, MaxRiser = MaxRiser, Going = Going, Nosing = Nosing, TreadThickness = TreadThickness,
        OpenRisers = OpenRisers, HandrailHeight = HandrailHeight, BalusterSpacing = BalusterSpacing, Colour = Colour, MaterialId = MaterialId
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(StairTypeParameters.Construction,
            () => EnumText.Humanise(Construction),
            v => { if (EnumText.TryParse<StairConstruction>(v, out var construction)) Construction = construction; },
            EnumText.Choices<StairConstruction>());
        yield return Size(StairTypeParameters.MaxRiser, () => MaxRiser, v => MaxRiser = v, 100, 300);
        yield return Size(StairTypeParameters.Going, () => Going, v => Going = v, 150, 500);
        yield return Size(StairTypeParameters.Nosing, () => Nosing, v => Nosing = v, 0, 60);
        yield return Size(StairTypeParameters.TreadThickness, () => TreadThickness, v => TreadThickness = v, 15, 100);
        yield return ParameterValue.Bind(StairTypeParameters.OpenRisers, () => OpenRisers, v => OpenRisers = v);
        yield return Size(StairTypeParameters.HandrailHeight, () => HandrailHeight, v => HandrailHeight = v, 700, 1200);
        yield return Size(StairTypeParameters.BalusterSpacing, () => BalusterSpacing, v => BalusterSpacing = v, 60, 300);

        if (document is not null)
            yield return ParameterValue.BindChoice(StairTypeParameters.Material,
                () => document.FindMaterial(MaterialId)?.Name ?? ComponentType.ByColour,
                v => MaterialId = document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty,
                new[] { ComponentType.ByColour }.Concat(document.Materials.Select(material => material.Name).OrderBy(name => name)).ToArray());

        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }

    private static ParameterValue Size(ParameterDefinition definition, Func<double> get, Action<double> set, double least, double most) =>
        ParameterValue.BindValidated(definition, get, (double v) =>
        {
            if (v < least || v > most) return false;
            set(v);
            return true;
        });

    /// <summary>The stair types a project starts with: timber with closed and open risers, and concrete.</summary>
    public static IReadOnlyList<StairType> Library() => new[]
    {
        new StairType("Stair - Timber, Closed Risers") { TypeMark = "ST1", Cost = 1800m },
        new StairType("Stair - Timber, Open Risers") { OpenRisers = true, TypeMark = "ST2", Cost = 2100m },
        new StairType("Stair - Concrete")
        {
            Construction = StairConstruction.Concrete, MaxRiser = 175, Going = 250, Nosing = 0, Colour = new ColourRgb(0xB5, 0xB3, 0xAE),
            TypeMark = "ST3", Cost = 3200m
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
    public static readonly ParameterDefinition HandrailHeight = new("Handrail Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BalusterSpacing = new("Baluster Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
}

/// <summary>
/// A stair from one level to another: where its first riser is and which way it goes up, how
/// wide it is, and whether it runs straight or turns - a quarter on a landing or on winders, a
/// half on a landing. How many risers it has, and how high each, it works out from the storey's
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

    public double BaseOffset { get; set; }

    /// <summary>The level it goes up to - the one above, usually; none for a stair of its own height.</summary>
    public Guid? TopLevelId { get; set; }

    public double TopOffset { get; set; }

    /// <summary>How high it goes with no top level, mm.</summary>
    public double UnconnectedHeight { get; set; } = 2600;

    public StairRailings Railings { get; set; } = StairRailings.Auto;

    /// <summary>Whether it cuts the floor over it for headroom.</summary>
    public bool CutsFloorAbove { get; set; } = true;

    /// <summary>Which way is up its first flight.</summary>
    public Vector2D Up => new(Math.Cos(Angle * Math.PI / 180), Math.Sin(Angle * Math.PI / 180));

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var levels = document.Levels.Select(level => level.Name).ToArray();
        const string unconnected = "Unconnected";

        yield return ParameterValue.BindChoice(StairParameters.BaseLevel,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            levels);
        yield return ParameterValue.BindValidated(StairParameters.BaseOffset, () => BaseOffset, (double v) => { BaseOffset = v; return true; });
        yield return ParameterValue.BindChoice(StairParameters.TopLevel,
            () => TopLevelId is { } top ? document.FindLevel(top)?.Name ?? unconnected : unconnected,
            v => TopLevelId = document.Levels.FirstOrDefault(level => level.Name == v)?.Id,
            levels.Append(unconnected).ToArray());
        yield return ParameterValue.BindValidated(StairParameters.TopOffset, () => TopOffset, (double v) => { TopOffset = v; return true; });
        yield return ParameterValue.BindValidated(StairParameters.UnconnectedHeight, () => UnconnectedHeight, (double v) =>
        {
            if (v < 300) return false;
            UnconnectedHeight = v;
            return true;
        });

        yield return ParameterValue.BindChoice(StairParameters.Shape,
            () => EnumText.Humanise(Shape),
            v => { if (EnumText.TryParse<StairShape>(v, out var shape)) Shape = shape; },
            EnumText.Choices<StairShape>());
        yield return ParameterValue.BindChoice(StairParameters.Corner,
            () => EnumText.Humanise(Corner),
            v => { if (EnumText.TryParse<StairCorner>(v, out var corner)) Corner = corner; },
            EnumText.Choices<StairCorner>());
        yield return ParameterValue.BindChoice(StairParameters.Turns,
            () => TurnsLeft ? "Left" : "Right",
            v => TurnsLeft = v == "Left",
            new[] { "Left", "Right" });
        yield return ParameterValue.BindValidated(StairParameters.Width, () => Width, (double v) =>
        {
            if (v < 500 || v > 5000) return false;
            Width = v;
            return true;
        });
        yield return ParameterValue.BindValidated(StairParameters.RisersBeforeTurn, () => RisersBeforeTurn ?? 0, (int v) =>
        {
            if (v < 0) return false;
            RisersBeforeTurn = v == 0 ? null : v;
            return true;
        });
        yield return ParameterValue.BindValidated(StairParameters.Well, () => Well, (double v) =>
        {
            if (v < 0 || v > 3000) return false;
            Well = v;
            return true;
        });
        yield return ParameterValue.BindValidated(StairParameters.Angle, () => Angle, (double v) => { Angle = ((v % 360) + 360) % 360; return true; });
        yield return ParameterValue.BindChoice(StairParameters.Railings,
            () => EnumText.Humanise(Railings),
            v => { if (EnumText.TryParse<StairRailings>(v, out var railings)) Railings = railings; },
            EnumText.Choices<StairRailings>());
        yield return ParameterValue.Bind(StairParameters.CutsFloorAbove, () => CutsFloorAbove, v => CutsFloorAbove = v);

        // What it works out to, and whether that meets the regulations.
        var layout = Stairs.Layout(document, this);
        yield return ParameterValue.ReadOnly(StairParameters.Risers, () => layout.Risers);
        yield return ParameterValue.ReadOnly(StairParameters.RiserHeight, () => layout.Rise);
        yield return ParameterValue.ReadOnly(StairParameters.ActualGoing, () => layout.Going);
        yield return ParameterValue.ReadOnly(StairParameters.Pitch, () => Math.Round(layout.Pitch, 1));
        yield return ParameterValue.ReadOnly(StairParameters.Rule, () => Stairs.Verdict(document, this));

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
    public static readonly ParameterDefinition Angle = new("Direction", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Railings = new("Handrails", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition CutsFloorAbove = new("Cut the Floor Above", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Risers = new("Risers", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition RiserHeight = new("Riser Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition ActualGoing = new("Actual Going", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Pitch = new("Pitch", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rule = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

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

/// <summary>
/// A stair worked out: how many risers and how high, its steps in plan with their heights, the
/// line walked up it, its two sides with the pitch line along them, the newel a winding corner
/// turns on, and the edge where it reaches the floor.
/// </summary>
public sealed class StairLayout
{
    public double Bottom { get; init; }
    public double Top { get; init; }
    public int Risers { get; init; }
    public double Rise { get; init; }
    public double Going { get; init; }
    public double Width { get; init; }
    public IReadOnlyList<StairStep> Steps { get; init; } = Array.Empty<StairStep>();
    public IReadOnlyList<Point2D> WalkingLine { get; init; } = Array.Empty<Point2D>();

    /// <summary>The left side going up, and the right: each the points it turns or steps at, with the pitch line's height there.</summary>
    public IReadOnlyList<StairSidePoint> Left { get; init; } = Array.Empty<StairSidePoint>();
    public IReadOnlyList<StairSidePoint> Right { get; init; } = Array.Empty<StairSidePoint>();

    /// <summary>The newel post a winding corner turns on, as an outline; none for a landing.</summary>
    public IReadOnlyList<Point2D>? Newel { get; init; }

    /// <summary>The narrowest end of a winder, mm; none without winders.</summary>
    public double? WinderNarrowEnd { get; init; }

    /// <summary>A winder's going along the middle of the stair, mm; none without winders.</summary>
    public double? WinderGoing { get; init; }

    /// <summary>Its pitch, degrees: how steep the line through its nosings is.</summary>
    public double Pitch => Math.Atan2(Rise, Going) * 180 / Math.PI;

    /// <summary>Where it comes out at the top: the edge of the floor it reaches.</summary>
    public (Point2D From, Point2D To) TopEdge { get; init; }

    /// <summary>Its first riser's middle, and the way up its first flight: what distances up it are measured from.</summary>
    public Point2D Origin { get; init; }
    public Vector2D Axis { get; init; }

    /// <summary>
    /// For a half turn, the well between its two flights from so far up the first one to the
    /// landing - so an opening over the flights takes the well with them, and leaves no sliver of
    /// floor between them; none for any other stair, or one with no well.
    /// </summary>
    public Func<double, IReadOnlyList<Point2D>?>? WellFrom { get; init; }

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
        double Height(int step) => bottom + step * rise;

        // Laid out as if it turned right - up the first flight along u, the second toward -v -
        // then mirrored across its first flight for one that turns left.
        var up = stair.Up;
        var left = up.PerpendicularLeft();
        var mirror = stair.TurnsLeft ? -1.0 : 1.0;
        Point2D P(double u, double v) => stair.Start + up * u + left * (v * mirror);
        Vector2D D(double u, double v) => up * u + left * (v * mirror);

        var steps = new List<StairStep>();

        // A flight of treads from a riser line, going one way, its sides either side of it.
        void Flight(double startU, double startV, double dirU, double dirV, int first, int count)
        {
            var (acrossU, acrossV) = (-dirV, dirU);
            for (var i = 0; i < count; i++)
            {
                double U(double along, double across) => startU + dirU * along + acrossU * across;
                double V(double along, double across) => startV + dirV * along + acrossV * across;
                var (a0, a1) = (i * g - nosing, (i + 1) * g);
                var outline = new[] { P(U(a0, -w / 2), V(a0, -w / 2)), P(U(a1, -w / 2), V(a1, -w / 2)), P(U(a1, w / 2), V(a1, w / 2)), P(U(a0, w / 2), V(a0, w / 2)) };
                steps.Add(new StairStep(first + i, StairStepKind.Tread, outline, Height(first + i),
                    P(U(i * g, -w / 2), V(i * g, -w / 2)), P(U(i * g, w / 2), V(i * g, w / 2)), D(dirU, dirV)));
            }
        }

        StairStep Landing(int number, double u0, double u1, double v0, double v1) =>
            new(number, StairStepKind.Landing, new[] { P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1) }, Height(number),
                P(u0, -w / 2), P(u0, w / 2), D(1, 0));

        var shape = stair.Shape;
        var winders = shape == StairShape.QuarterTurn && stair.Corner == StairCorner.Winders;
        var turnSteps = shape == StairShape.Straight ? 0 : winders ? 3 : 1;
        if (risers - 1 < turnSteps + 1) (shape, winders, turnSteps) = (StairShape.Straight, false, 0);

        // Risers up to the turn: as asked, or halfway; at least one, leaving the second flight its treads.
        var beforeTurn = Math.Clamp(stair.RisersBeforeTurn ?? (risers - turnSteps + 1) / 2, 1, Math.Max(1, risers - turnSteps));
        var c1 = shape == StairShape.Straight ? risers - 1 : beforeTurn - 1;
        var l1 = c1 * g;
        var c2 = shape == StairShape.Straight ? 0 : risers - 1 - (beforeTurn - 1) - turnSteps;
        var l2 = c2 * g;
        var h1 = Height(1);

        Flight(0, 0, 1, 0, 1, c1);

        IReadOnlyList<Point2D> walk;
        List<StairSidePoint> outer, inner;
        Func<double, IReadOnlyList<Point2D>?>? wellFrom = null;
        IReadOnlyList<Point2D>? newel = null;
        double? narrowEnd = null, winderGoing = null;
        (Point2D, Point2D) topEdge;
        StairSidePoint S(double u, double v, double h) => new(P(u, v), h);

        switch (shape)
        {
            case StairShape.QuarterTurn when winders:
            {
                // Three winders round a 100 newel at the inner corner, their risers fanning out
                // from it at 30° apart: each narrow end on the newel's faces, 58 at the least.
                const double post = 100;
                var t = Math.Tan(Math.PI / 6);
                var c = (U: l1, V: -w / 2);
                var n = beforeTurn;
                steps.Add(new StairStep(n, StairStepKind.Winder,
                    new[] { P(c.U, c.V), P(l1, w / 2), P(l1 + t * w, w / 2) }, Height(n), P(c.U, c.V), P(l1, w / 2), D(1, 0)));
                steps.Add(new StairStep(n + 1, StairStepKind.Winder,
                    new[] { P(c.U, c.V), P(l1 + t * w, w / 2), P(l1 + w, w / 2), P(l1 + w, -w / 2 + t * w) }, Height(n + 1),
                    P(c.U, c.V), P(l1 + t * w, w / 2), D(Math.Cos(Math.PI / 6), -Math.Sin(Math.PI / 6))));
                steps.Add(new StairStep(n + 2, StairStepKind.Winder,
                    new[] { P(c.U, c.V), P(l1 + w, -w / 2 + t * w), P(l1 + w, -w / 2) }, Height(n + 2),
                    P(c.U, c.V), P(l1 + w, -w / 2 + t * w), D(Math.Sin(Math.PI / 6), -Math.Cos(Math.PI / 6))));
                Flight(l1 + w / 2, -w / 2, 0, -1, n + 3, c2);

                newel = new[] { P(c.U, c.V), P(c.U + post, c.V), P(c.U + post, c.V + post), P(c.U, c.V + post) };
                narrowEnd = post * t;
                winderGoing = w / 2 * Math.PI / 6;

                var arc = Enumerable.Range(0, 7).Select(k => Math.PI / 2 * (1 - k / 6.0)).Select(a => P(c.U + w / 2 * Math.Cos(a), c.V + w / 2 * Math.Sin(a)));
                walk = new[] { P(0, 0) }.Concat(arc).Append(P(l1 + w / 2, -w / 2 - l2)).ToList();

                // The outer side rises steadily round the corner, the inner steps up at the newel.
                outer = new List<StairSidePoint>
                {
                    S(0, w / 2, h1), S(l1, w / 2, Height(n)), S(l1 + t * w, w / 2, Height(n + 1)), S(l1 + w, w / 2, Height(n + 1) + rise / 2),
                    S(l1 + w, -w / 2 + t * w, Height(n + 2)), S(l1 + w, -w / 2, Height(n + 3)), S(l1 + w, -w / 2 - l2, top)
                };
                inner = new List<StairSidePoint> { S(0, -w / 2, h1), S(c.U, c.V, Height(n)), S(c.U, c.V, Height(n + 3)), S(l1, -w / 2 - l2, top) };
                topEdge = (P(l1, -w / 2 - l2), P(l1 + w, -w / 2 - l2));
                break;
            }

            case StairShape.QuarterTurn:
            {
                var n = beforeTurn;
                steps.Add(Landing(n, l1, l1 + w, -w / 2, w / 2));
                Flight(l1 + w / 2, -w / 2, 0, -1, n + 1, c2);
                walk = new[] { P(0, 0), P(l1 + w / 2, 0), P(l1 + w / 2, -w / 2 - l2) };
                outer = new List<StairSidePoint>
                {
                    S(0, w / 2, h1), S(l1, w / 2, Height(n)), S(l1 + w, w / 2, Height(n)), S(l1 + w, -w / 2, Height(n)),
                    S(l1 + w, -w / 2, Height(n + 1)), S(l1 + w, -w / 2 - l2, top)
                };
                inner = new List<StairSidePoint> { S(0, -w / 2, h1), S(l1, -w / 2, Height(n)), S(l1, -w / 2, Height(n + 1)), S(l1, -w / 2 - l2, top) };
                topEdge = (P(l1, -w / 2 - l2), P(l1 + w, -w / 2 - l2));
                break;
            }

            case StairShape.HalfTurn:
            {
                var n = beforeTurn;
                var well = Math.Max(0, stair.Well);
                var far = -w / 2 - well - w;
                steps.Add(Landing(n, l1, l1 + w, far, w / 2));
                Flight(l1, -w - well, -1, 0, n + 1, c2);
                walk = new[] { P(0, 0), P(l1 + w / 2, 0), P(l1 + w / 2, -w - well), P(l1 - l2, -w - well) };
                outer = new List<StairSidePoint>
                {
                    S(0, w / 2, h1), S(l1, w / 2, Height(n)), S(l1 + w, w / 2, Height(n)), S(l1 + w, far, Height(n)), S(l1, far, Height(n)),
                    S(l1, far, Height(n + 1)), S(l1 - l2, far, top)
                };
                inner = new List<StairSidePoint>
                {
                    S(0, -w / 2, h1), S(l1, -w / 2, Height(n)), S(l1, -w / 2 - well, Height(n)), S(l1, -w / 2 - well, Height(n + 1)), S(l1 - l2, -w / 2 - well, top)
                };
                topEdge = (P(l1 - l2, -w / 2 - well), P(l1 - l2, far));

                // The well, a little into each flight so it joins them.
                if (well > 1)
                    wellFrom = from =>
                    {
                        var start = Math.Max(Math.Max(0, l1 - l2), from);
                        return start >= l1 - 1 ? null : new[] { P(start, -w / 2 + 1), P(l1, -w / 2 + 1), P(l1, -w / 2 - well - 1), P(start, -w / 2 - well - 1) };
                    };
                break;
            }

            default:
                walk = new[] { P(0, 0), P(l1, 0) };
                outer = new List<StairSidePoint> { S(0, w / 2, h1), S(l1, w / 2, top) };
                inner = new List<StairSidePoint> { S(0, -w / 2, h1), S(l1, -w / 2, top) };
                topEdge = (P(l1, -w / 2), P(l1, w / 2));
                break;
        }

        // Turning right, the outer side is on the left going up; turning left, mirrored, on the right.
        var (leftSide, rightSide) = stair.TurnsLeft ? (inner, outer) : (outer, inner);

        return new StairLayout
        {
            Bottom = bottom, Top = top, Risers = risers, Rise = rise, Going = g, Width = w,
            Steps = steps, WalkingLine = walk, Left = leftSide, Right = rightSide, Newel = newel,
            WinderNarrowEnd = narrowEnd, WinderGoing = winderGoing, TopEdge = topEdge,
            Origin = stair.Start, Axis = up, WellFrom = wellFrom
        };
    }
}

/// <summary>
/// Stairs in the model: how high they go - floor to floor - how they lay out, the openings they
/// cut in the floors over them, and whether they meet Approved Document K (England's stairs) or
/// the IRC's R311.7, whichever the project is checked against.
/// </summary>
public static class Stairs
{
    private static readonly StairType Fallback = StairType.Library()[0];

    public static StairType TypeOf(BimDocument document, Stair stair) => document.FindType<StairType>(stair.TypeId) ?? Fallback;

    /// <summary>Its foot: the top of the floor it stands on at its first riser - or its level, where the level is the floor - and its offset.</summary>
    public static double Bottom(BimDocument document, Stair stair) => FloorTop(document, stair.LevelId, stair.Start) + stair.BaseOffset;

    /// <summary>Its head: the top of the floor of the level it goes up to, where it has one; otherwise its own height above its foot.</summary>
    public static double Top(BimDocument document, Stair stair) =>
        stair.TopLevelId is { } top && document.FindLevel(top) is not null
            ? FloorTop(document, top, stair.Start) + stair.TopOffset
            : Bottom(document, stair) + stair.UnconnectedHeight;

    private static double FloorTop(BimDocument document, Guid levelId, Point2D at) =>
        FloorSurfaces.FloorUnder(document, levelId, at) is { } floor ? FloorSurfaces.TopAt(document, floor, at) : document.FindLevel(levelId)?.Elevation ?? 0;

    public static StairLayout Layout(BimDocument document, Stair stair) =>
        StairLayout.Of(stair, TypeOf(document, stair), Bottom(document, stair), Top(document, stair));

    /// <summary>The rules a stair is checked against, by name.</summary>
    public static string Label(ClearanceStandard standard) =>
        standard == ClearanceStandard.Ada ? "IRC R311.7" : "Approved Document K";

    /// <summary>The clear height over a stair its rules ask for, mm: 2000, or 6 ft 8 in.</summary>
    public static double Headroom(ClearanceStandard standard) => standard == ClearanceStandard.Ada ? 2032 : 2000;

    /// <summary>
    /// The holes stairs cut in a slab over them: over each the steps that would not have the
    /// headroom under it - which, steps rising as they go, is its top end - as one outline.
    /// The floor a stair stands on, and those well above it, are not cut.
    /// </summary>
    public static IEnumerable<IReadOnlyList<Point2D>> Holes(BimDocument document, Slab slab)
    {
        foreach (var stair in document.Elements.OfType<Stair>())
            if (Opening(document, stair, slab, Layout(document, stair)) is { } hole) yield return hole;
    }

    /// <summary>
    /// Whether a slab is one a stair passes through rather than stops under: a floor, a ceiling,
    /// or a flat roof that is a floor - a deck the storey above stands on. A pitched roof is not cut.
    /// </summary>
    public static bool IsFloorLike(BimDocument document, Slab slab) =>
        slab is Floor or Ceiling || slab is Roof && FloorSurfaces.IsStoodOn(document, slab);

    /// <summary>The hole one stair cuts in one slab over it, or none.</summary>
    public static IReadOnlyList<Point2D>? Opening(BimDocument document, Stair stair, Slab slab, StairLayout layout)
    {
        if (!stair.CutsFloorAbove || slab.Boundary.Count < 3 || !IsFloorLike(document, slab)) return null;

        var headroom = Headroom(document.ProjectInformation.ClearanceStandard);
        var (underside, top) = (slab.GetBottomElevation(document), slab.GetTopElevation(document));
        if (top <= layout.Bottom + 1 || underside >= layout.Top + headroom) return null;

        var steps = layout.Steps.Where(step => step.Top + headroom > underside).ToList();
        var opened = steps.Select(step => step.Outline).ToList();

        // A half turn's well, beside as much of its flights as is opened: no floor left between them.
        if (layout.WellFrom is { } well && steps.Any(step => step.Kind == StairStepKind.Tread)
            && well(steps.Where(step => step.Kind == StairStepKind.Tread).SelectMany(step => step.Outline).Min(point => (point - layout.Origin).Dot(layout.Axis))) is { } gap)
            opened.Add(gap);

        if (Union(opened) is not { Count: >= 3 } hole) return null;
        return PolygonBoolean.Combine(slab.Boundary, hole, BooleanOperation.Intersection).Sum(region => region.Area) > 1000 ? hole : null;
    }

    /// <summary>
    /// The floors a stair comes up through at its top, with the hole it cuts in each: what is
    /// guarded round the stairwell on the floor above.
    /// </summary>
    public static IEnumerable<(Slab Floor, IReadOnlyList<Point2D> Hole)> Stairwells(BimDocument document, Stair stair, StairLayout layout) =>
        document.Elements.OfType<Slab>()
            .Where(slab => slab is not Ceiling && IsFloorLike(document, slab) && Math.Abs(slab.GetTopElevation(document) - layout.Top) < 600)
            .Select(floor => (Floor: floor, Hole: Opening(document, stair, floor, layout)))
            .Where(entry => entry.Hole is not null)
            .Select(entry => (entry.Floor, entry.Hole!));

    /// <summary>The outline round steps that meet: each added to the last.</summary>
    private static IReadOnlyList<Point2D>? Union(IReadOnlyList<IReadOnlyList<Point2D>> outlines)
    {
        if (outlines.Count == 0) return null;

        var whole = outlines[0];
        foreach (var next in outlines.Skip(1))
        {
            var joined = PolygonBoolean.Combine(whole, next, BooleanOperation.Union);
            if (joined.Count > 0) whole = joined.OrderByDescending(region => region.Area).First().Outer;
        }

        return whole;
    }

    /// <summary>The whole of a stair in plan: its steps as one outline.</summary>
    public static IReadOnlyList<Point2D> Footprint(StairLayout layout) => Union(layout.Steps.Select(step => step.Outline).ToList()) ?? Array.Empty<Point2D>();

    /// <summary>
    /// The least headroom over a stair's steps - from each step up to the underside of whatever
    /// floor or ceiling is over it, its own opening taken out - and over which step; none where
    /// nothing is over it.
    /// </summary>
    public static (double Clear, int Step)? LeastHeadroom(BimDocument document, Stair stair, StairLayout layout)
    {
        (double, int)? least = null;
        foreach (var slab in document.Elements.OfType<Slab>().Where(slab => slab.Boundary.Count >= 3 && IsFloorLike(document, slab)))
        {
            if (slab.GetTopElevation(document) <= layout.Bottom + 1) continue;
            var underside = slab.GetBottomElevation(document);
            var regions = SlabEdges.Regions(document, slab);
            foreach (var step in layout.Steps)
            {
                if (underside <= step.Top) continue;
                var over = regions.Sum(region => PolygonBoolean.Combine(Shafts.Loops(region), new[] { step.Outline }, BooleanOperation.Intersection).Sum(piece => piece.Area));
                if (over < 1000) continue;
                var clear = underside - step.Top;
                if (least is not { } known || clear < known.Item1) least = (clear, step.Number);
            }
        }

        return least;
    }

    /// <summary>What about a stair falls short of its rules - each said, with what is asked - or nothing.</summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Stair stair)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        var type = TypeOf(document, stair);
        var layout = Layout(document, stair);
        var problems = new List<string>();
        var (rise, going) = (layout.Rise, layout.Going);

        if (standard == ClearanceStandard.Ada)
        {
            if (rise > 196 + 0.5) problems.Add($"Each riser is {rise:0}: the IRC allows 196 (7¾ in) at most.");
            if (going < 254 - 0.5) problems.Add($"Each tread is {going:0}: the IRC asks for 254 (10 in) at least.");
            if (layout.Width < 914 - 0.5) problems.Add($"It is {layout.Width:0} wide: the IRC asks for 914 (36 in) at least.");
            if (type.HandrailHeight < 864 - 0.5 || type.HandrailHeight > 965 + 0.5) problems.Add($"Its handrail is {type.HandrailHeight:0} high: the IRC asks for 864 to 965 (34 to 38 in).");
            if (layout.WinderNarrowEnd is { } narrow && narrow < 152) problems.Add($"Its winders are {narrow:0} at their narrow end: the IRC asks for 152 (6 in).");
            if (layout.WinderGoing is { } winder && winder * (305 / (layout.Width / 2)) < 254) problems.Add("Its winders are too shallow at the walkline 305 (12 in) from their narrow end: the IRC asks for 254 (10 in).");
        }
        else
        {
            if (rise > 220 + 0.5) problems.Add($"Each riser is {rise:0}: Approved Document K allows 220 at most on a private stair.");
            if (going < 220 - 0.5) problems.Add($"Each going is {going:0}: Approved Document K asks for 220 at least on a private stair.");
            if (layout.Pitch > 42 + 0.05) problems.Add($"It is pitched at {layout.Pitch:0.0}°: Approved Document K allows 42° at most on a private stair.");
            var twice = 2 * rise + going;
            if (twice < 550 - 0.5 || twice > 700 + 0.5) problems.Add($"Twice the rise plus the going is {twice:0}: Approved Document K asks for 550 to 700.");
            if (type.HandrailHeight < 900 - 0.5 || type.HandrailHeight > 1000 + 0.5) problems.Add($"Its handrail is {type.HandrailHeight:0} high: Approved Document K asks for 900 to 1000.");
            if (layout.WinderNarrowEnd is { } narrow && narrow < 50) problems.Add($"Its winders are {narrow:0} at their narrow end: Approved Document K asks for 50.");
            if (layout.WinderGoing is { } winder && winder < 220 - 0.5) problems.Add($"Its winders' going along the middle is {winder:0}: Approved Document K asks for the flight's 220 there.");
            if (layout.Width >= 1000 && stair.Railings is StairRailings.LeftSide or StairRailings.RightSide or StairRailings.None)
                problems.Add("It is 1000 wide or more: Approved Document K asks for a handrail on both sides.");
        }

        if (stair.Railings == StairRailings.None) problems.Add("It has no handrail: a stair needs one on at least one side.");

        var needed = Headroom(standard);
        if (LeastHeadroom(document, stair, layout) is { } least && least.Clear < needed - 0.5)
            problems.Add($"Its headroom over step {least.Step} is {least.Clear:0}: {Label(standard)} asks for {needed:0}.");

        return problems;
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
