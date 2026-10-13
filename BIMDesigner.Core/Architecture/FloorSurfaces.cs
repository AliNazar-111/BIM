using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What stands on a floor stands on its top. Where a storey's floor - or the flat roof over the
/// storey below, that is its floor - is built up above its level, the storey starts over the
/// slab, not inside it, and keeps its own height.
///
/// The floor between two storeys, laid on the walls of the one below, carries the level of the
/// one above: the level is the slab's top, so everything on that storey - its walls, the roof
/// over them, its furniture - rises with it. A floor built up on its own level instead has what
/// stands on it tied to it: a wall's base attached, furniture standing on it. Either way they
/// follow the slab however thick it is made, whatever type it is changed to, wherever it goes.
/// </summary>
public static class FloorSurfaces
{
    /// <summary>
    /// The levels a floor or flat roof is the floor of: at the height of its underside, above
    /// its own level, with the storey below's walls under it - most of their extent covered - so
    /// it is laid on them. Not one standing on a floor already.
    /// </summary>
    public static IReadOnlyList<Level> StoreysOn(BimDocument document, Slab slab)
    {
        if (!IsStoodOn(document, slab) || slab.Boundary.Count < 3) return Array.Empty<Level>();

        var bottom = slab.GetBottomElevation(document);
        var own = document.FindLevel(slab.LevelId)?.Elevation ?? 0;
        var levels = document.Levels
            .Where(level => level.StandsOn is null && level.Id != slab.LevelId && level.Elevation > own + 1 && Math.Abs(level.Elevation - bottom) < 1)
            .ToList();
        if (levels.Count == 0) return levels;

        // Laid on the storey below: the walls whose tops it rests on, their extent mostly under it.
        var below = document.Walls.Where(wall => wall.TopAttachedTo == slab.Id || Math.Abs(wall.GetTopElevation(document) - bottom) < 1).ToList();
        if (below.Count == 0) return Array.Empty<Level>();

        var ends = below.SelectMany(wall => new[] { wall.Start, wall.End }).ToList();
        var extent = (ends.Max(p => p.X) - ends.Min(p => p.X)) * (ends.Max(p => p.Y) - ends.Min(p => p.Y));
        return Math.Abs(Polygon2D.SignedArea(slab.Boundary)) >= 0.5 * extent ? levels : Array.Empty<Level>();
    }

    /// <summary>
    /// The storey a wall is on, with the floor it would stand on, where that floor is the one
    /// between it and the storey below - for Attach Base to set the storey on it; null otherwise.
    /// </summary>
    public static (Level Level, Slab Floor)? StoreyFloor(BimDocument document, Wall wall)
    {
        if (document.FindLevel(wall.LevelId) is not { StandsOn: null } level) return null;
        var floor = wall.BaseAttachedTo is { } id && document.FindElement(id) is Slab attached
            ? attached
            : Burying(document, OwnFoot(document, wall), wall.LocationCurve.PointAt(wall.Length / 2));
        return floor is not null && StoreysOn(document, floor).Contains(level) ? (level, floor) : null;
    }

    /// <summary>
    /// Levels set on the top of a floor, as one step - with any wall on them that stood on the
    /// wall under it let go, so it stands on the floor rather than inside it from the wall below.
    /// </summary>
    public static IUndoableCommand SetOn(BimDocument document, IReadOnlyList<Level> levels, Slab floor)
    {
        var changes = levels.Select(level => ((Action)(() => level.StandsOn = floor.Id), (Action)(() => level.StandsOn = null))).ToList();

        var ids = levels.Select(level => level.Id).ToHashSet();
        var bottom = floor.GetBottomElevation(document);
        foreach (var wall in document.Walls.Where(wall => ids.Contains(wall.LevelId)))
        {
            if (wall.BaseAttachedTo is not { } id || document.FindElement(id) is not Wall below || below.GetTopElevation(document) > bottom + 1) continue;
            changes.Add((() => wall.BaseAttachedTo = null, () => wall.BaseAttachedTo = id));
        }

        return new StandOnSlabCommand(changes);
    }
    /// <summary>Whether a slab is one things stand on: a floor, or a flat roof - a terrace, or the floor of the storey over it.</summary>
    public static bool IsStoodOn(BimDocument document, Slab slab) =>
        slab is Floor || slab is Roof { IsExtrusion: false } roof && roof.Boundary.Count >= 3 && roof.Surface(document).IsFlat;

