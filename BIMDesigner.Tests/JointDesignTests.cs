using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Joints and bearings (round 34): concentrated loads on masonry against Bright and Roberts's worked cases, padstones, ties against PD 6697's table as Ancon quotes it, timber across
/// the grain by hand, bolts against the Blue Book and AISC's manual, a base plate against SCI P398's worked example; then whole buildings.
/// </summary>
public class JointDesignTests
{
    private const double Psi = 0.00689476;

    [Fact]
    public void ALoadSpreadsSixtyDegreesAsTheWorkedCasesHaveIt()
    {
        // Bright and Roberts: 100 by 150 on a wall 2.5 m to the load. In the middle of a wall it spreads to 1.54 m at mid height, at its end to 0.82 m.
        Assert.InRange(JointDesign.EffectiveLength(100, 1250, 1250, 2500), 1530, 1550);
        Assert.InRange(JointDesign.EffectiveLength(100, 0, 2500, 2500), 810, 830);
    }

    [Fact]
    public void EnhancementIsCappedAsTheWorkedCasesHaveIt()
    {
        // Mid wall: the cap, 1.25 + 1250/5000 = 1.5. At its end the uncapped 1.37 stops at 1.25.
        Assert.Equal(1.5, JointDesign.Enhancement(1250, 2500, 0.015e6, 0.232e6), 6);
        Assert.Equal(1.25, JointDesign.Enhancement(0, 2500, 0.015e6, 0.123e6), 6);

        // Past 0.45 Ab/Aef counts no more, and it never falls under 1.
        Assert.Equal(JointDesign.Enhancement(0, 2500, 45, 100), JointDesign.Enhancement(0, 2500, 90, 100), 6);
        Assert.True(JointDesign.Enhancement(0, 2500, 90, 100) >= 1);
        Assert.Equal((1 - 0) * (1.5 - 1.1 * 0.45), JointDesign.Enhancement(0, 2500, 90, 100), 6);
    }

    [Fact]
    public void AWallThatCanTakeABeamWantsNoPadstone()
    {
        // 146 by 140 on fd 2.4: β 1.5 at most, 1.5 × 20,440 × 2.4 = 73.6 kN.
        var (check, wants) = JointDesign.OnMasonry(40, 146, 140, 3000, 3000, 3000, 2.4);
        Assert.Equal("Bearing on the wall", check.Name);
        Assert.Equal(1.5 * 146 * 140 * 2.4 / 1000, check.Capacity, 6);
        Assert.Equal(string.Empty, wants);
    }

    [Fact]
    public void ABeamOnAircreteWantsAPadstone()
    {
        // 40 kN on fd 0.67 carries only 20.4 kN. Three times the bearing is 438; 1.5 fd over 140 wants 286: 450 of padstone, 215 high.
        var fd = 2.0 / 3;
        var (check, wants) = JointDesign.OnMasonry(40, 146, 140, 3000, 3000, 3000, fd);
        Assert.Equal("Bearing on a padstone", check.Name);
        Assert.Contains("450 long, 215 high and 140 wide", wants);
        Assert.Equal(1.5 * fd * 140 * 450 / 1000, check.Capacity, 6);
        Assert.True(check.Ratio <= 1);
    }

    [Fact]
    public void APadstoneNeedsTheWallToStandOn()
    {
        // Past three times the bearing, the stress decides: 400 kN wants 2,860 mm of a 2 m wall, and there is no padstone for that.
        var (check, wants) = JointDesign.OnMasonry(400, 146, 140, 900, 900, 3000, 2.0 / 3);
        Assert.Equal("Bearing on the wall", check.Name);
        Assert.True(check.Ratio > 1);
        Assert.Equal(string.Empty, wants);
        Assert.Contains("more than the wall has", check.Note);
    }

