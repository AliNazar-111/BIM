using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// The views open, each a tab over the drawing area, as they are opened: the floor plans and
/// services plans, the 3D views, elevations and walkthroughs, the sections, callouts and interior
/// elevations, the sheets. Those in sight are lit; a tab picked brings its view in front; one closed
/// puts its view away; Close Inactive closes the rest. And the 3D view in a window of its own, to
/// put on a second screen while the plan keeps this one.
/// </summary>
public partial class MainWindow
{
    /// <summary>A view open: what it is known by, its title, what kind it is, how it is brought back, whether it still is one, and how it is put away.</summary>
    private sealed record ViewTab(string Key, string Title, ViewTabKind Kind, Action Show, Func<bool> Exists, Action PutAway);

    private enum ViewTabKind
    {
        Plan,
        Model,
        Section,
        Sheet
    }

    private readonly List<ViewTab> _viewTabs = new();

    /// <summary>The tabs as last drawn, to draw them again only when they change.</summary>
    private string _tabsDrawn = string.Empty;

    private bool _tabsPending;

    /// <summary>After any change to what is laid out, once things settle: the tabs follow what is in sight.</summary>
    private void QueueViewTabs()
    {
        if (_tabsPending) return;
        _tabsPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _tabsPending = false;
            RefreshViewTabs();
        }));
    }

    /// <summary>The tabs of the views open, by title - those in sight first in each pair. For the harness and tests.</summary>
    internal IReadOnlyList<(string Title, bool Shown)> OpenViews()
    {
        RefreshViewTabs();
        var shown = ShownViews().Select(view => view.Key).ToHashSet();
        return _viewTabs.Select(tab => (tab.Title, shown.Contains(tab.Key))).ToList();
    }

    /// <summary>A tab picked by its title: its view brought in front. For the harness and tests.</summary>
    internal bool PickViewTab(string title)
    {
        RefreshViewTabs();
        if (_viewTabs.FirstOrDefault(tab => tab.Title == title) is not { } tab) return false;
        tab.Show();
        RefreshViewTabs();
        return true;
    }

    /// <summary>A tab closed by its title. For the harness and tests.</summary>
    internal bool CloseViewTab(string title)
    {
        RefreshViewTabs();
        if (_viewTabs.FirstOrDefault(tab => tab.Title == title) is not { } tab) return false;
        CloseViewTab(tab);
        return true;
    }

    /// <summary>The views in sight now: the plan unless something covers it, the 3D view, the section or interior elevation under the plan, a sheet.</summary>
    private IEnumerable<ViewTab> ShownViews()
    {
        if (SheetPanel.Visibility != Visibility.Visible && !ModelCoversPlan && PlanTab() is { } plan) yield return plan;
        if (ModelPanel.Visibility == Visibility.Visible) yield return ModelTab();
        if (SectionPanel.Visibility == Visibility.Visible && SectionTab() is { } section) yield return section;
        if (SheetPanel.Visibility == Visibility.Visible && SheetSurface.Sheet is { } sheet) yield return SheetTab(sheet);
    }

    private ViewTab? PlanTab()
    {
        var level = _document.FindLevel(Plan.ActiveLevelId);
        var discipline = Plan.ActiveDiscipline;
        if (Plan.ActiveCallout is { } callout)
            return new ViewTab($"callout:{callout.Id}", PlanTitle.Text, ViewTabKind.Plan, () => ShowCallout(callout), () => _document.Elements.Contains(callout), () => { });
        if (level is null) return null;
        return new ViewTab($"plan:{level.Id}:{discipline}", PlanTitle.Text, ViewTabKind.Plan, () => ShowServicesPlan(level, discipline), () => _document.Levels.Contains(level), () => { });
    }

    private ViewTab ModelTab()
    {
        var title = ModelCaption.Text;
        void PutAway()
        {
            if (ModelPanel.Visibility == Visibility.Visible && ModelCaption.Text == title) OnClose3D(this, new RoutedEventArgs());
        }

        if (Model3D.Elevation is { } side) return new ViewTab($"elevation:{side}", title, ViewTabKind.Model, () => ShowElevation(side), () => true, PutAway);
        if (_shownWalkthrough is { } walk) return new ViewTab($"walk:{walk.Id}", title, ViewTabKind.Model, () => ShowWalkthrough(walk), () => _document.Elements.Contains(walk), PutAway);
        var view = Model3D.View;
        return new ViewTab($"3d:{view.Id}", title, ViewTabKind.Model, () => Show3D(view), () => IsKept(view), PutAway);
    }

    private ViewTab? SectionTab()
    {
        if (Projected.Visibility == Visibility.Visible && Projected.View is { } interior)
            return new ViewTab($"interior:{interior}", interior.TitleIn(_document), ViewTabKind.Section, () => ShowInteriorElevation(interior), () => interior.ExistsIn(_document), () => OnCloseSection(this, new RoutedEventArgs()));
        if (Section.Visibility != Visibility.Visible || Section.Marker is not { } marker) return null;
        if (Section.Callout is { } callout)
            return new ViewTab($"callout:{callout.Id}", callout.Name, ViewTabKind.Section, () => ShowCallout(callout), () => _document.Elements.Contains(callout), () => OnCloseSection(this, new RoutedEventArgs()));
        return new ViewTab($"section:{marker.Id}", $"Section: {marker.Name}", ViewTabKind.Section, () => ShowSection(marker), () => _document.Elements.Contains(marker), () => OnCloseSection(this, new RoutedEventArgs()));
    }

    private ViewTab SheetTab(Sheet sheet) =>
        new($"sheet:{sheet.Id}", $"Sheet: {sheet.Number} - {sheet.Name}", ViewTabKind.Sheet, () => ShowSheet(sheet), () => _document.Elements.Contains(sheet), () => OnCloseSheet(this, new RoutedEventArgs()));

    /// <summary>The tabs brought up to date: a view in sight that has none gets one, at the end; one whose view has gone is closed; those in sight are lit.</summary>
    private void RefreshViewTabs()
    {
        if (ViewTabs is null) return;
        var shown = ShownViews().ToList();
        foreach (var view in shown)
        {
            var at = _viewTabs.FindIndex(tab => tab.Key == view.Key);
            if (at < 0) _viewTabs.Add(view);
            else _viewTabs[at] = view;
        }

        _viewTabs.RemoveAll(tab => !tab.Exists());
        var lit = shown.Select(view => view.Key).ToHashSet();
        var drawn = string.Join("|", _viewTabs.Select(tab => $"{tab.Key}={tab.Title}={lit.Contains(tab.Key)}"));
        if (_modelWindow is { } floating) floating.Title = $"{ModelCaption.Text} - BIMDesigner";
        if (drawn == _tabsDrawn) return;
        _tabsDrawn = drawn;

        ViewTabs.Children.Clear();
        foreach (var tab in _viewTabs) ViewTabs.Children.Add(TabFor(tab, lit.Contains(tab.Key)));
    }

    /// <summary>A tab: its view's kind and title, lit when in sight, a cross to close it.</summary>
    private FrameworkElement TabFor(ViewTab tab, bool inSight)
    {
        var icon = new Image
        {
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0),
            Source = (ImageSource)FindResource(tab.Kind switch
            {
                ViewTabKind.Model => "Icon.View3D",
                ViewTabKind.Section => "Icon.Section",
                ViewTabKind.Sheet => "Icon.Sheet",
                _ => "Icon.Plan"
            })
        };
        var title = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, inSight ? "Text" : "Text.Muted");
        var close = new Button
        {
            Content = "✕", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(8, 0, 0, 0), FontSize = 10,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = $"Close {tab.Title}", Cursor = Cursors.Hand
        };
        close.SetResourceReference(ForegroundProperty, "Text.Muted");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseViewTab(tab);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(title);
        row.Children.Add(close);
        var border = new Border
        {
            Child = row, Padding = new Thickness(10, 4, 4, 4), BorderThickness = new Thickness(0, 2, 1, 0), Cursor = Cursors.Hand,
            ToolTip = inSight ? tab.Title : $"{tab.Title}: bring it in front"
        };
        border.SetResourceReference(Border.BackgroundProperty, inSight ? "Bg.Field" : "Bg.Window");
        if (inSight) border.BorderBrush = AccentBrush;
        else border.SetResourceReference(Border.BorderBrushProperty, "Line");
        border.MouseLeftButtonUp += (_, _) =>
        {
            tab.Show();
            RefreshViewTabs();
        };
        return border;
    }

    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xD6));

    /// <summary>
    /// A tab closed, and its view put away where it is in sight. The plan stays, for it is where
    /// the model is drawn: its tab closed while it is in sight brings the tab beside it forward
    /// instead - and is kept, where there is no other.
    /// </summary>
    private void CloseViewTab(ViewTab tab)
    {
        var inSight = ShownViews().Any(view => view.Key == tab.Key);
        if (tab.Kind == ViewTabKind.Plan && inSight)
        {
            var at = _viewTabs.IndexOf(tab);
            var other = _viewTabs.Where(each => each != tab).OrderBy(each => Math.Abs(_viewTabs.IndexOf(each) - at)).FirstOrDefault();
            if (other is null)
            {
                StatusHint.Text = "The plan stays open: it is where the model is drawn. Open another view to close this one.";
                return;
            }

            _viewTabs.Remove(tab);
            other.Show();
            RefreshViewTabs();
            return;
        }

        _viewTabs.Remove(tab);
        if (inSight) tab.PutAway();
        RefreshViewTabs();
    }

    private void OnCloseInactiveViews(object sender, RoutedEventArgs e)
    {
        var lit = ShownViews().Select(view => view.Key).ToHashSet();
        var closed = _viewTabs.RemoveAll(tab => !lit.Contains(tab.Key));
        RefreshViewTabs();
        StatusHint.Text = closed == 0 ? "Every view open is in sight." : $"{closed} {(closed == 1 ? "view" : "views")} closed.";
    }

    /// <summary>Another project opened: none of the last one's views are open.</summary>
    private void ForgetViewTabs()
    {
        _viewTabs.Clear();
        _tabsDrawn = string.Empty;
        DockModel();
        QueueViewTabs();
    }

    // ---- the 3D view in a window of its own ----------------------------------------------

    private System.Windows.Window? _modelWindow;

    /// <summary>Whether the 3D view is in a window of its own.</summary>
    private bool ModelFloats => _modelWindow is not null;

    private void OnFloatModel(object sender, RoutedEventArgs e)
    {
        if (ModelFloats) DockModel();
        else FloatModel();
    }

    /// <summary>
    /// The 3D view taken out into a window of its own - on the second screen, where there is one -
    /// to orbit while the plan has this one. Undo and redo work from it as from here; closed, the
    /// 3D view closes; Dock puts it back.
    /// </summary>
    internal System.Windows.Window FloatModel()
    {
        if (_modelWindow is { } open)
        {
            open.Activate();
            return open;
        }

        if (_tiled) SetTiledKeeping3D(false);
        if (ModelPanel.Visibility != Visibility.Visible) Show3D();
        ViewArea.Children.Remove(ModelPanel);

        var window = new ModelWindow
        {
            Title = $"{ModelCaption.Text} - BIMDesigner", Width = Math.Max(640, ActualWidth * 0.6), Height = Math.Max(480, ActualHeight * 0.7),
            ShowInTaskbar = true, Icon = Icon, WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.SetResourceReference(BackgroundProperty, "Bg.Window");
        window.SetResourceReference(ForegroundProperty, "Text");
        window.SetResourceReference(FontFamilyProperty, "UiFont");
        window.SetResourceReference(FontSizeProperty, "UiFontSize");
        window.CommandBindings.AddRange(CommandBindings);
        if (IsLoaded) window.Owner = this;

        // On the other screen where there is one; beside this window where there is not.
        var primary = SystemParameters.PrimaryScreenWidth;
        if (SystemParameters.VirtualScreenWidth > primary + 200)
        {
            window.Left = SystemParameters.VirtualScreenLeft < 0 ? SystemParameters.VirtualScreenLeft + 40 : primary + 40;
            window.Top = Math.Max(SystemParameters.VirtualScreenTop, 0) + 40;
        }
        else
        {
            window.Left = Math.Max(0, Left + ActualWidth - window.Width - 40);
            window.Top = Math.Max(0, Top + 80);
        }

        window.Content = ModelPanel;
        window.Closed += (_, _) =>
        {
            // Closed by its own close box: the 3D view put back, and closed.
            if (!ReferenceEquals(_modelWindow, window)) return;
            PutModelBack(window);
            OnClose3D(this, new RoutedEventArgs());
        };

        _modelWindow = window;
        FloatModelButton.Content = "Dock";
        FloatModelButton.ToolTip = "Put the 3D view back in the main window";
        window.Show();
        StatusHint.Text = SystemParameters.VirtualScreenWidth > primary + 200
            ? "The 3D view is on the other screen: orbit it there while you draw here. Dock puts it back."
            : "The 3D view is in a window of its own: move it where it suits, or make it as big as the screen. Dock puts it back.";
        QueueViewTabs();
        return window;
    }

    /// <summary>The 3D view put back in the main window, open as it was.</summary>
    internal void DockModel()
    {
        if (_modelWindow is not { } window) return;
        PutModelBack(window);
        window.Close();
        StatusHint.Text = "The 3D view is back in the main window.";
        QueueViewTabs();
    }

    private void PutModelBack(System.Windows.Window window)
    {
        _modelWindow = null;
        window.Content = null;
        if (!ViewArea.Children.Contains(ModelPanel)) ViewArea.Children.Add(ModelPanel);
        Grid.SetColumn(ModelPanel, _tiled ? 2 : 0);
        FloatModelButton.Content = "Own Window";
        FloatModelButton.ToolTip = "The 3D view in a window of its own - to put on a second screen, or make as big as the screen - while the plan has this one; Dock puts it back";
    }

    /// <summary>Untiled without closing the 3D view: it is going into a window of its own.</summary>
    private void SetTiledKeeping3D(bool tiled)
    {
        _tiled = tiled;
        TileToggle.IsChecked = tiled;
        Grid.SetColumn(ModelPanel, tiled ? 2 : 0);
        TileColumn.Width = tiled ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TileSplitterColumn.Width = tiled ? new GridLength(4) : new GridLength(0);
        TileSplitter.Visibility = tiled ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>The window the 3D view is taken out into, to put on a second screen.</summary>
internal sealed class ModelWindow : System.Windows.Window
{
}
