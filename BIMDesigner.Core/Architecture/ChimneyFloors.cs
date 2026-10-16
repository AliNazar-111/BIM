using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A fireplace on one floor of a chimney: the floor it stands on, what burns in it, and the flue
/// it has to itself - two fireplaces never share a flue, or the smoke of one comes back into the
/// house through the other.
/// </summary>
/// <param name="LevelId">The floor it is on: the chimney's own level for the one at its foot.</param>
/// <param name="Floor">How high that floor is.</param>
/// <param name="Kind">An open fire or a stove.</param>
/// <param name="Lane">Which of the stack's flues is its own, counted along the stack.</param>
/// <param name="Ceiling">The most its breast rises to: the next floor up, or short of the roof.</param>
public sealed record ChimneyHearth(Guid LevelId, double Floor, ChimneyFireplace Kind, int Lane, double Ceiling);

/// <summary>
/// One flue up a stack, as a line through its middle from where it starts to the top: straight
/// up from its foot, or up from over a fireplace and leaning across to its place in the stack.
/// </summary>
public sealed record FlueRun(int Lane, IReadOnlyList<(double Z, Point2D At)> Path, ChimneyHearth? Hearth)
{
    public double From => Path[0].Z;

    /// <summary>Where it has come upright in its place in the stack, to run straight on to the top.</summary>
    public double Upright => Path[^2].Z;

    /// <summary>Where it is at a height, in plan; null below its start or above the top.</summary>
    public Point2D? At(double z)
    {
        if (z < Path[0].Z - 1e-6 || z > Path[^1].Z + 1e-6) return null;
        for (var i = 0; i + 1 < Path.Count; i++)
        {
            var ((z0, a), (z1, b)) = (Path[i], Path[i + 1]);
            if (z > z1 + 1e-6) continue;
            var share = z1 - z0 < 1e-9 ? 1 : Math.Clamp((z - z0) / (z1 - z0), 0, 1);
            return a + (b - a) * share;
        }

        return Path[^1].At;
    }
}

/// <summary>
/// Chimneys with a fireplace on more than one floor: a stack carrying a flue for each - with a
/// withe of brick between each pair - the flue from a fireplace below rising straight past the
/// one above it, beside its opening, and each fireplace's own flue gathered over it and leaning
/// across to its place in the stack no more steeply than its rule allows.
/// </summary>
public static partial class Chimneys
{
    /// <summary>
    /// The brick between flues in one stack, and between a flue and the stack's face, mm: a half
    /// brick - 4 in under the IRC (R1003.13), bonded into the chimney's walls.
    /// </summary>
    public const double Withe = 100;

    /// <summary>The brick between a fireplace's opening and the flue rising past it, mm: Approved Document J's least jamb.</summary>
    public const double Jamb = 200;

    /// <summary>How far over a fireplace's opening its smoke gathers before the flue leans, mm.</summary>
    public const double Gather = 300;

    /// <summary>How far above the top of a fireplace's opening its damper is, mm: at least 8 in, under the IRC (R1001.7.1).</summary>
    public const double DamperAbove = 203;

    /// <summary>The least height a breast with a fireplace in it is built to, mm.</summary>
    public const double LeastBreast = 1500;

    /// <summary>How tall a breast is built when nothing above stops it sooner, mm: to near the ceiling.</summary>
    public const double BreastHeight = 2400;

    /// <summary>
    /// The most a flue leans from upright, degrees: 45° under Approved Document J; 30° under the
    /// IRC, whose clay flue lining is carried up no more than that - followed by a chimney built
    /// to the US 3-2-10 rule.
    /// </summary>
    public static double LeanLimit(Chimney chimney) => chimney.Rule == ChimneyRule.ThreeTwoTen ? 30 : 45;

    /// <summary>Whether a chimney's type is built with a breast a fireplace opens out of: masonry, or a block system.</summary>
    public static bool BuildsBreasts(BimDocument document, Chimney chimney) =>
        TypeOf(document, chimney).Construction is ChimneyConstruction.Masonry or ChimneyConstruction.PrecastBlock;

    /// <summary>
    /// The floors above its foot a chimney could have a fireplace on: those its stack rises
    /// through with room under the roof for a breast.
    /// </summary>
    public static IReadOnlyList<Level> UpperFloors(BimDocument document, Chimney chimney)
    {
        if (!BuildsBreasts(document, chimney)) return Array.Empty<Level>();

        var foot = Foot(document, chimney);
        var under = UnderRoof(document, chimney);
        return document.Levels
            .Where(level => level.Elevation > foot + LeastBreast && level.Elevation + LeastBreast <= under)
            .OrderBy(level => level.Elevation)
            .ToList();
    }

