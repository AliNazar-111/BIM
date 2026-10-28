using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BIMDesigner.Core;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Rendering;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.UI;

/// <summary>
/// The project's materials: each one's name, its colour in elevation and 3D and where it is cut, and
/// its face - the pattern it is laid in, its unit and joint, its joint's colour, how it reflects -
/// shown at its true size as a 1.2 m square; each change one step to undo. A new material starts from
/// what its name says it is; a copy from the one it is copied from.
/// </summary>
internal sealed class MaterialsWindow : System.Windows.Window
{
    private const double PreviewMm = 1200;
    private const double PreviewPx = 230;

    private readonly MainWindow _main;
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly ListBox _list = new();
    private readonly TextBox _name = new() { Width = 250 };
    private readonly TextBox _surface = new() { Width = 92 };
    private readonly TextBox _cut = new() { Width = 92 };
    private readonly ComboBox _pattern = new() { Width = 170, ItemsSource = Enum.GetValues<SurfacePattern>().Select(Label).ToList() };
    private readonly TextBox _length = new() { Width = 80 };
    private readonly TextBox _height = new() { Width = 80 };
    private readonly TextBox _joint = new() { Width = 80 };
    private readonly TextBox _jointColour = new() { Width = 92 };
    private readonly TextBox _reflectance = new() { Width = 80 };
    private readonly Border _surfaceSwatch = Swatch(), _cutSwatch = Swatch(), _jointSwatch = Swatch();
    private readonly Rectangle _preview = new() { Width = PreviewPx, Height = PreviewPx, Stroke = Brushes.Gray, StrokeThickness = 1 };
    private readonly TextBlock _lengthLabel = new() { VerticalAlignment = VerticalAlignment.Center }, _heightLabel = new() { VerticalAlignment = VerticalAlignment.Center }, _jointLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _problem = new();
    private bool _filling;

    public MaterialsWindow(MainWindow main)
    {
        _main = main;
        DialogLook.Apply(this, "Materials", 1040, 600);
        MinWidth = 640;
        MinHeight = 460;

        // The list: searchable by name.
        var left = new DockPanel { Margin = new Thickness(12, 8, 6, 8), Width = 280 };
        _search.TextChanged += (_, _) => Fill();
        _search.ToolTip = "Find a material by name";
        DockPanel.SetDock(_search, Dock.Top);
        left.Children.Add(_search);
        left.Children.Add(_list);
        _list.SelectionChanged += (_, _) => ShowSelected();

        // The material picked: its name and colours, its face, and how it looks at its size.
        var form = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        void Row(UIElement label, UIElement field, string? tip = null)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (label is FrameworkElement labelled) labelled.Margin = new Thickness(0, 4, 12, 4);
            if (field is FrameworkElement element)
            {
                element.Margin = new Thickness(0, 3, 0, 3);
                element.HorizontalAlignment = HorizontalAlignment.Left;
                if (tip is not null) element.ToolTip = tip;
            }

            Grid.SetRow(label, row);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            form.Children.Add(label);
            form.Children.Add(field);
            row++;
        }

        TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
        StackPanel WithSwatch(TextBox box, Border swatch) => new() { Orientation = Orientation.Horizontal, Children = { box, swatch } };

        Row(Label("Name"), _name);
        Row(Label("Surface colour"), WithSwatch(_surface, _surfaceSwatch), "Its colour in elevation, 3D and a rendering: six hex digits, #A8563C");
        Row(Label("Cut colour"), WithSwatch(_cut, _cutSwatch), "Its colour where a plan or section cuts through it");
        Row(Label("Pattern"), _pattern, "What its face is laid as: drawn on it in the Realistic style and in a rendering");
        Row(_lengthLabel, _length);
        Row(_heightLabel, _height);
        Row(_jointLabel, _joint);
        Row(Label("Joint colour"), WithSwatch(_jointColour, _jointSwatch), "Mortar, grout, or the shadow between boards and slates");
        Row(Label("Reflectance"), _reflectance, "How much it reflects as a mirror, square on, 0 to 1: matt brick 0, glossy tiles 0.08, polished metal 0.25 and up - more at a glancing angle");
        var caption = new TextBlock { Text = $"A {Units.FormatLength(PreviewMm)} square of it", Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };
        var preview = new StackPanel { Margin = new Thickness(6, 12, 16, 8), Children = { _preview, caption } };

