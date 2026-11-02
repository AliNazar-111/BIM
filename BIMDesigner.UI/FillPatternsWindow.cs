using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Geometry;
using Microsoft.Win32;

namespace BIMDesigner.UI;

/// <summary>
/// The project's own fill patterns, which filled regions can be filled with beside the program's:
/// a simple one made - lines at an angle and a spacing, crossed or not, sized on the paper or in
/// the building - or patterns read from a .pat file and written to one. Each change is one step to
/// undo; a pattern deleted leaves the regions filled with it in their own pattern.
/// </summary>
internal sealed class FillPatternsWindow : System.Windows.Window
{
    private readonly MainWindow _main;
    private readonly ListBox _list = new() { Margin = new Thickness(16, 0, 8, 8), MinHeight = 180 };
    private readonly Border _preview = new() { Width = 150, Height = 110, HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _name = new() { Text = "Hatch", Width = 150 };
    private readonly TextBox _angle = new() { Text = "45", Width = 60 };
    private readonly TextBox _spacing = new() { Text = "3", Width = 60 };
    private readonly CheckBox _crossed = new() { Content = "Crossed", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _model = new() { Content = "Model - sized in the building", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _inches = new() { Content = "A .pat file with no units line is in inches", Margin = new Thickness(16, 0, 16, 6) };
    private readonly TextBlock _said = new();

    public FillPatternsWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Fill Patterns", 680, 640);
        MinWidth = 560;
        MinHeight = 480;

        Button Make(string text, string tip, Action click)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (_, _) => click();
            return button;
        }

        var add = Make("Add", "Make this pattern and add it to the project", AddSimple);
        var import = Make("Import .pat...", "Read the patterns in a .pat file into the project", () =>
        {
            var dialog = new OpenFileDialog { Title = "Import Fill Patterns", Filter = "Fill pattern files (*.pat)|*.pat|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == true) Import(dialog.FileName);
        });
        var export = Make("Export .pat...", "Write the project's patterns to a .pat file, in millimetres", () =>
        {
            var dialog = new SaveFileDialog { Title = "Export Fill Patterns", Filter = "Fill pattern files (*.pat)|*.pat", FileName = "Patterns.pat" };
            if (dialog.ShowDialog(this) == true) Export(dialog.FileName);
        });
        var delete = Make("Delete", "Take the pattern picked out of the project: regions filled with it keep their own pattern", DeletePicked);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        // A simple pattern of one's own, made beside the list.
        var form = new StackPanel { Margin = new Thickness(8, 0, 16, 8) };
        form.Children.Add(_preview);
        void Field(string label, FrameworkElement box, string tip)
        {
            box.ToolTip = tip;
            box.HorizontalAlignment = HorizontalAlignment.Left;
            form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2), Opacity = 0.8 });
            form.Children.Add(box);
        }

        Field("Name", _name, "What it is called, in the region's pattern list");
        Field("Angle, degrees", _angle, "The lines' angle: 0 level, 90 upright, 45 rising to the right");
        Field("Spacing, mm", _spacing, "How far apart the lines are: on the paper for a drafting pattern, in the building for a model one");
        _crossed.ToolTip = "As many lines again, square to these";
        _model.ToolTip = "Sized in the building, as brick courses are, so it scales with the drawing; unticked, it reads alike at every scale";
        form.Children.Add(_crossed);
        form.Children.Add(_model);
        add.Margin = new Thickness(0, 10, 0, 0);
        form.Children.Add(add);

