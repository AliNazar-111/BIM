using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The fire-separating elements of a building other than a home (round 125), as Approved Document B Volume 2 Section 10 and Appendix A have
/// them - where a pipe or duct passing through is to be fire-stopped, sealed, sleeved or dampered: a wall between two of a storey's fire
/// compartments (Section 8, found as round 112 finds them); the enclosure of a protected stairway, of a protected shaft for services - a riser or
/// duct, not a lift shaft - and of a place of special fire hazard; a compartment floor where 8.8 to 8.11 ask one; and any wall or floor typed
/// separating. A pipe within a protected shaft is not asked it (10.2). A home's building keeps Volume 1's separating walls and floors.
/// </summary>
public static class FireSeparating
{
    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a room is a protected shaft for services: a riser, a duct or a service shaft - not a lift's, nor a stair's.</summary>
    public static bool ServiceShaft(Room room) => Named(room, "riser", "duct", "service shaft", "services shaft") && !Named(room, "lift", "stair");

    /// <summary>Whether a room is a protected stairway: a stair's room, so named or a flight's, not a room the stair stands open in.</summary>
    private static bool ProtectedStair(BimDocument document, Room room) =>
        (Named(room, "stair") || Doors.StairRooms(document).Contains(room)) && !HorizontalEscape.OpenStairRoom(document, room);

    /// <summary>
    /// What a wall is as a fire-separating element in a building other than a home - "compartment wall", "enclosure of a protected
    /// stairway", "enclosure of a protected shaft" - with whether it encloses a services shaft (Table 10.1's first row); null where it is
    /// none, or the building is a home's.
    /// </summary>
    public static (string What, bool Shaft)? Of(BimDocument document, Wall wall)
    {
        if (!HorizontalEscape.Applies(document) || document.GetWallType(wall) is not { } type) return null;
        if (type.Separating) return ("separating wall", false);
        if (type.Function == WallFunction.Exterior || document.IsCurtainWall(wall)) return null;
        var (one, other) = WallRules.RoomsBeside(document, wall);
        if (one is null || other is null || one == other) return null;
        if (ServiceShaft(one) != ServiceShaft(other)) return ("enclosure of a protected shaft", true);
        if (ProtectedStair(document, one) != ProtectedStair(document, other)) return ("enclosure of a protected stairway", false);
        if (FireSeparation.Special(one) != FireSeparation.Special(other)) return ("enclosure of a place of special fire hazard", false);
        var minutes = Math.Max(30, FireCompartments.Minutes(document));
        IReadOnlyList<(List<Room> Rooms, double Area)> Work() => FireCompartments.Compartments(document, wall.LevelId, minutes);
        var compartments = ModelCache.Of(document) is { } cache ? cache.Keep("fire separating: compartments", document, wall.LevelId, minutes, Work) : Work();
        int Which(Room room) => compartments.Select((compartment, i) => (compartment, i)).Where(entry => entry.compartment.Rooms.Contains(room)).Select(entry => entry.i).DefaultIfEmpty(-1).First();
        return compartments.Count > 1 && Which(one) >= 0 && Which(other) >= 0 && Which(one) != Which(other) ? ("compartment wall", false) : null;
    }

    /// <summary>What a floor is as a fire-separating element in a building other than a home: a compartment floor where 8.8 to 8.11 ask one; null otherwise.</summary>
    public static string? Of(BimDocument document, Floor floor)
    {
        if (!HorizontalEscape.Applies(document)) return null;
        if (document.FindType<SlabType>(floor.TypeId) is { Separating: true }) return "separating floor";
        return FireCompartments.CompartmentFloor(document, floor) is not null ? "compartment floor" : null;
    }

    /// <summary>"a compartment wall", "the enclosure of a protected stairway": an element's name with its article.</summary>
    public static string Article(string what) => what.StartsWith("enclosure", StringComparison.Ordinal) ? $"the {what}" : $"a {what}";

    /// <summary>Whether a point is inside a protected shaft for services on a storey, where a pipe needs no fire-stopping of its own (10.2).</summary>
    public static bool InShaft(BimDocument document, Guid level, Point2D at) => RoomsOf.Containing(document, level, at) is { } room && ServiceShaft(room);
}
