using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;

namespace BIMDesigner.UI;

/// <summary>
/// An imported drawing's layers, each in its colour, ticked where it is shown: untick what is not
/// wanted to work over - text, hatching, furniture - and OK hides it as one step to undo.
/// </summary>
internal sealed class CadLayersWindow : System.Windows.Window
{
    private readonly List<(string Name, CheckBox Box)> _boxes = new();

    public CadLayersWindow(CadUnderlay underlay)
    {
        DialogLook.Apply(this, $"Layers - {System.IO.Path.GetFileName(underlay.Source)}", 440, 560);
        MinHeight = 300;

        var list = new StackPanel { Margin = new Thickness(8) };
        foreach (var layer in underlay.Drawing.Layers.OrderBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase))
        {
            var lines = underlay.Drawing.Paths.Count(path => underlay.Drawing.Layers[path.Layer].Name == layer.Name);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
                Background = new SolidColorBrush(Color.FromRgb(layer.Colour.R, layer.Colour.G, layer.Colour.B))
            });
            row.Children.Add(new TextBlock { Text = $"{layer.Name}  ({lines:#,0} lines)", VerticalAlignment = VerticalAlignment.Center });
            var box = new CheckBox { Content = row, IsChecked = !underlay.HiddenLayers.Contains(layer.Name), Margin = new Thickness(0, 2, 0, 2) };
            _boxes.Add((layer.Name, box));
            list.Children.Add(box);
        }

        var all = new Button { Content = "Show All" };
        all.Click += (_, _) => _boxes.ForEach(entry => entry.Box.IsChecked = true);
        var none = new Button { Content = "Hide All" };
        none.Click += (_, _) => _boxes.ForEach(entry => entry.Box.IsChecked = false);
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(new TextBlock(), all, none, ok, cancel));
        root.Children.Add(DialogLook.Intro("Tick the layers to show. The drawing tools snap only to what is shown."));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8, 0, 8, 8) });
        Content = root;
    }

    /// <summary>The layers left unticked.</summary>
    public IEnumerable<string> Hidden => _boxes.Where(entry => entry.Box.IsChecked != true).Select(entry => entry.Name);
}