    [Fact]
    public void ALintelTakesMoreBearingWhereItsEndsAskIt()
    {
        var (light, bearing) = JointDesign.LintelEnd(10, 140, 500, 3000, 2100, 2.0 / 3);
        Assert.Equal(150, bearing);
        Assert.True(light.Ratio <= 1);

        // 25 kN on 150 by 140 at fd 0.67 is over the 14 to 21 kN it can carry; it wants more.
        var (heavy, more) = JointDesign.LintelEnd(25, 140, 500, 3000, 2100, 2.0 / 3);
        Assert.True(more > 150);
        Assert.True(heavy.Ratio <= 1);
    }

    [Theory]
    [InlineData(0.20, 8, "Type 4")]
    [InlineData(0.40, 8, "Type 3")]
    [InlineData(0.80, 8, "Type 2")]
    [InlineData(1.50, 8, "Type 1")]
    [InlineData(0.20, 12, "Type 3")]
    [InlineData(0.20, 20, "Type 1")]
    public void TheLightestTieThatDoesIsChosen(double wind, double height, string type) =>
        Assert.Equal(type, JointDesign.TiesFor(wind, height).Type.Name);

    [Fact]
    public void TiesAreTwoAndAHalfAMetreUnlessTheWindAsksMore()
    {
        // Each Type 1 tie at 2.0 kN over 3.0 carries 0.67 kN: 3 kN/m² wants 4.5 a metre.
        var (type, density, needed) = JointDesign.TiesFor(3, 8);
        Assert.Equal("Type 1", type.Name);
        Assert.Equal(4.5, needed, 6);
        Assert.Equal(4.5, density);

        Assert.Equal(2.5, JointDesign.TiesFor(0.2, 8).Density);
    }

    [Theory]
    [InlineData(75, 200)]
    [InlineData(100, 225)]
    [InlineData(125, 250)]
    [InlineData(150, 275)]
    [InlineData(175, 300)]
    [InlineData(200, 325)]
    public void TiesAreLongEnoughForTheirCavity(double cavity, double length) => Assert.Equal(length, JointDesign.TieLength(cavity));

    [Fact]
    public void TimberAcrossTheGrainIsWorkedOutByEurocode5()
    {
        // 5 kN on 47 wide, 100 of contact: 130 effective, 0.82 N/mm². C24: 2.5 × 0.8 / 1.3 = 1.54, by 1.5.
        var (check, wants) = JointDesign.TimberEndEurocode(5, 47, 100, TimberDesign.C24, 0.8);
        Assert.Equal(5000.0 / (47 * 130), check.Demand, 6);
        Assert.Equal(1.5 * 2.5 * 0.8 / 1.3, check.Capacity, 6);

        // It would do on 46 of effective contact: half of that and as much again past it, the extra being no more than the contact.
        Assert.InRange(wants, 22.5, 23.5);
    }

    [Fact]
    public void GlulamBearsHarderAndAShortContactGainsOnlyItsOwnLength()
    {
        Assert.Equal(1.75 * 2.5 * 0.8 / 1.25, JointDesign.TimberEndEurocode(5, 90, 100, TimberDesign.Gl24h, 0.8).Check.Capacity, 6);

        // 20 mm of contact gains 20 past it, not 30.
        Assert.Equal(5000.0 / (47 * 40), JointDesign.TimberEndEurocode(5, 47, 20, TimberDesign.C24, 0.8).Check.Demand, 6);
    }

    [Fact]
    public void TimberAcrossTheGrainIsWorkedOutByTheNds()
    {
        var (check, _) = JointDesign.TimberEndNds(5, 47, 100);
        Assert.Equal(625 * Psi * 1.67 * 0.9, check.Capacity, 6);
        Assert.InRange(check.Capacity, 6.4, 6.55);
    }

