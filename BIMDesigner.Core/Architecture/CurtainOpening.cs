namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Where a window sits inside a curtain wall's panel.
///
/// A door replaces its panel: it fills the bay, mullion to mullion and floor to head, because
/// that is what a doorway in a glazed wall is. A window does not. A window in a curtain wall is
/// an opening vent set in a panel, with glass round it - exactly as a window sits in a wall -
/// and a window drawn to fill its whole bay reads as a door, which is the one thing it is not.
///
/// The grid is left alone either way. Grid lines run the full width of a curtain wall, so
/// cutting one to fit a window would open up every other bay along it - and take the head off
/// any door already there.
/// </summary>
public static class CurtainOpening
{
    /// <summary>The least glass left round a window; below this the window simply fills the panel.</summary>
    public const double Margin = 120;

    /// <summary>
    /// The glass a panel is left with once the openings crossing it are taken out, as upright
    /// rectangles. The panel is cut into bands at every sill and head, and each band into the
    /// stretches between the openings that reach it - so any number of windows can be cut into
    /// one panel and the glass still comes out in one piece per gap.
    /// </summary>
    public static IEnumerable<(double From, double To, double Bottom, double Top)> Panes(
        CurtainCell cell, IReadOnlyList<(double From, double To, double Sill, double Head)> holes)
    {
        var inside = holes
            .Select(h => (From: Math.Max(h.From, cell.ClearFrom), To: Math.Min(h.To, cell.ClearTo),
                Sill: Math.Max(h.Sill, cell.ClearBottom), Head: Math.Min(h.Head, cell.ClearTop)))
            .Where(h => h.To - h.From > 1 && h.Head - h.Sill > 1)
            .ToList();

        if (inside.Count == 0)
        {
            yield return (cell.ClearFrom, cell.ClearTo, cell.ClearBottom, cell.ClearTop);
            yield break;
        }

        var levels = inside.SelectMany(h => new[] { h.Sill, h.Head })
            .Append(cell.ClearBottom).Append(cell.ClearTop)
            .Where(z => z >= cell.ClearBottom - 1 && z <= cell.ClearTop + 1)
            .Distinct().OrderBy(z => z).ToList();

        for (var i = 0; i + 1 < levels.Count; i++)
        {
            var (bottom, top) = (levels[i], levels[i + 1]);
            if (top - bottom <= 1) continue;

            var middle = (bottom + top) / 2;
            var crossing = inside.Where(h => h.Sill < middle && h.Head > middle).OrderBy(h => h.From).ToList();

            var at = cell.ClearFrom;
            foreach (var hole in crossing)
            {
                if (hole.From - at > 1) yield return (at, hole.From, bottom, top);
                at = Math.Max(at, hole.To);
            }

            if (cell.ClearTo - at > 1) yield return (at, cell.ClearTo, bottom, top);
        }
    }

    /// <summary>
    /// What is left of a run once the stretches given are taken out of it - which is how a
    /// mullion is interrupted by the window cut across it rather than running through its glass.
    /// </summary>
    public static IEnumerable<(double From, double To)> Gaps(
        double from, double to, IEnumerable<(double From, double To)> cuts)
    {
        var at = from;

        foreach (var cut in cuts.OrderBy(c => c.From))
        {
            if (cut.From - at > 1) yield return (at, cut.From);
            at = Math.Max(at, cut.To);
        }

        if (to - at > 1) yield return (at, to);
    }

}