    /// <summary>A slab's top at a point: a flat roof's with its falls, a floor's level top.</summary>
    public static double TopAt(BimDocument document, Slab slab, Point2D at) =>
        slab is Roof roof ? roof.TopAt(document, at) : slab.GetTopElevation(document);

    /// <summary>
    /// The floor or flat roof something with its foot at a height, at a point in plan, would be
    /// buried in: over the point, its underside at or below the foot and its top above it -
    /// however thick it is. The highest, where there are more; only the one slab, where one is given.
    /// </summary>
    public static Slab? Burying(BimDocument document, double foot, Point2D at, Slab? only = null)
    {
        var slabs = only is not null ? new[] { only } : document.Elements.OfType<Slab>();
        return slabs
            .Where(slab => slab.Boundary.Count >= 3 && slab.Contains(at) && IsStoodOn(document, slab))
            .Select(slab => (Slab: slab, Top: TopAt(document, slab, at)))
            .Where(candidate => candidate.Top > foot + 1 && candidate.Slab.GetBottomElevation(document) <= foot + 1)
            .OrderByDescending(candidate => candidate.Top)
            .Select(candidate => candidate.Slab)
            .FirstOrDefault();
    }

    /// <summary>The floor something put on a level at a point stands on, where that floor is built up above the level; null where the level is the floor.</summary>
    public static Slab? FloorUnder(BimDocument document, Guid levelId, Point2D at) =>
        Burying(document, document.FindLevel(levelId)?.Elevation ?? 0, at);

    private static Point2D Middle(Wall wall) => wall.LocationCurve.PointAt(wall.Length / 2);

    private static Point2D Middle(IReadOnlyList<Point2D> outline) =>
        new(outline.Average(point => point.X), outline.Average(point => point.Y));

    /// <summary>Where a wall's foot would be with nothing under it to stand on: its level, and its base offset.</summary>
    public static double OwnFoot(BimDocument document, Wall wall) => (document.FindLevel(wall.LevelId)?.Elevation ?? 0) + wall.BaseOffset;

    /// <summary>
    /// The changes that set something on the top of a slab it would be buried in - a wall's or
    /// column's base attached to it, keeping its own height; furniture stood on it - as pairs of
    /// what to do and how to undo it. None where it is not buried, or already stands on something.
    /// </summary>
    private static IEnumerable<(Action Redo, Action Undo)> Stand(BimDocument document, Element element, Slab? only)
    {
        switch (element)
        {
            case Wall { BaseAttachedTo: null } wall when Burying(document, wall.GetBaseElevation(document), Middle(wall), only) is { } slab:
                yield return (() => wall.BaseAttachedTo = slab.Id, () => wall.BaseAttachedTo = null);
                break;

            case Column { BaseAttachedTo: null } column when Burying(document, column.GetBaseElevation(document), column.Location, only) is { } slab:
                yield return (() => column.BaseAttachedTo = slab.Id, () => column.BaseAttachedTo = null);
                break;

            case PolygonWall { BaseAttachedTo: null } shaped when Burying(document, shaped.GetBaseElevation(document), Middle(shaped.Outline), only) is { } slab:
                yield return (() => shaped.BaseAttachedTo = slab.Id, () => shaped.BaseAttachedTo = null);
                break;

            case Component { StandsOn: null } component when document.FindType<ComponentType>(component.TypeId) is { } type &&
                                                            ComponentHosting.HostOf(document, component) is not Slab:
            {
                var foot = ComponentHosting.BaseElevation(document, component, type);
                if (Burying(document, foot, component.Location, only) is not { } slab) break;

                // Its elevation is from the floor's top from now on: a chair at nothing, on it.
                yield return (() => component.StandsOn = slab.Id, () => component.StandsOn = null);
                break;
            }
        }
    }

