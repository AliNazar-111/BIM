using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>
/// What each view leaves out (specification section 6.2, visibility and filters).
///
/// Settings belong to a view, not to the project: hiding interior partitions on the ground
/// floor plan to study the envelope does not hide them in the section or in 3D. A plan placed
/// on a sheet is the same view, so it shows the same thing there as on screen.
///
/// Only views that differ from the default are stored. A view nobody has filtered has no
/// entry, and shows everything.
/// </summary>
public sealed class ViewSettings
{
    private readonly Dictionary<ViewReference, HashSet<WallFunction>> _hiddenWallFunctions = new();
    private readonly Dictionary<ViewReference, double> _scales = new();
    private readonly Dictionary<ViewReference, WallJoinDisplay> _joinDisplays = new();
    private readonly Dictionary<ViewReference, DetailLevel> _detailLevels = new();
    private readonly Dictionary<ViewReference, Guid> _templates = new();
    private readonly Dictionary<ViewReference, DesignPhase> _phases = new();
    private readonly Dictionary<ViewReference, PhaseFilter> _phaseFilters = new();
    private readonly Dictionary<ViewReference, List<FilterOverride>> _filters = new();
    private readonly Dictionary<ViewReference, Dictionary<Guid, Guid>> _options = new();

    /// <summary>The phase a view shows the building in: new construction, unless set.</summary>
    public DesignPhase PhaseOf(ViewReference view) => _phases.TryGetValue(view, out var phase) ? phase : DesignPhase.New;

    public void SetPhase(ViewReference view, DesignPhase phase)
    {
        Version++;
        if (phase == DesignPhase.New) _phases.Remove(view);
        else _phases[view] = phase;
    }

    /// <summary>Every view in a phase of its own, for saving.</summary>
    public IEnumerable<(ViewReference View, DesignPhase Phase)> Phased => _phases.Select(entry => (entry.Key, entry.Value));

    /// <summary>What a view shows of what is there in its phase, and how: Show All, unless set.</summary>
    public PhaseFilter PhaseFilterOf(ViewReference view) => _phaseFilters.TryGetValue(view, out var filter) ? filter : PhaseFilter.ShowAll;

    public void SetPhaseFilter(ViewReference view, PhaseFilter filter)
    {
        Version++;
        if (filter == PhaseFilter.ShowAll) _phaseFilters.Remove(view);
        else _phaseFilters[view] = filter;
    }

    /// <summary>Every view with a phase filter of its own, for saving.</summary>
    public IEnumerable<(ViewReference View, PhaseFilter Filter)> PhaseFiltered => _phaseFilters.Select(entry => (entry.Key, entry.Value));

    /// <summary>The view filters added to a view, in order: a later one's colour over an earlier's.</summary>
    public IReadOnlyList<FilterOverride> FiltersOf(ViewReference view) =>
        _filters.TryGetValue(view, out var uses) ? uses : Array.Empty<FilterOverride>();

    public void SetFilters(ViewReference view, IEnumerable<FilterOverride> uses)
    {
        Version++;
        var list = uses.ToList();
        if (list.Count == 0) _filters.Remove(view);
        else _filters[view] = list;
    }

    /// <summary>Every view with filters of its own, for saving.</summary>
    public IEnumerable<(ViewReference View, IReadOnlyList<FilterOverride> Filters)> WithFilters => _filters.Select(entry => (entry.Key, (IReadOnlyList<FilterOverride>)entry.Value));

    /// <summary>The option a view has been set to show of a set; null where it shows the primary.</summary>
    public Guid? OptionShownIn(ViewReference view, Guid set) =>
        _options.TryGetValue(view, out var shown) && shown.TryGetValue(set, out var option) ? option : null;

    public void SetOptionShown(ViewReference view, Guid set, Guid? option)
    {
        Version++;
        if (!_options.TryGetValue(view, out var shown))
        {
            if (option is null) return;
            _options[view] = shown = new Dictionary<Guid, Guid>();
        }

        if (option is { } id) shown[set] = id;
        else shown.Remove(set);
        if (shown.Count == 0) _options.Remove(view);
    }

