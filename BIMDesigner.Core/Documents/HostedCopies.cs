using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Documents;

/// <summary>
/// Doors, windows, openings cut in walls and components fixed to a face, moved without what
/// carries them - pasted, arrayed, mirrored on their own: each goes into whatever it lands on,
/// facing out or in and hung from the hand it was - or, mirrored, as the mirror image of
/// itself - or is left out where nothing there can take it. A window arrayed along a front goes
/// into the front; one pasted over another wall goes into that wall.
/// </summary>
public static class HostedCopies
{
    /// <summary>How far in front of a wall's face a component fixed to it can land and still find it.</summary>
    public const double Reach = 400;

    /// <summary>
    /// Whether an element is carried by something not among <paramref name="with"/>, so that
    /// moving it means finding it a host of its own.
    /// </summary>
    public static bool Alone(Element element, IReadOnlySet<Guid> with) => element switch
    {
        Opening opening => !with.Contains(opening.HostWallId),
        WallOpening cut => !with.Contains(cut.HostWallId),
        Component { IsHosted: true } component => !with.Contains(component.HostId),
        _ => false
    };

    /// <summary>Whether a selection has anything a move, an array or a mirror can take: what moves on its own, or what can find a new host.</summary>
    public static bool AnyToMove(IReadOnlyCollection<Element> selection)
    {
        var ids = selection.Select(element => element.Id).ToHashSet();
        return selection.Any(element => ElementTransforms.CanMove(element) || Alone(element, ids));
    }

    /// <summary>
    /// Puts copies made without their hosts where <paramref name="map"/> takes them, into what
    /// is there. Copies still on the hosts they were copied from are taken from there; the rest
    /// - those copied with their host - are left as they are. Returns those with nowhere to go,
    /// for the caller to leave out.
    /// </summary>
    public static IReadOnlyList<Element> Settle(BimDocument document, IReadOnlyList<Element> copies, Func<Point2D, Point2D> map) =>
        Settle(document, copies, map, new List<(Guid, double, double)>());

    /// <summary>
    /// <see cref="Settle(BimDocument, IReadOnlyList{Element}, Func{Point2D, Point2D})"/> for
    /// copies made in several sets - an array - so no two of them go into the same place.
    /// </summary>
    public static IReadOnlyList<Element> Settle(BimDocument document, IReadOnlyList<Element> copies, Func<Point2D, Point2D> map, List<(Guid Wall, double From, double To)> taken)
    {
        var ids = copies.Select(copy => copy.Id).ToHashSet();
        var left = new List<Element>();
        foreach (var copy in copies.Where(copy => Alone(copy, ids)))
            if (!Place(document, copy, map, taken)) left.Add(copy);
        return left;
    }

    /// <summary>
    /// Elements themselves - not copies - taken where <paramref name="map"/> takes them, into
    /// what is there, as one step to undo. Null where none of them can go anywhere; those that
    /// cannot are in <paramref name="stayed"/>, where they were.
    /// </summary>
    public static IUndoableCommand? Move(BimDocument document, IReadOnlyList<Element> elements, Func<Point2D, Point2D> map, string name, out IReadOnlyList<Element> stayed)
    {
        var ids = elements.Select(element => element.Id).ToHashSet();
        var taken = new List<(Guid, double, double)>();
        var moves = new List<IUndoableCommand>();
        var left = new List<Element>();
        foreach (var element in elements.Where(element => Alone(element, ids)))
        {
            // Worked out on a copy, so both where it was and where it goes are known.
            if (ElementCopy.Clone(element) is { } trial && Place(document, trial, map, taken, ignore: element))
                moves.Add(new PlaceCommand(element, Of(element), Of(trial), name));
            else
                left.Add(element);
        }

        stayed = left;
        return moves.Count == 0 ? null : moves.Count == 1 ? moves[0] : new CompositeCommand(name, moves);
    }

