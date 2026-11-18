using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Fire separation in a block of flats (round 94), by Approved Document B Volume 1 - Tables B3, B4 and C1 read off the pages: its doors,
/// walls and floors. And the ground outside a building with a basement: the ground floor's level, not the basement's.
/// </summary>
[Collection("Wpf")]
public class FireSeparationTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
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

    /// <summary>A storey of the block: its level, the wall between the stair and the rest (x 3 m), the corridor's wall with the flat (y 2 m), their doors, and its rooms.</summary>
    private sealed record Storey(Level Level, Wall StairWall, Wall FlatWall, Door StairDoor, Door FlatDoor, Room Stair, Room Corridor, Room Flat);

    /// <summary>
    /// A block of flats 12 m by 6 m on so many storeys 3 m apart, each a stair at the west end (x 0 to 3 m), a corridor along the south side
    /// (y 0 to 2 m) entered from the stair at 1 m, and a flat north of it entered at x 8 m; the stair rising north to the next storey. Plain
    /// library doors - 30 minutes, no smoke seals, no closer - and 30 minute partitions.
    /// </summary>
    private static (BimDocument Document, List<Storey> Storeys) Block(int storeys)
    {
        var document = BimDocument.CreateDefault();
        var levels = new List<Level> { document.Levels[0], document.Levels[1] };
        for (var i = 2; i < storeys; i++)
        {
            var level = new Level { Name = $"Level {i}", Elevation = 3000 * i };
            document.AddLevel(level);
            levels.Add(level);
        }

        var built = new List<Storey>();
        foreach (var level in levels.Take(storeys))
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) };
            for (var i = 0; i < 4; i++) Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick);
            var stairWall = Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 6000));
            var flatWall = Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(12000, 2000));
            built.Add(new Storey(level, stairWall, flatWall, Door(document, stairWall, 1000), Door(document, flatWall, 5000),
                Room(document, level.Id, "Stair", 1500, 3000), Room(document, level.Id, "Corridor", 7500, 1000), Room(document, level.Id, "Living Room", 7500, 4000, $"Flat {built.Count + 1}")));
        }

        for (var i = 0; i + 1 < built.Count; i++)
            document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = built[i].Level.Id, TopLevelId = built[i + 1].Level.Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
        return (document, built);
    }

    private static List<PlacementProblem> Of(BimDocument document, Door door) =>
        Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.Contains("Table C1") || problem.Rule.Contains("(3.7")).ToList();

    private static List<PlacementProblem> Of(BimDocument document, Wall wall) =>
        WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.Contains("Table B3")).ToList();

    private static List<PlacementProblem> Of(BimDocument document, Floor floor) =>
        SlabRules.Problems(document, floor, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.Contains("Approved Document B")).ToList();

    [Fact]
    public void SmokeSealsAreReadFromTheRating()
    {
        Assert.True(FireSeparation.Smoke("30 min (FD30S)"));
        Assert.True(FireSeparation.Smoke("FD 30 S"));
        Assert.True(FireSeparation.Smoke("E 30 Sa"));
        Assert.True(FireSeparation.Smoke("E30Sa"));
        Assert.True(FireSeparation.Smoke("FD 30 Sa"));
        Assert.False(FireSeparation.Smoke("30 min"));
        Assert.False(FireSeparation.Smoke("FD30"));
        Assert.False(FireSeparation.Smoke("FD30 Steel"));
    }

    [Fact]
    public void ABlocksDoorsAreFireDoorsWithSealsAndClosers()
    {
        // Five storeys, 12 m up. The plain doors: 30 minutes, but no smoke seals and no closer - a flat's entrance (2a) and the stair's (2b).
        var (document, storeys) = Block(5);
        var third = storeys[2];
        Assert.Equal(("it is Flat 3's entrance door from the Corridor, rated 30 minutes without smoke seals (no S) and with no closer.",
                "Approved Document B (Table C1 item 2a, C5) asks a door in the compartment wall between a flat and the rest of the building for E 30 Sa - FD 30S - and self-closing."),
            Of(document, third.FlatDoor).Select(problem => (problem.Said, problem.Rule)).Single());
        Assert.Equal("it is the door into the common stair from the Corridor, rated 30 minutes without smoke seals (no S) and with no closer.", Of(document, third.StairDoor).Single().Said);

        // FD30S fire doors, self-closing: met.
        var fire = document.TypesOf<DoorType>().First(type => type.Name == "Fire Door FD30S - 900 x 2100");
        foreach (var storey in storeys) (storey.FlatDoor.TypeId, storey.StairDoor.TypeId) = (fire.Id, fire.Id);
        Assert.All(storeys, storey => Assert.Empty(Of(document, storey.FlatDoor).Concat(Of(document, storey.StairDoor))));

        // A store off the corridor at its east end, a plain door: the enclosure of a protected corridor (5a). A meter cupboard needs no closer (C5).
        var cross = Wall(document, third.Level.Id, new Point2D(10000, 0), new Point2D(10000, 2000));
        var storeDoor = Door(document, cross, 1000);
        var store = Room(document, third.Level.Id, "Store", 11000, 1000);
        Assert.Equal("it is the door to the Store off the Corridor, rated 30 minutes without smoke seals (no S) and with no closer.", Of(document, storeDoor).Single().Said);
        store.Name = "Meter Cupboard";
        Assert.Equal("it is the door to the Meter Cupboard off the Corridor, rated 30 minutes without smoke seals (no S).", Of(document, storeDoor).Single().Said);

        // A boiler room there: a place of special fire hazard - FD 30, no seals asked, but a closer (10a).
        store.Name = "Boiler Room";
        var boiler = Of(document, storeDoor).Single();
        Assert.Equal("it is the door to the Boiler Room, a place of special fire hazard, with no closer.", boiler.Said);
        Assert.Contains("Table C1 item 10a", boiler.Rule);

        // The ground floor's stair opening into a boiler room behind it: the only stair, in a building past 3.28's - 3.73.
        var ground = storeys[0];
        var back = Wall(document, ground.Level.Id, new Point2D(0, 4500), new Point2D(3000, 4500));
        var plant = Door(document, back, 1500, "Fire Door FD30S - 900 x 2100");
        Room(document, ground.Level.Id, "Boiler Room", 1500, 5200);
        Assert.Equal("the only common stair opens into the Boiler Room, a place of special fire hazard.", Of(document, plant).Single().Said);
    }

    [Fact]
    public void ABlocksWallsAndFloorsResistFireForTableB4sPeriod()
    {
        // Five storeys, 12 m up: Table B4's 60 minutes. The 30 minute partitions: the stair's a protected shaft (8c), the flat's a compartment wall (6a).
        var (document, storeys) = Block(5);
        var third = storeys[2];
        Assert.Equal(("it encloses the common stair, beside the Corridor, rated 30 minutes.",
                "Approved Document B (7.21, 7.22, Table B3 item 8c) asks the walls of a protected shaft - a common stair, the floors of a block of flats being compartment floors - for Table B4's period: 60 minutes: flats of 5 storeys, its top floor 12.0 m up, Approved Document B Table B4."),
            Of(document, third.StairWall).Select(problem => (problem.Said, problem.Rule)).Single());
        Assert.Equal(("it separates Flat 3 from the Corridor, rated 30 minutes.",
                "Approved Document B (7.1b, 3.34, Table B3 item 6a) asks a wall separating a flat from any other part of the building to be a compartment wall, REI 60 or Table B4's period where that is less - here 60 minutes."),
            Of(document, third.FlatWall).Select(problem => (problem.Said, problem.Rule)).Single());

        // The outside walls - 120 minutes, and not between parts of the block - nothing asked here.
        Assert.Empty(document.Walls.Where(wall => document.GetWallType(wall)!.Name == Brick).SelectMany(wall => Of(document, wall)));

        // A 60 minute partition for both: met.
        var sixty = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm").Duplicate("Partition - 60 min");
        sixty.FireRating = "60 min";
        document.AddType(sixty);
        third.StairWall.TypeId = third.FlatWall.TypeId = sixty.Id;
        Assert.Empty(Of(document, third.StairWall).Concat(Of(document, third.FlatWall)));

        // Its floors: timber, 30 minutes, under each storey - compartment floors of 60; the ground floor none, nothing under it.
        var timber = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Timber 220mm");
        Floor Lay(Level level, params Point2D[] outline)
        {
            var floor = new Floor { LevelId = level.Id, TypeId = timber.Id };
            floor.SetBoundary(outline.Length > 0 ? outline : new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) });
            document.Add(floor);
            return floor;
        }

        var floors = storeys.Select(storey => Lay(storey.Level)).ToList();
        Assert.Empty(Of(document, floors[0]));
        var compartment = Of(document, floors[3]).Single();
        Assert.Equal("it is a compartment floor, rated 30 minutes.", compartment.Said);
        Assert.Equal("Approved Document B (7.1a, Table B3 item 3c, Table B4) asks every floor of a block of flats but one within a flat to be a compartment floor: 60 minutes: flats of 5 storeys, its top floor 12.0 m up, Approved Document B Table B4.", compartment.Rule);

        // The flat on the top two storeys one maisonette: a floor only under its upper rooms is within a flat - 30 minutes, met (note +).
        storeys[4].Flat.Dwelling = "Flat 4";
        document.Remove(floors[4]);
        var within = Lay(storeys[4].Level, new Point2D(3200, 2200), new Point2D(11800, 2200), new Point2D(11800, 5800), new Point2D(3200, 5800));
        Assert.Empty(Of(document, within));

        // Screed on a slab, 90 minutes: met.
        document.FindType<FloorType>(floors[3].TypeId)!.FireRating = "90 min";
        Assert.Empty(Of(document, floors[3]));
    }

    [Fact]
    public void ALowBlockAsksThirtyMinutesAndATerraceIsLeftToItsPartyWalls()
    {
        // Two storeys, the top 3 m up: Table B4's 30 minutes - the 30 minute partitions meet it; no protected corridors under 4.5 m (3.25).
        var (document, storeys) = Block(2);
        Assert.All(storeys, storey => Assert.Empty(Of(document, storey.StairWall).Concat(Of(document, storey.FlatWall))));
        Assert.Contains("rated 30 minutes without smoke seals", Of(document, storeys[1].FlatDoor).Single().Said);

        // Two houses side by side, each its own dwelling, a party wall between: no common parts, none over another - a terrace, its party wall by 5.8.
        var terrace = BimDocument.CreateDefault();
        var level = terrace.Levels[0].Id;
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) };
        for (var i = 0; i < 4; i++) Wall(terrace, level, corners[i], corners[(i + 1) % 4], Brick);
        var party = Wall(terrace, level, new Point2D(6000, 0), new Point2D(6000, 6000));
        terrace.GetWallType(party)!.Separating = true;
        Room(terrace, level, "Living Room", 3000, 3000, "House 1");
        Room(terrace, level, "Living Room", 9000, 3000, "House 2");
        Assert.False(FireSeparation.Covers(terrace));
        Assert.Contains(WallRules.Problems(terrace, party, terrace.ProjectInformation.ClearanceStandard), problem => problem.Rule.Contains("(5.8, Table B4)"));
    }

    [Fact]
    public void TheGroundOutsideABasementIsTheGroundFloors()
    {
        // A box 8 m by 6 m on the ground floor, 3 m high, and a basement 3 m down under it with a room.
        var document = BimDocument.CreateDefault();
        var ground = document.Levels[0];
        var basement = new Level { Name = "Basement", Elevation = -3000 };
        document.AddLevel(basement);
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(8000, 6000), new Point2D(8000, 0) };
        foreach (var level in new[] { ground, basement })
            for (var i = 0; i < 4; i++) Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick);
        Room(document, ground.Id, "Living Room", 4000, 3000);
        Room(document, basement.Id, "Cellar", 4000, 3000);
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = ground.Id, HeightOffset = 3000 };
        roof.SetBoundary(corners);
        document.Add(roof);

        // The wind's height and Table B4's storeys from the ground floor, not the basement's.
        Assert.Equal(0, Wind.Grade(document), 6);
        Assert.Equal(-3000, Wind.Ground(document), 6);
        Assert.Equal(3000, Wind.Height(document), 6);
        Assert.Equal((1, 0.0), FireDesign.HeightOf(document));

        // A drain outside 600 mm down is below the ground; a pipe in the cellar over its floor is not; one under the cellar's floor is.
        var level0 = ground.Id;
        var outside = new Pipe { Kind = SystemKind.Foul, LevelId = level0, Start = new Point2D(9000, 1000), End = new Point2D(12000, 1000), StartOffset = -600, EndOffset = -650, Diameter = 110 };
        var cellar = new Pipe { Kind = SystemKind.Foul, LevelId = basement.Id, Start = new Point2D(1000, 1000), End = new Point2D(4000, 1000), StartOffset = 300, EndOffset = 250, Diameter = 110 };
        var under = new Pipe { Kind = SystemKind.Foul, LevelId = basement.Id, Start = new Point2D(1000, 1000), End = new Point2D(4000, 1000), StartOffset = -600, EndOffset = -650, Diameter = 110 };
        foreach (var pipe in new[] { outside, cellar, under }) document.Add(pipe);
        Assert.True(Drainage.BelowGround(document, outside));
        Assert.False(Drainage.BelowGround(document, cellar));
        Assert.True(Drainage.BelowGround(document, under));

        // The wind on the walls above the ground, not on the basement's.
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        var model = AnalyticalModel.Of(document);
        var loading = StructuralLoading.Of(document, model);
        var onWalls = loading.SurfaceLoads.Where(load => load.Case.Nature == LoadNature.Wind && load.Panel.Element is Wall).Select(load => (Wall)load.Panel.Element).ToList();
        Assert.Equal(16, onWalls.Count);
        Assert.All(onWalls, wall => Assert.Equal(level0, wall.LevelId));
    }
}
