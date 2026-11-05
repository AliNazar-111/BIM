using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>A joint or bearing checked: where it is, what kind, its checks, what it wants that the model does not hold, and what is left out.</summary>
public sealed record JointCheck(string Name, string Kind, IReadOnlyList<StrengthCheck> Checks, string Wants, string Limits)
{
    public StrengthCheck? Worst => Checks.Count == 0 ? null : Checks.MaxBy(check => check.Ratio);

    public double Ratio => Worst?.Ratio ?? 0;

    public bool Enough => Ratio <= 1 + 1e-9;
}

/// <summary>A cavity wall tie, by what PD 6697 asks of each type: its declared tension and compression, kN, and the tallest building it is for, m.</summary>
public sealed record TieType(string Name, double Tension, double Compression, double Height);

/// <summary>
/// What holds one thing to another. The model holds no padstones, lintels, ties, end plates, base plates or hangers, so like a slab's punching links
/// each joint says what it wants, from the forces at it. Where a beam lands on masonry, BS EN 1996-1-1 6.1.3, a padstone where it comes to one; over each opening in a loaded masonry
/// wall, the lintel it wants and its bearing; each cavity wall's ties by (6.20) and PD 6697; timber across the grain, BS EN 1995-1-1 6.1.5 or the NDS 3.10;
/// a steel beam's end as a partial depth end plate, BS EN 1993-1-8 or AISC 360-16 Chapter J; and a steel column's base plate.
/// </summary>
public static class JointDesign
{
    private const double Psi = 0.00689476;

    /// <summary>What a lintel bears on at each end, mm, as lintel makers ask.</summary>
    public const double LintelBearing = 150;

    /// <summary>The least a concentrated load may bear on a wall, mm (8.1.6).</summary>
    public const double LeastBearing = 90;

    /// <summary>The least ties a square metre of cavity wall in the UK, 900 by 450, as PD 6697 is quoted.</summary>
    public const double LeastTies = 2.5;

    /// <summary>Lightest first, as PD 6697:2019 Table 12 is quoted.</summary>
    public static readonly IReadOnlyList<TieType> TieTypes = new[]
    {
        new TieType("Type 4", 0.65, 0.35, 10), new TieType("Type 3", 1.1, 0.65, 15), new TieType("Type 2", 1.8, 1.05, 15), new TieType("Type 1", 2.5, 2.0, double.PositiveInfinity)
    };

    // ---- on masonry --------------------------------------------------------------------

    /// <summary>The effective length of a bearing at mid height of a wall, mm: the load spreading at 60° to the horizontal each way, as far as the wall goes (Figure 6.2).</summary>
    public static double EffectiveLength(double bearing, double toNearEnd, double toFarEnd, double height)
    {
        var spread = height / 2 * Math.Tan(Math.PI / 6);
        return bearing + Math.Min(toNearEnd, spread) + Math.Min(toFarEnd, spread);
    }

    /// <summary>
    /// BS EN 1996-1-1 (6.11): β = (1 + 0.3 a1/hc)(1.5 - 1.1 Ab/Aef), Ab/Aef no more than 0.45, β at least 1 and no more than the lesser of 1.25 + a1/2hc and 1.5.
    /// </summary>
    public static double Enhancement(double a1, double height, double loaded, double effective)
    {
        var beta = (1 + 0.3 * a1 / height) * (1.5 - 1.1 * Math.Min(0.45, loaded / effective));
        return Math.Clamp(beta, 1, Math.Min(1.25 + a1 / (2 * height), 1.5));
    }

    /// <summary>
    /// A load landing on a masonry wall, kN, on so much of its length and all of its thickness. Where the wall alone will not take it, a padstone by 6.1.3(7): as wide as the wall, over 200 high,
    /// over three times the bearing long, the stress beneath it no more than 1.5 fd. What is said of it is what it wants.
    /// </summary>
    public static (StrengthCheck Check, string Wants) OnMasonry(double force, double bearing, double t, double toNearEnd, double toFarEnd, double height, double fd)
    {
        var (loaded, effective) = (bearing * t, EffectiveLength(bearing, toNearEnd, toFarEnd, height) * t);
        var beta = Enhancement(toNearEnd, height, loaded, effective);
        var carries = beta * loaded * fd / 1000;
        var said = $"β {beta:0.00} on {bearing:0} by {t:0} mm, fd {fd:0.00}, taken as a solid unit";
        if (force <= carries) return (new StrengthCheck("Bearing on the wall", force, carries, "kN", "6.1.3", said), string.Empty);

        var length = Math.Ceiling(Math.Max(3 * bearing, force * 1000 / (1.5 * fd * t)) / 25) * 25;
        if (length > bearing + toNearEnd + toFarEnd)
            return (new StrengthCheck("Bearing on the wall", force, carries, "kN", "6.1.3", $"{said}; a padstone would want {length:0} mm, more than the wall has"), string.Empty);
        return (new StrengthCheck("Bearing on a padstone", force, 1.5 * fd * t * length / 1000, "kN", "6.1.3(7)", $"needs a padstone: the wall alone takes {carries:0.0} kN"), $"a padstone {length:0} long, 215 high and {t:0} wide");
    }

