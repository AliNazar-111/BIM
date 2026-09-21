using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Documents.Commands;

/// <summary>Names for new types that do not clash with the ones already there.</summary>
public static class TypeNames
{
    /// <summary>"Exterior 330mm" becomes "Exterior 330mm 2", then 3, and so on.</summary>
    public static string Unique(BimDocument document, string wanted)
    {
        var taken = document.ElementTypes.Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted)) return wanted;

        // A copy of a copy should not become "Name 2 2".
        var stem = wanted;
        var space = wanted.LastIndexOf(' ');
        if (space > 0 && int.TryParse(wanted[(space + 1)..], out _)) stem = wanted[..space];

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>Adds a type to the project - a duplicate made to be edited, usually.</summary>
public sealed class AddTypeCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly ElementType _type;

    public AddTypeCommand(BimDocument document, ElementType type)
    {
        _document = document;
        _type = type;
    }

    public string Name => $"New Type {_type.Name}";

    public void Redo() => _document.AddType(_type);

    public void Undo() => _document.RemoveType(_type);
}

/// <summary>
/// Removes a type nothing is built from. A type in use cannot be deleted: every wall of it
/// would be left pointing at nothing, and quietly giving them another type instead is a
/// decision that belongs to the user.
/// </summary>
public sealed class DeleteTypeCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly ElementType _type;

    public DeleteTypeCommand(BimDocument document, ElementType type)
    {
        if (document.ElementsOfType(type).Any())
            throw new InvalidOperationException($"{type.Name} is in use and cannot be deleted.");

        _document = document;
        _type = type;
    }

    public string Name => $"Delete Type {_type.Name}";

    public void Redo() => _document.RemoveType(_type);

    public void Undo() => _document.AddType(_type);
}

public sealed class RenameTypeCommand : IUndoableCommand
{
    private readonly ElementType _type;
    private readonly string _oldName;
    private readonly string _newName;

    public RenameTypeCommand(ElementType type, string newName)
    {
        _type = type;
        _oldName = type.Name;
        _newName = newName;
    }

    public string Name => $"Rename Type to {_newName}";

    public void Redo() => _type.Name = _newName;

    public void Undo() => _type.Name = _oldName;
}

/// <summary>Gives several elements the same type as one step, as picking a type for a selection does.</summary>
public sealed class SetElementsTypeCommand : IUndoableCommand
{
    private readonly List<(Element Element, Guid OldTypeId)> _elements;
    private readonly Guid _newTypeId;

    public SetElementsTypeCommand(IEnumerable<Element> elements, ElementType type)
    {
        _elements = elements.Distinct().Select(element => (element, element.TypeId)).ToList();
        _newTypeId = type.Id;
        Name = _elements.Count == 1 ? $"Change Type to {type.Name}" : $"Change {_elements.Count} to {type.Name}";
    }

    public string Name { get; }

    public void Redo()
    {
        foreach (var (element, _) in _elements) element.TypeId = _newTypeId;
    }

    public void Undo()
    {
        foreach (var (element, oldTypeId) in _elements) element.TypeId = oldTypeId;
    }
}

/// <summary>
/// Everything the wall type editor changes on a type, taken together: its name, what it is
/// for, how it wraps, and its layers.
/// </summary>
public sealed record WallTypeDesign(
    string Name,
    WallFunction Function,
    WallWrapping WrapAtInserts,
    WallWrapping WrapAtEnds,
    IReadOnlyList<MaterialLayer> Layers)
{
    /// <summary>What a type currently is, with its own copies of the layers.</summary>
    public static WallTypeDesign Of(WallType type) => new(
        type.Name, type.Function, type.WrapAtInserts, type.WrapAtEnds,
        type.Structure.Layers.Select(layer => layer.Clone()).ToList());

    /// <summary>Why this cannot be applied, or null if it can.</summary>
    public string? Problem(BimDocument document, WallType editing)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "The type needs a name.";

        if (document.ElementTypes.Any(type =>
                !ReferenceEquals(type, editing) && string.Equals(type.Name, Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return $"Another type is already called {Name.Trim()}.";

        if (Layers.Count == 0) return "A wall needs at least one layer.";
        if (Layers.Any(layer => layer.Function == LayerFunction.Membrane ? layer.Thickness != 0 : !(layer.Thickness > 0)))
            return "Every layer except a membrane needs a thickness greater than zero.";
        if (Layers.All(layer => layer.Thickness <= 0)) return "A wall cannot be made of membranes alone.";
        if (Layers.Any(layer => document.FindMaterial(layer.MaterialId) is null)) return "Every layer needs a material.";

        return null;
    }
}

/// <summary>
/// Applies an edit to a wall type as one step. Every wall of the type changes with it - that
/// is the point of a type - and undo puts the whole build-up back as it was.
/// </summary>
public sealed class EditWallTypeCommand : IUndoableCommand
{
    private readonly WallType _type;
    private readonly WallTypeDesign _before;
    private readonly WallTypeDesign _after;

    public EditWallTypeCommand(WallType type, WallTypeDesign design)
    {
        _type = type;
        _before = WallTypeDesign.Of(type);
        _after = design with
        {
            Name = design.Name.Trim(),
            Layers = design.Layers.Select(layer => layer.Clone()).ToList()
        };
    }

    public string Name => $"Edit Type {_after.Name}";

    public void Redo() => Apply(_after);

    public void Undo() => Apply(_before);

    private void Apply(WallTypeDesign design)
    {
        _type.Name = design.Name;
        _type.Function = design.Function;
        _type.WrapAtInserts = design.WrapAtInserts;
        _type.WrapAtEnds = design.WrapAtEnds;

        // Fresh copies each time, so later edits to the live layers can never reach back
        // into what undo will restore.
        _type.Structure.ReplaceLayers(design.Layers.Select(layer => layer.Clone()));
    }
}