    /// <summary>Every view showing an option other than a set's primary, for saving.</summary>
    public IEnumerable<(ViewReference View, Guid Set, Guid Option)> OptionsShown =>
        _options.SelectMany(entry => entry.Value.Select(choice => (entry.Key, choice.Key, choice.Value)));

    /// <summary>Something every view draws from changed - a filter's rules, the design options: what is kept of them is out of date.</summary>
    public void Touch() => Version++;

    /// <summary>The view template governing a view, where one does.</summary>
    public Guid? TemplateOf(ViewReference view) => _templates.TryGetValue(view, out var id) ? id : null;

    public void SetTemplate(ViewReference view, Guid? template)
    {
        Version++;
        if (template is { } id) _templates[view] = id;
        else _templates.Remove(view);
    }

    /// <summary>Every view governed by a template, for saving.</summary>
    public IEnumerable<(ViewReference View, Guid Template)> Templated => _templates.Select(entry => (entry.Key, entry.Value));

    /// <summary>Everything set on one view: what a change to it is put back to.</summary>
    public sealed record ViewState(ViewReference View, double? Scale, DetailLevel? Detail, WallJoinDisplay? Join, WallFunction[] Hidden, Guid? Template,
        DesignPhase Phase, PhaseFilter PhaseFilter, FilterOverride[] Filters, (Guid Set, Guid Option)[] Options);

    public ViewState Capture(ViewReference view) => new(
        view,
        _scales.TryGetValue(view, out var scale) ? scale : null,
        DetailLevelOf(view),
        _joinDisplays.TryGetValue(view, out var join) ? join : null,
        HiddenWallFunctions(view).ToArray(),
        TemplateOf(view),
        PhaseOf(view),
        PhaseFilterOf(view),
        FiltersOf(view).ToArray(),
        _options.TryGetValue(view, out var shown) ? shown.Select(entry => (entry.Key, entry.Value)).ToArray() : Array.Empty<(Guid, Guid)>());

    public void Restore(ViewState state)
    {
        SetScale(state.View, state.Scale ?? DefaultScale);
        SetDetailLevel(state.View, state.Detail);
        SetJoinDisplay(state.View, state.Join ?? WallJoinDisplay.CleanAllWallJoins);
        foreach (var function in Enum.GetValues<WallFunction>()) SetWallFunctionVisible(state.View, function, !state.Hidden.Contains(function));
        SetTemplate(state.View, state.Template);
        SetPhase(state.View, state.Phase);
        SetPhaseFilter(state.View, state.PhaseFilter);
        SetFilters(state.View, state.Filters);
        _options.Remove(state.View);
        foreach (var (set, option) in state.Options) SetOptionShown(state.View, set, option);
    }

    /// <summary>How much detail a view is drawn in, where it has been set: a plan, a callout. Null where it never was.</summary>
    public DetailLevel? DetailLevelOf(ViewReference view) => _detailLevels.TryGetValue(view, out var level) ? level : null;

    public void SetDetailLevel(ViewReference view, DetailLevel? level)
    {
        Version++;
        if (level is { } set) _detailLevels[view] = set;
        else _detailLevels.Remove(view);
    }

    /// <summary>Every view with a detail level of its own, for saving.</summary>
    public IEnumerable<(ViewReference View, DetailLevel Level)> DetailLevels => _detailLevels.Select(entry => (entry.Key, entry.Value));

    /// <summary>How the view cleans the wall joins left to it. Every join, unless set otherwise.</summary>
    public WallJoinDisplay JoinDisplayOf(ViewReference view) =>
        _joinDisplays.TryGetValue(view, out var display) ? display : WallJoinDisplay.CleanAllWallJoins;

    /// <summary>Counted up each time anything here is changed: what a drawing kept of a view is checked against.</summary>
    public int Version { get; private set; }

    public void SetJoinDisplay(ViewReference view, WallJoinDisplay display)
    {
        Version++;
        if (display == WallJoinDisplay.CleanAllWallJoins) _joinDisplays.Remove(view);
        else _joinDisplays[view] = display;
    }

    /// <summary>Every view with its own Wall Join Display, for saving.</summary>
    public IEnumerable<(ViewReference View, WallJoinDisplay Display)> JoinDisplays => _joinDisplays.Select(e => (e.Key, e.Value));