        var buttons = new WrapPanel { Margin = new Thickness(16, 0, 16, 8) };
        foreach (var button in new[] { import, export, delete }) buttons.Children.Add(button);
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(_inches, Dock.Top);
        var side = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(side, Dock.Right);

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_said, close));
        root.Children.Add(DialogLook.Intro("Fill patterns of the project's own, beside the program's: make a simple one, or read the patterns in a .pat file - the hatch files drawing programs share. "
                                           + "Pick one for a filled region in its Properties or on the option bar."));
        root.Children.Add(buttons);
        root.Children.Add(_inches);
        root.Children.Add(side);
        root.Children.Add(_list);
        Content = root;

        _list.SelectionChanged += (_, _) => Preview(_list.SelectedItem is ListBoxItem { Tag: CustomFillPattern pattern } ? pattern : null);
        foreach (var box in new[] { _angle, _spacing }) box.TextChanged += (_, _) => Preview(Simple());
        _crossed.Click += (_, _) => Preview(Simple());
        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    internal void Refresh()
    {
        _list.Items.Clear();
        foreach (var pattern in _main.Document.FillPatterns)
            _list.Items.Add(new ListBoxItem
            {
                Tag = pattern,
                Content = $"{pattern.Name}  -  {(pattern.Model ? "model" : "drafting")}, {pattern.Lines.Count} line famil{(pattern.Lines.Count == 1 ? "y" : "ies")}{(pattern.Description.Length > 0 ? $" ({pattern.Description})" : string.Empty)}"
            });
        DialogLook.Say(_said, _main.Document.FillPatterns.Count == 0 ? "No patterns of the project's own yet." : $"{_main.Document.FillPatterns.Count} of the project's own.");
        Preview(Simple());
    }

    /// <summary>The simple pattern the form describes, or null where it does not make one.</summary>
    private CustomFillPattern? Simple() =>
        double.TryParse(_angle.Text, out var angle) && double.TryParse(_spacing.Text, out var spacing) && spacing > 0 && double.IsFinite(angle)
            ? FillPatterns.Simple(string.IsNullOrWhiteSpace(_name.Text) ? "Hatch" : _name.Text, angle, spacing, _crossed.IsChecked == true, _model.IsChecked == true)
            : null;

    private void AddSimple()
    {
        if (Simple() is not { } pattern)
        {
            DialogLook.Warn(_said, "Give an angle in degrees and a spacing of more than nothing, in mm.");
            return;
        }

        Apply(FillPatterns.Add(_main.Document, new[] { pattern }), $"{pattern.Name} added: pick it for a filled region.");
    }

    /// <summary>The patterns in a .pat file read into the project, as one step to undo. Internal so tests and the harness can read one.</summary>
    internal void Import(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DialogLook.Warn(_said, $"Could not read {Path.GetFileName(path)}: {error.Message}");
            return;
        }

        var patterns = FillPatterns.Read(text, out var problems, inches: _inches.IsChecked == true);
        if (patterns.Count == 0)
        {
            DialogLook.Warn(_said, $"No patterns in {Path.GetFileName(path)}{(problems.Count > 0 ? $": {problems[0]}" : ".")}");
            return;
        }

        Apply(FillPatterns.Add(_main.Document, patterns),
            $"{patterns.Count} pattern{(patterns.Count == 1 ? string.Empty : "s")} read from {Path.GetFileName(path)}{(problems.Count > 0 ? $"; {problems.Count} line{(problems.Count == 1 ? string.Empty : "s")} not understood - {problems[0]}" : ".")}");
    }

    /// <summary>The project's patterns written to a .pat file. Internal so tests and the harness can write one.</summary>
    internal void Export(string path)
    {
        try
        {
            File.WriteAllText(path, FillPatterns.Write(_main.Document.FillPatterns));
            DialogLook.Say(_said, $"{_main.Document.FillPatterns.Count} written to {Path.GetFileName(path)}, in millimetres.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DialogLook.Warn(_said, $"Could not write {Path.GetFileName(path)}: {error.Message}");
        }
    }

    private void DeletePicked()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: CustomFillPattern pattern })
        {
            DialogLook.Warn(_said, "Pick a pattern to delete.");
            return;
        }

        Apply(FillPatterns.Remove(_main.Document, pattern), $"{pattern.Name} deleted: regions filled with it keep their own pattern.");
    }

    private void Apply(Core.Documents.Commands.IUndoableCommand command, string said)
    {
        _main.History.Execute(command);
        _main.AfterFillPatterns();
        Refresh();
        DialogLook.Say(_said, said);
    }

    /// <summary>A pattern drawn over a swatch as it hatches - a drafting one at 1:1 on the paper, a model one at 1:20.</summary>
    private void Preview(CustomFillPattern? pattern)
    {
        var (width, height) = (_preview.Width - 2, _preview.Height - 2);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (pattern is not null)
            {
                // A pixel to a fifth of a millimetre on the paper.
                var perMm = 5.0 / (pattern.Model ? 20 : 1);
                var outline = new[] { new Point2D(0, 0), new Point2D(width / perMm, 0), new Point2D(width / perMm, height / perMm), new Point2D(0, height / perMm) };
                var pen = new Pen(Brushes.Black, 1);
                pen.Freeze();
                foreach (var (from, to) in FillPatterns.Hatch(outline, pattern, 1, 0.4 / perMm * 2))
                    dc.DrawLine(pen, new Point(from.X * perMm, height - from.Y * perMm), new Point(to.X * perMm, height - to.Y * perMm));
            }
        }

        _preview.Child = new Image { Source = new DrawingImage(drawing) { }, Stretch = Stretch.None, ClipToBounds = true };
    }
}
