namespace BIMDesigner.Core.Geometry;

/// <summary>A point on a floor plan. X and Y are millimetres in model space, Y pointing up.</summary>
public readonly record struct Point2D(double X, double Y)
{
    public double DistanceTo(Point2D other) => (other - this).Length;

    public static Vector2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);

    public static Point2D operator +(Point2D p, Vector2D v) => new(p.X + v.X, p.Y + v.Y);

    public static Point2D operator -(Point2D p, Vector2D v) => new(p.X - v.X, p.Y - v.Y);

    /// <summary>Midpoint of the segment to another point.</summary>
    public Point2D MidpointTo(Point2D other) => new((X + other.X) / 2, (Y + other.Y) / 2);

    public override string ToString() => $"({X:0} mm, {Y:0} mm)";
}

/// <summary>A direction and distance on a floor plan, in millimetres.</summary>
public readonly record struct Vector2D(double X, double Y)
{
    public static readonly Vector2D UnitX = new(1, 0);

    public static readonly Vector2D UnitY = new(0, 1);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static Vector2D operator *(Vector2D v, double scale) => new(v.X * scale, v.Y * scale);

    public static Vector2D operator *(double scale, Vector2D v) => v * scale;

    public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);

    public static Vector2D operator -(Vector2D v) => new(-v.X, -v.Y);

    public static Vector2D operator -(Vector2D a, Vector2D b) => new(a.X - b.X, a.Y - b.Y);

    public static Vector2D operator /(Vector2D v, double divisor) => new(v.X / divisor, v.Y / divisor);

    public double Dot(Vector2D other) => X * other.X + Y * other.Y;

    /// <summary>
    /// The 2D cross product. Its sign says which way <paramref name="other"/> turns from
    /// this vector, and it is zero exactly when the two are parallel - which is how line
    /// intersection detects that there is no answer.
    /// </summary>
    public double Cross(Vector2D other) => X * other.Y - Y * other.X;

    /// <summary>
    /// Rotated 90° anticlockwise. Used to step sideways off a wall's centreline into its
    /// layers, so the sign convention here decides which side counts as exterior.
    /// </summary>
    public Vector2D PerpendicularLeft() => new(-Y, X);

    /// <summary>
    /// Unit vector in the same direction, or <paramref name="fallback"/> when this vector
    /// has no length - which happens for a wall whose ends coincide.
    /// </summary>
    public Vector2D NormalisedOrDefault(Vector2D fallback)
    {
        var length = Length;
        return length > 0 ? new Vector2D(X / length, Y / length) : fallback;
    }

    public override string ToString() => $"<{X:0.##}, {Y:0.##}>";
}
