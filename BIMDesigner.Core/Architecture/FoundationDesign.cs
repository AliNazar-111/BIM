using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>A footing or pile checked: what it is, and its checks; and what is left unchecked, and why.</summary>
public sealed record FoundationCheck(Element Foundation, string Name, IReadOnlyList<StrengthCheck> Checks, string Limits)
{
    public double Ratio => Checks.Count == 0 ? 0 : Checks.Max(check => check.Ratio);

    public bool Enough => Ratio <= 1 + 1e-9;
}

/// <summary>
/// Strip and pad footings and piles against what the analysis puts on them. Under every load but the wind at its characteristic value:
/// a footing's pressure, its own weight in, against the ground's presumed bearing value - with a fixed pad's moment, the pressure at its edge - and a pile's
/// load against what the alpha method gives it in clay. Under the ultimate combinations, a footing as concrete: reinforced by BS EN 1992-1-1 or ACI 318-19, its
/// bending at the face of what stands on it, its shear and a pad's punching; plain by BS EN 1992-1-1 12.9.3 or ACI 318-19 Chapter 14. Reinforced concrete
/// is taken as the ground beams' is, RC28/35 or 3,000 psi; plain as C16/20 or 2,500 psi, the model holding no grade. First order, at full stiffness.
/// </summary>
public static class FoundationDesign
{
    /// <summary>What these checks take, for whoever reads them.</summary>
    public const string Said = "Footings as concrete: reinforced taken as RC28/35 or 3,000 psi and plain as C16/20 or 2,500 psi, unless the footing's type sets its own; the pier's face stands for a column's. A strip by its heaviest metre, a beam landing on the wall above being followed down through the wall as a plate. Settlement is an estimate from a presumed stiffness of the ground, the supports in the analysis not giving way: a site investigation to say. Its limits are those of BS EN 1997-1 Annex H as remembered, 50 mm and 1 in 500.";

    /// <summary>What a cubic metre of concrete weighs, kN.</summary>
    private const double Concrete = 24;

    private const double Psi = 0.00689476;

    /// <summary>
    /// The depth a plain footing wants by BS EN 1992-1-1 12.9.3, mm: 0.85 hF/a ≥ √(3 σgd / fctd,pl) as corrected (the first printing had 9), or the simpler hF/a ≥ 2 if that is less.
    /// fctd,pl = αct,pl fctk,0.05 / γc, αct,pl the recommended 0.8, as the Concrete Centre's table of this rule works out.
    /// </summary>
    public static double PlainDepthEurocode(double pressure, double projection, double fck)
    {
        var tension = 0.8 * LowerTension(fck) / 1.5;
        return Math.Min(projection * Math.Sqrt(3 * pressure / tension) / 0.85, 2 * projection);
    }

    /// <summary>BS EN 1992-1-1 Table 3.1's fctk,0.05, N/mm², the class at or under fck.</summary>
    private static double LowerTension(double fck)
    {
        (double Fck, double Tension)[] table = { (12, 1.1), (16, 1.3), (20, 1.5), (25, 1.8), (30, 2.0), (35, 2.2), (40, 2.5), (45, 2.7), (50, 2.9) };
        return table.LastOrDefault(row => row.Fck <= fck + 1e-9, table[0]).Tension;
    }

    /// <summary>A strip under its line of ultimate load, kN/m, standing out so far past what is on it, by BS EN 1992-1-1. Its steel, mm²/m across it, 0 where it is plain that way.</summary>
    public static IEnumerable<StrengthCheck> StripEurocode(double line, double width, double projection, double thickness, double d, double steel, double fck)
    {
        var pressure = line / (width / 1000);
        if (steel <= 0)
        {
            yield return new StrengthCheck("Depth as plain concrete", PlainDepthEurocode(pressure / 1000, projection, fck), thickness, "mm", "12.9.3", $"{projection:0} mm out past its wall at {pressure:0} kN/m², C16/20");
            yield break;
        }

        foreach (var check in BendingAndShearEurocode(pressure, projection, d, steel, fck, "wall's", "")) yield return check;
    }

