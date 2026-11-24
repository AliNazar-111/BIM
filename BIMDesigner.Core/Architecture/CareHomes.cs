using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Residential care homes (round 129), by Approved Document B Volume 2 2.33 to 2.46 and Table C1, from its own text: designed for progressive
/// horizontal evacuation (2.34, 2.35) - not a hospital, which Firecode covers (0.10).
///
/// Each storey used for the care of residents - one with a bedroom on it - is divided by compartment walls into three protected areas at least
/// (2.36), its floors compartment floors (8.8, said already); each protected area has two exits at least into adjoining protected areas (2.37), and
/// from any point of it the walk to the exit into the next is within Table 2.1's 9 m one way and 18 m in all, and to a storey exit or final exit 64 m
/// (2.37) - walked on the storey's grid (`HorizontalEscape`); no more than 10 residents' beds in one (2.39). Escape routes do not pass through
/// ancillary accommodation (2.38). Each bedroom is enclosed in REI 30 with E 30 doors (2.42) and holds one single or double bed (2.43); the
/// ancillary accommodation 2.44 lists - stores, cleaners' and disposal rooms, day rooms, kitchens, laundries, linen, plant and staff changing rooms -
/// is enclosed in REI 30, its doors E 30 (Table C1 item 10b); a door in a compartment wall between protected areas is as long as the wall, with
/// smoke seals (Table C1 item 2e). Detection is L1 throughout (2.41), said with the alarms; sprinklers throughout (2.46) are said with the
/// compartments already.
///
/// The compartment walls of protected areas are taken as walls of 60 minutes or more, or typed separating: a bedroom's own enclosure is 30
/// minutes, and a protected area's walls are told from it so - Table B4 lets a care home's compartment walls be 30 minutes where its top storey is
/// 5 m up or less, and there they are to be typed separating. Not done: 2.40's floor area for the occupants of the largest adjoining area, the
/// assessment of staff and dependency (2.33, 2.39), door closers (2.45), Diagram 2.11's routes from each area to a final exit not through the area
/// on fire (2.38), care homes other than for the elderly choosing simultaneous evacuation (2.34).
/// </summary>
public static class CareHomes
{
    /// <summary>A protected area's compartment walls, taken so: minutes; the most residents' beds in one protected area (2.39); the walk to a storey exit (2.37b), mm.</summary>
    public const int CompartmentMinutes = 60, MostBeds = 10;

