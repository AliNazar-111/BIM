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
        BuiltInCategory.PlumbingFixtures, BuiltInCategory.Drainage, BuiltInCategory.MechanicalEquipment, BuiltInCategory.AirTerminals, BuiltInCategory.SpecialtyEquipment, BuiltInCategory.ElectricalEquipment,
        BuiltInCategory.ElectricalFixtures, BuiltInCategory.LightingDevices, BuiltInCategory.LightingFixtures, BuiltInCategory.FireAlarmDevices
    };

    /// <summary>The Fitting button: a menu of the fittings with services, by category - picking one starts placing it.</summary>
    private void OnPlaceFitting(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FittingButton, Placement = PlacementMode.Bottom };
        foreach (var category in ServiceCategories)
        {
            var types = _document.TypesOf<ComponentType>()
                .Where(type => type.Kind == category && ProjectTemplates.Fits(_document, type)
                                                     && (Connectors.Of(type).Count > 0 || type.Kind == BuiltInCategory.Drainage
                                                         || type.Form is ComponentForm.AirAdmittanceValve or ComponentForm.BackgroundVentilator or ComponentForm.DuctSmokeDetector))
                .OrderBy(type => type.Name).ToList();
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
        StatusHint.Text = $"What is selected could make {kinds.Count} kinds of system: pick which from the list. Make System again for another.";
    }

    internal void MakeSystem(SystemKind kind, IReadOnlyList<Component> picked)
    {
        var (system, refusal) = MepSystems.Make(_document, kind, picked);
        if (system is null)
        {
            StatusHint.Text = $"{refusal}.";
            return;
        }

        // Pipes and ducts already drawn to its fittings go on it with them.
        var (pipes, ducts) = MepSystems.Strays(_document, system);
        var make = new AddElementCommand(_document, system, $"Make {system.Name}");
        _history.Execute(pipes.Count + ducts.Count == 0
            ? make
            : new CompositeCommand($"Make {system.Name}", new IUndoableCommand[] { make, new SetPipeSystemCommand(pipes, system.Id), new SetDuctSystemCommand(ducts, system.Id) }));
        Plan.Select(system);
        RefreshProperties();
        var members = MepSystems.MembersOf(_document, system).Count;
        var fed = MepSystems.SourceOf(_document, system) is { } source
            ? $", fed by {MepSystems.Describe(_document, source)}"
            : kind.Fed() is { } feeds ? $" - nothing feeds it yet: Pick Source and click {feeds}" : string.Empty;
        var runs = pipes.Count > 0 ? $", with the {pipes.Count} pipe{(pipes.Count == 1 ? string.Empty : "s")} drawn to them" : ducts.Count > 0 ? $", with the {ducts.Count} duct{(ducts.Count == 1 ? string.Empty : "s")} drawn to them" : string.Empty;
        StatusHint.Text = $"{system.Name} made of {members} fitting{(members == 1 ? string.Empty : "s")}{runs}{fed}. Edit System clicks more on.";
    }

    private const string PipeSizeFromFitting = "As the fitting";

    private static readonly SystemKind[] PipeKinds = Enum.GetValues<SystemKind>().Where(kind => kind.Domain() == ConnectorDomain.Pipe).ToArray();

    private void ShowPipeOptions()
    {
        _loadingOptions = true;
        var types = _document.TypesOf<PipeType>().Where(each => ProjectTemplates.Fits(_document, each)).ToList();
        PipeTypePicker.ItemsSource = types;
        // A US project starts on its own copper tube sizes, an English one on BS EN 1057.
        var us = _document.ProjectInformation.ClearanceStandard.IsUs();
        var type = types.FirstOrDefault(each => each.Id == Plan.ActivePipeTypeId) ?? types.FirstOrDefault(each => each.Material.IsCts() == us && each.Material.Like() != PipeMaterial.PvcU) ?? types.FirstOrDefault();
        PipeTypePicker.SelectedItem = type;
        Plan.ActivePipeTypeId = type?.Id ?? Guid.Empty;

        var sizes = new[] { PipeSizeFromFitting }.Concat(type is null ? Array.Empty<string>() : PipeSizes.Of(type.Material).Select(size => size.Name)).ToList();
        PipeSizePicker.ItemsSource = sizes;
        PipeSizePicker.SelectedItem = Plan.NewPipeDiameter > 0 && type is not null ? PipeSizes.Nearest(type.Material, Plan.NewPipeDiameter).Name : PipeSizeFromFitting;
        PipeOffsetBox.Text = Core.Units.FormatLength(Plan.NewPipeOffset);
        PipeKindPicker.ItemsSource = PipeKinds.Select(kind => kind.Title()).ToList();
        PipeKindPicker.SelectedItem = Plan.NewPipeKind.Title();
        PipeFallBox.Text = Core.Units.FallText(Plan.NewPipeFall);
        PipeFallLabel.Text = Core.Units.Imperial ? "Fall (in/ft)" : "Fall (mm/m)";
        _loadingOptions = false;
    }

    private void OnPipeOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (PipeTypePicker.SelectedItem is PipeType type) Plan.ActivePipeTypeId = type.Id;
        var material = _document.FindType<PipeType>(Plan.ActivePipeTypeId)?.Material ?? PipeMaterial.Copper;
        Plan.NewPipeDiameter = PipeSizes.Of(material).FirstOrDefault(size => size.Name == PipeSizePicker.SelectedItem as string)?.Outside ?? 0;
        if (ParameterFormatter.TryParse(ParameterDataType.Length, PipeOffsetBox.Text, out var value) && value is double offset && Math.Abs(offset) <= 20000)
            Plan.NewPipeOffset = offset;
        if (PipeKinds.FirstOrDefault(kind => kind.Title() == PipeKindPicker.SelectedItem as string) is var picked && picked.Title() == PipeKindPicker.SelectedItem as string)
            Plan.NewPipeKind = picked;
        if (Core.Units.TryParseFall(PipeFallBox.Text, out var fall) && fall is > 0 and <= 200) Plan.NewPipeFall = fall;
        ShowPipeOptions();
    }

    /// <summary>Size Pipes: every cold and hot water pipe - of the systems selected, or all of them - sized as BS EN 806-3 asks, or in the US the IRC's Appendix AP, as one step.</summary>
    private void OnSizePipes(object sender, RoutedEventArgs e)
    {
        var picked = Plan.SelectedElements.OfType<MepSystem>()
            .Concat(Plan.SelectedElements.OfType<Pipe>().Select(pipe => _document.Find<MepSystem>(pipe.SystemId)).OfType<MepSystem>())
            .Distinct().ToList();
        var systems = picked.Count > 0 ? picked : _document.All<MepSystem>().ToList();
        var us = _document.ProjectInformation.ClearanceStandard.IsUs();
        var sized = PipeNetworks.Sized(_document, systems);

        // A US house's water as the table was read for it - or why it could not be.
        var water = us && systems.FirstOrDefault(system => PipeNetworks.IsWater(system.Kind)) is { } first ? PipeNetworks.UsWaterOf(_document, first) : null;
        if (sized.Count == 0)
        {
            StatusHint.Text = !_document.All<Pipe>().Any() ? "There are no pipes yet: draw them with Pipe, from the fittings to what feeds them."
                : water?.Beyond is { } beyond ? $"The water pipes cannot be sized by the IRC's Appendix AP: {beyond}."
                : us ? "Every water pipe traced from its source is the size the IRC's Appendix AP asks already."
                : "Every water pipe traced from its source is the size BS EN 806-3 asks already.";
            return;
        }

        _history.Execute(new SetPipeSizesCommand(sized));
        StatusHint.Text = us
            ? $"{sized.Count} pipe{(sized.Count == 1 ? string.Empty : "s")} sized: water by the IRC's Appendix AP - the water-supply fixture units beyond each (Table P2903.6) read off Table AP201.1"
              + (water is { Beyond: null } ? $" at {water.Pressure:0} psi at the highest outlet and {water.DevelopedFeet:0} ft developed to the furthest fixture ({water.Where})" : string.Empty)
              + "; heating by the heat beyond each."
            : $"{sized.Count} pipe{(sized.Count == 1 ? string.Empty : "s")} sized: water by the loading units of the draw-offs beyond each, as BS EN 806-3 asks; heating by the heat beyond each, within 1.0 m/s and 300 Pa a metre.";
    }

    private const string DuctSizeFromFitting = "As the spigot";

    private static readonly SystemKind[] DuctKinds = Enum.GetValues<SystemKind>().Where(kind => kind.Domain() == ConnectorDomain.Duct).ToArray();

    private void ShowDuctOptions()
    {
        _loadingOptions = true;
        var types = _document.TypesOf<DuctType>().Where(each => ProjectTemplates.Fits(_document, each)).ToList();
        DuctTypePicker.ItemsSource = types;
        var type = types.FirstOrDefault(each => each.Id == Plan.ActiveDuctTypeId) ?? types.FirstOrDefault();
        DuctTypePicker.SelectedItem = type;
        Plan.ActiveDuctTypeId = type?.Id ?? Guid.Empty;

        DuctSizePicker.ItemsSource = new[] { DuctSizeFromFitting }.Concat(type is null ? Array.Empty<string>() : DuctSizes.Of(type).Select(size => size.Name)).ToList();
        DuctSizePicker.SelectedItem = Plan.NewDuctWidth > 0 && type is not null ? DuctSizes.Nearest(type, Plan.NewDuctWidth, Plan.NewDuctHeight).Name : DuctSizeFromFitting;
        DuctOffsetBox.Text = Core.Units.FormatLength(Plan.NewDuctOffset);
        DuctKindPicker.ItemsSource = DuctKinds.Select(kind => kind.Title()).ToList();
        DuctKindPicker.SelectedItem = Plan.NewDuctKind.Title();
        _loadingOptions = false;
    }

    private void OnDuctOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (DuctTypePicker.SelectedItem is DuctType type) Plan.ActiveDuctTypeId = type.Id;
        var current = _document.FindType<DuctType>(Plan.ActiveDuctTypeId);
        var size = current is null ? null : DuctSizes.Of(current).FirstOrDefault(each => each.Name == DuctSizePicker.SelectedItem as string);
        (Plan.NewDuctWidth, Plan.NewDuctHeight) = size is null ? (0, 0) : (size.Width, size.Height);
        if (ParameterFormatter.TryParse(ParameterDataType.Length, DuctOffsetBox.Text, out var value) && value is double offset && Math.Abs(offset) <= 20000)
            Plan.NewDuctOffset = offset;
        if (DuctKinds.FirstOrDefault(kind => kind.Title() == DuctKindPicker.SelectedItem as string) is var picked && picked.Title() == DuctKindPicker.SelectedItem as string)
            Plan.NewDuctKind = picked;
        ShowDuctOptions();
    }

    /// <summary>Size Ducts: every duct - of the systems selected, or all of them - sized to the system's static pressure limit, as one step.</summary>
    private void OnSizeDucts(object sender, RoutedEventArgs e)
    {
        var picked = Plan.SelectedElements.OfType<MepSystem>()
            .Concat(Plan.SelectedElements.OfType<Duct>().Select(duct => _document.Find<MepSystem>(duct.SystemId)).OfType<MepSystem>())
            .Distinct().ToList();
        var sized = DuctNetworks.Sized(_document, picked.Count > 0 ? picked : _document.All<MepSystem>().ToList());
        if (sized.Count == 0)
        {
            StatusHint.Text = _document.All<Duct>().Any()
                ? "Every duct traced from a heat recovery unit is the size its system's pressure and a quiet 2.5 m/s ask already."
                : "There are no ducts yet: draw them with Duct, from the heat recovery unit's spigots to the valves.";
            return;
        }

        _history.Execute(new SetDuctSizesCommand(sized));
        StatusHint.Text = $"{sized.Count} duct{(sized.Count == 1 ? string.Empty : "s")} sized by equal friction, to keep each system within Approved Document F's static pressure (Table 1.8), and its air to 2.5 m/s for quiet.";
    }

    private EnergyRatingWindow? _energyRatingWindow;

    private void OnEnergyRating(object sender, RoutedEventArgs e) => OpenEnergyRating();

    /// <summary>The Energy Rating window: the home's SAP 10.3 rating and its Future Homes Standard check.</summary>
    internal EnergyRatingWindow OpenEnergyRating()
    {
        if (_energyRatingWindow is { IsLoaded: true })
        {
            _energyRatingWindow.Activate();
            return _energyRatingWindow;
        }

        _energyRatingWindow = new EnergyRatingWindow(this);
        if (IsLoaded) _energyRatingWindow.Owner = this;
        _energyRatingWindow.Show();
        return _energyRatingWindow;
    }

    private HeatLossWindow? _heatLossWindow;

    private void OnHeatLoss(object sender, RoutedEventArgs e) => OpenHeatLoss();

    /// <summary>The Heat Loss window: each room's heat loss and what heats it, the building's load and its heat sources.</summary>
    internal HeatLossWindow OpenHeatLoss()
    {
        if (_heatLossWindow is { IsLoaded: true })
        {
            _heatLossWindow.Activate();
            return _heatLossWindow;
        }

        _heatLossWindow = new HeatLossWindow(this);
        if (IsLoaded) _heatLossWindow.Owner = this;
        _heatLossWindow.Show();
        return _heatLossWindow;
    }

    private void OnCutOpenings(object sender, RoutedEventArgs e) => StatusHint.Text = CutOpenings();

    /// <summary>Cuts an opening wherever a pipe or duct passes through a wall or floor with none, as one step to undo, and says what it did.</summary>
    internal string CutOpenings()
    {
        var openings = Penetrations.Openings(_document);
        if (openings.Count == 0)
            return Penetrations.All(_document).Count == 0
                ? "No pipe or duct passes through a wall or a floor."
                : "Every pipe and duct passing through a wall or a floor has its opening already.";
        _history.Execute(new AddElementsCommand(_document, openings, "Cut Openings"));
        var walls = openings.OfType<WallOpening>().Count();
        var floors = openings.Count - walls;
        return $"{openings.Count} opening{(openings.Count == 1 ? string.Empty : "s")} cut for the services - {walls} through walls, {floors} through floors - " +
               $"each its pipe or duct and {Core.Units.Millimetres(Penetrations.Gap, "0")} round it for fire-stopping.";
    }

    private ElectricalWindow? _electricalWindow;

    private void OnElectrical(object sender, RoutedEventArgs e) => OpenElectrical();

    /// <summary>The Electrical window: each board's supply and demand, its circuits' breakers and cables, and each room's light.</summary>
    internal ElectricalWindow OpenElectrical()
    {
        if (_electricalWindow is { IsLoaded: true })
        {
            _electricalWindow.Activate();
            return _electricalWindow;
        }

        _electricalWindow = new ElectricalWindow(this);
        if (IsLoaded) _electricalWindow.Owner = this;
        _electricalWindow.Show();
        return _electricalWindow;
    }

    private void OnSizeCircuits(object sender, RoutedEventArgs e) => StatusHint.Text = SizeCircuits();

    /// <summary>Gives the circuits selected - or every circuit - the breaker and cable their use and run ask, as one step to undo, and says what it did.</summary>
    internal string SizeCircuits()
    {
        var picked = Plan.SelectedElements.OfType<MepSystem>().Where(system => system.Kind == SystemKind.Power).ToList();
        var sized = Circuits.Sized(_document, picked.Count > 0 ? picked : Circuits.Of(_document));
        if (sized.Count == 0)
            return Circuits.Of(_document).Count == 0
                ? "There are no circuits yet: select fittings with the consumer unit and Make System."
                : "Every circuit has the breaker and cable Size Circuits chooses already.";
        _history.Execute(new SetCircuitsCommand(sized));
        var left = Circuits.Of(_document).Select(circuit => Circuits.Design(_document, circuit)).OfType<CircuitDesign>().Count(design => Circuits.Faults(_document, design).Count > 0);
        return $"{sized.Count} circuit{(sized.Count == 1 ? string.Empty : "s")} given the breaker and cable {(sized.Count == 1 ? "its" : "their")} use and run ask" +
               (left > 0 ? $"; {left} still wanting - a shorter run, another way of installing it, or splitting it." : ".");
    }

    /// <summary>Light Data: a maker's IES or EULUMDAT file loaded into the type of the light selected.</summary>
    private void OnPhotometricFile(object sender, RoutedEventArgs e)
    {
        var light = Plan.SelectedElements.OfType<Component>().FirstOrDefault(component => _document.FindType<ComponentType>(component.TypeId)?.Kind == BuiltInCategory.LightingFixtures);
        if (light is null || _document.FindType<ComponentType>(light.TypeId) is not { } type)
        {
            StatusHint.Text = "Select a light first: its maker's photometric file is loaded into its type, for every light of the type.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Light Data for {type.Name}", Filter = "Photometric files (*.ies;*.ldt)|*.ies;*.ldt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        StatusHint.Text = LoadLightData(type, dialog.FileName);
    }

    /// <summary>Loads a photometric file into a light's type as one step to undo, and says what it read.</summary>
    internal string LoadLightData(ComponentType type, string path)
    {
        try
        {
            var photometry = Photometry.Read(System.IO.File.ReadAllText(path), System.IO.Path.GetFileName(path));
            _history.Execute(new SetPhotometryCommand(type, photometry));
            return $"{type.Name}: {photometry.Describe()}. Each room's light now takes its lumens, and its direct light point by point.";
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IndexOutOfRangeException or ArgumentException or System.IO.IOException)
        {
            return $"{System.IO.Path.GetFileName(path)} could not be read as an IES or EULUMDAT file: {exception.Message}";
        }
    }

    private void OnSizeRadiators(object sender, RoutedEventArgs e) => StatusHint.Text = SizeRadiators();

    /// <summary>Sizes every room's radiators to its heat loss as one step to undo, and says what it did.</summary>
    internal string SizeRadiators()
    {
        var (command, changed, unmet) = Heating.SizeRadiators(_document);
        if (command is not null) _history.Record(command);
        var said = changed > 0
            ? $"{changed} radiator{(changed == 1 ? string.Empty : "s")} sized to {(changed == 1 ? "its room's" : "their rooms'")} heat loss at {(changed == 1 ? "its" : "their")} zone's temperatures."
            : !_document.All<Component>().Any(component => _document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.Radiator)
                ? "There are no radiators to size: place them in the rooms from Systems → Fitting."
                : unmet.Count == 0 ? "Every room's radiators give its heat loss already." : "No radiator changed.";
        return unmet.Count == 0 ? said : $"{said} Short still - {string.Join("; ", unmet)}: another radiator, less heat lost or a warmer flow.";
    }

    /// <summary>Long Section: the run through the drain selected, from its head to its outfall, in a window of its own.</summary>
    private void OnDrainSection(object sender, RoutedEventArgs e)
    {
        var picked = Plan.SelectedElements.OfType<Pipe>().FirstOrDefault(pipe => Drainage.IsDrainage(pipe.Kind));
        if (picked is null || DrainSections.Through(_document, picked) is not { } section)
        {
            StatusHint.Text = "Select a foul or rainwater drain below the ground, on a system with an outfall, then Long Section.";
            return;
        }

        new DrainSectionWindow(section) { Owner = IsLoaded ? this : null }.Show();
        StatusHint.Text = $"{section.System.Name}: {Core.Units.Metres(section.Length, "0.0")} of drain, {section.Accesses.Count} access point{(section.Accesses.Count == 1 ? string.Empty : "s")} along it.";
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
        if (element is not null and not MepSystem && _document.FindLevel(element.LevelId) is { } level && level.Id != Plan.ActiveLevelId)
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
