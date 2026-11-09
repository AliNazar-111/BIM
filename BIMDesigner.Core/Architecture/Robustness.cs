using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Architecture;

/// <summary>What the building's robustness comes to: the class it falls in, why, what that asks of it, each tie it wants, and what is left out.</summary>
public sealed record RobustnessResult(string Class, string Said, string Asks, IReadOnlyList<JointCheck> Ties, string Limits)
{
    /// <summary>Its notional removals, for a building in Consequence Class 2b, once they are worked out.</summary>
    public RemovalResult? Removal { get; init; }
}

/// <summary>
/// Whether a building is robust enough that an accident brings down no more than it should: Approved Document A's Requirement A3. Its consequence class by Table 11, from what it is used for and how many storeys it has,
/// and what paragraph 5.1 asks of that class: for 1, nothing beyond designing it properly; for 2a, horizontal ties, or floors anchored to the walls they bear on; for 2b, those ties and vertical ties as well, or the notional removal
/// of each support in turn, which is not worked out here; for 3, a risk assessment. The ties by Annex A of BS EN 1991-1-7, under the accidental combination. In the US, the general structural integrity of ASCE 7-16 1.4 as remembered.
/// </summary>
public static class Robustness
{
    /// <summary>Approved Document A's Table 11, for the uses worked out here; nothing for any other.</summary>
    public static string ClassOf(BuildingUse use, int storeys) => use switch
    {
        BuildingUse.House => storeys <= 4 ? "1" : storeys == 5 ? "2a" : "3",
        BuildingUse.Flats or BuildingUse.Office => storeys <= 4 ? "2a" : storeys <= 15 ? "2b" : "3",
        _ => string.Empty
    };

    /// <summary>A framed building's tie, kN, from (gk + ψ qk) s L, the accidental load on the area it ties: 0.8 of it within, (A.1), or 0.4 round the edge, (A.2), and never under 75.</summary>
    public static double FramedTie(double load, bool perimeter) => Math.Max((perimeter ? 0.4 : 0.8) * load, 75);

    /// <summary>Ft, kN/m: 20 + 4 for each storey and at most 60, A.5.2(2). All a peripheral tie needs to be, (A.4).</summary>
    public static double BasicTie(int storeys) => Math.Min(60, 20 + 4 * storeys);

    /// <summary>A loadbearing wall building's internal tie, kN/m, (A.3), from gk + ψ qk, kN/m², and z, m.</summary>
    public static double WallTie(int storeys, double load, double z)
    {
        var basic = BasicTie(storeys);
        return Math.Max(basic, basic * load / 7.5 * z / 5);
    }

    /// <summary>What a wall has to carry as a vertical tie, kN, (A.5): 34 A / 8000 (H/t)² N from its area on plan, mm², or 100 kN/m, the greater.</summary>
    public static double VerticalTie(double area, double height, double thickness, double length) =>
        Math.Max(34 * area / 8000 * Math.Pow(height / thickness, 2) / 1000, 100 * length / 1000);

    /// <summary>What a member's section pulls apart at in an accident, kN, its partial factors at 1: nothing for concrete, whose bars are not in the model.</summary>
    private static double Pulls(AnalyticalMember member)
    {
        switch (member.Section.Material.Kind)
        {
            case AnalyticalMaterialKind.Steel when SteelSection.Of(member.Section) is { } steel:
                return steel.Area * SteelDesign.YieldStrength(Math.Max(steel.Tf, steel.Tw)) / 1000;
            case AnalyticalMaterialKind.Timber:
                return member.Section.Area * (member.Section.Material.Quality.StartsWith("GL", StringComparison.Ordinal) ? TimberDesign.Gl24h : TimberDesign.C24).Ft0 * 1.1 / 1000;
            default:
                return 0;
        }
    }

    private static JointCheck Tied(string name, string kind, AnalyticalMember member, double force, string clause, string note)
    {
        var carries = Pulls(member);
        if (carries <= 0)
            return new JointCheck(name, kind, Array.Empty<StrengthCheck>(), $"{Math.Ceiling(force * 1000 / 500):0} mm² of continuous bars for {force:0} kN", "Its bars are not in the model.");
        return new JointCheck(name, kind, new[] { new StrengthCheck("Its section", force, carries, "kN", clause, note) }, $"its ends and splices to carry {force:0} kN in tension", "The connections are for whoever details them.");
    }

    /// <summary>What the heaviest span of a beam carries under a combination, kN: what the two ends of that span hold up. A tie goes by its span, and a beam over a post has two.</summary>
    private static double Carried(StructuralAnalysis analysis, AnalyticalMember member, object loading) =>
        analysis.BeamsOf(member).GroupBy(piece => piece.Span).Select(span =>
        {
            var pieces = span.ToList();
            return (Math.Abs(analysis.BeamForces(pieces[0].Beam, loading)[2]) + Math.Abs(analysis.BeamForces(pieces[^1].Beam, loading)[8])) / 1000;
        }).DefaultIfEmpty(0).Max();

