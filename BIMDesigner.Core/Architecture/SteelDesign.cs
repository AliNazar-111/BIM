namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A steel section as design takes it, mm: its overall sizes, area and second moments, its elastic and
/// plastic moduli and torsion and warping constants about its strong axis (y) and its weak (z). The
/// types hold no root radius, and a rolled I's matters - to its class as well as its moduli - so it
/// is taken as 1.25 times the web, which brings UK and US sections within about 1% of their published
/// figures; a hollow rectangle has the corners of BS EN 10210-2.
/// </summary>
public sealed record SteelSection(
    string Shape, double H, double B, double Tf, double Tw, double Area, double Iy, double Iz,
    double Wely, double Welz, double Wply, double Wplz, double It, double Iw, double Root = 0)
{
    public bool IsI => Shape == "I Section";

    public bool IsTube => Shape == "Tube";

    public bool IsPipe => Shape == "Pipe";

    public bool IsSolid => Shape is "Rectangle" or "Circle";

    public double Thickest => Math.Max(Tf, Tw);

    public double Ry => Math.Sqrt(Iy / Area);

    public double Rz => Math.Sqrt(Iz / Area);

    /// <summary>A section of the analytical model as steel, or null for a shape not worked out.</summary>
    public static SteelSection? Of(AnalyticalSection section)
    {
        var d = section.Dimensions;
        var it = StructuralAnalysis.TorsionConstant(section);
        switch (section.Shape)
        {
            case "I Section" when d.Count >= 6:
            {
                var (h, b, tf, tw) = (d[0], d[1], d[3], d[5]);
                var hw = h - 2 * tf;
                var root = 1.25 * tw;

                // Its four fillets, each a spandrel at its own centroid.
                var fillets = (4 - Math.PI) * root * root;
                var (offY, offZ) = (h / 2 - tf - 0.2234 * root, tw / 2 + 0.2234 * root);
                var (iy, iz) = (section.Iy + fillets * offY * offY, section.Iz + fillets * offZ * offZ);
                return new SteelSection("I Section", h, b, tf, tw, section.Area + fillets, iy, iz, iy / (h / 2), iz / (b / 2),
                    b * tf * (h - tf) + tw * hw * hw / 4 + fillets * offY, tf * b * b / 2 + hw * tw * tw / 4 + fillets * offZ, it, iz * Math.Pow(h - tf, 2) / 4, root);
            }

            case "Tube" when d.Count >= 3:
                return Tube(d[0], d[1], d[2], it);

            case "Pipe" when d.Count >= 2:
            {
                var (diameter, wall) = (d[0], d[1]);
                var plastic = (Math.Pow(diameter, 3) - Math.Pow(diameter - 2 * wall, 3)) / 6;
                return new SteelSection("Pipe", diameter, diameter, wall, wall, section.Area, section.Iy, section.Iz, section.Iy / (diameter / 2), section.Iz / (diameter / 2), plastic, plastic, it, 0);
            }

            case "Circle" when d.Count >= 1:
            {
                var diameter = d[0];
                var plastic = Math.Pow(diameter, 3) / 6;
                return new SteelSection("Circle", diameter, diameter, diameter, diameter, section.Area, section.Iy, section.Iz, section.Iy / (diameter / 2), section.Iz / (diameter / 2), plastic, plastic, it, 0);
            }

            case "Rectangle" when d.Count >= 2:
            {
                var (h, b) = (d[0], d[1]);
                return new SteelSection("Rectangle", h, b, h, b, section.Area, section.Iy, section.Iz, b * h * h / 6, h * b * b / 6, b * h * h / 4, h * b * b / 4, it, 0);
            }

            default:
                return null;
        }
    }

    /// <summary>A hollow rectangle: square-cornered, less what its corners take off - 1.5 times its wall outside, once inside, each a spandrel at its own centroid.</summary>
    private static SteelSection Tube(double h, double b, double t, double it)
    {
        const double spandrel = 1 - Math.PI / 4, centroid = (10 - 3 * Math.PI) / (12 - 3 * Math.PI);
        var (outer, inner) = (1.5 * t, t);
        var (ao, ai) = (spandrel * outer * outer, spandrel * inner * inner);
        double Off(double size) => size / 2 - centroid * outer;
        double In(double size) => size / 2 - t - centroid * inner;
        var (ih, ib) = (h - 2 * t, b - 2 * t);
        var area = b * h - ib * ih - 4 * ao + 4 * ai;
        var iy = (b * Math.Pow(h, 3) - ib * Math.Pow(ih, 3)) / 12 - 4 * ao * Off(h) * Off(h) + 4 * ai * In(h) * In(h);
        var iz = (h * Math.Pow(b, 3) - ih * Math.Pow(ib, 3)) / 12 - 4 * ao * Off(b) * Off(b) + 4 * ai * In(b) * In(b);
        var wply = (b * h * h - ib * ih * ih) / 4 - 4 * ao * Off(h) + 4 * ai * In(h);
        var wplz = (h * b * b - ih * ib * ib) / 4 - 4 * ao * Off(b) + 4 * ai * In(b);
        return new SteelSection("Tube", h, b, t, t, area, iy, iz, iy / (h / 2), iz / (b / 2), wply, wplz, it, 0);
    }
}

