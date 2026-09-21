using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>A wall's profile seen end on (specification section 3.1, "slanted and tapered walls").</summary>
public enum WallCrossSection
{
    Vertical,

    /// <summary>The whole wall leans, by its angle from vertical.</summary>
    Slanted,

    /// <summary>Its faces lean in as it rises, so it is thinner at the top than the bottom.</summary>
    Tapered
}

/// <summary>
/// How far a leaning wall has moved sideways at a given height.
///
/// A slanted or tapered wall is built exactly as a vertical one - the same outline, joins,
/// openings and layers - and then every point of it is moved across the wall by how high it is.
/// For a slanted wall every point moves alike; for a tapered one the two faces move toward each
/// other, the finishes on each side keep their thickness, and the core in between gets thinner.
/// That is the "variable" layer of a tapered wall: here, the core.
///
/// Because the move is across the wall where each point is, it works for curved walls too.
/// </summary>
public static class WallLean
{
    /// <summary>The steepest a wall may lean, in degrees. Past this it is a roof, not a wall.</summary>
    public const double MaxAngle = 75;

    /// <summary>The thinnest the core of a tapered wall is allowed to get, in millimetres.</summary>
    private const double MinimumCore = 1;

    /// <summary>Whether a wall leans at all.</summary>
    public static bool Leans(Wall wall, WallType type) => wall.CrossSection switch
    {
        WallCrossSection.Slanted => Math.Abs(wall.SlantAngle) > 1e-9,
        WallCrossSection.Tapered => Math.Abs(ExteriorTaper(wall, type)) > 1e-9 || Math.Abs(InteriorTaper(wall, type)) > 1e-9,
        _ => false
    };

    /// <summary>The exterior face's lean inward, in degrees: the wall's own if overridden, else its type's.</summary>
    public static double ExteriorTaper(Wall wall, WallType type) => wall.OverrideTaper ? wall.ExteriorTaper : type.ExteriorTaperAngle;

    /// <summary>The interior face's lean inward, in degrees.</summary>
    public static double InteriorTaper(Wall wall, WallType type) => wall.OverrideTaper ? wall.InteriorTaper : type.InteriorTaperAngle;

    /// <summary>
    /// How far a point this far across the wall (from its body centreline, toward the
    /// exterior) has moved toward the exterior at this height above the wall's base.
    /// </summary>
    public static double Shift(Wall wall, WallType type, double across, double height)
    {
        switch (wall.CrossSection)
        {
            case WallCrossSection.Slanted:
                return Math.Tan(Clamp(wall.SlantAngle) * Math.PI / 180) * height;

            case WallCrossSection.Tapered:
            {
                var (outer, inner) = VariableBand(type.Structure);

                // Inward from each face: the exterior moves toward the interior, and back.
                var exterior = -Math.Tan(Clamp(ExteriorTaper(wall, type)) * Math.PI / 180) * height;
                var interior = Math.Tan(Clamp(InteriorTaper(wall, type)) * Math.PI / 180) * height;

                // The core cannot be squeezed through itself: past its thickness both faces
                // stop closing in, in proportion, and the top of the wall is where they meet.
                var room = outer - inner - MinimumCore;
                var closing = interior - exterior;
                if (closing > room && closing > 0)
                {
                    var scale = Math.Max(0, room) / closing;
                    exterior *= scale;
                    interior *= scale;
                }

                if (across >= outer - 1e-9) return exterior;
                if (across <= inner + 1e-9) return interior;

                // Inside the core, in proportion between its two faces.
                return interior + (exterior - interior) * (across - inner) / (outer - inner);
            }

            default:
                return 0;
        }
    }

    /// <summary>Where a point of the wall's plan is at this height above its base.</summary>
    public static Point2D Move(Wall wall, WallType type, Point2D point, double height)
    {
        if (!Leans(wall, type) || height == 0) return point;

        var (along, across) = wall.Locate(type.Structure, point);
        return point + wall.ExteriorNormalAt(Math.Clamp(along, 0, wall.Length)) * Shift(wall, type, across, height);
    }

    /// <summary>
    /// The layer that takes up the change in a tapered wall - the core, from its first
    /// structural layer to its last - as offsets across the wall. The middle layer stands in
    /// when there is no structure at all.
    /// </summary>
    public static (double Outer, double Inner) VariableBand(CompoundStructure structure)
    {
        var half = structure.TotalWidth / 2;
        var layers = structure.GetLayerOffsets().ToList();
        if (layers.Count == 0) return (half, -half);

        var first = structure.CoreStartIndex;
        var last = structure.CoreEndIndex;
        if (first < 0) first = last = layers.Count / 2;

        return (half - layers[first].Start, half - layers[last].End);
    }

    private static double Clamp(double degrees) => Math.Clamp(degrees, -MaxAngle, MaxAngle);
}
