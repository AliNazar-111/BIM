using System.Globalization;
using System.IO;
using BIMDesigner.Core.Coordination;

namespace BIMDesigner.UI.Export;

/// <summary>
/// A comparison written up to send on: the two issues and what was found, then each element added,
/// removed, changed or moved - what it is, where, and what about it - and the types and levels that
/// changed; as a PDF of A4 pages.
/// </summary>
public static class ComparisonReport
{
    private static readonly ReportColumn[] Columns =
    {
        new("Element", 0.26), new("Category", 0.14), new("Level", 0.12), new("What has changed", 0.48)
    };

    public static ReportDocument Build(ModelComparison comparison, string project, string version, DateTime on)
    {
        var date = on.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        var report = new ReportDocument($"{project} - model comparison, {date} - BIMDesigner {version}");
        report.Title("Model Comparison", project);
        report.Facts(new[]
        {
            ("Before", comparison.Before),
            ("After", comparison.After),
            ("Compared", comparison.At.ToString("d MMMM yyyy HH:mm", CultureInfo.CurrentCulture)),
            ("Found", comparison.Said()),
            ("As they were", $"{comparison.Unchanged:#,0} elements")
        });
        report.Paragraph("Elements are matched by their ids, which a project keeps from save to save and an IFC file keeps as its GlobalIds. " +
                         "A moved element has every point it is set out by shifted alike; a changed one has a setting in its Properties, its outline or its type not as it was. " +
                         "Views, sheets and annotations are not compared.", ReportTone.Muted);

        foreach (var (kind, heading) in new[] { (ChangeKind.Added, "Added"), (ChangeKind.Removed, "Removed"), (ChangeKind.Changed, "Changed"), (ChangeKind.Moved, "Moved") })
        {
            var changes = comparison.Elements.Where(change => change.Kind == kind).OrderBy(change => change.Category).ThenBy(change => change.Level).ThenBy(change => change.Name).ToList();
            if (changes.Count == 0) continue;
            report.Heading($"{heading} ({changes.Count:#,0})");
            report.Table(Columns, changes.Select(change => (IReadOnlyList<ReportCell>)new ReportCell[]
            {
                change.Name, change.Category, change.Level, kind is ChangeKind.Added or ChangeKind.Removed ? string.Empty : change.Summary
            }));
        }

        if (comparison.Types.Count > 0)
        {
            report.Heading($"Types ({comparison.Types.Count:#,0})");
            report.Table(new ReportColumn[] { new("Type", 0.3), new("Category", 0.16), new("What has changed", 0.54) },
                comparison.Types.Select(change => (IReadOnlyList<ReportCell>)new ReportCell[] { change.Name, change.Category, change.Kind == ChangeKind.Changed ? change.Summary : change.Details().First() }));
        }

        if (comparison.Levels.Count > 0)
        {
            report.Heading($"Levels ({comparison.Levels.Count:#,0})");
            report.Table(new ReportColumn[] { new("Level", 0.3), new("What has changed", 0.7) },
                comparison.Levels.Select(change => (IReadOnlyList<ReportCell>)new ReportCell[] { change.Name, change.Summary }));
        }

        if (comparison.Same) report.Paragraph("Nothing has changed.");
        return report;
    }

    public static void ToPdf(ReportDocument report, string path) => PdfWriter.Write(path, report.Pages());

    /// <summary>A suggested file name: the project's, as a comparison, dated.</summary>
    public static string SuggestedFileName(string project, DateTime on)
    {
        var name = new string(project.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return $"{(name.Length == 0 ? "Model" : name)} - Comparison {on:yyyy-MM-dd}.pdf";
    }
}
