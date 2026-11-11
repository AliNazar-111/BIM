namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One way's modal response spectrum analysis, ASCE 7-16 12.9.1: how many modes were taken and the share of the mass they move that way, the modal base shear Vt they combine to, the equivalent
/// lateral force base shear V, kN, and what the forces were scaled by to reach it; each storey's combined shear, kN, from the lowest up, and the force at each level that makes it; and what drift
/// is taken at, of those scaled forces.
/// </summary>
public sealed record ModalWay(string Way, int Modes, double MassShare, double Combined, double Static, double Scale, IReadOnlyList<double> StoreyShears, IReadOnlyList<double> LevelForces, double DriftScale);

/// <summary>A building's modal response spectrum analysis: each way's, why it was done, and what was taken.</summary>
public sealed record ModalSeismic(IReadOnlyList<ModalWay> Ways, string Why, IReadOnlyList<string> Notes);

/// <summary>
/// ASCE 7-16's design response spectrum, 11.4.6, and the modal response spectrum analysis of 12.9.1: each mode's response to the spectrum at its period, by Ie / R, combined by the complete quadratic
/// combination with 5% damping, the modes the analysis did not find taken as one rigid mode at 0.05 s as 12.9.1.1 lets them be; its base shear scaled to the equivalent lateral force's, 12.9.1.4.1.
/// </summary>
public static class ResponseSpectrum
{
    /// <summary>Gravity, mm/s².</summary>
    private const double G = 9810;

    /// <summary>The period the modes not found are taken at, s, 12.9.1.1.</summary>
    public const double RigidPeriod = 0.05;

    /// <summary>
    /// The design spectral acceleration at a period, g, 11.4.6: SDS (0.4 + 0.6 T / T0) up to T0 = 0.2 SD1 / SDS, SDS to TS = SD1 / SDS, SD1 / T to TL, SD1 TL / T² past it.
    /// </summary>
    public static double Acceleration(double sds, double sd1, double period, double transition = 8)
    {
        if (sds <= 0) return 0;
        var (t0, ts) = (0.2 * sd1 / sds, sd1 / sds);
        if (period < t0) return sds * (0.4 + 0.6 * period / t0);
        if (period <= ts) return sds;
        return period <= transition ? sd1 / period : sd1 * transition / (period * period);
    }

    /// <summary>
    /// The correlation of two modes' responses for the complete quadratic combination, Der Kiureghian's, both damped alike: 8 ζ² (1 + r) r^1.5 / ((1 - r²)² + 4 ζ² r (1 + r)²), r the ratio of their
    /// circular frequencies. One for a mode with itself; small for modes well apart.
    /// </summary>
    public static double Correlation(double first, double second, double damping = 0.05)
    {
        if (first <= 0 || second <= 0) return 0;
        var r = second / first;
        var z = damping * damping;
        return 8 * z * (1 + r) * Math.Pow(r, 1.5) / (Math.Pow(1 - r * r, 2) + 4 * z * r * Math.Pow(1 + r, 2));
    }

    /// <summary>Modes' values of one response combined, √(Σ Σ ρij ri rj), their circular frequencies giving ρ.</summary>
    public static double Combine(IReadOnlyList<double> values, IReadOnlyList<double> frequencies, double damping = 0.05)
    {
        var sum = 0.0;
        for (var i = 0; i < values.Count; i++)
            for (var j = 0; j < values.Count; j++)
                sum += Correlation(frequencies[i], frequencies[j], damping) * values[i] * values[j];
        return Math.Sqrt(Math.Max(0, sum));
    }

    /// <summary>
    /// One way's analysis. The modes, mass normalised to a tonne; the seismic mass at each node, t, and its level, -1 for none; the axis, 0 for x and 1 for y; the spectrum and R and Ie; and the equivalent
    /// lateral force's V, kN, and whether its Cs came by (12.8-6), with Cs W: each mode's force at each level is its mass there by its shape by Γ Sa g Ie / R, its storeys' shears combined mode by mode and
    /// the rigid mode's added by the square root of the sum of squares; the forces scaled up to V where Vt is less, and drift taken at Vt unless (12.8-6) governs Cs, when at no less than 0.85 Cs W.
    /// </summary>
    public static ModalWay Of(string way, IReadOnlyList<Mode> modes, IReadOnlyList<double> mass, IReadOnlyList<int> levelOf, int levels, int axis, double sds, double sd1, double r, double importance,
        double staticShear, bool bySixth, double csW)
    {
        var total = mass.Sum();
        var count = mass.Count;
        var residual = mass.ToArray();
        var perMode = new List<double[]>();
        var omegas = new List<double>();
        var moved = 0.0;
        foreach (var mode in modes)
        {
            var gamma = 0.0;
            for (var n = 0; n < count; n++) gamma += mass[n] * mode.Shape[n * 6 + axis];
            var acceleration = Acceleration(sds, sd1, mode.Period) * G * importance / r;
            var forces = new double[levels];
            for (var n = 0; n < count; n++)
            {
                var share = mass[n] * mode.Shape[n * 6 + axis] * gamma;
                residual[n] -= share;
                if (levelOf[n] >= 0) forces[levelOf[n]] += share * acceleration / 1000;
            }

            moved += gamma * gamma;
            perMode.Add(Storeys(forces));
            omegas.Add(2 * Math.PI * mode.Frequency);
        }

        var rigid = new double[levels];
        var lifted = Acceleration(sds, sd1, RigidPeriod) * G * importance / r;
        for (var n = 0; n < count; n++)
            if (levelOf[n] >= 0) rigid[levelOf[n]] += residual[n] * lifted / 1000;
        var rigidShears = Storeys(rigid);

        var shears = new double[levels];
        for (var i = 0; i < levels; i++)
        {
            var combined = Combine(perMode.Select(each => each[i]).ToList(), omegas);
            shears[i] = Math.Sqrt(combined * combined + rigidShears[i] * rigidShears[i]);
        }

        var vt = levels > 0 ? shears[0] : 0;
        var scale = vt > 0 && vt < staticShear ? staticShear / vt : 1;
        var forcesAt = Enumerable.Range(0, levels).Select(i => scale * (shears[i] - (i + 1 < levels ? shears[i + 1] : 0))).ToList();
        var drift = bySixth && vt > 0 && vt < 0.85 * csW ? 0.85 * csW / vt : 1;
        return new ModalWay(way, modes.Count, total > 0 ? moved / total : 0, vt, staticShear, scale, shears.Select(shear => shear * scale).ToList(), forcesAt, drift / scale);
    }

    /// <summary>Each storey's shear from its level's force up, the lowest first.</summary>
    private static double[] Storeys(double[] forces)
    {
        var shears = new double[forces.Length];
        var sum = 0.0;
        for (var i = forces.Length - 1; i >= 0; i--)
        {
            sum += forces[i];
            shears[i] = sum;
        }

        return shears;
    }
}
