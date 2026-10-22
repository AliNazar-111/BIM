using System.Reflection;

namespace BIMDesigner.Infrastructure.Diagnostics;

/// <summary>
/// What went wrong, and when, written down where it can be found and sent on: one file a day in
/// the user's local application data, the last fortnight of them kept.
///
/// Nothing here may itself go wrong in a way anyone sees. A log that cannot be written - a full
/// disk, a folder it may not write to - is not worth stopping anyone's work for.
/// </summary>
public static class AppLog
{
    private const int DaysKept = 14;

    private static readonly object Gate = new();
    private static bool _tidied;

    /// <summary>Where the logs are written.</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMDesigner", "Logs");

    /// <summary>Today's log.</summary>
    public static string CurrentFile => Path.Combine(Folder, $"BIMDesigner-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>This build of BIMDesigner, as the log and a report name it.</summary>
    public static string Version =>
        (Assembly.GetEntryAssembly() ?? typeof(AppLog).Assembly).GetName().Version?.ToString(3) ?? "unknown";

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>
    /// What went wrong as someone would send it on: this build, this Windows and .NET, when, what
    /// was being done, and the whole of the exception - each one inside it, and where it was thrown.
    /// </summary>
    public static string Report(Exception exception, string? doing = null) =>
        string.Join(Environment.NewLine,
            $"BIMDesigner {Version}",
            $"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            doing is null ? string.Empty : $"While: {doing}",
            string.Empty,
            exception.ToString());

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}";
                if (exception is not null) entry += Environment.NewLine + exception;
                File.AppendAllText(CurrentFile, entry + Environment.NewLine);

                if (_tidied) return;
                _tidied = true;
                Tidy();
            }
        }
        catch (Exception)
        {
            // Not written: see above.
        }
    }

    /// <summary>Logs older than a fortnight are deleted, once a session.</summary>
    private static void Tidy()
    {
        foreach (var file in Directory.EnumerateFiles(Folder, "BIMDesigner-*.log"))
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-DaysKept))
                File.Delete(file);
    }
}