    /// <summary>A strip by ACI 318-19. On masonry its moment is taken halfway from the wall's middle to its face (Table 13.2.7.1); plain, by Chapter 14 at 50 mm under its thickness.</summary>
    public static IEnumerable<StrengthCheck> StripAci(double line, double width, double projection, double thickness, double d, double steel, double fc)
    {
        var pressure = line / (width / 1000);
        var root = Math.Sqrt(fc / Psi) * Psi;
        if (steel <= 0)
        {
            foreach (var check in PlainAci(pressure, projection, thickness, root, "x")) yield return check with { Name = check.Name.Replace(", x way", string.Empty, StringComparison.Ordinal) };
            yield break;
        }

        foreach (var check in BendingAndShearAci(pressure, projection, thickness, d, steel, fc, "")) yield return check;
    }

    /// <summary>
    /// A pad under its ultimate load, kN, and any moment at its base, kNm, by BS EN 1992-1-1: bending at the face each way, shear at d each way and punching at every control perimeter within 2d (6.4.4(2)), each with what the
    /// ground pushes back inside it taken off and 2d/a on what the concrete carries; (6.51) for a moment. Its steel, mm²/m each way, 0 where it is plain.
    /// </summary>
    public static IEnumerable<StrengthCheck> PadEurocode(double force, double moment, double c1, double c2, double width, double length, double thickness, double d, double steel, double fck)
    {
        var area = width * length / 1e6;
        var narrow = Math.Min(width, length) / 1000;
        var pressure = force / area + moment / (Math.Max(width, length) / 1000 * narrow * narrow / 6);
        var (aWide, aLong) = ((width - c1) / 2, (length - c2) / 2);
        if (steel <= 0)
        {
            yield return new StrengthCheck("Depth as plain concrete", PlainDepthEurocode(pressure / 1000, Math.Max(aWide, aLong), fck), thickness, "mm", "12.9.3", $"{Math.Max(aWide, aLong):0} mm out past its pier at {pressure:0} kN/m², C16/20");
            yield break;
        }

        foreach (var check in BendingAndShearEurocode(pressure, aWide, d, steel, fck, "pier's", ", width way")) yield return check;
        foreach (var check in BendingAndShearEurocode(pressure, aLong, d, steel, fck, "pier's", ", length way")) yield return check;

        // Punching: the worst perimeter out to 2d that stays on the pad.
        var vRdc = PlateDesign.OneWayEurocode(d, steel / (1000 * d), fck);
        var k = (c1 / Math.Max(c2, 1)) switch { <= 0.5 => 0.45, <= 1 => 0.6, <= 2 => 0.7, _ => 0.8 };
        StrengthCheck? worst = null;
        for (var step = 2; step <= 40; step++)
        {
            var a = step * d / 20;
            if (c1 / 2 + a >= width / 2 || c2 / 2 + a >= length / 2) break;
            var (v, u, left) = PadPunchingAt(force, moment, c1, c2, area, d, a, k);
            if (left <= 0) continue;
            var carries = vRdc * 2 * d / a;
            if (worst is null || v / carries > worst.Ratio)
                worst = new StrengthCheck("Punching within 2d", v, carries, "N/mm²", "6.4.4(2)", $"{a:0} mm out, u {u:0} mm, {left:0} of {force:0} kN left");
        }

        if (worst is not null) yield return worst;
        yield return new StrengthCheck("Punching at the pier's face", force * 1000 / (2 * (c1 + c2) * d), 0.4 * 0.6 * (1 - fck / 250) * fck / 1.5, "N/mm²", "6.4.5(3)", $"u0 {2 * (c1 + c2):0} mm");
    }

    /// <summary>
    /// The shear on a pad's control perimeter a from its column, N/mm²: (6.48) to (6.51), the ground's push inside it taken off a load spread evenly over its area, m².
    /// With it, the perimeter, mm, and the force left, kN. W is (6.41)'s, with a for 2d.
    /// </summary>
    public static (double Stress, double Perimeter, double Left) PadPunchingAt(double force, double moment, double c1, double c2, double area, double d, double a, double k)
    {
        var u = 2 * (c1 + c2) + 2 * Math.PI * a;
        var inside = (c1 * c2 + 2 * a * (c1 + c2) + Math.PI * a * a) / 1e6;
        var left = force - force / area * inside;
        var w = c1 * c1 / 2 + c1 * c2 + 2 * c2 * a + 4 * a * a + Math.PI * a * c1;
        return (left * 1000 / (u * d) * (1 + (left <= 0 ? 0 : k * moment * u / (left * w) * 1000)), u, left);
    }

