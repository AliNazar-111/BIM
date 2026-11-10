using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How a combination stands up to sway, each way it can lean: its critical load factor, how much sway
/// amplifies by, how far it moves at most and how far its lean alone moves it, mm, how many passes its second-order analysis took, none where
/// first-order was enough, and whether it buckles before it reaches its loads.
/// </summary>
/// <summary>
/// The load factor at which the structure or some part of it buckles under a combination, by eigenvalue; the member or plate its buckled shape moves most at, and whether it is a part buckling on its own
/// rather than the building as a whole.
/// </summary>
public sealed record BucklingResult(LoadCombination Combination, double Factor, string Where, bool Local)
{
    public bool Buckles => Factor <= 1;

    /// <summary>What it comes to, in words.</summary>
    public string Said => $"By eigenvalue under {Combination.Name}, λ {(Factor >= 1000 ? "over 1000" : Factor.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture))}: " + (Local
        ? $"{Where} buckling on its own, {(Buckles ? "before the loads are reached - it is to be stiffened" : "a member's or wall's own buckling, in its own checks")}."
        : $"the building as a whole, {Where} moving most{(Buckles ? " - it buckles before the loads are reached" : "")}.");
}

public sealed record SwayResult(LoadCombination Combination, double Alpha, double Amplifier, double Movement, double Lean, int Passes, bool Buckles)
{
    public bool SecondOrder => Passes > 0;
}

/// <summary>
/// The structure's second-order effects. Its ultimate combinations are analysed with stiffnesses
/// reduced as the codes ask for stability, and with its initial out-of-plumb as horizontal forces at each
/// node, in proportion to the weight there, leaning with the wind or each of four ways without it.
/// How sensitive it is comes from an energy ratio, the work the leaning forces do in moving it over what
/// its compression takes out of its stiffness along that movement - what the storey formula of BS EN 1993-1-1
/// 5.2.1(4), H/V h/δ, comes to in a storey frame, but for any shape of structure. First-order is enough at
/// αcr 10 or more by BS EN 1993-1-1 5.2.1 (and at 20, Q 0.05, in the US by ACI 318-19 6.6.4.3); below that the
/// analysis is taken to second order, P-delta and P-small delta over the pieces of each member, by putting
/// what the compression pushes with back through the factorised structure until it settles.
/// </summary>
public sealed class SecondOrderAnalysis
{
    /// <summary>The first-order limit on αcr: BS EN 1993-1-1 5.2.1(3), elastic analysis.</summary>
    public const double UkLimit = 10;

    /// <summary>The first-order limit on αcr in the US: ACI 318-19 6.6.4.3's stability index Q of 0.05, its reciprocal; B2 of 1.05.</summary>
    public const double UsLimit = 20;

    private const int MostPasses = 40;

    public StructuralAnalysis Analysis { get; }

    /// <summary>Whether imposed loads are reduced for the area and floors a member carries, as the project asked when this was worked out.</summary>
    public bool ReducesImposed { get; private init; }

    private bool _usImposed;

    /// <summary>The out-of-plumb taken, as a share of the height: the horizontal force on a node over the weight on it.</summary>
    public double Imperfection { get; }

    /// <summary>The αcr under which a combination is taken to second order.</summary>
    public double Limit { get; }

    /// <summary>Each ultimate combination, each way, least stable first.</summary>
    public IReadOnlyList<SwayResult> Results { get; }

    /// <summary>What was taken.</summary>
    public IReadOnlyList<string> Notes { get; }

    public SwayResult? Worst => Results.Count == 0 ? null : Results[0];

    private IReadOnlyList<BucklingResult>? _bucklings;

    /// <summary>
    /// The lowest load factor at which any part of it buckles, (K + λ Kg) φ = 0, by subspace iteration on K⁻¹ Kg with the stiffness and stresses second order took, under the combination with the
    /// least αcr and under the heaviest. Where αcr reads the building leaning as a whole, this finds a slender part buckling on its own as well. Worked out when first asked for: a few seconds on
    /// a building of size.
    /// </summary>
    public IReadOnlyList<BucklingResult> Bucklings => _bucklings ??= WorkOutBucklings();

    private IReadOnlyList<BucklingResult> WorkOutBucklings()
    {
        var loading = Analysis.Loading;
        LoadCombination? least = null;
        if (Results.Count > 0)
            foreach (var (variant, combination, _, _, _) in _variants)
                if (ReferenceEquals(variant, Results[0].Combination)) least = combination;
        var heaviest = loading.Combinations.Where(combination => combination.Factored).MaxBy(combination => combination.Terms.Where(term => !term.Case.Lateral).Sum(term => term.Factor * loading.Total(term.Case)));
        var found = new List<BucklingResult>();
        foreach (var combination in new[] { least, heaviest }.OfType<LoadCombination>().Distinct())
            if (BucklingUnder(combination) is { } result) found.Add(result);
        return found;
    }

    private BucklingResult? BucklingUnder(LoadCombination combination)
    {
        var solver = Analysis.Solver;
        var length = solver.NodeCount * 6;
        if (length == 0 || combination.Terms.Any(term => !_stress.ContainsKey(term.Case))) return null;
        var stressed = StructuralSolver.Prestress.Sum(combination.Terms.Select(term => (_stress[term.Case], term.Factor)));
        if (LowestBuckling(solver, _factored, stressed) is not var (factor, shape)) return null;

        // Where it moves most, and whether a part of it moves alone.
        var moves = Enumerable.Range(0, solver.NodeCount).Select(node => Math.Sqrt(shape[node * 6] * shape[node * 6] + shape[node * 6 + 1] * shape[node * 6 + 1] + shape[node * 6 + 2] * shape[node * 6 + 2])).ToList();
        var top = moves.IndexOf(moves.Max());
        var moving = moves.Count(move => move > 0.3 * moves[top]);
        var where = "the structure";
        foreach (var member in Analysis.Model.Members)
            foreach (var (beam, _, _, _) in Analysis.BeamsOf(member))
                if (where == "the structure" && (solver.Beams[beam].I == top || solver.Beams[beam].J == top)) where = member.Name;
        if (where == "the structure")
            foreach (var panel in Analysis.Model.Panels)
                foreach (var shell in Analysis.ShellsOf(panel))
                {
                    var s = solver.Shells[shell];
                    if (where == "the structure" && (s.I == top || s.J == top || s.K == top)) where = panel.Name;
                }

        return new BucklingResult(combination, factor, where, moving < 0.15 * solver.NodeCount);
    }

