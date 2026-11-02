using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Annotation;

/// <summary>
/// Dimensions that drive the model. A dimension's value typed moves what its far end is on - the
/// wall, with the walls at its locked corners and teed into it stretching to stay on it - so the two are
/// that far apart. A locked dimension keeps what it measures that far apart from then on: after
/// every edit, where one end has moved, what the other is on is moved after it, as part of the
/// same step, and on along any other locked dimension that moves; where it cannot be kept - both
/// ends moved apart, what has to follow pinned, locks pulling against each other - the edit is
/// refused and the model left as it was. A string made equal (EQ) has what it measures to between
/// its ends spaced evenly, and spaced again whenever an end moves; one of the middle moved on its
/// own would leave it uneven, and is refused.
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

        message = "Neither of what it measures can move - pinned, not something that moves, or a wall meeting it would be left with no length.";
        return null;
    }

    /// <summary>An element moved, a wall with the walls at its locked corners and teed into it stretching to stay on it; null where it cannot move.</summary>
    private static IUndoableCommand? Moving(BimDocument document, Element element, Vector2D by)
    {
        if (element.Pinned || !ElementTransforms.CanMove(element)) return null;
        var followers = element is Wall wall ? WallJointLock.Following(document, new[] { wall }) : Array.Empty<WallFollower>();
        var move = new NudgeElementsCommand(new[] { element }, followers, by, document);
        return move.IsEmpty || move.Collapses(by) ? null : move;
    }

    private static bool Off(BimDocument document, Dimension dimension) =>
        Math.Abs(dimension.Measure(document) - dimension.LockedLength) > Tolerance;

    /// <summary>
    /// A string made equal (EQ), or let go: made equal, what it measures to between its ends is moved
    /// along it, in the same step, so its stretches are even. Null, and why, where they cannot be.
    /// </summary>
    public static IUndoableCommand? Equalise(BimDocument document, Dimension dimension, bool equal, out string? message)
    {
        message = null;
        if (equal == dimension.Equal) return null;

        var set = new DelegateCommand(equal ? "Equal Dimension" : "Unequal Dimension",
            () => dimension.Equal = equal, () => dimension.Equal = !equal);
        if (!equal)
        {
            message = "Its stretches move freely again.";
            return set;
        }

        if (dimension.Between.Count == 0)
        {
            message = "Only a string - a dimension through what lies between its ends - can be made equal.";
            return null;
        }

        var (moves, why) = Spacing(document, dimension);
        if (moves is null)
        {
            message = why;
            return null;
        }

        // Found by making them; taken back, to be made again as the one step.
        foreach (var move in Enumerable.Reverse(moves)) move.Undo();
        var segments = dimension.Segments(document).Count;
        message = $"Equal: its {segments} stretches held at {DimensionTypes.Of(document, dimension).Format(dimension.Measure(document) / segments)} each - move an end and the rest are spaced again.";
        return new CompositeCommand("Equal Dimension", moves.Prepend(set).ToList());
    }

    /// <summary>Whether an equal string's stretches have come apart.</summary>
    private static bool Uneven(BimDocument document, Dimension dimension)
    {
        var segments = dimension.Segments(document);
        return segments.Count > 1 && segments.Max() - segments.Min() > Tolerance;
    }

    /// <summary>
    /// What it measures to between its ends moved along it to even stretches, each move made - or
    /// why it cannot be, and nothing moved.
    /// </summary>
    private static (List<IUndoableCommand>? Moves, string? Why) Spacing(BimDocument document, Dimension dimension)
    {
        var references = dimension.Between.Prepend(dimension.Start).Append(dimension.End).ToList();
        if (references.Any(reference => !reference.IsAttached(document)))
            return (null, "Each of what it measures has to be on something - a wall, a gridline - to be spaced evenly.");
        if (references.Select(reference => reference.ElementId).Distinct().Count() < references.Count)
            return (null, "Two of what it measures are on one element - both faces of a wall: measure to each wall's middle to hold them equal.");

        var (from, to) = dimension.Ends(document);
        var along = (to - from).NormalisedOrDefault(Vector2D.UnitX);
        var length = from.DistanceTo(to);
        var order = dimension.Between.OrderBy(reference => (Dimension.StationOf(document, reference, from, along) - from).Dot(along)).ToList();

        var moves = new List<IUndoableCommand>();
        (List<IUndoableCommand>?, string?) Refuse(string why)
        {
            foreach (var move in Enumerable.Reverse(moves)) move.Undo();
            return (null, why);
        }

        for (var i = 0; i < order.Count; i++)
        {
            var by = length * (i + 1) / (order.Count + 1) - (Dimension.StationOf(document, order[i], from, along) - from).Dot(along);
            if (Math.Abs(by) <= Tolerance / 4) continue;
            if (document.FindElement(order[i].ElementId) is not { } element)
                return Refuse("Something it measures to is gone.");
            if (Moving(document, element, along * by) is not { } move)
                return Refuse(element.Pinned || !ElementTransforms.CanMove(element)
                    ? $"{Capital(Describe(element, between: true))} is pinned: unpin it to space them evenly."
                    : $"{Capital(Describe(element, between: true))} cannot move that far: a wall meeting it would be left with no length.");
            move.Redo();
            moves.Add(move);
        }

        return (moves, null);
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Where each of a string's references stood along it, from its start.</summary>
    private static Dictionary<DimensionReference, double> Placed(BimDocument document, Dimension dimension, Point2D from, Vector2D along) =>
        dimension.Between.Prepend(dimension.Start).Append(dimension.End)
            .ToDictionary(reference => reference, reference => (Dimension.StationOf(document, reference, from, along) - from).Dot(along));

    /// <summary>
    /// After an edit, every locked dimension kept: what moves after it, already moved - or why it
    /// cannot be kept, and the edit is to be taken back. Nothing to do where no lock has moved.
    /// </summary>
    public static (IUndoableCommand? Follow, string? Refused) Hold(BimDocument document, IUndoableCommand edit)
    {
        var locked = document.All<Dimension>().Where(dimension => dimension.Locked && dimension.Between.Count == 0).ToList();
        var equal = document.All<Dimension>().Where(dimension => dimension.Equal && dimension.Between.Count > 0).ToList();
        if (!locked.Any(dimension => Off(document, dimension)) && !equal.Any(dimension => Uneven(document, dimension))) return (null, null);

        // Where each one's ends stood before the edit, and whether it was kept then - one already
        // off, from a file or a change it could not see, is left as it is.
        edit.Undo();
        var before = locked.ToDictionary(dimension => dimension, dimension => (Ends: dimension.Ends(document), Kept: !Off(document, dimension)));
        var evenBefore = equal.Where(dimension => !Uneven(document, dimension)).ToDictionary(dimension => dimension, dimension =>
        {
            var (from, to) = dimension.Ends(document);
            var along = (to - from).NormalisedOrDefault(Vector2D.UnitX);
            return (From: from, Along: along, Placed: Placed(document, dimension, from, along));
        });
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
            var uneven = evenBefore.Keys.Where(dimension => Uneven(document, dimension)).ToList();
            if (off.Count == 0 && uneven.Count == 0)
                return (follows.Count == 0 ? null : new CompositeCommand("Hold Dimensions", follows), null);

            // An equal string whose ends have moved is spaced again; one whose middle was moved on its own cannot be.
            foreach (var dimension in uneven)
            {
                var (from, along, placed) = evenBefore[dimension];
                var now = Placed(document, dimension, from, along);
                var moved = now.Where(pair => Math.Abs(pair.Value - placed[pair.Key]) > Tolerance).Select(pair => pair.Key).ToList();
                var ends = new[] { dimension.Start, dimension.End };
                if (moved.Any(reference => !ends.Contains(reference)))
                {
                    var middle = moved.First(reference => !ends.Contains(reference));
                    var what = document.FindElement(middle.ElementId) is { } element ? Describe(element, between: true) : "what it measures to";
                    return Refuse($"A string held equal (EQ) would come apart: {what} moved on its own. Move an end and the rest are spaced again, or turn EQ off in its Properties.");
                }

                var (moves, why) = Spacing(document, dimension);
                if (moves is null) return Refuse($"A string held equal (EQ) cannot be spaced again: {why}");
                follows.AddRange(moves);
                evenBefore[dimension] = (from, along, Placed(document, dimension, from, along));
            }

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
                    return Refuse(partner.Pinned || !ElementTransforms.CanMove(partner)
                        ? $"A dimension locked at {said} would change: {Describe(partner)} has to follow, and is pinned. Unpin it, or unlock the dimension."
                        : $"A dimension locked at {said} would change: {Describe(partner)} has to follow, and a wall meeting it would be left with no length.");
                move.Redo();
                follows.Add(move);
            }
        }

        return Refuse("Locked dimensions pull against each other: no way to keep them all. Unlock one to make the change.");
    }

    private static string Describe(Element element, bool between = false) => element switch
    {
        Wall => between ? "a wall it measures to" : "the wall at its other end",
        Datums.Grid grid => $"gridline {grid.Name}",
        _ => between ? "something it measures to" : "what is at its other end"
    };
}
