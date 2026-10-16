using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Furniture and fittings set against the walls, as they are in a room: a bed, a wardrobe, a WC,
/// a kitchen unit put down near a wall backs onto it and faces the room; kitchen units close up
/// side by side into a run along the wall; and a wall unit hangs at its height above the worktop
/// under it - the underside 500 mm over it, as UK kitchen guides set it, and as the US 18 in over
/// a 36 in counter comes to.
/// </summary>
public static class ComponentPlacing
{
    /// <summary>How far from a wall's face a piece put down near it is drawn back onto it, mm.</summary>
    public const double Snap = 300;

    /// <summary>How small a gap between two units along a wall is closed up, mm: they are a run.</summary>
    public const double RunGap = 300;

    /// <summary>How far a wall unit's underside is above the worktop under it, mm.</summary>
    public const double AboveWorktop = 500;

    /// <summary>
    /// How far a cooker hood's underside is hung over the hob: 700, the middle of the 650 to 750
    /// Approved Document F asks for where the hood's maker gives none - 28 in (711) in the US,
    /// between the 24 and 30 in hoods are hung at.
    /// </summary>
    public static double AboveHob(ClearanceStandard standard) => standard.IsUs() ? 711 : 700;

    /// <summary>Whether a piece of this type is set against a wall: one standing on the floor, made to back onto one.</summary>
    public static bool BacksOntoWall(ComponentType type) => type.AgainstWall && type.Placement == ComponentPlacement.Freestanding;

    /// <summary>
    /// Whether a piece of this type is part of a kitchen run, closing up to the next: the units,
    /// and the fridge, cooker, dishwasher, washing machine and sink unit that stand in a run among them.
    /// </summary>
    public static bool InRun(ComponentType type) =>
        type.Kind == Elements.BuiltInCategory.Casework
        || type.Form is ComponentForm.Fridge or ComponentForm.Cooker or ComponentForm.Dishwasher or ComponentForm.WashingMachine or ComponentForm.Sink
            or ComponentForm.TumbleDryer or ComponentForm.TallUnit or ComponentForm.OvenHousing or ComponentForm.WallUnit;

    /// <summary>
    /// Whether a type has a worktop on top, for a wall unit to hang over: a unit, a sink unit, a
    /// cooker, and a dishwasher, washing machine or fridge under one.
    /// </summary>
    public static bool HasWorktop(ComponentType type) =>
        type.Form is ComponentForm.Counter or ComponentForm.Sink or ComponentForm.Cooker or ComponentForm.Dishwasher or ComponentForm.WashingMachine
            or ComponentForm.TumbleDryer
        || ComponentModel.UnderCounter(type);

    /// <summary>A wall's body in plan, about its centreline: its ends, its left normal and half its thickness.</summary>
    private readonly record struct Body(Wall Wall, Point2D Start, Point2D End, Vector2D Normal, double Half)
    {
        public double Across(Point2D point) => (point - Start).Dot(Normal);

        public Vector2D Along => (End - Start).NormalisedOrDefault(Vector2D.UnitX);

        public IReadOnlyList<Point2D> Outline => new[] { Start - Normal * Half, End - Normal * Half, End + Normal * Half, Start + Normal * Half };
    }