    /// <summary>A lintel's end on a masonry wall, and the bearing it wants if 150 will not do.</summary>
    public static (StrengthCheck Check, double Bearing) LintelEnd(double force, double t, double toNearEnd, double toFarEnd, double height, double fd)
    {
        for (var bearing = LintelBearing; bearing <= Math.Min(600, LintelBearing + toFarEnd); bearing += 25)
        {
            var (loaded, effective) = (bearing * t, EffectiveLength(bearing, toNearEnd, toFarEnd, height) * t);
            var carries = Enhancement(toNearEnd, height, loaded, effective) * loaded * fd / 1000;
            if (force <= carries) return (new StrengthCheck("Lintel's end on the wall", force, carries, "kN", "6.1.3", $"bearing {bearing:0} mm, fd {fd:0.00}"), bearing);
        }

        var (first, area) = (LintelBearing * t, EffectiveLength(LintelBearing, toNearEnd, toFarEnd, height) * t);
        return (new StrengthCheck("Lintel's end on the wall", force, Enhancement(toNearEnd, height, first, area) * first * fd / 1000, "kN", "6.1.3", "no bearing the wall has is enough"), LintelBearing);
    }

    /// <summary>
    /// The lightest tie for a wind on the wall, kN/m², and a building so tall, m: nt = WEd / Fd by (6.20), Fd the lesser declared value over γM 3.0; 2.5 a square metre where that will do, else as many as it comes to of the heaviest.
    /// WEd is what passes between the leaves.
    /// </summary>
    public static (TieType Type, double Density, double Needed) TiesFor(double wind, double height)
    {
        foreach (var type in TieTypes.Where(each => height <= each.Height))
        {
            var needed = wind / (Math.Min(type.Tension, type.Compression) / 3);
            if (needed <= LeastTies) return (type, LeastTies, needed);
        }

        var heaviest = TieTypes[^1];
        var most = wind / (Math.Min(heaviest.Tension, heaviest.Compression) / 3);
        return (heaviest, Math.Ceiling(most * 2) / 2, most);
    }

    /// <summary>A tie's length for a cavity, mm, as Approved Document A's Table 5 is quoted, and the cavity and 125 past it.</summary>
    public static double TieLength(double cavity) => cavity switch
    {
        <= 75 => 200,
        <= 100 => 225,
        <= 125 => 250,
        <= 150 => 275,
        <= 175 => 300,
        _ => cavity + 125
    };

    // ---- timber ------------------------------------------------------------------------

    /// <summary>
    /// A timber end across the grain by BS EN 1995-1-1 6.1.5: its contact length and 30 mm on its side towards the span, no more than the contact itself, against kc,90 fc,90,d;
    /// kc,90 1.5 for solid softwood and 1.75 for glulam on discrete supports, fc,90,k 2.5 in C24 and GL24h. With it, the contact that would do.
    /// </summary>
    public static (StrengthCheck Check, double Wants) TimberEndEurocode(double force, double width, double contact, TimberGrade grade, double kmod)
    {
        var fd = kmod * 2.5 / grade.GammaM;
        var k = grade.Glulam ? 1.75 : 1.5;
        var effective = contact + Math.Min(30, contact);
        var wanted = force * 1000 / (width * k * fd);
        return (new StrengthCheck("Across the grain", force * 1000 / (width * effective), k * fd, "N/mm²", "6.1.5", $"{contact:0} mm of contact and {effective - contact:0} past it, kc,90 {k:0.00}, kmod {kmod:0.0}"), Math.Max(wanted - 30, wanted / 2));
    }

    /// <summary>A timber end across the grain by the NDS 3.10, LRFD: Fc⊥ 625 psi, KF 1.67, φ 0.90, no time effect and no bearing area factor at an end.</summary>
    public static (StrengthCheck Check, double Wants) TimberEndNds(double force, double width, double contact)
    {
        var strength = 625 * Psi * 1.67 * 0.9;
        return (new StrengthCheck("Across the grain", force * 1000 / (width * contact), strength, "N/mm²", "NDS 3.10", $"{contact:0} mm of contact, Fc⊥ 625 psi, KF 1.67, φ 0.90"), force * 1000 / (width * strength));
    }

    // ---- steel -------------------------------------------------------------------------

    /// <summary>An 8.8 bolt in shear by BS EN 1993-1-8 Table 3.4, kN: 0.6 fub As / γM2, the shear plane through its thread, γM2 1.25.</summary>
    public static double BoltShearEurocode(double tensileArea) => 0.6 * 800 * tensileArea / 1.25 / 1000;

    /// <summary>
    /// A bolt bearing on a plate by BS EN 1993-1-8 Table 3.4, kN: k1 αb fu d t / γM2. An end bolt's αd is e1/3d0, the smallest of that, fub/fu and 1; k1 at an edge the least of 2.8 e2/d0 - 1.7 and 2.5.
    /// </summary>
    public static double BoltBearingEurocode(double d, double hole, double t, double fu, double e1, double e2)
    {
        var alpha = Math.Min(Math.Min(e1 / (3 * hole), 800 / fu), 1);
        var k1 = Math.Min(2.8 * e2 / hole - 1.7, 2.5);
        return k1 * alpha * fu * d * t / 1.25 / 1000;
    }

