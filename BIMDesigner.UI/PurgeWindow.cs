using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.UI;

/// <summary>
/// Purge Unused: the types and materials nothing in the project uses, by kind, each ticked to go -
/// untick what is to stay - and Purge takes them out as one step to undo. What only something
/// purged used goes with it; what something kept uses stays. The last type of each kind stays for
/// its tool, as do the types the tools are set to draw with. The library's doors, windows and
/// families purged are not brought back as the project opens - Bring Back Library Types does.
/// </summary>
internal sealed class PurgeWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly StackPanel _items = new() { Margin = new Thickness(4) };
    private readonly TextBlock _said = new();
    private readonly List<(CheckBox Box, object Item)> _checks = new();
    private readonly Button _purge = new() { Content = "Purge", IsDefault = true };

    public PurgeWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Purge Unused", 560, 620);
        MinWidth = 420;
        MinHeight = 320;

        var all = new Button { Content = "Check All" };
        all.Click += (_, _) => Tick(true);
        var none = new Button { Content = "Check None" };
        none.Click += (_, _) => Tick(false);
        var library = new Button
        {
            Content = "Bring Back Library Types",
            ToolTip = "Put back the library's doors, windows, families, roof edges and the rest purged from this project - and any it has never had - as one step to undo"
        };
        library.Click += (_, _) => BringBackLibrary();
        _purge.Click += (_, _) => PurgeChecked();
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new WrapPanel { Margin = new Thickness(16, 0, 16, 8) };
        foreach (var button in new[] { all, none, library })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Padding = new Thickness(10, 4, 10, 4);
            buttons.Children.Add(button);
        }

        DockPanel.SetDock(buttons, Dock.Top);
        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, _purge, close));
        root.Children.Add(DialogLook.Intro(
            "The types and materials nothing in the project uses: no element is built from them, and no type or setting kept refers to them; the view filters no view uses, and the schedules on no sheet - those left unticked. "
            + "Untick what should stay, then Purge - one step to undo. What only these use goes with them. "
            + "The last type of each kind stays for its tool, and so do the types the tools are set to draw with."));
        root.Children.Add(buttons);
        var list = new Border { Child = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, BorderThickness = new Thickness(1), Margin = new Thickness(16, 0, 16, 8) }.Lined();
        root.Children.Add(list);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    /// <summary>What is unused now, listed by kind, all ticked.</summary>
    public void Refresh()
    {
        var unused = Purging.Find(_main.Document, _main.ToolTypes());
        _items.Children.Clear();
        _checks.Clear();

        void Heading(string text, int count)
        {
            var heading = new TextBlock { Text = $"{text} ({count})", FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, _items.Children.Count == 0 ? 4 : 12, 6, 4) };
            _items.Children.Add(heading);
        }

        void Item(string name, object item, bool ticked = true)
        {
            var box = new CheckBox { Content = name, IsChecked = ticked, Margin = new Thickness(18, 2, 6, 2) };
            box.SetResourceReference(ForegroundProperty, "Text");
            box.Checked += (_, _) => Count();
            box.Unchecked += (_, _) => Count();
            _checks.Add((box, item));
            _items.Children.Add(box);
        }

        foreach (var kind in unused.Types.GroupBy(Purging.Heading).OrderBy(kind => kind.Key, StringComparer.CurrentCulture))
        {
            Heading(kind.Key, kind.Count());
            foreach (var type in kind.OrderBy(type => type.Name, StringComparer.CurrentCulture)) Item(type.Name, type);
        }

        if (unused.Materials.Count > 0)
        {
            Heading("Materials", unused.Materials.Count);
            foreach (var material in unused.Materials.OrderBy(material => material.Name, StringComparer.CurrentCulture)) Item(material.Name, material);
        }

        // The views' own: filters no view uses, ticked; schedules on no sheet, left for you to tick - every project starts with some.
        var views = Purging.FindViews(_main.Document);
        if (views.Filters.Count > 0)
        {
            Heading("View Filters no view uses", views.Filters.Count);
            foreach (var filter in views.Filters.OrderBy(filter => filter.Name, StringComparer.CurrentCulture)) Item(filter.Name, filter);
        }

        if (views.Schedules.Count > 0)
        {
            Heading("Schedules on no sheet - tick those to go", views.Schedules.Count);
            foreach (var schedule in views.Schedules.OrderBy(schedule => schedule.Name, StringComparer.CurrentCulture)) Item(schedule.Name, schedule, false);
        }

        if (unused.Count + views.Count == 0)
            _items.Children.Add(new TextBlock { Text = "Nothing is unused: every type and material in the project is used.", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });

        Count();
    }

    private void Tick(bool on)
    {
        foreach (var (box, _) in _checks) box.IsChecked = on;
        Count();
    }

    private void Count()
    {
        var (types, materials) = Ticked();
        var views = TickedViews();
        _purge.IsEnabled = types.Count + materials.Count + views.Count > 0;
        DialogLook.Say(_said, _checks.Count == 0
            ? "Nothing to purge."
            : $"{types.Count} {(types.Count == 1 ? "type" : "types")}, {materials.Count} {(materials.Count == 1 ? "material" : "materials")} and {views.Count} {(views.Count == 1 ? "schedule or filter" : "schedules and filters")} ticked, of {_checks.Count} unused.");
    }

    private UnusedViews TickedViews() =>
        new(_checks.Where(check => check.Box.IsChecked == true).Select(check => check.Item).OfType<Core.Schedules.ScheduleDefinition>().ToList(),
            _checks.Where(check => check.Box.IsChecked == true).Select(check => check.Item).OfType<Core.Views.ParameterFilter>().ToList());

    /// <summary>A schedule or filter ticked or not by name, for the harness and tests.</summary>
    public void TickView(string name, bool on)
    {
        foreach (var (box, item) in _checks)
            if (item is Core.Schedules.ScheduleDefinition schedule && schedule.Name == name || item is Core.Views.ParameterFilter filter && filter.Name == name) box.IsChecked = on;
        Count();
    }

    private (IReadOnlyList<ElementType> Types, IReadOnlyList<Material> Materials) Ticked() =>
        (_checks.Where(check => check.Box.IsChecked == true).Select(check => check.Item).OfType<ElementType>().ToList(),
         _checks.Where(check => check.Box.IsChecked == true).Select(check => check.Item).OfType<Material>().ToList());

    /// <summary>
    /// What is ticked, purged - all of it nothing kept still needs - as one step. How many went.
    /// Public so the harness and tests can press it.
    /// </summary>
    public int PurgeChecked()
    {
        var (types, materials) = Ticked();
        var views = TickedViews();
        var command = Purging.Purge(_main.Document, types, materials, _main.ToolTypes());
        var gone = command.Types.Count + command.Materials.Count + views.Count;
        if (gone == 0)
        {
            DialogLook.Warn(_said, "Nothing ticked can go: what is left uses it.");
            return 0;
        }

        _main.History.Execute(views.Count == 0 ? command : new Core.Documents.Commands.CompositeCommand("Purge Unused", new Core.Documents.Commands.IUndoableCommand[] { command, Purging.PurgeViews(_main.Document, views.Schedules, views.Filters) }));
        _main.AfterPurge();
        Refresh();
        var stayed = types.Count + materials.Count + views.Count - gone;
        var said = $"Purged {command.Types.Count} {(command.Types.Count == 1 ? "type" : "types")}, {command.Materials.Count} {(command.Materials.Count == 1 ? "material" : "materials")}"
                   + (views.Count > 0 ? $", {views.Schedules.Count} schedule{(views.Schedules.Count == 1 ? string.Empty : "s")} and {views.Filters.Count} filter{(views.Filters.Count == 1 ? string.Empty : "s")}" : string.Empty)
                   + (stayed > 0 ? $"; {stayed} kept, used by what stays" : string.Empty) + ". Undo puts them back.";
        DialogLook.Say(_said, said);
        _main.StatusSay(said);
        return gone;
    }

    /// <summary>The library's types and materials purged, brought back.</summary>
    private void BringBackLibrary()
    {
        var before = _main.Document.ElementTypes.Count + _main.Document.Materials.Count;
        _main.History.Execute(Purging.RestoreLibrary(_main.Document));
        _main.AfterPurge();
        Refresh();
        var back = _main.Document.ElementTypes.Count + _main.Document.Materials.Count - before;
        DialogLook.Say(_said, back == 0 ? "The project has every library type already." : $"{back} library {(back == 1 ? "type or material" : "types and materials")} brought back. Undo takes them out again.");
    }
}
