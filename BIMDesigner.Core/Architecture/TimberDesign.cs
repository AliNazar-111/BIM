namespace BIMDesigner.Core.Architecture;

/// <summary>A strength class of timber, N/mm²: bending, tension and compression along the grain, shear, its mean and 5th percentile moduli, and its partial factor.</summary>
public sealed record TimberGrade(string Name, double Fm, double Ft0, double Fc0, double Fv, double E0Mean, double E05, double GammaM, bool Glulam);

/// <summary>
/// Timber members checked at a point along them, a rectangle of a depth and width. By BS EN 1995-1-1
/// with its UK National Annex: bending with the depth factor, load sharing for joists and lateral
/// torsional buckling, shear with kcr 0.67, tension, compression with buckling, and those together.
/// C24 by BS EN 338:2016 Table 1 and GL24h by BS EN 14080; γM 1.3 and 1.25; kmod for service classes 1
/// and 2 by the shortest load in the combination. By the NDS in its LRFD format: Douglas fir-larch No. 2
/// (2018 Supplement Table 4A) with its size factors and the repetitive member factor for joists, beam
/// and column stability, and 24F-1.8E glulam for bending and shear.
/// </summary>
public static class TimberDesign
{
    public static readonly TimberGrade C24 = new("C24", 24, 14.5, 21, 4.0, 11000, 7400, 1.3, false);

    /// <summary>C16 of BS EN 338:2016.</summary>
    public static readonly TimberGrade C16 = new("C16", 16, 8.5, 17, 3.2, 8000, 5400, 1.3, false);

    public static readonly TimberGrade Gl24h = new("GL24h", 24, 19.2, 24, 3.5, 11500, 9600, 1.25, true);

    /// <summary>
    /// kmod for a combination, by the shortest load in it, BS EN 1995-1-1 Table 3.1 for solid timber and
    /// glulam in service class 1 or 2: 0.6 for its own weight, 0.8 for imposed floor loads, 0.9 for snow
    /// and roof maintenance, 1.1 for the wind.
    /// </summary>
    public static double Kmod(LoadCombination combination) =>
        combination.Terms.Where(term => term.Factor > 0).Select(term => term.Case.Nature switch
        {
            LoadNature.Wind => 1.1,
            LoadNature.Snow or LoadNature.RoofImposed => 0.9,
            LoadNature.Imposed => 0.8,
            _ => 0.6
        }).DefaultIfEmpty(0.6).Max();

    /// <summary>The depth factor kh for bending and tension over a depth: to 150 mm for solid timber, to 600 for glulam (3.2, 3.3).</summary>
    public static double DepthFactor(TimberGrade grade, double depth) => grade.Glulam
        ? depth < 600 ? Math.Min(Math.Pow(600 / depth, 0.1), 1.1) : 1
        : depth < 150 ? Math.Min(Math.Pow(150 / depth, 0.2), 1.3) : 1;

    /// <summary>kcrit, 6.3.3, for a depth, width and effective length: 1 until its relative slenderness for bending is 0.75.</summary>
    public static double Kcrit(TimberGrade grade, double depth, double width, double effective)
    {
        if (effective <= 0) return 1;
        var critical = 0.78 * width * width / (depth * effective) * grade.E05;
        var slenderness = Math.Sqrt(grade.Fm / critical);
        return slenderness <= 0.75 ? 1 : slenderness <= 1.4 ? 1.56 - 0.75 * slenderness : 1 / (slenderness * slenderness);
    }

    /// <summary>kc, 6.3.2, for a slenderness, length over radius of gyration.</summary>
    public static double Kc(TimberGrade grade, double slenderness)
    {
        var relative = slenderness / Math.PI * Math.Sqrt(grade.Fc0 / grade.E05);
        if (relative <= 0.3) return 1;
        var k = 0.5 * (1 + (grade.Glulam ? 0.1 : 0.2) * (relative - 0.3) + relative * relative);
        return Math.Min(1, 1 / (k + Math.Sqrt(k * k - relative * relative)));
    }

