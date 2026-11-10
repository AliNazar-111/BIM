using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Holding-down bolts and US base plates at steel column bases, timber joists notched at their bearings, and torsion in timber and steel hollow sections.</summary>
public class BasesNotchesAndTorsionTests
{
    private static MemberForces Forces(double vz = 0, double t = 0, double n = 0) => new(n, 0, vz, t, 0, 0, false);

    [Fact]
    public void HoldingDownBoltsAreCheckedByBsEn19931_8AndBsEn19924()
    {
        // 100 kN up and 30 along on four M20 grade 4.6 bolts at 200, far from an edge, cast 300 into C28/35. Each bolt 0.9 x 400 x 245 / 1.25 = 70.6 kN in tension; the cone 8.9 √28 300^1.5 by
        // (200 + 900)² / 900², over 1.5; in shear (0.44 - 0.072) x 400 x 245 / 1.25 = 28.8 kN each and no friction when it lifts; and the two together by Table 3.4.
        var (checks, _) = JointDesign.HoldingDownEurocode(100, 30, 0, 200, 1e9, 600, 28);
        StrengthCheck Named(string name) => checks.Single(check => check.Name == name);
        Assert.Equal(4 * 0.9 * 400 * 245 / 1.25 / 1000, Named("Bolts in tension").Capacity, 6);
        Assert.Equal(8.9 * Math.Sqrt(28) * Math.Pow(300, 1.5) / 1000 * 1100.0 * 1100 / (900 * 900) / 1.5, Named("Concrete cone").Capacity, 6);
        var each = (0.44 - 0.0003 * 240) * 400 * 245 / 1.25 / 1000;
        Assert.Equal(4 * each, Named("Shear at the base").Capacity, 6);
        Assert.Equal(30.0 / 4 / each + 100.0 / 4 / (1.4 * 0.9 * 400 * 245 / 1.25 / 1000), Named("Bolts in shear and tension").Demand, 6);

        // Pressed down by 200 kN, friction of 0.2 of it takes the shear first; near an edge the cone is smaller.
        var (pressed, _) = JointDesign.HoldingDownEurocode(0, 30, 200, 200, 1e9, 600, 28);
        Assert.Equal(40 + 4 * each, pressed.Single(check => check.Name == "Shear at the base").Capacity, 6);
        var (edged, _) = JointDesign.HoldingDownEurocode(100, 0, 0, 200, 150, 600, 28);
        Assert.True(edged.Single(check => check.Name == "Concrete cone").Capacity < Named("Concrete cone").Capacity * 0.6);
    }

    [Fact]
    public void AnchorRodsAreCheckedByAci318Chapter17()
    {
        // Four 3/4 in F1554 grade 36 rods, 215.5 mm² at 400 N/mm², 12 in into 4,000 psi concrete: φ 0.75 Ase futa a rod; breakout 0.70 x 10 √f'c hef^1.5 by its projected area; in shear 0.65 x 0.6
        // Ase futa x 0.8 for a grout pad.
        var (checks, _) = JointDesign.HoldingDownAci(100, 30, 200, 1e9, 600, 27.6);
        StrengthCheck Named(string name) => checks.Single(check => check.Name == name);
        Assert.Equal(0.75 * 4 * 215.5 * 400 / 1000, Named("Anchor rods in tension").Capacity, 6);
        var reach = 1.5 * 305;
        Assert.Equal(0.70 * 10 * Math.Sqrt(27.6) * Math.Pow(305, 1.5) / 1000 * Math.Pow(200 + 2 * reach, 2) / Math.Pow(2 * reach, 2), Named("Concrete breakout").Capacity, 6);
        Assert.Equal(0.65 * 4 * 0.6 * 215.5 * 400 * 0.8 / 1000, Named("Anchor rods in shear").Capacity, 6);
    }

    [Fact]
    public void AUsBasePlateIsByDesignGuide1()
    {
        // A W10x49, d 253 and bf 254: a plate 6 in more each way, 16 by 16 in; bearing 0.65 x 0.85 f'c A1; the thickness l √(2 Pu / 0.9 Fy B N), l the most of m, n and √(d bf)/4.
        var column = new SteelSection("I Section", 253, 254, 14.2, 8.6, 9290, 113e6, 38.9e6, 893e3, 306e3, 991e3, 466e3, 0.5e6, 0.0e12, 0);
        var (check, wants) = JointDesign.BasePlateAisc(1500, column, 27.6);
        var side = 16 * 25.4;
        Assert.Equal(0.65 * 0.85 * 27.6 * side * side / 1000, check.Capacity, 6);
        var l = Math.Max((side - 0.95 * 253) / 2, Math.Max((side - 0.8 * 254) / 2, Math.Sqrt(253 * 254) / 4));
        var need = l * Math.Sqrt(2 * 1500e3 / (0.9 * 250 * side * side));
        Assert.InRange(need, 25.4, 31.75);
        Assert.Contains("16 by 16 by 1.25 in", wants);
    }

