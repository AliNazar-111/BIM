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
}
