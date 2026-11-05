using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>A metre square of a plate: its average moments, N mm per mm, about its two ways and its twist.</summary>
public readonly record struct PlateStretch(double MomentU, double MomentV, double MomentUv);

/// <summary>The shear in a plate beside a line that holds or loads it, N per mm, and which way across the line it runs, in the plate's axes.</summary>
public readonly record struct PlateShear(Point2D At, double Shear, double AcrossU, double AcrossV);

/// <summary>One plate's checks, and the steel it comes to each way in each face, mm²/m: infinite where no steel will do, the slab being too thin.</summary>
public sealed record PlateCheck(AnalyticalPanel Panel, string Grade, string By, IReadOnlyList<StrengthCheck> Checks, double BottomX, double BottomY, double TopX, double TopY, string Limits)
{
    public StrengthCheck? Worst => Checks.Count == 0 ? null : Checks.MaxBy(check => check.Ratio);

    public double Ratio => Worst?.Ratio ?? 0;

    public bool Enough => Ratio <= 1 + 1e-9;
}

/// <summary>
/// Concrete floors, roofs and rafts designed from the plate moments of the analysis, averaged over a metre square so a peak over a
/// column does not stand for the slab: the steel each way in each face by Wood and Armer's equations, as bending about a
/// way and the twist between them come together; against a raft's own bars, and for a slab that has none in the model, what it needs.
/// Punching at each column on it, by BS EN 1992-1-1 6.4 or ACI 318-19 22.6, with the links it wants where it wants them and can take them;
/// one-way shear beside what holds it; crack control by Tables 7.2N and 7.3N or ACI 318-19 24.3.2 where there are bars to judge;
/// deflection against span/250, uncracked, and long-term by span to depth or the least thickness.
/// </summary>
public static class PlateDesign
{
    private const double Psi = 0.00689476;

    /// <summary>What is taken of the ultimate moment under the loads that stay on, for the steel's stress in service.</summary>
    private const double ServiceShare = 0.65;

    /// <summary>
    /// Wood and Armer's moments for bending and twist, each a positive amount of steel to find: the bottom each way and the
    /// top each way. Where one way comes out needing none, all the twist goes the other.
    /// </summary>
    public static (double BottomX, double BottomY, double TopX, double TopY) WoodArmer(double mx, double my, double mxy)
    {
        var twist = Math.Abs(mxy);
        var (bottomX, bottomY) = (mx + twist, my + twist);
        if (bottomX < 0) (bottomX, bottomY) = (0, Math.Abs(mx) < 1e-9 ? 0 : my + mxy * mxy / Math.Abs(mx));
        else if (bottomY < 0) (bottomX, bottomY) = (Math.Abs(my) < 1e-9 ? 0 : mx + mxy * mxy / Math.Abs(my), 0);
        var (topX, topY) = (mx - twist, my - twist);
        if (topX > 0) (topX, topY) = (0, Math.Abs(mx) < 1e-9 ? 0 : my - mxy * mxy / Math.Abs(mx));
        else if (topY > 0) (topX, topY) = (Math.Abs(my) < 1e-9 ? 0 : mx - mxy * mxy / Math.Abs(my), 0);
        return (Math.Max(0, bottomX), Math.Max(0, bottomY), Math.Max(0, -topX), Math.Max(0, -topY));
    }

    /// <summary>
    /// The largest bar and widest spacing that hold cracks to 0.3 mm at a steel stress, N/mm²: Tables 7.2N and 7.3N, by straight
    /// lines between their rows. Past the end of Table 7.3N no spacing is enough.
    /// </summary>
    public static (double Bar, double Spacing) CrackLimits(double stress)
    {
        double[] stresses = { 160, 200, 240, 280, 320, 360, 400, 450 };
        double[] bars = { 32, 25, 16, 12, 10, 8, 6, 5 };
        double[] spacings = { 300, 250, 200, 150, 100, 50, 0, 0 };
        if (stress <= stresses[0]) return (bars[0], spacings[0]);
        for (var i = 0; i + 1 < stresses.Length; i++)
            if (stress <= stresses[i + 1])
            {
                var share = (stress - stresses[i]) / (stresses[i + 1] - stresses[i]);
                return (bars[i] + share * (bars[i + 1] - bars[i]), spacings[i] + share * (spacings[i + 1] - spacings[i]));
            }

        return (bars[^1], 0);
    }

