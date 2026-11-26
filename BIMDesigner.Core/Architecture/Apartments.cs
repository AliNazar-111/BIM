using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A US apartment building - condos, a triplex or more stacked - Group R-2 under the 2021 IBC (round 144), read from its model text. A US
/// project whose Building Type is apartments, flats, condos, a triplex, fourplex or multifamily building; its dwelling units the rooms named
/// into them; its common parts the rooms named into none - corridors, lobbies, halls - and its exit stairs the stairs in them.
///
/// <b>Separations.</b> Walls between dwelling units are fire partitions (420.2): 1 hour (708.3), 1/2 hour in a Type IIB, IIIB or VB building
/// sprinklered throughout (exception 2) - the same of floors between them, horizontal assemblies (420.3, 711.2.4.3) - running up to the floor
/// or roof deck above (708.4). Corridor walls 1/2 hour in a sprinklered building, a corridor serving more than 10 not allowed without (Table
/// 1020.2). The enclosure of an interior exit stair 1 hour connecting fewer than four stories, 2 hours four or more (1023.2). Walls and floors
/// between units, or between a unit and the common parts, STC 50 (1206.2) - the type's acoustic rating taken as its STC.
///
/// <b>Systems.</b> Sprinklers throughout (420.4, 903.2.8). A fire alarm - its panel and sounders, manual call points unless sprinklered - where
/// a dwelling unit is three or more stories above the lowest level of exit discharge or there are more than 16 units (907.2.9.1).
///
/// <b>Egress.</b> Two exits from each story with dwelling units (1006.3.3) - its common stairs down, at the level of exit discharge its common
/// parts' doors out - one only up to the third story above grade plane with four dwelling units at most, sprinklered, with emergency escape
/// openings (Table 1006.3.4(1)), whose sleeping rooms then each have one (1031.2). Corridors 44 in wide, 36 in where the story's occupants -
/// its rooms' area at 200 sq ft gross each (Table 1004.5) - are fewer than 50 (1020.3); the common stairs likewise (1011.2), with risers 4 to 7
/// in, treads 11 in and handrails both sides (1011.5.2, 1011.11) - a stair within a unit the IRC's 7 3/4 and 10 (exception 3). Guards 42 in in
/// the common parts, 36 in within a unit in a building of three stories at most (1015.3).
///
/// <b>Travel</b> (round 146): each story walked on a grid, every point to the nearest door into an exit stair or out - exit access travel
/// 250 ft in Group R sprinklered, 200 ft not (Table 1017.2), 125 ft on a story of one exit (Table 1006.3.4(1)); the stretch walked before two
/// ways to two exits open up 45 degrees apart, the common path of egress travel, 125 ft (Table 1006.2.1); in a corridor that stretch its dead
/// end, 50 ft sprinklered, 20 ft not, where two exits are asked (1020.5); and two exits on a story at least a third of the story's diagonal
/// apart sprinklered, half not (1007.1.1).
///
/// Not done: Type A and B units (1107); allowable height and area (Chapter 5); IIC 50 (1206.3).
/// </summary>
public static class Apartments
{
    /// <summary>Table 1004.5's residential load factor, 200 sq ft gross a person, m²; 1011.2 and 1020.3's widths, mm; 1206.2's STC.</summary>
    public const double PerOccupant = 18.58, Wide = 1118, Narrow = 914, Stc = 50;

    /// <summary>Table 1017.2's exit access travel in Group R sprinklered and not, Table 1006.3.4(1)'s and Table 1006.2.1's 125 ft, 1020.5's dead ends, mm.</summary>
    public const double TravelSprinklered = 76200, Travel = 60960, OneExitTravel = 38100, CommonPath = 38100, DeadEnd = 15240, ShortDeadEnd = 6096;

    private static string Feet(double mm) => $"{mm / 304.8:0} ft ({mm / 1000:0.0} m)";

