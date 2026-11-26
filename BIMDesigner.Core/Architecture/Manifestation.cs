using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Manifestation of glazing - Approved Document K Section 7, requirement K5.2 (round 155), read from the 2013 edition: transparent glazing people
/// are likely to come into contact with while moving in or about a building other than a dwelling (K5.2 does not apply to dwellings) made
/// apparent. Critical locations are large uninterrupted areas of transparent glazing forming, or part of, internal or external walls and doors
/// (7.1); the risk of collision is greatest where people may think they can walk from one side to the other (7.2). Either the glass is marked
/// (7.3a) or something else shows it is there (7.3b, Diagram 7.1): a pane less than 400 wide between frames; rails between 850 and 1000 and
/// between 1400 and 1600 above the floor; a single-pane glazed door with a substantial frame; a glazed door with no frame or narrow frames and a
/// large handle or push plate on each pane. Marked, it is at two levels, 850 to 1000 and 1400 to 1600 (7.4a, Diagram 7.2), contrasting from
/// both sides in all lighting (b), a logo or sign 150 high or bands 50 high (c); a glazed door beside or in a glazed screen with a high-contrast
/// strip at its top and both sides (d); a glass door that may be held open guarded against its leading edge (e). The type's
/// <c>Manifestation</c> stands for all of a to d.
///
/// What the app reads as a critical location - Diagram 7.1's door-height glazing - is glass coming down below 850, the bottom of the lower band;
/// a pane needs marking where it is 400 wide or more and its glass runs right across either band, no rail breaking it there. Glass that cannot be
/// seen through - obscured, frosted, etched, a spandrel - is not transparent glazing. Not in the US, whose codes ask nothing of it.
/// </summary>
public static class Manifestation
{
    /// <summary>The narrowest pane that wants marking, mm: one less than 400 wide between frames does not (Diagram 7.1a).</summary>
    public const double NarrowPane = 400;

    /// <summary>The two levels it is marked at, mm above the floor (7.4a, Diagram 7.2).</summary>
    public static readonly (double From, double To) Lower = (850, 1000), Upper = (1400, 1600);

    /// <summary>Whether K5.2 reaches a project: England's, not the US's.</summary>
    public static bool Applies(BimDocument document) => !document.ProjectInformation.ClearanceStandard.IsUs();

    /// <summary>
    /// The first pane of a piece of glazing - each its width and its glass's bottom and top above the floor, mm - that wants marking: in glazing
    /// coming down below 850, 400 wide or more and its glass running across a band uninterrupted. None where none does.
    /// </summary>
    public static (double Width, double Bottom, double Top)? Unmarked(IEnumerable<(double Width, double Bottom, double Top)> panes)
    {
        var list = panes.Where(pane => pane.Width > 0 && pane.Top > pane.Bottom).ToList();
        if (list.Count == 0 || list.Min(pane => pane.Bottom) >= Lower.From - 0.5) return null;
        foreach (var pane in list)
            if (pane.Width >= NarrowPane - 0.5 && (Across(pane, Lower) || Across(pane, Upper))) return pane;
        return null;
    }

    private static bool Across((double Width, double Bottom, double Top) pane, (double From, double To) band) =>
        pane.Bottom <= band.From + 0.5 && pane.Top >= band.To - 0.5;

    /// <summary>Whether glazing described so is transparent - not obscured, frosted, etched, sandblasted, patterned, translucent or a spandrel.</summary>
    public static bool Transparent(string glazing) =>
        !new[] { "obscure", "frosted", "etched", "sandblast", "patterned", "translucent", "opaque", "spandrel" }.Any(word => glazing.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a door's glass wants marking: one clear pane in a slim frame - not a glazed door's panes in its stiles and rails, a substantial
    /// frame (Diagram 7.1c) - with no large pull handle or push plate on it (Diagram 7.1d), as its hardware set says.
    /// </summary>
    public static bool DoorWants(DoorType type) => type.LeafDesign == DoorLeafDesign.FullGlass && !LargeHandle(type);

    /// <summary>Whether a door's hardware set gives each pane a large handle or push plate (Diagram 7.1d).</summary>
    public static bool LargeHandle(DoorType type) =>
        new[] { "pull handle", "push plate", "pull bar", "push pad", "d handle", "d-handle" }.Any(word => type.HardwareSet.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>What is said of a pane wanting marking.</summary>
    public static string Said((double Width, double Bottom, double Top) pane) =>
        $"its glass, a pane {pane.Width:0} wide from {pane.Bottom:0} to {pane.Top:0} above the floor, is not marked to be seen.";

    /// <summary>The rule for glazing in a wall: a window or a curtain wall.</summary>
    public const string Rule =
        "Approved Document K (K5.2, 7.1, 7.4, Diagram 7.2) asks for large uninterrupted areas of transparent glazing in a building other than a dwelling to be marked at two levels - 850 to 1000 and 1400 to 1600 above the floor - contrasting from both sides, as a logo 150 high or bands 50 high; or to be broken by rails at both levels, or in panes less than 400 wide between frames (7.3b, Diagram 7.1). Tick its type's Manifestation.";

    /// <summary>The rule for a glass door.</summary>
    public const string DoorRule =
        "Approved Document K (K5.2, 7.4, Diagram 7.1) asks for a glass door in a slim frame in a building other than a dwelling to be marked at two levels - 850 to 1000 and 1400 to 1600 above the floor - and, beside or in a glazed screen, with a high-contrast strip at its top and both sides (7.4d); or for a large pull handle or push plate on each pane (Diagram 7.1d). Tick its type's Manifestation.";
}
