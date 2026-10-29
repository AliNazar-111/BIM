using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.UI.Rendering;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// Two issues of a model compared: this project against its last save or another saved project or IFC
/// file, or a linked model against the issue it replaced or another issue of it. What was found is
/// counted, listed - added, removed, changed, moved, then the types and levels - each with what about it
/// has changed, and coloured on the plan and in 3D while the window is open; written up as a PDF.
/// </summary>
internal sealed class CompareWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly ComboBox _source = new() { MinWidth = 380, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _compare = new() { Content = "Compare", Padding = new Thickness(14, 4, 14, 4) };
    private readonly TextBlock _found = new() { Margin = new Thickness(16, 2, 16, 6), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _kinds = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 6) };
    private readonly ComboBox _category = new() { MinWidth = 160, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { Width = 170, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Find by name, category or level" };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 6, 16, 6), MinHeight = 48 };
    private readonly TextBlock _problem = new();
    private readonly Button _show = new() { Content = "Show", IsEnabled = false, ToolTip = "Select it and go to its storey" };
    private readonly Button _report = new() { Content = "Report...", IsEnabled = false, ToolTip = "Write what was found as a PDF to send on" };
    private readonly CheckBox _colour = new() { Content = "Colour the plan and 3D", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Dictionary<ChangeKind, CheckBox> _kindBoxes = new();
    private ComparisonOverlay? _shown;
    private bool _stale;

    private sealed record Source(string Text, Func<Window, ComparisonOverlay?> Compare);

    public CompareWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Compare", 860, 620);
        MinWidth = 600;
        MinHeight = 420;
        foreach (var box in new Control[] { _list }) { box.SetResourceReference(BackgroundProperty, "Bg.Window"); box.SetResourceReference(ForegroundProperty, "Text"); }
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

        _compare.Click += (_, _) => Run();
        _list.SelectionChanged += (_, _) => Picked();
        _list.MouseDoubleClick += (_, _) => ShowPicked();
        _show.Click += (_, _) => ShowPicked();
        _report.Click += (_, _) => Report();
        _colour.Click += (_, _) => _main.ShowComparison(_colour.IsChecked == true ? _shown : null);
        _category.SelectionChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();

        foreach (var kind in new[] { ChangeKind.Added, ChangeKind.Removed, ChangeKind.Changed, ChangeKind.Moved })
        {
            var swatch = new Rectangle { Width = 10, Height = 10, Fill = Swatch(kind), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            var box = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(swatch);
            label.Children.Add(new TextBlock { Text = EnumText(kind) });
            box.Content = label;
            box.Click += (_, _) => KindsChanged();
            _kindBoxes[kind] = box;
            _kinds.Children.Add(box);
        }

        _kinds.Children.Add(_category);
        _kinds.Children.Add(_search);

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 4, 16, 6) };
        top.Children.Add(new TextBlock { Text = "Compare", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_source);
        top.Children.Add(_compare);

        var intro = DialogLook.Intro("Two issues of a model set side by side, each element matched by its id - which a project keeps from save to save and an IFC file keeps as its GlobalId: " +
                                     "what was added, removed, changed or only moved. While this window is open the plan and 3D show them - added green, changed amber, moved blue, removed red - and the rest faded.");
        var list = new Border { Child = _list, BorderThickness = new Thickness(1), Margin = new Thickness(16, 0, 16, 0) }.Lined();
        var details = new Border { Child = _details, BorderThickness = new Thickness(1), Margin = new Thickness(16, 6, 16, 6) }.Lined();

        var root = new DockPanel();
        var heading = new StackPanel();
        heading.Children.Add(intro);
        heading.Children.Add(top);
        heading.Children.Add(_found);
        heading.Children.Add(_kinds);
        DockPanel.SetDock(heading, Dock.Top);
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();
        var footer = DialogLook.Footer(_problem, _show, _report, close);
        DockPanel.SetDock(details, Dock.Bottom);
        _colour.Margin = new Thickness(14, 0, 0, 0);
        top.Children.Add(_colour);

        root.Children.Add(heading);
        root.Children.Add(footer);
        root.Children.Add(details);
        root.Children.Add(list);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) =>
        {
            _main.History.Changed -= OnHistoryChanged;
            _main.ShowComparison(null);
        };
        Sources();
        Fill();
    }

    /// <summary>What has been found and is shown, or none yet.</summary>
    public ModelComparison? Comparison => _shown?.Comparison;

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (_shown is not { Link: null } || _stale) return;
        _stale = true;
        DialogLook.Warn(_problem, "The project has changed since it was compared: Compare again to see it as it is now.");
    }

    /// <summary>What there is to compare: this project with its last save or a file, each linked model with its issue before or a file.</summary>
    public void Sources()
    {
        var keep = _source.SelectedIndex;
        _source.Items.Clear();
        if (_main.ProjectPath is { } path && File.Exists(path))
            Add($"This project with its last save ({Path.GetFileName(path)}, {File.GetLastWriteTime(path):d MMM HH:mm})", _ => _main.CompareWithFile(path));
        Add("This project with a saved project or IFC file...", owner => Ask(owner, "Compare This Project With") is { } file ? _main.CompareWithFile(file) : null);
        foreach (var link in _main.Document.All<ModelLink>().Where(link => link.Shown).OrderBy(link => link.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (ModelLinks.PreviousOf(link) is not null) Add($"{link.Title} with the issue read before its last reload", _ => _main.CompareLink(link, null));
            Add($"{link.Title} with another issue of it...", owner => Ask(owner, $"Compare {link.Title} With") is { } file ? _main.CompareLink(link, file) : null);
        }

        _source.SelectedIndex = keep >= 0 && keep < _source.Items.Count ? keep : 0;

        void Add(string text, Func<Window, ComparisonOverlay?> compare) =>
            _source.Items.Add(new ComboBoxItem { Content = text, Tag = new Source(text, compare) });
    }

    private static string? Ask(Window owner, string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = LinkFiles.Filter };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>The comparison picked made, and shown.</summary>
    public void Run(int? index = null)
    {
        if (index is { } at) _source.SelectedIndex = at;
        if ((_source.SelectedItem as ComboBoxItem)?.Tag is not Source source) return;
        DialogLook.Say(_problem, string.Empty);
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        ComparisonOverlay? overlay;
        try
        {
            overlay = source.Compare(this);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DialogLook.Warn(_problem, $"It could not be compared: {error.Message}");
            return;
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }

        if (overlay is null) return;
        Display(overlay);
    }

    /// <summary>A comparison made elsewhere - by a reload - shown here.</summary>
    public void Display(ComparisonOverlay overlay)
    {
        _shown = overlay;
        _stale = false;
        DialogLook.Say(_problem, string.Empty);

        // A linked model's reload shown here: what is compared said as it is in the list.
        if (overlay.Link is { } link && _source.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is Source source && source.Text.StartsWith($"{link.Title} with the issue read before", StringComparison.Ordinal)) is { } picked)
            _source.SelectedItem = picked;
        foreach (var (kind, box) in _kindBoxes) box.IsChecked = overlay.Kinds.Contains(kind);
        var categories = overlay.Comparison.Elements.Select(change => change.Category).Distinct().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _category.Items.Clear();
        _category.Items.Add("Every category");
        foreach (var category in categories) _category.Items.Add(category);
        _category.SelectedIndex = 0;
        var comparison = overlay.Comparison;
        _found.Text = $"{comparison.Before}  →  {comparison.After}:  {comparison.Said()}{(comparison.Same ? "." : $"; {comparison.Unchanged:#,0} elements as they were.")}";
        _report.IsEnabled = true;
        if (_colour.IsChecked == true) _main.ShowComparison(overlay);
        Fill();
    }

    private void KindsChanged()
    {
        if (_shown is not { } overlay) return;
        overlay.Kinds.Clear();
        foreach (var (kind, box) in _kindBoxes)
            if (box.IsChecked == true) overlay.Kinds.Add(kind);
        if (_colour.IsChecked == true) _main.ShowComparison(overlay);
        Fill();
    }

    /// <summary>The changes listed as the kinds, category and search have them.</summary>
    private void Fill()
    {
        _list.Items.Clear();
        _details.Text = string.Empty;
        _show.IsEnabled = false;
        if (_shown is not { } overlay)
        {
            _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "Pick what to compare above, then Compare.", Opacity = 0.7, Margin = new Thickness(6) }, IsEnabled = false });
            return;
        }

        var comparison = overlay.Comparison;
        var category = _category.SelectedIndex > 0 ? _category.SelectedItem as string : null;
        var find = _search.Text.Trim();
        bool Wanted(ElementChange change) =>
            (category is null || change.Category == category) &&
            (find.Length == 0 || $"{change.Name} {change.Category} {change.Level}".Contains(find, StringComparison.CurrentCultureIgnoreCase));

        foreach (var change in comparison.Elements.Where(change => overlay.Kinds.Contains(change.Kind) && Wanted(change))
                     .OrderBy(change => change.Kind).ThenBy(change => change.Level).ThenBy(change => change.Category).ThenBy(change => change.Name))
            _list.Items.Add(Row(change, $"{change.Category}{(change.Level.Length == 0 ? string.Empty : $" on {change.Level}")}"));

        if (category is null)
        {
            foreach (var change in comparison.Types.Where(Wanted)) _list.Items.Add(Row(change, $"{change.Category} type"));
            foreach (var change in comparison.Levels.Where(Wanted)) _list.Items.Add(Row(change, "Level"));
        }

        if (_list.Items.Count == 0)
            _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = comparison.Same ? "Nothing has changed." : "Nothing of the kinds ticked.", Opacity = 0.7, Margin = new Thickness(6) }, IsEnabled = false });
    }

    private static ListBoxItem Row(ElementChange change, string what)
    {
        var panel = new DockPanel { Margin = new Thickness(4, 3, 4, 4) };
        var swatch = new Rectangle { Width = 10, Height = 10, Fill = Swatch(change.Kind), Margin = new Thickness(2, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(swatch, Dock.Left);
        panel.Children.Add(swatch);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = $"{change.Name}  -  {EnumText(change.Kind)}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var said = change.Kind is ChangeKind.Added or ChangeKind.Removed ? what : $"{what}: {change.Summary}";
        text.Children.Add(new TextBlock { Text = said, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34 });
        panel.Children.Add(text);
        return new ListBoxItem { Content = panel, Tag = change };
    }

    private ElementChange? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as ElementChange;

    private void Picked()
    {
        var change = Selected;
        _details.Text = change is null ? string.Empty : string.Join(Environment.NewLine, change.Details());
        _show.IsEnabled = change is not null && _shown is not null && _main.CanShowChange(_shown, change);
    }

    private void ShowPicked()
    {
        if (Selected is { } change && _shown is { } overlay) _main.ShowChange(overlay, change);
    }

    private void Report()
    {
        if (_shown is not { } overlay) return;
        var project = _main.Document.ProjectInformation.Name;
        var dialog = new SaveFileDialog { Title = "Save the Comparison", Filter = "PDF document (*.pdf)|*.pdf", FileName = Export.ComparisonReport.SuggestedFileName(project, DateTime.Today) };
        if (dialog.ShowDialog(this) != true) return;
        SaveReportTo(overlay.Comparison, dialog.FileName);
    }

    /// <summary>The comparison written as a PDF.</summary>
    internal void SaveReportTo(ModelComparison comparison, string path)
    {
        try
        {
            Export.ComparisonReport.ToPdf(Export.ComparisonReport.Build(comparison, _main.Document.ProjectInformation.Name, MainWindow.AppVersion, DateTime.Now), path);
            DialogLook.Say(_problem, $"Written to {Path.GetFileName(path)}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DialogLook.Warn(_problem, $"It could not be written: {error.Message}");
        }
    }

    private static string EnumText(ChangeKind kind) => kind.ToString();

    private static Brush Swatch(ChangeKind kind)
    {
        var colour = ComparisonOverlay.ColourOf(kind);
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }
}