    [Fact]
    public void BoltsComeToThePublishedResistances()
    {
        // The Blue Book's M16, M20 and M24 8.8 in single shear: 60.3, 94.1 and 136 kN.
        Assert.InRange(JointDesign.BoltShearEurocode(157), 60.2, 60.4);
        Assert.InRange(JointDesign.BoltShearEurocode(245), 94.0, 94.2);
        Assert.InRange(JointDesign.BoltShearEurocode(353), 135, 136);

        // 10 mm of S275, fu 410, M20 in a 22 hole, 40 from the end and 30 from the edge: αd 0.606, k1 2.12, 84 kN.
        Assert.InRange(JointDesign.BoltBearingEurocode(20, 22, 10, 410, 40, 30), 83.5, 85);
    }

    private static readonly SteelSection Ub = new("I Section", 406.4, 177.9, 12.8, 7.8, 7600, 21600e4, 1160e4, 1060e3, 130e3, 1190e3, 202e3, 45e4, 0.5e12, 10);

    [Fact]
    public void AnEndPlateIsAsDeepAndAsWellBoltedAsItsShearAsks()
    {
        // A 406 UB at 100 kN: two rows of M20 at 0.8 of 94.1 kN carry 301 kN, deep at least 0.6 of the beam.
        var (checks, wants) = JointDesign.EndPlateEurocode(100, Ub, 12);
        Assert.Equal(4 * 0.8 * 94.08, checks.Single(check => check.Name == "Bolts in shear").Capacity, 1);
        Assert.Contains("150 by 244 by 10", wants);
        Assert.All(checks, check => Assert.True(check.Ratio <= 1, check.Name));

        // At 500 kN it takes four rows.
        var (more, larger) = JointDesign.EndPlateEurocode(500, Ub, 12);
        Assert.Contains("8 M20", larger);
        Assert.True(more.Single(check => check.Name == "Bolts in shear").Ratio <= 1);
    }

    [Fact]
    public void WebShearOverThePlateIsTheBeamsNotTheBolts()
    {
        // 0.9 × 244 × 7.8 × 355 / √3 = 351 kN, as P358 takes it.
        var web = JointDesign.EndPlateEurocode(100, Ub, 12).Checks.Single(check => check.Name == "Web in shear at the plate");
        Assert.InRange(web.Capacity, 345, 356);
    }

    [Fact]
    public void AnAiscEndPlateComesToItsManual()
    {
        // 3/4 in A325-N in single shear: φrn 17.9 kips (Table 7-1). E70 fillets 1.392 kips/in a sixteenth (Eq. 8-2a).
        var (checks, _) = JointDesign.EndPlateAisc(100, Ub, 12);
        var bolts = checks.Single(check => check.Name == "Bolts in shear");
        Assert.InRange(bolts.Capacity / 4 / 4.44822, 17.8, 18.0);
        var welds = checks.Single(check => check.Name == "Welds to the web");
        var length = (2 * 31.75 + 76.2 - 2 * 6.35) / 25.4;
        Assert.InRange(welds.Capacity / 4.44822 / (2 * length * 4), 1.38, 1.40);
    }

    [Fact]
    public void ABasePlatesWidthIsTheWorkedExamples()
    {
        // SCI P398 Example E.1: 50 mm S275 at fy 255 on fjd 17 gives c 112; a 305 UKC's flange then bears over 243 by 531.
        var c = JointDesign.BaseWidth(50, 255, 17);
        Assert.InRange(c, 111, 113);
        var column = new SteelSection("I Section", 314.5, 307.4, 18.7, 12.0, 15000, 27700e4, 9060e4, 1760e3, 589e3, 1960e3, 895e3, 160e4, 4e12, 15);
        var flange = (2 * c + column.Tf) * (2 * c + column.B);
        Assert.InRange(flange, 243.0 * 531 * 0.99, 243.0 * 531 * 1.01);

        // Two flanges and the web between what is left of them.
        Assert.Equal(2 * flange + (column.H - 2 * column.Tf - 2 * c) * (column.Tw + 2 * c), JointDesign.BaseArea(column, c), 3);
    }

