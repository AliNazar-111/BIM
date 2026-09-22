using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Works out how a wall's plan outline is cut where it meets another wall (specification
/// section 2.4, "element joins: wall-to-wall mitre/butt").
///
/// Without this every wall is an independent rectangle, so corners overlap and the drawing
/// reads as CAD linework rather than a building. Joining is what lets the same corner be
/// drawn correctly, measured correctly, and exported to IFC as connected elements.
///
/// The cases, decided in this order:
///
/// <list type="number">
/// <item><b>The user said not to join.</b> The end is free, and the other walls there behave as
/// if it were not there.</item>
///
/// <item><b>The end runs into the side of a wall that passes by.</b> It stops at that wall's
/// near face. The wall passing is never altered.</item>
///
/// <item><b>A straight run passes through the joint.</b> Walls in line with each other at the
/// joint form a run. If several do - a cross of split walls - the heaviest run wins. Walls in
/// the run are cut square and read as one; everything else stops against the run's face.
/// This is also why splitting a wall leaves its junctions looking as they did.</item>
///
/// <item><b>Two walls turn a corner.</b> A mitre, unless the user asked for a butt or square
/// end, or the mitre has run away (see the mitre limit below).</item>
///
/// <item><b>Three or more walls meet with no run through them.</b> Each is cut to the corners
/// it makes with the walls either side of it, and they share the middle of the junction, so
/// between them they fill it once - no overlap and no gap.</item>
/// </list>
///
/// Anything that fails its checks falls back to a square end, which overlaps its neighbours
/// but never draws as something that is not a wall.
/// </summary>
public static class WallJoins
{
    /// <summary>Endpoints closer than this are treated as the same point. Millimetres.</summary>
    public const double JoinTolerance = 1.0;

    /// <summary>Below this, two walls are in line and there is no corner to mitre.</summary>
    private const double CollinearTolerance = 1e-6;

    /// <summary>
    /// How far a mitre may reach past the joint, as a multiple of the wider wall.
    ///
    /// As two walls approach parallel their faces approach parallel too, so the mitre corner
    /// runs away and the wall is drawn as a long taper. The same problem, and the same fix,
    /// as the mitre limit on a stroked path in vector graphics: past the limit the end is cut
    /// square instead. 2 starts butting at about 29 degrees, the same cut-off as SVG's
    /// default.
    /// </summary>
    private const double MiterLimit = 2.0;

    /// <summary>
    /// A mitre may also reach no further than this fraction of the shorter of the two walls.
    /// A corner longer than the wall it belongs to is never right, however wide the wall.
    /// </summary>
    private const double MiterLengthFraction = 0.5;

    private const double Epsilon = 1e-6;

    /// <summary>How each end of the wall is cut, and what kind of end it is.</summary>
    public static (WallCut Start, WallCut End) GetEndCuts(BimDocument document, Wall wall, WallType type) =>
        (ComputeEnd(document, wall, type, atStart: true),
         ComputeEnd(document, wall, type, atStart: false));

    /// <summary>
    /// The outline of a band running along the wall between two signed offsets from its body
    /// centreline, measured positive toward the exterior, with both ends cut to any joined
    /// neighbours.
    ///
    /// The whole wall is one band from +half to -half; a single layer is a narrower one. The
    /// corners run outer edge at the start, outer edge at the end, round the end to the inner
    /// edge, and back round the start - four of them for straight cuts, more where a shared
    /// junction or a wrapped layer turns a corner.
    /// </summary>
    public static Point2D[] GetBandOutline(
        BimDocument document, Wall wall, WallType type, double outerOffset, double innerOffset)
    {
        var (startCut, endCut) = GetEndCuts(document, wall, type);
        return GetBandOutline(wall, type, outerOffset, innerOffset, startCut, endCut);
    }

    /// <summary>
    /// As above, but with the cuts supplied. Drawing every layer of a wall solves the joins
    /// once and reuses them, rather than re-solving per layer.
    /// </summary>
    public static Point2D[] GetBandOutline(
        Wall wall, WallType type, double outerOffset, double innerOffset, WallCut startCut, WallCut endCut)
    {
        var start = EndPoints(wall, type, outerOffset, innerOffset, startCut, atStart: true);
        var end = EndPoints(wall, type, outerOffset, innerOffset, endCut, atStart: false);

        var ring = new List<Point2D>(start.Count + end.Count) { start[0] };

        // A curved wall's long edges follow the arc; a straight wall's need nothing between
        // their ends.
        ring.AddRange(Edge(wall, type, outerOffset, start[0], end[0]));
        ring.AddRange(end);
        ring.AddRange(Edge(wall, type, innerOffset, end[^1], start[^1]));
        for (var i = start.Count - 1; i >= 1; i--) ring.Add(start[i]);

        return ring.ToArray();
    }

