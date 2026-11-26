using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Getting round a building other than a dwelling by Approved Document M Volume 2 (round 106): doors by Table 2 and the space beside their
/// leading edges, doors not standing into corridors, corridors' clear width and passing places, lobbies by Diagram 10.
/// </summary>
[Collection("Wpf")]
public class CirculationTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Single = "Single - 900 x 2100", Wide = "Single - 1000 x 2100";

    private static Wall Wall(BimDocument document, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = Single)
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

    /// <summary>Turns a door to open into a room - or out of it.</summary>
    private static void Swing(BimDocument document, Door door, Room room, bool into)
    {
        var sweep = DoorSwing.Sweeps(document, door).First();
        var inside = Polygon2D.Contains(room.GetBoundary(document).Polygon, new Point2D(sweep.Average(point => point.X), sweep.Average(point => point.Y)));
        if (inside != into) door.FlipFacing = !door.FlipFacing;
    }

    private static List<PlacementProblem> M(BimDocument document, Door door) =>
        Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.StartsWith("Approved Document M", StringComparison.Ordinal)).ToList();

    private sealed record Office(BimDocument Document, Wall CorridorWall, Room Corridor, Room West, Room East, Door WestDoor, Door EastDoor, Door Entrance);

    /// <summary>
    /// An office 20 m by 10 m: a corridor along the south - its partition on y 1800, 1572.5 clear - two offices north of it split at x 10 m, each
    /// off a 900 door from the corridor opening into the office, and the entrance at the corridor's west end, a 1000 door.
    /// </summary>
    private static Office Build(double corridor = 1800)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var corners = new[] { new Point2D(0, 0), new Point2D(20000, 0), new Point2D(20000, 10000), new Point2D(0, 10000) };
        var outside = new List<Wall>();
        for (var i = 0; i < 4; i++) outside.Add(Wall(document, corners[i], corners[(i + 1) % 4], Brick));
        var corridorWall = Wall(document, new Point2D(0, corridor), new Point2D(20000, corridor));
        Wall(document, new Point2D(10000, corridor), new Point2D(10000, 10000));
        var hall = Room(document, "Corridor", 10000, corridor / 2);
        var west = Room(document, "Office 1", 5000, 6000);
        var east = Room(document, "Office 2", 15000, 6000);
        var westDoor = Door(document, corridorWall, 5000);
        var eastDoor = Door(document, corridorWall, 15000);
        var entrance = Door(document, outside[3], 10000 - corridor / 2, Wide);
        Swing(document, westDoor, west, into: true);
        Swing(document, eastDoor, east, into: true);
        Swing(document, entrance, hall, into: false);
        return new Office(document, corridorWall, hall, west, east, westDoor, eastDoor, entrance);
    }

    [Fact]
    public void AnOfficesDoorsAreTable2s()
    {
        var office = Build();
        var document = office.Document;

        // A 900 door gives 760 clear, off a corridor 1572.5 wide: 800 asked at right angles to it (3.10b, Table 2) - not a dwelling's M4(2) 750.
        var said = Assert.Single(M(document, office.WestDoor));
        Assert.Equal("it gives 760 clear, approached at right angles off the Corridor, 1573 wide.", said.Said);
        Assert.StartsWith("Approved Document M Volume 2 (3.10b, Table 2, Diagram 9) asks 800 effective clear width", said.Rule);
        office.WestDoor.TypeId = document.TypesOf<DoorType>().First(type => type.Name == Wide).Id;
        Assert.Empty(M(document, office.WestDoor));

        // The entrance's 1000 door, 860 clear: an office's taken straight on, 800 - a library's, used by the general public, 1000 (2.13b).
        Assert.Empty(M(document, office.Entrance));
        document.ProjectInformation.BuildingType = "Library";
        Assert.Contains(M(document, office.Entrance), problem => problem.Said == "it gives 860 clear, approached as an outside door of a building the general public use."
                                                                  && problem.Rule.StartsWith("Approved Document M Volume 2 (2.13b, Table 2, Diagram 9) asks 1000", StringComparison.Ordinal));

        // A narrower corridor, 1072.5 clear: 825 asked of the doors off it.
        var narrow = Build(1300);
        narrow.Document.FindType<DoorType>(narrow.EastDoor.TypeId)!.Width = 950;
        Assert.Contains(M(narrow.Document, narrow.EastDoor), problem => problem.Said == "it gives 810 clear, approached at right angles off the Corridor, 1073 wide." && problem.Rule.Contains("asks 825"));

        // In a house: M4(2)'s 750, met by the 760.
        var house = Build();
        house.Document.ProjectInformation.BuildingType = "House";
        Assert.DoesNotContain(M(house.Document, house.EastDoor), problem => problem.Rule.Contains("Volume 2"));
    }

    [Fact]
    public void ADoorsLeadingEdgeHas300BesideItAndADoorDoesNotStandIntoACorridor()
    {
        var office = Build();
        var document = office.Document;
        var wide = document.TypesOf<DoorType>().First(type => type.Name == Wide).Id;

        // Office 2's door moved to 100 off the partition at x 10 m: one way round, its leading edge by the partition, too near it (3.10c).
        office.EastDoor.TypeId = wide;
        office.EastDoor.DistanceAlongWall = 10000 + 62.5 + 100 + 500;
        var said = new[] { false, true }.Select(hand =>
        {
            office.EastDoor.FlipHand = hand;
            Swing(document, office.EastDoor, office.East, into: true);
            return M(document, office.EastDoor).Select(problem => problem.Rule).ToList();
        }).ToList();
        Assert.Single(said, rules => rules.Any(rule => rule.StartsWith("Approved Document M Volume 2 (3.10c, Diagram 9) asks 300 clear on a door's pull side", StringComparison.Ordinal)));

        // Office 1's door turned to open out into the corridor: its 900 leaf stands into it (3.14g).
        office.WestDoor.TypeId = wide;
        Swing(document, office.WestDoor, office.West, into: false);
        var stands = Assert.Single(M(document, office.WestDoor));
        Assert.Equal("it opens into the Corridor, standing 900 into it when fully open.", stands.Said);
        Assert.StartsWith("Approved Document M Volume 2 (3.14g)", stands.Rule);

        // Office 1 a store: a small store's door may.
        office.West.Name = "Store";
        Assert.Empty(M(document, office.WestDoor));
    }

    [Fact]
    public void ADoorRecessedOffTheCorridorDoesNotStandIntoIt()
    {
        // The corridor wall stepped back 1000 for 1200 at x 4 m: a recess off the corridor, Office 1's door in its back wall opening into it.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var corners = new[] { new Point2D(0, 0), new Point2D(20000, 0), new Point2D(20000, 10000), new Point2D(0, 10000) };
        for (var i = 0; i < 4; i++) Wall(document, corners[i], corners[(i + 1) % 4], Brick);
        Wall(document, new Point2D(0, 1800), new Point2D(4000, 1800));
        Wall(document, new Point2D(4000, 1800), new Point2D(4000, 2800));
        var back = Wall(document, new Point2D(4000, 2800), new Point2D(5200, 2800));
        Wall(document, new Point2D(5200, 2800), new Point2D(5200, 1800));
        Wall(document, new Point2D(5200, 1800), new Point2D(20000, 1800));
        var corridor = Room(document, "Corridor", 10000, 900);
        var office = Room(document, "Office 1", 10000, 6000);
        var door = Door(document, back, 600, Wide);
        Swing(document, door, corridor, into: true);
        Assert.DoesNotContain(M(document, door), problem => problem.Rule.Contains("3.14g"));

        // The recess 600 deep: 300 of its leaf stands out of it.
        foreach (var wall in document.Walls.Where(wall => wall.Start.Y == 2800 || wall.End.Y == 2800).ToList())
        {
            if (wall.Start.Y == 2800) wall.Start = new Point2D(wall.Start.X, 2400);
            if (wall.End.Y == 2800) wall.End = new Point2D(wall.End.X, 2400);
        }

        Assert.Contains(M(document, door), problem => problem.Said == "it opens into the Corridor, standing 300 into it when fully open.");
        Assert.NotNull(office);
    }

    [Fact]
    public void ACorridorIs1200ClearWithPlacesToPass()
    {
        var office = Build();
        var document = office.Document;

        // 1572.5 clear end to end, no wider anywhere: a passing place asked (3.14c).
        var said = Circulation.Verdict(document, office.Corridor)!;
        Assert.StartsWith("Corridor: 1.57 m clear at its narrowest,", said);
        Assert.Contains("no passing place 1800 long and 1800 wide in it, nor in a corridor or hall its doors open into - Approved Document M Volume 2 (3.14c)", said);
        Assert.DoesNotContain("(3.14a, b)", said);

        // A radiator 100 deep on its south wall at x 8 m, between the offices' doors: 1472.5 at its narrowest.
        var radiator = document.TypesOf<ComponentType>().First(type => type.Name == "Radiator - 600 x 1000");
        document.Add(new Component { TypeId = radiator.Id, TypeKind = radiator.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(8000, 165 + 50), Elevation = 150 });
        Assert.StartsWith("Corridor: 1.47 m clear at its narrowest,", Circulation.Verdict(document, office.Corridor));

        // Narrowed to 1072.5: under 1200 (3.14b).
        var narrow = Build(1300);
        Assert.Contains("1073 clear at its narrowest - Approved Document M Volume 2 (3.14a, b) asks 1200", Circulation.Verdict(narrow.Document, narrow.Corridor));

        // 2.1 m wide from x 14 m - its east end a passing place.
        var stepped = Build();
        stepped.CorridorWall.End = new Point2D(14000, 1800);
        Wall(stepped.Document, new Point2D(14000, 1800), new Point2D(14000, 2200));
        Wall(stepped.Document, new Point2D(14000, 2200), new Point2D(20000, 2200));
        stepped.EastDoor.DistanceAlongWall = 2000;
        stepped.EastDoor.HostWallId = stepped.Document.Walls.Single(wall => wall.Start == new Point2D(14000, 2200)).Id;
        said = Circulation.Verdict(stepped.Document, stepped.Corridor)!;
        Assert.StartsWith("Corridor: 1.57 m clear at its narrowest,", said);
        Assert.Contains("a passing place in it", said);
        Assert.DoesNotContain("(3.14c)", said);

        // A dwelling's hall: not Volume 2's corridor - M4(2)'s hall, its narrowest width, 900 asked and met (round 158).
        document.ProjectInformation.BuildingType = "House";
        var home = Circulation.Verdict(document, office.Corridor)!;
        Assert.StartsWith("Hall: ", home);
        Assert.DoesNotContain("Volume 2", home);
        Assert.DoesNotContain("asks", home);
    }

    [Fact]
    public void ALobbyIsLongEnoughForAWheelchairAndCompanion()
    {
        // An entrance lobby 1772.5 wide, 2772.5 between its two doors' faces: the outside door opening in and the inner one too, 900 leaves each,
        // 437.5 beside each leading edge - 900 + 900 + 1570 = 3370, less 100 for the 137.5 past 300 = 3270 asked (2.29a, Diagram 10).
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
        var outside = new List<Wall>();
        for (var i = 0; i < 4; i++) outside.Add(Wall(document, corners[i], corners[(i + 1) % 4], Brick));
        var inner = Wall(document, new Point2D(0, 3000), new Point2D(5000, 3000));
        Wall(document, new Point2D(5000, 0), new Point2D(5000, 3000));
        Wall(document, new Point2D(2000, 0), new Point2D(2000, 3000));
        var lobby = Room(document, "Entrance Lobby", 1000, 1500);
        var hall = Room(document, "Reception", 6000, 5000);
        Room(document, "Store", 3500, 1500);
        var outer = Door(document, outside[0], 1000, Wide);
        var inside = Door(document, inner, 1000, Wide);
        Swing(document, outer, lobby, into: true);
        Swing(document, inside, lobby, into: true);
        var said = Circulation.Verdict(document, lobby)!;
        Assert.StartsWith("Lobby: 2.77 m long between its doors in line, 1.77 m wide;", said);
        Assert.EndsWith("2773 long between its doors - Approved Document M Volume 2 (2.29a, Diagram 10) asks 3270: the leaves swinging into it, 1800, and 1570 for a wheelchair with a companion pushing, less 100 for the 438 beside the leading edges", said);

        // The inner door turned to open into the reception: 900 + 1570 = 2470 asked - met.
        Swing(document, inside, hall, into: true);
        Assert.DoesNotContain("Diagram 10) asks", Circulation.Verdict(document, lobby));
        Assert.DoesNotContain("wide - Approved Document M Volume 2", Circulation.Verdict(document, lobby));
    }
}
