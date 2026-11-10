using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>Columns strengthened at moment joints by SCI P398 STEP 6, and moment-resisting column bases by its Section 5 and AISC Design Guide 1.</summary>
public class ColumnStiffeningAndBasesTests
{
    private static readonly SteelSection Ukb533 = new("I Section", 533.1, 209.3, 15.6, 10.1, 11700, 55200e4, 2390e4, 2070e3, 228e3, 2360e3, 355e3, 75.7e4, 1.6e12, 12.7);
    private static readonly SteelSection Ukc254 = new("I Section", 266.7, 258.8, 20.5, 12.8, 13600, 17500e4, 5930e4, 1310e3, 458e3, 1480e3, 697e3, 172e4, 0.898e12, 12.7);
    private static readonly SteelSection Ukc305 = new("I Section", 314.5, 307.4, 18.7, 12.0, 15000, 27700e4, 9060e4, 1760e3, 589e3, 1960e3, 895e3, 160e4, 1.98e12, 15.2);
    private static readonly EndPlateLayout C1 = new(24, 25, 250, 100, 2, AboveFlange: 40, Extension: 50, FirstBelow: 60, Pitch: 90, TensionWeld: 12, CompressionWeld: 8, WebWeld: 8);

    [Fact]
    public void CompressionStiffenersLetEveryRowCarryAsP398sExampleC2()
    {
        // Example C.2: Example C.1 with full depth compression stiffeners. Its rows, 377 + 320 + 291 = 988 kN, no longer cut by the web's 841: Mj,Rd = 565 x 377 + 465 x 320 + 375 x 291 = 471 kNm.
        var joint = MomentJoints.Eurocode(Ukb533, 275, Ukc254, 265, C1, beta: 0, stiffening: new ColumnStiffening(Compression: true));
        Assert.InRange(joint.Moment, 470, 473);
        Assert.InRange(joint.Rows[2].Force, 289, 293);
        // The web stiffened, the beam's flange limits the compression zone, Mc,Rd / (h - tf) = 649 / 0.5175 = 1254 kN as P398 has it; the stiffeners' own cross-section, on their net width with
        // the 248 mm of web the force spreads into, is more.
        Assert.Contains("beam flange", joint.CompressionBy);
        Assert.Equal(2360e3 * 275 / (533.1 - 15.6) / 1000, joint.Compression, 6);
        var outstand = (258.8 - 12.8) / 2;
        Assert.Equal(20, joint.StiffenerThickness);
        Assert.True((2 * (outstand - 15) * 20 + 247.6 * 12.8) * 265 / 1000 > joint.Compression);
    }

    [Fact]
    public void AWebPlateAddsAWebsShearAreaAndTensionStiffenersHoldTheFlange()
    {
        // One-sided, the web panel limits it, 519 kN; a supplementary web plate adds its depth between root radii times the web, 200.3 x 12.8, to the shear area.
        var plain = MomentJoints.Eurocode(Ukb533, 275, Ukc254, 265, C1, beta: 1);
        var plated = MomentJoints.Eurocode(Ukb533, 275, Ukc254, 265, C1, beta: 1, stiffening: new ColumnStiffening(WebPlates: 1));
        var avc = MomentJoints.ShearArea(Ukc254);
        Assert.Equal(0.9 * 265 * (avc + (266.7 - 2 * (20.5 + 12.7)) * 12.8) / Math.Sqrt(3) / 1000, plated.PanelShear, 6);
        Assert.True(plated.Moment > plain.Moment);
        Assert.True(plated.WebPlateThickness >= 12.8);

        // Tension stiffeners at the tension flange: the rows either side of them are next to a stiffener, α m on the column flange, and no group crosses them, so the column's rows carry more.
        var stiffened = MomentJoints.Eurocode(Ukb533, 275, Ukc254, 265, C1, beta: 0, stiffening: new ColumnStiffening(true, true));
        var compressed = MomentJoints.Eurocode(Ukb533, 275, Ukc254, 265, C1, beta: 0, stiffening: new ColumnStiffening(Compression: true));
        Assert.True(stiffened.Moment >= compressed.Moment - 0.5);
        Assert.True(stiffened.TensionStiffenerForce > 0);
        Assert.True(stiffened.Stiffness > compressed.Stiffness);
    }

    [Fact]
    public void BackingPlatesStrengthenAThinColumnFlangeInMode1()
    {
        // A UC 152 x 152 x 23's 6.8 mm flange under M20 rows: Mode 1 of the column flange governs the extension's row, and backing plates as thick add 4 n Mbp to it.
        var column = new SteelSection("I Section", 152.4, 152.2, 6.8, 5.8, 2920, 1250e4, 400e4, 164e3, 52.6e3, 184e3, 80.1e3, 4.63e4, 0.0212e12, 7.6);
        var beam = new SteelSection("I Section", 254.1, 146.1, 8.6, 6.0, 3970, 4410e4, 448e3, 351e3, 61.3e3, 393e3, 94.1e3, 8.55e4, 0.066e12, 7.6);
        var plate = new EndPlateLayout(20, 15, 200, 90, 1, TensionWeld: 8, WebWeld: 6);
        var thin = MomentJoints.Eurocode(beam, 355, column, 355, plate, beta: 1);
        var backed = MomentJoints.Eurocode(beam, 355, column, 355, plate, beta: 1, stiffening: new ColumnStiffening(Backing: 10));
        Assert.Contains("column flange (mode 1)", thin.Rows[0].By);
        Assert.True(backed.Rows[0].Force > thin.Rows[0].Force);
    }

