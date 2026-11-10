using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// An extended end plate's layout, mm, as SCI P398 draws one: a row of bolts <paramref name="AboveFlange"/> over the beam's tension flange with <paramref name="Extension"/> of plate past it
/// (no row there where it is 0), the first row under the flange <paramref name="FirstBelow"/> from the flange's outer face and the rest <paramref name="Pitch"/> apart, the bolts at
/// <paramref name="Gauge"/> across a plate <paramref name="Width"/> wide; extended both ways, <paramref name="BothWays"/>, for a moment that reverses. The fillets' leg lengths: the tension flange's
/// and the web's full strength, the compression flange's nominal. S275 plate, M20, M24 or M30 8.8 bolts.
/// </summary>
public sealed record EndPlateLayout(double Bolt, double Thickness, double Width, double Gauge, int RowsBelow, bool BothWays = false,
    double AboveFlange = 40, double Extension = 50, double FirstBelow = 60, double Pitch = 90, double TensionWeld = 12, double CompressionWeld = 8, double WebWeld = 8, double Haunch = 0)
{
    /// <summary>The bolt's tensile stress area, mm².</summary>
    public double TensileArea => Bolt switch { <= 16 => 157, <= 20 => 245, <= 24 => 353, <= 27 => 459, _ => 561 };

    /// <summary>Its hole, mm: 2 mm clearance to M24 and 3 past it.</summary>
    public double Hole => Bolt <= 24 ? Bolt + 2 : Bolt + 3;

    /// <summary>The least width across the points of its head, mm, EN ISO 4014: dw, whose quarter ew is in Method 2 of Table 6.2.</summary>
    public double Head => Bolt switch { <= 16 => 26.75, <= 20 => 32.95, <= 24 => 39.55, <= 27 => 45.2, _ => 50.85 };

    /// <summary>What a bolt stretches over, mm, (6.3.2): the grip, two washers and half the head and nut.</summary>
    public double Stretch(double flange) => Thickness + flange + (Bolt <= 20 ? 6 : 8) + (Bolt <= 20 ? 12.5 + 18 : Bolt <= 24 ? 15 + 21.5 : 18.7 + 25.6) / 2;

    /// <summary>S275's yield strength for its thickness, N/mm², BS EN 10025-2 Table 7.</summary>
    public double PlateYield => Thickness <= 16 ? 275 : Thickness <= 40 ? 265 : 255;

    public const double PlateUltimate = 410;

    /// <summary>Its depth, mm: deep enough below the compression flange for the force to spread through it at 45°, as P398 asks for sp = 2 tp.</summary>
    public double Depth(double beamDepth) => BothWays ? beamDepth + 2 * (AboveFlange + Extension) : Extension + AboveFlange + beamDepth + Haunch + CompressionWeld + Thickness;
}

/// <summary>A row of bolts in tension: its lever arm from the centre of compression, mm, the force it can take, kN, and what limits it.</summary>
public sealed record BoltRow(double Lever, double Force, string By);

/// <summary>
/// What an end plate joint resists by BS EN 1993-1-8 6.2.7.2: each row's tension, the compression zone, Mj,Rd kNm, the shear its bolts carry kN, the column web panel's shear kN, and its
/// initial rotational stiffness Sj,ini kNm/rad by 6.3.
/// </summary>
public sealed record EndPlateMoment(EndPlateLayout Layout, IReadOnlyList<BoltRow> Rows, double Compression, string CompressionBy, double Moment, double Shear, double PanelShear, double Stiffness,
    ColumnStiffening? Stiffening = null, double StiffenerThickness = 0, double WebPlateThickness = 0, double TensionStiffenerForce = 0);

/// <summary>
/// What strengthens a column at a moment joint, by SCI P398 STEP 6: a pair of full depth stiffeners in line with the beam's compression flange (6B) and with its tension flange (6A); supplementary
/// web plates on one side or both (6C); backing plates <paramref name="Backing"/> thick behind its flange (6E).
/// </summary>
public sealed record ColumnStiffening(bool Compression = false, bool Tension = false, int WebPlates = 0, double Backing = 0)
{
    public static readonly ColumnStiffening None = new();

    public bool Any => Compression || Tension || WebPlates > 0 || Backing > 0;
}

/// <summary>
/// The four-bolt extended end plate, 4E, by AISC Design Guide 4 as remembered: φMnp the bolts' rupture without prying, kNm; φMpl the plate's yielding by its yield lines Yp, kNm; the
/// bolts at the compression flange in shear, kN; and the column side unstiffened - its flange bending by Yc, kNm, its web's local yielding and crippling under the flange force, kN, and its
/// panel zone in shear, kN.
/// </summary>
public sealed record FourBoltMoment(double Bolt, double Thickness, double Width, double Gauge, double Pitch, double BoltRupture, double PlateYield, double Shear, double ColumnFlange,
    double WebYielding, double WebCrippling, double PanelZone);

/// <summary>
/// Moment joints: where a steel beam is modelled fixed into the flange of a steel column that runs on past it. In the UK an extended end plate by SCI P398's steps on BS EN 1993-1-8 6.2.7:
/// each row of bolts as an equivalent T-stub on the column flange and on the end plate, alone and in groups, Method 2 of Table 6.2, effective lengths by Tables 6.4 and 6.6 with α from P398's
/// Appendix G; the column and beam webs in tension; the plastic distribution limit of the UK National Annex; the compression zone - column web by 6.2.6.2 with ω and ρ, beam flange by 6.2.6.7 -
/// and the column web panel's shear by 6.2.6.1 over β; the rows' forces cut from the bottom up to meet the compression; Mj,Rd by (6.25); the shear by the bottom row and 28% of the rows in
/// tension; and Sj,ini by 6.3.1, classified against 8 and 25 E Ib / Lb by 5.2.2.5. In the US a four-bolt extended end plate by AISC Design Guide 4.
/// </summary>
public static class MomentJoints
{
    private const double E = 210000, GammaM2 = 1.25;

    /// <summary>
    /// α of BS EN 1993-1-8 Figure 6.11 for a row next to a flange or stiffener, by the expressions of P398 Appendix G: on a curve, λ1 is λ1,lim = 1.25 / (α - 2.75) down to λ2,lim = α λ1,lim / 2
    /// and below that λ1,lim + (1 - λ1,lim)(1 - λ2 / λ2,lim)^(0.185 α^1.785); solved for α between 4.45 and 8.
    /// </summary>
    public static double Alpha(double lambda1, double lambda2)
    {
        static double Lambda1(double alpha, double l2)
        {
            var lim1 = 1.25 / (alpha - 2.75);
            var lim2 = alpha * lim1 / 2;
            return l2 >= lim2 ? lim1 : lim1 + (1 - lim1) * Math.Pow(1 - l2 / lim2, 0.185 * Math.Pow(alpha, 1.785));
        }

        if (lambda1 >= Lambda1(4.45, lambda2)) return 4.45;
        if (lambda1 <= Lambda1(8, lambda2)) return 8;
        double low = 4.45, high = 8;
        for (var i = 0; i < 60; i++)
        {
            var mid = (low + high) / 2;
            if (Lambda1(mid, lambda2) > lambda1) low = mid;
            else high = mid;
        }

        return (low + high) / 2;
    }