    /// <summary>What a timber rectangle has to carry at a point against what it can, by BS EN 1995-1-1.</summary>
    public static IEnumerable<StrengthCheck> Eurocode(TimberGrade grade, double depth, double width, bool joist, MemberLengths lengths, MemberForces f, double kmod)
    {
        const double km = 0.7, kcr = 0.67;
        var share = joist ? 1.1 : 1.0;
        double Design(double characteristic) => kmod * characteristic / grade.GammaM;
        var area = depth * width;
        var (sy, sz) = (width * depth * depth / 6, depth * width * width / 6);
        var (sigmaY, sigmaZ) = (Math.Abs(f.My) * 1e6 / sy, Math.Abs(f.Mz) * 1e6 / sz);
        var (fmy, fmz) = (Design(grade.Fm) * share * DepthFactor(grade, depth), Design(grade.Fm) * share * DepthFactor(grade, width));
        var fv = Design(grade.Fv);

        // Lateral buckling: 0.9 of the length held, 2 depths more for a load on its top (Table 6.1).
        var lateral = f.TopInTension ? lengths.LateralHogging : lengths.Lateral;
        var kcrit = lateral > 0 ? Kcrit(grade, depth, width, 0.9 * lateral + 2 * depth) : 1;

        yield return new StrengthCheck("Bending, strong axis", sigmaY, kcrit * fmy, "N/mm²", "6.1.6, 6.3.3", $"kmod {kmod:0.0}, kcrit {kcrit:0.00}");
        yield return new StrengthCheck("Bending, weak axis", sigmaZ, fmz, "N/mm²", "6.1.6");
        yield return new StrengthCheck("Bending both ways", Math.Max(sigmaY / fmy + km * sigmaZ / fmz, km * sigmaY / fmy + sigmaZ / fmz), 1, "", "6.1.6 (6.11), (6.12)");
        yield return new StrengthCheck("Shear along the depth", 1.5 * Math.Abs(f.Vz) * 1e3 / (kcr * area), fv, "N/mm²", "6.1.7");
        yield return new StrengthCheck("Shear across", 1.5 * Math.Abs(f.Vy) * 1e3 / (kcr * area), fv, "N/mm²", "6.1.7");

        var (tension, compression) = (Math.Max(0, f.N) * 1e3 / area, Math.Max(0, -f.N) * 1e3 / area);
        if (tension > 0)
        {
            var ft = Design(grade.Ft0) * DepthFactor(grade, depth);
            yield return new StrengthCheck("Tension", tension, ft, "N/mm²", "6.1.2");
            yield return new StrengthCheck("Tension with bending", tension / ft + Math.Max(sigmaY / fmy + km * sigmaZ / fmz, km * sigmaY / fmy + sigmaZ / fmz), 1, "", "6.2.3");
        }

        if (compression <= 0) yield break;
        var fc = Design(grade.Fc0);
        double Slenderness(double length, double size) => length / (size / Math.Sqrt(12));
        var (kcY, kcZ) = (Kc(grade, Slenderness(lengths.Strong, depth)), Kc(grade, Slenderness(lengths.Weak, width)));
        yield return new StrengthCheck("Compression with buckling", compression, Math.Min(kcY, kcZ) * fc, "N/mm²", "6.3.2", $"kc,y {kcY:0.00}, kc,z {kcZ:0.00}");
        yield return new StrengthCheck("Compression with bending", Math.Pow(compression / fc, 2) + Math.Max(sigmaY / fmy + km * sigmaZ / fmz, km * sigmaY / fmy + sigmaZ / fmz), 1, "", "6.2.4");
        yield return new StrengthCheck("Buckling with bending",
            Math.Max(compression / (kcY * fc) + sigmaY / fmy + km * sigmaZ / fmz, compression / (kcZ * fc) + km * sigmaY / fmy + sigmaZ / fmz), 1, "", "6.3.2 (6.23), (6.24)");
        if (kcrit < 1)
            yield return new StrengthCheck("Lateral buckling with compression", Math.Pow(sigmaY / (kcrit * fmy), 2) + compression / (kcZ * fc), 1, "", "6.3.3 (6.35)");
    }

