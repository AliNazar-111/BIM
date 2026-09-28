using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Openings drawn in a roof's sketch (Revit: a loop inside the outline is an opening): a
/// rectangle, polygon or circle drawn inside the roof's outline is a hole cut straight through
/// it - a skylight, a chimney.
/// </summary>
public class RoofOpeningTests
{
    private static List<RoofSketchLine> Loop(IReadOnlyList<Point2D> corners, bool sloping)
    {
        var lines = new List<RoofSketchLine>();
        for (var i = 0; i < corners.Count; i++)
            lines.Add(new RoofSketchLine(corners[i], corners[(i + 1) % corners.Count],
                new RoofEdge { DefinesSlope = sloping, SlopeDegrees = 30 }));

        return lines;
    }

    private static readonly Point2D[] Outline = { new(0, 0), new(10000, 0), new(10000, 6000), new(0, 6000) };
    private static readonly Point2D[] Skylight = { new(4000, 1000), new(5000, 1000), new(5000, 2000), new(4000, 2000) };

    private static (BimDocument Document, Roof Roof) Made(List<RoofSketchLine> sketch)
    {
        var check = RoofSketch.Check(sketch);
        Assert.True(check.IsValid, check.Problem);

        var document = BimDocument.CreateDefault();
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[0].Id, HeightOffset = 3000 };
        roof.SetBoundary(check.Boundary);
        roof.SetEdges(check.Edges);
        roof.SetOpenings(check.Openings);
        document.Add(roof);
        return (document, roof);
    }

    [Fact]
    public void ALoopInsideTheOutlineIsAnOpening()
    {
        var sketch = Loop(Outline, sloping: true);
        sketch.AddRange(Loop(Skylight, sloping: false));

        var check = RoofSketch.Check(sketch);
        Assert.True(check.IsValid, check.Problem);
        Assert.Equal(4, check.Boundary.Count);
        var opening = Assert.Single(check.Openings);
        Assert.Equal(1e6, Polygon2D.Area(opening.Points), precision: 0);
    }

    [Fact]
    public void TheOpeningIsCutThroughTheRoof()
    {
        var sketch = Loop(Outline, sloping: true);
        sketch.AddRange(Loop(Skylight, sloping: false));
        var (document, roof) = Made(sketch);

        var whole = Made(Loop(Outline, sloping: true));
        var before = whole.Roof.SlopingArea(whole.Document);

        // No piece of the roof is over the middle of the skylight.
        Assert.DoesNotContain(RoofSolid.Pieces(document, roof), piece => Polygon2D.Contains(piece.Outline, new Point2D(4500, 1500)));

        // Its covering is less by the hole, measured on the slope.
        var stretch = 1 / Math.Cos(Math.PI / 6);
        Assert.Equal(before - 1e6 * stretch, roof.SlopingArea(document), 1e6 * 0.01);
    }

    [Fact]
    public void ARoundOpeningOpensAgainAsACircle()
    {
        var sketch = Loop(Outline, sloping: true);
        sketch.AddRange(WallShapes.Circle(new Point2D(5000, 3000), new Point2D(5600, 3000))
            .Select(piece => new RoofSketchLine(piece.Start, piece.End, new RoofEdge(), piece.Bulge)));

        var (_, roof) = Made(sketch);
        Assert.Single(roof.Openings);

        var lines = RoofSketch.LinesOf(roof);
        Assert.Equal(6, lines.Count);
        Assert.Equal(2, lines.Count(line => line.IsArc));
    }

    [Fact]
    public void ALoopOutsideTheOutlineIsRefused()
    {
        var sketch = Loop(Outline, sloping: true);
        sketch.AddRange(Loop(new Point2D[] { new(12000, 0), new(14000, 0), new(14000, 2000), new(12000, 2000) }, sloping: true));

        var check = RoofSketch.Check(sketch);
        Assert.False(check.IsValid);
        Assert.Contains("separate", check.Problem);
    }

    [Fact]
    public void OpeningsMoveSaveAndCopyWithTheRoof()
    {
        var sketch = Loop(Outline, sloping: true);
        sketch.AddRange(Loop(Skylight, sloping: false));
        var (document, roof) = Made(sketch);

        ElementTransforms.Move(roof, new Vector2D(1000, 0));
        Assert.Equal(5000, roof.Openings[0].Points.Min(point => point.X), precision: 6);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single();
        Assert.Single(reloaded.Openings);
        Assert.Equal(1e6, Polygon2D.Area(reloaded.Openings[0].Points), precision: 0);

        Assert.Single(((Roof)ElementCopy.Clone(roof)!).Openings);

        // Edit Footprint shows the outline and the opening.
        Assert.Equal(8, RoofSketch.LinesOf(roof).Count);
    }
}
