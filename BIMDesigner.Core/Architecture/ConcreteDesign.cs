using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Concrete members. The model holds no bars for a concrete beam or column, so they are designed rather
/// than checked: what is checked is whether the section can carry its forces, and the note says what
/// steel it comes to - bars of 20 mm and links of 10, 30 mm of nominal cover to the links. A ground beam
/// has its bars, and is checked against them. BS EN 1992-1-1 with its UK National Annex (αcc 0.85, γc 1.5,
/// γs 1.15, B500), or ACI 318-19 (Grade 60). Beams: bending and shear; axial force in a beam is left out.
/// Columns: a strain compatibility interaction of axial force and moment, each way, with the least
/// eccentricity and imperfection and second order effects by nominal curvature or moment magnification.
/// </summary>
public static class ConcreteDesign
{
    private const double Psi = 0.00689476;
    private const double Fyk = 500, FyUs = 60000 * Psi;

    /// <summary>A concrete grade's fck, N/mm², from its name: C30/37, or 4000 psi.</summary>
    public static double StrengthOf(string quality)
    {
        if (quality.StartsWith('C') && quality.IndexOf('/') is > 1 and var slash && double.TryParse(quality[1..slash], out var fck)) return fck;
        if (quality.EndsWith(" psi", StringComparison.Ordinal) && double.TryParse(quality[..^4], out var psi)) return psi * Psi;
        return 30;
    }

    /// <summary>The tension steel a rectangular section needs for a moment by BS EN 1992-1-1, mm² - the least given where that is more - or why it cannot.</summary>
    public static (double Needed, string? Problem) Eurocode(double moment, double width, double depth, double fck, double least)
    {
        if (depth <= 0) return (0, "no depth left inside the cover");
        var m = moment * 1e6;
        var k = m / (width * depth * depth * fck);
        if (k > 0.167) return (least, $"K is {k:0.000}, over K' 0.167 - it would need compression steel; make it deeper");
        var z = Math.Min(0.95 * depth, depth * (0.5 + Math.Sqrt(0.25 - k / 1.134)));
        return (Math.Max(least, m / (0.87 * Fyk * z)), null);
    }

    /// <summary>The tension steel a rectangular section needs for a moment by ACI 318-19 22.2, φ 0.9, mm² - the least given where that is more - or why it cannot.</summary>
    public static (double Needed, string? Problem) Aci(double moment, double width, double depth, double fc, double least)
    {
        const double phi = 0.9;
        if (depth <= 0) return (0, "no depth left inside the cover");
        var m = moment * 1e6;
        var discriminant = depth * depth - 2 * m / (phi * 0.85 * fc * width);
        if (discriminant < 0) return (least, "the section cannot carry it, whatever its bars; make it deeper or wider");
        var a = depth - Math.Sqrt(discriminant);
        var needed = Math.Max(least, 0.85 * fc * width * a / FyUs);
        var cd = a / Beta1(fc) / depth;
        return (needed, cd > 0.375 ? $"c/d is {cd:0.00}, over 0.375 - not tension-controlled; make it deeper" : null);
    }

    /// <summary>ACI 318-19 Table 22.2.2.4.3's β1: 0.85 to 4,000 psi, 0.05 less for every 1,000 psi over, 0.65 at the least.</summary>
    public static double Beta1(double fc)
    {
        var psi = fc / Psi;
        return psi <= 4000 ? 0.85 : Math.Max(0.65, 0.85 - 0.05 * (psi - 4000) / 1000);
    }

    /// <summary>BS EN 1992-1-1's least tension steel for a width and effective depth, mm²: 0.26 fctm/fyk, 0.0013 at the least.</summary>
    public static double LeastEurocode(double width, double depth, double fck) =>
        Math.Max(0.26 * 0.30 * Math.Pow(fck, 2.0 / 3) / Fyk, 0.0013) * width * depth;

    /// <summary>ACI 318-19 9.6.1.2's least tension steel, mm²: the greater of 3√f'c/fy and 200/fy.</summary>
    public static double LeastAci(double width, double depth, double fc) =>
        Math.Max(3 * Math.Sqrt(fc / Psi), 200) / (FyUs / Psi) * width * depth;

