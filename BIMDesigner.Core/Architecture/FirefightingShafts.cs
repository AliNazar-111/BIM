using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Firefighting shafts in a building other than a dwelling (round 116), as Approved Document B Volume 2 sets them out in Section 17 and
/// Diagrams 17.1 to 17.3 - heights and depths from the fire and rescue service's vehicle access, taken as the ground.
///
/// Asked: one or more with a firefighting lift where a storey is more than 18 m up (17.2) or a basement more than 10 m down (17.3a); without
/// one, where there are two basement storeys or more of 900 m² each (17.3b); two at least where a storey of 900 m² or more is 18 m up (17.5),
/// or - a shop, a place of assembly or a factory - 7.5 m up, without lifts (17.6). A storey's area its gross internal area.
///
/// A firefighting shaft is an escape stair whose rooms or lobbies are called firefighting - "Firefighting Stair", "Firefighting Lobby" - and a
/// firefighting lift is a lift whose type says it is one, to BS EN 81-72. Each shaft serving every storey from the top one asked, or down to the
/// lowest basement asked (17.4, Diagram 17.2); its lift, where one is asked, stopping at every storey it serves; approached from the
/// accommodation through a firefighting lobby on every storey (17.9); a fire main's outlet in its lobby or stair on every storey (17.10,
/// Diagram 17.1 note 1); enclosed, as Diagram 17.1 shows, in REI 120 from the accommodation - its doors into the lobby E 60 Sa, self-closing -
/// and REI 60 within, between its lobby, stair and lift well, the doors from the lobby to the stair E 30 Sa. On every storey more than 18 m up,
/// every point within 60 m of hose from a fire main outlet in a firefighting shaft; and without sprinklers within 45 m of an outlet in a
/// protected stair (17.7, 17.8, Diagram 17.3).
///
/// Not done: storeys of plant alone left out of the height (Diagram 17.2 note 1); the shafts' smoke venting by BS 9999 (17.9); only the
/// shaft's own services in it (17.9); the firefighting lift's controls and communications (17.11); rolling shutters (17.12); the REI 60 a shaft's
/// walls stand from inside it, taken as their rating.
/// </summary>
public static class FirefightingShafts
{
    /// <summary>The heights from which shafts are asked - a storey 18 m up, a basement 10 m down, a commercial storey 7.5 m up - mm; a large storey, m².</summary>
    public const double Height = 18000, Depth = 10000, Commercial = 7500, Large = 900;