    /// <summary>The most a column takes from any one storey under a combination, kN, A.6(2): the steps in its load up its length, and at its top what lands there beyond what the column above brings down.</summary>
    private static double LargestStorey(StructuralAnalysis analysis, AnalyticalMember member, object loading)
    {
        IReadOnlyList<(double Bottom, double Top)> LoadsUp(AnalyticalMember column)
        {
            var loads = analysis.BeamsOf(column).Select(piece => analysis.BeamForces(piece.Beam, loading)).Select(f => (Bottom: Math.Abs(f[0]) / 1000, Top: Math.Abs(f[6]) / 1000)).ToList();
            return column.Start.At.Z <= column.End.At.Z ? loads : Enumerable.Reverse(loads).Select(load => (Bottom: load.Top, Top: load.Bottom)).ToList();
        }

        var ordered = LoadsUp(member);
        if (ordered.Count == 0) return 0;
        var top = member.Start.At.Z >= member.End.At.Z ? member.Start : member.End;
        var above = analysis.Model.Members.FirstOrDefault(other => other.Kind == AnalyticalMemberKind.Column && !ReferenceEquals(other, member) && ReferenceEquals(other.Start.At.Z <= other.End.At.Z ? other.Start : other.End, top));
        var largest = Math.Abs(ordered[^1].Top - (above is null ? 0 : LoadsUp(above).FirstOrDefault().Bottom));
        for (var i = 0; i + 1 < ordered.Count; i++) largest = Math.Max(largest, Math.Abs(ordered[i].Top - ordered[i + 1].Bottom));
        return largest;
    }

