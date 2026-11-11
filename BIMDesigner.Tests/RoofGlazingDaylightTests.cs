using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// Round 62's leftovers: daylight and sun through roof glazing - sloped glazing and roof windows - the glazing bars'
/// own weight in the loads; and Purge Unused offering drafting views and saved 3D views on no sheet.
/// </summary>
public class RoofGlazingDaylightTests
{
    /// <summary>A 10 by 8 m living room under a 40° hipped roof, with no windows in its walls.</summary>
    private static (BimDocument Document, Roof Roof, Room Room) UnderTheRoof()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        // Hipped all round, so nothing is open between the walls' tops and the roof: a gable's triangle would let the sky in.
        roof.SetEdges(Enumerable.Range(0, 4).Select(_ => new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }));
        var level = document.Levels[0].Id;
        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Screed on Slab 250mm").Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(floor);
        var room = new Room { LevelId = level, Location = new Point2D(5000, 4000), Name = "Living Room", Number = "1" };
        document.Add(room);
        return (document, roof, room);
    }

    private static RoomDaylight Lit(BimDocument document, Room room) =>
        Daylight.Assess(document, new DaylightSettings { Quality = DaylightQuality.Draft }, new[] { room }).Rooms.Single();

    [Fact]
    public void SlopedGlazingLetsInDaylightAndTheSunAllDay()
    {
        // Under slate and no window: dark, and no sun.
        var (document, roof, room) = UnderTheRoof();
        var dark = Lit(document, room);
        Assert.True(dark.Median < 0.05, $"{dark.Median:0.000}%");
        Assert.Equal((0, 0.0), (dark.Windows, dark.SunHours));

        // Glazed all over: a point under each face's glass, and the sun whenever it is above the least altitude it
        // counts from. On 21 March at London's 51.5° that is 16.75 - 0.699 (51.5 - 43.875) = 11.4°, which the sun
        // passes at an hour angle of acos(sin 11.4° / cos 51.5°) = 71.5° either side of noon: 9.5 hours.
        roof.TypeId = document.TypesOf<RoofType>().Single(type => type.SlopedGlazing).Id;
        var glazed = Lit(document, room);
        Assert.Equal(4, glazed.Windows);
        Assert.InRange(glazed.SunHours, 9.0, 9.8);
        Assert.Equal(DaylightLevel.High, glazed.SunLevel);

        // The overcast sky through it: far more than any wall window gives - the room meets the annex's 150 lux many times over.
        Assert.True(glazed.Median > 10, $"{glazed.Median:0.0}%");
        Assert.Equal(true, glazed.MeetsAnnex);
    }

    [Fact]
    public void ARoofWindowCountsForSunlightAsAWindowInAWallDoes()
    {
        // A roof window in the slope falling south: the sun reaches the point under it for most of the day.
        var (document, roof, room) = UnderTheRoof();
        var type = document.TypesOf<RoofWindowType>().First(each => each.Name.Contains("780 x 980"));
        document.Add(new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, Location = new Point2D(5000, 2000) });
        var lit = Lit(document, room);
        Assert.Equal(1, lit.Windows);
        Assert.True(lit.SunHours >= 4, $"{lit.SunHours:0.0} h");
        // The overcast sky through it: under it, where a point sees it; one roof window over 80 m² leaves most of the floor with little.
        Assert.True(lit.Points.Max(point => point.Factor) > 0.3, $"{lit.Points.Max(point => point.Factor):0.00}%");

        // Turned to the slope falling north, it sees the sun far less.
        document.Remove(document.All<RoofWindow>().Single());
        document.Add(new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, Location = new Point2D(5000, 6000) });
        Assert.True(Lit(document, room).SunHours < lit.SunHours);
    }

    [Fact]
    public void SlopedGlazingsBarsAreWeighedWithItsGlass()
    {
        // Glass: 30 kg/m² over 28 mm. Bars: 4 kg a metre, at 1 m up the slope and 2 m across it - 1/1 + 1/2 = 1.5 m of bar a
        // square metre, 6 kg. 36 kg/m² in all, 0.353 kN/m².
        var document = BimDocument.CreateDefault();
        var glazing = document.TypesOf<RoofType>().Single(type => type.SlopedGlazing);
        Assert.Equal(30 * 9.81 / 1000, Loads.Weight(document, glazing.Structure), 6);
        Assert.Equal(6 * 9.81 / 1000, Loads.GlazingBars(glazing), 6);
        Assert.Equal(36 * 9.81 / 1000, Loads.Weight(document, glazing), 6);

        // Any other roof weighs its layers alone.
        var slate = document.TypesOf<RoofType>().First(type => !type.SlopedGlazing);
        Assert.Equal(Loads.Weight(document, slate.Structure), Loads.Weight(document, slate), 9);

        // The mass is its type's, within reason, and kept with the project.
        var mass = glazing.GetTypeParameters(document).Single(parameter => parameter.Definition == GlazedRoofParameters.BarMass);
        Assert.False(mass.TrySet(0.0));
        Assert.True(mass.TrySet(5.5));
        var reopened = Infrastructure.Serialization.ProjectFile.FromJson(Infrastructure.Serialization.ProjectFile.ToJson(document));
        Assert.Equal(5.5, reopened.TypesOf<RoofType>().Single(type => type.SlopedGlazing).GlazingBarMass);
    }

    [Fact]
    public void PurgeUnusedOffersDraftingAnd3DViewsOnNoSheetAndTakesTheirDrawingWithThem()
    {
        var document = BimDocument.CreateDefault();
        var detail = new DraftingView { Name = "Eaves Detail" };
        document.Add(detail);
        var line = new DetailLine { LevelId = detail.Id, Start = new Point2D(0, 0), End = new Point2D(1000, 0) };
        document.Add(line);
        var placedDetail = new DraftingView { Name = "Sill Detail" };
        document.Add(placedDetail);
        var (camera, placedCamera) = (new View3D { Name = "Camera 1" }, new View3D { Name = "Camera 2" });
        document.Add(camera);
        document.Add(placedCamera);
        var sheet = new Sheet { Number = "A501", Name = "Details" };
        sheet.Add(new Viewport { View = Detailing.Reference(placedDetail) });
        sheet.Add(new Viewport { View = Views3D.Reference(document, placedCamera) });
        document.Add(sheet);

        // Those on no sheet, and never the {3D} view.
        var views = Purging.FindViews(document);
        Assert.Equal(new Core.Elements.Element[] { detail, camera }, views.Views);
        Assert.DoesNotContain(document.Default3D, views.Views);

        // Purged with what is drawn in them, as one step; undone, back.
        var command = Purging.PurgeViews(document, Array.Empty<Core.Schedules.ScheduleDefinition>(), Array.Empty<ParameterFilter>(), views.Views);
        command.Redo();
        Assert.Null(document.Find<DraftingView>(detail.Id));
        Assert.Null(document.Find<DetailLine>(line.Id));
        Assert.Null(document.Find<View3D>(camera.Id));
        Assert.NotNull(document.Find<View3D>(placedCamera.Id));
        command.Undo();
        Assert.NotNull(document.Find<DetailLine>(line.Id));
        Assert.NotNull(document.Find<View3D>(camera.Id));
    }
}