    /// <summary>Hose from a fire main outlet in a firefighting shaft, and without sprinklers from one in a protected shaft, mm (17.8).</summary>
    public const double Reach = 60000, Unsprinklered = 45000;

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a lift is a firefighting lift: its type said to be one.</summary>
    public static bool FirefightingLift(ComponentType type) => type.Name.Contains("firefighting", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a building asks: how many shafts, whether with a firefighting lift, whether to its upper storeys and its basements, and why, said.</summary>
    public sealed record Asked(int Shafts, bool Lift, bool Upper, bool Basement, string Why, string Cited);

    /// <summary>The storeys with rooms on them, each with its height above the ground, mm, and its gross internal area, m².</summary>
    private static List<(Level Level, double Above, double Area)> Storeys(BimDocument document)
    {
        var ground = Escape.Ground(document);
        var used = document.All<Room>().Where(room => room.GetBoundary(document) is { IsEnclosed: true }).Select(room => room.LevelId).ToHashSet();
        return document.Levels.Where(level => used.Contains(level.Id)).OrderBy(level => level.Elevation)
            .Select(level => (level, level.Elevation - ground, AreaPlans.Measure(document, level.Id, AreaScheme.GrossInternal).Area / 1e6)).ToList();
    }

    /// <summary>The firefighting shafts Section 17 asks of a building other than a dwelling; null for none.</summary>
    public static Asked? Needed(BimDocument document)
    {
        if (!HorizontalEscape.Applies(document)) return null;
        var storeys = Storeys(document);
        if (storeys.Count == 0) return null;
        var group = HorizontalEscape.Purpose(document);
        var why = new List<string>();
        var cited = new List<string>();
        var top = storeys.Max(storey => storey.Above);
        var high = top > Height + 0.5;
        var deep = storeys.Any(storey => storey.Above < -Depth - 0.5);
        var basements = storeys.Where(storey => storey.Above < -Escape.Basement - 0.5).ToList();
        var bigBasements = basements.Count >= 2 && basements.All(storey => storey.Area >= Large - 1e-6);
        var two = storeys.Any(storey => storey.Area >= Large - 1e-6 && storey.Above >= Height - 0.5);
        var commercial = group is PurposeGroup.ShopAndCommercial or PurposeGroup.AssemblyAndRecreation or PurposeGroup.Industrial
                         && storeys.Any(storey => storey.Area >= Large - 1e-6 && storey.Above >= Commercial - 0.5);
        if (high) { why.Add($"its top storey is {top / 1000:0.0} m up"); cited.Add("17.2"); }
        if (deep) { why.Add($"a basement is {-storeys.Min(storey => storey.Above) / 1000:0.0} m down"); cited.Add("17.3a"); }
        if (bigBasements) { why.Add($"it has {basements.Count} basement storeys of {Large:0} m² or more"); cited.Add("17.3b"); }
        if (two) { why.Add($"a storey of {Large:0} m² or more is 18 m up or more"); cited.Add("17.5"); }
        if (commercial && !two) { why.Add($"a storey of {Large:0} m² or more of {HorizontalEscape.Said(group)} is 7.5 m up or more"); cited.Add("17.6"); }
        if (why.Count == 0) return null;
        return new Asked(two || commercial ? 2 : 1, high || deep, high || commercial, deep || bigBasements, string.Join(", and ", why), string.Join(", ", cited));
    }

    /// <summary>A firefighting shaft as drawn: its stair, its lobbies, and its firefighting lift, where it has one.</summary>
    public sealed record Shaft(VerticalEscape.StairStack Stair, IReadOnlyList<Room> Lobbies, Lifts.Placed? Lift);

    /// <summary>The building's firefighting shafts: its escape stairs whose rooms or lobbies are called firefighting, each with the firefighting lift landing into them.</summary>
    public static IReadOnlyList<Shaft> Of(BimDocument document)
    {
        var links = Escape.Links(document, document.All<Room>().ToHashSet());
        var shafts = new List<Shaft>();
        foreach (var stair in VerticalEscape.Stairs(document))
        {
            var lobbies = links.Where(link => stair.Rooms.Contains(link.Room) && link.Other is { } other && !stair.Rooms.Contains(other) && Circulation.IsLobby(other))
                .Select(link => link.Other!).Distinct().ToList();
            if (!stair.Rooms.Any(room => Named(room, "firefighting")) && !lobbies.Any(room => Named(room, "firefighting"))) continue;
            var within = stair.Rooms.Concat(lobbies).ToHashSet();
            var lift = Lifts.Of(document).FirstOrDefault(each => FirefightingLift(each.Type)
                && each.Landings.Any(landing => Doors.RoomsBeside(document, landing.Door) is var (a, b) && (a is not null && within.Contains(a) || b is not null && within.Contains(b))));
            shafts.Add(new Shaft(stair, lobbies, lift));
        }

        return shafts;
    }

    /// <summary>What a building other than a dwelling falls short of in its firefighting shafts, each said on a room - a shaft's on its stair's foot.</summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!HorizontalEscape.Applies(document) || document.ProjectInformation.ClearanceStandard.IsUs()) yield break;
        var asked = Needed(document);
        if (asked is null) yield break;
        var shafts = Of(document);
        var storeys = Storeys(document);
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
        Room First(Guid level) => rooms.Where(entry => entry.Key.LevelId == level).MaxBy(entry => Math.Abs(Polygon2D.SignedArea(entry.Value))).Key;
        var topStorey = storeys[^1];
        var lowest = storeys[0];

        if (shafts.Count < asked.Shafts)
            yield return new EscapeProblem(First(topStorey.Level.Id),
                $"{asked.Why}, and it has {(shafts.Count == 0 ? "no firefighting shaft" : $"{shafts.Count} firefighting shaft")} - Approved Document B Volume 2 ({asked.Cited}, Diagram 17.1) asks {(asked.Shafts == 1 ? "one at least" : "two at least")}{(asked.Lift ? ", with a firefighting lift" : string.Empty)}: a firefighting stair approached through a firefighting lobby on every storey, with a fire main - call a stair's rooms Firefighting Stair and its lobbies Firefighting Lobby{(asked.Lift ? ", and put in a firefighting lift" : string.Empty)}");

        var links = Escape.Links(document, rooms.Keys.ToHashSet());
        var outlets = document.All<Component>().Where(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.FireMainOutlet).ToList();
        string Called(Guid level) => document.FindLevel(level)?.Name ?? "storey";
        foreach (var shaft in shafts)
        {
            var stair = shaft.Stair;
            var foot = stair.Foot;
            var levels = stair.Rooms.Select(room => room.LevelId).Distinct().ToList();
            double Elevation(Guid level) => document.FindLevel(level)?.Elevation ?? 0;
            var said = $"its firefighting shaft";

            // Its extent: to the top storey, down to the lowest basement where asked (17.4, Diagram 17.2).
            if (asked.Upper && levels.Max(Elevation) < topStorey.Level.Elevation - 1)
                yield return new EscapeProblem(foot, $"{said} stops at the {Called(levels.MaxBy(Elevation))}, short of the {topStorey.Level.Name} - Approved Document B Volume 2 (17.4, Diagram 17.2) asks it to serve the storeys above up to the top one");
            if (asked.Basement && levels.Min(Elevation) > lowest.Level.Elevation + 1)
                yield return new EscapeProblem(foot, $"{said} stops at the {Called(levels.MinBy(Elevation))}, short of the {lowest.Level.Name} - Approved Document B Volume 2 (17.3, Diagram 17.2) asks it to serve the basement storeys down to the lowest");

            // Its firefighting lift, stopping at every storey it serves (17.2, 17.3a, Diagram 17.1 note 2).
            if (asked.Lift)
            {
                if (shaft.Lift is not { } lift)
                    yield return new EscapeProblem(foot, $"{said} has no firefighting lift landing into it - Approved Document B Volume 2 (17.2, 17.3a, Diagram 17.1 note 2) asks one where a storey is more than 18 m up or a basement more than 10 m down, to BS EN 81-72: put in a lift whose type is a firefighting lift, its landing doors into the firefighting lobbies");
                else if (levels.Where(level => lift.Landings.All(landing => landing.Level.Id != level)).Select(Called).ToList() is { Count: > 0 } missed)
                    yield return new EscapeProblem(foot, $"{said}'s lift does not stop at the {string.Join(", the ", missed)} - Approved Document B Volume 2 (17.4) asks it to serve every storey the shaft passes");
            }

            // A firefighting lobby between the accommodation and the stair on every storey (17.9).
            var open = stair.Rooms.Where(room => links.Any(link => link.Room == room && link.Other is { } other && !stair.Rooms.Contains(other) && !Circulation.IsLobby(other))
                                                 || !links.Any(link => link.Room == room && link.Other is { } other && Circulation.IsLobby(other)))
                .Select(room => Called(room.LevelId)).Distinct().ToList();
            if (open.Count > 0)
                yield return new EscapeProblem(foot, $"{said}'s stair has no firefighting lobby between it and the accommodation on the {string.Join(", the ", open)} - Approved Document B Volume 2 (17.9, Diagram 17.1) asks every firefighting stair and lift approached from the accommodation through a firefighting lobby");

            // A fire main's outlet in its lobby or stair on every storey (17.10, Diagram 17.1 note 1).
            var shaftRooms = stair.Rooms.Concat(shaft.Lobbies).ToHashSet();
            var dry = levels.Where(level => !outlets.Any(outlet => outlet.LevelId == level && shaftRooms.Any(room => room.LevelId == level && rooms.TryGetValue(room, out var outline) && Polygon2D.Contains(outline, outlet.Location))))
                .Select(Called).ToList();
            if (dry.Count > 0)
                yield return new EscapeProblem(foot, $"{said} has no fire main outlet on the {string.Join(", the ", dry)} - Approved Document B Volume 2 (17.10, Diagram 17.1 note 1) asks a fire main in every firefighting shaft, its outlets in the firefighting lobby at every storey");

            // Its enclosure (Diagram 17.1): REI 120 to the accommodation, REI 60 within; doors into the lobby E 60 Sa, from it to the stair E 30 Sa.
            // Its lift well - a room called a lift or shaft off its lobbies, the lift in it or not yet - part of it.
            foreach (var link in links.Where(link => shaft.Lobbies.Contains(link.Room) && link.Other is { } other && Named(other, "lift", "shaft")))
                shaftRooms.Add(link.Other!);
            var weak = new List<string>();
            foreach (var wall in document.Walls.Where(wall => levels.Contains(wall.LevelId)))
            {
                if (document.GetWallType(wall) is not { Function: not WallFunction.Exterior } type || WallRules.RoomsBeside(document, wall) is not ({ } one, { } other) || one == other) continue;
                var (inOne, inOther) = (shaftRooms.Contains(one), shaftRooms.Contains(other));
                if (!inOne && !inOther) continue;
                var asks = inOne && inOther ? 60 : 120;
                if (Doors.FireMinutes(type.FireRating) < asks)
                    weak.Add($"{type.Name} between the {one.Name} and the {other.Name} on the {Called(wall.LevelId)}, REI {asks} asked");
            }

            if (weak.Count > 0)
                yield return new EscapeProblem(foot, $"{said}'s walls stand short: {string.Join("; ", weak.Distinct())} - Approved Document B Volume 2 (Diagram 17.1) asks a firefighting shaft REI 120 from the accommodation and REI 60 between its lobby, stair and lift well");

            var doors = new List<string>();
            foreach (var link in links.Where(link => shaft.Lobbies.Contains(link.Room) && link.Other is { } other && !Lifts.IsLanding(document, link.Door)))
            {
                var toStair = stair.Rooms.Contains(link.Other!);
                if (!toStair && shaftRooms.Contains(link.Other!)) continue;
                var asks = toStair ? 30 : 60;
                if (document.FindType<DoorType>(link.Door.TypeId) is not { } doorType || Doors.FireMinutes(doorType.FireRating) < asks || !doorType.SelfClosing)
                    doors.Add($"the {link.Room.Name}'s door to the {link.Other!.Name} on the {Called(link.Room.LevelId)}, E {asks} Sa asked");
            }

            if (doors.Count > 0)
                yield return new EscapeProblem(foot, $"{said}'s doors stand short: {string.Join("; ", doors.Distinct())} - Approved Document B Volume 2 (Diagram 17.1, Table C1) asks the doors from the accommodation into a firefighting lobby E 60 Sa and from the lobby to the stair E 30 Sa, self-closing");
        }

        // Hose on every storey more than 18 m up: 60 m from an outlet in a firefighting shaft; without sprinklers, 45 m from one in a protected stair (17.7, 17.8).
        var high = storeys.Where(storey => storey.Above > Height + 0.5).Select(storey => storey.Level.Id).ToHashSet();
        if (high.Count == 0 || shafts.Count == 0) yield break;
        var flights = Escape.Flights(document, rooms);
        var walk = new Escape.Walk(rooms, links, flights.Where(flight => flight.Foot is not null && flight.Head is not null)
            .Select(flight => (flight.Foot!, flight.FootAt, flight.Head!, flight.HeadAt, flight.Length)));
        List<(Room, Point2D, double)> From(Func<Room, bool> where) => outlets
            .Select(outlet => (Outlet: outlet, Room: rooms.FirstOrDefault(entry => entry.Key.LevelId == outlet.LevelId && Polygon2D.Contains(entry.Value, outlet.Location)).Key))
            .Where(entry => entry.Room is not null && where(entry.Room)).Select(entry => (entry.Room!, entry.Outlet.Location, 0.0)).ToList();
        var inShafts = shafts.SelectMany(shaft => shaft.Stair.Rooms.Concat(shaft.Lobbies)).ToHashSet();
        var stairs = VerticalEscape.Stairs(document).SelectMany(stair => stair.Rooms).ToHashSet();
        var firefighting = From(inShafts.Contains);
        var protectedShafts = From(room => stairs.Contains(room) || Circulation.IsLobby(room));
        var fromShafts = firefighting.Count > 0 ? walk.From(firefighting) : null;
        var fromProtected = !document.ProjectInformation.Sprinklered && protectedShafts.Count > 0 ? walk.From(protectedShafts) : null;
        foreach (var (room, outline) in rooms.Where(entry => high.Contains(entry.Key.LevelId) && !inShafts.Contains(entry.Key) && !stairs.Contains(entry.Key)))
        {
            double Furthest(Func<Room, Point2D, double?> from) => Escape.Points(outline).Select(point => from(room, point) ?? double.PositiveInfinity).DefaultIfEmpty(0).Max();
            var reach = fromShafts is null ? double.PositiveInfinity : Furthest(fromShafts);
            if (reach > Reach + 0.5)
                yield return new EscapeProblem(room, $"its furthest point is {(double.IsPositiveInfinity(reach) ? "out of reach" : $"{Units.Metres(reach, "0.0")} along the hose's route")} from a fire main outlet in a firefighting shaft - Approved Document B Volume 2 (17.7, 17.8a, Diagram 17.3) asks 60 m at most on every storey more than 18 m up");
            if (document.ProjectInformation.Sprinklered) continue;
            var near = fromProtected is null ? double.PositiveInfinity : Furthest(fromProtected);
            if (near > Unsprinklered + 0.5)
                yield return new EscapeProblem(room, $"its furthest point is {(double.IsPositiveInfinity(near) ? "out of reach" : $"{Units.Metres(near, "0.0")} along the hose's route")} from a fire main outlet in a protected stair or lobby, and the building has no sprinklers - Approved Document B Volume 2 (17.8b, Diagram 17.3) asks 45 m at most as well");
        }
    }
}
