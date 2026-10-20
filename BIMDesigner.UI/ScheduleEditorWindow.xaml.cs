using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;

namespace BIMDesigner.UI;

/// <summary>
/// A schedule's properties: the fields it shows and in what order, the values it works out,
/// the rows it filters out, how it sorts, groups and totals, and the cells it colours. Edits are
/// made to a copy, and only OK puts them into the project - as one step to undo.
/// </summary>
public partial class ScheduleEditorWindow : System.Windows.Window
{
    private readonly BimDocument _document;
    private readonly ScheduleDefinition _working;
    private bool _loading;

    /// <summary>The colours a rule can give a cell: soft enough to read black text on.</summary>
    public static readonly IReadOnlyList<(string Name, ColourRgb Colour)> Swatches = new[]
    {
        ("Red", new ColourRgb(0xF8, 0xC8, 0xC8)),
        ("Amber", new ColourRgb(0xFC, 0xE3, 0xB0)),
        ("Green", new ColourRgb(0xCD, 0xEB, 0xC8)),
        ("Blue", new ColourRgb(0xC9, 0xDD, 0xF5)),
        ("Purple", new ColourRgb(0xE2, 0xD4, 0xF0)),
        ("Grey", new ColourRgb(0xDA, 0xDD, 0xE1))
    };

    private static readonly (string Text, ScheduleOperator Operator)[] Operators =
    {
        ("equals", ScheduleOperator.Equals), ("does not equal", ScheduleOperator.NotEquals), ("contains", ScheduleOperator.Contains),
        ("is greater than", ScheduleOperator.GreaterThan), ("is less than", ScheduleOperator.LessThan),
        ("has no value", ScheduleOperator.IsEmpty), ("has a value", ScheduleOperator.IsNotEmpty)
    };

    private static readonly (string Text, FormatTest Test)[] Tests =
    {
        ("equal to", FormatTest.EqualTo), ("not equal to", FormatTest.NotEqualTo), ("greater than", FormatTest.GreaterThan),
        ("greater than or equal to", FormatTest.GreaterThanOrEqualTo), ("less than", FormatTest.LessThan),
        ("less than or equal to", FormatTest.LessThanOrEqualTo), ("between", FormatTest.Between), ("not between", FormatTest.NotBetween),
        ("contains", FormatTest.Contains), ("does not contain", FormatTest.DoesNotContain), ("has a value", FormatTest.HasValue),
        ("has no value", FormatTest.HasNoValue)
    };

    public ScheduleEditorWindow(BimDocument document, ScheduleDefinition working)
    {
        InitializeComponent();
        ScreenFit.Apply(this);
        (_document, _working) = (document, working);

        _loading = true;
        NameBox.Text = working.Name;
        CategoryPicker.ItemsSource = EnumText.Choices<BuiltInCategory>();
        CategoryPicker.SelectedItem = EnumText.Humanise(working.Category);
        FormatsOnSheetsBox.IsChecked = working.ShowFormatsOnSheets;
        _loading = false;
        RefreshAll();
    }

    /// <summary>The schedule as edited: what OK hands back.</summary>
    public ScheduleDefinition Schedule => _working;

    /// <summary>
    /// The fields a schedule of a category can show: what its elements in the project report,
    /// and the fields its schedules have, so one can be set up before anything is drawn.
    /// </summary>
    public static IReadOnlyList<string> FieldsFor(BimDocument document, BuiltInCategory category) =>
        Core.Schedules.Schedule.AvailableFields(document, category)
            .Concat(ScheduleDefinition.Defaults().Where(schedule => schedule.Category == category).SelectMany(schedule => schedule.Fields))
            .Concat(new[] { "Mark", "Comments" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IReadOnlyList<string> Available => FieldsFor(_document, _working.Category);

    /// <summary>What a formula may use: the fields there are, and the values worked out before it.</summary>
    private IReadOnlyList<string> FormulaFields(CalculatedField calculated) =>
        Available.Concat(_working.Calculated.TakeWhile(other => !ReferenceEquals(other, calculated)).Select(other => other.Name)).ToList();

    private void RefreshAll()
    {
        RefreshFields();
        RefreshCalculated();
        RefreshFilters();
        RefreshSorting();
        RefreshFormats();
    }

    // ---- name and category -------------------------------------------------------

    private void OnNameChanged(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length > 0) _working.Name = name;
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategoryPicker.SelectedItem is not string text || !EnumText.TryParse<BuiltInCategory>(text, out var category)) return;
        if (category == _working.Category) return;
        _working.Category = category;

        // Only what the new category has, and what is worked out, stays.
        var available = Available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _working.Fields.RemoveAll(field => !available.Contains(field) && _working.CalculatedNamed(field) is null);
        RefreshAll();
    }

