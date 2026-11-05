using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using SolverBeam = BIMDesigner.Core.Architecture.StructuralSolver.Beam;
using Shell = BIMDesigner.Core.Architecture.StructuralSolver.Shell;
using Vector3 = BIMDesigner.Core.Architecture.StructuralSolver.Vector3;

namespace BIMDesigner.Tests;

/// <summary>
/// The solver against what is known (round 25): a cantilever's deflection and turn, a simply supported
/// beam's under a uniform load and its moment, pinned ends, square plates simply supported and clamped
/// against Timoshenko's coefficients, and the analytical model of a house and of a frame analysed, their
/// loads and reactions in balance.
/// </summary>
public class StructuralSolverTests
{
    private const double E = 210000, Iy = 1e8, Iz = 4e7, A = 5000;

    private static (StructuralSolver Solver, List<int> Nodes) Line(int pieces, double length, bool pinnedEnds = false)
    {
        var solver = new StructuralSolver();
        var nodes = Enumerable.Range(0, pieces + 1).Select(i => solver.AddNode(new Point3D(length * i / pieces, 0, 0))).ToList();
        for (var i = 0; i < pieces; i++)
            solver.AddBeam(new SolverBeam(nodes[i], nodes[i + 1], E, 81000, A, Iy, Iz, 1e6, new Vector3(0, 1, 0), pinnedEnds && i == 0, pinnedEnds && i == pieces - 1));
        return (solver, nodes);
    }

    [Fact]
    public void ACantileverDeflectsAsPlOverThreeEi()
    {
        foreach (var pieces in new[] { 1, 5 })
        {
            var (solver, nodes) = Line(pieces, 3000);
            solver.Restrain(nodes[0], true, true, true, true, true, true);
            var load = new double[solver.NodeCount * 6];
            load[nodes[^1] * 6 + 2] = -10000;
            load[nodes[^1] * 6 + 1] = 4000;
            var u = solver.Solve(new[] { load }).Displacements[0];

            Assert.Equal(-10000 * Math.Pow(3000, 3) / (3 * E * Iy), u[nodes[^1] * 6 + 2], 6);
            Assert.Equal(4000 * Math.Pow(3000, 3) / (3 * E * Iz), u[nodes[^1] * 6 + 1], 6);

            // Its tip turns PL²/2EI: down at the tip is a turn about y one way.
            Assert.Equal(10000.0 * 3000 * 3000 / (2 * E * Iy), u[nodes[^1] * 6 + 4], 9);

            // The wall holds it back with the load and the moment PL.
            var reaction = solver.InternalForces(u);
            Assert.Equal(10000, reaction[nodes[0] * 6 + 2], 3);
            Assert.Equal(-10000 * 3000.0, reaction[nodes[0] * 6 + 4], 0);
        }
    }

    [Fact]
    public void ASimplySupportedBeamUnderAUniformLoad()
    {
        var (solver, nodes) = Line(4, 6000);
        solver.Restrain(nodes[0], true, true, true, true, false, false);
        solver.Restrain(nodes[^1], false, true, true, false, false, false);
        var w = new Vector3(0, 0, -5);
        var load = new double[solver.NodeCount * 6];
        var locals = new List<double[]>();
        for (var b = 0; b < solver.Beams.Count; b++)
        {
            var (local, global) = solver.BeamUniformLoad(solver.Beams[b], w);
            locals.Add(local);
            for (var i = 0; i < 6; i++)
            {
                load[solver.Beams[b].I * 6 + i] += global[i];
                load[solver.Beams[b].J * 6 + i] += global[6 + i];
            }
        }

        var u = solver.Solve(new[] { load }).Displacements[0];
        Assert.Equal(-5 * 5 * Math.Pow(6000, 4) / (384 * E * Iy), u[nodes[2] * 6 + 2], 6);

        // At midspan, wL²/8 - the end of the second beam.
        var forces = solver.BeamEndForces(solver.Beams[1], u, locals[1]);
        Assert.Equal(5 * 6000.0 * 6000 / 8, Math.Abs(forces[10]), 0);

        // Its ends pinned in the beams themselves, between two fixed walls, it is the same beam.
        var (pinned, ends) = Line(4, 6000, pinnedEnds: true);
        pinned.Restrain(ends[0], true, true, true, true, true, true);
        pinned.Restrain(ends[^1], true, true, true, true, true, true);
        var pinnedLoad = new double[pinned.NodeCount * 6];
        for (var b = 0; b < pinned.Beams.Count; b++)
        {
            var (_, global) = pinned.BeamUniformLoad(pinned.Beams[b], w);
            for (var i = 0; i < 6; i++)
            {
                pinnedLoad[pinned.Beams[b].I * 6 + i] += global[i];
                pinnedLoad[pinned.Beams[b].J * 6 + i] += global[6 + i];
            }
        }

        var up = pinned.Solve(new[] { pinnedLoad }).Displacements[0];
        Assert.Equal(-5 * 5 * Math.Pow(6000, 4) / (384 * E * Iy), up[ends[2] * 6 + 2], 6);
    }

