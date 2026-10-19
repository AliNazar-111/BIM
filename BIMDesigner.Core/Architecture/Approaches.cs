using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The way up to a home's door - its approach - and the landing at the door, as Approved
/// Document M asks of them: a path 900 wide (1200 for a wheelchair user, M4(2) 2.9, M4(3) 3.9),
/// falling no more than 1 in 40 across it (1.6), a wheelchair user's with a level place 1500
/// square to pass or turn at every 10 m (3.9d); and at the entrance a level landing 1200 square,
/// covered 900 wide and 600 deep (2.20) - 1500 square, covered 1200 by 1200, for a wheelchair
/// user (3.22). The ADA: an accessible route 36 in wide (403.5.1), its cross slope 1:48 (403.3).
/// </summary>
public static class Approaches
{
    /// <summary>A gradient no steeper than this is level, as Approved Document M has it: 1 in 60.</summary>
    public const double Level = 1 / 60.0;

    /// <summary>How long a path is along its middle, mm.</summary>
    public static double Length(IReadOnlyList<Point2D> line) => line.Zip(line.Skip(1), (a, b) => a.DistanceTo(b)).Sum();

    /// <summary>The steepest the ground falls across a path, as a rise over a run, sampled every metre along its middle.</summary>
    public static double? CrossFall(BimDocument document, GroundRegion path)
    {
        if (path.PathLine.Count < 2) return null;
        double? steepest = null;
        for (var i = 0; i + 1 < path.PathLine.Count; i++)
        {
            var (a, b) = (path.PathLine[i], path.PathLine[i + 1]);
            var across = (b - a).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (path.PathWidth / 2);
            var steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / 1000));
            for (var s = 0; s <= steps; s++)
            {
                var at = a + (b - a) * (s / (double)steps);
                var fall = Math.Abs(GroundRegions.HeightAt(document, path, at + across) - GroundRegions.HeightAt(document, path, at - across)) / path.PathWidth;
                if (steepest is not { } known || fall > known) steepest = fall;
            }
        }

        return steepest;
    }

    /// <summary>What an approach path falls short of in its width, its cross-fall and its passing places.</summary>
    public static IReadOnlyList<string> PathProblems(BimDocument document, GroundRegion path)
    {
        if (!path.Approach || path.PathLine.Count < 2 || path.Surface is not (GroundSurface.Path or GroundSurface.Paving)) return Array.Empty<string>();
        var standard = document.ProjectInformation.ClearanceStandard;
        var problems = new List<string>();
        var width = path.PathWidth;

        var (least, rule) = standard switch
        {
            _ when standard.IsUs() => (915.0, "the ADA asks for an accessible route 36 in (915) wide (403.5.1)"),
            ClearanceStandard.ApprovedDocumentM3 => (1200.0, "Approved Document M asks a wheelchair user's approach for 1200 (M4(3) 3.9b)"),
            _ => (900.0, "Approved Document M asks for 900 (M4(2) 2.9b)")
        };
        if (width < least - 0.5) problems.Add($"It is {width:0} wide: {rule}.");

        var (most, fallRule) = standard.IsUs() ? (1 / 48.0, "the ADA allows 1:48 across an accessible route (403.3)") : (1 / 40.0, "Approved Document M allows 1 in 40 across an approach (1.6)");
        if (CrossFall(document, path) is { } fall && fall > most + 1e-6)
            problems.Add($"It falls 1:{1 / fall:0.#} across: {fallRule}.");

        // A wheelchair user's approach: a level place 1500 square every 10 m, to pass or turn.
        var length = Length(path.PathLine);
        if (standard == ClearanceStandard.ApprovedDocumentM3 && width < 1500 - 0.5 && length > 10000)
        {
            var wanted = (int)Math.Floor(length / 10000);
            var places = PassingPlaces(document, path);
            if (places < wanted)
                problems.Add($"It runs {length / 1000:0.0} m at {width:0} wide with {(places == 0 ? "no" : places.ToString())} passing place{(places == 1 ? "" : "s")}: " +
                             "Approved Document M asks for a level space 1500 square at least every 10 m (M4(3) 3.9d) - widen it there, or lay paving 1500 square beside it.");
        }

        return problems;
    }

    /// <summary>The paving or path laid against a path, 1500 square or more, that it can be passed on.</summary>
    private static int PassingPlaces(BimDocument document, GroundRegion path) =>
        document.Elements.OfType<GroundRegion>()
            .Where(other => !ReferenceEquals(other, path) && other.LevelId == path.LevelId && other.Surface is GroundSurface.Path or GroundSurface.Paving && other.Boundary.Count >= 3)
            .Count(other => Polygon2D.Area(other.Boundary) >= 1500 * 1500 - 1
                            && PolygonBoolean.Combine(other.Boundary, path.Boundary, BooleanOperation.Union).Count == 1);

    /// <summary>A square in front of a door, outside it: its width across the door and its depth out from the wall's face.</summary>
    private static IReadOnlyList<Point2D> InFront(Point2D face, Vector2D outward, double width, double depth)
    {
        var along = outward.PerpendicularLeft();
        return new[] { face - along * (width / 2), face + along * (width / 2), face + along * (width / 2) + outward * depth, face - along * (width / 2) + outward * depth };
    }

    /// <summary>Whether a door is the entrance an approach path comes to: outside, and an approach path's end near it.</summary>
    public static bool IsApproachedEntrance(BimDocument document, Door door)
    {
        if (document.FindType<DoorType>(door.TypeId) is not { } type || !Doors.External(document, door, type)) return false;
        if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } wall) return false;
        var at = wall.PointAt(document.GetWallType(wall)?.Structure ?? new Materials.CompoundStructure(), door.DistanceAlongWall, 0);
        return document.Elements.OfType<GroundRegion>().Any(region => region.Approach && region.Boundary.Count >= 3
            && Enumerable.Range(0, region.Boundary.Count).Min(i => Line2D.DistanceFromSegment(at, region.Boundary[i], region.Boundary[(i + 1) % region.Boundary.Count])) < 2000);
    }

    /// <summary>What the landing outside an entrance an approach comes to falls short of: level, big enough, covered.</summary>
    public static IReadOnlyList<(string Said, string Rule)> EntranceProblems(BimDocument document, Door door, ClearanceStandard standard)
    {
        if (standard.IsUs() || !IsApproachedEntrance(document, door)) return Array.Empty<(string, string)>();
        if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return Array.Empty<(string, string)>();

        var wheelchair = standard == ClearanceStandard.ApprovedDocumentM3;
        var (size, coverWidth, coverDepth, paragraph) = wheelchair ? (1500.0, 1200.0, 1200.0, "M4(3) 3.22") : (1200.0, 900.0, 600.0, "M4(2) 2.20");
        var outward = SpaceSeparation.Outward(document, wall);
        var face = wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0) + outward * (wallType.Structure.TotalWidth / 2);
        var landing = InFront(face, outward, size, size);
        var problems = new List<(string, string)>();

        // Level: the ground under it, or the paving or deck laid there.
        var probe = new GroundRegion { LevelId = wall.LevelId };
        probe.SetBoundary(landing);
        if (GroundRegions.Steepest(document, probe) is { } steepest && steepest > Level + 1e-6)
            problems.Add(($"the ground outside it falls 1:{1 / steepest:0.#} where its landing should be.",
                $"Approved Document M ({paragraph}) asks for a level landing {size:0} square outside the entrance - no steeper than 1:60."));

        // Covered: a roof or floor over it, a canopy or porch, over the width and depth asked.
        var head = wall.GetBaseElevation(document) + door.SillHeight + door.HeightOf(document.FindType<DoorType>(door.TypeId));
        var cover = InFront(face, outward, coverWidth, coverDepth);
        var covered = document.Elements.OfType<Slab>().Where(slab => slab is Roof or Floor && slab.GetBottomElevation(document) > head - 1)
            .Any(slab => cover.All(slab.Contains) || cover.All(point => Polygon2D.Contains(slab.Boundary, point)));
        if (!covered)
            problems.Add(("nothing covers the landing outside it.",
                $"Approved Document M ({paragraph}) asks for the entrance landing to be covered {coverWidth:0} wide and {coverDepth:0} deep - a canopy or porch roof over the door."));

        return problems;
    }
}
