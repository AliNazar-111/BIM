using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Fire separation in a building other than a dwelling (round 112), as Approved Document B Volume 2 sets it out in Sections 7, 8 and 10 and
/// Appendices B and C.
///
/// The elements of structure's period by Table B2 for the building's purpose group, the height of its top floor and its sprinklers (FireDesign);
/// every floor not on the ground to it, a compartment floor where 8.8, 8.10 or 8.11 asks - every floor of a residential (institutional) or other
/// residential building, every floor of one whose top storey is more than 30 m up, the floor of the ground storey over a basement, the floors of
/// basements more than 10 m deep (SlabRules) - the floor over a basement the more of its two periods. Each storey's compartments - its rooms
/// joined through walls, doors and room separators that do not stand the building's period - no larger than Table 8.1 allows: an office any
/// size; a shop 2000 m², 4000 with sprinklers - a single storey with sprinklers any; assembly and recreation any on one storey, 2000 m² a
/// storey on more, 4000 with sprinklers; industrial 7000 m², 14,000 with sprinklers, a storey 18 m up 2000 and 4000. A place of special fire
/// hazard - a boiler, transformer or switch room, fuel or an engine - enclosed in REI 30, its doors E 30 (8.7, Table C1 item 10a). Sprinklers
/// throughout a building of purpose groups 3 to 7 whose top storey is more than 30 m up (Table B2 note 6), and in a care home (2.46).
///
/// Not done: storage buildings' compartment volumes; separate occupancies (8.11e); shops' store rooms (5.46); two storey shops and industrial
/// buildings with an ancillary upper storey (8.12); atria (8.13); protected shafts other than stairs (8.6); doors in compartment walls by Table
/// C1 item 2 - their walls' being found here only as the rooms they divide.
/// </summary>
public static class FireCompartments
{
    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Table 8.1's largest storey of a compartment for a building, m², by its purpose group, its storeys, sprinklers and a storey's height; null for no limit.</summary>
    public static double? Largest(PurposeGroup group, bool single, bool sprinklered, double storeyHeight) => group switch
    {
        PurposeGroup.ShopAndCommercial => sprinklered ? single ? null : 4000 : 2000,
        PurposeGroup.AssemblyAndRecreation => single ? null : sprinklered ? 4000 : 2000,
        PurposeGroup.Industrial => storeyHeight >= 18000 - 0.5 ? sprinklered ? 4000 : 2000 : sprinklered ? 14000 : 7000,
        _ => null
    };

    /// <summary>The period a building's elements of structure stand by Table B2, minutes - the floor over a basement the more of its two.</summary>
    public static int Minutes(BimDocument document) => FireDesign.PeriodOf(document).Minutes;

    /// <summary>
    /// The compartment floors 8.8, 8.10 and 8.11 ask of a building other than a dwelling, said with why: every floor of a residential building;
    /// every floor over 30 m; the ground storey's floor over a basement; the floors between basements more than 10 m deep. Null for none.
    /// </summary>
    public static string? CompartmentFloor(BimDocument document, Floor floor) => CompartmentFloorAt(document, document.FindLevel(floor.LevelId)?.Elevation ?? 0);

    /// <summary>The compartment floor 8.8, 8.10 or 8.11 asks at a storey's level, said with why - a lift well through it a protected shaft (5.35); null for none.</summary>
    public static string? CompartmentFloorAt(BimDocument document, double level)
    {
        if (!HorizontalEscape.Applies(document)) return null;
        var group = HorizontalEscape.Purpose(document);
        if (group == PurposeGroup.ResidentialInstitutional) return "every floor of a residential (institutional) building a compartment floor (8.8)";
        if (group == PurposeGroup.ResidentialOther) return "every floor of an other residential building a compartment floor (8.10)";
        var ground = Escape.Ground(document);
        var (_, top) = FireDesign.HeightOf(document);
        if (top > 30 + 1e-6) return "every floor a compartment floor, the top storey more than 30 m up (8.11b)";
        var lowest = document.Levels.Select(each => each.Elevation).DefaultIfEmpty(0).Min();
        if (Math.Abs(level - ground) <= 100 && lowest < ground - Escape.Basement - 0.5 && !HorizontalEscape.SmallPremises(document).Small) return "the ground storey's floor over a basement a compartment floor (8.11c)";
        if (level < ground - 100 && lowest < ground - 10000 - 0.5 && level > lowest + 100) return "a basement's floor a compartment floor, a basement more than 10 m deep (8.11d)";
        return null;
    }

