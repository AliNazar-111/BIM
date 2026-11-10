using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A stud wall. Its frame as drawn, or for a wall with none drawn, studs that fit its depth at 400 centres, or 16 in; in the plates a stud wall is an even sheet of its depth, so
/// what comes down it is read a metre at a time and each stud given its spacing of that, with the wind on it by statics, between its plates. Each stud through the same checks as any timber
/// member, BS EN 1995-1-1 or the NDS, held across by what is fixed to its faces and sharing its load with its neighbours; its foot on the sole plate; and the wall's racking, by Method A of
/// 9.2.4.2 or the SDPWS, on the lengths of it with no opening in them.
/// </summary>
public static class TimberWallDesign
{
    private const double Psi = 0.00689476;

    /// <summary>
    /// One smooth nail through a board into timber in single shear by BS EN 1995-1-1 (8.6), kN: the least of Johansen's six ways. Embedment by (8.22) for OSB and (8.15) for timber, yield moment by (8.14),
    /// nothing taken for the rope effect, on the safe side.
    /// </summary>
    public static double NailInBoard(double d, double board, double into, double density = 350, double fu = 600)
    {
        var (first, second) = (65 * Math.Pow(d, -0.7) * Math.Pow(board, 0.1), 0.082 * density * Math.Pow(d, -0.3));
        var moment = 0.3 * fu * Math.Pow(d, 2.6);
        var beta = second / first;
        var ratio = into / board;
        double[] ways =
        {
            first * board * d,
            second * into * d,
            first * board * d / (1 + beta) * (Math.Sqrt(beta + 2 * beta * beta * (1 + ratio + ratio * ratio) + beta * beta * beta * ratio * ratio) - beta * (1 + ratio)),
            1.05 * first * board * d / (2 + beta) * (Math.Sqrt(2 * beta * (1 + beta) + 4 * beta * (2 + beta) * moment / (first * d * board * board)) - beta),
            1.05 * first * into * d / (1 + 2 * beta) * (Math.Sqrt(2 * beta * beta * (1 + beta) + 4 * beta * (1 + 2 * beta) * moment / (first * d * into * into)) - beta),
            1.15 * Math.Sqrt(2 * beta / (1 + beta)) * Math.Sqrt(2 * moment * first * d)
        };
        return ways.Min() / 1000;
    }

    /// <summary>
    /// What lengths of wall carry in racking by Method A of BS EN 1995-1-1 9.2.4.2, kN: (9.21), each Ff,Rd bi c / s, c 1 from half the wall's height and in proportion under, the fasteners along a sheet's edge at 1.2 of their worth.
    /// </summary>
    public static double RackingEurocode(IEnumerable<double> lengths, double height, double nail, double spacing) =>
        lengths.Sum(length => 1.2 * nail * length / spacing * Math.Min(1, length / (height / 2)));

    /// <summary>
    /// What lengths of wall carry in racking by the SDPWS, LRFD, kN: 670 plf nominal for wind on 7/16 in sheathing with 8d at 6 in, φ 0.8; a length narrower than the wall is tall over 3.5 counting for nothing, and over 2 at 1.25 - 0.125 h/b.
    /// </summary>
    public static double RackingSdpws(IEnumerable<double> lengths, double height) => lengths.Sum(length =>
    {
        var aspect = height / length;
        return aspect > 3.5 ? 0 : 0.8 * 670 * 4.44822e-3 / 0.3048 * length / 1000 * Math.Min(1, 1.25 - 0.125 * aspect);
    });

    /// <summary>The sheathing's thickness, mm: 9 mm OSB, or 7/16 in.</summary>
    public static double Board(bool us) => us ? 11.1 : 9;

    /// <summary>OSB's modulus along a sheet, N/mm², taken as 3,000: what a wall of studs has across them in its plane.</summary>
    public const double BoardModulus = 3000;

