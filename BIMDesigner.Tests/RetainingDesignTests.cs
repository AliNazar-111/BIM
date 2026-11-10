using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Foundations finished: a strip by its heaviest metre, how far footings go down, and retaining walls, each against figures worked by hand.</summary>
public class RetainingDesignTests
{
    // 2 m of stem 200 thick at 5 kN/m² of face, a base 300 thick standing 1,000 behind it and 500 before: 1.7 wide. φ' 30°, 18 kN/m³, 10 kN/m² behind it, δ 30°.
    private static readonly RetainedSection Wall = new(2000, 200, 1000, 500, 300, 5, 10, 30, 18, 30);

    [Fact]
    public void RankinesCoefficientIsItsFormula()
    {
        Assert.Equal(1.0 / 3, RetainingDesign.Active(30), 6);
        Assert.Equal(0.2174, RetainingDesign.Active(40), 3);
    }

    [Fact]
    public void WhatStandsOnTheBaseIsItsParts()
    {
        // Stem 5 × 2 = 10 at 0.6; base 24 × 1.7 × 0.3 = 12.24 at 0.85; soil 18 × 1 × 2 = 36 at 1.2. 58.24 kN/m and 6 + 10.4 + 43.2 = 59.6 kNm/m.
        var (weight, moment) = RetainingDesign.Stands(Wall);
        Assert.Equal(58.24, weight, 6);
        Assert.Equal(59.604, moment, 3);
        Assert.Equal(1700, Wall.Base);
        Assert.Equal(2.3, Wall.Pushed, 6);
    }

    [Fact]
    public void ItSlidesByDesignApproachOne()
    {
        // DA1-1: Ka 1/3; earth ½ × ⅓ × 18 × 2.3² = 15.87, 1.35 of it 21.42; surcharge ⅓ × 10 × 2.3 = 7.67, 1.5 of it 11.5: 32.9 kN/m against 58.24 tan 30° = 33.6.
        // DA1-2: tan φ'd = 0.5774 / 1.25 = 0.4619, 24.8°, Ka 0.409; earth 19.5, surcharge 9.4 × 1.3 = 12.2: 31.7 against 58.24 × 0.4619 = 26.9. It is the second that governs.
        var sliding = RetainingDesign.StabilityEurocode(Wall, 150).Single(check => check.Name == "Sliding");
        Assert.Equal("DA1-2", sliding.Under);
        Assert.InRange(sliding.Demand, 31.4, 32.0);
        Assert.InRange(sliding.Capacity, 26.7, 27.1);
    }

    [Fact]
    public void ItOverturnsByStaticEquilibrium()
    {
        // At Ka 0.409: earth 19.47 at 0.767 and surcharge 9.41 at 1.15; 1.1 × 14.9 + 1.5 × 10.8 = 32.6 kNm/m against 0.9 × 59.6 = 53.6.
        var over = RetainingDesign.StabilityEurocode(Wall, 150).Single(check => check.Name == "Overturning");
        Assert.InRange(over.Demand, 32.2, 33.0);
        Assert.Equal(0.9 * 59.604, over.Capacity, 2);
    }

    [Fact]
    public void ItsLoadStaysInItsMiddleThird()
    {
        // Characteristic, Ka ⅓: 15.87 × 0.767 + 7.67 × 1.15 = 21.0 kNm/m. x = (59.6 - 21.0) / 58.24 = 0.663; e = 0.85 - 0.663 = 0.187, within 1.7 / 6 = 0.283.
        // q = 58.24 / 1.7 × (1 + 6 × 0.187 / 1.7) = 56.9 kPa.
        var checks = RetainingDesign.StabilityEurocode(Wall, 150);
        var off = checks.Single(check => check.Name == "Load off its middle");
        Assert.InRange(off.Demand, 184, 191);
        Assert.Equal(283.3, off.Capacity, 1);
        Assert.InRange(checks.Single(check => check.Name == "Ground under its toe").Demand, 56, 58);
    }

    [Fact]
    public void PastTheMiddleThirdTheGroundTakesATriangle()
    {
        // V 100 kN/m at 0.3 from the toe of a 1.7 m base has lifted its heel: 2V / 3x.
        var section = Wall with { Heel = 300, Toe = 300 };
        var (weight, moment) = RetainingDesign.Stands(section);
        var overturning = moment - 0.2 * weight;
        var (_, pressure) = RetainingDesign.OnTheGround(section, overturning);
        Assert.Equal(2 * weight / (3 * 0.2), pressure, 6);
    }

