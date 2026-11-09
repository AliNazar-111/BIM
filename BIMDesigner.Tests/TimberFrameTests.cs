using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using static BIMDesigner.Core.Architecture.StructuralSolver;

namespace BIMDesigner.Tests;

/// <summary>Timber frame finished: walls that bend one way as studs do, what ties them down, their end studs, headers on their jacks, and what a lining does in a fire.</summary>
public class TimberFrameTests
{
    /// <summary>A panel on the flat, studs along its y, a mm long and b mm wide in a grid of triangles, loaded evenly; held along y = 0 and y = b, and along its other two edges too if asked.</summary>
    private static (StructuralSolver Solver, int[,] Grid, double[] Moved) Panel(double a, double b, int across, int along, double share, bool sidesHeld, bool mirrored = false)
    {
        const double e = 11000, nu = 0.3, t = 140, q = 0.001;
        var solver = new StructuralSolver();
        var grid = new int[across + 1, along + 1];
        for (var i = 0; i <= across; i++)
        for (var j = 0; j <= along; j++)
            grid[i, j] = solver.AddNode(new Point3D(a * i / across, b * j / along, 0));

        void Triangle(int first, int second, int third)
        {
            var shell = new Shell(first, second, third, e, nu, t);
            var (x, y, _) = solver.Axes(shell);
            var studs = new StructuralSolver.Vector3(0, 1, 0);
            solver.AddShell(share > 0 ? shell with { Framed = new Studs(studs.Dot(x), studs.Dot(y), share) } : shell);
        }

        for (var i = 0; i < across; i++)
        for (var j = 0; j < along; j++)
        {
            // Two ways of cutting a square, and first sides lying every way, so nothing rests on how a triangle happens to lie.
            if (mirrored ? (i + j) % 2 == 0 : true)
            {
                Triangle(grid[i, j], grid[i + 1, j], grid[i + 1, j + 1]);
                Triangle(grid[i, j], grid[i + 1, j + 1], grid[i, j + 1]);
            }
            else
            {
                Triangle(grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1]);
                Triangle(grid[i, j + 1], grid[i, j], grid[i + 1, j]);
            }
        }

        var load = new double[solver.NodeCount * 6];
        for (var i = 0; i <= across; i++)
        for (var j = 0; j <= along; j++)
        {
            var end = j == 0 || j == along;
            var side = i == 0 || i == across;
            if (end || (side && sidesHeld)) solver.Restrain(grid[i, j], true, true, true, false, false, true);
            else solver.Restrain(grid[i, j], true, true, false, false, false, true);
            load[grid[i, j] * 6 + 2] = -q * (a / across) * (b / along) * (i == 0 || i == across ? 0.5 : 1) * (j == 0 || j == along ? 0.5 : 1);
        }

