using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Projects;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI.StartUp;

namespace BIMDesigner.UI;

/// <summary>
/// The project window's side of starting and stopping: the project the home screen or the New
/// Project steps hand it, File's New Project, Open Recent and Close Project - which goes back to
/// the home screen - and the recent projects kept up to date, with a picture of each as last seen.
/// </summary>
public partial class MainWindow
{
    /// <summary>What to open when the window is shown: from the home screen. Null opens as before - a file named on the command line, or the example.</summary>
    internal StartChoice? Start { get; set; }

    /// <summary>Whether the window is closing to go back to the home screen, rather than to stop BIMDesigner.</summary>
    internal bool ClosingToHome { get; private set; }

    /// <summary>A project from the home screen or the New Project steps opened: a new one saved where it was asked to be.</summary>
    internal void Begin(StartChoice choice)
    {
        LoadDocument(choice.Document, choice.Path, choice.Example);
        if (choice.Unsaved) _history.MarkModified();
        if (choice.SaveTo is { } saveTo)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(saveTo)!);
                WriteTo(saveTo);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Infrastructure.Diagnostics.AppLog.Warning($"Could not make the folder for {saveTo}.", exception);
                MessageBox.Show(this, $"The project could not be saved in {Path.GetDirectoryName(saveTo)}: {exception.Message}\n\nIt is open, not yet saved - File > Save As keeps it somewhere else.",
                    "New project", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        StatusHint.Text = choice.Said;
        RefreshTitle();
        if (choice.Notes.Count > 0 && IsVisible)
            MessageBox.Show(this,
                $"{choice.Said}\n\n" + string.Join("\n", choice.Notes.Take(15).Select(note => "• " + note)) + (choice.Notes.Count > 15 ? $"\n... and {choice.Notes.Count - 15} more." : string.Empty),
                "Open IFC", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>A new project from the template, with nothing in it and no steps asked: what the walkthroughs start from.</summary>
    internal void StartEmptyProject() => LoadDocument(BimDocument.CreateDefault(), path: null, seedExample: false);

    /// <summary>Closes the project - asking about unsaved work first - and goes back to the home screen. False where it was not closed.</summary>
    internal bool CloseProject()
    {
        var closed = false;
        void Shut(object? sender, EventArgs e) => closed = true;
        ClosingToHome = true;
        Closed += Shut;
        Close();
        Closed -= Shut;
        if (!closed) ClosingToHome = false;
        return closed;
    }

    private void OnCloseProject(object sender, RoutedEventArgs e) => CloseProject();

    /// <summary>File > Open Recent: the projects opened before, newest first, the pinned above them.</summary>
    private void OnRecentOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        menu.Items.Clear();
        var recent = RecentProjects.All().Take(12).ToList();
        if (recent.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No recent projects", IsEnabled = false });
            return;
        }

        for (var i = 0; i < recent.Count; i++)
        {
            var project = recent[i];
            var item = new MenuItem
            {
                Header = $"_{(i + 1) % 10} {project.Name}{(project.Pinned ? "  (pinned)" : string.Empty)}", ToolTip = project.Path, IsEnabled = project.Exists,
                InputGestureText = project.Exists ? Path.GetFileName(project.Path) : "not found"
            };
            item.Click += (_, _) => OpenRecentProject(project.Path);
            menu.Items.Add(item);
        }
    }

    private void OpenRecentProject(string path)
    {
        if (!ConfirmDiscardingChanges()) return;
        try
        {
            LoadDocument(ProjectFile.Load(path), path, seedExample: false);
            StatusHint.Text = $"Opened {Path.GetFileName(path)}.";
        }
        catch (Exception exception) when (exception is ProjectFileException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Could not open project", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>A project opened or saved goes to the top of the recent list - and its picture is taken once it is drawn.</summary>
    private void Remember()
    {
        if (_path is null) return;
        RecentProjects.Opened(_path, _document.ProjectInformation.Name);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, KeepThumbnail);
    }

    /// <summary>The picture of the project on the home screen: the plan as it is now, small.</summary>
    internal void KeepThumbnail()
    {
        if (_path is null || !Plan.IsVisible || Plan.ActualWidth < 50 || Plan.ActualHeight < 50) return;
        try
        {
            const int width = 320;
            var height = (int)Math.Clamp(width * Plan.ActualHeight / Plan.ActualWidth, 120, 260);
            var picture = new DrawingVisual();
            using (var drawing = picture.RenderOpen())
            {
                drawing.DrawRectangle((Brush)FindResource("Bg.Field"), null, new Rect(0, 0, width, height));
                drawing.DrawRectangle(new VisualBrush(Plan) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(picture);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            RecentProjects.KeepThumbnail(_path, stream.ToArray());
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Infrastructure.Diagnostics.AppLog.Warning("Could not keep the project's picture.", exception);
        }
    }
}
