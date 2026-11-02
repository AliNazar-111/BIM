using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.UI;

/// <summary>
/// The worksets of a shared project: each with what it holds, who has it, whether views show it by
/// default and whether the view open shows it; a new one made, one renamed, made one's own - so
/// everything on it is one's own to change - or made the one new elements go on.
/// </summary>
internal sealed class WorksetsWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly ListBox _list = new() { Margin = new Thickness(16, 0, 16, 8) };
    private readonly TextBox _name = new() { Width = 180, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _said = new();

    public WorksetsWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Worksets", 720, 480);
        MinWidth = 560;
        MinHeight = 320;

        Button Make(string text, string tip, Action click)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (_, _) => click();
            return button;
        }

        var bar = new WrapPanel { Margin = new Thickness(16, 0, 16, 8) };
        bar.Children.Add(_name);
        bar.Children.Add(Make("New", "A new workset of this name", () => Apply(Worksets.Create(_main.Document, _name.Text, out var why), why, $"{_name.Text.Trim()} made.")));
        bar.Children.Add(Make("Rename", "The workset picked called this name, and everything on it", () =>
        {
            if (Picked is not { } workset) { DialogLook.Warn(_said, "Pick a workset to rename."); return; }
            Apply(Worksets.Rename(_main.Document, workset, _name.Text, out var why), why, $"Renamed {_name.Text.Trim()}.");
        }));
        bar.Children.Add(Make("Make Active", "New elements go on the workset picked", () =>
        {
            if (Picked is not { } workset || workset.Kind is WorksetKind.Standards or WorksetKind.Views) { DialogLook.Warn(_said, "Pick a workset elements can go on."); return; }
            Apply(Worksets.SetActive(_main.Document, workset.Name), null, $"New elements go on {workset.Name}.");
        }));
        bar.Children.Add(Make("Make Editable", "The workset picked made yours: everything on it yours to change until you let it go", () =>
        {
            if (Picked is not { } workset) { DialogLook.Warn(_said, "Pick a workset to make yours."); return; }
            if (_main.Workshare is not { } session) { DialogLook.Warn(_said, "Open your local copy of the central model first."); return; }
            DialogLook.Say(_said, session.MakeEditable(workset.Name));
            _main.RefreshWorksharingDisplay();
            Refresh();
        }));
        bar.Children.Add(Make("Ask For It", "Ask whoever has the workset picked to let you have it", () =>
        {
            if (Picked is not { } workset || _main.Workshare is not { } session) return;
            DialogLook.Say(_said, session.Request(Guid.Empty, workset.Name, $"workset {workset.Name}"));
        }));
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        DockPanel.SetDock(bar, Dock.Top);
        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, close));
        root.Children.Add(DialogLook.Intro("Each element is on a workset. Make one yours to have everything on it yours to change; tick or untick it to show or hide it in the view open, and its default for every view."));
        root.Children.Add(bar);
        root.Children.Add(_list);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    private WorksetInfo? Picked => (_list.SelectedItem as ListBoxItem)?.Tag as WorksetInfo;

    internal void Refresh()
    {
        var document = _main.Document;
        var keep = Picked?.Name;
        _list.Items.Clear();
        if (!document.Worksharing.Enabled)
        {
            DialogLook.Say(_said, "This project is not shared: Collaborate, Worksharing makes it a central model, with its worksets.");
            return;
        }

        var registry = _main.Workshare?.Registry();
        var view = _main.Plan.CurrentView;
        foreach (var workset in document.Worksharing.Worksets)
        {
            var count = document.Elements.Count(element => string.Equals(element.Workset, workset.Name, StringComparison.OrdinalIgnoreCase));
            var owner = registry?.WorksetOwners.GetValueOrDefault(workset.Name);
            var borrowers = registry is null ? new List<string>() : document.Elements
                .Where(element => string.Equals(element.Workset, workset.Name, StringComparison.OrdinalIgnoreCase) && registry.Owners.ContainsKey(element.Id))
                .Select(element => registry.Owners[element.Id]).Distinct().ToList();

            var row = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };
            var shown = new CheckBox
            {
                IsChecked = document.ViewSettings.WorksetShownIn(view, workset.Name) ?? workset.VisibleByDefault,
                ToolTip = "Shown in the view open", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
            };
            var name = workset.Name;
            shown.Click += (_, _) =>
            {
                document.ViewSettings.SetWorksetShown(view, name, shown.IsChecked == true);
                _main.Plan.RefreshModel();
            };
            var byDefault = new CheckBox { Content = "Visible by default", IsChecked = workset.VisibleByDefault, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            byDefault.Click += (_, _) =>
            {
                workset.VisibleByDefault = byDefault.IsChecked == true;
                _main.Plan.RefreshModel();
            };
            DockPanel.SetDock(shown, Dock.Left);
            DockPanel.SetDock(byDefault, Dock.Right);
            row.Children.Add(shown);
            row.Children.Add(byDefault);
            var active = string.Equals(document.Worksharing.Active, workset.Name, StringComparison.OrdinalIgnoreCase) ? "  - active" : string.Empty;
            var who = owner is not null ? $"{owner}'s" : borrowers.Count > 0 ? $"some borrowed by {string.Join(", ", borrowers)}" : "nobody's";
            row.Children.Add(new TextBlock { Text = $"{workset.Name}{active}   {count} element{(count == 1 ? string.Empty : "s")}, {who}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _list.Items.Add(new ListBoxItem { Content = row, Tag = workset });
        }

        if (keep is not null) _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(item => (item.Tag as WorksetInfo)?.Name == keep);
        DialogLook.Say(_said, $"{document.Worksharing.Worksets.Count} worksets; new elements go on {document.Worksharing.Active}.");
    }

    private void Apply(Core.Documents.Commands.IUndoableCommand? command, string? why, string said)
    {
        if (command is null)
        {
            DialogLook.Warn(_said, why ?? "That cannot be done.");
            return;
        }

        _main.History.Execute(command);
        _main.RefreshCollaborate();
        Refresh();
        DialogLook.Say(_said, said);
    }
}
