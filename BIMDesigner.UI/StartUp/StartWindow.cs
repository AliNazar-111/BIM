using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Diagnostics;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Projects;
using BIMDesigner.Infrastructure.Serialization;
using Microsoft.Win32;

namespace BIMDesigner.UI.StartUp;

/// <summary>
/// The home screen, shown when BIMDesigner starts and when a project is closed: the projects
/// opened before, newest first and the pinned ones above them, with a picture of each and a
/// search; and what to start from - a new project step by step, a project file, an IFC file
/// from another program, the example house, or work brought back after BIMDesigner stopped.
/// What is picked is read here, so a file that will not open is said here and the home screen stays.
/// </summary>
public sealed class StartWindow : Window
{
    private readonly ContentControl _content = new();
    private readonly StackPanel _recent = new();
    private readonly TextBox _search = new() { Padding = new Thickness(8, 6, 8, 6) };
    private readonly Grid _home;
    private NewProjectSteps? _steps;

    public StartWindow()
    {
        Title = "BIMDesigner";
        Icon = Logo.Image(32);
        Width = 1180;
        Height = 760;
        MinWidth = 940;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg.Window");
        SetResourceReference(ForegroundProperty, "Text");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(FontSizeProperty, "UiFontSize");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;

        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width - 24);
        Height = Math.Min(Height, work.Height - 24);

        _search.TextChanged += (_, _) => FillRecent();
        _home = Home();
        _content.Content = _home;
        Content = _content;
        FillRecent();

