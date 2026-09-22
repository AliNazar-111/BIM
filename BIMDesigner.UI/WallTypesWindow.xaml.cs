using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
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
///
/// Stacked types are listed with the rest. Choosing one swaps the layer editor for a tier
/// editor, since a stacked wall is built of other wall types rather than of layers.
/// </summary>
public partial class WallTypesWindow : Window
{
    private readonly BimDocument _document;
    private readonly UndoStack _history;
    private readonly ObservableCollection<LayerDraftRow> _rows = new();
    private readonly ObservableCollection<TierDraftRow> _tiers = new();
    private readonly ObservableCollection<SweepDraftRow> _sweeps = new();

    private WallType? _editing;
    private StackedWallType? _stacked;
    private bool _loading;
    private bool _dirty;

    public WallTypesWindow(BimDocument document, UndoStack history, ElementType? start)
    {
        InitializeComponent();
        ScreenFit.Apply(this);

        _document = document;
        _history = history;

        FunctionPicker.ItemsSource = EnumText.Choices<WallFunction>();
        InsertWrapPicker.ItemsSource = EnumText.Choices<WallWrapping>();
        EndWrapPicker.ItemsSource = new[] { WallWrapping.None, WallWrapping.Exterior, WallWrapping.Interior }
            .Select(wrapping => EnumText.Humanise(wrapping))
            .ToList();

        LayerGrid.ItemsSource = _rows;
        TierGrid.ItemsSource = _tiers;
        SweepGrid.ItemsSource = _sweeps;

        ShowTypes(start is WallType or StackedWallType
            ? start
            : _document.TypesOf<WallType>().OrderBy(t => t.Name).FirstOrDefault());
    }

    /// <summary>Raised whenever a change reaches the model, so the owner can repaint and resync.</summary>
    public event EventHandler? Changed;

    private sealed record TypeItem(ElementType Type, string Name, string Summary);

    private LayerDraftRow? SelectedRow => LayerGrid.SelectedItem as LayerDraftRow;

    private TierDraftRow? SelectedTier => TierGrid.SelectedItem as TierDraftRow;

    /// <summary>The type being edited, whichever kind it is.</summary>
    private ElementType? Current => (ElementType?)_editing ?? _stacked;

    // ---- the list of types ----------------------------------------------------------

