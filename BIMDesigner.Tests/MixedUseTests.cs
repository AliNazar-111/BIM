using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Flats over a shop (round 121): the homes' part judged by Approved Document B Volume 1 and left out of Volume 2's walks, the rest by Volume 2,
/// and a stair serving both by Volume 1's 3.79 and 3.80.
/// </summary>
public class MixedUseTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = "Single - 900 x 2100")
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y, string dwelling = "")
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name, Dwelling = dwelling };
        document.Add(room);
        return room;
    }

    private sealed record Storey(Level Level, Wall StairWall, Door StairDoor, Door BackDoor, Room Stair, Room Corridor, Room Back);

    /// <summary>
    /// A shop 12 m by 6 m under flats, on so many storeys 3 m apart: each a stair at the west end (x 0 to 3 m), a corridor along the south side
    /// (y 0 to 2 m) entered from the stair at 1 m, and north of it - entered at x 8 m - the shop on the ground storey and a flat on each above;
    /// a door out of the stair at the ground. Plain library doors and partitions, 30 minutes.
    /// </summary>
    private static (BimDocument Document, List<Storey> Storeys) ShopWithFlats(int storeys)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        var levels = new List<Level> { document.Levels[0], document.Levels[1] };
        for (var i = 2; i < storeys; i++)
        {
            var level = new Level { Name = $"Level {i}", Elevation = 3000 * i };
            document.AddLevel(level);
            levels.Add(level);
        }

        var built = new List<Storey>();
        foreach (var level in levels.Take(storeys))
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) };
            var outside = new List<Wall>();
            for (var i = 0; i < 4; i++) outside.Add(Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick));
            if (built.Count == 0) Door(document, outside[3], 3000);
            var stairWall = Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 6000));
            var backWall = Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(12000, 2000));
            var ground = built.Count == 0;
            built.Add(new Storey(level, stairWall, Door(document, stairWall, 1000), Door(document, backWall, 5000),
                Room(document, level.Id, "Stair", 1500, 3000), Room(document, level.Id, ground ? "Corridor" : "Landing", 7500, 1000),
                Room(document, level.Id, ground ? "Shop" : "Living Room", 7500, 4000, ground ? string.Empty : $"Flat {built.Count}")));
        }

        for (var i = 0; i + 1 < built.Count; i++)
            document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = built[i].Level.Id, TopLevelId = built[i + 1].Level.Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
        return (document, built);
    }

    [Fact]
    public void TheHomesAreVolume1sAndTheShopVolume2s()
    {
        var (document, storeys) = ShopWithFlats(3);
        var (shop, flat, landing, stair) = (storeys[0].Back, storeys[1].Back, storeys[1].Corridor, storeys[1].Stair);
        Assert.True(MixedUse.Is(document));

        // The flat and the landing serving only flats are the homes'; the stair, serving both, and the shop's corridor are not.
        Assert.True(MixedUse.Home(document, flat));
        Assert.True(MixedUse.Home(document, landing));
        Assert.False(MixedUse.Home(document, stair));
        Assert.False(MixedUse.Home(document, storeys[0].Corridor));
        Assert.False(MixedUse.Home(document, shop));

        // Volume 1's common parts: the landing and the stair - not the shop, nor its corridor.
        Assert.True(MixedUse.Common(document, landing));
        Assert.True(MixedUse.Common(document, stair));
        Assert.False(MixedUse.Common(document, shop));
        Assert.False(MixedUse.Common(document, storeys[0].Corridor));
        Assert.False(MixedUse.Common(document, flat));

        // Volume 2 walks the shop, not the flats: its occupants by Table D1, and no verdict for a flat's living room.
        Assert.Null(HorizontalEscape.Verdict(document, flat));
        Assert.Null(HorizontalEscape.Verdict(document, landing));
        Assert.Contains("people", HorizontalEscape.Verdict(document, shop));
        Assert.DoesNotContain(HorizontalEscape.Problems(document), problem => problem.Room == flat || problem.Room == landing);

        // The shop's Properties: Volume 2's escape, not "Nothing wanting" from Volume 1's walk of the flats.
        Assert.Contains("people", Escape.Describe(document, shop));

        // Its fire service access by Volume 2's Section 15, the flats' own reach by Volume 1 as well.
        Assert.True(FireAccess.Volume2(document));
    }

    [Fact]
    public void AStairServingFlatsAndAShopIsSeparatedFromEachByAProtectedLobby()
    {
        // Ground and two storeys of flats over: 3.79 - a protected lobby between the stair and each occupancy at each storey.
        var (document, storeys) = ShopWithFlats(3);
        var said = MixedUse.Problems(document).Select(problem => (problem.Room, problem.Said)).ToList();
        Assert.Equal(3, said.Count);
        Assert.Contains((storeys[0].Stair, "on the Ground Floor it opens straight into the Corridor, serving the other use - Approved Document B (3.79) lets a stair serve flats and other uses in a building of three storeys at most above the ground storey, separated from each occupancy at each storey by a protected lobby, REI 30"), said);
        Assert.Contains(said, entry => entry.Room == storeys[1].Stair && entry.Said.StartsWith("on the First Floor it opens straight into the Landing, serving flats", StringComparison.Ordinal));
        Assert.Contains(Escape.Problems(document), problem => problem.Room == storeys[0].Stair && problem.Said.Contains("(3.79)"));
        Assert.Contains("(3.79)", Escape.Describe(document, storeys[0].Stair));

        // Each a protected lobby, its doors and walls 30 minutes: met.
        foreach (var storey in storeys) storey.Corridor.Name = "Lobby";
        Assert.Empty(MixedUse.Problems(document));

        // A lobby's door to the shop of no fire rating: wanting.
        var plain = (DoorType)document.TypesOf<DoorType>().First(each => each.Name == "Single - 900 x 2100").Duplicate("Single - Not Fire Rated");
        plain.FireRating = string.Empty;
        document.AddType(plain);
        storeys[0].BackDoor.TypeId = plain.Id;
        var weak = Assert.Single(MixedUse.Problems(document));
        Assert.Equal(storeys[0].Corridor, weak.Room);
        Assert.StartsWith("it is the lobby on the Ground Floor between the Stair and the other use, its door to the Shop not a 30 minute fire door", weak.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void OverThreeStoreysTheFlatsWantAnIndependentRouteAStrongerStairAndTheAlarm()
    {
        // Ground and four storeys of flats over, 12 m up: 3.80. Lobbies only at the shop's storey (c); each flat an escape route independent of
        // the stair (b); the stair's enclosure 60 minutes, a shop's period by Table B2 (d).
        var (document, storeys) = ShopWithFlats(5);
        var said = MixedUse.Problems(document).Select(problem => problem.Said).ToList();
        Assert.Contains(said, each => each.StartsWith("on the Ground Floor it opens straight into the Corridor, serving the other use", StringComparison.Ordinal) && each.Contains("(3.80c)"));
        Assert.DoesNotContain(said, each => each.Contains("into the Landing"));
        Assert.Equal(4, said.Count(each => each.Contains("is reached only by the Stair, which serves the other use too") && each.Contains("(3.80b)")));
        Assert.Contains("5 walls of its enclosure stand less than 60 minutes", said.Single(each => each.Contains("(3.80d)")));
        Assert.DoesNotContain(said, each => each.Contains("(3.80e)"));

        // A fire alarm system in the shop - a call point by its door: each flat wants a detector of it (e); one in Flat 1 and it is covered.
        var callPoint = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.FireAlarmCallPoint);
        document.Add(new Component { TypeId = callPoint.Id, TypeKind = callPoint.Kind, LevelId = storeys[0].Level.Id, Location = new Point2D(7500, 2300) });
        Assert.Equal(4, MixedUse.Problems(document).Count(problem => problem.Said.Contains("(3.80e)")));
        var detector = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.SmokeDetector);
        document.Add(new Component { TypeId = detector.Id, TypeKind = detector.Kind, LevelId = storeys[1].Level.Id, Location = new Point2D(7500, 4000) });
        Assert.Equal(3, MixedUse.Problems(document).Count(problem => problem.Said.Contains("(3.80e)")));
        Assert.DoesNotContain(MixedUse.Problems(document), problem => problem.Said.Contains("Flat 1 has no detector"));
    }

    [Fact]
    public void FlatsInABlockOfFlatsAreNotMixedUse()
    {
        var (document, storeys) = ShopWithFlats(3);
        document.ProjectInformation.BuildingType = "Flat";
        Assert.False(MixedUse.Is(document));
        Assert.Empty(MixedUse.Problems(document));
        Assert.True(MixedUse.Common(document, storeys[0].Back));
    }
}
