using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using BIMDesigner.UI.Export;

namespace BIMDesigner.Tests;

/// <summary>
/// Drawing in 2D: detail lines and filled regions on a plan or in a drafting view, their hatching
/// cut to their outline whatever its shape; drafting views made at a detail's scale, drawn in with
/// only the tools that draw detail, kept with the project and printed on a sheet.
/// </summary>
[Collection("Wpf")]
public class DetailingTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void HatchingIsCutToTheOutlineWhateverItsShape()
    {
        // A 100 by 100 square, hatched level every 10: nine lines inside it, each right across.
        var square = new[] { new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 100), new Point2D(0, 100) };
        var level = Detailing.Hatch(square, 0, 10);
        Assert.InRange(level.Count, 9, 11);
        Assert.All(level, line => Assert.Equal(100, line.From.DistanceTo(line.To), 6));

        // An L: no line runs across the notch cut out of it.
        var ell = new[] { new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 40), new Point2D(40, 40), new Point2D(40, 100), new Point2D(0, 100) };
        var hatched = Detailing.Hatch(ell, 45, 5);
        Assert.NotEmpty(hatched);
        Assert.All(hatched, line => Assert.True(Polygon2D.Contains(ell, line.From.MidpointTo(line.To)), $"{line.From}-{line.To} crosses the notch"));

        // Crosshatch is two sets; a solid fill none.
        Assert.Equal(2, Detailing.Lines(FillPattern.Crosshatch).Count);
        Assert.Empty(Detailing.Lines(FillPattern.SolidBlack));
    }

    [Fact]
    public void DetailLinesRegionsAndDraftingViewsAreKeptWithTheProject()
    {
        var document = BimDocument.CreateDefault();
        var history = new UndoStack();
        var (view, make) = Detailing.Make(document, "Eaves Detail");
        history.Execute(make);
        Assert.Equal(Detailing.DraftingScale, document.ViewSettings.ScaleOf(Detailing.Reference(view)));
        Assert.True(Detailing.Reference(view).ExistsIn(document));
        Assert.Equal("Eaves Detail", Detailing.Reference(view).TitleIn(document));

        var line = new DetailLine { LevelId = view.Id, Start = new Point2D(0, 0), End = new Point2D(300, 0), Style = DetailLineStyle.Wide };
        var region = new FilledRegion { LevelId = view.Id, Pattern = FillPattern.Crosshatch, Outlined = false };
        region.SetBoundary(new[] { new Point2D(0, 0), new Point2D(300, 0), new Point2D(300, 100), new Point2D(0, 100) });
        var onPlan = new DetailLine { LevelId = document.Levels[0].Id, Start = new Point2D(0, 0), End = new Point2D(5000, 0), Style = DetailLineStyle.Hidden };
        foreach (var each in new Core.Elements.Element[] { line, region, onPlan }) document.Add(each);

        // What is drawn in it is its extent, a little beyond.
        var extent = ViewExtent.Of(document, Detailing.Reference(view));
        Assert.True(extent.MinX < 0 && extent.MaxX > 300 && extent.MaxY > 100);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = reopened.All<DraftingView>().Single();
        Assert.Equal("Eaves Detail", kept.Name);
        Assert.Equal(DetailLineStyle.Wide, reopened.All<DetailLine>().Single(each => each.Id == line.Id).Style);
        Assert.Equal(DetailLineStyle.Hidden, reopened.All<DetailLine>().Single(each => each.Id == onPlan.Id).Style);
        var keptRegion = reopened.All<FilledRegion>().Single();
        Assert.Equal((FillPattern.Crosshatch, false, 4), (keptRegion.Pattern, keptRegion.Outlined, keptRegion.Boundary.Count));
        Assert.Equal(view.Id, keptRegion.LevelId);

        // Copied, moved and mirrored like anything drawn on a plan.
        var copy = (DetailLine)ElementCopy.Clone(line)!;
        Assert.Equal((line.Start, line.End, line.Style), (copy.Start, copy.End, copy.Style));
        history.Execute(new MoveElementsCommand(new Core.Elements.Element[] { line, region }, new Vector2D(10, 20), "Move", document));
        Assert.Equal(new Point2D(10, 20), line.Start);
        Assert.Contains(new Point2D(10, 20), region.Boundary);
    }

    [Fact]
    public void ADraftingViewOnASheetPrintsItsDetail()
    {
        var document = BimDocument.CreateDefault();
        var (view, make) = Detailing.Make(document, "Sill Detail");
        new UndoStack().Execute(make);
        document.Add(new DetailLine { LevelId = view.Id, Start = new Point2D(0, 0), End = new Point2D(250, 0), Style = DetailLineStyle.Wide });
        var region = new FilledRegion { LevelId = view.Id, Pattern = FillPattern.Diagonal };
        region.SetBoundary(new[] { new Point2D(0, 0), new Point2D(250, 0), new Point2D(250, 60), new Point2D(0, 60) });
        document.Add(region);
        document.Add(new TextNote { LevelId = view.Id, Position = new Point2D(0, 120), Text = "DPC LAPPED 150" });

        var sheet = new Sheet { Number = "A-501", Name = "Details", PaperSize = PaperSize.A3 };
        sheet.Add(new Viewport { View = Detailing.Reference(view), Centre = new Point2D(150, 150), Scale = new ViewScale(5) });
        document.Add(sheet);
        Assert.True(sheet.IsWellPlaced(document, sheet.Viewports[0]));

        var page = SheetExport.Page(document, sheet);
        var words = Words(page.Content!);
        Assert.Contains("DPCLAPPED150", words);
        Assert.Contains("SillDetail", words);
        Assert.True(Lines(page.Content!) > 20, "The diagonal hatching is drawn as lines");
    }

    [Fact]
    public void ADraftingViewIsDrawnInWithTheDetailToolsOnly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var window = new MainWindow();
                typeof(MainWindow).GetMethod("LoadDocument", Hidden)!.Invoke(window, new object?[] { BimDocument.CreateDefault(), null, false });
                var plan = (PlanView)typeof(MainWindow).GetField("Plan", Hidden)!.GetValue(window)!;
                var document = plan.Document!;
                document.Add(new Core.Datums.Grid { Start = new Point2D(-5000, 0), End = new Point2D(5000, 0), Name = "A" });

                var view = (DraftingView)typeof(MainWindow).GetMethod("NewDraftingView", Hidden)!.Invoke(window, new object?[] { "Ridge Detail" })!;
                Assert.True(plan.Drafting);
                Assert.Equal("Drafting View: Ridge Detail", ((TextBlock)typeof(MainWindow).GetField("PlanTitle", Hidden)!.GetValue(window)!).Text);

                // Lines drawn here go into it, a length typed among them; a region clicked round.
                ((RadioButton)typeof(MainWindow).GetField("DetailLineTool", Hidden)!.GetValue(window)!).IsChecked = true;
                Assert.Equal(PlanTool.DetailLine, plan.ActiveTool);
                plan.ClickDetailLine(new Point2D(0, 0));
                Assert.True(plan.PlaceTypedLength("200", toward: new Point2D(150, 3)));
                var drawn = Assert.Single(document.All<DetailLine>());
                Assert.Equal((view.Id, 200.0), (drawn.LevelId, Math.Round(drawn.Length, 6)));
                plan.CancelPendingOperation();

                // Close in, as a detail is drawn: corners a few millimetres apart are corners, not the same click twice.
                typeof(PlanView).GetProperty("PixelsPerMm")!.SetValue(plan, 4.0);
                ((RadioButton)typeof(MainWindow).GetField("FilledRegionTool", Hidden)!.GetValue(window)!).IsChecked = true;
                foreach (var corner in new[] { new Point2D(0, 0), new Point2D(200, 0), new Point2D(200, 40), new Point2D(0, 40) }) plan.ClickOutline(corner);
                Assert.True(plan.FinishOutline());
                Assert.Equal(view.Id, Assert.Single(document.All<FilledRegion>()).LevelId);

                // A tool that builds the model is turned away; the building's gridlines are not drawn here.
                ((RadioButton)typeof(MainWindow).GetField("WallTool", Hidden)!.GetValue(window)!).IsChecked = true;
                Assert.Equal(PlanTool.Select, plan.ActiveTool);
                Assert.Contains("drafting view holds 2D detail", ((TextBlock)typeof(MainWindow).GetField("StatusHint", Hidden)!.GetValue(window)!).Text);

                // Back to a storey's plan: drawn there, a line is the plan's.
                ((ComboBox)typeof(MainWindow).GetField("LevelPicker", Hidden)!.GetValue(window)!).SelectedItem = document.Levels[0];
                Assert.False(plan.Drafting);
                ((RadioButton)typeof(MainWindow).GetField("DetailLineTool", Hidden)!.GetValue(window)!).IsChecked = true;
                plan.ClickDetailLine(new Point2D(0, 0));
                plan.ClickDetailLine(new Point2D(3000, 0));
                Assert.Contains(document.All<DetailLine>(), line => line.LevelId == document.Levels[0].Id);
                plan.CancelPendingOperation();

                // A storey added while a drafting view is open leaves it open.
                window.GetType().GetMethod("OpenDrafting", Hidden)!.Invoke(window, new object?[] { view });
                typeof(MainWindow).GetMethod("AfterLevelsChanged", Hidden)!.Invoke(window, null);
                Assert.True(plan.Drafting);

                // Its Properties are its own - its name to change, not a plan's; it goes to half size from the scale bar, as a plan cannot.
                typeof(MainWindow).GetMethod("RefreshProperties", Hidden)!.Invoke(window, null);
                Assert.Equal("Drafting View", ((TextBlock)typeof(MainWindow).GetField("SelectedFamilyName", Hidden)!.GetValue(window)!).Text);
                var scales = (ComboBox)typeof(MainWindow).GetField("PlanScalePicker", Hidden)!.GetValue(window)!;
                scales.SelectedItem = scales.Items.Cast<ViewScale>().Single(scale => scale.Denominator == 2);
                Assert.Equal(2, document.ViewSettings.ScaleOf(Detailing.Reference(view)));
                ((ComboBox)typeof(MainWindow).GetField("LevelPicker", Hidden)!.GetValue(window)!).SelectedItem = document.Levels[0];
                Assert.DoesNotContain(scales.Items.Cast<ViewScale>(), scale => scale.Denominator == 2);
                window.GetType().GetMethod("OpenDrafting", Hidden)!.Invoke(window, new object?[] { view });

                // Copied, it is opened with its detail; deleted while open, the plan goes back to a storey and Undo brings it back.
                var copy = (DraftingView)typeof(MainWindow).GetMethod("DuplicateDrafting", Hidden)!.Invoke(window, new object?[] { view })!;
                Assert.Equal(("Ridge Detail Copy", copy.Id), (copy.Name, plan.ActiveLevelId));
                Assert.Equal(2, document.Elements.Count(element => element.LevelId == copy.Id));
                typeof(MainWindow).GetMethod("DeleteDrafting", Hidden)!.Invoke(window, new object?[] { copy });
                Assert.False(plan.Drafting);
                Assert.NotNull(document.FindLevel(plan.ActiveLevelId));
                Assert.DoesNotContain(document.Elements, element => element.LevelId == copy.Id);
                Assert.Equal(2, document.Elements.Count(element => element.LevelId == view.Id));
                typeof(MainWindow).GetMethod("OnUndo", Hidden)!.Invoke(window, new object?[] { null, null });
                Assert.Contains(copy, document.Elements);
                Assert.Equal(2, document.Elements.Count(element => element.LevelId == copy.Id));

                // A new one undone away while open: back to a storey too.
                window.GetType().GetMethod("NewDraftingView", Hidden)!.Invoke(window, new object?[] { "Verge Detail" });
                Assert.True(plan.Drafting);
                typeof(MainWindow).GetMethod("OnUndo", Hidden)!.Invoke(window, new object?[] { null, null });
                Assert.False(plan.Drafting);
                Assert.NotNull(document.FindLevel(plan.ActiveLevelId));
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The window failed on the UI thread.", failure);
    }

    private static int Lines(System.Windows.Media.Drawing drawing) => drawing switch
    {
        System.Windows.Media.DrawingGroup group => group.Children.Sum(Lines),
        System.Windows.Media.GeometryDrawing { Geometry: System.Windows.Media.LineGeometry } => 1,
        _ => 0
    };

    private static string Words(System.Windows.Media.Drawing drawing)
    {
        var said = new StringBuilder();
        void Walk(System.Windows.Media.Drawing each)
        {
            switch (each)
            {
                case System.Windows.Media.DrawingGroup group:
                    foreach (var child in group.Children) Walk(child);
                    break;
                case System.Windows.Media.GlyphRunDrawing glyphs when glyphs.GlyphRun?.Characters is { } characters:
                    said.Append(characters.ToArray());
                    break;
            }
        }

        Walk(drawing);
        return Regex.Replace(said.ToString(), @"\s+", string.Empty);
    }
}

