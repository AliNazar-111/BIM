using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Vehicle access, fire mains and hydrants for a building other than a dwelling by Approved Document B Volume 2 Sections 15 and 16 (round 115):
/// Table 15.1's share of the perimeter for a pump or a high reach appliance, a small building's 15.1, fire mains' outlets and hose (16.3, 16.4,
/// 16.6), hydrants (16.8, 16.9).
/// </summary>
[Collection("Wpf")]
public class FireAccessVolume2Tests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static List<Wall> Box(BimDocument document, Guid level, double width, double depth)
    {
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        return Enumerable.Range(0, 4).Select(i =>
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = document.TypesOf<WallType>().First(each => each.Name == Brick).Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            return wall;
        }).ToList();
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y)
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = "Single - 1000 x 2100")
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static GroundRegion Drive(BimDocument document, double x0, double y0, double x1, double y1)
    {
        var drive = new GroundRegion { LevelId = document.Levels.OrderBy(level => Math.Abs(level.Elevation)).First().Id, Surface = GroundSurface.Driveway };
        drive.SetBoundary(new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) });
        document.Add(drive);
        return drive;
    }

    private static Component Put(BimDocument document, Guid level, string type, double x, double y, DesignPhase phase = DesignPhase.New)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = level, Location = new Point2D(x, y), Elevation = kind.DefaultElevation, PhaseCreated = phase };
        document.Add(piece);
        return piece;
    }

    /// <summary>An office of so many storeys 4 m apart, each a box with one room, its front door in the south wall at x 5 m.</summary>
    private static BimDocument Office(double width, double depth, int storeys = 1)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var levels = document.Levels.Where(level => level.Elevation >= 0).OrderBy(level => level.Elevation).Take(1).ToList();
        for (var i = 1; i < storeys; i++)
        {
            var level = new Level { Name = $"Floor {i}", Elevation = i * 4000 };
            document.AddLevel(level);
            levels.Add(level);
        }

        foreach (var level in levels)
        {
            var walls = Box(document, level.Id, width, depth);
            Room(document, level.Id, "Office", width / 2, depth / 2);
            if (level == levels[0]) Door(document, walls[0], 5000);
        }

        return document;
    }

    private static IReadOnlyList<AccessProblem> Problems(BimDocument document) => FireAccess.Problems(document);

    [Fact]
    public void ASmallBuildingIsReachedAlong15PercentOrWithin45MOfItsFootprint()
    {
        // A single storey office 40 m by 30 m - about 1200 m², a small building (15.1). A drive stub 4 m long by its south-west corner: 3% of the
        // perimeter, and its north-east corner 48 m off.
        var document = Office(40000, 30000);
        Assert.Null(FireAccess.Table151(document).Share);
        var drive = Drive(document, 0, -6000, 4000, -2300);
        var said = Assert.Single(Problems(document), problem => problem.Said.Contains("(15.1, Diagram 15.1)"));
        Assert.StartsWith("the drive reaches 3% of the building's perimeter", said.Said);
        Assert.Contains("asks a building of 1", said.Said);

        // Run along the whole south side: 40 m of 141 m - 28% - met.
        drive.SetBoundary(new[] { new Point2D(-2000, -6000), new Point2D(42000, -6000), new Point2D(42000, -2300), new Point2D(-2000, -2300) });
        Assert.DoesNotContain(Problems(document), problem => problem.Said.Contains("(15.1,"));

        // Not the house's 45 m to every point inside: no Volume 1 (13.1) said of an office.
        Assert.DoesNotContain(Problems(document), problem => problem.Said.Contains("(13.1)"));
    }

    [Fact]
    public void Table151AsksAShareOfThePerimeterForAPumpOrAHighReachAppliance()
    {
        // Two storeys 50 m by 25 m - about 2450 m², the top 4 m up: 15% for a pump. A drive along 20 m of the south side, 2 m off the face: 13%.
        var document = Office(50000, 25000, 2);
        var (area, top, share, high) = FireAccess.Table151(document);
        Assert.InRange(area, 2400, 2500);
        Assert.Equal(4, top, 3);
        Assert.Equal(0.15, share!.Value, 6);
        Assert.False(high);
        var drive = Drive(document, 0, -5865, 20000, -2165);
        Assert.Contains(Problems(document), problem => problem.Said.StartsWith("the drive reaches 13% of the building's perimeter", StringComparison.Ordinal)
                                                       && problem.Said.Contains("Approved Document B Volume 2 (15.2, 15.8a, Table 15.1) asks 15% for a building of 24"));
        drive.SetBoundary(new[] { new Point2D(0, -5865), new Point2D(30000, -5865), new Point2D(30000, -2165), new Point2D(0, -2165) });
        Assert.DoesNotContain(Problems(document), problem => problem.Said.Contains("Table 15.1"));

        // Four storeys 20 m by 10 m, the top 12 m up: 15% for a high reach appliance (Table 15.1). A road 3.7 m wide 1 m off the face is no
        // hardstanding for one - not 10 m out for a turntable ladder, nor 7.5 m for a platform (Diagram 15.2).
        document = Office(20000, 10000, 4);
        (_, top, share, high) = FireAccess.Table151(document);
        Assert.Equal(12, top, 3);
        Assert.Equal(0.15, share!.Value, 6);
        Assert.True(high);
        drive = Drive(document, 0, -4865, 20000, -1165);
        Assert.Contains(Problems(document), problem => problem.Said.StartsWith("the drive reaches 0% of the building's perimeter", StringComparison.Ordinal) && problem.Said.Contains("for a high reach appliance"));

        // A hardstanding 10 m wide from 1 m out to 11 m: 20 m of 61 m reached - met.
        drive.SetBoundary(new[] { new Point2D(0, -11165), new Point2D(20000, -11165), new Point2D(20000, -1165), new Point2D(0, -1165) });
        Assert.DoesNotContain(Problems(document), problem => problem.Said.Contains("Table 15.1"));
    }

    [Fact]
    public void ABuildingOver280SquareMetresHasAHydrantWithin100MOrItsOwnWithin90M()
    {
        // The single storey office, its drive along the south side: one compartment of about 1200 m² and no hydrant drawn.
        var document = Office(40000, 30000);
        Drive(document, -2000, -6000, 42000, -2300);
        var level = document.Levels.OrderBy(each => Math.Abs(each.Elevation)).First().Id;
        Assert.Contains(Problems(document), problem => problem.Said.Contains("and no fire hydrant is drawn within 100 m of it, nor one of its own within 90 m of an entrance - Approved Document B Volume 2 (16.8, 16.9)"));

        // An existing hydrant in the road 95 m south of the building: met. Moved to 110 m: not.
        var existing = Put(document, level, "Fire Hydrant - Underground, BS 750", 20000, -95165, DesignPhase.Existing);
        Assert.DoesNotContain(Problems(document), problem => problem.Said.Contains("(16.8"));
        existing.Location = new Point2D(20000, -110165);
        Assert.Contains(Problems(document), problem => problem.Said.Contains("(16.8, 16.9)"));

        // One of its own 85 m from the front door (at x 5 m): met. A second 120 m east of it, 147 m from the door: too far from the door, and the two too far apart.
        var own = Put(document, level, "Fire Hydrant - Underground, BS 750", 5000, -85000);
        Assert.DoesNotContain(Problems(document), problem => problem.Said.Contains("(16.8") || problem.Said.Contains("(16.9"));
        var far = Put(document, level, "Fire Hydrant - Underground, BS 750", 125000, -85000);
        var said = Problems(document).Where(problem => problem.Element == far).Select(problem => problem.Said).ToList();
        Assert.Contains(said, each => each.StartsWith("it is 1", StringComparison.Ordinal) && each.EndsWith("from an entrance - Approved Document B Volume 2 (16.9b i) asks the building's own hydrants within 90 m of an entrance to it", StringComparison.Ordinal));
        Assert.Contains(said, each => each == "it is 120.0 m from the next hydrant - Approved Document B Volume 2 (16.9b ii) asks the building's own hydrants 90 m apart at most");
        Assert.DoesNotContain(Problems(document), problem => problem.Element == own && problem.Said.Contains("(16.9b i)"));
        Assert.Contains(Problems(document), problem => problem.Element == own && problem.Said.Contains("(16.9b ii)"));
    }

    [Fact]
    public void FireMainsOutletsAreInALobbyWithin45MAndWetOver50M()
    {
        // The office 40 m by 30 m with a dry fire main: its inlet on the south face by the drive; its outlet in the office itself.
        var document = Office(40000, 30000);
        var level = document.Levels.OrderBy(each => Math.Abs(each.Elevation)).First().Id;
        Drive(document, -2000, -6000, 42000, -2300);
        Put(document, level, "Fire Main Inlet - Dry, 2 Way", 8000, -400);
        var outlet = Put(document, level, "Fire Main Outlet - Landing Valve", 1000, 1000);
        var said = Problems(document);
        Assert.Contains(said, problem => problem.Element == outlet && problem.Said == "it is in the Office, not a stair's enclosure or lobby - Approved Document B Volume 2 (16.4) asks fire main outlets in the protected stairway or protected lobby");

        // Fitted with fire mains: Table 15.1 no longer asked (15.6); 45 m of hose from the outlet - the office's far corner about 48 m off (16.3).
        Assert.DoesNotContain(said, problem => problem.Said.Contains("Table 15.1") || problem.Said.Contains("(15.1,"));
        Assert.Contains(said, problem => problem.Said.StartsWith("its furthest point is 4", StringComparison.Ordinal) && problem.Said.EndsWith("from the nearest fire main outlet - Approved Document B Volume 2 (16.3) asks 45 m at most from a fire main outlet to the furthest point", StringComparison.Ordinal));

        // The outlet in the middle: every point within 45 m.
        outlet.Location = new Point2D(20000, 15000);
        Assert.DoesNotContain(Problems(document), problem => problem.Said.Contains("(16.3)"));

        // A storey 52 m up: a dry main's inlet said - wet mains asked (16.6).
        var high = new Level { Name = "Floor 13", Elevation = 52000 };
        document.AddLevel(high);
        Box(document, high.Id, 40000, 30000);
        Assert.Contains(Problems(document), problem => problem.Said.StartsWith("it is a dry fire main's inlet where the top storey is 52.0 m up - Approved Document B Volume 2 (16.6)", StringComparison.Ordinal));
    }
}
