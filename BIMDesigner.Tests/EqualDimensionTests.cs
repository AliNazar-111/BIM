using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Equal (EQ) strings: what a string measures to between its ends set out evenly, read as EQ, and
/// spaced again whenever an end moves; one of the middle moved on its own is refused.
/// </summary>
public class EqualDimensionTests
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

    private static Wall Upright(BimDocument document, double x)
    {
        var wall = new Wall { Start = new Point2D(x, 0), End = new Point2D(x, 3000), TypeId = document.TypesOf<WallType>().First().Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2400 };
        document.Add(wall);
        return wall;
    }

    private static DimensionReference To(Wall wall) => DimensionReference.ToWallLine(wall, DimensionAnchor.WallCentreline, new Point2D(wall.Start.X, 1500));

    /// <summary>A string from the first wall to the last through those between.</summary>
    private static Dimension String(BimDocument document, params Wall[] walls)
    {
        var dimension = new Dimension { LevelId = document.Levels[0].Id, Start = To(walls[0]), End = To(walls[^1]) };
        dimension.Between.AddRange(walls[1..^1].Select(To));
        document.Add(dimension);
        return dimension;
    }

    private static ParameterValue Parameter(BimDocument document, Dimension dimension, ParameterDefinition definition) =>
        dimension.GetInstanceParameters(document).Single(parameter => parameter.Name == definition.Name);

    private static void Set(BimDocument document, UndoStack history, Dimension dimension, ParameterDefinition definition, object value)
    {
        var parameter = Parameter(document, dimension, definition);
        Assert.True(parameter.TrySet(value), parameter.TakeMessage());
        history.Record(parameter.TakeAppliedChange()!);
    }

    [Fact]
    public void MadeEqualTheWallsBetweenItsEndsAreSpacedEvenlyAsOneStep()
    {
        var (document, history, _) = Model();
        var (a, b, c, d) = (Upright(document, 0), Upright(document, 1000), Upright(document, 2500), Upright(document, 6000));
        var dimension = String(document, a, b, c, d);

        // A single dimension has nothing to make equal.
        Assert.DoesNotContain(new Dimension().GetInstanceParameters(document), parameter => parameter.Name == DimensionParameters.Equal.Name);

        var depth = history.Depth;
        Set(document, history, dimension, DimensionParameters.Equal, true);
        Assert.Equal((0.0, 2000.0, 4000.0, 6000.0), (a.Start.X, b.Start.X, c.Start.X, d.Start.X));
        Assert.Equal(depth + 1, history.Depth);
        Assert.Equal(new[] { "EQ", "EQ", "EQ" }, dimension.Labels(document));

        history.Undo();
        Assert.Equal((1000.0, 2500.0, false), (b.Start.X, c.Start.X, dimension.Equal));
    }

    [Fact]
    public void AnEndMovedSpacesTheRestAgainAndAMiddleMovedAloneIsRefused()
    {
        var (document, history, refusals) = Model();
        var (a, b, c, d) = (Upright(document, 0), Upright(document, 1000), Upright(document, 2500), Upright(document, 6000));
        var dimension = String(document, a, b, c, d);
        Set(document, history, dimension, DimensionParameters.Equal, true);

        history.Execute(new MoveElementsCommand(new[] { d }, new Vector2D(600, 0), "Move", document));
        Assert.Equal((2200.0, 4400.0, 6600.0), (Math.Round(b.Start.X, 6), Math.Round(c.Start.X, 6), d.Start.X));
        Assert.Empty(refusals);

        // The whole string moved together stays even: nothing more to do.
        history.Execute(new MoveElementsCommand(new[] { a, b, c, d }, new Vector2D(-500, 0), "Move", document));
        Assert.Empty(refusals);

        history.Execute(new MoveElementsCommand(new[] { b }, new Vector2D(300, 0), "Move", document));
        Assert.Equal(1700, Math.Round(b.Start.X, 6));
        Assert.Contains("equal (EQ)", Assert.Single(refusals));

        // Let go, it moves freely.
        Set(document, history, dimension, DimensionParameters.Equal, false);
        history.Execute(new MoveElementsCommand(new[] { b }, new Vector2D(300, 0), "Move", document));
        Assert.Equal(2000, Math.Round(b.Start.X, 6));
    }

    [Fact]
    public void APinnedWallBetweenCannotBeSpaced()
    {
        var (document, _, _) = Model();
        var (a, b, c) = (Upright(document, 0), Upright(document, 1000), Upright(document, 6000));
        var dimension = String(document, a, b, c);
        b.Pinned = true;

        var parameter = Parameter(document, dimension, DimensionParameters.Equal);
        Assert.False(parameter.TrySet(true));
        Assert.Contains("pinned", parameter.TakeMessage());
        Assert.Equal(1000, b.Start.X);
    }

    [Fact]
    public void ItReadsEqTheValuesOrOneFormulaAndIsKeptWithTheProject()
    {
        var (document, history, _) = Model();
        var dimension = String(document, Upright(document, 0), Upright(document, 900), Upright(document, 2700));
        Set(document, history, dimension, DimensionParameters.Equal, true);

        var display = Parameter(document, dimension, DimensionParameters.EqualityDisplay);
        Assert.True(display.TrySet("Formula"));
        Assert.Matches(@"^2 @ 1350 = 2700", Assert.Single(dimension.Labels(document)));
        Assert.True(display.TrySet("Value"));
        Assert.Equal(2, dimension.Labels(document).Count);
        Assert.DoesNotContain("EQ", dimension.Labels(document));

        dimension.EqualityDisplay = EqualityDisplay.Formula;
        var kept = ProjectFile.FromJson(ProjectFile.ToJson(document)).All<Dimension>().Single();
        Assert.Equal((true, EqualityDisplay.Formula), (kept.Equal, kept.EqualityDisplay));
    }
}
