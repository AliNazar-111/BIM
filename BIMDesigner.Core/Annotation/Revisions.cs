using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Annotation;

/// <summary>
/// How a revision is numbered among those numbered the same way. P01, P02 for preliminary issues
/// and C01, C02 for contract issues, as BS EN ISO 19650's UK annex has it; or 1, 2, 3; or A, B, C -
/// without I and O, which read as one and nought.
/// </summary>
public enum RevisionNumbering
{
    Preliminary,
    Contractual,
    Numeric,
    Alphabetic
}

/// <summary>What of a revision is drawn: its clouds and their tags, the tags alone, or neither.</summary>
public enum RevisionVisibility
{
    CloudsAndTags,
    TagsOnly,
    Hidden
}

/// <summary>
/// One revision of the drawings (specification section 6.5): a change, when it was made and
/// why, and who it went to. Numbered by its place in the project's list among those numbered
/// the same way. Once issued it is locked - what was sent out cannot be changed after it was.
/// </summary>
public sealed class Revision
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public RevisionNumbering Numbering { get; set; } = RevisionNumbering.Preliminary;

    public DateTime? Date { get; set; }

    public string Description { get; set; } = string.Empty;

    public string IssuedTo { get; set; } = string.Empty;

    public string IssuedBy { get; set; } = string.Empty;

    /// <summary>Sent out: its details, and its clouds, can no longer be changed.</summary>
    public bool Issued { get; set; }

    public RevisionVisibility Visibility { get; set; } = RevisionVisibility.CloudsAndTags;

    /// <summary>The same revision, to put back as it was.</summary>
    public Revision Copy() => new()
    {
        Id = Id, Numbering = Numbering, Date = Date, Description = Description, IssuedTo = IssuedTo, IssuedBy = IssuedBy,
        Issued = Issued, Visibility = Visibility
    };

    public void CopyFrom(Revision other) =>
        (Numbering, Date, Description, IssuedTo, IssuedBy, Issued, Visibility) =
        (other.Numbering, other.Date, other.Description, other.IssuedTo, other.IssuedBy, other.Issued, other.Visibility);
}

/// <summary>The project's revisions: their numbers, and which are on each sheet.</summary>
public static class Revisions
{
    /// <summary>The way a revision's date is written: 3 Oct 2026.</summary>
    public static string FormatDate(DateTime? date) => date?.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>A date read the ways it is written: 3 Oct 2026, 2026-10-03, 03/10/2026; empty for none.</summary>
    public static bool TryParseDate(string? text, out DateTime? date)
    {
        date = null;
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0) return true;
        var formats = new[] { "d MMM yyyy", "d MMMM yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "d.M.yyyy" };
        if (!DateTime.TryParseExact(text, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            && !DateTime.TryParse(text, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out parsed))
            return false;
        date = parsed.Date;
        return true;
    }

    /// <summary>A revision's number: its place among the project's revisions numbered as it is.</summary>
    public static string NumberOf(BimDocument document, Revision revision) => NumberOf(document.Revisions, revision);

    /// <summary>A revision's number in a list of revisions, as the project would number it with that list.</summary>
    public static string NumberOf(IReadOnlyList<Revision> revisions, Revision revision)
    {
        var place = revisions.Where(other => other.Numbering == revision.Numbering).ToList().IndexOf(revision) + 1;
        if (place <= 0) return "?";
        return revision.Numbering switch
        {
            RevisionNumbering.Preliminary => $"P{place:00}",
            RevisionNumbering.Contractual => $"C{place:00}",
            RevisionNumbering.Numeric => place.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => Letters(place)
        };
    }

    /// <summary>A, B ... H, J ... N, P ... Z, then AA, AB: the letters, without I and O.</summary>
    public static string Letters(int place)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        var text = string.Empty;
        for (var n = place; n > 0; n = (n - 1) / alphabet.Length) text = alphabet[(n - 1) % alphabet.Length] + text;
        return text;
    }

    /// <summary>"P02 - Planning issue": a revision as it is picked from a list.</summary>
    public static string Describe(BimDocument document, Revision revision) =>
        string.IsNullOrWhiteSpace(revision.Description) ? NumberOf(document, revision) : $"{NumberOf(document, revision)} - {revision.Description}";

    /// <summary>
    /// The revisions on a sheet, in the project's order: those clouded in a view placed on it,
    /// and those put on it by hand for a change with nothing to cloud.
    /// </summary>
    public static IReadOnlyList<Revision> OnSheet(BimDocument document, Sheet sheet)
    {
        var on = Clouded(document, sheet).ToHashSet();
        on.UnionWith(sheet.RevisionIds);
        return document.Revisions.Where(revision => on.Contains(revision.Id)).ToList();
    }

    /// <summary>The revisions clouded in the views on a sheet, by their ids.</summary>
    public static IEnumerable<Guid> Clouded(BimDocument document, Sheet sheet) =>
        document.All<RevisionCloud>()
            .Where(cloud => sheet.Viewports.Any(viewport => Shows(document, viewport.View, cloud)))
            .Select(cloud => cloud.RevisionId)
            .Distinct();

    /// <summary>The latest revision on a sheet, or null where it has none.</summary>
    public static Revision? CurrentOn(BimDocument document, Sheet sheet) => OnSheet(document, sheet).LastOrDefault();

