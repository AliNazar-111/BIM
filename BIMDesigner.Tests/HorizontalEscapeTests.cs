using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Escape across a storey of a building other than a dwelling by Approved Document B Volume 2 (round 109): purpose groups, occupants by Table
/// D1, exits by Tables 2.2 and 2.3, travel distances by Table 2.1 with dead ends by Diagram 2.1.
/// </summary>
[Collection("Wpf")]
public class HorizontalEscapeTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Wide = "Single - 1000 x 2100";

    private static Wall Wall(BimDocument document, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = Wide)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, string name, double x, double y)
    {
        var room = new Room { LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    private sealed record Offices(BimDocument Document, Room Corridor, Room West, Room East, Door WestDoor, Door EastExit);

    /// <summary>
    /// An office 50 m by 10 m: a corridor along the south (y 0 to 2 m) with a final exit at each end, two offices north of it split at x 25 m,
    /// each 192.5 m² off a 1000 door from the corridor at its middle.
    /// </summary>
    private static Offices Build()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var corners = new[] { new Point2D(0, 0), new Point2D(50000, 0), new Point2D(50000, 10000), new Point2D(0, 10000) };
        var outside = new List<Wall>();
        for (var i = 0; i < 4; i++) outside.Add(Wall(document, corners[i], corners[(i + 1) % 4], Brick));
        var corridor = Wall(document, new Point2D(0, 2000), new Point2D(50000, 2000));
        Wall(document, new Point2D(25000, 2000), new Point2D(25000, 10000));
        var hall = Room(document, "Corridor", 25000, 1000);
        var west = Room(document, "Office A", 12500, 6000);
        var east = Room(document, "Office B", 37500, 6000);
        var westDoor = Door(document, corridor, 12500);
        Door(document, corridor, 37500);
        Door(document, outside[3], 9000);
        var eastExit = Door(document, outside[1], 1000);
        return new Offices(document, hall, west, east, westDoor, eastExit);
    }

    [Fact]
    public void ABuildingsPurposeGroupAndItsRoomsOccupantsAreVolume2s()
    {
        var document = BimDocument.CreateDefault();
        foreach (var (type, group) in new[]
                 {
                     ("Office", PurposeGroup.Office), ("Hotel", PurposeGroup.ResidentialOther), ("Care home", PurposeGroup.ResidentialInstitutional), ("Hospital", PurposeGroup.ResidentialInstitutional),
                     ("Restaurant", PurposeGroup.ShopAndCommercial), ("Shop", PurposeGroup.ShopAndCommercial), ("Conference Centre", PurposeGroup.AssemblyAndRecreation),
                     ("Primary School", PurposeGroup.AssemblyAndRecreation), ("Factory", PurposeGroup.Industrial), ("Warehouse", PurposeGroup.Storage)
                 })
        {
            document.ProjectInformation.BuildingType = type;
            Assert.Equal(group, HorizontalEscape.Purpose(document));
        }

        // Table 2.3: 750 for 60, 850 for 110, 1050 for 220, 5 mm a person beyond; Table 2.2: one exit to 60, two to 600, three beyond.
        Assert.Equal(new[] { 0, 60, 110, 220, 220, 300 }, new[] { 740.0, 750, 850, 1050, 1099, 1500 }.Select(HorizontalEscape.Capacity));
        Assert.Equal(new[] { 1, 2, 2, 3 }, new[] { 60, 61, 600, 601 }.Select(HorizontalEscape.ExitsFor));

        // An office of 192.5 m²: 33 people at 6 m² each (Table D1).
        var office = Build();
        var said = Escape.Describe(office.Document, office.West);
        Assert.StartsWith("An office: 33 people, 192.5 m² at 6.0 m² a person, as an office; 1 exit 860 clear;", said);

        // Named a meeting room: 1 m² each, 193 people - two exits asked (Table 2.2), and room for them.
        office.West.Name = "Meeting Room";
        said = Escape.Describe(office.Document, office.West);
        Assert.StartsWith("An office: 193 people, 192.5 m² at 1.0 m² a person, as a meeting room, dining room, restaurant, lounge or the like;", said);
        Assert.Contains("1 exit for 193 people - Approved Document B Volume 2 (2.9, Table 2.2) asks 2 for more than 60", said);
        Assert.Contains("its exits take 110 people - Approved Document B Volume 2 (2.18, 2.21, Table 2.3) asks room for its 193", said);

        // A second 1000 door: two exits, but with the wider one left out the other takes 110 - still short of 193 (2.21).
        Door(office.Document, office.Document.Walls.Single(wall => wall.Start == new Point2D(0, 2000)), 3000);
        said = Escape.Describe(office.Document, office.West);
        Assert.DoesNotContain("(2.9, Table 2.2)", said);
        Assert.Contains("its exits take 110 people, the widest left out", said);
    }

    [Fact]
    public void TravelDistancesAreWalkedToTheNearestStoreyExit()
    {
        // From each office's far corner through its door to the corridor, then either way to an exit: 18 m one way and 45 m in all - met.
        var office = Build();
        var document = office.Document;
        var said = Escape.Describe(document, office.West);
        Assert.Matches(@"; \d+\.\d m from a storey exit at most, \d+\.\d m one way$", said);
        Assert.DoesNotContain("Table 2.1", said);

        // Office A's door moved to its east end: its far corner 25 m from it, one way - more than 18 - and 49 m from the west exit in all, more
        // than 45 (Table 2.1, Diagram 2.1).
        office.WestDoor.DistanceAlongWall = 24000;
        said = Escape.Describe(document, office.West);
        Assert.Matches(@"; 24\.\d m in one direction only and 48\.\d m from a storey exit in all, walked - Approved Document B Volume 2 \(Table 2\.1, 2\.7, Diagram 2\.1\) allows 18 m in one direction only and 45 m where escape is possible in more than one, for an office$", said);
        office.WestDoor.DistanceAlongWall = 12500;

        // The east exit taken away: the corridor a dead end 50 m long, Office B beyond 18 m one way.
        document.Remove(office.EastExit);
        Assert.Contains("in one direction only, walked", Escape.Describe(document, office.East));
        Assert.Contains("in one direction only, walked", Escape.Describe(document, office.Corridor));

        // No exit at all: no way to a storey exit (2.1), and the storey's exits asked (Table 2.2).
        foreach (var door in document.All<Door>().Where(door => Doors.External(document, door, document.FindType<DoorType>(door.TypeId)!)).ToList()) document.Remove(door);
        Assert.Contains("no way from it to a storey exit, walked through the doors - Approved Document B Volume 2 (2.1)", Escape.Describe(document, office.West));
        Assert.Contains(HorizontalEscape.Problems(document), problem => problem.Said.StartsWith($"the {document.Levels[0].Name} holds 66 people and has 0 storey exits - Approved Document B Volume 2 (2.3, 2.9, Table 2.2) asks 2", StringComparison.Ordinal));
    }

    [Fact]
    public void AStoreyForManyHasExitsEnoughAndWideEnough()
    {
        // An assembly hall 30 m by 12 m - 346.2 m² inside its walls at 0.5 m² a person, 693 people - with two 1000 doors out: three exits asked, and room for them.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Community Centre";
        var corners = new[] { new Point2D(0, 0), new Point2D(30000, 0), new Point2D(30000, 12000), new Point2D(0, 12000) };
        var outside = new List<Wall>();
        for (var i = 0; i < 4; i++) outside.Add(Wall(document, corners[i], corners[(i + 1) % 4], Brick));
        var hall = Room(document, "Assembly Hall", 15000, 6000);
        Door(document, outside[0], 10000);
        Door(document, outside[0], 20000);
        var problems = HorizontalEscape.Problems(document).Select(problem => problem.Said).ToList();
        Assert.Contains(problems, said => said.Contains("holds 693 people and has 2 storey exits - Approved Document B Volume 2 (2.3, 2.9, Table 2.2) asks 3"));
        Assert.Contains(problems, said => said.Contains("take 110 people with the widest left out where it holds 693"));
        var room = Escape.Describe(document, hall);
        Assert.StartsWith("Assembly and recreation: 693 people, 346.2 m² at 0.5 m² a person,", room);
        Assert.Contains("2 exits for 693 people - Approved Document B Volume 2 (2.9, Table 2.2) asks 3 for more than 600", room);

        // In a house, none of this: Volume 1's means of escape.
        document.ProjectInformation.BuildingType = "House";
        Assert.Empty(HorizontalEscape.Problems(document));
        Assert.DoesNotContain("Volume 2", Escape.Describe(document, hall));
    }
}