    /// <summary>A story walked (round 146): the rooms' travel and its exits, by the UK walk's grid - kept while the model does not change.</summary>
    internal static HorizontalEscape.Storey Walked(BimDocument document, Guid level)
    {
        // Its exits the doors into common rooms a stair starts or ends in - not a room only named a stair - and out.
        HorizontalEscape.Storey Work()
        {
            var stairs = Doors.StairRooms(document);
            return HorizontalEscape.WalkLevel(document, level, room => !InUnit(room) && stairs.Contains(room));
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("apartments: walked", document, level, null, Work) : Work();
    }

    /// <summary>The farthest a story's rooms are from their nearest exit, walked, mm; NaN where none is walked.</summary>
    public static double FarthestTravel(BimDocument document, Guid level) =>
        Walked(document, level).Rooms.Values.Where(travel => travel.Reached).Select(travel => travel.Nearest).DefaultIfEmpty(double.NaN).Max();

    /// <summary>Whether a project is a US apartment building, Group R-2 under the IBC.</summary>
    public static bool Applies(BimDocument document) =>
        document.ProjectInformation.ClearanceStandard.IsUs() && FireDesign.UseOf(document) == BuildingUse.Flats;

    /// <summary>Whether 708.3's and 711.2.4.3's exceptions allow 1/2 hour between units: Type IIB, IIIB or VB, sprinklered throughout.</summary>
    public static bool HalfHour(BimDocument document) =>
        document.ProjectInformation.Sprinklered && document.ProjectInformation.ConstructionType is ConstructionType.IIB or ConstructionType.IIIB or ConstructionType.VB;

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool InUnit(Room room) => Dwellings.Of(room).Length > 0;

    /// <summary>Whether a room is an exit stair's: named a stair, or a flight's, in no dwelling unit.</summary>
    public static bool ExitStair(BimDocument document, Room room) => !InUnit(room) && (Named(room, "stair") || Doors.StairRooms(document).Contains(room));

    /// <summary>Whether a room is of the common parts: in no dwelling unit, not an exit stair.</summary>
    public static bool Common(BimDocument document, Room room) => !InUnit(room) && !ExitStair(document, room);

    /// <summary>Whether a stair is within a dwelling unit: its foot or its head in a room named into one.</summary>
    public static bool WithinUnit(BimDocument document, Stair stair)
    {
        StairLayout layout;
        try { layout = Stairs.Layout(document, stair); }
        catch (Exception) { return false; }
        if (layout.WalkingLine is not { Count: >= 2 } walk) return false;
        return RoomsOf.Containing(document, stair.LevelId, walk[0]) is { } foot && InUnit(foot)
               || stair.TopLevelId is { } top && RoomsOf.Containing(document, top, walk[^1]) is { } head && InUnit(head);
    }

    private static string Rated(int minutes) => minutes > 0 ? $"rated {UnitSeparation.Hours(minutes)}" : "not fire-rated";

    // --- Walls ----------------------------------------------------------------------------------

    /// <summary>What a wall of an apartment building falls short of: between units, along a corridor, round an exit stair - said, and the rule.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (!Applies(document) || document.IsCurtainWall(wall) || type.Function == WallFunction.Exterior) yield break;
        var pairs = Beside(document, wall, type);
        if (pairs.Count == 0) yield break;
        var minutes = Doors.FireMinutes(type.FireRating);
        var sprinklered = document.ProjectInformation.Sprinklered;

        // Round an exit stair: a fire barrier, 1 hour or 2 by the stories it connects (1023.2).
        if (pairs.FirstOrDefault(pair => ExitStair(document, pair.One) != ExitStair(document, pair.Other)) is { One: not null } enclosing)
        {
            var stories = StoriesConnected(document, ExitStair(document, enclosing.One) ? enclosing.One : enclosing.Other);
            var asked = stories >= 4 ? 120 : 60;
            if (minutes < asked)
                yield return ($"it encloses an interior exit stair connecting {stories} stories, {Rated(minutes)}.",
                    "The IBC (1023.2) encloses an interior exit stairway in fire barriers rated 2 hours where it connects four stories or more, 1 hour where it connects fewer.");
            yield break;
        }

        var units = pairs.Any(pair => InUnit(pair.One) && InUnit(pair.Other) && !Dwellings.Same(pair.One, pair.Other));
        var corridor = !units && pairs.Any(pair => InUnit(pair.One) != InUnit(pair.Other) && (Common(document, pair.One) || Common(document, pair.Other)));
        if (!units && !corridor) yield break;

        if (units)
        {
            var asked = HalfHour(document) ? 30 : 60;
            if (minutes < asked)
                yield return ($"it separates two dwelling units, {Rated(minutes)}.",
                    "The IBC (420.2, 708.3) builds the walls between dwelling units as fire partitions rated 1 hour - 1/2 hour in a building of Type IIB, IIIB or VB construction sprinklered throughout to NFPA 13 (exception 2).");
        }
        else if (!sprinklered || minutes < 30)
            yield return ($"it is a corridor wall of an apartment building, {Rated(minutes)}{(sprinklered ? string.Empty : ", the building not sprinklered")}.",
                "The IBC (Table 1020.2) rates the walls of a corridor serving more than 10 in Group R 1/2 hour where the building is sprinklered throughout - such a corridor not allowed in one that is not.");

        // Up to the deck over it (708.4).
        if (Short(document, wall) is { } gap)
            yield return ($"it {(units ? "separates two dwelling units" : "is a corridor wall")} and stops {Units.FormatLength(gap)} below the floor or roof over it.",
                "The IBC (708.4) carries a fire partition from the foundation or floor below up to the underside of the floor or roof sheathing, deck or slab above, or of a rated floor/ceiling or roof/ceiling assembly - attach its top.");

        // STC 50 (1206.2).
        if (type.AcousticRating < Stc - 0.05)
            yield return ($"it separates {(units ? "two dwelling units" : "a dwelling unit from the common parts")} with a sound rating of {type.AcousticRating:0}.",
                "The IBC (1206.2) asks walls between dwelling units, or between one and the public or service areas, for STC 50 (ASTM E90), or NNIC 45 tested in the field - entrance doors aside, tight-fitting.");
    }