    /// <summary>
    /// How stiff a sheathed stud wall is in racking, N/mm of its length for a unit shear strain: each nail's slip modulus Kser = ρm^1.5 d^0.8 / 30 of BS EN 1995-1-1 Table 7.1, ρm 420, round a rigid sheet 1200 by 2400
    /// at 150, by which the frame racks 2 v s / Kser (1/b + 1/h), in series with the sheet's own shear at a G of 1,080 N/mm². The same nails as the racking resistance takes: 3.1 mm, or 8d common at 0.131 in.
    /// </summary>
    public static double RackingStiffness(bool us)
    {
        var slip = Math.Pow(420, 1.5) * Math.Pow(us ? 3.33 : 3.1, 0.8) / 30;
        var nails = slip / (2 * 150 * (1.0 / 1200 + 1.0 / 2400));
        var sheet = 1080 * Board(us);
        return 1 / (1 / nails + 1 / sheet);
    }

    /// <summary>
    /// The force at each end of a length of wall from racking, kN: (9.23), Fi,v h / bi, and what a wall of several lengths comes to, each taking its share by what it can carry, which brings the wall's overturning moment, kNm,
    /// to that over the lengths that count, m. The same for the SDPWS, where a tie-down takes v h.
    /// </summary>
    public static double EndForce(double moment, IEnumerable<double> lengths, double height) =>
        moment / Math.Max(lengths.Sum(length => length / 1000 * Math.Min(1, length / (height / 2))), 1e-6);

    /// <summary>What lifts the end of a length of wall once what stands on it has held it down, kN: half the length's load on the arm of the whole length, what it comes to at one end.</summary>
    public static double Uplift(double force, double line, double length) => force - line * length / 2000;

    /// <summary>What a stud wall's ends come to under a combination: the force at each end, what stands on it, kN/m, and what is left lifting the narrowest full length.</summary>
    public sealed record EndForces(string Under, double Force, double Line, double Length, double Uplift, double Moment);

    /// <summary>
    /// The ends of a stud wall under each combination, in the order the analysis gives them. The overturning moment at its foot is each storey's racking on its own height, this one's and those of the walls standing on it, by the rule that it is the sum of storey shears
    /// by storey heights; what holds it down is what its foot carries under that same combination, so the wind's lift on the roof is in it.
    /// </summary>
    public static IReadOnlyList<EndForces> EndsOf(BimDocument document, SecondOrderAnalysis second, AnalyticalPanel panel)
    {
        var flat = panel.FlatOutline;
        var height = flat.Max(p => p.Y) - flat.Min(p => p.Y);
        var solid = SolidLengths(panel);
        if (solid.Count == 0) return Array.Empty<EndForces>();

        // The narrowest length that counts in full holds down least.
        var narrowest = solid.Where(length => length >= height / 2).DefaultIfEmpty(solid.Max()).Min();
        var bands = second.WallStretches(panel, height);
        var above = new List<(IReadOnlyList<(LoadCombination Combination, IReadOnlyList<WallStretch> Bands)> Bands, double Height, double Share)>();
        if (panel.Element is Wall wall)
        {
            var own = flat.Max(p => p.X) - flat.Min(p => p.X);
            foreach (var part in Loads.Stack(document, wall).Where(part => !ReferenceEquals(part, wall)))
                foreach (var upper in second.Analysis.Model.Panels.Where(each => each.Kind == AnalyticalPanelKind.Wall && ReferenceEquals(each.Element, part)))
                {
                    var outline = upper.FlatOutline;
                    var tall = outline.Max(p => p.Y) - outline.Min(p => p.Y);
                    above.Add((second.WallStretches(upper, tall), tall, own / Math.Max(wall.Length, 1)));
                }
        }

        static double Racked(IReadOnlyList<WallStretch> stretches) => Math.Abs(stretches.Where(stretch => stretch.Level == 0).Sum(stretch => stretch.Lateral * stretch.Length)) / 1000;
        var results = new List<EndForces>();
        for (var k = 0; k < bands.Count; k++)
        {
            var (combination, stretches) = bands[k];
            var moment = Racked(stretches) * height / 1000;
            foreach (var (upper, tall, share) in above)
                if (k < upper.Count) moment += Racked(upper[k].Bands) * tall / 1000 * share;
            var foot = stretches.Where(stretch => stretch.Level == 0).ToList();
            var line = Math.Max(0, -foot.Sum(stretch => stretch.Vertical * stretch.Length) / Math.Max(foot.Sum(stretch => stretch.Length), 1));
            var force = EndForce(moment, solid, height);
            results.Add(new EndForces(combination.Name, force, line, narrowest, Uplift(force, line, narrowest), moment));
        }

        return results;
    }

