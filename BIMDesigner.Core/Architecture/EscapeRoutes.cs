using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Doors on escape routes, escape lighting and exit signs in a building other than a dwelling (round 113), as Approved Document B Volume 2 sets
/// them out in Section 5.
///
/// Who uses a door in a fire is walked (HorizontalEscape): each point of a storey's rooms to its nearest storey exit, its share of its room's
/// people summed through the doors it passes; a door's escape direction out of the room on the side farther from a storey exit. A door more than
/// 60 people might use - so many walked through it, or out of a room of more than 60 - hung to open in the direction of escape (5.11a); one that
/// slides, folds or lifts for them by hand a swing door of the route's width beside it, unless it is automatic, failing safe open (5.15); a
/// revolving door across an escape route the same (5.15). In a shop or a place of assembly (purpose groups 4 and 5), a door on the way out of a
/// room of more than 60 with no lock, latch or bolt, or with panic fastenings to BS EN 1125 (5.9). A door dividing a corridor on an escape route
/// with a vision panel (5.14a). Every storey exit - a final exit, or a door into an escape stair - but a main entrance, in ordinary use, with an
/// exit sign by it (5.28): the entrance an approach path comes to, else a final exit whose type or mark says entrance, main or front, else the
/// widest final exit on the ground storey.
///
/// Escape lighting where Table 5.1 asks it - an emergency light in the room (5.25): in any building, toilets of 8 m² or more, electricity,
/// generator, switch and battery rooms, emergency control rooms; in a residential building, its common escape routes; in an office, a factory,
/// a warehouse or another non-residential building - and a shop - underground or windowless accommodation, stairs in a central core or serving
/// a storey more than 18 m up, internal corridors more than 30 m long, open-plan areas of more than 60 m²; in a shop besides, the escape routes
/// the public use, but in a shop of three storeys at most with no sales floor over 280 m² that is no restaurant or bar; in a place of assembly
/// every escape route and all its accommodation.
///
/// Not done: 5.7's simple fastenings and 5.8's electric locks; doors swinging both ways (5.14b); a door's swing clear of a step or a landing
/// (5.12); headroom (5.16); final exits (5.20 to 5.24); an escape stair's lighting on its own circuit (5.26); protected power circuits (5.29,
/// 5.30); lifts (5.32 to 5.41); refuse (5.42 to 5.45); shops' store rooms (5.46); escape lighting's spacing and levels by BS 5266-1; signs
/// showing the way to an exit.
/// </summary>
public static partial class EscapeRoutes
{
    /// <summary>The most people a door may serve before it must open in the direction of escape (5.11a) and, in a shop or a place of assembly, have panic fastenings (5.9).</summary>
    public const int Crowd = 60;

    /// <summary>How near an exit sign is to the door it marks, mm; and how near a swing door is beside a revolving or sliding one, past their edges.</summary>
    public const double SignReach = 2000, Beside = 2000;

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether Volume 2's Section 5 reaches the building: one other than a dwelling, under England's rules.</summary>
    private static bool Applies(BimDocument document) => HorizontalEscape.Applies(document) && !document.ProjectInformation.ClearanceStandard.IsUs();

    /// <summary>A door's use in a fire, where the walk finds one.</summary>
    internal static HorizontalEscape.Use? UseOf(BimDocument document, Door door) =>
        Applies(document) && document.Find<Wall>(door.HostWallId) is { } wall && HorizontalEscape.Storeys(document).TryGetValue(wall.LevelId, out var storey)
        && storey.Uses.TryGetValue(door, out var use) ? use : null;

    /// <summary>Which way a door is walked through in a fire: out of the room on its side farther from a storey exit, into the room beyond - none outside; null where the walk does not use it.</summary>
    public static (Room From, Room? Into)? Way(BimDocument document, Door door) => UseOf(document, door) is { } use ? (use.From, use.Into) : null;

    /// <summary>A room's people by Appendix D.</summary>
    private static int People(BimDocument document, Room room, PurposeGroup group) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary && HorizontalEscape.Occupants(document, room, boundary.Polygon, group) is var (people, _) ? people : 0;

