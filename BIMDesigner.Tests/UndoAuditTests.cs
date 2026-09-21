using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// An audit of undo.
///
/// Each command has its own tests, but those check what their author thought to check. This
/// one asks a single blunt question of every command: after doing it and undoing it, is the
/// project byte-for-byte what it was? The saved file is the yardstick, because it is the
/// whole of what a project is.
///
/// A command that undoes <em>almost</em> completely is the worst kind: the user sees the shape
/// go back and believes the model did too, and the difference is only discovered much later.
/// </summary>
public class UndoAuditTests
{
    /// <summary>The whole project as it would be saved, minus the time it was saved at.</summary>
    private static string State(BimDocument document) =>
        string.Join('\n', ProjectFile.ToJson(document).Split('\n').Where(line => !line.Contains("\"savedUtc\"")));

    /// <summary>
    /// Runs a command through the history, undoes it, and insists nothing is left behind. Then
    /// redoes it, to check that redo produces exactly what the command did the first time.
    /// </summary>
    private static void AssertReversible(BimDocument document, Func<IUndoableCommand> build, string what)
    {
        var history = new UndoStack();
        var before = State(document);

        history.Execute(build());
        var after = State(document);

        Assert.True(before != after, $"{what}: the command changed nothing, so the test proves nothing.");

        history.Undo();
        Assert.True(before == State(document), $"{what}: undo did not put the project back.");

        history.Redo();
        Assert.True(after == State(document), $"{what}: redo did not reproduce the change.");
    }