    /// <summary>
    /// The rooms either side of a wall along its length - at a tenth, three tenths and on to nine - each pair once: a corridor wall's middle
    /// may fall where a wall between two units meets it.
    /// </summary>
    private static List<(Room One, Room Other)> Beside(BimDocument document, Wall wall, WallType type)
    {
        var reach = type.Structure.TotalWidth / 2 + 150;
        var pairs = new List<(Room One, Room Other)>();
        foreach (var fraction in new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
        {
            var along = wall.Length * fraction;
            if (RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, along, reach)) is not { } one
                || RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, along, -reach)) is not { } other || one == other) continue;
            if (!pairs.Any(pair => pair.One == one && pair.Other == other || pair.One == other && pair.Other == one)) pairs.Add((one, other));
        }

        return pairs;
    }

    /// <summary>How far a wall stops short of the floor or roof over it - the lowest underside over its middle - mm; null where it reaches it, or none is over it.</summary>
    private static double? Short(BimDocument document, Wall wall)
    {
        if (wall.AttachedRoof(document) is not null) return null;
        var middle = wall.Start.MidpointTo(wall.End);
        var top = wall.GetTopElevation(document);
        var undersides = document.All<Slab>().Where(slab => slab is Floor or Roof && slab.Boundary.Count >= 3 && slab.Contains(middle))
            .Select(slab => slab is Roof roof ? roof.UndersideAt(document, middle) : slab.GetBottomElevation(document))
            .Where(under => under > top - 400).ToList();
        if (undersides.Count == 0) return null;
        var gap = undersides.Min() - top;
        return gap > 50 ? gap : null;
    }

    /// <summary>How many stories an exit stair's room connects: the levels its stairs, standing in it, reach - its own counted.</summary>
    private static int StoriesConnected(BimDocument document, Room stairRoom)
    {
        if (stairRoom.GetBoundary(document) is not { IsEnclosed: true } boundary) return 1;
        var levels = new HashSet<Guid> { stairRoom.LevelId };
        foreach (var stair in document.All<Stair>())
        {
            StairLayout layout;
            try { layout = Stairs.Layout(document, stair); }
            catch (Exception) { continue; }
            if (layout.WalkingLine is not { Count: >= 2 } walk || !Polygon2D.Contains(boundary.Polygon, walk[walk.Count / 2])) continue;
            levels.Add(stair.LevelId);
            if (stair.TopLevelId is { } top) levels.Add(top);
        }

        // The flights stacked in its shaft, whatever their storey: every story with rooms from the lowest they touch to the highest.
        var elevations = levels.Select(id => document.FindLevel(id)?.Elevation ?? 0).ToList();
        return document.Levels.Count(level => level.Elevation >= elevations.Min() - 1 && level.Elevation <= elevations.Max() + 1 && document.All<Room>().Any(room => room.LevelId == level.Id));
    }

    // --- Floors ---------------------------------------------------------------------------------

    /// <summary>What a floor between dwelling units, or between one and the common parts, falls short of (420.3, 711.2.4.3, 1206.2): said and the rule.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Floor floor, SlabType type)
    {
        if (!Applies(document) || floor.Boundary.Count < 3 || document.FindLevel(floor.LevelId) is not { } level || Levels.Below(document, level.Id) is not { } below) yield break;
        var units = false;
        var common = false;
        foreach (var room in document.All<Room>().Where(room => room.LevelId == level.Id && floor.Contains(room.Location)))
        foreach (var under in document.All<Room>().Where(under => under.LevelId == below.Id && floor.Contains(under.Location)))
        {
            if (!RoomsOverlap(document, room, under)) continue;
            if (InUnit(room) && InUnit(under) && !Dwellings.Same(room, under)) units = true;
            else if (InUnit(room) != InUnit(under)) common = true;
        }

        if (!units && !common) yield break;
        var minutes = Doors.FireMinutes(type.FireRating);
        var asked = HalfHour(document) ? 30 : 60;
        if (units && minutes < asked)
            yield return ($"it separates dwelling units one over another, {Rated(minutes)}.",
                "The IBC (420.3, 711.2.4.3) builds the floors between dwelling units as horizontal assemblies rated 1 hour - 1/2 hour in a building of Type IIB, IIIB or VB construction sprinklered throughout to NFPA 13.");
        if (type.AcousticRating < Stc - 0.05)
            yield return ($"it separates {(units ? "dwelling units one over another" : "a dwelling unit from the common parts")} with a sound rating of {type.AcousticRating:0}.",
                "The IBC (1206.2) asks floor-ceiling assemblies between dwelling units, or between one and the public or service areas, for STC 50 (ASTM E90).");
    }

    /// <summary>Whether two rooms on storeys one over the other overlap in plan.</summary>
    private static bool RoomsOverlap(BimDocument document, Room a, Room b) =>
        a.GetBoundary(document) is { IsEnclosed: true } one && b.GetBoundary(document) is { IsEnclosed: true } other
        && (Polygon2D.Contains(other.Polygon, a.Location) || Polygon2D.Contains(one.Polygon, b.Location)
            || one.Polygon.Any(point => Polygon2D.Contains(other.Polygon, point)));

    // --- The building -----------------------------------------------------------------------------

    /// <summary>A story of the building: its level, rooms, dwelling units, occupants, its exits and where they are, and its number above grade plane.</summary>
    public sealed record Story(Level Level, IReadOnlyList<Room> Rooms, int Units, double Occupants, int Exits, int AboveGrade, Room Said);

    /// <summary>Its stories with rooms, lowest first: each one's units, occupants by Table 1004.5, exits (1006.3.3) and its number above grade plane.</summary>
    public static IReadOnlyList<Story> Stories(BimDocument document)
    {
        IReadOnlyList<Story> Work()
        {
            var grade = SplitLevels.GradePlane(document);
            var levels = document.Levels.OrderBy(level => level.Elevation).Where(level => document.All<Room>().Any(room => room.LevelId == level.Id)).ToList();
            if (levels.Count == 0) return Array.Empty<Story>();
            var discharge = levels.FirstOrDefault(level => level.Elevation >= grade - 1) ?? levels[^1];
            var stairs = document.All<Stair>().Where(stair => stair.TopLevelId is not null && !WithinUnit(document, stair)).ToList();
            var stories = new List<Story>();
            foreach (var level in levels)
            {
                var rooms = document.All<Room>().Where(room => room.LevelId == level.Id).ToList();
                var units = rooms.Select(Dwellings.Of).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var area = rooms.Sum(room => room.GetBoundary(document) is { IsEnclosed: true } boundary ? boundary.Area / 1e6 : 0);
                int exits;
                if (level.Elevation > discharge.Elevation + 1) exits = stairs.Count(stair => stair.TopLevelId == level.Id);
                else if (level.Elevation < discharge.Elevation - 1) exits = stairs.Count(stair => stair.LevelId == level.Id);
                else exits = document.All<Door>().Count(door => document.FindType<DoorType>(door.TypeId) is { } type && Doors.External(document, door, type)
                                                                 && Doors.RoomsBeside(document, door) is var (swing, otherSide) && new[] { swing, otherSide }.Any(room => room is not null && !InUnit(room)));
                var above = level.Elevation < grade - 1 ? 0 : levels.Count(each => each.Elevation >= grade - 1 && each.Elevation <= level.Elevation + 1);
                var said = rooms.Where(room => Common(document, room)).OrderBy(room => room.Name.Contains("corridor", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault() ?? rooms[0];
                stories.Add(new Story(level, rooms, units, area / PerOccupant, exits, above, said));
            }

            return stories;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("apartments: stories", document, null, null, Work) : Work();
    }

    /// <summary>Whether a story is one of a single exit as Table 1006.3.4(1) allows it: up to the third above grade plane, four units at most, sprinklered.</summary>
    public static bool SingleExitAllowed(BimDocument document, Story story) =>
        story.AboveGrade <= 3 && story.Units <= 4 && document.ProjectInformation.Sprinklered && !(FarthestTravel(document, story.Level.Id) > OneExitTravel + 0.5);

    /// <summary>Whether a room's story has but one exit: where its sleeping rooms need emergency escape openings (1031.2).</summary>
    public static bool SingleExit(BimDocument document, Room room) =>
        Stories(document).FirstOrDefault(story => story.Level.Id == room.LevelId) is { Exits: <= 1 };

    /// <summary>What the building falls short of as a whole and story by story - sprinklers, fire alarm, exits, corridor widths - each said on a room.</summary>
    public static IEnumerable<EscapeProblem> BuildingProblems(BimDocument document)
    {
        if (!Applies(document)) yield break;
        var stories = Stories(document);
        if (stories.Count == 0) yield break;
        var first = stories.FirstOrDefault(story => story.Rooms.Any(room => Common(document, room)))?.Said ?? stories[0].Said;
        var sprinklered = document.ProjectInformation.Sprinklered;

        if (!sprinklered)
            yield return new EscapeProblem(first, "the building is not sprinklered - the IBC (420.4, 903.2.8) asks an automatic sprinkler system throughout a building with a Group R fire area, NFPA 13 or 13R (903.3)");

        // A fire alarm: a unit three or more stories above the lowest level of exit discharge, or more than 16 units (907.2.9.1).
        var discharge = stories.FirstOrDefault(story => story.Level.Elevation >= SplitLevels.GradePlane(document) - 1) ?? stories[0];
        var highest = stories.Where(story => story.Units > 0).Select(story => stories.Count(each => each.Level.Elevation > discharge.Level.Elevation + 1 && each.Level.Elevation <= story.Level.Elevation + 1)).DefaultIfEmpty(0).Max();
        var units = Dwellings.Named(document).Count;
        if (highest >= 3 || units > 16)
        {
            bool Has(ComponentForm form) => document.All<Component>().Any(component => document.FindType<ComponentType>(component.TypeId)?.Form == form);
            var wanting = new List<string>();
            if (!Has(ComponentForm.FireAlarmPanel)) wanting.Add("a fire alarm panel");
            if (!Has(ComponentForm.FireAlarmSounder)) wanting.Add("sounders");
            if (!sprinklered && !Has(ComponentForm.FireAlarmCallPoint)) wanting.Add("manual call points");
            if (wanting.Count > 0)
                yield return new EscapeProblem(first, $"it has {(highest >= 3 ? $"a dwelling unit {highest} stories above the lowest level of exit discharge" : $"{units} dwelling units")}, and no {string.Join(" or ", wanting)} - the IBC (907.2.9.1) asks a manual fire alarm system sounding the occupant notification (907.5) where a unit is three or more stories above the lowest level of exit discharge or there are more than 16 - its manual call points not needed where sprinklered throughout with the sprinklers' water flow sounding it (exception 2)");
        }

        foreach (var story in stories.Where(story => story.Units > 0))
        {
            // Two exits, or one as Table 1006.3.4(1) allows (1006.3.3, 1006.3.4).
            if (story.Exits == 0)
                yield return new EscapeProblem(story.Said, $"{story.Level.Name} has no exit - no common stair down from it, or door out of its common parts at the level of exit discharge - the IBC (1006.3.3) asks two from each story");
            else if (story.Exits == 1 && !SingleExitAllowed(document, story))
                yield return new EscapeProblem(story.Said, $"{story.Level.Name} has one exit, {story.Units} dwelling units on it, story {story.AboveGrade} above grade plane{(sprinklered ? string.Empty : ", not sprinklered")}{(FarthestTravel(document, story.Level.Id) is var far && far > OneExitTravel + 0.5 ? $", {Feet(far)} from it at the farthest" : string.Empty)} - the IBC (1006.3.3) asks two; Table 1006.3.4(1) allows one only up to the third story above grade plane with 4 dwelling units at most, sprinklered throughout with emergency escape openings (1031), its exit access travel 125 ft at most");

            // Corridors 44 in, 36 in for fewer than 50 (1020.3).
            var asked = story.Occupants >= 50 ? Wide : Narrow;
            foreach (var room in story.Rooms.Where(room => Common(document, room) && room.GetBoundary(document) is { IsEnclosed: true } boundary && Circulation.IsCorridor(room, boundary.Polygon)))
                if (Circulation.Measure(document, room) is { } measured && measured.Width < asked - 0.5)
                    yield return new EscapeProblem(room, $"{room.Name} is {Units.FormatLength(measured.Width)} wide at its narrowest, the story's occupants {story.Occupants:0} at 200 sq ft each - the IBC (1020.3) asks a corridor {(asked > Narrow ? "44 in (1118)" : "36 in (914)")} wide{(asked > Narrow ? string.Empty : " for fewer than 50")}");

            foreach (var problem in TravelProblems(document, story)) yield return problem;
        }
    }

    /// <summary>
    /// A story's travel, walked (round 146): exit access travel by Table 1017.2 - on a story of two exits or more; one exit's 125 ft is said with
    /// the exits - the common path of egress travel by Table 1006.2.1, a corridor's dead end by 1020.5, and its exits' separation by 1007.1.1.
    /// </summary>
    private static IEnumerable<EscapeProblem> TravelProblems(BimDocument document, Story story)
    {
        if (story.Exits == 0) yield break;
        var walked = Walked(document, story.Level.Id);
        if (walked.Exits.Count == 0) yield break;
        var sprinklered = document.ProjectInformation.Sprinklered;
        var limit = sprinklered ? TravelSprinklered : Travel;
        var two = story.Exits >= 2;

        // Exit access travel (1017.2): the farthest point of each room to its nearest exit.
        if (two)
            foreach (var (room, travel) in walked.Rooms.Where(entry => entry.Value.Reached && entry.Value.Nearest > limit + 0.5).OrderByDescending(entry => entry.Value.Nearest).Take(1))
                yield return new EscapeProblem(room, $"its farthest point is {Feet(travel.Nearest)} from the nearest exit, walked - the IBC (1017.2, Table 1017.2) allows exit access travel of {(sprinklered ? "250 ft (76.2 m) in Group R sprinklered throughout" : "200 ft (61.0 m) in Group R without sprinklers")}");

        // Common path of egress travel (1006.2.1): walked before two ways to two exits open up.
        if (two)
            foreach (var (room, travel) in walked.Rooms.Where(entry => entry.Value.Reached && !Circulation.IsCorridor(entry.Key, entry.Key.GetBoundary(document).Polygon) && entry.Value.OneWay > CommonPath + 0.5)
                         .OrderByDescending(entry => entry.Value.OneWay).Take(1))
                yield return new EscapeProblem(room, $"from its farthest point {Feet(travel.OneWay)} is walked before there are two ways to two exits - the IBC (1006.2.1, Table 1006.2.1) allows a common path of egress travel of 125 ft (38.1 m) in Group R-2");

        // Dead ends (1020.5): a corridor's one-way stretch, where two exits are asked.
        if (two)
        {
            var deadEnd = sprinklered ? DeadEnd : ShortDeadEnd;
            foreach (var (room, travel) in walked.Rooms.Where(entry => Common(document, entry.Key) && entry.Value.Reached && entry.Key.GetBoundary(document) is { IsEnclosed: true } boundary
                                                                        && Circulation.IsCorridor(entry.Key, boundary.Polygon) && entry.Value.OneWay > deadEnd + 0.5))
                yield return new EscapeProblem(room, $"{room.Name} has a dead end of {Feet(travel.OneWay)} - the IBC (1020.5) allows a dead-end corridor {(sprinklered ? "50 ft (15.2 m) in Group R-2 sprinklered throughout" : "20 ft (6.1 m)")} where more than one exit is asked");
        }

        // Exit separation (1007.1.1): two exits at least a third of the story's diagonal apart, sprinklered; half without.
        if (two && walked.Exits.Count >= 2)
        {
            var points = story.Rooms.Select(room => room.GetBoundary(document)).Where(boundary => boundary.IsEnclosed).SelectMany(boundary => boundary.Polygon).ToList();
            if (points.Count >= 3)
            {
                var diagonal = Math.Sqrt(Math.Pow(points.Max(p => p.X) - points.Min(p => p.X), 2) + Math.Pow(points.Max(p => p.Y) - points.Min(p => p.Y), 2));
                var doors = walked.Exits.Where(exit => exit.Door is not null).Select(exit => document.Find<Wall>(exit.Door!.HostWallId) is { } wall ? wall.LocationCurve.PointAt(exit.Door.DistanceAlongWall) : (Geometry.Point2D?)null)
                    .OfType<Geometry.Point2D>().ToList();
                var apart = doors.SelectMany(a => doors.Select(b => a.DistanceTo(b))).DefaultIfEmpty(0).Max();
                var asked = diagonal / (sprinklered ? 3 : 2);
                if (doors.Count >= 2 && apart < asked - 0.5)
                    yield return new EscapeProblem(story.Said, $"{story.Level.Name}'s exits are {Feet(apart)} apart, its diagonal {Feet(diagonal)} - the IBC (1007.1.1) asks two exits at least {(sprinklered ? "a third" : "half")} of the diagonal apart, {Feet(asked)}");
            }
        }
    }

    /// <summary>The width a common stair of the building is asked (1011.2): 44 in where a story it serves has 50 occupants or more, else 36 in, mm.</summary>
    public static double StairWidth(BimDocument document, Stair stair)
    {
        var served = Stories(document).Where(story => story.Level.Id == stair.LevelId || story.Level.Id == stair.TopLevelId).ToList();
        return served.Any(story => story.Occupants >= 50) ? Wide : Narrow;
    }

    /// <summary>What a room's Means of Escape row says in an apartment building: its story's exits, or what they fall short of.</summary>
    public static string Describe(BimDocument document, Room room)
    {
        var said = BuildingProblems(document).Where(problem => problem.Room == room).Select(problem => problem.Said).ToList();
        if (said.Count > 0) return string.Join("; ", said);
        return Stories(document).FirstOrDefault(story => story.Level.Id == room.LevelId) is { } story
            ? $"IBC Group R-2: {story.Level.Name} has {story.Exits} exit{(story.Exits == 1 ? string.Empty : "s")} (1006.3.3), {story.Units} dwelling unit{(story.Units == 1 ? string.Empty : "s")}, {story.Occupants:0} occupants (Table 1004.5)" +
              (Walked(document, room.LevelId).Rooms.TryGetValue(room, out var travel) && travel.Reached
                  ? $"; its farthest point {Feet(travel.Nearest)} from an exit, walked (1017.2), {Feet(travel.OneWay)} of it before two ways open (1006.2.1)" : string.Empty)
            : "IBC Group R-2";
    }
}
