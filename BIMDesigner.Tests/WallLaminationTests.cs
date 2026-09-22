using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Place by segment, place by room, and walls joined and locked face to face.</summary>
public class WallLaminationTests
{
    /// <summary>A closed 6 m by 4 m room of exterior walls, drawn clockwise so their exteriors face out.</summary>
    private static (BimDocument Document, WallType Exterior, WallType Lining, IReadOnlyList<Wall> Walls) Room()
    {
        var document = BimDocument.CreateDefault();
        var exterior = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var lining = document.TypesOf<WallType>().First(t => t.Id != exterior.Id && !t.Name.StartsWith("Curtain"));
        var walls = WallShapes.Walls(WallShapes.Rectangle(new Point2D(0, 0), new Point2D(6000, 4000)),
            exterior.Id, document.Levels[0].Id, WallLocationLine.WallCentreline, flipped: false);
        foreach (var wall in walls)
        {
            wall.UnconnectedHeight = 3000;
            document.Add(wall);
        }

        return (document, exterior, lining, walls);
    }

    private static Wall Bottom(IReadOnlyList<Wall> walls) => walls.OrderBy(w => w.Start.Y + w.End.Y).First();

    [Fact]
    public void PlaceBySegmentLinesTheFaceBetweenTheWallsItMeets()
    {
        var (document, exterior, liningType, walls) = Room();
        var host = Bottom(walls);

        var lining = WallPlacement.BySegment(document, host, exteriorSide: false, liningType.Id, host.LevelId)!;
        Assert.NotNull(lining);
        Assert.Equal(WallLocationLine.FinishFaceExterior, lining.LocationLine);

        // Its exterior face lies on the host's interior face, and its body is inside the room.
        var half = exterior.Width / 2;
        var middle = lining.Length / 2;
        var liningFace = lining.PointAt(liningType.Structure, middle, liningType.Width / 2);
        var (_, across) = host.Locate(exterior.Structure, liningFace);
        Assert.Equal(-half, across, precision: 6);
        Assert.True(host.Locate(exterior.Structure, lining.PointAt(liningType.Structure, middle, 0)).Across < -half);

        // Inside the corners it runs only between the side walls' faces.
        Assert.Equal(6000 - exterior.Width, lining.Length, precision: 3);
    }

    [Fact]
    public void PlaceByRoomLinesEveryFaceAndMeetsAtTheCorners()
    {
        var (document, exterior, liningType, walls) = Room();

        var boundary = RoomBoundary.Trace(document, walls[0].LevelId, new Point2D(3000, 2000));
        Assert.True(boundary.IsEnclosed);

        var linings = WallPlacement.ByRoom(boundary.Polygon, liningType.Id, walls[0].LevelId);
        Assert.Equal(4, linings.Count);

        // Each ends where the next begins.
        for (var i = 0; i < linings.Count; i++)
            Assert.True(linings[i].End.DistanceTo(linings[(i + 1) % linings.Count].Start) < 1e-6);

        // Clockwise, so each one's exterior is toward the wall it lines.
        foreach (var lining in linings)
        {
            document.Add(lining);
            var touching = WallLamination.Touching(document, lining);
            Assert.Single(touching);
            Assert.Contains(touching[0], walls);
        }
    }

    [Fact]
    public void ADoorInAJoinedWallCutsThroughBoth()
    {
        var (document, _, liningType, walls) = Room();
        var host = Bottom(walls);
        var lining = WallPlacement.BySegment(document, host, exteriorSide: false, liningType.Id, host.LevelId)!;
        lining.UnconnectedHeight = 3000;
        lining.JoinedTo.AddRange(WallLamination.Touching(document, lining).Select(w => w.Id));
        document.Add(lining);
        Assert.Contains(host.Id, lining.JoinedTo);

        var doorType = document.TypesOf<DoorType>().First();
        var door = new Door { HostWallId = host.Id, TypeId = doorType.Id, LevelId = host.LevelId, DistanceAlongWall = 3000 };
        document.Add(door);

        var hole = Assert.Single(WallHoles.Of(document, lining));
        Assert.Equal(doorType.Width, hole.To - hole.From, precision: 3);
        Assert.Equal(2, WallOpenings.GetSolidRuns(document, lining).Count);

        // Not joined, the lining would cover the door.
        lining.JoinedTo.Clear();
        Assert.Empty(WallHoles.Of(document, lining));
    }

    [Fact]
    public void LockedWallsMoveTogether()
    {
        var (document, _, liningType, walls) = Room();
        var host = Bottom(walls);
        var lining = WallPlacement.BySegment(document, host, exteriorSide: false, liningType.Id, host.LevelId)!;
        lining.JoinedTo.Add(host.Id);
        document.Add(lining);

        Assert.Single(WallLamination.LockedGroup(document, host));

        lining.LockedToJoined = true;
        Assert.Contains(lining, WallLamination.LockedGroup(document, host));
        Assert.Contains(host, WallLamination.LockedGroup(document, lining));
    }

