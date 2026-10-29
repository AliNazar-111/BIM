using System.IO;
using System.Windows;
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
