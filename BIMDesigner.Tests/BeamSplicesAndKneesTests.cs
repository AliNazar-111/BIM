using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>Bolted cover plate beam splices by SCI P398 Section 4, and knees - beams fixed on a column's top - by its STEP 8.</summary>
public class BeamSplicesAndKneesTests
{
    // P398 Example D.1: two 457 x 191 x 67 UKB in S275 spliced with 180 x 12 flange cover plates, three rows of two preloaded M20 8.8 each side, and two 340 x 10 web plates with two lines of
    // three each side; 200 kNm, 150 kN of shear and 150 kN of compression at the ultimate, 133, 100 and 100 in service.
    private static readonly SteelSection Ukb457 = new("I Section", 453.4, 189.9, 12.7, 8.5, 8550, 29400e4, 1450e4, 1300e3, 153e3, 1470e3, 237e3, 37.1e4, 0.705e12, 10.2);
    private static readonly SpliceLayout D1 = new(20, 12, 180, 3, 10, 340, 3, 2);

    [Fact]
    public void ASpliceComesToP398sExampleD1()
    {
        Assert.Equal((60, 80, 30, 120, 50, 70, 85), (D1.End, D1.Pitch, D1.Edge, D1.WebPitch, D1.WebEdge, D1.WebFirst, D1.WebGauge));
        var checks = BeamSplices.Checks(Ukb457, 275, 410, D1, new[] { new SpliceForces("ULS", 200, 150, 150), new SpliceForces("SLS", 133, 100, 100, Service: true) });
        StrengthCheck Named(string name) => checks.Single(check => check.Name == name);

        // The web carries 18.9% of the moment and 42.6% of the axial force: each flange 0.811 x 200 / 0.4407 = 368 kN, the compression flange 368 + 43 = 411, the tension one 325. Six bolts
        // in single shear, 6 x 94.1 = 565 kN.
        Assert.InRange(Named("Flange bolts").Demand, 410, 412);
        Assert.Equal(6 * 0.6 * 800 * 245 / 1.25 / 1000, Named("Flange bolts").Capacity, 6);
        Assert.InRange(Named("Tension flange cover plate").Demand, 324, 326);

        // The cover plate in tension, its net section 0.9 x 1632 x 410 / 1.1 = 547 kN. P398 has its block tearing at 1020 kN with four rows a side where the plate has three; with three,
        // 410 x 456 / 1.1 + 275 x 3960 / √3 = 799, still not the least.
        Assert.Equal(0.9 * (180 - 44) * 12 * 410 / 1.1 / 1000, Named("Tension flange cover plate").Capacity, 6);

        // In compression, 120 across the joint over 12 is past 9 ε, so it buckles over 0.6 x 120: χ 0.98, 582 kN.
        Assert.InRange(Named("Compression flange cover plate").Capacity, 580, 584);

        // The web bolts: the most loaded takes the shear, the axial force and the web's moment with the shear's eccentricity, √((25 + 33.8)² + (95.4 + 10.7)²) = 121 kN, against the
        // bearing on the 8.5 mm web, 2.5 αb fu d t / γM2 with αb 65 / 66.
        Assert.InRange(Named("Web bolts").Demand, 120, 123);
        Assert.Equal(2.5 * (65.0 / 66) * 410 * 20 * 8.5 / 1.25 / 1000, Named("Web bolts").Capacity, 6);

        // In service each bolt's slip, 0.5 x 0.7 x 800 x 245 / 1.1 = 62.4 kN a surface: the flange's 274 kN on six, the web's most loaded 81 kN on two surfaces.
        Assert.InRange(Named("Flange bolts slipping").Demand, 273, 275);
        Assert.Equal(6 * 0.5 * 0.7 * 800 * 245 / 1.1 / 1000, Named("Flange bolts slipping").Capacity, 6);
        Assert.InRange(Named("Web bolts slipping").Demand, 80, 82);

        // The web plates: gross shear 2 x 340 x 10 x 275 / (1.27 √3) = 850 kN the least; bending and axial 63.9 / 1870 + 54.4 / 106 = 0.55.
        Assert.InRange(Named("Web cover plates in shear").Capacity, 849, 851);
        Assert.InRange(Named("Web cover plates, bending and axial").Demand, 0.54, 0.56);
        Assert.All(checks, check => Assert.True(check.Ratio <= 1, check.Name));
    }