    // ---- shear --------------------------------------------------------------------------

    /// <summary>
    /// A beam in shear by BS EN 1992-1-1 6.2.3: what the struts carry at their steepest, kN, the angle
    /// from 45° to cot θ 2.5, the links it needs, mm²/m, the least of 9.2.2(5) given where that is more;
    /// and with links in it, what they and the struts carry at the best angle.
    /// </summary>
    public static (double Crushing, double Needed, double? WithLinks) ShearEurocode(double shear, double width, double depth, double fck, double? links)
    {
        var (v, z, fywd, nu) = (shear * 1e3, 0.9 * depth, Fyk / 1.15, 0.6 * (1 - fck / 250));
        double Struts(double cot) => width * z * nu * (fck / 1.5) / (cot + 1 / cot);
        var angle = Math.Clamp(1 / Math.Tan(0.5 * Math.Asin(Math.Min(1, v / (width * z) / (0.20 * fck * (1 - fck / 250))))), 1, 2.5);
        var needed = Math.Max(v / (z * fywd * angle), 0.08 * Math.Sqrt(fck) / Fyk * width) * 1000;
        double? carried = null;
        if (links is { } given)
            carried = Enumerable.Range(0, 16).Select(i => 1 + i * 0.1).Max(cot => Math.Min(Struts(cot), given / 1000 * z * fywd * cot)) / 1e3;
        return (Struts(1) / 1e3, needed, carried);
    }

    /// <summary>
    /// A beam in shear by ACI 318-19 22.5, φ 0.75: the most the section can carry, 22.5.1.2, kN, the
    /// stirrups it needs, mm²/m, the least of 9.6.3.4 given where that is more; and with stirrups in it, φVn.
    /// </summary>
    public static (double Crushing, double Needed, double? WithLinks) ShearAci(double shear, double width, double depth, double fc, double? links)
    {
        const double phi = 0.75;
        var root = Math.Sqrt(fc / Psi) * Psi;
        var (vu, vc) = (shear * 1e3, 2 * root * width * depth);
        var needed = Math.Max((vu / phi - vc) / (FyUs * depth), Math.Max(0.75 * root, 50 * Psi) * width / FyUs) * 1000;
        double? carried = links is { } given ? phi * (vc + given / 1000 * FyUs * depth) / 1e3 : null;
        return (phi * (vc + 8 * root * width * depth) / 1e3, needed, carried);
    }

    // ---- columns ------------------------------------------------------------------------

    /// <summary>A column section: its depth across the axis it bends about, its width, whether round, and how far its bars' centres are in from its face, mm.</summary>
    public sealed record ColumnSection(double Depth, double Width, bool Round, double Inset)
    {
        public double Area => Round ? Math.PI * Depth * Depth / 4 : Depth * Width;

        public double Radius => Round ? Depth / 4 : Depth / Math.Sqrt(12);
    }

    /// <summary>A point of a section's interaction: the compression it carries, N, positive, and the moment, N mm, at a neutral axis depth.</summary>
    private static (double N, double M, double TensionStrain) Point(ColumnSection s, double steel, double x, double fcd, double blockDepth, double strainAt, double pivot, double fyd)
    {
        const double es = 200000;
        var h = s.Depth;
        var (n, m) = (0.0, 0.0);
        var block = Math.Min(blockDepth * x, h);

        // The concrete: a rectangle in closed form, a circle in strips.
        if (!s.Round)
        {
            var force = fcd * s.Width * block;
            (n, m) = (force, force * (h - block) / 2);
        }
        else
            for (var i = 0; i < 24; i++)
            {
                var z = (i + 0.5) * h / 24;
                if (z >= block) break;
                var chord = 2 * Math.Sqrt(Math.Max(0, h * h / 4 - Math.Pow(z - h / 2, 2)));
                var force = fcd * chord * h / 24;
                n += force;
                m += force * (h / 2 - z);
            }

        // The bars: half at each face, or eight in a ring. Past the section's depth the Eurocode's strain turns about a point within it (3.1.7).
        double Strain(double z) => x <= h || pivot <= 0 ? strainAt * (x - z) / x : pivot * (x - z) / Math.Max(1, x - 3 * h / 7);
        var deepest = double.NegativeInfinity;
        void Bar(double z, double area)
        {
            var strain = Strain(z);
            var stress = Math.Clamp(es * strain, -fyd, fyd);
            if (z < block && stress > 0) stress -= fcd;
            n += stress * area;
            m += stress * area * (h / 2 - z);
            deepest = Math.Max(deepest, -strain);
        }

        if (s.Round)
            for (var i = 0; i < 8; i++) Bar(h / 2 + (h / 2 - s.Inset) * Math.Cos(i * Math.PI / 4), steel / 8);
        else
        {
            Bar(s.Inset, steel / 2);
            Bar(h - s.Inset, steel / 2);
        }

        return (n, m, deepest);
    }

