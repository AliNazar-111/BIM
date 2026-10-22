using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How far a house's sides are from the property line, and how much of each may be open to it -
/// Approved Document B's space separation (Volume 1, section 11), so a fire cannot spread across
/// the boundary to the house next door. The windows and doors in a side, and a curtain wall's
/// glass, are its unprotected area; Method 1 (11.17, 11.18, Diagram 11.7) allows a side 5.6 m² of
/// it at 1 m from the boundary, 12 at 2 m, 18 at 3, 24 at 4, 30 at 5 and any at 6 m or more. A
/// wall less than 1 m from the boundary may have only the small openings Diagram 11.5 lets be
/// disregarded - none bigger than 1 m² - and is to be fire resisting from both sides (11.8).
/// </summary>
public static class SpaceSeparation
{
    /// <summary>One side of the building: the way it faces, its outside walls, how near the boundary it comes, and its unprotected area.</summary>
    public sealed record Side(string Facing, IReadOnlyList<Wall> Walls, double Distance, double Unprotected);

    /// <summary>The most unprotected area Diagram 11.7 allows a side at a distance from the boundary, m²; null for no limit at 6 m or more.</summary>
    public static double? Allowed(double metres) =>
        metres >= 6 ? null : metres < 1 ? 0 : new[] { 5.6, 12, 18, 24, 30 }[(int)Math.Floor(metres) - 1];

    /// <summary>
    /// As <see cref="Allowed(double)"/>, and for a building sprinklered throughout the more of the
    /// two 11.21 allows: double the area, or the area allowed twice as far from the boundary - a
    /// metre from it at the least.
    /// </summary>
    public static double? Allowed(double metres, bool sprinklered)
    {
        if (!sprinklered || metres < 1) return Allowed(metres);
        var (doubled, halved) = (Allowed(metres) * 2, Allowed(metres * 2));
        return doubled is null || halved is null ? null : Math.Max(doubled.Value, halved.Value);
    }

    /// <summary>The way a wall faces, as a compass point: the project's north up the page.</summary>
    private static string Facing(Vector2D outward)
    {
        var degrees = (Math.Atan2(outward.Y, outward.X) * 180 / Math.PI + 360) % 360;
        return degrees switch
        {
            >= 45 and < 135 => "north",
            >= 135 and < 225 => "west",
            >= 225 and < 315 => "south",
            _ => "east"
        };
    }

    /// <summary>How near a wall's outside face comes to the property line, mm; null with no property line.</summary>
    public static double? DistanceToBoundary(BimDocument document, Wall wall)
    {
        if (document.All<PropertyLine>().FirstOrDefault(line => line.Boundary.Count >= 3) is not { } property) return null;
        if (document.GetWallType(wall) is not { } type) return null;
        var half = type.Structure.TotalWidth / 2;
        var outward = Outward(document, wall);
        var boundary = property.Boundary;
        var nearest = double.MaxValue;
        for (var s = 0; s <= 8; s++)
        {
            var face = wall.PointAt(type.Structure, wall.Length * s / 8, 0) + outward * half;
            for (var i = 0; i < boundary.Count; i++)
                nearest = Math.Min(nearest, Line2D.DistanceFromSegment(face, boundary[i], boundary[(i + 1) % boundary.Count]));
        }

        return nearest;
    }

    /// <summary>
    /// The way out of the building from a wall: away from the middle of its outside walls - whichever
    /// way round the wall was drawn.
    /// </summary>
    public static Vector2D Outward(BimDocument document, Wall wall)
    {
        var outside = document.Walls.Where(other => other.LevelId == wall.LevelId && other.Length > 1 && Outside(document, other)).ToList();
        var middle = outside.Count == 0 ? wall.Start.MidpointTo(wall.End)
            : new Point2D(outside.Average(other => (other.Start.X + other.End.X) / 2), outside.Average(other => (other.Start.Y + other.End.Y) / 2));
        var normal = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        return normal.Dot(wall.Start.MidpointTo(wall.End) - middle) >= 0 ? normal : normal * -1;
    }

