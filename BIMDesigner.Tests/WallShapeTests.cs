using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>Rectangles, polygons, circles and ovals of walls, placed with two clicks.</summary>
public class WallShapeTests
{
    private static (BimDocument Document, WallType Exterior) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document, document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    private static IReadOnlyList<Wall> Build(BimDocument document, WallType type, IReadOnlyList<WallPiece> pieces)
    {
        var walls = WallShapes.Walls(pieces, type.Id, document.Levels[0].Id, WallLocationLine.WallCentreline, flipped: false);
        new AddElementsCommand(document, walls, "Draw Shape").Redo();
        return walls;
    }

    /// <summary>The shapes a user can draw, each about the origin.</summary>
    public static TheoryData<string> Shapes => new() { "rectangle", "square", "triangle", "hexagon", "octagon", "circle", "oval", "tall oval" };

    private static IReadOnlyList<WallPiece> Shape(string name) => name switch
    {
        "rectangle" => WallShapes.Rectangle(new Point2D(-4000, -2500), new Point2D(4000, 2500)),
        "square" => WallShapes.Rectangle(new Point2D(0, 0), WallShapes.SquareCorner(new Point2D(0, 0), new Point2D(5000, -3000))),
        "triangle" => WallShapes.Polygon(new Point2D(0, 0), new Point2D(0, 4000), 3, inscribed: true),
        "hexagon" => WallShapes.Polygon(new Point2D(0, 0), new Point2D(4000, 0), 6, inscribed: false),
        "octagon" => WallShapes.Polygon(new Point2D(0, 0), new Point2D(3000, 1000), 8, inscribed: true),
        "circle" => WallShapes.Circle(new Point2D(0, 0), new Point2D(3500, 0)),
        "oval" => WallShapes.Oval(new Point2D(-5000, -2000), new Point2D(5000, 2000)),
        "tall oval" => WallShapes.Oval(new Point2D(-1500, -4000), new Point2D(1500, 4000)),
        _ => throw new ArgumentException(name)
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryShapeIsAClosedLoopOfJoinedWallsFacingOut(string name)
    {
        var (document, exterior) = Project();
        var walls = Build(document, exterior, Shape(name));

        Assert.NotEmpty(walls);
        var centre = new Point2D(walls.Average(w => w.Start.X), walls.Average(w => w.Start.Y));

        foreach (var wall in walls)
        {
            // Each wall ends where the next begins.
            Assert.Contains(walls, other => other.Start.DistanceTo(wall.End) < 1e-6);

            // Outward: the exterior side of the middle of each wall points away from the shape.
            var middle = wall.LocationCurve.PointAt(wall.Length / 2);
            var outward = wall.ExteriorNormalAt(wall.Length / 2);
            Assert.True((middle + outward * 100).DistanceTo(centre) > middle.DistanceTo(centre), $"{name}: a wall faces in.");

            // Every end is joined: no free ends, and no corner left to overlap.
            var (start, end) = WallJoins.GetEndCuts(document, wall, exterior);
            Assert.True(start.IsJoined && end.IsJoined, $"{name}: an end is not joined.");
            Assert.NotEqual(WallEndCondition.Overlap, start.Condition);
            Assert.NotEqual(WallEndCondition.Overlap, end.Condition);
        }

        foreach (var wall in walls)
        {
            var (most, _) = OutlineChecks.Coverage(document, walls, wall.Start, exterior.Width);
            Assert.True(most <= 1, $"{name}: walls overlap at {wall.Start}.");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ARoomFitsInsideEveryShape(string name)
    {
        var (document, exterior) = Project();
        var walls = Build(document, exterior, Shape(name));

        var centre = name == "square" ? new Point2D(2500, -2500) : new Point2D(0, 0);
        var result = RoomBoundary.Trace(document, document.Levels[0].Id, centre);

        Assert.True(result.IsEnclosed, $"{name}: the room is not enclosed.");
        Assert.All(walls, wall => Assert.Contains(wall, result.BoundingWalls));
    }

    [Fact]
    public void ARectangleIsTheBoxBetweenItsCorners()
    {
        var pieces = WallShapes.Rectangle(new Point2D(4000, 3000), new Point2D(0, 0));

        Assert.Equal(4, pieces.Count);
        Assert.Equal(2 * (4000 + 3000), pieces.Sum(p => p.Start.DistanceTo(p.End)), precision: 6);
    }

    [Fact]
    public void ASquareTakesTheLongerSideAndTheCursorsDirection()
    {
        var corner = WallShapes.SquareCorner(new Point2D(0, 0), new Point2D(-3000, 1200));
        Assert.Equal(new Point2D(-3000, 3000), corner);
    }

    [Fact]
    public void APolygonIsSizedByACornerOrByTheMiddleOfASide()
    {
        var inscribed = WallShapes.Polygon(new Point2D(0, 0), new Point2D(3000, 0), 6, inscribed: true);
        Assert.Contains(inscribed, p => p.Start.DistanceTo(new Point2D(3000, 0)) < 1e-6);

        // Circumscribed: the click is the middle of a side, 3000 from the centre.
        var circumscribed = WallShapes.Polygon(new Point2D(0, 0), new Point2D(3000, 0), 6, inscribed: false);
        Assert.Contains(circumscribed, p => p.Start.MidpointTo(p.End).DistanceTo(new Point2D(3000, 0)) < 1e-6);

        Assert.Equal(3, WallShapes.Polygon(new Point2D(0, 0), new Point2D(1000, 0), 1, true).Count);
    }

    [Fact]
    public void ACircleIsTwoHalvesOfTheRightSize()
    {
        var pieces = WallShapes.Circle(new Point2D(1000, 1000), new Point2D(4000, 1000));
        var curves = pieces.Select(p => WallCurve.Of(p.Start, p.End, p.Bulge)).ToList();

        Assert.Equal(2, curves.Count);
        Assert.Equal(2 * Math.PI * 3000, curves.Sum(c => c.Length), precision: 6);
        Assert.All(curves, c => Assert.True(c.Centre.DistanceTo(new Point2D(1000, 1000)) < 1e-6));
    }

    [Fact]
    public void AnOvalHasStraightSidesAndRoundEnds()
    {
        var pieces = WallShapes.Oval(new Point2D(0, 0), new Point2D(10000, 4000));

        Assert.Equal(2, pieces.Count(p => p.Bulge == 0));
        Assert.Equal(2, pieces.Count(p => p.Bulge != 0));

        // A square box makes a circle: the straight sides vanish.
        var round = WallShapes.Oval(new Point2D(0, 0), new Point2D(4000, 4000));
        Assert.All(round, p => Assert.NotEqual(0, p.Bulge));
    }

    [Fact]
    public void AnOffsetGrowsEveryShapeBySameAmount()
    {
        var rectangle = WallShapes.Offset(WallShapes.Rectangle(new Point2D(0, 0), new Point2D(4000, 3000)), 200);
        Assert.Contains(rectangle, p => p.Start.DistanceTo(new Point2D(-200, 3200)) < 1e-6);
        Assert.Equal(2 * (4400 + 3400), rectangle.Sum(p => p.Start.DistanceTo(p.End)), precision: 6);

        var circle = WallShapes.Offset(WallShapes.Circle(new Point2D(0, 0), new Point2D(3000, 0)), 200);
        Assert.All(circle, p => Assert.Equal(3200, WallCurve.Of(p.Start, p.End, p.Bulge).Radius, precision: 6));

        var inward = WallShapes.Offset(WallShapes.Oval(new Point2D(0, 0), new Point2D(10000, 4000)), -300);
        Assert.All(inward.Where(p => p.Bulge != 0), p =>
            Assert.Equal(1700, WallCurve.Of(p.Start, p.End, p.Bulge).Radius, precision: 6));
    }

    [Fact]
    public void AShapeTooSmallToBuildBuildsNothing()
    {
        Assert.Empty(WallShapes.Rectangle(new Point2D(0, 0), new Point2D(0.5, 3000)));
        Assert.Empty(WallShapes.Circle(new Point2D(0, 0), new Point2D(0.2, 0)));
    }
}
