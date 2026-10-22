using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI;

/// <summary>
/// The System Browser: every system by discipline, each with what feeds it and the fittings on
/// it; the fittings not connected yet; and what the rules ask that is wanting. A system or a
/// fitting picked in it is selected in the model; Edit, Source and Delete act on the system picked.
/// </summary>
internal sealed class SystemsWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly ListBox _wanting = new() { BorderThickness = new Thickness(0), MinHeight = 120 };
    private readonly TextBlock _summary = new() { Margin = new Thickness(16, 12, 16, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _problem = new();

    public SystemsWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "System Browser", 640, 720);
        MinWidth = 420;
        MinHeight = 480;

        ScrollViewer.SetHorizontalScrollBarVisibility(_wanting, ScrollBarVisibility.Disabled);
        foreach (var list in new Control[] { _tree, _wanting })
        {
            list.SetResourceReference(BackgroundProperty, "Bg.Window");
            list.SetResourceReference(ForegroundProperty, "Text");
        }

        _tree.SelectedItemChanged += (_, _) => Pick((_tree.SelectedItem as TreeViewItem)?.Tag);
        _wanting.SelectionChanged += (_, _) => Pick((_wanting.SelectedItem as ListBoxItem)?.Tag);

        Button Make(string text, Action act, string tip)
        {
            var button = new Button { Content = text, ToolTip = tip };
            button.Click += (_, _) => act();
            return button;
        }

        var footer = DialogLook.Footer(_problem,
            Make("Edit", () => Act(system => _main.EditSystemInPlan(system)), "Click fittings on and off the system picked"),
            Make("Source", () => Act(system => _main.PickSourceInPlan(system)), "Click the equipment that feeds the system picked"),
            Make("Delete", () => Act(Delete), "Take the system picked away - its fittings stay where they are"),
            Make("Close", Close, "Close the browser"));

        var lists = new Grid { Margin = new Thickness(16, 0, 16, 8) };
        lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        void Row(UIElement element, int row)
        {
            Grid.SetRow(element, row);
            lists.Children.Add(element);
        }

        Row(Label("SYSTEMS  -  PICK ONE TO SELECT IT"), 0);
        Row(new Border { Child = _tree, BorderThickness = new Thickness(1) }.Lined(), 1);
        Row(Label("WANTING"), 2);
        Row(new Border { Child = _wanting, BorderThickness = new Thickness(1) }.Lined(), 3);

        var root = new DockPanel();
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(footer);
        root.Children.Add(_summary);
        root.Children.Add(lists);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Refresh();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>The system picked in the tree - or the one of a fitting picked under it.</summary>
    public MepSystem? Picked =>
        (_tree.SelectedItem as TreeViewItem) is { } item
            ? item.Tag as MepSystem ?? (item.Parent as TreeViewItem)?.Tag as MepSystem
            : null;

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 10, 0, 4) };

    /// <summary>Lists the systems, the fittings not connected and what is wanting again, as the model now stands.</summary>
    public void Refresh()
    {
        var document = _main.Document;
        var keep = Picked?.Id;
        var systems = document.All<MepSystem>().ToList();
        var problems = MepSystems.Problems(document);

        _tree.Items.Clear();
        foreach (var discipline in new[] { Discipline.Mechanical, Discipline.Electrical, Discipline.Plumbing })
        {
            var of = systems.Where(system => system.Kind.Discipline() == discipline).OrderBy(system => system.Kind).ThenBy(system => system.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var group = new TreeViewItem { Header = $"{discipline.Title()}  ({of.Count})", IsExpanded = true };
            foreach (var system in of)
            {
                var members = MepSystems.MembersOf(document, system);
                var source = MepSystems.Describe(document, MepSystems.SourceOf(document, system));
                var item = new TreeViewItem
                {
                    Header = Swatch(system.Kind, $"{system.Name}  -  {system.Kind.Title()}, {members.Count} fitting{(members.Count == 1 ? string.Empty : "s")}{(source is null ? string.Empty : $", from {source}")}"),
                    Tag = system,
                    IsExpanded = system.Id == keep
                };
                foreach (var member in members)
                    item.Items.Add(new TreeViewItem { Header = MepSystems.Describe(document, member), Tag = member });
                if (problems.Any(problem => problem.Element == system)) item.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A));
                group.Items.Add(item);
                if (system.Id == keep) item.IsSelected = true;
            }

            _tree.Items.Add(group);
        }

        // Fittings with a connection on no system - what a services plan draws red.
        var open = document.All<Component>().Select(component => (Component: component, Kinds: MepSystems.OpenKinds(document, component)))
            .Where(entry => entry.Kinds.Count > 0).ToList();
        var unconnected = new TreeViewItem { Header = $"Not connected  ({open.Count})", IsExpanded = open.Count is > 0 and <= 12 };
        foreach (var (component, kinds) in open)
            unconnected.Items.Add(new TreeViewItem { Header = $"{MepSystems.Describe(document, component)}: {string.Join(", ", kinds.Select(kind => kind.Title()))}", Tag = component });
        _tree.Items.Add(unconnected);

        _wanting.Items.Clear();
        foreach (var problem in problems)
        {
            var who = problem.Element switch
            {
                MepSystem system => system.Name,
                Room room => room.ToString(),
                Pipe pipe => $"{document.FindType<PipeType>(pipe.TypeId)?.Name ?? "Pipe"} {pipe.SizeName(document.FindType<PipeType>(pipe.TypeId))}",
                _ => MepSystems.Describe(document, problem.Element as Component) ?? "Fitting"
            };
            _wanting.Items.Add(new ListBoxItem { Content = new TextBlock { Text = $"{who}: {problem.Text}", TextWrapping = TextWrapping.Wrap }, Tag = problem.Element });
        }

        var pipes = document.All<Pipe>().ToList();
        _summary.Text = systems.Count == 0 && open.Count == 0
            ? "No fittings with services yet. Place them from Systems → Fitting, select some and Make System."
            : $"{systems.Count} system{(systems.Count == 1 ? string.Empty : "s")}; {open.Count} fitting{(open.Count == 1 ? string.Empty : "s")} not connected; {problems.Count} thing{(problems.Count == 1 ? string.Empty : "s")} wanting." +
              (pipes.Count > 0 ? $" {pipes.Sum(pipe => pipe.Length) / 1000:0.0} m of pipe." : string.Empty) +
              (document.ProjectInformation.ClearanceStandard.IsUs() ? string.Empty : $"\nWater use: {WaterUse.Assess(document).Said}.");
    }

    /// <summary>A line of the tree with a dot in its system's colour before it.</summary>
    private static StackPanel Swatch(SystemKind kind, string text)
    {
        var colour = kind.Colour();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B)) });
        line.Children.Add(new TextBlock { Text = text });
        return line;
    }

    private void Pick(object? picked)
    {
        _problem.Text = string.Empty;
        if (picked is Element element) _main.SelectFromBrowser(element);
    }

    private void Act(Action<MepSystem> act)
    {
        if (Picked is not { } system)
        {
            _problem.Text = "Pick a system first.";
            return;
        }

        _problem.Text = string.Empty;
        act(system);
    }

    private void Delete(MepSystem system)
    {
        _main.History.Execute(new DeleteElementCommand(_main.Document, system));
        _main.SelectFromBrowser(null);
    }
}

internal static class BorderLining
{
    /// <summary>The border drawn in the theme's line colour.</summary>
    public static Border Lined(this Border border)
    {
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        return border;
    }
}
