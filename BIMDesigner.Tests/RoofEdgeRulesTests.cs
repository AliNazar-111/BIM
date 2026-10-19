using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A roof's edges against the rules: its gutters sized for the water off it and its downpipes
/// for their outlets, as Approved Document H asks, and its soffits airing a cold roof.
/// </summary>
public class RoofEdgeRulesTests
{
    /// <summary>A house 10 m by 8 m under a 40° gable roof overhanging its eaves by 400, a gutter along both eaves.</summary>
    private static (BimDocument Document, Roof Roof, Gutter Gutter, GutterType Type) Guttered()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        roof.SetBoundary(new[] { new Point2D(0, -400), new Point2D(10000, -400), new Point2D(10000, 8400), new Point2D(0, 8400) });
        roof.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40, Overhang = 400 }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40, Overhang = 400 }, new RoofEdge()
        });
        var type = document.TypesOf<GutterType>().Single(candidate => candidate.Name == "Gutter - Half Round 125");
        var gutter = new Gutter { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id };
        gutter.EdgeIds.AddRange(RoofEdgeSweeps.GutterEdges(document, roof));
        document.Add(gutter);
        return (document, roof, gutter, type);
    }

    [Fact]
    public void TheTablesAreApprovedDocumentHs()
    {
        // Table 1: a pitched roof's plan area counts 1.29 times at 30°, 1.50 at 45°, 1.87 at 60°.
        Assert.Equal(1.29, RoofEdgeRules.EffectiveFactor(Math.Tan(Math.PI / 6)), 2);
        Assert.Equal(1.50, RoofEdgeRules.EffectiveFactor(1), 2);
        Assert.Equal(1.87, RoofEdgeRules.EffectiveFactor(Math.Tan(Math.PI / 3)), 2);

        // Table 2, and a 112 between its 100 and 115.
        Assert.Equal(65, RoofEdgeRules.Capacity(125), 6);
        Assert.Equal(49.8, RoofEdgeRules.Capacity(112), 1);
        Assert.Equal((115.0, 63.0), RoofEdgeRules.Asked(40));
        Assert.Null(RoofEdgeRules.Asked(120));

        // A box 100 by 75 holds as much as a 138 half round; past 50 times its depth a gutter takes less.
        Assert.Equal(138, RoofEdgeRules.HalfRoundSize(new GutterType("Box") { Shape = GutterShape.Box, Width = 100, Depth = 75 }), 0);
        Assert.Equal(1, RoofEdgeRules.LongFactor(3000, 62.5), 6);
        Assert.Equal(0.96, RoofEdgeRules.LongFactor(5000, 62.5), 6);
    }

    [Fact]
    public void AGutterIsSizedForTheRoofDrainingIntoItAndItsDownpipesForTheirOutlets()
    {
        var (document, _, gutter, type) = Guttered();
        var standard = document.ProjectInformation.ClearanceStandard;

        // 10 m by 4.4 m a side at 40°: 62.5 m² of effective area into each eave - and no downpipe to take it.
        var problems = RoofEdgeRules.Problems(document, gutter, standard);
        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.Contains("62.5 m² of roof draining into it has no downpipe", problem.Said));
        Assert.StartsWith("124.9 m² of roof drains into it; a 125 half round takes 65 m²", RoofEdgeRules.Verdict(document, gutter));

        // A downpipe halfway along each: 31 m² a side, which it takes; but 62.5 m² wants a 75 outlet, more than a 68 pipe.
        Downpipe Pipe(double x, double y)
        {
            var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = new Point2D(x, y) };
            document.Add(pipe);
            return pipe;
        }

        var south = Pipe(5000, -400);
        Pipe(5000, 8400);
        problems = RoofEdgeRules.Problems(document, gutter, standard);
        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.Contains("wants an outlet of 75", problem.Said));
        type.DownpipeWidth = 80;
        Assert.Empty(RoofEdgeRules.Problems(document, gutter, standard));
        Assert.EndsWith("big enough", RoofEdgeRules.Verdict(document, gutter));

        // At the end of its eave, all 62.5 m² comes 10 m along to it: more than a 125 takes so far - a 150 is asked.
        south.Location = new Point2D(9990, -400);
        var problem = Assert.Single(RoofEdgeRules.Problems(document, gutter, standard));
        Assert.Contains("more than a 125 half round takes", problem.Said);
        Assert.Contains("150 half-round gutter", problem.Rule);
        Assert.Contains(Clearances.Blocked(document, gutter.LevelId, standard), blocked => blocked.Zone.Element == gutter);

        // The IRC sizes no gutter.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Empty(RoofEdgeRules.Problems(document, gutter, ClearanceStandard.Irc));
    }

    [Fact]
    public void ADownpipeOntoALowerRoofFromMoreThan25SquareMetresHasADistributor()
    {
        // A shed dormer 9 m wide: its gutter's downpipe lets the water off it onto the main roof.
        var (document, main, wallType) = DormerToolTests.House(14000, 10000, 40);
        Dormers.Add(document, main, new Point2D(7000, 800), DormerSettings.Default with { Shape = DormerShape.Shed, Slope = 15, Width = 9000 }, wallType.Id, out _, out _);
        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        var gutter = new Gutter { RoofId = dormer.Id, LevelId = dormer.LevelId, TypeId = document.TypesOf<GutterType>().First().Id };
        gutter.EdgeIds.AddRange(RoofEdgeSweeps.GutterEdges(document, dormer));
        document.Add(gutter);
        var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = Downpipes.AtEnds(document, gutter)[0] };
        document.Add(pipe);
        Assert.True(Downpipes.Path(document, pipe)!.OntoRoof);

        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.Contains(RoofEdgeRules.Problems(document, gutter, standard), problem => problem.Rule.Contains("(1.9)"));
        pipe.Distributor = true;
        Assert.DoesNotContain(RoofEdgeRules.Problems(document, gutter, standard), problem => problem.Rule.Contains("(1.9)"));
    }

    [Fact]
    public void ASteepColdRoofIsAiredAtTheRidgeTooAndTheIrcAsksTwiceTheEavesVentsWithoutIt()
    {
        var (document, roof, gutter, _) = Guttered();
        roof.TypeId = document.TypesOf<RoofType>().Single(type => type.Name == "Roof - Slate on Timber Rafters 150mm").Id;
        var ceiling = new Ceiling
        {
            LevelId = roof.LevelId, HeightOffset = 3000,
            TypeId = document.TypesOf<CeilingType>().Single(type => type.Name == "Ceiling - Plasterboard under 270mm Mineral Wool").Id
        };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(ceiling);
        var soffit = new Soffit { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<SoffitType>().First().Id };
        soffit.EdgeIds.AddRange(gutter.EdgeIds);
        document.Add(soffit);

        // Pitched 40°: BS 5250 wants it aired at the ridge as well.
        Assert.Contains(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("5,000 mm²"));
        roof.HighLevelVents = true;
        Assert.DoesNotContain(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("5,000 mm²"));

        // The IRC: 1/300 with half of it high up, 1/150 at the eaves without.
        var withRidge = RoofEdgeRules.EavesAirAsked(document, roof, ClearanceStandard.Irc)!.Value.PerMetre;
        roof.HighLevelVents = false;
        Assert.Equal(4 * withRidge, RoofEdgeRules.EavesAirAsked(document, roof, ClearanceStandard.Irc)!.Value.PerMetre, 6);
    }

    [Fact]
    public void ASoffitUnderAColdRoofLetsAirIntoIt()
    {
        var (document, roof, gutter, _) = Guttered();
        roof.TypeId = document.TypesOf<RoofType>().Single(type => type.Name == "Roof - Slate on Timber Rafters 150mm").Id;
        var soffitType = document.TypesOf<SoffitType>().First(type => type.FreeAirArea == 0);
        var soffit = new Soffit { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = soffitType.Id };
        soffit.EdgeIds.AddRange(gutter.EdgeIds);
        document.Add(soffit);
        var standard = document.ProjectInformation.ClearanceStandard;

        // With nothing on the ceiling, the roof is no cold roof: no air asked.
        Assert.False(SlabRules.Cold(document, roof));
        Assert.Empty(RoofEdgeRules.Problems(document, soffit, standard));

        // A loft ceiling with its insulation on it under the roof: the eaves want 10,000 mm² a metre.
        var ceiling = new Ceiling
        {
            LevelId = roof.LevelId, HeightOffset = 3000,
            TypeId = document.TypesOf<CeilingType>().Single(type => type.Name == "Ceiling - Plasterboard under 270mm Mineral Wool").Id
        };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(ceiling);
        Assert.True(SlabRules.Cold(document, roof));
        var problems = RoofEdgeRules.Problems(document, soffit, standard);
        Assert.NotEmpty(problems);
        Assert.Contains("lets no air into the cold roof", problems[0].Said);
        Assert.Contains("10,000 mm²", problems[0].Rule);

        soffitType.FreeAirArea = 10000;
        Assert.Empty(RoofEdgeRules.Problems(document, soffit, standard));
        Assert.StartsWith("Airs the cold roof over it", RoofEdgeRules.Verdict(document, soffit));
    }
}
