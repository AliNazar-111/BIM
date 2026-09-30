using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Documents.Commands;

/// <summary>
/// Adds several elements as one step (specification section 2.4).
///
/// Pasting twelve walls should be one thing to undo, not twelve. The undo stack is a record
/// of what the user did, and they did one thing.
/// </summary>
public sealed class AddElementsCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly List<Element> _elements;

    public AddElementsCommand(BimDocument document, IEnumerable<Element> elements, string name)
    {
        _document = document;
        _elements = elements.ToList();
        Name = name;
    }

    public string Name { get; }

    public IReadOnlyList<Element> Elements => _elements;

    public void Redo()
    {
        foreach (var element in _elements) _document.Add(element);
    }

    public void Undo()
    {
        foreach (var element in _elements) _document.Remove(element);
    }
}

/// <summary>
/// Deletes several elements, and everything hosted by any of them, as one step - and the
/// dormers made on a roof being deleted, which stand on it and have nowhere else to be.
///
/// Undo restores the original order, so the project browser and the drawing order do not
/// shuffle every time something is undone.
/// </summary>
public sealed class DeleteElementsCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly List<Element> _targets;
    private readonly List<(Element Element, int Index)> _removed = new();

    public DeleteElementsCommand(BimDocument document, IEnumerable<Element> elements)
    {
        _document = document;
        _targets = elements.Distinct().ToList();

        var hosted = CountHosted();

        Name = (_targets.Count, hosted) switch
        {
            (1, 0) => $"Delete {_targets[0].Category}",
            (1, _) => $"Delete {_targets[0].Category} and {hosted} hosted",
            (_, 0) => $"Delete {_targets.Count} Elements",
            _ => $"Delete {_targets.Count} Elements and {hosted} hosted"
        };
    }

    public string Name { get; }

    private int CountHosted() => Doomed().Count - _targets.Count;

    /// <summary>What goes: the targets, the dormers made on any roof among them, and everything hosted by any of those.</summary>
    private List<Element> Doomed()
    {
        var doomed = new List<Element>(_targets);
        var ids = doomed.Select(element => element.Id).ToHashSet();

        foreach (var roof in _targets.OfType<Roof>())
        foreach (var dormer in _document.Elements.OfType<Roof>().Where(other => other.Dormer is not null && other.JoinedTo == roof.Id).ToList())
        foreach (var part in Dormers.Parts(_document, dormer))
            if (ids.Add(part.Id)) doomed.Add(part);

        doomed.AddRange(_document.Elements
            .OfType<IHostedElement>()
            .Where(hosted => ids.Contains(hosted.HostId))
            .Cast<Element>()
            .Where(element => !ids.Contains(element.Id)));

        return doomed;
    }

    // What was left pointing at something deleted, and what it pointed at: undone, put back.
    private readonly List<Action> _reattach = new();

    public void Redo()
    {
        _removed.Clear();
        _reattach.Clear();

        var doomed = Doomed();
        Detach(doomed.Select(element => element.Id).ToHashSet());

        // Highest index first, so removing one does not shift the others.
        foreach (var entry in doomed
                     .Select(element => (Element: element, Index: _document.Elements.IndexOf(element)))
                     .Where(entry => entry.Index >= 0)
                     .OrderByDescending(entry => entry.Index))
        {
            _document.Elements.RemoveAt(entry.Index);
            _removed.Add(entry);
        }
    }

    /// <summary>
    /// Lets go of what is being deleted: walls and columns attached to a roof or floor that is
    /// going stand to their own heights again, a roof joined to it is no longer joined, and a
    /// roof opened for a dormer that is going is closed - rather than all of them pointing at
    /// something no longer there.
    /// </summary>
    private void Detach(HashSet<Guid> gone)
    {
        foreach (var element in _document.Elements.Where(element => !gone.Contains(element.Id)))
        {
            switch (element)
            {
                case Wall wall:
                    if (wall.TopAttachedTo is { } wallTop && gone.Contains(wallTop)) { wall.TopAttachedTo = null; _reattach.Add(() => wall.TopAttachedTo = wallTop); }
                    if (wall.BaseAttachedTo is { } wallFoot && gone.Contains(wallFoot)) { wall.BaseAttachedTo = null; _reattach.Add(() => wall.BaseAttachedTo = wallFoot); }
                    break;

                case Column column:
                    if (column.TopAttachedTo is { } columnTop && gone.Contains(columnTop)) { column.TopAttachedTo = null; _reattach.Add(() => column.TopAttachedTo = columnTop); }
                    if (column.BaseAttachedTo is { } columnFoot && gone.Contains(columnFoot)) { column.BaseAttachedTo = null; _reattach.Add(() => column.BaseAttachedTo = columnFoot); }
                    break;

                case Roof roof:
                    if (roof.JoinedTo is { } joined && gone.Contains(joined)) { roof.JoinedTo = null; _reattach.Add(() => roof.JoinedTo = joined); }
                    for (var i = roof.DormerOpenings.Count - 1; i >= 0; i--)
                    {
                        if (!gone.Contains(roof.DormerOpenings[i])) continue;

                        var (index, id) = (i, roof.DormerOpenings[i]);
                        roof.DormerOpenings.RemoveAt(i);
                        _reattach.Add(() => roof.DormerOpenings.Insert(index, id));
                    }

                    break;
            }
        }
    }

    public void Undo()
    {
        foreach (var (element, index) in _removed.OrderBy(entry => entry.Index))
            _document.Elements.Insert(Math.Min(index, _document.Elements.Count), element);

        // Last let go, first taken back, so a list is rebuilt in the order it was.
        for (var i = _reattach.Count - 1; i >= 0; i--) _reattach[i]();

        _removed.Clear();
        _reattach.Clear();
    }
}

