using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Coordination;

/// <summary>Where a linked model is put: its origin on this one's, or its survey point on this one's - each project's own place on the national grid.</summary>
public enum LinkPositioning
{
    OriginToOrigin,
    BySurveyPoint
}

/// <summary>
/// Another model linked into this one: a project or an IFC file - the structure's, the services', the
/// building next door - drawn with this one in 3D and cut on its plans, and clash tested against it,
/// but read from its own file and never made part of this project. It is put where its origin, or its
/// survey point, falls on this project's, moved and turned from there as set; reloaded when its file
/// changes. Only the link is saved: where the file is, and where it goes.
/// </summary>
public sealed class ModelLink : Element
{
    public override BuiltInCategory Category => BuiltInCategory.LinkedModels;

    /// <summary>The file, where it was last found.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The file, from the folder the project was last saved in - how it is found again where the two have moved together.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public LinkPositioning Positioning { get; set; }

    /// <summary>How far it is moved from where its positioning puts it, mm, east and north; and up.</summary>
    public Vector2D Offset { get; set; }

    public double Elevation { get; set; }

    /// <summary>How far it is turned about its origin, degrees anticlockwise.</summary>
    public double Rotation { get; set; }

    /// <summary>Whether it is drawn; unloaded, it is still linked, ready to show again.</summary>
    public bool Shown { get; set; } = true;

    /// <summary>Whether it is drawn faded on the plans, so this model stands out over it.</summary>
    public bool Halftone { get; set; } = true;