        foreach (var box in new[] { _name, _surface, _cut, _length, _height, _joint, _jointColour, _reflectance })
        {
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter) return;
                Commit();
                args.Handled = true;
            };
        }

        _pattern.SelectionChanged += (_, _) => ChangePattern();

        var make = new Button { Content = "New" };
        make.Click += (_, _) => Add(null);
        var copy = new Button { Content = "Duplicate" };
        copy.Click += (_, _) => Add(Selected);
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(preview, Dock.Right);
        body.Children.Add(left);
        body.Children.Add(preview);
        body.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var root = new DockPanel();
        root.Children.Add(DialogLook.Footer(_problem, make, copy, close));
        root.Children.Add(DialogLook.Intro("Each material's colours and its face: the pattern it is laid in at its true size, drawn in the 3D view's Realistic style and in a rendering."));
        root.Children.Add(body);
        Content = root;

        _main.History.Changed += OnHistoryChanged;
        Closed += (_, _) => _main.History.Changed -= OnHistoryChanged;
        Fill();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private Material? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Material;

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (_filling) return;
        Fill();
    }

    /// <summary>The list again, by name, keeping what was picked.</summary>
    private void Fill()
    {
        var picked = Selected;
        _filling = true;
        _list.Items.Clear();
        var find = _search.Text.Trim();
        foreach (var material in _main.Document.Materials.Where(material => find.Length == 0 || material.Name.Contains(find, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(material => material.Name, StringComparer.OrdinalIgnoreCase))
        {
            var swatch = new Rectangle { Width = 22, Height = 16, Margin = new Thickness(0, 0, 8, 0), Stroke = Brushes.Gray, StrokeThickness = 0.5, Fill = FaceBrush(material.Finish, 0.06) };
            var item = new ListBoxItem
            {
                Tag = material,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = material.Name, VerticalAlignment = VerticalAlignment.Center } } }
            };
            _list.Items.Add(item);
            if (ReferenceEquals(material, picked)) _list.SelectedItem = item;
        }

        _filling = false;
        ShowSelected();
    }

    /// <summary>The picked material's name, colours and face in the boxes.</summary>
    private void ShowSelected()
    {
        if (_filling) return;
        var material = Selected;
        _filling = true;
        foreach (var box in new[] { _name, _surface, _cut, _length, _height, _joint, _jointColour, _reflectance }) box.IsEnabled = material is not null;
        _pattern.IsEnabled = material is not null;
        if (material is not null)
        {
            var finish = material.Finish;
            _name.Text = material.Name;
            _surface.Text = material.SurfaceColour.ToString();
            _cut.Text = material.CutColour.ToString();
            _pattern.SelectedIndex = (int)finish.Pattern;
            _length.Text = Units.FormatLength(finish.UnitLength);
            _height.Text = Units.FormatLength(finish.UnitHeight);
            _joint.Text = Units.FormatLength(finish.JointWidth);
            _jointColour.Text = finish.JointColour.ToString();
            _reflectance.Text = finish.Reflectance.ToString("0.###", CultureInfo.CurrentCulture);
            (_surfaceSwatch.Background, _cutSwatch.Background, _jointSwatch.Background) = (Solid(material.SurfaceColour), Solid(material.CutColour), Solid(finish.JointColour));
            Labels(finish.Pattern);
            var units = finish.Pattern is not (SurfacePattern.None or SurfacePattern.Render or SurfacePattern.Concrete or SurfacePattern.Metal or SurfacePattern.Grass or SurfacePattern.Gravel or SurfacePattern.Planting);
            _length.IsEnabled = _height.IsEnabled = _joint.IsEnabled = _jointColour.IsEnabled = units;
            _height.IsEnabled = units && finish.Pattern != SurfacePattern.StandingSeam;
            _preview.Fill = FaceBrush(finish, PreviewPx / PreviewMm);
        }
        else _preview.Fill = Brushes.Transparent;

        _filling = false;
    }

    /// <summary>What a pattern's unit is called: a brick's length and height, a board's length and width, a slate's width and gauge.</summary>
    private void Labels(SurfacePattern pattern)
    {
        (_lengthLabel.Text, _heightLabel.Text, _jointLabel.Text) = pattern switch
        {
            SurfacePattern.Brick => ("Brick length", "Brick height", "Mortar joint"),
            SurfacePattern.Block => ("Block length", "Block height", "Mortar joint"),
            SurfacePattern.Stone => ("Stone length", "Course height", "Joint"),
            SurfacePattern.Boards => ("Board length", "Board width", "Gap"),
            SurfacePattern.Tiles or SurfacePattern.Paving => ("Unit length", "Unit width", "Joint"),
            SurfacePattern.Slate or SurfacePattern.PlainTiles or SurfacePattern.InterlockingTiles => ("Width", "Gauge up the slope", "Gap"),
            SurfacePattern.StandingSeam => ("Seams at", "-", "Seam width"),
            _ => ("Unit length", "Unit height", "Joint")
        };
        foreach (var label in new[] { _lengthLabel, _heightLabel, _jointLabel }) label.Opacity = 0.85;
    }

    /// <summary>A new pattern picked: its usual units and joint, in the material's colour.</summary>
    private void ChangePattern()
    {
        if (_filling || Selected is not { } material || _pattern.SelectedIndex < 0) return;
        var pattern = (SurfacePattern)_pattern.SelectedIndex;
        if (pattern == material.Pattern) return;
        var finish = SurfacePatterns.Standard(pattern, material.SurfaceColour, material.Reflectance);
        Apply(material, MaterialLook.Of(material) with { Finish = finish });
    }

    /// <summary>The boxes as typed, as one change; anything that does not read put back as it was.</summary>
    private void Commit()
    {
        if (_filling || Selected is not { } material) return;
        var look = MaterialLook.Of(material);
        var finish = look.Finish;
        var name = _name.Text.Trim();
        if (name.Length > 0 && !string.Equals(name, material.Name, StringComparison.Ordinal))
        {
            if (_main.Document.Materials.Any(other => !ReferenceEquals(other, material) && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
                DialogLook.Warn(_problem, $"There is a material called {name} already.");
            else look = look with { Name = name };
        }

        if (ColourRgb.TryParse(_surface.Text, out var surface)) finish = finish with { Colour = surface };
        if (ColourRgb.TryParse(_cut.Text, out var cut)) look = look with { CutColour = cut };
        if (ColourRgb.TryParse(_jointColour.Text, out var joint)) finish = finish with { JointColour = joint };
        if (Units.TryParseLength(_length.Text, out var length) && length is > 0 and <= 100000) finish = finish with { UnitLength = length };
        if (Units.TryParseLength(_height.Text, out var height) && height is >= 0 and <= 100000) finish = finish with { UnitHeight = height };
        if (Units.TryParseLength(_joint.Text, out var gap) && gap is >= 0 and <= 1000) finish = finish with { JointWidth = gap };
        if (double.TryParse(_reflectance.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var reflectance) && reflectance is >= 0 and <= 1)
            finish = finish with { Reflectance = reflectance };

        Apply(material, look with { Finish = finish });
        ShowSelected();
    }

    private void Apply(Material material, MaterialLook look)
    {
        if (look == MaterialLook.Of(material)) return;
        _main.History.Execute(new SetMaterialCommand(material, look));
        DialogLook.Say(_problem, $"{look.Name} changed.");
    }

    /// <summary>A material added: a copy of the one given, or a new one laid as its name says.</summary>
    private void Add(Material? original)
    {
        var name = MaterialCopies.FreeName(_main.Document, original?.Name ?? "New Material");
        var material = original is null ? new Material(name) : MaterialCopies.Copy(original, name);
        if (original is null) SurfacePatterns.Settle(material);
        _main.History.Execute(new AddMaterialCommand(_main.Document, material));
        _search.Text = string.Empty;
        Fill();
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, material));
        _list.ScrollIntoView(_list.SelectedItem);
        _name.Focus();
        _name.SelectAll();
    }

    /// <summary>A face as it looks at a scale, pixels a millimetre: its pattern repeated, or its colour.</summary>
    private static Brush FaceBrush(SurfaceFinish finish, double scale)
    {
        if (SurfaceTextures.Of(finish) is not { } texture) return Solid(finish.Colour);
        var brush = new ImageBrush(ModelView.PictureOf(texture))
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, texture.Length * scale, texture.Rise * scale),
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        return brush;
    }

    private static Brush Solid(ColourRgb colour)
    {
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    private static Border Swatch() => new() { Width = 22, Height = 18, Margin = new Thickness(8, 0, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };

    private static string Label(SurfacePattern pattern) => pattern switch
    {
        SurfacePattern.None => "None - its colour",
        SurfacePattern.PlainTiles => "Plain roof tiles",
        SurfacePattern.InterlockingTiles => "Interlocking roof tiles",
        SurfacePattern.StandingSeam => "Standing seam",
        _ => EnumText.Humanise(pattern)
    };
}