    /// <summary>A pad by ACI 318-19: bending at the face and one-way shear at d each way and two-way at d/2, λs 1 (13.2.6.2); plain, by Chapter 14 at 50 mm under its thickness.</summary>
    public static IEnumerable<StrengthCheck> PadAci(double force, double moment, double c1, double c2, double width, double length, double thickness, double d, double steel, double fc)
    {
        var area = width * length / 1e6;
        var narrow = Math.Min(width, length) / 1000;
        var pressure = force / area + moment / (Math.Max(width, length) / 1000 * narrow * narrow / 6);
        var (aWide, aLong) = ((width - c1) / 2, (length - c2) / 2);
        var root = Math.Sqrt(fc / Psi) * Psi;
        var beta = Math.Max(c1, c2) / Math.Max(1, Math.Min(c1, c2));
        if (steel <= 0)
        {
            foreach (var check in PlainAci(pressure, aWide, thickness, root, "width")) yield return check;
            foreach (var check in PlainAci(pressure, aLong, thickness, root, "length")) yield return check;

            var h = thickness - 50;
            var outer = 2 * (c1 + h) + 2 * (c2 + h);
            var left = force - force / area * ((c1 + h) * (c2 + h) / 1e6);
            if (c1 + h < width && c2 + h < length) yield return new StrengthCheck("Two-way shear", left, 0.6 * Math.Min(1 + 2 / beta, 2) * 4.0 / 3 * root * outer * h / 1000, "kN", "14.5.5.1", $"at h/2, bo {outer:0} mm, h {h:0} mm, φ 0.6");
            yield break;
        }

        foreach (var check in BendingAndShearAci(pressure, aWide, thickness, d, steel, fc, ", width way")) yield return check;
        foreach (var check in BendingAndShearAci(pressure, aLong, thickness, d, steel, fc, ", length way")) yield return check;

        var bo = 2 * (c1 + d) + 2 * (c2 + d);
        var reduced = force - force / area * ((c1 + d) * (c2 + d) / 1e6);
        var vc = Math.Min(4, Math.Min(2 + 4 / beta, 2 + 40 * d / bo)) * root;
        if (c1 + d < width && c2 + d < length) yield return new StrengthCheck("Two-way shear", reduced * 1000 / (bo * d), 0.75 * vc, "N/mm²", "22.6.5.2", $"at d/2, bo {bo:0} mm, {reduced:0} of {force:0} kN left, λs 1");
    }

    private static IEnumerable<StrengthCheck> BendingAndShearEurocode(double pressure, double projection, double d, double steel, double fck, string face, string way)
    {
        var moment = pressure * Math.Pow(projection / 1000, 2) / 2;
        var (needed, problem) = ConcreteDesign.Eurocode(moment, 1000, d, fck, ConcreteDesign.LeastEurocode(1000, d, fck));
        yield return new StrengthCheck($"Bottom steel{way}", needed, steel, "mm²/m", "6.1, 9.2.1.1", problem ?? $"{moment:0.0} kNm/m at the {face} face, d {d:0} mm");
        if (projection <= d) yield break;
        var carries = PlateDesign.OneWayEurocode(d, steel / (1000 * d), fck) * d;
        yield return new StrengthCheck($"Shear at d{way}", pressure * (projection - d) / 1000, carries, "kN/m", "6.2.2", $"{pressure:0} kN/m² on what stands out past d");
    }

