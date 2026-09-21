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
    private readonly WallJoinKind _originalEndJoin;
    private readonly double _originalBulge;
    private readonly double _firstBulge;
    private readonly Wall _remainder;
    private readonly List<(Opening Opening, double Distance)> _moved = new();
    private readonly double _splitAlong;

    public SplitWallCommand(BimDocument document, Wall wall, Point2D splitPoint)
    {
        _document = document;
        _wall = wall;
        _originalEnd = wall.End;
        _originalEndJoin = wall.EndJoin;
        _originalBulge = wall.Bulge;

        // On a curved wall the split lands on the arc, and each half keeps its share of the
        // curve, so together they are exactly the wall that was there.
        var curve = wall.LocationCurve;
        _splitAlong = Math.Clamp(curve.Locate(splitPoint).Along, 0, curve.Length);
        _splitPoint = wall.IsCurved ? curve.PointAt(_splitAlong) : splitPoint;
        _firstBulge = curve.Part(0, _splitAlong).Bulge;

        // Doors and windows beyond the split belong to the far half now.
        foreach (var opening in WallOpenings.Of(document, wall).Where(o => o.DistanceAlongWall > _splitAlong))
            _moved.Add((opening, opening.DistanceAlongWall));

        _remainder = new Wall
        {
            Start = _splitPoint,
            End = wall.End,
            Bulge = curve.Part(_splitAlong, curve.Length).Bulge,
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
            EndJoin = wall.EndJoin,
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
        _wall.Bulge = _firstBulge;
        _wall.EndJoin = WallJoinKind.Auto;
        _document.Add(_remainder);

        foreach (var (opening, distance) in _moved)
        {
            opening.HostWallId = _remainder.Id;
            opening.DistanceAlongWall = distance - _splitAlong;
        }
    }

    public void Undo()
    {
        foreach (var (opening, distance) in _moved)
        {
            opening.HostWallId = _wall.Id;
            opening.DistanceAlongWall = distance;
        }

        _document.Remove(_remainder);
        _wall.End = _originalEnd;
        _wall.Bulge = _originalBulge;
        _wall.EndJoin = _originalEndJoin;
    }
}

/// <summary>
/// Changes which line of a wall's assembly its end points sit on, without moving the wall.
///
/// The stored end points are always on the location line, so switching from the centreline
/// to a finish face moves them sideways by half the wall's width while the body stays put.
/// The walls joined to it are carried along: a corner partner or a stem teeing into it has
/// its end slid along its own line to meet the new location line, so the joins still find
/// each other. Doors and windows keep their place in the wall.
///
/// Every position touched is recorded before and after, so undo restores them exactly
/// instead of recomputing the reverse, which would drift by rounding.
/// </summary>
public sealed class ChangeLocationLineCommand : IUndoableCommand
{
    private readonly Wall _wall;
    private readonly WallLocationLine _oldLine;
    private readonly WallLocationLine _newLine;
    private readonly List<(Wall Wall, Point2D Start, Point2D End, Point2D NewStart, Point2D NewEnd)> _walls = new();
    private readonly List<(Opening Opening, double Distance, double NewDistance)> _openings = new();

