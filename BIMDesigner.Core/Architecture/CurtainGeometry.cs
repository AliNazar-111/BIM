using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The plan shapes a curtain wall is built from - a strip of panel or mullion between two
/// distances along the wall, a round mullion - in the wall's own frame, so a curved curtain
/// wall's panels and transoms follow the curve the way its body does.
/// </summary>
public static class CurtainGeometry
{
    /// <summary>Sides of the polygon a round mullion is drawn as.</summary>
    public const int RoundSides = 16;

    /// <summary>
    /// A strip of the wall between two distances along it and two offsets across it from its
    /// centreline (positive toward the exterior), as a closed outline.
    /// </summary>
    public static IReadOnlyList<Point2D> Band(Wall wall, WallType body, double from, double to, double outer, double inner)
    {
        var structure = body.Structure;
        var curve = wall.LocationCurve;

        var points = new List<Point2D> { wall.PointAt(structure, from, outer) };
        points.AddRange(curve.Between(from, to, wall.LeftOf(structure, outer)).Select(a => wall.PointAt(structure, a, outer)));
        points.Add(wall.PointAt(structure, to, outer));
        points.Add(wall.PointAt(structure, to, inner));
        points.AddRange(curve.Between(to, from, wall.LeftOf(structure, inner)).Select(a => wall.PointAt(structure, a, inner)));
        points.Add(wall.PointAt(structure, from, inner));
        return points;
    }

    /// <summary>A round mullion's plan: a circle on the wall's centreline at a distance along it.</summary>
    public static IReadOnlyList<Point2D> Circle(Wall wall, WallType body, double along, double radius)
    {
        var centre = wall.PointAt(body.Structure, along, 0);
        return Enumerable.Range(0, RoundSides)
            .Select(i => 2 * Math.PI * i / RoundSides)
            .Select(angle => new Point2D(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle)))
            .ToList();
    }

    /// <summary>Points along the wall's centreline between two distances, close enough to follow a curve.</summary>
    public static IReadOnlyList<(Point2D Point, Vector2D Across)> Path(Wall wall, WallType body, double from, double to)
    {
        var structure = body.Structure;
        var alongs = new List<double> { from };
        alongs.AddRange(wall.LocationCurve.Between(from, to));
        alongs.Add(to);

        return alongs.Select(a => (wall.PointAt(structure, a, 0), wall.ExteriorNormalAt(a))).ToList();
    }
}
