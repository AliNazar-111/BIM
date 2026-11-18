using System.Text.RegularExpressions;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Security of new dwellings (round 97), as Approved Document Q (2015) sets it out for requirement Q1 - in England, for houses and flats.
///
/// Easily accessible doors and windows (Appendix A) - any part within 2 m vertically of an accessible level surface, the ground or a
/// basement's; a window too within 2 m of a flat or sloping roof pitched under 30° that is within 3.5 m of the ground - giving access into a
/// dwelling, or a building of flats, from outside, and a flat's door from the common parts: secure doorsets (1.1, 1.2) - PAS 24, STS 201, LPS
/// 1175 security rating 2, STS 202 burglary rating 2, LPS 2081 security rating B, or a bespoke timber doorset by Appendix B - and secure
/// windows and rooflights (2.1, 2.2) - PAS 24, STS 204, LPS 1175 SR1 or LPS 2081 A. A garage's doors need not be secure where it has no door
/// into the home; where it has, either its doors or the door into the home are (1.1 note). A letter plate's aperture 260 mm by 40 mm at most
/// (1.3). The main door into each dwelling - a flat's from the common parts, a house's the one its approach path comes to, else one named
/// front or entrance, else one into a hall - a door viewer unless clear glass in it or a window beside it shows callers, and a door chain or
/// limiter (1.4).
/// </summary>
public static partial class Security
{
    /// <summary>Easily accessible: within so far vertically of a surface; a roof under that pitch, within so far of the ground (Appendix A), mm and degrees.</summary>
    public const double Reach = 2000, LowRoof = 3500, LowPitch = 30;

    /// <summary>A letter plate's largest aperture (1.3), mm.</summary>
    public const double LetterWidth = 260, LetterHeight = 40;

    /// <summary>A window beside the main door, its glass showing callers in place of a viewer (1.4): within so far of the door's frame, mm - taken so.</summary>
    public const double Beside = 1000;

    [GeneratedRegex(@"(?:SR|BR|RATING)\s*([1-6])", RegexOptions.IgnoreCase)]
    private static partial Regex Rating();

    [GeneratedRegex(@"LPS\s*2081[^A-Z]*(?:ISSUE\s*\d[^A-Z]*)?(?:SECURITY\s*RATING\s*|SR\s*)?([A-D])\b", RegexOptions.IgnoreCase)]
    private static partial Regex Lps2081();

    /// <summary>
    /// Whether a type's security standard, as written, makes it secure (1.2, 2.2): PAS 24 for either; for a door STS 201, LPS 1175 SR2 or
    /// better, STS 202 BR2 or better, LPS 2081 B or better, or Appendix B's bespoke timber doorset; for a window STS 204, LPS 1175 SR1 or
    /// better, LPS 2081 A or better.
    /// </summary>
    public static bool Secure(string standard, bool door)
    {
        if (string.IsNullOrWhiteSpace(standard)) return false;
        var text = standard.ToUpperInvariant().Replace(" ", string.Empty);
        if (text.Contains("PAS24")) return true;
        int? Graded() => Rating().Match(standard) is { Success: true } match ? int.Parse(match.Groups[1].Value) : null;
        if (text.Contains("LPS1175")) return Graded() >= (door ? 2 : 1);
        if (text.Contains("LPS2081")) return Lps2081().Match(standard) is { Success: true } letter && char.ToUpperInvariant(letter.Groups[1].Value[0]) >= (door ? 'B' : 'A');
        if (door)
            return text.Contains("STS201") || text.Contains("STS202") && Graded() >= 2 || text.Contains("APPENDIXB") || text.Contains("BESPOKETIMBER");
        return text.Contains("STS204");
    }

    /// <summary>Whether Approved Document Q applies: a project of homes, in England.</summary>
    private static bool Applies(BimDocument document) =>
        !document.ProjectInformation.ClearanceStandard.IsUs() && FireDesign.UseOf(document) is BuildingUse.House or BuildingUse.Flats;

    /// <summary>The vertical gap between a span - a door's or window's bottom and top - and a surface: none where the surface is level with any part of it.</summary>
    private static double Gap(double bottom, double top, double surface) => Math.Max(0, Math.Max(bottom - surface, surface - top));

    /// <summary>The ground at a point outside: the ground modelled there, else the ground floor's level.</summary>
    private static double GroundAt(BimDocument document, Point2D at) => Terrains.GroundAt(document, at) ?? Escape.Ground(document);

