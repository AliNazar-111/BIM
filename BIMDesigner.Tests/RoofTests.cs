using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Roofs built from a footprint, the way Revit's Roof by Footprint works and IFC's IfcRoof
/// describes: every edge either defines a slope or is cut off, and the roof is what those
/// eave planes make between them.
/// </summary>
public class RoofTests
{
    private static Roof Rectangle(double width, double depth, bool allSloping = true, double pitch = 30)
    {
        var roof = new Roof();
        roof.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth)
        });

        foreach (var edge in roof.Edges)
        {
            edge.DefinesSlope = allSloping;
            edge.SlopeDegrees = pitch;
        }

        return roof;
    }

    /// <summary>
    /// The plan area the faces cover - once each has been checked to be a proper polygon, since
    /// a face whose outline crosses itself has an area that partly cancels and can add up to the
    /// right total while leaving a hole in the roof.
    /// </summary>
    private static double PlanArea(RoofSurface surface)
    {
        Assert.All(surface.Facets, facet =>
            Assert.Equal(facet.Outline.Count - 2, Polygon2D.Triangulate(facet.Outline).Count));

        return surface.Facets.Sum(facet => facet.Area);
    }

    [Fact]
    public void ARoofWithNoSlopingEdgeIsFlat()
    {
        var roof = Rectangle(6000, 4000, allSloping: false);
        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 3000);

        Assert.True(surface.IsFlat);
        Assert.Equal(RoofForm.Flat, surface.Form);
        Assert.Single(surface.Facets);
        Assert.Empty(surface.BreakLines);
        Assert.Equal(3000, surface.HeightAt(new Point2D(2000, 2000)), precision: 6);
        Assert.Equal(24e6, PlanArea(surface), precision: 0);
    }

    [Fact]
    public void FourSlopingEdgesOnASquareMakeAPyramid()
    {
        var roof = Rectangle(6000, 6000, pitch: 45);
        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(RoofForm.Hip, surface.Form);
        Assert.Equal(4, surface.Facets.Count);

        // At 45° the apex stands as high above the eaves as it is in from them.
        Assert.Equal(3000, surface.RidgeElevation, precision: 3);
        Assert.Equal(3000, surface.HeightAt(new Point2D(3000, 3000)), precision: 3);

        // The eaves are the eaves: on the line itself the roof is at its base.
        Assert.Equal(0, surface.HeightAt(new Point2D(3000, 0)), precision: 3);

        // The faces cover the footprint exactly - no gaps, nothing counted twice.
        Assert.Equal(36e6, PlanArea(surface), precision: 0);

        // Four hips running up to the apex, and no ridge, because there is no ridge on a
        // square: the four planes all meet at one point.
        Assert.Equal(4, surface.BreakLines.Count);
    }

    [Fact]
    public void AHipRoofOnARectangleHasARidgeAsLongAsTheDifference()
    {
        var roof = Rectangle(10000, 6000);
        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(4, surface.Facets.Count);
        Assert.Equal(60e6, PlanArea(surface), precision: 0);

        // The ridge runs the long way, 3 m in from each end, so 10 m - 2 × 3 m.
        var ridge = surface.BreakLines
            .Where(line => Math.Abs(line.From.Y - 3000) < 1 && Math.Abs(line.To.Y - 3000) < 1)
            .ToList();

        Assert.Single(ridge);
        Assert.Equal(4000, ridge[0].From.DistanceTo(ridge[0].To), precision: 3);

        // And it sits at the pitch: 3 m in at 30°.
        Assert.Equal(3000 * Math.Tan(30 * Math.PI / 180), surface.RidgeElevation, precision: 3);
    }

    [Fact]
    public void TwoOppositeSlopingEdgesMakeAGable()
    {
        var roof = Rectangle(10000, 6000, allSloping: false);
        roof.Edges[0].DefinesSlope = true;   // the 10 m side at y = 0
        roof.Edges[2].DefinesSlope = true;   // the 10 m side at y = 6000

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(RoofForm.Gable, surface.Form);
        Assert.Equal(2, surface.Facets.Count);
        Assert.Equal(60e6, PlanArea(surface), precision: 0);

        // One ridge, the full length of the building, over the middle.
        Assert.Single(surface.BreakLines);
        Assert.Equal(10000, surface.BreakLines[0].From.DistanceTo(surface.BreakLines[0].To), precision: 3);

        // The ends are cut off vertically rather than hipped, so the roof is at full height
        // right up to the gable wall.
        Assert.Equal(surface.RidgeElevation, surface.HeightAt(new Point2D(20, 3000)), precision: 3);
    }

    [Fact]
    public void OneSlopingEdgeMakesAShed()
    {
        var roof = Rectangle(6000, 4000, allSloping: false, pitch: 20);
        roof.Edges[0].DefinesSlope = true;   // the edge at y = 0

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 2500);

        Assert.Equal(RoofForm.Shed, surface.Form);
        Assert.Single(surface.Facets);
        Assert.Empty(surface.BreakLines);

        // It climbs the whole way across, from the eave to the high wall.
        Assert.Equal(2500, surface.HeightAt(new Point2D(3000, 0)), precision: 3);
        Assert.Equal(2500 + 4000 * Math.Tan(20 * Math.PI / 180),
            surface.HeightAt(new Point2D(3000, 4000)), precision: 3);
    }

    [Fact]
    public void AnLShapedRoofGetsAValley()
    {
        // An L: 10 m × 10 m with a 5 m × 5 m bite out of the far corner.
        var roof = new Roof();
        roof.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 5000),
            new Point2D(5000, 5000), new Point2D(5000, 10000), new Point2D(0, 10000)
        });

        foreach (var edge in roof.Edges) edge.DefinesSlope = true;

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(6, surface.Facets.Count);

        // Still covers its footprint exactly: 100 m² less the 25 m² bite.
        Assert.Equal(75e6, PlanArea(surface), precision: 0);

        // The inside corner throws a valley: a line starting at that corner and climbing
        // inward, which is the thing an L-shaped roof is known for.
        var inner = new Point2D(5000, 5000);
        var valley = surface.BreakLines.Where(line =>
            line.From.DistanceTo(inner) < 1 || line.To.DistanceTo(inner) < 1).ToList();

        Assert.NotEmpty(valley);
        Assert.Equal(0, surface.HeightAt(inner), precision: 3);
    }

    [Fact]
    public void OneEaveCanBeLiftedAboveTheOther()
    {
        var roof = Rectangle(8000, 6000, allSloping: false, pitch: 30);
        roof.Edges[0].DefinesSlope = true;
        roof.Edges[2].DefinesSlope = true;
        roof.Edges[2].PlateOffset = 1000;    // a wall plate a metre higher on the far side

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        Assert.Equal(0, surface.HeightAt(new Point2D(4000, 0)), precision: 3);
        Assert.Equal(1000, surface.HeightAt(new Point2D(4000, 6000)), precision: 3);

        // The ridge is no longer over the middle. The low side has further to climb before the
        // two planes meet, so the ridge sits away from it - which is what lifting one plate
        // does to a roof, and why the two slopes end up covering unequal spans.
        var ridge = surface.BreakLines.Single();
        Assert.True(ridge.From.Y > 3000, $"The ridge sat at y = {ridge.From.Y:0}, not away from the low eave.");
    }

    [Fact]
    public void TheSurfaceOfAPitchedRoofIsLargerThanItsFootprint()
    {
        var roof = Rectangle(10000, 6000, pitch: 45);
        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);

        // At 45° every face is √2 times its shadow, and tiles are bought by the face.
        Assert.Equal(60e6 * Math.Sqrt(2), surface.SlopingArea, precision: 0);
    }

    [Fact]
    public void TheShapeCanBeChosenByName()
    {
        var roof = Rectangle(10000, 6000, allSloping: false);

        roof.SetShape(RoofForm.Hip);
        Assert.Equal(RoofForm.Hip, roof.Form);
        Assert.All(roof.Edges, edge => Assert.True(edge.DefinesSlope));

        // A gable slopes the long sides, so the ridge runs the length of the building.
        roof.SetShape(RoofForm.Gable);
        Assert.Equal(RoofForm.Gable, roof.Form);
        Assert.True(roof.Edges[0].DefinesSlope);
        Assert.False(roof.Edges[1].DefinesSlope);
        Assert.True(roof.Edges[2].DefinesSlope);
        Assert.False(roof.Edges[3].DefinesSlope);

        roof.SetShape(RoofForm.Shed);
        Assert.Equal(RoofForm.Shed, roof.Form);
        Assert.Equal(1, roof.Edges.Count(edge => edge.DefinesSlope));

        roof.SetShape(RoofForm.Flat);
        Assert.Equal(RoofForm.Flat, roof.Form);
        Assert.All(roof.Edges, edge => Assert.False(edge.DefinesSlope));
    }

    [Fact]
    public void ThePitchIsSetForEveryEdgeAtOnce()
    {
        var roof = Rectangle(8000, 8000, pitch: 25);
        Assert.Equal(25, roof.SlopeDegrees, precision: 6);

        roof.SlopeDegrees = 40;
        Assert.All(roof.Edges, edge => Assert.Equal(40, edge.SlopeDegrees, precision: 6));

        // A flat roof given a pitch becomes a pitched one, rather than storing a number that
        // nothing is pitched at.
        roof.SetShape(RoofForm.Flat);
        Assert.Equal(0, roof.SlopeDegrees, precision: 6);

        roof.SlopeDegrees = 35;
        Assert.All(roof.Edges, edge => Assert.True(edge.DefinesSlope));
        Assert.Equal(RoofForm.Hip, roof.Form);
    }

    [Fact]
    public void AnEdgePerBoundarySegmentIsKept()
    {
        var roof = Rectangle(6000, 6000);
        Assert.Equal(4, roof.Edges.Count);

        roof.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 6000),
            new Point2D(3000, 9000), new Point2D(0, 6000)
        });

        Assert.Equal(5, roof.Edges.Count);

        // The edge that has just appeared does not slope: a roof that grew an edge should not
        // silently grow a slope with it.
        Assert.False(roof.Edges[4].DefinesSlope);
    }

    [Fact]
    public void ThePitchedRoofReportsItsRealSurfaceAndVolume()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<SlabType>().First();
        var roof = Rectangle(10000, 6000, pitch: 45);
        roof.TypeId = type.Id;
        roof.LevelId = document.Levels[0].Id;
        roof.HeightOffset = 3000;
        document.Add(roof);

        Assert.Equal(60e6, roof.Area, precision: 0);
        Assert.Equal(60e6 * Math.Sqrt(2), roof.SlopingArea(document), precision: 0);
        Assert.Equal(roof.SlopingArea(document) * type.Thickness, roof.GetVolume(document), precision: 0);

        // The underside rises 3 m in from the long sides, at 45°, from a roof based 3 m up;
        // the ridge proper is the top of the build-up over that, measured square to the slope.
        Assert.Equal(6000, roof.Surface(document).RidgeElevation, precision: 3);
        Assert.Equal(6000 + type.Thickness * Math.Sqrt(2), roof.RidgeElevation(document), precision: 3);
    }

    // ---- what the rest of the model does with it ---------------------------------

    private static (BimDocument Document, Roof Roof) Placed(RoofForm form, double pitch = 30)
    {
        var document = BimDocument.CreateDefault();
        var roof = Rectangle(10000, 6000, allSloping: false, pitch: pitch);

        roof.TypeId = document.TypesOf<RoofType>().First().Id;
        roof.LevelId = document.Levels[0].Id;
        roof.HeightOffset = 3000;
        roof.SlopeDegrees = pitch;
        roof.SetShape(form);
        document.Add(roof);

        return (document, roof);
    }

    [Fact]
    public void ThePitchedRoofIsBuiltAsFacesInTheirOwnPlanes()
    {
        var (document, roof) = Placed(RoofForm.Gable, pitch: 45);
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == roof.Id).ToList();

        Assert.NotEmpty(meshes);

        var bounds = ModelMeshBuilder.Bounds(meshes)!.Value;
        var thickness = document.FindType<SlabType>(roof.TypeId)!.Thickness;

        // The underside rises 3 m in from a long side at 45°, from a roof based 3 m up, and
        // the build-up stands on it, square to the slope - so the ridge is the thickness
        // stretched by the pitch above that.
        Assert.Equal(6000 + thickness * Math.Sqrt(2), bounds.Max.Z, precision: 3);

        // And its lowest point is the underside at the eaves: the base, which is where it
        // sits on its walls.
        Assert.Equal(3000, bounds.Min.Z, precision: 3);

        // It is not a box: a flat roof of the same outline would have a level top.
        Assert.True(bounds.Max.Z - bounds.Min.Z > 2000, "The roof came out flat.");
    }

    [Fact]
    public void ASectionThroughAPitchedRoofSlopes()
    {
        var (document, roof) = Placed(RoofForm.Gable);

        // Across the building, so the cut crosses both slopes and the ridge between them.
        var marker = new SectionMarker
        {
            Name = "A",
            Start = new Point2D(5000, -2000),
            End = new Point2D(5000, 8000),
            LevelId = document.Levels[0].Id,
            ViewDepth = 20000
        };

        document.Add(marker);

        var pieces = SectionProjection.Build(document, marker).Pieces
            .Where(piece => piece.ElementId == roof.Id)
            .ToList();

        Assert.NotEmpty(pieces);

        // Each piece is cut as the sloping thing it is, not as an upright rectangle.
        Assert.All(pieces, piece => Assert.NotNull(piece.Shape));

        // The two slopes meet at the ridge, which is the top of the cut: the underside's rise
        // plus the build-up standing on it.
        var rise = 3000 * Math.Tan(30 * Math.PI / 180);
        var build = document.FindType<SlabType>(roof.TypeId)!.Thickness / Math.Cos(30 * Math.PI / 180);
        Assert.Equal(3000 + rise + build, pieces.Max(piece => piece.Bounds.Top), precision: 1);
    }

    [Fact]
    public void ASavedRoofComesBackWithItsSlopes()
    {
        var (document, roof) = Placed(RoofForm.Gable, pitch: 35);
        roof.Edges[0].PlateOffset = 250;

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single();

        Assert.Equal(RoofForm.Gable, reloaded.Form);
        Assert.Equal(35, reloaded.SlopeDegrees, precision: 6);
        Assert.Equal(250, reloaded.Edges[0].PlateOffset, precision: 6);
        Assert.Equal(
            roof.Edges.Select(edge => edge.DefinesSlope),
            reloaded.Edges.Select(edge => edge.DefinesSlope));
    }

    [Fact]
    public void ARoofFromBeforePitchedRoofsReadsBackAsTheFlatOneItWas()
    {
        var (document, roof) = Placed(RoofForm.Hip);

        // A file written before roofs could be pitched has no edges in it at all.
        var json = ProjectFile.ToJson(document);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            json, "\"roofEdges\":\\s*\\[[^\\]]*\\]", "\"roofEdges\": []");

        var reloaded = ProjectFile.FromJson(stripped).Elements.OfType<Roof>().Single();

        Assert.Equal(RoofForm.Flat, reloaded.Form);
        Assert.Equal(roof.Boundary.Count, reloaded.Edges.Count);
    }

    [Fact]
    public void ACopiedRoofIsTheSameRoof()
    {
        var (_, roof) = Placed(RoofForm.Shed, pitch: 15);
        var copy = (Roof)ElementCopy.Clone(roof)!;

        Assert.Equal(RoofForm.Shed, copy.Form);
        Assert.Equal(15, copy.SlopeDegrees, precision: 6);
        Assert.Equal(
            roof.Edges.Select(edge => edge.DefinesSlope),
            copy.Edges.Select(edge => edge.DefinesSlope));
    }

    [Fact]
    public void TheTakeoffCountsTheSlopingSurfaceNotTheFootprint()
    {
        var (document, roof) = Placed(RoofForm.Gable, pitch: 45);

        var lines = MaterialTakeoff.Lines(document).Where(line => line.Category == "Roofs").ToList();
        Assert.NotEmpty(lines);

        // A 45° roof is √2 times its footprint, and that is what the tiles are bought by.
        Assert.All(lines, line => Assert.Equal(60e6 * Math.Sqrt(2), line.Area, precision: 0));
    }

    [Fact]
    public void ChangingAnEdgeIsOneThingToUndo()
    {
        var (_, roof) = Placed(RoofForm.Hip);

        var edges = roof.Edges.Select(edge => edge.Copy()).ToList();
        edges[1].DefinesSlope = false;
        edges[3].DefinesSlope = false;

        var command = new SetRoofEdgesCommand(roof, edges, "Flatten Roof Edge");
        command.Redo();

        Assert.Equal(RoofForm.Gable, roof.Form);

        command.Undo();
        Assert.Equal(RoofForm.Hip, roof.Form);
    }
}