    /// <summary>A wall's unprotected area, m²: its windows and its doors that are not fire doors - all of a curtain wall.</summary>
    public static double Unprotected(BimDocument document, Wall wall)
    {
        if (document.IsCurtainWall(wall)) return wall.Length * wall.GetHeight(document) / 1e6;
        var area = 0.0;
        foreach (var window in document.All<Window>().Where(window => window.HostWallId == wall.Id))
        {
            var type = document.FindType<WindowType>(window.TypeId);
            area += window.WidthOf(type) * window.HeightOf(type);
        }

        foreach (var door in document.All<Door>().Where(door => door.HostWallId == wall.Id))
        {
            var type = document.FindType<DoorType>(door.TypeId);
            if (Doors.FireMinutes(type?.FireRating ?? string.Empty) >= 30) continue;
            area += door.WidthOf(type) * door.HeightOf(type);
        }

        return area / 1e6;
    }

    /// <summary>Whether a wall is an outside wall of the building: an exterior wall's type, or a curtain wall.</summary>
    private static bool Outside(BimDocument document, Wall wall) =>
        document.IsCurtainWall(wall) || document.GetWallType(wall) is { Function: WallFunction.Exterior };

    /// <summary>The building's sides as they face the property line - none where there is no property line.</summary>
    public static IReadOnlyList<Side> Sides(BimDocument document)
    {
        if (!document.All<PropertyLine>().Any(line => line.Boundary.Count >= 3)) return Array.Empty<Side>();
        return document.Walls
            .Where(wall => wall.Length > 1 && Outside(document, wall) && !wall.IsCurved)
            .GroupBy(wall => Facing(Outward(document, wall)))
            .Select(side => new Side(side.Key, side.ToList(),
                side.Min(wall => DistanceToBoundary(document, wall) ?? double.MaxValue),
                side.Sum(wall => Unprotected(document, wall))))
            .ToList();
    }

    /// <summary>What a wall falls short of in its side's space separation - or, in the US, its fire separation: said, and the rule.</summary>
    public static IReadOnlyList<(string Said, string Rule)> Problems(BimDocument document, Wall wall, ClearanceStandard standard)
    {
        if (!Outside(document, wall) || wall.IsCurved) return Array.Empty<(string, string)>();
        if (standard.IsUs()) return IrcProblems(document, wall);
        if (Sides(document).FirstOrDefault(candidate => candidate.Walls.Contains(wall)) is not { } side) return Array.Empty<(string, string)>();

        var problems = new List<(string, string)>();
        var own = Unprotected(document, wall);
        var distance = DistanceToBoundary(document, wall) ?? double.MaxValue;

        if (distance < 1000)
        {
            if (document.GetWallType(wall) is { } type && !document.IsCurtainWall(wall) && Doors.FireMinutes(type.FireRating) < 30)
                problems.Add(($"it is {distance / 1000:0.00} m from the property line, {(Doors.FireMinutes(type.FireRating) is > 0 and var minutes ? $"rated {minutes} minutes" : "not fire-rated")}.",
                    "Approved Document B (11.8, Table B4) asks a wall less than 1 m from the boundary to resist fire for 30 minutes from both sides."));
            var big = OpeningsOver(document, wall, 1.0);
            if (big > 0 || document.IsCurtainWall(wall))
                problems.Add(($"it is {distance / 1000:0.00} m from the property line, with {(document.IsCurtainWall(wall) ? "its glass" : $"{big} opening{(big == 1 ? "" : "s")} over 1 m²")} in it.",
                    "Approved Document B (11.8, Diagram 11.5) allows a wall less than 1 m from the boundary only small openings - 1 m² at most, 4 m apart."));
            return problems;
        }

        var sprinklered = document.ProjectInformation.Sprinklered;
        if (own > 0 && Allowed(side.Distance / 1000, sprinklered) is { } allowed && side.Unprotected > allowed + 0.005)
            problems.Add(($"its side, facing {side.Facing} {side.Distance / 1000:0.0} m from the property line, has {side.Unprotected:0.0} m² of windows, glazed doors and glass.",
                sprinklered
                    ? $"Approved Document B (11.18, Diagram 11.7, and 11.21 for a building sprinklered throughout) allows a house's side {allowed:0.#} m² of unprotected area there - smaller windows, fire-resisting glazing, or the house further from the boundary."
                    : $"Approved Document B (11.18, Diagram 11.7) allows a house's side {allowed:0.#} m² of unprotected area there - smaller windows, fire-resisting glazing, sprinklers throughout (11.21), or the house further from the boundary."));
        return problems;
    }

