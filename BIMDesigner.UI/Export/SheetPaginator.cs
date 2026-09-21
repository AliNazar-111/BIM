using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.UI.Export;

/// <summary>
/// Feeds sheets to the Windows print system, one page each (specification section 6.5).
///
/// It renders a sheet only when the printer asks for that page, so printing a large set does
/// not build the whole thing in memory first - and every page is cut from the model at the
/// moment it is printed, exactly as it is on screen.
/// </summary>
public sealed class SheetPaginator : DocumentPaginator
{
    /// <summary>WPF measures printed output in device-independent units of 1/96 inch.</summary>
    private const double UnitsPerMillimetre = 96.0 / 25.4;

    private readonly BimDocument _document;
    private readonly IReadOnlyList<Sheet> _sheets;

    public SheetPaginator(BimDocument document, IReadOnlyList<Sheet> sheets)
    {
        _document = document;
        _sheets = sheets;

        PageSize = sheets.Count > 0
            ? new Size(sheets[0].Width * UnitsPerMillimetre, sheets[0].Height * UnitsPerMillimetre)
            : new Size(210 * UnitsPerMillimetre, 297 * UnitsPerMillimetre);
    }

    public override bool IsPageCountValid => true;

    public override int PageCount => _sheets.Count;

    /// <summary>
    /// Set by the print system, and deliberately ignored when producing a page.
    ///
    /// A sheet has a size of its own: an A1 drawing is A1. Rescaling it to whatever paper
    /// happens to be loaded would make its printed scale a lie, and a drawing whose scale bar
    /// does not match its lines is worse than no drawing. Fitting to the paper is the print
    /// driver's job, and it says so on the dialog.
    /// </summary>
    public override Size PageSize { get; set; }

    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        if (pageNumber < 0 || pageNumber >= _sheets.Count) return DocumentPage.Missing;

        var sheet = _sheets[pageNumber];
        var size = new Size(sheet.Width * UnitsPerMillimetre, sheet.Height * UnitsPerMillimetre);

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
            dc.DrawDrawing(SheetExport.Render(_document, sheet, UnitsPerMillimetre));

        return new DocumentPage(visual, size, new Rect(size), new Rect(size));
    }
}
