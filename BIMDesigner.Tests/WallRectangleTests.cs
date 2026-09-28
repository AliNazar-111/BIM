using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Four walls drawn by eye round a room, squared into an exact rectangle or square; and a
/// wall's length and height typed into its properties.
/// </summary>
public class WallRectangleTests
{
    /// <summary>A room drawn by hand: nearly 21 m by 19 m, no two sides quite equal, one corner off square.</summary>
    private static (BimDocument Document, List<Wall> Walls) HandDrawn(bool clockwise = false)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        var corners = new List<Point2D>
        {
            new(0, 0), new(20700, 0), new(20820, 19320), new(-30, 19400)
        };
        if (clockwise) corners.Reverse();

        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall
            {
                Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id,
                LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id
            };

            document.Add(wall);
            walls.Add(wall);
        }

        return (document, walls);
    }

    private static double Thickness(BimDocument document, Wall wall) => document.GetWallType(wall)!.Structure.TotalWidth;

    /// <summary>Every corner square, every wall along the page, and the walls still meeting end to end.</summary>
    private static void AssertRectangle(IReadOnlyList<Wall> walls)
    {
        foreach (var wall in walls)
        {
            var run = wall.End - wall.Start;
            Assert.True(Math.Abs(run.X) < 1e-6 || Math.Abs(run.Y) < 1e-6, $"{wall.Start} to {wall.End} is not along the page.");
            Assert.Equal(2, walls.Count(other => !ReferenceEquals(other, wall) &&
                (other.Start.DistanceTo(wall.End) < 1e-6 || other.End.DistanceTo(wall.End) < 1e-6 ||
                 other.Start.DistanceTo(wall.Start) < 1e-6 || other.End.DistanceTo(wall.Start) < 1e-6)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourWallsDrawnByEyeBecomeASquare(bool clockwise)
    {
        var (document, walls) = HandDrawn(clockwise);
        var rectangle = WallRectangle.Find(document, walls, out var problem);
        Assert.Null(problem);

        // What they come to now, centre to centre.
        var (width, depth) = rectangle!.Measure(RectangleMeasure.WallCentres);
        Assert.InRange(width, 20600, 20900);
        Assert.InRange(depth, 19300, 19450);

        rectangle.Make(20000, 20000, RectangleMeasure.WallCentres, out problem)!.Redo();
        Assert.Null(problem);

        AssertRectangle(walls);
        Assert.All(walls, wall => Assert.Equal(20000, wall.Length, precision: 6));
    }

    [Fact]
    public void ARoomCanBeSizedBetweenItsInsideFaces()
    {
        var (document, walls) = HandDrawn();
        var rectangle = WallRectangle.Find(document, walls, out _)!;

        rectangle.Make(4000, 3000, RectangleMeasure.InsideFaces, out _)!.Redo();
        AssertRectangle(walls);

        // The room inside is 4 m by 3 m; the walls' lines are half a wall further out each side.
        var t = Thickness(document, walls[0]);
        var lengths = walls.Select(wall => wall.Length).OrderBy(length => length).ToList();
        Assert.Equal(3000 + t, lengths[0], precision: 6);
        Assert.Equal(4000 + t, lengths[3], precision: 6);

        Assert.Equal(4000, WallRectangle.Find(document, walls, out _)!.Measure(RectangleMeasure.InsideFaces).Width, precision: 6);
        Assert.Equal(4000 + 2 * t, WallRectangle.Find(document, walls, out _)!.Measure(RectangleMeasure.OutsideFaces).Width, precision: 6);
    }

    [Fact]
    public void ItStaysWhereTheRoomWasAndIsOneStepToUndo()
    {
        var (document, walls) = HandDrawn();
        var before = walls.Select(wall => (wall.Start, wall.End)).ToList();
        var middle = new Point2D(walls.Average(wall => wall.Start.X), walls.Average(wall => wall.Start.Y));

        var command = WallRectangle.Find(document, walls, out _)!.Make(20000, 19000, RectangleMeasure.WallCentres, out _)!;
        command.Redo();

        var now = new Point2D(walls.Average(wall => wall.Start.X), walls.Average(wall => wall.Start.Y));
        Assert.True(now.DistanceTo(middle) < 100, $"The room moved from {middle} to {now}.");

        command.Undo();
        Assert.Equal(before, walls.Select(wall => (wall.Start, wall.End)).ToList());
    }

    [Fact]
    public void AWallEndingAtACornerGoesWithIt()
    {
        var (document, walls) = HandDrawn();
        var partition = new Wall
        {
            Start = new Point2D(20700, 0), End = new Point2D(26000, 0),
            TypeId = walls[0].TypeId, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id
        };
        document.Add(partition);

        WallRectangle.Find(document, walls, out _)!.Make(20000, 20000, RectangleMeasure.WallCentres, out _)!.Redo();

        Assert.Contains(walls, wall => wall.Start.DistanceTo(partition.Start) < 1e-6 || wall.End.DistanceTo(partition.Start) < 1e-6);
        Assert.Equal(new Point2D(26000, 0), partition.End);
    }

    [Fact]
    public void WallsThatAreNotARoomAreRefusedWithTheReason()
    {
        var (document, walls) = HandDrawn();

        WallRectangle.Find(document, walls.Take(3).ToList(), out var problem);
        Assert.Contains("four walls", problem);

        walls[2].End = new Point2D(-2000, 19400);
        WallRectangle.Find(document, walls, out problem);
        Assert.Contains("end to end", problem);
    }

    /// <summary>Walls round a room of this many sides, drawn by eye: a regular shape with every corner nudged.</summary>
    private static (BimDocument Document, List<Wall> Walls) RoughPolygon(int sides, double radius, double turnDegrees = 0)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        var corners = Enumerable.Range(0, sides)
            .Select(i => 2 * Math.PI * i / sides + turnDegrees * Math.PI / 180 + 0.05 * Math.Sin(i * 1.7))
            .Select((angle, i) => new Point2D((radius + 150 * Math.Cos(i * 2.3)) * Math.Cos(angle), (radius + 150 * Math.Cos(i * 2.3)) * Math.Sin(angle)))
            .ToList();

        var walls = new List<Wall>();
        for (var i = 0; i < sides; i++)
        {
            var wall = new Wall
            {
                Start = corners[i], End = corners[(i + 1) % sides], TypeId = type.Id,
                LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id
            };

            document.Add(wall);
            walls.Add(wall);
        }

        return (document, walls);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    [InlineData(8, 22.5)]
    public void WallsRoundARoomBecomeARegularPolygon(int sides, double turnDegrees)
    {
        var (document, walls) = RoughPolygon(sides, 5000, turnDegrees);
        var polygon = WallPolygon.Find(document, walls, out var problem);
        Assert.Null(problem);

        polygon!.Make(3000, RectangleMeasure.WallCentres, out problem)!.Redo();
        Assert.Null(problem);

        // Every side the same length, every corner the same angle, and still joined all round.
        Assert.All(walls, wall => Assert.Equal(3000, wall.Length, precision: 6));

        var corners = WallPolygon.Find(document, walls, out _)!.Corners;
        var interior = 180.0 * (sides - 2) / sides;
        for (var i = 0; i < sides; i++)
        {
            var back = corners[(i + sides - 1) % sides] - corners[i];
            var on = corners[(i + 1) % sides] - corners[i];
            var angle = Math.Acos(back.Dot(on) / (back.Length * on.Length)) * 180 / Math.PI;
            Assert.Equal(interior, angle, precision: 6);
        }

        // Drawn nearly with a side along the page, it is put exactly along it.
        Assert.Contains(walls, wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1e-6 || Math.Abs(wall.Start.X - wall.End.X) < 1e-6);
    }

    [Fact]
    public void ARegularPolygonCanBeSizedInsideTheRoomAndUndone()
    {
        var (document, walls) = RoughPolygon(6, 5000);
        var before = walls.Select(wall => (wall.Start, wall.End)).ToList();

        var command = WallPolygon.Find(document, walls, out _)!.Make(4000, RectangleMeasure.InsideFaces, out _)!;
        command.Redo();

        Assert.Equal(4000, WallPolygon.Find(document, walls, out _)!.Measure(RectangleMeasure.InsideFaces), precision: 6);
        Assert.All(walls, wall => Assert.True(wall.Length > 4000, "The walls' lines are further out than the room's faces."));

        command.Undo();
        Assert.Equal(before, walls.Select(wall => (wall.Start, wall.End)).ToList());
    }

    [Fact]
    public void TwoWallsAreNotARoom()
    {
        var (document, walls) = RoughPolygon(6, 5000);
        WallPolygon.Find(document, walls.Take(2).ToList(), out var problem);
        Assert.Contains("three or more", problem);
    }

    [Fact]
    public void TypingALengthMovesTheEndAndTheWallMeetingIt()
    {
        var (document, walls) = HandDrawn();
        var south = walls[0];
        var east = walls[1];
        var eastEnd = east.End;

        var length = south.GetInstanceParameters(document).Single(p => p.Name == "Length");
        Assert.False(length.IsReadOnly);

        var old = length.Value;
        Assert.True(length.TrySetFromText("21000"));
        Assert.Equal(new Point2D(21000, 0), south.End);

        // The corner held: the wall meeting the south wall's end starts where it now ends.
        Assert.Equal(south.End, east.Start);
        Assert.Equal(eastEnd, east.End);

        var undo = new ParameterChangeCommand(length, old, length.Value);
        undo.Undo();
        Assert.Equal(new Point2D(20700, 0), south.End);
        Assert.Equal(new Point2D(20700, 0), east.Start);
    }

    [Fact]
    public void TypingAHeightMovesTheTop()
    {
        var (document, walls) = HandDrawn();
        var wall = walls[0];
        var height = wall.GetInstanceParameters(document).Single(p => p.Name == "Height");

        Assert.False(height.IsReadOnly);
        Assert.True(height.TrySetFromText("3500"));
        Assert.Equal(3500, wall.GetHeight(document), precision: 6);
        Assert.Equal(500, wall.TopOffset, precision: 6);

        // With no top level, it is the unconnected height that changes.
        wall.TopLevelId = null;
        Assert.True(wall.GetInstanceParameters(document).Single(p => p.Name == "Height").TrySetFromText("2700"));
        Assert.Equal(2700, wall.UnconnectedHeight, precision: 6);
        Assert.Equal(2700, wall.GetHeight(document), precision: 6);
    }
}
