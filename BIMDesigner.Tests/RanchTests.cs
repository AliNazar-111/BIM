using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// A US ranch house, one storey with its garage attached, by the 2021 IRC and the NEC (round 152): the 1-hour wall a builder takes between the
/// house and its garage parts no dwelling units, nor does a 1-hour floor over a garage, which asks 5/8 in Type X gypsum board under the habitable
/// rooms on it (Table R302.6); a window is a hazard only by a bath or shower in its own room (R308.4.5); a hallway 3 ft wide (R311.6); and the
/// receptacles each space asks - living spaces by 210.52(A), a hallway 10 ft long one by (H), a garage one by (G)(1) - and lights by 210.70(A).
/// </summary>
[Collection("Wpf")]
public class RanchTests
{
    private const string Siding = "Timber Frame - Siding 183mm", OneHour = "Unit Separation Wall - 2x4 Studs, 5/8 in Type X Each Side, 1 hr",
        Partition = "Interior - Partition 125mm", Timber = "Floor - Timber 220mm", UnitFloor = "Floor - 2x10 Joists, 5/8 in Type X Ceiling, 1 hr";

    private static Guid TypeOf<T>(BimDocument document, string name) where T : ElementType => document.TypesOf<T>().First(each => each.Name == name).Id;

    private static BimDocument Us(string use = "Single-family house")
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BuildingType = use;
        return document;
    }

    private static Wall Wall(BimDocument document, Core.Datums.Level level, double x0, double y0, double x1, double y1, string type, double height = 2700)
    {
        var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = TypeOf<WallType>(document, type), LevelId = level.Id, UnconnectedHeight = height };
        document.Add(wall);
        return wall;
    }

    /// <summary>Four walls round a rectangle on a storey, of a type, south, east, north, west.</summary>
    private static List<Wall> Box(BimDocument document, Core.Datums.Level level, double x0, double y0, double x1, double y1, string type, double height = 2700) =>
        new()
        {
            Wall(document, level, x0, y0, x1, y0, type, height), Wall(document, level, x1, y0, x1, y1, type, height),
            Wall(document, level, x1, y1, x0, y1, type, height), Wall(document, level, x0, y1, x0, y0, type, height)
        };

    private static Room Room(BimDocument document, Core.Datums.Level level, string name, double x, double y)
    {
        var room = new Room { LevelId = level.Id, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    private static Component Put(BimDocument document, string name, double x, double y, double elevation = 0)
    {
        var type = document.TypesOf<ComponentType>().First(each => each.Name == name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = elevation };
        ComponentPlacing.FitToWalls(document, piece);
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void TheOneHourWallToTheGarageIsNoWallBetweenUnits()
    {
        // The house 10 m by 8 m, its garage 6 m on to the east, the wall between them the 1-hour unit separation wall a builder takes for it.
        var document = Us();
        var ground = document.Levels.OrderBy(level => level.Elevation).First();
        Wall(document, ground, 0, 0, 16000, 0, Siding);
        Wall(document, ground, 16000, 0, 16000, 8000, Siding);
        Wall(document, ground, 16000, 8000, 0, 8000, Siding);
        Wall(document, ground, 0, 8000, 0, 0, Siding);
        var between = Wall(document, ground, 10000, 0, 10000, 8000, OneHour);
        Room(document, ground, "Living", 5000, 4000);
        Room(document, ground, "Garage", 13000, 4000);

        // A single-family house is one dwelling, whatever its walls: nothing of R302.2 or R302.3 asked of the wall, its gypsum the board R302.6 asks.
        Assert.Equal(UnitKind.None, UnitSeparation.Kind(document));
        Assert.False(UnitSeparation.Separates(document, between));
        Assert.DoesNotContain(WallRules.Problems(document, between, ClearanceStandard.Irc), problem => problem.Rule.Contains("R302"));

        // Named only a house, the garage wall still makes it no two-family dwelling - it parts the house from its garage, not two units.
        document.ProjectInformation.BuildingType = "House";
        Assert.Equal(UnitKind.None, UnitSeparation.Kind(document));
        Assert.False(UnitSeparation.Separates(document, between));

        // A duplex is two units by its name - but the wall to the garage still parts none of them.
        document.ProjectInformation.BuildingType = "Duplex";
        Assert.Equal(UnitKind.TwoFamily, UnitSeparation.Kind(document));
        Assert.False(UnitSeparation.Separates(document, between));

        // An ordinary partition to the garage, ungypsumed, is said by R302.6, as before.
        document.ProjectInformation.BuildingType = "Single-family house";
        var bare = new WallType("Studs Only", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, document.Materials.First(material => material.Name == "Timber, Softwood").Id, 89)))
        {
            Function = WallFunction.Interior
        };
        document.AddType(bare);
        between.TypeId = bare.Id;
        Assert.Contains(WallRules.Problems(document, between, ClearanceStandard.Irc), problem => problem.Rule.Contains("R302.6"));
    }

    [Fact]
    public void AFloorOverAGarageWantsTypeXUnderTheHabitableRoomsOnItAndPartsNoUnits()
    {
        // A garage 6 m by 7 m, a bedroom over it.
        var document = Us("House");
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        var height = levels[1].Elevation - levels[0].Elevation;
        Box(document, levels[0], 0, 0, 6000, 7000, Siding, height);
        Box(document, levels[1], 0, 0, 6000, 7000, Siding);
        Room(document, levels[0], "Garage", 3000, 3500);
        var bedroom = Room(document, levels[1], "Bedroom", 3000, 3500);
        var floor = new Floor { LevelId = levels[1].Id, TypeId = TypeOf<FloorType>(document, Timber) };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 7000), new Point2D(0, 7000) });
        document.Add(floor);

        // A timber floor with plasterboard under it: no 5/8 in Type X between the garage and the bedroom (Table R302.6).
        var said = Assert.Single(SlabRules.Problems(document, floor, ClearanceStandard.Irc), problem => problem.Rule.Contains("R302.6"));
        Assert.StartsWith("it is the floor of Bedroom over a garage, with no 5/8 in", said.Said);

        // The 1-hour floor with its 5/8 in Type X ceiling: what R302.6 asks - and no floor between dwelling units for it.
        floor.TypeId = TypeOf<FloorType>(document, UnitFloor);
        Assert.Equal(UnitKind.None, UnitSeparation.Kind(document));
        Assert.False(UnitSeparation.Separates(document, floor));
        Assert.DoesNotContain(SlabRules.Problems(document, floor, ClearanceStandard.Irc), problem => problem.Rule.Contains("R302"));

        // The timber floor again, a ceiling of 5/8 in Type X hung in the garage: met as well.
        floor.TypeId = TypeOf<FloorType>(document, Timber);
        var typeX = new CeilingType("Ceiling - Type X", new CompoundStructure(new MaterialLayer(LayerFunction.Finish2, document.Materials.First(material => material.Name == "Gypsum Board, Type X 5/8 in").Id, 15.9)));
        document.AddType(typeX);
        var ceiling = new Ceiling { LevelId = levels[0].Id, TypeId = typeX.Id, HeightOffset = height - 300 };
        ceiling.SetBoundary(new[] { new Point2D(100, 100), new Point2D(5900, 100), new Point2D(5900, 6900), new Point2D(100, 6900) });
        document.Add(ceiling);
        Assert.DoesNotContain(SlabRules.Problems(document, floor, ClearanceStandard.Irc), problem => problem.Rule.Contains("R302.6"));

        // A store over the garage, no habitable room: nothing asked.
        document.Remove(ceiling);
        bedroom.Name = "Storage";
        Assert.DoesNotContain(SlabRules.Problems(document, floor, ClearanceStandard.Irc), problem => problem.Rule.Contains("R302.6"));
    }

    [Fact]
    public void AWindowIsAHazardOnlyByABathOrShowerInItsOwnRoom()
    {
        // A bedroom and a bathroom side by side, 3 m each; the bath along the north wall in the bathroom, hard against the partition.
        var document = Us();
        var ground = document.Levels.OrderBy(level => level.Elevation).First();
        var walls = Box(document, ground, 0, 0, 6000, 3000, Siding);
        Wall(document, ground, 3000, 0, 3000, 3000, Partition);
        Room(document, ground, "Bedroom", 1500, 1500);
        Room(document, ground, "Bathroom", 4500, 1500);
        Put(document, "Bath - 1700 x 700", 3950, 2550);

        // The bedroom's casement, 24 in from the partition, its glass under 60 in: the bath is a wall away, not beside it (R308.4.5).
        var north = walls[2];
        var casement = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 24 x 48 in");
        var window = new Window { TypeId = casement.Id, LevelId = ground.Id, HostWallId = north.Id, DistanceAlongWall = 6000 - 2400, SillHeight = 900 };
        document.Add(window);
        Assert.DoesNotContain(WindowRules.Problems(document, window, ClearanceStandard.Irc), problem => problem.Rule.Contains("R308.4.5"));

        // In the bathroom's wall, over the bath: safety glazing.
        window.DistanceAlongWall = 6000 - 4500;
        Assert.Contains(WindowRules.Problems(document, window, ClearanceStandard.Irc), problem => problem.Rule.Contains("R308.4.5"));
        casement.SafetyGlazing = true;
        Assert.DoesNotContain(WindowRules.Problems(document, window, ClearanceStandard.Irc), problem => problem.Rule.Contains("R308.4.5"));
    }

    [Fact]
    public void AHallwayIs3FtWideAtLeast()
    {
        // A hall 5 m long, its partitions' faces 800 apart: short of 3 ft (R311.6); 1000 apart, wide enough.
        var document = Us();
        var ground = document.Levels.OrderBy(level => level.Elevation).First();
        var walls = Box(document, ground, -62.5, -62.5, 5062.5, 862.5, Partition);
        var hall = Room(document, ground, "Hall", 2500, 400);
        Assert.Contains("the IRC (R311.6) asks a hallway 3 ft", Circulation.Verdict(document, hall));

        walls[2].Start = new Point2D(5062.5, 1062.5);
        walls[2].End = new Point2D(-62.5, 1062.5);
        walls[1].End = new Point2D(5062.5, 1062.5);
        walls[3].Start = new Point2D(-62.5, 1062.5);
        Assert.DoesNotContain("R311.6", Circulation.Verdict(document, hall) ?? string.Empty);

        // A bedroom is no hallway, however narrow.
        hall.Name = "Bedroom";
        Assert.Null(Circulation.Verdict(document, hall));
    }

    [Fact]
    public void EachSpaceHasTheReceptaclesAndLightsTheNecAsksOfIt()
    {
        // A hall 1 m by 5 m and a garage 6 m by 6 m, neither with a light or a receptacle.
        var document = Us();
        var ground = document.Levels.OrderBy(level => level.Elevation).First();
        Box(document, ground, 0, 0, 5000, 1000, Partition);
        Box(document, ground, 0, 2000, 6000, 8000, Siding);
        var hall = Room(document, ground, "Hall", 2500, 500);
        var garage = Room(document, ground, "Garage", 3000, 5000);

        // The hallway: not a living space, so no wall space asked by 210.52(A) - one receptacle, as it is 10 ft long or more (H); a light (210.70(A)(2)).
        var hallSaid = Services.Verdict(document, hall)!;
        Assert.DoesNotContain("210.52(A)", hallSaid);
        Assert.Contains("NEC 210.52(H)", hallSaid);
        Assert.Contains("NEC 210.70(A)(2)", hallSaid);

        // The garage: one receptacle where its cars park (210.52(G)(1)) and a light.
        var garageSaid = Services.Verdict(document, garage)!;
        Assert.DoesNotContain("210.52(A)", garageSaid);
        Assert.Contains("NEC 210.52(G)(1)", garageSaid);
        Assert.Contains("NEC 210.70(A)(2)", garageSaid);

        // A receptacle in each, and a light: nothing of the NEC said.
        Put(document, "Receptacle - Duplex 15 A", 2500, 900, 300);
        Put(document, "Receptacle - Duplex 15 A", 5850, 5000, 300);
        Put(document, "Ceiling Light - Flush", 2500, 500, 2400);
        Put(document, "Ceiling Light - Flush", 3000, 5000, 2400);
        Assert.DoesNotContain("NEC", Services.Verdict(document, hall) ?? string.Empty);
        Assert.DoesNotContain("NEC", Services.Verdict(document, garage) ?? string.Empty);

        // A hall shorter than 10 ft asks no receptacle; a closet asks no light.
        var shortHall = Room(document, ground, "Hallway", 1000, 10000);
        Box(document, ground, 0, 9500, 2500, 10500, Partition);
        Assert.DoesNotContain("210.52(H)", Services.Verdict(document, shortHall) ?? string.Empty);
        var closet = Room(document, ground, "Closet", 1000, 12500);
        Box(document, ground, 0, 12000, 2000, 13000, Partition);
        Assert.DoesNotContain("210.70", Services.Verdict(document, closet) ?? string.Empty);
    }
}
