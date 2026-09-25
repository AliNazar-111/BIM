using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// The roof shapes people actually build, each modelled the way someone would model it here.
///
/// The list is the one from Balkan Architect's "10 Common Roof Shapes Modelled in Revit":
/// shed, gable, gable with catslide, gambrel, clerestory, hip, half-hip, dutch gable,
/// cross-gabled and butterfly. They are worth keeping as tests because between them they use
/// every part of a roof by footprint - which edges slope, at what pitch, from what plate
/// height - and because a roof engine that cannot make the common shapes is not finished,
/// whatever its geometry does in the abstract.
///
/// Gambrel is the one that cannot be made this way, and says so below.
/// </summary>
public class RoofShapeLibraryTests
{
    private static readonly double Rise30 = Math.Tan(30 * Math.PI / 180);

    private static Roof Footprint(params Point2D[] corners)
    {
        var roof = new Roof();
        roof.SetBoundary(corners);
        return roof;
    }

    private static Roof Rectangle(double width, double depth)
    {
        return Footprint(
            new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth));
    }

    private static Roof Slopes(Roof roof, double pitch, params int[] edges)
    {
        foreach (var index in edges)
        {
            roof.Edges[index].DefinesSlope = true;
            roof.Edges[index].SlopeDegrees = pitch;
        }

        return roof;
    }

    /// <summary>
    /// The faces must tile the footprint: no gaps, nothing counted twice, and every face a
    /// proper polygon.
    ///
    /// Adding up areas alone is not enough. A face whose outline crosses itself - a bow-tie -
    /// has a signed area in which the two halves partly cancel, and four such faces once added
    /// up to exactly the right total while the 3D view showed a hole in the roof. A simple
    /// polygon of n corners always cuts into n - 2 triangles; a crossed one does not.
    /// </summary>
    private static void Covers(RoofSurface surface, double area)
    {
        Assert.Equal(area, surface.Facets.Sum(facet => facet.Area), precision: 0);

        Assert.All(surface.Facets, facet =>
            Assert.Equal(facet.Outline.Count - 2, Polygon2D.Triangulate(facet.Outline).Count));
    }

    // 1. Shed -----------------------------------------------------------------------

    [Fact]
    public void AShedRoofRisesFromOneWallToTheOther()
    {
        var roof = Slopes(Rectangle(8000, 5000), 20, 0);
        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(RoofForm.Shed, surface.Form);
        Assert.Single(surface.Facets);
        Covers(surface, 40e6);
        Assert.Equal(5000 * Math.Tan(20 * Math.PI / 180), surface.RidgeElevation, precision: 3);
    }

    // 2. Gable ----------------------------------------------------------------------

    [Fact]
    public void AGableRoofIsTwoSlopesAndTwoOpenEnds()
    {
        var roof = Slopes(Rectangle(10000, 6000), 30, 0, 2);
        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(2, surface.Facets.Count);
        Covers(surface, 60e6);
        Assert.Equal(3000 * Rise30, surface.RidgeElevation, precision: 3);
    }

    // 3. Gable with a catslide -------------------------------------------------------

    [Fact]
    public void ACatslideCarriesOneSlopeDownPastTheOther()
    {
        // The catslide side reaches 3 m further out and its eave sits 1.5 m lower, which is
        // what a catslide is: one plane carried on down over a lean-to.
        var roof = Slopes(Rectangle(10000, 9000), 30, 0, 2);
        roof.Edges[2].PlateOffset = -1500;

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 3000);

        Assert.Equal(2, surface.Facets.Count);
        Covers(surface, 90e6);

        // The two eaves are at their own heights.
        Assert.Equal(3000, surface.HeightAt(new Point2D(5000, 0)), precision: 3);
        Assert.Equal(1500, surface.HeightAt(new Point2D(5000, 9000)), precision: 3);

        // And the ridge is off centre, toward the high side - the long slope is the catslide.
        var ridge = surface.BreakLines.Single();
        Assert.True(ridge.From.Y < 4500, $"The ridge sat at y = {ridge.From.Y:0}: the catslide is on the wrong side.");
    }

    // 4. Gambrel ---------------------------------------------------------------------

    [Fact]
    public void AGambrelNeedsTwoPitchesOnOneSideAndCannotBeMadeFromAFootprint()
    {
        // A gambrel breaks each slope in two - steep at the eaves, shallow at the ridge. A
        // footprint roof has one plane per eave, so the second pitch has nowhere to live: the
        // steeper of the two simply wins the whole side.
        var roof = Slopes(Rectangle(10000, 8000), 60, 0, 2);
        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(2, surface.Facets.Count);
        Assert.All(surface.Facets, facet => Assert.Equal(60, facet.Plane.SlopeDegrees, precision: 3));

        // Two roofs stacked will not do it either: the upper roof's eaves would have to start
        // where the lower one's slope has got to, which is a height the lower roof knows and
        // nothing asks it. This wants a roof by extrusion - a profile drawn in elevation -
        // which is not built yet.
    }

    // 5. Clerestory -------------------------------------------------------------------

    [Fact]
    public void AClerestoryIsTwoShedRoofsWithAGlazedStepBetweenThem()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<RoofType>().First().Id;
        var level = document.Levels[0].Id;

        // The low half, sloping down away from the step.
        var low = Slopes(Rectangle(10000, 4000), 15, 2);
        low.TypeId = type;
        low.LevelId = level;
        low.HeightOffset = 3000;
        document.Add(low);

        // The high half, starting a metre higher, with the glazing standing in between.
        var high = Footprint(
            new Point2D(0, 4000), new Point2D(10000, 4000), new Point2D(10000, 8000), new Point2D(0, 8000));
        Slopes(high, 15, 2);
        high.TypeId = type;
        high.LevelId = level;
        high.HeightOffset = 4200;
        document.Add(high);

        Assert.Equal(RoofForm.Shed, low.Form);
        Assert.Equal(RoofForm.Shed, high.Form);

        // The step between them is the clerestory: the high roof's eave stands well above the
        // low roof's ridge, and the wall between carries the glazing.
        Assert.True(high.GetTopElevation(document) - low.RidgeElevation(document) > 100,
            "The two roofs met, leaving no clerestory to glaze.");
    }

    // 6. Hip -------------------------------------------------------------------------

    [Fact]
    public void AHipRoofSlopesOnEverySide()
    {
        var roof = Slopes(Rectangle(10000, 6000), 30, 0, 1, 2, 3);
        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(RoofForm.Hip, surface.Form);
        Assert.Equal(4, surface.Facets.Count);
        Covers(surface, 60e6);

        // Four hips up from the corners and a ridge between them.
        Assert.Equal(5, surface.BreakLines.Count);
    }

    // 7. Half-hip (jerkinhead) --------------------------------------------------------

    [Fact]
    public void AHalfHipClipsTheTopOfAGableEnd()
    {
        // The ends slope, but from a plate raised most of the way to the ridge, so they only
        // catch the top corner - which is exactly what a jerkinhead is.
        var roof = Slopes(Rectangle(10000, 6000), 30, 0, 1, 2, 3);
        roof.Edges[1].PlateOffset = 1000;
        roof.Edges[3].PlateOffset = 1000;

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(4, surface.Facets.Count);
        Covers(surface, 60e6);

        var ends = surface.Facets
            .Where(facet => Math.Abs(facet.Plane.A) > Math.Abs(facet.Plane.B))
            .ToList();

        Assert.Equal(2, ends.Count);

        // The hipped part is small - a clipped corner, not a full hip end. A full hip end on
        // this roof would be 9 m²; the half-hip is a fraction of it.
        Assert.All(ends, end => Assert.True(end.Area < 4e6,
            $"The end face covered {end.Area / 1e6:0.0} m², which is a full hip rather than a half."));

        // The gable is still there under it: the wall sees a vertical end up to the plate.
        Assert.Equal(1000, surface.HeightAt(new Point2D(0, 3000)), precision: 1);
    }

    // 8. Dutch gable -------------------------------------------------------------------

    [Fact]
    public void ADutchGableIsAGabletSittingOnAHip()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<RoofType>().First().Id;
        var level = document.Levels[0].Id;

        // The hip, cut off 1.5 m in from its eaves - the height the gablet's eaves want.
        var deck = 1500 * Rise30;
        var hip = Slopes(Rectangle(10000, 6000), 30, 0, 1, 2, 3);
        hip.TypeId = type;
        hip.LevelId = level;
        hip.HeightOffset = 3000;
        hip.CutoffOffset = deck;
        document.Add(hip);

        // The gablet: a small gable roof standing on the deck the cutoff leaves - on top of the
        // hip's build-up there, since a roof stands on what carries it.
        var thickness = document.FindType<SlabType>(type)!.Thickness;
        var gablet = Footprint(
            new Point2D(1500, 1500), new Point2D(8500, 1500), new Point2D(8500, 4500), new Point2D(1500, 4500));
        Slopes(gablet, 30, 0, 2);
        gablet.TypeId = type;
        gablet.LevelId = level;
        gablet.HeightOffset = 3000 + deck + thickness;
        document.Add(gablet);

        Assert.Equal(RoofForm.Hip, hip.Form);
        Assert.Equal(RoofForm.Gable, gablet.Form);

        // The hip stops at the deck rather than running on to a ridge nobody can see.
        Assert.Equal(3000 + deck, hip.Surface(document).RidgeElevation, precision: 3);
        Assert.Equal(3000 + deck, hip.UndersideAt(document, new Point2D(5000, 3000)), precision: 3);
        // Its highest point is the rim of the deck, where the slopes' build-up - measured square
        // to the slope, so deeper than the flat deck's - meets it.
        Assert.Equal(3000 + deck + thickness / Math.Cos(30 * Math.PI / 180), hip.RidgeElevation(document), precision: 3);

        // The gablet bears exactly on the deck's top and carries on up, so the two meet
        // instead of passing through each other.
        Assert.Equal(hip.TopAt(document, new Point2D(5000, 3000)), gablet.GetBottomElevation(document), precision: 3);
        Assert.True(gablet.RidgeElevation(document) > hip.RidgeElevation(document));

        // The deck itself is a face of the hip: flat, and the size of what the slopes left.
        var flat = hip.Surface(document).Facets.Where(facet => facet.Plane.Rise < 1e-9).ToList();
        Assert.Single(flat);
        Assert.Equal(7000 * 3000, flat[0].Area, precision: 0);
    }

    // 9. Cross-gabled --------------------------------------------------------------------

    [Fact]
    public void ACrossGabledRoofMakesItsOwnValleys()
    {
        // A T-shaped house: a 12 m x 6 m range with a 6 m x 6 m wing off the back.
        var roof = Footprint(
            new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000),
            new Point2D(9000, 6000), new Point2D(9000, 12000), new Point2D(3000, 12000),
            new Point2D(3000, 6000), new Point2D(0, 6000));

        // Gable both the range's ends and the wing's end; slope every long side.
        Slopes(roof, 30, 0, 2, 3, 5, 6, 7);
        roof.Edges[1].DefinesSlope = false;   // the range's east end
        roof.Edges[4].DefinesSlope = false;   // the wing's north end
        roof.Edges[7].DefinesSlope = false;   // the range's west end

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        // 72 m² of range plus 36 m² of wing, all covered.
        Covers(surface, 108e6);

        // Two ridges at the same height, one along each range, crossing in the middle.
        Assert.Equal(3000 * Rise30, surface.RidgeElevation, precision: 3);

        // And the valleys: lines running down to where the wing meets the range, at the two
        // inside corners. They are what makes this shape worth having.
        var corners = new[] { new Point2D(3000, 6000), new Point2D(9000, 6000) };
        foreach (var corner in corners)
        {
            Assert.Contains(surface.BreakLines, line =>
                line.From.DistanceTo(corner) < 1 || line.To.DistanceTo(corner) < 1);

            // A valley runs down to the eave height at the corner it starts from.
            Assert.Equal(0, surface.HeightAt(corner), precision: 3);
        }
    }

    // 10. Butterfly ------------------------------------------------------------------------

    [Fact]
    public void AButterflyIsTwoRoofsRisingAwayFromACentralValley()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<RoofType>().First().Id;
        var level = document.Levels[0].Id;

        // Each half slopes up from the middle: the slope-defining edge is the inner one, so
        // the roof climbs outward and the water runs to the gutter between them.
        var west = Slopes(Rectangle(5000, 8000), 15, 1);
        west.TypeId = type;
        west.LevelId = level;
        west.HeightOffset = 3000;
        document.Add(west);

        var east = Footprint(
            new Point2D(5000, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(5000, 8000));
        Slopes(east, 15, 3);
        east.TypeId = type;
        east.LevelId = level;
        east.HeightOffset = 3000;
        document.Add(east);

        // Low in the middle, high at the outside walls: a butterfly.
        Assert.Equal(3000, west.UndersideAt(document, new Point2D(5000, 4000)), precision: 0);
        Assert.Equal(3000, east.UndersideAt(document, new Point2D(5000, 4000)), precision: 0);

        var rise = 5000 * Math.Tan(15 * Math.PI / 180);
        Assert.Equal(3000 + rise, west.UndersideAt(document, new Point2D(0, 4000)), precision: 0);
        Assert.Equal(3000 + rise, east.UndersideAt(document, new Point2D(10000, 4000)), precision: 0);
    }
}
