using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// How a stair meets what is round it: the balcony, deck or roof terrace it comes up to -
/// set down below the storey's floor or raised above it - and the ground it stands on outside;
/// the roof coming down over it, the walls it must keep clear of, the doors at its foot and
/// its head, its open sides; and what it keeps when it is pasted up a storey or mirrored.
/// </summary>
[Collection("Wpf")]
public class StairConnectionTests
{
    private static Floor Deck(BimDocument document, Level level, double offset, params (double X, double Y)[] corners)
    {
        var deck = new Floor { LevelId = level.Id, TypeId = document.TypesOf<FloorType>().First().Id, HeightOffset = offset };
        deck.SetBoundary(corners.Select(corner => new Point2D(corner.X, corner.Y)));
        document.Add(deck);
        return deck;
    }

    [Fact]
    public void AStairOutsideComesUpToTheBalconyItReachesAsHighAsThatIs()
    {
        // A balcony off the first floor, and a stair up to it from the garden, its head at the balcony's edge.
        var (document, _) = House();
        var balcony = Deck(document, document.Levels[1], 0, (6000, 2500), (7500, 2500), (7500, 5000), (6000, 5000));
        var stair = Put(document, 6750, 2500 - 14 * 225);
        Assert.Equal(3000, Stairs.Layout(document, stair).Top, 6);
        Assert.Empty(Stairs.Problems(document, stair));

        // Set down a step below the house's floor, as a balcony is to keep the rain out: 150 lower.
        balcony.HeightOffset = -150;
        var layout = Stairs.Layout(document, stair);
        Assert.Equal((2850.0, 15), (layout.Top, layout.Risers));
        Assert.Equal(190, layout.Rise, 6);

        // Raised 100 on its decking: one more riser.
        balcony.HeightOffset = 100;
        Assert.Equal((3100.0, 16), (Stairs.Layout(document, stair).Top, Stairs.Layout(document, stair).Risers));
    }

    [Fact]
    public void AStairUpToARoofTerraceMeetsItsTopAndSaysWhenItComesUpShort()
    {
        // A flat roof over an extension, stood on: its top 320 above the storey's level, with its falls.
        var (document, _) = House();
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[1].Id };
        roof.SetBoundary(new[] { new Point2D(6000, 0), new Point2D(9000, 0), new Point2D(9000, 5000), new Point2D(6000, 5000) });
        roof.SetShape(RoofForm.Flat);
        document.Add(roof);

        // Drawn as for the storey's height, it would come up short of the terrace: it is laid out to the terrace's top instead.
        var stair = Put(document, 9000 + 14 * 225, 4000, angle: 180);
        var layout = Stairs.Layout(document, stair);
        Assert.Equal(roof.TopAt(document, new Point2D(8900, 4000)), layout.Top, 0);
        Assert.Equal(17, layout.Risers);
        var problems = Stairs.Problems(document, stair);
        Assert.DoesNotContain(problems, problem => problem.Contains("short of the floor"));

        // Two risers more, it runs on 450 past the terrace's edge into it: said, and made to pass, its foot set back to come up at the edge.
        Assert.Contains(problems, problem => problem.StartsWith("It runs 450 in past the edge of the floor it comes up onto"));
        var command = StairFixes.MakeItPass(document, stair, out var message);
        Assert.Contains("its foot set back 450", message);
        command!.Redo();
        Assert.Equal(9000 + 16 * 225, stair.Start.X, 6);
        layout = Stairs.Layout(document, stair);
        Assert.Equal((17, 9000.0), (layout.Risers, layout.TopEdge.From.X));
        Assert.Empty(Stairs.Openings(document, stair, roof, layout));
        Assert.Empty(Stairs.Problems(document, stair));