    public ChangeLocationLineCommand(BimDocument document, Wall wall, WallLocationLine newLine)
    {
        _wall = wall;
        _oldLine = wall.LocationLine;
        _newLine = newLine;

        if (document.GetWallType(wall) is not { } type) return;

        var delta = LocationOffset(wall, newLine, type) - wall.GetLocationLineOffset(type.Structure);
        if (Math.Abs(delta) < 1e-9) return;

        if (wall.IsCurved)
        {
            ShiftCurved(document, wall, type, delta);
            return;
        }

        var shift = wall.ExteriorNormal * delta;

        var newLine2D = Line2D.Through(wall.Start + shift, wall.End + shift);
        var moves = new Dictionary<Wall, (Point2D Start, Point2D End)>
        {
            [wall] = (wall.Start + shift, wall.End + shift)
        };

        void MoveEnd(Wall target, bool atStart, Point2D to)
        {
            var (start, end) = moves.TryGetValue(target, out var current) ? current : (target.Start, target.End);
            moves[target] = atStart ? (to, end) : (start, to);
        }

        // Each end of this wall: follow whatever it is joined to.
        foreach (var atStart in new[] { true, false })
        {
            var joint = atStart ? wall.Start : wall.End;
            var shifted = joint + shift;

            // Curved neighbours are left where they are: their line is not a line to slide along.
            var others = document.Walls
                .Where(other => !ReferenceEquals(other, wall) && other.LevelId == wall.LevelId && !other.IsCurved)
                .ToList();

            var partners = others.Where(other => WallJoins.TouchesAt(other, joint)).ToList();

            // A wall continuing in line keeps its own line; this end simply moves across.
            if (partners.Any(partner => IsParallel(partner, wall)))
            {
                MoveEnd(wall, atStart, shifted);
                continue;
            }

            // Corner partners - or the two halves of a split run this wall tees into - all
            // lie along one line, so they meet the new location line at one point.
            if (partners.Count > 0 && partners.All(partner => IsParallel(partner, partners[0])))
            {
                if (Meet(partners[0], newLine2D) is { } meeting && StaysNear(meeting, joint, type))
                {
                    MoveEnd(wall, atStart, meeting);
                    foreach (var partner in partners)
                        MoveEnd(partner, partner.Start.DistanceTo(joint) <= WallJoins.JoinTolerance, meeting);
                    continue;
                }
            }

            // Ending on the side of a wall that runs through: slide along to stay on it.
            if (partners.Count == 0 &&
                others.FirstOrDefault(other =>
                    Line2D.DistanceFromSegment(joint, other.Start, other.End) <= WallJoins.JoinTolerance) is { } run &&
                !IsParallel(run, wall) &&
                Meet(run, newLine2D) is { } onRun && StaysNear(onRun, joint, type))
            {
                MoveEnd(wall, atStart, onRun);
                continue;
            }

            MoveEnd(wall, atStart, shifted);
        }

        // Walls teeing into the side of this one follow its location line across.
        foreach (var stem in document.Walls)
        {
            if (ReferenceEquals(stem, wall) || stem.LevelId != wall.LevelId || stem.IsCurved || IsParallel(stem, wall)) continue;

            foreach (var atStart in new[] { true, false })
            {
                var end = atStart ? stem.Start : stem.End;
                if (WallJoins.TouchesAt(wall, end)) continue;
                if (Line2D.DistanceFromSegment(end, wall.Start, wall.End) > WallJoins.JoinTolerance) continue;

                if (Meet(stem, newLine2D) is { } meeting && StaysNear(meeting, end, type))
                    MoveEnd(stem, atStart, meeting);
            }
        }

        foreach (var (target, (newStart, newEnd)) in moves)
        {
            // A move that would turn a wall round or shrink it to nothing is not a move.
            if (newStart.DistanceTo(newEnd) <= WallJoins.JoinTolerance ||
                (newEnd - newStart).Dot(target.End - target.Start) <= 0)
                continue;

            _walls.Add((target, target.Start, target.End, newStart, newEnd));

            // Openings are placed from the start, so if the start slid along the wall they
            // have to be moved back by the same amount to stay in the same place.
            var along = target.Direction;
            var startShift = ReferenceEquals(target, wall) ? shift : new Vector2D(0, 0);
            var slide = (newStart - (target.Start + startShift)).Dot(along);
            if (Math.Abs(slide) < 1e-9) continue;

            foreach (var opening in WallOpenings.Of(document, target))
                _openings.Add((opening, opening.DistanceAlongWall, Math.Max(0, opening.DistanceAlongWall - slide)));
        }
    }

    public string Name => "Change Location Line";

    public void Redo()
    {
        foreach (var (wall, _, _, start, end) in _walls)
        {
            wall.Start = start;
            wall.End = end;
        }

        foreach (var (opening, _, distance) in _openings) opening.DistanceAlongWall = distance;
        _wall.LocationLine = _newLine;
    }

    public void Undo()
    {
        foreach (var (wall, start, end, _, _) in _walls)
        {
            wall.Start = start;
            wall.End = end;
        }

        foreach (var (opening, distance, _) in _openings) opening.DistanceAlongWall = distance;
        _wall.LocationLine = _oldLine;
    }

