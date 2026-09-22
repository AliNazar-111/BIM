namespace BIMDesigner.Core.Geometry;

/// <summary>
/// The shape of an elliptical wall, held relative to the wall's two ends the way a bulge holds
/// an arc (specification section 3.1, "elliptical walls").
///
/// The curve is the part of an ellipse from parameter <see cref="From"/> to <see cref="To"/>,
/// where a point at parameter t is <c>centre + a·cos t·u + b·sin t·v</c> and v is u turned a
/// quarter left. Only the ratio of the two semi-axes is kept: the size of the ellipse and the
/// direction of its axes both follow from where the ends are. So moving, rotating or scaling a
/// wall never has to touch this, and mirroring it just negates the two parameters.
///
/// Parameter rising from start to end turns left (anticlockwise), falling turns right.
/// </summary>
public readonly record struct WallEllipse(double Ratio, double From, double To)
{
    /// <summary>
    /// Half an ellipse whose axis is the line from the wall's start to its end: <paramref name="ratio"/>
    /// is the other semi-axis over half that line. It bows to the left of the direction of travel,
    /// or to the right when <paramref name="toLeft"/> is false.
    /// </summary>
    public static WallEllipse Half(double ratio, bool toLeft) =>
        toLeft ? new WallEllipse(ratio, Math.PI, 0) : new WallEllipse(ratio, Math.PI, 2 * Math.PI);

    /// <summary>Whether this describes a real piece of ellipse: a positive ratio and a turn short of a full circuit.</summary>
    public bool IsValid =>
        double.IsFinite(Ratio) && double.IsFinite(From) && double.IsFinite(To) && Ratio > 1e-6 &&
        Math.Abs(To - From) > 1e-6 && Math.Abs(To - From) < 2 * Math.PI - 1e-6;

    /// <summary>The same curve seen in a mirror.</summary>
    public WallEllipse Mirrored() => new(Ratio, -From, -To);
}
