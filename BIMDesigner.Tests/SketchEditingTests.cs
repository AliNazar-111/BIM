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
    [Fact]
    public void TheWholeOutlineIsRotatedAndScaledAndALineArrayed() => OnUiThread(() =>
    {
        var plan = Sketched();
        plan.SelectAllSketchLines();
        Assert.Equal(4, plan.SelectedSketchLines.Count);

        // A quarter turn anticlockwise about the corner at the origin, picked with three clicks: the 4 by 3 m
        // outline now stands up the plan, to the left of the origin.
        plan.SketchTool = RoofSketchTool.Rotate;
        Assert.Equal(4, plan.SelectedSketchLines.Count);
        plan.SketchClick(new Point2D(0, 0));
        plan.SketchClick(new Point2D(1000, 0));
        Assert.True(plan.SketchClick(new Point2D(0, 1000)));
        var turned = RoofSketch.Check(plan.SketchLines.ToList());
        Assert.True(turned.IsValid);
        Assert.Equal(12e6, Polygon2D.Area(turned.Boundary), 0);
        Assert.Equal((-3000.0, 4000.0), (Math.Round(turned.Boundary.Min(p => p.X), 6), Math.Round(turned.Boundary.Max(p => p.Y), 6)));

        // An angle typed turns it at the one click on its centre: 30° and back again.
        plan.SketchRotateAngle = 30;
        Assert.True(plan.SketchClick(new Point2D(0, 0)));
        Assert.NotEqual(-3000, RoofSketch.Check(plan.SketchLines.ToList()).Boundary.Min(p => p.X), 3);
        plan.SketchRotateAngle = -30;
        Assert.True(plan.SketchClick(new Point2D(0, 0)));
        Assert.Equal(-3000, RoofSketch.Check(plan.SketchLines.ToList()).Boundary.Min(p => p.X), 6);

        // Halved about the origin: a quarter of the area, still closed; undone, whole again.
        plan.SketchTool = RoofSketchTool.Scale;
        plan.SketchScaleFactor = 0.5;
        Assert.True(plan.SketchClick(new Point2D(0, 0)));
        Assert.Equal(3e6, Area(plan), 0);
        plan.UndoSketch();
        Assert.Equal(12e6, Area(plan), 0);

        // Rotate with Copy: the copies turned, the outline left where it is.
        plan.SketchRotateCopy = true;
        plan.SketchTool = RoofSketchTool.Rotate;
        plan.SelectAllSketchLines();
        plan.SketchRotateAngle = 90;
        Assert.True(plan.SketchClick(new Point2D(0, 0)));
        Assert.Equal(8, plan.SketchLines.Count);
        Assert.Equal(4, plan.SelectedSketchLines.Count);
    });

    [Fact]
    public void ALineIsArrayedAsManyTimesAsTheCount() => OnUiThread(() =>
    {
        var plan = Sketched();

        // The left side picked and repeated 1 m on twice: three in all, at x 0, 1 and 2 m - the copies selected.
        plan.SketchClick(new Point2D(0, 1500));
        Assert.Single(plan.SelectedSketchLines);
        plan.SketchTool = RoofSketchTool.Array;
        plan.SketchArrayCount = 3;
        plan.SketchClick(new Point2D(0, 0));
        Assert.True(plan.SketchClick(new Point2D(1000, 0)));
        Assert.Equal(6, plan.SketchLines.Count);
        foreach (var x in new[] { 1000.0, 2000.0 })
            Assert.Single(plan.SketchLines, line => Math.Abs(line.Start.X - x) < 1e-6 && Math.Abs(line.End.X - x) < 1e-6);
        Assert.Equal(2, plan.SelectedSketchLines.Count);
    });

    [Fact]
    public void ALineMovedOffTheWallItWasPickedFromLetsGoOfIt() => OnUiThread(() =>
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(each => each.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        var plan = new PlanView { Document = document, ActiveLevelId = level, History = new UndoStack() };
        plan.SetTool(PlanTool.Roof);
        plan.SketchTool = RoofSketchTool.PickWalls;
        Assert.True(plan.SketchClick(new Point2D(3000, -250)));
        var picked = Assert.Single(plan.SketchLines);
        Assert.Equal(wall.Id, picked.Edge.WallId);

        // Copied 2 m down the plan: the copy is drawn where it is put - finished, it is not put back on the wall.
        plan.SketchTool = RoofSketchTool.Modify;
        plan.SketchClick(picked.Middle);
        plan.SketchMoveCopy = true;
        plan.SketchTool = RoofSketchTool.Move;
        plan.SketchClick(new Point2D(3000, -3000));
        Assert.True(plan.SketchClick(new Point2D(3000, -5000)));
        Assert.Equal(2, plan.SketchLines.Count);
        Assert.Equal(wall.Id, picked.Edge.WallId);
        Assert.Null(plan.SketchLines.Single(line => !ReferenceEquals(line, picked)).Edge.WallId);
    });

    [Fact]
    public void ARoofLinePickedOffAFloorsEdgeWithLockFollowsIt() => OnUiThread(() =>
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) });
        document.Add(floor);
        var plan = new PlanView { Document = document, ActiveLevelId = level, History = new UndoStack() };
        plan.SetTool(PlanTool.Roof);
        plan.SketchTool = RoofSketchTool.PickLines;
        plan.SketchLock = true;
        foreach (var at in new[] { new Point2D(3000, 0), new Point2D(6000, 2000), new Point2D(3000, 4000), new Point2D(0, 2000) })
            Assert.True(plan.SketchClick(at));
        Assert.All(plan.SketchLines, line => Assert.Equal(floor.Id, line.Edge.SlabId));
        Assert.True(plan.FinishSketch());

        // The floor's right-hand edge 2 m further out: the roof's goes with it.
        var roof = document.All<Roof>().Single();
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 4000), new Point2D(0, 4000) });
        Assert.True(RoofSketch.FollowWalls(document));
        Assert.Equal(8000, roof.Boundary.Max(point => point.X), 6);
        Assert.Equal(32e6, roof.Area, 0);
    });
    [Fact]
    public void ALineIsArrayedRoundACentre() => OnUiThread(() =>
    {
        var plan = Sketched();

        // The right-hand side, (4000,0) to (4000,3000), four in all round the origin: a quarter turn apart.
        plan.SketchClick(new Point2D(4000, 1500));
        plan.SketchTool = RoofSketchTool.Array;
        (plan.SketchArrayRadial, plan.SketchArrayCount, plan.SketchArrayAngle) = (true, 4, 360);
        Assert.True(plan.SketchClick(new Point2D(0, 0)));
        Assert.Equal(7, plan.SketchLines.Count);
        bool Has(Point2D a, Point2D b) => plan.SketchLines.Any(line => line.Start.DistanceTo(a) < 1e-6 && line.End.DistanceTo(b) < 1e-6 || line.Start.DistanceTo(b) < 1e-6 && line.End.DistanceTo(a) < 1e-6);
        Assert.True(Has(new Point2D(0, 4000), new Point2D(-3000, 4000)));
        Assert.True(Has(new Point2D(-4000, 0), new Point2D(-4000, -3000)));
        Assert.True(Has(new Point2D(0, -4000), new Point2D(3000, -4000)));
        Assert.Equal(3, plan.SelectedSketchLines.Count);

        // Undone; then three over a quarter turn: the last a quarter round from the first, one between at 45°.
        plan.UndoSketch();
        plan.SketchTool = RoofSketchTool.Modify;
        plan.SketchClick(new Point2D(4000, 1500));
        plan.SketchTool = RoofSketchTool.Array;
        (plan.SketchArrayCount, plan.SketchArrayAngle) = (3, 90);
        Assert.True(plan.SketchClick(new Point2D(0, 0)));
        Assert.Equal(6, plan.SketchLines.Count);
        Assert.True(Has(new Point2D(0, 4000), new Point2D(-3000, 4000)));
        var half = Math.Sqrt(0.5);
        Assert.True(Has(new Point2D(4000 * half, 4000 * half), new Point2D((4000 - 3000) * half, (4000 + 3000) * half)));
    });
    [Fact]
    public void APictureIsScaledToItsTrueSizeByTwoPointsClicked() => OnUiThread(() =>
    {
        // A 2000 by 1000 pixel picture at 10 mm a pixel; its scale bar's ends clicked 500 mm apart, 25 m in truth: 50 times bigger,
        // about the first point - its corner, which stays where it is. One step to undo.
        var document = BimDocument.CreateDefault();
        var picture = ImageUnderlay.From("site.png", ImageUnderlayTests.Png(2000, 1000), document.Levels[0].Id)!;
        document.Add(picture);
        var history = new UndoStack();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = history };
        plan.ImageScaleLength = 25_000;
        Assert.True(plan.BeginImageScale());
        Assert.True(plan.IsScalingImage);
        plan.ImageScaleAt(new Point2D(0, 0));
        plan.ImageScaleAt(new Point2D(500, 0));
        Assert.False(plan.IsScalingImage);
        Assert.Equal(500, picture.Scale, 9);
        Assert.Equal(new Point2D(0, 0), picture.Location);
        history.Undo();
        Assert.Equal(10, picture.Scale, 9);

        // Esc stops it half way.
        plan.BeginImageScale(picture);
        plan.ImageScaleAt(new Point2D(0, 0));
        Assert.True(plan.CancelPendingOperation());
        Assert.False(plan.IsScalingImage);
    });
}
