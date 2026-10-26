using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
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

        // A stair stands where it was put, and cuts the floor over it wherever that is; a ramp the same.
        Stair => true,
        Ramp => true,

        // A railing runs where it was drawn, on whatever floor is under it; a beam spans where it was drawn; a room separator divides where it was drawn.
        Railing => true,
        Beam => true,
        RoomSeparator => true,
        Mep.Pipe => true,
        Mep.Duct => true,

        // The ground, with its spot levels; a region of it, and a joist layout, with their outlines.
        Toposurface or GroundRegion or BeamSystem => true,
        SitePad or PropertyLine or SpotElevation => true,
        Mass => true,
        Walkthrough => true,
        InteriorElevationMarker => true,
        Callout { Parent.Kind: ViewKind.FloorPlan } => true,
        PadFooting { ColumnId: null } => true,
        IHostedElement => false,
        Wall or PolygonWall or Slab or Room or Column or Grid or SectionMarker or Dimension or Tag or TextNote => true,
        AngularDimension or RadialDimension => true,
        RevisionCloud or KeynoteTag => true,
        _ => false
    };

    /// <summary>
    /// The points an element is set out by in plan - its ends, its corners, its path, where it
    /// stands - in order, so that following them traces its lines. Found by putting a copy of it
    /// through the transform every move, turn and scale uses, so whatever those move is found.
    /// Empty for what is carried by something else.
    /// </summary>
    public static IReadOnlyList<Point2D> PlanPoints(Element element)
    {
        if (ElementCopy.Clone(element) is not { } copy) return Array.Empty<Point2D>();

        var points = new List<Point2D>();
        Transform(copy, point =>
        {
            points.Add(point);
            return point;
        }, direction => direction, 0, 1);
        return points;
    }

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
                slab.SetHoles(slab.Holes.Select(hole => hole.Select(point => point + delta)).ToList());
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

            case SlabEdge slabEdge:
                SlabEdgeRuns.Transform(slabEdge, point => point + delta);
                break;

            case RoofDrain drain:
                drain.Location += delta;
                break;

            case Chimney chimney:
                chimney.Location += delta;
                break;

            case Stair stair:
                stair.Start += delta;
                break;

            case Ramp ramp:
                ramp.Start += delta;
                break;

            case Railing railing:
                railing.SetPath(railing.Path.Select(point => point + delta).ToList());
                break;

            case Beam beam:
                (beam.Start, beam.End) = (beam.Start + delta, beam.End + delta);
                break;

            case RoomSeparator separator:
                (separator.Start, separator.End) = (separator.Start + delta, separator.End + delta);
                break;

            case Mep.Pipe pipe:
                (pipe.Start, pipe.End) = (pipe.Start + delta, pipe.End + delta);
                break;

            case Mep.Duct duct:
                (duct.Start, duct.End) = (duct.Start + delta, duct.End + delta);
                break;

            case Toposurface ground:
                ground.SetPoints(ground.Points.Select(point => new Point3D(point.X + delta.X, point.Y + delta.Y, point.Z)).ToList());
                ground.SetExistingPoints(ground.ExistingPoints.Select(point => new Point3D(point.X + delta.X, point.Y + delta.Y, point.Z)).ToList());
                break;

            case GroundRegion region:
                region.SetBoundary(region.Boundary.Select(point => point + delta).ToList());
                region.TransformPath(point => point + delta);
                break;

            case SitePad pad:
                pad.SetBoundary(pad.Boundary.Select(point => point + delta).ToList());
                break;

            case Mass mass:
                mass.SetBoundary(mass.Boundary.Select(point => point + delta).ToList());
                mass.TransformCircle(point => point + delta);
                break;

            case Walkthrough walk:
                walk.SetPath(walk.Path.Select(point => point + delta).ToList());
                break;

            case InteriorElevationMarker marker:
                marker.Location += delta;
                break;

            case Callout callout:
                callout.Region = new BoundingBox2D(callout.Region.MinX + delta.X, callout.Region.MinY + delta.Y, callout.Region.MaxX + delta.X, callout.Region.MaxY + delta.Y);
                break;

            case PropertyLine line:
                line.SetBoundary(line.Boundary.Select(point => point + delta).ToList());
                break;

            case SpotElevation spot:
                spot.Location += delta;
                break;

            case BeamSystem system:
                system.SetBoundary(system.Boundary.Select(point => point + delta).ToList());
                break;

            case Column column:
                column.Location += delta;
                break;

            case PadFooting pad:
                pad.Location += delta;
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
                foreach (var between in dimension.Between) MoveReference(between, delta);
                break;

            case AngularDimension angular:
                MoveReference(angular.First, delta);
                MoveReference(angular.Second, delta);
                angular.Through += delta;
                break;

            case RadialDimension radial:
                radial.Through += delta;
                break;

            case Tag tag:
                tag.Position += delta;
                break;

            case TextNote note:
                note.Position += delta;
                if (note.LeaderEnd is { } leader) note.LeaderEnd = leader + delta;
                break;

            case RevisionCloud cloud:
                for (var i = 0; i < cloud.Outline.Count; i++) cloud.Outline[i] += delta;
                if (cloud.TagAt is { } tagAt) cloud.TagAt = tagAt + delta;
                break;

            case KeynoteTag keynote:
                keynote.Position += delta;
                if (keynote.LeaderEnd is { } end) keynote.LeaderEnd = end + delta;
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
                slab.SetHoles(slab.Holes.Select(hole => hole.Select(Reflect)).ToList());
                if (slab is Floor { SpanDirection: { } span } spanned) spanned.SpanDirection = Floors.Reflected(span, Reflect);
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

            case PadFooting pad:
                pad.Location = Reflect(pad.Location);
                break;

            case RoofWindow roofWindow:
                roofWindow.Location = Reflect(roofWindow.Location);
                break;

            case Downpipe pipe:
                pipe.Location = Reflect(pipe.Location);
                break;

            case SlabEdge slabEdge:
                SlabEdgeRuns.Transform(slabEdge, Reflect);
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

            case Stair stair:
            {
                // Its way up reflected, and - a mirror image turning the other way - its turn too.
                var up = Reflect(stair.Start + stair.Up * 1000);
                stair.Start = Reflect(stair.Start);
                var way = up - stair.Start;
                stair.Angle = Math.Atan2(way.Y, way.X) * 180 / Math.PI;
                stair.TurnsLeft = !stair.TurnsLeft;
                Stairs.SwapSides(stair);
                break;
            }

            case Ramp ramp:
            {
                // Its way up reflected, and - a mirror image turning the other way - its turns too.
                var up = Reflect(ramp.Start + ramp.Up * 1000);
                ramp.Start = Reflect(ramp.Start);
                var way = up - ramp.Start;
                ramp.Angle = Math.Atan2(way.Y, way.X) * 180 / Math.PI;
                ramp.TurnsLeft = !ramp.TurnsLeft;
                break;
            }

            case Railing railing:
            {
                // Mirrored and run the other way round, so what was on its left - a handrail on its
                // inside - is on its left still; its arcs turn as they did, reflected and reversed.
                var (path, bulges, count) = (railing.Path, railing.Bulges, railing.Path.Count);
                var closed = railing.Closed && count >= 3;
                var points = Enumerable.Range(0, count).Select(j => Reflect(path[closed ? (count - j) % count : count - 1 - j])).ToList();
                var bows = Enumerable.Range(0, count)
                    .Select(j => closed ? bulges[(count - 1 - j + count) % count] : j <= count - 2 ? bulges[count - 2 - j] : 0)
                    .ToList();
                railing.SetPath(points, bows);
                break;
            }

            case Beam beam:
                (beam.Start, beam.End) = (Reflect(beam.Start), Reflect(beam.End));
                break;

            case RoomSeparator separator:
                (separator.Start, separator.End) = (Reflect(separator.Start), Reflect(separator.End));
                break;

            case Mep.Pipe pipe:
                (pipe.Start, pipe.End) = (Reflect(pipe.Start), Reflect(pipe.End));
                break;

            case Mep.Duct duct:
                (duct.Start, duct.End) = (Reflect(duct.Start), Reflect(duct.End));
                break;

            case Toposurface ground:
                ground.SetPoints(ground.Points.Select(point => Reflect(new Point2D(point.X, point.Y)) is var at ? new Point3D(at.X, at.Y, point.Z) : point).ToList());
                ground.SetExistingPoints(ground.ExistingPoints.Select(point => Reflect(new Point2D(point.X, point.Y)) is var at ? new Point3D(at.X, at.Y, point.Z) : point).ToList());
                break;

            case GroundRegion region:
                region.SetBoundary(region.Boundary.Select(Reflect).ToList());
                region.TransformPath(Reflect);
                break;

            case Mass mass:
                mass.SetBoundary(mass.Boundary.Select(Reflect).ToList());
                mass.TransformCircle(Reflect);
                break;

            case Walkthrough walk:
                walk.SetPath(walk.Path.Select(Reflect).ToList());
                break;

            case Callout callout:
            {
                var a = Reflect(new Point2D(callout.Region.MinX, callout.Region.MinY));
                var b = Reflect(new Point2D(callout.Region.MaxX, callout.Region.MaxY));
                var c = Reflect(new Point2D(callout.Region.MinX, callout.Region.MaxY));
                var d = Reflect(new Point2D(callout.Region.MaxX, callout.Region.MinY));
                callout.Region = BoundingBox2D.Empty.Include(a).Include(b).Include(c).Include(d);
                break;
            }

            case InteriorElevationMarker marker:
            {
                // Its first arrow reflected; the arrows to either side change places.
                var up = ReflectPoint(marker.Location + marker.Look(0) * 1000, axis);
                marker.Location = Reflect(marker.Location);
                var look = up - marker.Location;
                marker.Rotation = Math.Atan2(look.Y, look.X) * 180 / Math.PI - 90;
                (marker.LooksRight, marker.LooksLeft) = (marker.LooksLeft, marker.LooksRight);
                break;
            }

            case SitePad pad:
            {
                // The way it falls reflected with it.
                var middle = Polygon2D.Centroid(pad.Boundary, pad.Boundary.Count > 0 ? pad.Boundary[0] : default);
                var falls = Reflect(middle + new Vector2D(Math.Cos(pad.FallDirection * Math.PI / 180), Math.Sin(pad.FallDirection * Math.PI / 180)) * 1000) - Reflect(middle);
                pad.SetBoundary(pad.Boundary.Select(Reflect).ToList());
                pad.FallDirection = (Math.Atan2(falls.Y, falls.X) * 180 / Math.PI % 360 + 360) % 360;
                break;
            }

            case PropertyLine line:
                line.SetBoundary(line.Boundary.Select(Reflect).ToList());
                break;

            case SpotElevation spot:
                spot.Location = Reflect(spot.Location);
                break;

            case BeamSystem system:
            {
                // Its joists' way reflected with it.
                var way = Reflect(new Point2D(Math.Cos(BeamSystems.Angle(system) * Math.PI / 180), Math.Sin(BeamSystems.Angle(system) * Math.PI / 180))) - Reflect(new Point2D(0, 0));

                // Run round the other way, as a reflection must be, starting from the image of its first side - which the short way is worked out from.
                var reflected = system.Boundary.Select(Reflect).ToList();
                var count = reflected.Count;
                system.SetBoundary(Enumerable.Range(0, count).Select(j => reflected[(1 - j + count) % count]).ToList());
                if (system.Direction is not null) system.Direction = ((Math.Atan2(way.Y, way.X) * 180 / Math.PI) % 180 + 180) % 180;
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

                // And it is the mirror image of what it was: left is right.
                component.FlipHand = !component.FlipHand;
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
                foreach (var between in dimension.Between) between.FallbackPoint = ReflectPoint(between.FallbackPoint, axis);

                // The offset is measured along a perpendicular that has just turned over.
                dimension.Offset = -dimension.Offset;
                break;

            case AngularDimension angular:
                angular.First.FallbackPoint = ReflectPoint(angular.First.FallbackPoint, axis);
                angular.Second.FallbackPoint = ReflectPoint(angular.Second.FallbackPoint, axis);
                angular.Through = ReflectPoint(angular.Through, axis);
                break;

            case RadialDimension radial:
                radial.Through = ReflectPoint(radial.Through, axis);
                break;

            case Tag tag:
                tag.Position = Reflect(tag.Position);
                break;

            case TextNote note:
                note.Position = Reflect(note.Position);
                if (note.LeaderEnd is { } leader) note.LeaderEnd = Reflect(leader);
                break;

            case RevisionCloud cloud:
                for (var i = 0; i < cloud.Outline.Count; i++) cloud.Outline[i] = Reflect(cloud.Outline[i]);
                if (cloud.TagAt is { } tagAt) cloud.TagAt = Reflect(tagAt);
                break;

            case KeynoteTag keynote:
                keynote.Position = Reflect(keynote.Position);
                if (keynote.LeaderEnd is { } end) keynote.LeaderEnd = Reflect(end);
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
    public static bool CanScale(Element element) => CanMove(element) && element is not Roof { Extrusion: not null } || element is SlabEdge;

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
                slab.SetHoles(slab.Holes.Select(hole => hole.Select(map)).ToList());
                if (slab is Floor { SpanDirection: { } span } spanned) spanned.SpanDirection = Floors.Turned(span, degrees);
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

            case PadFooting pad:
                pad.Location = map(pad.Location);
                pad.Rotation += degrees;
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

            case SlabEdge slabEdge:
                SlabEdgeRuns.Transform(slabEdge, map);
                break;

            case RoofDrain drain:
                drain.Location = map(drain.Location);
                break;

            case Chimney chimney:
                chimney.Location = map(chimney.Location);
                chimney.Angle += degrees;
                break;

            case Stair stair:
                stair.Start = map(stair.Start);
                stair.Angle += degrees;
                break;

            case Ramp ramp:
                ramp.Start = map(ramp.Start);
                ramp.Angle += degrees;
                break;

            case Railing railing:
                railing.SetPath(railing.Path.Select(map).ToList());
                break;

            case Beam beam:
                (beam.Start, beam.End) = (map(beam.Start), map(beam.End));
                break;

            case RoomSeparator separator:
                (separator.Start, separator.End) = (map(separator.Start), map(separator.End));
                break;

            case Mep.Pipe pipe:
                (pipe.Start, pipe.End) = (map(pipe.Start), map(pipe.End));
                break;

            case Mep.Duct duct:
                (duct.Start, duct.End) = (map(duct.Start), map(duct.End));
                break;

            case Toposurface ground:
                ground.SetPoints(ground.Points.Select(point => map(new Point2D(point.X, point.Y)) is var at ? new Point3D(at.X, at.Y, point.Z) : point).ToList());
                ground.SetExistingPoints(ground.ExistingPoints.Select(point => map(new Point2D(point.X, point.Y)) is var at ? new Point3D(at.X, at.Y, point.Z) : point).ToList());
                break;

            case GroundRegion region:
                region.SetBoundary(region.Boundary.Select(map).ToList());
                region.TransformPath(map);
                break;

            case SitePad pad:
                pad.SetBoundary(pad.Boundary.Select(map).ToList());
                pad.FallDirection = ((pad.FallDirection + degrees) % 360 + 360) % 360;
                break;

            case Mass mass:
                mass.SetBoundary(mass.Boundary.Select(map).ToList());
                mass.TransformCircle(map);
                break;

            case Walkthrough walk:
                walk.SetPath(walk.Path.Select(map).ToList());
                break;

            case InteriorElevationMarker marker:
                marker.Location = map(marker.Location);
                marker.Rotation += degrees;
                break;

            // A callout's box stays square to the plan: round where its corners go.
            case Callout callout:
            {
                var box = BoundingBox2D.Empty;
                foreach (var corner in new[] { new Point2D(callout.Region.MinX, callout.Region.MinY), new Point2D(callout.Region.MaxX, callout.Region.MinY),
                             new Point2D(callout.Region.MaxX, callout.Region.MaxY), new Point2D(callout.Region.MinX, callout.Region.MaxY) })
                    box = box.Include(map(corner));
                callout.Region = box;
                break;
            }

            case PropertyLine line:
                line.SetBoundary(line.Boundary.Select(map).ToList());
                break;

            case SpotElevation spot:
                spot.Location = map(spot.Location);
                break;

            case BeamSystem system:
                system.SetBoundary(system.Boundary.Select(map).ToList());
                if (system.Direction is { } angle) system.Direction = ((angle + degrees) % 180 + 180) % 180;
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
                foreach (var between in dimension.Between.Where(between => between.Anchor == DimensionAnchor.Point)) between.FallbackPoint = map(between.FallbackPoint);
                dimension.Offset *= factor;
                break;

            case AngularDimension angular:
                if (angular.First.Anchor == DimensionAnchor.Point) angular.First.FallbackPoint = map(angular.First.FallbackPoint);
                if (angular.Second.Anchor == DimensionAnchor.Point) angular.Second.FallbackPoint = map(angular.Second.FallbackPoint);
                angular.Through = map(angular.Through);
                break;

            case RadialDimension radial:
                radial.Through = map(radial.Through);
                break;

            case Tag tag:
                tag.Position = map(tag.Position);
                break;

            case TextNote note:
                note.Position = map(note.Position);
                if (note.LeaderEnd is { } leader) note.LeaderEnd = map(leader);
                break;

            case RevisionCloud cloud:
                for (var i = 0; i < cloud.Outline.Count; i++) cloud.Outline[i] = map(cloud.Outline[i]);
                if (cloud.TagAt is { } tagAt) cloud.TagAt = map(tagAt);
                break;

            case KeynoteTag keynote:
                keynote.Position = map(keynote.Position);
                if (keynote.LeaderEnd is { } end) keynote.LeaderEnd = map(end);
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
