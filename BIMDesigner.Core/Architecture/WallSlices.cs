using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A stretch of wall between two cuts: the shape actually drawn or built.
///
/// A wall is not one solid. Its ends are cut to its neighbours, and its doors and windows
/// divide what is left, so the thing on the drawing is a series of these.
/// </summary>
public readonly record struct WallSlice(double From, double To, Line2D CutFrom, Line2D CutTo);

/// <summary>
/// Cuts a wall into the pieces that are actually drawn (specification sections 2.4 and 2.5).
///
/// This lives here rather than in a renderer because the plan, the 3D model and anything else
/// that draws a wall have to agree about where it starts and stops. Working it out twice is
/// how two views of one wall come to disagree.
/// </summary>
public static class WallSlices
{
    /// <summary>A line straight across the wall, a given distance along it.</summary>
    public static Line2D CrossCutAt(Wall wall, WallType type, double distance)
    {
        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);
        return new Line2D(bodyStart + wall.Direction * distance, wall.Direction.PerpendicularLeft());
    }

    /// <summary>
    /// One stretch of wall, with the cut at each end: the join treatment where the stretch
    /// reaches the wall's own end, and a square cut across it anywhere else - which is what a
    /// door jamb is.
    ///
    /// Returns null when the stretch has been consumed by the joins at its ends. A wall that
    /// butts into another is shortened, and a short piece of wall beside a door can be
    /// shortened past nothing; drawing it anyway would turn the outline inside out, and an
    /// inside-out outline is what draws as crossing triangles.
    /// </summary>
    public static WallSlice? Between(
        BimDocument document, Wall wall, WallType type, double from, double to,
        Line2D startCut, Line2D endCut)
    {
        if (to - from <= WallJoins.JoinTolerance) return null;

        var cutFrom = from <= WallJoins.JoinTolerance ? startCut : CrossCutAt(wall, type, from);
        var cutTo = to >= wall.Length - WallJoins.JoinTolerance ? endCut : CrossCutAt(wall, type, to);

        return IsInsideOut(wall, type, cutFrom, cutTo) ? null : new WallSlice(from, to, cutFrom, cutTo);
    }

    /// <summary>Every solid stretch of the wall, with its doors and windows taken out.</summary>
    public static IReadOnlyList<WallSlice> Solid(BimDocument document, Wall wall, WallType type)
    {
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var slices = new List<WallSlice>();

        foreach (var (from, to) in WallOpenings.GetSolidRuns(document, wall))
            if (Between(document, wall, type, from, to, startCut, endCut) is { } slice)
                slices.Add(slice);

        return slices;
    }

    /// <summary>
    /// Whether the two cuts have crossed, so that the stretch between them has no length left
    /// - or has turned over and would be drawn back to front.
    ///
    /// Measured on the wall's own two faces rather than on its centreline: an angled cut can
    /// leave the centreline looking fine while one face has already passed the other.
    /// </summary>
    private static bool IsInsideOut(Wall wall, WallType type, Line2D cutFrom, Line2D cutTo)
    {
        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);
        var normal = wall.ExteriorNormal;
        var half = type.Width / 2;

        foreach (var offset in new[] { half, -half })
        {
            var edge = new Line2D(bodyStart + normal * offset, wall.Direction);

            if (!Line2D.TryIntersect(edge, cutFrom, out var start)) return true;
            if (!Line2D.TryIntersect(edge, cutTo, out var end)) return true;

            if ((end - start).Dot(wall.Direction) <= 0) return true;
        }

        return false;
    }
}
