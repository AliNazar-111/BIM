using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Split levels (round 143): a house whose storeys are half a storey apart - a side split, a back split, a tri-level - its parts on levels
/// 1.2 m or 1.4 m apart, joined by short flights.
///
/// <b>Walls standing through a level.</b> A wall on a lower level that stands on up through a level - the wall between a split's halves, from
/// the lower level's floor past the upper ones - is that level's as well: it bounds its rooms and is cut on its plan. One that starts below the
/// level's floor and rises 2 m over it at least, where no wall of the level's own stands along it.
///
/// <b>Smoke alarms</b> (IRC R314.3 item 3): with split levels and no door between adjacent levels, a smoke alarm on the upper level does for
/// the adjacent lower level where the lower is less than one full storey below - here where the lower level's walls, at their middling height,
/// rise past the upper level's floor, a flight joining them with no door in line with its foot or head.
///
/// <b>Basements</b> (IRC R202): a storey not a storey above grade plane - its floor below the grade plane, the average of the ground at the
/// outside walls, and the floor next above it 6 ft (1829) or less over it. The grade plane is the modelled ground's, read 300 out from each
/// outside wall; with no ground modelled, the ground floor's.
/// </summary>
public static class SplitLevels
{
    /// <summary>How far over a level a wall from below rises to stand on it; R202's 6 ft; how far out from a wall its ground is read, mm.</summary>
    public const double Through = 2000, SixFeet = 1829, Out = 300;

    /// <summary>
    /// A door between the levels a stair joins: one ahead of its foot or beyond its head, in line with it - no more than half its width and
    /// 300 to the side, from 200 back to 1100 on, a landing's depth and the door's swing - mm.
    /// </summary>
    public const double DoorAhead = 1100, DoorBack = 200, DoorAside = 300;

    private static bool Along(Wall wall, Wall other)
    {
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        if (Math.Abs(way.Cross((other.End - other.Start).NormalisedOrDefault(way))) > 0.02 || Math.Abs(way.Cross(other.Start.MidpointTo(other.End) - wall.Start)) > 50) return false;
        double At(Point2D point) => (point - wall.Start).Dot(way);
        var (from, to) = (Math.Min(At(other.Start), At(other.End)), Math.Max(At(other.Start), At(other.End)));
        return Math.Min(wall.Length, to) - Math.Max(0, from) > 100;
    }

    /// <summary>Whether a wall from a level below stands up through a level - from below its floor to 2 m over it - with no wall of its own along it.</summary>
    public static bool StandsThrough(BimDocument document, Wall wall, Guid levelId)
    {
        if (wall.LevelId == levelId || document.FindLevel(levelId) is not { } level) return false;
        if (wall.GetBaseElevation(document) > level.Elevation - 1 || wall.GetTopElevation(document) < level.Elevation + Through) return false;
        return !document.Walls.Any(own => own.LevelId == levelId && Along(own, wall));
    }

    /// <summary>The walls from levels below standing up through a level.</summary>
    public static IReadOnlyList<Wall> StandingThrough(BimDocument document, Guid levelId)
    {
        IReadOnlyList<Wall> Work() => document.Walls.Where(wall => StandsThrough(document, wall, levelId)).ToList();
        return ModelCache.Of(document) is { } cache ? cache.Keep("walls standing through", document, levelId, null, Work) : Work();
    }

    /// <summary>
    /// Whether a lower level is less than one full storey below the level above it: its walls, at their middling height, rising past the upper
    /// level's floor - as a split's lower half's walls do, up to the floor of the half over it.
    /// </summary>
    public static bool LessThanAStorey(BimDocument document, Level lower, Level upper)
    {
        var tops = document.Walls.Where(wall => wall.LevelId == lower.Id).Select(wall => wall.GetTopElevation(document)).OrderBy(top => top).ToList();
        if (tops.Count == 0) return upper.Elevation - lower.Elevation < SixFeet;
        return tops[tops.Count / 2] > upper.Elevation + 300;
    }

    /// <summary>
    /// The level whose smoke alarm does for this one (IRC R314.3 item 3): the next above, less than a full storey up, joined to it by a flight
    /// with no door at its foot or head; null where there is none.
    /// </summary>
    public static Level? CoveredFrom(BimDocument document, Guid levelId)
    {
        if (document.FindLevel(levelId) is not { } level || Levels.Above(document, levelId) is not { } above || !LessThanAStorey(document, level, above)) return null;
        var doors = document.All<Door>().Select(door => (Door: door, Wall: document.Find<Wall>(door.HostWallId))).Where(entry => entry.Wall is not null)
            .Select(entry => (Level: entry.Wall!.LevelId, At: entry.Wall.LocationCurve.PointAt(entry.Door.DistanceAlongWall))).ToList();
        foreach (var stair in document.All<Stair>().Where(stair => stair.LevelId == level.Id && stair.TopLevelId == above.Id))
        {
            StairLayout layout;
            try { layout = Stairs.Layout(document, stair); }
            catch (Exception) { continue; }
            if (layout.WalkingLine is not { Count: >= 2 } walk) continue;
            // A door in line with the flight off its foot, going down, or off its head, going up.
            bool InLine(Point2D door, Point2D end, Vector2D outward)
            {
                var (on, aside) = ((door - end).Dot(outward), Math.Abs(outward.Cross(door - end)));
                return on >= -DoorBack && on <= DoorAhead && aside <= stair.Width / 2 + DoorAside;
            }

            var (foot, head) = (walk[0], walk[^1]);
            var (down, up) = ((walk[0] - walk[1]).NormalisedOrDefault(Vector2D.UnitX), (walk[^1] - walk[^2]).NormalisedOrDefault(Vector2D.UnitX));
            if (!doors.Any(door => door.Level == level.Id && InLine(door.At, foot, down) || door.Level == above.Id && InLine(door.At, head, up)))
                return above;
        }

        return null;
    }

    /// <summary>The grade plane (IRC R202): the average of the modelled ground 300 out from each outside wall; with none modelled, the ground floor's level.</summary>
    public static double GradePlane(BimDocument document)
    {
        double Work()
        {
            var grounds = new List<double>();
            foreach (var wall in document.Walls.Where(wall => document.GetWallType(wall) is { Function: WallFunction.Exterior } && wall.Length > 1))
            {
                var half = (document.GetWallType(wall)?.Structure.TotalWidth ?? 0) / 2;
                var at = wall.Start.MidpointTo(wall.End) + SpaceSeparation.Outward(document, wall) * (half + Out);
                if (Terrains.GroundAt(document, at) is { } ground) grounds.Add(ground);
            }

            return grounds.Count > 0 ? grounds.Average() : Escape.Ground(document);
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("grade plane", document, null, null, Work) : Work();
    }

    /// <summary>Whether a level is a basement (IRC R202): its floor below the grade plane, the next level up 6 ft or less over the grade plane.</summary>
    public static bool IsBasement(BimDocument document, Guid levelId)
    {
        if (document.FindLevel(levelId) is not { } level) return false;
        var grade = GradePlane(document);
        if (level.Elevation >= grade - 1) return false;
        return Levels.Above(document, levelId) is not { } above || above.Elevation - grade <= SixFeet + 0.5;
    }
}