    [Fact]
    public void ThereIsNoBearingCheckWithoutABearingValue() =>
        Assert.DoesNotContain(RetainingDesign.StabilityEurocode(Wall, null), check => check.Name == "Ground under its toe");

    [Fact]
    public void ItStandsByTheIrcAtOneAndAHalf()
    {
        // 45 psf/ft is 7.07 kN/m³: earth ½ × 7.07 × 2.3² = 18.7; surcharge 7.07 × 10 / 18 × 2.3 = 9.03. 27.7 kN/m, and 1.5 of it 41.6, against 0.25 × 58.24 = 14.6: it slides.
        var fluid = RetainingDesign.FluidPressure(GroundType.Sand)!.Value;
        Assert.Equal(7.07, fluid, 2);
        var (friction, cohesion) = RetainingDesign.SlidingOf(GroundType.Sand);
        var checks = RetainingDesign.StabilityUs(Wall, fluid, friction, cohesion, 95);
        var sliding = checks.Single(check => check.Name == "Sliding, at 1.5");
        Assert.InRange(sliding.Demand, 41, 42);
        Assert.Equal(14.56, sliding.Capacity, 2);
        Assert.False(sliding.Ratio <= 1);

        // 1.5 × (18.7 × 0.767 + 9.03 × 1.15) = 1.5 × 24.7 = 37.1 kNm/m against 59.6.
        var over = checks.Single(check => check.Name == "Overturning, at 1.5");
        Assert.InRange(over.Demand, 36.8, 37.4);
        Assert.Equal(59.604, over.Capacity, 3);
    }

    [Fact]
    public void LateralBearingBelowGradeHelpsItHold()
    {
        // 100 psf/ft is 15.7 kN/m³; 600 below grade, ½ × 15.7 × 0.36 = 2.83 kN/m on top of the 10.58.
        Assert.Equal(15.71, RetainingDesign.LateralBearing(GroundType.StiffClay), 2);
        var (friction, cohesion) = RetainingDesign.SlidingOf(GroundType.StiffClay);
        var fluid = RetainingDesign.FluidPressure(GroundType.StiffClay)!.Value;
        var held = RetainingDesign.StabilityUs(Wall with { Embedded = 600 }, fluid, friction, cohesion, 70, RetainingDesign.LateralBearing(GroundType.StiffClay)).Single(check => check.Name == "Sliding, at 1.5");
        Assert.Equal(10.58 + 2.83, held.Capacity, 1);
    }

    [Fact]
    public void AWallThatTipsOverSaysSoAndNotInfinity()
    {
        var narrow = Wall with { Heel = 0, Toe = 0 };
        var checks = RetainingDesign.StabilityEurocode(narrow, 150);
        Assert.Contains(checks, check => check.Name == "Overturning" && check.Ratio > 1);
        Assert.DoesNotContain(checks, check => check.Name == "Ground under its toe");
        Assert.All(checks, check => Assert.False(double.IsInfinity(check.Demand)));
    }

    [Fact]
    public void OnClayItSlidesOnCohesionAtMostHalfItsWeight()
    {
        // 130 psf is 6.22 kPa, × 1.7 m = 10.6 kN/m, under half of 58.24.
        var (friction, cohesion) = RetainingDesign.SlidingOf(GroundType.StiffClay);
        Assert.Equal(0, friction);
        var sliding = RetainingDesign.StabilityUs(Wall, RetainingDesign.FluidPressure(GroundType.StiffClay)!.Value, friction, cohesion, 70).Single(check => check.Name == "Sliding, at 1.5");
        Assert.Equal(10.58, sliding.Capacity, 1);

        // On a base wide and light it stops at half the weight.
        var wide = new RetainedSection(500, 200, 0, 7800, 100, 5, 0, 30, 18, 30);
        var (weight, _) = RetainingDesign.Stands(wide);
        var limited = RetainingDesign.StabilityUs(wide, 9.4, friction, cohesion, null).Single(check => check.Name == "Sliding, at 1.5");
        Assert.Equal(weight / 2, limited.Capacity, 6);
    }

    [Fact]
    public void TheStemCarriesTheEarthAboveTheBase()
    {
        // Over 2 m at Ka ⅓: 1.35 × ½ × 6 × 4 = 16.2 at 0.667, 1.5 × 3.33 × 2 = 10 at 1: 20.8 kNm/m, 26.2 kN/m.
        // Under DA1-2, Ka 0.409: ½ × 7.36 × 4 = 14.7 at 0.667, 1.3 × 4.09 × 2 = 10.6 at 1: 20.4 kNm/m, 25.4 kN/m. The first governs.
        var (moment, shear) = RetainingDesign.StemForcesEurocode(Wall);
        Assert.InRange(moment, 20.6, 21.0);
        Assert.InRange(shear, 26, 26.4);
    }