    /// <summary>The most a breast rises to, short of the roof the chimney goes out through.</summary>
    private static double UnderRoof(BimDocument document, Chimney chimney) =>
        HighestContact(document, chimney) is { } contact ? contact - 600 : Top(document, chimney) - 600;

    /// <summary>
    /// A chimney's fireplaces from the bottom up, each with its own flue. The flues no fireplace
    /// has - from a boiler, say - come first along the stack, rising from its foot past every
    /// fireplace; then each fireplace's in turn.
    /// </summary>
    public static IReadOnlyList<ChimneyHearth> Hearths(BimDocument document, Chimney chimney)
    {
        if (!BuildsBreasts(document, chimney)) return Array.Empty<ChimneyHearth>();

        var floors = new List<(Guid Level, double Floor, ChimneyFireplace Kind)>();
        if (chimney.Fireplace != ChimneyFireplace.None) floors.Add((chimney.LevelId, Foot(document, chimney), chimney.Fireplace));
        foreach (var level in UpperFloors(document, chimney))
            if (chimney.FireplaceOn(level.Id) is var kind and not ChimneyFireplace.None)
                floors.Add((level.Id, level.Elevation, kind));

        if (floors.Count == 0) return Array.Empty<ChimneyHearth>();

        var spare = Math.Max(0, chimney.Flues - floors.Count);
        var under = UnderRoof(document, chimney);
        return floors.Select((floor, i) =>
        {
            var next = document.Levels.Where(level => level.Elevation > floor.Floor + 1).Select(level => level.Elevation).DefaultIfEmpty(double.PositiveInfinity).Min();
            return new ChimneyHearth(floor.Level, floor.Floor, floor.Kind, spare + i, Math.Min(Math.Min(floor.Floor + BreastHeight, under), next));
        }).ToList();
    }

    /// <summary>How many flues a chimney carries: as many as it is given, and one for each of its fireplaces at least.</summary>
    public static int FlueCount(BimDocument document, Chimney chimney) =>
        Math.Max(Math.Max(1, chimney.Flues), Hearths(document, chimney).Count);

    /// <summary>How wide a stack has to be for its flues: each liner, with a withe of brick between and either side.</summary>
    public static double WidthFor(int flues) => flues * LinerSize + (flues + 1) * Withe;

    /// <summary>
    /// The flue liners' outside: in a masonry stack a clay liner kept a withe of brick from the
    /// next and from the face; in a block system its own ceramic liner.
    /// </summary>
    public static double LinerWidth(BimDocument document, Chimney chimney)
    {
        var type = TypeOf(document, chimney);
        var count = FlueCount(document, chimney);
        if (type.Construction == ChimneyConstruction.PrecastBlock)
            return Math.Max(60, Math.Min(type.FlueDiameter + 40, Math.Min(chimney.Width / count, chimney.Depth) - 120));

        var room = Math.Min((chimney.Width - (count + 1) * Withe) / count, chimney.Depth - 2 * Withe);
        return Math.Max(100, Math.Min(LinerSize, room));
    }

    /// <summary>How far along the stack from its middle each flue is: evenly spaced, with as much brick between them as at the sides.</summary>
    public static IReadOnlyList<double> FlueOffsets(BimDocument document, Chimney chimney)
    {
        var count = FlueCount(document, chimney);
        var liner = LinerWidth(document, chimney);
        var withe = (chimney.Width - count * liner) / (count + 1);
        return withe > 0
            ? Enumerable.Range(0, count).Select(i => -chimney.Width / 2 + withe + liner / 2 + i * (liner + withe)).ToList()
            : Enumerable.Range(0, count).Select(i => chimney.Width * ((i + 0.5) / count - 0.5)).ToList();
    }

