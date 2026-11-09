using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Seismic loads by ASCE 7-16's equivalent lateral force procedure, Supplement 1's site coefficients taken in.</summary>
public class SeismicTests
{
    [Fact]
    public void SiteCoefficientsAreSupplementOnesTablesInStraightLines()
    {
        // Site Class D at Ss 0.6: 1.4 at 0.5, 1.2 at 0.75, so 1.4 - 0.2 × 0.1 / 0.25 = 1.32. Fv at S1 0.25: halfway from 2.2 to 2.0.
        Assert.Equal(1.32, Seismic.Fa(SiteClass.D, 0.6)!.Value, 6);
        Assert.Equal(2.1, Seismic.Fv(SiteClass.D, 0.25)!.Value, 6);
        Assert.Equal(1.6, Seismic.Fa(SiteClass.D, 0.1)!.Value, 6);
        Assert.Equal(1.0, Seismic.Fa(SiteClass.D, 2.0)!.Value, 6);
        Assert.Equal(1.7, Seismic.Fv(SiteClass.D, 0.6)!.Value, 6);
        Assert.Equal(2.1, Seismic.Fv(SiteClass.E, 0.55)!.Value, 6);
        Assert.Equal(0.8, Seismic.Fa(SiteClass.A, 1.0)!.Value, 6);

        // Site Class E: C's Fa where Ss is 1.0 or more, by 11.4.8; between 0.75 and 1.0 none; F wants its own study.
        Assert.Equal(1.2, Seismic.Fa(SiteClass.E, 1.2)!.Value, 6);
        Assert.Null(Seismic.Fa(SiteClass.E, 0.9));
        Assert.Null(Seismic.Fa(SiteClass.F, 0.5));
        Assert.Null(Seismic.Fv(SiteClass.F, 0.5));
    }

    [Theory]
    [InlineData(0.10, 0.05, 0.05, 2, "A")]
    [InlineData(0.30, 0.10, 0.10, 2, "B")]
    [InlineData(0.30, 0.10, 0.10, 4, "C")]
    [InlineData(0.40, 0.05, 0.05, 2, "C")]
    [InlineData(0.10, 0.15, 0.15, 2, "C")]
    [InlineData(0.10, 0.15, 0.15, 4, "D")]
    [InlineData(0.50, 0.20, 0.30, 2, "D")]
    [InlineData(1.00, 0.70, 0.80, 2, "E")]
    [InlineData(1.00, 0.70, 0.80, 4, "F")]
    public void TheCategoryIsTheWorseOfItsTwoTables(double sds, double sd1, double s1, int risk, string category) =>
        Assert.Equal(category, Seismic.Category(sds, sd1, s1, risk));

    [Fact]
    public void TheIrcSplitsDByItsShortPeriodAcceleration()
    {
        Assert.Equal("C", Seismic.IrcCategory(0.45));
        Assert.Equal("D0", Seismic.IrcCategory(0.6));
        Assert.Equal("D1", Seismic.IrcCategory(0.75));
        Assert.Equal("D2", Seismic.IrcCategory(1.0));
        Assert.Equal("E", Seismic.IrcCategory(1.3));
        Assert.Equal(1.25, Seismic.Importance(3));
        Assert.Equal(1.5, Seismic.Importance(4));
    }

