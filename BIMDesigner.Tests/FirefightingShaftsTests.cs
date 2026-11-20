using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Firefighting shafts in a building other than a dwelling by Approved Document B Volume 2 Section 17 (round 116): how many are asked and
/// whether with a lift (17.2, 17.3, 17.5, 17.6); each serving its storeys with its lift, lobbies and fire main outlets (17.4, 17.9, 17.10); its
/// enclosure by Diagram 17.1; hose from its outlets (17.8).
/// </summary>
[Collection("Wpf")]
public class FirefightingShaftsTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", ShaftWall = "Firefighting Shaft - Reinforced Concrete 200mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = ShaftWall)
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type)
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

    /// <summary>The storeys of a building at so many metres apart, the ground's first: those the template has, and more added.</summary>
    private static List<Level> Levels(BimDocument document, int storeys, double apart = 3000, int basements = 0)
    {
        var levels = new List<Level> { document.Levels.Where(level => level.Elevation >= 0).OrderBy(level => level.Elevation).First() };
        for (var i = 1; i < storeys; i++)
        {
            var level = new Level { Name = $"Floor {i}", Elevation = i * apart };
            document.AddLevel(level);
            levels.Add(level);
        }

        for (var i = 1; i <= basements; i++)
        {
            var level = new Level { Name = $"Basement {i}", Elevation = -i * 3500 };
            document.AddLevel(level);
            levels.Insert(0, level);
        }

        return levels;
    }

    /// <summary>A building of plain boxes, one room on each storey.</summary>
    private static BimDocument Boxes(string type, double width, double depth, int storeys, double apart = 3000, int basements = 0)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = type;
        foreach (var level in Levels(document, storeys, apart, basements))
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
            for (var i = 0; i < 4; i++) Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick);
            Room(document, level.Id, "Office", width / 2, depth / 2);
        }

        return document;
    }

    private sealed record Tower(BimDocument Document, List<Level> Levels, List<Room> Stairs, List<Room> Lobbies, List<Door> Into, List<Component> Outlets, Component Lift, List<Wall> LobbyWalls);

    /// <summary>
    /// An office of so many storeys 3 m apart, so wide by 20 m: on each, along its west end, a stair (y 12 to 20 m) through a lobby (y 6 to
    /// 12 m) from the office, a lift's well south of the lobby (y 4 to 6 m) - all in the library's REI 120 shaft wall; an FD60S from the office
    /// into the lobby, an FD30S from the lobby to the stair; a fire main outlet in each lobby; a firefighting lift landing into each.
    /// </summary>
    private static Tower Build(double width = 30000, int storeys = 8, string stairName = "Firefighting Stair", string lobbyName = "Firefighting Lobby")
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var levels = Levels(document, storeys);
        var (stairs, lobbies, into, outlets, lobbyWalls) = (new List<Room>(), new List<Room>(), new List<Door>(), new List<Component>(), new List<Wall>());
        foreach (var level in levels)
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, 20000), new Point2D(0, 20000) };
            var outside = Enumerable.Range(0, 4).Select(i => Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick)).ToList();
            Wall(document, level.Id, new Point2D(3000, 4000), new Point2D(3000, 6000));
            var lobbyWall = Wall(document, level.Id, new Point2D(3000, 6000), new Point2D(3000, 12000));
            Wall(document, level.Id, new Point2D(3000, 12000), new Point2D(3000, 20000));
            Wall(document, level.Id, new Point2D(0, 4000), new Point2D(3000, 4000));
            var well = Wall(document, level.Id, new Point2D(0, 6000), new Point2D(3000, 6000));
            var stairWall = Wall(document, level.Id, new Point2D(0, 12000), new Point2D(3000, 12000));
            lobbyWalls.Add(lobbyWall);
            stairs.Add(Room(document, level.Id, stairName, 2200, 19500));
            lobbies.Add(Room(document, level.Id, lobbyName, 1500, 9000));
            Room(document, level.Id, "Lift Shaft", 1500, 5000);
            Room(document, level.Id, "Office", width / 2, 10000);
            into.Add(Door(document, lobbyWall, 3000, "Fire Door FD60S - 1000 x 2100"));
            Door(document, stairWall, 1500, "Fire Door FD30S - 900 x 2100");
            Door(document, well, 1500, "Lift Landing Door - Sliding 900 x 2100");
            outlets.Add(Put(document, level.Id, "Fire Main Outlet - Landing Valve", 800, 9000));
            if (level != levels[0]) continue;
            Door(document, outside[2], width - 1500, "Single - 1000 x 2100");
            Door(document, outside[0], 10000, "Single - 1000 x 2100");
        }

        foreach (var (foot, head) in levels.Zip(levels.Skip(1)))
            document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = foot.Id, TopLevelId = head.Id, Start = new Point2D(1500, 12600), Angle = 90, Width = 1100 });
        var lift = Put(document, levels[0].Id, "Firefighting Lift - Car 1100 x 1400, BS EN 81-72", 1500, 5000);
        return new Tower(document, levels, stairs, lobbies, into, outlets, lift, lobbyWalls);
    }

    [Fact]
    public void ShaftsAreAskedByHeightDepthAndStoreyArea()
    {
        // Top storey 21 m up, storeys of 600 m²: one, with a firefighting lift (17.2). 15 m up: none.
        Assert.Equal((1, true, true, false), Shape(FirefightingShafts.Needed(Boxes("Office", 30000, 20000, 8))!));
        Assert.Null(FirefightingShafts.Needed(Boxes("Office", 30000, 20000, 6)));

        // Storeys of about 1000 m² and one 18 m up or more: two (17.5).
        Assert.Equal(2, FirefightingShafts.Needed(Boxes("Office", 50000, 20000, 8))!.Shafts);

        // A shop of storeys of 1000 m², its top 9 m up: two, without lifts (17.6); an office of the same, none.
        Assert.Equal((2, false, true, false), Shape(FirefightingShafts.Needed(Boxes("Shop", 50000, 20000, 4))!));
        Assert.Null(FirefightingShafts.Needed(Boxes("Office", 50000, 20000, 4)));

        // Two basement storeys of 1000 m² each: shafts down to them, no lift (17.3b). Four, the lowest 14 m down: with a lift (17.3a).
        Assert.Equal((1, false, false, true), Shape(FirefightingShafts.Needed(Boxes("Office", 50000, 20000, 1, basements: 2))!));
        Assert.Equal((1, true, false, true), Shape(FirefightingShafts.Needed(Boxes("Office", 30000, 20000, 1, basements: 4))!));
    }

    private static (int, bool, bool, bool) Shape(FirefightingShafts.Asked asked) => (asked.Shafts, asked.Lift, asked.Upper, asked.Basement);

    [Fact]
    public void AFirefightingShaftHasItsLiftLobbiesOutletsAndEnclosure()
    {
        // Eight storeys, the top 21 m up: the shaft as Diagram 17.1 draws it - nothing wanting.
        var tower = Build();
        var document = tower.Document;
        var shaft = Assert.Single(FirefightingShafts.Of(document));
        Assert.NotNull(shaft.Lift);
        Assert.Equal(8, shaft.Stair.Rooms.Count);
        Assert.Empty(FirefightingShafts.Problems(document));

        // The top storey's door into the lobby an FD30S: E 60 Sa asked. Its lobby's wall to the office a 30 minute partition: REI 120 asked.
        var top = tower.Levels[^1].Name;
        tower.Into[^1].TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Fire Door FD30S - 900 x 2100").Id;
        tower.LobbyWalls[^1].TypeId = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm").Id;
        var said = FirefightingShafts.Problems(document).Select(problem => problem.Said).ToList();
        Assert.Contains(said, each => each.StartsWith("its firefighting shaft's doors stand short: the Firefighting Lobby's door to the Office on the " + top + ", E 60 Sa asked", StringComparison.Ordinal)
                                      && each.EndsWith("Approved Document B Volume 2 (Diagram 17.1, Table C1) asks the doors from the accommodation into a firefighting lobby E 60 Sa and from the lobby to the stair E 30 Sa, self-closing", StringComparison.Ordinal));
        Assert.Contains(said, each => each.StartsWith("its firefighting shaft's walls stand short: Interior - Partition 125mm between the ", StringComparison.Ordinal) && each.Contains($"on the {top}, REI 120 asked"));

        // The third storey's outlet taken away; the lift taken away.
        document.Remove(tower.Outlets[2]);
        document.Remove(tower.Lift);
        said = FirefightingShafts.Problems(document).Select(problem => problem.Said).ToList();
        Assert.Contains(said, each => each.StartsWith($"its firefighting shaft has no fire main outlet on the {tower.Levels[2].Name} - Approved Document B Volume 2 (17.10", StringComparison.Ordinal));
        Assert.Contains(said, each => each.StartsWith("its firefighting shaft has no firefighting lift landing into it - Approved Document B Volume 2 (17.2, 17.3a", StringComparison.Ordinal));

        // Its well still part of the shaft with no lift in it: its landing doors are not asked E 60 Sa as doors to the accommodation.
        Assert.DoesNotContain(said, each => each.Contains("door to the Lift Shaft"));
        Assert.All(FirefightingShafts.Problems(document), problem => Assert.Equal(tower.Stairs[0], problem.Room));
    }

    [Fact]
    public void AStairNotCalledFirefightingIsNoFirefightingShaft()
    {
        // The same building, its stair and lobbies called plainly: none found - one asked, with a lift (17.2).
        var tower = Build(stairName: "Stair", lobbyName: "Lobby");
        var document = tower.Document;
        Assert.Empty(FirefightingShafts.Of(document));
        var said = Assert.Single(FirefightingShafts.Problems(document));
        Assert.StartsWith("its top storey is 21.0 m up, and it has no firefighting shaft - Approved Document B Volume 2 (17.2, Diagram 17.1) asks one at least, with a firefighting lift", said.Said);

        // Its lobbies renamed Firefighting Lobby: found.
        foreach (var lobby in tower.Lobbies) lobby.Name = "Firefighting Lobby";
        Assert.Single(FirefightingShafts.Of(document));
        Assert.Empty(FirefightingShafts.Problems(document));
    }

    [Fact]
    public void EveryPointUpHighIsWithinTheHoseOfAnOutlet()
    {
        // The building 70 m long: storeys of 1400 m², two shafts asked (17.5) - and the office's far end some 70 m from the one outlet on each
        // storey: 60 m asked from a firefighting shaft's (17.8a), 45 m from a protected stair's without sprinklers (17.8b).
        var tower = Build(width: 70000);
        var document = tower.Document;
        var said = FirefightingShafts.Problems(document).ToList();
        Assert.Contains(said, problem => problem.Said.StartsWith("its top storey is 21.0 m up, and a storey of 900 m² or more is 18 m up or more, and it has 1 firefighting shaft - Approved Document B Volume 2 (17.2, 17.5, Diagram 17.1) asks two at least, with a firefighting lift", StringComparison.Ordinal));
        var office = said.Where(problem => problem.Room.Name == "Office").Select(problem => problem.Said).ToList();
        Assert.Contains(office, each => each.StartsWith("its furthest point is 6", StringComparison.Ordinal) && each.EndsWith("Approved Document B Volume 2 (17.7, 17.8a, Diagram 17.3) asks 60 m at most on every storey more than 18 m up", StringComparison.Ordinal));
        Assert.Contains(office, each => each.EndsWith("and the building has no sprinklers - Approved Document B Volume 2 (17.8b, Diagram 17.3) asks 45 m at most as well", StringComparison.Ordinal));
        Assert.Single(FirefightingShafts.Problems(document).Select(problem => problem.Room).Where(room => room.Name == "Office").Distinct());

        // Sprinklered: 17.8b no longer asked.
        document.ProjectInformation.Sprinklered = true;
        Assert.DoesNotContain(FirefightingShafts.Problems(document), problem => problem.Said.Contains("(17.8b"));
    }
}