    /// <summary>How many people might use a door in a fire: so many walked through it, or the people of the room it leads out of - the more.</summary>
    public static int Users(BimDocument document, Door door) =>
        UseOf(document, door) is { } use ? Math.Max((int)Math.Ceiling(use.People - 1e-6), People(document, use.From, HorizontalEscape.Purpose(document))) : 0;

    private static Point2D? Middle(BimDocument document, Door door) =>
        document.Find<Wall>(door.HostWallId) is { } wall && document.GetWallType(wall) is { } type ? wall.PointAt(type.Structure, door.DistanceAlongWall, 0) : null;

    private static bool Hinged(DoorType type) => type.Operation is DoorOperation.Swing or DoorOperation.DoubleSwing;

    /// <summary>A vision panel: a leaf glazed at eye level - not a top light alone.</summary>
    public static bool VisionPanel(DoorType type) => type.LeafDesign is DoorLeafDesign.Glazed or DoorLeafDesign.HalfGlazed or DoorLeafDesign.FrenchGlazed or DoorLeafDesign.FullGlass;

    /// <summary>Whether a swing door wide enough for so many people by Table 2.3 stands beside a door, joining the same two sides (5.15b).</summary>
    private static bool SwingDoorBeside(BimDocument document, Door door, DoorType type, int people)
    {
        if (Middle(document, door) is not { } here) return false;
        var (a, b) = Doors.RoomsBeside(document, door);
        return document.All<Door>().Any(other => other != door && document.FindType<DoorType>(other.TypeId) is { } otherType && Hinged(otherType)
            && HorizontalEscape.Capacity(Doors.EscapeWidth(otherType)) >= Math.Max(1, people)
            && Middle(document, other) is { } there && here.DistanceTo(there) <= (type.Width + otherType.Width) / 2 + Beside
            && Doors.RoomsBeside(document, other) is var (c, d) && (c == a && d == b || c == b && d == a));
    }

    /// <summary>Whether a door is a storey exit: a final exit, or a door into an escape stair.</summary>
    private static bool StoreyExit(BimDocument document, Door door) =>
        document.Find<Wall>(door.HostWallId) is { } wall && HorizontalEscape.Storeys(document).TryGetValue(wall.LevelId, out var storey) && storey.Exits.Any(exit => exit.Door == door);

