using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Walls teed into a moved wall follow it: their end on its side slides along their own line to
/// meet it again, so a room divided by a partition stays divided when the outside wall moves.
/// </summary>
public class WallTeeTests
{
    /// <summary>A 9 by 6 m box of exterior walls drawn clockwise, a partition across it at x 4000 from the bottom wall to the top.</summary>
    private static (BimDocument Document, IReadOnlyList<Wall> Walls, Wall Partition) Box()
    {
        var document = BimDocument.CreateDefault();
        var exterior = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var level = document.Levels[0].Id;
        var walls = WallShapes.Walls(WallShapes.Rectangle(new Point2D(0, 0), new Point2D(9000, 6000)),
            exterior.Id, level, WallLocationLine.WallCentreline, flipped: false);
        foreach (var wall in walls)
        {
            wall.UnconnectedHeight = 3000;
            document.Add(wall);
        }

        var interior = document.TypesOf<WallType>().First(t => t.Id != exterior.Id && !t.Name.StartsWith("Curtain"));
        var partition = new Wall { Start = new Point2D(4000, 0), End = new Point2D(4000, 6000), TypeId = interior.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(partition);
        return (document, walls, partition);
    }

    private static Wall Top(IReadOnlyList<Wall> walls) => walls.OrderByDescending(w => w.Start.Y + w.End.Y).First();

    private static Wall Left(IReadOnlyList<Wall> walls) => walls.OrderBy(w => w.Start.X + w.End.X).First();

    private static void Near(Point2D expected, Point2D actual) =>
        Assert.True(expected.DistanceTo(actual) < 0.01, $"Expected {expected}, was {actual}.");

    [Fact]
    public void APartitionTeedIntoAWallStretchesToFollowItAndUndoPutsItBack()
    {
        var (document, walls, partition) = Box();
        var top = Top(walls);

        var follower = Assert.Single(WallJointLock.Following(document, new[] { top }));
        Assert.Same(partition, follower.Wall);
        Assert.False(follower.AtStart);

        var move = new NudgeElementsCommand(new[] { top }, WallJointLock.Following(document, new[] { top }), new Vector2D(300, 900), document);
        move.Redo();

        // Only the share square to the wall moves it; it stays upright and its foot stays put.
        Near(new Point2D(4000, 0), partition.Start);
        Near(new Point2D(4000, 6900), partition.End);

        move.Undo();
        Near(new Point2D(4000, 6000), partition.End);
    }

    [Fact]
    public void ADoorInAStretchedPartitionStaysWhereItStands()
    {
        var (document, walls, partition) = Box();
        var door = new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = partition.LevelId, HostWallId = partition.Id, DistanceAlongWall = 4500 };
        document.Add(door);

        // Drawn from the top down, the partition's start is the end that follows.
        (partition.Start, partition.End) = (partition.End, partition.Start);
        door.DistanceAlongWall = 1500;

        var top = Top(walls);
        var move = new NudgeElementsCommand(new[] { top }, WallJointLock.Following(document, new[] { top }), new Vector2D(0, 600), document);
        move.Redo();
        Near(new Point2D(4000, 6600), partition.Start);
        Assert.Equal(2100, door.DistanceAlongWall, 6);

        move.Undo();
        Assert.Equal(1500, door.DistanceAlongWall, 6);
    }

    [Fact]
    public void AnAngledWallSlidesAlongItsOwnLine()
    {
        var (document, walls, partition) = Box();
        partition.Start = new Point2D(2000, 3000);
        partition.End = new Point2D(0, 5000);
        var left = Left(walls);

        var move = new NudgeElementsCommand(new[] { left }, WallJointLock.Following(document, new[] { left }), new Vector2D(-1000, 0), document);
        move.Redo();

        // At 45 degrees, a metre out is a metre further down its line each way.
        Near(new Point2D(-1000, 6000), partition.End);
        Near(new Point2D(2000, 3000), partition.Start);
    }

    [Fact]
    public void APinnedWallAnEndJoiningNothingAndAWallMovingWithItDoNotFollow()
    {
        var (document, walls, partition) = Box();
        var top = Top(walls);

        partition.Pinned = true;
        Assert.Empty(WallJointLock.Teed(document, new[] { top }));
        partition.Pinned = false;

        partition.EndJoin = WallJoinKind.Disallow;
        Assert.Empty(WallJointLock.Teed(document, new[] { top }));
        partition.EndJoin = WallJoinKind.Auto;

        Assert.Empty(WallJointLock.Teed(document, new[] { top, partition }));
    }

    [Fact]
    public void AWallEndingOnTheFaceFollowsAndOneAlongItDoesNot()
    {
        var (document, walls, partition) = Box();
        var top = Top(walls);
        var half = document.GetWallType(top)!.Width / 2;

        // Stopped against the inside face rather than at the middle of the wall.
        partition.End = new Point2D(4000, 6000 - half);
        Assert.Single(WallJointLock.Teed(document, new[] { top }));

        // A lining run along the wall's face is beside it, not teed into it.
        partition.Start = new Point2D(1000, 6000 - half);
        partition.End = new Point2D(3000, 6000 - half);
        Assert.Empty(WallJointLock.Teed(document, new[] { top }));
    }

    [Fact]
    public void AMoveThatWouldPushAPartitionBackPastItsFootIsStopped()
    {
        var (document, walls, partition) = Box();
        var top = Top(walls);
        var move = new NudgeElementsCommand(new[] { top }, WallJointLock.Following(document, new[] { top }), new Vector2D(0, -6000), document);

        Assert.True(move.Collapses(new Vector2D(0, -6000)));
        Assert.True(move.Collapses(new Vector2D(0, -7000)));
        Assert.False(move.Collapses(new Vector2D(0, -5000)));
    }

    [Fact]
    public void BothRoomsStayEnclosedWhenTheOutsideWallMoves()
    {
        var (document, walls, partition) = Box();
        var left = new Room { LevelId = partition.LevelId, Location = new Point2D(2000, 3000), Name = "Living" };
        var right = new Room { LevelId = partition.LevelId, Location = new Point2D(6500, 3000), Name = "Kitchen" };
        document.Add(left);
        document.Add(right);
        Assert.True(left.GetBoundary(document).IsEnclosed);

        // Its corners locked, as the Wall tool draws a chain, so the side walls stretch too.
        foreach (var wall in walls) (wall.StartLocked, wall.EndLocked) = (true, true);
        var top = Top(walls);
        var following = WallJointLock.Following(document, new[] { top });
        new NudgeElementsCommand(new[] { top }, following, new Vector2D(0, 1200), document).Redo();

        Assert.True(left.GetBoundary(document).IsEnclosed);
        Assert.True(right.GetBoundary(document).IsEnclosed);
        Assert.True(left.GetBoundary(document).Area > 4000 * 6000 * 0.9);
    }

    [Fact]
    public void ADimensionTypedMovesTheWallAndThePartitionTeedIntoIt()
    {
        var (document, walls, partition) = Box();
        var top = Top(walls);
        var bottom = walls.OrderBy(w => w.Start.Y + w.End.Y).First();
        var dimension = new Dimension
        {
            LevelId = top.LevelId,
            Start = DimensionReference.ToWallLine(bottom, DimensionAnchor.WallCentreline, new Point2D(7000, 0)),
            End = DimensionReference.ToWallLine(top, DimensionAnchor.WallCentreline, new Point2D(7000, 6000))
        };
        document.Add(dimension);

        var drive = DimensionLocks.Drive(document, dimension, 7000, out var message);
        Assert.NotNull(drive);
        drive!.Redo();

        Near(new Point2D(4000, 7000), partition.End);
    }
}
