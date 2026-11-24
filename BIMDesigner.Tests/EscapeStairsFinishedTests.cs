using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Escape stairs finished (round 132), by Approved Document B Volume 2 Section 3 and 2.23: phased evacuation (3.16, 3.19 to 3.22, Table 3.3),
/// widths not narrowing and central handrails (3.10c, 3.10d, 3.11), basement stairs (3.14, 3.16a, 3.41), materials (3.24), lobbies to places
/// of special fire hazard (3.35), adjoining stairways (3.37), what may be within a protected stairway (3.38) and final exits shared at the
/// ground (2.23, Diagram 2.6).
/// </summary>
[Collection("Wpf")]
public class EscapeStairsFinishedTests
{
    private const string FireDoor = "Fire Door FD30S - 900 x 2100", Wide = "Single - 1000 x 2100", Plain = "Interior - Unrated Partition 100mm";

    private static WallType Unrated(BimDocument document)
    {
        if (document.TypesOf<WallType>().FirstOrDefault(each => each.Name == Plain) is { } known) return known;
        var plain = new WallType(Plain, new CompoundStructure(new MaterialLayer(LayerFunction.Finish2, document.Materials.First(material => material.Name == "Gypsum Plasterboard").Id, 100)))
            { Function = WallFunction.Interior };
        document.AddType(plain);
        return plain;
    }

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        if (type == Plain) Unrated(document);
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = FireDoor)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y)
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    private static Component Put(BimDocument document, Guid level, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = level, Location = new Point2D(x, y), Elevation = kind.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    private sealed record Building(BimDocument Document, List<Level> Levels, List<Room> Stairs, List<Room> Offices, List<Room> Corridors, Door Exit, Dictionary<Guid, List<Wall>> Outside);

    /// <summary>
    /// An office 14 m by 10 m on so many storeys 3 m apart, and so many basements under them: on each a stair at the west end (x 0 to 3 m) entered
    /// from a corridor along the south (y 0 to 2 m) through an FD30S door, an office north of the corridor - in a basement without a corridor, the
    /// office's own; a final exit, 1000 wide, from the ground floor's corridor at its east end; the stair 900 wide rising north from each storey
    /// to the next. A second stair at the east end (x 11 to 14 m, north of the corridor) on the storeys above the basements, where asked.
    /// </summary>
    private static Building Build(int storeys = 2, int basements = 0, bool corridorBelow = true, bool second = false)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        while (levels.Count < storeys)
        {
            var next = new Level { Name = $"Floor {levels.Count}", Elevation = levels.Count * 3000 };
            document.AddLevel(next);
            levels.Add(next);
        }

        levels = levels.Take(storeys).ToList();
        var ground = levels[0];
        for (var k = 1; k <= basements; k++)
        {
            var below = new Level { Name = $"Basement {k}", Elevation = -3000 * k };
            document.AddLevel(below);
            levels.Insert(0, below);
        }

        var (stairs, offices, corridors) = (new List<Room>(), new List<Room>(), new List<Room>());
        var outsides = new Dictionary<Guid, List<Wall>>();
        Door? exit = null;
        foreach (var level in levels)
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(14000, 0), new Point2D(14000, 10000), new Point2D(0, 10000) };
            var outside = Enumerable.Range(0, 4).Select(i => Wall(document, level.Id, corners[i], corners[(i + 1) % 4], "Exterior - Brick on Block 330mm")).ToList();
            outsides[level.Id] = outside;
            var stairWall = Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 10000));
            stairs.Add(Room(document, level.Id, "Stair", 1500, 8000));
            Door(document, stairWall, 1000);
            if (level.Elevation < 0 && !corridorBelow)
            {
                offices.Add(Room(document, level.Id, "Office", 8500, 5000));
                continue;
            }

            var corridorWall = Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(14000, 2000));
            Door(document, corridorWall, 5500, Wide);
            if (level == ground) exit = Door(document, outside[1], 1000, Wide);
            offices.Add(Room(document, level.Id, "Office", 7000, 6000));
            corridors.Add(Room(document, level.Id, "Corridor", 8500, 1000));
            if (second && level.Elevation >= 0)
            {
                Wall(document, level.Id, new Point2D(11000, 2000), new Point2D(11000, 10000));
                Door(document, corridorWall, 9500);
                Room(document, level.Id, "East Stair", 12500, 6000);
            }
        }

        var type = document.TypesOf<StairType>().First().Id;
        foreach (var (foot, head) in levels.Zip(levels.Skip(1)))
        {
            document.Add(new Stair { TypeId = type, LevelId = foot.Id, TopLevelId = head.Id, Start = new Point2D(1500, 2600), Angle = 90, Width = 900 });
            if (second && foot.Elevation >= 0)
                document.Add(new Stair { TypeId = type, LevelId = foot.Id, TopLevelId = head.Id, Start = new Point2D(12500, 2600), Angle = 90, Width = 900 });
        }

        return new Building(document, levels, stairs, offices, corridors, exit!, outsides);
    }

    private static string Verdict(Building building) => VerticalEscape.Verdict(building.Document, building.Stairs[^1])!;

    [Fact]
    public void PhasedEvacuationSizesStairsByTable33()
    {
        // Table 3.3 and its note 3: (P x 10) - 100, 1000 at least; its worked examples - 179 a storey over two stairs is 90, 1000; 240 over two is
        // 120, 1100. A central handrail makes each side a stair of its own (3.10d).
        Assert.Equal(new[] { 0, 110, 120, 190 }, new[] { 900.0, 1000, 1100, 1800 }.Select(width => VerticalEscape.PhasedCapacity(width)));
        Assert.Equal(new[] { 1000.0, 1100, 1200 }, new[] { 90, 120, 130 }.Select(VerticalEscape.PhasedWidth));
        Assert.Equal(260, VerticalEscape.PhasedCapacity(2400, handrail: true));
        Assert.Equal(480, VerticalEscape.Capacity(2400, 1, handrail: true));

        // An office of four storeys, a meeting room of 84 on each above the ground, one stair 1000 wide: evacuating at once it takes 230 of the
        // 252 from three storeys (Table 3.2, 150 + 40 + 40).
        var building = Build(4);
        var document = building.Document;
        foreach (var office in building.Offices.Skip(1)) office.Name = "Meeting Room";
        foreach (var flight in document.All<Stair>()) flight.Width = 1000;
        Assert.Contains("it is 1000 wide, taking 230 people from 3 storeys where 252 of the 252 on them may use it - Approved Document B Volume 2 (3.13, 3.14, 3.17, 3.18, Table 3.2)", Verdict(building));

        // Evacuated in phases: the busiest storey's 84, and 1000 takes 110 (Table 3.3) - met.
        document.ProjectInformation.PhasedEvacuation = true;
        Assert.True(VerticalEscape.Phased(document));
        var said = Verdict(building);
        Assert.DoesNotContain("Table 3.2)", said);
        Assert.DoesNotContain("(3.22", said);

        // 900 wide: under the 1000 phased evacuation asks at least.
        foreach (var flight in document.All<Stair>()) flight.Width = 900;
        Assert.Contains("it is 900 wide, taking 0 people of a storey evacuated in phases where 84 of the 84 on the busiest storey may use it - Approved Document B Volume 2 (3.22, Table 3.3) asks W = (P x 10) - 100, 1000 at least, P the people of the most heavily occupied storey: 1000", Verdict(building));

        // Every floor a compartment floor (3.21c), and an L3 alarm - the corridors, escape routes, detected (3.21e).
        Assert.Equal("every floor a compartment floor, the building designed for phased evacuation (3.21c)", FireCompartments.CompartmentFloorAt(document, 3000));
        Assert.Contains("Approved Document B Volume 2 (3.21e)", Alarms.Verdict(document, building.Corridors[1]));
        Put(document, building.Levels[1].Id, "Smoke Detector - Optical, BS EN 54-7", 8500, 1000);
        Assert.DoesNotContain("(3.21e)", Alarms.Verdict(document, building.Corridors[1]) ?? string.Empty);

        // A hotel may not be evacuated in phases (3.16c): sized for everyone at once again.
        document.ProjectInformation.BuildingType = "Hotel";
        Assert.False(VerticalEscape.Phased(document));
        Assert.Contains("the building is set for phased evacuation, which Approved Document B Volume 2 (3.16) does not allow in an other residential building - a hotel, hostel or hall (3.16c)", Verdict(building));
        Assert.DoesNotContain("(3.21c)", FireCompartments.CompartmentFloorAt(document, 3000) ?? string.Empty);
    }

    [Fact]
    public void AStairDoesNotNarrowOnTheWayOutAndAWideOneHasACentralHandrail()
    {
        // Three storeys: the flight from the ground 800 wide under one of 900 from the first floor - narrowing on the way out (3.10c).
        var building = Build(3);
        var document = building.Document;
        var flights = document.All<Stair>().OrderBy(flight => document.FindLevel(flight.LevelId)!.Elevation).ToList();
        flights[0].Width = 800;
        Assert.Contains($"its flight from the {building.Levels[0].Name} is 800 wide, narrower than the 900 of the one from the {building.Levels[1].Name} before it on the way out - Approved Document B Volume 2 (3.10c)", Verdict(building));
        flights[0].Width = 900;
        Assert.DoesNotContain("(3.10c)", Verdict(building));

        // A shop's stair 2100 wide: Approved Document K's central handrail in a public building (3.11); ticked in its type, met.
        document.ProjectInformation.BuildingType = "Shop";
        foreach (var flight in flights) flight.Width = 2100;
        Assert.Contains("it is 2100 wide with no central handrail - Approved Document B Volume 2 (3.11)", Verdict(building));
        document.FindType<StairType>(flights[0].TypeId)!.CentralHandrail = true;
        Assert.DoesNotContain("(3.11)", Verdict(building));

        // Twelve storeys, rising 33 m: a stair 1500 wide asks a central handrail (3.10d).
        var tall = Build(12);
        foreach (var flight in tall.Document.All<Stair>()) flight.Width = 1500;
        Assert.Contains("it rises 33.0 m and is 1500 wide with no central handrail - Approved Document B Volume 2 (3.10d, Table 3.3 note 2)", Verdict(tall));
        tall.Document.FindType<StairType>(tall.Document.All<Stair>().First().TypeId)!.CentralHandrail = true;
        Assert.DoesNotContain("(3.10d", Verdict(tall));
    }

    [Fact]
    public void ABasementsStairTakesItsPeopleAndIsNonCombustible()
    {
        // An office over a basement: the basement a meeting room of 84, its stair 900 - 50 from one storey (Table 3.2, 3.16a).
        var building = Build(2, basements: 1);
        var document = building.Document;
        building.Offices[0].Name = "Meeting Room";
        var said = Verdict(building);
        Assert.Contains("it is 900 wide, taking 50 people from 1 basement storey where 84 of the 84 on them may use it - Approved Document B Volume 2 (3.14, 3.16a, 3.17, Table 3.2)", said);

        // A timber stair serving a basement (3.24b) - an office of three storeys spared 3.24a; concrete, met.
        Assert.Contains("its flights are Stair - Timber, Closed Risers, timber D-s2, d0 - Approved Document B Volume 2 (3.24) asks the flights and landings of an escape stair serving a basement (3.24b) of class A2-s3, d2 or better, B-s3, d2 or worse only on their top surface", said);
        var type = document.FindType<StairType>(document.All<Stair>().First().TypeId)!;
        type.Construction = StairConstruction.Concrete;
        foreach (var flight in document.All<Stair>()) flight.Width = 1000;
        said = Verdict(building);
        Assert.DoesNotContain("(3.24)", said);
        Assert.DoesNotContain("(3.14, 3.16a", said);

        // A timber stair given a material declared A2-s1, d0: met; a shop's only stair of two storeys asks it (3.24a), an office's does not.
        var shop = Build(2);
        shop.Document.ProjectInformation.BuildingType = "Shop";
        Assert.Contains("of an escape stair that is the only stair (3.24a) of class A2-s3, d2 or better", Verdict(shop));
        var timber = shop.Document.FindType<StairType>(shop.Document.All<Stair>().First().TypeId)!;
        var board = shop.Document.Materials.First(material => material.Name == "Gypsum Plasterboard");
        timber.MaterialId = board.Id;
        Assert.Equal(Euroclass.A2s1d0, VerticalEscape.ClassOf(shop.Document, timber).Class);
        Assert.DoesNotContain("(3.24)", Verdict(shop));
    }

    [Fact]
    public void OtherStairsThanTheOneEndingAtTheGroundAreLobbiedInTheBasement()
    {
        // Two stairs from the first floor; the west one goes on down to a basement whose office it opens straight into (3.41).
        var open = Build(2, basements: 1, corridorBelow: false, second: true);
        Assert.Contains($"it is one of 2 stairs from the upper storeys and goes on down to the basement, opening straight into the accommodation on the {open.Levels[0].Name} - Approved Document B Volume 2 (3.41)", Verdict(open));
        Assert.DoesNotContain("(3.40)", Verdict(open));

        // A corridor between them in the basement: met.
        var lobbied = Build(2, basements: 1, corridorBelow: true, second: true);
        Assert.DoesNotContain("(3.41)", Verdict(lobbied));
    }

    [Fact]
    public void APlaceOfSpecialFireHazardIsReachedFromAStairThroughAVentilatedLobby()
    {
        // Upstairs, a boiler room partitioned off the north end of the stair, opening straight into it (3.35).
        var building = Build(2);
        var document = building.Document;
        var upstairs = building.Levels[1];
        building.Stairs[1].Location = new Point2D(1500, 6500);
        Door(document, Wall(document, upstairs.Id, new Point2D(0, 7000), new Point2D(3000, 7000)), 1500);
        var boiler = Room(document, upstairs.Id, "Boiler Room", 1500, 8500);
        Assert.Contains($"it opens straight into the Boiler Room, a place of special fire hazard, on the {upstairs.Name} - Approved Document B Volume 2 (3.35)", Verdict(building));

        // A lobby between: but no permanent ventilation.
        Door(document, Wall(document, upstairs.Id, new Point2D(0, 8500), new Point2D(3000, 8500)), 1500);
        boiler.Location = new Point2D(1500, 9250);
        var lobby = Room(document, upstairs.Id, "Lobby", 1500, 7750);
        Assert.Contains($"its lobby to the Boiler Room, the Lobby on the {upstairs.Name}, has no permanent ventilation - Approved Document B Volume 2 (3.35)", Verdict(building));

        // A louvre in its outside wall, permanently open, 0.5 m² free: met.
        var louvre = document.TypesOf<WindowType>().First(each => each.Name == "Awning - 1200 x 600").Duplicate("Louvre - Permanent 1200 x 600");
        louvre.SmokeVent = SmokeVentKind.Permanent;
        louvre.SmokeVentArea = 0.5;
        document.AddType(louvre);
        document.Add(new Window { HostWallId = building.Outside[upstairs.Id][3].Id, DistanceAlongWall = 2250, LevelId = upstairs.Id, TypeId = louvre.Id });
        Assert.Equal(0.5, VerticalEscape.PermanentVentilation(document, lobby), 3);
        Assert.DoesNotContain("(3.35)", Verdict(building));
    }

    [Fact]
    public void TwoProtectedStairwaysLeadingToDifferentFinalExitsHaveNoDoorBetweenThem()
    {
        // A second stair east of the first (x 3 to 6 m, north of the corridor) on both storeys; the west stair given a final exit of its own
        // in the west wall; a door between the two stairs upstairs (3.37).
        var building = Build(2);
        var document = building.Document;
        foreach (var level in building.Levels)
        {
            Wall(document, level.Id, new Point2D(6000, 2000), new Point2D(6000, 10000));
            var corridorWall = document.Walls.First(wall => wall.LevelId == level.Id && wall.Start == new Point2D(3000, 2000));
            Door(document, corridorWall, 1500);
            Room(document, level.Id, "Second Stair", 4500, 6000);
        }

        foreach (var office in building.Offices) office.Location = new Point2D(10000, 6000);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = building.Levels[0].Id, TopLevelId = building.Levels[1].Id, Start = new Point2D(4500, 2600), Angle = 90, Width = 900 });
        var own = Door(document, building.Outside[building.Levels[0].Id][3], 5000, Wide);
        var between = Door(document, document.Walls.First(wall => wall.LevelId == building.Levels[1].Id && wall.Start == new Point2D(3000, 0)), 6000);
        Assert.Equal(2, VerticalEscape.Stairs(document).Count);
        Assert.Contains($"a door joins it to another protected stairway leading to another final exit - the Second Stair's on the {building.Levels[1].Name} - Approved Document B Volume 2 (3.37)", Verdict(building));

        // Both leading to the one final exit - the corridor's - the door may stay; or the door gone.
        document.Remove(own);
        Assert.DoesNotContain("(3.37)", Verdict(building));
        document.Add(own);
        document.Remove(between);
        Assert.DoesNotContain("(3.37)", Verdict(building));
    }

    [Fact]
    public void OnlyToiletsLiftsADeskAndCupboardsAreWithinAProtectedStairway()
    {
        // Upstairs, a WC partitioned off the north end of the stair by a plain partition, its only door into the stair: within the stairway,
        // and allowed (3.38a) - its other walls the stairway's enclosure.
        var building = Build(2);
        var document = building.Document;
        var upstairs = building.Levels[1];
        Door(document, Wall(document, upstairs.Id, new Point2D(0, 8500), new Point2D(3000, 8500), Plain), 1500, "Single - 900 x 2100");
        var room = Room(document, upstairs.Id, "WC", 1500, 9250);
        var said = Verdict(building);
        Assert.DoesNotContain("(3.38", said);
        Assert.DoesNotContain("(3.23", said);

        // A cloakroom, a cupboard, a reception desk upstairs in the only stair, an office: not allowed.
        foreach (var (name, why) in new[]
                 {
                     ("Cloakroom", "a cloakroom (3.38a)"),
                     ("Cleaner's Cupboard", "a cupboard not enclosed in fire-resisting construction, in the only stair (3.38d)"),
                     ("Reception", "a reception or enquiry office in the only stair, not at the ground (3.38c)"),
                     ("Office", "opening only into it, its wall to it less than 30 minutes")
                 })
        {
            room.Name = name;
            Assert.Contains($"within it: the {name} on the {upstairs.Name}, {why}", Verdict(building));
        }

        // A WC again, a gas water heater in it: allowed; a gas boiler in the stair itself: not (3.38a).
        room.Name = "WC";
        Put(document, upstairs.Id, "Water Heater - Gas 50 gal", 2400, 9600);
        Assert.DoesNotContain("(3.38", Verdict(building));
        var boiler = Put(document, upstairs.Id, "Boiler - Gas Combi", 500, 7500);
        Assert.Contains($"within it: the Boiler - Gas Combi, a gas appliance, in the Stair on the {upstairs.Name} (3.38a)", Verdict(building));
        document.Remove(boiler);
        Assert.DoesNotContain("(3.38", Verdict(building));
    }

    [Fact]
    public void AFinalExitSharedWithAGroundStoreyExitTakesBothFlows()
    {
        // The ground floor office a meeting room of 84, its door into the corridor the stair comes down into, the corridor's final exit 1000:
        // W = (84/2.5 + 60 x 0.9)/80 = 1.095 m (2.23, Diagram 2.6).
        var building = Build(2);
        var document = building.Document;
        building.Offices[0].Name = "Meeting Room";
        var single = Doors.EscapeWidth(document.TypesOf<DoorType>().First(each => each.Name == Wide));
        Assert.Contains($"its final exit from the Corridor gives {single:0} where 84 people from the ground storey's Meeting Room join the stair's flow - Approved Document B Volume 2 (2.23, Diagram 2.6) asks W = (N/2.5 + 60S)/80: 1095", Verdict(building));

        // A double door: wide enough.
        building.Exit.TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Double - 1800 x 2100").Id;
        Assert.DoesNotContain("(2.23", Verdict(building));

        // The final exit moved into the south wall facing the meeting room's door, less than 2 m from it with more than 60 joining: the stair's
        // width and the storey exit's together.
        document.Remove(building.Exit);
        Door(document, building.Outside[building.Levels[0].Id][0], 8500, Wide);
        var said = Verdict(building);
        Assert.Contains($"its final exit from the Corridor gives {single:0} where 84 people from the ground storey's Meeting Room join the stair's flow, ", said);
        Assert.Contains($"m from it - Approved Document B Volume 2 (2.23, Diagram 2.6) asks the stair's width and the storey exit's together where more than 60 join less than 2 m from the final exit: {900 + single:0}", said);
    }
}
