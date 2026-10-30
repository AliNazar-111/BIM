using System.Windows.Input;
using BIMDesigner.Infrastructure.Projects;

namespace BIMDesigner.UI;

/// <summary>
/// A command keys can be set for: a ribbon button or tool, by its id - its name, or where it sits
/// on the ribbon - what it is called, where it is, and what pressing it does.
/// </summary>
public sealed class ShortcutCommand
{
    private readonly Action _run;
    private readonly Func<bool> _can;

    public ShortcutCommand(string id, string name, string path, Action run, Func<bool> can, bool needsPlan)
    {
        (Id, Name, Path, _run, _can, NeedsPlan) = (id, name, path, run, can, needsPlan);
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Where it is on the ribbon: "Architecture > Build".</summary>
    public string Path { get; }

    /// <summary>Whether it works on the plan - a drawing tool - and so only with the plan in front.</summary>
    public bool NeedsPlan { get; }

    public bool CanRun => _can();

    public void Run() => _run();

    public override string ToString() => Name;
}

/// <summary>
/// Keys as they are written and pressed: one to five letters typed one after another - W, WA - or
/// a key held with Ctrl, Alt or Shift - Ctrl+3, Shift+P - or a function key, F2.
/// </summary>
public static class KeyChords
{
    /// <summary>The longest run of letters a shortcut can be.</summary>
    public const int LongestRun = 5;

    /// <summary>The keys every program keeps for itself, and that this one does: never a command's.</summary>
    private static readonly HashSet<string> Kept = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl+Z", "Ctrl+Y", "Ctrl+N", "Ctrl+O", "Ctrl+S", "Ctrl+Shift+S", "Alt+F4"
    };

    /// <summary>A key as it is written: a letter, a figure, a function key; null for any other.</summary>
    public static string? NameOf(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => $"F{key - Key.F1 + 1}",
        _ => null
    };

