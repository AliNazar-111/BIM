using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A moment-resisting base plate's layout, mm, as SCI P398 draws one: a single row of <paramref name="Across"/> bolts outside each flange, <paramref name="Out"/> beyond the flange's face and
/// <paramref name="Edge"/> from the plate's end and its sides, on an S275 plate <paramref name="Thickness"/> thick and <paramref name="Width"/> wide; the column welded to it with fillets
/// <paramref name="Weld"/> in leg. 8.8 bolts with anchor plates, P398 Table 5.1's lengths.
/// </summary>
public sealed record BaseLayout(double Bolt, int Across, double Thickness, double Out, double Edge, double Width, double Weld = 10)
{
    public double Depth(SteelSection column) => column.H + 2 * (Out + Edge);

    /// <summary>The bolt line from the column's centre, zt.</summary>
    public double BoltLine(SteelSection column) => column.H / 2 + Out;

    public double PlateYield => Thickness <= 16 ? 275 : Thickness <= 40 ? 265 : 255;

    public double TensileArea => Bolt switch { <= 16 => 157, <= 20 => 245, <= 24 => 353, <= 27 => 459, _ => 561 };

    /// <summary>P398 Table 5.1: each bolt's embedded length to its anchor plate, mm.</summary>
    public double Embedded => Bolt <= 20 ? 300 : Bolt <= 24 ? 375 : 450;

    /// <summary>The pitch between the bolts across the plate.</summary>
    public double Pitch => Across > 1 ? (Width - 2 * Edge) / (Across - 1) : 0;
}

/// <summary>A base plate's two T-stubs, kN: in tension, the least of the plate bending without prying, the bolts and the column flange; in compression, the least of the concrete under the flange and the column's flange; c the additional bearing width, mm.</summary>
public sealed record BaseResistance(double Tension, string TensionBy, double Compression, string CompressionBy, double C, double TensionLever, double CompressionLever);

/// <summary>
/// Moment-resisting column bases: in the UK by SCI P398 Section 5 on BS EN 1993-1-8 6.2.8 - two T-stubs, the flange in tension pulling on the bolts outside it and the one in compression
/// bearing on the concrete under it, their forces from the axial force and moment by statics (P398 STEP 1, Table 6.7's model); the compression T-stub the concrete fjd beff leff with c by (6.5)
/// and the column flange Mc,Rd / (h - tf); the tension T-stub the plate bending in single curvature with no prying, 2 Mpl / mx with P398 Table 5.3's lengths, the bolts and the column flange;
/// the shear by friction and the bolts (6.2.2), the bolts' shear and tension together; the bolts' anchorage by the cone of concrete they pull out, BS EN 1992-4 as remembered. In the US by AISC
/// Design Guide 1 as remembered: small or large moment, the bearing length Y, the anchor rods' tension and the plate's thickness at its bearing and tension sides.
/// </summary>
public static class ColumnBases
{
    /// <summary>
    /// The forces on the two T-stubs, kN, P398 STEP 1: <paramref name="axial"/> tension positive, kN, <paramref name="moment"/> kNm, about the strong axis; zt to the bolts and zc to the flange's
    /// centre, mm, from the column's centre. Where one flange pulls and the other pushes, the tension on the bolts and the compression under the flange; where both push, under the more pushed;
    /// where both pull, on the more pulled bolts.
    /// </summary>
    public static (double Tension, double Compression) Forces(double axial, double moment, double zt, double zc)
    {
        var m = Math.Abs(moment) * 1000;
        var (pulled, pushed) = (axial / 2 + m / (2 * zc), axial / 2 - m / (2 * zc));
        if (pulled > 0 && pushed < 0)
            return (Math.Max(0, m / (zt + zc) + axial * zc / (zt + zc)), Math.Max(0, m / (zt + zc) - axial * zt / (zt + zc)));
        if (pulled <= 0) return (0, -pushed);
        return (axial / 2 + m / (2 * zt), 0);
    }

