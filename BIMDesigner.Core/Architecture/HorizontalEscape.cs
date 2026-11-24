using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>A building's purpose group other than a dwelling's, as Approved Document B Volume 2 classes it (Table 0.1).</summary>
public enum PurposeGroup
{
    /// <summary>2(a): a hospital, a care home, a place of lawful detention - where people sleep and are cared for.</summary>
    ResidentialInstitutional,

    /// <summary>2(b): a hotel, a boarding house, a hall of residence, a hostel.</summary>
    ResidentialOther,

    /// <summary>3: offices.</summary>
    Office,

    /// <summary>4: shops, restaurants, cafés, bars, hairdressers.</summary>
    ShopAndCommercial,

    /// <summary>5: places of assembly, entertainment, worship, education and sport, libraries and clinics.</summary>
    AssemblyAndRecreation,

    /// <summary>6: factories and workshops.</summary>
    Industrial,

    /// <summary>7: warehouses and storage.</summary>
    Storage
}

/// <summary>
/// Escape across each storey of a building other than a dwelling (round 109), as Approved Document B Volume 2 (2019, with its 2020, 2022 and
/// 2025 amendments) sets it out in Section 2 and Appendix D.
///
/// The building's purpose group by its building type (Table 0.1). Each room's occupants by Table D1's floor space factor for what its name says it
/// is - an office 6 m² a person, a meeting room or restaurant 1, a bar 0.5, a shop's sales area 2, a bedroom 8 - else its purpose group's; its
/// fixed seats where it has them (note 5); none in stairs, lifts, corridors, halls, lobbies, toilets, cupboards and plant (D2). A room for more
/// than 60 two exits, more than 600 three (2.9, Table 2.2); its exits wide enough for them by Table 2.3 - 750 for 60, 850 for 110, 1050 for 220,
/// 5 mm a person beyond - the widest left out where there are two or more (2.21, 2.22). A storey likewise, by its storey exits: its outside
/// doors and the doors into its stairs.
///
/// Travel distances by Table 2.1, walked: a grid every 300 over each storey's rooms, joined through their doors - to within about 1%; from each point, the nearest
/// storey exit, and whether it has routes to two of them setting off 45 degrees or more apart (2.10, Diagram 2.2). Where it has, the nearest
/// exit within the limit for more than one direction; the stretch of a route before such a point - a dead end - within the limit for one
/// direction only (2.7, Diagram 2.1). A place of special fire hazard, a plant room and a hotel's bedroom with its en-suite measured within it,
/// to its own door (notes 2, 5); seating in rows 15 and 32 m.
///
/// Inner rooms (2.11) and corridors (2.24, 2.26, 2.28) - round 110 - in the room verdict too.
///
/// Not done: exits in a central core and open connections (2.12, 2.13), separate occupancies (2.17), single routes from rooms of more than 60 in places of
/// assembly (2.6a), stairs (Section 3), small premises (Section 4), higher hazard industrial and storage uses (note 3), buildings primarily for
/// disabled people, fire resisting separation of routes less than 45 degrees apart (2.10b), and final exits shared with a stair (2.23).
/// </summary>
public static class HorizontalEscape
{
    /// <summary>The grid's spacing, and how far apart its points are joined - far enough that a walk across it strays from straight by about 1% at most, mm.</summary>
    private const double Spacing = 300, Join = 1000;

    /// <summary>Whether Approved Document B Volume 2 reaches the building: one that is not a dwelling, nor a block of flats, in England.</summary>
    public static bool Applies(BimDocument document) => Circulation.Applies(document);

    /// <summary>The building's purpose group by its building type (Table 0.1); an office where the type says nothing more.</summary>
    public static PurposeGroup Purpose(BimDocument document)
    {
        var words = Toilets.WordsOf(document.ProjectInformation.BuildingType);
        bool Any(params string[] names) => names.Any(words.Contains);
        if (Any("hospital", "hospice", "infirmary", "sanatorium", "prison", "custodial", "detention") || Any("care", "nursing") && Any("home", "homes")) return PurposeGroup.ResidentialInstitutional;
        if (Any("hotel", "motel", "hostel", "boarding", "guest", "guesthouse", "residence", "residences", "dormitory") || Any("student") && Any("accommodation", "housing")) return PurposeGroup.ResidentialOther;
        if (Any("office", "offices", "bank")) return PurposeGroup.Office;
        if (Any("shop", "shops", "retail", "supermarket", "store", "stores", "restaurant", "cafe", "takeaway", "hairdresser", "salon", "showroom", "market", "pub", "bar", "commercial"))
            return PurposeGroup.ShopAndCommercial;
        if (Any("factory", "industrial", "workshop", "works", "manufacturing", "mill")) return PurposeGroup.Industrial;
        if (Any("warehouse", "storage", "depot", "distribution")) return PurposeGroup.Storage;
        if (Any("assembly", "bingo", "casino", "dance", "entertainment", "conference", "exhibition", "leisure", "amusement", "museum", "gallery", "club", "theatre", "cinema",
                "concert", "school", "college", "university", "academy", "gym", "gymnasium", "swimming", "pool", "sports", "sport", "stadium", "arena", "court", "church", "chapel",
                "mosque", "temple", "synagogue", "crematorium", "library", "clinic", "surgery", "health", "community", "village", "centre"))
            return PurposeGroup.AssemblyAndRecreation;
        return PurposeGroup.Office;
    }

    /// <summary>A purpose group as said: "an office (purpose group 3)".</summary>
    public static string Said(PurposeGroup group) => group switch
    {
        PurposeGroup.ResidentialInstitutional => "residential (institutional), purpose group 2(a)",
        PurposeGroup.ResidentialOther => "residential (other), purpose group 2(b)",
        PurposeGroup.Office => "an office, purpose group 3",
        PurposeGroup.ShopAndCommercial => "shop and commercial, purpose group 4",
        PurposeGroup.AssemblyAndRecreation => "assembly and recreation, purpose group 5",
        PurposeGroup.Industrial => "industrial, purpose group 6",
        _ => "storage, purpose group 7"
    };

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Table 2.3: how many people an exit of a clear width takes - none under 750.</summary>
    public static int Capacity(double width) => width >= 1100 - 0.5 ? (int)Math.Floor(width / 5 + 1e-6) : width >= 1050 - 0.5 ? 220 : width >= 850 - 0.5 ? 110 : width >= 750 - 0.5 ? 60 : 0;

    /// <summary>Table 2.2: the escape routes and exits a number of people needs.</summary>
    public static int ExitsFor(int people) => people > 600 ? 3 : people > 60 ? 2 : 1;