    [Fact]
    public void ABasePlateGrowsWithItsLoad()
    {
        var column = new SteelSection("I Section", 152.4, 152.2, 6.8, 5.8, 2900, 1250e4, 400e4, 164e3, 52.6e3, 182e3, 80.2e3, 4.6e4, 0.03e12, 7);
        var (light, thin) = JointDesign.BasePlateEurocode(200, column, 28);
        Assert.Contains("by 10", thin);
        Assert.True(light.Ratio <= 1);
        Assert.Contains("base plate", JointDesign.BasePlateEurocode(1500, column, 28).Wants);
        Assert.True(JointDesign.BasePlateEurocode(1500, column, 28).Check.Ratio <= 1);

        // Past what a 40 mm plate carries it says so and wants nothing it cannot give.
        Assert.True(JointDesign.BasePlateEurocode(2500, column, 28).Check.Ratio > 1);
    }

    private static BimDocument House()
    {
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        return document;
    }

    private static IReadOnlyList<JointCheck> Joints(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        return JointDesign.Of(document, SecondOrderAnalysis.Run(document, analysis));
    }

    private static Beam Steel(BimDocument document, Point2D from, Point2D to)
    {
        var type = document.TypesOf<BeamType>().First(each => each.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var beam = new Beam { TypeId = type.Id, LevelId = document.Levels[1].Id, Start = from, End = to };
        document.Add(beam);
        return beam;
    }

    [Fact]
    public void WhatABeamPutsOnAWallIsWhatHoldsItUp()
    {
        // A steel beam from wall to wall: what it puts on its two walls adds up to the load on it and its own weight, by statics.
        var document = House();
        Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var second = SecondOrderAnalysis.Run(document, analysis);
        var beam = model.Members.Single(member => member.Kind == AnalyticalMemberKind.Beam);
        var (combination, down) = second.LandingsOf(beam).First(entry => entry.Combination.Name.StartsWith("ULS1", StringComparison.Ordinal));
        var ends = down.GetValueOrDefault(beam.Start) + down.GetValueOrDefault(beam.End);

        // UB 254x146x31, 31.1 kg/m at 1.35 over about 10.16 m, as much at each end.
        var weight = 1.35 * 31.1 * 9.81 / 1000 * beam.Length / 1000;
        Assert.InRange(ends, 0.95 * weight, 1.05 * weight);
        Assert.Equal(down.GetValueOrDefault(beam.Start), down.GetValueOrDefault(beam.End), 1);
    }

    [Fact]
    public void ABeamLandingOnAWallIsChecked()
    {
        var document = House();
        Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        var joint = Joints(document).First(each => each.Kind == "Beam on a wall");
        var bearing = joint.Checks.Single(check => check.Name.StartsWith("Bearing", StringComparison.Ordinal));
        Assert.Contains(joint.Checks, check => check.Name == "Beam's web on the wall");
        Assert.StartsWith("ULS", bearing.Under, StringComparison.Ordinal);
    }

    [Fact]
    public void JoistsAreKeptToTheWorstOfALayoutAndCheckedAcrossTheGrain()
    {
        var document = House();
        var timber = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber);
        for (var i = 0; i < 3; i++)
            document.Add(new Beam { TypeId = timber.Id, LevelId = document.Levels[1].Id, Start = new Point2D(2000 + i * 400, 0), End = new Point2D(2000 + i * 400, 8000), Usage = BeamUsage.Joist });
        var joists = Joints(document).Where(each => each.Kind == "Joist on a wall").ToList();
        Assert.NotEmpty(joists);
        Assert.All(joists, joist =>
        {
            Assert.Equal("Across the grain", joist.Checks.Single().Name);
            Assert.Contains("hanger rated", joist.Wants);
        });
    }

