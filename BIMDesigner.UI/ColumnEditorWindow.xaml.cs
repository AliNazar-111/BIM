using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.UI;

/// <summary>
/// The column editor: a cross-section drawn square on, and the column it makes standing beside
/// it in 3D (specification section 16).
///
/// The section is the column. Everything drawn here ends in a boolean against one profile -
/// added to it, cut out of it, or kept where the two overlap - and that profile is what the
/// model runs up between the column's levels. So what is drawn here is what gets built, and the
/// 3D beside it is not a picture of the intention but the column itself, rebuilt on every edit.
///
/// Every shape can be given as numbers as well as drawn by hand, because a column is built to a
/// dimension: a section that can only be drawn by eye has to be measured afterwards, and a
/// drawing that has to be measured is a drawing nobody can build from.
/// </summary>
public partial class ColumnEditorWindow : System.Windows.Window
{
    /// <summary>A project of its own holding one column, so the preview is built by the real builder.</summary>
    private readonly BimDocument _preview = new();
    private readonly ColumnType _previewType;
    private readonly Column _previewColumn;

    private bool _loading;

    public ColumnEditorWindow(BimDocument document, ColumnType type)
    {
        InitializeComponent();

        Title = $"Column Editor — {type.Name}";

        // The preview is a real column of a copy of this type, in a project of its own: the
        // same geometry the model would build, not a drawing of it.
        _preview.AddLevel(new Level { Name = "Base", Elevation = 0 });
        foreach (var material in document.Materials) _preview.AddMaterial(material);

        _previewType = new ColumnType(type.Name, type.Shape, type.Width, type.Depth)
        {
            MaterialId = type.MaterialId,
            CoarseScaleFillColour = type.CoarseScaleFillColour,
            Shaping = type.Shaping.Copy()
        };

        _preview.AddType(_previewType);

        _previewColumn = new Column
        {
            TypeId = _previewType.Id,
            LevelId = _preview.Levels[0].Id,
            UnconnectedHeight = 3000
        };

        _preview.Add(_previewColumn);
        Preview.Document = _preview;

        _loading = true;

        ToolPicker.ItemsSource = new[] { "Rectangle", "Circle / ellipse", "Polygon", "Freeform" };
        ToolPicker.SelectedIndex = 0;

        OperationPicker.ItemsSource = new[] { "Cut  (A − B)", "Add  (A + B)", "Intersect  (A ∩ B)" };
        OperationPicker.SelectedIndex = 0;

        SnapPicker.ItemsSource = new[] { "Off", "1 mm", "5 mm", "10 mm", "25 mm", "50 mm" };
        SnapPicker.SelectedIndex = 3;

        // The ready-made sections, grouped the way they are worth looking through.
        StartShapePicker.ItemsSource = ColumnLibrary.All;
        StartShapePicker.SelectedIndex = 0;

        ShowShaping();

        Sketch.ProfileChanged += (_, _) => AfterEdit();
        Sketch.StatusChanged += (_, status) => ShowStatus(status);

        Sketch.Load(type.Profile);
        _loading = false;

        AfterEdit();
        Loaded += (_, _) =>
        {
            Sketch.Fit();
            Sketch.Focus();
        };
    }

    /// <summary>The section as it stands, once Save has been pressed.</summary>
    public ColumnProfile? Result { get; private set; }

    /// <summary>The shaping as it stands, so a column's whole form is settled in one place.</summary>
    public ColumnShaping? Shaping { get; private set; }

    /// <summary>Rebuilds the preview and the readouts from whatever the sketch now holds.</summary>
    private void AfterEdit()
    {
        var profile = Sketch.Profile;

        // The preview type carries the drawn section, so the builder makes exactly the column
        // this type would make.
        _previewType.Shape = ColumnShape.Custom;
        _previewType.CustomProfile = profile;

        var (width, depth) = profile.Extent;
        _previewType.Width = Math.Max(1, width);
        _previewType.Depth = Math.Max(1, depth);

        Preview.Rebuild();
        Preview.ZoomToFit();

        UndoButton.IsEnabled = Sketch.CanUndo;
        RedoButton.IsEnabled = Sketch.CanRedo;
        OkButton.IsEnabled = !profile.IsEmpty;

        var was = _loading;
        _loading = true;
        WidthBox.Text = ParameterFormatter.Format(ParameterDataType.Length, width);
        DepthBox.Text = ParameterFormatter.Format(ParameterDataType.Length, depth);
        _loading = was;

        AreaText.Text = profile.IsEmpty
            ? "Nothing left: the section has been cut away."
            : $"Section area {ParameterFormatter.Format(ParameterDataType.Area, profile.Area)}" +
              (profile.Holes.Count > 0 ? $"   ·   {Plural(profile.Holes.Count, "hole")}" : string.Empty);
    }

    private void ShowStatus(ProfileSketch.SketchStatus status) =>
        StatusText.Text = status.Drawing
            ? $"{status.Width:0} × {status.Depth:0} mm    at  X {status.At.X:0}   Y {status.At.Y:0}"
            : $"X {status.At.X:0} mm    Y {status.At.Y:0} mm    Scale {Sketch.ScalePercent:0}%    " +
              $"Snap {(Sketch.SnapOn ? $"{Sketch.SnapStep:0} mm" : "off")}    " +
              "·   wheel zooms, middle-drag pans";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // ---- the toolbar -----------------------------------------------------------

