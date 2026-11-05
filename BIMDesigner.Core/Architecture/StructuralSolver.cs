using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A linear elastic finite element solver for frames and plates in three dimensions: nodes with six
/// freedoms each - three movements, three turns - joined by beams and by flat shell triangles, held by
/// restraints and springs, loaded at the nodes and along the beams. Millimetres and newtons throughout:
/// moduli in N/mm², loads in N and N/mm, moments in N mm.
///
/// A beam is the Euler-Bernoulli one, stretching, twisting and bending both ways, an end that is pinned
/// let turn by condensing its bending out. A shell triangle is a constant strain triangle in its plane
/// and Batoz, Bathe and Ho's discrete Kirchhoff triangle (DKT, 1980) across it, with a small stiffness
/// about its normal so that a node only plates meet at is not free to spin. The equations are numbered
/// in reverse Cuthill-McKee order and solved by Bathe's active column (skyline) LDL^T factorisation,
/// once for every load case.
/// </summary>
public sealed class StructuralSolver
{
    private readonly List<Point3D> _nodes = new();
    private readonly List<Beam> _beams = new();
    private readonly List<Shell> _shells = new();
    private readonly Dictionary<int, bool[]> _restraints = new();
    private readonly List<(int Node, int Freedom, double Stiffness)> _springs = new();

    /// <summary>A beam between two nodes: its section and material, the way its section's y axis lies, and which ends are pinned.</summary>
    public sealed record Beam(int I, int J, double E, double G, double A, double Iy, double Iz, double J0, Vector3 LocalY, bool PinnedI, bool PinnedJ);

    /// <summary>A flat shell triangle: its three nodes, its material and thickness.</summary>
    public sealed record Shell(int I, int J, int K, double E, double Poisson, double Thickness, double Bending = 1);

