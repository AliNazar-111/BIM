using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedBldgElements;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Stairs drawn along their walking line, turning wherever they were clicked; the three-quarter
/// turn; a stair repeated up every storey of a stairwell; and the rules for landings - how far a
/// stair may rise between them, how far it may climb without turning, and the doors round them.
/// </summary>
[Collection("Wpf")]
public class StairPathTests
{
    private static Stair Drawn(BimDocument document, params (double X, double Y)[] points)
    {
        var stair = Put(document, points[0].X, points[0].Y);
        StairGrips.SetPath(stair, points.Select(point => new Point2D(point.X, point.Y)).ToList());
        return stair;
    }

    [Fact]
    public void ADrawnStairTurnsWhereItWasClickedOnLandingsAsWideAsItIs()
    {
        var (document, floor) = House();

        // Up, a right angle right, then a right angle left: two landings, each a 900 square at its corner.
        var stair = Drawn(document, (1000, 600), (1000, 2000), (3000, 2000), (3000, 4300));
        Assert.Equal(StairShape.Custom, stair.Shape);
        Assert.Equal(new[] { 90.0, -90.0 }, stair.PathTurns);
        var layout = Stairs.Layout(document, stair);
        var landings = layout.Steps.Where(step => step.Kind == StairStepKind.Landing).ToList();
        Assert.Equal(2, landings.Count);
        Assert.All(landings, landing => Assert.Equal(900 * 900, Polygon2D.Area(landing.Outline), 0));
        Assert.Equal(layout.Risers - 1, layout.Steps.Count);
        Assert.Equal(4, layout.PathPoints.Count);

        // Each flight has its share of the 12 treads: as its stretch as clicked, less half a landing at each turn - 950, 1100, 1850.
        Assert.Equal(new[] { 3, 3, 6 }, Flights(layout));

        // It meets Approved Document K, cuts its floor, and turns the other way when flipped.
        Assert.Empty(Stairs.Problems(document, stair));
        Assert.Single(Stairs.Holes(document, floor));
        StairGrips.Flip(document, stair);
        Assert.Equal(-1, Stairs.Layout(document, stair).Steps.Single(step => step.Number == landings[0].Number + 1).Up.X, 6);
        StairGrips.Flip(document, stair);

        // On winders, three round a newel at each right angle.
        stair.Corner = StairCorner.Winders;
        layout = Stairs.Layout(document, stair);
        Assert.Equal((6, 2), (layout.Steps.Count(step => step.Kind == StairStepKind.Winder), layout.Newels.Count));

        // Its turns snap to 15°, a right angle at most; a straight on is a landing part way up a straight run.
        var angled = Drawn(document, (1200, 600), (1200, 2300), (3200, 3300), (5200, 3300));
        Assert.Equal(new[] { 60.0, 30.0 }, angled.PathTurns);
        var straight = Drawn(document, (3000, 400), (3000, 2200), (3000, 4700));
        Assert.Equal(new[] { 0.0 }, straight.PathTurns.Select(Math.Abs));
        Assert.Single(Stairs.Layout(document, straight).Steps, step => step.Kind == StairStepKind.Landing);
    }

    /// <summary>How many treads each flight has, foot to head: a flight ends at each landing or winder.</summary>
    private static int[] Flights(StairLayout layout)
    {
        var counts = new List<int> { 0 };
        foreach (var step in layout.Steps.OrderBy(step => step.Number))
        {
            if (step.Kind == StairStepKind.Tread) counts[^1]++;
            else if (counts[^1] > 0) counts.Add(0);
        }

        return counts.Where(count => count > 0).ToArray();
    }

