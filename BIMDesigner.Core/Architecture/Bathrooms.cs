using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A bathroom's fittings and its electrics, checked as the regulations check them.
///
/// In the UK, BS 7671's section 701: no socket within 2.5 m of the edge of a bath or shower
/// (701.512.3) - a shaver supply unit alone may be nearer - and no switch within its zones, 600
/// of it (a pull cord instead). In the US, the NEC: no receptacle within 3 ft (914) of a bathtub
/// or shower (406.9(C)), a receptacle within 3 ft of each basin (210.52(D)), no pendant within
/// 3 ft of a tub and 8 ft over it (410.10(D)); and the IRC's smallest shower, 30 in (762) across
/// and 900 sq in (P2708.1).
///
/// And room by room, as Approved Document G asks: a WC with a basin in its room, or in the room
/// next to it that is its only way in and not a kitchen (G4, 4.10); and never a WC in a room where
/// food is prepared, a door between them (G4(3), 4.11).
/// </summary>
public static class Bathrooms
{
    /// <summary>Whether a piece is a WC.</summary>
    public static bool IsWc(ComponentType type) => type.Form is ComponentForm.Toilet or ComponentForm.WallHungToilet;

    /// <summary>
    /// The baths and showers in the same room as a piece - on its storey, where there are no
    /// rooms - by their outlines in plan, and how high their rims are. A socket on the far side of
    /// the bathroom's wall is not in the bathroom.
    /// </summary>
    private static List<(string Name, IReadOnlyList<Point2D> Outline, double Rim)> Wet(BimDocument document, Component near)
    {
        var room = RoomsOf.Containing(document, near.LevelId, near.Location);
        return document.All<Component>()
            .Where(piece => piece.LevelId == near.LevelId && (room is null || RoomsOf.Containing(document, piece.LevelId, piece.Location) == room))
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type?.Form is ComponentForm.Bath or ComponentForm.Shower)
            .Select(entry => (entry.Type!.Name, ComponentModel.Footprint(entry.Type, ComponentModel.FrameOf(entry.Piece, 0)),
                ComponentHosting.BaseElevation(document, entry.Piece, entry.Type) + (entry.Type.Form == ComponentForm.Bath ? entry.Type.Height : ComponentModel.ShowerTray(entry.Type))))
            .ToList();
    }

    /// <summary>What is wrong with where one bathroom fitting, socket, switch or light is, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Component piece, ClearanceStandard standard)
    {
        if (document.FindType<ComponentType>(piece.TypeId) is not { } type) return Array.Empty<PlacementProblem>();

        var us = standard.IsUs();
        var footprint = ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0));
        var problems = new List<PlacementProblem>();

        // The nearest bath or shower, and how far it is, edge to edge.
        (string Name, double Apart, double Rim)? NearestWet()
        {
            var wet = Wet(document, piece);
            return wet.Count == 0 ? null : wet.Select(entry => (entry.Name, Kitchens.Apart(entry.Outline, footprint), entry.Rim)).MinBy(entry => entry.Item2);
        }

        switch (type.Form)
        {
            case ComponentForm.Socket when NearestWet() is { } wet && wet.Apart < (us ? 914 : 2500) - 0.5:
                problems.Add(new PlacementProblem(piece, $"it is {Units.Bare(wet.Apart)} from {wet.Name}.",
                    us
                        ? $"NEC 406.9(C) allows no receptacle within 3 ft{Units.MetricAside("914")} of a bathtub or shower, nor 8 ft over it."
                        : "BS 7671 (701.512.3) keeps a socket 2.5 m from the edge of a bath or shower; only a shaver supply unit may be nearer.",
                    footprint));
                break;

            case ComponentForm.Switch when !us && NearestWet() is { } wet && wet.Apart < 600 - 0.5:
                problems.Add(new PlacementProblem(piece, $"it is {wet.Apart:0} from {wet.Name}, in its zone 2.",
                    "BS 7671 section 701 keeps switches out of zones 0, 1 and 2 - within 600 of a bath or shower: a pull cord there instead.",
                    footprint));
                break;

            case ComponentForm.CeilingLight when us && type.Height > 250 && NearestWet() is { } wet && wet.Apart < 914 - 0.5
                && ComponentHosting.BaseElevation(document, piece, type) < wet.Rim + 2438:
                problems.Add(new PlacementProblem(piece, $"it hangs {Units.Bare(wet.Apart)} from {wet.Name}.",
                    $"NEC 410.10(D) allows no pendant within 3 ft{Units.MetricAside("914")} of a bathtub or shower and 8 ft{Units.MetricAside("2438")} over its rim.",
                    footprint));
                break;

            case ComponentForm.Basin when us:
            {
                var sockets = document.All<Component>()
                    .Where(other => other.LevelId == piece.LevelId && document.FindType<ComponentType>(other.TypeId)?.Form == ComponentForm.Socket)
                    .Select(other => ComponentModel.Footprint(document.FindType<ComponentType>(other.TypeId)!, ComponentModel.FrameOf(other, 0)));
                if (!sockets.Any(socket => Kitchens.Apart(socket, footprint) <= 914 + 0.5))
                    problems.Add(new PlacementProblem(piece, $"there is no receptacle within 3 ft{Units.MetricAside("914")} of it.",
                        $"NEC 210.52(D) asks for a receptacle within 3 ft{Units.MetricAside("914")} of the outside edge of each basin.", footprint));
                break;
            }

            case ComponentForm.Shower when us && (Math.Min(type.Width, type.Depth) < 762 - 0.5 || type.Width * type.Depth < 580645):
                problems.Add(new PlacementProblem(piece, $"at {Units.Bare(type.Width)} x {Units.Bare(type.Depth)} it is too small to shower in.",
                    $"IRC P2708.1 asks for a shower 30 in{Units.MetricAside("762")} across at least, and 900 sq in{Units.MetricAside("0.58 m²")}.", footprint));
                break;
        }

        return problems;
    }

    /// <summary>Every bathroom fitting's, socket's, switch's and light's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Component>()
            .Where(piece => piece.LevelId == levelId)
            .SelectMany(piece => Problems(document, piece, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(piece, "its place", problem.Outline, problem.Rule), string.Empty,
                    document.FindType<ComponentType>(piece.TypeId)?.Name ?? "A fitting", problem.Said)))
            .ToList();

    /// <summary>
    /// The rooms a room's doors open onto, each with its door - none for one opening outside - and
    /// how many doors it has.
    /// </summary>
    public static (IReadOnlyList<Room> Through, int Doors) Neighbours(BimDocument document, Room room)
    {
        var through = new List<Room>();
        var doors = 0;
        foreach (var door in document.All<Door>())
        {
            if (document.Find<Wall>(door.HostWallId) is not { } wall || wall.LevelId != room.LevelId) continue;
            if (document.GetWallType(wall) is not { } type) continue;

            var reach = type.Structure.TotalWidth / 2 + 150;
            var (one, other) = (RoomsOf.Containing(document, room.LevelId, wall.PointAt(type.Structure, door.DistanceAlongWall, reach)),
                RoomsOf.Containing(document, room.LevelId, wall.PointAt(type.Structure, door.DistanceAlongWall, -reach)));
            if (one != room && other != room) continue;

            doors++;
            if ((one == room ? other : one) is { } next && next != room) through.Add(next);
        }

        return (through, doors);
    }

    /// <summary>What is in a room, by form: the components on its storey standing in it.</summary>
    private static List<ComponentType> In(BimDocument document, Room room, IReadOnlyList<Point2D> outline) =>
        document.All<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(outline, piece.Location))
            .Select(piece => document.FindType<ComponentType>(piece.TypeId))
            .OfType<ComponentType>()
            .ToList();

    /// <summary>Whether food is prepared in a room: it has a kitchen sink or a hob in it.</summary>
    private static bool Kitchen(IEnumerable<ComponentType> types) => types.Any(type => type.Form is ComponentForm.Sink or ComponentForm.Cooker);

    /// <summary>
    /// What a room comes to as a WC or a bathroom - "Bathroom: bath, basin and WC" - with what
    /// Approved Document G asks of it that it falls short of; null for a room with neither a WC
    /// nor a bath or shower in it. The IRC asks a home for a WC, a lavatory and a bath or shower
    /// (R306.1), not where they are, so a US room is only described.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;

        var types = In(document, room, boundary.Polygon);
        var (wc, basin, bath, shower) = (types.Any(IsWc), types.Any(type => type.Form == ComponentForm.Basin),
            types.Any(type => type.Form == ComponentForm.Bath), types.Any(type => type.Form == ComponentForm.Shower));
        if (!wc && !bath && !shower) return null;

        var fittings = new[] { bath ? "bath" : null, shower ? "shower" : null, basin ? "basin" : null, wc ? "WC" : null }.OfType<string>().ToList();
        var said = $"{(bath || shower ? "Bathroom" : "WC")}: {(fittings.Count == 1 ? fittings[0] : $"{string.Join(", ", fittings.SkipLast(1))} and {fittings[^1]}")}";
        if (!wc || document.ProjectInformation.ClearanceStandard.IsUs()) return said;

        var wanting = new List<string>();
        if (Kitchen(types))
            wanting.Add("a WC where food is prepared - Approved Document G (G4(3), 4.11) asks for a door between them");

        if (!basin)
        {
            // A basin in the room next to it that is its only way in, and not a kitchen, will do.
            var (through, doors) = Neighbours(document, room);
            var lobby = doors == 1 && through.Count == 1 && through[0].GetBoundary(document) is { IsEnclosed: true } next
                && In(document, through[0], next.Polygon) is var there && there.Any(type => type.Form == ComponentForm.Basin) && !Kitchen(there);
            if (lobby) said += $", its basin in {RoomsOf.Label(through[0])}";
            else wanting.Add("no basin - Approved Document G (G4, 4.10) asks for one in the WC's room, or in the room next to it that is its only way in and not a kitchen");
        }

        return wanting.Count == 0 ? said : $"{said} - {string.Join("; ", wanting)}";
    }
}