    /// <summary>
    /// What a room is for, as Table D1 has it - its floor space factor, m² a person, and what it is taken as - or none for a room that holds no
    /// one of its own: a stair, a lift, a corridor, hall or lobby, a toilet, a cupboard or a plant room (D2).
    /// </summary>
    public static (double Factor, string Said)? FloorSpace(BimDocument document, Room room, PurposeGroup group)
    {
        if (Named(room, "stair", "lift", "shaft", "corridor", "landing", "lobby", "porch", "passage", "toilet", "wc", "w.c", "washroom", "lavatory", "shower", "bathroom", "en-suite",
                "ensuite", "cupboard", "riser", "duct", "plant", "boiler", "switch", "transformer", "meter", "cleaner", "void") || FireSeparation.Refuse(room))
            return null;
        if (Named(room, "entrance hall", "reception hall") || FireSeparation.Circulation(room) && !Named(room, "assembly", "dance", "sports", "function", "village", "church", "concert", "main hall", "bingo"))
            return null;
        if (Toilets.Plan(document).TryGetValue(room, out var fitted) && (fitted.Wcs + fitted.Urinals + fitted.Showers + fitted.Baths > 0 || fitted.Changing)) return null;
        if (Lodging.Suites(document).TryGetValue(room, out var suite) && Lodging.Called(room) != suite) return null;

        if (Named(room, "bed-sit", "bedsit", "bed sit", "billiard", "snooker")) return (10, "a bed-sitting room or billiard room");
        if (Named(room, "bedroom") || Lodging.Suites(document).ContainsKey(room)) return (8, "a bedroom");
        if (Named(room, "dormitory")) return (5, "a dormitory");
        if (Named(room, "kitchen", "library")) return (7, "a kitchen or library");
        if (Named(room, "furniture", "carpet", "bulky", "cash and carry")) return (7, "a shop sales area for bulky goods");
        if (Named(room, "warehouse", "storage", "store")) return (30, "storage");
        if (Named(room, "office", "admin")) return (6, "an office");
        if (Named(room, "workshop", "factory", "production", "gallery", "museum")) return (5, "a workshop, production area, gallery or museum");
        if (Named(room, "shop", "sales", "retail")) return (2, "a shop sales area");
        if (Named(room, "skating")) return (2, "a skating rink");
        if (Named(room, "exhibition", "studio")) return (1.5, "an exhibition hall or studio");
        if (Named(room, "committee", "common room", "conference", "dining", "betting", "lounge", "meeting", "reading", "restaurant", "staff room", "staffroom", "waiting", "canteen", "cafe", "café", "boardroom", "board room"))
            return (1, "a meeting room, dining room, restaurant, lounge or the like");
        if (Named(room, "concourse", "queue", "queuing", "foyer")) return (0.7, "a concourse or queuing area");
        if (Named(room, "bar", "assembly", "dance", "club", "bingo", "arcade", "function", "hall")) return (0.5, "a bar without fixed seating, an assembly or dance hall, a club");
        return group switch
        {
            PurposeGroup.Office => (6, "an office, as the building is"),
            PurposeGroup.ShopAndCommercial => (2, "a shop sales area, as the building is"),
            PurposeGroup.AssemblyAndRecreation => (1, "a place of assembly with seating, as the building is"),
            PurposeGroup.Industrial => (5, "a production area, as the building is"),
            PurposeGroup.Storage => (30, "storage, as the building is"),
            _ => (8, "a bedroom, as the building is")
        };
    }

    /// <summary>A room's occupants: its fixed seats where it has them (Table D1 note 5), else its area by its floor space factor, rounded up; and how it was found.</summary>
    public static (int People, string How)? Occupants(BimDocument document, Room room, IReadOnlyList<Point2D> outline, PurposeGroup group)
    {
        var seats = document.All<Component>().Count(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(outline, piece.Location)
                                                              && document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.AudienceSeat);
        if (seats > 0) return (seats, $"{seats} fixed seats");
        if (FloorSpace(document, room, group) is not var (factor, said)) return null;
        var area = Polygon2D.Area(outline) / 1e6;
        return ((int)Math.Ceiling(area / factor - 1e-9), $"{area:0.0} m² at {factor:0.0#} m² a person, as {said}");
    }

    /// <summary>How a part of a building is used, as Table 2.1 sorts it.</summary>
    internal enum Kind
    {
        General,
        SeatedRows,
        SpecialHazard,
        Plant,
        Bedroom,
        BedroomCorridor
    }

    /// <summary>Table 2.1's travel distances for a use: in one direction only, and in more than one, mm; and the use said.</summary>
    internal static (double One, double More, string Use) Limits(PurposeGroup group, Kind kind) => kind switch
    {
        Kind.SpecialHazard => (9000, 18000, "a place of special fire hazard, within it"),
        Kind.Plant => (9000, 35000, "a plant room, within it"),
        Kind.SeatedRows => (15000, 32000, "an area with seating in rows"),
        Kind.Bedroom => (9000, 18000, "a bedroom, within it and its en-suite to its door"),
        Kind.BedroomCorridor => (9000, 35000, "a bedroom corridor"),
        _ => group switch
        {
            PurposeGroup.ResidentialInstitutional => (9000, 18000, "residential (institutional)"),
            PurposeGroup.ResidentialOther => (18000, 35000, "residential (other), elsewhere than bedrooms and their corridors"),
            PurposeGroup.Office => (18000, 45000, "an office"),
            PurposeGroup.ShopAndCommercial => (18000, 45000, "shop and commercial"),
            PurposeGroup.AssemblyAndRecreation => (18000, 45000, "assembly and recreation"),
            _ => (25000, 45000, $"{(group == PurposeGroup.Industrial ? "industrial" : "storage")}, normal hazard")
        }
    };

    // --- The storeys walked -------------------------------------------------------------

    /// <summary>A storey exit: its node on the grid, its door, and what it leads to.</summary>
    internal sealed record Exit(int Node, Door? Door, string What, double Width, Room Room);

    // --- Small premises (Section 4, round 124) ---------------------------------------------------------------------------------------

    /// <summary>Whether a building is small premises (2.2, 4.2), why or why not, and whether it is a bar or restaurant.</summary>
    public sealed record Premises(bool Small, string Said, bool BarOrRestaurant);

    /// <summary>Words that say highly flammable materials are sold, stored or used, so the premises are no small premises (4.1, round 131).</summary>
    private static readonly HashSet<string> Flammable = new(StringComparer.OrdinalIgnoreCase)
        { "flammable", "flammables", "firework", "fireworks", "explosive", "explosives", "petrol", "solvent", "solvents", "lpg" };

    /// <summary>The largest storey of small premises (4.2a iii), and of one with a single open stair alone (4.8d ii), m².</summary>
    public const double SmallStorey = 280, OpenStairStorey = 90;

    private static readonly string[] BarWords = { "restaurant", "bar", "pub", "cafe", "café", "dining", "bistro", "takeaway" };

