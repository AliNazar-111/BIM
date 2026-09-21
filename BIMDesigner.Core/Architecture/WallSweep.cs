using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>Whether a profile stands proud of the wall or is cut into it.</summary>
public enum SweepKind
{
    /// <summary>Stands out from the face: a skirting, plinth, cornice, string course.</summary>
    Sweep,

    /// <summary>Cut into the face: a groove or recess.</summary>
    Reveal
}

/// <summary>The shapes a sweep can take. Reveals are always rectangular.</summary>
public enum SweepProfile
{
    Rectangle,
    Skirting,
    Cornice,
    Bead
}

/// <summary>Which face of the wall.</summary>
public enum WallSide
{
    Exterior,
    Interior
}

/// <summary>
/// A profile run along a wall type's face (specification section 3.1, "sweeps and reveals"):
/// how deep it is out from the face (or into it), how tall, and where it sits - measured from the
/// wall's base, or down from its top.
/// </summary>
public sealed record WallSweep(
    SweepKind Kind,
    SweepProfile Profile,
    WallSide Side,
    double Depth,
    double Height,
    double Elevation,
    bool FromTop,
    Guid MaterialId)
{
    /// <summary>The bottom and top of the profile on a wall standing between these elevations.</summary>
    public (double Bottom, double Top) Span(double wallBottom, double wallTop)
    {
        var bottom = FromTop ? wallTop - Elevation - Height : wallBottom + Elevation;
        return (bottom, bottom + Height);
    }

    /// <summary>
    /// The profile's outline in its own terms: how far out from the face, and how far up from its
    /// bottom, both from 0 to 1. Scaled by <see cref="Depth"/> and <see cref="Height"/> when used.
    /// </summary>
    public IReadOnlyList<(double Out, double Up)> Shape() => Kind == SweepKind.Reveal
        ? Rectangle
        : Profile switch
        {
            SweepProfile.Skirting => Skirting,
            SweepProfile.Cornice => Cornice,
            SweepProfile.Bead => Bead,
            _ => Rectangle
        };

    private static readonly (double, double)[] Rectangle = { (0, 0), (1, 0), (1, 1), (0, 1) };

    /// <summary>A board with its top edge splayed back to the wall.</summary>
    private static readonly (double, double)[] Skirting = { (0, 0), (1, 0), (1, 0.75), (0.4, 1), (0, 1) };

    /// <summary>A stepped cornice, growing out toward the top.</summary>
    private static readonly (double, double)[] Cornice =
    {
        (0, 0), (0.3, 0), (0.3, 0.25), (0.55, 0.35), (0.75, 0.55), (0.9, 0.75), (1, 0.8), (1, 1), (0, 1)
    };

    /// <summary>A half-round bead.</summary>
    private static readonly (double, double)[] Bead = Enumerable.Range(0, 13)
        .Select(i => i * Math.PI / 12)
        .Select(t => (Math.Sin(t), 0.5 - 0.5 * Math.Cos(t)))
        .ToArray();
}
