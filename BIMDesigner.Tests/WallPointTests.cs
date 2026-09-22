using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>Points added to a wall by hand and dragged to stretch it into a curve.</summary>
public class WallPointTests
{
    private static (BimDocument Document, Wall Wall) Straight()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, wall);
    }

    [Fact]
    public void AddingAPointChangesNothingUntilItIsDragged()
    {
        var (_, wall) = Straight();

        var spline = WallPoints.AddPoint(wall, new Point2D(2000, 80));
        Assert.NotNull(spline);

        var command = new ReshapeSplineWallCommand(wall, wall.Spline, spline, "Add Wall Point");
        command.Redo();

        Assert.True(wall.IsSpline);
        Assert.Equal(6000, wall.Length, precision: 3);
        var (index, point) = Assert.Single(wall.LocationCurve.SplinePoints());
        Assert.Equal(new Point2D(2000, 0).X, point.X, precision: 6);
        Assert.Equal(0, point.Y, precision: 6);
        for (var along = 0.0; along <= wall.Length; along += 250)
            Assert.Equal(0, wall.LocationCurve.PointAt(along).Y, precision: 6);

        // Dragged out, the wall stretches through it.
        wall.Spline = wall.Spline!.WithPointAt(index, new Point2D(2000, 1500), wall.Start, wall.End);
        Assert.True(wall.LocationCurve.DistanceTo(new Point2D(2000, 1500)) < 0.01);
        Assert.True(wall.Length > 6000);
    }

    [Fact]
    public void PointsGoInOrderAlongTheWall()
    {
        var (_, wall) = Straight();
        wall.Spline = WallPoints.AddPoint(wall, new Point2D(4000, 0));
        wall.Spline = wall.Spline!.WithPointAt(0, new Point2D(4000, -1000), wall.Start, wall.End);
        wall.Spline = WallPoints.AddPoint(wall, wall.LocationCurve.PointAt(1500));

        var points = wall.LocationCurve.SplinePoints().Select(p => p.Point).ToList();
        Assert.Equal(2, points.Count);
        Assert.True(points[0].X < points[1].X, "The new point should come first: it is nearer the start.");
        Assert.True(wall.LocationCurve.DistanceTo(new Point2D(4000, -1000)) < 0.01);
    }

    [Fact]
    public void NoPointGoesOnAnEndOrOnTopOfAnother()
    {
        var (_, wall) = Straight();

        Assert.Null(WallPoints.AddPoint(wall, new Point2D(30, 0)));
        Assert.Null(WallPoints.AddPoint(wall, new Point2D(5990, 0)));

        wall.Spline = WallPoints.AddPoint(wall, new Point2D(3000, 0));
        Assert.Null(WallPoints.AddPoint(wall, new Point2D(3050, 0)));
    }

    [Fact]
    public void AnArcGivenAPointKeepsItsCurve()
    {
        var (_, wall) = Straight();
        wall.Bulge = 0.5;
        var arc = wall.LocationCurve;
        var samples = Enumerable.Range(0, 31).Select(i => arc.PointAt(arc.Length * i / 30)).ToList();

        var command = new ReshapeSplineWallCommand(wall, null, WallPoints.AddPoint(wall, arc.PointAt(arc.Length * 0.3)), "Add Wall Point");
        command.Redo();

        Assert.True(wall.IsSpline);
        Assert.Equal(0, wall.Bulge);
        foreach (var sample in samples)
            Assert.True(wall.LocationCurve.DistanceTo(sample) < 1, $"The wall left its arc by {wall.LocationCurve.DistanceTo(sample):0.##} mm at {sample}.");

        command.Undo();
        Assert.False(wall.IsSpline);
        Assert.Equal(0.5, wall.Bulge);
    }

    [Fact]
    public void RemovingTheLastPointMakesTheWallStraightAgain()
    {
        var (_, wall) = Straight();
        wall.Spline = WallPoints.AddPoint(wall, new Point2D(3000, 0));
        wall.Spline = wall.Spline!.WithPointAt(0, new Point2D(3000, 2000), wall.Start, wall.End);

        var (index, _) = Assert.Single(wall.LocationCurve.SplinePoints());
        var command = new ReshapeSplineWallCommand(wall, wall.Spline, WallPoints.RemovePoint(wall, index), "Remove Wall Point");
        command.Redo();

        Assert.False(wall.IsCurved);
        Assert.Equal(6000, wall.Length, precision: 9);

        command.Undo();
        Assert.True(wall.LocationCurve.DistanceTo(new Point2D(3000, 2000)) < 0.01);
    }

    [Fact]
    public void StraighteningTakesOffPointsAndBends()
    {
        var (_, wall) = Straight();
        wall.Bulge = 0.3;

        var command = new ReshapeSplineWallCommand(wall, wall.Spline, null, "Straighten", straighten: true);
        command.Redo();
        Assert.False(wall.IsCurved);

        command.Undo();
        Assert.Equal(0.3, wall.Bulge);
    }
}