    /// <summary>
    /// The breast round a fireplace on one floor: wider and deeper than the stack, its back on the
    /// stack's back. Where flues from fireplaces below rise through it, the opening is set beside
    /// them with a jamb of brick between - the breast running on past the stack to take it.
    /// </summary>
    public static ChimneyBreast? BreastFor(BimDocument document, Chimney chimney, ChimneyHearth hearth)
    {
        if (hearth.Ceiling - hearth.Floor < LeastBreast) return null;

        var front = Front(chimney);
        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        var depth = Math.Max(chimney.Depth + 150, 600);
        var stove = hearth.Kind == ChimneyFireplace.Stove;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();

        // A stove's recess as wide as the breast allows; an open fire's opening the size its flue is
        // sized for - 500 by 550, the largest Approved Document J's Table 2 gives a 200 flue for; 24 in square in the US.
        var (openingWidth, openingHeight) = stove
            ? (Math.Max(500, Math.Min(Math.Max(chimney.Width, 1200) - 450, 900)), 750.0)
            : us ? (610.0, 610.0) : (500.0, 550.0);

        // Past the flues rising from below, a jamb of brick between them and the opening.
        var offsets = FlueOffsets(document, chimney);
        var liner = LinerWidth(document, chimney);
        var middle = hearth.Lane == 0 ? 0 : Math.Max(0, offsets[hearth.Lane - 1] + liner / 2 + Jamb + openingWidth / 2);

        var left = Math.Min(-chimney.Width / 2, middle - openingWidth / 2 - Jamb);
        var right = Math.Max(chimney.Width / 2, middle + openingWidth / 2 + Jamb);
        if (right - left < 1200)
        {
            var grow = (1200 - (right - left)) / 2;
            (left, right) = (left - grow, right + grow);
        }

        var outline = Rectangle(chimney, right - left, depth, chimney.Location + along * ((left + right) / 2) + front * ((depth - chimney.Depth) / 2));
        var openingDepth = Math.Min(depth - 115, stove ? 500 : 400);
        var face = chimney.Location + along * middle + front * (depth - chimney.Depth / 2);
        var opening = Rectangle(chimney, openingWidth, openingDepth + 2, face - front * (openingDepth / 2 - 1));
        // Its hearth: Approved Document J's 150 past each side of the opening and 500 out in front
        // (Diagram 24); the IRC's 8 in past each side and 16 in out - 12 in and 20 in before an
        // opening of 6 sq ft or more (R1001.10).
        var (side, ahead) = !us ? (150.0, 500.0) : openingWidth * openingHeight >= Chimneys.LargeOpening ? (305.0, 508.0) : (203.0, 500.0);
        var before = Rectangle(chimney, openingWidth + 2 * side, openingDepth + ahead, face + front * ((ahead - openingDepth) / 2));

        return new ChimneyBreast(outline, hearth.Ceiling, opening, openingHeight, before);
    }

    /// <summary>The fireplace's breast on a floor of a chimney, or null where it has none there.</summary>
    public static ChimneyBreast? BreastOn(BimDocument document, Chimney chimney, Guid levelId) =>
        Hearths(document, chimney).FirstOrDefault(hearth => hearth.LevelId == levelId) is { } hearth ? BreastFor(document, chimney, hearth) : null;

    /// <summary>
    /// Every flue up a masonry or block stack: those no fireplace has straight up from above its
    /// solid foot; each fireplace's up from over its opening, gathered, then leaning across to
    /// its place in the stack at the steepest its rule allows - in steps, as the liners are laid.
    /// </summary>
    public static IReadOnlyList<FlueRun> FlueRuns(BimDocument document, Chimney chimney)
    {
        if (!BuildsBreasts(document, chimney)) return Array.Empty<FlueRun>();

        var (foot, top) = (Foot(document, chimney), Top(document, chimney));
        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        var offsets = FlueOffsets(document, chimney);
        var hearths = Hearths(document, chimney);
        var runs = new List<FlueRun>();

        var spare = offsets.Count - hearths.Count;
        for (var lane = 0; lane < spare; lane++)
        {
            var at = chimney.Location + along * offsets[lane];
            runs.Add(new FlueRun(lane, new[] { (Math.Min(foot + SolidFoot, top - 300), at), (top, at) }, null));
        }

        var lean = Math.Tan(LeanLimit(chimney) * Math.PI / 180);
        foreach (var hearth in hearths)
        {
            var place = chimney.Location + along * offsets[hearth.Lane];
            if (BreastFor(document, chimney, hearth) is not { } breast)
            {
                runs.Add(new FlueRun(hearth.Lane, new[] { (Math.Min(hearth.Floor + SolidFoot, top - 300), place), (top, place) }, hearth));
                continue;
            }

            var over = new Point2D(breast.Opening.Average(p => p.X), breast.Opening.Average(p => p.Y));
            over = chimney.Location + along * (over - chimney.Location).Dot(along);
            var lintel = hearth.Floor + breast.OpeningHeight;
            var leans = lintel + Gather;
            var rise = over.DistanceTo(place) / lean;
            var steps = Math.Max(1, (int)Math.Ceiling(rise / 75));

            var path = new List<(double, Point2D)> { (lintel, over), (leans, over) };
            for (var step = 1; step <= steps; step++)
                path.Add((leans + rise * step / steps, over + (place - over) * ((double)step / steps)));
            path.Add((Math.Max(top, leans + rise + 1), place));
            runs.Add(new FlueRun(hearth.Lane, path, hearth));
        }

        return runs.OrderBy(run => run.Lane).ToList();
    }

    /// <summary>Where a stack's flues are at a height, in plan: those started by then, wherever their lean has brought them.</summary>
    public static IReadOnlyList<Point2D> FluesAt(BimDocument document, Chimney chimney, double z) =>
        FlueRuns(document, chimney).Select(run => run.At(z)).Where(at => at.HasValue).Select(at => at!.Value).ToList();