        return (solver, grid, solver.Solve(new[] { load }).Displacements[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWallOfStudsBendsAsABeamUpThem(bool mirrored)
    {
        // Held only at its two ends, 3 m apart: 5 q L⁴ / 384 D, with D the share of the solid plate's along the studs. Whichever way the triangles lie.
        const double share = 0.1, length = 3000;
        var d = share * 11000 * Math.Pow(140, 3) / (12 * (1 - 0.3 * 0.3));
        var (_, grid, moved) = Panel(2400, length, 8, 12, share, false, mirrored);
        var expected = 5 * 0.001 * Math.Pow(length, 4) / (384 * d);
        Assert.InRange(-moved[grid[4, 6] * 6 + 2], 0.97 * expected, 1.03 * expected);
    }

    [Fact]
    public void HeldAllRoundItStillSpansUpItsStuds()
    {
        // 9 m long and 3 m high, its sides held too. An isotropic plate takes some to its sides; a frame of studs next to none, and at its middle is the beam of the hand sum to within a hair.
        const double share = 0.1, length = 3000;
        var d = share * 11000 * Math.Pow(140, 3) / (12 * (1 - 0.3 * 0.3));
        var (_, studs, framed) = Panel(9000, length, 24, 8, share, true);
        var expected = 5 * 0.001 * Math.Pow(length, 4) / (384 * d);
        Assert.InRange(-framed[studs[12, 4] * 6 + 2], 0.97 * expected, 1.03 * expected);

        // Near a side, 750 from it: an isotropic plate is well under its middle; the frame barely so.
        var (_, plate, solid) = Panel(9000, length, 24, 8, 0, true);
        double Share(int[,] grid, double[] moved) => moved[grid[2, 4] * 6 + 2] / moved[grid[12, 4] * 6 + 2];
        Assert.True(Share(plate, solid) < 0.92);
        Assert.True(Share(studs, framed) > Share(plate, solid) + 0.05);
    }

    [Fact]
    public void ItsMomentIsUpItsStuds()
    {
        // q L² / 8 = 0.001 × 3000² / 8 = 1125 N mm per mm, up the studs, and next to none across them.
        var (solver, _, moved) = Panel(2400, 3000, 8, 12, 0.1, false);
        var (mostUp, mostAcross) = (0.0, 0.0);
        foreach (var shell in solver.Shells)
        {
            var (x, y, _) = solver.Axes(shell);
            var (c, s) = (new StructuralSolver.Vector3(0, 1, 0).Dot(x), new StructuralSolver.Vector3(0, 1, 0).Dot(y));
            var m = solver.ShellMoments(shell, moved);
            mostUp = Math.Max(mostUp, Math.Abs(m[0] * c * c + m[1] * s * s + 2 * m[2] * c * s));
            mostAcross = Math.Max(mostAcross, Math.Abs(m[0] * s * s + m[1] * c * c - 2 * m[2] * c * s));
        }

        Assert.InRange(mostUp, 1125 * 0.93, 1125 * 1.05);
        Assert.True(mostAcross < 0.05 * mostUp);
    }

    [Fact]
    public void ASheathedWallIsAsStiffAsItsNailsLetIt()
    {
        // Kser = 420^1.5 × 3.1^0.8 / 30 = 709 N/mm a nail; round a 1200 by 2400 sheet at 150: 709 / (2 × 150 × (1/1200 + 1/2400)) = 1,892 N/mm; with 9 mm of OSB at G 1,080 in series, 1,583.
        Assert.InRange(TimberWallDesign.RackingStiffness(false), 1575, 1590);

        // As a solid 140 plate of C24 it was E t / 2(1 + ν), 592,000: some 370 times as stiff.
        Assert.True(11000 * 140 / 2.6 / TimberWallDesign.RackingStiffness(false) > 300);
    }

    [Fact]
    public void AWallOfStudsRacksOnItsNails()
    {
        // 6 m long, 3 m high, held along its foot, pushed along its top by 10 kN: in shear 10,000 × 3000 / (1,583 × 6000) = 3.16 mm, and in bending, its studs its chords, a percent more.
        const double e = 11000, nu = 0.3, t = 140, length = 6000, height = 3000, push = 10000;
        var solid = e * t / (1 - nu * nu);
        var shear = TimberWallDesign.RackingStiffness(false);
        var studs = new Studs(0, 0, 0.1, e * 38 * 140 / 400 / solid, 3000 * 9 / solid, shear / solid);
        var solver = new StructuralSolver();
        const int across = 12, along = 6;
        var grid = new int[across + 1, along + 1];
        for (var i = 0; i <= across; i++)
        for (var j = 0; j <= along; j++)
            grid[i, j] = solver.AddNode(new Point3D(length * i / across, height * j / along, 0));
        void Triangle(int first, int second, int third)
        {
            var shell = new Shell(first, second, third, e, nu, t);
            var (x, y, _) = solver.Axes(shell);
            var up = new StructuralSolver.Vector3(0, 1, 0);
            solver.AddShell(shell with { Framed = studs with { Cos = up.Dot(x), Sin = up.Dot(y) } });
        }

        for (var i = 0; i < across; i++)
        for (var j = 0; j < along; j++)
        {
            Triangle(grid[i, j], grid[i + 1, j], grid[i + 1, j + 1]);
            Triangle(grid[i, j], grid[i + 1, j + 1], grid[i, j + 1]);
        }

        var load = new double[solver.NodeCount * 6];
        for (var i = 0; i <= across; i++)
        for (var j = 0; j <= along; j++)
        {
            solver.Restrain(grid[i, j], j == 0, j == 0, true, true, true, false);
            if (j == along) load[grid[i, j] * 6] = push / (across + 1);
        }

        var moved = solver.Solve(new[] { load }).Displacements[0];
        var top = Enumerable.Range(0, across + 1).Average(i => moved[grid[i, along] * 6]);
        var byShear = push * height / (shear * length);
        var byBending = push * Math.Pow(height, 3) / (3 * e * (38.0 * 140 / 400) * Math.Pow(length, 3) / 12);
        Assert.InRange(top, 0.95 * (byShear + byBending), 1.08 * (byShear + byBending));
    }

    [Fact]
    public void AnIsotropicPlateIsWhatItWas()
    {
        var plain = new Shell(0, 1, 2, 30000, 0.2, 200);
        var d = 30000 * Math.Pow(200, 3) / (12 * (1 - 0.04));
        var matrix = BendingStiffness(plain);
        Assert.Equal(d, matrix[0, 0], 3);
        Assert.Equal(d * 0.2, matrix[0, 1], 3);
        Assert.Equal(d * 0.4, matrix[2, 2], 3);
    }
}