    [Fact]
    public void SplittingAJoinedWallKeepsBothHalvesJoined()
    {
        var (document, _, liningType, walls) = Room();
        var host = Bottom(walls);
        var lining = WallPlacement.BySegment(document, host, exteriorSide: false, liningType.Id, host.LevelId)!;
        lining.JoinedTo.Add(host.Id);
        document.Add(lining);

        var split = new SplitWallCommand(document, host, host.LocationCurve.PointAt(host.Length / 3));
        split.Redo();
        Assert.Contains(split.Remainder.Id, lining.JoinedTo);

        split.Undo();
        Assert.DoesNotContain(split.Remainder.Id, lining.JoinedTo);
    }

    [Fact]
    public void JoinsAreSaved()
    {
        var (document, _, liningType, walls) = Room();
        var host = Bottom(walls);
        var lining = WallPlacement.BySegment(document, host, exteriorSide: false, liningType.Id, host.LevelId)!;
        lining.JoinedTo.Add(host.Id);
        lining.LockedToJoined = true;
        document.Add(lining);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var copy = ProjectFile.Load(path).Walls.Single(w => w.Id == lining.Id);
            Assert.Equal(new[] { host.Id }, copy.JoinedTo);
            Assert.True(copy.LockedToJoined);
            Assert.Equal(WallLocationLine.FinishFaceExterior, copy.LocationLine);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CurtainWallsAreNotJoined()
    {
        var (document, _, _, walls) = Room();
        var curtain = document.ElementTypes.OfType<CurtainWallType>().First();
        var host = Bottom(walls);
        var glass = new Wall
        {
            Start = host.Start, End = host.End, TypeId = curtain.Id, LevelId = host.LevelId,
            LocationLine = WallLocationLine.FinishFaceExterior, Flipped = host.Flipped
        };

        Assert.False(WallLamination.CanJoin(document, glass));
        Assert.Empty(WallLamination.Touching(document, glass));
    }

    [Fact]
    public void ThePadlockLocksWallsLyingAgainstEachOtherAndUndoes()
    {
        var (document, _, liningType, walls) = Room();
        var host = Bottom(walls);
        var lining = WallPlacement.BySegment(document, host, exteriorSide: false, liningType.Id, host.LevelId)!;
        document.Add(lining);

        // Placed without Auto Join: touching, but neither joined nor locked.
        Assert.Contains(host, WallLamination.Touching(document, lining));
        Assert.False(WallLockPoint.AreLocked(host, lining));

        // The padlock sits on the face they share.
        var padlock = WallLockPoint.Between(document, host, lining)!.Value;
        var hostType = document.GetWallType(host)!;
        Assert.Equal(-hostType.Width / 2, host.Locate(hostType.Structure, padlock).Across, precision: 6);

        // Clicked from the older wall: it holds the new join, and they move as one.
        var command = new SetWallLockCommand(host, lining, locked: true);
        command.Redo();
        Assert.True(WallLockPoint.AreLocked(host, lining));
        Assert.Contains(lining, WallLamination.LockedGroup(document, host));

        command.Undo();
        Assert.False(WallLockPoint.AreLocked(host, lining));
        Assert.Empty(WallLamination.Partners(document, host));

        // Unlocking keeps them joined.
        new SetWallLockCommand(host, lining, locked: true).Redo();
        new SetWallLockCommand(lining, host, locked: false).Redo();
        Assert.False(WallLockPoint.AreLocked(host, lining));
        Assert.Contains(lining, WallLamination.Partners(document, host));
    }
    [Fact]
    public void ALockedCornerKeepsItsWallsMeetingWhenOneMoves()
    {
        var (document, _, _, walls) = Room();
        var corner = walls[0].End;
        var level = walls[0].LevelId;

        Assert.True(WallJointLock.IsCorner(document, level, corner));
        Assert.False(WallJointLock.IsLocked(document, level, corner));
        Assert.Empty(WallJointLock.Followers(document, new[] { walls[0] }));

        var command = new SetJointLockCommand(document, level, corner, locked: true);
        command.Redo();
        Assert.True(WallJointLock.IsLocked(document, level, corner));

        // Moving the first wall: the wall it meets at the locked corner follows by its end there,
        // and nothing at the other, unlocked corner does.
        var followers = WallJointLock.Followers(document, new[] { walls[0] });
        var follower = Assert.Single(followers);
        Assert.Equal(walls[1], follower.Wall);
        Assert.True(follower.AtStart);

        command.Undo();
        Assert.False(WallJointLock.IsLocked(document, level, corner));
    }

    [Fact]
    public void ALockedEndGoesWithTheFarHalfOfASplit()
    {
        var (document, _, _, walls) = Room();
        var wall = walls[0];
        wall.EndLocked = true;

        var split = new SplitWallCommand(document, wall, wall.LocationCurve.PointAt(wall.Length / 2));
        split.Redo();
        Assert.False(wall.EndLocked);
        Assert.True(split.Remainder.EndLocked);

        split.Undo();
        Assert.True(wall.EndLocked);
    }
}