    /// <summary>
    /// The least load factor λ at which (K + λ Kg) φ = 0 for a structure stressed so, and its buckled shape: subspace iteration on K⁻¹ (-Kg) with six vectors, leaning each way to start, the small
    /// problem solved each pass with the stiffness, which is positive, as its metric; the greatest 1/λ found. Null where nothing is compressed enough to buckle.
    /// </summary>
    public static (double Factor, double[] Shape)? LowestBuckling(StructuralSolver solver, StructuralSolver.Factored factored, StructuralSolver.Prestress stressed)
    {
        var length = solver.NodeCount * 6;
        double[] Softening(double[] u)
        {
            var f = solver.GeometricForces(stressed, u);
            for (var i = 0; i < f.Length; i++) f[i] = -f[i];
            return f;
        }

        // Starting shapes: leaning each way, more the higher, then scattered movements.
        const int count = 6;
        var random = new Random(48);
        var heights = Enumerable.Range(0, solver.NodeCount).Select(node => solver.NodeAt(node).Z).ToList();
        var (low, high) = (heights.Min(), Math.Max(heights.Max(), heights.Min() + 1));
        var x = new double[count][];
        for (var j = 0; j < count; j++)
        {
            x[j] = new double[length];
            for (var node = 0; node < solver.NodeCount; node++)
            for (var axis = 0; axis < 3; axis++)
                x[j][node * 6 + axis] = j < 2 ? (axis == j ? (heights[node] - low) / (high - low) : 0) : random.NextDouble() - 0.5;
        }

        double Dot(double[] a, double[] b)
        {
            var sum = 0.0;
            for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        var (most, previous) = (0.0, double.NaN);
        var shape = x[0];
        for (var pass = 0; pass < 40; pass++)
        {
            var gx = x.Select(Softening).ToArray();
            var y = gx.Select(factored.Solve).ToArray();
            var gy = y.Select(Softening).ToArray();
            var (stiff, soft) = (new double[count, count], new double[count, count]);
            for (var i = 0; i < count; i++)
            for (var j = i; j < count; j++)
            {
                stiff[i, j] = stiff[j, i] = 0.5 * (Dot(y[i], gx[j]) + Dot(y[j], gx[i]));
                soft[i, j] = soft[j, i] = 0.5 * (Dot(y[i], gy[j]) + Dot(y[j], gy[i]));
            }

            var (values, vectors) = ModalAnalysis.Generalised(soft, stiff);
            most = values[^1];
            for (var j = 0; j < count; j++)
            {
                var next = new double[length];
                for (var i = 0; i < count; i++)
                {
                    var q = vectors[i, j];
                    if (q == 0) continue;
                    for (var k = 0; k < length; k++) next[k] += q * y[i][k];
                }

                var scale = next.Select(Math.Abs).DefaultIfEmpty(0).Max();
                if (scale > 0)
                    for (var k = 0; k < length; k++) next[k] /= scale;
                x[j] = next;
            }

            shape = x[count - 1];
            if (!double.IsNaN(previous) && Math.Abs(most - previous) <= 1e-6 * Math.Abs(most)) break;
            previous = most;
        }

        if (!(most > 1e-12)) return null;
        return (1 / most, shape);
    }

    private readonly StructuralSolver.Factored _factored;
    private readonly Dictionary<LoadCase, double[]> _moved = new();
    private readonly Dictionary<(LoadCase, int), double[]> _leanMoved = new();
    private readonly Dictionary<LoadCase, StructuralSolver.Prestress> _stress = new();
    private readonly List<(LoadCombination Variant, LoadCombination Combination, double X, double Y, double[]? Extra)> _variants = new();

    private SecondOrderAnalysis(StructuralAnalysis analysis, double imperfection, double limit, StructuralSolver.Factored factored, IReadOnlyList<string> notes, List<SwayResult> results)
    {
        (Analysis, Imperfection, Limit, _factored, Notes, Results) = (analysis, imperfection, limit, factored, notes, results);
    }

    /// <summary>
    /// The share of its stiffness an element is taken at for stability. In the UK steel as it is, concrete
    /// cracked at 0.4 Ecd, Ecd being Ecm / 1.2 (BS EN 1992-1-1 Annex H), timber at E / γM (BS EN 1995-1-1 2.2.2)
    /// and masonry cracked at half. In the US steel at 0.8 (AISC 360-16 C2.3), concrete columns at 0.70, beams and
    /// cracked walls 0.35 and floors 0.25 (ACI 318-19 Table 6.6.3.1.1(a)), timber at Emin (NDS Table 4A,
    /// and 0.95 of 1.8 million for 24F-1.8E) and masonry at half.
    /// </summary>
    public static double ShareOf(AnalyticalMaterial material, bool us, AnalyticalMemberKind? member, AnalyticalPanelKind? panel)
    {
        var glulam = material.Quality is "GL24h" or "24F-1.8E";
        return material.Kind switch
        {
            AnalyticalMaterialKind.Steel => us ? 0.8 : 1.0,
            AnalyticalMaterialKind.Concrete when !us => 0.4 / 1.2,
            AnalyticalMaterialKind.Concrete when member == AnalyticalMemberKind.Column => 0.70,
            AnalyticalMaterialKind.Concrete when member is not null || panel == AnalyticalPanelKind.Wall => 0.35,
            AnalyticalMaterialKind.Concrete => 0.25,
            AnalyticalMaterialKind.Timber when !us => glulam ? 1 / 1.25 : 1 / 1.3,
            AnalyticalMaterialKind.Timber => glulam ? 0.95 / 1.8 : 580000.0 / 1600000,
            AnalyticalMaterialKind.Masonry => 0.5,
            _ => 1.0
        };
    }

    /// <summary>
    /// The out-of-plumb of a structure so high, mm. In the UK φ0 αh of BS EN 1993-1-1 5.3.2, as BS EN 1992-1-1
    /// 5.2 has it too: 1/200, by 2/√h between 2/3 and 1, αm taken as 1 for one column, on the safe side. In the
    /// US 0.003 of the weight: AISC 360-16 C2.2b's 0.002 and C2.3's 0.001 for taking τb as 1.0 throughout.
    /// </summary>
    public static double ImperfectionOf(bool us, double height)
    {
        if (us) return 0.003;
        var metres = Math.Max(height / 1000, 1e-3);
        return Math.Clamp(2 / Math.Sqrt(metres), 2.0 / 3, 1) / 200;
    }

    private static double Dot(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static double[] Summed(int length, IEnumerable<(double[] Values, double Factor)> terms)
    {
        var sum = new double[length];
        foreach (var (values, factor) in terms)
            if (factor != 0)
                for (var i = 0; i < length; i++) sum[i] += factor * values[i];
        return sum;
    }

    /// <summary>The analysis taken to second order, as far as each combination asks.</summary>
    public static SecondOrderAnalysis Run(BimDocument document, StructuralAnalysis analysis)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var solver = analysis.Solver;
        var (beamShares, shellShares) = (Enumerable.Repeat(1.0, solver.Beams.Count).ToArray(), Enumerable.Repeat(1.0, solver.Shells.Count).ToArray());
        foreach (var member in analysis.Model.Members)
            foreach (var (beam, _, _, _) in analysis.BeamsOf(member))
                beamShares[beam] = ShareOf(member.Section.Material, us, member.Kind, null);
        foreach (var panel in analysis.Model.Panels)
            foreach (var shell in analysis.ShellsOf(panel))
                shellShares[shell] = ShareOf(panel.Material, us, null, panel.Kind);

        var factored = solver.Factorise(beamShares, shellShares);
        var length = solver.NodeCount * 6;
        var heights = Enumerable.Range(0, solver.NodeCount).Select(node => solver.NodeAt(node).Z).DefaultIfEmpty(0).ToList();
        var imperfection = ImperfectionOf(us, heights.Max() - heights.Min());
        var limit = us ? UsLimit : UkLimit;

        // A stud wall leans where a floor holds it. Its weight pushed sideways halfway up would only bow its studs, which is a stud's own buckling, in the stud's own checks, and no measure of how the building sways.
        var leansAt = Enumerable.Range(0, solver.NodeCount).ToArray();
        var floored = analysis.Model.Panels.Where(panel => panel.Kind != AnalyticalPanelKind.Wall).Concat(analysis.Model.Diaphragms).SelectMany(analysis.ShellsOf)
            .Select(shell => solver.Shells[shell]).SelectMany(shell => new[] { shell.I, shell.J, shell.K }).ToHashSet();
        foreach (var panel in analysis.Model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall))
        {
            var shells = analysis.ShellsOf(panel).Select(shell => solver.Shells[shell]).ToList();
            if (shells.Count == 0 || shells[0].Framed is null) continue;
            // The nearest node above or below each that a floor holds, from the mesh, or failing one the top of the wall over it: not a door's head, which nothing holds, nor the strip of a wall that runs on past its floor up to a roof.
            var foot = panel.Outline.Min(node => node.At.Z);
            var nodes = shells.SelectMany(shell => new[] { shell.I, shell.J, shell.K }).Distinct().Select(node => (Node: node, At: panel.Flat(solver.NodeAt(node)))).ToList();
            foreach (var (node, at) in nodes.Where(each => solver.NodeAt(each.Node).Z > foot + 1))
            {
                var over = nodes.Where(other => Math.Abs(other.At.X - at.X) <= analysis.MeshSize / 2 + 1).ToList();
                var held = over.Where(other => floored.Contains(other.Node)).ToList();
                leansAt[node] = held.Count > 0 ? held.MinBy(other => Math.Abs(other.At.Y - at.Y)).Node : over.MaxBy(other => other.At.Y).Node;
            }
        }

        // Each case's movement and stress, and how it moves leaning each way under the weight of it.
        var moved = new Dictionary<LoadCase, double[]>();
        var stress = new Dictionary<LoadCase, StructuralSolver.Prestress>();
        var lean = new Dictionary<(LoadCase, int), double[]>();
        var leanMoved = new Dictionary<(LoadCase, int), double[]>();

        // A diaphragm stands for a floor or roof holding wall tops in its own plane: it carries no weight, and has next to no bending, so what it carries in its plane would buckle it out of it at
        // a touch, as no real deck does between its joists. Its forces are left out of second order.
        var diaphragms = analysis.Model.Diaphragms.SelectMany(analysis.ShellsOf).ToList();
        foreach (var loadCase in analysis.Loading.Cases)
        {
            var load = analysis.LoadOf(loadCase);
            moved[loadCase] = factored.Solve(load);
            var stressed = solver.PrestressOf(factored, moved[loadCase]);
            foreach (var shell in diaphragms) (stressed.Nx[shell], stressed.Ny[shell], stressed.Nxy[shell]) = (0, 0, 0);
            stress[loadCase] = stressed;
            if (loadCase.Lateral) continue;
            for (var axis = 0; axis < 2; axis++)
            {
                var horizontal = new double[length];
                for (var node = 0; node < solver.NodeCount; node++) horizontal[leansAt[node] * 6 + axis] += -load[node * 6 + 2] * imperfection;
                lean[(loadCase, axis)] = horizontal;
                leanMoved[(loadCase, axis)] = factored.Solve(horizontal);
            }
        }

        var results = new List<SwayResult>();
        var variants = new List<(LoadCombination, LoadCombination, double, double, double[]?)>();
        foreach (var combination in analysis.Loading.Combinations.Where(combination => combination.Factored))
        {
            var wind = combination.Terms.Where(term => term.Factor > 0 && term.Case.Wind is not null).Select(term => term.Case.Wind).FirstOrDefault();
            var ways = wind is { } blows
                ? new[] { (Name: string.Empty, X: blows.NormalisedOrDefault(Geometry.Vector2D.UnitX).X, Y: blows.NormalisedOrDefault(Geometry.Vector2D.UnitX).Y) }
                : new[] { (Name: " +x", X: 1.0, Y: 0.0), (Name: " -x", X: -1.0, Y: 0.0), (Name: " +y", X: 0.0, Y: 1.0), (Name: " -y", X: 0.0, Y: -1.0) };
            var stressed = StructuralSolver.Prestress.Sum(combination.Terms.Select(term => (stress[term.Case], term.Factor)));
            var gravity = combination.Terms.Where(term => !term.Case.Lateral).ToList();
            foreach (var (name, x, y) in ways)
            {
                var variant = combination with { Name = combination.Name + name };
                var force = Summed(length, gravity.SelectMany(term => new[] { (lean[(term.Case, 0)], term.Factor * x), (lean[(term.Case, 1)], term.Factor * y) }));
                var leaning = Summed(length, gravity.SelectMany(term => new[] { (leanMoved[(term.Case, 0)], term.Factor * x), (leanMoved[(term.Case, 1)], term.Factor * y) }));

                // αcr: the work of the leaning forces over what compression takes from the stiffness along the same movement.
                var softening = -Dot(leaning, solver.GeometricForces(stressed, leaning));
                var alpha = softening > 1e-12 ? Dot(leaning, force) / softening : double.PositiveInfinity;

                var first = Summed(length, combination.Terms.Select(term => (moved[term.Case], term.Factor)).Append((leaning, 1.0)));
                var (now, passes, buckles) = (first, 0, alpha <= 1);
                if (!buckles && alpha < limit)
                {
                    var was = double.MaxValue;
                    for (var pass = 1; pass <= MostPasses; pass++)
                    {
                        var pushed = solver.GeometricForces(stressed, now);
                        for (var i = 0; i < length; i++) pushed[i] = -pushed[i];
                        var more = factored.Solve(pushed);
                        var next = new double[length];
                        double change = 0, size = 0;
                        for (var i = 0; i < length; i++)
                        {
                            next[i] = first[i] + more[i];
                            change = Math.Max(change, Math.Abs(next[i] - now[i]));
                            size = Math.Max(size, Math.Abs(next[i]));
                        }

                        now = next;
                        passes = pass;
                        var share = size > 0 ? change / size : 0;
                        if (share < 1e-3) break;
                        if (double.IsNaN(share) || (pass > 8 && share >= was) || pass == MostPasses)
                        {
                            buckles = true;
                            break;
                        }

                        was = share;
                    }
                }

                var extra = passes > 0 && !buckles ? now.Select((value, i) => value - first[i]).ToArray() : null;
                double Sideways(double[] u) => Enumerable.Range(0, solver.NodeCount).Select(node => Math.Sqrt(Math.Pow(u[node * 6], 2) + Math.Pow(u[node * 6 + 1], 2))).DefaultIfEmpty(0).Max();
                var worst = buckles ? Math.Min(alpha, 1) : alpha;
                results.Add(new SwayResult(variant, worst, worst > 1 ? 1 / (1 - 1 / worst) : double.PositiveInfinity, buckles ? double.PositiveInfinity : Sideways(now), Sideways(leaning), passes, buckles));
                variants.Add((variant, combination, x, y, extra));
            }
        }

        var notes = new List<string>
        {
            us
                ? $"Second-order effects by the direct analysis method of AISC 360-16 C2: notional loads of {imperfection:0.000} of the weight at each node, in every combination, EI and EA at 0.8 for steel, τb taken as 1.0. Concrete at ACI 318-19 Table 6.6.3.1.1(a)'s stiffnesses, timber at Emin, masonry cracked at half, as taken here. Taken to second order where αcr is under {limit:0}, Q over 0.05."
                : $"Second-order effects by BS EN 1993-1-1 5.2: an out-of-plumb of 1 in {1 / imperfection:0} (5.3.2, αm taken as 1) as horizontal forces at each node, leaning with the wind or each of four ways. Concrete at 0.4 Ecd (BS EN 1992-1-1 Annex H), timber at E/γM, masonry cracked at half, as taken here. Taken to second order where αcr is under {limit:0}.",
            "αcr is the energy ratio along the structure's leaning shape, the general form of the storey formula; a slender member's own buckling is in its own checks, and a stud wall's weight leans at the floor that holds it, its studs' bowing between floors being theirs. A diaphragm's forces are left out: it carries no weight and has next to no bending of its own. The moments the foundations are designed for stay first order, at full stiffness."
        };

        if (document.ProjectInformation.ReduceImposedLoads)
            notes.Add(us
                ? "Imposed loads on members reduced by ASCE 7-16 4.7: 0.25 + 15 / √(KLL AT), KLL 4 for a column and 2 for a beam, where KLL AT is 400 ft² or more, no less than 0.5, or 0.4 for a column carrying two floors or more; an office's 15 psf for partitions, walls and footings taken whole."
                : "Imposed loads on members reduced by BS EN 1991-1-1 6.3.1.2 with the UK National Annex: a beam by 1 - A/1000, no less than 0.75, a column by the floors over it, 0.9 for two down to 0.6 for five to ten and 0.5 past ten; walls and footings take them whole.");
        results.Sort((a, b) => a.Alpha.CompareTo(b.Alpha));
        var made = new SecondOrderAnalysis(analysis, imperfection, limit, factored, notes, results) { ReducesImposed = document.ProjectInformation.ReduceImposedLoads, _usImposed = us };
        foreach (var (key, value) in moved) made._moved[key] = value;
        foreach (var (key, value) in leanMoved) made._leanMoved[key] = value;
        foreach (var (key, value) in stress) made._stress[key] = value;
        foreach (var (variant, combination, x, y, extra) in variants) made._variants.Add((variant, combination, x, y, extra));
        return made;
    }

