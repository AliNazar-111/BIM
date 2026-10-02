using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>What happens at one end of a wall, or at one side of a door or window in it.</summary>
public enum WallEndCondition
{
    /// <summary>Nothing meets it. The end is exposed, drawn, and may be wrapped.</summary>
    Free,

    /// <summary>Another wall carries straight on from it; the two read as one run.</summary>
    Continues,

    /// <summary>It stops against the face of a wall that runs past.</summary>
    Butt,

    /// <summary>It carries on past a wall that stops against it, flush with that wall's far face.</summary>
    RunsThrough,

    /// <summary>It meets one other wall on the bisector of the corner.</summary>
    Mitre,

    /// <summary>Three or more walls meet here and share the junction between them.</summary>
    Shared,

    /// <summary>Walls meet here but no clean join could be found, so they overlap.</summary>
    Overlap,

    /// <summary>The user has told this end not to join anything. It is treated as free.</summary>
    Disallowed,

    /// <summary>The side of a door or window opening.</summary>
    Jamb
}

/// <summary>
/// Which layers turn the corner at an exposed end or around an opening (specification section
/// 3.1, "wrapping at inserts and ends"). Only layers outside the core, and marked as wrapping,
/// ever wrap.
/// </summary>
public enum WallWrapping
{
    None,
    Exterior,
    Interior,
    Both
}

/// <summary>How the user wants one end of a wall joined (specification section 2.4).</summary>
public enum WallJoinKind
{
    /// <summary>Worked out from what meets the end: a mitre for a corner, a butt for a tee.</summary>
    Auto,
    Mitre,

    /// <summary>This wall stops against the other.</summary>
    Butt,

    /// <summary>This wall carries on, and the other stops against it.</summary>
    RunThrough,

    /// <summary>This wall carries on with a square end, and the other stops against it.</summary>
    SquareOff,

    /// <summary>This end joins nothing.</summary>
    Disallow
}

/// <summary>
/// The line a wall's end is cut along, and what kind of end it is.
///
/// Usually a straight line, but where three or more walls meet it is two lines meeting at the
/// middle of the junction, so that the walls between them fill it exactly once. The points
/// run across the wall from its exterior side to its interior side, and the first and last
/// segments extend indefinitely, so any band of the wall can be cut by it.
/// </summary>
public sealed class WallCut
{
    public WallCut(IReadOnlyList<Point2D> points, WallEndCondition condition, WallWrapping wrapping = WallWrapping.None)
    {
        if (points.Count < 2) throw new ArgumentException("A cut needs at least two points.", nameof(points));

        Points = points;
        Condition = condition;
        Wrapping = wrapping;
    }

    public IReadOnlyList<Point2D> Points { get; }

    public WallEndCondition Condition { get; }

    public WallWrapping Wrapping { get; }

    /// <summary>
    /// For an end stopping against another wall's face: how far each of this wall's layers goes
    /// on into that wall, past the face, by the layers' join priorities - as (outer, inner)
    /// offsets of the layer from this wall's middle, and the depth. Empty for any other cut.
    /// </summary>
    public IReadOnlyList<(double Outer, double Inner, double Depth)> LayerDepths { get; init; } = Array.Empty<(double, double, double)>();

    /// <summary>Which way is into the wall this end stops against, square to its face.</summary>
    public Vector2D Into { get; init; }

    /// <summary>The wall this end stops against, where its layers go on into it; empty otherwise.</summary>
    public Guid Against { get; init; }

    /// <summary>
    /// The cut one layer of the wall ends at: the face this end stops against, or deeper into
    /// that wall where the layer's priority takes it on past layers of lower priority.
    /// </summary>
    public WallCut ForBand(double outer, double inner)
    {
        if (LayerDepths.Count == 0 || !IsStraight) return this;

        var middle = (outer + inner) / 2;
        foreach (var (o, i, depth) in LayerDepths)
        {
            if (middle > Math.Max(o, i) + 1e-6 || middle < Math.Min(o, i) - 1e-6) continue;
            return depth <= 1e-6 ? this : new WallCut(Points.Select(point => point + Into * depth).ToList(), Condition, Wrapping);
        }

        return this;
    }

    /// <summary>A plain line: the only kind of cut an opening or a free end has.</summary>
    public bool IsStraight => Points.Count == 2;

    /// <summary>
    /// Whether the end is part of a join. A joined end is not drawn as a line: where walls
    /// meet they read as one mass, as they would on a finished drawing.
    /// </summary>
    public bool IsJoined => Condition is not (WallEndCondition.Free or WallEndCondition.Disallowed or WallEndCondition.Jamb);

    /// <summary>A straight cut along a line, with its points put in exterior-first order.</summary>
    public static WallCut Along(
        Line2D line, Wall wall, WallEndCondition condition, WallWrapping wrapping = WallWrapping.None)
    {
        var a = line.Origin;
        var b = line.Origin + line.Direction;

        return (b - a).Dot(wall.ExteriorNormal) > 0
            ? new WallCut(new[] { b, a }, condition, wrapping)
            : new WallCut(new[] { a, b }, condition, wrapping);
    }

    /// <summary>The line of a straight cut.</summary>
    public Line2D Line => Line2D.Through(Points[0], Points[1]);

    public WallCut With(WallEndCondition condition) =>
        new(Points, condition, Wrapping) { LayerDepths = LayerDepths, Into = Into, Against = Against };

    /// <summary>The same cut with its layers cut straight through rather than turned round it.</summary>
    public WallCut Unwrapped() => Wrapping == WallWrapping.None ? this : new WallCut(Points, Condition) { LayerDepths = LayerDepths, Into = Into, Against = Against };
}

/// <summary>
/// How a wall join is drawn in plan (specification section 3.1, "wall join cleanup"): cleaned,
/// with no line across it so the walls read as one mass; not cleaned, each wall drawn whole and
/// butting the next; or as the view's Wall Join Display says.
/// </summary>
public enum WallJoinCleanup
{
    UseViewSetting,
    Clean,
    DontClean
}

/// <summary>A view's rule for cleaning wall joins that are left to it: every join, or only those between walls of one type.</summary>
public enum WallJoinDisplay
{
    CleanAllWallJoins,
    CleanSameTypeWallJoins
}
