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
    private double _elevation;

    /// <summary>The levels being worked out on this thread: a level whose floor is measured from itself stops there.</summary>
    [ThreadStatic] private static HashSet<Level>? _working;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Level 1";

    /// <summary>
    /// Millimetres above the project base point. May be negative for basements. A level standing
    /// on a floor is at that floor's top, wherever it is; set to a height of its own, it no
    /// longer stands on it.
    /// </summary>
    public double Elevation
    {
        get
        {
            if (StandsOn is not { } floor || FloorTop is null) return _elevation;

            _working ??= new HashSet<Level>();
            if (!_working.Add(this)) return _elevation;
            try { return FloorTop(floor) ?? _elevation; }
            finally { _working.Remove(this); }
        }
        set
        {
            if (StandsOn is not null && Math.Abs(value - Elevation) > 1e-6) StandsOn = null;
            _elevation = value;
        }
    }

    /// <summary>
    /// The floor - or the flat roof over the storey below - this storey is built on: the level is
    /// that slab's top, and rises and falls with it however thick it is made, so the storey
    /// starts over the slab and keeps its own height. Null for a level at a height of its own.
    /// </summary>
    public Guid? StandsOn { get; set; }

    /// <summary>Where the top of a slab is, given by the project the level is in.</summary>
    internal Func<Guid, double?>? FloorTop { get; set; }

    /// <summary>Lets go of its floor where it is now: it keeps that height as its own.</summary>
    public void StandAlone()
    {
        var at = Elevation;
        StandsOn = null;
        _elevation = at;
    }

    public override string ToString() => Name;
}
