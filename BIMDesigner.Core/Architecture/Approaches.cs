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
///
/// A building other than a home (round 123), by Approved Document M Volume 2: a level approach 1.5 m wide - 1.2 m only on a restricted
/// site, as building control agrees (1.11) - with passing places 1.8 m wide and 2 m long 50 m apart at most where it is narrower than 1.8 m,
/// falling no more than 1 in 40 across it, and steeper than 1:60 a level landing for each 500 mm it rises (1.13a-c); at an accessible
/// entrance a level landing 1500 square clear of the door's swing (2.7d), and over a door not powered, weather protection (2.7g) - taken as
/// a roof or canopy over its width and 600 deep, the paragraph giving no size.
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

        // A building other than a dwelling's: Approved Document M Volume 2 Section 1 (round 123).
        if (!Domestic.Rules(document)) return Volume2Path(document, path);
        var standard = document.ProjectInformation.ClearanceStandard;
        var problems = new List<string>();
        var width = path.PathWidth;

        var (least, rule) = standard switch
        {
            _ when standard.IsUs() => (915.0, $"the ADA asks for an accessible route 36 in{Units.MetricAside("915")} wide (403.5.1)"),
            ClearanceStandard.ApprovedDocumentM3 => (1200.0, "Approved Document M asks a wheelchair user's approach for 1200 (M4(3) 3.9b)"),
            ClearanceStandard.ApprovedDocumentM1 => (900.0, "Approved Document M asks for 900 (M4(1) 1.6c)"),
            _ => (900.0, "Approved Document M asks for 900 (M4(2) 2.9b)")
        };
        if (width < least - 0.5) problems.Add($"It is {Units.Bare(width)} wide: {rule}.");

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
                problems.Add($"It runs {Units.Metres(length, "0.0")} at {width:0} wide with {(places == 0 ? "no" : places.ToString())} passing place{(places == 1 ? "" : "s")}: " +
                             "Approved Document M asks for a level space 1500 square at least every 10 m (M4(3) 3.9d) - widen it there, or lay paving 1500 square beside it.");
        }

        return problems;
    }

    /// <summary>The paving or path laid against a path, so wide and long or more - 1500 square unless said - that it can be passed on.</summary>
    private static int PassingPlaces(BimDocument document, GroundRegion path, double wide = 1500, double longest = 1500) =>
        document.All<GroundRegion>()
            .Where(other => !ReferenceEquals(other, path) && other.LevelId == path.LevelId && other.Surface is GroundSurface.Path or GroundSurface.Paving && other.Boundary.Count >= 3)
            .Count(other => Polygon2D.Area(other.Boundary) >= wide * longest - 1 && FireAccess.Narrowest(other.Boundary) >= Math.Min(wide, longest) - 0.5
                            && PolygonBoolean.Combine(other.Boundary, path.Boundary, BooleanOperation.Union).Count == 1);

    /// <summary>
    /// An approach to a building other than a dwelling, by Approved Document M Volume 2 (1.11, 1.13): 1.5 m wide, 1 in 40 across, passing
    /// places 1.8 m by 2 m every 50 m on one narrower than 1.8 m, a level landing for each 500 mm it rises.
    /// </summary>
    private static IReadOnlyList<string> Volume2Path(BimDocument document, GroundRegion path)
    {
        var problems = new List<string>();
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return problems;
        var width = path.PathWidth;
        if (width < 1500 - 0.5)
            problems.Add($"It is {Units.Bare(width)} wide: Approved Document M Volume 2 (1.13a) asks an approach to a building other than a dwelling for 1.5 m - 1.2 m only on a restricted site, as building control agrees (1.11).");
        if (CrossFall(document, path) is { } fall && fall > 1 / 40.0 + 1e-6)
            problems.Add($"It falls 1:{1 / fall:0.#} across: Approved Document M Volume 2 (1.13c) allows 1 in 40 across an approach.");

        var length = Length(path.PathLine);
        if (width < 1800 - 0.5 && length > 50000 + 0.5)
        {
            var wanted = (int)Math.Floor(length / 50000);
            var places = PassingPlaces(document, path, 1800, 2000);
            if (places < wanted)
                problems.Add($"It runs {Units.Metres(length, "0.0")} at {width:0} wide with {(places == 0 ? "no" : places.ToString())} passing place{(places == 1 ? "" : "s")}: Approved Document M Volume 2 (1.13b) asks passing places 1.8 m wide and 2 m long within sight of each other and 50 m apart at most - paving 1.8 by 2 m beside it, or the approach 1.8 m wide.");
        }

        if (Climb(document, path) is { } climb && climb > 500 + 0.5)
            problems.Add($"It climbs {climb:0} between level landings: Approved Document M Volume 2 (1.13c) asks an approach steeper than 1:60 for a level landing for each 500 mm it rises - 1.5 m long, 1:60 at most (1.26i, k).");
        return problems;
    }

    /// <summary>
    /// The most an approach rises or falls along its middle between level landings - stretches 1.5 m long or more no steeper than 1:60 - mm;
    /// null where all of it is that level.
    /// </summary>
    public static double? Climb(BimDocument document, GroundRegion path)
    {
        if (path.PathLine.Count < 2) return null;
        var samples = new List<(double At, double Height)>();
        var travelled = 0.0;
        for (var i = 0; i + 1 < path.PathLine.Count; i++)
        {
            var (a, b) = (path.PathLine[i], path.PathLine[i + 1]);
            var steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / 100));
            for (var s = i == 0 ? 0 : 1; s <= steps; s++)
                samples.Add((travelled + a.DistanceTo(b) * s / steps, GroundRegions.HeightAt(document, path, a + (b - a) * (s / (double)steps))));
            travelled += a.DistanceTo(b);
        }

        // Each piece level or not; level runs of 1.5 m or more are landings; between them, how far it climbs.
        var level = new bool[samples.Count - 1];
        for (var i = 0; i < level.Length; i++)
            level[i] = Math.Abs(samples[i + 1].Height - samples[i].Height) <= (samples[i + 1].At - samples[i].At) / 60 + 0.01;
        if (level.All(flat => flat)) return null;
        var landing = new bool[level.Length];
        for (var i = 0; i < level.Length;)
        {
            if (!level[i]) { i++; continue; }
            var j = i;
            while (j < level.Length && level[j]) j++;
            if (samples[j].At - samples[i].At >= 1500 - 0.5)
                for (var k = i; k < j; k++) landing[k] = true;
            i = j;
        }

        var most = 0.0;
        for (var i = 0; i < landing.Length;)
        {
            if (landing[i]) { i++; continue; }
            var j = i;
            while (j < landing.Length && !landing[j]) j++;
            most = Math.Max(most, Math.Abs(samples[j].Height - samples[i].Height));
            i = j;
        }

        return most;
    }

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
        if (document.Find<Wall>(door.HostWallId) is not { } wall) return false;
        var at = wall.PointAt(document.GetWallType(wall)?.Structure ?? new Materials.CompoundStructure(), door.DistanceAlongWall, 0);
        return document.All<GroundRegion>().Any(region => region.Approach && region.Boundary.Count >= 3
            && Enumerable.Range(0, region.Boundary.Count).Min(i => Line2D.DistanceFromSegment(at, region.Boundary[i], region.Boundary[(i + 1) % region.Boundary.Count])) < 2000);
    }

    /// <summary>What the landing outside an entrance an approach comes to falls short of: level, big enough, covered.</summary>
    public static IReadOnlyList<(string Said, string Rule)> EntranceProblems(BimDocument document, Door door, ClearanceStandard standard)
    {
        // A home's entrance (M4(2) 2.20, M4(3) 3.22); a building other than a dwelling's by Approved Document M Volume 2 (2.7d, g, round 123).
        if (standard.IsUs() || !IsApproachedEntrance(document, door)) return Array.Empty<(string, string)>();
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return Array.Empty<(string, string)>();
        if (!Domestic.Rules(document, door)) return Volume2Entrance(document, door, wall, wallType);

        // A visitable dwelling's: M4(1) asks no landing or cover at the door - its width and threshold only (1.14, round 148).
        if (standard == ClearanceStandard.ApprovedDocumentM1) return Array.Empty<(string, string)>();

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
        var covered = document.All<Slab>().Where(slab => slab is Roof or Floor && slab.GetBottomElevation(document) > head - 1)
            .Any(slab => cover.All(slab.Contains) || cover.All(point => Polygon2D.Contains(slab.Boundary, point)));
        if (!covered)
            problems.Add(("nothing covers the landing outside it.",
                $"Approved Document M ({paragraph}) asks for the entrance landing to be covered {coverWidth:0} wide and {coverDepth:0} deep - a canopy or porch roof over the door."));

        return problems;
    }

    /// <summary>
    /// An accessible entrance of a building other than a dwelling (Approved Document M Volume 2, 2.7d, g): a level landing 1500 square in front
    /// of it, clear of its swing - an outward opening door's leaf swept first - and, at a door not powered, weather protection over it.
    /// </summary>
    private static IReadOnlyList<(string Said, string Rule)> Volume2Entrance(BimDocument document, Door door, Wall wall, WallType wallType)
    {
        var problems = new List<(string, string)>();
        var type = document.FindType<DoorType>(door.TypeId);
        var outward = SpaceSeparation.Outward(document, wall);
        var face = wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0) + outward * (wallType.Structure.TotalWidth / 2);
        var swingsOut = Doors.RoomsBeside(document, door) is (null, not null);
        var leaf = swingsOut ? door.WidthOf(type) : 0;
        var landing = InFront(face, outward, 1500, 1500 + leaf);
        var probe = new GroundRegion { LevelId = wall.LevelId };
        probe.SetBoundary(landing);
        if (GroundRegions.Steepest(document, probe) is { } steepest && steepest > Level + 1e-6)
            problems.Add(($"the ground outside it falls 1:{1 / steepest:0.#} where its landing should be{(swingsOut ? ", 1500 beyond its swing" : string.Empty)}.",
                "Approved Document M Volume 2 (2.7d) asks for a level landing at least 1500 x 1500 immediately in front of an accessible entrance, clear of any door swings - no steeper than 1:60."));

        var powered = type is not null && (type.Automatic || type.Operation == DoorOperation.Revolving);
        if (!powered)
        {
            var head = wall.GetBaseElevation(document) + door.SillHeight + door.HeightOf(type);
            var cover = InFront(face, outward, door.WidthOf(type), 600);
            var covered = document.All<Slab>().Where(slab => slab is Roof or Floor && slab.GetBottomElevation(document) > head - 1)
                .Any(slab => cover.All(slab.Contains) || cover.All(point => Polygon2D.Contains(slab.Boundary, point)));
            if (!covered)
                problems.Add(("nothing covers it, a door not powered.",
                    "Approved Document M Volume 2 (2.7g) asks weather protection at an accessible entrance's manual, non-powered door - taken here as a canopy or roof over its width and 600 deep."));
        }

        return problems;
    }
}
