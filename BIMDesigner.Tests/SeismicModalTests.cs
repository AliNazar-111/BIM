using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>The earthquake's leftovers: the modal response spectrum analysis, Table 12.6-1, ρ by 12.3.4.2, the irregularities the drawing shows, walls out of their plane, and snow in W, against figures worked by hand.</summary>
public class SeismicModalTests
{
    [Fact]
    public void TheDesignSpectrumIsItsFourPieces()
    {
        // SDS 1.0, SD1 0.6: T0 0.12 s, TS 0.6 s. 0.4 at nought, 0.7 at 0.06, the plateau, SD1 / T, and past TL 8 s SD1 TL / T².
        Assert.Equal(0.4, ResponseSpectrum.Acceleration(1.0, 0.6, 0), 9);
        Assert.Equal(0.7, ResponseSpectrum.Acceleration(1.0, 0.6, 0.06), 9);
        Assert.Equal(1.0, ResponseSpectrum.Acceleration(1.0, 0.6, 0.3), 9);
        Assert.Equal(0.6, ResponseSpectrum.Acceleration(1.0, 0.6, 1.0), 9);
        Assert.Equal(0.6 * 8 / 100, ResponseSpectrum.Acceleration(1.0, 0.6, 10), 9);
    }

    [Fact]
    public void ModesAreCombinedByTheirCorrelation()
    {
        // A mode with itself, 1; at twice its frequency, 8 × 0.0025 × 3 × 2.828 / (9 + 4 × 0.0025 × 2 × 9) = 0.0185.
        Assert.Equal(1, ResponseSpectrum.Correlation(10, 10), 9);
        Assert.Equal(0.16971 / 9.18, ResponseSpectrum.Correlation(10, 20), 4);

        // Two of 1: far apart all but the square root of the sum of their squares, together their sum.
        Assert.Equal(Math.Sqrt(2 + 2 * 0.16971 / 9.18), ResponseSpectrum.Combine(new[] { 1.0, 1.0 }, new[] { 10.0, 20.0 }), 3);
        Assert.Equal(2, ResponseSpectrum.Combine(new[] { 1.0, 1.0 }, new[] { 10.0, 10.0 }), 9);
    }

    private static double[] Shape(params double[] alongX)
    {
        var shape = new double[alongX.Length * 6];
        for (var n = 0; n < alongX.Length; n++) shape[n * 6] = alongX[n];
        return shape;
    }

    [Fact]
    public void TwoMassesOnTwoLevelsByHand()
    {
        // A tonne at each of two levels, the first mode both moving 1 / √2 at 1 s, the second one against the other at 0.2 s. Γ1 = √2, Γ2 = 0: all the mass in the first.
        // Sa(1 s) = SD1 / T = 0.6 g; by Ie / R of 1, 5,886 mm/s²: 1 × (1 / √2) × √2 × 5,886 = 5.886 kN at each level, 11.772 at the base.
        var root = 1 / Math.Sqrt(2);
        var modes = new[] { new Mode(1, 1, Shape(root, root), 1, 0, 0), new Mode(2, 5, Shape(root, -root), 0, 0, 0) };
        var way = ResponseSpectrum.Of("x", modes, new[] { 1.0, 1.0 }, new[] { 0, 1 }, 2, 0, 1.0, 0.6, 1, 1, 15, false, 20);
        Assert.Equal(1, way.MassShare, 9);
        Assert.Equal(11.772, way.Combined, 6);

        // Under V of 15 it is scaled up by 15 / 11.772 = 1.274, each level 7.5; drift at Vt, 1 / 1.274 of those forces.
        Assert.Equal(15 / 11.772, way.Scale, 6);
        Assert.Equal(7.5, way.LevelForces[0], 6);
        Assert.Equal(7.5, way.LevelForces[1], 6);
        Assert.Equal(15, way.StoreyShears[0], 6);
        Assert.Equal(11.772 / 15, way.DriftScale, 6);

        // Where (12.8-6) gave Cs, drift at no less than 0.85 Cs W: 17 / 11.772 of the modal forces.
        var sixth = ResponseSpectrum.Of("x", modes, new[] { 1.0, 1.0 }, new[] { 0, 1 }, 2, 0, 1.0, 0.6, 1, 1, 15, true, 20);
        Assert.Equal(17 / 11.772 * 11.772 / 15, sixth.DriftScale, 6);

        // Over V, it stands as it is.
        Assert.Equal(1, ResponseSpectrum.Of("x", modes, new[] { 1.0, 1.0 }, new[] { 0, 1 }, 2, 0, 1.0, 0.6, 1, 1, 10, false, 20).Scale, 9);
    }

