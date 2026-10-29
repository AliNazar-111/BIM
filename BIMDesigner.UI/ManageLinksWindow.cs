using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Infrastructure.Interoperability;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The models and drawings linked into the project: each with its file and how it stands - read, not
/// found, changed since - to reload from its file or from another, to unload and load again, or to take
/// out; and more linked from here. Picking one selects it, its placement in its Properties.
/// </summary>
internal sealed class ManageLinksWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _problem = new();
    private readonly Button _reload = new() { Content = "Reload", IsEnabled = false };
    private readonly Button _reloadFrom = new() { Content = "Reload From...", IsEnabled = false };
    private readonly Button _unload = new() { Content = "Unload", IsEnabled = false };
    private readonly Button _remove = new() { Content = "Remove", IsEnabled = false };

    private static readonly Brush Unmet = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A));

    public ManageLinksWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Manage Links", 820, 460);
        MinWidth = 520;
        MinHeight = 300;
        _list.SetResourceReference(BackgroundProperty, "Bg.Window");
        _list.SetResourceReference(ForegroundProperty, "Text");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) =>
        {
            var picked = Picked;
            (_reload.IsEnabled, _reloadFrom.IsEnabled, _remove.IsEnabled) = (picked is not null, picked is not null, picked is not null);
            _unload.IsEnabled = picked is ModelLink;
            _unload.Content = picked is ModelLink { Shown: false } ? "Load" : "Unload";
            if (picked is not null) _main.SelectFromBrowser(picked);
        };

        var linkModel = new Button { Content = "Link Model...", ToolTip = "Link a BIMDesigner project or an IFC file: drawn with this one, read from its own file" };
        linkModel.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Link a Model", Filter = LinkFiles.Filter };
            if (dialog.ShowDialog(this) == true && _main.LinkModel(dialog.FileName) is { } link) Select(link);
        };
        var linkDrawing = new Button { Content = "Link DWG...", ToolTip = "Link a DWG or DXF drawing onto the storey shown: read again from its file as the project opens and when reloaded" };
        linkDrawing.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Link a DWG or DXF Drawing", Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf" };
            if (dialog.ShowDialog(this) == true && _main.ImportCad(dialog.FileName, quiet: false, linked: true) is { } underlay) Select(underlay);
        };
        _reload.Click += (_, _) => Reload(null);
        _reloadFrom.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Reload From",
                Filter = Picked is CadUnderlay ? "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf" : LinkFiles.Filter
            };
            if (dialog.ShowDialog(this) == true) Reload(dialog.FileName);
        };
        _unload.Click += (_, _) =>
        {
            if (Picked is not ModelLink link) return;
            _main.History.Execute(new SetLinkShownCommand(link, !link.Shown));
            _main.Relinked();
            Select(link);
        };
        _remove.Click += (_, _) =>
        {
            if (Picked is not { } picked) return;
            _main.History.Execute(new DeleteElementCommand(_main.Document, picked));
            if (picked is ModelLink link) ModelLinks.Forget(link);
            _main.Relinked();
        };
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new WrapPanel { Margin = new Thickness(16, 6, 16, 6) };
        foreach (var button in new[] { linkModel, linkDrawing, _reload, _reloadFrom, _unload, _remove })
        {
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Padding = new Thickness(10, 4, 10, 4);
            buttons.Children.Add(button);
        }

        var intro = DialogLook.Intro("Models and drawings linked in, read from their own files: a project or an IFC file drawn with this one in 3D and cut on its plans, clash tested against it; a DWG or DXF under a storey. Each file is looked at every few seconds - reload what has changed. Pick one to select it: its place and turn are in its Properties.");
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Top);
        root.Children.Add(DialogLook.Footer(_problem, close));
        root.Children.Add(intro);
        root.Children.Add(buttons);
        var border = new Border { Child = _list, BorderThickness = new Thickness(1), Margin = new Thickness(16, 0, 16, 8) }.Lined();
        root.Children.Add(border);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Refresh();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>The link picked: a model's or a drawing's.</summary>
    public Element? Picked => (_list.SelectedItem as ListBoxItem)?.Tag as Element;

    private void Select(Element element)
    {
        Refresh();
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, element));
    }

    private void Reload(string? from)
    {
        switch (Picked)
        {
            case ModelLink link when from is null:
                _main.ReloadLink(link);
                break;
            case ModelLink link:
                _main.ReloadLinkFrom(link, from);
                break;
            case CadUnderlay underlay:
                _main.ReloadUnderlay(underlay, from);
                break;
        }

        if (Picked is { } picked) Select(picked);
    }

    /// <summary>Every link listed as it now stands.</summary>
    public void Refresh()
    {
        var document = _main.Document;
        var keep = Picked?.Id;
        _list.Items.Clear();
        foreach (var link in document.All<ModelLink>().OrderBy(link => link.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var status = ModelLinks.Status(link);
            var wanting = !link.Shown ? false : status.StartsWith("Not", StringComparison.Ordinal) || status.Contains("could not", StringComparison.Ordinal) || ModelLinks.Changed(link);
            _list.Items.Add(Row(link, $"{link.Title}  -  {(Path.GetExtension(link.Path).Equals(".ifc", StringComparison.OrdinalIgnoreCase) ? "IFC" : "Project")}, {EnumText.Humanise(link.Positioning).ToLowerInvariant()}", status, link.Path, wanting));
        }

        foreach (var underlay in document.All<CadUnderlay>().Where(underlay => underlay.Linked).OrderBy(underlay => Path.GetFileName(underlay.Source), StringComparer.CurrentCultureIgnoreCase))
        {
            var found = File.Exists(underlay.Source);
            var changed = _main.Changed(underlay);
            var status = !found ? "Not found - the copy kept in the project is drawn" : changed ? "Its file has changed since: reload it" : $"{underlay.Drawing.Paths.Count:#,0} lines, {underlay.Drawing.Labels.Count:#,0} texts";
            _list.Items.Add(Row(underlay, $"{Path.GetFileName(underlay.Source)}  -  DWG on {document.FindLevel(underlay.LevelId)?.Name ?? "a storey"}", status, underlay.Source, !found || changed));
        }

        if (_list.Items.Count == 0)
            _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "Nothing linked yet: Link Model... or Link DWG... above.", Opacity = 0.7, Margin = new Thickness(6) }, IsEnabled = false });
        if (keep is { } id) _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(item => (item.Tag as Element)?.Id == id);
    }

    private static ListBoxItem Row(Element element, string title, string status, string path, bool wanting)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 4, 4, 6) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var said = new TextBlock { Text = status, TextWrapping = TextWrapping.Wrap };
        if (wanting) said.Foreground = Unmet;
        panel.Children.Add(said);
        panel.Children.Add(new TextBlock { Text = path, Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        return new ListBoxItem { Content = panel, Tag = element };
    }

    /// <summary>A linked model unloaded or loaded again, as one step to undo.</summary>
    private sealed class SetLinkShownCommand : IUndoableCommand
    {
        private readonly ModelLink _link;
        private readonly bool _shown;

        public SetLinkShownCommand(ModelLink link, bool shown) => (_link, _shown) = (link, shown);

        public string Name => _shown ? $"Load {_link.Title}" : $"Unload {_link.Title}";

        public void Redo() => _link.Shown = _shown;

        public void Undo() => _link.Shown = !_shown;
    }
}