    /// <summary>A small but complete project for the commands to act on.</summary>
    private static (BimDocument Document, Wall Wall, Door Door, Floor Floor, Sheet Sheet) Project()
    {
        var document = BimDocument.CreateDefault();
        var ground = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));

        Point2D[] corners = { new(0, 0), new(6000, 0), new(6000, 4000), new(0, 4000) };
        Wall? first = null;

        for (var i = 0; i < corners.Length; i++)
        {
            var wall = new Wall
            {
                Start = corners[i],
                End = corners[(i + 1) % corners.Length],
                TypeId = type.Id,
                LevelId = ground
            };

            document.Add(wall);
            first ??= wall;
        }

        var door = new Door
        {
            TypeId = document.TypesOf<DoorType>().First(t => t.Width == 900).Id,
            LevelId = ground,
            HostWallId = first!.Id,
            DistanceAlongWall = 3000
        };
        document.Add(door);

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = ground };
        floor.SetBoundary(corners);
        document.Add(floor);

        document.Add(new Room { Location = new Point2D(3000, 2000), LevelId = ground, Name = "Hall" });

        var section = new SectionMarker
        {
            Start = new Point2D(3000, -2000),
            End = new Point2D(3000, 6000),
            LevelId = ground,
            Name = "A"
        };
        document.Add(section);

        var sheet = new Sheet { Number = "A-101", Name = "Plans" };
        sheet.Add(new Viewport { View = ViewReference.FloorPlan(ground), Centre = new Point2D(200, 300) });
        document.Add(sheet);

        return (document, first, door, floor, sheet);
    }

    // ---- elements ----------------------------------------------------------------

    [Fact]
    public void AddingAnElementIsReversible()
    {
        var (document, wall, _, _, _) = Project();

        AssertReversible(document, () => new AddElementCommand(document, new Wall
        {
            Start = new Point2D(0, 8000),
            End = new Point2D(6000, 8000),
            TypeId = wall.TypeId,
            LevelId = wall.LevelId
        }, "Add Wall"), "Add element");
    }

    [Fact]
    public void DeletingAWallAndItsDoorIsReversible()
    {
        var (document, wall, _, _, _) = Project();

        // The cascade is the interesting part: the door has to come back too, in its old place.
        AssertReversible(document, () => new DeleteElementCommand(document, wall), "Delete with cascade");
    }

    [Fact]
    public void DeletingASelectionIsReversible()
    {
        var (document, wall, _, floor, _) = Project();

        AssertReversible(document,
            () => new DeleteElementsCommand(document, new Element[] { wall, floor }),
            "Delete selection");
    }

    [Fact]
    public void PastingSeveralElementsIsReversible()
    {
        var (document, wall, _, _, _) = Project();

        AssertReversible(document, () =>
        {
            var copies = ElementCopy.Duplicate(document, new Element[] { wall });
            foreach (var copy in copies) ElementTransforms.Move(copy, new Vector2D(0, 9000));

            return new AddElementsCommand(document, copies, "Paste");
        }, "Paste");
    }

    [Fact]
    public void MovingASelectionIsReversible()
    {
        var (document, wall, _, floor, _) = Project();

        AssertReversible(document,
            () => new MoveElementsCommand(new Element[] { wall, floor }, new Vector2D(1250, -500)),
            "Move selection");
    }

    [Fact]
    public void MovingAndReshapingAWallIsReversible()
    {
        var (document, wall, _, _, _) = Project();

        AssertReversible(document,
            () => new MoveWallCommand(wall, wall.Start, wall.End,
                new Point2D(-500, -500), new Point2D(6500, -500), "Move Wall"),
            "Move wall");
    }

    [Fact]
    public void SplittingAWallIsReversible()
    {
        var (document, wall, _, _, _) = Project();

        AssertReversible(document,
            () => new SplitWallCommand(document, wall, new Point2D(3000, 0)),
            "Split wall");
    }

    [Fact]
    public void ChangingAParameterIsReversible()
    {
        var (document, wall, _, _, _) = Project();

        AssertReversible(document, () =>
        {
            var parameter = wall.GetInstanceParameters(document).First(p => p.Name == "Unconnected Height");
            return new ParameterChangeCommand(parameter, wall.UnconnectedHeight, 2650d);
        }, "Parameter change");
    }

    [Fact]
    public void ChangingAnElementTypeIsReversible()
    {
        var (document, wall, _, _, _) = Project();
        var other = document.TypesOf<WallType>().First(t => t.Id != wall.TypeId);

        AssertReversible(document,
            () => new SetElementTypeCommand(wall, other.Id, other.Name),
            "Change type");
    }

    // ---- levels ------------------------------------------------------------------

    [Fact]
    public void AddingALevelIsReversible()
    {
        var (document, _, _, _, _) = Project();

        AssertReversible(document,
            () => new AddLevelCommand(document, new Level { Name = "Second Floor", Elevation = 6000 }),
            "Add level");
    }

    [Fact]
    public void RenamingALevelIsReversible()
    {
        var (document, _, _, _, _) = Project();

        AssertReversible(document,
            () => new RenameLevelCommand(document.Levels[0], "Lower Ground"),
            "Rename level");
    }

    [Fact]
    public void MovingALevelPastAnotherIsReversible()
    {
        var (document, _, _, _, _) = Project();

        // Moving a level reorders the list, so undo has to put the order back as well.
        AssertReversible(document,
            () => new MoveLevelCommand(document, document.Levels[1], -2000),
            "Move level");
    }

    [Fact]
    public void DeletingAWholeStoreyIsReversible()
    {
        var (document, _, _, _, _) = Project();

        // Everything on the ground floor goes with it, and one undo has to bring it all back
        // in its original order.
        AssertReversible(document,
            () => new DeleteLevelCommand(document, document.Levels[0]),
            "Delete level");
    }

    // ---- sheets ------------------------------------------------------------------

    [Fact]
    public void PlacingAViewOnASheetIsReversible()
    {
        var (document, _, _, _, sheet) = Project();

        AssertReversible(document, () => new AddViewportCommand(sheet, new Viewport
        {
            View = ViewReference.FloorPlan(document.Levels[1].Id),
            Centre = new Point2D(200, 120)
        }, "Place View"), "Add viewport");
    }

    [Fact]
    public void RemovingAViewFromASheetIsReversible()
    {
        var (document, _, _, _, sheet) = Project();

        AssertReversible(document,
            () => new RemoveViewportCommand(sheet, sheet.Viewports[0]),
            "Remove viewport");
    }

    [Fact]
    public void MovingAViewOnASheetIsReversible()
    {
        var (document, _, _, _, sheet) = Project();
        var viewport = sheet.Viewports[0];

        AssertReversible(document,
            () => new MoveViewportCommand(viewport, viewport.Centre, new Point2D(250, 180)),
            "Move viewport");
    }

    [Fact]
    public void ChangingAViewsScaleIsReversible()
    {
        var (document, _, _, _, sheet) = Project();

        AssertReversible(document,
            () => SetViewportScaleCommand.On(document, sheet, sheet.Viewports[0], new ViewScale(50)),
            "Change viewport scale");
    }

    // ---- the stack itself ---------------------------------------------------------

    [Fact]
    public void AWholeSessionOfEditsUndoesBackToWhereItStarted()
    {
        var (document, wall, _, floor, sheet) = Project();
        var history = new UndoStack();
        var before = State(document);

        var other = document.TypesOf<WallType>().First(t => t.Id != wall.TypeId);

        history.Execute(new MoveElementsCommand(new Element[] { wall, floor }, new Vector2D(500, 500)));
        history.Execute(new SetElementTypeCommand(wall, other.Id, other.Name));
        history.Execute(new AddLevelCommand(document, new Level { Name = "Second", Elevation = 6000 }));
        history.Execute(new SplitWallCommand(document, wall, wall.Start.MidpointTo(wall.End)));
        history.Execute(new AddViewportCommand(sheet, new Viewport
        {
            View = ViewReference.FloorPlan(document.Levels[0].Id),
            Centre = new Point2D(150, 120)
        }, "Place View"));

        Assert.NotEqual(before, State(document));

        // Undo far enough and the project must be exactly as it started - not nearly.
        while (history.CanUndo) history.Undo();

        Assert.Equal(before, State(document));
    }

    [Fact]
    public void UndoingBackToTheLastSaveLeavesTheProjectClean()
    {
        var (document, wall, _, _, _) = Project();
        var history = new UndoStack();

        history.MarkClean();
        Assert.False(history.IsModified);

        history.Execute(new MoveElementsCommand(new Element[] { wall }, new Vector2D(100, 0)));
        Assert.True(history.IsModified);

        history.Undo();

        // Undoing back to where it was saved means there is nothing to save, and the prompt
        // on closing should not appear.
        Assert.False(history.IsModified);
    }
}
