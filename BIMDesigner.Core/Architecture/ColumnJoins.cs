using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How an architectural column reads where it stands in a wall (specification section 3.2).
///
/// A column built into a wall is one piece of construction with it, not an object sitting in
/// front of it, so it is drawn as part of that wall: "when a wall with a coarse-scale fill
/// pattern is joined to an architectural column, the joined column assumes that pattern". A
/// column left with its own hatch in the middle of a wall would read on the drawing as a
/// separate thing standing there, which is exactly what it is not.
/// </summary>
public static class ColumnJoins
{
    /// <summary>
    /// A column's section with the walls it is buried in taken out of it, at a height.
    ///
    /// Where a column and a wall occupy the same place, one of them has to give way, and which
    /// one is decided by which goes right through the other: a column spanning the wall's whole
    /// thickness is a pier, and the wall gives way to it (see <see cref="Crossings"/>); a column
    /// that only reaches part way in is standing against the wall, and the buried part of it is
    /// not there - so it is cut off the column rather than left inside the masonry.
    ///
    /// Two solids in the same place is the one thing a model must not contain: it is wrong in
    /// the 3D view, it is wrong in section, and it is counted twice in every quantity.
    /// </summary>
    public static ColumnProfile CutByWalls(
        BimDocument document, Column column, ColumnType type, ColumnProfile placed, double elevation)
    {
        if (!column.CutByWalls || placed.IsEmpty) return placed;

        var result = placed;

        foreach (var wall in document.Walls)
        {
            if (wall.LevelId != column.LevelId) continue;
            if (document.GetWallType(wall) is not { } wallType || wallType.Width <= 0) continue;

            // Only where the wall actually is: a column carrying on above a low wall is not
            // inside it up there.
            if (elevation < wall.GetBaseElevation(document) - 1 ||
                elevation > wall.GetTopElevation(document) + 1) continue;

            // A column right through the wall is a pier; that one the wall gives way to.
            if (GoesThrough(wall, wallType, column, type)) continue;

            var half = wallType.Width / 2;
            var body = WallJoins.GetBandOutline(document, wall, wallType, half, -half);
            if (body.Length < 3) continue;

            // Nothing near it: no point cutting against a wall on the far side of the building.
            if (!Overlaps(result.Outer, body)) continue;

            var cut = result.Combine(body, BooleanOperation.Difference);
            if (!cut.IsEmpty) result = cut;
        }

        return result;
    }

    /// <summary>Whether a column reaches right through a wall, which makes it a pier rather than an obstruction.</summary>
    private static bool GoesThrough(Wall wall, WallType wallType, Column column, ColumnType type)
    {
        var half = wallType.Width / 2;
        var located = column.Outline(type).Select(corner => wall.Locate(wallType.Structure, corner)).ToList();

        return located.Count > 0 && located.Min(p => p.Across) <= -half + Tolerance
                                 && located.Max(p => p.Across) >= half - Tolerance;
    }

    /// <summary>Whether two outlines are near enough each other to be worth intersecting.</summary>
    private static bool Overlaps(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b) =>
        a.Min(p => p.X) <= b.Max(p => p.X) && a.Max(p => p.X) >= b.Min(p => p.X) &&
        a.Min(p => p.Y) <= b.Max(p => p.Y) && a.Max(p => p.Y) >= b.Min(p => p.Y);

    /// <summary>Which face of a wall a column is set flush with.</summary>
    public enum Face
    {
        Interior,
        Exterior
    }

    /// <summary>
    /// Where a column should stand to sit flush with a face of the wall it is on, rather than
    /// straddling the wall's line.
    ///
    /// A column drawn on a wall's centreline projects out of both faces by whatever it is
    /// wider than the wall, which is not a pier anyone builds: a pier is flush with one face
    /// and steps out of the other, and a column narrower than the wall is buried in it with a
    /// sliver of wall left on each side. So the column is set against the face it was put on -
    /// its back on that face - and everything it is wider than the wall by goes the other way,
    /// into the room.
    ///
    /// Null when it is not on a wall at all, or already flush.
    /// </summary>
    public static Point2D? FlushWith(BimDocument document, Column column, ColumnType type, Face face)
    {
        if (JoinedWall(document, column) is not { } wall) return null;
        if (document.GetWallType(wall) is not { } wallType) return null;

        var (along, across) = wall.Locate(wallType.Structure, column.Location);

        // How deep the column is measured across the wall, which is what has to sit on the face.
        var located = column.Outline(type).Select(corner => wall.Locate(wallType.Structure, corner)).ToList();
        var depth = located.Max(p => p.Across) - located.Min(p => p.Across);
        if (depth <= Tolerance) return null;

        // The chosen face, and the middle of a column whose back is on it.
        var side = face == Face.Exterior ? 1.0 : -1.0;
        var wanted = side * (wallType.Width / 2 - depth / 2);

        if (Math.Abs(wanted - across) <= Tolerance) return null;
        return wall.PointAt(wallType.Structure, along, wanted);
    }

