using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BIMDesigner.Infrastructure.Projects;

/// <summary>A project opened or saved before: where it is, what it is called, when it was last opened, and whether it is kept at the top of the list.</summary>
public sealed record RecentProject(string Path, string Name, DateTime Opened, bool Pinned)
{
    /// <summary>Whether the file is still where it was.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Exists => File.Exists(Path);

    /// <summary>A picture of the project as it was last seen, where one was kept.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Thumbnail => RecentProjects.ThumbnailOf(Path) is var file && File.Exists(file) ? file : null;
}

/// <summary>
/// The projects opened and saved, most recent first and the pinned ones above them, for the
/// home screen and File's Open Recent: kept in a small file of its own beside a picture of each.
/// A list that cannot be read is started again rather than stopping anything.
/// </summary>
public static class RecentProjects
{
    /// <summary>How many are kept; pinned ones are always kept.</summary>
    public const int Most = 25;

    public static string Folder { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMDesigner");

    private static string ListFile => System.IO.Path.Combine(Folder, "Recent Projects.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Where a project's picture is kept.</summary>
    public static string ThumbnailOf(string path)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Full(path).ToLowerInvariant())))[..16];
        return System.IO.Path.Combine(Full(Folder), "Thumbnails", key + ".png");
    }

    private static string Full(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>The list: pinned first, then the most recently opened.</summary>
    public static IReadOnlyList<RecentProject> All() =>
        Read().OrderByDescending(project => project.Pinned).ThenByDescending(project => project.Opened).ToList();

    /// <summary>A project opened or saved now: at the top of the list, under its name as it now is.</summary>
    public static void Opened(string path, string name)
    {
        var full = Full(path);
        var list = Read();
        var pinned = list.FirstOrDefault(project => Same(project.Path, full))?.Pinned ?? false;
        list.RemoveAll(project => Same(project.Path, full));
        list.Insert(0, new RecentProject(full, string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(full) : name, DateTime.Now, pinned));

        // The oldest that are not pinned go, past the most kept.
        while (list.Count > Most && list.FindLastIndex(project => !project.Pinned) is var last and >= 0) list.RemoveAt(last);
        Write(list);
    }

    /// <summary>Kept at the top of the list, or not.</summary>
    public static void Pin(string path, bool pinned)
    {
        var list = Read();
        var index = list.FindIndex(project => Same(project.Path, path));
        if (index < 0) return;
        list[index] = list[index] with { Pinned = pinned };
        Write(list);
    }

    /// <summary>Taken off the list, with its picture.</summary>
    public static void Forget(string path)
    {
        var list = Read();
        if (list.RemoveAll(project => Same(project.Path, path)) == 0) return;
        Write(list);
        try
        {
            File.Delete(ThumbnailOf(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A project's picture kept, as PNG bytes, to show beside it on the list.</summary>
    public static void KeepThumbnail(string path, byte[] png)
    {
        try
        {
            var file = ThumbnailOf(path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, png);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    private static List<RecentProject> Read()
    {
        try
        {
            if (!File.Exists(ListFile)) return new List<RecentProject>();
            return JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(ListFile), Options)?
                .Where(project => !string.IsNullOrWhiteSpace(project.Path)).ToList() ?? new List<RecentProject>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new List<RecentProject>();
        }
    }

    private static void Write(List<RecentProject> list)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(ListFile, JsonSerializer.Serialize(list, Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
