using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

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

        new MainWindow().Show();
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