    /// <summary>The straight walls standing at a height - a piece's foot - on its storey.</summary>
    private static List<Body> WallsAt(BimDocument document, double foot)
    {
        var bodies = new List<Body>();
        foreach (var wall in document.Walls.Where(wall => !wall.IsCurved))
        {
            if (document.GetWallType(wall) is not { } type || type.Structure.TotalWidth <= 0) continue;
            if (wall.GetBaseElevation(document) > foot + 1 || wall.GetTopElevation(document) <= foot + 1) continue;

            var (start, end) = wall.GetBodyCentreline(type.Structure);
            if (start.DistanceTo(end) < 1) continue;
            bodies.Add(new Body(wall, start, end, (end - start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft(), type.Structure.TotalWidth / 2));
        }

        return bodies;
    }

    /// <summary>Which side of a wall the room is: the side a point is on; for one in the wall, the side the rest of the walls are.</summary>
    private static double RoomSide(IReadOnlyList<Body> walls, Body wall, Point2D at)
    {
        var across = wall.Across(at);
        if (Math.Abs(across) > wall.Half + 1) return Math.Sign(across);

        var others = walls.Where(other => other.Wall != wall.Wall).ToList();
        if (others.Count > 0)
        {
            var middle = new Point2D(others.Average(other => (other.Start.X + other.End.X) / 2), others.Average(other => (other.Start.Y + other.End.Y) / 2));
            if (Math.Abs(wall.Across(middle)) > 1) return Math.Sign(wall.Across(middle));
        }

        return wall.Wall.ExteriorNormalAt(0).Dot(wall.Normal) > 0 ? -1 : 1;
    }

    private static IReadOnlyList<Point2D> Footprint(Component component, ComponentType type) =>
        ComponentModel.Footprint(type, ComponentModel.FrameOf(component, 0));

    private static double Overlap(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b) =>
        PolygonBoolean.Combine(a, b, BooleanOperation.Intersection).Sum(region => region.Area);

    /// <summary>
    /// Sets a piece made to back onto a wall against the nearest one, if it is put down near
    /// enough: turned to face the room, its back on the wall's face, slid clear of a wall it runs
    /// into at a corner, and closed up to a unit beside it along the wall. False where it is left
    /// as it was - well out in the room, or not one that backs onto a wall.
    /// </summary>
    public static bool FitToWalls(BimDocument document, Component component)
    {
        if (component.IsHosted || document.FindType<ComponentType>(component.TypeId) is not { } type || !BacksOntoWall(type)) return false;

        var walls = WallsAt(document, ComponentHosting.BaseElevation(document, component, type));
        if (walls.Count == 0) return false;

        // The nearest wall within reach of it, whichever way round it was put down.
        var reach = Math.Max(type.Width, type.Depth) / 2 + Snap;
        var near = walls
            .Select(body => (Body: body, Distance: Line2D.DistanceFromSegment(component.Location, body.Start, body.End) - body.Half))
            .Where(candidate => candidate.Distance <= reach)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => (Body?)candidate.Body)
            .FirstOrDefault();
        if (near is not { } back) return false;

        // Its front to the room, its back on the wall's face.
        var side = RoomSide(walls, back, component.Location);
        var facing = back.Normal * side;
        component.FaceToward(facing);
        component.Location += facing * (back.Half + type.Depth / 2 - back.Across(component.Location) * side);

        // Clear of the walls either side of it, sliding along the one it backs onto.
        var along = back.Along;
        for (var pass = 0; pass < 3; pass++)
        {
            var moved = false;
            foreach (var wall in walls.Where(wall => wall.Wall != back.Wall))
            {
                var footprint = Footprint(component, type);
                if (Overlap(wall.Outline, footprint) <= 100) continue;

                var away = RoomSide(walls, wall, component.Location);
                var slide = away * along.Dot(wall.Normal);
                if (Math.Abs(slide) < 0.2) continue;

                var shortfall = footprint.Max(point => wall.Half - away * wall.Across(point));
                component.Location += along * (shortfall / slide);
                moved = true;
            }

            if (!moved) break;
        }

        // One open on one side - a corner shower's glass, a walk-in's way in - has that side away
        // from the wall it was slid clear of: flipped, where that wall is on its open side.
        if (ComponentModel.OpenOnTheRight(type))
        {
            var half = type.Width / 2;
            bool WallBeside(Point2D end) => walls.Any(wall => wall.Wall != back.Wall
                && Line2D.DistanceFromSegment(end, wall.Start, wall.End) - wall.Half <= 50);

            var left = WallBeside(component.Location - component.Across * half);
            var right = WallBeside(component.Location + component.Across * half);
            if (left != right) component.FlipHand = right;
        }

        CloseUp(document, component, type);
        return true;
    }

