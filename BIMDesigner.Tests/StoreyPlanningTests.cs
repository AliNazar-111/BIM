using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// How a storey of a building other than a home is planned for escape (round 130), by Approved Document B Volume 2 2.12 to 2.17: exits in a
/// central core, openings between floors, a stair passed through or used as circulation, eating areas, separate occupancies.
/// </summary>
public class StoreyPlanningTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";
    private const string Partition = "Interior - Partition 125mm";

    private sealed class Plan
    {
        public BimDocument Document { get; } = BimDocument.CreateDefault();

        public Guid Level => Document.Levels[0].Id;

        public Plan(string use) => Document.ProjectInformation.BuildingType = use;

        public Wall Wall(double x0, double y0, double x1, double y1, string type = Partition)
        {
            var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = Document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = Level, UnconnectedHeight = 3000 };
            Document.Add(wall);
            return wall;
        }

        public List<Wall> Box(double x1, double y1) =>
            new() { Wall(0, 0, x1, 0, Brick), Wall(x1, 0, x1, y1, Brick), Wall(x1, y1, 0, y1, Brick), Wall(0, y1, 0, 0, Brick) };

        public Door Door(Wall wall, double along, string? type = null)
        {
            var doorType = type is null ? Document.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100")
                : type == "exterior" ? Document.TypesOf<DoorType>().First(each => each.Function == DoorFunction.Exterior) : Document.TypesOf<DoorType>().First(each => each.Name == type);
            var door = new Door { HostWallId = wall.Id, DistanceAlongWall = along, LevelId = Level, TypeId = doorType.Id };
            Document.Add(door);
            return door;
        }

        public Room Room(double x, double y, string name, string number, string occupancy = "")
        {
            var room = new Room { LevelId = Level, Location = new Point2D(x, y), Name = name, Number = number, Occupancy = occupancy };
            Document.Add(room);
            return room;
        }

        public List<string> Said() => StoreyPlanning.Problems(Document).Select(problem => $"{problem.Room.Name} {problem.Room.Number}: {problem.Said}").ToList();
    }

    [Fact]
    public void TwoStairsAreNotApproachedFromOneLobbyNorReachedThroughEachOtherNorUsedAsTheWayBetweenParts()
    {
        // An office 24 m by 14 m - more than small premises' 280 m² - a stair, a lobby, a second stair and the office beyond it, in a row; doors
        // from the lobby into both stairs and from the office into the second.
        var plan = new Plan("Office");
        var outside = plan.Box(24000, 14000);
        var (a, b, c) = (plan.Wall(3000, 0, 3000, 14000), plan.Wall(6000, 0, 6000, 14000), plan.Wall(9000, 0, 9000, 14000));
        plan.Room(1500, 7000, "Stair", "S1");
        plan.Room(4500, 7000, "Lobby", "1");
        plan.Room(7500, 7000, "Stair", "S2");
        plan.Room(16500, 7000, "Office", "2");
        plan.Door(a, 7000);
        plan.Door(b, 7000);
        plan.Door(c, 7000);
        var said = plan.Said();

        // 2.12: both stairs from the one lobby.
        Assert.Contains(said, text => text.StartsWith("Lobby 1: storey exits into the Stair S1 and the Stair S2 are both approached from it - Approved Document B Volume 2 (2.12, Diagram 2.4)", StringComparison.Ordinal));
        // 2.14: the office reaches the first stair only through the second.
        Assert.Contains(said, text => text.StartsWith("Office 2: the Stair S1 is reached from here only through the Stair S2 - Approved Document B Volume 2 (2.14)", StringComparison.Ordinal));
        // 2.15: the second stair the only way between the lobby and the office.
        Assert.Contains(said, text => text.StartsWith("Stair S2: it is the only way on this storey between the Lobby 1 and the Office 2, its doors not held open on an automatic release - Approved Document B Volume 2 (2.15)", StringComparison.Ordinal));

        // Its doors held open on the fire alarm: 2.15 met.
        var held = plan.Document.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100");
        held.AutomaticRelease = true;
        Assert.DoesNotContain(plan.Said(), text => text.Contains("(2.15)"));
    }

    [Fact]
    public void EscapeRoutesKeepClearOfAnOpeningBetweenFloorsOrLeadAwayFromIt()
    {
        // An open-plan office 20 m by 15 m - more than small premises' 280 m² - an opening 4 m square at its middle through its floor to the storey
        // below - an atrium; one final exit, at the north end of its west wall.
        var plan = new Plan("Office");
        var outside = plan.Box(20000, 15000);
        plan.Room(3000, 7500, "Office", "1");
        plan.Door(outside[3], 1000, "exterior");
        var floor = new Floor { LevelId = plan.Document.Levels[0].Id, TypeId = plan.Document.TypesOf<FloorType>().First(each => each.Name == "Floor - Screed on Slab 250mm").Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(20000, 0), new Point2D(20000, 15000), new Point2D(0, 15000) });
        floor.SetHoles(new[] { new[] { new Point2D(8000, 5500), new Point2D(12000, 5500), new Point2D(12000, 9500), new Point2D(8000, 9500) } });
        plan.Document.Add(floor);
        Assert.Single(HorizontalEscape.Openings(plan.Document, plan.Level));

        // The east end walks west past the opening, getting nearer it (2.13).
        var said = Assert.Single(plan.Said());
        Assert.StartsWith("Office 1: every way out of part of it passes ", said);
        Assert.Contains("m from an opening between floors, getting nearer it - Approved Document B Volume 2 (2.13, Diagram 2.5)", said);

        // A second exit at the south end of the east wall: from everywhere one way leads away from it, or along it.
        plan.Door(outside[1], 1000, "exterior");
        Assert.Empty(plan.Said());
    }

    [Fact]
    public void ACafeInAnOfficeHasTwoWaysOutNotThroughItsKitchen()
    {
        // An office 20 m by 15 m - more than small premises' 280 m² - a cafe at the east end whose only door is into its kitchen, the kitchen's into
        // the office, a final exit at the west end.
        var plan = new Plan("Office");
        var outside = plan.Box(20000, 15000);
        var (kitchenWall, cafeWall) = (plan.Wall(12000, 0, 12000, 15000), plan.Wall(15000, 0, 15000, 15000));
        plan.Room(6000, 7500, "Office", "1");
        plan.Room(13500, 7500, "Kitchen", "2");
        plan.Room(17500, 7500, "Cafe", "3");
        plan.Door(kitchenWall, 7500);
        plan.Door(cafeWall, 7500);
        plan.Door(outside[3], 7500, "exterior");
        var said = plan.Said();
        Assert.Contains(said, text => text.StartsWith("Cafe 3: one way out of it, an area for eating or drinking - Approved Document B Volume 2 (2.16a)", StringComparison.Ordinal));
        Assert.Contains(said, text => text.StartsWith("Cafe 3: its escape passes through the Kitchen 2 - Approved Document B Volume 2 (2.16b)", StringComparison.Ordinal));

        // A door of its own straight outside: two ways out, and its escape leads there.
        plan.Door(outside[1], 7500, "exterior");
        Assert.DoesNotContain(plan.Said(), text => text.StartsWith("Cafe 3", StringComparison.Ordinal));

        // In a restaurant, eating is the main use: not asked.
        plan.Document.ProjectInformation.BuildingType = "Restaurant";
        Assert.DoesNotContain(plan.Said(), text => text.Contains("(2.16"));
    }

    [Fact]
    public void SeparateOccupanciesEscapeNotThroughEachOtherAndTheirCommonCorridorIsProtectedOrDetected()
    {
        // An office 30 m by 8 m let to two tenants: a corridor 2 m wide along its south side, a final exit at its west end; Tenant A's office west of
        // 15 m, Tenant B's east, each with a door into the corridor.
        var plan = new Plan("Office");
        var outside = plan.Box(30000, 8000);
        var corridorWall = plan.Wall(0, 2000, 30000, 2000);
        var between = plan.Wall(15000, 2000, 15000, 8000);
        var corridor = plan.Room(15000, 1000, "Corridor", "1");
        var a = plan.Room(7500, 5000, "Office", "2", "Tenant A");
        var b = plan.Room(22500, 5000, "Office", "3", "Tenant B");
        plan.Door(outside[3], 7000, "exterior");
        plan.Door(corridorWall, 7500);
        var bDoor = plan.Door(corridorWall, 22500);

        // The corridor's partitions 30 minutes and doors FD30S: a protected corridor - met.
        Assert.Empty(plan.Said());

        // Its partitions not fire-resisting: neither protected nor detected (2.17b).
        var partition = plan.Document.TypesOf<WallType>().First(each => each.Name == Partition);
        partition.FireRating = string.Empty;
        Assert.Contains(plan.Said(), text => text.StartsWith("Corridor 1: a common corridor on the escape routes of the storey's separate occupancies - Tenant A, Tenant B - neither a protected corridor nor covered", StringComparison.Ordinal));

        // A detector in every room of the storey: met.
        var detector = plan.Document.TypesOf<ComponentType>().First(each => each.Form == ComponentForm.SmokeDetector);
        foreach (var room in new[] { corridor, a, b })
            plan.Document.Add(new Component { TypeId = detector.Id, TypeKind = detector.Kind, LevelId = plan.Level, Location = room.Location, Elevation = 2700 });
        Assert.Empty(plan.Said());

        // Tenant B's door moved from the corridor into Tenant A's office: B's escape through A's (2.17a).
        plan.Document.Remove(bDoor);
        plan.Door(between, 3000);
        Assert.Contains(plan.Said(), text => text.StartsWith("Office 3: its escape, Tenant B's, passes through the Office 2, Tenant A's - Approved Document B Volume 2 (2.17a)", StringComparison.Ordinal));
    }
}
