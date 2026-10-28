using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// Clashes handed on as issues: as BCF 2.1 (buildingSMART's BIM Collaboration Format) - a zip of
/// one folder per clash, named by its GUID, holding its markup (the topic: title, status, priority,
/// who it is assigned to, when it is due, its comments) and a viewpoint (a camera looking at the
/// clash and the two elements selected by their IFC GUIDs, in metres) with a snapshot where one
/// is given - which Solibri, Navisworks, BIMcollab and the rest open against the IFC; as an HTML
/// report; or as a spreadsheet.
/// </summary>
public static class ClashReports
{
    private static string IfcGuid(Guid id) => id.ToIfc().ToString();

    private static string Iso(DateTime date) => date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The clashes worth handing on: every one still open, or all of them.</summary>
    private static IEnumerable<(ClashTest Test, Clash Clash)> Chosen(IEnumerable<ClashTest> tests, bool openOnly) =>
        tests.SelectMany(test => test.Results.Where(clash => !openOnly || clash.IsOpen).Select(clash => (test, clash)));

    /// <summary>
    /// Writes clashes as a BCF 2.1 file. A snapshot for a clash, a PNG, goes in with it where one
    /// is given; the author is who the topics are said to come from.
    /// </summary>
    public static void WriteBcf(Stream output, BimDocument document, IEnumerable<ClashTest> tests, string author, Func<Clash, byte[]?>? snapshot = null, bool openOnly = true)
    {
        var index = 0;
        var issues = Chosen(tests, openOnly).Select(chosen => Issues.FromClash(chosen.Test, chosen.Clash, author, snapshot?.Invoke(chosen.Clash), ++index)).ToList();
        BcfFile.Write(output, document, issues, "2.1");
    }

