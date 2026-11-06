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
        _swayTab.Content = _sway;
        tabs.Items.Add(_swayTab);
        tabs.Items.Add(new TabItem { Header = "Supports", Content = _supports });
        tabs.Items.Add(new TabItem { Header = "Plates", Content = _plates });
        _foundationsTab.Content = _foundations;
        tabs.Items.Add(_foundationsTab);
        tabs.Items.Add(new TabItem { Header = "Notes", Content = _notes });

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, _report, again, close));
        root.Children.Add(DialogLook.Intro("The structure analysed here, linear and elastic: its members as beams and its plates as shell triangles about the mesh size apart, sharing their nodes where they meet, " +
                                           "held at its footings and piles and by the ground under a raft, each load case solved and each combination added up. Forces are kN and kNm, deflections mm. " +
                                           "Its members are checked for strength on the Strength tab, on second-order forces where the Sway tab says the structure needs them, masonry, stud and concrete walls on the Walls tab, concrete floors and rafts on the Slabs tab, joints and bearings on the Joints tab and the ground on the Foundations tab."));
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
        _swayTab.Header = second.Results.Any(result => result.Buckles) ? "Sway (buckles)" : taken > 0 ? $"Sway ({taken} second order)" : "Sway";
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
        Task.Run(() =>
        {
            var worked = SecondOrderAnalysis.Run(document, analysis);
            return (Second: worked, Checks: MemberStrength.Of(document, analysis, worked), Walls: MasonryDesign.Of(document, worked), Plates: PlateDesign.Of(document, worked), Footings: FoundationDesign.Of(document, analysis), Joints: JointDesign.Of(document, worked));
        }).ContinueWith(done => Dispatcher.BeginInvoke(() => StrengthWorkedOut(done, run)), TaskScheduler.Default);
    }

    /// <summary>Back on the window's own thread: what was worked out shown, or why it could not be.</summary>
    private void StrengthWorkedOut(Task<(SecondOrderAnalysis Second, IReadOnlyList<MemberCheck> Checks, IReadOnlyList<WallCheck> Walls, IReadOnlyList<PlateCheck> Plates, IReadOnlyList<FoundationCheck> Footings, IReadOnlyList<JointCheck> Joints)> done, int run)
    {
        if (run != _strengthRun || !IsLoaded) return;
        if (done.Exception is { } failed)
        {
            _strengthTab.Header = "Strength";
            _swayTab.Header = "Sway";
            _slabsTab.Header = "Slabs";
            _jointsTab.Header = "Joints";
            _wallsTab.Header = "Walls";
            _strengthSaid.Text = $"This could not be worked out, the model perhaps changing as it was: {failed.GetBaseException().Message} Analyse Again.";
            return;
        }

        var (second, checks, walls, plates, footings, joints) = done.Result;
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
        ShowStrength();
        Refresh();
        _report.IsEnabled = !_stale;
    }

    /// <summary>The calculations saved where asked.</summary>
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
            var report = Export.CalculationReport.Build(_main.Document, _analysis, _second, _strengthChecks, _wallChecks, _plateChecks, _footingChecks, _jointChecks, MainWindow.AppVersion, DateTime.Today);
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

    private static void Fill(DataGrid grid, string[] headings, IEnumerable<string[]> rows)
    {
        grid.Columns.Clear();
        for (var i = 0; i < headings.Length; i++)
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = headings[i],
                Width = i == headings.Length - 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                Binding = new Binding($"[{i}]")
            });
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
            if (check.Limits.Length > 0) rows.Add(new[] { check.Name, "Not checked", check.Limits, "-", "-", "-", "" });
        }

        Fill(_foundations, new[] { "Foundation", "Checked", "Found", "Allowed or needed", "Has", "Of it", "By" },
            rows.Count > 0 ? rows : new[] { new[] { "-", "No foundation that can be checked in the model", "", "", "", "", "" } });
        var shortfalls = bearings.Count(b => b.Ratio > 1) + designs.Count(d => !d.Enough) + _footingChecks.Count(check => !check.Enough);
        _foundationsTab.Header = shortfalls > 0 ? $"Foundations ({shortfalls} short)" : "Foundations";

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
