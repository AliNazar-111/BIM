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
    private readonly DataGrid _balance = Grid(), _members = Grid(), _checks = Grid(), _supports = Grid(), _plates = Grid(), _foundations = Grid();
    private readonly TextBox _notes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(8) };
    private readonly TabItem _checksTab = new() { Header = "Deflection Checks" };
    private readonly TabItem _foundationsTab = new() { Header = "Foundations" };
    private readonly TextBlock _said = new();
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
        tabs.Items.Add(new TabItem { Header = "Supports", Content = _supports });
        tabs.Items.Add(new TabItem { Header = "Plates", Content = _plates });
        _foundationsTab.Content = _foundations;
        tabs.Items.Add(_foundationsTab);
        tabs.Items.Add(new TabItem { Header = "Notes", Content = _notes });

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, again, close));
        root.Children.Add(DialogLook.Intro("The structure analysed here, linear and elastic: its members as beams and its plates as shell triangles about the mesh size apart, sharing their nodes where they meet, " +
                                           "held at its footings and piles and by the ground under a raft, each load case solved and each combination added up. Forces are kN and kNm, deflections mm. " +
                                           "It does not check the members' strength, but for the ground under a raft and the bars in ground beams on the Foundations tab: hand them on with Export SAF or IFC for that."));
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

    private static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false
    };

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        _stale = true;
        DialogLook.Warn(_said, "The model has changed since it was analysed: Analyse Again for results as it now stands.");
    }

    /// <summary>A new analysis shown.</summary>
    internal void Show(StructuralAnalysis analysis)
    {
        _analysis = analysis;
        _stale = false;
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
        Fill(_foundations, new[] { "Foundation", "Checked", "Found", "Allowed or needed", "Has", "Of it", "By" },
            rows.Count > 0 ? rows : new[] { new[] { "-", "No raft, slab on the ground or ground beam in the model", "", "", "", "", "" } });
        var shortfalls = bearings.Count(b => b.Ratio > 1) + designs.Count(d => !d.Enough);
        _foundationsTab.Header = shortfalls > 0 ? $"Foundations ({shortfalls} short)" : "Foundations";

        _notes.Text = string.Join(Environment.NewLine + Environment.NewLine,
            analysis.Problems.DefaultIfEmpty("Nothing went wrong: every node is held, one way or another.")
                .Append("What was taken:").Concat(analysis.Notes));

        if (!_stale)
            DialogLook.Say(_said, $"{analysis.Solver.NodeCount} nodes, {analysis.Solver.Beams.Count} beams, {analysis.Solver.Shells.Count} triangles; " +
                                  $"{analysis.Loading.Cases.Count} cases, {analysis.Loading.Combinations.Count} combinations{(over > 0 ? $"; {over} deflection{(over == 1 ? "" : "s")} over its limit" : "")}.");
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
