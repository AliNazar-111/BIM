using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>What was read from a BCF file: its issues, the version it was written in, and what could not be taken.</summary>
public sealed record BcfRead(IReadOnlyList<Issue> Issues, string Version, IReadOnlyList<string> Notes);

/// <summary>
/// Issues as BCF - buildingSMART's BIM Collaboration Format, 2.1 or 3.0 - and back: a zip of one
/// folder for each topic, named by its GUID, holding its markup (title, type, status, priority, who
/// raised it and has it, by when, its labels, comments and the list of its viewpoints) and each
/// viewpoint (a camera, the components it is about by their IFC GlobalIds, what is shown, hidden and
/// coloured, the planes clipping the model) with its snapshot. Lengths in metres, in the coordinates
/// the IFC is set out in: the model's own, moved by the project's survey offset where it was brought
/// in from a national grid. BCF 2.1's field of view is across the picture and 3.0's up it, with the
/// picture's aspect ratio; a file without one is taken as square, as 3.0 says.
/// </summary>
public static class BcfFile
{
    private const string Originating = "BIMDesigner";

    private static readonly XmlWriterSettings Settings = new() { Indent = true, Encoding = new UTF8Encoding(false) };

    private static string Iso(DateTime date) => (date.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime())
        .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>Writes issues as a BCF file of the version given, "2.1" or "3.0".</summary>
    public static void Write(Stream output, BimDocument document, IEnumerable<Issue> issues, string version = "2.1")
    {
        var three = version.StartsWith('3');
        var list = issues.ToList();
        var offset = document.ProjectInformation.SurveyOffset;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        void Xml(string path, Action<XmlWriter> write)
        {
            using var stream = zip.CreateEntry(path).Open();
            using var writer = XmlWriter.Create(stream, Settings);
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }

        Xml("bcf.version", writer =>
        {
            writer.WriteStartElement("Version");
            writer.WriteAttributeString("VersionId", three ? "3.0" : "2.1");
            if (!three) writer.WriteElementString("DetailedVersion", "2.1");
            writer.WriteEndElement();
        });

        var project = Guid.NewGuid().ToString();
        var name = string.IsNullOrWhiteSpace(document.ProjectInformation.Name) ? "Project" : document.ProjectInformation.Name;
        Xml("project.bcfp", writer =>
        {
            writer.WriteStartElement(three ? "ProjectInfo" : "ProjectExtension");
            writer.WriteStartElement("Project");
            writer.WriteAttributeString("ProjectId", project);
            writer.WriteElementString("Name", name);
            writer.WriteEndElement();
            if (!three) writer.WriteElementString("ExtensionSchema", string.Empty);
            writer.WriteEndElement();
        });

        // 3.0: the lists the topics' types, statuses, priorities, labels, people and stages come from.
        if (three)
            Xml("extensions.xml", writer =>
            {
                writer.WriteStartElement("Extensions");
                void Values(string group, string item, IEnumerable<string> values)
                {
                    var distinct = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
                    if (distinct.Count == 0) return;
                    writer.WriteStartElement(group);
                    foreach (var value in distinct) writer.WriteElementString(item, value);
                    writer.WriteEndElement();
                }

                Values("TopicTypes", "TopicType", Issues.Types.Concat(list.Select(issue => issue.Type)));
                Values("TopicStatuses", "TopicStatus", Issues.Statuses.Concat(list.Select(issue => issue.Status)));
                Values("Priorities", "Priority", Issues.Priorities.Concat(list.Select(issue => issue.Priority)));
                Values("TopicLabels", "TopicLabel", list.SelectMany(issue => issue.Labels));
                Values("Users", "User", list.SelectMany(issue => new[] { Named(issue.Author), issue.AssignedTo, issue.ModifiedBy }.Concat(issue.Comments.Select(comment => Named(comment.Author)))));
                Values("Stages", "Stage", list.Select(issue => issue.Stage));
                writer.WriteEndElement();
            });

        foreach (var issue in list)
        {
            var folder = issue.Id.ToString();
            var files = issue.Viewpoints.Select((view, i) => (
                View: view,
                Visinfo: view.Camera is null && three ? null : i == 0 ? "viewpoint.bcfv" : $"viewpoint-{view.Id}.bcfv",
                Snapshot: view.Snapshot is null ? null : i == 0 ? $"snapshot.{Extension(view)}" : $"snapshot-{view.Id}.{Extension(view)}")).ToList();

            Xml($"{folder}/markup.bcf", writer => WriteMarkup(writer, issue, files.Select(file => (file.View, file.Visinfo, file.Snapshot)).ToList(), three));
            foreach (var (view, visinfo, snapshot) in files)
            {
                if (visinfo is not null) Xml($"{folder}/{visinfo}", writer => WriteViewpoint(writer, view, offset, three));
                if (snapshot is not null)
                {
                    using var stream = zip.CreateEntry($"{folder}/{snapshot}").Open();
                    stream.Write(view.Snapshot);
                }
            }
        }
    }