    [Fact]
    public void TheResponseCoefficientIsTheSpectrumOverRByHand()
    {
        // SDS 1.0, SD1 0.6, R 6.5: on the plateau 1 / 6.5 = 0.1538, for T of 0.25 under SD1 / (T R) = 0.369.
        var (plateau, said) = Seismic.Coefficient(1.0, 0.6, 0.5, 6.5, 1, 0.25, 8, false);
        Assert.Equal(1 / 6.5, plateau, 6);
        Assert.Equal("(12.8-2)", said);

        // At T 2.0 it falls: 0.6 / (2 × 6.5) = 0.0462, over the least 0.044 SDS.
        var (falling, fell) = Seismic.Coefficient(1.0, 0.6, 0.5, 6.5, 1, 2.0, 8, false);
        Assert.Equal(0.6 / 13, falling, 6);
        Assert.Equal("(12.8-3)", fell);

        // Past TL at 10 s: 0.6 × 8 / (100 × 6.5) = 0.0074, so the least, 0.044; and with S1 of 0.6, 0.5 × 0.6 / 6.5 = 0.0462.
        var (floor, floored) = Seismic.Coefficient(1.0, 0.6, 0.5, 6.5, 1, 10, 8, false);
        Assert.Equal(0.044, floor, 6);
        Assert.Equal("(12.8-5)", floored);
        var (least, by) = Seismic.Coefficient(1.0, 0.6, 0.6, 6.5, 1, 10, 8, false);
        Assert.Equal(0.3 / 6.5, least, 6);
        Assert.Equal("(12.8-6)", by);

        // On Site Class D with S1 0.2 or more, 11.4.8: Ts 0.6, so at T 1.2, past 1.5 Ts, 1.5 × 0.6 / (1.2 × 6.5) = 0.1154; within it, the plateau.
        Assert.Equal(1.5 * 0.6 / (1.2 * 6.5), Seismic.Coefficient(1.0, 0.6, 0.5, 6.5, 1, 1.2, 8, true).Value, 6);
        Assert.Equal(1 / 6.5, Seismic.Coefficient(1.0, 0.6, 0.5, 6.5, 1, 0.8, 8, true).Value, 6);

        // A risk category IV building, Ie 1.5: SDS / (R / Ie) = 1.5 / 6.5.
        Assert.Equal(1.5 / 6.5, Seismic.Coefficient(1.0, 0.6, 0.5, 6.5, 1.5, 0.25, 8, false).Value, 6);
    }

    [Fact]
    public void ThePeriodIsCtTimesTheHeightInFeet()
    {
        // 6 m is 19.685 ft: 0.02 × 19.685^0.75 = 0.02 × 9.346 = 0.1869 s. 100 ft of steel frame: 0.028 × 100^0.8 = 1.115 s.
        Assert.InRange(Seismic.Period(6000, 0.02, 0.75), 0.1868, 0.1870);
        Assert.InRange(Seismic.Period(30480, 0.028, 0.8), 1.114, 1.116);
        Assert.Equal(1, Seismic.Exponent(0.3));
        Assert.Equal(1.5, Seismic.Exponent(1.5), 6);
        Assert.Equal(2, Seismic.Exponent(3));
    }

    [Fact]
    public void DriftIsAllowedByTable1212()
    {
        Assert.Equal(0.025, Seismic.DriftLimit(Seismic.LightFrame, 2, 2));
        Assert.Equal(0.020, Seismic.DriftLimit(Seismic.LightFrame, 2, 3));
        Assert.Equal(0.015, Seismic.DriftLimit(Seismic.LightFrame, 2, 4));
        Assert.Equal(0.020, Seismic.DriftLimit(Seismic.SteelFrame, 5, 2));
        Assert.Equal(0.010, Seismic.DriftLimit(Seismic.SteelFrame, 5, 4));
        Assert.Equal(0.007, Seismic.DriftLimit(Seismic.PlainMasonry, 2, 2));
    }

    /// <summary>The test house on Site Class D at Ss 1.5 and S1 0.6: Fa 1.0 and Fv 1.7, so SDS 1.0 and SD1 0.68, category D.</summary>
    private static (BimDocument Document, StructuralLoading Loading, StructuralAnalysis Analysis) Shaken(string use = "", double ss = 1.5, double s1 = 0.6)
    {
        var document = House(ClearanceStandard.Irc);
        document.ProjectInformation.SeismicSs = ss;
        document.ProjectInformation.SeismicS1 = s1;
        document.ProjectInformation.BuildingType = use;
        var model = AnalyticalModel.Of(document);
        var loading = StructuralLoading.Of(document, model);
        return (document, loading, StructuralAnalysis.Run(document, model, loading));
    }