    // ---- fields ------------------------------------------------------------------

    private void RefreshFields(string? select = null)
    {
        var scheduled = _working.Fields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        AvailableList.ItemsSource = Available.Concat(_working.Calculated.Select(calculated => calculated.Name))
            .Where(field => !scheduled.Contains(field)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ScheduledList.ItemsSource = _working.Fields.Select(field => _working.CalculatedNamed(field) is null ? field : $"{field}   (calculated)").ToList();
        if (select is not null) ScheduledList.SelectedIndex = _working.Fields.FindIndex(field => field.Equals(select, StringComparison.OrdinalIgnoreCase));
    }

    private string? PickedScheduled => ScheduledList.SelectedIndex is var index and >= 0 && index < _working.Fields.Count ? _working.Fields[index] : null;

    private void OnAddField(object sender, RoutedEventArgs e)
    {
        var picked = AvailableList.SelectedItems.OfType<string>().ToList();
        if (picked.Count == 0) return;
        _working.Fields.AddRange(picked);
        RefreshAll();
        RefreshFields(picked[^1]);
    }

    private void OnRemoveField(object sender, RoutedEventArgs e)
    {
        if (PickedScheduled is not { } field) return;
        _working.Fields.Remove(field);
        RefreshAll();
    }

    private void OnFieldUp(object sender, RoutedEventArgs e) => MoveField(-1);

    private void OnFieldDown(object sender, RoutedEventArgs e) => MoveField(1);

    private void MoveField(int by)
    {
        if (PickedScheduled is not { } field) return;
        var index = _working.Fields.IndexOf(field);
        var to = Math.Clamp(index + by, 0, _working.Fields.Count - 1);
        if (to == index) return;
        _working.Fields.RemoveAt(index);
        _working.Fields.Insert(to, field);
        RefreshFields(field);
    }

    // ---- calculated fields -------------------------------------------------------

    private CalculatedField? PickedCalculated => CalculatedList.SelectedIndex is var index and >= 0 && index < _working.Calculated.Count ? _working.Calculated[index] : null;

    private void RefreshCalculated(CalculatedField? select = null)
    {
        var keep = select ?? PickedCalculated;
        _loading = true;
        CalculatedList.ItemsSource = _working.Calculated.Select(calculated => calculated.Name).ToList();
        CalculatedList.SelectedIndex = keep is null ? (_working.Calculated.Count > 0 ? 0 : -1) : _working.Calculated.IndexOf(keep);
        _loading = false;
        ShowCalculated();
    }

    private void OnCalculatedPicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) ShowCalculated();
    }

    /// <summary>A new calculated field, shown in the schedule at its end.</summary>
    private void OnAddCalculated(object sender, RoutedEventArgs e)
    {
        var taken = Available.Concat(_working.Calculated.Select(c => c.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Enumerable.Range(1, 999).Select(i => i == 1 ? "Calculated" : $"Calculated {i}").First(n => !taken.Contains(n));
        var calculated = new CalculatedField { Name = name, Formula = string.Empty };
        _working.Calculated.Add(calculated);
        _working.Fields.Add(name);
        RefreshAll();
        RefreshCalculated(calculated);
    }

    private void OnRemoveCalculated(object sender, RoutedEventArgs e)
    {
        if (PickedCalculated is not { } calculated) return;
        _working.Calculated.Remove(calculated);
        _working.Fields.RemoveAll(field => field.Equals(calculated.Name, StringComparison.OrdinalIgnoreCase));
        RefreshAll();
    }

    /// <summary>A calculated field renamed: every place the schedule names it follows.</summary>
    private void Rename(CalculatedField calculated, string name)
    {
        var old = calculated.Name;
        if (name.Length == 0 || name.Equals(old, StringComparison.Ordinal)) return;
        calculated.Name = name;
        static string Swap(string field, string from, string to) => field.Equals(from, StringComparison.OrdinalIgnoreCase) ? to : field;
        for (var i = 0; i < _working.Fields.Count; i++) _working.Fields[i] = Swap(_working.Fields[i], old, name);
        foreach (var filter in _working.Filters) filter.Field = Swap(filter.Field, old, name);
        foreach (var format in _working.Formats) format.Field = Swap(format.Field, old, name);
        foreach (var other in _working.Calculated) other.PercentageOf = Swap(other.PercentageOf, old, name);
        if (_working.SortBy is { } sort) _working.SortBy = Swap(sort, old, name);
        if (_working.GroupBy is { } group) _working.GroupBy = Swap(group, old, name);
        RefreshFields();
        RefreshFilters();
        RefreshSorting();
        RefreshFormats();
        RefreshCalculated(calculated);
    }

    private void ShowCalculated()
    {
        CalculatedDetails.Children.Clear();
        if (PickedCalculated is not { } calculated)
        {
            CalculatedDetails.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
                Text = "A calculated field works a value out for each row: a formula over its other fields - Width * Height, if(Width < 800, 1, 0) - or a field's share of its total, as a percentage. Add one to start."
            });
            return;
        }

        var name = new TextBox { Text = calculated.Name, Margin = new Thickness(0, 0, 0, 10) };
        name.LostFocus += (_, _) => Rename(calculated, name.Text.Trim());
        CalculatedDetails.Children.Add(Label("Name"));
        CalculatedDetails.Children.Add(name);

        var kind = new ComboBox { ItemsSource = EnumText.Choices<CalculatedKind>(), SelectedItem = EnumText.Humanise(calculated.Kind), Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedItem is string text && EnumText.TryParse<CalculatedKind>(text, out var chosen) && chosen != calculated.Kind)
            {
                calculated.Kind = chosen;
                ShowCalculated();
            }
        };
        CalculatedDetails.Children.Add(Label("Holds"));
        CalculatedDetails.Children.Add(kind);

        if (calculated.Kind == CalculatedKind.Percentage)
        {
            var of = new ComboBox { ItemsSource = FormulaFields(calculated), SelectedItem = FormulaFields(calculated).FirstOrDefault(f => f.Equals(calculated.PercentageOf, StringComparison.OrdinalIgnoreCase)), Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            of.SelectionChanged += (_, _) => { if (of.SelectedItem is string field) calculated.PercentageOf = field; };
            CalculatedDetails.Children.Add(Label("A share of"));
            CalculatedDetails.Children.Add(of);

            var byGroup = new CheckBox { Content = "Of its group's total, not the whole schedule's", IsChecked = calculated.ByGroup };
            byGroup.Click += (_, _) => calculated.ByGroup = byGroup.IsChecked == true;
            CalculatedDetails.Children.Add(byGroup);
            return;
        }

        var check = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        void Check()
        {
            var problem = string.IsNullOrWhiteSpace(calculated.Formula) ? "Type a formula." : ScheduleFormula.Check(calculated.Formula, FormulaFields(calculated));
            check.Text = problem ?? "The formula reads correctly.";
            check.Foreground = problem is null ? Brushes.SeaGreen : new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        }

        var formula = new TextBox { Text = calculated.Formula, FontFamily = new FontFamily("Consolas"), AcceptsReturn = false };
        formula.TextChanged += (_, _) => { calculated.Formula = formula.Text; Check(); };
        CalculatedDetails.Children.Add(Label("Formula"));
        CalculatedDetails.Children.Add(formula);
        CalculatedDetails.Children.Add(check);
        Check();

        CalculatedDetails.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
            Text = "Fields by name as they are - Clear Width - or in square brackets. + - * / ^, = <> < > <= >=, and if(test, then, else), and, or, not, round, roundup, rounddown, abs, min, max, sqrt. "
                 + "Lengths are in millimetres, areas in square millimetres and volumes in cubic millimetres, as the model holds them: Width * Height held as an area is right as it is. Yes is 1 and No is 0."
        });

        var fields = new ListBox { ItemsSource = FormulaFields(calculated), Height = 150, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Double-click a field to put it in the formula" };
        fields.MouseDoubleClick += (_, _) =>
        {
            if (fields.SelectedItem is not string field) return;
            var text = field.Any(c => !char.IsLetterOrDigit(c) && c != ' ') ? $"[{field}]" : field;
            var at = formula.CaretIndex;
            formula.Text = formula.Text.Insert(at, text);
            formula.CaretIndex = at + text.Length;
            formula.Focus();
        };
        CalculatedDetails.Children.Add(Label("Fields it can use"));
        CalculatedDetails.Children.Add(fields);
    }

    // ---- filters -----------------------------------------------------------------

    private void RefreshFilters()
    {
        FilterRows.Children.Clear();
        foreach (var filter in _working.Filters.ToList())
        {
            var field = new ComboBox { ItemsSource = _working.Fields.ToList(), SelectedItem = _working.Fields.FirstOrDefault(f => f.Equals(filter.Field, StringComparison.OrdinalIgnoreCase)), Width = 200 };
            field.SelectionChanged += (_, _) => { if (field.SelectedItem is string chosen) filter.Field = chosen; };
            var comparison = new ComboBox { ItemsSource = Operators.Select(o => o.Text).ToList(), SelectedIndex = Array.FindIndex(Operators, o => o.Operator == filter.Comparison), Width = 140, Margin = new Thickness(8, 0, 8, 0) };
            var value = new TextBox { Text = filter.Value, Width = 160 };
            void ShowValue() => value.Visibility = filter.Comparison is ScheduleOperator.IsEmpty or ScheduleOperator.IsNotEmpty ? Visibility.Hidden : Visibility.Visible;
            comparison.SelectionChanged += (_, _) =>
            {
                if (comparison.SelectedIndex >= 0) filter.Comparison = Operators[comparison.SelectedIndex].Operator;
                ShowValue();
            };
            value.TextChanged += (_, _) => filter.Value = value.Text;
            ShowValue();
            FilterRows.Children.Add(RuleRow(field, comparison, value, Remove(() => { _working.Filters.Remove(filter); RefreshFilters(); })));
        }

        if (_working.Filters.Count == 0)
            FilterRows.Children.Add(new TextBlock { Text = "No filters: every element of the category is listed.", Opacity = 0.7 });
    }

    private void OnAddFilter(object sender, RoutedEventArgs e)
    {
        _working.Filters.Add(new ScheduleFilter(_working.Fields.FirstOrDefault() ?? string.Empty, ScheduleOperator.Equals));
        RefreshFilters();
    }

    // ---- sorting, grouping and totals --------------------------------------------

    private void RefreshSorting()
    {
        SortingDetails.Children.Clear();
        const string None = "(none)";
        var fields = new[] { None }.Concat(_working.Fields).ToList();

        var sort = new ComboBox { ItemsSource = fields, SelectedItem = fields.FirstOrDefault(f => f.Equals(_working.SortBy ?? None, StringComparison.OrdinalIgnoreCase)) ?? None, Width = 240 };
        sort.SelectionChanged += (_, _) => _working.SortBy = sort.SelectedItem is string chosen && chosen != None ? chosen : null;
        var descending = new CheckBox { Content = "Descending", IsChecked = _working.SortDescending, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        descending.Click += (_, _) => _working.SortDescending = descending.IsChecked == true;
        SortingDetails.Children.Add(Label("Sort by"));
        SortingDetails.Children.Add(Row(sort, descending));

        var group = new ComboBox { ItemsSource = fields, SelectedItem = fields.FirstOrDefault(f => f.Equals(_working.GroupBy ?? None, StringComparison.OrdinalIgnoreCase)) ?? None, Width = 240 };
        group.SelectionChanged += (_, _) => _working.GroupBy = group.SelectedItem is string chosen && chosen != None ? chosen : null;
        SortingDetails.Children.Add(Label("Group under"));
        SortingDetails.Children.Add(Row(group));

        var totals = new CheckBox { Content = "Totals - and subtotals under each group", IsChecked = _working.ShowTotals, Margin = new Thickness(0, 6, 0, 0) };
        totals.Click += (_, _) => _working.ShowTotals = totals.IsChecked == true;
        var itemize = new CheckBox { Content = "Itemize every instance - untick to count those alike in one row", IsChecked = _working.Itemize, Margin = new Thickness(0, 6, 0, 0) };
        itemize.Click += (_, _) => _working.Itemize = itemize.IsChecked == true;
        SortingDetails.Children.Add(totals);
        SortingDetails.Children.Add(itemize);

        SortingDetails.Children.Add(Label("Totalled", top: 14));
        SortingDetails.Children.Add(new TextBlock { Text = "A total only means something for a quantity: untick the fields whose sum is not one - heights, widths, a gradient.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 6) });
        var wrap = new WrapPanel();
        foreach (var field in _working.Fields)
        {
            var box = new CheckBox { Content = field, IsChecked = !_working.NotTotalled.Contains(field), Margin = new Thickness(0, 0, 14, 4) };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) _working.NotTotalled.Remove(field);
                else _working.NotTotalled.Add(field);
            };
            wrap.Children.Add(box);
        }

        SortingDetails.Children.Add(wrap);
    }

    // ---- conditional formatting ----------------------------------------------------

    private void RefreshFormats()
    {
        FormatRows.Children.Clear();
        foreach (var format in _working.Formats.ToList())
        {
            var field = new ComboBox { ItemsSource = _working.Fields.ToList(), SelectedItem = _working.Fields.FirstOrDefault(f => f.Equals(format.Field, StringComparison.OrdinalIgnoreCase)), Width = 170 };
            field.SelectionChanged += (_, _) => { if (field.SelectedItem is string chosen) format.Field = chosen; };
            var test = new ComboBox { ItemsSource = Tests.Select(t => t.Text).ToList(), SelectedIndex = Array.FindIndex(Tests, t => t.Test == format.Test), Width = 150, Margin = new Thickness(8, 0, 8, 0) };
            var value = new TextBox { Text = format.Value, Width = 90 };
            var value2 = new TextBox { Text = format.Value2, Width = 90, Margin = new Thickness(6, 0, 0, 0), ToolTip = "To" };
            void ShowValues()
            {
                value.Visibility = format.Test is FormatTest.HasValue or FormatTest.HasNoValue ? Visibility.Hidden : Visibility.Visible;
                value2.Visibility = format.Test is FormatTest.Between or FormatTest.NotBetween ? Visibility.Visible : Visibility.Hidden;
            }

            test.SelectionChanged += (_, _) =>
            {
                if (test.SelectedIndex >= 0) format.Test = Tests[test.SelectedIndex].Test;
                ShowValues();
            };
            value.TextChanged += (_, _) => format.Value = value.Text;
            value2.TextChanged += (_, _) => format.Value2 = value2.Text;
            ShowValues();

            var colour = new ComboBox { Width = 110, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var (name, rgb) in Swatches)
            {
                var swatch = new StackPanel { Orientation = Orientation.Horizontal };
                swatch.Children.Add(new Border { Width = 14, Height = 14, Background = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) });
                swatch.Children.Add(new TextBlock { Text = name });
                colour.Items.Add(new ComboBoxItem { Content = swatch, Tag = rgb });
            }

            var at = Swatches.ToList().FindIndex(s => s.Colour == format.Background);
            if (at < 0)
            {
                colour.Items.Add(new ComboBoxItem { Content = format.Background.ToString(), Tag = format.Background });
                at = colour.Items.Count - 1;
            }

            colour.SelectedIndex = at;
            colour.SelectionChanged += (_, _) => { if (colour.SelectedItem is ComboBoxItem { Tag: ColourRgb rgb }) format.Background = rgb; };

            FormatRows.Children.Add(RuleRow(field, test, value, value2, colour, Remove(() => { _working.Formats.Remove(format); RefreshFormats(); })));
        }

        if (_working.Formats.Count == 0)
            FormatRows.Children.Add(new TextBlock { Text = "No rules: no cell is coloured.", Opacity = 0.7 });
    }

    private void OnAddFormat(object sender, RoutedEventArgs e)
    {
        _working.Formats.Add(new ConditionalFormat { Field = _working.Fields.FirstOrDefault() ?? string.Empty });
        RefreshFormats();
    }

    private void OnFormatsOnSheets(object sender, RoutedEventArgs e) => _working.ShowFormatsOnSheets = FormatsOnSheetsBox.IsChecked == true;

    // ---- OK ----------------------------------------------------------------------

    /// <summary>Why the schedule cannot be kept as it is, or null.</summary>
    public string? Problems()
    {
        if (string.IsNullOrWhiteSpace(_working.Name)) return "A schedule needs a name.";
        if (_document.Schedules.Any(other => other.Id != _working.Id && other.Name.Equals(_working.Name, StringComparison.OrdinalIgnoreCase)))
            return $"There is a schedule called {_working.Name} already.";
        if (_working.Fields.Count == 0) return "A schedule needs a field at least.";

        foreach (var calculated in _working.Calculated)
        {
            if (calculated.Kind == CalculatedKind.Percentage)
            {
                if (string.IsNullOrWhiteSpace(calculated.PercentageOf)) return $"{calculated.Name}: pick the field it is a share of.";
                continue;
            }

            if (string.IsNullOrWhiteSpace(calculated.Formula)) return $"{calculated.Name}: type its formula.";
            if (ScheduleFormula.Check(calculated.Formula, FormulaFields(calculated)) is { } problem) return $"{calculated.Name}: {problem}";
        }

        return null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        OnNameChanged(sender, e);
        if (Problems() is { } problem)
        {
            Problem.Text = problem;
            return;
        }

        DialogResult = true;
    }

    // ---- pieces --------------------------------------------------------------------

    private static TextBlock Label(string text, double top = 0) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };

    private static StackPanel Row(params FrameworkElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static StackPanel RuleRow(params FrameworkElement[] children)
    {
        var row = Row(children);
        row.Margin = new Thickness(0, 0, 0, 6);
        return row;
    }

    private static Button Remove(Action remove)
    {
        var button = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0), ToolTip = "Remove" };
        button.Click += (_, _) => remove();
        return button;
    }
}