    /// <summary>The scale a view is drawn at when nobody has chosen one.</summary>
    public const double DefaultScale = 100;

    /// <summary>
    /// The scale a view is meant to be drawn at, as the denominator of 1:n. It is what the view
    /// is placed on a sheet at, when it fits.
    /// </summary>
    public double ScaleOf(ViewReference view) => _scales.TryGetValue(view, out var scale) ? scale : DefaultScale;

    public void SetScale(ViewReference view, double denominator)
    {
        Version++;
        if (!double.IsFinite(denominator) || denominator <= 0 || denominator == DefaultScale) _scales.Remove(view);
        else _scales[view] = denominator;
    }

    /// <summary>Every view with a scale of its own, for saving.</summary>
    public IEnumerable<(ViewReference View, double Scale)> Scaled => _scales.Select(entry => (entry.Key, entry.Value));

    /// <summary>The wall functions this view does not draw.</summary>
    public IReadOnlySet<WallFunction> HiddenWallFunctions(ViewReference view) =>
        _hiddenWallFunctions.TryGetValue(view, out var hidden) ? hidden : new HashSet<WallFunction>();

    public bool IsWallFunctionVisible(ViewReference view, WallFunction function) =>
        !HiddenWallFunctions(view).Contains(function);

    public void SetWallFunctionVisible(ViewReference view, WallFunction function, bool visible)
    {
        Version++;
        if (!_hiddenWallFunctions.TryGetValue(view, out var hidden))
        {
            if (visible) return;
            _hiddenWallFunctions[view] = hidden = new HashSet<WallFunction>();
        }

        if (visible) hidden.Remove(function);
        else hidden.Add(function);

        if (hidden.Count == 0) _hiddenWallFunctions.Remove(view);
    }

    /// <summary>Every view with something hidden, for saving.</summary>
    public IEnumerable<(ViewReference View, IReadOnlySet<WallFunction> Hidden)> Filtered =>
        _hiddenWallFunctions.Select(entry => (entry.Key, (IReadOnlySet<WallFunction>)entry.Value));

    /// <summary>
    /// The test a view applies to each element: whether it is drawn at all.
    ///
    /// A hidden wall takes what it hosts with it - a door floating where a wall has been
    /// filtered out is not a door anyone can read - and a tag goes with what it labels.
    /// </summary>
    public Func<Element, bool> FilterFor(BimDocument document, ViewReference view)
    {
        var walls = WallFilter(document, view);
        var phase = PhaseOf(view);
        var phaseFilter = PhaseFilterOf(view);

        // The filters hiding what they pick out, and the option of each set the view shows.
        var hiding = FiltersOf(view).Where(use => !use.Visible)
            .Select(use => document.ParameterFilters.FirstOrDefault(filter => filter.Id == use.FilterId)).OfType<ParameterFilter>().ToList();
        var shownOf = new Dictionary<Guid, Guid?>();
        foreach (var set in document.DesignOptionSets)
        {
            var shown = DesignOptions.ShownIn(document, view, set);
            foreach (var option in set.Options) shownOf[option.Id] = shown;
        }

        if (walls is null && hiding.Count == 0 && shownOf.Count == 0 && phase == DesignPhase.New && phaseFilter is not (PhaseFilter.ShowPrevious or PhaseFilter.ShowPreviousAndDemolition)
            && !document.Elements.Any(element => element.PhaseCreated != DesignPhase.New || element.PhaseDemolished is not null))
            return _ => true;

        var picked = new Dictionary<Guid, bool>();
        bool Shows(Element element)
        {
            if (walls is not null && !walls(element)) return false;
            if (!Phasing.IsAnnotation(element.Category))
            {
                if (Phasing.StatusIn(element, phase) is not { } status || Phasing.Display(phaseFilter, status) == PhaseDisplay.NotDisplayed) return false;
            }
            else if (element is IHostedElement { HostId: var host } && host != element.Id && document.FindElement(host) is { } target && !Phasing.IsAnnotation(target.Category))
            {
                // A tag goes with what it labels.
                if (!Shows(target)) return false;
            }

            if (element.DesignOptionId is { } option && shownOf.TryGetValue(option, out var shown) && shown != option) return false;
            if (hiding.Count > 0)
            {
                if (!picked.TryGetValue(element.Id, out var hidden))
                    picked[element.Id] = hidden = hiding.Any(filter => filter.Picks(document, element));
                if (hidden) return false;
            }

            return true;
        }

        return Shows;
    }

