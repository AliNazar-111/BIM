using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

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
/// Round 134, the rest of Section 8: a storage building's compartments by Table 8.1 - on one storey 20,000 m² and 18 m high, any with
/// sprinklers; on more, 20,000 m³ a compartment, 4000 where it reaches a storey 18 m up, 40,000 and 8000 with sprinklers - storeys joined into
/// one compartment where the floor between is not of the building's period or has an opening through it; a two storey shop or factory whose
/// upper storey is ancillary - a fifth of the ground storey's area at most, 500 m², compartmented from it and with its own way out - its ground
/// storey taken as a single storey (8.12); in a shop, factory or store, every wall between separate occupancies REI 60 or the building's
/// period, the greater, its doors the same, and every floor between them a compartment floor (8.11e, 8.30, Table B1 item 6b, Table C1 item 2e);
/// protected shafts - a stair, lift or service riser passing through a compartment floor - their walls of the building's period, a stair's
/// 30 towards a protected lobby or corridor, a stair's doors E 30 Sa in a hotel, assembly building or office and half the period elsewhere,
/// a service riser's half the period, 30 at least (8.6, 8.32, 8.33, Table B1 item 8, Table C1 item 2b to 2d); no ventilating duct in a stair's
/// or lift's protected shaft (8.36b) and a gas pipe in one of steel (8.37a).
///
/// Not done: atria (8.13, BS 9999 not seen); compartment walls running the full height and through the roof space (8.18 to 8.21), and the
/// roof either side of them (8.26 to 8.29); oil pipes, there being none (8.36a); a gas shaft's ventilation (8.38, BS 8313 not seen); a stair's
/// 8.34 glazed screen; car parks for light vehicles.
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
    public static string? CompartmentFloor(BimDocument document, Floor floor) => CompartmentFloorAt(document, document.FindLevel(floor.LevelId)?.Elevation ?? 0) ?? Occupancies(document, floor);

    /// <summary>The compartment floor 8.8, 8.10 or 8.11 asks at a storey's level, said with why - a lift well through it a protected shaft (5.35); null for none.</summary>
    public static string? CompartmentFloorAt(BimDocument document, double level)
    {
        if (!HorizontalEscape.Applies(document)) return null;
        var group = HorizontalEscape.Purpose(document);
        if (group == PurposeGroup.ResidentialInstitutional) return "every floor of a residential (institutional) building a compartment floor (8.8)";
        if (group == PurposeGroup.ResidentialOther) return "every floor of an other residential building a compartment floor (8.10)";
        if (VerticalEscape.Phased(document)) return "every floor a compartment floor, the building designed for phased evacuation (3.21c)";
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

        // A two storey shop or factory whose upper storey is ancillary: its ground storey a single storey for compartmentation (8.12).
        var ancillary = Ancillary(document, storeys, minutes);

        // A storage building: one storey by its area and height, more by its compartments' volumes (Table 8.1).
        if (group == PurposeGroup.Storage)
        {
            foreach (var problem in Storage(document, storeys, minutes, sprinklered, ground)) yield return problem;
        }
        else
        foreach (var level in storeys)
        {
            if (Largest(group, storeys.Count == 1 || ancillary && Math.Abs(level.Elevation - ground) <= 100, sprinklered, level.Elevation - ground) is not { } largest) continue;
            foreach (var (rooms, area) in Compartments(document, level.Id, minutes).Where(compartment => compartment.Area > largest + 0.5))
                yield return new EscapeProblem(First(rooms),
                    $"the {level.Name} is {area:0} m² in one compartment - {rooms.Count} room{(rooms.Count == 1 ? string.Empty : "s")} joined through walls, doors or room separators of less than {minutes} minutes - Approved Document B Volume 2 (8.11a, Table 8.1) allows {largest:0} m² a storey of a compartment for {HorizontalEscape.Said(group)}{(sprinklered ? " with sprinklers" : " without sprinklers")}: divide it by compartment walls of {minutes} minutes, their doors the same{(sprinklered ? string.Empty : ", or fit sprinklers throughout")}");
        }

        // Sprinklers: purpose groups 3 to 7 with a top storey more than 30 m up (Table B2 note 6), and care homes (2.46).
        var (_, top) = FireDesign.HeightOf(document);
        var care = group == PurposeGroup.ResidentialInstitutional && Toilets.WordsOf(document.ProjectInformation.BuildingType).Overlaps(new[] { "care", "nursing" });
        var tall = group is not (PurposeGroup.ResidentialInstitutional or PurposeGroup.ResidentialOther) && top > 30 + 1e-6;
        var phased = !tall && VerticalEscape.Phased(document) && top > 30 + 1e-6;
        if (!sprinklered && (tall || care || phased))
        {
            var highest = storeys[^1];
            var rooms = HorizontalEscape.Storeys(document)[highest.Id].Rooms.Keys;
            if (rooms.Any())
                yield return new EscapeProblem(First(rooms), phased
                    ? $"its top storey is {top:0.0} m up, the building designed for phased evacuation, and it has no sprinklers - Approved Document B Volume 2 (3.21d) asks a building evacuated in phases with a storey more than 30 m up fitted with sprinklers throughout, by Appendix E: tick Sprinklered in the plan's Properties once it is"
                    : tall
                    ? $"its top storey is {top:0.0} m up and the building has no sprinklers - Approved Document B Volume 2 (Table B2 note 6) asks a building of {HorizontalEscape.Said(group)} with a top storey more than 30 m up fitted with sprinklers throughout, by Appendix E: tick Sprinklered in the plan's Properties once it is"
                    : "it is a care home without sprinklers - Approved Document B Volume 2 (2.46) asks care homes fitted with sprinklers throughout, by Appendix E: tick Sprinklered in the plan's Properties once it is");
        }
    }

    /// <summary>
    /// Whether a two storey shop or factory's upper storey is ancillary to its ground storey as 8.12 has it: its area a fifth of the ground
    /// storey's at most and 500 m², its floor of the building's period, and every stair from it with a door out of its foot of its own.
    /// </summary>
    private static bool Ancillary(BimDocument document, IReadOnlyList<Datums.Level> storeys, int minutes)
    {
        var group = HorizontalEscape.Purpose(document);
        if (group is not (PurposeGroup.ShopAndCommercial or PurposeGroup.Industrial) || storeys.Count != 2 || Math.Abs(storeys[0].Elevation - Escape.Ground(document)) > 100) return false;
        double Area(Datums.Level level) => document.All<Room>().Where(room => room.LevelId == level.Id && room.GetBoundary(document) is { IsEnclosed: true }).Sum(room => room.GetBoundary(document).Area) / 1e6;
        var (lower, upper) = (Area(storeys[0]), Area(storeys[1]));
        if (upper > Math.Min(0.2 * lower, 500) + 1e-6) return false;
        var floors = document.All<Floor>().Where(floor => floor.LevelId == storeys[1].Id).ToList();
        if (floors.Count == 0 || floors.Any(floor => document.FindType<FloorType>(floor.TypeId) is not { } type || Doors.FireMinutes(type.FireRating) < minutes)) return false;
        var links = Escape.Links(document, document.All<Room>().ToHashSet());
        var stairs = VerticalEscape.Stairs(document).Where(stair => stair.Upper.Contains(storeys[1].Id)).ToList();
        return stairs.Count > 0 && stairs.All(stair => links.Any(link => link.Room == stair.Foot && link.Other is null && link.Outside));
    }

    /// <summary>A storey's height for its compartments, mm: from its floor to the floor or roof over each room, the most; the next level's else (Table 8.1 note 5).</summary>
    private static double Height(BimDocument document, Datums.Level level, IEnumerable<Room> rooms)
    {
        var next = document.Levels.Where(each => each.Elevation > level.Elevation + 1).Select(each => each.Elevation).DefaultIfEmpty(level.Elevation + 3000).Min();
        var over = rooms.Select(room => room.GetBoundary(document)).Where(boundary => boundary.IsEnclosed && boundary.Polygon.Count >= 3)
            .Select(boundary => Cavities.Soffit(document, new Point2D(boundary.Polygon.Average(point => point.X), boundary.Polygon.Average(point => point.Y)), level.Elevation + 1))
            .Where(soffit => soffit is not null).Select(soffit => soffit!.Value.Elevation - level.Elevation).ToList();
        return over.Count > 0 ? over.Max() : next - level.Elevation;
    }

    /// <summary>
    /// A storage building's compartments by Table 8.1: on one storey, 20,000 m² and 18 m high, any with sprinklers; on more, each compartment's
    /// volume - storeys joined where the floor between does not stand the building's period, or an opening goes through it - 20,000 m³, 4000 where
    /// it reaches a storey 18 m up (note 4), 40,000 and 8000 with sprinklers.
    /// </summary>
    private static IEnumerable<EscapeProblem> Storage(BimDocument document, IReadOnlyList<Datums.Level> storeys, int minutes, bool sprinklered, double ground)
    {
        Room First(IEnumerable<Room> rooms) => rooms.OrderBy(room => room.Number, StringComparer.Ordinal).ThenBy(room => room.Name, StringComparer.Ordinal).First();
        var parts = storeys.SelectMany(level => Compartments(document, level.Id, minutes).Select(part => (Level: level, part.Rooms, part.Area, Height: Height(document, level, part.Rooms)))).ToList();
        if (storeys.Count == 1)
        {
            if (sprinklered) yield break;
            foreach (var part in parts.Where(part => part.Area > 20000 + 0.5 || part.Height > 18000 + 0.5))
                yield return new EscapeProblem(First(part.Rooms), $"the {part.Level.Name} is {part.Area:0} m² in one compartment, {part.Height / 1000:0.0} m high - Approved Document B Volume 2 (8.11a, Table 8.1) allows a single storey storage building without sprinklers 20,000 m² and 18 m high: divide it by compartment walls of {minutes} minutes, or fit sprinklers throughout");
            yield break;
        }

        // Storeys joined into one compartment: rooms one over the other, the floor between not of the period or opened through.
        var parent = Enumerable.Range(0, parts.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        Point2D Middle(Room room) => room.GetBoundary(document).Polygon is { Count: >= 3 } outline ? new Point2D(outline.Average(point => point.X), outline.Average(point => point.Y)) : room.Location;
        bool Over(Room upper, Room lower) => upper.GetBoundary(document) is { IsEnclosed: true } a && lower.GetBoundary(document) is { IsEnclosed: true } b
                                            && (Polygon2D.Contains(b.Polygon, Middle(upper)) || Polygon2D.Contains(a.Polygon, Middle(lower)));
        for (var i = 0; i < parts.Count; i++)
        for (var j = 0; j < parts.Count; j++)
        {
            var (below, above) = (parts[i], parts[j]);
            var k = storeys.ToList().IndexOf(below.Level);
            if (k < 0 || k + 1 >= storeys.Count || above.Level != storeys[k + 1]) continue;
            var pairs = above.Rooms.SelectMany(upper => below.Rooms.Where(lower => Over(upper, lower)).Select(lower => (upper, lower))).ToList();
            if (pairs.Count == 0) continue;
            var holes = HorizontalEscape.Openings(document, above.Level.Id);
            var rated = pairs.All(pair => document.All<Floor>().Any(floor => floor.LevelId == above.Level.Id && floor.Contains(Middle(pair.upper))
                                                                           && document.FindType<FloorType>(floor.TypeId) is { } type && Doors.FireMinutes(type.FireRating) >= minutes));
            var opened = holes.Any(hole => above.Rooms.Any(room => room.GetBoundary(document) is { IsEnclosed: true } outline && hole.Any(point => Polygon2D.Contains(outline.Polygon, point))));
            if (!rated || opened) parent[Find(i)] = Find(j);
        }

        foreach (var group in Enumerable.Range(0, parts.Count).GroupBy(Find))
        {
            var members = group.Select(i => parts[i]).ToList();
            var volume = members.Sum(part => part.Area * part.Height / 1000);
            var high = members.Any(part => part.Level.Elevation - ground >= 18000 - 0.5);
            var limit = (sprinklered ? 2 : 1) * (high ? 4000.0 : 20000);
            if (volume <= limit + 0.5) continue;
            var levels = members.Select(part => part.Level).Distinct().OrderBy(level => level.Elevation).Select(level => level.Name).ToList();
            yield return new EscapeProblem(First(members.SelectMany(part => part.Rooms)),
                $"{volume:0} m³ in one compartment over the {string.Join(", the ", levels)} - rooms joined through walls, doors and floors of less than {minutes} minutes - Approved Document B Volume 2 (8.11a, Table 8.1{(high ? " note 4" : string.Empty)}) allows a storage building's compartment {limit:0} m³{(high ? ", reaching a storey 18 m up," : string.Empty)}{(sprinklered ? " with sprinklers" : " without sprinklers")}, its height from floor to the underside of the floor or roof over it: divide it by compartment walls and floors of {minutes} minutes{(sprinklered ? string.Empty : ", or fit sprinklers throughout")}");
        }
    }

    // ---- separate occupancies and protected shafts (8.11e, 8.30, 8.32 to 8.37, Tables B1 and C1) ----------------------------------

    private static string OccupancyOf(Room room) => room.Occupancy.Trim();

    /// <summary>Whether two rooms are separate occupancies of a shop, factory or store: each given one, not the same (8.11e).</summary>
    private static bool Separate(BimDocument document, Room one, Room other) =>
        HorizontalEscape.Purpose(document) is PurposeGroup.ShopAndCommercial or PurposeGroup.Industrial or PurposeGroup.Storage
        && OccupancyOf(one).Length > 0 && OccupancyOf(other).Length > 0 && !string.Equals(OccupancyOf(one), OccupancyOf(other), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a room might be a protected shaft - a stair's room, or one called a lift, shaft, riser or duct - before the shafts are found.</summary>
    private static bool ShaftLike(BimDocument document, Room room) =>
        Named(room, "stair", "lift", "shaft", "riser", "duct") || Doors.StairRooms(document).Contains(room);

    /// <summary>The rooms that are protected shafts - a stair's, a lift's or a service riser's passing through a compartment floor - each with what kind.</summary>
    public static IReadOnlyDictionary<Room, string> Shafts(BimDocument document)
    {
        IReadOnlyDictionary<Room, string> Work()
        {
            var found = new Dictionary<Room, string>();
            if (!HorizontalEscape.Applies(document)) return found;
            double Elevation(Guid level) => document.FindLevel(level)?.Elevation ?? 0;
            bool Through(IEnumerable<double> levels) => levels.Distinct().OrderBy(each => each).Skip(1).Any(each => CompartmentFloorAt(document, each) is not null);
            foreach (var stair in VerticalEscape.Stairs(document).Where(stair => Through(stair.Rooms.Select(room => Elevation(room.LevelId)))))
                foreach (var room in stair.Rooms) found.TryAdd(room, "stair");

            foreach (var lift in Lifts.Of(document).Where(lift => Through(lift.Landings.Select(landing => landing.Level.Elevation))))
            {
                var centre = new Point2D(lift.Car.Average(point => point.X), lift.Car.Average(point => point.Y));
                foreach (var landing in lift.Landings)
                    if (RoomsOf.Containing(document, landing.Level.Id, centre) is { } room) found.TryAdd(room, "lift");
            }

            var risers = document.All<Room>().Where(room => (room.Name.Contains("riser", StringComparison.OrdinalIgnoreCase) || room.Name.Contains("duct", StringComparison.OrdinalIgnoreCase)
                                                             || room.Name.Contains("service shaft", StringComparison.OrdinalIgnoreCase)) && room.GetBoundary(document) is { IsEnclosed: true }).ToList();
            foreach (var riser in risers)
            {
                var outline = riser.GetBoundary(document).Polygon;
                var middle = new Point2D(outline.Average(point => point.X), outline.Average(point => point.Y));
                var level = Elevation(riser.LevelId);
                var above = risers.Where(other => other != riser && Elevation(other.LevelId) > level + 1 && other.GetBoundary(document).Polygon is var top && Polygon2D.Contains(top, middle))
                    .OrderBy(other => Elevation(other.LevelId)).FirstOrDefault();
                if (above is null || CompartmentFloorAt(document, Elevation(above.LevelId)) is null) continue;
                found.TryAdd(riser, "service");
                found.TryAdd(above, "service");
            }

            return found;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("compartments: shafts", document, null, null, Work) : Work();
    }

    /// <summary>
    /// What a wall falls short of as a compartment wall: between separate occupancies of a shop, factory or store REI 60 or the building's period,
    /// the greater (8.11e, Table B1 item 6b); round a protected shaft the building's period - a stair's 30 towards a protected lobby or corridor,
    /// said by 3.23 (8.33, Table B1 item 8). A lift's shaft is 5.35's.
    /// </summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (!HorizontalEscape.Applies(document) || type.Function != WallFunction.Interior) yield break;
        var (one, other) = WallRules.RoomsBeside(document, wall);
        if (one is null || other is null || one == other) yield break;
        var group = HorizontalEscape.Purpose(document);
        var period = Math.Max(30, Minutes(document));
        var rated = Doors.FireMinutes(type.FireRating);
        string Rated() => rated > 0 ? $"rated {rated} minutes" : "not fire-rated";
        if (Separate(document, one, other) && rated < Math.Max(60, period))
            yield return ($"it divides the {one.Name} ({OccupancyOf(one)}) from the {other.Name} ({OccupancyOf(other)}), separate occupancies, {Rated()}.",
                $"Approved Document B Volume 2 (8.11e, Table B1 item 6b) asks a compartment wall of REI {Math.Max(60, period)} - 60 minutes or the building's period, the greater - between separate occupancies of {HorizontalEscape.Said(group)}, spaces used by different organisations.");

        if (!ShaftLike(document, one) && !ShaftLike(document, other)) yield break;
        var shafts = Shafts(document);
        var shaft = shafts.ContainsKey(one) && !shafts.ContainsKey(other) ? one : shafts.ContainsKey(other) && !shafts.ContainsKey(one) ? other : null;
        if (shaft is null || shafts[shaft] == "lift" || period <= 30 && shafts[shaft] == "stair") yield break;
        var beyond = shaft == one ? other : one;
        var lobby = shafts[shaft] == "stair" && (Circulation.IsLobby(beyond) || beyond.GetBoundary(document) is { IsEnclosed: true } outline && Circulation.IsCorridor(beyond, outline.Polygon));
        if (lobby || rated >= period) yield break;
        yield return ($"it encloses the {shaft.Name}, a protected shaft through a compartment floor, toward the {beyond.Name}, {Rated()}.",
            $"Approved Document B Volume 2 (8.6, 8.33, Table B1 item 8c) asks the enclosure of a protected shaft - a {(shafts[shaft] == "stair" ? "stair" : "service riser")} passing from one compartment to another - REI {period}, the building's period{(shafts[shaft] == "stair" ? "; REI 30 toward a protected lobby or corridor (item 8b)" : string.Empty)}.");
    }

    /// <summary>
    /// What a door falls short of in a compartment wall: between separate occupancies the wall's period (8.30, Table C1 item 2e); into a stair's
    /// protected shaft E 30 Sa in a hotel, assembly building or office and half the wall's period, 30 at least, elsewhere (item 2b, 2c); into a
    /// service riser's half the wall's period, 30 at least (item 2d). 30 minutes into a stair is 3.23's.
    /// </summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Door door, DoorType type)
    {
        if (!HorizontalEscape.Applies(document)) yield break;
        var (one, other) = Doors.RoomsBeside(document, door);
        if (one is null || other is null || one == other) yield break;
        var group = HorizontalEscape.Purpose(document);
        var period = Math.Max(30, Minutes(document));
        var rated = Doors.FireMinutes(type.FireRating);
        string Rated() => rated > 0 ? $"rated {rated} minutes" : "not fire-rated";
        if (Separate(document, one, other) && rated < Math.Max(60, period))
            yield return ($"it opens between the {one.Name} ({OccupancyOf(one)}) and the {other.Name} ({OccupancyOf(other)}), separate occupancies, {Rated()}.",
                $"Approved Document B Volume 2 (8.30, Table C1 item 2e) allows an opening between separate occupancies only as a fire doorset for escape of the wall's own fire resistance, {Math.Max(60, period)} minutes.");

        if (!ShaftLike(document, one) && !ShaftLike(document, other)) yield break;
        var shafts = Shafts(document);
        var shaft = shafts.ContainsKey(one) && !shafts.ContainsKey(other) ? one : shafts.ContainsKey(other) && !shafts.ContainsKey(one) ? other : null;
        if (shaft is null || shafts[shaft] == "lift") yield break;
        var stairLike = group is PurposeGroup.ResidentialOther or PurposeGroup.AssemblyAndRecreation or PurposeGroup.Office;
        var asked = shafts[shaft] == "stair" && stairLike ? 30 : Math.Max(30, period / 2);
        if (rated >= asked || shafts[shaft] == "stair" && asked <= 30) yield break;
        yield return ($"it opens into the {shaft.Name}, a protected shaft through a compartment floor, {Rated()}.", shafts[shaft] == "stair"
            ? $"Approved Document B Volume 2 (Table C1 item 2c) asks a door in the enclosure of a protected stairway's shaft in {HorizontalEscape.Said(group)} half the period of its wall, 30 minutes at least: E {asked} Sa."
            : $"Approved Document B Volume 2 (Table C1 item 2d) asks a door in the enclosure of a protected service shaft half the period of its wall, 30 minutes at least: E {asked}.");
    }

    /// <summary>
    /// What pipes and ducts in protected shafts fall short of: no ventilating duct in a stair's or a lift's (8.36b) - but one pressurising the
    /// stair or ventilating it alone, said so - and a gas pipe of screwed or welded steel (8.37a).
    /// </summary>
    public static IEnumerable<MepProblem> ServiceProblems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || !HorizontalEscape.Applies(document)) yield break;
        var shafts = Shafts(document);
        if (shafts.Count == 0) yield break;
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        IEnumerable<(Room Room, string Kind)> Within(Point3D start, Point3D end, Guid levelId)
        {
            var (low, high) = (Math.Min(start.Z, end.Z), Math.Max(start.Z, end.Z));
            var middle = new Point2D((start.X + end.X) / 2, (start.Y + end.Y) / 2);
            var on = levels.Where(level => level.Id == levelId || level.Elevation > low - 1 && level.Elevation < high - 1).Select(level => level.Id).Distinct();
            foreach (var level in on)
                if (RoomsOf.Containing(document, level, middle) is { } room && shafts.TryGetValue(room, out var kind))
                    yield return (room, kind);
        }

        foreach (var duct in document.All<Duct>().Where(duct => duct.Kind is SystemKind.SupplyAir or SystemKind.ExtractAir or SystemKind.OutsideAir))
        {
            var (start, end) = duct.Ends(document);
            if (Within(start, end, duct.LevelId).FirstOrDefault(entry => entry.Kind is "stair" or "lift") is { Room: not null } inside)
                yield return new MepProblem(duct, $"it is a ventilating duct in the {inside.Room.Name}, a protected shaft holding a {inside.Kind} - Approved Document B Volume 2 (8.36b) keeps ventilating ducts out of a protected shaft with a protected stairway or a lift in it, but one pressurising the stairway or ventilating it alone");
        }

        foreach (var pipe in document.All<Pipe>().Where(pipe => pipe.Kind == SystemKind.Gas))
        {
            var material = (document.FindType<PipeType>(pipe.TypeId)?.Material ?? PipeMaterial.Copper).Like();
            if (material is PipeMaterial.GalvanisedSteel or PipeMaterial.StainlessSteel) continue;
            var (start, end) = pipe.Ends(document);
            if (Within(start, end, pipe.LevelId).FirstOrDefault() is { Room: not null } inside)
                yield return new MepProblem(pipe, $"it is a gas pipe of {PipeType.Title(material)} in the {inside.Room.Name}, a protected shaft - Approved Document B Volume 2 (8.37a) asks a pipe carrying natural gas or LPG in a protected shaft of screwed steel or all-welded steel, installed by the Pipelines Safety Regulations 1996 and the Gas Safety (Installation and Use) Regulations 1998");
        }
    }

    /// <summary>A floor between separate occupancies of a shop, factory or store, said with why - a compartment floor (8.11e); null for none.</summary>
    private static string? Occupancies(BimDocument document, Floor floor)
    {
        if (!HorizontalEscape.Applies(document) || HorizontalEscape.Purpose(document) is not (PurposeGroup.ShopAndCommercial or PurposeGroup.Industrial or PurposeGroup.Storage)) return null;
        var elevation = document.FindLevel(floor.LevelId)?.Elevation ?? 0;
        var below = document.Levels.Where(level => level.Elevation < elevation - 1).OrderByDescending(level => level.Elevation).FirstOrDefault();
        if (below is null) return null;
        Point2D Middle(Room room) => room.GetBoundary(document).Polygon is { Count: >= 3 } outline ? new Point2D(outline.Average(point => point.X), outline.Average(point => point.Y)) : room.Location;
        var uppers = document.All<Room>().Where(room => room.LevelId == floor.LevelId && OccupancyOf(room).Length > 0 && floor.Contains(Middle(room))).ToList();
        var lowers = document.All<Room>().Where(room => room.LevelId == below.Id && OccupancyOf(room).Length > 0 && room.GetBoundary(document) is { IsEnclosed: true }).ToList();
        foreach (var upper in uppers)
        foreach (var lower in lowers)
            if (Separate(document, upper, lower) && (Polygon2D.Contains(lower.GetBoundary(document).Polygon, Middle(upper)) || upper.GetBoundary(document) is { IsEnclosed: true } top && Polygon2D.Contains(top.Polygon, Middle(lower))))
                return $"a floor between separate occupancies - the {upper.Name} ({OccupancyOf(upper)}) over the {lower.Name} ({OccupancyOf(lower)}) - a compartment floor (8.11e)";
        return null;
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
