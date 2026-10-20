using System.ComponentModel;
using System.Windows.Media;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
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

    /// <param name="heading">How the row's group is headed, given its value in the grouped field - with its subtotals, say.</param>
    /// <param name="colour">The colour a cell of the row is given by the schedule's formatting, or null.</param>
    public ScheduleRowView(ScheduleRow row, string? groupField, Func<string, string>? heading = null, Func<ScheduleRow, string, ColourRgb?>? colour = null)
    {
        _row = row;
        var key = string.IsNullOrWhiteSpace(groupField) ? string.Empty : row.Text(groupField);
        GroupKey = heading is null ? key : heading(key);
        Colours = new CellColours(field => colour?.Invoke(_row, field));
    }

    public Element Element => _row.Element;

    /// <summary>Each cell's background by its field: what conditional formatting colours it, or nothing.</summary>
    public CellColours Colours { get; private set; }

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
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Colours = new CellColours(Colours.Lookup);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Colours)));
    }

    /// <summary>A field's name as a binding path to it by index: its commas and brackets escaped.</summary>
    public static string IndexPath(string field) =>
        "[" + field.Replace("^", "^^").Replace(",", "^,").Replace("]", "^]") + "]";
}

/// <summary>A row's cell colours, by field - bindable as an indexer.</summary>
public sealed class CellColours
{
    private static readonly Dictionary<ColourRgb, Brush> Brushes = new();

    public CellColours(Func<string, ColourRgb?> lookup) => Lookup = lookup;

    public Func<string, ColourRgb?> Lookup { get; }

    /// <summary>The cell's background, or null to leave it as the grid draws it.</summary>
    public Brush? this[string field]
    {
        get
        {
            if (Lookup(field) is not { } colour) return null;
            if (!Brushes.TryGetValue(colour, out var brush))
            {
                brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
                brush.Freeze();
                Brushes[colour] = brush;
            }

            return brush;
        }
    }
}
