using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI;

/// <summary>
/// The colours a plan's rooms are coloured in, picked by hand: each value of what they are coloured
/// by - each name, department, occupancy or size band - with its swatch and its colour as six hex
/// digits to type over, or given back to the scheme with Automatic. A colour picked is that value's
/// on every view coloured by it, in its legend on a sheet too; each change is one step to undo.
/// </summary>
internal sealed class RoomColoursWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly StackPanel _rows = new() { Margin = new Thickness(16, 4, 16, 8) };
    private readonly TextBlock _said = new();

    public RoomColoursWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Room Colours", 520, 520);
        MinWidth = 400;
        MinHeight = 260;

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, close));
        root.Children.Add(DialogLook.Intro(
            "Each value the rooms are coloured by, and its colour: type six hex digits - #8DD3C7 - and press Enter, "
            + "or Automatic to give it back to the scheme. A colour picked is the same on every view coloured by it, and in its legend."));
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>The values of the plan open, each with its swatch, colour and Automatic.</summary>
    internal void Refresh()
    {
        _rows.Children.Clear();
        var document = _main.Document;
        var view = _main.Plan.CurrentView;
        var by = document.ViewSettings.ColourByOf(view);
        if (by == RoomColourBy.None)
        {
            DialogLook.Say(_said, "This view's rooms are plain: colour them by name, department, occupancy or size on the bar under the plan first.");
            return;
        }

        var entries = RoomColours.Legend(document, view);
        foreach (var entry in entries) _rows.Children.Add(Row(by, entry));
        DialogLook.Say(_said, entries.Count == 0
            ? "No rooms on this view to colour."
            : $"Rooms by {RoomColours.Name(by).ToLowerInvariant()}: {entries.Count} values, {entries.Count(entry => RoomColours.Picked(document, by, entry.Value))} picked by hand.");
    }

    private FrameworkElement Row(RoomColourBy by, RoomColourEntry entry)
    {
        var swatch = new Border
        {
            Width = 26, Height = 18, Margin = new Thickness(0, 0, 10, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(entry.Colour.R, entry.Colour.G, entry.Colour.B))
        };
        var name = new TextBlock { Text = entry.Value, Width = 190, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var hex = new TextBox { Text = entry.Colour.ToString(), Width = 92, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var automatic = new Button
        {
            Content = "Automatic", Padding = new Thickness(8, 2, 8, 2), IsEnabled = RoomColours.Picked(_main.Document, by, entry.Value),
            ToolTip = "Give this value back the scheme's own colour"
        };

        hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Pick(by, entry.Value, hex.Text);
            e.Handled = true;
        };
        hex.LostKeyboardFocus += (_, _) => { if (hex.Text.Trim() != entry.Colour.ToString()) Pick(by, entry.Value, hex.Text); };
        automatic.Click += (_, _) => Apply(by, entry.Value, null);

        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3), Children = { swatch, name, hex, automatic } };
    }

    private void Pick(RoomColourBy by, string value, string text)
    {
        if (!ColourRgb.TryParse(text, out var colour))
        {
            DialogLook.Warn(_said, $"\"{text.Trim()}\" is not a colour: six hex digits, #8DD3C7.");
            return;
        }

        Apply(by, value, colour);
    }

    /// <summary>A value's colour picked, or given back - one step to undo - and the plan drawn again.</summary>
    internal void Apply(RoomColourBy by, string value, ColourRgb? colour)
    {
        if (_main.Document.ViewSettings.ColourPicked(by, value) == colour) return;
        _main.History.Execute(RoomColours.Pick(_main.Document, by, value, colour));
        _main.AfterRoomColours();
    }
}
