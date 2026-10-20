using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The cross-section a column is made from (specification section 16).
///
/// A column is a profile run up between two heights, and nothing else. Keeping the section as
/// a profile rather than a shape name is what makes one column system serve a square pier, a
/// round shaft, a hollow box and whatever someone draws for themselves: the named shapes below
/// are generators for the same thing an editor produces, so a drawn profile is not a special
/// case that the rest of the model has to know about.
///
/// It holds an outer loop and any holes in it - a hole being what is left when something is cut
/// out of the middle - and both are plain outlines, so every view, quantity and export reads
/// the section the same way.
/// </summary>
public sealed class ColumnProfile
{
    /// <summary>How many straight pieces a full circle is drawn in.</summary>
    public const int CircleSides = 32;

    public ColumnProfile(IReadOnlyList<Point2D> outer, IReadOnlyList<IReadOnlyList<Point2D>>? holes = null)
    {
        Outer = outer;
        Holes = holes ?? Array.Empty<IReadOnlyList<Point2D>>();
    }

    /// <summary>The outline of the section, anticlockwise, about the column's own centre.</summary>
    public IReadOnlyList<Point2D> Outer { get; }

    /// <summary>What has been cut out of it, each a closed loop.</summary>
    public IReadOnlyList<IReadOnlyList<Point2D>> Holes { get; }

    /// <summary>Every loop, the outline first: what a boolean takes and a mesh draws.</summary>
    public IReadOnlyList<IReadOnlyList<Point2D>> Loops => new[] { Outer }.Concat(Holes).ToList();

    public bool IsEmpty => Outer.Count < 3;

    /// <summary>The material in the section: the outline less its holes.</summary>
    public double Area => Math.Max(0, Polygon2D.Area(Outer) - Holes.Sum(Polygon2D.Area));

    /// <summary>How far it reaches across and front to back, which is what a type's size means.</summary>
    public (double Width, double Depth) Extent => Outer.Count == 0
        ? (0, 0)
        : (Outer.Max(p => p.X) - Outer.Min(p => p.X), Outer.Max(p => p.Y) - Outer.Min(p => p.Y));

    // ---- the shapes a column starts from ---------------------------------------

    public static ColumnProfile Rectangle(double width, double depth) => new(new[]
    {
        new Point2D(-width / 2, -depth / 2), new Point2D(width / 2, -depth / 2),
        new Point2D(width / 2, depth / 2), new Point2D(-width / 2, depth / 2)
    });

    /// <summary>An ellipse, and with equal axes a circle - the same shape, so the same generator.</summary>
    public static ColumnProfile Ellipse(double width, double depth, int sides = CircleSides) =>
        new(Enumerable.Range(0, Math.Max(3, sides))
            .Select(i => 2 * Math.PI * i / Math.Max(3, sides))
            .Select(angle => new Point2D(width / 2 * Math.Cos(angle), depth / 2 * Math.Sin(angle)))
            .ToList());

    /// <summary>
    /// A regular polygon of this many sides across the given size, stood so a flat is at the
    /// bottom - which is how a hexagonal or octagonal column is built.
    /// </summary>
    public static ColumnProfile Polygon(int sides, double width, double depth)
    {
        sides = Math.Max(3, sides);
        var start = -Math.PI / 2 + Math.PI / sides;

        return new ColumnProfile(Enumerable.Range(0, sides)
            .Select(i => start + 2 * Math.PI * i / sides)
            .Select(angle => new Point2D(width / 2 * Math.Cos(angle), depth / 2 * Math.Sin(angle)))
            .ToList());
    }

    /// <summary>A square with the far quarter taken out, for a column tucked into the corner of a room.</summary>
    public static ColumnProfile Corner(double width, double depth) => new(new[]
    {
        new Point2D(-width / 2, -depth / 2), new Point2D(width / 2, -depth / 2), new Point2D(width / 2, 0),
        new Point2D(0, 0), new Point2D(0, depth / 2), new Point2D(-width / 2, depth / 2)
    });

    /// <summary>The section a named shape and size make, which is what a plain column type is.</summary>
    public static ColumnProfile Of(ColumnShape shape, double width, double depth) => Of(shape, width, depth, 0, 0, 0);

