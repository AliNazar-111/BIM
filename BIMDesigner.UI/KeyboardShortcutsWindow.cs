using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIMDesigner.Infrastructure.Projects;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// Keyboard Shortcuts: every command on the ribbon with its keys, found by name or by tab, or
/// only those with keys. Pick one, press the new keys in the box - a run of letters, or a key held
/// with Ctrl, Alt or Shift, or a function key - and Assign; keys another command had are taken off
/// it, and said. Remove takes a command's keys away; Reset All goes back to the defaults. Export
/// writes them to a file to hand round an office, and Import reads one - another program's too,
/// matching its commands to these by name. OK keeps them for this person, whatever project is open.
/// </summary>
internal sealed class KeyboardShortcutsWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly IReadOnlyList<ShortcutCommand> _commands;
    private ShortcutMap _map;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBox _search = new() { MinWidth = 200, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _filter = new() { MinWidth = 150, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _press = new() { MinWidth = 160, IsReadOnly = true, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _own = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _picked = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _assign = new() { Content = "Assign", IsEnabled = false };
    private readonly TextBlock _said = new();

    private const string AllCommands = "All commands";
    private const string WithKeys = "Those with keys";

    public KeyboardShortcutsWindow(MainWindow main)
    {
        _main = main;
        _commands = main.ShortcutCommands;
        _map = main.Shortcuts.Copy();
        DialogLook.Apply(this, "Keyboard Shortcuts", 860, 640);
        MinWidth = 560;
        MinHeight = 400;

        _list.SetResourceReference(BackgroundProperty, "Bg.Window");
        _list.SetResourceReference(ForegroundProperty, "Text");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => ShowPicked();

        _filter.Items.Add(AllCommands);
        _filter.Items.Add(WithKeys);
        foreach (var tab in _commands.Select(command => command.Path.Split(" > ")[0]).Distinct()) _filter.Items.Add(tab);
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 8) };
        top.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_search);
        top.Children.Add(_filter);
        DockPanel.SetDock(top, Dock.Top);

        // The keys pressed in the box, written as they will be kept.
        _press.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Tab) return;
            e.Handled = true;
            if (key is Key.Back or Key.Escape or Key.Delete)
            {
                _press.Text = string.Empty;
            }
            else if (key is >= Key.A and <= Key.Z && Keyboard.Modifiers == ModifierKeys.None)
            {
                var letter = KeyChords.NameOf(key)!;
                _press.Text = KeyChords.IsRun(_press.Text) && _press.Text.Length < KeyChords.LongestRun ? _press.Text + letter : letter;
            }
            else if (KeyChords.Of(key, Keyboard.Modifiers) is { } chord && (Keyboard.Modifiers != ModifierKeys.None || chord.StartsWith('F')))
            {
                _press.Text = chord;
            }

            _press.CaretIndex = _press.Text.Length;
            _assign.IsEnabled = Picked is not null && _press.Text.Length > 0;
        };
        _assign.Click += (_, _) => Assign(_press.Text);

        var setting = new Grid { Margin = new Thickness(16, 0, 16, 10) };
        setting.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        setting.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        setting.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var keysNow = new StackPanel { Orientation = Orientation.Horizontal };
        keysNow.Children.Add(_picked);
        keysNow.Children.Add(_own);
        setting.Children.Add(keysNow);
        var press = new StackPanel { Orientation = Orientation.Horizontal };
        press.Children.Add(new TextBlock { Text = "Press new keys", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        press.Children.Add(_press);
        _assign.Margin = new Thickness(8, 0, 0, 0);
        _assign.Padding = new Thickness(14, 4, 14, 4);
        press.Children.Add(_assign);
        Grid.SetColumn(press, 2);
        setting.Children.Add(press);
        DockPanel.SetDock(setting, Dock.Bottom);

        var import = new Button { Content = "Import..." };
        import.Click += (_, _) => Import();
        var export = new Button { Content = "Export..." };
        export.Click += (_, _) => Export();
        var reset = new Button { Content = "Reset All" };
        reset.Click += (_, _) =>
        {
            _map = ShortcutMap.Starting(_commands);
            Fill();
            DialogLook.Say(_said, "Every command back to the keys it starts with. OK keeps it.");
        };
        var ok = new Button { Content = "OK", IsDefault = false };
        ok.Click += (_, _) => Keep();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, import, export, reset, ok, cancel));
        root.Children.Add(DialogLook.Intro(
            "Keys for any command on the ribbon: a run of letters typed one after another - W, or WA - or a key held with Ctrl, Alt or Shift, or a function key. "
            + "Pick a command, press its new keys in the box and Assign: keys another command had are taken off it. A run that begins a longer one waits a moment for the rest. "
            + "They are kept for you, whatever project is open."));
        root.Children.Add(top);
        root.Children.Add(setting);
        root.Children.Add(new Border { Child = _list, BorderThickness = new Thickness(1), Margin = new Thickness(16, 0, 16, 8) }.Lined());
        Content = root;
        Fill();
    }

    /// <summary>The command picked in the list.</summary>
    public ShortcutCommand? Picked => (_list.SelectedItem as ListBoxItem)?.Tag as ShortcutCommand;

    /// <summary>The keys as they stand in the window, not yet kept.</summary>
    internal ShortcutMap Map => _map;

    /// <summary>The commands the search and the filter leave, each with its keys and where it is.</summary>
    private void Fill()
    {
        var picked = Picked;
        var find = _search.Text.Trim();
        var filter = _filter.SelectedItem as string ?? AllCommands;
        _list.Items.Clear();
        foreach (var command in _commands
                     .Where(command => filter == AllCommands || filter == WithKeys && _map.KeysOf(command.Id).Count > 0 || command.Path.Split(" > ")[0] == filter)
                     .Where(command => find.Length == 0 || command.Name.Contains(find, StringComparison.CurrentCultureIgnoreCase)
                                                        || string.Join(" ", _map.KeysOf(command.Id)).Contains(find, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var keys = new TextBlock { Text = string.Join(", ", _map.KeysOf(command.Id)), FontWeight = FontWeights.SemiBold };
            var path = new TextBlock { Text = command.Path, TextTrimming = TextTrimming.CharacterEllipsis };
            path.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            row.Children.Add(new TextBlock { Text = command.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(keys, 1);
            Grid.SetColumn(path, 2);
            row.Children.Add(keys);
            row.Children.Add(path);
            var item = new ListBoxItem { Content = row, Tag = command, Padding = new Thickness(8, 3, 8, 3) };
            _list.Items.Add(item);
            if (ReferenceEquals(command, picked)) _list.SelectedItem = item;
        }

        ShowPicked();
    }

    /// <summary>The command picked: its keys, each to take off.</summary>
    private void ShowPicked()
    {
        _own.Children.Clear();
        var picked = Picked;
        _picked.Text = picked is null ? "Pick a command." : $"{picked.Name}:";
        _assign.IsEnabled = picked is not null && _press.Text.Length > 0;
        if (picked is null) return;

        var keys = _map.KeysOf(picked.Id);
        if (keys.Count == 0) _own.Children.Add(new TextBlock { Text = "no keys", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        foreach (var each in keys.ToList())
        {
            var remove = new Button { Content = $"{each}  ✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = $"Take {each} off {picked.Name}" };
            remove.Click += (_, _) =>
            {
                _map.Remove(picked.Id, each);
                Fill();
                DialogLook.Say(_said, $"{each} taken off {picked.Name}.");
            };
            _own.Children.Add(remove);
        }
    }

    /// <summary>
    /// Keys given to the command picked - taken off another that had them, and said. Null where
    /// they went on; what was wrong where they did not. Internal so the harness and tests can press it.
    /// </summary>
    internal string? Assign(string typed)
    {
        if (Picked is not { } command) return Warn("Pick a command first.");
        if (KeyChords.Tidy(typed) is not { } keys) return Warn($"\"{typed}\" are not keys: one to five letters, or a key with Ctrl, Alt or Shift, or a function key.");
        if (KeyChords.Refusal(keys) is { } refusal) return Warn(refusal);

        var from = _map.Assign(command.Id, keys);
        _press.Text = string.Empty;
        Fill();
        var said = from is null ? $"{keys} now {command.Name}." : $"{keys} now {command.Name} - taken off {_commands.FirstOrDefault(other => other.Id == from)?.Name ?? from}.";

        // A run that begins another, or that another begins, waits a moment when typed.
        if (KeyChords.IsRun(keys))
        {
            var shorter = Enumerable.Range(1, keys.Length - 1).Select(length => keys[..length]).FirstOrDefault(start => _map.Owner(start) is not null);
            if (shorter is not null) said += $" {shorter} waits a moment now, for the {keys[shorter.Length..]}.";
            else if (_map.Begins(keys)) said += $" {keys} waits a moment, for the rest of a longer one.";
        }

        DialogLook.Say(_said, said);
        return null;
    }

    private string Warn(string said)
    {
        DialogLook.Warn(_said, said);
        return said;
    }

    /// <summary>The keys kept, for now and next time.</summary>
    internal void Keep()
    {
        _main.UseShortcuts(_map);
        Close();
    }

    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "Export Keyboard Shortcuts", Filter = "Keyboard shortcuts (*.xml)|*.xml", FileName = "Keyboard Shortcuts.xml" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ShortcutFile.Write(dialog.FileName, _map.Entries(_commands));
            DialogLook.Say(_said, $"Written to {System.IO.Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            Warn($"Could not write it: {error.Message}");
        }
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Import Keyboard Shortcuts", Filter = "Keyboard shortcuts (*.xml)|*.xml" };
        if (dialog.ShowDialog(this) == true) ImportFrom(dialog.FileName);
    }

    /// <summary>Shortcuts read from a file over these. Internal so tests can read one.</summary>
    internal void ImportFrom(string path)
    {
        if (ShortcutFile.Read(path) is not { } entries)
        {
            Warn($"{System.IO.Path.GetFileName(path)} is not a keyboard shortcuts file.");
            return;
        }

        var lost = _map.Take(entries, _commands);
        Fill();
        DialogLook.Say(_said, $"Read {entries.Count(entry => entry.Keys.Count > 0)} commands' keys from {System.IO.Path.GetFileName(path)}"
                              + (lost > 0 ? $"; {lost} are for commands not here." : ".") + " OK keeps them.");
    }
}
