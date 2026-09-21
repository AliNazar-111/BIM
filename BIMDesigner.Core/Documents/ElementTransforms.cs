using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Documents;

/// <summary>
/// Moving and mirroring elements (specification section 2.4).
///
/// Every element that has a position in the project knows how to be moved; the ones that do
/// not have one are left alone rather than silently ignored. A door is the interesting case:
/// it is positioned along its host wall, so it moves when the wall does and must <em>not</em>
/// be moved again on its own - doing both would slide every door along its wall by the
/// distance the wall travelled.
/// </summary>
public static class ElementTransforms
{
    /// <summary>
    /// Whether this element has a position of its own to move.
    ///
    /// False for hosted elements, which follow their host, and for sheets, which are not in
    /// the building at all.
    /// </summary>
    public static bool CanMove(Element element) => element switch
    {
        IHostedElement => false,
        Wall or Slab or Room or Grid or SectionMarker or Dimension or Tag or TextNote => true,
        _ => false
    };

    public static void Move(Element element, Vector2D delta)
    {
        switch (element)
        {
            case Wall wall:
                wall.Start += delta;
                wall.End += delta;
                break;

            case Slab slab:
                slab.SetBoundary(slab.Boundary.Select(point => point + delta).ToList());
                break;

            case Room room:
                room.Location += delta;
                break;

            case Grid grid:
                grid.Start += delta;
                grid.End += delta;
                break;

            case SectionMarker section:
                section.Start += delta;
                section.End += delta;
                break;

            case Dimension dimension:
                // Only the ends that are loose points move. An end referring to a wall is
                // already following that wall, and moving it as well would double the shift.
                MoveReference(dimension.Start, delta);
                MoveReference(dimension.End, delta);
                break;

            case Tag tag:
                tag.Position += delta;
                break;

            case TextNote note:
                note.Position += delta;
                if (note.LeaderEnd is { } leader) note.LeaderEnd = leader + delta;
                break;
        }
    }

    /// <summary>
    /// Reflects an element about a line.
    ///
    /// Reflection reverses handedness, which matters for anything with a front and a back: a
    /// mirrored wall's exterior face has to stay on the same physical side of it, and a
    /// mirrored section has to go on looking at the same half of the building. Both are fixed
    /// by flipping the stored side, because the perpendicular they are measured from turns
    /// over when the geometry does.
    /// </summary>
    public static void Mirror(Element element, Line2D axis)
    {
        Point2D Reflect(Point2D point) => ReflectPoint(point, axis);

        switch (element)
        {
            case Wall wall:
                wall.Start = Reflect(wall.Start);
                wall.End = Reflect(wall.End);
                wall.Flipped = !wall.Flipped;
                break;

            case Slab slab:
                // SetBoundary re-winds the outline, so a reflected one comes back the right
                // way round and its area stays positive.
                slab.SetBoundary(slab.Boundary.Select(Reflect).ToList());
                break;

            case Room room:
                room.Location = Reflect(room.Location);
                break;

            case Grid grid:
                grid.Start = Reflect(grid.Start);
                grid.End = Reflect(grid.End);
                break;

            case SectionMarker section:
                section.Start = Reflect(section.Start);
                section.End = Reflect(section.End);
                section.Flipped = !section.Flipped;
                break;

            case Dimension dimension:
                dimension.Start.FallbackPoint = ReflectPoint(dimension.Start.FallbackPoint, axis);
                dimension.End.FallbackPoint = ReflectPoint(dimension.End.FallbackPoint, axis);

                // The offset is measured along a perpendicular that has just turned over.
                dimension.Offset = -dimension.Offset;
                break;

            case Tag tag:
                tag.Position = Reflect(tag.Position);
                break;

            case TextNote note:
                note.Position = Reflect(note.Position);
                if (note.LeaderEnd is { } leader) note.LeaderEnd = Reflect(leader);
                break;
        }
    }

    /// <summary>Reflects a point about a line: twice the distance to it, the other way.</summary>
    public static Point2D ReflectPoint(Point2D point, Line2D axis)
    {
        var onAxis = axis.ClosestPointTo(point);
        return onAxis + (onAxis - point);
    }

    private static void MoveReference(DimensionReference reference, Vector2D delta)
    {
        if (reference.Anchor == DimensionAnchor.Point) reference.FallbackPoint += delta;
    }
}
