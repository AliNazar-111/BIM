using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Groups on the plan: any of a group picked picks all of it, so it moves, turns, mirrors,
/// copies and goes as one; made from what is picked, broken up again, and edited - its members
/// picked one by one, added to and taken out of, what is drawn while editing it put in it - with
/// every other group of its name made over to match when the edit is finished.
/// </summary>
public partial class PlanView
{
    /// <summary>The group being edited: its members are picked one by one until it is finished.</summary>
    public Group? EditingGroup { get; private set; }

    /// <summary>Raised when a group's edit starts or ends.</summary>
    public event EventHandler? EditingGroupChanged;

    private int _groupEditDepth;
    private HashSet<Guid>? _groupEditKnown;

    /// <summary>
    /// What picking these picks: a group, or anything in one, brings the whole group - the group
    /// first, so it is what Properties shows - except the group being edited, whose members are
    /// picked on their own.
    /// </summary>
    private IEnumerable<Element> WithGroups(IEnumerable<Element> elements)
    {
        if (Document is null) return elements;

        var picked = new List<Element>();
        foreach (var element in elements)
        {
            var group = element as Group ?? Groups.Of(Document, element);
            if (group is null || ReferenceEquals(group, EditingGroup))
            {
                if (!ReferenceEquals(element, EditingGroup)) picked.Add(element);
                continue;
            }

            picked.AddRange(Groups.Whole(Document, group));
        }

        return picked;
    }

