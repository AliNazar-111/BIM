using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Diagnostics;

namespace BIMDesigner.UI;

/// <summary>
/// What happens when something goes wrong: it is written to the log, unsaved work is kept in a
/// recovery copy, and it is said - in words, with the details to copy - and then work goes on.
/// Nothing that goes wrong while working closes BIMDesigner by itself.
///
/// Something going wrong at every repaint would otherwise bring the window up again and again
/// and never let anyone work: the same failure is shown once a minute at most, and logged in
/// full only the first few times.
/// </summary>
public static class ErrorReporting
{
    // Each kind of failure - its type and where it was thrown - how often it has happened, and when it was last shown.
    private static readonly Dictionary<string, (int Count, DateTime Shown)> Seen = new();
    private static readonly HashSet<Guid> NotDrawn = new();
    private static bool _showing;

    /// <summary>Takes in whatever goes wrong from here on: on the window's thread, on others, and in drawing.</summary>
    public static void Attach(Application application)
    {
        application.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Stopping(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Something done in the background went wrong.", e.Exception);
            e.SetObserved();
        };
        DrawFailures.Failed += NotDrawable;

        AppLog.Info($"BIMDesigner {AppLog.Version} started, on Windows {Environment.OSVersion.Version}.");
    }

    /// <summary>Logs what went wrong, keeps the work, and says so.</summary>
    public static void Report(Exception exception)
    {
        var key = Signature(exception);
        var (count, shown) = Seen.GetValueOrDefault(key);
        Seen[key] = (++count, shown);
        if (count <= 3) AppLog.Error(count == 1 ? "Something went wrong." : $"The same went wrong again ({count} times now).", exception);
        else if (count % 50 == 0) AppLog.Error($"The same went wrong again ({count} times now): {exception.GetType().Name}: {exception.Message}");

        var main = Application.Current?.MainWindow as MainWindow;
        var kept = Keep(main);

        if (_showing || DateTime.Now - shown < TimeSpan.FromMinutes(1))
        {
            main?.Tell($"{Said(exception)} (again - it is in the log)");
            return;
        }

        Seen[key] = (count, DateTime.Now);
        _showing = true;
        try
        {
            var window = new ErrorWindow(Said(exception), Safe(main, kept), AppLog.Report(exception));
            if (main is { IsLoaded: true, IsVisible: true }) window.Owner = main;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowDialog();

            if (!window.CloseApp) return;
            if (main is not null) main.Close();
            else Application.Current?.Shutdown(1);
        }
        catch (Exception secondary)
        {
            AppLog.Error("What went wrong could not be shown.", secondary);
        }
        finally
        {
            _showing = false;
        }
    }

    /// <summary>What went wrong, as it is said: an edit that could not be done and was taken back, or something unexpected.</summary>
    public static string Said(Exception exception) => exception switch
    {
        EditFailedException failed =>
            $"{failed.Edit} could not be done, so what it had begun has been taken back: the model is as it was before it. ({Innermost(failed).Message})",
        _ => $"BIMDesigner ran into a problem it did not expect, and stopped what it was doing. ({Innermost(exception).Message})"
    };

    /// <summary>Whether the work is safe.</summary>
    public static string Safe(MainWindow? main, bool kept) => main switch
    {
        null => "Nothing had been opened yet, so no work has been lost.",
        { HasUnsavedWork: false } => "There are no unsaved changes, so nothing has been lost.",
        _ when kept => "A recovery copy of your unsaved work has been kept, so it is safe even if BIMDesigner has to close. Save when you can.",
        _ => "A recovery copy could not be kept. Save your work as soon as you can."
    };

    /// <summary>
    /// BIMDesigner is stopping - something went wrong on another thread that nothing can carry on
    /// from. All that can be done is to log it and keep the work, so it is offered back next time.
    /// </summary>
    private static void Stopping(Exception? exception)
    {
        AppLog.Error("BIMDesigner is stopping: something went wrong that it could not carry on from.", exception);
        try
        {
            Application.Current?.Dispatcher.Invoke(() => Keep(Application.Current.MainWindow as MainWindow),
                DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(10));
        }
        catch (Exception secondary)
        {
            AppLog.Error("The work could not be kept as BIMDesigner stopped.", secondary);
        }
    }

    private static bool Keep(MainWindow? main)
    {
        try
        {
            return main?.KeepWork(now: true) ?? false;
        }
        catch (Exception exception)
        {
            AppLog.Error("A recovery copy could not be kept after something went wrong.", exception);
            return false;
        }
    }

    /// <summary>One element could not be drawn and is left out: logged once, and said on the status bar.</summary>
    private static void NotDrawable(Element element, Exception exception)
    {
        var what = Regex.Replace(element.Category.ToString(), "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        if (NotDrawn.Add(element.Id)) AppLog.Error($"One of the {what} ({element.Id}) could not be drawn, and is left out.", exception);

        // Not while drawing: the status bar is told once the drawing is done.
        if (Application.Current?.MainWindow is MainWindow main)
            main.Dispatcher.BeginInvoke(() => main.Tell($"One of the {what} could not be drawn, and is left out of the drawing. What went wrong is in the log."));
    }

    private static string Signature(Exception exception) =>
        $"{Innermost(exception).GetType().FullName}|{Innermost(exception).StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is { } inner) exception = inner;
        return exception;
    }
}