    [Fact]
    public void AHouseShakenTakesCsTimesItsWeight()
    {
        var (_, loading, analysis) = Shaken();
        var quake = loading.Seismic!;
        Assert.Equal("D", quake.Category);
        Assert.Equal("D2", quake.IrcCategory);
        Assert.True(quake.Applies);
        Assert.Equal(1.0, quake.Sds, 6);
        Assert.Equal(2.0 / 3 * 1.7 * 0.6, quake.Sd1, 6);
        Assert.Same(Seismic.LightFrame, quake.System);
        Assert.Equal(1 / 6.5, quake.Coefficient, 6);
        Assert.Equal(1.3, quake.Redundancy);
        Assert.Equal(4, loading.Cases.Count(each => each.Nature == LoadNature.Seismic));

        // V = Cs W; the levels' forces come to it, and so do the reactions, against it.
        var result = analysis.Seismic!;
        Assert.Equal(quake.Coefficient * result.Weight, result.Shear, 6);
        Assert.Equal(result.Shear, result.Levels.Sum(level => level.Force), 6);
        var first = loading.Cases.First(each => each.Nature == LoadNature.Seismic);
        Assert.InRange(-analysis.TotalReaction(first).X, 0.999 * result.Shear, 1.001 * result.Shear);
        Assert.InRange(Math.Abs(analysis.TotalReaction(first).Y), 0, 0.001 * result.Shear);

        // W is the dead load over the base: all of it but what sits at the foot of the ground floor walls.
        var dead = loading.Cases.Where(each => each.Permanent).Sum(each => analysis.TotalReaction(each).Z);
        Assert.InRange(result.Weight, 0.9 * dead, dead);

        // Higher up is pushed harder than its weight alone would have it.
        var top = result.Levels.Single(level => level.Name == "Eaves");
        Assert.True(top.Force / result.Shear > top.Weight / result.Weight + 0.15);
    }

    [Fact]
    public void EachStoreyDriftsAsItsWallsRack()
    {
        // Each storey's shear on its two walls in a direction, in shear H h / (G t L): 10 m walls one way, 8 m the other.
        var (_, _, analysis) = Shaken();
        var result = analysis.Seismic!;
        var stiffness = TimberWallDesign.RackingStiffness(true);
        var upper = result.Levels.Single(level => level.Name == "Eaves").Force;
        foreach (var (name, length) in new[] { ("E1", 10000.0), ("E3", 8000.0) })
        {
            var ground = result.Drifts.Single(drift => drift.Case == name && drift.Level == "First Floor");
            var byHand = result.Shear * 1000 / 2 * 3000 / (stiffness * length);
            Assert.InRange(ground.Elastic, 0.92 * byHand, 1.05 * byHand);
            var eaves = result.Drifts.Single(drift => drift.Case == name && drift.Level == "Eaves");
            var aboveByHand = upper * 1000 / 2 * 3000 / (stiffness * length);
            Assert.InRange(eaves.Elastic - ground.Elastic, 0.92 * aboveByHand, 1.08 * aboveByHand);

            // Cd δ / Ie, against 0.025 of the storey's 3 m.
            Assert.Equal(4 * ground.Elastic, ground.Drift, 6);
            Assert.Equal(75, ground.Allowed, 6);
            Assert.True(ground.Ratio < 1);

            // θ = Px Δ Ie / (Vx h Cd), which comes to Px δ / (Vx h): some 700 kN over 8 to 10 mm on 91 kN over 3 m, about 0.02.
            Assert.InRange(ground.Theta, 0.01, 0.04);
            Assert.Equal(0.125, ground.ThetaMax, 6);
        }
    }

    [Fact]
    public void TheEarthquakeIsCombinedByAsce7()
    {
        // In category D, Ev = 0.2 SDS D = 0.2 D on the dead load and ρ 1.3 on QE: 1.4 D + 1.3 E + 0.5 L + 0.2 S, and 0.7 D + 1.3 E.
        var (_, loading, analysis) = Shaken();
        var e1 = loading.Cases.Single(each => each.Name == "E1");
        double Factor(LoadCombination combination, Func<LoadCase, bool> which) => combination.Terms.Where(term => which(term.Case)).Select(term => term.Factor).DefaultIfEmpty(0).Max();
        LoadCombination Of(string clause) => loading.Combinations.Single(each => each.Description == clause && each.Terms.Any(term => term.Case == e1));
        var six = Of("ASCE 7-16 2.3.6 (6)");
        Assert.Equal(1.4, Factor(six, each => each.Permanent), 6);
        Assert.Equal(1.3, Factor(six, each => each == e1), 6);
        Assert.Equal(0.5, Factor(six, each => each.Nature == LoadNature.Imposed), 6);
        Assert.Equal(0.2, Factor(six, each => each.Nature == LoadNature.Snow), 6);
        Assert.True(six.Factored);
        Assert.Equal(0.7, Factor(Of("ASCE 7-16 2.3.6 (7)"), each => each.Permanent), 6);

        // Allowable stress: 1.14 D + 0.91 E, 1.105 D + 0.6825 E + 0.75 L + 0.75 S, 0.46 D + 0.91 E; not checked against as strength.
        var eight = Of("ASCE 7-16 2.4.5 (8)");
        Assert.Equal(1.14, Factor(eight, each => each.Permanent), 6);
        Assert.Equal(0.91, Factor(eight, each => each == e1), 6);
        Assert.False(eight.Factored);
        Assert.Equal(0.6825, Factor(Of("ASCE 7-16 2.4.5 (9)"), each => each == e1), 6);
        Assert.Equal(0.46, Factor(Of("ASCE 7-16 2.4.5 (10)"), each => each.Permanent), 6);

        // An earthquake is not something a member deflects under for good.
        Assert.DoesNotContain(analysis.Total, term => term.Item1.Nature == LoadNature.Seismic);
    }

