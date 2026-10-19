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
        if (document.Elements.OfType<PropertyLine>().FirstOrDefault(line => line.Boundary.Count >= 3) is not { } property) return null;
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
        foreach (var window in document.Elements.OfType<Window>().Where(window => window.HostWallId == wall.Id))
        {
            var type = document.FindType<WindowType>(window.TypeId);
            area += window.WidthOf(type) * window.HeightOf(type);
        }

        foreach (var door in document.Elements.OfType<Door>().Where(door => door.HostWallId == wall.Id))
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
        if (!document.Elements.OfType<PropertyLine>().Any(line => line.Boundary.Count >= 3)) return Array.Empty<Side>();
        return document.Walls
            .Where(wall => wall.Length > 1 && Outside(document, wall) && !wall.IsCurved)
            .GroupBy(wall => Facing(Outward(document, wall)))
            .Select(side => new Side(side.Key, side.ToList(),
                side.Min(wall => DistanceToBoundary(document, wall) ?? double.MaxValue),
                side.Sum(wall => Unprotected(document, wall))))
            .ToList();
    }

    /// <summary>What a wall falls short of in its side's space separation: said, and the rule.</summary>
    public static IReadOnlyList<(string Said, string Rule)> Problems(BimDocument document, Wall wall, ClearanceStandard standard)
    {
        if (standard.IsUs() || !Outside(document, wall) || wall.IsCurved) return Array.Empty<(string, string)>();
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

        if (own > 0 && Allowed(side.Distance / 1000) is { } allowed && side.Unprotected > allowed + 0.005)
            problems.Add(($"its side, facing {side.Facing} {side.Distance / 1000:0.0} m from the property line, has {side.Unprotected:0.0} m² of windows, glazed doors and glass.",
                $"Approved Document B (11.18, Diagram 11.7) allows a house's side {allowed:0.#} m² of unprotected area there - smaller windows, fire-resisting glazing, or the house further from the boundary."));
        return problems;
    }

    /// <summary>How many of a wall's openings are bigger than an area, m².</summary>
    private static int OpeningsOver(BimDocument document, Wall wall, double square)
    {
        var count = 0;
        foreach (var opening in document.Elements.OfType<Opening>().Where(opening => opening.HostWallId == wall.Id))
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

    /// <summary>Each side's space separation, for the property line's Properties.</summary>
    public static string Summary(BimDocument document)
    {
        var sides = Sides(document);
        if (sides.Count == 0) return "No outside walls to measure";
        return string.Join("; ", sides.OrderBy(side => side.Facing).Select(side =>
        {
            var metres = side.Distance / 1000;
            var allowed = Allowed(metres);
            var limit = metres < 1 ? "only small openings" : allowed is { } most ? $"{most:0.#} m² allowed" : "no limit";
            return $"{char.ToUpperInvariant(side.Facing[0])}{side.Facing[1..]} {metres:0.0} m: {side.Unprotected:0.0} m² open, {limit}";
        }));
    }
}
