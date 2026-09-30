using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>The shape of a shaft opening in plan.</summary>
public enum ShaftShape
{
    Rectangle,
    Round
}

/// <summary>
/// A shaft opening - Revit's Shaft Opening: a hole cut straight down through every roof, floor
/// and ceiling it passes through, from its base to its top. A chimney or flue through a roof,
/// a stairwell or lift shaft through the floors, a riser for pipes and ducts.
///
/// It belongs to nothing. It is set where it is in plan and between two heights, and whatever
/// is there is cut: a floor drawn across it later has the hole in it too, and moving it moves
/// the hole in everything it passes through at once.
/// </summary>
public sealed class ShaftOpening : Element
{
    /// <summary>The top constraint of a shaft whose top is its base plus its height.</summary>
    public const string Unconnected = "Unconnected";

    public override BuiltInCategory Category => BuiltInCategory.ShaftOpenings;

    /// <summary>Its middle, in plan.</summary>
    public Point2D Location { get; set; }

    /// <summary>How wide it is along its angle, mm; a round one's diameter.</summary>
    public double Width { get; set; } = 600;

    /// <summary>How deep it is across its angle, mm. A round one has only its diameter.</summary>
    public double Depth { get; set; } = 600;

    public ShaftShape Shape { get; set; } = ShaftShape.Rectangle;

    /// <summary>Which way its width runs in plan, degrees anticlockwise from the project's X axis.</summary>
    public double Angle { get; set; }

    /// <summary>How far its bottom is above its level, mm.</summary>
    public double BaseOffset { get; set; }

    /// <summary>The level its top is held to, or null for a top its height above its base.</summary>
    public Guid? TopLevelId { get; set; }

    /// <summary>How far its top is above its top level, mm.</summary>
    public double TopOffset { get; set; }

    /// <summary>How tall it is when its top is not held to a level, mm.</summary>
    public double UnconnectedHeight { get; set; } = 3000;

    public double BaseElevation(BimDocument document) => (document.FindLevel(LevelId)?.Elevation ?? 0) + BaseOffset;

    public double TopElevation(BimDocument document) =>
        TopLevelId is { } id && document.FindLevel(id) is { } level
            ? level.Elevation + TopOffset
            : BaseElevation(document) + UnconnectedHeight;

    /// <summary>Its outline in plan, anticlockwise: a rectangle, or a circle drawn as many short sides.</summary>
    public IReadOnlyList<Point2D> Outline
    {
        get
        {
            var turn = Angle * Math.PI / 180;
            var along = new Vector2D(Math.Cos(turn), Math.Sin(turn));
            var across = along.PerpendicularLeft();

            if (Shape == ShaftShape.Round)
            {
                var radius = Width / 2;
                return Enumerable.Range(0, Shafts.RoundSides)
                    .Select(i => 2 * Math.PI * i / Shafts.RoundSides)
                    .Select(a => Location + along * (radius * Math.Cos(a)) + across * (radius * Math.Sin(a)))
                    .ToList();
            }

            var (w, d) = (along * (Width / 2), across * (Depth / 2));
            return new[] { Location - w - d, Location + w - d, Location + w + d, Location - w + d };
        }
    }

    /// <summary>The cross a plan draws through it: corner to corner, and across a round one at 45°.</summary>
    public IReadOnlyList<(Point2D From, Point2D To)> Cross
    {
        get
        {
            var outline = Outline;
            if (outline.Count == 4) return new[] { (outline[0], outline[2]), (outline[1], outline[3]) };

            var eighth = outline.Count / 8;
            return new[] { (outline[eighth], outline[5 * eighth]), (outline[3 * eighth], outline[7 * eighth]) };
        }
    }

    /// <summary>The top constraint as the property panel shows it.</summary>
    public string TopConstraintName(BimDocument document) =>
        TopLevelId is { } id && document.FindLevel(id) is { } level ? $"Up to level: {level.Name}" : Unconnected;

    /// <summary>What the top can be held to: nothing, or a level above its base.</summary>
    public IReadOnlyList<string> TopConstraintChoices(BimDocument document)
    {
        var bottom = BaseElevation(document);
        var choices = new List<string> { Unconnected };
        choices.AddRange(document.Levels
            .Where(level => level.Elevation > bottom || level.Id == TopLevelId)
            .Select(level => $"Up to level: {level.Name}"));
        return choices;
    }

