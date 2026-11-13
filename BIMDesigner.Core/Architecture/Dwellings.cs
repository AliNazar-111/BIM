using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A dwelling as the Nationally Described Space Standard sizes it: the gross internal area its
/// Table 1 asks for its bedrooms, its bedspaces and its storeys, and the built-in storage it asks
/// on top - told by the beds and the rooms in the model. Its rooms' areas add up to less than its
/// gross internal area, which takes in the walls between them and any hall or landing not drawn as
/// a room, so a dwelling whose rooms already reach the area asked is big enough, and one whose
/// rooms fall short may be.
/// </summary>
public static class Dwellings
{
    /// <summary>Table 1: (bedrooms, bedspaces) → the least gross internal area, m², on one, two and three storeys (0 where the standard gives none).</summary>
    private static readonly Dictionary<(int Bedrooms, int Bedspaces), double[]> Areas = new()
    {
        [(1, 1)] = new[] { 39.0, 0, 0 },
        [(1, 2)] = new[] { 50.0, 58, 0 },
        [(2, 3)] = new[] { 61.0, 70, 0 },
        [(2, 4)] = new[] { 70.0, 79, 0 },
        [(3, 4)] = new[] { 74.0, 84, 90 },
        [(3, 5)] = new[] { 86.0, 93, 99 },
        [(3, 6)] = new[] { 95.0, 102, 108 },
        [(4, 5)] = new[] { 90.0, 97, 103 },
        [(4, 6)] = new[] { 99.0, 106, 112 },
        [(4, 7)] = new[] { 108.0, 115, 121 },
        [(4, 8)] = new[] { 117.0, 124, 130 },
        [(5, 6)] = new[] { 103.0, 110, 116 },
        [(5, 7)] = new[] { 112.0, 119, 125 },
        [(5, 8)] = new[] { 121.0, 128, 134 },
        [(6, 7)] = new[] { 116.0, 123, 129 },
        [(6, 8)] = new[] { 125.0, 132, 138 }
    };

    /// <summary>
    /// The gross internal area Table 1 asks of a dwelling, m² - for a number of bedspaces it does
    /// not list with so many bedrooms, the next it does - or null where it lists none.
    /// </summary>
    public static double? AreaAsked(int bedrooms, int bedspaces, int storeys)
    {
        if (bedrooms < 1 || storeys < 1) return null;
        var rows = Areas.Where(row => row.Key.Bedrooms == Math.Min(bedrooms, 6)).OrderBy(row => row.Key.Bedspaces).ToList();
        var row = rows.FirstOrDefault(candidate => candidate.Key.Bedspaces >= bedspaces);
        var areas = row.Value ?? rows.LastOrDefault().Value;
        if (areas is null) return null;
        var area = areas[Math.Clamp(storeys, 1, 3) - 1];
        return area > 0 ? area : areas.Max();
    }

    /// <summary>The built-in storage Table 1 asks on top of the area, m²: 1.0 for one bedspace, 1.5 for a one-bedroom for two, half a square metre more a bedroom after.</summary>
    public static double StorageAsked(int bedrooms, int bedspaces) => bedrooms <= 1 ? (bedspaces <= 1 ? 1.0 : 1.5) : 1.0 + 0.5 * Math.Min(bedrooms, 6);