    /// <summary>The column's shear area by 6.2.6(3) of BS EN 1993-1-1, mm²: A - 2 b tf + (tw + 2r) tf, at least hw tw.</summary>
    public static double ShearArea(SteelSection column) =>
        Math.Max(column.Area - 2 * column.B * column.Tf + (column.Tw + 2 * column.Root) * column.Tf, (column.H - 2 * column.Tf) * column.Tw);

    /// <summary>ω of Table 6.3 for an effective width of the column web, the shear in its panel taken in through β.</summary>
    public static double Omega(SteelSection column, double width, double beta) => Omega(width, column.Tw, ShearArea(column), beta);

    private static double Omega(double width, double web, double shearArea, double beta)
    {
        var r = width * web / shearArea;
        var (w1, w2) = (1 / Math.Sqrt(1 + 1.3 * r * r), 1 / Math.Sqrt(1 + 5.2 * r * r));
        return beta <= 0.5 ? 1 : beta < 1 ? w1 + 2 * (1 - beta) * (1 - w1) : beta <= 1 ? w1 : beta < 2 ? w1 + (beta - 1) * (w2 - w1) : w2;
    }

    /// <summary>
    /// An end plate joint's resistance by BS EN 1993-1-8 6.2.7.2 as SCI P398 steps through it, for a hogging moment: the beam and column in their own grades, the plate S275.
    /// <paramref name="beta"/> is the transformation parameter of 5.3 for the column web panel, 0 where the moments either side balance and 1 on one side alone; <paramref name="kwc"/>
    /// the reduction for the column's own longitudinal stress; <paramref name="axial"/> the beam's compression, kN.
    /// </summary>
    public static EndPlateMoment Eurocode(SteelSection beam, double fyBeam, SteelSection column, double fyColumn, EndPlateLayout plate, double beta = 1, double kwc = 1, double axial = 0,
        ColumnStiffening? stiffening = null)
    {
        var (d, tp, fyp, w, bp) = (plate.Bolt, plate.Thickness, plate.PlateYield, plate.Gauge, plate.Width);
        var ew = plate.Head / 4;
        var ft = 0.9 * 800 * plate.TensileArea / GammaM2;
        var (ep, ec) = ((bp - w) / 2, (column.B - w) / 2);
        var emin = Math.Min(ep, ec);
        var mp = (w - beam.Tw - 2 * 0.8 * plate.WebWeld) / 2;
        var mc = (w - column.Tw - 2 * 0.8 * column.Root) / 2;
        var mx = plate.AboveFlange - 0.8 * plate.TensionWeld;
        var extended = plate.AboveFlange > 0 && plate.Extension > 0;
        var ys = new List<double>();
        if (extended) ys.Add(-plate.AboveFlange);
        for (var k = 0; k < plate.RowsBelow; k++) ys.Add(plate.FirstBelow + k * plate.Pitch);
        var n = ys.Count;
        var first = extended ? 1 : 0;
        var levers = ys.Select(y => beam.H + plate.Haunch - beam.Tf / 2 - y).ToArray();
        var m2 = plate.FirstBelow - beam.Tf - 0.8 * plate.TensionWeld;
        var alpha = Alpha(mp / (mp + ep), m2 / (mp + ep));
        var avc = ShearArea(column);
        var dwc = column.H - 2 * (column.Tf + column.Root);

        // What strengthens the column. Its stiffeners as wide as its flange's outstand and thick enough for a Class 3 outstand, no thinner than the beam's flange; a web plate as deep as the web
        // between its root radii, at least the web's thickness and no more than 40 ε of it wide; with one plate the web 1.5 times as thick, with two twice, and its shear area that of one more web.
        var stiff = stiffening ?? ColumnStiffening.None;
        var eps = Math.Sqrt(235 / fyColumn);
        var outstand = (column.B - column.Tw) / 2;
        var ts = Math.Max(10, 5 * Math.Ceiling(Math.Max(beam.Tf, outstand / (14 * eps)) / 5));
        var net = outstand - Math.Max(15, column.Root);
        var webPlate = stiff.WebPlates > 0 ? Math.Max(column.Tw, Math.Ceiling(dwc / (40 * eps))) : 0;
        if (stiff.WebPlates > 0) avc += dwc * column.Tw;
        var webThick = column.Tw * (1 + 0.5 * Math.Min(stiff.WebPlates, 2));

        // A tension stiffener in line with the beam's tension flange: the rows either side of it are next to a stiffener on the column flange, α m, and no group crosses it.
        var level = beam.Tf / 2;
        double M2Column(int r) => Math.Max(1, (ys[r] < level ? level - ts / 2 - ys[r] : ys[r] - level - ts / 2) - 0.8 * 8);
        bool Adjacent(int r) => stiff.Tension && (r == first || (extended && r == first - 1));
        double AlphaColumn(int r) => Alpha(mc / (mc + ec), M2Column(r) / (mc + ec));

        double Between(int i, int j) => Math.Abs(ys[j] - ys[i]);

        // A row's share of a group's effective length, (circular, non-circular), on each side: Tables 2.2 and 2.3 of P398, 6.4 and 6.6 of BS EN 1993-1-8.
        (double Cp, double Nc) OnColumn(int r, int a, int b)
        {
            if (Adjacent(r))
            {
                if (a == b) return (2 * Math.PI * mc, AlphaColumn(r) * mc);
                var p = r == a ? Between(a, a + 1) : Between(b - 1, b);
                return (Math.PI * mc + p, 0.5 * p + AlphaColumn(r) * mc - (2 * mc + 0.625 * ec));
            }

            if (a == b) return (2 * Math.PI * mc, 4 * mc + 1.25 * ec);
            if (r == a) return (Math.PI * mc + Between(a, a + 1), 2 * mc + 0.625 * ec + 0.5 * Between(a, a + 1));
            if (r == b) return (Math.PI * mc + Between(b - 1, b), 2 * mc + 0.625 * ec + 0.5 * Between(b - 1, b));
            return (Between(r - 1, r) + Between(r, r + 1), 0.5 * (Between(r - 1, r) + Between(r, r + 1)));
        }

        (double Cp, double Nc) OnPlate(int r, int a, int b)
        {
            if (extended && r == 0)
                return (Math.Min(2 * Math.PI * mx, Math.Min(Math.PI * mx + w, Math.PI * mx + 2 * ep)),
                    Math.Min(Math.Min(4 * mx + 1.25 * plate.Extension, ep + 2 * mx + 0.625 * plate.Extension), Math.Min(0.5 * bp, 0.5 * w + 2 * mx + 0.625 * plate.Extension)));
            if (a == b) return r == first ? (2 * Math.PI * mp, alpha * mp) : (2 * Math.PI * mp, 4 * mp + 1.25 * ep);
            if (r == a) return r == first
                ? (Math.PI * mp + Between(a, a + 1), 0.5 * Between(a, a + 1) + alpha * mp - (2 * mp + 0.625 * ep))
                : (Math.PI * mp + Between(a, a + 1), 2 * mp + 0.625 * ep + 0.5 * Between(a, a + 1));
            if (r == b) return (Math.PI * mp + Between(b - 1, b), 2 * mp + 0.625 * ep + 0.5 * Between(b - 1, b));
            return (Between(r - 1, r) + Between(r, r + 1), 0.5 * (Between(r - 1, r) + Between(r, r + 1)));
        }

        (double Cp, double Nc) Sum(Func<int, int, int, (double Cp, double Nc)> share, int a, int b) =>
            Enumerable.Range(a, b - a + 1).Select(r => share(r, a, b)).Aggregate((0.0, 0.0), (s, x) => (s.Item1 + x.Cp, s.Item2 + x.Nc));

        // The T-stub's resistance, N, the least of its three modes, Method 2 for Mode 1.
        (double F, string Mode) TStub((double Cp, double Nc) length, double m, double e, double t, double fy, int rows, double backing = 0)
        {
            var nn = Math.Min(e, 1.25 * m);
            var (l1, l2) = (Math.Min(length.Cp, length.Nc), length.Nc);
            var (mpl1, mpl2) = (0.25 * l1 * t * t * fy, 0.25 * l2 * t * t * fy);
            var mbp = 0.25 * l1 * backing * backing * (backing <= 16 ? 275 : 265);
            var bolts = 2 * rows * ft;
            var modes = new[]
            {
                (((8 * nn - 2 * ew) * mpl1 + 4 * nn * mbp) / (2 * m * nn - ew * (m + nn)), "mode 1"),
                ((2 * mpl2 + nn * bolts) / (m + nn), "mode 2"),
                (bolts, "the bolts")
            };
            return modes.MinBy(mode => mode.Item1);
        }

        double PlateM(int r) => extended && r == 0 ? mx : mp;
        double PlateE(int r) => extended && r == 0 ? plate.Extension : emin;

        var forces = new double[n];
        var by = new string[n];
        for (var r = 0; r < n; r++)
        {
            var options = new List<(double F, string By)>();
            void Add(double force, string what, int a)
            {
                var before = Enumerable.Range(a, r - a).Sum(i => forces[i]);
                options.Add((force - before, a == r ? what : $"{what}, rows {a + 1} to {r + 1}"));
            }

            for (var a = r; a >= 0; a--)
            {
                if (!(stiff.Tension && a < first && r >= first))
                {
                    var onColumn = Sum(OnColumn, a, r);
                    var flange = TStub(onColumn, mc, emin, column.Tf, fyColumn, r - a + 1, stiff.Backing);
                    Add(flange.F, $"column flange ({flange.Mode})", a);
                    var web = Math.Min(onColumn.Cp, onColumn.Nc);
                    if (!Enumerable.Range(a, r - a + 1).Any(Adjacent)) Add(Omega(web, webThick, avc, beta) * web * webThick * fyColumn, "column web", a);
                }

                if (a == r || a >= first)
                {
                    var onPlate = a == r ? OnPlate(r, r, r) : Sum(OnPlate, a, r);
                    var end = TStub(onPlate, PlateM(a == r ? r : a), PlateE(a == r ? r : a), tp, fyp, r - a + 1);
                    Add(end.F, $"end plate ({end.Mode})", a);
                    if (a >= first) Add(Math.Min(onPlate.Cp, onPlate.Nc) * beam.Tw * fyBeam, "beam web", a);
                }
            }

            var least = options.MinBy(option => option.F);
            (forces[r], by[r]) = (Math.Max(0, least.F), least.By);

            // The UK National Annex's plastic distribution limit: past a row over 1.9 Ft,Rd, the rows below it no more than in proportion to their lever arms.
            var ductile = tp <= d * 800 / (1.9 * fyp) || column.Tf <= d * 800 / (1.9 * fyColumn);
            var x = Enumerable.Range(0, r).FirstOrDefault(i => forces[i] > 1.9 * ft, -1);
            if (!ductile && x >= 0 && forces[x] * levers[r] / levers[x] < forces[r])
                (forces[r], by[r]) = (forces[x] * levers[r] / levers[x], $"the triangular limit from row {x + 1}");
        }

        // The compression zone: the column web, crushing or buckling, ω kwc ρ beff,c,wc twc fy; the beam's flange, Mc,Rd / (h - tf); and the web panel's shear over β.
        var spread = beam.Tf + 2 * (plate.BothWays ? plate.TensionWeld : plate.CompressionWeld) + 5 * (column.Tf + column.Root) + 2 * tp;
        var slenderness = 0.932 * Math.Sqrt(spread * dwc * fyColumn / (E * webThick * webThick));
        var rho = slenderness <= 0.72 ? 1 : (slenderness - 0.2) / (slenderness * slenderness);
        var panel = 0.9 * fyColumn * avc / Math.Sqrt(3);
        var webCompression = (F: Omega(spread, webThick, avc, beta) * kwc * rho * spread * webThick * fyColumn, By: "the column web in compression");
        if (stiff.Compression)
        {
            // STEP 6B: the stiffeners in contact with the flange and the web the force spreads into, or the cruciform of the stiffeners and 15 ε tw of web each side buckling over the web's
            // depth, curve c.
            var fy = Math.Min(fyColumn, SteelDesign.YieldStrength(ts));
            var section = (2 * net * ts + spread * webThick) * fy;
            var cruciform = 30 * eps * column.Tw * column.Tw + ts * column.Tw + 2 * outstand * ts;
            var radius = Math.Sqrt(Math.Pow(2 * outstand + column.Tw, 3) * ts / 12 / cruciform);
            var buckling = SteelDesign.Chi(0.49, (column.H - 2 * column.Tf) / radius / (93.9 * eps)) * cruciform * fy;
            webCompression = (Math.Min(section, buckling), section <= buckling ? "the column's compression stiffeners" : "the column's compression stiffeners buckling");
        }

        var limits = new List<(double F, string By)>
        {
            webCompression,
            plate.Haunch > 0 ? (1.4 * beam.B * beam.Tf * fyBeam, "the haunch flange in compression, 1.4 Afb fy") : (beam.Wply * fyBeam / (beam.H - beam.Tf), "the beam flange in compression")
        };
        if (beta > 0) limits.Add((panel / beta, "the column web panel in shear"));
        var (compression, compressionBy) = limits.MinBy(limit => limit.F);
        var room = compression - axial * 1000;
        for (var r = n - 1; r >= 0 && forces.Sum() > room; r--)
        {
            var cut = Math.Max(0, forces[r] - (forces.Sum() - room));
            if (cut < forces[r]) (forces[r], by[r]) = (cut, $"{compressionBy}, cut from {forces[r] / 1000:0} kN");
        }

        // The shear: the rows not in tension in full, those in tension at 28% (Table 3.4's interaction with them fully in tension), each bolt the least of its shear and its bearing.
        var shearOne = Math.Min(0.6 * 800 * plate.TensileArea / GammaM2 / 1000,
            Math.Min(JointDesign.BoltBearingEurocode(d, plate.Hole, tp, EndPlateLayout.PlateUltimate, plate.Pitch - 0.75 * plate.Hole, ep),
                JointDesign.BoltBearingEurocode(d, plate.Hole, column.Tf, 470, plate.Pitch - 0.75 * plate.Hole, ec)));
        var bearingRows = plate.BothWays ? n : 1;
        var shear = (2 * bearingRows + 0.28 * 2 * n) * shearOne;

        // Sj,ini by 6.3: each row's springs - column web in tension, column flange and end plate in bending, the bolts - in series, the rows together at zeq, then the web panel and web in
        // compression. Each row's effective length the least it has, alone or in any group.
        double Least(bool onPlate, int r)
        {
            var least = double.MaxValue;
            var lowest = onPlate ? (r < first ? r : first) : 0;
            var highest = onPlate && r < first ? r : n - 1;
            for (var a = lowest; a <= r; a++)
                for (var b = r; b <= highest; b++)
                {
                    var (cp, nc) = onPlate ? OnPlate(r, a, b) : OnColumn(r, a, b);
                    least = Math.Min(least, Math.Min(cp, nc));
                }

            return least;
        }

        // A stiffened web is as stiff as need be where it is stiffened: no web spring in tension at a row next to a tension stiffener, none in compression with compression stiffeners.
        var keff = Enumerable.Range(0, n).Select(r =>
        {
            var onColumn = Least(false, r);
            var onPlate = Least(true, r);
            var springs = new List<double>
            {
                0.9 * onColumn * Math.Pow(column.Tf, 3) / Math.Pow(mc, 3),
                0.9 * onPlate * Math.Pow(tp, 3) / Math.Pow(PlateM(r), 3),
                1.6 * plate.TensileArea / plate.Stretch(column.Tf)
            };
            if (!Adjacent(r)) springs.Add(0.7 * onColumn * webThick / dwc);
            return 1 / springs.Sum(k => 1 / k);
        }).ToArray();
        var zeq = Enumerable.Range(0, n).Sum(r => keff[r] * levers[r] * levers[r]) / Enumerable.Range(0, n).Sum(r => keff[r] * levers[r]);
        var keq = Enumerable.Range(0, n).Sum(r => keff[r] * levers[r]) / zeq;
        var k1 = beta > 0 ? 0.38 * avc / (beta * zeq) : double.PositiveInfinity;
        var k2 = stiff.Compression ? double.PositiveInfinity : 0.7 * spread * webThick / dwc;
        var stiffness = E * zeq * zeq / (1 / k1 + 1 / k2 + 1 / keq) / 1e6;

        // STEP 6A: each tension stiffener's force from its share of holding the flange, m1/2 [Fri / (m1 + m2) + Frj / (m1 + m2)], and its thickness enough for it on its net width.
        var stiffenerForce = 0.0;
        if (stiff.Tension)
        {
            var above = extended ? forces[first - 1] / (mc + M2Column(first - 1)) : 0;
            stiffenerForce = mc / 2 * (above + forces[first] / (mc + M2Column(first)));
            ts = Math.Max(ts, 5 * Math.Ceiling(stiffenerForce / (net * Math.Min(fyColumn, SteelDesign.YieldStrength(ts))) / 5));
        }

        var rows = Enumerable.Range(0, n).Select(r => new BoltRow(levers[r], forces[r] / 1000, by[r])).ToList();
        return new EndPlateMoment(plate, rows, compression / 1000, compressionBy, rows.Sum(row => row.Lever * row.Force) / 1000, shear, panel / 1000, stiffness,
            stiff, stiff.Compression || stiff.Tension ? ts : 0, webPlate, stiffenerForce / 1000);
    }

