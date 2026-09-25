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
/// Deletes several elements, and everything hosted by any of them, as one step.
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

    private int CountHosted()
    {
        var ids = _targets.Select(element => element.Id).ToHashSet();

        return _document.Elements
            .OfType<IHostedElement>()
            .Count(hosted => ids.Contains(hosted.HostId) && !ids.Contains(((Element)hosted).Id));
    }

    public void Redo()
    {
        _removed.Clear();

        var ids = _targets.Select(element => element.Id).ToHashSet();
        var doomed = new List<Element>(_targets);

        doomed.AddRange(_document.Elements
            .OfType<IHostedElement>()
            .Where(hosted => ids.Contains(hosted.HostId))
            .Cast<Element>()
            .Where(element => !ids.Contains(element.Id)));

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

    public void Undo()
    {
        foreach (var (element, index) in _removed.OrderBy(entry => entry.Index))
            _document.Elements.Insert(Math.Min(index, _document.Elements.Count), element);

        _removed.Clear();
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
