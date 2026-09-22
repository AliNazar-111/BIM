using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.UI.Controls;
using BIMDesigner.UI.ViewModels;
using Window = System.Windows.Window;

namespace BIMDesigner.UI;

/// <summary>
/// Edits a straight wall's elevation outline (specification section 3.1, "edit profile"). The
/// drawing and the corners table are two views of the same corners; nothing reaches the wall
/// until OK, which checks the outline first.
/// </summary>
public partial class EditProfileWindow : Window
{
    private readonly double _length;
    private readonly double _height;
    private readonly ObservableCollection<ProfileCornerRow> _rows = new();
    private bool _syncing;

    public EditProfileWindow(BimDocument document, Wall wall)
    {
        InitializeComponent();
        ScreenFit.Apply(this);

        _length = wall.Length;
        _height = wall.GetHeight(document);

        WallCaption.Text = $"ELEVATION  -  {Units.FormatLength(_length)} LONG, ITS START ON THE LEFT";
        RemoveProfileButton.IsEnabled = wall.Profile is not null;

        var holes = WallOpenings.Of(document, wall)
            .Select(opening => (Opening: opening, Type: document.FindType<OpeningType>(opening.TypeId)))
            .Where(entry => entry.Type is not null)
            .Select(entry =>
            {
                var (from, to) = entry.Opening.GetSpan(entry.Type!);
                return (from, to, entry.Opening.SillHeight, entry.Opening.SillHeight + entry.Type!.Height);
            })
            .ToList();

        Editor.Show(WallProfile.Of(document, wall) ?? WallProfile.Rectangle(_length, _height), _length, _height, holes);
        Editor.Edited += (_, _) =>
        {
            FromDrawing();
            ShowSelectedEdge();
        };
        Editor.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            CornerGrid.SelectedIndex = Editor.SelectedIndex;
            _syncing = false;
            ShowSelectedEdge();
        };

        CornerGrid.ItemsSource = _rows;
        FromDrawing();
    }

    /// <summary>The outline to give the wall when OK is pressed, or null to take its profile away.</summary>
    public IReadOnlyList<Point2D>? Result { get; private set; }

    /// <summary>The table follows the drawing: same rows where it can, rebuilt when corners come and go.</summary>
    private void FromDrawing()
    {
        var corners = Editor.Corners;

        if (_rows.Count == corners.Count)
        {
            for (var i = 0; i < corners.Count; i++) _rows[i].Set(corners[i]);
        }
        else
        {
            _rows.Clear();
            for (var i = 0; i < corners.Count; i++)
            {
                var row = new ProfileCornerRow(i + 1, corners[i]);
                row.Edited += (_, _) => FromTable();
                _rows.Add(row);
            }
        }

        ShowProblem();
    }

    /// <summary>A value typed in the table moves the corner on the drawing, once every row reads as lengths.</summary>
    private void FromTable()
    {
        if (_rows.Any(row => row.Corner is null))
        {
            ProblemText.Text = "Every corner needs a length along and a height, such as 1500 or 1.5 m.";
            OkButton.IsEnabled = false;
            return;
        }

        Editor.SetCorners(_rows.Select(row => row.Corner!.Value));
        ShowProblem();
    }

    private void ShowProblem()
    {
        var problem = WallProfile.Problem(Editor.Corners, _length);
        ProblemText.Text = problem ?? string.Empty;
        OkButton.IsEnabled = problem is null;
    }

    private void Replace(IEnumerable<Point2D> corners)
    {
        Editor.SetCorners(corners);
        FromDrawing();
    }

    private void OnCornerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Editor.Select(CornerGrid.SelectedIndex);
        _syncing = false;
        ShowSelectedEdge();
    }

    /// <summary>Shows the selected corner's edge from the one before, ready to be typed over.</summary>
    private void ShowSelectedEdge()
    {
        var index = Editor.SelectedIndex;
        if (index < 0 || Editor.Corners.Count < 3)
        {
            PlaceLengthBox.Text = PlaceAngleBox.Text = string.Empty;
            return;
        }

        var previous = Editor.Corners[(index + Editor.Corners.Count - 1) % Editor.Corners.Count];
        var corner = Editor.Corners[index];
        PlaceLengthBox.Text = Units.FormatLength(previous.DistanceTo(corner));
        PlaceAngleBox.Text = $"{ProfileEditor.AngleBetween(previous, corner):0.##}";
    }

    private void OnPlaceCorner(object sender, RoutedEventArgs e)
    {
        if (Editor.SelectedIndex < 0)
        {
            ProblemText.Text = "Select a corner to place, on the drawing or in the table.";
            return;
        }

        if (!ParameterFormatter.TryParse(ParameterDataType.Length, PlaceLengthBox.Text, out var parsed) ||
            parsed is not double length || length <= 0)
        {
            ProblemText.Text = "Type how far from the corner before, such as 1500 or 1.5 m.";
            return;
        }

        if (!double.TryParse(PlaceAngleBox.Text.Replace("°", string.Empty).Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var angle) &&
            !double.TryParse(PlaceAngleBox.Text.Replace("°", string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out angle))
        {
            ProblemText.Text = "Type an angle in degrees: 0 along the wall, 90 straight up, 270 down.";
            return;
        }

        Editor.PlaceFromPrevious(length, angle);
        FromDrawing();
        ShowSelectedEdge();
    }

    private void OnAngleStepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Editor is null) return;
        Editor.AngleStep = AngleStepPicker.SelectedIndex switch { 0 => 90, 1 => 45, 2 => 15, _ => 0 };
    }

    private void OnAddCorner(object sender, RoutedEventArgs e) => Editor.AddCorner();

    private void OnRemoveCorner(object sender, RoutedEventArgs e) => Editor.RemoveCorner();

    private void OnRectangle(object sender, RoutedEventArgs e) => Replace(WallProfile.Rectangle(_length, _height));

    private void OnGable(object sender, RoutedEventArgs e) => Replace(new[]
    {
        new Point2D(0, 0), new Point2D(_length, 0), new Point2D(_length, _height),
        new Point2D(_length / 2, _height + _length / 2), new Point2D(0, _height)
    });

    private void OnStepped(object sender, RoutedEventArgs e) => Replace(new[]
    {
        new Point2D(0, 0), new Point2D(_length, 0),
        new Point2D(_length, _height / 3), new Point2D(_length * 2 / 3, _height / 3),
        new Point2D(_length * 2 / 3, _height * 2 / 3), new Point2D(_length / 3, _height * 2 / 3),
        new Point2D(_length / 3, _height), new Point2D(0, _height)
    });

    private void OnRemoveProfile(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (WallProfile.Problem(Editor.Corners, _length) is { } problem)
        {
            ProblemText.Text = problem;
            return;
        }

        Result = Editor.Corners.ToList();
        DialogResult = true;
    }
}
