using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// The project's design options: sets of alternatives, each with its primary - what every view
/// shows unless told otherwise - and which one new work goes into. Sets and options are made,
/// renamed, made primary and deleted in copies, put in on OK as one step; a set's primary can be
/// accepted, making it the building and the others gone.
/// </summary>
public sealed class DesignOptionsWindow : System.Windows.Window
{
    private readonly BimDocument _document;
    private readonly List<DesignOptionSet> _sets;
    private readonly TreeView _tree = new() { Width = 300 };
    private readonly StackPanel _details = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly ComboBox _editing = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _problem = new();
    private object? _picked;
    private bool _loading;

    public DesignOptionsWindow(BimDocument document)
    {
        DialogLook.Apply(this, "Design Options", 820, 560);
        _document = document;
        _sets = document.DesignOptionSets.Select(set => set.Copy()).ToList();
        Editing = document.EditingDesignOption;
        _tree.SelectedItemChanged += (_, _) =>
        {
            _picked = (_tree.SelectedItem as TreeViewItem)?.Tag;
            ShowDetails();
        };

        var newSet = new Button { Content = "New Set" };
        newSet.Click += (_, _) => AddSet("Option Set");
        var newOption = new Button { Content = "New Option" };
        newOption.Click += (_, _) =>
        {
            var set = _picked as DesignOptionSet ?? _sets.FirstOrDefault(candidate => _picked is DesignOption option && candidate.Options.Contains(option));
            if (set is null) _problem.Text = "Pick the set to add an option to.";
            else AddOption(set);
        };
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;

        var body = new DockPanel { Margin = new Thickness(16, 0, 16, 12) };
        DockPanel.SetDock(_tree, Dock.Left);
        body.Children.Add(_tree);
        body.Children.Add(new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var top = new StackPanel { Margin = new Thickness(16, 0, 16, 10) };
        top.Children.Add(new TextBlock { Text = "New work goes into", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        top.Children.Add(_editing);
        _editing.SelectionChanged += (_, _) => { if (!_loading) Editing = (_editing.SelectedItem as ComboBoxItem)?.Tag as Guid?; };
        DockPanel.SetDock(top, Dock.Top);

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "A set holds alternatives for one part of the building - two kitchens, three porches. Everything outside the sets is the main model, in every option. "
            + "Each view shows a set's primary unless its Properties pick another. Accept Primary makes the primary part of the building and deletes the other options with what is in them."));
        dock.Children.Add(top);
        dock.Children.Add(DialogLook.Footer(_problem, newSet, newOption, ok, new Button { Content = "Cancel", IsCancel = true }));
        dock.Children.Add(body);
        Content = dock;
        Refresh();
    }

    /// <summary>The sets as edited.</summary>
    public IReadOnlyList<DesignOptionSet> Sets => _sets;

    /// <summary>The option new work goes into; null for the main model.</summary>
    public Guid? Editing { get; private set; }

    /// <summary>A set whose primary is to be accepted once the sets are put in; null for none.</summary>
    public Guid? AcceptSet { get; private set; }

    public DesignOptionSet AddSet(string stem)
    {
        var taken = _sets.Select(set => set.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var set = new DesignOptionSet { Name = Enumerable.Range(1, 999).Select(i => $"{stem} {i}").First(name => !taken.Contains(name)) };
        set.Options.Add(new DesignOption { Name = "Option 1", IsPrimary = true });
        _sets.Add(set);
        _picked = set;
        Refresh();
        return set;
    }

    public DesignOption AddOption(DesignOptionSet set)
    {
        var option = new DesignOption { Name = $"Option {set.Options.Count + 1}", IsPrimary = set.Options.Count == 0 };
        set.Options.Add(option);
        _picked = option;
        Refresh();
        return option;
    }

    /// <summary>An option made its set's primary.</summary>
    public void MakePrimary(DesignOption option)
    {
        foreach (var set in _sets.Where(set => set.Options.Contains(option)))
            foreach (var other in set.Options) other.IsPrimary = ReferenceEquals(other, option);
        Refresh();
    }

    /// <summary>The option new work goes into.</summary>
    public void Edit(Guid? option)
    {
        Editing = option;
        Refresh();
    }

    private int ElementsIn(Guid option) => _document.Elements.Count(element => element.DesignOptionId == option);

    private void Refresh()
    {
        _tree.Items.Clear();
        foreach (var set in _sets)
        {
            var node = new TreeViewItem { Header = set.Name, Tag = set, IsExpanded = true, IsSelected = ReferenceEquals(set, _picked) };
            foreach (var option in set.Options)
                node.Items.Add(new TreeViewItem
                {
                    Header = $"{option.Name}{(option.IsPrimary ? "  (primary)" : string.Empty)}  -  {ElementsIn(option.Id)}",
                    Tag = option, IsSelected = ReferenceEquals(option, _picked)
                });
            _tree.Items.Add(node);
        }

        _loading = true;
        _editing.Items.Clear();
        _editing.Items.Add(new ComboBoxItem { Content = "Main Model", Tag = null });
        foreach (var set in _sets)
            foreach (var option in set.Options)
                _editing.Items.Add(new ComboBoxItem { Content = $"{set.Name}: {option.Name}", Tag = option.Id });
        _editing.SelectedItem = _editing.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, Editing)) ?? _editing.Items[0];
        _loading = false;
        ShowDetails();
    }

