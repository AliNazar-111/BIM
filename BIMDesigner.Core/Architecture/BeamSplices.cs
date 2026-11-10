using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A bolted cover plate beam splice's layout, mm, as SCI P398's Example D.1 draws one: an outer cover plate on each flange <paramref name="FlangeWidth"/> wide and <paramref name="FlangePlate"/>
/// thick with two bolts across it in <paramref name="FlangeRows"/> rows each side of the joint; two web cover plates <paramref name="WebPlate"/> thick and <paramref name="WebDepth"/> deep with
/// <paramref name="WebLines"/> lines of <paramref name="WebRows"/> bolts each side. Its spacings follow the bolt's hole: e1 2.75 d0, p1 3.6 d0, e2 1.4 d0 on the flanges; on the web 5.5 d0
/// up it, 2.3 d0 to its edges, the first line 3.2 d0 from the joint and the next 3.9 d0 on - 60, 80, 30, 120, 50, 70 and 85 for an M20.
/// </summary>
public sealed record SpliceLayout(double Bolt, double FlangePlate, double FlangeWidth, int FlangeRows, double WebPlate, double WebDepth, int WebRows, int WebLines, bool Us = false, double WebSpacing = 0)
{
    public double Hole => Us ? Bolt + 1.6 : Bolt <= 24 ? Bolt + 2 : Bolt + 3;

    public double TensileArea => Us ? Math.PI * Bolt * Bolt / 4 : Bolt switch { <= 16 => 157, <= 20 => 245, <= 24 => 353, _ => 561 };

    /// <summary>Rounded to the nearest 10 or 5 mm, or in the US the quarter inch.</summary>
    private double Round(double value, double to) => Us ? Math.Round(value / 6.35) * 6.35 : Math.Round(value / to) * to;

    public double End => Round(2.75 * Hole, 10);

    public double Pitch => Round(3.6 * Hole, 10);

    public double Edge => Round(1.4 * Hole, 5);

    /// <summary>The web bolts' spacing up the web: 5.5 d0, or closer where two rows want it on a shallow beam.</summary>
    public double WebPitch => WebSpacing > 0 ? WebSpacing : Round(5.5 * Hole, 10);

    public double WebEdge => Round(2.3 * Hole, 10);

    public double WebFirst => Round(3.2 * Hole, 10);

    public double WebGauge => Round(3.9 * Hole, 5);

    /// <summary>The flange bolts' gauge across the plate.</summary>
    public double Across => FlangeWidth - 2 * Edge;

    /// <summary>The web bolt group's centroid from the joint's centreline: the eccentricity of the shear.</summary>
    public double Eccentricity => WebFirst + (WebLines - 1) * WebGauge / 2;

    public double FlangeLength => 2 * (End + (FlangeRows - 1) * Pitch + End);

    public double WebLength => 2 * (WebFirst + (WebLines - 1) * WebGauge + WebEdge);
}

/// <summary>What a splice carries at it, kN and kNm: the moment, the shear and the compression in the beam, under a combination, and whether it is a serviceability one.</summary>
public sealed record SpliceForces(string Combination, double Moment, double Shear, double Compression, bool Service = false);