    /// <summary>
    /// How near to its limit crack control is, 1 being the limit. By BS EN 1992-1-1 7.3.3: a bar no larger than Table 7.2N has it, or at no
    /// more than Table 7.3N's spacing, either being enough. By ACI 318-19 24.3.2: s no more than 15 (40,000 / fs) - 2.5 cc, nor 12 (40,000 / fs), fs 2/3 fy.
    /// </summary>
    public static double CrackShare(bool us, double stress, double bar, double spacing, double clearCover)
    {
        if (us)
        {
            var fs = 2.0 / 3 * 60000;
            var most = Math.Min(15 * (40000 / fs) * 25.4 - 2.5 * clearCover, 12 * (40000 / fs) * 25.4);
            return spacing / most;
        }

        var (maxBar, maxSpacing) = CrackLimits(stress);
        return Math.Min(bar / maxBar, maxSpacing <= 0 ? double.PositiveInfinity : spacing / maxSpacing);
    }

    /// <summary>
    /// The punching links a column on a slab comes to: mm² on each perimeter, those perimeters' spacing, how many, the last's distance from
    /// its face, the perimeter past which it needs none, and how many legs of what size make it up.
    /// </summary>
    public sealed record PunchingLinks(double Area, double Spacing, int Perimeters, double Last, double Outer, int Legs, double Leg);

    /// <summary>
    /// Links by BS EN 1992-1-1 6.4.5 and 9.4.3: Asw = (vEd - 0.75 vRd,c) sr u1 / (1.5 fywd,ef), fywd,ef = 250 + 0.25 d, perimeters no more than 0.75 d
    /// apart, the first 0.5 d from its face, the last within 1.5 d of uout = β VEd / (vRd,c d), each leg at least Asw,min of (9.11) at 1.5 d round the perimeter.
    /// <paramref name="column"/> is what the perimeter has of the column; <paramref name="round"/> how much of a circle goes round it.
    /// </summary>
    public static PunchingLinks LinksEurocode(double force, double v, double vRdc, double beta, double near, double d, double column, double round, double fck)
    {
        var spacing = Math.Floor(0.75 * d / 25) * 25;
        var strength = Math.Min(250 + 0.25 * d, 500 / 1.15);
        var least = 0.08 * Math.Sqrt(fck) / 500 * spacing * 1.5 * d / 1.5;
        var leg = new[] { 10.0, 12, 16, 20, 25 }.FirstOrDefault(bar => Math.PI * bar * bar / 4 >= least, 25);
        var area = (v - 0.75 * vRdc) * spacing * near / (1.5 * strength);
        var outer = beta * force * 1000 / (vRdc * d);
        var last = Math.Max(0, (outer - column) / round - 1.5 * d);
        var perimeters = Math.Max(2, 1 + (int)Math.Ceiling((last - 0.5 * d) / spacing));
        return new PunchingLinks(area, spacing, perimeters, 0.5 * d + (perimeters - 1) * spacing, outer, (int)Math.Ceiling(area / (Math.PI * leg * leg / 4)), leg);
    }

    /// <summary>
    /// Punching at a column by BS EN 1992-1-1 6.4: the control perimeter 2d out, β 1.15, 1.4 and 1.5 for an inner, edge and corner column (6.4.3(6)),
    /// vRd,c from the steel over it; and at the column's face, 0.4 ν fcd, the lower of what is quoted. Where it is over vRd,c it wants links, if the
    /// slab can take them: 200 mm deep (9.3.2(1)) and up to twice vRd,c, the UK's limit as it is quoted for the National Annex.
    /// </summary>
    public static IEnumerable<StrengthCheck> PunchingEurocode(double force, double c1, double c2, double d, double steelShare, int edges, double fck, double thickness)
    {
        var (near, beta, face, column, round) = edges switch
        {
            0 => (2 * (c1 + c2) + 4 * Math.PI * d, 1.15, 2 * (c1 + c2), 2 * (c1 + c2), 2 * Math.PI),
            1 => (2 * c1 + c2 + 2 * Math.PI * d, 1.4, Math.Min(c2 + 3 * d, c2 + 2 * c1), 2 * c1 + c2, Math.PI),
            _ => (c1 + c2 + Math.PI * d, 1.5, Math.Min(3 * d, c1 + c2), c1 + c2, Math.PI / 2)
        };
        var vRdc = OneWayEurocode(d, steelShare, fck);
        var vMax = 0.4 * 0.6 * (1 - fck / 250) * fck / 1.5;
        var v = force * 1000;
        var at = beta * v / (near * d);
        var said = $"β {beta:0.00}, u1 {near:0} mm, {force:0.0} kN";
        if (at <= vRdc)
            yield return new StrengthCheck("Punching at 2d", at, vRdc, "N/mm²", "6.4.4", $"{said}, no links");
        else if (thickness < 200)
            yield return new StrengthCheck("Punching at 2d", at, vRdc, "N/mm²", "6.4.4, 9.3.2(1)", $"{said}: it would want links, and a slab under 200 mm cannot take them");
        else
        {
            var links = LinksEurocode(force, at, vRdc, beta, near, d, column, round, fck);
            yield return new StrengthCheck("Punching at 2d", at, 2 * vRdc, "N/mm²", "6.4.5, 9.4.3", $"needs links: {links.Area:0} mm² a perimeter ({links.Legs} H{links.Leg:0}), {links.Perimeters} perimeters {links.Spacing:0} apart, the last {links.Last:0} from its face; {said}");
        }

        yield return new StrengthCheck("Punching at the column's face", beta * v / (face * d), vMax, "N/mm²", "6.4.5(3)", $"u0 {face:0} mm");
    }

