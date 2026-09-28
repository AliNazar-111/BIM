using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI;

/// <summary>The shapes Add Shapes can put the walls of a room into.</summary>
public enum WallShapeKind
{
    Rectangle,
    Square,
    Polygon
}

/// <summary>
/// Asks what size the walls of a room should be put into a shape at: a width and a depth for a
/// rectangle, one side for a square or a regular polygon - measured between the walls' centres
/// or their inside or outside faces. It opens on the size they are now, so a plain OK just
/// straightens the shape up.
/// </summary>
public partial class WallShapeWindow : System.Windows.Window
{
    private readonly WallShapeKind _kind;
    private readonly WallRectangle? _rectangle;
    private readonly WallPolygon? _polygon;

    public WallShapeWindow(WallRectangle rectangle, bool square)
        : this(square ? WallShapeKind.Square : WallShapeKind.Rectangle, rectangle, null)
    {
    }

    public WallShapeWindow(WallPolygon polygon)
        : this(WallShapeKind.Polygon, null, polygon)
    {
    }

    private WallShapeWindow(WallShapeKind kind, WallRectangle? rectangle, WallPolygon? polygon)
    {
        InitializeComponent();

        _kind = kind;
        _rectangle = rectangle;
        _polygon = polygon;

        var name = kind switch
        {
            WallShapeKind.Square => "Square",
            WallShapeKind.Polygon => polygon!.Name,
            _ => "Rectangle"
        };

        Title = kind == WallShapeKind.Polygon ? $"Regular Polygon - {name}" : name;
        OkButton.Content = $"Make {name}";
        ShapeImage.Source = (ImageSource)FindResource(kind switch
        {
            WallShapeKind.Square => "Icon.ShapeSquare",
            WallShapeKind.Polygon => "Icon.ShapePolygon",
            _ => "Icon.ShapeRectangle"
        });

        // A rectangle has a width and a depth; a square and a regular polygon one side.
        var oneSize = kind != WallShapeKind.Rectangle;
        WidthLabel.Text = oneSize ? "Side" : "Width";
        WidthBox.ToolTip = oneSize ? "The length every side will be" : "The size left to right across the page";
        DepthLabel.Visibility = DepthBox.Visibility = oneSize ? Visibility.Collapsed : Visibility.Visible;

        StatusText.Text = kind == WallShapeKind.Polygon
            ? "The room keeps its middle; every side comes out the same length and every corner the same angle. Walls ending at a corner go with it."
            : "The room keeps its middle; its corners go square. Walls ending at a corner go with it.";

        MeasurePicker.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    /// <summary>The change to make, once the window is finished.</summary>
    public IUndoableCommand? Command { get; private set; }

    /// <summary>What was made, in words, for the status bar.</summary>
    public string Made { get; private set; } = string.Empty;

    private RectangleMeasure Measure => MeasurePicker.SelectedIndex switch
    {
        1 => RectangleMeasure.InsideFaces,
        2 => RectangleMeasure.OutsideFaces,
        _ => RectangleMeasure.WallCentres
    };

    private string MeasureWords => Measure switch
    {
        RectangleMeasure.InsideFaces => "between their inside faces",
        RectangleMeasure.OutsideFaces => "between their outside faces",
        _ => "between their centres"
    };

    private static double Round(double length) => Math.Round(length / 10) * 10;

    /// <summary>The size the walls are now, measured the way chosen, to the nearest 10 mm.</summary>
    private void Fill()
    {
        if (_polygon is not null)
        {
            var side = Round(_polygon.Measure(Measure));
            WidthBox.Text = Units.FormatLength(side);
            NowText.Text = $"The {_polygon.Count} walls' sides are now {Units.FormatLength(side)} long {MeasureWords}, on average. " +
                           "Type the length every side should be.";
            return;
        }

        var (width, depth) = _rectangle!.Measure(Measure);
        (width, depth) = (Round(width), Round(depth));

        if (_kind == WallShapeKind.Square)
        {
            WidthBox.Text = Units.FormatLength(Round((width + depth) / 2));
            NowText.Text = $"The four walls are now {Units.FormatLength(width)} by {Units.FormatLength(depth)} {MeasureWords}. " +
                           "Type the length all four sides should be.";
            return;
        }

        WidthBox.Text = Units.FormatLength(width);
        DepthBox.Text = Units.FormatLength(depth);
        NowText.Text = $"The four walls are now {Units.FormatLength(width)} by {Units.FormatLength(depth)} {MeasureWords}, " +
                       "near enough. Type the size the room should be.";
    }

    private void OnMeasureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || WidthBox is null) return;
        Fill();
    }

    private static double? Length(string text) =>
        ParameterFormatter.TryParse(ParameterDataType.Length, text, out var value) && value is double length ? length : null;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Length(WidthBox.Text) is not { } first)
        {
            StatusText.Text = "Type a length, such as 4000 or 4 m.";
            return;
        }

        IUndoableCommand? command;
        string? problem;

        switch (_kind)
        {
            case WallShapeKind.Polygon:
                command = _polygon!.Make(first, Measure, out problem);
                Made = $"A {_polygon.Name.ToLowerInvariant()}, every side {Units.FormatLength(first)}.";
                break;

            case WallShapeKind.Square:
                command = _rectangle!.Make(first, first, Measure, out problem);
                Made = $"A square, {Units.FormatLength(first)} each way.";
                break;

            default:
                if (Length(DepthBox.Text) is not { } depth)
                {
                    StatusText.Text = "Type a depth, such as 3000 or 3 m.";
                    return;
                }

                command = _rectangle!.Make(first, depth, Measure, out problem);
                Made = $"A rectangle, {Units.FormatLength(first)} by {Units.FormatLength(depth)}.";
                break;
        }

        if (command is null)
        {
            StatusText.Text = problem ?? "Those walls cannot be made that size.";
            return;
        }

        Command = command;
        DialogResult = true;
    }
}
