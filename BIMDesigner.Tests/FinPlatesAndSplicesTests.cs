using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Fin plates as a second kind of simple joint, block tearing in fin plates and end plates, and splices where one steel column stands on another.</summary>
public class FinPlatesAndSplicesTests
{
    private static readonly SteelSection Ukb356 = new("I Section", 355.0, 171.5, 11.5, 7.4, 6490, 141e6, 9.68e6, 796e3, 113e3, 896e3, 174e3, 23.8e4, 0.0761e12, 10.2);
    private static readonly SteelSection Ukb254 = new("I Section", 251.4, 146.1, 8.6, 6.0, 3970, 44.1e6, 4.48e6, 351e3, 61.3e3, 393e3, 94.1e3, 8.55e4, 0.0660e12, 7.6);
    private static readonly SteelSection W16 = new("I Section", 403, 140, 11.2, 7.0, 5890, 156e6, 5.16e6, 773e3, 73.6e3, 885e3, 115e3, 19.2e4, 0.198e12, 10);

    [Fact]
    public void AFinPlateIsCheckedAsP358SetsItOut()
    {
        // A 356 x 171 x 51 UKB on a fin plate: three M20 8.8 in a line at 70, 40 to the plate's ends - 220 deep, at least 0.6 of the beam - and 50 to its edge, the bolt line 60 from the weld.
        var (checks, wants) = JointDesign.FinPlateEurocode(150, Ukb356, 9.0);
        StrengthCheck Named(string name) => checks.Single(check => check.Name == name);
        var fv = 0.6 * 800 * 245 / 1.25 / 1000;
        var beta = 6 * 60.0 / (3 * 4 * 70);
        Assert.Equal(3 * fv / Math.Sqrt(1 + Math.Pow(3 * beta, 2)), Named("Bolt group in shear").Capacity, 6);

        // Bearing on the plate: down it k1 2.5 by e2 and αb 40 / 66 by e1; across it k1 2.5 by p1 and αb 50 / 66 by e2; the two together by P358's interaction with βn.
        var down = 2.5 * (40.0 / 66) * 410 * 20 * 10 / 1.25 / 1000;
        var across = 2.5 * (50.0 / 66) * 410 * 20 * 10 / 1.25 / 1000;
        Assert.Equal(3 / Math.Sqrt(Math.Pow(1 / down, 2) + Math.Pow(3 * beta / across, 2)), Named("Bolts bearing on the plate").Capacity, 6);
        Assert.Equal(220 * 10 * 275 / (1.27 * Math.Sqrt(3)) / 1000, Named("Plate in shear, gross").Capacity, 6);
        Assert.Equal(10 * (220 - 3 * 22) * 410 / (Math.Sqrt(3) * 1.25) / 1000, Named("Plate in shear, net").Capacity, 6);

        // Block tearing, eccentric by (3.10): half the tension face from the bolt line to the plate's edge, the shear face from the top bolt to its foot.
        Assert.Equal((0.5 * 410 * 10 * (50 - 11) / 1.25 + 275 * 10 * (220 - 40 - 2.5 * 22) / Math.Sqrt(3)) / 1000, Named("Plate's block tearing").Capacity, 6);
        Assert.Equal(2 * 410 / Math.Sqrt(3) / (0.85 * 1.25) * 0.7 * 6 * (220 - 12) / 1000, Named("Welds to what holds it").Capacity, 6);
        Assert.DoesNotContain(checks, check => check.Name == "Plate in bending");
        Assert.Contains("220 deep", wants);
        Assert.Contains("3 M20 8.8", wants);

        // A 254 x 146 x 31 has room for two, a plate 150 deep: short of 2.73 z, 164, so its bending, Wel fy / z.
        var (shallow, small) = JointDesign.FinPlateEurocode(80, Ukb254, 9.0);
        Assert.Equal(10 * 150.0 * 150 / 6 * 275 / 60 / 1000, shallow.Single(check => check.Name == "Plate in bending").Capacity, 6);
        Assert.Contains("150 deep", small);

        // A 152 x 89 x 16 is 122 deep between its root radii: two bolts at 70 with 40 to the plate's ends do not fit there, for a fin plate or a partial depth end plate.
        var ukb152 = new SteelSection("I Section", 152.4, 88.7, 7.7, 4.5, 2030, 8.34e6, 0.898e6, 109e3, 20.2e3, 123e3, 31.2e3, 3.56e4, 0.00471e12, 7.6);
        var fits = JointDesign.FinPlateEurocode(10, ukb152, 9.0).Checks.Single(check => check.Name == "Plate within the beam's web");
        Assert.Equal(150, fits.Demand, 6);
        Assert.Equal(152.4 - 2 * (7.7 + 7.6), fits.Capacity, 6);
        Assert.True(fits.Ratio > 1);
        Assert.True(JointDesign.EndPlateEurocode(10, ukb152, 9.0).Checks.Single(check => check.Name == "Plate within the web").Ratio > 1);
        Assert.DoesNotContain(JointDesign.FinPlateEurocode(150, Ukb356, 9.0).Checks, check => check.Name == "Plate within the beam's web");
    }