    /// <summary>
    /// Punching at a column by ACI 318-19 22.6: the perimeter d/2 out, vc the least of Table 22.6.5.2's three with λs, φ 0.75. Where that is not enough it wants
    /// stirrups, if d is 6 in and 16 of their bars (22.6.7.1): vc 2 λs √f'c (Table 22.6.6.1), vu up to φ 6√f'c, lines d/2 apart and the first d/2 from
    /// its face (Table 8.7.6.3), out to where vu at d/2 past the last is φ 2 λs √f'c (22.6.6.1).
    /// </summary>
    public static IEnumerable<StrengthCheck> PunchingAci(double force, double c1, double c2, double d, int edges, double fc)
    {
        var (bo, alpha, column, grows) = edges switch
        {
            0 => (2 * (c1 + d) + 2 * (c2 + d), 40.0, 2 * (c1 + c2), 8.0),
            1 => (2 * (c1 + d / 2) + c2 + d, 30.0, 2 * c1 + c2, 4.0),
            _ => (c1 + d / 2 + c2 + d / 2, 20.0, c1 + c2, 2.0)
        };
        var root = Math.Sqrt(fc / Psi) * Psi;
        var size = Math.Min(1, Math.Sqrt(2 / (1 + d / 25.4 / 10)));
        var beta = Math.Max(c1, c2) / Math.Max(1, Math.Min(c1, c2));
        var vc = Math.Min(4 * size * root, Math.Min((2 + 4 / beta) * size * root, (2 + alpha * d / bo) * size * root));
        var vu = force * 1000 / (bo * d);
        var said = $"bo {bo:0} mm, {force:0.0} kN";
        const double phi = 0.75;
        if (vu <= phi * vc)
            yield return new StrengthCheck("Punching at d/2", vu, phi * vc, "N/mm²", "22.6.5.2", $"{said}, no stirrups");
        else if (d < 152.4 || d < 16 * 9.5)
            yield return new StrengthCheck("Punching at d/2", vu, phi * vc, "N/mm²", "22.6.5.2, 22.6.7.1", $"{said}: it would want stirrups, and d under 6 in or 16 bars cannot take them");
        else
        {
            var with = 2 * size * root;
            var spacing = Math.Floor(d / 2 / 25.4) * 25.4;
            var area = (vu / phi - with) * bo * spacing / (60000 * Psi);
            var outer = force * 1000 / (phi * with * d);
            var last = Math.Max(0, (outer - column) / grows - d / 2);
            var lines = Math.Max(2, 1 + (int)Math.Ceiling((last - d / 2) / spacing));
            var legs = (int)Math.Ceiling(area / (Math.PI * 9.525 * 9.525 / 4));
            yield return new StrengthCheck("Punching at d/2", vu, phi * 6 * root, "N/mm²", "22.6.6, 8.7.6.3", $"needs stirrups: {area:0} mm² a line ({legs} #3 legs), {lines} lines {spacing / 25.4:0} in apart; {said}");
        }
    }

