using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The rest of Approved Document B Volume 2's Section 5 in a building other than a dwelling (round 114): final exits, lift wells, refuse stores
/// and shops' store rooms.
///
/// A stair's final exit as wide as the stair needs to be - Table 3.1's least, or wider where Table 3.2 asks it for the people of the storeys
/// above (5.20; in VerticalEscape). Every final exit - a door out of a room that is no refuse store, place of special fire hazard or plant room -
/// clear of a basement's smoke outlets and of the doors and opening windows of refuse stores and boiler, transformer, switch, fuel, generator
/// and engine rooms (5.24, 5.45): 3 m apart, edge to edge, as "avoid" and "not next to" are read here - the Approved Document gives no distance.
///
/// A lift's shaft - the room drawn round its car - on each storey it stops at within a protected stairway's enclosure (5.34a), or enclosed in
/// REI 30, its landing doors E 30 (5.34b, Table C1 item 4b); where it passes a compartment floor a protected shaft, its walls the building's
/// period and its landing doors half that, 30 minutes at least (5.35, Table C1 item 2d). On a basement storey its landing door into the stair, or
/// into a protected lobby or corridor - a lobby or corridor whose other walls are REI 30 (5.38). Not down to a basement as well as up to the
/// storeys above the ground where it is within the enclosure of an escape stair ending at the ground (5.40b), or there is only one escape stair
/// from the upper storeys (5.40a).
///
/// A refuse store enclosed in REI 30 - REI 60 where the top storey is more than 5 m up (5.43a); not off a protected stairway or its lobby
/// (5.43b); come to from the open air, or through a lobby (5.44). A shop's walk-in store room - 2 m² or more - separated from the retail areas
/// and escape routes beside it by REI 30, unless it has a smoke or heat detector in it or the building sprinklers (5.46).
///
/// Not done: final exits' level thresholds and the way away from the building (5.21 to 5.23); lifts in a smoke reservoir (5.36), phased
/// evacuation (5.37), sleeping accommodation's lifts (5.39), machine rooms (5.41); BS 5906's chutes and hoppers (5.42); a lobby's 0.2 m² of
/// permanent ventilation (5.44); the doors of refuse stores and store rooms, which Table C1 does not name.
/// </summary>
public static partial class EscapeRoutes
{
    /// <summary>How far a final exit is kept from a basement's smoke outlet or a hazard's opening, edge to edge, mm - "avoid", as read here (5.24).</summary>
    public const double Near = 3000;

    /// <summary>The least floor area of a walk-in store room, m² (5.46).</summary>
    public const double WalkInArea = 2;

    /// <summary>A room whose openings final exits are kept clear of: a refuse store, a place of special fire hazard.</summary>
    private static bool Hazard(Room room) => FireSeparation.Refuse(room) || FireSeparation.Special(room);

