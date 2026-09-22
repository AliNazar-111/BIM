using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>How one end of a sweep's run finishes.</summary>
public enum SweepEndKind
{
    /// <summary>Cut square across: at a door or window, or where it is set back from the wall's end.</summary>
    Square,

    /// <summary>At a corner where the wall joins another: cut along the join, so it meets the neighbour's sweep.</summary>
    Joined,

    /// <summary>At an exposed end of the wall: cut flat, or turned round the end when it returns.</summary>
    Free
}

/// <summary>One end of a run, and for a joined end the line of the join it is cut along.</summary>
public readonly record struct SweepEnd(SweepEndKind Kind, Line2D? Cut = null);

/// <summary>One unbroken stretch of a sweep along a wall's face, as distances along the wall, and how each end finishes.</summary>
public readonly record struct SweepRun(double From, double To, SweepEnd Start, SweepEnd End);

/// <summary>
/// Where a wall's sweeps and reveals run, worked out once for every view that draws them.
/// </summary>
public static class WallSweeps
{
    /// <summary>Stretches shorter than this are not built.</summary>
    private const double MinimumRun = 1;

    /// <summary>The sweeps and reveals placed on this wall on their own - not the ones its type carries - and which element each is.</summary>
    public static IReadOnlyList<(WallSweep Sweep, Guid ElementId)> Placed(BimDocument document, Wall wall) =>
        document.Elements
            .OfType<PlacedSweep>()
            .Where(placed => placed.HostWallIds.Contains(wall.Id))
            .Select(placed => (Sweep: placed.On(document, wall), placed.Id))
            .Where(entry => entry.Sweep is not null)
            .Select(entry => (entry.Sweep!, entry.Id))
            .ToList();

    /// <summary>
    /// The stretches of a wall's face a horizontal sweep runs along: from one corner of the face
    /// to the other, broken wherever a door or window reaches the sweep's height if it is cut by
    /// them, and stopped short of the wall's ends by its setback.
    /// </summary>
    public static IReadOnlyList<SweepRun> Runs(
        BimDocument document, Wall wall, WallType type, WallSweep sweep, double bottom, double top)
    {
        var structure = type.Structure;
        var half = type.Width / 2;
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);

        var start = WallJoins.EndPoints(wall, type, half, -half, startCut, atStart: true);
        var end = WallJoins.EndPoints(wall, type, half, -half, endCut, atStart: false);

        var from = wall.Locate(structure, sweep.Side == WallSide.Exterior ? start[0] : start[^1]).Along;
        var to = wall.Locate(structure, sweep.Side == WallSide.Exterior ? end[0] : end[^1]).Along;

        var runs = new List<SweepRun> { new(from, to, EndOf(startCut), EndOf(endCut)) };
        var wallBottom = wall.GetBaseElevation(document);

        void Break(double gapFrom, double gapTo)
        {
            runs = runs
                .SelectMany(run => run.To <= gapFrom || run.From >= gapTo
                    ? new[] { run }
                    : new[]
                    {
                        run with { To = Math.Min(run.To, gapFrom), End = new SweepEnd(SweepEndKind.Square) },
                        run with { From = Math.Max(run.From, gapTo), Start = new SweepEnd(SweepEndKind.Square) }
                    })
                .Where(run => run.To - run.From > MinimumRun)
                .ToList();
        }

