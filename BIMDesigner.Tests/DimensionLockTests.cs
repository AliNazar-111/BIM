using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Dimensions that drive the model: a value typed moves what the far end is on; locked, what one
/// measures is kept that far apart - the other follows whatever moves one, in the same step, or the
/// edit is refused and taken back.
/// </summary>
public class DimensionLockTests
{
    private static (BimDocument Document, UndoStack History, List<string> Refusals) Model()
    {
        var document = BimDocument.CreateDefault();
        var history = new UndoStack();
        var refusals = new List<string>();
        history.Hold = command => DimensionLocks.Hold(document, command);
        history.Refused += (_, why) => refusals.Add(why);
        return (document, history, refusals);
    }

    /// <summary>An upright wall, x along, 3 m long.</summary>
    private static Wall Upright(BimDocument document, double x)
    {
        var wall = new Wall { Start = new Point2D(x, 0), End = new Point2D(x, 3000), TypeId = document.TypesOf<WallType>().First().Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2400 };
        document.Add(wall);
        return wall;
    }

    /// <summary>A dimension between two upright walls' centrelines.</summary>
    private static Dimension Between(BimDocument document, Wall a, Wall b)
    {
        var dimension = new Dimension
        {
            LevelId = document.Levels[0].Id,
            Start = DimensionReference.ToWallLine(a, DimensionAnchor.WallCentreline, new Point2D(a.Start.X, 1500)),
            End = DimensionReference.ToWallLine(b, DimensionAnchor.WallCentreline, new Point2D(b.Start.X, 1500))
        };
        document.Add(dimension);
        return dimension;
    }

    private static ParameterValue Parameter(BimDocument document, Dimension dimension, ParameterDefinition definition) =>
        dimension.GetInstanceParameters(document).Single(parameter => parameter.Name == definition.Name);

    /// <summary>A value set in Properties, recorded as the panel records it.</summary>
    private static void Set(BimDocument document, UndoStack history, Dimension dimension, ParameterDefinition definition, object value)
    {
        var parameter = Parameter(document, dimension, definition);
        Assert.True(parameter.TrySet(value), parameter.TakeMessage());
        history.Record(parameter.TakeAppliedChange()!);
    }

    [Fact]
    public void ADimensionsValueTypedMovesWhatItsFarEndIsOn()
    {
        var (document, history, _) = Model();
        var (a, b) = (Upright(document, 0), Upright(document, 4000));
        var dimension = Between(document, a, b);
        Assert.Equal(4000, dimension.Measure(document), 6);

        Set(document, history, dimension, DimensionParameters.Value, 5250.0);
        Assert.Equal(5250, dimension.Measure(document), 6);
        Assert.Equal((0.0, 5250.0), (a.Start.X, b.Start.X));
        history.Undo();
        Assert.Equal(4000, b.Start.X, 6);

        // With its far end pinned, its near end moves instead.
        b.Pinned = true;
        Set(document, history, dimension, DimensionParameters.Value, 3000.0);
        Assert.Equal((1000.0, 4000.0), (a.Start.X, b.Start.X));
    }

    [Fact]
    public void ALockedDimensionTakesWhatItMeasuresAlongWithWhatMoves()
    {
        var (document, history, refusals) = Model();
        var (a, b, c) = (Upright(document, 0), Upright(document, 4000), Upright(document, 7000));
        var first = Between(document, a, b);
        var second = Between(document, b, c);
        Set(document, history, first, DimensionParameters.Locked, true);
        Set(document, history, second, DimensionParameters.Locked, true);
        Assert.Equal((true, 4000.0), (first.Locked, Math.Round(first.LockedLength, 6)));

        // Locked, its value is not typed.
        Assert.True(Parameter(document, first, DimensionParameters.Value).IsReadOnly);

        // One wall moved: the next follows it, and the one after that, as one step.
        var depth = history.Depth;
        history.Execute(new MoveElementsCommand(new[] { a }, new Vector2D(600, 0), "Move", document));
        Assert.Equal((600.0, 4600.0, 7600.0), (a.Start.X, b.Start.X, c.Start.X));
        Assert.Equal(depth + 1, history.Depth);
        Assert.Empty(refusals);

        // Moved along itself, nothing needs to follow.
        history.Execute(new MoveElementsCommand(new[] { a }, new Vector2D(0, 250), "Move", document));
        Assert.Equal(4600, b.Start.X, 6);

        history.Undo();
        history.Undo();
        Assert.Equal((0.0, 4000.0, 7000.0), (a.Start.X, b.Start.X, c.Start.X));
    }

    [Fact]
    public void AnEditALockCannotKeepIsRefusedAndTakenBack()
    {
        var (document, history, refusals) = Model();
        var (a, b) = (Upright(document, 0), Upright(document, 4000));
        var dimension = Between(document, a, b);
        Set(document, history, dimension, DimensionParameters.Locked, true);
        b.Pinned = true;

        var depth = history.Depth;
        history.Execute(new MoveElementsCommand(new[] { a }, new Vector2D(500, 0), "Move", document));
        Assert.Equal((0.0, 4000.0), (a.Start.X, b.Start.X));
        Assert.Equal(depth, history.Depth);
        Assert.Contains("pinned", Assert.Single(refusals));

        // Both ends moved apart at once: refused too.
        b.Pinned = false;
        history.Execute(new MoveElementsCommand(new[] { b }, new Vector2D(300, 0), "Move", document) is var right ? new CompositeCommand("Spread", new IUndoableCommand[] { new MoveElementsCommand(new[] { a }, new Vector2D(-200, 0), "Move", document), right }) : null!);
        Assert.Equal((0.0, 4000.0), (a.Start.X, b.Start.X));
        Assert.Contains("both of what it measures moved", refusals[^1]);

        // Unlocked, it moves freely.
        Set(document, history, dimension, DimensionParameters.Locked, false);
        history.Execute(new MoveElementsCommand(new[] { a }, new Vector2D(500, 0), "Move", document));
        Assert.Equal((500.0, 4000.0), (a.Start.X, b.Start.X));
    }

    [Fact]
    public void ALockIsKeptWithTheProject()
    {
        var (document, history, _) = Model();
        var dimension = Between(document, Upright(document, 0), Upright(document, 3600));
        Set(document, history, dimension, DimensionParameters.Locked, true);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = reopened.All<Dimension>().Single();
        Assert.Equal((true, 3600.0), (kept.Locked, Math.Round(kept.LockedLength, 6)));
    }
}
