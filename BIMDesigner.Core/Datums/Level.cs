namespace BIMDesigner.Core.Datums;

/// <summary>
/// A horizontal datum (specification section 2.3). Elements are hosted on a base level
/// with an offset and may be constrained to a top level.
///
/// Levels are the reason a wall's height can be "up to Level 2" rather than a fixed
/// number: raise the level and every wall constrained to it follows.
/// </summary>
public sealed class Level
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Level 1";

    /// <summary>Millimetres above the project base point. May be negative for basements.</summary>
    public double Elevation { get; set; }

    public override string ToString() => Name;
}
