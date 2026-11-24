using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Small premises finished (round 131), by Approved Document B Volume 2 Section 4: highly flammable materials (4.1), cooking at a dead end's
/// extremity (4.2b), an undivided floor (4.4), exits in effective alternative directions (4.6), three storeys with an open stair (4.9).
/// </summary>
public class SmallPremisesPlanningTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";
    private const string Partition = "Interior - Partition 125mm";

    private static BimDocument Building(string use)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        return document;
    }

    private static Wall Wall(BimDocument document, Guid level, double x0, double y0, double x1, double y1, string type = Partition)
    {
        var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    /// <summary>Four outside walls, x1 by y1: south, east, north, west - the west running from north to south.</summary>
    private static List<Wall> Box(BimDocument document, Guid level, double x1, double y1) =>
        new() { Wall(document, level, 0, 0, x1, 0, Brick), Wall(document, level, x1, 0, x1, y1, Brick), Wall(document, level, x1, y1, 0, y1, Brick), Wall(document, level, 0, y1, 0, 0, Brick) };

    private static Door Door(BimDocument document, Wall wall, double along, bool exterior = false)
    {
        var type = exterior ? document.TypesOf<DoorType>().First(each => each.Function == DoorFunction.Exterior) : document.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100");
        var door = new Door { HostWallId = wall.Id, DistanceAlongWall = along, LevelId = wall.LevelId, TypeId = type.Id };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, double x, double y, string name, string number = "")
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name, Number = number };
        document.Add(room);
        return room;
    }

    private static List<string> Said(BimDocument document) => StoreyPlanning.Problems(document).Select(problem => $"{problem.Room.Name}: {problem.Said}").ToList();

    [Fact]
    public void HighlyFlammableStockMakesNoSmallPremises()
    {
        var shop = Building("Shop");
        var level = shop.Levels[0].Id;
        var walls = Box(shop, level, 12000, 8000);
        Door(shop, walls[3], 4000, exterior: true);
        Room(shop, level, 6000, 4000, "Shop");
        Assert.True(HorizontalEscape.SmallPremises(shop).Small);
        shop.ProjectInformation.BuildingType = "Fireworks Shop";
        var premises = HorizontalEscape.SmallPremises(shop);
        Assert.False(premises.Small);
        Assert.Equal("highly flammable materials sold, stored or used in it - fireworks - which small premises are not for (4.1)", premises.Said);
    }

    [Fact]
    public void CookingIsAtTheFarEndOfADeadEndNotOnTheWayOut()
    {
        // A cafe-shop 12 m by 8 m: its kitchen the west third, the only way out through the kitchen's west door (4.2b).
        var shop = Building("Shop");
        var level = shop.Levels[0].Id;
        var walls = Box(shop, level, 12000, 8000);
        var kitchenWall = Wall(shop, level, 4000, 0, 4000, 8000);
        var west = Door(shop, walls[3], 4000, exterior: true);
        Door(shop, kitchenWall, 4000);
        Room(shop, level, 2000, 4000, "Kitchen");
        Room(shop, level, 8000, 4000, "Shop");
        Assert.Contains(Said(shop), text => text.StartsWith("Kitchen: the escape from the Shop passes through it, a kitchen - Approved Document B Volume 2 (4.2b)", StringComparison.Ordinal));

        // The way out moved to the shop's east wall: the kitchen at the far end of a dead end.
        shop.Remove(west);
        Door(shop, walls[1], 4000, exterior: true);
        Assert.DoesNotContain(Said(shop), text => text.Contains("(4.2b)"));

        // One room 12 m by 8 m, its way out at the west end, a cooker in the open at its middle: those farther in pass it (4.2b); at the east end, not.
        var cafe = Building("Shop");
        level = cafe.Levels[0].Id;
        walls = Box(cafe, level, 12000, 8000);
        Door(cafe, walls[3], 4000, exterior: true);
        Room(cafe, level, 6000, 4000, "Shop");
        var cookerType = cafe.TypesOf<ComponentType>().First(each => each.Form == ComponentForm.Cooker);
        var cooker = new Component { TypeId = cookerType.Id, TypeKind = cookerType.Kind, LevelId = level, Location = new Point2D(6000, 4000) };
        cafe.Add(cooker);
        Assert.Contains(Said(cafe), text => text.StartsWith("Shop: the escape from farther in passes the cooker in it, an open cooking arrangement - Approved Document B Volume 2 (4.2b)", StringComparison.Ordinal));
        cooker.Location = new Point2D(11300, 4000);
        Assert.DoesNotContain(Said(cafe), text => text.Contains("(4.2b)"));
    }

    [Fact]
    public void ASmallPremisesFloorIsUndividedButForKitchensOfficesAndStores()
    {
        // A shop 12 m by 8 m, its store and back office partitioned off: undivided enough (4.4).
        var shop = Building("Shop");
        var level = shop.Levels[0].Id;
        var walls = Box(shop, level, 12000, 8000);
        Door(shop, walls[3], 4000, exterior: true);
        var back = Wall(shop, level, 8000, 0, 8000, 8000);
        var middle = Wall(shop, level, 8000, 4000, 12000, 4000);
        Door(shop, back, 2000);
        Door(shop, back, 6000);
        Room(shop, level, 4000, 4000, "Shop");
        var store = Room(shop, level, 10000, 2000, "Store");
        var office = Room(shop, level, 10000, 6000, "Office");
        Assert.DoesNotContain(Said(shop), text => text.Contains("(4.4)"));

        // The back room a second sales area: the floor divided.
        office.Name = "Sales";
        Assert.Contains(Said(shop), text => text.StartsWith("Sales: it divides the storey's floor from the Shop - Approved Document B Volume 2 (4.4)", StringComparison.Ordinal));

        // In an office building its offices are the floor itself: two offices divide it.
        shop.ProjectInformation.BuildingType = "Office";
        office.Name = "Office";
        store.Name = "Office";
        Assert.Contains(Said(shop), text => text.Contains("(4.4)"));
        Assert.NotNull(middle);
    }

    [Fact]
    public void TwoExitsOfSmallPremisesLeadInEffectiveAlternativeDirections()
    {
        // A shop 12 m by 8 m, its two doors side by side in its west wall: from its east end both lie the same way (4.6).
        var shop = Building("Shop");
        var level = shop.Levels[0].Id;
        var walls = Box(shop, level, 12000, 8000);
        Door(shop, walls[3], 3000, exterior: true);
        var second = Door(shop, walls[3], 5000, exterior: true);
        Room(shop, level, 6000, 4000, "Shop");
        Assert.Contains(Said(shop), text => text.StartsWith("Shop: from part of it the storey's 2 exits lie within 45° of one way - Approved Document B Volume 2 (4.6)", StringComparison.Ordinal));

        // The second door at the far end: alternative directions from everywhere.
        shop.Remove(second);
        Door(shop, walls[1], 4000, exterior: true);
        Assert.DoesNotContain(Said(shop), text => text.Contains("(4.6)"));
    }

    [Fact]
    public void ThreeStoreysWithAnOpenStairHaveTheOtherStairEnclosedWithItsOwnExit()
    {
        // A shop of three storeys, 10 m by 8 m each: an open stair from the ground storey's sales area up to the first, and the basement's stair
        // enclosed at the ground storey in a stair room 3 m by 5 m in the south-east corner with a door straight outside (4.9, Diagram 4.3).
        var shop = Building("Shop");
        var (ground, first) = (shop.Levels[0].Id, shop.Levels[1].Id);
        var basement = new Level { Name = "Basement", Elevation = -3000 };
        shop.AddLevel(basement);
        var walls = Box(shop, ground, 10000, 8000);
        Box(shop, first, 10000, 8000);
        Box(shop, basement.Id, 10000, 8000);
        var stairWest = Wall(shop, ground, 7000, 0, 7000, 5000);
        Wall(shop, ground, 7000, 5000, 10000, 5000);
        Door(shop, walls[3], 4000, exterior: true);
        Door(shop, stairWest, 2500);
        var own = Door(shop, walls[0], 8500, exterior: true);
        Room(shop, ground, 3000, 6000, "Sales Area");
        var stairRoom = Room(shop, ground, 8500, 2500, "Stair");
        Room(shop, first, 5000, 4000, "Sales Area");
        Room(shop, basement.Id, 5000, 4000, "Sales Area");
        var type = shop.TypesOf<StairType>().First().Id;
        shop.Add(new Stair { TypeId = type, LevelId = ground, TopLevelId = first, Start = new Point2D(2500, 1200), Angle = 90, Width = 900 });
        shop.Add(new Stair { TypeId = type, LevelId = basement.Id, TopLevelId = ground, Start = new Point2D(8500, 400), Angle = 90, Width = 900 });
        Assert.True(HorizontalEscape.SmallPremises(shop).Small);
        Assert.DoesNotContain(Said(shop), text => text.Contains("(4.9"));

        // Its door outside taken away: the basement's stair discharges through the ground storey.
        shop.Remove(own);
        var said = Assert.Single(Said(shop), text => text.Contains("(4.9"));
        Assert.StartsWith("Stair: the premises are three storeys, the first storey served by the open stair in the Sales Area, and the stair to the basement has no final exit of its own at the ground storey", said);
        Assert.NotNull(stairRoom);
    }
}