    /// <summary>
    /// Why a chimney cannot have its fireplaces as they are - a flue that could not lean across
    /// to its place before the floor above, or the breast for it - or null when every one fits.
    /// </summary>
    public static string? WhyFireplacesDoNotFit(BimDocument document, Chimney chimney)
    {
        foreach (var hearth in Hearths(document, chimney))
        {
            var name = document.FindLevel(hearth.LevelId)?.Name ?? "its floor";
            if (BreastFor(document, chimney, hearth) is null)
                return $"There is no room under the roof on {name} for a breast round a fireplace.";

            var run = FlueRuns(document, chimney).First(candidate => candidate.Hearth == hearth);
            if (run.Upright > hearth.Ceiling + 1e-6)
                return $"The flue from the fireplace on {name} could not lean across to its place in the stack - no more than {LeanLimit(chimney):0}° from upright - before the floor above.";
        }

        return null;
    }
}

/// <summary>
/// A fireplace put in a chimney's breast on one floor - or taken out - as one step: the stack
/// widened where it needs another flue, with a withe of brick between each. Refused, saying
/// why, where its flue, or the flue from a fireplace below, would not fit.
/// </summary>
public sealed class SetChimneyFireplaceCommand : IUndoableCommand
{
    private readonly Chimney _chimney;
    private readonly Guid? _levelId;
    private readonly ChimneyFireplace _before, _after;
    private readonly double _widthBefore, _widthAfter;

    private SetChimneyFireplaceCommand(Chimney chimney, Guid? levelId, ChimneyFireplace before, ChimneyFireplace after, double widthBefore, double widthAfter)
    {
        (_chimney, _levelId, _before, _after, _widthBefore, _widthAfter) = (chimney, levelId, before, after, widthBefore, widthAfter);
    }

    public string Name => _after == ChimneyFireplace.None ? "Remove Fireplace" : "Fireplace";

    /// <summary>
    /// The change to a chimney's fireplace on a floor - its foot, for a null level - or null,
    /// with why, where it does not fit. What was made of it, when the stack had to be widened, is
    /// said too. The chimney is left as it was, for the command to make the change.
    /// </summary>
    public static SetChimneyFireplaceCommand? For(BimDocument document, Chimney chimney, Guid? levelId, ChimneyFireplace kind, out string? message)
    {
        message = null;
        var before = levelId is { } level ? chimney.FireplaceOn(level) : chimney.Fireplace;
        var widthBefore = chimney.Width;

        Set(chimney, levelId, kind);
        var flues = Chimneys.FlueCount(document, chimney);
        var needs = Chimneys.BuildsBreasts(document, chimney) && flues > 1 ? Chimneys.WidthFor(flues) : 0;
        if (chimney.Width < needs) chimney.Width = needs;

        var problem = kind == ChimneyFireplace.None ? null : Chimneys.WhyFireplacesDoNotFit(document, chimney);
        var widthAfter = chimney.Width;
        Set(chimney, levelId, before);
        chimney.Width = widthBefore;

        if (problem is not null)
        {
            message = problem;
            return null;
        }

        if (widthAfter > widthBefore + 1e-6)
            message = $"Each fireplace has a flue of its own, with a withe of brick between: the stack is widened to {widthAfter:0} mm to carry {flues} flues.";

        return new SetChimneyFireplaceCommand(chimney, levelId, before, kind, widthBefore, widthAfter);
    }

    private static void Set(Chimney chimney, Guid? levelId, ChimneyFireplace kind)
    {
        if (levelId is not { } level) chimney.Fireplace = kind;
        else if (kind == ChimneyFireplace.None) chimney.FireplacesAbove.Remove(level);
        else chimney.FireplacesAbove[level] = kind;
    }

    public void Redo()
    {
        Set(_chimney, _levelId, _after);
        _chimney.Width = _widthAfter;
    }

    public void Undo()
    {
        Set(_chimney, _levelId, _before);
        _chimney.Width = _widthBefore;
    }

    /// <summary>The setting for a chimney's fireplace on a floor above its foot, as Properties shows it.</summary>
    public static ParameterValue Parameter(BimDocument document, Chimney chimney, Level level) =>
        ParameterValue.BindChoiceCommand(
            new ParameterDefinition($"Fireplace on {level.Name}", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints),
            () => EnumText.Humanise(chimney.FireplaceOn(level.Id)),
            (string value, out string? message) =>
            {
                message = null;
                return EnumText.TryParse<ChimneyFireplace>(value, out var kind) ? For(document, chimney, level.Id, kind, out message) : null;
            },
            EnumText.Choices<ChimneyFireplace>());
}