/// <summary>
/// Steel members checked at a point along them. By BS EN 1993-1-1 with its UK National Annex (γM0 and
/// γM1 1.0): the section's class, its resistances to axial force, bending and shear, the two together,
/// flexural and torsional buckling, lateral torsional buckling as rolled sections have it (λLT,0 0.4,
/// β 0.75, C1 1.0, on the safe side) and the interaction of 6.3.3 with Annex B's factors and every Cm
/// 1.0, also on the safe side. S355, BS EN 10025-2's yield strength for the thickest plate. By AISC 360-16,
/// LRFD: chapters D to H, A992, Fy 50 ksi.
/// </summary>
public static class SteelDesign
{
    private const double GammaM0 = 1.0, GammaM1 = 1.0;
    private const double E = 210000, G = 81000;

    /// <summary>S355's yield strength for a thickness, N/mm²: BS EN 10025-2 Table 7.</summary>
    public static double YieldStrength(double thickness) => thickness switch
    {
        <= 16 => 355,
        <= 40 => 345,
        <= 63 => 335,
        <= 80 => 325,
        <= 100 => 315,
        _ => 295
    };

    /// <summary>The buckling reduction factor χ of 6.3.1.2 for a curve's imperfection factor and a non-dimensional slenderness.</summary>
    public static double Chi(double alpha, double slenderness)
    {
        if (slenderness <= 0.2) return 1;
        var phi = 0.5 * (1 + alpha * (slenderness - 0.2) + slenderness * slenderness);
        return Math.Min(1, 1 / (phi + Math.Sqrt(phi * phi - slenderness * slenderness)));
    }

    /// <summary>Rolled sections' lateral torsional buckling reduction χLT by 6.3.2.3, the UK National Annex's λLT,0 0.4 and β 0.75.</summary>
    public static double ChiLt(double alpha, double slenderness)
    {
        const double start = 0.4, beta = 0.75;
        if (slenderness <= start) return 1;
        var phi = 0.5 * (1 + alpha * (slenderness - start) + beta * slenderness * slenderness);
        return Math.Min(Math.Min(1, 1 / (slenderness * slenderness)), 1 / (phi + Math.Sqrt(phi * phi - beta * slenderness * slenderness)));
    }

    /// <summary>Table 6.2's imperfection factors for buckling about the strong axis and the weak, by the section.</summary>
    private static (double Strong, double Weak) Curves(SteelSection s)
    {
        const double a = 0.21, b = 0.34, c = 0.49, d = 0.76;
        if (s.IsTube || s.IsPipe) return (a, a);
        if (!s.IsI) return (c, c);
        if (s.H / s.B > 1.2) return s.Tf <= 40 ? (a, b) : (b, c);
        return s.Tf <= 100 ? (b, c) : (d, d);
    }