    /// <summary>What a slab or footing with no shear steel carries by BS EN 1992-1-1 6.2.2 and 6.4.4, N/mm²: 0.18/γc k (100 ρ fck)^⅓, and vmin at the least.</summary>
    public static double OneWayEurocode(double d, double steelShare, double fck)
    {
        var k = Math.Min(2, 1 + Math.Sqrt(200 / d));
        return Math.Max(0.18 / 1.5 * k * Math.Pow(100 * Math.Min(steelShare, 0.02) * fck, 1.0 / 3), 0.035 * Math.Pow(k, 1.5) * Math.Sqrt(fck));
    }

    /// <summary>
    /// What a slab or footing with no shear steel carries by ACI 318-19 Table 22.5.5.1(c), φ 0.75, N/mm²: 8 λs ρ^⅓ √f'c, no more than 5√f'c;
    /// λs 1 for a footing or a mat (13.2.6.2).
    /// </summary>
    public static double OneWayAci(double d, double steelShare, double fc, bool footing = false)
    {
        var root = Math.Sqrt(fc / Psi) * Psi;
        var size = footing ? 1 : Math.Min(1, Math.Sqrt(2 / (1 + d / 25.4 / 10)));
        return 0.75 * Math.Min(8 * size * Math.Cbrt(steelShare) * root, 5 * root);
    }

    /// <summary>
    /// The span over effective depth a slab may have by BS EN 1992-1-1 7.4.2, Expressions (7.16a) and (7.16b) with no compression steel: K a factor for how it is
    /// held, and F2, F3 of the UK's reading - 7 m over a longer span, 8.5 m on a flat slab, and the steel given over what it needs, up to 1.5 - the whole no more than 40 K.
    /// </summary>
    public static double SpanDepthLimit(double k, double rho, double fck, double span, double givenOverNeeded, bool flat)
    {
        var (root, rho0) = (Math.Sqrt(fck), Math.Sqrt(fck) / 1000);
        var ratio = rho <= rho0
            ? 11 + 1.5 * root * rho0 / rho + 3.2 * root * Math.Pow(rho0 / rho - 1, 1.5)
            : 11 + 1.5 * root * rho0 / rho;
        var most = flat ? 8500 : 7000;
        return Math.Min(40, ratio) * k * (span > most ? most / span : 1) * Math.Min(1.5, givenOverNeeded);
    }

    /// <summary>
    /// The spans a plate has, from what holds it: from the point furthest from any support, the two ways to what is nearest each side within half a metre of its line. Where neither way
    /// closes it is a cantilever, of that furthest distance. On columns, the long span is the furthest from any column to what is nearest it; supports away from its edges make it continuous.
    /// </summary>
    public static (double Short, double Long, bool Flat, bool Continuous, bool Cantilever)? SpansOf(IReadOnlyList<(Point2D At, bool Column)> held, IReadOnlyList<Point2D> outline, IReadOnlyList<Point2D> points)
    {
        if (held.Count == 0 || points.Count == 0) return null;
        var far = points.MaxBy(point => held.Min(support => support.At.DistanceTo(point)));
        var clear = held.Min(support => support.At.DistanceTo(far));

        double? Across(Point2D from, Func<Point2D, double> along, Func<Point2D, double> side, bool forward)
        {
            var hits = held.Where(support => Math.Abs(side(support.At) - side(from)) <= 450 && (forward ? along(support.At) > along(from) + 1 : along(support.At) < along(from) - 1)).ToList();
            return hits.Count == 0 ? null : hits.Min(support => Math.Abs(along(support.At) - along(from)));
        }

        double? Way(Point2D from, Func<Point2D, double> along, Func<Point2D, double> side) =>
            Across(from, along, side, true) is { } on && Across(from, along, side, false) is { } back ? on + back : null;

        var (wayX, wayY) = (Way(far, p => p.X, p => p.Y), Way(far, p => p.Y, p => p.X));
        var continuous = held.Any(support => Enumerable.Range(0, outline.Count).All(i => Line2D.DistanceFromSegment(support.At, outline[i], outline[(i + 1) % outline.Count]) > 400));
        var columns = held.Where(support => support.Column).ToList();
        if (columns.Count > 0)
        {
            var longest = columns.Max(column => new[]
            {
                Across(column.At, p => p.X, p => p.Y, true), Across(column.At, p => p.X, p => p.Y, false), Across(column.At, p => p.Y, p => p.X, true), Across(column.At, p => p.Y, p => p.X, false)
            }.Where(each => each is not null).Select(each => each!.Value).DefaultIfEmpty(2 * clear).Max());
            return (2 * clear, longest, true, continuous, false);
        }

        if (wayX is null && wayY is null) return (clear, clear, false, continuous, true);
        var ways = new[] { wayX, wayY }.Where(each => each is not null).Select(each => each!.Value).ToList();
        return (ways.Min(), ways.Max(), false, continuous, false);
    }

