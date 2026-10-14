using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using static BIMDesigner.Tests.StairTests;
using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;

namespace BIMDesigner.Tests;

/// <summary>
/// A stair reshaped by its handles, in plan and in 3D: widened from a side, its turn moved a
/// riser at a time, its well widened, a spiral's inside and outside pulled round the same
/// centre, turned, flipped, and its top and bottom dragged up and down - each one step to undo.
/// </summary>
[Collection("Wpf")]
public class StairGripTests
{
    private static StairGrip Grip(BimDocument document, Stair stair, StairGripKind kind) =>
        StairGrips.Of(document, stair).Single(grip => grip.Kind == kind);

    [Fact]
    public void ASideDraggedOutWidensItTheOtherSideStayingPut()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);

        // Going up +Y, its left side is at 2550: pulled out to 2450, it is 1000 wide, its right side still at 3450.
        var left = Grip(document, stair, StairGripKind.WidthLeft);
        Assert.Equal(2550, left.At.X, 6);
        var said = StairGrips.Drag(document, stair, left, new Point2D(2450, left.At.Y + 300), 0);
        Assert.Equal((1000.0, 2950.0), (stair.Width, stair.Start.X));
        Assert.StartsWith("1000 wide: 15 risers of 200, going 225.", said);

        // No narrower than 500.
        said = StairGrips.Drag(document, stair, Grip(document, stair, StairGripKind.WidthRight), new Point2D(2000, 1000), 0);
        Assert.Equal(500, stair.Width);
        Assert.Contains("it is no narrower", said);
    }

    [Fact]
    public void ItsTurnMovesARiserAtATimeAndItsWellWidens()
    {
        var (document, _) = House();
        var stair = Put(document, 1500, 600, shape: StairShape.HalfTurn);
        var turn = Grip(document, stair, StairGripKind.Turn);
        Assert.Equal(7, Stairs.Layout(document, stair).TurnRisers);

        // Dragged two goings further up its first flight: it turns two risers later.
        StairGrips.Drag(document, stair, turn, turn.At + new Vector2D(0, 2 * 225 + 40), 0);
        Assert.Equal(9, stair.RisersBeforeTurn);
        Assert.Equal(turn.At.Y + 450, Grip(document, stair, StairGripKind.Turn).At.Y, 6);

        // Too far: as late as it can.
        var said = StairGrips.Drag(document, stair, Grip(document, stair, StairGripKind.Turn), new Point2D(1500, 9000), 0);
        Assert.Contains("it can turn no later", said);
        Assert.Equal(Stairs.Layout(document, stair).TurnRisers, stair.RisersBeforeTurn);

        // Its second flight pulled 200 further away: a 300 well.
        var well = Grip(document, stair, StairGripKind.Well);
        StairGrips.Drag(document, stair, well, well.At + well.Axis * 200, 0);
        Assert.Equal(300, stair.Well);
    }

    [Fact]
    public void ASpiralsInsideAndOutsideArePulledRoundTheSameCentre()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 1500, shape: StairShape.Spiral, width: 800);
        var centre = Stairs.Layout(document, stair).Centre!.Value;

        StairGrips.Drag(document, stair, Grip(document, stair, StairGripKind.Outside), centre + new Vector2D(0, 1000), 0);
        Assert.Equal(940, stair.Width);
        Assert.Equal(0, (Stairs.Layout(document, stair).Centre!.Value - centre).Length, 6);

        StairGrips.Drag(document, stair, Grip(document, stair, StairGripKind.Inside), centre + new Vector2D(200, 0), 0);
        Assert.Equal((200.0, 940.0), (stair.InnerRadius, stair.Width));
        Assert.Equal(0, (Stairs.Layout(document, stair).Centre!.Value - centre).Length, 6);
    }

    [Fact]
    public void ItIsTurnedRoundItsFootFlippedAndItsTopAndBottomDraggedUpAndDown()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);

        // The handle in front of its foot, dragged round: up points away from it, 15° when near one.
        StairGrips.Drag(document, stair, Grip(document, stair, StairGripKind.Rotate), new Point2D(3400, 610), 0);
        Assert.Equal(180, stair.Angle, 6);
        var away = 202 * Math.PI / 180;
        StairGrips.Drag(document, stair, Grip(document, stair, StairGripKind.Rotate), new Point2D(3000 + 400 * Math.Cos(away), 600 + 400 * Math.Sin(away)), 0);
        Assert.Equal(22, stair.Angle, 6);
        stair.Angle = 90;

        // Flipped, a straight stair goes up the other way from what was its head.
        var head = Stairs.Layout(document, stair).TopEdge;
        StairGrips.Flip(document, stair);
        Assert.Equal((270.0, head.From.MidpointTo(head.To)), (stair.Angle, stair.Start));

        // Its top dragged 200 up: 200 off the level above, a riser more.
        var top = Grip(document, stair, StairGripKind.Top);
        StairGrips.Drag(document, stair, top, top.At, top.Height + 203);
        Assert.Equal((200.0, 16), (stair.TopOffset, Stairs.Layout(document, stair).Risers));

        // Its foot raised 100 off its floor.
        StairGrips.Drag(document, stair, Grip(document, stair, StairGripKind.Bottom), stair.Start, 100);
        Assert.Equal(100, stair.BaseOffset);

        // A turning stair flipped turns the other way; a split stair has no flip.
        var turning = Put(document, 1500, 600, shape: StairShape.QuarterTurn);
        StairGrips.Flip(document, turning);
        Assert.True(turning.TurnsLeft);
        Assert.DoesNotContain(StairGrips.Of(document, Put(document, 3000, 400, shape: StairShape.Bifurcated)), grip => grip.Kind == StairGripKind.Flip);
    }

    [Fact]
    public void ItsHandlesAreDraggedInPlanAndIn3DEachOneStepToUndo()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _) = House();
                var stair = Put(document, 3000, 600);
                var history = new UndoStack();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = history };
                var hint = "";
                plan.HintChanged += (_, said) => hint = said;
                plan.SetTool(PlanTool.Select);
                plan.Select(stair);

                // Drawn: the handles on the selected stair.
                var border = new Border { Width = 600, Height = 500, Child = plan };
                border.Measure(new Size(600, 500));
                border.Arrange(new Rect(0, 0, 600, 500));
                plan.ZoomToFit();
                new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32).Render(border);

                // In plan: the handle under the cursor is the one picked; its left side dragged out 100.
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var left = Grip(document, stair, StairGripKind.WidthLeft);
                var picked = (StairGrip?)typeof(PlanView).GetMethod("StairGripAt", flags)!.Invoke(plan, new object[] { left.At + new Vector2D(3, 3) });
                Assert.Equal(StairGripKind.WidthLeft, picked?.Kind);
                Assert.True(plan.DragStairGrip(StairGripKind.WidthLeft, left.At + new Vector2D(-100, 0)));
                Assert.True(stair.Width == 1000, $"plan drag: {stair.Width} {hint}");
                Assert.Equal("Widen Stair", history.UndoName);
                history.Undo();
                Assert.Equal((900.0, 3000.0), (stair.Width, stair.Start.X));

                // Its flip button, clicked, flips it there and then.
                Assert.True(plan.DragStairGrip(StairGripKind.Flip, default));
                Assert.Equal((270.0, "Flip Stair"), (stair.Angle, history.UndoName));
                history.Undo();
                Assert.Equal(90, stair.Angle, 6);

                // In 3D: every handle, top and bottom too; its top dragged up a plane facing the eye.
                var grips = plan.StairGrips3D();
                Assert.Contains(grips, grip => grip.Key == "stair:Top" && grip.Vertical);
                var top = grips.Single(grip => grip.Key == "stair:Top").At;
                Assert.True(plan.DragStairGripIn3D("stair:Top", new CorePoint3D(top.X - 5000, top.Y, top.Z + 200), new CorePoint3D(top.X - 4000, top.Y, top.Z + 200)));
                plan.EndStairGripIn3D();
                Assert.Equal((200.0, "Change Stair Height"), (stair.TopOffset, history.UndoName));

                // A side handle followed to where a line of sight from above meets its height.
                var right = grips.Single(grip => grip.Key == "stair:WidthRight").At;
                Assert.True(plan.DragStairGripIn3D("stair:WidthRight", new CorePoint3D(right.X + 100, right.Y, 9000), new CorePoint3D(right.X + 100, right.Y, 8000)));
                plan.EndStairGripIn3D();
                Assert.True(stair.Width == 1000, $"3D drag: {stair.Width} {hint}");
                Assert.True(plan.ClickStairGripIn3D("stair:Flip"));
                Assert.Equal(270, stair.Angle, 6);

                // From low down, nearly level with it, a side still follows the mouse across: the
                // handle slides along its own line to where the line of sight passes nearest.
                (stair.Width, stair.Start, stair.Angle) = (900, new Point2D(3000, 600), 90);
                var side = plan.StairGrips3D().Single(grip => grip.Key == "stair:WidthRight").At;
                var aim = new CorePoint3D(side.X + 120, side.Y, side.Z);
                var eye = new CorePoint3D(aim.X - 200, aim.Y - 9000, aim.Z + 150);
                Assert.True(plan.DragStairGripIn3D("stair:WidthRight", eye, aim));
                plan.EndStairGripIn3D();
                Assert.Equal(1020, stair.Width);

                // Not with the Select tool on: a stair clicked in 3D is selected all the same, and has its handles.
                plan.SetTool(PlanTool.Wall);
                plan.Select(stair);
                Assert.NotEmpty(plan.StairGrips3D());
                plan.SetTool(PlanTool.Select);
                plan.Select(stair);

                // And drawn over the 3D view.
                var model = new ModelView { Document = document, Grips = plan.StairGrips3D };
                var host = new Border { Width = 600, Height = 500, Child = model };
                host.Measure(new Size(600, 500));
                host.Arrange(new Rect(0, 0, 600, 500));
                model.Rebuild();
                model.ZoomToFit();
                Assert.Contains(plan.StairGrips3D(), grip => model.ScreenOf(grip.At) is { X: > 0 and < 600, Y: > 0 and < 500 });
                new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32).Render(host);

                // While a handle is dragged only the stair is shown anew; let go, the model is built again.
                stair.Width = 1400;
                model.PreviewElement(stair.Id, StairModel.Meshes(document, stair));
                new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32).Render(host);
                model.SetKindVisible(MeshKind.Stair, false);
                Assert.DoesNotContain(plan.StairGrips3D(), grip => model.ScreenOf(grip.At) is null);
                model.SetKindVisible(MeshKind.Stair, true);
                model.Rebuild();
                new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32).Render(host);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
