using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Doors on escape routes, escape lighting and exit signs in a building other than a dwelling by Approved Document B Volume 2 Section 5 (round
/// 113): who uses a door in a fire, walked; its direction of opening (5.11a), its fastening (5.9), a vision panel across a corridor (5.14a), a
/// revolving or sliding door (5.15), exit signs (5.28) and escape lighting by Table 5.1.
/// </summary>
[Collection("Wpf")]
public class EscapeRoutesTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Partition = "Interior - Partition 125mm", Wide = "Single - 1000 x 2100";

    private static Wall Wall(BimDocument document, Point2D from, Point2D to, string type = Partition)
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static List<Wall> Outside(BimDocument document, double width, double depth)
    {
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        return Enumerable.Range(0, 4).Select(i => Wall(document, corners[i], corners[(i + 1) % 4], Brick)).ToList();
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

    private static void Put(BimDocument document, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        document.Add(new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = kind.DefaultElevation });
    }

    private static int People(BimDocument document, Room room) =>
        HorizontalEscape.Occupants(document, room, room.GetBoundary(document).Polygon, HorizontalEscape.Purpose(document))!.Value.People;

    /// <summary>Made to swing into a room: flipped where it swings the other way.</summary>
    private static void SwingInto(BimDocument document, Door door, Room room)
    {
        if (Doors.RoomsBeside(document, door).Swing != room) door.FlipFacing = !door.FlipFacing;
        Assert.Equal(room, Doors.RoomsBeside(document, door).Swing);
    }

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Door door) => Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard);

    [Fact]
    public void ADoorForMoreThan60OpensOutwardAndInAShopHasPanicFastenings()
    {
        // A shop's sales hall 20 m by 10 m - about 95 people at 2 m² a person - its front door in the south wall, a second exit in the north wall at x 15 m.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        var walls = Outside(document, 20000, 10000);
        var sales = Room(document, "Sales", 10000, 5000);
        var front = Door(document, walls[0], 5000, "Front Door - 1000 x 2100");
        var back = Door(document, walls[2], 5000);
        SwingInto(document, front, sales);
        SwingInto(document, back, sales);
        var people = People(document, sales);
        Assert.InRange(people, 61, 100);
        Assert.Equal(people, EscapeRoutes.Users(document, back));

        // Both open into the hall against the way out: 5.11a. Both latched: 5.9, a shop's room of more than 60.
        foreach (var door in new[] { front, back })
        {
            Assert.Contains(Problems(document, door), problem => problem.Said == $"it opens into the Sales, against the way out of the building, for {people} people."
                                                                && problem.Rule.StartsWith("Approved Document B Volume 2 (5.11a)", StringComparison.Ordinal));
            Assert.Contains(Problems(document, door), problem => problem.Said == "its fastening is a latch, on the way out of the Sales, a room of more than 60 people."
                                                                && problem.Rule.StartsWith("Approved Document B Volume 2 (5.9)", StringComparison.Ordinal));
        }

        // The second exit flipped to open outward, panic bars on it: neither asked. In an office neither would 5.9 ask.
        back.FlipFacing = !back.FlipFacing;
        document.TypesOf<DoorType>().First(each => each.Name == Wide).Fastening = DoorFastening.PanicBar;
        Assert.Empty(Problems(document, back).Where(problem => problem.Rule.Contains("(5.11a)") || problem.Rule.Contains("(5.9)")).Select(problem => problem.Said + " " + problem.Rule));
        document.ProjectInformation.BuildingType = "Office";
        Assert.DoesNotContain(Problems(document, front), problem => problem.Rule.Contains("(5.9)"));
        document.ProjectInformation.BuildingType = "Shop";

        // The front door is the main entrance, in ordinary use - no exit sign asked of it; the second exit wants one, till one is put up over it.
        Assert.DoesNotContain(Problems(document, front), problem => problem.Rule.Contains("(5.28)"));
        Assert.Contains(Problems(document, back), problem => problem.Said == "no exit sign by it." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.28)", StringComparison.Ordinal));
        Put(document, "Exit Sign - Running Man, Maintained", 15000, 9750);
        Assert.DoesNotContain(Problems(document, back), problem => problem.Rule.Contains("(5.28)"));

        // The hall's escape lighting: an open-plan area of more than 60 m² (Table 5.1 d), till an emergency light is put up in it.
        Assert.StartsWith("an open-plan area of 1", EscapeRoutes.Lighting(document, sales));
        var said = Assert.Single(EscapeRoutes.Problems(document));
        Assert.EndsWith("with no escape lighting - Approved Document B Volume 2 (5.25, Table 5.1) asks escape lighting there for when the mains fail, to BS 5266-1: put an emergency light up in it", said.Said);
        Assert.Contains("with no escape lighting", Escape.Describe(document, sales));
        Put(document, "Emergency Light - Non-maintained LED 3 h", 10000, 5000);
        Assert.Empty(EscapeRoutes.Problems(document));
    }

    [Fact]
    public void ThePeopleWalkedThroughADoorAreSummedAndACorridorsDividingDoorHasAVisionPanel()
    {
        // An office 24 m by 8 m: a corridor along the south, 2 m wide, its one exit at the west end; three meeting rooms north of it, each
        // about 45 people at 1 m² a person - none more than 60 - off it by its own door.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var walls = Outside(document, 24000, 8000);
        var spine = Wall(document, new Point2D(0, 2000), new Point2D(24000, 2000));
        Wall(document, new Point2D(8000, 2000), new Point2D(8000, 8000));
        Wall(document, new Point2D(16000, 2000), new Point2D(16000, 8000));
        var corridor = Room(document, "Corridor", 6000, 1000);
        var rooms = new[] { Room(document, "Meeting Room 1", 4000, 5000), Room(document, "Meeting Room 2", 12000, 5000), Room(document, "Meeting Room 3", 20000, 5000) };
        var doors = new[] { Door(document, spine, 4000), Door(document, spine, 10000), Door(document, spine, 20000) };
        var exit = Door(document, walls[3], 7000);
        SwingInto(document, exit, corridor);

        // Each meeting room's door for its own people, out into the corridor; the exit for them all.
        var each = rooms.Select(room => People(document, room)).ToList();
        Assert.All(each, count => Assert.InRange(count, 40, 60));
        for (var i = 0; i < 3; i++)
        {
            var way = EscapeRoutes.Way(document, doors[i])!.Value;
            Assert.Equal(rooms[i], way.From);
            Assert.Equal(corridor, way.Into);
            Assert.Equal(each[i], EscapeRoutes.Users(document, doors[i]));
            Assert.DoesNotContain(Problems(document, doors[i]), problem => problem.Rule.Contains("(5.11a)"));
        }

        Assert.Equal(each.Sum(), EscapeRoutes.Users(document, exit));
        Assert.Contains(Problems(document, exit), problem => problem.Said == $"it opens into the Corridor, against the way out of the building, for {each.Sum()} people.");

        // The corridor divided at x 12 m by a fire door: the east part walks out through it - the third room's people - and it has no vision panel.
        var divider = Wall(document, new Point2D(12000, 0), new Point2D(12000, 2000));
        var east = Room(document, "Corridor East", 18000, 1000);
        var fireDoor = Door(document, divider, 1000, "Fire Door FD30S - 900 x 2100");
        var through = EscapeRoutes.Way(document, fireDoor)!.Value;
        Assert.Equal(east, through.From);
        Assert.Equal(corridor, through.Into);
        Assert.Equal(each[2], EscapeRoutes.Users(document, fireDoor));
        Assert.Contains(Problems(document, fireDoor), problem => problem.Said == "it divides the corridor - the Corridor East from the Corridor - with no vision panel."
                                                                 && problem.Rule.StartsWith("Approved Document B Volume 2 (5.14a)", StringComparison.Ordinal));
        fireDoor.TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Fire Door FD30S Vision Panel - 1000 x 2100").Id;
        Assert.DoesNotContain(Problems(document, fireDoor), problem => problem.Rule.Contains("(5.14a)"));

        // Flipped to open out: met.
        exit.FlipFacing = !exit.FlipFacing;
        Assert.DoesNotContain(Problems(document, exit), problem => problem.Rule.Contains("(5.11a)"));
    }

    [Fact]
    public void ASlidingOrRevolvingDoorIsAutomaticOrHasASwingDoorBesideIt()
    {
        // The shop's hall again, its second exit a sliding door by hand, for more than 60.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        var walls = Outside(document, 20000, 10000);
        var sales = Room(document, "Sales", 10000, 5000);
        Door(document, walls[0], 5000, "Front Door - 1000 x 2100").FlipFacing = true;
        var slider = Door(document, walls[2], 10000, "Sliding Twin - 1800 x 2100");
        var people = People(document, sales);
        Assert.Contains(Problems(document, slider), problem => problem.Said == $"it slides by hand, on the way out of the building for {people} people."
                                                               && problem.Rule.StartsWith("Approved Document B Volume 2 (5.11a, 5.15)", StringComparison.Ordinal));

        // Made automatic, failing safe open: met.
        var type = document.TypesOf<DoorType>().First(each => each.Name == "Sliding Twin - 1800 x 2100");
        type.Automatic = true;
        Assert.DoesNotContain(Problems(document, slider), problem => problem.Rule.Contains("(5.11a, 5.15)"));

        // A revolving door by hand across the way out: a swing door asked beside it - one put 1.1 m off its edge, opening out, meets it.
        type.Automatic = false;
        type.Operation = DoorOperation.Revolving;
        Assert.Contains(Problems(document, slider), problem => problem.Said == "it revolves, across the way out of the Sales, with no swing door beside it.");
        var beside = Door(document, walls[2], 12500);
        SwingInto(document, beside, sales);
        beside.FlipFacing = !beside.FlipFacing;
        Assert.Empty(Problems(document, slider).Where(problem => problem.Rule.Contains("(5.15)")).Select(problem => problem.Said + " " + problem.Rule));
    }

    [Fact]
    public void APairOfDoorsIsMeasuredWithBothLeavesOpenForEscape()
    {
        // The fire exit pair, 1800: 1800 less two 50 frames and two 40 leaves open - 1620 - for escape (D4a, Diagram D1); 810 through its main
        // leaf alone, as Approved Document M measures it. Table 2.3 takes 324 people through it, 60 through 810.
        var document = BimDocument.CreateDefault();
        var pair = document.TypesOf<DoorType>().First(each => each.Name == "Fire Exit Double - Panic Bars 1800 x 2100");
        Assert.Equal(1620, Doors.EscapeWidth(pair), 6);
        Assert.Equal(810, Doors.ClearWidth(pair), 6);
        Assert.Equal(324, HorizontalEscape.Capacity(Doors.EscapeWidth(pair)));

        // A single leaf and a slider as they were; the automatic entrance's leaves parting, 1900.
        var single = document.TypesOf<DoorType>().First(each => each.Name == Wide);
        Assert.Equal(Doors.ClearWidth(single), Doors.EscapeWidth(single), 6);
        Assert.Equal(1900, Doors.EscapeWidth(document.TypesOf<DoorType>().First(each => each.Name == "Automatic Sliding Entrance - 2000 x 2200")), 6);
        Assert.Equal(850, Doors.EscapeWidth(document.TypesOf<DoorType>().First(each => each.Name == "Sliding Twin - 1800 x 2100")), 6);
    }

    [Fact]
    public void Table51AsksEscapeLightingWhereTheBuildingNeedsIt()
    {
        // An office 12 m by 8 m with no window: windowless accommodation (a). With a window: an open-plan area of more than 60 m² (d).
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var walls = Outside(document, 12000, 8000);
        Wall(document, new Point2D(9000, 0), new Point2D(9000, 8000));
        Wall(document, new Point2D(9000, 4000), new Point2D(12000, 4000));
        var office = Room(document, "Office", 4500, 4000);
        var wc = Room(document, "WC", 10500, 2000);
        var switchRoom = Room(document, "Switch Room", 10500, 6000);
        Door(document, walls[3], 4000).FlipFacing = true;
        Assert.Equal("windowless accommodation", EscapeRoutes.Lighting(document, office));
        document.Add(new Window { HostWallId = walls[0].Id, TypeId = document.TypesOf<WindowType>().First().Id, LevelId = document.Levels[0].Id, DistanceAlongWall = 4500, SillHeight = 900 });
        Assert.Matches(@"^an open-plan area of \d+ m², more than 60 m²$", EscapeRoutes.Lighting(document, office));

        // A WC of about 11 m² - toilet accommodation of 8 m² or more - and a switch room, in any building.
        Assert.StartsWith("toilet accommodation of 1", EscapeRoutes.Lighting(document, wc));
        Assert.Equal("an electricity, generator, switch or battery room", EscapeRoutes.Lighting(document, switchRoom));

        // An emergency light in the office: met there.
        Put(document, "Emergency Light - Non-maintained LED 3 h", 4500, 4000);
        Assert.Null(EscapeRoutes.LightingWanting(document, office));
        Assert.NotNull(EscapeRoutes.LightingWanting(document, wc));

        // A theatre: all its accommodation. A hotel: its common escape routes only - not a room for people.
        document.ProjectInformation.BuildingType = "Theatre";
        Assert.Equal("accommodation in a place of assembly", EscapeRoutes.Lighting(document, office));
        document.ProjectInformation.BuildingType = "Hotel";
        Assert.Null(EscapeRoutes.Lighting(document, office));

        // A shop of one storey, its sales floor under 280 m²: Table 5.1's small shop - its public routes ask none, though (d) still asks the office's.
        document.ProjectInformation.BuildingType = "Shop";
        office.Name = "Sales";
        Assert.Matches(@"^an open-plan area of \d+ m², more than 60 m²$", EscapeRoutes.Lighting(document, office));
    }
}
