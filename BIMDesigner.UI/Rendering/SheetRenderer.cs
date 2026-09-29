using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
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
                _plan.DetailLevel = DetailLevel.Fine;
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
                break;
            }
        }
    }

    private void DrawViewportTitle(DrawingContext dc, Viewport viewport, Rect frame)
    {
        if (Document is null) return;

        var title = Text(viewport.TitleIn(Document), Paper(3.6), _titleTextBrush, bold: true);
        var scale = Text(viewport.Scale.ToString(), Paper(2.6), _titleMutedBrush);

        var baseline = frame.Bottom + Paper(3);

        dc.DrawText(title, new Point(frame.X, baseline));
        dc.DrawText(scale, new Point(frame.X, baseline + title.Height));

        // The rule under a view title is the convention that binds the two lines to the
        // drawing above rather than to the one below.
        var rule = baseline + title.Height + scale.Height + Paper(1);
        dc.DrawLine(_titleBlockPen,
            new Point(frame.X, rule),
            new Point(frame.X + Math.Max(title.Width, Paper(30)), rule));
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
        var scales = Sheet.Viewports.Select(viewport => viewport.Scale.ToString()).Distinct().ToList();

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
            ("Revision", Sheet.Revision, 2.4, 3.6)
        });

        var gap = Paper(3);
        var x = frame.X + padding;

        DrawRows(dc, identity, x, frame.Y + padding, gap);

        var issueTop = frame.Bottom - padding - MeasureRows(issue, gap);
        DrawRows(dc, issue, x, issueTop, gap);

        dc.DrawLine(_titleBlockPen,
            new Point(frame.X, issueTop - gap),
            new Point(frame.X + frame.Width, issueTop - gap));
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