    /// <summary>
    /// Attach Base for furniture: each freestanding piece put down on the top of the floor under
    /// it - a floor or flat roof built up above its level - its underside on it and following it
    /// from then on; null where none has such a floor under it.
    /// </summary>
    public static IUndoableCommand? PutOnFloors(BimDocument document, IEnumerable<Component> components)
    {
        var changes = new List<(Action Redo, Action Undo)>();
        foreach (var component in components.Where(component => ComponentHosting.HostOf(document, component) is null))
        {
            if (FloorUnder(document, component.LevelId, component.Location) is not { } floor) continue;

            var (standsOn, elevation) = (component.StandsOn, component.Elevation);
            changes.Add((() => (component.StandsOn, component.Elevation) = (floor.Id, 0), () => (component.StandsOn, component.Elevation) = (standsOn, elevation)));
        }

        return changes.Count == 0 ? null : new StandOnSlabCommand(changes);
    }

    /// <summary>Sets something new - not yet in the project - on top of the floor it would be buried in.</summary>
    public static void StandNew(BimDocument document, Element element)
    {
        foreach (var (redo, _) in Stand(document, element, null).ToList()) redo();
    }

    /// <summary>
    /// What a new floor or flat roof would bury - walls, columns and furniture already on the
    /// storey it is the floor of - set on its top, as a step to go with it; null when it buries
    /// nothing.
    /// </summary>
    public static IUndoableCommand? LiftOnto(BimDocument document, Slab slab, IReadOnlyCollection<Guid>? risen = null)
    {
        if (!IsStoodOn(document, slab)) return null;

        var changes = document.Elements.Where(element => element is Wall or Column or PolygonWall or Component)
            .Where(element => risen is null || !risen.Contains(element.LevelId))
            .SelectMany(element => Stand(document, element, slab))
            .ToList();
        return changes.Count == 0 ? null : new StandOnSlabCommand(changes);
    }

    /// <summary>
    /// A command adding things to a storey, with what it adds stood on the floor - and, for a
    /// floor or flat roof, what it would bury lifted onto it - as one step.
    /// </summary>
    public static IUndoableCommand Standing(BimDocument document, IUndoableCommand command)
    {
        IReadOnlyList<Element> added = command switch
        {
            AddElementCommand one => new[] { one.Element },
            AddElementsCommand many => many.Elements,
            _ => Array.Empty<Element>()
        };
        if (added.Count == 0) return command;

        var existing = document.Elements.Select(element => element.Id).ToHashSet();
        foreach (var element in added.Where(element => !existing.Contains(element.Id))) StandNew(document, element);

        // A floor laid on the storey below carries the storey above: its level goes on the
        // slab's top, and all on it with it. What else it would bury is stood on it.
        var steps = new List<IUndoableCommand>();
        foreach (var slab in added.OfType<Slab>().Where(slab => !existing.Contains(slab.Id)))
        {
            var storeys = StoreysOn(document, slab);
            if (storeys.Count > 0) steps.Add(SetOn(document, storeys, slab));
            if (LiftOnto(document, slab, storeys.Select(level => level.Id).ToHashSet()) is { } lift) steps.Add(lift);
        }

        return steps.Count == 0 ? command : new CompositeCommand(command.Name, new[] { command }.Concat(steps));
    }
}

/// <summary>Things set on the top of a floor or flat roof they were buried in, as one step.</summary>
public sealed class StandOnSlabCommand : IUndoableCommand
{
    private readonly IReadOnlyList<(Action Redo, Action Undo)> _changes;

    public StandOnSlabCommand(IReadOnlyList<(Action Redo, Action Undo)> changes) => _changes = changes;

    public string Name => "Stand on Floor";

    public void Redo()
    {
        foreach (var (redo, _) in _changes) redo();
    }

    public void Undo()
    {
        foreach (var (_, undo) in _changes.Reverse()) undo();
    }
}