    /// <summary>
    /// A storey's compartments: its rooms joined where a wall, a door or a room separator between them does not stand the building's period;
    /// each with its area, m².
    /// </summary>
    public static IReadOnlyList<(List<Room> Rooms, double Area)> Compartments(BimDocument document, Guid level, int minutes)
    {
        var rooms = document.All<Room>().Where(room => room.LevelId == level && !MixedUse.Home(document, room)).Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => entry.Boundary.Polygon);
        var parent = rooms.Keys.ToDictionary(room => room, room => room);
        Room Find(Room room)
        {
            while (parent[room] != room) room = parent[room] = parent[parent[room]];
            return room;
        }

        void Join(Room? a, Room? b)
        {
            if (a is not null && b is not null && a != b && parent.ContainsKey(a) && parent.ContainsKey(b)) parent[Find(a)] = Find(b);
        }

        foreach (var wall in document.Walls.Where(wall => wall.LevelId == level))
            if (document.GetWallType(wall) is { } type && Doors.FireMinutes(type.FireRating) < minutes && type.Function != WallFunction.Exterior)
            {
                var (one, other) = WallRules.RoomsBeside(document, wall);
                Join(one, other);
            }

        foreach (var door in document.All<Door>().Where(door => document.Find<Wall>(door.HostWallId)?.LevelId == level))
            if (document.FindType<DoorType>(door.TypeId) is { } type && Doors.FireMinutes(type.FireRating) < minutes)
            {
                var (one, other) = Doors.RoomsBeside(document, door);
                Join(one, other);
            }

        foreach (var line in document.All<RoomSeparator>().Where(line => line.LevelId == level && line.Length > 1))
        {
            var middle = line.Start.MidpointTo(line.End);
            var across = (line.End - line.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
            Join(RoomsOf.Containing(document, level, middle + across * 150), RoomsOf.Containing(document, level, middle - across * 150));
        }

        return rooms.Keys.GroupBy(Find).Select(group => (group.ToList(), group.Sum(room => Polygon2D.Area(rooms[room]) / 1e6))).ToList();
    }

    /// <summary>
    /// What a building other than a dwelling falls short of in its compartments and sprinklers, each said on a room: a compartment larger than
    /// Table 8.1 allows, said on its first room; sprinklers wanting, said on the top storey's first room.
    /// </summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!HorizontalEscape.Applies(document)) yield break;
        var group = HorizontalEscape.Purpose(document);
        var sprinklered = document.ProjectInformation.Sprinklered;
        var ground = Escape.Ground(document);
        var minutes = Math.Max(30, Minutes(document));
        var storeys = HorizontalEscape.Storeys(document).Keys.Select(level => document.FindLevel(level)).OfType<Datums.Level>().OrderBy(level => level.Elevation).ToList();
        if (storeys.Count == 0) yield break;
        Room First(IEnumerable<Room> rooms) => rooms.OrderBy(room => room.Number, StringComparer.Ordinal).ThenBy(room => room.Name, StringComparer.Ordinal).First();

        foreach (var level in storeys)
        {
            if (Largest(group, storeys.Count == 1, sprinklered, level.Elevation - ground) is not { } largest) continue;
            foreach (var (rooms, area) in Compartments(document, level.Id, minutes).Where(compartment => compartment.Area > largest + 0.5))
                yield return new EscapeProblem(First(rooms),
                    $"the {level.Name} is {area:0} m² in one compartment - {rooms.Count} room{(rooms.Count == 1 ? string.Empty : "s")} joined through walls, doors or room separators of less than {minutes} minutes - Approved Document B Volume 2 (8.11a, Table 8.1) allows {largest:0} m² a storey of a compartment for {HorizontalEscape.Said(group)}{(sprinklered ? " with sprinklers" : " without sprinklers")}: divide it by compartment walls of {minutes} minutes, their doors the same{(sprinklered ? string.Empty : ", or fit sprinklers throughout")}");
        }

