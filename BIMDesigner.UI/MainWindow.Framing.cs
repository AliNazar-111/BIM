using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The Framing panel: frame the selected walls with studs, list what the frames are cut from,
/// and take a frame out again. A frame's material, section, spacing, plates and noggings are in
/// Properties when it is selected - click a stud in plan, or the frame in 3D.
/// </summary>
public partial class MainWindow
{
    private void OnFrameWalls(object sender, RoutedEventArgs e)
    {
        var walls = Plan.SelectedElements.OfType<Wall>().ToList();
        if (walls.Count == 0)
        {
            StatusHint.Text = "Select the walls to frame first.";
            return;
        }

        var frames = walls.Where(wall => WallFramings.WhyNot(_document, wall) is null).Select(wall => (Element)WallFramings.New(_document, wall)).ToList();
        if (frames.Count == 0)
        {
            StatusHint.Text = WallFramings.WhyNot(_document, walls[0]) ?? "Those walls cannot be framed.";
            return;
        }

        _history.Execute(new AddElementsCommand(_document, frames, "Frame Walls"));
        AfterHistoryChange();
        var studs = frames.OfType<WallFraming>().Sum(frame => WallFramings.Layout(_document, frame).Count(member => member.IsUpright));
        StatusHint.Text = $"{frames.Count} {(frames.Count == 1 ? "wall" : "walls")} framed: {studs} studs. Untick Walls in the 3D view to see the frame; Cut List lists what to cut.";
    }

    private void OnRemoveFraming(object sender, RoutedEventArgs e)
    {
        var frames = Plan.SelectedElements.OfType<WallFraming>()
            .Concat(Plan.SelectedElements.OfType<Wall>().Select(wall => WallFramings.Of(_document, wall)).OfType<WallFraming>())
            .Distinct().Cast<Element>().ToList();
        if (frames.Count == 0)
        {
            StatusHint.Text = "Select a framed wall, or its frame, to take the frame out.";
            return;
        }

        _history.Execute(new DeleteElementsCommand(_document, frames));
        Plan.Select(null);
        AfterHistoryChange();
        StatusHint.Text = "Frame taken out.";
    }

    /// <summary>The cut list of the frames selected - or of the selected walls', or every frame in the project.</summary>
    private void OnCutList(object sender, RoutedEventArgs e)
    {
        var chosen = Plan.SelectedElements.OfType<WallFraming>()
            .Concat(Plan.SelectedElements.OfType<Wall>().Select(wall => WallFramings.Of(_document, wall)).OfType<WallFraming>())
            .Distinct().ToList();
        var frames = chosen.Count > 0 ? chosen : _document.Elements.OfType<WallFraming>().ToList();
        if (frames.Count == 0)
        {
            StatusHint.Text = "No walls are framed yet: select walls and Frame Walls first.";
            return;
        }

        var rows = WallFramings.CutList(_document, frames);
        var grid = new DataGrid
        {
            ItemsSource = rows.Select(row => new
            {
                row.Member, row.Section, Length = Units.FormatLength(row.Length), row.Quantity,
                Total = Units.FormatLength(row.TotalLength), row.Walls
            }).ToList(),
            IsReadOnly = true, AutoGenerateColumns = true, CanUserSortColumns = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(12, 12, 12, 6)
        };

        var totals = string.Join("    ", rows.GroupBy(row => row.Section)
            .Select(group => $"{group.Key}: {group.Sum(row => row.Quantity)} pieces, {Units.FormatLength(group.Sum(row => row.TotalLength))}"));
        var summary = new TextBlock { Text = totals, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 8) };

        var export = new Button { Content = "Export CSV", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close", Padding = new Thickness(18, 5, 18, 5), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        buttons.Children.Add(export);
        buttons.Children.Add(close);

        var layout = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(summary, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(summary);
        layout.Children.Add(grid);

        var window = new System.Windows.Window
        {
            Title = $"Cut List - {frames.Count} framed {(frames.Count == 1 ? "wall" : "walls")}", Content = layout, Owner = this,
            Width = 820, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("Bg.Panel") as System.Windows.Media.Brush ?? Background
        };

        export.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Cut List.csv" };
            if (dialog.ShowDialog(window) != true) return;
            File.WriteAllText(dialog.FileName, CutListCsv(rows), Encoding.UTF8);
            StatusHint.Text = $"Cut list saved to {dialog.FileName}.";
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    /// <summary>A cut list as comma-separated values, lengths in millimetres.</summary>
    public static string CutListCsv(IReadOnlyList<CutListRow> rows)
    {
        static string Quote(string text) => text.Contains(',') || text.Contains('"') ? $"\"{text.Replace("\"", "\"\"")}\"" : text;

        var csv = new StringBuilder("Member,Section,Length (mm),Quantity,Total length (mm),Walls\r\n");
        foreach (var row in rows)
            csv.Append($"{Quote(row.Member)},{Quote(row.Section)},{row.Length:0},{row.Quantity},{row.TotalLength:0},{Quote(row.Walls)}\r\n");
        return csv.ToString();
    }
}
