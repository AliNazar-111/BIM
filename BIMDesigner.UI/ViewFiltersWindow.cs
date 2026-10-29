using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// The project's view filters - each a name, the categories it looks at and the rules their
/// parameters are tested by - and, for the view shown, which of them it has and what each does:
/// hides what it picks out, or colours or fades it. Changes are made to copies; OK puts them in
/// as one step to undo.
/// </summary>
public sealed class ViewFiltersWindow : System.Windows.Window
{
    private readonly BimDocument _document;
    private readonly ViewReference _view;
    private readonly bool _locked;
    private readonly List<ParameterFilter> _filters;
    private readonly List<FilterOverride> _uses;
    private readonly ListBox _list = new() { Width = 220 };
    private readonly StackPanel _details = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _problem = new();

    /// <summary>The categories a filter can look at: what is built and what is put in it.</summary>
    public static readonly BuiltInCategory[] Categories =
    {
        BuiltInCategory.Walls, BuiltInCategory.Doors, BuiltInCategory.Windows, BuiltInCategory.Floors, BuiltInCategory.Ceilings, BuiltInCategory.Roofs,
        BuiltInCategory.Columns, BuiltInCategory.StructuralFraming, BuiltInCategory.Stairs, BuiltInCategory.Ramps, BuiltInCategory.Railings, BuiltInCategory.Rooms,
        BuiltInCategory.Furniture, BuiltInCategory.Casework, BuiltInCategory.PlumbingFixtures, BuiltInCategory.SpecialtyEquipment, BuiltInCategory.LightingFixtures,
        BuiltInCategory.LightingDevices, BuiltInCategory.ElectricalFixtures, BuiltInCategory.ElectricalEquipment, BuiltInCategory.MechanicalEquipment,
        BuiltInCategory.FireAlarmDevices, BuiltInCategory.Sprinklers, BuiltInCategory.Chimneys, BuiltInCategory.RoofWindows, BuiltInCategory.Planting, BuiltInCategory.GenericModels
    };

    /// <summary>The colours a filter can give what it picks out.</summary>
    private static readonly (string Name, ColourRgb Colour)[] Colours =
    {
        ("Red", new ColourRgb(0xD0, 0x30, 0x30)), ("Orange", new ColourRgb(0xE0, 0x80, 0x10)), ("Green", new ColourRgb(0x20, 0x9A, 0x40)),
        ("Blue", new ColourRgb(0x20, 0x60, 0xD0)), ("Purple", new ColourRgb(0x80, 0x40, 0xC0)), ("Grey", new ColourRgb(0x80, 0x80, 0x80))
    };

    private static readonly (string Text, ScheduleOperator Operator)[] Operators =
    {
        ("equals", ScheduleOperator.Equals), ("does not equal", ScheduleOperator.NotEquals), ("contains", ScheduleOperator.Contains),
        ("is greater than", ScheduleOperator.GreaterThan), ("is less than", ScheduleOperator.LessThan),
        ("has no value", ScheduleOperator.IsEmpty), ("has a value", ScheduleOperator.IsNotEmpty)
    };

    public ViewFiltersWindow(BimDocument document, ViewReference view)
    {
        DialogLook.Apply(this, $"View Filters - {view.TitleIn(document)}", 960, 620);
        (_document, _view) = (document, view);
        _locked = ViewTemplates.Locks(document, view, ViewTemplateSetting.Filters);
        _filters = document.ParameterFilters.Select(filter => filter.Copy()).ToList();
        _uses = document.ViewSettings.FiltersOf(view).ToList();
        _list.SelectionChanged += (_, _) => ShowDetails();

        var add = new Button { Content = "New Filter" };
        add.Click += (_, _) => Add();
        var remove = new Button { Content = "Delete" };
        remove.Click += (_, _) =>
        {
            if (Picked is not { } filter) return;
            _filters.Remove(filter);
            _uses.RemoveAll(use => use.FilterId == filter.Id);
            Refresh(null);
        };
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (_filters.GroupBy(filter => filter.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } twice)
                _problem.Text = $"Two filters are called {twice.Key}.";
            else DialogResult = true;
        };

