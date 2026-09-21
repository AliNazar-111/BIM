using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

public class GridTests
{
    private static Grid AddGrid(BimDocument document, Point2D start, Point2D end)
    {
        var grid = new Grid { Start = start, End = end };
        grid.Name = Grids.NextName(document, grid.IsVertical);
        document.Add(grid);
        return grid;
    }

    [Fact]
    public void GridsRunningUpThePageAreNumbered()
    {
        var document = BimDocument.CreateDefault();

        var first = AddGrid(document, new Point2D(0, 0), new Point2D(0, 10000));
        var second = AddGrid(document, new Point2D(5000, 0), new Point2D(5000, 10000));

        Assert.True(first.IsVertical);
        Assert.Equal("1", first.Name);
        Assert.Equal("2", second.Name);
    }

    [Fact]
    public void GridsRunningAcrossThePageAreLettered()
    {
        var document = BimDocument.CreateDefault();

        var first = AddGrid(document, new Point2D(0, 0), new Point2D(10000, 0));
        var second = AddGrid(document, new Point2D(0, 5000), new Point2D(10000, 5000));

        Assert.False(first.IsVertical);
        Assert.Equal("A", first.Name);
        Assert.Equal("B", second.Name);
    }

    [Fact]
    public void LettersAndNumbersAreCountedSeparately()
    {
        var document = BimDocument.CreateDefault();

        AddGrid(document, new Point2D(0, 0), new Point2D(10000, 0));       // A
        AddGrid(document, new Point2D(0, 0), new Point2D(0, 10000));       // 1
        var thirdAcross = AddGrid(document, new Point2D(0, 3000), new Point2D(10000, 3000));
        var thirdUp = AddGrid(document, new Point2D(3000, 0), new Point2D(3000, 10000));

        Assert.Equal("B", thirdAcross.Name);
        Assert.Equal("2", thirdUp.Name);
    }

    [Fact]
    public void NamingRunsPastZIntoTwoLetters()
    {
        var document = BimDocument.CreateDefault();

        for (var i = 0; i < 26; i++)
            AddGrid(document, new Point2D(0, i * 1000), new Point2D(10000, i * 1000));

        var next = AddGrid(document, new Point2D(0, 99000), new Point2D(10000, 99000));

        Assert.Equal("AA", next.Name);
    }

    /// <summary>Where two grids cross is where a column is set out.</summary>
    [Fact]
    public void CrossingGridsGiveSettingOutPoints()
    {
        var document = BimDocument.CreateDefault();

        AddGrid(document, new Point2D(0, 0), new Point2D(0, 10000));        // grid 1
        AddGrid(document, new Point2D(6000, 0), new Point2D(6000, 10000));  // grid 2
        AddGrid(document, new Point2D(-2000, 4000), new Point2D(9000, 4000)); // grid A

        var crossings = Grids.Intersections(document).ToList();

        Assert.Contains(crossings, p => p.DistanceTo(new Point2D(0, 4000)) < 1e-6);
        Assert.Contains(crossings, p => p.DistanceTo(new Point2D(6000, 4000)) < 1e-6);

        // The two parallel grids never meet, so only two crossings exist.
        Assert.Equal(2, crossings.Count);
    }

    [Fact]
    public void ParallelGridsProduceNoCrossings()
    {
        var document = BimDocument.CreateDefault();

        AddGrid(document, new Point2D(0, 0), new Point2D(0, 10000));
        AddGrid(document, new Point2D(6000, 0), new Point2D(6000, 10000));

        Assert.Empty(Grids.Intersections(document));
    }

    /// <summary>
    /// A grid belongs to the building, not to a storey, so it carries no level at all - that
    /// is what lets every plan be set out against the same lines.
    /// </summary>
    [Fact]
    public void AGridBelongsToNoLevel()
    {
        var document = BimDocument.CreateDefault();
        var grid = AddGrid(document, new Point2D(0, 0), new Point2D(0, 10000));

        Assert.Equal(Guid.Empty, grid.LevelId);
    }

    [Fact]
    public void GridsSurviveSavingAndReopening()
    {
        var document = BimDocument.CreateDefault();

        var across = AddGrid(document, new Point2D(-2000, 4000), new Point2D(9000, 4000));
        across.BubbleAtEnd = false;
        AddGrid(document, new Point2D(0, 0), new Point2D(0, 10000));

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var grids = reloaded.Elements.OfType<Grid>().ToList();

        Assert.Equal(2, grids.Count);

        var reloadedAcross = grids.Single(g => g.Id == across.Id);
        Assert.Equal("A", reloadedAcross.Name);
        Assert.Equal(across.Start, reloadedAcross.Start);
        Assert.Equal(across.End, reloadedAcross.End);
        Assert.True(reloadedAcross.BubbleAtStart);
        Assert.False(reloadedAcross.BubbleAtEnd);
    }
}
