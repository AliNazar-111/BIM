using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A stretch of curtain grid line taken out between the lines crossing it - Revit's
/// Add/Remove Segments: the bays either side are one panel, the mullion stops there, and the
/// stretch stays taken out as the wall is saved, split and merged.
/// </summary>
public class CurtainSegmentTests
{
    private static CurtainWallType Storefront(BimDocument document) =>
        document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Storefront"));

    /// <summary>A 6 m storefront, 3 m high: lines at 2 m and 4 m along, and 1.5 m up - three bays by two.</summary>
    private static (BimDocument Document, Wall Wall) Wall(params CurtainSegment[] removed)
    {
        var document = BimDocument.CreateDefault();
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), LevelId = document.Levels[0].Id, UnconnectedHeight = 3000,
            TypeId = Storefront(document).Id,
            CurtainGrid = new CurtainGrid(new[] { 2000.0, 4000 }, new[] { 1500.0 }).WithRemoved(removed)
        };
        document.Add(wall);
        return (document, wall);
    }

    [Fact]
    public void TakenOutInOneRowTheBaysEitherSideAreOnePanelThere()
    {
        // The line 2 m along, taken out in the top row only.
        var (document, wall) = Wall(new CurtainSegment(true, 2000, 1500, 3000));
        var layout = CurtainLayout.Of(document, wall)!;

        var merged = layout.Cells.Single(cell => cell.Row == 1 && cell.Column == 0);
        Assert.Equal(2, merged.Columns);
        Assert.Equal(4000, merged.To, 3);
        Assert.DoesNotContain(layout.Cells, cell => cell.Row == 1 && cell.Column == 1);

        // Below it, the bays are as they were.
        Assert.Equal(1, layout.Cells.Single(cell => cell.Row == 0 && cell.Column == 0).Columns);
        Assert.Contains(layout.Cells, cell => cell.Row == 0 && cell.Column == 1);

        // The mullion on that line stops at the transom, not running on up through the panel.
        var upright = layout.Mullions.Single(mullion => mullion.IsVertical && Math.Abs((mullion.From + mullion.To) / 2 - 2000) < 1);
        Assert.Equal(1500, upright.Top, 3);
    }

    [Fact]
    public void TakenOutAllTheWayUpTheTransomRunsOnUnbroken()
    {
        var (document, wall) = Wall(new CurtainSegment(true, 2000, 0, 3000));
        var layout = CurtainLayout.Of(document, wall)!;

        Assert.DoesNotContain(layout.Mullions, mullion => mullion.IsVertical && Math.Abs((mullion.From + mullion.To) / 2 - 2000) < 1);

        // The transom at 1.5 m crosses where the line was in one piece, from the border to the line at 4 m.
        var transom = layout.Mullions.Where(mullion => !mullion.IsVertical && Math.Abs((mullion.Bottom + mullion.Top) / 2 - 1500) < 1).OrderBy(m => m.From).First();
        Assert.True(transom.To > 3900, $"the transom stops at {transom.To:0}");
    }

    [Fact]
    public void WhereTheyWouldNotMakeARectangleTheBaysStayAsTheyWere()
    {
        // The line at 2 m out in the top row, and the transom out under the bay beside: an L.
        var (document, wall) = Wall(new CurtainSegment(true, 2000, 1500, 3000), new CurtainSegment(false, 1500, 2000, 4000));
        var layout = CurtainLayout.Of(document, wall)!;

        Assert.All(layout.Cells, cell => Assert.True(cell.Columns == 1 && cell.Rows == 1));
    }

    [Fact]
    public void SavedAndOpenedItIsStillTakenOut()
    {
        var (document, wall) = Wall(new CurtainSegment(true, 2000, 1500, 3000));

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = reloaded.Walls.Single(w => w.Id == wall.Id);

        Assert.Equal(new CurtainSegment(true, 2000, 1500, 3000), again.CurtainGrid!.Removed!.Single());
        Assert.Equal(2, CurtainLayout.Of(reloaded, again)!.Cells.Single(cell => cell.Row == 1 && cell.Column == 0).Columns);
    }

    [Fact]
    public void SplitAndMergedAgainItIsStillTakenOut()
    {
        // The line at 4 m out in the top row; the wall split at 1 m, far from it.
        var (document, wall) = Wall(new CurtainSegment(true, 4000, 1500, 3000));
        var split = new SplitWallCommand(document, wall, new Point2D(1000, 0));
        split.Redo();

        var far = document.Walls.Single(w => !ReferenceEquals(w, wall));
        var farLayout = CurtainLayout.Of(document, far)!;
        Assert.Contains(farLayout.Cells, cell => cell.Row == 1 && cell.Columns == 2);

        new MergeWallsCommand(document, wall, far).Redo();
        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Contains(layout.Cells, cell => cell.Row == 1 && cell.Columns == 2 && Math.Abs(cell.To - 6000) < 1);
    }
}

/// <summary>The Curtain Grid editor: a click on a line is the stretch of it between the lines crossing it.</summary>
public class CurtainSegmentEditorTests
{
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The editor failed on the UI thread.", failure);
    }

    [Fact]
    public void DeleteTakesOutTheStretchSelectedAndPutsItBack()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var type = document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Storefront"));
            var editor = new BIMDesigner.UI.Controls.CurtainGridEditor();
            editor.Show(type, 6000, 3000, new[] { 2000.0, 4000 }, new[] { 1500.0 }, Array.Empty<CurtainPanelOverride>());

            // The line at 2 m, its top stretch: out, and the line itself still there below.
            editor.Select(true, 0, 1);
            editor.RemoveSelected();
            Assert.Equal(new CurtainSegment(true, 2000, 1500, 3000), editor.Removed.Single());
            Assert.Equal(new[] { 2000.0, 4000 }, editor.Verticals);

            // Delete again on it: back.
            editor.RemoveSelected();
            Assert.Empty(editor.Removed);

            // The whole line, all of it.
            editor.Select(true, 0, null);
            editor.RemoveSelected();
            Assert.Equal(new[] { 4000.0 }, editor.Verticals);
        });
    }

    [Fact]
    public void AStretchThatWouldLeaveAnLShapedPanelIsNotTakenOut()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var type = document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Storefront"));
            var editor = new BIMDesigner.UI.Controls.CurtainGridEditor();
            editor.Show(type, 6000, 3000, new[] { 2000.0, 4000 }, new[] { 1500.0 }, Array.Empty<CurtainPanelOverride>());
            string? refused = null;
            editor.Refused += (_, why) => refused = why;

            editor.Select(true, 0, 1);
            editor.RemoveSelected();

            // The transom under the next bay along: the panel would be an L.
            editor.Select(false, 0, 1);
            editor.RemoveSelected();

            Assert.Single(editor.Removed);
            Assert.Contains("rectangle", refused);
        });
    }
}