        // Sprinklers: purpose groups 3 to 7 with a top storey more than 30 m up (Table B2 note 6), and care homes (2.46).
        var (_, top) = FireDesign.HeightOf(document);
        var care = group == PurposeGroup.ResidentialInstitutional && Toilets.WordsOf(document.ProjectInformation.BuildingType).Overlaps(new[] { "care", "nursing" });
        var tall = group is not (PurposeGroup.ResidentialInstitutional or PurposeGroup.ResidentialOther) && top > 30 + 1e-6;
        if (!sprinklered && (tall || care))
        {
            var highest = storeys[^1];
            var rooms = HorizontalEscape.Storeys(document)[highest.Id].Rooms.Keys;
            if (rooms.Any())
                yield return new EscapeProblem(First(rooms), tall
                    ? $"its top storey is {top:0.0} m up and the building has no sprinklers - Approved Document B Volume 2 (Table B2 note 6) asks a building of {HorizontalEscape.Said(group)} with a top storey more than 30 m up fitted with sprinklers throughout, by Appendix E: tick Sprinklered in the plan's Properties once it is"
                    : "it is a care home without sprinklers - Approved Document B Volume 2 (2.46) asks care homes fitted with sprinklers throughout, by Appendix E: tick Sprinklered in the plan's Properties once it is");
        }
    }

    /// <summary>Whether a wall encloses a place of special fire hazard in a building other than a dwelling: an inside wall with one on one side, something else on the other.</summary>
    private static Room? Hazard(BimDocument document, Wall wall, WallType type)
    {
        if (!HorizontalEscape.Applies(document) || type.Function != WallFunction.Interior) return null;
        var (one, other) = WallRules.RoomsBeside(document, wall);
        if (one is null || other is null || one == other) return null;
        return FireSeparation.Special(one) && !FireSeparation.Special(other) ? one : FireSeparation.Special(other) && !FireSeparation.Special(one) ? other : null;
    }

    /// <summary>What a wall falls short of enclosing a place of special fire hazard: REI 30 (8.7).</summary>
    public static (string Said, string Rule)? Problem(BimDocument document, Wall wall, WallType type) =>
        Hazard(document, wall, type) is { } room && Doors.FireMinutes(type.FireRating) < 30
            ? ($"it encloses the {room.Name}, a place of special fire hazard, {(Doors.FireMinutes(type.FireRating) is > 0 and var rated ? $"rated {rated} minutes" : "not fire-rated")}.",
                "Approved Document B Volume 2 (8.7, Table B1) asks fire-resisting construction of REI 30 at least round a place of special fire hazard - a boiler room, a transformer or switch room, a fuel store, an engine.")
            : null;

    /// <summary>What a door falls short of into a place of special fire hazard: E 30 (Table C1 item 10a).</summary>
    public static (string Said, string Rule)? Problem(BimDocument document, Door door, DoorType type)
    {
        if (!HorizontalEscape.Applies(document) || Doors.FireMinutes(type.FireRating) >= 30) return null;
        var (one, other) = Doors.RoomsBeside(document, door);
        var room = one is not null && FireSeparation.Special(one) && (other is null || !FireSeparation.Special(other)) ? one
            : other is not null && FireSeparation.Special(other) && (one is null || !FireSeparation.Special(one)) ? other : null;
        return room is null ? null
            : ($"it opens into the {room.Name}, a place of special fire hazard, {(Doors.FireMinutes(type.FireRating) is > 0 and var rated ? $"rated {rated} minutes" : "not fire-rated")}.",
                "Approved Document B Volume 2 (Table C1 item 10a) asks a door in the enclosure of a place of special fire hazard for E 30 - FD 30.");
    }
}