    /// <summary>
    /// The points along one long edge of a band strictly between two of its corners, which is
    /// none at all for a straight wall and enough to follow the arc for a curved one.
    /// </summary>
    public static IEnumerable<Point2D> Edge(Wall wall, WallType type, double across, Point2D from, Point2D to)
    {
        if (!wall.IsCurved) return Array.Empty<Point2D>();

        var structure = type.Structure;
        var curve = wall.LocationCurve;
        var a = wall.Locate(structure, from).Along;
        var b = wall.Locate(structure, to).Along;

        return curve.Between(a, b, wall.LeftOf(structure, across)).Select(along => wall.PointAt(structure, along, across)).ToList();
    }

    /// <summary>
    /// Where a band meets one end of the wall, from its outer edge round to its inner edge.
    /// Two points for a straight cut; more where the cut bends or the band wraps.
    /// </summary>
    public static IReadOnlyList<Point2D> EndPoints(
        Wall wall, WallType type, double outerOffset, double innerOffset, WallCut cut, bool atStart)
    {
        if (cut.Wrapping != WallWrapping.None && cut.IsStraight &&
            WrappedEnd(wall, type, outerOffset, innerOffset, cut, atStart) is { } wrapped)
        {
            return wrapped;
        }

        var structure = type.Structure;

        // Where along the wall this cut is - its own end for a join, part-way along for a
        // jamb or a crossing. On a curve a line meets each edge twice, and the crossing that
        // belongs to this cut is the one near here, not near the wall's end.
        var end = Math.Clamp(wall.Locate(structure, cut.Points[cut.Points.Count / 2]).Along, 0, wall.Length);

        double Across(Point2D point) => wall.Locate(structure, point).Across;

        Point2D Hit(double offset)
        {
            var points = cut.Points;

            // The segment of the cut that spans this offset; the first and last run on for ever.
            var k = 0;
            while (k < points.Count - 2 && offset < Across(points[k + 1])) k++;

            // The edge is a line on a straight wall and an arc on a curved one; where a line
            // cuts a circle twice, the crossing near this end is the one that belongs to it.
            var squareEnd = wall.PointAt(structure, end, offset);
            return wall.EdgeCrossing(structure, Line2D.Through(points[k], points[k + 1]), offset, squareEnd)
                   ?? squareEnd;
        }

        var result = new List<Point2D> { Hit(outerOffset) };

        if (!cut.IsStraight)
        {
            foreach (var point in cut.Points)
            {
                var across = Across(point);
                if (across < outerOffset - Epsilon && across > innerOffset + Epsilon) result.Add(point);
            }
        }

        result.Add(Hit(innerOffset));
        return result;
    }

    /// <summary>
    /// Any other wall on the same level with an end at this point. Returns null when the end
    /// is free, or when the only neighbour runs straight on and needs no mitre.
    /// </summary>
    public static Wall? FindPartnerAt(BimDocument document, Wall wall, Point2D joint)
    {
        Wall? best = null;
        var bestTurn = CollinearTolerance;

        foreach (var candidate in PartnersAt(document, wall, joint))
        {
            var turn = Math.Abs(wall.Direction.Cross(candidate.Direction));
            if (turn <= bestTurn) continue;

            best = candidate;
            bestTurn = turn;
        }

        return best;
    }

    public static bool TouchesAt(Wall wall, Point2D point) =>
        wall.Start.DistanceTo(point) <= JoinTolerance || wall.End.DistanceTo(point) <= JoinTolerance;

    /// <summary>The join the user asked for at the end of a wall that lies at this point.</summary>
    public static WallJoinKind JoinAt(Wall wall, Point2D joint) =>
        wall.Start.DistanceTo(joint) <= JoinTolerance ? wall.StartJoin : wall.EndJoin;

    /// <summary>
    /// Where other walls stop against this one's faces, as stretches along it: the part of
    /// each face that is not a face at all, because another wall carries on from it.
    ///
    /// A plan drawn with a line straight across the mouth of every tee reads as a collection
    /// of boxes rather than a building. These are the gaps the outline leaves.
    /// </summary>
    public static IReadOnlyList<(bool Exterior, double From, double To)> FaceGaps(
        BimDocument document, Wall wall, WallType type)
    {
        var gaps = new List<(bool, double, double)>();
        var structure = type.Structure;
        var curve = wall.LocationCurve;
        var half = type.Width / 2;

        foreach (var other in document.Walls)
        {
            if (ReferenceEquals(other, wall) || other.LevelId != wall.LevelId) continue;
            if (document.GetWallType(other) is not { } otherType) continue;

            // Only a wall that ends near this one, or crosses it, can be stopping against it.
            var reach = half + otherType.Width + type.Width + JoinTolerance;
            if (curve.DistanceTo(other.Start) > reach &&
                curve.DistanceTo(other.End) > reach &&
                !SegmentsCross(wall, other))
                continue;

            var otherHalf = otherType.Width / 2;

            // Every stretch the other wall is drawn as, so a wall broken by a crossing counts too.
            var cuts = WallSlices.Solid(document, other, otherType)
                .SelectMany(slice => new[] { (slice.CutFrom, true), (slice.CutTo, false) });

            foreach (var (cut, atStart) in cuts)
            {
                if (cut.Condition != WallEndCondition.Butt) continue;

                var corners = EndPoints(other, otherType, otherHalf, -otherHalf, cut, atStart);
                var a = corners[0];
                var b = corners[^1];

                // Both corners have to sit on one of this wall's faces for it to be this wall
                // they stop against. On a curved wall the other's end is cut along the tangent,
                // so its corners sit a hair off the arc; the allowance covers that.
                var (alongA, across) = wall.Locate(structure, a);
                var (alongB, acrossB) = wall.Locate(structure, b);
                var allowance = wall.IsCurved ? 0.5 + otherType.Width * otherType.Width / (8 * curve.MinRadius) : 0.5;

                if (Math.Abs(Math.Abs(across) - half) > allowance) continue;
                if (Math.Abs(acrossB - across) > 2 * allowance) continue;

                var from = Math.Min(alongA, alongB);
                var to = Math.Max(alongA, alongB);
                if (to <= Epsilon || from >= wall.Length - Epsilon) continue;

                gaps.Add((across > 0, from, to));
            }
        }

        return gaps;
    }

