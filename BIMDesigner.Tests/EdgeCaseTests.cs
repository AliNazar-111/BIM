using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Edge cases of railings, storeys, stairs, beams, joists and the ground: an arc through one of
/// its own ends, an arc closing a railing round, corners merged, a mirrored handrail, guarding on
/// the wrong storey, walls split only where a floor runs all along them, beams in line and beams
/// far below, joists at their trimmers, a joist layout put back to the short way, and the ground
/// left as it was where a handle is only pressed.
/// </summary>
public class EdgeCaseTests
{
    private static RailingType Type(BimDocument document, string name) => document.TypesOf<RailingType>().Single(type => type.Name == name);

    [Fact]
    public void AnArcThroughOneOfItsOwnEndsIsStraightAndNothingRunsAway()
    {
        Assert.Equal(0, WallCurve.BulgeThrough(new Point2D(0, 0), new Point2D(2000, 0), new Point2D(2000, 0)));
        Assert.Equal(0, WallCurve.BulgeThrough(new Point2D(0, 0), new Point2D(2000, 0), new Point2D(0, 0)));
        Assert.True(Math.Abs(WallCurve.BulgeThrough(new Point2D(0, 0), new Point2D(2000, 0), new Point2D(0.001, 0.001))) <= WallCurve.MostBulge);

        var railing = new Railing();
        railing.SetPath(new[] { new Point2D(0, 0), new Point2D(2000, 0) }, new[] { 1e16 });
        Assert.Equal(WallCurve.MostBulge, railing.BulgeAt(0), 6);
        Assert.InRange(Railings.Lengths(railing).Count, 2, 1000);
    }

    [Fact]
    public void CornersMergedKeepTheArcsAfterThem()
    {
        var railing = new Railing();
        railing.SetPath(new[] { new Point2D(0, 0), new Point2D(2000, 0), new Point2D(2020, 0), new Point2D(4000, 0) }, new[] { 0.0, 0, 0.4, 0 });
        ElementHandles.Settle(railing, "corner:2");
        Assert.Equal(3, railing.Path.Count);
        Assert.Equal(0.4, railing.BulgeAt(1), 9);
    }

    [Fact]
    public void AMirroredRailingKeepsItsHandrailOnItsInside()
    {
        var railing = new Railing();
        railing.SetPath(new[] { new Point2D(6300, 1050), new Point2D(8450, 1050) });
        new MirrorElementsCommand(new[] { railing }, new Line2D(new Point2D(0, 0), new Vector2D(0, 1))).Redo();

        // Still running so that the balcony - to the north - is on its left.
        Assert.True(railing.Path[1].X > railing.Path[0].X);
    }

