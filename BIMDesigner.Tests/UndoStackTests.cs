using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

public class UndoStackTests
{
    private static (BimDocument Document, UndoStack History, WallType Type, Guid LevelId) Scenario()
    {
        var document = BimDocument.CreateDefault();
        return (document, new UndoStack(), document.TypesOf<WallType>().First(), document.Levels.First().Id);
    }

    private static Wall NewWall(WallType type, Guid levelId, double y = 0) => new()
    {
        Start = new Point2D(0, y),
        End = new Point2D(5000, y),
        TypeId = type.Id,
        LevelId = levelId
    };

    [Fact]
    public void Undo_RemovesAnAddedElement()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId), "Draw Wall"));
        Assert.Single(document.Walls);

        history.Undo();
        Assert.Empty(document.Walls);

        history.Redo();
        Assert.Single(document.Walls);
    }

    [Fact]
    public void Undo_RestoresADeletedElementAtItsOriginalPosition()
    {
        var (document, history, type, levelId) = Scenario();

        var first = NewWall(type, levelId, 0);
        var middle = NewWall(type, levelId, 1000);
        var last = NewWall(type, levelId, 2000);
        foreach (var wall in new[] { first, middle, last }) document.Add(wall);

        history.Execute(new DeleteElementCommand(document, middle));
        Assert.Equal(new[] { first, last }, document.Elements);

        history.Undo();
        Assert.Equal(new[] { first, middle, last }, document.Elements);
    }

    [Fact]
    public void Undo_ReversesAParameterEdit()
    {
        var (document, history, type, levelId) = Scenario();
        var wall = NewWall(type, levelId);
        document.Add(wall);

        var height = wall.GetInstanceParameters(document).Single(p => p.Name == "Unconnected Height");
        var before = height.Value;
        height.TrySetFromText("4.00 m");
        history.Record(new ParameterChangeCommand(height, before, height.Value));

        Assert.Equal(4000, wall.UnconnectedHeight, precision: 6);

        history.Undo();
        Assert.Equal(3000, wall.UnconnectedHeight, precision: 6);

        history.Redo();
        Assert.Equal(4000, wall.UnconnectedHeight, precision: 6);
    }

    [Fact]
    public void Undo_ReversesARetype()
    {
        var (document, history, type, levelId) = Scenario();
        var wall = NewWall(type, levelId);
        document.Add(wall);

        var partition = document.TypesOf<WallType>().Single(t => t.Name.Contains("Partition"));
        history.Execute(new SetElementTypeCommand(wall, partition.Id, partition.Name));

        Assert.Equal(partition.Id, wall.TypeId);

        history.Undo();
        Assert.Equal(type.Id, wall.TypeId);
    }

    [Fact]
    public void UndoNames_DescribeTheEditInTheMenu()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId), "Draw Wall"));

        Assert.Equal("Draw Wall", history.UndoName);
        Assert.Null(history.RedoName);

        history.Undo();
        Assert.Equal("Draw Wall", history.RedoName);
        Assert.Null(history.UndoName);
    }

    [Fact]
    public void ANewEditDiscardsTheRedoBranch()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId, 0), "Draw Wall"));
        history.Undo();
        Assert.True(history.CanRedo);

        history.Execute(new AddElementCommand(document, NewWall(type, levelId, 1000), "Draw Wall"));

        Assert.False(history.CanRedo);
        Assert.Single(document.Walls);
    }

    [Fact]
    public void ReplayingDoesNotRecordItself()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId), "Draw Wall"));
        history.Undo();
        history.Redo();
        history.Undo();

        // One command in, one command out - an undo must not push its own inverse.
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    // ---- unsaved-changes tracking ----------------------------------------------

    [Fact]
    public void ANewProjectIsNotModified()
    {
        var (_, history, _, _) = Scenario();

        Assert.False(history.IsModified);
    }

    [Fact]
    public void AnEditMarksTheProjectModified()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId), "Draw Wall"));

        Assert.True(history.IsModified);
    }

    [Fact]
    public void SavingClearsTheModifiedFlag()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId), "Draw Wall"));
        history.MarkClean();

        Assert.False(history.IsModified);
    }

    [Fact]
    public void UndoingBackToTheSavedStateClearsTheModifiedFlag()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId, 0), "Draw Wall"));
        history.MarkClean();
        history.Execute(new AddElementCommand(document, NewWall(type, levelId, 1000), "Draw Wall"));
        Assert.True(history.IsModified);

        history.Undo();
        Assert.False(history.IsModified);
    }

    [Fact]
    public void RedoingPastTheSavedStateMarksTheProjectModifiedAgain()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId, 0), "Draw Wall"));
        history.MarkClean();
        history.Undo();
        Assert.True(history.IsModified);

        history.Redo();
        Assert.False(history.IsModified);
    }

    [Fact]
    public void DivergingFromTheSavedStateLeavesTheProjectPermanentlyModified()
    {
        var (document, history, type, levelId) = Scenario();

        history.Execute(new AddElementCommand(document, NewWall(type, levelId, 0), "Draw Wall"));
        history.MarkClean();
        history.Undo();

        // The saved state now lives on a branch the redo stack just lost, so the document
        // cannot be considered saved again until it is written out.
        history.Execute(new AddElementCommand(document, NewWall(type, levelId, 1000), "Draw Wall"));

        Assert.True(history.IsModified);
    }
}
