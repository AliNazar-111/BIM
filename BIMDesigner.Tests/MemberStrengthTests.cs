using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Members' strength (round 27): BS EN 1993-1-1 and AISC 360-16 against their published tables, BS EN
/// 1995-1-1 and the NDS, BS EN 1992-1-1 and ACI 318-19 against worked sections, and whole models checked
/// from what the analysis finds in them.
/// </summary>
public class MemberStrengthTests
{
    private const double Psi = 0.00689476, Inch = 25.4;

    private static readonly AnalyticalMaterial Steel = new("Steel", AnalyticalMaterialKind.Steel, "S355", 7850, 210000, 81000, 0.3, 12e-6, "");

    private static AnalyticalSection I(double depth, double width, double flange, double web) =>
        AnalyticalModel.SectionOf(new BeamType("I") { Section = BeamSection.IBeam, Depth = depth, Width = width, FlangeThickness = flange, WebThickness = web }, Steel);

    /// <summary>UB 254 x 146 x 31: Blue Book h 251.4, b 146.1, tw 6.0, tf 8.6.</summary>
    private static SteelSection Ub254 => SteelSection.Of(I(251.4, 146.1, 8.6, 6.0))!;

    private static SteelSection Inches(double depth, double width, double flange, double web) =>
        SteelSection.Of(I(depth * Inch, width * Inch, flange * Inch, web * Inch))!;

    private static double KipFeet(double newtonMillimetres) => newtonMillimetres / 4448.22 / 304.8;

    [Theory]
    [InlineData(0.13, 0.7253)]
    [InlineData(0.21, 0.6656)]
    [InlineData(0.34, 0.5970)]
    [InlineData(0.49, 0.5399)]
    [InlineData(0.76, 0.4671)]
    public void BucklingCurvesMatchTable6Point2AtASlendernessOfOne(double alpha, double chi) =>
        Assert.Equal(chi, SteelDesign.Chi(alpha, 1.0), 3);

    [Fact]
    public void LateralTorsionalBucklingIsAsTheUkNationalAnnexHasIt()
    {
        // A UKB of h/b 2.39, curve c, at λLT 1.07: 0.60, as a worked example has it; nothing lost to 0.4.
        Assert.Equal(0.60, SteelDesign.ChiLt(0.49, 1.07), 2);
        Assert.Equal(1.0, SteelDesign.ChiLt(0.49, 0.4));
    }

    [Fact]
    public void AUniversalBeamComesToItsBlueBookFigures()
    {
        var s = Ub254;
        Assert.InRange(s.Area, 0.98 * 39.7e2, 1.01 * 39.7e2);
        Assert.InRange(s.Wply, 0.99 * 393e3, 1.01 * 393e3);
        Assert.InRange(s.Wplz, 0.95 * 94.1e3, 1.01 * 94.1e3);
        Assert.InRange(s.Iy, 0.99 * 4413e4, 1.01 * 4413e4);
        Assert.InRange(s.Iw, 0.97 * 66e9, 1.03 * 66e9);
        Assert.Equal(355, SteelDesign.YieldStrength(s.Thickest));
        Assert.Equal(345, SteelDesign.YieldStrength(20));
        Assert.Equal(1, SteelDesign.ClassOf(s, 355, 0, 1e6));

        // Mc,y,Rd 140 kNm in S355.
        var held = new MemberLengths(6000, 6000, 0, 0);
        var bending = SteelDesign.Eurocode(s, held, new MemberForces(0, 0, 0, 0, 100, 0, false)).Single(check => check.Name == "Bending, strong axis");
        Assert.InRange(bending.Capacity, 0.99 * 140, 1.01 * 140);
        Assert.Equal(100 / bending.Capacity, bending.Ratio, 6);
        Assert.Equal("6.2.5, 6.2.8", bending.Clause);

        // Unrestrained over 6 m at C1 1.0: Mcr about 52 kNm, λLT 1.6, curve b.
        var free = new MemberLengths(6000, 6000, 6000, 6000);
        Assert.InRange(SteelDesign.CriticalMoment(s, 6000) / 1e6, 49, 56);
        var buckling = SteelDesign.Eurocode(s, free, new MemberForces(0, 0, 0, 0, 40, 0, false)).Single(check => check.Name == "Lateral torsional buckling");
        Assert.InRange(buckling.Capacity, 46, 56);

        // A floor holds its top, not its underside over a support.
        var floored = new MemberLengths(6000, 6000, 0, 6000);
        Assert.DoesNotContain(SteelDesign.Eurocode(s, floored, new MemberForces(0, 0, 0, 0, 40, 0, false)), check => check.Name == "Lateral torsional buckling");
        Assert.Contains(SteelDesign.Eurocode(s, floored, new MemberForces(0, 0, 0, 0, 40, 0, true)), check => check.Name == "Lateral torsional buckling");
    }

