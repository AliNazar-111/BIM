using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// Moving the selection with the arrow keys.
///
/// Revit nudges by a distance that depends on the zoom, so the same key press means a
/// different move at every zoom level. Here a nudge is the snapping step - a distance that
/// can be said out loud, and the same wherever the view happens to be.
/// </summary>
[Collection("Wpf")]
public class NudgeTests
{
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

        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }

    private static (BimDocument Document, Wall Wall, PlanView Plan) Wall6m()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        var plan = new PlanView { Document = document, ActiveLevelId = wall.LevelId, History = new UndoStack() };
        return (document, wall, plan);
    }

    private static readonly Vector2D Right = new(1, 0);
    private static readonly Vector2D Up = new(0, 1);

    [Fact]
    public void AnArrowMovesTheSelectionBySnappingStep()
    {
        OnUiThread(() =>
        {
            var (_, wall, plan) = Wall6m();
            plan.Select(wall);

            Assert.True(plan.Nudge(Right, far: false, continuing: false));

            // The step is the snapping step, not a screen distance: a round 100 mm.
            Assert.Equal(100, wall.Start.X, precision: 6);
            Assert.Equal(6100, wall.End.X, precision: 6);
            Assert.Equal(0, wall.Start.Y, precision: 6);

            // It follows the snapping step rather than the zoom, so what a press means is the
            // distance the drawing is already set out on.
            plan.SnapStepMm = 50;
            Assert.True(plan.Nudge(Up, far: false, continuing: false));
            Assert.Equal(50, wall.Start.Y, precision: 6);
        });
    }

    [Fact]
    public void ShiftMovesTenTimesAsFar()
    {
        OnUiThread(() =>
        {
            var (_, wall, plan) = Wall6m();
            plan.Select(wall);

            Assert.True(plan.Nudge(Right, far: true, continuing: false));

            Assert.Equal(1000, wall.Start.X, precision: 6);
            Assert.Equal(7000, wall.End.X, precision: 6);
        });
    }

    [Fact]
    public void OnePressIsOneThingToUndo()
    {
        OnUiThread(() =>
        {
            var (_, wall, plan) = Wall6m();
            plan.Select(wall);

            plan.Nudge(Right, far: false, continuing: false);
            plan.Nudge(Right, far: false, continuing: false);

            Assert.Equal(200, wall.Start.X, precision: 6);

            plan.History!.Undo();
            Assert.Equal(100, wall.Start.X, precision: 6);

            plan.History.Undo();
            Assert.Equal(0, wall.Start.X, precision: 6);
            Assert.False(plan.History.CanUndo);

            // And redo puts the run back rather than losing it.
            plan.History.Redo();
            Assert.Equal(100, wall.Start.X, precision: 6);
        });
    }

    [Fact]
    public void AHeldKeyIsOneMoveNotThirtyASecond()
    {
        OnUiThread(() =>
        {
            var (_, wall, plan) = Wall6m();
            plan.Select(wall);

            plan.Nudge(Right, far: false, continuing: false);
            for (var i = 0; i < 24; i++) plan.Nudge(Right, far: false, continuing: true);

            Assert.Equal(2500, wall.Start.X, precision: 6);

            // One Undo takes the whole run back, leaving nothing behind it on the stack.
            plan.History!.Undo();
            Assert.Equal(0, wall.Start.X, precision: 6);
            Assert.False(plan.History.CanUndo);
        });
    }

    [Fact]
    public void ADoorInTheWallTravelsWithItRatherThanTwice()
    {
        OnUiThread(() =>
        {
            var (document, wall, plan) = Wall6m();
            var doorType = document.TypesOf<DoorType>().First();
            var door = new Door
            {
                HostWallId = wall.Id, TypeId = doorType.Id,
                LevelId = wall.LevelId, DistanceAlongWall = 2000
            };
            document.Add(door);

            plan.SelectMany(new Element[] { wall, door });
            plan.Nudge(Right, far: false, continuing: false);

            // The door is positioned along its wall, so it has gone with it; moving it again
            // on its own would slide it 100 mm further along the wall every press.
            Assert.Equal(2000, door.DistanceAlongWall, precision: 6);
            Assert.Equal(100, wall.Start.X, precision: 6);
        });
    }

    [Fact]
    public void NothingSelectedMeansNothingHappens()
    {
        OnUiThread(() =>
        {
            var (_, wall, plan) = Wall6m();

            Assert.False(plan.Nudge(Right, far: false, continuing: false));
            Assert.Equal(0, wall.Start.X, precision: 6);
            Assert.False(plan.History!.CanUndo);
        });
    }
}