    /// <summary>
    /// The layouts tried for a beam on a column, in order of what they cost: M20 at 90 gauge on a 200 plate 15 then 20 thick, M24 at 100 on 250, 20 then 25, M30 at 140 on 300, 25 then 30, each
    /// with as few rows under the flange as carry it; where the column's flange leaves too little edge for a gauge, not that one.
    /// </summary>
    public static IEnumerable<EndPlateLayout> Layouts(SteelSection beam, SteelSection column, bool bothWays, bool knee = false)
    {
        if (knee)
        {
            // A knee: the column stops at the beam's top, so the plate is flush with it; no haunch, then one half the beam's depth, then one as deep as the beam, cut from it.
            foreach (var haunch in new[] { 0, Math.Round(beam.H / 2 / 10) * 10, Math.Round(beam.H / 10) * 10 })
                foreach (var layout in Layouts(beam, column, false))
                {
                    var most = Math.Max(1, (int)Math.Floor((beam.H + haunch - beam.Tf - 50 - 90 - 60) / 90) + 1);
                    if (layout.RowsBelow <= most && layout.RowsBelow == 1)
                        for (var rows = 1; rows <= most; rows++)
                            yield return layout with { RowsBelow = rows, AboveFlange = 0, Extension = 0, Haunch = haunch };
                }

            yield break;
        }

        var options = new (double Bolt, double Gauge, double Width, double[] Plates)[] { (20, 90, 200, new[] { 15.0, 20 }), (24, 100, 250, new[] { 20.0, 25 }), (30, 140, 300, new[] { 25.0, 30 }) };
        var (tension, web) = (Leg(beam.Tf), Leg(beam.Tw));
        foreach (var (bolt, gauge, width, plates) in options)
        {
            var hole = bolt <= 24 ? bolt + 2 : bolt + 3;
            if ((column.B - gauge) / 2 < 1.2 * hole || (gauge - beam.Tw) / 2 - 0.8 * web < bolt) continue;
            var last = bothWays ? beam.H / 2 - 45 : beam.H - beam.Tf - 50 - 90;
            var most = Math.Max(1, (int)Math.Floor((last - 60) / 90) + 1);
            foreach (var thick in plates)
                for (var rows = 1; rows <= most; rows++)
                    yield return new EndPlateLayout(bolt, thick, width, gauge, rows, bothWays, TensionWeld: tension, CompressionWeld: bothWays ? tension : 8, WebWeld: web);
        }
    }