    [Fact]
    public void AnEndPlateIsCheckedForBlockTearing()
    {
        // 200 kN on a 356 deep beam's end plate: two rows of M20 at 90 gauge, the plate 0.6 of the beam deep, 213; each side torn out by (3.9), 30 to its edge and the shear face from its top row down.
        var (checks, wants) = JointDesign.EndPlateEurocode(200, Ukb356, 9.0);
        var tearing = checks.Single(check => check.Name == "Plate's block tearing");
        Assert.Equal(2 * (410 * 10 * (30 - 11) / 1.25 + 275 * 10 * (213 - 40 - 1.5 * 22) / Math.Sqrt(3)) / 1000, tearing.Capacity, 6);
        Assert.Contains("150 by 213", wants);
    }

    [Fact]
    public void AShearTabIsCheckedByAisc360()
    {
        // A W16x31 on a 3/8 in A36 single plate: three 3/4 in A325-N at 3 in, 9 in deep - at least half the beam - 1 1/2 in to its ends and edge, the bolt line 3 in from the weld.
        var (checks, wants) = JointDesign.FinPlateAisc(120, W16, 9.0);
        StrengthCheck Named(string name) => checks.Single(check => check.Name == name);
        var bolt = 0.75 * 372 * 285 / 1000;
        var share = 1 / Math.Sqrt(1 + Math.Pow(6 * 76.2 / (4 * 76.2), 2));
        Assert.Equal(3 * bolt * share, Named("Bolt group in shear").Capacity, 6);

        // Tearout at the end bolt, 1.2 lc t Fu with lc 1 1/2 in less half the hole; the two inner bolts held to 2.4 d t Fu; the group by C / n.
        var (tp, d, depth) = (9.525, 19.05, 228.6);
        var bearing = share * 0.75 * (1.2 * (38.1 - 10.3) * tp * 400 + 2 * 2.4 * d * tp * 400) / 1000;
        Assert.Equal(bearing, Named("Bolts bearing on the plate").Capacity, 6);
        Assert.Equal(0.6 * 248 * depth * tp / 1000, Named("Plate's shear yielding").Capacity, 6);
        Assert.Equal(0.75 * 0.6 * 400 * (depth - 3 * 20.6) * tp / 1000, Named("Plate's shear rupture").Capacity, 6);

        // Block shear by (J4-5): the net shear face's rupture or its gross yielding, the less, with the tension face from the bolt line to the edge.
        var tension = 400 * (38.1 - 10.3) * tp;
        Assert.Equal(0.75 * Math.Min(0.6 * 400 * (depth - 38.1 - 2.5 * 20.6) * tp + tension, 0.6 * 248 * (depth - 38.1) * tp + tension) / 1000, Named("Plate's block shear").Capacity, 6);
        Assert.Equal(0.9 * 248 * tp * depth * depth / 4 / 1000, Named("Plate in flexure").Capacity, 6);
        Assert.Equal(120 * 76.2, Named("Plate in flexure").Demand, 6);
        Assert.Contains("9 in deep", wants);
    }

