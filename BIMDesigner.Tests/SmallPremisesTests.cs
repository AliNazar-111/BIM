using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Small premises (round 124), by Approved Document B Volume 2 Section 4: what they are (2.2, 4.2), Table 4.1's travel distances, no count of
/// exits by Table 2.2, a single stair, an open stair by 4.8, store rooms in REI 30 (4.5), and no compartment floor over a basement (8.11c).
/// </summary>
public class SmallPremisesTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static List<Wall> Box(BimDocument document, Guid level, double width, double depth)
    {
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = document.TypesOf<WallType>().First(each => each.Name == Brick).Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            walls.Add(wall);
        }

        return walls;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = "Single - 900 x 2100")
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    /// <summary>A shop of one storey, so wide and deep to the walls' middles, its one door out near the south-west corner, one sales area inside.</summary>
    private static (BimDocument Document, Room Sales, List<Wall> Walls) Shop(double width, double depth)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        var ground = document.Levels[0].Id;
        var walls = Box(document, ground, width, depth);
        Door(document, walls[0], 1000);
        var sales = new Room { LevelId = ground, Location = new Point2D(width / 2, depth / 2), Name = "Sales Area" };
        document.Add(sales);
        return (document, sales, walls);
    }

    [Fact]
    public void ASmallShopIsWalkedByTable41()
    {
        // 28 m by 10 m to the walls' middles: 27.67 by 9.67 inside, 267.5 m² - one storey, under 280 m²: small premises (2.2, 4.2).
        var (document, sales, _) = Shop(28000, 10000);
        var premises = HorizontalEscape.SmallPremises(document);
        Assert.True(premises.Small);
        Assert.Equal("small premises (2.2, 4.2): 1 storey, the largest 268 m²", premises.Said);

        // From the door, 1 m along the south wall, to the far corner: about 27.2 m along and 9.4 m up inside - some 28.7 m, over Table 4.1's 27 m
        // for a ground storey with a single exit (Table 2.1's 18 m in one direction no longer asked). Its 134 people at 2 m² - more than 60 - ask
        // no second exit (4.3a).
        var verdict = HorizontalEscape.Verdict(document, sales)!;
        Assert.StartsWith("Small premises, a ground storey with a single exit:", verdict, StringComparison.Ordinal);
        Assert.Contains("Approved Document B Volume 2 (4.6, Table 4.1) allows 27 m from any point of a ground storey with a single exit in small premises to its nearest storey exit", verdict);
        Assert.DoesNotContain("Table 2.1", verdict);
        Assert.DoesNotContain("Table 2.2", verdict);
        Assert.DoesNotContain(HorizontalEscape.Problems(document), problem => problem.Said.Contains("Table 2.2"));

        // 22 m long, 21.67 inside: within 27 m - nothing wanting.
        var (shorter, room, _) = Shop(22000, 10000);
        Assert.DoesNotContain("allows", HorizontalEscape.Verdict(shorter, room));

        // 30 m long: 29.67 by 9.67, 287 m² - not small premises; Table 2.1 and Table 2.2 again.
        var (bigger, big, _) = Shop(30000, 10000);
        Assert.Equal("a storey of 287 m² - small premises have none over 280 m² (2.2a, 4.2a iii)", HorizontalEscape.SmallPremises(bigger).Said);
        var said = HorizontalEscape.Verdict(bigger, big)!;
        Assert.Contains("Table 2.1", said);
        Assert.Contains("(2.9, Table 2.2)", said);
    }

    [Fact]
    public void AnOfficeIsSmallPremisesAHotelOrAFlatOverItIsNot()
    {
        var (office, _, _) = Shop(14000, 10000);
        office.ProjectInformation.BuildingType = "Office";
        Assert.True(HorizontalEscape.SmallPremises(office).Small);
        office.ProjectInformation.BuildingType = "Hotel";
        Assert.StartsWith("residential (other), purpose group 2(b) - 2.2 offers small premises", HorizontalEscape.SmallPremises(office).Said, StringComparison.Ordinal);

        // A restaurant of 9.67 by 13.67, 132.2 m² at 1 m² a person: 133 people, rounded up, on its ground storey with its own final exit -
        // past 100 (4.2c).
        var (cafe, room, _) = Shop(10000, 14000);
        cafe.ProjectInformation.BuildingType = "Restaurant";
        room.Name = "Restaurant";
        Assert.StartsWith("a bar or restaurant planned for 133 people on the Ground Floor", HorizontalEscape.SmallPremises(cafe).Said, StringComparison.Ordinal);
    }

    /// <summary>A shop on two storeys of the same box, an open stair in its sales area from 1 m inside the front door, rising north.</summary>
    private static (BimDocument Document, Room Ground, Room First) TwoStoreys(double width, double depth)
    {
        var (document, sales, _) = Shop(width, depth);
        var first = document.Levels[1].Id;
        Box(document, first, width, depth);
        var upper = new Room { LevelId = first, Location = new Point2D(width / 2, depth / 2), Name = "Sales Area" };
        document.Add(upper);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = document.Levels[0].Id, TopLevelId = first, Start = new Point2D(2500, 1200), Angle = 90, Width = 900 });
        return (document, sales, upper);
    }

    [Fact]
    public void AnOpenStairInSmallPremisesIsWalkedToItsHeadAndJudgedBy48()
    {
        // 10 m by 8 m, 74 m² a storey: the only stair, open in the sales areas, two storeys, entering the ground storey a metre or so from the
        // door - 4.8 met. Its head is the first storey's exit (Table 4.1 note b): 18 m there.
        var (document, ground, first) = TwoStoreys(10000, 8000);
        Assert.True(HorizontalEscape.SmallPremises(document).Small);
        Assert.True(HorizontalEscape.OpenStairRoom(document, first));
        var upper = HorizontalEscape.Verdict(document, first)!;
        Assert.StartsWith("Small premises, a first storey with a single stair:", upper, StringComparison.Ordinal);
        Assert.DoesNotContain("allows", upper);
        Assert.NotNull(HorizontalEscape.Verdict(document, ground));
        var stair = Assert.Single(VerticalEscape.Stairs(document));
        var said = VerticalEscape.Problems(document, stair);
        Assert.DoesNotContain(said, each => each.Contains("(4.8)") || each.Contains("not a protected stairway") || each.Contains("(3.3)") || each.Contains("refuge"));

        // 12 m by 10 m, 113 m² a storey: a single open stair allows 90 m² (4.8d).
        var (wider, _, _) = TwoStoreys(12000, 10000);
        var open = Assert.Single(VerticalEscape.Problems(wider, Assert.Single(VerticalEscape.Stairs(wider))), each => each.Contains("(4.8)"));
        Assert.Contains("a storey is 113 m², over the 90 m² a single open stair allows (4.8d)", open);

        // A restaurant of the same two storeys: 74 m² at 1 m² a person, 75 people a storey, past 4.2c's 30 - not small premises. Its sales
        // floors are walked all the same, not taken as a stair's enclosure; the open stair is no storey exit, and wants enclosing (3.23).
        var (restaurant, below, above) = TwoStoreys(10000, 8000);
        restaurant.ProjectInformation.BuildingType = "Restaurant";
        (below.Name, above.Name) = ("Restaurant", "Restaurant");
        Assert.False(HorizontalEscape.SmallPremises(restaurant).Small);
        Assert.Contains("no way from it to a storey exit", HorizontalEscape.Verdict(restaurant, above));
        Assert.Contains(VerticalEscape.Problems(restaurant, Assert.Single(VerticalEscape.Stairs(restaurant))),
            each => each.StartsWith("it stands open in the Restaurant on the Ground Floor, the Restaurant on the First Floor, not in an enclosure of its own - Approved Document B Volume 2 (3.23)", StringComparison.Ordinal));
    }

    [Fact]
    public void AStoreRoomOfSmallPremisesIsEnclosedInRei30()
    {
        // A store 4 m deep at the east end of a 14 m shop, behind a 30 minute partition and a 30 minute door: met.
        var (document, _, _) = Shop(14000, 10000);
        var partition = new Wall { Start = new Point2D(10000, 0), End = new Point2D(10000, 10000), TypeId = document.TypesOf<WallType>().First(each => each.Name == "Interior - Partition 125mm").Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(partition);
        var door = Door(document, partition, 5000);
        var store = new Room { LevelId = document.Levels[0].Id, Location = new Point2D(12000, 5000), Name = "Store" };
        document.Add(store);
        Assert.DoesNotContain("(4.5)", HorizontalEscape.Verdict(document, store));

        // Its door of no rating: wanting.
        var plain = (DoorType)document.TypesOf<DoorType>().First(each => each.Name == "Single - 900 x 2100").Duplicate("Single - Not Fire Rated");
        plain.FireRating = string.Empty;
        document.AddType(plain);
        door.TypeId = plain.Id;
        Assert.Contains("its doors Single - Not Fire Rated not 30 minute fire doors - Approved Document B Volume 2 (4.5) asks store rooms of small premises enclosed in fire resisting construction, REI 30", HorizontalEscape.Verdict(document, store));
    }

    [Fact]
    public void SmallPremisesAskNoCompartmentFloorOverABasement()
    {
        var (document, _, _) = Shop(14000, 10000);
        var basement = new Level { Name = "Basement", Elevation = -3000 };
        document.AddLevel(basement);
        Box(document, basement.Id, 14000, 10000);
        document.Add(new Room { LevelId = basement.Id, Location = new Point2D(7000, 5000), Name = "Store Room" });
        Assert.True(HorizontalEscape.SmallPremises(document).Small);
        Assert.Null(FireCompartments.CompartmentFloorAt(document, 0));

        // 30 m long: not small premises - the ground storey's floor over the basement a compartment floor (8.11c).
        var (bigger, _, _) = Shop(30000, 10000);
        var below = new Level { Name = "Basement", Elevation = -3000 };
        bigger.AddLevel(below);
        Box(bigger, below.Id, 30000, 10000);
        bigger.Add(new Room { LevelId = below.Id, Location = new Point2D(15000, 5000), Name = "Store Room" });
        Assert.Equal("the ground storey's floor over a basement a compartment floor (8.11c)", FireCompartments.CompartmentFloorAt(bigger, 0));
    }
}
