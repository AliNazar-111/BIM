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
    private readonly List<(ShaftOpening Shaft, double Height, double WasHeight)> _releasedShafts;
    private readonly List<(Column Column, double Height, double WasHeight)> _releasedColumns;
    private readonly List<(Stair Stair, Guid? Top, Guid? Repeat, double Height, Guid? WasTop, Guid? WasRepeat, double WasHeight)> _releasedStairs;
    private readonly List<(Ramp Ramp, double Height, double WasHeight)> _releasedRamps;

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

        // A shaft up to the level keeps the height it has, as a wall does.
        _releasedShafts = document.All<ShaftOpening>()
            .Where(shaft => shaft.TopLevelId == level.Id && !_hosted.Contains(shaft))
            .Select(shaft => (shaft, shaft.TopElevation(document) - shaft.BaseElevation(document), shaft.UnconnectedHeight))
            .ToList();

        // A column up to it the same.
        _releasedColumns = document.All<Column>()
            .Where(column => column.TopLevelId == level.Id && !_hosted.Contains(column))
            .Select(column => (column, column.GetHeight(document), column.UnconnectedHeight))
            .ToList();

        // A stair up to it keeps the height it climbs; one repeated up to it stops at the highest level left above its own top.
        _releasedStairs = document.All<Stair>()
            .Where(stair => !_hosted.Contains(stair) && (stair.TopLevelId == level.Id || stair.RepeatToLevelId == level.Id))
            .Select(stair =>
            {
                var layout = Stairs.Layout(document, stair);
                var top = stair.TopLevelId == level.Id ? null : stair.TopLevelId;
                var reached = top is { } topId ? document.FindLevel(topId)?.Elevation : null;
                var repeat = top is null ? null
                    : stair.RepeatToLevelId != level.Id ? stair.RepeatToLevelId
                    : document.Levels.Where(candidate => candidate.Elevation > reached && candidate.Elevation < level.Elevation)
                        .OrderByDescending(candidate => candidate.Elevation).Select(candidate => (Guid?)candidate.Id).FirstOrDefault();
                return (stair, top, repeat, top is null ? layout.Top - layout.Bottom : stair.UnconnectedHeight, stair.TopLevelId, stair.RepeatToLevelId, stair.UnconnectedHeight);
            })
            .ToList();

        // A ramp up to it keeps the height it rises.
        _releasedRamps = document.All<Ramp>()
            .Where(ramp => !_hosted.Contains(ramp) && ramp.TopLevelId == level.Id)
            .Select(ramp => (ramp, Ramps.Top(document, ramp) - Ramps.Bottom(document, ramp), ramp.UnconnectedHeight))
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

        foreach (var (shaft, height, _) in _releasedShafts)
        {
            shaft.TopLevelId = null;
            if (height > 0) shaft.UnconnectedHeight = height;
        }

        foreach (var (column, height, _) in _releasedColumns)
        {
            column.TopLevelId = null;
            if (height > 0) column.UnconnectedHeight = height;
        }

        foreach (var (stair, top, repeat, height, _, _, _) in _releasedStairs)
        {
            (stair.TopLevelId, stair.RepeatToLevelId) = (top, repeat);
            if (height > 0) stair.UnconnectedHeight = Math.Max(Stairs.MinimumRise, height);
        }

        foreach (var (ramp, height, _) in _releasedRamps)
        {
            ramp.TopLevelId = null;
            ramp.UnconnectedHeight = Math.Max(Ramps.MinimumRise, height);
        }

        foreach (var element in _hosted) _document.Remove(element);

        _document.RemoveLevel(_level);
    }

    public void Undo()
    {
        _document.InsertLevel(_index, _level);

        foreach (var element in _hosted) _document.Add(element);

        foreach (var (wall, _) in _released) wall.TopLevelId = _level.Id;

        foreach (var (shaft, _, wasHeight) in _releasedShafts)
        {
            shaft.TopLevelId = _level.Id;
            shaft.UnconnectedHeight = wasHeight;
        }

        foreach (var (column, _, wasHeight) in _releasedColumns)
        {
            column.TopLevelId = _level.Id;
            column.UnconnectedHeight = wasHeight;
        }

        foreach (var (stair, _, _, _, wasTop, wasRepeat, wasHeight) in _releasedStairs)
            (stair.TopLevelId, stair.RepeatToLevelId, stair.UnconnectedHeight) = (wasTop, wasRepeat, wasHeight);

        foreach (var (ramp, _, wasHeight) in _releasedRamps)
            (ramp.TopLevelId, ramp.UnconnectedHeight) = (_level.Id, wasHeight);
    }
}
