using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;

namespace BIMDesigner.UI;

/// <summary>
/// The structure analysed: how the loads and reactions balance case by case, each member's forces
/// and deflection under the combination or case picked, the deflection checks against their limits,
/// each support's reactions and the ground's, each plate's deflection and moments, and what was taken
/// and what went wrong - with the deflected shape shown in 3D. Once the model changes, it says so and
/// can be analysed again.
/// </summary>
internal sealed class AnalysisWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private StructuralAnalysis _analysis;
    private readonly ComboBox _under = new() { MinWidth = 260, Margin = new Thickness(6, 0, 16, 0) };
    private readonly CheckBox _shape = new() { Content = "Deflected shape in 3D", VerticalAlignment = VerticalAlignment.Center, IsChecked = true };
    private readonly DataGrid _balance = Grid(), _members = Grid(), _checks = Grid(), _supports = Grid(), _plates = Grid(), _foundations = Grid(), _strength = Grid(), _strengthDetail = Grid(), _sway = Grid(), _walls = Grid(), _wallDetail = Grid(), _slabs = Grid(), _slabDetail = Grid();
    private readonly TextBox _notes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(8) };
    private readonly TabItem _checksTab = new() { Header = "Deflection Checks" };
    private readonly TabItem _foundationsTab = new() { Header = "Foundations" };
    private readonly TabItem _strengthTab = new() { Header = "Strength" };
    private readonly TabItem _swayTab = new() { Header = "Sway" };
    private readonly TabItem _wallsTab = new() { Header = "Walls" };
    private readonly TabItem _slabsTab = new() { Header = "Slabs" };
    private readonly TextBlock _slabSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private IReadOnlyList<PlateCheck> _plateChecks = Array.Empty<PlateCheck>();
    private readonly DataGrid _joints = Grid(), _jointDetail = Grid();
    private readonly TabItem _jointsTab = new() { Header = "Joints" };
    private readonly TextBlock _jointSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private IReadOnlyList<JointCheck> _jointChecks = Array.Empty<JointCheck>();
    private readonly DataGrid _fires = Grid(), _fireDetail = Grid();
    private readonly TabItem _fireTab = new() { Header = "Fire" };
    private readonly TextBlock _fireSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private IReadOnlyList<FireCheck> _fireChecks = Array.Empty<FireCheck>();
    private readonly DataGrid _ties = Grid(), _tieDetail = Grid();
    private readonly TabItem _robustnessTab = new() { Header = "Robustness" };
    private readonly TextBlock _tieSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private RobustnessResult? _robustness;
    private readonly DataGrid _quake = Grid(), _quakeDetail = Grid();
    private readonly TabItem _quakeTab = new() { Header = "Seismic" };
    private readonly TextBlock _quakeSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DataGrid _removal = Grid();
    private readonly TabItem _removalTab = new() { Header = "Removal", Visibility = Visibility.Collapsed };
    private readonly TextBlock _removalSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private int _removalRun;
    private readonly DataGrid _bracing = Grid();
    private readonly TabItem _modesTab = new() { Header = "Modes" };
    private readonly TextBlock _swaySaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DataGrid _modes = Grid(), _vibration = Grid();
    private readonly TextBlock _modesSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private int _modesRun;
    private ModalResult? _modal;
    private IReadOnlyList<FloorVibration> _floors = Array.Empty<FloorVibration>();
    private readonly TabItem _bracingTab = new() { Header = "Bracing" };
    private readonly TextBlock _bracingSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private IReadOnlyList<FoundationCheck> _footingChecks = Array.Empty<FoundationCheck>();
    private readonly TextBlock _wallSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private IReadOnlyList<WallCheck> _wallChecks = Array.Empty<WallCheck>();
    private SecondOrderAnalysis? _second;
    private int _strengthRun;
    private readonly TextBlock _strengthSaid = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private IReadOnlyList<MemberCheck> _strengthChecks = Array.Empty<MemberCheck>();
    private readonly TextBlock _said = new();
    private readonly Button _report = new() { Content = "Calculations PDF...", IsEnabled = false, ToolTip = "The loads, analysis, sway and every check, as a PDF for whoever checks them" };
    private bool _stale;

    public AnalysisWindow(MainWindow main, StructuralAnalysis analysis)
    {
        _main = main;
        _analysis = analysis;
        DialogLook.Apply(this, "Structural Analysis", 1100, 620);
        MinWidth = 700;
        MinHeight = 340;

        var again = new Button { Content = "Analyse Again", ToolTip = "The model analysed again as it now stands" };
        again.Click += (_, e) => _main.OnAnalyse(this, e);
        _report.Click += (_, _) => SaveReport();
        var notes = new Button { Content = "Notes...", ToolTip = "The engineer's own notes for the calculations - the basis taken, what was assumed, what is to be confirmed on site - and who checked and approved them" };
        notes.Click += (_, _) => EditNotes();
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 8) };
        bar.Children.Add(new TextBlock { Text = "Under", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_under);
        bar.Children.Add(_shape);
        _under.SelectionChanged += (_, _) => Refresh();
        _shape.Click += (_, _) => ShowShape();

        var tabs = new TabControl { Margin = new Thickness(16, 0, 16, 6) };
        tabs.Items.Add(new TabItem { Header = "Balance", Content = _balance });
        tabs.Items.Add(new TabItem { Header = "Members", Content = _members });
        _checksTab.Content = _checks;
        tabs.Items.Add(_checksTab);
        _strengthTab.Content = StrengthPane();
        tabs.Items.Add(_strengthTab);
        _wallsTab.Content = Pane(_walls, _wallDetail, _wallSaid);
        _walls.SelectionChanged += (_, _) => ShowWall();
        tabs.Items.Add(_wallsTab);
        _slabsTab.Content = Pane(_slabs, _slabDetail, _slabSaid);
        _slabs.SelectionChanged += (_, _) => ShowSlab();
        tabs.Items.Add(_slabsTab);
        _jointsTab.Content = Pane(_joints, _jointDetail, _jointSaid);
        _joints.SelectionChanged += (_, _) => ShowJoint();
        tabs.Items.Add(_jointsTab);
        _fireTab.Content = Pane(_fires, _fireDetail, _fireSaid);
        _fires.SelectionChanged += (_, _) => ShowFireCheck();
        tabs.Items.Add(_fireTab);
        _robustnessTab.Content = Pane(_ties, _tieDetail, _tieSaid);
        var removalPane = new DockPanel();
        DockPanel.SetDock(_removalSaid, Dock.Bottom);
        removalPane.Children.Add(_removalSaid);
        removalPane.Children.Add(_removal);
        _removalTab.Content = removalPane;
        _ties.SelectionChanged += (_, _) => ShowTie();
        tabs.Items.Add(_robustnessTab);
        tabs.Items.Add(_removalTab);
        // Its text can run long - the system, the drift's basis, each irregularity - so it scrolls under both grids rather than taking their room.
        var quakePane = new DockPanel();
        var quakeText = new ScrollViewer { Content = _quakeSaid, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(quakeText, Dock.Bottom);
        quakePane.Children.Add(quakeText);
        quakePane.Children.Add(Pane(_quake, _quakeDetail, new TextBlock()));
        _quakeTab.Content = quakePane;
        tabs.Items.Add(_quakeTab);
        var bracingPane = new DockPanel();
        DockPanel.SetDock(_bracingSaid, Dock.Bottom);
        bracingPane.Children.Add(_bracingSaid);
        bracingPane.Children.Add(_bracing);
        _bracingTab.Content = bracingPane;
        tabs.Items.Add(_bracingTab);
        var swayPane = new DockPanel();
        DockPanel.SetDock(_swaySaid, Dock.Bottom);
        swayPane.Children.Add(_swaySaid);
        swayPane.Children.Add(_sway);
        _swayTab.Content = swayPane;
        tabs.Items.Add(_swayTab);
        // The modes over the timber floors, what was taken scrolling under both rather than taking their room.
        var modesPane = new DockPanel();
        var modesText = new ScrollViewer { Content = _modesSaid, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(modesText, Dock.Bottom);
        modesPane.Children.Add(modesText);
        modesPane.Children.Add(Pane(_modes, _vibration, new TextBlock()));
        _modesTab.Content = modesPane;
        tabs.Items.Add(_modesTab);
        tabs.Items.Add(new TabItem { Header = "Supports", Content = _supports });
        tabs.Items.Add(new TabItem { Header = "Plates", Content = _plates });
        _foundationsTab.Content = _foundations;
        tabs.Items.Add(_foundationsTab);
        tabs.Items.Add(new TabItem { Header = "Notes", Content = _notes });

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, notes, _report, again, close));
        root.Children.Add(DialogLook.Intro("The structure analysed here, linear and elastic: its members as beams and its plates as shell triangles about the mesh size apart, sharing their nodes where they meet, " +
                                           "held at its footings and piles and by the ground under a raft, each load case solved and each combination added up. Forces are kN and kNm, deflections mm. " +
                                           "Its members are checked for strength on the Strength tab, on second-order forces where the Sway tab says the structure needs them, masonry, stud and concrete walls on the Walls tab, concrete floors and rafts on the Slabs tab, joints and bearings on the Joints tab, the period it must stand in a fire on the Fire tab, what an accident asks of it on the Robustness tab and the ground on the Foundations tab."));
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(tabs);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) =>
        {
            _main.History.Changed -= OnHistoryChanged;
            _main.ShowDeflected(null);
        };
        Show(analysis);
    }

    /// <summary>The members, worst first, over the checks of the one picked.</summary>
    private FrameworkElement StrengthPane()
    {
        _strength.SelectionChanged += (_, _) => ShowMember();
        return Pane(_strength, _strengthDetail, _strengthSaid);
    }

    /// <summary>A list over the details of what is picked in it, what is said about that under them.</summary>
    private static FrameworkElement Pane(DataGrid list, DataGrid detail, TextBlock said)
    {
        var pane = new System.Windows.Controls.Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        var below = new DockPanel();
        DockPanel.SetDock(said, Dock.Bottom);
        below.Children.Add(said);
        below.Children.Add(detail);
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        System.Windows.Controls.Grid.SetRow(splitter, 1);
        System.Windows.Controls.Grid.SetRow(below, 2);
        pane.Children.Add(list);
        pane.Children.Add(splitter);
        pane.Children.Add(below);
        return pane;
    }

    private static string Share(double ratio) => double.IsInfinity(ratio) ? "over" : $"{ratio.ToString("P0", CultureInfo.CurrentCulture)}{(ratio > 1 ? " - too much" : "")}";

    /// <summary>How each combination stands up to sway, least stable first.</summary>
    private void ShowSway(SecondOrderAnalysis second)
    {
        string Said(SwayResult result) => result.Buckles
            ? "It buckles before it reaches its loads: stiffen it"
            : result.SecondOrder ? $"Taken to second order, in {result.Passes} pass{(result.Passes == 1 ? "" : "es")}" : "First order is enough";
        Fill(_sway, new[] { "Under", "αcr", "Amplifies sway by", "Moves at most", "What it comes to" }, second.Results.Select(result => new[]
        {
            result.Combination.Name, double.IsInfinity(result.Alpha) ? "no sway" : result.Alpha >= 1000 ? "over 1000" : result.Alpha.ToString("0.0", CultureInfo.CurrentCulture),
            double.IsInfinity(result.Amplifier) ? "-" : result.Amplifier.ToString("0.00", CultureInfo.CurrentCulture), double.IsInfinity(result.Movement) ? "-" : $"{F(result.Movement)} mm", Said(result)
        }));
        var taken = second.Results.Count(result => result.SecondOrder);
        _swayTab.Header = second.Results.Any(result => result.Buckles) || second.Bucklings.Any(buckling => buckling.Buckles) ? "Sway (buckles)" : taken > 0 ? $"Sway ({taken} second order)" : "Sway";
        _swaySaid.Text = string.Join(" ", second.Bucklings.Select(buckling => buckling.Said));
    }

    /// <summary>
    /// Sway and strength worked out away from the window, which stays free: a large structure takes a
    /// while. A run overtaken by another analysis, or by the window closing, comes to nothing.
    /// </summary>
    private void WorkOutStrength()
    {
        var (analysis, document, run) = (_analysis, _main.Document, ++_strengthRun);
        _report.IsEnabled = false;
        _second = null;
        _strengthChecks = Array.Empty<MemberCheck>();
        _strengthTab.Header = "Strength (working)";
        _swayTab.Header = "Sway (working)";
        _wallsTab.Header = "Walls (working)";
        _slabsTab.Header = "Slabs (working)";
        _jointsTab.Header = "Joints (working)";
        _fireTab.Header = "Fire (working)";
        _robustnessTab.Header = "Robustness (working)";
        _plateChecks = Array.Empty<PlateCheck>();
        _jointChecks = Array.Empty<JointCheck>();
        _joints.ItemsSource = null;
        _jointDetail.ItemsSource = null;
        _jointSaid.Text = string.Empty;
        _footingChecks = Array.Empty<FoundationCheck>();
        _slabs.ItemsSource = null;
        _slabDetail.ItemsSource = null;
        _slabSaid.Text = string.Empty;
        _wallChecks = Array.Empty<WallCheck>();
        _walls.ItemsSource = null;
        _wallDetail.ItemsSource = null;
        _wallSaid.Text = string.Empty;
        _strength.ItemsSource = null;
        _sway.ItemsSource = null;
        _strengthDetail.ItemsSource = null;
        _strengthSaid.Text = "Working out how the structure sways, then each member's strength...";
        StartModes();
        Task.Run(() =>
        {
            var worked = SecondOrderAnalysis.Run(document, analysis);
            _ = worked.Bucklings;
            var members = MemberStrength.Of(document, analysis, worked);
            var joints = JointDesign.Of(document, worked);
            var fires = FireDesign.Of(document, worked, members, joints);
            return (Second: worked, Checks: members, Walls: MasonryDesign.Of(document, worked), Plates: PlateDesign.Of(document, worked), Footings: FoundationDesign.Of(document, analysis), Joints: joints, Fires: fires, Robustness: Robustness.Of(document, worked));
        }).ContinueWith(done => Dispatcher.BeginInvoke(() => StrengthWorkedOut(done, run)), TaskScheduler.Default);
    }

    /// <summary>Back on the window's own thread: what was worked out shown, or why it could not be.</summary>
    private void StrengthWorkedOut(Task<(SecondOrderAnalysis Second, IReadOnlyList<MemberCheck> Checks, IReadOnlyList<WallCheck> Walls, IReadOnlyList<PlateCheck> Plates, IReadOnlyList<FoundationCheck> Footings, IReadOnlyList<JointCheck> Joints, IReadOnlyList<FireCheck> Fires, RobustnessResult Robustness)> done, int run)
    {
        if (run != _strengthRun || !IsLoaded) return;
        if (done.Exception is { } failed)
        {
            _strengthTab.Header = "Strength";
            _swayTab.Header = "Sway";
            _slabsTab.Header = "Slabs";
            _jointsTab.Header = "Joints";
            _fireTab.Header = "Fire";
            _robustnessTab.Header = "Robustness";
            _wallsTab.Header = "Walls";
            _strengthSaid.Text = $"This could not be worked out, the model perhaps changing as it was: {failed.GetBaseException().Message} Analyse Again.";
            return;
        }

        var (second, checks, walls, plates, footings, joints, fires, robustness) = done.Result;
        _second = second;
        ShowSway(second);
        _strengthChecks = checks.OrderByDescending(check => check.Ratio).ToList();
        _wallChecks = walls.OrderByDescending(check => check.Ratio).ToList();
        ShowWalls();
        _plateChecks = plates.OrderByDescending(check => check.Ratio).ToList();
        _footingChecks = footings.OrderByDescending(check => check.Ratio).ToList();
        ShowSlabs();
        _jointChecks = joints.OrderByDescending(check => check.Ratio).ToList();
        ShowJoints();
        _fireChecks = fires;
        ShowFires();
        _robustness = robustness;
        ShowRobustness();
        ShowStrength();
        Refresh();
        _report.IsEnabled = !_stale;
        StartRemoval(second);
    }

    /// <summary>The structure's natural modes and its timber floors' vibration, worked out away from the window beside the strength checks.</summary>
    private void StartModes()
    {
        var (analysis, document, run) = (_analysis, _main.Document, ++_modesRun);
        _modal = null;
        _floors = Array.Empty<FloorVibration>();
        _modesTab.Header = "Modes (working)";
        _modes.ItemsSource = null;
        _vibration.ItemsSource = null;
        _modesSaid.Text = "Working out how the structure vibrates...";
        Task.Run(() => (Modes: ModalAnalysis.Of(analysis), Floors: FloorVibrations.Of(document, analysis))).ContinueWith(done => Dispatcher.BeginInvoke(() =>
        {
            if (run != _modesRun || !IsLoaded) return;
            if (done.Exception is { } failed)
            {
                _modesTab.Header = "Modes";
                _modesSaid.Text = $"This could not be worked out: {failed.GetBaseException().Message} Analyse Again.";
                return;
            }

            (_modal, _floors) = done.Result;
            ShowModes();
        }), TaskScheduler.Default);
    }

    /// <summary>Each mode - its frequency, period and the mass it moves each way - then each timber floor's vibration checks, and what the periods come to for an earthquake.</summary>
    private void ShowModes()
    {
        if (_modal is not { } modal) return;
        var c = CultureInfo.CurrentCulture;
        var (sumX, sumY, sumZ) = (0.0, 0.0, 0.0);
        Fill(_modes, new[] { "Mode", "Frequency, Hz", "Period, s", "Moves mostly", "Mass along x", "Along y", "Up and down", "So far, x / y / z" }, modal.Modes.Select(mode =>
        {
            (sumX, sumY, sumZ) = (sumX + mode.ShareX, sumY + mode.ShareY, sumZ + mode.ShareZ);
            return new[]
            {
                mode.Number.ToString(c), mode.Frequency.ToString("0.00", c), mode.Period.ToString("0.000", c), mode.Way, mode.ShareX.ToString("P1", c), mode.ShareY.ToString("P1", c), mode.ShareZ.ToString("P1", c),
                $"{sumX.ToString("P0", c)} / {sumY.ToString("P0", c)} / {sumZ.ToString("P0", c)}"
            };
        }));
        Fill(_vibration, new[] { "Timber floor", "Joists", "f1, Hz", "Under 1 kN, mm", "a, mm", "Impulse velocity", "Limit", "Worst", "What it is taken as" }, _floors.Select(floor => new[]
        {
            _main.Document.FindType<SlabType>(floor.Floor.TypeId)?.Name ?? "Floor", floor.Joists.ToString(c), floor.Frequency.ToString("0.0", c),
            floor.Checks[1].Demand.ToString("0.00", c), floor.Checks[1].Capacity.ToString("0.00", c), floor.Checks[2].Demand.ToString("0.0", c), floor.Checks[2].Capacity.ToString("0.0", c), Share(floor.Ratio), floor.Said
        }), 8);
        var over = _floors.Count(floor => !floor.Enough);
        _modesTab.Header = over > 0 ? $"Modes ({over} over)" : "Modes";
        var said = new List<string>(modal.Notes);
        if (_analysis.Loading.Seismic is { Applies: true } quake)
        {
            var cu = ModalAnalysis.UpperLimit(quake.Sd1);
            foreach (var (axis, name) in new[] { (0, "x"), (1, "y") })
                if (modal.PeriodAlong(axis) is { } period)
                    said.Add($"Along {name} the analysis gives T = {period.ToString("0.000", c)} s, where Ta is {quake.Period.ToString("0.000", c)} s and Cu Ta {(cu * quake.Period).ToString("0.000", c)} s (ASCE 7-16 12.8.2, Table 12.8-1): the seismic loads take Ta, on the safe side.");
        }

        said.Add(_main.Document.ProjectInformation.ClearanceStandard.IsUs()
            ? "Floor vibration: the IRC and the NDS set no limit on it; the floors' deflections are checked as the Deflection Checks tab has them."
            : _floors.Count == 0 ? "Floor vibration by BS EN 1995-1-1 7.3.3 is worked out for floors on drawn timber joists; there are none here." : FloorVibrationsSaid);
        _modesSaid.Text = string.Join(" ", said);
    }

    private const string FloorVibrationsSaid = "Timber floors by BS EN 1995-1-1 7.3.3 with the UK National Annex, unloaded: f1 by (7.5), over 8 Hz or a special investigation is wanted; the deflection under 1 kN by the Annex's kdist and kamp 1.05 against a, and the unit impulse velocity by (7.6) and (7.7) against b^(f1 ζ - 1), ζ 0.02 - the Annex's figures as remembered, the deck taken as 22 mm boards at 3,500 N/mm².";

    /// <summary>
    /// A Consequence Class 2b building's notional removals, each support taken away in turn: a minute or so for a building of some size, so worked out after the rest, the calculations waiting for them.
    /// </summary>
    private void StartRemoval(SecondOrderAnalysis second)
    {
        var document = _main.Document;
        var run = ++_removalRun;
        var (storeys, _) = FireDesign.HeightOf(document);
        var asked = !document.ProjectInformation.ClearanceStandard.IsUs() && Robustness.ClassOf(FireDesign.UseOf(document), storeys) == "2b";
        _removalTab.Visibility = asked ? Visibility.Visible : Visibility.Collapsed;
        if (!asked) return;
        _removalTab.Header = "Removal (working)";
        _removal.ItemsSource = null;
        _removalSaid.Text = "Taking each column away in turn, a storey at a time, and each beam a column stands on...";
        _report.IsEnabled = false;
        var wallChecks = _wallChecks;
        Task.Run(() => NotionalRemoval.Of(document, second.Analysis, wallChecks)).ContinueWith(done => Dispatcher.BeginInvoke(() =>
        {
            if (run != _removalRun || !IsLoaded) return;
            if (done.Exception is { } failed)
            {
                _removalTab.Header = "Removal";
                _removalSaid.Text = $"This could not be worked out: {failed.GetBaseException().Message} Analyse Again.";
                return;
            }

            if (_robustness is { } robustness) _robustness = robustness with { Removal = done.Result };
            ShowRemoval(done.Result);
            _report.IsEnabled = !_stale;
        }), TaskScheduler.Default);
    }

    /// <summary>Each support taken away: whether the building stands, what fails and where, the floor at risk against its limit, and as a key element what it comes to.</summary>
    private void ShowRemoval(RemovalResult? result)
    {
        var removals = result?.Removals ?? Array.Empty<RemovalCheck>();
        Fill(_removal, new[] { "Taken away", "Stands", "Parts over", "Worst", "As a key element", "What it comes to, and the floor at risk, m², against its limit" }, removals.Select(removal => new[]
        {
            removal.Name, removal.Stands ? "yes" : "no", removal.Failed.ToString(CultureInfo.CurrentCulture), removal.Failed > 0 ? $"{Share(removal.Worst)}, {removal.WorstWhere}" : "-",
            removal.Stands ? "-" : removal.Key.Count == 0 ? "not worked out" : $"{Share(removal.Key.Max(check => check.Ratio))}, {removal.Key.MaxBy(check => check.Ratio)!.Name}",
            removal.AtRisk.Count == 0 ? removal.Said
                : $"{removal.Said} {string.Join("; ", removal.AtRisk.Select(at => $"{at.Level} {(at.Area / 1e6).ToString("0.0", CultureInfo.CurrentCulture)} of {(at.Limit / 1e6).ToString("0.0", CultureInfo.CurrentCulture)}"))}."
        }), 5);
        var short_ = removals.Count(removal => !removal.Enough);
        _removalTab.Header = short_ > 0 ? $"Removal ({short_} short)" : "Removal";
        _removalSaid.Text = result is null ? string.Empty : $"{result.Said} {result.Limits}";
    }

    /// <summary>The calculations saved where asked.</summary>
    /// <summary>
    /// The engineer's own notes for the calculations - each paragraph a blank line apart - and who checked and approved them,
    /// asked for and kept with the project as one step to undo.
    /// </summary>
    private void EditNotes()
    {
        var project = _main.Document.ProjectInformation;
        var notes = new TextBox
        {
            Text = project.EngineerNotes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 240, Width = 560, Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(4)
        };
        var checkedBy = new TextBox { Text = project.CheckedBy, Width = 260, Margin = new Thickness(0, 0, 0, 6) };
        var approvedBy = new TextBox { Text = project.ApprovedBy, Width = 260, Margin = new Thickness(0, 0, 0, 10) };
        var ok = new Button { Content = "OK", Padding = new Thickness(20, 5, 20, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "Notes for the calculations - the design basis taken, what was assumed, what is to be confirmed on site. A blank line starts a new paragraph. Each member's, wall's and slab's own Comments are put in too, under its check.", TextWrapping = TextWrapping.Wrap, Width = 560 });
        content.Children.Add(notes);
        content.Children.Add(new TextBlock { Text = "Checked by" });
        content.Children.Add(checkedBy);
        content.Children.Add(new TextBlock { Text = "Approved by" });
        content.Children.Add(approvedBy);
        content.Children.Add(buttons);
        var dialog = new System.Windows.Window
        {
            Title = "Engineer's Notes", Content = content, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Background, Foreground = Foreground
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        SetNotes(notes.Text.Trim(), checkedBy.Text.Trim(), approvedBy.Text.Trim());
    }

    /// <summary>The notes and names set as one step to undo - internal so tests and the harness can set them without the dialog.</summary>
    internal void SetNotes(string notes, string checkedBy, string approvedBy)
    {
        var project = _main.Document.ProjectInformation;
        var before = (project.EngineerNotes, project.CheckedBy, project.ApprovedBy);
        var after = (notes, checkedBy, approvedBy);
        if (before == after) return;
        _main.History.Execute(new Core.Views.DelegateCommand("Engineer's Notes",
            () => (project.EngineerNotes, project.CheckedBy, project.ApprovedBy) = after,
            () => (project.EngineerNotes, project.CheckedBy, project.ApprovedBy) = before));
        DialogLook.Say(_said, Paragraphs(notes) is var paragraphs && paragraphs > 0 ? $"{paragraphs} paragraph{(paragraphs == 1 ? "" : "s")} of notes, put in the calculations after their status." : "No notes of the engineer's own.");
    }

    private static int Paragraphs(string text) => Export.CalculationReport.Paragraphs(text).Count;

    private void SaveReport()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the structural calculations",
            Filter = "PDF document (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            FileName = Export.CalculationReport.SuggestedFileName(_main.Document)
        };
        if (dialog.ShowDialog(this) == true) SaveReportTo(dialog.FileName);
    }

    /// <summary>The calculations written to a file and how long they run said; their page count, or none where they could not be.</summary>
    internal int? SaveReportTo(string path)
    {
        if (_second is null) return null;
        try
        {
            var report = Export.CalculationReport.Build(_main.Document, _analysis, _second, _strengthChecks, _wallChecks, _plateChecks, _footingChecks, _jointChecks, _fireChecks, _robustness!, MainWindow.AppVersion, DateTime.Today, _modal, _floors);
            Export.CalculationReport.ToPdf(report, path);
            DialogLook.Say(_said, $"Calculations saved: {System.IO.Path.GetFileName(path)}, {report.PageCount} page{(report.PageCount == 1 ? "" : "s")}.");
            return report.PageCount;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            DialogLook.Warn(_said, $"Could not write the PDF: {exception.Message}");
            return null;
        }
    }

    /// <summary>Every wall's strength, worst first.</summary>
    private void ShowWalls()
    {
        Fill(_walls, new[] { "Wall", "Thickness", "Made of", "Of its strength", "Governed by", "Under", "By" }, _wallChecks.Select(check => new[]
        {
            check.Panel.Name, $"{check.Panel.Thickness:0} mm", check.Grade, check.Checks.Count > 0 ? Share(check.Ratio) : "not checked",
            check.Worst?.Name ?? "-", check.Worst?.Under ?? "-", check.By
        }));
        var over = _wallChecks.Count(check => !check.Enough);
        _wallsTab.Header = over > 0 ? $"Walls ({over} over)" : "Walls";
        _walls.SelectedIndex = _wallChecks.Count > 0 ? 0 : -1;
        ShowWall();
    }

    /// <summary>The checks of the wall picked, and how it stands.</summary>
    private void ShowWall()
    {
        if (_walls.SelectedIndex < 0 || _walls.SelectedIndex >= _wallChecks.Count)
        {
            _wallDetail.ItemsSource = null;
            _wallSaid.Text = string.Empty;
            return;
        }

        var wall = _wallChecks[_walls.SelectedIndex];
        Fill(_wallDetail, new[] { "Check", "It carries", "It can carry", "Of it", "Under", "Clause", "Note" }, wall.Checks.OrderByDescending(check => check.Ratio).Select(check => new[]
        {
            check.Name, $"{check.Demand.ToString("0.00", CultureInfo.CurrentCulture)} {check.Unit}".TrimEnd(), $"{check.Capacity.ToString("0.00", CultureInfo.CurrentCulture)} {check.Unit}".TrimEnd(),
            Share(check.Ratio), check.Under, check.Clause, check.Note
        }));
        _wallSaid.Text = wall.Checks.Count == 0 ? wall.Limits : $"{wall.Panel.Name} has an effective height of {Units.Metres(wall.EffectiveHeight, "0.00")} and an effective thickness of {wall.EffectiveThickness:0} mm. {wall.Limits}";
    }

    private static string Steel(double area) => double.IsInfinity(area) ? "too thin" : area.ToString("0", CultureInfo.CurrentCulture);

    /// <summary>Every concrete floor, roof and raft, worst first.</summary>
    /// <summary>How long the building has to stand in a fire, and every element of structure against it: what could not be judged first, then the worst.</summary>
    private void ShowFires()
    {
        Fill(_fires, new[] { "Element", "Kind", "Stands for", "Of its limit", "Governed by", "It wants" }, _fireChecks.Select(check => new[]
        {
            check.Name, check.Kind, $"{check.Minutes} min", check.Checks.Count > 0 ? Share(check.Ratio) : "not checked", check.Worst?.Name ?? "-", check.Wants.Length > 0 ? check.Wants : "-"
        }));
        var over = _fireChecks.Count(check => !check.Enough);
        var unknown = _fireChecks.Count(check => check.Checks.Count == 0);
        _fireTab.Header = over > 0 ? $"Fire ({over} over)" : unknown > 0 ? $"Fire ({unknown} unchecked)" : "Fire";
        _fires.SelectedIndex = _fireChecks.Count > 0 ? 0 : -1;
        ShowFireCheck();
    }

    private void ShowFireCheck()
    {
        var period = FireDesign.PeriodOf(_main.Document).Said;
        if (_fires.SelectedIndex < 0 || _fires.SelectedIndex >= _fireChecks.Count)
        {
            _fireDetail.ItemsSource = null;
            _fireSaid.Text = period;
            return;
        }

        var check = _fireChecks[_fires.SelectedIndex];
        Fill(_fireDetail, new[] { "Check", "It carries", "It can carry", "Of it", "Clause", "Note" }, check.Checks.OrderByDescending(item => item.Ratio).Select(item => new[]
        {
            item.Name, $"{item.Demand.ToString("0.00", CultureInfo.CurrentCulture)} {item.Unit}".TrimEnd(), $"{item.Capacity.ToString("0.00", CultureInfo.CurrentCulture)} {item.Unit}".TrimEnd(), Share(item.Ratio), item.Clause, item.Note
        }));
        _fireSaid.Text = $"{period}. {check.Limits}".Replace("..", ".", StringComparison.Ordinal);
    }

    /// <summary>The consequence class the building falls in, what it asks, and each tie it wants.</summary>
    private void ShowRobustness()
    {
        var ties = _robustness?.Ties ?? Array.Empty<JointCheck>();
        Fill(_ties, new[] { "Tie", "Kind", "Of its limit", "It wants" }, ties.Select(check => new[]
        {
            check.Name, check.Kind, check.Checks.Count > 0 ? Share(check.Ratio) : "-", check.Wants.Length > 0 ? check.Wants : "-"
        }));
        var over = ties.Count(check => !check.Enough);
        _robustnessTab.Header = over > 0 ? $"Robustness ({over} over)" : "Robustness";
        _ties.SelectedIndex = ties.Count > 0 ? 0 : -1;
        ShowTie();
    }

    private void ShowTie()
    {
        var said = _robustness is { } result ? $"{result.Said} {result.Asks} {result.Limits}".TrimEnd() : string.Empty;
        var ties = _robustness?.Ties ?? Array.Empty<JointCheck>();
        if (_ties.SelectedIndex < 0 || _ties.SelectedIndex >= ties.Count)
        {
            _tieDetail.ItemsSource = null;
            _tieSaid.Text = said;
            return;
        }

        var tie = ties[_ties.SelectedIndex];
        Fill(_tieDetail, new[] { "Check", "It carries", "It can carry", "Of it", "Clause", "Note" }, tie.Checks.OrderByDescending(item => item.Ratio).Select(item => new[]
        {
            item.Name, $"{item.Demand.ToString("0.00", CultureInfo.CurrentCulture)} {item.Unit}".TrimEnd(), $"{item.Capacity.ToString("0.00", CultureInfo.CurrentCulture)} {item.Unit}".TrimEnd(), Share(item.Ratio), item.Clause, item.Note
        }));
        _tieSaid.Text = $"{said} {tie.Limits}".TrimEnd();
    }

    /// <summary>The earthquake: how the design was come to, each level's weight and share of the base shear, and each storey's drift against its limit.</summary>
    private void ShowSeismic()
    {
        var quake = _analysis.Loading.Seismic;
        var result = _analysis.Seismic;
        if (result?.Modal is { } modal)
            Fill(_quake, new[] { "Level", "Weight", "Force along x", "Along y", "Storey shear, x", "Storey shear, y", "Its diaphragm, Fpx" }, result.Levels.Select((level, i) => new[]
            {
                level.Name, Units.Kilonewtons(level.Weight, "0"), Units.Kilonewtons(level.Force, "0.0"), Units.Kilonewtons(level.AlongY, "0.0"),
                Units.Kilonewtons(result.Levels.Skip(i).Sum(each => each.Force), "0.0"), Units.Kilonewtons(result.Levels.Skip(i).Sum(each => each.AlongY), "0.0"), Units.Kilonewtons(level.Diaphragm, "0.0")
            }));
        else
            Fill(_quake, new[] { "Level", "Weight", "Force", "Of the base shear", "Its diaphragm, Fpx" }, (result?.Levels ?? Array.Empty<SeismicLevel>()).Select(level => new[]
            {
                level.Name, Units.Kilonewtons(level.Weight, "0"), Units.Kilonewtons(level.Force, "0.0"), Share(result!.Shear > 0 ? level.Force / result.Shear : 0), Units.Kilonewtons(level.Diaphragm, "0.0")
            }));
        var rigid = quake is { Flexible: false };
        Fill(_quakeDetail, rigid
                ? new[] { "Case", "Storey", "Height", "Moves, elastic", "Design drift", "Allowed", "Of it", "θ, (12.8-16)", "Edge over average" }
                : new[] { "Case", "Storey", "Height", "Moves, elastic", "Design drift", "Allowed", "Of it", "θ, (12.8-16)" },
            (result?.Drifts ?? Array.Empty<SeismicDrift>()).Select(drift => new[]
            {
                drift.Case, drift.Level, Units.Bare(drift.Height), Units.Bare(drift.Elastic, "0.0"), Units.Bare(drift.Drift, "0.0"), Units.Bare(drift.Allowed, "0.0"), Share(drift.Ratio),
                drift.Theta > drift.ThetaMax ? $"{drift.Theta:0.000}, over {drift.ThetaMax:0.000}: unstable"
                    : drift.Theta > 0.1 ? $"{drift.Theta:0.000}: drift taken {drift.Increase:0.00} times, 1 / (1 - θ)" : $"{drift.Theta:0.000}",
                drift.Irregular.Length > 0 ? $"{drift.Torsion:0.00}, irregular {drift.Irregular}" : $"{drift.Torsion:0.00}"
            }.Take(rigid ? 9 : 8).ToArray()));
        var over = result?.Drifts.Count(drift => drift.Ratio > 1) ?? 0;
        _quakeTab.Header = over > 0 ? $"Seismic ({over} over)" : "Seismic";
        _quakeSaid.Text = quake is null
            ? _main.Document.ProjectInformation.ClearanceStandard.IsUs()
                ? "Set Ss and S1 for the site in the plan's Properties, from the ASCE hazard tool, for its seismic design to be worked out."
                : "Approved Document A 5.5: seismic design is not usually needed for buildings in consequence classes 1, 2a and 2b, so none is worked out."
            : result is null
                ? quake.Said
                : $"{quake.Said} Fa {quake.Fa:0.00}, Fv {quake.Fv:0.00}: SDS {quake.Sds:0.000}, SD1 {quake.Sd1:0.000}. {quake.System.Name}: R {quake.System.R:0.#}, Ω0 {quake.System.Overstrength:0.#}, Cd {quake.System.Cd:0.#}, Ie {quake.Importance:0.00}. T {quake.Period:0.000} s for {Units.Metres(quake.Height, "0.0")}, k {quake.Exponent:0.00}; Cs {quake.Coefficient:0.0000} by {quake.CoefficientSaid}. W {Units.Kilonewtons(result.Weight, "0")}{(result.Snow ? " with 20% of the snow (12.7.2)" : string.Empty)}, V = Cs W {Units.Kilonewtons(result.Shear, "0.0")}, each way. {(quake.RedundancySaid.Length > 0 ? quake.RedundancySaid : $"ρ {quake.Redundancy:0.0}")}. " +
                  (result.Procedure.Length > 0 ? $"{result.Procedure}. " : string.Empty) +
                  (result.Modal is { } done ? string.Join(" ", done.Ways.Select(way => $"Modal along {way.Way}: {way.Modes} modes moving {way.MassShare:P0} of the mass, the rest a rigid mode at 0.05 s; Vt {Units.Kilonewtons(way.Combined, "0.0")} by CQC against V {Units.Kilonewtons(way.Static, "0.0")}, forces by {way.Scale:0.000}{(way.Scale > 1 ? " (12.9.1.4.1)" : string.Empty)}, drift at {way.DriftScale:0.000} of them.")) + " " : string.Empty) +
                  (result.Modal is null ? $"Drift from Cs {quake.DriftCoefficient:0.0000}, without (12.8-5)'s least" : "Drift from the modal forces as 12.9.1.4.2 has them") + $"{(result.Cracked ? ", concrete and masonry cracked" : string.Empty)}. " +
                  (result.Modal is { } noted ? string.Join(" ", noted.Notes.Where(note => note.StartsWith("The modes found", StringComparison.Ordinal))) + " " : string.Empty) +
                  (quake.Flexible ? "Floors and roofs of timber, flexible diaphragms: no accidental torsion. " : "Accidental torsion 5% of each level's width across the forces, both ways. ") +
                  string.Join(" ", result.Irregularities ?? Array.Empty<string>()) + Anchored(result);
    }

    /// <summary>The walls' earthquake out of their plane, 12.11, said: the most pressure on a wall, and the most an anchorage to a flexible floor takes.</summary>
    private static string Anchored(SeismicResult result)
    {
        if (result.Anchorage is not { Count: > 0 } rows) return string.Empty;
        var most = rows.MaxBy(row => row.Pressure)!;
        var held = rows.Where(row => row.Anchor > 0).MaxBy(row => row.Anchor);
        return $" Walls out of their plane, 12.11.1: the most {most.Pressure:0.00} kPa, on {most.Wall}." +
               (held is null ? string.Empty : $" Anchored to a flexible floor or roof, (12.11-1): the most {held.Anchor:0.0} kN/m, {held.Wall} at {held.Level}, ka {held.Ka:0.00} for {Units.Metres(held.Span, "0.0")}.");
    }

    /// <summary>A US house's braced wall lines by the IRC, R602.10: what each wants by Method WSP and what it has, and what about where its panels stand does not do.</summary>
    private void ShowBracing()
    {
        var document = _main.Document;
        var result = IrcBracing.Of(document, _analysis);
        _bracingTab.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        var lines = result?.Lines ?? Array.Empty<BracedWallLine>();
        Fill(_bracing, new[] { "Braced wall line", "Long, ft", "Apart, ft", "Table, ft", "Factor", "Wants, ft", "Has, ft", "Of it", "Its storey, its factor and what is not as R602.10 asks" }, lines.Select(line => new[]
        {
            line.Name, line.Length.ToString("0.0", CultureInfo.CurrentCulture), line.Spacing.ToString("0.0", CultureInfo.CurrentCulture), line.Table.ToString("0.00", CultureInfo.CurrentCulture),
            line.Factor > 0 ? line.Factor.ToString("0.00", CultureInfo.CurrentCulture) : "-", line.Required.ToString("0.0", CultureInfo.CurrentCulture), line.Provided.ToString("0.0", CultureInfo.CurrentCulture),
            line.Enough ? Share(line.Ratio) : line.Faults.Count > 0 ? "not as asked" : Share(line.Ratio),
            string.Join(" ", new[] { $"{char.ToUpperInvariant(line.Location[0])}{line.Location[1..]}; {line.FactorSaid}." }.Concat(line.Faults).Concat(line.Notes))
        }), 8);
        var short_ = lines.Count(line => !line.Enough);
        _bracingTab.Header = short_ > 0 ? $"Bracing ({short_} short)" : "Bracing";
        _bracingSaid.Text = result?.Said ?? string.Empty;
    }

    /// <summary>Every joint and bearing, worst first, and what each wants.</summary>
    private void ShowJoints()
    {
        Fill(_joints, new[] { "Joint", "Kind", "Of its limit", "Governed by", "It wants" }, _jointChecks.Select(check => new[]
        {
            check.Name, check.Kind, check.Checks.Count > 0 ? Share(check.Ratio) : "not checked", check.Worst?.Name ?? "-", check.Wants.Length > 0 ? check.Wants : "-"
        }));
        var over = _jointChecks.Count(check => !check.Enough);
        _jointsTab.Header = over > 0 ? $"Joints ({over} over)" : "Joints";
        _joints.SelectedIndex = _jointChecks.Count > 0 ? 0 : -1;
        ShowJoint();
    }

    /// <summary>The checks of the joint picked.</summary>
    private void ShowJoint()
    {
        if (_joints.SelectedIndex < 0 || _joints.SelectedIndex >= _jointChecks.Count)
        {
            _jointDetail.ItemsSource = null;
            _jointSaid.Text = _jointChecks.Count == 0 ? "No joint in the model that can be looked at." : string.Empty;
            return;
        }

        var joint = _jointChecks[_joints.SelectedIndex];
        Fill(_jointDetail, new[] { "Check", "It carries", "It can carry", "Of it", "Under", "Clause", "Note" }, joint.Checks.OrderByDescending(check => check.Ratio).Select(check => new[]
        {
            check.Name, $"{check.Demand.ToString("0.00", CultureInfo.CurrentCulture)} {check.Unit}".TrimEnd(), $"{check.Capacity.ToString("0.00", CultureInfo.CurrentCulture)} {check.Unit}".TrimEnd(),
            Share(check.Ratio), check.Under, check.Clause, check.Note
        }));
        _jointSaid.Text = string.Join(" ", new[] { joint.Wants.Length > 0 ? $"It wants {joint.Wants}." : string.Empty, joint.Limits }.Where(text => text.Length > 0));
    }

    private void ShowSlabs()
    {
        Fill(_slabs, new[] { "Plate", "Kind", "Thickness", "Grade", "Bottom, mm²/m (x, y)", "Top, mm²/m (x, y)", "Of its limit", "Governed by" }, _plateChecks.Select(check => new[]
        {
            check.Panel.Name, check.Panel.Kind.ToString(), $"{check.Panel.Thickness:0} mm", check.Grade, check.Checks.Count > 0 ? $"{Steel(check.BottomX)}, {Steel(check.BottomY)}" : "-",
            check.Checks.Count > 0 ? $"{Steel(check.TopX)}, {Steel(check.TopY)}" : "-", check.Checks.Count > 0 ? Share(check.Ratio) : "not checked", check.Worst?.Name ?? "-"
        }));
        var over = _plateChecks.Count(check => !check.Enough);
        _slabsTab.Header = over > 0 ? $"Slabs ({over} over)" : "Slabs";
        _slabs.SelectedIndex = _plateChecks.Count > 0 ? 0 : -1;
        ShowSlab();
    }

    /// <summary>The checks of the plate picked.</summary>
    private void ShowSlab()
    {
        if (_slabs.SelectedIndex < 0 || _slabs.SelectedIndex >= _plateChecks.Count)
        {
            _slabDetail.ItemsSource = null;
            _slabSaid.Text = _plateChecks.Count == 0 ? "No concrete floor, roof or raft in the model." : string.Empty;
            return;
        }

        var plate = _plateChecks[_slabs.SelectedIndex];
        Fill(_slabDetail, new[] { "Check", "It carries", "It can carry", "Of it", "Under", "Clause", "Note" }, plate.Checks.OrderByDescending(check => check.Ratio).Select(check => new[]
        {
            check.Name, $"{check.Demand.ToString("0.00", CultureInfo.CurrentCulture)} {check.Unit}".TrimEnd(), $"{check.Capacity.ToString("0.00", CultureInfo.CurrentCulture)} {check.Unit}".TrimEnd(),
            Share(check.Ratio), check.Under, check.Clause, check.Note
        }));
        _slabSaid.Text = plate.Limits;
    }

    /// <summary>Every member's strength, worst first.</summary>
    private void ShowStrength()
    {
        Fill(_strength, new[] { "Member", "Kind", "Section", "Grade", "Of its strength", "Governed by", "Under", "By" }, _strengthChecks.Select(check => new[]
        {
            check.Member.Name, check.Member.Kind.ToString(), check.Member.Section.Name, check.Grade, check.Checks.Count > 0 ? Share(check.Ratio) : "not checked",
            check.Worst?.Name ?? "-", check.Worst?.Under ?? "-", check.By
        }));
        var over = _strengthChecks.Count(check => !check.Enough);
        _strengthTab.Header = over > 0 ? $"Strength ({over} over)" : "Strength";
        _strength.SelectedIndex = _strengthChecks.Count > 0 ? 0 : -1;
        ShowMember();
    }

    /// <summary>The checks of the member picked: what each carries and can, and how it was held.</summary>
    private void ShowMember()
    {
        if (_strength.SelectedIndex < 0 || _strength.SelectedIndex >= _strengthChecks.Count)
        {
            _strengthDetail.ItemsSource = null;
            _strengthSaid.Text = string.Empty;
            return;
        }

        var member = _strengthChecks[_strength.SelectedIndex];
        Fill(_strengthDetail, new[] { "Check", "It carries", "It can carry", "Of it", "Under", "Clause", "Note" }, member.Checks.OrderByDescending(check => check.Ratio).Select(check => new[]
        {
            check.Name, $"{check.Demand.ToString("0.00", CultureInfo.CurrentCulture)} {check.Unit}".TrimEnd(), $"{check.Capacity.ToString("0.00", CultureInfo.CurrentCulture)} {check.Unit}".TrimEnd(),
            Share(check.Ratio), check.Under, check.Clause, check.Note
        }));
        var lengths = member.Lengths;
        var sideways = lengths.Lateral > 0 ? $"sideways, {Units.Metres(lengths.Lateral, "0.00")}" : $"its top held by what is on it, its underside free over {Units.Metres(lengths.LateralHogging, "0.00")}";
        _strengthSaid.Text = $"{member.Member.Name} buckles over {Units.Metres(lengths.Strong, "0.00")} about its strong axis and {Units.Metres(lengths.Weak, "0.00")} about its weak; {sideways}. {member.Limits}";
    }

    private static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false
    };

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        _stale = true;
        _report.IsEnabled = false;
        DialogLook.Warn(_said, "The model has changed since it was analysed: Analyse Again for results as it now stands.");
    }

    /// <summary>A new analysis shown.</summary>
    internal void Show(StructuralAnalysis analysis)
    {
        _analysis = analysis;
        _stale = false;
        WorkOutStrength();
        var picked = (_under.SelectedItem as ComboBoxItem)?.Content as string;
        _under.Items.Clear();
        foreach (var combination in analysis.Loading.Combinations) _under.Items.Add(new ComboBoxItem { Content = $"{combination.Name}: {combination.Said}", Tag = combination });
        foreach (var loadCase in analysis.Loading.Cases) _under.Items.Add(new ComboBoxItem { Content = $"{loadCase.Name}: {loadCase.Description}", Tag = loadCase });
        _under.SelectedItem = _under.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Content == picked) ?? _under.Items.OfType<ComboBoxItem>().FirstOrDefault();
        Refresh();
    }

    private object? Loading => (_under.SelectedItem as ComboBoxItem)?.Tag;

    /// <summary>Fills a grid. A column of wordy notes can be given a width of its own to wrap in, or its longest note would push the figures beside it out of sight.</summary>
    private static void Fill(DataGrid grid, string[] headings, IEnumerable<string[]> rows, int wrapped = -1)
    {
        grid.Columns.Clear();
        for (var i = 0; i < headings.Length; i++)
        {
            var column = new DataGridTextColumn
            {
                Header = headings[i],
                Width = i == wrapped ? new DataGridLength(320) : i == headings.Length - 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                Binding = new Binding($"[{i}]")
            };
            if (i == wrapped)
            {
                var style = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
                style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                column.ElementStyle = style;
            }

            grid.Columns.Add(column);
        }

        // The theme holds every row to one line, which a wrapped note would be cut off by.
        if (wrapped >= 0)
        {
            grid.MinRowHeight = 28;
            grid.RowHeight = double.NaN;
        }

        grid.ItemsSource = rows.ToList();
    }

    private static string F(double value, string format = "0.0") => Math.Abs(value) < 0.05 && format == "0.0" ? "0.0" : value.ToString(format, CultureInfo.CurrentCulture);

    private void Refresh()
    {
        var analysis = _analysis;
        var document = _main.Document;
        if (Loading is not { } loading) return;

        // How the loads and the reactions balance, case by case.
        Fill(_balance, new[] { "Case", "What it is", "Loads, kN (x, y, z)", "Reactions, kN (x, y, z)", "Out of balance" }, analysis.Loading.Cases.Select(loadCase =>
        {
            var load = analysis.TotalLoad(loadCase);
            var reaction = analysis.TotalReaction(loadCase);
            var off = (load + reaction).Length;
            return new[]
            {
                loadCase.Name, loadCase.Description, $"{F(load.X)}, {F(load.Y)}, {F(load.Z)}", $"{F(reaction.X)}, {F(reaction.Y)}, {F(reaction.Z)}",
                load.Length < 1e-6 ? "-" : $"{(off / load.Length).ToString("P2", CultureInfo.CurrentCulture)}"
            };
        }));

        // Each member's greatest forces and its deflection under what is picked.
        Fill(_members, new[] { "Member", "Kind", "Section", "Axial N", "Shear Vz", "Shear Vy", "Moment My", "Moment Mz", "Torsion", "Deflection", "Span" },
            analysis.Model.Members.Select(member =>
            {
                var r = analysis.MemberOf(member, loading);
                return new[]
                {
                    member.Name, member.Kind.ToString(), member.Section.Name, F(r.Axial), F(r.ShearZ), F(r.ShearY), F(r.MomentY), F(r.MomentZ), F(r.Torsion, "0.00"),
                    $"{F(r.Deflection, "0.0")} mm", Units.Metres(r.Span, "0.00")
                };
            }));

        // Deflection against its limits.
        var checks = analysis.DeflectionChecks(document);
        var over = checks.Count(check => check.Ratio > 1);
        Fill(_checks, new[] { "Member", "Under", "Deflection", "Span", "Limit", "Of its limit", "Limit from" }, checks.Select(check => new[]
        {
            check.Member.Name, check.LimitSaid.Contains("imposed", StringComparison.Ordinal) || check.LimitSaid.Contains("live load,", StringComparison.Ordinal) ? "Imposed loads" : "All loads",
            $"{F(check.Deflection)} mm", Units.Metres(check.Span, "0.00"), $"span/{check.Limit:0} = {F(check.Span / check.Limit!.Value)} mm",
            check.Ratio is { } ratio ? $"{ratio.ToString("P0", CultureInfo.CurrentCulture)}{(ratio > 1 ? " - too much" : "")}" : "-", check.LimitSaid
        }));
        _checksTab.Header = over > 0 ? $"Deflection Checks ({over} over)" : "Deflection Checks";

        // Reactions.
        Fill(_supports, new[] { "Support", "Holds", "Fx", "Fy", "Fz (up)", "Mx", "My", "Mz" },
            analysis.Model.Supports.Select(support =>
            {
                var r = analysis.ReactionOf(support, loading);
                return new[]
                {
                    support.Name, support.IsLine ? $"{support.Panel!.Name} along its foot" : support.Node!.Name, F(r.Fx), F(r.Fy), F(r.Fz),
                    support.Fixity == SupportFixity.Fixed ? F(r.Mx) : "-", support.Fixity == SupportFixity.Fixed ? F(r.My) : "-", support.Fixity == SupportFixity.Fixed ? F(r.Mz) : "-"
                };
            }).Concat(analysis.Model.Panels.Where(panel => panel.Subgrade is not null).Select(panel =>
            {
                var r = analysis.GroundOf(panel, loading);
                return new[] { "Ground", $"under {panel.Name}", F(r.Fx), F(r.Fy), F(r.Fz), "-", "-", "-" };
            })));

        // Plates.
        Fill(_plates, new[] { "Plate", "Kind", "Thickness", "Deflection", "Moment along U, sagging", "Along V, sagging", "Along U, hogging", "Along V, hogging" },
            analysis.Model.Panels.Select(panel =>
            {
                var r = analysis.PanelOf(panel, loading);
                return new[]
                {
                    panel.Name, panel.Kind.ToString(), $"{panel.Thickness:0} mm", $"{F(r.Deflection)} mm", $"{F(r.MomentU, "0.00")} kNm/m", $"{F(r.MomentV, "0.00")} kNm/m",
                    $"{F(r.LeastMomentU, "0.00")} kNm/m", $"{F(r.LeastMomentV, "0.00")} kNm/m"
                };
            }));

        // The ground under rafts and slabs against what it bears, and ground beams' bars from their moments - whatever is picked.
        var bearings = analysis.BearingChecks(document);
        var designs = analysis.GroundBeamDesigns(document);
        string Amount(double value, string unit) => $"{value.ToString(Math.Abs(value) < 10 ? "0.00" : "0", CultureInfo.CurrentCulture)} {unit}";
        string Ratio(double? ratio) => ratio is { } r ? $"{r.ToString("P0", CultureInfo.CurrentCulture)}{(r > 1 ? " - too much" : "")}" : "-";
        string Needed(double needed, double least) => Units.SquareMillimetres(needed, "0") + (needed <= least + 0.5 ? ", the least" : "");
        var rows = bearings.Select(b => new[]
        {
            b.Panel.Name, "Ground pressure, all loads but wind", $"{Units.Kilopascals(b.Greatest, "0")} at most, {Units.Kilopascals(b.Average, "0")} on average, {F(b.Settlement)} mm down",
            b.Allowable is { } allowable ? Units.Kilopascals(allowable, "0") : "-", "-", Ratio(b.Ratio),
            b.AllowableSaid + (b.Lifts ? ". It lifts off the ground in places: the springs hold it down there, which the ground would not" : "")
        }).Concat(designs.SelectMany(d => new[]
        {
            new[]
            {
                d.Member.Name, "Bottom bars, sagging", $"{F(d.Sagging)} kNm, d {Units.Bare(d.BottomDepth)}", Needed(d.BottomNeeded, d.BottomLeast),
                $"{d.Beam.BottomBars}, {Units.SquareMillimetres(d.BottomGiven, "0")}", Ratio(d.BottomGiven > 0 ? d.BottomNeeded / d.BottomGiven : null), d.By + (d.Problem is { } p ? $". {p}" : "")
            },
            new[]
            {
                d.Member.Name, "Top bars, hogging", $"{F(d.Hogging)} kNm, d {Units.Bare(d.TopDepth)}", Needed(d.TopNeeded, d.TopLeast),
                $"{d.Beam.TopBars}, {Units.SquareMillimetres(d.TopGiven, "0")}", Ratio(d.TopGiven > 0 ? d.TopNeeded / d.TopGiven : null), d.By
            }
        })).ToList();
        foreach (var check in _footingChecks)
        {
            foreach (var each in check.Checks.OrderByDescending(item => item.Ratio))
                rows.Add(new[] { check.Name, each.Name, each.Note, Amount(each.Capacity, each.Unit), Amount(each.Demand, each.Unit), Ratio(each.Ratio), each.Clause });

            // Its first sentence: all of a long note would widen the column and push the figures out of sight. Warnings and the calculations have the rest.
            if (check.Limits.Length > 0) rows.Add(new[] { check.Name, check.Checks.Count > 0 ? "Note" : "Not checked", check.Limits.Split(". ")[0].TrimEnd('.') + ".", "-", "-", "-", "" });
        }

        Fill(_foundations, new[] { "Foundation", "Checked", "Found", "Allowed or needed", "Has", "Of it", "By" },
            rows.Count > 0 ? rows : new[] { new[] { "-", "No foundation that can be checked in the model", "", "", "", "", "" } }, 2);
        var shortfalls = bearings.Count(b => b.Ratio > 1) + designs.Count(d => !d.Enough) + _footingChecks.Count(check => !check.Enough);
        _foundationsTab.Header = shortfalls > 0 ? $"Foundations ({shortfalls} short)" : "Foundations";
        ShowSeismic();
        ShowBracing();

        _notes.Text = string.Join(Environment.NewLine + Environment.NewLine,
            analysis.Problems.DefaultIfEmpty("Nothing went wrong: every node is held, one way or another.")
                .Append("What was taken:").Concat(analysis.Notes).Concat(_second?.Notes ?? Array.Empty<string>()).Append(MemberStrength.Said).Append(FoundationDesign.Said));

        var weak = _strengthChecks.Count(check => !check.Enough);
        var thin = _wallChecks.Count(check => !check.Enough);
        if (!_stale)
            DialogLook.Say(_said, $"{analysis.Solver.NodeCount} nodes, {analysis.Solver.Beams.Count} beams, {analysis.Solver.Shells.Count} triangles; " +
                                  $"{analysis.Loading.Cases.Count} cases, {analysis.Loading.Combinations.Count} combinations{(over > 0 ? $"; {over} deflection{(over == 1 ? "" : "s")} over its limit" : "")}" +
                                  $"{(weak > 0 ? $"; {weak} member{(weak == 1 ? "" : "s")} over {(weak == 1 ? "its" : "their")} strength" : "")}" +
                                  $"{(thin > 0 ? $"; {thin} wall{(thin == 1 ? "" : "s")} over {(thin == 1 ? "its" : "their")} strength" : "")}" +
                                  $"{(_plateChecks.Any(check => !check.Enough) ? $"; {_plateChecks.Count(check => !check.Enough)} slab{(_plateChecks.Count(check => !check.Enough) == 1 ? "" : "s")} over" : "")}" +
                                  $"{(_jointChecks.Any(check => !check.Enough) ? $"; {_jointChecks.Count(check => !check.Enough)} joint{(_jointChecks.Count(check => !check.Enough) == 1 ? "" : "s")} over" : "")}" +
                                  $"{(_second?.Results.Any(result => result.Buckles) == true ? "; it buckles before it reaches its loads" : "")}.");
        ShowShape();
    }

    /// <summary>The deflected shape in 3D, made big enough to see: its greatest movement a twenty-fifth of the structure's size.</summary>
    private void ShowShape()
    {
        if (_shape.IsChecked != true || Loading is not { } loading)
        {
            _main.ShowDeflected(null);
            return;
        }

        var u = _analysis.DisplacementsOf(loading);
        var most = 0.0;
        for (var n = 0; n < _analysis.Solver.NodeCount; n++)
            most = Math.Max(most, Math.Sqrt(u[n * 6] * u[n * 6] + u[n * 6 + 1] * u[n * 6 + 1] + u[n * 6 + 2] * u[n * 6 + 2]));
        var nodes = Enumerable.Range(0, _analysis.Solver.NodeCount).Select(_analysis.Solver.NodeAt).ToList();
        var size = nodes.Count == 0 ? 1 : Math.Max(nodes.Max(p => p.X) - nodes.Min(p => p.X), Math.Max(nodes.Max(p => p.Y) - nodes.Min(p => p.Y), nodes.Max(p => p.Z) - nodes.Min(p => p.Z)));
        var scale = most < 1e-9 ? 1 : Math.Max(1, size / 25 / most);
        _main.ShowDeflected((_analysis, loading, scale));
    }
}