    private static string Extension(IssueViewpoint view) => view.SnapshotType.Equals("jpg", StringComparison.OrdinalIgnoreCase) || view.SnapshotType.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";

    private static string Named(string text) => string.IsNullOrWhiteSpace(text) ? "Unknown" : text.Trim();

    private static void WriteMarkup(XmlWriter writer, Issue issue, IReadOnlyList<(IssueViewpoint View, string? Visinfo, string? Snapshot)> views, bool three)
    {
        writer.WriteStartElement("Markup");
        writer.WriteStartElement("Topic");
        writer.WriteAttributeString("Guid", issue.Id.ToString());
        writer.WriteAttributeString("TopicType", Named(issue.Type));
        writer.WriteAttributeString("TopicStatus", Named(issue.Status));

        void Optional(string element, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) writer.WriteElementString(element, value.Trim());
        }

        void Listed(string group, string item, IEnumerable<string> values)
        {
            var items = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (items.Count == 0) return;
            if (three) writer.WriteStartElement(group);
            foreach (var value in items) writer.WriteElementString(three ? item : group, value.Trim());
            if (three) writer.WriteEndElement();
        }

        // In the order the schema has them.
        if (three) Listed("ReferenceLinks", "ReferenceLink", issue.ReferenceLinks);
        else foreach (var link in issue.ReferenceLinks.Where(link => !string.IsNullOrWhiteSpace(link))) writer.WriteElementString("ReferenceLink", link);
        writer.WriteElementString("Title", string.IsNullOrWhiteSpace(issue.Title) ? "Untitled" : issue.Title.Trim());
        Optional("Priority", issue.Priority);
        if (issue.Index is { } index) writer.WriteElementString("Index", index.ToString(CultureInfo.InvariantCulture));
        Listed("Labels", "Label", issue.Labels);
        writer.WriteElementString("CreationDate", Iso(issue.Created));
        writer.WriteElementString("CreationAuthor", Named(issue.Author));
        if (issue.Modified is { } modified) writer.WriteElementString("ModifiedDate", Iso(modified));
        Optional("ModifiedAuthor", issue.ModifiedBy);
        if (issue.Due is { } due) writer.WriteElementString("DueDate", Iso(due));
        Optional("AssignedTo", issue.AssignedTo);
        Optional("Stage", issue.Stage);
        Optional("Description", issue.Description);
        if (issue.RelatedTopics.Count > 0)
        {
            if (three) writer.WriteStartElement("RelatedTopics");
            foreach (var related in issue.RelatedTopics)
            {
                writer.WriteStartElement("RelatedTopic");
                writer.WriteAttributeString("Guid", related.ToString());
                writer.WriteEndElement();
            }

            if (three) writer.WriteEndElement();
        }

        if (three)
        {
            WriteComments(writer, issue, three);
            WriteViewpointList(writer, views, three);
            writer.WriteEndElement();
        }
        else
        {
            writer.WriteEndElement();
            WriteComments(writer, issue, three);
            WriteViewpointList(writer, views, three);
        }

