using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Means of escape (round 85), as Approved Document B Volume 1 (2019, with the 2020 and 2022 amendments) sets it out: inner rooms (2.11,
/// 2.12, 3.7, 3.8); a flat more than 4.5 m up walked from its entrance door, 9 m at most without a protected hall (3.18); and the common
/// parts walked from each flat's entrance door to a stair, 7.5 m with one stair to go to, 30 m with more (Table 3.1).
/// </summary>
[Collection("Wpf")]
public class EscapeTests
{
    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 2700 };
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

    /// <summary>
    /// A ground floor of rooms in a row, 4 m each and 4 m deep, a door between each two and a front door into the first - the hall - from
    /// outside; the outer walls brick, the rest partitions.
    /// </summary>
    private static (BimDocument Document, List<Room> Rooms, List<Wall> Partitions, Wall Back) Row(params string[] names)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var length = 4000.0 * names.Length;
        const string brick = "Exterior - Brick on Block 330mm";
        Wall(document, level, new Point2D(0, 0), new Point2D(length, 0), brick);
        var east = Wall(document, level, new Point2D(length, 0), new Point2D(length, 4000), brick);
        var back = Wall(document, level, new Point2D(length, 4000), new Point2D(0, 4000), brick);
        var west = Wall(document, level, new Point2D(0, 4000), new Point2D(0, 0), brick);
        Door(document, west, 2000);
        var partitions = new List<Wall>();
        for (var i = 1; i < names.Length; i++)
        {
            var partition = Wall(document, level, new Point2D(4000 * i, 0), new Point2D(4000 * i, 4000));
            Door(document, partition, 2000);
            partitions.Add(partition);
        }

        var rooms = names.Select((name, i) => Room(document, level, name, 4000 * i + 2000, 2000)).ToList();
        _ = east;
        return (document, rooms, partitions, back);
    }

    private static List<EscapeProblem> Of(BimDocument document, Room room) => Escape.Problems(document).Where(problem => problem.Room == room).ToList();

    [Fact]
    public void AnInnerRoomIsAKitchenABathroomOrHasAnEscapeWindow()
    {
        // Hall, living room, bedroom: the bedroom is reached only through the living room - an inner room, with no escape window wanting.
        var (document, rooms, _, back) = Row("Hall", "Living Room", "Bedroom");
        var bedroom = rooms[2];
        Assert.Equal("it is an inner room, reached only through the Living Room - Approved Document B (2.11, 3.7) allows one only as a kitchen, a utility room, a dressing room, a bathroom or WC, or on a storey 4.5 m or less above the ground with an escape window",
            Assert.Single(Of(document, bedroom)).Said);
        Assert.Empty(Of(document, rooms[1]));

        // An escape window in its back wall: allowed on the ground floor.
        document.Add(new Window { HostWallId = back.Id, TypeId = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").Id, LevelId = back.LevelId, DistanceAlongWall = 2000, SillHeight = 800 });
        Assert.Empty(Of(document, bedroom));

        // A kitchen reached through the living room is allowed as it is.
        var (kitchenRow, kitchenRooms, _, _) = Row("Hall", "Living Room", "Kitchen");
        Assert.Empty(Of(kitchenRow, kitchenRooms[2]));
        Assert.Equal("Nothing wanting", Escape.Describe(kitchenRow, kitchenRooms[2]));
    }

    [Fact]
    public void AnInnerInnerRoomIsReachedThroughRoomsWithSmokeAlarmsNotAKitchen()
    {
        // Hall, living room, study, bedroom - the study and bedroom with escape windows: the bedroom reached through the study and the living room.
        var (document, rooms, _, back) = Row("Hall", "Living Room", "Study", "Bedroom");
        var casement = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").Id;
        foreach (var along in new[] { 6000.0, 2000.0 })
            document.Add(new Window { HostWallId = back.Id, TypeId = casement, LevelId = back.LevelId, DistanceAlongWall = along, SillHeight = 800 });
        Assert.Empty(Of(document, rooms[2]));
        Assert.Contains("has no smoke alarm", Assert.Single(Of(document, rooms[3])).Said);

        // Smoke alarms in the study and living room: allowed.
        var alarm = document.TypesOf<ComponentType>().First(type => type.Name == "Smoke Alarm");
        foreach (var room in new[] { rooms[1], rooms[2] })
            document.Add(new Component { TypeId = alarm.Id, TypeKind = alarm.Kind, LevelId = room.LevelId, Location = room.Location, Elevation = alarm.DefaultElevation });
        Assert.Empty(Of(document, rooms[3]));

        // The living room a kitchen: not allowed.
        rooms[1].Name = "Kitchen";
        Assert.Equal("it is reached only through inner rooms, one the Kitchen - Approved Document B (2.12, 3.8) allows that only where none of them is a kitchen", Assert.Single(Of(document, rooms[3])).Said);
    }

    /// <summary>
    /// A storey 6 m up: a common corridor 24 m long along its south side, its stair at the far end; and Flat 1 north of it - a hall 2 m wide
    /// running back 7.5 m from its entrance door off the corridor, a living room and a bedroom each off the hall, 6 m wide.
    /// </summary>
    private static (BimDocument Document, Room Corridor, Room Hall, Room Living, Room Bedroom, Door ToLiving, Door ToBedroom, Level Floor) Block()
    {
        var document = BimDocument.CreateDefault();
        var floor = new Level { Name = "Second Floor", Elevation = 6000 };
        var above = new Level { Name = "Third Floor", Elevation = 9000 };
        document.AddLevel(floor);
        document.AddLevel(above);
        var level = floor.Id;
        const string brick = "Exterior - Brick on Block 330mm";
        Wall(document, level, new Point2D(0, 0), new Point2D(24000, 0), brick);
        Wall(document, level, new Point2D(24000, 0), new Point2D(24000, 1500), brick);
        Wall(document, level, new Point2D(0, 1500), new Point2D(0, 0), brick);
        var front = Wall(document, level, new Point2D(0, 1500), new Point2D(24000, 1500));
        Wall(document, level, new Point2D(0, 1500), new Point2D(0, 9000), brick);
        Wall(document, level, new Point2D(0, 9000), new Point2D(8000, 9000), brick);
        Wall(document, level, new Point2D(8000, 9000), new Point2D(8000, 1500), brick);
        var hallWall = Wall(document, level, new Point2D(2000, 1500), new Point2D(2000, 9000));
        Wall(document, level, new Point2D(2000, 5000), new Point2D(8000, 5000));
        Door(document, front, 1000);

        // The doors off the hall not fire doors: the library's single door is rated 30 minutes, so a plain one made from it.
        var plain = (DoorType)document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Duplicate("Single - Plain");
        plain.FireRating = string.Empty;
        document.AddType(plain);
        var toLiving = Door(document, hallWall, 1500, plain.Name);
        var toBedroom = Door(document, hallWall, 6500, plain.Name);
        var corridor = Room(document, level, "Corridor", 12000, 750);
        var hall = Room(document, level, "Hall", 1000, 5000, "Flat 1");
        var living = Room(document, level, "Living Room", 5000, 3200, "Flat 1");
        var bedroom = Room(document, level, "Bedroom", 5000, 7000, "Flat 1");
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = level, TopLevelId = above.Id, Start = new Point2D(20000, 750), Angle = 0, Width = 900, Mark = "ST1" });
        return (document, corridor, hall, living, bedroom, toLiving, toBedroom, floor);
    }

    [Fact]
    public void AFlatHighUpIsWalkedNoMoreThan9MetresFromItsEntranceUnlessItsHallIsProtected()
    {
        // From the entrance door up the hall 6.4 m, through the bedroom's door and across to its far corner 6.3 m: 13 m, past 9.
        var (document, _, _, _, bedroom, toLiving, toBedroom, _) = Block();
        var far = Assert.Single(Escape.Problems(document), problem => problem.Said.Contains("(3.18)"));
        Assert.Equal(bedroom, far.Room);
        var walked = double.Parse(far.Said[(far.Said.IndexOf(" is ", StringComparison.Ordinal) + 4)..far.Said.IndexOf(" m from", StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(walked, 12.5, 13.5);

        // The doors off the hall to the living room and bedroom fire doors: the hall protected.
        var single = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100");
        var fire = (DoorType)single.Duplicate("Single - FD30");
        fire.FireRating = "FD30";
        document.AddType(fire);
        toLiving.TypeId = toBedroom.TypeId = fire.Id;
        Assert.DoesNotContain(Escape.Problems(document), problem => problem.Said.Contains("(3.18)"));
    }

    [Fact]
    public void TheCommonPartsAreWalkedToAStair()
    {
        // One stair, 19 m along the corridor from the flat's door: past Table 3.1's 7.5 m in one direction.
        var (document, corridor, _, _, _, _, _, floor) = Block();
        var common = Assert.Single(Escape.Problems(document), problem => problem.Said.Contains("(Table 3.1)"));
        Assert.Equal(corridor, common.Room);
        Assert.Contains("allows 7.5 m where escape is in one direction only", common.Said);

        // A second stair 3 m from the door: escape in more than one direction, 30 m, the nearest well within.
        var above = document.Levels.First(level => level.Name == "Third Floor");
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = floor.Id, TopLevelId = above.Id, Start = new Point2D(4000, 750), Angle = 0, Width = 900, Mark = "ST2" });
        Assert.DoesNotContain(Escape.Problems(document), problem => problem.Said.Contains("(Table 3.1)"));

        // Not under the US's codes.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Empty(Escape.Problems(document));
    }
}
