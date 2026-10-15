using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.ProductExtension;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Handles on railings, beams and the ground, in plan and in 3D; beams cut back to the columns
/// and beams they land on, and said where nothing carries them; joist layouts, trimmed round a
/// stairwell; regions of the ground, a path to a door checked for its gradient; what is cut and
/// filled; and retaining walls stepping the ground.
/// </summary>
[Collection("Wpf")]
public class HandleAndSiteTests
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

    private static Beam Hang(BimDocument document, string type, (double X, double Y) from, (double X, double Y) to, int level = 1)
    {
        var beam = new Beam
        {
            LevelId = document.Levels[level].Id, TypeId = document.TypesOf<BeamType>().Single(candidate => candidate.Name == type).Id,
            Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y)
        };
        document.Add(beam);
        return beam;
    }

    private static Toposurface Ground(BimDocument document, Func<double, double, double> height, double from = -6000, double to = 12000, double step = 2000)
    {
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        var points = new List<Point3D>();
        for (var x = from; x <= to; x += step)
        for (var y = from; y <= to; y += step)
            points.Add(new Point3D(x, y, height(x, y)));
        ground.SetPoints(points);
        document.Add(ground);
        return ground;
    }

    [Fact]
    public void ARailingsCornersAreDraggedAddedAndDroppedInPlan() => OnUi(() =>
    {
        var (document, _) = House();
        var railing = new Railing { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<RailingType>().First().Id };
        railing.SetPath(new[] { new Point2D(1000, 1000), new Point2D(4000, 1000) });
        document.Add(railing);
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id, History = new UndoStack() };
        plan.SelectMany(new[] { railing });

        // Its end dragged; a "+" midway dragged out, a corner added there.
        Assert.True(plan.DragHandle("corner:1", new Point2D(5000, 1000)));
        Assert.Equal(new Point2D(5000, 1000), railing.Path[1]);
        Assert.True(plan.DragHandle("insert:0", new Point2D(3000, 2000)));
        Assert.Equal(new[] { new Point2D(1000, 1000), new Point2D(3000, 2000), new Point2D(5000, 1000) }, railing.Path);

        // That corner dropped on the next: gone again; and all of it, one step at a time, undone.
        Assert.True(plan.DragHandle("corner:1", new Point2D(5010, 1000)));
        Assert.Equal(2, railing.Path.Count);
        plan.History!.Undo();
        Assert.Equal(3, railing.Path.Count);
        plan.History.Undo();
        plan.History.Undo();
        Assert.Equal(new[] { new Point2D(1000, 1000), new Point2D(4000, 1000) }, railing.Path);
    });

    [Fact]
    public void ABeamsEndsAreDraggedInPlanAndRaisedIn3D() => OnUi(() =>
    {
        var (document, _) = House();
        var beam = Hang(document, "UB 203x133x25", (0, 2500), (6000, 2500));
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id, History = new UndoStack() };
        plan.SelectMany(new[] { beam });

        Assert.True(plan.DragHandle("end", new Point2D(6000, 3000)));
        Assert.Equal(new Point2D(6000, 3000), beam.End);

        // Its end raised in 3D, looking at it from the south: up its own line, to the line of sight's nearest.
        var handle = Assert.Single(plan.Handles3D(), grip => grip.Key == "handle:raise:end");
        Assert.True(plan.DragHandleIn3D(handle.Key, new Point3D(6000, -10000, handle.At.Z + 500), new Point3D(6000, 3000, handle.At.Z + 500)));
        plan.EndHandleIn3D();
        Assert.Equal(500, beam.EndOffset, 0);
        Assert.Equal(0, beam.StartOffset, 6);
    });

    [Fact]
    public void TheGroundsPointsAreMovedInPlanRaisedIn3DAndDroppedOnEachOther() => OnUi(() =>
    {
        var document = BimDocument.CreateDefault();
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        ground.SetPoints(new[] { new Point3D(0, 0, 0), new Point3D(10000, 0, 0), new Point3D(10000, 10000, 0), new Point3D(0, 10000, 0) });
        document.Add(ground);
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
        plan.SelectMany(new[] { ground });

        Assert.True(plan.DragHandle("point:2", new Point2D(12000, 12000)));
        Assert.Equal(new Point3D(12000, 12000, 0), ground.Points[2]);

        var raise = Assert.Single(plan.Handles3D(), grip => grip.Key == "handle:raise:0");
        // Its arrow, 300 over it, raised to 1100: the spot level to 800.
        Assert.True(plan.DragHandleIn3D(raise.Key, new Point3D(0, -20000, 1100), new Point3D(0, 0, 1100)));
        plan.EndHandleIn3D();
        Assert.Equal(800, ground.Points[0].Z, 0);

        // A point dropped onto another is taken out: three left.
        Assert.True(plan.DragHandle("point:3", new Point2D(20, 10000)));
        Assert.True(plan.DragHandle("point:3", new Point2D(0, 0)));
        Assert.Equal(3, ground.Points.Count);
    });

    [Fact]
    public void ABeamIsCutBackToTheColumnOrBeamItLandsOnAndSaidWhereNothingCarriesIt()
    {
        var (document, _) = House();
        var column = new Column { TypeId = document.TypesOf<ColumnType>().Single(type => type.Name == "Column - 300 x 300").Id, LevelId = document.Levels[0].Id, Location = new Point2D(3000, 2500), TopLevelId = document.Levels[1].Id };
        document.Add(column);

        // Onto a column: stopped at its face.
        var beam = Hang(document, "UB 254x146x31", (0, 2500), (3000, 2500));
        Assert.Equal(3000 - 150, Beams.Ends(document, beam).End.X, 6);
        Assert.Equal("Clear", Beams.Verdict(document, beam));

        // A joist framing into a beam's side: stopped at it; at a corner of two, neither cut.
        var girder = Hang(document, "UB 305x165x40", (0, 3500), (6000, 3500));
        var joist = Hang(document, "UB 203x133x25", (4000, 100), (4000, 3500));
        Assert.Equal(3500 - 165 / 2.0, Beams.Ends(document, joist).End.Y, 6);
        Assert.Equal(new Point2D(0, 3500), Beams.Ends(document, girder).Start);

        // Out in the room, nothing under either end.
        var loose = Hang(document, "UB 203x133x25", (1000, 1000), (2000, 1000));
        Assert.Contains("Its start rests on nothing", Beams.Verdict(document, loose));
        Assert.Contains("Its end rests on nothing", Beams.Verdict(document, loose));
    }

    [Fact]
    public void JoistsFillTheirOutlineAcrossTheShortWayAndAreTrimmedRoundAStairwell()
    {
        var (document, floor) = House();
        var system = new BeamSystem { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<BeamType>().Single(type => type.Name == "Timber C24 47x200 Joist").Id };
        system.SetBoundary(new[] { new Point2D(100, 100), new Point2D(5900, 100), new Point2D(5900, 4900), new Point2D(100, 4900) });
        document.Add(system);

        // 5800 by 4800: they span the 4800 way, 15 of them at 400.
        Assert.Equal(90, BeamSystems.Angle(system), 6);
        var joists = BeamSystems.Joists(document, system);
        Assert.Equal(15, joists.Count);
        Assert.All(joists, joist => Assert.Equal(4800, joist.From.DistanceTo(joist.To), 6));

        // A stair up through the floor they carry: the joists over its well stop short of it, and trimmers carry them.
        var stair = Put(document, 3000, 600);
        var hole = Assert.Single(Stairs.Holes(document, floor));
        joists = BeamSystems.Joists(document, system);
        Assert.DoesNotContain(joists, joist => Polygon2D.Contains(hole, joist.From.MidpointTo(joist.To)));
        var trimmers = joists.Where(joist => Math.Abs(joist.From.Y - joist.To.Y) < 1).ToList();
        Assert.Equal(2, trimmers.Count);
        Assert.Empty(Stairs.Problems(document, stair));

        // With no floor over the stair, joists across it are what its headroom is measured to.
        document.Remove(floor);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("under a joist"));
        Assert.All(BeamSystems.Meshes(document, system), mesh => Assert.Equal(system.Id, mesh.ElementId));
    }

    [Fact]
    public void ARegionLiesOnTheGroundAndAPathToADoorIsCheckedForItsGradient()
    {
        var document = BimDocument.CreateDefault();
        var ground = Ground(document, (_, y) => y * 0.1);
        var path = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Path, Approach = true };
        path.SetBoundary(GroundRegions.Band(new[] { new Point2D(0, 0), new Point2D(0, 6000) }, 1200));
        document.Add(path);

        // A strip 1200 wide, falling 1:10: too steep to be the way up to a door.
        Assert.Equal(1200 * 6000, Polygon2D.Area(path.Boundary), 0);
        Assert.Equal(0.1, GroundRegions.Steepest(document, path)!.Value, 6);
        Assert.Contains("Approved Document M allows an approach no steeper than 1:12", GroundRegions.Verdict(document, path));

        // Laid on the ground in its own colour, the ground round it in grass.
        var meshes = Terrains.Meshes(document, ground);
        Assert.Contains(meshes, mesh => mesh.ElementId == path.Id && mesh.Colour == GroundRegions.Colour(GroundSurface.Path));
        Assert.Equal(300, GroundRegions.HeightAt(document, path, new Point2D(0, 3000)), 6);

        // At 1:30, gently sloping: fine.
        ground.SetPoints(ground.Points.Select(point => point with { Z = point.Y / 30 }));
        Assert.Equal("Gently sloping enough for an approach", GroundRegions.Verdict(document, path));
    }

    [Fact]
    public void WhatIsCutAndFilledIsMeasuredFromTheGroundAsItWas()
    {
        var (document, _) = House();
        var ground = Ground(document, (_, _) => -150);
        Assert.Equal((0.0, 0.0), Terrains.CutAndFill(document, ground));

        // A ground floor slab, its underside 400 down: dug out under it, 6 m by 5 m by 250.
        var slab = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        slab.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(slab);
        var (cut, fill) = Terrains.CutAndFill(document, ground);
        Assert.InRange(cut / 1e9, 30 * (slab.GetBottomElevation(document) - -150) * -1e-3 * 0.95, 30 * (slab.GetBottomElevation(document) - -150) * -1e-3 * 1.05);
        Assert.Equal(0, fill, 0);

        // Recorded, then a point raised a metre: made up round it.
        new SetExistingGroundCommand(ground, record: true).Redo();
        ground.SetPoints(ground.Points.Select(point => point.X == 10000 && point.Y == 10000 ? point with { Z = 850 } : point));
        Assert.True(Terrains.CutAndFill(document, ground).Fill > 1e9);
        Assert.NotEmpty(Terrains.Contours(document, ground, existing: true).Concat(Terrains.Contours(document, ground)));
    }

    [Fact]
    public void ARetainingWallStepsTheGroundAndItsTopIsGuarded()
    {
        // Ground rising to the north; a retaining wall across it, 1200 high.
        var document = BimDocument.CreateDefault();
        var ground = Ground(document, (_, y) => y * 0.1);
        var type = document.TypesOf<WallType>().First().Duplicate("Retaining 300");
        type.Function = WallFunction.Retaining;
        document.AddType(type);
        var wall = new Wall { Start = new Point2D(-4000, 3000), End = new Point2D(10000, 3000), TypeId = type.Id, LevelId = document.Levels[0].Id, BaseOffset = 0, UnconnectedHeight = 1200 };
        document.Add(wall);

        // Held level with its top behind it, at its foot in front; nothing under it.
        var half = type.Structure.TotalWidth / 2;
        Assert.Equal(1200, Terrains.HeightAt(document, ground, new Point2D(3000, 3000 + half + 5))!.Value, 0);
        Assert.Equal(0, Terrains.HeightAt(document, ground, new Point2D(3000, 3000 - half - 5))!.Value, 0);
        Assert.Null(Terrains.HeightAt(document, ground, new Point2D(3000, 3000)));
        Assert.Equal(300, Terrains.HeightAt(ground, new Point2D(3000, 3000))!.Value, 6);

        // 1200 of drop off its top, with nothing along it: to be guarded; with a railing along it, not.
        Assert.Contains(Terrains.Problems(document, ground), problem => problem.Contains("holds back 1200"));
        var railing = new Railing { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RailingType>().First().Id };
        railing.SetPath(new[] { new Point2D(-4000, 3000), new Point2D(10000, 3000) });
        document.Add(railing);
        Assert.DoesNotContain(Terrains.Problems(document, ground), problem => problem.Contains("holds back"));

        // It stands on the wall's top.
        Assert.Equal(1200, Railings.Base(document, railing, new Point2D(3000, 3000)), 0);
    }

    /// <summary>The house with a balcony 2500 out off its first floor, along its east wall.</summary>
    private static (BimDocument Document, Floor Balcony) Balcony()
    {
        var (document, _) = House();
        var balcony = new Floor { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        balcony.SetBoundary(new[] { new Point2D(6000, 1000), new Point2D(8500, 1000), new Point2D(8500, 4000), new Point2D(6000, 4000) });
        document.Add(balcony);
        return (document, balcony);
    }

    [Fact]
    public void ARailingStopsEitherSideOfAStairComingUpThroughIt()
    {
        // A railing drawn along the balcony's south edge, and a stair from the garden up to that edge.
        var (document, _) = Balcony();
        var railing = new Railing { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<RailingType>().First().Id };
        railing.SetPath(new[] { new Point2D(6050, 1050), new Point2D(8450, 1050) });
        document.Add(railing);
        Assert.Single(Railings.Segments(document, railing));

        Put(document, 7250, 1000 - 14 * 225);
        var segments = Railings.Segments(document, railing);
        Assert.Equal(2, segments.Count);
        Assert.Equal((6800.0, 7700.0), (segments[0].To.X, segments[1].From.X));
        var posts = Railings.Posts(document, railing, Railings.TypeOf(document, railing));
        Assert.Contains(posts, post => post.DistanceTo(new Point2D(6800, 1050)) < 1);
        Assert.Contains(posts, post => post.DistanceTo(new Point2D(7700, 1050)) < 1);
        Assert.DoesNotContain(RailingModel.Meshes(document, railing).SelectMany(mesh => mesh.Positions), point => point.X > 6800 + 30 && point.X < 7700 - 30);
    }

    [Fact]
    public void EdgesPickedOneByOneJoinAtTheirCornersAndAllRoundCloseUp() => OnUi(() =>
    {
        var (document, balcony) = Balcony();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id, History = new UndoStack(), PickingEdges = true };
        plan.SetTool(PlanTool.Railing);

        // Its south edge, then its east and north: one railing, round two corners.
        Assert.True(plan.PickRailingEdge(new Point2D(7000, 1000)));
        Assert.True(plan.PickRailingEdge(new Point2D(8500, 2500)));
        Assert.True(plan.PickRailingEdge(new Point2D(7000, 4000)));
        var railing = Assert.Single(document.Elements.OfType<Railing>());
        Assert.Equal(new[] { new Point2D(6050, 1050), new Point2D(8450, 1050), new Point2D(8450, 3950), new Point2D(6050, 3950) }, railing.Path);

        // And the west, against the house: all four sides, all the way round.
        Assert.True(plan.PickRailingEdge(new Point2D(6000, 2500)));
        Assert.True(Assert.Single(document.Elements.OfType<Railing>()).Closed);
        plan.History!.Undo();
        Assert.False(railing.Closed);
        Assert.False(plan.PickRailingEdge(new Point2D(20000, 20000)));
        Assert.NotNull(balcony);
    });

    [Fact]
    public void AStairDrawnGoingDownHasItsHeadWhereItWasClicked() => OnUi(() =>
    {
        // From the balcony's edge, down to the garden.
        var (document, balcony) = Balcony();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id, NewStairGoesDown = true };
        plan.SetTool(PlanTool.Stair);
        Assert.True(plan.ClickStair(new Point2D(7250, 1000)));
        Assert.True(plan.ClickStair(new Point2D(7250, -2000)));

        var stair = Assert.Single(document.Elements.OfType<Stair>());
        Assert.Equal((document.Levels[0].Id, document.Levels[1].Id), (stair.LevelId, stair.TopLevelId!.Value));
        var layout = Stairs.Layout(document, stair);
        Assert.True(layout.TopEdge.From.MidpointTo(layout.TopEdge.To).DistanceTo(new Point2D(7250, 1000)) < 1);
        Assert.Equal(90, stair.Angle, 6);
        Assert.Equal(balcony.GetTopElevation(document), layout.Top, 6);

        // On the ground floor there is no storey below to go down to.
        plan.ActiveLevelId = document.Levels[0].Id;
        plan.ClickStair(new Point2D(1000, 1000));
        Assert.False(plan.ClickStair(new Point2D(1000, 0)));
    });

    [Fact]
    public void AStairDrawnOffTheEdgeOfATerraceGoesDownByItself() => OnUi(() =>
    {
        // Left to work it out: drawn from the balcony's edge out over the garden, it goes down.
        var (document, _) = Balcony();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id };
        plan.SetTool(PlanTool.Stair);
        Assert.Equal(StairDirection.Auto, plan.NewStairDirection);
        plan.ClickStair(new Point2D(7250, 1000));
        plan.ClickStair(new Point2D(7250, -2000));
        var down = Assert.Single(document.Elements.OfType<Stair>());
        Assert.Equal((document.Levels[0].Id, document.Levels[1].Id), (down.LevelId, down.TopLevelId!.Value));

        // Drawn from inside, across the floor: up, as ever.
        plan.ClickStair(new Point2D(2000, 1000));
        plan.ClickStair(new Point2D(2000, 3000));
        var up = document.Elements.OfType<Stair>().Single(stair => stair != down);
        Assert.Equal(document.Levels[1].Id, up.LevelId);

        // Told to go up, it goes up even off the edge.
        plan.NewStairDirection = StairDirection.Up;
        plan.ClickStair(new Point2D(8000, 1000));
        plan.ClickStair(new Point2D(8000, -2000));
        Assert.Equal(2, document.Elements.OfType<Stair>().Count(stair => stair.LevelId == document.Levels[1].Id));
    });

    [Fact]
    public void ADoubleClickOrAllRoundRailsEverySideOfABalcony() => OnUi(() =>
    {
        var (document, balcony) = Balcony();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id, History = new UndoStack(), PickingEdges = true };
        plan.SetTool(PlanTool.Railing);

        // An edge picked by clicking on the balcony near it, then a double-click: all the way round, the one picked taken in.
        Assert.True(plan.PickRailingEdge(new Point2D(7250, 1400)));
        Assert.True(plan.PickRailingOutline(new Point2D(7250, 2500)));
        var railing = Assert.Single(document.Elements.OfType<Railing>());
        Assert.True(railing.Closed);
        Assert.Equal(2 * (2400 + 2900), Railings.Length(railing), 0);

        // All Round on a balcony with a stair up onto it: open where it comes up.
        document.Remove(railing);
        Put(document, 7250, 1000 - 14 * 225);
        Assert.True(plan.GuardAllRound(balcony));
        Assert.Equal(5, Railings.Segments(document, Assert.Single(document.Elements.OfType<Railing>())).Count);
    });

    [Fact]
    public void AStairSaysWhatItCutsThrough()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        Assert.Equal("Floor on First Floor", Stairs.CutsThrough(document, stair));
        stair.CutsFloorAbove = false;
        stair.CutsRoof = false;
        Assert.Equal("Nothing: told not to cut", Stairs.CutsThrough(document, stair));
    }

    [Fact]
    public void ARailingIsDrawnRoundACurveWithArcs() => OnUi(() =>
    {
        var (document, _) = Balcony();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id, History = new UndoStack(), RailingArc = true };
        plan.SetTool(PlanTool.Railing);

        // Its start, its end, and a point it bows through: a half circle.
        plan.ClickRailing(new Point2D(6500, 1500));
        plan.ClickRailing(new Point2D(8500, 1500));
        plan.ClickRailing(new Point2D(7500, 2500));
        Assert.True(plan.FinishRailing());

        var railing = Assert.Single(document.Elements.OfType<Railing>());
        Assert.Equal(1, Math.Abs(railing.BulgeAt(0)), 3);
        Assert.InRange(Railings.Length(railing), Math.PI * 1000 * 0.99, Math.PI * 1000);
        Assert.Contains(ElementHandles.Of(document, railing), handle => handle.Key == "bend:0");
    });

    [Fact]
    public void AStairIsPutDownInThreeDWhereItIsClicked() => OnUi(() =>
    {
        var (document, _) = House();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id };
        plan.SetTool(PlanTool.Stair);

        // Clicked on the ground floor, in 3D: straight, running north, up to the first floor.
        Assert.True(plan.PlaceStairIn3D(Guid.Empty, new Point3D(2000, 1000, 0)));
        var stair = Assert.Single(document.Elements.OfType<Stair>());
        Assert.Equal((document.Levels[0].Id, new Point2D(2000, 1000), 90.0, StairShape.Straight), (stair.LevelId, stair.Start, stair.Angle, stair.Shape));
    });

    [Fact]
    public void RegionsJoistLayoutsAndTheExistingGroundAreSavedCopiedAndExported()
    {
        var (document, _) = House();
        var ground = Ground(document, (_, y) => y * 0.02 - 300);
        new SetExistingGroundCommand(ground, record: true).Redo();
        var lawn = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Lawn };
        lawn.SetBoundary(new[] { new Point2D(7000, 0), new Point2D(11000, 0), new Point2D(11000, 4000), new Point2D(7000, 4000) });
        document.Add(lawn);
        var system = new BeamSystem { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<BeamType>().First().Id, Spacing = 600, Direction = 0 };
        system.SetBoundary(new[] { new Point2D(100, 100), new Point2D(5900, 100), new Point2D(5900, 4900), new Point2D(100, 4900) });
        document.Add(system);

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(ground.ExistingPoints, back.Elements.OfType<Toposurface>().Single().ExistingPoints);
        Assert.Equal(lawn.Boundary, back.Elements.OfType<GroundRegion>().Single().Boundary);
        var savedSystem = back.Elements.OfType<BeamSystem>().Single();
        Assert.Equal((600.0, (double?)0, system.TypeId), (savedSystem.Spacing, savedSystem.Direction, savedSystem.TypeId));

        var copy = (BeamSystem)ElementCopy.Clone(system)!;
        new RotateElementsCommand(new[] { copy }, new Point2D(0, 0), 90).Redo();
        Assert.Equal(90, copy.Direction!.Value, 6);
        Assert.Equal(lawn.Surface, ((GroundRegion)ElementCopy.Clone(lawn)!).Surface);

        using var model = IfcExport.Build(document);
        Assert.Single(model.Instances.OfType<IfcElementAssembly>(), assembly => assembly.PredefinedType == IfcElementAssemblyTypeEnum.BEAM_GRID);
        Assert.Contains(model.Instances.OfType<IfcGeographicElement>(), element => element.ObjectType == "LAWN");
    }

    [Fact]
    public void AStairUpToABalconyStaysThereWhenItsShapeOrTypeIsChanged()
    {
        // A stair from the garden up to the balcony's south edge, and a railing along that edge.
        var (document, _) = Balcony();
        var railing = new Railing { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<RailingType>().First().Id };
        railing.SetPath(new[] { new Point2D(6050, 1050), new Point2D(8450, 1050) });
        document.Add(railing);
        var stair = Put(document, 7250, 1000 - 14 * 225);
        var hung = Assert.IsType<(Point2D Head, Vector2D Up)>(Stairs.HungFrom(document, stair));
        Assert.True(hung.Head.DistanceTo(new Point2D(7250, 1000)) < 1);

        (Point2D Head, Vector2D Up) Head() => Stairs.HeadOf(Stairs.Layout(document, stair))!.Value;

        // Made a curved stair in its Properties: its head still at the balcony's edge, coming straight up onto it, the railing open for it.
        var shape = stair.GetInstanceParameters(document).Single(parameter => parameter.Name == StairParameters.Shape.Name);
        Assert.True(shape.TrySet("Curved"));
        var change = new ParameterChangeCommand(shape, "Straight", "Curved");
        Assert.Equal(StairShape.Curved, stair.Shape);
        var (head, up) = Head();
        Assert.True(head.DistanceTo(new Point2D(7250, 1000)) < 1, $"Its head is at {head}");
        Assert.Equal(1, up.Y, 3);
        Assert.Single(Railings.StairGaps(document, railing));

        // Undone: straight again, its foot where it was.
        change.Undo();
        Assert.Equal((StairShape.Straight, new Point2D(7250, 1000 - 14 * 225), 90.0), (stair.Shape, stair.Start, stair.Angle));

        // Given the concrete type - longer goings, lower risers: its head stays, its foot goes further out.
        var concrete = document.TypesOf<StairType>().Single(type => type.Name == "Stair - Concrete");
        new StairRelaidCommand(document, new[] { stair }, new SetElementsTypeCommand(new[] { stair }, concrete)).Redo();
        Assert.True(Head().Head.DistanceTo(new Point2D(7250, 1000)) < 1);
        Assert.True(stair.Start.Y < 1000 - 14 * 225 - 500);

        // Made a spiral: its landing stepped off onto the balcony there, the stair beside it, and the railing open as wide as the stair.
        var spiral = stair.GetInstanceParameters(document).Single(parameter => parameter.Name == StairParameters.Shape.Name);
        Assert.True(spiral.TrySet("Spiral"));
        var off = Stairs.HeadOf(Stairs.Layout(document, stair))!.Value;
        Assert.True(off.At.DistanceTo(new Point2D(7250, 1000)) < 1, $"Its landing is stepped off at {off.At}");
        Assert.True(Stairs.Layout(document, stair).Centre!.Value.Y < 1000);
        Assert.Single(Railings.StairGaps(document, railing));
        Assert.Equal(Stairs.Layout(document, stair).Width, Railings.Length(railing) - Railings.Segments(document, railing).Sum(piece => piece.From.DistanceTo(piece.To)), 1);
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("short of the floor"));

        // A stair coming up through the floor inside keeps its foot.
        var inside = Put(document, 3000, 600);
        Assert.Null(Stairs.HungFrom(document, inside));
        Assert.True(inside.GetInstanceParameters(document).Single(parameter => parameter.Name == StairParameters.Width.Name).TrySet(1000.0));
        Assert.Equal((new Point2D(3000, 600), 1000.0), (inside.Start, inside.Width));
    }

    [Fact]
    public void AFloorDrawnALittlePastTheWallStandingOnItStopsAtItsFaceButABalconyIsLeft()
    {
        var document = BimDocument.CreateDefault();
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null);
        var half = outside.Structure.TotalWidth / 2;
        var floor = new Floor { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6300, 0), new Point2D(6300, 5000), new Point2D(0, 5000) });
        document.Add(floor);
        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(0, 5000), TypeId = outside.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 });
        Assert.Empty(SlabEdges.Lips(document, floor));

        // The upper storey's outside wall, drawn after the floor, along x = 6000: the 300 past it is cut back to its face.
        document.Add(new Wall { Start = new Point2D(6000, 0), End = new Point2D(6000, 5000), TypeId = outside.Id, LevelId = document.Levels[1].Id, UnconnectedHeight = 3000 });
        Assert.True(SlabEdges.IsCut(document, floor));
        var region = Assert.Single(SlabEdges.Regions(document, floor));
        Assert.InRange(region.Outer.Max(point => point.X), 6000 + half - 2, 6000 + half);

        // Drawn on out 2500: a balcony, left as it is.
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(8500, 0), new Point2D(8500, 5000), new Point2D(0, 5000) });
        Assert.Empty(SlabEdges.Lips(document, floor));
        Assert.Equal(8500, Assert.Single(SlabEdges.Regions(document, floor)).Outer.Max(point => point.X), 6);
    }

    [Fact]
    public void ARoofAGlassFrontRisesPastStopsAtTheGlassWhicheverIsDrawnFirst()
    {
        static (BimDocument Document, Roof Roof) Build(bool glassFirst, double from)
        {
            var document = BimDocument.CreateDefault();
            var glass = new Wall
            {
                Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = document.TypesOf<CurtainWallType>().First(type => type.Name.Contains("Storefront")).Id,
                LevelId = document.Levels[0].Id, UnconnectedHeight = 7000
            };
            var roof = new Roof { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 4500 };
            roof.SetBoundary(new[] { new Point2D(0, from), new Point2D(6000, from), new Point2D(6000, 5000), new Point2D(0, 5000) });
            if (glassFirst) document.Add(glass);
            document.Add(roof);
            if (!glassFirst) document.Add(glass);
            return (document, roof);
        }

        // The roof behind the glass drawn on 1500 past it: what is past the glass is cut away, whichever came first.
        foreach (var glassFirst in new[] { true, false })
        {
            var (document, roof) = Build(glassFirst, -1500);
            var cut = Assert.Single(SlabEdges.Beyond(document, roof));
            Assert.True(Polygon2D.Contains(cut, new Point2D(3000, -700)));
            Assert.False(Polygon2D.Contains(cut, new Point2D(3000, 2000)));
            Assert.Contains(RoofJoin.Openings(document, roof, null, trimmed: true), opening => Polygon2D.Contains(opening, new Point2D(3000, -700)));
            Assert.DoesNotContain(RoofJoin.Openings(document, roof), opening => Polygon2D.Contains(opening, new Point2D(3000, -700)));
        }

        // A canopy out in front of the glass, with nothing behind: left as it is.
        var (front, canopy) = Build(true, -1500);
        canopy.SetBoundary(new[] { new Point2D(0, -1500), new Point2D(6000, -1500), new Point2D(6000, 0), new Point2D(0, 0) });
        Assert.Empty(SlabEdges.Beyond(front, canopy));
        Assert.Empty(SlabEdges.Lips(front, canopy));
    }
}