        writer.WriteEndElement();
    }

    private static void WriteComments(XmlWriter writer, Issue issue, bool three)
    {
        var comments = issue.Comments.Where(comment => !string.IsNullOrWhiteSpace(comment.Text) || comment.Viewpoint is not null).ToList();
        if (comments.Count == 0) return;
        if (three) writer.WriteStartElement("Comments");
        foreach (var comment in comments)
        {
            writer.WriteStartElement("Comment");
            writer.WriteAttributeString("Guid", comment.Id.ToString());
            writer.WriteElementString("Date", Iso(comment.Date));
            writer.WriteElementString("Author", Named(comment.Author));
            if (!three || !string.IsNullOrWhiteSpace(comment.Text)) writer.WriteElementString("Comment", comment.Text);
            if (comment.Viewpoint is { } viewpoint && issue.Viewpoints.Any(view => view.Id == viewpoint))
            {
                writer.WriteStartElement("Viewpoint");
                writer.WriteAttributeString("Guid", viewpoint.ToString());
                writer.WriteEndElement();
            }

            if (comment.Modified is { } modified) writer.WriteElementString("ModifiedDate", Iso(modified));
            if (!string.IsNullOrWhiteSpace(comment.ModifiedBy)) writer.WriteElementString("ModifiedAuthor", comment.ModifiedBy);
            writer.WriteEndElement();
        }

        if (three) writer.WriteEndElement();
    }

    private static void WriteViewpointList(XmlWriter writer, IReadOnlyList<(IssueViewpoint View, string? Visinfo, string? Snapshot)> views, bool three)
    {
        if (views.Count == 0) return;
        if (three) writer.WriteStartElement("Viewpoints");
        foreach (var (view, visinfo, snapshot) in views)
        {
            writer.WriteStartElement(three ? "ViewPoint" : "Viewpoints");
            writer.WriteAttributeString("Guid", view.Id.ToString());
            if (visinfo is not null) writer.WriteElementString("Viewpoint", visinfo);
            if (snapshot is not null) writer.WriteElementString("Snapshot", snapshot);
            if (view.Index is { } index) writer.WriteElementString("Index", index.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        if (three) writer.WriteEndElement();
    }

    private static void WriteViewpoint(XmlWriter writer, IssueViewpoint view, Vector2D offset, bool three)
    {
        writer.WriteStartElement("VisualizationInfo");
        writer.WriteAttributeString("Guid", view.Id.ToString());

        void Component(string guid)
        {
            writer.WriteStartElement("Component");
            writer.WriteAttributeString("IfcGuid", guid);
            writer.WriteElementString("OriginatingSystem", Originating);
            if (IfcGuids.TryExpand(guid, out var id)) writer.WriteElementString("AuthoringToolId", id.ToString());
            writer.WriteEndElement();
        }

        void Hints()
        {
            writer.WriteStartElement("ViewSetupHints");
            writer.WriteAttributeString("SpacesVisible", "false");
            writer.WriteAttributeString("SpaceBoundariesVisible", "false");
            writer.WriteAttributeString("OpeningsVisible", "false");
            writer.WriteEndElement();
        }

        writer.WriteStartElement("Components");
        if (!three) Hints();
        if (view.Selected.Count > 0)
        {
            writer.WriteStartElement("Selection");
            foreach (var guid in view.Selected) Component(guid);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("Visibility");
        writer.WriteAttributeString("DefaultVisibility", view.DefaultVisible ? "true" : "false");
        if (three) Hints();
        if (view.Exceptions.Count > 0)
        {
            writer.WriteStartElement("Exceptions");
            foreach (var guid in view.Exceptions) Component(guid);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        var colours = view.Colours.Where(colour => colour.Components.Count > 0).ToList();
        if (colours.Count > 0)
        {
            writer.WriteStartElement("Coloring");
            foreach (var colour in colours)
            {
                writer.WriteStartElement("Color");
                writer.WriteAttributeString("Color", colour.Colour.TrimStart('#').ToUpperInvariant());
                if (three) writer.WriteStartElement("Components");
                foreach (var guid in colour.Components) Component(guid);
                if (three) writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        void Triple(string element, double x, double y, double z)
        {
            writer.WriteStartElement(element);
            writer.WriteElementString("X", Number(x));
            writer.WriteElementString("Y", Number(y));
            writer.WriteElementString("Z", Number(z));
            writer.WriteEndElement();
        }

        void At(string element, Point3D point) => Triple(element, (point.X + offset.X) / 1000, (point.Y + offset.Y) / 1000, point.Z / 1000);

        if (view.Camera is { } camera)
        {
            writer.WriteStartElement(camera.Perspective ? "PerspectiveCamera" : "OrthogonalCamera");
            At("CameraViewPoint", camera.Eye);
            var look = Unit(camera.Direction);
            var up = Unit(camera.Up);
            Triple("CameraDirection", look.X, look.Y, look.Z);
            Triple("CameraUpVector", up.X, up.Y, up.Z);
            var aspect = camera.AspectRatio > 0 ? camera.AspectRatio : 1;
            if (camera.Perspective)
            {
                // 2.1: across the picture, within the 45 to 60 degrees its schema allows; 3.0: up it, with the aspect ratio.
                var across = 2 * Math.Atan(Math.Tan(camera.FieldOfView * Math.PI / 360) * aspect) * 180 / Math.PI;
                writer.WriteElementString("FieldOfView", Number(three ? camera.FieldOfView : Math.Clamp(across, 45, 60)));
            }
            else writer.WriteElementString("ViewToWorldScale", Number((three ? camera.ViewHeight : camera.ViewHeight * Math.Max(aspect, 1)) / 1000));

            if (three) writer.WriteElementString("AspectRatio", Number(aspect));
            writer.WriteEndElement();
        }

        if (view.Clipping.Count > 0)
        {
            writer.WriteStartElement("ClippingPlanes");
            foreach (var plane in view.Clipping)
            {
                writer.WriteStartElement("ClippingPlane");
                At("Location", plane.Location);
                var direction = Unit(plane.Direction);
                Triple("Direction", direction.X, direction.Y, direction.Z);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static Point3D Unit(Point3D v)
    {
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return length < 1e-12 ? new Point3D(0, 0, 1) : new Point3D(v.X / length, v.Y / length, v.Z / length);
    }

    // ---- reading ----------------------------------------------------------------------------------

    /// <summary>
    /// Reads a BCF file - 2.0, 2.1 or 3.0 - into issues: each topic with its comments and viewpoints,
    /// their cameras brought into the model's millimetres by the project's survey offset.
    /// </summary>
    public static BcfRead Read(Stream input, Vector2D offset = default)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var notes = new List<string>();
        var entries = zip.Entries.Where(entry => entry.Length > 0 || !entry.FullName.EndsWith('/'))
            .GroupBy(entry => entry.FullName.Replace('\\', '/').TrimStart('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var version = "2.1";
        if (entries.TryGetValue("bcf.version", out var versionEntry) && Load(versionEntry) is { } versionXml)
            version = versionXml.Root?.Attribute("VersionId")?.Value is { Length: > 0 } id ? id : DetailedVersion(versionXml) ?? version;
        else notes.Add("It has no bcf.version: read as BCF 2.1.");
        var three = version.StartsWith('3');

        var issues = new List<Issue>();
        foreach (var markupPath in entries.Keys.Where(path => path.EndsWith("/markup.bcf", StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.Ordinal))
        {
            var folder = markupPath[..^"markup.bcf".Length];
            try
            {
                if (Load(entries[markupPath]) is not { Root: { } markup }) throw new InvalidDataException("its markup could not be read");
                issues.Add(ReadTopic(markup, folder, entries, offset, three, notes));
            }
            catch (Exception exception) when (exception is XmlException or InvalidDataException or FormatException or IOException)
            {
                notes.Add($"The topic in {folder.TrimEnd('/')} was left out: {exception.Message}");
            }
        }

        return new BcfRead(issues, version, notes);
    }

    private static string? DetailedVersion(XDocument xml) => Child(xml.Root, "DetailedVersion")?.Value;

    private static XDocument? Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? parent, string name) => parent?.Elements().Where(element => element.Name.LocalName == name) ?? Enumerable.Empty<XElement>();

    private static string Text(XElement? parent, string name) => Child(parent, name)?.Value.Trim() ?? string.Empty;

    private static DateTime? Date(XElement? parent, string name) =>
        Child(parent, name)?.Value is { Length: > 0 } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private static Guid GuidOf(XElement? element) =>
        Guid.TryParse(element?.Attribute("Guid")?.Value, out var id) ? id : Guid.NewGuid();

    private static Issue ReadTopic(XElement markup, string folder, IReadOnlyDictionary<string, ZipArchiveEntry> entries, Vector2D offset, bool three, List<string> notes)
    {
        var topic = Child(markup, "Topic") ?? throw new InvalidDataException("it has no topic");
        var issue = new Issue
        {
            Id = GuidOf(topic),
            Title = Text(topic, "Title"),
            Type = topic.Attribute("TopicType")?.Value.Trim() is { Length: > 0 } type ? type : "Issue",
            Status = topic.Attribute("TopicStatus")?.Value.Trim() is { Length: > 0 } status ? status : "Open",
            Priority = Text(topic, "Priority"),
            Index = int.TryParse(Text(topic, "Index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : null,
            Created = Date(topic, "CreationDate") ?? DateTime.UtcNow,
            Author = Text(topic, "CreationAuthor"),
            Modified = Date(topic, "ModifiedDate"),
            ModifiedBy = Text(topic, "ModifiedAuthor"),
            Due = Date(topic, "DueDate"),
            AssignedTo = Text(topic, "AssignedTo"),
            Stage = Text(topic, "Stage"),
            Description = Text(topic, "Description")
        };

        // 2.x lists them straight in the topic, 3.0 in a group of their own: whichever is there.
        IEnumerable<XElement> Grouped(XElement parent, string group, string item) =>
            Children(parent, group).SelectMany(element => element.HasElements ? Children(element, item) : new[] { element });
        issue.Labels.AddRange(Grouped(topic, "Labels", "Label").Select(label => label.Value.Trim()).Where(label => label.Length > 0));
        issue.ReferenceLinks.AddRange(Children(topic, "ReferenceLink").Concat(Children(Child(topic, "ReferenceLinks"), "ReferenceLink")).Select(link => link.Value.Trim()).Where(link => link.Length > 0));
        issue.RelatedTopics.AddRange(Children(topic, "RelatedTopic").Concat(Children(Child(topic, "RelatedTopics"), "RelatedTopic"))
            .Select(related => Guid.TryParse(related.Attribute("Guid")?.Value, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty));

        foreach (var comment in Children(markup, "Comment").Concat(Children(Child(topic, "Comments"), "Comment")))
            issue.Comments.Add(new IssueComment
            {
                Id = GuidOf(comment),
                Date = Date(comment, "Date") ?? issue.Created,
                Author = Text(comment, "Author"),
                Text = Text(comment, "Comment"),
                Viewpoint = Guid.TryParse(Child(comment, "Viewpoint")?.Attribute("Guid")?.Value, out var view) ? view : null,
                Modified = Date(comment, "ModifiedDate"),
                ModifiedBy = Text(comment, "ModifiedAuthor")
            });

        // The viewpoints listed - in 2.x beside the topic, in 3.0 inside it - or, in an old file listing none, the one it has.
        var listed = Children(markup, "Viewpoints").Concat(Children(Child(topic, "Viewpoints"), "ViewPoint")).ToList();
        if (listed.Count == 0 && (entries.ContainsKey(folder + "viewpoint.bcfv") || entries.ContainsKey(folder + "snapshot.png")))
            listed.Add(new XElement("Viewpoints", new XAttribute("Guid", Guid.NewGuid()), new XElement("Viewpoint", "viewpoint.bcfv"), new XElement("Snapshot", "snapshot.png")));
        foreach (var entry in listed)
        {
            var view = new IssueViewpoint
            {
                Id = GuidOf(entry),
                Index = int.TryParse(Text(entry, "Index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ? order : null
            };
            if (Text(entry, "Viewpoint") is { Length: > 0 } file && entries.TryGetValue(folder + file, out var visinfo) && Load(visinfo) is { Root: { } info })
                ReadViewpoint(info, view, offset, three);
            if (Text(entry, "Snapshot") is { Length: > 0 } picture && entries.TryGetValue(folder + picture, out var snapshot))
            {
                using var stream = snapshot.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                view.Snapshot = copy.ToArray();
                view.SnapshotType = picture.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || picture.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
            }

            issue.Viewpoints.Add(view);
        }

        if (string.IsNullOrWhiteSpace(issue.Title))
        {
            issue.Title = "Untitled";
            notes.Add($"A topic with no title, {issue.Id}, was called Untitled.");
        }

        return issue;
    }

    private static void ReadViewpoint(XElement info, IssueViewpoint view, Vector2D offset, bool three)
    {
        static IEnumerable<string> Guids(IEnumerable<XElement> components) =>
            components.Select(component => component.Attribute("IfcGuid")?.Value.Trim()).OfType<string>().Where(guid => guid.Length == 22);

        var components = Child(info, "Components");
        view.Selected.AddRange(Guids(Children(Child(components, "Selection"), "Component")));
        var visibility = Child(components, "Visibility");
        view.DefaultVisible = visibility?.Attribute("DefaultVisibility")?.Value is { } shown ? shown.Equals("true", StringComparison.OrdinalIgnoreCase) || shown == "1" : !three;
        view.Exceptions.AddRange(Guids(Children(Child(visibility, "Exceptions"), "Component")));
        foreach (var colour in Children(Child(components, "Coloring"), "Color"))
        {
            var guids = Guids(Children(colour, "Component").Concat(Children(Child(colour, "Components"), "Component"))).ToList();
            if (guids.Count > 0 && colour.Attribute("Color")?.Value is { Length: 6 or 8 } argb) view.Colours.Add(new IssueColour(argb.ToUpperInvariant(), guids));
        }

        double Value(XElement? parent, string name) =>
            double.TryParse(Text(parent, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : double.NaN;
        Point3D Triple(XElement? parent, string name)
        {
            var element = Child(parent, name);
            return new Point3D(Value(element, "X"), Value(element, "Y"), Value(element, "Z"));
        }

        Point3D Model(Point3D metres) => new(metres.X * 1000 - offset.X, metres.Y * 1000 - offset.Y, metres.Z * 1000);
        static bool Finite(Point3D p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

        var perspective = Child(info, "PerspectiveCamera");
        var camera = perspective ?? Child(info, "OrthogonalCamera");
        if (camera is not null)
        {
            var (eye, direction, up) = (Triple(camera, "CameraViewPoint"), Triple(camera, "CameraDirection"), Triple(camera, "CameraUpVector"));
            var aspect = Value(camera, "AspectRatio") is var ratio && ratio > 0 ? ratio : 1;
            var angle = Value(camera, "FieldOfView");
            var scale = Value(camera, "ViewToWorldScale");
            if (Finite(eye) && Finite(direction) && (direction.X != 0 || direction.Y != 0 || direction.Z != 0))
                view.Camera = new IssueCamera(Model(eye), direction, Finite(up) ? up : new Point3D(0, 0, 1), perspective is not null,
                    double.IsFinite(angle) && angle is > 0 and < 180 ? angle : 60, double.IsFinite(scale) && scale > 0 ? scale * 1000 : 10000, aspect);
        }

        foreach (var plane in Children(Child(info, "ClippingPlanes"), "ClippingPlane"))
        {
            var (location, direction) = (Triple(plane, "Location"), Triple(plane, "Direction"));
            if (Finite(location) && Finite(direction)) view.Clipping.Add(new IssueClippingPlane(Model(location), direction));
        }
    }
}
