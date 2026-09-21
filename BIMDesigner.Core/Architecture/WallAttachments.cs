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

        return document.Elements.OfType<Floor>()
            .Where(floor => floor.GetTopElevation(document) < top - 1)
            .Where(floor => IsOver(document, floor, wall))
            .OrderByDescending(floor => floor.GetTopElevation(document))
            .Cast<Slab>()
            .FirstOrDefault();
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
