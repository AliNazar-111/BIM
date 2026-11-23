using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Drives drawn as several regions (round 122): those that overlap or meet are one road for the fire service's dead ends and turning
/// (Approved Document B 13.4, Diagram 13.1; Volume 2 15.10) - a turning head drawn as a region of its own is where the appliance turns.
/// </summary>
public class DriveRoadTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    /// <summary>A house 12 m by 9 m, a room filling it, its front door in the south wall; the site's property line along y = -50 m.</summary>
    private static BimDocument House()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 9000), new Point2D(0, 9000) };
        var walls = Enumerable.Range(0, 4).Select(i =>
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = document.TypesOf<WallType>().First(each => each.Name == Brick).Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            return wall;
        }).ToList();
        document.Add(new Door { HostWallId = walls[0].Id, TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Id, LevelId = level, DistanceAlongWall = 3000 });
        document.Add(new Room { LevelId = level, Location = new Point2D(6000, 4500), Name = "Living Room" });
        var site = new PropertyLine { LevelId = level };
        site.SetBoundary(new[] { new Point2D(-20000, -50000), new Point2D(30000, -50000), new Point2D(30000, 20000), new Point2D(-20000, 20000) });
        document.Add(site);
        return document;
    }

    private static GroundRegion Drive(BimDocument document, double x0, double y0, double x1, double y1)
    {
        var drive = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Driveway };
        drive.SetBoundary(new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) });
        document.Add(drive);
        return drive;
    }

    private static List<AccessProblem> DeadEnds(BimDocument document) => FireAccess.Problems(document).Where(problem => problem.Said.Contains("(13.4, Diagram 13.1)")).ToList();

    [Fact]
    public void ATurningHeadDrawnAsARegionOfItsOwnIsWhereTheApplianceTurns()
    {
        // A strip 4 m wide 46 m up from the road, and a square 18 m by 18 m drawn over its last 4 m - room for a 16.8 m circle.
        var document = House();
        var strip = Drive(document, 4000, -50000, 8000, -4000);
        Assert.Single(DeadEnds(document));
        var head = Drive(document, -3000, -26000, 15000, -8000);

        var road = Assert.Single(FireAccess.Roads(new[] { strip, head }));
        Assert.Equal(2, road.Members.Count);
        // The union's area: the strip's 4 by 46 and the square's 18 by 18, less the 4 by 18 they share - 184 + 324 - 72 = 436 m².
        Assert.Equal(436, Polygon2D.Area(road.Outline) / 1e6, 3);
        Assert.Empty(DeadEnds(document));
    }

    [Fact]
    public void RegionsMeetingEdgeToEdgeAreOneRoadAndRegionsApartAreTwo()
    {
        // The strip from the road to y = -20 m; a square from there to y = -2 m, sharing its edge: one road, a turning place in the square.
        var document = House();
        var strip = Drive(document, 4000, -50000, 8000, -20000);
        var head = Drive(document, -3000, -20000, 15000, -2000);
        var road = Assert.Single(FireAccess.Roads(new[] { strip, head }));
        Assert.Equal(30 * 4 + 18 * 18, Polygon2D.Area(road.Outline) / 1e6, 3);
        Assert.Empty(DeadEnds(document));

        // A strip 5 m off to the east is a road of its own.
        var apart = Drive(document, 13000, -50000, 17000, -30000);
        Assert.Equal(2, FireAccess.Roads(new[] { strip, head, apart }).Count);
    }

    [Fact]
    public void AnLShapedRoadIsOneDeadEndSaidWhereItEnds()
    {
        // A strip 4 m wide 30 m up from the road, and a spur 4 m wide west from its top, 24 m long overlapping it: one road with nowhere to
        // turn, walked from 100 mm in by the road up 25.9 m to the inside corner and 20.3 m on to the spur's far corner, 46.2 m - said once,
        // on the spur, where its far end is; before, each region was a dead end of its own.
        var document = House();
        var strip = Drive(document, 4000, -50000, 8000, -20000);
        var spur = Drive(document, -16000, -24000, 8000, -20000);
        var dead = Assert.Single(DeadEnds(document));
        Assert.Equal(spur, dead.Element);
        Assert.StartsWith("it runs 46.", dead.Said, StringComparison.Ordinal);
        Assert.Contains("a dead end with nowhere a pumping appliance can turn", dead.Said);
    }
}
