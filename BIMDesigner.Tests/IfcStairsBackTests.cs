using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;

namespace BIMDesigner.Tests;

/// <summary>
/// Stairs back from IFC as stairs: a straight flight, and two flights at a quarter or half turn with a landing between - from
/// the middle of the first riser, up the first flight, turning the way the second goes, so many risers to the landing, the well
/// between - between the same storeys, with the going and rise they were drawn with. Winders come in as their shape.
/// </summary>
public class IfcStairsBackTests
{
    private static (BimDocument Document, Stair Stair) Straight(StairShape shape = StairShape.Straight)
    {
        var document = BimDocument.CreateDefault();
        var stair = new Stair
        {
            TypeId = document.TypesOf<StairType>().First().Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id,
            Start = new Point2D(2000, 1000), Angle = 90, Width = 900, Shape = shape, Mark = "ST1"
        };
        document.Add(stair);
        return (document, stair);
    }

    private static IfcImportResult Back(BimDocument document)
    {
        using var model = IfcExport.Build(document);
        return IfcImport.Read(model, "Stair");
    }

    [Fact]
    public void AStraightStairComesBackAsAStair()
    {
        var (document, original) = Straight();
        var result = Back(document);
        Assert.Equal(1, result["Stairs"]);
        var stair = Assert.Single(result.Document.All<Stair>());

        // Up the same way from the same first riser, as wide, between the same storeys - so with the same risers.
        Assert.Equal(StairShape.Straight, stair.Shape);
        Assert.Equal(90, stair.Angle, 1);
        Assert.Equal(original.Start.X, stair.Start.X, 0);
        Assert.InRange(stair.Start.Y - original.Start.Y, -30, 30);
        Assert.InRange(stair.Width, 880, 1000);
        Assert.Equal(document.FindLevel(original.TopLevelId!.Value)!.Name, result.Document.FindLevel(stair.TopLevelId!.Value)!.Name);
        Assert.Equal("ST1", stair.Mark);
        Assert.Equal(Stairs.Layout(document, original).Risers, Stairs.Layout(result.Document, stair).Risers);
        Assert.Equal(Stairs.TypeOf(document, original).Name, Stairs.TypeOf(result.Document, stair).Name);
        Assert.Empty(result.Document.All<Component>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnLStairComesBackAsAQuarterTurnWithItsLanding(bool turnsLeft)
    {
        // Sixteen risers or so from the ground floor to the first, six of them to the landing, turning right - or left.
        var (document, original) = Straight(StairShape.QuarterTurn);
        (original.TurnsLeft, original.RisersBeforeTurn) = (turnsLeft, 6);
        var result = Back(document);
        Assert.Equal(1, result["Stairs"]);
        var stair = Assert.Single(result.Document.All<Stair>());
        Assert.Equal((StairShape.QuarterTurn, StairCorner.Landing, turnsLeft, 6), (stair.Shape, stair.Corner, stair.TurnsLeft, stair.RisersBeforeTurn));
        Assert.Equal(90, stair.Angle, 1);
        Assert.InRange(stair.Start.DistanceTo(original.Start), 0, 30);
        Assert.InRange(stair.Width, 880, 1000);

        // Laid out again, its landing where the original's is, at its height, and its risers as many.
        var (was, now) = (Stairs.Layout(document, original), Stairs.Layout(result.Document, stair));
        Assert.Equal(was.Risers, now.Risers);
        var (before, after) = (was.Steps.Single(step => step.Kind == StairStepKind.Landing), now.Steps.Single(step => step.Kind == StairStepKind.Landing));
        Assert.Equal(before.Top, after.Top, 0);
        Assert.InRange(Polygon2D.Centroid(after.Outline, after.Outline[0]).DistanceTo(Polygon2D.Centroid(before.Outline, before.Outline[0])), 0, 40);
        Assert.Empty(result.Document.All<Component>());

        // Its landing taken in with it, not again as a floor of its own.
        Assert.Empty(result.Document.All<Floor>());
    }

    [Fact]
    public void AUStairComesBackAsAHalfTurnWithItsWell()
    {
        // Back down the other side of a 300 well, turning left.
        var (document, original) = Straight(StairShape.HalfTurn);
        (original.TurnsLeft, original.Well) = (true, 300);
        var result = Back(document);
        var stair = Assert.Single(result.Document.All<Stair>());
        Assert.Equal((StairShape.HalfTurn, true), (stair.Shape, stair.TurnsLeft));
        Assert.InRange(stair.Well, 270, 330);
        Assert.Equal(Stairs.Layout(document, original).Risers, Stairs.Layout(result.Document, stair).Risers);
        Assert.Equal(original.RisersBeforeTurn ?? Stairs.Layout(document, original).Steps.Single(step => step.Kind == StairStepKind.Landing).Number, stair.RisersBeforeTurn);
    }

    [Fact]
    public void AStairOfAGoingNoTypeHasComesWithATypeOfItsOwnAndWindersComeInAsTheirShape()
    {
        // A timber stair of 260 goings and 190 risers at most: no type here has them, so one is made from the one it is named for.
        var (document, original) = Straight();
        var steep = Stairs.TypeOf(document, original).Duplicate("Stair - Timber, Closed Risers 260");
        (steep.Going, steep.MaxRiser) = (260, 190);
        document.AddType(steep);
        original.TypeId = steep.Id;
        var result = Back(document);
        var stair = Assert.Single(result.Document.All<Stair>());
        var type = Stairs.TypeOf(result.Document, stair);
        Assert.Equal(260, type.Going, 0);
        Assert.StartsWith("Stair - Timber, Closed Risers", type.Name);
        Assert.Equal(Stairs.Layout(document, original).Risers, Stairs.Layout(result.Document, stair).Risers);

        // Winders round a newel at the turn: not a landing - the stair comes in as its shape.
        var (winding, turning) = Straight(StairShape.QuarterTurn);
        turning.Corner = StairCorner.Winders;
        var back = Back(winding);
        Assert.Empty(back.Document.All<Stair>());
        Assert.NotEmpty(back.Document.All<Component>());
    }

    /// <summary>A stair along a walking line - 15 risers of 200 at 225 goings, 900 wide - of these stretches and turns.</summary>
    private static (BimDocument Document, Stair Stair) Drawn(double[] legs, double[] turns)
    {
        var (document, stair) = Straight(StairShape.Custom);
        stair.Corner = StairCorner.Landing;
        stair.PathLengths.AddRange(legs);
        stair.PathTurns.AddRange(turns);
        return (document, stair);
    }

    /// <summary>Two stairs' landings - their tops and middles - and how many treads each flight between them has.</summary>
    private static void SameLandingsAndFlights(BimDocument was, Stair before, BimDocument now, Stair after)
    {
        var (a, b) = (Stairs.Layout(was, before), Stairs.Layout(now, after));
        Assert.Equal(a.Risers, b.Risers);
        var (one, two) = (a.Steps.Where(step => step.Kind == StairStepKind.Landing).ToList(), b.Steps.Where(step => step.Kind == StairStepKind.Landing).ToList());
        Assert.Equal(one.Select(step => step.Number), two.Select(step => step.Number));
        foreach (var (x, y) in one.Zip(two))
        {
            Assert.Equal(x.Top, y.Top, 0);
            Assert.InRange(Polygon2D.Centroid(y.Outline, y.Outline[0]).DistanceTo(Polygon2D.Centroid(x.Outline, x.Outline[0])), 0, 40);
        }
    }

    [Fact]
    public void AStraightRunBrokenByALandingComesBackAlongItsWalkingLine()
    {
        // 13 treads and a landing: 7 then 6 - stretches of 7 x 225 + 450 and 6 x 225 + 450, straight on.
        var (document, original) = Drawn(new[] { 2025.0, 1800.0 }, new[] { 0.0 });
        var result = Back(document);
        var stair = Assert.Single(result.Document.All<Stair>());
        Assert.Equal((StairShape.Custom, StairCorner.Landing), (stair.Shape, stair.Corner));
        Assert.Equal(new[] { 0.0 }, stair.PathTurns);
        Assert.Equal(new[] { 2025.0, 1800.0 }, stair.PathLengths.Select(length => Math.Round(length, 1)));
        Assert.InRange(stair.Start.DistanceTo(original.Start), 0, 30);
        SameLandingsAndFlights(document, original, result.Document, stair);
        Assert.Empty(result.Document.All<Component>());
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(-90.0)]
    public void AThreeQuarterTurnComesBackOnItsLandings(double turn)
    {
        // 12 treads and two landings, four to each flight - right round, or left round.
        var (document, original) = Drawn(new[] { 1350.0, 1800.0, 1350.0 }, new[] { turn, turn });
        var result = Back(document);
        var stair = Assert.Single(result.Document.All<Stair>());
        Assert.Equal(StairShape.Custom, stair.Shape);
        Assert.Equal(new[] { turn, turn }, stair.PathTurns);
        SameLandingsAndFlights(document, original, result.Document, stair);
        Assert.Empty(result.Document.All<Floor>());
    }
}