    /// <summary>
    /// Closes a piece up to its neighbour along the wall it backs onto - a kitchen unit to the
    /// next, a wall unit to the one beside it - where the gap between them is small, or where it
    /// was put down overlapping it: a run, not a row with gaps.
    /// </summary>
    public static void CloseUp(BimDocument document, Component component, ComponentType type)
    {
        // A kitchen run only: two single beds or two desks stay as far apart as they were put.
        if (!InRun(type)) return;

        var across = component.Across;
        var back = component.Location - component.Facing * (type.Depth / 2);
        var foot = ComponentHosting.BaseElevation(document, component, type);

        double? best = null;
        foreach (var other in document.Elements.OfType<Component>().Where(other => other != component && other.LevelId == component.LevelId))
        {
            if (document.FindType<ComponentType>(other.TypeId) is not { } otherType || !InRun(otherType)) continue;
            if (other.Facing.Dot(component.Facing) < 0.999) continue;

            // On the same wall line, at the same height - a wall unit closes up to wall units, not to the base units under them.
            var otherBack = other.Location - other.Facing * (otherType.Depth / 2);
            if (Math.Abs((otherBack - back).Dot(component.Facing)) > 1) continue;
            var otherFoot = ComponentHosting.BaseElevation(document, other, otherType);
            if (Math.Abs(otherFoot - foot) > 50) continue;

            var offset = (other.Location - component.Location).Dot(across);
            var touching = (type.Width + otherType.Width) / 2;
            var gap = Math.Abs(offset) - touching;
            if (gap > RunGap || gap < -touching) continue;

            // Moved along to touch it, on the side it is on.
            var move = Math.Sign(offset) * gap;
            if (best is null || Math.Abs(move) < Math.Abs(best.Value)) best = move;
        }

        if (best is { } shift && Math.Abs(shift) > 1e-6) component.Location += across * shift;
    }

    /// <summary>
    /// The height a cooker hood hangs at over the hob under it - its underside 700 over it, or
    /// 28 in in the US - as an elevation for it; null where no cooker is under it.
    /// </summary>
    public static double? OverHob(BimDocument document, Component hood, ComponentType type)
    {
        var footprint = Footprint(hood, type);
        double? top = null;
        foreach (var cooker in document.Elements.OfType<Component>().Where(other => other != hood && other.LevelId == hood.LevelId))
        {
            if (document.FindType<ComponentType>(cooker.TypeId) is not { Form: ComponentForm.Cooker } cookerType) continue;
            if (Overlap(footprint, Footprint(cooker, cookerType)) <= 100) continue;

            var height = ComponentHosting.BaseElevation(document, cooker, cookerType) + cookerType.Height;
            top = top is { } known ? Math.Max(known, height) : height;
        }

        return top is { } hob
            ? hood.Elevation + hob + AboveHob(document.ProjectInformation.ClearanceStandard) - ComponentHosting.BaseElevation(document, hood, type)
            : null;
    }

    /// <summary>
    /// The height a wall unit hangs at over the worktop under it - its underside 500 mm above the
    /// highest worktop or counter it is over - as an elevation for it; null where none is under it.
    /// </summary>
    public static double? OverWorktop(BimDocument document, Component unit, ComponentType type)
    {
        var footprint = Footprint(unit, type);
        double? top = null;
        foreach (var counter in document.Elements.OfType<Component>().Where(other => other != unit && other.LevelId == unit.LevelId))
        {
            if (document.FindType<ComponentType>(counter.TypeId) is not { } counterType || !HasWorktop(counterType)) continue;
            if (Overlap(footprint, Footprint(counter, counterType)) <= 100) continue;

            var height = ComponentHosting.BaseElevation(document, counter, counterType) + counterType.Height;
            top = top is { } known ? Math.Max(known, height) : height;
        }

        return top is { } worktop ? unit.Elevation + worktop + AboveWorktop - ComponentHosting.BaseElevation(document, unit, type) : null;
    }
}

