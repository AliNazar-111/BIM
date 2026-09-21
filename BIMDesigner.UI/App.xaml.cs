using System.IO;
using System.Windows;

namespace BIMDesigner.UI;

public partial class App : Application
{
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
    }
}
