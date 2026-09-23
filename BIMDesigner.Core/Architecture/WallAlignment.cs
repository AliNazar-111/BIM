using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Lining a wall up with the face of a thicker or thinner one it runs into.
///
/// Two walls of different thickness drawn along the same line both sit on that line, so the
/// thinner one ends up in the middle of the thicker one, standing off both its faces. On a
/// building that is almost never what is wanted: a shopfront set between two masonry piers, or
/// a stud partition meeting a blockwork wall, runs flush with one face and steps on the other.
///
/// The fix is the wall's own <see cref="Wall.AcrossOffset"/>, so the wall really does sit
/// where it is drawn and everything downstream - joins, rooms, 3D, schedules - sees the same
/// wall. Nothing here guesses at a wall that is not actually running into another one.
/// </summary>
public static class WallAlignment
{
    /// <summary>How far from collinear two walls may run and still count as one line, in degrees.</summary>
    public const double CollinearAngle = 20;

    /// <summary>A difference in half-width smaller than this is not worth shifting a wall for.</summary>
    public const double Tolerance = 1;

    /// <summary>Which face of a wall another one lines up with.</summary>
    public enum Face
    {
        Interior,
        Exterior
    }

    /// <summary>
    /// The <see cref="Wall.AcrossOffset"/> this wall needs for the named face to run flush with
    /// the same face of a wall it meets end to end, or null if it meets none or is already
    /// flush. The wall it meets is left alone: the one that moves is the one being aligned.
    /// </summary>
    public static double? OffsetFor(BimDocument document, Wall wall, Face face = Face.Interior)
    {
        if (document.GetWallType(wall) is not { } type || type.Structure.TotalWidth <= 0) return null;
        if (Neighbour(document, wall) is not (var other, var otherType, var sameWay)) return null;

        var half = type.Structure.TotalWidth / 2;
        var otherHalf = otherType.Structure.TotalWidth / 2;
        if (Math.Abs(half - otherHalf) <= Tolerance) return null;

        // Where the other wall's body centre sits across its own line, exterior positive, and
        // in this wall's sense of which way the exterior is.
        var theirs = -other.GetLocationLineOffset(otherType.Structure) * (sameWay ? 1 : -1);

        // The two faces coincide when this wall's body centre sits half a wall further in.
        var toward = face == Face.Interior ? -1 : 1;
        var wanted = theirs + toward * (otherHalf - half);

        // AcrossOffset is what is left once the location line rule has had its say.
        var offset = wanted + wall.GetLocationLineOffset(type.Structure) + wall.AcrossOffset;
        return Math.Abs(offset - wall.AcrossOffset) <= Tolerance ? null : offset;
    }

    /// <summary>Whether this wall runs into another end to end, so that lining it up means anything.</summary>
    public static bool Joins(BimDocument document, Wall wall) => Neighbour(document, wall) is not null;

    /// <summary>
    /// The wall this one runs into end to end: their ends meet, and they carry on in much the
    /// same line rather than turning a corner. The nearer match wins when there are two.
    /// </summary>
    private static (Wall Wall, WallType Type, bool SameWay)? Neighbour(BimDocument document, Wall wall)
    {
        if (wall.Length <= WallJoins.JoinTolerance) return null;

        var mine = (wall.End - wall.Start).NormalisedOrDefault(Geometry.Vector2D.UnitX);

        foreach (var end in new[] { wall.Start, wall.End })
        foreach (var other in document.Walls)
        {
            if (ReferenceEquals(other, wall) || other.LevelId != wall.LevelId || other.Length <= WallJoins.JoinTolerance) continue;
            if (document.GetWallType(other) is not { } otherType || otherType.Structure.TotalWidth <= 0) continue;

            if (other.Start.DistanceTo(end) > WallJoins.JoinTolerance && other.End.DistanceTo(end) > WallJoins.JoinTolerance) continue;

            // Running on much the same line, rather than turning a corner.
            var theirs = (other.End - other.Start).NormalisedOrDefault(Geometry.Vector2D.UnitX);
            var along = mine.Dot(theirs);
            if (Math.Acos(Math.Clamp(Math.Abs(along), -1, 1)) * 180 / Math.PI > CollinearAngle) continue;

            // Drawn the same way round, or head to head - which swaps which side is the exterior.
            var sameWay = along >= 0 == (wall.Flipped == other.Flipped);
            return (other, otherType, sameWay);
        }

        return null;
    }
}
