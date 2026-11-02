using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Fill patterns of one's own - made, or read from and written to .pat files - hatched cut to an
/// outline in their dashes; and details repeated along a line at their true size.
/// </summary>
public class PatternAndRepeatingDetailTests
{
    /// <summary>Two of the standard patterns drawing programs ship, in inches, with a comment, a line that is not a pattern's, and a pattern with nothing in it.</summary>
    private const string Pat = """
        ;%UNITS=INCH
        ; Standard hatches
        *ANSI31, ANSI Iron, Brick, Stone masonry
        45, 0,0, 0,.125
        *BRICK,Brick or masonry-type surface
        ;%TYPE=MODEL
        0, 0,0, 0,.25
        90, 0,0, .25,.25, .25,-.25
        not, a, line
        *EMPTY, nothing
        """;

    private static readonly IReadOnlyList<Point2D> Square = new[] { new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 100), new Point2D(0, 100) };

    [Fact]
    public void APatFileIsReadInMillimetresWithWhatEachPatternIsSizedAgainst()
    {
        var patterns = FillPatterns.Read(Pat, out var problems);
        Assert.Equal(new[] { "ANSI31", "BRICK" }, patterns.Select(pattern => pattern.Name));
        var (ansi, brick) = (patterns[0], patterns[1]);
        Assert.Equal("ANSI Iron, Brick, Stone masonry", ansi.Description);
        Assert.False(ansi.Model);
        Assert.True(brick.Model);

        var hatch = Assert.Single(ansi.Lines);
        Assert.Equal((45.0, 3.175), (hatch.Angle, Math.Round(hatch.Spacing, 6)));
        var upright = brick.Lines[1];
        Assert.Equal((90.0, 6.35, 6.35), (upright.Angle, Math.Round(upright.Shift, 6), Math.Round(upright.Spacing, 6)));
        Assert.Equal(new[] { 6.35, -6.35 }, upright.Dashes.Select(dash => Math.Round(dash, 6)));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("BRICK"));
        Assert.Contains(problems, problem => problem.Contains("EMPTY"));

        // Written out in millimetres and read back, the same.
        var again = FillPatterns.Read(FillPatterns.Write(patterns), out var none);
        Assert.Empty(none);
        Assert.Equal(patterns.Select(pattern => (pattern.Name, pattern.Model, pattern.Lines.Count)), again.Select(pattern => (pattern.Name, pattern.Model, pattern.Lines.Count)));
        Assert.Equal(upright.Dashes.Select(dash => Math.Round(dash, 5)), again[1].Lines[1].Dashes.Select(dash => Math.Round(dash, 5)));
    }

    [Fact]
    public void APatternHatchesAnOutlineInItsDashesAndDots()
    {
        var level = FillPatterns.Simple("Level", 0, 10, crossed: false, model: true);
        var whole = FillPatterns.Hatch(Square, level, 1, 0.2);
        Assert.Equal(10, whole.Count);
        Assert.All(whole, line => Assert.Equal(100, line.From.DistanceTo(line.To), 6));

        Assert.InRange(FillPatterns.Hatch(Square, FillPatterns.Simple("Grid", 0, 10, crossed: true, model: true), 1, 0.2).Count, 20, 21);

        // Dashed 5 on, 5 off: half of each line drawn.
        var dashed = new CustomFillPattern { Model = true };
        dashed.Lines.Add(new PatternLine(0, 0, 0, 0, 10, new[] { 5.0, -5.0 }));
        var dashes = FillPatterns.Hatch(Square, dashed, 1, 0.2);
        Assert.Equal(10 * 10, dashes.Count);
        Assert.Equal(10 * 50, dashes.Sum(line => line.From.DistanceTo(line.To)), 6);

        // A dot every 5, drawn as a short stroke; at 1:2 everything twice the size, half as many.
        var dotted = new CustomFillPattern();
        dotted.Lines.Add(new PatternLine(0, 0, 0, 0, 10, new[] { 0.0, -5.0 }));
        Assert.All(FillPatterns.Hatch(Square, dotted, 1, 0.2), line => Assert.Equal(0.2, line.From.DistanceTo(line.To), 6));
        Assert.Equal(5, FillPatterns.Hatch(Square, level, 2, 0.2).Count);
    }

    [Fact]
    public void ARegionFilledWithTheProjectsPatternKeepsItAndADeletedPatternLeavesItsOwn()
    {
        var document = BimDocument.CreateDefault();
        var history = new UndoStack();
        var patterns = FillPatterns.Read(Pat, out _);
        history.Execute(FillPatterns.Add(document, patterns));
        Assert.Equal(2, document.FillPatterns.Count);

        // A name the program's own patterns already have is numbered on.
        var clash = FillPatterns.Simple("Crosshatch", 45, 2, crossed: true, model: false);
        history.Execute(FillPatterns.Add(document, new[] { clash }));
        Assert.Equal("Crosshatch 2", clash.Name);

        var region = new FilledRegion { LevelId = document.Levels[0].Id, Pattern = FillPattern.Horizontal };
        region.SetBoundary(Square);
        document.Add(region);
        var pattern = region.GetInstanceParameters(document).Single(parameter => parameter.Name == DetailingParameters.Pattern.Name);
        Assert.True(pattern.TrySet("BRICK"));
        Assert.Equal(patterns[1].Id, region.CustomPatternId);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = reopened.All<FilledRegion>().Single();
        Assert.Equal("BRICK", FillPatterns.Find(reopened, kept.CustomPatternId)?.Name);
        Assert.Equal(new[] { 6.35, -6.35 }, reopened.FillPatterns.Single(each => each.Name == "BRICK").Lines[1].Dashes.Select(dash => Math.Round(dash, 6)));

        history.Execute(FillPatterns.Remove(document, patterns[1]));
        Assert.Null(region.CustomPatternId);
        Assert.Equal(FillPattern.Horizontal, region.Pattern);
        history.Undo();
        Assert.Equal(patterns[1].Id, region.CustomPatternId);
    }

    private static RepeatingDetail Detail(RepeatingKind kind, double length, bool us = false)
    {
        var (width, unit, spacing) = Detailing.Defaults(kind, us);
        return new RepeatingDetail { Start = new Point2D(0, 0), End = new Point2D(length, 0), Kind = kind, Width = width, Unit = unit, Spacing = spacing };
    }

    [Fact]
    public void BrickCoursesAreLaidWholeAlongTheLineToItsLeftOrItsRightFlipped()
    {
        var brick = Detail(RepeatingKind.Brick, 1000);
        Assert.Equal(13, Detailing.Pieces(brick));
        var strokes = Detailing.Strokes(brick);
        Assert.Equal(13, strokes.Count);
        Assert.All(strokes, stroke => Assert.All(stroke, point => Assert.InRange(point.Y, 0, 102.5)));
        Assert.Equal(12 * 75 + 65, strokes.SelectMany(stroke => stroke).Max(point => point.X), 6);

        brick.Flipped = true;
        Assert.All(Detailing.Strokes(brick), stroke => Assert.All(stroke, point => Assert.InRange(point.Y, -102.5, 0)));

        // In the US, a modular brick: three courses to 8 inches.
        var us = Detail(RepeatingKind.Brick, 203.2 * 3, us: true);
        Assert.Equal(9, Detailing.Pieces(us));

        // Timbers at centres, each crossed corner to corner as cut timber is.
        var timbers = Detail(RepeatingKind.Timber, 2000);
        Assert.Equal(5, Detailing.Pieces(timbers));
        Assert.Equal(15, Detailing.Strokes(timbers).Count);
    }

    [Fact]
    public void BattInsulationLoopsBetweenItsFacesAndMirroredItIsDrawnOnTheOtherSide()
    {
        var batt = Detail(RepeatingKind.BattInsulation, 600);
        var loop = Assert.Single(Detailing.Strokes(batt));
        Assert.All(loop, point => Assert.InRange(point.Y, 0, 100));
        Assert.All(loop, point => Assert.InRange(point.X, 0, 600));
        Assert.Equal(12, Detailing.Pieces(batt));

        var document = BimDocument.CreateDefault();
        batt.LevelId = document.Levels[0].Id;
        document.Add(batt);
        new MirrorElementsCommand(new[] { batt }, new Line2D(new Point2D(0, -500), new Vector2D(1, 0)), document).Redo();
        Assert.True(batt.Flipped);
        Assert.All(Detailing.Strokes(batt).Single(), point => Assert.InRange(point.Y, -1100, -1000));

        var kept = ProjectFile.FromJson(ProjectFile.ToJson(document)).All<RepeatingDetail>().Single();
        Assert.Equal((RepeatingKind.BattInsulation, true, 100.0), (kept.Kind, kept.Flipped, kept.Width));
    }
}