    /// <summary>A square plate a mm across in a grid of so many pieces, held round its edge - simply, or clamped.</summary>
    private static double PlateCentre(double a, int pieces, bool clamped)
    {
        const double e = 30000, nu = 0.3, t = 200, q = 0.005;
        var solver = new StructuralSolver();
        var grid = new int[pieces + 1, pieces + 1];
        for (var i = 0; i <= pieces; i++)
        for (var j = 0; j <= pieces; j++)
            grid[i, j] = solver.AddNode(new Point3D(a * i / pieces, a * j / pieces, 0));
        for (var i = 0; i < pieces; i++)
        for (var j = 0; j < pieces; j++)
        {
            solver.AddShell(new Shell(grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], e, nu, t));
            solver.AddShell(new Shell(grid[i, j], grid[i + 1, j + 1], grid[i, j + 1], e, nu, t));
        }

        var load = new double[solver.NodeCount * 6];
        var piece = a / pieces;
        for (var i = 0; i <= pieces; i++)
        for (var j = 0; j <= pieces; j++)
        {
            var edge = i == 0 || j == 0 || i == pieces || j == pieces;
            if (edge) solver.Restrain(grid[i, j], true, true, true, clamped, clamped, true);
            var share = (i == 0 || i == pieces ? 0.5 : 1) * (j == 0 || j == pieces ? 0.5 : 1);
            load[grid[i, j] * 6 + 2] = -q * piece * piece * share;
        }

        var u = solver.Solve(new[] { load }).Displacements[0];
        var d = e * t * t * t / (12 * (1 - nu * nu));
        return -u[grid[pieces / 2, pieces / 2] * 6 + 2] / (q * Math.Pow(a, 4) / d);
    }

    [Fact]
    public void SquarePlatesDeflectAsTimoshenkosTablesSay()
    {
        // Simply supported: 0.00406 q a⁴ / D; clamped: 0.00126 (Timoshenko and Woinowsky-Krieger, Tables 8 and 35).
        Assert.InRange(PlateCentre(4000, 16, clamped: false), 0.00406 * 0.97, 0.00406 * 1.03);
        Assert.InRange(PlateCentre(4000, 16, clamped: true), 0.00126 * 0.95, 0.00126 * 1.05);
    }

    [Fact]
    public void ALooseStructureIsSaid()
    {
        var (solver, nodes) = Line(2, 3000);
        var load = new double[solver.NodeCount * 6];
        load[nodes[1] * 6 + 2] = -1000;
        var solution = solver.Solve(new[] { load });
        Assert.NotEmpty(solution.Problems);
        Assert.NotEmpty(solution.Unstable);
    }

    [Fact]
    public void AHouseAndAFrameAreAnalysedTheirLoadsAndReactionsInBalance()
    {
        foreach (var document in new[] { HouseOfBearingWalls(), SteelFrame() })
        {
            var model = AnalyticalModel.Of(document);
            var loading = StructuralLoading.Of(document, model);
            var analysis = StructuralAnalysis.Run(document, model, loading);
            Assert.Empty(analysis.Problems);

            foreach (var loadCase in loading.Cases)
            {
                var load = analysis.TotalLoad(loadCase);
                var reaction = analysis.TotalReaction(loadCase);
                var scale = Math.Max(1, load.Length);
                Assert.True((load + reaction).Length < 0.01 * scale, $"{loadCase.Name}: loads {load} against reactions {reaction}");
            }

            // Its own weight is carried down: what holds it up is what it weighs.
            var selfWeight = loading.Cases.First(loadCase => loadCase.Nature == LoadNature.SelfWeight);
            Assert.True(analysis.TotalLoad(selfWeight).Z < -1);
        }

        // Pinned at its feet as at its beams' ends, nothing stops the frame swaying: it is said to be free to move.
        var loose = SteelFrame(fixedBases: false);
        var looseModel = AnalyticalModel.Of(loose);
        Assert.Contains(StructuralAnalysis.Run(loose, looseModel, StructuralLoading.Of(loose, looseModel)).Problems, problem => problem.Contains("free to move", StringComparison.Ordinal));
    }

    private static BimDocument HouseOfBearingWalls()
    {
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        return document;
    }

    private static BimDocument SteelFrame(bool fixedBases = true)
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var columnType = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var beamType = document.TypesOf<BeamType>().First(type => type.Section == BeamSection.IBeam);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000) };
        var columns = corners.Select(corner => new Column { TypeId = columnType.Id, LevelId = ground.Id, TopLevelId = first.Id, Location = corner, Structural = true }).ToList();
        foreach (var column in columns) document.Add(column);
        for (var i = 0; i < 4; i++) document.Add(new Beam { TypeId = beamType.Id, LevelId = first.Id, Start = corners[i], End = corners[(i + 1) % 4] });
        foreach (var column in columns)
        {
            var pad = Footings.Fit(document, column);
            if (fixedBases && pad is PadFooting held) held.Fixity = SupportFixity.Fixed;
            document.Add(pad);
        }

        return document;
    }
}
