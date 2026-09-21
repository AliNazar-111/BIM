using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Where a wall's sweeps and reveals run, worked out once for every view that draws them.
/// </summary>
public static class WallSweeps
{
    /// <summary>Stretches shorter than this are not built.</summary>
    private const double MinimumRun = 1;

    /// <summary>
    /// The stretches of a wall's face a sweep runs along, as distances along the wall: from one
    /// corner of the face to the other, broken wherever a door or window reaches the sweep's
    /// height - a skirting stops at a doorway, a string course carries on over a window below it.
    /// </summary>
    public static IReadOnlyList<(double From, double To)> Runs(
        BimDocument document, Wall wall, WallType type, WallSide side, double bottom, double top)
    {
        var structure = type.Structure;
        var half = type.Width / 2;
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);

        var start = WallJoins.EndPoints(wall, type, half, -half, startCut, atStart: true);
        var end = WallJoins.EndPoints(wall, type, half, -half, endCut, atStart: false);

        var from = wall.Locate(structure, side == WallSide.Exterior ? start[0] : start[^1]).Along;
        var to = wall.Locate(structure, side == WallSide.Exterior ? end[0] : end[^1]).Along;

        var runs = new List<(double From, double To)> { (from, to) };
        var wallBottom = wall.GetBaseElevation(document);

        foreach (var opening in WallOpenings.Of(document, wall))
        {
            if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType) continue;

            var sill = wallBottom + opening.SillHeight;
            var head = sill + openingType.Height;
            if (head <= bottom || sill >= top) continue;

            var (gapFrom, gapTo) = opening.GetSpan(openingType);

            runs = runs
                .SelectMany(run => run.To <= gapFrom || run.From >= gapTo
                    ? new[] { run }
                    : new[] { (run.From, Math.Min(run.To, gapFrom)), (Math.Max(run.From, gapTo), run.To) })
                .Where(run => run.Item2 - run.Item1 > MinimumRun)
                .ToList();
        }

        return runs;
    }

    /// <summary>The reveals of a wall type on a wall between these elevations: where, which side, how deep.</summary>
    public static IReadOnlyList<(double Bottom, double Top, WallSide Side, double Depth)> Reveals(
        WallType type, double bottom, double top) =>
        type.Sweeps
            .Where(sweep => sweep.Kind == SweepKind.Reveal)
            .Select(sweep =>
            {
                var (b, t) = sweep.Span(bottom, top);
                return (b, t, sweep.Side, sweep.Depth);
            })
            .ToList();

    /// <summary>
    /// A band of the wall with the reveals at this height taken out of it. Null when the band is
    /// entirely inside a reveal, and so not there at all.
    /// </summary>
    public static (double Outer, double Inner)? Recess(
        double outer, double inner, double half,
        IEnumerable<(double Bottom, double Top, WallSide Side, double Depth)> reveals, double elevation)
    {
        foreach (var (bottom, top, side, depth) in reveals)
        {
            if (elevation <= bottom || elevation >= top) continue;

            if (side == WallSide.Exterior)
            {
                var face = half - depth;
                if (inner >= face) return null;
                outer = Math.Min(outer, face);
            }
            else
            {
                var face = -half + depth;
                if (outer <= face) return null;
                inner = Math.Max(inner, face);
            }
        }

        return (outer, inner);
    }
}