    /// <summary>
    /// A steel beam's end as a partial depth end plate by BS EN 1993-1-8: 150 wide and 10 thick in S275, M20 8.8 bolts at 90 gauge and 70 pitch, 40 from the plate's ends, as few rows as carry it, deep at least 0.6 of the beam (P358 as quoted,
    /// which takes each bolt at 0.8 for the tension a simple joint's small moment puts in it); the beam's web at 0.9 of the plate's depth and the plate at 1/1.27 of it, as P358 has them; 6 mm fillets each side of the web. What it bolts to is so thick.
    /// </summary>
    public static (IReadOnlyList<StrengthCheck> Checks, string Wants) EndPlateEurocode(double shear, SteelSection beam, double supporter)
    {
        const double fyPlate = 275, fuPlate = 410, thick = 10, fy = 355, fu = 470, weld = 6;
        var (each, onPlate, onSupporter) = (BoltShearEurocode(245), BoltBearingEurocode(20, 22, thick, fuPlate, 40, 30), BoltBearingEurocode(20, 22, supporter, fu, 200, 30));
        var clear = beam.H - 2 * (beam.Tf + beam.Root);
        var rows = 2;
        while (0.8 * 2 * rows * each < shear && 80 + rows * 70 <= clear) rows++;
        var depth = Math.Max(80 + (rows - 1) * 70.0, Math.Min(0.6 * beam.H, clear));
        var bolts = 2 * rows;
        var throat = 0.7 * weld;
        var checks = new List<StrengthCheck>
        {
            new("Bolts in shear", shear, 0.8 * bolts * each, "kN", "Table 3.4", $"{bolts} M20 8.8, 0.8 of {each:0.0} kN each, as P358 is quoted"),
            new("Bolts bearing on the plate", shear, bolts * onPlate, "kN", "Table 3.4", $"10 mm S275, {onPlate:0.0} kN a bolt"),
            new("Bolts bearing on what it bolts to", shear, bolts * onSupporter, "kN", "Table 3.4", $"{supporter:0.0} mm of S355"),
            new("Web in shear at the plate", shear, 0.9 * depth * beam.Tw * fy / Math.Sqrt(3) / 1000, "kN", "EC3-1-1 6.2.6", $"{depth:0} deep, {beam.Tw:0.0} web"),
            new("Plate in shear", shear, 2 * depth * thick * fyPlate / 1.27 / Math.Sqrt(3) / 1000, "kN", "P358", "as P358 is quoted"),
            new("Welds to the web", shear, 2 * fu / Math.Sqrt(3) / (0.9 * 1.25) * throat * (depth - 2 * weld) / 1000, "kN", "4.5.3.3", $"{weld:0} mm fillets, βw 0.9")
        };
        return (checks, $"a partial depth end plate 150 by {depth:0} by 10, {bolts} M20 8.8, 6 mm fillets");
    }

    /// <summary>
    /// A steel beam's end as a shear end plate by AISC 360-16, LRFD: 3/4 in A325-N bolts at 3 in pitch, 1 1/4 in from the plate's ends (J3.6, Table J3.2, φ 0.75, 54 ksi), bearing and tearout (J3.10), 3/8 in A36;
    /// the web in shear over the plate (G2.1, φ 1.0) and 1/4 in E70 fillets each side (J2.4, φ 0.75).
    /// </summary>
    public static (IReadOnlyList<StrengthCheck> Checks, string Wants) EndPlateAisc(double shear, SteelSection beam, double supporter)
    {
        const double d = 19.05, hole = 20.6, thick = 9.525, end = 31.75, pitch = 76.2, weld = 6.35;
        var (fyPlate, fuPlate, fy, fu) = (36000 * Psi, 58000 * Psi, 50000 * Psi, 65000 * Psi);
        var each = 0.75 * 54000 * Psi * Math.PI * d * d / 4 / 1000;
        double Bearing(double t, double strength, double clearEdge) => 0.75 * Math.Min(1.2 * clearEdge * t * strength, 2.4 * d * t * strength) / 1000;
        var clear = beam.H - 2 * (beam.Tf + beam.Root);
        var rows = 2;
        while (2 * rows * each < shear && 2 * end + rows * pitch <= clear) rows++;
        var depth = 2 * end + (rows - 1) * pitch;
        var bolts = 2 * rows;
        var edge = end - hole / 2;
        var onPlate = 2 * Bearing(thick, fuPlate, edge) + (bolts - 2) * Bearing(thick, fuPlate, pitch - hole);
        var checks = new List<StrengthCheck>
        {
            new("Bolts in shear", shear, bolts * each, "kN", "J3.6", $"{bolts} 3/4 in A325-N, {each:0.0} kN each"),
            new("Bolts bearing on the plate", shear, onPlate, "kN", "J3.10", "3/8 in A36, tearout at its ends"),
            new("Bolts bearing on what it bolts to", shear, bolts * Bearing(supporter, fu, 1e6), "kN", "J3.10", $"{supporter:0.0} mm of A992"),
            new("Web in shear at the plate", shear, 0.6 * fy * depth * beam.Tw / 1000, "kN", "G2.1", $"{depth:0} deep, {beam.Tw:0.0} web"),
            new("Plate in shear", shear, 2 * 0.6 * fyPlate * depth * thick / 1000, "kN", "J4.2", "its gross section, φ 1.0"),
            new("Welds to the web", shear, 2 * 0.75 * 0.6 * 70000 * Psi * 0.7071 * weld * (depth - 2 * weld) / 1000, "kN", "J2.4", "1/4 in E70 fillets")
        };
        return (checks, $"a shear end plate {depth / 25.4:0.#} in deep, 3/8 in A36, {bolts} 3/4 in A325-N, 1/4 in fillets");
    }

