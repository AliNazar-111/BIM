using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Slope arrows (Revit's Slope Arrow): slopes drawn in plan from a tail on the outline toward
/// a head, for the roofs edges alone cannot describe.
/// </summary>
public class RoofSlopeArrowTests
{
    private static readonly double Rise30 = Math.Tan(30 * Math.PI / 180);

    private static List<RoofEdge> Edges(params bool[] sloping) =>
        sloping.Select(slope => new RoofEdge { DefinesSlope = slope, SlopeDegrees = 30 }).ToList();

    private static readonly Point2D[] Box =
    {
        new(0, 0), new(10000, 0), new(10000, 6000), new(0, 6000)
    };

    /// <summary>The faces must tile the footprint, each a proper polygon.</summary>
    private static void Covers(RoofSurface surface, double area)
    {
        Assert.Equal(area, surface.Facets.Sum(facet => facet.Area), precision: 0);
        Assert.All(surface.Facets, facet =>
            Assert.Equal(facet.Outline.Count - 2, Polygon2D.Triangulate(facet.Outline).Count));
    }

    [Fact]
    public void AnArrowOnAFlatRoofMakesItFallOneWay()
    {
        // From the middle of the west side to the middle of the east, rising 2°.
        var arrow = new RoofSlopeArrow { Tail = new Point2D(0, 3000), Head = new Point2D(10000, 3000), SlopeDegrees = 2 };
        var surface = RoofShape.Build(Box, Edges(false, false, false, false), 3000, arrows: new[] { arrow });

        Assert.Equal(RoofForm.Shed, surface.Form);
        Assert.Single(surface.Facets);
        Covers(surface, 60e6);

        var rise = Math.Tan(2 * Math.PI / 180);
        Assert.Equal(3000, surface.HeightAt(new Point2D(0, 1000)), precision: 3);
        Assert.Equal(3000 + 10000 * rise, surface.HeightAt(new Point2D(10000, 5000)), precision: 3);
    }

    [Fact]
    public void AnArrowCanRunDiagonallyFromCornerToCorner()
    {
        var arrow = new RoofSlopeArrow { Tail = new Point2D(0, 0), Head = new Point2D(10000, 6000), SlopeDegrees = 3 };
        var surface = RoofShape.Build(Box, Edges(false, false, false, false), 0, arrows: new[] { arrow });

        // One plane over the whole roof, low at one corner and high at the opposite one.
        Covers(surface, 60e6);

        var rise = Math.Tan(3 * Math.PI / 180);
        Assert.Equal(0, surface.HeightAt(new Point2D(0, 0)), precision: 3);
        Assert.Equal(Math.Sqrt(10000.0 * 10000 + 6000.0 * 6000) * rise, surface.HeightAt(new Point2D(10000, 6000)), precision: 3);

        // The other two corners are in between, where their distance along the arrow puts them.
        var along = (10000.0 * 10000) / Math.Sqrt(10000.0 * 10000 + 6000.0 * 6000);
        Assert.Equal(along * rise, surface.HeightAt(new Point2D(10000, 0)), precision: 3);
    }

    [Fact]
    public void AnArrowCanBeGivenByTheHeightsAtItsEnds()
    {
        // 300 mm of fall over 10 m: the pitch is worked out, not typed.
        var arrow = new RoofSlopeArrow
        {
            Tail = new Point2D(0, 3000), Head = new Point2D(10000, 3000),
            ByHeights = true, TailOffset = 0, HeadOffset = 300
        };

        Assert.Equal(0.03, arrow.Rise, precision: 9);

        var surface = RoofShape.Build(Box, Edges(false, false, false, false), 3000, arrows: new[] { arrow });
        Assert.Equal(3300, surface.HeightAt(new Point2D(10000, 3000)), precision: 3);
    }

    [Fact]
    public void AnArrowUpFromARaisedGableEndMakesAHalfHip()
    {
        // A gable roof, with a hip begun a metre up the east end: an arrow from the middle of
        // the end, inward, at the roof's own pitch, its tail raised a metre.
        var arrow = new RoofSlopeArrow
        {
            Tail = new Point2D(10000, 3000), Head = new Point2D(8000, 3000),
            SlopeDegrees = 30, TailOffset = 1000
        };

        var byArrow = RoofShape.Build(Box, Edges(true, false, true, false), 0, arrows: new[] { arrow });
        Covers(byArrow, 60e6);

        // The same as raising the end eave itself - two ways of saying one roof.
        var edges = Edges(true, true, true, false);
        edges[1].PlateOffset = 1000;
        var byEave = RoofShape.Build(Box, edges, 0);

        foreach (var point in new[] { new Point2D(9800, 3000), new Point2D(9900, 2500), new Point2D(9000, 1000), new Point2D(5000, 3000) })
            Assert.Equal(byEave.HeightAt(point), byArrow.HeightAt(point), precision: 3);

        // The gable is still there under the clipped top.
        Assert.Equal(1000, byArrow.HeightAt(new Point2D(10000, 3000)), precision: 3);
    }

    [Fact]
    public void TwoArrowsOnAStretchOfEaveMakeADormer()
    {
        // The south eave split in three; the middle stretch does not slope itself, but has two
        // arrows from its ends to its middle - Revit's dormer made with slope arrows.
        var outline = new[]
        {
            new Point2D(0, 0), new Point2D(4000, 0), new Point2D(6000, 0), new Point2D(10000, 0),
            new Point2D(10000, 6000), new Point2D(0, 6000)
        };

        var edges = Edges(true, false, true, false, true, false);
        var arrows = new[]
        {
            new RoofSlopeArrow { Tail = new Point2D(4000, 0), Head = new Point2D(5000, 0), SlopeDegrees = 30 },
            new RoofSlopeArrow { Tail = new Point2D(6000, 0), Head = new Point2D(5000, 0), SlopeDegrees = 30 }
        };

        var surface = RoofShape.Build(outline, edges, 0, arrows: arrows);
        Covers(surface, 60e6);

        // Either side of the dormer the eave is where it always was.
        Assert.Equal(0, surface.HeightAt(new Point2D(2000, 0)), precision: 3);
        Assert.Equal(0, surface.HeightAt(new Point2D(8000, 0)), precision: 3);

        // In the middle the eave is lifted into a little gable: up a metre's worth of pitch at
        // its peak, coming down to the eave again at the split points.
        Assert.Equal(1000 * Rise30, surface.HeightAt(new Point2D(5000, 0)), precision: 3);
        Assert.Equal(500 * Rise30, surface.HeightAt(new Point2D(4500, 0)), precision: 3);

        // It is a real gable with a ridge: from the peak inward to where the main slope has
        // risen to meet it, a metre in.
        Assert.Contains(surface.BreakLines, line =>
            (line.From.DistanceTo(new Point2D(5000, 0)) < 1 && line.To.DistanceTo(new Point2D(5000, 1000)) < 1) ||
            (line.To.DistanceTo(new Point2D(5000, 0)) < 1 && line.From.DistanceTo(new Point2D(5000, 1000)) < 1));

        // Past that the main roof carries on as before.
        Assert.Equal(2000 * Rise30, surface.HeightAt(new Point2D(5000, 2000)), precision: 3);
    }

    [Fact]
    public void ADormerOnAnOverhangingEaveStartsAtTheEaveNotTheBase()
    {
        // The same dormer, but on eaves picked from walls with a 400 mm overhang: the eave's
        // edge is then lower than the roof's base by the overhang times the pitch. The dormer
        // must start there too, or it would stand on a step above the eave it is cut into.
        var outline = new[]
        {
            new Point2D(0, 0), new Point2D(4000, 0), new Point2D(6000, 0), new Point2D(10000, 0),
            new Point2D(10000, 6000), new Point2D(0, 6000)
        };

        var edges = Edges(true, false, true, false, true, false);
        foreach (var edge in edges)
        {
            edge.WallId = Guid.NewGuid();
            edge.Overhang = 400;
        }

        var arrows = new[]
        {
            new RoofSlopeArrow { Tail = new Point2D(4000, 0), Head = new Point2D(5000, 0), SlopeDegrees = 30 },
            new RoofSlopeArrow { Tail = new Point2D(6000, 0), Head = new Point2D(5000, 0), SlopeDegrees = 30 }
        };

        var surface = RoofShape.Build(outline, edges, 0, arrows: arrows);
        Covers(surface, 60e6);

        var eave = -400 * Rise30;
        Assert.Equal(eave, surface.HeightAt(new Point2D(2000, 0)), precision: 3);
        Assert.Equal(eave, surface.HeightAt(new Point2D(4000, 0)), precision: 3);
        Assert.Equal(eave + 1000 * Rise30, surface.HeightAt(new Point2D(5000, 0)), precision: 3);

        // And the dormer is only as big as it was drawn: two small faces, not half the roof.
        Assert.Equal(1e6, surface.Facets.Where(facet => !facet.HasEave).Sum(facet => facet.Area), precision: 0);
    }

    [Fact]
    public void AnArrowWhoseTailIsOffTheOutlineIsIgnored()
    {
        var arrow = new RoofSlopeArrow { Tail = new Point2D(5000, 3000), Head = new Point2D(8000, 3000), SlopeDegrees = 5 };
        var surface = RoofShape.Build(Box, Edges(false, false, false, false), 3000, arrows: new[] { arrow });

        Assert.Equal(3000, surface.HeightAt(new Point2D(9000, 3000)), precision: 6);
    }

    [Fact]
    public void ArrowsAreSavedCopiedAndChangeTheRoofsShapeName()
    {
        var document = BimDocument.CreateDefault();
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[0].Id };
        roof.SetBoundary(Box);
        roof.SetSlopeArrows(new[]
        {
            new RoofSlopeArrow
            {
                Tail = new Point2D(0, 3000), Head = new Point2D(10000, 3000),
                ByHeights = true, TailOffset = 50, HeadOffset = 250
            }
        });

        document.Add(roof);
        Assert.Equal(RoofForm.Shed, roof.Form);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single();
        var arrow = Assert.Single(reloaded.SlopeArrows);
        Assert.True(arrow.ByHeights);
        Assert.Equal(250, arrow.HeadOffset, precision: 6);
        Assert.Equal(new Point2D(10000, 3000), arrow.Head);

        var copy = (Roof)ElementCopy.Clone(roof)!;
        Assert.Single(copy.SlopeArrows);
        Assert.NotSame(roof.SlopeArrows[0], copy.SlopeArrows[0]);
    }
}
