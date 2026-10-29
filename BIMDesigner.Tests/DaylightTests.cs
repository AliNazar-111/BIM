using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// Daylight by BS EN 17037: daylight factors worked by tracing the overcast sky through the model, set against
/// the standard's levels and the UK annex's targets, and each room's hours of sun on 21 March.
/// </summary>
[Collection("Wpf")]
public class DaylightTests
{
    [Fact]
    public void TheOpenSkyGivesADaylightFactorOfAHundred()
    {
        // With nothing over it, a point sees the whole overcast sky: what the ground outside does.
        var scene = Daylight.SceneOf(BimDocument.CreateDefault());
        Assert.InRange(Daylight.FactorAt(scene, new Point3D(0, 0, 1000), 1024, 7), 99, 101);
    }

    [Fact]
    public void TheLevelsAndTargetsAreBsEn17037s()
    {
        // London's 14,100 lux: 300 and 100 lux are 2.1% and 0.7%; the UK annex's 100, 150 and 200 lux 0.7%, 1.1% and 1.4%.
        Assert.Equal(DaylightLevel.Minimum, Daylight.LevelOf(2.2, 0.8, Daylight.London));
        Assert.Equal(DaylightLevel.Below, Daylight.LevelOf(2.0, 0.8, Daylight.London));
        Assert.Equal(DaylightLevel.Medium, Daylight.LevelOf(3.6, 2.2, Daylight.London));
        Assert.Equal(DaylightLevel.High, Daylight.LevelOf(5.4, 3.6, Daylight.London));
        Assert.Equal((0.7, 1.1, 1.4), (Math.Round(100 / Daylight.London * 100, 1), Math.Round(150 / Daylight.London * 100, 1), Math.Round(200 / Daylight.London * 100, 1)));
        Assert.Equal((100.0, 150.0, 200.0), (Daylight.AnnexTarget(LitRoom.Bedroom)!.Value, Daylight.AnnexTarget(LitRoom.Living)!.Value, Daylight.AnnexTarget(LitRoom.Kitchen)!.Value));
        Assert.Null(Daylight.AnnexTarget(LitRoom.Bathroom));

        // The value half the points reach, and the one 95% of them reach.
        var factors = Enumerable.Range(1, 20).Select(i => (double)i).ToList();
        Assert.Equal(11, Daylight.Reached(factors, 0.5));
        Assert.Equal(2, Daylight.Reached(factors, 0.95));

        // Sunlight: 1.5, 3 and 4 hours; the least altitude on the line through Table D.1's reported values.
        Assert.Equal(DaylightLevel.Minimum, Daylight.SunLevelOf(1.5));
        Assert.Equal(DaylightLevel.High, Daylight.SunLevelOf(4.2));
        Assert.InRange(Daylight.LeastAltitude(52.4), 10.5, 11.5);
        Assert.InRange(Daylight.LeastAltitude(35.2), 22.5, 23.5);
    }

    /// <summary>A living room and a bedroom side by side, each with a casement in its south wall, the living room one in its north too; flat-roofed.</summary>
    private static (BimDocument Document, Room Living, Room Bedroom) Rooms(bool northWindow)
    {
        var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "Living Room", "Bedroom 1");
        var exterior = document.Walls.Take(4).ToList();
        var south = exterior.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        var north = exterior.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y > 0);
        var casement = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200");
        document.Add(new BimWindow { TypeId = casement.Id, LevelId = rooms[0].LevelId, HostWallId = south.Id, DistanceAlongWall = Math.Abs(rooms[0].Location.X - south.Start.X), SillHeight = 900 });
        if (northWindow)
            document.Add(new BimWindow { TypeId = casement.Id, LevelId = rooms[1].LevelId, HostWallId = north.Id, DistanceAlongWall = Math.Abs(rooms[1].Location.X - north.Start.X), SillHeight = 900 });
        return (document, rooms[0], rooms[1]);
    }

    [Fact]
    public void ARoomsDaylightFallsAwayFromItsWindowAndIsGradedByTheAnnex()
    {
        var (document, living, bedroom) = Rooms(northWindow: true);
        var result = Daylight.Assess(document, new DaylightSettings { Quality = DaylightQuality.Draft });
        Assert.Equal(2, result.Rooms.Count);
        var lit = result.Rooms.First(room => ReferenceEquals(room.Room, living));
        Assert.Equal(150, lit.Target);
        Assert.Equal(LitRoom.Living, lit.Kind);

        // Its points 0.3 m in from the walls at 0.85 m; nearer the window, more light.
        Assert.All(lit.Points, point => Assert.InRange(point.At.Y, 300 - 1, 2700 + 1));
        var near = lit.Points.Where(point => point.At.Y < 1200).Average(point => point.Factor);
        var far = lit.Points.Where(point => point.At.Y > 1800).Average(point => point.Factor);
        Assert.True(near > far * 1.5, $"near {near:0.00}% far {far:0.00}%");
        Assert.InRange(lit.Median, 0.3, 8);
        Assert.True(lit.Least <= lit.Median);
        Assert.Equal(lit.Median >= 150 / Daylight.London * 100, lit.MeetsAnnex);

        // On 21 March in London, the south window has the sun for hours, the north one none.
        Assert.True(lit.SunHours >= 4, $"south {lit.SunHours:0.0} h");
        Assert.Equal(DaylightLevel.High, lit.SunLevel);
        var dark = result.Rooms.First(room => ReferenceEquals(room.Room, bedroom));
        Assert.True(dark.SunHours < 0.5, $"north {dark.SunHours:0.0} h");
        Assert.True(result.Sunlit);
        Assert.Equal(1, dark.Windows);
    }

    [Fact]
    public void ARoomWithNoWindowHasNoDaylightAndTheSettingsAreKept()
    {
        var (document, _, bedroom) = Rooms(northWindow: false);
        var dark = Daylight.Assess(document, new DaylightSettings { Quality = DaylightQuality.Draft }, new[] { bedroom }).Rooms.Single();
        Assert.True(dark.Median < 0.05, $"{dark.Median:0.000}%");
        Assert.Equal(DaylightLevel.Below, dark.Level);
        Assert.Equal(0, dark.SunHours);
        Assert.Equal(false, dark.MeetsAnnex);

        // A US project: London's figure until the site's own is set.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Contains("no American city", Daylight.Outside(document, document.ProjectInformation.Daylight).From);

        var settings = document.ProjectInformation.Daylight;
        (settings.SunlightDate, settings.OutsideLight, settings.Quality) = (new DateOnly(2026, 2, 1), 17_500, DaylightQuality.Fine);
        var path = Path.Combine(Path.GetTempPath(), $"daylight-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var kept = ProjectFile.Load(path).ProjectInformation.Daylight;
            Assert.Equal((new DateOnly(2026, 2, 1), 17_500.0, DaylightQuality.Fine), (kept.SunlightDate, kept.OutsideLight, kept.Quality));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
