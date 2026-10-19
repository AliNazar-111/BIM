using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>What kind of view a template is for.</summary>
public enum ViewTemplateKind
{
    /// <summary>Floor plans, and callouts of them.</summary>
    Plan,

    /// <summary>Sections, and callouts of them.</summary>
    Section,

    /// <summary>Elevations, of the outside and of rooms.</summary>
    Elevation,

    /// <summary>3D views and cameras.</summary>
    ThreeD
}

/// <summary>A setting a template can set on the views it governs.</summary>
public enum ViewTemplateSetting
{
    Scale,
    DetailLevel,
    WallFunctions,
    JoinDisplay,
    VisualStyle,
    HiddenKinds
}

/// <summary>
/// A view template (specification section 6.2): a named set of view settings - scale, detail
/// level, which walls show, how joins are cleaned, a 3D view's style and what it leaves out - each
/// included or not. Applied, it sets a view once; assigned, it governs the view: what it includes
/// is set by it and can only be changed in it, and a change to it reaches every view it governs.
/// </summary>
public sealed class ViewTemplate : Element
{
    public override BuiltInCategory Category => BuiltInCategory.ViewTemplates;

    public string Name { get; set; } = "Plan Template 1";

    public ViewTemplateKind For { get; set; }

    public bool IncludeScale { get; set; } = true;

    public double Scale { get; set; } = ViewSettings.DefaultScale;

    public bool IncludeDetailLevel { get; set; } = true;

    public DetailLevel DetailLevel { get; set; } = DetailLevel.Medium;

    public bool IncludeWallFunctions { get; set; } = true;

    public HashSet<WallFunction> HiddenWallFunctions { get; } = new();

    public bool IncludeJoinDisplay { get; set; } = true;

    public WallJoinDisplay JoinDisplay { get; set; } = WallJoinDisplay.CleanAllWallJoins;

    public bool IncludeVisualStyle { get; set; } = true;

    public VisualStyle VisualStyle { get; set; } = VisualStyle.HiddenLine;

    public bool IncludeHiddenKinds { get; set; } = true;

    public HashSet<MeshKind> HiddenKinds { get; } = new();

    /// <summary>Whether it sets a setting, and the setting means something for the views it is for.</summary>
    public bool Includes(ViewTemplateSetting setting) => setting switch
    {
        ViewTemplateSetting.Scale => IncludeScale && For != ViewTemplateKind.ThreeD,
        ViewTemplateSetting.DetailLevel => IncludeDetailLevel && For is ViewTemplateKind.Plan,
        ViewTemplateSetting.WallFunctions => IncludeWallFunctions,
        ViewTemplateSetting.JoinDisplay => IncludeJoinDisplay && For is ViewTemplateKind.Plan,
        ViewTemplateSetting.VisualStyle => IncludeVisualStyle && For == ViewTemplateKind.ThreeD,
        ViewTemplateSetting.HiddenKinds => IncludeHiddenKinds && For == ViewTemplateKind.ThreeD,
        _ => false
    };

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(ViewTemplateParameters.Name, () => Name, value => { if (!string.IsNullOrWhiteSpace(value)) Name = value.Trim(); });
        yield return ParameterValue.ReadOnly(ViewTemplateParameters.For, () => EnumText.Humanise(For));
        yield return ParameterValue.ReadOnly(ViewTemplateParameters.Governs, () => ViewTemplates.Governed(document, this).Count());
    }
}

