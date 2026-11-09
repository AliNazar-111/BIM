using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A braced wall line of the IRC, R602.10: which storey, which way and where it runs, how long it is and how far from the lines beside it, the bracing Table R602.10.3(3) asks of it by Method WSP and what
/// Table R602.10.3(4) adjusts that by, the panels along it at least as long as Table R602.10.5 asks, and what about where they stand does not do. Lengths in feet; where along it in mm.
/// </summary>
public sealed record BracedWallLine(
    string Name, string Storey, double Length, double Spacing, string Location, double Table, double Factor, string FactorSaid,
    double Required, double Provided, IReadOnlyList<(double From, double To)> Panels, IReadOnlyList<string> Faults, IReadOnlyList<string> Notes)
{
    /// <summary>What it wants over what it has.</summary>
    public double Ratio => Provided > 0 ? Required / Provided : double.PositiveInfinity;

    public bool Enough => Faults.Count == 0 && Ratio <= 1 + 1e-9;
}

/// <summary>A house's bracing by the IRC: its category, what was taken, and each braced wall line.</summary>
public sealed record BracingResult(string Category, string Said, IReadOnlyList<BracedWallLine> Lines);

/// <summary>
/// A US house's walls braced by the IRC's prescriptive rules, R602.10, in Seismic Design Categories D0 to D2, where Table R602.10.3(3) by the seismic design category applies as well as Table R602.10.3(1)
/// by the wind speed: the tables as the 2021 IRC has them, from Oregon's printing of R602.10. Each storey of stud walls is split into braced wall lines, walls within 4 ft of a line being on it, R602.10.1.2;
/// each wants Method WSP's length for its own length and where its storey stands, times the adjustments for storey height, spacing past 25 ft, the walls' and roof's dead load and walls with no gypsum
/// board inside, and has the lengths of wall with no opening at least 48 in long. The wind table is not worked out here, the wind racking of the Walls tab standing for it.
/// </summary>
public static class IrcBracing
{
    private const double Foot = 304.8;
    private const double Psf = 0.0478803;

    // Table R602.10.3(3), Method WSP: feet of braced wall panel along a braced wall line 10, 20, 30, 40 and 50 ft long. [0] a one storey house or the top storey, [1] the first storey of two or the
    // second of three, [2] the first of three; null where the table does not permit it.
    private static readonly Dictionary<string, double[]?[]> Wsp = new()
    {
        ["D0"] = new double[]?[] { new[] { 1.8, 3.6, 5.4, 7.2, 9.0 }, new[] { 3.8, 7.5, 11.3, 15.3, 18.8 }, new[] { 5.3, 10.5, 15.8, 21.0, 26.3 } },
        ["D1"] = new double[]?[] { new[] { 2.0, 4.0, 6.0, 8.0, 10.0 }, new[] { 4.5, 9.0, 13.5, 18.0, 22.5 }, new[] { 6.0, 12.0, 18.0, 24.0, 30.0 } },
        ["D2"] = new double[]?[] { new[] { 2.5, 5.0, 7.5, 10.0, 12.5 }, new[] { 5.5, 11.0, 16.5, 22.0, 27.5 }, null }
    };

    private static readonly string[] Locations = { "one storey, or the top one", "the first of two storeys, or the second of three", "the first of three storeys" };

    /// <summary>
    /// Table R602.10.3(3)'s Method WSP length, ft, for a braced wall line so long: straight lines between its rows, footnote a, and in proportion below 10 ft and past 50, as its rows run. Null where
    /// the table does not permit it, or for a category it does not cover.
    /// </summary>
    public static double? TableLength(string category, int location, double feet)
    {
        if (!Wsp.TryGetValue(category, out var rows) || location is < 0 or > 2 || rows[location] is not { } row) return null;
        if (feet <= 10) return row[0] * feet / 10;
        if (feet >= 50) return row[4] * feet / 50;
        var i = Math.Min(3, (int)Math.Floor((feet - 10) / 10));
        return row[i] + (row[i + 1] - row[i]) * (feet - 10 - 10 * i) / 10;
    }

