using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One thing a member is checked for: what it has to carry, what it can, their unit and the clause, the
/// combination that asks the most, and for concrete what it comes to in steel. Where the check is an
/// equation of several forces together, what it has to carry is the equation's value and what it can is 1.
/// </summary>
public sealed record StrengthCheck(string Name, double Demand, double Capacity, string Unit, string Clause, string Note = "", string Under = "")
{
    /// <summary>How much of what it can carry it is asked to: 1 is all of it.</summary>
    public double Ratio => Capacity > 0 ? Math.Abs(Demand) / Capacity : Demand == 0 ? 0 : double.PositiveInfinity;
}

/// <summary>
/// What a member carries at a point along it under a combination: axial force, kN, tension positive;
/// shears, kN; torsion and moments, kNm, about its strong axis and its weak; and whether its top is the
/// side in tension, so its underside is what would buckle sideways.
/// </summary>
public readonly record struct MemberForces(double N, double Vy, double Vz, double T, double My, double Mz, bool TopInTension)
{
    public static MemberForces operator +(MemberForces a, MemberForces b) =>
        new(a.N + b.N, a.Vy + b.Vy, a.Vz + b.Vz, a.T + b.T, a.My + b.My, a.Mz + b.Mz, a.TopInTension);

    public MemberForces Times(double factor) => new(N * factor, Vy * factor, Vz * factor, T * factor, My * factor, Mz * factor, TopInTension);
}

/// <summary>
/// How far a member runs between what holds it, mm: to buckle about its strong axis and about its weak,
/// and sideways under bending with its top in compression and with its underside - none where a floor
/// holds it along its length. A free end doubles the two, and trebles the lengths it can buckle sideways over.
/// </summary>
public sealed record MemberLengths(double Strong, double Weak, double Lateral, double LateralHogging);

/// <summary>A member's checks by its material and the project's code: the worst of each over every ultimate combination.</summary>
public sealed record MemberCheck(AnalyticalMember Member, string Grade, string By, IReadOnlyList<StrengthCheck> Checks, MemberLengths Lengths, string Limits)
{
    /// <summary>What it comes closest to, or goes over.</summary>
    public StrengthCheck? Worst => Checks.Count == 0 ? null : Checks.MaxBy(check => check.Ratio);

    public double Ratio => Worst?.Ratio ?? 0;

    public bool Enough => Ratio <= 1 + 1e-9;
}

/// <summary>
/// Each member checked for strength from what the analysis finds in it: steel by BS EN 1993-1-1 or
/// AISC 360-16, timber by BS EN 1995-1-1 or the NDS, concrete by BS EN 1992-1-1 or ACI 318-19, the
// worst of each check over every ultimate combination, each way it leans, along the member, on the forces
/// of the second-order analysis. Each member is taken held at each end, its buckling length the longest
/// stretch between what holds it each way, as second-order analysis of the frame lets it be. Masonry walls
/// and plates are not checked.
/// </summary>
public static class MemberStrength
{
    /// <summary>What every member's check takes, for the analysis's notes.</summary>
    public const string Said =
        "Strength is checked on the forces of the second-order analysis, with the structure's out-of-plumb in them. " +
        "Each member is taken held at its ends, a free end apart, and buckles over the longest stretch between what holds it each way; a plate or support holds it both ways, another member the way that member runs. " +
        "Walls, floors and connections are not checked for strength.";

