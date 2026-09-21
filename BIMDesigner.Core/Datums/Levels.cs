using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Datums;

/// <summary>
/// Questions about the storeys of a building (specification section 2.3).
///
/// Levels are the one datum everything else is hung from, so adding, moving and removing one
/// reaches further than it looks: walls constrained to a level follow it, and everything
/// hosted on it goes with it.
/// </summary>
public static class Levels
{
    /// <summary>The storey height used when there is nothing to work one out from.</summary>
    public const double DefaultStoreyHeight = 3000;

    /// <summary>
    /// A name not already taken. Storeys are usually numbered, so the name follows the count
    /// rather than the elevation - two levels called "Level 2" would make every schedule,
    /// tag and sheet that names one of them ambiguous.
    /// </summary>
    public static string NextName(BimDocument document)
    {
        var used = document.Levels.Select(level => level.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var number = document.Levels.Count + 1; ; number++)
        {
            var name = $"Level {number}";
            if (!used.Contains(name)) return name;
        }
    }

    /// <summary>
    /// Where a new storey goes: one storey height above the highest one there is.
    ///
    /// The height is taken from the gap between the top two levels when there is one, so a
    /// project working in 2.8 m storeys keeps working in 2.8 m storeys.
    /// </summary>
    public static double NextElevation(BimDocument document)
    {
        var elevations = document.Levels.Select(level => level.Elevation).OrderBy(value => value).ToList();

        if (elevations.Count == 0) return 0;
        if (elevations.Count == 1) return elevations[0] + DefaultStoreyHeight;

        var storey = elevations[^1] - elevations[^2];
        return elevations[^1] + (storey > 0 ? storey : DefaultStoreyHeight);
    }

    /// <summary>Everything hosted on a level. What would be lost if it were deleted.</summary>
    public static IReadOnlyList<Element> ElementsOn(BimDocument document, Guid levelId) =>
        document.Elements.Where(element => element.LevelId == levelId).ToList();

    /// <summary>Walls whose top is constrained to this level, which is a different thing.</summary>
    public static IReadOnlyList<Wall> ConstrainedTo(BimDocument document, Guid levelId) =>
        document.Walls.Where(wall => wall.TopLevelId == levelId).ToList();

    /// <summary>
    /// The level immediately below this one, or null when it is the lowest.
    ///
    /// This is what a plan shows as an underlay: the storey you are setting out against.
    /// </summary>
    public static Level? Below(BimDocument document, Guid levelId)
    {
        if (document.FindLevel(levelId) is not { } level) return null;

        return document.Levels
            .Where(other => other.Elevation < level.Elevation)
            .OrderByDescending(other => other.Elevation)
            .FirstOrDefault();
    }

    public static Level? Above(BimDocument document, Guid levelId)
    {
        if (document.FindLevel(levelId) is not { } level) return null;

        return document.Levels
            .Where(other => other.Elevation > level.Elevation)
            .OrderBy(other => other.Elevation)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether a name can be used. Levels are referred to by name on drawings and in
    /// schedules, so two with the same one is a defect rather than an inconvenience.
    /// </summary>
    public static bool IsNameAvailable(BimDocument document, string name, Level? excluding = null) =>
        !string.IsNullOrWhiteSpace(name) &&
        !document.Levels.Any(level =>
            !ReferenceEquals(level, excluding) &&
            string.Equals(level.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
