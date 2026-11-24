using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Escape stairs in a building other than a dwelling (round 111), as Approved Document B Volume 2 sets them out in Section 3: a stair is the
/// rooms its flights join, storey above storey.
///
/// One escape stair only where 3.3 allows it - no storey more than 11 m up and each storey allowed a single route (60 people at most, every
/// point within the limit for one direction) (d); an office of five storeys above the ground at most, within the limit for one direction on each
/// storey, none more than 11 m up (e); a factory of normal risk one storey above the ground (f ii). Each stair's width: Table 3.1's least - 1000
/// in a residential (institutional) building, 1100 serving an area for assembly, 800 otherwise; as wide as the exits into it (3.10a); and wide
/// enough for the people of the upper storeys it serves, shared among the stairs with one of them left out where there are two or more (3.14,
/// 3.15b), by Table 3.2 - 150 people on one floor for 1000, 40 more a floor - and 3.18's P = 200W + 50(W - 0.3)(N - 1) from 1100, simultaneous
/// evacuation. Each a protected stairway, its inside walls REI 30, its doors E 30 Sa self-closing (3.23, Tables B1, C1); approached through a
/// protected lobby or corridor at every storey above the ground but the top where it is the only stair and serves more than one storey above the
/// ground, or serves a storey 18 m up or more (3.34a, b); a refuge 900 by 1400 - a wheelchair space - in it or in a lobby or corridor opening
/// into it at every storey but the ground (3.4 to 3.6); leading to a final exit at its foot, straight out or through one room (3.36); and an only
/// stair not carried down to a basement (3.40).
///
/// Round 132, the rest: phased evacuation where the project is set for it and 3.16 allows it - not in an other residential or an assembly
/// building, not with open spatial planning, not for a stair serving a basement - its stairs by Table 3.3, (P x 10) - 100 for the busiest
/// storey's share, 1000 at least, one stair left out where a storey is over 45 m up (3.20, its worked example), lobbies at every storey but the
/// top (3.21a, 3.34c); a stair's width not narrowing on the way to its final exit (3.10c); over 1400 wide rising more than 30 m, or over 2000
/// in a shop or assembly building, a central handrail, each side its own stair for capacity (3.10d, 3.11); a stair serving basements wide
/// enough for their people by Table 3.2, one stair left out where unlobbied (3.14, 3.16a), and lobbied at each basement storey where other
/// stairs serve the storeys above (3.41); its flights and landings A2-s3, d2 where it is the only stair (but an office of two or three
/// storeys), serves a basement or a storey over 18 m up, or is a firefighting stair (3.24); a protected lobby with 0.4 m² of permanent
/// ventilation between it and a place of special fire hazard (3.35); no door between two protected stairways leading to different final exits
/// (3.37); only sanitary accommodation, a lift well, a small reception desk and fire-resisting cupboards within it, and no gas appliance but a
/// water heater in its toilets (3.38); and a final exit shared with a ground storey exit wide enough for both flows, W = (N/2.5 + 60S)/80
/// (2.23, Diagram 2.6).
///
/// Not done: external escape stairs (3.29 to 3.33); the internal speech communication of phased evacuation (3.21f); gas pipes in a stairway
/// (3.39); mechanical smoke control in place of a lobby's vents (3.15a, 3.35); evacuation lifts and EVC (3.7, 5.32).
/// </summary>
public static class VerticalEscape
{
    /// <summary>The highest a storey may be for a single stair, and from which lobbies are asked, mm.</summary>
    public const double SingleStair = 11000, LobbyHeight = 18000;

    /// <summary>A stair's rise past which it is no wider than 1400 without a central handrail (3.10d); a storey's height past which a stair is left out in phased evacuation (3.20), mm.</summary>
    public const double TallStair = 30000, Widest = 1400, PhasedDiscount = 45000;

    /// <summary>A lobby's permanent ventilation between a stair and a place of special fire hazard (3.35), m²; a reception desk's area within a stairway (3.38c), m².</summary>
    public const double HazardLobbyVent = 0.4, Reception = 10;

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>How many people a stair of a width takes serving so many floors, evacuating at once (Table 3.2, 3.18); 50 under 1000, from 800 (Table 3.1 item 2).</summary>
    public static int Capacity(double width, int floors)
    {
        var n = Math.Max(1, floors);
        if (width >= 1100 - 0.5) return (int)Math.Floor(200 * width / 1000 + 50 * (width / 1000 - 0.3) * (n - 1) + 1e-6);
        if (width >= 1000 - 0.5) return 150 + 40 * (n - 1);
        return width >= 800 - 0.5 ? 50 : 0;
    }

    /// <summary>A stair's capacity evacuating at once, a central handrail making each side a stair of its own (3.10d).</summary>
    public static int Capacity(double width, int floors, bool handrail) => handrail ? 2 * Capacity(width / 2, floors) : Capacity(width, floors);

    /// <summary>How many people of the busiest storey a stair takes in phased evacuation (Table 3.3 and its note 3): (P x 10) - 100 = W, 1000 at least.</summary>
    public static int PhasedCapacity(double width, bool handrail = false)
    {
        if (handrail) return 2 * PhasedCapacity(width / 2);
        return width >= 1000 - 0.5 ? (int)Math.Floor((width + 100) / 10 + 1e-6) : 0;
    }

    /// <summary>The width phased evacuation asks for so many of a storey's people (Table 3.3 note 3), mm.</summary>
    public static double PhasedWidth(int people) => Math.Max(1000, people * 10.0 - 100);

