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
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Stairs: laid out from floor to floor - straight, or turning a quarter on a landing or on
/// winders, or a half - their risers worked out from the storey's height; checked against
/// Approved Document K or the IRC; cutting their opening in the floor above as headroom asks,
/// guarded round it; with handrails where the walls beside them leave them open; drawn, cut,
/// saved, scheduled and exported.
/// </summary>
[Collection("Wpf")]
public class StairTests
{
    /// <summary>A storey 3000 high: a room 6 m by 5 m, its first floor over the whole of it.</summary>
    private static (BimDocument Document, Floor Floor) House()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var wallType = document.TypesOf<WallType>().First();
        foreach (var (a, b) in new[] { ((0.0, 0.0), (6000.0, 0.0)), ((6000.0, 0.0), (6000.0, 5000.0)), ((6000.0, 5000.0), (0.0, 5000.0)), ((0.0, 5000.0), (0.0, 0.0)) })
            document.Add(new Wall { Start = new Point2D(a.Item1, a.Item2), End = new Point2D(b.Item1, b.Item2), TypeId = wallType.Id, LevelId = ground.Id, UnconnectedHeight = 3000 });

        var floor = new Floor { LevelId = first.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(floor);
        return (document, floor);
    }

    private static Stair Put(BimDocument document, double x, double y, double angle = 90, StairShape shape = StairShape.Straight,
        StairCorner corner = StairCorner.Landing, bool left = false, double width = 900)
    {
        var stair = new Stair
        {
            TypeId = document.TypesOf<StairType>().First().Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id,
            Start = new Point2D(x, y), Angle = angle, Shape = shape, Corner = corner, TurnsLeft = left, Width = width
        };
        document.Add(stair);
        return stair;
    }

