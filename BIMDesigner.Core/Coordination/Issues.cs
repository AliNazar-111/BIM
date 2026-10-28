using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Coordination;

/// <summary>Something said about an issue: who, when, what - and the viewpoint it points to, if any.</summary>
public sealed class IssueComment
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>When it was said, UTC.</summary>
    public DateTime Date { get; set; }

    public string Author { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public Guid? Viewpoint { get; set; }

    public DateTime? Modified { get; set; }

    public string ModifiedBy { get; set; } = string.Empty;

    public IssueComment Copy() => (IssueComment)MemberwiseClone();
}

/// <summary>
/// Where a viewpoint looks from, as BCF has it: the eye, the way it looks and which way is up -
/// in the model's millimetres - in perspective by its vertical field of view, or orthographic by how
/// tall a slice of the model it shows; and how wide the picture is for its height.
/// </summary>
public sealed record IssueCamera(Point3D Eye, Point3D Direction, Point3D Up, bool Perspective, double FieldOfView, double ViewHeight, double AspectRatio)
{
    /// <summary>A 3D view's camera, its field of view across, as BCF wants it - up and down - for a picture of the shape given.</summary>
    public static IssueCamera From(Camera3D camera, double aspectRatio)
    {
        var aspect = aspectRatio > 0 && double.IsFinite(aspectRatio) ? aspectRatio : 1;
        var across = camera.FieldOfView * Math.PI / 180;
        var vertical = 2 * Math.Atan(Math.Tan(across / 2) / aspect) * 180 / Math.PI;
        return new IssueCamera(camera.Eye, camera.Look, camera.Up, camera.Perspective, vertical, camera.Width / aspect, aspect);
    }

    /// <summary>The camera for a 3D view, looking at a point this far ahead, its field of view across as the view has it.</summary>
    public Camera3D ToCamera(double distance)
    {
        var look = Camera3D.Unit(Direction, new Point3D(0, 1, 0));
        var reach = Math.Max(distance, 100);
        var target = new Point3D(Eye.X + look.X * reach, Eye.Y + look.Y * reach, Eye.Z + look.Z * reach);
        var aspect = AspectRatio > 0 && double.IsFinite(AspectRatio) ? AspectRatio : 1;
        var across = Perspective
            ? 2 * Math.Atan(Math.Tan(Math.Clamp(FieldOfView, 1, 179) * Math.PI / 360) * aspect) * 180 / Math.PI
            : 2 * Math.Atan(Math.Max(ViewHeight, 1) * aspect / 2 / reach) * 180 / Math.PI;
        return new Camera3D(Eye, target, Perspective) { FieldOfView = Math.Clamp(across, 1, 179) };
    }
}

/// <summary>A colour some of a viewpoint's components are shown in: six or eight hex digits, ARGB.</summary>
public sealed record IssueColour(string Colour, IReadOnlyList<string> Components);

/// <summary>A plane cutting the model away: where, and the way to the side cut away; in millimetres.</summary>
public sealed record IssueClippingPlane(Point3D Location, Point3D Direction);

