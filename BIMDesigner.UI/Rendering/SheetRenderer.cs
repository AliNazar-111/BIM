using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Draws a whole sheet - paper, border, title block and every viewport on it - into any
/// <see cref="DrawingContext"/>, under any transform.
///
/// The sheet on screen and the sheet in an exported PDF are produced by this one class, so a
/// drawing cannot come out of the printer looking different from the drawing that was
/// checked on screen. That is the same bargain <see cref="PlanRenderer"/> makes between the
/// plan editor and a viewport, one level up. (§6.5)
/// </summary>
public sealed class SheetRenderer
{
    private readonly PlanRenderer _plan = new(DrawingPalette.Paper);
    private readonly SectionRenderer _section = new(DrawingPalette.Paper);
    private readonly ProjectionRenderer _projection = new(DrawingPalette.Paper);

    private readonly Brush _paperBrush;
    private readonly Brush _shadowBrush;
    private readonly Brush _titleTextBrush;
    private readonly Brush _titleMutedBrush;
    private readonly Pen _paperEdgePen;
    private readonly Pen _borderPen;
    private readonly Pen _titleBlockPen;
    private readonly Pen _viewportPen;
    private readonly Pen _selectedPen;
    private readonly Pen _missingPen;

    private Func<Point2D, Point> _toScreen = paper => new Point(paper.X, -paper.Y);

    public SheetRenderer()
    {
        _paperBrush = RenderPens.Fill(Color.FromRgb(0xFC, 0xFC, 0xFA));
        _shadowBrush = RenderPens.Fill(Color.FromArgb(0x50, 0, 0, 0));
        _titleTextBrush = RenderPens.Fill(Color.FromRgb(0x1A, 0x1A, 0x1A));
        _titleMutedBrush = RenderPens.Fill(Color.FromRgb(0x6A, 0x70, 0x78));

        _paperEdgePen = RenderPens.Solid(Color.FromRgb(0x00, 0x00, 0x00), 1.0);
        _borderPen = RenderPens.Solid(Color.FromRgb(0x1A, 0x1A, 0x1A), 1.4);
        _titleBlockPen = RenderPens.Solid(Color.FromRgb(0x1A, 0x1A, 0x1A), 1.0);

        // A viewport's own frame is a guide, not part of the drawing, so it is drawn lightly
        // and does not compete with the lines inside it.
        _viewportPen = RenderPens.Dashed(Color.FromRgb(0xA8, 0xB0, 0xB8), 0.8, 4, 3);
        _selectedPen = RenderPens.Solid(Color.FromRgb(0x1D, 0x74, 0xC8), 1.8);
        _missingPen = RenderPens.Dashed(Color.FromRgb(0xB3, 0x2D, 0x12), 1.2, 5, 3);
    }

    public BimDocument? Document { get; set; }

    public Sheet? Sheet { get; set; }

    /// <summary>Highlighted while laying the sheet out. Always null when exporting.</summary>
    public Viewport? SelectedViewport { get; set; }

    /// <summary>Surface pixels to one millimetre of paper.</summary>
    public double PixelsPerPaperMm { get; private set; } = 1;

    public double PixelsPerDip { get; set; } = 1;

    /// <summary>
    /// Whether to draw the things that help lay a sheet out but are not on the sheet: the
    /// paper's drop shadow and the viewport frames.
    ///
    /// They are off when exporting. A viewport boundary is a piece of editor furniture, and
    /// printing one would put a dashed box round every drawing on the issued sheet.
    /// </summary>
    public bool ShowGuides { get; set; } = true;

    public void SetTransform(Func<Point2D, Point> toScreen, double pixelsPerPaperMm)
    {
        _toScreen = toScreen;
        PixelsPerPaperMm = pixelsPerPaperMm > 0 ? pixelsPerPaperMm : 1;
    }

    public void InvalidateBrushes() => _plan.InvalidateBrushes();

    /// <summary>A size given in paper millimetres, as surface pixels.</summary>
    private double Paper(double millimetres) => millimetres * PixelsPerPaperMm;

