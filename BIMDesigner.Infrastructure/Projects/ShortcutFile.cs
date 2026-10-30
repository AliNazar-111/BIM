using System.Xml;
using System.Xml.Linq;

namespace BIMDesigner.Infrastructure.Projects;

/// <summary>One command's keys as a file keeps them: what it is called, its id, its keys, and where it is on the ribbon.</summary>
public sealed record ShortcutEntry(string CommandId, string CommandName, string Path, IReadOnlyList<string> Keys);

/// <summary>
/// The keyboard shortcuts someone has set, kept for them - not with a project, so they are theirs
/// whatever they open - in the layout other BIM programs export theirs in: a ShortcutItem for each
/// command, its keys joined by #. The same file is what Export writes and Import reads, so an
/// office can hand its shortcuts round. A file that cannot be read is left alone and the defaults used.
/// </summary>
public static class ShortcutFile
{
    public static string Folder { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMDesigner");

    /// <summary>Where the shortcuts set are kept.</summary>
    public static string Kept => System.IO.Path.Combine(Folder, "Keyboard Shortcuts.xml");

    /// <summary>The shortcuts kept, or null where none have been set or the file cannot be read.</summary>
    public static IReadOnlyList<ShortcutEntry>? Load() => File.Exists(Kept) ? Read(Kept) : null;

    /// <summary>The shortcuts kept from now on.</summary>
    public static void Save(IEnumerable<ShortcutEntry> entries)
    {
        Directory.CreateDirectory(Folder);
        Write(Kept, entries);
    }

    /// <summary>The shortcuts in a file, or null where it is not one.</summary>
    public static IReadOnlyList<ShortcutEntry>? Read(string path)
    {
        try
        {
            var root = XDocument.Load(path).Root;
            if (root is null || root.Name.LocalName != "Shortcuts") return null;
            return root.Elements().Where(item => item.Name.LocalName == "ShortcutItem")
                .Select(item => new ShortcutEntry(
                    (string?)item.Attribute("CommandId") ?? string.Empty,
                    (string?)item.Attribute("CommandName") ?? string.Empty,
                    (string?)item.Attribute("Paths") ?? string.Empty,
                    ((string?)item.Attribute("Shortcuts") ?? string.Empty).Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                .Where(entry => entry.CommandId.Length > 0 || entry.CommandName.Length > 0)
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    /// <summary>Shortcuts written to a file: every command, those with no keys too, so a key taken off one stays off.</summary>
    public static void Write(string path, IEnumerable<ShortcutEntry> entries)
    {
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("Shortcuts",
                entries.Select(entry => new XElement("ShortcutItem",
                    new XAttribute("CommandName", entry.CommandName),
                    new XAttribute("CommandId", entry.CommandId),
                    new XAttribute("Shortcuts", string.Join("#", entry.Keys)),
                    new XAttribute("Paths", entry.Path)))));
        var temporary = path + ".tmp";
        document.Save(temporary);
        File.Move(temporary, path, overwrite: true);
    }
}