    [Fact]
    public void AWallRacksUnderTheEarthquakeOnTheWindsCapacityOver14()
    {
        // Each 10 m wall of the ground storey takes half of ρ V: 1.3 × 90.9 / 2 = 59 kN, a little going round into the walls it meets.
        var (document, _, analysis) = Shaken();
        var second = SecondOrderAnalysis.Run(document, analysis);
        var wall = MasonryDesign.Of(document, second).Single(check => check.Panel.Name == "W1");
        var front = wall.Checks.Single(each => each.Name == "Racking");
        Assert.Contains("seismic", front.Note, StringComparison.Ordinal);
        var hand = 1.3 * analysis.Seismic!.Shear / 2;
        Assert.InRange(front.Demand, 0.95 * hand, 1.01 * hand);

        // What it carries is the SDPWS's for the wind over 1.4.
        var height = MasonryDesign.Holding(analysis.Model, wall.Panel).Item1;
        Assert.Equal(TimberWallDesign.RackingSdpws(TimberWallDesign.SolidLengths(wall.Panel), height) / 1.4, front.Capacity, 3);
    }

    [Fact]
    public void ADetachedHouseInCategoryCIsExempt()
    {
        // Ss 0.5 and S1 0.1 on Site Class D: Fa 1.4, Fv 2.4, so SDS 0.467 and SD1 0.16, category C. A house there needs no seismic design; an office does.
        var (_, house, analysis) = Shaken(ss: 0.5, s1: 0.1);
        Assert.Equal("C", house.Seismic!.Category);
        Assert.False(house.Seismic.Applies);
        Assert.Contains("exempt", house.Seismic.Said, StringComparison.Ordinal);
        Assert.DoesNotContain(house.Cases, each => each.Nature == LoadNature.Seismic);
        Assert.Null(analysis.Seismic);

        var (_, office, worked) = Shaken("Office", 0.5, 0.1);
        Assert.True(office.Seismic!.Applies);
        Assert.Equal(1.0, office.Seismic.Redundancy);
        Assert.Equal(4, office.Cases.Count(each => each.Nature == LoadNature.Seismic));
        Assert.NotNull(worked.Seismic);

        // Under 0.4 g of Ss a house is exempt whatever its category: Ss 0.35 and S1 0.3 on D is category D by SD1 = 0.4.
        var (_, low, _) = Shaken(ss: 0.35, s1: 0.3);
        Assert.Equal("D", low.Seismic!.Category);
        Assert.False(low.Seismic.Applies);
    }

    [Fact]
    public void NoneIsWorkedOutWithoutASiteOrInEngland()
    {
        var (_, unset, _) = Shaken(ss: 0, s1: 0);
        Assert.Null(unset.Seismic);
        Assert.Contains(unset.Notes, note => note.StartsWith("Seismic: no Ss and S1", StringComparison.Ordinal));

        var england = House();
        england.ProjectInformation.SeismicSs = 1.5;
        england.ProjectInformation.SeismicS1 = 0.6;
        Assert.Null(StructuralLoading.Of(england, AnalyticalModel.Of(england)).Seismic);

        // Category A asks only for 1.4's integrity; Site Class F for a study of its own.
        var (_, quiet, _) = Shaken("Office", 0.1, 0.04);
        Assert.Equal("A", quiet.Seismic!.Category);
        Assert.False(quiet.Seismic.Applies);
        var soft = House(ClearanceStandard.Irc);
        soft.ProjectInformation.SeismicSs = 1.0;
        soft.ProjectInformation.SeismicS1 = 0.4;
        soft.ProjectInformation.SiteClass = SiteClass.F;
        var design = Seismic.Of(soft, AnalyticalModel.Of(soft))!;
        Assert.False(design.Applies);
        Assert.Contains("Site Class F", design.Said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSiteIsKeptWithTheProject()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.SeismicSs = 1.25;
        document.ProjectInformation.SeismicS1 = 0.45;
        document.ProjectInformation.SiteClass = SiteClass.C;
        document.ProjectInformation.RiskCategory = 3;
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document)).ProjectInformation;
        Assert.Equal(1.25, reopened.SeismicSs);
        Assert.Equal(0.45, reopened.SeismicS1);
        Assert.Equal(SiteClass.C, reopened.SiteClass);
        Assert.Equal(3, reopened.RiskCategory);