    /// <summary>A stud's foot on the sole plate by BS EN 1995-1-1 6.1.5, N/mm²: its thickness and 30 mm each way along the plate, on 1.25 of fc,90,d as a member on a continuous support.</summary>
    public static StrengthCheck FootEurocode(double force, double width, double depth, TimberGrade grade, double kmod)
    {
        var effective = width + 2 * Math.Min(30, width);
        return new StrengthCheck("Stud on its plate", force * 1000 / (depth * effective), 1.25 * kmod * 2.5 / grade.GammaM, "N/mm²", "6.1.5", $"{effective:0} mm of the plate, kc,90 1.25, kmod {kmod:0.0}");
    }

    /// <summary>A stud's foot on the sole plate by the NDS 3.10.2, LRFD: Fc⊥ 625 psi, KF 1.67, φ 0.90, and Cb (lb + 3/8 in) / lb, its bearing being short of 6 in and away from the plate's end.</summary>
    public static StrengthCheck FootNds(double force, double width, double depth)
    {
        var inches = width / 25.4;
        var factor = inches < 6 ? (inches + 0.375) / inches : 1;
        return new StrengthCheck("Stud on its plate", force * 1000 / (depth * width), 625 * Psi * 1.67 * 0.9 * factor, "N/mm²", "NDS 3.10.2, 3.10.4", $"Cb {factor:0.00}");
    }

    /// <summary>The lengths of a plate with no opening in them, mm: what can rack, or stand in shear.</summary>
    public static IReadOnlyList<double> SolidLengths(AnalyticalPanel panel)
    {
        var flat = panel.FlatOutline;
        var cuts = panel.Cuts.Select(opening => opening.Select(panel.Flat).ToList()).Select(opening => (From: opening.Min(p => p.X), To: opening.Max(p => p.X))).OrderBy(cut => cut.From).ToList();
        var (at, end) = (flat.Min(p => p.X), flat.Max(p => p.X));
        var lengths = new List<double>();
        foreach (var (from, to) in cuts)
        {
            if (from > at + 1) lengths.Add(from - at);
            at = Math.Max(at, to);
        }

        if (end > at + 1) lengths.Add(end - at);
        return lengths;
    }

    /// <summary>What wind stands on a wall under a combination, kN/m²: the most of its cases there.</summary>
    public static double WindOn(StructuralLoading loading, AnalyticalPanel panel, LoadCombination combination) => combination.Terms.Where(term => term.Case.Nature == LoadNature.Wind).Sum(term =>
        term.Factor * loading.SurfaceLoads.Where(load => ReferenceEquals(load.Panel, panel) && load.Case == term.Case).Select(load => Math.Sqrt(load.Force.X * load.Force.X + load.Force.Y * load.Force.Y)).DefaultIfEmpty(0).Max());

    /// <summary>The frame a wall has, or is taken to have; null for a wall of solid timber, or of steel studs.</summary>
    public static (FramingSection Section, double Spacing, int Noggings, bool Drawn)? FrameOf(BimDocument document, AnalyticalPanel panel, bool us)
    {
        if (panel.Element is not Wall wall) return null;
        if (WallFramings.Of(document, wall) is { } framing)
            return framing.Material == FramingMaterial.Steel ? null : (framing.SectionSize, framing.Spacing, framing.NoggingRows, true);

        // What is left between studs is insulation: nothing near timber's density. A solid timber or log wall is not a stud wall.
        return panel.Material.Density >= 200 ? null : (FramingSection.Fitting(FramingMaterial.Timber, panel.Thickness), us ? 406.4 : 400, 1, false);
    }

