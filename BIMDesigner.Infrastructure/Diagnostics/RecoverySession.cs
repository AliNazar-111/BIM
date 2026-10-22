using System.Text.Json;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Infrastructure.Diagnostics;

/// <summary>Work a session kept and never saved: the copy, what it had been saved as if anything, its name and when it was kept.</summary>
public sealed record RecoveredWork(string File, string? SavedAs, string Name, DateTime Kept);

/// <summary>
/// A copy of the work in hand, kept every few minutes while there are changes not saved, so that
/// if BIMDesigner stops - a crash, a power cut, Windows restarting for an update - the work does
/// not go with it.
///
/// Each session keeps its copy under an id of its own, beside a lock file it holds open for as
/// long as it runs and Windows deletes when it ends, however it ends. Saving the project, or
/// closing it, throws the copy away. A copy whose lock nobody holds belonged to a session that
/// stopped without doing either: that is the work to offer back at the next start. A copy whose
/// lock is held is another BIMDesigner's, still running, and is left alone.
/// </summary>
public sealed class RecoverySession : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private FileStream? _lock;

    /// <summary>Where copies are kept.</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMDesigner", "Recovery");

    private string Copy => Path.Combine(Folder, _id + ProjectFile.Extension);

    private string Note => Path.Combine(Folder, _id + ".json");

    /// <summary>Whether this session has a copy kept just now.</summary>
    public bool HasCopy => File.Exists(Copy);

    /// <summary>Keeps a copy of the project as it now is. False - and logged - where it could not be written.</summary>
    public bool Keep(BimDocument document, string? savedAs)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            _lock ??= new FileStream(Path.Combine(Folder, _id + ".lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            ProjectFile.Save(document, Copy);
            File.WriteAllText(Note, JsonSerializer.Serialize(new About(savedAs, document.ProjectInformation.Name, DateTime.Now)));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Could not keep a recovery copy of the work in hand.", exception);
            return false;
        }
    }

    /// <summary>Throws the copy away: the work is saved, or not wanted.</summary>
    public void Discard()
    {
        Delete(Copy);
        Delete(Note);
    }

    /// <summary>The session ends as it should: its copy thrown away and its lock let go.</summary>
    public void Dispose()
    {
        Discard();
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>Work kept by sessions that stopped without saving or closing it, the most recent first.</summary>
    public static IReadOnlyList<RecoveredWork> Left()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<RecoveredWork>();

        var left = new List<RecoveredWork>();
        foreach (var note in Directory.EnumerateFiles(Folder, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(note);
            var copy = Path.Combine(Folder, id + ProjectFile.Extension);
            if (!File.Exists(copy) || IsRunning(id)) continue;

            try
            {
                if (JsonSerializer.Deserialize<About>(File.ReadAllText(note)) is { } about)
                    left.Add(new RecoveredWork(copy, about.SavedAs, about.Name, about.Kept));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Warning($"Could not read the note kept with {copy}.", exception);
            }
        }

        return left.OrderByDescending(work => work.Kept).ToList();
    }

    /// <summary>Throws away work left by a session that stopped: brought back, or not wanted.</summary>
    public static void Forget(RecoveredWork work)
    {
        Delete(work.File);
        Delete(Path.ChangeExtension(work.File, ".json"));
    }

    /// <summary>Whether the session that kept a copy is still running: it holds its lock open, so nothing else can open it.</summary>
    private static bool IsRunning(string id)
    {
        var path = Path.Combine(Folder, id + ".lock");
        if (!File.Exists(path)) return false;

        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(path);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Could not delete {path}.", exception);
        }
    }

    private sealed record About(string? SavedAs, string Name, DateTime Kept);
}
