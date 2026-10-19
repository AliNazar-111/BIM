using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// The project's view templates: each named, for one kind of view, with every setting it can set
/// beside an Include tick. A change here is one step to undo, and reaches every view the
/// template governs at once.
/// </summary>
public partial class ViewTemplatesWindow : System.Windows.Window
{
    private readonly BimDocument _document;
    private readonly UndoStack _history;
    private bool _loading;

    /// <summary>The kinds of thing a 3D view's template can leave out.</summary>
    private static readonly MeshKind[] Kinds =
        { MeshKind.Roof, MeshKind.Ceiling, MeshKind.Floor, MeshKind.Wall, MeshKind.Framing, MeshKind.Stair, MeshKind.Ramp, MeshKind.Railing, MeshKind.Beam, MeshKind.Terrain };

    public ViewTemplatesWindow(BimDocument document, UndoStack history, ViewTemplate? selected = null)
    {
        InitializeComponent();
        ScreenFit.Apply(this);
        (_document, _history) = (document, history);
        Refresh(selected);
    }

    /// <summary>Raised whenever a template, or the views it governs, changed.</summary>
    public event EventHandler? Changed;

    private ViewTemplate? Selected => (TemplateList.SelectedItem as ListBoxItem)?.Tag as ViewTemplate;

    private void Refresh(ViewTemplate? select = null)
    {
        var keep = select ?? Selected;
        _loading = true;
        TemplateList.Items.Clear();
        foreach (var template in _document.Elements.OfType<ViewTemplate>().OrderBy(t => t.For).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListBoxItem { Content = $"{template.Name}  —  {Kind(template.For)}", Tag = template };
            TemplateList.Items.Add(item);
            if (ReferenceEquals(template, keep)) item.IsSelected = true;
        }

        if (TemplateList.SelectedItem is null && TemplateList.Items.Count > 0) TemplateList.SelectedIndex = 0;
        _loading = false;
        ShowDetails();
    }

