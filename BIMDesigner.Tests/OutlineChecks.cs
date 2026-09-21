using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>Shape checks shared by the wall junction tests.</summary>
internal static class OutlineChecks
{
    /// <summary>Whether a polygon's edges cross each other anywhere but at shared corners.</summary>
    public static bool IsSelfIntersecting(IReadOnlyList<Point2D> polygon)
    {
        var n = polygon.Count;

        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            // Neighbouring edges share a corner, which is not a crossing.
            if (j == i + 1 || (i == 0 && j == n - 1)) continue;

            if (Crosses(polygon[i], polygon[(i + 1) % n], polygon[j], polygon[(j + 1) % n])) return true;
        }

        return false;
    }

    private static bool Crosses(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        double Side(Point2D p, Point2D q, Point2D r) => (q - p).Cross(r - p);

        var d1 = Side(c, d, a);
        var d2 = Side(c, d, b);
        var d3 = Side(a, b, c);
        var d4 = Side(a, b, d);

        return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) &&
               ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
    }

    /// <summary>
    /// How many wall outlines cover each point of a grid around a joint: the most any point is
    /// covered by, and whether every point near the joint is covered at all. Walls that tile a
    /// junction cover it exactly once.
    /// </summary>
    public static (int MostCovering, bool JointCovered) Coverage(
        BimDocument document, IEnumerable<Wall> walls, Point2D joint, double radius)
    {
        walls = walls.ToList();
        var outlines = walls.Select(wall =>
        {
            var type = document.GetWallType(wall)!;
            return WallJoins.GetBandOutline(document, wall, type, type.Width / 2, -type.Width / 2);
        }).ToList();

        // Within half the thinnest wall of the joint is always inside the junction, whatever the
        // angles: every wall's faces are at least that far away.
        var thinnest = walls.Min(wall => document.GetWallType(wall)!.Width) / 2 * 0.95;

        var most = 0;
        var covered = true;
        const int steps = 40;

        for (var i = 0; i <= steps; i++)
        for (var j = 0; j <= steps; j++)
        {
            // Nudged off the grid so no sample lands exactly on a shared edge.
            var point = new Point2D(
                joint.X - radius + 2 * radius * i / steps + 0.0137,
                joint.Y - radius + 2 * radius * j / steps + 0.0291);

            var count = outlines.Count(outline => Polygon2D.Contains(outline, point));
            most = Math.Max(most, count);

            if (point.DistanceTo(joint) < thinnest && count == 0) covered = false;
        }

        return (most, covered);
    }
}
