namespace BIMDesigner.Core.Geometry;

/// <summary>
/// An axis-aligned extent, in whatever coordinates the caller is working in - model
/// millimetres for a plan, distance-along and elevation for a section, paper millimetres for
/// a sheet.
///
/// A viewport needs to know how big its contents are before it can be placed or scaled, and
/// "how big is this drawing" is the same question whichever of those it is asking about.
/// </summary>
public readonly record struct BoundingBox2D(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>An empty box, inverted so the first point added replaces both corners.</summary>
    public static readonly BoundingBox2D Empty = new(
        double.PositiveInfinity, double.PositiveInfinity,
        double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;

    public double Width => IsEmpty ? 0 : MaxX - MinX;

    public double Height => IsEmpty ? 0 : MaxY - MinY;

    public Point2D Centre => IsEmpty ? default : new Point2D((MinX + MaxX) / 2, (MinY + MaxY) / 2);

    public BoundingBox2D Include(Point2D point) => Include(point.X, point.Y);

    public BoundingBox2D Include(double x, double y) => new(
        Math.Min(MinX, x), Math.Min(MinY, y),
        Math.Max(MaxX, x), Math.Max(MaxY, y));

    public BoundingBox2D Include(BoundingBox2D other) =>
        other.IsEmpty ? this : Include(other.MinX, other.MinY).Include(other.MaxX, other.MaxY);

    /// <summary>Grows the box by the same amount on every side.</summary>
    public BoundingBox2D Expand(double margin) =>
        IsEmpty ? this : new BoundingBox2D(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);

    /// <summary>
    /// The box, guaranteed to have some size. A drawing of a single point has no extent, and
    /// dividing by its width to fit it to a viewport would give infinity.
    /// </summary>
    public BoundingBox2D OrAtLeast(double minimum)
    {
        if (IsEmpty) return new BoundingBox2D(0, 0, minimum, minimum);
        if (Width >= minimum && Height >= minimum) return this;

        var centre = Centre;
        var halfWidth = Math.Max(Width, minimum) / 2;
        var halfHeight = Math.Max(Height, minimum) / 2;

        return new BoundingBox2D(
            centre.X - halfWidth, centre.Y - halfHeight,
            centre.X + halfWidth, centre.Y + halfHeight);
    }

    public static BoundingBox2D Around(IEnumerable<Point2D> points)
    {
        var box = Empty;
        foreach (var point in points) box = box.Include(point);
        return box;
    }
}