    /// <summary>The neutral axis depths a section's interaction is drawn through.</summary>
    private static IEnumerable<double> Depths(double h) => Enumerable.Range(0, 48).Select(i => h * 0.02 * Math.Pow(1.135, i));

    /// <summary>The most moment a section carries at a compression, given a curve of points through it; zero past its top.</summary>
    private static double MomentAt(IReadOnlyList<(double N, double M)> curve, double compression)
    {
        var most = 0.0;
        var found = false;
        for (var i = 0; i + 1 < curve.Count; i++)
        {
            var (a, b) = (curve[i], curve[i + 1]);
            if ((compression - a.N) * (compression - b.N) > 0) continue;
            found = true;
            var t = Math.Abs(b.N - a.N) < 1e-9 ? 0 : (compression - a.N) / (b.N - a.N);
            most = Math.Max(most, a.M + t * (b.M - a.M));
        }

        return found ? most : 0;
    }

    /// <summary>A section's moment capacity at a compression by BS EN 1992-1-1, N mm: the rectangular block of 3.1.7 at η fcd, λ 0.8, εcu3 0.0035.</summary>
    public static double MomentEurocode(ColumnSection s, double steel, double compression, double fck)
    {
        var (fcd, fyd) = (0.85 * fck / 1.5, Fyk / 1.15);
        var curve = Depths(s.Depth).Select(x => Point(s, steel, x, fcd, 0.8, 0.0035, 0.00175, fyd)).Select(point => (point.N, point.M)).ToList();
        return MomentAt(curve, compression);
    }

    /// <summary>A section's φMn at a φPn by ACI 318-19: the block of 22.2.2, εcu 0.003, φ 0.65 to 0.90 by the tension strain, tied, φPn no more than 0.65 of 0.80 Po.</summary>
    public static double MomentAci(ColumnSection s, double steel, double compression, double fc)
    {
        const double es = 200000;
        var most = 0.65 * 0.80 * (0.85 * fc * (s.Area - steel) + FyUs * steel);
        if (compression > most) return 0;
        var yield = FyUs / es;
        var curve = Depths(s.Depth).Select(c =>
        {
            var point = Point(s, steel, c, 0.85 * fc, Beta1(fc), 0.003, 0, FyUs);
            var phi = 0.65 + 0.25 * Math.Clamp((point.TensionStrain - yield) / 0.003, 0, 1);
            return (N: Math.Min(most, phi * point.N), M: phi * point.M);
        }).ToList();
        return MomentAt(curve, compression);
    }

