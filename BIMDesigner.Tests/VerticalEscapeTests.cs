using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Escape stairs in a building other than a dwelling by Approved Document B Volume 2 Section 3 (round 111): one stair only where 3.3 allows, its
/// width by Tables 3.1 and 3.2, a protected stairway, lobbies, refuges and a final exit.
/// </summary>
[Collection("Wpf")]
public class VerticalEscapeTests
{
    private const string FireDoor = "Fire Door FD30S - 900 x 2100";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = FireDoor)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private sealed record Building(BimDocument Document, List<Level> Levels, List<Room> Stairs, List<Room> Offices, List<Room> Corridors);

    /// <summary>
    /// An office 14 m by 10 m on so many storeys 3 m apart: on each a stair at the west end (x 0 to 3 m) entered from a corridor along the south
    /// (y 0 to 2 m) through an FD30S door, an office north of the corridor; a final exit from the ground floor's corridor; the stair 900 wide rising
    /// north from each storey to the next.
    /// </summary>
    private static Building Build(int storeys = 2)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        while (levels.Count < storeys)
        {
            var next = new Level { Name = $"Floor {levels.Count}", Elevation = levels.Count * 3000 };
            document.AddLevel(next);
            levels.Add(next);
        }

        var (stairs, offices, corridors) = (new List<Room>(), new List<Room>(), new List<Room>());
        foreach (var level in levels.Take(storeys))
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(14000, 0), new Point2D(14000, 10000), new Point2D(0, 10000) };
            var outside = new List<Wall>();
            for (var i = 0; i < 4; i++) outside.Add(Wall(document, level.Id, corners[i], corners[(i + 1) % 4], "Exterior - Brick on Block 330mm"));
            var stairWall = Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 10000));
            var corridorWall = Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(14000, 2000));
            Door(document, stairWall, 1000);
            Door(document, corridorWall, 5500, "Single - 1000 x 2100");
            if (level == levels[0]) Door(document, outside[1], 1000, "Single - 1000 x 2100");
            foreach (var (name, x, y, list) in new[] { ("Stair", 1500.0, 8000.0, stairs), ("Office", 8500.0, 6000.0, offices), ("Corridor", 8500.0, 1000.0, corridors) })
            {
                var room = new Room { LevelId = level.Id, Location = new Point2D(x, y), Name = name };
                document.Add(room);
                list.Add(room);
            }
        }

        foreach (var (foot, head) in levels.Take(storeys).Zip(levels.Take(storeys).Skip(1)))
            document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = foot.Id, TopLevelId = head.Id, Start = new Point2D(1500, 2600), Angle = 90, Width = 900 });
        return new Building(document, levels.Take(storeys).ToList(), stairs, offices, corridors);
    }

    [Fact]
    public void AStairsWidthIsTable32s()
    {
        Assert.Equal(new[] { 0, 50, 150, 190, 220, 260, 330, 480 }, new[] { (700.0, 1), (900, 3), (1000, 1), (1000, 2), (1100, 1), (1100, 2), (1200, 3), (1500, 4) }.Select(each => VerticalEscape.Capacity(each.Item1, each.Item2)));
        Assert.Equal(1035, VerticalEscape.Capacity(1800, 10));
    }

    [Fact]
    public void OneStairIsEnoughForASmallOfficeAndItsAProtectedStairwayWithARefuge()
    {
        // Two storeys, 14 people upstairs within 18 m of the stair: one stair will do (3.3d); 900 takes 50; its doors FD30S; no refuge upstairs.
        var building = Build();
        var document = building.Document;
        var stair = Assert.Single(VerticalEscape.Stairs(document));
        Assert.Equal(900, stair.Width);
        var said = VerticalEscape.Verdict(document, building.Stairs[0])!;
        Assert.StartsWith("Escape stair, 900 wide, serving 1 storey above the ground;", said);
        Assert.DoesNotContain("(3.3)", said);
        Assert.DoesNotContain("(3.23", said);
        Assert.Contains($"no refuge by it on the {building.Levels[1].Name} - Approved Document B Volume 2 (3.4 to 3.6)", said);

        // A refuge on the landing upstairs: met.
        var refuge = document.TypesOf<ComponentType>().First(type => type.Name == "Refuge - Wheelchair Space 900 x 1400");
        document.Add(new Component { TypeId = refuge.Id, TypeKind = refuge.Kind, LevelId = building.Levels[1].Id, Location = new Point2D(1500, 8500) });
        Assert.Equal("Escape stair, 900 wide, serving 1 storey above the ground", VerticalEscape.Verdict(document, building.Stairs[0]));

        // Its door upstairs a plain door with no closer: not a protected stairway (3.23).
        var door = document.All<Door>().Single(each => document.Find<Wall>(each.HostWallId) is { } wall && wall.LevelId == building.Levels[1].Id && wall.Start == new Point2D(3000, 0));
        door.TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Id;
        Assert.Contains("it is not a protected stairway: its doors Single - 900 x 2100 not E 30 Sa with closers - Approved Document B Volume 2 (3.23, Tables B1, C1)", VerticalEscape.Verdict(document, building.Stairs[0]));
    }

    [Fact]
    public void ABusyUpperStoreyAsksASecondStairAndAWiderOne()
    {
        // The upstairs office a meeting room - 84 people at 1 m² each. An office may still have one stair, its storeys within 18 m of it (3.3e) -
        // but 900 takes 50 (Table 3.2).
        var building = Build();
        var document = building.Document;
        building.Offices[1].Name = "Meeting Room";
        var said = VerticalEscape.Verdict(document, building.Stairs[0])!;
        Assert.DoesNotContain("(3.3)", said);

        // A shop: more than 60 upstairs, so not one stair (3.3d).
        document.ProjectInformation.BuildingType = "Shop";
        said = VerticalEscape.Verdict(document, building.Stairs[0])!;
        Assert.Contains("it is the only escape stair from 1 upper storey, the top 3.0 m up - Approved Document B Volume 2 (3.3)", said);
        Assert.Contains("it is 900 wide, taking 50 people from 1 storey where 84 of the 84 on them may use it - Approved Document B Volume 2 (3.13, 3.14, 3.17, 3.18, Table 3.2)", said);

        // In the Warnings window too, said on the stair's foot.
        Assert.Contains(VerticalEscape.Problems(document), problem => problem.Room == building.Stairs[0] && problem.Said.Contains("(3.3)"));
    }

    [Fact]
    public void AnOnlyStairOverTwoStoreysIsReachedThroughLobbiesAndLeadsOut()
    {
        // Three storeys, one stair, entered from corridors: lobbies asked at the first floor (3.34a) - a corridor will do.
        var building = Build(3);
        var document = building.Document;
        Assert.DoesNotContain("(3.34", VerticalEscape.Verdict(document, building.Stairs[0]));

        // The first floor's corridor named an open office: the stair opens straight into the accommodation there.
        building.Corridors[1].Name = "Open Office";
        Assert.Contains($"it opens straight into the accommodation on the {building.Levels[1].Name} - Approved Document B Volume 2 (3.34a)", VerticalEscape.Verdict(document, building.Stairs[0]));
        building.Corridors[1].Name = "Corridor";

        // The ground floor's final exit taken away: the stair's foot leads to no final exit (3.36).
        var exit = document.All<Door>().Single(door => Doors.External(document, door, document.FindType<DoorType>(door.TypeId)!));
        document.Remove(exit);
        Assert.Contains("its foot, the Stair, has no final exit - Approved Document B Volume 2 (3.36)", VerticalEscape.Verdict(document, building.Stairs[0]));

        // In a house: none of this.
        document.ProjectInformation.BuildingType = "House";
        Assert.Null(VerticalEscape.Verdict(document, building.Stairs[0]));
    }
}
