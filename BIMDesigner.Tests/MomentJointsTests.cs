using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>Moment joints: extended end plates by SCI P398's steps on BS EN 1993-1-8 6.2.7, and the four-bolt extended end plate of AISC Design Guide 4.</summary>
public class MomentJointsTests
{
    // P398 Example C.1: a 533 x 210 x 92 UKB into a 254 x 254 x 107 UKC, both S275, on a 25 mm S275 end plate 250 wide with M24 8.8 at 100 gauge: a row 40 above the
    // tension flange with 50 of plate past it, then rows 60 and 150 below the flange's top; 12 mm fillets to the tension flange, 8 to the web and the compression flange.
    private static readonly SteelSection Ukb533 = new("I Section", 533.1, 209.3, 15.6, 10.1, 11700, 55200e4, 2390e4, 2070e3, 228e3, 2360e3, 355e3, 75.7e4, 1.6e12, 12.7);
    private static readonly SteelSection Ukc254 = new("I Section", 266.7, 258.8, 20.5, 12.8, 13600, 17500e4, 5930e4, 1310e3, 458e3, 1480e3, 697e3, 172e4, 0.898e12, 12.7);
    private static readonly EndPlateLayout C1 = new(24, 25, 250, 100, 2, AboveFlange: 40, Extension: 50, FirstBelow: 60, Pitch: 90, TensionWeld: 12, CompressionWeld: 8, WebWeld: 8);

    [Fact]
    public void AnEndPlateComesToP398sExampleC1()
    {
        // The moments either side equal and opposite, β 0. Row 1 is the end plate's extension in Mode 2, 377 kN; row 2 the column flange with row 1, 697 - 377 = 320; row 3 the
        // column flange with rows 1 and 2, 988 - 697 = 291. Together 988 kN against the column web's 841 in compression, so row 3 is cut to 144: Mj,Rd = 565 x 377 + 465 x 320
        // + 375 x 144 = 416 kNm. The shear, a bottom row and 28% of the six bolts in tension, (2 + 6 x 0.28) x 136 = 500 kN.
        var joint = MomentJoints.Eurocode(Ukb533, 275, Ukc254, 265, C1, beta: 0);
        Assert.Equal(3, joint.Rows.Count);
        Assert.Equal(377, joint.Rows[0].Force, 0);
        Assert.InRange(joint.Rows[1].Force, 319, 322);
        Assert.InRange(joint.Rows[2].Force, 140, 145);
        Assert.Equal(565.3, joint.Rows[0].Lever, 1);
        Assert.InRange(joint.Compression, 838, 842);
        Assert.Contains("column web in compression", joint.CompressionBy);
        Assert.InRange(joint.Moment, 415, 417);
        Assert.InRange(joint.Shear, 498, 500);
        Assert.Contains("end plate", joint.Rows[0].By);
        Assert.Contains("column flange", joint.Rows[1].By);
    }

    [Fact]
    public void OnOneSideTheColumnWebPanelLimitsIt()
    {
        // The same joint with a beam on one side only, β 1: the web panel's 0.9 fy Avc / √3 = 0.9 x 265 x 3772 / √3 = 519 kN caps the tension, row 1's 377 and row 2 the rest.
        var joint = MomentJoints.Eurocode(Ukb533, 275, Ukc254, 265, C1, beta: 1);
        var avc = 13600 - 2 * 258.8 * 20.5 + (12.8 + 2 * 12.7) * 20.5;
        Assert.Equal(avc, MomentJoints.ShearArea(Ukc254), 6);
        var panel = 0.9 * 265 * avc / Math.Sqrt(3) / 1000;
        Assert.Equal(panel, joint.PanelShear, 6);
        Assert.Contains("panel", joint.CompressionBy);
        Assert.Equal(panel, joint.Rows.Sum(row => row.Force), 6);
        var first = joint.Rows[0].Force;
        var expected = (565.3 * first + 465.3 * (panel - first)) / 1000;
        Assert.InRange(joint.Moment, expected - 0.1, expected + 0.1);

        // ω for the web in compression, 1 / √(1 + 1.3 (beff twc / Avc)²) with beff = 15.6 + 2 x 8 + 5 (20.5 + 12.7) + 50 = 247.6.
        var omega = 1 / Math.Sqrt(1 + 1.3 * Math.Pow(247.6 * 12.8 / avc, 2));
        Assert.Equal(omega, MomentJoints.Omega(Ukc254, 247.6, 1), 6);

        // The joint is stiffer with the moments balanced than with one side alone, its web panel then not sheared.
        Assert.True(MomentJoints.Eurocode(Ukb533, 275, Ukc254, 265, C1, beta: 0).Stiffness > joint.Stiffness);
    }