    /// <summary>
    /// Where other walls cross this one part-way along both, and this one gives way.
    ///
    /// Two walls crossing with neither ending there have no end to join, so without this they
    /// simply pass through each other. As at a cross of split walls, the heavier carries on
    /// unbroken and the lighter stops against its faces on both sides - which leaves the
    /// lighter wall as two pieces with a gap the heavier one fills. Each crossing is the
    /// stretch along this wall that the other occupies, with the cuts either side of it.
    /// </summary>
    public static IReadOnlyList<(double From, double To, WallCut Before, WallCut After)> Crossings(
        BimDocument document, Wall wall, WallType type)
    {
        var crossings = new List<(double, double, WallCut, WallCut)>();
        var structure = type.Structure;
        var curve = wall.LocationCurve;

        foreach (var other in document.Walls)
        {
            if (ReferenceEquals(other, wall) || other.LevelId != wall.LevelId) continue;
            if (document.GetWallType(other) is not { } otherType) continue;

            var otherCurve = other.LocationCurve;

            foreach (var point in curve.Crossings(otherCurve))
            {
                // A proper crossing: well inside both walls, not at or near either one's end.
                var along = curve.Locate(point).Along;
                var otherAlong = otherCurve.Locate(point).Along;
                if (along <= JoinTolerance || along >= wall.Length - JoinTolerance) continue;
                if (otherAlong <= JoinTolerance || otherAlong >= other.Length - JoinTolerance) continue;

                // Running along each other is not crossing.
                if (Math.Abs(wall.TangentAt(along).Cross(other.TangentAt(otherAlong))) <= CollinearTolerance) continue;

                if (!GivesWayTo(wall, type, other, otherType)) continue;

                // The other wall's two faces where it crosses - straight lines along its
                // tangent there - and where each meets this wall's centreline.
                var faces = new[] { 1.0, -1.0 }
                    .Select(side => new Line2D(
                        other.PointAt(otherType.Structure, otherAlong, side * otherType.Width / 2),
                        other.TangentAt(otherAlong)))
                    .Select(face => wall.EdgeCrossing(structure, face, 0, point) is { } hit
                        ? (Face: face, At: wall.Locate(structure, hit).Along)
                        : (Face: face, At: double.NaN))
                    .ToList();

                if (faces.Any(f => double.IsNaN(f.At))) continue;

                var (near, far) = faces[0].At <= faces[1].At ? (faces[0], faces[1]) : (faces[1], faces[0]);

                // A crossing so shallow it eats metres of wall is not a crossing anyone drew.
                if (far.At - near.At > MiterLimit * 2 * Math.Max(type.Width, otherType.Width)) continue;

                // Across a curved wall the cuts follow its faces round; across a straight one
                // the faces are the lines already found.
                WallCut Cut((Line2D Face, double At) face) =>
                    (other.IsCurved
                        ? CurvedFaceCut(wall, type, other, otherType, otherAlong,
                            other.Locate(otherType.Structure, face.Face.Origin).Across, WallEndCondition.Butt)
                        : null)
                    ?? WallCut.Along(face.Face, wall, WallEndCondition.Butt);

                crossings.Add((near.At, far.At, Cut(near), Cut(far)));
            }
        }

        return crossings.OrderBy(c => c.Item1).ToList();
    }

    /// <summary>
    /// A cut along one face of a curved wall, where another wall meets it: a chain of short
    /// straight pieces following the arc, so the meeting wall stops exactly on the curve
    /// rather than on a tangent that parts from it a few millimetres out.
    /// </summary>
    private static WallCut? CurvedFaceCut(
        Wall cutWall, WallType cutType, Wall curved, WallType curvedType, double curvedAlong, double faceAcross,
        WallEndCondition condition)
    {
        const int steps = 12;
        var structure = curvedType.Structure;

        // Only as far along the face as it takes to cross the wall being cut, which is further
        // the more obliquely it crosses.
        var cutAlong = Math.Clamp(cutWall.Locate(cutType.Structure, curved.PointAt(structure, curvedAlong, faceAcross)).Along, 0, cutWall.Length);
        var sine = Math.Abs(cutWall.TangentAt(cutAlong).Cross(curved.TangentAt(curvedAlong)));
        var span = cutType.Width / Math.Max(sine, 0.2) + cutType.Width;

        var points = Enumerable.Range(-steps, 2 * steps + 1)
            .Select(i => curved.PointAt(structure, curvedAlong + span * i / steps, faceAcross))
            .ToList();

        // A cut runs across the wall it cuts, one way. A face that turns back within that
        // stretch - two curves grazing each other - cannot be followed, and the straight
        // tangent is used instead.
        var across = points.Select(point => cutWall.Locate(cutType.Structure, point).Across).ToList();
        var rising = across.Zip(across.Skip(1)).All(pair => pair.Second > pair.First);
        var falling = across.Zip(across.Skip(1)).All(pair => pair.Second < pair.First);
        if (!rising && !falling) return null;

        // Ordered across the wall being cut, exterior side first, as a cut has to be.
        if (rising) points.Reverse();
        return new WallCut(points, condition);
    }

