using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A mode the structure vibrates in: its number, frequency, Hz, its shape - six movements a node, scaled so its modal mass is one tonne - and the share of the structure's mass it moves each way.
/// </summary>
public sealed record Mode(int Number, double Frequency, double[] Shape, double ShareX, double ShareY, double ShareZ)
{
    public double Period => Frequency > 0 ? 1 / Frequency : double.PositiveInfinity;

    /// <summary>The way it mostly moves: along x, along y, or up and down.</summary>
    public string Way => ShareX >= ShareY && ShareX >= ShareZ ? "along x" : ShareY >= ShareZ ? "along y" : "up and down";
}

/// <summary>The structure's first modes, the mass they were worked from, tonnes, and what was taken.</summary>
public sealed record ModalResult(IReadOnlyList<Mode> Modes, double Mass, IReadOnlyList<string> Notes)
{
    /// <summary>The share of the mass the modes move each way between them.</summary>
    public (double X, double Y, double Z) Shares => (Modes.Sum(mode => mode.ShareX), Modes.Sum(mode => mode.ShareY), Modes.Sum(mode => mode.ShareZ));

    /// <summary>The fundamental period along x (0) or y (1), s: of the mode that moves the most mass that way; none where no mode does.</summary>
    public double? PeriodAlong(int axis)
    {
        var mode = Modes.Where(each => (axis == 0 ? each.ShareX : each.ShareY) > 0.05).MaxBy(each => axis == 0 ? each.ShareX : each.ShareY);
        return mode?.Period;
    }
}

/// <summary>
/// The structure's natural modes by subspace iteration (Bathe): K φ = ω² M φ with the stiffness the analysis has, factorised once, and the mass of the permanent loads with ψ2 0.3 of the floors'
/// imposed load, lumped at the nodes they are put on, each way; a block of vectors put through K⁻¹ M and the small problem they span solved by Jacobi rotations, until the first modes change
/// by less than a hundred thousandth a pass - joists side by side make a cluster of near equal modes that creeps on long after its frequencies are right. Stiffness at its full value: concrete uncracked, as for the vibration of floors.
/// </summary>
public static class ModalAnalysis
{
    public const int DefaultCount = 12;
    private const double Gravity = 9.81;

    public static ModalResult Of(StructuralAnalysis analysis, int count = DefaultCount)
    {
        var solver = analysis.Solver;
        var weight = new double[solver.NodeCount];
        foreach (var loadCase in analysis.Loading.Cases.Where(loadCase => !loadCase.Lateral))
        {
            var share = loadCase.Permanent ? 1.0 : loadCase.Nature == LoadNature.Imposed && !loadCase.Drifted ? 0.3 : 0;
            if (share <= 0) continue;
            var load = analysis.LoadOf(loadCase);
            for (var node = 0; node < solver.NodeCount; node++) weight[node] += share * Math.Max(0, -load[node * 6 + 2]);
        }

        // N to tonnes: with stiffness in N/mm, ω² comes out in 1/s².
        var result = Of(solver, weight.Select(each => each / Gravity / 1000).ToArray(), count);
        return result with
        {
            Notes = result.Notes.Append($"Natural modes by subspace iteration on the analysis's own stiffness, uncracked, and the mass of the permanent loads with ψ2 0.3 of the floors' imposed load, {result.Mass:0.0} t, lumped where each load is put on; the first {result.Modes.Count}.").ToList()
        };
    }

