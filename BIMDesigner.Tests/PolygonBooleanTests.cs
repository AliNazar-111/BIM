using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Combining flat shapes: the engine a profile editor is built on. Cutting a notch, punching a
/// hole and joining two shapes into one are the same arithmetic, so they are tested together.
/// </summary>
public class PolygonBooleanTests
{
    private static IReadOnlyList<Point2D> Box(double left, double bottom, double right, double top) => new[]
    {
        new Point2D(left, bottom), new Point2D(right, bottom), new Point2D(right, top), new Point2D(left, top)
    };

    private static IReadOnlyList<Point2D> Circle(Point2D centre, double radius, int sides = 32) =>
        Enumerable.Range(0, sides)
            .Select(i => 2 * Math.PI * i / sides)
            .Select(angle => new Point2D(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle)))
            .ToList();

    [Fact]
    public void CuttingANotchOutOfTheEdgeLeavesOneShape()
    {
        var column = Box(0, 0, 400, 400);
        var notch = Box(300, 300, 500, 500);

        var result = PolygonBoolean.Combine(column, notch, BooleanOperation.Difference);

        var region = Assert.Single(result);
        Assert.Empty(region.Holes);
        Assert.Equal(400 * 400 - 100 * 100, region.Area, precision: 3);

        // The corner that was cut away is outside now, and the rest of the column is still in.
        Assert.False(Polygon2D.Contains(region.Outer, new Point2D(350, 350)));
        Assert.True(Polygon2D.Contains(region.Outer, new Point2D(200, 200)));
        Assert.True(Polygon2D.Contains(region.Outer, new Point2D(350, 200)));
    }

    [Fact]
    public void CuttingInTheMiddleLeavesAHoleRatherThanTwoShapes()
    {
        var column = Box(0, 0, 400, 400);
        var hole = Box(150, 150, 250, 250);

        var result = PolygonBoolean.Combine(column, hole, BooleanOperation.Difference);

        var region = Assert.Single(result);
        var void_ = Assert.Single(region.Holes);

        Assert.Equal(400 * 400 - 100 * 100, region.Area, precision: 3);
        Assert.Equal(100 * 100, Polygon2D.Area(void_), precision: 3);

        // The outer edge is untouched and the middle is genuinely out of the shape.
        Assert.Equal(400 * 400, Polygon2D.Area(region.Outer), precision: 3);
        Assert.False(PolygonBoolean.Inside(new[] { region.Outer, void_ }, new Point2D(200, 200)));
        Assert.True(PolygonBoolean.Inside(new[] { region.Outer, void_ }, new Point2D(50, 50)));
    }

    [Fact]
    public void CuttingRightThroughLeavesTwoShapes()
    {
        var column = Box(0, 0, 400, 400);
        var slot = Box(-50, 150, 450, 250);

        var result = PolygonBoolean.Combine(column, slot, BooleanOperation.Difference);

        Assert.Equal(2, result.Count);
        Assert.Equal(400 * 150, result[0].Area, precision: 3);
        Assert.Equal(400 * 150, result[1].Area, precision: 3);
    }

    [Fact]
    public void AddingTwoShapesMakesOne()
    {
        var shaft = Box(0, 0, 200, 400);
        var wing = Box(150, 100, 400, 200);

        var result = PolygonBoolean.Combine(shaft, wing, BooleanOperation.Union);

        var region = Assert.Single(result);
        Assert.Empty(region.Holes);

        // The overlap is counted once, which is the whole point of a union.
        Assert.Equal(200 * 400 + 250 * 100 - 50 * 100, region.Area, precision: 3);
        Assert.True(Polygon2D.Contains(region.Outer, new Point2D(300, 150)));
        Assert.True(Polygon2D.Contains(region.Outer, new Point2D(100, 350)));
    }

    [Fact]
    public void ShapesThatDoNotTouchStaySeparateOrVanish()
    {
        var one = Box(0, 0, 100, 100);
        var other = Box(300, 300, 400, 400);

        Assert.Equal(2, PolygonBoolean.Combine(one, other, BooleanOperation.Union).Count);
        Assert.Empty(PolygonBoolean.Combine(one, other, BooleanOperation.Intersection));

        var difference = Assert.Single(PolygonBoolean.Combine(one, other, BooleanOperation.Difference));
        Assert.Equal(100 * 100, difference.Area, precision: 3);
    }

    [Fact]
    public void IntersectionKeepsOnlyWhatIsInBoth()
    {
        var result = PolygonBoolean.Combine(Box(0, 0, 400, 400), Box(300, 300, 700, 700), BooleanOperation.Intersection);

        var region = Assert.Single(result);
        Assert.Equal(100 * 100, region.Area, precision: 3);
        Assert.True(Polygon2D.Contains(region.Outer, new Point2D(350, 350)));
    }

    [Fact]
    public void ItWorksOnCurvesAsWellAsBoxes()
    {
        // A round column with a round hole down the middle: the shape a hollow pier is.
        var shaft = Circle(new Point2D(0, 0), 200);
        var bore = Circle(new Point2D(0, 0), 80);

        var region = Assert.Single(PolygonBoolean.Combine(shaft, bore, BooleanOperation.Difference));
        var hole = Assert.Single(region.Holes);

        Assert.Equal(Polygon2D.Area(shaft) - Polygon2D.Area(bore), region.Area, precision: 3);
        Assert.Equal(Polygon2D.Area(bore), Polygon2D.Area(hole), precision: 3);

        // And a quarter taken out of a round column by a box, which crosses the curve twice.
        var quartered = Assert.Single(PolygonBoolean.Combine(shaft, Box(0, 0, 300, 300), BooleanOperation.Difference));
        Assert.Equal(Polygon2D.Area(shaft) * 0.75, quartered.Area, precision: 0);
    }

    [Fact]
    public void AShapeThatAlreadyHasAHoleKeepsItThroughFurtherEdits()
    {
        var withHole = PolygonBoolean.Combine(Box(0, 0, 400, 400), Box(150, 150, 250, 250), BooleanOperation.Difference);
        var region = Assert.Single(withHole);

        var loops = new[] { region.Outer }.Concat(region.Holes).ToList();

        // Cutting a second hole leaves both.
        var twice = Assert.Single(PolygonBoolean.Combine(loops, new[] { Box(300, 50, 350, 100) }, BooleanOperation.Difference));
        Assert.Equal(2, twice.Holes.Count);
        Assert.Equal(400 * 400 - 100 * 100 - 50 * 50, twice.Area, precision: 3);

        // Adding a shape over the first hole fills it in again.
        var filled = Assert.Single(PolygonBoolean.Combine(loops, new[] { Box(140, 140, 260, 260) }, BooleanOperation.Union));
        Assert.Empty(filled.Holes);
        Assert.Equal(400 * 400, filled.Area, precision: 3);
    }

    [Fact]
    public void ShapesThatShareAnEdgeExactlyStillComeOutRight()
    {
        // Drawing a shape flush with an edge of what is already there is an ordinary thing to
        // do - a nib on the end of a pier, a notch lined up with a face - and the two then
        // share part of an edge exactly. Asking whether such an edge is inside the other shape
        // has no answer, so it has to be decided by which way each of them runs.
        var bar = Box(-250, -150, 250, 150);
        var nib = Box(-250, -250, -150, 250);

        var joined = Assert.Single(PolygonBoolean.Combine(bar, nib, BooleanOperation.Union));
        Assert.Equal(500 * 300 + 100 * 500 - 100 * 300, joined.Area, precision: 3);
        Assert.Empty(joined.Holes);

        var cut = Assert.Single(PolygonBoolean.Combine(bar, Box(-250, -50, 0, 50), BooleanOperation.Difference));
        Assert.Equal(500 * 300 - 250 * 100, cut.Area, precision: 3);

        var both = Assert.Single(PolygonBoolean.Combine(bar, nib, BooleanOperation.Intersection));
        Assert.Equal(100 * 300, both.Area, precision: 3);

        // Two shapes meeting along an edge from opposite sides: joined they are one, and they
        // share nothing.
        var above = Box(-250, 150, 250, 400);
        Assert.Equal(500 * 300 + 500 * 250,
            Assert.Single(PolygonBoolean.Combine(bar, above, BooleanOperation.Union)).Area, precision: 3);

        Assert.Empty(PolygonBoolean.Combine(bar, above, BooleanOperation.Intersection));
        Assert.Equal(500 * 300,
            Assert.Single(PolygonBoolean.Combine(bar, above, BooleanOperation.Difference)).Area, precision: 3);
    }

    [Fact]
    public void TheWindingItIsDrawnInDoesNotMatter()
    {
        var clockwise = Box(0, 0, 400, 400).Reverse().ToList();
        var cut = Box(150, 150, 250, 250).Reverse().ToList();

        var region = Assert.Single(PolygonBoolean.Combine(clockwise, cut, BooleanOperation.Difference));

        Assert.Single(region.Holes);
        Assert.Equal(400 * 400 - 100 * 100, region.Area, precision: 3);

        // What comes out is always wound the same way: the shape one way, its holes the other.
        Assert.True(Polygon2D.SignedArea(region.Outer) > 0);
        Assert.True(Polygon2D.SignedArea(region.Holes[0]) < 0);
    }

    [Fact]
    public void EdgesThatRunAlongEachOtherForPartOfTheirLengthAreSharedNotCutAtRandom()
    {
        // The end face of a half-hipped roof, and the ground the far slope covers. They meet
        // along a hip: the triangle's top edge lies on the hexagon's long diagonal, for part of
        // its length. The coordinates are the ones the roof builder produced, down to the last
        // digit, because it is the last digits that did it - two edges that parallel have a
        // cross product that is not quite zero, and dividing by it invented crossings at
        // arbitrary points along the hip. The triangle was cut at two of them, the piece
        // between was thrown away, the rest could not be closed into a loop, and the face
        // vanished from the roof.
        var face = new List<Point2D>
        {
            new(1267.9491924311196, 2999.999999999996),
            new(0, 4267.949192431116),
            new(0, 1732.050807568873)
        };

        var slope = new List<Point2D>
        {
            new(4267.94919243112, 0), new(5732.05080756887, 0),
            new(10000, 4267.949192431135), new(10000, 6000), new(0, 6000), new(0, 4267.949192431115)
        };

        // They only touch, so taking one from the other leaves it whole.
        var left = Assert.Single(PolygonBoolean.Combine(face, slope, BooleanOperation.Difference));
        Assert.Equal(Polygon2D.Area(face), left.Area, precision: 0);

        // And joined, they are one shape the size of both.
        var joined = Assert.Single(PolygonBoolean.Combine(face, slope, BooleanOperation.Union));
        Assert.Equal(Polygon2D.Area(face) + Polygon2D.Area(slope), joined.Area, precision: 0);
    }
}