    private Rect ToScreen(BoundingBox2D paper)
    {
        var topLeft = _toScreen(new Point2D(paper.MinX, paper.MaxY));
        var bottomRight = _toScreen(new Point2D(paper.MaxX, paper.MinY));
        return new Rect(topLeft, bottomRight);
    }

    // ---- the sheet --------------------------------------------------------------

    public void DrawContent(DrawingContext dc)
    {
        if (Sheet is null || Document is null) return;

        DrawPaper(dc);

        foreach (var viewport in Sheet.Viewports) DrawViewport(dc, viewport);

        DrawTitleBlock(dc);
    }

    private void DrawPaper(DrawingContext dc)
    {
        if (Sheet is null) return;

        var paper = ToScreen(Sheet.Bounds);

        // A faint drop shadow is the cheapest way to say "this is a sheet of paper on a desk"
        // rather than "this is the background of the application".
        if (ShowGuides)
        {
            dc.DrawRectangle(_shadowBrush, null,
                new Rect(paper.X + 4, paper.Y + 4, paper.Width, paper.Height));
        }

        dc.DrawRectangle(_paperBrush, ShowGuides ? _paperEdgePen : null, paper);

        // The drawing border, inside the trimmed edge of the sheet.
        var margin = TitleBlock.Margin;
        var border = ToScreen(new BoundingBox2D(
            margin, margin, Sheet.Width - margin, Sheet.Height - margin));

        dc.DrawRectangle(null, _borderPen, border);
    }

    /// <summary>
    /// Draws one viewport: its frame, the view itself clipped inside it, and its title.
    ///
    /// The content is produced by the same renderers the plan and section editors use, given
    /// a transform that lands the view's extent inside this rectangle at this scale.
    /// </summary>
    private void DrawViewport(DrawingContext dc, Viewport viewport)
    {
        if (Document is null) return;

        var bounds = viewport.PaperBounds(Document);
        var frame = ToScreen(bounds);
        if (frame.Width <= 0 || frame.Height <= 0) return;

        var isSelected = ReferenceEquals(viewport, SelectedViewport);
        var exists = viewport.View.ExistsIn(Document);

        if (exists)
        {
            dc.PushClip(new RectangleGeometry(frame));
            DrawViewportContent(dc, viewport, bounds);
            dc.Pop();
        }

        // A view that is off the drawing area or on top of another is framed in red while the
        // sheet is being laid out. It usually got there without being dragged - the paper was
        // made smaller, or a scale made larger - so it has to be pointed out rather than
        // left for someone to notice on the printed sheet.
        var misplaced = ShowGuides && Sheet is not null && !Sheet.IsWellPlaced(Document, viewport);

        if (ShowGuides || !exists)
        {
            var pen = !exists || misplaced ? _missingPen : isSelected ? _selectedPen : _viewportPen;
            dc.DrawRectangle(null, pen, frame);

            // Selection still has to be visible on a misplaced view, or it cannot be seen which
            // one is being moved out of the way.
            if (misplaced && isSelected)
                dc.DrawRectangle(null, _selectedPen, new Rect(frame.X - 3, frame.Y - 3, frame.Width + 6, frame.Height + 6));
        }

        if (!exists)
        {
            var warning = Text("The view this came from has been deleted.", Paper(3), _missingPen.Brush);
            dc.DrawText(warning, new Point(
                frame.X + Paper(2),
                frame.Y + frame.Height / 2 - warning.Height / 2));
        }

        if (viewport.ShowTitle) DrawViewportTitle(dc, viewport, frame);
    }

