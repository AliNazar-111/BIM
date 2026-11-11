using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
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

        // Regular in plan, its 10 m and 8 m walls on every side two bays of light frame each many times over: ρ 1.0 by 12.3.4.2(b), once the analysis has found no irregularity.
        Assert.Equal(1.0, quake.Redundancy);
        Assert.Contains("12.3.4.2(b)", quake.RedundancySaid);
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
        // In category D, Ev = 0.2 SDS D = 0.2 D on the dead load and ρ on QE, here 1.0 by 12.3.4.2(b): 1.4 D + 1.0 E + 0.5 L + 0.2 S, and 0.7 D + 1.0 E.
        var (_, loading, analysis) = Shaken();
        var e1 = loading.Cases.Single(each => each.Name == "E1");
        double Factor(LoadCombination combination, Func<LoadCase, bool> which) => combination.Terms.Where(term => which(term.Case)).Select(term => term.Factor).DefaultIfEmpty(0).Max();
        LoadCombination Of(string clause) => loading.Combinations.Single(each => each.Description == clause && each.Terms.Any(term => term.Case == e1));
        var six = Of("ASCE 7-16 2.3.6 (6)");
        Assert.Equal(1.4, Factor(six, each => each.Permanent), 6);
        Assert.Equal(1.0, Factor(six, each => each == e1), 6);
        Assert.Equal(0.5, Factor(six, each => each.Nature == LoadNature.Imposed), 6);
        Assert.Equal(0.2, Factor(six, each => each.Nature == LoadNature.Snow), 6);
        Assert.True(six.Factored);
        Assert.Equal(0.7, Factor(Of("ASCE 7-16 2.3.6 (7)"), each => each.Permanent), 6);

        // Allowable stress: 1.14 D + 0.7 E, 1.105 D + 0.525 E + 0.75 L + 0.75 S, 0.46 D + 0.7 E; not checked against as strength.
        var eight = Of("ASCE 7-16 2.4.5 (8)");
        Assert.Equal(1.14, Factor(eight, each => each.Permanent), 6);
        Assert.Equal(0.7, Factor(eight, each => each == e1), 6);
        Assert.False(eight.Factored);
        Assert.Equal(0.525, Factor(Of("ASCE 7-16 2.4.5 (9)"), each => each == e1), 6);
        Assert.Equal(0.46, Factor(Of("ASCE 7-16 2.4.5 (10)"), each => each.Permanent), 6);

        // An earthquake is not something a member deflects under for good.
        Assert.DoesNotContain(analysis.Total, term => term.Item1.Nature == LoadNature.Seismic);
    }

    [Fact]
    public void AWallRacksUnderTheEarthquakeOnTheWindsCapacityOver14()
    {
        // Each 10 m wall of the ground storey takes half of ρ V: ρ 1.0 by 12.3.4.2(b), 90.9 / 2 = 45 kN, a little going round into the walls it meets.
        var (document, _, analysis) = Shaken();
        var second = SecondOrderAnalysis.Run(document, analysis);
        var wall = MasonryDesign.Of(document, second).Single(check => check.Panel.Name == "W1");
        var front = wall.Checks.Single(each => each.Name == "Racking");
        Assert.Contains("seismic", front.Note, StringComparison.Ordinal);
        var hand = 1.0 * analysis.Seismic!.Shear / 2;
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
    public void DriftIsWorkedWithoutTheLeastCoefficient()
    {
        // Past TL at 10 s: 0.6 × 8 / (100 × 6.5) = 0.00738, all drift is worked from, where strength takes the least, 0.044 (12.8.6.1).
        var (drift, said) = Seismic.Coefficient(1.0, 0.6, 0.5, 6.5, 1, 10, 8, false, forDrift: true);
        Assert.Equal(0.6 * 8 / (100 * 6.5), drift, 6);
        Assert.Equal("(12.8-4)", said);

        // (12.8-6) still holds it where S1 is 0.6 or more: 0.5 × 0.6 / 6.5.
        Assert.Equal(0.3 / 6.5, Seismic.Coefficient(1.0, 0.6, 0.6, 6.5, 1, 10, 8, false, forDrift: true).Value, 6);
    }

    [Fact]
    public void TorsionDiaphragmsAndPDeltaByHand()
    {
        // Ax = (1.5 / (1.2 × 1.0))² = 1.5625, no less than 1 nor more than 3.
        Assert.Equal(1.5625, Seismic.TorsionAmplification(1.5, 1.0), 6);
        Assert.Equal(1, Seismic.TorsionAmplification(1.1, 1.0));
        Assert.Equal(3, Seismic.TorsionAmplification(3, 1.0));
        Assert.Equal("1a", Seismic.TorsionalIrregularity(1.3));
        Assert.Equal("1b", Seismic.TorsionalIrregularity(1.5));
        Assert.Equal(string.Empty, Seismic.TorsionalIrregularity(1.1));

        // θ of 0.15: the drift taken 1 / 0.85 = 1.176 times; under 0.10, as it is.
        Assert.Equal(1 / 0.85, Seismic.DriftIncrease(0.15), 6);
        Assert.Equal(1, Seismic.DriftIncrease(0.08));

        // Fpx: 300 kN over 1,000 kN from its level up, of its 400 kN, 120; at SDS 1.0 between 80 and 160. At 100 over 1,000 it is 40, so the least; at 900, 360, so the most.
        Assert.Equal(120, Seismic.DiaphragmForce(300, 1000, 400, 1.0, 1), 6);
        Assert.Equal(80, Seismic.DiaphragmForce(100, 1000, 400, 1.0, 1), 6);
        Assert.Equal(160, Seismic.DiaphragmForce(900, 1000, 400, 1.0, 1), 6);
    }

    /// <summary>A concrete office in the US: six 400 columns, 14 by 7, two 300 slabs, and a 200 concrete wall along its west end if asked.</summary>
    private static (StructuralLoading Loading, StructuralAnalysis Analysis) Frame(bool wall, double ss = 1.0, double s1 = 0.4, double first = 3000, int storeys = 2)
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        d.ProjectInformation.BuildingType = "Office";
        d.ProjectInformation.Ground = GroundType.StiffClay;
        d.ProjectInformation.SeismicSs = ss;
        d.ProjectInformation.SeismicS1 = s1;
        d.Levels[1].Elevation = first;
        var levels = new List<Guid> { d.Levels[0].Id, d.Levels[1].Id };
        for (var storey = 2; storey <= storeys; storey++)
        {
            var level = new BIMDesigner.Core.Datums.Level { Name = storey == storeys ? "Roof" : $"Level {storey}", Elevation = first + 3000 * (storey - 1) };
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
        if (wall)
        {
            var type = new WallType("Concrete 200mm", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, 200))) { Function = WallFunction.Exterior };
            d.AddType(type);
            for (var i = 0; i < 2; i++)
            {
                var placed = new Wall { Start = new Point2D(0, 700), End = new Point2D(0, 6300), TypeId = type.Id, LevelId = levels[i], TopLevelId = levels[i + 1], StructuralUsage = StructuralUsage.Bearing };
                d.Add(placed);
                if (i == 0) d.Add(Footings.Fit(d, placed));
            }
        }

        var model = AnalyticalModel.Of(d);
        var loading = StructuralLoading.Of(d, model);
        return (loading, StructuralAnalysis.Run(d, model, loading));
    }

    [Fact]
    public void ConcreteFloorsTakeAccidentalTorsionBothWays()
    {
        var (loading, analysis) = Frame(false);
        var quake = loading.Seismic!;
        Assert.False(quake.Flexible);
        var t1 = loading.Cases.Single(each => each.Name == "T1");
        Assert.True(t1.Torsion);

        // Each earthquake taken with its torsion one way and the other: 2.3.6 (6) for E1 twice, ρ 1.3 on both.
        var e1 = loading.Cases.Single(each => each.Name == "E1");
        var six = loading.Combinations.Where(each => each.Description == "ASCE 7-16 2.3.6 (6)" && each.Terms.Any(term => term.Case == e1)).ToList();
        Assert.Equal(2, six.Count);
        Assert.Equal(new[] { -1.3, 1.3 }, six.Select(each => each.Terms.Single(term => term.Case == t1).Factor).OrderBy(factor => factor).Select(factor => Math.Round(factor, 6)));

        // Its load turns and does not push: no force either way, and a torque of each level's force 5% of its 7.6 m across the forces.
        var load = analysis.LoadOf(t1);
        var nodes = Enumerable.Range(0, analysis.Solver.NodeCount).ToList();
        Assert.InRange(Math.Abs(nodes.Sum(n => load[n * 6])), 0, 1);
        Assert.InRange(Math.Abs(nodes.Sum(n => load[n * 6 + 1])), 0, 1);
        var torque = nodes.Sum(n => analysis.Solver.NodeAt(n).X * load[n * 6 + 1] - analysis.Solver.NodeAt(n).Y * load[n * 6]);
        var hand = analysis.Seismic!.Levels.Sum(level => level.Force * 1000 * 0.05 * 7600);
        Assert.InRange(torque, 0.97 * hand, 1.03 * hand);

        // Even all round, it barely turns: no irregularity, and Ax stays 1.
        Assert.All(analysis.Seismic.Drifts, drift => Assert.InRange(drift.Torsion, 1, 1.2));
        Assert.Equal(1, loading.AmplificationOf(t1));
        Assert.DoesNotContain(analysis.Seismic.Irregularities!, said => said.Contains("torsional", StringComparison.Ordinal));
    }

    [Fact]
    public void AWallAtOneEndMakesItTorsionallyIrregular()
    {
        // A concrete wall along its west end holds that end against the earthquake along Y; the columns at the east end swing round it.
        var (loading, analysis) = Frame(true);
        Assert.Equal("D", loading.Seismic!.Category);

        // Its wall stands only along Y, so along X its columns' frame is all there is: an ordinary concrete moment frame, R 3, the least, and not the wall's R 4.
        Assert.Same(Seismic.ConcreteFrame, loading.Seismic.System);
        var along = analysis.Seismic!.Drifts.Where(drift => drift.Case.StartsWith("E3", StringComparison.Ordinal)).ToList();
        Assert.All(along, drift => Assert.Equal("1b", drift.Irregular));

        // Category D amplifies its torsion, Ax up to 3, in its combinations: ρ 1.3 times Ax on T2.
        var t2 = loading.Cases.Single(each => each.Name == "T2");
        var ax = loading.AmplificationOf(t2);
        Assert.InRange(ax, 1.01, 3);
        Assert.All(along, drift => Assert.Equal(ax, drift.Amplification, 6));
        var six = loading.Combinations.First(each => each.Description == "ASCE 7-16 2.3.6 (6)" && each.Terms.Any(term => term.Case == t2));
        Assert.Equal(1.3 * ax, Math.Abs(six.Terms.Single(term => term.Case == t2).Factor), 6);
        Assert.Contains(analysis.Seismic.Irregularities!, said => said.Contains("extreme torsional", StringComparison.Ordinal));

        // Along X the wall stands across the forces and the frame does not turn.
        Assert.All(analysis.Seismic.Drifts.Where(drift => drift.Case.StartsWith("E1", StringComparison.Ordinal)), drift => Assert.Equal(string.Empty, drift.Irregular));

        // In category B it is as irregular, but its torsion is not amplified.
        var (low, lowAnalysis) = Frame(true, 0.3, 0.08);
        Assert.Equal("B", low.Seismic!.Category);
        Assert.Equal(1, low.AmplificationOf(low.Cases.Single(each => each.Name == "T2")));
        Assert.Contains(lowAnalysis.Seismic!.Drifts, drift => drift.Irregular.Length > 0);
    }

    [Fact]
    public void ATallGroundStoreyIsSoft()
    {
        // Three storeys, the first 6 m and the two over it 3 m: its columns an eighth as stiff, so an extreme soft storey, and it drifts far more than 1.3 times the storey over it, so 12.3.2.2 does not excuse it.
        var (_, analysis) = Frame(false, first: 6000, storeys: 3);
        Assert.Contains(analysis.Seismic!.Irregularities!, said => said.StartsWith("First Floor", StringComparison.Ordinal) && said.Contains("1b, extreme soft storey", StringComparison.Ordinal));

        // The storey over it, 23 kN/mm against 32 over that, is over 0.7 of it; with fewer than three storeys over it, no average of three is asked.
        Assert.DoesNotContain(analysis.Seismic.Irregularities!, said => said.StartsWith("Level 2", StringComparison.Ordinal));

        // All three 3 m, the first storey is still soft by the rule - its columns pinned on their pads, 19 kN/mm against 34 fixed by the slabs at both ends - and the second is not.
        var (_, even) = Frame(false, storeys: 3);
        Assert.Contains(even.Seismic!.Irregularities!, said => said.StartsWith("First Floor", StringComparison.Ordinal) && said.Contains("soft storey", StringComparison.Ordinal));
        Assert.DoesNotContain(even.Seismic.Irregularities!, said => said.StartsWith("Level 2", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcreteIsCrackedForDrift()
    {
        // At ACI's shares - columns 0.70, slabs 0.25 - the frame moves more than it did whole: between 1.4 and 4 times at the first floor.
        var (loading, analysis) = Frame(false);
        var result = analysis.Seismic!;
        Assert.True(result.Cracked);
        var e1 = loading.Cases.Single(each => each.Name == "E1");
        var first = result.Drifts.First(drift => drift.Case.StartsWith("E1", StringComparison.Ordinal) && drift.Level == "First Floor");
        var whole = Enumerable.Range(0, analysis.Solver.NodeCount).Where(n => Math.Abs(analysis.Solver.NodeAt(n).Z - 3000) <= 150).Max(n => analysis.Displacements[e1][n * 6]);
        Assert.InRange(first.Elastic / (result.DriftScale * whole), 1.4, 4);

        // Its diaphragms: the roof's force over its weight is its own Cvx, the first floor's at least 0.2 SDS of its weight.
        var quake = loading.Seismic!;
        var (floor, top) = (result.Levels[0], result.Levels[^1]);
        Assert.Equal(Math.Clamp(top.Force / top.Weight, 0.2 * quake.Sds, 0.4 * quake.Sds) * top.Weight, top.Diaphragm, 6);
        Assert.Equal(Seismic.DiaphragmForce(floor.Force + top.Force, floor.Weight + top.Weight, floor.Weight, quake.Sds, 1), floor.Diaphragm, 6);
        Assert.True(floor.Diaphragm >= 0.2 * quake.Sds * floor.Weight - 1e-6);
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
    internal static BimDocument House(ClearanceStandard? standard = null, string wall = "Timber Frame", int spacing = 2000)
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
