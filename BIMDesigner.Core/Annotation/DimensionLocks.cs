using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Annotation;

/// <summary>
/// Dimensions that drive the model. A dimension's value typed moves what its far end is on - the
/// wall, with the walls joined at its locked corners stretching to stay on them - so the two are
/// that far apart. A locked dimension keeps what it measures that far apart from then on: after
/// every edit, where one end has moved, what the other is on is moved after it, as part of the
/// same step, and on along any other locked dimension that moves; where it cannot be kept - both
/// ends moved apart, what has to follow pinned, locks pulling against each other - the edit is
/// refused and the model left as it was.
/// </summary>
public static class DimensionLocks
{
    /// <summary>How far a locked dimension may drift before it is held, mm.</summary>
    public const double Tolerance = 0.5;

    /// <summary>A dimension locked, or unlocked, at what it measures now. One step to undo.</summary>
    public static IUndoableCommand? Lock(BimDocument document, Dimension dimension, bool locked, out string? message)
    {
        if (locked && !dimension.Drivable(document))
        {
            message = "Only a single dimension with both ends on something - a wall, a gridline - can be locked.";
            return null;
        }

        var length = dimension.Measure(document);
        var (wasLocked, wasLength) = (dimension.Locked, dimension.LockedLength);
        message = locked
            ? $"Locked at {DimensionTypes.Of(document, dimension).Format(length)}: what it measures stays that far apart - move one and the other follows."
            : "Unlocked: what it measures moves freely again.";
        return new DelegateCommand(locked ? "Lock Dimension" : "Unlock Dimension",
            () => (dimension.Locked, dimension.LockedLength) = (locked, locked ? length : wasLength),
            () => (dimension.Locked, dimension.LockedLength) = (wasLocked, wasLength));
    }

    /// <summary>
    /// A dimension's value typed: what its far end is on moved square to it so the two are that far
    /// apart - or, where that cannot move, what its near end is on. Null, and why, where neither can.
    /// </summary>
    public static IUndoableCommand? Drive(BimDocument document, Dimension dimension, double value, out string? message)
    {
        message = null;
        if (value <= 0)
        {
            message = "A dimension measures more than nothing.";
            return null;
        }

        if (dimension.Start.ElementId == dimension.End.ElementId)
        {
            message = "Both its ends are on the one element: what it measures is that element's own size, set in its type.";
            return null;
        }

        var (from, to) = dimension.Ends(document);
        var along = (to - from).NormalisedOrDefault(Vector2D.UnitX);
        var by = value - from.DistanceTo(to);
        if (Math.Abs(by) < 1e-6) return null;

        foreach (var (reference, sign) in new[] { (dimension.End, 1.0), (dimension.Start, -1.0) })
            if (document.FindElement(reference.ElementId) is { } element && Moving(document, element, along * (by * sign)) is { } move)
                return move;

        message = "Neither of what it measures can move - pinned, or not something that moves.";
        return null;
    }

    /// <summary>An element moved, a wall with the walls joined at its locked corners stretching to stay on them; null where it cannot move.</summary>
    private static IUndoableCommand? Moving(BimDocument document, Element element, Vector2D by)
    {
        if (element.Pinned || !ElementTransforms.CanMove(element)) return null;
        var followers = element is Wall wall ? WallJointLock.Followers(document, new[] { wall }) : Array.Empty<(Wall, bool)>();
        var move = new NudgeElementsCommand(new[] { element }, followers, by, document);
        return move.IsEmpty ? null : move;
    }

    private static bool Off(BimDocument document, Dimension dimension) =>
        Math.Abs(dimension.Measure(document) - dimension.LockedLength) > Tolerance;

    /// <summary>
    /// After an edit, every locked dimension kept: what moves after it, already moved - or why it
    /// cannot be kept, and the edit is to be taken back. Nothing to do where no lock has moved.
    /// </summary>
    public static (IUndoableCommand? Follow, string? Refused) Hold(BimDocument document, IUndoableCommand edit)
    {
        var locked = document.All<Dimension>().Where(dimension => dimension.Locked && dimension.Between.Count == 0).ToList();
        if (locked.Count == 0 || !locked.Any(dimension => Off(document, dimension))) return (null, null);

        // Where each one's ends stood before the edit, and whether it was kept then - one already
        // off, from a file or a change it could not see, is left as it is.
        edit.Undo();
        var before = locked.ToDictionary(dimension => dimension, dimension => (Ends: dimension.Ends(document), Kept: !Off(document, dimension)));
        edit.Redo();

        var follows = new List<IUndoableCommand>();
        (IUndoableCommand?, string?) Refuse(string why)
        {
            foreach (var follow in Enumerable.Reverse(follows)) follow.Undo();
            return (null, why);
        }

        for (var round = 0; round < 16; round++)
        {
            var off = locked.Where(dimension => before[dimension].Kept && Off(document, dimension)).ToList();
            if (off.Count == 0) return (follows.Count == 0 ? null : new CompositeCommand("Hold Locked Dimensions", follows), null);

            foreach (var dimension in off.Where(dimension => Off(document, dimension)))
            {
                var (wasFrom, wasTo) = before[dimension].Ends;
                var along = (wasTo - wasFrom).NormalisedOrDefault(Vector2D.UnitX);
                var (from, to) = dimension.Ends(document);
                var startMoved = Math.Abs((from - wasFrom).Dot(along)) > Tolerance;
                var endMoved = Math.Abs((to - wasTo).Dot(along)) > Tolerance;
                var said = DimensionTypes.Of(document, dimension).Format(dimension.LockedLength);
                if (startMoved && endMoved)
                    return Refuse($"A dimension locked at {said} would change: both of what it measures moved. Unlock it in its Properties to change it.");

                // The end that did not move follows the one that did, square to the dimension.
                var shift = dimension.LockedLength - (to - from).Dot(along);
                var (reference, by) = startMoved ? (dimension.End, along * shift) : (dimension.Start, along * -shift);
                if (document.FindElement(reference.ElementId) is not { } partner)
                    return Refuse($"A dimension locked at {said} would change, and what it measures to is gone.");
                if (Moving(document, partner, by) is not { } move)
                    return Refuse($"A dimension locked at {said} would change: {Describe(partner)} has to follow, and is pinned. Unpin it, or unlock the dimension.");
                move.Redo();
                follows.Add(move);
            }
        }

        return Refuse("Locked dimensions pull against each other: no way to keep them all. Unlock one to make the change.");
    }

    private static string Describe(Element element) => element switch
    {
        Wall => "the wall at its other end",
        Datums.Grid grid => $"gridline {grid.Name}",
        _ => "what is at its other end"
    };
}
