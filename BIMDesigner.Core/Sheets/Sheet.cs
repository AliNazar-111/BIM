using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Sheets;

/// <summary>
/// One view placed on a sheet (specification section 6.5).
///
/// It stores a reference to the view, where its centre sits on the paper, and at what scale.
/// It does not store the drawing. Asking the model for the view on every repaint is what
/// stops a drawing set drifting away from the building it documents - the failure that makes
/// hand-redrawn sets expensive and dangerous.
/// </summary>
public sealed class Viewport
{
    /// <summary>
    /// Paper depth, in millimetres, taken by the title and scale printed under a view. Layout
    /// counts it as part of the view, so another drawing is never placed on top of a title.
    /// </summary>
    public const double TitleBand = 14;

    public Guid Id { get; init; } = Guid.NewGuid();

    public ViewReference View { get; set; }

    /// <summary>Centre of the viewport, in millimetres from the sheet's bottom-left corner.</summary>
    public Point2D Centre { get; set; }

    public ViewScale Scale { get; set; } = ViewScale.OneToOneHundred;

    /// <summary>Whether the title and scale are printed under the drawing.</summary>
    public bool ShowTitle { get; set; } = true;

    /// <summary>
    /// A title typed in place of the view's own name. Empty means the view names itself,
    /// which is what keeps a sheet honest when a level or a section is renamed.
    /// </summary>
    public string TitleOverride { get; set; } = string.Empty;

    public string TitleIn(BimDocument document) =>
        string.IsNullOrWhiteSpace(TitleOverride) ? View.TitleIn(document) : TitleOverride;

    /// <summary>
    /// The rectangle this viewport occupies on the paper, in sheet millimetres, worked out
    /// from how big the view's contents are and the scale it is drawn at.
    ///
    /// The size is derived rather than stored, so a wall added outside the building makes the
    /// viewport grow to include it instead of quietly cropping it off the drawing.
    /// </summary>
    public BoundingBox2D PaperBounds(BimDocument document)
    {
        var content = ViewExtent.Of(document, View).OrAtLeast(1000);

        var halfWidth = Scale.ToPaper(content.Width) / 2;
        var halfHeight = Scale.ToPaper(content.Height) / 2;

        return new BoundingBox2D(
            Centre.X - halfWidth, Centre.Y - halfHeight,
            Centre.X + halfWidth, Centre.Y + halfHeight);
    }

    /// <summary>Whether a point on the paper falls inside this viewport.</summary>
    public bool Contains(BimDocument document, Point2D paperPoint)
    {
        var bounds = PaperBounds(document);

        return paperPoint.X >= bounds.MinX && paperPoint.X <= bounds.MaxX &&
               paperPoint.Y >= bounds.MinY && paperPoint.Y <= bounds.MaxY;
    }

    public Viewport Copy() => new()
    {
        Id = Id,
        View = View,
        Centre = Centre,
        Scale = Scale,
        ShowTitle = ShowTitle,
        TitleOverride = TitleOverride
    };
}

/// <summary>
/// A drawing sheet (specification section 6.5): a piece of paper, a title block and the
/// viewports placed on it.
///
/// A sheet is an element so that it gets an id, parameters, undo and persistence like
/// everything else. It carries no level, because a sheet belongs to the drawing set rather
/// than to a storey - a single sheet routinely shows plans of several.
/// </summary>
public sealed class Sheet : Element
{
    private readonly List<Viewport> _viewports = new();

    public override BuiltInCategory Category => BuiltInCategory.Sheets;

    /// <summary>The drawing number, e.g. "A-101". What a sheet is referred to by.</summary>
    public string Number { get; set; } = "A-101";

    public string Name { get; set; } = "Unnamed";

    public PaperSize PaperSize { get; set; } = PaperSize.A1;

    public PaperOrientation Orientation { get; set; } = PaperOrientation.Landscape;

    // --- title block ------------------------------------------------------------

    public string DrawnBy { get; set; } = string.Empty;

    public string CheckedBy { get; set; } = string.Empty;

    public string Revision { get; set; } = "P1";

