using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Where a door goes when one is put into a curtain wall, and the grid that has to be there for
/// it. A curtain wall door is a panel, so it is as big as the panel it replaces: click a bay
/// that is already door-sized and the door simply takes it, as a double door takes a 1.5 m bay.
/// A bay too wide or too tall to be a door - a whole wall with no grid in it is one panel -
/// gets the grid lines a door needs first, so the door ends up its own size with glass around
/// it and a transom light over it.
/// </summary>
public sealed record CurtainDoorPlacement(
    CurtainGrid Grid, IReadOnlyList<CurtainPanelOverride> Panels, CurtainCell Cell, bool AddedLines);

public static class CurtainDoors
{
    /// <summary>A grid line this close to where one is wanted is used as it is.</summary>
    public const double Tolerance = 10;

    /// <summary>The narrowest panel a new grid line may leave beside it; a sliver becomes the edge instead.</summary>
    public const double MinimumPanel = 150;

    /// <summary>Wider than this, a panel is a shopfront window rather than a doorway.</summary>
    public const double MaximumWidth = 2400;

    /// <summary>A panel between these two heights can hold a door as it stands.</summary>
    public const double MinimumHeight = 1900;

    public const double MaximumHeight = 2700;

    /// <summary>
    /// The door types a curtain wall will take: the curtain wall doors, which are glass doors
    /// made to be a panel. As in Revit, an ordinary door cannot replace a panel; if a project
    /// has no curtain wall door at all, every door type is offered rather than none.
    /// </summary>
    public static IReadOnlyList<DoorType> TypesFor(BimDocument document)
    {
        var curtain = document.TypesOf<DoorType>().Where(type => type.CurtainPanel).OrderBy(type => type.Width).ToList();
        return curtain.Count > 0 ? curtain : document.TypesOf<DoorType>().OrderBy(type => type.Width).ToList();
    }

    /// <summary>The curtain wall door to use when the tool holds this type: itself, or the nearest one that is.</summary>
    public static DoorType? TypeFor(BimDocument document, Guid preferred)
    {
        if (document.FindType<DoorType>(preferred) is { CurtainPanel: true } held) return held;

        var choices = TypesFor(document);
        if (choices.Count == 0) return null;

        // The one closest in width to the door asked for, so a pair stays a pair.
        var wanted = document.FindType<DoorType>(preferred)?.Width ?? 1000;
        return choices.OrderBy(type => Math.Abs(type.Width - wanted)).First();
    }

    /// <summary>
    /// The grid and panels the wall takes when a door of this type is put in at this distance
    /// along it, or null if it cannot go there.
    /// </summary>
    public static CurtainDoorPlacement? Place(BimDocument document, Wall wall, DoorType type, double along)
    {
        if (CurtainLayout.Of(document, wall) is not { } layout) return null;

        var length = layout.Length;
        var height = layout.Height;
        var mullion = Math.Max(0, layout.Type.MullionWidth);

        if (layout.Cells.FirstOrDefault(c => c.Row == 0 && along >= c.From && along <= c.To) is not { } clicked) return null;

        var verticals = layout.Verticals.ToList();
        var horizontals = layout.Horizontals.ToList();
        var added = false;

        // Jambs: the bay stays as it is when it is already a doorway, and otherwise a bay the
        // width of the door type is cut where the click landed.
        var clearWidth = clicked.ClearTo - clicked.ClearFrom;
        var centre = Math.Clamp(along, clicked.From, clicked.To);

        if (clearWidth > MaximumWidth || clearWidth < type.Width)
        {
            var wanted = Math.Min(type.Width, length - mullion - 2 * MinimumPanel);
            if (wanted < 500) return null;

            centre = Math.Clamp(centre, wanted / 2 + mullion / 2, length - wanted / 2 - mullion / 2);
            added |= Insert(verticals, centre - wanted / 2 - mullion / 2, length);
            added |= Insert(verticals, centre + wanted / 2 + mullion / 2, length);
        }

        // Head: a transom at the door's head height, unless the bay is already a doorway. Any
        // line that would cut the door off below its head gives way to it.
        var clearHeight = clicked.ClearTop - clicked.ClearBottom;

        if (clearHeight < MinimumHeight || clearHeight > MaximumHeight)
        {
            var head = Math.Min(type.Height + mullion / 2, height);
            var kept = horizontals.Where(h => h <= 0 || h >= head - Tolerance).ToList();
            added |= kept.Count != horizontals.Count;
            horizontals = kept;
            if (head < height - MinimumPanel) added |= Insert(horizontals, head, height);
        }

        // The panels as they were, each kept with the cell its middle now falls in, so choices
        // follow their bay through the lines coming and going.
        var panels = new Dictionary<(int Column, int Row), CurtainPanelOverride>();
        foreach (var cell in layout.Cells.Where(c => c.Kind != CurtainPanelKind.Glazed || c.Glass != CurtainGlass.Clear))
        {
            var was = CellAt(verticals, (cell.From + cell.To) / 2);
            var storey = CellAt(horizontals, (cell.Bottom + cell.Top) / 2);
            if (was < 0 || storey < 0 || (cell.Kind == CurtainPanelKind.Door && storey != 0)) continue;

            panels[(was, storey)] = new CurtainPanelOverride(was, storey, cell.Kind, cell.OpeningTypeId, cell.Glass, cell.FlipHand, cell.FlipFacing);
        }

        var column = CellAt(verticals, centre);
        if (column < 0) return null;

        panels[(column, 0)] = new CurtainPanelOverride(column, 0, CurtainPanelKind.Door, type.Id);

        var grid = new CurtainGrid(Inner(verticals, length), Inner(horizontals, height), wall.CurtainGrid?.Removed);
        var result = panels.Values.OrderBy(p => p.Column).ThenBy(p => p.Row).ToList();

        if (CurtainLayout.Build(layout.Type, length, height, grid, result, wall.CurtainGlass, layout.TopLine)
                .Cells.FirstOrDefault(c => c.Column == column && c.Row == 0) is not { } filled)
            return null;

        // Hung to open to a side it has room to: not up into the soffit under the eaves.
        if (DoorSwing.Settle(flip => DoorSwing.Hits(document, wall, filled, flip), false, out _))
        {
            filled = filled with { FlipFacing = true };
            result = result.Select(p => p.Column == column && p.Row == 0 ? p with { FlipFacing = true } : p).ToList();
        }

        return new CurtainDoorPlacement(grid, result, filled, added);
    }

    /// <summary>
    /// Puts a line in among the others, unless one is already there or it would leave a sliver
    /// against the wall's end - then the end itself is the side of the bay. Says whether the
    /// lines changed.
    /// </summary>
    private static bool Insert(List<double> lines, double at, double extent)
    {
        if (at <= MinimumPanel || at >= extent - MinimumPanel) return false;
        if (lines.Any(x => Math.Abs(x - at) <= Tolerance)) return false;

        lines.Add(at);
        lines.Sort();
        return true;
    }

    /// <summary>Which bay a point falls in, counting from 0, or -1 if it is outside them all.</summary>
    private static int CellAt(IReadOnlyList<double> lines, double at)
    {
        for (var i = 0; i + 1 < lines.Count; i++)
            if (at >= lines[i] && at <= lines[i + 1]) return i;

        return -1;
    }

    /// <summary>The lines a wall keeps as its own grid: the inner ones, without its two edges.</summary>
    private static List<double> Inner(IEnumerable<double> lines, double extent) =>
        lines.Where(x => x > Tolerance && x < extent - Tolerance).ToList();
}
