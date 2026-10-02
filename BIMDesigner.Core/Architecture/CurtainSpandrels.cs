using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Spandrels at floors: where a floor runs behind a curtain wall, the wall has a transom at the
/// floor's top and another at its underside - or at the ceiling's, where one hangs just under it
/// - and the band between is an opaque spandrel panel. It hides what is there: the slab's edge,
/// the fire-stopped slot between it and the glass, and the void above the ceiling. The clear
/// glass is left to run from floor to ceiling.
/// </summary>
public static class CurtainSpandrels
{
    /// <summary>How near a band's edge an existing grid line may be before it goes, mm: nearer, it would leave a sliver of a panel.</summary>
    public const double Sliver = 150;

    /// <summary>How far under a floor a ceiling may hang and still close the band, mm.</summary>
    private const double CeilingVoid = 1500;

    /// <summary>
    /// The bands of a curtain wall that floors run behind, as heights above the wall's base: each
    /// from the floor's underside - or the ceiling's under it - up to its top, within the wall.
    /// A floor at the wall's foot, the wall standing on it, has no band.
    /// </summary>
    public static IReadOnlyList<(double Bottom, double Top)> Bands(BimDocument document, Wall wall)
    {
        if (!document.IsCurtainWall(wall)) return Array.Empty<(double, double)>();

        var foot = wall.GetBaseElevation(document);
        var height = SlabEdges.CurtainHeight(document, wall);
        var ceilings = document.Elements.OfType<Ceiling>().Where(ceiling => SlabEdges.Meets(document, wall, ceiling)).ToList();

        var bands = new List<(double Bottom, double Top)>();
        foreach (var slab in document.Elements.OfType<Slab>())
        {
            // A floor, or a flat roof carrying the storey above; not a ceiling, nor a pitched roof.
            if (slab is Ceiling) continue;
            if (slab is Roof roof && (roof.IsExtrusion || !roof.Surface(document).IsFlat)) continue;
            if (!SlabEdges.Meets(document, wall, slab)) continue;

            var top = slab.GetTopElevation(document) - foot;
            var bottom = slab.GetBottomElevation(document) - foot;

            // Down to the underside of a ceiling hung just under it: the void above is hidden too.
            foreach (var ceiling in ceilings)
            {
                var ceilingTop = ceiling.GetTopElevation(document) - foot;
                if (ceilingTop <= bottom + 1 && bottom - ceilingTop <= CeilingVoid)
                    bottom = Math.Min(bottom, ceiling.GetBottomElevation(document) - foot);
            }

            if (top <= 1 || bottom >= height - 1) continue;
            bands.Add((Math.Max(bottom, 0), Math.Min(top, height)));
        }

        // Bands that touch or overlap are one.
        var merged = new List<(double Bottom, double Top)>();
        foreach (var band in bands.OrderBy(band => band.Bottom))
        {
            if (merged.Count > 0 && band.Bottom <= merged[^1].Top + 1)
                merged[^1] = (merged[^1].Bottom, Math.Max(merged[^1].Top, band.Top));
            else
                merged.Add(band);
        }

        return merged;
    }

    /// <summary>
    /// The change that puts spandrels at the floors behind a curtain wall: a grid line at each
    /// band's top and bottom, none left inside or just beside it, and each panel in the band made
    /// opaque spandrel glass. The panels already set - a door, a solid panel - stay in the place
    /// they were. Null, with why, where no floor runs behind it.
    /// </summary>
    public static IUndoableCommand? Apply(BimDocument document, Wall wall, out string? message)
    {
        message = null;
        if (document.FindType<CurtainWallType>(wall.TypeId) is not { } type || CurtainLayout.Of(document, wall) is not { } before)
        {
            message = "Spandrels go in a curtain wall.";
            return null;
        }

        var bands = Bands(document, wall);
        if (bands.Count == 0)
        {
            message = "No floor runs behind this curtain wall within its height: there is no slab edge to hide.";
            return null;
        }

        var height = SlabEdges.CurtainHeight(document, wall);
        bool InBand(double at) => bands.Any(band => at > band.Bottom + 1e-6 && at < band.Top - 1e-6);

        // A line at each edge of each band; none inside one, nor just beside it.
        var horizontals = (wall.CurtainGrid?.Horizontals ?? CurtainLayout.TypeLines(type, false, height))
            .Where(line => !bands.Any(band => line > band.Bottom - Sliver && line < band.Top + Sliver))
            .ToList();
        foreach (var (bottom, top) in bands)
            foreach (var edge in new[] { bottom, top })
                if (edge > 1 && edge < height - 1) horizontals.Add(edge);

        var verticals = wall.CurtainGrid?.Verticals ?? CurtainLayout.TypeLines(type, true, wall.Length);
        var grid = new CurtainGrid(verticals, horizontals.Distinct().OrderBy(line => line).ToList(), wall.CurtainGrid?.Removed);
        var after = CurtainLayout.Build(type, wall.Length, height, grid, null, wall.CurtainGlass, WallProfile.CurtainTop(document, wall));

        // What was set on a panel stays where it was; a door stays even in a band.
        var panels = new List<CurtainPanelOverride>();
        foreach (var old in wall.CurtainPanels ?? Array.Empty<CurtainPanelOverride>())
        {
            if (before.Cells.FirstOrDefault(cell => cell.Column == old.Column && cell.Row == old.Row) is not { } was) continue;

            var (along, up) = ((was.From + was.To) / 2, (was.Bottom + was.Top) / 2);
            if (after.Cells.FirstOrDefault(cell => along >= cell.From && along <= cell.To && up >= cell.Bottom && up <= cell.Top) is not { } now) continue;
            if (old.Kind != CurtainPanelKind.Door && InBand((now.Bottom + now.Top) / 2)) continue;
            if (panels.Any(panel => panel.Column == now.Column && panel.Row == now.Row)) continue;

            panels.Add(old with { Column = now.Column, Row = now.Row });
        }

        var spandrels = 0;
        foreach (var cell in after.Cells.Where(cell => InBand((cell.Bottom + cell.Top) / 2)))
        {
            if (panels.Any(panel => panel.Column == cell.Column && panel.Row == cell.Row)) continue;

            panels.Add(new CurtainPanelOverride(cell.Column, cell.Row, CurtainPanelKind.Glazed, Glass: CurtainGlass.Spandrel));
            spandrels++;
        }

        message = $"{spandrels} spandrel panel{(spandrels == 1 ? "" : "s")} across {bands.Count} floor{(bands.Count == 1 ? "" : "s")}: the slab edge{(bands.Count == 1 ? "" : "s")} hidden behind them, clear glass from floor to ceiling.";
        return new SetCurtainLayoutCommand(wall, grid, panels, "Spandrels at Floors");
    }

    /// <summary>
    /// The storey a flat roof sits under when it is really a floor: a roof stands on its line, so
    /// one at a level with walls standing on it rises up into that storey. The level's name, or
    /// null where the roof is not between storeys.
    /// </summary>
    public static string? FloorPosingAsRoof(BimDocument document, Roof roof)
    {
        if (roof.IsExtrusion || !roof.Surface(document).IsFlat) return null;

        var bottom = roof.GetBottomElevation(document);
        var level = document.Levels.FirstOrDefault(candidate => Math.Abs(candidate.Elevation - bottom) < 50);
        if (level is null) return null;

        var storey = document.Walls.Any(wall => wall.LevelId == level.Id && Math.Abs(wall.BaseOffset) < 50);
        return storey ? level.Name : null;
    }
}