    /// <summary>Issue date. Null until the sheet is issued.</summary>
    public DateTime? IssuedOn { get; set; }

    // --- geometry ---------------------------------------------------------------

    public double Width => Paper.Width(PaperSize, Orientation);

    public double Height => Paper.Height(PaperSize, Orientation);

    /// <summary>The paper itself, with its origin at the bottom-left corner.</summary>
    public BoundingBox2D Bounds => new(0, 0, Width, Height);

    public IReadOnlyList<Viewport> Viewports => _viewports;

    public void Add(Viewport viewport) => _viewports.Add(viewport);

    public void Insert(int index, Viewport viewport) => _viewports.Insert(index, viewport);

    public bool Remove(Viewport viewport) => _viewports.Remove(viewport);

    public int IndexOf(Viewport viewport) => _viewports.IndexOf(viewport);

    public Viewport? FindViewport(Guid id) => _viewports.FirstOrDefault(viewport => viewport.Id == id);

    // ---- layout -----------------------------------------------------------------

    /// <summary>
    /// The part of the paper drawings may go on: inside the border, and clear of the title
    /// block. A view outside it is either off the sheet or printed over the title block, and
    /// neither is a drawing anyone can issue.
    /// </summary>
    public BoundingBox2D DrawingArea => new(
        TitleBlock.Margin,
        TitleBlock.Margin,
        Width - TitleBlock.Width(this) - TitleBlock.Margin,
        Height - TitleBlock.Margin);

    /// <summary>
    /// Everything a viewport takes up on the paper: the drawing, and the title and scale
    /// printed underneath it. Two views whose drawings clear each other but whose titles
    /// collide are still two views on top of each other.
    /// </summary>
    public static BoundingBox2D Footprint(BimDocument document, Viewport viewport) =>
        FootprintAt(viewport.PaperBounds(document), viewport.ShowTitle);

    private static BoundingBox2D FootprintAt(BoundingBox2D drawing, bool showTitle) =>
        showTitle
            ? new BoundingBox2D(drawing.MinX, drawing.MinY - Viewport.TitleBand, drawing.MaxX, drawing.MaxY)
            : drawing;

    /// <summary>Where a viewport would sit, and what it would take up, with its centre here.</summary>
    private static BoundingBox2D FootprintWithCentre(BimDocument document, Viewport viewport, Point2D centre)
    {
        var drawing = viewport.PaperBounds(document);
        var shift = centre - viewport.Centre;

        return FootprintAt(
            new BoundingBox2D(drawing.MinX + shift.X, drawing.MinY + shift.Y,
                              drawing.MaxX + shift.X, drawing.MaxY + shift.Y),
            viewport.ShowTitle);
    }

    /// <summary>Whether a viewport is on the drawing area and clear of every other view.</summary>
    public bool IsWellPlaced(BimDocument document, Viewport viewport) =>
        Fits(document, viewport, viewport.Centre, OtherFootprints(document, viewport));

    /// <summary>
    /// The views that are not where a drawing can be: off the drawing area, over the title
    /// block, or on top of another view. Usually the result of making the paper smaller or a
    /// scale larger, which moves nothing but changes what fits.
    /// </summary>
    public IReadOnlyList<Viewport> Misplaced(BimDocument document) =>
        _viewports.Where(viewport => !IsWellPlaced(document, viewport)).ToList();

    /// <summary>
    /// Where a viewport being dragged towards <paramref name="desired"/> is allowed to go.
    ///
    /// It is held inside the drawing area, and it cannot be pushed through another view:
    /// against an edge or a neighbour it slides along whichever way is still free, the way a
    /// sheet of card slides along a ruler. A view that is already somewhere it should not be
    /// may move freely inside the drawing area, so it can always be dragged out of trouble.
    /// </summary>
    public Point2D ConstrainMove(BimDocument document, Viewport viewport, Point2D desired)
    {
        var others = OtherFootprints(document, viewport);
        var current = viewport.Centre;
        var target = ClampIntoArea(document, viewport, desired);

        if (!Fits(document, viewport, current, others)) return target;
        if (Fits(document, viewport, target, others)) return target;

        // Blocked. Try each direction on its own, so the view slides along what stopped it.
        var alongX = ClampIntoArea(document, viewport, new Point2D(target.X, current.Y));
        var alongY = ClampIntoArea(document, viewport, new Point2D(current.X, target.Y));

        var xFits = Fits(document, viewport, alongX, others);
        var yFits = Fits(document, viewport, alongY, others);

        return (xFits, yFits) switch
        {
            (true, true) => Distance(alongX, desired) <= Distance(alongY, desired) ? alongX : alongY,
            (true, false) => alongX,
            (false, true) => alongY,
            _ => current
        };
    }