    /// <summary>
    /// A curved wall's drawn line moves in or out to a concentric arc, with the same sweep, so
    /// its ends move along the radius rather than sideways. A straight corner partner has its
    /// end slid along its own line to meet the new end, and stems teeing into the arc follow
    /// it too. Doors and windows keep their angle round the arc, which is their place in it.
    /// </summary>
    private void ShiftCurved(BimDocument document, Wall wall, WallType type, double delta)
    {
        var curve = wall.LocationCurve;
        var moves = new Dictionary<Wall, (Point2D Start, Point2D End)>
        {
            [wall] = (wall.Start + wall.ExteriorNormalAt(0) * delta, wall.End + wall.ExteriorNormalAt(curve.Length) * delta)
        };

        var others = document.Walls
            .Where(other => !ReferenceEquals(other, wall) && other.LevelId == wall.LevelId && !other.IsCurved)
            .ToList();

        foreach (var atStart in new[] { true, false })
        {
            var joint = atStart ? wall.Start : wall.End;
            var moved = atStart ? moves[wall].Start : moves[wall].End;
            var tangent = new Line2D(moved, wall.TangentAt(atStart ? 0 : curve.Length));

            var partners = others.Where(other => WallJoins.TouchesAt(other, joint)).ToList();
            if (partners.Count == 0 || !partners.All(partner => IsParallel(partner, partners[0]))) continue;

            if (Meet(partners[0], tangent) is not { } meeting || !StaysNear(meeting, joint, type)) continue;

            var (start, end) = moves[wall];
            moves[wall] = atStart ? (meeting, end) : (start, meeting);

            foreach (var partner in partners)
            {
                var partnerAtStart = partner.Start.DistanceTo(joint) <= WallJoins.JoinTolerance;
                moves[partner] = partnerAtStart ? (meeting, partner.End) : (partner.Start, meeting);
            }
        }

        // Stems ending on the arc are carried to the new arc along their own lines.
        var newCurve = WallCurve.Of(moves[wall].Start, moves[wall].End, wall.Bulge);
        foreach (var stem in others)
        {
            foreach (var atStart in new[] { true, false })
            {
                var end = atStart ? stem.Start : stem.End;
                if (WallJoins.TouchesAt(wall, end) || curve.DistanceTo(end) > WallJoins.JoinTolerance) continue;
                if (newCurve.Intersect(Line2D.Through(stem.Start, stem.End), 0, end) is not { } meeting) continue;
                if (!StaysNear(meeting, end, type)) continue;

                var (start, finish) = moves.TryGetValue(stem, out var current) ? current : (stem.Start, stem.End);
                moves[stem] = atStart ? (meeting, finish) : (start, meeting);
            }
        }

        foreach (var (target, (newStart, newEnd)) in moves)
            _walls.Add((target, target.Start, target.End, newStart, newEnd));

        // Same angle round a larger or smaller arc.
        var ratio = newCurve.Length / Math.Max(curve.Length, 1e-9);
        foreach (var opening in WallOpenings.Of(document, wall))
            _openings.Add((opening, opening.DistanceAlongWall, opening.DistanceAlongWall * ratio));
    }

    private static double LocationOffset(Wall wall, WallLocationLine line, WallType type)
    {
        var probe = new Wall { LocationLine = line };
        return probe.GetLocationLineOffset(type.Structure);
    }

    private static bool IsParallel(Wall a, Wall b) => Math.Abs(a.Direction.Cross(b.Direction)) <= 1e-6;

    private static Point2D? Meet(Wall other, Line2D line) =>
        Line2D.TryIntersect(Line2D.Through(other.Start, other.End), line, out var point) ? point : null;

    /// <summary>
    /// A very shallow angle can put the meeting point far down the line; past a few wall
    /// widths that is no longer the same joint, so the join is let go instead.
    /// </summary>
    private static bool StaysNear(Point2D meeting, Point2D joint, WallType type) =>
        meeting.DistanceTo(joint) <= 10 * Math.Max(type.Width, 1);
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

/// <summary>
/// Sets what a wall's top follows: a level with an offset, or nothing, with its own height.
/// The three are changed together so undo puts back exactly the wall there was.
/// </summary>
public sealed class SetWallTopCommand : IUndoableCommand
{
    private readonly Wall _wall;
    private readonly (Guid? Level, double Offset, double Height) _old;
    private readonly (Guid? Level, double Offset, double Height) _new;

    public SetWallTopCommand(Wall wall, Guid? topLevelId, double topOffset, double unconnectedHeight)
    {
        _wall = wall;
        _old = (wall.TopLevelId, wall.TopOffset, wall.UnconnectedHeight);
        _new = (topLevelId, topOffset, unconnectedHeight);
    }

    public string Name => "Change Top Constraint";

    public void Redo() => Apply(_new);

    public void Undo() => Apply(_old);

    private void Apply((Guid? Level, double Offset, double Height) state)
    {
        _wall.TopLevelId = state.Level;
        _wall.TopOffset = state.Offset;
        _wall.UnconnectedHeight = state.Height;
    }
}

/// <summary>
/// Swaps the interior and exterior sides of walls (specification section 3.1).
///
/// The wall mirrors about its location line, which is the line the user drew and the one
/// that stays put - so a wall drawn to its exterior face flips over that face, as it would
/// on a drawing board.
/// </summary>
public sealed class FlipWallsCommand : IUndoableCommand
{
    private readonly List<Wall> _walls;

