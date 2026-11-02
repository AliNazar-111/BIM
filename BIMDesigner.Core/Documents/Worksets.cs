using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Documents;

/// <summary>What a workset holds: the users' own, or the project's standards, its views, or its levels and grids.</summary>
public enum WorksetKind
{
    User,
    LevelsAndGrids,
    Standards,
    Views
}

/// <summary>A workset: a name the elements on it carry, what kind it is, and whether views show it until told not to.</summary>
public sealed class WorksetInfo
{
    public string Name { get; set; } = Worksharing.DefaultWorkset;

    public WorksetKind Kind { get; init; }

    /// <summary>Whether a view shows it unless it is hidden there; unticked, it is hidden until a view shows it.</summary>
    public bool VisibleByDefault { get; set; } = true;
}

/// <summary>
/// A project shared through a central model: where the central file is, whether this is it or a
/// local copy of it, the worksets its elements are divided into and the one new ones go on. Nothing
/// of it applies until worksharing is enabled. Who has borrowed what, and how the copies are
/// brought together, is kept beside the central file - see the worksharing session.
/// </summary>
public sealed class Worksharing
{
    public const string DefaultWorkset = "Workset1";
    public const string LevelsAndGrids = "Shared Levels and Grids";
    public const string Standards = "Project Standards";
    public const string ViewsWorkset = "Views";

    /// <summary>The central model's file; empty where the project is not shared.</summary>
    public string CentralPath { get; set; } = string.Empty;

    /// <summary>Whether this file is the central model itself rather than someone's local copy of it.</summary>
    public bool IsCentral { get; set; }

    /// <summary>The central's issue this copy was last brought up to: what has been written to it since is newer than this copy.</summary>
    public int BaseVersion { get; set; }

    public bool Enabled => CentralPath.Length > 0;

    public List<WorksetInfo> Worksets { get; } = new();

    /// <summary>The workset new elements go on.</summary>
    public string Active { get; set; } = DefaultWorkset;

    /// <summary>The worksets elements can be put on: the users' own and the levels and grids'.</summary>
    public IEnumerable<WorksetInfo> ForElements => Worksets.Where(workset => workset.Kind is WorksetKind.User or WorksetKind.LevelsAndGrids);

    public WorksetInfo? Find(string name) => Worksets.FirstOrDefault(workset => string.Equals(workset.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The worksets a project starts sharing with: its own, the levels and grids', and the standards' and views'.</summary>
    public void Start()
    {
        foreach (var (name, kind) in new[] { (DefaultWorkset, WorksetKind.User), (LevelsAndGrids, WorksetKind.LevelsAndGrids), (Standards, WorksetKind.Standards), (ViewsWorkset, WorksetKind.Views) })
            if (Find(name) is null) Worksets.Add(new WorksetInfo { Name = name, Kind = kind });
    }
}

/// <summary>Worksets made, renamed, made active and elements moved between them - each one step to undo.</summary>
public static class Worksets
{
    /// <summary>A new workset of the users' own; null and why where the name will not do.</summary>
    public static IUndoableCommand? Create(BimDocument document, string name, out string? why)
    {
        name = name.Trim();
        why = Problem(document, name);
        if (why is not null) return null;
        var workset = new WorksetInfo { Name = name, Kind = WorksetKind.User };
        return new DelegateCommand($"New Workset {name}", () => document.Worksharing.Worksets.Add(workset), () => document.Worksharing.Worksets.Remove(workset));
    }

    /// <summary>A user workset renamed, and every element on it with it; null and why where it cannot be.</summary>
    public static IUndoableCommand? Rename(BimDocument document, WorksetInfo workset, string name, out string? why)
    {
        name = name.Trim();
        why = workset.Kind is WorksetKind.Standards or WorksetKind.Views ? "The project's standards and views worksets keep their names." : Problem(document, name);
        if (why is not null) return null;
        var was = workset.Name;
        var on = document.Elements.Where(element => string.Equals(element.Workset, was, StringComparison.OrdinalIgnoreCase)).ToList();
        var active = string.Equals(document.Worksharing.Active, was, StringComparison.OrdinalIgnoreCase);
        return new DelegateCommand($"Rename Workset {was}",
            () => { workset.Name = name; foreach (var element in on) element.Workset = name; if (active) document.Worksharing.Active = name; },
            () => { workset.Name = was; foreach (var element in on) element.Workset = was; if (active) document.Worksharing.Active = was; });
    }

    /// <summary>The workset new elements go on, set.</summary>
    public static IUndoableCommand SetActive(BimDocument document, string name)
    {
        var was = document.Worksharing.Active;
        return new DelegateCommand("Active Workset", () => document.Worksharing.Active = name, () => document.Worksharing.Active = was);
    }

    private static string? Problem(BimDocument document, string name) =>
        name.Length == 0 ? "A workset needs a name."
        : document.Worksharing.Find(name) is not null ? $"There is a workset called {name} already."
        : null;

    /// <summary>The worksets an element can be put on, by name.</summary>
    public static IReadOnlyList<string> Choices(BimDocument document) => document.Worksharing.ForElements.Select(workset => workset.Name).ToList();
}
