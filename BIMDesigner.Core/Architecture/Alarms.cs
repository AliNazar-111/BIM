using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Smoke, heat and carbon monoxide alarms, where the rules want them.
///
/// In England, Approved Document B asks for at least a Grade D2, Category LD3 system to BS 5839-6
/// (1.1) - LD2, a heat alarm in the kitchen with it, in a house of three storeys or more (1.7) -
/// and BS 5839-6 sites it: a smoke alarm on every storey, within 3 m of every bedroom's door and
/// 7.5 m of every other room's, 300 from walls and lights on the ceiling, and none in a kitchen or
/// bathroom, where cooking and steam set them off. Approved Document J asks for a carbon monoxide
/// alarm in the room with a stove, an open fire, a boiler or another fixed gas appliance - a gas fire, a
/// gas water heater or dryer, not a cooker - 1 to 3 m from it (2.36, 3.45).
///
/// In the US, the IRC (R314.3): a smoke alarm in every bedroom, one by the bedrooms outside them,
/// and one on every storey; 3 ft (914) at least from the door of a bathroom with a tub or shower,
/// and 6 ft (1828) from a cooking appliance - a photoelectric alarm's, R314.3.1. A carbon monoxide
/// alarm by the bedrooms where there is a fuel-fired appliance, and in a bedroom with one in it (R315).
/// </summary>
public static class Alarms
{
    /// <summary>Whether a piece is one of the alarms.</summary>
    private static bool IsAlarm(ComponentType type) =>
        type.Form is ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm or ComponentForm.CarbonMonoxideAlarm;

