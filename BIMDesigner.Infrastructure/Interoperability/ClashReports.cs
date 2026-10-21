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
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };

        void Xml(string path, Action<XmlWriter> write)
        {
            using var stream = zip.CreateEntry(path).Open();
            using var writer = XmlWriter.Create(stream, settings);
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }

        Xml("bcf.version", writer =>
        {
            writer.WriteStartElement("Version");
            writer.WriteAttributeString("VersionId", "2.1");
            writer.WriteElementString("DetailedVersion", "2.1");
            writer.WriteEndElement();
        });

        Xml("project.bcfp", writer =>
        {
            writer.WriteStartElement("ProjectExtension");
            writer.WriteStartElement("Project");
            writer.WriteAttributeString("ProjectId", Guid.NewGuid().ToString());
            writer.WriteElementString("Name", document.ProjectInformation.Name);
            writer.WriteEndElement();
            writer.WriteElementString("ExtensionSchema", string.Empty);
            writer.WriteEndElement();
        });

        var index = 0;
        foreach (var (test, clash) in Chosen(tests, openOnly))
        {
            var folder = clash.Id.ToString();
            var viewpoint = Guid.NewGuid();
            var image = snapshot?.Invoke(clash);

            Xml($"{folder}/markup.bcf", writer =>
            {
                writer.WriteStartElement("Markup");
                writer.WriteStartElement("Topic");
                writer.WriteAttributeString("Guid", folder);
                writer.WriteAttributeString("TopicType", "Clash");
                writer.WriteAttributeString("TopicStatus", clash.Status.ToString());
                writer.WriteElementString("Title", $"{test.Name}: {clash.Name}");
                writer.WriteElementString("Priority", clash.Priority.ToString());
                writer.WriteElementString("Index", (++index).ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("Labels", test.Kind.ToString());
                writer.WriteElementString("CreationDate", Iso(clash.Found));
                writer.WriteElementString("CreationAuthor", author);
                if (clash.DueDate is { } due) writer.WriteElementString("DueDate", Iso(due.ToDateTime(new TimeOnly(17, 0))));
                if (!string.IsNullOrWhiteSpace(clash.AssignedTo)) writer.WriteElementString("AssignedTo", clash.AssignedTo);
                writer.WriteElementString("Description",
                    $"{clash.Description} {(string.IsNullOrEmpty(clash.Level) ? "" : $"On {clash.Level}")}{(string.IsNullOrEmpty(clash.GridLocation) ? "" : $" at grid {clash.GridLocation}")}.".Trim());
                writer.WriteEndElement();

                foreach (var comment in clash.Comments)
                {
                    writer.WriteStartElement("Comment");
                    writer.WriteAttributeString("Guid", Guid.NewGuid().ToString());
                    writer.WriteElementString("Date", Iso(comment.Date));
                    writer.WriteElementString("Author", comment.Author);
                    writer.WriteElementString("Comment", comment.Text);
                    writer.WriteStartElement("Viewpoint");
                    writer.WriteAttributeString("Guid", viewpoint.ToString());
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteStartElement("Viewpoints");
                writer.WriteAttributeString("Guid", viewpoint.ToString());
                writer.WriteElementString("Viewpoint", "viewpoint.bcfv");
                if (image is not null) writer.WriteElementString("Snapshot", "snapshot.png");
                writer.WriteEndElement();
                writer.WriteEndElement();
            });

            Xml($"{folder}/viewpoint.bcfv", writer => WriteViewpoint(writer, viewpoint, clash));

            if (image is not null)
            {
                using var stream = zip.CreateEntry($"{folder}/snapshot.png").Open();
                stream.Write(image);
            }
        }
    }

    /// <summary>A clash's viewpoint: the two elements selected and coloured, a camera 6 m off looking down at the clash from the south-west.</summary>
    private static void WriteViewpoint(XmlWriter writer, Guid id, Clash clash)
    {
        writer.WriteStartElement("VisualizationInfo");
        writer.WriteAttributeString("Guid", id.ToString());

        writer.WriteStartElement("Components");
        writer.WriteStartElement("Selection");
        foreach (var element in new[] { clash.ElementA, clash.ElementB })
        {
            writer.WriteStartElement("Component");
            writer.WriteAttributeString("IfcGuid", IfcGuid(element));
            writer.WriteElementString("OriginatingSystem", "BIMDesigner");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteStartElement("Visibility");
        writer.WriteAttributeString("DefaultVisibility", "true");
        writer.WriteEndElement();
        writer.WriteStartElement("Coloring");
        foreach (var (element, colour) in new[] { (clash.ElementA, "FFFF0000"), (clash.ElementB, "FF00A000") })
        {
            writer.WriteStartElement("Color");
            writer.WriteAttributeString("Color", colour);
            writer.WriteStartElement("Component");
            writer.WriteAttributeString("IfcGuid", IfcGuid(element));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        // In metres, as BCF has it: 6 m back along a line down from the south-west.
        var target = (X: clash.Point.X / 1000, Y: clash.Point.Y / 1000, Z: clash.Point.Z / 1000);
        var (dx, dy, dz) = (0.577, 0.577, -0.577);
        var eye = (X: target.X - dx * 6, Y: target.Y - dy * 6, Z: target.Z - dz * 6);
        var up = (X: dx * -dz, Y: dy * -dz, Z: dx * dx + dy * dy);
        var length = Math.Sqrt(up.X * up.X + up.Y * up.Y + up.Z * up.Z);

        void Triple(string name, double x, double y, double z)
        {
            writer.WriteStartElement(name);
            writer.WriteElementString("X", Number(x));
            writer.WriteElementString("Y", Number(y));
            writer.WriteElementString("Z", Number(z));
            writer.WriteEndElement();
        }

        writer.WriteStartElement("PerspectiveCamera");
        Triple("CameraViewPoint", eye.X, eye.Y, eye.Z);
        Triple("CameraDirection", dx, dy, dz);
        Triple("CameraUpVector", up.X / length, up.Y / length, up.Z / length);
        writer.WriteElementString("FieldOfView", "60");
        writer.WriteEndElement();
        writer.WriteEndElement();
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