    /// <summary>The first modes of a structure with so many tonnes at each of its nodes, moving each way with them.</summary>
    public static ModalResult Of(StructuralSolver solver, IReadOnlyList<double> nodeMass, int count = DefaultCount)
    {
        var n = solver.NodeCount * 6;
        var notes = new List<string>();
        var mass = new double[n];
        for (var node = 0; node < solver.NodeCount; node++)
            for (var axis = 0; axis < 3; axis++)
                mass[node * 6 + axis] = Math.Max(0, nodeMass[node]);
        var total = nodeMass.Sum(each => Math.Max(0, each));
        var massed = Enumerable.Range(0, n).Count(dof => mass[dof] > 0);
        if (total <= 0 || massed == 0) return new ModalResult(Array.Empty<Mode>(), 0, new[] { "No mass to move: no loads on the structure." });

        count = Math.Min(count, massed);
        var p = Math.Min(count + Math.Max(count, 16), massed);
        var factored = solver.Factorise();
        foreach (var problem in factored.Problems) notes.Add(problem);

        // Starting vectors: the mass moved each way as a whole, then the mass at scattered nodes.
        var random = new Random(47);
        var x = new double[p][];
        for (var j = 0; j < p; j++)
        {
            x[j] = new double[n];
            for (var dof = 0; dof < n; dof++)
                if (mass[dof] > 0)
                    x[j][dof] = j < 3 ? (dof % 6 == j ? 1 : 0) : random.NextDouble() - 0.5;
        }

        double[] values = new double[p];
        var previous = Enumerable.Repeat(double.NaN, count).ToArray();
        for (var pass = 0; pass < 80; pass++)
        {
            var mx = x.Select(vector => Times(mass, vector)).ToArray();
            var y = mx.Select(factored.Solve).ToArray();
            var reduced = new double[p, p];
            var massReduced = new double[p, p];
            for (var i = 0; i < p; i++)
            {
                var my = Times(mass, y[i]);
                for (var j = i; j < p; j++)
                {
                    reduced[i, j] = reduced[j, i] = Dot(y[i], mx[j]) * 0.5 + Dot(y[j], mx[i]) * 0.5;
                    massReduced[i, j] = massReduced[j, i] = Dot(y[j], my);
                }
            }

            var (found, vectors) = Generalised(reduced, massReduced);
            values = found;
            for (var j = 0; j < p; j++)
            {
                var next = new double[n];
                for (var i = 0; i < p; i++)
                {
                    var q = vectors[i, j];
                    if (q == 0) continue;
                    var from = y[i];
                    for (var dof = 0; dof < n; dof++) next[dof] += q * from[dof];
                }

                x[j] = next;
            }

            var settled = Enumerable.Range(0, count).All(i => !double.IsNaN(previous[i]) && Math.Abs(values[i] - previous[i]) <= 1e-5 * Math.Max(Math.Abs(values[i]), 1e-9));
            Array.Copy(values, previous, count);
            if (settled) break;
            if (pass == 79) notes.Add("The modes had not quite settled after 80 passes: the last are as they stood.");
        }

        var modes = new List<Mode>();
        for (var k = 0; k < count; k++)
        {
            var shape = x[k];
            var modal = Dot(shape, Times(mass, shape));
            if (modal <= 0 || values[k] <= 0) continue;
            var scale = 1 / Math.Sqrt(modal);
            for (var dof = 0; dof < n; dof++) shape[dof] *= scale;
            double Share(int axis)
            {
                var gamma = 0.0;
                for (var node = 0; node < solver.NodeCount; node++) gamma += mass[node * 6 + axis] * shape[node * 6 + axis];
                return gamma * gamma / total;
            }

            modes.Add(new Mode(modes.Count + 1, Math.Sqrt(values[k]) / (2 * Math.PI), shape, Share(0), Share(1), Share(2)));
        }

        return new ModalResult(modes, total, notes);
    }

