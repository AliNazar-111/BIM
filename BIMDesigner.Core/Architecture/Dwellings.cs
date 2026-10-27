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

    /// <summary>What a dwelling comes to against the Nationally Described Space Standard, or null where it has no bedroom to size it by - or is built to the US's codes.</summary>
    public static string? Verdict(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return null;

        var rooms = document.All<Room>()
            .Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed)
            .ToList();
        var bedrooms = rooms.Count(entry => Bedrooms.Of(document, entry.Room, entry.Boundary.Polygon) is not null);
        var bedspaces = Kitchens.Bedspaces(document);
        if (bedrooms == 0 || bedspaces == 0) return null;

        var storeys = rooms.Select(entry => entry.Room.LevelId).Distinct().Count();
        var area = rooms.Sum(entry => entry.Boundary.Area) / 1e6;
        var storage = rooms
            .Where(entry => new[] { "store", "storage", "cupboard", "airing" }.Any(word => entry.Room.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Sum(entry => entry.Boundary.Area) / 1e6;
        var said = $"{bedrooms} bedroom{(bedrooms == 1 ? string.Empty : "s")}, {bedspaces} bedspace{(bedspaces == 1 ? string.Empty : "s")}, {storeys} storey{(storeys == 1 ? string.Empty : "s")}: rooms {Units.OfSquareMetres(area, "0.0")}";

        // A one-bedroom flat for one with a shower room, not a bath: 37 m².
        var showerRoom = bedrooms == 1 && bedspaces == 1 && storeys == 1
            && !document.All<Component>().Any(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Bath);
        var wanting = new List<string>();
        if ((showerRoom ? 37 : AreaAsked(bedrooms, bedspaces, storeys)) is { } asked && area < asked - 0.05)
            wanting.Add($"the Nationally Described Space Standard asks {Units.OfSquareMetres(asked, "0")} of gross internal area, which takes in the walls between the rooms - short if they do not make up the {Units.OfSquareMetres(asked - area, "0.0")}");
        var stored = StorageAsked(bedrooms, bedspaces);
        if (storage < stored - 0.05)
            wanting.Add($"built-in storage {Units.OfSquareMetres(storage, "0.0")} in rooms called a store or cupboard - it asks {Units.OfSquareMetres(stored, "0.0")}");

        return wanting.Count == 0 ? $"{said} - big enough" : $"{said} - {string.Join("; ", wanting)}";
    }
}
