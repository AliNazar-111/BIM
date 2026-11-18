using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// The fire and rescue service's access finished (round 88), as Approved Document B Volume 1 Section 13 and Section 14 have it: somewhere to
/// turn on a dead end longer than 20 m (13.4, Diagram 13.1); gateways 3.1 m wide and 3.7 m clear overhead (Table 13.1); a door 750 mm wide in
/// every elevation the drive reaches, and in a block of flats no more than 60 m apart (13.1, 13.2a); and a dry fire main as the flats'
/// alternative - its inlet within 18 m of the drive, on the face of the building and in sight, its outlets in the stair, 45 m of hose from
/// them (13.5, 14.3, 14.4).
/// </summary>
[Collection("Wpf")]
public class FireAccessRouteTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = Brick)
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static List<Wall> Box(BimDocument document, Guid level, double x0, double y0, double x1, double y1)
    {
        var corners = new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) };
        return Enumerable.Range(0, 4).Select(i => Wall(document, level, corners[i], corners[(i + 1) % 4])).ToList();
    }

    private static Door Door(BimDocument document, Wall wall, double along)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static GroundRegion Drive(BimDocument document, params (double X, double Y)[] corners)
    {
        var drive = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Driveway };
        drive.SetBoundary(corners.Select(corner => new Point2D(corner.X, corner.Y)));
        document.Add(drive);
        return drive;
    }

    /// <summary>A house 12 m by 9 m, a room filling it, its front door in the south wall 3 m along; its walls - south, east, north, west.</summary>
    private static (BimDocument Document, List<Wall> Walls, Room Room) House()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var walls = Box(document, level, 0, 0, 12000, 9000);
        Door(document, walls[0], 3000);
        var room = new Room { LevelId = level, Location = new Point2D(6000, 4500), Name = "Living Room" };
        document.Add(room);
        return (document, walls, room);
    }

    private static List<AccessProblem> Said(BimDocument document, string clause) => FireAccess.Problems(document).Where(problem => problem.Said.Contains(clause)).ToList();

    [Fact]
    public void ADeadEndLongerThan20MetresHasSomewhereToTurn()
    {
        // A drive 4 m wide from the road - the site's property line along y = -50 m - straight up to the front of the house, about 49.8 m:
        // from its corner by the road, 100 mm in, to its far corner by the house, 3.8 m across and 49.6 m up. Nowhere a 16.8 m circle fits.
        var (document, _, _) = House();
        var site = new PropertyLine { LevelId = document.Levels[0].Id };
        site.SetBoundary(new[] { new Point2D(-20000, -50000), new Point2D(30000, -50000), new Point2D(30000, 20000), new Point2D(-20000, 20000) });
        document.Add(site);
        var drive = Drive(document, (4000, -50000), (8000, -50000), (8000, -165), (4000, -165));
        var dead = Assert.Single(Said(document, "(13.4, Diagram 13.1)"));
        Assert.Equal(drive, dead.Element);
        Assert.StartsWith("it runs ", dead.Said);
        Assert.InRange(double.Parse(dead.Said[8..dead.Said.IndexOf(" m from", StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture), 49.5, 50.0);

        // A head 18 m square at the house end - a 16.8 m circle fits in it, its far corners well within 20 m of it.
        document.Remove(drive);
        var head = Drive(document, (4000, -50000), (8000, -50000), (8000, -18165), (15000, -18165), (15000, -165), (-3000, -165), (-3000, -18165), (4000, -18165));
        Assert.NotEmpty(FireAccess.TurningPlaces(head.Boundary));
        Assert.Empty(Said(document, "(13.4, Diagram 13.1)"));

        // A circle needs 16.8 m between kerbs: a head 16 m square has no room for one.
        Assert.Empty(FireAccess.TurningPlaces(new[] { new Point2D(0, 0), new Point2D(16000, 0), new Point2D(16000, 16000), new Point2D(0, 16000) }));
        Assert.NotEmpty(FireAccess.TurningPlaces(new[] { new Point2D(0, 0), new Point2D(16800, 0), new Point2D(16800, 16800), new Point2D(0, 16800) }));
    }

    [Fact]
    public void AGatewayIs31MetresWideAndNothingHangsLowerThan37Metres()
    {
        // A drive 5 m wide through the garden wall 20 m out, the wall's ends 2.8 m apart across it: too narrow a gateway.
        var (document, _, _) = House();
        var level = document.Levels[0].Id;
        Drive(document, (3500, -30000), (8500, -30000), (8500, -165), (3500, -165));
        var west = Wall(document, level, new Point2D(-5000, -20000), new Point2D(4500, -20000), "Interior - Partition 125mm");
        var east = Wall(document, level, new Point2D(7300, -20000), new Point2D(15000, -20000), "Interior - Partition 125mm");
        var gate = Assert.Single(Said(document, "(Table 13.1) asks gateways"));
        Assert.Equal(west, gate.Element);
        Assert.Equal("the drive passes through a gateway 2.8 m wide in it - Approved Document B (Table 13.1) asks gateways 3.1 m wide on a pumping appliance's route", gate.Said);

        // 3.2 m apart: wide enough.
        east.Start = new Point2D(7700, -20000);
        Assert.Empty(Said(document, "gateway"));

        // A floor over the drive 2.7 m up - a room over a carport - its underside its thickness lower.
        var type = document.TypesOf<FloorType>().First();
        var floor = new Floor { TypeId = type.Id, LevelId = level, HeightOffset = 2700 };
        floor.SetBoundary(new[] { new Point2D(3000, -6000), new Point2D(9000, -6000), new Point2D(9000, -165), new Point2D(3000, -165) });
        document.Add(floor);
        var under = 2700 - document.FindType<SlabType>(type.Id)!.Thickness;
        Assert.Equal($"it passes under a floor {Units.Metres(under, "0.0")} above it - Approved Document B (Table 13.1) asks 3.7 m clear over a pumping appliance's route",
            Assert.Single(Said(document, "3.7 m clear")).Said);
        floor.HeightOffset = 4500;
        Assert.Empty(Said(document, "3.7 m clear"));
    }

    [Fact]
    public void EveryElevationTheDriveReachesHasADoorIn()
    {
        // The drive down the east side of the house: the east elevation has no door in - the front door is round the corner.
        var (document, walls, _) = House();
        Drive(document, (14000, -2000), (18000, -2000), (18000, 9000), (14000, 9000));
        var east = Assert.Single(Said(document, "(13.1, 13.2a) asks one in every elevation"));
        Assert.Equal(walls[1], east.Element);

        // A garden wall along the drive's far side is no elevation of the building.
        Wall(document, document.Levels[0].Id, new Point2D(19000, -2000), new Point2D(19000, 9000));
        Assert.Single(Said(document, "(13.1, 13.2a) asks one in every elevation"));

        // A door in the east wall: met.
        Door(document, walls[1], 4500);
        Assert.Empty(Said(document, "(13.1, 13.2a) asks one in every elevation"));
    }

    [Fact]
    public void ABlockOfFlatsHasItsDoorsNoMoreThan60MetresApart()
    {
        // A block 130 m long, one flat behind it, the drive all along its front; doors in at 10 m and 120 m - 110 m between them.
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var walls = Box(document, level, 0, 0, 130000, 10000);
        Door(document, walls[0], 10000);
        Door(document, walls[0], 120000);
        document.Add(new Room { LevelId = level, Location = new Point2D(65000, 5000), Name = "Living Room", Dwelling = "Flat 1" });
        Drive(document, (0, -8000), (130000, -8000), (130000, -4000), (0, -4000));
        Assert.Equal("along this elevation its doors in are 110.0 m apart, or from its end - Approved Document B (13.2a) asks no more than 60 m between doors or to the end of the elevation",
            Assert.Single(Said(document, "(13.2a) asks no more than 60 m")).Said);

        // A third door at 65 m: 55 m apart at most.
        Door(document, walls[0], 65000);
        Assert.Empty(Said(document, "(13.2a) asks no more than 60 m"));
    }

    /// <summary>
    /// A block 50 m by 10 m: on the ground floor a common entrance hall filling it, its front door 2 m along the south wall and a stair up at
    /// its west end; upstairs a common landing over the west 10 m, the stair's head in it, and Flat 1's living room east of it through a door,
    /// 40 m long. The drive 4 m deep across the front of the west 10 m, 4 m out.
    /// </summary>
    private static (BimDocument Document, Room Landing, Room Living, Level First) Block()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var down = Box(document, ground.Id, 0, 0, 50000, 10000);
        Door(document, down[0], 2000);
        Box(document, first.Id, 0, 0, 50000, 10000);
        Door(document, Wall(document, first.Id, new Point2D(10000, 0), new Point2D(10000, 10000), "Interior - Partition 125mm"), 5000);
        document.Add(new Room { LevelId = ground.Id, Location = new Point2D(25000, 5000), Name = "Entrance Hall" });
        var landing = new Room { LevelId = first.Id, Location = new Point2D(3000, 5000), Name = "Landing" };
        var living = new Room { LevelId = first.Id, Location = new Point2D(30000, 5000), Name = "Living Room", Dwelling = "Flat 1" };
        document.Add(landing);
        document.Add(living);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = ground.Id, TopLevelId = first.Id, Start = new Point2D(5000, 2000), Angle = 90, Width = 900 });
        Drive(document, (0, -8165), (10000, -8165), (10000, -4165), (0, -4165));
        return (document, landing, living, first);
    }

    private static Component Fitting(BimDocument document, string type, Guid level, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = level, Location = new Point2D(x, y), Elevation = kind.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void ADryFireMainServesTheFlatsInsteadOfThePumpingAppliance()
    {
        // From the drive, in at the front door, up the stair and through the landing to the far end of the flat: well past 45 m.
        var (document, landing, living, first) = Block();
        Assert.Contains("(13.2a) asks access for a pumping appliance within 45 m of all points inside each flat, or fire mains", Assert.Single(FireAccess.Problems(document), problem => problem.Element == living).Said);

        // A fire main: its outlet on the landing - 2 m to the flat's door, 40 m across it, about 42.5 m - and its inlet on the front face,
        // 3.9 m from the drive and in sight of it.
        var ground = document.Levels[0].Id;
        var outlet = Fitting(document, "Fire Main Outlet - Landing Valve", first.Id, 8000, 5000);
        var inlet = Fitting(document, "Fire Main Inlet - Dry, 2 Way", ground, 1000, -300);
        Assert.Empty(FireAccess.Problems(document));

        // The outlet in the flat, not the stair: wanting (14.4).
        outlet.Location = new Point2D(12000, 5000);
        Assert.Equal("it is in the Living Room, not a stair's enclosure - Approved Document B (14.4) asks fire main outlets in the protected stairway", Assert.Single(FireAccess.Problems(document), problem => problem.Element == outlet).Said);
        outlet.Location = new Point2D(8000, 5000);

        // The inlet round on the east end, 41 m from the drive (13.5a); on the back, a wall between it and the drive (13.5b); inside: not on the face.
        // Round the east end: from the drive's corner (10, -4.165) to (50.3, 5) is 40.3 m across and 9.165 m up - 41.3 m; and out of sight of it.
        inlet.Location = new Point2D(50300, 5000);
        Assert.Contains(FireAccess.Problems(document), problem => problem.Element == inlet && problem.Said.StartsWith("it is 41.3 m from the drive - Approved Document B (13.5a)", StringComparison.Ordinal));
        inlet.Location = new Point2D(1000, 10300);
        Assert.Equal("a wall stands between it and the drive - Approved Document B (13.5b) asks it visible from where the pumping appliance parks", Assert.Single(FireAccess.Problems(document), problem => problem.Element == inlet).Said);
        inlet.Location = new Point2D(1000, 1000);
        Assert.Equal("it is not on the face of the building - Approved Document B (13.5a) asks fire main inlets there", Assert.Single(FireAccess.Problems(document), problem => problem.Element == inlet).Said);

        // Sent out to IFC as fire suppression terminals: the inlet a breeching inlet, the landing valve a hydrant's outlet.
        using (var model = Infrastructure.Interoperability.IfcExport.Build(document))
            Assert.Equal(new[] { Xbim.Ifc4.Interfaces.IfcFireSuppressionTerminalTypeEnum.BREECHINGINLET, Xbim.Ifc4.Interfaces.IfcFireSuppressionTerminalTypeEnum.FIREHYDRANT },
                model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcFireSuppressionTerminal>().Select(terminal => terminal.PredefinedType!.Value).Order());

        // No inlet at all: the outlet unfed.
        document.Remove(inlet);
        Assert.StartsWith("a fire main's outlet with no inlet to feed it", Assert.Single(FireAccess.Problems(document), problem => problem.Element == outlet).Said);
        _ = landing;
    }
}
