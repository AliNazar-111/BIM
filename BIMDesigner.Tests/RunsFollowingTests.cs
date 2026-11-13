using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Pipes and ducts following what they meet (round 79): a fitting moved, turned, mirrored or nudged takes the ends of the pipes and
/// ducts at its connectors with it - an upright drop under it whole - and a pipe moved takes the ends of those joined to it; undone,
/// they go back where they were.
/// </summary>
[Collection("Wpf")]
public class RunsFollowingTests
{
    private static Component Put(BimDocument document, string type, double x, double y, double rotation = 0)
    {
        var family = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation, Elevation = family.DefaultElevation
        };
        document.Add(component);
        return component;
    }

    private static Pipe Run(BimDocument document, Point3D from, Point3D to, double bore = 100)
    {
        var type = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.PvcU);
        var pipe = new Pipe
        {
            TypeId = type.Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = PipeSizes.Of(PipeMaterial.PvcU).First(size => size.Inside == bore).Outside, Kind = SystemKind.Foul
        };
        document.Add(pipe);
        return pipe;
    }

    private static Point3D Connector(BimDocument document, Component fitting, ConnectorDomain domain) =>
        Connectors.Placed(document, fitting).First(placed => placed.Connector.Domain == domain).At;

    private static void Near(Point3D expected, Point3D actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }

    [Fact]
    public void AWcMovedTakesItsBranchAndUndoneGivesItBack()
    {
        // A WC's soil branch 1.5 m across to a stack at x = 3000: the WC moved 300 mm along, the branch's end at it goes 300 mm
        // with it, its end at the stack stays put.
        var document = BimDocument.CreateDefault();
        var wc = Put(document, "WC - Close Coupled", 0, 1500, 180);
        var soil = Connector(document, wc, ConnectorDomain.Pipe);
        var stack = new Point3D(3000, soil.Y, soil.Z - 30);
        var branch = Run(document, soil, stack);
        var (start, end) = branch.Ends(document);

        var move = new MoveElementsCommand(new[] { wc }, new Vector2D(300, 0), document: document);
        move.Redo();
        Near(Connector(document, wc, ConnectorDomain.Pipe), branch.Ends(document).Start);
        Near(new Point3D(soil.X + 300, soil.Y, soil.Z), branch.Ends(document).Start);
        Near(end, branch.Ends(document).End);

        move.Undo();
        Near(start, branch.Ends(document).Start);
        Near(end, branch.Ends(document).End);

        // Turned a quarter about its own place, the branch's end goes round with its outlet.
        var turn = new RotateElementsCommand(new[] { wc }, wc.Location, 90, document);
        turn.Redo();
        Near(Connector(document, wc, ConnectorDomain.Pipe), branch.Ends(document).Start);
        turn.Undo();
        Near(start, branch.Ends(document).Start);

        // Mirrored across a line beside it, and back.
        var mirror = new MirrorElementsCommand(new[] { wc }, new Line2D(new Point2D(-1000, 0), new Vector2D(0, 1)), document);
        mirror.Redo();
        Near(Connector(document, wc, ConnectorDomain.Pipe), branch.Ends(document).Start);
        mirror.Undo();
        Near(start, branch.Ends(document).Start);

        // Its setting out scaled twice as far from a point, and back.
        var scale = new ScaleElementsCommand(new[] { wc }, new Point2D(-500, 0), 2, document);
        scale.Redo();
        Near(Connector(document, wc, ConnectorDomain.Pipe), branch.Ends(document).Start);
        scale.Undo();
        Near(start, branch.Ends(document).Start);

        // Nudged with the arrow keys, a step at a time, and undone as one.
        var nudge = new NudgeElementsCommand(new[] { wc }, Array.Empty<WallFollower>(), new Vector2D(0, 100), document);
        nudge.Redo();
        nudge.Grow(new Vector2D(0, 100));
        Near(new Point3D(soil.X, soil.Y + 200, soil.Z), branch.Ends(document).Start);
        nudge.Undo();
        Near(start, branch.Ends(document).Start);
    }

    [Fact]
    public void ADragThenSetAgainstAWallKeepsTheBranchOnTheOutletBothWays()
    {
        // As a drag goes: the WC moved 300 live, its branch following; let go, it is set 95 nearer the wall by a step of its own.
        // Recorded as one with the runs following, undone the branch is back on the outlet where it began, done again on it where it ended.
        var document = BimDocument.CreateDefault();
        var wc = Put(document, "WC - Close Coupled", 0, 1500, 180);
        var soil = Connector(document, wc, ConnectorDomain.Pipe);
        var branch = Run(document, soil, new Point3D(3000, soil.Y, soil.Z - 30));
        var start = branch.Ends(document).Start;
        var runs = RunsFollowing.For(document, new[] { wc });

        ElementTransforms.Move(wc, new Vector2D(300, 0));
        runs.Follow();
        var move = new MoveElementsCommand(new[] { wc }, new Vector2D(300, 0), document: document);
        var fit = new MoveElementsCommand(new[] { wc }, new Vector2D(0, -95));
        fit.Redo();
        runs.Follow();
        var ended = Connector(document, wc, ConnectorDomain.Pipe);
        Near(ended, branch.Ends(document).Start);

        var recorded = new FollowingRunsCommand(new CompositeCommand("Move", new IUndoableCommand[] { move, fit }), runs);
        recorded.Undo();
        Near(start, branch.Ends(document).Start);
        recorded.Redo();
        Near(ended, branch.Ends(document).Start);
    }

    [Fact]
    public void AnUprightDropGoesWholeAndWhatMeetsItsFootFollows()
    {
        // A basin's waste drops 300 upright, then runs 2 m across: moved 200 along, the drop goes whole - still upright - and the
        // run's end at its foot follows; the run's far end stays.
        var document = BimDocument.CreateDefault();
        var basin = Put(document, "Basin - Pedestal", 0, 2000);
        var waste = Connector(document, basin, ConnectorDomain.Pipe);
        var foot = new Point3D(waste.X, waste.Y, waste.Z - 300);
        var drop = Run(document, waste, foot, 40);
        var across = Run(document, foot, new Point3D(waste.X + 2000, waste.Y, foot.Z - 40), 40);
        var far = across.Ends(document).End;

        var move = new MoveElementsCommand(new[] { basin }, new Vector2D(200, 0), document: document);
        move.Redo();
        Assert.True(drop.Upright);
        Near(new Point3D(waste.X + 200, waste.Y, waste.Z), drop.Ends(document).Start);
        Near(new Point3D(foot.X + 200, foot.Y, foot.Z), drop.Ends(document).End);
        Near(new Point3D(foot.X + 200, foot.Y, foot.Z), across.Ends(document).Start);
        Near(far, across.Ends(document).End);

        move.Undo();
        Near(waste, drop.Ends(document).Start);
        Near(foot, across.Ends(document).Start);
    }

    [Fact]
    public void APipeMovedTakesTheEndsOfThoseJoinedToItAndADuctFollowsItsValve()
    {
        // Three pipes end to end; the middle one moved 500 across: the ends of the two either side that met it follow, their far ends stay.
        var document = BimDocument.CreateDefault();
        var (a, b, c, d) = (new Point3D(0, 0, -600), new Point3D(2000, 0, -650), new Point3D(4000, 0, -700), new Point3D(6000, 0, -750));
        var first = Run(document, a, b);
        var middle = Run(document, b, c);
        var last = Run(document, c, d);
        var move = new MoveElementsCommand(new[] { middle }, new Vector2D(0, 500), document: document);
        move.Redo();
        Near(new Point3D(b.X, 500, b.Z), first.Ends(document).End);
        Near(a, first.Ends(document).Start);
        Near(new Point3D(c.X, 500, c.Z), last.Ends(document).Start);
        Near(d, last.Ends(document).End);
        move.Undo();
        Near(b, first.Ends(document).End);

        // An extract valve's duct: the valve moved, the duct's end at its spigot goes with it; a pipe nowhere near stays.
        var valve = Put(document, "Extract Valve - 125", 1000, 3000);
        var spigot = Connector(document, valve, ConnectorDomain.Duct);
        var duct = new Duct
        {
            TypeId = document.TypesOf<DuctType>().First().Id, LevelId = document.Levels[0].Id, Start = new Point2D(spigot.X, spigot.Y), End = new Point2D(spigot.X + 3000, spigot.Y),
            StartOffset = spigot.Z, EndOffset = spigot.Z, Width = 125, Height = 125, Kind = SystemKind.ExtractAir
        };
        document.Add(duct);
        var following = RunsFollowing.For(document, new[] { valve });
        Assert.Equal(new Element[] { duct }, following.Runs);
        new MoveElementsCommand(new[] { valve }, new Vector2D(-400, 0), document: document).Redo();
        Near(new Point3D(spigot.X - 400, spigot.Y, spigot.Z), duct.Ends(document).Start);
        Near(new Point3D(spigot.X + 3000, spigot.Y, spigot.Z), duct.Ends(document).End);
        Near(a, first.Ends(document).Start);
    }
}
