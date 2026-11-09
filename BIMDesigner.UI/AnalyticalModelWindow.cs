using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;

namespace BIMDesigner.UI;

/// <summary>
/// The analytical model and its loads: its members and plates, what holds it, each load case and
/// how much it comes to, each combination the project's standard sets, and what is not joined up -
/// written out for an analysis program from here. It follows the model as it changes.
/// </summary>
internal sealed class AnalyticalModelWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly DataGrid _members = Grid(), _supports = Grid(), _cases = Grid(), _combinations = Grid();
    private readonly TextBox _problems = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(8)
    };
    private readonly TabItem _problemsTab = new() { Header = "Not Joined Up" };
    private readonly TextBlock _said = new();

    public AnalyticalModelWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Analytical Model and Loads", 1060, 600);
        MinWidth = 640;
        MinHeight = 320;

        var saf = new Button { Content = "Export SAF...", ToolTip = "The analytical model and its loads as SAF, the Structural Analysis Format: an .xlsx analysis programs read" };
        saf.Click += (_, e) => _main.OnExportSaf(this, e);
        var ifc = new Button { Content = "Export IFC...", ToolTip = "The model as IFC4 with its analytical model, supports, load cases and combinations" };
        ifc.Click += (_, e) => _main.OnExportIfcAnalytical(this, e);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var tabs = new TabControl { Margin = new Thickness(16, 0, 16, 6) };
        tabs.Items.Add(new TabItem { Header = "Members and Plates", Content = _members });
        tabs.Items.Add(new TabItem { Header = "Supports", Content = _supports });
        tabs.Items.Add(new TabItem { Header = "Load Cases", Content = _cases });
        tabs.Items.Add(new TabItem { Header = "Combinations", Content = _combinations });
        _problemsTab.Content = _problems;
        tabs.Items.Add(_problemsTab);

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, saf, ifc, close));
        root.Children.Add(DialogLook.Intro("The structure as an analysis program takes it: each structural column and beam a line, each bearing wall and concrete floor a plate, "
                                           + "joined where they meet and held where the foundations are - and the loads on it case by case, combined as the project's standard sets. "
                                           + "Turn Analytical Model on to see it over the plan and in 3D."));
        root.Children.Add(tabs);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Refresh();
    }

    private static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false
    };

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    private static void Fill(DataGrid grid, string[] headings, IEnumerable<string[]> rows)
    {
        grid.Columns.Clear();
        for (var i = 0; i < headings.Length; i++)
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = headings[i],
                Width = i == headings.Length - 1 ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                Binding = new System.Windows.Data.Binding($"[{i}]")
            });
        grid.ItemsSource = rows.ToList();
    }

    /// <summary>The model and its loads as they now stand.</summary>
    internal void Refresh()
    {
        var document = _main.Document;
        var model = AnalyticalModel.Of(document);
        var loading = StructuralLoading.Of(document, model);

        string Kind(AnalyticalMemberKind kind) => kind switch
        {
            AnalyticalMemberKind.GroundBeam => "Ground beam",
            _ => kind.ToString()
        };
        var rows = model.Members.Select(member => new[]
        {
            member.Name, Kind(member.Kind), string.Join(" - ", member.Nodes.Select(node => node.Name)), Units.Metres(member.Length, "0.00"),
            member.Section.Name, $"{member.Section.Material.Quality} ({member.Section.Material.Kind})",
            member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist ? $"{member.StartRelease} / {member.EndRelease}, line at its {member.SystemLine.ToLowerInvariant()}" : $"line at its {member.SystemLine.ToLowerInvariant()}"
        }).Concat(model.Panels.Select(panel => new[]
        {
            panel.Name, panel.Kind == AnalyticalPanelKind.GroundSlab ? "Slab on the ground" : panel.Kind.ToString(), string.Join(" - ", panel.Outline.Select(node => node.Name)),
            Units.SquareMetres(panel.Area, "0.0"), $"{Units.Bare(panel.Thickness)} plate", $"{panel.Material.Quality} ({panel.Material.Kind})",
            $"plane at its {panel.SystemPlane.ToLowerInvariant()}{(panel.Openings.Count > 0 ? $", {panel.Openings.Count} opening{(panel.Openings.Count == 1 ? "" : "s")}" : "")}{(panel.Inside.Count > 0 ? $", {panel.Inside.Count} node{(panel.Inside.Count == 1 ? "" : "s")} inside" : "")}{(panel.Subgrade is { } k ? $", on ground of {k:0} MN/m³" : "")}"
        }));
        Fill(_members, new[] { "Name", "Kind", "Nodes", "Length / Area", "Section", "Material", "Ends and plane" }, rows);

        Fill(_supports, new[] { "Name", "Holds", "Foundation", "Fixity" }, model.Supports.Select(support => new[]
        {
            support.Name,
            support.IsLine ? $"{support.Panel!.Name} along {support.From!.Name} - {support.To!.Name}" : support.Node!.Name,
            Core.Coordination.Comparisons.NameOf(document, support.Foundation),
            support.Fixity == SupportFixity.Fixed ? "Fixed" : support.IsLine ? "Pinned: held in place, free to turn about its line" : "Pinned: held in place, free to turn"
        }).Concat(model.Panels.Where(panel => panel.Subgrade is not null).Select(panel => new[]
        {
            "Ground", panel.Name, Core.Coordination.Comparisons.NameOf(document, panel.Element), $"{document.ProjectInformation.Ground}: {panel.Subgrade:0} MN/m³ under it"
        })));

        Fill(_cases, new[] { "Case", "What it is", "Loads", "Down", "Across", "From" }, loading.Cases.Select(loadCase =>
        {
            var count = loading.SurfaceLoads.Count(load => load.Case == loadCase) + loading.LineLoads.Count(load => load.Case == loadCase);
            var from = loading.SurfaceLoads.Where(load => load.Case == loadCase).Select(load => load.Said)
                .Concat(loading.LineLoads.Where(load => load.Case == loadCase).Select(load => load.Said)).FirstOrDefault() ?? string.Empty;
            return new[]
            {
                loadCase.Name, loadCase.Description, loadCase.Nature is LoadNature.SelfWeight or LoadNature.Seismic ? "by the analysis program" : count.ToString(),
                loadCase.Nature is LoadNature.SelfWeight || loadCase.Lateral ? "" : Units.Kilonewtons(loading.Total(loadCase), "#,0.0"),
                loadCase.Nature == LoadNature.Wind ? Units.Kilonewtons(loading.Across(loadCase), "#,0.0") : "",
                loadCase.Nature == LoadNature.Seismic && loading.Seismic is { } quake ? $"V = Cs W, Cs {quake.Coefficient:0.0000}, up the building as w h^{quake.Exponent:0.00}" : from
            };
        }));

        Fill(_combinations, new[] { "Name", "For", "Combination", "Expression" }, loading.Combinations.Select(combination => new[]
        {
            combination.Name, combination.State == LimitState.Ultimate ? "Strength" : "In use", combination.Said, combination.Description
        }));

        var problems = model.Problems.Concat(loading.Problems).ToList();
        _problemsTab.Header = problems.Count == 0 ? "Not Joined Up" : $"Not Joined Up ({problems.Count})";
        _problems.Text = string.Join(Environment.NewLine + Environment.NewLine,
            problems.Select(problem => problem.Elements.Count == 0 ? problem.Said : $"{Core.Coordination.Comparisons.NameOf(document, problem.Elements[0])}: {problem.Said}")
                .DefaultIfEmpty("Everything in the analytical model is joined up and held.")
                .Append("What the loads rest on and leave out:")
                .Concat(loading.Notes));

        DialogLook.Say(_said, model.Members.Count == 0 && model.Panels.Count == 0
            ? "Nothing is in the analytical model yet: draw structural columns and beams, or set walls' Structural Usage to Bearing."
            : $"{model.Members.Count} member{(model.Members.Count == 1 ? "" : "s")}, {model.Panels.Count} plate{(model.Panels.Count == 1 ? "" : "s")}, {model.Nodes.Count} nodes, {model.Supports.Count} support{(model.Supports.Count == 1 ? "" : "s")}; {loading.Cases.Count} load cases, {loading.Combinations.Count} combinations.");
    }
}
