using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A curtain wall's own grid, set on the wall rather than following its type: vertical lines
/// as distances along the wall, horizontal lines as heights above its base.
/// </summary>
public sealed record CurtainGrid(IReadOnlyList<double> Verticals, IReadOnlyList<double> Horizontals);

/// <summary>
/// One cell of a curtain wall filled with something other than glass. A door panel says which
/// door type it is, so a curtain wall door is a door of the project's own types - a glazed pair,
/// a flush single - rather than one fixed design.
/// </summary>
public readonly record struct CurtainPanelOverride(
    int Column, int Row, CurtainPanelKind Kind, Guid? OpeningTypeId = null, CurtainGlass Glass = CurtainGlass.Clear,
    bool FlipHand = false, bool FlipFacing = false, bool IsOpen = false);

/// <summary>
/// One cell of the grid: the lines round it, and the clear opening inside the mullions that
/// the panel fills. Along the wall and height above its base.
/// </summary>
public sealed record CurtainCell(
    int Column, int Row, double From, double To, double Bottom, double Top,
    double ClearFrom, double ClearTo, double ClearBottom, double ClearTop, CurtainPanelKind Kind,
    Guid? OpeningTypeId = null, CurtainGlass Glass = CurtainGlass.Clear,
    bool FlipHand = false, bool FlipFacing = false, bool IsOpen = false);

/// <summary>
/// One straight piece of mullion, as the box it fills in the wall's elevation: along the wall
/// and height above its base. Vertical mullions run the full height; horizontal ones run
/// between them.
/// </summary>
public sealed record CurtainMullion(bool IsVertical, bool IsBorder, double From, double To, double Bottom, double Top);

/// <summary>
/// Where everything in a curtain wall goes (specification section 3.1, "curtain walls"): its
/// grid lines, the panel in each cell, and the mullions along the lines. Worked out from the
/// wall and its type whenever it is asked for, so it follows the wall as it is stretched.
/// </summary>
public sealed class CurtainLayout
{
    /// <summary>Grid lines closer than this to each other or to an edge are dropped.</summary>
    private const double MinimumPanel = 1;

    private CurtainLayout(CurtainWallType type, double length, double height,
        IReadOnlyList<double> verticals, IReadOnlyList<double> horizontals,
        IReadOnlyList<CurtainCell> cells, IReadOnlyList<CurtainMullion> mullions)
    {
        Type = type;
        Length = length;
        Height = height;
        Verticals = verticals;
        Horizontals = horizontals;
        Cells = cells;
        Mullions = mullions;
    }

    public CurtainWallType Type { get; }

    public double Length { get; }

    public double Height { get; }

    /// <summary>Vertical grid lines along the wall, both ends included.</summary>
    public IReadOnlyList<double> Verticals { get; }

    /// <summary>Horizontal grid lines above the base, bottom and top included.</summary>
    public IReadOnlyList<double> Horizontals { get; }

    public IReadOnlyList<CurtainCell> Cells { get; }

    public IReadOnlyList<CurtainMullion> Mullions { get; }

    /// <summary>How many cells hold a panel of some kind: every one that is not left empty.</summary>
    public int PanelCount => Cells.Count(c => c.Kind != CurtainPanelKind.Empty);

    /// <summary>The length of all the mullions put end to end.</summary>
    public double MullionLength => Mullions.Sum(m => m.IsVertical ? m.Top - m.Bottom : m.To - m.From);

    /// <summary>What the wall is made of, by volume: its panels and its mullions.</summary>
    public double Volume
    {
        get
        {
            var panels = Cells
                .Where(c => c.Kind is CurtainPanelKind.Glazed or CurtainPanelKind.Solid or CurtainPanelKind.Door)
                .Sum(c => Math.Max(0, c.ClearTo - c.ClearFrom) * Math.Max(0, c.ClearTop - c.ClearBottom)) * Type.PanelThickness;

            var section = Type.MullionProfile == MullionProfile.Circular
                ? Math.PI * Type.MullionWidth * Type.MullionWidth / 4
                : Type.MullionWidth * Type.MullionDepth;

            return panels + MullionLength * section;
        }
    }

    public int Columns => Verticals.Count - 1;

    public int Rows => Horizontals.Count - 1;

    /// <summary>The layout of a curtain wall, or null for any other wall.</summary>
    public static CurtainLayout? Of(BimDocument document, Wall wall) =>
        document.FindType<CurtainWallType>(wall.TypeId) is { } type
            ? Build(type, wall.Length, wall.GetHeight(document), wall.CurtainGrid, wall.CurtainPanels, wall.CurtainGlass)
            : null;

    /// <summary>The grid lines a type sets out along a wall of this length, or up one of this height.</summary>
    public static IReadOnlyList<double> TypeLines(CurtainWallType type, bool vertical, double extent) => vertical
        ? Lines(type.VerticalLayout, type.VerticalSpacing, type.VerticalCount, type.VerticalJustification, extent)
        : Lines(type.HorizontalLayout, type.HorizontalSpacing, type.HorizontalCount, type.HorizontalJustification, extent);

