using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// Planning Drawings: which property line is the application site, edged in red, and which are
/// other land the applicant owns, in blue; the scales the location plan and block plan are drawn
/// at, and the line the map is credited with; Make Drawings puts each on a sheet of its own, or
/// brings them up to date - one step to undo; and what the drawings are asked for, each ticked or
/// crossed, as the model now stands.
/// </summary>
internal sealed class PlanningWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly ComboBox _site = new() { MinWidth = 280 };
    private readonly StackPanel _others = new();
    private readonly ComboBox _locationScale = new() { MinWidth = 110 };
    private readonly ComboBox _blockScale = new() { MinWidth = 110 };
    private readonly TextBox _credit = new() { MinWidth = 420, Padding = new Thickness(4, 2, 4, 2) };
    private readonly StackPanel _checks = new() { Margin = new Thickness(16, 0, 16, 8) };
    private readonly TextBlock _said = new();
    private readonly Button _make = new() { Content = "Make Drawings", IsDefault = true };
    private bool _filling;

    public PlanningWindow(MainWindow main)
    {
        _main = main;
        var us = main.Document.ProjectInformation.ClearanceStandard.IsUs();
        DialogLook.Apply(this, "Planning Drawings", 760, 680);
        MinWidth = 560;
        MinHeight = 480;

        foreach (var (box, drawing) in new[] { (_locationScale, PlanningDrawing.Location), (_blockScale, PlanningDrawing.Block) })
        {
            foreach (var scale in PlanningDrawings.Scales(drawing, us)) box.Items.Add(new ViewScale(scale));
            box.SelectedIndex = 0;
        }

        _site.SelectionChanged += (_, _) =>
        {
            if (_filling) return;
            SetEdges();
        };

        var grid = new Grid { Margin = new Thickness(16, 0, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, UIElement field)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 12, 5) };
            Grid.SetRow(text, grid.RowDefinitions.Count - 1);
            Grid.SetRow(field, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(field, 1);
            if (field is FrameworkElement element) element.Margin = new Thickness(0, 3, 0, 3);
            grid.Children.Add(text);
            grid.Children.Add(field);
        }

        Row(us ? "The site" : "Red line - the application site", _site);
        Row(us ? "Other land owned" : "Blue line - other land owned", _others);
        Row($"{PlanningDrawings.Name(PlanningDrawing.Location, us)} at", _locationScale);
        Row($"{PlanningDrawings.Name(PlanningDrawing.Block, us)} at", _blockScale);
        if (!us) Row("Map credited with", _credit);
        DockPanel.SetDock(grid, Dock.Top);

        var heading = new TextBlock { Text = "What they are asked for", FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 4, 16, 6) };
        DockPanel.SetDock(heading, Dock.Top);

        _make.Click += (_, _) => Make();
        var openLocation = new Button { Content = $"Open {PlanningDrawings.Name(PlanningDrawing.Location, us)}" };
        openLocation.Click += (_, _) => _main.OpenPlanningSheet(PlanningDrawing.Location);
        var openBlock = new Button { Content = $"Open {PlanningDrawings.Name(PlanningDrawing.Block, us)}" };
        openBlock.Click += (_, _) => _main.OpenPlanningSheet(PlanningDrawing.Block);
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, _make, openLocation, openBlock, close));
        root.Children.Add(DialogLook.Intro(us
            ? "The site plans a permit application goes in with: a vicinity map showing where the site is, and a site plan of the work on its lot - each a view of the ground floor plan cropped round the site, on a sheet of its own with a north point and a scale bar."
            : "The plans a planning application goes in with: a location plan identifying the land, edged in red, among at least two named roads and the buildings round it on up-to-date Ordnance Survey mapping, at 1:1250 or 1:2500 - and a block plan of the development within its boundaries at 1:500 or 1:200. Each is a view of the ground floor plan cropped round the site, drawing only what such a plan shows, on a sheet of its own with a north point and a scale bar."));
        root.Children.Add(grid);
        root.Children.Add(heading);
        root.Children.Add(new ScrollViewer { Content = _checks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Fill();
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) Fill();
    }

    private sealed record LineChoice(PropertyLine? Line, string Said)
    {
        public override string ToString() => Said;
    }

    /// <summary>The property lines to choose from, the scales and credit as they stand, and what is met.</summary>
    public void Fill()
    {
        var document = _main.Document;
        _filling = true;
        var lines = document.All<PropertyLine>().ToList();
        string Said(PropertyLine line, int index) => $"Property line {index + 1} - {Units.SquareMetres(Polygon2D.Area(line.Boundary), "#,0")}{(string.IsNullOrWhiteSpace(line.Mark) ? string.Empty : $" ({line.Mark})")}";

        var choices = new List<LineChoice> { new(null, lines.Count == 0 ? "None drawn yet - Site → Property Line" : "None") };
        choices.AddRange(lines.Select((line, index) => new LineChoice(line, Said(line, index))));
        _site.ItemsSource = choices;
        _site.SelectedItem = choices.FirstOrDefault(choice => choice.Line?.Planning == PlanningEdge.ApplicationSite) ?? choices[0];

        _others.Children.Clear();
        foreach (var (line, index) in lines.Select((line, index) => (line, index)).Where(entry => entry.line.Planning != PlanningEdge.ApplicationSite))
        {
            var box = new CheckBox { Content = Said(line, index), IsChecked = line.Planning == PlanningEdge.OtherLand, Margin = new Thickness(0, 2, 0, 2), Tag = line };
            box.SetResourceReference(ForegroundProperty, "Text");
            box.Click += (_, _) => SetEdges();
            _others.Children.Add(box);
        }

        if (_others.Children.Count == 0) _others.Children.Add(new TextBlock { Text = "None", Opacity = 0.7 });

        // The scales each drawing is at, where made; the credit as kept.
        foreach (var (box, drawing) in new[] { (_locationScale, PlanningDrawing.Location), (_blockScale, PlanningDrawing.Block) })
            if (PlanningDrawings.Drawing(document, drawing) is { } callout &&
                box.Items.OfType<ViewScale>().FirstOrDefault(scale => Math.Abs(scale.Denominator - document.ViewSettings.ScaleOf(Callouts.Reference(callout))) < 0.5) is var made && made.IsValid)
                box.SelectedItem = made;
        if (_credit.Text.Length == 0) _credit.Text = document.ProjectInformation.PlanningMapNote;
        _make.Content = PlanningDrawings.Drawing(document, PlanningDrawing.Location) is null ? "Make Drawings" : "Update Drawings";
        _filling = false;

        _checks.Children.Clear();
        foreach (var check in PlanningDrawings.Checks(document))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var mark = new TextBlock { Text = check.Met switch { true => "✓", false => "✗", _ => "•" }, Width = 20, FontWeight = FontWeights.Bold };
            mark.Foreground = check.Met switch
            {
                true => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0xA8, 0x4F)),
                false => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x5A, 0x4A)),
                _ => (System.Windows.Media.Brush)FindResource("Text.Muted")
            };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            row.Children.Add(new TextBlock { Text = check.Said, TextWrapping = TextWrapping.Wrap });
            _checks.Children.Add(row);
        }
    }

    /// <summary>The red and blue lines as chosen here, set on the property lines - one step to undo.</summary>
    private void SetEdges()
    {
        var document = _main.Document;
        var site = (_site.SelectedItem as LineChoice)?.Line;
        var blue = _others.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => (PropertyLine)box.Tag).ToHashSet();
        var lines = document.All<PropertyLine>().ToList();
        var before = lines.Select(line => line.Planning).ToList();
        var after = lines.Select(line => ReferenceEquals(line, site) ? PlanningEdge.ApplicationSite : blue.Contains(line) ? PlanningEdge.OtherLand : PlanningEdge.None).ToList();
        if (before.SequenceEqual(after)) return;

        _main.History.Execute(new DelegateCommand("Planning Edges",
            () => { for (var i = 0; i < lines.Count; i++) lines[i].Planning = after[i]; },
            () => { for (var i = 0; i < lines.Count; i++) lines[i].Planning = before[i]; }));
        _main.AfterPlanningChange();
    }

    /// <summary>The drawings made, or brought up to date, at the scales chosen. Public so the harness and tests can press it.</summary>
    public void Make()
    {
        var document = _main.Document;
        var location = (_locationScale.SelectedItem as ViewScale?)?.Denominator ?? PlanningDrawings.Scales(PlanningDrawing.Location, false)[0];
        var block = (_blockScale.SelectedItem as ViewScale?)?.Denominator ?? PlanningDrawings.Scales(PlanningDrawing.Block, false)[0];
        var made = PlanningDrawings.Drawing(document, PlanningDrawing.Location) is null;
        _main.History.Execute(PlanningDrawings.Make(document, location, block, _credit.Text));
        _main.AfterPlanningChange();
        Fill();
        var unmet = PlanningDrawings.Checks(document).Count(check => check.Met == false);
        var sheets = string.Join(" and ", new[] { PlanningDrawing.Location, PlanningDrawing.Block }.Select(drawing => PlanningDrawings.SheetOf(document, drawing)?.Number).OfType<string>());
        DialogLook.Say(_said, $"{(made ? "Made" : "Updated")} on {sheets}." + (unmet == 0 ? " Everything asked for is met." : $" {unmet} still to see to, above."));
    }
}