    /// <summary>A named shape, a steel section's with its flanges, web or wall: a solid one where they are not given.</summary>
    public static ColumnProfile Of(ColumnShape shape, double width, double depth, double flange, double web, double wall) => shape switch
    {
        ColumnShape.Round => Ellipse(width, depth),
        ColumnShape.LShaped => Corner(width, depth),
        ColumnShape.Triangular => Polygon(3, width, depth),
        ColumnShape.Hexagonal => Polygon(6, width, depth),
        ColumnShape.Octagonal => Polygon(8, width, depth),
        ColumnShape.IBeam when flange > 0 && web > 0 && 2 * flange < depth && web < width => ISection(width, depth, flange, web),
        ColumnShape.HollowRectangular when wall > 0 && 2 * wall < Math.Min(width, depth) => new ColumnProfile(Rectangle(width, depth).Outer, new[] { Rectangle(width - 2 * wall, depth - 2 * wall).Outer.Reverse().ToList() }),
        ColumnShape.HollowRound when wall > 0 && 2 * wall < width => new ColumnProfile(Ellipse(width, width).Outer, new[] { Ellipse(width - 2 * wall, width - 2 * wall).Outer.Reverse().ToList() }),
        ColumnShape.HollowRound => Ellipse(width, width),
        _ => Rectangle(width, depth)
    };

    /// <summary>
    /// A steel I: two flanges the width across, the web between them along the depth - a universal
    /// column or beam, a wide flange. Its root radii are left out, as a beam's are.
    /// </summary>
    public static ColumnProfile ISection(double width, double depth, double flange, double web)
    {
        var (b, d, tf, tw) = (width / 2, depth / 2, flange, web / 2);
        return new ColumnProfile(new[]
        {
            new Point2D(-b, -d), new Point2D(b, -d), new Point2D(b, -d + tf), new Point2D(tw, -d + tf),
            new Point2D(tw, d - tf), new Point2D(b, d - tf), new Point2D(b, d), new Point2D(-b, d),
            new Point2D(-b, d - tf), new Point2D(-tw, d - tf), new Point2D(-tw, -d + tf), new Point2D(-b, -d + tf)
        });
    }

    /// <summary>What a section is as a structural member: its area, its second moments about its own centroid, and the least radius of gyration that buckling goes by.</summary>
    public readonly record struct SectionProperties(double Area, double Ixx, double Iyy)
    {
        public double LeastRadiusOfGyration => Area <= 0 ? 0 : Math.Sqrt(Math.Min(Ixx, Iyy) / Area);
    }

    /// <summary>The section's area and second moments, the holes taken out - about its centroid, along its width and depth.</summary>
    public SectionProperties Properties()
    {
        // Each loop's area, first moments and second moments about the origin, by the shoelace
        // sums; a hole counts against the outline whichever way it is wound.
        static (double A, double Sx, double Sy, double Ixx, double Iyy) Of(IReadOnlyList<Point2D> loop)
        {
            double a = 0, sx = 0, sy = 0, ixx = 0, iyy = 0;
            for (var i = 0; i < loop.Count; i++)
            {
                var (p, q) = (loop[i], loop[(i + 1) % loop.Count]);
                var cross = p.X * q.Y - q.X * p.Y;
                a += cross / 2;
                sx += (p.Y + q.Y) * cross / 6;
                sy += (p.X + q.X) * cross / 6;
                ixx += (p.Y * p.Y + p.Y * q.Y + q.Y * q.Y) * cross / 12;
                iyy += (p.X * p.X + p.X * q.X + q.X * q.X) * cross / 12;
            }

            var sign = a < 0 ? -1 : 1;
            return (a * sign, sx * sign, sy * sign, ixx * sign, iyy * sign);
        }

        var outer = Of(Outer);
        var (area, firstX, firstY, secondX, secondY) = outer;
        foreach (var hole in Holes)
        {
            var cut = Of(hole);
            (area, firstX, firstY, secondX, secondY) = (area - cut.A, firstX - cut.Sx, firstY - cut.Sy, secondX - cut.Ixx, secondY - cut.Iyy);
        }

        if (area <= 0) return new SectionProperties(0, 0, 0);
        var (cy, cx) = (firstX / area, firstY / area);
        return new SectionProperties(area, secondX - area * cy * cy, secondY - area * cx * cx);
    }

    // ---- editing ---------------------------------------------------------------

