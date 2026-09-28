using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Attaching walls to the slabs over and under them (specification section 3.1, "attach
/// top/base to roofs, floors and ceilings").
///
/// A partition that should reach the ceiling, or a gable wall that should meet the roof, is not
/// "3 m high": it is as high as whatever is above it. Attached, it follows that slab when the
/// slab moves or changes build-up, which a typed height never would.
/// </summary>
public static class WallAttachments
{
    /// <summary>
    /// The slab directly over a wall that its top should meet: the lowest floor, ceiling or
    /// roof whose underside is above the wall's base and whose outline is over the wall.
    /// </summary>
    public static Slab? SlabAbove(BimDocument document, Wall wall)
    {
        var bottom = wall.GetBaseElevation(document);

        return document.Elements.OfType<Slab>()
            .Where(slab => slab.GetBottomElevation(document) > bottom + 1)
            .Where(slab => IsOver(document, slab, wall))
            .OrderBy(slab => slab.GetBottomElevation(document))
            .FirstOrDefault();
    }

    /// <summary>
    /// The floor directly under a wall that its base should stand on: the highest floor whose
    /// upper surface is below the wall's top and whose outline is under the wall.
    /// </summary>
    public static Slab? FloorBelow(BimDocument document, Wall wall)
    {
        var top = wall.GetTopElevation(document);

        var floors = document.Elements.OfType<Floor>()
            .Where(floor => floor.GetTopElevation(document) < top - 1)
            .Where(floor => IsOver(document, floor, wall))
            .Select(floor => (Slab: (Slab)floor, Top: floor.GetTopElevation(document)));

        // A pitched roof the wall rises through and comes out above - a dormer's walls, on the
        // roof they stand on. A wall under a roof, whose top it never comes out of, is not on it.
        var middle = wall.LocationCurve.PointAt(wall.Length / 2);
        var roofs = document.Elements.OfType<Roof>()
            .Where(roof => roof.Contains(middle) && !roof.Surface(document).IsFlat)
            .Select(roof => (Slab: (Slab)roof, Top: roof.TopAt(document, middle)))
            .Where(roof => roof.Top < top - 1 && roof.Top > wall.GetBaseElevation(document) - 1);

        return floors.Concat(roofs)
            .OrderByDescending(candidate => candidate.Top)
            .Select(candidate => candidate.Slab)
            .FirstOrDefault();
    }

    /// <summary>
    /// The wall directly over this one that its top can reach up to: the lowest whose base is
    /// above this one's base, standing in the same vertical plane - straight, parallel and
    /// overlapping along its length, on this wall's line (specification section 3.1, "attach
    /// walls to walls above or below").
    /// </summary>
    public static Wall? WallAbove(BimDocument document, Wall wall)
    {
        var bottom = wall.GetBaseElevation(document);
        return document.Walls
            .Where(other => InSamePlane(document, wall, other) && other.GetBaseElevation(document) > bottom + 1)
            .OrderBy(other => other.GetBaseElevation(document))
            .FirstOrDefault();
    }

    /// <summary>The wall directly under this one that its base can stand on: the highest whose top is below this one's top.</summary>
    public static Wall? WallBelow(BimDocument document, Wall wall)
    {
        var top = wall.GetTopElevation(document);
        return document.Walls
            .Where(other => InSamePlane(document, wall, other) && other.GetTopElevation(document) < top - 1)
            .OrderByDescending(other => other.GetTopElevation(document))
            .FirstOrDefault();
    }

    /// <summary>Whether two walls stand one over the other: straight, parallel, one's line within the other's body, overlapping.</summary>
    private static bool InSamePlane(BimDocument document, Wall wall, Wall other)
    {
        if (ReferenceEquals(wall, other) || wall.IsCurved || other.IsCurved) return false;
        if (document.GetWallType(wall) is not { } type || document.GetWallType(other) is not { } otherType) return false;
        if (Math.Abs(wall.Direction.Cross(other.Direction)) > 1e-3) return false;

        var (_, across) = wall.Locate(type.Structure, other.PointAt(otherType.Structure, other.Length / 2, 0));
        if (Math.Abs(across) > type.Width / 2) return false;

        var a = wall.Locate(type.Structure, other.Start).Along;
        var b = wall.Locate(type.Structure, other.End).Along;
        return Math.Min(wall.Length, Math.Max(a, b)) - Math.Max(0, Math.Min(a, b)) >= WallLamination.MinimumOverlap;
    }

    /// <summary>
    /// Whether a slab lies over or under a wall. Walls usually carry the edges of the slabs
    /// they hold up, so a wall along the outline counts as well as one inside it.
    /// </summary>
    private static bool IsOver(BimDocument document, Slab slab, Wall wall)
    {
        if (slab.Boundary.Count < 3) return false;

        var reach = (document.GetWallType(wall)?.Width ?? 0) / 2 + 1;
        var curve = wall.LocationCurve;

        return new[] { 0.25, 0.5, 0.75 }
            .Select(fraction => curve.PointAt(curve.Length * fraction))
            .Any(point => slab.Contains(point) || DistanceToOutline(slab.Boundary, point) <= reach);
    }

    private static double DistanceToOutline(IReadOnlyList<Point2D> outline, Point2D point) =>
        outline.Select((corner, i) => Line2D.DistanceFromSegment(point, corner, outline[(i + 1) % outline.Count])).Min();
}

/// <summary>
/// Attaching columns, which is the same question a wall's attachment is: a column meets the
/// slab over it rather than standing to a typed height, so it follows that slab when it moves.
/// </summary>
public static class ColumnAttachments
{
    /// <summary>The lowest floor, ceiling or roof over a column whose outline covers it.</summary>
    public static Slab? SlabAbove(BimDocument document, Column column)
    {
        var bottom = column.GetBaseElevation(document);

        return document.Elements.OfType<Slab>()
            .Where(slab => slab.GetBottomElevation(document) > bottom + 1 && slab.Contains(column.Location))
            .OrderBy(slab => slab.GetBottomElevation(document))
            .FirstOrDefault();
    }

    /// <summary>The highest floor under a column that it can stand on.</summary>
    public static Slab? FloorBelow(BimDocument document, Column column)
    {
        var top = column.GetTopElevation(document);

        return document.Elements.OfType<Floor>()
            .Where(floor => floor.GetTopElevation(document) < top - 1 && floor.Contains(column.Location))
            .OrderByDescending(floor => floor.GetTopElevation(document))
            .Cast<Slab>()
            .FirstOrDefault();
    }
}

/// <summary>
/// Attaches or detaches the tops or bases of columns, as one step - carrying the style and
/// offset the attachment was made with, so undoing puts back not only what held the column but
/// how it met it.
/// </summary>
public sealed class AttachColumnsCommand : IUndoableCommand
{
    private readonly record struct State(Guid? Attached, ColumnAttachmentStyle Style, double Offset);

    private readonly List<(Column Column, bool Top, State Old, State New)> _changes;

    public AttachColumnsCommand(
        IEnumerable<(Column Column, bool Top, Guid? SlabId)> changes, string name,
        ColumnAttachmentStyle style = ColumnAttachmentStyle.CutColumn, double offset = 0)
    {
        _changes = changes
            .Select(change => (change.Column, change.Top, Read(change.Column, change.Top),
                new State(change.SlabId, style, offset)))
            .ToList();

        Name = name;
    }

    public string Name { get; }

    public int Count => _changes.Count;

    public void Redo()
    {
        foreach (var (column, top, _, state) in _changes) Write(column, top, state);
    }

    public void Undo()
    {
        foreach (var (column, top, old, _) in _changes) Write(column, top, old);
    }

    private static State Read(Column column, bool top) => top
        ? new State(column.TopAttachedTo, column.TopAttachmentStyle, column.OffsetFromAttachmentAtTop)
        : new State(column.BaseAttachedTo, column.BaseAttachmentStyle, column.OffsetFromAttachmentAtBase);

    private static void Write(Column column, bool top, State state)
    {
        if (top)
        {
            column.TopAttachedTo = state.Attached;
            column.TopAttachmentStyle = state.Style;
            column.OffsetFromAttachmentAtTop = state.Offset;
        }
        else
        {
            column.BaseAttachedTo = state.Attached;
            column.BaseAttachmentStyle = state.Style;
            column.OffsetFromAttachmentAtBase = state.Offset;
        }
    }
}

/// <summary>Attaches or detaches the tops or bases of walls, as one step.</summary>
public sealed class AttachWallsCommand : IUndoableCommand
{
    private readonly List<(Wall Wall, bool Top, Guid? Old, Guid? New)> _changes;

    public AttachWallsCommand(IEnumerable<(Wall Wall, bool Top, Guid? SlabId)> changes, string name)
    {
        _changes = changes
            .Select(change => (change.Wall, change.Top, change.Top ? change.Wall.TopAttachedTo : change.Wall.BaseAttachedTo, change.SlabId))
            .ToList();
        Name = name;
    }

    public string Name { get; }

    public int Count => _changes.Count;

    public void Redo()
    {
        foreach (var (wall, top, _, slab) in _changes) Set(wall, top, slab);
    }

    public void Undo()
    {
        foreach (var (wall, top, old, _) in _changes) Set(wall, top, old);
    }

    private static void Set(Wall wall, bool top, Guid? slab)
    {
        if (top) wall.TopAttachedTo = slab;
        else wall.BaseAttachedTo = slab;
    }
}