    /// <summary>
    /// Where a viewport should go once its size has changed - after a change of scale, say.
    ///
    /// It stays put if it still fits. Otherwise it is nudged back inside the drawing area, and
    /// if that still leaves it on top of something it is moved to the next free space rather
    /// than left overlapping.
    /// </summary>
    public Point2D PositionAfterResize(BimDocument document, Viewport viewport)
    {
        var others = OtherFootprints(document, viewport);

        if (Fits(document, viewport, viewport.Centre, others)) return viewport.Centre;

        var clamped = ClampIntoArea(document, viewport, viewport.Centre);
        if (Fits(document, viewport, clamped, others)) return clamped;

        var drawing = viewport.PaperBounds(document);
        return NextFreePosition(document, drawing.Width, drawing.Height, viewport.ShowTitle, viewport);
    }

    /// <summary>
    /// A free spot for a viewport of this size, filling the drawing area from the top left,
    /// across and then down - the order a drawing set is read in.
    ///
    /// Dropping every view at the sheet's centre would stack them on top of each other, and a
    /// drawing set where the second view hides the first is not a drawing set.
    /// </summary>
    public Point2D NextFreePosition(
        BimDocument document, double width, double height, bool showTitle = true, Viewport? ignore = null)
    {
        const double step = 5;

        var area = DrawingArea;
        var band = showTitle ? Viewport.TitleBand : 0;

        // Worked out once: a section's extent means cutting the model, and this loop would
        // otherwise do it several thousand times.
        var others = _viewports
            .Where(viewport => !ReferenceEquals(viewport, ignore))
            .Select(viewport => Footprint(document, viewport))
            .ToList();

        for (var top = area.MaxY; top - height - band >= area.MinY - 1e-9; top -= step)
        {
            for (var left = area.MinX; left + width <= area.MaxX + 1e-9; left += step)
            {
                var footprint = new BoundingBox2D(left, top - height - band, left + width, top);
                if (!others.Any(other => Overlap(footprint, other)))
                    return new Point2D(left + width / 2, top - height / 2);
            }
        }

        // Nothing fits: it goes at the top left of the drawing area, where it will be shown as
        // misplaced, rather than being refused. The user can then rescale it or make room.
        return new Point2D(area.MinX + width / 2, area.MaxY - height / 2);
    }

    private List<BoundingBox2D> OtherFootprints(BimDocument document, Viewport viewport) =>
        _viewports
            .Where(other => !ReferenceEquals(other, viewport))
            .Select(other => Footprint(document, other))
            .ToList();

    private bool Fits(BimDocument document, Viewport viewport, Point2D centre, List<BoundingBox2D> others)
    {
        var footprint = FootprintWithCentre(document, viewport, centre);
        var area = DrawingArea;

        const double tolerance = 1e-6;

        var inside = footprint.MinX >= area.MinX - tolerance && footprint.MaxX <= area.MaxX + tolerance &&
                     footprint.MinY >= area.MinY - tolerance && footprint.MaxY <= area.MaxY + tolerance;

        return inside && !others.Any(other => Overlap(footprint, other));
    }