    /// <summary>
    /// Whether a building is small premises, whose escape Section 4 sets out in place of Sections 2 and 3's number and position of exits and
    /// stairs, travel distances and open stairs (4.3): a small shop, office, factory or store (2.2) - single occupancy, its homes none; a
    /// basement, the ground storey and a first storey at most; no storey over 280 m²; a bar or restaurant planned for 30 people a storey, 100 on
    /// a ground storey with a final exit of its own (4.2). Highly flammable materials sold, stored or used (4.1) are not known here.
    /// </summary>
    public static Premises SmallPremises(BimDocument document)
    {
        Premises Work()
        {
            if (!Applies(document)) return new Premises(false, "not a building other than a dwelling", false);
            var group = Purpose(document);
            if (group is not (PurposeGroup.Office or PurposeGroup.ShopAndCommercial or PurposeGroup.Industrial or PurposeGroup.Storage))
                return new Premises(false, $"{Said(group)} - 2.2 offers small premises to shops, offices, industrial and storage premises", false);
            if (MixedUse.Is(document)) return new Premises(false, "homes in it too - small premises are a single occupancy (4.2a i)", false);
            if (document.All<Room>().Select(room => room.Occupancy.Trim()).Where(each => each.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
                return new Premises(false, "rooms of separate occupancies in it - small premises are a single occupancy (4.2a i)", false);
            if (new[] { document.ProjectInformation.BuildingType ?? string.Empty }.Concat(document.All<Room>().Select(room => room.Name))
                    .SelectMany(Toilets.WordsOf).FirstOrDefault(word => Flammable.Contains(word)) is { } flammable)
                return new Premises(false, $"highly flammable materials sold, stored or used in it - {flammable} - which small premises are not for (4.1)", false);
            var ground = Escape.Ground(document);
            var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document))).Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToList();
            if (rooms.Count == 0) return new Premises(false, "no rooms", false);
            double Elevation(Guid level) => document.FindLevel(level)?.Elevation ?? 0;
            var storeys = rooms.GroupBy(entry => entry.Room.LevelId).ToList();
            var above = storeys.Count(storey => Elevation(storey.Key) > ground + 100);
            var below = storeys.Count(storey => Elevation(storey.Key) < ground - 100);
            if (above > 1 || below > 1)
                return new Premises(false, $"{above} storey{(above == 1 ? "" : "s")} over the ground storey and {below} basement{(below == 1 ? "" : "s")} - small premises have a basement, the ground storey and a first storey at most (2.2b, 4.2a ii)", false);
            var largest = storeys.Max(storey => storey.Sum(entry => entry.Boundary.Area) / 1e6);
            if (largest > SmallStorey + 1e-6)
                return new Premises(false, $"a storey of {largest:0} m² - small premises have none over 280 m² (2.2a, 4.2a iii)", false);
            var bar = Toilets.WordsOf(document.ProjectInformation.BuildingType).Overlaps(BarWords) || rooms.Any(entry => Toilets.WordsOf(entry.Room.Name).Overlaps(BarWords));
            if (bar)
            {
                var stairs = Doors.StairRooms(document);
                var links = Escape.Links(document, rooms.Select(entry => entry.Room).ToHashSet());
                foreach (var storey in storeys)
                {
                    var people = storey.Sum(entry => Occupants(document, entry.Room, entry.Boundary.Polygon, group) is var (count, _) ? count : 0);
                    var groundStorey = Math.Abs(Elevation(storey.Key) - ground) <= 100;
                    var own = groundStorey && links.Any(link => link.Room.LevelId == storey.Key && link.Other is null && link.Outside && !stairs.Contains(link.Room));
                    var most = own ? 100 : 30;
                    if (people > most)
                        return new Premises(false, $"a bar or restaurant planned for {people} people on the {document.FindLevel(storey.Key)?.Name ?? "storey"} - small premises allow 30 a storey, 100 on a ground storey with a final exit of its own (4.2c)", true);
                }
            }

            var count = storeys.Count;
            return new Premises(true, $"small premises (2.2, 4.2): {count} storey{(count == 1 ? "" : "s")}, the largest {largest:0} m²{(bar ? ", a bar or restaurant" : string.Empty)}", bar);
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("horizontal escape: small premises", document, null, null, Work) : Work();
    }

    /// <summary>Whether a room a stair rises through is a room of its own with a stair standing open in it, rather than the stair's enclosure.</summary>
    public static bool OpenStairRoom(BimDocument document, Room room) =>
        Doors.StairRooms(document).Contains(room) && !Named(room, "stair", "landing", "hall", "lobby", "corridor", "entrance", "foyer", "passage", "vestibule");

    /// <summary>What a room comes to on its storey's grid: the farthest point from a storey exit and the longest one-way stretch, mm, against its limits.</summary>
    public sealed record Travel(double Nearest, double OneWay, double One, double More, string Use, bool Reached, bool Within, bool TwoWays = false, bool Small = false)
    {
        /// <summary>In a care home, the farthest walk to the nearest door into an adjoining protected area or a storey exit, mm (2.37 a); NaN elsewhere.</summary>
        public double Area { get; init; } = double.NaN;

        /// <summary>In a care home, how much of that walk is one way, mm; NaN elsewhere.</summary>
        public double AreaOneWay { get; init; } = double.NaN;
    }

    /// <summary>
    /// How a door is used in a fire, as the walk finds it (round 113): out of the room on the side farther from a storey exit - into the room
    /// beyond, or outside - by so many people walking to their nearest storey exit, and the room of the most people, of more than 60, among them.
    /// </summary>
    internal sealed record Use(Room From, Room? Into, bool Outside, double People, Room? Crowd);

    /// <summary>A storey walked: its rooms' travel, its storey exits, and how its doors are used.</summary>
    internal sealed record Storey(Guid Level, IReadOnlyDictionary<Room, Travel> Rooms, IReadOnlyList<Exit> Exits, IReadOnlyDictionary<Door, Use> Uses)
    {
        /// <summary>Each room walked: the other rooms its points' walk to their nearest storey exit passes through (round 130).</summary>
        public IReadOnlyDictionary<Room, HashSet<Room>> Passes { get; init; } = new Dictionary<Room, HashSet<Room>>();

        /// <summary>
        /// The rooms with a point whose every way to a storey exit passes within 4.5 m of an opening between floors, getting nearer it (2.13),
        /// with how near that point is, mm (round 130).
        /// </summary>
        public IReadOnlyDictionary<Room, double> Near { get; init; } = new Dictionary<Room, double>();

        /// <summary>The walk's points, each in its room (round 131).</summary>
        public IReadOnlyList<(Room Room, Point2D At)> Points { get; init; } = Array.Empty<(Room, Point2D)>();

        /// <summary>Each point's walk to its nearest storey exit, mm, and the next point toward it (-1 at an exit) (round 131).</summary>
        public IReadOnlyList<double> Nearest { get; init; } = Array.Empty<double>();

        public IReadOnlyList<int> Toward { get; init; } = Array.Empty<int>();

        /// <summary>The points with two ways out, their first steps 45 degrees or more apart (Diagram 2.2) (round 131).</summary>
        public IReadOnlySet<int> TwoWay { get; init; } = new HashSet<int>();
    }

    /// <summary>How far an escape route keeps from an opening between floors unless it leads away from it or another does, mm (2.13).</summary>
    public const double OpeningClear = 4500;

    /// <summary>
    /// The openings between floors a storey's escape routes are kept from (2.13), as Diagram 2.5 draws them on the storey they open from: the holes
    /// in its own floors - an atrium, an escalator's or an open stair's well - not one in an enclosed stair, a lift or a shaft. Nobody walks
    /// across one either.
    /// </summary>
    public static List<IReadOnlyList<Point2D>> Openings(BimDocument document, Guid level)
    {
        var stairs = Doors.StairRooms(document);
        bool Enclosed(Room room) => (stairs.Contains(room) || Named(room, "stair", "lift", "shaft", "riser", "duct")) && !OpenStairRoom(document, room);
        var found = new List<IReadOnlyList<Point2D>>();
        foreach (var floor in document.All<Floor>().Where(floor => floor.LevelId == level))
        foreach (var hole in floor.Holes.Where(hole => hole.Count >= 3))
        {
            var centre = new Point2D(hole.Average(p => p.X), hole.Average(p => p.Y));
            if (RoomsOf.Containing(document, level, centre) is { } room && Enclosed(room)) continue;
            found.Add(hole);
        }

        return found;
    }

    /// <summary>
    /// The rooms of a storey of a kind that the escape from other rooms passes through: a door out of one used, as the walk finds it, by more people
    /// than the room holds (round 129, care homes' 2.38).
    /// </summary>
    internal static IEnumerable<Room> PassedThrough(BimDocument document, Guid level, Func<Room, bool> which)
    {
        if (!Storeys(document).TryGetValue(level, out var storey)) yield break;
        var group = Purpose(document);
        foreach (var uses in storey.Uses.Values.Where(use => which(use.From)).GroupBy(use => use.From))
        {
            var own = uses.Key.GetBoundary(document) is { IsEnclosed: true } boundary && Occupants(document, uses.Key, boundary.Polygon, group) is var (count, _) ? count : 0;
            if (uses.Max(use => use.People) > own + 0.5) yield return uses.Key;
        }
    }

