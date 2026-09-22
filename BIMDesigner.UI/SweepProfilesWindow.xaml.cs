using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI.ViewModels;
using Window = System.Windows.Window;

namespace BIMDesigner.UI;

/// <summary>
/// The sweep profiles drawn in the project (Revit's profile families): any shape a skirting,
/// dado rail, cornice or coping is to have, drawn corner by corner out from the wall face.
/// A profile is used by picking it in a wall type's sweeps or in a sweep type.
/// </summary>
public partial class SweepProfilesWindow : Window
{
    private readonly BimDocument _document;
    private readonly UndoStack _history;
    private readonly ObservableCollection<ProfileCornerRow> _rows = new();
    private SweepProfileType? _current;
    private bool _loading;
    private bool _dirty;

    public SweepProfilesWindow(BimDocument document, UndoStack history)
    {
        InitializeComponent();
        ScreenFit.Apply(this);

        _document = document;
        _history = history;

        CornerGrid.ItemsSource = _rows;
        Editor.Edited += (_, _) => FromDrawing();
        Editor.SelectionChanged += (_, _) => CornerGrid.SelectedIndex = Editor.SelectedIndex;

        ShowProfiles(_document.TypesOf<SweepProfileType>().OrderBy(p => p.Name).FirstOrDefault());
    }

    /// <summary>Raised whenever a change reaches the model.</summary>
    public event EventHandler? Changed;

    private void ShowProfiles(SweepProfileType? select)
    {
        _loading = true;
        ProfileList.ItemsSource = _document.TypesOf<SweepProfileType>().OrderBy(p => p.Name).ToList();
        ProfileList.SelectedItem = select;
        _loading = false;
        Load(select);
    }

    private void Load(SweepProfileType? profile)
    {
        _current = profile;
        _loading = true;
        NameBox.Text = profile?.Name ?? string.Empty;
        NameBox.IsEnabled = Editor.IsEnabled = CornerGrid.IsEnabled = profile is not null;

        var points = profile?.Points.ToList() ?? new List<Point2D>();
        var reach = Math.Max(300, points.Count == 0 ? 0 : points.Max(p => p.X) * 1.5);
        var height = Math.Max(300, points.Count == 0 ? 0 : points.Max(p => p.Y) * 1.3);
        Editor.Show(points, reach, height, Array.Empty<(double, double, double, double)>());

        _rows.Clear();
        _loading = false;
        FromDrawing();
        _dirty = false;
        UpdateState();
    }

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

        MarkChanged();
    }

    private void FromTable()
    {
        if (_rows.Any(r => r.Corner is null))
        {
            ProblemText.Text = "Every corner needs two lengths, such as 20 or 1.5 m.";
            ApplyButton.IsEnabled = false;
            return;
        }

        Editor.SetCorners(_rows.Select(r => r.Corner!.Value));
        MarkChanged();
    }

    private void MarkChanged()
    {
        if (_loading) return;
        _dirty = true;
        UpdateState();
    }

    private string? Problem()
    {
        if (_current is null) return null;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return "The profile needs a name.";
        if (_document.ElementTypes.Any(t => !ReferenceEquals(t, _current) && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"Another type is already called {name}.";
        return SweepProfileType.Problem(Editor.Corners);
    }

    private void UpdateState()
    {
        var problem = Problem();
        ProblemText.Text = problem ?? (_dirty ? "Not applied yet: every sweep drawn with this profile will change." : string.Empty);
        ApplyButton.IsEnabled = _dirty && problem is null && _current is not null;
        DeleteButton.IsEnabled = _current is not null;
    }

    private bool SettleChanges()
    {
        if (!_dirty || _current is null) return true;
        return MessageBox.Show(this, $"Apply your changes to {_current.Name}?", "Sweep Profiles",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes || Apply();
    }

    private bool Apply()
    {
        if (_current is null) return true;
        if (Problem() is { } problem)
        {
            ProblemText.Text = problem;
            return false;
        }

        _history.Execute(new EditSweepProfileCommand(_current, NameBox.Text, Editor.Corners));
        _dirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
        ShowProfiles(_current);
        return true;
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ReferenceEquals(ProfileList.SelectedItem, _current)) return;
        if (!SettleChanges())
        {
            _loading = true;
            ProfileList.SelectedItem = _current;
            _loading = false;
            return;
        }

        Load(ProfileList.SelectedItem as SweepProfileType);
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => MarkChanged();

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (!SettleChanges()) return;

        var profile = new SweepProfileType(TypeNames.Unique(_document, "Profile"));
        profile.Points.AddRange(Board());
        _history.Execute(new AddTypeCommand(_document, profile));
        Changed?.Invoke(this, EventArgs.Empty);
        ShowProfiles(profile);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_current is null || !SettleChanges()) return;

        var copy = _current.Duplicate(TypeNames.Unique(_document, _current.Name));
        _history.Execute(new AddTypeCommand(_document, copy));
        Changed?.Invoke(this, EventArgs.Empty);
        ShowProfiles(copy);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (EditSweepProfileCommand.InUse(_document, _current))
        {
            MessageBox.Show(this, $"{_current.Name} is used by a sweep. Give the sweep another profile first.",
                "Sweep Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _history.Execute(new DeleteTypeCommand(_document, _current));
        _dirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
        ShowProfiles(_document.TypesOf<SweepProfileType>().OrderBy(p => p.Name).FirstOrDefault());
    }

    private void OnCornerSelected(object sender, SelectionChangedEventArgs e) => Editor.Select(CornerGrid.SelectedIndex);

    private void OnAddCorner(object sender, RoutedEventArgs e) => Editor.AddCorner();

    private void OnRemoveCorner(object sender, RoutedEventArgs e) => Editor.RemoveCorner();

    private void Replace(IEnumerable<Point2D> points)
    {
        Editor.SetCorners(points);
        FromDrawing();
    }

    private static Point2D[] Board() => new Point2D[] { new(0, 0), new(20, 0), new(20, 100), new(0, 100) };

    private void OnBoard(object sender, RoutedEventArgs e) => Replace(Board());

    /// <summary>A quarter-round bead: flat on the wall, curved out to the room.</summary>
    private void OnOvolo(object sender, RoutedEventArgs e) =>
        Replace(new[] { new Point2D(0, 0) }
            .Concat(Enumerable.Range(0, 9).Select(i => i * Math.PI / 16).Select(a => new Point2D(Math.Round(30 * Math.Cos(a), 1), Math.Round(30 * Math.Sin(a), 1)))));

    private void OnStepped(object sender, RoutedEventArgs e) => Replace(new Point2D[]
    {
        new(0, 0), new(40, 0), new(40, 30), new(25, 30), new(25, 60), new(10, 60), new(10, 90), new(0, 90)
    });
}
