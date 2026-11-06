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
            var carries = us ? RackingSdpws(solid, height) : RackingEurocode(solid, height, kmod * nail / 1.3, 150);
            yield return new StrengthCheck("Racking", racked, carries, "kN", us ? "SDPWS 4.3" : "9.2.4.2", $"{solid.Sum() / 1000:0.0} m of it with no opening");
        }

        var worst = MemberStrength.Worst(bands.Select(entry => (entry.Combination.Name, Under(entry.Combination, entry.Bands)))).ToList();
        var taken = drawn ? string.Empty : $"No frame is drawn for it: {section.Name} studs at {spacing:0} are taken. ";
        var sheet = us ? "7/16 in sheathing with 8d nails at 6 in round each sheet" : "9 mm OSB with 3.1 by 50 nails at 150 round each sheet";
        return new WallCheck(panel, $"{section.Name} at {spacing:0} centres", us ? "the NDS and the SDPWS, LRFD" : "BS EN 1995-1-1 and its UK National Annex", worst, lengths.Strong, depth,
            $"{taken}Studs held across every {lengths.Weak:0} by what is fixed to their faces, the wind on them between their plates. Racking is the wind's share of the shear at its foot, taken on {sheet} on one face, each end tied down: the force in the tie-downs is not worked out. The plate standing for the wall spans sideways to its corners, as studs do not, so more of the wind on the walls across it comes through it than the half a hand sum would send, which is on the safe side.");
    }
}

/// <summary>
/// A concrete wall, its load and moments read a metre at a time. BS EN 1992-1-1: each metre as a column of the wall's thickness, with its slenderness, the steel it comes to and at least 0.002 Ac (9.6.2); in-plane shear for the wall
/// as a whole by 6.2.2. ACI 318-19: the simplified method of 11.5.3 with the load in the middle third, else as a column; in-plane shear by 11.5.4.5. The model holds no bars for it, so what it comes to is said.
/// </summary>
public static class ConcreteWallDesign
{
    private const double Psi = 0.00689476;

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

        var worst = MemberStrength.Worst(bands.Select(entry => (entry.Combination.Name, Under(entry.Bands)))).ToList();
        var flat = panel.FlatOutline;
        if (us) worst.Insert(0, new StrengthCheck("Least thickness", Math.Max(100, Math.Min(height, flat.Max(p => p.X) - flat.Min(p => p.X)) / 25), t, "mm", "Table 11.3.1.1", "a bearing wall's, unless analysis shows a thinner one stands"));
        return new WallCheck(panel, panel.Material.Quality, us ? "ACI 318-19" : "BS EN 1992-1-1 and its UK National Annex", worst, effective, t,
            $"No bars are in the model: the steel said is what each metre of it comes to, each face half, with {(us ? "at least 0.0020" : "a quarter of it or 0.001, the greater,")} along it. Taken as pinned top and foot.{(us ? string.Empty : " Its slenderness goes in by nominal curvature: BS EN 1992-1-1 limits a plain wall's, 12.6.5.1(5), and not a reinforced one's.")}");
    }
}
