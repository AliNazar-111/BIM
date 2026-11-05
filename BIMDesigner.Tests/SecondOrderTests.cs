using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using SolverBeam = BIMDesigner.Core.Architecture.StructuralSolver.Beam;
using Shell = BIMDesigner.Core.Architecture.StructuralSolver.Shell;
using Vector3 = BIMDesigner.Core.Architecture.StructuralSolver.Vector3;

namespace BIMDesigner.Tests;

/// <summary>
/// Second-order effects (round 28): the geometric stiffness against Euler's load, the exact deflection and
/// moment of a beam-column and Timoshenko's plate in compression, the imperfections and reduced stiffnesses
/// the codes ask for, and whole frames - the storey formula, fixed against pinned feet, sway amplified
/// into the strength checks and a frame that buckles.
/// </summary>
public class SecondOrderTests
{
    private const double E = 210000, I = 1e8, Area = 5000, Height = 6000;

    /// <summary>A column up from the ground in so many pieces, its foot held as asked.</summary>
    private static (StructuralSolver Solver, List<int> Nodes) Column(int pieces, bool fixedFoot, bool pinnedTop = false)
    {
        var solver = new StructuralSolver();
        var nodes = Enumerable.Range(0, pieces + 1).Select(i => solver.AddNode(new Point3D(0, 0, Height * i / pieces))).ToList();
        for (var i = 0; i < pieces; i++)
            solver.AddBeam(new SolverBeam(nodes[i], nodes[i + 1], E, 81000, Area, I, I, 1e6, new Vector3(0, 1, 0), false, false));
        solver.Restrain(nodes[0], fixedFoot ? new[] { true, true, true, true, true, true } : new[] { true, true, true, false, false, true });
        if (pinnedTop) solver.Restrain(nodes[^1], true, true, false, false, false, false);
        return (solver, nodes);
    }

    /// <summary>The energy ratio along a movement: the work a load does in it over what the stress takes from its stiffness.</summary>
    private static double Alpha(StructuralSolver solver, StructuralSolver.Prestress stress, double[] load, double[] moved)
    {
        var geometric = solver.GeometricForces(stress, moved);
        return moved.Zip(load, (a, b) => a * b).Sum() / -moved.Zip(geometric, (a, b) => a * b).Sum();
    }

    /// <summary>A load to second order: what the compression pushes with put back through until it settles.</summary>
    private static double[] Settled(StructuralSolver solver, StructuralSolver.Factored factored, StructuralSolver.Prestress stress, double[] first)
    {
        var now = first;
        for (var pass = 0; pass < 60; pass++)
        {
            var pushed = solver.GeometricForces(stress, now).Select(value => -value).ToArray();
            var more = factored.Solve(pushed);
            now = first.Zip(more, (a, b) => a + b).ToArray();
        }

        return now;
    }

    [Fact]
    public void AColumnsCriticalLoadIsEulers()
    {
        // A cantilever: π²EI / 4L². Pushed sideways at its top, under half of that.
        var (solver, nodes) = Column(12, fixedFoot: true);
        var critical = Math.PI * Math.PI * E * I / (4 * Height * Height);
        var (top, axial, sideways) = (nodes[^1], new double[nodes.Count * 6], new double[nodes.Count * 6]);
        axial[top * 6 + 2] = -0.5 * critical;
        sideways[top * 6] = 1000;
        var factored = solver.Factorise();
        var stress = solver.PrestressOf(factored, factored.Solve(axial));
        Assert.Equal(-0.5 * critical, stress.Axial[0], 3);
        Assert.Equal(2, Alpha(solver, stress, sideways, factored.Solve(sideways)), 1);

        // Pin-ended: π²EI / L², four times as much, leaning on the same way along its height.
        var (pinned, pinnedNodes) = Column(12, fixedFoot: false, pinnedTop: true);
        var along = new double[pinnedNodes.Count * 6];
        foreach (var node in pinnedNodes) along[node * 6] = 100;
        var load = new double[pinnedNodes.Count * 6];
        load[pinnedNodes[^1] * 6 + 2] = -critical;
        var strut = pinned.Factorise();
        Assert.Equal(4, Alpha(pinned, pinned.PrestressOf(strut, strut.Solve(load)), along, strut.Solve(along)), 1);
    }