    public const double StoreyExit = 64000;

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the building is a care home planned for progressive horizontal evacuation: a residential care or nursing home, not a hospital.</summary>
    public static bool Applies(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || !HorizontalEscape.Applies(document)
            || HorizontalEscape.Purpose(document) != PurposeGroup.ResidentialInstitutional) return false;
        var use = document.ProjectInformation.BuildingType ?? string.Empty;
        bool Has(string word) => use.Contains(word, StringComparison.OrdinalIgnoreCase);
        return (Has("care") || Has("nursing") || Has("residential home")) && !Has("hospital") && !Has("hospice");
    }

    /// <summary>A residents' bedroom.</summary>
    public static bool Bedroom(Room room) => Named(room, "bedroom");

    /// <summary>The ancillary accommodation 2.44 lists: chemical, clothes, linen and other stores, cleaners' and disposal rooms, day rooms, smoking rooms, plant rooms, kitchens, laundries, staff changing and locker rooms.</summary>
    public static bool Ancillary(Room room) =>
        Named(room, "store", "cleaner", "disposal", "sluice", "day room", "dayroom", "lounge", "smoking", "plant", "linen", "kitchen", "laundry", "changing", "locker");

    /// <summary>A room an L1 system need not detect in, taken so: a WC, a bathroom, a shower room, an en-suite.</summary>
    public static bool Sanitary(Room room) => Named(room, "wc", "toilet", "bathroom", "shower", "en-suite", "ensuite", "en suite", "washroom", "lavatory");

    private static bool Shaftlike(BimDocument document, Room room) => Doors.StairRooms(document).Contains(room) || Named(room, "stair", "lift", "shaft", "riser", "duct");

    /// <summary>Whether a wall type is a protected area's compartment wall, as it is taken: 60 minutes or more, or typed separating.</summary>
    public static bool Compartment(WallType type) => type.Separating || Doors.FireMinutes(type.FireRating) >= CompartmentMinutes;

    /// <summary>A storey's protected areas: its rooms joined where no compartment wall stands between them - a stair's or a lift's not one - kept while the model does not change.</summary>
    public static IReadOnlyList<List<Room>> Areas(BimDocument document, Guid level)
    {
        IReadOnlyList<List<Room>> Work()
        {
            var rooms = document.All<Room>().Where(room => room.LevelId == level && !MixedUse.Home(document, room) && room.GetBoundary(document) is { IsEnclosed: true } boundary && boundary.Polygon.Count >= 3).ToList();
            var parent = rooms.ToDictionary(room => room, room => room);
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
                if (document.GetWallType(wall) is { } type && type.Function != WallFunction.Exterior && !Compartment(type))
                {
                    var (one, other) = WallRules.RoomsBeside(document, wall);
                    Join(one, other);
                }

            foreach (var line in document.All<RoomSeparator>().Where(line => line.LevelId == level && line.Length > 1))
            {
                var middle = line.Start.MidpointTo(line.End);
                var across = (line.End - line.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
                Join(RoomsOf.Containing(document, level, middle + across * 150), RoomsOf.Containing(document, level, middle - across * 150));
            }

            return rooms.GroupBy(Find).Select(group => group.ToList()).Where(area => !area.All(room => Shaftlike(document, room))).ToList();
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("care homes: areas", document, level, null, Work) : Work();
    }

    /// <summary>The beds in a room: the bed and bunk bed components standing in it.</summary>
    public static int BedsIn(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary
            ? document.All<Component>().Count(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location)
                                                       && document.FindType<ComponentType>(piece.TypeId)?.Form is ComponentForm.Bed or ComponentForm.BunkBed)
            : 0;

    private static Room First(IEnumerable<Room> rooms) => rooms.OrderBy(room => room.Number, NaturalOrder.Instance).ThenBy(room => room.Name, StringComparer.Ordinal).First();

    private static string Names(IReadOnlyCollection<Room> rooms)
    {
        var named = rooms.OrderBy(room => room.Number, NaturalOrder.Instance).ThenBy(room => room.Name, StringComparer.Ordinal).Select(room => $"{room.Name} {room.Number}".Trim()).ToList();
        return named.Count <= 3 ? string.Join(", ", named) : $"{string.Join(", ", named.Take(3))} and {named.Count - 3} more";
    }

    /// <summary>
    /// What a care home's storeys fall short of, each said on a room: protected areas fewer than three (2.36); a protected area with fewer than two
    /// exits into adjoining ones (2.37) or more than ten residents' beds (2.39); a bedroom with more than one bed (2.43); ancillary accommodation the
    /// escape from other rooms passes through (2.38).
    /// </summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!Applies(document)) yield break;
        foreach (var level in document.Levels.OrderBy(level => level.Elevation))
        {
            var areas = Areas(document, level.Id);
            var bedrooms = areas.SelectMany(area => area).Where(Bedroom).ToList();
            if (bedrooms.Count == 0) continue;
            if (areas.Count < 3)
                yield return new EscapeProblem(First(bedrooms),
                    $"its storey is {(areas.Count == 1 ? "one protected area" : $"{areas.Count} protected areas")} - Approved Document B Volume 2 (2.36) asks each storey of a care home used for the care of residents divided by compartment walls into three protected areas at least, for progressive horizontal evacuation; compartment walls taken as those of 60 minutes or more, or typed separating, a bedroom's own enclosure being 30 (2.42)");

            var areaOf = new Dictionary<Room, int>();
            for (var i = 0; i < areas.Count; i++)
                foreach (var room in areas[i]) areaOf[room] = i;
            var doors = document.All<Door>().Where(door => document.Find<Wall>(door.HostWallId)?.LevelId == level.Id)
                .Select(door => (Door: door, Sides: Doors.RoomsBeside(document, door))).ToList();
            for (var i = 0; i < areas.Count; i++)
            {
                var area = areas[i];
                if (areas.Count >= 2)
                {
                    // Into an adjoining protected area, or a storey exit or final exit - outside, or into a stair - as Diagram 2.11 has them.
                    var exits = doors.Count(entry =>
                    {
                        var (a, b) = entry.Sides;
                        var (x, y) = (a is not null && areaOf.TryGetValue(a, out var ax) ? ax : -1, b is not null && areaOf.TryGetValue(b, out var bx) ? bx : -1);
                        if (x != i && y != i || x == y) return false;
                        var other = x == i ? b : a;
                        return other is null || areaOf.ContainsKey(other) || Shaftlike(document, other);
                    });
                    if (exits < 2)
                        yield return new EscapeProblem(First(area),
                            $"its protected area - {Names(area)} - has {(exits == 0 ? "no exit" : "one exit")} - Approved Document B Volume 2 (2.37, Diagram 2.11) asks every protected area of a care home two exits at least, into adjoining protected areas or to a storey exit or final exit");
                }

                var beds = area.Where(Bedroom).Sum(room => Math.Max(1, BedsIn(document, room)));
                if (beds > MostBeds)
                    yield return new EscapeProblem(First(area.Where(Bedroom)),
                        $"its protected area - {Names(area.Where(Bedroom).ToList())} - has {beds} residents' beds - Approved Document B Volume 2 (2.39) allows 10 at most in one protected area, fewer where the staff and the residents' dependency ask it");
            }

            foreach (var bedroom in bedrooms.Where(room => BedsIn(document, room) > 1))
                yield return new EscapeProblem(bedroom, $"{BedsIn(document, bedroom)} beds in it - Approved Document B Volume 2 (2.43) asks a care home's bedroom to hold no more than one single or double bed");

            foreach (var room in HorizontalEscape.PassedThrough(document, level.Id, Ancillary))
                yield return new EscapeProblem(room, $"the escape from other rooms passes through it, ancillary accommodation - Approved Document B Volume 2 (2.38) asks a care home's escape routes not to pass through its stores, kitchens, laundries, day rooms and the like (2.44)");
        }
    }

    /// <summary>What a wall of a care home falls short of: a bedroom's enclosure or ancillary accommodation's, REI 30 (2.42, 2.44).</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (!Applies(document) || type.Function == WallFunction.Exterior || document.IsCurtainWall(wall)) yield break;
        var (one, other) = WallRules.RoomsBeside(document, wall);
        if (one is null || other is null || one == other || Together(document, one, other)) yield break;
        var minutes = Doors.FireMinutes(type.FireRating);
        if (minutes >= 30) yield break;
        var rated = minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated";
        if (Bedroom(one) || Bedroom(other))
            yield return ($"it encloses the {(Bedroom(one) ? one : other).Name}, a care home's bedroom, {rated}.",
                "Approved Document B Volume 2 (2.42) asks each bedroom of a care home enclosed in fire-resisting construction, REI 30, its door E 30.");
        else if (Ancillary(one) || Ancillary(other))
            yield return ($"it encloses the {(Ancillary(one) ? one : other).Name}, ancillary accommodation of a care home, {rated}.",
                "Approved Document B Volume 2 (2.44) asks a care home's ancillary accommodation - stores, cleaners' and disposal rooms, day rooms, kitchens, laundries, plant and staff changing rooms - enclosed in fire-resisting construction, REI 30.");
    }

    /// <summary>
    /// What a door of a care home falls short of: in a compartment wall between protected areas, as long as the wall with smoke seals (Table C1 item
    /// 2e); a bedroom's, E 30 (2.42); ancillary accommodation's, E 30 (Table C1 item 10b).
    /// </summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Door door, DoorType type)
    {
        if (!Applies(document)) yield break;
        var (a, b) = Doors.RoomsBeside(document, door);
        if (a is null || b is null || a == b || Together(document, a, b)) yield break;
        var minutes = Doors.FireMinutes(type.FireRating);
        var rated = minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated";
        if (document.Find<Wall>(door.HostWallId) is { } wall && document.GetWallType(wall) is { } wallType && Compartment(wallType))
        {
            var areas = Areas(document, wall.LevelId);
            int Which(Room room) => areas.Select((area, i) => (area, i)).Where(entry => entry.area.Contains(room)).Select(entry => entry.i).DefaultIfEmpty(-1).First();
            if (Which(a) >= 0 && Which(b) >= 0 && Which(a) != Which(b))
            {
                var asked = Math.Max(CompartmentMinutes, Doors.FireMinutes(wallType.FireRating));
                if (minutes < asked || !FireSeparation.Smoke(type.FireRating))
                    yield return ($"it is in the compartment wall between two protected areas, {(minutes < asked ? rated : $"rated {minutes} minutes without smoke seals (no Sa)")}.",
                        $"Approved Document B Volume 2 (Table C1 item 2e, 2.35) asks a door in a compartment wall as long as the wall - {asked} minutes - with smoke seals, Sa, where it is used for progressive horizontal evacuation.");
                yield break;
            }
        }

        if (minutes >= 30) yield break;
        if (Bedroom(a) || Bedroom(b))
            yield return ($"it is the door of the {(Bedroom(a) ? a : b).Name}, a care home's bedroom, {rated}.", "Approved Document B Volume 2 (2.42) asks each bedroom of a care home a fire-resisting door, E 30.");
        else if (Ancillary(a) || Ancillary(b))
            yield return ($"it is the door of the {(Ancillary(a) ? a : b).Name}, ancillary accommodation of a care home, {rated}.", "Approved Document B Volume 2 (2.44, Table C1 item 10b) asks a door to a care home's ancillary accommodation E 30.");
    }

    /// <summary>Whether two rooms are one bedroom and its own en-suite, or both within it.</summary>
    private static bool Together(BimDocument document, Room one, Room other)
    {
        var suites = Lodging.Suites(document);
        if (suites.TryGetValue(one, out var a) && suites.TryGetValue(other, out var b) && a == b) return true;
        return Bedroom(one) && Sanitary(other) || Bedroom(other) && Sanitary(one);
    }
}
