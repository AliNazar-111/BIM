using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.UI;
using BIMDesigner.UI.Export;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// The energy and overheating report: a PDF of A4 pages setting out the SAP rating, the Future Homes Standard
/// and Part O checks, the home as SAP takes it and its worksheets - its tables broken between pages under their
/// heading rows, every page numbered of the whole.
/// </summary>
[Collection("Wpf")]
public class EnergyReportTests
{
    private static T OnUiThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                result = work();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("It failed on the UI thread.", failure);
        return result;
    }

    /// <summary>Text without its spaces: WPF sets a line's text in runs that can break inside a word.</summary>
    private static string Bare(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", string.Empty);

    /// <summary>The words a page's drawing writes, run together without their spaces.</summary>
    private static string Words(Drawing drawing)
    {
        var said = new StringBuilder();
        void Walk(Drawing each)
        {
            switch (each)
            {
                case DrawingGroup group:
                    foreach (var child in group.Children) Walk(child);
                    break;
                case GlyphRunDrawing glyphs when glyphs.GlyphRun?.Characters is { } characters:
                    said.Append(characters.ToArray()).Append(' ');
                    break;
            }
        }

        Walk(drawing);
        return Bare(said.ToString());
    }

    /// <summary>A living room and a bedroom with a casement each, flat-roofed.</summary>
    private static BimDocument Home()
    {
        var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "Living Room", "Bedroom 1");
        var south = document.Walls.Take(4).First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        var casement = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200");
        foreach (var room in rooms)
            document.Add(new BimWindow { TypeId = casement.Id, LevelId = room.LevelId, HostWallId = south.Id, DistanceAlongWall = Math.Abs(room.Location.X - south.Start.X), SillHeight = 900 });
        document.ProjectInformation.Name = "Plot 7";
        document.ProjectInformation.Address = "7 Mill Lane, Leeds LS1 4AP";
        return document;
    }

    [Fact]
    public void TheReportSetsOutTheRatingTheChecksAndTheWorksheets()
    {
        var document = Home();
        var (pages, text, pdf) = OnUiThread(() =>
        {
            var report = EnergyReport.Build(document, "1.2.3", new DateTime(2026, 10, 29))!;
            var drawings = report.Drawings();
            var path = Path.Combine(Path.GetTempPath(), $"report-{Guid.NewGuid():N}.pdf");
            try
            {
                EnergyReport.ToPdf(report, path);
                return (report.PageCount, drawings.Select(Words).ToList(), File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        });

        // The front: what it is, plainly a design-stage calculation; the ratings and the verdict.
        Assert.Contains(Bare("Energy and Overheating Report"), text[0]);
        Assert.Contains(Bare("Plot 7, 7 Mill Lane, Leeds LS1 4AP"), text[0]);
        Assert.Contains(Bare("not an accredited SAP assessment"), text[0]);
        Assert.Contains(Bare("Energy efficiency rating (SAP)"), string.Join(" ", text));

        // The rates against their targets and each check; the home, its fabric and junctions; Part O; the worksheets as appendices.
        var all = string.Join(" ", text);
        foreach (var said in new[] { "Dwelling emission rate (DER)", "What it asks, each in turn", "Its fabric", "Its thermal bridges", "Overheating - Approved Document O, simplified method",
                     "Appendix A - the home's worksheet", "Appendix E - the notional home's fabric energy efficiency" })
            Assert.Contains(Bare(said), all);

        // Each appendix on a page of its own; every page numbered of the whole and footed with the project.
        Assert.True(pages >= 6, $"{pages} pages");
        Assert.Contains(Bare($"Page 1 of {pages}"), text[0]);
        Assert.Contains(Bare($"Page {pages} of {pages}"), text[^1]);
        Assert.All(text, page => Assert.Contains(Bare("Plot 7 - energy and overheating report, 29 October 2026 - BIMDesigner 1.2.3"), page));

        // A PDF of as many A4 pages.
        var written = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", written);
        Assert.Contains($"/Type /Pages /Count {pages}", written);
        Assert.Contains("/MediaBox [ 0 0 595.28 841.89 ]", written);
    }

    [Fact]
    public void ALongTableGoesOnOverThePageUnderItsHeadingRow()
    {
        var text = OnUiThread(() =>
        {
            var report = new ReportDocument("Footer");
            report.Title("A long table", "To break over pages");
            report.Table(new[] { new ReportColumn("Item", 3), new ReportColumn("Amount", 1, Right: true) },
                Enumerable.Range(1, 120).Select(i => (IReadOnlyList<ReportCell>)new ReportCell[] { $"Row {i}", $"{i * 10}" }));
            return report.Drawings().Select(Words).ToList();
        });

        Assert.True(text.Count >= 2);
        Assert.All(text, page => Assert.Contains("Item", page));
        Assert.Contains("Row1", text[0]);
        Assert.Contains("Row120", text[^1]);
        Assert.DoesNotContain("Row120", text[0]);
    }

    [Fact]
    public void AUsProjectHasNoReport()
    {
        var document = Home();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Contains("US project", EnergyReport.WhyNot(document));
        Assert.Null(OnUiThread(() => EnergyReport.Build(document, "1", DateTime.Today)));
    }
}
