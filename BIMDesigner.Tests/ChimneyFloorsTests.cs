using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A chimney with a fireplace on each floor: a flue of its own for each - never one shared - with
/// brick between them; the flue from below rising straight past the fireplace above, beside its
/// opening; each fireplace's own flue leaning across to its place no more than its rule allows.
/// </summary>
public class ChimneyFloorsTests
{
    /// <summary>A two-storey house, 10 x 8 m, under a 40° roof, with a brick chimney and an open fire in the sitting room.</summary>
    private static (BimDocument Document, Chimney Chimney, Level First) House()
    {
        var document = BimDocument.CreateDefault();
        var ground = document.Levels[0];
        var first = document.Levels.OrderBy(level => level.Elevation).First(level => level.Elevation > ground.Elevation + 2000);
        var eaves = first.Elevation + 2700;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = ground.Id, UnconnectedHeight = eaves });

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = ground.Id, HeightOffset = eaves };
        roof.SetBoundary(corners);
        roof.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge()
        });
        document.Add(roof);

        var type = document.TypesOf<ChimneyType>().First(t => t.Construction == ChimneyConstruction.Masonry);
        var chimney = new Chimney
        {
            LevelId = ground.Id, TypeId = type.Id, Location = new Point2D(5000, 2500), Width = type.Width, Depth = type.Depth,
            Flues = 1, Fireplace = ChimneyFireplace.Open
        };
        document.Add(chimney);
        return (document, chimney, first);
    }

    private static Core.Parameters.ParameterValue Setting(BimDocument document, Chimney chimney, Level level) =>
        chimney.GetInstanceParameters(document).Single(parameter => parameter.Name == $"Fireplace on {level.Name}");

    /// <summary>How far a flue leans from upright, at its steepest, as a tangent.</summary>
    private static double SteepestLean(FlueRun run) =>
        run.Path.Zip(run.Path.Skip(1), (a, b) => b.Z - a.Z < 1e-9 ? 0 : a.At.DistanceTo(b.At) / (b.Z - a.Z)).Max();

    [Fact]
    public void AFireplaceUpstairsHasAFlueOfItsOwnAndTheOneFromBelowRisesBesideIt()
    {
        var (document, chimney, first) = House();
        Assert.Contains(first, Chimneys.UpperFloors(document, chimney));

        // Set in Properties: the stack widens to carry a second flue, a withe of brick between.
        var setting = Setting(document, chimney, first);
        Assert.True(setting.TrySet("Open"));
        Assert.Equal(ChimneyFireplace.Open, chimney.FireplaceOn(first.Id));
        Assert.Equal(Chimneys.WidthFor(2), chimney.Width, 6);
        Assert.Contains("widened", setting.Message);

        // A flue each - two at the top, each in its own pot.
        var hearths = Chimneys.Hearths(document, chimney);
        Assert.Equal(new[] { 0, 1 }, hearths.Select(hearth => hearth.Lane));
        Assert.Equal(2, Chimneys.Flues(document, chimney).Count);
        var runs = Chimneys.FlueRuns(document, chimney);
        Assert.Equal(2, runs.Count);

        // Never touching: at every height they share, a withe of brick between their liners.
        var liner = Chimneys.LinerWidth(document, chimney);
        for (var z = runs[1].From; z < Chimneys.Top(document, chimney); z += 50)
        {
            var (a, b) = (runs[0].At(z), runs[1].At(z));
            if (a is { } p && b is { } q) Assert.True(p.DistanceTo(q) >= liner + Chimneys.Withe - 1e-6, $"The flues meet at {z:0}.");
        }

        // Upstairs, the flue from below rises through the breast beside the opening, a jamb between.
        var upstairs = Chimneys.BreastOn(document, chimney, first.Id)!;
        var cut = first.Elevation + 1200;
        var below = runs[0].At(cut)!.Value;
        Assert.True(Polygon2D.Contains(upstairs.Outline, below));
        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        var openingFrom = upstairs.Opening.Min(point => (point - chimney.Location).Dot(along));
        Assert.True(openingFrom - ((below - chimney.Location).Dot(along) + liner / 2) >= Chimneys.Jamb - 1e-6, "No jamb between the flue and the opening.");

        // Each leans across to its place at no more than Approved Document J's 45°, and is upright
        // before the floor above.
        Assert.All(runs, run => Assert.True(SteepestLean(run) <= 1 + 1e-9, $"Flue {run.Lane} leans too far."));
        Assert.True(runs[0].Upright <= first.Elevation);

        // A damper 8 in over each opening; a fire in each.
        var meshes = Chimneys.Meshes(document, chimney);
        var dampers = meshes.Single(mesh => mesh.Description == "Damper").Positions.Select(point => Math.Round(point.Z)).Distinct().ToList();
        foreach (var hearth in hearths)
        {
            var breast = Chimneys.BreastFor(document, chimney, hearth)!;
            Assert.Contains(Math.Round(hearth.Floor + breast.OpeningHeight + Chimneys.DamperAbove), dampers);
        }

        var hearthStones = meshes.Single(mesh => mesh.Description == "Hearth").Positions;
        Assert.All(hearths, hearth => Assert.Contains(hearthStones, point => Math.Abs(point.Z - hearth.Floor) < 1e-6));
    }

    [Fact]
    public void UnderTheUsRuleAFlueLeansNoMoreThan30Degrees()
    {
        var (document, chimney, first) = House();
        chimney.Rule = ChimneyRule.ThreeTwoTen;
        Assert.True(Setting(document, chimney, first).TrySet("Open"));

        var tan30 = Math.Tan(30 * Math.PI / 180);
        Assert.All(Chimneys.FlueRuns(document, chimney), run => Assert.True(SteepestLean(run) <= tan30 + 1e-9));
        Assert.Contains(Chimneys.FlueRuns(document, chimney), run => SteepestLean(run) > tan30 - 1e-6);
    }

    [Fact]
    public void WhereTheFlueCannotLeanAcrossInTheRoomUnderTheRoofTheFireplaceIsRefusedSayingWhy()
    {
        var (document, chimney, first) = House();
        chimney.Rule = ChimneyRule.ThreeTwoTen;

        // The first floor only just under the roof there: room for a breast, not for the flue over a stove to lean 30°.
        first.Elevation = Chimneys.HighestContact(document, chimney)!.Value - 600 - Chimneys.LeastBreast;
        Assert.Contains(first, Chimneys.UpperFloors(document, chimney));

        var setting = Setting(document, chimney, first);
        Assert.False(setting.TrySet("Stove"));
        Assert.Contains("30°", setting.Message);
        Assert.Equal(ChimneyFireplace.None, chimney.FireplaceOn(first.Id));
        Assert.Equal(450, chimney.Width, 6);
    }

    [Fact]
    public void ItIsSavedCopiedAndUndone()
    {
        var (document, chimney, first) = House();
        var command = SetChimneyFireplaceCommand.For(document, chimney, first.Id, ChimneyFireplace.Stove, out _)!;
        command.Redo();

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Chimney>().Single();
        Assert.Equal(ChimneyFireplace.Stove, loaded.FireplaceOn(first.Id));
        Assert.Equal(ChimneyFireplace.Stove, ((Chimney)ElementCopy.Clone(chimney)!).FireplaceOn(first.Id));

        command.Undo();
        Assert.Empty(chimney.FireplacesAbove);
        Assert.Equal(450, chimney.Width, 6);
    }

    [Fact]
    public void UpstairsThePlanShowsItsBreastWithBothFlues()
    {
        var (document, chimney, first) = House();
        Assert.True(Setting(document, chimney, first).TrySet("Open"));

        var breast = Chimneys.BreastOn(document, chimney, first.Id)!;
        Assert.Equal(breast.Outline, Chimneys.Footprint(document, chimney, first.Id));
        Assert.Equal(2, Chimneys.FluesAt(document, chimney, first.Elevation + 1200).Count);

        // Downstairs, under the upstairs fireplace, only the one flue - its own.
        Assert.Single(Chimneys.FluesAt(document, chimney, 1200));
    }
}