    /// <summary>A plain three dimensional vector.</summary>
    public readonly record struct Vector3(double X, double Y, double Z)
    {
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator *(Vector3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vector3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vector3 Cross(Vector3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(Dot(this));
        public Vector3 Unit => Length < 1e-12 ? this : this * (1 / Length);
        public static Vector3 Of(Point3D p) => new(p.X, p.Y, p.Z);

        public override string ToString() => FormattableString.Invariant($"({X:0.###}, {Y:0.###}, {Z:0.###})");
    }

    public int NodeCount => _nodes.Count;

    public IReadOnlyList<Beam> Beams => _beams;

    public IReadOnlyList<Shell> Shells => _shells;

    public Point3D NodeAt(int node) => _nodes[node];

    public int AddNode(Point3D at)
    {
        _nodes.Add(at);
        return _nodes.Count - 1;
    }

    public int AddBeam(Beam beam)
    {
        _lines = null;
        _beams.Add(beam);
        return _beams.Count - 1;
    }

    public int AddShell(Shell shell)
    {
        _shapes = null;
        _shells.Add(shell);
        return _shells.Count - 1;
    }

    /// <summary>Holds a node's freedoms: x, y, z movements then turns about x, y, z.</summary>
    public void Restrain(int node, params bool[] freedoms)
    {
        if (!_restraints.TryGetValue(node, out var held)) _restraints[node] = held = new bool[6];
        for (var i = 0; i < 6 && i < freedoms.Length; i++) held[i] |= freedoms[i];
    }

    public bool IsRestrained(int node, int freedom) => _restraints.TryGetValue(node, out var held) && held[freedom];

    /// <summary>A spring holding a node's freedom, N/mm or N mm per radian.</summary>
    public void AddSpring(int node, int freedom, double stiffness) => _springs.Add((node, freedom, stiffness));

    public IReadOnlyList<(int Node, int Freedom, double Stiffness)> Springs => _springs;

    // ---- element stiffness ----------------------------------------------------------------

    /// <summary>A beam's axes: along it, its y and its z, as the rows of its rotation.</summary>
    public (Vector3 X, Vector3 Y, Vector3 Z, double Length) Axes(Beam beam)
    {
        var a = Vector3.Of(_nodes[beam.I]);
        var b = Vector3.Of(_nodes[beam.J]);
        var x = (b - a).Unit;
        var y = beam.LocalY - x * beam.LocalY.Dot(x);
        if (y.Length < 1e-6)
        {
            // Its y given along it: take one square to it, horizontal where it can be.
            y = Math.Abs(x.Z) < 0.9 ? new Vector3(0, 0, 1).Cross(x) : new Vector3(0, 1, 0).Cross(x);
        }

        y = y.Unit;
        return (x, y, x.Cross(y), (b - a).Length);
    }

    /// <summary>A beam's stiffness in its own axes, its pinned ends' bending condensed out.</summary>
    public static double[,] BeamLocal(Beam beam, double length)
    {
        var k = new double[12, 12];
        var (e, g, a, iy, iz, j, l) = (beam.E, beam.G, beam.A, beam.Iy, beam.Iz, beam.J0, length);
        void Set(int r, int c, double v)
        {
            k[r, c] += v;
            if (r != c) k[c, r] += v;
        }

        var ea = e * a / l;
        Set(0, 0, ea); Set(6, 6, ea); Set(0, 6, -ea);
        var gj = g * j / l;
        Set(3, 3, gj); Set(9, 9, gj); Set(3, 9, -gj);

        // Bending in the x-y plane: v and the turn about z.
        var z12 = 12 * e * iz / (l * l * l);
        var z6 = 6 * e * iz / (l * l);
        var z4 = 4 * e * iz / l;
        var z2 = 2 * e * iz / l;
        Set(1, 1, z12); Set(7, 7, z12); Set(1, 7, -z12);
        Set(1, 5, z6); Set(1, 11, z6); Set(7, 5, -z6); Set(7, 11, -z6);
        Set(5, 5, z4); Set(11, 11, z4); Set(5, 11, z2);

        // Bending in the x-z plane: w and the turn about y.
        var y12 = 12 * e * iy / (l * l * l);
        var y6 = 6 * e * iy / (l * l);
        var y4 = 4 * e * iy / l;
        var y2 = 2 * e * iy / l;
        Set(2, 2, y12); Set(8, 8, y12); Set(2, 8, -y12);
        Set(2, 4, -y6); Set(2, 10, -y6); Set(8, 4, y6); Set(8, 10, y6);
        Set(4, 4, y4); Set(10, 10, y4); Set(4, 10, y2);

        var released = new List<int>();
        if (beam.PinnedI) released.AddRange(new[] { 4, 5 });
        if (beam.PinnedJ) released.AddRange(new[] { 10, 11 });
        return released.Count == 0 ? k : Condense(k, released);
    }

    /// <summary>A stiffness with some freedoms condensed out: K_aa - K_ab K_bb^-1 K_ba, their rows and columns left empty.</summary>
    private static double[,] Condense(double[,] k, IReadOnlyList<int> released)
    {
        var n = k.GetLength(0);
        var m = released.Count;
        var kbb = new double[m, m];
        for (var i = 0; i < m; i++)
        for (var j = 0; j < m; j++)
            kbb[i, j] = k[released[i], released[j]];
        var inverse = Invert(kbb);
        var result = new double[n, n];
        for (var r = 0; r < n; r++)
        for (var c = 0; c < n; c++)
        {
            if (released.Contains(r) || released.Contains(c)) continue;
            var sum = 0.0;
            for (var i = 0; i < m; i++)
            for (var j = 0; j < m; j++)
                sum += k[r, released[i]] * inverse[i, j] * k[released[j], c];
            result[r, c] = k[r, c] - sum;
        }

        return result;
    }

    /// <summary>The loads a pinned end cannot carry moved to the freedoms it can: F_a - K_ab K_bb^-1 F_b.</summary>
    public static double[] CondenseLoad(Beam beam, double length, double[] load)
    {
        var released = new List<int>();
        if (beam.PinnedI) released.AddRange(new[] { 4, 5 });
        if (beam.PinnedJ) released.AddRange(new[] { 10, 11 });
        if (released.Count == 0) return load;

        var full = BeamLocal(beam with { PinnedI = false, PinnedJ = false }, length);
        var m = released.Count;
        var kbb = new double[m, m];
        for (var i = 0; i < m; i++)
        for (var j = 0; j < m; j++)
            kbb[i, j] = full[released[i], released[j]];
        var inverse = Invert(kbb);
        var result = (double[])load.Clone();
        for (var r = 0; r < 12; r++)
        {
            if (released.Contains(r))
            {
                result[r] = 0;
                continue;
            }

            for (var i = 0; i < m; i++)
            for (var j = 0; j < m; j++)
                result[r] -= full[r, released[i]] * inverse[i, j] * load[released[j]];
        }

        return result;
    }

    private static double[,] Invert(double[,] a)
    {
        var n = a.GetLength(0);
        var m = new double[n, 2 * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n + i] = 1;
        }

        for (var c = 0; c < n; c++)
        {
            var pivot = Enumerable.Range(c, n - c).OrderByDescending(r => Math.Abs(m[r, c])).First();
            if (Math.Abs(m[pivot, c]) < 1e-30) continue;
            if (pivot != c)
                for (var j = 0; j < 2 * n; j++)
                    (m[c, j], m[pivot, j]) = (m[pivot, j], m[c, j]);
            var d = m[c, c];
            for (var j = 0; j < 2 * n; j++) m[c, j] /= d;
            for (var r = 0; r < n; r++)
            {
                if (r == c || m[r, c] == 0) continue;
                var f = m[r, c];
                for (var j = 0; j < 2 * n; j++) m[r, j] -= f * m[c, j];
            }
        }

        var inverse = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            inverse[i, j] = m[i, n + j];
        return inverse;
    }

    /// <summary>A triangle's axes: along its first side, across it in its plane, and its normal.</summary>
    public (Vector3 X, Vector3 Y, Vector3 Z) Axes(Shell shell)
    {
        var a = Vector3.Of(_nodes[shell.I]);
        var b = Vector3.Of(_nodes[shell.J]);
        var c = Vector3.Of(_nodes[shell.K]);
        var x = (b - a).Unit;
        var z = (b - a).Cross(c - a).Unit;
        return (x, z.Cross(x), z);
    }

    /// <summary>
    /// A shell triangle's stiffness in its own axes, 18 freedoms - at each corner u, v, w and the
    /// turns about x, y and z: the constant strain triangle in its plane, the DKT across it, and a
    /// small stiffness about its normal.
    /// </summary>
    public static double[,] ShellLocal(Shell shell, (double X, double Y)[] p)
    {
        var k = new double[18, 18];
        var (x1, y1) = p[0];
        var (x2, y2) = p[1];
        var (x3, y3) = p[2];
        var twiceArea = (x2 - x1) * (y3 - y1) - (x3 - x1) * (y2 - y1);
        var area = twiceArea / 2;
        var (e, nu, t) = (shell.E, shell.Poisson, shell.Thickness);

        // In its plane: the constant strain triangle.
        var dm = e / (1 - nu * nu);
        var dMembrane = new[,] { { dm, dm * nu, 0 }, { dm * nu, dm, 0 }, { 0, 0, dm * (1 - nu) / 2 } };
        var (y23, y31, y12) = (y2 - y3, y3 - y1, y1 - y2);
        var (x32, x13, x21) = (x3 - x2, x1 - x3, x2 - x1);
        var bm = new double[3, 6]
        {
            { y23, 0, y31, 0, y12, 0 },
            { 0, x32, 0, x13, 0, x21 },
            { x32, y23, x13, y31, x21, y12 }
        };
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 6; c++)
            bm[r, c] /= twiceArea;
        var membrane = Multiply(Transpose(bm), Multiply(dMembrane, bm), t * area);
        int[] mapMembrane = { 0, 1, 6, 7, 12, 13 };
        for (var r = 0; r < 6; r++)
        for (var c = 0; c < 6; c++)
            k[mapMembrane[r], mapMembrane[c]] += membrane[r, c];

        // Across it: the discrete Kirchhoff triangle, by the three mid-side points.
        var db = shell.Bending * e * t * t * t / (12 * (1 - nu * nu));
        var dBending = new[,] { { db, db * nu, 0 }, { db * nu, db, 0 }, { 0, 0, db * (1 - nu) / 2 } };
        var bending = new double[9, 9];
        foreach (var (xi, eta) in new[] { (0.5, 0.0), (0.5, 0.5), (0.0, 0.5) })
        {
            var b = DktB(p, xi, eta);
            var kb = Multiply(Transpose(b), Multiply(dBending, b), twiceArea / 6);
            for (var r = 0; r < 9; r++)
            for (var c = 0; c < 9; c++)
                bending[r, c] += kb[r, c];
        }

        int[] mapBending = { 2, 3, 4, 8, 9, 10, 14, 15, 16 };
        for (var r = 0; r < 9; r++)
        for (var c = 0; c < 9; c++)
            k[mapBending[r], mapBending[c]] += bending[r, c];

        // About its normal: a little, so that a node only plates meet at does not spin.
        var drill = 1e-4 * e * t * area / 3;
        foreach (var i in new[] { 5, 11, 17 }) k[i, i] += drill;
        return k;
    }