    /// <summary>
    /// This profile with another shape cut out of it, added to it, or kept only where the two
    /// overlap. Where the result falls into separate pieces the largest is kept, because a
    /// column is one column: cutting it in two is a mistake, not a two-part column.
    /// </summary>
    public ColumnProfile Combine(IReadOnlyList<Point2D> shape, BooleanOperation operation)
    {
        var regions = PolygonBoolean.Combine(Loops, new[] { shape }, operation);
        if (regions.Count == 0) return new ColumnProfile(Array.Empty<Point2D>());

        var biggest = regions.OrderByDescending(region => region.Area).First();
        return new ColumnProfile(biggest.Outer, biggest.Holes);
    }

    /// <summary>
    /// The same section with its corners taken off: rounded to a radius, or cut straight across.
    ///
    /// A corner too shallow to be a corner is left alone, and one whose edges are too short for
    /// the radius is taken back only as far as those edges allow - so asking for more than the
    /// shape can give produces the most it can rather than a shape turned inside out.
    /// </summary>
    public ColumnProfile Rounded(double radius) => CornersOff(radius, round: true);

    public ColumnProfile Chamfered(double size) => CornersOff(size, round: false);

    private ColumnProfile CornersOff(double size, bool round)
    {
        if (size <= 0) return this;

        return new ColumnProfile(
            Corners(Outer, size, round),
            Holes.Select(hole => (IReadOnlyList<Point2D>)Corners(hole, size, round)).ToList());
    }

    /// <summary>How many straight pieces a rounded corner is drawn in.</summary>
    private const int CornerSegments = 6;

    private static IReadOnlyList<Point2D> Corners(IReadOnlyList<Point2D> loop, double size, bool round)
    {
        if (loop.Count < 3) return loop;

        var result = new List<Point2D>(loop.Count * (round ? CornerSegments + 1 : 2));

        for (var i = 0; i < loop.Count; i++)
        {
            var corner = loop[i];
            var before = loop[(i - 1 + loop.Count) % loop.Count];
            var after = loop[(i + 1) % loop.Count];

            var toBefore = before - corner;
            var toAfter = after - corner;
            if (toBefore.Length <= 1e-9 || toAfter.Length <= 1e-9) continue;

            var a = toBefore / toBefore.Length;
            var b = toAfter / toAfter.Length;

            // Already almost straight: nothing there to take off.
            if (Math.Abs(a.Cross(b)) < 0.02)
            {
                result.Add(corner);
                continue;
            }

            var back = Math.Min(size, Math.Min(toBefore.Length, toAfter.Length) / 2);
            var from = corner + a * back;
            var to = corner + b * back;

            if (!round)
            {
                result.Add(from);
                result.Add(to);
                continue;
            }

            // A quadratic curve through the corner: the two trimmed ends with the corner
            // pulling the curve toward it, which is the arc a fillet leaves.
            for (var s = 0; s <= CornerSegments; s++)
            {
                var t = (double)s / CornerSegments;
                var u = 1 - t;

                result.Add(new Point2D(
                    u * u * from.X + 2 * u * t * corner.X + t * t * to.X,
                    u * u * from.Y + 2 * u * t * corner.Y + t * t * to.Y));
            }
        }

        return result;
    }

    /// <summary>The same profile moved, turned about its centre, or reflected.</summary>
    public ColumnProfile Transformed(Func<Point2D, Point2D> move) =>
        new(Outer.Select(move).ToList(), Holes.Select(hole => (IReadOnlyList<Point2D>)hole.Select(move).ToList()).ToList());

    /// <summary>
    /// Moved so its middle is where a column stands, turned the way the column faces. What
    /// every view draws and what the 3D solid is run from.
    /// </summary>
    public ColumnProfile PlacedAt(Point2D centre, Vector2D across)
    {
        var along = new Vector2D(across.Y, -across.X);
        return Transformed(point => centre + across * point.X + along * point.Y);
    }

    /// <summary>
    /// Scaled to a width and depth, for a type whose size is changed after its section was
    /// drawn. A profile with no extent one way is left alone rather than collapsed.
    /// </summary>
    public ColumnProfile ResizedTo(double width, double depth)
    {
        var (currentWidth, currentDepth) = Extent;
        if (currentWidth <= 1e-6 || currentDepth <= 1e-6) return this;

        var x = width / currentWidth;
        var y = depth / currentDepth;
        return Transformed(point => new Point2D(point.X * x, point.Y * y));
    }
}