    /// <summary>Whether two walls' drawn lines cross.</summary>
    private static bool SegmentsCross(Wall a, Wall b) => a.LocationCurve.Crossings(b.LocationCurve).Count > 0;

    /// <summary>
    /// Which of two crossing walls carries on: the wider, then the longer, then - so the
    /// answer never depends on drawing order - the one with the lower id.
    /// </summary>
    private static bool GivesWayTo(Wall wall, WallType type, Wall other, WallType otherType)
    {
        if (Math.Abs(type.Width - otherType.Width) > Epsilon) return type.Width < otherType.Width;
        if (Math.Abs(wall.Length - other.Length) > Epsilon) return wall.Length < other.Length;
        return wall.Id.CompareTo(other.Id) > 0;
    }

    // ---- who meets whom ---------------------------------------------------------

    /// <summary>Every other wall on this level with an end at this point that is willing to join.</summary>
    private static List<Wall> PartnersAt(BimDocument document, Wall wall, Point2D joint) =>
        document.Walls
            .Where(candidate => candidate.Id != wall.Id)
            .Where(candidate => candidate.LevelId == wall.LevelId)
            .Where(candidate => TouchesAt(candidate, joint))
            .Where(candidate => JoinAt(candidate, joint) != WallJoinKind.Disallow)
            .Select(candidate => Straight(candidate, joint))
            .ToList();

    /// <summary>
    /// A wall as seen from one point on it: itself if straight, or, if curved, a straight wall
    /// along its tangent there. A join only looks at the wall where it meets, and there a
    /// curved wall is running in the direction of its tangent - so every rule written for
    /// straight walls applies to curved ones through this. The cut it produces is then applied
    /// to the true arc.
    /// </summary>
    private static Wall Straight(Wall wall, Point2D at)
    {
        if (!wall.IsCurved) return wall;

        var curve = wall.LocationCurve;
        var length = Math.Max(curve.Length, 1);

        Point2D start, end;
        if (wall.Start.DistanceTo(at) <= JoinTolerance)
        {
            start = wall.Start;
            end = start + curve.TangentAt(0) * length;
        }
        else if (wall.End.DistanceTo(at) <= JoinTolerance)
        {
            end = wall.End;
            start = end - curve.TangentAt(curve.Length) * length;
        }
        else
        {
            var along = Math.Clamp(curve.Locate(at).Along, 0, curve.Length);
            var point = curve.PointAt(along);
            var tangent = curve.TangentAt(along);
            start = point - tangent * (length / 2);
            end = point + tangent * (length / 2);
        }

        return new Wall
        {
            Id = wall.Id,
            Start = start,
            End = end,
            TypeId = wall.TypeId,
            LevelId = wall.LevelId,
            LocationLine = wall.LocationLine,
            Flipped = wall.Flipped,
            StartJoin = wall.StartJoin,
            EndJoin = wall.EndJoin
        };
    }

    /// <summary>Whether two walls run along the same line, whichever way each was drawn.</summary>
    private static bool AreInLine(Wall a, Wall b) =>
        Math.Abs(a.Direction.Cross(b.Direction)) <= CollinearTolerance;

    /// <summary>A wall whose body passes through the joint without ending there.</summary>
    private static Wall? FindPassingWall(BimDocument document, Wall wall, Point2D joint)
    {
        foreach (var candidate in document.Walls)
        {
            if (candidate.Id == wall.Id) continue;
            if (candidate.LevelId != wall.LevelId) continue;
            if (TouchesAt(candidate, joint)) continue;
            if (candidate.LocationCurve.DistanceTo(joint) > JoinTolerance) continue;

            var seen = Straight(candidate, joint);
            if (!AreInLine(wall, seen)) return seen;
        }

        return null;
    }