    private static string Kind(ViewTemplateKind kind) => kind == ViewTemplateKind.ThreeD ? "3D views" : $"{EnumText.Humanise(kind)}s";

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) ShowDetails();
    }

    /// <summary>A change to the template, and to every view it governs, as one step.</summary>
    private void Change(Action<ViewTemplate> change, string name)
    {
        if (_loading || Selected is not { } template) return;
        _history.Execute(ViewTemplates.Change(_document, template, change, name));
        Changed?.Invoke(this, EventArgs.Empty);
        Refresh(template);
    }

    private void ShowDetails()
    {
        Details.Children.Clear();
        DuplicateButton.IsEnabled = DeleteButton.IsEnabled = Selected is not null;
        if (Selected is not { } template)
        {
            Details.Children.Add(new TextBlock { Text = "No templates yet. Open a view and use Create from View on the View tab.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            return;
        }

        _loading = true;
        var name = new TextBox { Text = template.Name, Margin = new Thickness(0, 0, 0, 10) };
        name.LostFocus += (_, _) =>
        {
            var text = name.Text.Trim();
            if (text.Length > 0 && text != template.Name) Change(t => t.Name = text, "Rename View Template");
        };
        Details.Children.Add(Label("Name"));
        Details.Children.Add(name);

        var governed = ViewTemplates.Governed(_document, template).Select(view => view.TitleIn(_document)).ToList();
        Details.Children.Add(new TextBlock
        {
            Text = $"For {Kind(template.For).ToLowerInvariant()}. " + (governed.Count == 0 ? "It governs no view yet." : $"It governs {string.Join(", ", governed)}."),
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 12)
        });

        if (template.For != ViewTemplateKind.ThreeD)
        {
            var scales = ViewScale.Common.Select(d => new ViewScale(d)).ToList();
            var picker = new ComboBox { ItemsSource = scales.Select(s => s.ToString()).ToList(), SelectedIndex = Math.Max(0, scales.FindIndex(s => s.Denominator == template.Scale)), Width = 120 };
            picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex >= 0) Change(t => t.Scale = scales[picker.SelectedIndex].Denominator, "View Template Scale"); };
            Row("View Scale", template.IncludeScale, on => Change(t => t.IncludeScale = on, "View Template Scale"), picker);
        }

        if (template.For == ViewTemplateKind.Plan)
        {
            var detail = new ComboBox { ItemsSource = EnumText.Choices<DetailLevel>(), SelectedItem = EnumText.Humanise(template.DetailLevel), Width = 120 };
            detail.SelectionChanged += (_, _) =>
            {
                if (detail.SelectedItem is string text && EnumText.TryParse<DetailLevel>(text, out var level)) Change(t => t.DetailLevel = level, "View Template Detail Level");
            };
            Row("Detail Level", template.IncludeDetailLevel, on => Change(t => t.IncludeDetailLevel = on, "View Template Detail Level"), detail);

            var joins = new ComboBox { ItemsSource = new[] { "Clean all wall joins", "Clean same type wall joins" }, Width = 190,
                SelectedIndex = template.JoinDisplay == WallJoinDisplay.CleanAllWallJoins ? 0 : 1 };
            joins.SelectionChanged += (_, _) => Change(t => t.JoinDisplay = joins.SelectedIndex == 0 ? WallJoinDisplay.CleanAllWallJoins : WallJoinDisplay.CleanSameTypeWallJoins, "View Template Wall Joins");
            Row("Wall Join Display", template.IncludeJoinDisplay, on => Change(t => t.IncludeJoinDisplay = on, "View Template Wall Joins"), joins);
        }

        // The walls shown, by function: ticked, shown.
        var walls = new WrapPanel();
        foreach (var function in Enum.GetValues<WallFunction>())
        {
            var shown = new CheckBox { Content = EnumText.Humanise(function), IsChecked = !template.HiddenWallFunctions.Contains(function), Margin = new Thickness(0, 0, 12, 4) };
            shown.Click += (_, _) => Change(t =>
            {
                if (shown.IsChecked == true) t.HiddenWallFunctions.Remove(function);
                else t.HiddenWallFunctions.Add(function);
            }, "View Template Walls Shown");
            walls.Children.Add(shown);
        }

        Row("Walls Shown", template.IncludeWallFunctions, on => Change(t => t.IncludeWallFunctions = on, "View Template Walls Shown"), walls);

        if (template.For == ViewTemplateKind.ThreeD)
        {
            var style = new ComboBox { ItemsSource = EnumText.Choices<VisualStyle>(), SelectedItem = EnumText.Humanise(template.VisualStyle), Width = 140 };
            style.SelectionChanged += (_, _) =>
            {
                if (style.SelectedItem is string text && EnumText.TryParse<VisualStyle>(text, out var chosen)) Change(t => t.VisualStyle = chosen, "View Template Visual Style");
            };
            Row("Visual Style", template.IncludeVisualStyle, on => Change(t => t.IncludeVisualStyle = on, "View Template Visual Style"), style);

            var kinds = new WrapPanel();
            foreach (var kind in Kinds)
            {
                var shown = new CheckBox { Content = EnumText.Humanise(kind), IsChecked = !template.HiddenKinds.Contains(kind), Margin = new Thickness(0, 0, 12, 4) };
                shown.Click += (_, _) => Change(t =>
                {
                    if (shown.IsChecked == true) t.HiddenKinds.Remove(kind);
                    else t.HiddenKinds.Add(kind);
                }, "View Template Shown");
                kinds.Children.Add(shown);
            }

            Row("Shown", template.IncludeHiddenKinds, on => Change(t => t.IncludeHiddenKinds = on, "View Template Shown"), kinds);
        }

        _loading = false;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    /// <summary>A setting: its Include tick, its name, and what it is set to - greyed while not included.</summary>
    private void Row(string name, bool included, Action<bool> include, FrameworkElement editor)
    {
        var tick = new CheckBox { Content = "Include", IsChecked = included, VerticalAlignment = VerticalAlignment.Center, Width = 80 };
        tick.Click += (_, _) => include(tick.IsChecked == true);
        editor.IsEnabled = included;
        editor.Margin = new Thickness(0, 4, 0, 0);

        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(tick);
        Grid.SetColumn(editor, 1);
        row.Children.Add(heading);
        row.Children.Add(editor);
        Details.Children.Add(row);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } template || ElementCopy.Clone(template) is not ViewTemplate copy) return;
        copy.Name = ViewTemplates.NextName(_document, template.For);
        _history.Execute(new AddElementCommand(_document, copy, "Duplicate View Template"));
        Changed?.Invoke(this, EventArgs.Empty);
        Refresh(copy);
    }

    /// <summary>A template deleted: the views it governed keep their settings, governed by nothing.</summary>
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } template) return;
        var governed = ViewTemplates.Governed(_document, template).ToList();
        _history.Execute(new CompositeCommand("Delete View Template", governed
            .Select(view => (IUndoableCommand)ViewTemplates.Apply(_document, null, view, govern: true))
            .Append(new DeleteElementCommand(_document, template))));
        Changed?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
