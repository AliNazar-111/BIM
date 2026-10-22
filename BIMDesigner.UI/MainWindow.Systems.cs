using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Parameters;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.UI;

/// <summary>
/// The building services: fittings placed from their categories, systems made of the fittings
/// selected, edited by clicking fittings on and off and given what feeds them, the System
/// Browser, and each storey's mechanical, electrical and plumbing plans.
/// </summary>
public partial class MainWindow
{
    private SystemsWindow? _systemsWindow;

    internal BimDocument Document => _document;

    internal UndoStack History => _history;

    /// <summary>The categories a fitting of the services is placed from, in the order the Fitting menu lists them.</summary>
    private static readonly BuiltInCategory[] ServiceCategories =
    {
        BuiltInCategory.PlumbingFixtures, BuiltInCategory.MechanicalEquipment, BuiltInCategory.SpecialtyEquipment, BuiltInCategory.ElectricalEquipment,
        BuiltInCategory.ElectricalFixtures, BuiltInCategory.LightingDevices, BuiltInCategory.LightingFixtures, BuiltInCategory.FireAlarmDevices
    };

    /// <summary>The Fitting button: a menu of the fittings with services, by category - picking one starts placing it.</summary>
    private void OnPlaceFitting(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FittingButton, Placement = PlacementMode.Bottom };
        foreach (var category in ServiceCategories)
        {
            var types = _document.TypesOf<ComponentType>().Where(type => type.Kind == category && Connectors.Of(type).Count > 0).OrderBy(type => type.Name).ToList();
            if (types.Count == 0) continue;
            var group = new MenuItem { Header = EnumText.Humanise(category) };
            foreach (var type in types)
            {
                var item = new MenuItem { Header = type.Name };
                item.Click += (_, _) => PlaceFitting(type);
                group.Items.Add(item);
            }

            menu.Items.Add(group);
        }

