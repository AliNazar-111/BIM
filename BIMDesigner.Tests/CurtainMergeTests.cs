using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Two curtain walls running on from each other in a straight line made one: the joint between
/// them goes, and what each had of its own - grid lines, a door, tinted glass - stays where it was.
/// </summary>
public class CurtainMergeTests
{
    private static (BimDocument Document, Wall First, Wall Second) TwoInLine()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Glazed")).Id;

        var first = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), TypeId = type, LevelId = level, UnconnectedHeight = 3000 };
        var second = new Wall { Start = new Point2D(4000, 0), End = new Point2D(10000, 0), TypeId = type, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(first);
        document.Add(second);
        return (document, first, second);
    }

    [Fact]
    public void MadeOneTheJointGoesAndWhatEachHadStays()
    {
        var (document, first, second) = TwoInLine();

        // The first half has a line of its own at 1 m and a door in its first bay; the second a line
        // 2 m along it - 6 m along the whole - and its first bay's glass tinted.
        first.CurtainGrid = new CurtainGrid(new[] { 1000.0 }, Array.Empty<double>());
        first.CurtainPanels = new[] { new CurtainPanelOverride(0, 0, CurtainPanelKind.Door) };
        second.CurtainGrid = new CurtainGrid(new[] { 2000.0 }, Array.Empty<double>());
        second.CurtainPanels = new[] { new CurtainPanelOverride(0, 0, CurtainPanelKind.Glazed, Glass: CurtainGlass.Tinted) };

        Assert.True(WallCorners.CanMerge(first, second));
        Assert.True(WallCorners.InLine(first, second));

        var merge = new MergeWallsCommand(document, first, second);
        merge.Redo();

        Assert.Single(document.Walls);
        Assert.Equal(10000, first.Length, 3);

        var layout = CurtainLayout.Of(document, first)!;
        Assert.Equal(new[] { 0.0, 1000, 6000, 10000 }, layout.Verticals.Select(v => Math.Round(v)));
        Assert.DoesNotContain(layout.Verticals, v => Math.Abs(v - 4000) < 1);

        // The door is still in the bay from 0 to 1 m; the tinted glass from 1 m to 6 m, where the
        // second half's first bay now falls.
        Assert.Equal(CurtainPanelKind.Door, layout.Cells.Single(cell => cell.Column == 0).Kind);
        Assert.Equal(CurtainGlass.Tinted, layout.Cells.Single(cell => cell.Column == 1).Glass);

        merge.Undo();
        Assert.Equal(2, document.Walls.Count());
        Assert.Equal(4000, first.Length, 3);
        Assert.Equal(new[] { 1000.0 }, first.CurtainGrid!.Verticals);
        Assert.Equal(CurtainPanelKind.Door, first.CurtainPanels!.Single().Kind);
    }

    [Fact]
    public void TwoPlainOnesFollowTheirTypeAlongTheWhole()
    {
        var (document, first, second) = TwoInLine();

        new MergeWallsCommand(document, first, second).Redo();

        Assert.Null(first.CurtainGrid);
        Assert.Equal(10000, first.Length, 3);
    }

    [Fact]
    public void WallsAtACornerAreNotInLine()
    {
        var (document, first, second) = TwoInLine();
        second.End = new Point2D(4000, 5000);

        Assert.True(WallCorners.CanMerge(first, second));
        Assert.False(WallCorners.InLine(first, second));
    }

    [Fact]
    public void AGlassGableSplitInTwoIsOneSheetMadeOne()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) });
        roof.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge()
        });
        document.Add(roof);

        var type = document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Glazed")).Id;
        var north = new Wall { Start = new Point2D(0, 6000), End = new Point2D(0, 3275), TypeId = type, LevelId = level, UnconnectedHeight = 3000, TopAttachedTo = roof.Id };
        var south = new Wall { Start = new Point2D(0, 3275), End = new Point2D(0, 0), TypeId = type, LevelId = level, UnconnectedHeight = 3000, TopAttachedTo = roof.Id };
        document.Add(north);
        document.Add(south);

        new MergeWallsCommand(document, north, south).Redo();

        var layout = CurtainLayout.Of(document, north)!;
        Assert.Single(document.Walls);
        Assert.NotNull(layout.TopLine);
        Assert.Equal(2, layout.Verticals.Count);
        Assert.True(layout.TopAt(3000) > 5000);
    }
}