    /// <summary>
    /// A column under an axial compression, kN, and two moments, kNm, with its buckling lengths, mm, by
    /// BS EN 1992-1-1: the moments raised to the least eccentricity of 6.1(4) with the imperfection of 5.2,
    /// and nominal curvature (5.8.8) where it is slender (5.8.3.1), the two ways together by 5.8.9(4).
    /// How far over 1 its interaction is at the most steel, 4% (9.5.2), and the least steel that does.
    /// </summary>
    public static (double Unity, double Steel) ColumnEurocode(ColumnSection strong, ColumnSection weak, double compression, double momentY, double momentZ, double lengthStrong, double lengthWeak, double fck)
    {
        var (n, fcd, fyd, area) = (Math.Max(0, compression) * 1e3, 0.85 * fck / 1.5, Fyk / 1.15, strong.Area);

        double Designed(ColumnSection s, double moment, double length)
        {
            var effective = s.Depth - s.Inset;
            var first = Math.Max(moment * 1e6 + n * length / 400, n * Math.Max(s.Depth / 30, 20));
            var slenderness = length / s.Radius;
            var share = n / (area * fcd);
            var limit = share > 0 ? 10.78 / Math.Sqrt(share) : double.MaxValue;
            if (slenderness <= limit) return first;
            var creep = 1 + Math.Max(0, 0.35 + fck / 200 - slenderness / 150) * 2;
            var curvature = creep * fyd / 200000 / (0.45 * effective);
            return first + n * curvature * length * length / 10;
        }

        var (my, mz) = (Designed(strong, momentY, lengthStrong), Designed(weak, momentZ, lengthWeak));
        double Unity(double steel)
        {
            var (rdY, rdZ) = (MomentEurocode(strong, steel, n, fck), MomentEurocode(weak, steel, n, fck));
            if (rdY <= 0 || rdZ <= 0) return 2;
            var ratio = n / (area * fcd + steel * fyd);
            var a = ratio <= 0.1 ? 1 : ratio <= 0.7 ? 1 + (ratio - 0.1) / 0.6 * 0.5 : ratio <= 1 ? 1.5 + (ratio - 0.7) / 0.3 * 0.5 : 2;
            return Math.Pow(my / rdY, a) + Math.Pow(mz / rdZ, a);
        }

        var (least, most) = (Math.Max(0.10 * n / fyd, 0.002 * area), 0.04 * area);
        return Solved(Unity, least, most);
    }

    /// <summary>
    /// A column by ACI 318-19: the least moment of 6.6.4.5.4, moments magnified by 6.6.4.5.2 where it is
    /// slender (6.2.5, taken as bent in single curvature, kl/r over 22) with EI as 0.4 Ec Ig / (1 + 0.6),
    /// the two ways together taken straight-line. Between 1% and 8% steel (10.6.1.1).
    /// </summary>
    public static (double Unity, double Steel) ColumnAci(ColumnSection strong, ColumnSection weak, double compression, double momentY, double momentZ, double lengthStrong, double lengthWeak, double fc)
    {
        var (pu, area) = (Math.Max(0, compression) * 1e3, strong.Area);
        var ec = 57000 * Math.Sqrt(fc / Psi) * Psi;

        double Designed(ColumnSection s, double moment, double length)
        {
            var least = pu * (15.24 + 0.03 * s.Depth);
            var demand = Math.Max(moment * 1e6, least);
            if (length / s.Radius <= 22) return demand;
            var second = s.Round ? Math.PI * Math.Pow(s.Depth, 4) / 64 : s.Width * Math.Pow(s.Depth, 3) / 12;
            var critical = Math.PI * Math.PI * (0.4 * ec * second / 1.6) / (length * length);
            return pu >= 0.75 * critical ? double.PositiveInfinity : demand / (1 - pu / (0.75 * critical));
        }

        var (mx, my) = (Designed(strong, momentY, lengthStrong), Designed(weak, momentZ, lengthWeak));
        double Unity(double steel)
        {
            var (rdX, rdY) = (MomentAci(strong, steel, pu, fc), MomentAci(weak, steel, pu, fc));
            return rdX <= 0 || rdY <= 0 ? 2 : mx / rdX + my / rdY;
        }

        return Solved(Unity, 0.01 * area, 0.08 * area);
    }

    /// <summary>The interaction at the most steel, and the least steel that brings it to 1 - bisected; the most if nothing does.</summary>
    private static (double Unity, double Steel) Solved(Func<double, double> unity, double least, double most)
    {
        var atMost = unity(most);
        if (unity(least) <= 1) return (atMost, least);
        if (atMost > 1) return (atMost, most);
        var (low, high) = (least, most);
        for (var i = 0; i < 18; i++)
        {
            var middle = (low + high) / 2;
            if (unity(middle) <= 1) high = middle;
            else low = middle;
        }

        return (atMost, high);
    }

    // ---- a member's checks --------------------------------------------------------------