    /// <summary>The storeys walked, kept while the model does not change.</summary>
    internal static IReadOnlyDictionary<Guid, Storey> Storeys(BimDocument document)
    {
        IReadOnlyDictionary<Guid, Storey> Work()
        {
            var result = new Dictionary<Guid, Storey>();
            if (!Applies(document)) return result;
            // A building of mixed use: its homes are Volume 1's, left out (round 121).
            var outlines = document.All<Room>().Where(room => !MixedUse.Home(document, room)).Select(room => (Room: room, Boundary: room.GetBoundary(document)))
                .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
            var stairs = Doors.StairRooms(document);
            var cars = Lifts.Of(document).Select(lift => lift.Car).ToList();

            // A room with a stair standing open in it walked like any other - the stair no storey exit, but in small premises, where its head or
            // foot is (Table 4.1 notes, round 124).
            var premises = SmallPremises(document);
            bool Stair(Room room) => (stairs.Contains(room) || Named(room, "stair")) && !OpenStairRoom(document, room);
            bool Shaft(Room room) => Named(room, "lift", "shaft") || cars.Any(car => Polygon2D.Contains(car, room.Location));
            var links = Escape.Links(document, outlines.Keys.ToHashSet());
            var group = Purpose(document);
            var flights = premises.Small ? Escape.Flights(document, outlines).Where(flight => flight.Foot is not null && flight.Head is not null).ToList() : new List<Escape.Flight>();
            foreach (var level in outlines.Keys.Select(room => room.LevelId).Distinct())
                result[level] = Walk(document, outlines.Where(entry => entry.Key.LevelId == level).ToDictionary(entry => entry.Key, entry => entry.Value), links, Stair, Shaft, group, level, premises, flights);
            return result;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("horizontal escape", document, null, null, Work) : Work();
    }

    /// <summary>Whether a straight line between two points stays inside an outline.</summary>
    private static bool Clear(IReadOnlyList<Point2D> outline, Point2D a, Point2D b)
    {
        if (!Polygon2D.Contains(outline, a.MidpointTo(b))) return false;
        static double Cross(Point2D o, Point2D p, Point2D q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        for (var i = 0; i < outline.Count; i++)
        {
            var (c, d) = (outline[i], outline[(i + 1) % outline.Count]);
            var (d1, d2, d3, d4) = (Cross(c, d, a), Cross(c, d, b), Cross(a, b, c), Cross(a, b, d));
            if ((d1 > 1e-6 && d2 < -1e-6 || d1 < -1e-6 && d2 > 1e-6) && (d3 > 1e-6 && d4 < -1e-6 || d3 < -1e-6 && d4 > 1e-6)) return false;
        }

        return true;
    }

    private static Storey Walk(BimDocument document, Dictionary<Room, IReadOnlyList<Point2D>> rooms, List<Escape.Link> links, Func<Room, bool> stair, Func<Room, bool> shaft,
        PurposeGroup group, Guid level, Premises? premises = null, List<Escape.Flight>? flights = null)
    {
        // The grid: points every 300 in each room walked through - not its stairs, nor its lift shafts - joined to those within 1000 in sight.
        var nodes = new List<(Room Room, Point2D At)>();
        var edges = new List<List<(int To, double Length)>>();
        var inRoom = new Dictionary<Room, List<int>>();
        int Node(Room room, Point2D at)
        {
            nodes.Add((room, at));
            edges.Add(new List<(int, double)>());
            if (!inRoom.TryGetValue(room, out var list)) inRoom[room] = list = new List<int>();
            list.Add(nodes.Count - 1);
            return nodes.Count - 1;
        }

        void Edge(int a, int b, double length)
        {
            edges[a].Add((b, length));
            edges[b].Add((a, length));
        }

        var walked = rooms.Keys.Where(room => !stair(room) && !shaft(room)).ToList();
        var grid = new Dictionary<Room, List<int>>();

        // Nobody walks across an opening in the storey's floor - an atrium, an escalator's well (round 130).
        var voids = Openings(document, level);
        foreach (var room in walked)
        {
            var outline = rooms[room];
            var points = Energy.Daylight.Grid(outline, Spacing).Where(point => !voids.Any(hole => Polygon2D.Contains(hole, point))).Select(point => Node(room, point)).ToList();
            grid[room] = points;
            var buckets = points.GroupBy(node => ((int)Math.Floor(nodes[node].At.X / Join), (int)Math.Floor(nodes[node].At.Y / Join))).ToDictionary(bucket => bucket.Key, bucket => bucket.ToList());
            foreach (var node in points)
            {
                var (bx, by) = ((int)Math.Floor(nodes[node].At.X / Join), (int)Math.Floor(nodes[node].At.Y / Join));
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    foreach (var other in buckets.GetValueOrDefault((bx + dx, by + dy)) ?? new List<int>())
                        if (other > node && nodes[node].At.DistanceTo(nodes[other].At) <= Join && Clear(outline, nodes[node].At, nodes[other].At)
                            && !voids.Any(hole => Polygon2D.Contains(hole, nodes[node].At.MidpointTo(nodes[other].At))))
                            Edge(node, other, nodes[node].At.DistanceTo(nodes[other].At));
            }
        }

        // Each door's two sides, joined to the points in sight within 900 of them; its storey exits - outside, or into a stair.
        void Near(int door, Room room)
        {
            foreach (var node in grid.GetValueOrDefault(room) ?? new List<int>())
                if (nodes[node].At.DistanceTo(nodes[door].At) <= 900 && Clear(rooms[room], nodes[node].At, nodes[door].At))
                    Edge(door, node, nodes[node].At.DistanceTo(nodes[door].At));
        }

        var exits = new List<Exit>();
        var onto = ExternalStairs.DoorsOnto(document);
        var sides = new Dictionary<(Door, Room), int>();
        foreach (var link in links.Where(link => link.Room.LevelId == level && grid.ContainsKey(link.Room)))
        {
            if (!sides.TryGetValue((link.Door, link.Room), out var inside))
            {
                sides[(link.Door, link.Room)] = inside = Node(link.Room, link.Inside);
                Near(inside, link.Room);
            }

            var width = document.FindType<DoorType>(link.Door.TypeId) is { } type ? Doors.EscapeWidth(type) : 0;
            if (link.Other is { } other && grid.ContainsKey(other))
            {
                if (!sides.TryGetValue((link.Door, other), out var beyond))
                {
                    sides[(link.Door, other)] = beyond = Node(other, link.Beyond);
                    Near(beyond, other);
                    Edge(inside, beyond, link.Inside.DistanceTo(link.Beyond));
                }
            }
            // Outside: a final exit at the ground, or a storey exit onto an external stair - not a door onto a drop (round 133).
            else if (link.Other is null && link.Outside && exits.All(exit => exit.Node != inside) && ExternalStairs.WayOut(document, link.Door, onto))
                exits.Add(new Exit(inside, link.Door, onto.Contains(link.Door) ? "the door onto the external stair" : "a final exit, outside", width, link.Room));
            else if (link.Other is { } into && stair(into) && exits.All(exit => exit.Node != inside)) exits.Add(new Exit(inside, link.Door, $"the door into the {into.Name}", width, link.Room));
        }

        // Small premises: an open stair's head on a first storey, its foot in a basement, a storey exit (Table 4.1 notes a, b).
        var ground = Escape.Ground(document);
        var elevation = document.FindLevel(level)?.Elevation ?? 0;
        if (premises is { Small: true } && flights is not null)
            foreach (var flight in flights)
            {
                var (room, at, end) = elevation > ground + 100 && flight.Stair.TopLevelId == level ? (flight.Head, flight.HeadAt, "head")
                    : elevation < ground - 100 && flight.Stair.LevelId == level ? (flight.Foot, flight.FootAt, "foot") : (null, default, string.Empty);
                if (room is null || !grid.ContainsKey(room)) continue;
                var node = Node(room, at);
                Near(node, room);
                exits.Add(new Exit(node, null, $"the {end} of the open stair in the {room.Name}", flight.Stair.Width, room));
            }

        // From each storey exit, walked: how far, and the way back toward it from each point.
        var count = nodes.Count;
        (double[] Distance, int[] Toward) From(IEnumerable<int> sources)
        {
            var distance = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
            var toward = Enumerable.Repeat(-1, count).ToArray();
            var queue = new PriorityQueue<int, double>();
            foreach (var source in sources)
            {
                distance[source] = 0;
                queue.Enqueue(source, 0);
            }

            while (queue.TryDequeue(out var node, out var at))
            {
                if (at > distance[node] + 1e-6) continue;
                foreach (var (to, length) in edges[node])
                    if (at + length < distance[to] - 1e-6)
                    {
                        distance[to] = at + length;
                        toward[to] = node;
                        queue.Enqueue(to, distance[to]);
                    }
            }

            return (distance, toward);
        }

        var fields = exits.Select(exit => From(new[] { exit.Node })).ToList();
        var (nearest, toward) = From(exits.Select(exit => exit.Node));

        // Two ways out: the first steps toward two exits 45 degrees or more apart (Diagram 2.2).
        Vector2D? Heading((double[] Distance, int[] Toward) field, int node) =>
            field.Toward[node] is var next and >= 0 && !double.IsPositiveInfinity(field.Distance[node]) ? (nodes[next].At - nodes[node].At).NormalisedOrDefault(Vector2D.UnitX) : null;
        var twoWays = Enumerable.Range(0, count).Where(node =>
        {
            var headings = fields.Select(field => Heading(field, node)).OfType<Vector2D>().ToList();
            return headings.Any(one => headings.Any(other => one.Dot(other) <= Math.Cos(Math.PI / 4) + 1e-9));
        }).ToList();

        var twoWay = twoWays.ToHashSet();

        // The one-way stretch: from each point to the nearest point with two ways out, or to an exit.
        var (oneWay, _) = From(twoWays.Concat(exits.Select(exit => exit.Node)));

        // A care home: progressive horizontal evacuation (2.35 to 2.37) - each point walked to the nearest door into an adjoining protected area,
        // or a storey exit, and how much of it is one way (round 129).
        double[]? toArea = null, areaOneWay = null;
        if (CareHomes.Applies(document))
        {
            var areas = CareHomes.Areas(document, level);
            var areaOf = new Dictionary<Room, int>();
            for (var i = 0; i < areas.Count; i++)
                foreach (var room in areas[i]) areaOf[room] = i;
            var into = sides.Where(side => areaOf.TryGetValue(side.Key.Item2, out var mine)
                                           && links.FirstOrDefault(link => link.Door == side.Key.Item1 && link.Room == side.Key.Item2) is { Other: { } beyond }
                                           && areaOf.TryGetValue(beyond, out var theirs) && theirs != mine)
                .Select(side => side.Value).ToList();
            var targets = exits.Select(exit => exit.Node).Concat(into).Distinct().ToList();
            if (targets.Count > 0)
            {
                toArea = From(targets).Distance;
                var areaFields = targets.Select(target => From(new[] { target })).ToList();
                var areaTwo = Enumerable.Range(0, count).Where(node =>
                {
                    var headings = areaFields.Select(field => Heading(field, node)).OfType<Vector2D>().ToList();
                    return headings.Any(one => headings.Any(other => one.Dot(other) <= Math.Cos(Math.PI / 4) + 1e-9));
                }).ToList();
                areaOneWay = From(areaTwo.Concat(targets)).Distance;
            }
        }

        // Within a room: to its own doors - a place of special fire hazard's, a plant room's, a hotel bedroom's with its en-suite.
        var suites = Lodging.Suites(document);
        var corridors = walked.Where(room => FireSeparation.Circulation(room) && links.Any(link => link.Room == room && link.Other is { } other && suites.ContainsKey(other))).ToHashSet();
        Kind KindOf(Room room) =>
            FireSeparation.Special(room) ? Kind.SpecialHazard
            : Named(room, "plant") ? Kind.Plant
            : suites.ContainsKey(room) && group == PurposeGroup.ResidentialOther ? Kind.Bedroom
            : corridors.Contains(room) && group == PurposeGroup.ResidentialOther ? Kind.BedroomCorridor
            : document.All<Component>().Any(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(rooms[room], piece.Location) && document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.AudienceSeat) ? Kind.SeatedRows
            : Kind.General;

        var travel = new Dictionary<Room, Travel>();

        // Small premises: Table 4.1 from any point to the nearest storey exit - 27 m on a ground storey with a single exit, 18 m on a basement
        // or first storey with a single stair, 45 m on a storey with more than one - in place of Table 2.1 (4.3b, 4.6).
        if (premises is { Small: true })
        {
            var groundStorey = Math.Abs(elevation - ground) <= 100;
            var (limit, what) = exits.Count >= 2 ? (45000.0, "a storey with more than one exit or stair")
                : groundStorey ? (27000.0, "a ground storey with a single exit")
                : (18000.0, elevation < ground ? "a basement with a single stair" : "a first storey with a single stair");
            foreach (var room in walked)
            {
                var points = grid[room];
                if (points.Count == 0) continue;
                travel[room] = new Travel(points.Max(node => nearest[node]), 0, limit, limit, $"small premises, {what}", points.All(node => !double.IsPositiveInfinity(nearest[node])), false,
                    points.Any(twoWay.Contains), true);
            }
        }

        foreach (var room in walked.Where(room => !travel.ContainsKey(room)))
        {
            var kind = KindOf(room);
            var (one, more, use) = Limits(group, kind);
            var points = grid[room];
            if (points.Count == 0) continue;
            if (kind is Kind.SpecialHazard or Kind.Plant or Kind.Bedroom)
            {
                // To the doors out of it - or out of the suite it is part of.
                var own = kind == Kind.Bedroom ? walked.Where(other => suites.GetValueOrDefault(other) == suites[room]).ToHashSet() : new HashSet<Room> { room };
                var doors = sides.Where(side => own.Contains(side.Key.Item2) && links.Any(link => link.Door == side.Key.Item1 && link.Room == side.Key.Item2 && (link.Other is null || !own.Contains(link.Other))))
                    .Select(side => side.Value).ToList();
                var (within, _) = From(doors);
                var far = points.Max(node => within[node]);
                var ways = doors.Count >= 2;
                travel[room] = new Travel(far, ways ? 0 : far, one, more, use, !double.IsPositiveInfinity(far), true);
                continue;
            }

            travel[room] = new Travel(points.Max(node => nearest[node]), points.Max(node => oneWay[node]), one, more, use, points.All(node => !double.IsPositiveInfinity(nearest[node])), false,
                points.Any(twoWay.Contains))
            {
                Area = toArea is null ? double.NaN : points.Max(node => toArea[node]),
                AreaOneWay = areaOneWay is null ? double.NaN : points.Max(node => areaOneWay[node])
            };
        }

        // Who would use each door in a fire (round 113): each point's share of its room's people walked to its nearest storey exit, summed
        // through the doors on the way - and the room of the most people, of more than 60, among them.
        var people = walked.ToDictionary(room => room, room => Occupants(document, room, rooms[room], group) is var (n, _) ? n : 0);
        var flow = new double[count];
        var crowd = new Room?[count];
        foreach (var room in walked)
        {
            var points = grid[room];
            if (points.Count == 0 || people[room] == 0) continue;
            foreach (var node in points)
            {
                flow[node] = (double)people[room] / points.Count;
                if (people[room] > 60) crowd[node] = room;
            }
        }

        foreach (var node in Enumerable.Range(0, count).Where(node => toward[node] >= 0).OrderByDescending(node => nearest[node]))
        {
            var next = toward[node];
            flow[next] += flow[node];
            if (crowd[node] is { } from && (crowd[next] is not { } held || people[from] > people[held])) crowd[next] = from;
        }

        var uses = new Dictionary<Door, Use>();
        foreach (var ((door, room), node) in sides)
        {
            if (double.IsPositiveInfinity(nearest[node]) || links.FirstOrDefault(each => each.Door == door && each.Room == room) is not { } link) continue;
            if (link.Other is { } other && sides.TryGetValue((door, other), out var beyond))
            {
                // Between two rooms walked: out of this one where it is the farther from a storey exit.
                if (nearest[node] <= nearest[beyond]) continue;
                var through = toward[node] == beyond;
                uses[door] = new Use(room, other, false, through ? flow[node] : 0, through ? crowd[node] : null);
            }
            else if (exits.Any(exit => exit.Node == node))
                uses[door] = new Use(room, link.Other, link.Other is null, flow[node], crowd[node]);
        }

        // The rooms each room's escape passes through on the way to its nearest storey exit (round 130: 2.16, 2.17).
        var passes = walked.ToDictionary(room => room, _ => new HashSet<Room>());
        foreach (var room in walked)
        {
            var seen = new HashSet<int>();
            foreach (var start in grid[room])
                for (var at = start; at >= 0 && seen.Add(at); at = toward[at])
                    if (nodes[at].Room != room) passes[room].Add(nodes[at].Room);
        }

        // Openings between floors: a point fails where its every way to a storey exit comes within 4.5 m of one getting nearer it (2.13).
        var near = new Dictionary<Room, double>();
        var openings = voids;
        if (openings.Count > 0 && exits.Count > 0)
        {
            double Gap(Point2D p) => openings.Min(hole => Polygon2D.Contains(hole, p) ? 0
                : Enumerable.Range(0, hole.Count).Min(i => Line2D.DistanceFromSegment(p, hole[i], hole[(i + 1) % hole.Count])));
            var gap = nodes.Select(node => Gap(node.At)).ToArray();
            // Bad where, once within 4.5 m of it, the way gets more than half a metre nearer it than where it came within - toward it, not along it.
            bool Good((double[] Distance, int[] Toward) field, int node)
            {
                if (double.IsPositiveInfinity(field.Distance[node])) return false;
                var entry = double.NaN;
                var steps = 0;
                for (var at = node; at >= 0 && steps++ < count; at = field.Toward[at])
                {
                    if (gap[at] >= OpeningClear - 0.5) continue;
                    if (double.IsNaN(entry)) entry = gap[at];
                    else if (gap[at] < entry - 500) return false;
                }

                return true;
            }

            foreach (var room in walked)
            foreach (var node in grid[room].Where(node => gap[node] > 0.5))
                if (!fields.Any(field => Good(field, node)))
                    near[room] = Math.Min(near.GetValueOrDefault(room, double.PositiveInfinity), gap[node]);
        }

        return new Storey(level, travel, exits, uses) { Passes = passes, Near = near, Points = nodes, Nearest = nearest, Toward = toward, TwoWay = twoWay };
    }

    // --- What a room and a storey come to ------------------------------------------------

    /// <summary>The doors out of a room that lead on: outside, into a stair, or into a room with another door of its own.</summary>
    internal static List<(Door Door, double Width)> ExitsOf(BimDocument document, Room room, List<Escape.Link> links)
    {
        var exits = new List<(Door, double)>();
        foreach (var link in links.Where(link => link.Room == room))
        {
            var leads = link.Other is null ? link.Outside
                : Named(link.Other, "stair") || links.Any(other => other.Room == link.Other && other.Door != link.Door);
            if (!leads || link.Other is { } beyond && Named(beyond, "cupboard", "store", "wc", "toilet", "riser", "duct")) continue;
            if (exits.Any(exit => exit.Item1 == link.Door)) continue;
            exits.Add((link.Door, document.FindType<DoorType>(link.Door.TypeId) is { } type ? Doors.EscapeWidth(type) : 0));
        }

        return exits;
    }

    /// <summary>How many people some exits take with the widest left out where there are two or more (2.21, 2.22).</summary>
    private static int Taken(IReadOnlyList<double> widths) =>
        widths.Count <= 1 ? widths.Sum(Capacity) : widths.OrderByDescending(width => width).Skip(1).Sum(Capacity);

    /// <summary>
    /// What a room of a building other than a dwelling comes to for escape - "An office: 14 people, 82.0 m² at 6 m² a person; 1 exit 760 clear;
    /// 23.4 m from a storey exit at most, 4.1 m of it one way" - with what Approved Document B Volume 2 asks of it that it falls short of; null
    /// for a room it does not walk.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        if (!Applies(document) || !Storeys(document).TryGetValue(room.LevelId, out var storey) || !storey.Rooms.TryGetValue(room, out var travel)
            || room.GetBoundary(document) is not { IsEnclosed: true } boundary) return null;
        var group = Purpose(document);
        var links = Escape.Links(document, new HashSet<Room>(document.All<Room>().Where(each => each.LevelId == room.LevelId)));
        var said = new List<string>();
        var wanting = new List<string>();

        if (Occupants(document, room, boundary.Polygon, group) is var (people, how) && people > 0)
        {
            var exits = ExitsOf(document, room, links);
            said.Add($"{people} {(people == 1 ? "person" : "people")}, {how}; {exits.Count} exit{(exits.Count == 1 ? string.Empty : "s")}{(exits.Count > 0 ? $" {string.Join(", ", exits.Select(exit => $"{exit.Width:0}"))} clear" : string.Empty)}");
            var asked = ExitsFor(people);
            if (exits.Count < asked && !travel.Small)
                wanting.Add($"{exits.Count} exit{(exits.Count == 1 ? string.Empty : "s")} for {people} people - Approved Document B Volume 2 (2.9, Table 2.2) asks {(asked == 1 ? "one at least" : $"{asked} for more than {(asked == 3 ? 600 : 60)}")}");
            var taken = Taken(exits.Select(exit => exit.Width).ToList());
            if (exits.Count > 0 && taken < people)
                wanting.Add($"its exits take {taken} people{(exits.Count > 1 ? ", the widest left out" : string.Empty)} - Approved Document B Volume 2 (2.18, 2.21, Table 2.3) asks room for its {people}: 750 clear for 60, 850 for 110, 1050 for 220, 5 mm a person beyond");
        }

        var to = travel.Within ? "from its door" : "from a storey exit";
        if (!travel.Reached)
            wanting.Add(travel.Within ? "no door out of it" : "no way from it to a storey exit, walked through the doors - Approved Document B Volume 2 (2.1) asks means of escape from every point of a storey to a storey exit: a final exit or a door into a protected stair");
        else if (!double.IsNaN(travel.Area))
        {
            // A care home, planned for progressive horizontal evacuation: Table 2.1 to the exit into the next protected area, 64 m to a storey exit (2.37).
            said.Add($"{travel.Area / 1000:0.0} m from the next protected area or a storey exit at most{(travel.AreaOneWay > 0.5 ? $", {travel.AreaOneWay / 1000:0.0} m one way" : string.Empty)}; {travel.Nearest / 1000:0.0} m from a storey exit");
            var oneOver = travel.AreaOneWay > travel.One + 0.5;
            var moreOver = travel.Area > travel.More + 0.5 && travel.Area > travel.AreaOneWay + 0.5;
            var over = new[] { oneOver ? $"{travel.AreaOneWay / 1000:0.0} m in one direction only" : null, moreOver ? $"{travel.Area / 1000:0.0} m in all" : null }.OfType<string>().ToList();
            if (over.Count > 0)
                wanting.Add($"{string.Join(" and ", over)} from the exit into the next protected area, walked - Approved Document B Volume 2 (2.37 a, Table 2.1) allows {travel.One / 1000:0} m in one direction only and {travel.More / 1000:0} m where escape is possible in more than one, within a care home's protected area");
            if (travel.Nearest > CareHomes.StoreyExit + 0.5)
                wanting.Add($"{travel.Nearest / 1000:0.0} m from a storey exit, walked - Approved Document B Volume 2 (2.37 b) allows 64 m from any point of a care home to a storey exit or a final exit");
        }
        else
        {
            said.Add($"{travel.Nearest / 1000:0.0} m {to} at most{(travel.OneWay > 0.5 ? $", {travel.OneWay / 1000:0.0} m one way" : string.Empty)}");
            if (travel.Small && travel.Nearest > travel.More + 0.5)
                wanting.Add($"{travel.Nearest / 1000:0.0} m from a storey exit, walked - Approved Document B Volume 2 (4.6, Table 4.1) allows {travel.More / 1000:0} m from any point of {travel.Use[("small premises, ".Length)..]} in small premises to its nearest storey exit");
            var oneOver = !travel.Small && travel.OneWay > travel.One + 0.5;
            var moreOver = !travel.Small && travel.Nearest > travel.More + 0.5 && travel.Nearest > travel.OneWay + 0.5;
            var over = new[] { oneOver ? $"{travel.OneWay / 1000:0.0} m in one direction only" : null, moreOver ? $"{travel.Nearest / 1000:0.0} m {to} in all" : null }.OfType<string>().ToList();
            if (over.Count > 0)
                wanting.Add($"{string.Join(" and ", over)}, walked - Approved Document B Volume 2 (Table 2.1{(travel.Within ? ", notes 2, 5" : ", 2.7, Diagram 2.1")}) allows {travel.One / 1000:0} m in one direction only and {travel.More / 1000:0} m where escape is possible in more than one, for {travel.Use}");
        }

        if (travel.Small && Named(room, "store", "storage", "stock") && StoreShort(document, room, boundary) is { } store) wanting.Add(store);
        wanting.AddRange(InnerRoom(document, room, links, group));
        if (Circulation.IsCorridor(room, boundary.Polygon)) wanting.AddRange(Corridor(document, room, boundary.Polygon, travel, storey, links));

        var head = $"{char.ToUpperInvariant(travel.Use[0])}{travel.Use[1..]}{(said.Count > 0 ? $": {string.Join("; ", said)}" : string.Empty)}";
        return wanting.Count == 0 ? head : $"{head}; {string.Join("; ", wanting)}";
    }

