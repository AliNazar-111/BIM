using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI.Controls;
using BIMDesigner.UI.Export;
using BIMDesigner.UI.ViewModels;
using Microsoft.Win32;

// The model has a Window too (the thing in a wall). This file only ever means the WPF one.
using Window = System.Windows.Window;

namespace BIMDesigner.UI;

public partial class MainWindow : Window
{
    private BimDocument _document = BimDocument.CreateDefault();
    private UndoStack _history = new();

    /// <summary>Null until the project has been saved somewhere.</summary>
    private string? _path;

    /// <summary>The schedule currently shown, kept so it can be exported.</summary>
    private ScheduleResult? _scheduleResult;

    /// <summary>Stops the plan and the schedule bouncing a selection back at each other.</summary>
    private bool _syncingScheduleSelection;

    /// <summary>The same guard for the plan and the section.</summary>
    private bool _syncingSectionSelection;

    /// <summary>Guards the section picker while it is being repopulated.</summary>
    private bool _loadingSections;

    /// <summary>The same guard for the sheet pickers.</summary>
    private bool _loadingSheets;

    /// <summary>Guards the option-bar handlers while they are being repopulated.</summary>
    private bool _loadingOptions;

    public MainWindow()
    {
        InitializeComponent();

        Plan.CursorMoved += OnCursorMoved;
        Plan.SelectionChanged += (_, _) =>
        {
            RefreshProperties();
            SyncScheduleToPlan();
            SyncSectionToPlan();

            if (ModelPanel.Visibility == Visibility.Visible)
                Model3D.SetSelection(Plan.SelectedElements.Select(element => element.Id));
        };

        // Drawing a section opens it: the marker and the view are the same thing, so there is
        // nothing sensible to do between creating one and looking at it.
        Plan.SectionPlaced += (_, marker) => ShowSection(marker);

        Section.SelectionChanged += (_, _) =>
        {
            if (_syncingSectionSelection) return;
            Plan.Select(Section.SelectedElement);
        };

        SheetSurface.SelectionChanged += (_, _) => RefreshViewportControls();

        // Clicking something in 3D selects it everywhere, so its properties can be edited
        // without leaving the view it was found in.
        Model3D.ElementClicked += (_, id) =>
        {
            var element = id is { } found ? _document.Elements.FirstOrDefault(e => e.Id == found) : null;

            // Something on another storey is selected by switching the plan to that storey;
            // otherwise the plan would drop it again as not being on its drawing.
            if (element is not null && element.LevelId != Plan.ActiveLevelId &&
                _document.FindLevel(element.LevelId) is { } level)
            {
                LevelPicker.SelectedItem = level;
            }

            Plan.Select(element);
            Model3D.Focus();
        };

        // A viewport drag writes to the model live and records one command on release, the
        // same bargain a wall drag makes.
        SheetSurface.ViewportMoved += (_, moved) =>
            _history.Record(new MoveViewportCommand(moved.Viewport, moved.From, moved.To));
        Plan.ViewChanged += (_, _) => RefreshStatus();
        Plan.HintChanged += (_, hint) => StatusHint.Text = hint;
        Plan.ModelChanged += (_, _) =>
        {
            RefreshProjectBrowser();
            RefreshProperties();

            // The schedule reports the model, so it follows anything added or removed. So does
            // the section: it is a cut through the same building, not a picture of it.
            RefreshSchedule();
            RefreshSectionPicker();
            RefreshSection();

            // A sheet shows views of the model, so adding or deleting anything can change
            // what is on it - and a new section becomes something that can be placed.
            RefreshSheetPicker();
            RefreshSheetViewOptions();
            SheetSurface.Refresh();
            Refresh3D();
        };

        Loaded += (_, _) =>
        {
            FitToWorkArea();
            OpenStartupProject();
            RefreshSchedule();
            Plan.Focus();
        };
    }