    [Fact]
    public void ABeamColumnDeflectsAndBendsAsTheExactSolutionHasIt()
    {
        // A cantilever under P and H at its top: δ = H/Pk (tan kL - kL) and M = H tan(kL) / k, k = √(P/EI).
        var (solver, nodes) = Column(12, fixedFoot: true);
        var critical = Math.PI * Math.PI * E * I / (4 * Height * Height);
        var (p, h) = (0.5 * critical, 2000.0);
        var (axial, push) = (new double[nodes.Count * 6], new double[nodes.Count * 6]);
        axial[nodes[^1] * 6 + 2] = -p;
        push[nodes[^1] * 6] = h;
        var factored = solver.Factorise();
        var stress = solver.PrestressOf(factored, factored.Solve(axial));
        var settled = Settled(solver, factored, stress, factored.Solve(push));

        var k = Math.Sqrt(p / (E * I));
        var exact = h / (p * k) * (Math.Tan(k * Height) - k * Height);
        Assert.InRange(settled[nodes[^1] * 6], 0.99 * exact, 1.01 * exact);

        // At its foot, from the first piece's end forces; first order it would be H L.
        var forces = solver.BeamEndForces(solver.Beams[0], settled);
        var moment = Math.Abs(forces[4]);
        var exactMoment = h * Math.Tan(k * Height) / k;
        Assert.InRange(moment, 0.97 * exactMoment, 1.03 * exactMoment);
        Assert.True(moment > 1.4 * h * Height, $"{moment / (h * Height):0.00} times first order");
    }

    [Fact]
    public void APlateInCompressionBucklesAtTimoshenkosLoad()
    {
        // A square plate simply supported round its edges, compressed one way: N = 4π²D / a².
        const double a = 3000, t = 100, nu = 0.3, Ec = 30000;
        const int cells = 14;
        var solver = new StructuralSolver();
        var node = new int[cells + 1, cells + 1];
        for (var i = 0; i <= cells; i++)
        for (var j = 0; j <= cells; j++)
            node[i, j] = solver.AddNode(new Point3D(a * i / cells, a * j / cells, 0));
        for (var i = 0; i < cells; i++)
        for (var j = 0; j < cells; j++)
        {
            solver.AddShell(new Shell(node[i, j], node[i + 1, j], node[i + 1, j + 1], Ec, nu, t));
            solver.AddShell(new Shell(node[i, j], node[i + 1, j + 1], node[i, j + 1], Ec, nu, t));
        }

        // Its edges held from lifting; one held along x, so it can shorten, and a corner across.
        for (var i = 0; i <= cells; i++)
        for (var j = 0; j <= cells; j++)
            if (i == 0 || j == 0 || i == cells || j == cells)
                solver.Restrain(node[i, j], i == 0, i == 0 && j == 0, true, false, false, false);

        var (edge, lift) = (new double[solver.NodeCount * 6], new double[solver.NodeCount * 6]);
        for (var j = 0; j <= cells; j++) edge[node[cells, j] * 6] = -1000 * a / cells * (j == 0 || j == cells ? 0.5 : 1);
        for (var i = 1; i < cells; i++)
        for (var j = 1; j < cells; j++)
            lift[node[i, j] * 6 + 2] = 100;

        var factored = solver.Factorise();
        var stress = solver.PrestressOf(factored, factored.Solve(edge));
        // Each triangle's stress is in its own axes: those along a cell's side carry it all, those along its diagonal half.
        Assert.InRange(stress.Nx.Where((_, index) => index % 2 == 0).Average(), -1010, -990);
        var d = Ec * Math.Pow(t, 3) / (12 * (1 - nu * nu));
        var expected = 4 * Math.PI * Math.PI * d / (a * a) / 1000;
        var alpha = Alpha(solver, stress, lift, factored.Solve(lift));
        Assert.InRange(alpha, 0.93 * expected, 1.07 * expected);
    }