    private static bool Place(BimDocument document, Element element, Func<Point2D, Point2D> map, List<(Guid Wall, double From, double To)> taken, Element? ignore = null) => element switch
    {
        Opening opening => PlaceOpening(document, opening, map, taken, ignore as Opening),
        WallOpening cut => PlaceCut(document, cut, map),
        Component component => PlaceComponent(document, component, map),
        _ => false
    };

    /// <summary>A direction where the map takes it, from a point it starts at.</summary>
    private static Vector2D Turned(Func<Point2D, Point2D> map, Point2D at, Vector2D direction) => map(at + direction) - map(at);

    /// <summary>Whether the map turns left into right: a mirror.</summary>
    private static bool Reflects(Func<Point2D, Point2D> map)
    {
        var origin = map(new Point2D(0, 0));
        return (map(new Point2D(1, 0)) - origin).Cross(map(new Point2D(0, 1)) - origin) < 0;
    }

    private static bool PlaceOpening(BimDocument document, Opening opening, Func<Point2D, Point2D> map, List<(Guid Wall, double From, double To)> taken, Opening? ignore)
    {
        if (document.Find<Wall>(opening.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType ||
            document.FindType<OpeningType>(opening.TypeId) is not { } type) return false;

        // Which way it faces and which side it is hung from, as directions on the plan.
        var along = opening.DistanceAlongWall;
        var centre = wall.PointAt(wallType.Structure, along, 0);
        var facing = Turned(map, centre, wall.ExteriorNormalAt(along) * (opening.FlipFacing ? -1 : 1));
        var hinge = Turned(map, centre, wall.LocationCurve.TangentAt(along) * (opening.FlipHand ? 1 : -1));
        var outside = Outside(wall, along);

        var to = map(centre);
        if (ComponentHosting.WallAt(document, to, opening.LevelId) is not { } host || document.IsCurtainWall(host)) return false;

        var width = opening.WidthOf(type);
        if (WallOpenings.ClampToWall(host, width, host.LocationCurve.Locate(to).Along) is not { } placed) return false;
        if (!WallOpenings.CanPlace(document, host, width, placed, ignore) || Overlaps(taken, host.Id, placed, width)) return false;
        if (opening.PlacedIn(document, host, type, placed) is null) return false;

        opening.HostWallId = host.Id;
        opening.LevelId = host.LevelId;
        opening.DistanceAlongWall = placed;
        if (Reflects(map))
        {
            // Mirrored, it is the mirror image of itself: opening into the reflected room, hung from the other hand.
            opening.FlipFacing = facing.Dot(host.ExteriorNormalAt(placed)) < 0;
            opening.FlipHand = hinge.Dot(host.LocationCurve.TangentAt(placed)) > 0;
        }
        else if (Outside(host, placed) != outside)
        {
            // Moved, it is to its new wall as it was to its old: facing out or in as it did, and
            // hung from the same hand seen from outside, whichever way that wall was drawn.
            opening.FlipHand = !opening.FlipHand;
        }

        taken.Add((host.Id, placed - width / 2, placed + width / 2));
        return true;
    }

    /// <summary>Which hand of a wall's run its outside is on: the sign that turns along it into out of it.</summary>
    private static int Outside(Wall wall, double along) => Math.Sign(wall.LocationCurve.TangentAt(along).Cross(wall.ExteriorNormalAt(along)));

    private static bool Overlaps(List<(Guid Wall, double From, double To)> taken, Guid wall, double along, double width) =>
        taken.Any(span => span.Wall == wall && along - width / 2 < span.To && along + width / 2 > span.From);

    private static bool PlaceCut(BimDocument document, WallOpening cut, Func<Point2D, Point2D> map)
    {
        if (document.Find<Wall>(cut.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return false;

        var to = map(wall.PointAt(wallType.Structure, cut.DistanceAlongWall, 0));
        if (ComponentHosting.WallAt(document, to, cut.LevelId) is not { } host) return false;
        if (WallOpenings.ClampToWall(host, cut.Width, host.LocationCurve.Locate(to).Along) is not { } placed) return false;

        cut.HostWallId = host.Id;
        cut.LevelId = host.LevelId;
        cut.DistanceAlongWall = placed;
        return true;
    }

    private static bool PlaceComponent(BimDocument document, Component component, Func<Point2D, Point2D> map)
    {
        var to = map(component.Location);
        var facing = Turned(map, component.Location, component.Facing);
        if (Reflects(map)) component.FlipHand = !component.FlipHand;

        switch (document.Find<Element>(component.HostId))
        {
            case Wall:
            {
                // Against the face of the wall it lands in front of, on the side it faced out to.
                var near = document.Walls
                    .Where(wall => wall.LevelId == component.LevelId)
                    .Select(wall => (Wall: wall, Type: document.GetWallType(wall)))
                    .Where(entry => entry.Type is not null)
                    .Select(entry => (entry.Wall, entry.Type, At: entry.Wall.Locate(entry.Type!.Structure, to)))
                    .Where(entry => entry.At.Along >= 0 && entry.At.Along <= entry.Wall.Length && Math.Abs(entry.At.Across) <= entry.Type!.Width / 2 + Reach)
                    .OrderBy(entry => Math.Abs(entry.At.Across))
                    .FirstOrDefault();
                if (near.Wall is null) return false;

                // On the side of the wall it lands on; landing in the wall itself, the side it faced out to.
                var side = Math.Abs(near.At.Across) > near.Type!.Width / 2 ? Math.Sign(near.At.Across)
                    : facing.Dot(near.Wall.ExteriorNormalAt(near.At.Along)) >= 0 ? 1.0 : -1.0;
                return component.HostOn(document, near.Wall, near.Wall.PointAt(near.Type!.Structure, near.At.Along, side * (near.Type.Width / 2 + 1)));
            }

            case Slab slab:
            {
                // On the same face where it still covers the point; otherwise what is there.
                var on = slab.LevelId == component.LevelId && slab.Contains(to) ? slab : ComponentHosting.SlabAt(document, to, component.LevelId);
                if (on is null || !component.HostOn(document, on, to, underneath: component.FlipFacing)) return false;
                component.FaceToward(facing);
                return true;
            }

            default:
                return false;
        }
    }

    private sealed record Placement(Guid Host, Guid Level, double Along, Point2D Location, double Rotation, bool FlipFacing, bool FlipHand);

    private static Placement Of(Element element) => element switch
    {
        Opening opening => new(opening.HostWallId, opening.LevelId, opening.DistanceAlongWall, default, 0, opening.FlipFacing, opening.FlipHand),
        WallOpening cut => new(cut.HostWallId, cut.LevelId, cut.DistanceAlongWall, default, 0, false, false),
        Component component => new(component.HostId, component.LevelId, 0, component.Location, component.Rotation, component.FlipFacing, component.FlipHand),
        _ => throw new ArgumentException("Not a hosted element.", nameof(element))
    };

    /// <summary>A hosted element put somewhere else, and back.</summary>
    private sealed class PlaceCommand(Element element, Placement before, Placement after, string name) : IUndoableCommand
    {
        public string Name => name;

        public void Redo() => Put(after);

        public void Undo() => Put(before);

        private void Put(Placement placement)
        {
            element.LevelId = placement.Level;
            switch (element)
            {
                case Opening opening:
                    (opening.HostWallId, opening.DistanceAlongWall, opening.FlipFacing, opening.FlipHand) = (placement.Host, placement.Along, placement.FlipFacing, placement.FlipHand);
                    break;
                case WallOpening cut:
                    (cut.HostWallId, cut.DistanceAlongWall) = (placement.Host, placement.Along);
                    break;
                case Component component:
                    (component.HostId, component.Location, component.Rotation, component.FlipFacing, component.FlipHand) =
                        (placement.Host, placement.Location, placement.Rotation, placement.FlipFacing, placement.FlipHand);
                    break;
            }
        }
    }
}
