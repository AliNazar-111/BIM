using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What a column does between its base and its top (specification section 16).
///
/// A column is a section run up a path, and these are the things that make the run something
/// other than a straight prism: the section growing or shrinking, turning, leaning, and the
/// column being built in parts with a base and a capital. They are parameters rather than
/// geometry, so a classical column is a set of numbers that regenerates when its height
/// changes - not a mesh that was right once and is wrong afterwards.
/// </summary>
public sealed class ColumnShaping
{
    /// <summary>
    /// How big the section is at the top against the bottom. Below one it tapers, above one it
    /// flares, and at one it runs straight.
    /// </summary>
    public double TopScale { get; set; } = 1;

    /// <summary>How far the section turns between bottom and top, in degrees.</summary>
    public double Twist { get; set; }

    /// <summary>How far the top stands off the base across the column and along it, in millimetres.</summary>
    public double SlantAcross { get; set; }

    public double SlantAlong { get; set; }

    /// <summary>How many flutes are cut round the shaft. None leaves it plain.</summary>
    public int Flutes { get; set; }

    /// <summary>How deep each flute is cut into the face, in millimetres.</summary>
    public double FluteDepth { get; set; } = 25;

    /// <summary>How tall the base is, and how much wider than the shaft it spreads.</summary>
    public double BaseHeight { get; set; }

    public double BaseSpread { get; set; } = 0.18;

    /// <summary>How tall the capital is, and how much wider than the shaft it spreads.</summary>
    public double CapitalHeight { get; set; }

    public double CapitalSpread { get; set; } = 0.22;

    public bool HasBase => BaseHeight > 0;

    public bool HasCapital => CapitalHeight > 0;

    /// <summary>Whether the shaft is anything other than a straight run of one section.</summary>
    public bool ShaftVaries =>
        Math.Abs(TopScale - 1) > 1e-6 || Math.Abs(Twist) > 1e-6 ||
        Math.Abs(SlantAcross) > 1e-6 || Math.Abs(SlantAlong) > 1e-6;

    public ColumnShaping Copy() => new()
    {
        TopScale = TopScale, Twist = Twist, SlantAcross = SlantAcross, SlantAlong = SlantAlong,
        Flutes = Flutes, FluteDepth = FluteDepth,
        BaseHeight = BaseHeight, BaseSpread = BaseSpread,
        CapitalHeight = CapitalHeight, CapitalSpread = CapitalSpread
    };

    /// <summary>The unshaped column: a straight prism of one section, in one piece.</summary>
    public static ColumnShaping Plain => new();
}

/// <summary>
/// Turning a column's section, its shaping and its height into the rings a solid is built from.
///
/// Everything a column can be - straight, tapered, twisted, leaning, fluted, and in three parts
/// - comes out of the same place: a stack of sections at heights, each the one before it
/// transformed. The 3D view lofts between them, so no shape needs its own builder, and a shape
/// nobody has thought of yet works the day its parameters exist.
/// </summary>
public static class ColumnGeometry
{
    /// <summary>How many rings a varying shaft is drawn in. A straight one needs only its ends.</summary>
    private const int ShaftRings = 16;

    /// <summary>One section at one height.</summary>
    public readonly record struct Ring(ColumnProfile Profile, double Elevation);

    /// <summary>
    /// The rings a column of this type is built from, between two heights. A plain column is
    /// two rings; a shaped one is as many as its curve needs; and base and capital add their own
    /// so the step between them and the shaft is a step rather than a slope.
    /// </summary>
    public static IReadOnlyList<Ring> Rings(ColumnType type, double bottom, double top)
    {
        var shaping = type.Shaping;
        var height = top - bottom;
        if (height <= 1e-6) return Array.Empty<Ring>();

        var section = Fluted(type.Profile, shaping);

        // What the shaft occupies once the base and capital have taken their share. A base and
        // capital taller than the column between them leave the shaft nothing, so they give way.
        var baseHeight = Math.Max(0, Math.Min(shaping.BaseHeight, height * 0.45));
        var capitalHeight = Math.Max(0, Math.Min(shaping.CapitalHeight, height * 0.45));
        var shaftBottom = bottom + baseHeight;
        var shaftTop = top - capitalHeight;

        var rings = new List<Ring>();

        if (baseHeight > 0)
        {
            // A base is a plinth: the shaft's section spread out, straight up, and stepped in
            // at the top where the shaft starts.
            var spread = section.ResizedTo(
                section.Extent.Width * (1 + shaping.BaseSpread),
                section.Extent.Depth * (1 + shaping.BaseSpread));

            rings.Add(new Ring(spread, bottom));
            rings.Add(new Ring(spread, shaftBottom - 1));
            rings.Add(new Ring(At(section, shaping, 0), shaftBottom));
        }

        // The shaft: one ring at each end when it runs straight, and a stack when it does not.
        var steps = shaping.ShaftVaries ? ShaftRings : 1;
        var from = baseHeight > 0 ? 1 : 0;

        for (var i = from; i <= steps; i++)
        {
            var fraction = (double)i / steps;
            rings.Add(new Ring(At(section, shaping, fraction), shaftBottom + (shaftTop - shaftBottom) * fraction));
        }

        if (capitalHeight > 0)
        {
            var atTop = At(section, shaping, 1);
            var spread = atTop.ResizedTo(
                atTop.Extent.Width * (1 + shaping.CapitalSpread),
                atTop.Extent.Depth * (1 + shaping.CapitalSpread));

            rings.Add(new Ring(spread, shaftTop + 1));
            rings.Add(new Ring(spread, top));
        }

        return rings;
    }

    /// <summary>The section this far up the shaft: scaled, turned and moved as the shaping says.</summary>
    private static ColumnProfile At(ColumnProfile section, ColumnShaping shaping, double fraction)
    {
        var scale = 1 + (shaping.TopScale - 1) * fraction;
        var radians = shaping.Twist * fraction * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = shaping.SlantAcross * fraction;
        var dy = shaping.SlantAlong * fraction;

        return section.Transformed(point =>
        {
            var x = point.X * scale;
            var y = point.Y * scale;
            return new Point2D(x * cos - y * sin + dx, x * sin + y * cos + dy);
        });
    }

    /// <summary>
    /// The section with its flutes cut into it: hollows spaced evenly round the face, each cut
    /// out of the shape by the same boolean a hand-drawn cut uses.
    /// </summary>
    public static ColumnProfile Fluted(ColumnProfile section, ColumnShaping shaping)
    {
        if (shaping.Flutes < 2 || shaping.FluteDepth <= 0 || section.IsEmpty) return section;

        var (width, depth) = section.Extent;
        var radius = Math.Min(shaping.FluteDepth, Math.Min(width, depth) / 4);
        if (radius <= 0.5) return section;

        var result = section;

        for (var i = 0; i < shaping.Flutes; i++)
        {
            var angle = 2 * Math.PI * i / shaping.Flutes;

            // On the face rather than outside it: the hollow's middle sits a flute's depth in
            // from the edge, so what is cut is a groove and not a bite out of the column.
            var at = new Point2D(
                (width / 2 - radius * 0.35) * Math.Cos(angle),
                (depth / 2 - radius * 0.35) * Math.Sin(angle));

            var flute = ColumnProfile.Ellipse(radius * 2, radius * 2, 16)
                .Transformed(point => new Point2D(point.X + at.X, point.Y + at.Y));

            var cut = result.Combine(flute.Outer, BooleanOperation.Difference);

            // A flute that would cut the column apart is not a flute; that one is left out.
            if (!cut.IsEmpty) result = cut;
        }

        return result;
    }
}