    private void DrawViewportContent(DrawingContext dc, Viewport viewport, BoundingBox2D bounds)
    {
        if (Document is null) return;

        var extent = ViewExtent.Of(Document, viewport.View).OrAtLeast(1000);
        var scale = viewport.Scale;

        // View coordinates to paper: the extent's bottom-left corner sits on the viewport's
        // bottom-left corner, and everything shrinks by the scale denominator.
        Point ViewToScreen(double alongX, double upY) => _toScreen(new Point2D(
            bounds.MinX + scale.ToPaper(alongX - extent.MinX),
            bounds.MinY + scale.ToPaper(upY - extent.MinY)));

        // Magnification of the building on this surface: paper zoom divided by the scale.
        var pixelsPerModelMm = PixelsPerPaperMm / (scale.IsValid ? scale.Denominator : 1);

        switch (viewport.View.Kind)
        {
            case ViewKind.FloorPlan:
                _plan.Document = Document;
                _plan.ActiveLevelId = viewport.View.TargetId;
                _plan.Filter = Document.ViewSettings.FilterFor(Document, viewport.View);
                _plan.DetailLevel = Document.ViewSettings.DetailLevelOf(viewport.View) ?? DetailLevel.Fine;
                _plan.ShowWallLabels = false;
                _plan.View = viewport.View;

                // Paper has no selection: a highlight is an editing state, not a drawing.
                _plan.SetSelection(null);
                _plan.PixelsPerDip = PixelsPerDip;
                _plan.PixelsPerPaperMm = PixelsPerPaperMm;
                _plan.SetTransform(point => ViewToScreen(point.X, point.Y), pixelsPerModelMm);
                _plan.DrawContent(dc);
                break;

            case ViewKind.Section:
            {
                var marker = Document.Elements.OfType<SectionMarker>()
                    .FirstOrDefault(m => m.Id == viewport.View.TargetId);

                if (marker is null) return;

                _section.SetSelection(null);
                _section.PixelsPerDip = PixelsPerDip;
                _section.PixelsPerPaperMm = PixelsPerPaperMm;
                _section.SetTransform(ViewToScreen);

                // Datum names are suppressed: they would spill outside the frame and be
                // clipped, which reads as a mistake rather than as a label.
                _section.DrawContent(dc, SectionProjection.Build(Document, marker, Document.ViewSettings.FilterFor(Document, viewport.View)), labelLevels: false);
                _section.DrawCallouts(dc, Document, viewport.View);
                break;
            }

            // A callout: its parent drawn again at its own scale and detail, cropped to its box by the viewport's frame.
            case ViewKind.Callout when Document.FindElement(viewport.View.TargetId) is Callout callout:
                if (callout.Parent.Kind == ViewKind.FloorPlan)
                {
                    _plan.Document = Document;
                    _plan.ActiveLevelId = callout.Parent.TargetId;
                    _plan.Filter = Document.ViewSettings.FilterFor(Document, viewport.View);
                    _plan.DetailLevel = Document.ViewSettings.DetailLevelOf(viewport.View) ?? DetailLevel.Fine;
                    _plan.ShowWallLabels = false;
                    _plan.View = viewport.View;
                    _plan.SetSelection(null);
                    _plan.PixelsPerDip = PixelsPerDip;
                    _plan.PixelsPerPaperMm = PixelsPerPaperMm;
                    _plan.SetTransform(point => ViewToScreen(point.X, point.Y), pixelsPerModelMm);
                    _plan.DrawContent(dc);
                }
                else if (Document.Elements.OfType<SectionMarker>().FirstOrDefault(m => m.Id == callout.Parent.TargetId) is { } cut)
                {
                    _section.SetSelection(null);
                    _section.PixelsPerDip = PixelsPerDip;
                    _section.PixelsPerPaperMm = PixelsPerPaperMm;
                    _section.SetTransform(ViewToScreen);
                    _section.DrawContent(dc, SectionProjection.Build(Document, cut, Document.ViewSettings.FilterFor(Document, viewport.View)), labelLevels: false);
                }

                break;

            // A 3D view as it was left, or an elevation: the model projected into lines and painted faces.
            case ViewKind.Model3D or ViewKind.Elevation:
                _projection.PixelsPerDip = PixelsPerDip;
                _projection.PixelsPerPaperMm = PixelsPerPaperMm;
                _projection.SetTransform(ViewToScreen);
                _projection.DrawContent(dc, Projections.Of(Document, viewport.View), labelLevels: false);
                break;

            // A schedule, or a keynote legend: its table as it now reports, the size its text is.
            case ViewKind.Schedule or ViewKind.KeynoteLegend when viewport.Table(Document) is { } table:
                DrawTable(dc, table, bounds);
                break;
        }
    }