    /// <summary>
    /// The DKT's curvatures from its nine freedoms - w, the turn about x (dw/dy) and about y (-dw/dx) at
    /// each corner - at a point (xi, eta) of it: Batoz, Bathe and Ho's B matrix.
    /// </summary>
    public static double[,] DktB((double X, double Y)[] p, double xi, double eta)
    {
        var (x1, y1) = p[0];
        var (x2, y2) = p[1];
        var (x3, y3) = p[2];
        double[] xs = { x2 - x3, x3 - x1, x1 - x2 };
        double[] ys = { y2 - y3, y3 - y1, y1 - y2 };
        var P = new double[3];
        var q = new double[3];
        var tt = new double[3];
        var r = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var l2 = xs[i] * xs[i] + ys[i] * ys[i];
            P[i] = -6 * xs[i] / l2;
            tt[i] = -6 * ys[i] / l2;
            q[i] = 3 * xs[i] * ys[i] / l2;
            r[i] = 3 * ys[i] * ys[i] / l2;
        }

        // Sides 4, 5, 6 are 23, 31, 12.
        var (P4, P5, P6) = (P[0], P[1], P[2]);
        var (q4, q5, q6) = (q[0], q[1], q[2]);
        var (t4, t5, t6) = (tt[0], tt[1], tt[2]);
        var (r4, r5, r6) = (r[0], r[1], r[2]);
        var a = 1 - 2 * xi;
        var bb = 1 - 2 * eta;

