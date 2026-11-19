using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Lifts in a building other than a dwelling (round 107), as Approved Document M Volume 2 sets them out (3.17 to 3.43, Diagram 11): a lift is
/// its car, placed once in its shaft; it serves each storey where a landing door stands by its car - a door in the shaft's wall.
///
/// A new building with rooms on two storeys or more has a passenger lift serving them all (3.24a) - a lifting platform only where one cannot be
/// accommodated (3.24b, 3.22). A passenger lift's car 1100 wide and 1400 deep at least (3.34c), its doors power-operated and sliding, 800 clear
/// (3.34e). A lifting platform's 800 by 1250 unenclosed, travelling 2 m at most; 900 by 1400 in an enclosure, travelling further; 1100 by 1400
/// with doors at right angles (3.43b, g); its doors 800 clear, 900 for the largest (3.43h). In front of each landing door 1500 by 1500 clear
/// (3.28a) - of walls, fittings and doors' swings.
///
/// Not done: a straight route 900 wide in place of the 1500 square (3.28a); call buttons and car controls (3.28b to d, 3.34g, h, 3.43d to f);
/// mirrors (3.34d), handrails (3.28f), timing, signs, announcements and glass (3.34f, i to k); evacuation lifts (3.34l); wheelchair platform
/// stairlifts (3.44 to 3.49); a block of flats' lifts (Approved Document M Volume 1).
/// </summary>
public static class Lifts
{
    /// <summary>A passenger lift's least car, its door's least clear width, the space before it, and the most a platform travels unenclosed, mm.</summary>
    public const double CarWidth = 1100, CarDepth = 1400, DoorClear = 800, Landing = 1500, OpenTravel = 2000;

    /// <summary>A lift as it stands: its car's outline, whether it is a lifting platform, and its landing doors with their storeys.</summary>
    public sealed record Placed(Component Piece, ComponentType Type, IReadOnlyList<Point2D> Car, bool Platform, IReadOnlyList<(Door Door, Level Level)> Landings);

    private static double Distance(IReadOnlyList<Point2D> outline, Point2D point) =>
        Polygon2D.Contains(outline, point) ? 0 : Enumerable.Range(0, outline.Count).Min(i => Line2D.DistanceFromSegment(point, outline[i], outline[(i + 1) % outline.Count]));

