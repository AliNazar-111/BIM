using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>
/// A view filter (specification section 6.2): the elements of some categories whose parameters
/// pass its rules - "walls whose Fire Rating equals 60 min", "doors wider than 900". Defined once
/// in the project; each view it is added to hides what it picks out, or colours or fades it.
/// </summary>
public sealed class ParameterFilter
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Filter 1";

    /// <summary>What it looks at; none means every category.</summary>
    public HashSet<BuiltInCategory> Categories { get; } = new();

    /// <summary>The tests on its parameters, as a schedule's filters are put.</summary>
    public List<ScheduleFilter> Rules { get; } = new();

    /// <summary>Whether one rule passing is enough, rather than every one.</summary>
    public bool MatchAny { get; set; }

    /// <summary>Whether an element is one it picks out: of its categories, its rules passing.</summary>
    public bool Picks(BimDocument document, Element element)
    {
        if (Categories.Count > 0 && !Categories.Contains(element.Category)) return false;
        if (Rules.Count == 0) return true;
        var cells = Schedule.ParametersOf(document, element);
        return MatchAny ? Rules.Any(rule => Schedule.Passes(cells, rule)) : Rules.All(rule => Schedule.Passes(cells, rule));
    }

    public ParameterFilter Copy()
    {
        var copy = new ParameterFilter { Id = Id, Name = Name, MatchAny = MatchAny };
        copy.Categories.UnionWith(Categories);
        copy.Rules.AddRange(Rules.Select(rule => new ScheduleFilter(rule.Field, rule.Comparison, rule.Value)));
        return copy;
    }

    public void CopyFrom(ParameterFilter other)
    {
        (Name, MatchAny) = (other.Name, other.MatchAny);
        Categories.Clear();
        Categories.UnionWith(other.Categories);
        Rules.Clear();
        Rules.AddRange(other.Rules.Select(rule => new ScheduleFilter(rule.Field, rule.Comparison, rule.Value)));
    }
}

/// <summary>A filter added to a view: whether what it picks out is shown, and if so in a colour of its own, or faded.</summary>
public sealed record FilterOverride(Guid FilterId, bool Visible = true, ColourRgb? Colour = null, bool Halftone = false);

/// <summary>
/// Design options (specification section 7): alternatives for part of the building, worked up
/// side by side - two schemes for a kitchen, three for an extension. Each set of options has
/// one primary, which every view shows unless told to show another; the rest of the building,
/// the main model, is in every one.
/// </summary>
public sealed class DesignOptionSet
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Option Set 1";

    public List<DesignOption> Options { get; } = new();

    public DesignOption? Primary => Options.FirstOrDefault(option => option.IsPrimary) ?? Options.FirstOrDefault();

    public DesignOptionSet Copy()
    {
        var copy = new DesignOptionSet { Id = Id, Name = Name };
        copy.Options.AddRange(Options.Select(option => option with { }));
        return copy;
    }
}

public sealed record DesignOption
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Option 1";

    public bool IsPrimary { get; set; }
}

public static class DesignOptions
{
    /// <summary>The set an option is in, and the option.</summary>
    public static (DesignOptionSet Set, DesignOption Option)? Find(BimDocument document, Guid optionId)
    {
        foreach (var set in document.DesignOptionSets)
            if (set.Options.FirstOrDefault(option => option.Id == optionId) is { } option)
                return (set, option);
        return null;
    }

    /// <summary>"Kitchen: Option 2 (primary)".</summary>
    public static string Describe(BimDocument document, Guid? optionId) =>
        optionId is { } id && Find(document, id) is var (set, option)
            ? $"{set.Name}: {option.Name}{(option.IsPrimary ? " (primary)" : string.Empty)}"
            : "Main Model";

    /// <summary>The option a view shows of a set: the one chosen for it, else the primary.</summary>
    public static Guid? ShownIn(BimDocument document, ViewReference view, DesignOptionSet set) =>
        document.ViewSettings.OptionShownIn(view, set.Id) is { } chosen && set.Options.Any(option => option.Id == chosen) ? chosen : set.Primary?.Id;

    /// <summary>
    /// A set made one: its primary's elements made part of the main model, every other option's
    /// deleted, and the set gone - one step to undo.
    /// </summary>
    public static IUndoableCommand AcceptPrimary(BimDocument document, DesignOptionSet set)
    {
        var primary = set.Primary?.Id;
        var kept = document.Elements.Where(element => element.DesignOptionId is { } id && id == primary).ToList();
        var others = set.Options.Where(option => option.Id != primary).Select(option => option.Id).ToHashSet();
        var dropped = document.Elements.Where(element => element.DesignOptionId is { } id && others.Contains(id)).ToList();
        var index = document.DesignOptionSets.IndexOf(set);

        var commands = new List<IUndoableCommand>(dropped.Select(element => (IUndoableCommand)new DeleteElementCommand(document, element)))
        {
            new DelegateCommand("Accept Primary",
                () =>
                {
                    foreach (var element in kept) element.DesignOptionId = null;
                    document.DesignOptionSets.Remove(set);
                    document.ViewSettings.Touch();
                },
                () =>
                {
                    foreach (var element in kept) element.DesignOptionId = primary;
                    document.DesignOptionSets.Insert(Math.Clamp(index, 0, document.DesignOptionSets.Count), set);
                    document.ViewSettings.Touch();
                })
        };

        return new CompositeCommand($"Accept Primary of {set.Name}", commands);
    }
}
