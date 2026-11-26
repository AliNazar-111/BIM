using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Means of escape by storey (round 87), as Approved Document B Volume 1 sets it out: a house's storeys more than 7.5 m up (2.6); a flat over
/// storeys entered from the common parts (3.21); basements with habitable rooms (2.16); galleries (2.15, Diagram 2.6); and in the common
/// parts, a dead end between two stairs (3.27b, the directions told by Volume 2's 2.10) and a flat opening onto the only stair (3.27a).
/// </summary>
[Collection("Wpf")]
public class EscapeStoreysTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm", double height = 3000)
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = height };
        document.Add(wall);
        return wall;
    }

    /// <summary>Four walls round a rectangle on a level.</summary>
    private static List<Wall> Box(BimDocument document, Guid level, double x0, double y0, double x1, double y1, double height = 3000)
    {
        var corners = new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) };
        return Enumerable.Range(0, 4).Select(i => Wall(document, level, corners[i], corners[(i + 1) % 4], Brick, height)).ToList();
    }

    private static Door Door(BimDocument document, Wall wall, double along, DoorType? type = null)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = (type ?? document.TypesOf<DoorType>().First(each => each.Name == "Single - 900 x 2100")).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    /// <summary>A door type not fire-rated: the library's single door is rated 30 minutes, so a plain one made from it.</summary>
    private static DoorType Plain(BimDocument document)
    {
        var plain = (DoorType)document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Duplicate("Single - Plain");
        plain.FireRating = string.Empty;
        document.AddType(plain);
        return plain;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y, string dwelling = "")
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name, Dwelling = dwelling };
        document.Add(room);
        return room;
    }

    private static Stair Stair(BimDocument document, Level from, Level to, double x, double y, double angle) =>
        Add(document, new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = from.Id, TopLevelId = to.Id, Start = new Point2D(x, y), Angle = angle, Width = 900 });

    private static T Add<T>(BimDocument document, T element) where T : Core.Elements.Element
    {
        document.Add(element);
        return element;
    }

    private static Level Level(BimDocument document, string name, double elevation)
    {
        var level = new Level { Name = name, Elevation = elevation };
        document.AddLevel(level);
        return level;
    }

    /// <summary>
    /// A house of four storeys, 8 m by 6 m, a room filling each and a stair up from each to the next at its east end; the front door on the
    /// ground floor. Its top storey 9 m up.
    /// </summary>
    private static (BimDocument Document, List<Level> Levels, List<Room> Rooms, List<List<Wall>> Walls) Tower()
    {
        var document = BimDocument.CreateDefault();
        var levels = new List<Level> { document.Levels[0], document.Levels[1], Level(document, "Second Floor", 6000), Level(document, "Third Floor", 9000) };
        var walls = levels.Select(level => Box(document, level.Id, 0, 0, 8000, 6000)).ToList();
        Door(document, walls[0][0], 2000);
        var rooms = levels.Select(level => Room(document, level.Id, level == levels[0] ? "Living Room" : "Bedroom", 3000, 3000)).ToList();
        for (var i = 0; i < 3; i++) Stair(document, levels[i], levels[i + 1], 6500, 1000, 90);
        return (document, levels, rooms, walls);
    }

    [Fact]
    public void AHouseStoreyMoreThan75MetresUpHasAnAlternativeEscapeRouteOrSprinklers()
    {
        // The third floor 9 m up, its only way down the one stair: wanting. The second floor 6 m up asks nothing.
        var (document, levels, rooms, walls) = Tower();
        var high = Assert.Single(Escape.Problems(document));
        Assert.Equal(rooms[3], high.Room);
        Assert.Equal("Third Floor is 9.0 m above the ground, with no alternative escape route from it - Approved Document B (2.6) asks one from each storey more than 7.5 m up - a second stair down, or a door out to an escape stair - or sprinklers throughout (BS 9251)", high.Said);

        // Sprinklers throughout: met (2.6b).
        document.ProjectInformation.Sprinklered = true;
        Assert.Empty(Escape.Problems(document));
        document.ProjectInformation.Sprinklered = false;

        // A door out of the third floor's east wall and an escape stair outside rising to it from the ground: met (2.6a).
        Door(document, walls[3][1], 3000);
        Stair(document, levels[0], levels[3], 9000, -12000, 90);
        Assert.Empty(Escape.Problems(document));
    }

    /// <summary>
    /// A basement 2.7 m down under a ground floor hall with the front door, 8 m by 6 m each; the basement's stair up into the hall from its
    /// west half - and in the second case a partition across it at 4 m, a lobby with the stair west of it and the living room east.
    /// </summary>
    private static (BimDocument Document, Room Living, Wall East, Door? Between) Basement(bool lobby) => Basement(lobby, out _, out _);

    private static (BimDocument Document, Room Living, Wall East, Door? Between) Basement(bool lobby, out Wall front, out Room hall)
    {
        var document = BimDocument.CreateDefault();
        var ground = document.Levels[0];
        var basement = Level(document, "Basement", -2700);
        var walls = Box(document, ground.Id, 0, 0, 8000, 6000);
        front = walls[0];
        Door(document, walls[0], 2000);
        hall = Room(document, ground.Id, "Hall", 4000, 3000);
        var down = Box(document, basement.Id, 0, 0, 8000, 6000, 2700);
        Door? between = null;
        Room living;
        if (lobby)
        {
            var partition = Wall(document, basement.Id, new Point2D(4000, 0), new Point2D(4000, 6000), height: 2700);
            between = Door(document, partition, 3000, Plain(document));
            Room(document, basement.Id, "Lobby", 2000, 3000);
            living = Room(document, basement.Id, "Living Room", 6000, 3000);
        }
        else living = Room(document, basement.Id, "Living Room", 4000, 3000);

        var stair = new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = basement.Id, TopLevelId = ground.Id, Start = new Point2D(2000, 1000), Angle = 90, Width = 900 };
        document.Add(stair);
        return (document, living, down[1], between);
    }

    [Fact]
    public void ABasementWithHabitableRoomsHasAnEscapeWindowOrAProtectedStairway()
    {
        // The stair rising out of the living room: no protected stairway, and no window or door out.
        var (document, living, east, _) = Basement(lobby: false);
        Assert.Equal("it is in a basement, with no escape window or door out of it, and its stair is not a protected stairway - it rises through the Living Room - Approved Document B (2.16) asks one of them of a basement with habitable rooms",
            Assert.Single(Escape.Problems(document), problem => problem.Room == living).Said);

        // A basement is no storey above the ground: the ground and first floors are not more than 4.5 m up; the front door opens onto no
        // drop - the ground outside the ground floor's, not the basement's; and the hall on the ground floor is asked no escape window.
        Assert.False(Doors.HasHighStorey(document));
        var (house, _, _, _) = Basement(false, out var front, out var hall);
        Assert.Equal(0, OpeningGuarding.DropSide(house, front, 2000).Drop, 1);
        Assert.DoesNotContain("escape window", WindowRules.Verdict(house, hall) ?? string.Empty);

        // An escape window in its east wall: met (2.16a).
        document.Add(new Window { HostWallId = east.Id, TypeId = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").Id, LevelId = east.LevelId, DistanceAlongWall = 3000, SillHeight = 800 });
        Assert.Empty(Escape.Problems(document));

        // The stair in a lobby, its door to the living room plain: not protected; a fire door - FD30 - and it is (2.16b).
        var (lobbied, room, _, between) = Basement(lobby: true);
        Assert.Contains("its stair is not a protected stairway - the Living Room's door onto it is not a fire door", Assert.Single(Escape.Problems(lobbied), problem => problem.Room == room).Said);
        var fire = (DoorType)lobbied.FindType<DoorType>(between!.TypeId)!.Duplicate("Single - FD30");
        fire.FireRating = "FD30";
        lobbied.AddType(fire);
        between.TypeId = fire.Id;
        Assert.Empty(Escape.Problems(lobbied));
    }

    /// <summary>
    /// Flat 1 over two storeys, 6 m and 9 m up, entered off a common corridor on the lower: a hall with the stair, and a living room east of
    /// it through a plain door; upstairs a landing at the stair's head and a bedroom east of it through a plain door.
    /// </summary>
    private static (BimDocument Document, Room Hall, Room Living, Room Bedroom, Door ToLiving, Door ToBedroom) Maisonette()
    {
        var document = BimDocument.CreateDefault();
        var lower = Level(document, "Second Floor", 6000);
        var upper = Level(document, "Third Floor", 9000);
        Wall(document, lower.Id, new Point2D(0, 0), new Point2D(12000, 0), Brick);
        Wall(document, lower.Id, new Point2D(12000, 0), new Point2D(12000, 1500), Brick);
        Wall(document, lower.Id, new Point2D(0, 1500), new Point2D(0, 0), Brick);
        var front = Wall(document, lower.Id, new Point2D(0, 1500), new Point2D(12000, 1500));
        Door(document, front, 1500);
        var plain = Plain(document);
        Door ToEast(Level level)
        {
            Wall(document, level.Id, new Point2D(0, 1500), new Point2D(0, 7500), Brick);
            Wall(document, level.Id, new Point2D(0, 7500), new Point2D(8000, 7500), Brick);
            Wall(document, level.Id, new Point2D(8000, 7500), new Point2D(8000, 1500), Brick);
            if (level == upper) Wall(document, level.Id, new Point2D(0, 1500), new Point2D(8000, 1500), Brick);
            return Door(document, Wall(document, level.Id, new Point2D(3000, 1500), new Point2D(3000, 7500)), 4500, plain);
        }

        var toLiving = ToEast(lower);
        var toBedroom = ToEast(upper);
        Room(document, lower.Id, "Corridor", 10000, 750);
        var hall = Room(document, lower.Id, "Hall", 1500, 4500, "Flat 1");
        var living = Room(document, lower.Id, "Living Room", 5500, 4500, "Flat 1");
        Room(document, upper.Id, "Landing", 1500, 4500, "Flat 1");
        var bedroom = Room(document, upper.Id, "Bedroom", 5500, 4500, "Flat 1");
        Stair(document, lower, upper, 1500, 2000, 90);
        return (document, hall, living, bedroom, toLiving, toBedroom);
    }

    [Fact]
    public void AFlatOverTwoStoreysTakesOneOfTheFourApproaches()
    {
        // No alternative exit off the entrance storey, and the stair's doors plain: none of the four approaches.
        var (document, hall, living, bedroom, toLiving, toBedroom) = Maisonette();
        var flat = Assert.Single(Escape.Problems(document));
        Assert.Equal(hall, flat.Room);
        Assert.StartsWith("Flat 1: a flat on 2 storeys, entered from the common parts, a storey of it more than 4.5 m up - Approved Document B (3.21) asks", flat.Said);
        Assert.EndsWith("; its stair is not protected - the Living Room's door onto it is not a fire door", flat.Said);

        // The doors onto the hall and landing fire doors: the stair protected, but no smoke alarms.
        var fire = (DoorType)document.FindType<DoorType>(toLiving.TypeId)!.Duplicate("Single - FD30");
        fire.FireRating = "FD30";
        document.AddType(fire);
        toLiving.TypeId = toBedroom.TypeId = fire.Id;
        Assert.EndsWith("; there is no smoke alarm in the Bedroom", Assert.Single(Escape.Problems(document)).Said);

        // Smoke alarms in the bedroom and living room, the upper storey within 7.5 m of the entrance storey: approach 4, met.
        var alarm = document.TypesOf<ComponentType>().First(type => type.Name == "Smoke Alarm");
        foreach (var room in new[] { living, bedroom })
            document.Add(new Component { TypeId = alarm.Id, TypeKind = alarm.Kind, LevelId = room.LevelId, Location = room.Location, Elevation = alarm.DefaultElevation });
        Assert.Empty(Escape.Problems(document));
    }

    /// <summary>
    /// A storey 6 m up: a common corridor 32 m long and 1.5 m deep along its south side, and Flat 1 north of it, a room 4 m square entered at
    /// 12 m along the corridor; the common stairs in the corridor, each rising east from where it starts.
    /// </summary>
    private static (BimDocument Document, Room Corridor, Level Floor, Level Above) Corridor(params double[] stairs)
    {
        var document = BimDocument.CreateDefault();
        var floor = Level(document, "Second Floor", 6000);
        var above = Level(document, "Third Floor", 9000);
        Wall(document, floor.Id, new Point2D(0, 0), new Point2D(32000, 0), Brick);
        Wall(document, floor.Id, new Point2D(32000, 0), new Point2D(32000, 1500), Brick);
        Wall(document, floor.Id, new Point2D(0, 1500), new Point2D(0, 0), Brick);
        var front = Wall(document, floor.Id, new Point2D(0, 1500), new Point2D(32000, 1500));
        Door(document, front, 12000);
        Wall(document, floor.Id, new Point2D(10000, 1500), new Point2D(10000, 5500), Brick);
        Wall(document, floor.Id, new Point2D(10000, 5500), new Point2D(14000, 5500), Brick);
        Wall(document, floor.Id, new Point2D(14000, 5500), new Point2D(14000, 1500), Brick);
        var corridor = Room(document, floor.Id, "Corridor", 1000, 750);
        Room(document, floor.Id, "Living Room", 12000, 3500, "Flat 1");
        foreach (var x in stairs) Stair(document, floor, above, x, 750, 0);
        return (document, corridor, floor, above);
    }

    private static List<EscapeProblem> Table31(BimDocument document) => Escape.Problems(document).Where(problem => problem.Said.Contains("(Table 3.1)")).ToList();

    [Fact]
    public void AFlatInADeadEndBetweenTwoStairsIs75MetresFromOne()
    {
        // Stairs 8 m west and 8 m east of the flat's door: escape in two directions, 180 degrees apart - 30 m allowed.
        var (document, _, _, _) = Corridor(4000, 20000);
        Assert.Empty(Table31(document));

        // Both east of it, 8 m and 14 m: the routes leave the door the same way - a dead end, 7.5 m allowed (3.27b).
        (document, var corridor, _, _) = Corridor(20000, 26000);
        var dead = Assert.Single(Table31(document));
        Assert.Equal(corridor, dead.Room);
        Assert.Contains("allows 7.5 m where escape is in one direction only, out of a dead end (3.27b)", dead.Said);
    }

    [Fact]
    public void AFlatIsNotEnteredStraightOffTheOnlyStair()
    {
        // The only stair in the corridor the flat opens into, 2 m from its door: no protected lobby between them.
        var (document, corridor, floor, _) = Corridor(14000);
        var onto = Assert.Single(Escape.Problems(document), problem => problem.Said.Contains("(3.27a"));
        Assert.Equal(corridor, onto.Room);
        Assert.Equal("Flat 1's entrance door opens straight onto the only common stair - Approved Document B (3.27a, Diagram 3.7) asks a protected lobby or protected corridor between each flat and it, or - in a small single stair building of two flats a storey - the flat's entrance hall protected (Diagram 3.9b)", onto.Said);
        Assert.Empty(Table31(document));

        // A partition across the corridor at 13 m with a door: the stair in a lobby of its own.
        Door(document, Wall(document, floor.Id, new Point2D(13000, 0), new Point2D(13000, 1500)), 750);
        Room(document, floor.Id, "Stair Lobby", 20000, 750);
        Assert.DoesNotContain(Escape.Problems(document), problem => problem.Said.Contains("(3.27a"));
        Assert.Empty(Table31(document));
    }

    /// <summary>
    /// A house 8 m by 6 m whose living room rises both storeys, 6 m; over its west end a gallery on the first floor - separation lines round
    /// it, so many metres east - and a stair up into it from the living room at 1.5 m along; the front door in the south wall so far along.
    /// </summary>
    private static (BimDocument Document, Room Gallery) Gallery(double east, double frontDoor)
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var walls = Box(document, ground.Id, 0, 0, 8000, 6000, 6000);
        Door(document, walls[0], frontDoor);
        var living = Room(document, ground.Id, "Living Room", 6000, 3000);
        living.UpperLimitOffset = 6000;
        var corners = new[] { new Point2D(0, 0), new Point2D(east, 0), new Point2D(east, 6000), new Point2D(0, 6000) };
        for (var i = 0; i < 4; i++) document.Add(new RoomSeparator { LevelId = first.Id, Start = corners[i], End = corners[(i + 1) % 4] });
        var gallery = Room(document, first.Id, "Gallery", east / 2, 3000);
        Stair(document, ground, first, 1500, 500, 90);
        return (document, gallery);
    }

    [Fact]
    public void AGalleryWithoutAnAlternativeExitMeetsDiagram26()
    {
        // 3 m deep - 37.5 % of the room below; its far corners 4 m or so from the head of its stair; the stair's foot 2.5 m from the front door.
        var (document, gallery) = Gallery(3000, 4000);
        Assert.Empty(Escape.Problems(document));

        // The front door 7.5 m along: the stair's foot 6 m from it, past 3 m.
        (document, gallery) = Gallery(3000, 7500);
        var far = Assert.Single(Escape.Problems(document));
        Assert.Equal(gallery, far.Room);
        Assert.StartsWith("it is a gallery over the Living Room, with no alternative exit or escape window - Approved Document B (2.15, Diagram 2.6) then asks the foot of its stair within 3 m of the way out of the Living Room - it is ", far.Said);

        // 5 m deep: inside the walls standing up through the first floor from the living room - its brick 330 thick - and the separator,
        // 4.835 by 5.67 m, 27.4 m², over the room below's 7.67 by 5.67 m, 43.5 m²: 63 %, past half (round 143).
        (document, _) = Gallery(5000, 4000);
        Assert.Contains("half the Living Room at least left open to view from it - it covers 63% of it", Assert.Single(Escape.Problems(document)).Said);
    }
}