    /// <summary>
    /// What a dwelling comes to against the Nationally Described Space Standard, or null where it has no bedroom to size it by - or is built to the US's codes.
    /// Where the rooms are named into dwellings, each of them, by its name: "Flat 1: ... | Flat 2: ...".
    /// </summary>
    public static string? Verdict(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return null;
        if (Named(document) is { Count: > 0 } named)
        {
            var each = named.Select(dwelling => Verdict(document, dwelling.Name) is { } verdict ? $"{dwelling.Name}: {verdict}" : null).OfType<string>().ToList();
            return each.Count == 0 ? null : string.Join(" | ", each);
        }

        var rooms = Enclosed(document, document.All<Room>());

        // Its gross internal area as the standard measures it (note 10): to the inside faces of the
        // walls round it, the partitions, stairs and voids over them in it - and floor under 1.5 m
        // of headroom left out, unless it is only storage. Where its walls close round nothing yet, its rooms.
        double? Measured()
        {
            var gross = document.Levels.Sum(level => Views.AreaPlans.Measure(document, level.Id, Views.AreaScheme.GrossInternal).Area) / 1e6;
            var low = rooms.Where(entry => !Storage(entry.Room))
                .Select(entry => RoomHeadroom.Measure(document, entry.Room))
                .Sum(space => space is { UnderRoof: true } ? space.Area - space.AreaCounted : 0) / 1e6;
            return gross > 0 ? gross - low : null;
        }

        return Judge(document, rooms, Kitchens.Bedspaces(document), document.All<Component>(), Measured);
    }

