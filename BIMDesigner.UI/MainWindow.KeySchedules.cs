using System.Windows;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;

namespace BIMDesigner.UI;

/// <summary>
/// Key schedules in the schedule panel and Properties: a key schedule's keys added and taken out,
/// and an element's fields that its key gives it shown and not typed into.
/// </summary>
public partial class MainWindow
{
    /// <summary>An element's parameters, those its keys give it made read only: they follow the key.</summary>
    private IEnumerable<ParameterValue> KeyLocked(Element element, IEnumerable<ParameterValue> parameters)
    {
        var locked = KeySchedules.Locked(_document, element);
        return locked.Count == 0
            ? parameters
            : parameters.Select(parameter => locked.Contains(parameter.Name) && !parameter.IsReadOnly ? ParameterValue.ReadOnly(parameter.Definition, () => parameter.Value) : parameter);
    }

    /// <summary>The key buttons shown where the schedule is a key schedule.</summary>
    private void ShowKeyButtons(ScheduleDefinition? schedule) =>
        KeyButtons.Visibility = schedule is { IsKeySchedule: true } ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddKey(object sender, RoutedEventArgs e)
    {
        if (SchedulePicker.SelectedItem is not ScheduleDefinition { IsKeySchedule: true } schedule) return;
        var command = KeySchedules.AddKey(schedule);
        _history.Execute(command);
        RefreshSchedule();
        StatusHint.Text = $"{command.Name.Replace("New Key ", "")}: a new {schedule.KeyName}. Type its name and what it gives each field into its row.";
    }

    private void OnDeleteKey(object sender, RoutedEventArgs e)
    {
        if (SchedulePicker.SelectedItem is not ScheduleDefinition { IsKeySchedule: true } schedule) return;
        if (ScheduleGrid.SelectedItem is not ViewModels.ScheduleRowView { Element: ScheduleKey key })
        {
            StatusHint.Text = "Pick the key to delete in the schedule first.";
            return;
        }

        _history.Execute(KeySchedules.DeleteKey(_document, key));
        RefreshSchedule();
        RefreshProperties();
        StatusHint.Text = $"{key.Name} deleted: what had it keeps its values, with no {schedule.KeyName}.";
    }
}