    /// <summary>
    /// Table R602.10.3(4)'s adjustments, multiplied together: walls over 10 ft high to 1.2 at 12 ft, braced wall lines over 25 ft apart 1.2 and over 30 ft 1.4, walls under 8 psf 0.85, a roof and ceiling over
    /// 15 psf 1.2 on the storey under the roof alone and 1.1 under it and a storey or two, and no gypsum board inside the panels 1.5. What is past the table, said, and null for the factor.
    /// </summary>
    public static (double? Factor, string Said) Adjustment(double wallFeet, double spacingFeet, double wallPsf, double roofPsf, bool roofOnly, bool gypsum)
    {
        var (factor, said) = (1.0, new List<string>());
        if (wallFeet > 12 + 1e-6) return (null, $"walls {wallFeet:0.0} ft high, past the 12 ft of Table R602.10.3(4)");
        if (spacingFeet > 35 + 1e-6) return (null, $"braced wall lines {spacingFeet:0.0} ft apart, past the 35 ft of Table R602.10.1.3");
        if (wallPsf > 15 + 1e-6) return (null, $"walls of {wallPsf:0.0} psf, past the 15 psf of Table R602.10.3(4); a masonry veneer wants Method BV-WSP of R602.10.6.5, not worked out here");
        if (roofPsf > 25 + 1e-6) return (null, $"a roof and ceiling of {roofPsf:0.0} psf, past the 25 psf of Table R602.10.3(4)");
        if (wallFeet > 10)
        {
            var tall = 1 + 0.1 * (wallFeet - 10);
            (factor, _) = (factor * tall, 0);
            said.Add($"{tall:0.00} for walls {wallFeet:0.0} ft high");
        }

        if (spacingFeet > 25 + 1e-6)
        {
            var apart = spacingFeet > 30 + 1e-6 ? 1.4 : 1.2;
            factor *= apart;
            said.Add($"{apart:0.0} for lines {spacingFeet:0.0} ft apart");
        }

        if (wallPsf < 8)
        {
            factor *= 0.85;
            said.Add($"0.85 for walls of {wallPsf:0.0} psf");
        }

        if (roofPsf > 15 + 1e-6)
        {
            var heavy = roofOnly ? 1.2 : 1.1;
            factor *= heavy;
            said.Add($"{heavy:0.0} for a roof and ceiling of {roofPsf:0.0} psf");
        }

        if (!gypsum)
        {
            factor *= 1.5;
            said.Add("1.5 for no gypsum board inside");
        }

        return (factor, said.Count == 0 ? "none" : string.Join(", ", said));
    }

    /// <summary>The least length of a Method WSP braced wall panel, Table R602.10.5: 48 in for walls to 10 ft high, 53 at 11 ft and 58 at 12, mm.</summary>
    public static double LeastPanel(double wallFeet) => (wallFeet <= 10 ? 48 : 48 + 5 * Math.Min(2, wallFeet - 10)) * 25.4;

    /// <summary>The house's bracing, for a US house whose site is set; null otherwise.</summary>
    public static BracingResult? Of(BimDocument document, StructuralAnalysis analysis)
    {
        var project = document.ProjectInformation;
        if (!project.ClearanceStandard.IsUs() || FireDesign.UseOf(document) != BuildingUse.House) return null;
        var quake = analysis.Loading.Seismic ?? Seismic.Of(document, analysis.Model);
        if (quake is null) return null;
        var category = quake.IrcCategory;
        if (category is "A" or "B" or "C")
            return new BracingResult(category, $"IRC category {category}: a detached house is braced by Table R602.10.3(1), by the wind speed, R602.10.3; that table is not worked out here, the wind racking on the Walls tab standing for it.", Array.Empty<BracedWallLine>());
        if (!Wsp.ContainsKey(category))
            return new BracingResult(category, $"IRC category {category}: past the IRC's prescriptive bracing, which goes to D2; the house is to be designed, R301.2.2.", Array.Empty<BracedWallLine>());

        var us = true;
        var studs = analysis.Model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Material.Kind == AnalyticalMaterialKind.Timber && TimberWallDesign.FrameOf(document, panel, us) is not null).ToList();
        if (studs.Count == 0)
            return new BracingResult(category, $"IRC category {category}: there are no stud walls to brace.", Array.Empty<BracedWallLine>());