/// <summary>
/// Bolted cover plate beam splices: where two steel beams meet in line with their ends fixed to each other and nothing else there. In the UK by SCI P398 Section 4 on BS EN 1993-1-8,
/// preloaded 8.8 bolts in Category B, slip resistant in service and bearing at the ultimate: the forces shared between the flanges and the web by the web's share of I and of A, P398 STEP 1;
/// the flange bolts in single shear and bearing, long joints by βLf, and their slip, ks μ Fp,C / γM3,ser with a Class A surface; the flange cover plate in tension, gross, net with γM2 1.1 and
/// block tearing by (3.9), and in compression with its buckling between bolts past 9 ε t; the web bolts, each the resultant of the shear, the axial force and the web's moment with the shear's
/// eccentricity, in double shear and bearing on the web and the plates, and their slip; the web plates in shear, gross by 1.27, net and by block tearing, and in bending with axial force by
/// 6.2.9 and 6.2.10; the beam's web net in shear; and STEP 6's minimum, a quarter of the beam's moment resistance alone. In the US by AISC 360-16 with A325-N bolts in bearing (J3.6, J3.10),
/// the plates by J4.1 to J4.4 and H1.
/// </summary>
public static class BeamSplices
{
    /// <summary>The checks of a layout under its forces, the worst of each kept, in the beam's own steel.</summary>
    public static IReadOnlyList<StrengthCheck> Checks(SteelSection beam, double fy, double fu, SpliceLayout s, IReadOnlyList<SpliceForces> forces)
    {
        var us = s.Us;
        var (fyp, fup) = us ? (248.0, 400.0) : (s.FlangePlate <= 16 ? 275.0 : 265.0, 410.0);
        var (fywp, fuwp) = us ? (248.0, 400.0) : (s.WebPlate <= 16 ? 275.0 : 265.0, 410.0);
        var d0 = s.Hole;
        var eps = Math.Sqrt(235 / fyp);
        var hw = beam.H - 2 * beam.Tf;
        var webShare = Math.Pow(hw, 3) * beam.Tw / 12 / beam.Iy;
        var axialShare = hw * beam.Tw / beam.Area;

        // The bolts. UK: 0.6 fub As / γM2 a shear plane, bearing by Table 3.4 at its spacings, slip 0.5 x 0.7 fub As / 1.1 a surface; a long joint past 15 d by βLf. US: 0.75 x 54 ksi Ab a plane,
        // bearing and tearout φ 0.75 (1.2 lc t Fu, 2.4 d t Fu).
        var plane = us ? 0.75 * 372 * s.TensileArea / 1000 : 0.6 * 800 * s.TensileArea / 1.25 / 1000;
        var slip = 0.5 * 0.7 * 800 * s.TensileArea / 1.1 / 1000;
        double Bearing(double t, double fuOf, double end, double pitch, double edge, double across)
        {
            if (us) return 0.75 * Math.Min(1.2 * Math.Min(end - d0 / 2, pitch - d0) * t * fuOf, 2.4 * s.Bolt * t * fuOf) / 1000;
            var alpha = Math.Min(Math.Min(end / (3 * d0), pitch / (3 * d0) - 0.25), Math.Min(800 / fuOf, 1));
            var k1 = Math.Min(Math.Min(2.8 * edge / d0 - 1.7, 1.4 * across / d0 - 1.7), 2.5);
            return k1 * alpha * fuOf * s.Bolt * t / 1.25 / 1000;
        }

        var joint = (s.FlangeRows - 1) * s.Pitch;
        var longJoint = us || joint <= 15 * s.Bolt ? 1 : Math.Clamp(1 - (joint - 15 * s.Bolt) / (200 * s.Bolt), 0.75, 1);
        var flangeBolts = 2 * s.FlangeRows;
        var flangeEach = Math.Min(longJoint * plane, Math.Min(Bearing(s.FlangePlate, fup, s.End, s.Pitch, s.Edge, s.Across), Bearing(beam.Tf, fu, s.End, s.Pitch, (beam.B - s.Across) / 2, s.Across)));
        var webBolts = s.WebRows * s.WebLines;
        var webSpacing = Math.Min(s.WebPitch, s.WebGauge);
        var webEach = Math.Min(2 * plane, Math.Min(Bearing(beam.Tw, fu, s.WebFirst - 5, webSpacing, s.WebFirst - 5, webSpacing), Bearing(2 * s.WebPlate, fuwp, s.WebEdge, webSpacing, s.WebEdge, webSpacing)));

        // The plates. The flange's cover plate in tension, gross, net and by block tearing; in compression, gross, or buckling over 0.6 p1 across the joint where that is more than 9 ε t.
        var area = s.FlangeWidth * s.FlangePlate;
        var net = (s.FlangeWidth - 2 * d0) * s.FlangePlate;
        var (ant, anv) = (s.FlangePlate * (2 * s.Edge - d0), 2 * s.FlangePlate * ((s.FlangeRows - 1) * s.Pitch + s.End - (s.FlangeRows - 0.5) * d0));
        var (agv, ag) = (2 * s.FlangePlate * ((s.FlangeRows - 1) * s.Pitch + s.End), area);
        var inTension = us
            ? Math.Min(Math.Min(0.9 * fyp * ag, 0.75 * fup * Math.Min(net, 0.85 * ag)), 0.75 * Math.Min(0.6 * fup * anv + fup * ant, 0.6 * fyp * agv + fup * ant)) / 1000
            : Math.Min(Math.Min(area * fyp, 0.9 * net * fup / 1.1), fup * ant / 1.1 + fyp * anv / Math.Sqrt(3)) / 1000;
        var flangeNet = us ? Math.Min(0.9 * fy * beam.B * beam.Tf, 0.75 * fu * Math.Min((beam.B - 2 * d0) * beam.Tf, 0.85 * beam.B * beam.Tf)) / 1000
            : Math.Min(beam.B * beam.Tf * fy, 0.9 * (beam.B - 2 * d0) * beam.Tf * fu / 1.1) / 1000;
        var across = 2 * s.End;
        var slender = 0.6 * across / (s.FlangePlate / Math.Sqrt(12)) / (93.9 * eps);
        // US: J4.4 up to KL/r 25, past it E3 with K 0.65.
        var kl = 0.65 * across / (s.FlangePlate / Math.Sqrt(12));
        var euler = Math.PI * Math.PI * 200000 / (kl * kl);
        var aisc = kl <= 25 ? 0.9 : 0.9 * (kl <= 4.71 * Math.Sqrt(200000 / fyp) ? Math.Pow(0.658, fyp / euler) : 0.877 * euler / fyp);
        var inCompression = (us ? aisc : across / s.FlangePlate > 9 * eps ? SteelDesign.Chi(0.49, slender) : 1) * area * fyp / 1000;

        // The web's cover plates, two: shear gross, net and by block tearing; bending with axial force. The beam's own web net in shear.
        var (hwp, twp) = (s.WebDepth, s.WebPlate);
        var gross = us ? 2 * 1.0 * 0.6 * fywp * hwp * twp / 1000 : 2 * hwp * twp * fywp / (1.27 * Math.Sqrt(3)) / 1000;
        var webNet = us ? 2 * 0.75 * 0.6 * fuwp * (hwp - s.WebRows * d0) * twp / 1000 : 2 * (hwp - s.WebRows * d0) * twp * fuwp / (1.1 * Math.Sqrt(3)) / 1000;
        var (wAnt, wAnv) = (twp * (s.WebEdge - d0 / 2), twp * (hwp - s.WebEdge - (s.WebRows - 0.5) * d0));
        var webTearing = us ? 2 * 0.75 * Math.Min(0.6 * fuwp * wAnv + fuwp * wAnt, 0.6 * fywp * twp * (hwp - s.WebEdge) + fuwp * wAnt) / 1000
            : 2 * (fuwp * wAnt / 1.1 + fywp * wAnv / Math.Sqrt(3)) / 1000;
        var webShear = Math.Min(gross, Math.Min(webNet, webTearing));
        var av = Math.Max(beam.Area - 2 * beam.B * beam.Tf + (beam.Tw + 2 * beam.Root) * beam.Tf, hw * beam.Tw);
        var beamNet = us ? 0.75 * 0.6 * fu * (beam.H * beam.Tw - s.WebRows * d0 * beam.Tw) / 1000 : (av - s.WebRows * d0 * beam.Tw) * fu / (1.1 * Math.Sqrt(3)) / 1000;
        var squash = 2 * hwp * twp * fywp / 1000 * (us ? 0.9 : 1);
        var bending = 2 * twp * hwp * hwp / (us ? 4 : 6) * fywp / 1e6 * (us ? 0.9 : 1);

        // The web bolt group: its polar moment about its centroid.
        var zs = Enumerable.Range(0, s.WebRows).Select(i => (i - (s.WebRows - 1) / 2.0) * s.WebPitch).ToList();
        var xs = Enumerable.Range(0, s.WebLines).Select(i => (i - (s.WebLines - 1) / 2.0) * s.WebGauge).ToList();
        var polar = xs.Sum(x => zs.Sum(z => x * x + z * z));
        var (zMax, xMax) = (zs.Max(Math.Abs), xs.Max(Math.Abs));

        var worst = new Dictionary<string, StrengthCheck>();
        void Keep(StrengthCheck check)
        {
            if (!worst.TryGetValue(check.Name, out var was) || check.Ratio > was.Ratio) worst[check.Name] = check;
        }

        foreach (var f in forces)
        {
            // P398 STEP 1: the flanges' share of the moment and of the axial force, the web's, and the shear's eccentricity.
            var flangeMoment = (1 - webShare) * Math.Abs(f.Moment) * 1000 / (beam.H - beam.Tf);
            var flangeAxial = (1 - axialShare) * f.Compression / 2;
            var (pulled, pushed) = (flangeMoment - flangeAxial, flangeMoment + flangeAxial);
            var webMoment = webShare * Math.Abs(f.Moment) + f.Shear * s.Eccentricity / 1000;
            var webAxial = axialShare * f.Compression;
            var horizontal = Math.Abs(webAxial) / webBolts + webMoment * 1e3 * zMax / polar;
            var vertical = f.Shear / webBolts + webMoment * 1e3 * xMax / polar;
            var resultant = Math.Sqrt(horizontal * horizontal + vertical * vertical);
            if (f.Service)
            {
                if (us) continue;
                Keep(new StrengthCheck("Flange bolts slipping", Math.Max(Math.Abs(pulled), Math.Abs(pushed)), flangeBolts * longJoint * slip, "kN", "BS EN 1993-1-8 3.9.1, Category B", $"{flangeBolts} preloaded M{s.Bolt:0} 8.8 in single shear, Class A, {slip:0.0} kN each", f.Combination));
                Keep(new StrengthCheck("Web bolts slipping", resultant, 2 * slip, "kN", "3.9.1, Category B", "the most loaded, in double shear", f.Combination));
                continue;
            }

            var (bolts, plates, beamWeb, clause) = us ? ("A325-N", "J3.6, J3.10", "J4.2", "AISC 360-16") : ("8.8", "Table 3.4", "6.2.6", "BS EN 1993-1-8");
            Keep(new StrengthCheck("Flange bolts", Math.Max(Math.Abs(pulled), Math.Abs(pushed)), flangeBolts * flangeEach, "kN", $"{clause} {plates}",
                $"{flangeBolts} {(us ? $"{s.Bolt / 25.4:0.###} in" : $"M{s.Bolt:0}")} {bolts} in single shear{(longJoint < 1 ? $", βLf {longJoint:0.00}" : string.Empty)}", f.Combination));
            Keep(new StrengthCheck("Tension flange cover plate", Math.Max(0, pulled), inTension, "kN", us ? "J4.1, J4.3" : "BS EN 1993-1-1 6.2.3, 3.10.2", "gross, net and block tearing", f.Combination));
            Keep(new StrengthCheck("Tension flange, net", Math.Max(0, pulled), flangeNet, "kN", us ? "J4.1" : "BS EN 1993-1-1 6.2.3", "the beam's own flange through two holes", f.Combination));
            Keep(new StrengthCheck("Compression flange cover plate", Math.Max(0, pushed), inCompression, "kN", us ? "J4.4" : "P398 STEP 4", $"{across:0} across the joint, {s.FlangePlate:0} thick", f.Combination));
            Keep(new StrengthCheck("Web bolts", resultant, webEach, "kN", $"{clause} {plates}", $"the most loaded of {webBolts} each side, e {s.Eccentricity:0} mm, in double shear and bearing", f.Combination));
            Keep(new StrengthCheck("Web cover plates in shear", f.Shear, webShear, "kN", us ? "J4.2, J4.3" : "P398 STEP 5", "gross, net and block tearing", f.Combination));
            var reduced = !us && f.Shear > 0.5 * webShear ? 1 - Math.Pow(2 * f.Shear / webShear - 1, 2) : 1;
            var interaction = us
                ? (webAxial / squash >= 0.2 ? webAxial / squash + 8.0 / 9 * webMoment / bending : webAxial / (2 * squash) + webMoment / bending)
                : Math.Abs(webAxial) / squash + webMoment / (bending * reduced);
            Keep(new StrengthCheck("Web cover plates, bending and axial", interaction, 1, "", us ? "H1.1" : "BS EN 1993-1-1 6.2.9, 6.2.10", $"{webMoment:0.0} kNm and {webAxial:0.0} kN", f.Combination));
            Keep(new StrengthCheck("Beam's web net in shear", f.Shear, beamNet, "kN", us ? "J4.2(b)" : $"BS EN 1993-1-1 {beamWeb}", $"{s.WebRows} holes", f.Combination));
        }

        return worst.Values.ToList();
    }

    /// <summary>The layouts tried, cheapest first: the smaller bolt, the fewest flange rows, one web line then two, the thinnest plates no thinner than the flange and half the web.</summary>
    public static IEnumerable<SpliceLayout> Layouts(SteelSection beam, bool us)
    {
        var bolts = us ? new[] { 19.05, 22.225, 25.4 } : new[] { 20.0, 24 };
        var plates = us ? new[] { 9.525, 12.7, 15.875, 19.05, 25.4 } : new[] { 10.0, 12, 15, 20, 25 };
        var flangePlate = plates.FirstOrDefault(t => t >= beam.Tf - 0.5, plates[^1]);
        foreach (var bolt in bolts)
        {
            var hole = us ? bolt + 1.6 : bolt <= 24 ? bolt + 2 : bolt + 3;
            var step = us ? 6.35 : 10;
            var width = Math.Floor(beam.B / step) * step;
            if (width - 2 * Math.Round(1.4 * hole / (us ? 6.35 : 5)) * (us ? 6.35 : 5) < 2.5 * hole) continue;
            // The web plates as deep as their rows of bolts ask, inside the web's root radii with 10 to spare each side: two rows at the least, closed up to 2.4 d0 on a shallow beam.
            var room = beam.H - 2 * (beam.Tf + beam.Root) - 20;
            var webEdge = Math.Round(2.3 * hole / step) * step;
            var webPitch = Math.Round(5.5 * hole / step) * step;
            var rows = (int)Math.Floor((room - 2 * webEdge) / webPitch) + 1;
            if (rows < 2)
            {
                webPitch = Math.Floor((room - 2 * webEdge) / (us ? 6.35 : 5)) * (us ? 6.35 : 5);
                rows = 2;
                if (webPitch < 2.4 * hole) continue;
            }

            var depth = 2 * webEdge + (rows - 1) * webPitch;
            foreach (var flangeRows in new[] { 2, 3, 4, 5, 6 })
                foreach (var lines in new[] { 1, 2 })
                    foreach (var web in plates.Where(t => t >= Math.Max(us ? 6.35 : 8, beam.Tw / 2)).Take(3))
                        foreach (var flange in plates.Where(t => t >= flangePlate).Take(2))
                            yield return new SpliceLayout(bolt, flange, width, flangeRows, web, depth, rows, lines, us, webPitch);
        }
    }

    private static MemberRelease Release(AnalyticalMember member, AnalyticalNode node) => ReferenceEquals(node, member.Start) ? member.StartRelease : member.EndRelease;

    /// <summary>Two steel beams meeting in line at a node, both fixed there, with no column at it: a splice.</summary>
    internal static IEnumerable<JointCheck> Of(BimDocument document, SecondOrderAnalysis second, bool us)
    {
        var model = second.Analysis.Model;
        var beams = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Beam && member.Section.Material.Kind == AnalyticalMaterialKind.Steel && SteelSection.Of(member.Section) is { IsI: true }).ToList();
        var done = new HashSet<AnalyticalNode>();
        foreach (var one in beams)
            foreach (var node in new[] { one.Start, one.End })
            {
                if (Release(one, node) != MemberRelease.Fixed || done.Contains(node)) continue;
                if (model.Members.Any(member => member.Kind == AnalyticalMemberKind.Column && member.Nodes.Contains(node))) continue;
                var away = ReferenceEquals(node, one.Start) ? one.End : one.Start;
                var other = beams.FirstOrDefault(each => !ReferenceEquals(each, one) && (ReferenceEquals(each.Start, node) || ReferenceEquals(each.End, node)) && Release(each, node) == MemberRelease.Fixed
                    && InLine(node, away, ReferenceEquals(node, each.Start) ? each.End : each.Start));
                if (other is null) continue;
                done.Add(node);
                var (a, b) = (SteelSection.Of(one.Section)!, SteelSection.Of(other.Section)!);
                var beam = a.Wply <= b.Wply ? a : b;
                var differs = Math.Abs(a.H - b.H) > 5 || Math.Abs(a.B - b.B) > 5;
                var (fy, fu) = us ? (345.0, 448.0) : (SteelDesign.YieldStrength(beam.Tf), 470.0);
                var forces = MomentJoints.AtNode(second, one, node).Where(each => each.Combination.Factored)
                    .Select(each => new SpliceForces(each.Combination.Name, each.Forces.My, Math.Abs(each.Forces.Vz), -each.Forces.N)).ToList();

                // In service, for the bolts' slip, from the analysis as it is: the end of the piece at the splice under each serviceability combination.
                var analysis = second.Analysis;
                var at = analysis.NodeOf(node);
                var piece = analysis.BeamsOf(one).Select(each => each.Beam).First(each => analysis.Solver.Beams[each].I == at || analysis.Solver.Beams[each].J == at);
                var atStart = analysis.Solver.Beams[piece].I == at;
                if (!us)
                    foreach (var combination in analysis.Loading.Combinations.Where(each => each.State == LimitState.Serviceability))
                    {
                        var f = analysis.BeamForces(piece, combination);
                        var (axial, shear, moment) = atStart ? (-f[0], f[2], -f[4]) : (f[6], -f[8], f[10]);
                        forces.Add(new SpliceForces(combination.Name, moment / 1e6, Math.Abs(shear) / 1e3, -axial / 1e3, Service: true));
                    }
                var continuity = 0.25 * beam.Wply * fy / 1e6;
                forces.Add(new SpliceForces("a quarter of Mc,y,Rd", continuity, 0, 0));
                yield return Design($"{one.Name} to {other.Name}", beam, fy, fu, forces, us, differs, continuity);
            }
    }

    private static bool InLine(AnalyticalNode at, AnalyticalNode one, AnalyticalNode other)
    {
        var (ax, ay, az) = (one.At.X - at.At.X, one.At.Y - at.At.Y, one.At.Z - at.At.Z);
        var (bx, by, bz) = (other.At.X - at.At.X, other.At.Y - at.At.Y, other.At.Z - at.At.Z);
        var cos = (ax * bx + ay * by + az * bz) / Math.Sqrt((ax * ax + ay * ay + az * az) * (bx * bx + by * by + bz * bz));
        return cos < -0.995;
    }

    internal static JointCheck Design(string name, SteelSection beam, double fy, double fu, IReadOnlyList<SpliceForces> forces, bool us, bool differs, double continuity)
    {
        var layouts = Layouts(beam, us).ToList();
        if (layouts.Count == 0)
            return new JointCheck(name, "Beam splice", Array.Empty<StrengthCheck>(), string.Empty, "No cover plate layout fits this beam: not worked out.");
        var tried = layouts.Select(layout => (Layout: layout, Checks: Checks(beam, fy, fu, layout, forces))).ToList();
        var (chosen, checks) = tried.FirstOrDefault(each => each.Checks.All(check => check.Ratio <= 1));
        if (chosen is null) (chosen, checks) = tried.MinBy(each => each.Checks.Max(check => check.Ratio));
        var bolt = us ? $"{chosen.Bolt / 25.4:0.###} in A325-N" : $"preloaded M{chosen.Bolt:0} 8.8";
        var size = (double mm) => us ? $"{mm / 25.4:0.###} in" : $"{mm:0}";
        var over = checks.Any(check => check.Ratio > 1);
        return new JointCheck(name, "Beam splice", checks,
            $"flange cover plates {size(chosen.FlangeLength)} by {size(chosen.FlangeWidth)} by {size(chosen.FlangePlate)} with {2 * chosen.FlangeRows} {bolt} each side, two web plates {size(chosen.WebLength)} by {size(chosen.WebDepth)} by {size(chosen.WebPlate)} " +
            $"with {chosen.WebLines * chosen.WebRows} each side{(us ? string.Empty : ", Class A faying surfaces")}{(over ? "; and more than these layouts give - an end plate splice or a welded one" : string.Empty)}",
            (us ? "By AISC 360-16, the bolts in bearing; whether they need to be slip-critical is not worked out. " : "By SCI P398 Section 4: Category B, slip resistant in service, in bearing at the ultimate. ") +
            $"At least a quarter of the beam's moment resistance, {continuity:0} kNm, as P398 STEP 6 asks. {(differs ? "The two beams differ in size: packs to make up the difference are not worked out, the lighter taken. " : string.Empty)}" +
            "Its minor axis moment, where it is away from a restraint, is not worked out.");
    }
}
