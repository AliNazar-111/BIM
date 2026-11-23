using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Space separation as Approved Document B has it for any building (round 119): each side measured to the boundary it faces, Method 1 for
/// small buildings of homes and small hotels, Method 2's share of the rectangle round a side's unprotected areas by Table 11.1 and 13.1
/// for the rest, half a combustible face counted (13.7), small areas disregarded (Diagram 13.5) and a protected stairway's windows left out
/// (13.10) - every figure worked by hand.
/// </summary>
public class SpaceSeparationVolume2Tests
{
    private sealed record Building(BimDocument Document, Dictionary<string, Wall> Walls, Guid Level, double Half, PropertyLine Line, double Length, double Depth)
    {
        /// <summary>The property line round the building: so far from its south, east, north and west faces, mm.</summary>
        public void Boundary(double south, double east = 20000, double north = 20000, double west = 20000) =>
            Line.SetBoundary(new[]
            {
                new Point2D(-Half - west, -Half - south), new Point2D(Length + Half + east, -Half - south),
                new Point2D(Length + Half + east, Depth + Half + north), new Point2D(-Half - west, Depth + Half + north)
            });

        public void Windows(string wall, string type, double sill, params double[] centres)
        {
            var windowType = Document.TypesOf<WindowType>().Single(candidate => candidate.Name == type);
            foreach (var centre in centres)
                Document.Add(new Window { TypeId = windowType.Id, LevelId = Level, HostWallId = Walls[wall].Id, DistanceAlongWall = centre, SillHeight = sill });
        }

        public IReadOnlyList<(string Said, string Rule)> Problems(string wall) =>
            SpaceSeparation.Problems(Document, Walls[wall], Document.ProjectInformation.ClearanceStandard);
    }

