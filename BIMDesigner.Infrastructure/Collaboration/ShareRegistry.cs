using System.Text.Json;

namespace BIMDesigner.Infrastructure.Collaboration;

/// <summary>One synchronisation with the central model: its issue number, who, when, what they said, and how much went in.</summary>
public sealed class SyncRecord
{
    public int Version { get; set; }
    public string User { get; set; } = string.Empty;
    public DateTime When { get; set; }
    public string Comment { get; set; } = string.Empty;
    public int Changed { get; set; }
}

/// <summary>One person asking another for something they have: what, its name, who asks, who has it, when, and how it was answered.</summary>
public sealed class EditRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid Item { get; set; }
    public string Workset { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public DateTime When { get; set; }

    /// <summary>Empty while waiting; "Granted" or "Denied" once answered.</summary>
    public string Answer { get; set; } = string.Empty;
}

/// <summary>
/// What is kept beside a central model for the people sharing it: who has borrowed which elements
/// and made which worksets their own, the issue the central is at and the issue each element was
/// last changed in, each synchronisation, and the requests made of one another. One file, read and
/// written only while a lock beside it is held, so two people's borrowing never cross.
/// </summary>
public sealed class ShareRegistry
{
    public int Version { get; set; } = 1;

    /// <summary>Each element, type, material or level borrowed, and by whom.</summary>
    public Dictionary<Guid, string> Owners { get; set; } = new();

    /// <summary>Each workset made someone's own, and whose.</summary>
    public Dictionary<string, string> WorksetOwners { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The issue each element was last changed in.</summary>
    public Dictionary<Guid, int> Versions { get; set; } = new();

    public List<SyncRecord> History { get; set; } = new();

    public List<EditRequest> Requests { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The folder kept beside a central model: "(central).share".</summary>
    public static string FolderOf(string central) => central + ".share";

    private static string FileOf(string central) => Path.Combine(FolderOf(central), "registry.json");

    /// <summary>The registry as it now is; a new one where there is none yet.</summary>
    public static ShareRegistry Read(string central)
    {
        var file = FileOf(central);
        if (!File.Exists(file)) return new ShareRegistry();
        var registry = JsonSerializer.Deserialize<ShareRegistry>(File.ReadAllText(file), Options) ?? new ShareRegistry();
        registry.WorksetOwners = new Dictionary<string, string>(registry.WorksetOwners, StringComparer.OrdinalIgnoreCase);
        return registry;
    }

    public void Write(string central)
    {
        Directory.CreateDirectory(FolderOf(central));
        var file = FileOf(central);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>
    /// The lock beside a central model, held until disposed: waited for while someone else holds it,
    /// up to a while; then why not.
    /// </summary>
    public static FileStream Lock(string central, TimeSpan? wait = null)
    {
        Directory.CreateDirectory(FolderOf(central));
        var path = Path.Combine(FolderOf(central), "lock");
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(40);
            }
            catch (IOException error)
            {
                throw new IOException("Someone else is synchronising with the central model: try again in a moment.", error);
            }
        }
    }

    /// <summary>Who has an item: whoever borrowed it, or whoever has made its workset their own; null for nobody.</summary>
    public string? OwnerOf(Guid item, string workset) =>
        Owners.TryGetValue(item, out var owner) ? owner : WorksetOwners.TryGetValue(workset, out var whole) ? whole : null;

    /// <summary>Everything a person has, let go.</summary>
    public int Relinquish(string user)
    {
        var mine = Owners.Where(entry => entry.Value == user).Select(entry => entry.Key).ToList();
        foreach (var id in mine) Owners.Remove(id);
        var worksets = WorksetOwners.Where(entry => entry.Value == user).Select(entry => entry.Key).ToList();
        foreach (var name in worksets) WorksetOwners.Remove(name);
        return mine.Count + worksets.Count;
    }
}