    /// <summary>
    /// Keeps the default size from spilling off a smaller display. The margin matters: a
    /// window sized to exactly the work area still loses its status bar to the taskbar on
    /// some DPI settings.
    /// </summary>
    private void FitToWorkArea()
    {
        const double margin = 24;
        var work = SystemParameters.WorkArea;

        Width = Math.Min(Width, work.Width - margin);
        Height = Math.Min(Height, work.Height - margin);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    // ---- document --------------------------------------------------------------

    /// <summary>Opens a project named on the command line, else starts from the template.</summary>
    private void OpenStartupProject()
    {
        var path = (Application.Current as App)?.StartupProjectPath;

        if (path is not null)
        {
            try
            {
                LoadDocument(ProjectFile.Load(path), path, seedExample: false);
                return;
            }
            catch (ProjectFileException exception)
            {
                MessageBox.Show(this, exception.Message, "Could not open project",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        LoadDocument(BimDocument.CreateDefault(), path: null, seedExample: true);
    }

    private void LoadDocument(BimDocument document, string? path, bool seedExample)
    {
        _document = document;
        _path = path;

        _history = new UndoStack();
        _history.Changed += (_, _) =>
        {
            RefreshTitle();
            CommandManager.InvalidateRequerySuggested();

            // Every edit reaches here, undo and redo included - and the section is a cut
            // through whatever the model is now, not a picture taken when it was drawn.
            // Moving a wall does not change the element collection, so nothing else would
            // tell the section that the building it is cutting has changed. The same is true
            // of every viewport on every sheet.
            RefreshSection();
            SheetSurface.Refresh();
            Refresh3D();
        };

        Plan.History = _history;
        Plan.Document = document;

        // The section and the sheets cut and lay out the same document object the plan draws;
        // neither holds a copy.
        Section.Marker = null;
        Section.Document = document;

        SheetSurface.Sheet = null;
        SheetSurface.Document = document;

        Model3D.Document = document;

        PopulateOptionBar();
        RefreshSectionPicker();
        RefreshSheetPicker();
        RefreshSheetViewOptions();
        if (seedExample) SeedExampleWalls();

        // Whatever we just loaded or seeded is the baseline, not an unsaved edit.
        _history.Clear();
        _history.MarkClean();

        RefreshProjectBrowser();
        RefreshProperties();
        RefreshTitle();

        // Fitting needs the canvas's real size. Right after a load the window may not have
        // laid out yet - especially on the first show, where it is still being resized to
        // the work area - so the fit waits for layout rather than measuring a stale size.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Plan.ZoomToFit());
    }

    private void PopulateOptionBar()
    {
        _loadingOptions = true;

        var wallTypes = WallTypeChoices();
        var doorTypes = _document.TypesOf<DoorType>().OrderBy(t => t.Name).ToList();
        var windowTypes = _document.TypesOf<WindowType>().OrderBy(t => t.Name).ToList();

        WallTypePicker.ItemsSource = wallTypes;
        WallTypePicker.SelectedItem = wallTypes.FirstOrDefault();

        DoorTypePicker.ItemsSource = doorTypes;
        DoorTypePicker.SelectedItem = doorTypes.FirstOrDefault();

        WindowTypePicker.ItemsSource = windowTypes;
        WindowTypePicker.SelectedItem = windowTypes.FirstOrDefault();

        LevelPicker.ItemsSource = _document.Levels;
        LevelPicker.SelectedItem = _document.Levels.FirstOrDefault();

        LocationLinePicker.ItemsSource = EnumText.Choices<WallLocationLine>();
        LocationLinePicker.SelectedItem = EnumText.Humanise(WallLocationLine.WallCentreline);

        VisualStylePicker.ItemsSource = new[] { "Shaded", "Flat colours", "X-ray" };
        VisualStylePicker.SelectedIndex = 0;
        Model3D.RenderStyle = VisualStyle.Shaded;

        var schedules = ScheduleDefinition.Defaults();
        SchedulePicker.ItemsSource = schedules;
        SchedulePicker.SelectedItem = schedules.FirstOrDefault();

        _loadingOptions = false;

        Plan.ActiveWallTypeId = wallTypes.FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveDoorTypeId = doorTypes.FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveWindowTypeId = windowTypes.FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveFloorTypeId = _document.TypesOf<FloorType>().FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveCeilingTypeId = _document.TypesOf<CeilingType>().FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveRoofTypeId = _document.TypesOf<RoofType>().FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveLevelId = _document.Levels.FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveLocationLine = WallLocationLine.WallCentreline;

        RefreshNewWallTopChoices();
        ShowOptionsForActiveTool();
    }

    /// <summary>
    /// The option bar carries the settings of whichever tool is active, so only one type
    /// picker is on show at a time.
    /// </summary>
    private void ShowOptionsForActiveTool()
    {
        var tool = Plan.ActiveTool;
        var isSlab = tool is PlanTool.Floor or PlanTool.Ceiling or PlanTool.Roof;

        // Grids, sections, annotation and the editing tools are not built from a type, so
        // offering one would be asking a question the tool never reads the answer to.
        var typeless = tool is PlanTool.Grid or PlanTool.Section
            or PlanTool.Dimension or PlanTool.Tag or PlanTool.Text
            or PlanTool.Offset or PlanTool.Mirror or PlanTool.Array;

        OffsetOptions.Visibility = tool == PlanTool.Offset ? Visibility.Visible : Visibility.Collapsed;
        WallDrawOptions.Visibility = tool == PlanTool.Wall ? Visibility.Visible : Visibility.Collapsed;
        MirrorOptions.Visibility = tool == PlanTool.Mirror ? Visibility.Visible : Visibility.Collapsed;
        ArrayOptions.Visibility = tool == PlanTool.Array ? Visibility.Visible : Visibility.Collapsed;

        WallTypePicker.Visibility = tool is PlanTool.Door or PlanTool.Window || isSlab || typeless
            ? Visibility.Collapsed : Visibility.Visible;
        TypeLabel.Visibility = typeless ? Visibility.Collapsed : Visibility.Visible;
        DoorTypePicker.Visibility = tool == PlanTool.Door ? Visibility.Visible : Visibility.Collapsed;
        WindowTypePicker.Visibility = tool == PlanTool.Window ? Visibility.Visible : Visibility.Collapsed;
        SlabTypePicker.Visibility = isSlab ? Visibility.Visible : Visibility.Collapsed;

        if (isSlab)
        {
            _loadingOptions = true;

            var types = tool switch
            {
                PlanTool.Floor => _document.TypesOf<SlabType>().Where(t => t is FloorType),
                PlanTool.Ceiling => _document.TypesOf<SlabType>().Where(t => t is CeilingType),
                _ => _document.TypesOf<SlabType>().Where(t => t is RoofType)
            };

            var list = types.OrderBy(t => t.Name).ToList();
            SlabTypePicker.ItemsSource = list;
            SlabTypePicker.SelectedItem = list.FirstOrDefault(t => t.Id == ActiveSlabTypeId(tool))
                                          ?? list.FirstOrDefault();

            _loadingOptions = false;
        }

        TypeLabel.Text = tool switch
        {
            PlanTool.Door => "Door type",
            PlanTool.Window => "Window type",
            PlanTool.Floor => "Floor type",
            PlanTool.Ceiling => "Ceiling type",
            PlanTool.Roof => "Roof type",
            _ => "Wall type"
        };

        // The location line only means anything while drawing walls.
        var forWalls = tool is PlanTool.Select or PlanTool.Wall or PlanTool.Split or PlanTool.Trim;
        LocationLineLabel.Visibility = forWalls ? Visibility.Visible : Visibility.Collapsed;
        LocationLinePicker.Visibility = forWalls ? Visibility.Visible : Visibility.Collapsed;
    }

    private Guid ActiveSlabTypeId(PlanTool tool) => tool switch
    {
        PlanTool.Floor => Plan.ActiveFloorTypeId,
        PlanTool.Ceiling => Plan.ActiveCeilingTypeId,
        _ => Plan.ActiveRoofTypeId
    };

    private void OnActiveSlabTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (SlabTypePicker.SelectedItem is not SlabType type) return;

        switch (Plan.ActiveTool)
        {
            case PlanTool.Floor: Plan.ActiveFloorTypeId = type.Id; break;
            case PlanTool.Ceiling: Plan.ActiveCeilingTypeId = type.Id; break;
            case PlanTool.Roof: Plan.ActiveRoofTypeId = type.Id; break;
        }
    }

    /// <summary>
    /// A 6 m x 4 m room on the ground floor: exterior cavity walls on three sides and a
    /// partition on the fourth, so the difference between wall types is visible at a glance.
    /// </summary>
    private void SeedExampleWalls()
    {
        var exterior = _document.TypesOf<WallType>().FirstOrDefault(t => t.Function == WallFunction.Exterior);
        var partition = _document.TypesOf<WallType>().FirstOrDefault(t => t.Name.Contains("Partition"));
        var level = _document.Levels.FirstOrDefault();
        if (exterior is null || partition is null || level is null) return;

        var corners = new[]
        {
            new Point2D(0, 0),
            new Point2D(6000, 0),
            new Point2D(6000, 4000),
            new Point2D(0, 4000)
        };

        for (var i = 0; i < corners.Length; i++)
        {
            var type = i == 3 ? partition : exterior;
            _document.Add(new Wall
            {
                Start = corners[i],
                End = corners[(i + 1) % corners.Length],
                TypeId = type.Id,
                LevelId = level.Id,
                Mark = $"W{i + 1}"
            });
        }
    }

    // ---- file ------------------------------------------------------------------

    private void OnNewProject(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardingChanges()) return;
        LoadDocument(BimDocument.CreateDefault(), path: null, seedExample: false);
    }

    private void OnOpenProject(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardingChanges()) return;

        var dialog = new OpenFileDialog
        {
            Title = "Open Project",
            Filter = ProjectFile.FileFilter,
            DefaultExt = ProjectFile.Extension
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            LoadDocument(ProjectFile.Load(dialog.FileName), dialog.FileName, seedExample: false);
            StatusHint.Text = $"Opened {Path.GetFileName(dialog.FileName)}";
        }
        catch (ProjectFileException exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open project",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSaveProject(object sender, ExecutedRoutedEventArgs e) => Save();

    private void OnSaveProjectAs(object sender, ExecutedRoutedEventArgs e) => SaveAs();

    /// <summary>Saves to the current path, asking for one the first time.</summary>
    private bool Save() => _path is null ? SaveAs() : WriteTo(_path);

    private bool SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Project",
            Filter = ProjectFile.FileFilter,
            DefaultExt = ProjectFile.Extension,
            FileName = _path is null
                ? _document.ProjectInformation.Name + ProjectFile.Extension
                : Path.GetFileName(_path)
        };

        return dialog.ShowDialog(this) == true && WriteTo(dialog.FileName);
    }

    private bool WriteTo(string path)
    {
        try
        {
            ProjectFile.Save(_document, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Could not save project",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _path = path;
        _history.MarkClean();
        RefreshTitle();
        StatusHint.Text = $"Saved {Path.GetFileName(path)}";
        return true;
    }

    /// <summary>
    /// Returns false when the user cancels, which aborts whatever was about to discard
    /// their work.
    /// </summary>
    private bool ConfirmDiscardingChanges()
    {
        if (!_history.IsModified) return true;

        var name = _path is null ? "this project" : Path.GetFileName(_path);
        var answer = MessageBox.Show(this,
            $"Save changes to {name}?", "BIMDesigner",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardingChanges()) e.Cancel = true;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    // ---- edit ------------------------------------------------------------------

    private void OnUndo(object sender, ExecutedRoutedEventArgs e)
    {
        _history.Undo();
        AfterHistoryChange();
    }

    private void OnRedo(object sender, ExecutedRoutedEventArgs e)
    {
        _history.Redo();
        AfterHistoryChange();
    }

    private void OnCanUndo(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _history.CanUndo;
        if (UndoItem is not null)
            UndoItem.Header = _history.CanUndo ? $"_Undo {_history.UndoName}" : "_Undo";
    }

    private void OnCanRedo(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _history.CanRedo;
        if (RedoItem is not null)
            RedoItem.Header = _history.CanRedo ? $"_Redo {_history.RedoName}" : "_Redo";
    }

    private void OnDeleteSelected(object sender, ExecutedRoutedEventArgs e) => Plan.DeleteSelected();

    private void OnCanDelete(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = Plan?.SelectedElement is not null;

    /// <summary>
    /// An undo may have removed the selected element or changed values the panel is showing,
    /// so everything reading the model is rebuilt.
    /// </summary>
    private void AfterHistoryChange()
    {
        if (Plan.SelectedElement is { } selected && !_document.Elements.Contains(selected))
            Plan.Select(null);

        Plan.RefreshModel();
        RefreshProperties();
        RefreshProjectBrowser();

        // Every view is drawn from the model, so every open one follows the undo.
        RefreshSchedule();
        RefreshSection();
        SheetSurface.Refresh();
        Refresh3D();
    }

    // ---- view ------------------------------------------------------------------

    private void OnZoomToFit(object sender, RoutedEventArgs e) => Plan.ZoomToFit();

    private void OnToolChanged(object sender, RoutedEventArgs e)
    {
        if (Plan is null) return;

        Plan.SetTool(
            WallTool.IsChecked == true ? PlanTool.Wall
            : DoorTool.IsChecked == true ? PlanTool.Door
            : WindowTool.IsChecked == true ? PlanTool.Window
            : RoomTool.IsChecked == true ? PlanTool.Room
            : FloorTool.IsChecked == true ? PlanTool.Floor
            : CeilingTool.IsChecked == true ? PlanTool.Ceiling
            : RoofTool.IsChecked == true ? PlanTool.Roof
            : GridTool.IsChecked == true ? PlanTool.Grid
            : SectionTool.IsChecked == true ? PlanTool.Section
            : DimensionTool.IsChecked == true ? PlanTool.Dimension
            : TagTool.IsChecked == true ? PlanTool.Tag
            : TextTool.IsChecked == true ? PlanTool.Text
            : SplitTool.IsChecked == true ? PlanTool.Split
            : TrimTool.IsChecked == true ? PlanTool.Trim
            : OffsetTool.IsChecked == true ? PlanTool.Offset
            : MirrorTool.IsChecked == true ? PlanTool.Mirror
            : ArrayTool.IsChecked == true ? PlanTool.Array
            : PlanTool.Select);

        ShowOptionsForActiveTool();
    }

    private void OnDetailLevelChanged(object sender, RoutedEventArgs e)
    {
        var chosen = sender as MenuItem;

        var level = ReferenceEquals(chosen, DetailCoarse) ? DetailLevel.Coarse
            : ReferenceEquals(chosen, DetailMedium) ? DetailLevel.Medium
            : DetailLevel.Fine;

        DetailCoarse.IsChecked = level == DetailLevel.Coarse;
        DetailMedium.IsChecked = level == DetailLevel.Medium;
        DetailFine.IsChecked = level == DetailLevel.Fine;

        Plan.DetailLevel = level;
        Plan.RefreshModel();
        StatusDetail.Text = $"Detail: {level}";
    }

    /// <summary>
    /// The spacebar flips walls - the one being drawn, or the ones selected.
    ///
    /// It is caught on the way down, before a focused toolbar button can take it as a press:
    /// with the wall tool just picked, its button still has focus, and Space would otherwise
    /// click it again instead of flipping the wall.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        if (ModelPanel.Visibility == Visibility.Visible || SheetPanel.Visibility == Visibility.Visible) return;

        if (Plan.Flip())
        {
            RefreshProperties();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Let text boxes in the property panel and the schedule grid keep their keystrokes.
        if (Keyboard.FocusedElement is TextBox) return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T)
        {
            OnToggleSchedules(this, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
        {
            OnToggleSheet(this, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P)
        {
            OnPrintSheets(this, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D3 or Key.NumPad3)
        {
            OnToggle3D(this, e);
            e.Handled = true;
            return;
        }

        // In 3D the drawing tools have nowhere to draw, so their single-letter shortcuts would
        // switch tools invisibly. Esc closes the view instead; the view handles its own keys.
        if (ModelPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (Plan.SelectedElements.Count > 0) Plan.Select(null);
                else OnClose3D(this, e);

                e.Handled = true;
            }

            return;
        }

        // While a sheet is open the single-letter tool shortcuts would change a tool nobody
        // can see, so only the sheet's own keys are live.
        if (SheetPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Delete && SheetSurface.SelectedViewport is not null)
            {
                OnRemoveViewport(this, e);
                e.Handled = true;
            }

            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C:
                    OnCopy(this, e);
                    e.Handled = true;
                    return;

                case Key.V:
                    OnPaste(this, e);
                    e.Handled = true;
                    return;

                case Key.A:
                    OnSelectAll(this, e);
                    e.Handled = true;
                    return;
            }
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V)
        {
            OnPasteInPlace(this, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Escape:
                // Esc first abandons whatever tool operation is half-done, and only then lets
                // go of the selection - so it takes two presses to lose a selection by accident.
                if (!Plan.CancelPendingOperation()) Plan.Select(null);
                break;
            case Key.O:
                OffsetTool.IsChecked = true;
                break;
            case Key.I:
                MirrorTool.IsChecked = true;
                break;
            case Key.Y:
                ArrayTool.IsChecked = true;
                break;
            case Key.F:
                Plan.ZoomToFit();
                break;
            case Key.W:
                WallTool.IsChecked = true;
                break;
            case Key.S:
                SelectTool.IsChecked = true;
                break;
            case Key.D:
                DoorTool.IsChecked = true;
                break;
            case Key.N:
                WindowTool.IsChecked = true;
                break;
            case Key.R:
                RoomTool.IsChecked = true;
                break;
            case Key.G:
                GridTool.IsChecked = true;
                break;
            case Key.C:
                SectionTool.IsChecked = true;
                break;
            case Key.M:
                DimensionTool.IsChecked = true;
                break;
            case Key.A:
                TagTool.IsChecked = true;
                break;
            case Key.E:
                TextTool.IsChecked = true;
                break;
            case Key.F2:
                FloorTool.IsChecked = true;
                break;
            case Key.F3:
                CeilingTool.IsChecked = true;
                break;
            case Key.F4:
                RoofTool.IsChecked = true;
                break;
            case Key.X:
                SplitTool.IsChecked = true;
                break;
            case Key.T:
                TrimTool.IsChecked = true;
                break;
        }
    }

    // ---- option bar ------------------------------------------------------------

    private void OnActiveWallTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (WallTypePicker.SelectedItem is ElementType type) Plan.ActiveWallTypeId = type.Id;
    }

    private void OnActiveDoorTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (DoorTypePicker.SelectedItem is DoorType type) Plan.ActiveDoorTypeId = type.Id;
    }

    private void OnActiveWindowTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (WindowTypePicker.SelectedItem is WindowType type) Plan.ActiveWindowTypeId = type.Id;
    }

    /// <summary>
    /// Switching level changes which storey the plan shows, not just what new elements are
    /// put on. A floor plan is a cut through one storey.
    /// </summary>
    private void OnActiveLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (LevelPicker.SelectedItem is not Core.Datums.Level level) return;

        Plan.ActiveLevelId = level.Id;
        RefreshNewWallTopChoices();
        OnNewWallHeightChanged(this, new RoutedEventArgs());
        RefreshProjectBrowser();
        RefreshProperties();
        StatusHint.Text = $"Showing {level.Name} at {Units.FormatLength(level.Elevation)}.";
    }

    private void OnActiveLocationLineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (LocationLinePicker.SelectedItem is string text &&
            EnumText.TryParse<WallLocationLine>(text, out var locationLine))
        {
            Plan.ActiveLocationLine = locationLine;
        }
    }

    /// <summary>Re-types the selected element, which swaps its whole definition at once.</summary>
    private void OnSelectedTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan?.SelectedElement is not { } element) return;
        if (SelectedTypePicker.SelectedItem is not ElementType type || type.Id == element.TypeId) return;