        double[] hxXi =
        {
            P6 * a + (P5 - P6) * eta, q6 * a - (q5 + q6) * eta, -4 + 6 * (xi + eta) + r6 * a - eta * (r5 + r6),
            -P6 * a + eta * (P4 + P6), q6 * a - eta * (q6 - q4), -2 + 6 * xi + r6 * a + eta * (r4 - r6),
            -eta * (P5 + P4), eta * (q4 - q5), -eta * (r5 - r4)
        };
        double[] hyXi =
        {
            t6 * a + eta * (t5 - t6), 1 + r6 * a - eta * (r5 + r6), -q6 * a + eta * (q5 + q6),
            -t6 * a + eta * (t4 + t6), -1 + r6 * a + eta * (r4 - r6), -q6 * a - eta * (q4 - q6),
            -eta * (t4 + t5), eta * (r4 - r5), -eta * (q4 - q5)
        };
        double[] hxEta =
        {
            -P5 * bb - xi * (P6 - P5), q5 * bb - xi * (q5 + q6), -4 + 6 * (xi + eta) + r5 * bb - xi * (r5 + r6),
            xi * (P4 + P6), xi * (q4 - q6), -xi * (r6 - r4),
            P5 * bb - xi * (P4 + P5), q5 * bb + xi * (q4 - q5), -2 + 6 * eta + r5 * bb + xi * (r4 - r5)
        };
        double[] hyEta =
        {
            -t5 * bb - xi * (t6 - t5), 1 + r5 * bb - xi * (r5 + r6), -q5 * bb + xi * (q5 + q6),
            xi * (t4 + t6), xi * (r4 - r6), -xi * (q4 - q6),
            t5 * bb - xi * (t4 + t5), -1 + r5 * bb + xi * (r4 - r5), -q5 * bb - xi * (q4 - q5)
        };

        var (x31, y31v, x12, y12v) = (x3 - x1, y3 - y1, x1 - x2, y1 - y2);
        var twiceArea = x31 * y12v - x12 * y31v;
        var bmat = new double[3, 9];
        for (var i = 0; i < 9; i++)
        {
            bmat[0, i] = (y31v * hxXi[i] + y12v * hxEta[i]) / twiceArea;
            bmat[1, i] = (-x31 * hyXi[i] - x12 * hyEta[i]) / twiceArea;
            bmat[2, i] = (-x31 * hxXi[i] - x12 * hxEta[i] + y31v * hyXi[i] + y12v * hyEta[i]) / twiceArea;
        }

