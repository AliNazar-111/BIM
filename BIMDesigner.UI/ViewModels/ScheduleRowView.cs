using System.ComponentModel;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;

namespace BIMDesigner.UI.ViewModels;

/// <summary>A schedule cell that was edited, and what it held either side of the change.</summary>
public sealed class ScheduleCellEditedEventArgs : EventArgs
{
    public ScheduleCellEditedEventArgs(ParameterValue parameter, object? oldValue, object? newValue)
    {
        Parameter = parameter;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public ParameterValue Parameter { get; }

    public object? OldValue { get; }

    public object? NewValue { get; }
}

/// <summary>
/// One row of the schedule grid.
///
/// Its cells are the element's own live parameters, so typing into the table writes to the
/// model - the bidirectional editing section 6.4 asks for. A schedule is a view of the
/// building, not a report about it, and the difference shows the moment someone corrects a
/// door number in the table and expects the door to change.
/// </summary>
public sealed class ScheduleRowView : INotifyPropertyChanged
{
    private readonly ScheduleRow _row;

    public ScheduleRowView(ScheduleRow row, string? groupField)
    {
        _row = row;
        GroupKey = string.IsNullOrWhiteSpace(groupField) ? string.Empty : row.Text(groupField);
    }

    public Element Element => _row.Element;

    /// <summary>What the grid groups on, when the schedule groups at all.</summary>
    public string GroupKey { get; }

    /// <summary>
    /// Indexed by field name so the grid can bind columns it only learns about at runtime.
    /// A rejected edit leaves the model alone and the cell reverts.
    /// </summary>
    public string this[string field]
    {
        get => _row.Text(field);
        set
        {
            var cell = _row[field];
            if (cell is null || cell.IsReadOnly) return;

            var before = cell.Value;
            var accepted = cell.TrySetFromText(value);

            Refresh();

            if (accepted && !Equals(before, cell.Value))
                CellEdited?.Invoke(this, new ScheduleCellEditedEventArgs(cell, before, cell.Value));
        }
    }

    /// <summary>Raised after an edit the model accepted, so it can be recorded and redrawn.</summary>
    public event EventHandler<ScheduleCellEditedEventArgs>? CellEdited;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tells the grid every cell may have changed - one edit can move several.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
