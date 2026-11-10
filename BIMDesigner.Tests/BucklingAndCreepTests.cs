using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using SolverBeam = BIMDesigner.Core.Architecture.StructuralSolver.Beam;
using Vector3 = BIMDesigner.Core.Architecture.StructuralSolver.Vector3;

namespace BIMDesigner.Tests;

/// <summary>Buckling by eigenvalue, (K + λ Kg) φ = 0, and long-term deflection: timber's creep by BS EN 1995-1-1 and the NDS, concrete cracked and creeping by BS EN 1992-1-1 7.4.3 and ACI 318-19.</summary>
public class BucklingAndCreepTests
{
    /// <summary>A steel column 3 m up, twenty pieces, held at its foot fully or pinned top and bottom, 1,000 kN on its top: what it buckles at by eigenvalue.</summary>
    private static double Column(bool cantilever)
    {
        var solver = new StructuralSolver();
        var nodes = Enumerable.Range(0, 21).Select(i => solver.AddNode(new Point3D(0, 0, 3000.0 * i / 20))).ToList();
        for (var i = 0; i < 20; i++) solver.AddBeam(new SolverBeam(nodes[i], nodes[i + 1], 210000, 81000, 5380, 8.356e7, 8.356e7, 2e5, new Vector3(1, 0, 0), false, false));
        if (cantilever) solver.Restrain(nodes[0], true, true, true, true, true, true);
        else
        {
            solver.Restrain(nodes[0], true, true, true, false, false, true);
            solver.Restrain(nodes[^1], true, true, false, false, false, false);
        }

        var factored = solver.Factorise();
        var load = new double[solver.NodeCount * 6];
        load[nodes[^1] * 6 + 2] = -1e6;
        var stressed = solver.PrestressOf(factored, factored.Solve(load));
        var (factor, _) = SecondOrderAnalysis.LowestBuckling(solver, factored, stressed)!.Value;
        return factor;
    }

    [Fact]
    public void AColumnBucklesAtEulersLoad()
    {
        // π² EI / (kL)²: a cantilever, k 2, 4,810 kN; pinned both ends, k 1, four times that - against the 1,000 kN on it.
        var euler = Math.PI * Math.PI * 210000 * 8.356e7 / (3000.0 * 3000);
        Assert.InRange(Column(true), 0.995 * euler / 4 / 1e6, 1.005 * euler / 4 / 1e6);
        Assert.InRange(Column(false), 0.99 * euler / 1e6, 1.01 * euler / 1e6);
    }