        // Storeys by where their walls stand, from the ground up.
        static double Foot0(AnalyticalPanel panel) => panel.Outline.Min(node => node.At.Z);
        var bases = studs.Select(Foot0).Select(z => Math.Round(z / 100) * 100).Distinct().OrderBy(z => z).ToList();
        if (bases.Count > 3)
            return new BracingResult(category, $"IRC category {category}: {bases.Count} storeys of stud walls, past the three the IRC's prescriptive bracing goes to.", Array.Empty<BracedWallLine>());

        // The roof and ceiling's dead load, psf on plan, the heaviest of them: its type's layers over its slope, a diaphragm weighing nothing in the model, its weight being on the walls.
        var roofs = analysis.Model.Panels.Concat(analysis.Model.Diaphragms).Where(panel => panel.Element is Roof && Math.Abs(panel.Normal.Z) > 0.2).ToList();
        var roofPsf = roofs.Select(panel => panel.Element is Roof roof && document.FindType<RoofType>(roof.TypeId) is { } type ? Loads.Weight(document, type.Structure) / Math.Abs(panel.Normal.Z) / Psf : 0)
            .DefaultIfEmpty(0).Max();

        var lines = new List<BracedWallLine>();
        for (var storey = 0; storey < bases.Count; storey++)
        {
            var location = bases.Count - 1 - storey;
            var mine = studs.Where(panel => Math.Abs(Math.Round(Foot0(panel) / 100) * 100 - bases[storey]) < 1).ToList();
            var name = mine.Select(panel => panel.Element is Wall wall ? document.Levels.FirstOrDefault(level => level.Id == wall.LevelId)?.Name : null).FirstOrDefault(each => each is not null) ?? $"Storey {storey + 1}";
            var wallFeet = mine.Max(panel => panel.Outline.Max(node => node.At.Z) - panel.Outline.Min(node => node.At.Z)) / Foot;
            var wallPsf = mine.Max(panel => panel.Weight) / Psf;
            var gypsum = mine.All(panel => panel.Element is Wall wall && document.GetWallType(wall) is { } type &&
                                           type.Structure.Layers.Any(layer => document.Materials.FirstOrDefault(material => material.Id == layer.MaterialId)?.Name.Contains("Plasterboard", StringComparison.OrdinalIgnoreCase) == true));
            var least = LeastPanel(wallFeet);

            foreach (var alongX in new[] { true, false })
            {
                // Walls running this way, each at its distance across, with the lengths of it with no opening, where they run along.
                var walls = new List<(double Across, double From, double To, List<(double From, double To)> Solid)>();
                foreach (var panel in mine)
                {
                    var (u, origin) = (panel.U, panel.Origin);
                    if (Math.Abs(alongX ? u.X : u.Y) < Math.Cos(10 * Math.PI / 180)) continue;
                    double Along(double x) => alongX ? origin.X + u.X * x : origin.Y + u.Y * x;
                    var flat = panel.FlatOutline;
                    var (start, end) = (flat.Min(p => p.X), flat.Max(p => p.X));
                    var cuts = panel.Cuts.Select(cut => cut.Select(panel.Flat).ToList()).Select(cut => (From: cut.Min(p => p.X), To: cut.Max(p => p.X))).OrderBy(cut => cut.From).ToList();
                    var solid = new List<(double From, double To)>();
                    var at = start;
                    foreach (var (from, to) in cuts)
                    {
                        if (from > at + 1) solid.Add((at, from));
                        at = Math.Max(at, to);
                    }

                    if (end > at + 1) solid.Add((at, end));
                    var placed = solid.Select(piece => (From: Math.Min(Along(piece.From), Along(piece.To)), To: Math.Max(Along(piece.From), Along(piece.To)))).ToList();
                    walls.Add((alongX ? origin.Y : origin.X, Math.Min(Along(start), Along(end)), Math.Max(Along(start), Along(end)), placed));
                }

                if (walls.Count == 0) continue;

                // Into lines, each wall within 4 ft of the first on it.
                var groups = new List<List<(double Across, double From, double To, List<(double From, double To)> Solid)>>();
                foreach (var wall in walls.OrderBy(wall => wall.Across))
                    if (groups.Count > 0 && wall.Across - groups[^1][0].Across <= 4 * Foot + 1) groups[^1].Add(wall);
                    else groups.Add(new() { wall });

                var places = groups.Select(group => group.Sum(wall => (wall.To - wall.From) * wall.Across) / group.Sum(wall => wall.To - wall.From)).ToList();
                for (var i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    var (from, to) = (group.Min(wall => wall.From), group.Max(wall => wall.To));
                    var feet = (to - from) / Foot;
                    var spacing = new[] { i > 0 ? places[i] - places[i - 1] : 0, i + 1 < groups.Count ? places[i + 1] - places[i] : 0 }.Max() / Foot;
                    var panels = group.SelectMany(wall => wall.Solid).Where(piece => piece.To - piece.From >= least - 1).OrderBy(piece => piece.From).ToList();
                    var provided = panels.Sum(piece => piece.To - piece.From) / Foot;
                    // A length with no opening is as many panels as fit in it, side by side: one at each end of a long one.
                    var count = panels.Sum(piece => Math.Max(1, (int)Math.Floor((piece.To - piece.From + 1) / least)));
                    var (faults, notes) = (new List<string>(), new List<string>());
                    var table = TableLength(category, location, feet);
                    var (factor, factorSaid) = Adjustment(wallFeet, spacing, wallPsf, roofPsf, location == 0, gypsum);
                    if (table is null) faults.Add($"Table R602.10.3(3) does not permit Method WSP for {Locations[location]} in {category}.");
                    if (factor is null) faults.Add($"Past the IRC's prescriptive bracing: {factorSaid}.");
                    if (groups.Count < 2) faults.Add("The only braced wall line this way: R602.10.1.3 asks two at least.");

                    // Where its panels stand, R602.10.2.2: one at each end in D0 to D2, or within 10 ft of it with 24 in round the corner or an 1,800 lb hold-down; no more than 20 ft between them; two at
                    // least on a line over 16 ft.
                    if (panels.Count == 0) faults.Add($"No length of it with no opening is {least / 25.4:0} in long, Table R602.10.5.");
                    else
                    {
                        foreach (var (gap, end) in new[] { (panels[0].From - from, "start"), (to - panels[^1].To, "end") })
                            if (gap > 10 * Foot + 1) faults.Add($"Its first panel from its {end} is {gap / Foot:0.0} ft in, more than the 10 ft of R602.10.2.2.");
                            else if (gap > 300) notes.Add($"Its panel at its {end} is {gap / Foot:0.0} ft in: R602.10.2.2.1 asks 24 in of sheathing round the corner there, or an 1,800 lb hold-down.");
                        for (var k = 1; k < panels.Count; k++)
                            if (panels[k].From - panels[k - 1].To > 20 * Foot + 1)
                                faults.Add($"{(panels[k].From - panels[k - 1].To) / Foot:0.0} ft between two of its panels, more than the 20 ft of R602.10.2.2.");
                        if (feet > 16 && count < 2) faults.Add("One panel on a line over 16 ft: R602.10.2.3 asks two.");
                    }

                    var required = (table ?? 0) * (factor ?? 1);
                    var label = $"{name}, along {(alongX ? "X" : "Y")} at {Units.Metres(Math.Abs(places[i]) < 50 ? 0 : places[i], "0.0")}";
                    lines.Add(new BracedWallLine(label, name, feet, spacing, Locations[location], table ?? 0, factor ?? 0, factorSaid, required, provided, panels, faults, notes));
                }
            }
        }

        return new BracingResult(category,
            $"IRC category {category}, from SDS {quake.Sds:0.000} by Table R301.2.2.1.1. Each braced wall line by Method WSP, Table R602.10.3(3), the greater of it and Table R602.10.3(1) by the wind speed being what " +
            $"R602.10.3 asks in D0 to D2; Table R602.10.3(1) is not worked out here. Roof and ceiling {roofPsf:0.0} psf on plan. Panels the lengths of wall with no opening at least as long as Table R602.10.5 asks.",
            lines);
    }
}