    public static WallCheck Check(BimDocument document, SecondOrderAnalysis second, AnalyticalPanel panel, bool us)
    {
        var quality = panel.Material.Quality;
        if (FrameOf(document, panel, us) is not { } frame)
            return new WallCheck(panel, quality, string.Empty, Array.Empty<StrengthCheck>(), 0, panel.Thickness, "Not checked: a wall of solid timber or steel studs is not worked out here.");

        var analysis = second.Analysis;
        var (section, spacing, noggings, drawn) = frame;
        var (height, _, _, _) = MasonryDesign.Holding(analysis.Model, panel);
        var grade = section.Name.Contains("C16", StringComparison.Ordinal) ? TimberDesign.C16 : TimberDesign.C24;
        var (width, depth) = (section.Thickness, section.Depth);
        var lengths = new MemberLengths(height, Math.Min(600, height / (noggings + 1)), 0, 0);
        var solid = SolidLengths(panel);
        var nail = NailInBoard(3.1, 9, 41);
        var bands = second.WallStretches(panel, height);
        var ends = EndsOf(document, second, panel);

        IEnumerable<StrengthCheck> Under(LoadCombination combination, IReadOnlyList<WallStretch> stretches)
        {
            var stud = stretches.Select(stretch => Math.Max(0, -stretch.Vertical)).DefaultIfEmpty(0).Max() * spacing / 1000;
            var wind = WindOn(analysis.Loading, panel, combination) * spacing / 1000;
            var forces = new MemberForces(-stud, 0, wind * height / 2000, 0, wind * Math.Pow(height / 1000, 2) / 8, 0, false);
            var kmod = TimberDesign.Kmod(combination);
            var checks = (us ? TimberDesign.Nds(depth, width, false, true, true, lengths, height, forces, TimberDesign.TimeEffect(combination)) : TimberDesign.Eurocode(grade, depth, width, true, lengths, forces, kmod)).ToList();
            foreach (var check in checks) yield return check with { Name = $"Stud: {check.Name.ToLowerInvariant()}" };
            yield return us ? FootNds(stud, width, depth) : FootEurocode(stud, width, depth, grade, kmod);

            // Racking is the wind's, at its foot. The plate standing for the wall also arches over its openings under its load, which reads as shear there; studs have no such thing.
            var racked = Math.Abs(stretches.Where(stretch => stretch.Level == 0).Sum(stretch => stretch.Lateral * stretch.Length)) / 1000;
            var quake = combination.Terms.Any(term => term.Case.Nature == LoadNature.Seismic && term.Factor > 0);
            var carries = us ? RackingSdpws(solid, height) / (quake ? 1.4 : 1) : RackingEurocode(solid, height, kmod * nail / 1.3, 150);
            yield return new StrengthCheck("Racking", racked, carries, "kN", us ? "SDPWS 4.3" : "9.2.4.2", $"{solid.Sum() / 1000:0.0} m of it with no opening{(quake ? ", seismic at the wind's over 1.4" : string.Empty)}");

            // The end studs, two together, take what racking pushes down at the end of a length as well as their share of the wall's load: (9.23) and 9.2.4.2(9).
            var end = ends.FirstOrDefault(each => each.Under == combination.Name)?.Force ?? 0;
            if (end <= 0.01) yield break;
            var together = forces with { N = -(stud + end) };
            var pair = us ? TimberDesign.Nds(depth, 2 * width, false, true, true, lengths, height, together, TimberDesign.TimeEffect(combination)) : TimberDesign.Eurocode(grade, depth, 2 * width, true, lengths, together, kmod);
            foreach (var check in pair.Where(check => check.Name.Contains("uckling", StringComparison.Ordinal))) yield return check with { Name = $"End studs: {check.Name.ToLowerInvariant()}", Note = $"{end:0.0} kN from racking on top of {stud:0.0} kN" };
        }

        var worst = MemberStrength.Worst(bands.Select(entry => (entry.Combination.Name, Under(entry.Combination, entry.Bands)))).ToList();
        var taken = drawn ? string.Empty : $"No frame is drawn for it: {section.Name} studs at {spacing:0} are taken. ";
        var sheet = us ? "7/16 in sheathing with 8d nails at 6 in round each sheet" : "9 mm OSB with 3.1 by 50 nails at 150 round each sheet";
        return new WallCheck(panel, $"{section.Name} at {spacing:0} centres", us ? "the NDS and the SDPWS, LRFD" : "BS EN 1995-1-1 and its UK National Annex", worst, lengths.Strong, depth,
            $"{taken}Studs held across every {lengths.Weak:0} by what is fixed to their faces, the wind on them between their plates. Racking is the wind's share of the shear at its foot, taken on {sheet} on one face, each end tied down, for which see Joints. Two studs together at each end. In the analysis it bends one way, up its studs, at their own stiffness, with a hundredth of that across them.");
    }
}

/// <summary>
/// A concrete wall, its load and moments read a metre at a time. BS EN 1992-1-1: each metre as a column of the wall's thickness, with its slenderness, the steel it comes to and at least 0.002 Ac (9.6.2); in-plane shear for the wall
/// as a whole by 6.2.2. ACI 318-19: the simplified method of 11.5.3 with the load in the middle third, else as a column; in-plane shear by 11.5.4.5. The model holds no bars for it, so what it comes to is said.
/// </summary>
public static class ConcreteWallDesign
{
    private const double Psi = 0.00689476;

