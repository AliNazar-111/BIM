using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Spline and freeform walls: the curve through the clicked points, and the wall built along it.</summary>
public class SplineWallTests
{
    private static readonly Point2D Start = new(0, 0);
    private static readonly Point2D End = new(8000, 0);
    private static readonly Point2D[] Through = { new(2000, 1500), new(4000, -500), new(6000, 1200) };

    private static WallCurve Wave() => WallCurve.Of(Start, End, 0, null, WallSpline.Between(Start, End, Through));

    private static (BimDocument Document, Wall Wall) Project()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = Start, End = End, Spline = WallSpline.Between(Start, End, Through),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, wall);
    }

    [Fact]
    public void TheCurvePassesThroughEveryClickedPoint()
    {
        var curve = Wave();

        Assert.True(curve.IsSpline);
        Assert.True(curve.IsCurved);
        Assert.Equal(Start, curve.PointAt(0));
        Assert.Equal(End.X, curve.PointAt(curve.Length).X, precision: 6);
        Assert.Equal(End.Y, curve.PointAt(curve.Length).Y, precision: 6);

        foreach (var point in Through)
            Assert.True(curve.DistanceTo(point) < 0.01, $"The curve misses {point} by {curve.DistanceTo(point):0.###} mm.");
    }

    [Fact]
    public void ItBendsSmoothlyThroughEachPoint()
    {
        var curve = Wave();

        foreach (var point in Through)
        {
            var along = curve.Locate(point).Along;
            var before = curve.TangentAt(along - 0.5);
            var after = curve.TangentAt(along + 0.5);
            Assert.True(before.Dot(after) > 0.9999, $"The curve kinks at {point}.");
        }
    }

    [Fact]
    public void ASplineThroughPointsOnACircleIsNearlyThatCircle()
    {
        const double radius = 3000;
        var through = Enumerable.Range(1, 5)
            .Select(i => Math.PI - Math.PI * i / 6)
            .Select(a => new Point2D(radius * Math.Cos(a), radius * Math.Sin(a)))
            .ToList();
        var curve = WallCurve.Of(new Point2D(-radius, 0), new Point2D(radius, 0), 0, null,
            WallSpline.Between(new Point2D(-radius, 0), new Point2D(radius, 0), through));

        Assert.Equal(Math.PI * radius, curve.Length, Math.PI * radius * 0.002);
        for (var along = 0.0; along <= curve.Length; along += curve.Length / 50)
            Assert.Equal(radius, curve.PointAt(along) - new Point2D(0, 0) is var v ? v.Length : 0, radius * 0.003);
    }

    [Fact]
    public void LocatingAPointGivesBackWhereItWasPut()
    {
        var curve = Wave();

        for (var along = 50.0; along < curve.Length - 50; along += curve.Length / 29)
        foreach (var left in new[] { -150.0, 0, 150 })
        {
            var (foundAlong, foundLeft) = curve.Locate(curve.At(along, left));
            Assert.Equal(along, foundAlong, 0.5);
            Assert.Equal(left, foundLeft, 0.5);
        }
    }

    [Fact]
    public void MovingOrTurningTheWallCarriesTheCurveWithIt()
    {
        var spline = WallSpline.Between(Start, End, Through)!;
        var original = WallCurve.Of(Start, End, 0, null, spline);

        // Turned a quarter anticlockwise about the origin and moved: the same spline, held
        // relative to the ends, follows without being touched.
        Point2D Turn(Point2D p) => new(-p.Y + 1000, p.X + 500);
        var turned = WallCurve.Of(Turn(Start), Turn(End), 0, null, spline);

        Assert.Equal(original.Length, turned.Length, precision: 3);
        foreach (var point in Through)
            Assert.True(turned.DistanceTo(Turn(point)) < 0.01);
    }

    [Fact]
    public void MirroringAWallMirrorsItsCurve()
    {
        var (document, wall) = Project();
        ElementTransforms.Mirror(wall, Line2D.Through(new Point2D(0, 0), new Point2D(1, 0)));

        foreach (var point in Through)
            Assert.True(wall.LocationCurve.DistanceTo(new Point2D(point.X, -point.Y)) < 0.01);
        Assert.NotNull(document);
    }

    [Fact]
    public void SplittingASplineWallLeavesTheSameCurveInTwoParts()
    {
        var (document, wall) = Project();
        var curve = wall.LocationCurve;
        var samples = Enumerable.Range(0, 41).Select(i => curve.PointAt(curve.Length * i / 40)).ToList();

        var split = new SplitWallCommand(document, wall, curve.PointAt(curve.Length * 0.37));
        split.Redo();

        var halves = new[] { wall, split.Remainder };
        Assert.All(halves, half => Assert.True(half.IsSpline));
        Assert.Equal(curve.Length, halves.Sum(h => h.LocationCurve.Length), precision: 1);
        foreach (var sample in samples)
            Assert.True(halves.Min(h => h.LocationCurve.DistanceTo(sample)) < 0.05, $"Nothing of the split wall passes {sample}.");

        split.Undo();
        Assert.Equal(curve.Length, wall.LocationCurve.Length, precision: 6);
    }

    [Fact]
    public void AnOffsetStaysItsDistanceFromTheCurve()
    {
        var curve = Wave();
        var offset = curve.Offset(165);

        Assert.True(offset.IsSpline);
        for (var along = 0.0; along <= offset.Length; along += offset.Length / 60)
        {
            var point = offset.PointAt(along);
            var found = curve.Locate(point);
            Assert.True(Math.Abs(found.Left - 165) < 1, $"at {along:0} of {offset.Length:0}: point {point}, located {found.Along:0.#} along, {found.Left:0.#} left; distance {curve.DistanceTo(point):0.#}");
        }
    }

    [Fact]
    public void AWallAlongASplineIsBuiltIn3DAndCutInPlan()
    {
        var (document, wall) = Project();
        var type = document.GetWallType(wall)!;

        var body = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.Kind == MeshKind.Wall).ToList();
        Assert.NotEmpty(body);

        // Every point of the solid is within the wall's width of its curve.
        var points = body.SelectMany(m => m.Positions).ToList();
        Assert.All(points, p => Assert.True(wall.LocationCurve.DistanceTo(new Point2D(p.X, p.Y)) <= type.Width / 2 + 1));
        Assert.Equal(3000, points.Max(p => p.Z), precision: 6);

        var outline = WallJoins.GetBandOutline(document, wall, type, type.Width / 2, -type.Width / 2);
        Assert.True(outline.Length > 20, "A spline wall's outline should follow its curve, not cut straight across.");
    }

    [Fact]
    public void TwoSplineWallsJoinAtACorner()
    {
        var (document, first) = Project();
        var corner = End;
        var second = new Wall
        {
            Start = corner, End = new Point2D(8000, 6000),
            Spline = WallSpline.Between(corner, new Point2D(8000, 6000), new[] { new Point2D(9500, 3000) }),
            TypeId = first.TypeId, LevelId = first.LevelId, UnconnectedHeight = 3000
        };
        document.Add(second);

        var type = document.GetWallType(first)!;
        var (_, endCut) = WallJoins.GetEndCuts(document, first, type);
        var (startCut, _) = WallJoins.GetEndCuts(document, second, type);

        Assert.True(endCut.IsJoined);
        Assert.True(startCut.IsJoined);
    }

    [Fact]
    public void ASplineWallIsSavedAndLoaded()
    {
        var (document, wall) = Project();
        var split = new SplitWallCommand(document, wall, wall.LocationCurve.PointAt(3000));
        split.Redo();

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);

            foreach (var original in new[] { wall, split.Remainder })
            {
                var copy = reloaded.Walls.Single(w => w.Id == original.Id);
                Assert.Equal(original.Spline, copy.Spline);
                Assert.Equal(original.LocationCurve.Length, copy.LocationCurve.Length, precision: 6);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnInvalidSplineFallsBackToAStraightWall()
    {
        Assert.False(new WallSpline(Array.Empty<Point2D>()).IsValid);
        Assert.False(new WallSpline(new[] { new Point2D(0.5, double.NaN) }).IsValid);

        var curve = WallCurve.Of(Start, End, 0, null, new WallSpline(Array.Empty<Point2D>()));
        Assert.False(curve.IsCurved);
        Assert.Equal(8000, curve.Length, precision: 9);
    }

    [Fact]
    public void ClickedPointsMakeOneOpenWallOrAClosedLoopOfTwo()
    {
        var clicks = new[] { new Point2D(0, 0), new Point2D(3000, 2000), new Point2D(6000, 0), new Point2D(3000, -2500) };

        var open = WallShapes.Spline(clicks, closed: false);
        var wall = Assert.Single(open);
        Assert.Equal(clicks[0], wall.Start);
        Assert.Equal(clicks[^1], wall.End);
        Assert.True(wall.Curve.IsSpline);

        var loop = WallShapes.Spline(clicks, closed: true);
        Assert.Equal(2, loop.Count);
        Assert.Equal(loop[0].End, loop[1].Start);
        Assert.Equal(loop[1].End, loop[0].Start);

        // Every click is on the loop, and it runs smoothly through the two joins.
        foreach (var click in clicks)
            Assert.True(loop.Min(p => p.Curve.DistanceTo(click)) < 0.01, $"The loop misses {click}.");
        foreach (var (arriving, leaving) in new[] { (loop[0], loop[1]), (loop[1], loop[0]) })
            Assert.True(arriving.Curve.TangentAt(arriving.Curve.Length).Dot(leaving.Curve.TangentAt(0)) > 0.9999);

        // Clockwise, so the walls face out, whichever way it was clicked.
        var outline = loop.SelectMany(p => p.Curve.Points().SkipLast(1)).ToList();
        Assert.True(Polygon2D.SignedArea(outline) < 0);
        var reversed = WallShapes.Spline(clicks.Reverse().ToArray(), closed: true);
        Assert.True(Polygon2D.SignedArea(reversed.SelectMany(p => p.Curve.Points().SkipLast(1)).ToList()) < 0);
    }

    [Fact]
    public void AFreehandStrokeKeepsOnlyThePointsItsShapeNeeds()
    {
        // A wobbly quarter circle of radius 4 m: hundreds of samples, a few millimetres of shake.
        var random = new Random(7);
        var stroke = Enumerable.Range(0, 400)
            .Select(i => Math.PI / 2 * i / 399)
            .Select(a => new Point2D(4000 * Math.Cos(a) + random.NextDouble() * 6 - 3, 4000 * Math.Sin(a) + random.NextDouble() * 6 - 3))
            .ToList();

        var kept = WallShapes.Simplify(stroke, 20);
        Assert.InRange(kept.Count, 4, 30);
        Assert.Equal(stroke[0], kept[0]);
        Assert.Equal(stroke[^1], kept[^1]);

        var wall = Assert.Single(WallShapes.Spline(kept, closed: false));
        foreach (var point in stroke)
            Assert.True(wall.Curve.DistanceTo(point) < 25, $"The wall strays {wall.Curve.DistanceTo(point):0} mm from the stroke at {point}.");
    }

    [Fact]
    public void MovingASplinePointIsUndoable()
    {
        var (document, wall) = Project();
        var before = wall.Spline;
        var moved = wall.Spline!.WithPointAt(1, new Point2D(4000, -1500), wall.Start, wall.End);

        var command = new ReshapeSplineWallCommand(wall, before, moved);
        command.Redo();
        Assert.True(wall.LocationCurve.DistanceTo(new Point2D(4000, -1500)) < 0.01);
        Assert.Contains(wall.LocationCurve.SplinePoints(), p => p.Index == 1 && p.Point.DistanceTo(new Point2D(4000, -1500)) < 0.01);

        command.Undo();
        Assert.Equal(before, wall.Spline);
        Assert.NotNull(document);
    }
}