    [Fact]
    public void ItsWalkingLineIsReshapedByItsHandlesSavedCopiedAndExported()
    {
        var (document, _) = House();
        var stair = Drawn(document, (1000, 600), (1000, 2000), (3000, 2000), (3000, 4300));
        var layout = Stairs.Layout(document, stair);

        // Its first turn dragged on up: still turning right, on a longer first stretch.
        var turn = StairGrips.Of(document, stair).Single(grip => grip.Kind == StairGripKind.PathPoint && grip.Index == 1);
        var before = StairState.Of(stair);
        StairGrips.Drag(document, stair, turn, turn.At + new Vector2D(0, 500), 0);
        Assert.Equal(new[] { 90.0, -90.0 }, stair.PathTurns);
        Assert.True(stair.PathLengths[0] > layout.PathPoints[1].DistanceTo(layout.PathPoints[0]) + 400);
        var after = StairState.Of(stair);
        var edit = new StairEditCommand(stair, before, after, "Reshape Stair");
        edit.Undo();
        Assert.Equal(before, StairState.Of(stair));
        edit.Redo();
        Assert.Equal(after, StairState.Of(stair));

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Stair>().Single();
        Assert.Equal(StairShape.Custom, loaded.Shape);
        Assert.Equal(stair.PathLengths, loaded.PathLengths);
        Assert.Equal(stair.PathTurns, loaded.PathTurns);
        var copy = (Stair)ElementCopy.Clone(stair)!;
        Assert.Equal(stair.PathTurns, copy.PathTurns);
        copy.PathTurns[0] = 45;
        Assert.Equal(90, stair.PathTurns[0]);

        // In IFC, by its turns: right then left is two quarter turns.
        using var model = IfcExport.Build(document);
        Assert.Equal(IfcStairTypeEnum.TWO_QUARTER_TURN_STAIR, Assert.Single(model.Instances.OfType<IfcStair>()).PredefinedType);
    }

    [Fact]
    public void AThreeQuarterTurnHasThreeEqualFlights()
    {
        var (document, _) = House();
        var stair = Put(document, 2000, 800, shape: StairShape.ThreeQuarterTurn);
        var layout = Stairs.Layout(document, stair);

        // Up, across to the right, and back down the plan beside the first.
        Assert.Equal(2, layout.Steps.Count(step => step.Kind == StairStepKind.Landing));
        Assert.Equal(new[] { 4, 4, 4 }, Flights(layout));
        Assert.Equal(-1, layout.Steps.Last().Up.Y, 6);
        Assert.Empty(Stairs.Problems(document, stair));

        stair.Corner = StairCorner.Winders;
        Assert.Equal(6, Stairs.Layout(document, stair).Steps.Count(step => step.Kind == StairStepKind.Winder));
        using var model = IfcExport.Build(document);
        Assert.Equal(IfcStairTypeEnum.THREE_QUARTER_WINDING_STAIR, Assert.Single(model.Instances.OfType<IfcStair>()).PredefinedType);
    }

