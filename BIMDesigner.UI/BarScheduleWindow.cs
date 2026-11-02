using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Schedules;

namespace BIMDesigner.UI;

/// <summary>
/// The Bar Schedule: the steel in the foundations member by member - each bar mark's size, how many,
/// how long, its shape - then each size's mass and the fabric, as it goes on a sheet. It follows the
/// model as it changes; Copy puts it on the clipboard, tab apart, for a spreadsheet.
/// </summary>
internal sealed class BarScheduleWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly DataGrid _list = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserAddRows = false,
        Margin = new Thickness(16, 0, 16, 6)
    };
    private readonly TextBlock _said = new();
    private ScheduleTable? _table;

    public BarScheduleWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, Reinforcement.Title(main.Document), 980, 520);
        MinWidth = 600;
        MinHeight = 280;

        var copy = new Button { Content = "Copy", ToolTip = "Copy the schedule to the clipboard, its columns tab apart, for a spreadsheet" };
        copy.Click += (_, _) => Copy();
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, copy, close));
        root.Children.Add(DialogLook.Intro("The bars and fabric in the reinforced footings, rafts, ground beams and piles. "
                                           + "Each member's steel is set in its Properties; put the schedule on a sheet from Place, under its title."));
        root.Children.Add(_list);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Refresh();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>The schedule as the model now stands.</summary>
    internal void Refresh()
    {
        var document = _main.Document;
        _table = Reinforcement.Table(document);
        var headings = _table.Lines[1].Cells;
        _list.Columns.Clear();
        for (var i = 0; i < headings.Count; i++)
            _list.Columns.Add(new DataGridTextColumn
            {
                Header = headings[i],
                Width = i == 0 ? new DataGridLength(180) : DataGridLength.Auto,
                Binding = new System.Windows.Data.Binding($"[{i}]")
            });
        _list.ItemsSource = _table.Lines.Skip(2).Where(line => line.Kind != ScheduleLineKind.GroupHeading).Select(line => line.Cells.ToArray()).ToList();

        var lines = Reinforcement.Schedule(document);
        var mass = Reinforcement.Mass(lines) + document.All<RaftFoundation>().Sum(raft => Reinforcement.FabricMass(document, raft));
        DialogLook.Say(_said, lines.Count == 0 && Reinforcement.FabricOf(document).Count == 0
            ? "No foundation holds steel yet: set a footing's Reinforced in its Properties, or put in a raft or ground beam."
            : $"{lines.Count} bar mark{(lines.Count == 1 ? string.Empty : "s")}, {(document.ProjectInformation.ClearanceStandard.IsUs() ? $"{mass * 2.20462:#,0} lb" : $"{mass:#,0} kg")} of steel in all.");
    }

    private void Copy()
    {
        if (_table is null) return;
        var text = string.Join(Environment.NewLine, _table.Lines.Select(line => string.Join("\t", line.Cells)));
        try
        {
            Clipboard.SetText(text);
            DialogLook.Say(_said, "Copied, tab apart.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            DialogLook.Say(_said, "The clipboard is in use by another program - try again.");
        }
    }
}