    [Fact]
    public void TheMassNoModeMovesIsARigidModeAtAFiftiethOfASecond()
    {
        // A third tonne at the upper level that the one mode does not move: Sa(0.05 s) = 0.4 + 0.6 × 0.05 / 0.12 = 0.65 g, 6.377 kN there. The upper storey √(5.886² + 6.377²) = 8.678,
        // the lower √(11.772² + 6.377²) = 13.388; the mode moves two thirds of the mass.
        var root = 1 / Math.Sqrt(2);
        var way = ResponseSpectrum.Of("x", new[] { new Mode(1, 1, Shape(root, root, 0), 1, 0, 0) }, new[] { 1.0, 1.0, 1.0 }, new[] { 0, 1, 1 }, 2, 0, 1.0, 0.6, 1, 1, 1, false, 1);
        Assert.Equal(2.0 / 3, way.MassShare, 9);
        Assert.Equal(Math.Sqrt(11.772 * 11.772 + 6.3765 * 6.3765), way.Combined, 3);
        Assert.Equal(Math.Sqrt(5.886 * 5.886 + 6.3765 * 6.3765), way.StoreyShears[1], 3);
    }

    private static SeismicDesign Design(string category = "D", int risk = 2, SeismicSystem? system = null, double height = 10000, double period = 0.4) =>
        new(1.5, 0.6, SiteClass.D, risk, 1, 1.7, 1, 0.68, category, "D2", system ?? Seismic.ConcreteWalls, 1, height, period, 1, 0.25, "(12.8-2)", 1.3, true, string.Empty);

    [Fact]
    public void Table126PermitsTheEquivalentLateralForceProcedure()
    {
        string[] none = Array.Empty<string>();
        Assert.True(SeismicRules.EquivalentPermitted(Design("C"), new[] { "H1b" }, 5).Permitted);
        Assert.True(SeismicRules.EquivalentPermitted(Design(), new[] { "H1b" }, 2).Permitted);
        Assert.False(SeismicRules.EquivalentPermitted(Design(risk: 3), new[] { "H1b" }, 2).Permitted);
        Assert.True(SeismicRules.EquivalentPermitted(Design(system: Seismic.LightFrame), new[] { "V1b" }, 3).Permitted);
        Assert.True(SeismicRules.EquivalentPermitted(Design(), none, 3).Permitted);
        Assert.False(SeismicRules.EquivalentPermitted(Design(), new[] { "H1b" }, 3).Permitted);
        Assert.False(SeismicRules.EquivalentPermitted(Design(), new[] { "V1a" }, 3).Permitted);
        Assert.True(SeismicRules.EquivalentPermitted(Design(), new[] { "H2", "V4" }, 3).Permitted);

        // Past 160 ft with no irregularity, T against 3.5 Ts = 3.5 × 0.68 = 2.38 s.
        Assert.False(SeismicRules.EquivalentPermitted(Design(height: 60000, period: 2.5), none, 15).Permitted);
        Assert.True(SeismicRules.EquivalentPermitted(Design(height: 60000, period: 2.0), none, 15).Permitted);
        Assert.False(SeismicRules.EquivalentPermitted(Design(height: 60000, period: 2.0), new[] { "H2" }, 15).Permitted);
    }

    [Fact]
    public void AReEntrantCornerIsBothItsProjections()
    {
        // An L 20 by 10 with 12 by 6 cut out: 60% each way, past 15%. A notch 2 by 1 is 10% one way, not.
        var l = new[] { new Point2D(0, 0), new Point2D(20000, 0), new Point2D(20000, 4000), new Point2D(8000, 4000), new Point2D(8000, 10000), new Point2D(0, 10000) };
        Assert.Contains("60%", SeismicRules.ReEntrant(l));
        Assert.Contains("60%", SeismicRules.ReEntrant(l.Reverse().ToList()));
        var notched = new[] { new Point2D(0, 0), new Point2D(20000, 0), new Point2D(20000, 9000), new Point2D(18000, 9000), new Point2D(18000, 10000), new Point2D(0, 10000) };
        Assert.Null(SeismicRules.ReEntrant(notched));
        Assert.Null(SeismicRules.ReEntrant(new[] { new Point2D(0, 0), new Point2D(20000, 0), new Point2D(20000, 10000), new Point2D(0, 10000) }));
    }