    /// <summary>Why the building cannot be evacuated in phases (3.16), said with its paragraph; null where it can.</summary>
    public static string? PhasedRefused(BimDocument document)
    {
        var group = HorizontalEscape.Purpose(document);
        if (group == PurposeGroup.ResidentialOther) return "an other residential building - a hotel, hostel or hall (3.16c)";
        if (group == PurposeGroup.AssemblyAndRecreation) return "an assembly and recreation building (3.16c)";
        return document.Levels.FirstOrDefault(level => HorizontalEscape.Openings(document, level.Id).Count > 0) is { } open
            ? $"open spatial planning - an opening between floors on the {open.Name} (3.16b)"
            : null;
    }

    /// <summary>Whether a building other than a dwelling is designed for phased evacuation, and may be (3.16, 3.19).</summary>
    public static bool Phased(BimDocument document) =>
        document.ProjectInformation.PhasedEvacuation && HorizontalEscape.Applies(document) && PhasedRefused(document) is null;

    /// <summary>A stair's flights and landings' reaction to fire, and what they are taken to be of: the material of its type, else its construction's.</summary>
    public static (Euroclass? Class, string Of) ClassOf(BimDocument document, StairType type)
    {
        if (type.MaterialId != Guid.Empty && document.FindMaterial(type.MaterialId) is { } material)
            return (Euroclass.Of(material)?.Class, material.Name);
        return type.Construction == StairConstruction.Concrete ? (Euroclass.A1, "concrete") : (Euroclass.Typical("timber")?.Class, "timber");
    }

    /// <summary>A room's permanent ventilation: the free area of the vents in its walls to the open air, and roof windows over it, that are never shut, m².</summary>
    public static double PermanentVentilation(BimDocument document, Room room)
    {
        if (room.GetBoundary(document) is not { IsEnclosed: true } boundary) return 0;
        var outline = boundary.Polygon;
        var area = 0.0;
        foreach (var window in document.All<Window>())
        {
            if (document.Find<Wall>(window.HostWallId) is not { } wall || wall.LevelId != room.LevelId || document.GetWallType(wall) is not { } type) continue;
            if (SmokeVents.Of(document, window) is not { Kind: SmokeVentKind.Permanent } vent) continue;
            var reach = type.Structure.TotalWidth / 2 + 150;
            var (a, b) = (wall.PointAt(type.Structure, window.DistanceAlongWall, reach), wall.PointAt(type.Structure, window.DistanceAlongWall, -reach));
            var beyond = Polygon2D.Contains(outline, a) ? b : Polygon2D.Contains(outline, b) ? a : (Point2D?)null;
            if (beyond is { } there && RoomsOf.Containing(document, room.LevelId, there) is null) area += vent.FreeArea;
        }

        var floor = document.FindLevel(room.LevelId)?.Elevation ?? 0;
        var next = document.Levels.Where(level => level.Elevation > floor + 1).Select(level => level.Elevation).DefaultIfEmpty(double.MaxValue).Min();
        foreach (var window in document.All<RoofWindow>())
            if (RoofWindows.Frame(document, window) is { } frame && Polygon2D.Contains(outline, frame.Centre) && frame.Corners.Min(frame.Top) is var low && low > floor + 1 && low < next
                && SmokeVents.Of(document, window) is { Kind: SmokeVentKind.Permanent } vent)
                area += vent.FreeArea;
        return area;
    }

    /// <summary>A stair as found: its rooms storey by storey, its flights, its narrowest width, the levels above the ground it serves, and its foot.</summary>
    public sealed record StairStack(IReadOnlyList<Room> Rooms, double Width, IReadOnlyList<Guid> Upper, Room Foot, bool Basement, Point2D FootAt = default)
    {
        /// <summary>Its flights, each a stair element from one storey to the next.</summary>
        public IReadOnlyList<Stair> Flights { get; init; } = Array.Empty<Stair>();

        /// <summary>The basement storeys it serves, below the ground by more than a basement's depth.</summary>
        public IReadOnlyList<Guid> Lower { get; init; } = Array.Empty<Guid>();
    }