    private static double[] Times(double[] mass, double[] vector)
    {
        var result = new double[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = mass[i] * vector[i];
        return result;
    }

    private static double Dot(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>
    /// K q = λ M q for small symmetric K and positive definite M: M = L Lᵀ, then L⁻¹ K L⁻ᵀ by Jacobi rotations; the values ascending and their vectors, each a column, M-normal.
    /// </summary>
    public static (double[] Values, double[,] Vectors) Generalised(double[,] k, double[,] m)
    {
        var p = k.GetLength(0);
        var l = new double[p, p];
        for (var i = 0; i < p; i++)
        for (var j = 0; j <= i; j++)
        {
            var sum = m[i, j];
            for (var r = 0; r < j; r++) sum -= l[i, r] * l[j, r];
            if (i == j) l[i, i] = Math.Sqrt(Math.Max(sum, 1e-30 * Math.Max(m[i, i], 1e-30)));
            else l[i, j] = sum / l[j, j];
        }

        // C = L⁻¹ K L⁻ᵀ.
        var w = new double[p, p];
        for (var col = 0; col < p; col++)
        for (var i = 0; i < p; i++)
        {
            var sum = k[i, col];
            for (var r = 0; r < i; r++) sum -= l[i, r] * w[r, col];
            w[i, col] = sum / l[i, i];
        }

        var c = new double[p, p];
        for (var row = 0; row < p; row++)
        for (var i = 0; i < p; i++)
        {
            var sum = w[row, i];
            for (var r = 0; r < i; r++) sum -= l[i, r] * c[row, r];
            c[row, i] = sum / l[i, i];
        }

        for (var i = 0; i < p; i++)
        for (var j = i + 1; j < p; j++)
            c[i, j] = c[j, i] = 0.5 * (c[i, j] + c[j, i]);

        var (values, v) = Jacobi(c);

        // q = L⁻ᵀ v.
        var q = new double[p, p];
        for (var col = 0; col < p; col++)
        for (var i = p - 1; i >= 0; i--)
        {
            var sum = v[i, col];
            for (var r = i + 1; r < p; r++) sum -= l[r, i] * q[r, col];
            q[i, col] = sum / l[i, i];
        }

        var order = Enumerable.Range(0, p).OrderBy(i => values[i]).ToArray();
        var sorted = new double[p, p];
        for (var j = 0; j < p; j++)
        for (var i = 0; i < p; i++)
            sorted[i, j] = q[i, order[j]];
        return (order.Select(i => values[i]).ToArray(), sorted);
    }

    /// <summary>A symmetric matrix's eigenvalues and eigenvectors, each a column, by cyclic Jacobi rotations.</summary>
    public static (double[] Values, double[,] Vectors) Jacobi(double[,] a)
    {
        var p = a.GetLength(0);
        var m = (double[,])a.Clone();
        var v = new double[p, p];
        for (var i = 0; i < p; i++) v[i, i] = 1;
        for (var sweep = 0; sweep < 100; sweep++)
        {
            var off = 0.0;
            for (var i = 0; i < p; i++)
            for (var j = i + 1; j < p; j++)
                off += m[i, j] * m[i, j];
            var scale = 0.0;
            for (var i = 0; i < p; i++) scale += m[i, i] * m[i, i];
            if (off <= 1e-26 * Math.Max(scale, 1e-300)) break;
            for (var i = 0; i < p; i++)
            for (var j = i + 1; j < p; j++)
            {
                if (Math.Abs(m[i, j]) < 1e-300) continue;
                var theta = (m[j, j] - m[i, i]) / (2 * m[i, j]);
                var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                var (cos, sin) = (1 / Math.Sqrt(t * t + 1), 0.0);
                sin = t * cos;
                for (var r = 0; r < p; r++)
                {
                    var (ri, rj) = (m[r, i], m[r, j]);
                    m[r, i] = cos * ri - sin * rj;
                    m[r, j] = sin * ri + cos * rj;
                }

                for (var r = 0; r < p; r++)
                {
                    var (ir, jr) = (m[i, r], m[j, r]);
                    m[i, r] = cos * ir - sin * jr;
                    m[j, r] = sin * ir + cos * jr;
                }

                for (var r = 0; r < p; r++)
                {
                    var (ri, rj) = (v[r, i], v[r, j]);
                    v[r, i] = cos * ri - sin * rj;
                    v[r, j] = sin * ri + cos * rj;
                }
            }
        }

        return (Enumerable.Range(0, p).Select(i => m[i, i]).ToArray(), v);
    }

    /// <summary>ASCE 7-16 Table 12.8-1's Cu by SD1, straight between its rows.</summary>
    public static double UpperLimit(double sd1)
    {
        (double Sd1, double Cu)[] rows = { (0.1, 1.7), (0.15, 1.6), (0.2, 1.5), (0.3, 1.4), (0.4, 1.4) };
        if (sd1 <= rows[0].Sd1) return rows[0].Cu;
        for (var i = 0; i + 1 < rows.Length; i++)
            if (sd1 <= rows[i + 1].Sd1)
                return rows[i].Cu + (rows[i + 1].Cu - rows[i].Cu) * (sd1 - rows[i].Sd1) / (rows[i + 1].Sd1 - rows[i].Sd1);
        return rows[^1].Cu;
    }
}

/// <summary>A timber joisted floor's vibration by BS EN 1995-1-1 7.3.3: what it is taken as and its checks.</summary>
public sealed record FloorVibration(Floor Floor, int Joists, double Span, double Spacing, double Width, double Mass, double Stiffness, double Frequency, IReadOnlyList<StrengthCheck> Checks, string Said)
{
    public double Ratio => Checks.Select(check => check.Ratio).DefaultIfEmpty(0).Max();
    public bool Enough => Ratio <= 1;
}

/// <summary>
/// A residential timber floor's vibration, BS EN 1995-1-1 7.3.3 with the UK National Annex, for each floor on drawn joists: unloaded, the mass of its build-up and its joists alone (7.3.3(3));
/// its fundamental frequency by (7.5), f1 = π / 2ℓ² √((EI)ℓ / m), at least 8 Hz or a special investigation is wanted; its deflection under a 1 kN point load, the Annex's 1000 kdist ℓ³ kamp /
/// 48 EI with kdist from the deck's stiffness across the joists, against a = 1.8 mm for spans to 4 m and 16500 / ℓ^1.1 past; and its unit impulse velocity by (7.6) and (7.7) against b^(f1 ζ - 1),
/// b = 180 - 60a to a of 1 and 160 - 40a past, ζ 0.02. The Annex's figures as remembered. The deck taken as 22 mm boards at 3,500 N/mm², the floors' types not saying what decks them.
/// </summary>
public static class FloorVibrations
{
    private const double Deck = 3500 * 22.0 * 22 * 22 / 12 * 1000;

    public static IReadOnlyList<FloorVibration> Of(BimDocument document, StructuralAnalysis analysis)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return Array.Empty<FloorVibration>();
        var results = new List<FloorVibration>();
        var joists = analysis.Model.Members.Where(member => member.Kind == AnalyticalMemberKind.Joist && member.Section.Material.Kind == AnalyticalMaterialKind.Timber).ToList();
        foreach (var floor in document.All<Floor>().Where(floor => floor.Boundary.Count >= 3 && !AnalyticalModel.IsPlate(document, floor)))
        {
            var level = floor.GetBottomElevation(document);
            var under = joists.Where(joist => Math.Abs((joist.Start.At.Z + joist.End.At.Z) / 2 - level) < 600
                                              && floor.Contains(new Point2D((joist.Start.At.X + joist.End.At.X) / 2, (joist.Start.At.Y + joist.End.At.Y) / 2))).ToList();
            if (under.Count < 2) continue;
            var way = new Vector2D(under[0].End.At.X - under[0].Start.At.X, under[0].End.At.Y - under[0].Start.At.Y).NormalisedOrDefault(Vector2D.UnitX);
            under = under.Where(joist => Math.Abs(new Vector2D(joist.End.At.X - joist.Start.At.X, joist.End.At.Y - joist.Start.At.Y).NormalisedOrDefault(Vector2D.UnitX).Cross(way)) < 0.1).ToList();

            // Bay by bay: joists running over the same stretch of the span, side by side - a floor over a spine wall is two bays.
            (double Low, double High) Run(AnalyticalMember joist)
            {
                var (a, b) = ((joist.Start.At.X * way.X + joist.Start.At.Y * way.Y), (joist.End.At.X * way.X + joist.End.At.Y * way.Y));
                return (Math.Min(a, b), Math.Max(a, b));
            }

            var bays = new List<(double Low, double High, List<AnalyticalMember> Joists)>();
            foreach (var joist in under.OrderBy(joist => Run(joist).Low))
            {
                var (low, high) = Run(joist);
                var bay = bays.FindIndex(each => Math.Min(each.High, high) - Math.Max(each.Low, low) > 0.5 * (high - low));
                if (bay < 0) bays.Add((low, high, new List<AnalyticalMember> { joist }));
                else
                {
                    bays[bay].Joists.Add(joist);
                    bays[bay] = (Math.Min(bays[bay].Low, low), Math.Max(bays[bay].High, high), bays[bay].Joists);
                }
            }

            foreach (var (_, _, bay) in bays.Where(bay => bay.Joists.Count >= 2))
                results.AddRange(Bay(document, analysis, floor, bay, way));
        }

        return results;
    }

    /// <summary>One bay of a floor's joists, side by side over the same span.</summary>
    private static IEnumerable<FloorVibration> Bay(BimDocument document, StructuralAnalysis analysis, Floor floor, List<AnalyticalMember> under, Vector2D way)
    {
        {
            var across = new Vector2D(-way.Y, way.X);
            var places = under.Select(joist => new Point2D((joist.Start.At.X + joist.End.At.X) / 2, (joist.Start.At.Y + joist.End.At.Y) / 2) - default(Point2D)).Select(at => at.Dot(across)).OrderBy(at => at).ToList();
            var gaps = places.Zip(places.Skip(1), (a, b) => b - a).Where(gap => gap > 50).OrderBy(gap => gap).ToList();
            if (gaps.Count == 0) yield break;
            var spacing = gaps[gaps.Count / 2];
            var span = under.Max(joist => analysis.DeflectionOf(joist, analysis.Loading.Cases[0]).Span is var found && found > 0 ? found : joist.Length);
            var width = places[^1] - places[0] + spacing;
            var section = under.MaxBy(joist => joist.Section.Iy)!.Section;
            var stiffness = under.Min(joist => joist.Section.Material.Modulus * joist.Section.Iy);

            // Unloaded: its build-up and its joists, kg/m².
            var type = document.FindType<SlabType>(floor.TypeId);
            var mass = (type is null ? 0 : Loads.Weight(document, type.Structure) * 1000 / 9.81) + section.Area * 1e-6 * section.Material.Density / (spacing / 1000);
            var (l, b) = (span / 1000, width / 1000);
            var along = stiffness / (spacing / 1000) * 1e-6;
            var frequency = Math.PI / (2 * l * l) * Math.Sqrt(along / mass);

            var deck = Deck;
            var kdist = Math.Max(0.38 - 0.08 * Math.Log(14 * deck / Math.Pow(spacing, 4)), 0.30);
            var deflection = 1000 * kdist * Math.Pow(span, 3) * 1.05 / (48 * stiffness);
            var a = span <= 4000 ? 1.8 : 16500 / Math.Pow(span, 1.1);
            var bLimit = a <= 1 ? 180 - 60 * a : 160 - 40 * a;
            var plate = deck * 1e-6;
            var n40 = Math.Pow(Math.Max(0, (Math.Pow(40 / frequency, 2) - 1) * Math.Pow(b / l, 4) * along / plate), 0.25);
            var velocity = 4 * (0.4 + 0.6 * n40) / (mass * b * l + 200);
            var velocityLimit = Math.Pow(bLimit, frequency * 0.02 - 1);

            var checks = new List<StrengthCheck>
            {
                new("Fundamental frequency", 8, frequency, "Hz", "7.3.3(1)", frequency > 8 ? $"{frequency:0.0} Hz, over 8 Hz" : $"{frequency:0.0} Hz: under 8 Hz a special investigation is to be made"),
                new("Deflection under 1 kN", deflection, a, "mm", "7.3.3(2), (7.3)", $"kdist {kdist:0.00}, kamp 1.05, a {a:0.00} mm/kN"),
                new("Unit impulse velocity", velocity * 1000, velocityLimit * 1000, "mm/Ns²", "7.3.3(2), (7.4)", $"n40 {n40:0.0}, b {bLimit:0}, ζ 0.02")
            };
            yield return new FloorVibration(floor, under.Count, span, spacing, width, mass, along, frequency, checks,
                $"{under.Count} joists {section.Name} at {spacing:0} over {span / 1000:0.00} m, {width / 1000:0.0} m wide; {mass:0} kg/m² unloaded, (EI)ℓ {along / 1000:0.0} kNm²/m.");
        }
    }
}
