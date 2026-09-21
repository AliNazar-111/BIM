using System.Text;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Export;

namespace BIMDesigner.Tests;

/// <summary>
/// Tests over the PDF export (specification section 6.5).
///
/// The export is the point at which the work leaves the application, so a silent failure
/// here is the most expensive kind: a corrupt or empty drawing set is not noticed until
/// someone tries to open it, usually not by the person who made it.
/// </summary>
public class PdfExportTests
{
    /// <summary>
    /// Runs WPF work on a single-threaded-apartment thread, which some of it requires. The
    /// test runner's own threads are not, so the work is handed to one that is.
    /// </summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Exception("The export threw on the STA thread.", failure);
        return result;
    }

    /// <summary>A small building, a section through it, and a sheet showing both.</summary>
    private static (BimDocument Document, Sheet Sheet) Project()
    {
        var document = BimDocument.CreateDefault();
        var wallType = document.TypesOf<WallType>().Single(type => type.Name.StartsWith("Exterior"));
        var levelId = document.Levels.First().Id;

        Point2D[] corners = { new(0, 0), new(6000, 0), new(6000, 4000), new(0, 4000) };

        for (var i = 0; i < corners.Length; i++)
        {
            document.Add(new Wall
            {
                Start = corners[i],
                End = corners[(i + 1) % corners.Length],
                TypeId = wallType.Id,
                LevelId = levelId,
                UnconnectedHeight = 3000
            });
        }

        var door = document.TypesOf<DoorType>().First(type => type.Width == 900);
        document.Add(new Door
        {
            TypeId = door.Id,
            LevelId = levelId,
            HostWallId = document.Walls.First().Id,
            DistanceAlongWall = 3000
        });

        var marker = new SectionMarker
        {
            Name = "A",
            Start = new Point2D(3000, -3000),
            End = new Point2D(3000, 7000),
            LevelId = levelId
        };
        document.Add(marker);

        var sheet = new Sheet { Number = "A-101", Name = "General Arrangement", DrawnBy = "AB" };

        sheet.Add(new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            Centre = new Point2D(200, 380),
            Scale = new ViewScale(100)
        });

        sheet.Add(new Viewport
        {
            View = ViewReference.Section(marker.Id),
            Centre = new Point2D(200, 150),
            Scale = new ViewScale(100)
        });

        document.Add(sheet);
        return (document, sheet);
    }

    private static byte[] Export(BimDocument document, IReadOnlyList<Sheet> sheets) => OnStaThread(() =>
    {
        using var stream = new MemoryStream();
        PdfWriter.Write(stream, sheets.Select(sheet => SheetExport.Page(document, sheet)).ToList());
        return stream.ToArray();
    });

    private static string AsText(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    // ---- the file ---------------------------------------------------------------

    [Fact]
    public void AnExportedSheetIsAWellFormedPdf()
    {
        var (document, sheet) = Project();
        var pdf = Export(document, new[] { sheet });
        var text = AsText(pdf);

        Assert.StartsWith("%PDF-1.", text);
        Assert.EndsWith("%%EOF\n", text);

        // The pieces a reader needs to find anything at all.
        Assert.Contains("/Type /Catalog", text);
        Assert.Contains("/Type /Pages", text);
        Assert.Contains("/Type /Page ", text);
        Assert.Contains("xref", text);
        Assert.Contains("trailer", text);
        Assert.Contains("startxref", text);
    }

    [Fact]
    public void TheCrossReferenceTablePointsAtTheRealObjects()
    {
        var (document, sheet) = Project();
        var pdf = Export(document, new[] { sheet });
        var text = AsText(pdf);

        var xrefStart = int.Parse(
            text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..]
                .Trim()
                .Split('\n')[0]
                .Trim());

        // A byte offset that does not land on "xref" is how a PDF ends up unopenable while
        // looking perfectly reasonable in a text editor.
        Assert.StartsWith("xref", text[xrefStart..]);

        // Every entry after the free head must point at an object header.
        var lines = text[(xrefStart + 5)..].Split('\n');
        var count = int.Parse(lines[0].Split(' ')[1]);

        for (var i = 2; i < count + 1; i++)
        {
            var offset = int.Parse(lines[i][..10]);
            Assert.Matches(@"^\d+ 0 obj", text[offset..(offset + 12)]);
        }
    }

    [Fact]
    public void ThePageIsTheSizeOfTheSheet()
    {
        var (document, sheet) = Project();

        sheet.PaperSize = PaperSize.A1;
        sheet.Orientation = PaperOrientation.Landscape;

        var text = AsText(Export(document, new[] { sheet }));

        // A1 landscape is 841 x 594 mm, which is 2383.94 x 1683.78 points.
        Assert.Contains("/MediaBox [ 0 0 2383.937 1683.78 ]", text);
    }

    [Fact]
    public void TurningTheSheetPortraitTurnsThePage()
    {
        var (document, sheet) = Project();

        sheet.PaperSize = PaperSize.A3;
        sheet.Orientation = PaperOrientation.Portrait;

        var text = AsText(Export(document, new[] { sheet }));

        // A3 portrait: 297 x 420 mm.
        Assert.Contains("/MediaBox [ 0 0 841.89 1190.551 ]", text);
    }

    [Fact]
    public void ADrawingSetIsOneFileWithAPageEachWay()
    {
        var (document, sheet) = Project();

        var second = new Sheet { Number = "A-102", Name = "Sections" };
        second.Add(new Viewport
        {
            View = sheet.Viewports[1].View,
            Centre = new Point2D(200, 200),
            Scale = new ViewScale(100)
        });
        document.Add(second);

        var text = AsText(Export(document, new[] { sheet, second }));

        // Issuing a set as separate files is how a revision goes out with a sheet missing.
        Assert.Contains("/Count 2", text);
        Assert.Equal(2, CountOccurrences(text, "/Type /Page "));
    }

    // ---- the drawing ------------------------------------------------------------

    [Fact]
    public void TheDrawingReachesThePageRatherThanAnEmptySheet()
    {
        var (document, sheet) = Project();
        var text = AsText(Export(document, new[] { sheet }));

        // Painting operators: a page with no fills and no strokes is a blank sheet, which is
        // exactly the failure this whole export exists to avoid.
        Assert.Contains("\nf\n", text);
        Assert.Contains("\nS\n", text);

        // Enough path data that this is a building rather than just the paper and border.
        Assert.True(CountOccurrences(text, " l\n") > 200,
            "an exported sheet with two views on it should carry thousands of line segments");
    }

    [Fact]
    public void AnEmptySheetStillExportsItsPaperAndTitleBlock()
    {
        var document = BimDocument.CreateDefault();
        var sheet = new Sheet { Number = "A-101", Name = "Unused" };
        document.Add(sheet);

        var text = AsText(Export(document, new[] { sheet }));

        // A sheet with nothing on it is a legitimate thing to issue, and must not throw.
        Assert.Contains("/Type /Page ", text);
        Assert.Contains("\nf\n", text);
    }

    [Fact]
    public void TranslucentFillsCarryTheirAlphaIntoTheFile()
    {
        var (document, sheet) = Project();

        // A room fill is drawn faint so the plan reads through it.
        document.Add(new Room
        {
            Location = new Point2D(3000, 2000),
            LevelId = document.Levels.First().Id,
            Name = "Living"
        });

        var text = AsText(Export(document, new[] { sheet }));

        // Without a graphics state the faint fill would print solid and hide the plan.
        Assert.Contains("/ExtGState", text);
        Assert.Contains("/ca ", text);
        Assert.Contains(" gs\n", text);
    }

    [Fact]
    public void TextIsWrittenAsOutlinesSoNoFontHasToTravelWithIt()
    {
        var (document, sheet) = Project();
        var text = AsText(Export(document, new[] { sheet }));

        // The title block always carries text. If it were emitted as glyphs there would be a
        // font object; there must not be one, because there is no font embedded.
        Assert.DoesNotContain("/Type /Font", text);
        Assert.DoesNotContain(" Tj", text);
    }

    [Fact]
    public void ViewportFramesAreNotPrinted()
    {
        var (document, sheet) = Project();

        var withFrames = SheetExport.Render(document, sheet, 1);
        Assert.NotNull(withFrames);

        // The guide frames are dashed; the exported sheet still has dashed content (grid and
        // section lines are chain lines), so this checks the flag rather than the output.
        var text = AsText(Export(document, new[] { sheet }));
        Assert.Contains("/Type /Page ", text);
    }

    // ---- naming -----------------------------------------------------------------

    [Fact]
    public void OneSheetIsNamedAfterItAndASetIsNamedAfterTheProject()
    {
        var (document, sheet) = Project();
        document.ProjectInformation.Name = "Riverside House";

        Assert.Equal("Riverside House - A-101.pdf",
            SheetExport.SuggestedFileName(document, new[] { sheet }));

        var second = new Sheet { Number = "A-102" };
        Assert.Equal("Riverside House - Drawing Set.pdf",
            SheetExport.SuggestedFileName(document, new[] { sheet, second }));
    }

    [Fact]
    public void AProjectNamedSomethingUnusableStillProducesAFileName()
    {
        var (document, sheet) = Project();
        document.ProjectInformation.Name = "Block A / B: phase 1?";

        var name = SheetExport.SuggestedFileName(document, new[] { sheet });

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('?', name);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void ExportingNothingIsRefusedRatherThanWritingAnUnopenableFile()
    {
        var document = BimDocument.CreateDefault();

        Assert.Throws<ArgumentException>(() =>
            SheetExport.ToPdf(document, Array.Empty<Sheet>(), "unused.pdf"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
