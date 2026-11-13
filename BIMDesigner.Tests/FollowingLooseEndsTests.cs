using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Round 83: what rounds 79 to 81 left - a fitting fixed to a wall carried with it when the wall moves, and its pipes with it.
/// </summary>
[Collection("Wpf")]
public class FollowingLooseEndsTests
{
    private static (BimDocument Document, Wall Wall, Component Basin, Pipe Waste) WallHung()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First();
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), TypeId = type.Id, LevelId = level, UnconnectedHeight = 2400 };
        document.Add(wall);
        var family = document.TypesOf<ComponentType>().First(each => each.Name == "Basin - Wall Hung");
        var basin = new Component { TypeId = family.Id, TypeKind = family.Kind, LevelId = level, Elevation = family.DefaultElevation };
        Assert.True(basin.HostOn(document, wall, new Point2D(2000, 300)));
        document.Add(basin);
        var at = Connectors.Placed(document, basin).First(placed => placed.Connector.Kind == SystemKind.Foul).At;
        var pipe = new Pipe
        {
            TypeId = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.PvcU).Id, LevelId = level,
            Start = new Point2D(at.X, at.Y), End = new Point2D(at.X + 2000, at.Y), StartOffset = at.Z, EndOffset = at.Z - 40, Diameter = 43, Kind = SystemKind.Foul
        };
        document.Add(pipe);
        return (document, wall, basin, pipe);
    }

    [Fact]
    public void AFittingFixedToAWallMovesWithItAndItsPipesFollow()
    {
        var (document, wall, basin, waste) = WallHung();
        var was = basin.Location;
        var began = waste.Ends(document).Start;
        var move = new MoveElementsCommand(new[] { wall }, new Vector2D(0, 300), document: document);
        move.Redo();
        Assert.Equal(was.Y + 300, basin.Location.Y, 6);
        var outlet = Connectors.Placed(document, basin).First(placed => placed.Connector.Kind == SystemKind.Foul).At;
        Assert.Equal(outlet.Y, waste.Ends(document).Start.Y, 6);
        move.Undo();
        Assert.Equal(was.Y, basin.Location.Y, 6);
        Assert.Equal(began.Y, waste.Ends(document).Start.Y, 6);

        // Nudged a step at a time, the same; mirrored across a line, the same.
        var nudge = new NudgeElementsCommand(new[] { wall }, Array.Empty<WallFollower>(), new Vector2D(0, 100), document);
        nudge.Redo();
        nudge.Grow(new Vector2D(0, 100));
        Assert.Equal(was.Y + 200, basin.Location.Y, 6);
        Assert.Equal(Connectors.Placed(document, basin).First(placed => placed.Connector.Kind == SystemKind.Foul).At.Y, waste.Ends(document).Start.Y, 6);
        nudge.Undo();
        var mirror = new MirrorElementsCommand(new[] { wall }, new Line2D(new Point2D(0, 1000), new Vector2D(1, 0)), document);
        mirror.Redo();
        Assert.Equal(2000 - was.Y, basin.Location.Y, 6);
        Assert.Equal(Connectors.Placed(document, basin).First(placed => placed.Connector.Kind == SystemKind.Foul).At.Y, waste.Ends(document).Start.Y, 6);
        mirror.Undo();
        Assert.Equal(was.Y, basin.Location.Y, 6);
    }

    [Fact]
    public void AValveSlidAlongItsPipeIsOneStepToUndo()
    {
        var (document, _, _, waste) = WallHung();
        var valve = PipeValves.For(waste, new Point2D(waste.Start.X + 500, waste.Start.Y), ValveKind.Servicing);
        new AddPipeValveCommand(document, valve).Redo();
        var from = valve.Along;
        var slide = new SlideValvesCommand(new[] { (valve, from, from + 800) });
        slide.Redo();
        Assert.Equal(from + 800, valve.Along, 6);
        slide.Undo();
        Assert.Equal(from, valve.Along, 6);
    }
}