    /// <summary>
    /// The limiting width c round a steel section on a base plate by BS EN 1993-1-8 6.2.5, mm: tp √(fy / (3 fjd γM0)), as P398's (5.2) has it.
    /// </summary>
    public static double BaseWidth(double thickness, double fy, double fjd) => thickness * Math.Sqrt(fy / (3 * fjd));

    /// <summary>The area under an I section bearing through its plate, mm²: its flanges and web each with c round them, or the whole of it where they run together.</summary>
    public static double BaseArea(SteelSection column, double c)
    {
        var whole = (column.B + 2 * c) * (column.H + 2 * c);
        if (!column.IsI) return whole;
        var web = column.H - 2 * column.Tf - 2 * c;
        return web <= 0 ? whole : 2 * (column.B + 2 * c) * (column.Tf + 2 * c) + web * (column.Tw + 2 * c);
    }

    /// <summary>
    /// A steel column's base plate under axial load by BS EN 1993-1-8 6.2.5: fjd = fcd = 0.85 fck / 1.5, as P398 takes it for a foundation of usual proportions, the thinnest S275 plate whose T-stubs carry it, at least 100 more than the column each way for its bolts.
    /// </summary>
    public static (StrengthCheck Check, string Wants) BasePlateEurocode(double force, SteelSection column, double fck)
    {
        var fjd = 0.85 * fck / 1.5;
        foreach (var thick in new[] { 10.0, 12, 15, 20, 25, 30, 40 })
        {
            var fy = thick <= 16 ? 275.0 : 265;
            var c = BaseWidth(thick, fy, fjd);
            var carries = BaseArea(column, c) * fjd / 1000;
            if (carries >= force)
            {
                var (wide, deep) = (Math.Ceiling((column.B + Math.Max(2 * c, 100)) / 10) * 10, Math.Ceiling((column.H + Math.Max(2 * c, 100)) / 10) * 10);
                return (new StrengthCheck("Column on its base plate", force, carries, "kN", "6.2.5", $"fjd {fjd:0.0} N/mm², c {c:0} mm"), $"a base plate {wide:0} by {deep:0} by {thick:0}");
            }
        }

        return (new StrengthCheck("Column on its base plate", force, BaseArea(column, BaseWidth(40, 265, fjd)) * fjd / 1000, "kN", "6.2.5", "more than a 40 mm plate carries"), string.Empty);
    }

    // ---- from the model ----------------------------------------------------------------

