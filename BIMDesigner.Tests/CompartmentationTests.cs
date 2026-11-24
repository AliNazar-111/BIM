using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Compartmentation finished (round 134), by Approved Document B Volume 2 Section 8 with Tables 8.1, B1 and C1: storage buildings' compartments
/// by area, height and volume, an ancillary upper storey (8.12), separate occupancies (8.11e, 8.30), protected shafts' walls and doors (8.6,
/// 8.33, Table C1 item 2) and the services in them (8.36, 8.37).
/// </summary>
[Collection("Wpf")]
public class CompartmentationTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Partition = "Interior - Partition 125mm", FireDoor = "Fire Door FD30S - 900 x 2100", Rated = "Floor - Screed on Slab 250mm";

    private static Wall Wall(BimDocument document, Guid level, double x0, double y0, double x1, double y1, string type = Partition)
    {
        var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static List<Wall> Box(BimDocument document, Guid level, double width, double depth) => new()
    {
        Wall(document, level, 0, 0, width, 0, Brick), Wall(document, level, width, 0, width, depth, Brick), Wall(document, level, width, depth, 0, depth, Brick), Wall(document, level, 0, depth, 0, 0, Brick)
    };

    private static Door Door(BimDocument document, Wall wall, double along, string type = FireDoor)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y, string occupancy = "")
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name, Occupancy = occupancy };
        document.Add(room);
        return room;
    }

    private static Floor Slab(BimDocument document, Guid level, double x0, double y0, double x1, double y1)
    {
        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First(each => each.Name == Rated).Id };
        floor.SetBoundary(new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) });
        document.Add(floor);
        return floor;
    }

    /// <summary>A document of so many storeys so far apart, any other level put above them all.</summary>
    private static (BimDocument Document, List<Level> Levels) Storeys(string use, int storeys, double height)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        levels[1].Elevation = height;
        while (levels.Count < storeys)
        {
            var next = new Level { Name = $"Floor {levels.Count}", Elevation = levels.Count * height };
            document.AddLevel(next);
            levels.Add(next);
        }

        foreach (var spare in document.Levels.Where(level => !levels.Contains(level))) spare.Elevation = Math.Max(storeys, 2) * height + 3000;
        if (storeys == 1) levels[1].Elevation = height;
        return (document, levels.Take(Math.Max(storeys, 1)).ToList());
    }

    private static List<string> Said(BimDocument document) => FireCompartments.Problems(document).Select(problem => problem.Said).ToList();

    [Fact]
    public void AStorageBuildingIsCompartmentedByAreaHeightAndVolume()
    {
        // A warehouse of one storey 20 m by 10 m, 19 m to the level over it: higher than 18 m without sprinklers (Table 8.1).
        var (single, levels) = Storeys("Warehouse", 1, 19000);
        var walls = Box(single, levels[0].Id, 20000, 10000);
        Door(single, walls[0], 5000, "Single - 1000 x 2100");
        Room(single, levels[0].Id, "Warehouse", 10000, 5000);
        Assert.Contains(Said(single), text => text.Contains("19.0 m high - Approved Document B Volume 2 (8.11a, Table 8.1) allows a single storey storage building without sprinklers 20,000 m² and 18 m high"));
        single.ProjectInformation.Sprinklered = true;
        Assert.DoesNotContain(Said(single), text => text.Contains("storage building"));

        // Two storeys 40 m by 30 m, 9 m apart: each 1177 m² by 9 m, 10,600 m³; with no floor between of the period - 90 minutes - one compartment
        // of 21,200 m³, over 20,000 (Table 8.1). A floor of 90 minutes between them: two compartments, each under it.
        var (store, two) = Storeys("Warehouse", 2, 9000);
        store.AddLevel(new Level { Name = "Roof", Elevation = 18000 });
        foreach (var level in two)
        {
            var box = Box(store, level.Id, 40000, 30000);
            if (level == two[0]) Door(store, box[0], 5000, "Single - 1000 x 2100");
            Room(store, level.Id, "Warehouse", 20000, 15000);
        }

        Assert.Contains(Said(store), text => text.Contains($"m³ in one compartment over the {two[0].Name}, the {two[1].Name}") && text.Contains("(8.11a, Table 8.1) allows a storage building's compartment 20000 m³ without sprinklers"));
        Slab(store, two[1].Id, 0, 0, 40000, 30000);
        Assert.DoesNotContain(Said(store), text => text.Contains("storage building"));
    }

    [Fact]
    public void ATwoStoreyShopWithAnAncillaryUpperStoreyTakesItsGroundStoreyAsOne()
    {
        // A shop 80 m by 60 m with sprinklers, an office 20 m by 20 m over its corner reached by a stair with its own door out at its foot: the
        // ground storey of 4700 m² over 4000 for a shop of more than one storey (Table 8.1).
        var (document, levels) = Storeys("Shop", 2, 3000);
        document.ProjectInformation.Sprinklered = true;
        var ground = Box(document, levels[0].Id, 80000, 60000);
        Door(document, ground[1], 30000, "Single - 1000 x 2100");
        Wall(document, levels[0].Id, 3000, 0, 3000, 6000);
        Wall(document, levels[0].Id, 0, 6000, 3000, 6000);
        Door(document, ground[0], 1500, "Single - 1000 x 2100");
        Room(document, levels[0].Id, "Stair", 1500, 5000);
        Room(document, levels[0].Id, "Sales Area", 40000, 30000);
        var upper = new[] { Wall(document, levels[1].Id, 0, 0, 20000, 0, Brick), Wall(document, levels[1].Id, 20000, 0, 20000, 20000, Brick), Wall(document, levels[1].Id, 20000, 20000, 0, 20000, Brick), Wall(document, levels[1].Id, 0, 20000, 0, 0, Brick) };
        var stairWall = Wall(document, levels[1].Id, 3000, 0, 3000, 6000);
        Wall(document, levels[1].Id, 0, 6000, 3000, 6000);
        Door(document, stairWall, 3000);
        Room(document, levels[1].Id, "Stair", 1500, 5000);
        Room(document, levels[1].Id, "Office", 10000, 10000);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = levels[0].Id, TopLevelId = levels[1].Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
        Assert.NotNull(upper);
        Assert.Contains(Said(document), text => text.Contains("(8.11a, Table 8.1) allows 4000 m² a storey of a compartment"));

        // Its office floor of 90 minutes, compartmenting it from the shop: an ancillary upper storey, the ground storey a single storey (8.12).
        Slab(document, levels[1].Id, 0, 0, 20000, 20000);
        Assert.DoesNotContain(Said(document), text => text.Contains("(8.11a, Table 8.1)"));
    }

    [Fact]
    public void SeparateOccupanciesAreCompartmented()
    {
        // A shop 20 m by 10 m let as two units, A Ltd's and B Ltd's, a 30 minute partition between with an FD30S door (8.11e, 8.30).
        var (document, levels) = Storeys("Shop", 2, 3000);
        var walls = Box(document, levels[0].Id, 20000, 10000);
        Door(document, walls[0], 5000, "Single - 1000 x 2100");
        Door(document, walls[0], 15000, "Single - 1000 x 2100");
        var party = Wall(document, levels[0].Id, 10000, 0, 10000, 10000);
        var door = Door(document, party, 5000);
        var a = Room(document, levels[0].Id, "Unit 1", 5000, 5000, "A Ltd");
        var b = Room(document, levels[0].Id, "Unit 2", 15000, 5000, "B Ltd");
        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.Contains(WallRules.Problems(document, party, standard), problem => problem.Said == "it divides the Unit 1 (A Ltd) from the Unit 2 (B Ltd), separate occupancies, rated 30 minutes." || problem.Said == "it divides the Unit 2 (B Ltd) from the Unit 1 (A Ltd), separate occupancies, rated 30 minutes.");
        Assert.Contains(WallRules.Problems(document, party, standard), problem => problem.Rule.StartsWith("Approved Document B Volume 2 (8.11e, Table B1 item 6b) asks a compartment wall of REI 60", StringComparison.Ordinal));
        Assert.Contains(Doors.Problems(document, door, standard), problem => problem.Rule.StartsWith("Approved Document B Volume 2 (8.30, Table C1 item 2e)", StringComparison.Ordinal));

        // One tenant: nothing asked.
        b.Occupancy = "A Ltd";
        Assert.DoesNotContain(WallRules.Problems(document, party, standard), problem => problem.Rule.Contains("(8.11e"));
        Assert.DoesNotContain(Doors.Problems(document, door, standard), problem => problem.Rule.Contains("(8.30"));

        // B Ltd upstairs over A Ltd: the floor between a compartment floor (8.11e).
        b.Occupancy = "B Ltd";
        Box(document, levels[1].Id, 20000, 10000);
        Room(document, levels[1].Id, "Upper Unit", 10000, 5000, "B Ltd");
        var floor = Slab(document, levels[1].Id, 0, 0, 20000, 10000);
        Assert.Equal("a floor between separate occupancies - the Upper Unit (B Ltd) over the Unit 1 (A Ltd) - a compartment floor (8.11e)", FireCompartments.CompartmentFloor(document, floor));
        Assert.NotNull(a);
    }

    /// <summary>
    /// A hotel of three storeys 14 m by 10 m, 6 m to its top floor - every floor a compartment floor (8.10), 60 minutes: a stair at the west end
    /// behind a 30 minute partition, a corridor along the south, a room north of it, and a service riser 2 m by 2 m in the north-east corner with
    /// a louvred door - each stair and riser passing through the compartment floors, protected shafts.
    /// </summary>
    private static (BimDocument Document, List<Level> Levels, List<Room> Risers, List<Door> RiserDoors) Hotel()
    {
        var (document, levels) = Storeys("Hotel", 3, 3000);
        var (risers, riserDoors) = (new List<Room>(), new List<Door>());
        foreach (var level in levels)
        {
            var box = Box(document, level.Id, 14000, 10000);
            var stairWall = Wall(document, level.Id, 3000, 0, 3000, 10000);
            var corridorWall = Wall(document, level.Id, 3000, 2000, 14000, 2000);
            Door(document, stairWall, 1000);
            Door(document, corridorWall, 5500);
            if (level == levels[0]) Door(document, box[1], 1000, "Single - 1000 x 2100");
            var riserFront = Wall(document, level.Id, 12000, 8000, 14000, 8000);
            Wall(document, level.Id, 12000, 8000, 12000, 10000);
            riserDoors.Add(Door(document, riserFront, 1000, "Bi-fold Louvred - 1600 x 2100"));
            Room(document, level.Id, "Stair", 1500, 8000);
            Room(document, level.Id, "Lounge", 7000, 6000);
            Room(document, level.Id, "Corridor", 8500, 1000);
            risers.Add(Room(document, level.Id, "Riser", 13000, 9000));
        }

        foreach (var (foot, head) in levels.Zip(levels.Skip(1)))
            document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = foot.Id, TopLevelId = head.Id, Start = new Point2D(1500, 2600), Angle = 90, Width = 900 });
        return (document, levels, risers, riserDoors);
    }

    [Fact]
    public void AProtectedShaftsWallsStandTheBuildingsPeriodAndARisersDoorsHalfOfIt()
    {
        var (document, levels, risers, riserDoors) = Hotel();
        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.Equal(60, FireCompartments.Minutes(document));
        var shafts = FireCompartments.Shafts(document);
        Assert.Equal("service", shafts[risers[1]]);
        Assert.Contains(shafts, entry => entry.Value == "stair");

        // The stair's 30 minute partition to the lounge: the shaft's enclosure asks 60 (Table B1 item 8c).
        var stairWall = document.Walls.First(wall => wall.LevelId == levels[1].Id && wall.Start == new Point2D(3000, 0));
        Assert.Contains(WallRules.Problems(document, stairWall, standard), problem => problem.Said.StartsWith("it encloses the Stair, a protected shaft through a compartment floor", StringComparison.Ordinal)
                                                                                  && problem.Rule.StartsWith("Approved Document B Volume 2 (8.6, 8.33, Table B1 item 8c) asks the enclosure of a protected shaft - a stair", StringComparison.Ordinal));

        // The riser's walls the same, its louvred door half the period, 30 minutes (Table C1 item 2d).
        var riserFront = document.Walls.First(wall => wall.LevelId == levels[1].Id && wall.Start == new Point2D(12000, 8000) && wall.End == new Point2D(14000, 8000));
        Assert.Contains(WallRules.Problems(document, riserFront, standard), problem => problem.Said.StartsWith("it encloses the Riser, a protected shaft through a compartment floor", StringComparison.Ordinal));
        Assert.Contains(Doors.Problems(document, riserDoors[1], standard), problem => problem.Said == "it opens into the Riser, a protected shaft through a compartment floor, not fire-rated."
                                                                                     && problem.Rule.StartsWith("Approved Document B Volume 2 (Table C1 item 2d)", StringComparison.Ordinal) && problem.Rule.EndsWith("E 30.", StringComparison.Ordinal));
        riserDoors[1].TypeId = document.TypesOf<DoorType>().First(each => each.Name == FireDoor).Id;
        Assert.DoesNotContain(Doors.Problems(document, riserDoors[1], standard), problem => problem.Rule.Contains("item 2d"));
    }

    [Fact]
    public void NoVentilatingDuctInAStairsShaftAndAGasPipeInAShaftOfSteel()
    {
        var (document, levels, _, _) = Hotel();
        Assert.Empty(FireCompartments.ServiceProblems(document));

        // A supply air duct across the stair on the first floor (8.36b).
        var duct = new Duct { Start = new Point2D(300, 7500), End = new Point2D(2700, 7500), LevelId = levels[1].Id, StartOffset = 2600, EndOffset = 2600, TypeId = document.TypesOf<DuctType>().First().Id, Kind = SystemKind.SupplyAir };
        document.Add(duct);
        Assert.Contains(FireCompartments.ServiceProblems(document), problem => problem.Element == duct && problem.Text.StartsWith("it is a ventilating duct in the Stair, a protected shaft holding a stair - Approved Document B Volume 2 (8.36b)", StringComparison.Ordinal));
        document.Remove(duct);

        // A copper gas pipe up the riser (8.37a); of galvanised steel, met.
        var pipe = new Pipe { Start = new Point2D(13000, 9000), End = new Point2D(13000, 9000), LevelId = levels[0].Id, StartOffset = 100, EndOffset = 5900, Kind = SystemKind.Gas, Diameter = 28,
                              TypeId = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.Copper).Id };
        document.Add(pipe);
        Assert.Contains(FireCompartments.ServiceProblems(document), problem => problem.Element == pipe && problem.Text.Contains("in the Riser, a protected shaft - Approved Document B Volume 2 (8.37a)"));
        pipe.TypeId = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.GalvanisedSteel).Id;
        Assert.Empty(FireCompartments.ServiceProblems(document));
    }
}