    public static RobustnessResult Of(BimDocument document, SecondOrderAnalysis second)
    {
        var analysis = second.Analysis;
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return Us(analysis);

        var use = FireDesign.UseOf(document);
        var (storeys, _) = FireDesign.HeightOf(document);
        var cc = ClassOf(use, storeys);
        if (cc.Length == 0) return new RobustnessResult("-", "Set the Building Type in the plan's Properties: only houses, flats and offices are classed here.", string.Empty, Array.Empty<JointCheck>(), string.Empty);

        var said = $"Consequence Class {cc}: {(use == BuildingUse.House ? "a house" : use == BuildingUse.Flats ? "flats" : "an office")} of {storeys} storey{(storeys == 1 ? "" : "s")}, Approved Document A Table 11.";
        if (cc == "1") return new RobustnessResult(cc, said, "No additional measures: designed and built to Requirements A1 and A2, paragraph 5.1(b).", Array.Empty<JointCheck>(), string.Empty);

        var model = analysis.Model;
        var accidental = FireDesign.Combination(document, analysis.Loading);
        var nodes = model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).SelectMany(panel => panel.Outline).Concat(model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column).SelectMany(member => member.Nodes)).ToList();
        var (minX, maxX, minY, maxY) = nodes.Count == 0 ? (0.0, 0.0, 0.0, 0.0) : (nodes.Min(node => node.At.X), nodes.Max(node => node.At.X), nodes.Min(node => node.At.Y), nodes.Max(node => node.At.Y));
        bool OnEdge(AnalyticalMember member, double near) =>
            member.Nodes.All(node => Math.Abs(node.At.X - minX) <= near) || member.Nodes.All(node => Math.Abs(node.At.X - maxX) <= near)
            || member.Nodes.All(node => Math.Abs(node.At.Y - minY) <= near) || member.Nodes.All(node => Math.Abs(node.At.Y - maxY) <= near);

        var ties = new List<JointCheck>();
        foreach (var beam in model.Members.Where(member => member.Kind == AnalyticalMemberKind.Beam))
        {
            var perimeter = OnEdge(beam, 500);
            var load = Carried(analysis, beam, accidental);
            ties.Add(Tied($"{beam.Name} as a tie", perimeter ? "Perimeter tie" : "Internal tie", beam, FramedTie(load, perimeter), perimeter ? "A.5.1, (A.2)" : "A.5.1, (A.1)", $"from {load:0.0} kN on it"));
        }

        var upper = cc != "2a";
        if (upper)
            foreach (var column in model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column))
            {
                var force = LargestStorey(analysis, column, accidental);
                ties.Add(Tied($"{column.Name} as a tie", "Vertical tie", column, force, "A.6(2)", "the most it takes from a storey"));
            }

        // Floors bearing on walls.
        var ground = Wind.Ground(document);
        var walls = model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).ToList();
        foreach (var floor in document.All<Floor>().Where(floor => floor.GetTopElevation(document) > ground + 300 && walls.Count > 0))
        {
            var type = document.FindType<SlabType>(floor.TypeId);
            var name = $"{type?.Name ?? "Floor"} on {document.FindLevel(floor.LevelId)?.Name}";
            if (!upper)
            {
                ties.Add(new JointCheck(name, "Floor anchorage", Array.Empty<StrengthCheck>(), "anchored to each wall it bears on, in a cellular plan", "A.5.2(1): the anchorage is for whoever details it."));
                continue;
            }

            // z is taken at 5H, as much as it can come to; (gk + ψ qk) from its build-up and what is on it.
            var height = walls.Max(panel => panel.FlatOutline.Max(p => p.Y) - panel.FlatOutline.Min(p => p.Y)) / 1000;
            var imposed = StructuralLoading.FloorImposed(document, floor).Load;
            var load = (type is null ? 0 : Loads.Weight(document, type.Structure)) + StructuralLoading.Psi(document, LoadNature.Imposed).Frequent * imposed;
            var within = WallTie(storeys, load, 5 * height);
            var round = BasicTie(storeys);
            ties.Add(new JointCheck(name, "Floor ties", Array.Empty<StrengthCheck>(), $"internal ties of {within:0} kN/m both ways and {round:0} kN/m round its edge", "A.5.2(2): z taken at 5H, the most it can be. Ties are not in the model."));
        }

        // Floors on columns with no beams to tie them: the bars in the slab do it, A.5.1(2), along each line of columns, s and L each the grid they stand on.
        var columns = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column).ToList();
        if (walls.Count == 0 && columns.Count > 1 && !model.Members.Any(member => member.Kind == AnalyticalMemberKind.Beam))
        {
            var spots = columns.Select(column => (column.Start.At.X, column.Start.At.Y)).Distinct().ToList();
            var grid = spots.Count < 2 ? 0 : spots.Max(spot => spots.Where(other => other != spot).Min(other => Math.Sqrt(Math.Pow(spot.X - other.X, 2) + Math.Pow(spot.Y - other.Y, 2)))) / 1000;
            foreach (var floor in document.All<Floor>().Where(floor => floor.GetTopElevation(document) > ground + 300))
            {
                var type = document.FindType<SlabType>(floor.TypeId);
                var load = ((type is null ? 0 : Loads.Weight(document, type.Structure)) + StructuralLoading.Psi(document, LoadNature.Imposed).Frequent * StructuralLoading.FloorImposed(document, floor).Load) * grid * grid;
                var (within, round) = (FramedTie(load, false), FramedTie(load, true));
                ties.Add(new JointCheck($"{type?.Name ?? "Floor"} on {document.FindLevel(floor.LevelId)?.Name}", "Floor ties", Array.Empty<StrengthCheck>(),
                    $"{Math.Ceiling(within * 1000 / 500):0} mm² of continuous bars for {within:0} kN along each line of columns, {Math.Ceiling(round * 1000 / 500):0} mm² for {round:0} kN round its edge",
                    $"A.5.1(2): (A.1) and (A.2) on a {grid:0.0} m grid. Its bars are not in the model."));
            }
        }

        if (upper)
            foreach (var wall in walls.Where(panel => panel.Material.Kind == AnalyticalMaterialKind.Masonry))
            {
                var flat = wall.FlatOutline;
                var (height, length) = (flat.Max(p => p.Y) - flat.Min(p => p.Y), flat.Max(p => p.X) - flat.Min(p => p.X));
                var force = VerticalTie(wall.Thickness * length, height, wall.Thickness, length);
                var checks = new[]
                {
                    new StrengthCheck("Thickness", 150, wall.Thickness, "mm", "A.6(3)(a)"),
                    new StrengthCheck("Clear height", height / wall.Thickness, 20, "H/t", "A.6(3)(b)")
                };
                ties.Add(new JointCheck($"{wall.Name} as a tie", "Vertical tie", checks, $"vertical ties for {force:0} kN, at most 5 m apart and 2.5 m from a free end", "A.6(3): its unit strength of at least 5 N/mm² is for whoever specifies it."));
            }

        var asks = cc switch
        {
            "2a" => "Effective horizontal ties, or suspended floors anchored to the walls they bear on: paragraph 5.1(c).",
            "2b" => "Effective horizontal ties and vertical ties in every supporting column and wall: paragraph 5.1(d).",
            _ => "A systematic risk assessment of the building, which is not done here: paragraph 5.1(e). The ties of Class 2b are given as a start."
        };
        var limits = cc == "2a" ? string.Empty : "Notional removal of each support, the alternative in paragraph 5.1(d), and key elements of 34 kN/m² are on the Removal tab.";
        return new RobustnessResult(cc, said, asks, ties.OrderByDescending(tie => tie.Ratio).ToList(), limits);
    }

    private static RobustnessResult Us(StructuralAnalysis analysis)
    {
        var dead = analysis.Loading.Cases.Where(each => each.Nature is LoadNature.SelfWeight or LoadNature.Dead or LoadNature.Imposed).Select(each => (each, 1.0)).ToList();
        var ties = analysis.Model.Members.Where(member => member.Kind == AnalyticalMemberKind.Beam).Select(beam =>
        {
            var reaction = Carried(analysis, beam, dead) / 2;
            return new JointCheck($"{beam.Name} on its supports", "Connection to supports", Array.Empty<StrengthCheck>(), $"a positive connection for {0.05 * reaction:0.0} kN along it at each end", "ASCE 7-16 1.4.3: 5% of what it brings down, dead and live.");
        }).ToList();
        return new RobustnessResult("-", "ASCE 7-16 1.4, general structural integrity.", "A continuous load path; each storey held against 1% of its weight; each beam tied to its supports; walls anchored to what holds them.", ties,
            "The figures of 1.4 are as remembered and not read from the standard. Seismic Design Categories B to F are taken to meet them.");
    }
}
