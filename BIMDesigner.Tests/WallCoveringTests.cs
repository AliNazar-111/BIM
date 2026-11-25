using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Rendering;

namespace BIMDesigner.Tests;

/// <summary>
/// Board and batten, shingles and stucco on US homes' walls (round 141): drawn as they are laid - battens standing proud over the boards' joints,
/// shingles in courses whose butts stand proud and shade the course below, stucco's coarse float finish - and asphalt shingles no longer taken
/// for a beach's; and the IRC near the ground: wood siding 6 in clear or durable (R317.1), stucco's weep screed 4 in up (R703.7.2.1), wood
/// shingles over 1/2 in wood-based sheathing (R703.6.1).
/// </summary>
[Collection("Wpf")]
public class WallCoveringTests
{
    private const string Batten = "Wood Frame 2x6 - Board and Batten", Shingled = "Wood Frame 2x6 - Cedar Shingles", Stuccoed = "Wood Frame 2x6 - Stucco";

    [Fact]
    public void BoardAndBattenShinglesAndStuccoAreDrawnAsTheyAreLaid()
    {
        Assert.Equal(SurfacePattern.BoardAndBatten, SurfacePatterns.Guess("Board and Batten, Painted Pine", new ColourRgb(200, 200, 200)).Pattern);
        Assert.Equal(SurfacePattern.Shingles, SurfacePatterns.Guess("Cedar Shingles", new ColourRgb(160, 120, 80)).Pattern);
        Assert.Equal(SurfacePattern.Stucco, SurfacePatterns.Guess("Stucco, Three-Coat Portland Cement", new ColourRgb(220, 210, 190)).Pattern);
        var asphalt = SurfacePatterns.Guess("Asphalt Shingles", new ColourRgb(70, 70, 75));
        Assert.Equal((SurfacePattern.Shingles, 333.0, 143.0), (asphalt.Pattern, asphalt.UnitLength, asphalt.UnitHeight));
        Assert.Equal(SurfacePattern.Gravel, SurfacePatterns.Guess("Pea Shingle", new ColourRgb(150, 150, 140)).Pattern);

        // The library's materials take them.
        var document = BimDocument.CreateDefault();
        foreach (var (name, pattern) in new[] { ("Board and Batten, Painted Pine", SurfacePattern.BoardAndBatten), ("Cedar Shingles", SurfacePattern.Shingles),
                     ("Stucco, Three-Coat Portland Cement", SurfacePattern.Stucco), ("Asphalt Shingles", SurfacePattern.Shingles) })
            Assert.Equal(pattern, document.Materials.Single(material => material.Name == name).Pattern);

        // Board and batten: four boards 305 wide across, 2.4 m up; a batten over each joint stands proud of the board and casts its shadow on the
        // board to its right.
        var colour = new ColourRgb(200, 190, 170);
        var battens = SurfaceTextures.Of(SurfacePatterns.Standard(SurfacePattern.BoardAndBatten, colour))!;
        Assert.Equal((4 * 305.0, 2400.0), (battens.Length, battens.Rise));
        Assert.True(battens.ReliefAt(305, 1200) > battens.ReliefAt(305 + 152, 1200) + 0.4);
        static double Bright(ColourRgb c) => c.R + c.G + c.B;
        Assert.True(Bright(battens.At(305 + 32 + 2, 1200)) < 0.8 * Bright(battens.At(305 + 152, 1200)));

        // Shingles: each course thicker at its butt than at its top, under the course above's butt.
        var shingles = SurfaceTextures.Of(SurfacePatterns.Standard(SurfacePattern.Shingles, colour))!;
        Assert.Equal(8 * 127.0, shingles.Rise);
        var across = Enumerable.Range(0, 40).Select(i => i * shingles.Length / 40).ToList();
        double Relief(double fromTop) => across.Average(u => shingles.ReliefAt(u, shingles.Rise - fromTop));
        Assert.True(Relief(127 - 6) > Relief(20) + 0.4, $"{Relief(127 - 6)} against {Relief(20)}");

        // Stucco: a grain, no units.
        Assert.NotNull(SurfaceTextures.Of(SurfacePatterns.Standard(SurfacePattern.Stucco, colour)));
    }

    /// <summary>A US house 10 m by 8 m of a wall type on flat ground at a height, a paved patio along its south wall where asked.</summary>
    private static (BimDocument Document, Wall South) House(string type, double ground, bool patio = false, bool us = true)
    {
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var level = document.Levels.OrderBy(each => each.Elevation).First(each => each.Elevation >= 0).Id;
        var id = document.TypesOf<WallType>().First(each => each.Name == type).Id;
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        Wall? south = null;
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            south ??= wall;
        }

