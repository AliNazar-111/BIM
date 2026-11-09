using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using SolverBeam = BIMDesigner.Core.Architecture.StructuralSolver.Beam;
using Vector3 = BIMDesigner.Core.Architecture.StructuralSolver.Vector3;

namespace BIMDesigner.Tests;

/// <summary>The structure's natural modes by subspace iteration, and a timber floor's vibration by BS EN 1995-1-1 7.3.3 with the UK National Annex.</summary>
public class ModalAnalysisTests
{
    [Fact]
    public void ASmallProblemIsSolvedByJacobi()
    {
        // [[2, -1], [-1, 2]] has 1 and 3; with M = diag(2, 1), det(K - λM) = (2 - 2λ)(2 - λ) - 1 = 0, λ = (3 ± √3) / 2.
        var (values, vectors) = ModalAnalysis.Generalised(new double[,] { { 2, -1 }, { -1, 2 } }, new double[,] { { 1, 0 }, { 0, 1 } });
        Assert.Equal(1, values[0], 9);
        Assert.Equal(3, values[1], 9);
        Assert.Equal(Math.Abs(vectors[0, 0]), Math.Abs(vectors[1, 0]), 9);
        var (weighted, _) = ModalAnalysis.Generalised(new double[,] { { 2, -1 }, { -1, 2 } }, new double[,] { { 2, 0 }, { 0, 1 } });
        Assert.Equal((3 - Math.Sqrt(3)) / 2, weighted[0], 9);
        Assert.Equal((3 + Math.Sqrt(3)) / 2, weighted[1], 9);
    }

    /// <summary>A steel beam on its own, so many pieces, its mass of 42.2 kg/m lumped at its nodes, tonnes.</summary>
    private static (StructuralSolver Solver, double[] Mass) Beam(double length, int pieces, bool cantilever, double weak = 8.356e7)
    {
        var solver = new StructuralSolver();
        var nodes = Enumerable.Range(0, pieces + 1).Select(i => solver.AddNode(new Point3D(length * i / pieces, 0, 0))).ToList();
        for (var i = 0; i < pieces; i++) solver.AddBeam(new SolverBeam(nodes[i], nodes[i + 1], 210000, 81000, 5380, 8.356e7, weak, 2e5, new Vector3(0, 1, 0), false, false));
        if (cantilever) solver.Restrain(nodes[0], true, true, true, true, true, true);
        else
        {
            solver.Restrain(nodes[0], true, true, true, true, false, false);
            solver.Restrain(nodes[^1], false, true, true, false, false, false);
        }

        var piece = 42.2 * length / 1000 / pieces / 1000;
        return (solver, nodes.Select((_, i) => i == 0 || i == pieces ? piece / 2 : piece).ToArray());
    }

    [Fact]
    public void ASimplySupportedBeamVibratesAtItsOwnFrequencies()
    {
        // f1 = π / 2L² √(EI / m): up and down on an IPE 300's 8,356 cm⁴, π / 72 √(210e9 x 8.356e-5 / 42.2) = 28.1 Hz; across on its 604 cm⁴, 7.6 Hz, and that way's second four times it. Each first
        // moves 8/π² of the mass that moves, a twentieth of it being at the ends, held.
        var (solver, mass) = Beam(6000, 20, false, 6.04e6);
        var result = ModalAnalysis.Of(solver, mass, 6);
        var (up, across) = (Math.PI / (2 * 36) * Math.Sqrt(210e9 * 8.356e-5 / 42.2), Math.PI / (2 * 36) * Math.Sqrt(210e9 * 6.04e-6 / 42.2));
        Assert.InRange(result.Modes[0].Frequency, 0.995 * across, 1.005 * across);
        Assert.Equal("along y", result.Modes[0].Way);
        var bending = result.Modes.Single(mode => mode.ShareZ > 0.5);
        Assert.InRange(bending.Frequency, 0.995 * up, 1.005 * up);
        Assert.Contains(result.Modes, mode => mode.ShareY < 1e-6 && mode.ShareZ < 1e-6 && Math.Abs(mode.Frequency / (4 * across) - 1) < 0.02);
        foreach (var first in new[] { result.Modes[0].ShareY, bending.ShareZ })
            Assert.InRange(first, 0.95 * 8 / (Math.PI * Math.PI), 8 / (Math.PI * Math.PI));
        Assert.Equal("up and down", bending.Way);
        Assert.Equal(42.2 * 6 / 1000, result.Mass, 9);
    }

    [Fact]
    public void ACantileverVibratesAtItsOwnFrequency()
    {
        // f1 = 1.875² / 2π √(EI / m L⁴) over 3 m: 37.4 Hz.
        var (solver, mass) = Beam(3000, 20, true);
        var result = ModalAnalysis.Of(solver, mass, 4);
        var f1 = 1.875104 * 1.875104 / (2 * Math.PI) * Math.Sqrt(210e9 * 8.356e-5 / (42.2 * 81));
        Assert.InRange(result.Modes[0].Frequency, 0.99 * f1, 1.01 * f1);
        Assert.InRange(result.Modes[0].Period, 0.99 / f1, 1.01 / f1);
    }

    [Fact]
    public void TheUpperLimitOnAPeriodIsTable1281s()
    {
        Assert.Equal(1.7, ModalAnalysis.UpperLimit(0.05), 9);
        Assert.Equal(1.6, ModalAnalysis.UpperLimit(0.15), 9);
        Assert.Equal(1.45, ModalAnalysis.UpperLimit(0.25), 9);
        Assert.Equal(1.4, ModalAnalysis.UpperLimit(0.6), 9);
    }