    /// <summary>
    /// The straight runs through a joint: groups of walls meeting there that are in line with
    /// each other, strongest first. A run's strength is its width, then its length, so that
    /// at a cross the main wall carries through and the partitions stop against it. The id
    /// settles a perfect tie so the answer never depends on the order things were drawn in.
    /// </summary>
    private static List<List<Wall>> RunsThrough(BimDocument document, IReadOnlyList<Wall> walls)
    {
        var runs = new List<List<Wall>>();

        foreach (var wall in walls)
        {
            var run = runs.FirstOrDefault(r => AreInLine(r[0], wall));
            if (run is null) runs.Add(new List<Wall> { wall });
            else run.Add(wall);
        }

        return runs
            .Where(run => run.Count >= 2)
            .OrderByDescending(run => run.Max(w => document.GetWallType(w)?.Width ?? 0))
            .ThenByDescending(run => run.Sum(w => w.Length))
            .ThenBy(run => run.Min(w => w.Id))
            .ToList();
    }

    // ---- the cut at one end -----------------------------------------------------

    private static WallCut ComputeEnd(BimDocument document, Wall wall, WallType type, bool atStart)
    {
        var joint = atStart ? wall.Start : wall.End;
        var asked = atStart ? wall.StartJoin : wall.EndJoin;

        // From here on a curved wall is its tangent at this end; see Straight.
        wall = Straight(wall, joint);
        var square = SquareCut(wall, type, atStart);

        WallCut Free(WallEndCondition condition) =>
            WallCut.Along(square, wall, condition, type.WrapAtEnds);

        WallCut Square(WallEndCondition condition) => WallCut.Along(square, wall, condition);

        if (asked == WallJoinKind.Disallow) return Free(WallEndCondition.Disallowed);

        // Running into the side of a wall that passes by: stop at its face.
        if (FindPassingWall(document, wall, joint) is { } passing)
        {
            if (ButtAgainst(document, wall, type, passing, joint, atStart) is not { } butt)
                return Square(WallEndCondition.Overlap);

            // Against a curved wall, stop on its face as it curves, not on the tangent.
            var real = document.Walls.FirstOrDefault(w => w.Id == passing.Id);
            if (real is not { IsCurved: true } || document.GetWallType(real) is not { } realType) return butt;

            var faceAcross = real.Locate(realType.Structure, butt.Line.Origin).Across;
            var along = real.LocationCurve.Locate(joint).Along;
            return CurvedFaceCut(wall, type, real, realType, along, faceAcross, WallEndCondition.Butt) ?? butt;
        }

        var partners = PartnersAt(document, wall, joint);
        if (partners.Count == 0) return Free(WallEndCondition.Free);

        // Straight runs through the joint.
        var everyone = partners.Append(wall).ToList();
        var runs = RunsThrough(document, everyone);

        if (runs.Count > 0)
        {
            var main = runs[0];

            if (main.Contains(wall))
                return Square(WallEndCondition.Continues);

            // Stop against the run. Either half of a split run describes the same line; the
            // longer is the safer to measure against.
            var against = main.OrderByDescending(w => w.Length).First();
            return ButtAgainst(document, wall, type, against, joint, atStart) ?? Square(WallEndCondition.Overlap);
        }

        if (partners.Count == 1)
            return Corner(document, wall, type, partners[0], joint, atStart, asked) ?? Square(WallEndCondition.Overlap);

        return SharedJunction(document, wall, type, everyone, joint, atStart) ?? Square(WallEndCondition.Overlap);
    }

    /// <summary>
    /// Two walls turning a corner, joined the way either of them asked - or mitred if neither
    /// asked for anything. A wall asked to butt means its partner runs through, and so on, so
    /// setting one end of a corner is enough.
    /// </summary>
    private static WallCut? Corner(
        BimDocument document, Wall wall, WallType type, Wall partner, Point2D joint, bool atStart, WallJoinKind asked)
    {
        var partnerType = document.GetWallType(partner);
        if (partnerType is null) return null;

        var kind = asked != WallJoinKind.Auto
            ? asked
            : JoinAt(partner, joint) switch
            {
                WallJoinKind.Butt => WallJoinKind.RunThrough,
                WallJoinKind.RunThrough or WallJoinKind.SquareOff => WallJoinKind.Butt,
                _ => WallJoinKind.Mitre
            };

        return kind switch
        {
            WallJoinKind.Butt => ButtAgainst(document, wall, type, partner, joint, atStart),
            WallJoinKind.RunThrough => RunPast(wall, type, partner, partnerType, joint, atStart, squareEnd: false),
            WallJoinKind.SquareOff => RunPast(wall, type, partner, partnerType, joint, atStart, squareEnd: true),
            _ => Mitre(wall, type, partner, partnerType, joint, atStart)
        };
    }

    private static WallCut? Mitre(Wall wall, WallType type, Wall partner, WallType partnerType, Point2D joint, bool atStart)
    {
        var square = SquareCut(wall, type, atStart);

        // Directions pointing away from the corner along each wall.
        var awayFromJoint = AwayFrom(wall, joint);
        var partnerAway = AwayFrom(partner, joint);

        // The interior bisector of the corner. It vanishes when the walls are in line.
        var bisector = awayFromJoint + partnerAway;
        if (bisector.Length < CollinearTolerance) return null;

        var (insideA, outsideA) = FaceLines(wall, type, bisector);
        var (insideB, outsideB) = FaceLines(partner, partnerType, bisector);

        // Faces on matching sides of the corner meet at the mitre's two corner points.
        if (!Line2D.TryIntersect(insideA, insideB, out var insideCorner)) return null;
        if (!Line2D.TryIntersect(outsideA, outsideB, out var outsideCorner)) return null;
        if (insideCorner.DistanceTo(outsideCorner) < CollinearTolerance) return null;

        // Refuse a mitre that has run away from the corner, or the wall draws as a spike.
        var mitre = Line2D.Through(outsideCorner, insideCorner);
        return CutStaysNearTheJoint(mitre, wall, type, square, Budget(type, partnerType, wall, partner))
            ? WallCut.Along(mitre, wall, WallEndCondition.Mitre)
            : null;
    }

