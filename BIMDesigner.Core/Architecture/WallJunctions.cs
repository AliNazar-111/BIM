using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Architecture;

/// <summary>The kinds of join the Wall Joins tool offers for a junction, as Revit names them.</summary>
public enum JunctionType
{
    Butt,
    Mitre,
    SquareOff
}

/// <summary>
/// Junctions: the points where walls meet, as the Wall Joins tool sees them (specification
/// section 3.1, "change the configuration of a wall join"). Everything about a junction is kept
/// on the ends of the walls that meet there - the kind of join each asks for and how its join is
/// cleaned up - so a junction needs no element of its own, and moving a wall takes its settings
/// with it.
/// </summary>
public static class WallJunctions
{
    /// <summary>The most walls a junction may have for the tool to change it, as in Revit.</summary>
    public const int MaximumWalls = 4;

    /// <summary>Every junction on a level: each point where a wall end meets another wall, or is kept from meeting it.</summary>
    public static IReadOnlyList<Point2D> On(BimDocument document, Guid levelId)
    {
        var points = new List<Point2D>();
        foreach (var wall in document.Walls.Where(w => w.LevelId == levelId))
        {
            if (document.GetWallType(wall) is not { } type) continue;
            var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);

            foreach (var (point, cut, asked) in new[] { (wall.Start, startCut, wall.StartJoin), (wall.End, endCut, wall.EndJoin) })
            {
                var meets = cut.IsJoined || WallCorners.At(document, levelId, point).Count >= 2 ||
                            (asked == WallJoinKind.Disallow && NearAnother(document, wall, point));
                if (meets && points.All(p => p.DistanceTo(point) > WallJoins.JoinTolerance)) points.Add(point);
            }
        }

