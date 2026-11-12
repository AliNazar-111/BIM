using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Elements;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.UI;

/// <summary>
/// A floor plan in a window of its own - another storey beside the one drawn on, or the same one larger on a second screen. It
/// draws the same project and follows every change, whoever makes it; what is picked in it is picked in the main window too, so
/// its Properties, its tab, Delete and the rest work on it there; things are moved in it by dragging, and undone from it as from
/// the main window. The drawing tools stay with the main window's plan.
/// </summary>
internal sealed class PlanWindow : Window
{
    private readonly MainWindow _main;
    private readonly ComboBox _storeys = new() { MinWidth = 170, Margin = new Thickness(0, 0, 8, 0), ToolTip = "The storey this window shows" };
    private readonly TextBlock _hint = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    private bool _choosing;

    public PlanWindow(MainWindow main, Guid levelId)
    {
        _main = main;
        Plan = new PlanView { Document = main.Document, History = main.History, ActiveLevelId = levelId, Focusable = true };
        SetResourceReference(BackgroundProperty, "Bg.Window");
        SetResourceReference(ForegroundProperty, "Text");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(FontSizeProperty, "UiFontSize");
        Width = Math.Max(640, main.ActualWidth * 0.6);
        Height = Math.Max(460, main.ActualHeight * 0.7);
        ShowInTaskbar = true;
        Icon = main.Icon;
        CommandBindings.AddRange(main.CommandBindings);

        Button Make(string text, string tip, Action act)
        {
            var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => act();
            return button;
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 6) };
        bar.Children.Add(new TextBlock { Text = "Storey", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.75 });
        bar.Children.Add(_storeys);
        bar.Children.Add(Make("Zoom to Fit", "Frame the whole storey", () => Plan.ZoomToFit()));
        bar.Children.Add(Make("Show Here", "Show this storey in the main window's plan too", () => _main.ShowStorey(Plan.ActiveLevelId)));
        DockPanel.SetDock(bar, Dock.Top);
        var foot = new Border { Child = _hint, Padding = new Thickness(10, 4, 10, 4) };
        DockPanel.SetDock(foot, Dock.Bottom);
        var dock = new DockPanel();
        dock.Children.Add(bar);
        dock.Children.Add(foot);
        dock.Children.Add(Plan);
        Content = dock;

        _storeys.SelectionChanged += (_, _) =>
        {
            if (_choosing || _storeys.SelectedItem is not Level level || level.Id == Plan.ActiveLevelId) return;
            Plan.ActiveLevelId = level.Id;
            Plan.ZoomToFit();
            Said();
        };
        Plan.SelectionChanged += (_, _) => _main.PickedElsewhere(Plan.SelectedElements);
        Plan.ModelChanged += (_, _) => _main.ChangedElsewhere();
        Plan.HintChanged += (_, hint) => _hint.Text = hint;
        PreviewKeyDown += OnKey;
        main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => main.History.Changed -= OnHistoryChanged;
        Loaded += (_, _) =>
        {
            Plan.ZoomToFit();
            Plan.Focus();
        };
        Said();
    }

    /// <summary>The plan it shows.</summary>
    public PlanView Plan { get; }

    private void OnHistoryChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

    /// <summary>Drawn again as the project now stands: a storey since taken away gives way to the first.</summary>
    public void Refresh()
    {
        var document = _main.Document;
        if (!ReferenceEquals(Plan.Document, document)) Plan.Document = document;
        if (document.FindLevel(Plan.ActiveLevelId) is null && document.Levels.FirstOrDefault() is { } first) Plan.ActiveLevelId = first.Id;
        if (Plan.SelectedElements.Any(element => !document.Elements.Contains(element))) Plan.Select(null);
        Plan.RefreshModel();
        Said();
    }

    /// <summary>Its title and storey picker, as the project names its storeys.</summary>
    private void Said()
    {
        var document = _main.Document;
        var level = document.FindLevel(Plan.ActiveLevelId);
        Title = $"Floor Plan: {level?.Name ?? "?"} - BIMDesigner";
        _choosing = true;
        _storeys.ItemsSource = document.Levels.ToList();
        _storeys.DisplayMemberPath = nameof(Level.Name);
        _storeys.SelectedItem = level;
        _choosing = false;
    }

    /// <summary>Esc stops what is half done, then lets go of what is picked; the rest - Delete, Ctrl+Z - go through the main window's commands.</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (!Plan.CancelPendingOperation()) Plan.Select(null);
        e.Handled = true;
    }
}