    /// <summary>
    /// The least thickness of a slab by ACI 318-19, mm, Grade 60: on columns Table 8.3.1.1, ln/30 and 125 mm; on walls or beams all round, taken as stiff, Table 8.3.1.2(d), ln (0.8 + fy/1400) / (36 + 9β)
    /// and 90 mm; spanning one way, Table 7.3.1.1, l/20, l/24 with a support between, l/10 as a cantilever.
    /// </summary>
    public static (double Thickness, string Clause, string Said) ThicknessAci(double shorter, double longer, bool flat, bool continuous, bool cantilever)
    {
        if (cantilever) return (shorter / 10, "Table 7.3.1.1", "l/10, a cantilever");
        if (flat) return (Math.Max(longer / 30, 125), "Table 8.3.1.1", "ln/30, on columns");
        var beta = longer / Math.Max(1, shorter);
        if (beta <= 2) return (Math.Max(longer * 1.1 / (36 + 9 * beta), 90), "Table 8.3.1.2", $"two-way, β {beta:0.00}");
        return continuous ? (shorter / 24, "Table 7.3.1.1", "l/24, one way") : (shorter / 20, "Table 7.3.1.1", "l/20, one way");
    }

    /// <summary>Every concrete floor, roof and raft.</summary>
    public static IReadOnlyList<PlateCheck> Of(BimDocument document, SecondOrderAnalysis second)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var results = new List<PlateCheck>();
        foreach (var panel in second.Analysis.Model.Panels.Where(panel => panel.Kind != AnalyticalPanelKind.Wall))
        {
            if (panel.Material.Kind != AnalyticalMaterialKind.Concrete)
            {
                results.Add(new PlateCheck(panel, panel.Material.Quality, string.Empty, Array.Empty<StrengthCheck>(), 0, 0, 0, 0, "Not checked: only concrete plates are worked out here."));
                continue;
            }

            results.Add(Check(second, panel, us));
        }