    [Fact]
    public void ABeamTypeOnFinPlatesIsKeptAndItsJointsAreFinPlates()
    {
        // One steel beam across the house, another framing into it partway along on a type with its end connection set to a fin plate.
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        var plain = document.TypesOf<BeamType>().First(each => each.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var finned = plain.Duplicate("UB 254 on fin plates");
        var parameter = finned.GetTypeParameters(document).Single(each => each.Definition.Name == "End Connection");
        Assert.Equal("End Plate", parameter.DisplayValue);
        Assert.True(parameter.TrySet("Fin Plate"));
        Assert.Equal(BeamConnection.FinPlate, finned.Connection);
        document.AddType(finned);
        document.Add(new Beam { TypeId = plain.Id, LevelId = document.Levels[1].Id, Start = new Point2D(0, 4000), End = new Point2D(10000, 4000) });
        document.Add(new Beam { TypeId = finned.Id, LevelId = document.Levels[1].Id, Start = new Point2D(5000, 0), End = new Point2D(5000, 4000) });

        var path = Path.Combine(Path.GetTempPath(), $"finned-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            Assert.Equal(BeamConnection.FinPlate, ProjectFile.Load(path).FindType<BeamType>(finned.Id)!.Connection);
        }
        finally
        {
            File.Delete(path);
        }

        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var joints = JointDesign.Of(document, SecondOrderAnalysis.Run(document, analysis));
        var fin = joints.Single(each => each.Kind == "Fin plate");
        Assert.Contains("fin plate", fin.Wants);
        Assert.Contains(fin.Checks, check => check.Name == "Plate's block tearing" && check.Ratio > 0);
        Assert.DoesNotContain(joints, each => each.Kind == "Steel end plate");
    }

    /// <summary>A steel frame of corner columns a storey at a time, beams round each floor and a concrete floor on them.</summary>
    private static BimDocument Frame(int storeys, bool us)
    {
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BuildingType = "Office";
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        for (var i = levels.Count; i <= storeys; i++)
        {
            var level = new Level { Name = $"Level {i}", Elevation = 3000 * i };
            document.AddLevel(level);
            levels.Add(level);
        }

        var columnType = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var beamType = document.TypesOf<BeamType>().First(type => type.Section == BeamSection.IBeam);
        var slab = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        for (var s = 0; s < storeys; s++)
        {
            var columns = corners.Select(corner => new Column { TypeId = columnType.Id, LevelId = levels[s].Id, TopLevelId = levels[s + 1].Id, Location = corner, Structural = true }).ToList();
            foreach (var column in columns) document.Add(column);
            if (s == 0)
                foreach (var column in columns) document.Add(Footings.Fit(document, column));
            foreach (var i in Enumerable.Range(0, 4)) document.Add(new Beam { TypeId = beamType.Id, LevelId = levels[s + 1].Id, Start = corners[i], End = corners[(i + 1) % 4] });
            var floor = new Floor { TypeId = slab.Id, LevelId = levels[s + 1].Id };
            floor.SetBoundary(corners);
            document.Add(floor);
        }

        return document;
    }

    [Fact]
    public void ColumnSplicesCarryAQuarterOfTheirCompressionAndTheirVerticalTie()
    {
        // A six storey steel office in England, Consequence Class 2b: a splice wherever one column stands on another, four at each of four floors. Low down a quarter of what the column above carries
        // is the most it asks of the splice (BS EN 1993-1-8 6.2.7.1(14)); at the top, under the roof's single storey, its vertical tie is (Approved Document A 5.1, BS EN 1991-1-7 A.6(2)).
        var document = Frame(5, us: false);
        var model = AnalyticalModel.Of(document);
        var second = SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model)));
        var splices = JointDesign.Of(document, second).Where(joint => joint.Kind == "Column splice").ToList();
        Assert.Equal(16, splices.Count);
        Assert.All(splices, splice => Assert.Contains("6.2.7.1(14)", splice.Limits));
        Assert.All(splices, splice => Assert.Contains("Class 2b", splice.Limits));
        Assert.All(splices, splice => Assert.True(splice.Checks.All(check => check.Demand > 0)));

        // The bottom four and the top four, by where the column above stands.
        double Foot(JointCheck splice) => model.Members.Single(member => splice.Name.StartsWith($"{member.Name} on ", StringComparison.Ordinal)).Nodes.Min(node => node.At.Z);
        var ordered = splices.OrderBy(Foot).ToList();
        Assert.All(ordered.Take(4), splice => Assert.Contains("a quarter of its compression", splice.Checks[0].Note));
        Assert.All(ordered.TakeLast(4), splice => Assert.Contains("its vertical tie", splice.Checks[0].Note));

        // The cover plates' net area, 0.9 Anet fu / γM2, two of them, each as wide as the narrower flange less two holes.
        var column = SteelSection.Of(model.Members.First(member => member.Kind == AnalyticalMemberKind.Column).Section)!;
        Assert.All(splices, splice => Assert.Equal(2 * 0.9 * 410 * 10 * (column.B - 44) / 1.25 / 1000, splice.Checks.Single(check => check.Name == "Cover plates in tension").Capacity, 6));
        Assert.All(splices, splice => Assert.Equal(4 * JointDesign.BoltShearEurocode(245), splice.Checks.Single(check => check.Name == "Bolts in shear, each side").Capacity, 6));
    }

    [Fact]
    public void ABeamIntoAColumnThatRunsOnAboveFramesIntoItsSide()
    {
        // Two storeys of steel frame: at the first floor each beam frames into the side of a column running on above it, an end plate at each of its ends carrying its shear whichever way it
        // acts - next to a stiff concrete floor the analysis holds the beams' ends down - and only at the roof, on the posts' tops, are they on a post.
        var document = Frame(2, us: false);
        var model = AnalyticalModel.Of(document);
        var second = SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model)));
        var joints = JointDesign.Of(document, second);
        var plates = joints.Where(joint => joint.Kind == "Steel end plate").ToList();
        Assert.Equal(8, plates.Count);
        Assert.All(plates, plate => Assert.True(plate.Ratio > 0));
        var top = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column && member.Nodes.Max(node => node.At.Z) > 5000).Select(member => member.Name).ToList();
        Assert.All(joints.Where(joint => joint.Kind == "Beam on a post"), joint => Assert.Contains(top, name => joint.Name.EndsWith($" to {name}", StringComparison.Ordinal)));
    }

    [Fact]
    public void AUsColumnSpliceIsHeldInLineAndCarriesWhatLiftsIt()
    {
        // In the US a column finished to bear needs only holding in line, AISC 360-16 J1.4(a); the splice is for what lifts it.
        var document = Frame(2, us: true);
        var model = AnalyticalModel.Of(document);
        var second = SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model)));
        var splices = JointDesign.Of(document, second).Where(joint => joint.Kind == "Column splice").ToList();
        Assert.Equal(4, splices.Count);
        Assert.All(splices, splice => Assert.Contains("J1.4(a)", splice.Limits));
        Assert.All(splices, splice => Assert.Equal(4 * 0.75 * 372 * 285 / 1000, splice.Checks.Single(check => check.Name == "Bolts in shear, each side").Capacity, 6));
        Assert.All(splices, splice => Assert.DoesNotContain("Class", splice.Limits));
    }
}
