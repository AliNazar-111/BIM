using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// The drawing of the model a plan keeps between repaints: the same while nothing it shows has
/// changed, made again the moment the model or how it is shown changes - an edit done or undone,
/// a wall moved, a view setting - and moved with the view when it is panned.
/// </summary>
[Collection("Wpf")]
public class PlanDrawingTests
{
    private static void OnUi(Action act)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                act();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    private static byte[] Pixels(PlanView plan)
    {
        plan.InvalidateVisual();
        plan.UpdateLayout();
        var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(plan);
        var pixels = new byte[400 * 300 * 4];
        bitmap.CopyPixels(pixels, 400 * 4, 0);
        return pixels;
    }

    private static PlanView Plan(BimDocument document, UndoStack history)
    {
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = history, Width = 400, Height = 300, ShowUnderlay = false };
        plan.Measure(new Size(400, 300));
        plan.Arrange(new Rect(0, 0, 400, 300));
        plan.ZoomToFit();
        return plan;
    }

    [Fact]
    public void TheDrawingKeptIsMadeAgainWhenTheModelChanges() => OnUi(() =>
    {
        var (document, _) = House();
        var history = new UndoStack();
        var plan = Plan(document, history);
        var before = Pixels(plan);

        // Drawn again with nothing changed: the same.
        Assert.Equal(before, Pixels(plan));

        // A wall taken away: drawn without it - and back again once that is undone.
        history.Execute(new DeleteElementCommand(document, document.Walls.First()));
        Assert.NotEqual(before, Pixels(plan));
        history.Undo();
        Assert.Equal(before, Pixels(plan));

        // A wall moved - nothing added or taken away, only changed: drawn where it is now.
        history.Execute(new MoveElementsCommand(new[] { document.Walls.First() }, new Vector2D(800, 0)));
        Assert.NotEqual(before, Pixels(plan));
        history.Undo();
        Assert.Equal(before, Pixels(plan));

        // How the plan is shown, changed: drawn so.
        document.ViewSettings.SetWallFunctionVisible(plan.CurrentView, WallFunction.Interior, false);
        Assert.NotEqual(before, Pixels(plan));
    });

    [Fact]
    public void PannedTheDrawingKeptMovesWithTheView() => OnUi(() =>
    {
        var (document, _) = House();
        var history = new UndoStack();
        var plan = Plan(document, history);
        Pixels(plan);

        // Panned, then drawn afresh after: the drawing kept, moved, is the drawing made anew.
        typeof(PlanView).GetProperty("ViewCentre")!.SetValue(plan, new Point2D(plan.ViewCentre.X + 1000, plan.ViewCentre.Y));
        var moved = Pixels(plan);
        plan.RefreshModel();
        Assert.Equal(moved, Pixels(plan));
    });
}
