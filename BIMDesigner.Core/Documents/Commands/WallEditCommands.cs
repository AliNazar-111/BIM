using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Documents.Commands;

/// <summary>
/// Moves a wall's location line: dragging the whole wall, dragging one end, or trimming and
/// extending to another wall all reduce to the same change.
/// </summary>
public sealed class MoveWallCommand : IUndoableCommand
{
    private readonly Wall _wall;
    private readonly Point2D _oldStart;
    private readonly Point2D _oldEnd;
    private readonly Point2D _newStart;
    private readonly Point2D _newEnd;

    public MoveWallCommand(
        Wall wall, Point2D oldStart, Point2D oldEnd, Point2D newStart, Point2D newEnd, string name)
    {
        _wall = wall;
        _oldStart = oldStart;
        _oldEnd = oldEnd;
        _newStart = newStart;
        _newEnd = newEnd;
        Name = name;
    }

    public string Name { get; }

    public void Redo()
    {
        _wall.Start = _newStart;
        _wall.End = _newEnd;
    }

    public void Undo()
    {
        _wall.Start = _oldStart;
        _wall.End = _oldEnd;
    }
}

/// <summary>
/// Splits a wall in two at a point on its location line.
///
/// The original element is shortened rather than replaced, so it keeps its id, its mark and
/// every parameter set on it; only the new remainder is a fresh element. Deleting and
/// recreating both halves would silently discard that data and break anything already
/// referring to the wall.
/// </summary>
public sealed class SplitWallCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Wall _wall;
    private readonly Point2D _splitPoint;
    private readonly Point2D _originalEnd;
    private readonly Wall _remainder;

    public SplitWallCommand(BimDocument document, Wall wall, Point2D splitPoint)
    {
        _document = document;
        _wall = wall;
        _splitPoint = splitPoint;
        _originalEnd = wall.End;

        _remainder = new Wall
        {
            Start = splitPoint,
            End = wall.End,
            TypeId = wall.TypeId,
            LevelId = wall.LevelId,
            TopLevelId = wall.TopLevelId,
            TopOffset = wall.TopOffset,
            BaseOffset = wall.BaseOffset,
            UnconnectedHeight = wall.UnconnectedHeight,
            LocationLine = wall.LocationLine,
            Flipped = wall.Flipped,
            RoomBounding = wall.RoomBounding,
            StructuralUsage = wall.StructuralUsage,
            Mark = wall.Mark,
            Comments = wall.Comments,
            Workset = wall.Workset,
            PhaseCreated = wall.PhaseCreated,
            PhaseDemolished = wall.PhaseDemolished
        };
    }

    public string Name => "Split Wall";

    /// <summary>The new wall covering the far half. Useful for selecting it after the split.</summary>
    public Wall Remainder => _remainder;

    public void Redo()
    {
        _wall.End = _splitPoint;
        _document.Add(_remainder);
    }

    public void Undo()
    {
        _document.Remove(_remainder);
        _wall.End = _originalEnd;
    }
}

/// <summary>
/// Trims or extends a wall so that one of its ends lands on another wall's location line
/// (specification section 2.4, "trim/extend").
///
/// Both directions are the same operation: the end moves to where the two location lines
/// cross, whether that is shorter or longer than before.
/// </summary>
public static class WallTrim
{
    /// <summary>
    /// Works out where <paramref name="wall"/> should end to meet <paramref name="target"/>,
    /// and which of its ends should move - whichever already lies nearer the crossing.
    /// Returns false when the two are parallel and never meet.
    /// </summary>
    public static bool TryResolve(Wall wall, Wall target, out Point2D newStart, out Point2D newEnd)
    {
        newStart = wall.Start;
        newEnd = wall.End;

        var wallLine = Line2D.Through(wall.Start, wall.End);
        var targetLine = Line2D.Through(target.Start, target.End);

        if (!Line2D.TryIntersect(wallLine, targetLine, out var crossing)) return false;

        // Moving the nearer end keeps the wall roughly where the user drew it; moving the
        // far end would flip it through the target.
        if (wall.Start.DistanceTo(crossing) < wall.End.DistanceTo(crossing)) newStart = crossing;
        else newEnd = crossing;

        // A zero-length wall is not a wall.
        return newStart.DistanceTo(newEnd) > WallJoins.JoinTolerance;
    }
}
