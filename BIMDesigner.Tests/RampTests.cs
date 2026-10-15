using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Ramps: laid out from how high they rise - their flights no longer than their gradient allows
/// under Approved Document M or the ADA, landings between and at their turns and head - straight,
/// turning or doubling back, or drawn; checked for their gradient, width, rise, landings,
/// handrails, kerbs and guarding; built, drawn, put in by the tool, saved, scheduled and exported.
/// </summary>
[Collection("Wpf")]
public class RampTests
{
    private static Ramp Put(BimDocument document, double rise = 450, double gradient = 15, double width = 1200,
        RailingBuilding building = RailingBuilding.Dwelling, RampShape shape = RampShape.Straight)
    {
        var ramp = new Ramp
        {
            LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RampType>().First().Id, Start = new Point2D(0, 0), Angle = 90,
            UnconnectedHeight = rise, Gradient = gradient, Width = width, Building = building, Shape = shape
        };
        document.Add(ramp);
        return ramp;
    }

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
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void ADwellingsRampIsLaidOutInFlightsNoLongerThanItsGradientAllows()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document);
        var layout = Ramps.Layout(document, ramp);

        // 450 at 1:15 is 6.75 m of slope: at 1:15 a flight goes 5 m at most, so two of 3.375 m, a 1200 landing between, and one at its head.
        Assert.Equal(new[] { RampPieceKind.Flight, RampPieceKind.Landing, RampPieceKind.Flight, RampPieceKind.Landing }, layout.Pieces.Select(piece => piece.Kind));
        Assert.All(layout.Flights, flight => Assert.Equal(3375, flight.Going, 6));
        Assert.All(layout.Flights, flight => Assert.Equal(225, flight.Rise, 6));
        Assert.Equal(1200, layout.Pieces[1].Going, 6);
        Assert.Equal(1200, layout.Pieces[3].Going, 6);
        Assert.Equal(450, layout.Top, 6);
        Assert.Equal(0, layout.Head.X, 6);
        Assert.Equal(9150, layout.Head.Y, 6);
        Assert.Equal("Meets Approved Document M, Volume 1", Ramps.Verdict(document, ramp));

        // Gentler, its flights may be longer: at 1:20, one flight of 9 m.
        ramp.Gradient = 20;
        Assert.Single(Ramps.Layout(document, ramp).Flights);
    }

    [Fact]
    public void InAnotherBuildingItsFlightsAreShorterAndItsLandingsLongerAndPassingPlacesAtThree()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, gradient: 12, width: 1500, building: RailingBuilding.Other);
        var layout = Ramps.Layout(document, ramp);

        // At 1:12 a flight goes 2 m at most: 450 is three flights of 1.8 m.
        Assert.Equal(3, layout.FlightCount);
        Assert.All(layout.Flights, flight => Assert.Equal(1800, flight.Going, 6));

        // Three flights: the landings between are passing places, 1800 long and 1800 wide; its head landing 1200.
        var between = layout.Pieces.Where((piece, i) => piece.Kind == RampPieceKind.Landing && i < layout.Pieces.Count - 1).ToList();
        Assert.Equal(2, between.Count);
        Assert.All(between, landing => Assert.Equal(1800, landing.Going, 6));
        Assert.All(between, landing => Assert.Equal(1800, landing.Outline.Max(point => point.X) - landing.Outline.Min(point => point.X), 6));
        Assert.Equal(1200, layout.Pieces[^1].Going, 6);

        // At 1:15, 5 m flights: two, with a 1500 landing.
        ramp.Gradient = 15;
        layout = Ramps.Layout(document, ramp);
        Assert.Equal(2, layout.FlightCount);
        Assert.Equal(1500, layout.Pieces[1].Going, 6);
    }

    [Fact]
    public void UnderTheAdaARunRisesThirtyInchesAtMostWithLandingsSixtyInchesLong()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        var ramp = Put(document, rise: 1000, gradient: 12, width: 1000);
        ramp.TopLanding = Ramps.EndLanding(document);
        Ramps.TypeOf(document, ramp).HandrailExtension = 305;
        var layout = Ramps.Layout(document, ramp);

        Assert.Equal(2, layout.FlightCount);
        Assert.All(layout.Flights, flight => Assert.True(flight.Rise <= 760 + 1e-6));
        Assert.Equal(1525, layout.Pieces[1].Going, 6);
        Assert.Equal("Meets the ADA", Ramps.Verdict(document, ramp));

        // Steeper than 1:12 it is no ramp the ADA allows.
        ramp.Gradient = 10;
        Assert.Contains(Ramps.Problems(document, ramp), problem => problem.Contains("1:12 the ADA allows"));
    }

    [Fact]
    public void TurningItLandsSquareRoundEachTurnAndASwitchbackComesBackBesideItself()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, rise: 900, shape: RampShape.QuarterTurn);
        var layout = Ramps.Layout(document, ramp);

        // Up north, then east after a right turn on a landing as wide as it is long.
        Assert.Equal(new Vector2D(1, 0), layout.HeadUp);
        var turning = layout.Pieces.Single(piece => piece.Kind == RampPieceKind.Landing && layout.Walk.Skip(1).SkipLast(1).Any(turn => Polygon2D.Contains(piece.Outline, turn)));
        Assert.Equal(1200 * 1200, Polygon2D.Area(turning.Outline), 0);

        // Doubling back: its head beside its foot, coming back the way it went, the gap between its runs.
        ramp.Shape = RampShape.HalfTurn;
        layout = Ramps.Layout(document, ramp);
        Assert.Equal(new Vector2D(0, -1), new Vector2D(Math.Round(layout.HeadUp.X, 9), Math.Round(layout.HeadUp.Y, 9)));
        Assert.Equal(1200 + 300, layout.Head.X, 6);
        var back = layout.Pieces.Single(piece => piece.Kind == RampPieceKind.Landing && Polygon2D.Contains(piece.Outline, layout.Walk[1]));
        Assert.True(Polygon2D.Contains(back.Outline, layout.Walk[2]));
        Assert.Equal(2 * 1200 + 300, back.Outline.Max(point => point.X) - back.Outline.Min(point => point.X), 6);

        // Turning left, mirrored.
        ramp.TurnsLeft = true;
        Assert.True(Ramps.Layout(document, ramp).Head.X < 0);
    }

    [Fact]
    public void ADrawnRampLandsWhereverItTurnsAndRunsOnFromItsLastTurnAsFarAsItMust()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, rise: 900);
        Ramps.SetPath(ramp, new[] { new Point2D(0, 0), new Point2D(0, 4000), new Point2D(3000, 4000), new Point2D(3000, 5000) });
        var layout = Ramps.Layout(document, ramp);

        Assert.Equal(RampShape.Custom, ramp.Shape);
        Assert.Equal(13500, layout.Run, 6);
        Assert.Equal(900, layout.Top, 6);

        // North, east, then north again as far as it takes: past where it was clicked to.
        Assert.Equal(new Vector2D(0, 1), new Vector2D(Math.Round(layout.HeadUp.X, 9), Math.Round(layout.HeadUp.Y, 9)));
        Assert.True(layout.Head.Y > 5000);
        Assert.Equal(3000, layout.Head.X, 6);
    }

    [Fact]
    public void AnchoredByItsHeadItStaysAtTheDoorAsItIsMadeGentlerOrSteeper()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document);
        (ramp.Anchor, ramp.Start, ramp.Angle) = (RampAnchor.Head, new Point2D(5000, 2000), 180);

        var steep = Ramps.Layout(document, ramp);
        ramp.Gradient = 20;
        var gentle = Ramps.Layout(document, ramp);

        Assert.Equal(new Point2D(5000, 2000), steep.Head);
        Assert.Equal(new Point2D(5000, 2000), gentle.Head);
        Assert.Equal(new Vector2D(-1, 0), new Vector2D(Math.Round(gentle.HeadUp.X, 9), Math.Round(gentle.HeadUp.Y, 9)));
        Assert.True(gentle.Foot.X > steep.Foot.X);

        // Made to stay put by its foot instead: where its foot is now.
        Ramps.Reanchor(document, ramp, RampAnchor.Foot);
        Assert.Equal(gentle.Foot.X, ramp.Start.X, 6);
        Assert.Equal(new Point2D(5000, 2000).X, Ramps.Layout(document, ramp).Head.X, 6);
    }

    [Fact]
    public void ItIsCheckedForWhatItIsToldItsWidthItsRiseItsHandrailsAndItsKerb()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, rise: 700, width: 1200);

        // A dwelling's ramp rising more than 600, 1 m wide or more: handrails both sides, as Approved Document K asks.
        Assert.Equal((true, true), Ramps.Handrails(document, ramp, Ramps.Layout(document, ramp)));
        ramp.Railings = StairRailings.LeftSide;
        Assert.Contains(Ramps.Problems(document, ramp), problem => problem.Contains("both sides"));
        ramp.Railings = StairRailings.Auto;

        // Rising 600 or less it needs none.
        ramp.UnconnectedHeight = 450;
        Assert.Equal((false, false), Ramps.Handrails(document, ramp, Ramps.Layout(document, ramp)));

        // Steeper than 1:12, and narrower than 900.
        (ramp.Gradient, ramp.Width) = (10, 800);
        var problems = Ramps.Problems(document, ramp);
        Assert.Contains(problems, problem => problem.Contains("1:12 Approved Document M allows"));
        Assert.Contains(problems, problem => problem.Contains("900 clear"));

        // In another building: 1500 between its kerbs, handrails both sides, steps as well past 300, a lift past 2 m.
        (ramp.Gradient, ramp.Width, ramp.Building, ramp.UnconnectedHeight) = (15, 1200, RailingBuilding.Other, 2400);
        problems = Ramps.Problems(document, ramp);
        Assert.Contains(problems, problem => problem.Contains("1.5 m between its kerbs"));
        Assert.Contains(problems, problem => problem.Contains("a lift as well"));
        Assert.Contains(problems, problem => problem.Contains("signposted steps"));

        // A kerb lower than 100 on its open sides.
        var type = Ramps.TypeOf(document, ramp);
        type.KerbHeight = 50;
        Assert.Contains(Ramps.Problems(document, ramp), problem => problem.Contains("100 high on its open sides"));
    }

    [Fact]
    public void ItsLandingAtItsHeadIsAskedForUnlessItComesUpOntoAFloorAndClearOfADoorSwing()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, rise: 300);
        ramp.TopLanding = 0;
        Assert.Contains(Ramps.Problems(document, ramp), problem => problem.Contains("no landing"));

        // A deck at its head's height beyond it: it lands on that.
        var head = Ramps.Layout(document, ramp).Head;
        var deck = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id, HeightOffset = 300 };
        deck.SetBoundary(new[] { new Point2D(-2000, head.Y), new Point2D(2000, head.Y), new Point2D(2000, head.Y + 3000), new Point2D(-2000, head.Y + 3000) });
        document.Add(deck);
        Assert.DoesNotContain(Ramps.Problems(document, ramp), problem => problem.Contains("no landing"));

        // Its foot on nothing modelled: on the ground, taken to be there. Ground modelled lower than it: its foot is off it.
        Assert.DoesNotContain(Ramps.Problems(document, ramp), problem => problem.Contains("Its foot"));
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        ground.SetPoints(new[] { new Point3D(-5000, -5000, -300), new Point3D(5000, -5000, -300), new Point3D(5000, 5000, -300), new Point3D(-5000, 5000, -300) });
        document.Add(ground);
        Assert.Contains(Ramps.Problems(document, ramp), problem => problem.Contains("Its foot starts from no landing, off the floor or ground"));
        ramp.BaseOffset = -300;
        ramp.UnconnectedHeight = 600;
        Assert.DoesNotContain(Ramps.Problems(document, ramp), problem => problem.Contains("Its foot"));
    }

    [Fact]
    public void ItsHandrailsRunOnPastItsEndsAndItIsGuardedWhereItStandsHigh()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, rise: 900, width: 1500, building: RailingBuilding.Other);
        var layout = Ramps.Layout(document, ramp);

        // Both sides, 900 over its surface, running on 300 past its foot and its head.
        var runs = RampModel.HandrailRuns(document, ramp, layout);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.Equal(-300, run[0].Y, 6));
        Assert.All(runs, run => Assert.Equal(900, run[0].Z, 6));
        Assert.All(runs, run => Assert.Equal(layout.Head.Y + 300, run[^1].Y, 6));
        Assert.All(runs, run => Assert.Equal(1800, run[^1].Z, 6));

        // Nothing under it modelled: its sides drop to its foot's height - more than 380 up, guarded, 1100 round its landings.
        var guards = RampModel.Guards(document, ramp, layout);
        Assert.Contains(guards, guard => guard.Height == 1100);
        Assert.Contains(guards, guard => guard.Height == 900);
        Assert.All(guards, guard => Assert.True(guard.Points.Max(point => point.Height) > 380));

        // On the ground all the way up - an embankment built to it - nothing to guard.
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        ground.SetPoints(layout.Left.SelectMany(point => new[] { new Point3D(-5000, point.At.Y, point.Height), new Point3D(5000, point.At.Y, point.Height) }).Distinct()
            .Concat(new[] { new Point3D(-5000, -3000, 0), new Point3D(5000, -3000, 0), new Point3D(-5000, layout.Head.Y + 3000, 900), new Point3D(5000, layout.Head.Y + 3000, 900) }));
        document.Add(ground);
        Assert.Empty(RampModel.Guards(document, ramp, Ramps.Layout(document, ramp)));
    }

    [Fact]
    public void ItIsBuiltIn3DCutInSectionAndCountedInItsSchedule()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, rise: 900, building: RailingBuilding.Other, width: 1500);
        ramp.Mark = "R1";

        var meshes = RampModel.Meshes(document, ramp);
        Assert.NotEmpty(meshes);
        Assert.All(meshes, mesh => Assert.Equal(MeshKind.Ramp, mesh.Kind));
        Assert.Contains(ModelMeshBuilder.Build(document), mesh => mesh.ElementId == ramp.Id);

        // Its surface rises from its foot to its head.
        var top = RampModel.Parts(document, ramp).Pieces.SelectMany(piece => piece.Body.Positions).ToList();
        Assert.Equal(0, top.Where(point => Math.Abs(point.Y) < 1).Max(point => point.Z), 6);
        Assert.Equal(900, top.Max(point => point.Z), 6);

        var schedule = Schedule.Run(document, ScheduleDefinition.Defaults().Single(definition => definition.Name == "Ramp Schedule"));
        var row = Assert.Single(schedule.Rows);
        Assert.Equal("R1", row.Text("Mark"));
        Assert.Equal(900, Schedule.Numeric(row["Rise"])!.Value, 6);
    }

    [Fact]
    public void SavedAndOpenedItIsTheSameRampAndExportedItIsAnIfcRampOfFlightsAndLandings()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, rise: 900, shape: RampShape.HalfTurn);
        (ramp.Gap, ramp.TurnsLeft, ramp.TopLanding, ramp.Railings) = (500, true, 1500, StairRailings.BothSides);

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = Assert.Single(loaded.Elements.OfType<Ramp>());
        Assert.Equal((ramp.Shape, ramp.Gap, ramp.TurnsLeft, ramp.TopLanding, ramp.Railings, ramp.Gradient, ramp.UnconnectedHeight),
            (again.Shape, again.Gap, again.TurnsLeft, again.TopLanding, again.Railings, again.Gradient, again.UnconnectedHeight));
        Assert.Equal(Ramps.Layout(document, ramp).Head, Ramps.Layout(loaded, again).Head);
        Assert.Equal(document.TypesOf<RampType>().Count(), loaded.TypesOf<RampType>().Count());

        using var model = IfcExport.Build(document);
        var exported = Assert.Single(model.Instances.OfType<IfcRamp>());
        Assert.Equal(IfcRampTypeEnum.HALF_TURN_RAMP, exported.PredefinedType);
        Assert.Equal(Ramps.Layout(document, ramp).FlightCount, model.Instances.OfType<IfcRampFlight>().Count());
        Assert.Contains(model.Instances.OfType<IfcSlab>(), slab => slab.PredefinedType == IfcSlabTypeEnum.LANDING);
        Assert.Contains(model.Instances.OfType<IfcRailing>(), railing => railing.PredefinedType == IfcRailingTypeEnum.HANDRAIL);
    }

    [Fact]
    public void MovedTurnedMirroredOrCopiedItGoesWithItAndDeletingTheLevelItReachesKeepsItsRise()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document, rise: 900, shape: RampShape.QuarterTurn);
        var before = Ramps.Layout(document, ramp);

        ElementTransforms.Move(ramp, new Vector2D(1000, 0));
        Assert.Equal(before.Head.X + 1000, Ramps.Layout(document, ramp).Head.X, 6);

        // Mirrored across its own line: turning the other way.
        ElementTransforms.Mirror(ramp, new Line2D(new Point2D(1000, 0), new Vector2D(0, 1)));
        Assert.True(ramp.TurnsLeft);
        Assert.Equal(1000 - (before.Head.X), Ramps.Layout(document, ramp).Head.X, 6);

        var copy = Assert.IsType<Ramp>(ElementCopy.Clone(ramp));
        Assert.Equal(Ramps.Layout(document, ramp).Head, Ramps.Layout(document, copy).Head);

        // Up to the level above, which goes: it keeps its rise.
        var first = document.Levels[1];
        ramp.TopLevelId = first.Id;
        var rise = Ramps.Top(document, ramp) - Ramps.Bottom(document, ramp);
        var delete = new DeleteLevelCommand(document, first);
        delete.Redo();
        Assert.Null(ramp.TopLevelId);
        Assert.Equal(rise, Ramps.Top(document, ramp) - Ramps.Bottom(document, ramp), 6);
        delete.Undo();
        Assert.Equal(first.Id, ramp.TopLevelId);
    }

    [Fact]
    public void ItsHandlesMoveTurnAndWidenItAndRaiseItsHead()
    {
        var document = BimDocument.CreateDefault();
        var ramp = Put(document);
        var handles = ElementHandles.Of(document, ramp);

        ElementHandles.Drag(document, ramp, handles.Single(handle => handle.Key == "width"), new Point2D(900, 300), 0);
        Assert.Equal(1800, ramp.Width, 6);

        ElementHandles.Drag(document, ramp, handles.Single(handle => handle.Key == "turn"), new Point2D(1000, 0), 0);
        Assert.Equal(0, ramp.Angle, 6);

        ElementHandles.Drag(document, ramp, handles.Single(handle => handle.Key == "raise:head"), Ramps.Layout(document, ramp).Head, 600 + 300);
        Assert.Equal(600, Ramps.Top(document, ramp), 6);

        var snapshot = ElementHandles.Snapshot(ramp);
        ElementHandles.Drag(document, ramp, handles.Single(handle => handle.Key == "start"), new Point2D(5000, 5000), 0);
        ElementHandles.Restore(ramp, snapshot);
        Assert.Equal(new Point2D(0, 0), ramp.Start);
    }

    [Fact]
    public void ADoorWithARampsLandingOutsideItIsNotOverADrop()
    {
        var (document, _) = StairTests.House();
        var level = document.Levels[0];
        var inside = new Floor { LevelId = level.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        inside.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(inside);
        var ground = new Toposurface { LevelId = level.Id };
        ground.SetPoints(new[] { new Point3D(-20000, -20000, -900), new Point3D(20000, -20000, -900), new Point3D(20000, 20000, -900), new Point3D(-20000, 20000, -900) });
        document.Add(ground);
        var south = document.Walls.Single(wall => Math.Abs(wall.Start.Y) < 1 && Math.Abs(wall.End.Y) < 1);
        var face = document.GetWallType(south)!.Structure.TotalWidth / 2;
        Assert.Equal(900, OpeningGuarding.Drop(document, south, 3000), 6);

        // A ramp up to it from the garden, its head landing outside the door.
        var ramp = Put(document, rise: 900);
        (ramp.Anchor, ramp.Start, ramp.Angle, ramp.TopLevelId, ramp.TopOffset, ramp.BaseOffset) = (RampAnchor.Head, new Point2D(3000, -face), 90, level.Id, 0, -900);
        Assert.Equal(0, OpeningGuarding.Drop(document, south, 3000), 6);
    }

    [Fact]
    public void AnApproachTooSteepForAPathAloneIsRampedOver()
    {
        var document = BimDocument.CreateDefault();
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        ground.SetPoints(new[] { new Point3D(-3000, -1000, 0), new Point3D(3000, -1000, 0), new Point3D(-3000, 10000, 1100), new Point3D(3000, 10000, 1100) });
        document.Add(ground);
        var path = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Path, Approach = true };
        path.SetBoundary(GroundRegions.Band(new[] { new Point2D(0, 0), new Point2D(0, 8000) }, 1200));
        document.Add(path);
        Assert.Contains("Approved Document M", GroundRegions.Verdict(document, path));

        Put(document, rise: 600);
        Assert.Empty(GroundRegions.Problems(document, path));
        Assert.Contains("ramped", GroundRegions.Verdict(document, path));
    }

    [Fact]
    public void TheRampToolPutsOneUpFromTheGroundOrDownFromAFloorsEdgeToIt() => OnUi(() =>
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0];
        var floor = new Floor { LevelId = level.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(floor);
        var ground = new Toposurface { LevelId = level.Id };
        ground.SetPoints(new[] { new Point3D(-20000, -20000, -450), new Point3D(30000, -20000, -450), new Point3D(30000, 30000, -450), new Point3D(-20000, 30000, -450) });
        document.Add(ground);

        var plan = new PlanView { Document = document, ActiveLevelId = level.Id };
        plan.SetTool(PlanTool.Ramp);

        // From the floor's edge out over the ground: down from its head there to the ground, 450 below.
        Assert.True(plan.ClickRamp(new Point2D(6000, 2500)));
        Assert.True(plan.ClickRamp(new Point2D(9000, 2500)));
        var down = Assert.Single(document.Elements.OfType<Ramp>());
        var layout = Ramps.Layout(document, down);
        Assert.Equal(RampAnchor.Head, down.Anchor);
        Assert.Equal(6000, layout.Head.X, 6);
        Assert.Equal(-450, layout.Bottom, 6);
        Assert.Equal(floor.GetTopElevation(document), layout.Top, 6);
        Assert.True(layout.Foot.X > 6000);
        Assert.True(down.Outside);

        // From the ground toward the floor: up from its foot there to the floor.
        plan.NewRampDirection = StairDirection.Up;
        Assert.True(plan.ClickRamp(new Point2D(-12000, 2500)));
        Assert.True(plan.ClickRamp(new Point2D(-8000, 2500)));
        var up = document.Elements.OfType<Ramp>().Single(ramp => ramp != down);
        Assert.Equal(RampAnchor.Foot, up.Anchor);
        Assert.Equal(-450, Ramps.Bottom(document, up), 6);
        Assert.Equal(level.Elevation, Ramps.Top(document, up), 6);

        // A drawn one, along the points clicked.
        plan.NewRampShape = RampShape.Custom;
        Assert.True(plan.ClickRamp(new Point2D(-12000, 10000)));
        Assert.True(plan.ClickRamp(new Point2D(-12000, 14000)));
        Assert.True(plan.ClickRamp(new Point2D(-8000, 14000)));
        Assert.True(plan.FinishRamp());
        Assert.Contains(document.Elements.OfType<Ramp>(), ramp => ramp.Shape == RampShape.Custom && ramp.PathTurns.Count == 1);
    });
}