    /// <summary>A box of bearing masonry walls 6 m along x and so deep, to the first floor, its timber floor on 47 x 200 joists at 400 spanning the depth, on strip footings.</summary>
    private static (BimDocument Document, Floor Floor) Joisted(double deep, bool us = false)
    {
        var document = BimDocument.CreateDefault();
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, deep), new Point2D(0, deep) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = ground, TopLevelId = first, StructuralUsage = StructuralUsage.Bearing }).ToList();
        foreach (var wall in walls) document.Add(wall);
        foreach (var wall in walls) document.Add(Footings.Fit(document, wall));
        var joist = new BeamType("Timber 47 x 200") { Material = BeamMaterial.Timber, Width = 47, Depth = 200 };
        document.AddType(joist);
        for (var x = 200; x <= 5800; x += 400)
            document.Add(new Beam { TypeId = joist.Id, LevelId = first, Start = new Point2D(x, 0), End = new Point2D(x, deep), Usage = BeamUsage.Joist });
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)).Id, LevelId = first, SpanDirection = 0 };
        floor.SetBoundary(corners);
        document.Add(floor);
        return (document, floor);
    }

    [Theory]
    [InlineData(4000.0)]
    [InlineData(6000.0)]
    public void ATimberFloorsVibrationIsByBsEn199511(double deep)
    {
        // Unloaded: the floor's build-up and its joists, kg/m². f1 = π / 2ℓ² √((EI)ℓ / m), (EI)ℓ the joists' EI over their spacing. Over 4 m about 13 Hz, well clear of 8; over 6 m four ninths of
        // that, under 8, and a special investigation is wanted. w under 1 kN = 1000 kdist ℓ³ 1.05 / 48 EI, kdist = 0.38 - 0.08 ln(14 (EI)b / s⁴), a 1.8 mm to 4 m and 16500 / ℓ^1.1 past.
        var (document, floor) = Joisted(deep);
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var result = Assert.Single(FloorVibrations.Of(document, analysis));
        Assert.Same(floor, result.Floor);
        Assert.Equal(15, result.Joists);
        Assert.Equal(400, result.Spacing, 6);
        Assert.InRange(result.Span, deep, deep + 200);

        var section = model.Members.First(member => member.Kind == AnalyticalMemberKind.Joist).Section;
        var ei = section.Material.Modulus * section.Iy;
        var mass = Loads.Weight(document, document.FindType<SlabType>(floor.TypeId)!.Structure) * 1000 / 9.81 + section.Area * 1e-6 * section.Material.Density / 0.4;
        var l = result.Span / 1000;
        var f1 = Math.PI / (2 * l * l) * Math.Sqrt(ei * 1e-6 / 0.4 / mass);
        Assert.Equal(f1, result.Frequency, 6);
        Assert.Equal(mass, result.Mass, 6);
        var frequency = result.Checks.Single(check => check.Name == "Fundamental frequency");
        Assert.Equal(8 / f1, frequency.Ratio, 6);
        if (deep <= 4000) Assert.InRange(f1, 10, 20);
        else Assert.True(f1 < 8, $"{f1:0.0} Hz");

        var deck = 3500 * 22.0 * 22 * 22 / 12 * 1000;
        var kdist = Math.Max(0.38 - 0.08 * Math.Log(14 * deck / Math.Pow(400, 4)), 0.3);
        Assert.InRange(kdist, 0.33, 0.34);
        var w = 1000 * kdist * Math.Pow(result.Span, 3) * 1.05 / (48 * ei);
        var deflection = result.Checks.Single(check => check.Name == "Deflection under 1 kN");
        Assert.Equal(w, deflection.Demand, 6);
        Assert.Equal(result.Span <= 4000 ? 1.8 : 16500 / Math.Pow(result.Span, 1.1), deflection.Capacity, 6);
        Assert.Equal(deep > 4000, !result.Enough);
    }

    [Fact]
    public void AUsFloorHasNoVibrationLimit()
    {
        var (document, _) = Joisted(4000, us: true);
        var model = AnalyticalModel.Of(document);
        Assert.Empty(FloorVibrations.Of(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model))));
    }

    [Fact]
    public void TheHousesModesHaveItsFloorInThem()
    {
        // The mass is the permanent loads and 0.3 of the floors' imposed load. Among the house's modes are its joists going up and down, at what (7.5) gives for that mass - 7.3.3 taking the floor
        // unloaded, 0.3 of 1.5 kN/m² is as much again as its build-up - each joist near enough on its own, the floor's share of the house's mass between them.
        var (document, _) = Joisted(4000);
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var result = ModalAnalysis.Of(analysis, 24);
        var cases = analysis.Loading.Cases;
        double Weight(string name) => cases.Any(each => each.Name == name) ? -analysis.TotalLoad(cases.Single(each => each.Name == name)).Z : 0;
        Assert.Equal((Weight("G1") + Weight("G2") + 0.3 * Weight("Q1")) / 9.81, result.Mass, 2);
        Assert.InRange(result.Modes.Sum(mode => mode.ShareZ), 0, 1.0001);
        var floor = FloorVibrations.Of(document, analysis)[0];
        var loaded = floor.Frequency * Math.Sqrt(floor.Mass / (floor.Mass + 0.3 * 1.5 * 1000 / 9.81));
        var joists = result.Modes.Where(mode => Math.Abs(mode.Frequency / loaded - 1) < 0.1).ToList();
        Assert.InRange(joists.Count, 10, 20);
        Assert.InRange(joists.Sum(mode => mode.ShareZ), 0.05, 0.12);
        Assert.All(result.Modes.Zip(result.Modes.Skip(1)), pair => Assert.True(pair.First.Frequency <= pair.Second.Frequency + 1e-9));
    }
}
