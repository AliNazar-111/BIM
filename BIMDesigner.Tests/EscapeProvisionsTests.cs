using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// The rest of Approved Document B Volume 2's Section 5 (round 114): final exits as wide as their stair needs (5.20) and clear of hazards'
/// openings and smoke outlets (5.24, 5.45); lift wells enclosed, protected shafts, lobbied in a basement and not down to it past one stair (5.34,
/// 5.35, 5.38, 5.40); refuse stores (5.43, 5.44) and shops' store rooms (5.46).
/// </summary>
[Collection("Wpf")]
public class EscapeProvisionsTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Partition = "Interior - Partition 125mm", Plain = "Interior - Unrated Partition 100mm", Wide = "Single - 1000 x 2100";

    private static WallType Unrated(BimDocument document)
    {
        if (document.TypesOf<WallType>().FirstOrDefault(each => each.Name == Plain) is { } known) return known;
        var plain = new WallType(Plain, new CompoundStructure(new MaterialLayer(LayerFunction.Finish2, document.Materials.First(material => material.Name == "Gypsum Plasterboard").Id, 100)))
            { Function = WallFunction.Interior };
        document.AddType(plain);
        return plain;
    }

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = Partition)
    {
        if (type == Plain) Unrated(document);
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static List<Wall> Outside(BimDocument document, Guid level, double width, double depth)
    {
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        return Enumerable.Range(0, 4).Select(i => Wall(document, level, corners[i], corners[(i + 1) % 4], Brick)).ToList();
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = Wide)
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

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Door door) => Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard);

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Wall wall) => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard);

    [Fact]
    public void AFinalExitIsKeptClearOfAHazardsOpeningsAndASmokeOutlet()
    {
        // An office 12 m by 8 m, its final exit in the south wall at x 3 m; a boiler room in the south-east corner, its own door out at x 10.5 m.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var level = document.Levels[0].Id;
        var walls = Outside(document, level, 12000, 8000);
        Wall(document, level, new Point2D(9000, 0), new Point2D(9000, 4000));
        Wall(document, level, new Point2D(9000, 4000), new Point2D(12000, 4000));
        Room(document, level, "Office", 4500, 4000);
        var boiler = Room(document, level, "Boiler Room", 10500, 2000);
        var exit = Door(document, walls[0], 3000);
        Door(document, walls[0], 10500);

        // 6.5 m apart, edge to edge: clear. The exit moved to x 7 m: 2.5 m - too near (5.24); a bin store's door the same (5.24, 5.45).
        Assert.DoesNotContain(Problems(document, exit), problem => problem.Rule.Contains("(5.24"));
        exit.DistanceAlongWall = 7000;
        Assert.Contains(Problems(document, exit), problem => problem.Said == "it is a final exit 2.5 m from the Boiler Room's door." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.24)", StringComparison.Ordinal));
        boiler.Name = "Bin Store";
        Assert.Contains(Problems(document, exit), problem => problem.Said == "it is a final exit 2.5 m from the Bin Store's door." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.24, 5.45)", StringComparison.Ordinal));

        // Back at x 3 m, a basement's smoke outlet - a stallboard grille - put 2 m off it: too near.
        exit.DistanceAlongWall = 3000;
        Assert.DoesNotContain(Problems(document, exit), problem => problem.Rule.Contains("(5.24"));
        Put(document, level, "Smoke Outlet - Stallboard Grille 1200 x 450", 600, -300);
        Assert.Contains(Problems(document, exit), problem => problem.Said.StartsWith("it is a final exit 1.", StringComparison.Ordinal) && problem.Said.EndsWith("m from a basement smoke outlet.", StringComparison.Ordinal));
    }

    /// <summary>
    /// An office 14 m by 10 m on a basement, a ground and a first floor: on the two above the ground a stair at the west end (x 0 to 3 m) off a
    /// corridor along the south, an office north of it; the basement one room. A lift in the north-east corner, its shaft 2.2 m by 2.5 m of
    /// unrated partitions, a landing door on each storey.
    /// </summary>
    private static (BimDocument Document, List<Level> Levels, Component Lift, List<Room> Basement) Lifted()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var basement = new Level { Name = "Basement", Elevation = -3000 };
        document.AddLevel(basement);
        var levels = new List<Level> { basement };
        levels.AddRange(document.Levels.Where(level => level.Elevation >= 0).OrderBy(level => level.Elevation).Take(2));
        var below = new List<Room>();
        foreach (var level in levels)
        {
            Outside(document, level.Id, 14000, 10000);
            var shaftFront = Wall(document, level.Id, new Point2D(11800, 7500), new Point2D(14000, 7500), Plain);
            Wall(document, level.Id, new Point2D(11800, 7500), new Point2D(11800, 10000), Plain);
            Room(document, level.Id, "Lift Shaft", 12900, 8750);
            Door(document, shaftFront, 1100, "Lift Landing Door - Sliding 900 x 2100");
            if (level == basement)
            {
                below.Add(Room(document, level.Id, "Basement", 6000, 5000));
                continue;
            }

            var stairWall = Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 10000));
            var corridorWall = Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(14000, 2000));
            Door(document, stairWall, 1000, "Fire Door FD30S - 900 x 2100");
            Door(document, corridorWall, 5500);
            Room(document, level.Id, "Stair", 1500, 8000);
            Room(document, level.Id, "Office", 8500, 5000);
            Room(document, level.Id, "Corridor", 8500, 1000);
        }

        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = levels[1].Id, TopLevelId = levels[2].Id, Start = new Point2D(1500, 2600), Angle = 90, Width = 900 });
        var lift = Put(document, basement.Id, "Passenger Lift - 8 Person, Car 1100 x 1400", 12900, 8750);
        return (document, levels, lift, below);
    }

    [Fact]
    public void ALiftWellIsEnclosedAProtectedShaftThroughACompartmentFloorAndLobbiedInABasement()
    {
        // A library: an office of these three storeys of 132 m² would be small premises (2.2, 4.2), which ask no compartment floor over a
        // basement (8.11c) - a library, assembly and recreation, is not offered them.
        var (document, levels, lift, basement) = Lifted();
        document.ProjectInformation.BuildingType = "Library";
        Assert.False(HorizontalEscape.SmallPremises(document).Small);
        var standard = document.ProjectInformation.ClearanceStandard;
        var placed = Assert.Single(Lifts.Of(document));
        Assert.Equal(3, placed.Landings.Count);

        // A landing door upstairs opens onto the car, not a drop: no Juliet balcony asked of it (Approved Document K).
        Assert.Empty(OpeningGuarding.Problems(document, placed.Landings.Single(landing => landing.Level.Id == levels[2].Id).Door));

        // The ground floor over a basement a compartment floor (8.11c): the lift well a protected shaft, its walls the building's period (5.35).
        Assert.NotNull(FireCompartments.CompartmentFloorAt(document, 0));
        var period = Math.Max(30, FireCompartments.Minutes(document));
        var said = Lifts.Problems(document, lift, standard);
        Assert.Contains(said, problem => problem.Said.Contains("Interior - Unrated Partition 100mm to the Office on the Ground Floor")
                                         && problem.Said.EndsWith($"stand less than {period} minutes.", StringComparison.Ordinal)
                                         && problem.Rule.StartsWith("Approved Document B Volume 2 (5.35, Section 8, Table B1)", StringComparison.Ordinal));

        // In the basement its landing door opens into the basement itself, not a protected lobby (5.38); and it goes down there past the
        // building's only escape stair from the upper storeys (5.40a).
        Assert.Contains(said, problem => problem.Said == "its landing door - the Basement's opens into the Basement - not a protected stairway, lobby or corridor." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.38", StringComparison.Ordinal));
        Assert.Contains(said, problem => problem.Said == "it serves the basement and the storeys above the ground, which have only one escape stair." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.40a)", StringComparison.Ordinal));

        // The shaft's walls made the building's period; the basement room a lift lobby: those met.
        Unrated(document).FireRating = $"{period} min";
        basement[0].Name = "Lift Lobby";
        said = Lifts.Problems(document, lift, standard);
        Assert.DoesNotContain(said, problem => problem.Rule.Contains("(5.35") || problem.Rule.Contains("(5.38"));

        // No basement: no compartment floor - the well enclosed in REI 30 only (5.34b); unrated, said so.
        Unrated(document).FireRating = string.Empty;
        lift.LevelId = levels[1].Id;
        foreach (var element in document.Elements.Where(element => LevelOf(element) == levels[0].Id).ToList()) document.Remove(element);
        document.RemoveLevel(levels[0]);
        Assert.Null(FireCompartments.CompartmentFloorAt(document, 0));
        said = Lifts.Problems(document, lift, standard);
        Assert.Contains(said, problem => problem.Said.EndsWith("stand less than 30 minutes.", StringComparison.Ordinal) && problem.Rule.StartsWith("Approved Document B Volume 2 (5.34b, Table B1)", StringComparison.Ordinal));
        Assert.DoesNotContain(said, problem => problem.Rule.Contains("(5.40"));
    }

    private static Guid? LevelOf(BIMDesigner.Core.Elements.Element element) => element switch
    {
        Wall wall => wall.LevelId,
        Door door => door.LevelId,
        Room room => room.LevelId,
        _ => null
    };

    [Fact]
    public void AStairsFinalExitIsAsWideAsTheStairNeedsToBe()
    {
        // The two storey office's stair: its final exit through the ground floor's corridor a door 800 wide, 660 clear - Table 3.1 asks 800 (5.20).
        var (document, levels, _, _) = Lifted();
        var ground = levels[1];
        var corridorExit = Door(document, document.Walls.First(wall => wall.LevelId == ground.Id && wall.Start == new Point2D(14000, 0)), 1000, "Single Glazed - 800 x 2100");
        var stair = Assert.Single(VerticalEscape.Stairs(document));
        Assert.Contains(VerticalEscape.Problems(document, stair), said => said.StartsWith("its final exit gives 660 where the stair needs to be 800 wide - Approved Document B Volume 2 (5.20)", StringComparison.Ordinal));

        // A 1000 door in its place: 860 - met.
        corridorExit.TypeId = document.TypesOf<DoorType>().First(type => type.Name == Wide).Id;
        Assert.DoesNotContain(VerticalEscape.Problems(document, stair), said => said.Contains("(5.20)"));
        Assert.Equal(1000, VerticalEscape.Needed(800, 1, 60));
        Assert.Equal(1100, VerticalEscape.Needed(800, 1, 220));
    }

    [Fact]
    public void InPhasedEvacuationALiftIsEnteredThroughAProtectedLobbyOnEveryStorey()
    {
        // The office over a basement, its lift's landing doors into the offices above the ground: evacuated at once, nothing asked of them.
        var (document, levels, lift, _) = Lifted();
        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.DoesNotContain(Lifts.Problems(document, lift, standard), problem => problem.Rule.Contains("(5.37"));

        // Designed for phased evacuation: a protected lobby before the lift on every storey (5.37, 3.21b) - the basement's said by 5.38 already.
        document.ProjectInformation.PhasedEvacuation = true;
        Assert.Contains(Lifts.Problems(document, lift, standard), problem => problem.Said == $"its landing doors - the {levels[1].Name}'s opens into the Office; the {levels[2].Name}'s opens into the Office - not a protected lobby."
                                                                            && problem.Rule.StartsWith("Approved Document B Volume 2 (5.37, 3.21b)", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefuseStoresLobbyHasPermanentVentilation()
    {
        // A shop 12 m by 8 m, a bin store in the south-east corner reached through a lobby 1.5 m wide off the sales floor (5.44).
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        var level = document.Levels[0].Id;
        var walls = Outside(document, level, 12000, 8000);
        Door(document, Wall(document, level, new Point2D(9000, 0), new Point2D(9000, 4000)), 2000);
        Door(document, Wall(document, level, new Point2D(10500, 0), new Point2D(10500, 4000)), 2000);
        Wall(document, level, new Point2D(9000, 4000), new Point2D(12000, 4000));
        Door(document, walls[3], 4000).FlipFacing = true;
        Room(document, level, "Sales", 4500, 4000);
        var lobby = Room(document, level, "Lobby", 9750, 2000);
        var bins = Room(document, level, "Bin Store", 11250, 2000);
        Assert.Contains(EscapeRoutes.Problems(document), problem => problem.Room == bins && problem.Said.StartsWith("it is a refuse store come to through the Lobby, which has no permanent ventilation - Approved Document B Volume 2 (5.44)", StringComparison.Ordinal));

        // A louvre in the lobby's outside wall, permanently open, 0.25 m² free: met.
        var louvre = document.TypesOf<WindowType>().First(each => each.Name == "Awning - 1200 x 600").Duplicate("Louvre - Permanent 1200 x 600");
        louvre.SmokeVent = SmokeVentKind.Permanent;
        louvre.SmokeVentArea = 0.25;
        document.AddType(louvre);
        document.Add(new Window { HostWallId = walls[0].Id, DistanceAlongWall = 9750, LevelId = level, TypeId = louvre.Id });
        Assert.Equal(0.25, VerticalEscape.PermanentVentilation(document, lobby), 3);
        Assert.DoesNotContain(EscapeRoutes.Problems(document), problem => problem.Room == bins && problem.Said.Contains("(5.44)"));
    }

    [Fact]
    public void ARefuseStoreAndAShopsStoreRoomAreEnclosed()
    {
        // A shop 12 m by 8 m: a stock room 3 m by 3 m in the north-east corner, a bin store in the south-east, both of unrated partitions
        // and both entered from the sales floor.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        var level = document.Levels[0].Id;
        var walls = Outside(document, level, 12000, 8000);
        var dividing = Wall(document, level, new Point2D(9000, 0), new Point2D(9000, 4000), Plain);
        var stockWall = Wall(document, level, new Point2D(9000, 4000), new Point2D(9000, 8000), Plain);
        Wall(document, level, new Point2D(9000, 4000), new Point2D(12000, 4000), Plain);
        Room(document, level, "Sales", 4500, 4000);
        var stock = Room(document, level, "Stock Room", 10500, 6000);
        var bins = Room(document, level, "Bin Store", 10500, 2000);
        Door(document, stockWall, 2000);
        var binDoor = Door(document, dividing, 2000);
        Door(document, walls[3], 4000).FlipFacing = true;

        // The partition: round the bin store REI 30 asked (5.43a), the top storey 5 m up at most; the stock room's off the sales floor (5.46).
        var said = Problems(document, dividing);
        Assert.Contains(said, problem => problem.Said == "it encloses the Bin Store, a refuse store, not fire-rated." && problem.Rule.Equals("Approved Document B Volume 2 (5.43a, Table B1) asks a room for storing refuse separated from the rest of the building by fire-resisting construction of REI 30, the top storey being 5 m up at most.", StringComparison.Ordinal));
        Assert.Contains(EscapeRoutes.Problems(document), problem => problem.Room == bins && problem.Said.StartsWith("it is a refuse store come to from the Sales - Approved Document B Volume 2 (5.44)", StringComparison.Ordinal));

        // The stock room: its wall to the sales floor REI 30 asked (5.46) - not with a smoke detector in it, nor with sprinklers.
        Assert.Contains(Problems(document, stockWall), problem => problem.Said == "it divides the Stock Room, a walk-in store room, from the Sales, not fire-rated." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.46)", StringComparison.Ordinal));
        Put(document, level, "Smoke Alarm", 10500, 6000);
        Assert.DoesNotContain(Problems(document, stockWall), problem => problem.Rule.Contains("(5.46)"));

        // The bin store come to from outside instead: 5.44 met.
        document.Remove(binDoor);
        Door(document, walls[1], 2000).FlipFacing = true;
        Assert.DoesNotContain(EscapeRoutes.Problems(document), problem => problem.Room == bins && problem.Said.Contains("(5.44)"));
        Assert.Contains("(5.43a", string.Join(" ", Problems(document, dividing).Select(problem => problem.Rule)));
        Assert.Null(EscapeRoutes.Lighting(document, bins));
        Assert.Equal("windowless accommodation", EscapeRoutes.Lighting(document, stock));
    }
}