    [Fact]
    public void ANotchedJoistIsCheckedByBsEn19951_1AndTheNds()
    {
        // 47 x 200 C24 notched 50 deep 100 from its bearing: hef 150, α 0.75, kv = 5 / (√200 (√(α(1-α)) + 0.8 x/h √(1/α - α²))) = 0.45; τ = 1.5 V / (kcr b hef) against kv fv,d.
        var lengths = new MemberLengths(4000, 4000, 0, 0);
        var checks = TimberDesign.Eurocode(TimberDesign.C24, 200, 47, true, lengths, Forces(vz: 5), 0.8, 50, 100).ToList();
        var notched = checks.Single(check => check.Name == "Shear at the notch");
        var alpha = 0.75;
        var kv = 5 / (Math.Sqrt(200) * (Math.Sqrt(alpha * (1 - alpha)) + 0.8 * 100 / 200 * Math.Sqrt(1 / alpha - alpha * alpha)));
        Assert.InRange(kv, 0.44, 0.46);
        Assert.Equal(1.5 * 5000 / (0.67 * 47 * 150), notched.Demand, 6);
        Assert.Equal(kv * 0.8 * 4.0 / 1.3, notched.Capacity, 6);
        Assert.DoesNotContain(TimberDesign.Eurocode(TimberDesign.C24, 200, 47, true, lengths, Forces(vz: 5), 0.8), check => check.Name == "Shear at the notch");

        // NDS 3.4.3.2(a): V'r = 2/3 F'v b dn (dn/d)², and 4.4.3.2's d/4 for an end notch.
        var nds = TimberDesign.Nds(200, 47, false, true, false, lengths, 4000, Forces(vz: 5), 0.8, 50).ToList();
        var plain = nds.Single(check => check.Name == "Shear along the depth");
        var us = nds.Single(check => check.Name == "Shear at the notch");
        Assert.Equal(plain.Capacity, us.Capacity, 9);
        Assert.Equal(plain.Demand * 200 / 150 * Math.Pow(200.0 / 150, 2), us.Demand, 6);
        Assert.Equal(50, nds.Single(check => check.Name == "Notch depth").Ratio * 50, 6);
    }

    [Fact]
    public void TimberAndSteelTubesAreCheckedInTorsion()
    {
        // Timber, 6.1.8: 0.1 kNm on a 47 x 200, τ = T (3 + 1.8 b/h) / b² h, against kshape fv,d, kshape 1 + 0.15 h/b.
        var lengths = new MemberLengths(3000, 3000, 0, 0);
        var timber = TimberDesign.Eurocode(TimberDesign.C24, 200, 47, false, lengths, Forces(t: 0.1), 0.8).Single(check => check.Name == "Torsion");
        Assert.Equal(0.1e6 * (3 + 1.8 * 47 / 200) / (47.0 * 47 * 200), timber.Demand, 6);
        Assert.Equal((1 + 0.15 * 200 / 47) * 0.8 * 4.0 / 1.3, timber.Capacity, 6);

        // A 100 x 100 x 5 box, Bredt: T Rd = 2 Am t fy / √3, Am = 95², 18.5 kNm in S355, half of it taken - and its shear resistance half by that, (6.28).
        var box = new SteelSection("Tube", 100, 100, 5, 5, 1870, 2.79e6, 2.79e6, 55.9e3, 55.9e3, 65.9e3, 65.9e3, 4.39e6, 0);
        var steel = SteelDesign.Eurocode(box, lengths, new MemberForces(0, 0, 10, 9.25, 0, 0, false)).ToList();
        var torsion = steel.Single(check => check.Name == "Torsion");
        Assert.Equal(2 * 95.0 * 95 * 5 * 355 / Math.Sqrt(3) / 1e6, torsion.Capacity, 6);
        Assert.InRange(torsion.Ratio, 0.49, 0.51);
        Assert.InRange(steel.Single(check => check.Name == "Shear with torsion").Capacity, 0.49, 0.51);

        // AISC H3.1: φ 0.9 x 0.6 Fy C, Fy 50 ksi, C = 2(B - t)(H - t)t - 4.5(4 - π)t³.
        var aisc = SteelDesign.Aisc(box, lengths, new MemberForces(0, 0, 10, 3.7, 0, 0, false), 1).Single(check => check.Name == "Torsion");
        Assert.Equal(0.9 * 0.6 * 50 * 6.894757 * (2 * 95.0 * 95 * 5 - 4.5 * (4 - Math.PI) * 125) / 1e6, aisc.Capacity, 2);

        // An I section is not: its torsion is mostly warping.
        Assert.DoesNotContain(SteelDesign.Eurocode(new SteelSection("I Section", 251.4, 146.1, 8.6, 6.0, 3960, 4410e4, 448e4, 353e3, 61.3e3, 393e3, 94.1e3, 8.55e4, 0.0659e12, 7.6), lengths,
            new MemberForces(0, 0, 10, 1, 0, 0, false)), check => check.Name == "Torsion");
    }

