using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Vehicle access for the fire and rescue service (round 86), as Approved Document B Volume 1 (Section 13) has it: a pumping appliance on the
/// drive within 45 m of all points inside the house along the route of the hose - in at a door 750 mm wide at least, through the rooms and
/// up the stair - and its access route 3.7 m wide between kerbs.
/// </summary>
[Collection("Wpf")]
public class FireAccessTests
{
    /// <summary>
    /// A house 12 m by 9 m of brick, a room on each storey filling it, the front door in the south wall 3 m along - 900 wide - and a stair at
    /// the back from (10, 1) running north; a drive 4 m deep across the front, so far from the door's outside face.
    /// </summary>
    private static (BimDocument Document, Room Ground, Room Upstairs, Door Front, GroundRegion Drive) House(double driveOff, double driveDepth = 4000)
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        Wall? south = null;
        foreach (var level in new[] { ground, first })
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 9000), new Point2D(0, 9000) };
            for (var i = 0; i < 4; i++)
            {
                var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = level.Id, UnconnectedHeight = first.Elevation - ground.Elevation };
                document.Add(wall);
                if (level == ground && i == 0) south = wall;
            }
        }

        var front = new Door { HostWallId = south!.Id, TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Id, LevelId = ground.Id, DistanceAlongWall = 3000 };
        document.Add(front);
        var downstairs = new Room { LevelId = ground.Id, Location = new Point2D(6000, 4500), Name = "Living Room" };
        var upstairs = new Room { LevelId = first.Id, Location = new Point2D(6000, 4500), Name = "Bedroom" };
        document.Add(downstairs);
        document.Add(upstairs);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = ground.Id, TopLevelId = first.Id, Start = new Point2D(10000, 1000), Angle = 90, Width = 900, Mark = "ST1" });

        // The door's outside face is 165 mm out from the wall's middle: the drive's edge so far beyond that.
        var edge = -165 - driveOff;
        var drive = new GroundRegion { LevelId = ground.Id, Surface = GroundSurface.Driveway };
        drive.SetBoundary(new[] { new Point2D(0, edge - driveDepth), new Point2D(12000, edge - driveDepth), new Point2D(12000, edge), new Point2D(0, edge) });
        document.Add(drive);
        return (document, downstairs, upstairs, front, drive);
    }

    [Fact]
    public void ADriveAtTheFrontReachesEveryPointWithin45Metres()
    {
        var (document, _, _, _, _) = House(3500);
        Assert.Empty(FireAccess.Problems(document));

        // 60 m away: every room past 45 m along the hose.
        var (far, ground, upstairs, _, _) = House(60000);
        var problems = FireAccess.Problems(far);
        Assert.Contains(problems, problem => problem.Element == ground && problem.Said.Contains("(13.1) asks access for a pumping appliance within 45 m of all points inside the house"));
        Assert.Contains(problems, problem => problem.Element == upstairs);
    }

    [Fact]
    public void TheHoseGoesUpTheStair()
    {
        // 28 m from the door: the ground floor's far corner about 28 + 12.3 = 40 m along the hose; the bedroom's, through the door, across to
        // the stair 7 m, up it 4.5 m and across the landing 11 m, about 50 - past 45.
        var (document, ground, upstairs, _, _) = House(28000);
        var problems = FireAccess.Problems(document);
        Assert.DoesNotContain(problems, problem => problem.Element == ground);
        var up = Assert.Single(problems, problem => problem.Element == upstairs);
        var said = up.Said[(up.Said.IndexOf(" is ", StringComparison.Ordinal) + 4)..up.Said.IndexOf(" m along", StringComparison.Ordinal)];
        Assert.InRange(double.Parse(said, System.Globalization.CultureInfo.InvariantCulture), 46, 54);
    }

    [Fact]
    public void TheDriveIs37MetresWideAndTheDoorIn750()
    {
        // A drive 3 m deep across the front - the narrowest way across it: past Table 13.1.
        var (document, _, _, _, drive) = House(3500, 3000);
        Assert.Equal(3000, FireAccess.Narrowest(drive.Boundary), 6);
        Assert.Contains(FireAccess.Problems(document), problem => problem.Element == drive && problem.Said == "it is 3.0 m wide - Approved Document B (Table 13.1) gives a pumping appliance's access route 3.7 m between kerbs");

        // The front door 700 wide: no way in for the hose.
        (document, _, _, var front, drive) = House(3500);
        var narrow = (DoorType)document.FindType<DoorType>(front.TypeId)!.Duplicate("Single - 700");
        narrow.Width = 700;
        document.AddType(narrow);
        front.TypeId = narrow.Id;
        Assert.Contains(FireAccess.Problems(document), problem => problem.Element == drive && problem.Said.StartsWith("no door into the building 750 mm wide"));

        // No drive drawn: nothing asked.
        document.Remove(drive);
        Assert.Empty(FireAccess.Problems(document));
    }
}