    /// <summary>The class of a section under a compression and a moment about its strong axis, N and N mm, by Table 5.2.</summary>
    public static int ClassOf(SteelSection s, double fy, double compression, double moment)
    {
        var e = Math.Sqrt(235 / fy);
        if (s.IsSolid) return 1;
        if (s.IsPipe)
        {
            var slenderness = s.H / s.Tw;
            return slenderness <= 50 * e * e ? 1 : slenderness <= 70 * e * e ? 2 : slenderness <= 90 * e * e ? 3 : 4;
        }

        int Internal(double c, double t)
        {
            var ct = c / t;
            double one, two, three;
            if (compression <= 1)
                (one, two, three) = (72 * e, 83 * e, 124 * e);
            else
            {
                var alpha = Math.Min(1, 0.5 * (1 + compression / (fy * 2 * t * c / (s.IsTube ? 1 : 2))));
                (one, two) = alpha > 0.5 ? (396 * e / (13 * alpha - 1), 456 * e / (13 * alpha - 1)) : (36 * e / alpha, 41.5 * e / alpha);
                var (axial, bending) = (compression / s.Area, moment / s.Wely);
                var psi = axial + bending <= 0 ? 1 : (axial - bending) / (axial + bending);
                three = psi > -1 ? 42 * e / (0.67 + 0.33 * psi) : 62 * e * (1 - psi) * Math.Sqrt(-psi);
            }

            return ct <= one ? 1 : ct <= two ? 2 : ct <= three ? 3 : 4;
        }

        if (s.IsTube)
        {
            // Flange: wholly in compression under bending or axial force.
            var flange = (s.B - 3 * s.Tf) / s.Tf;
            var f = flange <= 33 * e ? 1 : flange <= 38 * e ? 2 : flange <= 42 * e ? 3 : 4;
            return Math.Max(f, Internal(s.H - 3 * s.Tw, s.Tw));
        }

        var outstand = (s.B - s.Tw - 2 * s.Root) / 2 / s.Tf;
        var flanges = outstand <= 9 * e ? 1 : outstand <= 10 * e ? 2 : outstand <= 14 * e ? 3 : 4;
        return Math.Max(flanges, Internal(s.H - 2 * s.Tf - 2 * s.Root, s.Tw));
    }

    /// <summary>A class 4 section's effective area in uniform compression, by BS EN 1993-1-5 4.4: each slender plate down to ρ of its width.</summary>
    public static double EffectiveArea(SteelSection s, double fy)
    {
        var e = Math.Sqrt(235 / fy);
        double Internal(double c, double t)
        {
            var slenderness = c / t / (56.8 * e);
            return slenderness <= 0.673 ? 1 : Math.Min(1, (slenderness - 0.22) / (slenderness * slenderness));
        }

        double Outstand(double c, double t)
        {
            var slenderness = c / t / (18.6 * e);
            return slenderness <= 0.748 ? 1 : Math.Min(1, (slenderness - 0.188) / (slenderness * slenderness));
        }

        if (s.IsTube)
        {
            var (ch, cb) = (s.H - 3 * s.Tw, s.B - 3 * s.Tf);
            return s.Area - 2 * (1 - Internal(ch, s.Tw)) * ch * s.Tw - 2 * (1 - Internal(cb, s.Tf)) * cb * s.Tf;
        }

        if (!s.IsI) return s.Area;
        var (web, outstand) = (s.H - 2 * s.Tf - 2 * s.Root, (s.B - s.Tw - 2 * s.Root) / 2);
        return s.Area - (1 - Internal(web, s.Tw)) * web * s.Tw - 4 * (1 - Outstand(outstand, s.Tf)) * outstand * s.Tf;
    }

    /// <summary>The elastic critical moment of an I or a solid rectangle over a length, N mm, C1 1.0: its moment constant over the length, on the safe side.</summary>
    public static double CriticalMoment(SteelSection s, double length)
    {
        var euler = Math.PI * Math.PI * E * s.Iz / (length * length);
        return euler * Math.Sqrt(s.Iw / s.Iz + length * length * G * s.It / (Math.PI * Math.PI * E * s.Iz));
    }