    /// <summary>A full strength fillet's leg each side of a plate in S355, mm: a total throat of 1.2 t, 0.6 t each side, as P398 STEP 7 has it, to the even millimetre above.</summary>
    public static double Leg(double thickness) => Math.Max(6, 2 * Math.Ceiling(0.6 * thickness * Math.Sqrt(2) / 2));

    /// <summary>
    /// A four-bolt extended end plate, 4E, by AISC Design Guide 4 (2nd edition) as remembered, LRFD: the plate bf + 1 in wide, its bolts g = 5 1/2 in apart at pf = 2 in (d + 1/2 in where more)
    /// from the flange; h0 and h1 the outer and inner rows from the compression flange's centre. Bolt rupture without prying φ 2 Pt (h0 + h1), φ 0.75 and Fnt 90 ksi; the plate thick enough to
    /// leave no prying, φb Fyp tp² Yp with Yp = bp/2 [h1 (1/pfi + 1/s) + h0/pfo - 1/2] + 2/g h1 (pfi + s), s = √(bp g) / 2, against 1.11 Mu; the compression flange's four bolts in shear,
    /// Fnv 54 ksi; the column flange unstiffened, φ Fyc tcf² Yc with Yc = bcf/2 (h1/s + h0/s) + 2/g [h1 (s + 3c/4) + h0 (s + c/4) + c²/2] + g/2; its web by J10.2 and J10.3 under Mu / (d - tbf)
    /// over tbf + 2 tp; and its panel zone by J10.6, 0.9 x 0.6 Fy dc tw. A572 Grade 50 plate, A992 column.
    /// </summary>
    public static FourBoltMoment Aisc(SteelSection beam, SteelSection column, double bolt, double thickness)
    {
        const double fy = 345, fnt = 620, fnv = 372, es = 200000;
        var area = Math.PI * bolt * bolt / 4;
        var pf = Math.Max(50.8, bolt + 12.7);
        var bp = beam.B + 25.4;
        var g = Math.Min(139.7, bp - 2 * 1.25 * bolt);
        var (h0, h1) = (beam.H - beam.Tf / 2 + pf, beam.H - 1.5 * beam.Tf - pf);
        var s = Math.Sqrt(bp * g) / 2;
        var pfi = Math.Min(pf, s);
        var yp = bp / 2 * (h1 * (1 / pfi + 1 / s) + h0 / pf - 0.5) + 2 / g * h1 * (pfi + s);
        var sc = Math.Sqrt(column.B * g) / 2;
        var c = 2 * pf + beam.Tf;
        var yc = column.B / 2 * (h1 / sc + h0 / sc) + 2 / g * (h1 * (sc + 0.75 * c) + h0 * (sc + 0.25 * c) + c * c / 2) + g / 2;
        var bearing = beam.Tf + 2 * thickness;
        var k = column.Tf + column.Root;
        return new FourBoltMoment(bolt, thickness, bp, g, pf,
            0.75 * 2 * fnt * area * (h0 + h1) / 1e6,
            0.9 * fy * thickness * thickness * yp / 1e6,
            0.75 * 4 * fnv * area / 1000,
            0.9 * fy * column.Tf * column.Tf * yc / 1e6,
            1.0 * fy * column.Tw * (5 * k + bearing) / 1000,
            0.75 * 0.80 * column.Tw * column.Tw * (1 + 3 * (bearing / column.H) * Math.Pow(column.Tw / column.Tf, 1.5)) * Math.Sqrt(es * fy * column.Tf / column.Tw) / 1000,
            0.9 * 0.6 * fy * column.H * column.Tw / 1000);
    }

