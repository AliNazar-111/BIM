using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What Pick New will take as a component's new host, as Revit's Placement panel offers it.
/// </summary>
public enum ComponentPlacementMode
{
    /// <summary>
    /// Only a face that stands up: the side of a wall. It is what a wall light or a radiator
    /// wants, and refusing everything else is what stops one being dropped on the floor.
    /// </summary>
    VerticalFace,

    /// <summary>Any face, whichever way it looks: a wall, or a floor, ceiling or roof.</summary>
    Face,

    /// <summary>
    /// The work plane of the view rather than a face - here, the level the plan is cut on. The
    /// component stands on it wherever it is put.
    /// </summary>
    WorkPlane
}

/// <summary>What is actually carrying a component.</summary>
public enum ComponentHostKind
{
    /// <summary>Nothing: it stands on the work plane of its level.</summary>
    Level,

    /// <summary>The vertical face of a wall.</summary>
    WallFace,

    /// <summary>The horizontal face of a floor, ceiling or roof.</summary>
    SlabFace
}

/// <summary>
/// Which faces carry a component, and what moving it to another one does to it (specification
/// section 2.5, and Revit's "Move a Work Plane-Based or Face-Based Element to a Different
/// Host").
///
/// The rules live here rather than in the editor because they are model rules, not drawing
/// ones: what may host what, and where a component ends up when it does, has to be the same
/// answer for a click in the plan, a click in 3D, and a file being opened.
/// </summary>
public static class ComponentHosting
{
    /// <summary>The element carrying a component, or null when it stands on its level.</summary>
    public static Element? HostOf(BimDocument document, Component component) =>
        component.HostId == Guid.Empty
            ? null
            : document.Elements.FirstOrDefault(element => element.Id == component.HostId
                                                          && element is Wall or Slab);

    /// <summary>What kind of face is carrying it.</summary>
    public static ComponentHostKind KindOf(BimDocument document, Component component) => HostOf(document, component) switch
    {
        Wall => ComponentHostKind.WallFace,
        Slab => ComponentHostKind.SlabFace,
        _ => ComponentHostKind.Level
    };

    /// <summary>The host as Properties names it: "Wall : Interior - Partition", "Ceiling", "Level".</summary>
    public static string Describe(BimDocument document, Component component, ComponentType? type) =>
        HostOf(document, component) switch
        {
            Wall wall => $"Wall : {document.GetWallType(wall)?.Name ?? "?"}",
            Slab slab => component.FlipFacing ? $"{slab.Category} (soffit)" : $"{slab.Category}",
            _ => type?.Placement == ComponentPlacement.Freestanding ? "<none>" : "Level"
        };

    /// <summary>
    /// The height the component's own base sits at: its level, the height up the wall carrying
    /// it, or the face of the slab it is on - hanging below the soffit when it is under one, as
    /// a ceiling light hangs.
    /// </summary>
    public static double BaseElevation(BimDocument document, Component component, ComponentType type)
    {
        if (HostOf(document, component) is Slab slab)
        {
            return component.FlipFacing
                ? slab.GetBottomElevation(document) - component.Elevation - type.Height
                : slab.GetTopElevation(document) + component.Elevation;
        }

        var level = document.Levels.FirstOrDefault(l => l.Id == component.LevelId);
        return (level?.Elevation ?? 0) + component.Elevation;
    }

    /// <summary>Whether a mode can host this family at all: a work plane cannot carry a face-based one.</summary>
    public static bool Allows(ComponentType type, ComponentPlacementMode mode) => mode switch
    {
        ComponentPlacementMode.WorkPlane => type.Placement != ComponentPlacement.FaceBased,
        _ => type.IsHosted
    };

    /// <summary>
    /// The component as it would stand if it were moved onto whatever is under a point, as far
    /// as the mode allows - or null when nothing there can carry it.
    ///
    /// The move is worked out on a copy so the caller can hold both states: a command that can
    /// be undone needs where the thing was as much as where it is going.
    /// </summary>
    public static Component? MovedTo(
        BimDocument document, Component component, ComponentType type,
        ComponentPlacementMode mode, Point2D at, Guid levelId)
    {
        var moved = Copy(component);

        if (mode == ComponentPlacementMode.WorkPlane)
        {
            if (type.Placement == ComponentPlacement.FaceBased) return null;

            moved.HostId = Guid.Empty;
            moved.LevelId = levelId;
            moved.Location = at;
            moved.Elevation = 0;
            return moved;
        }

        if (!type.IsHosted) return null;

        // A wall first: where a wall stands on a slab, the wall is the face being pointed at.
        if (WallAt(document, at, levelId) is { } wall && moved.HostOn(document, wall, at))
        {
            moved.Elevation = component.Elevation > 0 ? component.Elevation : type.DefaultElevation;
            return moved;
        }

        if (mode == ComponentPlacementMode.VerticalFace) return null;

        // Then a horizontal face. The topmost slab under the cursor is the one being pointed
        // at, as a ceiling is what you see when you look up at the room you are standing in.
        if (SlabAt(document, at, levelId) is not { } slab) return null;

        // On a ceiling, a component hangs under it; on a floor or roof it stands on top.
        moved.HostOn(document, slab, at, underneath: slab is Ceiling);
        moved.Elevation = 0;
        return moved;
    }

    /// <summary>The wall whose body covers a point on this level, if there is one.</summary>
    public static Wall? WallAt(BimDocument document, Point2D at, Guid levelId) =>
        document.Walls
            .Where(wall => wall.LevelId == levelId)
            .FirstOrDefault(wall =>
                document.GetWallType(wall) is { } type &&
                wall.Locate(type.Structure, at) is var (along, across) &&
                along >= 0 && along <= wall.Length &&
                Math.Abs(across) <= type.Width / 2);

    /// <summary>
    /// The slab covering a point on this level: a ceiling first, then a roof, then a floor.
    ///
    /// A plan is one point on the page and a room usually has a floor and a ceiling over each
    /// other, so something has to decide. The face overhead wins, because that is the one a
    /// component is fixed <em>to</em>; a floor is what things stand <em>on</em>, and standing
    /// one on the level already does that - Work Plane, or simply placing it there.
    /// </summary>
    public static Slab? SlabAt(BimDocument document, Point2D at, Guid levelId) =>
        document.Elements.OfType<Slab>()
            .Where(slab => slab.LevelId == levelId && slab.Contains(at))
            .OrderByDescending(slab => slab is Ceiling ? 2 : slab is Roof ? 1 : 0)
            .FirstOrDefault();

    /// <summary>A stand-in with the same placement, for working a move out without committing it.</summary>
    public static Component Copy(Component component) => new()
    {
        TypeId = component.TypeId,
        TypeKind = component.TypeKind,
        LevelId = component.LevelId,
        Location = component.Location,
        Rotation = component.Rotation,
        Elevation = component.Elevation,
        HostId = component.HostId,
        FlipFacing = component.FlipFacing
    };
}