    [Fact]
    public void AConcreteStemSaysItsBars()
    {
        var (moment, shear) = RetainingDesign.StemForcesEurocode(Wall);
        var parts = RetainingDesign.ConcreteParts(Wall, moment, shear, 57, 30, false).ToList();
        var stem = parts.Single(check => check.Name == "Stem in bending");

        // d 150: 0.167 × 1000 × 150² × 30 = 113 kNm/m.
        Assert.Equal(112.7, stem.Capacity, 0);
        Assert.Contains("mm²/m", stem.Note);
        Assert.Contains(parts, check => check.Name == "Stem in shear");

        // The heel: (1.35 × (36 + 7.2) + 1.5 × 10) × 1² / 2 = 36.7 kNm/m; the toe: 1.35 × 57 × 0.5² / 2 = 9.6. The heel governs, its bars on top.
        var base_ = parts.Single(check => check.Name == "Base in bending");
        Assert.InRange(base_.Demand, 36, 37.5);
        Assert.Contains("heel", base_.Note);
    }

    [Fact]
    public void AMasonryStemHasItsFlexuralStrength()
    {
        // 215 thick: Z 7.7e6 mm³/m; 0.9 × 5 × 2 = 9 kN/m is 0.042 N/mm²; (0.25 / 2.7 + 0.042) × 7.7e6 = 1.0 kNm/m.
        var check = RetainingDesign.MasonryStem(Wall, 0.8, 215, 0.25, 5);
        Assert.InRange(check.Capacity, 0.98, 1.05);
        Assert.Equal("Stem in bending", check.Name);
    }

    [Fact]
    public void ItFindsTheBaseThatStands()
    {
        // Wider until it stands, the extra under the earth, no wider than it must be.
        Assert.False(RetainingDesign.StabilityEurocode(Wall, 150).All(check => check.Ratio <= 1));
        var wanted = RetainingDesign.BaseWanted(Wall, section => RetainingDesign.StabilityEurocode(section, 150));
        Assert.NotNull(wanted);
        Assert.True(wanted > Wall.Base);
        var fits = Wall with { Heel = wanted!.Value - Wall.Stem - Wall.Toe };
        Assert.True(RetainingDesign.StabilityEurocode(fits, 150).All(check => check.Ratio <= 1));
        var less = fits with { Heel = fits.Heel - 50 };
        Assert.False(RetainingDesign.StabilityEurocode(less, 150).All(check => check.Ratio <= 1));
    }

    [Fact]
    public void AWorstMetreIsNotAnAverage()
    {
        // 1 kN every 500 mm over 10 m is 2 kN/m all along, at its ends as in its middle: its ends' cells are half as long and carry half.
        var even = Enumerable.Range(0, 21).Select(i => (i * 500.0, i is 0 or 20 ? 0.5 : 1.0)).ToList();
        Assert.Equal(2, FoundationDesign.WorstLine(even), 6);

        // 10 kN more at one node over a cell 500 long stands in the metre round it: 2 + 10 = 12 kN/m.
        var loaded = even.Select((entry, i) => i == 10 ? (entry.Item1, entry.Item2 + 10) : entry).ToList();
        Assert.Equal(12, FoundationDesign.WorstLine(loaded), 6);

        // A line shorter than the metre: all it has, over its length.
        Assert.Equal(10, FoundationDesign.WorstLine(new[] { (0.0, 3.0), (500.0, 2.0) }), 6);
    }

    [Theory]
    [InlineData(GroundType.StiffClay, 1500, 1500, 8)]
    [InlineData(GroundType.FirmClay, 600, 6000, 8.53)]
    [InlineData(GroundType.Sand, 1500, 1500, 18)]
    [InlineData(GroundType.Rock, 1500, 1500, 300)]
    public void TheGroundIsLessStiffUnderABiggerFooting(GroundType ground, double width, double length, double stiffness)
    {
        // Stiff clay: 40 × 0.3 / 1.5 = 8, square. Firm clay: 25 × 0.3 / 0.6 = 12.5, × 10.5 / 15 for a strip ten long = 8.75, and sand 50 × (1.8 / 3)² = 18.
        var expected = ground == GroundType.FirmClay ? 8.75 : stiffness;
        Assert.Equal(expected, FoundationDesign.Stiffness(ground, width, length), 6);
    }