        var body = new DockPanel { Margin = new Thickness(16, 0, 16, 12) };
        DockPanel.SetDock(_list, Dock.Left);
        body.Children.Add(_list);
        body.Children.Add(new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "A filter picks things out by their parameters: the categories it looks at, and rules on their parameters - \"Fire Rating contains 60\", \"Width is greater than 900\" (lengths in millimetres). "
            + "Added to a view, it hides what it picks out, or colours or fades it there - a later filter's colour over an earlier's. "
            + (_locked ? "This view's filters are set by its template: change them in View Templates." : string.Empty)));
        dock.Children.Add(DialogLook.Footer(_problem, add, remove, ok, new Button { Content = "Cancel", IsCancel = true }));
        dock.Children.Add(body);
        Content = dock;
        Refresh(_filters.FirstOrDefault());
    }

    /// <summary>The project's filters as edited.</summary>
    public IReadOnlyList<ParameterFilter> Filters => _filters;

    /// <summary>The view's filters as edited, in order.</summary>
    public IReadOnlyList<FilterOverride> Uses => _uses;

    public bool ViewLocked => _locked;

    private ParameterFilter? Picked => (_list.SelectedItem as ListBoxItem)?.Tag as ParameterFilter;

    /// <summary>A new filter, added to the view as well, coloured red - the usual reason to make one.</summary>
    public ParameterFilter Add()
    {
        var taken = _filters.Select(filter => filter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filter = new ParameterFilter { Name = Enumerable.Range(1, 999).Select(i => $"Filter {i}").First(name => !taken.Contains(name)) };
        _filters.Add(filter);
        if (!_locked) _uses.Add(new FilterOverride(filter.Id, Colour: Colours[0].Colour));
        Refresh(filter);
        return filter;
    }

    private void Refresh(ParameterFilter? select)
    {
        _list.Items.Clear();
        foreach (var filter in _filters)
        {
            var item = new ListBoxItem { Content = filter.Name + (_uses.Any(use => use.FilterId == filter.Id) ? "   ●" : string.Empty), Tag = filter };
            _list.Items.Add(item);
            if (ReferenceEquals(filter, select)) item.IsSelected = true;
        }

        if (_list.SelectedItem is null && _list.Items.Count > 0) _list.SelectedIndex = 0;
        ShowDetails();
    }

    private static TextBlock Label(string text, double top = 10) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };

    private void ShowDetails()
    {
        _details.Children.Clear();
        if (Picked is not { } filter)
        {
            _details.Children.Add(new TextBlock { Text = "No filters yet: New Filter makes one.", Opacity = 0.7 });
            return;
        }

        var name = new TextBox { Text = filter.Name, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        name.LostFocus += (_, _) =>
        {
            if (name.Text.Trim().Length == 0) return;
            filter.Name = name.Text.Trim();
            if (_list.SelectedItem is ListBoxItem item) item.Content = filter.Name + (_uses.Any(use => use.FilterId == filter.Id) ? "   ●" : string.Empty);
        };
        _details.Children.Add(Label("Name", 0));
        _details.Children.Add(name);

        _details.Children.Add(Label("Looks at - none ticked, every category"));
        var categories = new WrapPanel();
        foreach (var category in Categories)
        {
            var box = new CheckBox { Content = EnumText.Humanise(category), IsChecked = filter.Categories.Contains(category), Margin = new Thickness(0, 0, 12, 4), Width = 160 };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) filter.Categories.Add(category);
                else filter.Categories.Remove(category);
                ShowDetails();
            };
            categories.Children.Add(box);
        }

        _details.Children.Add(categories);

        _details.Children.Add(Label("Rules"));
        var fields = (filter.Categories.Count == 0 ? Categories : filter.Categories.ToArray())
            .SelectMany(category => Schedule.AvailableFields(_document, category)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(field => field).ToList();
        foreach (var rule in filter.Rules.ToList())
        {
            var field = new ComboBox { ItemsSource = fields, IsEditable = true, Text = rule.Field, Width = 200 };
            field.LostFocus += (_, _) => rule.Field = field.Text.Trim();
            field.SelectionChanged += (_, _) => { if (field.SelectedItem is string chosen) rule.Field = chosen; };
            var comparison = new ComboBox { ItemsSource = Operators.Select(o => o.Text).ToList(), SelectedIndex = Array.FindIndex(Operators, o => o.Operator == rule.Comparison), Width = 130, Margin = new Thickness(8, 0, 8, 0) };
            comparison.SelectionChanged += (_, _) => { if (comparison.SelectedIndex >= 0) rule.Comparison = Operators[comparison.SelectedIndex].Operator; };
            var value = new TextBox { Text = rule.Value, Width = 160 };
            value.TextChanged += (_, _) => rule.Value = value.Text;
            var drop = new Button { Content = "✕", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
            drop.Click += (_, _) =>
            {
                filter.Rules.Remove(rule);
                ShowDetails();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var part in new FrameworkElement[] { field, comparison, value, drop }) row.Children.Add(part);
            _details.Children.Add(row);
        }

        var addRule = new Button { Content = "Add Rule", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
        addRule.Click += (_, _) =>
        {
            filter.Rules.Add(new ScheduleFilter(fields.FirstOrDefault() ?? "Mark", ScheduleOperator.Equals));
            ShowDetails();
        };
        var any = new CheckBox { Content = "Any rule passing is enough, not every one", IsChecked = filter.MatchAny, Margin = new Thickness(0, 6, 0, 0) };
        any.Click += (_, _) => filter.MatchAny = any.IsChecked == true;
        _details.Children.Add(addRule);
        _details.Children.Add(any);

        // In this view.
        _details.Children.Add(Label("In this view", 16));
        var index = _uses.FindIndex(use => use.FilterId == filter.Id);
        var added = new CheckBox { Content = "Added to it", IsChecked = index >= 0, IsEnabled = !_locked };
        var shown = new CheckBox { Content = "What it picks out is shown", IsChecked = index < 0 || _uses[index].Visible, IsEnabled = !_locked && index >= 0, Margin = new Thickness(0, 4, 0, 0) };
        var halftone = new CheckBox { Content = "Faded", IsChecked = index >= 0 && _uses[index].Halftone, IsEnabled = !_locked && index >= 0, Margin = new Thickness(0, 4, 0, 0) };
        var colour = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !_locked && index >= 0, Margin = new Thickness(0, 6, 0, 0) };
        colour.Items.Add(new ComboBoxItem { Content = "No colour of its own", Tag = null });
        foreach (var (label, rgb) in Colours)
        {
            var swatch = new StackPanel { Orientation = Orientation.Horizontal };
            swatch.Children.Add(new Border { Width = 14, Height = 14, Background = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)), Margin = new Thickness(0, 0, 6, 0) });
            swatch.Children.Add(new TextBlock { Text = label });
            colour.Items.Add(new ComboBoxItem { Content = swatch, Tag = rgb });
        }

        colour.SelectedIndex = index >= 0 && _uses[index].Colour is { } current ? Math.Max(0, Array.FindIndex(Colours, entry => entry.Colour == current) + 1) : 0;

        void Save()
        {
            var at = _uses.FindIndex(use => use.FilterId == filter.Id);
            if (added.IsChecked != true)
            {
                if (at >= 0) _uses.RemoveAt(at);
                return;
            }

            var use = new FilterOverride(filter.Id, shown.IsChecked == true, (colour.SelectedItem as ComboBoxItem)?.Tag as ColourRgb?, halftone.IsChecked == true);
            if (at >= 0) _uses[at] = use;
            else _uses.Add(use);
        }

        added.Click += (_, _) =>
        {
            Save();
            ShowDetails();
            if (_list.SelectedItem is ListBoxItem item) item.Content = filter.Name + (added.IsChecked == true ? "   ●" : string.Empty);
        };
        shown.Click += (_, _) => Save();
        halftone.Click += (_, _) => Save();
        colour.SelectionChanged += (_, _) => Save();
        foreach (var part in new FrameworkElement[] { added, shown, halftone, colour }) _details.Children.Add(part);

        var picks = _document.Elements.Count(element => filter.Picks(_document, element));
        _details.Children.Add(new TextBlock { Text = $"It picks out {picks} {(picks == 1 ? "element" : "elements")} in the project now.", Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0) });
    }
}
