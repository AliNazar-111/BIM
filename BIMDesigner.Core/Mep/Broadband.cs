using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// Infrastructure for electronic communications (round 126), as Approved Document R (2022) sets it out for new buildings in England.
///
/// New homes - a house, a block of flats, the dwellings of a building of mixed use (Volume 1, RA1, RA2): in each dwelling a network
/// termination point (RA1(1), 2.6), with a fixed electrical supply at it - a socket within 1 m of it, taken so (2.7) - and its controls where
/// they are easy to reach, 450 to 1200 above the floor as Part M has switches and sockets (2.8, M4(1) 1.18); and the building an access point
/// the operator's network comes in at (RA1(1)(d), 1.5, Diagram 5) - in a building of more than one dwelling a common access point serving
/// them all, outside every dwelling (RA1(2), 1.26, Diagram 6). Rooms for residential purposes - a hotel, hostel, boarding house, hospital - are
/// not dwellings (1.6c).
///
/// A new building other than a home (Volume 2, R1, 1.6): a suitable position for one network termination point at least, and an access point
/// it is ducted to. Not done: the ducts, risers and trays between them (1.19 to 1.21, Diagram 6), drawn only as the points; the connection to a
/// gigabit-capable network itself, its cost cap and the connectivity plan (RA2, 2.13 to 2.23, Section 3); isolated areas and material change of
/// use (1.6b, f).
/// </summary>
public static class Broadband
{
    /// <summary>How near a network termination point a socket gives it its fixed supply, mm - taken so; the heights Part M has controls at, mm.</summary>
    public const double Supply = 1000, LowestControl = 450, HighestControl = 1200;

    /// <summary>What a model's communications infrastructure falls short of in Approved Document R.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) yield break;
        var rooms = document.All<Room>().Where(room => room.GetBoundary(document) is { IsEnclosed: true }).OrderBy(room => room.Number, StringComparer.Ordinal).ThenBy(room => room.Name, StringComparer.Ordinal).ToList();
        if (rooms.Count == 0) yield break;
        var pieces = document.All<Component>().Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId))).Where(entry => entry.Type is not null)
            .Select(entry => (entry.Piece, Type: entry.Type!)).ToList();
        var terminations = pieces.Where(entry => entry.Type.Form == ComponentForm.NetworkTerminationPoint).ToList();
        var access = pieces.Where(entry => entry.Type.Form == ComponentForm.CommsAccessPoint).ToList();
        var sockets = pieces.Where(entry => entry.Type.Form == ComponentForm.Socket).Select(entry => entry.Piece).ToList();

        // The homes: each dwelling named, or - in a house or a block of flats with none named - the one home of all its rooms.
        var named = Dwellings.Named(document);
        var homes = named.Count > 0 ? named.Select(home => (home.Name, home.Rooms)).ToList()
            : FireDesign.UseOf(document) is BuildingUse.House or BuildingUse.Flats ? new List<(string Name, IReadOnlyList<Room> Rooms)> { ("The home", rooms) }
            : new List<(string Name, IReadOnlyList<Room> Rooms)>();
        string? Home(Component piece) => RoomsOf.Containing(document, piece.LevelId, piece.Location) is { } room
            ? homes.FirstOrDefault(home => home.Rooms.Contains(room)).Name : null;

        // Each network termination point: power at it, its controls within reach (2.7, 2.8).
        foreach (var (point, type) in terminations)
        {
            if (!sockets.Any(socket => socket.LevelId == point.LevelId && socket.Location.DistanceTo(point.Location) <= Supply + 0.5))
                yield return new MepProblem(point, $"no socket within {Supply / 1000:0} m of it - Approved Document R ({(homes.Count > 0 ? "Volume 1, 2.7" : "Volume 2, 1.6")}) asks a fixed electrical supply for the network termination point and its equipment at it - taken as a socket within 1 m");
            var middle = point.Elevation + type.Height / 2;
            if (homes.Count > 0 && (middle < LowestControl - 0.5 || middle > HighestControl + 0.5))
                yield return new MepProblem(point, $"its middle is {middle:0} above the floor - Approved Document R (Volume 1, 2.8) asks its controls where they are easy to reach, as Approved Document M has switches and sockets: 450 to 1200 above the floor");
        }

        if (homes.Count > 0)
        {
            // A network termination point in each new home (RA1(1), 2.6).
            foreach (var (name, own) in homes)
                if (!terminations.Any(entry => Home(entry.Piece) == name))
                    yield return new MepProblem(own[0], $"{(homes.Count == 1 && named.Count == 0 ? "the home" : name)} has no network termination point - Approved Document R (Volume 1, RA1, 2.6) asks at least one in a suitable position in each new dwelling, gigabit-ready infrastructure from it to the access point and on to the network");

            // An access point - a common one, outside every dwelling, in a building of more than one (RA1(1)(d), RA1(2), 1.5, 1.26).
            if (homes.Count > 1)
            {
                if (!access.Any(entry => Home(entry.Piece) is null))
                    yield return new MepProblem(rooms[0], $"no common access point for the building's {homes.Count} dwellings - Approved Document R (Volume 1, RA1(2), 1.5, 1.26, Diagram 6) asks a building of more than one dwelling a common access point outside them, in or on the building, with ducts or trays in a riser to the network termination point in each");
            }
            else if (access.Count == 0)
                yield return new MepProblem(rooms[0], "no access point for electronic communications networks - Approved Document R (Volume 1, RA1(1)(d), 1.5, Diagram 5) asks the home an access point, on its outside wall with a duct through the wall to the network termination point, or the gigabit-ready infrastructure carried to the network's distribution point");
            yield break;
        }

        // A new building other than a home (Volume 2, R1, 1.6).
        if (!HorizontalEscape.Applies(document)) yield break;
        if (terminations.Count == 0)
            yield return new MepProblem(rooms[0], "no network termination point - Approved Document R (Volume 2, R1, 1.6) asks a suitable position for at least one in a new building, ducted to an access point, so that high-speed networks can be put in");
        if (access.Count == 0)
            yield return new MepProblem(rooms[0], "no access point for electronic communications networks - Approved Document R (Volume 2, R1, 1.6, 1.7, Diagram 1) asks one for the network termination point to be ducted to, on an outside wall with a duct through it");
    }
}
