using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BIMDesigner.UI;

/// <summary>
/// The history to go back through: the arrow beside Undo lists the steps there are to undo, the
/// latest first, and pointing at one lights it and every step after it - all undone together at
/// a click. The arrow beside Redo lists what was undone, to bring back the same way.
/// </summary>
public partial class MainWindow
{
    private Popup? _historyList;

    /// <summary>The longest list shown: further back than this is undone a list at a time.</summary>
    private const int HistoryShown = 100;

    /// <summary>The history arrows usable only while there is a step to list.</summary>
    private void RefreshHistoryLists()
    {
        if (QuickUndoList is null) return;
        QuickUndoList.IsEnabled = _history.CanUndo;
        QuickRedoList.IsEnabled = _history.CanRedo;
    }

    private void OnUndoList(object sender, RoutedEventArgs e) => ShowHistoryList(undo: true);

    private void OnRedoList(object sender, RoutedEventArgs e) => ShowHistoryList(undo: false);

    /// <summary>
    /// The list of steps to undo, or to redo, opened under its arrow. Internal so the harness can
    /// open it.
    /// </summary>
    internal Popup? ShowHistoryList(bool undo)
    {
        _historyList?.SetCurrentValue(Popup.IsOpenProperty, false);

        // A sketch keeps its own lines' history: the model's steps are not undone from inside it.
        if (Plan.IsSketching)
        {
            StatusHint.Text = "In a sketch, Undo takes its lines back one at a time. Finish or cancel the sketch to go back through the model's history.";
            return null;
        }

        var names = undo ? _history.UndoNames : _history.RedoNames;
        if (names.Count == 0) return null;

        var list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            MaxHeight = 380,
            MinWidth = 220,
            BorderThickness = new Thickness(0)
        };
        foreach (var name in names.Take(HistoryShown)) list.Items.Add(new ListBoxItem { Content = name, Padding = new Thickness(8, 3, 16, 3) });

        var said = new TextBlock { Margin = new Thickness(8, 5, 8, 6) };
        said.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");

        // Pointing at a step lights it and every one after it: they go together.
        void Reach(int count)
        {
            for (var index = 0; index < list.Items.Count; index++)
                ((ListBoxItem)list.Items[index]).IsSelected = index < count;
            said.Text = count == 0
                ? names.Count > HistoryShown ? $"{names.Count} steps; the latest {HistoryShown} listed." : "Point at a step."
                : $"{(undo ? "Undo" : "Redo")} {count} {(count == 1 ? "step" : "steps")}";
        }

        int Under(MouseEventArgs args) =>
            ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) is ListBoxItem item ? list.Items.IndexOf(item) + 1 : 0;

        list.MouseMove += (_, args) => Reach(Under(args));
        list.PreviewMouseLeftButtonUp += (_, args) =>
        {
            var count = Under(args);
            if (count == 0) return;
            args.Handled = true;
            CloseHistoryList();
            StepHistory(undo, count);
        };
        list.PreviewKeyDown += (_, args) =>
        {
            var reached = list.SelectedItems.Count;
            switch (args.Key)
            {
                case Key.Down:
                    Reach(Math.Min(list.Items.Count, reached + 1));
                    break;
                case Key.Up:
                    Reach(Math.Max(1, reached - 1));
                    break;
                case Key.Enter when reached > 0:
                    CloseHistoryList();
                    StepHistory(undo, reached);
                    break;
                case Key.Escape:
                    CloseHistoryList();
                    break;
                default:
                    return;
            }

            args.Handled = true;
        };

        var panel = new DockPanel();
        DockPanel.SetDock(said, Dock.Bottom);
        panel.Children.Add(said);
        panel.Children.Add(list);

        var frame = new Border { Child = panel, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");

        _historyList = new Popup
        {
            PlacementTarget = undo ? QuickUndo : QuickRedo,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = frame
        };
        _historyList.Closed += (_, _) => _historyList = null;
        Reach(1);
        _historyList.IsOpen = true;
        list.Focus();
        return _historyList;
    }

    private void CloseHistoryList()
    {
        var open = _historyList;
        _historyList = null;
        if (open is not null) open.IsOpen = false;
    }

    /// <summary>
    /// Several steps undone, or redone, together - and the plan, the panels and every view
    /// brought up to where the history now stands, once.
    /// </summary>
    internal void StepHistory(bool undo, int count)
    {
        if (count <= 0) return;
        var before = _history.Depth;
        try
        {
            if (undo) _history.Undo(count);
            else _history.Redo(count);
        }
        finally
        {
            AfterHistoryChange();
            var moved = Math.Abs(_history.Depth - before);
            StatusHint.Text = $"{(undo ? "Undone" : "Redone")}: {moved} {(moved == 1 ? "step" : "steps")}.";
        }
    }
}
