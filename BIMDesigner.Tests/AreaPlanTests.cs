using System.Text;
using System.Text.RegularExpressions;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Export;

namespace BIMDesigner.Tests;

/// <summary>
/// Area plans: each storey measured as the RICS Code of Measuring Practice measures it - gross
/// external to the outside faces and a party wall's middle, gross internal to the inside faces,
/// net internal by the rooms - and a US home's finished area by ANSI Z765, scheduled and on a sheet.
/// </summary>
[Collection("Wpf")]
public class AreaPlanTests
{
    private static WallType Type(BimDocument document, string name) => document.TypesOf<WallType>().First(type => type.Name.StartsWith(name, StringComparison.Ordinal));

    /// <summary>Four walls round a box, centrelines from (x, y), on a storey.</summary>
    private static void Box(BimDocument document, Guid level, WallType type, double x, double y, double width, double depth, WallType? east = null)
    {
        var corners = new[] { new Point2D(x, y), new Point2D(x + width, y), new Point2D(x + width, y + depth), new Point2D(x, y + depth) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = (i == 1 && east is not null ? east : type).Id, LevelId = level, UnconnectedHeight = 2700 });
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y)
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    [Fact]
    public void AStoreyIsMeasuredGrossToItsOutsideFacesAndInternallyToItsInsideFacesWhateverItsWallsAreTypedAs()
    {
        foreach (var name in new[] { "Exterior - Brick on Block", "Generic - 200" })
        {
            var document = BimDocument.CreateDefault();
            var level = document.Levels[0].Id;
            var type = Type(document, name);
            var w = type.Width;
            Box(document, level, type, 0, 0, 10000, 8000);

            // A partition across it is in both: GIA takes in the walls between rooms (2.1).
            document.Add(new Wall { Start = new Point2D(6000, 0), End = new Point2D(6000, 8000), TypeId = Type(document, "Interior - Partition").Id, LevelId = level, UnconnectedHeight = 2700 });
            Room(document, level, "Living", 3000, 4000);

            var gea = AreaPlans.Measure(document, level, AreaScheme.GrossExternal);
            var gia = AreaPlans.Measure(document, level, AreaScheme.GrossInternal);
            Assert.Equal((10000 + w) * (8000 + w), gea.Area, 0);
            Assert.Equal((10000 - w) * (8000 - w), gia.Area, 0);
            Assert.Single(gea.Outlines);
        }
    }

    [Fact]
    public void APartyWallIsMeasuredToItsMiddleFromOutsideAndToItsFaceInside()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var outside = Type(document, "Exterior - Brick on Block");
        var party = Type(document, "Party Wall");
        Box(document, level, outside, 0, 0, 10000, 8000, east: party);
        Room(document, level, "Living", 3000, 4000);

        var (w, p) = (outside.Width, party.Width);
        Assert.Equal((10000 + w / 2) * (8000 + w), AreaPlans.Measure(document, level, AreaScheme.GrossExternal).Area, 0);
        Assert.Equal((10000 - w / 2 - p / 2) * (8000 - w), AreaPlans.Measure(document, level, AreaScheme.GrossInternal).Area, 0);
    }

    [Fact]
    public void TheNetInternalAreaIsTheRoomsThatCountWithThePartitionsBetweenThem()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var outside = Type(document, "Exterior - Brick on Block");
        var partition = Type(document, "Interior - Partition");
        Box(document, level, outside, 0, 0, 10000, 8000);
        document.Add(new Wall { Start = new Point2D(6000, 0), End = new Point2D(6000, 8000), TypeId = partition.Id, LevelId = level, UnconnectedHeight = 2700 });
        document.Add(new Wall { Start = new Point2D(0, 4000), End = new Point2D(6000, 4000), TypeId = partition.Id, LevelId = level, UnconnectedHeight = 2700 });
        Room(document, level, "Living", 3000, 6000);
        Room(document, level, "Dining", 3000, 2000);
        Room(document, level, "WC", 8000, 4000);

        var (w, p) = (outside.Width, partition.Width);
        var across = 6000 - w / 2 - p / 2;
        var room = across * (4000 - w / 2 - p / 2);
        var nia = AreaPlans.Measure(document, level, AreaScheme.NetInternal);
        Assert.InRange(nia.Area, 2 * room + across * p - 2e4, 2 * room + across * p + 2e4);
        Assert.Equal(2, nia.Outlines.Count);
        Assert.Contains(nia.Parts, part => part.Kind == AreaPartKind.Excluded && part.Said.StartsWith("WC: toilets and bathrooms (NIA 3.13)", StringComparison.Ordinal));
        Assert.Contains(nia.Parts, part => part.Kind == AreaPartKind.Added && part.Said.Contains("NIA 3.9"));
    }

    [Fact]
    public void AVoidOverTheStoreyBelowIsLeftOutAndAGardenStoreStandingApart()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var outside = Type(document, "Exterior - Brick on Block");
        var w = outside.Width;
        Box(document, first, outside, 0, 0, 10000, 8000);
        var floor = new Floor { LevelId = first, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        floor.SetHoles(new[] { new[] { new Point2D(2000, 2000), new Point2D(4000, 2000), new Point2D(4000, 4000), new Point2D(2000, 4000) } });
        document.Add(floor);

        var gia = AreaPlans.Measure(document, first, AreaScheme.GrossInternal);
        Assert.Equal((10000 - w) * (8000 - w) - 4e6, gia.Area, 0);
        Assert.Contains(gia.Parts, part => part.Said.Contains("GIA 2.3, 2.21"));

        // A garden store on its own on the ground: no part of either gross area.
        Box(document, ground, outside, 0, 0, 10000, 8000);
        Room(document, ground, "Kitchen", 3000, 4000);
        Box(document, ground, outside, 15000, 0, 2000, 2000);
        Room(document, ground, "Garden Store", 16000, 1000);
        var gea = AreaPlans.Measure(document, ground, AreaScheme.GrossExternal);
        Assert.Equal((10000 + w) * (8000 + w), gea.Area, 0);
        Assert.Contains(gea.Parts, part => part.Said.Contains("GEA 1.20"));
    }

    [Fact]
    public void AUsHomesFinishedAreaLeavesOutItsGarageAndPutsItsBasementBelowGrade()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Equal(new[] { AreaScheme.GrossExternal, AreaScheme.Finished }, AreaPlans.Schemes(document));

        var level = document.Levels[0].Id;
        var outside = Type(document, "Exterior - Brick on Block");
        var partition = Type(document, "Interior - Partition");
        Box(document, level, outside, 0, 0, 12000, 8000);
        document.Add(new Wall { Start = new Point2D(8000, 0), End = new Point2D(8000, 8000), TypeId = partition.Id, LevelId = level, UnconnectedHeight = 2700 });
        Room(document, level, "Family Room", 4000, 4000);
        var garage = Room(document, level, "Garage", 10000, 4000);

        var basement = new Level { Name = "Basement", Elevation = -2800 };
        document.Levels.Add(basement);
        Box(document, basement.Id, outside, 0, 0, 12000, 8000);
        Room(document, basement.Id, "Rec Room", 4000, 4000);

        var gross = AreaPlans.Measure(document, level, AreaScheme.GrossExternal).Area;
        var finished = AreaPlans.Measure(document, level, AreaScheme.Finished);
        Assert.Equal(gross - garage.GetBoundary(document).Area, finished.Area, 0);
        Assert.False(finished.BelowGrade);
        Assert.True(AreaPlans.Measure(document, basement.Id, AreaScheme.Finished).BelowGrade);

        // Scheduled above grade and below it apart, to the whole square foot, with the standard it is measured by.
        var table = AreaPlans.Table(document, AreaPlans.Schedule(AreaScheme.Finished));
        var cells = table.Lines.Select(line => string.Join("|", line.Cells)).ToList();
        Assert.Contains(cells, cell => cell == "Above grade");
        Assert.Contains(cells, cell => cell == "Below grade");
        Assert.Contains(table.Lines, line => line.Kind == ScheduleLineKind.Total && Regex.IsMatch(line.Cells[1], @"^[\d,]+ sq ft$"));
        Assert.Contains(cells, cell => cell.Contains("ANSI Z765-2021"));
    }

    [Fact]
    public void AnAreaPlanIsAViewOfItsStoreyOnASheetWithItsSchedule()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0];
        var outside = Type(document, "Exterior - Brick on Block");
        Box(document, level.Id, outside, 0, 0, 10000, 8000);
        Room(document, level.Id, "Living", 5000, 4000);

        var view = AreaPlans.View(level.Id, AreaScheme.GrossInternal);
        Assert.Equal(AreaScheme.GrossInternal, AreaPlans.Of(view));
        Assert.Null(AreaPlans.Of(ViewReference.FloorPlan(level.Id)));
        Assert.Equal(Core.Mep.Discipline.Architectural, Core.Mep.ServicesPlans.Of(view));
        Assert.Equal($"{level.Name} - Gross Internal Area (GIA)", view.TitleIn(document));

        var sheet = new Sheet { Number = "A-901", Name = "Areas", PaperSize = PaperSize.A3 };
        sheet.Add(new Viewport { View = view, Centre = new Point2D(150, 150), Scale = new ViewScale(200) });
        sheet.Add(new Viewport { View = AreaPlans.Schedule(AreaScheme.GrossInternal), Centre = new Point2D(330, 100), Scale = new ViewScale(100), ShowTitle = false });
        document.Add(sheet);

        var words = Words(SheetExport.Page(document, sheet).Content!);
        var area = Regex.Replace(AreaPlans.Said(AreaScheme.GrossInternal, AreaPlans.Measure(document, level.Id, AreaScheme.GrossInternal).Area), @"\s+", string.Empty);
        Assert.Contains($"{Regex.Replace(level.Name, @"\s+", string.Empty)}GIA{area}", words);
        Assert.Contains("AreaSchedule-GrossInternalArea(GIA)", words);
        Assert.Contains("RICSCodeofMeasuringPractice", words);
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