    // ---- the NDS ------------------------------------------------------------------------

    private const double Psi = 0.00689476;

    /// <summary>
    /// The time effect factor λ of NDS Table N3 for a combination of ASCE 7: 1.0 with the wind in it,
    /// 0.6 for its own weight alone, 0.8 otherwise, an occupancy live load or snow.
    /// </summary>
    public static double TimeEffect(LoadCombination combination)
    {
        var natures = combination.Terms.Where(term => term.Factor > 0).Select(term => term.Case.Nature).ToList();
        if (natures.Contains(LoadNature.Wind)) return 1.0;
        return natures.Any(nature => nature is LoadNature.Imposed or LoadNature.RoofImposed or LoadNature.Snow) ? 0.8 : 0.6;
    }

    /// <summary>The size factors of Table 4A for No. 2 dimension lumber: Fb, Ft and Fc, by its actual depth and width, mm.</summary>
    public static (double Bending, double Tension, double Compression) SizeFactors(double depth, double width)
    {
        var inches = depth / 25.4;
        var four = width / 25.4 > 2.5;
        return inches switch
        {
            <= 3.6 => (1.5, 1.5, 1.15),
            <= 4.6 => (1.4, 1.4, 1.1),
            <= 5.6 => (1.3, 1.3, 1.1),
            <= 7.3 => (four ? 1.3 : 1.2, 1.2, 1.05),
            <= 9.3 => (four ? 1.2 : 1.1, 1.1, 1.0),
            <= 11.3 => (four ? 1.1 : 1.0, 1.0, 1.0),
            _ => (four ? 1.0 : 0.9, 0.9, 0.9)
        };
    }

    /// <summary>The effective length of a beam for its stability, NDS Table 3.3.3 for any load: by its unbraced length over its depth.</summary>
    public static double EffectiveBeamLength(double unbraced, double depth)
    {
        var ratio = unbraced / depth;
        return ratio < 7 ? 2.06 * unbraced : ratio <= 14.3 ? 1.63 * unbraced + 3 * depth : 1.84 * unbraced;
    }

    /// <summary>The beam stability factor CL, NDS 3.3.3, or the column stability factor CP, 3.7.1 - both the same curve at their own ratio and c.</summary>
    public static double Stability(double ratio, double c)
    {
        var a = (1 + ratio) / (2 * c);
        return a - Math.Sqrt(a * a - ratio / c);
    }

