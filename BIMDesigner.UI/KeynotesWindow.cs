using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The project's keynotes: the table of keys and what they say, read from an office's
/// tab-separated file or begun from the starter set and edited here; and each material's key,
/// which material keynotes read. Changes are made to a copy; OK puts them into the project as
/// one step to undo.
/// </summary>
public sealed class KeynotesWindow : System.Windows.Window
{
    private readonly BimDocument _document;
    private readonly ObservableCollection<EntryRow> _entries = new();
    private readonly ObservableCollection<MaterialRow> _materials = new();
    private readonly TextBlock _source = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _problem = new();
    private string _path;

    /// <summary>A key, what it says and the key it comes under: a row of the table, editable.</summary>
    public sealed class EntryRow
    {
        public string Key { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Under { get; set; } = string.Empty;
    }

    /// <summary>A material and its key.</summary>
    public sealed class MaterialRow : INotifyPropertyChanged
    {
        private string _keynote = string.Empty;

        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required Func<string, string> Says { get; init; }

        public string Keynote
        {
            get => _keynote;
            set
            {
                _keynote = value?.Trim() ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Keynote)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public string Text => Says(Keynote);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public KeynotesWindow(BimDocument document)
    {
        DialogLook.Apply(this, "Keynotes", 980, 640);
        _document = document;
        _path = document.Keynotes.Source;
        Fill(document.Keynotes);
        foreach (var material in document.Materials.OrderBy(material => material.Name, StringComparer.OrdinalIgnoreCase))
            _materials.Add(new MaterialRow { Id = material.Id, Name = material.Name, Says = Says, Keynote = material.Keynote });

        var tabs = new TabControl { Margin = new Thickness(16, 0, 16, 12) };
        tabs.Items.Add(new TabItem { Header = "Keynotes", Content = TableTab() });
        tabs.Items.Add(new TabItem { Header = "Materials", Content = MaterialsTab() });

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (Problems() is { } problem) _problem.Text = problem;
            else DialogResult = true;
        };

        var dock = new DockPanel();
        dock.Children.Add(DialogLook.Intro(
            "A keynote file has a keynote a line: its key, a tab, what it says, and a tab and the key it comes under where it comes under one. "
            + "Element keynotes read the key of the element's type - set in Type Properties; material keynotes the key of the material, set here."));
        dock.Children.Add(DialogLook.Footer(_problem, ok, new Button { Content = "Cancel", IsCancel = true }));
        dock.Children.Add(tabs);
        Content = dock;
        ShowSource();
    }

    /// <summary>The table as edited: what OK hands back.</summary>
    public KeynoteTable Table
    {
        get
        {
            var table = new KeynoteTable { Source = _path };
            table.Entries.AddRange(_entries.Where(row => !string.IsNullOrWhiteSpace(row.Key))
                .Select(row => new KeynoteEntry(row.Key.Trim(), row.Text?.Trim() ?? string.Empty, row.Under?.Trim() ?? string.Empty)));
            return table;
        }
    }

    /// <summary>Each material's key as edited, by its id.</summary>
    public IReadOnlyDictionary<Guid, string> MaterialKeynotes => _materials.ToDictionary(row => row.Id, row => row.Keynote);

    /// <summary>The rows the table shows, to edit: for driving it without showing it.</summary>
    public IList<EntryRow> Entries => _entries;

    /// <summary>A material given a key, as typing it would.</summary>
    public void SetMaterialKeynote(Guid material, string key)
    {
        if (_materials.FirstOrDefault(row => row.Id == material) is { } row) row.Keynote = key;
    }

    /// <summary>Why the table cannot be kept as it is, or null.</summary>
    public string? Problems()
    {
        var keys = _entries.Where(row => !string.IsNullOrWhiteSpace(row.Key)).Select(row => row.Key.Trim()).ToList();
        if (keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } twice)
            return $"{twice.Key} is in the table twice: a key says one thing.";
        if (_entries.FirstOrDefault(row => string.IsNullOrWhiteSpace(row.Key) && !string.IsNullOrWhiteSpace(row.Text)) is { } keyless)
            return $"\"{keyless.Text}\" has no key.";
        return null;
    }

    /// <summary>A keynote file read into the table in place of what it holds. Null, or what was wrong with it.</summary>
    public string? Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _problem.Text = exception.Message;
            return exception.Message;
        }

        var table = KeynoteTable.Parse(text, out var problems);
        if (table.Entries.Count == 0)
        {
            _problem.Text = "There are no keynotes in it: a line each, key and text separated by a tab.";
            return _problem.Text;
        }

        _path = path;
        Fill(table);
        ShowSource();
        _problem.Text = problems.Count == 0 ? string.Empty : $"{problems.Count} {(problems.Count == 1 ? "line" : "lines")} left out: {problems[0]}";
        return problems.Count == 0 ? null : _problem.Text;
    }

    private string Says(string key) =>
        string.IsNullOrWhiteSpace(key) ? string.Empty
        : _entries.FirstOrDefault(row => row.Key.Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) is { } row ? row.Text
        : "(not in the table)";

    private void Fill(KeynoteTable table)
    {
        _entries.Clear();
        foreach (var entry in table.Entries) _entries.Add(new EntryRow { Key = entry.Key, Text = entry.Text, Under = entry.Parent });
    }

    private void ShowSource() => _source.Text = string.IsNullOrWhiteSpace(_path) ? "Kept in the project." : $"Read from {_path}";

    private UIElement TableTab()
    {
        var grid = new DataGrid
        {
            ItemsSource = _entries, AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true,
            HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(0, 8, 0, 0)
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Key", Binding = new Binding(nameof(EntryRow.Key)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Keynote", Binding = new Binding(nameof(EntryRow.Text)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Under", Binding = new Binding(nameof(EntryRow.Under)), Width = 90 });

        var load = new Button { Content = "Load File...", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        load.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Load Keynotes", Filter = "Keynote files (*.txt)|*.txt|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true) Load(dialog.FileName);
        };
        var reload = new Button { Content = "Reload", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Read the file it came from again, for what has changed in it" };
        reload.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_path)) _problem.Text = "It was not read from a file.";
            else Load(_path);
        };
        var save = new Button { Content = "Save As...", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        save.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Title = "Save Keynotes", Filter = "Keynote files (*.txt)|*.txt", FileName = "Keynotes.txt" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, Table.Write());
                _path = dialog.FileName;
                ShowSource();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _problem.Text = exception.Message; }
        };
        var starter = new Button { Content = "Starter Set", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 12, 0), ToolTip = "Start again from the keynotes a project starts with" };
        starter.Click += (_, _) =>
        {
            _path = string.Empty;
            Fill(KeynoteTable.Starter());
            ShowSource();
        };

        var bar = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { load, reload, save, starter }) buttons.Children.Add(button);
        DockPanel.SetDock(buttons, Dock.Left);
        bar.Children.Add(buttons);
        bar.Children.Add(_source);

        var panel = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(grid);
        return panel;
    }

    private UIElement MaterialsTab()
    {
        var grid = new DataGrid
        {
            ItemsSource = _materials, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(8)
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Material", Binding = new Binding(nameof(MaterialRow.Name)), IsReadOnly = true, Width = 220 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Key", Binding = new Binding(nameof(MaterialRow.Keynote)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Keynote", Binding = new Binding(nameof(MaterialRow.Text)) { Mode = BindingMode.OneWay }, IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        return grid;
    }
}
