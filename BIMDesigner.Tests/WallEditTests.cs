using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

public class WallEditTests
{
    private static (BimDocument Document, UndoStack History, WallType Type, Guid LevelId) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document, new UndoStack(),
            document.TypesOf<WallType>().Single(t => t.Name == "Generic - 200mm"),
            document.Levels.First().Id);
    }

    private static Wall AddWall(
        BimDocument document, WallType type, Guid levelId, Point2D start, Point2D end)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = levelId };
        document.Add(wall);
        return wall;
    }

    // ---- move ------------------------------------------------------------------

    [Fact]
    public void MovingAWallIsUndoable()
    {
        var (document, history, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(5000, 0));

        history.Execute(new MoveWallCommand(wall, wall.Start, wall.End,
            new Point2D(0, 1000), new Point2D(5000, 1000), "Move Wall"));

        Assert.Equal(new Point2D(0, 1000), wall.Start);

        history.Undo();
        Assert.Equal(new Point2D(0, 0), wall.Start);
        Assert.Equal(new Point2D(5000, 0), wall.End);
    }

    [Fact]
    public void MovingAnEndChangesTheLengthAndTheQuantities()
    {
        var (document, history, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(5000, 0));
        var volumeBefore = wall.GetVolume(document);

        history.Execute(new MoveWallCommand(wall, wall.Start, wall.End,
            wall.Start, new Point2D(8000, 0), "Move Wall End"));

        Assert.Equal(8000, wall.Length, precision: 6);
        Assert.Equal(volumeBefore * 8000 / 5000, wall.GetVolume(document), precision: 3);
    }

    // ---- split -----------------------------------------------------------------

    [Fact]
    public void SplittingProducesTwoWallsThatTogetherCoverTheOriginal()
    {
        var (document, history, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(6000, 0));

        history.Execute(new SplitWallCommand(document, wall, new Point2D(2500, 0)));

        var walls = document.Walls.ToList();
        Assert.Equal(2, walls.Count);
        Assert.Equal(6000, walls.Sum(w => w.Length), precision: 6);
        Assert.Equal(2500, wall.Length, precision: 6);
    }

    [Fact]
    public void SplittingKeepsTheOriginalElementRatherThanReplacingIt()
    {
        var (document, history, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(6000, 0));
        wall.Mark = "EW-07";
        var originalId = wall.Id;

        history.Execute(new SplitWallCommand(document, wall, new Point2D(2500, 0)));

        // The near half must still be the same element, keeping its id and its data.
        Assert.Contains(document.Walls, w => w.Id == originalId);
        Assert.Equal("EW-07", document.Walls.Single(w => w.Id == originalId).Mark);
    }

    [Fact]
    public void TheSplitHalvesCarryTheSameTypeAndConstraints()
    {
        var (document, history, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(6000, 0));
        wall.UnconnectedHeight = 2700;
        wall.LocationLine = WallLocationLine.CoreFaceExterior;
        wall.StructuralUsage = StructuralUsage.Bearing;

        var command = new SplitWallCommand(document, wall, new Point2D(2500, 0));
        history.Execute(command);

        Assert.Equal(wall.TypeId, command.Remainder.TypeId);
        Assert.Equal(wall.LevelId, command.Remainder.LevelId);
        Assert.Equal(2700, command.Remainder.UnconnectedHeight, precision: 6);
        Assert.Equal(WallLocationLine.CoreFaceExterior, command.Remainder.LocationLine);
        Assert.Equal(StructuralUsage.Bearing, command.Remainder.StructuralUsage);
    }

    [Fact]
    public void TheTwoHalvesMeetExactlySoTheyJoin()
    {
        var (document, history, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(6000, 0));

        var command = new SplitWallCommand(document, wall, new Point2D(2500, 0));
        history.Execute(command);

        Assert.Equal(wall.End, command.Remainder.Start);
        Assert.True(WallJoins.TouchesAt(command.Remainder, wall.End));
    }

    [Fact]
    public void UndoingASplitRestoresOneWall()
    {
        var (document, history, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(6000, 0));

        history.Execute(new SplitWallCommand(document, wall, new Point2D(2500, 0)));
        history.Undo();

        Assert.Single(document.Walls);
        Assert.Equal(6000, wall.Length, precision: 6);
    }

    // ---- trim and extend -------------------------------------------------------

    [Fact]
    public void TrimShortensAWallThatOvershootsItsTarget()
    {
        var (document, _, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(8000, 0));
        var target = AddWall(document, type, levelId, new Point2D(5000, -2000), new Point2D(5000, 2000));

        Assert.True(WallTrim.TryResolve(wall, target, out var start, out var end));

        Assert.Equal(new Point2D(0, 0), start);
        Assert.Equal(5000, end.X, precision: 6);
        Assert.Equal(0, end.Y, precision: 6);
    }

    [Fact]
    public void ExtendLengthensAWallThatFallsShort()
    {
        var (document, _, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(3000, 0));
        var target = AddWall(document, type, levelId, new Point2D(7000, -2000), new Point2D(7000, 2000));

        Assert.True(WallTrim.TryResolve(wall, target, out _, out var end));

        Assert.Equal(7000, end.X, precision: 6);
    }

    [Fact]
    public void TrimMovesWhicheverEndIsNearerTheCrossing()
    {
        var (document, _, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(8000, 0));
        var target = AddWall(document, type, levelId, new Point2D(1000, -2000), new Point2D(1000, 2000));

        Assert.True(WallTrim.TryResolve(wall, target, out var start, out var end));

        Assert.Equal(1000, start.X, precision: 6);
        Assert.Equal(new Point2D(8000, 0), end);
    }

    [Fact]
    public void ParallelWallsCannotBeTrimmedToEachOther()
    {
        var (document, _, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(5000, 0));
        var target = AddWall(document, type, levelId, new Point2D(0, 2000), new Point2D(5000, 2000));

        Assert.False(WallTrim.TryResolve(wall, target, out _, out _));
    }

    [Fact]
    public void TrimmingToABoundaryThroughAnEndLeavesTheWallAlone()
    {
        var (document, _, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(5000, 0));

        // The boundary already passes through the start, which is the end that would move,
        // so the wall is trimmed to exactly where it already is rather than collapsing.
        var target = AddWall(document, type, levelId, new Point2D(0, -2000), new Point2D(0, 2000));

        Assert.True(WallTrim.TryResolve(wall, target, out var start, out var end));
        Assert.Equal(wall.Start, start);
        Assert.Equal(wall.End, end);
    }

    [Fact]
    public void TrimmingIsUndoable()
    {
        var (document, history, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(8000, 0));
        var target = AddWall(document, type, levelId, new Point2D(5000, -2000), new Point2D(5000, 2000));

        WallTrim.TryResolve(wall, target, out var start, out var end);
        history.Execute(new MoveWallCommand(wall, wall.Start, wall.End, start, end, "Trim Wall"));

        Assert.Equal(5000, wall.Length, precision: 6);

        history.Undo();
        Assert.Equal(8000, wall.Length, precision: 6);
    }
}