    /// <summary>
    /// How a view draws each element it shows, beyond how it is drawn anyway: the phasing's
    /// overrides for what is existing, demolished or temporary, then each filter's colour or fading
    /// for what it picks out - a later filter's colour over an earlier's.
    /// </summary>
    public Func<Element, ElementStyle> StyleFor(BimDocument document, ViewReference view)
    {
        var phase = PhaseOf(view);
        var phaseFilter = PhaseFilterOf(view);
        var styling = FiltersOf(view).Where(use => use.Visible && (use.Colour is not null || use.Halftone))
            .Select(use => (Filter: document.ParameterFilters.FirstOrDefault(filter => filter.Id == use.FilterId), Use: use))
            .Where(entry => entry.Filter is not null).ToList();

        return element =>
        {
            var style = default(ElementStyle);
            if (!Phasing.IsAnnotation(element.Category) && Phasing.StatusIn(element, phase) is { } status && Phasing.Display(phaseFilter, status) == PhaseDisplay.Overridden)
                style = Phasing.Override(status);
            foreach (var (filter, use) in styling)
                if (filter!.Picks(document, element)) style = style.Then(new ElementStyle(use.Halftone, use.Colour));
            return style;
        };
    }

    /// <summary>The walls this view hides by their function, and what they carry; null where it hides none.</summary>
    private Func<Element, bool>? WallFilter(BimDocument document, ViewReference view)
    {
        var hidden = HiddenWallFunctions(view);
        if (hidden.Count == 0) return null;

        var hiddenWalls = document.Walls
            .Where(wall => document.GetWallType(wall) is { } type && hidden.Contains(type.Function))
            .Select(wall => wall.Id)
            .ToHashSet();

        var hiddenHosted = document.Elements
            .OfType<Opening>()
            .Where(opening => hiddenWalls.Contains(opening.HostWallId))
            .Select(opening => opening.Id)
            .ToHashSet();

        return element => element switch
        {
            Wall wall => !hiddenWalls.Contains(wall.Id),
            Opening opening => !hiddenHosted.Contains(opening.Id),
            IHostedElement hosted => !hiddenWalls.Contains(hosted.HostId) && !hiddenHosted.Contains(hosted.HostId),
            _ => true
        };
    }
}

/// <summary>Sets the scale a view is drawn at.</summary>
public sealed class SetViewScaleCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly ViewReference _view;
    private readonly double _old;
    private readonly double _new;

    public SetViewScaleCommand(BimDocument document, ViewReference view, double denominator)
    {
        _document = document;
        _view = view;
        _old = document.ViewSettings.ScaleOf(view);
        _new = denominator;
    }

    public string Name => "View Scale";

    public void Redo() => _document.ViewSettings.SetScale(_view, _new);

    public void Undo() => _document.ViewSettings.SetScale(_view, _old);
}

/// <summary>Shows or hides walls of one function in one view.</summary>
public sealed class SetWallFunctionVisibilityCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly ViewReference _view;
    private readonly WallFunction _function;
    private readonly bool _visible;
    private readonly bool _wasVisible;

    public SetWallFunctionVisibilityCommand(
        BimDocument document, ViewReference view, WallFunction function, bool visible)
    {
        _document = document;
        _view = view;
        _function = function;
        _visible = visible;
        _wasVisible = document.ViewSettings.IsWallFunctionVisible(view, function);
    }

    public string Name => _visible ? $"Show {EnumText.Humanise(_function)} Walls" : $"Hide {EnumText.Humanise(_function)} Walls";

    public void Redo() => _document.ViewSettings.SetWallFunctionVisible(_view, _function, _visible);

    public void Undo() => _document.ViewSettings.SetWallFunctionVisible(_view, _function, _wasVisible);
}