    [Fact]
    public void TheImperfectionsAndStiffnessesAreTheCodes()
    {
        // 1/200 by 2/√h, between 2/3 and 1: full to 4 m, 1/300 from 9 m. In the US 0.002 and 0.001 for τb 1.0.
        Assert.Equal(1.0 / 200, SecondOrderAnalysis.ImperfectionOf(false, 3000), 9);
        Assert.Equal(2 / Math.Sqrt(6.25) / 200, SecondOrderAnalysis.ImperfectionOf(false, 6250), 9);
        Assert.Equal(1.0 / 300, SecondOrderAnalysis.ImperfectionOf(false, 20000), 9);
        Assert.Equal(0.003, SecondOrderAnalysis.ImperfectionOf(true, 6000));

        AnalyticalMaterial Of(AnalyticalMaterialKind kind, string quality) => new(quality, kind, quality, 1000, 10000, 1000, 0.2, 0, "");
        var (steel, concrete, timber, glulam, usTimber, brick) = (Of(AnalyticalMaterialKind.Steel, "S355"), Of(AnalyticalMaterialKind.Concrete, "C30/37"), Of(AnalyticalMaterialKind.Timber, "C24"),
            Of(AnalyticalMaterialKind.Timber, "GL24h"), Of(AnalyticalMaterialKind.Timber, "DF-L No. 2"), Of(AnalyticalMaterialKind.Masonry, "Masonry"));
        var column = AnalyticalMemberKind.Column;
        Assert.Equal(1, SecondOrderAnalysis.ShareOf(steel, false, column, null));
        Assert.Equal(0.8, SecondOrderAnalysis.ShareOf(steel, true, column, null));
        Assert.Equal(1.0 / 3, SecondOrderAnalysis.ShareOf(concrete, false, column, null), 9);
        Assert.Equal(1 / 1.3, SecondOrderAnalysis.ShareOf(timber, false, null, null), 9);
        Assert.Equal(1 / 1.25, SecondOrderAnalysis.ShareOf(glulam, false, null, null), 9);
        Assert.Equal(0.3625, SecondOrderAnalysis.ShareOf(usTimber, true, null, null), 9);
        Assert.Equal(0.5, SecondOrderAnalysis.ShareOf(brick, false, null, AnalyticalPanelKind.Wall));

        // ACI 318-19 Table 6.6.3.1.1(a).
        Assert.Equal(0.70, SecondOrderAnalysis.ShareOf(concrete, true, column, null));
        Assert.Equal(0.35, SecondOrderAnalysis.ShareOf(concrete, true, AnalyticalMemberKind.Beam, null));
        Assert.Equal(0.35, SecondOrderAnalysis.ShareOf(concrete, true, null, AnalyticalPanelKind.Wall));
        Assert.Equal(0.25, SecondOrderAnalysis.ShareOf(concrete, true, null, AnalyticalPanelKind.Floor));
    }

    // ---- whole frames -----------------------------------------------------------------------