    /// <summary>Makes a group of what is picked, and picks it. Public so tests can drive it.</summary>
    public Group? GroupSelection()
    {
        if (Document is null) return null;
        if (EditingGroup is not null)
        {
            HintChanged?.Invoke(this, "Finish editing the group first.");
            return null;
        }

        if (Groups.Make(Document, _selection.Where(element => element is not Group && Groups.Of(Document, element) is null), ActiveLevelId, out var why) is not { } group)
        {
            HintChanged?.Invoke(this, why ?? "Nothing to group.");
            return null;
        }

        Apply(new AddElementCommand(Document, group, "Create Group"));
        Select(group);
        HintChanged?.Invoke(this, $"{group.Name}: {group.MemberIds.Count} elements, picked, moved and copied as one. A copy is another {group.Name}; Edit Group changes every one.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        return group;
    }

    /// <summary>Breaks up the groups picked: what was in them stays, on its own. Public so tests can drive it.</summary>
    public bool UngroupSelection()
    {
        if (Document is null) return false;
        var groups = _selection.OfType<Group>().ToList();
        if (groups.Count == 0)
        {
            HintChanged?.Invoke(this, "Pick a group to ungroup.");
            return false;
        }

        if (groups.Contains(EditingGroup)) FinishGroupEdit(propagate: false);
        var members = groups.SelectMany(group => Groups.Members(Document, group)).ToList();
        Apply(groups.Count == 1
            ? new DeleteElementCommand(Document, groups[0])
            : new CompositeCommand("Ungroup", groups.Select(group => (IUndoableCommand)new DeleteElementCommand(Document, group)).ToList()));
        SelectMany(members);
        HintChanged?.Invoke(this, $"Ungrouped: {members.Count} elements, each on its own again.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Starts editing the group picked: its members are picked one by one until Finish. Public so tests can drive it.</summary>
    public bool BeginGroupEdit()
    {
        if (Document is null || _selection.OfType<Group>().Distinct().ToList() is not [var group])
        {
            HintChanged?.Invoke(this, "Pick one group to edit.");
            return false;
        }

        EditingGroup = group;
        _groupEditDepth = History?.Depth ?? 0;
        _groupEditKnown = Document.Elements.Select(element => element.Id).ToHashSet();
        Document.Elements.CollectionChanged += OnGroupEditAdded;
        Select(null);

        var others = Groups.Like(Document, group).Count() - 1;
        HintChanged?.Invoke(this, $"Editing {group.Name}: pick, move, change or delete what is in it; what is drawn now goes in it. "
                                  + (others > 0 ? $"Finish makes the {others} other{(others == 1 ? "" : "s")} match." : "Finish when done."));
        EditingGroupChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>What is drawn while a group is edited goes in it - a door in one of its walls, a chair beside its table.</summary>
    private void OnGroupEditAdded(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Document is null || EditingGroup is not { } group || _groupEditKnown is null || e.NewItems is null) return;

        var members = group.MemberIds.ToHashSet();
        foreach (var added in e.NewItems.OfType<Element>())
        {
            if (!_groupEditKnown.Add(added.Id) || added is Group) continue;
            var belongs = added is IHostedElement hosted ? members.Contains(hosted.HostId) : ElementTransforms.CanMove(added) && Groups.Gathered(Document, new[] { added }).Count > 0;
            if (belongs && members.Add(added.Id)) group.MemberIds.Add(added.Id);
        }
    }

    /// <summary>
    /// Ends the edit: with <paramref name="propagate"/>, every other group of its name made over
    /// to match it, as one step. Public so tests can drive it.
    /// </summary>
    public bool FinishGroupEdit(bool propagate = true)
    {
        if (Document is null || EditingGroup is not { } group) return false;

        Document.Elements.CollectionChanged -= OnGroupEditAdded;
        EditingGroup = null;
        _groupEditKnown = null;

        var made = propagate ? Groups.Propagate(Document, group) : null;
        if (made is not null) Apply(made);
        if (Document.Elements.Contains(group)) Select(group);

        var others = Groups.Like(Document, group).Count() - 1;
        HintChanged?.Invoke(this, made is null ? $"{group.Name} finished." : $"{group.Name} finished, and the {others} other{(others == 1 ? "" : "s")} made over to match.");
        EditingGroupChanged?.Invoke(this, EventArgs.Empty);
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Ends the edit with what was done in it undone. Public so tests can drive it.</summary>
    public bool CancelGroupEdit()
    {
        if (Document is null || EditingGroup is not { } group) return false;

        Document.Elements.CollectionChanged -= OnGroupEditAdded;
        EditingGroup = null;
        _groupEditKnown = null;
        while (History is { } history && history.Depth > _groupEditDepth && history.CanUndo) history.Undo();

        if (Document.Elements.Contains(group)) Select(group);
        HintChanged?.Invoke(this, $"{group.Name} left as it was.");
        EditingGroupChanged?.Invoke(this, EventArgs.Empty);
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>What is picked put in the group being edited - and what is hosted in it. Public so tests can drive it.</summary>
    public bool AddToEditingGroup()
    {
        if (Document is null || EditingGroup is not { } group) return false;

        var adding = Groups.Gathered(Document, _selection).Where(element => !group.MemberIds.Contains(element.Id)).Select(element => element.Id).ToList();
        if (adding.Count == 0)
        {
            HintChanged?.Invoke(this, "Pick what to add - not what is in it already, or in another group.");
            return false;
        }

        Apply(new DelegateCommand("Add to Group", () => group.MemberIds.AddRange(adding), () => group.MemberIds.RemoveAll(adding.Contains)));
        HintChanged?.Invoke(this, $"{adding.Count} added to {group.Name}.");
        InvalidateVisual();
        return true;
    }

    /// <summary>What is picked taken out of the group being edited - and what is hosted in it. Public so tests can drive it.</summary>
    public bool RemoveFromEditingGroup()
    {
        if (Document is null || EditingGroup is not { } group) return false;

        var picked = _selection.Select(element => element.Id).ToHashSet();
        var removing = Groups.Members(Document, group)
            .Where(element => picked.Contains(element.Id) || element is IHostedElement hosted && picked.Contains(hosted.HostId))
            .Select(element => element.Id).ToList();
        if (removing.Count == 0)
        {
            HintChanged?.Invoke(this, "Pick what to take out of the group.");
            return false;
        }

        if (removing.Count == group.MemberIds.Count)
        {
            HintChanged?.Invoke(this, "That is all of it: Ungroup instead, after Finish.");
            return false;
        }

        var before = group.MemberIds.ToList();
        Apply(new DelegateCommand("Remove from Group", () => group.MemberIds.RemoveAll(removing.Contains), () =>
        {
            group.MemberIds.Clear();
            group.MemberIds.AddRange(before);
        }));
        HintChanged?.Invoke(this, $"{removing.Count} taken out of {group.Name}: it stays where it is, on its own.");
        InvalidateVisual();
        return true;
    }

    /// <summary>A dashed box round each group picked, with its name; round the one being edited, in the selection colour.</summary>
    private void DrawGroups(DrawingContext dc)
    {
        if (Document is null) return;

        var shown = _selection.OfType<Group>().Where(group => group.LevelId == ActiveLevelId).ToList();
        if (EditingGroup is { } editing && !shown.Contains(editing)) shown.Add(editing);
        foreach (var group in shown)
        {
            var box = BoxOf(Groups.Members(Document, group));
            if (box.IsEmpty) continue;

            var pad = 6 / PixelsPerMm;
            var (min, max) = (new Point2D(box.MinX - pad, box.MinY - pad), new Point2D(box.MaxX + pad, box.MaxY + pad));
            var pen = ReferenceEquals(group, EditingGroup) ? _selectedPen : _previewPen;
            dc.DrawGeometry(null, pen, _renderer.BuildOutline(new[] { min, new Point2D(max.X, min.Y), max, new Point2D(min.X, max.Y) }));

            var label = new FormattedText(ReferenceEquals(group, EditingGroup) ? $"Editing {group.Name}" : group.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, pen.Brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var corner = ModelToScreen(new Point2D(min.X, max.Y));
            dc.DrawText(label, new System.Windows.Point(corner.X, corner.Y - label.Height - 2));
        }
    }
}