        // Well short of it, it says so.
        stair.Start = new Point2D(9000 + 14 * 225 + 900, 4000);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("short of the floor on First Floor"));
    }

    [Fact]
    public void AStairOnAPatioSetDownStandsOnIt()
    {
        var (document, _) = House();
        Deck(document, document.Levels[0], -150, (2000, -3000), (4000, -3000), (4000, -1000), (2000, -1000));
        var stair = Put(document, 3000, -1500, angle: -90);
        stair.TopLevelId = null;
        stair.UnconnectedHeight = 600;

        // In front of its first riser is the patio, 150 down: its foot is there.
        var layout = Stairs.Layout(document, stair);
        Assert.Equal((-150.0, 450.0), (layout.Bottom, layout.Top));
    }

    [Fact]
    public void ItsHeadroomIsMeasuredUnderAPitchedRoofComingDownOverIt()
    {
        // An attic under a roof pitched 35° from eaves 400 out, and a stair up into it toward the eaves.
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var type = document.TypesOf<WallType>().First();
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000) };
        for (var i = 0; i < 4; i++) document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = ground.Id, TopLevelId = first.Id });
        Deck(document, first, 0, (0, 0), (10000, 0), (10000, 6000), (0, 6000));
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = ground.Id, HeightOffset = first.Elevation };
        roof.SetBoundary(new[] { new Point2D(-400, -400), new Point2D(10400, -400), new Point2D(10400, 6400), new Point2D(-400, 6400) });
        roof.SetEdges(new[] { new RoofEdge { DefinesSlope = true, SlopeDegrees = 35 }, new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 35 }, new RoofEdge() });
        document.Add(roof);

        var stair = Put(document, 5000, 6000 - 14 * 225 - 300);
        var least = Stairs.LeastHeadroom(document, stair, Stairs.Layout(document, stair));
        Assert.NotNull(least);
        Assert.IsType<Roof>(least!.Value.By);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("under the roof") && problem.Contains("a dormer over it"));

        // Turned to climb along under the ridge, it has the height.
        (stair.Start, stair.Angle) = (new Point2D(2000, 3000), 0);
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("under the roof"));
    }

    [Fact]
    public void AnOpenSideItLeavesWithoutARailingIsGuardedWhereItDropsTooFar()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        stair.Railings = StairRailings.LeftSide;
        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("right side is open, 3000 up") && problem.Contains("drops more than 600"));

        // Under the IRC: more than 30 in up.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("more than 762 (30 in) up"));

        // Made to pass: its open sides guarded.
        var command = StairFixes.MakeItPass(document, stair, out var message);
        Assert.NotNull(command);
        Assert.Contains("its open sides guarded", message);
        command!.Redo();
        Assert.Equal(StairRailings.Auto, stair.Railings);

        // Steps no more than 600 up need no guarding at their side.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        var steps = Put(document, 1500, 600);
        (steps.TopLevelId, steps.UnconnectedHeight, steps.Railings) = (null, 600, StairRailings.LeftSide);
        Assert.DoesNotContain(Stairs.Problems(document, steps), problem => problem.Contains("open"));
    }

    [Fact]
    public void AStairRunningThroughAWallIsSaid()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 3000);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.StartsWith("It runs through a wall") && problem.Contains("at step 9"));

        // Set against a wall, along its side, it is not.
        stair.Start = new Point2D(6000 - 100 - 450, 600);
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("runs through a wall"));
    }

    [Fact]
    public void ItsTopMustBeAboveItsFoot()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var parameters = stair.GetInstanceParameters(document).ToList();

        // Its top level offered from those above its base, and none at or below its foot taken.
        var top = parameters.Single(parameter => parameter.Definition == StairParameters.TopLevel);
        Assert.DoesNotContain(ground.Name, top.AllowedValues!);
        Assert.False(parameters.Single(parameter => parameter.Definition == StairParameters.BaseLevel).TrySet(first.Name));
        Assert.False(parameters.Single(parameter => parameter.Definition == StairParameters.TopOffset).TrySet(-2800.0));
        Assert.Equal((ground.Id, first.Id), (stair.LevelId, stair.TopLevelId));

        // A level moved down below it afterwards: said, and nothing else.
        first.Elevation = -1000;
        var problem = Assert.Single(Stairs.Problems(document, stair));
        Assert.StartsWith("Its head is 1000 below its foot", problem);
    }

    [Fact]
    public void DoorsAtItsFootAndHeadAreFoundByTheirHeightNotTheirWallsLevel()
    {
        // A wall two storeys high, with a door 3000 up it onto the floor at the head of the stair.
        var (document, _) = House();
        var wall = document.Walls.Single(candidate => candidate.Start == new Point2D(6000, 5000));
        wall.UnconnectedHeight = 6000;
        var stair = Put(document, 3000, 5000 - 120 - 900 - 14 * 225);
        var door = new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Operation == DoorOperation.Swing).Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000, SillHeight = 3000 };
        document.Add(door);
        if (Stairs.DoorSwings(document, 3000).Single().Average(point => point.Y) > 5000) door.FlipFacing = true;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("over the landing at its head"));
        Assert.Empty(Stairs.DoorSwings(document, 0));

        // Over the top steps themselves - under the IRC too.
        stair.Start = new Point2D(3000, 5000 - 120 - 14 * 225 - 200);
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("A door at its head swings out over its steps"));
    }

    [Fact]
    public void AFewStepsOutsideNeedAHandrailOnlyFromThreeOrFourRisers()
    {
        var (document, _) = House();
        var steps = Put(document, 3000, -1500);
        (steps.TopLevelId, steps.Railings) = (null, StairRailings.None);
        bool Wants() => Stairs.Problems(document, steps).Any(problem => problem.StartsWith("It has no handrail"));

        // In a house, two risers need none; three do, as Approved Document M asks of steps to a door.
        steps.UnconnectedHeight = 340;
        Assert.False(Wants());
        steps.UnconnectedHeight = 510;
        Assert.True(Wants());

        // The IRC: four risers or more.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.False(Wants());
        steps.UnconnectedHeight = 700;
        Assert.True(Wants());
    }

    [Fact]
    public void AHousesStairOutsideHasGoingsOf280()
    {
        var (document, _) = House();
        var balcony = Deck(document, document.Levels[1], 0, (6000, 2500), (8000, 2500), (8000, 5000), (6000, 5000));
        var stair = Put(document, 7000, 2500 - 14 * 225);
        Assert.True(Stairs.StandsOutside(document, stair));
        Assert.False(Stairs.StandsOutside(document, Put(document, 3000, 600)));

        stair.Outside = true;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("280 at least on a house's stair outside"));

        // Made to pass: its going 280, its rise kept within 2R+G.
        var command = StairFixes.MakeItPass(document, stair, out var said);
        Assert.True(command is not null, said);
        command!.Redo();
        var layout = Stairs.Layout(document, stair);
        Assert.InRange(layout.Going, 280, 300);
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("going"));
        Assert.NotNull(balcony);

        var saved = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Stair>().Single(candidate => candidate.Id == stair.Id);
        Assert.True(saved.Outside);
        Assert.True(((Stair)ElementCopy.Clone(stair)!).Outside);
    }

    [Fact]
    public void MirroredOrTurnedRoundItKeepsItsHandrailAndBullnoseWhereTheyWere()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        (stair.Railings, stair.BottomStep) = (StairRailings.LeftSide, BottomStep.BullnoseLeft);
        var railAt = StairModel.Railings(document, stair, Stairs.Layout(document, stair)).Single().Points[0].At.X;
        Assert.Equal(2550, railAt, 6);

        // Mirrored about a line across its foot's x: its handrail on the other side of it in plan, the mirror image's left now its right.
        new MirrorElementsCommand(new[] { stair }, new Line2D(new Point2D(3000, 0), new Vector2D(0, 1))).Redo();
        Assert.Equal((StairRailings.RightSide, BottomStep.BullnoseRight), (stair.Railings, stair.BottomStep));
        Assert.Equal(3450, StairModel.Railings(document, stair, Stairs.Layout(document, stair)).Single().Points[0].At.X, 6);

        // Turned to go up the other way: its handrail stays against the same side in plan.
        StairGrips.Flip(document, stair);
        Assert.Equal(StairRailings.LeftSide, stair.Railings);
        Assert.All(StairModel.Railings(document, stair, Stairs.Layout(document, stair)).Single().Points, point => Assert.Equal(3450, point.At.X, 6));
    }

    [Fact]
    public void TheLevelItGoesUpToDeletedItKeepsItsHeight()
    {
        var (document, _) = House();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var second = new Level { Name = "Second Floor", Elevation = 6000 };
        var third = new Level { Name = "Third Floor", Elevation = 9000 };
        document.AddLevel(second);
        document.AddLevel(third);
        var stair = Put(document, 1000, 600);
        stair.LevelId = first.Id;
        stair.TopLevelId = second.Id;
        var low = Put(document, 3000, 600);
        low.RepeatToLevelId = third.Id;

        // Up to the second floor, deleted: 3000 high, unconnected; repeated up to the third, deleted: as far as the second.
        var history = new UndoStack();
        history.Execute(new DeleteLevelCommand(document, second));
        Assert.Null(stair.TopLevelId);
        Assert.Equal(3000, stair.UnconnectedHeight, 6);
        history.Execute(new DeleteLevelCommand(document, third));
        Assert.Equal((first.Id, (Guid?)null), (low.TopLevelId!.Value, low.RepeatToLevelId));

        history.Undo();
        history.Undo();
        Assert.Equal((second.Id, third.Id), (stair.TopLevelId!.Value, low.RepeatToLevelId!.Value));
    }

    [Fact]
    public void PastedUpAStoreyItGoesUpToTheNextAsItDid()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _) = House();
                var (ground, first) = (document.Levels[0], document.Levels[1]);
                var second = new Level { Name = "Second Floor", Elevation = 6000 };
                document.AddLevel(second);
                var stair = Put(document, 3000, 600);

                var plan = new PlanView { Document = document, ActiveLevelId = ground.Id };
                plan.SelectMany(new[] { stair });
                plan.CopySelection();
                plan.ActiveLevelId = first.Id;
                plan.Paste(inPlace: true);

                var pasted = document.Elements.OfType<Stair>().Single(candidate => candidate.LevelId == first.Id);
                Assert.Equal(second.Id, pasted.TopLevelId);
                Assert.Equal(3000, Stairs.Layout(document, pasted).Top - Stairs.Layout(document, pasted).Bottom, 6);

                // Pasted on the top storey, with none above: the height it had, unconnected.
                plan.ActiveLevelId = second.Id;
                plan.Paste(inPlace: true);
                var highest = document.Elements.OfType<Stair>().Single(candidate => candidate.LevelId == second.Id);
                Assert.Null(highest.TopLevelId);
                Assert.Equal(3000, highest.UnconnectedHeight, 6);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