    private void ShowDetails()
    {
        _details.Children.Clear();
        _problem.Text = string.Empty;
        switch (_picked)
        {
            case DesignOptionSet set:
            {
                var name = new TextBox { Text = set.Name, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
                name.LostFocus += (_, _) => { if (name.Text.Trim().Length > 0) { set.Name = name.Text.Trim(); Refresh(); } };
                _details.Children.Add(new TextBlock { Text = "Set", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
                _details.Children.Add(name);

                var accept = new Button { Content = "Accept Primary", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                accept.Click += (_, _) =>
                {
                    AcceptSet = set.Id;
                    DialogResult = true;
                };
                var delete = new Button { Content = "Delete Set", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                delete.Click += (_, _) =>
                {
                    _sets.Remove(set);
                    _picked = null;
                    Refresh();
                };
                _details.Children.Add(accept);
                _details.Children.Add(delete);
                _details.Children.Add(new TextBlock { Text = "Deleting a set deletes what is in each of its options.", Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0) });
                break;
            }

            case DesignOption option:
            {
                var name = new TextBox { Text = option.Name, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
                name.LostFocus += (_, _) => { if (name.Text.Trim().Length > 0) { option.Name = name.Text.Trim(); Refresh(); } };
                _details.Children.Add(new TextBlock { Text = "Option", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
                _details.Children.Add(name);
                var primary = new Button { Content = "Make Primary", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !option.IsPrimary };
                primary.Click += (_, _) => MakePrimary(option);
                var edit = new Button { Content = "Work On It", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                edit.Click += (_, _) => Edit(option.Id);
                var delete = new Button { Content = "Delete Option", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                delete.Click += (_, _) =>
                {
                    var set = _sets.First(candidate => candidate.Options.Contains(option));
                    if (set.Options.Count == 1)
                    {
                        _problem.Text = "A set has one option at least: delete the set instead.";
                        return;
                    }

                    set.Options.Remove(option);
                    if (!set.Options.Any(other => other.IsPrimary)) set.Options[0].IsPrimary = true;
                    _picked = set;
                    Refresh();
                };
                foreach (var button in new[] { primary, edit, delete }) _details.Children.Add(button);
                var count = ElementsIn(option.Id);
                _details.Children.Add(new TextBlock { Text = $"{count} {(count == 1 ? "element is" : "elements are")} in it; deleting it deletes them.", Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0) });
                break;
            }

            default:
                _details.Children.Add(new TextBlock { Text = _sets.Count == 0 ? "No design options yet: New Set makes the first, with its first option." : "Pick a set or an option.", Opacity = 0.7 });
                break;
        }
    }
}
