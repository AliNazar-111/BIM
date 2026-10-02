using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The other ways Revit draws an arc wall: a tangent arc carrying on from the wall before it, an
/// arc from its centre and two ends, and a fillet arc rounding off the corner between two walls -
/// which is also what drawing straight walls with a radius set does at every corner.
/// </summary>
public static class WallArcs
{
    /// <summary>
    /// The bulge of the arc from a point to another that leaves the first along a direction:
    /// the arc carrying on smoothly from a wall that arrives there going that way.
    /// </summary>
    public static double TangentBulge(Vector2D tangent, Point2D from, Point2D to)
    {
        var chord = to - from;
        if (chord.Length < 1e-9 || tangent.Length < 1e-9) return 0;

        // The arc turns through twice the angle between the tangent and the chord.
        var turn = Math.Atan2(tangent.Cross(chord), tangent.Dot(chord));
        return Math.Tan(turn / 2);
    }

    /// <summary>
    /// An arc from its centre and two ends, the shorter way round: its radius from the centre to
    /// the start, its end on that circle where the third point lies. Null where the points are on
    /// the centre or the ends coincide.
    /// </summary>
    public static (Point2D Start, Point2D End, double Bulge)? CentreEnds(Point2D centre, Point2D start, Point2D towards)
    {
        var radius = start.DistanceTo(centre);
        var way = towards - centre;
        if (radius < 1 || way.Length < 1e-9) return null;

        var end = centre + way * (radius / way.Length);
        var (a, b) = (start - centre, end - centre);
        var sweep = Math.Atan2(a.Cross(b), a.Dot(b));
        if (Math.Abs(sweep) < 1e-6) return null;

        return (start, end, Math.Tan(sweep / 4));
    }

    /// <summary>
    /// What rounding off the corner between two straight walls with an arc of a radius makes of
    /// them: each wall cut back to where the arc leaves it, and the arc between. The corner is
    /// where their lines meet; each keeps the end away from it. Null, with why, where they are
    /// parallel, curved, or too short for the radius.
    /// </summary>
    public static Fillet? Round(Wall first, Wall second, double radius, out string? problem)
    {
        problem = null;
        if (first.IsCurved || second.IsCurved)
        {
            problem = "Only straight walls can be rounded off with a fillet arc.";
            return null;
        }

        if (radius <= 0)
        {
            problem = "Set a radius greater than nothing.";
            return null;
        }

        var (a, b) = (Line2D.Through(first.Start, first.End), Line2D.Through(second.Start, second.End));
        if (!Line2D.TryIntersect(a, b, out var corner))
        {
            problem = "Those walls are parallel: there is no corner to round off.";
            return null;
        }

        // Each wall keeps the end further from the corner, and runs away from it toward that end.
        var firstKeepsStart = first.Start.DistanceTo(corner) >= first.End.DistanceTo(corner);
        var secondKeepsStart = second.Start.DistanceTo(corner) >= second.End.DistanceTo(corner);
        var firstKept = firstKeepsStart ? first.Start : first.End;
        var secondKept = secondKeepsStart ? second.Start : second.End;
        var (u, v) = ((firstKept - corner).NormalisedOrDefault(Vector2D.UnitX), (secondKept - corner).NormalisedOrDefault(Vector2D.UnitX));

        var angle = Math.Acos(Math.Clamp(u.Dot(v), -1, 1));
        if (angle < 1e-3 || angle > Math.PI - 1e-3)
        {
            problem = "Those walls carry straight on from each other: there is no corner to round off.";
            return null;
        }

        // How far back from the corner the arc leaves each wall.
        var back = radius / Math.Tan(angle / 2);
        if (back >= firstKept.DistanceTo(corner) - 1 || back >= secondKept.DistanceTo(corner) - 1)
        {
            problem = $"A radius of {Units.FormatLength(radius)} needs {Units.FormatLength(back)} of each wall back from the corner, and one of them is shorter than that.";
            return null;
        }

        var (onFirst, onSecond) = (corner + u * back, corner + v * back);

        // Arriving along the first wall toward the corner, the arc turns toward the second.
        var bulge = Math.Tan((Math.PI - angle) / 4) * Math.Sign((-u).Cross(v));

        return new Fillet(
            firstKeepsStart ? (first.Start, onFirst) : (onFirst, first.End),
            secondKeepsStart ? (second.Start, onSecond) : (onSecond, second.End),
            onFirst, onSecond, bulge, corner);
    }

    /// <summary>A rounded corner: where each wall now runs, and the arc between them.</summary>
    public sealed record Fillet(
        (Point2D Start, Point2D End) First, (Point2D Start, Point2D End) Second,
        Point2D ArcStart, Point2D ArcEnd, double Bulge, Point2D Corner);
}