    private static IEnumerable<StrengthCheck> BendingAndShearAci(double pressure, double projection, double thickness, double d, double steel, double fc, string way)
    {
        var moment = pressure * Math.Pow(projection / 1000, 2) / 2;
        var (needed, problem) = ConcreteDesign.Aci(moment, 1000, d, fc, 0.0018 * 1000 * thickness);
        yield return new StrengthCheck($"Bottom steel{way}", needed, steel, "mm²/m", "22.2, 7.6.1.1", problem ?? (d < 150 ? "d is under 150 mm (13.3.1.2)" : $"{moment:0.0} kNm/m at its critical section, d {d:0} mm"));
        if (projection > d) yield return new StrengthCheck($"Shear at d{way}", pressure * (projection - d) / 1000, PlateDesign.OneWayAci(d, steel / (1000 * d), fc, true) * d, "kN/m", "22.5.5.1", "λs 1, ρ of what it has");
    }

    /// <summary>ACI 318-19 14.5, φ 0.6, each per metre: 5√f'c Sm in bending, 4/3 √f'c in one-way shear at h from its critical section, at 50 mm under its thickness (14.5.1.7).</summary>
    private static IEnumerable<StrengthCheck> PlainAci(double pressure, double projection, double thickness, double root, string way)
    {
        var h = thickness - 50;
        var moment = pressure * Math.Pow(projection / 1000, 2) / 2;
        var name = $", {way} way";
        yield return new StrengthCheck($"Bending{name}", moment, 0.6 * 5 * root * 1000 * h * h / 6 / 1e6, "kNm/m", "14.5.2.1", $"plain, h {h:0} mm, φ 0.6");
        if (projection > h) yield return new StrengthCheck($"Shear at h{name}", pressure * (projection - h) / 1000, 0.6 * 4.0 / 3 * root * h, "kN/m", "14.5.5.1", $"plain, h {h:0} mm, φ 0.6");
        yield return new StrengthCheck($"Thickness{name}", 200, thickness, "mm", "14.3.2.1", "at least 200 mm");
    }