    /// <summary>
    /// A metre of plain concrete wall by BS EN 1992-1-1 12.6.5.2, kN: NRd = b hw fcd,pl Φ with Φ = 1.14 (1 - 2 etot / hw) - 0.02 l0 / hw, no more than 1 - 2 etot / hw, (12.10) and (12.11);
    /// fcd,pl = αcc,pl fck / γC with the UK National Annex's αcc,pl 0.6 as remembered, etot = e0 + l0 / 400.
    /// </summary>
    public static double PlainEurocode(double fck, double thickness, double effectiveLength, double eccentricity)
    {
        var total = eccentricity + effectiveLength / 400;
        var phi = Math.Max(0, Math.Min(1.14 * (1 - 2 * total / thickness) - 0.02 * effectiveLength / thickness, 1 - 2 * total / thickness));
        return 1000 * thickness * 0.6 * fck / 1.5 * phi / 1000;
    }

    /// <summary>
    /// Plain concrete in shear in its plane by BS EN 1992-1-1 12.6.3, kN over a compressed area: fcvd = √(fctd,pl² + σcp fctd,pl) while σcp is no more than σc,lim = fcd,pl - 2 √(fctd,pl
    /// (fctd,pl + fcd,pl)), the shear's peak 1.5 times its average; fctd,pl = αct,pl fctk,0.05 / γC with αct,pl 0.6, fctk,0.05 = 0.7 x 0.30 fck^(2/3).
    /// </summary>
    public static double PlainShearEurocode(double fck, double area, double stress)
    {
        var tension = 0.6 * 0.7 * 0.30 * Math.Pow(fck, 2.0 / 3) / 1.5;
        var compression = 0.6 * fck / 1.5;
        var limit = compression - 2 * Math.Sqrt(tension * (tension + compression));
        var fcvd = Math.Sqrt(tension * tension + Math.Min(Math.Max(0, stress), Math.Max(0, limit)) * tension);
        return fcvd * area / 1.5 / 1000;
    }

    /// <summary>ACI 318-19 (14.5.3.1), φ 0.60, kN per metre: 0.45 f'c Ag (1 - (lc / 32 h)²), a plain wall with its load's resultant in its middle third.</summary>
    public static double PlainAci(double fc, double thickness, double length) => 0.60 * 0.45 * fc * thickness * (1 - Math.Pow(length / (32 * thickness), 2));

