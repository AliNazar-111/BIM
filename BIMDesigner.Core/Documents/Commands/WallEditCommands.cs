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
    private readonly WallEllipse? _originalEllipse;
    private readonly WallEllipse? _firstEllipse;
    private readonly WallSpline? _originalSpline;
    private readonly WallSpline? _firstSpline;
    private readonly (IReadOnlyList<Point2D>? Profile, double Length) _originalProfile, _firstProfile;
    private readonly (CurtainGrid? Grid, IReadOnlyList<CurtainPanelOverride>? Panels) _originalCurtain, _firstCurtain;
    private readonly Wall _remainder;
    private readonly List<(Opening Opening, double Distance)> _moved = new();
    private readonly List<Wall> _liningsOfWall = new();
    private readonly List<WallOpening> _movedCuts = new();
    private readonly bool _originalEndLocked;
    private readonly WallJoinCleanup _originalEndCleanup;
    private readonly double _splitAlong;

    public SplitWallCommand(BimDocument document, Wall wall, Point2D splitPoint)
    {
        _document = document;
        _wall = wall;
        _originalEnd = wall.End;
        _originalEndJoin = wall.EndJoin;
        _originalBulge = wall.Bulge;
        _originalEllipse = wall.Ellipse;
        _originalSpline = wall.Spline;

        // On a curved wall the split lands on the arc, and each half keeps its share of the
        // curve, so together they are exactly the wall that was there.
        var curve = wall.LocationCurve;
        _splitAlong = Math.Clamp(curve.Locate(splitPoint).Along, 0, curve.Length);
        _splitPoint = wall.IsCurved ? curve.PointAt(_splitAlong) : splitPoint;
        var first = curve.Part(0, _splitAlong);
        var second = curve.Part(_splitAlong, curve.Length);
        _firstBulge = first.Bulge;
        _firstEllipse = first.Ellipse;
        _firstSpline = first.Spline;

        // An edited profile is cut at the split too, each half keeping its own part of it.
        _originalProfile = (wall.Profile, wall.ProfileLength);
        _firstProfile = _originalProfile;
        (IReadOnlyList<Point2D>? Profile, double Length) secondProfile = (null, 0);
        if (wall.Profile is { } profile)
        {
            // As it stands now: corners on the far end are on the wall's end, however long it has become.
            var current = profile.Select(p => p.X >= wall.ProfileLength - 1e-6 ? new Point2D(curve.Length, p.Y) : p).ToList();
            var (before, after) = WallProfile.Split(current, _splitAlong);
            _firstProfile = before.Count >= 3 ? (before, _splitAlong) : (null, 0);
            secondProfile = after.Count >= 3 ? (after, curve.Length - _splitAlong) : (null, 0);
        }

        // A curtain wall keeps its grid where it is: each half takes the lines and panels on its side.
        _originalCurtain = (wall.CurtainGrid, wall.CurtainPanels);
        _firstCurtain = _originalCurtain;
        (CurtainGrid? Grid, IReadOnlyList<CurtainPanelOverride>? Panels) secondCurtain = (null, null);
        if (CurtainLayout.Of(document, wall) is { } layout)
        {
            var inner = layout.Verticals.Skip(1).SkipLast(1).ToList();
            var horizontals = layout.Horizontals.Skip(1).SkipLast(1).ToList();
            // The columns the near half keeps, and the first column of the far half: the same one
            // when the split falls inside a bay, the next when it falls on a line.
            var firstColumns = layout.Verticals.Count(v => v < _splitAlong - 1e-6);
            var farStart = layout.Verticals.Count(v => v <= _splitAlong + 1e-6) - 1;
            var panels = wall.CurtainPanels ?? Array.Empty<CurtainPanelOverride>();

            _firstCurtain = (new CurtainGrid(inner.Where(v => v < _splitAlong).ToList(), horizontals),
                panels.Where(p => p.Column < firstColumns).ToList());
            secondCurtain = (new CurtainGrid(inner.Where(v => v > _splitAlong).Select(v => v - _splitAlong).ToList(), horizontals),
                panels.Where(p => p.Column >= farStart)
                    .Select(p => p with { Column = p.Column - farStart }).ToList());
        }

        // Doors and windows beyond the split belong to the far half now.
        foreach (var opening in WallOpenings.Of(document, wall).Where(o => o.DistanceAlongWall > _splitAlong))
            _moved.Add((opening, opening.DistanceAlongWall));
        _movedCuts.AddRange(document.Elements.OfType<WallOpening>().Where(o => o.HostWallId == wall.Id && o.DistanceAlongWall > _splitAlong));

        _remainder = new Wall
        {
            Start = _splitPoint,
            End = wall.End,
            Bulge = second.Bulge,
            Ellipse = second.Ellipse,
            Spline = second.Spline,
            Profile = secondProfile.Profile,
            ProfileLength = secondProfile.Length,
            CurtainGrid = secondCurtain.Grid,
            CurtainPanels = secondCurtain.Panels,
            CrossSection = wall.CrossSection,
            SlantAngle = wall.SlantAngle,
            UpperSlantAngle = wall.UpperSlantAngle,
            SlantBreakHeight = wall.SlantBreakHeight,
            OverrideTaper = wall.OverrideTaper,
            ExteriorTaper = wall.ExteriorTaper,
            InteriorTaper = wall.InteriorTaper,
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
            TopAttachedTo = wall.TopAttachedTo,
            BaseAttachedTo = wall.BaseAttachedTo,
            Mark = wall.Mark,
            Comments = wall.Comments,
            Workset = wall.Workset,
            PhaseCreated = wall.PhaseCreated,
            PhaseDemolished = wall.PhaseDemolished
        };

        // Both halves stay joined to what the wall was joined to face to face, and the walls
        // joined to it are joined to both halves.
        _remainder.JoinedTo.AddRange(wall.JoinedTo);
        _remainder.LockedToJoined = wall.LockedToJoined;

        // A locked end stays with the far half; the split itself is not a corner to lock.
        _remainder.EndLocked = wall.EndLocked;
        _originalEndLocked = wall.EndLocked;
        _remainder.EndCleanup = wall.EndCleanup;
        _originalEndCleanup = wall.EndCleanup;
        _liningsOfWall.AddRange(document.Walls.Where(other => other.JoinedTo.Contains(wall.Id)));
    }

    public string Name => "Split Wall";

    /// <summary>The new wall covering the far half. Useful for selecting it after the split.</summary>
    public Wall Remainder => _remainder;

    public void Redo()
    {
        _wall.End = _splitPoint;
        _wall.Bulge = _firstBulge;
        _wall.Ellipse = _firstEllipse;
        _wall.Spline = _firstSpline;
        (_wall.Profile, _wall.ProfileLength) = _firstProfile;
        (_wall.CurtainGrid, _wall.CurtainPanels) = _firstCurtain;
        _wall.EndJoin = WallJoinKind.Auto;
        _document.Add(_remainder);
        foreach (var lining in _liningsOfWall) lining.JoinedTo.Add(_remainder.Id);
        _wall.EndLocked = false;
        _wall.EndCleanup = WallJoinCleanup.UseViewSetting;

        foreach (var (opening, distance) in _moved)
        {
            opening.HostWallId = _remainder.Id;
            opening.DistanceAlongWall = distance - _splitAlong;
        }

        foreach (var cut in _movedCuts)
        {
            cut.HostWallId = _remainder.Id;
            cut.DistanceAlongWall -= _splitAlong;
        }
    }

    public void Undo()
    {
        foreach (var (opening, distance) in _moved)
        {
            opening.HostWallId = _wall.Id;
            opening.DistanceAlongWall = distance;
        }

        foreach (var cut in _movedCuts)
        {
            cut.HostWallId = _wall.Id;
            cut.DistanceAlongWall += _splitAlong;
        }

        foreach (var lining in _liningsOfWall) lining.JoinedTo.Remove(_remainder.Id);
        _wall.EndLocked = _originalEndLocked;
        _wall.EndCleanup = _originalEndCleanup;
        _document.Remove(_remainder);
        _wall.End = _originalEnd;
        _wall.Bulge = _originalBulge;
        _wall.Ellipse = _originalEllipse;
        _wall.Spline = _originalSpline;
        (_wall.Profile, _wall.ProfileLength) = _originalProfile;
        (_wall.CurtainGrid, _wall.CurtainPanels) = _originalCurtain;
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
    private readonly WallEllipse? _oldEllipse;
    private WallEllipse? _newEllipse;
    private readonly WallSpline? _oldSpline;
    private WallSpline? _newSpline;

    public ChangeLocationLineCommand(BimDocument document, Wall wall, WallLocationLine newLine)
    {
        _wall = wall;
        _oldLine = wall.LocationLine;
        _newLine = newLine;
        _oldEllipse = _newEllipse = wall.Ellipse;
        _oldSpline = _newSpline = wall.Spline;

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
        _wall.Ellipse = _newEllipse;
        _wall.Spline = _newSpline;
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
        _wall.Ellipse = _oldEllipse;
        _wall.Spline = _oldSpline;
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

        // An elliptical or spline wall has no concentric curve: an ellipse's axes grow or shrink by
        // the shift instead, and a spline is drawn again through its points moved across.
        if (curve.IsElliptical || curve.IsSpline)
        {
            var shifted = curve.Offset(wall.ExteriorNormalAt(0).Dot(curve.LeftAt(0)) * delta);
            moves[wall] = (shifted.Start, shifted.End);
            _newEllipse = shifted.Ellipse;
            _newSpline = shifted.Spline;
        }

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
        var newCurve = WallCurve.Of(moves[wall].Start, moves[wall].End, wall.Bulge, _newEllipse, _newSpline);
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

        // Only a straight wall can be trimmed: a curved one would have to change its curve.
        if (wall.IsCurved) return false;

        var wallLine = Line2D.Through(wall.Start, wall.End);
        Point2D crossing;

        if (target.IsCurved)
        {
            // To the curve's circle - the arc itself, or where it would run on to - at the
            // crossing nearer this wall's ends.
            var curve = target.LocationCurve;

            // The line meets the circle twice. Prefer where it meets the arc itself, and of
            // those the one nearer the wall.
            var candidates = new[] { wall.Start, wall.End, wall.Start + (wall.Start - wall.End) * 1000, wall.End + (wall.End - wall.Start) * 1000 }
                .Select(near => curve.Intersect(wallLine, 0, near))
                .OfType<Point2D>()
                .Distinct()
                .ToList();
            if (candidates.Count == 0) return false;

            bool OnArc(Point2D point)
            {
                var along = curve.Locate(point).Along;
                return along >= -1e-6 && along <= curve.Length + 1e-6;
            }

            double Distance(Point2D point) => Math.Min(point.DistanceTo(wall.Start), point.DistanceTo(wall.End));

            crossing = candidates
                .OrderBy(point => OnArc(point) ? 0 : 1)
                .ThenBy(Distance)
                .First();
        }
        else if (!Line2D.TryIntersect(wallLine, Line2D.Through(target.Start, target.End), out crossing))
        {
            return false;
        }

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

/// <summary>
/// Reshapes a spline wall by moving one of the points it passes through (specification section
/// 3.1, "spline walls"). The ends stay where they are, so joins hold.
/// </summary>
public sealed class ReshapeSplineWallCommand : IUndoableCommand
{
    private readonly Wall _wall;
    private readonly WallSpline? _oldSpline;
    private readonly WallSpline? _newSpline;
    private readonly (double Bulge, WallEllipse? Ellipse) _oldCurve, _newCurve;

    public ReshapeSplineWallCommand(Wall wall, WallSpline? oldSpline, WallSpline? newSpline, string name = "Move Spline Point", bool straighten = false)
    {
        _wall = wall;
        _oldSpline = oldSpline;
        _newSpline = newSpline;
        Name = name;

        // A wall given points becomes a spline and nothing else: an arc or ellipse it was is
        // in the points now, and taking them all away later leaves it straight, not bowed.
        _oldCurve = (wall.Bulge, wall.Ellipse);
        _newCurve = straighten || (oldSpline is null && newSpline is not null) ? (0, null) : _oldCurve;
    }

    public string Name { get; }

    public void Redo()
    {
        _wall.Spline = _newSpline;
        (_wall.Bulge, _wall.Ellipse) = _newCurve;
    }

    public void Undo()
    {
        _wall.Spline = _oldSpline;
        (_wall.Bulge, _wall.Ellipse) = _oldCurve;
    }
}

/// <summary>
/// Gives a wall an edited elevation outline, or takes it away to go back to the rectangle
/// (specification section 3.1, "edit profile"). The outline is kept as the wall's length stood
/// when it was edited, so its far end stays on the wall's end if the wall later changes length.
/// </summary>
public sealed class SetWallProfileCommand : IUndoableCommand
{
    private readonly Wall _wall;
    private readonly IReadOnlyList<Point2D>? _oldProfile;
    private readonly double _oldLength;
    private readonly IReadOnlyList<Point2D>? _newProfile;
    private readonly double _newLength;

    public SetWallProfileCommand(Wall wall, IReadOnlyList<Point2D>? profile)
    {
        _wall = wall;
        _oldProfile = wall.Profile;
        _oldLength = wall.ProfileLength;
        _newProfile = profile?.ToList();
        _newLength = profile is null ? 0 : wall.Length;
    }

    public string Name => _newProfile is null ? "Reset Profile" : "Edit Profile";

    public void Redo()
    {
        _wall.Profile = _newProfile;
        _wall.ProfileLength = _newLength;
    }

    public void Undo()
    {
        _wall.Profile = _oldProfile;
        _wall.ProfileLength = _oldLength;
    }
}

/// <summary>
/// Sets a curtain wall's own grid and the panels in it (specification section 3.1, "curtain
/// walls"): lines added, moved or taken away, cells glazed, solid, empty or made a door.
/// </summary>
public sealed class SetCurtainLayoutCommand : IUndoableCommand
{
    private readonly Wall _wall;
    private readonly (CurtainGrid? Grid, IReadOnlyList<CurtainPanelOverride>? Panels) _old, _new;

    public SetCurtainLayoutCommand(Wall wall, CurtainGrid? grid, IReadOnlyList<CurtainPanelOverride>? panels, string name = "Edit Curtain Grid")
    {
        _wall = wall;
        _old = (wall.CurtainGrid, wall.CurtainPanels);
        _new = (grid, panels?.ToList());
        Name = name;
    }

    public string Name { get; }

    public void Redo() => (_wall.CurtainGrid, _wall.CurtainPanels) = _new;

    public void Undo() => (_wall.CurtainGrid, _wall.CurtainPanels) = _old;
}

/// <summary>
/// Changes which walls a placed sweep runs along, or how its ends finish, as one step:
/// Revit's Add/Remove Walls and Modify Returns.
/// </summary>
public sealed class EditPlacedSweepCommand : IUndoableCommand
{
    private readonly PlacedSweep _sweep;
    private readonly (List<Guid> Hosts, bool Start, bool End) _before, _after;

    public EditPlacedSweepCommand(PlacedSweep sweep, IEnumerable<Guid> hosts, bool returnAtStart, bool returnAtEnd, string name)
    {
        _sweep = sweep;
        _before = (sweep.HostWallIds.ToList(), sweep.ReturnAtStart, sweep.ReturnAtEnd);
        _after = (hosts.Distinct().ToList(), returnAtStart, returnAtEnd);
        Name = name;
    }

    public string Name { get; }

    public void Redo() => Apply(_after);

    public void Undo() => Apply(_before);

    private void Apply((List<Guid> Hosts, bool Start, bool End) state)
    {
        _sweep.HostWallIds.Clear();
        _sweep.HostWallIds.AddRange(state.Hosts);
        _sweep.ReturnAtStart = state.Start;
        _sweep.ReturnAtEnd = state.End;
    }
}

/// <summary>
/// Takes the corner point out from between two walls (specification section 3.1, "wall
/// editing"): the first becomes one straight wall from its far end to the second's, and the
/// second goes. Doors and windows in either stay where they were along the line, and sweeps
/// placed on the second carry on along the first.
/// </summary>
public sealed class MergeWallsCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Wall _keep;
    private readonly Wall _remove;
    private readonly (Point2D Start, Point2D End, WallJoinKind StartJoin, WallJoinKind EndJoin) _before, _after;
    private readonly List<(Opening Opening, Guid Host, double Distance, double NewDistance)> _openings = new();
    private readonly List<(PlacedSweep Sweep, List<Guid> Hosts, List<Guid> NewHosts)> _sweeps = new();

    public MergeWallsCommand(BimDocument document, Wall keep, Wall remove)
    {
        _document = document;
        _keep = keep;
        _remove = remove;

        var joint = WallCorners.SharedEnd(keep, remove) ?? keep.End;
        var keepStartAtJoint = keep.Start.DistanceTo(joint) <= keep.End.DistanceTo(joint);
        var removeStartAtJoint = remove.Start.DistanceTo(joint) <= remove.End.DistanceTo(joint);
        var far = removeStartAtJoint ? remove.End : remove.Start;
        var farJoin = removeStartAtJoint ? remove.EndJoin : remove.StartJoin;

        _before = (keep.Start, keep.End, keep.StartJoin, keep.EndJoin);
        _after = keepStartAtJoint
            ? (far, keep.End, farJoin, keep.EndJoin)
            : (keep.Start, far, keep.StartJoin, farJoin);

        // Each door and window keeps its place: measured again along the new line from
        // where it was.
        var line = WallCurve.Of(_after.Start, _after.End, 0);
        foreach (var host in new[] { keep, remove })
        foreach (var opening in WallOpenings.Of(document, host))
        {
            var at = host.LocationCurve.PointAt(opening.DistanceAlongWall);
            _openings.Add((opening, opening.HostWallId, opening.DistanceAlongWall,
                Math.Clamp(line.Locate(at).Along, 0, line.Length)));
        }

        foreach (var sweep in document.Elements.OfType<PlacedSweep>().Where(s => s.HostWallIds.Contains(remove.Id)))
        {
            var hosts = sweep.HostWallIds.Select(id => id == remove.Id ? keep.Id : id).Distinct().ToList();
            _sweeps.Add((sweep, sweep.HostWallIds.ToList(), hosts));
        }
    }

    public string Name => "Remove Wall Point";

    public void Redo()
    {
        (_keep.Start, _keep.End, _keep.StartJoin, _keep.EndJoin) = _after;
        foreach (var (opening, _, _, distance) in _openings)
        {
            opening.HostWallId = _keep.Id;
            opening.DistanceAlongWall = distance;
        }

        foreach (var (sweep, _, hosts) in _sweeps)
        {
            sweep.HostWallIds.Clear();
            sweep.HostWallIds.AddRange(hosts);
        }

        _document.Remove(_remove);
    }

    public void Undo()
    {
        _document.Add(_remove);
        foreach (var (sweep, hosts, _) in _sweeps)
        {
            sweep.HostWallIds.Clear();
            sweep.HostWallIds.AddRange(hosts);
        }

        foreach (var (opening, host, distance, _) in _openings)
        {
            opening.HostWallId = host;
            opening.DistanceAlongWall = distance;
        }

        (_keep.Start, _keep.End, _keep.StartJoin, _keep.EndJoin) = _before;
    }
}

/// <summary>
/// Locks two walls laid face to face so they move together, joining them first if they are
/// not, or unlocks them and leaves them joined (specification section 3.1, "auto join and
/// lock walls") - the padlock on the face they share.
/// </summary>
public sealed class SetWallLockCommand : IUndoableCommand
{
    private readonly Wall _holder;
    private readonly Guid _partnerId;
    private readonly bool _wasJoined;
    private readonly bool _wasLocked;
    private readonly bool _locked;

    public SetWallLockCommand(Wall wall, Wall partner, bool locked)
    {
        // The join is kept on whichever of the two already holds it; a new one on the wall picked.
        _holder = partner.JoinedTo.Contains(wall.Id) ? partner : wall;
        _partnerId = ReferenceEquals(_holder, wall) ? partner.Id : wall.Id;
        _wasJoined = _holder.JoinedTo.Contains(_partnerId);
        _wasLocked = _holder.LockedToJoined;
        _locked = locked;
    }

    public string Name => _locked ? "Lock Walls" : "Unlock Walls";

    public void Redo()
    {
        if (!_holder.JoinedTo.Contains(_partnerId)) _holder.JoinedTo.Add(_partnerId);
        _holder.LockedToJoined = _locked;
    }

    public void Undo()
    {
        if (!_wasJoined) _holder.JoinedTo.Remove(_partnerId);
        _holder.LockedToJoined = _wasLocked;
    }
}

/// <summary>Locks or unlocks the corner where walls meet end to end: every wall end there together.</summary>
public sealed class SetJointLockCommand : IUndoableCommand
{
    private readonly List<(Wall Wall, bool AtStart, bool Was)> _ends = new();
    private readonly bool _locked;

    public SetJointLockCommand(BimDocument document, Guid levelId, Point2D corner, bool locked)
    {
        _locked = locked;
        foreach (var (wall, atStart) in WallCorners.At(document, levelId, corner))
            _ends.Add((wall, atStart, atStart ? wall.StartLocked : wall.EndLocked));
    }

    public string Name => _locked ? "Lock Corner" : "Unlock Corner";

    public void Redo()
    {
        foreach (var (wall, atStart, _) in _ends) Set(wall, atStart, _locked);
    }

    public void Undo()
    {
        foreach (var (wall, atStart, was) in _ends) Set(wall, atStart, was);
    }

    private static void Set(Wall wall, bool atStart, bool locked)
    {
        if (atStart) wall.StartLocked = locked;
        else wall.EndLocked = locked;
    }
}

/// <summary>
/// Joins two walls with Join Geometry, or unjoins them (specification section 3.1, "join
/// parallel walls"): joined, the doors and windows of either cut through both.
/// </summary>
public sealed class JoinWallsCommand : IUndoableCommand
{
    private readonly Wall _holder;
    private readonly Guid _partnerId;
    private readonly bool _join;
    private readonly bool _wasJoined;
    private readonly bool _wasLocked;

    public JoinWallsCommand(Wall wall, Wall partner, bool join)
    {
        _holder = partner.JoinedTo.Contains(wall.Id) ? partner : wall;
        _partnerId = ReferenceEquals(_holder, wall) ? partner.Id : wall.Id;
        _join = join;
        _wasJoined = _holder.JoinedTo.Contains(_partnerId);
        _wasLocked = _holder.LockedToJoined;
    }

    public string Name => _join ? "Join Geometry" : "Unjoin Geometry";

    public void Redo()
    {
        if (_join && !_holder.JoinedTo.Contains(_partnerId)) _holder.JoinedTo.Add(_partnerId);
        if (!_join) _holder.JoinedTo.Remove(_partnerId);
        if (_holder.JoinedTo.Count == 0) _holder.LockedToJoined = false;
    }

    public void Undo()
    {
        if (_wasJoined && !_holder.JoinedTo.Contains(_partnerId)) _holder.JoinedTo.Add(_partnerId);
        if (!_wasJoined) _holder.JoinedTo.Remove(_partnerId);
        _holder.LockedToJoined = _wasLocked;
    }
}
