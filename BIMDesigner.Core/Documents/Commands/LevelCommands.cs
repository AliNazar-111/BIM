using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Documents.Commands;

/// <summary>Adds a storey (specification section 2.3).</summary>
public sealed class AddLevelCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Level _level;

    public AddLevelCommand(BimDocument document, Level level)
    {
        _document = document;
        _level = level;
    }

    public string Name => $"Add {_level.Name}";

    public Level Level => _level;

    public void Redo() => _document.InsertLevel(_level);

    public void Undo() => _document.RemoveLevel(_level);
}

/// <summary>Renames a storey.</summary>
public sealed class RenameLevelCommand : IUndoableCommand
{
    private readonly Level _level;
    private readonly string _from;
    private readonly string _to;

    public RenameLevelCommand(Level level, string to)
    {
        _level = level;
        _from = level.Name;
        _to = to;
    }

    public string Name => $"Rename to {_to}";

    public void Redo() => _level.Name = _to;

    public void Undo() => _level.Name = _from;
}

/// <summary>
/// Moves a storey up or down.
///
/// Nothing else has to be touched: a wall constrained to this level works its height out from
/// it every time it is asked, so raising a storey raises the walls that reach it and the
/// slabs that sit on it, and the section redraws. That is the whole point of a datum.
/// </summary>
public sealed class MoveLevelCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Level _level;
    private readonly double _from;
    private readonly double _to;

    public MoveLevelCommand(BimDocument document, Level level, double to)
    {
        _document = document;
        _level = level;
        _from = level.Elevation;
        _to = to;
    }

    public string Name => "Move Level";

    public void Redo() => Apply(_to);

    public void Undo() => Apply(_from);

    private void Apply(double elevation)
    {
        _level.Elevation = elevation;

        // The collection is kept in height order, so a level that moves past another has to
        // move in the list too - otherwise every picker shows the storeys out of order.
        _document.RemoveLevel(_level);
        _document.InsertLevel(_level);
    }
}

/// <summary>
/// Deletes a storey, and everything standing on it.
///
/// A level cannot be removed on its own: a wall whose base level has gone has no height and
/// no position in the building, and would quietly corrupt every schedule that counted it. So
/// the deletion cascades, exactly as deleting a wall takes its doors with it - and because it
/// is one command, one undo brings the whole storey back.
///
/// Walls that merely reach <em>up</em> to this level are a different case: they are real
/// walls on a level that still exists, so they keep their height as an unconnected one rather
/// than being deleted.
/// </summary>
public sealed class DeleteLevelCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Level _level;
    private readonly int _index;
    private readonly List<Element> _hosted;
    private readonly List<(Wall Wall, double Height)> _released;

    public DeleteLevelCommand(BimDocument document, Level level)
    {
        _document = document;
        _level = level;
        _index = document.IndexOfLevel(level);
        _hosted = Levels.ElementsOn(document, level.Id).ToList();

        // Their heights are captured now, while the level is still there to measure against.
        _released = Levels.ConstrainedTo(document, level.Id)
            .Where(wall => !_hosted.Contains(wall))
            .Select(wall => (wall, wall.GetHeight(document)))
            .ToList();
    }

    public string Name => $"Delete {_level.Name}";

    /// <summary>What would go with the level. The caller warns before running the command.</summary>
    public IReadOnlyList<Element> Hosted => _hosted;

    public IReadOnlyList<Wall> Released => _released.Select(entry => entry.Wall).ToList();

    public void Redo()
    {
        foreach (var (wall, height) in _released)
        {
            wall.TopLevelId = null;
            if (height > 0) wall.UnconnectedHeight = height;
        }

        foreach (var element in _hosted) _document.Remove(element);

        _document.RemoveLevel(_level);
    }

    public void Undo()
    {
        _document.InsertLevel(_index, _level);

        foreach (var element in _hosted) _document.Add(element);

        foreach (var (wall, _) in _released) wall.TopLevelId = _level.Id;
    }
}