        // Ctrl+N and Ctrl+O as in the project window.
        InputBindings.Add(new KeyBinding(new Act(() => ShowNewProject()), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Act(PickProject), Key.O, ModifierKeys.Control));
    }

    /// <summary>The project picked, read and ready to open - null until one is.</summary>
    public StartChoice? Choice { get; private set; }

    /// <summary>A project picked: the window that opens it is to be shown.</summary>
    public event EventHandler<StartChoice>? Chosen;

    /// <summary>The New Project steps, where they are shown.</summary>
    public NewProjectSteps? Steps => _steps;

    /// <summary>The recent projects as listed, after the search.</summary>
    public IReadOnlyList<RecentProject> Listed { get; private set; } = Array.Empty<RecentProject>();

    // ---- the home screen ----------------------------------------------------------------

    private Grid Home()
    {
        var root = new Grid { Margin = new Thickness(40, 28, 40, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The name, and the version.
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 26) };
        if (Logo.Image(48) is { } logo) header.Children.Add(new Image { Source = logo, Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0) });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "BIMDesigner", FontSize = 26, FontWeight = FontWeights.SemiBold });
        names.Children.Add(Muted($"Version {MainWindow.AppVersion}"));
        header.Children.Add(names);
        root.Children.Add(header);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 320 });
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        // Open recent: a search, and the list.
        var recent = new DockPanel();
        var recentTitle = new TextBlock { Text = "Open recent", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(recentTitle, Dock.Top);
        recent.Children.Add(recentTitle);
        var search = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        var hint = Muted("Search recent projects");
        hint.Margin = new Thickness(10, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        search.Children.Add(_search);
        search.Children.Add(hint);
        DockPanel.SetDock(search, Dock.Top);
        recent.Children.Add(search);
        recent.Children.Add(new ScrollViewer { Content = _recent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        columns.Children.Add(recent);

        // Get started.
        var start = new StackPanel();
        start.Children.Add(new TextBlock { Text = "Get started", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        if (RecoverySession.Left() is [var left, ..])
        {
            var name = left.SavedAs is { } saved ? Path.GetFileName(saved) : left.Name;
            var recover = Tile("Icon.Undo", "Recover unsaved work",
                $"{name}, kept at {left.Kept:HH:mm} on {left.Kept:d MMMM} - BIMDesigner stopped without saving it.", () => Recover(left));
            recover.SetResourceReference(Control.BorderBrushProperty, "Accent");
            start.Children.Add(recover);
            Button? discard = null;
            discard = Link("Throw that work away", () =>
            {
                if (MessageBox.Show(this, $"Throw away the unsaved work on {name}? It cannot be brought back.", "Recover unsaved work", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                RecoverySession.Forget(left);
                start.Children.Remove(recover);
                start.Children.Remove(discard);
            });
            discard.Margin = new Thickness(4, -4, 0, 14);
            start.Children.Add(discard);
        }

        start.Children.Add(Tile("Icon.New", "New project", "A building from a template, step by step: the rules it is checked to, the site, the storeys and where it is kept.", () => ShowNewProject()));
        start.Children.Add(Tile("Icon.Open", "Open a project", "A BIMDesigner project (.bimx) on this computer or the network.", PickProject));
        start.Children.Add(Tile("Icon.View3D", "Open an IFC file", "A model from another program, read into a new project.", PickIfc));
        start.Children.Add(Tile("Icon.Wall", "Open the example house", "Walls already drawn, to look round and try the tools on.", OpenExample));
        var empty = Link("Or start an empty project, without the steps", StartEmpty);
        empty.Margin = new Thickness(4, 6, 0, 0);
        start.Children.Add(empty);
        Grid.SetColumn(start, 2);
        columns.Children.Add(start);

        var footer = Muted("Each project opens in its own window. File > Close Project comes back here.");
        footer.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    /// <summary>The recent projects, grouped by when they were opened, those found by the search.</summary>
    private void FillRecent()
    {
        _recent.Children.Clear();
        var words = _search.Text.Trim();
        Listed = RecentProjects.All()
            .Where(project => words.Length == 0 || project.Name.Contains(words, StringComparison.OrdinalIgnoreCase) || project.Path.Contains(words, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (Listed.Count == 0)
        {
            var none = Muted(words.Length > 0 ? $"No recent project has \"{words}\" in its name or folder." : "No recent projects yet. Projects you open or save are listed here, newest first.");
            none.Margin = new Thickness(8, 8, 0, 0);
            _recent.Children.Add(none);
            return;
        }

        foreach (var group in Listed.GroupBy(project => project.Pinned ? "Pinned" : When(project.Opened)))
        {
            var heading = Muted(group.Key);
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(8, _recent.Children.Count == 0 ? 0 : 14, 0, 4);
            _recent.Children.Add(heading);
            foreach (var project in group) _recent.Children.Add(Card(project));
        }
    }

    private static string When(DateTime opened) =>
        opened.Date == DateTime.Today ? "Today"
        : opened.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : opened.Date > DateTime.Today.AddDays(-7) ? "This week"
        : opened.Date > DateTime.Today.AddMonths(-1) ? "This month"
        : "Older";

    private Button Card(RecentProject project)
    {
        var picture = new Border { Width = 112, Height = 70, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 14, 0), ClipToBounds = true };
        picture.SetResourceReference(Border.BackgroundProperty, "Bg.Field");
        picture.SetResourceReference(Border.BorderBrushProperty, "Line");
        picture.Child = Thumbnail(project) is { } image
            ? new Image { Source = image, Stretch = Stretch.UniformToFill }
            : new Image { Source = TryFindResource("Icon.Plan") as ImageSource, Width = 28, Height = 28, Opacity = 0.6 };

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = project.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var where = Muted(project.Path);
        where.TextWrapping = TextWrapping.NoWrap;
        where.TextTrimming = TextTrimming.CharacterEllipsis;
        where.Margin = new Thickness(0, 2, 0, 0);
        words.Children.Add(where);
        if (!project.Exists)
        {
            var gone = new TextBlock { Text = "Not found - moved, renamed or deleted", Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A)), Margin = new Thickness(0, 2, 0, 0) };
            words.Children.Add(gone);
        }

        var date = Muted(project.Opened.Date == DateTime.Today ? project.Opened.ToString("HH:mm", CultureInfo.CurrentCulture) : project.Opened.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        date.VerticalAlignment = VerticalAlignment.Center;
        date.Margin = new Thickness(12, 0, 6, 0);

        var pin = new Button
        {
            Content = new Image { Source = TryFindResource(project.Pinned ? "Icon.Unpin" : "Icon.Pin") as ImageSource, Width = 16, Height = 16 }, ToolTip = project.Pinned ? "Unpin from the top of the list" : "Pin to the top of the list",
            Style = (Style)FindResource("Start.Glyph"), Opacity = project.Pinned ? 1 : 0.55
        };
        pin.Click += (_, e) =>
        {
            e.Handled = true;
            RecentProjects.Pin(project.Path, !project.Pinned);
            FillRecent();
        };

        var layout = new DockPanel();
        DockPanel.SetDock(picture, Dock.Left);
        DockPanel.SetDock(pin, Dock.Right);
        DockPanel.SetDock(date, Dock.Right);
        layout.Children.Add(picture);
        layout.Children.Add(pin);
        layout.Children.Add(date);
        layout.Children.Add(words);

        var card = new Button { Content = layout, Style = (Style)FindResource("Start.Card"), ToolTip = project.Path, Tag = project };
        card.Click += (_, _) => OpenRecent(project);

        var menu = new ContextMenu();
        menu.Items.Add(Item("Open", () => OpenRecent(project)));
        menu.Items.Add(Item(project.Pinned ? "Unpin from the top" : "Pin to the top", () =>
        {
            RecentProjects.Pin(project.Path, !project.Pinned);
            FillRecent();
        }));
        menu.Items.Add(Item("Open the folder it is in", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", project.Exists ? $"/select,\"{project.Path}\"" : $"\"{Path.GetDirectoryName(project.Path)}\"") { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove from the list", () =>
        {
            RecentProjects.Forget(project.Path);
            FillRecent();
        }));
        card.ContextMenu = menu;
        return card;
    }

    private static ImageSource? Thumbnail(RecentProject project)
    {
        if (project.Thumbnail is not { } file) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(Path.GetFullPath(file));
            image.DecodePixelWidth = 224;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    // ---- what can be started from -----------------------------------------------------

    /// <summary>The New Project steps, in place of the home screen; Back to Home returns to it.</summary>
    public NewProjectSteps ShowNewProject()
    {
        _steps = new NewProjectSteps("Back to Home") { Margin = new Thickness(40, 28, 40, 24) };
        _steps.Created += (_, choice) => Finish(choice);
        _steps.Cancelled += (_, _) => ShowHome();
        _content.Content = _steps;
        Title = "BIMDesigner - New Project";
        return _steps;
    }

    public void ShowHome()
    {
        _steps = null;
        _content.Content = _home;
        Title = "BIMDesigner";
        FillRecent();
    }

    private void OpenRecent(RecentProject project)
    {
        if (!project.Exists)
        {
            if (MessageBox.Show(this, $"{project.Path} is not there any more - moved, renamed or deleted.\n\nTake it off the list?", "Open recent",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                RecentProjects.Forget(project.Path);
                FillRecent();
            }

            return;
        }

        OpenProject(project.Path);
    }

    private void PickProject()
    {
        var dialog = new OpenFileDialog { Title = "Open Project", Filter = "BIMDesigner project (*.bimx)|*.bimx|All files (*.*)|*.*", DefaultExt = ".bimx" };
        if (dialog.ShowDialog(this) == true) OpenProject(dialog.FileName);
    }

    private void PickIfc()
    {
        var dialog = new OpenFileDialog { Title = "Open IFC", Filter = "IFC model (*.ifc)|*.ifc|All files (*.*)|*.*", DefaultExt = ".ifc" };
        if (dialog.ShowDialog(this) == true) OpenIfc(dialog.FileName);
    }

    /// <summary>A project file read and opened - or, where it cannot be read, why, and the home screen kept.</summary>
    public bool OpenProject(string path, bool quiet = false)
    {
        BimDocument document;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            document = ProjectFile.Load(path);
        }
        catch (Exception exception) when (exception is ProjectFileException or IOException or UnauthorizedAccessException)
        {
            Mouse.OverrideCursor = null;
            AppLog.Warning($"Could not open {path}.", exception);
            if (!quiet) MessageBox.Show(this, exception.Message, "Could not open project", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        Finish(new StartChoice(document, path, $"Opened {Path.GetFileName(path)}."));
        return true;
    }

    /// <summary>An IFC file read into a new project, unsaved.</summary>
    public bool OpenIfc(string path, bool quiet = false)
    {
        IfcImportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = IfcImport.Open(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Mouse.OverrideCursor = null;
            if (!quiet) MessageBox.Show(this, $"Could not read the IFC file.\n\n{exception.Message}", "Open IFC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        Finish(new StartChoice(result.Document, null, $"Read {Path.GetFileName(path)} ({result.Schema}): {result.Summary()}. Save it as a project to keep it.")
        {
            Notes = quiet ? Array.Empty<string>() : result.Notes
        });
        return true;
    }

    public void OpenExample() =>
        Finish(new StartChoice(BimDocument.CreateDefault(), null, "The example house: walls drawn to look round and try the tools on. Save it to keep it.") { Example = true });

    public void StartEmpty() =>
        Finish(new StartChoice(ProjectTemplates.Create(new NewProjectSpec()), null, "An empty project, from the England house template. Start drawing walls on the Architecture tab."));

    private void Recover(RecoveredWork work)
    {
        var name = work.SavedAs is { } saved ? Path.GetFileName(saved) : work.Name;
        try
        {
            var document = ProjectFile.Load(work.File);
            RecoverySession.Forget(work);
            AppLog.Info($"Brought back the work on {name} kept at {work.Kept:yyyy-MM-dd HH:mm}.");
            Finish(new StartChoice(document, work.SavedAs, $"Brought back {name} as it was at {work.Kept:HH:mm} on {work.Kept:d MMMM}. Save it to keep it.") { Unsaved = true });
        }
        catch (ProjectFileException exception)
        {
            AppLog.Warning($"The work kept on {name} could not be read.", exception);
            MessageBox.Show(this, $"The copy kept of {name} could not be read: {exception.Message}", "Recover unsaved work", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Finish(StartChoice choice)
    {
        Choice = choice;
        Chosen?.Invoke(this, choice);
    }

    // ---- building blocks -----------------------------------------------------------------

    private Button Tile(string icon, string title, string description, Action act)
    {
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        var said = Muted(description);
        said.Margin = new Thickness(0, 3, 0, 0);
        words.Children.Add(said);
        var layout = new DockPanel();
        var image = new Image { Source = TryFindResource(icon) as ImageSource, Width = 30, Height = 30, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(image, Dock.Left);
        layout.Children.Add(image);
        layout.Children.Add(words);
        var tile = new Button { Content = layout, Style = (Style)FindResource("Start.Tile") };
        tile.Click += (_, _) => act();
        return tile;
    }

    private Button Link(string text, Action act)
    {
        var link = new Button { Content = text, Style = (Style)FindResource("Start.Link"), HorizontalAlignment = HorizontalAlignment.Left };
        link.Click += (_, _) => act();
        return link;
    }

    private static MenuItem Item(string header, Action act)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => act();
        return item;
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        return block;
    }

    /// <summary>A key's action.</summary>
    private sealed class Act(Action act) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => act();
    }
}