    [Fact]
    public void AStraightStairSharesTheStoreysHeightAmongItsRisers()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);

        var layout = Stairs.Layout(document, stair);
        Assert.Equal((15, 200.0, 225.0), (layout.Risers, Math.Round(layout.Rise, 6), layout.Going));
        Assert.Equal(14, layout.Steps.Count);
        Assert.Equal(3000, layout.Top - layout.Bottom, 6);
        Assert.Equal(41.6, layout.Pitch, 1);

        // Its run up the plan: 14 goings, from its first riser to the floor above.
        Assert.Equal(new Point2D(3000, 600 + 14 * 225), layout.WalkingLine[^1]);

        // The storey made 2700: it re-plans itself, 14 risers of 192.9.
        document.Levels[1].Elevation = 2700;
        layout = Stairs.Layout(document, stair);
        Assert.Equal(14, layout.Risers);
        Assert.Equal(192.86, layout.Rise, 2);
    }

    [Fact]
    public void AQuarterTurnTurnsOnALandingOrOnWindersAndAHalfTurnComesBack()
    {
        var (document, _) = House();

        // Turning right on a landing: a square landing, then the second flight going off to the right.
        var landing = Stairs.Layout(document, Put(document, 1500, 600, shape: StairShape.QuarterTurn));
        var square = Assert.Single(landing.Steps, step => step.Kind == StairStepKind.Landing);
        Assert.Equal(900 * 900, Polygon2D.Area(square.Outline), 0);
        Assert.Equal(1, landing.Steps.Last().Up.X, 6);

        // Turning left on winders: three winders fanned round a newel, the second flight off to the left.
        var winders = Stairs.Layout(document, Put(document, 4000, 600, shape: StairShape.QuarterTurn, corner: StairCorner.Winders, left: true));
        Assert.Equal(3, winders.Steps.Count(step => step.Kind == StairStepKind.Winder));
        Assert.NotNull(winders.Newel);
        Assert.Equal(-1, winders.Steps.Last().Up.X, 6);
        Assert.Equal(57.7, winders.WinderNarrowEnd!.Value, 1);
        Assert.Equal(235.6, winders.WinderGoing!.Value, 1);

        // A half turn: the second flight back down the plan beside the first, a well between.
        var half = Stairs.Layout(document, Put(document, 1000, 2500, angle: 0, shape: StairShape.HalfTurn, left: true));
        Assert.Equal(-1, half.Steps.Last().Up.X, 6);
        var walk = half.WalkingLine;
        Assert.Equal(900 + 100, walk[^1].Y - walk[0].Y, 6);
    }

    [Fact]
    public void ItIsCheckedAgainstApprovedDocumentKOrTheIrc()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        Assert.Empty(Stairs.Problems(document, stair));
        Assert.Equal("Meets Approved Document K", Stairs.Verdict(document, stair));

        // Winders on a stair only 800 wide: too shallow along the middle.
        var narrow = Put(document, 1200, 600, shape: StairShape.QuarterTurn, corner: StairCorner.Winders, width: 800);
        Assert.Contains(Stairs.Problems(document, narrow), problem => problem.Contains("winders' going"));

        // A steep type: too high a rise, too steep, too short a going.
        var steep = document.TypesOf<StairType>().First().Duplicate("Steep");
        steep.MaxRiser = 240;
        steep.Going = 200;
        document.AddType(steep);
        stair.TypeId = steep.Id;
        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("220 at most"));
        Assert.Contains(problems, problem => problem.Contains("42°"));
        Assert.Contains(problems, problem => problem.Contains("220 at least"));
        stair.TypeId = document.TypesOf<StairType>().First().Id;

        // Checked to the IRC, the same stair is too narrow and its rise too high.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("914 (36 in)"));
        Assert.Contains(problems, problem => problem.Contains("196 (7¾ in)"));
        Assert.Contains(problems, problem => problem.Contains("254 (10 in)"));
    }

    [Fact]
    public void ItCutsItsOpeningInTheFloorAboveAsFarAsHeadroomAsks()
    {
        var (document, floor) = House();
        var stair = Put(document, 3000, 600);
        var layout = Stairs.Layout(document, stair);
        var whole = floor.Area;

        // Over the steps without 2000 under the floor: the top end of the flight.
        var hole = Assert.Single(Stairs.Holes(document, floor));
        var underside = floor.GetBottomElevation(document);
        var opened = layout.Steps.Count(step => step.Top + 2000 > underside);
        Assert.InRange(opened, 10, layout.Steps.Count);
        Assert.InRange(Polygon2D.Area(hole), opened * 225.0 * 900 - 1000, opened * 225.0 * 900 + 25 * 900);
        Assert.Equal(whole - Polygon2D.Area(hole), SlabEdges.NetArea(document, floor), 0);

        // No headroom problem with its opening; told not to cut it, it has one.
        Assert.Null(Stairs.Problems(document, stair).FirstOrDefault(problem => problem.Contains("headroom")));
        stair.CutsFloorAbove = false;
        Assert.Empty(Stairs.Holes(document, floor));
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("headroom"));
    }

    [Fact]
    public void ItCutsAFlatRoofThatIsTheFloorAboveButNotAPitchedRoof()
    {
        // The storey above stands on a flat roof deck, as a first floor built as a warm deck does.
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) };
        var deck = new Roof { TypeId = document.TypesOf<RoofType>().First(t => t.Name.Contains("Flat")).Id, LevelId = ground.Id, HeightOffset = first.Elevation };
        deck.SetBoundary(corners);
        deck.SetEdges(corners.Select(_ => new RoofEdge()).ToList());
        document.Add(deck);

        var stair = Put(document, 3000, 600);
        var layout = Stairs.Layout(document, stair);
        Assert.Equal(deck.GetTopElevation(document), layout.Top, 6);

        // Cut where headroom asks, in its 3D build too, and guarded round on the deck.
        var hole = Assert.Single(Stairs.Holes(document, deck));
        Assert.Contains(RoofJoin.Openings(document, deck), opening => ReferenceEquals(opening, hole) || Math.Abs(Polygon2D.Area(opening) - Polygon2D.Area(hole)) < 1);
        var built = RoofSolid.Pieces(document, deck).GroupBy(piece => piece.Layer).First().Sum(piece => Polygon2D.Area(piece.Outline));
        Assert.Equal(deck.Area - Polygon2D.Area(hole), built, 0);
        Assert.NotEmpty(StairModel.Guarding(document, stair, layout));

        // A pitched roof over a stair is a roof, not a floor: it is not cut.
        var pitched = new Roof { TypeId = document.TypesOf<RoofType>().First(t => !t.Name.Contains("Flat")).Id, LevelId = first.Id };
        pitched.SetBoundary(corners);
        pitched.SetEdges(corners.Select(_ => new RoofEdge { DefinesSlope = true, SlopeDegrees = 35 }).ToList());
        Assert.False(Stairs.IsFloorLike(document, pitched));
    }

    [Fact]
    public void AHalfTurnsOpeningTakesTheWellAndLeavesNoFloorBetweenItsFlights()
    {
        var (document, floor) = House();
        var stair = Put(document, 1500, 3800, angle: 0, shape: StairShape.HalfTurn, left: true);
        stair.Well = 200;
        var layout = Stairs.Layout(document, stair);

        // The middle of the well beside the landing: open, not a sliver of floor.
        var landing = layout.Steps.Single(step => step.Kind == StairStepKind.Landing);
        var inWell = layout.Origin + layout.Axis * (landing.Outline.Min(point => (point - layout.Origin).Dot(layout.Axis)) - 300) + layout.Axis.PerpendicularLeft() * (900 / 2.0 + 100);
        Assert.DoesNotContain(SlabEdges.Regions(document, floor), region => Polygon2D.Contains(region.Outer, inWell) && !region.Holes.Any(hole => Polygon2D.Contains(hole, inWell)));

        // No guarding stands in the stairwell's middle.
        foreach (var run in StairModel.Guarding(document, stair, layout))
            foreach (var (a, b) in run.Points.Zip(run.Points.Skip(1)))
                Assert.True(Line2D.DistanceFromSegment(inWell, a.At, b.At) > 50, "Guarding across the well.");

        // A second floor laid on the first does not guard the stairwell twice.
        var guarded = StairModel.Guarding(document, stair, layout).Count;
        var twin = new Floor { LevelId = floor.LevelId, TypeId = floor.TypeId };
        twin.SetBoundary(floor.Boundary);
        document.Add(twin);
        Assert.Equal(guarded, StairModel.Guarding(document, stair, layout).Count);
    }

    [Fact]
    public void ItsHandrailFollowsItsSidesStepForStepRoundATurn()
    {
        var (document, _) = House();
        foreach (var corner in new[] { StairCorner.Winders, StairCorner.Landing })
        {
            var stair = Put(document, 2500, 600, shape: StairShape.QuarterTurn, corner: corner);
            var layout = Stairs.Layout(document, stair);

            // Out in the room, each side's handrail is its side - the step up at the turn kept where it is.
            foreach (var railing in StairModel.Railings(document, stair, layout))
            {
                var side = railing.LeftSide ? layout.Left : layout.Right;
                Assert.Equal(side.Select(point => Math.Round(point.Height, 6)), railing.Points.Select(point => Math.Round(point.Height, 6)));
            }

            document.Remove(stair);
        }
    }

    [Fact]
    public void ItsHandrailsFollowTheWallsBesideIt()
    {
        var (document, _) = House();
        var wallFace = document.TypesOf<WallType>().First().Structure.TotalWidth / 2;

        // In the middle of the room: a balustrade either side.
        var free = Put(document, 3000, 600);
        var railings = StairModel.Railings(document, free, Stairs.Layout(document, free));
        Assert.Equal(2, railings.Count);
        Assert.All(railings, railing => Assert.False(railing.OnWall));

        // Against the wall at x = 0, going up it: a balustrade on its open side only - under 1000 wide, one handrail does.
        document.Remove(free);
        var byWall = Put(document, wallFace + 450, 600);
        railings = StairModel.Railings(document, byWall, Stairs.Layout(document, byWall));
        var single = Assert.Single(railings);
        Assert.False(single.OnWall);
        Assert.False(single.LeftSide);

        // Made 1100 wide, a rail on the wall too, as Approved Document K asks.
        byWall.Width = 1100;
        byWall.Start = new Point2D(wallFace + 550, 600);
        railings = StairModel.Railings(document, byWall, Stairs.Layout(document, byWall));
        Assert.Equal(2, railings.Count);
        Assert.Contains(railings, railing => railing.OnWall && railing.LeftSide);

        // The stairwell above guarded on its open sides, not where it is come up into.
        var guarding = StairModel.Guarding(document, byWall, Stairs.Layout(document, byWall));
        Assert.NotEmpty(guarding);
        var arrival = Stairs.Layout(document, byWall).TopEdge;
        Assert.DoesNotContain(guarding.SelectMany(run => run.Points.Zip(run.Points.Skip(1))),
            pair => Line2D.DistanceFromSegment(new Point2D((pair.First.At.X + pair.Second.At.X) / 2, (pair.First.At.Y + pair.Second.At.Y) / 2), arrival.From, arrival.To) < 5);
    }

    [Fact]
    public void ItsBalustersStandOnTheStringOrOnTheTreads()
    {
        var (document, _) = House();
        foreach (var name in new[] { "Stair - Timber, Closed Risers", "Stair - Concrete" })
        {
            var stair = Put(document, 3000, 600);
            var type = document.TypesOf<StairType>().Single(t => t.Name == name);
            stair.TypeId = type.Id;
            var layout = Stairs.Layout(document, stair);

            var railing = StairModel.Railings(document, stair, layout).First();
            var balusters = railing.Points.Zip(railing.Points.Skip(1))
                .SelectMany(pair => StairModel.BalustersAlong(pair.First, pair.Second, railing.LeftSide, type, layout))
                .ToList();
            Assert.NotEmpty(balusters);

            foreach (var (at, foot, top) in balusters)
            {
                var step = layout.Steps.Where(s => Polygon2D.Contains(s.Outline, at)).MaxBy(s => s.Top)!;
                if (type.Construction == StairConstruction.Concrete)
                    Assert.Equal(step.Top, foot, 6);
                else
                    Assert.True(foot > step.Top, "A timber stair's baluster stands on its string, over the tread.");
                Assert.True(top - foot > 600, "Up to the handrail.");
            }

            document.Remove(stair);
        }
    }

    [Fact]
    public void ItIsBuiltIn3DAndCutInASection()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        var layout = Stairs.Layout(document, stair);

        var meshes = StairModel.Meshes(document, stair);
        Assert.All(meshes, mesh => Assert.Equal(MeshKind.Stair, mesh.Kind));
        Assert.Equal(layout.Bottom, meshes.Min(mesh => mesh.Bounds()!.Value.Min.Z), 0);
        Assert.True(meshes.Max(mesh => mesh.Bounds()!.Value.Max.Z) > layout.Top + 800, "Its handrail rises over the floor above.");
        Assert.Contains(meshes, mesh => mesh.Description == "Paint, White" && mesh.Edges.Count == 0);

        var marker = new SectionMarker { Start = new Point2D(3000, -500), End = new Point2D(3000, 5500), LevelId = stair.LevelId };
        var drawing = SectionProjection.Build(document, marker);
        Assert.True(drawing.Pieces.Count(piece => piece.ElementId == stair.Id) > 14);
    }

    [Fact]
    public void ItIsSavedMovedMirroredAndCopied()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600, shape: StairShape.QuarterTurn, corner: StairCorner.Winders);
        stair.Mark = "S1";

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var back = loaded.Elements.OfType<Stair>().Single();
        Assert.Equal((StairShape.QuarterTurn, StairCorner.Winders, "S1", stair.TopLevelId), (back.Shape, back.Corner, back.Mark, back.TopLevelId));
        Assert.Equal(3, loaded.TypesOf<StairType>().Count());

        // Mirrored, it turns the other way; turned, its way up turns with it.
        new MirrorElementsCommand(new[] { stair }, new Line2D(new Point2D(3000, 0), new Vector2D(0, 1))).Redo();
        Assert.True(stair.TurnsLeft);
        new RotateElementsCommand(new[] { stair }, stair.Start, 90).Redo();
        Assert.Equal(180, stair.Angle, 6);

        var copy = (Stair)ElementCopy.Clone(stair)!;
        Assert.Equal((stair.Shape, stair.TurnsLeft, stair.Width), (copy.Shape, copy.TurnsLeft, copy.Width));
    }

    [Fact]
    public void ItIsScheduledAndExportedAsAStairWithItsRailings()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        stair.Mark = "S1";

        var schedule = Schedule.Run(document, ScheduleDefinition.Defaults().Single(definition => definition.Name == "Stair Schedule"));
        var row = Assert.Single(schedule.Rows);
        Assert.Equal(15, Schedule.Numeric(row["Risers"]));
        Assert.Equal("Meets Approved Document K", row.Text("Regulations"));

        using var model = IfcExport.Build(document);
        var exported = Assert.Single(model.Instances.OfType<IfcStair>());
        Assert.Equal(IfcStairTypeEnum.STRAIGHT_RUN_STAIR, exported.PredefinedType);
        Assert.NotEmpty(model.Instances.OfType<IfcRailing>());
        var risers = model.Instances.OfType<IfcRelDefinesByProperties>()
            .Where(relation => relation.RelatedObjects.Contains(exported))
            .Select(relation => relation.RelatingPropertyDefinition).OfType<IfcPropertySet>()
            .Single(set => set.Name == "Pset_StairCommon")
            .HasProperties.OfType<IfcPropertySingleValue>().Single(property => property.Name == "NumberOfRiser");
        Assert.Equal(15L, (long)(Xbim.Ifc4.MeasureResource.IfcCountMeasure)risers.NominalValue!);
    }

    [Fact]
    public void TheStairToolPutsOneInFromItsFootTheWayUpToTheLevelAbove()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _) = House();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, NewStairShape = StairShape.QuarterTurn };
                plan.SetTool(PlanTool.Stair);

                Assert.True(plan.ClickStair(new Point2D(2000, 600)));
                Assert.Empty(document.Elements.OfType<Stair>());
                Assert.True(plan.ClickStair(new Point2D(4000, 600)));

                var stair = Assert.Single(document.Elements.OfType<Stair>());
                Assert.Equal(0, stair.Angle, 6);
                Assert.Equal(StairShape.QuarterTurn, stair.Shape);
                Assert.Equal(document.Levels[1].Id, stair.TopLevelId);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