    /// <summary>ACI 318-19 (11.5.3.1), φ 0.65, kN per metre: 0.55 f'c Ag (1 - (k lc / 32 h)²).</summary>
    public static double SimplifiedAci(double fc, double thickness, double length, double k) =>
        0.65 * 0.55 * fc * thickness * (1 - Math.Pow(k * length / (32 * thickness), 2));

    public static WallCheck Check(BimDocument document, SecondOrderAnalysis second, AnalyticalPanel panel, bool us)
    {
        var analysis = second.Analysis;
        var fc = ConcreteDesign.StrengthOf(panel.Material.Quality);
        var t = panel.Thickness;
        var (height, held, _, _) = MasonryDesign.Holding(analysis.Model, panel);
        var effective = held ? height : 2 * height;
        var strip = new ConcreteDesign.ColumnSection(t, 1000, false, 40);
        var across = new ConcreteDesign.ColumnSection(1000, t, false, 40);
        var (least, most) = ((us ? 0.0012 : 0.002) * 1000 * t, (us ? 0.08 : 0.04) * 1000 * t);
        var bands = second.WallStretches(panel, height);
        var solid = TimberWallDesign.SolidLengths(panel);
        var root = Math.Sqrt(fc / Psi) * Psi;

        IEnumerable<StrengthCheck> Under(IReadOnlyList<WallStretch> stretches)
        {
            foreach (var band in stretches)
            {
                var (load, moment) = (Math.Max(0, -band.Vertical), Math.Abs(band.MomentVertical) / 1000);
                if (load <= 0.01) continue;
                if (us && moment * 1000 / load <= t / 6)
                {
                    yield return new StrengthCheck("Load and bending", load, SimplifiedAci(fc, t, height, held ? 1 : 2), "kN/m", "11.5.3.1", $"in its middle third, at least {least:0} mm²/m up it and {0.0020 * 1000 * t:0} along it");
                    continue;
                }

                // The column routine's interaction is at the most steel it allows, which no wall has: what tells is the steel it comes to, unless even the most will not do.
                var (unity, steel) = us ? ConcreteDesign.ColumnAci(strip, across, load, moment, 0, effective, 0, fc, 0.0012) : ConcreteDesign.ColumnEurocode(strip, across, load, moment, 0, effective, 0, fc);
                var clause = us ? "11.5.2, 22.4" : "5.8, 6.1, 9.6.2";
                var said = $"{load:0.0} kN/m and {moment:0.0} kNm/m";
                yield return unity > 1
                    ? new StrengthCheck("Load and bending", unity, 1, "", clause, $"{said}, more than the most steel would carry")
                    : new StrengthCheck("Load and bending", Math.Max(steel, least), most, "mm²/m", clause, $"{said}, needs {Math.Max(steel, least):0} mm²/m up it of the most it may have");
            }

            var shear = Math.Abs(stretches.Where(stretch => stretch.Level == 0).Sum(stretch => stretch.Shear * stretch.Length)) / 1000;
            var d = 0.8 * solid.Sum();
            var carries = us ? 0.75 * 2 * root * t * d / 1000 : PlateDesign.OneWayEurocode(d, 0.002, fc) * t * d / 1000;
            yield return new StrengthCheck("Shear in its plane", shear, carries, "kN", us ? "11.5.4.5" : "6.2.2", $"{solid.Sum() / 1000:0.0} m of it, no links");
        }

        // Plain first: where every metre stands without bars, by BS EN 1992-1-1 12.6.5.2 or ACI 318-19 14.5.3, its load in its middle third, and its plane by 12.6.3, it is a plain wall.
        IEnumerable<StrengthCheck> Plain(IReadOnlyList<WallStretch> stretches)
        {
            foreach (var band in stretches)
            {
                var (load, moment) = (Math.Max(0, -band.Vertical), Math.Abs(band.MomentVertical) / 1000);
                if (load <= 0.01) continue;
                var eccentric = moment * 1000 / load;
                if (us)
                    yield return eccentric <= t / 6
                        ? new StrengthCheck("Load as plain concrete", load, PlainAci(fc, t, height), "kN/m", "14.5.3.1", $"its load {eccentric:0} mm off centre, in its middle third")
                        : new StrengthCheck("Load as plain concrete", eccentric, t / 6, "mm", "14.5.3.1", "its load outside its middle third");
                else
                    yield return new StrengthCheck("Load as plain concrete", load, PlainEurocode(fc, t, effective, eccentric), "kN/m", "12.6.5.2, (12.10)", $"etot {eccentric + effective / 400:0} mm");
            }

            if (!us)
            {
                yield return new StrengthCheck("Slenderness as plain concrete", effective / t * Math.Sqrt(12), 86, "λ", "12.6.5.1(5)", $"l0 {effective:0} mm");
                var level = stretches.Where(stretch => stretch.Level == 0).ToList();
                var shear = Math.Abs(level.Sum(stretch => stretch.Shear * stretch.Length)) / 1000;
                var compressed = level.Where(stretch => stretch.Vertical < 0).ToList();
                var area = compressed.Sum(stretch => stretch.Length) * t;
                var stress = area > 0 ? compressed.Sum(stretch => -stretch.Vertical * stretch.Length) / area : 0;
                yield return new StrengthCheck("Shear in its plane as plain concrete", shear, PlainShearEurocode(fc, area, stress), "kN", "12.6.3", $"{area / t / 1000:0.0} m in compression");
            }
        }

        var plain = MemberStrength.Worst(bands.Select(entry => (entry.Combination.Name, Plain(entry.Bands)))).ToList();
        var flat = panel.FlatOutline;
        if (us && plain.Count > 0)
            plain.Insert(0, new StrengthCheck("Least thickness", Math.Max(139.7, Math.Min(height, flat.Max(p => p.X) - flat.Min(p => p.X)) / 24), t, "mm", "Table 14.3.1.1", "a plain bearing wall's: 5.5 in or 1/24 of its height or length, as remembered"));
        if (plain.Count > 0 && plain.All(check => check.Ratio <= 1))
            return new WallCheck(panel, $"{panel.Material.Quality}, plain", us ? "ACI 318-19 Chapter 14" : "BS EN 1992-1-1 Section 12 and its UK National Annex", plain, effective, t,
                $"It stands as plain concrete, no bars needed for strength{(us ? " (14.5.3.1)" : " (12.6.5.2); a reinforced wall's least steel, 0.002 Ac, is for one that is reinforced (9.6.2)")}. Taken as pinned top and foot; αcc,pl and αct,pl as remembered.");

        var worst = MemberStrength.Worst(bands.Select(entry => (entry.Combination.Name, Under(entry.Bands)))).ToList();
        var plainSaid = plain.Count == 0 ? string.Empty : $" As plain concrete it would not stand: {plain.MaxBy(check => check.Ratio)!.Name}, {plain.Max(check => check.Ratio):P0}.";
        if (us) worst.Insert(0, new StrengthCheck("Least thickness", Math.Max(100, Math.Min(height, flat.Max(p => p.X) - flat.Min(p => p.X)) / 25), t, "mm", "Table 11.3.1.1", "a bearing wall's, unless analysis shows a thinner one stands"));
        return new WallCheck(panel, panel.Material.Quality, us ? "ACI 318-19" : "BS EN 1992-1-1 and its UK National Annex", worst, effective, t,
            $"No bars are in the model: the steel said is what each metre of it comes to, each face half, with {(us ? "at least 0.0020" : "a quarter of it or 0.001, the greater,")} along it. Taken as pinned top and foot.{(us ? string.Empty : " Its slenderness goes in by nominal curvature: BS EN 1992-1-1 limits a plain wall's, 12.6.5.1(5), and not a reinforced one's.")}{plainSaid}");
    }
}
