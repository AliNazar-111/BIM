using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>Curved walls crossed part-way along, and straight walls trimmed to curves.</summary>
public class CurvedCrossingTests
{
    private static (BimDocument Document, WallType Partition, WallType Exterior) Project()
    {
        var document = BimDocument.CreateDefault();
        return (
            document,
            document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Interior")),
            document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    private static Wall Add(BimDocument document, WallType type, Point2D start, Point2D end, double bulge = 0)
    {
        var wall = new Wall { Start = start, End = end, Bulge = bulge, TypeId = type.Id, LevelId = document.Levels[0].Id };
        document.Add(wall);
        return wall;
    }

    /// <summary>Whether any point near the crossing is covered by more than one piece of wall.</summary>
    private static void AssertNoOverlapAround(BimDocument document, Point2D centre, double radius)
    {
        var outlines = document.Walls.SelectMany(wall =>
        {
            var type = document.GetWallType(wall)!;
            return WallSlices.Solid(document, wall, type).Select(slice =>
                WallJoins.GetBandOutline(wall, type, type.Width / 2, -type.Width / 2, slice.CutFrom, slice.CutTo));
        }).ToList();

        for (var i = -20; i <= 20; i++)
        for (var j = -20; j <= 20; j++)
        {
            var point = new Point2D(centre.X + radius * i / 20 + 0.013, centre.Y + radius * j / 20 + 0.029);
            // Arcs are drawn as short straight pieces to within half a millimetre, so a point that
            // close to an edge is on it, not inside.
            var inside = outlines.Count(o => Polygon2D.Contains(o, point) && EdgeDistance(o, point) > 1);
            Assert.True(inside <= 1, $"{point} is covered twice.");
        }
    }

    private static double EdgeDistance(IReadOnlyList<Point2D> outline, Point2D point) =>
        outline.Select((corner, i) => Line2D.DistanceFromSegment(point, corner, outline[(i + 1) % outline.Count])).Min();

    [Fact]
    public void AStraightPartitionCrossingACurvedWallStopsAgainstIt()
    {
        var (document, partition, exterior) = Project();

        // A half circle round the bottom, and a partition straight down through its middle.
        var arc = Add(document, exterior, new Point2D(-3000, 0), new Point2D(3000, 0), bulge: 1);
        var stem = Add(document, partition, new Point2D(0, 1000), new Point2D(0, -5000));

        Assert.Single(WallSlices.Solid(document, arc, exterior));
        Assert.Equal(2, WallSlices.Solid(document, stem, partition).Count);

        AssertNoOverlapAround(document, new Point2D(0, -3000), 600);
    }

    [Fact]
    public void ACurvedPartitionCrossingAStraightWallStopsAgainstIt()
    {
        var (document, partition, exterior) = Project();

        Add(document, exterior, new Point2D(-5000, -3000), new Point2D(5000, -3000));
        var arc = Add(document, partition, new Point2D(-3000, 0), new Point2D(3000, 0), bulge: 1.3);

        var pieces = WallSlices.Solid(document, arc, partition);
        Assert.True(pieces.Count >= 2, "The curved partition should be broken where the wall crosses it.");

        foreach (var crossing in arc.LocationCurve.Crossings(WallCurve.Of(new Point2D(-5000, -3000), new Point2D(5000, -3000), 0)))
            AssertNoOverlapAround(document, crossing, 500);
    }

    [Fact]
    public void TwoCurvedWallsCrossingJoinToo()
    {
        var (document, partition, exterior) = Project();

        var first = Add(document, exterior, new Point2D(-3000, 0), new Point2D(3000, 0), bulge: 1);
        var second = Add(document, partition, new Point2D(-3000, -3000), new Point2D(3000, -3000), bulge: -1);

        var crossings = first.LocationCurve.Crossings(second.LocationCurve);
        Assert.NotEmpty(crossings);
        Assert.True(WallSlices.Solid(document, second, partition).Count > 1);

        foreach (var crossing in crossings)
            AssertNoOverlapAround(document, crossing, 400);
    }

    [Fact]
    public void AStraightWallTrimsAndExtendsToACurve()
    {
        var (document, partition, exterior) = Project();
        var arc = Add(document, exterior, new Point2D(-3000, 0), new Point2D(3000, 0), bulge: 1);

        // Short of the curve: extended to it.
        var wall = Add(document, partition, new Point2D(500, 0), new Point2D(500, -2000));
        Assert.True(WallTrim.TryResolve(wall, arc, out var start, out var end));
        Assert.Equal(new Point2D(500, 0), start);
        Assert.Equal(3000, end.DistanceTo(new Point2D(0, 0)), precision: 6);
        Assert.True(end.Y < 0);

        // A curved wall cannot be the one trimmed.
        Assert.False(WallTrim.TryResolve(arc, wall, out _, out _));
    }

    [Fact]
    public void CurvesCrossWhereTheyShould()
    {
        var circle = WallCurve.Of(new Point2D(-1000, 0), new Point2D(1000, 0), 1);
        var line = WallCurve.Of(new Point2D(0, 500), new Point2D(0, -2000), 0);

        var crossing = Assert.Single(circle.Crossings(line));
        Assert.True(crossing.DistanceTo(new Point2D(0, -1000)) < 1e-6);

        var apart = WallCurve.Of(new Point2D(5000, 0), new Point2D(6000, 0), 0);
        Assert.Empty(circle.Crossings(apart));
    }
}