    /// <summary>
    /// A table laid out on paper, its top-left corner at the top-left of a box: ruled round and
    /// between its columns, its title and headings bold, its groups' headings and totals set
    /// apart, numbers to the right, and cells coloured where formatting colours them.
    /// </summary>
    public void DrawTable(DrawingContext dc, ScheduleTable table, BoundingBox2D bounds)
    {
        var left = bounds.MinX;
        var top = bounds.MaxY;
        var thin = new Pen(_titleTextBrush, Math.Max(0.5, Paper(0.18)));
        var heavy = new Pen(_titleTextBrush, Math.Max(0.8, Paper(0.35)));
        thin.Freeze();
        heavy.Freeze();

        Rect Cell(double x0, double y0, double x1, double y1) => new(_toScreen(new Point2D(x0, y0)), _toScreen(new Point2D(x1, y1)));

        var y = top;
        foreach (var line in table.Lines)
        {
            var bottom = y - line.Height;
            var size = Paper(line.Kind == ScheduleLineKind.Title ? ScheduleTable.TitleSize : ScheduleTable.TextSize);
            var bold = line.Kind is ScheduleLineKind.Title or ScheduleLineKind.Header or ScheduleLineKind.GroupHeading or ScheduleLineKind.Total;

            if (line.Spans)
            {
                var text = Text(line.Cells[0], size, _titleTextBrush, bold);
                var row = Cell(left, y, left + table.Width, bottom);
                var x = line.Kind == ScheduleLineKind.Title ? row.X + (row.Width - text.Width) / 2 : row.X + Paper(ScheduleTable.Padding);
                dc.DrawText(text, new Point(x, row.Y + (row.Height - text.Height) / 2));
            }
            else
            {
                var x0 = left;
                for (var i = 0; i < table.ColumnWidths.Count; i++)
                {
                    var x1 = x0 + table.ColumnWidths[i];
                    var cell = Cell(x0, y, x1, bottom);
                    if (i < line.Backgrounds.Count && line.Backgrounds[i] is { } colour)
                        dc.DrawRectangle(RenderPens.Fill(Color.FromRgb(colour.R, colour.G, colour.B)), null, cell);

                    if (i < line.Cells.Count && !string.IsNullOrEmpty(line.Cells[i]))
                    {
                        var text = Text(line.Cells[i], size, _titleTextBrush, bold);
                        var pad = Paper(ScheduleTable.Padding);

                        // Headings centred over their columns; numbers to the right; words to the left.
                        var x = line.Kind == ScheduleLineKind.Header ? cell.X + (cell.Width - text.Width) / 2
                            : table.Numeric[i] ? cell.Right - pad - text.Width
                            : cell.X + pad;
                        dc.PushClip(new RectangleGeometry(cell));
                        dc.DrawText(text, new Point(x, cell.Y + (cell.Height - text.Height) / 2));
                        dc.Pop();
                    }

                    if (i > 0) dc.DrawLine(thin, _toScreen(new Point2D(x0, y)), _toScreen(new Point2D(x0, bottom)));
                    x0 = x1;
                }
            }

            // Rules: heavy under the headings and over the total; light between rows.
            var under = line.Kind is ScheduleLineKind.Header or ScheduleLineKind.Title ? heavy : thin;
            dc.DrawLine(line.Kind == ScheduleLineKind.Subtotal ? heavy : under, _toScreen(new Point2D(left, bottom)), _toScreen(new Point2D(left + table.Width, bottom)));
            y = bottom;
        }

        dc.DrawRectangle(null, heavy, Cell(left, top, left + table.Width, y));
    }