    private void OnToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Sketch is null) return;

        Sketch.Tool = ToolPicker.SelectedIndex switch
        {
            1 => SketchTool.Circle,
            2 => SketchTool.Polygon,
            3 => SketchTool.Freeform,
            _ => SketchTool.Rectangle
        };

        if (Sketch.Tool == SketchTool.Freeform)
            StatusText.Text = "Click each corner. Double-click or Enter closes the shape; Backspace takes one back.";
    }

    private void OnOperationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Sketch is null) return;

        Sketch.Operation = OperationPicker.SelectedIndex switch
        {
            1 => BooleanOperation.Union,
            2 => BooleanOperation.Intersection,
            _ => BooleanOperation.Difference
        };
    }

    private void OnSidesChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SidesBox.Text, out var sides) && sides >= 3) Sketch.PolygonSides = sides;
        SidesBox.Text = Sketch.PolygonSides.ToString();
    }

    private void OnSnapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Sketch is null) return;

        Sketch.SnapStep = SnapPicker.SelectedIndex switch
        {
            1 => 1, 2 => 5, 3 => 10, 4 => 25, 5 => 50, _ => 0
        };
    }

    /// <summary>Puts a shape of exactly the size and place typed into the toolbar.</summary>
    private void OnApplyShape(object sender, RoutedEventArgs e)
    {
        var width = Length(ShapeWidthBox, 0);
        var depth = Length(ShapeDepthBox, 0);

        if (width <= 0 || depth <= 0)
        {
            StatusText.Text = "Give the shape a width and a depth first.";
            return;
        }

        Sketch.AddShape(width, depth, new Point2D(Length(ShapeXBox, 0), Length(ShapeYBox, 0)));
    }

    private void OnFit(object sender, RoutedEventArgs e) => Sketch.Fit();

    private void OnZoomIn(object sender, RoutedEventArgs e) => Sketch.ZoomBy(1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Sketch.ZoomBy(1 / 1.25);

    private void OnUndo(object sender, RoutedEventArgs e) => Sketch.Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Sketch.Redo();

    // ---- the section -----------------------------------------------------------

    private void OnCentre(object sender, RoutedEventArgs e) => Sketch.CentreOnOrigin();

    private void OnRound(object sender, RoutedEventArgs e) => Sketch.RoundCorners(CornerSize, round: true);

    private void OnChamfer(object sender, RoutedEventArgs e) => Sketch.RoundCorners(CornerSize, round: false);

    private double CornerSize => Math.Max(1, Length(CornerBox, 40));

    private void OnRotate(object sender, RoutedEventArgs e) => Sketch.Turn(90);

    private void OnMirrorAcross(object sender, RoutedEventArgs e) => Sketch.MirrorAcross();

    private void OnMirrorUp(object sender, RoutedEventArgs e) => Sketch.MirrorUp();

    /// <summary>
    /// Replaces the section with a ready-made one, at whatever size the section is now - so
    /// picking a different one keeps the size that has already been decided.
    /// </summary>
    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (StartShapePicker.SelectedItem is not ColumnLibrary.Preset preset) return;

        var (width, depth) = Sketch.Profile.IsEmpty ? (400.0, 400.0) : Sketch.Profile.Extent;
        Sketch.Reset(preset.At(Math.Max(100, width), Math.Max(100, depth)));

        StatusText.Text = $"{preset}. Cut and add on top of it, or pick another.";
    }

    private void OnSizeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var width = Length(WidthBox, Sketch.Profile.Extent.Width);
        var depth = Length(DepthBox, Sketch.Profile.Extent.Depth);

        if (width > 0 && depth > 0) Sketch.ResizeTo(width, depth);
        else AfterEdit();
    }

    // ---- what the column does on the way up ------------------------------------

    private void ShowShaping()
    {
        var shaping = _previewType.Shaping;

        HeightBox.Text = ParameterFormatter.Format(ParameterDataType.Length, _previewColumn.UnconnectedHeight);
        TopScaleBox.Text = $"{shaping.TopScale * 100:0}%";
        TwistBox.Text = $"{shaping.Twist:0}°";
        SlantBox.Text = ParameterFormatter.Format(ParameterDataType.Length, shaping.SlantAcross);
        FlutesBox.Text = shaping.Flutes.ToString();
        BaseBox.Text = ParameterFormatter.Format(ParameterDataType.Length, shaping.BaseHeight);
        CapitalBox.Text = ParameterFormatter.Format(ParameterDataType.Length, shaping.CapitalHeight);
    }

    private void OnShapingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var shaping = _previewType.Shaping;

        _previewColumn.UnconnectedHeight = Math.Max(100, Length(HeightBox, _previewColumn.UnconnectedHeight));
        shaping.TopScale = Math.Clamp(Number(TopScaleBox, shaping.TopScale * 100) / 100, 0.05, 4);
        shaping.Twist = Number(TwistBox, shaping.Twist);
        shaping.SlantAcross = Length(SlantBox, shaping.SlantAcross);
        shaping.Flutes = (int)Math.Clamp(Number(FlutesBox, shaping.Flutes), 0, 96);
        shaping.BaseHeight = Math.Max(0, Length(BaseBox, shaping.BaseHeight));
        shaping.CapitalHeight = Math.Max(0, Length(CapitalBox, shaping.CapitalHeight));

        _loading = true;
        ShowShaping();
        _loading = false;

        AfterEdit();
    }

    private static double Length(TextBox box, double fallback) =>
        ParameterFormatter.TryParse(ParameterDataType.Length, box.Text, out var value) && value is double millimetres
            ? millimetres
            : fallback;

    /// <summary>A plain number typed with or without its unit, for the percentages and angles.</summary>
    private static double Number(TextBox box, double fallback)
    {
        var text = new string(box.Text.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(text, out var value) ? value : fallback;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Sketch.Profile.IsEmpty)
        {
            StatusText.Text = "There is no section left to save. Undo, or start again from a shape.";
            return;
        }

        Result = Sketch.Profile;
        Shaping = _previewType.Shaping.Copy();
        DialogResult = true;
    }
}