    [Fact]
    public void ASteelBeamOnASteelColumnWantsAnEndPlate()
    {
        var document = House();
        var post = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var column = new Column { TypeId = post.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Location = new Point2D(5000, 4000), Structural = true };
        document.Add(column);
        document.Add(Footings.Fit(document, column));
        Steel(document, new Point2D(0, 4000), new Point2D(5000, 4000));
        var joints = Joints(document);

        // On top of the post it is a cap plate, said to be left out; two beams meeting there are not a joint with each other.
        Assert.Contains(joints, each => each.Kind == "Beam on a post" && each.Wants.Contains("cap plate"));
        Assert.DoesNotContain(joints, each => each.Kind == "Steel end plate");

        // And its foot a base plate.
        Assert.Contains(joints, each => each.Kind == "Base plate" && each.Wants.Contains("base plate"));
    }

    [Fact]
    public void ASteelBeamIntoAnotherWantsAnEndPlate()
    {
        // One beam across the house, another framing into it partway along.
        var document = House();
        Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        Steel(document, new Point2D(5000, 0), new Point2D(5000, 4000));
        var plate = Joints(document).Single(each => each.Kind == "Steel end plate");
        Assert.Contains("M20 8.8", plate.Wants);
        Assert.Equal(7, plate.Checks.Count);
    }

