using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A US apartment building, Group R-2 under the 2021 IBC (round 144): fire partitions and horizontal assemblies between units, corridor walls,
/// exit stair enclosures and STC 50; sprinklers, the fire alarm's trigger and each story's exits; common stairs by 1011, corridors by 1020.3,
/// guards by 1015.3, smoke alarms by 907.2.11.2, escape windows only on a story of one exit (1031.2).
/// </summary>
[Collection("Wpf")]
public class ApartmentTests
{
    private const string Partition = "Interior - Partition 125mm", Demising = "Demising Wall - Staggered Studs, 5/8 in Type X Each Side, 1 hr, STC 51",
        Stucco = "Wood Frame 2x6 - Stucco", Pan = "Stair - Concrete Pan, 7 in Rise, 11 in Tread";

    private static Guid TypeOf<T>(BimDocument document, string name) where T : Core.Elements.ElementType => document.TypesOf<T>().First(each => each.Name == name).Id;

    private sealed record Block(BimDocument Document, List<Level> Stories, List<Wall> Partitions, List<Stair> Stairs, List<Room> Units, List<Room> Corridors);

    /// <summary>
    /// A US apartment building of stories 3 m apart, 24 m by 12.6 m, as round 144's walkthrough has it: a corridor down its middle between walls
    /// at y 5.4 m and 7.2 m, four studios on each story, an exit stair at each end (or the west one only) with a door out at the bottom.
    /// </summary>
    private static Block Build(int stories, string between = Partition, bool twoStairs = true, string use = "Apartments")
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BuildingType = use;
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        var storeys = new List<Level>();
        for (var i = 0; i < stories; i++)
        {
            var level = levels.FirstOrDefault(each => Math.Abs(each.Elevation - 3000 * i) < 1);
            if (level is null) document.AddLevel(level = new Level { Name = $"Floor {i + 1}", Elevation = 3000 * i });
            storeys.Add(level);
        }