    /// <summary>
    /// The change that sets the top constraint to one of <see cref="TopConstraintChoices"/>, or
    /// null where its top would not be above its bottom. Letting go of the level keeps the
    /// shaft as tall as it is now.
    /// </summary>
    public IUndoableCommand? SetTopConstraint(BimDocument document, string choice)
    {
        if (choice == Unconnected)
            return TopLevelId is null
                ? null
                : new SetShaftTopCommand(this, null, TopOffset, TopElevation(document) - BaseElevation(document));

        var level = document.Levels.FirstOrDefault(candidate => $"Up to level: {candidate.Name}" == choice);
        if (level is null || level.Elevation + TopOffset <= BaseElevation(document)) return null;

        return new SetShaftTopCommand(this, level.Id, TopOffset, UnconnectedHeight);
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoiceValidated(
            ShaftParameters.BaseConstraint,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            name =>
            {
                if (document.Levels.FirstOrDefault(level => level.Name == name) is not { } level) return false;
                if (TopLevelId is { } top && document.FindLevel(top) is { } topLevel &&
                    topLevel.Elevation + TopOffset <= level.Elevation + BaseOffset) return false;

                LevelId = level.Id;
                return true;
            },
            document.Levels.Select(level => level.Name).ToArray());

        yield return ParameterValue.BindValidated(
            ShaftParameters.BaseOffset,
            () => BaseOffset,
            offset =>
            {
                if (TopLevelId is not null && TopElevation(document) <= BaseElevation(document) - BaseOffset + offset) return false;
                BaseOffset = offset;
                return true;
            });

        yield return ParameterValue.BindChoiceCommand(
            ShaftParameters.TopConstraint,
            () => TopConstraintName(document),
            choice => SetTopConstraint(document, choice),
            TopConstraintChoices(document));

        // The top offset means something only against a level, the height only without one.
        if (TopLevelId is null)
        {
            yield return ParameterValue.ReadOnly(ShaftParameters.TopOffset, () => TopOffset);
            yield return ParameterValue.BindValidated(ShaftParameters.UnconnectedHeight, () => UnconnectedHeight, height =>
            {
                if (height <= 0) return false;
                UnconnectedHeight = height;
                return true;
            });
        }
        else
        {
            yield return ParameterValue.BindValidated(ShaftParameters.TopOffset, () => TopOffset, offset =>
            {
                if (TopElevation(document) - TopOffset + offset <= BaseElevation(document)) return false;
                TopOffset = offset;
                return true;
            });
            yield return ParameterValue.ReadOnly(ShaftParameters.UnconnectedHeight, () => TopElevation(document) - BaseElevation(document));
        }

        yield return ParameterValue.BindChoice(
            ShaftParameters.Shape,
            () => EnumText.Humanise(Shape),
            text => { if (EnumText.TryParse<ShaftShape>(text, out var shape)) Shape = shape; },
            EnumText.Choices<ShaftShape>());

        if (Shape == ShaftShape.Round)
        {
            yield return Positive(ShaftParameters.Diameter, () => Width, value => Width = value);
        }
        else
        {
            yield return Positive(ShaftParameters.Width, () => Width, value => Width = value);
            yield return Positive(ShaftParameters.Depth, () => Depth, value => Depth = value);
        }

        yield return ParameterValue.Bind(ShaftParameters.Rotation, () => Angle, value => Angle = value);
        yield return ParameterValue.ReadOnly(ShaftParameters.Cuts, () => Shafts.CutSummary(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    private static ParameterValue Positive(ParameterDefinition definition, Func<double> get, Action<double> set) =>
        ParameterValue.BindValidated(definition, get, value =>
        {
            if (value < Shafts.SmallestSize) return false;
            set(value);
            return true;
        });
}

/// <summary>Sets where a shaft's top is held: to a level with an offset, or its height above its base.</summary>
public sealed class SetShaftTopCommand : IUndoableCommand
{
    private readonly ShaftOpening _shaft;
    private readonly (Guid? Level, double Offset, double Height) _before;
    private readonly (Guid? Level, double Offset, double Height) _after;

    public SetShaftTopCommand(ShaftOpening shaft, Guid? level, double offset, double height)
    {
        _shaft = shaft;
        _before = (shaft.TopLevelId, shaft.TopOffset, shaft.UnconnectedHeight);
        _after = (level, offset, height);
    }

    public string Name => "Shaft Top";

    public void Redo() => Apply(_after);

    public void Undo() => Apply(_before);

    private void Apply((Guid? Level, double Offset, double Height) top) =>
        (_shaft.TopLevelId, _shaft.TopOffset, _shaft.UnconnectedHeight) = top;
}

/// <summary>
/// Shaft openings: what each one cuts - every roof, floor and ceiling it passes through - and a
/// slab as built with the shafts through it taken out.
/// </summary>
public static class Shafts
{
    /// <summary>How many sides a round shaft is drawn and cut with.</summary>
    public const int RoundSides = 32;

    /// <summary>The smallest a shaft may be across, mm.</summary>
    public const double SmallestSize = 50;

    /// <summary>How far into a slab a shaft must reach to cut it, mm: touching its face is not passing through it.</summary>
    private const double Reach = 1;

    /// <summary>How much of a shaft's outline a slab must cover to be cut, mm².</summary>
    private const double MinimumOverlap = 100;

    /// <summary>How tall a shaft is made when there is nothing above it to reach, mm.</summary>
    public const double DefaultHeight = 3000;

    /// <summary>How far past the top of a roof a shaft put through it is taken, mm, so it clears it at every point.</summary>
    public const double ClearAbove = 150;

    public static IEnumerable<ShaftOpening> All(BimDocument document) => document.Elements.OfType<ShaftOpening>();

    /// <summary>
    /// Whether a shaft passes through a slab: over it in plan, and reaching into it from above or
    /// below - into a floor's depth, a ceiling's, a roof's anywhere over the shaft. One that
    /// stands on a floor, its bottom on the floor's top, does not cut it.
    /// </summary>
    public static bool Cuts(BimDocument document, ShaftOpening shaft, Slab slab)
    {
        if (slab.Boundary.Count < 3 || shaft.Width <= 0) return false;
        if (slab is Roof { IsExtrusion: true }) return false;

        var outline = shaft.Outline;
        var over = PolygonBoolean.Combine(slab.Boundary, outline, BooleanOperation.Intersection);
        if (over.Sum(region => region.Area) < MinimumOverlap) return false;

        var (bottom, top) = Span(document, slab, over.SelectMany(region => region.Outer).Append(shaft.Location).ToList());
        return shaft.BaseElevation(document) < top - Reach && shaft.TopElevation(document) > bottom + Reach;
    }

    /// <summary>How low and how high a slab goes over some points: a flat one its depth, a roof its slope.</summary>
    private static (double Bottom, double Top) Span(BimDocument document, Slab slab, IReadOnlyList<Point2D> points)
    {
        if (slab is not Roof roof) return (slab.GetBottomElevation(document), slab.GetTopElevation(document));

        var inside = points.Where(roof.Contains).DefaultIfEmpty(points[0]).ToList();
        return (inside.Min(point => roof.UndersideAt(document, point)), inside.Max(point => roof.TopAt(document, point)));
    }

    /// <summary>The holes the shafts through a slab cut in it: each one's outline in plan.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Holes(BimDocument document, Slab slab) =>
        All(document).Where(shaft => Cuts(document, shaft, slab)).Select(shaft => shaft.Outline).ToList();

    /// <summary>The shafts that pass through a slab.</summary>
    public static IEnumerable<ShaftOpening> Through(BimDocument document, Slab slab) =>
        All(document).Where(shaft => Cuts(document, shaft, slab));

    /// <summary>A slab's plan as built: its outline with the shafts through it taken out, each part with its holes.</summary>
    public static IReadOnlyList<PolygonBoolean.Region> Regions(BimDocument document, Slab slab)
    {
        IReadOnlyList<PolygonBoolean.Region> regions = new[] { new PolygonBoolean.Region(slab.Boundary, Array.Empty<IReadOnlyList<Point2D>>()) };

        foreach (var hole in Holes(document, slab))
            regions = regions
                .SelectMany(region => PolygonBoolean.Combine(Loops(region), new[] { hole }, BooleanOperation.Difference))
                .ToList();

        return regions;
    }

    /// <summary>A region's loops: its outer one, then its holes.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Loops(PolygonBoolean.Region region) =>
        region.Holes.Prepend(region.Outer).ToList();

    /// <summary>
    /// A slab as the plain outlines it is built from: the whole of it where nothing passes
    /// through it, otherwise cut across each hole into pieces with none.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Pieces(BimDocument document, Slab slab) =>
        Through(document, slab).Any() ? Pieces(Regions(document, slab)) : new[] { slab.Boundary };

    /// <summary>Regions as the plain outlines they are built from, each cut across its holes.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Pieces(IReadOnlyList<PolygonBoolean.Region> regions) =>
        regions
            .SelectMany(PolygonBoolean.WithoutHoles)
            .Where(outline => Polygon2D.Area(outline) > MinimumOverlap)
            .ToList();

    /// <summary>
    /// Whether an edge of a piece of a slab is one it was cut along to leave no holes, rather
    /// than an edge of the slab or of a hole: inside the slab, with slab on both sides of it.
    /// </summary>
    public static Func<Point2D, Point2D, bool> CutAlong(IReadOnlyList<PolygonBoolean.Region> regions)
    {
        var edges = regions.SelectMany(Loops)
            .SelectMany(loop => loop.Select((point, i) => (From: point, To: loop[(i + 1) % loop.Count])))
            .ToList();

        return (a, b) =>
        {
            var middle = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            return !edges.Any(edge => Line2D.DistanceFromSegment(middle, edge.From, edge.To) < 0.5);
        };
    }

    /// <summary>A floor's or ceiling's area less the shafts through it, mm².</summary>
    public static double NetArea(BimDocument document, Slab slab) =>
        Through(document, slab).Any() ? Regions(document, slab).Sum(region => region.Area) : slab.Area;

    /// <summary>What a shaft passes through, for the property panel: "1 roof, 2 floors".</summary>
    public static string CutSummary(BimDocument document, ShaftOpening shaft)
    {
        var cut = document.Elements.OfType<Slab>().Where(slab => Cuts(document, shaft, slab)).ToList();
        if (cut.Count == 0) return "Nothing";

        string Count(int count, string one) => count == 1 ? $"1 {one}" : $"{count} {one}s";

        var parts = new List<string>();
        var roofs = cut.OfType<Roof>().Count();
        var floors = cut.OfType<Floor>().Count();
        var ceilings = cut.OfType<Ceiling>().Count();
        if (roofs > 0) parts.Add(Count(roofs, "roof"));
        if (floors > 0) parts.Add(Count(floors, "floor"));
        if (ceilings > 0) parts.Add(Count(ceilings, "ceiling"));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// A new shaft put in on a level, its middle at a point. Under a roof on that level it goes
    /// up through the roof and a little past its top - a chimney clicked onto a roof. Otherwise
    /// it goes up to the next level, through its floor, as a stairwell or a riser does; a storey
    /// high where there is no level above.
    /// </summary>
    public static ShaftOpening Placed(BimDocument document, Guid levelId, Point2D at, ShaftShape shape, double width, double depth)
    {
        var shaft = new ShaftOpening { LevelId = levelId, Location = at, Shape = shape, Width = width, Depth = depth };

        var bottom = document.FindLevel(levelId)?.Elevation ?? 0;
        if (RoofTopOver(document, levelId, shaft.Outline) is { } roofTop)
            shaft.UnconnectedHeight = Math.Ceiling((roofTop + ClearAbove - bottom) / 50) * 50;
        else if (document.Levels.Where(level => level.Elevation > bottom + Reach).OrderBy(level => level.Elevation).FirstOrDefault() is { } above)
            shaft.TopLevelId = above.Id;
        else
            shaft.UnconnectedHeight = DefaultHeight;

        return shaft;
    }

    /// <summary>
    /// A new shaft through just one floor, ceiling or roof, its middle at a point: what a click
    /// on one in 3D makes. A roof is gone through as a shaft put in on its level goes through it.
    /// </summary>
    public static ShaftOpening PlacedIn(BimDocument document, Slab slab, Point2D at, ShaftShape shape, double width, double depth)
    {
        if (slab is Roof) return Placed(document, slab.LevelId, at, shape, width, depth);

        var level = document.FindLevel(slab.LevelId)?.Elevation ?? 0;
        var bottom = slab.GetBottomElevation(document);
        var top = slab.GetTopElevation(document);

        return new ShaftOpening
        {
            LevelId = slab.LevelId, Location = at, Shape = shape, Width = width, Depth = depth,
            BaseOffset = bottom - level, UnconnectedHeight = Math.Max(top - bottom, SmallestSize)
        };
    }

    /// <summary>The highest the roofs on a level reach over an outline, or null where none is over it.</summary>
    private static double? RoofTopOver(BimDocument document, Guid levelId, IReadOnlyList<Point2D> outline)
    {
        var points = outline.Append(Centre(outline)).ToList();

        var tops = document.Elements.OfType<Roof>()
            .Where(roof => roof.LevelId == levelId && !roof.IsExtrusion)
            .SelectMany(roof => points.Where(roof.Contains).Select(point => roof.TopAt(document, point)))
            .ToList();

        return tops.Count == 0 ? null : tops.Max();
    }

    private static Point2D Centre(IReadOnlyList<Point2D> outline) =>
        new(outline.Average(point => point.X), outline.Average(point => point.Y));

    /// <summary>
    /// Whether a shaft is drawn on a level's plan: on its own level, and on every level it
    /// passes up through. It is not drawn on the level its top is held to - the hole is in that
    /// floor, and the floor shows it.
    /// </summary>
    public static bool ShownOn(BimDocument document, ShaftOpening shaft, Guid levelId)
    {
        if (shaft.LevelId == levelId) return true;
        if (document.FindLevel(levelId) is not { } level) return false;

        return level.Elevation >= shaft.BaseElevation(document) - Reach && level.Elevation < shaft.TopElevation(document) - Reach;
    }
}

public static class ShaftParameters
{
    public static readonly ParameterDefinition BaseConstraint = new("Base Constraint", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BaseOffset = new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition TopConstraint = new("Top Constraint", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition TopOffset = new("Top Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition UnconnectedHeight = new("Unconnected Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Shape = new("Shape", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Depth = new("Depth", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Diameter = new("Diameter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rotation = new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Cuts = new("Cuts Through", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Other);
}
