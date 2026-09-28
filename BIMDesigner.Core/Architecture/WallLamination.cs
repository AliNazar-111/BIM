using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Walls laid face to face and joined (specification section 3.1, "auto join and lock walls"):
/// a brick lining against a stud wall, an accent wall on one side of a room. A door or window in
/// either cuts through both, and when the join is locked the two move together.
///
/// The join is kept on the newer wall, as the ids of the walls it was laid against, so it
/// survives saving and undo like any other property; nothing is stored on the older wall.
/// </summary>
public static class WallLamination
{
    /// <summary>How far apart two faces may be and still count as touching. Millimetres.</summary>
    public const double FaceTolerance = 1.0;

    /// <summary>The least two faces must overlap along their length to be joined. Millimetres.</summary>
    public const double MinimumOverlap = 10;

    /// <summary>
    /// Whether a wall can be joined face to face: an ordinary layered wall. Curtain and stacked
    /// walls are left out, as in Revit.
    /// </summary>
    public static bool CanJoin(BimDocument document, Wall wall) =>
        !document.IsCurtainWall(wall) &&
        document.FindType<WallType>(wall.TypeId) is not null;

    /// <summary>Whether a joined wall may also be locked: not a leaning one, whose faces are not where its plan shows them.</summary>
    public static bool CanLock(Wall wall) => wall.CrossSection == WallCrossSection.Vertical;

    /// <summary>
    /// The walls a wall's side faces lie against, overlapping along their length - the ones a
    /// newly placed wall joins. Straight walls running the same way, face on face.
    /// </summary>
    public static IReadOnlyList<Wall> Touching(BimDocument document, Wall wall)
    {
        if (!CanJoin(document, wall) || wall.IsCurved || document.GetWallType(wall) is not { } type) return Array.Empty<Wall>();

        var found = new List<Wall>();
        foreach (var other in document.Walls)
        {
            if (ReferenceEquals(other, wall) || other.LevelId != wall.LevelId || other.IsCurved) continue;
            if (!CanJoin(document, other) || document.GetWallType(other) is not { } otherType) continue;
            if (Math.Abs(wall.Direction.Cross(other.Direction)) > 1e-6) continue;

            // Their body centrelines are as far apart as their two half widths.
            var middle = other.PointAt(otherType.Structure, other.Length / 2, 0);
            var (_, across) = wall.Locate(type.Structure, middle);
            if (Math.Abs(Math.Abs(across) - (type.Width + otherType.Width) / 2) > FaceTolerance) continue;

            // And they overlap along their length.
            var a = wall.Locate(type.Structure, other.Start).Along;
            var b = wall.Locate(type.Structure, other.End).Along;
            var overlap = Math.Min(wall.Length, Math.Max(a, b)) - Math.Max(0, Math.Min(a, b));
            if (overlap >= MinimumOverlap) found.Add(other);
        }

        return found;
    }

    /// <summary>How far apart two parallel walls may be and still be joined with Join Geometry, as in Revit: 150 mm.</summary>
    public const double JoinGeometryGap = 150;

    /// <summary>
    /// Whether Join Geometry can join two walls (specification section 3.1, "join parallel
    /// walls"): ordinary walls, straight and parallel, overlapping along their length, with no
    /// more than <see cref="JoinGeometryGap"/> between their faces.
    /// </summary>
    public static bool CanJoinGeometry(BimDocument document, Wall wall, Wall other)
    {
        if (ReferenceEquals(wall, other) || wall.LevelId != other.LevelId || wall.IsCurved || other.IsCurved) return false;
        if (!CanJoin(document, wall) || !CanJoin(document, other)) return false;
        if (document.GetWallType(wall) is not { } type || document.GetWallType(other) is not { } otherType) return false;
        if (Math.Abs(wall.Direction.Cross(other.Direction)) > 1e-6) return false;

        var (_, across) = wall.Locate(type.Structure, other.PointAt(otherType.Structure, other.Length / 2, 0));
        var gap = Math.Abs(across) - (type.Width + otherType.Width) / 2;
        if (gap < -FaceTolerance || gap > JoinGeometryGap) return false;

        var a = wall.Locate(type.Structure, other.Start).Along;
        var b = wall.Locate(type.Structure, other.End).Along;
        return Math.Min(wall.Length, Math.Max(a, b)) - Math.Max(0, Math.Min(a, b)) >= MinimumOverlap;
    }

    /// <summary>The walls joined face to face with this one, whichever of the two holds the join.</summary>
    public static IReadOnlyList<Wall> Partners(BimDocument document, Wall wall) =>
        document.Walls
            .Where(other => !ReferenceEquals(other, wall) && (wall.JoinedTo.Contains(other.Id) || other.JoinedTo.Contains(wall.Id)))
            .ToList();