    /// <summary>Every joint this looks at, those over their limits first.</summary>
    public static IReadOnlyList<JointCheck> Of(BimDocument document, SecondOrderAnalysis second)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var joints = new List<JointCheck>();
        joints.AddRange(Landings(document, second, us));
        joints.AddRange(UnderWalls(second, us));
        joints.AddRange(BaseOf(document, second, us));
        foreach (var panel in second.Analysis.Model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Material.Kind == AnalyticalMaterialKind.Masonry))
        {
            joints.AddRange(Lintels(document, second, panel, us));
            if (Ties(document, second.Analysis, panel, us) is { } ties) joints.Add(ties);
        }

        return joints;
    }

    private static double WidthOf(AnalyticalSection section) => section.Dimensions.Count switch
    {
        >= 4 => section.Dimensions[1],
        >= 2 => section.Dimensions[1],
        1 => section.Dimensions[0],
        _ => 100
    };

    private static bool IsJoist(AnalyticalMember member) => member.Kind == AnalyticalMemberKind.Joist;

    /// <summary>The wall going down from a node, if any.</summary>
    private static AnalyticalPanel? WallUnder(AnalyticalModel model, AnalyticalNode node) => model.Panels.FirstOrDefault(panel =>
        panel.Kind == AnalyticalPanelKind.Wall && (panel.Outline.Contains(node) || panel.Inside.Contains(node)) && panel.Outline.Min(each => each.At.Z) < node.At.Z - 100);

    /// <summary>Where each beam lands, a run of the same joists on the same thing being one row, the worst of them.</summary>
    private static IEnumerable<JointCheck> Landings(BimDocument document, SecondOrderAnalysis second, bool us)
    {
        var model = second.Analysis.Model;
        var worst = new Dictionary<string, (JointCheck Joint, int Count)>();
        foreach (var member in model.Members.Where(member => member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist))
        {
            var landings = second.LandingsOf(member);
            var run = IsJoist(member) ? member.Section.Name : member.Element.Id.ToString();
            foreach (var node in member.Nodes)
            {
                if (WallUnder(model, node) is { } wall)
                {
                    if (AlongWall(member, wall)) continue;
                    if (OnWall(document, member, node, wall, landings, us) is { } onWall) Keep(worst, $"{run}|{wall.Name}", onWall);
                    continue;
                }

                // What is under it: a column there, or a beam it ends on partway along. Two beams meeting end to end hold nothing up, and a beam passing another is not its joint.
                var others = model.Members.Where(each => !ReferenceEquals(each, member) && each.Nodes.Contains(node)).ToList();
                var ends = ReferenceEquals(node, member.Start) || ReferenceEquals(node, member.End);
                var passing = others.FirstOrDefault(each => each.Kind != AnalyticalMemberKind.Column && !ReferenceEquals(each.Start, node) && !ReferenceEquals(each.End, node));
                var post = others.FirstOrDefault(each => each.Kind == AnalyticalMemberKind.Column);
                var under = ends ? passing ?? post : post is not null && (ReferenceEquals(post.Start, node) || ReferenceEquals(post.End, node)) ? post : null;
                if (under is null) continue;
                if (OnMember(member, under, node, landings, us) is { } onMember) Keep(worst, $"{run}|{under.Name}", onMember);
            }
        }

        return worst.Values.Select(entry => entry.Count > 1 ? entry.Joint with { Name = $"{entry.Joint.Name}, and {entry.Count - 1} like it" } : entry.Joint);
    }

    private static void Keep(Dictionary<string, (JointCheck Joint, int Count)> worst, string key, JointCheck joint)
    {
        if (!worst.TryGetValue(key, out var held)) worst[key] = (joint, 1);
        else worst[key] = (joint.Ratio > held.Joint.Ratio ? joint : held.Joint, held.Count + 1);
    }

    private static bool AlongWall(AnalyticalMember member, AnalyticalPanel wall)
    {
        var (a, b) = (member.Start.At, member.End.At);
        var length = Math.Max(1, AnalyticalModel.Distance(a, b));
        return Math.Abs((b.X - a.X) * wall.U.X + (b.Y - a.Y) * wall.U.Y + (b.Z - a.Z) * wall.U.Z) / length > 0.7;
    }

    private static (LoadCombination Combination, double Down) WorstOn(IReadOnlyList<(LoadCombination Combination, IReadOnlyDictionary<AnalyticalNode, double> Down)> landings, AnalyticalNode node) =>
        landings.Select(each => (each.Combination, Down: each.Down.GetValueOrDefault(node))).MaxBy(each => each.Down);

    private static JointCheck? OnWall(BimDocument document, AnalyticalMember member, AnalyticalNode node, AnalyticalPanel wall, IReadOnlyList<(LoadCombination Combination, IReadOnlyDictionary<AnalyticalNode, double> Down)> landings, bool us)
    {
        var (combination, down) = WorstOn(landings, node);
        if (down <= 0.01) return null;
        var checks = new List<StrengthCheck>();
        var wants = new List<string>();
        var width = WidthOf(member.Section);
        var flat = wall.FlatOutline;
        var along = wall.Flat(node.At).X;
        var (first, last) = (flat.Min(p => p.X), flat.Max(p => p.X));
        var near = Math.Max(0, Math.Min(along - first, last - along) - width / 2);
        var far = Math.Max(0, Math.Max(along - first, last - along) - width / 2);
        var height = node.At.Z - wall.Outline.Min(each => each.At.Z);
        var masonry = wall.Material.Kind == AnalyticalMaterialKind.Masonry;
        var limits = string.Empty;

        if (masonry && !IsJoist(member))
        {
            if (us) limits = "Bearing on the masonry is not checked: TMS 402's strengths could not be confirmed.";
            else
            {
                var fd = MasonryDesign.Characteristic(MasonryDesign.LoadedUnitOf(document, wall)) / MasonryDesign.GammaCompression;
                var (check, wanted) = OnMasonry(down, width, wall.Thickness, near, far, height, fd);
                checks.Add(check with { Under = combination.Name });
                if (wanted.Length > 0) wants.Add(wanted);
            }
        }

        if (member.Section.Material.Kind == AnalyticalMaterialKind.Timber)
        {
            var (check, wanted, under) = TimberEnd(member, node, landings, Math.Min(wall.Thickness, 100), us);
            checks.Add(check with { Under = under });
            wants.Add($"{Math.Ceiling(Math.Max(wanted, LeastBearing) / 5) * 5:0} mm of bearing or a hanger rated {down:0.0} kN");
        }

        if (checks.Count == 0 && limits.Length == 0) return null;
        return new JointCheck($"{member.Name} on {wall.Name}", IsJoist(member) ? "Joist on a wall" : "Beam on a wall", checks, string.Join("; ", wants), limits);
    }

    /// <summary>A timber end across the grain, under the combination that is worst for it: not the heaviest, which may be the shortest, but the heaviest for how long it lasts.</summary>
    private static (StrengthCheck Check, double Wanted, string Under) TimberEnd(AnalyticalMember member, AnalyticalNode node, IReadOnlyList<(LoadCombination Combination, IReadOnlyDictionary<AnalyticalNode, double> Down)> landings, double contact, bool us)
    {
        var width = WidthOf(member.Section);
        if (us)
        {
            var (combination, down) = WorstOn(landings, node);
            var (check, wanted) = TimberEndNds(down, width, contact);
            return (check, wanted, combination.Name);
        }

        var grade = member.Section.Material.Quality.StartsWith("GL", StringComparison.Ordinal) ? TimberDesign.Gl24h : TimberDesign.C24;
        var worst = landings.MaxBy(each => each.Down.GetValueOrDefault(node) / TimberDesign.Kmod(each.Combination));
        var (eurocode, needs) = TimberEndEurocode(worst.Down.GetValueOrDefault(node), width, contact, grade, TimberDesign.Kmod(worst.Combination));
        return (eurocode, needs, worst.Combination.Name);
    }

    private static JointCheck? OnMember(AnalyticalMember member, AnalyticalMember other, AnalyticalNode node, IReadOnlyList<(LoadCombination Combination, IReadOnlyDictionary<AnalyticalNode, double> Down)> landings, bool us)
    {
        var (combination, down) = WorstOn(landings, node);
        if (down <= 0.01) return null;
        var (mine, theirs) = (member.Section.Material.Kind, other.Section.Material.Kind);
        var name = $"{member.Name} to {other.Name}";
        var onTop = other.Kind == AnalyticalMemberKind.Column && (ReferenceEquals(other.Start, node) || ReferenceEquals(other.End, node));
        if (mine == AnalyticalMaterialKind.Timber)
        {
            var seat = theirs == AnalyticalMaterialKind.Steel && SteelSection.Of(other.Section) is { } flange ? Math.Max(40, (flange.B - flange.Tw) / 2) : Math.Min(WidthOf(other.Section), 100);
            var (check, wanted, under) = TimberEnd(member, node, landings, seat, us);
            return new JointCheck(name, IsJoist(member) ? "Joist on a beam" : "Beam on a beam", new[] { check with { Under = under } }, $"{Math.Ceiling(wanted / 5) * 5:0} mm of bearing or a hanger rated {down:0.0} kN", string.Empty);
        }

        if (mine == AnalyticalMaterialKind.Steel && theirs == AnalyticalMaterialKind.Steel && SteelSection.Of(member.Section) is { } beam && SteelSection.Of(other.Section) is { } support)
        {
            if (onTop) return new JointCheck(name, "Beam on a post", Array.Empty<StrengthCheck>(), $"a cap plate for {down:0.0} kN", "A beam on a post's cap plate is not worked out: its web bearing and the plate are for whoever details it.");
            var thick = other.Kind == AnalyticalMemberKind.Column ? support.Tf : support.Tw;
            var (checks, wants) = us ? EndPlateAisc(down, beam, thick) : EndPlateEurocode(down, beam, thick);
            var fixedEnd = ReferenceEquals(node, member.Start) ? member.StartRelease == MemberRelease.Fixed : member.EndRelease == MemberRelease.Fixed;
            var limits = "Tying forces, block tearing and the supporting member's own local checks are not worked out.";
            if (fixedEnd) limits = $"It is modelled fixed, and this plate is for shear alone: the moment at it wants a joint of its own, not worked out here. {limits}";
            return new JointCheck(name, "Steel end plate", checks.Select(check => check with { Under = combination.Name }).ToList(), wants, limits);
        }

        return new JointCheck(name, "Not worked out", Array.Empty<StrengthCheck>(), string.Empty, $"A {mine.ToString().ToLowerInvariant()} member on a {theirs.ToString().ToLowerInvariant()} one is not worked out here.");
    }

    /// <summary>
    /// A beam a wall stands on, by statics. In the plates a wall over a beam is a deep beam, spanning between its returns with the beam for its tie, and the beam reads next to nothing; but that
    /// wants tension along its foot that masonry does not have. So it is also put to carry all that wall brings down, as a load along it, nothing taken for arching, by the same checks as any member,
    /// held along its top by the wall. Worst of the ultimate combinations, with what the analysis finds in it besides.
    /// </summary>
    private static IEnumerable<JointCheck> UnderWalls(SecondOrderAnalysis second, bool us)
    {
        var analysis = second.Analysis;
        var model = analysis.Model;
        foreach (var member in model.Members.Where(member => member.Kind == AnalyticalMemberKind.Beam))
        {
            var height = member.Start.At.Z;
            var middle = new Point3D((member.Start.At.X + member.End.At.X) / 2, (member.Start.At.Y + member.End.At.Y) / 2, height);
            var wall = model.Panels.FirstOrDefault(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Material.Kind == AnalyticalMaterialKind.Masonry && AlongWall(member, panel)
                && Math.Abs(panel.Outline.Min(node => node.At.Z) - height) <= 5
                && Math.Abs(AnalyticalModel.Dot(AnalyticalModel.Minus(middle, panel.Origin), panel.Normal)) <= 5
                && panel.Flat(middle).X > panel.FlatOutline.Min(p => p.X) && panel.Flat(middle).X < panel.FlatOutline.Max(p => p.X));
            if (wall is null) continue;

            var flat = wall.FlatOutline;
            var long_ = (flat.Max(p => p.X) - flat.Min(p => p.X)) / 1000;
            var span = member.Length / 1000;
            var factored = analysis.Loading.Combinations.Where(combination => combination.Factored).ToList();
            if (factored.Count == 0) continue;
            double On(LoadCombination combination) => combination.Terms.Sum(term => term.Factor * (
                (term.Case.Nature == LoadNature.SelfWeight ? wall.SelfWeight * wall.Area / 1e6 : 0)
                + analysis.Loading.SurfaceLoads.Where(load => ReferenceEquals(load.Panel, wall) && load.Case == term.Case).Sum(load => -load.Force.Z) * wall.Area / 1e6
                + analysis.Loading.LineLoads.Where(load => ReferenceEquals(load.Panel, wall) && load.Case == term.Case).Sum(load => -load.Force.Z * (load.End - load.Start) / 1000)));
            var worst = factored.MaxBy(On)!;
            var line = On(worst) / long_;
            var inMember = analysis.MemberOf(member, worst);
            var forces = new MemberForces(0, 0, line * span / 2 + Math.Abs(inMember.ShearZ), 0, line * span * span / 8 + Math.Abs(inMember.MomentY), 0, false);
            var lengths = new MemberLengths(member.Length, member.Length, 0, 0);
            IEnumerable<StrengthCheck>? checks = null;
            if (member.Section.Material.Kind == AnalyticalMaterialKind.Steel && SteelSection.Of(member.Section) is { } steel)
                checks = us ? SteelDesign.Aisc(steel, lengths, forces, 1) : SteelDesign.Eurocode(steel, lengths, forces);
            else if (member.Section.Material.Kind == AnalyticalMaterialKind.Timber && member.Section.Dimensions.Count >= 2)
            {
                var glulam = member.Section.Material.Quality.StartsWith("GL", StringComparison.Ordinal) || member.Section.Material.Quality.StartsWith("24F", StringComparison.Ordinal);
                var (depth, width) = (member.Section.Dimensions[0], member.Section.Dimensions[1]);
                checks = us ? TimberDesign.Nds(depth, width, glulam, false, false, lengths, member.Length, forces, TimberDesign.TimeEffect(worst))
                    : TimberDesign.Eurocode(glulam ? TimberDesign.Gl24h : TimberDesign.C24, depth, width, false, lengths, forces, TimberDesign.Kmod(worst));
            }

            var said = $"{line:0.0} kN/m of {wall.Name} on {span:0.00} m";
            yield return new JointCheck($"{member.Name} under {wall.Name}", "Beam under a wall", (checks ?? Array.Empty<StrengthCheck>()).Select(check => check with { Under = worst.Name, Note = said }).ToList(),
                $"to carry {line:0.0} kN/m of the wall on it, with nothing for arching",
                checks is null ? "A concrete beam under a wall is not worked out this way." : "By statics, the wall a load on it: masonry has no tension to make a deep beam of itself with the beam for its tie, whatever the plates say.");
        }
    }

    /// <summary>Each steel column's base plate.</summary>
    private static IEnumerable<JointCheck> BaseOf(BimDocument document, SecondOrderAnalysis second, bool us)
    {
        var model = second.Analysis.Model;
        foreach (var member in model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column && member.Section.Material.Kind == AnalyticalMaterialKind.Steel))
        {
            var node = member.Start.At.Z <= member.End.At.Z ? member.Start : member.End;
            var support = model.Supports.FirstOrDefault(each => ReferenceEquals(each.Node, node));
            if (support is null || SteelSection.Of(member.Section) is not { } column) continue;
            if (us)
            {
                yield return new JointCheck($"{member.Name} base", "Base plate", Array.Empty<StrengthCheck>(), string.Empty, "A base plate is not worked out for a US project.");
                continue;
            }

            var (combination, down) = WorstOn(second.LandingsOf(member), node);
            if (down <= 0.01) continue;
            var fck = support.Foundation is PadFooting { Reinforced: true } or RaftFoundation or Pile ? 28.0 : 16;
            var (check, wants) = BasePlateEurocode(down, column, fck);
            yield return new JointCheck($"{member.Name} base", "Base plate", new[] { check with { Under = combination.Name } }, wants, "Under axial load. Holding-down bolts and any moment at the base are not worked out.");
        }
    }

    /// <summary>The lintel each opening in a masonry wall wants: what comes down on it spread along it, no arching taken, and its end on the wall.</summary>
    private static IEnumerable<JointCheck> Lintels(BimDocument document, SecondOrderAnalysis second, AnalyticalPanel wall, bool us)
    {
        var analysis = second.Analysis;
        var flat = wall.FlatOutline;
        var (first, last, foot, top) = (flat.Min(p => p.X), flat.Max(p => p.X), flat.Min(p => p.Y), flat.Max(p => p.Y));
        var length = (last - first) / 1000;
        var factored = analysis.Loading.Combinations.Where(combination => combination.Factored).ToList();
        var supports = analysis.Model.Supports.Where(support => ReferenceEquals(support.Panel, wall)).ToList();
        var line = factored.Count == 0 || supports.Count == 0 ? 0
            : factored.Max(combination => supports.Sum(support => analysis.ReactionOf(support, combination).Fz)) / Math.Max(length, 1e-3);
        if (line <= 0 && wall.Element is Wall model) line = (us ? 1.6 : 1.5) * Loads.OnWall(document, model);
        var weight = (us ? 1.2 : 1.35) * (wall.SelfWeight + analysis.Loading.SurfaceLoads.Where(load => ReferenceEquals(load.Panel, wall) && load.Case.Nature != LoadNature.Wind && load.Case.Nature != LoadNature.Imposed).Sum(load => Math.Abs(load.Force.Z)));
        var fd = us ? 0 : MasonryDesign.Characteristic(MasonryDesign.LoadedUnitOf(document, wall)) / MasonryDesign.GammaCompression;
        var opening = 0;
        foreach (var hole in wall.Openings.Select(hole => hole.Select(wall.Flat).ToList()).OrderBy(hole => hole.Min(p => p.X)))
        {
            opening++;
            var (left, right, head) = (hole.Min(p => p.X), hole.Max(p => p.X), hole.Max(p => p.Y));
            if (head >= top - 50) continue;
            var clear = right - left;
            var load = Math.Max(0, line - weight * (head - foot) / 1000);
            var span = (clear + 2 * JointDesign.LintelBearing) / 1000;
            var end = load * span / 2;
            var name = $"{wall.Name} over opening {opening}";
            var kind = "Lintel";
            var said = $"a lintel {span * 1000:0} long over {clear:0}, to carry {load:0.0} kN/m at the ultimate";
            if (us)
            {
                yield return new JointCheck(name, kind, Array.Empty<StrengthCheck>(), $"{said}, {end:0.0} kN at each end", "Its bearing on the masonry is not checked: TMS 402's strengths could not be confirmed.");
                continue;
            }

            var (near, far) = (Math.Max(0, Math.Min(left - first, last - right) - LintelBearing), Math.Max(left - first, last - right) + clear);
            var (check, bearing) = LintelEnd(end, wall.Thickness, near, far, head - foot, fd);
            yield return new JointCheck(name, kind, new[] { check }, $"{said}, bearing {bearing:0} each end", "The lintel itself is to its maker's declared load (BS EN 845-2). Nothing is taken for the masonry arching over it.");
        }
    }

    /// <summary>A cavity wall's ties, by the wind that passes between its leaves.</summary>
    private static JointCheck? Ties(BimDocument document, StructuralAnalysis analysis, AnalyticalPanel wall, bool us)
    {
        var (outer, outerT) = MasonryDesign.OuterLeafOf(document, wall);
        if (outer is null || wall.Element is not Wall model || document.GetWallType(model) is not { } type) return null;
        if (us) return new JointCheck($"{wall.Name} ties", "Wall ties", Array.Empty<StrengthCheck>(), string.Empty, "Not checked: TMS 402 could not be confirmed.");

        var wind = analysis.Loading.SurfaceLoads.Where(load => ReferenceEquals(load.Panel, wall) && load.Case.Nature == LoadNature.Wind)
            .Select(load => Math.Sqrt(load.Force.X * load.Force.X + load.Force.Y * load.Force.Y)).DefaultIfEmpty(0).Max();
        var share = Math.Pow(wall.Thickness, 3) / (Math.Pow(wall.Thickness, 3) + Math.Pow(outerT, 3));
        var design = 1.5 * wind * share;
        var height = analysis.Model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).Max(panel => panel.Outline.Max(node => node.At.Z));
        var (tie, density, needed) = TiesFor(design, height / 1000);
        var cavity = type.Structure.TotalWidth - wall.Thickness - outerT;
        var openings = wall.Openings.Select(hole => hole.Select(wall.Flat).ToList()).ToList();
        var extra = openings.Sum(hole => 2 * (int)Math.Ceiling((hole.Max(p => p.Y) - hole.Min(p => p.Y)) / 300));
        var count = (int)Math.Ceiling(wall.Area / 1e6 * density);
        return new JointCheck($"{wall.Name} ties", "Wall ties", new[] { new StrengthCheck("Ties a square metre", needed, density, "/m²", "6.5, (6.20)", $"{design:0.00} kN/m² between the leaves, {share:P0} of the wind, {tie.Name}") },
            $"{tie.Name} ties {TieLength(cavity):0} long, {density:0.0} a m²: {count}, and {extra} more at its openings", "Cavity width and wind speed limits of each type are for whoever specifies them.");
    }
}
