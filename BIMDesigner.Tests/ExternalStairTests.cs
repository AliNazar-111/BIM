using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// External escape stairs (round 133), by Approved Document B Volume 2 3.24d, 3.29 to 3.33 and Volume 1 3.66 to 3.71: the stair found and
/// its doors ways out, an internal stair from every part, who may use it, its doors, the building beside it, weather and materials; and
/// protected stairways' windows across an angle in the outside wall.
/// </summary>
[Collection("Wpf")]
public class ExternalStairTests
{
    private const string FireDoor = "Fire Door FD30S - 900 x 2100", Wide = "Single - 1000 x 2100", Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
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

    private static Window Window(BimDocument document, Wall wall, double along, string type = "Casement - 1200 x 1200")
    {
        var window = new Window { HostWallId = wall.Id, TypeId = document.TypesOf<WindowType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(window);
        return window;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y)
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    private sealed record Building(BimDocument Document, List<Level> Levels, List<Room> Stairs, List<Room> Offices, List<Room> Corridors, Dictionary<Guid, List<Wall>> Outside, List<Door> StairDoors);

    /// <summary>
    /// A building 14 m by 10 m on so many storeys so far apart: on each an internal stair at the west end (x 0 to 3 m) entered from a corridor
    /// along the south (y 0 to 2 m) through an FD30S door, an office north of the corridor; a final exit from the ground floor's corridor at its
    /// east end; the internal stair 900 wide rising north.
    /// </summary>
    private static Building Build(string use = "Office", int storeys = 2, double height = 3000)
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

        foreach (var spare in document.Levels.Where(level => !levels.Contains(level))) spare.Elevation = storeys * height + 3000;
        levels = levels.Take(storeys).ToList();
        var (stairs, offices, corridors, doors) = (new List<Room>(), new List<Room>(), new List<Room>(), new List<Door>());
        var outsides = new Dictionary<Guid, List<Wall>>();
        foreach (var level in levels)
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(14000, 0), new Point2D(14000, 10000), new Point2D(0, 10000) };
            outsides[level.Id] = Enumerable.Range(0, 4).Select(i => Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick)).ToList();
            var stairWall = Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 10000));
            var corridorWall = Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(14000, 2000));
            doors.Add(Door(document, stairWall, 1000));
            Door(document, corridorWall, 5500, Wide);
            if (level == levels[0]) Door(document, outsides[level.Id][1], 1000, Wide);
            stairs.Add(Room(document, level.Id, "Stair", 1500, 8000));
            offices.Add(Room(document, level.Id, "Office", 8500, 6000));
            corridors.Add(Room(document, level.Id, "Corridor", 8500, 1000));
        }

        foreach (var (foot, head) in levels.Zip(levels.Skip(1)))
            document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = foot.Id, TopLevelId = head.Id, Start = new Point2D(1500, 2600), Angle = 90, Width = 900 });
        return new Building(document, levels, stairs, offices, corridors, outsides, doors);
    }

    /// <summary>An external stair outside the east wall (x 14.5 to 15.5 m), rising north from a foot at y so far, between two storeys.</summary>
    private static Stair Outside(Building building, int from, double y)
    {
        var stair = new Stair
        {
            TypeId = building.Document.TypesOf<StairType>().First().Id, LevelId = building.Levels[from].Id, TopLevelId = building.Levels[from + 1].Id,
            Start = new Point2D(15000, y), Angle = 90, Width = 1000, Outside = true
        };
        building.Document.Add(stair);
        return stair;
    }

    [Fact]
    public void AnExternalStairIsFoundAndItsDoorIsAWayOutWhereADoorOntoADropIsNot()
    {
        // A shop of two storeys; upstairs its internal stair's door taken away, a door out of the office in the east wall onto nothing: no way out.
        var building = Build("Shop");
        var document = building.Document;
        document.Remove(building.StairDoors[1]);
        var door = Door(document, building.Outside[building.Levels[1].Id][1], 4800, Wide);
        Assert.Empty(ExternalStairs.Of(document));
        Assert.Contains("no way from it to a storey exit", HorizontalEscape.Verdict(document, building.Offices[1]));

        // An external stair outside it, from the ground up to the door: found, its door onto it the office's way out.
        var stair = Outside(building, 0, 600);
        var placed = Assert.Single(ExternalStairs.Of(document));
        Assert.Equal(door, Assert.Single(placed.Doors).Door);
        Assert.Same(building.Offices[1], placed.Doors[0].Room);
        Assert.DoesNotContain("no way from it to a storey exit", HorizontalEscape.Verdict(document, building.Offices[1]));

        // ... and the internal stair is not the only one any longer (3.24a), though the office has no internal stair from it (3.31a).
        document.Add(building.StairDoors[1]);
        Assert.DoesNotContain("that is the only stair (3.24a)", VerticalEscape.Verdict(document, building.Stairs[1]));
        Assert.DoesNotContain("(3.31a)", Stairs.Verdict(document, stair));
        document.Remove(building.StairDoors[1]);
        Assert.Contains($"on the {building.Levels[1].Name} the Office, the Corridor have no internal escape stair, only this external one - Approved Document B Volume 2 (3.31a)", Stairs.Verdict(document, stair));
    }

    [Fact]
    public void AnExternalStairsDoorsAndTheBuildingBesideItAreFireResisting()
    {
        // An office of two storeys, an external stair up the east wall from y 4 m, clear of the final exit, to a door from the upstairs office:
        // the single door onto its top landing needs no fire resistance (3.32a); a second door onto it does, and so does the first then.
        var building = Build();
        var document = building.Document;
        var east = building.Outside[building.Levels[1].Id][1];
        var top = Door(document, east, 8200, Wide);
        var stair = Outside(building, 0, 4000);
        Assert.DoesNotContain("(3.32a)", Stairs.Verdict(document, stair));
        Assert.DoesNotContain("(3.32b", Stairs.Verdict(document, stair));

        // A fire escape from a storey above is no approach to the building: Approved Document M Volume 2's external steps (1.33) not asked of it.
        Assert.True(ExternalStairs.EscapeOnly(document, stair));
        Assert.DoesNotContain("(1.33", Stairs.Verdict(document, stair));
        var second = Door(document, east, 6900, Wide);
        Assert.Equal(2, ExternalStairs.Of(document)[0].Doors.Count);
        Assert.Contains($"its doors - the {building.Levels[1].Name}'s, Single - 1000 x 2100 - not E 30 with a closer - Approved Document B Volume 2 (3.32a)", Stairs.Verdict(document, stair));
        foreach (var each in new[] { top, second }) each.TypeId = document.TypesOf<DoorType>().First(type => type.Name == FireDoor).Id;
        Assert.DoesNotContain("(3.32a)", Stairs.Verdict(document, stair));

        // A window in the east wall beside the stair at the ground: opening and not E 30 (3.32b, e).
        var window = Window(document, building.Outside[building.Levels[0].Id][1], 4500);
        Assert.Contains($"the building beside it is not fire-resisting: the Casement - 1200 x 1200 window on the {building.Levels[0].Name}, opening and not E 30 - Approved Document B Volume 2 (3.32b, e, Diagram 3.4)", Stairs.Verdict(document, stair));

        // A fixed E 30 window in its place: met.
        var fixedType = document.TypesOf<WindowType>().First(each => each.Name == "Casement - 1200 x 1200").Duplicate("Fixed - E 30 1200 x 1200");
        fixedType.Operation = WindowOperation.Fixed;
        fixedType.FireRating = "E 30";
        document.AddType(fixedType);
        window.TypeId = fixedType.Id;
        Assert.DoesNotContain("(3.32b", Stairs.Verdict(document, stair));
    }

    [Fact]
    public void AnExternalStairOverSixMetresIsRoofedAndNonCombustible()
    {
        // Three storeys 3.5 m apart, an external stair of two flights up the east wall to the top: 7 m high, timber, nothing over it (3.32d, 3.24d).
        var building = Build(storeys: 3, height: 3500);
        var document = building.Document;
        Door(document, building.Outside[building.Levels[1].Id][1], 4800, FireDoor);
        Door(document, building.Outside[building.Levels[2].Id][1], 9400, Wide);
        var lower = Outside(building, 0, 600);
        Outside(building, 1, 5100);
        var placed = Assert.Single(ExternalStairs.Of(document));
        Assert.Equal(2, placed.Stairs.Count);
        Assert.Equal(2, placed.Doors.Count);
        var said = Stairs.Verdict(document, lower);
        Assert.Contains("it is 7.0 m high with nothing over it - Approved Document B Volume 2 (3.32d)", said);
        Assert.Contains("its flights are Stair - Timber, Closed Risers, timber D-s2, d0 - Approved Document B Volume 2 (3.24d)", said);

        // A canopy over it and concrete flights: met.
        var canopy = new Level { Name = "Canopy", Elevation = 9500 };
        document.AddLevel(canopy);
        var slab = new Floor { LevelId = canopy.Id, TypeId = document.TypesOf<FloorType>().First(each => each.Name == "Floor - Screed on Slab 250mm").Id };
        slab.SetBoundary(new[] { new Point2D(14200, 0), new Point2D(16000, 0), new Point2D(16000, 10000), new Point2D(14200, 10000) });
        document.Add(slab);
        document.FindType<StairType>(lower.TypeId)!.Construction = StairConstruction.Concrete;
        said = Stairs.Verdict(document, lower);
        Assert.DoesNotContain("(3.32d)", said);
        Assert.DoesNotContain("(3.24d)", said);

        // Flats: an external stair serving a floor 7 m up (3.70b) - another stair serving them.
        document.ProjectInformation.BuildingType = "Block of Flats";
        Assert.Contains($"it serves the {building.Levels[2].Name} 7.0 m up - Approved Document B Volume 1 (3.70b)", Stairs.Verdict(document, lower));
    }

    [Fact]
    public void WhoMayUseAnExternalStairInAnAssemblyOrCareBuilding()
    {
        // A library's external stair from an upstairs office: staff only, allowed; from a reading room the public use, not (3.31b).
        var building = Build("Library");
        var document = building.Document;
        Door(document, building.Outside[building.Levels[1].Id][1], 4800, FireDoor);
        var stair = Outside(building, 0, 600);
        Assert.DoesNotContain("(3.31b)", Stairs.Verdict(document, stair));
        building.Offices[1].Name = "Reading Room";
        Assert.Contains("its door from the Reading Room - a part the public use, as read by its name - Approved Document B Volume 2 (3.31b)", Stairs.Verdict(document, stair));

        // A care home: only offices and staff accommodation (3.31c).
        document.ProjectInformation.BuildingType = "Care Home";
        building.Offices[1].Name = "Lounge";
        Assert.Contains("its door from the Lounge - Approved Document B Volume 2 (3.31c)", Stairs.Verdict(document, stair));
        building.Offices[1].Name = "Staff Room";
        Assert.DoesNotContain("(3.31c)", Stairs.Verdict(document, stair));
    }

    [Fact]
    public void AProtectedStairwaysWindowsAreKeptFromTheBuildingsAcrossAnInternalAngle()
    {
        // An office 14 m by 10 m of two storeys, its stair in a tower 3 m by 4.5 m projecting from the north wall (x 6 to 9 m), entered through an
        // FD30S door; a window in the tower's east side near the façade and one in the façade near the tower: 0.4 m apart across the angle (3.30).
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        Wall? towerEast = null, north = null;
        foreach (var level in levels)
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(14000, 0), new Point2D(14000, 10000), new Point2D(0, 10000) };
            var outside = Enumerable.Range(0, 4).Select(i => Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick)).ToList();
            Wall(document, level.Id, new Point2D(6000, 10000), new Point2D(6000, 14500), Brick);
            Wall(document, level.Id, new Point2D(6000, 14500), new Point2D(9000, 14500), Brick);
            var east = Wall(document, level.Id, new Point2D(9000, 14500), new Point2D(9000, 10000), Brick);
            Door(document, outside[2], 6500);
            if (level == levels[0])
            {
                Door(document, outside[0], 3000, Wide);
                (towerEast, north) = (east, outside[2]);
            }

            Room(document, level.Id, "Stair", 7500, 12500);
            Room(document, level.Id, "Office", 3000, 5000);
        }

        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = levels[0].Id, TopLevelId = levels[1].Id, Start = new Point2D(7500, 10500), Angle = 90, Width = 900 });
        Window(document, towerEast!, 3600);
        var facade = Window(document, north!, 4200);
        var said = Assert.Single(ExternalStairs.Faces(document));
        Assert.Equal("Stair", said.Room.Name);
        Assert.StartsWith($"its Casement - 1200 x 1200 window on the {levels[0].Name} 0.4 m from the Office's Casement - 1200 x 1200 window, facing each other across an angle in the outside wall - Approved Document B Volume 2 (3.29, 3.30, Diagram 3.3)", said.Said);
        Assert.Contains("(3.29, 3.30, Diagram 3.3)", VerticalEscape.Verdict(document, said.Room));

        // The façade's window moved 2.5 m along: met. A window in the tower's north face, looking away: never asked.
        facade.DistanceAlongWall = 1500;
        Assert.Empty(ExternalStairs.Faces(document));
    }
}
