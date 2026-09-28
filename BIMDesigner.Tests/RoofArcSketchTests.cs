using System.Diagnostics;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Arcs and circles in a roof sketch (Revit's arc and circle boundary lines): a sloping circle
/// makes a cone, a sloping arc a rounded end - one curved surface each, built from straight
/// pieces, drawn and shaded as one, and given back as arcs when the sketch is opened again.
/// </summary>
public class RoofArcSketchTests
{
    private static RoofEdge Sloping(double degrees = 30) => new() { DefinesSlope = true, SlopeDegrees = degrees };

    /// <summary>A circle about the origin, as the circle tool draws it: two half circles.</summary>
    private static List<RoofSketchLine> Circle(double radius, bool sloping = true) =>
        WallShapes.Circle(new Point2D(0, 0), new Point2D(radius, 0))
            .Select(piece => new RoofSketchLine(piece.Start, piece.End, sloping ? Sloping() : new RoofEdge(), piece.Bulge))
            .ToList();

    private static (BimDocument Document, Roof Roof) Made(IReadOnlyList<RoofSketchLine> sketch)
    {
        var check = RoofSketch.Check(sketch);
        Assert.True(check.IsValid, check.Problem);

        var document = BimDocument.CreateDefault();
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[0].Id, HeightOffset = 3000 };
        roof.SetBoundary(check.Boundary);
        roof.SetEdges(check.Edges);
        document.Add(roof);
        return (document, roof);
    }

    [Fact]
    public void ASlopingCircleMakesACone()
    {
        var (document, roof) = Made(Circle(5000));
        var surface = roof.Surface(document);

        Assert.Equal(RoofForm.Conical, roof.Form);

        // Round in plan, near enough to the circle drawn.
        Assert.Equal(Math.PI * 5000 * 5000, roof.Area, 5000 * 5000 * 0.02);

        // Up to a point over the middle: the circle's radius times the pitch, a little less for
        // the straight pieces it is built from.
        var apex = surface.HeightAt(new Point2D(0, 0));
        Assert.InRange(apex - 3000, 5000 * Math.Tan(Math.PI / 6) * 0.98, 5000 * Math.Tan(Math.PI / 6));

        // One surface: no hip drawn down every join between its pieces.
        Assert.Empty(surface.BreakLines);
    }

    [Fact]
    public void AConeIsShadedAsOneSurface()
    {
        var (document, roof) = Made(Circle(5000));
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == roof.Id).ToList();

        Assert.NotEmpty(meshes);

        // Its top is shaded as one curve: where two of its faces meet on the rim, both are
        // shaded facing the same way there, so no band shows between them.
        foreach (var mesh in meshes)
        {
            Assert.NotNull(mesh.Normals);

            var up = Enumerable.Range(0, mesh.TriangleCount)
                .SelectMany(t => Enumerable.Range(0, 3).Select(k => mesh.Indices[3 * t + k]))
                .Where(index => mesh.Normals![index].Z > 0.5)
                .GroupBy(index => (Math.Round(mesh.Positions[index].X), Math.Round(mesh.Positions[index].Y), Math.Round(mesh.Positions[index].Z)))
                .Where(at => at.Count() == 2)
                .ToList();

            Assert.NotEmpty(up);
            Assert.All(up, at =>
            {
                var (a, b) = (mesh.Normals![at.First()], mesh.Normals![at.Last()]);
                Assert.True(a.X * b.X + a.Y * b.Y + a.Z * b.Z > 0.9999, "Two faces of the cone are shaded differently where they meet.");
            });
        }
    }

    [Fact]
    public void AConeIsQuickToWorkOut()
    {
        var clock = Stopwatch.StartNew();
        var (document, roof) = Made(Circle(8000));
        _ = roof.Surface(document);
        _ = ModelMeshBuilder.Build(document);

        Assert.True(clock.ElapsedMilliseconds < 3000, $"A cone took {clock.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void AnArcOpensAgainAsTheArcItWas()
    {
        var (_, roof) = Made(Circle(5000));
        var lines = RoofSketch.LinesOf(roof);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.True(line.IsArc));
        Assert.All(lines, line => Assert.Equal(1, Math.Abs(line.Bulge), precision: 6));
        Assert.All(lines, line => Assert.True(line.Edge.DefinesSlope));

        // And finishing it again makes the same roof.
        var again = RoofSketch.Check(lines);
        Assert.True(again.IsValid);
        Assert.Equal(roof.Boundary.Count, again.Boundary.Count);
    }

    [Fact]
    public void ARoundEndOnAGableRoof()
    {
        // A 6 m wide wing, 8 m long, ending in a half circle: the straight sides slope, the
        // back is a gable, and the round end slopes all round - an apse.
        var sketch = new List<RoofSketchLine>
        {
            new(new Point2D(0, 0), new Point2D(8000, 0), Sloping()),
            new(new Point2D(8000, 0), new Point2D(8000, 6000), Sloping(), bulge: 1),
            new(new Point2D(8000, 6000), new Point2D(0, 6000), Sloping()),
            new(new Point2D(0, 6000), new Point2D(0, 0), new RoofEdge())
        };

        var (document, roof) = Made(sketch);
        var surface = roof.Surface(document);

        // The ridge runs along the wing at half its width up, and the round end falls from it
        // at the same pitch all round.
        Assert.Equal(3000 + 3000 * Math.Tan(Math.PI / 6), surface.HeightAt(new Point2D(4000, 3000)), 1);
        Assert.InRange(surface.HeightAt(new Point2D(10500, 3000)), 3000, 3000 + 600 * Math.Tan(Math.PI / 6));

        // The ridge is drawn; the joins round the curve are not.
        Assert.True(surface.BreakLines.Count <= 4, $"{surface.BreakLines.Count} lines drawn on an apse roof.");
        Assert.Equal(4, RoofSketch.LinesOf(roof).Count);
    }

    [Fact]
    public void AnArcCrossingALineIsRefused()
    {
        var sketch = new List<RoofSketchLine>
        {
            new(new Point2D(0, 0), new Point2D(6000, 0), Sloping()),
            new(new Point2D(6000, 0), new Point2D(6000, 6000), Sloping()),
            new(new Point2D(6000, 6000), new Point2D(0, 6000), Sloping()),

            // Bowed into the square, past a half circle, so far that it swings out through the bottom line.
            new(new Point2D(0, 6000), new Point2D(0, 0), Sloping(), bulge: -1.5)
        };

        var check = RoofSketch.Check(sketch);
        Assert.False(check.IsValid);
    }

    [Fact]
    public void ArcsAreSavedWithTheRoof()
    {
        var (document, _) = Made(Circle(4000));

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single();
        Assert.Equal(RoofForm.Conical, reloaded.Form);
        Assert.Equal(2, RoofSketch.LinesOf(reloaded).Count);
    }
}
