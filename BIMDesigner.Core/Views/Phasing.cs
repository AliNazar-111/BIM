using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Views;

/// <summary>
/// Where an element stands in a view's phase (specification section 7, phasing): built in it,
/// there from before it, taken down in it, put up and taken down in it - or not there at all,
/// not yet built or gone before.
/// </summary>
public enum PhaseStatus
{
    New,
    Existing,
    Demolished,
    Temporary
}

/// <summary>
/// Which of what is there in a view's phase the view shows, and how: Revit's phase filters.
/// New work is drawn as itself; what was there before, what is taken down and what is temporary
/// are drawn as the phasing's graphic overrides have them - existing halftoned, demolished dashed
/// in red, temporary dashed and halftoned - or not at all.
/// </summary>
public enum PhaseFilter
{
    /// <summary>Everything there in the phase, as itself.</summary>
    None,

    /// <summary>New as itself; existing, demolished and temporary overridden.</summary>
    ShowAll,

    /// <summary>The building as it will be: new and existing as themselves; demolished and temporary not shown.</summary>
    ShowComplete,

    /// <summary>The new work alone.</summary>
    ShowNew,

    /// <summary>A demolition plan: what is there and stays, and what is taken down - not the new.</summary>
    ShowPreviousAndDemolition,

    /// <summary>What is there and stays, and the new work - not what is taken down.</summary>
    ShowPreviousAndNew,

    /// <summary>What was there before, alone.</summary>
    ShowPrevious,

    /// <summary>What is taken down, and the new work.</summary>
    ShowDemolitionAndNew
}

/// <summary>How a phase filter shows an element of a status.</summary>
public enum PhaseDisplay
{
    ByCategory,
    Overridden,
    NotDisplayed
}

/// <summary>
/// How an element is drawn in a view beyond how it is drawn anyway: faded, in a colour of its own,
/// dashed. What phasing and view filters change.
/// </summary>
public readonly record struct ElementStyle(bool Halftone = false, Materials.ColourRgb? Colour = null, bool Dashed = false)
{
    public bool IsPlain => !Halftone && Colour is null && !Dashed;

    /// <summary>One on top of another: the later's colour where it has one, faded or dashed if either is.</summary>
    public ElementStyle Then(ElementStyle later) => new(Halftone || later.Halftone, later.Colour ?? Colour, Dashed || later.Dashed);
}

public static class Phasing
{
    /// <summary>What is taken down is drawn in this red, dashed.</summary>
    public static readonly Materials.ColourRgb DemolishedColour = new(0xC0, 0x39, 0x2B);

    /// <summary>Where a phase falls on the project's time line: existing, then new and the temporary works of it, then future.</summary>
    public static int Order(DesignPhase phase) => phase switch
    {
        DesignPhase.Existing => 0,
        DesignPhase.New or DesignPhase.Temporary => 1,
        _ => 2
    };

    /// <summary>
    /// The categories phasing leaves alone: what a view draws on top of the building - its
    /// dimensions, tags, notes and marks - rather than what is built.
    /// </summary>
    public static bool IsAnnotation(BuiltInCategory category) => category is BuiltInCategory.Dimensions or BuiltInCategory.Tags
        or BuiltInCategory.TextNotes or BuiltInCategory.Sections or BuiltInCategory.Grids or BuiltInCategory.Sheets or BuiltInCategory.Views
        or BuiltInCategory.Elevations or BuiltInCategory.Callouts or BuiltInCategory.ViewTemplates or BuiltInCategory.RevisionClouds
        or BuiltInCategory.KeynoteTags or BuiltInCategory.SpotElevations or BuiltInCategory.PropertyLines or BuiltInCategory.DetailLines or BuiltInCategory.FilledRegions;

    /// <summary>
    /// An element's status in a phase - null where it is not there: not yet built, or gone
    /// before it. Temporary works stand only through the new phase.
    /// </summary>
    public static PhaseStatus? StatusIn(Element element, DesignPhase phase)
    {
        var view = Order(phase);
        var created = Order(element.PhaseCreated);
        if (created > view) return null;

        if (element.PhaseCreated == DesignPhase.Temporary)
            return view == created ? PhaseStatus.Temporary : null;

        if (element.PhaseDemolished is { } gone)
        {
            var demolished = Order(gone);
            if (demolished < view) return null;
            if (demolished == view) return created == view ? PhaseStatus.Temporary : PhaseStatus.Demolished;
        }

        return created == view ? PhaseStatus.New : PhaseStatus.Existing;
    }

    /// <summary>How a phase filter shows an element of a status.</summary>
    public static PhaseDisplay Display(PhaseFilter filter, PhaseStatus status) => (filter, status) switch
    {
        (PhaseFilter.None, _) => PhaseDisplay.ByCategory,
        (_, PhaseStatus.New) => filter is PhaseFilter.ShowPrevious or PhaseFilter.ShowPreviousAndDemolition ? PhaseDisplay.NotDisplayed : PhaseDisplay.ByCategory,
        (PhaseFilter.ShowAll, _) => PhaseDisplay.Overridden,
        (PhaseFilter.ShowComplete, PhaseStatus.Existing) => PhaseDisplay.ByCategory,
        (PhaseFilter.ShowPreviousAndDemolition or PhaseFilter.ShowPreviousAndNew or PhaseFilter.ShowPrevious, PhaseStatus.Existing) => PhaseDisplay.Overridden,
        (PhaseFilter.ShowPreviousAndDemolition or PhaseFilter.ShowDemolitionAndNew, PhaseStatus.Demolished) => PhaseDisplay.Overridden,
        (PhaseFilter.ShowDemolitionAndNew, PhaseStatus.Temporary) => PhaseDisplay.Overridden,
        _ => PhaseDisplay.NotDisplayed
    };

    /// <summary>The phasing's graphic override for a status: existing faded, demolished red and dashed, temporary dashed and faded.</summary>
    public static ElementStyle Override(PhaseStatus status) => status switch
    {
        PhaseStatus.Existing => new ElementStyle(Halftone: true),
        PhaseStatus.Demolished => new ElementStyle(Colour: DemolishedColour, Dashed: true),
        PhaseStatus.Temporary => new ElementStyle(Halftone: true, Dashed: true),
        _ => default
    };

    /// <summary>"Show Previous + Demo": a phase filter as it is listed.</summary>
    public static string Describe(PhaseFilter filter) => filter switch
    {
        PhaseFilter.None => "None",
        PhaseFilter.ShowAll => "Show All",
        PhaseFilter.ShowComplete => "Show Complete",
        PhaseFilter.ShowNew => "Show New",
        PhaseFilter.ShowPreviousAndDemolition => "Show Previous + Demo",
        PhaseFilter.ShowPreviousAndNew => "Show Previous + New",
        PhaseFilter.ShowPrevious => "Show Previous Phase",
        _ => "Show Demo + New"
    };

    public static bool TryParse(string? text, out PhaseFilter filter)
    {
        foreach (var candidate in Enum.GetValues<PhaseFilter>())
            if (string.Equals(Describe(candidate), text, StringComparison.OrdinalIgnoreCase) || string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                filter = candidate;
                return true;
            }

        filter = PhaseFilter.ShowAll;
        return false;
    }
}