    /// <summary>A building of a use, so long east to west and so deep, its outside walls of a type and height, 7.5 m from the boundary to the south and 20 m elsewhere.</summary>
    private static Building Make(string use, double length = 20000, double depth = 12000, double height = 3000, string type = "Exterior - Brick on Block 330mm")
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        var level = document.Levels.Where(each => each.Elevation >= 0).OrderBy(each => each.Elevation).First().Id;
        var wallType = document.TypesOf<WallType>().Single(candidate => candidate.Name == type);
        var corners = new[] { new Point2D(0, 0), new Point2D(length, 0), new Point2D(length, depth), new Point2D(0, depth) };
        var walls = new Dictionary<string, Wall>();
        var names = new[] { "south", "east", "north", "west" };
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = height };
            document.Add(wall);
            walls[names[i]] = wall;
        }

        var line = new PropertyLine { LevelId = level };
        document.Add(line);
        var building = new Building(document, walls, level, wallType.Structure.TotalWidth / 2, line, length, depth);
        building.Boundary(7500);
        return building;
    }

    [Fact]
    public void ASideIsMeasuredToTheBoundaryItFacesNotOneItRunsSquareTo()
    {
        // A house 8 m by 6 m, 0.8 m from the boundary to the west and 1.5 m to the east, 10 m to the south.
        var house = Make("House", 8000, 6000, 2700);
        house.Boundary(10000, east: 1500, west: 800);
        var south = house.Walls["south"];

        // The south wall's west end is 0.8 m + its half thickness from the west boundary - which it runs square to, so it is not its
        // relevant boundary (Diagram 11.2): it is 10 m from the one in front of it.
        Assert.Equal(10000, SpaceSeparation.DistanceToBoundary(house.Document, south)!.Value, 1);
        Assert.Equal(800, SpaceSeparation.DistanceToBoundary(house.Document, house.Walls["west"])!.Value, 1);

        // Five windows of 1.44 m² in it: no limit at 6 m or more - not "a wall less than 1 m from the boundary".
        house.Windows("south", "Casement - 1200 x 1200", 900, 1000, 2500, 4000, 5500, 7000);
        Assert.Empty(house.Problems("south"));
        Assert.Contains("South 10.0 m: 7.2 m² open, no limit", SpaceSeparation.Summary(house.Document));
    }

    [Fact]
    public void Table131IsReadByPurposeGroupAndInterpolated()
    {
        // Column 1 - residential, office, assembly and recreation: 8% at 1 m, 20 at 2.5, 40 at 5, 60 at 7.5, 80 at 10, 100 at 12.5.
        Assert.Equal(8, SpaceSeparation.Percent(1, false), 6);
        Assert.Equal(14, SpaceSeparation.Percent(1.75, false), 6);   // 8 + 0.75 / 1.5 x 12
        Assert.Equal(24, SpaceSeparation.Percent(3, false), 6);      // 20 + 0.5 / 2.5 x 20
        Assert.Equal(100, SpaceSeparation.Percent(12.5, false), 6);
        Assert.Equal(0, SpaceSeparation.Percent(0.9, false), 6);

        // Column 2 - shop and commercial, industrial, storage: 4% at 1 m, 8 at 2, 20 at 5, 40 at 10, 60 at 15, 80 at 20, 100 at 25.
        Assert.Equal(4, SpaceSeparation.Percent(1, true), 6);
        Assert.Equal(12, SpaceSeparation.Percent(3, true), 6);       // 8 + 1 / 3 x 12
        Assert.Equal(30, SpaceSeparation.Percent(7.5, true), 6);     // 20 + 2.5 / 5 x 20
        Assert.Equal(100, SpaceSeparation.Percent(30, true), 6);

        // Sprinklered throughout (13.22): the more of double the share - 2 x 32 at 4 m - and the share at 8 m, 60 + 0.5 / 2.5 x 20 = 64.
        Assert.Equal(64, SpaceSeparation.Percent(4, false, sprinklered: true), 6);
        Assert.Equal(40, SpaceSeparation.Percent(10, true, sprinklered: true) / 2, 6);
    }

    [Fact]
    public void AnOfficesSideIsJudgedByItsShareOfTheRectangleRoundItsOpenings()
    {
        // An office 20 m by 12 m, one storey: Method 2 (13.20) - Method 1 is for small residential (other) buildings.
        var office = Make("Office");
        Assert.Equal(SpaceSeparation.Way.Two, SpaceSeparation.MethodOf(office.Document)!.Value.Way);

        // Six windows 1.2 m square, sills 900, centres 2 m apart from 3 m: 8.64 m² in a rectangle 11.2 m by 1.2 m, 13.44 m² - 64.3%.
        office.Windows("south", "Casement - 1200 x 1200", 900, 3000, 5000, 7000, 9000, 11000, 13000);
        var side = SpaceSeparation.Sides(office.Document).Single(each => each.Facing == "south");
        Assert.Equal(8.64, side.Unprotected, 6);
        Assert.Equal(13.44, side.Enclosing, 3);
        Assert.Equal(64.29, side.Percent, 2);

        // 7.5 m from the boundary column 1 allows 60%.
        var problem = Assert.Single(office.Problems("south"));
        Assert.Contains("facing south 7.5 m from the property line, has 8.6 m² of windows, glazed doors and glass - 64% of the 13.4 m² rectangle enclosing it", problem.Said);
        Assert.Contains("Approved Document B Volume 2 (13.21, Table 13.1 column 1 - residential, office, assembly and recreation) allows 60%", problem.Rule);
        Assert.Contains("allows 60%", problem.Rule);
        Assert.Contains(WallRules.Problems(office.Document, office.Walls["south"], office.Document.ProjectInformation.ClearanceStandard), each => each.Rule.Contains("Table 13.1"));

        // 10 m off, 80%.
        office.Boundary(10000);
        Assert.Empty(office.Problems("south"));
        Assert.Contains("South 10.0 m: 8.6 m² open, 64% of its rectangle, 80% allowed", SpaceSeparation.Summary(office.Document));

        // A shop there has column 2's 40% - sprinklered throughout, 80 (13.22).
        office.Document.ProjectInformation.BuildingType = "Shop";
        Assert.Contains("allows 40%", Assert.Single(office.Problems("south")).Rule);
        Assert.Contains("column 2", Assert.Single(office.Problems("south")).Rule);
        office.Document.ProjectInformation.Sprinklered = true;
        Assert.Empty(office.Problems("south"));
    }

    [Fact]
    public void Method1IsForSmallBuildingsOfHomesAndSmallHotelsOnly()
    {
        Assert.Equal(SpaceSeparation.Way.One, SpaceSeparation.MethodOf(Make("House", 20000).Document)!.Value.Way);
        Assert.Equal(SpaceSeparation.Way.One, SpaceSeparation.MethodOf(Make("Hotel", 20000).Document)!.Value.Way);
        Assert.Equal(SpaceSeparation.Way.Two, SpaceSeparation.MethodOf(Make("Hotel", 30000).Document)!.Value.Way);
        Assert.Equal(SpaceSeparation.Way.Neither, SpaceSeparation.MethodOf(Make("Office", 20000, height: 10500).Document)!.Value.Way);
        Assert.Equal(SpaceSeparation.Way.Two, SpaceSeparation.MethodOf(Make("Car Park", 20000, height: 10500).Document)!.Value.Way);

        // A house 30 m long is past Method 1's 24 m (11.18): Table 11.1 at 7.5 m allows 60% - its six windows make 64%.
        var house = Make("House", 30000);
        Assert.Equal(SpaceSeparation.Way.Two, SpaceSeparation.MethodOf(house.Document)!.Value.Way);
        house.Windows("south", "Casement - 1200 x 1200", 900, 3000, 5000, 7000, 9000, 11000, 13000);
        Assert.Contains("Approved Document B (11.20, Table 11.1)", Assert.Single(house.Problems("south")).Rule);

        // Taller than 10 m: neither simple method; said, not judged.
        var tall = Make("Office", 20000, height: 10500);
        tall.Windows("south", "Casement - 1200 x 1200", 900, 3000, 5000, 7000, 9000, 11000, 13000);
        Assert.Empty(tall.Problems("south"));
        Assert.Contains("neither simple method reaches it: BR 187", SpaceSeparation.Summary(tall.Document));
    }

    [Fact]
    public void HalfOfACombustibleOutsideFaceIsUnprotectedArea()
    {
        // A house of cedar siding, class D-s2, d2 by Commission Decision 2006/213/EC, 1.5 m from the boundary to the east: its east wall
        // 6 m by 2.7 m, 16.2 m², with three windows of 1.44 m² - 4.32 m² and half the 11.88 m² of cedar, 5.94: 10.26 m² against 5.6.
        var house = Make("House", 8000, 6000, 2700, "Timber Frame - Siding 183mm");
        house.Boundary(10000, east: 1500);
        house.Windows("east", "Casement - 1200 x 1200", 900, 1000, 3000, 5000);
        Assert.Equal(10.26, SpaceSeparation.Unprotected(house.Document, house.Walls["east"]), 6);
        Assert.Contains("class D-s2, d2", SpaceSeparation.Combustible(house.Document, house.Walls["east"]));
        var problem = Assert.Single(house.Problems("east"));
        Assert.Contains("has 10.3 m² unprotected - its openings and half its Timber, Western Red Cedar, class D-s2, d2 (11.7)", problem.Said);

        // Of brick, the three windows alone, 4.32 m², are within 5.6.
        var brick = Make("House", 8000, 6000, 2700);
        brick.Boundary(10000, east: 1500);
        brick.Windows("east", "Casement - 1200 x 1200", 900, 1000, 3000, 5000);
        Assert.Empty(brick.Problems("east"));
    }

    [Fact]
    public void NearTheBoundaryOnlySmallAreasFarApartAndTheBuildingsPeriodFromBothSides()
    {
        // A house's south wall 0.8 m from the boundary: two fixed lights 0.36 m² centres 4.2 m apart are 3.6 m apart - under Diagram 11.5's 4 m.
        var house = Make("House", 8000, 6000, 2700);
        house.Boundary(800);
        house.Windows("south", "Fixed Light - 600 x 600", 1500, 1000, 5200);
        Assert.Contains("2 openings too big or too close together", Assert.Single(house.Problems("south")).Said);

        // Centres 4.7 m apart, 4.1 m clear: both disregarded.
        foreach (var window in house.Document.Elements.OfType<Window>().ToList()) house.Document.Remove(window);
        house.Windows("south", "Fixed Light - 600 x 600", 1500, 1000, 5700);
        Assert.Empty(house.Problems("south"));

        // A shop of one storey stands 60 minutes by Table B2: a 30 minute wall 0.8 m from the boundary falls short, from each side (Table B1 item 5a).
        var shop = Make("Shop", 20000, 12000, 3000, "Timber Frame - Siding 183mm");
        shop.Boundary(800);
        var rating = Assert.Single(shop.Problems("south"));
        Assert.Contains("rated 30 minutes", rating.Said);
        Assert.Contains("(13.8, Table B1 item 5a)", rating.Rule);
        Assert.Contains("60 minutes here", rating.Rule);

        // The house of the same wall stands 30: nothing said.
        var timberHouse = Make("House", 8000, 6000, 2700, "Timber Frame - Siding 183mm");
        timberHouse.Boundary(800);
        Assert.Empty(timberHouse.Problems("south"));
    }

    [Fact]
    public void AProtectedStairwaysWindowsAreLeftOut()
    {
        // An office 1.5 m from the boundary to the west, a stair at its west end behind a 30 minute partition and a 30 minute door.
        var office = Make("Office");
        office.Boundary(7500, west: 1500);
        var partition = new Wall
        {
            Start = new Point2D(4000, 0), End = new Point2D(4000, 12000), LevelId = office.Level, UnconnectedHeight = 3000,
            TypeId = office.Document.TypesOf<WallType>().Single(each => each.Name == "Interior - Partition 125mm").Id
        };
        office.Document.Add(partition);
        office.Document.Add(new Door { HostWallId = partition.Id, LevelId = office.Level, DistanceAlongWall = 6000, TypeId = office.Document.TypesOf<DoorType>().Single(each => each.Name == "Single - 1000 x 2100").Id });
        var stair = new Room { LevelId = office.Level, Location = new Point2D(2000, 6000), Name = "Stair" };
        office.Document.Add(stair);
        office.Document.Add(new Room { LevelId = office.Level, Location = new Point2D(12000, 6000), Name = "Office" });

        // Two windows in its west wall: 2.88 m² in a rectangle 5.2 m by 1.2 m, 46% - where 1.5 m from the boundary allows 8 + 0.5 / 1.5 x 12 = 12%.
        office.Windows("west", "Casement - 1200 x 1200", 900, 4000, 8000);
        Assert.Empty(office.Problems("west"));

        // A store there instead: its windows count.
        stair.Name = "Store";
        var problem = Assert.Single(office.Problems("west"));
        Assert.Contains("46% of the 6.2 m² rectangle", problem.Said);
        Assert.Contains("allows 12%", problem.Rule);
    }
}

/// <summary>The library as a schedule reads it: no two types of a category share a type mark (the audit of 23 November 2026 found P15 twice).</summary>
public class LibraryTypeMarkTests
{
    [Fact]
    public void NoTwoTypesOfACategoryShareATypeMark()
    {
        var document = BimDocument.CreateDefault();
        var types = document.TypesOf<BIMDesigner.Core.Elements.ElementType>().ToList();
        Assert.Contains(types, type => type.Name == "Stop Valve - Incoming Main" && type.TypeMark == "P22");
        Assert.Contains(types, type => type.Name == "Shower - Electric 9.5 kW" && type.TypeMark == "P15");
        var shared = types.Where(type => type.TypeMark.Length > 0)
            .GroupBy(type => (type.Category, type.TypeMark)).Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Category} {group.Key.TypeMark}: {string.Join(", ", group.Select(type => type.Name))}").ToList();
        Assert.Empty(shared);
    }
}
