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
        // A component standing on its level has a position of its own; one fixed to a wall
        // face is carried by it, like a door.
        Component component => !component.IsHosted,

        // A roof window is set where it is in plan, on its roof: it moves on its own, and with its roof.
        RoofWindow => true,

        // A shaft belongs to nothing: it is where it was put, and cuts whatever is there.
        ShaftOpening => true,

        // A downpipe slides along its gutter to wherever it is moved to.
        Downpipe => true,

        // A roof drain is set where it is on its roof, as a roof window is.
        RoofDrain => true,

        // A chimney stands where it was put, and cuts whatever roof is there.
        Chimney => true,
        IHostedElement => false,
        Wall or PolygonWall or Slab or Room or Column or Grid or SectionMarker or Dimension or Tag or TextNote => true,
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

            // An extruded roof's outline follows from its extrusion, so it is the extrusion that moves.
            case Roof { Extrusion: { } extrusion } extruded:
                extruded.SetExtrusion(extrusion.With(origin: extrusion.Origin + delta));
                break;

            case Slab slab:
                slab.SetBoundary(slab.Boundary.Select(point => point + delta).ToList());
                (slab as Roof)?.TransformSketch(point => point + delta);
                break;

            case Room room:
                room.Location += delta;
                break;

            case PolygonWall shaped:
                shaped.Outline = shaped.Outline.Select(point => point + delta).ToList();
                (shaped.Start, shaped.End) = (shaped.Start + delta, shaped.End + delta);
                break;

            case Component component:
                component.Location += delta;
                break;

            case RoofWindow roofWindow:
                roofWindow.Location += delta;
                break;

            case ShaftOpening shaft:
                shaft.Location += delta;
                break;

            case Downpipe pipe:
                pipe.Location += delta;
                break;

            case RoofDrain drain:
                drain.Location += delta;
                break;

            case Chimney chimney:
                chimney.Location += delta;
                break;

            case Column column:
                column.Location += delta;
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

                // A mirror image turns the other way.
                wall.Bulge = -wall.Bulge;
                wall.Ellipse = wall.Ellipse?.Mirrored();
                wall.Spline = wall.Spline?.Mirrored();
                break;

            // An extruded roof reflected: its line reflects, and since a reflection turns left into
            // right, the side it runs out to is the other one - its start and end change sign.
            case Roof { Extrusion: { } extrusion } extruded:
            {
                var origin = Reflect(extrusion.Origin);
                var direction = Reflect(extrusion.Origin + extrusion.Direction) - origin;
                extruded.SetExtrusion(extrusion.With(origin: origin, direction: direction, start: -extrusion.End, end: -extrusion.Start));
                break;
            }

            case Slab slab:
                // SetBoundary re-winds the outline, so a reflected one comes back the right
                // way round and its area stays positive.
                slab.SetBoundary(slab.Boundary.Select(Reflect).ToList());
                (slab as Roof)?.TransformSketch(Reflect);
                break;

            case Room room:
                room.Location = Reflect(room.Location);
                break;

            case PolygonWall shaped:
                // Reflected, the straight face is on the other hand of the line.
                shaped.Outline = shaped.Outline.Select(Reflect).ToList();
                (shaped.Start, shaped.End) = (Reflect(shaped.Start), Reflect(shaped.End));
                shaped.StraightSide = shaped.StraightSide switch
                {
                    TrapezoidSide.Left => TrapezoidSide.Right,
                    TrapezoidSide.Right => TrapezoidSide.Left,
                    _ => TrapezoidSide.Centre
                };
                break;

            case Column column:
                column.Location = Reflect(column.Location);
                break;

            case RoofWindow roofWindow:
                roofWindow.Location = Reflect(roofWindow.Location);
                break;

            case Downpipe pipe:
                pipe.Location = Reflect(pipe.Location);
                break;

            case RoofDrain drain:
                drain.Location = Reflect(drain.Location);
                break;

            case Chimney chimney:
            {
                var along = Reflect(chimney.Location + new Vector2D(chimney.Along.X, chimney.Along.Y) * 1000);
                chimney.Location = Reflect(chimney.Location);
                var way = along - chimney.Location;
                chimney.Angle = Math.Atan2(way.Y, way.X) * 180 / Math.PI;
                break;
            }

            case ShaftOpening shaft:
            {
                // Its width turned the way the mirror turns it.
                var turn = shaft.Angle * Math.PI / 180;
                var along = Reflect(shaft.Location + new Vector2D(Math.Cos(turn), Math.Sin(turn)) * 1000);
                shaft.Location = Reflect(shaft.Location);
                var way = along - shaft.Location;
                shaft.Angle = Math.Atan2(way.Y, way.X) * 180 / Math.PI;
                break;
            }

            case Component component:
            {
                // A mirrored component stands at the reflected point facing the reflected way:
                // a desk mirrored about a wall has its back to the other side of the room.
                var reflected = ReflectPoint(component.Location + component.Facing * 1000, axis);

                component.Location = Reflect(component.Location);
                component.FaceToward(reflected - component.Location);
                break;
            }

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

    /// <summary>
    /// Turns an element about a point, anticlockwise by an angle in degrees: where it is, and
    /// which way it faces. A wall's curve, profile and layers are held relative to its ends, so
    /// they turn with them; what a wall carries - doors, windows, sweeps - goes with it.
    /// </summary>
    public static void Rotate(Element element, Point2D centre, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        Point2D Turn(Point2D point)
        {
            var (x, y) = (point.X - centre.X, point.Y - centre.Y);
            return new Point2D(centre.X + x * cos - y * sin, centre.Y + x * sin + y * cos);
        }

        Vector2D TurnWay(Vector2D way) => new(way.X * cos - way.Y * sin, way.X * sin + way.Y * cos);

        Transform(element, Turn, TurnWay, degrees, 1);
    }

    /// <summary>
    /// Whether an element can be scaled: what is set out by points in plan - walls, floors,
    /// ceilings and footprint roofs, grids, sections, rooms, annotation - and things standing at
    /// a point, which keep their own size and only move. Not a roof by extrusion, whose section
    /// would have to change shape.
    /// </summary>
    public static bool CanScale(Element element) => CanMove(element) && element is not Roof { Extrusion: not null };

    /// <summary>
    /// Scales an element's setting out about a point: its points move away from it, or toward
    /// it, by the factor. Thicknesses and heights stay what they are - a wall twice as long is
    /// no thicker - and so do the sizes of things standing at a point.
    /// </summary>
    public static void Scale(Element element, Point2D centre, double factor)
    {
        if (!CanScale(element) || factor <= 0) return;

        Point2D Grow(Point2D point) => centre + (point - centre) * factor;
        Transform(element, Grow, way => way, 0, factor);
    }

    /// <summary>
    /// What turning and scaling share: every point mapped, every direction turned, every angle
    /// added to. Neither reflects, so nothing changes hands.
    /// </summary>
    private static void Transform(Element element, Func<Point2D, Point2D> map, Func<Vector2D, Vector2D> turn, double degrees, double factor)
    {
        switch (element)
        {
            case Wall wall:
                wall.Start = map(wall.Start);
                wall.End = map(wall.End);

                // Paint along a wall made longer stays the same part of it.
                if (factor != 1 && wall.FaceRegions.Count > 0)
                    wall.FaceRegions = wall.FaceRegions.Select(region => region with { From = region.From * factor, To = region.To * factor }).ToList();
                break;

            case Roof { Extrusion: { } extrusion } extruded:
                extruded.SetExtrusion(extrusion.With(origin: map(extrusion.Origin), direction: turn(extrusion.Direction)));
                break;

            case Slab slab:
                slab.SetBoundary(slab.Boundary.Select(map).ToList());
                (slab as Roof)?.TransformSketch(map);
                break;

            case Room room:
                room.Location = map(room.Location);
                break;

            case PolygonWall shaped:
                shaped.Outline = shaped.Outline.Select(map).ToList();
                (shaped.Start, shaped.End) = (map(shaped.Start), map(shaped.End));
                break;

            case Column column:
                column.Location = map(column.Location);
                column.Rotation += degrees;
                break;

            case Component component:
                component.Location = map(component.Location);
                component.Rotation += degrees;
                break;

            case RoofWindow roofWindow:
                roofWindow.Location = map(roofWindow.Location);
                break;

            case Downpipe pipe:
                pipe.Location = map(pipe.Location);
                break;

            case RoofDrain drain:
                drain.Location = map(drain.Location);
                break;

            case Chimney chimney:
                chimney.Location = map(chimney.Location);
                chimney.Angle += degrees;
                break;

            case ShaftOpening shaft:
                shaft.Location = map(shaft.Location);
                shaft.Angle += degrees;
                break;

            case Grid grid:
                grid.Start = map(grid.Start);
                grid.End = map(grid.End);
                break;

            case SectionMarker section:
                section.Start = map(section.Start);
                section.End = map(section.End);
                break;

            case Dimension dimension:
                // Its loose ends go where the points they stood at went; ends on a wall or a
                // grid follow what they measure to.
                if (dimension.Start.Anchor == DimensionAnchor.Point) dimension.Start.FallbackPoint = map(dimension.Start.FallbackPoint);
                if (dimension.End.Anchor == DimensionAnchor.Point) dimension.End.FallbackPoint = map(dimension.End.FallbackPoint);
                dimension.Offset *= factor;
                break;

            case Tag tag:
                tag.Position = map(tag.Position);
                break;

            case TextNote note:
                note.Position = map(note.Position);
                if (note.LeaderEnd is { } leader) note.LeaderEnd = map(leader);
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