    /// <summary>What a section has to carry at a point against what it can, by BS EN 1993-1-1.</summary>
    public static IEnumerable<StrengthCheck> Eurocode(SteelSection s, MemberLengths lengths, MemberForces f)
    {
        var fy = YieldStrength(s.Thickest);
        var (compression, tension) = (Math.Max(0, -f.N) * 1e3, Math.Max(0, f.N) * 1e3);
        var (my, mz) = (Math.Abs(f.My) * 1e6, Math.Abs(f.Mz) * 1e6);
        var (vz, vy) = (Math.Abs(f.Vz) * 1e3, Math.Abs(f.Vy) * 1e3);
        var type = ClassOf(s, fy, compression, my);
        var area = type == 4 ? EffectiveArea(s, fy) : s.Area;
        var (wy, wz) = type <= 2 ? (s.Wply, s.Wplz) : (s.Wely, s.Welz);

        // Resistances of the section.
        var nRk = area * fy;
        var axial = compression > 0 ? nRk / GammaM0 : s.Area * fy / GammaM0;
        double Shear(double area2) => area2 * fy / Math.Sqrt(3) / GammaM0;
        var (avz, avy) = s.IsI
            ? (Math.Max(s.Area - 2 * s.B * s.Tf + (s.Tw + 2 * s.Root) * s.Tf, (s.H - 2 * s.Tf) * s.Tw), s.Area - (s.H - 2 * s.Tf) * s.Tw)
            : s.IsTube ? (s.Area * s.H / (s.B + s.H), s.Area * s.B / (s.B + s.H))
            : s.IsPipe ? (2 * s.Area / Math.PI, 2 * s.Area / Math.PI) : (s.Area, s.Area);
        var (vzRd, vyRd) = (Shear(avz), Shear(avy));

        // Much shear takes from the moment the web can carry (6.2.8).
        var myRd = wy * fy / GammaM0;
        if (vz > 0.5 * vzRd && s.IsI)
        {
            var rho = Math.Pow(2 * vz / vzRd - 1, 2);
            var web = (s.H - 2 * s.Tf) * s.Tw;
            myRd = Math.Max(0, (wy - rho * web * web / (4 * s.Tw)) * fy / GammaM0);
        }

        var mzRd = wz * fy / GammaM0;
        var name = f.N >= 0 ? "Tension" : "Compression";
        yield return new StrengthCheck(name, (f.N >= 0 ? tension : compression) / 1e3, axial / 1e3, "kN", f.N >= 0 ? "6.2.3" : "6.2.4", $"Class {type}");
        yield return new StrengthCheck("Bending, strong axis", my / 1e6, myRd / 1e6, "kNm", "6.2.5, 6.2.8", $"Class {type}");
        yield return new StrengthCheck("Bending, weak axis", mz / 1e6, mzRd / 1e6, "kNm", "6.2.5");
        yield return new StrengthCheck("Shear along the web", vz / 1e3, vzRd / 1e3, "kN", "6.2.6");
        yield return new StrengthCheck("Shear across", vy / 1e3, vyRd / 1e3, "kN", "6.2.6");

        // A tube or pipe twisted, 6.2.7: Bredt's shear flow round its wall, T / 2 Am t, against fy / √3 γM0; and its shear resistance reduced by it, 6.2.6 (6.28).
        if ((s.IsTube || s.IsPipe) && Math.Abs(f.T) > 1e-6)
        {
            var t = s.IsTube ? s.Tw : s.Tf > 0 ? s.Tf : s.Tw;
            var enclosed = s.IsTube ? (s.H - t) * (s.B - t) : Math.PI * Math.Pow((s.H - t) / 2, 2);
            var limit = fy / Math.Sqrt(3) / GammaM0;
            var stress = Math.Abs(f.T) * 1e6 / (2 * enclosed * t);
            yield return new StrengthCheck("Torsion", Math.Abs(f.T), 2 * enclosed * t * limit / 1e6, "kNm", "6.2.7", $"Am {enclosed:0} mm²");
            var left = Math.Max(0, 1 - stress / limit);
            yield return new StrengthCheck("Shear with torsion", Math.Max(vz / vzRd, vy / vyRd), left, "", "6.2.6 (6.28)", $"Vpl,T,Rd {left:P0} of Vpl,Rd");
        }

        yield return new StrengthCheck("Axial and bending together", (compression + tension) / axial + my / Math.Max(1, myRd) + mz / Math.Max(1, mzRd), 1, "", "6.2.1(7)");

        // Flexural and, for an I, torsional buckling: its slendernesses about each axis.
        var (curveY, curveZ) = Curves(s);
        double Slenderness(double length, double second) => length <= 0 ? 0 : Math.Sqrt(nRk / (Math.PI * Math.PI * E * second / (length * length)));
        var (ly, lz) = (Slenderness(lengths.Strong, s.Iy), Slenderness(lengths.Weak, s.Iz));
        var (chiY, chiZ) = (Chi(curveY, ly), Chi(curveZ, lz));
        if (s.IsI && lengths.Weak > 0)
        {
            var ncrT = (G * s.It + Math.PI * Math.PI * E * s.Iw / (lengths.Weak * lengths.Weak)) * s.Area / (s.Iy + s.Iz);
            chiZ = Math.Min(chiZ, Chi(curveZ, Math.Sqrt(nRk / ncrT)));
        }

        // Lateral torsional buckling: the top in compression, unless a floor holds it, and the underside over a hogging length.
        var lateral = f.TopInTension ? lengths.LateralHogging : lengths.Lateral;
        var chiLt = 1.0;
        if (lateral > 0 && my > 0 && (s.IsI || s.Shape == "Rectangle"))
        {
            var slenderness = Math.Sqrt(wy * fy / CriticalMoment(s, lateral));
            var ratio = s.H / s.B;
            var alpha = !s.IsI ? 0.76 : ratio <= 2 ? 0.34 : ratio <= 3.1 ? 0.49 : 0.76;
            chiLt = ChiLt(alpha, slenderness);
            yield return new StrengthCheck("Lateral torsional buckling", my / 1e6, chiLt * wy * fy / GammaM1 / 1e6, "kNm", "6.3.2", $"over {lateral:0} mm, χLT {chiLt:0.00}");
        }

        if (compression <= 0) yield break;
        yield return new StrengthCheck("Buckling", compression / 1e3, Math.Min(chiY, chiZ) * nRk / GammaM1 / 1e3, "kN", "6.3.1", $"χy {chiY:0.00}, χz {chiZ:0.00}");

        // 6.3.3, Annex B with every Cm 1.0.
        var (ny, nz) = (compression / (chiY * nRk / GammaM1), compression / (chiZ * nRk / GammaM1));
        var plastic = type <= 2;
        var kyy = plastic ? Math.Min(1 + (ly - 0.2) * ny, 1 + 0.8 * ny) : Math.Min(1 + 0.6 * ly * ny, 1 + 0.6 * ny);
        var kzz = plastic
            ? s.IsI ? Math.Min(1 + (2 * lz - 0.6) * nz, 1 + 1.4 * nz) : Math.Min(1 + (lz - 0.2) * nz, 1 + 0.8 * nz)
            : Math.Min(1 + 0.6 * lz * nz, 1 + 0.6 * nz);
        kyy = Math.Max(1, kyy);
        kzz = Math.Max(1, kzz);
        var kyz = plastic ? 0.6 * kzz : kzz;
        double kzy;
        if (s.IsI)
        {
            var share = plastic ? 0.1 : 0.05;
            kzy = plastic && lz < 0.4
                ? Math.Min(0.6 + lz, 1 - share * lz * nz / 0.75)
                : Math.Max(1 - share * lz * nz / 0.75, 1 - share * nz / 0.75);
        }
        else kzy = plastic ? 0.6 * kyy : 0.8 * kyy;

        var bending = my / (chiLt * wy * fy / GammaM1);
        var weak = mz / (wz * fy / GammaM1);
        yield return new StrengthCheck("Buckling with bending, strong axis", compression / (chiY * nRk / GammaM1) + kyy * bending + kyz * weak, 1, "", "6.3.3 (6.61)");
        yield return new StrengthCheck("Buckling with bending, weak axis", compression / (chiZ * nRk / GammaM1) + kzy * bending + kzz * weak, 1, "", "6.3.3 (6.62)");
    }