    public static IReadOnlyList<FoundationCheck> Of(BimDocument document, StructuralAnalysis analysis)
    {
        var (allowable, said) = StructuralAnalysis.AllowableBearing(document);
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var clause = us ? "IRC R401.4.1" : "BS 8004:1986 Table 1";
        var factored = analysis.Loading.Combinations.Where(combination => combination.Factored).ToList();
        var results = new List<FoundationCheck>();
        var samples = new List<(Element Foundation, Point2D At, double Settlement)>();
        foreach (var group in analysis.Model.Supports.GroupBy(support => support.Foundation))
        {
            var reactions = group.Select(support => analysis.ReactionOf(support, analysis.Total)).ToList();
            var (down, moment) = (reactions.Sum(r => r.Fz), reactions.Max(r => Math.Sqrt(r.Mx * r.Mx + r.My * r.My)));
            var name = group.First().Name;
            switch (group.Key)
            {
                case Pile pile:
                    if (Piles.Capacity(document, pile) is { } capacity)
                        results.Add(new FoundationCheck(pile, name, new[] { new StrengthCheck("Load on the pile", down, capacity, "kN", "alpha method", Piles.CapacitySaid(document, pile), "All but wind") }, string.Empty));
                    else
                        results.Add(new FoundationCheck(pile, name, Array.Empty<StrengthCheck>(), Piles.CapacitySaid(document, pile)));
                    break;

                case WallFooting or PadFooting:
                {
                    var outlines = group.Key is WallFooting strip ? Footings.RunOutlines(document, strip).Select(run => run.Outline).ToList() : Footings.Outline(document, (PadFooting)group.Key) is { } pad ? new List<IReadOnlyList<Point2D>> { pad } : new List<IReadOnlyList<Point2D>>();
                    var area = outlines.Sum(outline => Polygon2D.Area(outline)) / 1e6;
                    if (area <= 0)
                    {
                        results.Add(new FoundationCheck(group.Key, name, Array.Empty<StrengthCheck>(), "Not checked: it has no outline."));
                        break;
                    }

                    var thickness = document.FindType<FootingType>(group.Key.TypeId)?.Thickness ?? 225;
                    var weight = area * thickness / 1000 * Concrete;
                    var pressure = (down + weight) / area;
                    var note = $"{down:0.0} kN on it and {weight:0.0} kN of itself over {area:0.00} m²";
                    var width = document.FindType<FootingType>(group.Key.TypeId)?.Width ?? 600;
                    if (group.Key is WallFooting)
                    {
                        // Its heaviest metre, not its average: a beam landing on the wall above comes down to the ground in one stretch of it.
                        var heaviest = group.Max(support => WorstLine(analysis.LineOf(support, analysis.Total)));
                        var even = (down + weight) / (area / (width / 1000));
                        var own = thickness / 1000 * Concrete * width / 1000;
                        if (heaviest + own > even)
                        {
                            pressure = (heaviest + own) / (width / 1000);
                            note = $"{heaviest:0.0} kN/m at its heaviest metre, {down / Math.Max(area / (width / 1000), 1e-3):0.0} on average, and {own:0.0} of itself over {width:0} wide";
                        }
                    }

                    if (group.Key is PadFooting && moment > 0.01 && outlines[0].Count >= 3)
                    {
                        // The moment at its edge, by its narrower way, on the safe side.
                        var narrow = Math.Min(outlines[0][0].DistanceTo(outlines[0][1]), outlines[0][1].DistanceTo(outlines[0][2])) / 1000;
                        var wide = area / Math.Max(narrow, 1e-3);
                        pressure += moment / (wide * narrow * narrow / 6);
                        note += $", with {moment:0.0} kNm";
                    }

                    var checks = new List<StrengthCheck>();
                    if (allowable is { } bears) checks.Add(new StrengthCheck("Bearing", pressure, bears, "kPa", clause, note, "All but wind"));
                    var (shortSide, longSide) = group.Key is PadFooting && outlines[0].Count >= 3
                        ? (Math.Min(outlines[0][0].DistanceTo(outlines[0][1]), outlines[0][1].DistanceTo(outlines[0][2])), Math.Max(outlines[0][0].DistanceTo(outlines[0][1]), outlines[0][1].DistanceTo(outlines[0][2])))
                        : (width, 10 * width);
                    var settles = pressure / Stiffness(document.ProjectInformation.Ground, shortSide, longSide);
                    checks.Add(new StrengthCheck("Settlement", settles, TotalSettlement, "mm", "EN 1997-1 Annex H", $"{pressure:0} kPa on ground of {Stiffness(document.ProjectInformation.Ground, shortSide, longSide):0.0} MN/m³ at this size", "All but wind"));
                    samples.AddRange(SettlementSamples(analysis, group.ToList(), group.Key, width, shortSide, longSide, thickness, document.ProjectInformation.Ground));
                    checks.AddRange(AsConcrete(document, group.Key, group.ToList(), analysis, factored, area, us, out var left));
                    var limits = string.Join(" ", new[] { allowable is null ? $"{pressure:0} kPa on the ground. {said}" : string.Empty, left }.Where(text => text.Length > 0));
                    results.Add(new FoundationCheck(group.Key, name, checks, limits));
                    break;
                }
            }
        }

        // How much each tilts against what is near it: over a stretch of 1.5 m to 9 m, what stands between two footings; less is one footing's own arithmetic, further is not one bay.
        for (var i = 0; i < results.Count; i++)
        {
            var mine = samples.Where(sample => ReferenceEquals(sample.Foundation, results[i].Foundation)).ToList();
            if (mine.Count == 0) continue;
            var rotation = 0.0;
            foreach (var a in mine)
                foreach (var b in samples)
                {
                    var apart = a.At.DistanceTo(b.At);
                    if (apart is >= 1500 and <= 9000) rotation = Math.Max(rotation, Math.Abs(a.Settlement - b.Settlement) / apart);
                }

            if (rotation > 0)
                results[i] = results[i] with { Checks = results[i].Checks.Append(new StrengthCheck("Relative rotation", rotation * 1000, RelativeRotation * 1000, "mm/m", "EN 1997-1 Annex H", "against what stands near it", "All but wind")).ToList() };
        }

        results.AddRange(RetainingDesign.Of(document));
        results.AddRange(RetainingDesign.Flotation(document, analysis));
        return results;
    }