    /// <summary>US bolt sizes, in, and plate thicknesses, in, tried in turn.</summary>
    private static readonly double[] UsBolts = { 0.75, 0.875, 1.0, 1.125, 1.25 }, UsPlates = { 0.5, 0.625, 0.75, 0.875, 1.0, 1.25, 1.5 };

    // ---- from the model ----------------------------------------------------------------

    /// <summary>
    /// Which way a beam meets a column at a node: +1 or -1 into one of its flanges, along its depth; 0 into its web or askew. A column's local y lies along its flanges' width.
    /// </summary>
    public static int Side(AnalyticalMember beam, AnalyticalMember column, AnalyticalNode node)
    {
        var far = ReferenceEquals(node, beam.Start) ? beam.End : beam.Start;
        var (dx, dy) = (far.At.X - node.At.X, far.At.Y - node.At.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return 0;
        var (ax, ay) = (column.LocalY.X, column.LocalY.Y);
        var across = Math.Sqrt(ax * ax + ay * ay);
        if (across < 1e-9) return 0;
        var along = (dx * -ay + dy * ax) / (length * across);
        return Math.Abs(along) >= 0.9 ? Math.Sign(along) : 0;
    }

    private static bool TopAt(AnalyticalMember column, AnalyticalNode node) => ReferenceEquals(column.Start.At.Z >= column.End.At.Z ? column.Start : column.End, node);

    private static bool EndAt(AnalyticalMember member, AnalyticalNode node) => ReferenceEquals(member.Start, node) || ReferenceEquals(member.End, node);

    /// <summary>The steel column a beam's end frames into the side of at a node, the column running on past it, where there is one; the other joint designs leave such an end to this.</summary>
    public static AnalyticalMember? ColumnPast(AnalyticalModel model, AnalyticalNode node)
    {
        var columns = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column && member.Nodes.Contains(node)).ToList();
        var below = columns.FirstOrDefault(column => TopAt(column, node));
        var through = columns.FirstOrDefault(column => !EndAt(column, node)) ?? (below is not null && columns.Any(column => !ReferenceEquals(column, below) && !TopAt(column, node)) ? below : null);
        return through is not null && through.Section.Material.Kind == AnalyticalMaterialKind.Steel && SteelSection.Of(through.Section) is { IsI: true } ? through : null;
    }

