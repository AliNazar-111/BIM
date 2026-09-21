using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.UI.ViewModels;

using Window = System.Windows.Window;

namespace BIMDesigner.UI;

/// <summary>
/// The wall types of the project, and the build-up of each (specification sections 3.1 and 13.1).
///
/// A type is shared by every wall of it, so an edit here is not applied keystroke by keystroke:
/// the build-up is assembled and checked on the right, previewed, and applied with Apply as a
/// single step that undo can reverse. Making a new type is always by duplicating one, so it
/// starts as something that already works.
/// </summary>
public partial class WallTypesWindow : Window
{
    private readonly BimDocument _document;
    private readonly UndoStack _history;
    private readonly ObservableCollection<LayerDraftRow> _rows = new();

    private WallType? _editing;
    private bool _loading;
    private bool _dirty;

    public WallTypesWindow(BimDocument document, UndoStack history, WallType? start)
    {
        InitializeComponent();

        _document = document;
        _history = history;

        FunctionPicker.ItemsSource = EnumText.Choices<WallFunction>();
        InsertWrapPicker.ItemsSource = EnumText.Choices<WallWrapping>();
        EndWrapPicker.ItemsSource = new[] { WallWrapping.None, WallWrapping.Exterior, WallWrapping.Interior }
            .Select(wrapping => EnumText.Humanise(wrapping))
            .ToList();

        LayerGrid.ItemsSource = _rows;

        ShowTypes(start ?? _document.TypesOf<WallType>().OrderBy(t => t.Name).FirstOrDefault());
    }

    /// <summary>Raised whenever a change reaches the model, so the owner can repaint and resync.</summary>
    public event EventHandler? Changed;

    private sealed record TypeItem(WallType Type, string Name, string Summary);

    private LayerDraftRow? SelectedRow => LayerGrid.SelectedItem as LayerDraftRow;

    // ---- the list of types ----------------------------------------------------------

    private void ShowTypes(WallType? select)
    {
        var items = _document.TypesOf<WallType>()
            .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(type =>
            {
                var uses = _document.ElementsOfType(type).Count();
                var walls = uses == 1 ? "1 wall" : $"{uses} walls";
                return new TypeItem(type, type.Name, $"{Units.FormatLength(type.Width)}  ·  {walls}");
            })
            .ToList();

        _loading = true;
        TypeList.ItemsSource = items;
        TypeList.SelectedItem = items.FirstOrDefault(item => ReferenceEquals(item.Type, select));
        _loading = false;

        Load(select);
    }

    private void OnTypeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var chosen = (TypeList.SelectedItem as TypeItem)?.Type;
        if (ReferenceEquals(chosen, _editing)) return;

        if (!SettlePendingChanges())
        {
            // Stay on the type with the unfinished edit.
            _loading = true;
            TypeList.SelectedItem = TypeList.Items.OfType<TypeItem>().FirstOrDefault(i => ReferenceEquals(i.Type, _editing));
            _loading = false;
            return;
        }