    [Fact]
    public void AlphaIsP398sAppendixG()
    {
        // Example C.3: λ1 0.315, λ2 0.387, read from the chart as 7.2. Example C.1: λ1 0.340, λ2 0.306, read as 7.5 - the expressions give 7.34, the chart read a little high.
        Assert.InRange(MomentJoints.Alpha(35.5 / 112.8, 43.6 / 112.8), 7.15, 7.25);
        Assert.InRange(MomentJoints.Alpha(38.6 / 113.6, 34.8 / 113.6), 7.3, 7.5);
        Assert.Equal(8, MomentJoints.Alpha(0.05, 1.2));
        Assert.Equal(4.45, MomentJoints.Alpha(0.95, 0.3));

        // On the straight part of a curve λ1 = 1.25 / (α - 2.75).
        Assert.Equal(2.75 + 1.25 / 0.3, MomentJoints.Alpha(0.3, 1.4), 3);
    }

    [Fact]
    public void AFourBoltExtendedEndPlateIsByDesignGuide4()
    {
        // A W18x50 into a W14x90 on a 1 in plate with 1 in A325 bolts: pf 2 in, h0 = d - tf/2 + pf, h1 = d - 3tf/2 - pf; the bolts φ 2 Pt (h0 + h1), Pt = 90 ksi Ab;
        // the plate φ Fyp tp² Yp with bp = bf + 1 in and g = 5 1/2 in.
        var beam = new SteelSection("I Section", 457.2, 190.5, 14.5, 9.0, 9480, 333e6, 16.6e6, 1460e3, 175e3, 1650e3, 271e3, 52e4, 1.27e12, 10.2);
        var column = new SteelSection("I Section", 356, 369, 18.0, 11.2, 17100, 416e6, 150e6, 2340e3, 813e3, 2580e3, 1240e3, 170e4, 4.1e12, 15.3);
        var joint = MomentJoints.Aisc(beam, column, 25.4, 25.4);
        var (h0, h1) = (457.2 - 7.25 + 50.8, 457.2 - 21.75 - 50.8);
        var area = Math.PI * 25.4 * 25.4 / 4;
        Assert.Equal(0.75 * 2 * 620 * area * (h0 + h1) / 1e6, joint.BoltRupture, 6);
        var (bp, g) = (190.5 + 25.4, 139.7);
        var s = Math.Sqrt(bp * g) / 2;
        var yp = bp / 2 * (h1 * (1 / 50.8 + 1 / s) + h0 / 50.8 - 0.5) + 2 / g * h1 * (50.8 + s);
        Assert.Equal(0.9 * 345 * 25.4 * 25.4 * yp / 1e6, joint.PlateYield, 6);
        Assert.InRange(joint.PlateYield, 600, 630);
        Assert.Equal(0.75 * 4 * 372 * area / 1000, joint.Shear, 6);

        // The column's flange by Yc, its panel zone 0.9 x 0.6 Fy dc tw.
        var sc = Math.Sqrt(369 * g) / 2;
        var c = 2 * 50.8 + 14.5;
        var yc = 369 / 2.0 * (h1 / sc + h0 / sc) + 2 / g * (h1 * (sc + 0.75 * c) + h0 * (sc + 0.25 * c) + c * c / 2) + g / 2;
        Assert.Equal(0.9 * 345 * 18.0 * 18.0 * yc / 1e6, joint.ColumnFlange, 6);
        Assert.Equal(0.9 * 0.6 * 345 * 356 * 11.2 / 1000, joint.PanelZone, 6);
    }