    /// <summary>
    /// Pulls a centre back far enough that the whole footprint is inside the drawing area.
    /// A view too big for the area in one direction is centred in it that way instead.
    /// </summary>
    private Point2D ClampIntoArea(BimDocument document, Viewport viewport, Point2D centre)
    {
        var footprint = FootprintWithCentre(document, viewport, centre);
        var area = DrawingArea;

        var x = centre.X;
        var y = centre.Y;

        if (footprint.Width > area.Width) x += area.Centre.X - footprint.Centre.X;
        else if (footprint.MinX < area.MinX) x += area.MinX - footprint.MinX;
        else if (footprint.MaxX > area.MaxX) x -= footprint.MaxX - area.MaxX;

        if (footprint.Height > area.Height) y += area.Centre.Y - footprint.Centre.Y;
        else if (footprint.MinY < area.MinY) y += area.MinY - footprint.MinY;
        else if (footprint.MaxY > area.MaxY) y -= footprint.MaxY - area.MaxY;

        return new Point2D(x, y);
    }

    /// <summary>Whether two footprints share any area. Touching edges do not count.</summary>
    private static bool Overlap(BoundingBox2D a, BoundingBox2D b)
    {
        const double tolerance = 1e-6;

        return a.MinX < b.MaxX - tolerance && a.MaxX > b.MinX + tolerance &&
               a.MinY < b.MaxY - tolerance && a.MaxY > b.MinY + tolerance;
    }

    private static double Distance(Point2D a, Point2D b) => a.DistanceTo(b);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(SheetParameters.Number, () => Number, v => Number = v);
        yield return ParameterValue.Bind(SheetParameters.Name, () => Name, v => Name = v);

        yield return ParameterValue.BindChoice(
            SheetParameters.PaperSize,
            () => PaperSize.ToString(),
            v => { if (EnumText.TryParse<PaperSize>(v, out var size)) PaperSize = size; },
            Enum.GetNames<PaperSize>());

        yield return ParameterValue.BindChoice(
            SheetParameters.Orientation,
            () => EnumText.Humanise(Orientation),
            v => { if (EnumText.TryParse<PaperOrientation>(v, out var o)) Orientation = o; },
            EnumText.Choices<PaperOrientation>());

        yield return ParameterValue.ReadOnly(SheetParameters.Width, () => Width);
        yield return ParameterValue.ReadOnly(SheetParameters.Height, () => Height);
        yield return ParameterValue.ReadOnly(SheetParameters.ViewCount, () => Viewports.Count);

        yield return ParameterValue.Bind(SheetParameters.DrawnBy, () => DrawnBy, v => DrawnBy = v);
        yield return ParameterValue.Bind(SheetParameters.CheckedBy, () => CheckedBy, v => CheckedBy = v);
        yield return ParameterValue.Bind(SheetParameters.Revision, () => Revision, v => Revision = v);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => $"{Number} - {Name}";
}

/// <summary>
/// Where the title block sits and how big it is. Kept here rather than in the renderer
/// because the layout code needs to know how much of the paper is not available for drawings.
/// </summary>
public static class TitleBlock
{
    /// <summary>Clear margin around the edge of the paper, in millimetres.</summary>
    public const double Margin = 10;

    /// <summary>The strip down the right-hand edge, sized to the sheet.</summary>
    public static double Width(Sheet sheet) => sheet.PaperSize switch
    {
        PaperSize.A0 or PaperSize.A1 => 160,
        PaperSize.A2 => 140,
        _ => 120
    };
}

public static class Sheets
{
    /// <summary>
    /// The next drawing number in the series, continuing from the highest already used:
    /// A-101, A-102, and so on. Two sheets with the same number is the kind of mistake that
    /// is only noticed on site.
    /// </summary>
    public static string NextNumber(BimDocument document)
    {
        var highest = 100;

        foreach (var sheet in document.Elements.OfType<Sheet>())
        {
            var digits = new string(sheet.Number.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var number) && number > highest) highest = number;
        }

        return $"A-{highest + 1}";
    }
}

public static class SheetParameters
{
    public static readonly ParameterDefinition Number =
        new("Sheet Number", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Name =
        new("Sheet Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition PaperSize =
        new("Paper Size", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Orientation =
        new("Orientation", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Width =
        new("Sheet Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Sheet Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition ViewCount =
        new("Views on Sheet", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition DrawnBy =
        new("Drawn By", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition CheckedBy =
        new("Checked By", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Revision =
        new("Revision", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}