        return points;
    }

    private static bool NearAnother(BimDocument document, Wall wall, Point2D point) =>
        document.Walls.Any(other => !ReferenceEquals(other, wall) && other.LevelId == wall.LevelId &&
                                    other.LocationCurve.DistanceTo(point) <= (document.GetWallType(other)?.Width ?? 0) / 2 + 1);

    /// <summary>The wall ends at a junction: walls running into the side of another have their end there; the wall they run into does not.</summary>
    public static IReadOnlyList<(Wall Wall, bool AtStart)> Ends(BimDocument document, Guid levelId, Point2D point) =>
        WallCorners.At(document, levelId, point);

    /// <summary>The join a junction has now, as the tool names it.</summary>
    public static JunctionType TypeOf(BimDocument document, Guid levelId, Point2D point)
    {
        var asked = Ends(document, levelId, point).Select(e => WallJoins.JoinAt(e.Wall, point)).ToList();
        if (asked.Any(a => a == WallJoinKind.SquareOff)) return JunctionType.SquareOff;
        if (asked.Any(a => a is WallJoinKind.Butt or WallJoinKind.RunThrough)) return JunctionType.Butt;
        if (asked.Count > 0 && asked.All(a => a == WallJoinKind.Mitre)) return JunctionType.Mitre;

        // Left to itself: two walls turning a corner mitre; anything else butts against a run.
        return asked.Count == 2 && !InLine(Ends(document, levelId, point)) ? JunctionType.Mitre : JunctionType.Butt;
    }

    /// <summary>Whether joins are kept from forming here: every wall end at it disallowed.</summary>
    public static bool IsDisallowed(BimDocument document, Guid levelId, Point2D point)
    {
        var ends = Ends(document, levelId, point);
        return ends.Count > 0 && ends.All(e => WallJoins.JoinAt(e.Wall, point) == WallJoinKind.Disallow);
    }

    /// <summary>How the junction is cleaned, when its ends agree; the view setting when they do not.</summary>
    public static WallJoinCleanup CleanupOf(BimDocument document, Guid levelId, Point2D point)
    {
        var settings = Ends(document, levelId, point).Select(e => e.AtStart ? e.Wall.StartCleanup : e.Wall.EndCleanup).Distinct().ToList();
        return settings.Count == 1 ? settings[0] : WallJoinCleanup.UseViewSetting;
    }

    private static bool InLine(IReadOnlyList<(Wall Wall, bool AtStart)> ends) =>
        ends.Count == 2 && Math.Abs(ends[0].Wall.Direction.Cross(ends[1].Wall.Direction)) < 1e-6;

    /// <summary>
    /// The ways a butt or square-off junction can be put together, for Previous and Next: which
    /// wall carries on while the others stop against it. Two walls turning a corner give two;
    /// walls in line through the junction give one for each line; a wall running into another's
    /// side gives only the one.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Wall>> Orders(BimDocument document, Guid levelId, Point2D point)
    {
        var ends = Ends(document, levelId, point);
        if (ends.Count == 2 && !InLine(ends))
            return new[] { (IReadOnlyList<Wall>)new[] { ends[0].Wall }, new[] { ends[1].Wall } };

        // Lines of walls through the junction, each one a way for the junction to go.
        var lines = new List<List<Wall>>();
        foreach (var (wall, _) in ends)
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].Direction.Cross(wall.Direction)) < 1e-6);
            if (line is null) lines.Add(new List<Wall> { wall });
            else line.Add(wall);
        }

        var runs = lines.Where(l => l.Count >= 2).Select(l => (IReadOnlyList<Wall>)l).ToList();
        return runs.Count > 0 ? runs : new[] { (IReadOnlyList<Wall>)Array.Empty<Wall>() };
    }

    /// <summary>
    /// The ends' settings that give a junction a type, and for butt and square off the order
    /// counted round <see cref="Orders"/>: the walls that carry on ask to, the rest to stop.
    /// </summary>
    public static IReadOnlyList<(Wall Wall, bool AtStart, WallJoinKind Join)> Configure(
        BimDocument document, Guid levelId, Point2D point, JunctionType type, int order)
    {
        var ends = Ends(document, levelId, point);
        if (type == JunctionType.Mitre) return ends.Select(e => (e.Wall, e.AtStart, WallJoinKind.Mitre)).ToList();

        var orders = Orders(document, levelId, point);
        var carriers = orders[((order % orders.Count) + orders.Count) % orders.Count];
        var carry = type == JunctionType.SquareOff ? WallJoinKind.SquareOff : WallJoinKind.RunThrough;

        return ends
            .Select(e => (e.Wall, e.AtStart, carriers.Contains(e.Wall) ? carry : WallJoinKind.Butt))
            .ToList();
    }

    /// <summary>Which of the <see cref="Orders"/> the junction has now: the first whose walls all ask to carry on.</summary>
    public static int OrderOf(BimDocument document, Guid levelId, Point2D point)
    {
        var orders = Orders(document, levelId, point);
        for (var i = 0; i < orders.Count; i++)
            if (orders[i].Count > 0 && orders[i].All(w => WallJoins.JoinAt(w, point) is WallJoinKind.RunThrough or WallJoinKind.SquareOff))
                return i;

        return 0;
    }

    /// <summary>
    /// Whether a wall's join at a point is drawn cleaned up: the ends' own setting where one asks,
    /// else the view's - every join, or only where the walls meeting are all of one type.
    /// </summary>
    public static bool IsClean(BimDocument document, ViewReference view, Wall wall, Point2D point, double reach)
    {
        var ends = document.Walls
            .Where(w => w.LevelId == wall.LevelId)
            .SelectMany(w => new[] { (Wall: w, AtStart: true, At: w.Start), (Wall: w, AtStart: false, At: w.End) })
            .Where(e => e.At.DistanceTo(point) <= reach)
            .ToList();

        var settings = ends.Select(e => e.AtStart ? e.Wall.StartCleanup : e.Wall.EndCleanup).ToList();
        if (settings.Contains(WallJoinCleanup.DontClean)) return false;
        if (settings.Contains(WallJoinCleanup.Clean)) return true;

        return document.ViewSettings.JoinDisplayOf(view) == WallJoinDisplay.CleanAllWallJoins ||
               ends.Select(e => e.Wall.TypeId).Append(wall.TypeId).Distinct().Count() == 1;
    }
}

/// <summary>Changes the join and cleanup settings of wall ends at junctions, as one step - the Wall Joins tool's every edit.</summary>
public sealed class SetWallEndsCommand : IUndoableCommand
{
    private readonly List<(Wall Wall, bool AtStart, WallJoinKind Join, WallJoinCleanup Cleanup, WallJoinKind OldJoin, WallJoinCleanup OldCleanup)> _ends = new();

    public SetWallEndsCommand(IEnumerable<(Wall Wall, bool AtStart, WallJoinKind? Join, WallJoinCleanup? Cleanup)> ends, string name)
    {
        foreach (var (wall, atStart, join, cleanup) in ends)
        {
            var oldJoin = atStart ? wall.StartJoin : wall.EndJoin;
            var oldCleanup = atStart ? wall.StartCleanup : wall.EndCleanup;
            _ends.Add((wall, atStart, join ?? oldJoin, cleanup ?? oldCleanup, oldJoin, oldCleanup));
        }

        Name = name;
    }

    public string Name { get; }

    public void Redo()
    {
        foreach (var end in _ends) Set(end.Wall, end.AtStart, end.Join, end.Cleanup);
    }

    public void Undo()
    {
        foreach (var end in _ends) Set(end.Wall, end.AtStart, end.OldJoin, end.OldCleanup);
    }

    private static void Set(Wall wall, bool atStart, WallJoinKind join, WallJoinCleanup cleanup)
    {
        if (atStart)
        {
            wall.StartJoin = join;
            wall.StartCleanup = cleanup;
        }
        else
        {
            wall.EndJoin = join;
            wall.EndCleanup = cleanup;
        }
    }
}