    /// <summary>Annex H's total settlement, mm, for a normal structure on isolated foundations.</summary>
    public const double TotalSettlement = 50;

    /// <summary>Annex H's relative rotation many structures can take: 1 in 500.</summary>
    public const double RelativeRotation = 1.0 / 500;

    /// <summary>
    /// The ground's stiffness under a footing this big, MN/m³, from the plate figure of the ground: Terzaghi's scaling, on sand and gravel by ((B + 0.3) / 2B)², on clay by 0.3 / B and a long footing softer by (m + 0.5) / 1.5m; rock taken as it is.
    /// Settlement is the pressure over this, kPa by MN/m³ being mm.
    /// </summary>
    public static double Stiffness(GroundType ground, double width, double length)
    {
        var plate = AnalyticalModel.Subgrade(ground);
        var side = Math.Max(0.3, width / 1000);
        var ratio = Math.Max(1, length / Math.Max(width, 1));
        return ground switch
        {
            GroundType.Rock => plate,
            GroundType.Gravel or GroundType.Sand or GroundType.LooseSand => plate * Math.Pow((side + 0.3) / (2 * side), 2),
            _ => plate * 0.3 / side * (ratio + 0.5) / (1.5 * ratio)
        };
    }

    /// <summary>The most a line of support carries over any stretch of it, kN/m: each node's load spread halfway to its neighbours, and a window of a metre run along it. A shorter line, all it has over its length.</summary>
    public static double WorstLine(IReadOnlyList<(double At, double Down)> line, double stretch = 1000)
    {
        if (line.Count == 0) return 0;
        var (first, last) = (line[0].At, line[^1].At);
        if (line.Count == 1 || last - first <= stretch) return line.Sum(entry => entry.Down) / (Math.Max(last - first, 1) / 1000);
        var cells = Cells(line);
        return cells.SelectMany(cell => new[] { cell.From, cell.To - stretch }).Where(at => at >= first - 1e-6 && at + stretch <= last + 1e-6).Select(start => Over(cells, start, stretch)).DefaultIfEmpty(0).Max();
    }

    /// <summary>Each node's load spread halfway to its neighbours.</summary>
    private static IReadOnlyList<(double From, double To, double Load)> Cells(IReadOnlyList<(double At, double Down)> line) =>
        line.Select((entry, i) => (i == 0 ? entry.At : (line[i - 1].At + entry.At) / 2, i == line.Count - 1 ? entry.At : (entry.At + line[i + 1].At) / 2, entry.Down)).ToList();

    /// <summary>What a line carries over a stretch of it, kN/m: a node's load counted in the share of its cell inside the stretch. Dividing by the gap between the nodes in it would read high or low as they fall.</summary>
    private static double Over(IReadOnlyList<(double From, double To, double Load)> cells, double start, double stretch) =>
        cells.Sum(cell => cell.To <= cell.From ? 0 : cell.Load * Math.Max(0, Math.Min(cell.To, start + stretch) - Math.Max(cell.From, start)) / (cell.To - cell.From)) / (stretch / 1000);

    /// <summary>How far a footing goes down where it does: a pad at its middle, a strip by the metre along it, in plan.</summary>
    private static IEnumerable<(Element, Point2D, double)> SettlementSamples(StructuralAnalysis analysis, IReadOnlyList<AnalyticalSupport> supports, Element footing, double width, double shortSide, double longSide, double thickness, GroundType ground)
    {
        var stiffness = Stiffness(ground, shortSide, longSide);
        var own = thickness / 1000 * Concrete;
        foreach (var support in supports)
        {
            if (support.Node is { } node)
            {
                yield return (footing, new Point2D(node.At.X, node.At.Y), (analysis.ReactionOf(support, analysis.Total).Fz / (shortSide * longSide / 1e6) + own) / stiffness);
                continue;
            }

            if (support.Panel is not { } panel) continue;
            var line = analysis.LineOf(support, analysis.Total);
            if (line.Count < 2) continue;
            var cells = Cells(line);
            var (first, last) = (line[0].At, line[^1].At);
            for (var at = first; at + 1000 <= last + 1; at += 1000)
                yield return (footing, new Point2D(panel.Origin.X + panel.U.X * (at + 500), panel.Origin.Y + panel.U.Y * (at + 500)), (Over(cells, at, 1000) / (width / 1000) + own) / stiffness);
        }
    }