        if (sweep.Cuttable)
        {
            foreach (var opening in WallOpenings.Of(document, wall))
            {
                if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType) continue;

                var sill = wallBottom + opening.SillHeight;
                var head = sill + openingType.Height;
                if (head <= bottom || sill >= top) continue;

                var (gapFrom, gapTo) = opening.GetSpan(openingType);
                Break(gapFrom, gapTo);
            }
        }

        foreach (var hole in CurtainEmbedding.In(document, wall))
        {
            if (wallBottom + hole.Head <= bottom || wallBottom + hole.Sill >= top) continue;
            Break(hole.From, hole.To);
        }

        // On an edited profile a sweep runs only where the wall is there for its whole height.
        // At the wall's ends it still reaches round to the joined face.
        if (WallProfile.Of(document, wall) is { } profile)
        {
            var length = wall.Length;
            var covered = WallProfile.RunsAt(profile, bottom - wallBottom + 1e-3, length)
                .SelectMany(low => WallProfile.RunsAt(profile, top - wallBottom - 1e-3, length)
                    .Select(high => (From: Math.Max(low.From, high.From), To: Math.Min(low.To, high.To))))
                .Where(run => run.To > run.From)
                .ToList();

            runs = runs
                .SelectMany(run => covered.Select(c =>
                {
                    var keepsStart = c.From <= 1e-6;
                    var keepsEnd = c.To >= length - 1e-6;
                    return new SweepRun(
                        keepsStart ? run.From : Math.Max(run.From, c.From),
                        keepsEnd ? run.To : Math.Min(run.To, c.To),
                        keepsStart || c.From <= run.From ? run.Start : new SweepEnd(SweepEndKind.Square),
                        keepsEnd || c.To >= run.To ? run.End : new SweepEnd(SweepEndKind.Square));
                }))
                .Where(run => run.To - run.From > MinimumRun)
                .ToList();
        }

        // Set back from the wall's own ends; a set-back end is cut square.
        if (sweep.Setback > 0)
        {
            runs = runs
                .Select(run => run with
                {
                    From = Math.Abs(run.From - from) < 1e-6 ? run.From + sweep.Setback : run.From,
                    To = Math.Abs(run.To - to) < 1e-6 ? run.To - sweep.Setback : run.To,
                    Start = Math.Abs(run.From - from) < 1e-6 ? new SweepEnd(SweepEndKind.Square) : run.Start,
                    End = Math.Abs(run.To - to) < 1e-6 ? new SweepEnd(SweepEndKind.Square) : run.End
                })
                .Where(run => run.To - run.From > MinimumRun)
                .ToList();
        }

        return runs;
    }

    /// <summary>The stretches alone, for a plain sweep on one side - kept for callers that need nothing more.</summary>
    public static IReadOnlyList<(double From, double To)> Runs(
        BimDocument document, Wall wall, WallType type, WallSide side, double bottom, double top) =>
        Runs(document, wall, type, new WallSweep(SweepKind.Sweep, SweepProfile.Rectangle, side, 1, top - bottom, 0, false, Guid.Empty), bottom, top)
            .Select(run => (run.From, run.To))
            .ToList();

    /// <summary>
    /// Where a point of the profile - this far out from the face - meets the end of a run: square
    /// across at a door or set-back end; along the line of the join at a corner, so the sweeps of
    /// two joined walls meet in a mitre; and at an exposed end that returns, carried on past the
    /// end by as much as it stands out, so it turns the corner in a mitre too.
    /// </summary>
    public static double EndAlong(Wall wall, WallType type, WallSweep sweep, SweepEnd end, double along, bool atStart, double outFromFace)
    {
        var structure = type.Structure;
        var across = Across(type, sweep, outFromFace);

        switch (end.Kind)
        {
            case SweepEndKind.Joined when end.Cut is { } cut:
            {
                var near = wall.PointAt(structure, along, across);
                if (wall.EdgeCrossing(structure, cut, across, near) is not { } hit) return along;

                // A join nearly along the wall would throw the end far away; keep it in reach.
                var met = wall.Locate(structure, hit).Along;
                return Math.Abs(met - along) <= Math.Abs(outFromFace) * 4 + type.Width * 2 + 1 ? met : along;
            }

            case SweepEndKind.Free when sweep.Returns:
                return atStart ? along - outFromFace : along + outFromFace;

            default:
                return along;
        }
    }

    /// <summary>How far across from the wall's centreline a point of a profile is, this far out from the face.</summary>
    public static double Across(WallType type, WallSweep sweep, double outFromFace)
    {
        var sign = sweep.Side == WallSide.Exterior ? 1.0 : -1.0;
        return sign * (type.Width / 2 + outFromFace);
    }

    /// <summary>
    /// The horizontal reveals on a wall between these elevations - its type's, those placed on it,
    /// and the recess a sweep set into the wall cuts for itself: where, which side, how deep.
    /// </summary>
    public static IReadOnlyList<(double Bottom, double Top, WallSide Side, double Depth)> Reveals(
        BimDocument document, Wall wall, WallType type, double bottom, double top) =>
        type.Sweeps.Select(s => (Sweep: s, Placed: false))
            .Concat(Placed(document, wall).Select(p => (p.Sweep, Placed: true)))
            .Where(entry => !entry.Sweep.Vertical)
            .Select(entry => Recessing(entry.Sweep) is { } depth ? (entry.Sweep, depth) : ((WallSweep, double)?)null)
            .OfType<(WallSweep Sweep, double Depth)>()
            .Select(entry =>
            {
                var (b, t) = entry.Sweep.Span(bottom, top);
                return (b, t, entry.Sweep.Side, entry.Depth);
            })
            .ToList();

    /// <summary>The type's reveals alone, for callers with no wall to hand.</summary>
    public static IReadOnlyList<(double Bottom, double Top, WallSide Side, double Depth)> Reveals(
        WallType type, double bottom, double top) =>
        type.Sweeps
            .Where(sweep => !sweep.Vertical && Recessing(sweep) is not null)
            .Select(sweep =>
            {
                var (b, t) = sweep.Span(bottom, top);
                return (b, t, sweep.Side, Recessing(sweep)!.Value);
            })
            .ToList();

    /// <summary>
    /// The upright reveals on a wall - grooves running its full height - as the stretch along it
    /// each takes out, which side, and how deep.
    /// </summary>
    public static IReadOnlyList<(double From, double To, WallSide Side, double Depth)> VerticalReveals(BimDocument document, Wall wall) =>
        Placed(document, wall)
            .Select(p => p.Sweep)
            .Where(sweep => sweep.Vertical && Recessing(sweep) is not null)
            .Select(sweep => (sweep.Along - sweep.Height / 2, sweep.Along + sweep.Height / 2, sweep.Side, Recessing(sweep)!.Value))
            .ToList();

    /// <summary>How far a sweep or reveal cuts into the wall, or null if it does not.</summary>
    private static double? Recessing(WallSweep sweep) => sweep.Kind switch
    {
        SweepKind.Reveal => sweep.Depth,
        SweepKind.Sweep when sweep.CutsWall && sweep.Offset < 0 => -sweep.Offset,
        _ => null
    };

    /// <summary>
    /// A stretch of wall split wherever an upright reveal starts or ends, each piece with the
    /// reveals over it as full-height bands, so it can be built or drawn with the groove taken out.
    /// </summary>
    public static IEnumerable<(WallSlice Slice, IReadOnlyList<(double Bottom, double Top, WallSide Side, double Depth)> Bands)> SplitAtVerticalReveals(
        Wall wall, WallType type, WallSlice slice, IReadOnlyList<(double From, double To, WallSide Side, double Depth)> verticals)
    {
        var inside = verticals.Where(v => v.To > slice.From && v.From < slice.To).ToList();
        if (inside.Count == 0)
        {
            yield return (slice, Array.Empty<(double, double, WallSide, double)>());
            yield break;
        }

        var cuts = inside.SelectMany(v => new[] { v.From, v.To })
            .Where(x => x > slice.From + MinimumRun && x < slice.To - MinimumRun)
            .Append(slice.From).Append(slice.To)
            .Distinct().OrderBy(x => x).ToList();

        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var (a, b) = (cuts[i], cuts[i + 1]);
            var piece = new WallSlice(a, b,
                i == 0 ? slice.CutFrom : WallSlices.JambAt(wall, type, a),
                i == cuts.Count - 2 ? slice.CutTo : WallSlices.JambAt(wall, type, b));
            var middle = (a + b) / 2;
            var bands = inside
                .Where(v => middle > v.From && middle < v.To)
                .Select(v => (double.NegativeInfinity, double.PositiveInfinity, v.Side, v.Depth))
                .ToList();
            yield return (piece, bands);
        }
    }

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

    /// <summary>How a run finishes at a wall end cut like this: along the join, or free.</summary>
    private static SweepEnd EndOf(WallCut cut)
    {
        if (cut.Condition == WallEndCondition.Free) return new SweepEnd(SweepEndKind.Free);

        var points = cut.Points;
        return points.Count >= 2 && points[0].DistanceTo(points[^1]) > 1e-6
            ? new SweepEnd(SweepEndKind.Joined, Line2D.Through(points[0], points[^1]))
            : new SweepEnd(SweepEndKind.Square);
    }
}
