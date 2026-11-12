using System.Windows;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.UI;

/// <summary>Floor plans in windows of their own: opened from the plan's bar, kept in step with the project, closed with it.</summary>
public partial class MainWindow
{
    private readonly List<PlanWindow> _planWindows = new();

    /// <summary>The plans open in windows of their own.</summary>
    internal IReadOnlyList<PlanWindow> PlanWindows => _planWindows;

    private void OnPlanOwnWindow(object sender, RoutedEventArgs e) => OpenPlanWindow(Plan.ActiveLevelId);

    /// <summary>
    /// A storey's plan in a window of its own - on the other screen where there is one - the main window keeping the plan it has.
    /// Internal so tests and the harness can open one.
    /// </summary>
    internal PlanWindow? OpenPlanWindow(Guid levelId)
    {
        if (Plan.Drafting || _document.FindLevel(levelId) is not { } level)
        {
            StatusHint.Text = "Open a floor plan first: a drafting view has no storey to show in a window of its own.";
            return null;
        }

        var window = new PlanWindow(this, levelId) { WindowStartupLocation = WindowStartupLocation.Manual };
        if (IsLoaded) window.Owner = this;
        var primary = SystemParameters.PrimaryScreenWidth;
        var offset = 30 * _planWindows.Count;
        if (SystemParameters.VirtualScreenWidth > primary + 200)
        {
            window.Left = (SystemParameters.VirtualScreenLeft < 0 ? SystemParameters.VirtualScreenLeft + 60 : primary + 60) + offset;
            window.Top = Math.Max(SystemParameters.VirtualScreenTop, 0) + 60 + offset;
        }
        else
        {
            window.Left = Math.Max(0, Left + ActualWidth - window.Width - 60 - offset);
            window.Top = Math.Max(0, Top + 120 + offset);
        }

        window.Closed += (_, _) => _planWindows.Remove(window);
        _planWindows.Add(window);
        window.Show();
        StatusHint.Text = $"{level.Name}'s plan is in a window of its own: pick and drag there as here - what is picked there has its Properties here. The drawing tools stay with this plan.";
        return window;
    }

    /// <summary>What was picked in a plan window, picked here too: its Properties, its tab, Delete and the rest work on it.</summary>
    internal void PickedElsewhere(IReadOnlyList<Element> picked)
    {
        if (picked.Count == 0) Plan.Select(null);
        else Plan.SelectMany(picked);
    }

    /// <summary>Something changed in a plan window: every view here follows it.</summary>
    internal void ChangedElsewhere() => AfterHistoryChange();

    /// <summary>A storey shown in the main window's plan, as picking it from the storeys there does.</summary>
    internal void ShowStorey(Guid levelId)
    {
        if (_document.FindLevel(levelId) is not { } level) return;
        LevelPicker.SelectedItem = level;
        Activate();
    }

    /// <summary>Every plan window closed: a project opened or made in place of this one.</summary>
    private void ClosePlanWindows()
    {
        foreach (var window in _planWindows.ToList()) window.Close();
        _planWindows.Clear();
    }
}
