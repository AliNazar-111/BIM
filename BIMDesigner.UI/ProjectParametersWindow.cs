using System.IO;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The project's own parameters: each its name, kind of value, whether on each element or each
/// type, where it is listed and the categories it is on - made here, or taken from a shared
/// parameter file with its GUID, and written out to one for other projects. Edits are made to
/// copies; OK puts them in as one step to undo.
/// </summary>
public sealed class ProjectParametersWindow : System.Windows.Window
{
    private readonly List<ProjectParameter> _parameters;
    private readonly ListBox _list = new() { Width = 260 };
    private readonly StackPanel _details = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _problem = new();

    private static readonly ParameterDataType[] Types =
    {
        ParameterDataType.Text, ParameterDataType.MultilineText, ParameterDataType.Integer, ParameterDataType.Number, ParameterDataType.Length,
        ParameterDataType.Area, ParameterDataType.Volume, ParameterDataType.Angle, ParameterDataType.Currency, ParameterDataType.YesNo, ParameterDataType.Url
    };

    public ProjectParametersWindow(BimDocument document)
    {
        DialogLook.Apply(this, "Project Parameters", 900, 600);
        _parameters = document.ProjectParameters.Select(parameter => parameter.Copy()).ToList();
        _list.SelectionChanged += (_, _) => ShowDetails();

        var add = new Button { Content = "New" };
        add.Click += (_, _) => Add("Parameter");
        var remove = new Button { Content = "Delete" };
        remove.Click += (_, _) =>
        {
            if (Picked is not { } parameter) return;
            _parameters.Remove(parameter);
            Refresh(null);
        };
        var load = new Button { Content = "From Shared File..." };
        load.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Shared Parameter File", Filter = "Shared parameter files (*.txt)|*.txt|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true) LoadShared(File.ReadAllText(dialog.FileName));
        };
        var save = new Button { Content = "To Shared File..." };
        save.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Title = "Save Shared Parameters", Filter = "Shared parameter files (*.txt)|*.txt", FileName = "Shared Parameters.txt" };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllText(dialog.FileName, ProjectParameters.WriteSharedFile(_parameters)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _problem.Text = exception.Message; }
        };
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (Problems() is { } problem) _problem.Text = problem;
            else DialogResult = true;
        };

        var body = new DockPanel { Margin = new Thickness(16, 0, 16, 12) };
        DockPanel.SetDock(_list, Dock.Left);
        body.Children.Add(_list);
        body.Children.Add(new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "A parameter of the project's own is on every element - or every type - of the categories ticked, beside what they have anyway: shown in Properties, scheduled, and written to IFC and COBie. "
            + "A shared parameter comes from a shared parameter file with its GUID, so it is the same parameter in every project that takes it."));
        dock.Children.Add(DialogLook.Footer(_problem, add, remove, load, save, ok, new Button { Content = "Cancel", IsCancel = true }));
        dock.Children.Add(body);
        Content = dock;
        Refresh(_parameters.FirstOrDefault());
    }

    /// <summary>The parameters as edited.</summary>
    public IReadOnlyList<ProjectParameter> Parameters => _parameters;

    private ProjectParameter? Picked => (_list.SelectedItem as ListBoxItem)?.Tag as ProjectParameter;

    public ProjectParameter Add(string stem)
    {
        var taken = _parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parameter = new ProjectParameter { Name = Enumerable.Range(1, 999).Select(i => i == 1 ? stem : $"{stem} {i}").First(name => !taken.Contains(name)) };
        _parameters.Add(parameter);
        Refresh(parameter);
        return parameter;
    }

    /// <summary>A shared parameter file's parameters taken in - those not here already, by their GUID - on no category yet. Null, or what was wrong.</summary>
    public string? LoadShared(string text)
    {
        var read = ProjectParameters.ReadSharedFile(text, out var problems);
        var added = read.Where(parameter => _parameters.All(existing => existing.Id != parameter.Id)).ToList();
        _parameters.AddRange(added);
        Refresh(added.FirstOrDefault());
        _problem.Text = read.Count == 0 ? "There are no parameters in it." : problems.Count > 0 ? problems[0] : added.Count == 0 ? "They are all here already." : $"{added.Count} taken in: tick the categories each is on.";
        return problems.Count > 0 ? problems[0] : null;
    }

    public string? Problems()
    {
        if (_parameters.FirstOrDefault(parameter => string.IsNullOrWhiteSpace(parameter.Name)) is not null) return "A parameter has no name.";
        if (_parameters.GroupBy(parameter => parameter.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } twice)
            return $"Two parameters are called {twice.Key}.";
        return null;
    }

    private void Refresh(ProjectParameter? select)
    {
        _list.Items.Clear();
        foreach (var parameter in _parameters)
        {
            var item = new ListBoxItem
            {
                Content = $"{parameter.Name}  -  {EnumText.Humanise(parameter.DataType)}, {(parameter.Binding == ParameterBinding.Type ? "type" : "instance")}{(parameter.Shared ? ", shared" : string.Empty)}",
                Tag = parameter
            };
            _list.Items.Add(item);
            if (ReferenceEquals(parameter, select)) item.IsSelected = true;
        }

        if (_list.SelectedItem is null && _list.Items.Count > 0) _list.SelectedIndex = 0;
        ShowDetails();
    }

    private static TextBlock Label(string text, double top = 10) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };

    private void ShowDetails()
    {
        _details.Children.Clear();
        if (Picked is not { } parameter)
        {
            _details.Children.Add(new TextBlock { Text = "No parameters of the project's own yet: New makes one, or take them from a shared parameter file.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            return;
        }

        void Relabel()
        {
            if (_list.SelectedItem is ListBoxItem item)
                item.Content = $"{parameter.Name}  -  {EnumText.Humanise(parameter.DataType)}, {(parameter.Binding == ParameterBinding.Type ? "type" : "instance")}{(parameter.Shared ? ", shared" : string.Empty)}";
        }

        var name = new TextBox { Text = parameter.Name, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        name.LostFocus += (_, _) => { if (name.Text.Trim().Length > 0) { parameter.Name = name.Text.Trim(); Relabel(); } };
        _details.Children.Add(Label("Name", 0));
        _details.Children.Add(name);

        var type = new ComboBox { ItemsSource = Types.Select(t => EnumText.Humanise(t)).ToList(), SelectedIndex = Array.IndexOf(Types, parameter.DataType), Width = 180, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !parameter.Shared };
        type.SelectionChanged += (_, _) => { if (type.SelectedIndex >= 0) { parameter.DataType = Types[type.SelectedIndex]; Relabel(); } };
        _details.Children.Add(Label("Holds" + (parameter.Shared ? " - set by its shared file" : string.Empty)));
        _details.Children.Add(type);

        var instance = new RadioButton { Content = "On each element", IsChecked = parameter.Binding == ParameterBinding.Instance, Margin = new Thickness(0, 0, 16, 0) };
        var typed = new RadioButton { Content = "On each type", IsChecked = parameter.Binding == ParameterBinding.Type };
        instance.Checked += (_, _) => { parameter.Binding = ParameterBinding.Instance; Relabel(); };
        typed.Checked += (_, _) => { parameter.Binding = ParameterBinding.Type; Relabel(); };
        var binding = new StackPanel { Orientation = Orientation.Horizontal };
        binding.Children.Add(instance);
        binding.Children.Add(typed);
        _details.Children.Add(Label("Where"));
        _details.Children.Add(binding);

        var groups = Enum.GetValues<ParameterGroup>();
        var group = new ComboBox { ItemsSource = groups.Select(g => EnumText.Humanise(g)).ToList(), SelectedIndex = Array.IndexOf(groups, parameter.Group), Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        group.SelectionChanged += (_, _) => { if (group.SelectedIndex >= 0) parameter.Group = groups[group.SelectedIndex]; };
        _details.Children.Add(Label("Listed under"));
        _details.Children.Add(group);

        _details.Children.Add(Label("On"));
        var categories = new WrapPanel();
        foreach (var category in ViewFiltersWindow.Categories.Append(BuiltInCategory.Sheets))
        {
            var box = new CheckBox { Content = EnumText.Humanise(category), IsChecked = parameter.Categories.Contains(category), Margin = new Thickness(0, 0, 12, 4), Width = 160 };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) parameter.Categories.Add(category);
                else parameter.Categories.Remove(category);
            };
            categories.Children.Add(box);
        }

        _details.Children.Add(categories);
        var description = new TextBox { Text = parameter.Description, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        description.TextChanged += (_, _) => parameter.Description = description.Text;
        _details.Children.Add(Label("Description"));
        _details.Children.Add(description);
        if (parameter.Shared)
            _details.Children.Add(new TextBlock { Text = $"Shared: {parameter.Id:D}{(parameter.SharedGroup.Length > 0 ? $", in its file's {parameter.SharedGroup} group" : string.Empty)}", Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0) });
    }
}

/// <summary>
/// The classification types and rooms are coded to: its name and edition, and its codes and their
/// titles - the Uniclass starter set, or a table read from a file of codes and titles, as NBS
/// publishes Uniclass's tables. Edits are made to a copy; OK puts it in as one step to undo.
/// </summary>
public sealed class ClassificationWindow : System.Windows.Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<Row> _rows = new();
    private readonly TextBox _system = new() { Width = 220 };
    private readonly TextBox _edition = new() { Width = 260, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _problem = new();

    public sealed class Row
    {
        public string Code { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
    }

    public ClassificationWindow(BimDocument document)
    {
        DialogLook.Apply(this, "Classification", 760, 600);
        Fill(document.Classification);

        var load = new Button { Content = "Load File..." };
        load.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Load Classification", Filter = "Tables (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            try { Load(File.ReadAllText(dialog.FileName)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _problem.Text = exception.Message; }
        };
        var starter = new Button { Content = "Starter Set" };
        starter.Click += (_, _) => Fill(ClassificationTable.Starter());
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (_rows.Where(row => !string.IsNullOrWhiteSpace(row.Code)).GroupBy(row => row.Code.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
                _problem.Text = $"{twice.Key} is in the table twice.";
            else DialogResult = true;
        };

        var grid = new DataGrid { ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = true, HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(16, 8, 16, 12) };
        grid.Columns.Add(new DataGridTextColumn { Header = "Code", Binding = new System.Windows.Data.Binding(nameof(Row.Code)), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Title", Binding = new System.Windows.Data.Binding(nameof(Row.Title)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var names = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 0) };
        names.Children.Add(_system);
        names.Children.Add(_edition);
        DockPanel.SetDock(names, Dock.Top);

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro("Give each type its code in Type Properties, under Classification - and each room its own. The codes go to IFC as classification references and to COBie as categories. "
                                            + "A file is a code and a title a line, separated by a tab or a comma - NBS's downloads of Uniclass's tables, saved as CSV, read as they are."));
        dock.Children.Add(names);
        dock.Children.Add(DialogLook.Footer(_problem, load, starter, ok, new Button { Content = "Cancel", IsCancel = true }));
        dock.Children.Add(grid);
        Content = dock;
    }

    /// <summary>The table as edited.</summary>
    public ClassificationTable Table
    {
        get
        {
            var table = new ClassificationTable { System = _system.Text.Trim(), Edition = _edition.Text.Trim() };
            table.Entries.AddRange(_rows.Where(row => !string.IsNullOrWhiteSpace(row.Code)).Select(row => new ClassificationEntry(row.Code.Trim(), row.Title?.Trim() ?? string.Empty)));
            return table;
        }
    }

    /// <summary>A file of codes and titles read in place of the table.</summary>
    public void Load(string text)
    {
        var table = ClassificationTable.Parse(text, _system.Text.Trim());
        if (table.Entries.Count == 0)
        {
            _problem.Text = "There are no codes in it: a code and a title a line, separated by a tab or a comma.";
            return;
        }

        table.Edition = string.Empty;
        Fill(table);
        _problem.Text = $"{table.Entries.Count} codes read.";
    }

    private void Fill(ClassificationTable table)
    {
        (_system.Text, _edition.Text) = (table.System, table.Edition);
        _rows.Clear();
        foreach (var entry in table.Entries) _rows.Add(new Row { Code = entry.Code, Title = entry.Title });
    }
}