        return results;
    }

    private static double PerMetre(BarSpec? bars) => bars is null ? 0
        : bars.FabricArea is { } fabric ? Math.Min(fabric.Main, fabric.Cross)
        : bars.Spacing is { } spacing ? bars.BarArea * 1000 / spacing : 0;

    private static PlateCheck Check(SecondOrderAnalysis second, AnalyticalPanel panel, bool us)
    {
        var fck = ConcreteDesign.StrengthOf(panel.Material.Quality);
        var h = panel.Thickness;
        var raft = panel.Element as RaftFoundation;
        var (topBars, bottomBars) = raft is null ? (null, null) : (BarSpec.Parse(raft.TopReinforcement), BarSpec.Parse(raft.BottomReinforcement));
        var (topCover, bottomCover) = raft is null ? (30.0, 30.0) : (Reinforcement.CoverTop, Reinforcement.CoverOnBlinding);
        var (dTop, dBottom) = (h - topCover - (topBars?.Diameter ?? 12), h - bottomCover - (bottomBars?.Diameter ?? 12));

        // The worst of each way and face, and under what.
        var worst = new (double Moment, string Under)[4];
        foreach (var (combination, stretches) in second.PlateStretches(panel))
            foreach (var stretch in stretches)
            {
                var moments = WoodArmer(stretch.MomentU, stretch.MomentV, stretch.MomentUv);
                var each = new[] { moments.BottomX, moments.BottomY, moments.TopX, moments.TopY };
                for (var i = 0; i < 4; i++)
                    if (each[i] > worst[i].Moment) worst[i] = (each[i], combination.Name);
            }

        var checks = new List<StrengthCheck>();
        var needs = new double[4];
        double Least(double d) => us ? 0.0018 * 1000 * h : ConcreteDesign.LeastEurocode(1000, d, fck);
        for (var i = 0; i < 4; i++)
        {
            var (moment, under) = (worst[i].Moment / 1000, worst[i].Under);
            var bottom = i < 2;
            var d = bottom ? dBottom : dTop;
            var bars = bottom ? bottomBars : topBars;
            var (needed, problem) = us ? ConcreteDesign.Aci(moment, 1000, d, fck, Least(d)) : ConcreteDesign.Eurocode(moment, 1000, d, fck, Least(d));
            needs[i] = problem is null ? needed : double.PositiveInfinity;
            var name = $"{(bottom ? "Bottom" : "Top")} steel, {(i % 2 == 0 ? "x" : "y")} way";
            var clause = us ? "22.2, 8.6.1.1" : "6.1, 9.3.1.1";
            if (bars is not null)
                checks.Add(new StrengthCheck(name, needed, PerMetre(bars), "mm²/m", clause, problem ?? $"{moment:0.0} kNm/m, {bars}", under));
            else
            {
                var most = us
                    ? 0.9 * 0.85 * fck * 1000 * 0.375 * ConcreteDesign.Beta1(fck) * d * (d - 0.375 * ConcreteDesign.Beta1(fck) * d / 2) / 1e6
                    : 0.167 * 1000 * d * d * fck / 1e6;
                checks.Add(new StrengthCheck(name, moment, most, "kNm/m", clause, problem ?? $"needs {needed:0} mm²/m", under));
            }

            // What a bar there would crack at, where there are bars to judge.
            if (bars is not null)
            {
                var given = Math.Max(1, PerMetre(bars));
                var stress = (us ? 413.7 : 500 / 1.15) * ServiceShare * Math.Min(1, needed / given);
                var spacing = bars.Spacing ?? (bars.Fabric is { } fabric ? Reinforcement.Fabrics[fabric].Pitch : 200);
                checks.Add(new StrengthCheck($"Cracks, {(bottom ? "bottom" : "top")} {(i % 2 == 0 ? "x" : "y")}", CrackShare(us, stress, bars.Diameter, spacing, (bottom ? bottomCover : topCover)), 1, "", us ? "24.3.2" : "7.3.3", $"σs {stress:0} N/mm²", under));
            }
        }

        // Punching where a column stands on it or comes down to it.
        var analysis = second.Analysis;
        var outline = panel.FlatOutline;
        var d2 = Math.Min(dTop, dBottom);
        var share = Math.Sqrt(needs[2] * needs[3]) / (1000 * d2);
        var held = panel.Outline.Concat(panel.Inside).ToHashSet();
        var around = new List<(Point2D At, double Reach)>();
        var wanted = new List<string>();
        foreach (var column in analysis.Model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column && member.Nodes.Any(held.Contains)))
        {
            var node = column.Nodes.First(held.Contains);
            var at = panel.Flat(node.At);
            var (c1, c2) = Sides(column.Section);
            var edges = Enumerable.Range(0, outline.Count).Count(i => Line2D.DistanceFromSegment(at, outline[i], outline[(i + 1) % outline.Count]) <= Math.Max(c1, c2) / 2 + 2 * d2);
            // A slab on beams does not punch: it is carried along them, and a column they frame into takes it from them.
            if (analysis.Model.Members.Any(member => member.Kind == AnalyticalMemberKind.Beam && member.Nodes.Contains(node))) continue;
            var (combination, force) = second.PlateForceOn(panel, node).MaxBy(entry => entry.Force);
            if (force < 0.5) continue;
            var punched = us ? PunchingAci(force, c1, c2, d2, edges, fck) : PunchingEurocode(force, c1, c2, d2, share, edges, fck, h);
            foreach (var check in punched)
            {
                checks.Add(check with { Name = $"{check.Name}, {column.Name}", Under = combination.Name });
                if (check.Note.StartsWith("needs", StringComparison.Ordinal)) wanted.Add(column.Name);
            }

            around.Add((at, Math.Max(c1, c2) / 2 + 2 * d2 + 500));
        }

        // One-way shear beside what holds or loads it, the steel it is sure of; round a column punching stands for it.
        double SteelThere()
        {
            var given = new[] { topBars, bottomBars }.Where(bars => bars is not null).Select(PerMetre).ToList();
            return (given.Count > 0 ? given.Min() : Least(d2)) / (1000 * d2);
        }

        (double Shear, string Under, PlateShear? Where) shearWorst = (0, string.Empty, null);
        foreach (var (combination, shears) in second.PlateShears(panel))
            foreach (var each in shears)
                if (each.Shear > shearWorst.Shear && !around.Any(column => column.At.DistanceTo(each.At) <= column.Reach))
                    shearWorst = (each.Shear, combination.Name, each);

        if (shearWorst.Where is { } shearAt)
        {
            var (first, x0, y0) = (SteelThere(), outline.Min(p => p.X), outline.Min(p => p.Y));
            var carries = (us ? OneWayAci(d2, first, fck, raft is not null) : OneWayEurocode(d2, first, fck)) * d2;
            checks.Add(new StrengthCheck("One-way shear", shearWorst.Shear, carries, "kN/m", us ? "22.5.5.1" : "6.2.2", $"beside its support at {(shearAt.At.X - x0) / 1000:0.0}, {(shearAt.At.Y - y0) / 1000:0.0} m, ρ {first * 100:0.00}%", shearWorst.Under));
        }

        // How far it bends, uncracked, under everything but the wind.
        var flat = outline;
        var span = Math.Min(flat.Max(p => p.X) - flat.Min(p => p.X), flat.Max(p => p.Y) - flat.Min(p => p.Y));
        var moved = Math.Abs(analysis.PanelOf(panel, analysis.Total).Deflection);
        if (panel.Subgrade is null)
        {
            checks.Add(new StrengthCheck("Deflection", moved, span / 250, "mm", us ? "24.2" : "7.4.1(4)", $"span/250 of its shorter way, {span:0} mm, uncracked", "All but wind"));

            // Long-term, by the rules that stand in for working it out; a plate on nothing here is left to the check above.
            var points = analysis.ShellsOf(panel).Select(s =>
            {
                var shell = analysis.Solver.Shells[s];
                var corners = new[] { shell.I, shell.J, shell.K }.Select(analysis.Solver.NodeAt).ToList();
                return panel.Flat(new Point3D(corners.Average(p => p.X), corners.Average(p => p.Y), corners.Average(p => p.Z)));
            }).ToList();
            if (SpansOf(second.HeldAt(panel), outline, points) is { } spans)
            {
                if (us)
                {
                    var (least, clause, said) = ThicknessAci(spans.Short, spans.Long, spans.Flat, spans.Continuous, spans.Cantilever);
                    checks.Add(new StrengthCheck("Least thickness", least, h, "mm", clause, $"{said}, spans {spans.Short:0} and {spans.Long:0} mm"));
                }
                else
                {
                    var length = spans.Flat ? spans.Long : spans.Short;
                    var (k, how) = spans.Cantilever ? (0.4, "a cantilever") : spans.Flat ? (1.2, "on columns") : spans.Continuous ? (1.3, "continuous") : (1.0, "simply held");
                    var most = Math.Max(needs[0], needs[1]);
                    var rho = double.IsInfinity(most) ? 0.02 : Math.Max(most, 1) / (1000 * dBottom);
                    var given = bottomBars is not null && !double.IsInfinity(most) ? PerMetre(bottomBars) / Math.Max(most, 1) : 1;
                    checks.Add(new StrengthCheck("Span to depth", length / dBottom, SpanDepthLimit(k, rho, fck, length, given, spans.Flat), "", "7.4.2", $"{length:0} mm span, {how}, K {k:0.0}, ρ {rho * 100:0.00}%"));
                }
            }
        }

        var limits = raft is null
            ? "No bars are in the model: the steel said is what the slab needs at its worst, each way and face. Cracking is not worked out."
            : "Against its own bars or fabric, the lesser way of a fabric. Steel stress in service taken as 0.65 of the ultimate.";
        if (wanted.Count > 0) limits += $" {(us ? "Stirrups" : "Punching links")} are wanted at {string.Join(", ", wanted.Distinct())}, and the model does not hold them.";
        return new PlateCheck(panel, panel.Material.Quality, us ? "ACI 318-19" : "BS EN 1992-1-1 and its UK National Annex", checks, needs[0], needs[1], needs[2], needs[3], limits);
    }

    /// <summary>A column's two sides, mm: a rectangle's, an I's, a circle's taken as the square of its area.</summary>
    private static (double C1, double C2) Sides(AnalyticalSection section)
    {
        var d = section.Dimensions;
        return section.Shape switch
        {
            "Circle" or "Pipe" => (d[0] * Math.Sqrt(Math.PI) / 2, d[0] * Math.Sqrt(Math.PI) / 2),
            _ when d.Count >= 2 => (d[0], d[1]),
            _ => (300, 300)
        };
    }
}