    [Fact]
    public void AJulietBalconyOnTheStoreyBelowGuardsNoDoorOverIt()
    {
        var document = BimDocument.CreateDefault();
        var exterior = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null);
        Door DoorOn(int level, double elevation)
        {
            var floor = new Floor { LevelId = document.Levels[level].Id, TypeId = document.TypesOf<FloorType>().First().Id };
            floor.SetBoundary(new[] { new Point2D(0, -5000), new Point2D(6000, -5000), new Point2D(6000, 0), new Point2D(0, 0) });
            document.Add(floor);
            var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = exterior.Id, LevelId = document.Levels[level].Id, UnconnectedHeight = 3000 };
            document.Add(wall);
            var door = new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
            document.Add(door);
            return door;
        }

        document.Levels[1].Elevation = 3000;
        document.AddLevel(new Level { Name = "Second Floor", Elevation = 6000 });
        var lower = DoorOn(1, 3000);
        var upper = DoorOn(2, 6000);
        document.Add(Railings.Juliet(document, lower)!);

        Assert.Empty(OpeningGuarding.Problems(document, lower));
        Assert.Contains("Juliet balcony", Assert.Single(OpeningGuarding.Problems(document, upper)));
    }

    [Fact]
    public void AFloorReachingIntoAWallAlongPartOfItLeavesItWhole()
    {
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), LevelId = first.Id, UnconnectedHeight = 6000,
            TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id
        };
        document.Add(wall);
        var roof = new Roof { LevelId = first.Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(2000, 0), new Point2D(2000, 5000), new Point2D(0, 5000) });
        document.Add(roof);

        // Into it along a third of it: no slot through the rest of it.
        Assert.Empty(Storeys.Through(document, roof));

        // All along it: split, and the part under it keeps its height if the floor goes.
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        var window = new Window { TypeId = document.TypesOf<WindowType>().First().Id, LevelId = first.Id, HostWallId = wall.Id, DistanceAlongWall = 3000, SillHeight = 4000 };
        document.Add(window);
        Assert.NotNull(Storeys.Divide(document, new[] { roof }));
        var upper = Assert.Single(document.Walls, other => other != wall);
        Assert.Equal(upper.LevelId, window.LevelId);
        Assert.Null(wall.TopLevelId);
        Assert.Equal(roof.GetBottomElevation(document) - wall.GetBaseElevation(document), wall.UnconnectedHeight, 1);
    }

    [Fact]
    public void AStoreyIsMadeOnAFloorTheWallsOfARoomAlreadyStandOn()
    {
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        var roof = new Roof { LevelId = first.Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(roof);
        var wall = new Wall
        {
            Start = new Point2D(1000, 1000), End = new Point2D(5000, 1000), LevelId = first.Id, BaseAttachedTo = roof.Id, UnconnectedHeight = 2400,
            TypeId = document.TypesOf<WallType>().First().Id
        };
        document.Add(wall);

        var done = Storeys.Divide(document, new[] { roof });
        Assert.NotNull(done);
        var storey = Assert.Single(document.Levels, level => level.StandsOn == roof.Id);
        Assert.Equal(storey.Id, wall.LevelId);
        done.Value.Done.Undo();
        Assert.Equal(first.Id, wall.LevelId);
    }

    [Fact]
    public void AVaultIsNotCutForAStairAndItsHeadroomUnderItIsSaid()
    {
        var (document, _) = House();
        var vault = new Roof { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RoofType>().First().Id, HeightOffset = 2000 };
        vault.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        vault.SetExtrusion(new RoofExtrusion(new Point2D(0, 0), new Vector2D(1, 0), new[] { new Point2D(0, 0), new Point2D(2500, 800), new Point2D(5000, 0) }, 0, 6000));
        Assert.True(vault.IsExtrusion);
        document.Add(vault);
        var stair = Put(document, 3000, 600);
        Assert.Empty(Stairs.Openings(document, stair, vault, Stairs.Layout(document, stair)));
    }

    [Fact]
    public void BeamsInLineDoNotCutEachOtherAndABeamFarBelowCarriesNothing()
    {
        var (document, _) = House();
        var type = document.TypesOf<BeamType>().First();
        Beam Hang(int level, (double X, double Y) from, (double X, double Y) to)
        {
            var beam = new Beam { LevelId = document.Levels[level].Id, TypeId = type.Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y) };
            document.Add(beam);
            return beam;
        }

        // Laid over each other in line: each built its whole length.
        var a = Hang(1, (1000, 2500), (3000, 2500));
        var b = Hang(1, (2000, 2500), (5000, 2500));
        Assert.Equal((a.Start, a.End), Beams.Ends(document, a));
        Assert.Equal((b.Start, b.End), Beams.Ends(document, b));

        // A beam a storey down, under the end of one: carrying nothing of it.
        document.Remove(a);
        document.Remove(b);
        Hang(0, (2000, 1000), (2000, 4000));
        var over = Hang(1, (2000, 2500), (4000, 2500));
        Assert.NotEqual("a beam", Beams.CarriedBy(document, over, over.Start));
    }

    [Fact]
    public void AJoistLayoutTurnedByHandIsPutBackToTheShortWayByUndo()
    {
        var document = BimDocument.CreateDefault();
        var system = new BeamSystem { LevelId = document.Levels[1].Id };
        system.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) });
        document.Add(system);

        var direction = system.GetInstanceParameters(document).Single(parameter => parameter.Name == BeamSystemParameters.Direction.Name);
        var was = direction.Value;
        Assert.True(direction.TrySet(45.0));
        var change = new ParameterChangeCommand(direction, was, 45.0);
        Assert.Equal(45, system.Direction!.Value, 6);
        change.Undo();
        Assert.Null(system.Direction);
    }

    [Fact]
    public void ASpotLevelPressedAndLetGoLeavesTheSurveyAsItWas()
    {
        var ground = new Toposurface();
        ground.SetPoints(new[] { new Point3D(0, 0, 0), new Point3D(10000, 0, 0), new Point3D(10000, 10000, 0), new Point3D(0, 10000, 0), new Point3D(5000, 5000, 100), new Point3D(5030, 5000, 300) });

        // A kerb's top and foot 30 apart stay, whatever is dragged elsewhere.
        ElementHandles.Settle(ground, "point:0");
        Assert.Equal(6, ground.Points.Count);

        // The point dragged onto another goes.
        ElementHandles.Settle(ground, "point:5");
        Assert.Equal(5, ground.Points.Count);
    }
}