    /// <summary>
    /// The main entrances, in ordinary use, that 5.28 asks no exit sign of: the final exits an approach path comes to; else those whose type or
    /// mark says entrance, main or front; else the widest final exit on the ground storey.
    /// </summary>
    internal static IReadOnlySet<Door> MainEntrances(BimDocument document)
    {
        IReadOnlySet<Door> Work()
        {
            var finals = HorizontalEscape.Storeys(document).Values.SelectMany(storey => storey.Exits).Where(exit => exit.What.StartsWith("a final exit", StringComparison.Ordinal))
                .Select(exit => exit.Door).OfType<Door>().Distinct().ToList();
            var approached = finals.Where(door => Approaches.IsApproachedEntrance(document, door)).ToHashSet();
            if (approached.Count > 0) return approached;
            bool Called(Door door) => new[] { "entrance", "main", "front" }.Any(word =>
                (document.FindType<DoorType>(door.TypeId)?.Name ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase) || door.Mark.Contains(word, StringComparison.OrdinalIgnoreCase));
            var called = finals.Where(Called).ToHashSet();
            if (called.Count > 0) return called;
            var ground = Escape.Ground(document);
            return finals.Where(door => document.Find<Wall>(door.HostWallId) is { } wall && Math.Abs((document.FindLevel(wall.LevelId)?.Elevation ?? 0) - ground) <= 100)
                .OrderByDescending(door => document.FindType<DoorType>(door.TypeId) is { } type ? Doors.EscapeWidth(type) : 0).Take(1).ToHashSet();
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("escape routes: main entrances", document, null, null, Work) : Work();
    }

    /// <summary>Whether an exit sign is put up by a door: within 2 m of its middle, on its storey.</summary>
    private static bool SignBy(BimDocument document, Door door) =>
        document.Find<Wall>(door.HostWallId) is { } wall && Middle(document, door) is { } middle
        && document.All<Component>().Any(piece => piece.LevelId == wall.LevelId && piece.Location.DistanceTo(middle) <= SignReach
                                                  && document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.ExitSign);

    private static string Said(DoorFastening fastening) => fastening switch
    {
        DoorFastening.Latch => "a latch",
        DoorFastening.EmergencyLever => "an emergency exit device to BS EN 179",
        DoorFastening.PanicBar => "a panic bar",
        _ => "none"
    };

    private static bool Corridor(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary && boundary.Polygon.Count >= 3 && Circulation.IsCorridor(room, boundary.Polygon);

    /// <summary>What a door on an escape route of a building other than a dwelling falls short of: its direction of opening, its fastening, its vision panel, its exit sign.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Door door, DoorType type)
    {
        // A final exit, sited clear of smoke outlets and hazards' openings (5.24, 5.45; round 114).
        foreach (var problem in FinalExitProblems(document, door, type)) yield return problem;
        if (UseOf(document, door) is not { } use) yield break;
        var group = HorizontalEscape.Purpose(document);
        var room = People(document, use.From, group);
        var users = Math.Max((int)Math.Ceiling(use.People - 1e-6), room);
        var way = use.Outside ? "out of the building" : use.Into is { } beyond ? $"out into the {beyond.Name}" : "out";
        var hinged = Hinged(type);

        // Its direction of opening (5.11a); a revolving door's, a sliding, folding or overhead door's (5.15).
        if (type.Operation == DoorOperation.Revolving && !type.Automatic && !SwingDoorBeside(document, door, type, users))
            yield return ($"it revolves, across the way out of the {use.From.Name}, with no swing door beside it.",
                "Approved Document B Volume 2 (5.15) asks a revolving door across an escape route to be automatic, failing safe open, or to have a swing door of the route's width immediately beside it.");
        else if (users > Crowd && hinged && Doors.RoomsBeside(document, door).Swing == use.From)
            yield return ($"it opens into the {use.From.Name}, against the way {way}, for {users} people.",
                "Approved Document B Volume 2 (5.11a) asks a door more than 60 people might use in a fire hung to open in the direction of escape: flip its facing.");
        else if (users > Crowd && !hinged && type.Operation != DoorOperation.Revolving && !type.Automatic && !SwingDoorBeside(document, door, type, users))
            yield return ($"it {(type.Operation switch { DoorOperation.Sliding => "slides", DoorOperation.Folding => "folds", _ => "lifts" })} by hand, on the way {way} for {users} people.",
                "Approved Document B Volume 2 (5.11a, 5.15) asks a door more than 60 people might use in a fire hung to open in the direction of escape - or automatic, failing safe open, or a swing door of the route's width beside it.");

        // In a shop or a place of assembly, on the way out of a room of more than 60: no lock, latch or bolt, or panic fastenings (5.9).
        var crowd = room > Crowd ? use.From : use.Crowd;
        if (group is PurposeGroup.ShopAndCommercial or PurposeGroup.AssemblyAndRecreation && crowd is not null && !type.Automatic
            && type.Fastening is not (DoorFastening.None or DoorFastening.PanicBar))
            yield return ($"its fastening is {Said(type.Fastening)}, on the way out of the {crowd.Name}, a room of more than 60 people.",
                $"Approved Document B Volume 2 (5.9) asks a door on an escape route from a room of more than 60 people in {(group == PurposeGroup.ShopAndCommercial ? "a shop" : "a place of assembly")} to have no lock, latch or bolt, or panic fastenings to BS EN 1125: set its type's Fastening to Panic Bar.");

        // Dividing a corridor: a vision panel (5.14a).
        if (use.Into is { } next && Corridor(document, use.From) && Corridor(document, next) && !VisionPanel(type))
            yield return ($"it divides the corridor - the {use.From.Name} from the {next.Name} - with no vision panel.",
                "Approved Document B Volume 2 (5.14a) asks a vision panel in a door on an escape route that divides a corridor: a glazed or half-glazed leaf.");

        // A storey exit but a main entrance: its exit sign (5.28).
        if (StoreyExit(document, door) && !MainEntrances(document).Contains(door) && !SignBy(document, door))
            yield return ("no exit sign by it.",
                "Approved Document B Volume 2 (5.28) asks every exit to a means of escape - but one in ordinary use, as a main entrance is - marked by an exit sign to BS ISO 3864-1 and BS 5499-4: put one up over it.");
    }

    // --- Escape lighting (5.25, Table 5.1) ------------------------------------------------

    /// <summary>Whether a room is a stair: one a stair rises through, or called one.</summary>
    private static bool Stair(BimDocument document, Room room) => Named(room, "stair") || Doors.StairRooms(document).Contains(room);

    /// <summary>Whether a room has an outside wall.</summary>
    private static bool Outside(BimDocument document, Room room) =>
        document.Walls.Any(wall => wall.LevelId == room.LevelId && document.GetWallType(wall) is { Function: WallFunction.Exterior }
                                   && WallRules.RoomsBeside(document, wall) is var (one, other) && (one == room || other == room));

    /// <summary>Whether a room has no window: none in its walls, no glazed door out, no roof window over it, no curtain wall to it.</summary>
    private static bool Windowless(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var (windows, doors) = WindowRules.Of(document, room, outline);
        if (windows.Count > 0 || doors.Any(entry => Doors.Glazed(entry.Type)) || WindowRules.RoofWindowsOver(document, room, outline).Count > 0) return false;
        return !document.Walls.Any(wall => wall.LevelId == room.LevelId && CurtainLayout.Of(document, wall) is not null
                                           && WallRules.RoomsBeside(document, wall) is var (one, other) && (one == room || other == room));
    }

    /// <summary>A room of a shop the public come into: its sales floor, a restaurant, café or bar, a showroom, a salon.</summary>
    private static bool Public(BimDocument document, Room room, PurposeGroup group) =>
        Toilets.WordsOf(room.Name).Overlaps(new[] { "restaurant", "cafe", "café", "bar", "dining", "showroom", "customer", "customers", "salon", "banking" })
        || HorizontalEscape.FloorSpace(document, room, group) is var (_, said) && said.StartsWith("a shop sales area", StringComparison.Ordinal);

    /// <summary>Table 5.1's small shop, whose public routes ask no escape lighting: three storeys at most, no sales floor over 280 m², no restaurant or bar.</summary>
    private static bool SmallShop(BimDocument document)
    {
        bool Work()
        {
            var group = HorizontalEscape.Purpose(document);
            if (HorizontalEscape.Storeys(document).Count > 3) return false;
            if (Toilets.WordsOf(document.ProjectInformation.BuildingType).Overlaps(new[] { "restaurant", "bar", "pub", "cafe" })) return false;
            foreach (var room in document.All<Room>())
            {
                if (Toilets.WordsOf(room.Name).Overlaps(new[] { "restaurant", "bar", "pub" })) return false;
                if (room.GetBoundary(document) is { IsEnclosed: true } boundary && Polygon2D.Area(boundary.Polygon) / 1e6 > 280 + 1e-6
                    && HorizontalEscape.FloorSpace(document, room, group) is var (_, said) && said.StartsWith("a shop sales area", StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("escape routes: small shop", document, null, null, Work) : Work();
    }

    /// <summary>
    /// Why Table 5.1 asks escape lighting in a room of a building other than a dwelling, said - "an open-plan area of 82 m², more than 60 m²" -
    /// or null where it does not.
    /// </summary>
    public static string? Lighting(BimDocument document, Room room)
    {
        if (!Applies(document) || MixedUse.Home(document, room) || room.GetBoundary(document) is not { IsEnclosed: true } boundary || boundary.Polygon.Count < 3) return null;
        var outline = boundary.Polygon;
        var group = HorizontalEscape.Purpose(document);
        var area = Polygon2D.Area(outline) / 1e6;

        // In any building.
        var toilet = Named(room, "toilet", "wc", "w.c", "washroom", "lavatory") || Toilets.Plan(document).TryGetValue(room, out var fitted) && fitted.Wcs + fitted.Urinals > 0;
        if (toilet && area >= 8 - 1e-6) return $"toilet accommodation of {area:0.0} m², 8 m² or more";
        if (Named(room, "switch", "electrical", "electricity", "intake", "generator", "battery", "transformer", "substation")) return "an electricity, generator, switch or battery room";
        if (Named(room, "control room", "fire control", "security control", "emergency control")) return "an emergency control room";

        // A room holds people where Appendix D gives it a floor space factor - a sports hall or a main hall as well as an office; circulation where it holds none.
        var stair = Stair(document, room);
        var holds = !stair && HorizontalEscape.FloorSpace(document, room, group) is not null;
        var circulation = stair || !holds && (FireSeparation.Circulation(room) || Circulation.IsCorridor(room, outline));
        var accommodation = holds;
        switch (group)
        {
            case PurposeGroup.ResidentialInstitutional or PurposeGroup.ResidentialOther:
                return circulation && !Lodging.Suites(document).ContainsKey(room) ? "a common escape route of a residential building" : null;
            case PurposeGroup.AssemblyAndRecreation:
                return circulation ? "an escape route of a place of assembly" : accommodation ? "accommodation in a place of assembly" : null;
        }

        // An office, a factory, a warehouse or the like - and a shop.
        var ground = Escape.Ground(document);
        var level = document.FindLevel(room.LevelId)?.Elevation ?? 0;
        if (accommodation && level < ground - Escape.Basement - 0.5) return "underground accommodation";
        if (accommodation && Windowless(document, room, outline)) return "windowless accommodation";
        if (stair)
        {
            var stack = VerticalEscape.Stairs(document).FirstOrDefault(each => each.Rooms.Contains(room));
            var top = (stack?.Rooms ?? new[] { room }).Max(each => document.FindLevel(each.LevelId)?.Elevation ?? 0) - ground;
            if (top > 18000 + 0.5) return $"a stair serving a storey {top / 1000:0.0} m up, more than 18 m";
            if (!Outside(document, room)) return "a stair in a central core, with no outside wall";
        }

        var (_, length) = Toilets.Size(outline);
        if (Circulation.IsCorridor(room, outline) && length > 30000 + 0.5) return $"an internal corridor {length / 1000:0.0} m long, more than 30 m";
        if (accommodation && area > 60 + 1e-6) return $"an open-plan area of {area:0} m², more than 60 m²";
        if (group == PurposeGroup.ShopAndCommercial && !SmallShop(document) && (circulation && !Named(room, "staff") || Public(document, room, group)))
            return "an escape route the public use in a shop - not one of three storeys at most, no sales floor over 280 m², no restaurant or bar";
        return null;
    }

    /// <summary>What a room falls short of in its escape lighting, said; null where it asks none or has it.</summary>
    public static string? LightingWanting(BimDocument document, Room room)
    {
        if (Lighting(document, room) is not { } why || room.GetBoundary(document) is not { IsEnclosed: true } boundary) return null;
        if (document.All<Component>().Any(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.EmergencyLight && RoomsOf.Holds(document, room, boundary.Polygon, piece)))
            return null;
        return $"{why}, with no escape lighting - Approved Document B Volume 2 (5.25, Table 5.1) asks escape lighting there for when the mains fail, to BS 5266-1: put an emergency light up in it";
    }

    /// <summary>The rooms of a building other than a dwelling wanting the escape lighting Table 5.1 asks, and refuse stores out of place (5.43b, 5.44).</summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!Applies(document)) yield break;
        foreach (var room in document.All<Room>())
        foreach (var said in RoomWanting(document, room))
            yield return new EscapeProblem(room, said);
    }
}
