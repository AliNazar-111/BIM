using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>Split with Gap: a wall split in two with a movement joint between, neither end joining across it.</summary>
public class SplitWithGapTests
{
    private static (BimDocument Document, Wall Wall) OneWall(double bulge = 0)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), Bulge = bulge, TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return (document, wall);
    }

    private static Door DoorAt(BimDocument document, Wall wall, double along)
    {
        var door = new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    [Fact]
    public void TheWallIsSplitWithTheGapBetweenAndNeitherEndJoinsAcrossIt()
    {
        var (document, wall) = OneWall();
        var near = DoorAt(document, wall, 1000);
        var far = DoorAt(document, wall, 5000);

        var (command, beyond) = WallGaps.Split(document, wall, 3000, 25, out var problem)!.Value;
        Assert.Null(problem);

        Assert.Equal(2, document.Walls.Count());
        Assert.Equal(new Point2D(2987.5, 0), wall.End);
        Assert.Equal(new Point2D(3012.5, 0), beyond.Start);
        Assert.Equal(new Point2D(6000, 0), beyond.End);
        Assert.Equal(WallJoinKind.Disallow, wall.EndJoin);
        Assert.Equal(WallJoinKind.Disallow, beyond.StartJoin);

        // Each door is in the half it was in, where it was.
        Assert.Equal(wall.Id, near.HostWallId);
        Assert.Equal(1000, near.DistanceAlongWall, 6);
        Assert.Equal(beyond.Id, far.HostWallId);
        Assert.Equal(5000 - 3012.5, far.DistanceAlongWall, 6);

        command.Undo();
        Assert.Single(document.Walls);
        Assert.Equal(new Point2D(6000, 0), wall.End);
        Assert.Equal(WallJoinKind.Auto, wall.EndJoin);
        Assert.Equal(wall.Id, far.HostWallId);
        Assert.Equal(5000, far.DistanceAlongWall, 6);

        command.Redo();
        Assert.Equal(2, document.Walls.Count());
        Assert.Equal(new Point2D(2987.5, 0), wall.End);
    }

    [Fact]
    public void ADoorInTheWayOrAnEndTooNearRefusesIt()
    {
        var (document, wall) = OneWall();
        DoorAt(document, wall, 3000);

        Assert.Null(WallGaps.Split(document, wall, 3000, 25, out var problem));
        Assert.Contains("in the way", problem);
        Assert.Null(WallGaps.Split(document, wall, 5, 25, out problem));
        Assert.Contains("end", problem);
        Assert.Null(WallGaps.Split(document, wall, 2000, 500, out problem));
        Assert.Single(document.Walls);
    }

    [Fact]
    public void ACurvedWallKeepsItsCurveEitherSideOfTheGap()
    {
        var (document, wall) = OneWall(bulge: 0.4);
        var length = wall.Length;
        var curve = wall.LocationCurve;
        var (from, to) = (curve.PointAt(length / 2 - 50), curve.PointAt(length / 2 + 50));

        var (_, beyond) = WallGaps.Split(document, wall, length / 2, 100, out _)!.Value;

        Assert.Equal(length - 100, wall.Length + beyond.Length, 3);
        Assert.True(wall.End.DistanceTo(from) < 1e-6);
        Assert.True(beyond.Start.DistanceTo(to) < 1e-6);
        Assert.True(wall.IsCurved && beyond.IsCurved);
    }

    [Fact]
    public void TheJointsTwoEndsAreLeftOpenInPlan()
    {
        var (document, wall) = OneWall();
        var (_, beyond) = WallGaps.Split(document, wall, 3000, 25, out _)!.Value;
        var type = document.GetWallType(wall)!;

        var (_, end) = WallJoins.GetEndCuts(document, wall, type);
        var (start, _) = WallJoins.GetEndCuts(document, beyond, type);
        Assert.False(end.IsJoined);
        Assert.False(start.IsJoined);
    }
}