    /// <summary>A concrete member's checks over the stations along it.</summary>
    public static (string Grade, string By, IReadOnlyList<StrengthCheck> Checks, string Limits) Checks(
        BimDocument document, StructuralAnalysis analysis, AnalyticalMember member, bool us, MemberLengths lengths,
        IReadOnlyList<(LoadCombination Combination, MemberForces Forces)> stations)
    {
        var by = us ? "ACI 318-19" : "BS EN 1992-1-1 and its UK National Annex";
        var quality = member.Section.Material.Quality;
        if (member.Element is GroundBeam beam) return OnPiles(beam, us, by, stations);
        var d = member.Section.Dimensions;
        var fck = StrengthOf(quality);
        if (member.Section.Shape is not ("Rectangle" or "Circle")) return (quality, by, Array.Empty<StrengthCheck>(), $"Not checked: concrete is worked out here as a rectangle or a circle, not a {member.Section.Shape}.");
        if (member.Kind == AnalyticalMemberKind.Column) return (quality, by, Column(member, us, lengths, stations, fck), "No bars are in the model: the steel said is what the section needs. Its bars' spacing and laps are not worked out.");
        if (member.Section.Shape != "Rectangle") return (quality, by, Array.Empty<StrengthCheck>(), "Not checked: a concrete beam is worked out here as a rectangle.");

        var (depth, width) = (d[0], d[1]);
        var effective = depth - 30 - 10 - 10;
        var checks = MemberStrength.Worst(stations.Select(station => (station.Combination.Name, Beam(width, effective, fck, us, null, station.Forces))));
        return (quality, by, checks, "No bars are in the model: the steel said is what the section needs, each face at its worst point. Axial force and torsion are left out, and so are crack widths and detailing.");
    }

    private static IEnumerable<StrengthCheck> Beam(double width, double depth, double fck, bool us, double? links, MemberForces f)
    {
        var moment = Math.Abs(f.My);
        var least = us ? LeastAci(width, depth, fck) : LeastEurocode(width, depth, fck);
        var (needed, problem) = us ? Aci(moment, width, depth, fck, least) : Eurocode(moment, width, depth, fck, least);
        var most = us
            ? 0.9 * 0.85 * fck * width * 0.375 * Beta1(fck) * depth * (depth - 0.375 * Beta1(fck) * depth / 2) / 1e6
            : 0.167 * width * depth * depth * fck / 1e6;
        var face = f.TopInTension ? "top" : "bottom";
        yield return new StrengthCheck(f.TopInTension ? "Bending, hogging" : "Bending, sagging", moment, most, "kNm", us ? "22.2, 21.2.2" : "6.1",
            problem ?? $"needs {needed:0} mm² in its {face}");
        var shear = Math.Abs(f.Vz);
        var (crushing, stirrups, carried) = us ? ShearAci(shear, width, depth, fck, links) : ShearEurocode(shear, width, depth, fck, links);
        yield return new StrengthCheck("Shear", shear, carried ?? crushing, "kN", us ? "22.5" : "6.2.3", $"needs links of {stirrups:0} mm²/m");
    }

    private static IReadOnlyList<StrengthCheck> Column(AnalyticalMember member, bool us, MemberLengths lengths, IReadOnlyList<(LoadCombination Combination, MemberForces Forces)> stations, double fck)
    {
        var round = member.Section.Shape == "Circle";
        var d = member.Section.Dimensions;
        var (depth, width) = round ? (d[0], d[0]) : (d[0], d[1]);
        const double inset = 30 + 10 + 10;
        var (strong, weak) = (new ColumnSection(depth, width, round, inset), new ColumnSection(width, depth, round, inset));

        // Three cases cover the rest: the greatest compression, and the greatest moment each way.
        var cases = stations.Where(station => station.Forces.N < 0).ToList();
        if (cases.Count == 0) cases = stations.ToList();
        var governing = new[]
        {
            cases.MaxBy(station => -station.Forces.N), cases.MaxBy(station => Math.Abs(station.Forces.My)), cases.MaxBy(station => Math.Abs(station.Forces.Mz))
        }.Distinct();

        var checks = new List<(string, IEnumerable<StrengthCheck>)>();
        foreach (var (combination, f) in governing)
        {
            var compression = Math.Max(0, -f.N);
            var (unity, steel) = us
                ? ColumnAci(strong, weak, compression, Math.Abs(f.My), Math.Abs(f.Mz), lengths.Strong, lengths.Weak, fck)
                : ColumnEurocode(strong, weak, compression, Math.Abs(f.My), Math.Abs(f.Mz), lengths.Strong, lengths.Weak, fck);
            var share = steel / strong.Area;
            var shear = Math.Max(Math.Abs(f.Vz), Math.Abs(f.Vy));
            var (crushing, stirrups, _) = us ? ShearAci(shear, width, depth - inset, fck, null) : ShearEurocode(shear, width, depth - inset, fck, null);
            checks.Add((combination.Name, new[]
            {
                new StrengthCheck("Axial and bending, at the most steel", unity, 1, "", us ? "22.4, 6.6.4, 10.6.1.1" : "5.8, 6.1, 9.5.2",
                    unity > 1 ? "it cannot be reinforced enough: make it bigger" : $"needs {steel:0} mm², {share:P1} of its section"),
                new StrengthCheck("Shear", shear, crushing, "kN", us ? "22.5" : "6.2.3", $"needs links of {stirrups:0} mm²/m")
            }));
        }

        return MemberStrength.Worst(checks);
    }

