using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Which rules reach a room: the ones for homes - Approved Document B Volume 1 and BS 5839-6's smoke alarms, Approved Document F Volume 1's
/// extract and purge, Approved Document J's carbon monoxide alarms (2.34, 3.43: a combustion appliance "installed in a dwelling"), M4(2) and
/// M4(3), Approved Document L Volume 1's limiting U-values - or, in a building other than a dwelling, those of the second volumes.
///
/// A house or a block of flats is judged as homes throughout, as before; in a building of any other use, a room named into a dwelling - a flat
/// over a shop - is judged as a home, and every other room by the rules for buildings other than dwellings. In the US the IRC's dwelling rules
/// reach everything.
/// </summary>
public static class Domestic
{
    /// <summary>Whether the rules for homes reach a room - or, given none, the building as a whole.</summary>
    public static bool Rules(BimDocument document, Room? room = null) =>
        document.ProjectInformation.ClearanceStandard.IsUs()
        || FireDesign.UseOf(document) is BuildingUse.House or BuildingUse.Flats
        || room is not null && Dwellings.Of(room).Length > 0;

    /// <summary>Whether the rules for homes reach a fitting: those of the room it stands in.</summary>
    public static bool Rules(BimDocument document, Component piece) =>
        Rules(document, RoomsOf.Containing(document, piece.LevelId, piece.Location));

    /// <summary>Whether the rules for homes reach something between rooms - a wall, a door, a window: a home's where either side is a home.</summary>
    public static bool Rules(BimDocument document, Room? one, Room? other) =>
        Rules(document) || one is not null && Rules(document, one) || other is not null && Rules(document, other);

    /// <summary>Whether the rules for homes reach a door or a window: those of the rooms either side of its wall where it is.</summary>
    public static bool Rules(BimDocument document, Opening opening)
    {
        if (Rules(document)) return true;
        if (document.Find<Wall>(opening.HostWallId) is not { } wall || document.GetWallType(wall) is not { } type) return false;
        var reach = type.Structure.TotalWidth / 2 + 150;
        return new[] { reach, -reach }.Any(side =>
            RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, opening.DistanceAlongWall, side)) is { } room && Rules(document, room));
    }
}
