using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Residential care homes (round 129), by Approved Document B Volume 2 2.33 to 2.46 and Table C1: progressive horizontal evacuation - three
/// protected areas a storey, two exits each, the walk to the next and to a storey exit, ten beds - bedrooms and ancillary accommodation enclosed,
/// one bed a bedroom, escape not through ancillary rooms, L1 detection.
/// </summary>
public class CareHomeTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";
    private const string Partition = "Interior - Partition 125mm";
    private const string Compartment = "Separating Wall - Dense Blockwork 215mm";

    /// <summary>
    /// A care home of three protected areas side by side, each <paramref name="span"/> long and 10 m deep: a corridor 2 m wide along the middle,
    /// three bedrooms either side of it, a compartment wall of 60 minutes between the areas with an FD60S door across the corridor, and final exits
    /// at the corridor's two ends - the east one left out where <paramref name="eastExit"/> is false.
    /// </summary>
    private static (BimDocument Document, List<Room> Bedrooms, List<Room> Corridors, List<Wall> Compartments, List<Door> Across, List<Door> Bedroom) CareHome(double span = 12000, bool eastExit = true)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Care Home";
        var level = document.Levels[0].Id;
        Guid Type(string name) => document.TypesOf<WallType>().First(each => each.Name == name).Id;
        Wall Wall(double x0, double y0, double x1, double y1, string type)
        {
            var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = Type(type), LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            return wall;
        }

        Door Door(Wall wall, double along, string type)
        {
            var door = new Door { HostWallId = wall.Id, DistanceAlongWall = along, LevelId = level, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id };
            document.Add(door);
            return door;
        }

        var length = 3 * span;
        var outside = new[] { Wall(0, 0, length, 0, Brick), Wall(length, 0, length, 10000, Brick), Wall(length, 10000, 0, 10000, Brick), Wall(0, 10000, 0, 0, Brick) };
        var exterior = document.TypesOf<DoorType>().First(each => each.Function == DoorFunction.Exterior).Name;
        Door(outside[3], 5000, exterior);
        if (eastExit) Door(outside[1], 5000, exterior);

        var compartments = new List<Wall>();
        var across = new List<Door>();
        foreach (var x in new[] { span, 2 * span })
        {
            var wall = Wall(x, 0, x, 10000, Compartment);
            compartments.Add(wall);
            across.Add(Door(wall, 5000, "Fire Door FD60S - 1000 x 2100"));
        }

        var bedrooms = new List<Room>();
        var corridors = new List<Room>();
        var bedroomDoors = new List<Door>();
        var width = span / 3;
        for (var area = 0; area < 3; area++)
        {
            var a = area * span;
            var south = Wall(a, 4000, a + span, 4000, Partition);
            var north = Wall(a, 6000, a + span, 6000, Partition);
            for (var k = 1; k < 3; k++)
            {
                Wall(a + k * width, 0, a + k * width, 4000, Partition);
                Wall(a + k * width, 6000, a + k * width, 10000, Partition);
            }

            var corridor = new Room { LevelId = level, Location = new Point2D(a + span / 2, 5000), Name = "Corridor", Number = $"C{area + 1}" };
            document.Add(corridor);
            corridors.Add(corridor);
            for (var k = 0; k < 3; k++)
            {
                foreach (var (y, wall) in new[] { (2000.0, south), (8000.0, north) })
                {
                    var bedroom = new Room { LevelId = level, Location = new Point2D(a + (k + 0.5) * width, y), Name = "Bedroom", Number = $"{bedrooms.Count + 1}" };
                    document.Add(bedroom);
                    bedrooms.Add(bedroom);
                    bedroomDoors.Add(Door(wall, (k + 0.5) * width, "Fire Door FD30S - 900 x 2100"));
                }
            }
        }

        return (document, bedrooms, corridors, compartments, across, bedroomDoors);
    }

    private static List<string> Said(BimDocument document) => CareHomes.Problems(document).Select(problem => $"{problem.Room.Name} {problem.Room.Number}: {problem.Said}").ToList();

    [Fact]
    public void ACareHomeIsThreeProtectedAreasEachWithTwoExitsAndTenBedsAtMost()
    {
        var (home, bedrooms, corridors, compartments, across, bedroomDoors) = CareHome();
        Assert.True(CareHomes.Applies(home));
        var areas = CareHomes.Areas(home, home.Levels[0].Id);
        Assert.Equal(new[] { 7, 7, 7 }, areas.Select(area => area.Count));
        Assert.Empty(Said(home));
        Assert.Empty(CareHomes.Problems(home, across[0], home.FindType<DoorType>(across[0].TypeId)!));

        // A hospital is Firecode's; a hotel no care home.
        home.ProjectInformation.BuildingType = "Hospital";
        Assert.False(CareHomes.Applies(home));
        home.ProjectInformation.BuildingType = "Nursing Home";
        Assert.True(CareHomes.Applies(home));

        // The east compartment wall a plain partition: two protected areas, the east one of twelve bedrooms.
        compartments[1].TypeId = home.TypesOf<WallType>().First(each => each.Name == Partition).Id;
        var said = Said(home);
        Assert.Contains(said, text => text.StartsWith("Bedroom 1: its storey is 2 protected areas - Approved Document B Volume 2 (2.36) asks each storey", StringComparison.Ordinal));
        Assert.Contains(said, text => text.StartsWith("Bedroom 7: its protected area - Bedroom 7, Bedroom 8, Bedroom 9 and 9 more - has 12 residents' beds - Approved Document B Volume 2 (2.39) allows 10 at most", StringComparison.Ordinal));
        compartments[1].TypeId = home.TypesOf<WallType>().First(each => each.Name == Compartment).Id;

        // The door across the corridor in the compartment wall only FD30S: Table C1 item 2e asks it as long as the wall, with smoke seals.
        across[0].TypeId = home.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100").Id;
        var (doorSaid, doorRule) = Assert.Single(CareHomes.Problems(home, across[0], home.FindType<DoorType>(across[0].TypeId)!));
        Assert.Equal("it is in the compartment wall between two protected areas, rated 30 minutes.", doorSaid);
        Assert.StartsWith("Approved Document B Volume 2 (Table C1 item 2e, 2.35) asks a door in a compartment wall as long as the wall - 60 minutes - with smoke seals", doorRule);

        // A bedroom's door not fire-rated (2.42); a bedroom's wall not either.
        var plain = home.TypesOf<DoorType>().First(each => each.Name == "Single - 900 x 2100");
        plain.FireRating = string.Empty;
        bedroomDoors[0].TypeId = plain.Id;
        Assert.Equal("it is the door of the Bedroom, a care home's bedroom, not fire-rated.", Assert.Single(CareHomes.Problems(home, bedroomDoors[0], plain)).Said);
        var partition = home.TypesOf<WallType>().First(each => each.Name == Partition);
        partition.FireRating = string.Empty;
        var bedroomWall = home.Walls.First(wall => wall.Start == new Point2D(4000, 0));
        var (wallSaid, wallRule) = Assert.Single(CareHomes.Problems(home, bedroomWall, partition));
        Assert.Equal("it encloses the Bedroom, a care home's bedroom, not fire-rated.", wallSaid);
        Assert.StartsWith("Approved Document B Volume 2 (2.42) asks each bedroom of a care home enclosed in fire-resisting construction, REI 30", wallRule);
    }

    [Fact]
    public void ACareHomesWalkIsToTheNextProtectedAreaWithin18MetresAndToAStoreyExitWithin64()
    {
        // 12 m areas: the corridor's far end 12 m from the exit into the next area at most - within Table 2.1's 18 m (2.37 a).
        var (home, _, corridors, _, _, _) = CareHome();
        var verdict = HorizontalEscape.Verdict(home, corridors[1])!;
        Assert.Contains("m from the next protected area or a storey exit at most", verdict);
        Assert.DoesNotContain("(2.37", verdict);

        // 25 m areas, the only final exit at the west end: the east area 75 m long from it, its far end 25 m from the door into the middle area.
        (home, _, corridors, _, _, _) = CareHome(25000, eastExit: false);
        verdict = HorizontalEscape.Verdict(home, corridors[2])!;
        Assert.Contains("from the exit into the next protected area, walked - Approved Document B Volume 2 (2.37 a, Table 2.1) allows 9 m in one direction only and 18 m", verdict);
        Assert.Contains("m from a storey exit, walked - Approved Document B Volume 2 (2.37 b) allows 64 m from any point of a care home", verdict);

        // The east area has one exit only now: into the middle area (2.37, Diagram 2.11).
        Assert.Contains(Said(home), text => text.StartsWith("Bedroom 13: its protected area - Bedroom 13, Bedroom 14, Bedroom 15 and 4 more - has one exit - Approved Document B Volume 2 (2.37, Diagram 2.11)", StringComparison.Ordinal));
    }

    [Fact]
    public void BedroomsHoldOneBedAncillaryRoomsAreEnclosedAndNotPassedThroughAndDetectionIsL1()
    {
        var (home, bedrooms, corridors, _, _, bedroomDoors) = CareHome();
        var level = home.Levels[0].Id;

        // Two beds in a bedroom (2.43).
        foreach (var x in new[] { 1000.0, 3000 })
        {
            var bed = home.TypesOf<ComponentType>().First(each => each.Name == "Bed - Single 900 x 1900");
            home.Add(new Component { TypeId = bed.Id, TypeKind = bed.Kind, LevelId = level, Location = new Point2D(x, 1500) });
        }

        Assert.Contains(Said(home), text => text.StartsWith("Bedroom 1: 2 beds in it - Approved Document B Volume 2 (2.43)", StringComparison.Ordinal));

        // The second bedroom a linen store: its plain door wants E 30 (Table C1 item 10b).
        bedrooms[1].Name = "Linen Store";
        var plain = home.TypesOf<DoorType>().First(each => each.Name == "Single - 900 x 2100");
        plain.FireRating = string.Empty;
        bedroomDoors[1].TypeId = plain.Id;
        var (said, rule) = Assert.Single(CareHomes.Problems(home, bedroomDoors[1], plain));
        Assert.Equal("it is the door of the Linen Store, ancillary accommodation of a care home, not fire-rated.", said);
        Assert.StartsWith("Approved Document B Volume 2 (2.44, Table C1 item 10b)", rule);

        // The west area's corridor a day room: the bedrooms' escape passes through it (2.38).
        corridors[0].Name = "Day Room";
        Assert.Contains(Said(home), text => text.StartsWith("Day Room C1: the escape from other rooms passes through it, ancillary accommodation - Approved Document B Volume 2 (2.38)", StringComparison.Ordinal));

        // L1 detection: a bedroom with no detector in it said with the alarms (2.41); one put in, met.
        var bedroom = bedrooms[2];
        Assert.Contains("(2.41) asks a care home a fire detection and alarm system to L1 of BS 5839-1", Alarms.Verdict(home, bedroom));
        var detector = home.TypesOf<ComponentType>().First(each => each.Form == ComponentForm.SmokeDetector);
        home.Add(new Component { TypeId = detector.Id, TypeKind = detector.Kind, LevelId = level, Location = bedroom.Location, Elevation = 2700 });
        Assert.DoesNotContain("(2.41)", Alarms.Verdict(home, bedroom) ?? string.Empty);
    }
}