    // ---- AISC 360-16 --------------------------------------------------------------------

    private const double Psi = 0.00689476;
    private const double Fy = 50000 * Psi, Eus = 29000000 * Psi, Gus = 11200000 * Psi;

    /// <summary>The critical stress of E3 for a flexural buckling stress, N/mm²: 0.658^(Fy/Fe) Fy, or 0.877 Fe once slender.</summary>
    public static double CriticalStress(double fe) =>
        Fy / fe <= 2.25 ? Math.Pow(0.658, Fy / fe) * Fy : 0.877 * fe;

    /// <summary>What slender plates leave of a section at a critical stress, by E7, mm²; the whole area where none is.</summary>
    public static double EffectiveAreaAisc(SteelSection s, double fcr)
    {
        var root = Math.Sqrt(Eus / Fy);
        double Lost(double b, double t, double limit, double c1, double c2)
        {
            var slenderness = b / t;
            if (slenderness <= limit * Math.Sqrt(Fy / fcr)) return 0;
            var fel = Math.Pow(c2 * limit / slenderness, 2) * Fy;
            var share = Math.Sqrt(fel / fcr);
            return (b - b * (1 - c1 * share) * share) * t;
        }

        if (s.IsTube)
            return s.Area - 2 * Lost(s.H - 3 * s.Tw, s.Tw, 1.40 * root, 0.20, 1.38) - 2 * Lost(s.B - 3 * s.Tf, s.Tf, 1.40 * root, 0.20, 1.38);
        if (s.IsPipe)
        {
            var dt = s.H / s.Tw;
            return dt <= 0.11 * Eus / Fy ? s.Area : (0.038 * Eus / (Fy * dt) + 2.0 / 3) * s.Area;
        }

        if (!s.IsI) return s.Area;
        return s.Area - Lost(s.H - 2 * s.Tf - 2 * s.Root, s.Tw, 1.49 * root, 0.18, 1.31) - 4 * Lost(s.B / 2, s.Tf, 0.56 * root, 0.22, 1.49);
    }

