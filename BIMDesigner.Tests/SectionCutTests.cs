using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// Sections cut through what is built only as a solid - a chimney with its flues and
/// fireplaces, furniture, a downpipe, a wall's studs - as it is built, and show the grid lines
/// the cut crosses.
/// </summary>
public class SectionCutTests
{
    private static bool Covered(SectionDrawing drawing, Guid elementId, double along, double height) =>
        drawing.Pieces.Where(piece => piece.ElementId == elementId && piece.Depth == SectionDepth.Cut)
            .Any(piece => piece.Shape is { } shape
                ? Polygon2D.Contains(shape.Select(point => new Point2D(point.X, point.Y)).ToList(), new Point2D(along, height))
                : along > piece.Bounds.Left && along < piece.Bounds.Right && height > piece.Bounds.Bottom && height < piece.Bounds.Top);

    [Fact]
    public void ACutAlongATwoStoreyChimneyShowsAFlueForEachFireplaceWithBrickBetween()
    {
        var (document, chimney, first) = ChimneyFloorsTestsHouse();
        SetChimneyFireplaceCommand.For(document, chimney, first.Id, ChimneyFireplace.Open, out _)!.Redo();

        // Along the stack, through the middle of its flues.
        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        var marker = new SectionMarker { Start = chimney.Location - along * 3000, End = chimney.Location + along * 3000, LevelId = chimney.LevelId };
        var drawing = SectionProjection.Build(document, marker);

        var top = Chimneys.Top(document, chimney);
        var flues = Chimneys.Flues(document, chimney).Select(marker.DistanceAlong).ToList();
        Assert.Equal(2, flues.Count);

        // Near the top, open down each flue, brick between and either side.
        var high = top - 300;
        Assert.All(flues, flue => Assert.False(Covered(drawing, chimney.Id, flue, high), "A flue is solid in the section."));
        Assert.True(Covered(drawing, chimney.Id, (flues[0] + flues[1]) / 2, high), "No brick between the flues.");
        Assert.True(Covered(drawing, chimney.Id, flues[0] - Chimneys.LinerWidth(document, chimney) / 2 - 50, high));
    }

    [Fact]
    public void ACutThroughTheBreastFrontToBackOpensIntoTheFireplace()
    {
        var (document, chimney, _) = ChimneyFloorsTestsHouse();
        var front = Chimneys.Front(chimney);
        var marker = new SectionMarker { Start = chimney.Location - front * 2000, End = chimney.Location + front * 2000, LevelId = chimney.LevelId };
        var drawing = SectionProjection.Build(document, marker);

        var breast = Chimneys.Breast(document, chimney)!;
        var foot = Chimneys.Foot(document, chimney);
        var opening = marker.DistanceAlong(new Point2D(breast.Opening.Average(p => p.X), breast.Opening.Average(p => p.Y)));

        // The opening is open, the brick over it solid; the hearth before it is cut too.
        Assert.False(Covered(drawing, chimney.Id, opening, foot + breast.OpeningHeight / 2), "The fireplace is bricked up in section.");
        Assert.Contains(drawing.Pieces, piece => piece.ElementId == chimney.Id && piece.Description == "Hearth");
        Assert.Contains(drawing.Pieces, piece => piece.ElementId == chimney.Id && piece.Description == "Damper");
    }

    [Fact]
    public void FurnitureAndADownpipeTheCutPassesThroughAreCut()
    {
        var document = BimDocument.CreateDefault();
        var chair = new Component { TypeId = document.TypesOf<ComponentType>().First(t => !t.IsHosted).Id, LevelId = document.Levels[0].Id, Location = new Point2D(2000, 2000) };
        document.Add(chair);

        var marker = new SectionMarker { Start = new Point2D(0, 2000), End = new Point2D(4000, 2000), LevelId = document.Levels[0].Id };
        var drawing = SectionProjection.Build(document, marker);
        Assert.Contains(drawing.Pieces, piece => piece.ElementId == chair.Id && piece.Depth == SectionDepth.Cut);

        var (gutterDocument, roof, gutter, _) = GutterPartsTestsGuttered();
        var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = new Point2D(5000, -400) };
        gutterDocument.Add(pipe);
        var foot = Downpipes.Path(gutterDocument, pipe)!.Foot;
        var across = new SectionMarker { Start = foot + new Vector2D(-1000, 0), End = foot + new Vector2D(1000, 0), LevelId = roof.LevelId };
        Assert.Contains(SectionProjection.Build(gutterDocument, across).Pieces, piece => piece.ElementId == pipe.Id);
    }

    [Fact]
    public void AFramedWallIsCutThroughItsFrameNotItsCavity()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior - Partition"));
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(3600, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2400 };
        document.Add(wall);
        var framing = new WallFraming { HostId = wall.Id, LevelId = wall.LevelId, Section = "38 x 89 (2x4)", Spacing = 600, TopPlates = 2, NoggingRows = 1 };
        document.Add(framing);

        // Across the wall through a stud: the plasterboard either side, the stud between.
        var marker = new SectionMarker { Start = new Point2D(1800, -1000), End = new Point2D(1800, 1000), LevelId = wall.LevelId };
        var drawing = SectionProjection.Build(document, marker);
        Assert.DoesNotContain(drawing.Pieces, piece => piece.ElementId == wall.Id && piece.Description == "Air Cavity");
        Assert.Contains(drawing.Pieces, piece => piece.ElementId == framing.Id);
    }

    [Fact]
    public void TheGridLinesTheCutCrossesStandUpThroughIt()
    {
        var document = BimDocument.CreateDefault();
        document.Add(new Grid { Name = "A", Start = new Point2D(1500, -5000), End = new Point2D(1500, 5000), LevelId = document.Levels[0].Id });
        document.Add(new Grid { Name = "B", Start = new Point2D(6500, -5000), End = new Point2D(6500, 5000), LevelId = document.Levels[0].Id });
        document.Add(new Grid { Name = "C", Start = new Point2D(-5000, 3000), End = new Point2D(5000, 3000), LevelId = document.Levels[0].Id });

        var marker = new SectionMarker { Start = new Point2D(0, 0), End = new Point2D(8000, 0), LevelId = document.Levels[0].Id };
        var drawing = SectionProjection.Build(document, marker);
        Assert.Equal(new[] { ("A", 1500.0), ("B", 6500.0) }, drawing.Grids.Select(grid => (grid.Name, Math.Round(grid.Along, 6))));
    }

    private static (BimDocument, Chimney, Level) ChimneyFloorsTestsHouse() =>
        ((BimDocument, Chimney, Level))typeof(ChimneyFloorsTests).GetMethod("House", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;

    private static (BimDocument, Roof, Gutter, GutterType) GutterPartsTestsGuttered() =>
        ((BimDocument, Roof, Gutter, GutterType))typeof(GutterPartsTests).GetMethod("Guttered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { "Gutter - K-Style 125, Aluminium White", false })!;
}
