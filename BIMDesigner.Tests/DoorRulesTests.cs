using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Doors as the rules see them: the clear width each gives, its glass, its closer, and the next
/// door's swing kept from its own.
/// </summary>
public class DoorRulesTests
{
    private static DoorType Type(BimDocument document, string name) =>
        document.TypesOf<DoorType>().Single(type => type.Name == name);

    [Fact]
    public void EachDoorGivesTheClearWidthItsLeafLeavesOpen()
    {
        var document = BimDocument.CreateDefault();

        // Between the stops, less the leaf open at 90 degrees: a 900 door gives 760, the 1000 front door 860.
        Assert.Equal(760, Doors.ClearWidth(Type(document, "Single - 900 x 2100")));
        Assert.Equal(860, Doors.ClearWidth(Type(document, "Entrance Arched Light - 1000 x 2300")));

        // A pair's main leaf; a slider's opening; a twin slider's half.
        Assert.Equal(810, Doors.ClearWidth(Type(document, "Double - 1800 x 2100")));
        Assert.Equal(800, Doors.ClearWidth(Type(document, "Sliding - 900 x 2100")));
        Assert.Equal(850, Doors.ClearWidth(Type(document, "Sliding Twin - 1800 x 2100")));
        Assert.Contains(Type(document, "Single - 900 x 2100").GetTypeParameters(document), parameter => parameter.Name == "Clear Opening Width");
    }

    [Fact]
    public void GlazedDoorsHaveSafetyGlassAndItIsSavedWithTheCloser()
    {
        var document = BimDocument.CreateDefault();
        var french = Type(document, "French Double - 1500 x 2100");
        Assert.True(Doors.Glazed(french));
        Assert.True(french.SafetyGlazing);
        Assert.False(Doors.Glazed(Type(document, "Single - 900 x 2100")));

        var single = Type(document, "Single - 900 x 2100");
        single.SelfClosing = true;
        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.True(Type(loaded, "Single - 900 x 2100").SelfClosing);
        Assert.True(Type(loaded, "French Double - 1500 x 2100").SafetyGlazing);
    }

    [Fact]
    public void TwoDoorsWhoseSwingsMeetAreCalledOutOnce()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();