    /// <summary>
    /// A US concrete office 12 by 8 on Site Class D at Ss 1.5 and S1 0.6, category D: 200 concrete walls all round, three storeys of 3 m, 250 slabs - or timber floors. Its south wall over the
    /// first storey stood a metre in, or one wall turned, if asked.
    /// </summary>
    private static (BimDocument Document, StructuralAnalysis Analysis) Box(bool offset = false, bool turned = false, bool timber = false, bool modal = false, double snow = 0)
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        d.ProjectInformation.BuildingType = "Office";
        d.ProjectInformation.Ground = GroundType.StiffClay;
        d.ProjectInformation.SeismicSs = 1.5;
        d.ProjectInformation.SeismicS1 = 0.6;
        d.ProjectInformation.SeismicModal = modal;
        d.ProjectInformation.GroundSnowLoad = snow;
        d.Levels[1].Elevation = 3000;
        var levels = new List<Guid> { d.Levels[0].Id, d.Levels[1].Id };
        for (var storey = 2; storey <= 3; storey++)
        {
            var level = new Level { Name = storey == 3 ? "Roof" : $"Level {storey}", Elevation = 3000 * storey };
            d.AddLevel(level);
            levels.Add(level.Id);
        }

        var concrete = d.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal));
        var slab = new FloorType("Slab 250", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, 250)));
        d.AddType(slab);
        var boards = d.TypesOf<FloorType>().First(each => each.Name.StartsWith("Floor - Timber", StringComparison.Ordinal));
        var type = new WallType("Concrete 200mm", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, 200))) { Function = WallFunction.Exterior };
        d.AddType(type);
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
        for (var s = 0; s < 3; s++)
        {
            for (var i = 0; i < 4; i++)
            {
                var (start, end) = (corners[i], corners[(i + 1) % 4]);
                if (i == 0 && offset && s > 0) (start, end) = (new Point2D(0, 1000), new Point2D(12000, 1000));
                if (i == 1 && turned && s == 2) (start, end) = (new Point2D(12000, 0), new Point2D(10000, 8000));
                var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = levels[s], TopLevelId = levels[s + 1], StructuralUsage = StructuralUsage.Bearing };
                d.Add(wall);
                if (s == 0) d.Add(Footings.Fit(d, wall));
            }

            var floor = new Floor { TypeId = timber ? boards.Id : slab.Id, LevelId = levels[s + 1], SpanDirection = timber ? 90 : null };
            floor.SetBoundary(corners);
            d.Add(floor);
        }

        var model = AnalyticalModel.Of(d);
        return (d, StructuralAnalysis.Run(d, model, StructuralLoading.Of(d, model)));
    }

    [Fact]
    public void ABoxRegularWithWallsOnEverySideTakesRhoOfOne()
    {
        // Walls 12 m and 8 m long on 3 m storeys: 4 and 2.7 bays on each side, more than two; regular in plan.
        var (_, analysis) = Box();
        var quake = analysis.Loading.Seismic!;
        Assert.Equal("D", quake.Category);
        Assert.Equal(1.0, quake.Redundancy);
        Assert.DoesNotContain(analysis.Seismic!.Types!, type => type.StartsWith('H'));
        Assert.Contains("permits the equivalent lateral force procedure", analysis.Seismic.Procedure);
        Assert.Null(analysis.Seismic.Modal);
        var e1 = analysis.Loading.Cases.Single(each => each.Name == "E1");
        Assert.All(analysis.Loading.Combinations.Where(each => each.Description == "ASCE 7-16 2.3.6 (6)" && each.Terms.Any(term => term.Case == e1)),
            each => Assert.Equal(1.0, each.Terms.Single(term => term.Case == e1).Factor, 9));
        Assert.Contains(analysis.Loading.Notes, note => note.Contains("12.3.4.2(b)", StringComparison.Ordinal));

        // Out of its plane each wall takes 0.4 SDS Ie of its weight: 0.4 × 1.0 × 0.2 × 2,400 × 9.81 / 1,000 = 1.88 kPa. The floors are concrete: no anchorage to a flexible one.
        var anchorage = analysis.Seismic.Anchorage!;
        Assert.NotEmpty(anchorage);
        var weight = analysis.Model.Panels.First(panel => panel.Kind == AnalyticalPanelKind.Wall).SelfWeight;
        Assert.All(anchorage, row => Assert.Equal(0.4 * weight, row.Pressure, 6));
        Assert.All(anchorage, row => Assert.Equal(0, row.Anchor));
    }

    [Fact]
    public void AWallStoodOffTheOneUnderItIsAnOutOfPlaneOffset()
    {
        // Its south wall a metre in over the ground storey: horizontal irregularity 4, so not regular in plan and ρ 1.3; Table 12.6-1 still permits the equivalent lateral force procedure.
        var (_, analysis) = Box(offset: true);
        Assert.Contains("H4", analysis.Seismic!.Types!);
        Assert.Contains(analysis.Seismic.Irregularities!, said => said.Contains("out-of-plane offset", StringComparison.Ordinal));
        Assert.Equal(1.3, analysis.Loading.Seismic!.Redundancy);
        Assert.Contains("not regular in plan", analysis.Loading.Seismic.RedundancySaid);
        Assert.Null(analysis.Seismic.Modal);

        // A wall turned 14° off the axes at the top: horizontal irregularity 5.
        var (_, turned) = Box(turned: true);
        Assert.Contains("H5", turned.Seismic!.Types!);
    }

    [Fact]
    public void ASoftStoreyAsksTheModalAnalysis()
    {
        // The concrete frame with its first storey 6 m: an extreme soft storey in category D, three storeys of Risk Category II - Table 12.6-1 does not permit the equivalent lateral force procedure.
        var (document, analysis) = SoftFrame();
        var result = analysis.Seismic!;
        Assert.Contains("V1b", result.Types!);
        Assert.Contains("does not permit", result.Procedure);
        var modal = result.Modal!;
        Assert.Equal(2, modal.Ways.Count);
        Assert.All(modal.Ways, way => Assert.InRange(way.MassShare, 0.5, 1.0001));

        // Each way its forces come to V, or Vt where that is more; the earthquake's reactions are them.
        foreach (var (way, name) in new[] { (modal.Ways[0], "E1"), (modal.Ways[1], "E3") })
        {
            var expected = Math.Max(way.Combined, way.Static);
            Assert.Equal(expected, way.LevelForces.Sum(), 3);
            var loadCase = analysis.Loading.Cases.Single(each => each.Name == name);
            var reaction = analysis.TotalReaction(loadCase);
            Assert.InRange(Math.Abs(name == "E1" ? reaction.X : reaction.Y), 0.995 * expected, 1.005 * expected);
        }

        Assert.All(result.Levels, level => Assert.False(double.IsNaN(level.ForceY)));
        Assert.Equal(1.3, analysis.Loading.Seismic!.Redundancy);
        _ = document;
    }

    private static (BimDocument, StructuralAnalysis) SoftFrame()
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        d.ProjectInformation.BuildingType = "Office";
        d.ProjectInformation.Ground = GroundType.StiffClay;
        d.ProjectInformation.SeismicSs = 1.0;
        d.ProjectInformation.SeismicS1 = 0.4;
        d.Levels[1].Elevation = 6000;
        var levels = new List<Guid> { d.Levels[0].Id, d.Levels[1].Id };
        for (var storey = 2; storey <= 3; storey++)
        {
            var level = new Level { Name = storey == 3 ? "Roof" : $"Level {storey}", Elevation = 6000 + 3000 * (storey - 1) };
            d.AddLevel(level);
            levels.Add(level.Id);
        }

        var concrete = d.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal));
        var slab = new FloorType("Slab 300", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, 300)));
        d.AddType(slab);
        var post = new ColumnType("Concrete 400", ColumnShape.Rectangular, 400, 400) { Structural = true, StructuralMaterial = BeamMaterial.Concrete };
        d.AddType(post);
        foreach (var x in new[] { 0.0, 7000, 14000 })
            foreach (var y in new[] { 0.0, 7000 })
                d.Add(new Column { TypeId = post.Id, LevelId = levels[0], TopLevelId = levels[^1], Location = new Point2D(x, y), Structural = true });
        foreach (var level in levels.Skip(1))
        {
            var floor = new Floor { TypeId = slab.Id, LevelId = level };
            floor.SetBoundary(new[] { new Point2D(-300, -300), new Point2D(14300, -300), new Point2D(14300, 7300), new Point2D(-300, 7300) });
            d.Add(floor);
        }

        foreach (var column in d.All<Column>().ToList()) d.Add(Footings.Fit(d, column));
        var model = AnalyticalModel.Of(d);
        return (d, StructuralAnalysis.Run(d, model, StructuralLoading.Of(d, model)));
    }

    [Fact]
    public void TheModalAnalysisIsDoneWhereItIsAskedFor()
    {
        // The regular box, the plan's Properties asking for it: done, and said so; the base shear each way no less than V.
        var (_, analysis) = Box(modal: true);
        var result = analysis.Seismic!;
        Assert.NotNull(result.Modal);
        Assert.Contains("asked for in the plan's Properties", result.Procedure);
        Assert.All(result.Modal!.Ways, way => Assert.True(way.LevelForces.Sum() >= way.Static - 1e-6));
        Assert.All(result.Modal.Ways, way => Assert.InRange(way.Scale, 1, 10));
    }

    [Fact]
    public void ConcreteWallsOnTimberFloorsAreAnchoredByKa()
    {
        // Concrete walls held by timber floors, flexible: (12.11-1) 0.4 SDS ka Ie Wp. The south wall's floor spans 12 m along it, 39.4 ft: ka 1.394.
        // At the first floor Wp is the wall from halfway down the storey under to halfway up the one over, 3 m of it at 4.709 kPa, 14.13 kN/m: 0.4 × 1.0 × 1.394 × 14.13 = 7.88 kN/m.
        var (_, analysis) = Box(timber: true);
        var anchorage = analysis.Seismic!.Anchorage!;
        var weight = analysis.Model.Panels.First(panel => panel.Kind == AnalyticalPanelKind.Wall).SelfWeight;
        var first = anchorage.Where(row => row.Level == "First Floor" && row.Anchor > 0).MaxBy(row => row.Span)!;
        Assert.Equal(1 + first.Span / 304.8 / 100, first.Ka, 9);
        Assert.InRange(first.Span, 11000, 12500);
        Assert.Equal(0.4 * 1.0 * first.Ka * weight * 3.0, first.Anchor, 1);

        // A timber floor over concrete walls is not a concrete one: no stiffness change between them, both timber.
        Assert.DoesNotContain(analysis.Seismic.Irregularities!, said => said.Contains("timber over concrete", StringComparison.Ordinal));
    }

    [Fact]
    public void SnowOver30PsfIsInTheWeight()
    {
        // The test house's roof under 50 psf on the ground, 35 psf flat: 20% of the roof's snow in W, 12.7.2. At 40 psf, 28 psf flat, none.
        StructuralAnalysis House(double ground)
        {
            var document = SeismicTests.House(ClearanceStandard.Irc);
            document.ProjectInformation.SeismicSs = 1.5;
            document.ProjectInformation.SeismicS1 = 0.6;
            document.ProjectInformation.GroundSnowLoad = ground;
            var model = AnalyticalModel.Of(document);
            return StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        }

        var (snowed, light) = (House(50), House(40));
        Assert.True(snowed.Seismic!.Snow);
        Assert.False(light.Seismic!.Snow);
        var s1 = snowed.Loading.Cases.Single(each => each.Name == "S1");
        Assert.Equal(light.Seismic.Weight + 0.2 * snowed.Loading.Total(s1), snowed.Seismic.Weight, 0);
    }

    [Fact]
    public void TheModalSettingIsKeptWithTheProject()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.SeismicModal = true;
        Assert.True(ProjectFile.FromJson(ProjectFile.ToJson(document)).ProjectInformation.SeismicModal);
    }
}