    /// <summary>What it is called: its file's name.</summary>
    public string Title => string.IsNullOrWhiteSpace(Path) ? "Linked model" : System.IO.Path.GetFileName(Path);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(ModelLinkParameters.File, () => Path);
        yield return ParameterValue.ReadOnly(ModelLinkParameters.Status, () => ModelLinks.Status(this));
        yield return ParameterValue.BindChoice(ModelLinkParameters.Positioning,
            () => EnumText.Humanise(Positioning),
            v => { if (EnumText.TryParse<LinkPositioning>(v, out var positioning)) Positioning = positioning; },
            EnumText.Choices<LinkPositioning>());
        yield return ParameterValue.BindValidated(ModelLinkParameters.OffsetX, () => Offset.X, (double v) => { if (!double.IsFinite(v)) return false; Offset = new Vector2D(v, Offset.Y); return true; });
        yield return ParameterValue.BindValidated(ModelLinkParameters.OffsetY, () => Offset.Y, (double v) => { if (!double.IsFinite(v)) return false; Offset = new Vector2D(Offset.X, v); return true; });
        yield return ParameterValue.BindValidated(ModelLinkParameters.Elevation, () => Elevation, (double v) => { if (!double.IsFinite(v)) return false; Elevation = v; return true; });
        yield return ParameterValue.BindValidated(ModelLinkParameters.Rotation, () => Rotation, (double v) =>
        {
            if (!double.IsFinite(v)) return false;
            Rotation = (v % 360 + 360) % 360;
            return true;
        });
        yield return ParameterValue.Bind(ModelLinkParameters.Shown, () => Shown, v => Shown = v);
        yield return ParameterValue.Bind(ModelLinkParameters.Halftone, () => Halftone, v => Halftone = v);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class ModelLinkParameters
{
    public static readonly ParameterDefinition File = new("File", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Status = new("Status", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Positioning = new("Positioning", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition OffsetX = new("Offset East", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition OffsetY = new("Offset North", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Elevation = new("Offset Up", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Rotation = new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Shown = new("Loaded", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Halftone = new("Halftone in Plan", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
}

/// <summary>A linked model as read: its document, when its file was last written and when it was read; or why it could not be.</summary>
public sealed class LinkedModel
{
    public BimDocument? Document { get; init; }

    public string? Error { get; init; }

    public DateTime FileTime { get; init; }

    public DateTime ReadAt { get; init; }

    /// <summary>The issue read before this one, where its file had been written again since: what a reload is compared with.</summary>
    public BimDocument? Previous { get; init; }

    /// <summary>When the issue before had been written.</summary>
    public DateTime PreviousTime { get; init; }

    /// <summary>This issue against the one before, once worked out.</summary>
    internal ModelComparison? Changes { get; set; }

    /// <summary>Its meshes as built, in its own coordinates, each its own element's - built once.</summary>
    internal IReadOnlyList<Mesh3D>? Built { get; set; }

    /// <summary>Its cuts at plan heights as last placed, so a plan is not cut through again on every redraw.</summary>
    internal Dictionary<(double Height, double X, double Y, double Rotation, double Elevation, LinkPositioning Positioning), IReadOnlyList<(Point2D A, Point2D B, Guid Element)>> Cuts { get; } = new();
}

/// <summary>
/// The linked models, read and kept while the project is open: each read from its file the first time it
/// is wanted - a project by the project reader, an IFC file by the IFC reader, as the app sets them - and
/// again when it is reloaded; where its file has been written since, it is said to have changed. Its meshes
/// are built once and put where the link puts them: as the link's, to pick it in 3D; as their own
/// elements', to clash test them; cut through at a plan's height, as lines on the plan.
/// </summary>
public static class ModelLinks
{
    /// <summary>What reads a linked file into a document of its own: a project or an IFC file, by its extension.</summary>
    public static Func<string, BimDocument>? Reader { get; set; }

    private static readonly Dictionary<Guid, LinkedModel> Read = new();
    private static readonly object Gate = new();

    /// <summary>The file a link is read from: where it was last found, or beside the project as it was from where the project was saved.</summary>
    public static string? FileOf(ModelLink link, string? projectFolder = null)
    {
        if (File.Exists(link.Path)) return link.Path;
        if (projectFolder is not null && link.RelativePath.Length > 0)
        {
            var near = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectFolder, link.RelativePath));
            if (File.Exists(near)) return near;
        }

        return null;
    }

    /// <summary>A link's model, read the first time it is asked for; null where it is not shown.</summary>
    public static LinkedModel? Of(ModelLink link)
    {
        if (!link.Shown) return null;
        lock (Gate)
        {
            if (Read.TryGetValue(link.Id, out var known)) return known;
        }

        return Load(link);
    }

    /// <summary>The model read again from its file.</summary>
    public static LinkedModel Load(ModelLink link)
    {
        LinkedModel model;
        if (!File.Exists(link.Path)) model = new LinkedModel { Error = $"{link.Title} is not where it was: {link.Path}", ReadAt = DateTime.Now };
        else if (Reader is null) model = new LinkedModel { Error = "Nothing to read it with", ReadAt = DateTime.Now };
        else
        {
            try
            {
                var time = File.GetLastWriteTimeUtc(link.Path);
                LinkedModel? known;
                lock (Gate) Read.TryGetValue(link.Id, out known);

                // The issue it was: kept to compare with, where the file has been written since - or, read again as it was, the one before that.
                var (previous, previousTime) = known is { Document: { } was } && known.FileTime != time ? (was, known.FileTime) : (known?.Previous, known?.PreviousTime ?? default);
                model = new LinkedModel { Document = Reader(link.Path), FileTime = time, ReadAt = DateTime.Now, Previous = previous, PreviousTime = previousTime };
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                model = new LinkedModel { Error = $"{link.Title} could not be read: {error.Message}", ReadAt = DateTime.Now };
            }
        }

        lock (Gate) Read[link.Id] = model;
        return model;
    }

    /// <summary>A link's model forgotten: read again when next wanted.</summary>
    public static void Forget(ModelLink link)
    {
        lock (Gate) Read.Remove(link.Id);
    }

    /// <summary>Whether a link's model has been read and its file written since.</summary>
    public static bool Changed(ModelLink link)
    {
        LinkedModel? model;
        lock (Gate) Read.TryGetValue(link.Id, out model);
        return model is { Document: not null } && File.Exists(link.Path) && File.GetLastWriteTimeUtc(link.Path) > model.FileTime;
    }

    /// <summary>How a link stands, as its Properties and the links' window say it.</summary>
    public static string Status(ModelLink link)
    {
        if (!link.Shown) return "Unloaded";
        LinkedModel? model;
        lock (Gate) Read.TryGetValue(link.Id, out model);
        if (model is null) return File.Exists(link.Path) ? "Not read yet" : "Not found";
        if (model.Error is { } error) return error;
        var count = model.Document!.Elements.Count(element => ClashDetective.Solidly(element.Category));
        return $"{count:#,0} elements, read {model.ReadAt:d MMM HH:mm}" + (Changed(link) ? " - its file has changed since: reload it" : string.Empty);
    }

    /// <summary>Where a point of a linked model is in this one: turned about its origin, then moved as its positioning and offset put it.</summary>
    public static Func<Point3D, Point3D> Placement(BimDocument host, ModelLink link, BimDocument linked)
    {
        var turn = link.Rotation * Math.PI / 180;
        var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
        var shift = link.Positioning == LinkPositioning.BySurveyPoint ? linked.ProjectInformation.SurveyOffset - host.ProjectInformation.SurveyOffset : new Vector2D(0, 0);
        var (dx, dy, dz) = (shift.X + link.Offset.X, shift.Y + link.Offset.Y, link.Elevation);
        return point => new Point3D(point.X * cos - point.Y * sin + dx, point.X * sin + point.Y * cos + dy, point.Z + dz);
    }

    /// <summary>The links shown, with their models read.</summary>
    public static IEnumerable<(ModelLink Link, BimDocument Linked)> Loaded(BimDocument host)
    {
        foreach (var link in host.All<ModelLink>())
            if (Of(link) is { Document: { } linked }) yield return (link, linked);
    }

    private static IReadOnlyList<Mesh3D> Built(ModelLink link, BimDocument linked)
    {
        LinkedModel? model;
        lock (Gate) Read.TryGetValue(link.Id, out model);
        if (model is null) return ModelMeshBuilder.Build(linked);
        lock (model)
            return model.Built ??= ModelMeshBuilder.Build(linked);
    }

    /// <summary>A link's meshes where it puts them, each the link's - picked in 3D as the link, on this project's lowest storey.</summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument host, ModelLink link)
    {
        if (Of(link) is not { Document: { } linked }) return Array.Empty<Mesh3D>();
        var place = Placement(host, link, linked);
        var level = host.Levels.OrderBy(each => each.Elevation).FirstOrDefault()?.Id ?? Guid.Empty;
        return Built(link, linked).Select(mesh => mesh.Mapped(place, link.Id, link.Id, level, mesh.ElementId)).ToList();
    }

    /// <summary>A link's meshes where it puts them, each its own element's - to clash test, with the elements they are.</summary>
    public static IReadOnlyList<Mesh3D> ElementMeshes(BimDocument host, ModelLink link)
    {
        if (Of(link) is not { Document: { } linked }) return Array.Empty<Mesh3D>();
        var place = Placement(host, link, linked);
        return Built(link, linked).Select(mesh => mesh.Mapped(place)).ToList();
    }

    /// <summary>What each shown link has cut through at a height of this project: its lines on the plan there.</summary>
    public static IReadOnlyList<(ModelLink Link, IReadOnlyList<(Point2D A, Point2D B, Guid Element)> Lines)> CutAt(BimDocument host, double height)
    {
        var cuts = new List<(ModelLink, IReadOnlyList<(Point2D, Point2D, Guid)>)>();
        foreach (var (link, linked) in Loaded(host))
        {
            LinkedModel? model;
            lock (Gate) Read.TryGetValue(link.Id, out model);
            var moved = Placement(host, link, linked)(new Point3D(0, 0, 0));
            var key = (Math.Round(height, 1), moved.X, moved.Y, link.Rotation, link.Elevation, link.Positioning);
            IReadOnlyList<(Point2D, Point2D, Guid)>? lines = null;
            if (model is not null) lock (model) model.Cuts.TryGetValue(key, out lines);
            if (lines is null)
            {
                lines = Sight.SliceAt(ElementMeshes(host, link), height);
                if (model is not null) lock (model) model.Cuts[key] = lines;
            }

            cuts.Add((link, lines));
        }
        return cuts;
    }

    /// <summary>A link's model as it now is against the issue read before it - what its last reload brought; null where there is none.</summary>
    public static ModelComparison? SinceLastIssue(ModelLink link)
    {
        LinkedModel? model;
        lock (Gate) Read.TryGetValue(link.Id, out model);
        if (!link.Shown || model is not { Document: { } now, Previous: { } before }) return null;
        // To the minute - or the second, where both were written within one.
        var (then, written) = (model.PreviousTime.ToLocalTime(), model.FileTime.ToLocalTime());
        var format = then.ToString("yyyyMMddHHmm") == written.ToString("yyyyMMddHHmm") ? "d MMM yyyy HH:mm:ss" : "d MMM yyyy HH:mm";
        lock (model)
            return model.Changes ??= Comparisons.Compare(before, now, $"{link.Title}, written {then.ToString(format)}", $"{link.Title}, written {written.ToString(format)}", DateTime.Now);
    }

    /// <summary>The issue of a link's model read before the one it now shows; null where there is none.</summary>
    public static BimDocument? PreviousOf(ModelLink link)
    {
        lock (Gate) return Read.TryGetValue(link.Id, out var model) ? model.Previous : null;
    }

    /// <summary>The element of a linked model an id is, and the link it is in; null where it is in none of them.</summary>
    public static (ModelLink Link, BimDocument Linked, Element Element)? Find(BimDocument host, Guid id)
    {
        foreach (var (link, linked) in Loaded(host))
            if (linked.FindElement(id) is { } element) return (link, linked, element);
        return null;
    }
}