    /// <summary>
    /// A plate's moments under each combination each way it leans, averaged over squares of about a metre, in its own axes:
    /// each case's and each lean's worked once and added up, with what second order adds on top.
    /// </summary>
    public IReadOnlyList<(LoadCombination Combination, IReadOnlyList<PlateStretch> Stretches)> PlateStretches(AnalyticalPanel panel)
    {
        var (shells, areas, centres, cases) = PlateField(panel);
        var flat = panel.FlatOutline;
        var (firstU, firstV) = (flat.Min(point => point.X), flat.Min(point => point.Y));
        var cells = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < shells.Count; i++)
        {
            var key = ((int)Math.Floor((centres[i].X - firstU) / 1000), (int)Math.Floor((centres[i].Y - firstV) / 1000));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
            list.Add(i);
        }

        var results = new List<(LoadCombination, IReadOnlyList<PlateStretch>)>();
        foreach (var (combination, moments) in cases)
        {
            var stretches = new List<PlateStretch>();
            foreach (var members in cells.Values)
            {
                double mu = 0, mv = 0, muv = 0, total = 0;
                foreach (var i in members)
                {
                    total += areas[i];
                    (mu, mv, muv) = (mu + areas[i] * moments[i][0], mv + areas[i] * moments[i][1], muv + areas[i] * moments[i][2]);
                }

                if (total >= 5e4) stretches.Add(new PlateStretch(mu / total, mv / total, muv / total));
            }

            results.Add((combination, stretches));
        }

