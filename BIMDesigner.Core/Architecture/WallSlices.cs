using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A stretch of wall between two cuts: the shape actually drawn or built.
///
/// A wall is not one solid. Its ends are cut to its neighbours, and its doors and windows
/// divide what is left, so the thing on the drawing is a series of these.
/// </summary>
public readonly record struct WallSlice(double From, double To, WallCut CutFrom, WallCut CutTo);

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
    /// The side of an opening: square across the wall, with the type's wrapping at inserts so
    /// the finishes return into the reveal.
    /// </summary>
    public static WallCut JambAt(Wall wall, WallType type, double distance) =>
        WallCut.Along(CrossCutAt(wall, type, distance), wall, WallEndCondition.Jamb, type.WrapAtInserts);

    /// <summary>
    /// One stretch of wall, with the cut at each end: the join treatment where the stretch
    /// reaches the wall's own end, and a jamb anywhere else.
    ///
    /// Returns null when the stretch has been consumed by the joins at its ends. A wall that
    /// butts into another is shortened, and a short piece of wall beside a door can be
    /// shortened past nothing; drawing it anyway would turn the outline inside out, and an
    /// inside-out outline is what draws as crossing triangles.
    /// </summary>
    public static WallSlice? Between(
        BimDocument document, Wall wall, WallType type, double from, double to,
        WallCut startCut, WallCut endCut) =>
        Piece(wall, type, from, to, null, null, startCut, endCut);

    /// <summary>
    /// A stretch whose ends may be given: a crossing wall supplies its own faces as the cuts.
    /// An end not given is the wall's own end if it is there, and a jamb anywhere else.
    /// </summary>
    private static WallSlice? Piece(
        Wall wall, WallType type, double from, double to,
        WallCut? givenFrom, WallCut? givenTo, WallCut startCut, WallCut endCut)
    {
        if (to - from <= WallJoins.JoinTolerance) return null;

        var cutFrom = givenFrom ?? (from <= WallJoins.JoinTolerance ? startCut : JambAt(wall, type, from));
        var cutTo = givenTo ?? (to >= wall.Length - WallJoins.JoinTolerance ? endCut : JambAt(wall, type, to));

        return IsInsideOut(wall, type, cutFrom, cutTo) ? null : new WallSlice(from, to, cutFrom, cutTo);
    }

    /// <summary>
    /// Every solid stretch of the wall, with its doors and windows taken out and any heavier
    /// wall crossing it cut through.
    /// </summary>
    public static IReadOnlyList<WallSlice> Solid(BimDocument document, Wall wall, WallType type)
    {
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var crossings = WallJoins.Crossings(document, wall, type);
        var slices = new List<WallSlice>();

        void Add(double from, double to, WallCut? cutFrom, WallCut? cutTo)
        {
            if (Piece(wall, type, from, to, cutFrom, cutTo, startCut, endCut) is { } slice) slices.Add(slice);
        }

        foreach (var (from, to) in WallOpenings.GetSolidRuns(document, wall))
        {
            var cursor = from;
            WallCut? cursorCut = null;

            foreach (var crossing in crossings.Where(c => c.To > from && c.From < to))
            {
                if (crossing.From > cursor) Add(cursor, crossing.From, cursorCut, crossing.Before);

                if (crossing.To > cursor)
                {
                    cursor = crossing.To;
                    cursorCut = crossing.After;
                }
            }

            if (cursor < to) Add(cursor, to, cursorCut, null);
        }

        return slices;
    }

    /// <summary>
    /// Whether the two cuts have crossed, so that the stretch between them has no length left
    /// - or has turned over and would be drawn back to front.
    ///
    /// Measured on the wall's own two faces: an angled cut can leave the centreline looking
    /// fine while one face has already passed the other.
    /// </summary>
    private static bool IsInsideOut(Wall wall, WallType type, WallCut cutFrom, WallCut cutTo)
    {
        var half = type.Width / 2;
        var (bodyStart, _) = wall.GetBodyCentreline(type.Structure);

        var start = WallJoins.EndPoints(wall, type, half, -half, cutFrom, atStart: true);
        var end = WallJoins.EndPoints(wall, type, half, -half, cutTo, atStart: false);

        double Along(Point2D point) => (point - bodyStart).Dot(wall.Direction);

        // The two ends of each face, which must still run the right way along the wall.
        return Along(end[0]) - Along(start[0]) <= 0 || Along(end[^1]) - Along(start[^1]) <= 0;
    }
}
