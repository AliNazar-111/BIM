using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Documents.Commands;


/// <summary>Places an element in the project.</summary>
public sealed class AddElementCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Element _element;

    public AddElementCommand(BimDocument document, Element element, string name)
    {
        _document = document;
        _element = element;
        Name = name;
    }

    public string Name { get; }

    public void Redo() => _document.Add(_element);

    public void Undo() => _document.Remove(_element);
}

/// <summary>
/// Removes an element and everything hosted by it (specification section 2.5: "host
/// deletion deletes hosted elements"). Deleting a wall takes its doors and windows with it,
/// because a door with no wall is not something a building can contain, and leaving one
/// behind would quietly corrupt every schedule that counts them.
///
/// Undo puts everything back at its original position in the collection, so draw order and
/// the project browser do not shuffle on every undo.
/// </summary>
public sealed class DeleteElementCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Element _element;

    /// <summary>Removed elements with the index each held, deepest index first.</summary>
    private readonly List<(Element Element, int Index)> _removed = new();

    public DeleteElementCommand(BimDocument document, Element element)
    {
        _document = document;
        _element = element;

        var hosted = document.Elements
            .OfType<IHostedElement>()
            .Count(candidate => candidate.HostId == element.Id);

        Name = hosted == 0
            ? $"Delete {element.Category}"
            : $"Delete {element.Category} and {hosted} hosted element{(hosted == 1 ? "" : "s")}";
    }

    public string Name { get; }

    public void Redo()
    {
        _removed.Clear();

        // What it hosts goes with it, and what that hosts: a roof's gutters, and their downpipes.
        var doomed = new List<Element> { _element };
        var ids = new HashSet<Guid> { _element.Id };
        for (var added = true; added;)
        {
            var more = _document.Elements.OfType<IHostedElement>()
                .Where(hosted => ids.Contains(hosted.HostId))
                .Cast<Element>()
                .Where(element => ids.Add(element.Id))
                .ToList();
            doomed.AddRange(more);
            added = more.Count > 0;
        }

        // Highest index first, so removing one does not shift the others.
        foreach (var element in doomed
                     .Select(element => (Element: element, Index: _document.Elements.IndexOf(element)))
                     .Where(entry => entry.Index >= 0)
                     .OrderByDescending(entry => entry.Index))
        {
            _document.Elements.RemoveAt(element.Index);
            _removed.Add(element);
        }
    }

    public void Undo()
    {
        // Lowest index first, rebuilding the original order.
        foreach (var (element, index) in _removed.OrderBy(entry => entry.Index))
            _document.Elements.Insert(Math.Min(index, _document.Elements.Count), element);

        _removed.Clear();
    }
}

/// <summary>
/// Re-types an element, which swaps its whole assembly at once. Only the type reference
/// moves; nothing about the placed instance changes.
/// </summary>
public sealed class SetElementTypeCommand : IUndoableCommand
{
    private readonly Element _element;
    private readonly Guid _oldTypeId;
    private readonly Guid _newTypeId;

    public SetElementTypeCommand(Element element, Guid newTypeId, string typeName)
    {
        _element = element;
        _oldTypeId = element.TypeId;
        _newTypeId = newTypeId;
        Name = $"Change Type to {typeName}";
    }

    public string Name { get; }

    public void Redo() => _element.TypeId = _newTypeId;

    public void Undo() => _element.TypeId = _oldTypeId;
}
