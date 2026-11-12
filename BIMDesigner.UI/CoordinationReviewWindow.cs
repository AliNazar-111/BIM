using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Coordination;

namespace BIMDesigner.UI;

/// <summary>
/// The Coordination Review: each level, gridline, column and wall following a linked model's whose
/// original has changed since it was last agreed - moved, renamed, its bottom or top moved, of another
/// type, gone, or its link taken out - with what to do:
/// Move to Match brings this project's to it, Accept leaves this project's where it is and agrees
/// the original as it now is, Stop Following lets it go its own way. Each is one step to undo.
/// </summary>
internal sealed class CoordinationReviewWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly StackPanel _rows = new() { Margin = new Thickness(16, 4, 16, 8) };
    private readonly TextBlock _said = new();

    public CoordinationReviewWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Coordination Review", 760, 460);
        MinWidth = 520;
        MinHeight = 260;

        var copy = new Button { Content = "Copy/Monitor", ToolTip = "Copy the picked link's levels, gridlines, columns or walls in, or pair them with this project's own, to follow them" };
        copy.Click += (_, _) => _main.AskAndCopyMonitor(null);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, copy, close));
        root.Children.Add(DialogLook.Intro("The levels, gridlines, columns, walls, floors, ceilings, doors, windows and fixtures copied from a linked model, or paired with one there, follow their originals. "
                                           + "Where an original has changed since it was last agreed, it is listed here: move this project's to match it, accept it as it now is, or stop following it."));
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>The changes as they now stand, each with its answers.</summary>
    internal void Refresh()
    {
        _rows.Children.Clear();
        var document = _main.Document;
        var findings = CopyMonitor.Review(document);
        var followed = document.Levels.Count(level => level.Monitor is not null) + document.All<Core.Datums.Grid>().Count(grid => grid.Monitor is not null)
                       + document.All<Core.Architecture.Column>().Count(column => column.Monitor is not null) + document.All<Core.Architecture.Wall>().Count(wall => wall.Monitor is not null)
                       + document.All<Core.Architecture.Slab>().Count(floor => floor.Monitor is not null) + document.All<Core.Architecture.Opening>().Count(opening => opening.Monitor is not null)
                       + document.All<Core.Architecture.Component>().Count(fixture => fixture.Monitor is not null);
        foreach (var finding in findings) _rows.Children.Add(Row(finding));

        DialogLook.Say(_said, followed == 0
            ? "Nothing follows a linked model yet: pick a link and Copy/Monitor."
            : findings.Count == 0
                ? $"{followed} following a linked model's, all as agreed."
                : $"{findings.Count} change{(findings.Count == 1 ? string.Empty : "s")} among the {followed} followed.");
    }

    private FrameworkElement Row(MonitorFinding finding)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(new TextBlock { Text = finding.Said, TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        void Add(string text, string tip, Func<Core.Documents.Commands.IUndoableCommand?> answer)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) };
            button.Click += (_, _) => Answer(answer());
            buttons.Children.Add(button);
        }

        if (finding.Change is MonitorChange.Moved or MonitorChange.Renamed or MonitorChange.Resized or MonitorChange.Retyped)
        {
            Add(finding.Change switch
                {
                    MonitorChange.Moved => "Move to Match",
                    MonitorChange.Renamed => "Rename to Match",
                    MonitorChange.Resized => finding.Datum is Core.Architecture.Opening ? "Match Size" : "Match Height",
                    _ => "Change Type to Match"
                }, "Bring this project's to where its original now is, or as it now is", () => CopyMonitor.Match(_main.Document, finding));
            Add("Accept", "Leave this project's as it is, and agree its original as it now is", () => CopyMonitor.Accept(_main.Document, finding));
        }

        Add("Stop Following", "Let this project's go its own way: it no longer follows the linked model's", () => CopyMonitor.Stop(finding));
        panel.Children.Add(buttons);
        return panel;
    }

    /// <summary>An answer made - one step to undo - and the list said again.</summary>
    internal void Answer(Core.Documents.Commands.IUndoableCommand? command)
    {
        if (command is null)
        {
            DialogLook.Warn(_said, "Its link is not loaded: load it to answer this.");
            return;
        }

        _main.History.Execute(command);
        _main.AfterCoordination();
        Refresh();
    }
}