    [Fact]
    public void AColumnBucklesAndItsInteractionComesInUnderLoad()
    {
        // 203 UC 46: h 203.2, b 203.6, tf 11.0, tw 7.2; h/b under 1.2, so curves b and c. Over 3.5 m about 1,400 kN in S355.
        var s = SteelSection.Of(I(203.2, 203.6, 11.0, 7.2))!;
        var lengths = new MemberLengths(3500, 3500, 3500, 3500);
        var checks = SteelDesign.Eurocode(s, lengths, new MemberForces(-500, 0, 0, 0, 20, 0, false)).ToList();
        Assert.InRange(checks.Single(check => check.Name == "Buckling").Capacity, 1250, 1500);
        var strong = checks.Single(check => check.Name == "Buckling with bending, strong axis");
        var weak = checks.Single(check => check.Name == "Buckling with bending, weak axis");
        Assert.InRange(strong.Demand, 0.35, 0.6);
        Assert.Equal(1, strong.Capacity);
        Assert.True(weak.Demand > 500 / checks.Single(check => check.Name == "Buckling").Capacity, "the moment adds to the axial force");

        // In tension there is nothing to buckle.
        Assert.DoesNotContain(SteelDesign.Eurocode(s, lengths, new MemberForces(500, 0, 0, 0, 0, 0, false)), check => check.Name == "Buckling");
    }

    [Fact]
    public void AHollowSectionHasItsCornersAndASlenderOneItsEffectiveArea()
    {
        // 200 x 200 x 5 SHS: BS EN 10210-2 gives A 19.4 cm², Wpl 287 cm³... square-cornered it would be 19.0 plus a bit.
        var material = Steel;
        var square = AnalyticalModel.SectionOf(new ColumnType("SHS", ColumnShape.HollowRectangular, 200, 200) { WallThickness = 5 }, material);
        var s = SteelSection.Of(square)!;
        Assert.True(s.Area < square.Area, "the rounded corners take a little off");
        Assert.True(s.Area > 0.97 * square.Area);

        // Class 4 in compression: (200 - 15) / 5 = 37 is over 42 ε... at S355 ε 0.81, 34.2.
        Assert.Equal(4, SteelDesign.ClassOf(s, 355, 1e6, 0));
        Assert.True(SteelDesign.EffectiveArea(s, 355) < s.Area);
    }

