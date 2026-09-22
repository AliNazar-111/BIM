using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Points added to a wall to shape it by hand (specification section 3.1, "spline walls"). A
/// point goes onto the wall where it is clicked, so adding one changes nothing; dragging it
/// then pulls the wall into a smooth curve through it. Any wall can take points - straight,
/// arc, elliptical or already a spline - and becomes a spline wall once it has one.
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
