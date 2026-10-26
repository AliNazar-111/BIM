using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI;

/// <summary>
/// The electrics: each consumer unit with its supply, its ways and what it is drawn after diversity -
/// in the US the dwelling's load by Article 220 - its circuits with their breakers, cables and what they
/// come to, and each room's light against what it is lit to. A board, a circuit, a fitting or a room
/// picked is selected in the model.
/// </summary>
internal sealed class ElectricalWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _summary = new() { Margin = new Thickness(16, 12, 16, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    private readonly TextBlock _problem = new();

    public ElectricalWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Electrical", 780, 760);
        MinWidth = 480;
        MinHeight = 480;

        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
        _tree.SetResourceReference(BackgroundProperty, "Bg.Window");
        _tree.SetResourceReference(ForegroundProperty, "Text");
        _tree.SelectedItemChanged += (_, _) =>
        {
            if ((_tree.SelectedItem as TreeViewItem)?.Tag is Element element) _main.SelectFromBrowser(element);
        };

        var size = new Button { Content = "Size Circuits", ToolTip = "Choose each circuit's breaker and cable: the breaker its use usually has, the smallest cable that carries it as installed and keeps its voltage drop and earth loop within the rules" };
        size.Click += (_, _) => _problem.Text = _main.SizeCircuits();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();

        var lists = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        lists.Children.Add(new TextBlock { Text = "BOARDS, CIRCUITS AND ROOMS  -  PICK ONE TO SELECT IT", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 4) });
        var border = new Border { Child = _tree, BorderThickness = new Thickness(1) }.Lined();
        Grid.SetRow(border, 1);
        lists.Children.Add(border);

        var root = new DockPanel();
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(DialogLook.Footer(_problem, size, close));
        root.Children.Add(_summary);
        root.Children.Add(lists);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Refresh();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    private static TextBlock Wrapped(string text, double opacity = 1) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity };

    private static readonly Brush Wanting = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A));

    /// <summary>Works the electrics out again, as the model now stands.</summary>
    public void Refresh()
    {
        var document = _main.Document;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        _tree.Items.Clear();
        var problems = MepSystems.Problems(document);
        var wanting = problems.Select(problem => problem.Element).ToHashSet();
        var boards = document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.ConsumerUnit).ToList();
        var lines = new List<string>();
        if (boards.Count == 0) lines.Add("No consumer unit yet: place one from Systems → Fitting, then make circuits of the fittings with it.");

        foreach (var board in boards)
        {
            var name = MepSystems.Describe(document, board) ?? "Consumer unit";
            if (Circuits.Demand(document, board) is not { } demand) continue;
            lines.Add(us
                ? $"{name}: {demand.Supply:0} A service, {demand.Circuits.Count} of {demand.Ways} spaces; the dwelling's load by NEC Article 220 {demand.Demand:0} A at 240 V ({string.Join("; ", demand.Workings)})."
                : $"{name}: {demand.Supply:0} A supply, {demand.Circuits.Count} of {demand.Ways} ways; maximum demand {demand.Demand:0} A after the On-Site Guide's diversity{(demand.Workings.Count > 0 ? $" ({string.Join(", ", demand.Workings)})" : string.Empty)}.");

            var item = new TreeViewItem { Header = Wrapped($"{name}  -  {demand.Circuits.Count} circuit{(demand.Circuits.Count == 1 ? string.Empty : "s")}"), Tag = board, IsExpanded = true };
            if (wanting.Contains(board)) item.Foreground = Wanting;
            foreach (var design in demand.Circuits.OrderBy(design => design.Circuit.Name, StringComparer.OrdinalIgnoreCase))
            {
                var header = new StackPanel();
                header.Children.Add(Wrapped($"{design.Circuit.Name}  -  {design.Use.Title(us)}, {MepSystems.MembersOf(document, design.Circuit).Count} fitting{(MepSystems.MembersOf(document, design.Circuit).Count == 1 ? string.Empty : "s")}"));
                header.Children.Add(Wrapped(Circuits.Describe(document, design.Circuit), 0.75));
                var circuit = new TreeViewItem { Header = header, Tag = design.Circuit };
                if (wanting.Contains(design.Circuit)) circuit.Foreground = Wanting;
                foreach (var problem in problems.Where(problem => problem.Element == design.Circuit))
                    circuit.Items.Add(new TreeViewItem { Header = Wrapped($"Wanting: {problem.Text}"), Foreground = Wanting });
                foreach (var (fitting, distance) in design.Stops)
                    circuit.Items.Add(new TreeViewItem
                    {
                        Header = Wrapped($"{MepSystems.Describe(document, fitting)} - {(us ? $"{distance * 3.28084:0} ft" : $"{distance:0.0} m")} along it"),
                        Tag = fitting
                    });
                item.Items.Add(circuit);
            }

            _tree.Items.Add(item);
        }

        // Circuits on no board.
        var loose = Circuits.Of(document).Where(circuit => MepSystems.SourceOf(document, circuit) is null).ToList();
        if (loose.Count > 0)
        {
            var item = new TreeViewItem { Header = Wrapped($"On no consumer unit  -  {loose.Count} circuit{(loose.Count == 1 ? string.Empty : "s")}"), IsExpanded = true, Foreground = Wanting };
            foreach (var circuit in loose) item.Items.Add(new TreeViewItem { Header = Wrapped($"{circuit.Name}  -  {Circuits.Describe(document, circuit)}"), Tag = circuit });
            _tree.Items.Add(item);
        }

        // Each room's light.
        var rooms = Lighting.Rooms(document).Where(light => light.Lights.Count > 0 || light.Target is not null).OrderBy(light => light.Room.Number, StringComparer.OrdinalIgnoreCase).ToList();
        if (rooms.Count > 0)
        {
            var lighting = new TreeViewItem { Header = Wrapped($"Lighting  -  {(us ? "the IES's recommended light (Lighting Handbook, 10th edition)" : "BRE's light for SAP 10 (S10TP-06)")}, by the lumen method"), IsExpanded = true };
            foreach (var light in rooms)
            {
                var said = Lighting.Describe(document, light) ?? (light.Target is { } target ? $"no lights - {(us ? $"{target.Lux / 10.764:0.#} fc" : $"{target.Lux:0} lux")} on the {target.Where} recommended" : "no lights");
                var item = new TreeViewItem { Header = Wrapped($"{RoomsOf.Label(light.Room)}: {said}"), Tag = light.Room };
                if (problems.Any(problem => problem.Element == light.Room && problem.Text.Contains("lux"))) item.Foreground = Wanting;
                lighting.Items.Add(item);
            }

            _tree.Items.Add(lighting);
        }

        _summary.Text = string.Join("\n", lines);
    }
}