    public static CurtainLayout Build(
        CurtainWallType type, double length, double height, CurtainGrid? grid,
        IReadOnlyList<CurtainPanelOverride>? panels, CurtainGlass glazing = CurtainGlass.Clear)
    {
        var verticals = Tidy(grid?.Verticals ?? TypeLines(type, true, length), length);
        var horizontals = Tidy(grid?.Horizontals ?? TypeLines(type, false, height), height);

        var overrides = (panels ?? Array.Empty<CurtainPanelOverride>())
            .GroupBy(p => (p.Column, p.Row))
            .ToDictionary(g => g.Key, g => g.Last());
        var kinds = overrides.ToDictionary(e => e.Key, e => e.Value.Kind);

        var w = type.MullionWidth;
        var border = type.BorderMullions && w > 0;

        // The faces of the mullion on each line: an interior one is centred on its line, a
        // border one sits inside the edge.
        (double Low, double High) Faces(IReadOnlyList<double> lines, int i)
        {
            if (w <= 0) return (lines[i], lines[i]);
            if (i == 0) return border ? (lines[0], lines[0] + w) : (lines[0], lines[0]);
            if (i == lines.Count - 1) return border ? (lines[i] - w, lines[i]) : (lines[i], lines[i]);
            return (lines[i] - w / 2, lines[i] + w / 2);
        }

        var cells = new List<CurtainCell>();
        for (var c = 0; c + 1 < verticals.Count; c++)
        for (var r = 0; r + 1 < horizontals.Count; r++)
        {
            var kind = kinds.GetValueOrDefault((c, r), CurtainPanelKind.Glazed);

            // A door stands on the floor: nothing across the bottom of it. A window does not - it
            // sits in its panel like any other pane.

            var clearBottom = kind == CurtainPanelKind.Door && r == 0 ? horizontals[0] : Faces(horizontals, r).High;

            cells.Add(new CurtainCell(c, r, verticals[c], verticals[c + 1], horizontals[r], horizontals[r + 1],
                Faces(verticals, c).High, Faces(verticals, c + 1).Low, clearBottom, Faces(horizontals, r + 1).Low, kind,
                overrides.TryGetValue((c, r), out var panel) ? panel.OpeningTypeId : null,
                overrides.TryGetValue((c, r), out var pane) ? pane.Glass : glazing,
                overrides.TryGetValue((c, r), out var hung) && hung.FlipHand,
                overrides.TryGetValue((c, r), out var faced) && faced.FlipFacing,
                overrides.TryGetValue((c, r), out var swung) && swung.IsOpen));
        }

        var mullions = new List<CurtainMullion>();
        if (w > 0)
        {
            for (var i = 0; i < verticals.Count; i++)
            {
                var isBorder = i == 0 || i == verticals.Count - 1;
                if (isBorder && !border) continue;

                var (low, high) = Faces(verticals, i);
                mullions.Add(new CurtainMullion(true, isBorder, low, high, horizontals[0], horizontals[^1]));
            }

            for (var j = 0; j < horizontals.Count; j++)
            {
                var isBorder = j == 0 || j == horizontals.Count - 1;
                if (isBorder && !border) continue;

                var (bottom, top) = Faces(horizontals, j);
                for (var c = 0; c + 1 < verticals.Count; c++)
                {
                    // No sill across a door.
                    if (j == 0 && kinds.GetValueOrDefault((c, 0)) == CurtainPanelKind.Door) continue;

                    var from = Faces(verticals, c).High;
                    var to = Faces(verticals, c + 1).Low;
                    if (to - from > MinimumPanel) mullions.Add(new CurtainMullion(false, isBorder, from, to, bottom, top));
                }
            }
        }

        return new CurtainLayout(type, length, height, verticals, horizontals, cells, mullions);
    }

    /// <summary>Interior lines for a layout rule over an extent.</summary>
    private static IReadOnlyList<double> Lines(
        CurtainGridLayout layout, double spacing, int count, CurtainGridJustification justification, double extent)
    {
        if (extent <= MinimumPanel) return Array.Empty<double>();

        switch (layout)
        {
            case CurtainGridLayout.FixedNumber:
                return Equal(Math.Clamp(count, 1, 200));

            case CurtainGridLayout.MaximumSpacing when spacing > 0:
                return Equal(Math.Clamp((int)Math.Ceiling(extent / spacing - 1e-9), 1, 200));

            case CurtainGridLayout.FixedDistance when spacing > 0:
            {
                var steps = Math.Min((int)Math.Floor(extent / spacing + 1e-9), 200);
                var start = justification switch
                {
                    CurtainGridJustification.Beginning => 0,
                    CurtainGridJustification.End => extent - steps * spacing,
                    _ => (extent - steps * spacing) / 2
                };

                return Enumerable.Range(0, steps + 1).Select(k => start + k * spacing).ToList();
            }

            default:
                return Array.Empty<double>();
        }

        IReadOnlyList<double> Equal(int panels) =>
            Enumerable.Range(1, panels - 1).Select(k => extent * k / panels).ToList();
    }

    /// <summary>The lines in order, within the extent and not crowding each other, with both edges added.</summary>
    private static IReadOnlyList<double> Tidy(IEnumerable<double> lines, double extent)
    {
        var result = new List<double> { 0 };
        foreach (var line in lines.Where(double.IsFinite).OrderBy(x => x))
        {
            if (line - result[^1] < MinimumPanel || extent - line < MinimumPanel) continue;
            result.Add(line);
        }

        result.Add(Math.Max(extent, 0));
        return result;
    }
}