    /// <summary>
    /// A cut along the near face of another wall, so this one stops against it.
    ///
    /// Returns null when the butt would reach too far along the wall - a very shallow
    /// approach makes a long tapered end, which is the same failure the mitre limit exists to
    /// prevent - so the caller can fall back to a square end.
    /// </summary>
    private static WallCut? ButtAgainst(
        BimDocument document, Wall wall, WallType type, Wall run, Point2D joint, bool atStart)
    {
        var runType = document.GetWallType(run);
        if (runType is null) return null;
        if (AreInLine(wall, run)) return null;

        var face = FaceToward(run, runType, AwayFrom(wall, joint), joint, near: true);

        var budget = Math.Min(
            MiterLimit * Math.Max(type.Width, runType.Width),
            MiterLengthFraction * wall.Length);

        return CutStaysNearTheJoint(face, wall, type, SquareCut(wall, type, atStart), budget)
            ? WallCut.Along(face, wall, WallEndCondition.Butt)
            : null;
    }

    /// <summary>
    /// Carries this wall on past a partner that stops against it, to the partner's far face so
    /// the outside of the corner is filled. Cut along that face, or square to this wall and just
    /// far enough to cover it.
    /// </summary>
    private static WallCut? RunPast(
        Wall wall, WallType type, Wall partner, WallType partnerType, Point2D joint, bool atStart, bool squareEnd)
    {
        if (AreInLine(wall, partner)) return null;

        var farFace = FaceToward(partner, partnerType, AwayFrom(wall, joint), joint, near: false);
        var square = SquareCut(wall, type, atStart);
        var budget = Budget(type, partnerType, wall, partner);

        if (!squareEnd)
        {
            return CutStaysNearTheJoint(farFace, wall, type, square, budget)
                ? WallCut.Along(farFace, wall, WallEndCondition.RunsThrough)
                : null;
        }

        // Square: far enough along that both of this wall's faces reach the partner's far face.
        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);
        var outward = atStart ? -1.0 : 1.0;
        var reach = double.NegativeInfinity;

        foreach (var offset in new[] { type.Width / 2, -type.Width / 2 })
        {
            var edge = new Line2D(bodyStart + wall.ExteriorNormal * offset, wall.Direction);
            if (!Line2D.TryIntersect(edge, farFace, out var hit)) return null;

            reach = Math.Max(reach, outward * (hit - bodyStart).Dot(wall.Direction));
        }

        var position = bodyStart + wall.Direction * (outward * reach);
        var cut = new Line2D(position, wall.Direction.PerpendicularLeft());