    /// <summary>
    /// How far a wall's outside face is from the lot line as the IRC measures its fire separation
    /// distance, mm: out from the face to the line in front of it - not to one it runs square to,
    /// which R302.1 leaves out (exception 1). Null with no property line, or none in front of it.
    /// </summary>
    public static double? FireSeparationDistance(BimDocument document, Wall wall)
    {
        if (document.All<PropertyLine>().FirstOrDefault(line => line.Boundary.Count >= 3) is not { } property) return null;
        if (document.GetWallType(wall) is not { } type) return null;
        var half = type.Structure.TotalWidth / 2;
        var outward = Outward(document, wall);
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var boundary = property.Boundary;
        var nearest = double.MaxValue;
        for (var i = 0; i < boundary.Count; i++)
        {
            var (a, b) = (boundary[i], boundary[(i + 1) % boundary.Count]);
            if (Math.Abs(way.Dot((b - a).NormalisedOrDefault(Vector2D.UnitX))) < 0.17) continue;
            for (var s = 0; s <= 8; s++)
            {
                var face = wall.PointAt(type.Structure, wall.Length * s / 8, 0) + outward * half;
                var on = Line2D.ClosestPointOnSegment(face, a, b);
                if ((on - face).Dot(outward) > 0) nearest = Math.Min(nearest, face.DistanceTo(on));
            }
        }

        return nearest < double.MaxValue ? nearest : null;
    }

    /// <summary>The IRC's lines for a wall near the lot line: 5 ft - 3 ft sprinklered - inside which it is rated an hour; 3 ft inside which it has no openings.</summary>
    public const double RatedWithin = 1524, RatedWithinSprinklered = 914, NoOpeningsWithin = 914;