    /// <summary>A store room of small premises not enclosed in fire resisting construction, REI 30, its doors too (4.5): said; null where it is.</summary>
    private static string? StoreShort(BimDocument document, Room room, RoomBoundaryResult boundary)
    {
        var walls = boundary.BoundingWalls.Where(wall => document.GetWallType(wall) is { Function: not WallFunction.Exterior } type && !document.IsCurtainWall(wall) && Doors.FireMinutes(type.FireRating) < 30)
            .Select(wall => document.GetWallType(wall)!.Name).Distinct().ToList();
        var doors = boundary.BoundingWalls.SelectMany(wall => document.All<Door>().Where(door => door.HostWallId == wall.Id))
            .Where(door => document.FindType<DoorType>(door.TypeId) is not { } type || Doors.FireMinutes(type.FireRating) < 30)
            .Select(door => document.FindType<DoorType>(door.TypeId)?.Name ?? "a door").Distinct().ToList();
        if (walls.Count == 0 && doors.Count == 0) return null;
        return $"it is a store room, {string.Join(" and ", new[] { walls.Count > 0 ? $"its walls {string.Join(", ", walls)} less than 30 minutes" : null, doors.Count > 0 ? $"its doors {string.Join(", ", doors)} not 30 minute fire doors" : null }.OfType<string>())} - Approved Document B Volume 2 (4.5) asks store rooms of small premises enclosed in fire resisting construction, REI 30";
    }