    /// <summary>
    /// A two storey steel frame: columns at x 0 and 6000 and y 0, 6000 and 12000 from the ground to the roof, past the first floor; beams fixed at their ends both ways at the first floor
    /// and the roof; concrete floors on them. Its columns' flanges face along y, so the beams along y are into their flanges and those along x into their webs.
    /// </summary>
    private static BimDocument Frame(bool us)
    {
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BuildingType = "Office";
        var roof = new Level { Name = "Roof", Elevation = 6000 };
        document.AddLevel(roof);
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var post = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var ub = document.TypesOf<BeamType>().First(type => type.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var slab = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var columns = new List<Column>();
        foreach (var x in new[] { 0.0, 6000 })
            foreach (var y in new[] { 0.0, 6000, 12000 })
                columns.Add(new Column { TypeId = post.Id, LevelId = ground.Id, TopLevelId = roof.Id, Location = new Point2D(x, y), Structural = true });
        foreach (var column in columns) document.Add(column);
        foreach (var level in new[] { first, roof })
        {
            foreach (var y in new[] { 0.0, 6000, 12000 })
                document.Add(new Beam { TypeId = ub.Id, LevelId = level.Id, Start = new Point2D(0, y), End = new Point2D(6000, y), StartRelease = MemberRelease.Fixed, EndRelease = MemberRelease.Fixed });
            foreach (var x in new[] { 0.0, 6000 })
                foreach (var y in new[] { 0.0, 6000 })
                    document.Add(new Beam { TypeId = ub.Id, LevelId = level.Id, Start = new Point2D(x, y), End = new Point2D(x, y + 6000), StartRelease = MemberRelease.Fixed, EndRelease = MemberRelease.Fixed });
            var floor = new Floor { TypeId = slab.Id, LevelId = level.Id };
            floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 12000), new Point2D(0, 12000) });
            document.Add(floor);
        }

        foreach (var column in columns)
        {
            var pad = Footings.Fit(document, column);
            pad.Fixity = SupportFixity.Fixed;
            document.Add(pad);
        }

        return document;
    }

    private static (AnalyticalModel Model, IReadOnlyList<JointCheck> Joints) Joints(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var second = SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model)));
        return (model, JointDesign.Of(document, second));
    }

    [Fact]
    public void AFixedBeamIntoAColumnsFlangeHasAMomentEndPlate()
    {
        var (model, joints) = Joints(Frame(us: false));

        // At the first floor the four beams along y frame into the columns' flanges, a joint at each end: eight moment end plates.
        var moment = joints.Where(joint => joint.Kind == "Moment end plate").ToList();
        Assert.Equal(8, moment.Count);
        Assert.All(moment, joint =>
        {
            Assert.Contains("extended end plate", joint.Wants);
            Assert.Contains("Sj,ini", joint.Limits);
            var check = joint.Checks.Single(each => each.Name == "Moment at the joint");
            Assert.True(check.Demand > 0 && check.Capacity > 0, $"{joint.Name}: {check.Demand:0.0} of {check.Capacity:0.0} {check.Under} {check.Note} | {joint.Wants} | {joint.Limits}");
            Assert.Contains(joint.Checks, each => each.Name == "Column web panel in shear");
        });

        // At the middle columns a beam either side, their moments much the same under the floor's weight: their web panels see the difference, at the end columns the whole of one side.
        var middle = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column && Math.Abs(member.Start.At.Y - 6000) < 1).Select(member => member.Name).ToHashSet();
        var (inside, outside) = (moment.Where(joint => middle.Any(name => joint.Name.EndsWith($" to {name}", StringComparison.Ordinal))).ToList(),
            moment.Where(joint => !middle.Any(name => joint.Name.EndsWith($" to {name}", StringComparison.Ordinal))).ToList());
        Assert.Equal(4, inside.Count);
        Assert.All(inside, joint => Assert.Contains("either side", joint.Checks.Single(each => each.Name == "Column web panel in shear").Note));
        double Panel(JointCheck joint) => joint.Checks.Single(each => each.Name == "Column web panel in shear").Demand;
        Assert.True(inside.Max(Panel) < outside.Min(Panel), $"{inside.Max(Panel):0} against {outside.Min(Panel):0}");

        // Those along x into the columns' webs keep a shear plate and say the moment there is not worked out; at the roof, on the posts' tops, knees.
        Assert.Contains(joints, joint => joint.Kind == "Steel end plate" && joint.Limits.Contains("column's web", StringComparison.Ordinal));
        Assert.All(joints.Where(joint => joint.Kind == "Beam on a post" && joint.Checks.Count > 0), joint => Assert.Contains("knee", joint.Limits));
    }

    [Fact]
    public void AUsFixedBeamHasAFourBoltExtendedEndPlate()
    {
        var (_, joints) = Joints(Frame(us: true));
        var moment = joints.Where(joint => joint.Kind == "Moment end plate").ToList();
        Assert.Equal(8, moment.Count);
        Assert.All(moment, joint =>
        {
            Assert.Contains("(4E)", joint.Wants);
            Assert.Contains(joint.Checks, each => each.Name == "Bolts in tension, no prying" && each.Ratio <= 1);
            Assert.Contains(joint.Checks, each => each.Name == "Panel zone in shear");
        });
    }
}