    [Fact]
    public void AnIBeamComesToAiscsTables()
    {
        // W18x50: d 18.0, bf 7.50, tf 0.570, tw 0.355. Table 3-2: φMp 379 kip-ft to Lp, 5.83 ft; at Lr, 16.9 ft, 0.9 x 0.7 Fy Sx = 233.
        var w18 = Inches(18.0, 7.50, 0.570, 0.355);
        Assert.InRange(KipFeet(0.9 * SteelDesign.NominalMoment(w18, 0, 1)), 0.99 * 379, 1.01 * 379);
        Assert.InRange(KipFeet(0.9 * SteelDesign.NominalMoment(w18, 5.8 * 304.8, 1)), 0.99 * 379, 1.01 * 379);
        Assert.InRange(KipFeet(0.9 * SteelDesign.NominalMoment(w18, 16.9 * 304.8, 1)), 0.97 * 233, 1.03 * 233);

        // W14x90 is non-compact in its flange: Table 3-2's φMp is 574, not the 589 of Fy Zx.
        var w14 = Inches(14.0, 14.5, 0.710, 0.440);
        Assert.InRange(KipFeet(0.9 * SteelDesign.NominalMoment(w14, 0, 1)), 0.98 * 574, 1.02 * 574);

        // Table 4-14: φFcr 21.7 ksi at KL/r 100 and Fy 50; 0.877 Fe once slender.
        var fe = Math.PI * Math.PI * 29000000 * Psi / (100 * 100);
        Assert.Equal(21.7, 0.9 * SteelDesign.CriticalStress(fe) / Psi / 1000, 1);
        var slender = Math.PI * Math.PI * 29000000 * Psi / (200 * 200);
        Assert.Equal(0.877 * slender, SteelDesign.CriticalStress(slender), 6);

        // H1-1: under 0.2 of its axial strength, half of it counts; over, all of it and 8/9 of the moments.
        var lengths = new MemberLengths(3000, 3000, 3000, 3000);
        var small = SteelDesign.Aisc(w14, lengths, new MemberForces(-100, 0, 0, 0, 100, 0, false), 1).Single(check => check.Name == "Axial and bending together");
        Assert.Equal("H1-1b", small.Clause);
        var large = SteelDesign.Aisc(w14, lengths, new MemberForces(-3000, 0, 0, 0, 100, 0, false), 1).Single(check => check.Name == "Axial and bending together");
        Assert.Equal("H1-1a", large.Clause);
    }

    [Fact]
    public void TimberIsCheckedByBsEn1995()
    {
        // C24 as BS EN 338:2016 Table 1 has it.
        Assert.Equal((24, 14.5, 21, 4.0, 11000, 7400), (TimberDesign.C24.Fm, TimberDesign.C24.Ft0, TimberDesign.C24.Fc0, TimberDesign.C24.Fv, TimberDesign.C24.E0Mean, TimberDesign.C24.E05));
        Assert.Equal(1.0845, TimberDesign.DepthFactor(TimberDesign.C24, 100), 3);
        Assert.Equal(1.0, TimberDesign.DepthFactor(TimberDesign.C24, 195));
        Assert.Equal(1.3, TimberDesign.DepthFactor(TimberDesign.C24, 30));

        // Slenderness 100: λrel 1.70, k 2.08, kc 0.305.
        Assert.Equal(0.305, TimberDesign.Kc(TimberDesign.C24, 100), 3);
        Assert.Equal(1.0, TimberDesign.Kc(TimberDesign.C24, 15));

        // A 47 x 195 joist over 3.6 m at 1.2 kN/m: 6.53 N/mm² against 0.8 x 1.1 x 24 / 1.3, 16.25; shear 0.53 against 2.46.
        var joist = new MemberLengths(3600, 3600, 0, 0);
        var checks = TimberDesign.Eurocode(TimberDesign.C24, 195, 47, true, joist, new MemberForces(0, 0, 2.16, 0, 1.944, 0, false), 0.8).ToList();
        var bending = checks.Single(check => check.Name == "Bending, strong axis");
        Assert.Equal(6.53, bending.Demand, 2);
        Assert.Equal(16.25, bending.Capacity, 2);
        var shear = checks.Single(check => check.Name == "Shear along the depth");
        Assert.Equal(0.528, shear.Demand, 3);
        Assert.Equal(2.46, shear.Capacity, 2);

        // Not held, a deep thin beam loses a good part of its bending to buckling sideways: σm,crit 18.0, λrel,m 1.15, kcrit 0.694.
        var free = new MemberLengths(3600, 3600, 3600, 3600);
        var loose = TimberDesign.Eurocode(TimberDesign.C24, 195, 47, false, free, new MemberForces(0, 0, 0, 0, 1, 0, false), 0.8).Single(check => check.Name == "Bending, strong axis");
        Assert.InRange(loose.Capacity, 10.2, 10.3);

        // kmod by the shortest load.
        var (dead, floor, snow, wind) = (new LoadCase("G", "", LoadNature.Dead), new LoadCase("Q", "", LoadNature.Imposed), new LoadCase("S", "", LoadNature.Snow), new LoadCase("W", "", LoadNature.Wind));
        LoadCombination Of(params LoadCase[] cases) => new("U", "", LimitState.Ultimate, cases.Select(each => (each, 1.5)).ToList());
        Assert.Equal(0.6, TimberDesign.Kmod(Of(dead)));
        Assert.Equal(0.8, TimberDesign.Kmod(Of(dead, floor)));
        Assert.Equal(0.9, TimberDesign.Kmod(Of(dead, floor, snow)));
        Assert.Equal(1.1, TimberDesign.Kmod(Of(dead, floor, snow, wind)));
    }