    /// <summary>The two T-stubs' resistances by P398 STEPS 2 and 3 and BS EN 1993-1-8 6.2.8, fjd the joint's bearing strength, N/mm².</summary>
    public static BaseResistance Eurocode(SteelSection column, double fyColumn, BaseLayout plate, double fjd)
    {
        const double gammaM2 = 1.25;
        var (tp, fyp, n) = (plate.Thickness, plate.PlateYield, plate.Across);

        // In compression: c = tp √(fy / 3 fjd), (6.5), no further than the plate reaches; the concrete over the flange and c round it, and the column's flange and web, Mc,Rd / (h - tf).
        var c = tp * Math.Sqrt(fyp / (3 * fjd));
        var beff = column.Tf + Math.Min(c, plate.Out + plate.Edge) + Math.Min(c, (column.H - 2 * column.Tf) / 2);
        var leff = column.B + 2 * Math.Min(c, Math.Max(0, (plate.Width - column.B) / 2));
        var concrete = fjd * beff * leff / 1000;
        var flange = column.Wply * fyColumn / (column.H - column.Tf) / 1000;

        // In tension: the plate bending in single curvature, no prying, 2 Mpl,1 / mx with leff the least of P398 Table 5.3's patterns for a row of n bolts; the bolts; the flange.
        var mx = plate.Out - 0.8 * plate.Weld;
        var (ex, e, p) = (plate.Edge, plate.Edge, plate.Pitch);
        var lengths = new[]
        {
            n * Math.PI * mx, n / 2.0 * (Math.PI * mx + 2 * e), 0.5 * plate.Width, n / 2.0 * (4 * mx + 1.25 * ex),
            2 * mx + 0.625 * ex + e + (n - 2) * (2 * mx + 0.625 * ex), 2 * mx + 0.625 * ex + (n - 1) * p / 2
        };
        var bending = 2 * 0.25 * lengths.Min() * tp * tp * fyp / mx / 1000;
        var bolts = n * 0.9 * 800 * plate.TensileArea / gammaM2 / 1000;
        var flangeTension = column.B * column.Tf * fyColumn / 1000;
        var (tension, tensionBy) = new[] { (bending, "the base plate in bending"), (bolts, "the bolts"), (flangeTension, "the column flange in tension") }.MinBy(each => each.Item1);
        var (compression, compressionBy) = new[] { (concrete, "the concrete under the flange"), (flange, "the column flange in compression") }.MinBy(each => each.Item1);
        return new BaseResistance(tension, tensionBy, compression, compressionBy, c, plate.BoltLine(column), (column.H - column.Tf) / 2);
    }

    /// <summary>The layouts tried, cheapest first: M20, M24 then M30, two bolts a side then four, the plate 15 to 50 thick; as P398 has it, M20 the least.</summary>
    public static IEnumerable<BaseLayout> Layouts(SteelSection column)
    {
        foreach (var (bolt, across) in new[] { (20.0, 2), (24.0, 2), (20.0, 4), (24.0, 4), (30.0, 2), (30.0, 4) })
        {
            var (out_, edge) = (2.5 * bolt + 10, 2.5 * bolt);
            var width = column.B + 2 * edge;
            if (across == 4 && (width - 2 * edge) / 3 < 3 * bolt) continue;
            foreach (var thick in new[] { 15.0, 20, 25, 30, 40, 50 })
                yield return new BaseLayout(bolt, across, thick, out_, edge, width, MomentJoints.Leg(column.Tf) > 12 ? 12 : MomentJoints.Leg(column.Tf));
        }
    }

    /// <summary>
    /// The cone of concrete a row of bolts in tension pulls out, kN, BS EN 1992-4 7.2.1.4 as remembered: k1 8.9 √fck hef^1.5 for cracked concrete, its projected area - the row's length and
    /// 1.5 hef past each end and to each side, no further than the pad's edges - over (3 hef)², ψs = 0.7 + 0.3 c / 1.5 hef for the nearest edge, γMc 1.5.
    /// </summary>
    public static double Cone(double hef, double length, double across, double outward, double fck)
    {
        var reach = 1.5 * hef;
        var (side, beyond) = (Math.Min(Math.Max(across, 0), reach), Math.Min(Math.Max(outward, 0), reach));
        var area = (length + 2 * side) * (beyond + reach);
        var edge = Math.Min(1, 0.7 + 0.3 * Math.Min(side, beyond) / reach);
        return 8.9 * Math.Sqrt(fck) * Math.Pow(hef, 1.5) * area / (9 * hef * hef) * edge / 1.5 / 1000;
    }

