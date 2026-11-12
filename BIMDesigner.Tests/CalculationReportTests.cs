using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Export;

namespace BIMDesigner.Tests;

/// <summary>
/// The structural calculations as a PDF for whoever checks them: what they come to and what they do not cover before
/// anything else, room to sign for them, each member and wall set out, and repeated members set out once.
/// </summary>
[Collection("Wpf")]
public class CalculationReportTests
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
                    said.Append(characters.ToArray());
                    break;
            }
        }

        Walk(drawing);
        return Bare(said.ToString());
    }

    private static ReportDocument Report(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var second = SecondOrderAnalysis.Run(document, analysis);
        return CalculationReport.Build(document, analysis, second, MemberStrength.Of(document, analysis, second), MasonryDesign.Of(document, second), PlateDesign.Of(document, second), FoundationDesign.Of(document, analysis), JointDesign.Of(document, second), FireDesign.Of(document, second, MemberStrength.Of(document, analysis, second)), Robustness.Of(document, second), "1.2.3", new DateTime(2026, 11, 5));
    }

    /// <summary>The house of cavity walls with its roof, on strip footings, and two steel posts carrying a beam across its middle.</summary>
    private static BimDocument House(int joists = 0)
    {
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        var type = document.TypesOf<BeamType>().First(each => each.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var timber = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber);
        document.Add(new Beam { TypeId = type.Id, LevelId = document.Levels[1].Id, Start = new Point2D(0, 4000), End = new Point2D(10000, 4000) });
        for (var i = 0; i < joists; i++)
            document.Add(new Beam { TypeId = timber.Id, LevelId = document.Levels[1].Id, Start = new Point2D(1000 + i * 400, 0), End = new Point2D(1000 + i * 400, 4000), Usage = BeamUsage.Joist });
        return document;
    }

    [Fact]
    public void WhatTheyComeToAndWhatTheyLeaveOutComeFirst() => OnUiThread(() =>
    {
        var report = Report(House());
        var pages = report.Drawings().Select(Words).ToList();
        var front = pages[0];
        foreach (var said in new[] { "StructuralCalculations", "Whattheycometo", "Whatthesecalculationsdonotcover", "Jointspastthosesetouthere", "Statusofthesecalculations", "Theyhavenotbeenreviewed", "Checkedby", "Approvedby", "BIMDesigner1.2.3", "5November2026" })
            Assert.Contains(said, front);

        // It stands for no more than it has: taken ground, not investigated.
        Assert.Contains("taken,notinvestigated", front);

        // Each kind of check it counted, named for what it counted; nothing said of what it did not: this house has no raft.
        Assert.Contains("Members,deflection", front);
        Assert.DoesNotContain("Groundunderplates", front);
        return 0;
    });

    [Fact]
    public void EachPartIsThereAndEveryPageNumbered() => OnUiThread(() =>
    {
        var document = House();
        var report = Report(document);
        Assert.True(report.PageCount >= 4, $"{report.PageCount} pages");
        var pages = report.Drawings().Select(Words).ToList();
        var all = string.Concat(pages);
        foreach (var said in new[] { "Keyplans", "Basisofthedesign", "Whatwastaken", "Loads", "Combinations", "Howtheloadsandreactionsbalance", "Sway", "Members", "Walls", "Deflection", "Foundations", "Reactionsatthesupports" })
            Assert.Contains(said, all);

        // Each wall by name, its checks by clause, and the codes they come from.
        Assert.Contains("W1", all);
        Assert.Contains("BSEN1996-1-1", all);
        Assert.Contains("6.1.2.2", all);
        Assert.Contains("5.5.1.4", all);
        for (var page = 0; page < pages.Count; page++) Assert.Contains($"Page{page + 1}of{pages.Count}", pages[page]);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}.pdf");
        try
        {
            CalculationReport.ToPdf(report, path);
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
        }
        finally
        {
            File.Delete(path);
        }

        return 0;
    });

    [Fact]
    public void WhatFailsIsSaidOnItsFirstPage() => OnUiThread(() =>
    {
        // Thin columns under a concrete floor buckle: the front page has to say so before anything else.
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var thin = new ColumnType("Thin", ColumnShape.HollowRectangular, 60, 60) { Structural = true, StructuralMaterial = BeamMaterial.Steel, WallThickness = 3 };
        document.AddType(thin);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var columns = corners.Select(corner => new Column { TypeId = thin.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(each => each.Name.StartsWith("Floor - Screed", StringComparison.Ordinal)).Id, LevelId = first.Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        foreach (var column in columns) document.Add(Footings.Fit(document, column));

        var front = Words(Report(document).Drawings()[0]);
        Assert.Contains("Thestructurebucklesbeforeitreachesitsloads", front);
        return 0;
    });

    [Fact]
    public void RepeatedMembersAreSetOutOnce() => OnUiThread(() =>
    {
        var report = Report(House(joists: 12));
        var all = string.Concat(report.Drawings().Select(Words));
        Assert.Contains("theworstof12", all);
        return 0;
    });

    [Fact]
    public void TheFileNameSaysWhatItIs()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Name = "Mill Lane: House";
        Assert.Equal("Mill Lane House - Structural Calculations.pdf", CalculationReport.SuggestedFileName(document));
    }

    [Fact]
    public void TheEngineersOwnNotesAndWhoCheckedThemAreInThem() => OnUiThread(() =>
    {
        // Two paragraphs of the engineer's, the checker named, and a note on the beam and on a wall in their Comments.
        var document = House();
        var project = document.ProjectInformation;
        project.EngineerNotes = "Ground taken as firm clay, to be confirmed\nby a trial pit before the footings are dug.\n\nTimber C24 throughout.";
        project.CheckedBy = "A Checker";
        document.All<Beam>().Single().Comments = "Bearing on the padstone to be seen on site";
        document.Walls.First().Comments = "Lintels by others";
        document.Elements.First(element => element.Category == BIMDesigner.Core.Elements.BuiltInCategory.StructuralFoundations).Comments = "Stepped down to the trial pit's clay";
        var pages = Report(document).Drawings().Select(Words).ToList();
        var all = string.Concat(pages);

        // Its paragraphs as written, a line break inside one run on; the checker in the status table on the front page.
        foreach (var said in new[] { "Engineer'snotes", "Groundtakenasfirmclay,tobeconfirmedbyatrialpitbeforethefootingsaredug.", "TimberC24throughout.", "Notesonmembers,walls,slabsandfootings", "Bearingonthepadstonetobeseenonsite", "Lintelsbyothers", "Steppeddowntothetrialpit'sclay", "Engineer'snote" })
            Assert.Contains(said, all);
        Assert.Contains("CheckedbyAChecker", pages[0]);

        // Kept with the project.
        var back = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document)).ProjectInformation;
        Assert.Equal((project.EngineerNotes, "A Checker", string.Empty), (back.EngineerNotes, back.CheckedBy, back.ApprovedBy));

        // Without any, no such part.
        var plain = string.Concat(Report(House()).Drawings().Select(Words));
        Assert.DoesNotContain("Engineer'snotes", plain);
        Assert.Equal(new[] { "one two", "three" }, CalculationReport.Paragraphs("one\r\ntwo\r\n\r\n\r\nthree\n"));
        return 0;
    });
}