    /// <summary>A key pressed with what is held, as written - "Ctrl+Shift+V", "F2"; null for one that cannot be a shortcut.</summary>
    public static string? Of(Key key, ModifierKeys held)
    {
        if (NameOf(key) is not { } name) return null;
        var parts = new List<string>();
        if (held.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (held.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (held.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(name);
        return string.Join("+", parts);
    }

    /// <summary>Whether keys are a run of letters typed one after another, not one key held with others.</summary>
    public static bool IsRun(string keys) => keys.Length is > 0 and <= LongestRun && keys.All(letter => letter is >= 'A' and <= 'Z');

    /// <summary>Keys as typed or read from a file, written the one way - "ctrl + shift + v" as "Ctrl+Shift+V", "wa" as "WA"; null where they are not keys.</summary>
    public static string? Tidy(string? text)
    {
        var written = (text ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        if (written.Length == 0) return null;
        if (!written.Contains('+')) return IsRun(written) || IsFunction(written) ? written : null;

        var parts = written.Split('+');
        var key = parts[^1];
        var held = parts[..^1].Select(part => part switch { "CTRL" or "CONTROL" => "Ctrl", "ALT" => "Alt", "SHIFT" => "Shift", _ => null }).ToList();
        if (held.Any(part => part is null) || held.Distinct().Count() != held.Count) return null;
        if (!(key.Length == 1 && char.IsLetterOrDigit(key[0]) || IsFunction(key))) return null;
        return string.Join("+", new[] { "Ctrl", "Alt", "Shift" }.Where(held.Contains).Append(key));
    }

    private static bool IsFunction(string key) => key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24;

    /// <summary>Why keys cannot be a command's, or null where they can.</summary>
    public static string? Refusal(string keys)
    {
        if (Kept.Contains(keys)) return $"{keys} is kept for {(keys == "Alt+F4" ? "closing the window" : "undo, redo, new, open and save")}.";
        if (keys.StartsWith("Shift+", StringComparison.Ordinal) && keys.Length == 7 && char.IsDigit(keys[^1])) return $"{keys} types a character in a length.";
        return null;
    }
}

/// <summary>
/// Which keys run which command: each command's own, any number of them, no keys on two commands.
/// Starts from the defaults and takes what has been set over them.
/// </summary>
public sealed class ShortcutMap
{
    private readonly Dictionary<string, List<string>> _keys = new(StringComparer.Ordinal);

    /// <summary>The keys each command starts with: the single letters, function keys and Ctrl keys they had before any were set.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Defaults = new Dictionary<string, string[]>
    {
        ["SelectTool"] = new[] { "S" }, ["WallTool"] = new[] { "W" }, ["DoorTool"] = new[] { "D" }, ["WindowTool"] = new[] { "N" },
        ["RoomTool"] = new[] { "R" }, ["GridTool"] = new[] { "G" }, ["SectionTool"] = new[] { "C" }, ["DimensionTool"] = new[] { "M" },
        ["TagTool"] = new[] { "A" }, ["TextTool"] = new[] { "E" }, ["FloorTool"] = new[] { "F2" }, ["CeilingTool"] = new[] { "F3" },
        ["RoofTool"] = new[] { "F4" }, ["SplitTool"] = new[] { "X" }, ["TrimTool"] = new[] { "T" }, ["OffsetTool"] = new[] { "O" },
        ["AlignTool"] = new[] { "L" }, ["MirrorTool"] = new[] { "I" }, ["ArrayTool"] = new[] { "Y" }, ["RotateTool"] = new[] { "Q" },
        ["ScaleTool"] = new[] { "K" },
        ["Modify>Modify>Pin"] = new[] { "P" }, ["Modify>Modify>Unpin"] = new[] { "Shift+P" },
        ["Modify>Clipboard>Copy"] = new[] { "Ctrl+C" }, ["Modify>Clipboard>Paste"] = new[] { "Ctrl+V" },
        ["Modify>Clipboard>Paste in Place"] = new[] { "Ctrl+Shift+V" }, ["Modify>Clipboard>Select All"] = new[] { "Ctrl+A" },
        ["View>Windows>Zoom to Fit"] = new[] { "F" }, ["ModelMenuItem"] = new[] { "Ctrl+3" }, ["ScheduleMenuItem"] = new[] { "Ctrl+T" },
        ["SheetMenuItem"] = new[] { "Ctrl+H" }, ["Manage>Export>Print"] = new[] { "Ctrl+P" }
    };

    /// <summary>A map with every command's default keys.</summary>
    public static ShortcutMap Starting(IEnumerable<ShortcutCommand> commands)
    {
        var map = new ShortcutMap();
        foreach (var command in commands)
            if (Defaults.TryGetValue(command.Id, out var keys))
                map._keys[command.Id] = keys.ToList();
        return map;
    }

    public ShortcutMap Copy()
    {
        var copy = new ShortcutMap();
        foreach (var (id, keys) in _keys) copy._keys[id] = keys.ToList();
        return copy;
    }

    /// <summary>A command's keys.</summary>
    public IReadOnlyList<string> KeysOf(string id) => _keys.TryGetValue(id, out var keys) ? keys : Array.Empty<string>();

    /// <summary>The command keys run, or null.</summary>
    public string? Owner(string keys) => _keys.FirstOrDefault(entry => entry.Value.Contains(keys, StringComparer.Ordinal)).Key;

    /// <summary>Whether a run of letters begins a longer one some command has.</summary>
    public bool Begins(string run) => _keys.Values.SelectMany(keys => keys).Any(keys => keys.Length > run.Length && KeyChords.IsRun(keys) && keys.StartsWith(run, StringComparison.Ordinal));

    /// <summary>Keys given to a command - taken off the command that had them, whose id is handed back.</summary>
    public string? Assign(string id, string keys)
    {
        var from = Owner(keys);
        if (from == id) return null;
        if (from is not null) _keys[from].Remove(keys);
        if (!_keys.TryGetValue(id, out var own)) _keys[id] = own = new List<string>();
        own.Add(keys);
        return from;
    }

    public void Remove(string id, string keys)
    {
        if (_keys.TryGetValue(id, out var own)) own.Remove(keys);
    }

    /// <summary>
    /// Shortcuts read from a file put over these: each command found by its id, or failing that by
    /// its name - so a file another program wrote finds the commands this one has of the same name.
    /// How many entries found no command here.
    /// </summary>
    public int Take(IEnumerable<ShortcutEntry> entries, IReadOnlyList<ShortcutCommand> commands)
    {
        var lost = 0;
        var byId = commands.ToDictionary(command => command.Id, StringComparer.Ordinal);
        var byName = commands.GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var command = byId.GetValueOrDefault(entry.CommandId) ?? byName.GetValueOrDefault(entry.CommandName);
            if (command is null)
            {
                if (entry.Keys.Count > 0) lost++;
                continue;
            }

            if (_keys.TryGetValue(command.Id, out var had)) had.Clear();
            foreach (var keys in entry.Keys.Select(KeyChords.Tidy).OfType<string>().Where(keys => KeyChords.Refusal(keys) is null))
                Assign(command.Id, keys);
        }

        return lost;
    }

    /// <summary>Every command with its keys, as a file keeps them.</summary>
    public IEnumerable<ShortcutEntry> Entries(IEnumerable<ShortcutCommand> commands) =>
        commands.Select(command => new ShortcutEntry(command.Id, command.Name, command.Path, KeysOf(command.Id).ToList()));
}