    /// <summary>
    /// A fixed steel column's base in the UK: the cheapest layout whose two T-stubs carry every ultimate combination's axial force and strong axis moment at its foot, its shear by friction 0.2
    /// of what bears and the bolts, the bolts' shear and tension together, and the cone their tension pulls out of the pad.
    /// </summary>
    internal static JointCheck Uk(string name, SteelSection column, IReadOnlyList<(string Combination, MemberForces Forces)> forces, double fck, double padWidth, double padThickness)
    {
        var fyColumn = SteelDesign.YieldStrength(column.Tf);
        var fjd = 0.85 * fck / 1.5;
        const double gammaM2 = 1.25;

        (double Ratio, List<StrengthCheck> Checks) Check(BaseLayout plate)
        {
            var resisted = Eurocode(column, fyColumn, plate, fjd);
            var shearEach = (0.44 - 0.0003 * 640) * 800 * plate.TensileArea / gammaM2 / 1000;
            var tensionEach = 0.9 * 800 * plate.TensileArea / gammaM2 / 1000;
            var hef = Math.Min(plate.Embedded, Math.Max(150, padThickness - 100));
            var cone = Cone(hef, (plate.Across - 1) * plate.Pitch, (padWidth - (plate.Across - 1) * plate.Pitch) / 2, padWidth / 2 - resisted.TensionLever, fck);
            var worst = new Dictionary<string, StrengthCheck>();
            void Keep(StrengthCheck check)
            {
                if (!worst.TryGetValue(check.Name, out var was) || check.Ratio > was.Ratio) worst[check.Name] = check;
            }

            foreach (var (combination, f) in forces)
            {
                var (tension, compression) = Forces(f.N, f.My, resisted.TensionLever, resisted.CompressionLever);
                var bears = Math.Max(0, -f.N) + tension;
                var shear = Math.Sqrt(f.Vy * f.Vy + f.Vz * f.Vz);
                Keep(new StrengthCheck("Tension T-stub", tension, resisted.Tension, "kN", "BS EN 1993-1-8 6.2.8, P398 STEP 3", $"{resisted.TensionBy}, the bolts {resisted.TensionLever:0} from the centre", combination));
                Keep(new StrengthCheck("Compression T-stub", compression, resisted.Compression, "kN", "6.2.8, P398 STEP 2", $"{resisted.CompressionBy}, c {resisted.C:0} mm, fjd {fjd:0.0} N/mm²", combination));
                Keep(new StrengthCheck("Shear at the base", shear, 0.2 * bears + 2 * plate.Across * shearEach, "kN", "6.2.2(6) to (8)", $"friction 0.2 x {bears:0} kN, {shearEach:0.0} kN a bolt", combination));
                if (tension > 0.01)
                {
                    Keep(new StrengthCheck("Bolts in shear and tension", shear / (2 * plate.Across) / shearEach + tension / plate.Across / (1.4 * tensionEach), 1, "", "Table 3.4", "the shear shared by every bolt", combination));
                    Keep(new StrengthCheck("Concrete cone", tension, cone, "kN", "BS EN 1992-4 7.2.1.4", $"hef {hef:0} mm to the anchor plates, cracked, k1 8.9, γMc 1.5", combination));
                }
            }

            var list = worst.Values.ToList();
            return (list.Count == 0 ? 0 : list.Max(check => check.Ratio), list);
        }

        var layouts = Layouts(column).ToList();
        var chosen = layouts.FirstOrDefault(plate => Check(plate).Ratio <= 1) ?? layouts.MinBy(plate => Check(plate).Ratio)!;
        var (ratio, checks) = Check(chosen);
        var weak = forces.Count == 0 ? 0 : forces.Max(f => Math.Abs(f.Forces.Mz));
        return new JointCheck(name, "Moment base plate", checks,
            $"a base plate {chosen.Depth(column):0} by {chosen.Width:0} by {chosen.Thickness:0} S275, {2 * chosen.Across} M{chosen.Bolt:0} 8.8 holding-down bolts, {chosen.Across} outside each flange {chosen.Out:0} from it, " +
            $"{chosen.Embedded:0} long with anchor plates (P398 Table 5.1), {chosen.Weld:0} mm fillets all round the column{(ratio > 1 ? "; and more than these layouts give - a stiffened base or a larger one" : string.Empty)}",
            $"Fixed at its foot: by SCI P398 Section 5 on BS EN 1993-1-8 6.2.8, the strong axis moment with the axial force, the shear by friction and the bolts. {(weak > 1 ? $"Its weak axis moment, up to {weak:0.0} kNm, is not worked out. " : string.Empty)}" +
            "The welds, the anchor plates' bearing and the pad's own reinforcement are not checked; a concrete base is rigid only as its ground lets it be.");
    }