    /// <summary>Every member's checks, in the model's order.</summary>
    public static IReadOnlyList<MemberCheck> Of(BimDocument document, StructuralAnalysis analysis, SecondOrderAnalysis? secondOrder = null)
    {
        var second = secondOrder ?? SecondOrderAnalysis.Run(document, analysis);
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var joined = Joined(analysis.Model);
        var results = new List<MemberCheck>();

        // A frame that buckles fails every member in it, however strong each is.
        var buckles = second.Results.FirstOrDefault(result => result.Buckles) is { } failed
            ? new StrengthCheck("Stability of the frame", failed.Alpha > 0 ? 1 / failed.Alpha : double.PositiveInfinity, 1, "", us ? "AISC 360-16 C1" : "BS EN 1993-1-1 5.2", "it buckles before it reaches its loads: stiffen it", failed.Combination.Name)
            : null;
        foreach (var member in analysis.Model.Members)
        {
            var lengths = LengthsOf(analysis.Model, member, joined);
            var stations = second.StationsOf(member);
            var (grade, by, checks, limits) = member.Section.Material.Kind switch
            {
                AnalyticalMaterialKind.Steel => SteelChecks(member, us, lengths, stations),
                AnalyticalMaterialKind.Timber => TimberChecks(member, us, lengths, stations, document),
                AnalyticalMaterialKind.Concrete => ConcreteDesign.Checks(document, analysis, member, us, lengths, stations),
                _ => (member.Section.Material.Quality, string.Empty, (IReadOnlyList<StrengthCheck>)Array.Empty<StrengthCheck>(), "Not checked: its material has no strengths here.")
            };
            if (buckles is not null) checks = checks.Append(buckles).ToList();
            results.Add(new MemberCheck(member, grade, by, checks, lengths, limits));
        }

        return results;
    }