    /// <summary>
    /// A US house's wall near its lot line, as IRC R302.1 has it: within 5 ft, rated 1 hour with
    /// exposure from both sides; no openings within 3 ft, and between 3 and 5 ft no more than 25%
    /// of the wall (Table R302.1(1)). Sprinklered throughout, rated 1 hour from the outside within
    /// 3 ft, no openings there, and any number beyond (Table R302.1(2)).
    /// </summary>
    private static IReadOnlyList<(string Said, string Rule)> IrcProblems(BimDocument document, Wall wall)
    {
        if (FireSeparationDistance(document, wall) is not { } distance) return Array.Empty<(string, string)>();

        var problems = new List<(string, string)>();
        var sprinklered = document.ProjectInformation.Sprinklered;
        var feet = $"{distance / 1000:0.00} m ({distance / 304.8:0.0} ft)";
        var curtain = document.IsCurtainWall(wall);
        var minutes = curtain ? 0 : Doors.FireMinutes(document.GetWallType(wall)?.FireRating ?? string.Empty);

        if (distance < (sprinklered ? RatedWithinSprinklered : RatedWithin) && minutes < 60)
            problems.Add(($"it is {feet} from the lot line, {(minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated")}.",
                sprinklered
                    ? "The IRC (R302.1, Table R302.1(2)) asks a wall of a house sprinklered throughout less than 3 ft from the lot line to be rated 1 hour, tested with exposure from the outside."
                    : "The IRC (R302.1, Table R302.1(1)) asks a wall less than 5 ft from the lot line to be rated 1 hour, tested with exposure from both sides - or sprinklers throughout, which bring that to 3 ft (Table R302.1(2))."));

        var (count, open) = Openings(document, wall);
        var area = wall.Length * wall.GetHeight(document) / 1e6;
        var what = curtain ? "its glass" : $"{count} window{(count == 1 ? "" : "s")} and door{(count == 1 ? "" : "s")} in it";
        if (distance < NoOpeningsWithin && open > 0)
            problems.Add(($"it is {feet} from the lot line, with {what}.",
                $"The IRC ({(sprinklered ? "Table R302.1(2)" : "Table R302.1(1)")}) allows no openings in a wall less than 3 ft from the lot line."));
        else if (!sprinklered && distance < RatedWithin && area > 0 && open > area / 4 + 0.005)
            problems.Add(($"it is {feet} from the lot line, its openings {open:0.0} m² of its {area:0.0} m² - {open / area:0%}.",
                "The IRC (Table R302.1(1)) allows a wall 3 to 5 ft from the lot line openings of 25% of its area at most - or any, with sprinklers throughout (Table R302.1(2))."));
        return problems;
    }

    /// <summary>A wall's openings as the IRC counts them: every window and door in it, how many and how big, m² - all of a curtain wall.</summary>
    private static (int Count, double Area) Openings(BimDocument document, Wall wall)
    {
        if (document.IsCurtainWall(wall)) return (1, wall.Length * wall.GetHeight(document) / 1e6);
        var (count, area) = (0, 0.0);
        foreach (var opening in document.All<Opening>().Where(opening => opening.HostWallId == wall.Id))
        {
            OpeningType? type = opening switch
            {
                Window window => document.FindType<WindowType>(window.TypeId),
                Door door => document.FindType<DoorType>(door.TypeId),
                _ => null
            };
            if (opening is not (Window or Door)) continue;
            count++;
            area += opening.WidthOf(type) * opening.HeightOf(type) / 1e6;
        }

        return (count, area);
    }

    /// <summary>How many of a wall's openings are bigger than an area, m².</summary>
    private static int OpeningsOver(BimDocument document, Wall wall, double square)
    {
        var count = 0;
        foreach (var opening in document.All<Opening>().Where(opening => opening.HostWallId == wall.Id))
        {
            var type = opening switch
            {
                Window window => (OpeningType?)document.FindType<WindowType>(window.TypeId),
                Door door => document.FindType<DoorType>(door.TypeId),
                _ => null
            };
            if (opening is Door && type is DoorType doorType && Doors.FireMinutes(doorType.FireRating) >= 30) continue;
            if (opening.WidthOf(type) * opening.HeightOf(type) / 1e6 > square + 1e-6) count++;
        }

        return count;
    }

    /// <summary>Each side's space separation - or, in the US, its fire separation distance - for the property line's Properties.</summary>
    public static string Summary(BimDocument document)
    {
        var sides = Sides(document);
        if (sides.Count == 0) return "No outside walls to measure";
        var sprinklered = document.ProjectInformation.Sprinklered;
        if (document.ProjectInformation.ClearanceStandard.IsUs())
            return string.Join("; ", sides.OrderBy(side => side.Facing).Select(side =>
            {
                var distance = side.Walls.Select(wall => FireSeparationDistance(document, wall)).OfType<double>().DefaultIfEmpty(double.MaxValue).Min();
                if (distance == double.MaxValue) return $"{char.ToUpperInvariant(side.Facing[0])}{side.Facing[1..]}: faces no lot line";
                var limit = distance < NoOpeningsWithin ? "1-hour walls, no openings"
                    : sprinklered ? "no limit"
                    : distance < RatedWithin ? "1-hour walls, openings up to 25%" : "no limit";
                return $"{char.ToUpperInvariant(side.Facing[0])}{side.Facing[1..]} {distance / 304.8:0.0} ft: {limit}";
            }));

        return string.Join("; ", sides.OrderBy(side => side.Facing).Select(side =>
        {
            var metres = side.Distance / 1000;
            var allowed = Allowed(metres, sprinklered);
            var limit = metres < 1 ? "only small openings" : allowed is { } most ? $"{most:0.#} m² allowed" : "no limit";
            return $"{char.ToUpperInvariant(side.Facing[0])}{side.Facing[1..]} {metres:0.0} m: {side.Unprotected:0.0} m² open, {limit}";
        }));
    }
}