    /// <summary>Whether a view shows a cloud: the plan of its storey, or a callout of that plan round it.</summary>
    public static bool Shows(BimDocument document, ViewReference view, RevisionCloud cloud) => view.Kind switch
    {
        ViewKind.FloorPlan => view.TargetId == cloud.LevelId,
        ViewKind.Callout => document.FindElement(view.TargetId) is Callout callout
                            && callout.Parent.Kind == ViewKind.FloorPlan && callout.Parent.TargetId == cloud.LevelId
                            && Overlaps(callout.Region, cloud.Bounds),
        _ => false
    };

    private static bool Overlaps(BoundingBox2D a, BoundingBox2D b) =>
        !a.IsEmpty && !b.IsEmpty && a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY;
}

/// <summary>
/// A revision cloud (specification section 6.5): a scalloped line round what a revision changed,
/// drawn on a plan, with a tag that reads the revision's number. The sheets showing it carry the
/// revision in their revision schedule. A cloud of an issued revision is locked with it.
/// </summary>
public sealed class RevisionCloud : Element
{
    public override BuiltInCategory Category => BuiltInCategory.RevisionClouds;

    public Guid RevisionId { get; set; }

    /// <summary>The outline round the change, in plan, closed back to its first point.</summary>
    public List<Point2D> Outline { get; } = new();

    /// <summary>Where its tag stands - its leader drawn to the cloud - or null for none.</summary>
    public Point2D? TagAt { get; set; }

    public BoundingBox2D Bounds => Outline.Count == 0 ? BoundingBox2D.Empty : BoundingBox2D.Around(Outline);

    public Revision? Revision(BimDocument document) => document.FindRevision(RevisionId);

    /// <summary>Whether its revision has been issued, and so it can no longer be changed.</summary>
    public bool IsLocked(BimDocument document) => Revision(document)?.Issued == true;

    /// <summary>Whether it is drawn: its clouds, its tag.</summary>
    public (bool Cloud, bool Tag) Drawn(BimDocument document) => Revision(document)?.Visibility switch
    {
        RevisionVisibility.Hidden => (false, false),
        RevisionVisibility.TagsOnly => (false, TagAt is not null),
        _ => (true, TagAt is not null)
    };

    /// <summary>
    /// The scallops: each edge cut into arcs about as long as a chord, bowed outwards - the
    /// cloud's line as points, closed. <paramref name="chord"/> is in model millimetres.
    /// </summary>
    public List<List<Point2D>> Scallops(double chord)
    {
        var arcs = new List<List<Point2D>>();
        if (Outline.Count < 2 || chord <= 0) return arcs;

        // Outwards is to the right going round anticlockwise, to the left going round clockwise.
        var area = 0.0;
        for (var i = 0; i < Outline.Count; i++)
        {
            var (a, b) = (Outline[i], Outline[(i + 1) % Outline.Count]);
            area += a.X * b.Y - b.X * a.Y;
        }

        var outwards = area >= 0 ? -1 : 1;
        for (var i = 0; i < Outline.Count; i++)
        {
            var (a, b) = (Outline[i], Outline[(i + 1) % Outline.Count]);
            var length = a.DistanceTo(b);
            if (length < 1e-6) continue;
            var along = (b - a) / length;
            var normal = along.PerpendicularLeft() * outwards;
            var count = Math.Max(1, (int)Math.Round(length / chord, MidpointRounding.AwayFromZero));
            for (var k = 0; k < count; k++)
            {
                var (from, to) = (a + along * (length * k / count), a + along * (length * (k + 1) / count));
                var bow = from.DistanceTo(to) * 0.3;
                arcs.Add(Enumerable.Range(0, 9).Select(s => s / 8.0).Select(t => from + (to - from) * t + normal * (bow * Math.Sin(Math.PI * t))).ToList());
            }
        }

        return arcs;
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var revisions = document.Revisions.Where(revision => !revision.Issued || revision.Id == RevisionId).ToList();
        yield return ParameterValue.BindChoice(
            RevisionCloudParameters.Revision,
            () => Revision(document) is { } revision ? Revisions.Describe(document, revision) : string.Empty,
            text =>
            {
                if (IsLocked(document)) return;
                if (revisions.FirstOrDefault(revision => Revisions.Describe(document, revision) == text) is { } picked) RevisionId = picked.Id;
            },
            revisions.Select(revision => Revisions.Describe(document, revision)).ToList());
        yield return ParameterValue.ReadOnly(RevisionCloudParameters.Number, () => Revision(document) is { } revision ? Revisions.NumberOf(document, revision) : string.Empty);
        yield return ParameterValue.ReadOnly(RevisionCloudParameters.Date, () => Revisions.FormatDate(Revision(document)?.Date));
        yield return ParameterValue.ReadOnly(RevisionCloudParameters.Description, () => Revision(document)?.Description ?? string.Empty);
        yield return ParameterValue.ReadOnly(RevisionCloudParameters.IssuedTo, () => Revision(document)?.IssuedTo ?? string.Empty);
        yield return ParameterValue.ReadOnly(RevisionCloudParameters.Issued, () => IsLocked(document));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => "Revision Cloud";
}

public static class RevisionCloudParameters
{
    public static readonly ParameterDefinition Revision =
        new("Revision", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Number =
        new("Revision Number", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Date =
        new("Revision Date", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Description =
        new("Revision Description", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition IssuedTo =
        new("Issued To", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Issued =
        new("Issued", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.IdentityData);
}
