namespace BIMDesigner.Core.Rendering;

/// <summary>
/// How a pattern lies on a face: along it level and up it - a wall's bricks in level courses, a
/// roof's slates in courses along the eaves and up the slope - and on a level face along the plan's
/// x and y. A point's place in the pattern is its distance along those, mm, so neighbouring faces
/// of a wall meet in step.
/// </summary>
public static class TextureMapping
{
    /// <summary>Along the face level and up it, unit vectors, for a face of the way it faces.</summary>
    public static ((double X, double Y, double Z) Along, (double X, double Y, double Z) Up) Basis(double nx, double ny, double nz)
    {
        var level = Math.Sqrt(nx * nx + ny * ny);
        var (tx, ty, tz) = level < 1e-6 ? (1.0, 0.0, 0.0) : (-ny / level, nx / level, 0.0);
        var (bx, by, bz) = (ny * tz - nz * ty, nz * tx - nx * tz, nx * ty - ny * tx);
        var length = Math.Sqrt(bx * bx + by * by + bz * bz);
        if (length > 1e-12) (bx, by, bz) = (bx / length, by / length, bz / length);
        return ((tx, ty, tz), (bx, by, bz));
    }

    /// <summary>A point's place in the pattern, mm along and up the face of the way it faces.</summary>
    public static (double U, double V) Of(double x, double y, double z, double nx, double ny, double nz)
    {
        var (along, up) = Basis(nx, ny, nz);
        return (x * along.X + y * along.Y + z * along.Z, x * up.X + y * up.Y + z * up.Z);
    }
}
