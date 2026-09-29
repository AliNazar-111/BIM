using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A curtain wall's own grid, set on the wall rather than following its type: vertical lines
/// as distances along the wall, horizontal lines as heights above its base - and the stretches
/// taken out of them, Revit's Add/Remove Segments, which make the bays either side one panel.
/// </summary>
public sealed record CurtainGrid(
    IReadOnlyList<double> Verticals, IReadOnlyList<double> Horizontals, IReadOnlyList<CurtainSegment>? Removed = null)
{
    /// <summary>The same grid with the stretches taken out of it given.</summary>
    public CurtainGrid WithRemoved(IEnumerable<CurtainSegment> removed)
    {
        var kept = removed.ToList();
        return this with { Removed = kept.Count == 0 ? null : kept };
    }
}

/// <summary>
/// A stretch taken out of a grid line: the line - vertical, at a distance along the wall, or
/// horizontal, at a height - and from where to where along it, between the lines crossing it.
/// Kept by where it is rather than by counting lines, so it stays with its line as others are
/// added, taken away or moved.
/// </summary>
public readonly record struct CurtainSegment(bool Vertical, double Line, double From, double To)
{
    /// <summary>Whether this is the stretch of a line at this place that a point along it falls in.</summary>
    public bool Covers(bool vertical, double line, double at) =>
        Vertical == vertical && Math.Abs(Line - line) < 0.5 && at >= Math.Min(From, To) - 1e-6 && at <= Math.Max(From, To) + 1e-6;
}

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
/// <remarks>
/// With a stretch of grid line taken out between them, bays are one panel: it is known by its
/// first column and row, and says how many of each it spans.
/// </remarks>
public sealed record CurtainCell(
    int Column, int Row, double From, double To, double Bottom, double Top,
    double ClearFrom, double ClearTo, double ClearBottom, double ClearTop, CurtainPanelKind Kind,
    Guid? OpeningTypeId = null, CurtainGlass Glass = CurtainGlass.Clear,
    bool FlipHand = false, bool FlipFacing = false, bool IsOpen = false, int Columns = 1, int Rows = 1);