    [Fact]
    public void ABasePlateComesToP398sExampleE1()
    {
        // A 305 x 305 x 118 UKC in S355 on a 600 x 600 x 50 S275 plate, four M24 8.8 a side at 450 centres, C30/37, fjd 17. The T-stubs' forces by statics, zt 225 and zc 148: under 2000 kN
        // and 350 kNm, 144 kN on the bolts and 2144 under the flange; under 350 kN and 350 kNm, 799 and 1149.
        var (t1, c1) = ColumnBases.Forces(-2000, 350, 225, (314.5 - 18.7) / 2);
        Assert.InRange(t1, 143, 146);
        Assert.InRange(c1, 2143, 2147);
        var (t2, c2) = ColumnBases.Forces(-350, 350, 225, (314.5 - 18.7) / 2);
        Assert.InRange(t2, 798, 801);
        Assert.InRange(c2, 1148, 1151);

        // c = 50 √(255 / 3 x 17) = 112, the concrete 17 x (2 x 112 + 18.7) x (2 x 112 + 307.4) = 2193 kN, the flange Mc,Rd / (h - tf) = 2282: 2193 governs. In tension the bolts, 4 x 203 = 812,
        // before the plate's 2 Mpl / mx = 1593 with leff the half width, 300.
        var plate = new BaseLayout(24, 4, 50, 314.5 / 2 > 0 ? 225 - 314.5 / 2 : 0, 75, 600, Weld: (225 - 314.5 / 2 - 60) / 0.8);
        Assert.Equal(600, plate.Depth(Ukc305), 6);
        var resisted = ColumnBases.Eurocode(Ukc305, 345, plate, 17);
        Assert.Equal(50 * Math.Sqrt(255 / 51.0), resisted.C, 6);
        var concrete = 17 * (18.7 + 2 * resisted.C) * (307.4 + 2 * resisted.C) / 1000;
        Assert.Equal(concrete, resisted.Compression, 6);
        Assert.InRange(resisted.Compression, 2180, 2200);
        Assert.Contains("concrete", resisted.CompressionBy);
        Assert.Equal(4 * 0.9 * 800 * 353 / 1.25 / 1000, resisted.Tension, 6);
        Assert.Contains("bolts", resisted.TensionBy);

        // At 20 mm the plate bends first: 2 x 0.25 x 300 x 20² x 265 / 60.
        var thin = ColumnBases.Eurocode(Ukc305, 345, plate with { Thickness = 20 }, 17);
        Assert.Equal(2 * 0.25 * 300 * 400 * 265 / 60.0 / 1000, thin.Tension, 3);
        Assert.Contains("plate", thin.TensionBy);
    }

    [Fact]
    public void ADesignGuide1BasePlateSplitsSmallAndLargeMoments()
    {
        // A W10x49 on a 16 x 14 in plate in 4,000 psi concrete, its rods 7 in from the centre. 500 kN at 20 kNm is a small moment, all in bearing; 100 kN at 120 kNm a large one, the rods
        // pulling qmax Y - Pu.
        var column = new SteelSection("I Section", 253, 254, 14.2, 8.6, 9290, 113e6, 38.9e6, 893e3, 306e3, 991e3, 466e3, 0.5e6, 0.0e12, 0);
        var (depth, width, rods) = (406.4, 355.6, 177.8);
        var small = ColumnBases.Aisc(column, depth, width, rods, 500, 20, 27.6);
        Assert.Equal(0, small.Tension);
        var y = depth - 2 * 20e6 / 500e3;
        Assert.Equal(500e3 / (width * y) / (0.65 * 0.85 * 27.6), small.Bearing, 6);

        var large = ColumnBases.Aisc(column, depth, width, rods, 100, 120, 27.6);
        var qmax = 0.65 * 0.85 * 27.6 * width;
        var reach = rods + depth / 2;
        var bearing = reach - Math.Sqrt(reach * reach - 2 * (120e6 + 100e3 * rods) / qmax);
        Assert.Equal((qmax * bearing - 100e3) / 1000, large.Tension, 6);
        Assert.True(large.Tension > 0);
    }

    private static IReadOnlyList<JointCheck> Frame(bool us)
    {
        // A storey of steel frame on four columns, its beams pinned and its bases fixed, a concrete roof on it: the wind takes it through its bases.
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var post = document.TypesOf<ColumnType>().First(type => type.Name == "UC 203 x 203 x 46");
        var ub = document.TypesOf<BeamType>().First(type => type.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var slab = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 6000), new Point2D(0, 6000) };
        var columns = corners.Select(corner => new Column { TypeId = post.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        foreach (var i in Enumerable.Range(0, 4)) document.Add(new Beam { TypeId = ub.Id, LevelId = first.Id, Start = corners[i], End = corners[(i + 1) % 4] });
        var floor = new Floor { TypeId = slab.Id, LevelId = first.Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        foreach (var column in columns)
        {
            var pad = Footings.Fit(document, column);
            pad.Fixity = SupportFixity.Fixed;
            document.Add(pad);
        }

        var model = AnalyticalModel.Of(document);
        return JointDesign.Of(document, SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFixedSteelColumnHasAMomentBasePlate(bool us)
    {
        var joints = Frame(us);
        var bases = joints.Where(joint => joint.Kind == "Moment base plate").ToList();
        Assert.Equal(4, bases.Count);
        Assert.DoesNotContain(joints, joint => joint.Kind is "Base plate" or "Holding-down bolts");
        Assert.All(bases, joint =>
        {
            Assert.Contains("Fixed at its foot", joint.Limits);
            Assert.Contains(joint.Checks, check => check.Name == (us ? "Anchor rods in shear" : "Compression T-stub") && check.Demand > 0);
            Assert.True(joint.Enough, $"{joint.Name}: {joint.Ratio:P0} {joint.Wants}");
        });
    }
}