    [Fact]
    public void ADrawnJoistsNotchIsKeptAndChecked()
    {
        // A joist type notched 50 at its ends 100 in: saved and opened again, and its joists' checks have the notch in them.
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = ground, TopLevelId = first, StructuralUsage = StructuralUsage.Bearing }).ToList();
        foreach (var wall in walls) document.Add(wall);
        foreach (var wall in walls) document.Add(Footings.Fit(document, wall));
        var joist = new BeamType("Timber 47 x 200 notched") { Material = BeamMaterial.Timber, Width = 47, Depth = 200, EndNotch = 50, EndNotchLength = 100 };
        document.AddType(joist);
        for (var x = 200; x <= 5800; x += 400)
            document.Add(new Beam { TypeId = joist.Id, LevelId = first, Start = new Point2D(x, 0), End = new Point2D(x, 4000), Usage = BeamUsage.Joist });
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)).Id, LevelId = first, SpanDirection = 0 };
        floor.SetBoundary(corners);
        document.Add(floor);

        var path = Path.Combine(Path.GetTempPath(), $"notched-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path).FindType<BeamType>(joist.Id)!;
            Assert.Equal(50, opened.EndNotch);
            Assert.Equal(100, opened.EndNotchLength);
        }
        finally
        {
            File.Delete(path);
        }

        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var checks = MemberStrength.Of(document, analysis, SecondOrderAnalysis.Run(document, analysis));
        var notched = checks.Where(check => check.Member.Kind == AnalyticalMemberKind.Joist).ToList();
        Assert.NotEmpty(notched);
        Assert.All(notched, check => Assert.Contains(check.Checks, each => each.Name == "Shear at the notch" && each.Ratio > 0));
        Assert.Contains(joist.GetTypeParameters(document), parameter => parameter.Definition.Name == "End Notch Depth");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASteelFramesColumnBasesHaveTheirHoldingDownBolts(bool us)
    {
        // The steel frame leans as every frame is taken to, and its column bases take that along: friction and the bolts in the UK, the rods in shear in the US; a US base plate by Design Guide 1.
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var columnType = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var beamType = document.TypesOf<BeamType>().First(type => type.Section == BeamSection.IBeam);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var columns = corners.Select(corner => new Column { TypeId = columnType.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        foreach (var i in Enumerable.Range(0, 4)) document.Add(new Beam { TypeId = beamType.Id, LevelId = first.Id, Start = corners[i], End = corners[(i + 1) % 4] });
        foreach (var column in columns) document.Add(Footings.Fit(document, column));
        var model = AnalyticalModel.Of(document);
        var second = SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model)));
        var joints = JointDesign.Of(document, second);
        var bolts = joints.Where(joint => joint.Kind == "Holding-down bolts").ToList();
        Assert.NotEmpty(bolts);
        Assert.All(bolts, joint => Assert.Contains(joint.Checks, check => check.Name == (us ? "Anchor rods in shear" : "Shear at the base")));
        var plates = joints.Where(joint => joint.Kind == "Base plate").ToList();
        Assert.Equal(4, plates.Count);
        Assert.All(plates, joint => Assert.Contains(joint.Checks, check => check.Clause.Contains(us ? "Design Guide 1" : "6.2.5", StringComparison.Ordinal)));
    }
}