        var (partitions, stairs, units, corridors) = (new List<Wall>(), new List<Stair>(), new List<Room>(), new List<Room>());
        Wall Wall(Level level, double x0, double y0, double x1, double y1, string type)
        {
            var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = TypeOf<WallType>(document, type), LevelId = level.Id, UnconnectedHeight = 3000 };
            document.Add(wall);
            if (type == between) partitions.Add(wall);
            return wall;
        }

        var door = TypeOf<DoorType>(document, "Interior Door - 32 x 80 in");
        for (var s = 0; s < stories; s++)
        {
            var level = storeys[s];
            var west = Wall(level, 0, 0, 0, 12600, Stucco);
            Wall(level, 0, 12600, 24000, 12600, Stucco);
            var east = Wall(level, 24000, 12600, 24000, 0, Stucco);
            Wall(level, 24000, 0, 0, 0, Stucco);
            var south = Wall(level, 3000, 5400, 21000, 5400, between);
            var north = Wall(level, 3000, 7200, 21000, 7200, between);
            var westStair = Wall(level, 3000, 3300, 3000, 9300, between);
            Wall(level, 0, 3300, 3000, 3300, between);
            Wall(level, 0, 9300, 3000, 9300, between);
            var eastStair = Wall(level, 21000, 3300, 21000, 9300, between);
            Wall(level, 21000, 3300, 24000, 3300, between);
            Wall(level, 21000, 9300, 24000, 9300, between);
            Wall(level, 12000, 0, 12000, 5400, between);
            Wall(level, 12000, 7200, 12000, 12600, between);
            foreach (var along in new[] { 4500.0, 13500 })
            {
                document.Add(new Door { HostWallId = south.Id, LevelId = level.Id, DistanceAlongWall = along, TypeId = door });
                document.Add(new Door { HostWallId = north.Id, LevelId = level.Id, DistanceAlongWall = along, TypeId = door });
            }

            document.Add(new Door { HostWallId = westStair.Id, LevelId = level.Id, DistanceAlongWall = 3000, TypeId = door });
            document.Add(new Door { HostWallId = eastStair.Id, LevelId = level.Id, DistanceAlongWall = 3000, TypeId = door });
            if (s == 0)
            {
                document.Add(new Door { HostWallId = west.Id, LevelId = level.Id, DistanceAlongWall = 6300, TypeId = TypeOf<DoorType>(document, "Exterior Door - 36 x 80 in") });
                if (twoStairs) document.Add(new Door { HostWallId = east.Id, LevelId = level.Id, DistanceAlongWall = 6300, TypeId = TypeOf<DoorType>(document, "Exterior Door - 36 x 80 in") });
            }

            foreach (var (x, y, unit) in new[] { (7500.0, 2700.0, 1), (16500, 2700, 2), (7500, 9900, 3), (16500, 9900, 4) })
            {
                var room = new Room { LevelId = level.Id, Location = new Point2D(x, y), Name = "Living", Dwelling = $"Apt {100 * (s + 1) + unit}" };
                document.Add(room);
                units.Add(room);
            }

            var corridor = new Room { LevelId = level.Id, Location = new Point2D(12000, 6300), Name = "Corridor" };
            document.Add(corridor);
            corridors.Add(corridor);
            document.Add(new Room { LevelId = level.Id, Location = new Point2D(1500, 6300), Name = "Stair 1" });
            document.Add(new Room { LevelId = level.Id, Location = new Point2D(22500, 6300), Name = "Stair 2" });
            if (s < stories - 1)
            {
                var stairType = TypeOf<StairType>(document, Pan);
                var up = new Stair { TypeId = stairType, LevelId = level.Id, TopLevelId = storeys[s + 1].Id, Start = new Point2D(1500, 3700), Angle = 90, Width = 1100 };
                document.Add(up);
                stairs.Add(up);
                if (twoStairs)
                {
                    var second = new Stair { TypeId = stairType, LevelId = level.Id, TopLevelId = storeys[s + 1].Id, Start = new Point2D(22500, 3700), Angle = 90, Width = 1100 };
                    document.Add(second);
                    stairs.Add(second);
                }
            }

            var floor = new Floor { LevelId = level.Id, TypeId = TypeOf<FloorType>(document, s == 0 ? "Floor - Timber 220mm" : "Floor - 2x10 Joists, 5/8 in Type X Ceiling, 1 hr") };
            floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(24000, 0), new Point2D(24000, 12600), new Point2D(0, 12600) });
            if (s > 0)
                floor.SetHoles(new[]
                {
                    new[] { new Point2D(60, 3360), new Point2D(2940, 3360), new Point2D(2940, 8170), new Point2D(60, 8170) },
                    new[] { new Point2D(21060, 3360), new Point2D(23940, 3360), new Point2D(23940, 8170), new Point2D(21060, 8170) }
                });
            document.Add(floor);
        }

        return new Block(document, storeys, partitions, stairs, units, corridors);
    }

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Wall wall) => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard);

    [Fact]
    public void AnApartmentBuildingIsGroupR2UnderTheIbc()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        foreach (var use in new[] { "Apartments", "Condos", "Triplex", "Fourplex", "Multi-family building", "Flat" })
        {
            document.ProjectInformation.BuildingType = use;
            Assert.Equal(BuildingUse.Flats, FireDesign.UseOf(document));
            Assert.True(Apartments.Applies(document), use);
        }

        document.ProjectInformation.BuildingType = "Townhouse";
        Assert.False(Apartments.Applies(document));
        document.ProjectInformation.BuildingType = "Apartments";
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.False(Apartments.Applies(document));
    }

    [Fact]
    public void WallsBetweenUnitsAreOneHourFirePartitionsOfStc50AndCorridorWallsHalfAnHourSprinklered()
    {
        var block = Build(3);
        var document = block.Document;
        var between = block.Partitions.First(wall => wall.LevelId == block.Stories[1].Id && Math.Abs(wall.Start.X - 12000) < 1 && wall.Start.Y < 1);
        var corridor = block.Partitions.First(wall => wall.LevelId == block.Stories[1].Id && Math.Abs(wall.Start.Y - 5400) < 1);
        var enclosure = block.Partitions.First(wall => wall.LevelId == block.Stories[1].Id && Math.Abs(wall.Start.X - 3000) < 1 && Math.Abs(wall.End.X - 3000) < 1);

        // The 125 partition, 1/2 hour and Rw 38: short of 708.3's hour and 1206.2's STC 50.
        Assert.Contains(Problems(document, between), problem => problem.Said == "it separates two dwelling units, rated 1/2 hour." && problem.Rule.Contains("(420.2, 708.3)"));
        Assert.Contains(Problems(document, between), problem => problem.Rule.Contains("(1206.2)"));

        // The corridor wall: no sprinklers, a corridor not allowed (Table 1020.2); the stair's: 3 stories connected, 1 hour (1023.2).
        Assert.Contains(Problems(document, corridor), problem => problem.Said == "it is a corridor wall of an apartment building, rated 1/2 hour, the building not sprinklered.");
        Assert.Contains(Problems(document, enclosure), problem => problem.Said == "it encloses an interior exit stair connecting 3 stories, rated 1/2 hour." && problem.Rule.Contains("(1023.2)"));

        // Type VB and sprinklered: 1/2 hour between units does (708.3 exception 2), and along the corridor.
        document.ProjectInformation.Sprinklered = true;
        document.ProjectInformation.ConstructionType = ConstructionType.VB;
        Assert.DoesNotContain(Problems(document, between), problem => problem.Rule.Contains("708.3"));
        Assert.DoesNotContain(Problems(document, corridor), problem => problem.Rule.Contains("1020.2"));

        // The demising wall: 1 hour, STC 51 - nothing asked of it anywhere.
        foreach (var wall in block.Partitions) wall.TypeId = TypeOf<WallType>(document, Demising);
        Assert.DoesNotContain(block.Partitions.SelectMany(wall => Problems(document, wall)), problem => problem.Rule.Contains("IBC"));

        // Four stories: the stairs enclosed 2 hours.
        var tall = Build(4, Demising);
        var tallEnclosure = tall.Partitions.First(wall => wall.LevelId == tall.Stories[1].Id && Math.Abs(wall.Start.X - 3000) < 1 && Math.Abs(wall.End.X - 3000) < 1);
        Assert.Contains(Problems(tall.Document, tallEnclosure), problem => problem.Said == "it encloses an interior exit stair connecting 4 stories, rated 1 hour.");
    }

    [Fact]
    public void FloorsBetweenUnitsAreOneHourHorizontalAssemblies()
    {
        var block = Build(2, Demising);
        var document = block.Document;
        var floor = document.All<Floor>().Single(each => each.LevelId == block.Stories[1].Id);
        Assert.DoesNotContain(SlabRules.Problems(document, floor, ClearanceStandard.Irc), problem => problem.Rule.Contains("IBC"));

        // The timber floor, 30 minutes and Rw 40: short of 711.2.4.3's hour and 1206.2's STC 50.
        floor.TypeId = TypeOf<FloorType>(document, "Floor - Timber 220mm");
        var problems = SlabRules.Problems(document, floor, ClearanceStandard.Irc);
        Assert.Contains(problems, problem => problem.Said == "it separates dwelling units one over another, rated 1/2 hour." && problem.Rule.Contains("(420.3, 711.2.4.3)"));
        Assert.Contains(problems, problem => problem.Rule.Contains("(1206.2)"));
    }

    [Fact]
    public void SprinklersAFireAlarmAndTwoExitsFromEachStory()
    {
        // Three stories, 12 units: no fire alarm asked (907.2.9.1); not sprinklered, said (903.2.8); two exits from each story.
        var block = Build(3, Demising);
        var document = block.Document;
        Assert.All(Apartments.Stories(document), story => Assert.Equal(2, story.Exits));
        var said = Apartments.BuildingProblems(document).Select(problem => problem.Said).ToList();
        Assert.Contains(said, each => each.StartsWith("the building is not sprinklered - the IBC (420.4, 903.2.8)"));
        Assert.DoesNotContain(said, each => each.Contains("907.2.9.1"));
        document.ProjectInformation.Sprinklered = true;
        Assert.Empty(Apartments.BuildingProblems(document));

        // Four stories: a unit three stories above the level of exit discharge - a panel and sounders asked, call points not, sprinklered.
        var tall = Build(4, Demising);
        tall.Document.ProjectInformation.Sprinklered = true;
        var alarm = Assert.Single(Apartments.BuildingProblems(tall.Document));
        Assert.StartsWith("it has a dwelling unit 3 stories above the lowest level of exit discharge, and no a fire alarm panel or sounders - the IBC (907.2.9.1)", alarm.Said);

        // One stair: one exit from each story. Allowed up to the third story with four units, sprinklered (Table 1006.3.4(1)); not without sprinklers.
        var single = Build(3, Demising, twoStairs: false);
        var singleDocument = single.Document;
        singleDocument.ProjectInformation.Sprinklered = true;
        Assert.All(Apartments.Stories(singleDocument), story => Assert.Equal(1, story.Exits));
        Assert.Empty(Apartments.BuildingProblems(singleDocument));
        singleDocument.ProjectInformation.Sprinklered = false;
        Assert.Contains(Apartments.BuildingProblems(singleDocument), problem => problem.Said.StartsWith("Floor 3 has one exit, 4 dwelling units on it, story 3 above grade plane, not sprinklered - the IBC (1006.3.3) asks two"));
    }

    [Fact]
    public void CommonStairsGuardsSmokeAlarmsAndEscapeWindowsGoByTheIbc()
    {
        var block = Build(2, Demising);
        var document = block.Document;
        document.ProjectInformation.Sprinklered = true;

        // The concrete pan stair: 7 in risers, 11 in treads, 1100 wide - nothing asked; the house stair's 7 3/4 and 10 short on a common stair.
        var stair = block.Stairs[0];
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("IBC"));
        stair.TypeId = TypeOf<StairType>(document, "Stair - Wood, 7 3/4 in Rise, 10 in Tread");
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.StartsWith("Each tread is 254: the IBC asks for 279 (11 in) at least on a common stair (1011.5.2)."));

        // A guard in the corridor: 42 in (1015.3).
        var railing = new Railing { LevelId = block.Stories[1].Id, TypeId = document.TypesOf<RailingType>().First().Id };
        railing.SetPath(new[] { new Point2D(8000, 6300), new Point2D(10000, 6300) });
        document.Add(railing);
        Assert.Equal(1067, Railings.Required(document, railing).Height);
        railing.SetPath(new[] { new Point2D(6000, 2700), new Point2D(8000, 2700) });
        Assert.Equal(914, Railings.Required(document, railing).Height);

        // Smoke alarms: a unit's room asks one by 907.2.11.2; the corridor none of its own.
        Assert.Contains("IBC 907.2.11.2", Alarms.Verdict(document, block.Units[0]) ?? string.Empty);
        Assert.DoesNotContain("smoke alarm", Alarms.Verdict(document, block.Corridors[0]) ?? string.Empty);

        // A bed in a unit on a story of two exits: no escape window asked (1031.2); on a story of one, asked.
        var bed = document.TypesOf<ComponentType>().First(each => each.Form == ComponentForm.Bed).Id;
        var bedroom = block.Units.First(unit => unit.LevelId == block.Stories[1].Id);
        document.Add(new Component { LevelId = bedroom.LevelId, TypeId = bed, Location = new Point2D(bedroom.Location.X + 1500, bedroom.Location.Y) });
        Assert.DoesNotContain("escape window", WindowRules.Verdict(document, bedroom) ?? string.Empty);
        var single = Build(2, Demising, twoStairs: false);
        var lone = single.Units.First(unit => unit.LevelId == single.Stories[1].Id);
        single.Document.Add(new Component { LevelId = lone.LevelId, TypeId = single.Document.TypesOf<ComponentType>().First(each => each.Form == ComponentForm.Bed).Id, Location = new Point2D(lone.Location.X + 1500, lone.Location.Y) });
        Assert.Contains("no escape window - the IBC (1031.2, Table 1006.3.4(1))", WindowRules.Verdict(single.Document, lone) ?? string.Empty);
    }

    /// <summary>
    /// A US apartment building's ground floor, sprinklered, a corridor 1.8 m wide down its north side, its length long: units 6 m wide and 6 m deep
    /// along its south side, each a door into it; two doors out - side by side at its west end, or one at each end.
    /// </summary>
    private static (BimDocument Document, List<Room> Units, Room Corridor) Corridor(double length, bool endToEnd)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BuildingType = "Apartments";
        document.ProjectInformation.Sprinklered = true;
        var level = document.Levels.OrderBy(each => each.Elevation).First();
        Wall Wall(double x0, double y0, double x1, double y1, string type)
        {
            var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = TypeOf<WallType>(document, type), LevelId = level.Id, UnconnectedHeight = 3000 };
            document.Add(wall);
            return wall;
        }

        var west = Wall(0, 0, 0, 7800, Stucco);
        var north = Wall(0, 7800, length, 7800, Stucco);
        var east = Wall(length, 7800, length, 0, Stucco);
        Wall(length, 0, 0, 0, Stucco);
        var corridorWall = Wall(0, 6000, length, 6000, Demising);
        var outDoor = TypeOf<DoorType>(document, "Exterior Door - 36 x 80 in");
        document.Add(new Door { HostWallId = west.Id, LevelId = level.Id, DistanceAlongWall = 6900, TypeId = outDoor });
        document.Add(endToEnd ? new Door { HostWallId = east.Id, LevelId = level.Id, DistanceAlongWall = 900, TypeId = outDoor } : new Door { HostWallId = north.Id, LevelId = level.Id, DistanceAlongWall = 1500, TypeId = outDoor });
        var units = new List<Room>();
        for (var x = 0.0; x < length - 1; x += 6000)
        {
            if (x > 0) Wall(x, 0, x, 6000, Demising);
            document.Add(new Door { HostWallId = corridorWall.Id, LevelId = level.Id, DistanceAlongWall = x + 3000, TypeId = TypeOf<DoorType>(document, "Interior Door - 32 x 80 in") });
            var unit = new Room { LevelId = level.Id, Location = new Point2D(x + 3000, 3000), Name = "Living", Dwelling = $"Apt {units.Count + 101}" };
            document.Add(unit);
            units.Add(unit);
        }

        var corridor = new Room { LevelId = level.Id, Location = new Point2D(length / 2, 6900), Name = "Corridor" };
        document.Add(corridor);
        return (document, units, corridor);
    }

    [Fact]
    public void EachStoryIsWalkedForItsTravelItsCommonPathItsDeadEndsAndItsExitsApart()
    {
        // 36 m long, a door out at each end: the middle units' far corners some 18 m along the corridor and 6.7 m across a unit from an exit -
        // within 250 ft; no dead end; the doors 36 m apart, a third of the 36.8 m diagonal is 12.3.
        var (good, goodUnits, _) = Corridor(36000, endToEnd: true);
        var story = Assert.Single(Apartments.Stories(good));
        Assert.Equal(2, story.Exits);
        Assert.InRange(Apartments.FarthestTravel(good, story.Level.Id), 20000, 28000);
        Assert.Empty(Apartments.BuildingProblems(good));
        Assert.Contains("from an exit, walked (1017.2)", Escape.Describe(good, goodUnits[2]));

        // 90 m long, both doors at the west end: the far unit 90 m or so from them - past 250 ft - its common path past 125 ft, the corridor a
        // dead end of some 88 m, the doors 1.8 m apart where a third of the 90.3 m diagonal is 30.1 (1017.2, 1006.2.1, 1020.5, 1007.1.1).
        var (bad, badUnits, corridor) = Corridor(90000, endToEnd: false);
        var said = Apartments.BuildingProblems(bad).ToList();
        var far = Assert.Single(said, problem => problem.Said.Contains("(1017.2, Table 1017.2)"));
        Assert.Same(badUnits[^1], far.Room);
        Assert.StartsWith("its farthest point is ", far.Said);
        Assert.Contains(said, problem => problem.Said.Contains("(1006.2.1, Table 1006.2.1) allows a common path of egress travel of 125 ft"));
        var deadEnd = Assert.Single(said, problem => problem.Said.Contains("(1020.5)"));
        Assert.Same(corridor, deadEnd.Room);
        Assert.StartsWith("Corridor has a dead end of ", deadEnd.Said);
        Assert.Contains(said, problem => problem.Said.Contains("(1007.1.1) asks two exits at least a third of the diagonal apart"));
    }
}
