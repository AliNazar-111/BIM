using BIMDesigner.Core.Documents;
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

/// <summary>The shapes a sweep can take without drawing one. Reveals are always rectangular.</summary>
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
/// A profile run along a wall's face (specification section 3.1, "sweeps and reveals"; the
/// settings follow Revit's Wall Sweeps table): how deep it is out from the face (or into it),
/// how tall, and where it sits - measured from the wall's base, or down from its top.
///
/// <list type="bullet">
///   <item><see cref="Offset"/> moves it off the face, or into the wall when negative.</item>
///   <item><see cref="Flip"/> turns the profile upside down.</item>
///   <item><see cref="Setback"/> stops it short of the wall's ends.</item>
///   <item><see cref="CutsWall"/> lets a sweep set into the wall cut its own recess.</item>
///   <item><see cref="Cuttable"/> is whether doors and windows break it.</item>
///   <item><see cref="ProfileId"/> names a drawn <see cref="SweepProfileType"/> to use instead of a built-in shape.</item>
///   <item><see cref="Returns"/> turns it round an exposed end of the wall instead of stopping flat.</item>
///   <item><see cref="Vertical"/> stands it upright, full height, at <see cref="Along"/> - a pilaster strip or vertical groove.</item>
/// </list>
/// </summary>
public sealed record WallSweep(
    SweepKind Kind,
    SweepProfile Profile,
    WallSide Side,
    double Depth,
    double Height,
    double Elevation,
    bool FromTop,
    Guid MaterialId,
    double Offset = 0,
    bool Flip = false,
    double Setback = 0,
    bool CutsWall = false,
    bool Cuttable = true,
    Guid? ProfileId = null,
    bool Returns = false,
    bool Vertical = false,
    double Along = 0)
{
    /// <summary>The bottom and top of the profile on a wall standing between these elevations.</summary>
    public (double Bottom, double Top) Span(double wallBottom, double wallTop)
    {
        if (Vertical) return (wallBottom, wallTop);

        var bottom = FromTop ? wallTop - Elevation - Height : wallBottom + Elevation;
        return (bottom, bottom + Height);
    }

    /// <summary>
    /// The profile's outline in its own terms: how far out from the face, and how far up from its
    /// bottom, both from 0 to 1, anticlockwise. Scaled by <see cref="Depth"/> and <see cref="Height"/>
    /// when used. A drawn profile is found in the document; flipped, it is turned upside down.
    /// </summary>
    public IReadOnlyList<(double Out, double Up)> Shape(BimDocument? document = null)
    {
        IReadOnlyList<(double Out, double Up)> shape = Kind == SweepKind.Reveal
            ? Rectangle
            : ProfileId is { } id && document?.FindType<SweepProfileType>(id) is { } drawn && drawn.Normalised() is { Count: >= 3 } points
                ? points
                : Profile switch
                {
                    SweepProfile.Skirting => Skirting,
                    SweepProfile.Cornice => Cornice,
                    SweepProfile.Bead => Bead,
                    _ => Rectangle
                };

        // Upside down, and reversed so it still runs anticlockwise.
        return Flip ? shape.Select(p => (p.Out, 1 - p.Up)).Reverse().ToList() : shape;
    }

    /// <summary>How far each point of the profile stands out from the face, offset included.</summary>
    public double OutAt(double normalisedOut) => Offset + normalisedOut * Depth;

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
