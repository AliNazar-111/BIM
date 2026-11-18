using System.Text.RegularExpressions;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Glass in fire resisting walls and doors (round 96), as Approved Document B Volume 1 sets it out: glazed elements meeting integrity but not
/// insulation limited by where they are (3.88, 3.89, Table B5, read off the page); and doors in a compartment wall no more than a quarter of
/// its length unless they insulate (C8).
///
/// Table B5 in a block of flats: between a flat and the common parts none (item 6); between the common stair and a protected corridor or lobby,
/// in a wall above 1100 mm with one stair and above 100 mm with more, in a door above 100 mm (item 8); between the stair and any other room
/// of the common parts, none in a wall with one stair - above 1100 mm with more - and a quarter of a door's area with one, half with more
/// (item 7); between a protected corridor or lobby and a room off it, in a wall above 1100 mm with one stair, 100 mm with more, in a door above
/// 100 mm (items 8b, 9); across the corridors, above 100 mm (item 10). In a house with a protected stairway, its enclosure's walls above 1100
/// mm, its doors as they come (item 2). Glass that insulates - its type's Fire Rating EI or REI - is not limited; glass of no rating in a wall
/// that resists fire is said as such.
/// </summary>
public static partial class FireGlazing
{
    /// <summary>Table B5's heights: glass above so far over the floor, mm.</summary>
    public const double Above1100 = 1100, Above100 = 100;

    /// <summary>C8: the most of a compartment wall's length in doors that do not insulate.</summary>
    public const double DoorShare = 0.25;

    [GeneratedRegex(@"\bR?EI\s*\d|insulat", RegexOptions.IgnoreCase)]
    private static partial Regex Insulating();

    /// <summary>Whether a rating says it insulates as well as holds integrity: EI 30, REI 60, or insulation said. FD 30S and E 30 do not.</summary>
    public static bool Insulated(string rating) => Insulating().IsMatch(rating);

    /// <summary>
    /// The glass in a door's leaves as its design draws it (OpeningModel's leaf): how high its bottom is over the floor, mm, and its share of
    /// the leaves' area. Null for a door with no glass.
    /// </summary>
    public static (double Bottom, double Share)? DoorGlass(DoorType type, double width, double height)
    {
        if (!Doors.Glazed(type)) return null;
        var w = Math.Max(1, (width - 2 * Doors.Frame) / Math.Max(1, type.LeafCount));
        var h = Math.Max(1, height - Doors.Frame);
        if (type.LeafDesign == DoorLeafDesign.FullGlass)
        {
            var (slim, head, kick) = (Math.Min(90, w * 0.14), Math.Min(100, h * 0.06), Math.Min(200, h * 0.09));
            return (kick, (w - 2 * slim) * (h - head - kick) / (w * h));
        }

        var (stile, topRail, bottomRail) = (Math.Min(110, w * 0.16), Math.Min(110, h * 0.06), Math.Min(220, h * 0.11));
        const double rail = 100;
        var across = w - 2 * stile;
        var top = h - topRail;
        var (bottom, area) = type.LeafDesign switch
        {
            DoorLeafDesign.FrenchGlazed => (bottomRail + h * 0.18 + rail, across * (top - bottomRail - h * 0.18 - rail)),
            DoorLeafDesign.HalfGlazed => (h * 0.42 + rail, across * (top - h * 0.42 - rail)),
            DoorLeafDesign.ArchedTopLight => ArchedLight(across, h, top),
            _ => (bottomRail, across * (top - bottomRail))
        };
        return (bottom, Math.Max(0, area) / (w * h));
    }

    /// <summary>An arched top light: from its springing to its crown, half an ellipse across the leaf.</summary>
    private static (double Bottom, double Area) ArchedLight(double across, double h, double top)
    {
        var rise = Math.Min(across / 2, h * 0.2);
        return (top - rise - 20, Math.PI / 4 * across * rise);
    }

    /// <summary>Where glass is between two rooms of a building, by Table B5: what it may be in a wall and in a door, and the item. Null where Table B5 does not limit it.</summary>
    private sealed record Limit(string Where, string Item, double? WallAbove, double? DoorAbove, double? DoorShare);

    /// <summary>Table B5's limit on glass between two rooms - each side's room - of the model; null where none applies.</summary>
    private static Limit? LimitBetween(BimDocument document, Room a, Room b)
    {
        if (FireSeparation.Of(document) is { } block)
        {
            if (!block.Outlines.ContainsKey(a) || !block.Outlines.ContainsKey(b)) return null;
            var (flatA, flatB) = (Dwellings.Of(a).Length > 0, Dwellings.Of(b).Length > 0);
            if (flatA || flatB)
                return flatA && flatB && Dwellings.Same(a, b) ? null : new Limit($"between {(flatA ? Dwellings.Of(a) : Dwellings.Of(b))} and the {(flatA ? b : a).Name}", "item 6", null, null, null);
            var single = block.StairCount <= 1;
            var (stairA, stairB) = (block.Stairs.Contains(a), block.Stairs.Contains(b));
            if (stairA != stairB)
            {
                var other = stairA ? b : a;
                return FireSeparation.Circulation(other)
                    ? new Limit($"between the common stair and the {other.Name}", "item 8a", single ? Above1100 : Above100, Above100, 1)
                    : new Limit($"between the common stair and the {other.Name}", "item 7", single ? null : Above1100, 0, single ? 0.25 : 0.5);
            }

            if (stairA) return null;
            var (circA, circB) = (FireSeparation.Circulation(a), FireSeparation.Circulation(b));
            if (circA && circB) return new Limit($"across the common parts, between the {a.Name} and the {b.Name}", "item 10", Above100, Above100, 1);
            if (circA != circB) return new Limit($"between the {(circA ? a : b).Name} and the {(circA ? b : a).Name} off it", "items 8b and 9", single ? Above1100 : Above100, Above100, 1);
            return null;
        }

        // A house with a storey more than 4.5 m up: its stair a protected stairway, the glass in its enclosure's walls above 1100 mm (item 2).
        if (document.ProjectInformation.ClearanceStandard.IsUs() || !Doors.HasHighStorey(document)) return null;
        var stairs = Doors.StairRooms(document);
        return stairs.Contains(a) != stairs.Contains(b) ? new Limit($"in the enclosure of the protected stairway, beside the {(stairs.Contains(a) ? b : a).Name}", "item 2", Above1100, 0, 1) : null;
    }

