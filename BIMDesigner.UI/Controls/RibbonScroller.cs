using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The ribbon's panels, scrolled sideways when they are wider than the window: an arrow at
/// either end while there is more that way, and the mouse wheel over the ribbon scrolls it -
/// so no panel is ever cut off on a small screen. Its look is the RibbonScroller style.
/// </summary>
public sealed class RibbonScroller : ContentControl
{
    /// <summary>How far one click of an arrow scrolls, in pixels: about a button.</summary>
    private const double Step = 60;

    private ScrollViewer? _scroll;
    private ButtonBase? _left;
    private ButtonBase? _right;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        if (_left is not null) _left.Click -= OnLeft;
        if (_right is not null) _right.Click -= OnRight;

        _scroll = GetTemplateChild("PART_Scroll") as ScrollViewer;
        _left = GetTemplateChild("PART_Left") as ButtonBase;
        _right = GetTemplateChild("PART_Right") as ButtonBase;

        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        if (_left is not null) _left.Click += OnLeft;
        if (_right is not null) _right.Click += OnRight;
        ShowArrows();
    }

    /// <summary>Whether the panels are wider than the ribbon, so some are scrolled out of sight.</summary>
    public bool Overflows => _scroll is { ScrollableWidth: > 0.5 };

    /// <summary>Back to the first panel - when another tab is opened.</summary>
    public void ScrollToStart() => _scroll?.ScrollToLeftEnd();

    /// <summary>Scrolls by some pixels, positive to the right. Public so tests can drive it.</summary>
    public void ScrollBy(double pixels) => _scroll?.ScrollToHorizontalOffset(_scroll.HorizontalOffset + pixels);

    private void OnLeft(object sender, RoutedEventArgs e) => ScrollBy(-Step);

    private void OnRight(object sender, RoutedEventArgs e) => ScrollBy(Step);

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => ShowArrows();

    private void ShowArrows()
    {
        if (_scroll is null) return;
        if (_left is not null) _left.Visibility = _scroll.HorizontalOffset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        if (_right is not null) _right.Visibility = _scroll.HorizontalOffset < _scroll.ScrollableWidth - 0.5 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The wheel over the ribbon scrolls it sideways - but a drop-down list in it keeps its own wheel.</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled || !Overflows || Within<ComboBox>(e.OriginalSource as DependencyObject)) return;

        ScrollBy(-e.Delta);
        e.Handled = true;
    }

    private static bool Within<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is T) return true;

        return false;
    }
}
