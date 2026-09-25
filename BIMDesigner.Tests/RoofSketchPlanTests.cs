using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// The roof sketch on the plan, driven the way a user drives it: the Roof tool opens a sketch,
/// walls are picked, Finish makes the roof - or refuses and says why - and Cancel leaves
/// nothing behind.
/// </summary>
[Collection("Wpf")]
public class RoofSketchPlanTests
{
    private const double Half = 165;

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

    /// <summary>A 10 m x 6 m box of exterior walls reaching the first floor, and a plan on it.</summary>
    private static (BimDocument Document, List<Wall> Walls, PlanView Plan) Box()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var ground = document.Levels[0];
        var first = document.Levels[1];

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000)
        };

        var walls = new List<Wall>();
        for (var i = 0; i < corners.Length; i++)
        {
            var wall = new Wall
            {
                Start = corners[i], End = corners[(i + 1) % corners.Length],
                TypeId = type.Id, LevelId = ground.Id, TopLevelId = first.Id
            };

            document.Add(wall);
            walls.Add(wall);
        }

        var plan = new PlanView
        {
            Document = document,
            ActiveLevelId = ground.Id,
            History = new UndoStack(),
            ActiveRoofTypeId = document.TypesOf<RoofType>().First().Id
        };

        return (document, walls, plan);
    }

    /// <summary>Just outside the middle of each wall of the box - where a roof line is picked from.</summary>
    private static readonly Point2D[] OutsideEach =
    {
        new(5000, -250), new(10250, 3000), new(5000, 6250), new(-250, 3000)
    };

    [Fact]
    public void TheRoofToolOpensASketchInsteadOfPlacingARoof()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Box();

            plan.SetTool(PlanTool.Roof);

            Assert.True(plan.IsSketching);
            Assert.Equal(RoofSketchTool.PickWalls, plan.SketchTool);
            Assert.Empty(document.Elements.OfType<Roof>());
        });
    }

    [Fact]
    public void PickingFourWallsAndFinishingMakesARoofSittingOnThem()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);
            plan.SketchOverhang = 500;

            foreach (var point in OutsideEach) Assert.True(plan.SketchClick(point));

            Assert.Equal(4, plan.SketchLines.Count);
            Assert.True(plan.FinishSketch());

            var roof = Assert.Single(document.Elements.OfType<Roof>());
            Assert.False(plan.IsSketching);

            // A hip roof, since every picked line slopes, on the outside faces plus the overhang.
            Assert.Equal(RoofForm.Hip, roof.Form);
            var grow = Half + 500;
            Assert.Equal((10000 + 2 * grow) * (6000 + 2 * grow), roof.Area, precision: 0);

            // It sits on the walls: its underside where it bears is the top of the walls.
            Assert.Equal(document.Walls.First().GetTopElevation(document), roof.GetBottomElevation(document), precision: 6);

            // And every edge remembers its wall, so it will follow it.
            Assert.All(roof.Edges, edge => Assert.NotNull(edge.WallId));

            // Finishing is one thing to undo.
            plan.History!.Undo();
            Assert.Empty(document.Elements.OfType<Roof>());
        });
    }

    [Fact]
    public void TabPicksTheWholeChainOfWallsInOneClick()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);

            plan.SketchHover(OutsideEach[0]);
            Assert.True(plan.SketchTab());
            Assert.True(plan.SketchClick(OutsideEach[0]));

            // All four walls, all on the outside, closed into a loop by themselves.
            Assert.Equal(4, plan.SketchLines.Count);
            Assert.True(plan.FinishSketch());

            var roof = Assert.Single(document.Elements.OfType<Roof>());
            Assert.Equal((10000 + 2 * Half) * (6000 + 2 * Half), roof.Area, precision: 0);
        });
    }

    [Fact]
    public void AnOpenSketchIsNotFinishedAndStaysOpenToBePutRight()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);

            foreach (var point in OutsideEach.Take(3)) plan.SketchClick(point);

            Assert.False(plan.FinishSketch());
            Assert.True(plan.IsSketching);
            Assert.Empty(document.Elements.OfType<Roof>());

            // Picking the last wall closes it, and then it finishes.
            plan.SketchClick(OutsideEach[3]);
            Assert.True(plan.FinishSketch());
            Assert.Single(document.Elements.OfType<Roof>());
        });
    }

    [Fact]
    public void CancelLeavesNothingBehind()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);
            foreach (var point in OutsideEach) plan.SketchClick(point);

            plan.CancelSketch();

            Assert.False(plan.IsSketching);
            Assert.Empty(document.Elements.OfType<Roof>());
            Assert.False(plan.History!.CanUndo);
            Assert.Equal(PlanTool.Select, plan.ActiveTool);
        });
    }

    [Fact]
    public void UndoInASketchTakesBackTheLastLineNotTheModel()
    {
        OnUiThread(() =>
        {
            var (_, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);
            plan.SketchClick(OutsideEach[0]);
            plan.SketchClick(OutsideEach[1]);

            Assert.True(plan.UndoSketch());
            Assert.Single(plan.SketchLines);

            Assert.True(plan.RedoSketch());
            Assert.Equal(2, plan.SketchLines.Count);
        });
    }

    [Fact]
    public void AWallIsNotPickedTwice()
    {
        OnUiThread(() =>
        {
            var (_, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);

            Assert.True(plan.SketchClick(OutsideEach[0]));
            Assert.False(plan.SketchClick(OutsideEach[0]));
            Assert.Single(plan.SketchLines);
        });
    }

    [Fact]
    public void EditFootprintOpensTheRoofsSketchAndFinishingChangesIt()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);
            foreach (var point in OutsideEach) plan.SketchClick(point);
            plan.FinishSketch();

            var roof = document.Elements.OfType<Roof>().Single();
            plan.Select(roof);

            Assert.True(plan.EditFootprint());
            Assert.True(plan.IsSketching);
            Assert.Same(roof, plan.SketchedRoof);
            Assert.Equal(4, plan.SketchLines.Count);

            // Turn the two short ends into gables: select each and clear Defines slope.
            var ends = plan.SketchLines.Where(line => Math.Abs(line.Start.X - line.End.X) < 1).ToList();
            Assert.Equal(2, ends.Count);

            foreach (var end in ends)
            {
                plan.SketchTool = RoofSketchTool.Modify;
                plan.SketchClick(end.Start.MidpointTo(end.End));
                plan.ChangeSelectedSketchLines(edge => edge.DefinesSlope = false);
            }

            Assert.True(plan.FinishSketch());
            Assert.Equal(RoofForm.Gable, roof.Form);
            Assert.Single(document.Elements.OfType<Roof>());

            // And undo puts the hip back.
            plan.History!.Undo();
            Assert.Equal(RoofForm.Hip, roof.Form);
        });
    }

    [Fact]
    public void ARoofDrawnWithoutWallsGoesOnTheStoreyAbove()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);
            plan.SketchTool = RoofSketchTool.Rectangle;

            plan.SketchClick(new Point2D(20000, 0));
            plan.SketchClick(new Point2D(24000, 3000));

            Assert.Equal(4, plan.SketchLines.Count);
            Assert.True(plan.FinishSketch());

            var roof = document.Elements.OfType<Roof>().Single();

            // Nothing to bear on, so it goes up to the next level - where a roof usually is.
            Assert.Equal(document.Levels[1].Elevation, roof.GetBottomElevation(document), precision: 6);
            Assert.Equal(12e6, roof.Area, precision: 0);
        });
    }

    [Fact]
    public void DeletingASketchLineOpensTheOutlineAgain()
    {
        OnUiThread(() =>
        {
            var (_, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);
            foreach (var point in OutsideEach) plan.SketchClick(point);

            plan.SketchTool = RoofSketchTool.Modify;
            plan.SketchClick(new Point2D(5000, -Half));
            Assert.Single(plan.SelectedSketchLines);

            Assert.True(plan.DeleteSelectedSketchLines());
            Assert.Equal(3, plan.SketchLines.Count);
            Assert.False(plan.FinishSketch());
        });
    }

    [Fact]
    public void FinishingFromPickedWallsOffersToAttachThemAndAttachingClosesTheGables()
    {
        OnUiThread(() =>
        {
            var (document, walls, plan) = Box();
            Roof? offered = null;
            plan.RoofMadeFromWalls += (_, roof) => offered = roof;

            plan.SetTool(PlanTool.Roof);
            foreach (var point in OutsideEach) plan.SketchClick(point);
            Assert.True(plan.FinishSketch());

            var roof = document.Elements.OfType<Roof>().Single();
            Assert.Same(roof, offered);

            // Make it a gable: the short ends stop sloping.
            foreach (var index in Enumerable.Range(0, roof.Edges.Count))
            {
                var from = roof.Boundary[index];
                var to = roof.Boundary[(index + 1) % roof.Boundary.Count];
                if (Math.Abs(from.X - to.X) < 1) roof.Edges[index].DefinesSlope = false;
            }

            Assert.Equal(RoofForm.Gable, roof.Form);

            // Yes to the question: every picked wall's top is attached, as one step.
            Assert.Equal(4, plan.AttachPickedWalls(roof));
            Assert.All(walls, wall => Assert.Equal(roof.Id, wall.TopAttachedTo));

            // And the end walls now rise to the ridge.
            var east = walls[1];
            Assert.True(WallProfile.Of(document, east)!.Max(point => point.Y) > 4000);

            // Undo takes the attachment off again, and the gable ends are open as before.
            plan.History!.Undo();
            Assert.All(walls, wall => Assert.Null(wall.TopAttachedTo));
            Assert.Null(WallProfile.Of(document, east));
        });
    }

    [Fact]
    public void AnotherToolClosesTheSketchWithoutMakingAnything()
    {
        OnUiThread(() =>
        {
            var (document, _, plan) = Box();
            plan.SetTool(PlanTool.Roof);
            foreach (var point in OutsideEach) plan.SketchClick(point);

            plan.SetTool(PlanTool.Wall);

            Assert.False(plan.IsSketching);
            Assert.Empty(document.Elements.OfType<Roof>());
        });
    }
}