    /// <summary>What a timber rectangle has to carry at a point against what it can, by the NDS in its LRFD format.</summary>
    public static IEnumerable<StrengthCheck> Nds(double depth, double width, bool glulam, bool joist, bool column, MemberLengths lengths, double length, MemberForces f, double lambda)
    {
        const double kb = 2.54 * 0.85, kt = 2.70 * 0.80, kv = 2.88 * 0.75, kc = 2.40 * 0.90, ke = 1.76 * 0.85;
        var (d, b) = (depth / 25.4, width / 25.4);
        var (cb, ct, cc) = glulam ? (1.0, 1.0, 1.0) : SizeFactors(depth, width);
        var (fb, ft, fv, fcBase, emin) = glulam ? (2400.0, 0.0, 265.0, 0.0, 950000.0) : (900.0, 575.0, 180.0, 1350.0, 580000.0);
        var repetitive = joist && !glulam ? 1.15 : 1.0;
        var fbStar = fb * cb * repetitive * kb * lambda;
        var eminPrime = emin * ke;
        var volume = glulam ? Math.Min(1, Math.Pow(21 / (length / 304.8) * 12 / d * 5.125 / b, 0.1)) : 1;

        var (sx, sy) = (b * d * d / 6, d * b * b / 6);
        var (fbx, fby) = (Math.Abs(f.My) * 1e6 / Psi / (25.4 * 25.4 * 25.4) / sx, Math.Abs(f.Mz) * 1e6 / Psi / (25.4 * 25.4 * 25.4) / sy);
        var (compression, tension) = (Math.Max(0, -f.N) * 1e3 / Psi / (depth * width), Math.Max(0, f.N) * 1e3 / Psi / (depth * width));

        // Beam stability.
        var lateral = f.TopInTension ? lengths.LateralHogging : lengths.Lateral;
        var (cl, fbe) = (1.0, double.MaxValue);
        if (lateral > 0)
        {
            var rb = Math.Sqrt(EffectiveBeamLength(lateral, depth) * depth / (width * width));
            fbe = 1.20 * eminPrime / (rb * rb);
            cl = Stability(fbe / fbStar, 0.95);
        }

        var fbPrime = fbStar * (glulam ? Math.Min(cl, volume) : cl);
        var fbWeak = fbStar * volume;
        yield return new StrengthCheck("Bending, strong axis", fbx, fbPrime, "psi", "3.3", $"λ {lambda:0.0}, CL {cl:0.00}");
        yield return new StrengthCheck("Bending, weak axis", fby, fbWeak, "psi", "3.3");
        yield return new StrengthCheck("Shear along the depth", 1.5 * Math.Abs(f.Vz) * 1e3 / Psi / (depth * width), fv * kv * lambda, "psi", "3.4");
        yield return new StrengthCheck("Shear across", 1.5 * Math.Abs(f.Vy) * 1e3 / Psi / (depth * width), fv * kv * lambda, "psi", "3.4");
        if (glulam) yield break;

        if (tension > 0)
        {
            var ftPrime = ft * ct * kt * lambda;
            yield return new StrengthCheck("Tension", tension, ftPrime, "psi", "3.8");
            yield return new StrengthCheck("Tension with bending", tension / ftPrime + fbx / fbStar + fby / fbWeak, 1, "", "3.9.1");
        }

        if (compression <= 0)
        {
            yield return new StrengthCheck("Bending both ways", fbx / fbPrime + fby / fbWeak, 1, "", "3.9.2");
            yield break;
        }

        // Column stability, sawn lumber's c 0.8: the more slender of its two ways.
        var fcStar = fcBase * cc * kc * lambda;
        var (ratioStrong, ratioWeak) = (lengths.Strong / depth, lengths.Weak / width);
        var (fcE1, fcE2) = (0.822 * eminPrime / (ratioStrong * ratioStrong), 0.822 * eminPrime / (ratioWeak * ratioWeak));
        var fcPrime = fcStar * Stability(Math.Min(fcE1, fcE2) / fcStar, 0.8);
        yield return new StrengthCheck("Compression with buckling", compression, fcPrime, "psi", "3.7", $"le/d {Math.Max(ratioStrong, ratioWeak):0.0}");
        if (column) yield return new StrengthCheck("Slenderness", Math.Max(ratioStrong, ratioWeak), 50, "le/d", "3.7.1.4");

        // 3.9-3: amplified moments, past what it can bear once the axial stress reaches the Euler stress.
        var first = compression >= fcE1 ? double.PositiveInfinity : fbx / (fbPrime * (1 - compression / fcE1));
        var left = 1 - compression / fcE2 - Math.Pow(fbx / fbe, 2);
        var second = fby <= 0 ? 0 : left <= 0 ? double.PositiveInfinity : fby / (fbWeak * left);
        yield return new StrengthCheck("Compression with bending", Math.Pow(compression / fcPrime, 2) + first + second, 1, "", "3.9.2 (3.9-3)");
    }
}
