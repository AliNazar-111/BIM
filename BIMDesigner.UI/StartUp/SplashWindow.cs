using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BIMDesigner.UI.StartUp;

/// <summary>
/// What is shown while BIMDesigner gets ready: its name and version, and what it is doing - the
/// model engine loading, the templates being prepared, the recent projects read.
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly TextBlock _doing = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly ProgressBar _progress = new() { Height = 3, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0) };

    public SplashWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 560;
        Height = 320;
        Title = "BIMDesigner";
        Icon = Logo.Image(32);
        ShowActivated = true;

        _doing.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        _progress.SetResourceReference(Control.ForegroundProperty, "Accent");
        _progress.SetResourceReference(Control.BackgroundProperty, "Line");

        var name = new TextBlock { Text = "BIMDesigner", FontSize = 34, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        if (Logo.Image(64) is { } logo) heading.Children.Add(new Image { Source = logo, Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0) });
        heading.Children.Add(name);

        var line = new TextBlock { Text = "Buildings designed, drawn and checked to the regulations", FontSize = 14 };
        line.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        var version = new TextBlock { Text = $"Version {MainWindow.AppVersion}", Margin = new Thickness(0, 4, 0, 0) };
        version.SetResourceReference(TextBlock.ForegroundProperty, "Text.Disabled");

        var top = new StackPanel();
        top.Children.Add(heading);
        top.Children.Add(line);
        top.Children.Add(version);
        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        bottom.Children.Add(_doing);
        bottom.Children.Add(_progress);

        var layout = new Grid();
        layout.Children.Add(top);
        layout.Children.Add(bottom);

        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(36, 34, 36, 28), Child = layout };
        card.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line.Strong");
        Content = card;
    }

    /// <summary>What it is doing now, and how far along it is - shown at once, before the work goes on.</summary>
    public void Step(string doing, double done)
    {
        _doing.Text = doing;
        _progress.Value = Math.Clamp(done, 0, 1);
        Dispatcher.Invoke(DispatcherPriority.Render, () => { });
    }
}
