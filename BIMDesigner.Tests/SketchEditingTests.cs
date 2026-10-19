using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// A sketch's lines changed as Revit's Modify panel changes them: a corner dragged, the lines
/// there following; lines moved, mirrored and aligned; a corner rounded off with an arc.
/// </summary>
[Collection("Wpf")]
public class SketchEditingTests
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

    /// <summary>A floor's sketch with a 4 m by 3 m rectangle drawn in it.</summary>
    private static PlanView Sketched()
    {
        var document = BimDocument.CreateDefault();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
        plan.SetTool(PlanTool.Floor);
        plan.SketchTool = RoofSketchTool.Rectangle;
        plan.SketchClick(new Point2D(0, 0));
        plan.SketchClick(new Point2D(4000, 3000));
        Assert.Equal(4, plan.SketchLines.Count);
        plan.SketchTool = RoofSketchTool.Modify;
        return plan;
    }

    private static double Area(PlanView plan) => Polygon2D.Area(RoofSketch.Check(plan.SketchLines.ToList()).Boundary);

    [Fact]
    public void ACornerIsDraggedAndTheLinesMeetingThereFollowIt() => OnUiThread(() =>
    {
        var plan = Sketched();

        Assert.True(plan.BeginSketchDragAt(new Point2D(4000, 3000)));
        Assert.True(plan.IsDraggingSketchCorner);
        plan.SketchDragTo(new Point2D(5000, 3000));
        Assert.True(plan.EndSketchDrag());

        // Still a closed outline: a trapezoid, 4 m wide at the foot, 5 at the head, 3 high.
        Assert.Equal(13.5e6, Area(plan), 0);
        Assert.Equal(2, plan.SketchLines.Count(line => line.Start.DistanceTo(new Point2D(5000, 3000)) < 1 || line.End.DistanceTo(new Point2D(5000, 3000)) < 1));

        // One step to undo.
        Assert.True(plan.UndoSketch());
        Assert.Equal(12e6, Area(plan), 0);
    });

    [Fact]
    public void ACornerIsRoundedOffWithAnArcOfTheRadiusGiven() => OnUiThread(() =>
    {
        var plan = Sketched();
        plan.SketchTool = RoofSketchTool.Fillet;
        plan.SketchFilletRadius = 1000;

        Assert.True(plan.SketchClick(new Point2D(2500, 0)));
        Assert.True(plan.SketchClick(new Point2D(0, 2000)));

        // The two lines cut back a metre from the corner, a quarter circle between them.
        var arc = Assert.Single(plan.SketchLines, line => line.IsArc);
        Assert.Equal(Math.Tan(Math.PI / 8), Math.Abs(arc.Bulge), 6);
        Assert.Contains(new[] { arc.Start, arc.End }, end => end.DistanceTo(new Point2D(1000, 0)) < 1e-6);
        Assert.Contains(new[] { arc.Start, arc.End }, end => end.DistanceTo(new Point2D(0, 1000)) < 1e-6);
        var check = RoofSketch.Check(plan.SketchLines.ToList());
        Assert.True(check.IsValid);

        // The corner's square metre less the quarter circle's π/4 of one is taken off - the arc built of 10° pieces, a little short of it.
        Assert.InRange(Polygon2D.Area(check.Boundary), 12e6 - (1e6 - Math.PI / 4 * 1e6) - 10000, 12e6 - (1e6 - Math.PI / 4 * 1e6) + 10000);
    });

    [Fact]
    public void SelectedLinesAreMovedMirroredAndAligned() => OnUiThread(() =>
    {
        var plan = Sketched();

        // The right-hand side picked and mirrored about the middle: a copy on the left, over the left side.
        plan.SketchClick(new Point2D(4000, 1500));
        Assert.Single(plan.SelectedSketchLines);
        plan.SketchTool = RoofSketchTool.Mirror;
        plan.SketchClick(new Point2D(2000, -1000));
        plan.SketchClick(new Point2D(2000, 4000));
        Assert.Equal(5, plan.SketchLines.Count);
        Assert.Equal(2, plan.SketchLines.Count(line => Math.Abs(line.Start.X) < 1e-6 && Math.Abs(line.End.X) < 1e-6));

        // That copy moved 500 to the left, then aligned back onto the left side.
        plan.SketchTool = RoofSketchTool.Move;
        plan.SketchClick(new Point2D(0, 0));
        plan.SketchClick(new Point2D(-500, 0));
        Assert.Single(plan.SketchLines, line => Math.Abs(line.Start.X + 500) < 1e-6 && Math.Abs(line.End.X + 500) < 1e-6);

        plan.SketchTool = RoofSketchTool.Align;
        plan.SketchClick(new Point2D(0, 1000));
        Assert.True(plan.SketchClick(new Point2D(-500, 1000)));
        Assert.Equal(2, plan.SketchLines.Count(line => Math.Abs(line.Start.X) < 1e-6 && Math.Abs(line.End.X) < 1e-6));
    });
}