    /// <summary>
    /// A steel or timber member's checks for forces along it given here, as Of takes them from second order: a damaged structure's, or a key element's under its accidental load. Empty for any other
    /// material.
    /// </summary>
    public static IReadOnlyList<StrengthCheck> ChecksOf(BimDocument document, AnalyticalMember member, MemberLengths lengths, IReadOnlyList<(LoadCombination Combination, MemberForces Forces)> stations)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        return member.Section.Material.Kind switch
        {
            AnalyticalMaterialKind.Steel => SteelChecks(member, us, lengths, stations).Item3,
            AnalyticalMaterialKind.Timber => TimberChecks(member, us, lengths, stations, document).Item3,
            _ => Array.Empty<StrengthCheck>()
        };
    }

    /// <summary>The worst of each check over the stations, in the order they first came.</summary>
    public static IReadOnlyList<StrengthCheck> Worst(IEnumerable<(string Under, IEnumerable<StrengthCheck> Checks)> asked)
    {
        var worst = new Dictionary<string, StrengthCheck>();
        var order = new List<string>();
        foreach (var (under, checks) in asked)
            foreach (var check in checks)
            {
                if (!worst.TryGetValue(check.Name, out var was))
                {
                    order.Add(check.Name);
                    worst[check.Name] = check with { Under = under };
                }
                else if (check.Ratio > was.Ratio)
                    worst[check.Name] = check with { Under = under };
            }

        return order.Select(name => worst[name]).ToList();
    }

    private static (string, string, IReadOnlyList<StrengthCheck>, string) SteelChecks(AnalyticalMember member, bool us, MemberLengths lengths, IReadOnlyList<(LoadCombination Combination, MemberForces Forces)> stations)
    {
        var section = SteelSection.Of(member.Section);
        if (section is null) return (member.Section.Material.Quality, string.Empty, Array.Empty<StrengthCheck>(), $"Not checked: {member.Section.Shape} sections are not worked out here.");
        
        // F1-1 belongs to a length between braces: only where that is the whole member do its stations give it; otherwise 1.0, as F1 allows.
        var whole = Math.Abs(lengths.LateralHogging - member.Length) < 1;
        var cbs = us && whole ? stations.GroupBy(station => station.Combination.Name).ToDictionary(group => group.Key, group => Cb(group.Select(station => station.Forces).ToList())) : null;
        var checks = Worst(stations.Select(station => (station.Combination.Name, us ? SteelDesign.Aisc(section, lengths, station.Forces, cbs?[station.Combination.Name] ?? 1) : SteelDesign.Eurocode(section, lengths, station.Forces))));
        var twisted = section.IsI && stations.Count > 0 ? stations.Max(station => Math.Abs(station.Forces.T)) : 0;
        return (member.Section.Material.Quality, us ? "AISC 360-16, LRFD" : "BS EN 1993-1-1 and its UK National Annex", checks,
            (us ? "Connections, block shear and net-section rupture are not checked." : "Connections and the net section at bolt holes are not checked.")
            + (twisted > 0.05 ? $" It is twisted, up to {twisted:0.00} kNm: an open section carries torsion mostly by warping, which is not worked out here." : string.Empty));
    }

    /// <summary>
    /// The moment gradient factor of AISC 360-16 F1-1 for a combination: its moments at the quarter points of
    /// the member, the stations in order along it, 12.5 Mmax over 2.5 Mmax + 3 MA + 4 MB + 3 MC.
    /// </summary>
    private static double Cb(IReadOnlyList<MemberForces> stations)
    {
        var moments = stations.Select(forces => Math.Abs(forces.My)).ToList();
        if (moments.Count < 2) return 1;
        double At(double t) => moments[(int)Math.Round(t * (moments.Count - 1))];
        var most = moments.Max();
        return most < 1e-9 ? 1 : Math.Min(3, 12.5 * most / (2.5 * most + 3 * At(0.25) + 4 * At(0.5) + 3 * At(0.75)));
    }

    private static (string, string, IReadOnlyList<StrengthCheck>, string) TimberChecks(AnalyticalMember member, bool us, MemberLengths lengths, IReadOnlyList<(LoadCombination Combination, MemberForces Forces)> stations, BimDocument document)
    {
        // A notch cut from its underside at its ends, where its type has one.
        var (notch, notchLength) = member.Element is Beam { } drawn && document.FindType<BeamType>(drawn.TypeId) is { EndNotch: > 0 } notched ? (notched.EndNotch, notched.EndNotchLength) : (0.0, 0.0);
        var quality = member.Section.Material.Quality;
        if (member.Section.Shape != "Rectangle" || member.Section.Dimensions.Count < 2)
            return (quality, string.Empty, Array.Empty<StrengthCheck>(), $"Not checked: timber is worked out here as a rectangle, not a {member.Section.Shape}.");
        var (depth, width) = (member.Section.Dimensions[0], member.Section.Dimensions[1]);
        var joist = member.Kind == AnalyticalMemberKind.Joist;
        var glulam = quality is "GL24h" or "24F-1.8E";
        var limits = notch > 0 ? "Bearing at its supports and connections are not checked." : "Bearing at its supports and connections are not checked; it has no notch.";
        if (us)
        {
            var checks = Worst(stations.Select(station => (station.Combination.Name, TimberDesign.Nds(depth, width, glulam, joist, member.Kind == AnalyticalMemberKind.Column, lengths, member.Length, station.Forces, notch: notch, lambda: TimberDesign.TimeEffect(station.Combination)))));
            if (glulam && stations.Any(station => Math.Abs(station.Forces.N) > 0.5))
                limits = "Its axial force is not checked: NDS Table 5A's compression and tension values for 24F-1.8E were not to hand. " + limits;
            return (quality, "NDS 2018, LRFD format", checks, limits);
        }

        var grade = glulam ? TimberDesign.Gl24h : TimberDesign.C24;
        return (quality, "BS EN 1995-1-1 and its UK National Annex",
            Worst(stations.Select(station => (station.Combination.Name, TimberDesign.Accidental(station.Combination)
                ? TimberDesign.Eurocode(grade with { GammaM = 1.0 }, depth, width, joist, lengths, station.Forces, TimberDesign.Instantaneous, notch, notchLength)
                : TimberDesign.Eurocode(grade, depth, width, joist, lengths, station.Forces, TimberDesign.Kmod(station.Combination), notch, notchLength)))), limits);
    }

    // ---- what holds a member ------------------------------------------------------------

    /// <summary>What meets each node: the members and the plates.</summary>
    private sealed record Joins(Dictionary<AnalyticalNode, List<AnalyticalMember>> Members, HashSet<AnalyticalNode> Plates, HashSet<AnalyticalNode> Supports);

    private static Joins Joined(AnalyticalModel model)
    {
        var members = new Dictionary<AnalyticalNode, List<AnalyticalMember>>();
        foreach (var member in model.Members)
            foreach (var node in member.Nodes)
            {
                if (!members.TryGetValue(node, out var list)) members[node] = list = new List<AnalyticalMember>();
                list.Add(member);
            }

        var plates = model.Panels.Concat(model.Diaphragms).SelectMany(panel => panel.Outline.Concat(panel.Inside)).ToHashSet();
        var supports = model.Supports.Where(support => support.Node is not null).Select(support => support.Node!).ToHashSet();
        return new Joins(members, plates, supports);
    }

    private static Direction3D Unit(Direction3D d)
    {
        var length = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        return length < 1e-9 ? d : new Direction3D(d.X / length, d.Y / length, d.Z / length);
    }

    /// <summary>
    /// How far a member runs between what holds it each way. A plate or a support at a node holds it both
    /// ways; another member, the way that member runs. Its ends are taken held, but for an end nothing
    /// else meets. A joist is held along its top by what is laid on it, and a beam by a floor or
    /// roof plate it runs under; that does not hold its underside, in tension at its top.
    /// </summary>
    public static MemberLengths LengthsOf(AnalyticalModel model, AnalyticalMember member) => LengthsOf(model, member, Joined(model));

    private static MemberLengths LengthsOf(AnalyticalModel model, AnalyticalMember member, Joins joined)
    {
        var x = Unit(AnalyticalModel.Minus(member.End.At, member.Start.At));
        var y = Unit(member.LocalY);
        var z = AnalyticalModel.Cross(x, y);
        var held = member.Nodes.Select((node, index) =>
        {
            var ended = index == 0 || index == member.Nodes.Count - 1;
            if (ended || joined.Plates.Contains(node) || joined.Supports.Contains(node)) return (Y: true, Z: true);
            var (alongY, alongZ) = (false, false);
            foreach (var other in joined.Members[node].Where(other => other != member))
            {
                var way = Unit(AnalyticalModel.Minus(other.End.At, other.Start.At));
                alongY |= Math.Abs(AnalyticalModel.Dot(way, y)) > 0.5;
                alongZ |= Math.Abs(AnalyticalModel.Dot(way, z)) > 0.5;
            }

            return (Y: alongY, Z: alongZ);
        }).ToList();

        double Longest(Func<(bool Y, bool Z), bool> holds)
        {
            var (most, from) = (0.0, member.Start);
            for (var i = 1; i < member.Nodes.Count; i++)
            {
                if (!holds(held[i])) continue;
                most = Math.Max(most, AnalyticalModel.Distance(from.At, member.Nodes[i].At));
                from = member.Nodes[i];
            }

            return most;
        }

        var (strong, weak) = (Longest(hold => hold.Z), Longest(hold => hold.Y));
        bool Free(AnalyticalNode node) => !joined.Plates.Contains(node) && !joined.Supports.Contains(node) && joined.Members[node].Count == 1;
        var free = Free(member.Start) || Free(member.End);
        var (lengthwise, sideways) = free ? (2.0, 3.0) : (1.0, 1.0);
        var decked = member.Kind == AnalyticalMemberKind.Joist || UnderAFloor(model, member);
        return new MemberLengths(strong * lengthwise, weak * lengthwise, decked ? 0 : weak * sideways, weak * sideways);
    }

    /// <summary>Whether a floor or roof plate runs along a member, within 600 mm of it.</summary>
    private static bool UnderAFloor(AnalyticalModel model, AnalyticalMember member)
    {
        if (member.Kind == AnalyticalMemberKind.Column) return false;
        var middle = new Point3D((member.Start.At.X + member.End.At.X) / 2, (member.Start.At.Y + member.End.At.Y) / 2, (member.Start.At.Z + member.End.At.Z) / 2);
        return model.Panels.Any(panel =>
        {
            if (panel.Kind is not (AnalyticalPanelKind.Floor or AnalyticalPanelKind.Roof or AnalyticalPanelKind.GroundSlab or AnalyticalPanelKind.Raft)) return false;
            if (Math.Abs(AnalyticalModel.Dot(AnalyticalModel.Minus(middle, panel.Origin), panel.Normal)) > 600) return false;
            var outline = panel.FlatOutline;
            var flat = panel.Flat(middle);
            return Polygon2D.Contains(outline, flat) || Enumerable.Range(0, outline.Count).Any(i => Line2D.DistanceFromSegment(flat, outline[i], outline[(i + 1) % outline.Count]) <= 100);
        });
    }
}