    /// <summary>A ground beam against the bars and links it has, at the concrete its foundations are made of.</summary>
    private static (string, string, IReadOnlyList<StrengthCheck>, string) OnPiles(GroundBeam beam, bool us, string by, IReadOnlyList<(LoadCombination Combination, MemberForces Forces)> stations)
    {
        var fck = us ? 3000 * Psi : 28;
        var quality = us ? "3000 psi" : "RC28/35";
        var (top, bottom, links) = (BarSpec.Parse(beam.TopBars), BarSpec.Parse(beam.BottomBars), BarSpec.Parse(beam.Links));
        var linkSize = links?.Diameter ?? 0;
        double Depth(BarSpec? bars) => beam.Depth - Reinforcement.CoverAgainstGround - linkSize - (bars?.Diameter ?? 16) / 2;
        double Given(BarSpec? bars) => bars is { Count: { } count } ? count * bars.BarArea : 0;
        double? perMetre = links is { Spacing: { } spacing } ? 2 * links.BarArea / spacing * 1000 : null;

        IEnumerable<StrengthCheck> At(MemberForces f)
        {
            var moment = Math.Abs(f.My);
            var bars = f.TopInTension ? top : bottom;
            var (effective, face) = (Depth(bars), f.TopInTension ? "Top bars" : "Bottom bars");
            var least = StructuralAnalysis.Least(us, beam.Width, effective);
            var (needed, problem) = us ? Aci(moment, beam.Width, effective, fck, least) : Eurocode(moment, beam.Width, effective, fck, least);
            yield return new StrengthCheck(face, needed, Given(bars), "mm²", us ? "22.2, 9.6.1.2" : "6.1, 9.2.1.1", problem ?? $"{moment:0.0} kNm");
            var shear = Math.Abs(f.Vz);
            var (crushing, stirrups, carried) = us ? ShearAci(shear, beam.Width, effective, fck, perMetre) : ShearEurocode(shear, beam.Width, effective, fck, perMetre);
            yield return new StrengthCheck("Shear", shear, Math.Min(crushing, carried ?? 0), "kN", us ? "22.5" : "6.2.3", $"needs links of {stirrups:0} mm²/m");

            // Cracks, where there is more than one bar to set a spacing: the steel's stress in service taken as 0.65 of the ultimate.
            if (bars is { Count: > 1 and var count })
            {
                var clear = Reinforcement.CoverAgainstGround + linkSize;
                var spacing = (beam.Width - 2 * clear - bars.Diameter) / (count - 1);
                var stress = (us ? 413.7 : 500 / 1.15) * 0.65 * Math.Min(1, needed / Math.Max(1, Given(bars)));
                yield return new StrengthCheck($"Cracks, {(f.TopInTension ? "top" : "bottom")}", PlateDesign.CrackShare(us, stress, bars.Diameter, spacing, clear), 1, "", us ? "24.3.2" : "7.3.3", $"σs {stress:0} N/mm²");
            }
        }

        return (quality, by, MemberStrength.Worst(stations.Select(station => (station.Combination.Name, At(station.Forces)))), "The bars' anchorage at the piles is not checked.");
    }
}