    /// <summary>The pieces of one form on a storey, where they are.</summary>
    private static List<(Component Piece, ComponentType Type)> All(BimDocument document, Guid levelId, Func<ComponentType, bool> which) =>
        document.All<Component>()
            .Where(piece => piece.LevelId == levelId)
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type is not null && which(entry.Type))
            .Select(entry => (entry.Piece, entry.Type!))
            .ToList();

    /// <summary>
    /// The appliances on a storey that burn a fuel, by name and where they stand: a boiler, a gas fire,
    /// a gas water heater or dryer - a fixed gas appliance, not a cooker (Approved Document J, 3.43) - and
    /// a chimney's stove or open fire on that storey, in front of its breast.
    /// </summary>
    public static IReadOnlyList<(string Name, Point2D At)> Appliances(BimDocument document, Guid levelId)
    {
        var found = All(document, levelId, type => type.BurnsGas && type.Form != ComponentForm.Cooker)
            .Select(entry => (entry.Type.Name, entry.Piece.Location))
            .ToList();

        foreach (var chimney in document.All<Chimney>())
        {
            var fireplace = chimney.LevelId == levelId ? chimney.Fireplace : chimney.FireplaceOn(levelId);
            if (fireplace == ChimneyFireplace.None) continue;

            var front = new Vector2D(-Math.Sin(chimney.Angle * Math.PI / 180), Math.Cos(chimney.Angle * Math.PI / 180));
            found.Add((fireplace == ChimneyFireplace.Stove ? "the stove" : "the open fire", chimney.Location + front * (chimney.Depth / 2)));
        }

        return found;
    }

    /// <summary>A room's doors, by a point just inside it at each.</summary>
    public static IReadOnlyList<Point2D> Doors(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var doors = new List<Point2D>();
        foreach (var door in document.All<Door>())
        {
            if (document.Find<Wall>(door.HostWallId) is not { } wall || wall.LevelId != room.LevelId) continue;
            if (document.GetWallType(wall) is not { } type) continue;

            var reach = type.Structure.TotalWidth / 2 + 150;
            foreach (var side in new[] { reach, -reach })
            {
                var at = wall.PointAt(type.Structure, door.DistanceAlongWall, side);
                if (!Polygon2D.Contains(outline, at)) continue;
                doors.Add(at);
                break;
            }
        }

        return doors;
    }

    /// <summary>What is wrong with where one alarm is, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Component piece, ClearanceStandard standard)
    {
        if (document.FindType<ComponentType>(piece.TypeId) is not { } type || !IsAlarm(type)) return Array.Empty<PlacementProblem>();

        var us = standard.IsUs();
        var footprint = ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0));
        var problems = new List<PlacementProblem>();
        var room = RoomsOf.Containing(document, piece.LevelId, piece.Location);

        // A home's alarm in a building other than a dwelling: its fire detection is a system's, to BS 5839-1 - Approved Document J's carbon
        // monoxide alarms asked only in a dwelling (2.34, 3.43), and sited as it says there.
        if (!us && !Domestic.Rules(document, room))
        {
            if (type.Form is ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm)
                problems.Add(new PlacementProblem(piece, $"it is a home's {(type.Form == ComponentForm.SmokeAlarm ? "smoke" : "heat")} alarm, to BS 5839-6.",
                    "Approved Document B Volume 2 (1.8) asks the fire detection and alarm system of a building other than a dwelling to comply with BS 5839-1: a detector wired to its panel.",
                    footprint));
            return problems;
        }
        var outline = room?.GetBoundary(document) is { IsEnclosed: true } boundary ? boundary.Polygon : null;
        var inRoom = outline is null
            ? new List<(Component Piece, ComponentType Type)>()
            : All(document, piece.LevelId, _ => true).Where(entry => Polygon2D.Contains(outline, entry.Piece.Location)).ToList();

        // 300 from the walls on the ceiling, and from the lights.
        if (!us)
        {
            var wall = document.Walls
                .Where(other => other.LevelId == piece.LevelId && document.GetWallType(other) is { } t && t.Structure.TotalWidth > 0)
                .Select(other => Line2D.DistanceFromSegment(piece.Location, other.Start, other.End) - document.GetWallType(other)!.Structure.TotalWidth / 2 - type.Width / 2)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            if (wall < 300 - 0.5)
                problems.Add(new PlacementProblem(piece, $"it is {wall:0} from the wall.",
                    type.Form == ComponentForm.CarbonMonoxideAlarm
                        ? "Approved Document J (2.36, 3.45) puts a carbon monoxide alarm on the ceiling 300 at least from any wall."
                        : "BS 5839-6, which Approved Document B (1.1) follows, puts an alarm on the ceiling 300 at least from the walls and the lights.",
                    footprint));

            var light = All(document, piece.LevelId, other => other.Kind == Elements.BuiltInCategory.LightingFixtures)
                .Select(entry => entry.Piece.Location.DistanceTo(piece.Location) - (entry.Type.Width + type.Width) / 2)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            if (type.Form != ComponentForm.CarbonMonoxideAlarm && light < 300 - 0.5)
                problems.Add(new PlacementProblem(piece, $"it is {light:0} from a light.",
                    "BS 5839-6, which Approved Document B (1.1) follows, puts an alarm on the ceiling 300 at least from the walls and the lights.", footprint));
        }

        switch (type.Form)
        {
            case ComponentForm.SmokeAlarm when !us && inRoom.Any(entry => entry.Type.Form is ComponentForm.Cooker or ComponentForm.Bath or ComponentForm.Shower):
                problems.Add(new PlacementProblem(piece, "it is in a kitchen or a bathroom, where cooking or steam will set it off.",
                    "BS 5839-6 keeps smoke alarms out of kitchens and bathrooms; a heat alarm goes in a kitchen.", footprint));
                break;

            case ComponentForm.SmokeAlarm when us:
            {
                var cooker = All(document, piece.LevelId, other => other.Form == ComponentForm.Cooker)
                    .Select(entry => Kitchens.Apart(ComponentModel.Footprint(entry.Type, ComponentModel.FrameOf(entry.Piece, 0)), footprint))
                    .DefaultIfEmpty(double.MaxValue)
                    .Min();
                if (cooker < 1828 - 0.5)
                    problems.Add(new PlacementProblem(piece, $"it is {Units.Bare(cooker)} from the cooker.",
                        $"IRC R314.3.1 keeps a smoke alarm 6 ft{Units.MetricAside("1828")} from a cooking appliance - a photoelectric one; an ionization one 20 ft.", footprint));

                var bathroomDoor = document.All<Room>()
                    .Where(other => other.LevelId == piece.LevelId)
                    .Select(other => (Room: other, Boundary: other.GetBoundary(document)))
                    .Where(other => other.Boundary.IsEnclosed && All(document, piece.LevelId, t => t.Form is ComponentForm.Bath or ComponentForm.Shower)
                        .Any(entry => Polygon2D.Contains(other.Boundary.Polygon, entry.Piece.Location)))
                    .SelectMany(other => Doors(document, other.Room, other.Boundary.Polygon))
                    .Select(door => door.DistanceTo(piece.Location))
                    .DefaultIfEmpty(double.MaxValue)
                    .Min();
                if (bathroomDoor < 914 - 0.5)
                    problems.Add(new PlacementProblem(piece, $"it is {Units.Bare(bathroomDoor)} from a bathroom's door.",
                        $"IRC R314.3 keeps a smoke alarm 3 ft{Units.MetricAside("914")} from the door of a bathroom with a tub or shower, out of its steam.", footprint));
                break;
            }

            case ComponentForm.CarbonMonoxideAlarm when !us:
            {
                var near = Appliances(document, piece.LevelId)
                    .Where(appliance => outline is null || Polygon2D.Contains(outline, appliance.At))
                    .Select(appliance => (appliance.Name, Apart: appliance.At.DistanceTo(piece.Location)))
                    .OrderBy(appliance => appliance.Apart)
                    .FirstOrDefault();
                if (near.Name is not null && (near.Apart < 1000 - 0.5 || near.Apart > 3000 + 0.5))
                    problems.Add(new PlacementProblem(piece, $"it is {Units.Metres(near.Apart, "0.0")} from {near.Name}.",
                        "Approved Document J (2.36, 3.45) puts a carbon monoxide alarm 1 to 3 m from the appliance, in its room.", footprint));
                break;
            }
        }

        return problems;
    }

    /// <summary>Every alarm's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Component>()
            .Where(piece => piece.LevelId == levelId)
            .SelectMany(piece => Problems(document, piece, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(piece, "its place", problem.Outline, problem.Rule), string.Empty,
                    document.FindType<ComponentType>(piece.TypeId)?.Name ?? "An alarm", problem.Said)))
            .ToList();

    /// <summary>Whether a piece is part of a fire detection and alarm system to BS 5839-1: a call point, a sounder or beacon, a detector, the panel.</summary>
    public static bool SystemPart(ComponentType type) =>
        type.Form is ComponentForm.FireAlarmCallPoint or ComponentForm.FireAlarmSounder or ComponentForm.VisualAlarm or ComponentForm.SmokeDetector
            or ComponentForm.HeatDetector or ComponentForm.FireAlarmPanel;

    /// <summary>Whether a piece detects fire automatically for such a system: a point smoke or heat detector.</summary>
    public static bool Detector(ComponentType type) => type.Form is ComponentForm.SmokeDetector or ComponentForm.HeatDetector;

    /// <summary>The escape routes - halls, corridors, lobbies, stairs - and the rooms with a door onto one: where an L3 system detects (BS 5839-1).</summary>
    private static HashSet<Room> OntoEscapeRoutes(BimDocument document)
    {
        var rooms = document.All<Room>().ToHashSet();
        var found = rooms.Where(FireSeparation.Circulation).ToHashSet();
        foreach (var link in Escape.Links(document, rooms))
            if (link.Other is { } other && FireSeparation.Circulation(other)) found.Add(link.Room);
        return found;
    }

    /// <summary>A fact about the whole building, the same for every room: while the model is drawn, found once.</summary>
    private static T Kept<T>(BimDocument document, string what, Func<T> work) =>
        ModelCache.Of(document) is { } cache ? cache.Keep($"alarms: {what}", document, null, null, work) : work();

    /// <summary>The fire detection and alarm system's parts in the building, where they are.</summary>
    private static IReadOnlyList<(Component Piece, ComponentType Type)> SystemParts(BimDocument document) => Kept(document, "system parts", () =>
        document.All<Component>()
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type is not null && SystemPart(entry.Type))
            .Select(entry => (entry.Piece, entry.Type!))
            .ToList());

    /// <summary>The largest enclosed room on a storey - or, given none, on the lowest storey with rooms: where a storey's or the building's wants are said.</summary>
    private static Room? Largest(BimDocument document, Guid? levelId) => Kept(document, $"largest room {levelId}", () =>
    {
        var rooms = document.All<Room>()
            .Select(room => (Room: room, Boundary: room.GetBoundary(document), Level: document.FindLevel(room.LevelId)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Level is not null && (levelId is null || entry.Room.LevelId == levelId) && !Domestic.Rules(document, entry.Room))
            .ToList();
        if (rooms.Count == 0) return null;
        var lowest = rooms.Min(entry => entry.Level!.Elevation);
        return rooms.Where(entry => levelId is not null || entry.Level!.Elevation <= lowest + 1).MaxBy(entry => entry.Boundary.Area).Room;
    });

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A part of the building not visited on a regular basis: a store, a stock room, an archive, a plant, boiler, tank, server or switch room (1.5).</summary>
    public static bool Unvisited(Room room) =>
        Named(room, "store", "storage", "stock", "archive", "plant", "boiler", "tank room", "server", "comms", "switch room", "switchroom");

    /// <summary>The circulation space a room's door opens into - a corridor, a lobby, a hall, a landing, a stair - or null where none does.</summary>
    private static Room? OpensOnto(BimDocument document, Room room)
    {
        foreach (var door in document.All<Door>())
        {
            if (document.Find<Wall>(door.HostWallId)?.LevelId != room.LevelId) continue;
            var (swing, other) = Architecture.Doors.RoomsBeside(document, door);
            var beyond = swing == room ? other : other == room ? swing : null;
            if (beyond is not null && (FireSeparation.Circulation(beyond) || Named(beyond, "passage", "foyer"))) return beyond;
        }

        return null;
    }

    /// <summary>
    /// What a room of a building other than a dwelling lacks of the fire detection and alarm system Approved Document B Volume 2 Section 1 asks:
    /// an electrically operated fire alarm system in all but small premises (1.2, 1.3), to BS 5839-1 (1.8) - said on the lowest storey's largest
    /// room; automatic fire detection on every storey of a hotel, a hostel, a hospital or a care home (1.4) - on each storey's largest; in a store
    /// or a plant room opening onto the escape route, where a fire could break out unseen and fill it (1.5) - in it, unless sprinklers, which can
    /// work the alarm (1.3, note), are fitted; and to open an automatic smoke vent (1.6). BS 5839-1's own siting of call points, detectors and
    /// sounders, and its categories (Table A1, 1.7), are not checked.
    /// </summary>
    private static string? Volume2(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        if (!HorizontalEscape.Applies(document)) return null;

        var parts = SystemParts(document);
        var group = HorizontalEscape.Purpose(document);
        var residential = group is PurposeGroup.ResidentialInstitutional or PurposeGroup.ResidentialOther;
        var detectors = parts.Where(entry => Detector(entry.Type)).ToList();
        var wanting = new List<string>();

        if (!parts.Any(entry => entry.Type.Form is ComponentForm.FireAlarmCallPoint or ComponentForm.FireAlarmSounder or ComponentForm.FireAlarmPanel)
            && Largest(document, null) == room)
            wanting.Add("no fire alarm system in the building - Approved Document B Volume 2 (1.3, 1.8) asks an electrically operated fire alarm system to BS 5839-1, call points, sounders and its panel, " +
                        "in all but small premises where a shouted warning is heard and understood throughout (1.2)");

        if (residential && Largest(document, room.LevelId) == room && !detectors.Any(entry => entry.Piece.LevelId == room.LevelId))
            wanting.Add($"no automatic fire detector on this storey - Approved Document B Volume 2 (1.4) asks automatic fire detection and alarms in {HorizontalEscape.Said(group)}, " +
                        "the system's category by BS 5839-1 Table A1 (1.7)");

        // A care home: an L1 system, detection throughout (2.41, round 129).
        if (CareHomes.Applies(document) && !CareHomes.Sanitary(room) && Polygon2D.Area(outline) >= 1e6
            && !detectors.Any(entry => entry.Piece.LevelId == room.LevelId && Polygon2D.Contains(outline, entry.Piece.Location)))
            wanting.Add("no automatic fire detector in it - Approved Document B Volume 2 (2.41) asks a care home a fire detection and alarm system to L1 of BS 5839-1, detection throughout - " +
                        "taken as in every room but WCs, bathrooms and shower rooms and cupboards under 1 m²");

        if (!residential && Unvisited(room) && !document.ProjectInformation.Sprinklered && OpensOnto(document, room) is { } route
            && !detectors.Any(entry => entry.Piece.LevelId == room.LevelId && Polygon2D.Contains(outline, entry.Piece.Location)))
            wanting.Add($"no automatic fire detector in it - a fire could break out unseen here, a part of the building not visited regularly, and fill the {route.Name} " +
                        "the escape goes along: Approved Document B Volume 2 (1.5) asks automatic fire detection and alarms where it could");

        // Designed for phased evacuation: an L3 system at least, detection on the escape routes and in the rooms opening onto them (3.21e).
        if (VerticalEscape.Phased(document) && !CareHomes.Sanitary(room) && Polygon2D.Area(outline) >= 1e6
            && Kept(document, "escape routes and rooms onto them", () => OntoEscapeRoutes(document)).Contains(room)
            && !detectors.Any(entry => entry.Piece.LevelId == room.LevelId && Polygon2D.Contains(outline, entry.Piece.Location)))
            wanting.Add($"no automatic fire detector in it - Approved Document B Volume 2 (3.21e) asks a building designed for phased evacuation a fire warning system to L3 of BS 5839-1 at least: detection on the escape routes and in the rooms opening onto them, {(FireSeparation.Circulation(room) ? "this one an escape route" : "this one opening onto one")}");

        var automaticVents = Kept(document, "automatic smoke vents", () =>
            document.All<Window>().Select(window => SmokeVents.Of(document, window)).Concat(document.All<RoofWindow>().Select(window => SmokeVents.Of(document, window)))
                .Count(vent => vent is { Kind: SmokeVentKind.Automatic }));
        if (automaticVents > 0 && detectors.Count == 0 && Largest(document, null) == room)
            wanting.Add($"{(automaticVents == 1 ? "an automatic opening vent opens" : $"{automaticVents} automatic opening vents open")} on detecting smoke, and nothing detects it - " +
                        "Approved Document B Volume 2 (1.6) asks automatic fire detection where fire protection systems are to work automatically");

        return wanting.Count == 0 ? null : $"Alarms: {string.Join("; ", wanting)}";
    }

    /// <summary>
    /// What a room lacks for its alarms - a smoke alarm on its storey, near its door or in it, a
    /// heat alarm, a carbon monoxide alarm by its stove or boiler - or null where it lacks nothing.
    /// In a building other than a dwelling, what its fire detection and alarm system lacks instead.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;
        if (!Domestic.Rules(document, room)) return Volume2(document, room, boundary.Polygon);

        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var everything = All(document, room.LevelId, _ => true);
        var inRoom = everything.Where(entry => Polygon2D.Contains(boundary.Polygon, entry.Piece.Location)).ToList();
        var smoke = everything.Where(entry => entry.Type.Form == ComponentForm.SmokeAlarm).Select(entry => entry.Piece.Location).ToList();
        bool Has(ComponentForm form) => inRoom.Any(entry => entry.Type.Form == form);

        var bedroom = Has(ComponentForm.Bed) || Has(ComponentForm.BunkBed);
        var wet = Has(ComponentForm.Bath) || Has(ComponentForm.Shower) || inRoom.Any(entry => Bathrooms.IsWc(entry.Type));
        var kitchen = Has(ComponentForm.Cooker);
        var doors = Doors(document, room, boundary.Polygon);
        double NearestSmoke(Point2D at) => smoke.Count == 0 ? double.MaxValue : smoke.Min(alarm => alarm.DistanceTo(at));

        var wanting = new List<string>();

        // In an apartment building, the IBC's 907.2.11.2, which asks the same - each story within a dwelling unit its own, its rooms' alarms (round 144).
        var code = Apartments.Applies(document) ? "IBC 907.2.11.2" : "IRC R314.3";
        if (Apartments.Applies(document) && Dwellings.Of(room).Length > 0)
            smoke = smoke.Where(alarm => RoomsOf.Containing(document, room.LevelId, alarm) is { } holding && Dwellings.Same(holding, room)).ToList();

        // In the US, on a split level with no door between it and the level half a storey up, that level's alarm does for it (IRC R314.3 item 3, round 143).
        var coveredFromAbove = us && smoke.Count == 0 && SplitLevels.CoveredFrom(document, room.LevelId) is { } over
                               && All(document, over.Id, type => type.Form == ComponentForm.SmokeAlarm).Count > 0;
        // An apartment building's common parts - corridors, stairs - ask no smoke alarm of their own: its fire alarm is 907.2.9's.
        var commonParts = Apartments.Applies(document) && Dwellings.Of(room).Length == 0;
        if (smoke.Count == 0 && !coveredFromAbove && !commonParts)
            wanting.Add(us
                ? $"no smoke alarm on this storey - {code} asks for one on every storey{(code.StartsWith("IBC", StringComparison.Ordinal) ? " within a dwelling unit" : string.Empty)}"
                : "no smoke alarm on this storey - BS 5839-6, which Approved Document B (1.1) follows, asks for one on every storey");
        else if (us)
        {
            if (bedroom && !Has(ComponentForm.SmokeAlarm)) wanting.Add($"no smoke alarm in this bedroom - {code} asks for one in every bedroom");

            var outside = smoke.Where(alarm => !Polygon2D.Contains(boundary.Polygon, alarm)).ToList();
            if (bedroom && doors.Count > 0 && doors.Min(door => outside.Count == 0 ? double.MaxValue : outside.Min(alarm => alarm.DistanceTo(door))) > 3000 + 0.5)
                wanting.Add($"no smoke alarm by its door outside it - {code} asks for one outside every sleeping area, by the bedrooms");
        }
        else if (!wet && !kitchen && doors.Count > 0)
        {
            var reach = bedroom ? 3000 : 7500;
            var nearest = doors.Min(NearestSmoke);
            if (nearest > reach + 0.5)
                wanting.Add($"its door is {Units.Metres(nearest, "0.0")} from a smoke alarm - BS 5839-6 asks for one within {(bedroom ? "3 m of a bedroom's door" : "7.5 m of a room's door")}");
        }

        // A heat alarm in the kitchen of a house of three storeys or more: LD2.
        var storeys = document.Levels.Count(level => document.All<Room>().Any(other => other.LevelId == level.Id));
        if (!us && kitchen && storeys >= 3 && !Has(ComponentForm.HeatAlarm))
            wanting.Add("no heat alarm in the kitchen - Approved Document B (1.7) asks for an LD2 system, with one, in a house of three storeys or more");

        // A carbon monoxide alarm by a stove, a fire or a boiler.
        var appliances = Appliances(document, room.LevelId).Where(appliance => Polygon2D.Contains(boundary.Polygon, appliance.At)).ToList();
        if (appliances.Count > 0 && !Has(ComponentForm.CarbonMonoxideAlarm) && (!us || bedroom))
            wanting.Add(us
                ? $"no carbon monoxide alarm in this bedroom with {appliances[0].Name} in it - IRC R315.3 asks for one"
                : $"no carbon monoxide alarm by {appliances[0].Name} - Approved Document J (2.34, 3.43) asks for one in its room");

        return wanting.Count == 0 ? null : $"Alarms: {string.Join("; ", wanting)}";
    }
}
