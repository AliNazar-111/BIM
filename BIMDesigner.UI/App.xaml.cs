using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace BIMDesigner.UI;

public partial class App : Application
{
    /// <summary>
    /// The error Windows gives when an application control policy - Smart App Control, or a
    /// company's own policy - refuses to let a file load.
    /// </summary>
    private const int BlockedByPolicy = unchecked((int)0x800711C7);

    /// <summary>
    /// A project file named on the command line, so that <c>BIMDesigner.UI project.bimx</c>
    /// opens it. This is what a file association needs, and it makes a specific model
    /// reproducible - handy when a drawing problem has to be looked at rather than guessed at.
    /// </summary>
    public string? StartupProjectPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF starts an application as soon as its dispatcher runs, even one made only to host
        // windows - by the tests, or a walkthrough driving the window. Only BIMDesigner itself,
        // started as a program, shows the splash and the home screen.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly) return;

        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            StartupProjectPath = e.Args[0];

        // Checked before the main window exists, because the main window is built from these
        // libraries: if one cannot load, all that would otherwise happen is a crash with no
        // explanation.
        if (BlockedLibrary() is { } blocked)
        {
            MessageBox.Show(
                $"Windows blocked part of BIMDesigner from loading:\n\n{blocked}\n\n" +
                "This is Smart App Control, or an application control policy set by your organisation. " +
                "It stops programs Windows does not recognise, and a build made on this computer is " +
                "new and unsigned, so it can be stopped even though nothing is wrong with it.\n\n" +
                "If you build BIMDesigner yourself: turn Smart App Control off in Windows Security > " +
                "App & browser control > Smart App Control settings, then start it again.\n\n" +
                "If you installed a released copy: please report this - released copies are meant " +
                "to be signed so that Windows recognises them.",
                "BIMDesigner could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Shutdown(1);
            return;
        }

        // From here on, whatever goes wrong is logged and said, the work kept - not the program closed.
        ErrorReporting.Attach(this);

        // Light or dark, as last chosen: the palette has to be in place before any window is built.
        AppTheme.Apply(this);

        // Windows come and go - the splash, the home screen, a project, the home screen again - so
        // BIMDesigner stops when it is told to: the home screen closed, or a project closed other
        // than to go back home.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var splash = new StartUp.SplashWindow();
        splash.Show();
        var shown = Stopwatch.StartNew();
        splash.Step("Loading the model engine...", 0.15);
        _ = Core.Documents.BimDocument.CreateDefault();
        splash.Step("Preparing the templates...", 0.5);
        _ = Core.Documents.ProjectTemplates.Create(new Core.Documents.NewProjectSpec());
        splash.Step("Reading the recent projects...", 0.8);
        _ = Infrastructure.Projects.RecentProjects.All();
        splash.Step("Ready", 1);

        // Long enough to be read, not so long as to be waited for.
        while (shown.Elapsed < TimeSpan.FromSeconds(1.2))
        {
            Thread.Sleep(40);
            splash.Step("Ready", 1);
        }

        // A project opened from Explorer goes straight to its window; otherwise the home screen.
        if (StartupProjectPath is not null)
        {
            var window = Open(new MainWindow());
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => window.OfferRecovery());
        }
        else ShowHome();

        splash.Close();
    }

    /// <summary>The home screen: what is picked there opens in a project window, and closing it stops BIMDesigner.</summary>
    internal StartUp.StartWindow ShowHome()
    {
        var home = new StartUp.StartWindow();
        MainWindow = home;
        home.Chosen += (_, choice) =>
        {
            Open(new MainWindow { Start = choice });
            home.Close();
        };
        home.Closed += (_, _) =>
        {
            if (home.Choice is null) Shutdown();
        };
        home.Show();
        home.Activate();
        return home;
    }

    /// <summary>A project window shown, its unsaved work kept every few minutes; closed, BIMDesigner stops - or goes back to the home screen.</summary>
    private MainWindow Open(MainWindow window)
    {
        MainWindow = window;
        window.Closed += (_, _) =>
        {
            if (window.ClosingToHome) ShowHome();
            else Shutdown();
        };
        window.Show();
        window.StartKeepingWork();
        return window;
    }

    /// <summary>
    /// Loads the application's own libraries, and names the first one Windows refuses.
    /// Null when everything loaded.
    /// </summary>
    private static string? BlockedLibrary()
    {
        try
        {
            LoadLibraries();
            return null;
        }
        catch (Exception exception) when (FindBlock(exception) is { } block)
        {
            return block.FileName ?? block.Message;
        }
    }

    /// <summary>
    /// Kept out of line so that the libraries load here, inside the check, rather than when
    /// the method that calls it is compiled.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadLibraries()
    {
        _ = typeof(Core.Documents.BimDocument).Assembly;
        _ = typeof(Infrastructure.Serialization.ProjectFile).Assembly;
        _ = typeof(Infrastructure.Interoperability.IfcExport).Assembly;
    }

    private static FileLoadException? FindBlock(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
            if (exception is FileLoadException { HResult: BlockedByPolicy } load) return load;

        return null;
    }
}
