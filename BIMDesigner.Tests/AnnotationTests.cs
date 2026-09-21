using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Annotation has one job: to say something true about the model. These are about it staying
/// true when the model moves - a dimension that disagrees with what it dimensions is the most
/// expensive kind of error a drawing set can carry onto a site.
/// </summary>
public class AnnotationTests
{
    private static (BimDocument Document, Wall Wall, Guid LevelId) Project()
    {
        var document = BimDocument.CreateDefault();
        var levelId = document.Levels.First().Id;

        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(5000, 0),
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = levelId,
            Mark = "W1"
        };

        document.Add(wall);
        return (document, wall, levelId);
    }

    // ---- dimensions -------------------------------------------------------------

    [Fact]
    public void ADimensionMeasuresTheModel()
    {
        var (document, wall, levelId) = Project();

        var dimension = new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            LevelId = levelId
        };

        document.Add(dimension);

        Assert.Equal(5000, dimension.Measure(document), precision: 3);
        Assert.Equal("5.00 m", dimension.DisplayText(document));
    }

    /// <summary>The whole point: move the wall and the number moves with it.</summary>
    [Fact]
    public void MovingTheWallChangesTheDimension()
    {
        var (document, wall, levelId) = Project();

        var dimension = new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            LevelId = levelId
        };

        document.Add(dimension);
        wall.End = new Point2D(8000, 0);

        Assert.Equal(8000, dimension.Measure(document), precision: 3);
        Assert.True(dimension.IsAssociative(document));
    }

    [Fact]
    public void ADimensionBetweenBarePointsDoesNotFollowAnything()
    {
        var (document, _, levelId) = Project();

        var dimension = new Dimension
        {
            Start = DimensionReference.ToPoint(new Point2D(0, 0)),
            End = DimensionReference.ToPoint(new Point2D(3000, 0)),
            LevelId = levelId
        };

        document.Add(dimension);

        Assert.Equal(3000, dimension.Measure(document), precision: 3);
        Assert.False(dimension.IsAssociative(document));
    }

    /// <summary>
    /// A deleted reference must not take the dimension with it, nor throw. It falls back to
    /// where it last was and reports that it is no longer attached, which is what a model
    /// health check needs to see.
    /// </summary>
    [Fact]
    public void ADimensionSurvivesItsReferenceBeingDeleted()
    {
        var (document, wall, levelId) = Project();

        var dimension = new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            LevelId = levelId
        };

        document.Add(dimension);
        document.Remove(wall);

        Assert.Equal(5000, dimension.Measure(document), precision: 3);
        Assert.False(dimension.IsAssociative(document));
    }

    [Fact]
    public void ADimensionCanFollowAGrid()
    {
        var (document, _, levelId) = Project();

        var grid = new Grid { Name = "1", Start = new Point2D(2000, -5000), End = new Point2D(2000, 5000) };
        document.Add(grid);

        var dimension = new Dimension
        {
            Start = DimensionReference.ToPoint(new Point2D(0, 0)),
            End = DimensionReference.ToGrid(grid, new Point2D(2000, 0)),
            LevelId = levelId
        };

        Assert.Equal(2000, dimension.Measure(document), precision: 3);

        // Move the grid and the setting-out dimension moves with it.
        grid.Start = new Point2D(3500, -5000);
        grid.End = new Point2D(3500, 5000);

        Assert.Equal(3500, dimension.Measure(document), precision: 3);
    }

    [Fact]
    public void AnOverrideReplacesTheTextButNotTheMeasurement()
    {
        var (document, wall, levelId) = Project();

        var dimension = new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            Override = "VARIES",
            LevelId = levelId
        };

        Assert.Equal("VARIES", dimension.DisplayText(document));
        Assert.Equal(5000, dimension.Measure(document), precision: 3);
    }

    [Fact]
    public void TheDimensionLineSitsOffWhatItMeasures()
    {
        var (document, wall, levelId) = Project();

        var dimension = new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            Offset = 800,
            LevelId = levelId
        };

        var (from, to) = dimension.GetDimensionLine(document);

        // The wall runs along +X, so left of it is +Y.
        Assert.Equal(800, from.Y, precision: 3);
        Assert.Equal(800, to.Y, precision: 3);
        Assert.Equal(5000, from.DistanceTo(to), precision: 3);
    }

    // ---- tags --------------------------------------------------------------------

    [Fact]
    public void ATagReadsItsValueFromTheElement()
    {
        var (document, wall, levelId) = Project();

        var tag = new Tag { TargetId = wall.Id, Field = "Mark", LevelId = levelId };
        document.Add(tag);

        Assert.Equal("W1", tag.Read(document));

        // Rename the wall and the label follows - a tag holds no text of its own.
        wall.Mark = "W-07";
        Assert.Equal("W-07", tag.Read(document));
    }

    [Fact]
    public void ATagCanReadATypeParameterToo()
    {
        var (document, wall, levelId) = Project();

        var tag = new Tag { TargetId = wall.Id, Field = "Type Name", LevelId = levelId };

        Assert.Equal(document.GetWallType(wall)!.Name, tag.Read(document));
    }

    [Fact]
    public void ATagOnADeletedElementSaysSo()
    {
        var (document, wall, levelId) = Project();

        var tag = new Tag { TargetId = wall.Id, Field = "Mark", LevelId = levelId };
        document.Add(tag);
        document.Remove(wall);

        Assert.Equal("<deleted>", tag.Read(document));
    }

    [Fact]
    public void AnEmptyParameterTagsAsAQuestionMarkNotAsNothing()
    {
        var (document, wall, levelId) = Project();
        wall.Mark = string.Empty;

        var tag = new Tag { TargetId = wall.Id, Field = "Mark", LevelId = levelId };

        // A blank tag looks like one nobody filled in, which is what it is.
        Assert.Equal("?", tag.Read(document));
    }

    [Fact]
    public void ATagOffersOnlyFieldsItsElementHas()
    {
        var (document, wall, levelId) = Project();

        var tag = new Tag { TargetId = wall.Id, Field = "Mark", LevelId = levelId };
        document.Add(tag);

        var field = tag.GetInstanceParameters(document).Single(p => p.Name == "Field");

        Assert.NotNull(field.AllowedValues);
        Assert.Contains("Length", field.AllowedValues!);
        Assert.DoesNotContain("Sill Height", field.AllowedValues!);   // that is a door's
    }

    // ---- persistence -------------------------------------------------------------

    [Fact]
    public void AnnotationSurvivesSavingAndReopening()
    {
        var (document, wall, levelId) = Project();

        var dimension = new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            Offset = 750,
            LevelId = levelId
        };

        document.Add(dimension);
        document.Add(new Tag { TargetId = wall.Id, Field = "Type Name", LevelId = levelId });
        document.Add(new TextNote
        {
            Text = "Verify on site",
            Position = new Point2D(1000, 2000),
            LeaderEnd = new Point2D(500, 500),
            LevelId = levelId
        });

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        var reloadedDimension = reloaded.Elements.OfType<Dimension>().Single();
        Assert.Equal(750, reloadedDimension.Offset, precision: 3);
        Assert.Equal(5000, reloadedDimension.Measure(reloaded), precision: 3);

        // The reference survives, so the reopened dimension still follows the wall.
        Assert.True(reloadedDimension.IsAssociative(reloaded));

        Assert.Equal("Generic - 200mm", reloaded.Elements.OfType<Tag>().Single().Read(reloaded));

        var note = reloaded.Elements.OfType<TextNote>().Single();
        Assert.Equal("Verify on site", note.Text);
        Assert.Equal(new Point2D(500, 500), note.LeaderEnd);
    }

    [Fact]
    public void AReopenedDimensionStillMovesWithItsWall()
    {
        var (document, wall, levelId) = Project();

        document.Add(new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            LevelId = levelId
        });

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var reloadedWall = reloaded.Walls.Single();
        var reloadedDimension = reloaded.Elements.OfType<Dimension>().Single();

        reloadedWall.End = new Point2D(7500, 0);

        Assert.Equal(7500, reloadedDimension.Measure(reloaded), precision: 3);
    }
}
