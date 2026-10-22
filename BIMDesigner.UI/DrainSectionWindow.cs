using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI;

/// <summary>
/// A drain's long section: the run from its head to its outfall laid out flat, its heights drawn
/// ten times their length's scale as long sections are - the ground over it, the pipe falling along
/// it, each access point with its cover, invert and depth - and under it a table of each length's
/// size, gradient and inverts.
/// </summary>
internal sealed class DrainSectionWindow : System.Windows.Window
{
    public DrainSectionWindow(DrainSection section)
    {
        DialogLook.Apply(this, $"Long Section - {section.System.Name}", 980, 560);
        MinWidth = 640;
        MinHeight = 400;

        var table = new TextBlock { Margin = new Thickness(16, 6, 16, 12), FontFamily = new FontFamily("Consolas"), Opacity = 0.85 };
        var lines = new List<string> { $"{"Length",-10}{"From",10}{"To",10}{"Size",8}{"Gradient",12}{"Upper invert",15}{"Lower invert",15}" };
        foreach (var pipe in section.Pipes)
            lines.Add($"{pipe.Pipe.Mark,-10}{pipe.From / 1000,9:0.00}m{pipe.To / 1000,9:0.00}m{pipe.Bore,8:0}{(pipe.Fall > 0 && double.IsFinite(pipe.Fall) ? $"1:{1000 / pipe.Fall:0}" : "-"),12}{Units.FormatLength(pipe.UpperInvert),15}{Units.FormatLength(pipe.LowerInvert),15}");
        foreach (var access in section.Accesses)
            lines.Add($"{access.Name} at {access.At / 1000:0.00} m: cover {Units.FormatLength(access.Cover)}, invert {Units.FormatLength(access.Invert)}, depth {Units.FormatLength(access.Cover - access.Invert)}");
        table.Text = string.Join(Environment.NewLine, lines);

        var root = new DockPanel();
        DockPanel.SetDock(table, Dock.Bottom);
        root.Children.Add(table);
        root.Children.Add(new SectionDrawing(section) { Margin = new Thickness(16) });
        Content = root;
    }

    private sealed class SectionDrawing : FrameworkElement
    {
        private readonly DrainSection _section;

        public SectionDrawing(DrainSection section) => _section = section;

        protected override void OnRender(DrawingContext dc)
        {
            var section = _section;
            if (section.Pipes.Count == 0 || ActualWidth < 50 || ActualHeight < 50) return;

            var colour = section.System.Kind.Colour();
            var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
            var text = (Brush)FindResource("Text");
            var line = new Pen(text, 1);
            var ground = new Pen(new SolidColorBrush(Color.FromRgb(0x6E, 0x9A, 0x50)), 1.5);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText Label(string said) => new(said, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, text, dpi);

            // Along the run at its own scale, heights ten times it - or as much as fits.
            var lowest = Math.Min(section.Pipes.Min(pipe => pipe.LowerInvert), section.Accesses.Select(access => access.Invert).DefaultIfEmpty(double.MaxValue).Min()) - 300;
            var highest = Math.Max(section.Ground, section.Accesses.Select(access => access.Cover).DefaultIfEmpty(double.MinValue).Max()) + 300;
            var across = (ActualWidth - 200) / Math.Max(section.Length, 1);
            var up = Math.Min(across * 10, (ActualHeight - 60) / Math.Max(highest - lowest, 1));
            Point At(double along, double height) => new(40 + along * across, 20 + (highest - height) * up);

            dc.DrawLine(ground, At(0, section.Ground), At(section.Length, section.Ground));
            var groundLabel = Label($"Ground {Units.FormatLength(section.Ground)}");
            dc.DrawText(groundLabel, At(section.Length / 2, section.Ground) + new Vector(-groundLabel.Width / 2, 4));

            foreach (var pipe in section.Pipes)
            {
                var pen = new Pen(brush, 2);
                dc.DrawLine(pen, At(pipe.From, pipe.UpperInvert), At(pipe.To, pipe.LowerInvert));
                dc.DrawLine(new Pen(brush, 1), At(pipe.From, pipe.UpperInvert + pipe.Bore), At(pipe.To, pipe.LowerInvert + pipe.Bore));
                var middle = At((pipe.From + pipe.To) / 2, (pipe.UpperInvert + pipe.LowerInvert) / 2);
                var said = $"{pipe.Bore:0} at {(pipe.Fall > 0 && double.IsFinite(pipe.Fall) ? $"1:{1000 / pipe.Fall:0}" : "-")}";
                dc.DrawText(Label(said), middle + new Vector(-20, 8));
            }

            foreach (var access in section.Accesses)
            {
                var top = At(access.At, access.Cover);
                var bottom = At(access.At, access.Invert);
                dc.DrawRectangle(null, line, new Rect(new Point(top.X - 6, top.Y), new Point(bottom.X + 6, bottom.Y)));
                var label = Label($"{access.Name}\nCL {Units.FormatLength(access.Cover)}\nIL {Units.FormatLength(access.Invert)}");
                dc.DrawText(label, new Point(top.X + 8, top.Y - label.Height - 2));
            }
        }
    }
}
