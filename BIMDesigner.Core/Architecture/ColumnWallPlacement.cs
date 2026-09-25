using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How a column sits against the walls round it (specification section 3.2).
///
/// Where a column goes relative to a wall is a decision, not an accident of where the cursor
/// was: a pier built into a wall, a column standing against one, and one straddling a wall line
/// are three different pieces of construction that happen to be near the same place. Storing
/// which one it is means the column can be put back where it belongs when the wall moves, and
/// that a drawing says what was meant rather than what was clicked.
/// </summary>
public enum ColumnPlacement
{
    /// <summary>Wherever it was put, related to nothing.</summary>
    Freestanding,

    /// <summary>Standing against a wall's face, wholly outside the wall.</summary>
    AgainstWall,

    /// <summary>Built into the wall, flush with the face it was put on.</summary>
    EmbeddedInWall,

    /// <summary>Straddling the wall's line, standing out of both faces equally.</summary>
    IntersectingWall,

    /// <summary>In the corner two walls make, tucked against both of them.</summary>
    WallCorner
}

/// <summary>
/// Where a column stands for a given placement, and keeping it there when its walls move.
/// </summary>
public static class ColumnWallPlacement
{
    /// <summary>A shift smaller than this is not worth moving a column for.</summary>
    private const double Tolerance = 1;

    /// <summary>
    /// Where the column should stand to sit as its placement says, or null when it already
    /// does - or when there is no wall there to sit against.
    /// </summary>
    public static Point2D? PositionFor(
        BimDocument document, Column column, ColumnType type, ColumnPlacement placement, ColumnJoins.Face face)
    {
        if (placement == ColumnPlacement.Freestanding) return null;

        if (placement == ColumnPlacement.WallCorner) return AtCorner(document, column, type);

        if (ColumnJoins.JoinedWall(document, column) is not { } wall) return NearWall(document, column, type, placement, face);
        if (document.GetWallType(wall) is not { } wallType) return null;

        var (along, _) = wall.Locate(wallType.Structure, column.Location);
        var depth = Depth(wall, wallType, column, type);
        if (depth <= Tolerance) return null;

        var side = face == ColumnJoins.Face.Exterior ? 1.0 : -1.0;

        // How far the column's middle sits from the wall's line for each way of standing.
        var across = placement switch
        {
            ColumnPlacement.AgainstWall => side * (wallType.Width / 2 + depth / 2),
            ColumnPlacement.EmbeddedInWall => side * (wallType.Width / 2 - depth / 2),
            _ => 0
        };

        var wanted = wall.PointAt(wallType.Structure, along, across);
        return wanted.DistanceTo(column.Location) <= Tolerance ? null : wanted;
    }

    /// <summary>
    /// A column standing against a wall it is not yet touching: the nearest wall on its level
    /// within reach is taken as the one meant.
    /// </summary>
    private static Point2D? NearWall(
        BimDocument document, Column column, ColumnType type, ColumnPlacement placement, ColumnJoins.Face face)
    {
        if (placement != ColumnPlacement.AgainstWall) return null;

        var reach = Math.Max(type.Width, type.Depth);

        var nearest = document.Walls
            .Where(wall => wall.LevelId == column.LevelId && document.GetWallType(wall) is not null)
            .Select(wall => (Wall: wall, Type: document.GetWallType(wall)!,
                Distance: Line2D.DistanceFromSegment(column.Location, wall.Start, wall.End)))
            .Where(entry => entry.Distance <= reach + entry.Type.Width)
            .OrderBy(entry => entry.Distance)
            .FirstOrDefault();

        if (nearest.Wall is null) return null;

        var (along, _) = nearest.Wall.Locate(nearest.Type.Structure, column.Location);
        var depth = Depth(nearest.Wall, nearest.Type, column, type);

        // The face asked for, not the side it happens to be on: a column standing against a
        // wall is already outside it, so where it is cannot decide which face it belongs to.
        var side = face == ColumnJoins.Face.Exterior ? 1.0 : -1.0;

        return nearest.Wall.PointAt(nearest.Type.Structure, along, side * (nearest.Type.Width / 2 + depth / 2));
    }

    /// <summary>
    /// The inside corner of the two nearest walls that meet: where their inner faces cross, with
    /// the column tucked into it. Null when there is no corner near enough to have been meant.
    /// </summary>
    private static Point2D? AtCorner(BimDocument document, Column column, ColumnType type)
    {
        var reach = Math.Max(type.Width, type.Depth) * 2;

        var walls = document.Walls
            .Where(wall => wall.LevelId == column.LevelId && document.GetWallType(wall) is not null)
            .Select(wall => (Wall: wall, Type: document.GetWallType(wall)!))
            .Where(entry => Line2D.DistanceFromSegment(column.Location, entry.Wall.Start, entry.Wall.End)
                            <= reach + entry.Type.Width)
            .ToList();

        if (walls.Count < 2) return null;

        Point2D? best = null;
        var closest = double.MaxValue;

        for (var i = 0; i < walls.Count; i++)
        for (var j = i + 1; j < walls.Count; j++)
        {
            var (a, aType) = walls[i];
            var (b, bType) = walls[j];

            // The face of each that the column is on, and where those two faces cross: the
            // inside corner of the room, which is what a corner column is tucked into.
            var aSide = a.Locate(aType.Structure, column.Location).Across >= 0 ? 1 : -1;
            var bSide = b.Locate(bType.Structure, column.Location).Across >= 0 ? 1 : -1;

            var aFace = new Line2D(a.PointAt(aType.Structure, 0, aSide * aType.Width / 2), a.Direction);
            var bFace = new Line2D(b.PointAt(bType.Structure, 0, bSide * bType.Width / 2), b.Direction);

            if (!Line2D.TryIntersect(aFace, bFace, out var corner)) continue;

            // Tucked in: half its size back from the corner along both faces, on the side the
            // column was put.
            var toward = column.Location - corner;
            if (toward.Length <= 1e-6) continue;

            var offset = corner
                         + a.Direction * (Math.Sign(toward.Dot(a.Direction)) * type.Width / 2)
                         + b.Direction * (Math.Sign(toward.Dot(b.Direction)) * type.Depth / 2);

            var distance = offset.DistanceTo(column.Location);
            if (distance >= closest || distance > reach) continue;

            closest = distance;
            best = offset;
        }

        return best is { } found && found.DistanceTo(column.Location) > Tolerance ? found : null;
    }

    /// <summary>How deep the column measures across the wall, which is what has to sit on a face.</summary>
    private static double Depth(Wall wall, WallType wallType, Column column, ColumnType type)
    {
        var located = column.Outline(type).Select(corner => wall.Locate(wallType.Structure, corner)).ToList();
        return located.Count == 0 ? 0 : located.Max(p => p.Across) - located.Min(p => p.Across);
    }
}
