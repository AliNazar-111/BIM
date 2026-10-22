using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Infrastructure.Diagnostics;

namespace BIMDesigner.UI;

/// <summary>
/// What is shown when something goes wrong: what happened, in words; whether the work is safe;
/// and the details, to copy and send on. Carry On goes back to work. Close BIMDesigner closes it
/// as it always closes, asking about saving first.
/// </summary>
public sealed class ErrorWindow : System.Windows.Window
{
    private readonly TextBlock _problem = new();

    public ErrorWindow(string said, string safe, string details)
    {
        DialogLook.Apply(this, "BIMDesigner - something went wrong", 640, 440);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Said = said;
        Safe = safe;
        Details = details;

        var copy = new Button { Content = "Copy Details" };
        copy.Click += (_, _) => CopyDetails();
        var logs = new Button { Content = "Open Log Folder" };
        logs.Click += (_, _) => OpenLogFolder();
        var close = new Button { Content = "Close BIMDesigner" };
        close.Click += (_, _) =>
        {
            CloseApp = true;
            DialogResult = true;
        };
        var carryOn = new Button { Content = "Carry On", IsDefault = true, IsCancel = true };

        var heading = new TextBlock { Text = "Something went wrong", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 14, 16, 8) };
        DockPanel.SetDock(heading, Dock.Top);
        var body = new TextBlock { Text = said, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 8) };
        DockPanel.SetDock(body, Dock.Top);
        var safety = new TextBlock { Text = safe, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 12), FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(safety, Dock.Top);

        var text = new TextBox
        {
            Text = details, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var expander = new Expander { Header = "Details", Content = text, Margin = new Thickness(16, 0, 16, 12) };

        var dock = new DockPanel();
        dock.Children.Add(heading);
        dock.Children.Add(body);
        dock.Children.Add(safety);
        dock.Children.Add(DialogLook.Footer(_problem, copy, logs, close, carryOn));
        dock.Children.Add(expander);
        Content = dock;
    }

    /// <summary>What went wrong, in words.</summary>
    public string Said { get; }

    /// <summary>Whether the work is safe.</summary>
    public string Safe { get; }

    /// <summary>The whole report, to copy and send on.</summary>
    public string Details { get; }

    /// <summary>Whether Close BIMDesigner was chosen rather than Carry On.</summary>
    public bool CloseApp { get; private set; }

    private void CopyDetails()
    {
        try
        {
            Clipboard.SetText(Details);
            _problem.Text = "Copied - paste it into an email or a message.";
        }
        catch (COMException)
        {
            // Another program has the clipboard open.
            _problem.Text = "The clipboard is busy - try again.";
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.Folder);
            Process.Start(new ProcessStartInfo { FileName = AppLog.Folder, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _problem.Text = $"The logs are in {AppLog.Folder}";
        }
    }
}
