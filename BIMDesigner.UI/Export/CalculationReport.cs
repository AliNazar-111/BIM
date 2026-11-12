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
    public static ReportDocument Build(BimDocument document, StructuralAnalysis analysis, SecondOrderAnalysis second, IReadOnlyList<MemberCheck> members, IReadOnlyList<WallCheck> walls, IReadOnlyList<PlateCheck> plates, IReadOnlyList<FoundationCheck> footings, IReadOnlyList<JointCheck> joints, IReadOnlyList<FireCheck> fires, RobustnessResult robustness, string version, DateTime on,
        ModalResult? modes = null, IReadOnlyList<FloorVibration>? floors = null)
    {
        floors ??= Array.Empty<FloorVibration>();
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

        Summary(report, document, analysis, second, byMember, byWall, byPlate, byFooting, byJoint, fires, robustness, floors);

        report.Heading("Status of these calculations");
        report.Paragraph("Worked out by software from the model as it stood on the date above. They have not been reviewed: an engineer is to check them, and what they rest on, before anything is built from them.", ReportTone.Unmet);
        report.Table(new[] { new ReportColumn("", 1), new ReportColumn("Name", 2), new ReportColumn("Signature", 2), new ReportColumn("Date", 1) }, new[]
        {
            new ReportCell[] { "Worked out", $"BIMDesigner {version}", "-", date },
            new ReportCell[] { "Checked by", string.IsNullOrWhiteSpace(project.CheckedBy) ? " " : project.CheckedBy.Trim(), " ", " " },
            new ReportCell[] { "Approved by", string.IsNullOrWhiteSpace(project.ApprovedBy) ? " " : project.ApprovedBy.Trim(), " ", " " }
        });

        // The engineer's own notes - the basis taken, what was assumed, what is to be confirmed - each paragraph as written; and
        // what has been said of each member, wall and slab in its Comments.
        var paragraphs = Paragraphs(project.EngineerNotes);
        var noted = members.Select(check => (check.Member.Name, check.Member.Element)).Concat(walls.Select(check => (check.Panel.Name, check.Panel.Element)))
            .Concat(plates.Select(check => (check.Panel.Name, check.Panel.Element)))
            .Concat(footings.Select(check => (check.Name, Element: check.Foundation)))
            .Where(each => !string.IsNullOrWhiteSpace(each.Element.Comments)).DistinctBy(each => each.Element.Id).ToList();
        if (paragraphs.Count > 0 || noted.Count > 0)
        {
            report.Heading("Engineer's notes");
            foreach (var note in paragraphs) report.Paragraph(note);
            if (noted.Count > 0)
            {
                report.Heading("Notes on members, walls, slabs and footings", 2);
                report.Table(new[] { new ReportColumn("Of", 1), new ReportColumn("Note", 4) }, noted.Select(each => new ReportCell[] { each.Name, each.Element.Comments.Trim() }));
            }
        }

        report.NewPage();
        KeyPlans(report, document, analysis, byMember, byWall);
        Basis(report, analysis, second);
        Loads(report, analysis);
        SeismicOf(report, analysis);
        BracingOf(report, document, analysis);
        Analysis(report, analysis, second);
        Members(report, byMember, second);
        Walls(report, byWall);
        Slabs(report, byPlate);
        Joints(report, byJoint);
        Fire(report, document, fires);
        RobustnessOf(report, robustness);
        RemovalOf(report, robustness);
        Deflections(report, document, analysis);
        ModesOf(report, document, analysis, modes, floors);
        Foundations(report, document, analysis, byFooting);
        return report;
    }

    /// <summary>A note's paragraphs: the text between blank lines, each run onto one line.</summary>
    public static IReadOnlyList<string> Paragraphs(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => string.Join(" ", paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())).Trim())
            .Where(paragraph => paragraph.Length > 0).ToList();

    /// <summary>What has been said of an element in its Comments, under its check as a note; nothing where nothing has.</summary>
    private static void NoteOn(ReportDocument report, Core.Elements.Element element)
    {
        if (!string.IsNullOrWhiteSpace(element.Comments)) report.Paragraph($"Note: {element.Comments.Trim()}");
    }

    // ---- what they come to ------------------------------------------------------------------

    private static void Summary(ReportDocument report, BimDocument document, StructuralAnalysis analysis, SecondOrderAnalysis second, IReadOnlyList<MemberCheck> members, IReadOnlyList<WallCheck> walls, IReadOnlyList<PlateCheck> plates, IReadOnlyList<FoundationCheck> footings, IReadOnlyList<JointCheck> joints, IReadOnlyList<FireCheck> fires, RobustnessResult robustness, IReadOnlyList<FloorVibration> floors)
    {
        var checkedMembers = members.Where(check => check.Checks.Count > 0).ToList();
        var checkedWalls = walls.Where(check => check.Checks.Count > 0).ToList();
        var (checkedPlates, checkedFootings) = (plates.Where(check => check.Checks.Count > 0).ToList(), footings.Where(check => check.Checks.Count > 0).ToList());
        var checkedJoints = joints.Where(check => check.Checks.Count > 0).ToList();
        var deflections = analysis.DeflectionChecks(document);
        var bearings = analysis.BearingChecks(document);
        var buckles = second.Results.Any(result => result.Buckles);
        var over = checkedMembers.Count(check => !check.Enough) + checkedWalls.Count(check => !check.Enough) + checkedPlates.Count(check => !check.Enough) + fires.Count(check => !check.Enough) + robustness.Ties.Count(check => !check.Enough) + checkedFootings.Count(check => !check.Enough) + checkedJoints.Count(check => !check.Enough) + deflections.Count(check => check.Ratio > 1) + bearings.Count(check => check.Ratio > 1);
        var braced = IrcBracing.Of(document, analysis);
        over += floors.Count(floor => !floor.Enough) + (analysis.Seismic?.Drifts.Count(drift => drift.Ratio > 1) ?? 0) + (braced?.Lines.Count(line => !line.Enough) ?? 0) + (robustness.Removal?.Removals.Count(removal => !removal.Enough) ?? 0);
        double RemovalShare(RemovalCheck removal) => removal.Stands ? 0 : removal.Key.Count == 0 ? double.PositiveInfinity : removal.Key.Max(check => check.Ratio);
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
            Row("Fire", fires.Count(check => check.Checks.Count > 0), fires.Count(check => !check.Enough), fires.Where(check => check.Checks.Count > 0).Select(check => check.Ratio).DefaultIfEmpty(0).Max(),
                fires.FirstOrDefault(check => check.Checks.Count > 0) is { } burnt ? $"{burnt.Name}: {burnt.Worst?.Name}" : "-"),
            Row("Ties", robustness.Ties.Count(check => check.Checks.Count > 0), robustness.Ties.Count(check => !check.Enough), robustness.Ties.Where(check => check.Checks.Count > 0).Select(check => check.Ratio).DefaultIfEmpty(0).Max(),
                robustness.Ties.FirstOrDefault(check => check.Checks.Count > 0) is { } tie ? $"{tie.Name}: {tie.Worst?.Name}" : "-"),
            robustness.Removal is { Removals.Count: > 0 } removed ? Row("Notional removal", removed.Removals.Count, removed.Removals.Count(removal => !removal.Enough), removed.Removals.Max(RemovalShare),
                removed.Removals.MaxBy(RemovalShare)!.Name) : null,
            braced is { Lines.Count: > 0 } ? Row("Braced wall lines, IRC", braced.Lines.Count, braced.Lines.Count(line => !line.Enough), braced.Lines.Max(line => line.Faults.Count > 0 ? double.PositiveInfinity : line.Ratio),
                braced.Lines.MaxBy(line => line.Faults.Count > 0 ? double.PositiveInfinity : line.Ratio)!.Name) : null,
            analysis.Seismic is { } quake ? Row("Storey drift", quake.Drifts.Count, quake.Drifts.Count(drift => drift.Ratio > 1), quake.Drifts.Select(drift => drift.Ratio).DefaultIfEmpty(0).Max(),
                quake.Drifts.MaxBy(drift => drift.Ratio) is { } drifting ? $"{drifting.Level}, {drifting.Case}" : "-") : null,
            Row("Timber floors, vibration", floors.Count, floors.Count(floor => !floor.Enough), floors.Select(floor => floor.Ratio).DefaultIfEmpty(0).Max(),
                floors.MaxBy(floor => floor.Ratio) is { } shaking ? $"{shaking.Joists} joists over {F(shaking.Span / 1000, "0.00")} m: {shaking.Checks.MaxBy(check => check.Ratio)!.Name}" : "-"),
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
            "Joints past those set out here: splices, moment joints, holding-down bolts, the fixings of what the model holds as drawn, and what connects a tie. What a joint wants is said; it is not in the model.",
            "Settlement as anything more than an estimate: each footing's is worked from a presumed stiffness of the ground, the analysis's supports not giving way as it does. Heave and consolidation over time are not worked out.",
            "Cracking of any slab with no bars in the model, whose steel is what it needs and not what it has; and long-term deflection worked out, rather than ruled by span to depth.",
            (document.ProjectInformation.ClearanceStandard.IsUs()
                ? "Seismic loads past what is set out: the overstrength combinations, collectors, a weak storey, the response history procedures, the walls' and anchors' own strength against 12.11's forces and the detailing R assumes; movement and durability. "
                : "Seismic loads in England, where Approved Document A does not usually ask for them; movement and durability. ") +
            "Impact and explosion; catenary and membrane action after a removal, a stud wall as a key element and what is attached to a key element; only the ties and anchorage of a building's class, and in Consequence Class 2b each column, beam carrying one and nominal length of wall taken away. A fire's effects as they pass from one element to the next, and joints in fire: each element stands alone here, and masonry, and stud walls with no rock fibre behind plasterboard, by the Fire Rating on their types.",
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

    /// <summary>The earthquake, where there is seismic design to do: how its base shear was come to, each level's share, each storey's drift.</summary>
    private static void SeismicOf(ReportDocument report, StructuralAnalysis analysis)
    {
        if (analysis.Loading.Seismic is not { } quake) return;
        report.Heading("Seismic");
        var modal = analysis.Seismic?.Modal;
        report.Paragraph($"{(modal is null ? "By the equivalent lateral force procedure of ASCE 7-16 12.8" : "By the modal response spectrum analysis of ASCE 7-16 12.9.1, scaled to the equivalent lateral force procedure's base shear")}, Supplement 1's site coefficients taken in. Ss {quake.Ss:0.00}, S1 {quake.S1:0.00}, Site Class {quake.Site}, Risk Category {quake.Risk}. {quake.Said}{(analysis.Seismic is { Procedure.Length: > 0 } done ? $" {done.Procedure}." : string.Empty)}");
        if (analysis.Seismic is not { } result) return;
        report.Facts(new[]
        {
            ("Site coefficients", $"Fa {quake.Fa:0.00}, Fv {quake.Fv:0.00} (Tables 11.4-1, 11.4-2)"),
            ("Design spectrum", $"SDS {quake.Sds:0.000}, SD1 {quake.Sd1:0.000} (11.4-3, 11.4-4)"),
            ("System", $"{quake.System.Name}: R {quake.System.R:0.#}, Ω0 {quake.System.Overstrength:0.#}, Cd {quake.System.Cd:0.#} (Table 12.2-1)"),
            ("Period", $"T {quake.Period:0.000} s for {Units.Metres(quake.Height, "0.0")} (12.8-7), k {quake.Exponent:0.00}"),
            ("Seismic response coefficient", $"Cs {quake.Coefficient:0.0000} by {quake.CoefficientSaid}, Ie {quake.Importance:0.00}"),
            ("Base shear", $"V = Cs W = {quake.Coefficient:0.0000} × {result.Weight:0} = {result.Shear:0.0} kN, each way{(result.Snow ? ", W with 20% of the snow, 12.7.2" : string.Empty)}"),
            ("Redundancy", quake.RedundancySaid.Length > 0 ? quake.RedundancySaid : $"ρ {quake.Redundancy:0.0}")
        });
        if (modal is not null)
            report.Table(new[] { new ReportColumn("Way", 0.4), new ReportColumn("Modes", 0.5, true), new ReportColumn("Mass they move", 0.8, true), new ReportColumn("Vt, kN", 0.7, true), new ReportColumn("V, kN", 0.7, true), new ReportColumn("Forces by", 0.7, true), new ReportColumn("Drift at", 0.7, true) },
                modal.Ways.Select(way => new ReportCell[] { way.Way, way.Modes.ToString(CultureInfo.CurrentCulture), Share(way.MassShare), F(way.Combined), F(way.Static), F(way.Scale, "0.000"), F(way.DriftScale, "0.000") }));
        if (modal is null)
            report.Table(new[] { new ReportColumn("Level", 1.2), new ReportColumn("Weight, kN", 0.8, true), new ReportColumn("Force, kN", 0.8, true), new ReportColumn("Of the base shear", 0.8, true), new ReportColumn("Its diaphragm Fpx, kN", 0.9, true) },
                result.Levels.Select(level => new ReportCell[] { level.Name, F(level.Weight, "0"), F(level.Force), Share(result.Shear > 0 ? level.Force / result.Shear : 0), F(level.Diaphragm) }));
        else
            report.Table(new[] { new ReportColumn("Level", 1.2), new ReportColumn("Weight, kN", 0.8, true), new ReportColumn("Force along x, kN", 0.8, true), new ReportColumn("Along y, kN", 0.8, true), new ReportColumn("Its diaphragm Fpx, kN", 0.9, true) },
                result.Levels.Select(level => new ReportCell[] { level.Name, F(level.Weight, "0"), F(level.Force), F(level.AlongY), F(level.Diaphragm) }));
        if (modal is not null)
            report.Paragraph("Each mode's force at each level is its mass there by its shape by Γ Sa(T) g Ie / R, Sa by 11.4.6; each storey's shears combined mode by mode by the complete quadratic combination, 5% damped, the modes not found taken as one rigid mode at 0.05 s by 12.9.1.1; each level's force the difference of the storey shears over and under it, scaled up to V where Vt is less by 12.9.1.4.1, spread over its nodes by their weight and solved as the earthquake cases with their accidental torsion. Drift at Vt, unless (12.8-6) governs Cs, 12.9.1.4.2. Each response is not combined on its own: the forces from the combined storey shears stand in for them.", ReportTone.Muted);
        report.Paragraph("Fpx by (12.10-1): the forces from a level up over the weights from it up, times its own weight, no less than 0.2 SDS Ie of its weight nor more than 0.4, (12.10-2) and (12.10-3); for its diaphragm, its chords and what joins it to the walls and frames.", ReportTone.Muted);
        report.Heading("Storey drift", 2);
        report.Table(new[] { new ReportColumn("Case", 0.5), new ReportColumn("Storey", 1.2), new ReportColumn("Height, mm", 0.7, true), new ReportColumn("Drift, mm", 0.7, true), new ReportColumn("Allowed, mm", 0.7, true), new ReportColumn("Of it", 0.6, true), new ReportColumn("θ", 0.5, true), new ReportColumn("Edge over average", 0.7, true) },
            result.Drifts.Select(drift => new ReportCell[]
            {
                drift.Case, drift.Level, F(drift.Height, "0"), F(drift.Drift, "0.0"), F(drift.Allowed, "0.0"), Share(drift.Ratio),
                new ReportCell(drift.Theta.ToString("0.000", CultureInfo.CurrentCulture), drift.Theta > drift.ThetaMax ? ReportTone.Unmet : ReportTone.Plain),
                quake.Flexible ? "-" : new ReportCell($"{drift.Torsion.ToString("0.00", CultureInfo.CurrentCulture)}{(drift.Irregular.Length > 0 ? $", {drift.Irregular}" : string.Empty)}", drift.Irregular == "1b" ? ReportTone.Unmet : ReportTone.Plain)
            }));
        foreach (var note in modal?.Notes.Where(note => note.StartsWith("The modes found", StringComparison.Ordinal)) ?? Enumerable.Empty<string>()) report.Paragraph(note, ReportTone.Muted);
        report.Paragraph((modal is null ? $"Drift is worked from Cs {quake.DriftCoefficient:0.0000}, (12.8-5)'s least not being asked of it by 12.8.6.1, so" : "Drift is worked from the modal forces as 12.9.1.4.2 has them,") + $" at {result.DriftScale:0.00} of the strength forces{(result.Cracked ? ", with concrete cracked at ACI 318-19 Table 6.6.3.1.1(a)'s shares and masonry at half, 12.7.3" : string.Empty)}. " +
                         (quake.Flexible
                             ? "The floors and roofs are of timber, flexible diaphragms by 12.3.1.1, and take no accidental torsion. "
                             : "Each level's mass is taken 5% of its width across the forces off its place, both ways, 12.8.4.2; where one edge drifts more than 1.2 times the average of the two it is torsionally irregular, and in categories C to F the torsion is amplified by Ax = (δmax / 1.2 δavg)², no more than 3, (12.8-14), in the combinations as well: as remembered. ") +
                         "Where θ is over 0.10 the drift is increased by 1 / (1 - θ), 12.8.7.", ReportTone.Muted);
        foreach (var said in result.Irregularities ?? Array.Empty<string>()) report.Paragraph($"- {said}", ReportTone.Unmet);
        if (result.Anchorage is { Count: > 0 } anchorage)
        {
            var most = anchorage.MaxBy(row => row.Pressure)!;
            var held = anchorage.Where(row => row.Anchor > 0).MaxBy(row => row.Anchor);
            report.Paragraph($"Walls out of their plane, 12.11.1: 0.4 SDS Ie of their weight and no less than 0.1 of it, the most {most.Pressure:0.00} kPa on {most.Wall}." +
                             (held is null ? string.Empty : $" Concrete and masonry walls anchored to flexible floors and roofs by (12.11-1), 0.4 SDS ka Ie Wp: the most {held.Anchor:0.0} kN/m, {held.Wall} at {held.Level}, ka {held.Ka:0.00} for a span of {Units.Metres(held.Span, "0.0")}."), ReportTone.Muted);
        }
        report.Paragraph("Design drift Cd δxe / Ie between each floor and the one under it, 12.8.6, against Table 12.12-1, taken as for a building of four storeys or fewer with its partitions and cladding made to take the drift, where it has no more. θ is the stability coefficient Px Δ Ie / (Vx hsx Cd) of (12.8-16), Px the dead and live load at and above the storey: under 0.10 P-delta need not be considered, and no more than 0.5 / Cd is allowed. ρ by 12.3.4.2(b) where the building is regular in plan with walls enough on its sides, else 1.3 in categories D to F. Storage and partitions in the seismic weight, and the overstrength combinations, are not worked out.", ReportTone.Muted);
    }

    /// <summary>The structure's natural modes, where they were worked out, what its periods come to for an earthquake, and its timber floors' vibration.</summary>
    private static void ModesOf(ReportDocument report, BimDocument document, StructuralAnalysis analysis, ModalResult? modes, IReadOnlyList<FloorVibration> floors)
    {
        if (modes is null && floors.Count == 0) return;
        report.Heading("Natural modes and floor vibration");
        if (modes is { Modes.Count: > 0 })
        {
            var (x, y, z) = (0.0, 0.0, 0.0);
            report.Table(new[] { new ReportColumn("Mode", 0.5, true), new ReportColumn("Hz", 0.6, true), new ReportColumn("Period, s", 0.7, true), new ReportColumn("Moves mostly", 1.1), new ReportColumn("Mass x", 0.6, true), new ReportColumn("Mass y", 0.6, true), new ReportColumn("Mass z", 0.6, true), new ReportColumn("So far, x / y / z", 1.4, true) },
                modes.Modes.Select(mode =>
                {
                    (x, y, z) = (x + mode.ShareX, y + mode.ShareY, z + mode.ShareZ);
                    return new ReportCell[] { mode.Number.ToString(CultureInfo.CurrentCulture), F(mode.Frequency, "0.00"), F(mode.Period, "0.000"), mode.Way, mode.ShareX.ToString("P1", CultureInfo.CurrentCulture),
                        mode.ShareY.ToString("P1", CultureInfo.CurrentCulture), mode.ShareZ.ToString("P1", CultureInfo.CurrentCulture), $"{x:P0} / {y:P0} / {z:P0}" };
                }));
            foreach (var note in modes.Notes) report.Paragraph(note, ReportTone.Muted);
            if (analysis.Loading.Seismic is { Applies: true } quake)
            {
                var cu = ModalAnalysis.UpperLimit(quake.Sd1);
                foreach (var (axis, name) in new[] { (0, "x"), (1, "y") })
                    if (modes.PeriodAlong(axis) is { } period)
                        report.Paragraph($"Along {name} the analysis gives T = {F(period, "0.000")} s, where Ta is {F(quake.Period, "0.000")} s and Cu Ta {F(cu * quake.Period, "0.000")} s (ASCE 7-16 12.8.2, Table 12.8-1); the seismic loads take Ta, on the safe side.");
            }
        }

        if (floors.Count > 0)
        {
            report.Heading("Timber floors, BS EN 1995-1-1 7.3.3", 2);
            report.Table(new[] { new ReportColumn("Floor", 1.6), new ReportColumn("f1, Hz", 0.6, true), new ReportColumn("w, mm", 0.6, true), new ReportColumn("a, mm", 0.6, true), new ReportColumn("v", 0.6, true), new ReportColumn("Limit", 0.6, true), new ReportColumn("Worst", 0.6, true) },
                floors.Select(floor => new ReportCell[]
                {
                    floor.Said, F(floor.Frequency, "0.0"), F(floor.Checks[1].Demand, "0.00"), F(floor.Checks[1].Capacity, "0.00"), F(floor.Checks[2].Demand, "0.0"), F(floor.Checks[2].Capacity, "0.0"),
                    new ReportCell(floor.Ratio.ToString("P0", CultureInfo.CurrentCulture), floor.Enough ? ReportTone.Plain : ReportTone.Unmet)
                }));
            report.Paragraph("Unloaded, the mass of the floor's build-up and joists (7.3.3(3)). f1 by (7.5), over 8 Hz or a special investigation is wanted; w under 1 kN by the UK National Annex, 1000 kdist ℓ³ kamp / 48 EI with kamp 1.05, against a of 1.8 mm to 4 m spans and 16500 / ℓ^1.1 past; v, mm/Ns², by (7.6) and (7.7) against b^(f1 ζ - 1), b = 180 - 60a to a of 1 and 160 - 40a past, ζ 0.02. The Annex's figures as remembered; the deck taken as 22 mm boards at 3,500 N/mm².", ReportTone.Muted);
        }
        else if (document.ProjectInformation.ClearanceStandard.IsUs())
            report.Paragraph("Floor vibration: the IRC and the NDS set no limit on it.", ReportTone.Muted);
    }

    /// <summary>A US house's walls braced by the IRC's prescriptive rules, where it is in categories D0 to D2.</summary>
    private static void BracingOf(ReportDocument report, BimDocument document, StructuralAnalysis analysis)
    {
        if (IrcBracing.Of(document, analysis) is not { } result) return;
        report.Heading("Wall bracing, IRC R602.10");
        report.Paragraph(result.Said);
        if (result.Lines.Count == 0) return;
        report.Table(new[] { new ReportColumn("Braced wall line", 1.6), new ReportColumn("Long, ft", 0.5, true), new ReportColumn("Apart, ft", 0.5, true), new ReportColumn("Table, ft", 0.5, true), new ReportColumn("Factor", 0.5, true), new ReportColumn("Wants, ft", 0.5, true), new ReportColumn("Has, ft", 0.5, true), new ReportColumn("Of it", 0.5, true) },
            result.Lines.Select(line => new ReportCell[]
            {
                line.Name, F(line.Length), F(line.Spacing), F(line.Table, "0.00"), line.Factor > 0 ? F(line.Factor, "0.00") : "-", F(line.Required), F(line.Provided),
                new ReportCell(line.Enough ? line.Ratio.ToString("P0", CultureInfo.CurrentCulture) : line.Faults.Count > 0 ? "not as asked" : line.Ratio.ToString("P0", CultureInfo.CurrentCulture), line.Enough ? ReportTone.Plain : ReportTone.Unmet)
            }));
        foreach (var line in result.Lines.Where(line => line.Faults.Count + line.Notes.Count > 0))
            report.Paragraph($"- {line.Name}: {string.Join(" ", line.Faults.Concat(line.Notes))}", line.Faults.Count > 0 ? ReportTone.Unmet : ReportTone.Muted);
        report.Paragraph("Table R602.10.3(3) and its adjustments of Table R602.10.3(4) as the 2021 IRC has them, from Oregon's printing of R602.10; each line's factor is the product of those for its storey's height, its spacing past 25 ft, its walls' and the roof's dead load and walls with no gypsum board inside. Method WSP throughout, as the walls are sheathed in wood structural panels; Table R602.10.3(1), by the wind speed, the cripple walls of R602.10.11, and the veneer of R602.10.6.5 are not worked out.", ReportTone.Muted);
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
                .Select(group => new ReportCell[] { group.Key.Name, group.Key.Said, F(group.Key.Value, "0.00"), F(group.Sum(load => load.LoadedArea) / 1e6) }));

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
        foreach (var buckling in second.Bucklings)
            report.Paragraph(buckling.Said, buckling.Buckles ? ReportTone.Unmet : ReportTone.Plain);
        if (second.Bucklings.Count > 0)
            report.Paragraph("The lowest load factor at which any part buckles, (K + λ Kg) φ = 0, by subspace iteration with the stiffness and stresses second order took, under the combination with the least αcr and the heaviest: where αcr reads the building leaning as a whole, this finds a slender part buckling on its own too.", ReportTone.Muted);
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

    private static void Members(ReportDocument report, IReadOnlyList<MemberCheck> members, SecondOrderAnalysis second)
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
        var groups = members.Where(check => check.Checks.Count > 0).GroupBy(check => (check.Member.Kind, check.Member.Section.Name, check.Grade))
            .Select(group => (Worst: group.First(), Count: group.Count(), Notes: group.Where(each => !string.IsNullOrWhiteSpace(each.Member.Element.Comments)).Select(each => $"{each.Member.Name}: {each.Member.Element.Comments.Trim()}").Distinct().ToList()))
            .Take(MostInFull).ToList();
        foreach (var (check, count, notes) in groups)
        {
            report.Heading($"{check.Member.Name}: {check.Member.Section.Name}, {check.Grade}{(count > 1 ? $", the worst of {count}" : string.Empty)}", 2);
            foreach (var note in notes) report.Paragraph($"Note on {note}");
            var lengths = check.Lengths;
            report.Paragraph($"By {check.By}. Buckles over {Units.Metres(lengths.Strong, "0.00")} about its strong axis and {Units.Metres(lengths.Weak, "0.00")} about its weak; {(lengths.Lateral > 0 ? $"sideways, {Units.Metres(lengths.Lateral, "0.00")}" : $"its top held, its underside free over {Units.Metres(lengths.LateralHogging, "0.00")}")}.", ReportTone.Muted);
            CheckTable(report, check.Checks);
            Diagrams(report, second, check);
            report.Paragraph(check.Limits, ReportTone.Muted);
        }
    }

    /// <summary>What the worst of a kind of member carries along it under the combination that governs it: a beam's moment on the side in tension and its shear, a column's load and its moment.</summary>
    private static void Diagrams(ReportDocument report, SecondOrderAnalysis second, MemberCheck check)
    {
        if (check.Worst?.Under is not { Length: > 0 } under) return;
        var along = second.DiagramOf(check.Member, under);
        if (along.Count < 2) return;
        var column = check.Member.Kind == AnalyticalMemberKind.Column;
        double Bending(MemberForces forces) => column ? forces.My : (forces.TopInTension ? -1 : 1) * Math.Abs(forces.My);
        var moment = along.Select(station => (station.At, Bending(station.Forces))).ToList();
        var other = along.Select(station => (station.At, column ? station.Forces.N : station.Forces.Vz)).ToList();
        report.Paragraph($"Under {under}, which governs it. {(column ? "Its load, compression below, and its moment" : "Its moment, on the side in tension, and its shear")}, along its {Units.Metres(along.Max(station => station.At) - along.Min(station => station.At), "0.00")}.", ReportTone.Muted);
        report.Block(92, (dc, area) =>
        {
            var half = (area.Width - 18) / 2;
            var (left, right) = (new Rect(area.Left, area.Top, half, area.Height), new Rect(area.Left + half + 18, area.Top, half, area.Height));
            if (column)
            {
                Plot(dc, left, "Load, kN", other, false);
                Plot(dc, right, "Moment, kNm", moment, false);
            }
            else
            {
                Plot(dc, left, "Moment, kNm", moment, true);
                Plot(dc, right, "Shear, kN", other, false);
            }
        });
    }

    /// <summary>One line of figures along a member over its length, its greatest and least said. Drawn from zero, the way a checker sketches it; <paramref name="down"/> takes positive figures below the line.</summary>
    private static void Plot(DrawingContext dc, Rect area, string title, IReadOnlyList<(double At, double Value)> points, bool down)
    {
        var ink = new SolidColorBrush(Color.FromRgb(0x2A, 0x4D, 0x7A));
        var fill = new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xF2));
        var grey = new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x98)), 0.75);
        var typeface = new Typeface("Segoe UI");
        FormattedText Text(string text, double size, Brush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, 1);
        var label = Text(title, 8, Brushes.Black);
        dc.DrawText(label, new Point(area.Left, area.Top));

        var plot = new Rect(area.Left + 4, area.Top + label.Height + 8, area.Width - 8, area.Height - label.Height - 18);
        var (first, last) = (points.Min(point => point.At), points.Max(point => point.At));
        var (low, high) = (Math.Min(0, points.Min(point => point.Value)), Math.Max(0, points.Max(point => point.Value)));
        var range = Math.Max(high - low, 1e-9);
        double X(double at) => plot.Left + (at - first) / Math.Max(last - first, 1e-9) * plot.Width;
        double Y(double value) => (down ? plot.Top + (value - low) / range * plot.Height : plot.Bottom - (value - low) / range * plot.Height);

        var shape = new StreamGeometry();
        using (var context = shape.Open())
        {
            context.BeginFigure(new Point(X(points[0].At), Y(0)), true, true);
            foreach (var (at, value) in points) context.LineTo(new Point(X(at), Y(value)), true, false);
            context.LineTo(new Point(X(points[^1].At), Y(0)), true, false);
        }

        shape.Freeze();
        dc.DrawGeometry(fill, new Pen(ink, 0.9), shape);
        dc.DrawLine(grey, new Point(plot.Left, Y(0)), new Point(plot.Right, Y(0)));

        void Peak((double At, double Value) point)
        {
            if (Math.Abs(point.Value) < 0.005 * range) return;
            var text = Text(point.Value.ToString("0.0", CultureInfo.CurrentCulture), 7, Brushes.Black);
            var x = Math.Clamp(X(point.At) - text.Width / 2, area.Left, area.Right - text.Width);
            var y = Math.Clamp(Y(point.Value) + (Y(point.Value) >= Y(0) ? 1 : -text.Height - 1), area.Top + label.Height, area.Bottom - text.Height);
            dc.DrawText(text, new Point(x, y));
        }

        Peak(points.MaxBy(point => point.Value));
        Peak(points.MinBy(point => point.Value));
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
            NoteOn(report, check.Panel.Element);
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
            NoteOn(report, check.Panel.Element);
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

    /// <summary>How long it has to stand in a fire, and every element of structure against that, grouped where they come to the same.</summary>
    private static void Fire(ReportDocument report, BimDocument document, IReadOnlyList<FireCheck> fires)
    {
        report.Heading("Fire");
        report.Paragraph(FireDesign.PeriodOf(document).Said + ".");
        if (fires.Count == 0) return;
        report.Paragraph("A structure carrying only a roof and the lowest floor are not elements of structure, Approved Document B 5.3. Forces in fire are its weight and the frequent value of what is on it.", ReportTone.Muted);
        var groups = fires.GroupBy(check => (check.Kind, check.Wants, Ratio: Math.Round(check.Ratio, 2), Governs: check.Worst?.Name)).Select(group => (Check: group.First(), Count: group.Count())).ToList();
        report.Table(new[] { new ReportColumn("Element", 1.3), new ReportColumn("Kind", 0.5), new ReportColumn("Stands for", 0.6, true), new ReportColumn("Of its limit", 0.6, true), new ReportColumn("It wants", 2.4) },
            groups.Select(each => new ReportCell[]
            {
                each.Count > 1 ? $"{each.Check.Name}, and {each.Count - 1} like it" : each.Check.Name, each.Check.Kind, $"{each.Check.Minutes} min", each.Check.Checks.Count > 0 ? Share(each.Check.Ratio) : "not checked", each.Check.Wants.Length > 0 ? each.Check.Wants : "-"
            }));

        foreach (var (check, count) in groups.Where(each => each.Check.Checks.Count > 0 && each.Check.Worst?.Name != "Fire Rating").GroupBy(each => each.Check.Kind).Select(group => group.First()).Take(MostInFull))
        {
            report.Heading($"{check.Name}: {check.Kind.ToLowerInvariant()}{(count > 1 ? $", the worst of {count}" : string.Empty)}", 2);
            CheckTable(report, check.Checks);
            if (check.Limits.Length > 0) report.Paragraph(check.Limits, ReportTone.Muted);
        }

        foreach (var unjudged in fires.Where(check => check.Checks.Count == 0).Select(check => check.Limits).Distinct()) report.Paragraph(unjudged, ReportTone.Unmet);
    }

    /// <summary>The building's consequence class, what it asks and each tie.</summary>
    /// <summary>A Consequence Class 2b building's notional removals, where they were worked out.</summary>
    private static void RemovalOf(ReportDocument report, RobustnessResult robustness)
    {
        if (robustness.Removal is not { } result) return;
        report.Heading("Notional removal", 2);
        report.Paragraph(result.Said);
        report.Table(new[] { new ReportColumn("Taken away", 1.4), new ReportColumn("Stands", 0.45), new ReportColumn("Parts over", 0.5, true), new ReportColumn("Worst", 0.9), new ReportColumn("Floor at risk, m²", 1.5), new ReportColumn("Key element", 0.8) },
            result.Removals.Select(removal => new ReportCell[]
            {
                removal.Name, removal.Stands ? "yes" : "no", removal.Failed.ToString(CultureInfo.CurrentCulture), removal.Failed > 0 ? $"{removal.Worst.ToString("P0", CultureInfo.CurrentCulture)}, {removal.WorstWhere}" : "-",
                removal.AtRisk.Count == 0 ? "none" : string.Join("; ", removal.AtRisk.Select(at => $"{at.Level} {F(at.Area / 1e6)} of {F(at.Limit / 1e6)}")),
                removal.Stands ? "-" : removal.Key.Count == 0 ? new ReportCell("not worked out", ReportTone.Unmet) : new ReportCell(removal.Key.Max(check => check.Ratio).ToString("P0", CultureInfo.CurrentCulture), removal.Enough ? ReportTone.Plain : ReportTone.Unmet)
            }));
        report.Paragraph(result.Limits, ReportTone.Muted);
    }

    private static void RobustnessOf(ReportDocument report, RobustnessResult robustness)
    {
        report.Heading("Robustness");
        report.Paragraph(robustness.Said);
        if (robustness.Asks.Length > 0) report.Paragraph(robustness.Asks);
        if (robustness.Ties.Count > 0)
            report.Table(new[] { new ReportColumn("Tie", 1.4), new ReportColumn("Kind", 0.9), new ReportColumn("Of its limit", 0.6, true), new ReportColumn("It wants", 2.4) },
                robustness.Ties.GroupBy(check => (check.Kind, check.Wants)).Select(group => (Check: group.First(), Count: group.Count())).Select(each => new ReportCell[]
                {
                    each.Count > 1 ? $"{each.Check.Name}, and {each.Count - 1} like it" : each.Check.Name, each.Check.Kind, each.Check.Checks.Count > 0 ? Share(each.Check.Ratio) : "-", each.Check.Wants.Length > 0 ? each.Check.Wants : each.Check.Limits
                }));
        if (robustness.Limits.Length > 0) report.Paragraph(robustness.Limits, ReportTone.Muted);
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
                    .Concat(check.Limits.Length > 0 ? new[] { new ReportCell[] { check.Name, check.Checks.Count > 0 ? "Note" : "Not checked", "-", "-", "-", check.Limits } } : Array.Empty<ReportCell[]>())
                    .Concat(string.IsNullOrWhiteSpace(check.Foundation.Comments) ? Array.Empty<ReportCell[]>() : new[] { new ReportCell[] { check.Name, "Engineer's note", "-", "-", "-", check.Foundation.Comments.Trim() } })));
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