    private static BimDocument House(GroundType ground = GroundType.FirmClay)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Ground = ground;
        var (lower, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 9000), new Point2D(0, 9000) };
        for (var i = 0; i < 4; i++) document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = lower, TopLevelId = first, StructuralUsage = StructuralUsage.Bearing });
        foreach (var each in document.Walls.ToList()) document.Add(Footings.Fit(document, each));
        var steel = document.TypesOf<BeamType>().First(each => each.Name.StartsWith("UB 254", StringComparison.Ordinal));
        document.Add(new Beam { TypeId = steel.Id, LevelId = first, Start = new Point2D(0, 4500), End = new Point2D(12000, 4500) });
        var timber = document.TypesOf<BeamType>().Where(each => each.Material == BeamMaterial.Timber).OrderBy(each => Math.Abs(each.Depth - 220)).First();
        for (var i = 0; i < 19; i++)
        {
            document.Add(new Beam { TypeId = timber.Id, LevelId = first, Start = new Point2D(600 + i * 600, 0), End = new Point2D(600 + i * 600, 4500), Usage = BeamUsage.Joist });
            document.Add(new Beam { TypeId = timber.Id, LevelId = first, Start = new Point2D(600 + i * 600, 4500), End = new Point2D(600 + i * 600, 9000), Usage = BeamUsage.Joist });
        }

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)).Id, LevelId = first, SpanDirection = 0 };
        floor.SetBoundary(corners);
        document.Add(floor);
        return document;
    }

    private static StructuralAnalysis Analysed(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        return StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
    }

    [Fact]
    public void ABeamLandingOnAWallIsFollowedDownToItsStrip()
    {
        // The end wall under the beam carries half as much again over its middle metre as its average: the old check saw only the average.
        var document = House();
        var analysis = Analysed(document);
        var checks = FoundationDesign.Of(document, analysis);
        var loaded = checks.OrderByDescending(check => check.Checks.FirstOrDefault(each => each.Name == "Bearing")?.Demand ?? 0).First();
        Assert.Contains("heaviest metre", loaded.Checks.First(each => each.Name == "Bearing").Note);
    }

    [Fact]
    public void AFootingSaysHowFarItGoesDown()
    {
        var document = House();
        var analysis = Analysed(document);
        var checks = FoundationDesign.Of(document, analysis);
        var strip = checks.First(check => check.Foundation is WallFooting);

        // About 13 kN/m over a 450 strip and its own 5 kPa, 34 kPa; firm clay 25 × 0.3 / 0.45 × 10.5 / 15 = 11.7 MN/m³: 3 mm or so, under Annex H's 50.
        var settles = strip.Checks.Single(each => each.Name == "Settlement");
        Assert.InRange(settles.Demand, 2, 6);
        Assert.Equal(50, settles.Capacity);

        // A wall with a beam landing on it goes down 2.5 mm at its ends and 4.7 under the beam, 3 m on: 0.7 mm/m, well under 2. That is the worst of a row of 1 m stretches, each a true kN/m, and no noise from how nodes fall in them.
        var rotations = checks.SelectMany(check => check.Checks).Where(each => each.Name == "Relative rotation").ToList();
        Assert.NotEmpty(rotations);
        Assert.All(rotations, each => Assert.Equal(2, each.Capacity));
        Assert.InRange(rotations.Max(each => each.Demand), 0.3, 1.2);
    }

    [Fact]
    public void AStrongerFootingIsTakenAtItsOwnGrade()
    {
        var document = House();
        foreach (var type in document.TypesOf<FootingType>()) type.Strength = 35;
        var analysis = Analysed(document);
        var strength = FoundationDesign.Of(document, analysis);
        foreach (var type in document.TypesOf<FootingType>()) type.Strength = 0;
        var usual = FoundationDesign.Of(document, analysis);
        double Capacity(IReadOnlyList<FoundationCheck> checks) => checks.First(check => check.Foundation is WallFooting).Checks.Where(each => each.Unit == "mm").Select(each => each.Demand).DefaultIfEmpty(1).Max();
        Assert.True(Capacity(strength) <= Capacity(usual));
    }

    private static (BimDocument Document, Wall Wall) Garden(string wall = "Generic", GroundType ground = GroundType.Sand, double width = 2000, double eccentricity = 0, bool us = false)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Ground = ground;
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith(wall, StringComparison.Ordinal));
        var retaining = type.Duplicate("Retaining");
        retaining.Function = WallFunction.Retaining;
        document.AddType(retaining);
        var (lower, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var placed = new Wall { Start = new Point2D(0, 0), End = new Point2D(8000, 0), TypeId = retaining.Id, LevelId = lower, TopLevelId = first };
        document.Add(placed);
        var footing = new FootingType("Base", FootingKind.Strip) { Width = width, Thickness = 300 };
        document.AddType(footing);
        document.Add(new WallFooting { WallId = placed.Id, LevelId = lower, TypeId = footing.Id, Depth = 300, Eccentricity = eccentricity });
        return (document, placed);
    }

    [Fact]
    public void ARetainingWallIsCheckedFromTheModel()
    {
        var (document, wall) = Garden();
        var check = RetainingDesign.Check(document, wall, "RW1");
        Assert.Contains(check.Checks, each => each.Name == "Sliding");
        Assert.Contains(check.Checks, each => each.Name == "Overturning");
        Assert.Contains(check.Checks, each => each.Name == "Stem in bending" && each.Note.Contains("mm²/m"));
        Assert.Contains("typical for the ground", check.Limits);
        Assert.Contains("drained behind it", check.Limits);
        Assert.Contains("ground in front", check.Limits);
    }

    [Fact]
    public void ItsFootingCanStandOutBehindIt()
    {
        // 2 m wide with its middle 600 toward the retained side, the exterior: the heel is 1000 + 600 - stem / 2.
        var (document, wall) = Garden(eccentricity: 600);
        var footing = document.All<WallFooting>().Single();
        var section = RetainingDesign.SectionOf(document, wall, footing, 30, 18, 30)!;
        Assert.Equal(1600 - section.Stem / 2, section.Heel, 6);
        Assert.Equal(400 - section.Stem / 2, section.Toe, 6);
        Assert.Equal(2000, section.Base, 6);
    }

    [Fact]
    public void WithoutAFootingItIsSaidNotToBeChecked()
    {
        var (document, wall) = Garden();
        document.Remove(document.All<WallFooting>().Single());
        var check = RetainingDesign.Check(document, wall, "RW1");
        Assert.Empty(check.Checks);
        Assert.Contains("no footing", check.Limits);
    }

    [Fact]
    public void TooNarrowItSaysWhatItWants()
    {
        var (document, wall) = Garden(width: 600);
        var check = RetainingDesign.Check(document, wall, "RW1");
        Assert.False(check.Enough);
        Assert.Contains("It wants a base about", check.Limits);
    }

    [Fact]
    public void ASiteInvestigationsFigureIsUsed()
    {
        var (document, wall) = Garden();
        document.ProjectInformation.RetainedFriction = 38;
        Assert.Contains("38° as set", RetainingDesign.Check(document, wall, "RW1").Limits);
    }

    [Fact]
    public void AUsRetainingWallIsCheckedByItsOwnRules()
    {
        var (document, wall) = Garden(us: true);
        var check = RetainingDesign.Check(document, wall, "RW1");
        Assert.Contains(check.Checks, each => each.Name == "Sliding, at 1.5" && each.Clause.Contains("R404.4"));
        Assert.Contains(check.Checks, each => each.Name == "Stem in bending" && each.Clause == "22.2");
    }

    [Fact]
    public void WarningsSayARetainingWallWillNotStand()
    {
        var (document, wall) = Garden(width: 600);
        var problems = StructureRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard);
        Assert.Single(problems);
        Assert.Contains("wants a base", problems[0].Said);

        var (fine, stands) = Garden(width: 3000, eccentricity: 900);
        var check = RetainingDesign.Check(fine, stands, "RW1");
        Assert.True(check.Checks.Where(each => !each.Name.StartsWith("Stem", StringComparison.Ordinal) && !each.Name.StartsWith("Base", StringComparison.Ordinal)).All(each => each.Ratio <= 1), check.Limits);
    }

    [Fact]
    public void AnOrdinaryWallIsNoRetainingWall()
    {
        var document = House();
        Assert.Empty(RetainingDesign.Of(document));
        Assert.Empty(StructureRules.Problems(document, document.Walls.First(), document.ProjectInformation.ClearanceStandard));
    }

    [Fact]
    public void TheNewSettingsAreKeptWithTheProject()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.RetainedSurcharge = 15;
        document.ProjectInformation.RetainedFriction = 34;
        var footing = new FootingType("Base", FootingKind.Strip) { Strength = 35 };
        document.AddType(footing);
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(15, reopened.ProjectInformation.RetainedSurcharge);
        Assert.Equal(34, reopened.ProjectInformation.RetainedFriction);
        Assert.Equal(35, reopened.FindType<FootingType>(footing.Id)!.Strength);
        Assert.Equal(35, footing.Duplicate("Copy").Strength);
    }
}
