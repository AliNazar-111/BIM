using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Views;

/// <summary>Which side of the building an elevation looks at: the North elevation is the north face, seen from the north.</summary>
public enum ElevationSide
{
    North,
    South,
    East,
    West
}

/// <summary>
/// The building's four elevations: each the model seen straight on from one side, with no
/// perspective, as an elevation is drawn. North is up the plan, the project's +Y.
/// </summary>
public static class Elevations
{
    /// <summary>Every side, in the order they are listed.</summary>
    public static IReadOnlyList<ElevationSide> All { get; } = new[] { ElevationSide.North, ElevationSide.East, ElevationSide.South, ElevationSide.West };

    /// <summary>Which way is out of the building toward the side: where the eye is.</summary>
    public static Vector2D Outward(ElevationSide side) => side switch
    {
        ElevationSide.North => new Vector2D(0, 1),
        ElevationSide.South => new Vector2D(0, -1),
        ElevationSide.East => new Vector2D(1, 0),
        _ => new Vector2D(-1, 0)
    };

    /// <summary>The view's name, as the project browser lists it.</summary>
    public static string Name(ElevationSide side) => $"{side} Elevation";

    /// <summary>
    /// What is drawn in plan, on every level, as the box round the building: walls, floors,
    /// roofs and everything standing at a point. Null for an empty project.
    /// </summary>
    public static (Point2D Min, Point2D Max)? Extents(BimDocument document)
    {
        var points = new List<Point2D>();
        foreach (var element in document.Elements)
        {
            switch (element)
            {
                case Architecture.Wall wall:
                    points.Add(wall.Start);
                    points.Add(wall.End);
                    points.Add(wall.LocationCurve.PointAt(wall.Length / 2));
                    break;
                case Architecture.Slab slab:
                    points.AddRange(slab.Boundary);
                    break;
                case Architecture.Column column:
                    points.Add(column.Location);
                    break;
                case Architecture.Chimney chimney:
                    points.Add(chimney.Location);
                    break;
                case Architecture.Component component:
                    points.Add(component.Location);
                    break;
            }
        }

        if (points.Count == 0) return null;
        return (new Point2D(points.Min(p => p.X), points.Min(p => p.Y)), new Point2D(points.Max(p => p.X), points.Max(p => p.Y)));
    }

    /// <summary>
    /// Where each elevation's marker stands in plan: off the middle of each side of the
    /// building, a margin out from it - Revit's elevation markers round a new project.
    /// </summary>
    public static Point2D? MarkerAt(BimDocument document, ElevationSide side, double margin = 3000)
    {
        if (Extents(document) is not var ((minX, minY), (maxX, maxY))) return null;

        var (midX, midY) = ((minX + maxX) / 2, (minY + maxY) / 2);
        return side switch
        {
            ElevationSide.North => new Point2D(midX, maxY + margin),
            ElevationSide.South => new Point2D(midX, minY - margin),
            ElevationSide.East => new Point2D(maxX + margin, midY),
            _ => new Point2D(minX - margin, midY)
        };
    }
}