    /// <summary>Whether a beam's end is fixed into a column's flange here, its moment this class's to carry.</summary>
    public static bool Carries(AnalyticalModel model, AnalyticalMember beam, AnalyticalNode node) =>
        beam.Kind == AnalyticalMemberKind.Beam && beam.Section.Material.Kind == AnalyticalMaterialKind.Steel && SteelSection.Of(beam.Section) is { IsI: true }
        && (ReferenceEquals(node, beam.Start) ? beam.StartRelease : beam.EndRelease) == MemberRelease.Fixed
        && (ColumnPast(model, node) ?? KneeColumn(model, node)) is { } column && Side(beam, column, node) != 0;

    /// <summary>The steel column whose top a beam's end is on, nothing standing on it there: a knee, where the beam is fixed to it.</summary>
    public static AnalyticalMember? KneeColumn(AnalyticalModel model, AnalyticalNode node)
    {
        var columns = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column && member.Nodes.Contains(node)).ToList();
        if (columns.Count != 1 || !TopAt(columns[0], node)) return null;
        return columns[0].Section.Material.Kind == AnalyticalMaterialKind.Steel && SteelSection.Of(columns[0].Section) is { IsI: true } ? columns[0] : null;
    }

    /// <summary>A member's forces at one of its nodes under each combination, each way it leans: the ends of its pieces that meet there.</summary>
    public static IEnumerable<(LoadCombination Combination, MemberForces Forces)> AtNode(SecondOrderAnalysis second, AnalyticalMember member, AnalyticalNode node)
    {
        var solver = second.Analysis.Solver;
        var at = second.Analysis.NodeOf(node);
        var pieces = second.Analysis.BeamsOf(member).Select(piece => solver.Beams[piece.Beam]).ToList();
        var stations = second.StationsOf(member);
        for (var k = 0; pieces.Count > 0 && k + 2 * pieces.Count <= stations.Count; k += 2 * pieces.Count)
            for (var p = 0; p < pieces.Count; p++)
            {
                if (pieces[p].I == at) yield return stations[k + 2 * p];
                if (pieces[p].J == at) yield return stations[k + 2 * p + 1];
            }
    }

    internal static IEnumerable<JointCheck> Of(BimDocument document, SecondOrderAnalysis second, bool us)
    {
        var model = second.Analysis.Model;
        var ends = model.Members.SelectMany(beam => new[] { beam.Start, beam.End }.Where(node => Carries(model, beam, node)).Select(node => (Beam: beam, Node: node))).ToList();
        foreach (var at in ends.GroupBy(end => end.Node))
        {
            var past = ColumnPast(model, at.Key);
            var knee = past is null;
            var column = past ?? KneeColumn(model, at.Key)!;
            var held = SteelSection.Of(column.Section)!;
            var fyColumn = us ? 345 : SteelDesign.YieldStrength(held.Tf);

            // Each beam's end: which side, its section, and each ultimate combination's hogging moment, kNm, shear and compression, kN.
            var beams = at.Select(end =>
            {
                var section = SteelSection.Of(end.Beam.Section)!;
                var forces = new Dictionary<string, (LoadCombination Combination, double Hogging, double Shear, double Compression)>();
                foreach (var (variant, f) in AtNode(second, end.Beam, at.Key))
                {
                    if (!variant.Factored) continue;
                    var hogging = f.TopInTension ? Math.Abs(f.My) : -Math.Abs(f.My);
                    if (!forces.TryGetValue(variant.Name, out var was) || Math.Abs(hogging) > Math.Abs(was.Hogging))
                        forces[variant.Name] = (variant, hogging, Math.Abs(f.Vz), -f.N);
                }

                return (end.Beam, Side: Side(end.Beam, column, at.Key), Section: section, Forces: forces);
            }).ToList();

            // The column's own longitudinal stress at the joint, for kwc: 6.2.6.2(2)'s greatest compression in its web at the root radius, its axial force and its bending each way there,
            // the worst of the combinations.
            var columnStations = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column && member.Nodes.Contains(at.Key))
                .SelectMany(member => AtNode(second, member, at.Key)).Where(s => s.Combination.Factored).ToList();
            var stress = columnStations.Count == 0 ? 0 : columnStations.Max(s => Math.Max(0, -s.Forces.N * 1000 / held.Area + Math.Abs(s.Forces.My) * 1e6 * (held.H / 2 - held.Tf - held.Root) / held.Iy + Math.Abs(s.Forces.Mz) * 1e6 * held.Tw / 2 / held.Iz));
            // P398: σcom,Ed no more than fy, so kwc no less than 0.7 - a column past its yield is the Strength tab's to say.
            stress = Math.Min(stress, fyColumn);
            var kwc = stress <= 0.7 * fyColumn ? 1 : 1.7 - stress / fyColumn;
            var names = beams.SelectMany(beam => beam.Forces.Keys).Distinct().ToList();

            // The web panel's shear for each combination: the flange forces either side, M / (h - tf), one side's against the other's.
            var panels = names.ToDictionary(name => name, name => Math.Abs(beams.Sum(beam => beam.Forces.TryGetValue(name, out var f) ? beam.Side * f.Hogging * 1000 / (beam.Section.H - beam.Section.Tf) : 0)));

            foreach (var beam in beams)
            {
                if (beam.Forces.Count == 0) continue;
                var opposite = beams.Where(other => other.Side == -beam.Side).ToList();
                double Beta(string name, double hogging)
                {
                    if (Math.Abs(hogging) < 1e-6) return 1;
                    var other = opposite.Sum(o => o.Forces.TryGetValue(name, out var f) ? f.Hogging : 0);
                    return Math.Min(2, Math.Abs(1 - other / hogging));
                }

                var name = $"{beam.Beam.Name} to {column.Name}";
                var hog = beam.Forces.Values.Max(f => f.Hogging);
                var sag = -beam.Forces.Values.Min(f => f.Hogging);
                if (us && knee)
                {
                    yield return new JointCheck(name, "Knee", Array.Empty<StrengthCheck>(), string.Empty,
                        "A beam fixed on a column's top, a knee: for a US project not worked out here - Design Guide 16's flush end plates and haunched knees are not in this.");
                    continue;
                }

                yield return us
                    ? Us(name, beam.Section, held, beam.Forces.Values.ToList(), panels, opposite.Count > 0)
                    : Uk(name, beam.Beam, beam.Section, held, fyColumn, kwc, beam.Forces.Values.ToList(), panels, n => Beta(n, beam.Forces[n].Hogging), hog, sag, opposite.Count > 0, knee);
            }
        }
    }

    private static JointCheck Uk(string name, AnalyticalMember member, SteelSection beam, SteelSection column, double fyColumn, double kwc,
        IReadOnlyList<(LoadCombination Combination, double Hogging, double Shear, double Compression)> forces, IReadOnlyDictionary<string, double> panels, Func<string, double> beta,
        double hog, double sag, bool twoSided, bool knee = false)
    {
        var fyBeam = SteelDesign.YieldStrength(beam.Tf);
        var bothWays = !knee && hog > 0.01 && sag > 0.1 * hog;
        var below = hog <= 0.01 && sag > 0.01;
        var npl = beam.Area * fyBeam / 1000;

        // What it asks, each combination: its moment about the centre of compression, less what compression in the beam does there where that is more than 5% of Npl (6.2.7.1(2)).
        double Asked((LoadCombination Combination, double Hogging, double Shear, double Compression) f) =>
            Math.Abs(f.Compression) > 0.05 * npl ? Math.Abs(f.Hogging) - f.Compression * (beam.H - beam.Tf) / 2 / 1000 : Math.Abs(f.Hogging);
        double Axial((LoadCombination Combination, double Hogging, double Shear, double Compression) f) => Math.Abs(f.Compression) > 0.05 * npl ? f.Compression : 0;

        var panelName = panels.MaxBy(p => p.Value).Key;
        var panelMost = panels[panelName];
        var cache = new Dictionary<(EndPlateLayout, double, double, ColumnStiffening), EndPlateMoment>();
        EndPlateMoment Resist(EndPlateLayout layout, ColumnStiffening stiffening, (LoadCombination Combination, double Hogging, double Shear, double Compression) f)
        {
            var key = (layout, Math.Round(beta(f.Combination.Name), 2), Math.Round(Axial(f)), stiffening);
            if (!cache.TryGetValue(key, out var resisted)) cache[key] = resisted = Eurocode(beam, fyBeam, column, fyColumn, layout, key.Item2, kwc, key.Item3, stiffening);
            return resisted;
        }

        // The worst of what it asks against what it has, every combination: the moment, the shear and the column web panel's shear.
        double Worst(EndPlateLayout layout, ColumnStiffening stiffening) => forces.Max(f =>
        {
            var resisted = Resist(layout, stiffening, f);
            return Math.Max(Math.Max(Asked(f) / Math.Max(resisted.Moment, 1e-9), f.Shear / resisted.Shear), panelMost / resisted.PanelShear);
        });

        var kind = knee ? "Knee" : "Moment end plate";
        var layouts = Layouts(beam, column, bothWays, knee).ToList();
        if (layouts.Count == 0)
            return new JointCheck(name, kind, Array.Empty<StrengthCheck>(), string.Empty, "No end plate layout of P398's fits this beam on this column's flange: not worked out.");

        // The plate and bolts first; only where none of them will do, the column strengthened, the least of P398 STEP 6 first: compression stiffeners, a web plate, tension stiffeners
        // with them, then both, two web plates, and backing plates as thick as the flange.
        var backing = 5 * Math.Ceiling(column.Tf / 5);
        // At a knee the column's cap plate is always there, its tension stiffener at the beam's top (P398 Table 2.2(c)); the plate and its haunch tried first, deeper haunches last.
        var strengthenings = knee
            ? new[]
            {
                new ColumnStiffening(Tension: true), new ColumnStiffening(true, true), new ColumnStiffening(false, true, 1), new ColumnStiffening(true, true, 1), new ColumnStiffening(true, true, 2),
                new ColumnStiffening(true, true, 2, backing)
            }
            : new[]
            {
                ColumnStiffening.None, new ColumnStiffening(Compression: true), new ColumnStiffening(WebPlates: 1), new ColumnStiffening(true, true), new ColumnStiffening(true, false, 1),
                new ColumnStiffening(true, true, 1), new ColumnStiffening(true, true, 2), new ColumnStiffening(true, true, 2, backing)
            };
        var tried = layouts.GroupBy(layout => layout.Haunch).SelectMany(haunched => strengthenings.SelectMany(stiffening => haunched.Select(layout => (Layout: layout, Stiffening: stiffening)))).ToList();
        var (chosen, stiffened) = tried.FirstOrDefault(each => Worst(each.Layout, each.Stiffening) <= 1);
        if (chosen is null) (chosen, stiffened) = tried.MinBy(each => Worst(each.Layout, each.Stiffening));
        EndPlateMoment Of((LoadCombination Combination, double Hogging, double Shear, double Compression) f) => Resist(chosen, stiffened, f);
        var governing = forces.MaxBy(f => Asked(f) / Math.Max(Of(f).Moment, 1e-9));
        var resisted = Of(governing);
        var sheared = forces.MaxBy(f => f.Shear);
        var checks = new List<StrengthCheck>
        {
            new("Moment at the joint", Asked(governing), resisted.Moment, "kNm", "BS EN 1993-1-8 6.2.7.2, (6.25)",
                $"rows {string.Join(", ", resisted.Rows.Select(row => $"{row.Force:0}"))} kN at {string.Join(", ", resisted.Rows.Select(row => $"{row.Lever:0}"))}; compression {resisted.Compression:0} kN, {resisted.CompressionBy}; β {beta(governing.Combination.Name):0.00}",
                governing.Combination.Name),
            new("Shear at the joint", sheared.Shear, Of(sheared).Shear, "kN", "Table 3.4, P398 STEP 5",
                $"{(chosen.BothWays ? chosen.RowsBelow + 1 : 1)} row{(chosen.BothWays ? "s" : string.Empty)} in shear, the rows in tension at 28%", sheared.Combination.Name),
            new("Column web panel in shear", panelMost, resisted.PanelShear, "kN", "6.2.6.1, (6.7)", twoSided ? "the flange forces either side, one against the other" : "a beam on one side", panelName)
        };

        // How stiff it is against how it is modelled: rigid by 5.2.2.5 from 8 E Ib / Lb in a braced frame, 25 in one that is not.
        var braced = 8 * E * beam.Iy / member.Length / 1e6;
        var stiffness = resisted.Stiffness;
        var classed = stiffness >= 25 * E * beam.Iy / member.Length / 1e6 ? "rigid in any frame" : stiffness >= braced ? "rigid where the frame is braced, semi-rigid where it is not" : "semi-rigid";
        if (stiffness < braced)
            checks.Add(new StrengthCheck("Stiffness, rigid as modelled", braced, stiffness, "kNm/rad", "5.2.2.5, 6.3.1", "8 E Ib / Lb, a braced frame's least for rigid", governing.Combination.Name));

        var depth = chosen.Depth(beam.H);
        var wants = $"{(knee ? "a flush end plate" : "an extended end plate")} {depth:0} by {chosen.Width:0} by {chosen.Thickness:0} S275{(chosen.BothWays ? ", extended both ways" : below ? ", extended below the bottom flange, which is in tension" : string.Empty)}" +
            $"{(chosen.Haunch > 0 ? $", on a haunch {chosen.Haunch:0} deep under the beam cut from the same section" : string.Empty)}, M{chosen.Bolt:0} 8.8 at {chosen.Gauge:0} gauge, " +
            $"{(chosen.BothWays ? 2 * (chosen.RowsBelow + 1) : chosen.RowsBelow + (knee ? 1 : 2))} rows; {chosen.TensionWeld:0} mm fillets to the {(chosen.BothWays ? "flanges" : "tension flange")} and {chosen.WebWeld:0} to the web" +
            $"{(chosen.BothWays ? string.Empty : $", {chosen.CompressionWeld:0} to the compression flange")}";
        // What strengthens the column, with its sizes: the stiffeners as wide as the flange's outstand, the tension pair's force by STEP 6A.
        if (stiffened.Any)
        {
            var parts = new List<string>();
            var at = chosen.BothWays ? "both the beam's flanges" : null;
            var outstand = (column.B - column.Tw) / 2;
            if (stiffened.Compression) parts.Add($"a pair of {resisted.StiffenerThickness:0} mm compression stiffeners {outstand:0} wide at {at ?? "the beam's compression flange"}, full depth (STEP 6B)");
            if (stiffened.Tension)
                parts.Add(knee
                    ? $"a {resisted.StiffenerThickness:0} mm cap plate at the beam's top, its tension stiffener, {resisted.TensionStiffenerForce:0} kN each side (STEP 6A)"
                    : $"a pair of {resisted.StiffenerThickness:0} mm tension stiffeners at {at ?? "its tension flange"}, {resisted.TensionStiffenerForce:0} kN each (STEP 6A)");
            if (stiffened.WebPlates > 0) parts.Add($"{(stiffened.WebPlates == 1 ? "a supplementary web plate" : "supplementary web plates both sides")} {resisted.WebPlateThickness:0} mm thick (STEP 6C)");
            if (stiffened.Backing > 0) parts.Add($"{stiffened.Backing:0} mm backing plates behind its flange (STEP 6E)");
            wants += $"; the column with {string.Join(", ", parts)}";
        }

        var over = checks.Where(check => check.Ratio > 1).ToList();
        if (over.Count == 1 && over[0].Name.StartsWith("Stiffness", StringComparison.Ordinal))
            wants += "; it is strong enough but not stiff enough to be modelled fixed - a thicker plate and column stiffeners stiffen it, or model its ends semi-rigid or pinned";
        else if (over.Count > 0)
            wants += "; and more than P398's layouts and column stiffening here give - a haunch, a deeper beam or a heavier column";

        return new JointCheck(name, kind, checks, wants,
            (knee ? "A knee, the column stopping at the beam's top: the end plate flush with it, the column's cap plate its tension stiffener, a haunch's flange in compression at 1.4 Afb fy (P398 STEP 2); its length, its own welds and the beam where it ends (STEP 8), and a moment reversing at it, are not worked out. " : string.Empty) +
            $"By SCI P398's steps on BS EN 1993-1-8 6.2.7: each row of bolts as T-stubs on the column flange and end plate, alone and in groups, the webs in tension, the compression zone and the column web panel, the column strengthened by STEP 6 only where a plate alone will not do; kwc {kwc:0.00}. " +
            $"Sj,ini {stiffness:0} kNm/rad, {classed} (5.2.2.5: 8 and 25 E Ib / Lb are {braced:0} and {braced * 25 / 8:0}). Welds full strength as P398 has them, not checked; diagonal stiffeners and haunches are not worked out.");
    }

    private static JointCheck Us(string name, SteelSection beam, SteelSection column,
        IReadOnlyList<(LoadCombination Combination, double Hogging, double Shear, double Compression)> forces, IReadOnlyDictionary<string, double> panels, bool twoSided)
    {
        var moment = forces.MaxBy(f => Math.Abs(f.Hogging));
        var sheared = forces.MaxBy(f => f.Shear);
        var mu = Math.Abs(moment.Hogging);
        // The smallest bolts, then the thinnest plate, that do; where none does, the largest of each.
        var tried = UsBolts.SelectMany(bolt => UsPlates.Select(plate => Aisc(beam, column, bolt * 25.4, plate * 25.4))).ToList();
        var chosen = tried.FirstOrDefault(each => each.BoltRupture >= mu && each.PlateYield >= 1.11 * mu && each.Shear >= sheared.Shear) ?? tried[^1];
        var flangeForce = mu * 1000 / (beam.H - beam.Tf);
        var panelName = panels.MaxBy(p => p.Value).Key;
        var checks = new List<StrengthCheck>
        {
            new("Bolts in tension, no prying", mu, chosen.BoltRupture, "kNm", "Design Guide 4, φ 2 Pt (h0 + h1)", $"φ 0.75, h0 and h1 {beam.H - beam.Tf / 2 + chosen.Pitch:0} and {beam.H - 1.5 * beam.Tf - chosen.Pitch:0} mm", moment.Combination.Name),
            new("End plate yielding", 1.11 * mu, chosen.PlateYield, "kNm", "Design Guide 4, φb Fyp tp² Yp", "1.11 Mu, thick enough that the bolts see no prying", moment.Combination.Name),
            new("Bolts in shear", sheared.Shear, chosen.Shear, "kN", "J3.6", "the four at the compression flange", sheared.Combination.Name),
            new("Column flange in bending", mu, chosen.ColumnFlange, "kNm", "Design Guide 4, φ Fyc tcf² Yc", "unstiffened", moment.Combination.Name),
            new("Column web local yielding", flangeForce, chosen.WebYielding, "kN", "J10.2", "the flange force Mu / (d - tbf)", moment.Combination.Name),
            new("Column web crippling", flangeForce, chosen.WebCrippling, "kN", "J10.3", string.Empty, moment.Combination.Name),
            new("Panel zone in shear", panels[panelName], chosen.PanelZone, "kN", "J10.6(a)", twoSided ? "the flange forces either side, one against the other" : "a beam on one side", panelName)
        };
        var columnShort = checks.Skip(3).Any(check => check.Ratio > 1);
        var wants = $"a four-bolt extended end plate (4E) {chosen.Width / 25.4:0.##} in wide by {chosen.Thickness / 25.4:0.###} in A572 Gr 50, {chosen.Bolt / 25.4:0.###} in A325-N at {chosen.Gauge / 25.4:0.##} in gauge, {chosen.Pitch / 25.4:0.##} in from each flange";
        if (columnShort) wants += "; and the column stiffened - continuity plates or a doubler plate";
        return new JointCheck(name, "Moment end plate", checks, wants,
            "By AISC Design Guide 4 as remembered, the thick plate design with no prying. Taken as fully restrained (B3.4). Bearing and tearout at the compression bolts, the welds and any stiffeners are not worked out.");
    }
}