    [Fact]
    public void ARunOfJoistsIsOneRowAndNeverAsksLessThanTheLeastBearing()
    {
        var document = House();
        var timber = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber);
        for (var i = 0; i < 5; i++)
            document.Add(new Beam { TypeId = timber.Id, LevelId = document.Levels[1].Id, Start = new Point2D(2000 + i * 400, 0), End = new Point2D(2000 + i * 400, 8000), Usage = BeamUsage.Joist });
        var joists = Joints(document).Where(each => each.Kind == "Joist on a wall").ToList();
        Assert.True(joists.Count <= 2, $"{joists.Count} rows");
        Assert.Contains(joists, each => each.Name.Contains("and 4 like it"));
        Assert.All(joists, joist => Assert.StartsWith("90 mm of bearing", joist.Wants, StringComparison.Ordinal));
    }

    [Fact]
    public void TimberIsWorstUnderWhatLastsNotJustWhatIsHeaviest()
    {
        // 5 kN at kmod 1.1 asks less of the timber than 4.5 kN at 0.8.
        var shortLoad = JointDesign.TimberEndEurocode(5, 47, 100, TimberDesign.C24, 1.1).Check;
        var longLoad = JointDesign.TimberEndEurocode(4.5, 47, 100, TimberDesign.C24, 0.8).Check;
        Assert.True(longLoad.Ratio > shortLoad.Ratio);
    }

    [Fact]
    public void ABeamOverAPostIsSaidToWantACapPlate()
    {
        // One beam wall to wall, a post under its middle: it does not end there, and the post holds it up.
        var document = House();
        var post = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var column = new Column { TypeId = post.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Location = new Point2D(5000, 4000), Structural = true };
        document.Add(column);
        document.Add(Footings.Fit(document, column));
        Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        Assert.Contains(Joints(document), each => each.Kind == "Beam on a post" && each.Wants.Contains("cap plate"));
    }

    private static (BimDocument Document, Floor Floor) Joisted(double? span)
    {
        var document = House();
        var boards = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Timber", StringComparison.Ordinal));
        var floor = new Floor { TypeId = boards.Id, LevelId = document.Levels[1].Id, SpanDirection = span };
        floor.SetBoundary(new[] { new Point2D(-165, -165), new Point2D(10165, -165), new Point2D(10165, 8165), new Point2D(-165, 8165) });
        document.Add(floor);
        var timber = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber);
        for (var i = 0; i < 9; i++)
            document.Add(new Beam { TypeId = timber.Id, LevelId = document.Levels[1].Id, Start = new Point2D(1000 + i * 1000, 0), End = new Point2D(1000 + i * 1000, 8000), Usage = BeamUsage.Joist });
        return (document, floor);
    }

    [Fact]
    public void JoistsTheFloorSpansAlongAreSaidToCarryNothing()
    {
        // Its short way is the way they run: the floor bears straight on the walls past them, and their own checks would read as if all were well.
        var (document, _) = Joisted(null);
        var model = AnalyticalModel.Of(document);
        Assert.Contains(StructuralLoading.Of(document, model).Problems, problem => problem.Said.Contains("joists"));
    }

    [Fact]
    public void JoistsTheFloorSpansAcrossCarryItAndNothingIsSaid()
    {
        var (document, _) = Joisted(0);
        var model = AnalyticalModel.Of(document);
        Assert.DoesNotContain(StructuralLoading.Of(document, model).Problems, problem => problem.Said.Contains("joists"));

        // A joist in from the wall: all that is on it reaches its two ends, by statics, the boards tying it in their own plane and holding none of it up.
        var loading = StructuralLoading.Of(document, model);
        var analysis = StructuralAnalysis.Run(document, model, loading);
        var joist = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Joist).ElementAt(3);
        var (combination, down) = SecondOrderAnalysis.Run(document, analysis).LandingsOf(joist).First(entry => entry.Combination.Name.StartsWith("ULS1", StringComparison.Ordinal));
        var along = combination.Terms.Sum(term => term.Factor * loading.LineLoads.Where(load => ReferenceEquals(load.Member, joist) && load.Case == term.Case).Sum(load => -load.Force.Z));
        var weight = 1.35 * joist.Section.Area / 1e6 * joist.Section.Material.Density * 9.81 / 1000;
        var total = (along + weight) * joist.Length / 1000;
        Assert.InRange(down.GetValueOrDefault(joist.Start) + down.GetValueOrDefault(joist.End), 0.97 * total, 1.03 * total);
    }

    [Fact]
    public void AJoistOverAPostSitsOnTheBeamNotThePost()
    {
        var document = House();
        var post = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var column = new Column { TypeId = post.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Location = new Point2D(5000, 4000), Structural = true };
        document.Add(column);
        document.Add(Footings.Fit(document, column));
        Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        var timber = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber);
        document.Add(new Beam { TypeId = timber.Id, LevelId = document.Levels[1].Id, Start = new Point2D(5000, 0), End = new Point2D(5000, 4000), Usage = BeamUsage.Joist });
        var joint = Joints(document).Single(each => each.Kind == "Joist on a beam");
        Assert.DoesNotContain("C1", joint.Name);
    }

    [Fact]
    public void ABeamInAWallThatGoesHigherKeepsItsHeightAndWhatIsFramedToIt()
    {
        // Walls run up past the first floor, as walls attached to a roof do: the beam stays at its level, and joists landing on it stay joined.
        var document = House();
        foreach (var wall in document.Walls) wall.UnconnectedHeight = 3150;
        var beam = Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        var timber = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber);
        document.Add(new Beam { TypeId = timber.Id, LevelId = document.Levels[1].Id, Start = new Point2D(5000, 0), End = new Point2D(5000, 4000), Usage = BeamUsage.Joist });
        var model = AnalyticalModel.Of(document);
        var member = model.Members.Single(each => each.Element == beam);
        Assert.All(member.Nodes, node => Assert.Equal(3000, node.At.Z, 0));
        Assert.Equal(3, member.Nodes.Count);
    }

    [Fact]
    public void ABeamJustOverAWallsTopComesDownOntoIt()
    {
        var document = House();
        foreach (var wall in document.Walls) wall.UnconnectedHeight = 2900;
        var beam = Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        var member = AnalyticalModel.Of(document).Members.Single(each => each.Element == beam);
        Assert.Equal(2900, member.Start.At.Z, 0);
    }

    [Fact]
    public void ABeamDrawnAsAJoistIsHeldAlongItsTopAndSharesItsLoad()
    {
        // Nailed boards hold its compression edge all the way: no lateral buckling length, whichever way it was drawn.
        var (document, _) = Joisted(0);
        var model = AnalyticalModel.Of(document);
        var joist = model.Members.First(member => member.Element is Beam { Usage: BeamUsage.Joist });
        Assert.Equal(AnalyticalMemberKind.Joist, joist.Kind);
        Assert.Equal(0, MemberStrength.LengthsOf(model, joist).Lateral);
    }

    [Fact]
    public void ABeamWithJoistsOnItBendsOverItsWholeSpan()
    {
        // 10.16 m wall to wall, joists a metre apart landing on it. Measured between them it would read nothing; over its span it is 5 w L⁴ / 384 E I of what they bring.
        var document = House();
        var beam = Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        var timber = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber);
        for (var i = 0; i < 9; i++)
            document.Add(new Beam { TypeId = timber.Id, LevelId = document.Levels[1].Id, Start = new Point2D(1000 + i * 1000, 0), End = new Point2D(1000 + i * 1000, 4000), Usage = BeamUsage.Joist });
        var boards = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Timber", StringComparison.Ordinal));
        var floor = new Floor { TypeId = boards.Id, LevelId = document.Levels[1].Id, SpanDirection = 0 };
        floor.SetBoundary(new[] { new Point2D(-165, -165), new Point2D(10165, -165), new Point2D(10165, 4000), new Point2D(-165, 4000) });
        document.Add(floor);

        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var member = model.Members.Single(each => each.Element == beam);
        var combination = analysis.Loading.Combinations.First(each => each.Name.StartsWith("ULS1", StringComparison.Ordinal));
        var (deflection, span) = analysis.DeflectionOf(member, combination);
        Assert.Equal(member.Length, span, 0);

        // All that reaches it, spread over it.
        var second = SecondOrderAnalysis.Run(document, analysis);
        var (_, down) = second.LandingsOf(member).First(entry => entry.Combination.Name.StartsWith("ULS1", StringComparison.Ordinal));
        var w = (down.GetValueOrDefault(member.Start) + down.GetValueOrDefault(member.End)) * 1000 / span;
        var table = 5 * w * Math.Pow(span, 4) / (384 * member.Section.Material.Modulus * member.Section.Iy);
        Assert.InRange(deflection, 0.8 * table, 1.1 * table);
    }

    [Fact]
    public void ABeamOverAPostSpansFromThePost()
    {
        var document = House();
        var post = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var column = new Column { TypeId = post.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Location = new Point2D(5000, 4000), Structural = true };
        document.Add(column);
        document.Add(Footings.Fit(document, column));
        var beam = Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var member = model.Members.Single(each => each.Element == beam);
        Assert.InRange(analysis.DeflectionOf(member, analysis.Total).Span, 5000, 5100);
    }

    [Fact]
    public void ACavityWallHasItsTiesWorkedOut()
    {
        var document = House();
        var ties = Joints(document).Where(each => each.Kind == "Wall ties").ToList();
        Assert.Equal(4, ties.Count);
        var check = ties[0].Checks.Single();
        Assert.Equal(2.5, check.Capacity);
        Assert.Contains("a m²", ties[0].Wants);
    }

    [Fact]
    public void AUsProjectSaysWhatItLeavesOutAndChecksItsTimber()
    {
        var document = House();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Steel(document, new Point2D(0, 4000), new Point2D(10000, 4000));
        var timber = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber);
        document.Add(new Beam { TypeId = timber.Id, LevelId = document.Levels[1].Id, Start = new Point2D(2000, 0), End = new Point2D(2000, 8000), Usage = BeamUsage.Joist });
        var joints = Joints(document);
        Assert.Contains(joints, each => each.Kind == "Beam on a wall" && each.Limits.Contains("TMS 402"));
        Assert.Contains(joints, each => each.Kind == "Joist on a wall" && each.Checks.Single().Clause == "NDS 3.10");
        Assert.DoesNotContain(joints, each => each.Kind == "Wall ties" && each.Checks.Count > 0);
    }
}
