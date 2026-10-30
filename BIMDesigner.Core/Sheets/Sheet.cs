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

    /// <summary>Paper depth, mm, taken by a note under the title: a map's copyright line.</summary>
    public const double NoteBand = 5;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Whether a north point is drawn in its top right corner, pointing to true north.</summary>
    public bool ShowNorth { get; set; }

    /// <summary>Whether a scale bar is drawn beside its title, so it reads true however it is printed.</summary>
    public bool ShowScaleBar { get; set; }

    /// <summary>A line printed under its title - an Ordnance Survey map's copyright notice and licence; empty for none.</summary>
    public string Note { get; set; } = string.Empty;

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

    /// <summary>
    /// Its number on the sheet - 1, 2, 3 in the order views are placed - which callouts, sections
    /// and elevations pointing at the view quote with the sheet number: "2 / A-101".
    /// </summary>
    public string DetailNumber { get; set; } = string.Empty;

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
        // A schedule, or a legend, is a table the size of its text, whatever the scale.
        if (Table(document) is { } table)
            return new BoundingBox2D(
                Centre.X - table.Width / 2, Centre.Y - table.Height / 2,
                Centre.X + table.Width / 2, Centre.Y + table.Height / 2);

        var content = ViewExtent.Of(document, View).OrAtLeast(ViewExtent.Least(View));

        var halfWidth = Scale.ToPaper(content.Width) / 2;
        var halfHeight = Scale.ToPaper(content.Height) / 2;

        return new BoundingBox2D(
            Centre.X - halfWidth, Centre.Y - halfHeight,
            Centre.X + halfWidth, Centre.Y + halfHeight);
    }

    /// <summary>What a schedule or a keynote legend placed here lays out; null for a drawing.</summary>
    public Schedules.ScheduleTable? Table(BimDocument document) => View.Kind switch
    {
        ViewKind.Schedule when View.ScheduleIn(document) is { } schedule => Schedules.ScheduleTable.Of(document, schedule),
        ViewKind.KeynoteLegend => Annotation.Keynotes.LegendFor(document, this),
        ViewKind.ColourLegend => Views.RoomColours.Table(document, View),
        ViewKind.AreaSchedule => Views.AreaPlans.Table(document, View),
        _ => null
    };

    /// <summary>Whether it is a table - a schedule or a legend - rather than a drawing at a scale.</summary>
    public bool IsTable => View.Kind is ViewKind.Schedule or ViewKind.KeynoteLegend or ViewKind.ColourLegend or ViewKind.AreaSchedule;

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
        TitleOverride = TitleOverride,
        DetailNumber = DetailNumber,
        ShowNorth = ShowNorth,
        ShowScaleBar = ShowScaleBar,
        Note = Note
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
    private readonly List<SheetImage> _images = new();

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

    /// <summary>The pictures on it: renderings, photographs, logos - drawn over the views, in the order put there.</summary>
    public IReadOnlyList<SheetImage> Images => _images;

    public void AddImage(SheetImage image) => _images.Add(image);

    public void InsertImage(int index, SheetImage image) => _images.Insert(Math.Clamp(index, 0, _images.Count), image);

    public bool RemoveImage(SheetImage image) => _images.Remove(image);

    public int IndexOfImage(SheetImage image) => _images.IndexOf(image);

    public SheetImage? FindImage(Guid id) => _images.FirstOrDefault(image => image.Id == id);

    /// <summary>
    /// Revisions put on the sheet by hand: a change with nothing on a plan to cloud - a title, a
    /// note. Those clouded in a view on the sheet are on it anyway.
    /// </summary>
    public List<Guid> RevisionIds { get; } = new();

    /// <summary>Puts a view on the sheet, numbered after those on it already unless it has a number.</summary>
    public void Add(Viewport viewport)
    {
        // A schedule is not a drawing anything points to: it has no number.
        if (string.IsNullOrWhiteSpace(viewport.DetailNumber) && !viewport.IsTable) viewport.DetailNumber = NextDetailNumber();
        _viewports.Add(viewport);
    }

    /// <summary>The lowest detail number not yet used on the sheet.</summary>
    public string NextDetailNumber()
    {
        var used = _viewports.Select(viewport => viewport.DetailNumber).ToHashSet();
        for (var i = 1; ; i++)
            if (!used.Contains(i.ToString())) return i.ToString();
    }

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
        FootprintAt(viewport.PaperBounds(document), viewport);

    private static BoundingBox2D FootprintAt(BoundingBox2D drawing, Viewport viewport) =>
        viewport.ShowTitle
            ? new BoundingBox2D(drawing.MinX, drawing.MinY - Viewport.TitleBand - (viewport.Note.Length > 0 ? Viewport.NoteBand : 0), drawing.MaxX, drawing.MaxY)
            : drawing;

    /// <summary>Where a viewport would sit, and what it would take up, with its centre here.</summary>
    private static BoundingBox2D FootprintWithCentre(BimDocument document, Viewport viewport, Point2D centre)
    {
        var drawing = viewport.PaperBounds(document);
        var shift = centre - viewport.Centre;

        return FootprintAt(
            new BoundingBox2D(drawing.MinX + shift.X, drawing.MinY + shift.Y,
                              drawing.MaxX + shift.X, drawing.MaxY + shift.Y),
            viewport);
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
        BimDocument document, double width, double height, bool showTitle = true, Viewport? ignore = null, SheetImage? ignoreImage = null)
    {
        const double step = 5;

        var area = DrawingArea;
        var band = showTitle ? Viewport.TitleBand : 0;

        // Worked out once: a section's extent means cutting the model, and this loop would
        // otherwise do it several thousand times.
        var others = _viewports
            .Where(viewport => !ReferenceEquals(viewport, ignore))
            .Select(viewport => Footprint(document, viewport))
            .Concat(_images.Where(image => !ReferenceEquals(image, ignoreImage)).Select(image => image.Footprint))
            .ToList();

        // The first on an empty sheet goes in the middle of the drawing area, where a drawing is put; those after it fit round it.
        if (others.Count == 0 && width <= area.Width + 1e-9 && height + band <= area.Height + 1e-9)
            return new Point2D(area.Centre.X, area.Centre.Y + band / 2);

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
            .Concat(_images.Select(image => image.Footprint))
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


    /// <summary>Whether a picture is on the drawing area and clear of the views and the other pictures.</summary>
    public bool IsWellPlaced(BimDocument document, SheetImage image) => FitsImage(image, image.Centre, OthersThan(document, image));

    /// <summary>
    /// Where a picture being dragged towards <paramref name="desired"/> may go: held inside the drawing area and
    /// sliding along a view or another picture rather than through it, as a view does.
    /// </summary>
    public Point2D ConstrainMove(BimDocument document, SheetImage image, Point2D desired)
    {
        var others = OthersThan(document, image);
        var current = image.Centre;
        var target = ClampIntoArea(image, desired);
        if (!FitsImage(image, current, others) || FitsImage(image, target, others)) return target;

        var alongX = ClampIntoArea(image, new Point2D(target.X, current.Y));
        var alongY = ClampIntoArea(image, new Point2D(current.X, target.Y));
        return (FitsImage(image, alongX, others), FitsImage(image, alongY, others)) switch
        {
            (true, true) => Distance(alongX, desired) <= Distance(alongY, desired) ? alongX : alongY,
            (true, false) => alongX,
            (false, true) => alongY,
            _ => current
        };
    }

    /// <summary>Where a picture goes once its size has changed: where it is if it still fits, else back inside the area, else the next free space.</summary>
    public Point2D PositionAfterResize(BimDocument document, SheetImage image)
    {
        var others = OthersThan(document, image);
        if (FitsImage(image, image.Centre, others)) return image.Centre;
        var clamped = ClampIntoArea(image, image.Centre);
        if (FitsImage(image, clamped, others)) return clamped;
        var free = NextFreePosition(document, image.Width, image.Height, image.ShowTitle, null, image);
        return new Point2D(free.X, free.Y);
    }

    private List<BoundingBox2D> OthersThan(BimDocument document, SheetImage image) =>
        _viewports.Select(viewport => Footprint(document, viewport))
            .Concat(_images.Where(other => !ReferenceEquals(other, image)).Select(other => other.Footprint))
            .ToList();

    private static BoundingBox2D FootprintAt(SheetImage image, Point2D centre)
    {
        var shift = centre - image.Centre;
        var footprint = image.Footprint;
        return new BoundingBox2D(footprint.MinX + shift.X, footprint.MinY + shift.Y, footprint.MaxX + shift.X, footprint.MaxY + shift.Y);
    }

    private bool FitsImage(SheetImage image, Point2D centre, List<BoundingBox2D> others)
    {
        var footprint = FootprintAt(image, centre);
        var area = DrawingArea;
        const double tolerance = 1e-6;
        var inside = footprint.MinX >= area.MinX - tolerance && footprint.MaxX <= area.MaxX + tolerance &&
                     footprint.MinY >= area.MinY - tolerance && footprint.MaxY <= area.MaxY + tolerance;
        return inside && !others.Any(other => Overlap(footprint, other));
    }

    private Point2D ClampIntoArea(SheetImage image, Point2D centre)
    {
        var footprint = FootprintAt(image, centre);
        var area = DrawingArea;
        var (x, y) = (centre.X, centre.Y);
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
        yield return ParameterValue.BindValidated(SheetParameters.IssueDate, () => Annotation.Revisions.FormatDate(IssuedOn), text =>
        {
            if (!Annotation.Revisions.TryParseDate(text, out var date)) return false;
            IssuedOn = date;
            return true;
        });

        // Its revision as the project's revisions have it; typed, where the project has none on it.
        yield return ParameterValue.Bind(SheetParameters.Revision, () => Revision, v => Revision = v);
        yield return ParameterValue.ReadOnly(SheetParameters.CurrentRevision, () => CurrentRevision(document));
        yield return ParameterValue.ReadOnly(SheetParameters.CurrentRevisionDate, () => Annotation.Revisions.FormatDate(Annotation.Revisions.CurrentOn(document, this)?.Date));
        yield return ParameterValue.ReadOnly(SheetParameters.CurrentRevisionDescription, () => Annotation.Revisions.CurrentOn(document, this)?.Description ?? string.Empty);
        yield return ParameterValue.ReadOnly(SheetParameters.RevisionsOnSheet, () =>
            string.Join(", ", Annotation.Revisions.OnSheet(document, this).Select(revision => Annotation.Revisions.NumberOf(document, revision))));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    /// <summary>The latest of the project's revisions on the sheet; or, where none is, the revision typed on it.</summary>
    public string CurrentRevision(BimDocument document) =>
        Annotation.Revisions.CurrentOn(document, this) is { } current ? Annotation.Revisions.NumberOf(document, current) : Revision;

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

        foreach (var sheet in document.All<Sheet>())
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

    public static readonly ParameterDefinition IssueDate =
        new("Sheet Issue Date", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition CurrentRevision =
        new("Current Revision", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition CurrentRevisionDate =
        new("Current Revision Date", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition CurrentRevisionDescription =
        new("Current Revision Description", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition RevisionsOnSheet =
        new("Revisions on Sheet", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}