        return results;
    }

    /// <summary>
    /// The shear in a plate where it meets what holds or loads it - a wall, above it or below, or a beam - under each combination each
    /// way it leans, over about a metre of each side of each such line. It is read from the forces the triangles themselves put on the line's
    /// nodes, which balance by statics, rather than from how the moments change, which is least sure just there. The load at a line's own
    /// nodes goes straight into what is there, so what is read is the shear about half a row of triangles in: about where the codes ask for it.
    /// Being what reaches the line, it has the twist at the edge in it, on the safe side where a slab spans two ways.
    /// </summary>
    public IReadOnlyList<(LoadCombination Combination, IReadOnlyList<PlateShear> Shears)> PlateShears(AnalyticalPanel panel)
    {
        var solver = Analysis.Solver;
        var shells = Analysis.ShellsOf(panel).ToList();
        var own = shells.ToHashSet();
        var lines = new HashSet<(int, int)>();
        void Line(int a, int b) => lines.Add((Math.Min(a, b), Math.Max(a, b)));
        for (var s = 0; s < solver.Shells.Count; s++)
        {
            if (own.Contains(s)) continue;
            var other = solver.Shells[s];
            Line(other.I, other.J);
            Line(other.J, other.K);
            Line(other.K, other.I);
        }

        foreach (var beam in solver.Beams) Line(beam.I, beam.J);

        var corners = shells.Select(s => new[] { solver.Shells[s].I, solver.Shells[s].J, solver.Shells[s].K }).ToArray();
        var fans = new Dictionary<int, List<int>>();
        for (var i = 0; i < corners.Length; i++)
            foreach (var node in corners[i])
            {
                if (!fans.TryGetValue(node, out var fan)) fans[node] = fan = new List<int>();
                fan.Add(i);
            }

        Geometry.Point2D Flat(int node)
        {
            var at = solver.NodeAt(node);
            return panel.Flat(new Geometry.Point3D(at.X, at.Y, at.Z));
        }

        // Round each node on such a line, the triangles that reach one another without crossing it: each a side of the line, with the length it stands for.
        var sides = new List<(int Node, List<int> Triangles, double Length, Geometry.Point2D At, double AcrossU, double AcrossV)>();
        foreach (var (node, fan) in fans)
        {
            var left = fan.ToHashSet();
            while (left.Count > 0)
            {
                var group = new List<int> { left.First() };
                left.Remove(group[0]);
                var (length, du, dv, edges) = (0.0, 0.0, 0.0, 0);
                for (var next = 0; next < group.Count; next++)
                    foreach (var corner in corners[group[next]].Where(each => each != node))
                    {
                        if (lines.Contains((Math.Min(node, corner), Math.Max(node, corner))))
                        {
                            // Square to the line, into the triangle.
                            var (from, to) = (Flat(node), Flat(corner));
                            var run = from.DistanceTo(to);
                            length += run / 2;
                            var (nu, nv) = (-(to.Y - from.Y) / run, (to.X - from.X) / run);
                            var inside = corners[group[next]].Select(Flat).ToList();
                            var into = (inside.Average(point => point.X) - from.X) * nu + (inside.Average(point => point.Y) - from.Y) * nv;
                            du += into >= 0 ? nu : -nu;
                            dv += into >= 0 ? nv : -nv;
                            edges++;
                            continue;
                        }

                        foreach (var other in left.Where(each => corners[each].Contains(corner)).ToList())
                        {
                            left.Remove(other);
                            group.Add(other);
                        }
                    }

                // On a straight run its line's two edges face the same way. At a corner or a line's end they do not: the twist in a plate gathers
                // into a point force at a corner, which is no shear across anything, and a wall's end is a load at a point.
                var size = edges == 0 ? 0 : Math.Sqrt(du * du + dv * dv) / edges;
                if (size < 0.9) continue;
                sides.Add((node, group, length, Flat(node), du / edges / size, dv / edges / size));
            }
        }

        // The sides along the same line within half a metre, which go together.
        var stretches = sides.Select(side => Enumerable.Range(0, sides.Count).Where(i =>
            sides[i].At.DistanceTo(side.At) <= 500 && sides[i].AcrossU * side.AcrossU + sides[i].AcrossV * side.AcrossV > 0.5).ToArray()).ToArray();

        var normal = new StructuralSolver.Vector3(panel.Normal.X, panel.Normal.Y, panel.Normal.Z);
        var results = new List<(LoadCombination, IReadOnlyList<PlateShear>)>();
        foreach (var (combination, forces) in ByCombination(shells.Count, moved =>
            shells.Select(s => solver.ShellCornerForces(_factored.Shells[s], moved).Select(force => force.Dot(normal)).ToArray()).ToArray()))
        {
            var pushed = sides.Select(side => side.Triangles.Sum(t => forces[t][Array.IndexOf(corners[t], side.Node)])).ToArray();
            var shears = new List<PlateShear>(sides.Count);
            for (var i = 0; i < sides.Count; i++)
            {
                var (total, length) = (0.0, 0.0);
                foreach (var j in stretches[i])
                {
                    total += pushed[j];
                    length += sides[j].Length;
                }

                shears.Add(new PlateShear(sides[i].At, Math.Abs(total) / length, sides[i].AcrossU, sides[i].AcrossV));
            }

            results.Add((combination, shears));
        }

        return results;
    }

    /// <summary>
    /// What a plate itself puts on a node under each combination each way it leans, kN: the punching force at a column. Not the column's force, which has the storeys over it and all
    /// that reaches it by beams, none of which comes through the slab.
    /// </summary>
    public IReadOnlyList<(LoadCombination Combination, double Force)> PlateForceOn(AnalyticalPanel panel, AnalyticalNode node)
    {
        var solver = Analysis.Solver;
        var at = Analysis.NodeOf(node);
        var shells = Analysis.ShellsOf(panel).Where(s => solver.Shells[s].I == at || solver.Shells[s].J == at || solver.Shells[s].K == at).ToList();
        var normal = new StructuralSolver.Vector3(panel.Normal.X, panel.Normal.Y, panel.Normal.Z);
        var corners = shells.Select(s => Array.IndexOf(new[] { solver.Shells[s].I, solver.Shells[s].J, solver.Shells[s].K }, at)).ToList();
        return ByCombination(shells.Count, moved => shells.Select(s => solver.ShellCornerForces(_factored.Shells[s], moved).Select(force => force.Dot(normal)).ToArray()).ToArray())
            .Select(entry => (entry.Combination, Math.Abs(Enumerable.Range(0, shells.Count).Sum(t => entry.Figures[t][corners[t]])) / 1000)).ToList();
    }

    /// <summary>
    /// Where a plate is held, on the plate: the nodes it shares with walls going down from it, with beams in it and with columns under
    /// it, which are marked. A wall standing on it loads it, and does not hold it.
    /// </summary>
    public IReadOnlyList<(Geometry.Point2D At, bool Column)> HeldAt(AnalyticalPanel panel)
    {
        var solver = Analysis.Solver;
        var shells = Analysis.ShellsOf(panel).ToHashSet();
        var own = shells.SelectMany(s => new[] { solver.Shells[s].I, solver.Shells[s].J, solver.Shells[s].K }).ToHashSet();
        var up = panel.Normal.Z >= 0 ? 1.0 : -1.0;
        double Above(int node)
        {
            var at = solver.NodeAt(node);
            return up * ((at.X - panel.Origin.X) * panel.Normal.X + (at.Y - panel.Origin.Y) * panel.Normal.Y + (at.Z - panel.Origin.Z) * panel.Normal.Z);
        }

        var held = new Dictionary<int, bool>();
        void Hold(int node, bool column) => held[node] = column || held.GetValueOrDefault(node);
        for (var s = 0; s < solver.Shells.Count; s++)
        {
            if (shells.Contains(s)) continue;
            var other = new[] { solver.Shells[s].I, solver.Shells[s].J, solver.Shells[s].K };
            if (!other.Any(node => Above(node) < -10)) continue;
            foreach (var node in other.Where(own.Contains)) Hold(node, false);
        }

        foreach (var beam in solver.Beams)
        {
            if (own.Contains(beam.I) && own.Contains(beam.J))
            {
                Hold(beam.I, false);
                Hold(beam.J, false);
            }
            else if (own.Contains(beam.I) && Above(beam.J) < -10) Hold(beam.I, true);
            else if (own.Contains(beam.J) && Above(beam.I) < -10) Hold(beam.J, true);
        }

        return held.Select(entry =>
        {
            var at = solver.NodeAt(entry.Key);
            return (panel.Flat(new Geometry.Point3D(at.X, at.Y, at.Z)), entry.Value);
        }).ToList();
    }

    /// <summary>A plate's triangles: which they are, their areas, where their middles lie on it, and each's moments, sagging positive, in the plate's axes under each combination each way it leans.</summary>
    private (IReadOnlyList<int> Shells, double[] Areas, Geometry.Point2D[] Centres, IReadOnlyList<(LoadCombination Combination, double[][] Moments)> Cases) PlateField(AnalyticalPanel panel)
    {
        var solver = Analysis.Solver;
        var shells = Analysis.ShellsOf(panel).ToList();
        var (u, v) = (new StructuralSolver.Vector3(panel.U.X, panel.U.Y, panel.U.Z), new StructuralSolver.Vector3(panel.V.X, panel.V.Y, panel.V.Z));
        var areas = new double[shells.Count];
        var signs = new double[shells.Count];
        var centres = new Geometry.Point2D[shells.Count];
        var turns = new (double XU, double YU, double XV, double YV)[shells.Count];
        for (var i = 0; i < shells.Count; i++)
        {
            var shell = solver.Shells[shells[i]];
            var corners = new[] { shell.I, shell.J, shell.K }.Select(node => StructuralSolver.Vector3.Of(solver.NodeAt(node))).ToArray();
            areas[i] = (corners[1] - corners[0]).Cross(corners[2] - corners[0]).Length / 2;
            var middle = (corners[0] + corners[1] + corners[2]) * (1.0 / 3);
            centres[i] = panel.Flat(new Geometry.Point3D(middle.X, middle.Y, middle.Z));
            var (x, y, z) = solver.Axes(shell);
            turns[i] = (x.Dot(u), y.Dot(u), x.Dot(v), y.Dot(v));
            signs[i] = StructuralAnalysis.SaggingSign(z, panel);
        }

        return (shells, areas, centres, ByCombination(shells.Count, moved =>
        {
            var result = new double[shells.Count][];
            for (var i = 0; i < shells.Count; i++)
            {
                var (xu, yu, xv, yv) = turns[i];
                var m = solver.ShellMoments(_factored.Shells[shells[i]], moved);
                var sign = signs[i];
                result[i] = new[]
                {
                    sign * (m[0] * xu * xu + m[1] * yu * yu + 2 * m[2] * xu * yu),
                    sign * (m[0] * xv * xv + m[1] * yv * yv + 2 * m[2] * xv * yv),
                    sign * (m[0] * xu * xv + m[1] * yu * yv + m[2] * (xu * yv + yu * xv))
                };
            }

            return result;
        }));
    }

    /// <summary>
    /// Whatever is read from a movement, three figures to a triangle, under each combination each way it leans: each case and lean read
    /// once, then added up by the combination's factors, with what second order adds.
    /// </summary>
    private IReadOnlyList<(LoadCombination Combination, double[][] Figures)> ByCombination(int count, Func<double[], double[][]> read)
    {
        var byCase = _moved.ToDictionary(entry => entry.Key, entry => read(entry.Value));
        var byLean = _leanMoved.ToDictionary(entry => entry.Key, entry => read(entry.Value));
        var results = new List<(LoadCombination, double[][])>();
        foreach (var (variant, combination, x, y, extra) in _variants)
        {
            var more = extra is null ? null : read(extra);
            var figures = new double[count][];
            for (var i = 0; i < count; i++)
            {
                var sum = new double[3];
                foreach (var (loadCase, factor) in combination.Terms)
                {
                    var own = byCase[loadCase][i];
                    for (var k = 0; k < 3; k++) sum[k] += factor * own[k];
                    if (loadCase.Lateral) continue;
                    var (leanX, leanY) = (byLean[(loadCase, 0)][i], byLean[(loadCase, 1)][i]);
                    for (var k = 0; k < 3; k++) sum[k] += factor * (x * leanX[k] + y * leanY[k]);
                }

                if (more is not null)
                    for (var k = 0; k < 3; k++) sum[k] += more[i][k];
                figures[i] = sum;
            }

            results.Add((variant, figures));
        }

        return results;
    }

    /// <summary>
    /// A wall's forces under each combination each way it leans, in stretches about a metre long and three bands of
    /// its height - the foot, the top and the three fifths between - each the average over the triangles in it, in the wall's
    /// own axes: up it and along it. What is left of the moment up it without the wind is kept apart, for the eccentricity of the load.
    /// </summary>
    public IReadOnlyList<(LoadCombination Combination, IReadOnlyList<WallStretch> Bands)> WallStretches(AnalyticalPanel panel, double height)
    {
        var solver = Analysis.Solver;
        var shells = Analysis.ShellsOf(panel).ToList();
        var (u, v) = (new StructuralSolver.Vector3(panel.U.X, panel.U.Y, panel.U.Z), new StructuralSolver.Vector3(panel.V.X, panel.V.Y, panel.V.Z));
        var flat = panel.FlatOutline;
        var (first, bottom) = (flat.Min(point => point.X), flat.Min(point => point.Y));

        // Where each triangle lies, and how its axes turn into the wall's.
        var cells = new Dictionary<(int Stretch, int Band), List<int>>();
        var signs = new double[shells.Count];
        var areas = new double[shells.Count];
        var turns = new (double XU, double YU, double XV, double YV)[shells.Count];
        for (var i = 0; i < shells.Count; i++)
        {
            var shell = solver.Shells[shells[i]];
            var corners = new[] { shell.I, shell.J, shell.K }.Select(node => StructuralSolver.Vector3.Of(solver.NodeAt(node))).ToArray();
            areas[i] = (corners[1] - corners[0]).Cross(corners[2] - corners[0]).Length / 2;
            var middle = (corners[0] + corners[1] + corners[2]) * (1.0 / 3);
            var at = panel.Flat(new Geometry.Point3D(middle.X, middle.Y, middle.Z));
            var up = at.Y - bottom;
            var band = up < height / 5 ? 0 : up > height * 4 / 5 ? 2 : 1;
            var key = ((int)Math.Floor((at.X - first) / 1000), band);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
            list.Add(i);
            var (x, y, z) = solver.Axes(shell);
            turns[i] = (x.Dot(u), y.Dot(u), x.Dot(v), y.Dot(v));
            signs[i] = StructuralAnalysis.SaggingSign(z, panel);
        }

        // Each case's forces in the wall's axes: up it, along-and-up, the moment up it, the moment along it.
        double[][] InWall(StructuralSolver.Prestress? stress, double[] moved)
        {
            var result = new double[shells.Count][];
            for (var i = 0; i < shells.Count; i++)
            {
                var (xu, yu, xv, yv) = turns[i];
                var m = solver.ShellMoments(_factored.Shells[shells[i]], moved);
                double Up(double a, double b, double c) => a * xv * xv + b * yv * yv + 2 * c * xv * yv;
                double Along(double a, double b, double c) => a * xu * xu + b * yu * yu + 2 * c * xu * yu;
                double Across(double a, double b, double c) => a * xu * xv + b * yu * yv + c * (xu * yv + yu * xv);
                var (nx, ny, nxy) = stress is null ? (0.0, 0.0, 0.0) : (stress.Nx[shells[i]], stress.Ny[shells[i]], stress.Nxy[shells[i]]);
                result[i] = new[] { Up(nx, ny, nxy), Across(nx, ny, nxy), signs[i] * Up(m[0], m[1], m[2]), signs[i] * Along(m[0], m[1], m[2]) };
            }

            return result;
        }

        var byCase = _moved.ToDictionary(entry => entry.Key, entry => InWall(_stress[entry.Key], entry.Value));
        var byLean = _leanMoved.ToDictionary(entry => entry.Key, entry => InWall(null, entry.Value));

        var results = new List<(LoadCombination, IReadOnlyList<WallStretch>)>();
        foreach (var (variant, combination, x, y, extra) in _variants)
        {
            var more = extra is null ? null : InWall(null, extra);
            var bands = new List<WallStretch>();
            foreach (var ((stretch, band), members) in cells)
            {
                double vertical = 0, shear = 0, lateral = 0, momentUp = 0, momentAlong = 0, gravity = 0, total = 0;
                foreach (var i in members)
                {
                    var area = areas[i];
                    total += area;
                    foreach (var (loadCase, factor) in combination.Terms)
                    {
                        var own = byCase[loadCase][i];
                        vertical += area * factor * own[0];
                        shear += area * factor * own[1];
                        momentUp += area * factor * own[2];
                        momentAlong += area * factor * own[3];
                        if (loadCase.Lateral)
                        {
                            lateral += area * factor * own[1];
                            continue;
                        }

                        gravity += area * factor * own[2];
                        var (leanX, leanY) = (byLean[(loadCase, 0)][i], byLean[(loadCase, 1)][i]);
                        momentUp += area * factor * (x * leanX[2] + y * leanY[2]);
                        momentAlong += area * factor * (x * leanX[3] + y * leanY[3]);
                        gravity += area * factor * (x * leanX[2] + y * leanY[2]);
                    }

                    if (more is null) continue;
                    momentUp += area * more[i][2];
                    momentAlong += area * more[i][3];
                    gravity += area * more[i][2];
                }

                if (total < 5e4) continue;
                bands.Add(new WallStretch(band != 1, vertical / total, shear / total, momentUp / total, momentAlong / total, gravity / total, band, total / (band == 1 ? height * 3 / 5 : height / 5), stretch, lateral / total));
            }

            results.Add((variant, bands));
        }

        return results;
    }

    /// <summary>
    /// Each end of each piece of a member under each combination each way it leans, from the movements of
    /// the analysis at its reduced stiffnesses: each case's forces and each lean's worked once and added up, so
    /// many members stay quick, and what second order adds put on top.
    /// </summary>
    public IReadOnlyList<(LoadCombination Combination, MemberForces Forces)> StationsOf(AnalyticalMember member)
    {
        var solver = Analysis.Solver;
        var ups = Analysis.BeamsOf(member).Select(piece => solver.Axes(solver.Beams[piece.Beam]).Z.Z).ToList();
        var stations = new List<(LoadCombination, MemberForces)>();
        foreach (var (variant, piece, f) in PieceForces(member))
        {
            // Its top is in tension where a moment puts its side towards the sky in tension; a column's sides are neither.
            var up = ups[piece];
            bool Top(double my) => Math.Abs(up) > 0.5 && my * up > 0;
            stations.Add((variant, new MemberForces(-f[0] / 1e3, f[1] / 1e3, f[2] / 1e3, -f[3] / 1e6, -f[4] / 1e6, -f[5] / 1e6, Top(-f[4]))));
            stations.Add((variant, new MemberForces(f[6] / 1e3, -f[7] / 1e3, -f[8] / 1e3, f[9] / 1e6, f[10] / 1e6, f[11] / 1e6, Top(f[10]))));
        }

        return stations;
    }

    /// <summary>
    /// What a member carries along it under one combination, by the name the strength checks give it: how far along, mm, and its forces at each end of each piece, so a load where two pieces
    /// meet shows as the step it is. These are the forces the checks read, so what is drawn is what was checked. Empty for a combination it has no forces under.
    /// </summary>
    public IReadOnlyList<(double At, MemberForces Forces)> DiagramOf(AnalyticalMember member, string combination)
    {
        var pieces = Analysis.BeamsOf(member).ToList();
        var stations = StationsOf(member).Where(station => station.Combination.Name == combination).ToList();
        if (stations.Count != pieces.Count * 2) return Array.Empty<(double, MemberForces)>();
        return stations.Select((station, i) => (i % 2 == 0 ? pieces[i / 2].From : pieces[i / 2].To, station.Forces)).ToList();
    }

    /// <summary>
    /// What a member puts down on each of its nodes under each combination each way it leans, kN, its own load along it in: what a wall or a padstone under it takes.
    /// Each piece's end forces hold it up, so what they add to at a node, upward, is what it bears on that node with.
    /// </summary>
    public IReadOnlyList<(LoadCombination Combination, IReadOnlyDictionary<AnalyticalNode, double> Down)> LandingsOf(AnalyticalMember member)
    {
        var solver = Analysis.Solver;
        var nodes = member.Nodes.ToDictionary(Analysis.NodeOf, node => node);
        var pieces = Analysis.BeamsOf(member).Select(piece => solver.Beams[piece.Beam]).ToList();
        var ups = pieces.Select(piece => solver.Axes(piece)).Select(axes => (axes.X.Z, axes.Y.Z, axes.Z.Z)).ToList();
        var results = new List<(LoadCombination, IReadOnlyDictionary<AnalyticalNode, double>)>();
        Dictionary<AnalyticalNode, double>? down = null;
        LoadCombination? current = null;
        foreach (var (variant, piece, f) in PieceForces(member, false))
        {
            if (!ReferenceEquals(variant, current))
            {
                down = new Dictionary<AnalyticalNode, double>();
                results.Add((variant, down));
                current = variant;
            }

            var (x, y, z) = ups[piece];
            if (nodes.TryGetValue(pieces[piece].I, out var start)) down![start] = down.GetValueOrDefault(start) + (f[0] * x + f[1] * y + f[2] * z) / 1e3;
            if (nodes.TryGetValue(pieces[piece].J, out var end)) down![end] = down.GetValueOrDefault(end) + (f[6] * x + f[7] * y + f[8] * z) / 1e3;
        }

        return results;
    }

    private readonly Dictionary<AnalyticalMember, double> _imposedShares = new();

    /// <summary>
    /// The share of the imposed load a member is checked for, where the project reduces it. BS EN 1991-1-1 6.3.1.2(10) and (11) as the UK National Annex has them: a beam by the area it carries,
    /// 1 - A/1000 and no less than 0.75, a column by the floors over it, 1.1 - n/10 for two to five, 0.6 to ten and 0.5 past ten, a published table of the Annex's agreeing. Or ASCE 7-16 4.7:
    /// 0.25 + 15 / √(KLL AT), KLL 4 for a column and 2 for a beam, where KLL AT is 400 ft² or more, no less than 0.5, or 0.4 for a column carrying two floors or more; a live load over 100 psf
    /// not reduced, but by 20% on a column carrying two floors or more, by 4.7.3; an office's 15 psf for partitions, being 4.3.2's and not Table 4.3-1's, taken whole. The area is the imposed load its
    /// ends carry over the floors' own load a square metre. 1 where the project does not reduce it, and for walls and footings, which take it whole.
    /// </summary>
    public double ImposedShare(AnalyticalMember member)
    {
        if (_imposedShares.TryGetValue(member, out var known)) return known;
        var share = 1.0;
        var loading = Analysis.Loading;
        var imposed = loading.Cases.FirstOrDefault(loadCase => loadCase.Nature == LoadNature.Imposed && !loadCase.Drifted);
        var intensity = imposed is null ? 0 : loading.SurfaceLoads.Where(load => load.Case == imposed).Select(load => Math.Abs(load.Force.Z)).DefaultIfEmpty(0).Max();
        if (imposed is not null && intensity > 0 && ReducesImposed && _moved.TryGetValue(imposed, out var moved))
        {
            var solver = Analysis.Solver;
            var pieces = Analysis.BeamsOf(member).Select(piece => piece.Beam).ToList();
            double[] End(int piece)
            {
                var (matrix, dofs) = solver.BeamForceMatrix(_factored.Beams[piece]);
                var f = new double[12];
                for (var i = 0; i < 12; i++)
                for (var j = 0; j < 12; j++)
                    f[i] += matrix[i, j] * moved[dofs[j]];
                if (Analysis.LoadAlong(imposed, piece) is { } along)
                {
                    var load = solver.CondensedLoad(_factored.Beams[piece], along);
                    for (var i = 0; i < 12; i++) f[i] -= load[i];
                }

                return f;
            }

            var column = member.Kind == AnalyticalMemberKind.Column;
            if (pieces.Count > 0)
            {
                var (first, last) = (End(pieces[0]), End(pieces[^1]));
                var carried = column
                    ? pieces.Select(piece => Math.Abs(End(piece)[0])).Max() / 1000
                    : (Math.Sqrt(first[1] * first[1] + first[2] * first[2]) + Math.Sqrt(last[7] * last[7] + last[8] * last[8])) / 1000;
                var area = carried / intensity;
                var bottom = member.Nodes.Min(node => node.At.Z);
                var floors = loading.SurfaceLoads.Where(load => load.Case == imposed).Select(load => Math.Round(load.Panel.Outline.Average(node => node.At.Z) / 100))
                    .Concat(loading.LineLoads.Where(load => load.Case == imposed && load.Panel is not null).Select(load => Math.Round(load.Panel!.Outline.Max(node => node.At.Z) / 100)))
                    .Distinct().Count(z => z * 100 > bottom + 100);
                if (_usImposed)
                {
                    // An office's 15 psf for partitions is 4.3.2's, not Table 4.3-1's, and is not reduced.
                    var partitions = loading.SurfaceLoads.Any(load => load.Case == imposed && load.Said.Contains("partitions, 15 psf", StringComparison.Ordinal)) ? Math.Min(15 * Loads.Psf, intensity) : 0;
                    var lo = intensity - partitions;
                    var kll = column ? 4.0 : 2.0;
                    var influence = kll * area * 10.7639;
                    var reduced = lo > 100 * Loads.Psf + 1e-9 ? (column && floors >= 2 ? 0.8 : 1)
                        : influence >= 400 ? Math.Min(1, Math.Max(column && floors >= 2 ? 0.4 : 0.5, 0.25 + 15 / Math.Sqrt(influence))) : 1;
                    share = (lo * reduced + partitions) / intensity;
                }
                else
                    share = column ? floors switch { <= 1 => 1.0, <= 5 => 1.1 - floors / 10.0, <= 10 => 0.6, _ => 0.5 } : Math.Max(0.75, 1 - area / 1000);
            }
        }

        _imposedShares[member] = share;
        return share;
    }

    /// <summary>
    /// Each piece of a member's end forces, N and N mm in its own axes, under each combination each way it leans: each case and lean read once, then added up. Reduced, its imposed load is
    /// taken at the share the member is checked for; what it lands on what holds it up is not reduced.
    /// </summary>
    private IEnumerable<(LoadCombination Variant, int Piece, double[] Forces)> PieceForces(AnalyticalMember member, bool reduced = true)
    {
        var imposedShare = reduced ? ImposedShare(member) : 1;
        var solver = Analysis.Solver;
        var pieces = Analysis.BeamsOf(member).Select(piece => piece.Beam).ToList();
        var matrices = pieces.Select(piece => solver.BeamForceMatrix(_factored.Beams[piece])).ToList();

        double[] Forces(int piece, double[] u)
        {
            var (matrix, dofs) = matrices[piece];
            var f = new double[12];
            for (var i = 0; i < 12; i++)
            for (var j = 0; j < 12; j++)
                f[i] += matrix[i, j] * u[dofs[j]];
            return f;
        }

        var byCase = new Dictionary<LoadCase, double[][]>();
        foreach (var (loadCase, u) in _moved)
            byCase[loadCase] = pieces.Select((piece, index) =>
            {
                var f = Forces(index, u);
                if (Analysis.LoadAlong(loadCase, piece) is not { } along) return f;
                var load = solver.CondensedLoad(_factored.Beams[piece], along);
                for (var i = 0; i < 12; i++) f[i] -= load[i];
                return f;
            }).ToArray();
        var byLean = _leanMoved.ToDictionary(entry => entry.Key, entry => pieces.Select((_, index) => Forces(index, entry.Value)).ToArray());

        foreach (var (variant, combination, x, y, extra) in _variants)
            for (var piece = 0; piece < pieces.Count; piece++)
            {
                var f = new double[12];
                foreach (var (loadCase, term) in combination.Terms)
                {
                    var factor = loadCase.Nature == LoadNature.Imposed ? term * imposedShare : term;
                    var own = byCase[loadCase][piece];
                    for (var i = 0; i < 12; i++) f[i] += factor * own[i];
                    if (loadCase.Lateral) continue;
                    var (leanX, leanY) = (byLean[(loadCase, 0)][piece], byLean[(loadCase, 1)][piece]);
                    for (var i = 0; i < 12; i++) f[i] += factor * (x * leanX[i] + y * leanY[i]);
                }

                if (extra is not null)
                {
                    var more = Forces(piece, extra);
                    for (var i = 0; i < 12; i++) f[i] += more[i];
                }

                yield return (variant, piece, f);
            }
    }
}