    /// <summary>The nominal moment of a section about its strong axis over an unbraced length, N mm, by chapter F.</summary>
    public static double NominalMoment(SteelSection s, double unbraced, double cb)
    {
        var root = Math.Sqrt(Eus / Fy);
        var plastic = Fy * s.Wply;
        if (s.IsI)
        {
            var (flange, web) = (s.B / 2 / s.Tf, (s.H - 2 * s.Tf - 2 * s.Root) / s.Tw);
            var (pf, rf) = (0.38 * root, 1.0 * root);
            var capped = web > 3.76 * root ? Fy * s.Wely : plastic;

            // Local buckling of the flange, F3.
            var local = flange <= pf ? capped
                : flange <= rf ? capped - (capped - 0.7 * Fy * s.Wely) * (flange - pf) / (rf - pf)
                : 0.9 * Eus * Math.Clamp(4 / Math.Sqrt(web), 0.35, 0.76) * s.Wely / (flange * flange);
            if (unbraced <= 0) return local;

            // Lateral torsional buckling, F2.
            var ho = s.H - s.Tf;
            var rts = Math.Sqrt(Math.Sqrt(s.Iz * s.Iw) / s.Wely);
            var ratio = s.It / (s.Wely * ho);
            var lp = 1.76 * s.Rz * root;
            var lr = 1.95 * rts * Eus / (0.7 * Fy) * Math.Sqrt(ratio + Math.Sqrt(ratio * ratio + 6.76 * Math.Pow(0.7 * Fy / Eus, 2)));
            var buckling = unbraced <= lp ? capped
                : unbraced <= lr ? cb * (capped - (capped - 0.7 * Fy * s.Wely) * (unbraced - lp) / (lr - lp))
                : cb * Math.PI * Math.PI * Eus / Math.Pow(unbraced / rts, 2) * Math.Sqrt(1 + 0.078 * ratio * Math.Pow(unbraced / rts, 2)) * s.Wely;
            return Math.Min(local, Math.Min(capped, buckling));
        }

        if (s.IsTube)
        {
            var (flange, web) = ((s.B - 3 * s.Tf) / s.Tf, (s.H - 3 * s.Tw) / s.Tw);
            var byFlange = flange <= 1.12 * root ? plastic : plastic - (plastic - Fy * s.Wely) * (3.57 * flange * Math.Sqrt(Fy / Eus) - 4.0);
            var byWeb = web <= 2.42 * root ? plastic : plastic - (plastic - Fy * s.Wely) * (0.305 * web * Math.Sqrt(Fy / Eus) - 0.738);
            return Math.Min(plastic, Math.Min(byFlange, byWeb));
        }

        if (s.IsPipe)
        {
            var dt = s.H / s.Tw;
            return dt <= 0.07 * Eus / Fy ? plastic : Math.Min(plastic, (0.021 * Eus / dt + Fy) * s.Wely);
        }

        // Solid bars, F11.
        var most = Math.Min(plastic, 1.6 * Fy * s.Wely);
        if (s.Shape != "Rectangle" || unbraced <= 0) return most;
        var bar = unbraced * s.H / (s.B * s.B);
        if (bar <= 0.08 * Eus / Fy) return most;
        return Math.Min(most, bar <= 1.9 * Eus / Fy
            ? cb * (1.52 - 0.274 * bar * Fy / Eus) * Fy * s.Wely
            : 1.9 * Eus * cb / bar * s.Wely);
    }