    [Fact]
    public void TimberIsCheckedByTheNds()
    {
        // Table 4A's size factors, by actual sizes: a 2x10 and a 2x12, a 4x10.
        Assert.Equal((1.1, 1.1, 1.0), TimberDesign.SizeFactors(235, 38));
        Assert.Equal((1.0, 1.0, 1.0), TimberDesign.SizeFactors(286, 38));
        Assert.Equal((1.2, 1.1, 1.0), TimberDesign.SizeFactors(235, 89));

        // CP at FcE/Fc* of 1, c 0.8: 0.691; nothing lost when very stocky.
        Assert.Equal(0.691, TimberDesign.Stability(1, 0.8), 3);
        Assert.InRange(TimberDesign.Stability(1000, 0.95), 0.995, 1);

        // Beam stability's effective length, by Table 3.3.3.
        Assert.Equal(2.06 * 1000, TimberDesign.EffectiveBeamLength(1000, 235), 6);
        Assert.Equal(1.84 * 5000, TimberDesign.EffectiveBeamLength(5000, 235), 6);

        // A 2x10 joist, No. 2: Fb' = 900 x 1.1 x 1.15 x 2.54 x 0.85 x 0.8 = 1,966 psi, held by its deck.
        var joist = new MemberLengths(3600, 3600, 0, 0);
        var bending = TimberDesign.Nds(235, 38, false, true, false, joist, 3600, new MemberForces(0, 0, 0, 0, 2, 0, false), 0.8).Single(check => check.Name == "Bending, strong axis");
        Assert.Equal(1966, bending.Capacity, 0);

        // A post no more slender than le/d 50 (3.7.1.4): a 2x4 over 3 m is 79 across its thin way.
        var post = TimberDesign.Nds(89, 38, false, false, true, new MemberLengths(3000, 3000, 3000, 3000), 3000, new MemberForces(-1, 0, 0, 0, 0, 0, false), 0.8).Single(check => check.Name == "Slenderness");
        Assert.Equal(3000 / 38.0, post.Demand, 6);
        Assert.Equal(50, post.Capacity);

        // The time effect factor by the combination.
        var (dead, floor, wind) = (new LoadCase("D", "", LoadNature.Dead), new LoadCase("L", "", LoadNature.Imposed), new LoadCase("W", "", LoadNature.Wind));
        LoadCombination Of(params LoadCase[] cases) => new("U", "", LimitState.Ultimate, cases.Select(each => (each, 1.2)).ToList());
        Assert.Equal(0.6, TimberDesign.TimeEffect(Of(dead)));
        Assert.Equal(0.8, TimberDesign.TimeEffect(Of(dead, floor)));
        Assert.Equal(1.0, TimberDesign.TimeEffect(Of(dead, floor, wind)));
    }