    private void DrawViewportTitle(DrawingContext dc, Viewport viewport, Rect frame)
    {
        if (Document is null) return;

        var title = Text(viewport.TitleIn(Document), Paper(3.6), _titleTextBrush, bold: true);

        // A schedule has no scale: it is the size its text is.
        var scale = Text(viewport.IsTable ? string.Empty : viewport.Scale.ToString(), Paper(2.6), _titleMutedBrush);

        var baseline = frame.Bottom + Paper(3);

        // Its detail number in a circle before the title: what a callout, section or elevation
        // pointing at the view quotes, with the sheet's number.
        var left = frame.X;
        if (!string.IsNullOrWhiteSpace(viewport.DetailNumber))
        {
            var radius = Paper(3.4);
            var centre = new Point(frame.X + radius, baseline + radius + Paper(0.6));
            dc.DrawEllipse(null, _titleBlockPen, centre, radius, radius);
            var number = Text(viewport.DetailNumber, Paper(3.2), _titleTextBrush, bold: true);
            dc.DrawText(number, new Point(centre.X - number.Width / 2, centre.Y - number.Height / 2));
            left = frame.X + radius * 2 + Paper(2);
        }

        dc.DrawText(title, new Point(left, baseline));
        dc.DrawText(scale, new Point(left, baseline + title.Height));

        // The rule under a view title is the convention that binds the two lines to the
        // drawing above rather than to the one below.
        var rule = baseline + title.Height + scale.Height + Paper(1);
        dc.DrawLine(_titleBlockPen,
            new Point(frame.X, rule),
            new Point(left + Math.Max(title.Width, Paper(30)), rule));
    }

    /// <summary>
    /// The title block: who drew it, what it is, and which number it is filed under.
    ///
    /// Every field except the ones typed onto the sheet is read from the project, so a project
    /// renamed once is renamed on every sheet.
    /// </summary>
    private void DrawTitleBlock(DrawingContext dc)
    {
        if (Sheet is null || Document is null) return;

        var margin = TitleBlock.Margin;
        var width = TitleBlock.Width(Sheet);
        var left = Sheet.Width - margin - width;

        var frame = ToScreen(new BoundingBox2D(left, margin, Sheet.Width - margin, Sheet.Height - margin));

        dc.DrawRectangle(_paperBrush, _borderPen, frame);

        var project = Document.ProjectInformation;
        var padding = Paper(4);

        // The scales actually used on this sheet, which is what a reader needs before picking
        // up a rule - and it is wrong often enough on hand-kept title blocks to be worth
        // deriving rather than typing.
        var scales = Sheet.Viewports.Where(viewport => !viewport.IsTable).Select(viewport => viewport.Scale.ToString()).Distinct().ToList();

        // What the sheet is, at the top; who is accountable for it, at the bottom. A title
        // block that bunched everything against the top edge would waste the strip and read
        // as unfinished.
        var identity = BuildRows(new[]
        {
            ("Project", project.Name, 2.4, 4.4),
            ("Client", project.Client, 2.4, 3.2),
            ("Address", project.Address, 2.4, 3.2),
            ("Drawing Title", Sheet.Name, 2.4, 4.4)
        });

        var issue = BuildRows(new[]
        {
            ("Drawn", Sheet.DrawnBy, 2.4, 3.2),
            ("Checked", Sheet.CheckedBy, 2.4, 3.2),
            ("Date", (Sheet.IssuedOn ?? DateTime.Today).ToString("d MMM yyyy", CultureInfo.CurrentCulture), 2.4, 3.2),
            ("Scale", scales.Count == 0 ? "-" : string.Join("   ", scales), 2.4, 3.2),
            ("Paper", $"{Sheet.PaperSize} {Sheet.Orientation}", 2.4, 3.2),
            ("Drawing Number", Sheet.Number, 2.4, 5.4),

            // The latest of the project's revisions on the sheet; or the one typed, where it has none.
            ("Revision", Sheet.CurrentRevision(Document), 2.4, 3.6)
        });

        var gap = Paper(3);
        var x = frame.X + padding;

        DrawRows(dc, identity, x, frame.Y + padding, gap);

        var issueTop = frame.Bottom - padding - MeasureRows(issue, gap);
        DrawRows(dc, issue, x, issueTop, gap);

        dc.DrawLine(_titleBlockPen,
            new Point(frame.X, issueTop - gap),
            new Point(frame.X + frame.Width, issueTop - gap));

        // The revision schedule over it: every revision on the sheet, the latest at the foot -
        // as many as there is room for under what the sheet is, the latest kept.
        var revisions = Revisions.OnSheet(Document, Sheet);
        if (revisions.Count > 0)
        {
            var room = issueTop - gap * 2 - (frame.Y + padding + MeasureRows(identity, gap) + gap);
            DrawRevisionSchedule(dc, revisions, frame.X + padding, issueTop - gap * 2, frame.Width - padding * 2, room);
        }
    }