    /// <summary>
    /// What one dwelling of a block or a terrace - the rooms named into it - comes to against the Nationally Described Space Standard: by its
    /// bedrooms, the beds in its rooms, the storeys its rooms are on, and its rooms' area - the area plans measuring the gross internal area of a
    /// whole storey, not of a flat on it. Null where it has no bedroom or bed, or is built to the US's codes.
    /// </summary>
    public static string? Verdict(BimDocument document, string dwelling)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return null;
        var rooms = Enclosed(document, document.All<Room>().Where(room => string.Equals(Of(room), dwelling.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (rooms.Count == 0) return null;
        using var outlines = new RoomsOf.Outlines();
        var held = rooms.Select(entry => entry.Room).ToHashSet();
        var pieces = document.All<Component>().Where(piece => RoomsOf.Of(document, piece) is { } room && held.Contains(room)).ToList();
        return Judge(document, rooms, Kitchens.Bedspaces(document, pieces), pieces, () => null);
    }

    private static List<(Room Room, RoomBoundaryResult Boundary)> Enclosed(BimDocument document, IEnumerable<Room> rooms) =>
        rooms.Select(room => (Room: room, Boundary: room.GetBoundary(document))).Where(entry => entry.Boundary.IsEnclosed).ToList();

    private static bool Storage(Room room) => new[] { "store", "storage", "cupboard", "airing" }.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A dwelling of these rooms, with so many bedspaces and these fittings in it, against Table 1 - its gross internal area measured, m², or, where none can be, its rooms'.</summary>
    private static string? Judge(BimDocument document, IReadOnlyList<(Room Room, RoomBoundaryResult Boundary)> rooms, int bedspaces, IEnumerable<Component> pieces, Func<double?> measure)
    {
        var bedrooms = rooms.Count(entry => Bedrooms.Of(document, entry.Room, entry.Boundary.Polygon) is not null);
        if (bedrooms == 0 || bedspaces == 0) return null;

        var storeys = rooms.Select(entry => entry.Room.LevelId).Distinct().Count();
        var storage = rooms.Where(entry => Storage(entry.Room)).Sum(entry => entry.Boundary.Area) / 1e6;
        var gross = measure();
        var measured = gross is not null;
        var area = gross ?? rooms.Sum(entry => entry.Boundary.Area) / 1e6;
        var said = $"{bedrooms} bedroom{(bedrooms == 1 ? string.Empty : "s")}, {bedspaces} bedspace{(bedspaces == 1 ? string.Empty : "s")}, {storeys} storey{(storeys == 1 ? string.Empty : "s")}: {(measured ? "gross internal area" : "rooms")} {Units.OfSquareMetres(area, "0.0")}";

        // A one-bedroom flat for one with a shower room, not a bath: 37 m².
        var showerRoom = bedrooms == 1 && bedspaces == 1 && storeys == 1
            && !pieces.Any(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Bath);
        var wanting = new List<string>();
        if ((showerRoom ? 37 : AreaAsked(bedrooms, bedspaces, storeys)) is { } asked && area < asked - 0.05)
            wanting.Add(measured
                ? $"the Nationally Described Space Standard asks {Units.OfSquareMetres(asked, "0")} of gross internal area - {Units.OfSquareMetres(asked - area, "0.0")} short"
                : $"the Nationally Described Space Standard asks {Units.OfSquareMetres(asked, "0")} of gross internal area, which takes in the walls between the rooms - short if they do not make up the {Units.OfSquareMetres(asked - area, "0.0")}");
        var stored = StorageAsked(bedrooms, bedspaces);
        if (storage < stored - 0.05)
            wanting.Add($"built-in storage {Units.OfSquareMetres(storage, "0.0")} in rooms called a store or cupboard - it asks {Units.OfSquareMetres(stored, "0.0")}");

        return wanting.Count == 0 ? $"{said} - big enough" : $"{said} - {string.Join("; ", wanting)}";
    }

    // ---- the dwellings of a block or a terrace ------------------------------------------------------------------------------
    //
    // Each dwelling - a flat of a block, a house of a terrace - is the rooms given its name in their Dwelling. A room given none
    // belongs to the one dwelling of a house, or to the common parts of a block: on a drain, fittings in rooms of no dwelling
    // count as one dwelling where they are all it serves, and as none beside fittings of named ones.

    /// <summary>The dwelling a room is in, its name trimmed; none for one in the common parts, or a house's.</summary>
    public static string Of(Room room) => room.Dwelling.Trim();

    /// <summary>The dwelling a fitting serves: the one the room it stands in is in; none where it is in no room, or a room in none.</summary>
    public static string Of(BimDocument document, Component fitting) => RoomsOf.Of(document, fitting) is { } room ? Of(room) : string.Empty;

    /// <summary>The dwellings named in the project, each with its rooms, in order of their names - Flat 2 before Flat 10.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<Room> Rooms)> Named(BimDocument document) =>
        document.All<Room>().Where(room => Of(room).Length > 0).GroupBy(Of, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, NaturalOrder.Instance)
            .Select(group => (Of(group.First()), (IReadOnlyList<Room>)group.ToList())).ToList();

    /// <summary>How many dwellings fittings of these dwellings' names serve: each name one; the unnamed one only where nothing is named.</summary>
    public static int Count(IEnumerable<string> names)
    {
        var all = names.ToList();
        var named = all.Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return named > 0 ? named : all.Count > 0 ? 1 : 0;
    }

    // Approved Document H Table 5 (and Table A1): the flow from so many dwellings, each with the typical household group of 1 WC,
    // 1 bath, 1 or 2 basins, 1 sink and 1 washing machine, as BS EN 12056 designs for.
    private static readonly (int Dwellings, double Flow)[] Flows = { (1, 2.5), (5, 3.5), (10, 4.1), (15, 4.6), (20, 5.1), (25, 5.4), (30, 5.8) };

    /// <summary>
    /// The flow from so many dwellings, l/s: Approved Document H Table 5 - read straight off it at 1, 5, 10 and on to 30, and
    /// between its rows on the straight line joining them; beyond 30 its last, 5.8, the table going no further.
    /// </summary>
    public static double Flow(int dwellings)
    {
        if (dwellings <= 1) return Flows[0].Flow;
        if (dwellings >= Flows[^1].Dwellings) return Flows[^1].Flow;
        var upper = Flows.First(row => row.Dwellings >= dwellings);
        var lower = Flows.Last(row => row.Dwellings <= dwellings);
        return upper.Dwellings == lower.Dwellings ? upper.Flow : lower.Flow + (upper.Flow - lower.Flow) * (dwellings - lower.Dwellings) / (upper.Dwellings - lower.Dwellings);
    }

    /// <summary>A dwelling's floor area, mm²: its rooms' together.</summary>
    public static double Area(BimDocument document, IEnumerable<Room> rooms) => rooms.Sum(room => room.GetBoundary(document).Area);
}