    /// <summary>The building's stairs, each its rooms joined by flights, kept while the model does not change.</summary>
    public static IReadOnlyList<StairStack> Stairs(BimDocument document)
    {
        IReadOnlyList<StairStack> Work()
        {
            var outlines = document.All<Room>().Where(room => !MixedUse.Home(document, room)).Select(room => (Room: room, Boundary: room.GetBoundary(document)))
                .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
            var flights = Escape.Flights(document, outlines).Where(flight => flight.Foot is not null && flight.Head is not null).ToList();
            if (flights.Count == 0) return Array.Empty<StairStack>();
            var ground = Escape.Ground(document);
            double Elevation(Room room) => document.FindLevel(room.LevelId)?.Elevation ?? 0;
            return Escape.Stacks(flights).Select(rooms =>
            {
                var ordered = rooms.OrderBy(Elevation).ToList();
                var width = flights.Where(flight => rooms.Contains(flight.Foot!)).Select(flight => flight.Stair.Width).DefaultIfEmpty(0).Min();
                var upper = ordered.Where(room => Elevation(room) > ground + 100).Select(room => room.LevelId).Distinct().ToList();
                var foot = ordered.FirstOrDefault(room => Math.Abs(Elevation(room) - ground) <= 100) ?? ordered[0];
                var at = flights.Where(flight => flight.Foot == foot).Select(flight => flight.FootAt)
                    .Concat(flights.Where(flight => flight.Head == foot).Select(flight => flight.HeadAt)).FirstOrDefault();
                var lower = ordered.Where(room => Elevation(room) < ground - Escape.Basement - 0.5).Select(room => room.LevelId).Distinct().ToList();
                return new StairStack(ordered, width, upper, foot, lower.Count > 0, at)
                {
                    Flights = flights.Where(flight => rooms.Contains(flight.Foot!)).Select(flight => flight.Stair).Distinct().ToList(),
                    Lower = lower
                };
            }).ToList();
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("vertical escape", document, null, null, Work) : Work();
    }

    /// <summary>How many people are on a storey, by Appendix D.</summary>
    private static int People(BimDocument document, Guid level, PurposeGroup group) =>
        HorizontalEscape.Storeys(document).TryGetValue(level, out var storey)
            ? storey.Rooms.Keys.Sum(room => room.GetBoundary(document) is { IsEnclosed: true } boundary && HorizontalEscape.Occupants(document, room, boundary.Polygon, group) is var (count, _) ? count : 0)
            : 0;

    /// <summary>What a stair falls short of, each said.</summary>
    public static IReadOnlyList<string> Problems(BimDocument document, StairStack stair)
    {
        var wanting = new List<string>();
        if (!HorizontalEscape.Applies(document)) return wanting;
        var group = HorizontalEscape.Purpose(document);
        var ground = Escape.Ground(document);
        var stairs = Stairs(document);
        var links = Escape.Links(document, document.All<Room>().ToHashSet());
        double Above(Guid level) => (document.FindLevel(level)?.Elevation ?? 0) - ground;
        string Called(Guid level) => document.FindLevel(level)?.Name ?? "storey";
        string Storeys(int count) => $"{count} storey{(count == 1 ? string.Empty : "s")}";
        double Width(Door door) => document.FindType<DoorType>(door.TypeId) is { } type ? Doors.EscapeWidth(type) : 0;
        var stairRooms = stair.Rooms.ToHashSet();

        // The upper storeys it serves with people on them, and the stairs that serve them.
        var upper = stair.Upper.Where(level => People(document, level, group) > 0).ToList();
        var serving = stairs.Where(other => other.Upper.Intersect(upper).Any()).ToList();

        // An external escape stair from those storeys too: then not the only stair (round 133) - its width judged on its own.
        var external = ExternalStairs.Of(document).Any(each => each.Doors.Any(onto => upper.Contains(onto.Level.Id)));
        var only = serving.Count == 1 && !external;
        var top = upper.Count > 0 ? upper.Max(Above) : 0;

        // Small premises: a single stair allowed (4.7), an open one by 4.8 (round 124).
        var premises = HorizontalEscape.SmallPremises(document);
        var openStair = stair.Rooms.Any(room => HorizontalEscape.OpenStairRoom(document, room));

        // Phased evacuation where the project is set for it and 3.16 allows it - never for a stair serving basements (3.16a).
        var refused = document.ProjectInformation.PhasedEvacuation ? PhasedRefused(document) : null;
        var phased = document.ProjectInformation.PhasedEvacuation && refused is null && !stair.Basement;
        if (refused is not null)
            wanting.Add($"the building is set for phased evacuation, which Approved Document B Volume 2 (3.16) does not allow in {refused}: its stairs are sized for simultaneous evacuation, every storey at once");

        // A central handrail down every flight: each side a stair of its own (3.10d).
        var handrail = stair.Flights.Count > 0 && stair.Flights.All(flight => document.FindType<StairType>(flight.TypeId)?.CentralHandrail == true);

        // One stair only where 3.3 allows it.
        if (only && upper.Count > 0 && !premises.Small)
        {
            var storeys = HorizontalEscape.Storeys(document);
            bool OneWay(Guid level) => storeys.TryGetValue(level, out var storey) && storey.Rooms.Values.All(travel => travel.Reached && travel.Nearest <= travel.One + 0.5);
            var single = upper.All(level => People(document, level, group) <= 60 && OneWay(level)) && top <= SingleStair + 0.5;
            var office = group == PurposeGroup.Office && upper.Count <= 5 && upper.All(OneWay) && top <= SingleStair + 0.5;
            var factory = group == PurposeGroup.Industrial && upper.Count <= 1 && upper.All(OneWay);
            if (!single && !office && !factory)
                wanting.Add($"it is the only escape stair from {upper.Count} upper storey{(upper.Count == 1 ? string.Empty : "s")}, the top {top / 1000:0.0} m up - Approved Document B Volume 2 (3.3) allows one only where no storey is more than 11 m up and each has 60 people at most within the travel distance for one direction (d), or in an office of five storeys above the ground at most within it (e), or a factory of one (f): give it a second stair");
            if (stair.Basement)
                wanting.Add("it is the only escape stair from the upper storeys and goes on down to the basement - Approved Document B Volume 2 (3.40) asks the basement its own stair");
        }

        // Its width: Table 3.1's least, as wide as the exits into it, and enough for the upper storeys' people, one stair left out (3.14).
        var least = group == PurposeGroup.ResidentialInstitutional ? 1000.0 : group == PurposeGroup.AssemblyAndRecreation ? 1100 : 800;
        if (stair.Width < least - 0.5)
            wanting.Add($"it is {stair.Width:0} wide - Approved Document B Volume 2 (3.10b, Table 3.1) asks {least:0} at least {(least >= 1100 ? "serving an area for assembly" : least >= 1000 ? "in a residential (institutional) building" : "of any escape stair")}");
        var into = links.Where(link => link.Other is { } other && stairRooms.Contains(other) && !stairRooms.Contains(link.Room)).Select(link => Width(link.Door)).DefaultIfEmpty(0).Max();
        if (into > stair.Width + 0.5)
            wanting.Add($"it is {stair.Width:0} wide where a door into it gives {into:0} - Approved Document B Volume 2 (3.10a) asks a stair as wide as any exit into it");

        // Not narrowing on the way to its final exit (3.10c): down from the storeys above the ground, up from the basements.
        double FootOf(Stair flight) => (document.FindLevel(flight.LevelId)?.Elevation ?? 0) - ground;
        foreach (var run in new[] { stair.Flights.Where(flight => FootOf(flight) >= -100).OrderByDescending(FootOf).ToList(), stair.Flights.Where(flight => FootOf(flight) < -100).OrderBy(FootOf).ToList() })
        {
            Stair? wider = null;
            foreach (var flight in run)
            {
                if (wider is not null && flight.Width < wider.Width - 0.5)
                {
                    wanting.Add($"its flight from the {Called(flight.LevelId)} is {flight.Width:0} wide, narrower than the {wider.Width:0} of the one from the {Called(wider.LevelId)} before it on the way out - Approved Document B Volume 2 (3.10c) asks an escape stair not to narrow on the way to a final exit");
                    break;
                }

                if (wider is null || flight.Width > wider.Width) wider = flight;
            }
        }

        // Over 1400 wide rising more than 30 m, or over 2000 in a public building, a central handrail (3.10d, 3.11).
        var elevations = stair.Rooms.Select(room => document.FindLevel(room.LevelId)?.Elevation ?? 0).ToList();
        var rise = elevations.Max() - elevations.Min();
        var widestFlight = stair.Flights.Select(flight => flight.Width).DefaultIfEmpty(stair.Width).Max();
        if (!handrail && rise > TallStair + 0.5 && widestFlight > Widest + 0.5)
            wanting.Add($"it rises {rise / 1000:0.0} m and is {widestFlight:0} wide with no central handrail - Approved Document B Volume 2 (3.10d, Table 3.3 note 2) asks a stair taller than 30 m no wider than 1400 unless it has one, each side then taken as a stair of its own: tick Central Handrail in its type");
        else if (!handrail && widestFlight > 2000 + 0.5 && group is PurposeGroup.ShopAndCommercial or PurposeGroup.AssemblyAndRecreation)
            wanting.Add($"it is {widestFlight:0} wide with no central handrail - Approved Document B Volume 2 (3.11) notes that Approved Document K asks a stair more than 2000 wide in a public building for one: tick Central Handrail in its type");

        // Its share of the upper storeys' people: all of them at once by Table 3.2, or the busiest storey's in phases by Table 3.3.
        var share = 0;
        if (upper.Count > 0)
        {
            var counts = upper.Select(level => People(document, level, group)).ToList();
            var people = counts.Sum();
            var lobbied = upper.Where(level => Above(level) < top - 1).All(level => Lobbied(document, stair, level, links));
            var discount = serving.Count >= 2 && (!lobbied || phased && top > PhasedDiscount + 0.5);
            var available = discount ? serving.Count - 1 : serving.Count;
            var leftOut = !discount ? string.Empty : lobbied ? ", one stair left out, a storey being more than 45 m up (3.20)" : ", one stair left out";
            if (phased)
            {
                var busiest = counts.Max();
                share = (int)Math.Ceiling(busiest / (double)Math.Max(1, available));
                var takes = PhasedCapacity(stair.Width, handrail);
                if (takes < share)
                    wanting.Add($"it is {stair.Width:0} wide{(handrail ? " with a central handrail" : string.Empty)}, taking {takes} people of a storey evacuated in phases where {share} of the {busiest} on the busiest storey may use it{leftOut} - Approved Document B Volume 2 (3.22, Table 3.3) asks W = (P x 10) - 100, 1000 at least, P the people of the most heavily occupied storey: {PhasedWidth(share):0}");
            }
            else
            {
                share = (int)Math.Ceiling(people / (double)Math.Max(1, available));
                var takes = Capacity(stair.Width, upper.Count, handrail);
                var basements = document.ProjectInformation.PhasedEvacuation && refused is null ? ", a stair serving basements sized for simultaneous evacuation (3.16a)" : string.Empty;
                if (takes < share)
                    wanting.Add($"it is {stair.Width:0} wide{(handrail ? " with a central handrail" : string.Empty)}, taking {takes} people from {Storeys(upper.Count)} where {share} of the {people} on them may use it{leftOut}{basements} - Approved Document B Volume 2 (3.13, 3.14, 3.17, 3.18, Table 3.2) asks it wide enough for them evacuating at once: 150 people a floor for 1000, from 1100 P = 200W + 50(W - 0.3)(N - 1)");
            }
        }

        // The basements' people, at once by Table 3.2 (3.16a), one stair left out where unlobbied; lobbied where other stairs serve the storeys above (3.41).
        var lower = stair.Lower.Where(level => People(document, level, group) > 0).ToList();
        var shareBelow = 0;
        if (lower.Count > 0)
        {
            var below = stairs.Where(other => other.Lower.Intersect(lower).Any()).ToList();
            var people = lower.Sum(level => People(document, level, group));
            var discount = below.Count >= 2 && !lower.All(level => Lobbied(document, stair, level, links));
            shareBelow = (int)Math.Ceiling(people / (double)Math.Max(1, discount ? below.Count - 1 : below.Count));
            var takes = Capacity(stair.Width, lower.Count, handrail);
            if (takes < shareBelow)
                wanting.Add($"it is {stair.Width:0} wide, taking {takes} people from {lower.Count} basement storey{(lower.Count == 1 ? string.Empty : "s")} where {shareBelow} of the {people} on them may use it{(discount ? ", one stair left out" : string.Empty)} - Approved Document B Volume 2 (3.14, 3.16a, 3.17, Table 3.2) asks a stair serving basements wide enough for their people evacuating at once");
            var open = lower.Where(level => !Lobbied(document, stair, level, links)).Select(Called).ToList();
            if (!only && upper.Count > 0 && open.Count > 0)
                wanting.Add($"it is one of {serving.Count} stairs from the upper storeys and goes on down to the basement, opening straight into the accommodation on the {string.Join(", the ", open)} - Approved Document B Volume 2 (3.41) asks a protected lobby or protected corridor between it and the accommodation at each basement storey, only one stair needing to end at the ground");
        }

        // The rooms within its enclosure: opening only into it, a wall between them and it of less than 30 minutes (3.38).
        var beside = document.Walls.Where(wall => document.GetWallType(wall) is { Function: WallFunction.Interior })
            .Select(wall => (Type: document.GetWallType(wall)!, Rooms: WallRules.RoomsBeside(document, wall)))
            .Where(entry => entry.Rooms.One is not null && entry.Rooms.Other is not null && entry.Rooms.One != entry.Rooms.Other)
            .Select(entry => (entry.Type, One: entry.Rooms.One!, Other: entry.Rooms.Other!)).ToList();
        bool OnlyInto(Room room) => links.Where(link => link.Room == room).ToList() is { Count: > 0 } own && own.All(link => link.Other is { } other && stairRooms.Contains(other));
        var within = beside.Where(entry => stairRooms.Contains(entry.One) != stairRooms.Contains(entry.Other) && Doors.FireMinutes(entry.Type.FireRating) < 30)
            .Select(entry => stairRooms.Contains(entry.One) ? entry.Other : entry.One).Where(OnlyInto).ToHashSet();
        var enclosure = stairRooms.Concat(within).ToHashSet();

        // A protected stairway: its walls REI 30, its doors E 30 Sa with closers (3.23).
        var walls = beside.Where(entry => enclosure.Contains(entry.One) != enclosure.Contains(entry.Other) && Doors.FireMinutes(entry.Type.FireRating) < 30)
            .Select(entry => entry.Type.Name).Distinct().ToList();
        var doors = links.Where(link => stairRooms.Contains(link.Room) && link.Other is { } beyond && !enclosure.Contains(beyond))
            .Where(link => document.FindType<DoorType>(link.Door.TypeId) is not { } type || Doors.FireMinutes(type.FireRating) < 30 || !type.SelfClosing)
            .Select(link => document.FindType<DoorType>(link.Door.TypeId)?.Name ?? "a door").Distinct().ToList();
        if (openStair && !premises.Small)
            wanting.Add($"it stands open in the {string.Join(", the ", stair.Rooms.Where(room => HorizontalEscape.OpenStairRoom(document, room)).Select(room => $"{room.Name} on the {Called(room.LevelId)}").Distinct())}, not in an enclosure of its own - Approved Document B Volume 2 (3.23) asks every internal escape stair to be a protected stairway, REI 30, its doors E 30 Sa; an open stair only where travel and the people are very limited, as in small premises (Section 4, 4.8)");
        else if (openStair)
        {
            // An open stair in small premises (4.8): two storeys at most, entering the ground storey 3 m from the final exit, not a bar or
            // restaurant, and another protected stairway serving the storey, or the only stair with no storey over 90 m².
            var levels = stair.Rooms.Select(room => room.LevelId).Distinct().ToList();
            var outDoors = links.Where(link => link.Room.LevelId == stair.Foot.LevelId && link.Other is null && link.Outside).Select(link => link.Inside).ToList();
            var toExit = outDoors.Count == 0 ? double.PositiveInfinity : outDoors.Min(point => point.DistanceTo(stair.FootAt));
            var protectedToo = stairs.Any(other => !ReferenceEquals(other, stair) && !other.Rooms.Any(room => HorizontalEscape.OpenStairRoom(document, room))
                                                   && other.Rooms.Select(room => room.LevelId).Intersect(levels).Any());
            var largest = levels.Max(level => document.All<Room>().Where(room => room.LevelId == level && room.GetBoundary(document) is { IsEnclosed: true }).Sum(room => room.GetBoundary(document).Area) / 1e6);
            var short_ = new List<string>();
            if (levels.Count > 2) short_.Add($"it connects {levels.Count} storeys, where 4.8a allows two");
            if (toExit > 3000 + 0.5) short_.Add(double.IsPositiveInfinity(toExit) ? "no final exit on the ground storey it enters (4.8b)" : $"it enters the ground storey {toExit / 1000:0.0} m from the final exit, where 4.8b allows 3 m");
            if (premises.BarOrRestaurant) short_.Add("the premises are a bar or restaurant (4.8c)");
            if (!protectedToo && !(only && largest <= HorizontalEscape.OpenStairStorey + 1e-6))
                short_.Add($"no protected stairway serves the storey too, and {(only ? $"a storey is {largest:0} m², over the 90 m² a single open stair allows" : "it is not the only stair")} (4.8d)");
            if (short_.Count > 0)
                wanting.Add($"it is an open stair in small premises: {string.Join("; ", short_)} - Approved Document B Volume 2 (4.8) allows one only where all four hold, else a protected stairway (3.23)");
        }
        else if (walls.Count > 0 || doors.Count > 0)
            wanting.Add($"it is not a protected stairway: {string.Join(" and ", new[] { walls.Count > 0 ? $"its walls {string.Join(", ", walls)} less than 30 minutes" : null, doors.Count > 0 ? $"its doors {string.Join(", ", doors)} not E 30 Sa with closers" : null }.OfType<string>())} - Approved Document B Volume 2 (3.23, Tables B1, C1) asks every internal escape stair enclosed in fire-resisting construction, REI 30, its doors E 30 Sa fire doorsets, self-closing");

        // What may be within it (3.38): sanitary accommodation but a cloakroom, with no gas appliance but a water heater; a lift well, but in a
        // firefighting stair; where it is not the only stair, a reception desk or enquiry office of 10 m² at most at the ground, and cupboards
        // enclosed in fire-resisting construction.
        var firefighting = FirefightingShafts.Of(document).Any(shaft => shaft.Stair.Foot == stair.Foot);
        var lifts = Lifts.Of(document);
        bool LiftIn(Room room) => room.GetBoundary(document) is { IsEnclosed: true } outline
            && lifts.Any(lift => lift.Landings.Any(landing => landing.Level.Id == room.LevelId) && Polygon2D.Contains(outline.Polygon, new Point2D(lift.Car.Average(point => point.X), lift.Car.Average(point => point.Y))));
        bool Sanitary(Room room) => Named(room, "wc", "toilet", "washroom", "lavatory", "shower") && !Named(room, "cloak");
        bool Cupboard(Room room) => Named(room, "cupboard", "store", "cleaner");
        var inside = new List<string>();
        if (!openStair)
        {
            foreach (var room in within.OrderBy(room => Above(room.LevelId)).ThenBy(room => room.Name, StringComparer.Ordinal))
            {
                var at = $"the {room.Name} on the {Called(room.LevelId)}";
                var area = room.GetBoundary(document) is { IsEnclosed: true } outline ? outline.Area / 1e6 : 0;
                if (Named(room, "cloak")) inside.Add($"{at}, a cloakroom (3.38a)");
                else if (Sanitary(room)) { }
                else if (LiftIn(room) || Named(room, "lift"))
                {
                    if (firefighting) inside.Add($"{at}, a lift well in a firefighting stair (3.38b)");
                }
                else if (Named(room, "reception", "enquir"))
                {
                    var why = new List<string>();
                    if (only) why.Add("in the only stair");
                    if (Math.Abs(Above(room.LevelId)) > 100) why.Add("not at the ground");
                    if (area > Reception + 1e-6) why.Add($"{area:0.0} m², over 10");
                    if (why.Count > 0) inside.Add($"{at}, a reception or enquiry office {string.Join(", ", why)} (3.38c)");
                }
                else if (Cupboard(room)) inside.Add($"{at}, a cupboard not enclosed in fire-resisting construction{(only ? ", in the only stair" : string.Empty)} (3.38d)");
                else inside.Add($"{at}, opening only into it, its wall to it less than 30 minutes");
            }

            if (only)
                foreach (var room in links.Where(link => stairRooms.Contains(link.Room) && link.Other is { } other && !enclosure.Contains(other) && Named(other, "cupboard") && OnlyInto(other))
                             .Select(link => link.Other!).Distinct())
                    inside.Add($"the {room.Name} on the {Called(room.LevelId)}, a cupboard off the only stair (3.38d)");

            var pieces = document.All<Component>().Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
                .Where(entry => entry.Type is { } type && (type.BurnsGas || type.Form == ComponentForm.ServiceCounter)).ToList();
            foreach (var room in enclosure.OrderBy(room => Above(room.LevelId)))
            {
                if (room.GetBoundary(document) is not { IsEnclosed: true } outline) continue;
                foreach (var (_, type) in pieces.Where(entry => entry.Piece.LevelId == room.LevelId && Polygon2D.Contains(outline.Polygon, entry.Piece.Location)))
                {
                    if (type!.BurnsGas && !(within.Contains(room) && Sanitary(room) && type.Form == ComponentForm.HotWaterCylinder))
                        inside.Add($"the {type.Name}, a gas appliance, in the {room.Name} on the {Called(room.LevelId)} (3.38a)");
                    else if (type.Form == ComponentForm.ServiceCounter && !type.Name.Contains("reception", StringComparison.OrdinalIgnoreCase))
                        inside.Add($"the {type.Name} in the {room.Name} on the {Called(room.LevelId)}, not a reception desk (3.38c)");
                    else if (type.Form == ComponentForm.ServiceCounter && (only || Math.Abs(Above(room.LevelId)) > 100))
                        inside.Add($"the {type.Name} in the {room.Name} on the {Called(room.LevelId)}, {(only ? "in the only stair" : "not at the ground")} (3.38c)");
                }

                if (firefighting && stairRooms.Contains(room) && LiftIn(room))
                    inside.Add($"a lift in the {room.Name} on the {Called(room.LevelId)}, a firefighting stair (3.38b)");
            }
        }

        if (inside.Count > 0)
            wanting.Add($"within it: {string.Join("; ", inside.Distinct())} - Approved Document B Volume 2 (3.38) allows a protected stairway to include only sanitary accommodation or washrooms - not a cloakroom - with no gas appliance but a water heater or sanitary towel incinerator; a lift well, but in a firefighting stair; and, where it is not the only stair, a reception desk or enquiry office of 10 m² at most at the ground or access level, and cupboards enclosed in fire-resisting construction");

        // Its flights and landings A2-s3, d2 where it is the only stair but in an office of two or three storeys, serves a basement or a storey
        // over 18 m up, or is a firefighting stair (3.24).
        var reasons = new List<string>();
        var storeyCount = document.All<Room>().Where(room => room.GetBoundary(document) is { IsEnclosed: true }).Select(room => room.LevelId).Distinct().Count();
        if (only && !(group == PurposeGroup.Office && storeyCount is 2 or 3)) reasons.Add("that is the only stair (3.24a)");
        if (stair.Basement) reasons.Add("serving a basement (3.24b)");
        if (stair.Upper.Any(level => Above(level) > LobbyHeight + 0.5)) reasons.Add("serving a storey more than 18 m up (3.24c)");
        if (firefighting) reasons.Add("that is a firefighting stair (3.24e)");
        if (reasons.Count > 0)
        {
            var poor = stair.Flights.Select(flight => document.FindType<StairType>(flight.TypeId)).OfType<StairType>().Distinct()
                .Select(type => (Type: type, Class: ClassOf(document, type)))
                .Where(entry => entry.Class.Class is not { } known || !known.AtLeast(Euroclass.A2s3d2))
                .Select(entry => $"{entry.Type.Name}, {entry.Class.Of}{(entry.Class.Class is { } known ? $" {known}" : " of no class given")}").ToList();
            if (poor.Count > 0)
                wanting.Add($"its flights are {string.Join("; ", poor)} - Approved Document B Volume 2 (3.24) asks the flights and landings of an escape stair {string.Join(", ", reasons)} of class A2-s3, d2 or better{(firefighting ? string.Empty : ", B-s3, d2 or worse only on their top surface")}");
        }

        // Lobbies at every storey above the ground but the top, where it is the only stair over more than one storey, serves one 18 m up or more,
        // or the building is evacuated in phases (3.34, 3.21a).
        if (upper.Count > 0 && (only && upper.Count > 1 || top >= LobbyHeight - 0.5 || phased && upper.Count > 1))
        {
            var open = upper.Where(level => Above(level) < top - 1 && !Lobbied(document, stair, level, links)).Select(Called).ToList();
            var (cited, why) = only && upper.Count > 1 ? ("3.34a", "it being the only stair over more than one storey")
                : top >= LobbyHeight - 0.5 ? ("3.34b", "it serving a storey 18 m up or more") : ("3.21a, 3.34c", "the building being designed for phased evacuation");
            if (open.Count > 0)
                wanting.Add($"it opens straight into the accommodation on the {string.Join(", the ", open)} - Approved Document B Volume 2 ({cited}) asks a protected lobby or protected corridor between it and each storey above the ground but the top, {why}");
        }

        // A refuge at every storey it serves but the ground: a wheelchair space 900 by 1400 in it, or in a lobby or corridor opening into it (3.4 to 3.6).
        var without = stair.Rooms.Where(room => !openStair && !ReferenceEquals(room, stair.Foot) && Math.Abs(Above(room.LevelId)) > 100 && People(document, room.LevelId, group) > 0 && !Refuge(document, stair, room, links))
            .Select(room => Called(room.LevelId)).Distinct().ToList();
        if (without.Count > 0)
            wanting.Add($"no refuge by it on the {string.Join(", the ", without)} - Approved Document B Volume 2 (3.4 to 3.6) asks one at every storey of each protected stairway that is an exit from it, 900 by 1400, reachable in a wheelchair, in it or in a protected lobby or corridor opening into it, with its emergency voice communication (3.7): place a wheelchair space there");

        // A protected lobby between it and a place of special fire hazard, with 0.4 m² of permanent ventilation (3.35).
        var hazards = new List<string>();
        foreach (var link in links.Where(link => stairRooms.Contains(link.Room) && link.Other is { } beyond && !stairRooms.Contains(beyond)))
        {
            var beyond = link.Other!;
            if (FireSeparation.Special(beyond))
                hazards.Add($"it opens straight into the {beyond.Name}, a place of special fire hazard, on the {Called(beyond.LevelId)}");
            else if (Circulation.IsLobby(beyond) && links.FirstOrDefault(next => next.Room == beyond && next.Other is { } hazard && FireSeparation.Special(hazard)) is { } through
                     && PermanentVentilation(document, beyond) is var vent && vent < HazardLobbyVent - 0.005)
                hazards.Add($"its lobby to the {through.Other!.Name}, the {beyond.Name} on the {Called(beyond.LevelId)}, has {(vent > 0.005 ? $"{vent:0.00} m² of" : "no")} permanent ventilation");
        }

        if (hazards.Count > 0)
            wanting.Add($"{string.Join("; ", hazards.Distinct())} - Approved Document B Volume 2 (3.35) asks a protected lobby between an escape stair and a place of special fire hazard, with 0.4 m² of permanent ventilation at least - a vent set to Permanent ventilation in its Edit Type - or a mechanical smoke control system");

        // To a final exit at its foot (3.36).
        IEnumerable<Door> Out(Room room) => links.Where(link => link.Room == room && link.Other is null && link.Outside).Select(link => link.Door);
        List<Door> FinalsOf(StairStack each) => Out(each.Foot).Concat(links.Where(link => link.Room == each.Foot && link.Other is { } next && !each.Rooms.Contains(next)).SelectMany(link => Out(link.Other!))).Distinct().ToList();
        var finals = FinalsOf(stair);

        // No door between it and another protected stairway leading to another final exit (3.37).
        var adjoining = links.Where(link => stairRooms.Contains(link.Room) && link.Other is { } beyond && !stairRooms.Contains(beyond))
            .Select(link => (Link: link, Next: stairs.FirstOrDefault(other => other.Rooms.Contains(link.Other!))))
            .Where(entry => entry.Next is { } next && !FinalsOf(next).ToHashSet().SetEquals(finals))
            .Select(entry => $"the {entry.Next!.Foot.Name}'s on the {Called(entry.Link.Room.LevelId)}").Distinct().ToList();
        if (adjoining.Count > 0)
            wanting.Add($"a door joins it to another protected stairway leading to another final exit - {string.Join("; ", adjoining)} - Approved Document B Volume 2 (3.37) asks the construction between two adjoining protected stairways imperforate");

        if (openStair) { }
        else if (finals.Count == 0)
            wanting.Add($"its foot, the {stair.Foot.Name}, has no final exit - Approved Document B Volume 2 (3.36) asks every protected stairway to lead to a final exit, straight out or through an exit passageway");
        else
        {
            // As wide as the stair needs to be: Table 3.1's least, or Table 3.2's or 3.3's for its share of the people (5.20, round 114).
            var needed = Math.Max(phased ? share > 0 ? Math.Max(least, PhasedWidth(share)) : least : Needed(least, upper.Count, share), Needed(least, lower.Count, shareBelow));
            var widest = finals.Max(Width);
            if (widest < needed - 0.5)
                wanting.Add($"its final exit gives {widest:0} where the stair needs to be {needed:0} wide{(share > 0 && needed > least + 0.5 ? phased ? $" for {share} people of the busiest storey, evacuated in phases" : $" for {share} people from {Storeys(upper.Count)}" : string.Empty)} - Approved Document B Volume 2 (5.20) asks a final exit at least as wide as the escape route it serves needs to be");

            // Shared at the ground with a ground storey exit into the stair's foot or the lobby beyond it: wide enough for both flows (2.23, Diagram 2.6).
            var lobbies = new List<Room> { stair.Foot };
            lobbies.AddRange(links.Where(link => link.Room == stair.Foot && link.Other is { } next && !stairRooms.Contains(next) && Out(next).Any()).Select(link => link.Other!));
            foreach (var lobby in lobbies.Distinct())
            {
                var exits = links.Where(link => link.Room == lobby && link.Other is null && link.Outside).ToList();
                var joining = links.Where(link => link.Other == lobby && link.Room != lobby && !stairRooms.Contains(link.Room) && !within.Contains(link.Room)
                                                  && EscapeRoutes.Way(document, link.Door) is { Into: { } towards } && towards == lobby).DistinctBy(link => link.Door).ToList();
                if (exits.Count == 0 || joining.Count == 0) continue;
                var people = joining.Sum(link => EscapeRoutes.Users(document, link.Door));
                if (people <= 0) continue;
                var footAt = lobby == stair.Foot ? stair.FootAt : links.FirstOrDefault(link => link.Room == lobby && link.Other == stair.Foot)?.Inside ?? stair.FootAt;
                var near = exits.Min(exit => Math.Min(exit.Inside.DistanceTo(footAt), joining.Min(link => exit.Inside.DistanceTo(link.Beyond))));
                var close = people > 60 && near < 2000 - 0.5;
                var asked = close ? stair.Width + joining.Sum(link => Width(link.Door)) : (people / 2.5 + 60 * stair.Width / 1000) / 80 * 1000;
                var gives = exits.Max(exit => Width(exit.Door));
                if (gives < asked - 0.5)
                    wanting.Add($"its final exit from the {lobby.Name} gives {gives:0} where {people} people from the ground storey's {string.Join(", the ", joining.Select(link => link.Room.Name).Distinct())} join the stair's flow{(close ? $", {near / 1000:0.0} m from it" : string.Empty)} - Approved Document B Volume 2 (2.23, Diagram 2.6) asks {(close ? "the stair's width and the storey exit's together where more than 60 join less than 2 m from the final exit" : "W = (N/2.5 + 60S)/80")}: {asked:0}");
            }
        }

        return wanting;
    }

    /// <summary>The width a stair needs to be: Table 3.1's least, or the least that takes its share of the people above by Table 3.2 and 3.18, to 10 mm.</summary>
    public static double Needed(double least, int floors, int share)
    {
        var width = least;
        while (share > 0 && Capacity(width, floors) < share && width < 10000) width += 10;
        return width;
    }

    /// <summary>Whether a stair is reached on a storey only through a lobby or a corridor - not straight from the accommodation.</summary>
    private static bool Lobbied(BimDocument document, StairStack stair, Guid level, List<Escape.Link> links) =>
        links.Where(link => link.Room.LevelId == level && stair.Rooms.Contains(link.Room) && link.Other is { } beyond && !stair.Rooms.Contains(beyond))
            .All(link => Named(link.Other!, "lobby") || link.Other!.GetBoundary(document) is { IsEnclosed: true } boundary && Circulation.IsCorridor(link.Other!, boundary.Polygon));

    /// <summary>Whether there is a wheelchair space - a refuge - in a stair's room on a storey, or in a lobby or corridor opening into it.</summary>
    private static bool Refuge(BimDocument document, StairStack stair, Room room, List<Escape.Link> links)
    {
        var near = new List<Room> { room };
        near.AddRange(links.Where(link => link.Room == room && link.Other is { } beyond && !stair.Rooms.Contains(beyond) && FireSeparation.Circulation(beyond)).Select(link => link.Other!));
        return near.Any(each => each.GetBoundary(document) is { IsEnclosed: true } boundary && document.All<Component>().Any(piece => piece.LevelId == each.LevelId
            && Polygon2D.Contains(boundary.Polygon, piece.Location) && document.FindType<ComponentType>(piece.TypeId) is { Form: ComponentForm.WheelchairSpace, Width: >= 900 - 0.5, Depth: >= 1400 - 0.5 }));
    }

    /// <summary>A stair room's verdict - "Escape stair, 1100 wide, serving 3 storeys above the ground; ..." - or null for a room that is no stair's.</summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        if (!HorizontalEscape.Applies(document) || Stairs(document).FirstOrDefault(stair => stair.Rooms.Contains(room)) is not { } stair) return null;
        var said = $"Escape stair, {stair.Width:0} wide, serving {stair.Upper.Count} storey{(stair.Upper.Count == 1 ? string.Empty : "s")} above the ground";
        var wanting = Problems(document, stair).Concat(ExternalStairs.Faces(document).Where(problem => stair.Rooms.Contains(problem.Room)).Select(problem => problem.Said)).ToList();
        return wanting.Count == 0 ? said : $"{said}; {string.Join("; ", wanting)}";
    }

    /// <summary>Every stair's problems, said on its foot, for the Warnings window.</summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!HorizontalEscape.Applies(document)) yield break;
        foreach (var stair in Stairs(document))
        foreach (var said in Problems(document, stair))
            yield return new EscapeProblem(stair.Foot, said);
    }
}
