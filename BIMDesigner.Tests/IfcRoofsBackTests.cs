using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;

namespace BIMDesigner.Tests;

/// <summary>
/// Pitched roofs back from IFC as roofs: an IfcRoof made of one sloping IfcSlab a face, taken back into a roof by its footprint -
/// each edge an eave or a gable - and kept only where the roof it makes has the faces the file drew.
/// </summary>
public class IfcRoofsBackTests
{
    private static BimDocument Back(BimDocument document)
    {
        using var model = IfcExport.Build(document);
        return IfcImport.Read(model, "Roofs").Document;
    }

    /// <summary>A 10 by 8 m box of walls with a roof on it at 40°, its edges sloping as given.</summary>
    private static (BimDocument Document, Roof Roof) Box(params bool[] slopes)
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        roof.SetEdges(slopes.Select(slope => new RoofEdge { DefinesSlope = slope, SlopeDegrees = 40 }));
        return (document, roof);
    }

    [Fact]
    public void AHippedRoofComesBackAsAHippedRoof()
    {
        var (document, original) = Box(true, true, true, true);
        Assert.Equal(RoofForm.Hip, original.Form);
        var back = Back(document);

        // One roof, no faces left over as shapes: its outline the box, every edge an eave at 40°, its base where the walls stop.
        var roof = Assert.Single(back.All<Roof>());
        Assert.Empty(back.All<Component>());
        Assert.Equal(RoofForm.Hip, roof.Form);
        Assert.Equal(Polygon2D.Area(original.Boundary), Polygon2D.Area(roof.Boundary), 0);
        Assert.Equal(4, roof.Boundary.Count);
        Assert.All(roof.Edges, edge => Assert.Equal((true, 40.0), (edge.DefinesSlope, Math.Round(edge.SlopeDegrees, 3))));
        Assert.Equal(original.GetBottomElevation(document), roof.GetBottomElevation(back), 1);
        Assert.Equal(original.SlopingArea(document), roof.SlopingArea(back), 0);
    }

    [Fact]
    public void AGableRoofComesBackWithItsGables()
    {
        // Eaves front and back, gables at the ends: the gables' edges, which the two faces meet at the ridge, are one edge again each.
        var (document, original) = Box(true, false, true, false);
        Assert.Equal(RoofForm.Gable, original.Form);
        var back = Back(document);
        var roof = Assert.Single(back.All<Roof>());
        Assert.Equal(RoofForm.Gable, roof.Form);
        Assert.Equal(4, roof.Boundary.Count);
        Assert.Equal(2, roof.Edges.Count(edge => edge.DefinesSlope));
        Assert.Equal(original.SlopingArea(document), roof.SlopingArea(back), 0);

        // The ridge where it was: 4 m in from the eaves at 40°, 4000 tan 40° = 3,356 mm over them.
        var surface = roof.Surface(back);
        Assert.Equal(surface.HeightAt(new Point2D(5000, 0)) + 4000 * Math.Tan(40 * Math.PI / 180), surface.HeightAt(new Point2D(5000, 4000)), 0);
    }

    [Fact]
    public void ALeanToComesBackFromItsOneFace()
    {
        var (document, original) = Box(true, false, false, false);
        Assert.Equal(RoofForm.Shed, original.Form);
        var roof = Assert.Single(Back(document).All<Roof>());
        Assert.Equal(RoofForm.Shed, roof.Form);
        Assert.Single(roof.Edges, edge => edge.DefinesSlope);
    }

    [Fact]
    public void ARoofWithAnOpeningComesBackWithIt()
    {
        // A hipped roof with a 1 by 1.5 m opening through its front face: the face goes out in pieces round it, and the hole their
        // union leaves comes back as the opening.
        var (document, original) = Box(true, true, true, true);
        var hole = new[] { new Point2D(4000, 1000), new Point2D(5000, 1000), new Point2D(5000, 2500), new Point2D(4000, 2500) };
        original.SetOpenings(new[] { new RoofOpening(hole, new Guid?[4]) });
        var roof = Assert.Single(Back(document).All<Roof>());
        var opening = Assert.Single(roof.Openings);
        Assert.Equal(1.5e6, Polygon2D.Area(opening.Points), 0);
        Assert.Equal(RoofForm.Hip, roof.Form);
    }
}