    [Fact]
    public void ConcreteShearIsWorkedOut()
    {
        // 300 wide, d 500, C30/37: VRd,max at 45° = 300 x 450 x 0.528 x 20 / 2 = 713 kN; at 300 kN cot θ is held to 2.5, 613 mm²/m of links.
        var (crushing, needed, carried) = ConcreteDesign.ShearEurocode(300, 300, 500, 30, null);
        Assert.Equal(712.8, crushing, 1);
        Assert.Equal(613.4, needed, 0);
        Assert.Null(carried);

        // 10 mm links at 200, two legs: 785 mm²/m carry 785/1000 x 450 x 434.8 x 2.5 = 384 kN.
        Assert.Equal(384, ConcreteDesign.ShearEurocode(300, 300, 500, 30, 785.4).WithLinks!.Value, 0);

        // A little shear wants only the least links: 0.08 √fck / fyk of the width.
        Assert.Equal(0.08 * Math.Sqrt(30) / 500 * 300 * 1000, ConcreteDesign.ShearEurocode(10, 300, 500, 30, null).Needed, 6);

        // ACI: 4,000 psi, 12 x 20 in - φ(2 + 8) √f'c bd = 0.75 x 10 x 63.2 x 240 = 113.8 kips.
        var fc = 4000 * Psi;
        var aci = ConcreteDesign.ShearAci(100, 12 * Inch, 20 * Inch, fc, null);
        Assert.Equal(113.8 * 4.44822, aci.Crushing, 0);
        Assert.Equal(0.85, ConcreteDesign.Beta1(fc));
        Assert.Equal(0.80, ConcreteDesign.Beta1(5000 * Psi), 6);
    }

    [Fact]
    public void AConcreteColumnsInteractionIsWorkedOutByStrainCompatibility()
    {
        // 400 square, bars 50 in, 1,000 mm² in all, C30/37: by hand 188 kNm at 1,429 kN, 157 at 1,983 kN.
        var section = new ConcreteDesign.ColumnSection(400, 400, false, 50);
        Assert.InRange(ConcreteDesign.MomentEurocode(section, 1000, 1429e3, 30) / 1e6, 183, 193);
        Assert.InRange(ConcreteDesign.MomentEurocode(section, 1000, 1983e3, 30) / 1e6, 152, 162);

        // More than it can carry: nothing.
        Assert.Equal(0, ConcreteDesign.MomentEurocode(section, 1000, 6000e3, 30));

        // ACI, 16 in square, 4 in² in all, bars 2.5 in in, 4,000 psi: by hand φMn 151 kNm with no axial force.
        var aci = new ConcreteDesign.ColumnSection(406.4, 406.4, false, 63.5);
        Assert.InRange(ConcreteDesign.MomentAci(aci, 2580, 0, 4000 * Psi) / 1e6, 146, 156);

        // The steel it needs rises with the load, and a small column under too much is said to be short.
        var (light, lightSteel) = ConcreteDesign.ColumnEurocode(section, section, 1000, 30, 10, 3000, 3000, 30);
        var (heavy, heavySteel) = ConcreteDesign.ColumnEurocode(section, section, 2500, 150, 30, 3000, 3000, 30);
        Assert.True(heavySteel > lightSteel, $"{heavySteel:0} against {lightSteel:0}");
        Assert.True(light < 1 && heavy < 1, $"{light:0.00}, {heavy:0.00}");
        Assert.True(lightSteel >= 0.002 * section.Area - 1e-6);
        Assert.True(ConcreteDesign.ColumnEurocode(section, section, 8000, 400, 200, 3000, 3000, 30).Unity > 1);

        // A slender column asks more steel than a stocky one under the same load.
        var (_, stocky) = ConcreteDesign.ColumnEurocode(section, section, 2000, 100, 0, 3000, 3000, 30);
        var (_, slender) = ConcreteDesign.ColumnEurocode(section, section, 2000, 100, 0, 9000, 9000, 30);
        Assert.True(slender > stocky, $"{slender:0} against {stocky:0}");

        // ACI's bounds: 1% at the least.
        var (_, us) = ConcreteDesign.ColumnAci(aci, aci, 300, 30, 0, 3000, 3000, 4000 * Psi);
        Assert.Equal(0.01 * aci.Area, us, 3);
    }

    // ---- whole models ---------------------------------------------------------------------

