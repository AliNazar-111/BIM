using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Covers room boundary tracing. A room's area is what a client is sold and what an
/// engineer heats, so these are about the number being right, not just a shape appearing.
/// </summary>
public class RoomBoundaryTests
{
    private static (BimDocument Document, WallType Type, Guid LevelId) Project(string typeName = "Generic - 200mm")
    {
        var document = BimDocument.CreateDefault();
        return (document,
            document.TypesOf<WallType>().Single(t => t.Name == typeName),
            document.Levels.First().Id);
    }

    private static Wall AddWall(
        BimDocument document, WallType type, Guid levelId, Point2D start, Point2D end)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = levelId };
        document.Add(wall);
        return wall;
    }

    /// <summary>Four walls round a rectangle, drawn anticlockwise.</summary>
    private static void AddRectangle(
        BimDocument document, WallType type, Guid levelId, double width, double height)
    {
        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(width, 0),
            new Point2D(width, height), new Point2D(0, height)
        };

        for (var i = 0; i < corners.Length; i++)
            AddWall(document, type, levelId, corners[i], corners[(i + 1) % corners.Length]);
    }

    [Fact]
    public void ASimpleRoomIsEnclosed()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        var boundary = RoomBoundary.Trace(document, levelId, new Point2D(3000, 2000));

        Assert.True(boundary.IsEnclosed);
        Assert.Equal(4, boundary.Polygon.Count);
        Assert.Equal(4, boundary.BoundingWalls.Count);
    }

    [Fact]
    public void AreaIsMeasuredToTheWallFacesNotTheCentrelines()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        var boundary = RoomBoundary.Trace(document, levelId, new Point2D(3000, 2000));

        // Walls are 200 mm, so the room is inset 100 mm on every side: 5800 x 3800.
        // Measuring to centrelines would count half of every wall as floor area.
        Assert.Equal(5800d * 3800, boundary.Area, precision: 3);
        Assert.Equal(2 * (5800d + 3800), boundary.Perimeter, precision: 3);
    }

    [Fact]
    public void AreaFollowsTheWallThicknessUsed()
    {
        var (document, _, levelId) = Project();
        var thick = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        AddRectangle(document, thick, levelId, 6000, 4000);

        var boundary = RoomBoundary.Trace(document, levelId, new Point2D(3000, 2000));

        // 330 mm walls inset 165 mm a side: 5670 x 3670.
        Assert.Equal(5670d * 3670, boundary.Area, precision: 3);
    }

    [Fact]
    public void MovingAWallChangesTheArea()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        var before = RoomBoundary.Trace(document, levelId, new Point2D(2000, 2000)).Area;

        // Pull the right-hand wall in by a metre, and the two walls that meet it.
        foreach (var wall in document.Walls.ToList())
        {
            if (Math.Abs(wall.Start.X - 6000) < 1) wall.Start = new Point2D(5000, wall.Start.Y);
            if (Math.Abs(wall.End.X - 6000) < 1) wall.End = new Point2D(5000, wall.End.Y);
        }

        var after = RoomBoundary.Trace(document, levelId, new Point2D(2000, 2000)).Area;

        Assert.Equal(4800d * 3800, after, precision: 3);
        Assert.True(after < before, "shrinking the room must shrink its area");
    }

    [Fact]
    public void APointOutsideTheWallsIsNotEnclosed()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        var boundary = RoomBoundary.Trace(document, levelId, new Point2D(9000, 2000));

        Assert.False(boundary.IsEnclosed);
        Assert.Equal(0, boundary.Area);
    }

    [Fact]
    public void AGapInTheWallsLeavesTheRoomUnenclosed()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        // Take one wall away and the space leaks out into the rest of the world.
        document.Remove(document.Walls.First());

        var boundary = RoomBoundary.Trace(document, levelId, new Point2D(3000, 2000));

        Assert.False(boundary.IsEnclosed);
    }

    [Fact]
    public void AWallMarkedNotRoomBoundingIsIgnored()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        // A partition across the middle, which does not bound rooms.
        var partition = AddWall(document, type, levelId, new Point2D(3000, 0), new Point2D(3000, 4000));
        partition.RoomBounding = false;

        var boundary = RoomBoundary.Trace(document, levelId, new Point2D(1500, 2000));

        // Ignoring it, the room is still the whole rectangle (section 2.5, room bounding).
        Assert.True(boundary.IsEnclosed);
        Assert.Equal(5800d * 3800, boundary.Area, precision: 3);
    }

    [Fact]
    public void ADividingWallMakesTwoRooms()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);
        AddWall(document, type, levelId, new Point2D(3000, 0), new Point2D(3000, 4000));

        var left = RoomBoundary.Trace(document, levelId, new Point2D(1500, 2000));
        var right = RoomBoundary.Trace(document, levelId, new Point2D(4500, 2000));

        Assert.True(left.IsEnclosed);
        Assert.True(right.IsEnclosed);

        // Each half spans 3000 mm centreline to centreline, less 100 mm at the outer wall
        // and 100 mm at the divider: 2800 x 3800.
        Assert.Equal(2800d * 3800, left.Area, precision: 3);
        Assert.Equal(2800d * 3800, right.Area, precision: 3);
    }

    [Fact]
    public void AWallMeetingAnotherPartWayAlongStillDividesTheSpace()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        // A T-junction: the divider lands mid-span of the walls above and below it, so the
        // tracer has to cut those walls at the crossing to see the junction at all.
        AddWall(document, type, levelId, new Point2D(2500, 0), new Point2D(2500, 4000));

        var left = RoomBoundary.Trace(document, levelId, new Point2D(1000, 2000));

        Assert.True(left.IsEnclosed);

        // x runs 100 to 2400, y runs 100 to 3900.
        Assert.Equal(2300d * 3800, left.Area, precision: 3);
    }

    [Fact]
    public void AnLShapedRoomIsTracedRoundItsCorner()
    {
        var (document, type, levelId) = Project();

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 2000),
            new Point2D(3000, 2000), new Point2D(3000, 4000), new Point2D(0, 4000)
        };

        for (var i = 0; i < corners.Length; i++)
            AddWall(document, type, levelId, corners[i], corners[(i + 1) % corners.Length]);

        var boundary = RoomBoundary.Trace(document, levelId, new Point2D(1000, 1000));

        Assert.True(boundary.IsEnclosed);
        Assert.Equal(6, boundary.Polygon.Count);

        // Inset 100 mm all round, the L becomes (100,100)-(5900,1900) plus (100,1900)-(2900,3900).
        Assert.Equal(5800d * 1800 + 2800d * 2000, boundary.Area, precision: 3);
    }

    [Fact]
    public void WallsOnAnotherLevelDoNotBoundThisRoom()
    {
        var (document, type, _) = Project();
        var ground = document.Levels.Single(l => l.Name == "Ground Floor").Id;
        var first = document.Levels.Single(l => l.Name == "First Floor").Id;

        AddRectangle(document, type, first, 6000, 4000);

        Assert.False(RoomBoundary.Trace(document, ground, new Point2D(3000, 2000)).IsEnclosed);
        Assert.True(RoomBoundary.Trace(document, first, new Point2D(3000, 2000)).IsEnclosed);
    }

    /// <summary>
    /// Two rooms in one space would both report their area, so every schedule would
    /// double-count that floor. Placing into an occupied space must find the occupant.
    /// </summary>
    [Fact]
    public void ASpaceThatAlreadyHasARoomIsRecognised()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        var room = new Room { Location = new Point2D(3000, 2000), LevelId = levelId, Number = "001" };
        document.Add(room);

        // Anywhere in the same enclosure finds it, not just the point it was placed at.
        Assert.Same(room, RoomBoundary.FindRoomEnclosing(document, levelId, new Point2D(3000, 2000)));
        Assert.Same(room, RoomBoundary.FindRoomEnclosing(document, levelId, new Point2D(500, 3500)));
        Assert.Same(room, RoomBoundary.FindRoomEnclosing(document, levelId, new Point2D(5800, 300)));
    }

    [Fact]
    public void AFreeSpaceHasNoRoom()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        Assert.Null(RoomBoundary.FindRoomEnclosing(document, levelId, new Point2D(3000, 2000)));

        // Outside the walls there is no space at all, let alone a room in it.
        Assert.Null(RoomBoundary.FindRoomEnclosing(document, levelId, new Point2D(9000, 2000)));
    }

    [Fact]
    public void RoomsInDifferentSpacesDoNotShadowEachOther()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);
        AddWall(document, type, levelId, new Point2D(3000, 0), new Point2D(3000, 4000));

        var left = new Room { Location = new Point2D(1500, 2000), LevelId = levelId, Number = "001" };
        document.Add(left);

        Assert.Same(left, RoomBoundary.FindRoomEnclosing(document, levelId, new Point2D(1000, 1000)));

        // The other half of the plan is still free.
        Assert.Null(RoomBoundary.FindRoomEnclosing(document, levelId, new Point2D(4500, 2000)));
    }

    [Fact]
    public void ARoomOnAnotherLevelDoesNotOccupyThisSpace()
    {
        var (document, type, _) = Project();
        var ground = document.Levels.Single(l => l.Name == "Ground Floor").Id;
        var first = document.Levels.Single(l => l.Name == "First Floor").Id;

        AddRectangle(document, type, ground, 6000, 4000);
        AddRectangle(document, type, first, 6000, 4000);

        document.Add(new Room { Location = new Point2D(3000, 2000), LevelId = ground, Number = "001" });

        Assert.Null(RoomBoundary.FindRoomEnclosing(document, first, new Point2D(3000, 2000)));
    }

    [Fact]
    public void VolumeIsAreaTimesTheRoomHeight()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        var room = new Room { Location = new Point2D(3000, 2000), LevelId = levelId, UpperLimitOffset = 2700 };
        document.Add(room);

        var area = room.GetBoundary(document).Area;
        var volume = room.GetInstanceParameters(document).Single(p => p.Name == "Volume").Value;

        Assert.Equal(area * 2700, Assert.IsType<double>(volume), precision: 3);
    }

    [Fact]
    public void AreaReadsInSquareMetresOnThePropertyPanel()
    {
        var (document, type, levelId) = Project();
        AddRectangle(document, type, levelId, 6000, 4000);

        var room = new Room { Location = new Point2D(3000, 2000), LevelId = levelId };
        document.Add(room);

        var area = room.GetInstanceParameters(document).Single(p => p.Name == "Area");

        // 5.8 m x 3.8 m = 22.04 m²
        Assert.Equal("22.04 m²", area.DisplayValue);
        Assert.True(area.IsReadOnly, "a room's area is traced, never typed in");
    }
}