/// <summary>
/// Moves a set of elements by the same amount.
///
/// Hosted elements are left out: a door is positioned along its wall, so it travels with the
/// wall already. Moving it as well would slide every door along its wall by the distance the
/// wall moved - a bug that looks like the doors drifting.
/// </summary>
public sealed class MoveElementsCommand : IUndoableCommand
{
    private readonly List<Element> _elements;
    private readonly Vector2D _delta;

    public MoveElementsCommand(IEnumerable<Element> elements, Vector2D delta, string? name = null, BimDocument? document = null)
    {
        _elements = elements.Where(ElementTransforms.CanMove).ToList();
        _delta = delta;

        // Moving a grid moves the columns set out on it, unless one has been told to stay put.
        // They are part of the same move, so one Undo puts the bay back as it was.
        var named = _elements.Count;
        if (document is not null) _elements.AddRange(ColumnGrids.Following(document, _elements));

        // A roof window goes with its roof.
        if (document is not null) _elements.AddRange(RoofWindows.Following(document, _elements));

        Name = name ?? (named == 1 ? "Move" : $"Move {named} Elements");
    }

    public string Name { get; }

    public bool IsEmpty => _elements.Count == 0;

    public void Redo()
    {
        foreach (var element in _elements) ElementTransforms.Move(element, _delta);
    }

    public void Undo()
    {
        foreach (var element in _elements) ElementTransforms.Move(element, -_delta);
    }
}

/// <summary>
/// A move that grows as the arrow key is held - a nudge, and then the run of nudges after it.
///
/// One press is one move, but a held key sends thirty a second, and recording each of them
/// would bury the last real edit under a stream of identical steps to be taken back one at a
/// time. So a run is a single command that keeps adding to its own step: one Undo puts the
/// element back where it stood when the key went down.
///
/// A wall left behind at a locked corner stretches rather than travels: only the end that
/// meets what is moving follows it, which is what keeps the corner a corner.
/// </summary>
public sealed class NudgeElementsCommand : IUndoableCommand
{
    private readonly List<Element> _elements;
    private readonly List<(Wall Wall, bool AtStart)> _stretched;
    private Vector2D _delta;

    public NudgeElementsCommand(
        IEnumerable<Element> elements,
        IEnumerable<(Wall Wall, bool AtStart)> stretched,
        Vector2D delta,
        BimDocument? document = null)
    {
        _elements = elements.Where(ElementTransforms.CanMove).ToList();
        _stretched = stretched.ToList();
        _delta = delta;

        // Columns set out on a moved grid go with it. They are worked out before the first
        // step is applied, so they are the columns on the grid where it stands now, not the
        // ones it would land on once pushed.
        var named = _elements.Count;
        if (document is not null) _elements.AddRange(ColumnGrids.Following(document, _elements));

        // A roof window goes with its roof.
        if (document is not null) _elements.AddRange(RoofWindows.Following(document, _elements));

        Name = named == 1 ? "Move" : "Move " + named + " Elements";
    }

    public string Name { get; }

    public bool IsEmpty => _elements.Count == 0 && _stretched.Count == 0;

    /// <summary>How far the run has travelled altogether.</summary>
    public Vector2D Delta => _delta;

    /// <summary>Takes one more step: moves the model, and adds it to what Undo will take back.</summary>
    public void Grow(Vector2D step)
    {
        _delta += step;
        Apply(step);
    }

    public void Redo() => Apply(_delta);

    public void Undo() => Apply(-_delta);

    private void Apply(Vector2D step)
    {
        foreach (var element in _elements) ElementTransforms.Move(element, step);

        foreach (var (wall, atStart) in _stretched)
        {
            if (atStart) wall.Start += step;
            else wall.End += step;
        }
    }
}

/// <summary>
/// Reflects a set of elements about a line.
///
/// A reflection is its own inverse, so undo is the same operation again - which is also why
/// it needs no stored "before" state.
/// </summary>
public sealed class MirrorElementsCommand : IUndoableCommand
{
    private readonly List<Element> _elements;
    private readonly Line2D _axis;

    public MirrorElementsCommand(IEnumerable<Element> elements, Line2D axis)
    {
        _elements = elements.Where(ElementTransforms.CanMove).ToList();
        _axis = axis;

        Name = _elements.Count == 1 ? "Mirror" : $"Mirror {_elements.Count} Elements";
    }

    public string Name { get; }

    public bool IsEmpty => _elements.Count == 0;

    public void Redo()
    {
        foreach (var element in _elements) ElementTransforms.Mirror(element, _axis);
    }

    public void Undo() => Redo();
}
