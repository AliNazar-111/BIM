using System.IO;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI.Export;

/// <summary>
/// Turns sheets into a PDF drawing set (specification section 6.5).
///
/// The sheets are drawn by the same <see cref="SheetRenderer"/> the sheet view uses, at a
/// transform where one unit is one PDF point. Nothing about the export knows what a wall or
/// a section is; it only decides how big a millimetre is. That is what makes "what you
/// checked is what you issued" true rather than aspirational.
/// </summary>
public static class SheetExport
{
    /// <summary>
    /// Writes one PDF containing every sheet given, in order, one page each.
    ///
    /// A drawing set is a set: issuing it as thirty separate files is how a revision goes out
    /// with two sheets missing.
    /// </summary>
    public static void ToPdf(BimDocument document, IReadOnlyList<Sheet> sheets, string path)
    {
        if (sheets.Count == 0) throw new ArgumentException("There are no sheets to export.", nameof(sheets));

        PdfWriter.Write(path, sheets.Select(sheet => Page(document, sheet)).ToList());
    }

    /// <summary>Draws one sheet at true size, in points, ready to be written or printed.</summary>
    public static PdfPage Page(BimDocument document, Sheet sheet)
    {
        return new PdfPage(
            sheet.Width * PdfWriter.PointsPerMillimetre,
            sheet.Height * PdfWriter.PointsPerMillimetre,
            Render(document, sheet, PdfWriter.PointsPerMillimetre));
    }

    /// <summary>
    /// Draws a sheet into a visual at a given number of units per paper millimetre, with the
    /// origin at the top-left corner and Y downward.
    ///
    /// The viewport frames and the paper's drop shadow are left off: both are editor
    /// furniture, and a dashed box printed round every drawing would be read as part of it.
    /// </summary>
    public static DrawingGroup Render(BimDocument document, Sheet sheet, double unitsPerMillimetre)
    {
        var renderer = new SheetRenderer
        {
            Document = document,
            Sheet = sheet,
            SelectedViewport = null,
            ShowGuides = false,

            // Text is measured in the same units it is drawn in, so one dip is one unit.
            PixelsPerDip = 1
        };

        renderer.SetTransform(
            paper => new Point(paper.X * unitsPerMillimetre, (sheet.Height - paper.Y) * unitsPerMillimetre),
            unitsPerMillimetre);

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            // The paper itself is drawn by the renderer, so an exported page is white because
            // the sheet is white - not because the exporter painted a background behind it.
            renderer.DrawContent(dc);
        }

        return visual.Drawing;
    }

    /// <summary>
    /// A suggested file name for a set: the project, and the sheet number when there is only
    /// one of them.
    /// </summary>
    public static string SuggestedFileName(BimDocument document, IReadOnlyList<Sheet> sheets)
    {
        var project = Sanitise(document.ProjectInformation.Name);
        if (string.IsNullOrWhiteSpace(project)) project = "Drawings";

        return sheets.Count == 1
            ? $"{project} - {Sanitise(sheets[0].Number)}.pdf"
            : $"{project} - Drawing Set.pdf";
    }

    private static string Sanitise(string name)
    {
        var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        return cleaned.Trim();
    }
}
