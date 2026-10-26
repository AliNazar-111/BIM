using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Diagnostics;

namespace BIMDesigner.Tests;

/// <summary>Thrown on purpose, by a test, to see what is done when something goes wrong.</summary>
public sealed class DeliberateFailure(string message = "Broken on purpose.") : Exception(message);

/// <summary>Set up once, before any test runs.</summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Start()
    {
        // The app leaves out an element that cannot be drawn and carries on. A test is to fail on
        // it instead, as it always did - unless the test broke the element on purpose.
        DrawFailures.Failed += (_, exception) =>
        {
            if (exception is not DeliberateFailure) ExceptionDispatchInfo.Capture(exception).Throw();
        };

        // Logs and recovery copies made by tests go somewhere of their own, never among the real ones.
        var scratch = Path.Combine(Path.GetTempPath(), "BIMDesigner.Tests", Guid.NewGuid().ToString("N"));
        AppLog.Folder = Path.Combine(scratch, "Logs");
        RecoverySession.Folder = Path.Combine(scratch, "Recovery");
        BIMDesigner.Infrastructure.Projects.RecentProjects.Folder = Path.Combine(scratch, "Recent");
    }
}