    /// <summary>Which face a column is nearer to, so placing it on one lines it up with that one.</summary>
    public static Face NearestFace(BimDocument document, Column column, Point2D at)
    {
        if (JoinedWall(document, column) is not { } wall || document.GetWallType(wall) is not { } wallType)
            return Face.Interior;

        return wall.Locate(wallType.Structure, at).Across >= 0 ? Face.Exterior : Face.Interior;
    }

    /// <summary>A shift smaller than this is not worth moving a column for.</summary>
    private const double Tolerance = 1;

    /// <summary>
    /// The wall a column is built into: the one whose body covers where the column stands. A
    /// column clear of every wall is joined to nothing and keeps its own fill.
    /// </summary>
    public static Wall? JoinedWall(BimDocument document, Column column) =>
        document.Walls
            .Where(wall => wall.LevelId == column.LevelId)
            .FirstOrDefault(wall =>
                document.GetWallType(wall) is { } type &&
                wall.Locate(type.Structure, column.Location) is var (along, across) &&
                along >= 0 && along <= wall.Length &&
                Math.Abs(across) <= type.Width / 2);

    /// <summary>
    /// Where architectural columns interrupt a wall, with the wall's layers turned round into
    /// each side of the gap - "compound layers in walls wrap at architectural columns".
    ///
    /// A column engaged in a wall is part of that wall's construction, so the wall stops at it
    /// and the column fills the gap. The sides of that gap are reveals like a doorway's, and the
    /// wall's finishes return into them, which is what wrapping means: otherwise the cavity and
    /// the blockwork would be left on show against the side of the column.
    ///
    /// Only a column that goes right through the wall interrupts it. A pilaster shallower than
    /// the wall stands proud of a wall that carries on behind it, and there is nothing to wrap.
    /// </summary>
    public static IReadOnlyList<(double From, double To, WallCut Before, WallCut After)> Crossings(
        BimDocument document, Wall wall, WallType type)
    {
        var crossings = new List<(double, double, WallCut, WallCut)>();
        var half = type.Width / 2;

        foreach (var column in document.Elements.OfType<Column>())
        {
            if (column.LevelId != wall.LevelId) continue;
            if (document.FindType<ColumnType>(column.TypeId) is not { } columnType) continue;

            // Where the column's section falls on the wall: along it, and across it.
            var located = column.Outline(columnType)
                .Select(corner => wall.Locate(type.Structure, corner))
                .ToList();

            if (located.Min(p => p.Across) > -half || located.Max(p => p.Across) < half) continue;

            var from = located.Min(p => p.Along);
            var to = located.Max(p => p.Along);

            // A column at an end of the wall is a join rather than a crossing, and one that
            // covers the whole wall would leave nothing of it to draw.
            if (from <= WallJoins.JoinTolerance || to >= wall.Length - WallJoins.JoinTolerance) continue;
            if (to - from <= WallJoins.JoinTolerance) continue;

            crossings.Add((from, to,
                WallSlices.JambAt(wall, type, from),
                WallSlices.JambAt(wall, type, to)));
        }

        return crossings.OrderBy(crossing => crossing.Item1).ToList();
    }

    /// <summary>
    /// What fills the column in plan at coarse detail: the fill of the wall it is joined to, or
    /// its own type's where it stands free.
    /// </summary>
    public static ColourRgb CoarseFill(BimDocument document, Column column, ColumnType type) =>
        JoinedWall(document, column) is { } wall && document.GetWallType(wall) is { } wallType
            ? wallType.CoarseScaleFillColour
            : type.CoarseScaleFillColour;

    /// <summary>
    /// What the column is made of where it is drawn cut: the material of the wall it is joined
    /// to - its structural core, being the body of the wall - or its own.
    /// </summary>
    public static Guid CutMaterial(BimDocument document, Column column, ColumnType type)
    {
        if (JoinedWall(document, column) is not { } wall || document.GetWallType(wall) is not { } wallType)
            return type.MaterialId;

        var core = wallType.Structure.Layers.FirstOrDefault(layer => layer.Function == LayerFunction.Structure);
        return core is null ? type.MaterialId : core.MaterialId;
    }
}
