using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>Room separators: lines that bound rooms where no wall does, drawn, moved, copied, saved and undone like anything else.</summary>
[Collection("Wpf")]
public class RoomSeparatorTests
{
    /// <summary>Four walls round an 8 m by 4 m room, 200 thick, with two rooms in it: one each end.</summary>
    private static (BimDocument Document, Room Kitchen, Room Living) Box()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(t => t.Name == "Generic - 200mm");
        Point2D[] corners = { new(0, 0), new(8000, 0), new(8000, 4000), new(0, 4000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 });
        var kitchen = new Room { LevelId = level, Location = new Point2D(2000, 2000), Name = "Kitchen" };
        var living = new Room { LevelId = level, Location = new Point2D(6000, 2000), Name = "Living" };
        document.Add(kitchen);
        document.Add(living);
        return (document, kitchen, living);
    }

    [Fact]
    public void ASeparatorBoundsTheRoomsEitherSideAsAWallDoesWithNoThicknessToTakeOff()
    {
        var (document, kitchen, living) = Box();
        var half = document.GetWallType(document.Walls.First())!.Width / 2;
        var whole = kitchen.GetBoundary(document);
        Assert.True(whole.IsEnclosed);
        Assert.Equal(whole.Area, living.GetBoundary(document).Area, 1);

        var separator = new RoomSeparator { LevelId = document.Levels[0].Id, Start = new Point2D(4000, 0), End = new Point2D(4000, 4000) };
        document.Add(separator);
        var (one, other) = (kitchen.GetBoundary(document), living.GetBoundary(document));
        Assert.Equal((4000 - half) * (4000 - 2 * half), one.Area, 1);
        Assert.Equal(one.Area, other.Area, 1);
        Assert.Equal(whole.Area, one.Area + other.Area, 1);
        Assert.Equal(3, one.BoundingWalls.Count);

        // On another storey, it parts nothing here.
        separator.LevelId = document.Levels[1].Id;
        Assert.Equal(whole.Area, kitchen.GetBoundary(document).Area, 1);
    }

    [Fact]
    public void ASeparatorIsMovedTurnedMirroredCopiedDraggedAndSaved()
    {
        var (document, _, _) = Box();
        var separator = new RoomSeparator { LevelId = document.Levels[0].Id, Start = new Point2D(4000, 0), End = new Point2D(4000, 4000), Mark = "S1" };
        document.Add(separator);

        ElementTransforms.Move(separator, new Vector2D(500, 0));
        Assert.Equal((new Point2D(4500, 0), new Point2D(4500, 4000)), (separator.Start, separator.End));
        ElementTransforms.Mirror(separator, new Line2D(new Point2D(4000, 0), new Vector2D(0, 1)));
        Assert.Equal(3500, separator.Start.X, 6);
        ElementTransforms.Rotate(separator, new Point2D(3500, 2000), 90);
        Assert.Equal(0, separator.Start.DistanceTo(new Point2D(5500, 2000)), 6);

        var copy = Assert.IsType<RoomSeparator>(Assert.Single(ElementCopy.Duplicate(document, new[] { separator })));
        Assert.NotEqual(separator.Id, copy.Id);
        Assert.Equal((separator.Start, separator.End, separator.LevelId), (copy.Start, copy.End, copy.LevelId));

        var end = ElementHandles.Of(document, separator).Single(handle => handle.Key == "end");
        var before = ElementHandles.Snapshot(separator);
        ElementHandles.Drag(document, separator, end, new Point2D(1500, 2500), 0);
        Assert.Equal(new Point2D(1500, 2500), separator.End);
        ElementHandles.Restore(separator, before);
        Assert.Equal(1500, separator.End.X, 6);

        var path = Path.Combine(Path.GetTempPath(), $"separator-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var back = Assert.Single(ProjectFile.Load(path).All<RoomSeparator>());
            Assert.Equal((separator.Id, separator.Start, separator.End, separator.LevelId, "S1"), (back.Id, back.Start, back.End, back.LevelId, back.Mark));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }

    [Fact]
    public void TheToolDrawsAChainOfSeparatorsEachAStepToUndo()
    {
        OnUiThread(() =>
        {
            var (document, kitchen, _) = Box();
            var history = new UndoStack();
            var plan = new PlanView { Width = 800, Height = 600, Document = document, History = history };
            plan.SetTool(PlanTool.RoomSeparator);
            plan.ActiveLevelId = document.Levels[0].Id;

            // Across the room, then along it: two lines, the second from where the first ended.
            Assert.True(plan.ClickRoomSeparator(new Point2D(4000, 0)));
            Assert.True(plan.ClickRoomSeparator(new Point2D(4000, 4000)));
            Assert.True(plan.ClickRoomSeparator(new Point2D(6000, 4000)));
            var lines = document.All<RoomSeparator>().ToList();
            Assert.Equal(2, lines.Count);
            Assert.Equal(lines[0].End, lines[1].Start);
            Assert.True(kitchen.GetBoundary(document).Area < 4000 * 4000);

            history.Undo();
            history.Undo();
            Assert.Empty(document.All<RoomSeparator>());
        });
    }
}