/// <summary>
/// One straight piece of mullion, as the box it fills in the wall's elevation: along the wall
/// and height above its base. Vertical mullions run the full height; horizontal ones run
/// between them. Under a sloping top a mullion's bottom and top need not be level: they are
/// given at its start, and <paramref name="BottomAtTo"/> and <paramref name="TopAtTo"/> at its
/// end - a vertical one cut off under the rake, the rake itself.
/// </summary>
public sealed record CurtainMullion(
    bool IsVertical, bool IsBorder, double From, double To, double Bottom, double Top,
    double? BottomAtTo = null, double? TopAtTo = null)
{
    /// <summary>Whether its bottom or top slopes.</summary>
    public bool IsSloped => BottomAtTo is not null || TopAtTo is not null;

    /// <summary>Its bottom this far along the wall.</summary>
    public double BottomAt(double along) => Lerp(Bottom, BottomAtTo ?? Bottom, along);

    /// <summary>Its top this far along the wall.</summary>
    public double TopAt(double along) => Lerp(Top, TopAtTo ?? Top, along);

    private double Lerp(double atFrom, double atTo, double along) =>
        To - From <= 1e-9 ? Math.Min(atFrom, atTo) : atFrom + (atTo - atFrom) * Math.Clamp((along - From) / (To - From), 0, 1);
}

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
        IReadOnlyList<CurtainCell> cells, IReadOnlyList<CurtainMullion> mullions, IReadOnlyList<Point2D>? topLine)
    {
        Type = type;
        Length = length;
        Height = height;
        Verticals = verticals;
        Horizontals = horizontals;
        Cells = cells;
        Mullions = mullions;
        TopLine = topLine;
    }

    /// <summary>
    /// The wall's top where it is not level - under a pitched roof it is attached to, the roof's
    /// underside all along it - as points along the wall and heights above its base. Null where
    /// the top is level at <see cref="Height"/>, which is then the most it reaches.
    /// </summary>
    public IReadOnlyList<Point2D>? TopLine { get; }

    /// <summary>How far below the top the panes and mullions stop: the depth of the frame along it.</summary>
    public double TopInset => TopLine is null ? 0 : Inset(Type);

    /// <summary>How high the wall goes this far along it.</summary>
    public double TopAt(double along) => TopOn(TopLine, Height, along);

    /// <summary>
    /// What is left of a box of the wall's elevation - from one distance along it to another,
    /// between two heights - under the wall's top, less its frame there: pieces each with a
    /// level bottom and a straight top, given at either end. The box itself where the top is
    /// level; nothing where the top comes down below it.
    /// </summary>
    public IReadOnlyList<(double From, double To, double Bottom, double TopFrom, double TopTo)> Under(
        double from, double to, double bottom, double top) =>
        Clip(TopLine, Height, TopInset, from, to, bottom, top);

    /// <summary>The panes a cell's clear opening comes to under the wall's top.</summary>
    public IReadOnlyList<(double From, double To, double Bottom, double TopFrom, double TopTo)> ClearPieces(CurtainCell cell) =>
        Under(cell.ClearFrom, cell.ClearTo, cell.ClearBottom, cell.ClearTop);

    /// <summary>The frame along a sloping top: a border mullion's width, or nothing without one.</summary>
    private static double Inset(CurtainWallType type) => type.BorderMullions && type.MullionWidth > 0 ? type.MullionWidth : 0;

    private static double TopOn(IReadOnlyList<Point2D>? line, double height, double along)
    {
        if (line is not { Count: > 0 }) return height;
        if (along <= line[0].X) return line[0].Y;
        if (along >= line[^1].X) return line[^1].Y;

        // Where the top steps, the lower of the two: nothing is to stand up past either.
        double? found = null;
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (line[i], line[i + 1]);
            if (along < a.X - 1e-9 || along > b.X + 1e-9) continue;

            var at = b.X - a.X <= 1e-9 ? Math.Min(a.Y, b.Y) : a.Y + (b.Y - a.Y) * (along - a.X) / (b.X - a.X);
            found = found is { } lower ? Math.Min(lower, at) : at;
        }

        return found ?? height;
    }

    private static IReadOnlyList<(double From, double To, double Bottom, double TopFrom, double TopTo)> Clip(
        IReadOnlyList<Point2D>? line, double height, double inset, double from, double to, double bottom, double top)
    {
        var pieces = new List<(double, double, double, double, double)>();
        if (to - from <= 1e-6 || top - bottom <= 1e-6) return pieces;

        if (line is null)
        {
            pieces.Add((from, to, bottom, top, top));
            return pieces;
        }

        double Ceiling(double along) => Math.Min(top, TopOn(line, height, along) - inset);

        // Straight between the corners of the top and the points where it crosses the box's top
        // and bottom: the pieces run between those.
        var stations = new List<double> { from, to };
        stations.AddRange(line.Select(point => point.X).Where(x => x > from && x < to));
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (line[i], line[i + 1]);
            if (b.X - a.X <= 1e-9) continue;

            foreach (var level in new[] { top + inset, bottom + inset })
            {
                if ((a.Y - level) * (b.Y - level) >= 0) continue;

                var x = a.X + (level - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                if (x > from && x < to) stations.Add(x);
            }
        }

        var sorted = stations.OrderBy(x => x).ToList();
        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            var (x0, x1) = (sorted[i], sorted[i + 1]);
            if (x1 - x0 <= 1e-6) continue;

            // Just inside each end, so a step in the top at a station counts on its own side.
            var nudge = Math.Min(1e-4, (x1 - x0) / 4);
            var (t0, t1) = (Ceiling(x0 + nudge), Ceiling(x1 - nudge));
            if (t0 <= bottom + 1e-6 && t1 <= bottom + 1e-6) continue;

            // A piece running on from the last at the box's full height is the same piece: a
            // transom well under the rake is not cut in two at a corner of it.
            var (topFrom, topTo) = (Math.Max(t0, bottom), Math.Max(t1, bottom));
            if (pieces.Count > 0 && pieces[^1] is var last && Math.Abs(last.Item2 - x0) <= 1e-6 &&
                Math.Abs(last.Item4 - top) <= 1e-6 && Math.Abs(last.Item5 - top) <= 1e-6 &&
                Math.Abs(topFrom - top) <= 1e-6 && Math.Abs(topTo - top) <= 1e-6)
            {
                pieces[^1] = (last.Item1, x1, bottom, top, top);
                continue;
            }

            pieces.Add((x0, x1, bottom, topFrom, topTo));
        }

        return pieces;
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

    /// <summary>The length of all the mullions put end to end - up a rake as far as it goes.</summary>
    public double MullionLength => Mullions.Sum(m => m.IsVertical
        ? (m.Top + m.TopAt(m.To)) / 2 - m.Bottom
        : Math.Sqrt((m.To - m.From) * (m.To - m.From) + (m.TopAt(m.To) - m.Top) * (m.TopAt(m.To) - m.Top)));

    /// <summary>What the wall is made of, by volume: its panels and its mullions.</summary>
    public double Volume
    {
        get
        {
            var panels = Cells
                .Where(c => c.Kind is CurtainPanelKind.Glazed or CurtainPanelKind.Solid or CurtainPanelKind.Door)
                .SelectMany(ClearPieces)
                .Sum(piece => (piece.To - piece.From) * ((piece.TopFrom + piece.TopTo) / 2 - piece.Bottom)) * Type.PanelThickness;

            var section = Type.MullionProfile == MullionProfile.Circular
                ? Math.PI * Type.MullionWidth * Type.MullionWidth / 4
                : Type.MullionWidth * Type.MullionDepth;

            return panels + MullionLength * section;
        }
    }

    public int Columns => Verticals.Count - 1;

    public int Rows => Horizontals.Count - 1;

    /// <summary>
    /// The layout of a curtain wall, or null for any other wall. Attached to a pitched roof, it
    /// is set out to the roof's underside at its highest and cut off along it.
    /// </summary>
    public static CurtainLayout? Of(BimDocument document, Wall wall)
    {
        if (document.FindType<CurtainWallType>(wall.TypeId) is not { } type) return null;

        var top = WallProfile.CurtainTop(document, wall);
        var height = top is null ? wall.GetHeight(document) : top.Max(point => point.Y);
        return Build(type, wall.Length, height, wall.CurtainGrid, wall.CurtainPanels, wall.CurtainGlass, top);
    }

    /// <summary>The grid lines a type sets out along a wall of this length, or up one of this height.</summary>
    public static IReadOnlyList<double> TypeLines(CurtainWallType type, bool vertical, double extent) => vertical
        ? Lines(type.VerticalLayout, type.VerticalSpacing, type.VerticalCount, type.VerticalJustification, extent)
        : Lines(type.HorizontalLayout, type.HorizontalSpacing, type.HorizontalCount, type.HorizontalJustification, extent);

    public static CurtainLayout Build(
        CurtainWallType type, double length, double height, CurtainGrid? grid,
        IReadOnlyList<CurtainPanelOverride>? panels, CurtainGlass glazing = CurtainGlass.Clear,
        IReadOnlyList<Point2D>? topLine = null)
    {
        var inset = topLine is null ? 0 : Inset(type);
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

        // The stretches of line taken out: a vertical line in a row, a horizontal one in a column.
        var columns = verticals.Count - 1;
        var rows = horizontals.Count - 1;
        var removed = grid?.Removed ?? Array.Empty<CurtainSegment>();
        bool VerticalGone(int i, int r) =>
            i > 0 && i < columns && r >= 0 && r < rows &&
            removed.Any(segment => segment.Covers(true, verticals[i], (horizontals[r] + horizontals[r + 1]) / 2));
        bool HorizontalGone(int j, int c) =>
            j > 0 && j < rows && c >= 0 && c < columns &&
            removed.Any(segment => segment.Covers(false, horizontals[j], (verticals[c] + verticals[c + 1]) / 2));

        // Bays with the line between them taken out are one panel - so long as it is a rectangle,
        // as a panel has to be; where they would not make one, they stay as they were.
        var group = new int[Math.Max(columns * rows, 0)];
        for (var k = 0; k < group.Length; k++) group[k] = k;
        int Find(int k) => group[k] == k ? k : group[k] = Find(group[k]);
        void Join(int a, int b) => group[Find(a)] = Find(b);

        for (var c = 0; c < columns; c++)
        for (var r = 0; r < rows; r++)
        {
            if (c > 0 && VerticalGone(c, r)) Join((c - 1) * rows + r, c * rows + r);
            if (r > 0 && HorizontalGone(r, c)) Join(c * rows + r - 1, c * rows + r);
        }

        var panelsOf = Enumerable.Range(0, group.Length)
            .GroupBy(Find)
            .SelectMany(members =>
            {
                var bays = members.Select(k => (Column: k / rows, Row: k % rows)).ToList();
                var (c0, c1, r0, r1) = (bays.Min(b => b.Column), bays.Max(b => b.Column), bays.Min(b => b.Row), bays.Max(b => b.Row));
                return bays.Count == (c1 - c0 + 1) * (r1 - r0 + 1)
                    ? new[] { (c0, c1, r0, r1) }
                    : bays.Select(b => (b.Column, b.Column, b.Row, b.Row)).ToArray();
            })
            .OrderBy(bounds => bounds.Item1).ThenBy(bounds => bounds.Item3)
            .ToList();

        // Which panel each bay is part of, by the panel's first column and row.
        var owner = new Dictionary<(int, int), (int Column, int Row)>();
        foreach (var (c0, c1, r0, r1) in panelsOf)
        for (var c = c0; c <= c1; c++)
        for (var r = r0; r <= r1; r++)
            owner[(c, r)] = (c0, r0);
        CurtainPanelKind KindAt(int c, int r) => kinds.GetValueOrDefault(owner.GetValueOrDefault((c, r), (c, r)), CurtainPanelKind.Glazed);

        var cells = new List<CurtainCell>();
        foreach (var (c0, c1, r0, r1) in panelsOf)
        {
            var kind = kinds.GetValueOrDefault((c0, r0), CurtainPanelKind.Glazed);

            // A door stands on the floor: nothing across the bottom of it. A window does not - it
            // sits in its panel like any other pane.

            var clearBottom = kind == CurtainPanelKind.Door && r0 == 0 ? horizontals[0] : Faces(horizontals, r0).High;

            // Wholly above a sloping top, there is no cell: nothing to fill, nothing to pick.
            if (topLine is not null &&
                Clip(topLine, height, inset, Faces(verticals, c0).High, Faces(verticals, c1 + 1).Low, clearBottom, Faces(horizontals, r1 + 1).Low).Count == 0)
                continue;

            cells.Add(new CurtainCell(c0, r0, verticals[c0], verticals[c1 + 1], horizontals[r0], horizontals[r1 + 1],
                Faces(verticals, c0).High, Faces(verticals, c1 + 1).Low, clearBottom, Faces(horizontals, r1 + 1).Low, kind,
                overrides.TryGetValue((c0, r0), out var panel) ? panel.OpeningTypeId : null,
                overrides.TryGetValue((c0, r0), out var pane) ? pane.Glass : glazing,
                overrides.TryGetValue((c0, r0), out var hung) && hung.FlipHand,
                overrides.TryGetValue((c0, r0), out var faced) && faced.FlipFacing,
                overrides.TryGetValue((c0, r0), out var swung) && swung.IsOpen,
                c1 - c0 + 1, r1 - r0 + 1));
        }

        // Whether a vertical line's mullion is taken out in a row: inside one panel.
        bool Inside(int i, int r) => i > 0 && i < columns && r >= 0 && r < rows && owner[(i - 1, r)] == owner[(i, r)];
        bool Across(int j, int c) => j > 0 && j < rows && c >= 0 && c < columns && owner[(c, j - 1)] == owner[(c, j)];

        var mullions = new List<CurtainMullion>();
        if (w > 0)
        {
            for (var i = 0; i < verticals.Count; i++)
            {
                var isBorder = i == 0 || i == verticals.Count - 1;
                if (isBorder && !border) continue;

                var (low, high) = Faces(verticals, i);

                // Up the rows it runs through, stopping where a stretch of it is taken out.
                for (var r = 0; r < rows;)
                {
                    if (Inside(i, r))
                    {
                        r++;
                        continue;
                    }

                    var first = r;
                    while (r < rows && !Inside(i, r)) r++;
                    var (runBottom, runTop) = (horizontals[first], horizontals[r]);

                    if (topLine is null)
                    {
                        mullions.Add(new CurtainMullion(true, isBorder, low, high, runBottom, runTop));
                        continue;
                    }

                    // Up to the rake, and stopped there.
                    var (topFrom, topTo) = (Math.Min(runTop, TopOn(topLine, height, low) - inset), Math.Min(runTop, TopOn(topLine, height, high) - inset));
                    if (Math.Max(topFrom, topTo) > runBottom + MinimumPanel)
                        mullions.Add(new CurtainMullion(true, isBorder, low, high, runBottom, Math.Max(topFrom, runBottom),
                            TopAtTo: Math.Max(topTo, runBottom)));
                }
            }

            // A sloping top has its frame along it: a mullion up each stretch of the rake.
            if (topLine is not null && border)
                for (var i = 0; i + 1 < topLine.Count; i++)
                {
                    var (a, b) = (topLine[i], topLine[i + 1]);
                    if (b.X - a.X > MinimumPanel)
                        mullions.Add(new CurtainMullion(false, true, a.X, b.X, a.Y - w, a.Y, b.Y - w, b.Y));
                }

            for (var j = 0; j < horizontals.Count; j++)
            {
                var isBorder = j == 0 || j == horizontals.Count - 1;
                if (isBorder && !border) continue;

                // Under a sloping top, the top of the grid is only its highest point: the rake is
                // the frame there.
                if (topLine is not null && j == horizontals.Count - 1) continue;

                var (bottom, top) = Faces(horizontals, j);

                // Along the columns it crosses, stopping where a stretch of it is taken out, and
                // running on unbroken past a vertical line that has no mullion there to stop at.
                bool Stops(int c) => Across(j, c) || (j == 0 && KindAt(c, 0) == CurtainPanelKind.Door);
                bool Upright(int i) => (j > 0 && !Inside(i, j - 1)) || (j < rows && !Inside(i, j));

                for (var c = 0; c < columns;)
                {
                    // No sill across a door.
                    if (Stops(c))
                    {
                        c++;
                        continue;
                    }

                    var first = c;
                    while (c + 1 < columns && !Stops(c + 1) && !Upright(c + 1)) c++;
                    var from = Faces(verticals, first).High;
                    var to = Faces(verticals, c + 1).Low;
                    c++;
                    if (to - from <= MinimumPanel) continue;

                    // Cut off where it runs into the rake.
                    foreach (var piece in Clip(topLine, height, inset, from, to, bottom, top))
                        if (piece.To - piece.From > MinimumPanel)
                            mullions.Add(Math.Abs(piece.TopFrom - top) < 1e-6 && Math.Abs(piece.TopTo - top) < 1e-6
                                ? new CurtainMullion(false, isBorder, piece.From, piece.To, bottom, top)
                                : new CurtainMullion(false, isBorder, piece.From, piece.To, bottom, piece.TopFrom, TopAtTo: piece.TopTo));
                }
            }
        }

        return new CurtainLayout(type, length, height, verticals, horizontals, cells, mullions, topLine);
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