        return CutStaysNearTheJoint(cut, wall, type, square, budget)
            ? WallCut.Along(cut, wall, WallEndCondition.RunsThrough)
            : null;
    }

    /// <summary>
    /// Three or more walls meeting at a point with no straight run through it.
    ///
    /// Going round the joint, each pair of neighbouring walls meets at a corner where their
    /// facing sides cross. Every wall is cut from the corner on one side of it to the middle of
    /// the junction and out to the corner on its other side. Because each piece of the
    /// junction is claimed by exactly one wall, the walls fill it without overlapping.
    /// </summary>
    private static WallCut? SharedJunction(
        BimDocument document, Wall wall, WallType type, IReadOnlyList<Wall> walls, Point2D joint, bool atStart)
    {
        var arms = new List<(Wall Wall, WallType Type, Vector2D Away, double Angle)>();

        foreach (var member in walls)
        {
            if (document.GetWallType(member) is not { } memberType) return null;

            var away = AwayFrom(member, joint);
            arms.Add((member, memberType, away, Math.Atan2(away.Y, away.X)));
        }

        arms.Sort((a, b) => a.Angle.CompareTo(b.Angle));

        // The corner between each arm and the next one anticlockwise from it.
        var corners = new Point2D[arms.Count];
        for (var i = 0; i < arms.Count; i++)
        {
            var (a, aType, aAway, _) = arms[i];
            var (b, bType, bAway, _) = arms[(i + 1) % arms.Count];

            var aSide = SideFace(a, aType, aAway.PerpendicularLeft());
            var bSide = SideFace(b, bType, -bAway.PerpendicularLeft());

            if (!Line2D.TryIntersect(aSide, bSide, out corners[i])) return null;
        }

        // Every arm has to reach its own corners without running away, whichever middle is used.
        var widest = arms.Max(arm => arm.Type.Width);

        for (var i = 0; i < arms.Count; i++)
        {
            var (member, _, away, _) = arms[i];
            var budget = Math.Min(MiterLimit * widest, MiterLengthFraction * member.Length);

            foreach (var corner in new[] { corners[i], corners[(i + arms.Count - 1) % arms.Count] })
                if (Math.Abs((corner - joint).Dot(away)) > budget) return null;
        }

        // The shared middle has to lie inside every wall and between each wall's two corners,
        // or the pieces do not tile the junction. The joint itself is inside every wall drawn on
        // its centreline, whatever their widths; the middle of the corners suits walls drawn to
        // a face. The first that every arm accepts is used, so all the walls at one joint agree.
        bool Accepts(Point2D middle)
        {
            for (var i = 0; i < arms.Count; i++)
            {
                var (member, memberType, away, _) = arms[i];

                var (bodyStart, _) = member.GetBodyCentreline(memberType.Structure);
                var across = (middle - bodyStart).Dot(member.ExteriorNormal);
                if (Math.Abs(across) >= memberType.Width / 2 - Epsilon) return false;

                var leftSide = away.PerpendicularLeft();
                if ((corners[i] - middle).Dot(leftSide) <= 0) return false;
                if ((corners[(i + arms.Count - 1) % arms.Count] - middle).Dot(leftSide) >= 0) return false;
            }

            return true;
        }

        var average = new Point2D(corners.Average(c => c.X), corners.Average(c => c.Y));
        Point2D? chosen = Accepts(joint) ? joint
            : Accepts(average) ? average
            : Accepts(joint.MidpointTo(average)) ? joint.MidpointTo(average)
            : null;

        if (chosen is not { } middlePoint) return null;

        var index = arms.FindIndex(arm => ReferenceEquals(arm.Wall, wall));
        var left = corners[index];
        var right = corners[(index + arms.Count - 1) % arms.Count];

        var exteriorIsLeft = wall.ExteriorNormal.Dot(AwayFrom(wall, joint).PerpendicularLeft()) > 0;
        var (exterior, interior) = exteriorIsLeft ? (left, right) : (right, left);

        return new WallCut(new[] { exterior, middlePoint, interior }, WallEndCondition.Shared);
    }

    // ---- wrapping -----------------------------------------------------------------

    /// <summary>
    /// The end of one band at an exposed end or an opening, with the wrapping layers turned
    /// round the corner (specification section 3.1).
    ///
    /// The wrapping layers on the wrapped side run on round the end, the outermost outside the
    /// rest, and across to the far face - or to the middle of the core when both sides wrap.
    /// Everything they wrap round is set back by their combined thickness. Null for a band
    /// that is not a single layer, which is then cut plainly.
    /// </summary>
    private static IReadOnlyList<Point2D>? WrappedEnd(
        Wall wall, WallType type, double outer, double inner, WallCut cut, bool atStart)
    {
        var structure = type.Structure;
        var coreStart = structure.CoreStartIndex;
        var coreEnd = structure.CoreEndIndex;
        if (coreStart < 0) return null;

        var half = structure.TotalWidth / 2;
        var layers = structure.GetLayerOffsets().ToList();

        var wrapsExterior = cut.Wrapping is WallWrapping.Exterior or WallWrapping.Both;
        var wrapsInterior = cut.Wrapping is WallWrapping.Interior or WallWrapping.Both;

        var exteriorWrap = Enumerable.Range(0, coreStart)
            .Where(i => wrapsExterior && layers[i].Layer.Wraps).ToList();
        var interiorWrap = Enumerable.Range(coreEnd + 1, layers.Count - coreEnd - 1)
            .Where(i => wrapsInterior && layers[i].Layer.Wraps).ToList();

        var exteriorDepth = exteriorWrap.Sum(i => layers[i].Layer.Thickness);
        var interiorDepth = interiorWrap.Sum(i => layers[i].Layer.Thickness);

        var middle = half - (structure.ExteriorWidth + structure.CoreWidth / 2);
        var exteriorStop = cut.Wrapping == WallWrapping.Both ? middle : -half;
        var interiorStop = cut.Wrapping == WallWrapping.Both ? middle : half;

        // Where the end is, and which way is back into the wall.
        var position = wall.Locate(structure, cut.Points[0]).Along;
        var inward = atStart ? 1.0 : -1.0;

        Point2D At(double setBack, double across) =>
            wall.PointAt(structure, position + inward * setBack, across);

        // The whole wall is square at the end: the wrapping fills the corner out to it.
        if (Math.Abs(outer - half) < Epsilon && Math.Abs(inner + half) < Epsilon)
            return new[] { At(0, outer), At(0, inner) };

        var layer = layers.FindIndex(l =>
            Math.Abs(half - l.Start - outer) < Epsilon && Math.Abs(half - l.End - inner) < Epsilon);
        if (layer < 0) return null;

        var thickness = layers[layer].Layer.Thickness;

        if (exteriorWrap.Contains(layer) && exteriorStop < inner - Epsilon)
        {
            var depth = exteriorWrap.Where(i => i < layer).Sum(i => layers[i].Layer.Thickness);
            return new[]
            {
                At(depth, outer), At(depth, exteriorStop), At(depth + thickness, exteriorStop), At(depth + thickness, inner)
            };
        }

        if (interiorWrap.Contains(layer) && interiorStop > outer + Epsilon)
        {
            var depth = interiorWrap.Where(i => i > layer).Sum(i => layers[i].Layer.Thickness);
            return new[]
            {
                At(depth + thickness, outer), At(depth + thickness, interiorStop), At(depth, interiorStop), At(depth, inner)
            };
        }

        // Wrapped round, not wrapping: set back behind the layers that are.
        if (cut.Wrapping == WallWrapping.Both && outer > middle + Epsilon && inner < middle - Epsilon)
            return new[] { At(exteriorDepth, outer), At(exteriorDepth, middle), At(interiorDepth, middle), At(interiorDepth, inner) };

        var setBack = cut.Wrapping switch
        {
            WallWrapping.Exterior => exteriorDepth,
            WallWrapping.Interior => interiorDepth,
            _ => inner >= middle - Epsilon ? exteriorDepth : interiorDepth
        };

        return new[] { At(setBack, outer), At(setBack, inner) };
    }

    // ---- geometry helpers ---------------------------------------------------------

    /// <summary>The direction along a wall pointing away from one of its ends.</summary>
    private static Vector2D AwayFrom(Wall wall, Point2D joint) =>
        wall.Start.DistanceTo(joint) <= wall.End.DistanceTo(joint) ? wall.Direction : -wall.Direction;

    /// <summary>A flat end, perpendicular to the wall through its body centreline point.</summary>
    private static Line2D SquareCut(Wall wall, WallType type, bool atStart)
    {
        var (bodyStart, bodyEnd) = wall.GetBodyCentreline(type.Structure);
        return new Line2D(atStart ? bodyStart : bodyEnd, wall.Direction.PerpendicularLeft());
    }

    /// <summary>The face of a wall on the given side of it.</summary>
    private static Line2D SideFace(Wall wall, WallType type, Vector2D side)
    {
        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);
        var sign = wall.ExteriorNormal.Dot(side) >= 0 ? 1.0 : -1.0;

        return new Line2D(bodyStart + wall.ExteriorNormal * (sign * type.Width / 2), wall.Direction);
    }

    /// <summary>
    /// One face of a wall that another meets: the near face, on the side the other arrives
    /// from, or the far face opposite it.
    /// </summary>
    private static Line2D FaceToward(Wall run, WallType runType, Vector2D arrivingAway, Point2D joint, bool near)
    {
        var (runBodyStart, _) = run.GetBodyCentreline(runType.Structure);
        var across = run.Direction.PerpendicularLeft();

        // Which side the other wall lies on, taken from the direction it runs away down.
        var side = arrivingAway.Dot(across) >= 0 ? 1.0 : -1.0;
        if (!near) side = -side;

        var onCentreline = new Line2D(runBodyStart, run.Direction).ClosestPointTo(joint);
        return new Line2D(onCentreline + across * (side * runType.Width / 2), run.Direction);
    }

    /// <summary>
    /// The wall's two outermost face lines, labelled by which side of the corner they fall
    /// on rather than by the wall's own orientation.
    /// </summary>
    private static (Line2D Inside, Line2D Outside) FaceLines(Wall wall, WallType type, Vector2D bisector)
    {
        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);
        var normal = wall.ExteriorNormal;
        var half = type.Width / 2;

        var exteriorFace = new Line2D(bodyStart + normal * half, wall.Direction);
        var interiorFace = new Line2D(bodyStart + normal * -half, wall.Direction);

        // The face leaning toward the bisector is the one inside the corner.
        return (normal * half).Dot(bisector) > 0
            ? (exteriorFace, interiorFace)
            : (interiorFace, exteriorFace);
    }

    /// <summary>
    /// Whether cutting the end along this line keeps the wall's corners near the joint.
    ///
    /// The test has to be made on the corners, not on where the cut crosses the wall's
    /// centreline: an oblique cut can cross the centreline close to the joint and still throw
    /// the corners metres away, which is precisely how a wall ends up drawn as a spike.
    /// </summary>
    private static bool CutStaysNearTheJoint(
        Line2D cut, Wall wall, WallType type, Line2D squareCut, double budget)
    {
        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);
        var normal = wall.ExteriorNormal;
        var half = type.Width / 2;

        foreach (var offset in new[] { half, -half })
        {
            var edge = new Line2D(bodyStart + normal * offset, wall.Direction);
            if (!Line2D.TryIntersect(edge, cut, out var corner)) return false;

            if (Math.Abs((corner - squareCut.Origin).Dot(wall.Direction)) > budget) return false;
        }

        return true;
    }

    /// <summary>
    /// How far past the joint a mitre may reach before it stops looking like a corner.
    /// Scaled by the wider of the two walls, and bounded by the shorter wall's length.
    /// </summary>
    private static double Budget(WallType type, WallType partnerType, Wall wall, Wall partner) =>
        Math.Min(
            MiterLimit * Math.Max(type.Width, partnerType.Width),
            MiterLengthFraction * Math.Min(wall.Length, partner.Length));
}