    /// <summary>
    /// A base plate by AISC Design Guide 1 (2nd edition) as remembered, LRFD, kN and mm: N deep, B wide, its rods f from the column's centre; Pu compression positive, Mu. fp,max = φ 0.85 f'c,
    /// φ 0.65, qmax = fp,max B; e = Mu / Pu against ecrit = N/2 - Pu / 2 qmax: a small moment bears over Y = N - 2e with no tension; a large one over Y = (f + N/2) - √((f + N/2)² - 2 Pu (e + f) / qmax)
    /// with the rods pulling Tu = qmax Y - Pu. The plate at least 1.5 m √(fp / Fy) where Y reaches m, 2.11 √(fp Y (m - Y/2) / Fy) where it does not, and 2.11 √(Tu x / B Fy) at the rods.
    /// </summary>
    public static (double Bearing, double Tension, double Thickness) Aisc(SteelSection column, double depth, double width, double rods, double pu, double mu, double fc, double fy = 248)
    {
        var fpMax = 0.65 * 0.85 * fc;
        var qMax = fpMax * width;
        var m = Math.Abs(mu) * 1e6;
        var lever = (depth - 0.95 * column.H) / 2;
        var cantilever = Math.Max(lever, (width - 0.8 * column.B) / 2);
        double y, tension = 0, fp;
        var critical = depth / 2 - pu * 1000 / (2 * qMax);
        if (pu > 0 && m / (pu * 1000) <= critical)
        {
            y = depth - 2 * m / (pu * 1000);
            fp = pu * 1000 / (width * y);
            var bearing = y >= lever ? 1.5 * cantilever * Math.Sqrt(fp / fy) : 2.11 * Math.Sqrt(fp * y * (lever - y / 2) / fy);
            return (fp / fpMax, 0, bearing);
        }

        var reach = rods + depth / 2;
        var root = reach * reach - 2 * (m + pu * 1000 * rods) / qMax;
        if (root < 0) return (2 * (m + pu * 1000 * rods) / qMax / (reach * reach), double.PositiveInfinity, double.PositiveInfinity);
        y = reach - Math.Sqrt(root);
        tension = Math.Max(0, qMax * y - pu * 1000) / 1000;
        fp = fpMax;
        var atBearing = y >= lever ? 1.5 * cantilever * Math.Sqrt(fp / fy) : 2.11 * Math.Sqrt(fp * y * (lever - y / 2) / fy);
        var x = rods - column.H / 2 + column.Tf / 2;
        var atRods = 2.11 * Math.Sqrt(tension * 1000 * x / (width * fy));
        return (2 * (m + pu * 1000 * rods) / qMax / (reach * reach), tension, Math.Max(atBearing, atRods));
    }