    /// <summary>A 6 by 4 m frame of the columns and beams given, a beam across its middle, on pads fixed so it stands.</summary>
    private static (BimDocument Document, List<Beam> Beams) Frame(ColumnType columnType, BeamType beamType, ClearanceStandard? standard = null)
    {
        var document = BimDocument.CreateDefault();
        if (standard is { } code) document.ProjectInformation.ClearanceStandard = code;
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        document.AddType(columnType);
        document.AddType(beamType);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var columns = corners.Select(corner => new Column { TypeId = columnType.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        var beams = Enumerable.Range(0, 4).Select(i => new Beam { TypeId = beamType.Id, LevelId = first.Id, Start = corners[i], End = corners[(i + 1) % 4] }).ToList();
        beams.Add(new Beam { TypeId = beamType.Id, LevelId = first.Id, Start = new Point2D(3000, 0), End = new Point2D(3000, 4000) });
        foreach (var beam in beams) document.Add(beam);
        foreach (var column in columns)
        {
            var pad = Footings.Fit(document, column);
            if (pad is PadFooting held) held.Fixity = SupportFixity.Fixed;
            document.Add(pad);
        }

        return (document, beams);
    }

    private static (StructuralAnalysis Analysis, IReadOnlyList<MemberCheck> Checks) Checked(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        Assert.Empty(analysis.Problems);
        return (analysis, MemberStrength.Of(document, analysis));
    }

    private static ColumnType SteelColumn(BimDocument? _ = null) =>
        new("203 UC 46", ColumnShape.IBeam, 203.6, 203.2) { Structural = true, StructuralMaterial = BeamMaterial.Steel, FlangeThickness = 11.0, WebThickness = 7.2 };

    private static BeamType SteelBeam() =>
        new("UB 254") { Section = BeamSection.IBeam, Material = BeamMaterial.Steel, Depth = 251.4, Width = 146.1, FlangeThickness = 8.6, WebThickness = 6.0 };

    [Fact]
    public void WhatHoldsAMemberSetsWhatItBucklesOver()
    {
        var (document, beams) = Frame(SteelColumn(), SteelBeam());
        var model = AnalyticalModel.Of(document);

        // The beam across frames into the middle of the long sides: held sideways there, not up and down.
        var south = model.Members.Single(member => member.Element == beams[0]);
        var lengths = MemberStrength.LengthsOf(model, south);
        Assert.Equal(6000, lengths.Strong, 0);
        Assert.Equal(3000, lengths.Weak, 0);
        Assert.Equal(3000, lengths.Lateral, 0);

        // A column runs its storey each way.
        var column = model.Members.First(member => member.Kind == AnalyticalMemberKind.Column);
        var storey = MemberStrength.LengthsOf(model, column);
        Assert.Equal(column.Length, storey.Strong, 0);
        Assert.Equal(column.Length, storey.Weak, 0);
    }

    [Fact]
    public void ASteelFrameIsCheckedByEitherCode()
    {
        var (document, _) = Frame(SteelColumn(), SteelBeam());
        var (analysis, checks) = Checked(document);
        Assert.Equal(analysis.Model.Members.Count, checks.Count);
        foreach (var check in checks)
        {
            Assert.Contains("BS EN 1993-1-1", check.By);
            Assert.Equal("S355", check.Grade);
            Assert.NotEmpty(check.Checks);
            Assert.All(check.Checks, each => Assert.False(double.IsNaN(each.Ratio), each.Name));
            Assert.All(check.Checks, each => Assert.StartsWith("ULS", each.Under));
        }

        // A light frame with only its own weight and the wind to carry: well within what it can.
        Assert.All(checks, check => Assert.True(check.Enough, $"{check.Member.Name}: {check.Worst?.Name} {check.Ratio:P0}"));
        var column = checks.First(check => check.Member.Kind == AnalyticalMemberKind.Column);
        Assert.Contains(column.Checks, each => each.Name == "Buckling");

        var (usDocument, usBeams) = Frame(SteelColumn(), SteelBeam(), ClearanceStandard.Irc);
        var (_, usChecks) = Checked(usDocument);
        Assert.All(usChecks, check => Assert.Equal("AISC 360-16, LRFD", check.By));
        Assert.All(usChecks, check => Assert.Equal("A992", check.Grade));
        Assert.Contains(usChecks.First(check => check.Member.Kind == AnalyticalMemberKind.Column).Checks, each => each.Clause.StartsWith("H1-1", StringComparison.Ordinal));

        // Cb from the moments only over a length between braces that is the whole member: 1.0 for the side braced at its middle.
        string Note(Beam beam) => usChecks.Single(check => check.Member.Element == beam).Checks.Single(each => each.Name == "Bending, strong axis").Note;
        Assert.Contains("Cb 1.00", Note(usBeams[0]));
        Assert.DoesNotContain("Cb 1.00", Note(usBeams[4]));
    }

    [Fact]
    public void ATimberFrameIsCheckedAndAConcreteOneDesigned()
    {
        var post = new ColumnType("Post", ColumnShape.Rectangular, 150, 150) { Structural = true, StructuralMaterial = BeamMaterial.Timber };
        var timber = new BeamType("Beam") { Material = BeamMaterial.Timber, Depth = 225, Width = 75 };
        var (wood, _) = Frame(post, timber);
        var (_, woodChecks) = Checked(wood);
        Assert.All(woodChecks, check => Assert.Equal("C24", check.Grade));
        Assert.All(woodChecks, check => Assert.Contains("BS EN 1995-1-1", check.By));
        Assert.Contains(woodChecks.First(check => check.Member.Kind == AnalyticalMemberKind.Column).Checks, each => each.Name == "Compression with buckling");

        var (usWood, _) = Frame(new ColumnType("Post", ColumnShape.Rectangular, 140, 140) { Structural = true, StructuralMaterial = BeamMaterial.Timber }, new BeamType("Beam") { Material = BeamMaterial.Timber, Depth = 235, Width = 89 }, ClearanceStandard.Irc);
        var (_, usChecks) = Checked(usWood);
        Assert.All(usChecks, check => Assert.Contains("NDS", check.By));
        Assert.All(usChecks, check => Assert.NotEmpty(check.Checks));

        var pier = new ColumnType("Column", ColumnShape.Rectangular, 300, 300) { Structural = true, StructuralMaterial = BeamMaterial.Concrete };
        var deep = new BeamType("Beam") { Material = BeamMaterial.Concrete, Depth = 450, Width = 300 };
        var (concrete, _) = Frame(pier, deep);
        var (_, concreteChecks) = Checked(concrete);
        var beam = concreteChecks.First(check => check.Member.Kind == AnalyticalMemberKind.Beam);
        Assert.Equal("C30/37", beam.Grade);
        Assert.Contains(beam.Checks, each => each.Name == "Shear" && each.Note.Contains("links", StringComparison.Ordinal));
        Assert.Contains(beam.Checks, each => each.Name.StartsWith("Bending", StringComparison.Ordinal) && each.Note.Contains("mm²", StringComparison.Ordinal));
        var column = concreteChecks.First(check => check.Member.Kind == AnalyticalMemberKind.Column);
        Assert.Contains(column.Checks, each => each.Name == "Axial and bending, at the most steel" && each.Note.Contains("%", StringComparison.Ordinal));
        Assert.All(concreteChecks, check => Assert.True(check.Enough, $"{check.Member.Name}: {check.Worst?.Name} {check.Ratio:P0}"));
    }

    [Fact]
    public void AGroundBeamIsCheckedAgainstTheBarsAndLinksItHas()
    {
        var (document, _) = EaveTests.House(hip: false);
        document.ProjectInformation.Ground = GroundType.FirmClay;
        var walls = document.Walls.ToList();
        foreach (var wall in walls) wall.StructuralUsage = StructuralUsage.Bearing;
        var beams = new List<GroundBeam>();
        foreach (var wall in walls)
        {
            var (beam, piles) = Piles.Fit(document, wall, 300, 3000);
            document.Add(beam);
            foreach (var pile in piles) document.Add(pile);
            beams.Add(beam);
        }

        var (_, checks) = Checked(document);
        Assert.Equal(beams.Count, checks.Count);
        foreach (var check in checks)
        {
            Assert.Equal("RC28/35", check.Grade);
            Assert.Contains(check.Checks, each => each.Name == "Shear");
            Assert.True(check.Enough, $"{check.Member.Name}: {check.Worst?.Name} {check.Ratio:P0}");
        }

        // With no links it has nothing to carry the shear.
        foreach (var beam in beams) beam.Links = string.Empty;
        var (_, unlinked) = Checked(document);
        Assert.All(unlinked, check => Assert.False(check.Enough));
    }
}
