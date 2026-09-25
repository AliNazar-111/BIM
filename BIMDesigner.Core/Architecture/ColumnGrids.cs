using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Columns and the grid they are set out on (specification section 3.2).
///
/// A frame is drawn as a grid first and columns second: the grid is the setting out, and a
/// column at an intersection of it is there because the grid says so. So columns are placed at
/// intersections rather than pointed at one by one, and they follow the grid when it is moved -
/// adjusting a bay should move the columns in it, not leave them behind for someone to notice
/// later. A column can be let off that, which is what someone does when a column has been moved
/// off the grid on purpose.
/// </summary>
public static class ColumnGrids
{
    /// <summary>How far outside a grid's drawn extent an intersection may still count.</summary>
    private const double Reach = 1000;

    /// <summary>
    /// Every point where these grids cross each other. Parallel ones never meet, and a crossing
    /// far off the end of either grid is not an intersection anyone set out to.
    /// </summary>
    public static IReadOnlyList<Point2D> Intersections(IEnumerable<Grid> grids)
    {
        var lines = grids.Where(grid => grid.Length > 1e-6).ToList();
        var points = new List<Point2D>();

        for (var i = 0; i < lines.Count; i++)
        for (var j = i + 1; j < lines.Count; j++)
        {
            if (!Line2D.TryIntersect(lines[i].Line, lines[j].Line, out var point)) continue;
            if (!Reaches(lines[i], point) || !Reaches(lines[j], point)) continue;

            // Two grids crossing at the same place as a third give one column, not three.
            if (points.Any(seen => seen.DistanceTo(point) < 1)) continue;

            points.Add(point);
        }

        return points;
    }

    /// <summary>Whether a point is on a grid, or near enough past its end to have been meant.</summary>
    private static bool Reaches(Grid grid, Point2D point)
    {
        var along = (point - grid.Start).Dot(grid.Direction);
        return along >= -Reach && along <= grid.Length + Reach;
    }

    /// <summary>
    /// A column of this type standing at each intersection of the grids, ready to be added. The
    /// intersections already holding a column of any type are left alone, so running it twice
    /// does not stack two columns on one gridline crossing.
    /// </summary>
    public static IReadOnlyList<Column> AtIntersections(
        BimDocument document, IEnumerable<Grid> grids, ColumnType type, Guid levelId, Guid? topLevelId = null)
    {
        var taken = document.Elements.OfType<Column>()
            .Where(column => column.LevelId == levelId)
            .Select(column => column.Location)
            .ToList();

        return Intersections(grids)
            .Where(point => taken.All(at => at.DistanceTo(point) > Math.Max(type.Width, type.Depth) / 2))
            .Select(point => new Column
            {
                TypeId = type.Id,
                LevelId = levelId,
                TopLevelId = topLevelId,
                Location = point
            })
            .ToList();
    }

    /// <summary>Whether a grid runs through a column's section, which is what puts it on that grid.</summary>
    public static bool IsOn(Grid grid, Column column, ColumnType type) =>
        grid.Length > 1e-6 &&
        Line2D.DistanceFromSegment(column.Location, grid.Start, grid.End) <= Math.Max(type.Width, type.Depth) / 2;

    /// <summary>
    /// The columns that should come along when these elements are moved: the ones standing on a
    /// grid in the set that have not been told to stay put. Anything already being moved is left
    /// out, so nothing is moved twice.
    /// </summary>
    public static IReadOnlyList<Column> Following(BimDocument document, IReadOnlyList<Element> moving)
    {
        var grids = moving.OfType<Grid>().ToList();
        if (grids.Count == 0) return Array.Empty<Column>();

        var already = moving.Select(element => element.Id).ToHashSet();

        return document.Elements.OfType<Column>()
            .Where(column => column.MovesWithGrids && !already.Contains(column.Id))
            .Where(column => document.FindType<ColumnType>(column.TypeId) is { } type &&
                             grids.Any(grid => IsOn(grid, column, type)))
            .ToList();
    }
}