/// <summary>
/// A view of an issue: where the camera is, the elements it is about by their IFC GlobalIds, what is
/// shown and hidden and in what colour, the planes cutting the model away, and a picture.
/// </summary>
public sealed class IssueViewpoint
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public IssueCamera? Camera { get; set; }

    public List<string> Selected { get; } = new();

    /// <summary>Whether everything is shown but the exceptions, or nothing but them.</summary>
    public bool DefaultVisible { get; set; } = true;

    public List<string> Exceptions { get; } = new();

    public List<IssueColour> Colours { get; } = new();

    public List<IssueClippingPlane> Clipping { get; } = new();

    /// <summary>A picture of the view: PNG or JPEG.</summary>
    public byte[]? Snapshot { get; set; }

    public string SnapshotType { get; set; } = "png";

    public int? Index { get; set; }

    public IssueViewpoint Copy()
    {
        var copy = new IssueViewpoint { Id = Id, Camera = Camera, DefaultVisible = DefaultVisible, Snapshot = Snapshot, SnapshotType = SnapshotType, Index = Index };
        copy.Selected.AddRange(Selected);
        copy.Exceptions.AddRange(Exceptions);
        copy.Colours.AddRange(Colours);
        copy.Clipping.AddRange(Clipping);
        return copy;
    }

    /// <summary>The box six square-on planes cut the model to, if that is what they are: a section box.</summary>
    public Box3D? Box()
    {
        if (Clipping.Count != 6) return null;
        double? Bound(Func<Point3D, double> axis, double sign)
        {
            var plane = Clipping.FirstOrDefault(plane => Math.Abs(axis(Camera3D.Unit(plane.Direction, new Point3D(0, 0, 0))) - sign) < 1e-6);
            return plane is null ? null : axis(plane.Location);
        }

        if (Bound(p => p.X, -1) is not { } x0 || Bound(p => p.X, 1) is not { } x1 || Bound(p => p.Y, -1) is not { } y0 ||
            Bound(p => p.Y, 1) is not { } y1 || Bound(p => p.Z, -1) is not { } z0 || Bound(p => p.Z, 1) is not { } z1) return null;
        return x1 > x0 && y1 > y0 && z1 > z0 ? new Box3D(new Point3D(x0, y0, z0), new Point3D(x1, y1, z1)) : null;
    }

    /// <summary>A section box as the six planes cutting the model to it, each facing out of it.</summary>
    public static IEnumerable<IssueClippingPlane> Planes(Box3D box)
    {
        var (min, max) = (box.Min, box.Max);
        var (cx, cy, cz) = ((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        yield return new IssueClippingPlane(new Point3D(min.X, cy, cz), new Point3D(-1, 0, 0));
        yield return new IssueClippingPlane(new Point3D(max.X, cy, cz), new Point3D(1, 0, 0));
        yield return new IssueClippingPlane(new Point3D(cx, min.Y, cz), new Point3D(0, -1, 0));
        yield return new IssueClippingPlane(new Point3D(cx, max.Y, cz), new Point3D(0, 1, 0));
        yield return new IssueClippingPlane(new Point3D(cx, cy, min.Z), new Point3D(0, 0, -1));
        yield return new IssueClippingPlane(new Point3D(cx, cy, max.Z), new Point3D(0, 0, 1));
    }
}

/// <summary>
/// An issue - a BCF topic: something to be put right or answered about the model, its title, type,
/// status and priority, who raised it and when, who has it and by when, its labels and stage, what is
/// said about it, and the views of it - kept with the project and handed to and from other programs
/// as BCF.
/// </summary>
public sealed class Issue
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Type { get; set; } = "Issue";

    public string Status { get; set; } = "Open";

    public string Priority { get; set; } = "Normal";

    public List<string> Labels { get; private set; } = new();

    public string Author { get; set; } = string.Empty;

    /// <summary>When it was raised, UTC.</summary>
    public DateTime Created { get; set; }

    public string ModifiedBy { get; set; } = string.Empty;

    public DateTime? Modified { get; set; }

    public DateTime? Due { get; set; }

    public string AssignedTo { get; set; } = string.Empty;

    public string Stage { get; set; } = string.Empty;

    public int? Index { get; set; }

    public List<string> ReferenceLinks { get; private set; } = new();

    public List<Guid> RelatedTopics { get; private set; } = new();

    public List<IssueComment> Comments { get; private set; } = new();

    public List<IssueViewpoint> Viewpoints { get; private set; } = new();

    /// <summary>Whether it still wants doing: not closed, resolved, done, approved or rejected.</summary>
    public bool IsOpen => !Issues.ClosedStatuses.Contains(Status);

    public Issue Copy()
    {
        var copy = (Issue)MemberwiseClone();
        copy.Labels = new List<string>(Labels);
        copy.ReferenceLinks = new List<string>(ReferenceLinks);
        copy.RelatedTopics = new List<Guid>(RelatedTopics);
        copy.Comments = Comments.Select(comment => comment.Copy()).ToList();
        copy.Viewpoints = Viewpoints.Select(view => view.Copy()).ToList();
        return copy;
    }
}

/// <summary>The project's issues changed - one edited, added, taken out, or many brought in from a BCF file - as one step to undo.</summary>
public sealed class SetIssuesCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly List<Issue> _before;
    private readonly List<Issue> _after;

    public SetIssuesCommand(BimDocument document, IEnumerable<Issue> after, string name)
    {
        (_document, Name) = (document, name);
        _before = document.Issues.ToList();
        _after = after.ToList();
    }

    public string Name { get; }

    public void Redo()
    {
        _document.Issues.Clear();
        _document.Issues.AddRange(_after);
    }

    public void Undo()
    {
        _document.Issues.Clear();
        _document.Issues.AddRange(_before);
    }
}

public static class Issues
{
    /// <summary>The types, statuses and priorities offered - other programs' own are kept as they come.</summary>
    public static readonly IReadOnlyList<string> Types = new[] { "Issue", "Clash", "Request", "Question", "Remark", "Fault" };

    public static readonly IReadOnlyList<string> Statuses = new[] { "Open", "In Progress", "Resolved", "Closed" };