    /// <summary>What final exits are kept clear of: each basement smoke outlet, and each door or opening window of a hazard room to the outside - where, how wide, said.</summary>
    private static IReadOnlyList<(Guid? Level, Point2D At, double Width, string Said, bool Refuse)> Risks(BimDocument document)
    {
        IReadOnlyList<(Guid?, Point2D, double, string, bool)> Work()
        {
            var risks = new List<(Guid?, Point2D, double, string, bool)>();
            foreach (var piece in document.All<Component>())
                if (document.FindType<ComponentType>(piece.TypeId) is { Form: ComponentForm.SmokeOutlet } outlet)
                    risks.Add((null, piece.Location, outlet.Width, "a basement smoke outlet", false));
            foreach (var opening in document.All<Opening>())
            {
                if (document.Find<Wall>(opening.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) continue;
                var (width, what) = opening switch
                {
                    Door door when document.FindType<DoorType>(door.TypeId) is { } type => (type.Width, "door"),
                    Window window when document.FindType<WindowType>(window.TypeId) is { } type && WindowLights.Opens(type) => (type.Width, "window"),
                    _ => (0.0, string.Empty)
                };
                if (width <= 0) continue;
                var reach = wallType.Structure.TotalWidth / 2 + 150;
                var one = RoomsOf.Containing(document, wall.LevelId, wall.PointAt(wallType.Structure, opening.DistanceAlongWall, reach));
                var other = RoomsOf.Containing(document, wall.LevelId, wall.PointAt(wallType.Structure, opening.DistanceAlongWall, -reach));
                var room = one is not null && other is null ? one : other is not null && one is null ? other : null;
                if (room is not null && Hazard(room))
                    risks.Add((wall.LevelId, wall.PointAt(wallType.Structure, opening.DistanceAlongWall, 0), width, $"the {room.Name}'s {what}", FireSeparation.Refuse(room)));
            }

            return risks;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("escape routes: risks", document, null, null, Work) : Work();
    }

    /// <summary>The room a door is a final exit from: a door to the outside from a room that is no refuse store, place of special fire hazard or plant room; else null.</summary>
    private static Room? FinalExitFrom(BimDocument document, Door door, DoorType type)
    {
        if (!Doors.External(document, door, type)) return null;
        var (one, other) = Doors.RoomsBeside(document, door);
        var inside = one is not null && other is null ? one : other is not null && one is null ? other : null;
        return inside is null || Hazard(inside) || Named(inside, "plant") ? null : inside;
    }

    /// <summary>What a final exit falls short of: sited clear of a basement's smoke outlets and the openings of refuse stores and places of special fire hazard (5.24, 5.45).</summary>
    private static IEnumerable<(string Said, string Rule)> FinalExitProblems(BimDocument document, Door door, DoorType type)
    {
        if (!Applies(document) || FinalExitFrom(document, door, type) is null || Middle(document, door) is not { } middle || document.Find<Wall>(door.HostWallId) is not { } wall) yield break;
        foreach (var (level, at, width, said, refuse) in Risks(document))
        {
            if (level is { } on && on != wall.LevelId) continue;
            var gap = Math.Max(0, middle.DistanceTo(at) - (type.Width + width) / 2);
            if (gap > Near + 0.5) continue;
            yield return ($"it is a final exit {gap / 1000:0.0} m from {said}.", refuse
                ? "Approved Document B Volume 2 (5.24, 5.45) asks final exits sited clear of the openings of refuse chambers, and access openings to refuse storage away from final exits - 3 m apart, as read here."
                : "Approved Document B Volume 2 (5.24) asks final exits sited clear of the outlets of basement smoke vents and the openings of transformer chambers, boiler rooms and similar risks - 3 m apart, as read here.");
        }
    }

    // --- Lift wells (5.34, 5.35, 5.38, 5.40) -------------------------------------------------

    /// <summary>Whether a room is a stair's.</summary>
    private static bool IsStair(BimDocument document, Room room) => Named(room, "stair") || Doors.StairRooms(document).Contains(room);

    /// <summary>A lift's shaft on a storey: the room round its car - called a lift or shaft, or no more than four times the car's area; else null.</summary>
    private static Room? Shaft(BimDocument document, Lifts.Placed lift, Guid level)
    {
        var centre = new Point2D(lift.Car.Average(point => point.X), lift.Car.Average(point => point.Y));
        if (RoomsOf.Containing(document, level, centre) is not { } room || room.GetBoundary(document) is not { IsEnclosed: true } boundary) return null;
        return Named(room, "lift", "shaft") || IsStair(document, room) || Polygon2D.Area(boundary.Polygon) <= 4 * Math.Abs(Polygon2D.SignedArea(lift.Car)) + 1 ? room : null;
    }

    /// <summary>The inside walls round a room on its storey, each with the room on its other side.</summary>
    private static List<(Wall Wall, WallType Type, Room? Other)> Round(BimDocument document, Room room) =>
        document.Walls.Where(wall => wall.LevelId == room.LevelId && document.GetWallType(wall) is { Function: not WallFunction.Exterior })
            .Select(wall => (Wall: wall, Type: document.GetWallType(wall)!, Beside: WallRules.RoomsBeside(document, wall)))
            .Where(entry => (entry.Beside.One == room || entry.Beside.Other == room) && entry.Beside.One != entry.Beside.Other)
            .Select(entry => (entry.Wall, entry.Type, entry.Beside.One == room ? entry.Beside.Other : entry.Beside.One))
            .ToList();

    /// <summary>Whether a room is a protected lobby or corridor: a lobby or a corridor, its walls but those to a shaft or a stair REI 30.</summary>
    private static bool ProtectedLobby(BimDocument document, Room room, Room shaft) =>
        (Circulation.IsLobby(room) || room.GetBoundary(document) is { IsEnclosed: true } boundary && Circulation.IsCorridor(room, boundary.Polygon))
        && Round(document, room).All(entry => entry.Other is null || entry.Other == shaft || IsStair(document, entry.Other) || Doors.FireMinutes(entry.Type.FireRating) >= 30);

    /// <summary>What a lift's well falls short of in a building other than a dwelling: its enclosure, its landing doors, a basement's lobby, and serving a basement at all.</summary>
    public static IEnumerable<(string Said, string Rule)> LiftProblems(BimDocument document, Lifts.Placed lift)
    {
        if (!Applies(document) || lift.Landings.Count == 0) yield break;
        var levels = lift.Landings.Select(landing => landing.Level).DistinctBy(level => level.Id).OrderBy(level => level.Elevation).ToList();
        var ground = Escape.Ground(document);
        var compartment = levels.Skip(1).Select(level => FireCompartments.CompartmentFloorAt(document, level.Elevation)).FirstOrDefault(why => why is not null);
        var period = compartment is null ? 30 : Math.Max(30, FireCompartments.Minutes(document));
        var doorAsked = compartment is null ? 30 : Math.Max(30, period / 2);

        var walls = new List<string>();
        var doors = new List<string>();
        var basements = new List<string>();
        var enclosing = new HashSet<Room>();
        foreach (var level in levels)
        {
            if (Shaft(document, lift, level.Id) is not { } shaft) continue;
            var round = Round(document, shaft);

            // Within a protected stairway's enclosure: the stair's room itself, or every inside wall of the shaft to the stair (5.34a).
            if (IsStair(document, shaft) || round.Count > 0 && round.All(entry => entry.Other is { } other && IsStair(document, other)))
            {
                enclosing.UnionWith(IsStair(document, shaft) ? new[] { shaft } : round.Select(entry => entry.Other!));
                continue;
            }

            foreach (var (_, type, other) in round)
                if (other is not null && !IsStair(document, other) && Doors.FireMinutes(type.FireRating) < period)
                    walls.Add($"{type.Name} to the {other.Name} on the {level.Name}");
            foreach (var (door, _) in lift.Landings.Where(landing => landing.Level.Id == level.Id))
            {
                if (document.FindType<DoorType>(door.TypeId) is { } doorType && Doors.FireMinutes(doorType.FireRating) < doorAsked)
                    doors.Add($"the {level.Name}'s, {(Doors.FireMinutes(doorType.FireRating) is > 0 and var rated ? $"rated {rated} minutes" : "not fire-rated")}");

                // On a basement storey: into the stair, or a protected lobby or corridor (5.38).
                if (level.Elevation < ground - Escape.Basement - 0.5)
                {
                    var (a, b) = Doors.RoomsBeside(document, door);
                    var into = a == shaft ? b : b == shaft ? a : a ?? b;
                    if (into is null || !IsStair(document, into) && !ProtectedLobby(document, into, shaft))
                        basements.Add($"the {level.Name}'s opens into the {into?.Name ?? "open"}");
                }
            }
        }

        if (walls.Count > 0)
            yield return ($"its shaft's walls - {string.Join("; ", walls.Distinct())} - stand less than {period} minutes.", compartment is null
                ? "Approved Document B Volume 2 (5.34b, Table B1) asks a lift well not within a protected stairway enclosed in fire-resisting construction, REI 30, where it might prejudice the means of escape."
                : $"Approved Document B Volume 2 (5.35, Section 8, Table B1) asks a lift well connecting compartments - {compartment} - a protected shaft, its walls REI {period}, the building's period.");
        if (doors.Count > 0)
            yield return ($"its landing door{(doors.Count == 1 ? string.Empty : "s")} - {string.Join("; ", doors)} - {(doors.Count == 1 ? "is" : "are")} less than E {doorAsked}.", compartment is null
                ? "Approved Document B Volume 2 (5.34b, Table C1 item 4b) asks the doors of a lift shaft enclosed in fire-resisting construction E 30."
                : $"Approved Document B Volume 2 (5.35, Table C1 item 2d) asks the doors of a protected shaft forming a lift half its walls' period, 30 minutes at least: E {doorAsked}.");
        if (basements.Count > 0)
            yield return ($"its landing door{(basements.Count == 1 ? string.Empty : "s")} - {string.Join("; ", basements)} - not a protected stairway, lobby or corridor.",
                "Approved Document B Volume 2 (5.38, Table C1 item 5c) asks a lift in a basement within the enclosure of a protected stairway, or approached only through a protected lobby or protected corridor, REI 30, its doors E 30 Sa.");

        // Down to a basement as well as up to the storeys above the ground (5.40).
        if (levels.Any(level => level.Elevation < ground - Escape.Basement - 0.5) && levels.Any(level => level.Elevation > ground + 100))
        {
            var stairs = VerticalEscape.Stairs(document);
            if (stairs.FirstOrDefault(stair => stair.Rooms.Any(enclosing.Contains)) is { Basement: false } stair)
                yield return ($"it serves the basement and the storeys above the ground from within the enclosure of the {stair.Foot.Name}, a stair that ends at the ground.",
                    "Approved Document B Volume 2 (5.40b) asks a lift shaft serving storeys above the ground not to serve a basement where it is within the enclosure of an escape stair that ends at ground level.");
            else if (stairs.Count(each => each.Upper.Count > 0) == 1)
                yield return ("it serves the basement and the storeys above the ground, which have only one escape stair.",
                    "Approved Document B Volume 2 (5.40a) asks a lift shaft serving storeys above the ground not to serve a basement where there is only one escape stair serving the storeys above and smoke from a basement fire would affect its escape routes.");
        }
    }

    // --- Refuse stores (5.43, 5.44) and shops' store rooms (5.46) ---------------------------------

    /// <summary>Whether a room is a shop's walk-in store room: called a store, stock room or storage, not a cupboard nor a refuse store, 2 m² or more.</summary>
    private static bool WalkIn(BimDocument document, Room room) =>
        Named(room, "store", "stock", "storage") && !Named(room, "cupboard") && !FireSeparation.Refuse(room)
        && room.GetBoundary(document) is { IsEnclosed: true } boundary && Polygon2D.Area(boundary.Polygon) / 1e6 >= WalkInArea - 1e-6;

    /// <summary>Whether a room has fire detection in it: a smoke or heat detector.</summary>
    private static bool Detected(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary && document.All<Component>().Any(piece =>
            document.FindType<ComponentType>(piece.TypeId)?.Form is ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm && RoomsOf.Holds(document, room, boundary.Polygon, piece));

    /// <summary>What an inside wall falls short of round a refuse store (5.43a) or a shop's walk-in store room (5.46).</summary>
    public static (string Said, string Rule)? Problem(BimDocument document, Wall wall, WallType type)
    {
        if (!Applies(document) || type.Function != WallFunction.Interior) return null;
        var (one, other) = WallRules.RoomsBeside(document, wall);
        if (one is null || other is null || one == other) return null;
        var minutes = Doors.FireMinutes(type.FireRating);
        var rated = minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated";

        if (FireSeparation.Refuse(one) != FireSeparation.Refuse(other))
        {
            var store = FireSeparation.Refuse(one) ? one : other;
            var (_, top) = FireDesign.HeightOf(document);
            var asked = top <= 5 + 1e-6 ? 30 : 60;
            return minutes >= asked ? null
                : ($"it encloses the {store.Name}, a refuse store, {rated}.",
                    $"Approved Document B Volume 2 (5.43a, Table B1) asks a room for storing refuse separated from the rest of the building by fire-resisting construction of REI {asked}, the top storey being {(asked == 30 ? "5 m up at most" : "more than 5 m up")}.");
        }

        var group = HorizontalEscape.Purpose(document);
        if (group != PurposeGroup.ShopAndCommercial || document.ProjectInformation.Sprinklered || minutes >= 30) return null;
        var walkIn = WalkIn(document, one) && !WalkIn(document, other) ? one : WalkIn(document, other) && !WalkIn(document, one) ? other : null;
        if (walkIn is null || Detected(document, walkIn)) return null;
        var shop = walkIn == one ? other : one;
        if (!Public(document, shop, group) && !FireSeparation.Circulation(shop) && !Circulation.IsCorridor(shop, shop.GetBoundary(document).Polygon)) return null;
        return ($"it divides the {walkIn.Name}, a walk-in store room, from the {shop.Name}, {rated}.",
            "Approved Document B Volume 2 (5.46) asks a shop's fully enclosed walk-in store room separated from the retail areas by fire-resisting construction, REI 30, where it would affect the means of escape - not where it has automatic fire detection or the shop sprinklers.");
    }

    /// <summary>What a refuse store falls short of in where it is and how it is come to (5.43b, 5.44), said.</summary>
    private static IEnumerable<string> RefuseProblems(BimDocument document, Room room)
    {
        if (!FireSeparation.Refuse(room)) yield break;
        var all = Escape.Links(document, document.All<Room>().Where(each => each.LevelId == room.LevelId).ToHashSet());
        var links = all.Where(link => link.Room == room).ToList();
        if (links.Count == 0) yield break;
        bool StairLobby(Room other) => Circulation.IsLobby(other) && all.Any(link => link.Room == other && link.Other is { } beyond && IsStair(document, beyond));
        if (links.Select(link => link.Other).OfType<Room>().FirstOrDefault(other => IsStair(document, other) || StairLobby(other)) is { } off)
        {
            yield return $"it is a refuse store off the {off.Name}, {(IsStair(document, off) ? "a protected stairway" : "a protected stairway's lobby")} - Approved Document B Volume 2 (5.43b) keeps a refuse store out of a protected stairway or protected lobby";
            yield break;
        }

        if (links.Select(link => link.Other).OfType<Room>().FirstOrDefault(other => !Circulation.IsLobby(other)) is { } from)
            yield return $"it is a refuse store come to from the {from.Name} - Approved Document B Volume 2 (5.44) asks a room for storing refuse come to straight from the open air, or through a protected lobby with 0.2 m² of permanent ventilation";
    }

    /// <summary>Everything a room of a building other than a dwelling falls short of under Section 5: its escape lighting, a refuse store's place and approach.</summary>
    public static IReadOnlyList<string> RoomWanting(BimDocument document, Room room)
    {
        var wanting = new List<string>();
        if (!Applies(document)) return wanting;
        if (LightingWanting(document, room) is { } dark) wanting.Add(dark);
        wanting.AddRange(RefuseProblems(document, room));
        return wanting;
    }
}
