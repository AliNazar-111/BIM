using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BIMDesigner.Infrastructure.Projects;

namespace BIMDesigner.UI;

/// <summary>
/// Keyboard shortcuts set by whoever uses the program: every button and tool on the ribbon a
/// command keys can be set for, by the keys in Keyboard Shortcuts - a run of letters typed one
/// after another, W or WA, or a key held with Ctrl, Alt or Shift, or a function key. A run that
/// begins a longer one waits a moment for the rest. Kept for the person, whatever project is open.
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<ShortcutCommand>? _shortcutCommands;
    private ShortcutMap? _shortcuts;
    private KeyboardShortcutsWindow? _shortcutsWindow;

    /// <summary>The letters typed so far toward a shortcut.</summary>
    private string _typedKeys = string.Empty;

    private DispatcherTimer? _keyWait;

    /// <summary>How long a run that begins a longer one waits for the rest.</summary>
    private static readonly TimeSpan KeyWait = TimeSpan.FromMilliseconds(800);

    /// <summary>The tabs whose commands draw on or change the model shown in the plan.</summary>
    private static readonly HashSet<string> DrawingTabs = new(StringComparer.Ordinal) { "Architecture", "Structure", "Systems", "Site", "Modify", "Annotate" };

    /// <summary>Every command keys can be set for: each button and tool on the ribbon's tabs, by tab and panel.</summary>
    internal IReadOnlyList<ShortcutCommand> ShortcutCommands => _shortcutCommands ??= GatherCommands();

    /// <summary>Which keys run which command, as set - the defaults until any are.</summary>
    internal ShortcutMap Shortcuts
    {
        get
        {
            if (_shortcuts is not null) return _shortcuts;
            _shortcuts = ShortcutMap.Starting(ShortcutCommands);
            if (ShortcutFile.Load() is { } kept) _shortcuts.Take(kept, ShortcutCommands);
            return _shortcuts;
        }
    }

    /// <summary>Shortcuts set from Keyboard Shortcuts: used from now on, and kept for next time.</summary>
    internal void UseShortcuts(ShortcutMap map)
    {
        _shortcuts = map;
        try
        {
            ShortcutFile.Save(map.Entries(ShortcutCommands));
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusHint.Text = $"The shortcuts are set for now, but could not be kept for next time: {error.Message}";
        }
    }

    private void OnKeyboardShortcuts(object sender, RoutedEventArgs e) => OpenKeyboardShortcuts();

    internal KeyboardShortcutsWindow OpenKeyboardShortcuts() => Open(ref _shortcutsWindow, () => new KeyboardShortcutsWindow(this), this);

    /// <summary>The ribbon's buttons and tools, each a command - its id its name where it has one, else its tab, panel and label.</summary>
    private List<ShortcutCommand> GatherCommands()
    {
        var found = new List<ShortcutCommand>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var press = typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;

        void Add(ButtonBase button, string tab, string panel)
        {
            if (button.Content is not string label || label.Trim().Length == 0) return;
            var name = label.Replace("_", string.Empty).Trim();
            var id = string.IsNullOrEmpty(button.Name) ? $"{tab}>{panel}>{name}" : button.Name;
            if (!ids.Add(id)) return;
            var owner = Ancestor<TabItem>(button);
            found.Add(new ShortcutCommand(id, name, panel.Length == 0 ? tab : $"{tab} > {panel}",
                () => press.Invoke(button, null),
                () => button.IsEnabled && owner?.Visibility != Visibility.Collapsed,
                needsPlan: button is RadioButton || DrawingTabs.Contains(tab)));
        }

        foreach (var tab in Ribbon.Items.OfType<TabItem>().Where(tab => !ReferenceEquals(tab, ContextTab)))
        {
            var title = tab.Header as string ?? string.Empty;
            foreach (var panel in LogicalDescendants(tab).OfType<HeaderedContentControl>())
            foreach (var button in LogicalDescendants(panel).OfType<ButtonBase>())
                Add(button, title, panel.Header as string ?? string.Empty);
        }

        // A tool kept off the ribbon - Select - is still one keys can be set for.
        foreach (var tool in ToolButtons().Where(tool => !ids.Contains(tool.Name)))
            Add(tool, "Tools", string.Empty);

        return found;
    }

    private static IEnumerable<object> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            yield return child;
            if (child is DependencyObject inner)
                foreach (var below in LogicalDescendants(inner))
                    yield return below;
        }
    }

    private static T? Ancestor<T>(DependencyObject? from) where T : DependencyObject
    {
        for (var at = from; at is not null; at = LogicalTreeHelper.GetParent(at))
            if (at is T found) return found;
        return null;
    }

    /// <summary>
    /// A key pressed, taken as a shortcut where it is one: a key held with Ctrl, Alt or Shift, or a
    /// function key, at once; a letter added to the run typed so far, run when the run is a
    /// command's and no longer one begins with it - or after a moment, where one does. Whether it
    /// was taken.
    /// </summary>
    private bool TakeShortcut(KeyEventArgs e) => TakeShortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, e.IsRepeat);

    /// <summary>A key pressed with what is held, taken as a shortcut where it is one. Internal so tests can press keys.</summary>
    internal bool TakeShortcut(Key key, ModifierKeys held, bool repeat = false)
    {
        var letter = key is >= Key.A and <= Key.Z && held == ModifierKeys.None;

        if (!letter)
        {
            // A figure on its own starts a length, not a shortcut; Shift alone with a figure types one.
            if (KeyChords.Of(key, held) is not { } chord || chord.Length == 1 && char.IsDigit(chord[0])) return false;
            if (held == ModifierKeys.Shift && KeyChords.NameOf(key) is { Length: 1 } typed && char.IsDigit(typed[0])) return false;
            ForgetTypedKeys();
            return Shortcuts.Owner(chord) is { } owner && RunShortcut(owner, chord);
        }

        // A letter held down does not type it again and again.
        if (repeat) return true;

        _keyWait?.Stop();
        var run = _typedKeys + KeyChords.NameOf(key);

        // The run so far goes no further: what it was is run, and this letter starts afresh.
        if (Shortcuts.Owner(run) is null && !Shortcuts.Begins(run) && _typedKeys.Length > 0)
        {
            var was = _typedKeys;
            ForgetTypedKeys();
            if (Shortcuts.Owner(was) is { } pending) RunShortcut(pending, was);
            run = KeyChords.NameOf(key)!;
        }

        var exact = Shortcuts.Owner(run);
        if (Shortcuts.Begins(run))
        {
            _typedKeys = run;
            _keyWait ??= new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = KeyWait };
            _keyWait.Tick -= OnKeyWaitOver;
            _keyWait.Tick += OnKeyWaitOver;
            _keyWait.Start();
            var longer = Shortcuts.Entries(ShortcutCommands).Where(entry => entry.Keys.Any(keys => keys.StartsWith(run, StringComparison.Ordinal) && keys != run))
                .Take(4).Select(entry => $"{entry.Keys.First(keys => keys.StartsWith(run, StringComparison.Ordinal))} {entry.CommandName}");
            StatusHint.Text = $"{run}… {(exact is null ? string.Empty : $"{ShortcutName(exact)}, or ")}{string.Join(", ", longer)}";
            return true;
        }

        ForgetTypedKeys();
        return exact is not null && RunShortcut(exact, run);
    }

    private void OnKeyWaitOver(object? sender, EventArgs e) => FinishTypedKeys();

    /// <summary>The run typed so far, run as it is: the moment it waited for the rest is over. Internal so tests need not wait.</summary>
    internal void FinishTypedKeys()
    {
        _keyWait?.Stop();
        var run = _typedKeys;
        ForgetTypedKeys();
        if (Shortcuts.Owner(run) is { } owner) RunShortcut(owner, run);
        else StatusHint.Text = $"{run}: no command has these keys. View → Keyboard Shortcuts sets them.";
    }

    private void ForgetTypedKeys()
    {
        _keyWait?.Stop();
        _typedKeys = string.Empty;
    }

    private string ShortcutName(string id) => ShortcutCommands.FirstOrDefault(command => command.Id == id)?.Name ?? id;

    /// <summary>A command run by its keys - or why not: a drawing tool with the plan out of sight, a button that is off.</summary>
    private bool RunShortcut(string id, string keys)
    {
        if (ShortcutCommands.FirstOrDefault(command => command.Id == id) is not { } command) return false;
        if (command.NeedsPlan && (ModelCoversPlan || SheetPanel.Visibility == Visibility.Visible))
        {
            StatusHint.Text = $"{keys}: {command.Name} works on the plan - close the {(SheetPanel.Visibility == Visibility.Visible ? "sheet" : "3D view")} first.";
            return true;
        }

        if (!command.CanRun)
        {
            StatusHint.Text = $"{keys}: {command.Name} cannot be used just now.";
            return true;
        }

        command.Run();
        return true;
    }
}