    public static readonly IReadOnlyList<string> Priorities = new[] { "Critical", "High", "Normal", "Low" };

    public static readonly IReadOnlySet<string> ClosedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Closed", "Resolved", "Done", "Approved", "Rejected" };

    /// <summary>The elements of the model a viewpoint selects, found by their IFC GlobalIds.</summary>
    public static IReadOnlyList<Element> Selected(BimDocument document, IssueViewpoint viewpoint) =>
        viewpoint.Selected.Select(guid => IfcGuids.TryExpand(guid, out var id) ? document.FindElement(id) : null).OfType<Element>().Distinct().ToList();

    /// <summary>A viewpoint of the 3D view as it is: its camera, the elements selected, its section box, and a picture of it.</summary>
    public static IssueViewpoint ViewOf(Camera3D camera, double aspectRatio, IEnumerable<Guid> selected, Box3D? box, byte[]? snapshot)
    {
        var view = new IssueViewpoint { Camera = IssueCamera.From(camera, aspectRatio), Snapshot = snapshot };
        view.Selected.AddRange(selected.Select(IfcGuids.Compress));
        if (box is { } cut) view.Clipping.AddRange(IssueViewpoint.Planes(cut));
        return view;
    }

    /// <summary>
    /// A clash as an issue of its own GUID: its test and name for its title, what it is between and
    /// where, its status, priority, who has it and by when, what has been said; and a view of it -
    /// 6 m off from the south-west looking down at it, its two elements selected, one red, one green.
    /// </summary>
    public static Issue FromClash(ClashTest test, Clash clash, string author, byte[]? snapshot = null, int? index = null)
    {
        var issue = new Issue
        {
            Id = clash.Id,
            Title = $"{test.Name}: {clash.Name}",
            Type = "Clash",
            Status = clash.Status.ToString(),
            Priority = clash.Priority.ToString(),
            Index = index,
            Author = author,
            Created = clash.Found.ToUniversalTime(),
            Due = clash.DueDate?.ToDateTime(new TimeOnly(17, 0)).ToUniversalTime(),
            AssignedTo = clash.AssignedTo,
            Description = $"{clash.Description} {(string.IsNullOrEmpty(clash.Level) ? "" : $"On {clash.Level}")}{(string.IsNullOrEmpty(clash.GridLocation) ? "" : $" at grid {clash.GridLocation}")}.".Trim()
        };
        issue.Labels.Add(test.Kind.ToString());

        var direction = new Point3D(Math.Sqrt(1.0 / 3), Math.Sqrt(1.0 / 3), -Math.Sqrt(1.0 / 3));
        var eye = new Point3D(clash.Point.X - direction.X * 6000, clash.Point.Y - direction.Y * 6000, clash.Point.Z - direction.Z * 6000);
        var camera = new Camera3D(eye, clash.Point) { FieldOfView = 60 };
        var view = new IssueViewpoint { Camera = IssueCamera.From(camera, 1), Snapshot = snapshot };
        view.Selected.AddRange(new[] { clash.ElementA, clash.ElementB }.Select(IfcGuids.Compress));
        view.Colours.Add(new IssueColour("FFFF0000", new[] { IfcGuids.Compress(clash.ElementA) }));
        view.Colours.Add(new IssueColour("FF00A000", new[] { IfcGuids.Compress(clash.ElementB) }));
        issue.Viewpoints.Add(view);
        issue.Comments.AddRange(clash.Comments.Select(comment => new IssueComment { Date = comment.Date.ToUniversalTime(), Author = comment.Author, Text = comment.Text, Viewpoint = view.Id }));
        return issue;
    }

    /// <summary>
    /// Issues brought in from another program merged into the project's: one of a GUID it has already
    /// replaced by the one come in, its comments and viewpoints added to; the rest added after.
    /// </summary>
    public static List<Issue> Merge(IReadOnlyList<Issue> own, IReadOnlyList<Issue> incoming)
    {
        var merged = own.ToList();
        foreach (var issue in incoming)
        {
            var at = merged.FindIndex(existing => existing.Id == issue.Id);
            if (at < 0)
            {
                merged.Add(issue);
                continue;
            }

            var combined = issue.Copy();
            combined.Comments.AddRange(merged[at].Comments.Where(comment => combined.Comments.All(other => other.Id != comment.Id)).Select(comment => comment.Copy()));
            combined.Comments.Sort((a, b) => a.Date.CompareTo(b.Date));
            combined.Viewpoints.InsertRange(0, merged[at].Viewpoints.Where(view => combined.Viewpoints.All(other => other.Id != view.Id)).Select(view => view.Copy()));
            merged[at] = combined;
        }

        return merged;
    }
}