    /// <summary>A concrete first floor 6 by 4 m on four steel columns, their feet fixed or pinned.</summary>
    private static BimDocument Table(ColumnType type, bool fixedFeet, ClearanceStandard? standard = null)
    {
        var document = BimDocument.CreateDefault();
        if (standard is { } code) document.ProjectInformation.ClearanceStandard = code;
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        document.AddType(type);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var columns = corners.Select(corner => new Column { TypeId = type.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        var concrete = document.TypesOf<FloorType>().First(each => each.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var floor = new Floor { TypeId = concrete.Id, LevelId = first.Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        foreach (var column in columns)
        {
            var pad = Footings.Fit(document, column);
            if (fixedFeet && pad is PadFooting held) held.Fixity = SupportFixity.Fixed;
            document.Add(pad);
        }

        return document;
    }

    private static ColumnType Uc(double depth, double width, double flange, double web) =>
        new("UC", ColumnShape.IBeam, width, depth) { Structural = true, StructuralMaterial = BeamMaterial.Steel, FlangeThickness = flange, WebThickness = web };

    private static SecondOrderAnalysis Analysed(BimDocument document, out StructuralAnalysis analysis)
    {
        var model = AnalyticalModel.Of(document);
        analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        return SecondOrderAnalysis.Run(document, analysis);
    }

    [Fact]
    public void TheEnergyRatioComesToTheStoreyFormula()
    {
        // H/V h/δ, with the leaning forces H = φ V: a stiff frame, gravity alone, δ its lean.
        var document = Table(Uc(254.1, 254.6, 14.2, 8.6), fixedFeet: true);
        var second = Analysed(document, out var analysis);
        var result = second.Results.First(each => each.Combination.Name.EndsWith("+x", StringComparison.Ordinal) && !each.SecondOrder);
        var height = analysis.Model.Members.First(member => member.Kind == AnalyticalMemberKind.Column).Length;

        // Bent both ways up its height a column loses 1.2 times what its chord does, so a frame like this comes to 0.82
        // of the formula: π²EI/L² over 12EI/L² - what AISC's RM of 0.85 is for. Bending a floor softens it a little more.
        Assert.InRange(result.Alpha, 0.78 * second.Imperfection * height / result.Lean, 0.95 * second.Imperfection * height / result.Lean);
        Assert.Equal(1 / (1 - 1 / result.Alpha), result.Amplifier, 9);
    }

    [Fact]
    public void FixedFeetStandUpToSwayBetterThanPinnedAndSecondOrderCarriesIntoTheChecks()
    {
        // 152 UC 23 on 3 m under a concrete floor: pinned at the foot, each column a cantilever down from the floor.
        var slender = Uc(152.4, 152.2, 6.8, 5.8);
        var document = Table(slender, fixedFeet: false);
        var pinned = Analysed(document, out var pinnedAnalysis);
        var held = Analysed(Table(slender, fixedFeet: true), out _);
        Assert.True(pinned.Worst!.Alpha < held.Worst!.Alpha / 2, $"pinned {pinned.Worst.Alpha:0.0}, fixed {held.Worst.Alpha:0.0}");

        // Sensitive, so it is taken to second order; amplified as far as αcr says, within a little.
        var worst = pinned.Worst;
        Assert.True(worst.Alpha < SecondOrderAnalysis.UkLimit, $"αcr {worst.Alpha:0.0}");
        Assert.True(worst.SecondOrder, "taken to second order");
        Assert.False(worst.Buckles);
        Assert.InRange(worst.Passes, 2, 39);

        // Gravity alone leans each of four ways; the checks say which.
        var checks = MemberStrength.Of(document, pinnedAnalysis, pinned);
        Assert.Contains(pinned.Results, each => each.Combination.Name.EndsWith(" +y", StringComparison.Ordinal));
        Assert.All(checks, check => Assert.DoesNotContain(check.Checks, each => each.Name == "Stability of the frame"));
        var column = pinnedAnalysis.Model.Members.First(member => member.Kind == AnalyticalMemberKind.Column);

        // Each column carries a quarter of the lean through its height, amplified: M = φ V / 4 x h x 1 / (1 - 1 / αcr).
        var gravityOnly = pinned.Results.First(each => each.Combination.Name.EndsWith(" +y", StringComparison.Ordinal) && each.SecondOrder);
        var plain = gravityOnly.Combination.Name[..^3];
        var weight = -pinnedAnalysis.TotalLoad(pinnedAnalysis.Loading.Combinations.First(each => each.Name == plain)).Z;
        var expected = pinned.Imperfection * weight / 4 * column.Length / 1000 * gravityOnly.Amplifier;

        // The floor's own bending is the same either way it leans: half the difference is the lean's.
        var stations = pinned.StationsOf(column);
        var (one, other) = (stations.Where(station => station.Combination.Name == plain + " +y").ToList(), stations.Where(station => station.Combination.Name == plain + " -y").ToList());
        var moment = one.Zip(other, (a, b) => Math.Max(Math.Abs(a.Forces.My - b.Forces.My), Math.Abs(a.Forces.Mz - b.Forces.Mz)) / 2).Max();
        Assert.InRange(moment, 0.75 * expected, 1.35 * expected);
    }

    [Fact]
    public void AFrameThatBucklesIsSaidToAndFailsEveryMember()
    {
        // Thin columns pinned at the foot: the floor's weight is more than they can stand up to.
        var thin = new ColumnType("Thin", ColumnShape.HollowRectangular, 60, 60) { Structural = true, StructuralMaterial = BeamMaterial.Steel, WallThickness = 3 };
        var document = Table(thin, fixedFeet: false);
        var second = Analysed(document, out var analysis);
        Assert.Contains(second.Results, each => each.Buckles);
        Assert.Equal(double.PositiveInfinity, second.Worst!.Movement);
        var checks = MemberStrength.Of(document, analysis, second);
        Assert.NotEmpty(checks);
        Assert.All(checks, check =>
        {
            Assert.Contains(check.Checks, each => each.Name == "Stability of the frame");
            Assert.False(check.Enough);
        });
    }

    [Fact]
    public void AHouseOfWallsStandsUpToSway()
    {
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        var second = Analysed(document, out _);
        Assert.NotEmpty(second.Results);
        // Its roof holds its walls' tops, so nothing leans out of its plane: first order is enough.
        Assert.DoesNotContain(second.Results, each => each.Buckles || each.SecondOrder);
        Assert.True(second.Worst!.Alpha > 10, $"αcr {second.Worst.Alpha:0.0}");
        Assert.Contains("1 in 2", second.Notes[0]);
    }

    [Fact]
    public void TheUsAsksMoreBeforeFirstOrderIsEnough()
    {
        // Between αcr 10 and 20 the UK stops at first order and the US does not; steel at 0.8 in the US.
        var column = Uc(203.2, 203.6, 11.0, 7.2);
        var uk = Analysed(Table(column, fixedFeet: false), out _);
        var us = Analysed(Table(column, fixedFeet: false, ClearanceStandard.Irc), out _);
        Assert.Equal(10, uk.Limit);
        Assert.Equal(20, us.Limit);
        Assert.Contains("0.003", us.Notes[0]);

        // Strength is by the factored combinations, not the allowable stress ones.
        Assert.DoesNotContain(us.Results, each => each.Combination.Name.StartsWith("ASD", StringComparison.Ordinal));
        Assert.All(us.Results.Where(each => each.Alpha < 20 && !each.Buckles), each => Assert.True(each.SecondOrder));
    }
}