        ShowTypes(chosen);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } source || !SettlePendingChanges()) return;

        var copy = source.Duplicate(TypeNames.Unique(_document, source.Name));
        _history.Execute(new AddTypeCommand(_document, copy));
        Changed?.Invoke(this, EventArgs.Empty);

        ShowTypes(copy);

        // A duplicate is made to be changed, and the first change is nearly always its name.
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } type) return;

        var uses = _document.ElementsOfType(type).Count();
        if (uses > 0)
        {
            MessageBox.Show(this,
                $"{uses} {(uses == 1 ? "wall uses" : "walls use")} {type.Name}.\n\nGive {(uses == 1 ? "it" : "them")} another type first - select them and pick one in Properties.",
                "Delete Wall Type", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_document.TypesOf<WallType>().Count() <= 1)
        {
            MessageBox.Show(this, "A project needs at least one wall type.", "Delete Wall Type",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _history.Execute(new DeleteTypeCommand(_document, type));
        _dirty = false;
        Changed?.Invoke(this, EventArgs.Empty);

        ShowTypes(_document.TypesOf<WallType>().OrderBy(t => t.Name).FirstOrDefault());
    }

    // ---- the type being edited --------------------------------------------------------

    private void Load(WallType? type)
    {
        _editing = type;
        _loading = true;

        foreach (var row in _rows) row.Edited -= OnRowEdited;
        _rows.Clear();

        if (type is not null)
        {
            NameBox.Text = type.Name;
            FunctionPicker.SelectedItem = EnumText.Humanise(type.Function);
            InsertWrapPicker.SelectedItem = EnumText.Humanise(type.WrapAtInserts);
            EndWrapPicker.SelectedItem = EnumText.Humanise(type.WrapAtEnds);

            foreach (var layer in type.Structure.Layers) AddRow(new LayerDraftRow(_document, layer));
        }
        else
        {
            NameBox.Text = string.Empty;
        }

        _loading = false;
        _dirty = false;

        UpdateState();
    }

    private void AddRow(LayerDraftRow row, int index = -1)
    {
        row.Edited += OnRowEdited;
        if (index < 0 || index > _rows.Count) _rows.Add(row);
        else _rows.Insert(index, row);
    }

    private void OnRowEdited(object? sender, EventArgs e) => MarkChanged();

    private void OnDesignChanged(object sender, RoutedEventArgs e) => MarkChanged();

    private void MarkChanged()
    {
        if (_loading) return;

        _dirty = true;
        UpdateState();
    }

    /// <summary>The build-up as it stands in the editor, or null while a thickness is not a length.</summary>
    private WallTypeDesign? Design()
    {
        var layers = _rows.Select(row => row.ToLayer()).ToList();
        if (layers.Any(layer => layer is null)) return null;

        return new WallTypeDesign(
            NameBox.Text,
            EnumText.TryParse<WallFunction>(FunctionPicker.SelectedItem as string, out var function) ? function : WallFunction.Interior,
            EnumText.TryParse<WallWrapping>(InsertWrapPicker.SelectedItem as string, out var inserts) ? inserts : WallWrapping.None,
            EnumText.TryParse<WallWrapping>(EndWrapPicker.SelectedItem as string, out var ends) ? ends : WallWrapping.None,
            layers!);
    }

    private string? Problem()
    {
        if (_editing is null) return null;

        var design = Design();
        return design is null
            ? "Every layer except a membrane needs a thickness greater than zero, e.g. 100 or 12.5 mm."
            : design.Problem(_document, _editing);
    }

    /// <summary>Brings the core markers, the preview, the width and the buttons up to date.</summary>
    private void UpdateState()
    {
        var coreStart = -1;
        var coreEnd = -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Function != LayerFunction.Structure) continue;
            if (coreStart < 0) coreStart = i;
            coreEnd = i;
        }

        for (var i = 0; i < _rows.Count; i++)
            _rows[i].IsCore = coreStart >= 0 && i >= coreStart && i <= coreEnd;

        Preview.Show(_rows.Select(row => (row.Thickness, row.Swatch)).ToList(), coreStart, coreEnd);

        var total = _rows.Where(row => row.Thickness > 0).Sum(row => row.Thickness);
        WidthText.Text = Units.FormatLength(total);

        var problem = Problem();
        ProblemText.Text = problem ?? (_dirty
            ? $"Not applied yet. Every wall of this type will change: {_document.ElementsOfType(_editing!).Count()} in the project."
            : string.Empty);
        ProblemText.Foreground = problem is null
            ? (Brush)FindResource("Text.Muted")
            : new SolidColorBrush(Color.FromRgb(0xE8, 0x80, 0x6A));

        ApplyButton.IsEnabled = _dirty && problem is null;
        RevertButton.IsEnabled = _dirty;
        DeleteButton.IsEnabled = _editing is not null;

        var index = SelectedRow is { } selected ? _rows.IndexOf(selected) : -1;
        RemoveLayerButton.IsEnabled = index >= 0 && _rows.Count > 1;
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index >= 0 && index < _rows.Count - 1;
    }

    // ---- layers ---------------------------------------------------------------------

    private void OnLayerSelected(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void OnAddLayer(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;

        // A new layer starts as a copy of the one it goes below, which is usually the nearest
        // thing to what is wanted; failing that, a thin interior finish.
        var template = SelectedRow?.ToLayer()
                       ?? new MaterialLayer(LayerFunction.Finish2,
                           _document.Materials.OrderBy(m => m.Name).First().Id, 15);

        var index = SelectedRow is { } selected ? _rows.IndexOf(selected) + 1 : _rows.Count;
        var row = new LayerDraftRow(_document, template);
        AddRow(row, index);

        LayerGrid.SelectedItem = row;
        MarkChanged();
    }

    private void OnRemoveLayer(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || _rows.Count <= 1) return;

        var index = _rows.IndexOf(row);
        row.Edited -= OnRowEdited;
        _rows.Remove(row);

        LayerGrid.SelectedItem = _rows[Math.Min(index, _rows.Count - 1)];
        MarkChanged();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int step)
    {
        if (SelectedRow is not { } row) return;

        var index = _rows.IndexOf(row);
        var target = index + step;
        if (target < 0 || target >= _rows.Count) return;

        _rows.Move(index, target);
        LayerGrid.SelectedItem = row;
        MarkChanged();
    }

    // ---- applying -------------------------------------------------------------------

    private bool Apply()
    {
        if (_editing is null || !_dirty) return true;

        if (Problem() is { } problem)
        {
            MessageBox.Show(this, problem, "Wall Type", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _history.Execute(new EditWallTypeCommand(_editing, Design()!));
        _dirty = false;
        Changed?.Invoke(this, EventArgs.Empty);

        ShowTypes(_editing);
        return true;
    }

    /// <summary>
    /// Before leaving a type with unapplied edits: apply them, throw them away, or stay.
    /// Returns false to stay.
    /// </summary>
    private bool SettlePendingChanges()
    {
        if (!_dirty || _editing is null) return true;

        var answer = MessageBox.Show(this,
            $"Apply your changes to {_editing.Name}?",
            "Wall Type", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => Apply(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnRevert(object sender, RoutedEventArgs e) => Load(_editing);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!SettlePendingChanges()) e.Cancel = true;
    }
}