    private const string Rule = "Approved Document B (3.88, Table B5) limits glass that holds integrity but does not insulate";

    /// <summary>What the glass of a door falls short of in Table B5.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Door door, DoorType type)
    {
        if (DoorGlass(type, door.WidthOf(type), door.HeightOf(type)) is not var (bottom, share) || Insulated(type.FireRating)) yield break;
        var (a, b) = Doors.RoomsBeside(document, door);
        if (a is null || b is null || a == b || LimitBetween(document, a, b) is not { } limit) yield break;
        if (limit.DoorAbove is null)
        {
            yield return ($"it is glazed {limit.Where}.", $"{Rule} in a door {limit.Where} to none ({limit.Item}) - its type's Fire Rating EI where it insulates.");
            yield break;
        }

        var (shortfalls, allowed) = (new List<string>(), new List<string>());
        if (limit.DoorAbove > 0)
        {
            allowed.Add($"above {limit.DoorAbove:0} mm from the floor");
            if (bottom < limit.DoorAbove.Value - 0.5) shortfalls.Add($"its glass comes down to {bottom:0} mm over the floor");
        }

        if (limit.DoorShare is { } most && most < 1)
        {
            allowed.Add($"{100 * most:0}% of the door's area");
            if (share > most + 1e-6) shortfalls.Add($"its glass is {100 * share:0}% of the leaf");
        }

        if (shortfalls.Count > 0)
            yield return ($"it is glazed {limit.Where}: {string.Join(", and ", shortfalls)}.", $"{Rule} in a door {limit.Where} to {string.Join(", and ", allowed)} ({limit.Item}) - its type's Fire Rating EI where it insulates.");
    }

    /// <summary>What a window in a wall between two rooms - a glazed screen - falls short of in Table B5.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Window window, WindowType type)
    {
        if (document.Find<Wall>(window.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType || Insulated(type.FireRating)) yield break;
        var reach = wallType.Structure.TotalWidth / 2 + 150;
        var a = RoomsOf.Containing(document, wall.LevelId, wall.PointAt(wallType.Structure, window.DistanceAlongWall, reach));
        var b = RoomsOf.Containing(document, wall.LevelId, wall.PointAt(wallType.Structure, window.DistanceAlongWall, -reach));
        if (a is null || b is null || a == b || LimitBetween(document, a, b) is not { } limit) yield break;

        if (Doors.FireMinutes(type.FireRating) == 0)
        {
            yield return ($"it is glass {limit.Where} with no fire rating.", "Approved Document B (3.88, Table B3) asks glass in a fire resisting wall to resist fire as the wall does - its type's Fire Rating, E for integrity, EI where it insulates too.");
            yield break;
        }

        var bottom = window.Placed(document, type, wall).Sill + WindowRules.Frame;
        if (limit.WallAbove is null)
            yield return ($"it is glass {limit.Where}.", $"{Rule} in a wall {limit.Where} to none ({limit.Item}) - its type's Fire Rating EI where it insulates.");
        else if (bottom < limit.WallAbove.Value - 0.5)
            yield return ($"it is glass {limit.Where}, coming down to {bottom:0} mm over the floor.", $"{Rule} in a wall {limit.Where} to above {limit.WallAbove:0} mm from the floor ({limit.Item}) - its type's Fire Rating EI where it insulates.");
    }

    /// <summary>C8: a compartment wall's doors no more than a quarter of its length, unless they insulate.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall)
    {
        if (wall.Length < 1 || !FireSeparation.Compartment(document, wall)) yield break;
        var doors = document.All<Door>().Where(door => door.HostWallId == wall.Id).Select(door => (Door: door, Type: document.FindType<DoorType>(door.TypeId)))
            .Where(entry => entry.Type is not null && !Insulated(entry.Type.FireRating)).ToList();
        var width = doors.Sum(entry => entry.Door.WidthOf(entry.Type));
        if (width > DoorShare * wall.Length + 0.5)
            yield return ($"it is a compartment wall {Units.Metres(wall.Length, "0.0")} long with {Units.Metres(width, "0.0")} of doors in it, {100 * width / wall.Length:0}%.",
                "Approved Document B (C8) keeps the doors in a compartment wall to a quarter of its length, unless they insulate as well as hold integrity (Table B3) - or a clear space is kept either side of them.");
    }
}