    /// <summary>A strip or pad as concrete, under the ultimate combination that puts most on it, and what is left out of that.</summary>
    private static IReadOnlyList<StrengthCheck> AsConcrete(BimDocument document, Element footing, IReadOnlyList<AnalyticalSupport> supports, StructuralAnalysis analysis, IReadOnlyList<LoadCombination> factored, double area, bool us, out string left)
    {
        left = string.Empty;
        if (factored.Count == 0 || document.FindType<FootingType>(footing.TypeId) is not { } type) return Array.Empty<StrengthCheck>();
        var worst = factored.Select(combination =>
        {
            var reactions = supports.Select(support => analysis.ReactionOf(support, combination)).ToList();
            return (Combination: combination, Down: reactions.Sum(r => r.Fz), Moment: reactions.Max(r => Math.Sqrt(r.Mx * r.Mx + r.My * r.My)));
        }).MaxBy(entry => entry.Down);
        if (worst.Down <= 0) return Array.Empty<StrengthCheck>();

        var (reinforced, text) = footing switch
        {
            WallFooting strip => (strip.Reinforced, strip.Bars),
            PadFooting pad => (pad.Reinforced, pad.Bars),
            _ => (false, string.Empty)
        };
        var bars = reinforced ? BarSpec.Parse(text) : null;
        var diameter = bars?.Diameter ?? 12;
        var strength = type.Strength > 0 ? type.Strength : bars is not null ? (us ? 3000 * Psi : 28) : us ? 2500 * Psi : 16;
        IEnumerable<StrengthCheck> checks;
        switch (footing)
        {
            case WallFooting strip when document.Find<Wall>(strip.WallId) is { } wall:
            {
                var thick = Footings.WallThickness(document, wall);
                var line = Math.Max(worst.Down / (area / (type.Width / 1000)), supports.Max(support => WorstLine(analysis.LineOf(support, worst.Combination))));
                var projection = (type.Width - thick) / 2 + Math.Abs(strip.Eccentricity);

                // Bars along a strip do nothing across it: only bars at centres cross it.
                var across = bars is { Spacing: { } spacing } ? bars.BarArea * 1000 / spacing : 0;
                if (type.Strength <= 0 && bars is not null && across <= 0) strength = us ? 2500 * Psi : 16;
                var d = type.Thickness - Reinforcement.CoverAgainstGround - diameter / 2;
                var concreteWall = AnalyticalModel.IsConcreteWall(document, wall);
                checks = us
                    ? StripAci(line, type.Width, projection + (concreteWall ? 0 : thick / 4), type.Thickness, d, across, strength)
                    : StripEurocode(line, type.Width, projection, type.Thickness, d, across, strength);
                break;
            }

            case PadFooting pad when Footings.PierSize(document, pad) is { } pier:
            {
                var d = type.Thickness - Reinforcement.CoverAgainstGround - diameter;
                var steel = bars is null ? 0 : bars.Spacing is { } spacing ? bars.BarArea * 1000 / spacing : (bars.Count ?? 0) * bars.BarArea / (Math.Max(type.Width, type.Length) / 1000);
                checks = us
                    ? PadAci(worst.Down, worst.Moment, pier.Across, pier.Along, type.Width, type.Length, type.Thickness, d, steel, strength)
                    : PadEurocode(worst.Down, worst.Moment, pier.Across, pier.Along, type.Width, type.Length, type.Thickness, d, steel, strength);
                if (steel <= 0 && worst.Moment > 0.01) left = "It takes a moment at its foot, and plain concrete is for axial load: reinforce it.";
                break;
            }

            default:
                return Array.Empty<StrengthCheck>();
        }

        return checks.Select(check => check.Name.StartsWith("Thickness", StringComparison.Ordinal) ? check : check with { Under = worst.Combination.Name }).ToList();
    }
}
