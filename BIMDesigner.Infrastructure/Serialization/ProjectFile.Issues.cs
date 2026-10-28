using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Infrastructure.Serialization;

/// <summary>The project's issues - BCF topics - kept with it: each with its comments and viewpoints, their pictures and all.</summary>
public static partial class ProjectFile
{
    private static void WriteIssues(BimDocument document, ProjectFileDto dto)
    {
        static double[] Triple(Point3D p) => new[] { p.X, p.Y, p.Z };
        static List<T>? Some<T>(IEnumerable<T> items) => items.ToList() is { Count: > 0 } list ? list : null;
        static string? Text(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

        dto.Issues = document.Issues.Count == 0 ? null : document.Issues.Select(issue => new IssueDto
        {
            Id = issue.Id, Title = issue.Title, Description = Text(issue.Description), Type = issue.Type, Status = issue.Status, Priority = Text(issue.Priority),
            Labels = Some(issue.Labels), Author = issue.Author, Created = issue.Created, ModifiedBy = Text(issue.ModifiedBy), Modified = issue.Modified, Due = issue.Due,
            AssignedTo = Text(issue.AssignedTo), Stage = Text(issue.Stage), Index = issue.Index, ReferenceLinks = Some(issue.ReferenceLinks), RelatedTopics = Some(issue.RelatedTopics),
            Comments = Some(issue.Comments.Select(comment => new IssueCommentDto
            {
                Id = comment.Id, Date = comment.Date, Author = comment.Author, Text = comment.Text, Viewpoint = comment.Viewpoint, Modified = comment.Modified, ModifiedBy = Text(comment.ModifiedBy)
            })),
            Viewpoints = Some(issue.Viewpoints.Select(view => new IssueViewpointDto
            {
                Id = view.Id, Index = view.Index, DefaultVisible = view.DefaultVisible,
                Camera = view.Camera is { } camera
                    ? new IssueCameraDto
                    {
                        Eye = Triple(camera.Eye), Direction = Triple(camera.Direction), Up = Triple(camera.Up), Perspective = camera.Perspective,
                        FieldOfView = camera.FieldOfView, ViewHeight = camera.ViewHeight, AspectRatio = camera.AspectRatio
                    }
                    : null,
                Selected = Some(view.Selected), Exceptions = Some(view.Exceptions),
                Colours = Some(view.Colours.Select(colour => new IssueColourDto { Colour = colour.Colour, Components = colour.Components.ToList() })),
                Clipping = Some(view.Clipping.Select(plane => new[] { plane.Location.X, plane.Location.Y, plane.Location.Z, plane.Direction.X, plane.Direction.Y, plane.Direction.Z })),
                Snapshot = view.Snapshot is { } picture ? Convert.ToBase64String(picture) : null,
                SnapshotType = view.Snapshot is null ? null : view.SnapshotType
            }))
        }).ToList();
    }

    private static void ReadIssues(ProjectFileDto dto, BimDocument document)
    {
        static DateTime Utc(DateTime date) => date.Kind switch
        {
            DateTimeKind.Utc => date,
            DateTimeKind.Local => date.ToUniversalTime(),
            _ => DateTime.SpecifyKind(date, DateTimeKind.Utc)
        };
        static DateTime? Maybe(DateTime? date) => date is { } value ? Utc(value) : null;
        static Point3D? Triple(double[]? values) =>
            values is { Length: 3 } && values.All(double.IsFinite) ? new Point3D(values[0], values[1], values[2]) : null;

        foreach (var saved in dto.Issues ?? new List<IssueDto>())
        {
            var issue = new Issue
            {
                Id = saved.Id == Guid.Empty ? Guid.NewGuid() : saved.Id,
                Title = saved.Title ?? string.Empty, Description = saved.Description ?? string.Empty, Type = saved.Type ?? "Issue", Status = saved.Status ?? "Open",
                Priority = saved.Priority ?? string.Empty, Author = saved.Author ?? string.Empty, Created = Utc(saved.Created), ModifiedBy = saved.ModifiedBy ?? string.Empty,
                Modified = Maybe(saved.Modified), Due = Maybe(saved.Due), AssignedTo = saved.AssignedTo ?? string.Empty, Stage = saved.Stage ?? string.Empty, Index = saved.Index
            };
            issue.Labels.AddRange(saved.Labels ?? new List<string>());
            issue.ReferenceLinks.AddRange(saved.ReferenceLinks ?? new List<string>());
            issue.RelatedTopics.AddRange(saved.RelatedTopics ?? new List<Guid>());
            foreach (var comment in saved.Comments ?? new List<IssueCommentDto>())
                issue.Comments.Add(new IssueComment
                {
                    Id = comment.Id, Date = Utc(comment.Date), Author = comment.Author ?? string.Empty, Text = comment.Text ?? string.Empty,
                    Viewpoint = comment.Viewpoint, Modified = Maybe(comment.Modified), ModifiedBy = comment.ModifiedBy ?? string.Empty
                });

            foreach (var view in saved.Viewpoints ?? new List<IssueViewpointDto>())
            {
                var read = new IssueViewpoint { Id = view.Id, Index = view.Index, DefaultVisible = view.DefaultVisible, SnapshotType = view.SnapshotType ?? "png" };
                if (view.Camera is { } camera && Triple(camera.Eye) is { } eye && Triple(camera.Direction) is { } direction)
                    read.Camera = new IssueCamera(eye, direction, Triple(camera.Up) ?? new Point3D(0, 0, 1), camera.Perspective,
                        double.IsFinite(camera.FieldOfView) ? camera.FieldOfView : 60, double.IsFinite(camera.ViewHeight) ? camera.ViewHeight : 10000,
                        double.IsFinite(camera.AspectRatio) && camera.AspectRatio > 0 ? camera.AspectRatio : 1);
                read.Selected.AddRange(view.Selected ?? new List<string>());
                read.Exceptions.AddRange(view.Exceptions ?? new List<string>());
                read.Colours.AddRange((view.Colours ?? new List<IssueColourDto>()).Where(colour => colour.Colour is not null)
                    .Select(colour => new IssueColour(colour.Colour!, colour.Components ?? new List<string>())));
                read.Clipping.AddRange((view.Clipping ?? new List<double[]>()).Where(plane => plane is { Length: 6 } && plane.All(double.IsFinite))
                    .Select(plane => new IssueClippingPlane(new Point3D(plane[0], plane[1], plane[2]), new Point3D(plane[3], plane[4], plane[5]))));
                if (view.Snapshot is { } picture)
                {
                    try
                    {
                        read.Snapshot = Convert.FromBase64String(picture);
                    }
                    catch (FormatException)
                    {
                        // A picture that does not read is left out; the rest of the viewpoint is kept.
                    }
                }

                issue.Viewpoints.Add(read);
            }

            document.Issues.Add(issue);
        }
    }
}

internal sealed class IssueDto
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public List<string>? Labels { get; set; }
    public string? Author { get; set; }
    public DateTime Created { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? Due { get; set; }
    public string? AssignedTo { get; set; }
    public string? Stage { get; set; }
    public int? Index { get; set; }
    public List<string>? ReferenceLinks { get; set; }
    public List<Guid>? RelatedTopics { get; set; }
    public List<IssueCommentDto>? Comments { get; set; }
    public List<IssueViewpointDto>? Viewpoints { get; set; }
}

internal sealed class IssueCommentDto
{
    public Guid Id { get; set; }
    public DateTime Date { get; set; }
    public string? Author { get; set; }
    public string? Text { get; set; }
    public Guid? Viewpoint { get; set; }
    public DateTime? Modified { get; set; }
    public string? ModifiedBy { get; set; }
}

internal sealed class IssueViewpointDto
{
    public Guid Id { get; set; }
    public int? Index { get; set; }
    public IssueCameraDto? Camera { get; set; }
    public List<string>? Selected { get; set; }
    public bool DefaultVisible { get; set; } = true;
    public List<string>? Exceptions { get; set; }
    public List<IssueColourDto>? Colours { get; set; }

    /// <summary>Each clipping plane as its location and direction, six numbers, mm.</summary>
    public List<double[]>? Clipping { get; set; }

    /// <summary>The picture, base 64.</summary>
    public string? Snapshot { get; set; }
    public string? SnapshotType { get; set; }
}

internal sealed class IssueCameraDto
{
    public double[]? Eye { get; set; }
    public double[]? Direction { get; set; }
    public double[]? Up { get; set; }
    public bool Perspective { get; set; }
    public double FieldOfView { get; set; }
    public double ViewHeight { get; set; }
    public double AspectRatio { get; set; }
}

internal sealed class IssueColourDto
{
    public string? Colour { get; set; }
    public List<string>? Components { get; set; }
}