    [Fact]
    public void AHaunchLengthensTheLeverAndItsFlangeTakesTheCompression()
    {
        // A flush plate on a 254 x 146 x 31 UKB with a haunch as deep as the beam, at a UKC 203's top with its cap plate: the rows' levers run from the haunch flange's centre, and the
        // compression there is the haunch flange's 1.4 Afb fy.
        var beam = new SteelSection("I Section", 251.4, 146.1, 8.6, 6.0, 3970, 4410e4, 448e4, 351e3, 61.3e3, 393e3, 94.1e3, 8.55e4, 0.066e12, 7.6);
        var column = new SteelSection("I Section", 203.6, 203.6, 11.0, 7.2, 5870, 4570e4, 1550e4, 450e3, 152e3, 497e3, 231e3, 22.2e4, 0.143e12, 10.2);
        var flush = new EndPlateLayout(20, 20, 200, 90, 3, AboveFlange: 0, Extension: 0, TensionWeld: 8, WebWeld: 6);
        var plain = MomentJoints.Eurocode(beam, 355, column, 355, flush, beta: 1, stiffening: new ColumnStiffening(Tension: true));
        var haunched = MomentJoints.Eurocode(beam, 355, column, 355, flush with { Haunch = 250 }, beta: 1, stiffening: new ColumnStiffening(true, true));
        Assert.Equal(251.4 - 4.3 - 60, plain.Rows[0].Lever, 6);
        Assert.Equal(251.4 + 250 - 4.3 - 60, haunched.Rows[0].Lever, 6);
        Assert.True(haunched.Moment > plain.Moment);
        Assert.True(haunched.Compression <= 1.4 * 146.1 * 8.6 * 355 / 1000 + 1e-6);
        Assert.Equal(251.4 + 250 + 8 + 20, (flush with { Haunch = 250 }).Depth(251.4), 6);
    }

    [Fact]
    public void TwoBeamsFixedTogetherInLineAreSpliced()
    {
        // One steel beam across the house drawn as two, fixed to each other at 5 m: a splice there, at least a quarter of the beam's moment resistance.
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        var type = document.TypesOf<BeamType>().First(each => each.Name.StartsWith("UB 254", StringComparison.Ordinal));
        document.Add(new Beam { TypeId = type.Id, LevelId = document.Levels[1].Id, Start = new Point2D(0, 4000), End = new Point2D(5000, 4000), EndRelease = MemberRelease.Fixed });
        document.Add(new Beam { TypeId = type.Id, LevelId = document.Levels[1].Id, Start = new Point2D(5000, 4000), End = new Point2D(10000, 4000), StartRelease = MemberRelease.Fixed });
        var model = AnalyticalModel.Of(document);
        var joints = JointDesign.Of(document, SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model))));
        var splice = joints.Single(joint => joint.Kind == "Beam splice");
        Assert.True(splice.Wants.Contains("flange cover plates", StringComparison.Ordinal), $"{splice.Wants} | {splice.Limits} | {string.Join("; ", splice.Checks.Select(c => $"{c.Name} {c.Demand:0.0}/{c.Capacity:0.0} {c.Under}"))}");
        Assert.Contains("preloaded M20 8.8", splice.Wants);
        Assert.Contains(splice.Checks, check => check.Name == "Flange bolts" && check.Demand > 0);
        Assert.Contains(splice.Checks, check => check.Name == "Web bolts slipping");
        Assert.Contains("a quarter", splice.Limits);
        Assert.True(splice.Enough, $"{splice.Ratio:P0}");
    }

    private static IReadOnlyList<JointCheck> Portal(bool us)
    {
        // A storey of steel frame on four UC 203s, its beams fixed on the columns' tops, a concrete roof: knees.
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var post = document.TypesOf<ColumnType>().First(type => type.Name == "UC 203 x 203 x 46");
        var ub = document.TypesOf<BeamType>().First(type => type.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var slab = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 6000), new Point2D(0, 6000) };
        var columns = corners.Select(corner => new Column { TypeId = post.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        foreach (var i in Enumerable.Range(0, 4))
            document.Add(new Beam { TypeId = ub.Id, LevelId = first.Id, Start = corners[i], End = corners[(i + 1) % 4], StartRelease = MemberRelease.Fixed, EndRelease = MemberRelease.Fixed });
        var floor = new Floor { TypeId = slab.Id, LevelId = first.Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        foreach (var column in columns) document.Add(Footings.Fit(document, column));
        var model = AnalyticalModel.Of(document);
        return JointDesign.Of(document, SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model))));
    }

    [Fact]
    public void ABeamFixedOnAColumnsTopIsAKnee()
    {
        // The beams along y meet the columns' flanges, four ends: knees, a flush end plate and a cap plate. Those along x meet the webs, on the posts' tops, and say the knee is not worked out.
        var joints = Portal(us: false);
        var knees = joints.Where(joint => joint.Kind == "Knee").ToList();
        Assert.Equal(4, knees.Count);
        Assert.All(knees, joint =>
        {
            Assert.Contains("flush end plate", joint.Wants);
            Assert.Contains("cap plate", joint.Wants);
            Assert.Contains("A knee", joint.Limits);
            Assert.Contains(joint.Checks, check => check.Name == "Moment at the joint" && check.Demand > 0);
        });
        Assert.Contains(joints, joint => joint.Kind == "Beam on a post" && joint.Limits.Contains("knee", StringComparison.Ordinal));

        var us = Portal(us: true).Where(joint => joint.Kind == "Knee").ToList();
        Assert.Equal(4, us.Count);
        Assert.All(us, joint => Assert.Contains("not worked out", joint.Limits));
    }
}