    public FlipWallsCommand(IEnumerable<Wall> walls)
    {
        _walls = walls.Distinct().ToList();
        Name = _walls.Count == 1 ? "Flip Wall" : $"Flip {_walls.Count} Walls";
    }

    public string Name { get; }

    public void Redo()
    {
        foreach (var wall in _walls) wall.Flipped = !wall.Flipped;
    }

    public void Undo() => Redo();
}

/// <summary>
/// Several edits that the user made as one action, undone and redone together - drawing a
/// wall that also pulls the previous one round to meet it, for instance.
/// </summary>
public sealed class CompositeCommand : IUndoableCommand
{
    private readonly List<IUndoableCommand> _commands;

    public CompositeCommand(string name, IEnumerable<IUndoableCommand> commands)
    {
        Name = name;
        _commands = commands.ToList();
    }

    public string Name { get; }

    public void Redo()
    {
        foreach (var command in _commands) command.Redo();
    }

    public void Undo()
    {
        for (var i = _commands.Count - 1; i >= 0; i--) _commands[i].Undo();
    }
}

/// <summary>
/// Sets how one end of a wall joins (specification section 2.4, "wall joins").
///
/// A corner has two walls and one join, so asking one of them to butt means asking the other
/// to run through: where exactly one other wall meets this end, its end is set to match.
/// Everything changed is recorded so undo puts both back.
/// </summary>
public sealed class SetWallJoinCommand : IUndoableCommand
{
    private readonly List<(Wall Wall, bool AtStart, WallJoinKind Old, WallJoinKind New)> _changes = new();

    public SetWallJoinCommand(BimDocument document, Wall wall, bool atStart, WallJoinKind kind)
    {
        _changes.Add((wall, atStart, atStart ? wall.StartJoin : wall.EndJoin, kind));

        var joint = atStart ? wall.Start : wall.End;
        var partners = document.Walls
            .Where(other => !ReferenceEquals(other, wall) && other.LevelId == wall.LevelId)
            .Where(other => WallJoins.TouchesAt(other, joint))
            .ToList();

        if (partners.Count == 1)
        {
            var partner = partners[0];
            var partnerAtStart = partner.Start.DistanceTo(joint) <= WallJoins.JoinTolerance;
            var current = partnerAtStart ? partner.StartJoin : partner.EndJoin;

            // A partner that has itself opted out keeps its choice.
            if (current != WallJoinKind.Disallow)
                _changes.Add((partner, partnerAtStart, current, Complement(kind)));
        }

        Name = $"Set {(atStart ? "Start" : "End")} Join";
    }

    public string Name { get; }

    /// <summary>What the other wall of a corner has to do for this end to be joined as asked.</summary>
    public static WallJoinKind Complement(WallJoinKind kind) => kind switch
    {
        WallJoinKind.Mitre => WallJoinKind.Mitre,
        WallJoinKind.Butt => WallJoinKind.RunThrough,
        WallJoinKind.RunThrough or WallJoinKind.SquareOff => WallJoinKind.Butt,
        _ => WallJoinKind.Auto
    };

    public void Redo()
    {
        foreach (var (wall, atStart, _, kind) in _changes) Set(wall, atStart, kind);
    }

    public void Undo()
    {
        for (var i = _changes.Count - 1; i >= 0; i--)
        {
            var (wall, atStart, old, _) = _changes[i];
            Set(wall, atStart, old);
        }
    }

    private static void Set(Wall wall, bool atStart, WallJoinKind kind)
    {
        if (atStart) wall.StartJoin = kind;
        else wall.EndJoin = kind;
    }
}

/// <summary>
/// Bends a wall into an arc, or straightens it, by changing how far it bows between its ends
/// (specification section 3.1, curved walls). The ends stay where they are, so joins hold.
/// </summary>
public sealed class BendWallCommand : IUndoableCommand
{
    private readonly Wall _wall;
    private readonly double _oldBulge;
    private readonly double _newBulge;

    public BendWallCommand(Wall wall, double oldBulge, double newBulge)
    {
        _wall = wall;
        _oldBulge = oldBulge;
        _newBulge = newBulge;
    }

    public string Name => _newBulge == 0 ? "Straighten Wall" : "Curve Wall";

    public void Redo() => _wall.Bulge = _newBulge;

    public void Undo() => _wall.Bulge = _oldBulge;
}