public static class ViewTemplateParameters
{
    public static readonly ParameterDefinition Name = new("Template Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition For = new("For", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Governs = new("Views Governed", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

public static class ViewTemplates
{
    /// <summary>What kind of view a reference is, for templates; null for one no template is for - a schedule.</summary>
    public static ViewTemplateKind? KindOf(BimDocument document, ViewReference view) => view.Kind switch
    {
        ViewKind.FloorPlan => ViewTemplateKind.Plan,
        ViewKind.Section => ViewTemplateKind.Section,
        ViewKind.Elevation => ViewTemplateKind.Elevation,
        ViewKind.Model3D => ViewTemplateKind.ThreeD,
        ViewKind.Callout when document.FindElement(view.TargetId) is Callout callout =>
            callout.Parent.Kind == ViewKind.Section ? ViewTemplateKind.Section : ViewTemplateKind.Plan,
        _ => null
    };

    /// <summary>The templates that can govern a view.</summary>
    public static IEnumerable<ViewTemplate> For(BimDocument document, ViewReference view) =>
        KindOf(document, view) is { } kind
            ? document.Elements.OfType<ViewTemplate>().Where(template => template.For == kind).OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<ViewTemplate>();

    /// <summary>The template governing a view, if one does and it is still there.</summary>
    public static ViewTemplate? Of(BimDocument document, ViewReference view) =>
        document.ViewSettings.TemplateOf(view) is { } id ? document.FindElement(id) as ViewTemplate : null;

    /// <summary>Whether a setting of a view is set by the template governing it - and so can only be changed there.</summary>
    public static bool Locks(BimDocument document, ViewReference view, ViewTemplateSetting setting) =>
        Of(document, view) is { } template && template.Includes(setting);

    /// <summary>The views a template governs.</summary>
    public static IEnumerable<ViewReference> Governed(BimDocument document, ViewTemplate template) =>
        document.ViewSettings.Templated.Where(entry => entry.Template == template.Id).Select(entry => entry.View);

    /// <summary>The next free name for a template of a kind.</summary>
    public static string NextName(BimDocument document, ViewTemplateKind kind)
    {
        var stem = kind switch { ViewTemplateKind.ThreeD => "3D View", _ => EnumText.Humanise(kind) } + " Template";
        var names = document.Elements.OfType<ViewTemplate>().Select(template => template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
            if (!names.Contains($"{stem} {i}")) return $"{stem} {i}";
    }

    /// <summary>A template of a view as it is now: Revit's Create Template from Current View. Null for a view no template is for.</summary>
    public static ViewTemplate? FromView(BimDocument document, ViewReference view)
    {
        if (KindOf(document, view) is not { } kind) return null;
        var settings = document.ViewSettings;
        var template = new ViewTemplate
        {
            Name = NextName(document, kind), For = kind,
            Scale = settings.ScaleOf(view), DetailLevel = settings.DetailLevelOf(view) ?? DetailLevel.Fine, JoinDisplay = settings.JoinDisplayOf(view)
        };
        template.HiddenWallFunctions.UnionWith(settings.HiddenWallFunctions(view));
        if (Views3D.Find(document, view) is { } view3D)
        {
            template.VisualStyle = view3D.Style;
            template.HiddenKinds.UnionWith(view3D.HiddenKinds);
        }

        return template;
    }

    /// <summary>
    /// A template applied to a view once - its included settings set on the view, which then goes
    /// its own way - or, governing, assigned to it: set now, and again whenever the template changes.
    /// One step to undo.
    /// </summary>
    public static IUndoableCommand Apply(BimDocument document, ViewTemplate? template, ViewReference view, bool govern) =>
        new ViewStateCommand(document, new[] { view }, govern ? "Assign View Template" : "Apply View Template", () =>
        {
            if (govern) document.ViewSettings.SetTemplate(view, template?.Id);
            if (template is not null) Set(document, template, view);
        });

    /// <summary>A template changed, and every view it governs set from it again - in the same step as the change.</summary>
    public static IUndoableCommand Change(BimDocument document, ViewTemplate template, Action<ViewTemplate> change, string name)
    {
        var before = Snapshot(template);
        change(template);
        var after = Snapshot(template);
        Restore(template, before);

        return new CompositeCommand(name, new IUndoableCommand[]
        {
            new DelegateCommand(name, () => Restore(template, after), () => Restore(template, before)),
            new ViewStateCommand(document, Governed(document, template).ToList(), name, () =>
            {
                foreach (var view in Governed(document, template).ToList()) Set(document, template, view);
            })
        });
    }

    /// <summary>The settings a template includes, set on a view.</summary>
    private static void Set(BimDocument document, ViewTemplate template, ViewReference view)
    {
        var settings = document.ViewSettings;
        if (template.Includes(ViewTemplateSetting.Scale)) settings.SetScale(view, template.Scale);
        if (template.Includes(ViewTemplateSetting.DetailLevel)) settings.SetDetailLevel(view, template.DetailLevel);
        if (template.Includes(ViewTemplateSetting.JoinDisplay)) settings.SetJoinDisplay(view, template.JoinDisplay);
        if (template.Includes(ViewTemplateSetting.WallFunctions))
            foreach (var function in Enum.GetValues<WallFunction>())
                settings.SetWallFunctionVisible(view, function, !template.HiddenWallFunctions.Contains(function));

        if (Views3D.Find(document, view) is { } view3D)
        {
            if (template.Includes(ViewTemplateSetting.VisualStyle)) view3D.Style = template.VisualStyle;
            if (template.Includes(ViewTemplateSetting.HiddenKinds))
            {
                view3D.HiddenKinds.Clear();
                view3D.HiddenKinds.UnionWith(template.HiddenKinds);
            }
        }
    }

    private sealed record TemplateState(string Name, bool[] Includes, double Scale, DetailLevel Detail, WallFunction[] Walls, WallJoinDisplay Join, VisualStyle Style, MeshKind[] Kinds);

    private static TemplateState Snapshot(ViewTemplate t) => new(t.Name,
        new[] { t.IncludeScale, t.IncludeDetailLevel, t.IncludeWallFunctions, t.IncludeJoinDisplay, t.IncludeVisualStyle, t.IncludeHiddenKinds },
        t.Scale, t.DetailLevel, t.HiddenWallFunctions.ToArray(), t.JoinDisplay, t.VisualStyle, t.HiddenKinds.ToArray());

    private static void Restore(ViewTemplate t, TemplateState s)
    {
        t.Name = s.Name;
        (t.IncludeScale, t.IncludeDetailLevel, t.IncludeWallFunctions, t.IncludeJoinDisplay, t.IncludeVisualStyle, t.IncludeHiddenKinds) =
            (s.Includes[0], s.Includes[1], s.Includes[2], s.Includes[3], s.Includes[4], s.Includes[5]);
        (t.Scale, t.DetailLevel, t.JoinDisplay, t.VisualStyle) = (s.Scale, s.Detail, s.Join, s.Style);
        t.HiddenWallFunctions.Clear();
        t.HiddenWallFunctions.UnionWith(s.Walls);
        t.HiddenKinds.Clear();
        t.HiddenKinds.UnionWith(s.Kinds);
    }
}

/// <summary>
/// A change to some views' settings - and their 3D views', where they are 3D - kept whole on
/// either side, so it is undone exactly however much of them it touched.
/// </summary>
public sealed class ViewStateCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly IReadOnlyList<ViewReference> _views;
    private readonly Action _change;
    private List<ViewSettings.ViewState>? _before;
    private List<ViewSettings.ViewState>? _after;
    private List<(View3D View, View3DState State)>? _before3D;
    private List<(View3D View, View3DState State)>? _after3D;

    public ViewStateCommand(BimDocument document, IReadOnlyList<ViewReference> views, string name, Action change)
    {
        (_document, _views, Name, _change) = (document, views, name, change);
    }

    public string Name { get; }

    public void Redo()
    {
        if (_after is { } after && _after3D is { } after3D)
        {
            foreach (var state in after) _document.ViewSettings.Restore(state);
            foreach (var (view, state) in after3D) state.Apply(view, camera: false);
            return;
        }

        // The first time: what the views were, the change, and what they are now.
        _before = _views.Select(_document.ViewSettings.Capture).ToList();
        _before3D = Views3DOf().Select(view => (view, View3DState.Of(view))).ToList();
        _change();
        _after = _views.Select(_document.ViewSettings.Capture).ToList();
        _after3D = Views3DOf().Select(view => (view, View3DState.Of(view))).ToList();
    }

    public void Undo()
    {
        foreach (var state in _before ?? new List<ViewSettings.ViewState>()) _document.ViewSettings.Restore(state);
        foreach (var (view, state) in _before3D ?? new List<(View3D, View3DState)>()) state.Apply(view, camera: false);
    }

    private IEnumerable<View3D> Views3DOf() => _views.Select(view => Views3D.Find(_document, view)).Where(view => view is not null).Select(view => view!);
}

/// <summary>A change given as what it does and what undoes it.</summary>
public sealed class DelegateCommand : IUndoableCommand
{
    private readonly Action _redo;
    private readonly Action _undo;

    public DelegateCommand(string name, Action redo, Action undo) => (Name, _redo, _undo) = (name, redo, undo);

    public string Name { get; }

    public void Redo() => _redo();

    public void Undo() => _undo();
}