    // --- Inner rooms (2.11) and corridors (2.24 to 2.28) ------------------------------------------

    /// <summary>Whether a room is walked through to escape: a hall, corridor, landing or lobby - a stair or a lift being no access room.</summary>
    private static bool Through(Room room) => FireSeparation.Circulation(room) && !Named(room, "stair");

    /// <summary>The one room a room's every door leads into, where it is not circulation, a stair or outside - its access room; else null.</summary>
    private static Room? AccessRoom(Room room, List<Escape.Link> links)
    {
        var outs = links.Where(link => link.Room == room).ToList();
        if (outs.Count == 0 || outs.Any(link => link.Other is null || Through(link.Other) || Named(link.Other, "stair", "lift"))) return null;
        var into = outs.Select(link => link.Other!).Distinct().ToList();
        return into.Count == 1 ? into[0] : null;
    }

    /// <summary>
    /// What an inner room falls short of (2.11, Diagram 2.3): 60 people at most - 30 in residential (institutional) buildings; not a bedroom;
    /// through one access room only; that room no place of special fire hazard; a vision panel in its door, or fire detection in the access room.
    /// A room holding no one - a toilet, a store, a cupboard - and a room within a hotel's suite are left alone.
    /// </summary>
    private static IEnumerable<string> InnerRoom(BimDocument document, Room room, List<Escape.Link> links, PurposeGroup group)
    {
        if (AccessRoom(room, links) is not { } access || Named(room, "store", "cupboard") || room.GetBoundary(document) is not { IsEnclosed: true } boundary) yield break;
        var suites = Lodging.Suites(document);
        if (suites.TryGetValue(room, out var suite) && suites.GetValueOrDefault(access) == suite) yield break;
        if (Occupants(document, room, boundary.Polygon, group) is not var (people, _) || people == 0) yield break;
        var most = group == PurposeGroup.ResidentialInstitutional ? 30 : 60;
        var what = $"an inner room, reached only through the {access.Name}";
        if (people > most) yield return $"{what}, for {people} people - Approved Document B Volume 2 (2.11a) allows {most} at most in one";
        if (Named(room, "bedroom") || suites.ContainsKey(room)) yield return $"{what}, a bedroom - Approved Document B Volume 2 (2.11b) allows no bedroom to be an inner room";
        if (AccessRoom(access, links) is { } beyond) yield return $"{what}, itself reached only through the {beyond.Name} - Approved Document B Volume 2 (2.11d) allows escape through one access room only";
        if (FireSeparation.Special(access)) yield return $"{what}, a place of special fire hazard - Approved Document B Volume 2 (2.11f) asks the access room not to be one";
        var panel = links.Where(link => link.Room == room && link.Other == access)
            .Any(link => document.FindType<DoorType>(link.Door.TypeId)?.LeafDesign is DoorLeafDesign.Glazed or DoorLeafDesign.HalfGlazed or DoorLeafDesign.FrenchGlazed or DoorLeafDesign.FullGlass);
        var detected = access.GetBoundary(document) is { IsEnclosed: true } accessBoundary && document.All<Component>().Any(piece => piece.LevelId == access.LevelId
            && Polygon2D.Contains(accessBoundary.Polygon, piece.Location) && document.FindType<ComponentType>(piece.TypeId)?.Form is ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm);
        if (!panel && !detected)
            yield return $"{what}, with no vision panel in its door and no fire detection in the {access.Name} - Approved Document B Volume 2 (2.11g) asks one of these, or its enclosure stopping 500 below the ceiling, so its people know of a fire in the access room";
    }

