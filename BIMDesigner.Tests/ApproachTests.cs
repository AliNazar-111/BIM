using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// The approach to a home's door as Approved Document M asks it to be: a path wide enough, not
/// falling too much across, with places to pass on a wheelchair user's - and at the door a level
/// landing, covered.
/// </summary>
public class ApproachTests
{
    /// <summary>A house of outside walls 8 m by 6 m with a front door in its south wall, on ground falling to the east as given.</summary>
    private static (BimDocument Document, Door Door) House(Func<double, double, double> ground)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(candidate => candidate.Function == WallFunction.Exterior && candidate.Log is null);
        var corners = new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 };
            document.Add(wall);
            walls.Add(wall);
        }

        var door = new Door { TypeId = document.TypesOf<DoorType>().First(candidate => candidate.Function == DoorFunction.Exterior).Id, LevelId = level, HostWallId = walls[0].Id, DistanceAlongWall = 4000, Mark = "D1" };
        document.Add(door);

        var surface = new Toposurface { LevelId = level };
        var points = new List<Point3D>();
        for (var x = -20000.0; x <= 28000; x += 4000)
        for (var y = -24000.0; y <= 24000; y += 4000)
            points.Add(new Point3D(x, y, ground(x, y)));
        surface.SetPoints(points);
        document.Add(surface);
        return (document, door);
    }

    private static GroundRegion Path(BimDocument document, double width, double length = 6000)
    {
        var path = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Path, Approach = true };
        path.SetPath(new[] { new Point2D(4000, -length - 300), new Point2D(4000, -300) }, width);
        document.Add(path);
        return path;
    }

    [Fact]
    public void AnApproachPathIsWideEnoughAndFallsLittleAcross()
    {
        var (document, _) = House((_, _) => 0);
        var path = Path(document, 800);

        Assert.Equal(6000, Approaches.Length(path.PathLine), 6);
        Assert.Contains(GroundRegions.Problems(document, path), problem => problem.Contains("800 wide: Approved Document M asks for 900"));
        path.SetPath(path.PathLine.ToList(), 1000);
        Assert.Empty(GroundRegions.Problems(document, path));

        // Kept with it when saved.
        var saved = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<GroundRegion>().Single();
        Assert.Equal(1000, saved.PathWidth);
        Assert.Equal(path.PathLine, saved.PathLine);

        // On ground falling 1 in 20 to the east, it falls 1:20 across: steeper than 1 in 40.
        var (sloping, _) = House((x, _) => -x / 20);
        var across = Path(sloping, 1000);
        Assert.Equal(1 / 20.0, Approaches.CrossFall(sloping, across)!.Value, 6);
        Assert.Contains(GroundRegions.Problems(sloping, across), problem => problem.Contains("falls 1:20 across"));
    }

    [Fact]
    public void AWheelchairUsersApproachHasAPlaceToPassEveryTenMetres()
    {
        var (document, _) = House((_, _) => 0);
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM3;
        var path = Path(document, 1200, 12000);
        Assert.Contains(GroundRegions.Problems(document, path), problem => problem.Contains("passing place"));

        var bay = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Paving };
        bay.SetBoundary(new[] { new Point2D(4500, -8000), new Point2D(6100, -8000), new Point2D(6100, -6400), new Point2D(4500, -6400) });
        document.Add(bay);
        Assert.DoesNotContain(GroundRegions.Problems(document, path), problem => problem.Contains("passing place"));
    }

    [Fact]
    public void TheEntranceAnApproachComesToHasALevelLandingCovered()
    {
        var (document, door) = House((_, _) => 0);
        var standard = document.ProjectInformation.ClearanceStandard;

        // No approach to it: nothing asked of it.
        Assert.DoesNotContain(Doors.Problems(document, door, standard), problem => problem.Rule.Contains("landing"));

        // An approach to it, and nothing over the landing outside.
        Path(document, 1000);
        Assert.True(Approaches.IsApproachedEntrance(document, door));
        Assert.Contains(Doors.Problems(document, door, standard), problem => problem.Said == "nothing covers the landing outside it.");

        // A canopy over it: covered.
        var canopy = new Roof { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 2500 };
        canopy.SetBoundary(new[] { new Point2D(3000, -1200), new Point2D(5000, -1200), new Point2D(5000, 0), new Point2D(3000, 0) });
        document.Add(canopy);
        Assert.DoesNotContain(Doors.Problems(document, door, standard), problem => problem.Rule.Contains("landing"));

        // Ground falling 1 in 30 away from the door is no level landing.
        var (sloping, entrance) = House((_, y) => y / 30);
        Path(sloping, 1000);
        Assert.Contains(Doors.Problems(sloping, entrance, standard), problem => problem.Said.Contains("falls 1:30"));
    }
}
