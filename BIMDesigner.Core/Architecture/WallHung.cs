using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A component hung on a wall - a boiler, a wall cupboard, a basin - slid along the wall it hangs on, as a door is: dragged, it goes where
/// the drag takes it along the wall, staying against the same face, never off the wall's ends; and back with Undo.
/// </summary>
public static class WallHung
{
    /// <summary>Slides a wall-hung component from where it was by so much, along its wall and on the same face. False where it hangs on no wall.</summary>
    public static bool Slide(BimDocument document, Component component, Point2D from, Vector2D by)
    {
        if (!component.IsHosted || document.Find<Wall>(component.HostId) is not { } wall || document.GetWallType(wall) is not { } type) return false;
        var (_, across) = wall.Locate(type.Structure, from);
        var (along, _) = wall.Locate(type.Structure, from + by);
        return component.HostOn(document, wall, wall.PointAt(type.Structure, Math.Clamp(along, 0, wall.Length), across));
    }
}

/// <summary>Wall-hung components slid along their walls, as one step to undo: each, where it was and how it was turned, and where it went.</summary>
public sealed class SlideOnWallCommand : IUndoableCommand
{
    private readonly IReadOnlyList<(Component Component, Point2D From, double FromRotation, Point2D To, double ToRotation)> _slides;

    public SlideOnWallCommand(IEnumerable<(Component Component, Point2D From, double FromRotation, Point2D To, double ToRotation)> slides) => _slides = slides.ToList();

    public string Name => _slides.Count == 1 ? "Slide Along Wall" : "Slide Along Walls";

    public void Redo()
    {
        foreach (var (component, _, _, to, rotation) in _slides) (component.Location, component.Rotation) = (to, rotation);
    }

    public void Undo()
    {
        foreach (var (component, from, rotation, _, _) in _slides) (component.Location, component.Rotation) = (from, rotation);
    }
}