    private void ShowTypes(ElementType? select)
    {
        string Walls(ElementType type)
        {
            var uses = _document.ElementsOfType(type).Count();
            return uses == 1 ? "1 wall" : $"{uses} walls";
        }

        var items = _document.ElementTypes
            .Where(type => type is WallType or StackedWallType)
            .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(type => type switch
            {
                StackedWallType stacked => new TypeItem(type, type.Name, $"Stacked, {stacked.Tiers.Count} tiers  ·  {Walls(type)}"),
                WallType wall => new TypeItem(type, type.Name, $"{Units.FormatLength(wall.Width)}  ·  {Walls(type)}"),
                _ => new TypeItem(type, type.Name, string.Empty)
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
        if (ReferenceEquals(chosen, Current)) return;

        if (!SettlePendingChanges())
        {
            // Stay on the type with the unfinished edit.
            _loading = true;
            TypeList.SelectedItem = TypeList.Items.OfType<TypeItem>().FirstOrDefault(i => ReferenceEquals(i.Type, Current));
            _loading = false;
            return;
        }

        ShowTypes(chosen);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (Current is not { } source || !SettlePendingChanges()) return;

        var name = TypeNames.Unique(_document, source.Name);
        ElementType copy = source switch
        {
            StackedWallType stacked => stacked.Duplicate(name),
            WallType wall => wall.Duplicate(name),
            _ => throw new InvalidOperationException("Only wall types are listed here.")
        };

        _history.Execute(new AddTypeCommand(_document, copy));
        Changed?.Invoke(this, EventArgs.Empty);

        ShowTypes(copy);

        // A duplicate is made to be changed, and the first change is nearly always its name.
        var nameBox = copy is StackedWallType ? StackNameBox : NameBox;
        nameBox.Focus();
        nameBox.SelectAll();
    }

    /// <summary>
    /// A new stacked type to start from: the first two wall types, one above the other, with
    /// the lower one a metre high and the upper one taking the rest.
    /// </summary>
    private void OnNewStacked(object sender, RoutedEventArgs e)
    {
        if (!SettlePendingChanges()) return;

        var wallTypes = _document.TypesOf<WallType>().OrderBy(t => t.Name).ToList();
        if (wallTypes.Count == 0) return;

        var stacked = new StackedWallType(TypeNames.Unique(_document, "Stacked Wall"));
        stacked.Tiers.Add(new StackTier(wallTypes[0].Id, 1000));
        stacked.Tiers.Add(new StackTier(wallTypes[Math.Min(1, wallTypes.Count - 1)].Id, 0));

        _history.Execute(new AddTypeCommand(_document, stacked));
        Changed?.Invoke(this, EventArgs.Empty);

        ShowTypes(stacked);
        StackNameBox.Focus();
        StackNameBox.SelectAll();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Current is not { } type) return;

        var uses = _document.ElementsOfType(type).Count();
        if (uses > 0)
        {
            MessageBox.Show(this,
                $"{uses} {(uses == 1 ? "wall uses" : "walls use")} {type.Name}.\n\nGive {(uses == 1 ? "it" : "them")} another type first - select them and pick one in Properties.",
                "Delete Wall Type", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (DeleteTypeCommand.UsedInStacks(_document, type))
        {
            MessageBox.Show(this,
                $"{type.Name} is a tier of a stacked wall type. Take it out of the stack first.",
                "Delete Wall Type", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (type is WallType && _document.TypesOf<WallType>().Count() <= 1)
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

    private void Load(ElementType? type)
    {
        _editing = type as WallType;
        _stacked = type as StackedWallType;
        _loading = true;

        foreach (var row in _rows) row.Edited -= OnRowEdited;
        _rows.Clear();
        foreach (var tier in _tiers) tier.Edited -= OnTierEdited;
        _tiers.Clear();
        foreach (var sweep in _sweeps) sweep.Edited -= OnRowEdited;
        _sweeps.Clear();

        LayeredPanel.Visibility = _stacked is null ? Visibility.Visible : Visibility.Collapsed;
        StackedPanel.Visibility = _stacked is null ? Visibility.Collapsed : Visibility.Visible;

        if (_editing is { } wall)
        {
            NameBox.Text = wall.Name;
            FunctionPicker.SelectedItem = EnumText.Humanise(wall.Function);
            InsertWrapPicker.SelectedItem = EnumText.Humanise(wall.WrapAtInserts);
            EndWrapPicker.SelectedItem = EnumText.Humanise(wall.WrapAtEnds);

            foreach (var layer in wall.Structure.Layers) AddRow(new LayerDraftRow(_document, layer));
            foreach (var sweep in wall.Sweeps) AddSweepRow(new SweepDraftRow(_document, sweep));
        }
        else if (_stacked is { } stacked)
        {
            StackNameBox.Text = stacked.Name;

            // Shown top first: a wall is read from the top down, the way it is drawn in section.
            foreach (var tier in Enumerable.Reverse(stacked.Tiers)) AddTier(new TierDraftRow(_document, tier));
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

    private void AddTier(TierDraftRow tier, int index = -1)
    {
        tier.Edited += OnTierEdited;
        if (index < 0 || index > _tiers.Count) _tiers.Add(tier);
        else _tiers.Insert(index, tier);
    }

    private void OnRowEdited(object? sender, EventArgs e) => MarkChanged();

    /// <summary>Only one tier can be variable: choosing one lets go of the last.</summary>
    private void OnTierEdited(object? sender, EventArgs e)
    {
        if (sender is TierDraftRow { IsVariable: true } chosen)
        {
            foreach (var other in _tiers.Where(t => !ReferenceEquals(t, chosen) && t.IsVariable).ToList())
                other.IsVariable = false;
        }

        MarkChanged();
    }

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

        var sweeps = _sweeps.Select(row => row.ToSweep()).ToList();
        if (sweeps.Any(sweep => sweep is null)) return null;

        return new WallTypeDesign(
            NameBox.Text,
            EnumText.TryParse<WallFunction>(FunctionPicker.SelectedItem as string, out var function) ? function : WallFunction.Interior,
            EnumText.TryParse<WallWrapping>(InsertWrapPicker.SelectedItem as string, out var inserts) ? inserts : WallWrapping.None,
            EnumText.TryParse<WallWrapping>(EndWrapPicker.SelectedItem as string, out var ends) ? ends : WallWrapping.None,
            layers!,
            sweeps.OfType<WallSweep>().ToList());
    }

    /// <summary>The tiers as they stand in the editor, bottom first, or null while a height is not a length.</summary>
    private List<StackTier>? Tiers()
    {
        var tiers = _tiers.Reverse().Select(row => row.ToTier()).ToList();
        return tiers.Any(tier => tier is null) ? null : tiers.OfType<StackTier>().ToList();
    }

    private string? Problem()
    {
        if (_stacked is { } stacked)
        {
            var name = StackNameBox.Text.Trim();
            if (name.Length == 0) return "The type needs a name.";
            if (_document.ElementTypes.Any(type => !ReferenceEquals(type, stacked) &&
                                                   string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)))
                return $"Another type is already called {name}.";

            return Tiers() is { } tiers
                ? StackedWallType.Problem(_document, tiers)
                : "Every fixed tier needs a height greater than zero, e.g. 900 or 1.2 m.";
        }

        if (_editing is null) return null;

        var design = Design();
        return design is null
            ? "Every layer except a membrane needs a thickness, and every sweep and reveal a depth and height, greater than zero - e.g. 100 or 12.5 mm."
            : design.Problem(_document, _editing);
    }

    /// <summary>Brings the markers, the previews, the width and the buttons up to date.</summary>
    private void UpdateState()
    {
        if (_stacked is not null)
        {
            if (Tiers() is { } tiers) StackPreview.Show(_document, tiers);

            var tierIndex = SelectedTier is { } tier ? _tiers.IndexOf(tier) : -1;
            RemoveTierButton.IsEnabled = tierIndex >= 0 && _tiers.Count > 1;
            TierUpButton.IsEnabled = tierIndex > 0;
            TierDownButton.IsEnabled = tierIndex >= 0 && tierIndex < _tiers.Count - 1;
        }
        else
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

            var index = SelectedRow is { } selected ? _rows.IndexOf(selected) : -1;
            RemoveLayerButton.IsEnabled = index >= 0 && _rows.Count > 1;
            UpButton.IsEnabled = index > 0;
            DownButton.IsEnabled = index >= 0 && index < _rows.Count - 1;
            RemoveSweepButton.IsEnabled = SelectedSweep is not null;
        }

        var problem = Problem();
        ProblemText.Text = problem ?? (_dirty && Current is { } current
            ? $"Not applied yet. Every wall of this type will change: {_document.ElementsOfType(current).Count()} in the project."
            : string.Empty);
        ProblemText.Foreground = problem is null
            ? (Brush)FindResource("Text.Muted")
            : new SolidColorBrush(Color.FromRgb(0xE8, 0x80, 0x6A));

        ApplyButton.IsEnabled = _dirty && problem is null;
        RevertButton.IsEnabled = _dirty;
        DeleteButton.IsEnabled = Current is not null;
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

    // ---- sweeps and reveals ---------------------------------------------------------

    private SweepDraftRow? SelectedSweep => SweepGrid.SelectedItem as SweepDraftRow;

    private void AddSweepRow(SweepDraftRow row)
    {
        row.Edited += OnRowEdited;
        _sweeps.Add(row);
    }

    private void OnSweepSelected(object sender, SelectionChangedEventArgs e) => UpdateState();

    /// <summary>A skirting to start from: 20 by 100, on the interior face, at the foot of the wall.</summary>
    private void OnAddSweep(object sender, RoutedEventArgs e) =>
        AddNewSweep(new WallSweep(SweepKind.Sweep, SweepProfile.Skirting, WallSide.Interior, 20, 100, 0, false, DefaultMaterial()));

    /// <summary>A groove to start from: 20 deep and 20 high, on the exterior face, a metre up.</summary>
    private void OnAddReveal(object sender, RoutedEventArgs e) =>
        AddNewSweep(new WallSweep(SweepKind.Reveal, SweepProfile.Rectangle, WallSide.Exterior, 20, 20, 1000, false, DefaultMaterial()));

    private void AddNewSweep(WallSweep sweep)
    {
        if (_editing is null) return;

        var row = new SweepDraftRow(_document, sweep);
        AddSweepRow(row);
        SweepGrid.SelectedItem = row;
        MarkChanged();
    }

    private Guid DefaultMaterial() => _document.Materials.OrderBy(m => m.Name).First().Id;

    private void OnRemoveSweep(object sender, RoutedEventArgs e)
    {
        if (SelectedSweep is not { } row) return;

        row.Edited -= OnRowEdited;
        _sweeps.Remove(row);
        MarkChanged();
    }

    // ---- tiers ----------------------------------------------------------------------

    private void OnTierSelected(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void OnAddTier(object sender, RoutedEventArgs e)
    {
        if (_stacked is null) return;

        // A copy of the selected tier, fixed, below it; or a metre of the first wall type.
        var template = SelectedTier?.ToTier() is { } selected
            ? selected with { Height = selected.IsVariable ? 1000 : selected.Height }
            : new StackTier(_document.TypesOf<WallType>().OrderBy(t => t.Name).First().Id, 1000);

        var index = SelectedTier is { } current ? _tiers.IndexOf(current) + 1 : _tiers.Count;
        var tier = new TierDraftRow(_document, template);
        AddTier(tier, index);

        TierGrid.SelectedItem = tier;
        MarkChanged();
    }

    private void OnRemoveTier(object sender, RoutedEventArgs e)
    {
        if (SelectedTier is not { } tier || _tiers.Count <= 1) return;

        var index = _tiers.IndexOf(tier);
        tier.Edited -= OnTierEdited;
        _tiers.Remove(tier);

        TierGrid.SelectedItem = _tiers[Math.Min(index, _tiers.Count - 1)];
        MarkChanged();
    }

    private void OnTierUp(object sender, RoutedEventArgs e) => MoveTier(-1);

    private void OnTierDown(object sender, RoutedEventArgs e) => MoveTier(+1);

    private void MoveTier(int step)
    {
        if (SelectedTier is not { } tier) return;

        var index = _tiers.IndexOf(tier);
        var target = index + step;
        if (target < 0 || target >= _tiers.Count) return;

        _tiers.Move(index, target);
        TierGrid.SelectedItem = tier;
        MarkChanged();
    }

    // ---- applying -------------------------------------------------------------------

    private bool Apply()
    {
        if (Current is null || !_dirty) return true;

        if (Problem() is { } problem)
        {
            MessageBox.Show(this, problem, "Wall Type", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (_stacked is { } stacked)
            _history.Execute(new EditStackedWallTypeCommand(stacked, StackNameBox.Text, Tiers()!));
        else
            _history.Execute(new EditWallTypeCommand(_editing!, Design()!));

        _dirty = false;
        Changed?.Invoke(this, EventArgs.Empty);

        ShowTypes(Current);
        return true;
    }

    /// <summary>
    /// Before leaving a type with unapplied edits: apply them, throw them away, or stay.
    /// Returns false to stay.
    /// </summary>
    private bool SettlePendingChanges()
    {
        if (!_dirty || Current is not { } current) return true;

        var answer = MessageBox.Show(this,
            $"Apply your changes to {current.Name}?",
            "Wall Type", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => Apply(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnRevert(object sender, RoutedEventArgs e) => Load(Current);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!SettlePendingChanges()) e.Cancel = true;
    }
}