    /// <summary>The revision schedule in the title block: number, date, what it was and who issued it, a row each, its foot at a height on this surface.</summary>
    private void DrawRevisionSchedule(DrawingContext dc, IReadOnlyList<Revision> revisions, double left, double bottom, double width, double room)
    {
        if (Document is null) return;
        var rowHeight = Paper(5);
        var fits = Math.Max(0, (int)(room / rowHeight) - 2);
        var shown = revisions.Skip(Math.Max(0, revisions.Count - fits)).ToList();
        if (shown.Count == 0) return;

        var widths = new[] { 0.14, 0.25, 0.45, 0.16 }.Select(share => share * width).ToArray();
        var rows = new List<string[]> { new[] { "Rev", "Date", "Description", "By" } };
        rows.AddRange(shown.Select(revision => new[] { Revisions.NumberOf(Document, revision), Revisions.FormatDate(revision.Date), revision.Description, revision.IssuedBy }));

        var top = bottom - rowHeight * rows.Count;
        var caption = Text("REVISIONS", Paper(2.4), _titleMutedBrush);
        dc.DrawText(caption, new Point(left, top - caption.Height - Paper(0.8)));

        for (var r = 0; r < rows.Count; r++)
        {
            var y = top + r * rowHeight;
            var x = left;
            for (var c = 0; c < widths.Length; c++)
            {
                var cell = new Rect(x, y, widths[c], rowHeight);
                dc.DrawRectangle(null, _titleBlockPen, cell);
                if (rows[r][c] is { Length: > 0 } value)
                {
                    var text = Text(value, Paper(2.3), r == 0 ? _titleMutedBrush : _titleTextBrush, bold: r > 0 && c == 0);
                    dc.PushClip(new RectangleGeometry(cell));
                    dc.DrawText(text, new Point(x + Paper(1), y + (rowHeight - text.Height) / 2));
                    dc.Pop();
                }

                x += widths[c];
            }
        }
    }

    /// <summary>A caption and its value, measured so a block of them can be placed as a unit.</summary>
    private sealed record TitleRow(FormattedText Caption, FormattedText Value)
    {
        public double Height => Caption.Height + Value.Height;
    }

    private List<TitleRow> BuildRows(
        IEnumerable<(string Caption, string Value, double CaptionSize, double ValueSize)> rows) =>
        rows
            // A field nobody has filled in is left off rather than printed as a dash: an empty
            // caption is noise, and the sheet's own fields are always shown regardless.
            .Where(row => !string.IsNullOrWhiteSpace(row.Value))
            .Select(row => new TitleRow(
                Text(row.Caption.ToUpperInvariant(), Paper(row.CaptionSize), _titleMutedBrush),
                Text(row.Value, Paper(row.ValueSize), _titleTextBrush, bold: true)))
            .ToList();

    private static double MeasureRows(List<TitleRow> rows, double gap) => rows.Sum(row => row.Height + gap);

    private void DrawRows(DrawingContext dc, List<TitleRow> rows, double x, double y, double gap)
    {
        foreach (var row in rows)
        {
            dc.DrawText(row.Caption, new Point(x, y));
            y += row.Caption.Height;

            dc.DrawText(row.Value, new Point(x, y));
            y += row.Value.Height + gap;
        }
    }

    private FormattedText Text(string value, double size, Brush brush, bool bold = false) => new(
        value,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        bold
            ? new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal)
            : new Typeface("Segoe UI"),
        Math.Max(1, size),
        brush,
        PixelsPerDip);
}