    /// <summary>
    /// Everything that moves with a wall because of locked joins, the wall itself included:
    /// followed on from partner to partner, since a lining locked to a wall locked to another
    /// moves with both.
    /// </summary>
    public static IReadOnlyList<Wall> LockedGroup(BimDocument document, Wall wall)
    {
        var group = new List<Wall> { wall };
        for (var i = 0; i < group.Count; i++)
        {
            var current = group[i];
            foreach (var other in document.Walls)
            {
                if (group.Contains(other)) continue;
                var locked = (current.LockedToJoined && current.JoinedTo.Contains(other.Id)) ||
                             (other.LockedToJoined && other.JoinedTo.Contains(current.Id));
                if (locked) group.Add(other);
            }
        }

        return group;
    }
}

/// <summary>A hole through a wall with nothing of the wall's own in it: from, to along it, and sill and head above its base.</summary>
public readonly record struct WallHole(double From, double To, double Sill, double Head);

/// <summary>
/// The holes in a wall that are not its own doors and windows: a curtain wall embedded in it,
/// wall openings cut through it, and the doors and windows of the walls joined to its faces,
/// which cut through both.
/// </summary>
public static class WallHoles
{
    public static IReadOnlyList<WallHole> Of(BimDocument document, Wall wall)
    {
        var holes = CurtainEmbedding.In(document, wall)
            .Select(hole => new WallHole(hole.From, hole.To, hole.Sill, hole.Head))
            .ToList();

        // Openings cut through the wall on their own.
        holes.AddRange(document.Elements.OfType<WallOpening>().Where(o => o.HostWallId == wall.Id).Select(o => o.Hole(wall)).Where(h => h.To - h.From > WallJoins.JoinTolerance));

        if (!WallLamination.CanJoin(document, wall) || document.GetWallType(wall) is not { } type) return holes;

        var baseElevation = wall.GetBaseElevation(document);
        foreach (var partner in WallLamination.Partners(document, wall))
        {
            var shift = partner.GetBaseElevation(document) - baseElevation;
            foreach (var opening in WallOpenings.Of(document, partner))
            {
                if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType) continue;

                // Where the partner's opening is, measured along this wall.
                var centre = wall.Locate(type.Structure, opening.GetCentre(partner)).Along;
                var from = Math.Max(0, centre - opening.WidthOf(openingType) / 2);
                var to = Math.Min(wall.Length, centre + opening.WidthOf(openingType) / 2);
                if (to - from <= WallJoins.JoinTolerance) continue;

                var (placedSill, placedHeight) = opening.Placed(document, openingType, partner);

                holes.Add(new WallHole(from, to, shift + placedSill, shift + placedSill + placedHeight));
            }
        }

        return holes;
    }
}

/// <summary>
/// Placing walls against walls already there (specification section 3.1, "place by segment",
/// "place by room"): along one face of a wall, or round every face of a room.
/// </summary>
public static class WallPlacement
{
    /// <summary>
    /// A wall laid along one face of another, its full length between the walls it meets, and
    /// on the side clicked. It runs the same way and faces the same way as the wall it lines,
    /// and its location line is its own face against that wall - so changing its type
    /// thickens it away from the wall rather than into it. Null when the wall cannot be lined.
    /// </summary>
    public static Wall? BySegment(BimDocument document, Wall host, bool exteriorSide, Guid typeId, Guid levelId)
    {
        if (document.GetWallType(host) is not { } hostType || document.IsCurtainWall(host)) return null;

        var half = hostType.Width / 2;
        var side = exteriorSide ? 1.0 : -1.0;

        // The face runs between the walls joined at either end: shorter inside a corner,
        // longer round the outside of one.
        var face = WallJoins.GetBandOutline(document, host, hostType, half, -half)
            .Select(point => host.Locate(hostType.Structure, point))
            .Where(p => Math.Abs(p.Across - side * half) <= WallLamination.FaceTolerance)
            .Select(p => p.Along)
            .ToList();
        var (from, to) = face.Count >= 2 ? (face.Min(), face.Max()) : (0.0, host.Length);
        if (to - from <= WallJoins.JoinTolerance) return null;

        var curve = host.LocationCurve.Part(from, to).Offset(host.LeftOf(hostType.Structure, side * half));
        return new Wall
        {
            Start = curve.Start,
            End = curve.End,
            Bulge = curve.Bulge,
            Ellipse = curve.Ellipse,
            Spline = curve.Spline,
            TypeId = typeId,
            LevelId = levelId,
            Flipped = host.Flipped,
            LocationLine = exteriorSide ? WallLocationLine.FinishFaceInterior : WallLocationLine.FinishFaceExterior
        };
    }

