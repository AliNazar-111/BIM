using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// Shaping a roof on the plan: clicking one of its edges turns the slope there on or off,
/// which is where Revit puts "Defines slope" and is the only decision a roof by footprint is.
/// </summary>
[Collection("Wpf")]
public class RoofPlanTests
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

    private static (BimDocument Document, Roof Roof, PlanView Plan) Roofed(RoofForm form)
    {
        var document = BimDocument.CreateDefault();
        var roof = new Roof
        {
            TypeId = document.TypesOf<RoofType>().First().Id,
            LevelId = document.Levels[0].Id,
            HeightOffset = 3000
        };

        roof.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000)
        });

        roof.SlopeDegrees = 30;
        roof.SetShape(form);
        document.Add(roof);

        var plan = new PlanView
        {
            Document = document,
            ActiveLevelId = roof.LevelId,
            History = new UndoStack(),
            Width = 900,
            Height = 600
        };

        plan.ZoomToFit();
        plan.Select(roof);

        return (document, roof, plan);
    }

    [Fact]
    public void ClickingAnEdgeOfASelectedRoofTurnsItsSlopeOff()
    {
        OnUiThread(() =>
        {
            var (_, roof, plan) = Roofed(RoofForm.Hip);

            // The short end at x = 10000, clicked half way along it.
            Assert.True(plan.ToggleRoofEdgeAt(new Point2D(10000, 3000)));
            Assert.False(roof.Edges[1].DefinesSlope);

            // The other end too, which is what turns a hip roof into a gable roof.
            Assert.True(plan.ToggleRoofEdgeAt(new Point2D(0, 3000)));
            Assert.Equal(RoofForm.Gable, roof.Form);

            // Each click is its own step, and undo puts the hip back.
            plan.History!.Undo();
            plan.History.Undo();
            Assert.Equal(RoofForm.Hip, roof.Form);
        });
    }

    [Fact]
    public void ClickingAGableEdgeGivesItASlopeAtTheRoofsOwnPitch()
    {
        OnUiThread(() =>
        {
            var (_, roof, plan) = Roofed(RoofForm.Gable);
            roof.SlopeDegrees = 40;

            Assert.True(plan.ToggleRoofEdgeAt(new Point2D(10000, 3000)));

            Assert.True(roof.Edges[1].DefinesSlope);

            // One roof, one pitch: the edge takes the roof's rather than the tool's default.
            Assert.Equal(40, roof.Edges[1].SlopeDegrees, precision: 6);
        });
    }

    [Fact]
    public void AClickInTheMiddleOfARoofIsNotAnEdge()
    {
        OnUiThread(() =>
        {
            var (_, roof, plan) = Roofed(RoofForm.Hip);

            // Away from every edge: this is a move, not a change of shape.
            Assert.False(plan.ToggleRoofEdgeAt(new Point2D(5000, 3000)));
            Assert.Equal(RoofForm.Hip, roof.Form);
            Assert.False(plan.History!.CanUndo);
        });
    }
}
