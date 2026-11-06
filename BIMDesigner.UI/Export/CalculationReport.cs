using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Export;

/// <summary>
/// The structure's calculations as a PDF for whoever checks them, a building control body or an engineer: first what
/// they come to, what they do not cover and room to sign for them; then a key plan of each storey to find each
/// member and wall on, the basis of the design, the loads, how the analysis balances and how the structure stands up to sway,
/// and each member and wall checked clause by clause, the repeated ones once, by the worst of them.
/// </summary>
public static class CalculationReport
{
    /// <summary>How many members or walls are set out in full; past that, the worst of each kind have been, and the rest are in the list.</summary>
    private const int MostInFull = 24;

    public static void ToPdf(ReportDocument report, string path) => PdfWriter.Write(path, report.Pages());

    public static string SuggestedFileName(BimDocument document)
    {
        var name = new string(document.ProjectInformation.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return $"{(name.Length == 0 ? "Project" : name)} - Structural Calculations.pdf";
    }

    private static string F(double value, string format = "0.0") => (Math.Abs(value) < 0.05 ? 0 : value).ToString(format, CultureInfo.CurrentCulture);

    private static ReportCell Share(double ratio) =>
        new(double.IsInfinity(ratio) ? "over" : ratio.ToString("P0", CultureInfo.CurrentCulture), ratio > 1 + 1e-9 ? ReportTone.Unmet : ReportTone.Plain);

    private static string Amount(double value, string unit) => $"{F(value, "0.00")} {unit}".TrimEnd();

    /// <summary>The report, from an analysis and what was worked out from it.</summary>
    public static ReportDocument Build(BimDocument document, StructuralAnalysis analysis, SecondOrderAnalysis second, IReadOnlyList<MemberCheck> members, IReadOnlyList<WallCheck> walls, IReadOnlyList<PlateCheck> plates, IReadOnlyList<FoundationCheck> footings, IReadOnlyList<JointCheck> joints, string version, DateTime on)
    {
        var project = document.ProjectInformation;
        var us = project.ClearanceStandard.IsUs();
        var date = on.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        var report = new ReportDocument($"{project.Name} - structural calculations, {date} - BIMDesigner {version}");
        var (byMember, byWall) = (members.OrderByDescending(check => check.Ratio).ToList(), walls.OrderByDescending(check => check.Ratio).ToList());
        var (byPlate, byFooting) = (plates.OrderByDescending(check => check.Ratio).ToList(), footings.OrderByDescending(check => check.Ratio).ToList());
        var byJoint = joints.OrderByDescending(check => check.Ratio).ToList();

        report.Title("Structural Calculations", $"{project.Name}{(string.IsNullOrWhiteSpace(project.Address) ? string.Empty : $", {project.Address}")}");
        report.Facts(new[]
        {
            ("Project", string.IsNullOrWhiteSpace(project.Number) ? project.Name : $"{project.Number} - {project.Name}"),
            ("Address", string.IsNullOrWhiteSpace(project.Address) ? "-" : project.Address),
            ("Date", date),
            ("Worked out by", $"BIMDesigner {version}, from the building model and its own analysis"),
            ("Designed to", us
                ? "ASCE 7-16, AISC 360-16, the NDS 2018, ACI 318-19 and the IRC"
                : "BS EN 1990, 1991, 1992, 1993, 1995 and 1996 with their UK National Annexes, and BS 8004:1986 for presumed bearing values"),
            ("Ground", $"{StructureRules.Describe(project.Ground)}, taken, not investigated")
        });

        Summary(report, document, analysis, second, byMember, byWall, byPlate, byFooting, byJoint);

        report.Heading("Status of these calculations");
        report.Paragraph("Worked out by software from the model as it stood on the date above. They have not been reviewed: an engineer is to check them, and what they rest on, before anything is built from them.", ReportTone.Unmet);
        report.Table(new[] { new ReportColumn("", 1), new ReportColumn("Name", 2), new ReportColumn("Signature", 2), new ReportColumn("Date", 1) }, new[]
        {
            new ReportCell[] { "Worked out", $"BIMDesigner {version}", "-", date },
            new ReportCell[] { "Checked by", " ", " ", " " },
            new ReportCell[] { "Approved by", " ", " ", " " }
        });

        report.NewPage();
        KeyPlans(report, document, analysis, byMember, byWall);
        Basis(report, analysis, second);
        Loads(report, analysis);
        Analysis(report, analysis, second);
        Members(report, byMember);
        Walls(report, byWall);
        Slabs(report, byPlate);
        Joints(report, byJoint);
        Deflections(report, document, analysis);
        Foundations(report, document, analysis, byFooting);
        return report;
    }

    // ---- what they come to ------------------------------------------------------------------

    private static void Summary(ReportDocument report, BimDocument document, StructuralAnalysis analysis, SecondOrderAnalysis second, IReadOnlyList<MemberCheck> members, IReadOnlyList<WallCheck> walls, IReadOnlyList<PlateCheck> plates, IReadOnlyList<FoundationCheck> footings, IReadOnlyList<JointCheck> joints)
    {
        var checkedMembers = members.Where(check => check.Checks.Count > 0).ToList();
        var checkedWalls = walls.Where(check => check.Checks.Count > 0).ToList();
        var (checkedPlates, checkedFootings) = (plates.Where(check => check.Checks.Count > 0).ToList(), footings.Where(check => check.Checks.Count > 0).ToList());
        var checkedJoints = joints.Where(check => check.Checks.Count > 0).ToList();
        var deflections = analysis.DeflectionChecks(document);
        var bearings = analysis.BearingChecks(document);
        var buckles = second.Results.Any(result => result.Buckles);
        var over = checkedMembers.Count(check => !check.Enough) + checkedWalls.Count(check => !check.Enough) + checkedPlates.Count(check => !check.Enough) + checkedFootings.Count(check => !check.Enough) + checkedJoints.Count(check => !check.Enough) + deflections.Count(check => check.Ratio > 1) + bearings.Count(check => check.Ratio > 1);
        string Named(AnalyticalProblem problem)
        {
            var names = problem.Elements.Select(element => analysis.Model.Members.FirstOrDefault(member => member.Element == element)?.Name ?? analysis.Model.Panels.FirstOrDefault(panel => panel.Element == element)?.Name).OfType<string>().Distinct().ToList();
            return names.Count == 0 ? problem.Said : $"{string.Join(", ", names)}: {problem.Said}";
        }

        var problems = analysis.Problems.Concat(analysis.Model.Problems.Select(Named)).Concat(analysis.Loading.Problems.Select(Named)).Distinct().ToList();

        report.Heading("What they come to");
        report.Paragraph(buckles
                ? "The structure buckles before it reaches its loads: it does not stand as modelled, and nothing else here can be relied on until it is stiffened."
                : over > 0
                    ? $"{over} of the checks summarised below {(over == 1 ? "is" : "are")} over {(over == 1 ? "its" : "their")} limit: the design does not yet work as modelled."
                    : problems.Count > 0
                        ? "Everything checked is within its limits, but the model has problems that are to be put right first, below."
                        : "Everything checked here is within its limits. That is not everything: see what these calculations do not cover.",
            buckles || over > 0 || problems.Count > 0 ? ReportTone.Unmet : ReportTone.Met);

        ReportCell[]? Row(string what, int count, int failed, double worst, string where) =>
            count == 0 ? null : new ReportCell[] { what, count.ToString(CultureInfo.CurrentCulture), new(failed.ToString(CultureInfo.CurrentCulture), failed > 0 ? ReportTone.Unmet : ReportTone.Plain), Share(worst), where };

        var rows = new List<ReportCell[]?>
        {
            Row("Members, strength", checkedMembers.Count, checkedMembers.Count(check => !check.Enough), checkedMembers.Select(check => check.Ratio).DefaultIfEmpty(0).Max(),
                checkedMembers.FirstOrDefault() is { } member ? $"{member.Member.Name}: {member.Worst?.Name}" : "-"),
            Row("Walls", checkedWalls.Count, checkedWalls.Count(check => !check.Enough), checkedWalls.Select(check => check.Ratio).DefaultIfEmpty(0).Max(),
                checkedWalls.FirstOrDefault() is { } wall ? $"{wall.Panel.Name}: {wall.Worst?.Name}" : "-"),
            Row("Concrete floors and rafts", checkedPlates.Count, checkedPlates.Count(check => !check.Enough), checkedPlates.Select(check => check.Ratio).DefaultIfEmpty(0).Max(),
                checkedPlates.FirstOrDefault() is { } plate ? $"{plate.Panel.Name}: {plate.Worst?.Name}" : "-"),
            Row("Footings and piles", checkedFootings.Count, checkedFootings.Count(check => !check.Enough), checkedFootings.Select(check => check.Ratio).DefaultIfEmpty(0).Max(),
                checkedFootings.FirstOrDefault()?.Name ?? "-"),
            Row("Joints and bearings", checkedJoints.Count, checkedJoints.Count(check => !check.Enough), checkedJoints.Select(check => check.Ratio).DefaultIfEmpty(0).Max(),
                checkedJoints.FirstOrDefault() is { } joint ? $"{joint.Name}: {joint.Worst?.Name}" : "-"),
            Row("Members, deflection", deflections.Count, deflections.Count(check => check.Ratio > 1), deflections.Select(check => check.Ratio ?? 0).DefaultIfEmpty(0).Max(),
                deflections.OrderByDescending(check => check.Ratio ?? 0).FirstOrDefault() is { } bent ? bent.Member.Name : "-"),
            Row("Ground under plates", bearings.Count, bearings.Count(check => check.Ratio > 1), bearings.Select(check => check.Ratio ?? 0).DefaultIfEmpty(0).Max(),
                bearings.FirstOrDefault()?.Panel.Name ?? "-")
        };
        report.Table(new[] { new ReportColumn("Checked", 1.6), new ReportColumn("How many", 0.7, true), new ReportColumn("Over", 0.5, true), new ReportColumn("Worst", 0.6, true), new ReportColumn("Where", 2.4) }, rows.OfType<ReportCell[]>().ToList());

        if (second.Worst is { } least)
            report.Paragraph(buckles
                ? $"Sway: under {least.Combination.Name} it buckles."
                : $"Sway: the least critical load factor is {(double.IsInfinity(least.Alpha) ? "more than can be read" : F(least.Alpha))}, under {least.Combination.Name}; {second.Results.Count(result => result.SecondOrder)} of {second.Results.Count} cases were taken to second order.");

        if (problems.Count > 0)
        {
            report.Heading("Problems with the model", 2);
            foreach (var problem in problems) report.Paragraph($"- {problem}", ReportTone.Unmet);
        }

        report.Heading("What these calculations do not cover");
        var left = new List<string>
        {
            "Joints past those set out here: splices, moment joints, tying forces, holding-down bolts, the fixings of what the model holds as drawn. What a joint wants is said; it is not in the model.",
            "The settlement of the ground: a footing's bearing and its concrete are checked here, not how far it goes down.",
            "Cracking of any slab with no bars in the model, whose steel is what it needs and not what it has; and long-term deflection worked out, rather than ruled by span to depth.",
            "Fire, accidental damage and disproportionate collapse, seismic loads, movement and durability.",
            "The ground: its kind is as set on the project, and no site investigation stands behind it."
        };
        var skipped = members.Where(check => check.Checks.Count == 0).Select(check => check.Member.Name).Concat(walls.Where(check => check.Checks.Count == 0).Select(check => check.Panel.Name)).ToList();
        if (skipped.Count > 0) left.Add($"These, which could not be checked: {string.Join(", ", skipped.Take(30))}{(skipped.Count > 30 ? $" and {skipped.Count - 30} more" : string.Empty)}. {members.FirstOrDefault(check => check.Checks.Count == 0)?.Limits ?? walls.First(check => check.Checks.Count == 0).Limits}");
        foreach (var line in left) report.Paragraph($"- {line}");
    }

    // ---- where things are --------------------------------------------------------------------

    private static void KeyPlans(ReportDocument report, BimDocument document, StructuralAnalysis analysis, IReadOnlyList<MemberCheck> members, IReadOnlyList<WallCheck> walls)
    {
        var model = analysis.Model;
        var nodes = model.Nodes.Select(node => node.At).ToList();
        if (nodes.Count == 0) return;
        var (minX, maxX, minY, maxY) = (nodes.Min(p => p.X), nodes.Max(p => p.X), nodes.Min(p => p.Y), nodes.Max(p => p.Y));
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        int LevelAt(double z)
        {
            var found = 0;
            for (var i = 0; i < levels.Count; i++)
                if (levels[i].Elevation <= z + 1) found = i;
            return found;
        }

        var over = members.Where(check => !check.Enough).Select(check => check.Member).ToHashSet();
        var thin = walls.Where(check => !check.Enough).Select(check => check.Panel).ToHashSet();
        var lines = new List<(int Level, Point2D From, Point2D To, string Name, bool Over, double Weight)>();
        foreach (var panel in model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall))
        {
            var flat = panel.FlatOutline;
            Point2D Plan(double along) => new(panel.Origin.X + panel.U.X * along, panel.Origin.Y + panel.U.Y * along);
            lines.Add((LevelAt(flat.Min(point => point.Y)), Plan(flat.Min(point => point.X)), Plan(flat.Max(point => point.X)), panel.Name, thin.Contains(panel), 3));
        }

        foreach (var member in model.Members)
        {
            var (start, end) = (member.Start.At, member.End.At);
            var level = member.Kind == AnalyticalMemberKind.Column ? LevelAt(Math.Min(start.Z, end.Z)) : LevelAt((start.Z + end.Z) / 2);
            lines.Add((level, new Point2D(start.X, start.Y), new Point2D(end.X, end.Y), member.Name, over.Contains(member), member.Kind == AnalyticalMemberKind.Joist ? 0.5 : 1.2));
        }

        report.Heading("Key plans");
        report.Paragraph("Each member and wall by its name in these calculations, anything over its limit in red, the walls of other storeys faintly.", ReportTone.Muted);
        var (spanX, spanY) = (Math.Max(maxX - minX, 1), Math.Max(maxY - minY, 1));
        var outline = lines.Where(line => line.Weight >= 3).ToList();
        foreach (var group in lines.GroupBy(line => line.Level).OrderBy(group => group.Key))
        {
            report.Heading(levels.Count > group.Key ? levels[group.Key].Name : "Storey", 2);
            var drawn = group.ToList();
            var labelled = drawn.Count <= 60;
            report.Block(Math.Min(250, 495 * spanY / spanX + 24), (dc, area) =>
            {
                var scale = Math.Min((area.Width - 40) / spanX, (area.Height - 24) / spanY);
                Point At(Point2D p) => new(area.Left + 20 + (p.X - minX) * scale, area.Bottom - 12 - (p.Y - minY) * scale);
                var ink = Brushes.Black;
                var red = new SolidColorBrush(Color.FromRgb(0xB8, 0x2E, 0x22));
                var faint = new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD2)), 1.5);
                foreach (var (_, from, to, _, _, _) in outline.Where(line => !drawn.Contains(line))) dc.DrawLine(faint, At(from), At(to));
                foreach (var (_, from, to, name, tooMuch, weight) in drawn)
                {
                    var brush = tooMuch ? red : ink;
                    if (from.DistanceTo(to) < 1) dc.DrawRectangle(brush, null, new Rect(At(from).X - 3, At(from).Y - 3, 6, 6));
                    else dc.DrawLine(new Pen(brush, weight), At(from), At(to));
                    if (!labelled) continue;
                    var (a, b) = (At(from), At(to));
                    var label = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 7, brush, 1);
                    dc.DrawText(label, new Point((a.X + b.X) / 2 + 3, (a.Y + b.Y) / 2 - label.Height - 1));
                }
            });
        }
    }

    // ---- what they rest on -------------------------------------------------------------------

    private static void Basis(ReportDocument report, StructuralAnalysis analysis, SecondOrderAnalysis second)
    {
        report.Heading("Basis of the design");
        report.Paragraph("Linear elastic analysis of the structure as modelled: its members as 3D beams, its walls and concrete floors as shell plates, each load case solved and each combination added up, taken to second order where the sway calls for it.");
        report.Heading("Materials", 2);
        report.Table(new[] { new ReportColumn("Material", 2), new ReportColumn("Grade", 1.2), new ReportColumn("E, N/mm²", 1, true), new ReportColumn("From", 2) },
            analysis.Model.Members.Select(member => member.Section.Material).Concat(analysis.Model.Panels.Select(panel => panel.Material)).Distinct()
                .Select(material => new ReportCell[] { material.Name, material.Quality, F(material.Modulus, "#,0"), material.Said }));

        report.Heading("What was taken", 2);
        foreach (var note in analysis.Loading.Notes.Concat(analysis.Notes).Concat(second.Notes).Append(MemberStrength.Said)) report.Paragraph($"- {note}");
    }

    private static void Loads(ReportDocument report, StructuralAnalysis analysis)
    {
        var loading = analysis.Loading;
        report.Heading("Loads");
        report.Table(new[] { new ReportColumn("Case", 0.6), new ReportColumn("What it is", 3.2), new ReportColumn("Down, kN", 1, true), new ReportColumn("Across, kN", 1, true) },
            loading.Cases.Select(loadCase => new ReportCell[] { loadCase.Name, loadCase.Description, F(-analysis.TotalLoad(loadCase).Z), F(Math.Sqrt(Math.Pow(analysis.TotalLoad(loadCase).X, 2) + Math.Pow(analysis.TotalLoad(loadCase).Y, 2))) }));

        report.Heading("On surfaces", 2);
        report.Table(new[] { new ReportColumn("Case", 0.6), new ReportColumn("What", 3.2), new ReportColumn("kN/m²", 0.8, true), new ReportColumn("On, m²", 0.8, true) },
            loading.SurfaceLoads.GroupBy(load => (load.Case.Name, load.Said, Value: Math.Round(Math.Sqrt(load.Force.X * load.Force.X + load.Force.Y * load.Force.Y + load.Force.Z * load.Force.Z), 2)))
                .OrderBy(group => group.Key.Name, StringComparer.Ordinal)
                .Select(group => new ReportCell[] { group.Key.Name, group.Key.Said, F(group.Key.Value, "0.00"), F(group.Sum(load => load.Panel.Area) / 1e6) }));

        report.Heading("Combinations", 2);
        report.Table(new[] { new ReportColumn("Name", 0.7), new ReportColumn("Expression", 2.2), new ReportColumn("Factors", 3) },
            loading.Combinations.Select(combination => new ReportCell[] { combination.Name, combination.Description, combination.Said }));
    }

    private static void Analysis(ReportDocument report, StructuralAnalysis analysis, SecondOrderAnalysis second)
    {
        report.Heading("Analysis");
        report.Facts(new[]
        {
            ("Mesh", $"{analysis.Solver.NodeCount} nodes, {analysis.Solver.Beams.Count} beams, {analysis.Solver.Shells.Count} triangles, about {F(analysis.MeshSize, "0")} mm apart"),
            ("Model", $"{analysis.Model.Members.Count} members, {analysis.Model.Panels.Count} plates, {analysis.Model.Supports.Count} supports, {analysis.Model.Diaphragms.Count} diaphragms, {analysis.Model.Stiffeners.Count} cross wall meetings")
        });
        report.Heading("How the loads and reactions balance", 2);
        report.Table(new[] { new ReportColumn("Case", 0.7), new ReportColumn("Loads, kN (x, y, z)", 2.2), new ReportColumn("Reactions, kN (x, y, z)", 2.2), new ReportColumn("Out of balance", 1, true) },
            analysis.Loading.Cases.Select(loadCase =>
            {
                var (load, reaction) = (analysis.TotalLoad(loadCase), analysis.TotalReaction(loadCase));
                var off = load.Length < 1e-6 ? 0 : (load + reaction).Length / load.Length;
                return new ReportCell[]
                {
                    loadCase.Name, $"{F(load.X)}, {F(load.Y)}, {F(load.Z)}", $"{F(reaction.X)}, {F(reaction.Y)}, {F(reaction.Z)}",
                    new(off.ToString("P2", CultureInfo.CurrentCulture), off > 0.01 ? ReportTone.Unmet : ReportTone.Plain)
                };
            }));

        report.Heading("Sway", 2);
        report.Paragraph($"An out-of-plumb of 1 in {F(1 / second.Imperfection, "0")} is in every ultimate combination, leaning with the wind or each of four ways without it. First order is enough at αcr {F(second.Limit, "0")} or more. The least stable cases:");
        report.Table(new[] { new ReportColumn("Under", 1), new ReportColumn("αcr", 0.7, true), new ReportColumn("Amplifies sway by", 1, true), new ReportColumn("Moves at most", 1, true), new ReportColumn("What it comes to", 2.2) },
            second.Results.Take(12).Select(result => new ReportCell[]
            {
                result.Combination.Name, double.IsInfinity(result.Alpha) ? "no sway" : result.Alpha >= 1000 ? "over 1000" : F(result.Alpha),
                double.IsInfinity(result.Amplifier) ? "-" : F(result.Amplifier, "0.00"), double.IsInfinity(result.Movement) ? "-" : $"{F(result.Movement)} mm",
                new(result.Buckles ? "It buckles" : result.SecondOrder ? $"Second order, {result.Passes} passes" : "First order is enough", result.Buckles ? ReportTone.Unmet : ReportTone.Plain)
            }));
    }

    // ---- each member and wall ----------------------------------------------------------------

    private static readonly ReportColumn[] CheckColumns =
    {
        new("Check", 2.2), new("It carries", 1, true), new("It can carry", 1, true), new("Of it", 0.6, true), new("Under", 0.8), new("Clause", 1.1), new("Note", 1.7)
    };

    private static IEnumerable<ReportCell[]> CheckRows(IEnumerable<StrengthCheck> checks) => checks.Where(check => check.Ratio >= Least).OrderByDescending(check => check.Ratio).Select(check => new ReportCell[]
    {
        check.Name, Amount(check.Demand, check.Unit), Amount(check.Capacity, check.Unit), Share(check.Ratio), check.Under, check.Clause, check.Note
    });

    /// <summary>Under this share of what it can do a check says nothing: counted, not set out.</summary>
    private const double Least = 0.01;

    /// <summary>A member's or wall's checks as a table, and how many were too small to set out.</summary>
    private static void CheckTable(ReportDocument report, IReadOnlyList<StrengthCheck> checks)
    {
        report.Table(CheckColumns, CheckRows(checks));
        var quiet = checks.Count(check => check.Ratio < Least);
        if (quiet > 0) report.Paragraph($"{quiet} more check{(quiet == 1 ? "" : "s")} came to under 1% and {(quiet == 1 ? "is" : "are")} left out.", ReportTone.Muted);
    }

    private static void Members(ReportDocument report, IReadOnlyList<MemberCheck> members)
    {
        if (members.Count == 0) return;
        report.Heading("Members");
        report.Paragraph("Worst first, each by the check that governs it, over every ultimate combination and each way it leans.");
        report.Table(new[] { new ReportColumn("Member", 0.7), new ReportColumn("Kind", 0.8), new ReportColumn("Section", 1.8), new ReportColumn("Grade", 0.8), new ReportColumn("Of its strength", 0.8, true), new ReportColumn("Governed by", 2) },
            members.Select(check => new ReportCell[]
            {
                check.Member.Name, check.Member.Kind.ToString(), check.Member.Section.Name, check.Grade, check.Checks.Count > 0 ? Share(check.Ratio) : "not checked", check.Worst?.Name ?? "-"
            }));

        // The worst of each kind of member in full.
        var groups = members.Where(check => check.Checks.Count > 0).GroupBy(check => (check.Member.Kind, check.Member.Section.Name, check.Grade)).Select(group => (Worst: group.First(), Count: group.Count())).Take(MostInFull).ToList();
        foreach (var (check, count) in groups)
        {
            report.Heading($"{check.Member.Name}: {check.Member.Section.Name}, {check.Grade}{(count > 1 ? $", the worst of {count}" : string.Empty)}", 2);
            var lengths = check.Lengths;
            report.Paragraph($"By {check.By}. Buckles over {Units.Metres(lengths.Strong, "0.00")} about its strong axis and {Units.Metres(lengths.Weak, "0.00")} about its weak; {(lengths.Lateral > 0 ? $"sideways, {Units.Metres(lengths.Lateral, "0.00")}" : $"its top held, its underside free over {Units.Metres(lengths.LateralHogging, "0.00")}")}.", ReportTone.Muted);
            CheckTable(report, check.Checks);
            report.Paragraph(check.Limits, ReportTone.Muted);
        }
    }

    private static void Walls(ReportDocument report, IReadOnlyList<WallCheck> walls)
    {
        if (walls.Count == 0) return;
        report.Heading("Walls");
        report.Table(new[] { new ReportColumn("Wall", 0.6), new ReportColumn("Made of", 3), new ReportColumn("hef", 0.7, true), new ReportColumn("tef", 0.6, true), new ReportColumn("Of its strength", 0.8, true), new ReportColumn("Governed by", 1.7) },
            walls.Select(check => new ReportCell[]
            {
                check.Panel.Name, check.Grade, check.Checks.Count > 0 ? F(check.EffectiveHeight, "0") : "-", check.Checks.Count > 0 ? F(check.EffectiveThickness, "0") : "-",
                check.Checks.Count > 0 ? Share(check.Ratio) : "not checked", check.Worst?.Name ?? "-"
            }));

        foreach (var check in walls.Where(check => check.Checks.Count > 0).Take(MostInFull))
        {
            report.Heading($"{check.Panel.Name}: {check.Grade}", 2);
            report.Paragraph($"By {check.By}. {F(check.Panel.Thickness, "0")} mm {(check.Panel.Material.Kind == AnalyticalMaterialKind.Masonry ? "loaded leaf" : "wall")}, effective height {Units.Metres(check.EffectiveHeight, "0.00")}, effective thickness {F(check.EffectiveThickness, "0")} mm.", ReportTone.Muted);
            CheckTable(report, check.Checks);
        }

        foreach (var limits in walls.Where(check => check.Checks.Count > 0).Select(check => check.Limits).Where(limits => limits.Length > 0).Distinct()) report.Paragraph(limits, ReportTone.Muted);
    }

    private static string Steel(double area) => double.IsInfinity(area) ? "too thin" : F(area, "0");

    private static void Slabs(ReportDocument report, IReadOnlyList<PlateCheck> plates)
    {
        var worked = plates.Where(check => check.Checks.Count > 0).ToList();
        if (worked.Count == 0) return;
        report.Heading("Floors, roofs and rafts");
        report.Paragraph("The steel each way and face comes from Wood and Armer's equations, over squares of about a metre so a peak over a column does not stand for the slab.");
        report.Table(new[] { new ReportColumn("Plate", 0.6), new ReportColumn("Kind", 0.9), new ReportColumn("Thick", 0.6, true), new ReportColumn("Bottom x, y, mm²/m", 1.2, true), new ReportColumn("Top x, y, mm²/m", 1.2, true), new ReportColumn("Of its limit", 0.7, true), new ReportColumn("Governed by", 1.8) },
            worked.Select(check => new ReportCell[] { check.Panel.Name, check.Panel.Kind.ToString(), F(check.Panel.Thickness, "0"), $"{Steel(check.BottomX)}, {Steel(check.BottomY)}", $"{Steel(check.TopX)}, {Steel(check.TopY)}", Share(check.Ratio), check.Worst?.Name ?? "-" }));
        foreach (var check in worked.Take(MostInFull))
        {
            report.Heading($"{check.Panel.Name}: {F(check.Panel.Thickness, "0")} mm {check.Panel.Kind.ToString().ToLowerInvariant()}, {check.Grade}", 2);
            report.Paragraph($"By {check.By}.", ReportTone.Muted);
            CheckTable(report, check.Checks);
            report.Paragraph(check.Limits, ReportTone.Muted);
        }
    }

    private static void Joints(ReportDocument report, IReadOnlyList<JointCheck> joints)
    {
        if (joints.Count == 0) return;
        report.Heading("Joints and bearings");
        report.Paragraph("The model holds no padstones, lintels, ties, end plates, base plates or hangers: each joint says what it wants from the forces at it, and those are for whoever details them.");
        report.Table(new[] { new ReportColumn("Joint", 1.1), new ReportColumn("Kind", 0.9), new ReportColumn("Of its limit", 0.6, true), new ReportColumn("It wants", 3) },
            joints.Select(check => new ReportCell[] { check.Name, check.Kind, check.Checks.Count > 0 ? Share(check.Ratio) : "not checked", check.Wants.Length > 0 ? check.Wants : check.Limits }));
        foreach (var group in joints.Where(check => check.Checks.Count > 0).GroupBy(check => check.Kind))
        {
            var worst = group.First();
            report.Heading($"{worst.Name}: {worst.Kind.ToLowerInvariant()}{(group.Count() > 1 ? $", the worst of {group.Count()}" : string.Empty)}", 2);
            CheckTable(report, worst.Checks);
            if (worst.Limits.Length > 0) report.Paragraph(worst.Limits, ReportTone.Muted);
        }
    }

    private static void Deflections(ReportDocument report, BimDocument document, StructuralAnalysis analysis)
    {
        // The same section over the same span against the same limit is set out once, by the worst of them.
        var checks = analysis.DeflectionChecks(document).GroupBy(check => (check.Member.Section.Name, Span: Math.Round(check.Span / 100), check.LimitSaid))
            .Select(group => (Check: group.MaxBy(check => check.Ratio ?? 0)!, Count: group.Count())).OrderByDescending(entry => entry.Check.Ratio ?? 0).ToList();
        if (checks.Count == 0) return;
        report.Heading("Deflection");
        report.Table(new[] { new ReportColumn("Member", 0.7), new ReportColumn("Deflects", 0.9, true), new ReportColumn("Span", 0.8, true), new ReportColumn("Limit", 0.9, true), new ReportColumn("Of it", 0.6, true), new ReportColumn("Limit from", 3) },
            checks.Select(entry => (entry.Check, entry.Count)).Select(entry => (Check: entry.Check, Name: entry.Count > 1 ? $"{entry.Check.Member.Name}, and {entry.Count - 1} like it" : entry.Check.Member.Name)).Select(entry => (entry.Name, check: entry.Check)).Select(row => new ReportCell[]
            {
                row.Name, $"{F(row.check.Deflection)} mm", Units.Metres(row.check.Span, "0.00"), $"span/{F(row.check.Limit ?? 0, "0")}", row.check.Ratio is { } ratio ? Share(ratio) : "-", row.check.LimitSaid
            }));
    }

    private static void Foundations(ReportDocument report, BimDocument document, StructuralAnalysis analysis, IReadOnlyList<FoundationCheck> footings)
    {
        var bearings = analysis.BearingChecks(document);
        var designs = analysis.GroundBeamDesigns(document);
        var supports = analysis.Model.Supports;
        if (bearings.Count == 0 && designs.Count == 0 && supports.Count == 0 && footings.Count == 0) return;
        report.Heading("Foundations");
        report.Paragraph("From the analysis at full stiffness, first order.", ReportTone.Muted);
        if (bearings.Count > 0)
        {
            report.Heading("The ground under plates, all loads but wind", 2);
            report.Table(new[] { new ReportColumn("Plate", 0.6), new ReportColumn("Greatest", 0.9, true), new ReportColumn("Average", 0.9, true), new ReportColumn("Allowed", 0.9, true), new ReportColumn("Of it", 0.6, true), new ReportColumn("By", 3) },
                bearings.Select(bearing => new ReportCell[]
                {
                    bearing.Panel.Name, Units.Kilopascals(bearing.Greatest, "0"), Units.Kilopascals(bearing.Average, "0"), bearing.Allowable is { } allowed ? Units.Kilopascals(allowed, "0") : "-",
                    bearing.Ratio is { } ratio ? Share(ratio) : "-", bearing.AllowableSaid
                }));
        }

        if (footings.Count > 0)
        {
            report.Heading("Footings and piles", 2);
            report.Paragraph($"Their bearing and piles under all loads but wind, their concrete under the worst ultimate combination. {FoundationDesign.Said}", ReportTone.Muted);
            report.Table(new[] { new ReportColumn("Support", 0.6), new ReportColumn("Checked", 0.8), new ReportColumn("It has", 0.8, true), new ReportColumn("It can take", 0.9, true), new ReportColumn("Of it", 0.6, true), new ReportColumn("Note", 3) },
                footings.SelectMany(check => check.Checks.OrderByDescending(item => item.Ratio).Select(each => new ReportCell[] { check.Name, each.Name, Amount(each.Demand, each.Unit), Amount(each.Capacity, each.Unit), Share(each.Ratio), $"{each.Note} ({each.Clause})" })
                    .Concat(check.Limits.Length > 0 ? new[] { new ReportCell[] { check.Name, check.Checks.Count > 0 ? "Note" : "Not checked", "-", "-", "-", check.Limits } } : Array.Empty<ReportCell[]>())));
        }

        if (designs.Count > 0)
        {
            report.Heading("Ground beams' bars", 2);
            report.Table(new[] { new ReportColumn("Beam", 0.6), new ReportColumn("Face", 0.8), new ReportColumn("Moment, kNm", 0.9, true), new ReportColumn("Needs", 0.9, true), new ReportColumn("Has", 1.5), new ReportColumn("Of it", 0.6, true) },
                designs.SelectMany(design => new[]
                {
                    new ReportCell[] { design.Member.Name, "Bottom", F(design.Sagging), Units.SquareMillimetres(design.BottomNeeded, "0"), $"{design.Beam.BottomBars}, {Units.SquareMillimetres(design.BottomGiven, "0")}", design.BottomGiven > 0 ? Share(design.BottomNeeded / design.BottomGiven) : "-" },
                    new ReportCell[] { design.Member.Name, "Top", F(design.Hogging), Units.SquareMillimetres(design.TopNeeded, "0"), $"{design.Beam.TopBars}, {Units.SquareMillimetres(design.TopGiven, "0")}", design.TopGiven > 0 ? Share(design.TopNeeded / design.TopGiven) : "-" }
                }));
        }

        if (supports.Count > 0)
        {
            report.Heading("Reactions at the supports, all loads but wind", 2);
            report.Table(new[] { new ReportColumn("Support", 0.8), new ReportColumn("Holds", 2.4), new ReportColumn("Up, kN", 1, true), new ReportColumn("Across, kN", 1, true) },
                supports.Take(80).Select(support =>
                {
                    var reaction = analysis.ReactionOf(support, analysis.Total);
                    var holds = support.IsLine ? $"{support.Panel!.Name} along its foot" : analysis.Model.Members.FirstOrDefault(member => member.Nodes.Contains(support.Node!))?.Name ?? support.Node!.Name;
                    return new ReportCell[] { support.Name, holds, F(reaction.Fz), F(Math.Sqrt(reaction.Fx * reaction.Fx + reaction.Fy * reaction.Fy)) };
                }));
        }
    }
}