    /// <summary>
    /// Walls round the inside of a room, one along each straight run of its outline, their
    /// exterior faces on the faces of the walls that bound it and meeting at its corners.
    /// </summary>
    public static IReadOnlyList<Wall> ByRoom(IReadOnlyList<Point2D> outline, Guid typeId, Guid levelId)
    {
        if (outline.Count < 3) return Array.Empty<Wall>();

        // Clockwise, so the exterior of each wall - its left - is toward the wall it lines.
        var ring = Polygon2D.SignedArea(outline) > 0 ? outline.Reverse().ToList() : outline.ToList();

        // One wall per straight run: points where the outline goes straight on are dropped.
        var corners = ring
            .Where((point, i) =>
            {
                var before = point - ring[(i - 1 + ring.Count) % ring.Count];
                var after = ring[(i + 1) % ring.Count] - point;
                return before.Length > 1e-6 && after.Length > 1e-6 &&
                       Math.Abs(before.Cross(after)) > 1e-4 * before.Length * after.Length;
            })
            .ToList();

        return corners
            .Select((corner, i) => (Start: corner, End: corners[(i + 1) % corners.Count]))
            .Where(run => run.Start.DistanceTo(run.End) > WallJoins.JoinTolerance)
            .Select(run => new Wall
            {
                Start = run.Start,
                End = run.End,
                TypeId = typeId,
                LevelId = levelId,
                LocationLine = WallLocationLine.FinishFaceExterior
            })
            .ToList();
    }
}

/// <summary>
/// Where two walls laid face to face meet, for the padlock drawn there: a quarter of the way
/// along the face they share, clear of the grips and the length label in the middle.
/// </summary>
public static class WallLockPoint
{
    public static Point2D? Between(BimDocument document, Wall wall, Wall partner)
    {
        if (document.GetWallType(wall) is not { } type || document.GetWallType(partner) is not { } partnerType) return null;

        var a = wall.Locate(type.Structure, partner.PointAt(partnerType.Structure, 0, 0)).Along;
        var b = wall.Locate(type.Structure, partner.PointAt(partnerType.Structure, partner.Length, 0)).Along;
        var from = Math.Max(0, Math.Min(a, b));
        var to = Math.Min(wall.Length, Math.Max(a, b));
        var middle = from < to ? from + (to - from) / 4 : wall.Length / 4;

        // On this wall's face toward the partner.
        var side = Math.Sign(wall.Locate(type.Structure, partner.PointAt(partnerType.Structure, partner.Length / 2, 0)).Across);
        return wall.PointAt(type.Structure, middle, (side == 0 ? 1 : side) * type.Width / 2);
    }

    /// <summary>Whether two walls are locked together, whichever of them holds the join.</summary>
    public static bool AreLocked(Wall wall, Wall partner) =>
        (wall.LockedToJoined && wall.JoinedTo.Contains(partner.Id)) ||
        (partner.LockedToJoined && partner.JoinedTo.Contains(wall.Id));
}

/// <summary>
/// Locked corners (specification section 3.1, "wall joins"): where walls meet end to end and
/// the corner is locked, moving any of them stretches the others so the corner stays a corner.
/// The lock is kept on each wall end at the corner, so it survives saving, copying and undo.
/// </summary>
public static class WallJointLock
{
    /// <summary>Whether the corner at a point is locked: any wall end there locked.</summary>
    public static bool IsLocked(BimDocument document, Guid levelId, Point2D point) =>
        WallCorners.At(document, levelId, point).Any(end => end.AtStart ? end.Wall.StartLocked : end.Wall.EndLocked);

    /// <summary>Whether a point is a corner at all: two or more walls ending there.</summary>
    public static bool IsCorner(BimDocument document, Guid levelId, Point2D point) =>
        WallCorners.At(document, levelId, point).Count >= 2;

    /// <summary>
    /// The wall ends that follow when these walls move: at each locked corner of theirs, the
    /// ends of the walls not moving with them, which stretch to stay on the corner.
    /// </summary>
    public static IReadOnlyList<(Wall Wall, bool AtStart)> Followers(BimDocument document, IReadOnlyCollection<Wall> moving)
    {
        var followers = new List<(Wall, bool)>();
        foreach (var wall in moving)
        foreach (var atStart in new[] { true, false })
        {
            var point = atStart ? wall.Start : wall.End;
            if (!IsLocked(document, wall.LevelId, point)) continue;

            foreach (var end in WallCorners.At(document, wall.LevelId, point))
                if (!moving.Contains(end.Wall) && !followers.Contains((end.Wall, end.AtStart))) followers.Add((end.Wall, end.AtStart));
        }

        return followers;
    }
}