        menu.IsOpen = true;
    }

    private void PlaceFitting(ComponentType type)
    {
        ComponentTool.IsChecked = true;
        ComponentTypePicker.SelectedItem = ComponentTypePicker.Items.OfType<ComponentFamilyChoice>().FirstOrDefault(choice => choice.Type == type);
        var kinds = Connectors.Of(type).Select(connector => connector.Kind.Title().ToLowerInvariant()).Distinct().ToList();
        StatusHint.Text = $"{type.Name}: click in the plan to put one down. Its connections - {string.Join(", ", kinds)} - are joined by making a system of it.";
    }

    /// <summary>Make System: a system of the fittings selected - of the one kind they can make, or the kind picked from a menu.</summary>
    private void OnMakeSystem(object sender, RoutedEventArgs e)
    {
        var picked = Plan.SelectedElements.OfType<Component>().Where(component => MepSystems.IsFitting(_document, component)).ToList();
        if (picked.Count == 0)
        {
            StatusHint.Text = "Select the fittings to make a system of first - basins, radiators, sockets, and what feeds them if you like - then Make System.";
            return;
        }

        var kinds = MepSystems.KindsFor(_document, picked);
        if (kinds.Count == 0)
        {
            StatusHint.Text = "Everything selected is on a system of each kind it has already: Edit System changes one.";
            return;
        }

        if (kinds.Count == 1)
        {
            MakeSystem(kinds[0], picked);
            return;
        }

        var menu = new ContextMenu { PlacementTarget = MakeSystemButton, Placement = PlacementMode.Bottom };
        foreach (var kind in kinds)
        {
            var count = picked.Count(component => MepSystems.ConnectorsOf(_document, component).Any(connector => connector.Kind == kind));
            var item = new MenuItem { Header = $"{kind.Title()}  ({count} of them)" };
            item.Click += (_, _) => MakeSystem(kind, picked);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    internal void MakeSystem(SystemKind kind, IReadOnlyList<Component> picked)
    {
        var (system, refusal) = MepSystems.Make(_document, kind, picked);
        if (system is null)
        {
            StatusHint.Text = $"{refusal}.";
            return;
        }

        _history.Execute(new AddElementCommand(_document, system, $"Make {system.Name}"));
        Plan.Select(system);
        RefreshProperties();
        var members = MepSystems.MembersOf(_document, system).Count;
        var fed = MepSystems.SourceOf(_document, system) is { } source
            ? $", fed by {MepSystems.Describe(_document, source)}"
            : kind.Fed() is { } feeds ? $" - nothing feeds it yet: Pick Source and click {feeds}" : string.Empty;
        StatusHint.Text = $"{system.Name} made of {members} fitting{(members == 1 ? string.Empty : "s")}{fed}. Edit System clicks more on.";
    }

    /// <summary>The system to act on: the one selected, or the one a fitting selected is on, or the one picked in the System Browser.</summary>
    private MepSystem? SystemInHand()
    {
        if (Plan.SelectedElements.OfType<MepSystem>().FirstOrDefault() is { } system) return system;
        if (Plan.SelectedElements.OfType<Component>().FirstOrDefault() is { } fitting && MepSystems.SystemsOf(_document, fitting).FirstOrDefault() is { } on) return on;
        return _systemsWindow is { IsLoaded: true } ? _systemsWindow.Picked : null;
    }

    private void OnEditSystem(object sender, RoutedEventArgs e)
    {
        if (SystemInHand() is { } system) EditSystemInPlan(system);
        else StatusHint.Text = "Select a system first - in the System Browser - or a fitting on one, then Edit System.";
    }

    private void OnPickSource(object sender, RoutedEventArgs e)
    {
        if (SystemInHand() is { } system) PickSourceInPlan(system);
        else StatusHint.Text = "Select a system first - in the System Browser - then Pick Source.";
    }

    internal void EditSystemInPlan(MepSystem system)
    {
        ShowPlanView();
        Plan.EditSystem(system);
        Plan.Focus();
    }

    internal void PickSourceInPlan(MepSystem system)
    {
        ShowPlanView();
        Plan.PickSystemSource(system);
        Plan.Focus();
    }

    private void OnSystemBrowser(object sender, RoutedEventArgs e) => OpenSystemBrowser();

    internal SystemsWindow OpenSystemBrowser()
    {
        if (_systemsWindow is { IsLoaded: true })
        {
            _systemsWindow.Activate();
            return _systemsWindow;
        }

        _systemsWindow = new SystemsWindow(this);
        if (IsLoaded) _systemsWindow.Owner = this;
        _systemsWindow.Show();
        return _systemsWindow;
    }

    /// <summary>Selects what was picked in the System Browser: a fitting on its own storey's plan, a system for its Properties.</summary>
    internal void SelectFromBrowser(Element? element)
    {
        if (element is Component fitting && _document.FindLevel(fitting.LevelId) is { } level && level.Id != Plan.ActiveLevelId)
            LevelPicker.SelectedItem = level;
        Plan.Select(element);
        RefreshProperties();
    }

    private void OnServicesPlan(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is string name && Enum.TryParse<Discipline>(name, out var discipline) && _document.FindLevel(Plan.ActiveLevelId) is { } level)
            ShowServicesPlan(level, discipline);
    }

    /// <summary>Opens one of a storey's plans: its own, or its mechanical, electrical or plumbing plan.</summary>
    internal void ShowServicesPlan(Level level, Discipline discipline)
    {
        Plan.ActiveCallout = null;
        LevelPicker.SelectedItem = level;
        Plan.ActiveDiscipline = discipline;
        ShowPlanView();
        RefreshProjectBrowser();
        if (discipline != Discipline.Architectural)
        {
            var open = _document.All<Component>().Where(component => component.LevelId == level.Id)
                .Count(component => MepSystems.OpenKinds(_document, component).Any(kind => kind.Discipline() == discipline));
            StatusHint.Text = $"{level.Name} {ServicesPlans.Title(discipline)}: the building faint, {discipline.Title().ToLowerInvariant()} fittings as they are" +
                              (open == 0 ? "." : $" - {open} not connected yet, in red.");
        }
    }

    /// <summary>The Project Browser's services plans: each storey's mechanical, electrical and plumbing plan.</summary>
    private TreeViewItem ServicesPlansItem()
    {
        var services = new TreeViewItem { Header = "Services Plans", IsExpanded = Plan.ActiveDiscipline != Discipline.Architectural };
        foreach (var discipline in new[] { Discipline.Mechanical, Discipline.Electrical, Discipline.Plumbing })
        {
            var group = new TreeViewItem { Header = $"{discipline.Title()} Plans", IsExpanded = Plan.ActiveDiscipline == discipline };
            foreach (var level in _document.Levels.OrderByDescending(level => level.Elevation))
            {
                var active = Plan.ActiveDiscipline == discipline && level.Id == Plan.ActiveLevelId && Plan.ActiveCallout is null;
                var item = new TreeViewItem { Header = level.Name, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, IsSelected = active };
                item.MouseLeftButtonUp += (_, args) =>
                {
                    ShowServicesPlan(level, discipline);
                    args.Handled = true;
                };
                group.Items.Add(item);
            }

            services.Items.Add(group);
        }

        return services;
    }
}