/// <summary>
/// Rooms coloured on a plan by what they are - each department, name or size band its own colour,
/// the same on every storey - and a legend of them, a swatch beside each, put on a sheet.
/// </summary>
[Collection("Wpf")]
public class RoomColourTests
{
    /// <summary>Two storeys of rooms in three departments: a box of four rooms on each.</summary>
    private static BimDocument Offices()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(each => each.Function == WallFunction.Exterior);
        var partition = document.TypesOf<WallType>().First(each => each.Function == WallFunction.Interior);
        foreach (var level in document.Levels.Take(2))
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
            for (var i = 0; i < 4; i++)
                document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level.Id, UnconnectedHeight = 3000 });
            document.Add(new Wall { Start = new Point2D(6000, 0), End = new Point2D(6000, 8000), TypeId = partition.Id, LevelId = level.Id, UnconnectedHeight = 3000 });
            document.Add(new Room { LevelId = level.Id, Location = new Point2D(3000, 4000), Name = "Office", Department = level == document.Levels[0] ? "Sales" : "Design" });
            document.Add(new Room { LevelId = level.Id, Location = new Point2D(9000, 4000), Name = "Store", Department = "Facilities" });
        }

        return document;
    }

    [Fact]
    public void EachValueHasItsColourOnEveryStoreyAndItsLegend()
    {
        var document = Offices();
        var ground = ViewReference.FloorPlan(document.Levels[0].Id);
        var first = ViewReference.FloorPlan(document.Levels[1].Id);
        var history = new UndoStack();
        history.Execute(RoomColours.Set(document, ground, RoomColourBy.Department));
        history.Execute(RoomColours.Set(document, first, RoomColourBy.Department));

        Room On(int level, string name) => document.All<Room>().Single(room => room.LevelId == document.Levels[level].Id && room.Name == name);
        Assert.Equal(RoomColours.ColourOf(document, ground, On(0, "Store")), RoomColours.ColourOf(document, first, On(1, "Store")));
        Assert.NotEqual(RoomColours.ColourOf(document, ground, On(0, "Office")), RoomColours.ColourOf(document, first, On(1, "Office")));
        Assert.Null(RoomColours.ColourOf(document, BIMDesigner.Core.Mep.ServicesPlans.Plan(document.Levels[0].Id, BIMDesigner.Core.Mep.Discipline.Electrical), On(0, "Office")));

        // The ground floor's legend: its two departments, a room each, and their floor.
        var legend = RoomColours.Legend(document, ground);
        Assert.Equal(new[] { "Facilities", "Sales" }, legend.Select(entry => entry.Value));
        Assert.All(legend, entry => Assert.Equal(1, entry.Rooms));
        Assert.All(legend, entry => Assert.InRange(entry.Area / 1e6, 40, 48));

        // By size: in bands, smallest first.
        history.Execute(RoomColours.Set(document, ground, RoomColourBy.Area));
        Assert.Equal("30 m² and over", Assert.Single(RoomColours.Legend(document, ground).Select(entry => entry.Value).Distinct()));

        // Undone, by department again; kept with the project.
        history.Undo();
        Assert.Equal(RoomColourBy.Department, document.ViewSettings.ColourByOf(ground));
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(RoomColourBy.Department, reopened.ViewSettings.ColourByOf(ViewReference.FloorPlan(reopened.Levels[1].Id)));
    }

    [Fact]
    public void ALegendOnASheetShowsASwatchBesideEachValue()
    {
        var document = Offices();
        var ground = ViewReference.FloorPlan(document.Levels[0].Id);
        document.ViewSettings.SetColourBy(ground, RoomColourBy.Department);
        var legend = RoomColours.LegendOf(ground);
        Assert.True(legend.ExistsIn(document));
        Assert.Equal(ground, RoomColours.ViewOf(legend));

        var table = RoomColours.Table(document, legend);
        var rows = table.Lines.Where(line => line.Kind == Core.Schedules.ScheduleLineKind.Row).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.Backgrounds[0]));
        Assert.Equal(RoomColours.Legend(document, ground)[0].Colour, rows[0].Backgrounds[0]);

        var sheet = new Sheet { Number = "A-100", Name = "Zones", PaperSize = PaperSize.A3 };
        var viewport = new Viewport { View = legend, Centre = new Point2D(100, 200), ShowTitle = false };
        sheet.Add(viewport);
        document.Add(sheet);
        Assert.True(viewport.IsTable);
        Assert.Equal("", viewport.DetailNumber);
        var page = SheetExport.Page(document, sheet);
        Assert.Contains("ColourLegend-GroundFloor", Words(page.Content!));
        Assert.Contains("Facilities", Words(page.Content!));
    }

    private static string Words(System.Windows.Media.Drawing drawing)
    {
        var said = new StringBuilder();
        void Walk(System.Windows.Media.Drawing each)
        {
            switch (each)
            {
                case System.Windows.Media.DrawingGroup group:
                    foreach (var child in group.Children) Walk(child);
                    break;
                case System.Windows.Media.GlyphRunDrawing glyphs when glyphs.GlyphRun?.Characters is { } characters:
                    said.Append(characters.ToArray());
                    break;
            }
        }

        Walk(drawing);
        return Regex.Replace(said.ToString(), @"\s+", string.Empty);
    }
}