    [Fact]
    public void AFramesEigenvalueIsNoMoreThanItsAlphaCr()
    {
        // αcr is the energy ratio along the frame's leaning shape, a Rayleigh quotient of the same problem: the lowest eigenvalue is no more than it, and for a frame that sways, not far under.
        var document = NotionalRemovalTests.Office(3);
        var model = AnalyticalModel.Of(document);
        var second = SecondOrderAnalysis.Run(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model)));
        var least = second.Results[0];
        var buckling = second.Bucklings.First();
        Assert.True(buckling.Factor <= least.Alpha * (1 + 1e-6), $"{buckling.Factor:0.00} against {least.Alpha:0.00}");
        Assert.True(buckling.Factor >= 0.5 * least.Alpha, $"{buckling.Factor:0.00} against {least.Alpha:0.00}");
        Assert.False(buckling.Local);
        Assert.Contains("the building as a whole", buckling.Said);
        Assert.InRange(second.Bucklings.Count, 1, 2);
    }

    /// <summary>A frame 6 by 4 m: concrete or steel columns at its corners to the first floor, beams round it and one across the middle, a timber floor spanning onto them, pads under the columns.</summary>
    private static (BimDocument Document, List<Beam> Beams) Frame(BeamType beamType, bool us = false)
    {
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        document.AddType(beamType);
        var columnType = new ColumnType("Concrete 400", ColumnShape.Rectangular, 400, 400) { Structural = true, StructuralMaterial = BeamMaterial.Concrete };
        document.AddType(columnType);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var columns = corners.Select(corner => new Column { TypeId = columnType.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        var beams = Enumerable.Range(0, 4).Select(i => new Beam { TypeId = beamType.Id, LevelId = first.Id, Start = corners[i], End = corners[(i + 1) % 4] }).ToList();
        foreach (var beam in beams) document.Add(beam);
        foreach (var column in columns) document.Add(Footings.Fit(document, column));
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)).Id, LevelId = first.Id, SpanDirection = 90 };
        floor.SetBoundary(corners);
        document.Add(floor);
        return (document, beams);
    }

    private static (StructuralAnalysis Analysis, AnalyticalMember Beam) Analysed(BimDocument document, Beam beam)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        return (analysis, model.Members.Single(member => member.Element == beam));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimberCreeps(bool us)
    {
        // A glulam-sized timber beam, 6 m along the frame's front. UK, 2.3.2.2: the permanent load's deflection by 1 + kdef, 1.6, the imposed load's by 1 + ψ2 kdef, 1.18, against span/250 net final;
        // US, NDS 3.5.2: the dead load's by Kcr 1.5, against L/240.
        var (document, beams) = Frame(new BeamType("Timber 140 x 400") { Material = BeamMaterial.Timber, Width = 140, Depth = 400 }, us);
        var (analysis, beam) = Analysed(document, beams[0]);
        var permanent = analysis.Loading.Cases.Where(each => each.Permanent).Select(each => (each, 1.0)).ToList();
        var imposed = analysis.Loading.Cases.Where(each => !each.Lateral && !each.Drifted && !each.Permanent).ToList();
        var check = analysis.DeflectionChecks(document).Single(result => result.Member == beam && result.LimitSaid.Contains(us ? "Kcr" : "kdef", StringComparison.Ordinal));
        var dead = analysis.DeflectionOf(beam, permanent).Deflection;
        var rest = imposed.Sum(each => analysis.DeflectionOf(beam, each).Deflection * (us ? 1 : 1 + 0.6 * StructuralLoading.Psi(document, each.Nature).QuasiPermanent));
        Assert.Equal((us ? 1.5 : 1.6) * dead + rest, check.Deflection, 3);
        Assert.Equal(us ? 240 : 250, check.Limit);
    }

    [Theory]
    [InlineData(600.0, false)]
    [InlineData(300.0, false)]
    [InlineData(300.0, true)]
    public void ConcreteBeamsCrackAndCreep(double depth, bool us)
    {
        // A 300 wide concrete beam over 6 m. 600 deep it stays uncracked under the quasi-permanent load, M under Mcr = fctm Ig / (h/2), and its deflection is the analysis's by 1 + φ = 3.5 alone;
        // 300 deep it cracks, ζ = 1 - 0.5 (Mcr / M)², and comes to more. In the US, Ie of 24.2.3.5 under Ig, and the dead load's creep by λΔ 2.0.
        var (document, beams) = Frame(new BeamType($"Concrete 300 x {depth:0}") { Material = BeamMaterial.Concrete, Width = 300, Depth = depth }, us);
        var (analysis, beam) = Analysed(document, beams[0]);
        var check = analysis.DeflectionChecks(document).Single(result => result.Member == beam && result.Limit == (us ? 240 : 250));
        Assert.Contains(us ? "24.2.3.5" : "7.4.3", check.LimitSaid);
        var fck = ConcreteDesign.StrengthOf(beam.Section.Material.Quality);
        var gross = 300 * depth * depth * depth / 12;
        if (us)
        {
            var dead = analysis.DeflectionOf(beam, analysis.Loading.Cases.Where(each => each.Permanent).Select(each => (each, 1.0)).ToList()).Deflection;
            var live = analysis.DeflectionOf(beam, analysis.Loading.Cases.Where(each => !each.Permanent && !each.Lateral && !each.Drifted).Select(each => (each, 1.0)).ToList()).Deflection;
            Assert.True(check.Deflection >= 3 * dead + live - 1e-9, $"{check.Deflection:0.00} against {3 * dead + live:0.00}");
            return;
        }

        var quasi = analysis.Loading.Cases.Where(each => !each.Lateral && !each.Drifted).Select(each => (each, each.Permanent ? 1.0 : StructuralLoading.Psi(document, each.Nature).QuasiPermanent)).Where(each => each.Item2 > 0).ToList();
        var elastic = analysis.DeflectionOf(beam, quasi).Deflection;
        var moment = Math.Abs(analysis.MemberOf(beam, quasi).MomentY) * 1e6;
        var cracking = 0.3 * Math.Pow(fck, 2.0 / 3) * gross / (depth / 2);
        if (depth >= 600)
        {
            Assert.True(moment < cracking, $"{moment / 1e6:0.0} kNm against Mcr {cracking / 1e6:0.0}");
            Assert.Equal(3.5 * elastic, check.Deflection, 6);
        }
        else
        {
            Assert.True(moment > cracking, $"{moment / 1e6:0.0} kNm against Mcr {cracking / 1e6:0.0}");
            Assert.True(check.Deflection > 3.5 * elastic * 1.05, $"{check.Deflection:0.00} against {3.5 * elastic:0.00}");
        }
    }
}