        var surface = new Toposurface { LevelId = level };
        var points = new List<Point3D>();
        for (var x = -8000.0; x <= 18000; x += 4000)
        for (var y = -8000.0; y <= 16000; y += 4000)
            points.Add(new Point3D(x, y, ground));
        surface.SetPoints(points);
        document.Add(surface);
        if (patio)
        {
            var paving = new GroundRegion { LevelId = level, Surface = GroundSurface.Paving };
            paving.SetBoundary(new[] { new Point2D(-500, -3000), new Point2D(10500, -3000), new Point2D(10500, 0), new Point2D(-500, 0) });
            document.Add(paving);
        }

        return (document, south!);
    }

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Wall wall) => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard);

    [Fact]
    public void WoodSidingClearsTheGroundBySixInchesUnlessDurable()
    {
        // Painted pine board and batten down to the ground: R317.1 item 5.
        var (document, south) = House(Batten, 0);
        var said = Assert.Single(Problems(document, south), problem => problem.Rule.Contains("(R317.1"));
        Assert.Equal("its Board and Batten, Painted Pine comes down to the ground or below it outside, and is neither naturally durable nor preservative-treated.", said.Said);

        // 100 clear of the earth: still short of 6 in; of a patio, more than the 2 in asked.
        var (lowDocument, lowSouth) = House(Batten, -100);
        Assert.Contains(Problems(lowDocument, lowSouth), problem => problem.Rule.Contains("(R317.1"));
        var (patioDocument, patioSouth) = House(Batten, -100, patio: true);
        Assert.DoesNotContain(Problems(patioDocument, patioSouth), problem => problem.Rule.Contains("(R317.1"));

        // 200 clear: nothing asked.
        var (clearDocument, clearSouth) = House(Batten, -200);
        Assert.DoesNotContain(Problems(clearDocument, clearSouth), problem => problem.Rule.Contains("(R317.1"));

        // Cedar shingles are naturally durable; so is the board treated.
        var (cedarDocument, cedarSouth) = House(Shingled, 0);
        Assert.DoesNotContain(Problems(cedarDocument, cedarSouth), problem => problem.Rule.Contains("(R317.1"));
        document.Materials.Single(material => material.Name == "Board and Batten, Painted Pine").Name = "Board and Batten, Pressure-Treated Pine";
        Assert.DoesNotContain(Problems(document, south), problem => problem.Rule.Contains("(R317.1"));

        // No ground modelled, or England: nothing asked.
        var (bare, bareSouth) = House(Batten, 0);
        bare.Remove(bare.All<Toposurface>().Single());
        Assert.DoesNotContain(Problems(bare, bareSouth), problem => problem.Rule.Contains("(R317.1"));
        var (england, englandSouth) = House(Batten, 0, us: false);
        Assert.DoesNotContain(Problems(england, englandSouth), problem => problem.Rule.Contains("IRC"));
    }

    [Fact]
    public void StuccosWeepScreedIsFourInchesOverTheEarth()
    {
        var (document, south) = House(Stuccoed, -80);
        var said = Assert.Single(Problems(document, south), problem => problem.Rule.Contains("(R703.7.2.1)"));
        Assert.StartsWith("its Stucco, Three-Coat Portland Cement's weep screed, at the foundation plate line, is ", said.Said);
        Assert.EndsWith(" over the earth outside.", said.Said);

        var (high, highSouth) = House(Stuccoed, -120);
        Assert.DoesNotContain(Problems(high, highSouth), problem => problem.Rule.Contains("(R703.7.2.1)"));
        var (paved, pavedSouth) = House(Stuccoed, -60, patio: true);
        Assert.DoesNotContain(Problems(paved, pavedSouth), problem => problem.Rule.Contains("(R703.7.2.1)"));
    }

    [Fact]
    public void WoodShinglesGoOverHalfInchWoodSheathing()
    {
        var (document, south) = House(Shingled, -300);
        Assert.DoesNotContain(Problems(document, south), problem => problem.Rule.Contains("(R703.6.1)"));

        // The plywood taken out of the type: shingles straight on the studs' insulation.
        var type = document.GetWallType(south)!;
        var layers = type.Structure.Layers.Where(layer => layer.Function != LayerFunction.Substrate).ToList();
        type.Structure.ReplaceLayers(layers);
        var said = Assert.Single(Problems(document, south), problem => problem.Rule.Contains("(R703.6.1)"));
        Assert.StartsWith("it is clad in Cedar Shingles over ", said.Said);
    }
}