        return bmat;
    }

    private static double[,] Transpose(double[,] a)
    {
        var t = new double[a.GetLength(1), a.GetLength(0)];
        for (var i = 0; i < a.GetLength(0); i++)
        for (var j = 0; j < a.GetLength(1); j++)
            t[j, i] = a[i, j];
        return t;
    }

    private static double[,] Multiply(double[,] a, double[,] b, double scale = 1)
    {
        var c = new double[a.GetLength(0), b.GetLength(1)];
        for (var i = 0; i < a.GetLength(0); i++)
        for (var k = 0; k < a.GetLength(1); k++)
        {
            var aik = a[i, k];
            if (aik == 0) continue;
            for (var j = 0; j < b.GetLength(1); j++) c[i, j] += aik * b[k, j];
        }

        if (scale != 1)
            for (var i = 0; i < c.GetLength(0); i++)
            for (var j = 0; j < c.GetLength(1); j++)
                c[i, j] *= scale;
        return c;
    }

    /// <summary>Turns a 3 by 3 rotation (its rows the element's axes) into the block diagonal transformation for n nodes.</summary>
    private static double[,] Rotation(Vector3 x, Vector3 y, Vector3 z, int nodes)
    {
        var n = 6 * nodes;
        var t = new double[n, n];
        double[,] r = { { x.X, x.Y, x.Z }, { y.X, y.Y, y.Z }, { z.X, z.Y, z.Z } };
        for (var block = 0; block < 2 * nodes; block++)
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            t[3 * block + i, 3 * block + j] = r[i, j];
        return t;
    }

    /// <summary>A beam's stiffness in the building's axes, and the transformation that took it there.</summary>
    public (double[,] K, double[,] T, double Length) BeamGlobal(Beam beam)
    {
        var (x, y, z, length) = Axes(beam);
        var t = Rotation(x, y, z, 2);
        return (Multiply(Transpose(t), Multiply(BeamLocal(beam, length), t)), t, length);
    }

    /// <summary>A shell's stiffness in the building's axes, and the transformation that took it there.</summary>
    public (double[,] K, double[,] T, (double X, double Y)[] Local) ShellGlobal(Shell shell)
    {
        var (x, y, z) = Axes(shell);
        var origin = Vector3.Of(_nodes[shell.I]);
        var local = new[] { shell.I, shell.J, shell.K }.Select(n => (Vector3.Of(_nodes[n]) - origin)).Select(v => (v.Dot(x), v.Dot(y))).ToArray();
        var t = Rotation(x, y, z, 3);
        return (Multiply(Transpose(t), Multiply(ShellLocal(shell, local), t)), t, local);
    }

    // ---- assembly and solution -------------------------------------------------------------

    /// <summary>What a solution comes to: each load's movements of every node, six to a node; what it found wrong.</summary>
    public sealed record Solution(IReadOnlyList<double[]> Displacements, IReadOnlyList<string> Problems, IReadOnlyList<(int Node, int Freedom)> Unstable);

    /// <summary>
    /// Solves for each load - a vector of six forces per node - the movement of every node: the
    /// structure's stiffness assembled once, factorised once, and each load put through it.
    /// </summary>
    public Solution Solve(IReadOnlyList<double[]> loads)
    {
        var factored = Factorise();
        return new Solution(loads.Select(factored.Solve).ToList(), factored.Problems, factored.Unstable);
    }

    /// <summary>
    /// The structure's stiffness assembled and factorised, kept to put loads through - each element's
    /// stiffness taken at its share of what it is, where a share is given: a cracked section's, say.
    /// </summary>
    public Factored Factorise(IReadOnlyList<double>? beamShares = null, IReadOnlyList<double>? shellShares = null)
    {
        var problems = new List<string>();
        var n = _nodes.Count;
        var beams = _beams.Select((beam, i) => beamShares is null ? beam : beam with { E = beam.E * beamShares[i], G = beam.G * beamShares[i] }).ToList();
        var shells = _shells.Select((shell, i) => shellShares is null ? shell : shell with { E = shell.E * shellShares[i] }).ToList();

        // Which nodes each element joins, for the ordering.
        var neighbours = Enumerable.Range(0, n).Select(_ => new HashSet<int>()).ToArray();
        void Join(params int[] joined)
        {
            foreach (var a in joined)
            foreach (var b in joined)
                if (a != b) neighbours[a].Add(b);
        }

        foreach (var beam in _beams) Join(beam.I, beam.J);
        foreach (var shell in _shells) Join(shell.I, shell.J, shell.K);
        var order = ReverseCuthillMcKee(neighbours);

        // Each free freedom an equation, node by node in that order.
        var equation = new int[n * 6];
        Array.Fill(equation, -1);
        var count = 0;
        foreach (var node in order)
            for (var f = 0; f < 6; f++)
                if (!IsRestrained(node, f)) equation[node * 6 + f] = count++;

        // The elements' stiffnesses in the building's axes, and where each lands.
        var elements = new List<(int[] Dofs, double[,] K)>();
        foreach (var beam in beams)
        {
            var (k, _, _) = BeamGlobal(beam);
            elements.Add((Dofs(beam.I, beam.J), k));
        }

        foreach (var shell in shells)
        {
            var (k, _, _) = ShellGlobal(shell);
            elements.Add((Dofs(shell.I, shell.J, shell.K), k));
        }

        // The skyline: how far up each column reaches.
        var top = Enumerable.Range(0, count).ToArray();
        foreach (var (dofs, _) in elements)
        {
            var eqs = dofs.Select(d => equation[d]).Where(e => e >= 0).ToList();
            if (eqs.Count == 0) continue;
            var least = eqs.Min();
            foreach (var e in eqs) top[e] = Math.Min(top[e], least);
        }

        var address = new long[count + 1];
        for (var e = 0; e < count; e++) address[e + 1] = address[e] + (e - top[e] + 1);
        var a = new double[address[count]];
        void Add(int row, int column, double value)
        {
            if (row > column) (row, column) = (column, row);
            a[address[column] + (column - row)] += value;
        }

        foreach (var (dofs, k) in elements)
            for (var i = 0; i < dofs.Length; i++)
            {
                var ei = equation[dofs[i]];
                if (ei < 0) continue;
                for (var j = i; j < dofs.Length; j++)
                {
                    var ej = equation[dofs[j]];
                    if (ej < 0 || k[i, j] == 0) continue;
                    Add(ei, ej, k[i, j]);
                }
            }

        foreach (var (node, freedom, stiffness) in _springs)
            if (equation[node * 6 + freedom] is var e and >= 0)
                Add(e, e, stiffness);

        // A freedom nothing stiffens - a turn at a node only pinned beams meet at - is held.
        var unstable = new List<(int, int)>();
        var biggest = 0.0;
        for (var e = 0; e < count; e++) biggest = Math.Max(biggest, Math.Abs(a[address[e]]));
        for (var e = 0; e < count; e++)
            if (Math.Abs(a[address[e]]) <= 1e-12 * Math.Max(biggest, 1)) a[address[e]] = Math.Max(biggest, 1) * 1e-6;

        // Factorised: L D L^T, column by column (Bathe's COLSOL).
        for (var col = 0; col < count; col++)
        {
            var kn = address[col];
            var height = (int)(address[col + 1] - kn - 1);
            var first = col - height;
            for (var row = first + 1; row < col; row++)
            {
                var rowHeight = (int)(address[row + 1] - address[row] - 1);
                var rowFirst = row - rowHeight;
                var from = Math.Max(first, rowFirst);
                var sum = 0.0;
                for (var m = from; m < row; m++) sum += a[address[row] + (row - m)] * a[kn + (col - m)];
                a[kn + (col - row)] -= sum;
            }

            var diagonal = a[kn];
            for (var row = first; row < col; row++)
            {
                var off = a[kn + (col - row)];
                var c = off / a[address[row]];
                diagonal -= c * off;
                a[kn + (col - row)] = c;
            }

            if (diagonal <= 1e-12 * Math.Max(biggest, 1))
            {
                // Nothing holds this way: a mechanism. Held, and said.
                var dof = Array.IndexOf(equation, col);
                unstable.Add((dof / 6, dof % 6));
                diagonal = Math.Max(biggest, 1);
            }

            a[kn] = diagonal;
        }

        if (unstable.Count > 0)
            problems.Add($"The structure is free to move at {unstable.Select(u => u.Item1).Distinct().Count()} node{(unstable.Select(u => u.Item1).Distinct().Count() == 1 ? "" : "s")}: nothing holds it there - those movements are held at nothing to go on.");

        return new Factored(a, address, equation, count, n * 6, beams, shells, problems, unstable);
    }

    /// <summary>A structure's stiffness factorised: what its elements were taken at, what was wrong, and any load put through it.</summary>
    public sealed class Factored
    {
        private readonly double[] _a;
        private readonly long[] _address;
        private readonly int[] _equation;
        private readonly int _count, _freedoms;

        internal Factored(double[] a, long[] address, int[] equation, int count, int freedoms, IReadOnlyList<Beam> beams, IReadOnlyList<Shell> shells, IReadOnlyList<string> problems, IReadOnlyList<(int, int)> unstable)
        {
            (_a, _address, _equation, _count, _freedoms) = (a, address, equation, count, freedoms);
            (Beams, Shells, Problems, Unstable) = (beams, shells, problems, unstable);
        }

        public IReadOnlyList<Beam> Beams { get; }

        public IReadOnlyList<Shell> Shells { get; }

        public IReadOnlyList<string> Problems { get; }

        public IReadOnlyList<(int Node, int Freedom)> Unstable { get; }

        /// <summary>A load - six forces to a node - as the movement of every node.</summary>
        public double[] Solve(double[] load)
        {
            var (a, address, equation, count) = (_a, _address, _equation, _count);
            var v = new double[count];
            for (var d = 0; d < _freedoms; d++)
                if (equation[d] >= 0) v[equation[d]] += load[d];

            // Forward, scale, back.
            for (var col = 0; col < count; col++)
            {
                var kn = address[col];
                var height = (int)(address[col + 1] - kn - 1);
                var sum = 0.0;
                for (var m = 1; m <= height; m++) sum += a[kn + m] * v[col - m];
                v[col] -= sum;
            }

            for (var col = 0; col < count; col++) v[col] /= a[address[col]];
            for (var col = count - 1; col > 0; col--)
            {
                var kn = address[col];
                var height = (int)(address[col + 1] - kn - 1);
                for (var m = 1; m <= height; m++) v[col - m] -= a[kn + m] * v[col];
            }

            var u = new double[_freedoms];
            for (var d = 0; d < _freedoms; d++)
                if (equation[d] >= 0) u[d] = v[equation[d]];
            return u;
        }
    }

    // ---- geometric stiffness -----------------------------------------------------------------

    /// <summary>
    /// What a structure's elements are stressed by under a load, tension positive: each beam's axial force, N,
    /// and each shell's membrane forces, N per mm - what its stiffness rises or falls by as it moves.
    /// </summary>
    public sealed record Prestress(double[] Axial, double[] Nx, double[] Ny, double[] Nxy)
    {
        /// <summary>Stresses added up, each by its factor: a combination's, from its cases'.</summary>
        public static Prestress Sum(IEnumerable<(Prestress Stress, double Factor)> terms)
        {
            var list = terms.ToList();
            var (beams, shells) = (list[0].Stress.Axial.Length, list[0].Stress.Nx.Length);
            var sum = new Prestress(new double[beams], new double[shells], new double[shells], new double[shells]);
            foreach (var (stress, factor) in list)
            {
                for (var i = 0; i < beams; i++) sum.Axial[i] += factor * stress.Axial[i];
                for (var i = 0; i < shells; i++)
                {
                    sum.Nx[i] += factor * stress.Nx[i];
                    sum.Ny[i] += factor * stress.Ny[i];
                    sum.Nxy[i] += factor * stress.Nxy[i];
                }
            }

            return sum;
        }
    }

    /// <summary>A shell's axes and the gradients of its three corners' shape functions in them.</summary>
    private sealed record ShellShape(Vector3 X, Vector3 Y, double[] B, double[] C, double Area);

    private ShellShape[]? _shapes;
    private (Vector3 X, double Length)[]? _lines;

    /// <summary>Each shell's shape and each beam's line, worked once: they do not change as a structure's loads do.</summary>
    private ShellShape[] Shapes => _shapes ??= _shells.Select(ShapeOf).ToArray();

    private (Vector3 X, double Length)[] Lines => _lines ??= _beams.Select(beam => Axes(beam) is var (x, _, _, length) ? (x, length) : default).ToArray();

    private ShellShape ShapeOf(Shell shell)
    {
        var (x, y, _) = Axes(shell);
        var origin = Vector3.Of(_nodes[shell.I]);
        var p = new[] { shell.I, shell.J, shell.K }.Select(node => Vector3.Of(_nodes[node]) - origin).Select(v => (X: v.Dot(x), Y: v.Dot(y))).ToArray();
        var twice = (p[1].X - p[0].X) * (p[2].Y - p[0].Y) - (p[2].X - p[0].X) * (p[1].Y - p[0].Y);
        var (b, c) = (new double[3], new double[3]);
        for (var i = 0; i < 3; i++)
        {
            var (j, k) = ((i + 1) % 3, (i + 2) % 3);
            b[i] = (p[j].Y - p[k].Y) / twice;
            c[i] = (p[k].X - p[j].X) / twice;
        }

        return new ShellShape(x, y, b, c, Math.Abs(twice) / 2);
    }

    private static Vector3 Moved(double[] u, int node) => new(u[node * 6], u[node * 6 + 1], u[node * 6 + 2]);

    /// <summary>What a movement stresses the elements by, at the stiffnesses they were factorised at.</summary>
    public Prestress PrestressOf(Factored factored, double[] u)
    {
        var (beams, shells) = (factored.Beams, factored.Shells);
        var axial = new double[beams.Count];
        for (var i = 0; i < beams.Count; i++)
        {
            var beam = beams[i];
            var (x, length) = Lines[i];
            axial[i] = beam.E * beam.A / length * (Moved(u, beam.J) - Moved(u, beam.I)).Dot(x);
        }

        var (nx, ny, nxy) = (new double[shells.Count], new double[shells.Count], new double[shells.Count]);
        for (var i = 0; i < shells.Count; i++)
        {
            var shell = shells[i];
            var shape = Shapes[i];
            double ex = 0, ey = 0, gamma = 0;
            var corners = new[] { shell.I, shell.J, shell.K };
            for (var c = 0; c < 3; c++)
            {
                var moved = Moved(u, corners[c]);
                var (ux, uy) = (moved.Dot(shape.X), moved.Dot(shape.Y));
                ex += shape.B[c] * ux;
                ey += shape.C[c] * uy;
                gamma += shape.C[c] * ux + shape.B[c] * uy;
            }

            var d = shell.E * shell.Thickness / (1 - shell.Poisson * shell.Poisson);
            nx[i] = d * (ex + shell.Poisson * ey);
            ny[i] = d * (ey + shell.Poisson * ex);
            nxy[i] = d * (1 - shell.Poisson) / 2 * gamma;
        }

        return new Prestress(axial, nx, ny, nxy);
    }

    /// <summary>
    /// Kg u: the forces a stressed structure puts on its nodes through a movement, N. A beam as a string, N/L
    /// across it; a shell by the gradients of its membrane forces, the same for each way a corner moves.
    /// Compression makes them push the way it has moved: what P-delta is.
    /// </summary>
    public double[] GeometricForces(Prestress stress, double[] u)
    {
        var f = new double[u.Length];
        var (lines, shapes) = (Lines, Shapes);
        for (var i = 0; i < _beams.Count; i++)
        {
            var n = stress.Axial[i];
            if (n == 0) continue;
            var beam = _beams[i];
            var (x, length) = lines[i];
            var (dx, dy, dz) = (u[beam.J * 6] - u[beam.I * 6], u[beam.J * 6 + 1] - u[beam.I * 6 + 1], u[beam.J * 6 + 2] - u[beam.I * 6 + 2]);
            var along = dx * x.X + dy * x.Y + dz * x.Z;
            var share = n / length;
            var (ax, ay, az) = ((dx - x.X * along) * share, (dy - x.Y * along) * share, (dz - x.Z * along) * share);
            f[beam.I * 6] -= ax;
            f[beam.I * 6 + 1] -= ay;
            f[beam.I * 6 + 2] -= az;
            f[beam.J * 6] += ax;
            f[beam.J * 6 + 1] += ay;
            f[beam.J * 6 + 2] += az;
        }

        Span<int> corners = stackalloc int[3];
        for (var s = 0; s < _shells.Count; s++)
        {
            var (nx, ny, nxy) = (stress.Nx[s], stress.Ny[s], stress.Nxy[s]);
            if (nx == 0 && ny == 0 && nxy == 0) continue;
            var shell = _shells[s];
            var shape = shapes[s];
            (corners[0], corners[1], corners[2]) = (shell.I * 6, shell.J * 6, shell.K * 6);
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                var c = shape.Area * (nx * shape.B[i] * shape.B[j] + ny * shape.C[i] * shape.C[j] + nxy * (shape.B[i] * shape.C[j] + shape.C[i] * shape.B[j]));
                f[corners[i]] += c * u[corners[j]];
                f[corners[i] + 1] += c * u[corners[j] + 1];
                f[corners[i] + 2] += c * u[corners[j] + 2];
            }
        }

        return f;
    }

    /// <summary>A beam's local stiffness times the rotation into its axes: what turns the movements at its ends into its end forces, twelve by twelve.</summary>
    public (double[,] Matrix, int[] Dofs) BeamForceMatrix(Beam beam)
    {
        var (_, t, length) = BeamGlobal(beam);
        return (Multiply(BeamLocal(beam, length), t), Dofs(beam.I, beam.J));
    }

    /// <summary>What a beam's loads along it come to at its ends, as a pinned end has them: what is taken off K d for its end forces.</summary>
    public double[] CondensedLoad(Beam beam, double[] localLoad) => CondenseLoad(beam, Axes(beam).Length, localLoad);

    private static int[] Dofs(params int[] nodes) => nodes.SelectMany(node => Enumerable.Range(0, 6).Select(f => node * 6 + f)).ToArray();

    /// <summary>The nodes in reverse Cuthill-McKee order: each part from a node with fewest neighbours, by breadth, reversed - so the equations bunch near the diagonal.</summary>
    private static int[] ReverseCuthillMcKee(HashSet<int>[] neighbours)
    {
        var n = neighbours.Length;
        var seen = new bool[n];
        var order = new List<int>(n);
        foreach (var start in Enumerable.Range(0, n).OrderBy(node => neighbours[node].Count))
        {
            if (seen[start]) continue;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            seen[start] = true;
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                order.Add(node);
                foreach (var next in neighbours[node].Where(next => !seen[next]).OrderBy(next => neighbours[next].Count))
                {
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }
        }

        order.Reverse();
        return order.ToArray();
    }

    /// <summary>A beam's forces at its two ends in its own axes, from the movements: K d less the loads along it.</summary>
    public double[] BeamEndForces(Beam beam, double[] displacements, double[]? localLoad = null)
    {
        var (k, t, length) = BeamGlobal(beam);
        var dofs = Dofs(beam.I, beam.J);
        var global = dofs.Select(d => displacements[d]).ToArray();
        var local = new double[12];
        for (var i = 0; i < 12; i++)
        for (var j = 0; j < 12; j++)
            local[i] += t[i, j] * global[j];
        var kl = BeamLocal(beam, length);
        var forces = new double[12];
        for (var i = 0; i < 12; i++)
        for (var j = 0; j < 12; j++)
            forces[i] += kl[i, j] * local[j];
        if (localLoad is not null)
        {
            var condensed = CondenseLoad(beam, length, localLoad);
            for (var i = 0; i < 12; i++) forces[i] -= condensed[i];
        }

        return forces;
    }

    /// <summary>A shell's bending moments at its middle, N mm per mm, in its own axes: m_x, m_y, m_xy.</summary>
    public double[] ShellMoments(Shell shell, double[] displacements)
    {
        var (_, t, local) = ShellGlobal(shell);
        var dofs = Dofs(shell.I, shell.J, shell.K);
        var global = dofs.Select(d => displacements[d]).ToArray();
        var l = new double[18];
        for (var i = 0; i < 18; i++)
        for (var j = 0; j < 18; j++)
            l[i] += t[i, j] * global[j];
        int[] mapBending = { 2, 3, 4, 8, 9, 10, 14, 15, 16 };
        var b = DktB(local, 1.0 / 3, 1.0 / 3);
        var db = shell.Bending * shell.E * Math.Pow(shell.Thickness, 3) / (12 * (1 - shell.Poisson * shell.Poisson));
        var kappa = new double[3];
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 9; c++)
            kappa[r] += b[r, c] * l[mapBending[c]];
        return new[] { db * (kappa[0] + shell.Poisson * kappa[1]), db * (shell.Poisson * kappa[0] + kappa[1]), db * (1 - shell.Poisson) / 2 * kappa[2] };
    }

    /// <summary>The force a shell puts on each of its three corners, N, in the building's axes. They add up to nothing: the loads it carries are held at the nodes.</summary>
    public Vector3[] ShellCornerForces(Shell shell, double[] displacements)
    {
        var (k, _, _) = ShellGlobal(shell);
        var dofs = Dofs(shell.I, shell.J, shell.K);
        var forces = new Vector3[3];
        for (var corner = 0; corner < 3; corner++)
        {
            var f = new double[3];
            for (var axis = 0; axis < 3; axis++)
            for (var c = 0; c < 18; c++)
                f[axis] += k[corner * 6 + axis, c] * displacements[dofs[c]];
            forces[corner] = new Vector3(f[0], f[1], f[2]);
        }

        return forces;
    }

    /// <summary>The forces every element puts on its nodes, in the building's axes, and every spring: K d, six to a node - what the reactions are read from.</summary>
    public double[] InternalForces(double[] displacements)
    {
        var f = new double[_nodes.Count * 6];
        foreach (var beam in _beams)
        {
            var (k, _, _) = BeamGlobal(beam);
            Scatter(f, Dofs(beam.I, beam.J), k, displacements);
        }

        foreach (var shell in _shells)
        {
            var (k, _, _) = ShellGlobal(shell);
            Scatter(f, Dofs(shell.I, shell.J, shell.K), k, displacements);
        }

        foreach (var (node, freedom, stiffness) in _springs) f[node * 6 + freedom] += stiffness * displacements[node * 6 + freedom];
        return f;
    }

    private static void Scatter(double[] f, int[] dofs, double[,] k, double[] d)
    {
        for (var i = 0; i < dofs.Length; i++)
        for (var j = 0; j < dofs.Length; j++)
            f[dofs[i]] += k[i, j] * d[dofs[j]];
    }

    /// <summary>
    /// A uniform load along a beam, N/mm in the building's axes, as the forces it puts on the beam's
    /// two nodes - in the beam's axes, condensed for its pinned ends - and in the building's: the
    /// loads a uniform load is equivalent to, half each way and the fixed end moments.
    /// </summary>
    public (double[] Local, double[] Global) BeamUniformLoad(Beam beam, Vector3 load)
    {
        var (x, y, z, l) = Axes(beam);
        var (wx, wy, wz) = (load.Dot(x), load.Dot(y), load.Dot(z));
        var local = new[]
        {
            wx * l / 2, wy * l / 2, wz * l / 2, 0, -wz * l * l / 12, wy * l * l / 12,
            wx * l / 2, wy * l / 2, wz * l / 2, 0, wz * l * l / 12, -wy * l * l / 12
        };
        var condensed = CondenseLoad(beam, l, local);
        var t = Rotation(x, y, z, 2);
        var global = new double[12];
        for (var i = 0; i < 12; i++)
        for (var j = 0; j < 12; j++)
            global[i] += t[j, i] * condensed[j];
        return (local, global);
    }
}