    /// <summary>The clashes as a web page: each test, what it checks and when it ran, and each clash with what it is between, where, its status and who has it - a picture of it where one is given.</summary>
    public static string Html(BimDocument document, IEnumerable<ClashTest> tests, Func<Clash, byte[]?>? snapshot = null, bool openOnly = false)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(E(document.ProjectInformation.Name)).Append(" - clash report</title>");
        html.Append("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}h1{font-size:20px}h2{font-size:16px;margin-top:28px}" +
                    "table{border-collapse:collapse;width:100%;font-size:12px}th,td{border:1px solid #ccc;padding:4px 6px;text-align:left;vertical-align:top}" +
                    "th{background:#eef}td.n{text-align:right}.New{color:#c00;font-weight:600}.Active{color:#c60}.Reviewed{color:#06c}.Approved{color:#080}.Resolved{color:#888}" +
                    "img{max-width:220px}</style></head><body>");
        html.Append("<h1>").Append(E(document.ProjectInformation.Name)).Append(" - clash report</h1>");
        html.Append("<p>").Append(E(DateTime.Now.ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture))).Append("</p>");

        foreach (var test in tests)
        {
            var clashes = test.Results.Where(clash => !openOnly || clash.IsOpen).ToList();
            var counts = string.Join(", ", Enum.GetValues<ClashStatus>().Select(status => (status, count: test.Results.Count(clash => clash.Status == status)))
                .Where(entry => entry.count > 0).Select(entry => $"{entry.count} {entry.status.ToString().ToLowerInvariant()}"));
            html.Append("<h2>").Append(E(test.Name)).Append("</h2><p>").Append(E(test.Kind.ToString())).Append(" clash")
                .Append(test.Kind == ClashKind.Clearance ? $", {test.Tolerance:0} mm clear" : test.Kind == ClashKind.Hard ? $", tolerance {test.Tolerance:0} mm" : string.Empty)
                .Append(" - ").Append(E(string.Join(", ", test.SelectionA.Distinct()))).Append(" against ").Append(E(string.Join(", ", test.SelectionB.Distinct())))
                .Append(". Run ").Append(E(test.LastRun?.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) ?? "never")).Append(": ").Append(E(counts.Length == 0 ? "no clashes" : counts)).Append(".</p>");
            if (clashes.Count == 0) continue;

            html.Append("<table><tr><th>Clash</th><th>Status</th><th>Distance (mm)</th><th>Level</th><th>Grid</th><th>Element 1</th><th>Element 2</th><th>Description</th><th>Found</th><th>Assigned to</th><th>Priority</th><th>Due</th><th>Comments</th>");
            if (snapshot is not null) html.Append("<th>View</th>");
            html.Append("</tr>");
            foreach (var clash in clashes)
            {
                html.Append("<tr><td>").Append(E(clash.Name)).Append("</td><td class=\"").Append(clash.Status).Append("\">").Append(clash.Status)
                    .Append("</td><td class=\"n\">").Append(clash.Distance.ToString("0", CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(E(clash.Level)).Append("</td><td>").Append(E(clash.GridLocation))
                    .Append("</td><td>").Append(E(ClashDetective.NameOf(document, clash.ElementA))).Append("</td><td>").Append(E(ClashDetective.NameOf(document, clash.ElementB)))
                    .Append("</td><td>").Append(E(clash.Description)).Append("</td><td>").Append(E(clash.Found.ToString("d MMM yyyy", CultureInfo.InvariantCulture)))
                    .Append("</td><td>").Append(E(clash.AssignedTo)).Append("</td><td>").Append(clash.Priority)
                    .Append("</td><td>").Append(E(clash.DueDate?.ToString("d MMM yyyy", CultureInfo.InvariantCulture)))
                    .Append("</td><td>").Append(string.Join("<br>", clash.Comments.Select(comment => $"{E(comment.Author)}, {comment.Date:d MMM}: {E(comment.Text)}"))).Append("</td>");
                if (snapshot is not null)
                    html.Append("<td>").Append(snapshot(clash) is { } png ? $"<img src=\"data:image/png;base64,{Convert.ToBase64String(png)}\">" : string.Empty).Append("</td>");
                html.Append("</tr>");
            }

            html.Append("</table>");
        }

        return html.Append("</body></html>").ToString();
    }

    /// <summary>The clashes as a spreadsheet: one sheet, a row each.</summary>
    public static void WriteXlsx(Stream output, BimDocument document, IEnumerable<ClashTest> tests, bool openOnly = false)
    {
        var sheet = new XlsxSheet("Clashes", new[]
        {
            ("Test", HeadingKind.Plain), ("Clash", HeadingKind.Plain), ("Status", HeadingKind.Plain), ("Distance (mm)", HeadingKind.Plain),
            ("Level", HeadingKind.Plain), ("Grid", HeadingKind.Plain), ("Element 1", HeadingKind.Plain), ("Element 1 IFC GUID", HeadingKind.External),
            ("Element 2", HeadingKind.Plain), ("Element 2 IFC GUID", HeadingKind.External), ("Description", HeadingKind.Plain), ("Found", HeadingKind.Plain),
            ("Assigned to", HeadingKind.Plain), ("Priority", HeadingKind.Plain), ("Due", HeadingKind.Plain), ("X (m)", HeadingKind.Plain), ("Y (m)", HeadingKind.Plain), ("Z (m)", HeadingKind.Plain)
        });
        foreach (var (test, clash) in Chosen(tests, openOnly))
            sheet.Add(test.Name, clash.Name, clash.Status.ToString(), Math.Round(clash.Distance, 1), clash.Level, clash.GridLocation,
                ClashDetective.NameOf(document, clash.ElementA), IfcGuid(clash.ElementA), ClashDetective.NameOf(document, clash.ElementB), IfcGuid(clash.ElementB),
                clash.Description, clash.Found.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), clash.AssignedTo, clash.Priority.ToString(),
                clash.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                Math.Round(clash.Point.X / 1000, 3), Math.Round(clash.Point.Y / 1000, 3), Math.Round(clash.Point.Z / 1000, 3));
        XlsxWriter.Write(output, new[] { sheet });
    }
}
