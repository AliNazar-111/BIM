using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Works out how a wall's plan outline is cut where it meets another wall (specification
/// section 2.4, "element joins: wall-to-wall mitre/butt").
///
/// Without this every wall is an independent rectangle, so corners overlap and the drawing
/// reads as CAD linework rather than a building. Joining is what lets the same corner be
/// drawn correctly, measured correctly, and eventually exported to IFC as connected
/// elements.
///
/// The rule for a mitre: take the two walls' face lines, pair the faces that sit on the
/// same side of the corner, and intersect each pair. The line through those two
/// intersection points is the cut. Pairing by "same side" rather than by the walls' own
/// left/right makes it independent of which way either wall happens to have been drawn.
///
/// There are four cases, decided in this order:
///
/// <list type="number">
/// <item><b>A run carries straight on.</b> If any wall meeting this point is in line with
/// this one, the run continues through and there is no corner to turn, so the end is cut
/// square. It is cut square even when other walls also meet there - which is what stops the
/// two halves of a split wall each mitring with the stem between them and meeting as a
/// notch.</item>
///
/// <item><b>The end runs into the side of another wall.</b> It is cut back to that wall's
/// near face, so it butts against it instead of pushing half a wall's width inside it. The
/// wall being run into is never altered: it simply passes through. A run that was split at the
/// junction counts as one wall for this purpose, which is why splitting a wall does not change
/// how the junction looks.</item>
///
/// <item><b>Exactly one wall turns a corner here.</b> Mitre, unless the mitre has run away
/// (see the mitre limit below).</item>
///
/// <item><b>Anything else</b> - a free end, or three walls meeting with no run through them -
/// is cut square and the walls overlap. Overlapping fills a junction solidly, and no single
/// mitre is right against two different neighbours.</item>
/// </list>
///
/// Cutting each layer at its own depth by function priority is a further refinement that is
/// deliberately not here: it was tried, and it misread common arrangements more often than it
/// improved the ordinary ones.
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
    /// default. It is deliberately strict: a 25 degree corner shown butted is a small
    /// compromise, while a shallow one let through draws a wall as a spike.
    /// </summary>
    private const double MiterLimit = 2.0;

    /// <summary>
    /// A mitre may also reach no further than this fraction of the shorter of the two walls.
    /// A corner longer than the wall it belongs to is never right, however wide the wall.
    /// </summary>
    private const double MiterLengthFraction = 0.5;

    /// <summary>The line along which each end of the wall is cut. A free end is cut square.</summary>
    public static (Line2D Start, Line2D End) GetEndCuts(BimDocument document, Wall wall, WallType type) =>
        (ComputeCut(document, wall, type, atStart: true),
         ComputeCut(document, wall, type, atStart: false));

    /// <summary>
    /// The four corners of a band running along the wall between two signed offsets from its
    /// body centreline, measured positive toward the exterior, with both ends cut to any
    /// joined neighbours.
    ///
    /// The whole wall is one band from +half to -half; a single layer is a narrower one.
    /// Both are cut by the same two lines, so layers stay aligned with the wall outline.
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
        Wall wall, WallType type, double outerOffset, double innerOffset, Line2D startCut, Line2D endCut)
    {
        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);
        var normal = wall.ExteriorNormal;
        var direction = wall.Direction;

        var outerEdge = new Line2D(bodyStart + normal * outerOffset, direction);
        var innerEdge = new Line2D(bodyStart + normal * innerOffset, direction);

        return new[]
        {
            CutOrFallback(outerEdge, startCut, wall, type, outerOffset, atStart: true),
            CutOrFallback(outerEdge, endCut, wall, type, outerOffset, atStart: false),
            CutOrFallback(innerEdge, endCut, wall, type, innerOffset, atStart: false),
            CutOrFallback(innerEdge, startCut, wall, type, innerOffset, atStart: true)
        };
    }

    /// <summary>
    /// Any other wall on the same level with an end at this point. Returns null when the end
    /// is free, or when the only neighbour runs straight on and needs no mitre.
    /// </summary>
    public static Wall? FindPartnerAt(BimDocument document, Wall wall, Point2D joint)
    {
        Wall? best = null;
        var bestTurn = CollinearTolerance;

        foreach (var candidate in document.Walls)
        {
            if (ReferenceEquals(candidate, wall)) continue;
            if (candidate.LevelId != wall.LevelId) continue;
            if (!TouchesAt(candidate, joint)) continue;

            // With several walls at one point, mitre against the one that turns hardest;
            // a wall carrying straight on has nothing to contribute to the corner.
            var turn = Math.Abs(wall.Direction.Cross(candidate.Direction));
            if (turn <= bestTurn) continue;

            best = candidate;
            bestTurn = turn;
        }

        return best;
    }

    public static bool TouchesAt(Wall wall, Point2D point) =>
        wall.Start.DistanceTo(point) <= JoinTolerance || wall.End.DistanceTo(point) <= JoinTolerance;

    // ---- who meets whom ---------------------------------------------------------

    /// <summary>Every other wall on this level with an end at this point.</summary>
    private static List<Wall> PartnersAt(BimDocument document, Wall wall, Point2D joint) =>
        document.Walls
            .Where(candidate => !ReferenceEquals(candidate, wall))
            .Where(candidate => candidate.LevelId == wall.LevelId)
            .Where(candidate => TouchesAt(candidate, joint))
            .ToList();

    /// <summary>Whether two walls run along the same line, whichever way each was drawn.</summary>
    private static bool AreInLine(Wall a, Wall b) =>
        Math.Abs(a.Direction.Cross(b.Direction)) <= CollinearTolerance;

    /// <summary>
    /// The wall this end runs into the side of, if any.
    ///
    /// That is either a wall whose body passes through the joint without ending there, or -
    /// and this is the case that matters in practice - two walls that end here and are in line
    /// with each other, which is what a run looks like after it has been split. Treating a
    /// split run as one wall is what makes splitting a wall leave the junction looking exactly
    /// as it did.
    /// </summary>
    private static Wall? FindRunThrough(
        BimDocument document, Wall wall, Point2D joint, List<Wall> partners)
    {
        foreach (var candidate in document.Walls)
        {
            if (ReferenceEquals(candidate, wall)) continue;
            if (candidate.LevelId != wall.LevelId) continue;

            // Ends here rather than passing through, so it is a corner, not a T.
            if (TouchesAt(candidate, joint)) continue;
            if (AreInLine(wall, candidate)) continue;

            if (Line2D.DistanceFromSegment(joint, candidate.Start, candidate.End) <= JoinTolerance)
                return candidate;
        }

        for (var i = 0; i < partners.Count; i++)
        for (var j = i + 1; j < partners.Count; j++)
        {
            if (!AreInLine(partners[i], partners[j])) continue;
            if (AreInLine(wall, partners[i])) continue;

            // Either half describes the same line; the longer one is the safer to measure
            // against if the two were somehow given different types.
            return partners[i].Length >= partners[j].Length ? partners[i] : partners[j];
        }

        return null;
    }

    /// <summary>
    /// A cut along the near face of the wall being run into, so this wall butts against it.
    ///
    /// Returns null when the butt would reach too far along the wall - a very shallow
    /// approach makes a long tapered end, which is the same failure the mitre limit exists to
    /// prevent - so the caller can fall back to a square end.
    /// </summary>
    private static Line2D? ButtCut(
        BimDocument document, Wall wall, WallType type, Wall run, Point2D joint, bool atStart, Line2D squareCut)
    {
        var runType = document.GetWallType(run);
        if (runType is null) return null;
        if (Math.Abs(wall.Direction.Cross(run.Direction)) <= CollinearTolerance) return null;

        var (runBodyStart, _) = run.GetBodyCentreline(runType.Structure);
        var across = run.Direction.PerpendicularLeft();

        // Which side this wall arrives from, taken from the direction it runs away down.
        var awayFromJoint = atStart ? wall.Direction : -wall.Direction;
        var side = awayFromJoint.Dot(across) >= 0 ? 1.0 : -1.0;

        var onCentreline = new Line2D(runBodyStart, run.Direction).ClosestPointTo(joint);
        var face = onCentreline + across * (side * runType.Width / 2);

        var cut = new Line2D(face, run.Direction);
        var budget = Math.Min(
            MiterLimit * Math.Max(type.Width, runType.Width),
            MiterLengthFraction * wall.Length);

        return CutStaysNearTheJoint(cut, wall, type, squareCut, budget) ? cut : null;
    }

    // ---- internals -------------------------------------------------------------

    private static Line2D ComputeCut(BimDocument document, Wall wall, WallType type, bool atStart)
    {
        var squareCut = SquareCut(wall, type, atStart);

        var joint = atStart ? wall.Start : wall.End;
        var partners = PartnersAt(document, wall, joint);

        // A wall in line with this one means the run carries on through the joint. There is no
        // corner to turn, so the end is square - and crucially this is decided before anything
        // else, so the two halves of a split wall stay square against each other instead of
        // each mitring with whatever stem meets them there.
        if (partners.Any(partner => AreInLine(wall, partner))) return squareCut;

        // Running into the side of another wall: stop at its face rather than pushing half a
        // wall's width inside it.
        if (FindRunThrough(document, wall, joint, partners) is { } run)
            return ButtCut(document, wall, type, run, joint, atStart, squareCut) ?? squareCut;

        // Only a single turning neighbour gives a corner that a mitre can be right about.
        if (partners.Count != 1) return squareCut;

        var partner = partners[0];

        var partnerType = document.GetWallType(partner);
        if (partnerType is null) return squareCut;

        // Directions pointing away from the corner along each wall.
        var awayFromJoint = atStart ? wall.Direction : -wall.Direction;
        var partnerAway = partner.Start.DistanceTo(joint) <= JoinTolerance
            ? partner.Direction
            : -partner.Direction;

        // The interior bisector of the corner. It vanishes when the walls are in line.
        var bisector = awayFromJoint + partnerAway;
        if (bisector.Length < CollinearTolerance) return squareCut;

        var (insideA, outsideA) = FaceLines(wall, type, bisector);
        var (insideB, outsideB) = FaceLines(partner, partnerType, bisector);

        // Faces on matching sides of the corner meet at the mitre's two corner points.
        if (!Line2D.TryIntersect(insideA, insideB, out var insideCorner)) return squareCut;
        if (!Line2D.TryIntersect(outsideA, outsideB, out var outsideCorner)) return squareCut;
        if (insideCorner.DistanceTo(outsideCorner) < CollinearTolerance) return squareCut;

        // Refuse a mitre that has run away from the corner, or the wall draws as a spike.
        var mitre = Line2D.Through(outsideCorner, insideCorner);
        return CutStaysNearTheJoint(mitre, wall, type, squareCut, Budget(type, partnerType, wall, partner))
            ? mitre
            : squareCut;
    }

    /// <summary>A flat end, perpendicular to the wall through its body centreline point.</summary>
    private static Line2D SquareCut(Wall wall, WallType type, bool atStart)
    {
        var (bodyStart, bodyEnd) = wall.GetBodyCentreline(type.Structure);
        return new Line2D(atStart ? bodyStart : bodyEnd, wall.Direction.PerpendicularLeft());
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
    /// the corners metres away, which is precisely how a wall ends up drawn as a spike. Only
    /// the distance along the wall is measured, since the offset across it is half the wall's
    /// width by construction.
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

    /// <summary>
    /// Where a band edge meets a cut. Parallel lines cannot meet, which happens when a mitre
    /// is degenerate; the square end is used instead so the wall still draws.
    /// </summary>
    private static Point2D CutOrFallback(
        Line2D edge, Line2D cut, Wall wall, WallType type, double offset, bool atStart)
    {
        if (Line2D.TryIntersect(edge, cut, out var point)) return point;

        var (bodyStart, bodyEnd) = wall.GetBodyCentreline(type.Structure);
        return (atStart ? bodyStart : bodyEnd) + wall.ExteriorNormal * offset;
    }
}