    /// <summary>
    /// What a corridor falls short of: a protected corridor where it serves bedrooms or is a dead end past a 2 m recess - its inside walls REI 30,
    /// its doors E 20 Sa and self-closing (2.24, Table B1, Table C1); more than 12 m long between two storey exits, divided about midway
    /// by a self-closing fire door (2.26); a dead end more than 4.5 m long separated by one from where escape is in two directions (2.28).
    /// </summary>
    private static IEnumerable<string> Corridor(BimDocument document, Room room, IReadOnlyList<Point2D> outline, Travel travel, Storey storey, List<Escape.Link> links)
    {
        var (bedrooms, deadEnd) = Protection(document, room, travel, links);
        var (_, length) = Toilets.Size(outline);

        if (bedrooms || deadEnd)
        {
            var walls = document.Walls.Where(wall => wall.LevelId == room.LevelId && document.GetWallType(wall) is { Function: WallFunction.Interior } type
                                                     && WallRules.RoomsBeside(document, wall) is var (one, other) && (one == room || other == room) && one is not null && other is not null && one != other
                                                     && Doors.FireMinutes(type.FireRating) < 30)
                .Select(wall => document.GetWallType(wall)!.Name).Distinct().ToList();
            var doors = links.Where(link => link.Room == room && link.Other is { } other && !Named(other, "stair") && !Through(other))
                .Where(link => document.FindType<DoorType>(link.Door.TypeId) is not { } type || Doors.FireMinutes(type.FireRating) < 20 || !type.SelfClosing)
                .Select(link => $"the {link.Other!.Name}'s").Distinct().ToList();
            if (walls.Count > 0 || doors.Count > 0)
                yield return $"a protected corridor, {(bedrooms ? "serving bedrooms" : $"a dead end {travel.OneWay / 1000:0.0} m long")}: {string.Join(" and ", new[] { walls.Count > 0 ? $"its walls {string.Join(", ", walls)} less than 30 minutes" : null, doors.Count > 0 ? $"{string.Join(", ", doors)} door{(doors.Count == 1 ? string.Empty : "s")} not E 20 Sa and self-closing" : null }.OfType<string>())} - Approved Document B Volume 2 (2.24, Tables B1, C1) asks a corridor serving bedrooms, or a dead end past a recess 2 m deep, enclosed in fire-resisting construction, REI 30, its doors E 20 Sa fire doorsets, self-closing";
        }

        var exits = storey.Exits.Count(exit => exit.Room == room);
        if (exits >= 2 && length > 12000 + 0.5)
            yield return $"{length / 1000:0.0} m long between {exits} storey exits, not divided - Approved Document B Volume 2 (2.26, Diagram 2.9) asks a corridor more than 12 m long joining two storey exits divided about midway by a self-closing fire door, its screens fire-resisting: draw it as two corridors either side of one";
        if (travel.TwoWays && travel.OneWay > 4500 + 0.5)
            yield return $"its dead end {travel.OneWay / 1000:0.0} m long opens straight into where escape is in two directions - Approved Document B Volume 2 (2.28, Diagram 2.10) asks a dead end more than 4.5 m long separated from it by self-closing fire doors";
    }