/// <summary>Pieces set against the walls, as one step with whatever moved them there.</summary>
public sealed class FitComponentsCommand : IUndoableCommand
{
    private readonly List<(Component Component, (Point2D At, double Rotation, bool Hand) From, (Point2D At, double Rotation, bool Hand) To)> _moves;

    private FitComponentsCommand(List<(Component, (Point2D, double, bool), (Point2D, double, bool))> moves) => _moves = moves;

    private static (Point2D At, double Rotation, bool Hand) State(Component component) =>
        (component.Location, component.Rotation, component.FlipHand);

    /// <summary>Fits those of the pieces that back onto a wall and are near one; null when none moves. They are left fitted.</summary>
    public static FitComponentsCommand? For(BimDocument document, IEnumerable<Component> components)
    {
        var moves = new List<(Component, (Point2D, double, bool), (Point2D, double, bool))>();
        foreach (var component in components.Distinct())
        {
            var before = State(component);
            if (!ComponentPlacing.FitToWalls(document, component)) continue;

            var after = State(component);
            if (after.At.DistanceTo(before.At) > 1e-6 || Math.Abs(after.Rotation - before.Rotation) > 1e-9 || after.Hand != before.Hand)
                moves.Add((component, before, after));
        }

        return moves.Count == 0 ? null : new FitComponentsCommand(moves);
    }

    public string Name => _moves.Count == 1 ? "Set Against the Wall" : "Set Against the Walls";

    public void Redo()
    {
        foreach (var (component, _, to) in _moves) (component.Location, component.Rotation, component.FlipHand) = to;
    }

    public void Undo()
    {
        foreach (var (component, from, _) in _moves) (component.Location, component.Rotation, component.FlipHand) = from;
    }
}

/// <summary>Turns pieces over, left for right - a corner shower's glass to its other side. Doing it again undoes it.</summary>
public sealed class FlipHandCommand : IUndoableCommand
{
    private readonly IReadOnlyList<Component> _components;

    public FlipHandCommand(IEnumerable<Component> components) => _components = components.Distinct().ToList();

    public string Name => "Flip Hand";

    public void Redo()
    {
        foreach (var component in _components) component.FlipHand = !component.FlipHand;
    }

    public void Undo() => Redo();
}

/// <summary>Which room something is in: a room on its storey whose outline is round it.</summary>
public static class RoomsOf
{
    /// <summary>The rooms' outlines traced while one lasts: a schedule asks for the room of every row, and each trace goes round the walls.</summary>
    [ThreadStatic] private static Dictionary<Room, IReadOnlyList<Point2D>>? _outlines;

    /// <summary>Keeps the rooms' outlines worked out while it lasts, with nothing changing under it.</summary>
    public sealed class Outlines : IDisposable
    {
        private readonly bool _outer;

        public Outlines()
        {
            _outer = _outlines is null;
            _outlines ??= new Dictionary<Room, IReadOnlyList<Point2D>>(ReferenceEqualityComparer.Instance);
        }

        public void Dispose()
        {
            if (_outer) _outlines = null;
        }
    }

    private static IReadOnlyList<Point2D> Outline(BimDocument document, Room room)
    {
        if (_outlines is not null && _outlines.TryGetValue(room, out var known)) return known;
        var traced = room.GetBoundary(document).Polygon;
        if (_outlines is not null) _outlines[room] = traced;
        return traced;
    }

    /// <summary>The room on a storey round a point, or null where there is none.</summary>
    public static Room? Containing(BimDocument document, Guid levelId, Point2D at) =>
        document.Elements.OfType<Room>()
            .Where(room => room.LevelId == levelId)
            .FirstOrDefault(room => Outline(document, room) is { Count: >= 3 } outline && Polygon2D.Contains(outline, at));

    /// <summary>How a room is named in a list: its number and name - "1 Kitchen".</summary>
    public static string Label(Room room) => string.IsNullOrWhiteSpace(room.Number) ? room.Name : $"{room.Number} {room.Name}";
}
