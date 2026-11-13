using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Sunlight in gardens and amenity areas (round 84), as BRE 209 (3.3) asks it: half of each at least with two hours of sun or more on
/// 21 March, and an existing one not left with less than 0.8 times the area it had before the new work.
/// </summary>
[Collection("Wpf")]
public class AmenitySunlightTests
{
    private static GroundRegion Lawn(BimDocument document, double x0, double y0, double x1, double y1, GroundSurface surface = GroundSurface.Lawn)
    {
        var region = new GroundRegion { LevelId = document.Levels[0].Id, Surface = surface };
        region.SetBoundary(new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) });
        document.Add(region);
        return region;
    }

    /// <summary>Four walls 15 m high round a 4 m square from (0, 0).</summary>
    private static List<Wall> Courtyard(BimDocument document)
    {
        var type = document.TypesOf<WallType>().First();
        var corners = new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 4000), new Point2D(0, 4000) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 15000 };
            document.Add(wall);
            walls.Add(wall);
        }

        return walls;
    }

    [Fact]
    public void AnOpenLawnHasTheWholeDaysSunAndACourtyardsNone()
    {
        // Open: every point has the equinox's day - sunrise to sunset, about twelve hours.
        var document = BimDocument.CreateDefault();
        var open = Lawn(document, 10000, 10000, 15000, 15000);
        var date = new DateOnly(2026, 3, 21);
        var garden = Assert.Single(AmenitySun.Assess(document, date));
        Assert.All(garden.Points, point => Assert.InRange(point.Hours, 11.8, 12.4));
        Assert.Equal(1.0, garden.Share);
        Assert.True(garden.Meets);
        Assert.Null(garden.Before);

        // Walled in 15 m high on a 4 m square: the sun, at 38.5° at noon at the latitude, never clears walls that need 75° from the middle.
        var courtyard = BimDocument.CreateDefault();
        var lawn = Lawn(courtyard, 400, 400, 3600, 3600);
        Courtyard(courtyard);
        var walled = Assert.Single(AmenitySun.Assess(courtyard, date));
        Assert.True(walled.Share < 0.05, $"{walled.Share:P0}");
        Assert.False(walled.Meets);
        _ = open;
        _ = lawn;
    }

    [Fact]
    public void AnExistingGardenKeepsFourFifthsOfItsSunnyArea()
    {
        // An existing lawn, and new walls round it: before, all of it in sun; after, none - less than 0.8 times.
        var document = BimDocument.CreateDefault();
        var lawn = Lawn(document, 400, 400, 3600, 3600);
        lawn.PhaseCreated = DesignPhase.Existing;
        foreach (var wall in Courtyard(document)) wall.PhaseCreated = DesignPhase.New;
        var garden = Assert.Single(AmenitySun.Assess(document, new DateOnly(2026, 3, 21)));
        Assert.Equal(1.0, garden.Before);
        Assert.False(garden.Kept);

        // A new garden is asked only its own half.
        lawn.PhaseCreated = DesignPhase.New;
        Assert.Null(Assert.Single(AmenitySun.Assess(document, new DateOnly(2026, 3, 21))).Before);
    }

    [Fact]
    public void LawnsAndPlantingAreAmenitySpaceAndAnyRegionCanBeSaidToBe()
    {
        var document = BimDocument.CreateDefault();
        var lawn = Lawn(document, 0, 0, 2000, 2000);
        var patio = Lawn(document, 3000, 0, 5000, 2000, GroundSurface.Paving);
        Assert.True(AmenitySun.IsAmenity(lawn));
        Assert.False(AmenitySun.IsAmenity(patio));
        var said = patio.GetInstanceParameters(document).Single(parameter => parameter.Name == "Amenity Space (BRE 209)");
        Assert.Equal(false, said.Value);
        Assert.True(said.TrySet(true));
        Assert.True(AmenitySun.IsAmenity(patio));
        Assert.Equal(2, AmenitySun.Assess(document, new DateOnly(2026, 3, 21)).Count);

        // Kept in the file; the daylight's result carries them.
        Assert.True(ProjectFile.FromJson(ProjectFile.ToJson(document)).Find<GroundRegion>(patio.Id)!.Amenity);
        Assert.Equal(2, Daylight.Assess(document, document.ProjectInformation.Daylight).Gardens.Count);
    }
}
