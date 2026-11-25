using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Doors on escape routes finished (round 137), by Approved Document B Volume 2 5.7, 5.8, 5.12, 5.14b, 5.16 and 5.22: simple fastenings, electric
/// locks with an emergency door release, swings clear of steps, double-swing doors' vision panels, headroom and level thresholds.
/// </summary>
[Collection("Wpf")]
public class EscapeDoorsTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Partition = "Interior - Partition 125mm", Wide = "Single - 1000 x 2100";

    private static Wall Wall(BimDocument document, Guid level, double x0, double y0, double x1, double y1, string type = Partition)
    {
        var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
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

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Door door) => Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard);

    /// <summary>An office 12 m by 8 m, one room, its final exit in the south wall at x 3 m - a door of its own type, to change.</summary>
    private static (BimDocument Document, Door Exit, DoorType Type, Room Office, Wall South) Office()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var level = document.Levels[0].Id;
        var south = Wall(document, level, 0, 0, 12000, 0, Brick);
        Wall(document, level, 12000, 0, 12000, 8000, Brick);
        Wall(document, level, 12000, 8000, 0, 8000, Brick);
        Wall(document, level, 0, 8000, 0, 0, Brick);
        var type = document.TypesOf<DoorType>().First(each => each.Name == Wide).Duplicate("Exit Door - 1000 x 2100");
        document.AddType(type);
        var exit = new Door { HostWallId = south.Id, TypeId = type.Id, LevelId = level, DistanceAlongWall = 3000 };
        document.Add(exit);
        var office = Room(document, level, "Office", 6000, 4000);
        return (document, exit, type, office, south);
    }

    [Fact]
    public void AnEscapeDoorOpensWithoutAKeyAndAnElectricLockHasADoorRelease()
    {
        var (document, exit, type, _, _) = Office();
        Assert.Contains(EscapeRoutes.Way(document, exit) is { } way ? way.From.Name : "-", "Office");

        // A key lock: not a simple fastening (5.7b).
        type.Fastening = DoorFastening.KeyLock;
        Assert.Contains(Problems(document, exit), problem => problem.Said == "its fastening is a lock opened by a key, on the way out of the building." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.7b)", StringComparison.Ordinal));

        // An electrically powered lock: its emergency door release wanted on the side people escape from (5.8).
        type.Fastening = DoorFastening.ElectricLock;
        Assert.Contains(Problems(document, exit), problem => problem.Said == "it has an electrically powered lock, and no emergency door release beside it in the Office, the side people escape from." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.8)", StringComparison.Ordinal));
        var release = document.TypesOf<ComponentType>().First(each => each.Form == ComponentForm.DoorRelease);
        var outside = new Component { TypeId = release.Id, TypeKind = release.Kind, LevelId = exit.LevelId, Location = new Point2D(3600, -400) };
        document.Add(outside);
        Assert.Contains(Problems(document, exit), problem => problem.Rule.StartsWith("Approved Document B Volume 2 (5.8)", StringComparison.Ordinal));
        document.Remove(outside);
        document.Add(new Component { TypeId = release.Id, TypeKind = release.Kind, LevelId = exit.LevelId, Location = new Point2D(3600, 400) });
        Assert.DoesNotContain(Problems(document, exit), problem => problem.Rule.Contains("(5.7") || problem.Rule.Contains("(5.8)"));
    }

    [Fact]
    public void ADoorSwingingBothWaysHasAVisionPanel()
    {
        var (document, exit, type, _, _) = Office();
        type.Operation = DoorOperation.DoubleSwing;
        Assert.Contains(Problems(document, exit), problem => problem.Said == "it swings both ways, with no vision panel." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.14b)", StringComparison.Ordinal));
        type.LeafDesign = DoorLeafDesign.HalfGlazed;
        Assert.DoesNotContain(Problems(document, exit), problem => problem.Rule.Contains("(5.14b)"));
    }

    [Fact]
    public void AnEscapeDoorsSwingIsClearOfSteps()
    {
        // An office of two storeys 14 m by 10 m, a stair room at its west end (x 0 to 3 m), the flight rising north from y 2.6 m at x 2 m to the
        // first floor; upstairs the office's door into the stair at y 5.5 m, by the flight's top treads.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        Door? upstairs = null;
        foreach (var level in levels)
        {
            var east = Wall(document, level.Id, 14000, 0, 14000, 10000, Brick);
            Wall(document, level.Id, 0, 0, 14000, 0, Brick);
            Wall(document, level.Id, 14000, 10000, 0, 10000, Brick);
            Wall(document, level.Id, 0, 10000, 0, 0, Brick);
            var stairWall = Wall(document, level.Id, 3000, 0, 3000, 10000);
            var stairDoor = Door(document, stairWall, 5500, "Fire Door FD30S - 900 x 2100");
            if (level == levels[0]) Door(document, east, 5000);
            else upstairs = stairDoor;
            Room(document, level.Id, "Stair", 1000, 8500);
            Room(document, level.Id, "Office", 8500, 5000);
        }

        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = levels[0].Id, TopLevelId = levels[1].Id, Start = new Point2D(2000, 2600), Angle = 90, Width = 900 });

        // Swung into the stair, over its top treads (5.12b); into the office, clear.
        var door = upstairs!;
        if (DoorSwing.Sweeps(document, door).SelectMany(sweep => sweep).Average(point => point.X) > 3000) door.FlipFacing = !door.FlipFacing;
        Assert.Contains(Problems(document, door), problem => problem.Said == "its leaf sweeps over the steps of the Stair - Timber, Closed Risers." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.12b)", StringComparison.Ordinal));
        door.DistanceAlongWall = 8000;
        Assert.DoesNotContain(Problems(document, door), problem => problem.Rule.Contains("(5.12b)"));
    }

    [Fact]
    public void AnEscapeRouteHasTwoMetresHeadroom()
    {
        // The office given a corridor along its south, a ceiling over it 1.9 m up, its underside 1875 (5.16).
        var (document, exit, _, office, _) = Office();
        var level = exit.LevelId;
        Door(document, Wall(document, level, 0, 2000, 12000, 2000), 6000);
        office.Location = new Point2D(6000, 5000);
        var corridor = Room(document, level, "Corridor", 6000, 1000);
        var ceiling = new Ceiling { LevelId = level, TypeId = document.TypesOf<CeilingType>().First().Id, HeightOffset = 1900 };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 2000), new Point2D(0, 2000) });
        document.Add(ceiling);
        Assert.Contains(EscapeRoutes.RoomWanting(document, corridor), said => said.StartsWith("its headroom is 1875, an escape route - Approved Document B Volume 2 (5.16)", StringComparison.Ordinal));
        Assert.DoesNotContain(EscapeRoutes.RoomWanting(document, office), said => said.Contains("(5.16)"));
        ceiling.HeightOffset = 2400;
        Assert.DoesNotContain(EscapeRoutes.RoomWanting(document, corridor), said => said.Contains("(5.16)"));
    }

    [Fact]
    public void AFinalExitHasALevelThresholdOrARamp()
    {
        // The ground outside 150 below the office's floor: a step at its final exit (5.22).
        var (document, exit, _, _, _) = Office();
        var surface = new Toposurface { LevelId = exit.LevelId };
        var points = new List<Point3D>();
        for (var x = -20000.0; x <= 30000; x += 5000)
        for (var y = -20000.0; y <= 30000; y += 5000)
            points.Add(new Point3D(x, y, -150));
        surface.SetPoints(points);
        document.Add(surface);
        Assert.Contains(Problems(document, exit), problem => problem.Said == "it is a final exit 150 above the ground outside it, with no ramp." && problem.Rule.StartsWith("Approved Document B Volume 2 (5.22)", StringComparison.Ordinal));

        // Ground made up level with the floor: met.
        surface.SetPoints(points.Select(point => new Point3D(point.X, point.Y, -10)).ToList());
        Assert.DoesNotContain(Problems(document, exit), problem => problem.Rule.Contains("(5.22)"));
    }
}
