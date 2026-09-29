using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Smooth points added to a spline wall to shape it by hand (specification section 3.1,
/// "spline walls"). A point goes onto the wall where it is clicked, so adding one changes
/// nothing; dragging it then pulls the curve through it. Other walls take corner points
/// instead - see <see cref="WallCorners"/> - but any wall given smooth points becomes a spline.
/// </summary>
public static class WallPoints
{
    /// <summary>How near either end, as a share of the wall's length, a new point may not go: a point on an end shapes nothing.</summary>
    private const double EndMargin = 0.02;

    /// <summary>
    /// The shape a wall has with a point added where it passes a click, or null when the click
    /// is too near an end or an existing point to add one.
    /// </summary>
    public static WallSpline? AddPoint(Wall wall, Point2D click)
    {
        var curve = wall.LocationCurve;
        var length = curve.Length;
        if (length <= 1e-6) return null;

        var along = curve.Locate(click).Along;
        if (along < length * EndMargin || along > length * (1 - EndMargin)) return null;

        // The points it has already, where they are along it, to put the new one among them.
        var points = ShapingPoints(curve).ToList();
        if (points.Any(p => Math.Abs(p.Along - along) < length * EndMargin)) return null;

        points.Add((along, curve.PointAt(along)));
        return WallSpline.Between(wall.Start, wall.End, points.OrderBy(p => p.Along).Select(p => p.Point));
    }

    /// <summary>
    /// The shape a wall has with one of its spline points taken away: a spline through the
    /// rest, or null - straight - when it was the last.
    /// </summary>
    public static WallSpline? RemovePoint(Wall wall, int index)
    {
        var points = wall.LocationCurve.SplinePoints().Where(p => p.Index != index).Select(p => p.Point).ToList();
        return points.Count == 0 ? null : WallSpline.Between(wall.Start, wall.End, points);
    }

    /// <summary>
    /// The points that keep a wall's present shape when it becomes a spline: its own for a
    /// spline, a few along it for an arc or ellipse, none for a straight wall.
    /// </summary>
    private static IEnumerable<(double Along, Point2D Point)> ShapingPoints(WallCurve curve)
    {
        if (curve.IsSpline)
            return curve.SplinePoints().Select(p => (curve.Locate(p.Point).Along, p.Point));

        if (!curve.IsCurved) return Array.Empty<(double, Point2D)>();

        // A bow is held by points every eighth of the way, close enough that the spline
        // through them stays within a hair of it.
        const int pieces = 8;
        return Enumerable.Range(1, pieces - 1)
            .Select(i => curve.Length * i / pieces)
            .Select(along => (along, curve.PointAt(along)));
    }
}

/// <summary>
/// Corner points between walls (specification section 3.1, "wall editing"). A point added to a
/// straight wall splits it there, and the two walls meet at a corner that can be dragged
/// anywhere: each stays straight, and the join between them follows. Taking the point away
/// again makes them one wall.
/// </summary>
public static class WallCorners
{
    /// <summary>Every wall on a level with an end at a point, and which end.</summary>
    public static IReadOnlyList<(Wall Wall, bool AtStart)> At(BimDocument document, Guid levelId, Point2D point) =>
        document.Walls
            .Where(wall => wall.LevelId == levelId)
            .Select(wall => (Wall: wall, Start: wall.Start.DistanceTo(point) <= WallJoins.JoinTolerance, End: wall.End.DistanceTo(point) <= WallJoins.JoinTolerance))
            .Where(w => w.Start != w.End)
            .Select(w => (w.Wall, w.Start))
            .ToList();

    /// <summary>
    /// Whether two walls can become one again: straight, of one type, on one level, meeting
    /// end to end, and with no edited profile, which belongs to one of them only. Curtain walls
    /// bring their grids with them - see MergeWallsCommand.
    /// </summary>
    public static bool CanMerge(Wall first, Wall second) =>
        !ReferenceEquals(first, second) &&
        !first.IsCurved && !second.IsCurved &&
        first.TypeId == second.TypeId && first.LevelId == second.LevelId &&
        first.Profile is null && second.Profile is null &&
        SharedEnd(first, second) is not null;

    /// <summary>Whether two walls meeting end to end run on in one straight line: made one, nothing moves.</summary>
    public static bool InLine(Wall first, Wall second)
    {
        if (SharedEnd(first, second) is not { } joint) return false;

        var line = WallCurve.Of(first.Start, first.End, 0);
        var far = second.Start.DistanceTo(joint) <= second.End.DistanceTo(joint) ? second.End : second.Start;
        var near = first.Start.DistanceTo(joint) <= first.End.DistanceTo(joint) ? first.End : first.Start;

        // Off the line by no more than a join's tolerance, and on the far side of the joint.
        var direction = (joint - near).NormalisedOrDefault(Vector2D.UnitX);
        return Math.Abs(line.Locate(far).Left) <= WallJoins.JoinTolerance && (far - joint).Dot(direction) > 0;
    }

    /// <summary>The point where two walls meet end to end, or null.</summary>
    public static Point2D? SharedEnd(Wall first, Wall second)
    {
        foreach (var a in new[] { first.Start, first.End })
        foreach (var b in new[] { second.Start, second.End })
            if (a.DistanceTo(b) <= WallJoins.JoinTolerance) return a;

        return null;
    }
}