        // A new project has none, on Site Class D and in Risk Category II.
        var fresh = BimDocument.CreateDefault().ProjectInformation;
        Assert.Equal(0, fresh.SeismicSs);
        Assert.Equal(SiteClass.D, fresh.SiteClass);
        Assert.Equal(2, fresh.RiskCategory);
    }

    [Fact]
    public void ADiaphragmOverCloseJoistsDoesNotSetSecondOrderRunningAway()
    {
        // Joists at 400 under a diaphragm with a thousandth of its bending: what the diaphragm carries in its plane had neighbouring joists going up and down, pass after pass, and every
        // combination with wind or an earthquake in it read as buckling. With walls racking on their nails αcr is about 12, under the 20 at which the passes are run, so it showed.
        var document = House(ClearanceStandard.Irc, spacing: 400);
        document.ProjectInformation.SeismicSs = 1.5;
        document.ProjectInformation.SeismicS1 = 0.6;
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var second = SecondOrderAnalysis.Run(document, analysis);
        Assert.DoesNotContain(second.Results, result => result.Buckles);
        Assert.InRange(second.Worst!.Alpha, 8, 20);
        Assert.All(second.Results.Where(result => result.Combination.Terms.Any(term => term.Case.Lateral)), result => Assert.InRange(result.Passes, 0, 6));
    }

    /// <summary>A two storey timber frame house, 10 by 8, with a floor on joists and a roof.</summary>
    private static BimDocument House(ClearanceStandard? standard = null, string wall = "Timber Frame", int spacing = 2000)
    {
        var document = BimDocument.CreateDefault();
        if (standard is { } each) document.ProjectInformation.ClearanceStandard = each;
        document.ProjectInformation.Ground = GroundType.StiffClay;
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var level = new BIMDesigner.Core.Datums.Level { Name = "Eaves", Elevation = 6000 };
        document.AddLevel(level);
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith(wall, StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        var walls = new List<Wall>();
        foreach (var (from, to) in new[] { (ground, first), (first, level.Id) })
            for (var i = 0; i < 4; i++)
            {
                var placed = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = from, TopLevelId = to, StructuralUsage = StructuralUsage.Bearing };
                document.Add(placed);
                walls.Add(placed);
            }

        foreach (var placed in walls.Where(placed => placed.LevelId == ground)) document.Add(Footings.Fit(document, placed));
        var joist = new BeamType("Timber 75 x 225") { Material = BeamMaterial.Timber, Width = 75, Depth = 225 };
        document.AddType(joist);
        for (var x = spacing / 2; x < 10000; x += spacing)
            document.Add(new Beam { TypeId = joist.Id, LevelId = first, Start = new Point2D(x, 0), End = new Point2D(x, 8000), Usage = BeamUsage.Joist });
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)).Id, LevelId = first, SpanDirection = 0 };
        floor.SetBoundary(corners);
        document.Add(floor);

        const double over = 465;
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First(t => t.Name.StartsWith("Roof - Slate on Timber", StringComparison.Ordinal)).Id, LevelId = level.Id };
        roof.SetBoundary(new[] { new Point2D(-over, -over), new Point2D(10000 + over, -over), new Point2D(10000 + over, 8000 + over), new Point2D(-over, 8000 + over) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i % 2 == 0, SlopeDegrees = 35, WallId = walls[4 + i].Id, OnLeftOfWall = false, Overhang = 300 }));
        document.Add(roof);
        return document;
    }
}
