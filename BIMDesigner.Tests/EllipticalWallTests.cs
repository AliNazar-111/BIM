using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>Elliptical walls: the curve itself, drawing them, and every view that follows them.</summary>
public class EllipticalWallTests
{
    private const double A = 5000;
    private const double B = 2000;

    /// <summary>The top half of an ellipse 10 m by 4 m about the origin, run from left to right.</summary>
    private static WallCurve TopHalf() =>
        WallCurve.Of(new Point2D(-A, 0), new Point2D(A, 0), 0, WallEllipse.Half(B / A, toLeft: true));

    /// <summary>Ramanujan's second approximation to an ellipse's perimeter - far closer than any test here needs.</summary>
    private static double Perimeter(double a, double b)
    {
        var h = Math.Pow((a - b) / (a + b), 2);
        return Math.PI * (a + b) * (1 + 3 * h / (10 + Math.Sqrt(4 - 3 * h)));
    }

    private static (BimDocument Document, WallType Type, IReadOnlyList<Wall> Walls) WholeEllipse()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var walls = WallShapes.Walls(
            WallShapes.Ellipse(new Point2D(-A, -B), new Point2D(A, B)),
            type.Id, document.Levels[0].Id, WallLocationLine.WallCentreline, flipped: false);

        foreach (var wall in walls) document.Add(wall);
        return (document, type, walls);
    }

    [Fact]
    public void HalfAnEllipseIsHalfItsPerimeterLongAndPassesOverTheTop()
    {
        var curve = TopHalf();

        Assert.True(curve.IsElliptical);
        Assert.True(curve.IsCurved);
        Assert.False(curve.IsArc);
        Assert.Equal(Perimeter(A, B) / 2, curve.Length, precision: 3);

        var top = curve.PointAt(curve.Length / 2);
        Assert.Equal(0, top.X, precision: 3);
        Assert.Equal(B, top.Y, precision: 3);

        // Straight up off the left end of the axis, straight down onto the right.
        Assert.Equal(1, curve.TangentAt(0).Y, precision: 9);
        Assert.Equal(-1, curve.TangentAt(curve.Length).Y, precision: 9);

        // The tightest bend is at the ends of the long axis.
        Assert.Equal(B * B / A, curve.MinRadius, precision: 3);
    }

    [Fact]
    public void LocatingAPointGivesBackWhereItWasPut()
    {
        var curve = TopHalf();

        for (var along = 0.0; along <= curve.Length; along += curve.Length / 37)
        foreach (var left in new[] { -400.0, 0, 250 })
        {
            var (foundAlong, foundLeft) = curve.Locate(curve.At(along, left));
            Assert.Equal(along, foundAlong, precision: 2);
            Assert.Equal(left, foundLeft, precision: 3);
        }

        // Past an end, measured along the end's tangent as for a line.
        var (before, beside) = curve.Locate(new Point2D(-A + 100, -500));
        Assert.Equal(-500, before, precision: 6);
        Assert.Equal(-100, beside, precision: 6);
    }

    [Fact]
    public void AnEllipseCrossesALineWhereItShould()
    {
        var curve = TopHalf();
        var line = WallCurve.Of(new Point2D(0, -10000), new Point2D(0, 10000), 0);

        var crossing = Assert.Single(curve.Crossings(line));
        Assert.Equal(0, crossing.X, precision: 6);
        Assert.Equal(B, crossing.Y, precision: 6);

        // Asked the other way round, the same point.
        Assert.Equal(crossing, Assert.Single(line.Crossings(curve)));

        // A line through the whole ellipse, offset 300 inside it, nearest a point below.
        var hit = curve.Intersect(new Line2D(new Point2D(0, 0), Vector2D.UnitY), -300, new Point2D(0, -5000))!.Value;
        Assert.Equal(-(B - 300), hit.Y, precision: 6);
    }

    [Fact]
    public void ThreeClicksMakeHalfAnEllipseThroughTheThird()
    {
        var through = new Point2D(3000, 1600);
        var ellipse = WallShapes.PartialEllipse(new Point2D(-A, 0), new Point2D(A, 0), through)!.Value;

        Assert.Equal(B / A, ellipse.Ratio, precision: 9);
        Assert.True(WallCurve.Of(new Point2D(-A, 0), new Point2D(A, 0), 0, ellipse).DistanceTo(through) < 1e-3);
        Assert.Null(WallShapes.PartialEllipse(new Point2D(-A, 0), new Point2D(A, 0), new Point2D(1000, 0)));

        // Below the line, it bows the other way.
        var below = WallShapes.PartialEllipse(new Point2D(-A, 0), new Point2D(A, 0), new Point2D(0, -1000))!.Value;
        Assert.Equal(-1000, WallCurve.Of(new Point2D(-A, 0), new Point2D(A, 0), 0, below).PointAt(Perimeter(A, 1000) / 4).Y, precision: 3);
    }

    [Fact]
    public void AnOffsetEllipseGrowsOrShrinksAboutItsCentre()
    {
        var inner = TopHalf().Offset(-300);

        Assert.True(inner.IsElliptical);
        Assert.True(inner.Start.DistanceTo(new Point2D(-A + 300, 0)) < 1e-9);
        Assert.True(inner.End.DistanceTo(new Point2D(A - 300, 0)) < 1e-9);
        Assert.Equal(B - 300, inner.PointAt(inner.Length / 2).Y, precision: 3);
    }

    [Fact]
    public void AWholeEllipseIsTwoHalvesFacingOut()
    {
        var (document, type, walls) = WholeEllipse();

        Assert.Equal(2, walls.Count);
        Assert.All(walls, wall => Assert.True(wall.IsElliptical));

        foreach (var wall in walls)
        {
            var middle = wall.LocationCurve.PointAt(wall.Length / 2);
            var outward = wall.ExteriorNormalAt(wall.Length / 2);
            Assert.True(outward.Dot(middle - new Point2D(0, 0)) > 0, $"The wall through {middle} faces in.");
        }

        // They carry straight on into each other at the ends of the axis: both outlines share
        // the corners there, across the whole width.
        var half = type.Width / 2;
        foreach (var joint in new[] { new Point2D(-A, 0), new Point2D(A, 0) })
        foreach (var wall in walls)
        {
            var (start, end) = WallJoins.GetEndCuts(document, wall, type);
            var outline = WallJoins.GetBandOutline(wall, type, half, -half, start, end);

            Assert.Contains(outline, p => p.DistanceTo(new Point2D(joint.X - half, 0)) < 1e-3);
            Assert.Contains(outline, p => p.DistanceTo(new Point2D(joint.X + half, 0)) < 1e-3);
        }
    }

    [Fact]
    public void ARoomInsideAnEllipseIsMeasuredToItsInnerFace()
    {
        var (document, type, _) = WholeEllipse();

        var result = RoomBoundary.Trace(document, document.Levels[0].Id, new Point2D(0, 0));
        Assert.True(result.IsEnclosed);

        // Steiner: the area inside a curve moved in by d is the area, less the perimeter times d,
        // plus pi d squared.
        var d = type.Width / 2;
        var expected = Math.PI * A * B - Perimeter(A, B) * d + Math.PI * d * d;
        Assert.Equal(expected, Polygon2D.Area(result.Polygon), expected * 0.005);
    }

    [Fact]
    public void SectionsAnd3DFollowTheEllipse()
    {
        var (document, type, walls) = WholeEllipse();

        var marker = new SectionMarker
        {
            Name = "A", Start = new Point2D(0, -4000), End = new Point2D(0, 4000), LevelId = document.Levels[0].Id
        };
        document.Add(marker);

        var cuts = SectionProjection.Build(document, marker).Pieces
            .Where(p => p.Depth == SectionDepth.Cut && walls.Any(w => w.Id == p.ElementId))
            .Select(p => p.Bounds)
            .ToList();

        // Cut through the bottom at 2000 along the line and the top at 6000, the full width of
        // the wall each time, and nothing in the room between.
        var half = type.Width / 2;
        var bottom = cuts.Where(c => c.Right <= 4000).ToList();
        var topCut = cuts.Where(c => c.Left >= 4000).ToList();
        Assert.Equal(cuts.Count, bottom.Count + topCut.Count);
        Assert.Equal(4000 - B - half, bottom.Min(c => c.Left), 1.0);
        Assert.Equal(4000 - B + half, bottom.Max(c => c.Right), 1.0);
        Assert.Equal(4000 + B - half, topCut.Min(c => c.Left), 1.0);
        Assert.Equal(4000 + B + half, topCut.Max(c => c.Right), 1.0);

        var top = walls.Single(w => w.LocationCurve.PointAt(w.Length / 2).Y > 0);
        var reach = ModelMeshBuilder.BuildWall(document, top).SelectMany(m => m.Positions).Max(p => p.Y);
        Assert.Equal(B + type.Width / 2, reach, 1.0);
    }

    [Fact]
    public void SplittingMirroringAndSavingKeepTheShape()
    {
        var (document, _, walls) = WholeEllipse();
        var wall = walls.Single(w => w.LocationCurve.PointAt(w.Length / 2).Y > 0);
        var length = wall.Length;
        var original = wall.LocationCurve.Points();

        var split = new SplitWallCommand(document, wall, new Point2D(1000, 2500));
        split.Redo();

        Assert.True(wall.IsElliptical);
        Assert.True(split.Remainder.IsElliptical);
        Assert.Equal(length, wall.Length + split.Remainder.Length, precision: 3);
        foreach (var piece in new[] { wall, split.Remainder })
            Assert.True(TopHalfCurve().DistanceTo(piece.LocationCurve.PointAt(piece.Length / 3)) < 1e-3);

        split.Undo();
        Assert.Equal(length, wall.Length, precision: 9);
        Assert.Equal(original, wall.LocationCurve.Points());

        ElementTransforms.Mirror(wall, new Line2D(new Point2D(0, 0), Vector2D.UnitX));
        Assert.Equal(-B, wall.LocationCurve.PointAt(wall.Length / 2).Y, precision: 3);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path).Walls.Single(w => w.Id == wall.Id);
            Assert.Equal(wall.Ellipse, reloaded.Ellipse);
            Assert.Equal(-B, reloaded.LocationCurve.PointAt(reloaded.Length / 2).Y, precision: 3);
        }
        finally
        {
            File.Delete(path);
        }

        static WallCurve TopHalfCurve() =>
            WallCurve.Of(new Point2D(A, 0), new Point2D(-A, 0), 0, WallEllipse.Half(B / A, toLeft: false));
    }

    [Fact]
    public void MovingTheLocationLineKeepsTheWallWhereItIs()
    {
        var (document, type, walls) = WholeEllipse();
        var wall = walls[0];
        var structure = type.Structure;
        var faceBefore = wall.PointAt(structure, wall.Length / 2, type.Width / 2);

        var change = new ChangeLocationLineCommand(document, wall, WallLocationLine.FinishFaceExterior);
        change.Redo();

        Assert.True(wall.IsElliptical);
        Assert.Equal(faceBefore.X, wall.PointAt(structure, wall.Length / 2, type.Width / 2).X, precision: 3);
        Assert.Equal(faceBefore.Y, wall.PointAt(structure, wall.Length / 2, type.Width / 2).Y, precision: 3);

        change.Undo();
        Assert.Equal(WallLocationLine.WallCentreline, wall.LocationLine);
        Assert.Equal(new Point2D(A, 0), wall.Start);
    }

    [Fact]
    public void EllipticalWallsExportToIfc()
    {
        var (document, _, walls) = WholeEllipse();
        document.Add(new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id, LevelId = walls[0].LevelId,
            HostWallId = walls[0].Id, DistanceAlongWall = walls[0].Length / 2
        });

        using var model = IfcExport.Build(document);

        Assert.Equal(2, model.Instances.OfType<IfcWall>().Count());
        Assert.Single(model.Instances.OfType<IfcDoor>());
    }

    [Fact]
    public void RandomEllipsesAlwaysDrawAsWalls()
    {
        var random = new Random(5);

        for (var attempt = 0; attempt < 150; attempt++)
        {
            var document = BimDocument.CreateDefault();
            var types = document.TypesOf<WallType>().ToList();
            var width = 3000 + random.NextDouble() * 9000;
            var height = 3000 + random.NextDouble() * 9000;
            var corner = new Point2D(random.Next(-5000, 5000), random.Next(-5000, 5000));

            var walls = WallShapes.Walls(
                WallShapes.Ellipse(corner, new Point2D(corner.X + width, corner.Y + height)),
                types[random.Next(types.Count)].Id, document.Levels[0].Id,
                (WallLocationLine)random.Next(6), flipped: random.Next(2) == 0);
            foreach (var wall in walls) document.Add(wall);

            foreach (var wall in walls)
            {
                var type = document.GetWallType(wall)!;
                var half = type.Width / 2;

                foreach (var slice in WallSlices.Solid(document, wall, type))
                {
                    var outline = WallJoins.GetBandOutline(wall, type, half, -half, slice.CutFrom, slice.CutTo);
                    Assert.False(OutlineChecks.IsSelfIntersecting(outline), $"Ellipse {width:0} x {height:0}: a wall folds over itself.");
                    Assert.True(Polygon2D.Area(outline) > 0.5);
                }
            }
        }
    }
}