    /// <summary>A roof low and flat enough to stand on at a point - pitched under 30°, its surface within 3.5 m of the ground - and its surface's height there; null where none.</summary>
    private static double? LowRoofAt(BimDocument document, Point2D at, double ground) =>
        document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Contains(at)).Select(roof => (Roof: roof, Top: roof.TopAt(document, at)))
            .Where(entry => entry.Top <= ground + LowRoof + 0.5 && Pitch(document, entry.Roof, at) < LowPitch)
            .Select(entry => (double?)entry.Top).DefaultIfEmpty(null).Min();

    private static double Pitch(BimDocument document, Roof roof, Point2D at)
    {
        const double step = 100;
        var here = roof.TopAt(document, at);
        var slope = new[] { new Vector2D(step, 0), new Vector2D(0, step) }.Select(d => (roof.TopAt(document, at + d) - here) / step).ToArray();
        return Math.Atan(Math.Sqrt(slope[0] * slope[0] + slope[1] * slope[1])) * 180 / Math.PI;
    }

    private const string Standards = "PAS 24, or STS 201, LPS 1175 SR2, STS 202 BR2, LPS 2081 B or Appendix B's bespoke timber doorset";

    /// <summary>What a door falls short of in Approved Document Q.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Door door, DoorType type)
    {
        if (!Applies(document) || document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) yield break;
        var (a, b) = Doors.RoomsBeside(document, door);
        var external = Doors.External(document, door, type) && (a is null) != (b is null);
        var inside = a ?? b;
        var entrance = !external && a is not null && b is not null && (Dwellings.Of(a).Length > 0) != (Dwellings.Of(b).Length > 0);
        if (!external && !entrance) yield break;

        // Easily accessible: an outside door within 2 m vertically of the ground at its foot; a flat's door from the common parts always (Q1 c).
        var floor = document.FindLevel(door.LevelId)?.Elevation ?? 0;
        var reach = wallType.Structure.TotalWidth / 2 + 300;
        if (external)
        {
            var side = inside is not null && RoomsOf.Containing(document, wall.LevelId, wall.PointAt(wallType.Structure, door.DistanceAlongWall, reach)) == inside ? -1.0 : 1.0;
            var outside = wall.PointAt(wallType.Structure, door.DistanceAlongWall, side * reach);
            if (Gap(floor, floor + door.HeightOf(type), GroundAt(document, outside)) > Reach + 0.5) yield break;
        }

        // A garage's doors: secure, or the door from the garage into the home (1.1 note); none asked where it has no door into the home.
        var secure = Secure(type.Security, door: true);
        if (external && inside is not null && Doors.Garage(document, inside))
        {
            var into = Interconnecting(document, inside);
            if (!secure && into.Count > 0 && !into.All(each => Secure(document.FindType<DoorType>(each.TypeId)?.Security ?? string.Empty, door: true)))
                yield return ("it is a door into the garage, which has a door into the home, and neither is a secure doorset.",
                    $"Approved Document Q (1.1 note, 1.2) asks either a garage's doors or the door from it into the home to be a secure doorset - {Standards}: its type's Security.");
        }
        else if (!secure)
            yield return ($"it is an easily accessible door {(entrance ? "into a flat from the common parts" : "into the building")}{(type.Security.Length > 0 ? $", its security '{type.Security}'" : ", no security standard given")}.",
                $"Approved Document Q (1.1, 1.2) asks an easily accessible doorset into a dwelling, or a building of flats, to be a secure doorset - {Standards}: its type's Security.");

        if (LetterPlate(type.LetterPlate) is var (width, height) && (Math.Max(width, height) > LetterWidth + 0.5 || Math.Min(width, height) > LetterHeight + 0.5))
            yield return ($"its letter plate's aperture is {width:0} by {height:0} mm.", "Approved Document Q (1.3) keeps a letter plate's aperture to 260 mm by 40 mm, with a flap or the like against keys being fished out.");

        // The main door into a dwelling: a viewer, unless clear glass or a window beside it shows callers; and a chain or limiter (1.4).
        if (entrance || Main(document, door, inside))
        {
            var sees = type.DoorViewer || Doors.Glazed(type) || WindowBeside(document, door, type, wall);
            if (!sees || !type.DoorLimiter)
                yield return ($"it is the main door into {(entrance ? Dwellings.Of(Dwellings.Of(a!).Length > 0 ? a! : b!) : "the home")}, with {string.Join(" and ", new[] { sees ? null : "no door viewer, glass in it or a window beside it", type.DoorLimiter ? null : "no door chain or limiter" }.OfType<string>())}.",
                    "Approved Document Q (1.4) asks the main door into a dwelling for a door viewer - unless clear glass in it or a window next to it shows callers - and a door chain or limiter: its type's Door Viewer and Door Chain or Limiter.");
        }
    }

    /// <summary>A letter plate's aperture as its type writes it - "260 x 40" - width and height, mm; null where it has none.</summary>
    public static (double Width, double Height)? LetterPlate(string text)
    {
        var numbers = Regex.Matches(text ?? string.Empty, @"\d+(?:\.\d+)?").Select(match => double.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return numbers.Count >= 2 && numbers[0] > 0 && numbers[1] > 0 ? (numbers[0], numbers[1]) : null;
    }

    /// <summary>The doors from a garage into rooms that are not garages - into the home.</summary>
    private static List<Door> Interconnecting(BimDocument document, Room garage) =>
        document.All<Door>().Where(door => Doors.RoomsBeside(document, door) is var (one, other) && one is not null && other is not null
                                           && (one == garage || other == garage) && !Doors.Garage(document, one == garage ? other : one)).ToList();

    /// <summary>
    /// Whether an outside door is a house's main door: the one its approach path comes to; with none, one whose type is named front or entrance;
    /// with none of those either, one into a hall, an entrance or a porch.
    /// </summary>
    private static bool Main(BimDocument document, Door door, Room? inside)
    {
        if (inside is null || Dwellings.Of(inside).Length > 0 && FireDesign.UseOf(document) == BuildingUse.Flats) return false;
        var outside = document.All<Door>().Where(each => document.FindType<DoorType>(each.TypeId) is { } type && Doors.External(document, each, type)).ToList();
        var approached = outside.Where(each => Approaches.IsApproachedEntrance(document, each)).ToList();
        if (approached.Count > 0) return approached.Contains(door);
        bool Named(Door each) => document.FindType<DoorType>(each.TypeId)?.Name is { } name && (name.Contains("front", StringComparison.OrdinalIgnoreCase) || name.Contains("entrance", StringComparison.OrdinalIgnoreCase));
        if (outside.Any(Named)) return Named(door);
        return new[] { "hall", "entrance", "porch" }.Any(word => inside.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a window stands beside a door, in its wall or one in line with it, within 1 m of its frame - its glass showing callers.</summary>
    private static bool WindowBeside(BimDocument document, Door door, DoorType type, Wall wall)
    {
        var structure = document.GetWallType(wall)!.Structure;
        var at = wall.PointAt(structure, door.DistanceAlongWall, 0);
        var half = door.WidthOf(type) / 2;
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        foreach (var window in document.All<Window>())
        {
            if (document.Find<Wall>(window.HostWallId) is not { } host || host.LevelId != wall.LevelId || document.FindType<WindowType>(window.TypeId) is not { } windowType) continue;
            var hostWay = (host.End - host.Start).NormalisedOrDefault(Vector2D.UnitX);
            if (Math.Abs(way.Cross(hostWay)) > 0.02 || Math.Abs(way.Cross(host.Start - wall.Start)) > 50) continue;
            var middle = host.PointAt(document.GetWallType(host)!.Structure, window.DistanceAlongWall, 0);
            var apart = Math.Abs((middle - at).Dot(way)) - half - window.WidthOf(windowType) / 2;
            if (apart <= Beside + 0.5) return true;
        }

        return false;
    }

    /// <summary>What a window in an outside wall falls short of in Approved Document Q: easily accessible, not a secure window.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Window window, WindowType type)
    {
        if (!Applies(document) || Secure(type.Security, door: false) || document.Find<Wall>(window.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) yield break;
        var reach = wallType.Structure.TotalWidth / 2 + 300;
        var sides = new[] { reach, -reach }.Select(d => wall.PointAt(wallType.Structure, window.DistanceAlongWall, d)).ToArray();
        var rooms = sides.Select(point => RoomsOf.Containing(document, wall.LevelId, point)).ToArray();
        if ((rooms[0] is null) == (rooms[1] is null)) yield break;
        var outside = rooms[0] is null ? sides[0] : sides[1];

        var floor = document.FindLevel(window.LevelId)?.Elevation ?? 0;
        var (sill, height) = window.Placed(document, type, wall);
        var (bottom, top) = (floor + sill, floor + sill + height);
        var ground = GroundAt(document, outside);
        var what = Gap(bottom, top, ground) <= Reach + 0.5 ? "the ground"
            : LowRoofAt(document, outside, ground) is { } roof && Gap(bottom, top, roof) <= Reach + 0.5 ? "a low roof outside it"
            : null;
        if (what is null) yield break;
        yield return ($"it is easily accessible - within 2 m of {what}{(type.Security.Length > 0 ? $" - its security '{type.Security}'" : " - no security standard given")}.",
            "Approved Document Q (2.1, 2.2) asks a ground floor, basement or other easily accessible window to be a secure window - PAS 24, or STS 204, LPS 1175 SR1 or LPS 2081 A: its type's Security.");
    }

    /// <summary>What a roof's rooflights fall short of in Approved Document Q: easily accessible - in a roof pitched under 30° within 3.5 m of the ground, or within 2 m of the ground - and not secure.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Roof roof)
    {
        if (!Applies(document)) yield break;
        foreach (var window in RoofWindows.On(document, roof))
        {
            if (document.FindType<RoofWindowType>(window.TypeId) is not { } type || Secure(type.Security, door: false) || RoofWindows.Frame(document, window) is not { } frame) continue;
            var ground = GroundAt(document, frame.Centre);
            var (low, high) = (frame.Corners.Min(frame.Top), frame.Corners.Max(frame.Top));
            var reachable = Gap(low, high, ground) <= Reach + 0.5 || LowRoofAt(document, frame.Centre, ground) is not null;
            if (reachable)
                yield return ($"its rooflight, the {type.Name}, is easily accessible - in a roof under 30° within 3.5 m of the ground{(type.Security.Length > 0 ? $" - its security '{type.Security}'" : " - no security standard given")}.",
                    "Approved Document Q (2.1, 2.2, Appendix A) asks an easily accessible rooflight to be a secure window - PAS 24, or STS 204, LPS 1175 SR1 or LPS 2081 A: its type's Security.");
        }
    }
}
