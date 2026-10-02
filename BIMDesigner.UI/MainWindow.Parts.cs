using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI;

/// <summary>
/// The Parts panel: make the selected walls into parts, divide parts into panels, take parts
/// out or put them back, and put walls back together.
/// </summary>
public partial class MainWindow
{
    /// <summary>The parts the selection means: the parts selected, or every part of the walls selected.</summary>
    private List<Part> SelectedParts() =>
        Plan.SelectedElements.OfType<Part>()
            .Concat(Plan.SelectedElements.OfType<Wall>().SelectMany(wall => Parts.Of(_document, wall)))
            .Distinct()
            .ToList();

    private void OnCreateParts(object sender, RoutedEventArgs e)
    {
        var walls = Plan.SelectedElements.OfType<Wall>().ToList();
        if (walls.Count == 0)
        {
            StatusHint.Text = "Select the walls to make into parts first.";
            return;
        }

        if (Parts.Create(_document, walls) is not { } command)
        {
            StatusHint.Text = Parts.WhyNot(_document, walls[0]) ?? "Those walls cannot be made into parts.";
            return;
        }

        _history.Execute(command);
        AfterHistoryChange();
        var made = walls.Sum(wall => Parts.Of(_document, wall).Count);
        StatusHint.Text = $"{made} parts made - one for each layer. Select one to give it its own material, divide it into panels, or leave it out.";
    }

    private void OnDivideParts(object sender, RoutedEventArgs e)
    {
        var parts = SelectedParts();
        if (parts.Count == 0)
        {
            StatusHint.Text = "Select the parts to divide - click a layer of a wall in parts - or a wall in parts for all of them.";
            return;
        }

        if (AskDivision() is not var (width, height, gap)) return;

        if (Parts.Divide(_document, parts, width, height, gap) is not { } command)
        {
            StatusHint.Text = "Those parts are no bigger than one panel: nothing to divide.";
            return;
        }

        _history.Execute(command);
        AfterHistoryChange();
        StatusHint.Text = $"Divided into panels {Units.FormatLength(width)} wide{(height is { } h ? $" and {Units.FormatLength(h)} high" : string.Empty)}, {Units.FormatLength(gap)} joints.";
    }

    private void OnExcludeParts(object sender, RoutedEventArgs e) => SetExcluded(Plan.SelectedElements.OfType<Part>().ToList(), true);

    private void OnRestoreParts(object sender, RoutedEventArgs e) => SetExcluded(SelectedParts(), false);

    private void SetExcluded(List<Part> parts, bool excluded)
    {
        var command = new ExcludePartsCommand(parts, excluded);
        if (command.IsEmpty)
        {
            StatusHint.Text = excluded ? "Select the parts to leave out." : "Nothing selected is left out.";
            return;
        }

        _history.Execute(command);
        AfterHistoryChange();
        StatusHint.Text = excluded ? "Left out: not built, shown or scheduled. Restore puts it back." : "Put back.";
    }

    private void OnRemoveParts(object sender, RoutedEventArgs e)
    {
        var parts = Plan.SelectedElements.OfType<Wall>().SelectMany(wall => Parts.Of(_document, wall))
            .Concat(Plan.SelectedElements.OfType<Part>().SelectMany(part => Parts.Host(_document, part) is { } wall ? Parts.Of(_document, wall) : Array.Empty<Part>()))
            .Distinct()
            .Cast<Element>()
            .ToList();
        if (parts.Count == 0)
        {
            StatusHint.Text = "Select a wall in parts, or one of its parts, to put the wall back together.";
            return;
        }

        _history.Execute(new DeleteElementsCommand(_document, parts));
        Plan.Select(null);
        AfterHistoryChange();
        StatusHint.Text = "The wall is whole again.";
    }

    /// <summary>Asks how to divide: the panel width, its height (none for the full height) and the joint.</summary>
    private (double Width, double? Height, double Gap)? AskDivision()
    {
        var width = new TextBox { Text = "1200 mm", Width = 110, Margin = new Thickness(0, 0, 0, 8) };
        var height = new TextBox { Text = string.Empty, Width = 110, Margin = new Thickness(0, 0, 0, 8), ToolTip = "Empty for panels the full height of the part" };
        var gap = new TextBox { Text = "10 mm", Width = 110, Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "Divide", Padding = new Thickness(18, 5, 18, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 5, 18, 5), IsCancel = true };

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Put(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 12, 0) };
        Put(Label("Panel width"), 0, 0);
        Put(width, 0, 1);
        Put(Label("Panel height"), 1, 0);
        Put(height, 1, 1);
        Put(Label("Joint"), 2, 0);
        Put(gap, 2, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Put(buttons, 3, 1);

        var dialog = new System.Windows.Window
        {
            Title = "Divide Parts", Content = grid, Owner = this, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("Bg.Panel") as System.Windows.Media.Brush ?? Background
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return null;

        static double? Read(string text) =>
            ParameterFormatter.TryParse(ParameterDataType.Length, text, out var value) && value is double mm ? mm : null;

        if (Read(width.Text) is not { } across || across < 10)
        {
            StatusHint.Text = "The panel width is a length, such as 1200 or 1.2 m.";
            return null;
        }

        double? up = string.IsNullOrWhiteSpace(height.Text) ? null : Read(height.Text);
        if (!string.IsNullOrWhiteSpace(height.Text) && up is not >= 10)
        {
            StatusHint.Text = "The panel height is a length, or empty for the full height.";
            return null;
        }

        return (across, up, Math.Max(0, Read(gap.Text) ?? 0));
    }
}