    [Fact]
    public void ItRepeatsUpEveryStoreyAsFarAsItIsTold()
    {
        var (document, floor) = House();
        var third = new Level { Name = "Level 3", Elevation = 7000 };
        document.AddLevel(third);
        var upper = new Floor { LevelId = third.Id, TypeId = floor.TypeId };
        upper.SetBoundary(floor.Boundary.ToList());
        document.Add(upper);

        var stair = Put(document, 3000, 600, shape: StairShape.QuarterTurn);
        Assert.Single(Stairs.Flights(document, stair));
        Assert.Contains(stair.GetInstanceParameters(document), parameter => parameter.Definition == StairParameters.RepeatTo);
        stair.RepeatToLevelId = third.Id;

        // A flight for each storey, laid out from that storey's floor to the next: 4000 high, 20 risers.
        var flights = Stairs.Flights(document, stair);
        Assert.Equal(2, flights.Count);
        Assert.Equal((document.Levels[1].Id, (Guid?)third.Id, 20), (flights[1].LevelId, flights[1].TopLevelId, flights[1].Layout.Risers));
        Assert.Single(Stairs.Holes(document, floor));
        Assert.Single(Stairs.Holes(document, upper));

        // Built on both storeys, each on its own level.
        var meshes = StairModel.Meshes(document, stair);
        Assert.Contains(meshes, mesh => mesh.LevelId == document.Levels[0].Id);
        Assert.Contains(meshes, mesh => mesh.LevelId == document.Levels[1].Id);

        // The guarding round where it comes up to the middle floor leaves the way on up open.
        var goingOn = flights[1].Layout.Steps.Where(step => step.Number <= 3).ToList();
        foreach (var railing in StairModel.Guarding(document, stair, flights[0].Layout, flights[1].Layout.Steps))
            Assert.DoesNotContain(railing.Points.Zip(railing.Points.Skip(1)), pair => goingOn.Any(step => Polygon2D.Contains(step.Outline, pair.First.At.MidpointTo(pair.Second.At))));

        // What is wrong only further up is said with the storey it goes up from.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        stair.Shape = StairShape.Straight;
        stair.Start = new Point2D(3000, 300);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.StartsWith($"Up from {document.Levels[1].Name}: It rises") && problem.Contains("3835 (151 in)"));

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(third.Id, loaded.Elements.OfType<Stair>().Single().RepeatToLevelId);
    }

    [Fact]
    public void ItsLandingsAreCheckedForHowFarItRisesAndClimbsAndForDoors()
    {
        var (document, _) = House();

        // The IRC: no more than 3835 between floors and landings - unless a landing breaks it.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        var tall = Put(document, 3000, 300);
        (tall.TopLevelId, tall.UnconnectedHeight) = (null, 4000);
        Assert.Contains(Stairs.Problems(document, tall), problem => problem.Contains("rises 4000 with no landing"));
        StairGrips.SetPath(tall, new[] { new Point2D(3000, 300), new Point2D(3000, 2600), new Point2D(3000, 5000) });
        Assert.DoesNotContain(Stairs.Problems(document, tall), problem => problem.Contains("with no landing"));
        document.Remove(tall);

        // Approved Document K: a change of direction after 36 risers - a landing straight on is no turn.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        var climb = Drawn(document, (3000, 300), (3000, 4000), (3000, 9000));
        (climb.TopLevelId, climb.UnconnectedHeight) = (null, 7400);
        Assert.Contains(Stairs.Problems(document, climb), problem => problem.Contains("without turning"));
        document.Remove(climb);

        // A door in the wall at its foot, swinging in over its first steps.
        var wall = document.Walls.Single(candidate => candidate.Start == new Point2D(0, 0));
        var door = new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Operation == DoorOperation.Swing).Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
        document.Add(door);
        if (Stairs.DoorSwings(document, 0).Single().Average(point => point.Y) < 0) door.FlipFacing = true;
        var stair = Put(document, 3000, 600);
        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("within 400 of its bottom step"));
        Assert.Contains(problems, problem => problem.Contains("swings over its steps"));

        // Its foot 2000 back from the wall: clear.
        stair.Start = new Point2D(3000, 2000);
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("door", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheStairToolDrawsOneAlongThePointsClicked()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _) = House();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, NewStairShape = StairShape.Custom, History = new UndoStack() };
                plan.SetTool(PlanTool.Stair);

                Assert.True(plan.ClickStair(new Point2D(1000, 600)));
                Assert.True(plan.ClickStair(new Point2D(1000, 2000)));
                Assert.True(plan.ClickStair(new Point2D(3200, 2000)));
                Assert.Empty(document.Elements.OfType<Stair>());

                // Drawn as it goes, laid out along what has been clicked.
                var host = new System.Windows.Controls.Border { Width = 600, Height = 500, Child = plan };
                host.Measure(new Size(600, 500));
                host.Arrange(new Rect(0, 0, 600, 500));
                new System.Windows.Media.Imaging.RenderTargetBitmap(600, 500, 96, 96, System.Windows.Media.PixelFormats.Pbgra32).Render(host);
                host.Child = null;

                // Clicked again where it ends: finished.
                Assert.True(plan.ClickStair(new Point2D(3200, 2000)));
                var stair = Assert.Single(document.Elements.OfType<Stair>());
                Assert.Equal((StairShape.Custom, 90.0), (stair.Shape, Assert.Single(stair.PathTurns)));
                Assert.Equal(new Point2D(1000, 600), stair.Start);

                // Or with a double-click; Esc forgets what was clicked.
                plan.ClickStair(new Point2D(4500, 600));
                plan.ClickStair(new Point2D(4500, 3000));
                Assert.True(plan.FinishStair());
                Assert.Equal(2, document.Elements.OfType<Stair>().Count());
                plan.ClickStair(new Point2D(2000, 4000));
                plan.CancelPendingOperation();
                Assert.False(plan.FinishStair());
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
