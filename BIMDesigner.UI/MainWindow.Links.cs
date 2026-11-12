using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// Models and drawings linked in: a project or an IFC file drawn with this one, a DWG or DXF read from its
/// file - linked, reloaded, unloaded, taken out; their files looked at every few seconds and anything written
/// since it was read said, to be reloaded.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _linkWatch = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>When each linked drawing's file had last been written, as it was read.</summary>
    private readonly Dictionary<Guid, DateTime> _underlayRead = new();

    /// <summary>What has been said to have changed, so it is said once.</summary>
    private readonly HashSet<(Guid, DateTime)> _announced = new();

    private ManageLinksWindow? _linksWindow;

    /// <summary>Linked models read by their kind; their files watched.</summary>
    private void StartLinks()
    {
        ModelLinks.Reader = LinkFiles.Read;
        _linkWatch.Tick += (_, _) => WatchLinks();
        _linkWatch.Tick += (_, _) => WatchCentral();
        _linkWatch.Start();
    }

    /// <summary>What was read for the last project forgotten, as another opens.</summary>
    private void ForgetLinks()
    {
        foreach (var link in _document.All<ModelLink>()) ModelLinks.Forget(link);
        _underlayRead.Clear();
        _announced.Clear();
    }

    private void OnLinkModel(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Link a Model", Filter = LinkFiles.Filter };
        if (dialog.ShowDialog(this) == true) LinkModel(dialog.FileName);
    }

    /// <summary>
    /// A model linked in: read, put origin to origin - or by survey point where both it and this project are
    /// set out on the national grid - drawn in 3D and cut on the plans. Null where it cannot be read.
    /// </summary>
    internal ModelLink? LinkModel(string path)
    {
        var link = new ModelLink { Path = Path.GetFullPath(path) };
        Mouse.OverrideCursor = Cursors.Wait;
        LinkedModel read;
        try
        {
            read = ModelLinks.Load(link);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (read.Document is not { } linked)
        {
            MessageBox.Show(this, read.Error ?? $"{link.Title} could not be read.", "Link a Model", MessageBoxButton.OK, MessageBoxImage.Warning);
            ModelLinks.Forget(link);
            return null;
        }

        var surveyed = linked.ProjectInformation.SurveyOffset.Length > 1 && _document.ProjectInformation.SurveyOffset.Length > 1;
        link.Positioning = surveyed ? LinkPositioning.BySurveyPoint : LinkPositioning.OriginToOrigin;
        _history.Execute(new AddElementCommand(_document, link, $"Link {link.Title}"));
        Relinked();
        var count = linked.Elements.Count(element => ClashDetective.Solidly(element.Category));
        StatusHint.Text = $"Linked {link.Title}: {count:#,0} elements, {(surveyed ? "by survey point - each set out on the national grid" : "origin to origin")}. " +
                          "It is drawn in 3D and cut on the plans; move it in its Properties, reload or unload it in Manage Links.";
        return link;
    }

    private void OnLinkCad(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Link a DWG or DXF Drawing",
            Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|DWG drawing (*.dwg)|*.dwg|DXF drawing (*.dxf)|*.dxf"
        };
        if (dialog.ShowDialog(this) == true) ImportCad(dialog.FileName, quiet: false, linked: true);
    }

    private void OnManageLinks(object sender, RoutedEventArgs e) => OpenLinks();

    /// <summary>The Manage Links window, opened or brought forward.</summary>
    internal ManageLinksWindow OpenLinks() => Open(ref _linksWindow, () => new ManageLinksWindow(this), this);

    /// <summary>A linked model read again from its file.</summary>
    internal void ReloadLink(ModelLink link)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var read = ModelLinks.Load(link);
            StatusHint.Text = read.Error ?? $"Reloaded {link.Title}.";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        Relinked();
        SayLinkChanges(link);

        // The levels and gridlines following it, where its originals have moved.
        var changed = CopyMonitor.Review(_document).Count(finding => finding.Link?.Id == link.Id);
        if (changed > 0)
        {
            StatusHint.Text += $" {changed} change{(changed == 1 ? string.Empty : "s")} to the levels, gridlines, columns and walls followed from it - Coordination Review.";
            _reviewWindow?.Refresh();
        }
    }

    private CoordinationReviewWindow? _reviewWindow;

    private void OnCopyMonitor(object sender, RoutedEventArgs e) => AskAndCopyMonitor(null);

    /// <summary>What Copy/Monitor brings from a link: its levels and gridlines to start with, then as last asked.</summary>
    internal CopyMonitorKinds CopyMonitorKinds { get; set; } = CopyMonitorKinds.Datums;

    /// <summary>Asks what to bring from the link - levels, gridlines, columns, walls - then copies and follows them.</summary>
    internal void AskAndCopyMonitor(ModelLink? link)
    {
        var boxes = new (CopyMonitorKinds Kind, CheckBox Box)[]
        {
            (CopyMonitorKinds.Levels, new CheckBox { Content = "Levels", ToolTip = "Its storeys: copied in, or paired with this project's at the same height" }),
            (CopyMonitorKinds.Grids, new CheckBox { Content = "Gridlines", ToolTip = "Its grid: copied in, or paired with this project's on the same line" }),
            (CopyMonitorKinds.Columns, new CheckBox { Content = "Columns", ToolTip = "Its columns, structural and architectural: copied in with their types, or paired with this project's standing there" }),
            (CopyMonitorKinds.Walls, new CheckBox { Content = "Walls", ToolTip = "Its straight and curved walls - not curtain, stacked, elliptical or spline walls: copied in with their types, or paired with this project's on the same line" })
        };
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "Copy from the linked model, each to follow its original:", Margin = new Thickness(0, 0, 0, 8) });
        foreach (var (kind, box) in boxes)
        {
            box.IsChecked = CopyMonitorKinds.HasFlag(kind);
            box.Margin = new Thickness(0, 2, 0, 2);
            content.Children.Add(box);
        }

        var ok = new Button { Content = "Copy", Padding = new Thickness(20, 5, 20, 5), Margin = new Thickness(0, 12, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        content.Children.Add(buttons);
        var window = new System.Windows.Window
        {
            Title = "Copy/Monitor", Content = content, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Background, Foreground = Foreground
        };
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return;

        var kinds = boxes.Where(entry => entry.Box.IsChecked == true).Aggregate(CopyMonitorKinds.None, (all, entry) => all | entry.Kind);
        if (kinds == CopyMonitorKinds.None)
        {
            StatusHint.Text = "Nothing picked to copy.";
            return;
        }

        CopyMonitorKinds = kinds;
        CopyMonitorFrom(link);
    }

    private void OnCoordinationReview(object sender, RoutedEventArgs e) => OpenCoordinationReview();

    /// <summary>
    /// A linked model's levels and gridlines - and its columns and walls, as <see cref="CopyMonitorKinds"/>
    /// says - copied in, or paired with this project's own where they already stand, each to follow its
    /// original: the link given, the one picked, or the only one. One step to undo; internal so tests and
    /// the harness can copy.
    /// </summary>
    internal void CopyMonitorFrom(ModelLink? link)
    {
        var links = _document.All<ModelLink>().Where(each => each.Shown).ToList();
        link ??= Plan.SelectedElements.OfType<ModelLink>().FirstOrDefault() ?? _linksWindow?.Picked as ModelLink ?? (links.Count == 1 ? links[0] : null);
        if (link is null)
        {
            StatusHint.Text = links.Count == 0
                ? "Link a model first - Manage → Link Model - to copy its levels, gridlines, columns or walls."
                : "Pick the linked model to copy from - on the plan, in 3D or in Manage Links - then Copy/Monitor.";
            return;
        }

        var (command, said) = CopyMonitor.Copy(_document, link, kinds: CopyMonitorKinds);
        if (command is not null)
        {
            _history.Execute(command);
            AfterHistoryChange();
        }

        StatusHint.Text = said;
    }

    /// <summary>After an answer in the Coordination Review: the storeys, the plan and the rest follow.</summary>
    internal void AfterCoordination() => AfterHistoryChange();

    /// <summary>The Coordination Review, opened or brought forward: what has changed in the levels and gridlines followed.</summary>
    internal CoordinationReviewWindow OpenCoordinationReview()
    {
        var window = Open(ref _reviewWindow, () => new CoordinationReviewWindow(this), this);
        window.Refresh();
        return window;
    }

    /// <summary>A linked model pointed at another file - the same model moved, or its next issue - and read from it.</summary>
    internal void ReloadLinkFrom(ModelLink link, string path)
    {
        var before = link.Path;
        var after = Path.GetFullPath(path);
        _history.Execute(new SetLinkPathCommand(link, before, after));
        ReloadLink(link);
    }

    /// <summary>A linked drawing read again from its file, or from another.</summary>
    internal bool ReloadUnderlay(CadUnderlay underlay, string? from = null)
    {
        var source = from ?? underlay.Source;
        if (!File.Exists(source))
        {
            StatusHint.Text = $"{Path.GetFileName(source)} is not where it was: {source}";
            return false;
        }

        try
        {
            var read = CadImport.Read(source);
            _history.Execute(new ReloadUnderlayCommand(underlay, read.Drawing, source));
            _underlayRead[underlay.Id] = File.GetLastWriteTimeUtc(source);
            StatusHint.Text = $"Reloaded {Path.GetFileName(source)}: {read.Drawing.Paths.Count:#,0} lines and {read.Drawing.Labels.Count:#,0} texts.";
            Relinked();
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            StatusHint.Text = $"{Path.GetFileName(source)} could not be read: {error.Message}";
            return false;
        }
    }

    /// <summary>The links drawn again as they now are.</summary>
    internal void Relinked()
    {
        Core.Views.Projections.Forget(_document);
        Model3D.Rebuild();
        Plan.RefreshModel();
        _linksWindow?.Refresh();
    }

    /// <summary>Whether a linked drawing's file has been written since it was read.</summary>
    internal bool Changed(CadUnderlay underlay) =>
        underlay.Linked && File.Exists(underlay.Source) && _underlayRead.TryGetValue(underlay.Id, out var read) && File.GetLastWriteTimeUtc(underlay.Source) > read;

    /// <summary>Each link's file looked at: anything written since it was read said once, to be reloaded.</summary>
    private void WatchLinks()
    {
        var changed = new List<string>();
        foreach (var link in _document.All<ModelLink>().Where(link => link.Shown))
            if (ModelLinks.Changed(link) && _announced.Add((link.Id, File.GetLastWriteTimeUtc(link.Path))))
                changed.Add(link.Title);
        foreach (var underlay in _document.All<CadUnderlay>().Where(underlay => underlay.Linked && File.Exists(underlay.Source)))
        {
            var written = File.GetLastWriteTimeUtc(underlay.Source);
            if (!_underlayRead.ContainsKey(underlay.Id)) _underlayRead[underlay.Id] = written;
            else if (Changed(underlay) && _announced.Add((underlay.Id, written))) changed.Add(Path.GetFileName(underlay.Source));
        }

        if (changed.Count == 0) return;
        StatusHint.Text = $"{string.Join(", ", changed)} {(changed.Count == 1 ? "has" : "have")} changed since {(changed.Count == 1 ? "it was" : "they were")} read: reload in Manage → Manage Links.";
        _linksWindow?.Refresh();
    }

    /// <summary>A linked model pointed at another file, as one step to undo.</summary>
    private sealed class SetLinkPathCommand : IUndoableCommand
    {
        private readonly ModelLink _link;
        private readonly string _before;
        private readonly string _after;

        public SetLinkPathCommand(ModelLink link, string before, string after) => (_link, _before, _after) = (link, before, after);

        public string Name => $"Reload {Path.GetFileName(_after)}";

        public void Redo()
        {
            _link.Path = _after;
            ModelLinks.Forget(_link);
        }

        public void Undo()
        {
            _link.Path = _before;
            ModelLinks.Forget(_link);
        }
    }
}