        // Every selected element that can take this type gets it: picking a type for four
        // selected walls should not change only the last of them.
        var targets = Plan.SelectedElements.Where(selected => selected.Category == type.Category).ToList();

        _history.Execute(new SetElementsTypeCommand(targets, type));
        AfterHistoryChange();
    }

    // ---- attaching walls ---------------------------------------------------------------

    private void OnAttachTops(object sender, RoutedEventArgs e) => AttachSelectedWalls(top: true);

    private void OnAttachBases(object sender, RoutedEventArgs e) => AttachSelectedWalls(top: false);

    /// <summary>
    /// Attaches each selected wall to the slab over it, or the floor under it. The slab is found
    /// rather than picked, because the one over a wall is usually on another storey, where the
    /// plan being worked in cannot show it.
    /// </summary>
    private void AttachSelectedWalls(bool top)
    {
        var walls = Plan.SelectedElements.OfType<Wall>().ToList();
        if (walls.Count == 0)
        {
            StatusHint.Text = "Select the walls to attach first.";
            return;
        }

        var changes = walls
            .Select(wall => (Wall: wall, Top: top, Slab: top
                ? WallAttachments.SlabAbove(_document, wall)
                : WallAttachments.FloorBelow(_document, wall)))
            .Where(change => change.Slab is not null)
            .Select(change => (change.Wall, change.Top, (Guid?)change.Slab!.Id))
            .ToList();

        if (changes.Count == 0)
        {
            StatusHint.Text = top
                ? "There is no floor, ceiling or roof over those walls to attach to."
                : "There is no floor under those walls to stand them on.";
            return;
        }

        _history.Execute(new AttachWallsCommand(changes, top ? "Attach Wall Tops" : "Attach Wall Bases"));
        AfterHistoryChange();

        StatusHint.Text = changes.Count == walls.Count
            ? $"{Plural(changes.Count, "wall")} attached."
            : $"{changes.Count} of {walls.Count} walls attached; the rest have nothing {(top ? "over" : "under")} them.";
    }

    private void OnDetachWalls(object sender, RoutedEventArgs e)
    {
        var changes = Plan.SelectedElements.OfType<Wall>()
            .SelectMany(wall => new[]
            {
                (Wall: wall, Top: true, Attached: wall.TopAttachedTo),
                (Wall: wall, Top: false, Attached: wall.BaseAttachedTo)
            })
            .Where(change => change.Attached is not null)
            .Select(change => (change.Wall, change.Top, (Guid?)null))
            .ToList();

        if (changes.Count == 0)
        {
            StatusHint.Text = "None of the selected walls is attached.";
            return;
        }

        _history.Execute(new AttachWallsCommand(changes, "Detach Walls"));
        AfterHistoryChange();
        StatusHint.Text = "Detached. The walls keep their own constraints again.";
    }

    private void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements.OfType<Wall>().ToList() is not [var wall])
        {
            StatusHint.Text = "Select one wall to edit its profile.";
            return;
        }

        if (!WallProfile.CanHave(_document, wall))
        {
            StatusHint.Text = "Only a straight, upright wall of one construction can have its profile edited.";
            return;
        }

        var dialog = new EditProfileWindow(_document, wall) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _history.Execute(new SetWallProfileCommand(wall, dialog.Result));
        AfterHistoryChange();
        StatusHint.Text = dialog.Result is null
            ? "Profile removed: the wall is its plain rectangle again."
            : "Profile applied. The plan cuts the wall where it reaches the cut height.";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // ---- new wall height ---------------------------------------------------------------

    /// <summary>Lists what new walls can reach up to: unconnected, or any level above the active one.</summary>
    private void RefreshNewWallTopChoices()
    {
        var active = _document.FindLevel(Plan.ActiveLevelId);
        var choices = new List<string> { Wall.Unconnected };
        choices.AddRange(_document.Levels
            .Where(level => active is null || level.Elevation > active.Elevation)
            .Select(level => $"Up to level: {level.Name}"));

        var current = NewWallTopPicker.SelectedItem as string;

        _loadingOptions = true;
        NewWallTopPicker.ItemsSource = choices;
        NewWallTopPicker.SelectedItem = choices.Contains(current ?? string.Empty) ? current : Wall.Unconnected;
        _loadingOptions = false;
    }

    private void OnNewWallHeightChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null || NewWallTopPicker is null) return;

        var depth = HeightModePicker.SelectedIndex == 1;
        Plan.NewWallDepth = depth;

        // A depth is measured down from the level, so there is no level to reach up to.
        NewWallTopPicker.Visibility = depth ? Visibility.Collapsed : Visibility.Visible;

        var top = NewWallTopPicker.SelectedItem as string;
        Plan.NewWallTopLevelId = depth || top is null || top == Wall.Unconnected
            ? null
            : _document.Levels.FirstOrDefault(level => $"Up to level: {level.Name}" == top)?.Id;

        // The typed height only matters when nothing else sets it.
        NewWallHeightBox.IsEnabled = depth || Plan.NewWallTopLevelId is null;

        if (ParameterFormatter.TryParse(ParameterDataType.Length, NewWallHeightBox.Text, out var value) &&
            value is double millimetres && millimetres > 0)
        {
            Plan.NewWallHeight = millimetres;
        }

        NewWallHeightBox.Text = Units.FormatLength(Plan.NewWallHeight);
    }

    // ---- wall types --------------------------------------------------------------------

    /// <summary>What the wall tool can build: every layered and stacked wall type, by name.</summary>
    private List<ElementType> WallTypeChoices() =>
        _document.ElementTypes
            .Where(type => type is WallType or StackedWallType)
            .OrderBy(type => type.Name)
            .ToList();

    private void OnManageWallTypes(object sender, RoutedEventArgs e) => ShowWallTypes(null);

    private void OnEditSelectedType(object sender, RoutedEventArgs e) =>
        ShowWallTypes(Plan.SelectedElement is { } element ? _document.FindType<ElementType>(element.TypeId) : null);

    private void ShowWallTypes(ElementType? start)
    {
        var dialog = new WallTypesWindow(_document, _history, start) { Owner = this };

        // Edits are applied to the model as they are made, so the drawing behind the dialog
        // follows each one rather than catching up when it closes.
        dialog.Changed += (_, _) => AfterTypesChanged();

        dialog.ShowDialog();
        AfterTypesChanged();
    }

    /// <summary>A type was added, removed or rebuilt: every picker listing types, and every view, follows.</summary>
    private void AfterTypesChanged()
    {
        _loadingOptions = true;

        var wallTypes = WallTypeChoices();
        WallTypePicker.ItemsSource = wallTypes;
        WallTypePicker.SelectedItem = wallTypes.FirstOrDefault(t => t.Id == Plan.ActiveWallTypeId) ?? wallTypes.FirstOrDefault();
        Plan.ActiveWallTypeId = (WallTypePicker.SelectedItem as ElementType)?.Id ?? Guid.Empty;

        _loadingOptions = false;

        AfterHistoryChange();
    }

    // ---- property panel --------------------------------------------------------

    /// <summary>
    /// Rebuilds the panel for whatever is selected. Nothing here knows about walls or doors
    /// specifically: an element reports its own parameters and the panel renders them, which
    /// is why a new category needs no new property-panel code.
    /// </summary>
    private void RefreshProperties()
    {
        var element = Plan.SelectedElement;

        if (element is null)
        {
            PropertyScroll.Visibility = Visibility.Collapsed;
            NoSelectionHint.Visibility = Visibility.Visible;
            return;
        }

        NoSelectionHint.Visibility = Visibility.Collapsed;
        PropertyScroll.Visibility = Visibility.Visible;

        var type = _document.ElementTypes.FirstOrDefault(t => t.Id == element.TypeId);

        // With several selected the panel shows the last one picked, and says so - otherwise
        // an edit here would look as if it applied to all of them.
        var others = Plan.SelectedElements.Count - 1;
        SelectedCategory.Text = others > 0
            ? $"{element.Category.ToString().ToUpperInvariant()}  Â·  {others + 1} SELECTED, SHOWING LAST"
            : element.Category.ToString().ToUpperInvariant();
        SelectedTypeName.Text = type?.Name ?? "<no type>";

        // "Doors" -> "door", so the hints read naturally for whatever is selected.
        var noun = element.Category.ToString().TrimEnd('s').ToLowerInvariant();
        InstanceHint.Text = $"This {noun} only.";
        TypeHint.Text = $"Shared by every {noun} of this type. Editing one changes them all.";

        // Only types of the same category can be swapped in: a door cannot become a wall.
        _loadingOptions = true;
        SelectedTypePicker.ItemsSource = _document.ElementTypes
            .Where(candidate => candidate.Category == element.Category)
            .OrderBy(candidate => candidate.Name)
            .ToList();
        SelectedTypePicker.SelectedItem = type;
        _loadingOptions = false;

        InstanceParameterList.ItemsSource = BuildGroupedRows(element.GetInstanceParameters(_document));
        TypeParameterList.ItemsSource = type is null
            ? null
            : BuildGroupedRows(type.GetTypeParameters());

        // Walls and slabs are both layered build-ups, so both show their assembly.
        var layers = type switch
        {
            WallType wall => wall.Structure.Layers,
            SlabType slab => slab.Structure.Layers,
            _ => null
        };

        StructureCaption.Visibility = layers is null ? Visibility.Collapsed : Visibility.Visible;
        EditTypeButton.Visibility = type is WallType or StackedWallType ? Visibility.Visible : Visibility.Collapsed;
        StructureHint.Visibility = layers is null ? Visibility.Collapsed : Visibility.Visible;
        StructureHint.Text = type is SlabType
            ? "Upper surface down."
            : "Exterior face to interior face.";

        LayerList.ItemsSource = layers?
            .Select(layer => new LayerRow(layer, _document))
            .ToList();
    }

    /// <summary>
    /// Wraps parameters as editable rows grouped by their parameter group, which is what
    /// produces the "Constraints / Dimensions / Identity Data" sections in the panel.
    /// </summary>
    private ICollectionView BuildGroupedRows(IEnumerable<ParameterValue> parameters)
    {
        var rows = parameters.Select(parameter =>
        {
            var row = new ParameterRow(parameter);
            row.ValueCommitted += OnParameterCommitted;
            return row;
        }).ToList();

        var view = new CollectionViewSource { Source = rows }.View;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ParameterRow.GroupName)));
        return view;
    }

    private void OnParameterCommitted(object? sender, ParameterCommittedEventArgs e)
    {
        _history.Record(new ParameterChangeCommand(e.Parameter, e.OldValue, e.NewValue));

        Plan.RefreshModel();
        RefreshProjectBrowser();

        // Editing one parameter can change derived ones, so rebuild the whole panel - but
        // after this edit has finished, or the row being edited is destroyed mid-binding.
        Dispatcher.BeginInvoke(RefreshProperties);
    }

    // ---- project browser -------------------------------------------------------

    private void RefreshProjectBrowser()
    {
        var project = new TreeViewItem
        {
            Header = _document.ProjectInformation.Name,
            IsExpanded = true
        };

        // Floor plans, one per level, with what is placed on each. Clicking one switches the
        // plan to that storey, which is how a project browser is expected to behave.
        var plans = new TreeViewItem { Header = "Floor Plans", IsExpanded = true };
        foreach (var level in _document.Levels.OrderByDescending(l => l.Elevation))
        {
            var counts = _document.Elements
                .Where(element => element.LevelId == level.Id)
                .GroupBy(element => element.Category)
                .OrderBy(group => group.Key.ToString())
                .Select(group => $"{group.Count()} {group.Key.ToString().ToLowerInvariant()}")
                .ToList();

            var isActive = level.Id == Plan.ActiveLevelId;

            var item = new TreeViewItem
            {
                Header = counts.Count == 0
                    ? $"{level.Name}  â€”  empty"
                    : $"{level.Name}  â€”  {string.Join(", ", counts)}",
                Tag = level,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                IsSelected = isActive
            };

            item.MouseLeftButtonUp += (_, args) =>
            {
                if (item.Tag is not Core.Datums.Level picked) return;
                LevelPicker.SelectedItem = picked;
                args.Handled = true;
            };

            plans.Items.Add(item);
        }

        // Sections sit beside the floor plans, because they are views of the project in
        // exactly the same sense: a cut through the model, made when it is looked at.
        var sections = new TreeViewItem { Header = "Sections", IsExpanded = true };

        foreach (var marker in _document.Elements.OfType<SectionMarker>().OrderBy(s => s.Name))
        {
            var item = new TreeViewItem
            {
                Header = $"Section {marker.Name}  â€”  {Units.FormatLength(marker.Length)}",
                Tag = marker
            };

            item.MouseLeftButtonUp += (_, args) =>
            {
                if (item.Tag is not SectionMarker picked) return;
                ShowSection(picked);
                Plan.Select(picked);
                args.Handled = true;
            };

            sections.Items.Add(item);
        }

        // The drawing set. Clicking a sheet opens it, the way clicking a plan switches storey.
        var sheets = new TreeViewItem { Header = "Sheets", IsExpanded = true };

        foreach (var sheet in _document.Elements.OfType<Sheet>()
                     .OrderBy(sheet => sheet.Number, StringComparer.OrdinalIgnoreCase))
        {
            var views = sheet.Viewports.Count == 1 ? "1 view" : $"{sheet.Viewports.Count} views";

            var item = new TreeViewItem
            {
                Header = $"{sheet.Number}  â€”  {sheet.Name}  ({views})",
                Tag = sheet
            };

            item.MouseLeftButtonUp += (_, args) =>
            {
                if (item.Tag is not Sheet picked) return;
                ShowSheet(picked);
                args.Handled = true;
            };

            sheets.Items.Add(item);
        }

        // Families and types, the library side of the browser.
        var families = new TreeViewItem { Header = "Families", IsExpanded = true };

        foreach (var group in _document.ElementTypes
                     .GroupBy(type => type.Category)
                     .OrderBy(group => group.Key.ToString()))
        {
            var family = new TreeViewItem { Header = group.Key.ToString(), IsExpanded = true };

            foreach (var type in group.OrderBy(type => type.Name))
            {
                var instances = _document.Elements.Count(element => element.TypeId == type.Id);
                family.Items.Add(new TreeViewItem { Header = $"{type.Name}  ({instances})" });
            }

            families.Items.Add(family);
        }

        var materials = new TreeViewItem { Header = "Materials" };
        foreach (var material in _document.Materials.OrderBy(m => m.Name))
            materials.Items.Add(new TreeViewItem { Header = material.Name });

        project.Items.Add(plans);

        var view3D = new TreeViewItem { Header = "3D View" };
        view3D.MouseLeftButtonUp += (_, args) =>
        {
            Show3D();
            args.Handled = true;
        };
        project.Items.Add(view3D);

        if (sections.Items.Count > 0) project.Items.Add(sections);
        if (sheets.Items.Count > 0) project.Items.Add(sheets);
        project.Items.Add(families);
        project.Items.Add(materials);

        ProjectBrowser.Items.Clear();
        ProjectBrowser.Items.Add(project);
    }

    // ---- sheets ----------------------------------------------------------------

    /// <summary>One entry in the "place a view on this sheet" list.</summary>
    private sealed record SheetViewOption(string Display, ViewReference View)
    {
        public override string ToString() => Display;
    }

    private void OnToggleSheet(object sender, RoutedEventArgs e)
    {
        var show = SheetPanel.Visibility != Visibility.Visible;

        if (!show)
        {
            OnCloseSheet(sender, e);
            return;
        }

        RefreshSheetPicker();

        var sheets = _document.Elements.OfType<Sheet>().ToList();

        // Opening the sheet view with no sheets at all should produce one rather than an
        // empty frame the user has to work out how to fill. Otherwise it comes back to
        // whichever sheet was last open.
        if (sheets.Count == 0) OnNewSheet(sender, e);
        else ShowSheet(SheetSurface.Sheet is { } last && sheets.Contains(last) ? last : sheets[0]);
    }

    private void OnCloseSheet(object sender, RoutedEventArgs e)
    {
        SheetPanel.Visibility = Visibility.Collapsed;
        SheetMenuItem.IsChecked = false;
    }

    private void OnNewSheet(object sender, RoutedEventArgs e)
    {
        var sheet = new Sheet
        {
            Number = Sheets.NextNumber(_document),
            Name = "General Arrangement",
            DrawnBy = Environment.UserName
        };

        _history.Execute(new AddElementCommand(_document, sheet, $"Add Sheet {sheet.Number}"));

        RefreshProjectBrowser();
        ShowSheet(sheet);
        StatusHint.Text = $"Sheet {sheet.Number} added. Pick a view and press Add to Sheet.";
    }

    private void OnDeleteSheet(object sender, RoutedEventArgs e)
    {
        if (SheetSurface.Sheet is not { } sheet) return;

        var answer = MessageBox.Show(this,
            $"Delete sheet {sheet.Number} - {sheet.Name}?\n\n" +
            "The views on it are not deleted; only the sheet and its layout go.",
            "Delete Sheet", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        _history.Execute(new DeleteElementCommand(_document, sheet));

        SheetSurface.Sheet = null;
        RefreshSheetPicker();
        RefreshProjectBrowser();

        if (SheetPicker.Items.Count > 0) SheetPicker.SelectedIndex = 0;
        else OnCloseSheet(sender, e);
    }

    /// <summary>Opens a sheet, making the sheet view visible if it was not already.</summary>
    private void ShowSheet(Sheet sheet)
    {
        if (ModelPanel.Visibility == Visibility.Visible) OnClose3D(this, new RoutedEventArgs());

        SheetPanel.Visibility = Visibility.Visible;
        SheetMenuItem.IsChecked = true;

        RefreshSheetPicker();

        _loadingSheets = true;
        SheetPicker.SelectedItem = sheet;
        _loadingSheets = false;

        SheetSurface.Document = _document;
        SheetSurface.Sheet = sheet;

        // The sheet's own parameters - paper size, title block fields - edit in the property
        // panel like any other element's, because a sheet is one.
        Plan.Select(sheet);

        RefreshSheetViewOptions();
        RefreshViewportControls();

        // Fitting needs the panel's real size, and it has only just been made visible.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SheetSurface.ZoomToFit());
    }

    private void RefreshSheetPicker()
    {
        var sheets = _document.Elements.OfType<Sheet>()
            .OrderBy(sheet => sheet.Number, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = SheetPicker.SelectedItem as Sheet;

        _loadingSheets = true;
        SheetPicker.ItemsSource = sheets;
        SheetPicker.SelectedItem = current is not null && sheets.Contains(current) ? current : null;
        _loadingSheets = false;

        if (SheetSurface.Sheet is { } shown && !sheets.Contains(shown)) SheetSurface.Sheet = null;
    }

    /// <summary>
    /// The views available to place: every floor plan and every section in the project. It is
    /// rebuilt from the model rather than kept, so a section drawn a moment ago is offered.
    /// </summary>
    private void RefreshSheetViewOptions()
    {
        var options = new List<SheetViewOption>();

        foreach (var level in _document.Levels.OrderByDescending(level => level.Elevation))
            options.Add(new SheetViewOption($"{level.Name} Plan", ViewReference.FloorPlan(level.Id)));

        foreach (var marker in _document.Elements.OfType<SectionMarker>().OrderBy(marker => marker.Name))
            options.Add(new SheetViewOption($"Section {marker.Name}", ViewReference.Section(marker.Id)));

        var current = (SheetViewPicker.SelectedItem as SheetViewOption)?.Display;

        _loadingSheets = true;
        SheetViewPicker.ItemsSource = options;
        SheetViewPicker.SelectedItem = options.FirstOrDefault(option => option.Display == current)
                                       ?? options.FirstOrDefault();
        _loadingSheets = false;
    }

    private void OnActiveSheetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSheets) return;
        if (SheetPicker.SelectedItem is Sheet sheet) ShowSheet(sheet);
    }

    private void OnPlaceViewOnSheet(object sender, RoutedEventArgs e)
    {
        if (SheetSurface.Sheet is not { } sheet)
        {
            StatusHint.Text = "Open or create a sheet first.";
            return;
        }

        if (SheetViewPicker.SelectedItem is not SheetViewOption option)
        {
            StatusHint.Text = "There is no view to place. Draw a plan or cut a section first.";
            return;
        }

        // The scale is chosen to fit the drawing area both ways, then the drawing is laid out
        // beside whatever is already on the sheet rather than on top of it.
        var extent = ViewExtent.Of(_document, option.View).OrAtLeast(1000);

        var availableWidth = sheet.Width - TitleBlock.Width(sheet) - TitleBlock.Margin * 3;

        // Room is left under the drawing for its title and scale.
        var availableHeight = sheet.Height - TitleBlock.Margin * 2 - 16;

        var scale = ViewScale.FittingInto(extent.Width, extent.Height, availableWidth, availableHeight);

        var viewport = new Viewport { View = option.View, Scale = scale };
        var size = viewport.PaperBounds(_document);
        viewport.Centre = sheet.NextFreePosition(_document, size.Width, size.Height);

        var command = new AddViewportCommand(sheet, viewport, $"Place {option.Display}");
        _history.Execute(command);

        SheetSurface.Select(viewport);
        SheetSurface.Refresh();
        RefreshViewportControls();

        StatusHint.Text = $"{option.Display} placed at {scale}. Drag it to move it.";
    }

    private void OnRemoveViewport(object sender, RoutedEventArgs e)
    {
        if (SheetSurface.Sheet is not { } sheet || SheetSurface.SelectedViewport is not { } viewport) return;

        _history.Execute(new RemoveViewportCommand(sheet, viewport));

        SheetSurface.Select(null);
        SheetSurface.Refresh();
        RefreshViewportControls();
    }

    private void OnViewportScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSheets) return;
        if (SheetSurface.SelectedViewport is not { } viewport) return;
        if (ViewportScalePicker.SelectedItem is not double denominator) return;
        if (Math.Abs(viewport.Scale.Denominator - denominator) < 1e-9) return;

        if (SheetSurface.Sheet is not { } sheet) return;

        var before = viewport.Centre;
        _history.Execute(SetViewportScaleCommand.On(_document, sheet, viewport, new ViewScale(denominator)));

        SheetSurface.Refresh();

        StatusHint.Text = viewport.Centre != before
            ? $"Set to {viewport.Scale}. It no longer fitted where it was, so it was moved."
            : !sheet.IsWellPlaced(_document, viewport)
                ? $"At {viewport.Scale} this view is too big for the free space. Try a smaller scale."
                : $"Set to {viewport.Scale}.";
    }

    private void OnZoomSheetToFit(object sender, RoutedEventArgs e) => SheetSurface.ZoomToFit();

    /// <summary>Enables the viewport controls, and points them at whatever is selected.</summary>
    private void RefreshViewportControls()
    {
        var viewport = SheetSurface.SelectedViewport;

        RemoveViewportButton.IsEnabled = viewport is not null;
        ViewportScalePicker.IsEnabled = viewport is not null;

        _loadingSheets = true;
        ViewportScalePicker.ItemsSource = ViewScale.Common;
        ViewportScalePicker.SelectedItem = viewport is null
            ? null
            : ViewScale.Common.Cast<double?>()
                  .FirstOrDefault(value => Math.Abs(value!.Value - viewport.Scale.Denominator) < 1e-9);
        _loadingSheets = false;
    }

    // ---- 3D view ---------------------------------------------------------------

    private void OnToggle3D(object sender, RoutedEventArgs e)
    {
        if (ModelPanel.Visibility == Visibility.Visible) OnClose3D(sender, e);
        else Show3D();
    }

    private void Show3D()
    {
        // The sheet and the 3D view both take over the drawing area; only one can.
        if (SheetPanel.Visibility == Visibility.Visible) OnCloseSheet(this, new RoutedEventArgs());

        ModelPanel.Visibility = Visibility.Visible;
        ModelMenuItem.IsChecked = true;

        RefreshStoreyToggles();
        Model3D.Rebuild();
        Model3D.SetSelection(Plan.SelectedElements.Select(element => element.Id));

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Model3D.ZoomToFit();
            Model3D.Focus();
        });

        StatusHint.Text = "3D view. Drag to orbit, right-drag to pan, scroll to zoom, click to select.";
    }

    private void OnClose3D(object sender, RoutedEventArgs e)
    {
        ModelPanel.Visibility = Visibility.Collapsed;
        ModelMenuItem.IsChecked = false;
        Plan.Focus();
    }

    /// <summary>Rebuilds the 3D meshes, but only while anyone can see them.</summary>
    private void Refresh3D()
    {
        if (ModelPanel.Visibility != Visibility.Visible) return;
        Model3D.Rebuild();
    }

    /// <summary>
    /// One tick box per storey, highest first, so a floor can be lifted off to see into the
    /// one below - which, in a model that is otherwise a closed box, is how anyone looks inside.
    /// </summary>
    private void RefreshStoreyToggles()
    {
        StoreyToggles.Items.Clear();

        foreach (var level in _document.Levels.OrderByDescending(level => level.Elevation))
        {
            var toggle = new CheckBox
            {
                Content = level.Name,
                IsChecked = Model3D.IsLevelVisible(level.Id),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = level.Id
            };

            toggle.Click += (_, _) =>
            {
                if (toggle.Tag is Guid id) Model3D.SetLevelVisible(id, toggle.IsChecked == true);
            };

            StoreyToggles.Items.Add(toggle);
        }
    }

    private void OnVisualStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions) return;

        Model3D.RenderStyle = VisualStylePicker.SelectedIndex switch
        {
            1 => VisualStyle.FlatColours,
            2 => VisualStyle.XRay,
            _ => VisualStyle.Shaded
        };
    }

    private void OnModelCategoryToggled(object sender, RoutedEventArgs e)
    {
        Model3D.SetKindVisible(MeshKind.Roof, ShowRoofsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Ceiling, ShowCeilingsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Floor, ShowFloorsBox.IsChecked == true);
        Model3D.ShowGround = ShowGroundBox.IsChecked == true;
        Model3D.ShowEdges = ShowEdgesBox.IsChecked == true;
    }

    private void OnReset3DView(object sender, RoutedEventArgs e) => Model3D.ResetView();

    private void OnZoom3DToFit(object sender, RoutedEventArgs e) => Model3D.ZoomToFit();

    // ---- editing ---------------------------------------------------------------

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements.Count == 0)
        {
            StatusHint.Text = "Nothing selected to copy.";
            return;
        }

        Plan.CopySelection();
    }

    private void OnPaste(object sender, RoutedEventArgs e) => PasteFromClipboard(inPlace: false);

    private void OnPasteInPlace(object sender, RoutedEventArgs e) => PasteFromClipboard(inPlace: true);

    private void PasteFromClipboard(bool inPlace)
    {
        if (!Plan.CanPaste)
        {
            StatusHint.Text = "Nothing has been copied yet. Select something and press Ctrl+C.";
            return;
        }

        // Pasting works on the plan; a sheet being open would hide where things landed.
        if (SheetPanel.Visibility == Visibility.Visible) OnCloseSheet(this, new RoutedEventArgs());

        Plan.Paste(inPlace);
        Plan.Focus();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        SelectTool.IsChecked = true;
        Plan.SelectAllOnLevel();
    }

    private void OnOffsetDistanceChanged(object sender, RoutedEventArgs e)
    {
        if (ParameterFormatter.TryParse(ParameterDataType.Length, OffsetDistanceBox.Text, out var value) &&
            value is double millimetres && millimetres > 0)
        {
            Plan.OffsetDistance = millimetres;
        }

        // Always rewrite the box in the app's own format, so what it says is what it will do.
        OffsetDistanceBox.Text = Units.FormatLength(Plan.OffsetDistance);
    }

    /// <summary>The wall tool's offset. Zero is allowed, and so is negative - the other side.</summary>
    private void OnWallOffsetChanged(object sender, RoutedEventArgs e)
    {
        if (ParameterFormatter.TryParse(ParameterDataType.Length, WallOffsetBox.Text, out var value) &&
            value is double millimetres)
        {
            Plan.DrawOffset = millimetres;
        }

        WallOffsetBox.Text = Units.FormatLength(Plan.DrawOffset);
    }

    private void OnWallShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Plan is null) return;

        var shape = (WallShape)Math.Max(0, WallShapePicker.SelectedIndex);
        Plan.DrawShape = shape;
        PolygonOptions.Visibility = shape == WallShape.Polygon ? Visibility.Visible : Visibility.Collapsed;

        StatusHint.Text = shape switch
        {
            WallShape.Arc => "Arc: click the start, the end, then a point the arc passes through.",
            WallShape.Rectangle => "Rectangle: click one corner, then the opposite one. Hold Shift for a square.",
            WallShape.Polygon => "Polygon: click the centre, then a corner. Set the number of sides on the bar.",
            WallShape.Circle => "Circle: click the centre, then a point on the circle.",
            WallShape.Oval => "Oval: click one corner of its box, then the opposite one. Hold Shift for a circle.",
            WallShape.Ellipse => "Ellipse: click one corner of its box, then the opposite one. Hold Shift for a circle.",
            WallShape.PartialEllipse => "Partial ellipse: click one end of an axis, the other end, then a point the ellipse passes through.",
            WallShape.Pick => "Pick lines: click a gridline to put a wall along it. The offset moves it toward the side you click.",
            _ => "Click the start of the wall, then its end."
        };
    }

    private void OnPolygonSidesChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(PolygonSidesBox.Text, out var sides))
            Plan.PolygonSides = Math.Clamp(sides, WallShapes.MinSides, WallShapes.MaxSides);

        PolygonSidesBox.Text = Plan.PolygonSides.ToString();
    }

    private void OnPolygonInscribedChanged(object sender, RoutedEventArgs e) =>
        Plan.PolygonInscribed = PolygonInscribedBox.IsChecked == true;

    /// <summary>Enter commits the offset and hands the keyboard back to the drawing.</summary>
    private void OnWallOffsetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnWallOffsetChanged(sender, e);
        Plan.Focus();
        e.Handled = true;
    }

    // ---- wall functions -----------------------------------------------------------

    /// <summary>
    /// Lists every view open right now - this level's plan, the section on screen, the 3D
    /// view - each with its own set of wall functions to tick, since each keeps its own.
    /// </summary>
    private void OnWallFunctionsMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WallFunctionsMenu)) return;

        WallFunctionsMenu.Items.Clear();

        var views = new List<ViewReference> { Plan.CurrentView };

        if (SectionPanel.Visibility == Visibility.Visible && Section.Marker is { } marker)
            views.Add(ViewReference.Section(marker.Id));

        views.Add(ViewReference.Model3D);

        foreach (var view in views)
        {
            var submenu = new MenuItem { Header = $"In {view.TitleIn(_document)}" };

            foreach (var function in Enum.GetValues<WallFunction>())
            {
                var item = new MenuItem
                {
                    Header = EnumText.Humanise(function),
                    IsCheckable = true,
                    IsChecked = _document.ViewSettings.IsWallFunctionVisible(view, function),
                    StaysOpenOnClick = true
                };

                item.Click += (_, _) =>
                {
                    _history.Execute(new SetWallFunctionVisibilityCommand(
                        _document, view, function, item.IsChecked));
                    AfterHistoryChange();
                };

                submenu.Items.Add(item);
            }

            WallFunctionsMenu.Items.Add(submenu);
        }
    }

    private void OnMirrorKeepOriginalChanged(object sender, RoutedEventArgs e) =>
        Plan.MirrorKeepsOriginal = MirrorKeepOriginalBox.IsChecked == true;

    private void OnArrayCountChanged(object sender, RoutedEventArgs e)
    {
        // A count past a few hundred is almost always a typo, and would freeze the plan.
        if (int.TryParse(ArrayCountBox.Text, out var count) && count is >= 2 and <= 500)
            Plan.ArrayCount = count;

        ArrayCountBox.Text = Plan.ArrayCount.ToString();
    }

    // ---- levels ----------------------------------------------------------------

    private void OnManageLevels(object sender, RoutedEventArgs e)
    {
        var dialog = new LevelsWindow(_document, _history) { Owner = this };

        // The dialog edits the model live, so the plan and every picker behind it follow
        // along rather than catching up when it closes.
        dialog.Changed += (_, _) => AfterLevelsChanged();

        dialog.ShowDialog();
        AfterLevelsChanged();
    }

    private void AfterLevelsChanged()
    {
        RefreshNewWallTopChoices();
        var levels = _document.Levels.ToList();

        // The storey being drawn may have been deleted out from under the plan.
        if (levels.All(level => level.Id != Plan.ActiveLevelId) && levels.Count > 0)
            Plan.ActiveLevelId = levels[0].Id;

        _loadingOptions = true;
        LevelPicker.ItemsSource = null;
        LevelPicker.ItemsSource = _document.Levels;
        LevelPicker.SelectedItem = _document.FindLevel(Plan.ActiveLevelId);
        _loadingOptions = false;

        Plan.RefreshModel();
        RefreshProjectBrowser();
        RefreshProperties();
        RefreshSchedule();
        RefreshSection();
        RefreshSheetViewOptions();
        SheetSurface.Refresh();
        RefreshStoreyToggles();
        Refresh3D();
    }

    private void OnToggleUnderlay(object sender, RoutedEventArgs e)
    {
        Plan.ShowUnderlay = UnderlayMenuItem.IsChecked;
        StatusHint.Text = Plan.ShowUnderlay
            ? "Showing the storey below as an underlay."
            : "Underlay hidden.";
    }

    // ---- publishing ------------------------------------------------------------

    /// <summary>
    /// Writes the building out as IFC4 (specification section 8).
    ///
    /// This is the handover format: the receiving application gets walls, storeys, spaces and
    /// material layers that it understands as such, rather than a drawing of them.
    /// </summary>
    private void OnExportIfc(object sender, RoutedEventArgs e)
    {
        if (!_document.Walls.Any() && !_document.Elements.OfType<Slab>().Any())
        {
            MessageBox.Show(this,
                "There is nothing to export yet. Draw some walls first.",
                "Export IFC", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        var name = _document.ProjectInformation.Name;
        if (string.IsNullOrWhiteSpace(name)) name = "Model";

        var dialog = new SaveFileDialog
        {
            Title = "Export IFC",
            Filter = "IFC model (*.ifc)|*.ifc|All files (*.*)|*.*",
            DefaultExt = ".ifc",
            FileName = $"{new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim()}.ifc"
        };

        if (dialog.ShowDialog(this) != true) return;

        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            IfcExport.Save(_document, dialog.FileName, new IfcExportOptions
            {
                ApplicationVersion = "0.1",
                AuthorGivenName = Environment.UserName,
                Organisation = _document.ProjectInformation.Client
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"Could not write the IFC file.\n\n{exception.Message}",
                "Export IFC", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        var counts =
            $"{_document.Walls.Count()} walls, " +
            $"{_document.Elements.OfType<Slab>().Count()} slabs, " +
            $"{_document.Openings.Count()} openings, " +
            $"{_document.Levels.Count} storeys";

        StatusHint.Text = $"Exported IFC4: {counts}.";

        OfferToOpen(dialog.FileName);
    }

    private void OnExportSheetPdf(object sender, RoutedEventArgs e)
    {
        if (SheetSurface.Sheet is not { } sheet)
        {
            MessageBox.Show(this,
                "Open a sheet first: Sheets â†’ New Sheet, or Ctrl+H.",
                "Export to PDF", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        ExportPdf(new[] { sheet });
    }

    private void OnExportDrawingSetPdf(object sender, RoutedEventArgs e)
    {
        var sheets = SheetsInOrder();

        if (sheets.Count == 0)
        {
            MessageBox.Show(this,
                "There are no sheets to export. Create one from the Sheets menu.",
                "Export to PDF", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        ExportPdf(sheets);
    }

    /// <summary>Every sheet, in drawing-number order. That is the order a set is issued in.</summary>
    private List<Sheet> SheetsInOrder() => _document.Elements.OfType<Sheet>()
        .OrderBy(sheet => sheet.Number, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void ExportPdf(IReadOnlyList<Sheet> sheets)
    {
        var dialog = new SaveFileDialog
        {
            Title = sheets.Count == 1 ? "Export Sheet to PDF" : "Export Drawing Set to PDF",
            Filter = "PDF document (*.pdf)|*.pdf|All files (*.*)|*.*",
            DefaultExt = ".pdf",
            FileName = SheetExport.SuggestedFileName(_document, sheets)
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            SheetExport.ToPdf(_document, sheets, dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Almost always the file being open in a reader, which locks it.
            MessageBox.Show(this,
                $"Could not write the PDF.\n\n{exception.Message}\n\n" +
                "If it is already open in a PDF reader, close it and try again.",
                "Export to PDF", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        StatusHint.Text = sheets.Count == 1
            ? $"Exported {sheets[0].Number} to {Path.GetFileName(dialog.FileName)}."
            : $"Exported {sheets.Count} sheets to {Path.GetFileName(dialog.FileName)}.";

        OfferToOpen(dialog.FileName);
    }

    /// <summary>
    /// Offers to open what was just written.
    ///
    /// An export nobody looks at is an export nobody checks, and a drawing set is checked by
    /// reading it rather than by trusting that it came out right.
    /// </summary>
    private void OfferToOpen(string path)
    {
        var answer = MessageBox.Show(this,
            $"Written to:\n{path}\n\nOpen it now?",
            "Export to PDF", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"The file was written, but could not be opened.\n\n{exception.Message}",
                "Export to PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPrintSheets(object sender, RoutedEventArgs e)
    {
        var sheets = SheetSurface.Sheet is { } open ? new List<Sheet> { open } : SheetsInOrder();

        if (sheets.Count == 0)
        {
            MessageBox.Show(this,
                "There are no sheets to print. Create one from the Sheets menu.",
                "Print", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        var dialog = new PrintDialog { UserPageRangeEnabled = sheets.Count > 1 };
        if (dialog.ShowDialog() != true) return;

        // Ask for paper the size of the sheet. The driver will scale to whatever is loaded if
        // it has to, but a drawing printed at the size it was drawn is the whole point of
        // putting it on a sheet in the first place.
        if (PaperSizeNameFor(sheets[0]) is { } media && dialog.PrintTicket is { } ticket)
        {
            ticket.PageMediaSize = new PageMediaSize(media);
            ticket.PageOrientation = sheets[0].Orientation == PaperOrientation.Landscape
                ? PageOrientation.Landscape
                : PageOrientation.Portrait;
        }

        try
        {
            dialog.PrintDocument(
                new SheetPaginator(_document, sheets),
                $"{_document.ProjectInformation.Name} - Drawings");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"Could not print.\n\n{exception.Message}",
                "Print", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        StatusHint.Text = sheets.Count == 1
            ? $"Sent {sheets[0].Number} to the printer."
            : $"Sent {sheets.Count} sheets to the printer.";
    }

    private static PageMediaSizeName? PaperSizeNameFor(Sheet sheet) => sheet.PaperSize switch
    {
        PaperSize.A0 => PageMediaSizeName.ISOA0,
        PaperSize.A1 => PageMediaSizeName.ISOA1,
        PaperSize.A2 => PageMediaSizeName.ISOA2,
        PaperSize.A3 => PageMediaSizeName.ISOA3,
        PaperSize.A4 => PageMediaSizeName.ISOA4,
        _ => null
    };

    // ---- sections --------------------------------------------------------------

    private void OnToggleSection(object sender, RoutedEventArgs e)
    {
        var show = SectionPanel.Visibility != Visibility.Visible;

        SectionPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SectionSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SectionMenuItem.IsChecked = show;

        if (!show) return;

        RefreshSectionPicker();

        // Opening the panel with no section chosen should show the first one there is,
        // rather than an empty frame the user has to work out how to fill.
        if (Section.Marker is null && SectionPicker.Items.Count > 0)
            SectionPicker.SelectedIndex = 0;

        RefreshSection();
    }

    private void OnCloseSection(object sender, RoutedEventArgs e)
    {
        SectionPanel.Visibility = Visibility.Collapsed;
        SectionSplitter.Visibility = Visibility.Collapsed;
        SectionMenuItem.IsChecked = false;
    }

    /// <summary>Opens the section panel on one marker, whether or not it was already open.</summary>
    private void ShowSection(SectionMarker marker)
    {
        SectionPanel.Visibility = Visibility.Visible;
        SectionSplitter.Visibility = Visibility.Visible;
        SectionMenuItem.IsChecked = true;

        RefreshSectionPicker();

        _loadingSections = true;
        SectionPicker.SelectedItem = marker;
        _loadingSections = false;

        Section.Marker = marker;

        // The cut needs the panel's real height before it can be fitted to it, and the panel
        // has only just been made visible.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Section.ZoomToFit());
        RefreshSectionSummary();
    }

    private void RefreshSectionPicker()
    {
        var markers = _document.Elements.OfType<SectionMarker>().OrderBy(s => s.Name).ToList();
        var current = SectionPicker.SelectedItem as SectionMarker;

        _loadingSections = true;
        SectionPicker.ItemsSource = markers;
        SectionPicker.SelectedItem = current is not null && markers.Contains(current) ? current : null;
        _loadingSections = false;

        // A marker that has been deleted takes its view with it: showing a cut along a line
        // that no longer exists would be a drawing of nothing.
        if (Section.Marker is { } shown && !markers.Contains(shown)) Section.Marker = null;
    }

    private void OnActiveSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSections) return;

        Section.Marker = SectionPicker.SelectedItem as SectionMarker;
        RefreshSectionSummary();
    }

    private void OnFlipSection(object sender, RoutedEventArgs e)
    {
        if (Section.Marker is not { } marker) return;

        // Which way a section looks is a property of the marker, so flipping it is an edit to
        // the model and belongs in the history like any other.
        _history.Execute(new ParameterChangeCommand(
            marker.GetInstanceParameters(_document).First(p => p.Definition == SectionParameters.Flipped),
            marker.Flipped,
            !marker.Flipped));

        Plan.RefreshModel();
        RefreshSection();
        RefreshProperties();
    }

    private void OnZoomSectionToFit(object sender, RoutedEventArgs e) => Section.ZoomToFit();

    private void RefreshSection()
    {
        if (SectionPanel.Visibility != Visibility.Visible) return;

        Section.Rebuild();
        RefreshSectionSummary();
    }

    private void RefreshSectionSummary()
    {
        if (Section.Marker is not { } marker || Section.Drawing is not { } drawing)
        {
            SectionSummary.Text = string.Empty;
            return;
        }

        var cut = drawing.Pieces.Count(piece => piece.Depth == SectionDepth.Cut);
        var seen = drawing.Pieces.Count - cut;

        SectionSummary.Text =
            $"{cut} cut, {seen} beyond     sees {Units.FormatLength(marker.ViewDepth)}";
    }

    /// <summary>
    /// Keeps the section's highlight on whatever the plan has selected, and switches the view
    /// when the thing selected is itself a section marker.
    /// </summary>
    private void SyncSectionToPlan()
    {
        if (Plan.SelectedElement is SectionMarker marker && !ReferenceEquals(Section.Marker, marker))
        {
            if (SectionPanel.Visibility == Visibility.Visible) ShowSection(marker);
            return;
        }

        if (SectionPanel.Visibility != Visibility.Visible) return;

        _syncingSectionSelection = true;
        try
        {
            Section.SetSelectionQuietly(Plan.SelectedElements);
        }
        finally
        {
            _syncingSectionSelection = false;
        }
    }

    // ---- schedules -------------------------------------------------------------

    private void OnToggleSchedules(object sender, RoutedEventArgs e)
    {
        var show = SchedulePanel.Visibility != Visibility.Visible;

        SchedulePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ScheduleSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ScheduleMenuItem.IsChecked = show;

        if (show) RefreshSchedule();
    }

    private void OnCloseSchedule(object sender, RoutedEventArgs e)
    {
        SchedulePanel.Visibility = Visibility.Collapsed;
        ScheduleSplitter.Visibility = Visibility.Collapsed;
        ScheduleMenuItem.IsChecked = false;
    }

    private void OnScheduleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions) return;
        RefreshSchedule();
    }

    /// <summary>
    /// Runs the chosen schedule against the model and rebuilds the grid.
    ///
    /// Columns are built at run time from what the schedule asked for, which is why adding a
    /// category needs no work here: an element reports its parameters and the table follows.
    /// </summary>
    private void RefreshSchedule()
    {
        if (SchedulePanel.Visibility != Visibility.Visible) return;
        if (SchedulePicker.SelectedItem is not ScheduleDefinition definition) return;

        var result = Core.Schedules.Schedule.Run(_document, definition);
        _scheduleResult = result;

        ScheduleGrid.Columns.Clear();

        foreach (var column in result.Columns)
        {
            ScheduleGrid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Field,
                IsReadOnly = column.IsReadOnly,
                Binding = new Binding($"[{column.Field}]")
                {
                    Mode = column.IsReadOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                }
            });
        }

        var rows = result.Rows
            .Select(row =>
            {
                var view = new ScheduleRowView(row, definition.GroupBy);
                view.CellEdited += OnScheduleCellCommitted;
                return view;
            })
            .ToList();

        var view = new CollectionViewSource { Source = rows }.View;
        if (!string.IsNullOrWhiteSpace(definition.GroupBy))
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ScheduleRowView.GroupKey)));

        ScheduleGrid.ItemsSource = view;

        ScheduleSummary.Text = result.Count == 1 ? "1 row" : $"{result.Count} rows";
        ScheduleTotals.Text = BuildTotalsLine(result);

        // Rebuilding the table drops the highlight, so put it back on whatever is selected.
        SyncScheduleToPlan();
    }

    private static string BuildTotalsLine(ScheduleResult result)
    {
        var totals = result.Columns
            .Where(column => column.IsTotalled && result.Totals.ContainsKey(column.Field))
            .Select(column =>
                $"{column.Field}: {ScheduleCsv.ParameterTotalText(column, result.Totals[column.Field])}")
            .ToList();

        return totals.Count == 0 ? "Nothing to total." : string.Join("     ", totals);
    }

    /// <summary>An edit made in the table is an edit to the model, so it joins the history.</summary>
    private void OnScheduleCellCommitted(object? sender, ScheduleCellEditedEventArgs e)
    {
        _history.Record(new ParameterChangeCommand(e.Parameter, e.OldValue, e.NewValue));

        Plan.RefreshModel();
        RefreshProjectBrowser();
        RefreshProperties();

        // Totals and any derived column move with it, so the table is rebuilt after the edit.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshSchedule);
    }

    /// <summary>
    /// Delete in the schedule deletes the element, not just the row.
    ///
    /// The row is the element. Removing one and leaving the other would make the two views
    /// disagree about what the building contains, which is the whole thing a schedule exists
    /// to prevent.
    /// </summary>
    private void OnScheduleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;

        // Mid-edit, Delete belongs to the text being typed.
        if (Keyboard.FocusedElement is TextBox) return;
        if (Plan.SelectedElement is null) return;

        Plan.DeleteSelected();
        e.Handled = true;
    }

    private void OnScheduleCellEdited(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // The binding commits on losing focus; this only keeps the plan in step immediately.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Plan.RefreshModel());
    }

    /// <summary>Selecting a row in the schedule selects that element on the plan.</summary>
    private void OnScheduleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingScheduleSelection) return;
        if (ScheduleGrid.SelectedItem is not ScheduleRowView row) return;
        if (ReferenceEquals(Plan.SelectedElement, row.Element)) return;

        Plan.Select(row.Element);
    }

    /// <summary>
    /// And the other way round: picking something on the plan highlights its row.
    ///
    /// The link has to work both ways to be worth having - a schedule is a view of the same
    /// building, so "this one" should mean the same thing whichever you point at.
    /// </summary>
    private void SyncScheduleToPlan()
    {
        if (SchedulePanel.Visibility != Visibility.Visible) return;
        if (ScheduleGrid.ItemsSource is null) return;

        var match = ScheduleGrid.Items
            .OfType<ScheduleRowView>()
            .FirstOrDefault(row => ReferenceEquals(row.Element, Plan.SelectedElement));

        if (ReferenceEquals(ScheduleGrid.SelectedItem, match)) return;

        _syncingScheduleSelection = true;
        try
        {
            ScheduleGrid.SelectedItem = match;
            if (match is not null) ScheduleGrid.ScrollIntoView(match);
        }
        finally
        {
            _syncingScheduleSelection = false;
        }
    }

    private void OnExportSchedule(object sender, RoutedEventArgs e)
    {
        if (_scheduleResult is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Schedule",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = _scheduleResult.Definition.Name + ".csv"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, ScheduleCsv.Write(_scheduleResult));
            StatusHint.Text = $"Exported {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Could not export schedule",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// The material takeoff: quantities by material rather than by element, which is the
    /// question that gets priced and ordered.
    /// </summary>
    private void OnShowTakeoff(object sender, RoutedEventArgs e)
    {
        var totals = MaterialTakeoff.Totals(_document);

        if (totals.Count == 0)
        {
            MessageBox.Show(this,
                "There is nothing to take off yet. Draw some walls or lay a floor first.",
                "Material Takeoff", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Material Takeoff",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "Material Takeoff.csv"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, ScheduleCsv.WriteTakeoff(totals));
            StatusHint.Text =
                $"Exported {totals.Count} materials, " +
                $"{Units.FormatVolume(totals.Sum(total => total.Volume))} in total";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Could not export takeoff",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- status bar ------------------------------------------------------------

    private void OnCursorMoved(object? sender, Point2D model)
    {
        StatusX.Text = $"X: {model.X:0} mm";
        StatusY.Text = $"Y: {model.Y:0} mm";
    }

    private void RefreshStatus() => StatusZoom.Text = $"Zoom {Plan.ZoomPercent:0}%";

    /// <summary>Title bar and status bar both show the file and whether it has unsaved work.</summary>
    private void RefreshTitle()
    {
        var name = _path is null ? "Untitled" : Path.GetFileNameWithoutExtension(_path);
        var marker = _history.IsModified ? "*" : string.Empty;

        Title = $"{name}{marker} â€” BIMDesigner 0.1";
        StatusSaved.Text = _path is null
            ? _history.IsModified ? "Unsaved project â€” modified" : "Unsaved project"
            : _history.IsModified ? $"{Path.GetFileName(_path)} â€” modified" : Path.GetFileName(_path);
    }
}