        // Two walls meeting at a corner, a door in each close to it, both opening into the corner.
        var south = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 };
        var west = new Wall { Start = new Point2D(0, 0), End = new Point2D(0, 4000), TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 };
        document.Add(south);
        document.Add(west);
        var single = Type(document, "Single - 900 x 2100");
        var first = new Door { TypeId = single.Id, LevelId = level, HostWallId = south.Id, DistanceAlongWall = 650, Mark = "D1" };
        var second = new Door { TypeId = single.Id, LevelId = level, HostWallId = west.Id, DistanceAlongWall = 650, Mark = "D2" };
        document.Add(first);
        document.Add(second);

        // Turned to swing into the room, toward the corner.
        foreach (var door in new[] { first, second })
        {
            var sweep = DoorSwing.Sweeps(document, door).Single();
            if (sweep.Average(point => point.X) < 0 || sweep.Average(point => point.Y) < 0) door.FlipFacing = !door.FlipFacing;
            if (DoorSwing.Sweeps(document, door).Single().Average(point => point.X + point.Y) > 1600) door.FlipHand = !door.FlipHand;
        }

        var blocked = Clearances.Blocked(document, level, ClearanceStandard.ApprovedDocumentM).Where(item => item.Zone.Element is Door).ToList();
        var clash = Assert.Single(blocked);
        Assert.Equal(second, clash.Zone.Element);
        Assert.Equal("door D1's swing", clash.By);
    }

    /// <summary>A room 4 m by 3 m inside from (0, 0), its outside walls of the exterior type: south, east, north, west.</summary>
    private static (BimDocument Document, List<Wall> Walls) Box(BimDocument? document = null, Guid? levelId = null, bool outside = true)
    {
        document ??= BimDocument.CreateDefault();
        var level = levelId ?? document.Levels[0].Id;
        var wallType = outside ? document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior) : document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(4000 + half, -half), new Point2D(4000 + half, 3000 + half), new Point2D(-half, 3000 + half) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2700 };
            document.Add(wall);
            walls.Add(wall);
        }

        return (document, walls);
    }

    private static Door Hang(BimDocument document, Wall wall, string type, double along, string mark)
    {
        var door = new Door { TypeId = Type(document, type).Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = along, Mark = mark };
        document.Add(door);
        return door;
    }

    [Fact]
    public void AnExternalDoorWants850ClearAndEveryDoor850ForAWheelchairUser()
    {
        var (document, walls) = Box();
        var front = Hang(document, walls[0], "Single - 900 x 2100", 2000, "D1");

        Assert.Contains(Doors.Problems(document, front, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "it gives 760 clear." && problem.Rule.Contains("2.20d"));
        Assert.StartsWith("760 clear - It gives 760 clear.", Doors.Verdict(document, front));
        Assert.Contains(front.GetInstanceParameters(document), parameter => parameter.Name == "Regulations");

        // The 1000 front door gives 860: wide enough.
        front.TypeId = Type(document, "Entrance Arched Light - 1000 x 2300").Id;
        Assert.DoesNotContain(Doors.Problems(document, front, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("850"));

        // A pair's main leaf gives what counts: 1500 French doors, 660.
        front.TypeId = Type(document, "French Double - 1500 x 2100").Id;
        Assert.Contains(Doors.Problems(document, front, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "it gives 660 clear through its main leaf.");

        // An internal door: 750 under M4(2), 850 under M4(3), 32 in under the ADA.
        var (inside, partitions) = Box(outside: false);
        var bedroom = Hang(inside, partitions[1], "Single - 900 x 2100", 1500, "D2");
        Assert.DoesNotContain(Doors.Problems(inside, bedroom, ClearanceStandard.ApprovedDocumentM), problem => problem.Said.Contains("clear"));
        Assert.Contains(Doors.Problems(inside, bedroom, ClearanceStandard.ApprovedDocumentM3), problem => problem.Rule.Contains("850 clear through every door"));
        Assert.Contains(Doors.Problems(inside, bedroom, ClearanceStandard.Ada), problem => problem.Rule.Contains("404.2.3"));
    }

    [Fact]
    public void InTheUsOneDoorOutIsAnEgressDoor()
    {
        var (document, walls) = Box();
        var back = Hang(document, walls[2], "Single - 900 x 2100", 2000, "D1");
        Assert.Contains(Doors.Problems(document, back, ClearanceStandard.Irc), problem => problem.Rule.Contains("R311.2"));

        Hang(document, walls[0], "Entrance Arched Light - 1000 x 2300", 2000, "D2");
        Assert.DoesNotContain(Doors.Problems(document, back, ClearanceStandard.Irc), problem => problem.Rule.Contains("R311.2"));
    }

    [Fact]
    public void AnExternalDoorWantsA300NibBesideItsLeadingEdge()
    {
        var (document, walls) = Box();

        // The front door hard by the east wall, hung so its latch and its swing are toward that corner.
        var type = Type(document, "Entrance Arched Light - 1000 x 2300");
        var front = Hang(document, walls[0], "Entrance Arched Light - 1000 x 2300", 4000 + 165 - 500 - 100, "D1");
        foreach (var (hand, facing) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            (front.FlipHand, front.FlipFacing) = (hand, facing);
            if (Doors.Nib(document, front, type) is { } nib && nib < 300) break;
        }

        Assert.InRange(Doors.Nib(document, front, type)!.Value, 50, 150);
        Assert.Contains(Doors.Problems(document, front, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("300 nib"));

        // Moved along the wall, it has room.
        front.DistanceAlongWall = 2000;
        Assert.DoesNotContain(Doors.Problems(document, front, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("300 nib"));
    }

    [Fact]
    public void ADoorIntoAnIntegralGarageIsAFireDoorThatClosesItself()
    {
        var (document, _) = Box(outside: false);
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var partition = new Wall { Start = new Point2D(2000 + half, -half), End = new Point2D(2000 + half, 3000 + half), TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2700 };
        document.Add(partition);
        document.Add(new Room { LevelId = level, Location = new Point2D(1000, 1500), Name = "Garage", Number = "1" });
        document.Add(new Room { LevelId = level, Location = new Point2D(3100, 1500), Name = "Utility", Number = "2" });

        var door = Hang(document, partition, "Single - 900 x 2100", 1500, "D1");
        Assert.Contains(Doors.Problems(document, door, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "it opens into the garage, rated 30 minutes and with no closer.");

        var fire = Type(document, "Single - 900 x 2100").Duplicate("Single FD30S - 900 x 2100");
        fire.SelfClosing = true;
        document.AddType(fire);
        door.TypeId = fire.Id;
        Assert.DoesNotContain(Doors.Problems(document, door, ClearanceStandard.ApprovedDocumentM), problem => problem.Said.Contains("garage"));
    }

    [Fact]
    public void InAHouseWithAStoreyOver45MTheDoorsOntoTheStairAreFireDoors()
    {
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        document.AddLevel(new Level { Name = "Second Floor", Elevation = 6000 });
        var second = document.Levels.Single(level => level.Name == "Second Floor");
        document.Add(new Room { LevelId = second.Id, Location = new Point2D(2000, 1500), Name = "Loft Bedroom", Number = "3" });

        // The first floor: a landing on the left, a bedroom on the right, a door between.
        Box(document, first.Id, outside: false);
        var wallType = document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var partition = new Wall { Start = new Point2D(2000 + half, -half), End = new Point2D(2000 + half, 3000 + half), TypeId = wallType.Id, LevelId = first.Id, UnconnectedHeight = 2700 };
        document.Add(partition);
        document.Add(new Room { LevelId = first.Id, Location = new Point2D(1000, 1500), Name = "Landing", Number = "1" });
        document.Add(new Room { LevelId = first.Id, Location = new Point2D(3100, 1500), Name = "Bedroom", Number = "2" });
        StairTests.Put(document, 1000, -500, angle: 90);
        Assert.True(Doors.HasHighStorey(document));
        Assert.Contains(Doors.StairRooms(document), room => room.Name == "Landing");

        var plain = Type(document, "Single - 900 x 2100").Duplicate("Single - Unrated");
        plain.FireRating = string.Empty;
        document.AddType(plain);
        var door = Hang(document, partition, "Single - Unrated", 1500, "D1");
        Assert.Contains(Doors.Problems(document, door, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("FD20"));

        door.TypeId = Type(document, "Single - 900 x 2100").Id;
        Assert.DoesNotContain(Doors.Problems(document, door, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("FD20"));
    }

    [Fact]
    public void TheLibrarysFrontFireAndUsDoorsMeetTheirRules()
    {
        var (document, walls) = Box();
        var front = Hang(document, walls[0], "Front Door - 1000 x 2100", 2000, "D1");
        Assert.Equal(860, Doors.ClearWidth(Type(document, "Front Door - 1000 x 2100")));
        Assert.Empty(Doors.Problems(document, front, ClearanceStandard.ApprovedDocumentM));

        var fire = Type(document, "Fire Door FD30S - 900 x 2100");
        Assert.True(fire.SelfClosing);
        Assert.Equal(30, Doors.FireMinutes(fire.FireRating));

        // The US door: 32 in clear by 78 in - the egress door.
        var us = Type(document, "Exterior Door - 36 x 80 in");
        Assert.True(Doors.ClearWidth(us) >= 813);
        front.TypeId = us.Id;
        Assert.Empty(Doors.Problems(document, front, ClearanceStandard.Irc));
    }
}