    /// <summary>The model's lifts, each with the doors in a wall within 700 of its car - its shaft's - on any storey, kept while the model does not change.</summary>
    public static IReadOnlyList<Placed> Of(BimDocument document)
    {
        IReadOnlyList<Placed> Work()
        {
            var lifts = new List<Placed>();
            foreach (var piece in document.All<Component>())
            {
                if (document.FindType<ComponentType>(piece.TypeId) is not { Form: ComponentForm.Lift or ComponentForm.LiftingPlatform } type) continue;
                var car = ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0));
                var landings = new List<(Door, Level)>();
                foreach (var door in document.All<Door>())
                    if (document.Find<Wall>(door.HostWallId) is { } wall && document.GetWallType(wall) is { } wallType && document.FindLevel(wall.LevelId) is { } level
                        && Distance(car, wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0)) <= 700)
                        landings.Add((door, level));
                lifts.Add(new Placed(piece, type, car, type.Form == ComponentForm.LiftingPlatform, landings.OrderBy(each => each.Item2.Elevation).ToList()));
            }

            return lifts;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("lifts", document, null, null, Work) : Work();
    }

    /// <summary>Whether a door is a lift's landing door - 3.34e's, not Table 2's.</summary>
    public static bool IsLanding(BimDocument document, Door door) => Of(document).Any(lift => lift.Landings.Any(landing => landing.Door == door));

    /// <summary>
    /// The storeys people use: those with an enclosed room on them other than a lift shaft, a stair, a plant room or a roof space, lowest first.
    /// </summary>
    public static IReadOnlyList<Level> Storeys(BimDocument document)
    {
        var cars = Of(document).Select(lift => lift.Car).ToList();
        bool Used(Room room) =>
            !new[] { "lift", "shaft", "stair", "landing", "plant", "riser", "roof", "loft", "void" }.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            && !cars.Any(car => Polygon2D.Contains(car, room.Location))
            && room.GetBoundary(document) is { IsEnclosed: true };
        var used = document.All<Room>().Where(Used).Select(room => room.LevelId).ToHashSet();
        return document.Levels.Where(level => used.Contains(level.Id)).OrderBy(level => level.Elevation).ToList();
    }

    /// <summary>
    /// A building other than a dwelling with rooms on two storeys or more and no lift at all, said on the first room of its second storey
    /// (3.24a).
    /// </summary>
    public static IEnumerable<EscapeProblem> Missing(BimDocument document)
    {
        if (!Circulation.Applies(document) || Of(document).Count > 0) yield break;
        var storeys = Storeys(document);
        if (storeys.Count < 2) yield break;
        var first = document.All<Room>().Where(room => room.LevelId == storeys[1].Id && room.GetBoundary(document) is { IsEnclosed: true })
            .OrderBy(room => room.Number, StringComparer.Ordinal).ThenBy(room => room.Name, StringComparer.Ordinal).FirstOrDefault();
        if (first is null) yield break;
        yield return new EscapeProblem(first,
            $"its building has rooms on {storeys.Count} storeys and no passenger lift - Approved Document M Volume 2 (3.24a) asks a new building other than a dwelling for a passenger lift serving every storey, a lifting platform only where one cannot be accommodated (3.24b), and stairs as well (3.19): place a lift's car in its shaft and a landing door by it on each storey.");
    }

    /// <summary>What a lift falls short of: serving every storey, its car, its landing doors, and the space in front of each.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Component piece, ClearanceStandard standard)
    {
        if (!Circulation.Applies(document) || Of(document).FirstOrDefault(lift => lift.Piece == piece) is not { } lift) return Array.Empty<PlacementProblem>();
        var problems = new List<PlacementProblem>();
        void Add(string said, string rule, IReadOnlyList<Point2D>? outline = null) => problems.Add(new PlacementProblem(piece, said, rule, outline ?? lift.Car));
        var what = lift.Platform ? "lifting platform" : "lift";

        // The storeys no lift stops at: said on the first lift.
        var lifts = Of(document);
        var unserved = Storeys(document).Where(level => !lifts.Any(each => each.Landings.Any(landing => landing.Level.Id == level.Id))).ToList();
        if (lifts[0].Piece == piece && Storeys(document).Count >= 2 && unserved.Count > 0)
            Add($"no lift stops on the {string.Join(", the ", unserved.Select(level => level.Name))} - no landing door by a lift there.",
                "Approved Document M Volume 2 (3.24a) asks a new building other than a dwelling for a passenger lift serving all its storeys: put a landing door in the shaft's wall by the car on each.");
        if (lift.Landings.Count == 0)
        {
            Add($"it has no landing door - none in a wall within 700 of its {(lift.Platform ? "platform" : "car")}.",
                $"Approved Document M Volume 2 ({(lift.Platform ? "3.43j" : "3.34b")}) asks a {what} to be reached from the rest of each storey it serves: a landing door in its shaft's wall by it.");
            return problems;
        }

        // Its size and its doors.
        var (wide, deep) = (lift.Type.Width, lift.Type.Depth);
        if (lift.Platform)
        {
            Add($"it is a lifting platform where a passenger lift is asked.",
                "Approved Document M Volume 2 (3.24a, b, 3.22) asks a new building for a passenger lift, a lifting platform only where site constraints leave no room for one - say why in the access statement.");
            var travel = lift.Landings.Max(landing => landing.Level.Elevation) - lift.Landings.Min(landing => landing.Level.Elevation);
            var turned = lift.Landings.Any(one => lift.Landings.Any(other => Turned(document, one.Door, other.Door)));
            var (askedWide, askedDeep, why) = turned ? (1100.0, 1400.0, "its doors at right angles to each other (iii)")
                : travel > OpenTravel + 0.5 ? (900.0, 1400.0, $"travelling {travel:0}, more than 2 m, in an enclosure (b ii, g ii)")
                : (800.0, 1250.0, "unenclosed, for a wheelchair user alone (g i)");
            if (wide < askedWide - 0.5 || deep < askedDeep - 0.5)
                Add($"its platform is {wide:0} by {deep:0}.", $"Approved Document M Volume 2 (3.43g) asks a lifting platform {askedWide:0} wide and {askedDeep:0} deep at least, {why}.");
        }
        else if (wide < CarWidth - 0.5 || deep < CarDepth - 0.5)
            Add($"its car is {wide:0} wide and {deep:0} deep.", $"Approved Document M Volume 2 (3.34c, Diagram 11) asks a passenger lift's car {CarWidth:0} wide and {CarDepth:0} deep at least - 2000 by 1400 for a wheelchair user to turn round in it (3.29).");

        var doorAsked = lift.Platform && wide >= 1100 - 0.5 && deep >= 1400 - 0.5 ? 900.0 : DoorClear;
        foreach (var (door, level) in lift.Landings)
        {
            if (document.FindType<DoorType>(door.TypeId) is not { } doorType) continue;
            var clear = Doors.ClearWidth(doorType);
            if (clear < doorAsked - 0.5)
                Add($"its landing door on the {level.Name} gives {clear:0} clear.", lift.Platform
                    ? $"Approved Document M Volume 2 (3.43h) asks a lifting platform's doors {doorAsked:0} clear at least."
                    : $"Approved Document M Volume 2 (3.34e) asks a passenger lift's doors {DoorClear:0} clear at least, power-operated and sliding.");
            if (!lift.Platform && doorType.Operation != DoorOperation.Sliding)
                Add($"its landing door on the {level.Name} is a {doorType.Operation.ToString().ToLowerInvariant()} door.",
                    "Approved Document M Volume 2 (3.34e) asks a passenger lift's doors to be power-operated horizontal sliding doors.");
            if (Space(document, lift, door) is { } space && space.In.Count > 0)
                Add($"the 1500 x 1500 in front of its landing door on the {level.Name} has {string.Join(", ", space.In)} in it.",
                    $"Approved Document M Volume 2 (3.28a) asks an unobstructed space of {Landing:0} by {Landing:0} in front of a {what}'s landing door - or a straight route 900 wide to it - clear of walls, fittings and doors' swings.", space.Outline);
        }

        return problems;
    }

    /// <summary>Whether two landing doors are in walls at right angles to each other.</summary>
    private static bool Turned(BimDocument document, Door one, Door other)
    {
        Vector2D? Way(Door door) => document.Find<Wall>(door.HostWallId) is { } wall ? (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX) : null;
        return one != other && Way(one) is { } a && Way(other) is { } b && Math.Abs(a.Dot(b)) < 0.2;
    }

    /// <summary>
    /// The 1500 by 1500 in front of a landing door, on the side away from the car, and what is in it: the landing ending - walls - the
    /// fittings standing in it, other doors' swings; sampled every 100.
    /// </summary>
    private static (IReadOnlyList<Point2D> Outline, List<string> In)? Space(BimDocument document, Placed lift, Door door)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return null;
        var structure = wallType.Structure;
        var middle = wall.PointAt(structure, door.DistanceAlongWall, 0);
        var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var out1 = along.PerpendicularLeft();
        var centre = new Point2D(lift.Car.Average(point => point.X), lift.Car.Average(point => point.Y));
        if (out1.Dot(middle - centre) < 0) out1 = -out1;
        var half = structure.TotalWidth / 2;
        var near = middle + out1 * half;
        var outline = new[] { near - along * (Landing / 2), near + along * (Landing / 2), near + along * (Landing / 2) + out1 * Landing, near - along * (Landing / 2) + out1 * Landing };

        var room = RoomsOf.Containing(document, wall.LevelId, near + out1 * 150);
        var shape = room?.GetBoundary(document) is { IsEnclosed: true } boundary ? boundary.Polygon : null;
        var pieces = document.All<Component>().Where(other => other.LevelId == wall.LevelId && other != lift.Piece)
            .Select(other => (Name: document.FindType<ComponentType>(other.TypeId)?.Name ?? "a fitting", Shape: document.FindType<ComponentType>(other.TypeId) is { } kind ? ComponentModel.Footprint(kind, ComponentModel.FrameOf(other, 0)) : null))
            .Where(entry => entry.Shape is not null).ToList();
        var swings = document.All<Door>().Where(other => other != door && document.Find<Wall>(other.HostWallId)?.LevelId == wall.LevelId)
            .SelectMany(other => DoorSwing.Sweeps(document, other).Select(sweep => (Name: $"{document.FindType<DoorType>(other.TypeId)?.Name ?? "a door"}'s swing", Shape: sweep))).ToList();

        var found = new List<string>();
        for (var a = 50.0; a < Landing; a += 100)
        for (var b = 25.0; b < Landing; b += 100)
        {
            var point = near - along * (Landing / 2) + along * a + out1 * b;
            if (shape is null || !Polygon2D.Contains(shape, point))
            {
                if (!found.Contains("a wall")) found.Add("a wall");
                continue;
            }

            foreach (var (name, piece) in pieces)
                if (Polygon2D.Contains(piece!, point) && !found.Contains(name)) found.Add(name);
            foreach (var (name, sweep) in swings)
                if (sweep.Count >= 3 && Polygon2D.Contains(sweep, point) && !found.Contains(name)) found.Add(name);
        }

        return (outline, found);
    }

    /// <summary>Every lift's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Component>()
            .Where(piece => piece.LevelId == levelId)
            .SelectMany(piece => Problems(document, piece, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(piece, "its place", problem.Outline, problem.Rule), string.Empty,
                    document.FindType<ComponentType>(piece.TypeId)?.Name ?? "A lift", problem.Said)))
            .ToList();
}