    /// <summary>Why a corridor is a protected corridor (2.24): it serves bedrooms, or it is a dead end more than 2 m long.</summary>
    private static (bool Bedrooms, bool DeadEnd) Protection(BimDocument document, Room room, Travel travel, List<Escape.Link> links)
    {
        var suites = Lodging.Suites(document);
        return (links.Any(link => link.Room == room && link.Other is { } other && (suites.ContainsKey(other) || Named(other, "bedroom"))),
            !travel.Within && travel.Reached && travel.OneWay > 2000 + 0.5);
    }

    /// <summary>Whether a room of a building other than a home is a protected corridor (2.24): a corridor serving bedrooms, or a dead end more than 2 m long.</summary>
    internal static bool ProtectedCorridor(BimDocument document, Room room)
    {
        if (!Applies(document) || !Storeys(document).TryGetValue(room.LevelId, out var storey) || !storey.Rooms.TryGetValue(room, out var travel)
            || room.GetBoundary(document) is not { IsEnclosed: true } boundary || !Circulation.IsCorridor(room, boundary.Polygon)) return false;
        List<Escape.Link> Work() => Escape.Links(document, new HashSet<Room>(document.All<Room>().Where(each => each.LevelId == room.LevelId)));
        var links = ModelCache.Of(document) is { } cache ? cache.Keep("escape: links", document, room.LevelId, null, Work) : Work();
        return Protection(document, room, travel, links) is (true, _) or (_, true);
    }

    /// <summary>
    /// What each storey of a building other than a dwelling falls short of in its storey exits - their number and width for its occupants (2.9,
    /// 2.21, Tables 2.2, 2.3) - said on its first room.
    /// </summary>
    public static IEnumerable<EscapeProblem> Problems(BimDocument document)
    {
        if (!Applies(document)) yield break;
        var group = Purpose(document);
        foreach (var storey in Storeys(document).Values)
        {
            var rooms = storey.Rooms.Keys.ToList();
            if (rooms.Count == 0) continue;
            var people = rooms.Sum(room => room.GetBoundary(document) is { IsEnclosed: true } boundary && Occupants(document, room, boundary.Polygon, group) is var (count, _) ? count : 0);
            if (people == 0) continue;
            var first = rooms.OrderBy(room => room.Number, StringComparer.Ordinal).ThenBy(room => room.Name, StringComparer.Ordinal).First();
            var name = document.FindLevel(storey.Level)?.Name ?? "storey";
            var asked = ExitsFor(people);
            if (storey.Exits.Count < asked && !SmallPremises(document).Small)
                yield return new EscapeProblem(first, $"the {name} holds {people} people and has {storey.Exits.Count} storey exit{(storey.Exits.Count == 1 ? string.Empty : "s")} - Approved Document B Volume 2 (2.3, 2.9, Table 2.2) asks {asked}: final exits, or doors into protected stairs");
            var taken = Taken(storey.Exits.Select(exit => exit.Width).ToList());
            if (storey.Exits.Count > 0 && taken < people)
                yield return new EscapeProblem(first, $"the {name}'s storey exits - {string.Join(", ", storey.Exits.Select(exit => $"{exit.What} {exit.Width:0} clear"))} - take {taken} people{(storey.Exits.Count > 1 ? " with the widest left out" : string.Empty)} where it holds {people} - Approved Document B Volume 2 (2.18, 2.21, 2.22, Table 2.3) asks room for them all: 750 clear for 60, 850 for 110, 1050 for 220, 5 mm a person beyond");
        }
    }
}