    /// <summary>The nominal moment about the weak axis, N mm, by F6, F7, F8 and F11.</summary>
    private static double NominalWeakMoment(SteelSection s)
    {
        var plastic = Math.Min(Fy * s.Wplz, s.IsI || s.IsSolid ? 1.6 * Fy * s.Welz : double.MaxValue);
        if (!s.IsI) return plastic;
        var root = Math.Sqrt(Eus / Fy);
        var flange = s.B / 2 / s.Tf;
        return flange <= 0.38 * root ? plastic
            : flange <= root ? plastic - (plastic - 0.7 * Fy * s.Welz) * (flange - 0.38 * root) / (0.62 * root)
            : 0.69 * Eus / (flange * flange) * s.Welz;
    }

    /// <summary>What a section has to carry at a point against what it can, by AISC 360-16, LRFD.</summary>
    public static IEnumerable<StrengthCheck> Aisc(SteelSection s, MemberLengths lengths, MemberForces f, double cb)
    {
        var root = Math.Sqrt(Eus / Fy);
        var (compression, tension) = (Math.Max(0, -f.N) * 1e3, Math.Max(0, f.N) * 1e3);
        var (my, mz) = (Math.Abs(f.My) * 1e6, Math.Abs(f.Mz) * 1e6);
        var (vz, vy) = (Math.Abs(f.Vz) * 1e3, Math.Abs(f.Vy) * 1e3);

        // Compression, E3 and E7, and E4's torsional buckling of an I.
        double Euler(double length, double radius) => length <= 0 ? double.MaxValue : Math.PI * Math.PI * Eus / Math.Pow(length / radius, 2);
        var fe = Math.Min(Euler(lengths.Strong, s.Ry), Euler(lengths.Weak, s.Rz));
        if (s.IsI && lengths.Weak > 0)
            fe = Math.Min(fe, (Math.PI * Math.PI * Eus * s.Iw / (lengths.Weak * lengths.Weak) + Gus * s.It) / (s.Iy + s.Iz));
        var fcr = CriticalStress(fe);
        var phiPn = 0.9 * fcr * EffectiveAreaAisc(s, fcr);
        var phiTn = 0.9 * Fy * s.Area;

        // Flexure.
        var lateral = f.TopInTension ? lengths.LateralHogging : lengths.Lateral;
        var phiMx = 0.9 * NominalMoment(s, lateral, cb);
        var phiMy = 0.9 * NominalWeakMoment(s);

        // Shear, G2 to G6.
        double strongShear;
        if (s.IsI)
        {
            var (ratio, aw) = ((s.H - 2 * s.Tf - 2 * s.Root) / s.Tw, s.H * s.Tw);
            const double kv = 5.34;
            var limit = 1.10 * Math.Sqrt(kv) * root;
            var (phi, cv) = ratio <= 2.24 * root ? (1.0, 1.0) : ratio <= limit ? (0.9, 1.0) : (0.9, limit / ratio);
            strongShear = phi * 0.6 * Fy * aw * cv;
        }
        else if (s.IsTube) strongShear = 0.9 * 0.6 * Fy * 2 * (s.H - 3 * s.Tw) * s.Tw;
        else if (s.IsPipe) strongShear = 0.9 * 0.6 * Fy * s.Area / 2;
        else strongShear = 0.9 * 0.6 * Fy * s.Area;
        var weakShear = s.IsI ? 0.9 * 0.6 * Fy * 2 * s.B * s.Tf : s.IsTube ? 0.9 * 0.6 * Fy * 2 * (s.B - 3 * s.Tf) * s.Tf : strongShear;

        var name = f.N >= 0 ? "Tension" : "Compression";
        yield return new StrengthCheck(name, (f.N >= 0 ? tension : compression) / 1e3, (f.N >= 0 ? phiTn : phiPn) / 1e3, "kN", f.N >= 0 ? "D2" : "E3, E7", f.N >= 0 ? string.Empty : $"Fcr {fcr / Psi / 1000:0.0} ksi");
        yield return new StrengthCheck("Bending, strong axis", my / 1e6, phiMx / 1e6, "kNm", "F2 to F11", lateral > 0 ? $"unbraced {lateral:0} mm, Cb {cb:0.00}" : "held along its length");
        yield return new StrengthCheck("Bending, weak axis", mz / 1e6, phiMy / 1e6, "kNm", "F6 to F11");
        yield return new StrengthCheck("Shear along the web", vz / 1e3, strongShear / 1e3, "kN", "G2");
        yield return new StrengthCheck("Shear across", vy / 1e3, weakShear / 1e3, "kN", "G6");

        // An HSS twisted, H3.1: φTn = 0.9 Fcr C, Fcr taken as 0.6 Fy, C = 2(B - t)(H - t)t - 4.5(4 - π)t³ or π(D - t)² t / 2; and over 0.2 φTn, with the rest by (H3-6).
        if ((s.IsTube || s.IsPipe) && Math.Abs(f.T) > 1e-6)
        {
            var t = s.IsTube ? s.Tw : s.Tf > 0 ? s.Tf : s.Tw;
            var constant = s.IsTube ? 2 * (s.B - t) * (s.H - t) * t - 4.5 * (4 - Math.PI) * t * t * t : Math.PI * Math.Pow(s.H - t, 2) * t / 2;
            var torsion = 0.9 * 0.6 * Fy * constant;
            var tr = Math.Abs(f.T) * 1e6;
            yield return new StrengthCheck("Torsion", tr / 1e6, torsion / 1e6, "kNm", "H3.1", $"C {constant / 1000:0} cm³");
            if (tr > 0.2 * torsion)
                yield return new StrengthCheck("Torsion with the rest", (f.N >= 0 ? tension / phiTn : compression / phiPn) + my / phiMx + mz / phiMy + Math.Pow(Math.Max(vz / strongShear, vy / weakShear) + tr / torsion, 2), 1, "", "H3.2 (H3-6)");
        }

        // Axial force and bending together, H1.
        var axial = f.N >= 0 ? tension / phiTn : compression / phiPn;
        var bending = my / phiMx + mz / phiMy;
        yield return new StrengthCheck("Axial and bending together", axial >= 0.2 ? axial + 8.0 / 9 * bending : axial / 2 + bending, 1, "", axial >= 0.2 ? "H1-1a" : "H1-1b");
    }
}