    /// <summary>
    /// A fixed steel column's base in the US by Design Guide 1: the plate 3 1/2 in past each flange and 2 in past its sides, two F1554 grade 36 rods each side 2 in outside the flange, the first
    /// rod size whose two can take the tension at φ 0.75 x 0.75 Fu Ab and the plate as thick as it has to be, to the eighth of an inch; the rods' shear at the four by ACI 318-19 17.7.1 with 0.8
    /// for a grout pad, and concrete breakout of the two in tension, 17.6.2, as round 49 has them.
    /// </summary>
    internal static JointCheck Us(string name, SteelSection column, IReadOnlyList<(string Combination, MemberForces Forces)> forces, double fc, double padThickness)
    {
        var (depth, width) = (column.H + 2 * 88.9, column.B + 2 * 50.8);
        var rods = column.H / 2 + 50.8;
        var sizes = new[] { 0.75, 0.875, 1.0, 1.25, 1.5 };
        var worstTension = 0.0;
        var thickness = 0.0;
        var bearing = 0.0;
        var tensionAt = string.Empty;
        var bearingAt = string.Empty;
        foreach (var (combination, f) in forces)
        {
            var (bears, tension, thick) = Aisc(column, depth, width, rods, -f.N, f.My, fc);
            if (tension > worstTension) (worstTension, tensionAt) = (tension, combination);
            if (bears > bearing) (bearing, bearingAt) = (bears, combination);
            thickness = Math.Max(thickness, thick);
        }

        var rod = sizes.FirstOrDefault(size => 2 * 0.75 * 0.75 * 400 * Math.PI * Math.Pow(size * 25.4, 2) / 4 / 1000 >= worstTension, sizes[^1]);
        var area = Math.PI * Math.Pow(rod * 25.4, 2) / 4 * 0.75;
        var plate = double.IsInfinity(thickness) ? 50.8 : Math.Max(12.7, Math.Ceiling(thickness / 3.175) * 3.175);
        var shear = forces.Count == 0 ? 0 : forces.Max(f => Math.Sqrt(f.Forces.Vy * f.Forces.Vy + f.Forces.Vz * f.Forces.Vz));
        var hef = Math.Min(305, Math.Max(100, padThickness - 75));
        var reach = 1.5 * hef;
        var breakout = 0.70 * 10 * Math.Sqrt(fc) * Math.Pow(hef, 1.5) / 1000 * Math.Min((width - 2 * 38.1 + 2 * reach) * 2 * reach / (9 * hef * hef), 2);
        var checks = new List<StrengthCheck>
        {
            new("Concrete bearing", bearing, 1, "", "Design Guide 1, J8", "against φ 0.65 x 0.85 f'c", bearingAt),
            new("Anchor rods in tension", worstTension, 2 * 0.75 * 0.75 * 400 * Math.PI * Math.Pow(rod * 25.4, 2) / 4 / 1000, "kN", "J3.6, Design Guide 1", $"two {rod:0.###} in F1554 grade 36, φ 0.75 x 0.75 Fu Ab", tensionAt),
            new("Anchor rods in shear", shear, 0.65 * 4 * 0.6 * area * 400 * 0.8 / 1000, "kN", "ACI 318-19 17.7.1", "the four, 0.8 for a grout pad")
        };
        if (worstTension > 0.01) checks.Add(new StrengthCheck("Concrete breakout", worstTension, breakout, "kN", "ACI 318-19 17.6.2", $"the two pulling, hef {hef:0} mm, cracked, φ 0.70", tensionAt));
        var weak = forces.Count == 0 ? 0 : forces.Max(f => Math.Abs(f.Forces.Mz));
        return new JointCheck(name, "Moment base plate", checks,
            $"a base plate {depth / 25.4:0.#} by {width / 25.4:0.#} by {plate / 25.4:0.###} in A36{(double.IsInfinity(thickness) ? string.Empty : $" (Design Guide 1 asks {thickness / 25.4:0.###} in at its bearing and its rods)")}, four {rod:0.###} in F1554 grade 36 anchor rods, two outside each flange",
            $"Fixed at its foot: by AISC Design Guide 1, the strong axis moment with the axial force. {(weak > 1 ? $"Its weak axis moment, up to {weak:0.0} kNm, is not worked out. " : string.Empty)}The welds and the pad's reinforcement are not checked; √(A2/A1) taken as 1.");
    }
}
