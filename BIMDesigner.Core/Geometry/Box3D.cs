namespace BIMDesigner.Core.Geometry;

/// <summary>A box square to the project's axes, in millimetres: its lowest and highest corners.</summary>
public readonly record struct Box3D(Point3D Min, Point3D Max)
{
    /// <summary>The box with two opposite corners at these points, whichever way round they are.</summary>
    public static Box3D Around(Point3D a, Point3D b) =>
        new(new Point3D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
            new Point3D(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));

    public double Width => Max.X - Min.X;

    public double Depth => Max.Y - Min.Y;

    public double Height => Max.Z - Min.Z;

    public Point3D Centre => new((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2, (Min.Z + Max.Z) / 2);

    public bool Contains(Point3D point) =>
        point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y && point.Z >= Min.Z && point.Z <= Max.Z;

    /// <summary>Larger by this much every way.</summary>
    public Box3D Expand(double by) =>
        new(new Point3D(Min.X - by, Min.Y - by, Min.Z - by), new Point3D(Max.X + by, Max.Y + by, Max.Z + by));

    /// <summary>A corner's coordinate along an axis: 0 is X, 1 Y, 2 Z.</summary>
    public static double Along(Point3D point, int axis) => axis switch { 0 => point.X, 1 => point.Y, _ => point.Z };

    /// <summary>A point with one coordinate changed.</summary>
    public static Point3D With(Point3D point, int axis, double value) => axis switch
    {
        0 => point with { X = value },
        1 => point with { Y = value },
        _ => point with { Z = value }
    };
}
