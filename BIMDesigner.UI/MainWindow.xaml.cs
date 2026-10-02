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
using BIMDesigner.Core.Annotation;
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

    /// <summary>A 3D rebuild asked for by a drag and not yet done, so a fast drag asks once.</summary>
    private bool _dragRebuildPending;

    /// <summary>Guards the section picker while it is being repopulated.</summary>
    private bool _loadingSections;

    /// <summary>The same guard for the sheet pickers.</summary>
    private bool _loadingSheets;

    /// <summary>Guards the option-bar handlers while they are being repopulated.</summary>
    private bool _loadingOptions;

    public MainWindow()
    {
        InitializeComponent();
        Cube.Model = Model3D;
        PlanDetailPicker.SelectedIndex = (int)DetailLevel.Fine;
        DarkThemeToggle.IsChecked = !AppTheme.IsLight;

        Plan.CursorMoved += OnCursorMoved;
        Plan.SelectionChanged += (_, _) =>
        {
            RefreshProperties();
            RefreshContextTab();
            SyncScheduleToPlan();
            SyncSectionToPlan();

            if (ModelPanel.Visibility == Visibility.Visible)
                Model3D.SetSelection(Plan.SelectedElements.Select(element => element.Id));
        };

        // Drawing a section opens it: the marker and the view are the same thing, so there is
        // nothing sensible to do between creating one and looking at it.
        Plan.SectionPlaced += (_, marker) => ShowSection(marker);
        Plan.SweepEditChanged += (_, _) => SyncSweepEditControls();
        Plan.JunctionsChanged += (_, _) => RefreshJunctionOptions();
        Plan.WallPointModeChanged += (_, _) => ContextAddPoint.IsChecked = Plan.AddingWallPoints;

        Section.SelectionChanged += (_, _) =>
        {
            if (_syncingSectionSelection) return;
            Plan.Select(Section.SelectedElement);
        };

        SheetSurface.SelectionChanged += (_, _) => RefreshViewportControls();

        // Clicking something in 3D selects it everywhere, so its properties can be edited
        // without leaving the view it was found in.
        // Right-clicking something in 3D asks what to do with it, the same menu the plan gives.
        Model3D.ElementMenuRequested += (_, id) =>
        {
            var picked = _document.Elements.FirstOrDefault(e => e.Id == id) ?? CurtainPanel.Find(_document, id);
            if (picked is null) return;

            Plan.Select(picked);
            Plan.ShowMenuFor(picked, Model3D);
        };

        // A roof window selected in 3D can be dragged along its roof. The view is rebuilt as it
        // goes, but no more often than it can be drawn, and the drag is one step to undo.
        Model3D.CanDrag = Plan.CanDragIn3D;
        Model3D.ElementDragged += (_, drag) =>
        {
            if (!Plan.DragRoofWindowIn3D(drag.Id, drag.From, drag.Through) || _dragRebuildPending) return;

            _dragRebuildPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _dragRebuildPending = false;
                Refresh3D();
            });
        };
        Model3D.ElementDragEnded += (_, _) => Plan.EndRoofWindowDrag();

        Model3D.ElementClicked += (_, hit) =>
        {
            // A door or window tool active: a click on a wall in 3D puts one in it, at the
            // place it was clicked. Revit places them in plan, section, elevation and 3D alike.
            if (hit is { } picked && Plan.ActiveTool is PlanTool.Door or PlanTool.Window &&
                Plan.PlaceOpeningIn3D(picked.Id, picked.At))
            {
                Model3D.Focus();
                return;
            }

            // Paint and Split Face act on the face of a wall where it is clicked.
            if (hit is { } painted && Plan.ActiveTool == PlanTool.Paint && Plan.PaintIn3D(painted.Id, painted.At))
            {
                Model3D.Focus();
                return;
            }

            if (hit is { } split && Plan.ActiveTool == PlanTool.SplitFace && Plan.SplitFaceIn3D(split.Id, split.At))
            {
                Model3D.Focus();
                return;
            }

            // And the roof window tool: a click on a roof puts one in it there.
            if (hit is { } onRoof && Plan.ActiveTool == PlanTool.RoofWindow &&
                Plan.PlaceRoofWindowIn3D(onRoof.Id, onRoof.At))
            {
                Model3D.Focus();
                return;
            }

            // The chimney tool: a click on a roof puts a stack up through it there.
            if (hit is { } onChimneyRoof && Plan.ActiveTool == PlanTool.Chimney &&
                Plan.PlaceChimneyIn3D(onChimneyRoof.Id, onChimneyRoof.At))
            {
                Model3D.Focus();
                return;
            }

            // The roof drain tool: a click on a flat roof puts one in it there.
            if (hit is { } onFlatRoof && Plan.ActiveTool == PlanTool.RoofDrain &&
                Plan.PlaceRoofDrainIn3D(onFlatRoof.Id, onFlatRoof.At))
            {
                Model3D.Focus();
                return;
            }

            // The downpipe tool: a click on a gutter puts one on it there.
            if (hit is { } onGutter && Plan.ActiveTool == PlanTool.Downpipe &&
                Plan.PlaceDownpipeIn3D(onGutter.Id, onGutter.At))
            {
                Model3D.Focus();
                return;
            }

            // The shaft tool: a click on a roof, floor or ceiling cuts a shaft through it there.
            if (hit is { } onSlab && Plan.ActiveTool == PlanTool.Shaft &&
                Plan.PlaceShaftIn3D(onSlab.Id, onSlab.At))
            {
                Model3D.Focus();
                return;
            }

            var id = hit?.Id;
            // A curtain wall's panel is not stored as an element of its own, so a click that
            // matches nothing in the document may still be one of them.
            var element = id is { } found
                ? _document.Elements.FirstOrDefault(e => e.Id == found) ?? CurtainPanel.Find(_document, found)
                : null;

            // Something on another storey is selected by switching the plan to that storey;
            // otherwise the plan would drop it again as not being on its drawing.
            if (element is not null && element.LevelId != Plan.ActiveLevelId &&
                _document.FindLevel(element.LevelId) is { } level)
            {
                LevelPicker.SelectedItem = level;
            }

            // A dormer is picked whole, and a second click on it picks the part.
            Plan.SelectPicked(element);
            Model3D.Focus();
        };

        // A viewport drag writes to the model live and records one command on release, the
        // same bargain a wall drag makes.
        SheetSurface.ViewportMoved += (_, moved) =>
            _history.Record(new MoveViewportCommand(moved.Viewport, moved.From, moved.To));
        Plan.ViewChanged += (_, _) => RefreshStatus();
        Plan.HintChanged += (_, hint) => StatusHint.Text = hint;

        // A door that cannot be turned to open a way it would hit something: an error, in a dialog.
        Plan.DoorRefused += (_, why) => MessageBox.Show(this, why, "Door", MessageBoxButton.OK, MessageBoxImage.Warning);
        Plan.RoofWindowRefused += (_, why) => MessageBox.Show(this, why, "Roof Window", MessageBoxButton.OK, MessageBoxImage.Warning);
        Plan.PinnedRefused += (_, why) => MessageBox.Show(this, why, "Pinned", MessageBoxButton.OK, MessageBoxImage.Warning);
        Plan.ToolFinished += (_, _) => BackToModify();
        Plan.ElevationPicked += (_, side) => ShowElevation(side);
        Plan.SplitFaceChanged += (_, _) => Model3D.PendingCorner = Plan.SplitFaceCorner;

        // Pick New offers its Placement choice only while it is waiting for a host, as Revit's
        // Placement panel appears only for the length of the move.
        Plan.PickNewHostChanged += (_, _) => ShowOptionsForActiveTool();
        Plan.SketchChanged += (_, _) =>
        {
            RefreshRoofSketch();
            CommandManager.InvalidateRequerySuggested();
        };

        // A roof made from picked walls: ask, as Revit does, whether those walls should rise to
        // meet it. Yes is what closes the gable ends.
        Plan.RoofMadeFromWalls += (_, roof) =>
        {
            var answer = MessageBox.Show(this,
                $"Attach {(roof.IsExtrusion ? "the walls under this roof" : "the walls this roof was picked from")} to it?\n\nTheir tops then follow the roof's underside - which closes the gable ends - and keep following it when it changes.",
                "Roof", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer == MessageBoxResult.Yes && Plan.AttachPickedWalls(roof) > 0)
            {
                RefreshProperties();
                Refresh3D();
            }
        };

        // A dormer the roof could not take says so where it will be seen, and offers the one
        // that does fit there, if one does.
        Plan.DormerRefused += (_, refused) =>
        {
            // Too near a hipped end: the same dormer, where it does fit along the slope.
            if (refused.FitsAt is { } fitsAt)
            {
                var move = MessageBox.Show(this,
                    $"{refused.Problem}\n\nPut the dormer there instead, {Units.FormatLength(refused.At.DistanceTo(fitsAt))} along from where you clicked?",
                    "Dormer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

                if (move == MessageBoxResult.Yes) Plan.DormerAt(fitsAt);
                return;
            }

            if (refused.Instead is { } instead)
            {
                var answer = MessageBox.Show(this,
                    $"{refused.Problem}\n\nA shed dormer - one gentle slope, no ridge of its own - fits here, " +
                    $"{Units.FormatLength(instead.Height)} high. Add that instead?",
                    "Dormer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

                if (answer != MessageBoxResult.Yes) return;

                Plan.Dormer = instead;
                ShowDormerOptions();
                Plan.DormerAt(refused.At);
                return;
            }

            MessageBox.Show(this, refused.Problem, "Dormer", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        // A roof by extrusion placed in plan: its section is drawn next, square-on, over the
        // width just clicked - where Revit would go to an elevation to draw it.
        Plan.ExtrusionPlaced += (_, request) => DrawExtrusionProfile(request);
        Plan.EditProfileRequested += (_, roof) => EditRoofProfile(roof);

        // Attach Top/Base offers its style and offset only while it waits for a target.
        Plan.AttachColumnsChanged += (_, _) => ShowOptionsForActiveTool();
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
        // Roofs on their walls and dormers on their roofs, as any edit would leave them: a file
        // saved before they followed as they do now opens as it would be drawn after one.
        RoofSketch.FollowWalls(document);
        Dormers.FollowRoofs(document);

        _document = document;
        _path = path;

        _history = new UndoStack();
        _history.Changed += (_, _) =>
        {
            // Roof edges picked off walls go wherever their walls now are - after a move, a
            // change of type, an undo. Worked out afresh rather than carried by each command,
            // so no way of moving a wall can leave its roof behind.
            if (RoofSketch.FollowWalls(_document)) Plan.RefreshModel();

            // And dormers go on standing on their roofs as those roofs now are.
            if (Dormers.FollowRoofs(_document)) Plan.RefreshModel();

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

            // What the selection can have done to it may have changed with it: a wall given a
            // point can be straightened.
            if (ContextTab.Visibility == Visibility.Visible) RefreshContextTab(bringForward: false);
            RefreshPlanScale();
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
        // An ordinary layered wall to start with, not whichever type sorts first.
        WallTypePicker.SelectedItem = wallTypes.OfType<WallType>().FirstOrDefault(t => t.Function == WallFunction.Exterior) ?? wallTypes.FirstOrDefault();

        DoorTypePicker.ItemsSource = doorTypes;
        DoorTypePicker.SelectedItem = doorTypes.FirstOrDefault();

        WindowTypePicker.ItemsSource = windowTypes;
        WindowTypePicker.SelectedItem = windowTypes.FirstOrDefault();

        var roofWindowTypes = _document.TypesOf<RoofWindowType>().OrderBy(t => t.Name).ToList();
        RoofWindowTypePicker.ItemsSource = roofWindowTypes;
        RoofWindowTypePicker.SelectedItem = roofWindowTypes.FirstOrDefault();
        Plan.ActiveRoofWindowTypeId = roofWindowTypes.FirstOrDefault()?.Id ?? Guid.Empty;

        var families = _document.TypesOf<ComponentType>()
            .OrderBy(t => t.Kind.ToString())
            .ThenBy(t => t.Name)
            .Select(t => new ComponentFamilyChoice(t))
            .ToList();

        ComponentTypePicker.ItemsSource = families;
        ComponentTypePicker.SelectedItem = families.FirstOrDefault();

        var columnTypes = _document.TypesOf<ColumnType>().OrderBy(t => t.Name).ToList();
        ColumnTypePicker.ItemsSource = columnTypes;
        ColumnTypePicker.SelectedItem = columnTypes.FirstOrDefault();

        // Height first, as Revit's option bar has it - a column is usually going up.
        ColumnDirectionPicker.ItemsSource = new[] { "Height", "Depth" };
        ColumnDirectionPicker.SelectedIndex = 0;

        ColumnLevelPicker.ItemsSource = new object[] { "Unconnected" }.Concat(_document.Levels).ToList();
        ColumnLevelPicker.SelectedIndex = 0;

        ColumnAttachmentStylePicker.ItemsSource = EnumText.Choices<ColumnAttachmentStyle>();
        ColumnAttachmentStylePicker.SelectedIndex = 0;

        PickNewHostModePicker.ItemsSource = EnumText.Choices<ComponentPlacementMode>();
        PickNewHostModePicker.SelectedItem = EnumText.Humanise(ComponentPlacementMode.Face);

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

        // Whatever the picker shows: it starts on an exterior wall, not the first type by name.
        Plan.ActiveWallTypeId = (WallTypePicker.SelectedItem as ElementType)?.Id ?? Guid.Empty;
        Plan.ActiveDoorTypeId = doorTypes.FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveWindowTypeId = windowTypes.FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveComponentTypeId = (ComponentTypePicker.SelectedItem as ComponentFamilyChoice)?.Type.Id ?? Guid.Empty;
        Plan.ActiveColumnTypeId = (ColumnTypePicker.SelectedItem as ColumnType)?.Id ?? Guid.Empty;
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
        RefreshPlaceWallTab();
        if (tool == PlanTool.Select && Plan.SelectedElements.Count > 0) RefreshContextTab();
        var isSlab = tool is PlanTool.Floor or PlanTool.Ceiling or PlanTool.Roof or PlanTool.RoofExtrusion;
        var isSweep = tool is PlanTool.Sweep or PlanTool.Reveal;
        var isRoofEdge = tool is PlanTool.Fascia or PlanTool.Gutter or PlanTool.Soffit;

        // Grids, sections, annotation and the editing tools are not built from a type, so
        // offering one would be asking a question the tool never reads the answer to.
        var typeless = tool is PlanTool.Grid or PlanTool.Section
            or PlanTool.Dimension or PlanTool.Tag or PlanTool.Text
            or PlanTool.Offset or PlanTool.Mirror or PlanTool.Array or PlanTool.Rotate or PlanTool.Scale or PlanTool.SplitGap
            or PlanTool.Paint or PlanTool.SplitFace
            or PlanTool.WallJoins or PlanTool.JoinGeometry or PlanTool.WallOpening
            or PlanTool.JoinRoof or PlanTool.DormerOpening or PlanTool.Dormer or PlanTool.Shaft or PlanTool.Downpipe or PlanTool.RoofDrain or PlanTool.Chimney;

        WallOpeningOptions.Visibility = tool == PlanTool.WallOpening ? Visibility.Visible : Visibility.Collapsed;
        DormerOptions.Visibility = tool == PlanTool.Dormer ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Dormer) ShowDormerOptions();
        ShaftOptions.Visibility = tool == PlanTool.Shaft ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Shaft) ShowShaftOptions();
        ChimneyOptions.Visibility = tool == PlanTool.Chimney ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Chimney) ShowChimneyOptions();

        JunctionOptions.Visibility = tool == PlanTool.WallJoins ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.WallJoins) RefreshJunctionOptions();

        PickNewHostOptions.Visibility = Plan.PickingNewHost ? Visibility.Visible : Visibility.Collapsed;
        AttachColumnOptions.Visibility = Plan.AttachingColumns ? Visibility.Visible : Visibility.Collapsed;
        OffsetOptions.Visibility = tool == PlanTool.Offset ? Visibility.Visible : Visibility.Collapsed;
        WallDrawOptions.Visibility = tool == PlanTool.Wall ? Visibility.Visible : Visibility.Collapsed;
        MirrorOptions.Visibility = tool == PlanTool.Mirror ? Visibility.Visible : Visibility.Collapsed;
        ArrayOptions.Visibility = tool == PlanTool.Array ? Visibility.Visible : Visibility.Collapsed;
        DimensionOptions.Visibility = tool == PlanTool.Dimension ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Dimension)
        {
            _loadingOptions = true;
            DimensionPreferPicker.ItemsSource ??= DimensionPreferences.Keys.ToList();
            DimensionPreferPicker.SelectedItem = DimensionPreferences.First(entry => entry.Value == Plan.DimensionPrefer).Key;
            _loadingOptions = false;
        }

        SplitGapOptions.Visibility = tool == PlanTool.SplitGap ? Visibility.Visible : Visibility.Collapsed;
        PaintOptions.Visibility = tool == PlanTool.Paint ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Paint) ShowPaintOptions();
        RotateOptions.Visibility = tool == PlanTool.Rotate ? Visibility.Visible : Visibility.Collapsed;
        RotateCentreButton.IsChecked = Plan.PlacingRotateCentre;
        ScaleOptions.Visibility = tool == PlanTool.Scale ? Visibility.Visible : Visibility.Collapsed;

        RoofEdgeTypePicker.Visibility = isRoofEdge ? Visibility.Visible : Visibility.Collapsed;
        if (isRoofEdge) LoadRoofEdgeTypes(tool);

        SweepOptions.Visibility = isSweep ? Visibility.Visible : Visibility.Collapsed;
        SweepTypePicker.Visibility = isSweep ? Visibility.Visible : Visibility.Collapsed;
        if (isSweep) LoadSweepTypes(tool == PlanTool.Sweep ? SweepKind.Sweep : SweepKind.Reveal);

        // Selecting builds nothing, so the bar names the selection instead of offering types.
        var selecting = tool == PlanTool.Select;
        ModifyCaption.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;

        WallTypePicker.Visibility = tool is PlanTool.Door or PlanTool.Window or PlanTool.RoofWindow or PlanTool.Component or PlanTool.Column || isSlab || isSweep || isRoofEdge || typeless || selecting
            ? Visibility.Collapsed : Visibility.Visible;
        TypeLabel.Visibility = typeless || selecting ? Visibility.Collapsed : Visibility.Visible;
        DoorTypePicker.Visibility = tool == PlanTool.Door ? Visibility.Visible : Visibility.Collapsed;
        TagOnPlacementBox.Visibility = tool is PlanTool.Door or PlanTool.Window ? Visibility.Visible : Visibility.Collapsed;
        WindowTypePicker.Visibility = tool == PlanTool.Window ? Visibility.Visible : Visibility.Collapsed;
        RoofWindowTypePicker.Visibility = tool == PlanTool.RoofWindow ? Visibility.Visible : Visibility.Collapsed;
        ComponentTypePicker.Visibility = tool == PlanTool.Component ? Visibility.Visible : Visibility.Collapsed;
        ColumnTypePicker.Visibility = ColumnOptions.Visibility =
            tool == PlanTool.Column ? Visibility.Visible : Visibility.Collapsed;
        SlabTypePicker.Visibility = isSlab ? Visibility.Visible : Visibility.Collapsed;
        RoofOptions.Visibility = tool == PlanTool.Roof ? Visibility.Visible : Visibility.Collapsed;

        if (tool == PlanTool.Roof) ShowRoofSketchOptions();

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
            PlanTool.Roof or PlanTool.RoofExtrusion => "Roof type",
            PlanTool.Sweep => "Sweep type",
            PlanTool.Reveal => "Reveal type",
            _ => "Wall type"
        };

        // The location line only means anything while drawing walls.
        var forWalls = tool is PlanTool.Wall or PlanTool.Split or PlanTool.Trim;
        LocationLineLabel.Visibility = forWalls ? Visibility.Visible : Visibility.Collapsed;
        LocationLinePicker.Visibility = forWalls ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The sweep or reveal types, whichever the tool places, with the last one used chosen.</summary>
    private void LoadSweepTypes(SweepKind kind)
    {
        _loadingOptions = true;
        var types = _document.TypesOf<WallSweepType>().Where(t => t.Kind == kind).OrderBy(t => t.Name).ToList();
        var active = kind == SweepKind.Sweep ? Plan.ActiveSweepTypeId : Plan.ActiveRevealTypeId;
        SweepTypePicker.ItemsSource = types;
        // First time round a skirting, the sweep most often placed, rather than the first by name.
        SweepTypePicker.SelectedItem = types.FirstOrDefault(t => t.Id == active)
            ?? types.FirstOrDefault(t => t.Profile == SweepProfile.Skirting)
            ?? types.FirstOrDefault();
        _loadingOptions = false;
        OnActiveSweepTypeChanged(this, null!);
    }

    /// <summary>The fascia or gutter types, on the options bar for the tool that runs one.</summary>
    /// <summary>A choice on the fascia, gutter or soffit picker: a type, or - for a fascia - whichever stands out.</summary>
    private sealed record RoofEdgeTypeChoice(string Name, Guid Id, PlanTool Tool);

    private void LoadRoofEdgeTypes(PlanTool tool)
    {
        _loadingOptions = true;
        var types = (tool switch
        {
            PlanTool.Gutter => _document.TypesOf<GutterType>().Cast<ElementType>(),
            PlanTool.Soffit => _document.TypesOf<SoffitType>().Cast<ElementType>(),
            _ => _document.TypesOf<FasciaType>().Cast<ElementType>()
        }).OrderBy(t => t.Name).Select(t => new RoofEdgeTypeChoice(t.Name, t.Id, tool)).ToList();

        // A fascia can be left to stand out against whatever walls and roof it is put on.
        if (tool == PlanTool.Fascia) types.Insert(0, new RoofEdgeTypeChoice("Automatic - stands out from the walls and roof", Guid.Empty, tool));

        var active = tool switch
        {
            PlanTool.Gutter => Plan.ActiveGutterTypeId,
            PlanTool.Soffit => Plan.ActiveSoffitTypeId,
            _ => Plan.ActiveFasciaTypeId
        };
        RoofEdgeTypePicker.ItemsSource = types;
        RoofEdgeTypePicker.SelectedItem = types.FirstOrDefault(t => t.Id == active) ?? types.FirstOrDefault();
        _loadingOptions = false;
        OnActiveRoofEdgeTypeChanged(this, null!);
    }

    private void OnActiveRoofEdgeTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null || RoofEdgeTypePicker.SelectedItem is not RoofEdgeTypeChoice choice) return;
        switch (choice.Tool)
        {
            case PlanTool.Gutter: Plan.ActiveGutterTypeId = choice.Id; break;
            case PlanTool.Soffit: Plan.ActiveSoffitTypeId = choice.Id; break;
            default: Plan.ActiveFasciaTypeId = choice.Id; break;
        }
    }

    /// <summary>Roof panel: a fascia all round each selected roof.</summary>
    private void OnAddFasciaAllRound(object sender, RoutedEventArgs e) => AddRoofEdgesAllRound(RoofEdgeKind.Fascia);

    /// <summary>Roof panel: gutters along every eave of each selected roof.</summary>
    private void OnAddGuttersAllRound(object sender, RoutedEventArgs e) => AddRoofEdgesAllRound(RoofEdgeKind.Gutter);

    /// <summary>Roof panel: soffits under every overhanging eave of each selected roof.</summary>
    private void OnAddSoffitsAllRound(object sender, RoutedEventArgs e) => AddRoofEdgesAllRound(RoofEdgeKind.Soffit);

    private void AddRoofEdgesAllRound(RoofEdgeKind kind)
    {
        var roofs = Plan.SelectedElements.OfType<Roof>().ToList();
        if (roofs.Count == 0)
        {
            StatusHint.Text = "Select a roof first.";
            return;
        }

        // A main roof's dormers too: their eaves want the same as its own.
        roofs = roofs.Concat(roofs.SelectMany(roof => _document.Elements.OfType<Roof>().Where(dormer => dormer.JoinedTo == roof.Id)))
            .Distinct().ToList();

        var added = roofs.Select(roof => Plan.AddAllRound(roof, kind)).OfType<RoofEdgeSweep>().ToList();
        AfterHistoryChange();
        var edges = added.Sum(sweep => sweep.EdgeIds.Count);
        var overhangs = roofs.Any(roof => RoofEdgeSweeps.SoffitEdges(_document, roof).Count > 0);
        StatusHint.Text = added.Count == 0
            ? roofs.All(roof => roof.IsExtrusion)
                ? $"A roof by extrusion has no footprint edges for a {kind.ToString().ToLowerInvariant()} to run along."
                : kind switch
                {
                    RoofEdgeKind.Gutter => "Every eave of that roof has a gutter already - or it has no eaves, being flat.",
                    RoofEdgeKind.Soffit when !overhangs =>
                        "This roof does not stand out past its walls, so there is no overhang underneath to close. " +
                        "Give it one: set Overhang in Properties - 450 mm, say - then add soffits.",
                    RoofEdgeKind.Soffit => "Every overhanging eave of that roof has a soffit already.",
                    _ => "That roof has a fascia all round already."
                }
            : kind switch
            {
                RoofEdgeKind.Gutter => $"Gutters hung along {edges} eaves. Select one to change its type or move it with its offsets.",
                RoofEdgeKind.Soffit => $"Soffits under {edges} eaves, closing the overhang back to the walls. Select one to change its type.",
                _ => $"A fascia round {edges} edges. Select it to change its type; Fascia on the ribbon picks edges one by one."
            };
    }

    private void OnActiveSweepTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null || SweepTypePicker.SelectedItem is not WallSweepType type) return;
        if (type.Kind == SweepKind.Sweep) Plan.ActiveSweepTypeId = type.Id;
        else Plan.ActiveRevealTypeId = type.Id;
    }

    // ---- roof sketch ------------------------------------------------------------

    /// <summary>
    /// Shows the roof sketch's tab and options bar while a sketch is open, and puts the window
    /// back when it closes. The options show the selected lines' settings when there are any -
    /// the same controls set up new lines and change existing ones, as Revit's do.
    /// </summary>
    private void RefreshRoofSketch()
    {
        var sketching = Plan.IsSketching;

        if (sketching)
        {
            if (RoofSketchTab.Visibility != Visibility.Visible)
            {
                _tabBeforeRoofSketch = Ribbon.SelectedItem as TabItem;
                RoofSketchTab.Visibility = Visibility.Visible;
            }

            RoofSketchTab.Header = Plan.SketchedRoof is null ? "Modify | Create Roof Footprint" : "Modify | Roofs > Edit Footprint";
            ContextTab.Visibility = Visibility.Collapsed;
            Ribbon.SelectedItem = RoofSketchTab;

            RoofSketchPickWalls.IsChecked = Plan.SketchTool == RoofSketchTool.PickWalls;
            RoofSketchLine.IsChecked = Plan.SketchTool == RoofSketchTool.Line;
            RoofSketchRectangle.IsChecked = Plan.SketchTool == RoofSketchTool.Rectangle;
            RoofSketchPolygon.IsChecked = Plan.SketchTool == RoofSketchTool.Polygon;
            RoofSketchModify.IsChecked = Plan.SketchTool == RoofSketchTool.Modify;
            RoofSketchArrow.IsChecked = Plan.SketchTool == RoofSketchTool.SlopeArrow;
            RoofSketchSplit.IsChecked = Plan.SketchTool == RoofSketchTool.Split;
            RoofSketchArc.IsChecked = Plan.SketchTool == RoofSketchTool.Arc;
            RoofSketchCircle.IsChecked = Plan.SketchTool == RoofSketchTool.Circle;
            RoofSketchPickLines.IsChecked = Plan.SketchTool == RoofSketchTool.PickLines;
            RoofSketchOffset.IsChecked = Plan.SketchTool == RoofSketchTool.Offset;
            RoofSketchTrim.IsChecked = Plan.SketchTool == RoofSketchTool.TrimExtend;
            RoofSketchAlignEaves.IsChecked = Plan.SketchTool == RoofSketchTool.AlignEaves;

            if (RoofTool.IsChecked != true)
            {
                _loadingOptions = true;
                RoofTool.IsChecked = true;
                _loadingOptions = false;
            }

            ShowRoofSketchOptions();
            return;
        }

        if (RoofSketchTab.Visibility == Visibility.Visible)
        {
            var wasOpen = ReferenceEquals(Ribbon.SelectedItem, RoofSketchTab);
            RoofSketchTab.Visibility = Visibility.Collapsed;
            if (wasOpen)
                Ribbon.SelectedItem = _tabBeforeRoofSketch is { Visibility: Visibility.Visible } before ? before : Ribbon.Items[0];
        }

        // The sketch finishing or being cancelled leaves the Select tool on in the plan; the
        // ribbon follows it.
        if (Plan.ActiveTool == PlanTool.Select && RoofTool.IsChecked == true)
        {
            _loadingOptions = true;
            SelectTool.IsChecked = true;
            _loadingOptions = false;
            ShowOptionsForActiveTool();
        }
    }

    private TabItem? _tabBeforeRoofSketch;

    private void ShowRoofSketchOptions()
    {
        _loadingOptions = true;

        // With lines selected, the bar shows theirs; otherwise what new lines will be.
        var selected = Plan.SelectedSketchLines;
        var first = selected.FirstOrDefault()?.Edge;

        RoofDefinesSlopeBox.IsChecked = first?.DefinesSlope ?? Plan.SketchDefinesSlope;
        RoofSlopeBox.Text = ParameterFormatter.Format(ParameterDataType.Angle, first?.SlopeDegrees ?? Plan.ActiveRoofSlopeDegrees);
        RoofOverhangBox.Text = Units.FormatLength(first?.Overhang ?? Plan.SketchOverhang);
        RoofExtendToCoreBox.IsChecked = first?.ExtendToCore ?? Plan.SketchExtendToCore;
        RoofSidesBox.Text = Plan.PolygonSides.ToString();

        // Overhang belongs to lines on walls; sides to polygons.
        var picking = Plan.SketchTool == RoofSketchTool.PickWalls || selected.Any(line => line.Edge.WallId is not null);
        RoofOverhangLabel.Visibility = RoofOverhangBox.Visibility = RoofExtendToCoreBox.Visibility =
            picking ? Visibility.Visible : Visibility.Collapsed;
        RoofSidesLabel.Visibility = RoofSidesBox.Visibility =
            Plan.SketchTool == RoofSketchTool.Polygon ? Visibility.Visible : Visibility.Collapsed;

        // The offset belongs to Pick Lines and to Offset; Copy to Offset alone.
        RoofOffsetBox.Text = Units.FormatLength(Plan.SketchOffset);
        RoofOffsetCopyBox.IsChecked = Plan.SketchOffsetCopy;
        RoofOffsetLabel.Visibility = RoofOffsetBox.Visibility =
            Plan.SketchTool is RoofSketchTool.PickLines or RoofSketchTool.Offset ? Visibility.Visible : Visibility.Collapsed;
        RoofOffsetCopyBox.Visibility = Plan.SketchTool == RoofSketchTool.Offset ? Visibility.Visible : Visibility.Collapsed;

        // How Align Eaves brings an eave to height.
        RoofAlignPicker.ItemsSource ??= new[] { "Adjust Height", "Adjust Overhang" };
        RoofAlignPicker.SelectedIndex = Plan.SketchAlignByOverhang ? 1 : 0;
        RoofAlignPicker.Visibility = Plan.SketchTool == RoofSketchTool.AlignEaves ? Visibility.Visible : Visibility.Collapsed;

        // A slope arrow's own settings, for new arrows or the ones selected.
        var arrow = Plan.SelectedSketchArrows.FirstOrDefault();
        var arrows = Plan.SketchTool == RoofSketchTool.SlopeArrow || arrow is not null;
        RoofArrowOptions.Visibility = arrows ? Visibility.Visible : Visibility.Collapsed;

        if (arrows)
        {
            RoofArrowSpecifyPicker.ItemsSource ??= new[] { "Slope", "Height at Tail" };

            var byHeights = arrow?.ByHeights ?? Plan.SketchArrowByHeights;
            RoofArrowSpecifyPicker.SelectedIndex = byHeights ? 1 : 0;
            RoofArrowTailBox.Text = Units.FormatLength(arrow?.TailOffset ?? Plan.SketchArrowTailOffset);
            RoofArrowHeadBox.Text = Units.FormatLength(arrow?.HeadOffset ?? Plan.SketchArrowHeadOffset);
            RoofArrowHeadLabel.Visibility = RoofArrowHeadBox.Visibility = byHeights ? Visibility.Visible : Visibility.Collapsed;

            if (arrow is not null)
                RoofSlopeBox.Text = ParameterFormatter.Format(ParameterDataType.Angle, arrow.SlopeDegrees);

            // Given by heights, an arrow's pitch follows from them, so there is no pitch to type.
            RoofSlopeBox.IsEnabled = !byHeights || Plan.SelectedSketchLines.Count > 0;
        }
        else
        {
            RoofSlopeBox.IsEnabled = true;
        }

        _loadingOptions = false;
    }

    private void OnRoofArrowSpecifyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        var byHeights = RoofArrowSpecifyPicker.SelectedIndex == 1;
        if (!Plan.ChangeSelectedSketchArrows(arrow => arrow.ByHeights = byHeights)) Plan.SketchArrowByHeights = byHeights;
        ShowRoofSketchOptions();
    }

    private void OnRoofArrowTailChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (ParameterFormatter.TryParse(ParameterDataType.Length, RoofArrowTailBox.Text, out var value) && value is double height)
        {
            if (!Plan.ChangeSelectedSketchArrows(arrow => arrow.TailOffset = height)) Plan.SketchArrowTailOffset = height;
        }

        ShowRoofSketchOptions();
    }

    private void OnRoofArrowTailKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnRoofArrowTailChanged(sender, e);
        e.Handled = true;
    }

    private void OnRoofArrowHeadChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (ParameterFormatter.TryParse(ParameterDataType.Length, RoofArrowHeadBox.Text, out var value) && value is double height)
        {
            if (!Plan.ChangeSelectedSketchArrows(arrow => arrow.HeadOffset = height)) Plan.SketchArrowHeadOffset = height;
        }

        ShowRoofSketchOptions();
    }

    private void OnRoofArrowHeadKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnRoofArrowHeadChanged(sender, e);
        e.Handled = true;
    }

    private void OnRoofSketchTool(object sender, RoutedEventArgs e)
    {
        if (Plan is null || sender is not RadioButton { CommandParameter: string name }) return;
        if (Enum.TryParse<RoofSketchTool>(name, out var tool)) Plan.SketchTool = tool;
        Plan.Focus();
    }

    private void OnFinishRoofSketch(object sender, RoutedEventArgs e)
    {
        if (Plan.FinishSketch()) AfterRoofSketch();
        Plan.Focus();
    }

    private void OnCancelRoofSketch(object sender, RoutedEventArgs e)
    {
        Plan.CancelSketch();
        AfterRoofSketch();
    }

    private void AfterRoofSketch()
    {
        RefreshProperties();
        RefreshProjectBrowser();
        Refresh3D();

        // A flat roof sitting at a storey's floor is really that storey's floor, and a roof stands
        // on its line: it would rise up into the storey rather than hang below it.
        if (Plan.SelectedElements.OfType<Roof>().FirstOrDefault() is { } roof &&
            CurtainSpandrels.FloorPosingAsRoof(_document, roof) is { } storey)
            StatusHint.Text = $"This flat roof sits at {storey}, where walls stand on it. A roof stands on its line, so it rises up into {storey}. " +
                              $"If it is the floor of {storey}, draw it with Floor on {storey} instead: its top is at the level and it hangs below.";
    }

    private void OnDeleteSketchLines(object sender, RoutedEventArgs e) => Plan.DeleteSelectedSketchLines();

    private void OnEditRoofFootprint(object sender, RoutedEventArgs e)
    {
        Plan.EditFootprint();
        Plan.Focus();
    }

    /// <summary>Add Dormer on a selected roof: the Dormer tool, ready to click onto it.</summary>
    private void OnAddDormer(object sender, RoutedEventArgs e)
    {
        DormerTool.IsChecked = true;
        Plan.Focus();
    }

    /// <summary>The dormer the next click makes, on the options bar.</summary>
    private void ShowDormerOptions()
    {
        _loadingOptions = true;

        var dormer = Plan.Dormer;
        DormerShapePicker.ItemsSource ??= EnumText.Choices<DormerShape>();
        DormerShapePicker.SelectedItem = EnumText.Humanise(dormer.Shape);
        DormerWidthBox.Text = Units.FormatLength(dormer.Width);
        DormerHeightBox.Text = Units.FormatLength(dormer.Height);
        DormerSlopeBox.Text = ParameterFormatter.Format(ParameterDataType.Angle, dormer.Slope);
        DormerOverhangBox.Text = Units.FormatLength(dormer.Overhang);

        _loadingOptions = false;
    }

    private void OnDormerOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        static double? Read(ParameterDataType type, string text) =>
            ParameterFormatter.TryParse(type, text, out var value) && value is double number ? number : null;

        var dormer = Plan.Dormer;
        var shape = DormerShapePicker.SelectedItem is string name && EnumText.TryParse<DormerShape>(name, out var picked) ? picked : dormer.Shape;

        // A shed falls gently to the front; changing to one brings a steep pitch down with it.
        var slope = Read(ParameterDataType.Angle, DormerSlopeBox.Text) is { } angle and > 1 and < 80 ? angle : dormer.Slope;
        if (shape == DormerShape.Shed && dormer.Shape != DormerShape.Shed && slope > 20) slope = 15;
        if (shape != DormerShape.Shed && dormer.Shape == DormerShape.Shed && slope < 25) slope = 35;

        Plan.Dormer = dormer with
        {
            Shape = shape,
            Width = Read(ParameterDataType.Length, DormerWidthBox.Text) is { } width and > 300 ? width : dormer.Width,
            Height = Read(ParameterDataType.Length, DormerHeightBox.Text) is { } height and > 0 ? height : dormer.Height,
            Slope = slope,
            Overhang = Read(ParameterDataType.Length, DormerOverhangBox.Text) is { } overhang and >= 0 ? overhang : dormer.Overhang
        };

        ShowDormerOptions();
    }

    private void OnDormerOptionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnDormerOptionChanged(sender, e);
        Plan.Focus();
        e.Handled = true;
    }

    /// <summary>The chimney the next click puts up, on the options bar.</summary>
    private void ShowChimneyOptions()
    {
        _loadingOptions = true;
        var types = _document.TypesOf<ChimneyType>().ToList();
        ChimneyTypePicker.ItemsSource = types;
        var type = types.FirstOrDefault(t => t.Id == Plan.ActiveChimneyTypeId) ?? types.FirstOrDefault();
        ChimneyTypePicker.SelectedItem = type;
        ChimneyFireplacePicker.ItemsSource ??= EnumText.Choices<ChimneyFireplace>();
        ChimneyFireplacePicker.SelectedItem = EnumText.Humanise(Plan.ActiveChimneyFireplace ?? type?.Fireplace ?? ChimneyFireplace.None);
        _loadingOptions = false;
    }

    private void OnChimneyOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (ChimneyTypePicker.SelectedItem is ChimneyType type && type.Id != Plan.ActiveChimneyTypeId)
        {
            // A new type brings its own fireplace with it: a stove for a twin-wall flue, none for a gas vent.
            Plan.ActiveChimneyTypeId = type.Id;
            Plan.ActiveChimneyFireplace = null;
        }
        else if (ChimneyFireplacePicker.SelectedItem is string fireplace && EnumText.TryParse<ChimneyFireplace>(fireplace, out var chosen))
        {
            Plan.ActiveChimneyFireplace = chosen;
        }

        ShowChimneyOptions();
    }

    /// <summary>The shaft the next click cuts, on the options bar.</summary>
    private void ShowShaftOptions()
    {
        _loadingOptions = true;

        var round = Plan.ActiveShaftShape == ShaftShape.Round;
        ShaftShapePicker.ItemsSource ??= EnumText.Choices<ShaftShape>();
        ShaftShapePicker.SelectedItem = EnumText.Humanise(Plan.ActiveShaftShape);
        ShaftWidthLabel.Text = round ? "Diameter" : "Width";
        ShaftWidthBox.Text = Units.FormatLength(Plan.ActiveShaftWidth);
        ShaftDepthBox.Text = Units.FormatLength(Plan.ActiveShaftDepth);
        ShaftDepthLabel.Visibility = ShaftDepthBox.Visibility = round ? Visibility.Collapsed : Visibility.Visible;

        _loadingOptions = false;
    }

    private void OnShaftOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        static double? Read(string text) =>
            ParameterFormatter.TryParse(ParameterDataType.Length, text, out var value) && value is double number ? number : null;

        if (ShaftShapePicker.SelectedItem is string name && EnumText.TryParse<ShaftShape>(name, out var shape))
            Plan.ActiveShaftShape = shape;
        if (Read(ShaftWidthBox.Text) is { } width and >= Shafts.SmallestSize) Plan.ActiveShaftWidth = width;
        if (Read(ShaftDepthBox.Text) is { } depth and >= Shafts.SmallestSize) Plan.ActiveShaftDepth = depth;

        ShowShaftOptions();
    }

    private void OnShaftOptionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnShaftOptionChanged(sender, e);
        Plan.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// Downpipes, for a selected gutter or roof: one at each end of each gutter - the roof's, and
    /// its dormers' - where there is not one already.
    /// </summary>
    private void OnAddDownpipes(object sender, RoutedEventArgs e)
    {
        var selected = Plan.SelectedElements;
        var roofs = selected.OfType<Roof>().ToList();
        roofs = roofs.Concat(roofs.SelectMany(roof => _document.Elements.OfType<Roof>().Where(dormer => dormer.JoinedTo == roof.Id))).Distinct().ToList();

        var gutters = selected.OfType<Gutter>()
            .Concat(_document.Elements.OfType<Gutter>().Where(gutter => roofs.Any(roof => roof.Id == gutter.RoofId)))
            .Distinct()
            .ToList();

        if (gutters.Count == 0)
        {
            StatusHint.Text = "That roof has no gutters to take downpipes: add Gutters first.";
            return;
        }

        var added = Plan.AddDownpipesAtEnds(gutters);
        AfterHistoryChange();
        StatusHint.Text = added == 0
            ? "Every end of those gutters has a downpipe already."
            : $"{added} downpipe{(added == 1 ? "" : "s")} down from the gutters' ends. Use the Downpipe tool to add one anywhere along a gutter.";
    }

    /// <summary>Roof Drain, from a selected flat roof: the tool, waiting for the click on the roof.</summary>
    private void OnAddRoofDrain(object sender, RoutedEventArgs e) => RoofDrainTool.IsChecked = true;

    /// <summary>Shaft, from a selected roof: the tool, waiting for the click on the roof.</summary>
    private void OnAddShaft(object sender, RoutedEventArgs e) => ShaftTool.IsChecked = true;

    /// <summary>Dormer Opening: the selected roof waits for the dormer to be clicked.</summary>
    private void OnDormerOpening(object sender, RoutedEventArgs e)
    {
        if (!Plan.BeginDormerOpening()) StatusHint.Text = "Select the roof the dormer comes out of first.";
        Plan.Focus();
    }

    /// <summary>The project's levels, by name and elevation, to set a roof's profile out against.</summary>
    private IReadOnlyList<(string Name, double Elevation)> LevelLines() =>
        _document.Levels.Select(level => (level.Name, level.Elevation)).ToList();

    /// <summary>The third click of Roof by Extrusion: the profile is drawn, then the roof made from it.</summary>
    private void DrawExtrusionProfile(RoofExtrusionRequest request)
    {
        var level = _document.FindLevel(Plan.ActiveLevelId)?.Elevation ?? 0;
        var window = new ExtrusionProfileWindow(0, request.Width, null, request.BaseOffset, level, LevelLines()) { Owner = this };

        if (window.ShowDialog() == true)
        {
            Plan.PlaceExtrusionRoof(
                new RoofExtrusion(request.Origin, request.Direction, window.Profile, request.Start, request.End),
                window.BaseOffset);
        }
        else
        {
            StatusHint.Text = "No roof made. Click the start of the profile's line to place another.";
        }

        Plan.Focus();
    }

    private void OnEditRoofProfile(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements is [Roof { IsExtrusion: true } roof]) EditRoofProfile(roof);
        else StatusHint.Text = "Select one roof by extrusion to edit its profile.";
    }

    /// <summary>Opens an extruded roof's section again, over the building it spans.</summary>
    private void EditRoofProfile(Roof roof)
    {
        if (roof.Extrusion is not { } extrusion) return;

        var (start, width) = Plan.SpanUnder(roof);
        var level = _document.FindLevel(roof.LevelId)?.Elevation ?? 0;
        var window = new ExtrusionProfileWindow(start, width, extrusion.Shape, roof.HeightOffset, level, LevelLines()) { Owner = this };

        if (window.ShowDialog() == true && Plan.ChangeExtrusion(roof, extrusion.With(shape: window.Profile), window.BaseOffset))
        {
            StatusHint.Text =
                $"{EnumText.Humanise(roof.Form)} roof by extrusion: {Units.FormatArea(roof.SlopingArea(_document))} of covering.";
        }

        Plan.Focus();
    }

    /// <summary>
    /// Leaving a roof sketch for another tool: Revit will not let a sketch be abandoned by
    /// accident, and neither does this. Finish, throw it away, or stay.
    /// </summary>
    private bool LeaveRoofSketch()
    {
        if (!Plan.IsSketching) return true;

        var answer = MessageBox.Show(this,
            "Finish the roof sketch before switching tools?\n\nYes makes the roof, No throws the sketch away, Cancel keeps sketching.",
            "Roof sketch", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                if (!Plan.FinishSketch()) return false;
                AfterRoofSketch();
                return true;

            case MessageBoxResult.No:
                Plan.CancelSketch();
                return true;

            default:
                return false;
        }
    }

    private void OnRoofDefinesSlopeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        var slopes = RoofDefinesSlopeBox.IsChecked == true;
        if (!Plan.ChangeSelectedSketchLines(edge => edge.DefinesSlope = slopes)) Plan.SketchDefinesSlope = slopes;
        Plan.Focus();
    }

    private void OnRoofSlopeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (ParameterFormatter.TryParse(ParameterDataType.Angle, RoofSlopeBox.Text, out var value) &&
            value is double degrees && degrees > 0 && degrees < 90)
        {
            // Selected lines first, then selected arrows; with nothing selected, it is the
            // pitch new lines and arrows get.
            if (!Plan.ChangeSelectedSketchLines(edge => edge.SlopeDegrees = degrees) &&
                !Plan.ChangeSelectedSketchArrows(arrow => arrow.SlopeDegrees = degrees))
            {
                Plan.ActiveRoofSlopeDegrees = degrees;
            }
        }

        ShowRoofSketchOptions();
    }

    private void OnRoofSlopeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnRoofSlopeChanged(sender, e);
        e.Handled = true;
    }

    private void OnRoofOverhangChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (ParameterFormatter.TryParse(ParameterDataType.Length, RoofOverhangBox.Text, out var value) &&
            value is double millimetres && millimetres >= 0)
        {
            if (!Plan.ChangeSelectedSketchLines(edge => { if (edge.WallId is not null) edge.Overhang = millimetres; }))
                Plan.SketchOverhang = millimetres;
        }

        ShowRoofSketchOptions();
    }

    private void OnRoofOverhangKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnRoofOverhangChanged(sender, e);
        e.Handled = true;
    }

    private void OnRoofOffsetChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (ParameterFormatter.TryParse(ParameterDataType.Length, RoofOffsetBox.Text, out var value) && value is double millimetres)
            Plan.SketchOffset = Math.Abs(millimetres);

        ShowRoofSketchOptions();
    }

    private void OnRoofOffsetKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnRoofOffsetChanged(sender, e);
        Plan.Focus();
        e.Handled = true;
    }

    private void OnRoofAlignModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        Plan.SketchAlignByOverhang = RoofAlignPicker.SelectedIndex == 1;
        Plan.Focus();
    }

    private void OnRoofOffsetCopyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        Plan.SketchOffsetCopy = RoofOffsetCopyBox.IsChecked == true;
        Plan.Focus();
    }

    private void OnRoofExtendToCoreChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        var toCore = RoofExtendToCoreBox.IsChecked == true;
        if (!Plan.ChangeSelectedSketchLines(edge => { if (edge.WallId is not null) edge.ExtendToCore = toCore; }))
            Plan.SketchExtendToCore = toCore;
        Plan.Focus();
    }

    private void OnRoofSidesChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (int.TryParse(RoofSidesBox.Text, out var sides))
            Plan.PolygonSides = Math.Clamp(sides, WallShapes.MinSides, WallShapes.MaxSides);

        ShowRoofSketchOptions();
    }

    private void OnRoofSidesKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnRoofSidesChanged(sender, e);
        e.Handled = true;
    }

    private void OnSweepPlacementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Plan is null) return;
        Plan.NewSweepVertical = SweepPlacementPicker.SelectedIndex == 1;
        SweepHeightLabel.Visibility = SweepHeightBox.Visibility = Plan.NewSweepVertical ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSweepHeightChanged(object sender, RoutedEventArgs e)
    {
        if (ParameterFormatter.TryParse(ParameterDataType.Length, SweepHeightBox.Text, out var value) && value is double millimetres && millimetres >= 0)
            Plan.NewSweepHeight = millimetres;
        SweepHeightBox.Text = Units.FormatLength(Plan.NewSweepHeight);
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
            case PlanTool.Roof or PlanTool.RoofExtrusion: Plan.ActiveRoofTypeId = type.Id; break;
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
        // In a sketch, undo takes back the last line: the model is not being changed yet.
        if (Plan.IsSketching)
        {
            Plan.UndoSketch();
            return;
        }

        _history.Undo();
        AfterHistoryChange();
    }

    private void OnRedo(object sender, ExecutedRoutedEventArgs e)
    {
        if (Plan.IsSketching)
        {
            Plan.RedoSketch();
            return;
        }

        _history.Redo();
        AfterHistoryChange();
    }

    private void OnCanUndo(object sender, CanExecuteRoutedEventArgs e)
    {
        if (Plan?.IsSketching == true)
        {
            e.CanExecute = Plan.CanUndoSketch;
            if (UndoItem is not null) UndoItem.Header = "_Undo Sketch Line";
            return;
        }

        e.CanExecute = _history.CanUndo;
        if (UndoItem is not null)
            UndoItem.Header = _history.CanUndo ? $"_Undo {_history.UndoName}" : "_Undo";
    }

    private void OnCanRedo(object sender, CanExecuteRoutedEventArgs e)
    {
        if (Plan?.IsSketching == true)
        {
            e.CanExecute = Plan.CanRedoSketch;
            if (RedoItem is not null) RedoItem.Header = "_Redo Sketch Line";
            return;
        }

        e.CanExecute = _history.CanRedo;
        if (RedoItem is not null)
            RedoItem.Header = _history.CanRedo ? $"_Redo {_history.RedoName}" : "_Redo";
    }

    private void OnDeleteSelected(object sender, ExecutedRoutedEventArgs e)
    {
        if (Plan.IsSketching) Plan.DeleteSelectedSketchLines();
        else Plan.DeleteSelected();
    }

    private void OnCanDelete(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = Plan?.IsSketching == true
            ? Plan.SelectedSketchLines.Count + Plan.SelectedSketchArrows.Count > 0
            : Plan?.SelectedElement is not null;

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

    /// <summary>Every tool button, on whichever ribbon tab it sits.</summary>
    private RadioButton[] ToolButtons() =>
    [
        SelectTool, WallTool, DoorTool, WindowTool, RoomTool, ComponentTool, ColumnTool, FloorTool, CeilingTool, RoofTool, RoofExtrusionTool, DormerTool,
        FasciaTool, GutterTool, SoffitTool, RoofWindowTool, ShaftTool, DownpipeTool, RoofDrainTool, ChimneyTool, GridTool,
        SectionTool, DimensionTool, TagTool, TextTool, SplitTool, SplitGapTool, TrimTool, OffsetTool, MirrorTool, ArrayTool,
        RotateTool, ScaleTool, PaintTool, SplitFaceTool,
        SweepTool, RevealTool, WallJoinsTool, JoinGeometryTool, JoinRoofTool, WallOpeningTool
    ];

    /// <summary>
    /// Leaves whatever tool is on for Modify, keeping the selection - Esc, the Modify button,
    /// and Rotate or Scale once they have done their work.
    /// </summary>
    private void BackToModify()
    {
        if (SelectTool.IsChecked == true)
        {
            // Already ticked - but some other tool still on in the plan: put the two back in step.
            foreach (var other in ToolButtons().Where(button => button is not null && !ReferenceEquals(button, SelectTool))) other.IsChecked = false;
            Plan.SetTool(PlanTool.Select);
            ShowOptionsForActiveTool();
        }
        else
        {
            SelectTool.IsChecked = true;
        }

        RefreshContextTab();
    }

    private void OnToolChanged(object sender, RoutedEventArgs e)
    {
        if (Plan is null) return;

        // One tool at a time. The buttons share a group, but WPF only keeps a group to one
        // choice among buttons that are on screen, and most sit on ribbon tabs not showing -
        // so a tool picked on one tab stayed on when another was picked on a different tab.
        // The one just chosen switches every other off itself.
        if (sender is RadioButton { IsChecked: true } chosen)
        {
            foreach (var other in ToolButtons().Where(button => button is not null && !ReferenceEquals(button, chosen)))
                other.IsChecked = false;
        }

        // The ribbon being brought into line with the plan, not a tool being chosen.
        if (_loadingOptions) return;

        // Picking another tool with a roof sketch open asks what to do with the sketch first.
        if (Plan.IsSketching && RoofTool.IsChecked != true && !LeaveRoofSketch())
        {
            _loadingOptions = true;
            foreach (var other in ToolButtons().Where(button => button is not null)) other.IsChecked = false;
            RoofTool.IsChecked = true;
            _loadingOptions = false;
            return;
        }

        Plan.SetTool(
            WallTool.IsChecked == true ? PlanTool.Wall
            : DoorTool.IsChecked == true ? PlanTool.Door
            : WindowTool.IsChecked == true ? PlanTool.Window
            : RoomTool.IsChecked == true ? PlanTool.Room
            : ComponentTool.IsChecked == true ? PlanTool.Component
            : ColumnTool.IsChecked == true ? PlanTool.Column
            : FloorTool.IsChecked == true ? PlanTool.Floor
            : CeilingTool.IsChecked == true ? PlanTool.Ceiling
            : RoofTool.IsChecked == true ? PlanTool.Roof
            : RoofExtrusionTool.IsChecked == true ? PlanTool.RoofExtrusion
            : DormerTool.IsChecked == true ? PlanTool.Dormer
            : FasciaTool.IsChecked == true ? PlanTool.Fascia
            : GutterTool.IsChecked == true ? PlanTool.Gutter
            : SoffitTool.IsChecked == true ? PlanTool.Soffit
            : RoofWindowTool.IsChecked == true ? PlanTool.RoofWindow
            : ShaftTool.IsChecked == true ? PlanTool.Shaft
            : DownpipeTool.IsChecked == true ? PlanTool.Downpipe
            : RoofDrainTool.IsChecked == true ? PlanTool.RoofDrain
            : ChimneyTool.IsChecked == true ? PlanTool.Chimney
            : GridTool.IsChecked == true ? PlanTool.Grid
            : SectionTool.IsChecked == true ? PlanTool.Section
            : DimensionTool.IsChecked == true ? PlanTool.Dimension
            : TagTool.IsChecked == true ? PlanTool.Tag
            : TextTool.IsChecked == true ? PlanTool.Text
            : SplitTool.IsChecked == true ? PlanTool.Split
            : SplitGapTool.IsChecked == true ? PlanTool.SplitGap
            : PaintTool.IsChecked == true ? PlanTool.Paint
            : SplitFaceTool.IsChecked == true ? PlanTool.SplitFace
            : TrimTool.IsChecked == true ? PlanTool.Trim
            : OffsetTool.IsChecked == true ? PlanTool.Offset
            : MirrorTool.IsChecked == true ? PlanTool.Mirror
            : ArrayTool.IsChecked == true ? PlanTool.Array
            : RotateTool.IsChecked == true ? PlanTool.Rotate
            : ScaleTool.IsChecked == true ? PlanTool.Scale
            : SweepTool.IsChecked == true ? PlanTool.Sweep
            : RevealTool.IsChecked == true ? PlanTool.Reveal
            : WallJoinsTool.IsChecked == true ? PlanTool.WallJoins
            : JoinGeometryTool.IsChecked == true ? PlanTool.JoinGeometry
            : JoinRoofTool.IsChecked == true ? PlanTool.JoinRoof
            : WallOpeningTool.IsChecked == true ? PlanTool.WallOpening
            : PlanTool.Select);

        ShowOptionsForActiveTool();
    }

    private void OnDetailLevelChanged(object sender, RoutedEventArgs e) =>
        SetDetailLevel(ReferenceEquals(sender, DetailCoarse) ? DetailLevel.Coarse
            : ReferenceEquals(sender, DetailMedium) ? DetailLevel.Medium
            : DetailLevel.Fine);

    /// <summary>The plan's detail level, from the ribbon or the view control bar: both show it.</summary>
    private void SetDetailLevel(DetailLevel level)
    {
        _loadingOptions = true;
        PlanDetailPicker.SelectedIndex = (int)level;
        _loadingOptions = false;

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

        // In a roof sketch, TAB widens a wall pick to the whole chain of joined walls.
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && Plan.IsSketching &&
            Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem) && Plan.SketchTab())
        {
            e.Handled = true;
            return;
        }

        // TAB over the plan steps through what is under the cursor, as in Revit, rather than
        // moving the keyboard focus to the next control.
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && Plan.IsMouseOver &&
            Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem) && Plan.CycleHover())
        {
            e.Handled = true;
            return;
        }

        // The arrow keys nudge the selection, as Revit's do. They are taken on the way down
        // because WPF would otherwise spend them moving the keyboard focus from one control to
        // the next - the plan would sit still while the toolbar lit up.
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down &&
            Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift &&
            !ModelCoversPlan && SheetPanel.Visibility != Visibility.Visible &&
            Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem or TreeView or TreeViewItem or DataGrid or DataGridCell) &&
            Plan.Nudge(NudgeDirection(e.Key), Keyboard.Modifiers == ModifierKeys.Shift, e.IsRepeat))
        {
            RefreshProperties();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        if (ModelCoversPlan || SheetPanel.Visibility == Visibility.Visible) return;

        // With the component tool live, Space turns what is about to be placed, as it does in
        // Revit; otherwise it flips whatever is selected.
        if (Plan.TurnComponent() || Plan.TurnColumn() || Plan.Flip())
        {
            RefreshProperties();
            e.Handled = true;
        }
    }

    /// <summary>Which way an arrow key points on the plan: up the screen is north, as drawn.</summary>
    private static Vector2D NudgeDirection(Key key) => key switch
    {
        Key.Left => new Vector2D(-1, 0),
        Key.Right => new Vector2D(1, 0),
        Key.Up => new Vector2D(0, 1),
        _ => new Vector2D(0, -1)
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Let text boxes in the property panel and the schedule grid keep their keystrokes.
        if (Keyboard.FocusedElement is TextBox) return;

        // Esc in a roof sketch stops the line being drawn, then goes back to selecting lines -
        // it never throws the sketch away. That is what Cancel is for.
        if (Plan.IsSketching && e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Plan.SketchEscape();
            e.Handled = true;
            return;
        }

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
        // Tiled, the plan is there beside it and keeps its keys.
        if (ModelCoversPlan)
        {
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (Plan.CancelPendingOperation()) { }
                else if (Plan.ActiveTool != PlanTool.Select) BackToModify();
                else if (Plan.SelectedElements.Count > 0) Plan.Select(null);
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

        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.P)
        {
            Plan.PinSelected(false);
            e.Handled = true;
            return;
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
            case Key.Enter:
                // Finishes a spline wall at its last point.
                if (Plan.FinishDrawing()) e.Handled = true;
                break;
            case Key.Escape:
                // Esc first abandons whatever tool operation is half-done, then leaves the tool
                // for Modify, as Revit's does, and only then lets go of the selection - so it
                // takes more than one press to lose a selection by accident.
                if (Plan.CancelPendingOperation()) break;
                if (Plan.ActiveTool != PlanTool.Select) BackToModify();
                else Plan.Select(null);
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
            case Key.Q:
                RotateTool.IsChecked = true;
                break;
            case Key.K:
                ScaleTool.IsChecked = true;
                break;
            case Key.P:
                Plan.PinSelected(true);
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

    private void OnActiveRoofWindowTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (RoofWindowTypePicker.SelectedItem is RoofWindowType type) Plan.ActiveRoofWindowTypeId = type.Id;
    }

    /// <summary>Roof panel: the Roof Window tool, to click on the roof's slope.</summary>
    private void OnAddRoofWindow(object sender, RoutedEventArgs e) => RoofWindowTool.IsChecked = true;

    private void OnActiveWindowTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (WindowTypePicker.SelectedItem is WindowType type) Plan.ActiveWindowTypeId = type.Id;
    }

    private void OnPickNewHostModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (PickNewHostModePicker.SelectedItem is string choice &&
            EnumText.TryParse<ComponentPlacementMode>(choice, out var mode))
        {
            Plan.PickNewHostMode = mode;
        }
    }

    private void OnActiveColumnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (ColumnTypePicker.SelectedItem is ColumnType type) Plan.ActiveColumnTypeId = type.Id;
    }

    /// <summary>
    /// How high a new column goes: up from this storey to the level chosen, or down from it -
    /// Revit's Height and Depth. Unconnected leaves it standing its own height.
    /// </summary>
    private void OnColumnPlacementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        Plan.ColumnGoesDown = (string?)ColumnDirectionPicker.SelectedItem == "Depth";
        Plan.ColumnTopLevelId = (ColumnLevelPicker.SelectedItem as Core.Datums.Level)?.Id;
    }

    private void OnColumnAttachmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (ColumnAttachmentStylePicker.SelectedItem is string choice &&
            EnumText.TryParse<ColumnAttachmentStyle>(choice, out var style))
        {
            Plan.ColumnAttachmentStyle = style;
        }
    }

    private void OnColumnAttachmentOffsetChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (ParameterFormatter.TryParse(ParameterDataType.Length, ColumnAttachmentOffsetBox.Text, out var value) &&
            value is double millimetres)
        {
            Plan.ColumnAttachmentOffset = millimetres;
        }

        // Rewritten in the app.s own format, so what the box says is what it will do.
        ColumnAttachmentOffsetBox.Text = ParameterFormatter.Format(ParameterDataType.Length, Plan.ColumnAttachmentOffset);
    }

    private void OnColumnsAtGrids(object sender, RoutedEventArgs e)
    {
        Plan.PlaceColumnsAtGrids();
        Plan.Focus();
    }

    private void OnActiveComponentTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (ComponentTypePicker.SelectedItem is ComponentFamilyChoice choice) Plan.ActiveComponentTypeId = choice.Type.Id;
    }

    /// <summary>
    /// A component family as the options bar lists it: the category first, then the family, so
    /// the list reads the way Revit's type selector groups them.
    /// </summary>
    private sealed record ComponentFamilyChoice(ComponentType Type)
    {
        public override string ToString() => $"{EnumText.Humanise(Type.Kind)} : {Type.Name}";
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
        if (_loadingOptions || Plan is null || SelectedTypePicker.SelectedItem is not ElementType type) return;

        // Every selected element that can take this type gets it: picking a type for four
        // selected walls should not change only the last of them.
        var targets = Plan.SelectedElements.Where(selected => selected.Category == type.Category).ToList();
        if (targets.All(target => target.TypeId == type.Id)) return;

        _history.Execute(new SetElementsTypeCommand(targets, type));
        AfterHistoryChange();
    }

    // ---- attaching walls ---------------------------------------------------------------

    private void OnAttachTops(object sender, RoutedEventArgs e)
    {
        if (!AttachSelectedColumns(top: true)) AttachSelectedWalls(top: true);
    }

    private void OnAttachBases(object sender, RoutedEventArgs e)
    {
        if (!AttachSelectedColumns(top: false)) AttachSelectedWalls(top: false);
    }

    /// <summary>
    /// Attaches the selected columns to the slab over them, or the floor under them, exactly as
    /// a wall is attached: a column that meets a roof should follow the roof, not be retyped
    /// every time the roof moves. Returns whether any column was selected to act on.
    /// </summary>
    private bool AttachSelectedColumns(bool top) => Plan.BeginAttachColumns(top);

    /// <summary>
    /// Attaches each selected wall to the slab over it, or the floor under it. The slab is found
    /// rather than picked, because the one over a wall is usually on another storey, where the
    /// plan being worked in cannot show it.
    /// </summary>
    private void AttachSelectedWalls(bool top)
    {
        var walls = SelectedWallsOrHosts();
        if (walls.Count == 0)
        {
            StatusHint.Text = "Select the walls to attach first.";
            return;
        }

        var changes = walls
            .Select(wall => (Wall: wall, Top: top, Target: AttachmentTarget(wall, top)))
            .Where(change => change.Target is not null)
            .Select(change => (change.Wall, change.Top, change.Target))
            .ToList();

        if (changes.Count == 0)
        {
            StatusHint.Text = top
                ? "There is no floor, ceiling, roof or wall over those walls to attach to."
                : "There is no floor or wall under those walls to stand them on.";
            return;
        }

        _history.Execute(new AttachWallsCommand(changes, top ? "Attach Wall Tops" : "Attach Wall Bases"));
        AfterHistoryChange();

        StatusHint.Text = changes.Count == walls.Count
            ? $"{Plural(changes.Count, "wall")} attached."
            : $"{changes.Count} of {walls.Count} walls attached; the rest have nothing {(top ? "over" : "under")} them.";
    }

    /// <summary>
    /// What a wall's top or base attaches to: the nearer of the slab and the wall over it (or
    /// under it). A wall already attached back to this one is passed over, so two walls are
    /// never left holding each other up.
    /// </summary>
    private Guid? AttachmentTarget(Wall wall, bool top)
    {
        if (top)
        {
            var slab = WallAttachments.SlabAbove(_document, wall);
            var above = WallAttachments.WallAbove(_document, wall);
            if (above is not null && above.BaseAttachedTo == wall.Id) above = null;

            if (above is null) return slab?.Id;
            if (slab is null) return above.Id;
            return above.GetBaseElevation(_document) < slab.GetBottomElevation(_document) ? above.Id : slab.Id;
        }

        var floor = WallAttachments.FloorBelow(_document, wall);
        var below = WallAttachments.WallBelow(_document, wall);
        if (below is not null && below.TopAttachedTo == wall.Id) below = null;

        if (below is null) return floor?.Id;
        if (floor is null) return below.Id;
        return below.GetTopElevation(_document) > floor.GetTopElevation(_document) ? below.Id : floor.Id;
    }

    private void OnDetachWalls(object sender, RoutedEventArgs e)
    {
        var columns = Plan.SelectedElements.OfType<Column>()
            .SelectMany(column => new[]
            {
                (Column: column, Top: true, Attached: column.TopAttachedTo),
                (Column: column, Top: false, Attached: column.BaseAttachedTo)
            })
            .Where(change => change.Attached is not null)
            .Select(change => (change.Column, change.Top, (Guid?)null))
            .ToList();

        if (columns.Count > 0)
        {
            _history.Execute(new AttachColumnsCommand(columns, "Detach Columns"));
            AfterHistoryChange();
            StatusHint.Text = "Detached. The columns keep their own constraints again.";
            return;
        }

        var changes = SelectedWallsOrHosts()
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

    /// <summary>
    /// Runs the selected walls flush with the face of whatever they meet end to end, rather
    /// than standing in the middle of a thicker wall. Clicking again goes to the other face,
    /// because once a wall is flush with one there is only the other left to line up with.
    /// </summary>
    private void OnAlignWallFaces(object sender, RoutedEventArgs e)
    {
        if (AlignSelectedColumns()) return;

        var changes = Plan.SelectedElements.OfType<Wall>()
            .Select(wall => (Wall: wall, Offset:
                WallAlignment.OffsetFor(_document, wall, WallAlignment.Face.Interior)
                ?? WallAlignment.OffsetFor(_document, wall, WallAlignment.Face.Exterior)))
            .Where(change => change.Offset is not null)
            .Select(change => (change.Wall, change.Offset!.Value))
            .ToList();

        if (changes.Count == 0)
        {
            StatusHint.Text = "Nothing to line up: the selected walls meet nothing end to end, or are flush already.";
            return;
        }

        _history.Execute(new OffsetWallsCommand(changes));
        AfterHistoryChange();
        StatusHint.Text = "Flush with the wall it meets. Click again to line up with its other face.";
    }

    /// <summary>
    /// Sets the selected columns flush with a face of the wall they are on, moving them to the
    /// other face when they are already flush with one - so pressing it again puts a pier on
    /// the other side, which is the same thing Align Faces does for a wall.
    /// Returns whether any column was selected to act on.
    /// </summary>
    private bool AlignSelectedColumns()
    {
        var columns = Plan.SelectedElements.OfType<Column>().ToList();
        if (columns.Count == 0) return false;

        var moves = new List<(Column Column, Point2D To)>();

        foreach (var column in columns)
        {
            if (_document.FindType<ColumnType>(column.TypeId) is not { } type) continue;

            // The face it is not already on, so pressing it again moves it across.
            var flush = ColumnJoins.FlushWith(_document, column, type, ColumnJoins.Face.Interior)
                        ?? ColumnJoins.FlushWith(_document, column, type, ColumnJoins.Face.Exterior);

            if (flush is { } to) moves.Add((column, to));
        }

        if (moves.Count == 0)
        {
            StatusHint.Text = "Nothing to line up: those columns are not on a wall, or are flush already.";
            return true;
        }

        _history.Execute(new CompositeCommand(
            "Align Columns",
            moves.Select(move => (IUndoableCommand)new MoveElementsCommand(
                new[] { move.Column }, move.To - move.Column.Location))));

        AfterHistoryChange();
        StatusHint.Text = "Flush with the wall face. Click again to put it against the other face.";
        return true;
    }

    /// <summary>
    /// Opens the column editor on the selected column's type: the section is drawn there, and
    /// saving it gives the type that section, so every column of that type takes it at once.
    /// </summary>
    private void OnEditColumnProfile(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements.OfType<Column>().ToList() is not [var column])
        {
            StatusHint.Text = "Select one column to draw its section.";
            return;
        }

        if (_document.FindType<ColumnType>(column.TypeId) is not { } type)
        {
            StatusHint.Text = "That column has no type to draw a section for.";
            return;
        }

        var editor = new ColumnEditorWindow(_document, type) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } profile) return;

        _history.Execute(new SetColumnProfileCommand(type, profile, editor.Shaping));
        AfterHistoryChange();

        var (width, depth) = profile.Extent;
        StatusHint.Text =
            $"{type.Name} is now {ParameterFormatter.Format(ParameterDataType.Length, width)} by " +
            $"{ParameterFormatter.Format(ParameterDataType.Length, depth)}. " +
            $"{Plural(_document.Elements.OfType<Column>().Count(c => c.TypeId == type.Id), "column")} changed with it.";
    }

    /// <summary>
    /// The walls selected - and for a curtain panel, the curtain wall it is part of: a panel is
    /// what a click on the glass picks, and it is the wall that has a grid and a top to attach.
    /// </summary>
    private List<Wall> SelectedWallsOrHosts() => Plan.SelectedElements
        .Select(element => element switch
        {
            Wall wall => wall,
            CurtainPanel panel => _document.Walls.FirstOrDefault(wall => wall.Id == panel.HostWallId),
            _ => null
        })
        .OfType<Wall>()
        .Distinct()
        .ToList();

    /// <summary>
    /// Makes one wall of walls running on from each other in a straight line, of one type: the
    /// selected ones, or with one selected - or a panel of one - that one and whatever runs on
    /// from its ends. A glass gable split in two becomes one sheet of glass, the joint gone.
    /// </summary>
    private void OnMergeWalls(object sender, RoutedEventArgs e)
    {
        var walls = SelectedWallsOrHosts();
        if (walls.Count == 0)
        {
            StatusHint.Text = "Select the walls to merge - or one of them, to merge it with the walls running on from its ends.";
            return;
        }

        var keep = walls[0];
        var pool = walls.Count == 1 ? _document.Walls.ToList() : walls;
        var commands = new List<IUndoableCommand>();

        // One at a time, each taken into the kept wall, which grows as it goes.
        while (pool.FirstOrDefault(other => WallCorners.CanMerge(keep, other) && WallCorners.InLine(keep, other)) is { } next)
        {
            var merge = new MergeWallsCommand(_document, keep, next);
            merge.Redo();
            commands.Add(merge);
            pool.Remove(next);
        }

        if (commands.Count == 0)
        {
            StatusHint.Text = "Nothing to merge: walls become one only where they are of the same type and run on from each other in a straight line.";
            return;
        }

        _history.Record(commands.Count == 1 ? commands[0] : new CompositeCommand("Merge Walls", commands));
        Plan.Select(keep);
        AfterHistoryChange();
        StatusHint.Text = $"{commands.Count + 1} walls made one - the joint between them is gone.";
    }

    /// <summary>Curtain Wall panel: from a panel to the wall it is part of.</summary>
    private void OnSelectCurtainHost(object sender, RoutedEventArgs e)
    {
        if (SelectedWallsOrHosts() is not [var wall])
        {
            StatusHint.Text = "Select a curtain panel first.";
            return;
        }

        Plan.Select(wall);
        StatusHint.Text = "Curtain wall selected: change its type or height in Properties, edit its grid, or attach its top to the roof over it.";
    }

    /// <summary>
    /// Spandrels at Floors, for the selected curtain walls: a transom at the top and the underside
    /// of each floor behind them, and spandrel panels between - all of them one step to undo.
    /// </summary>
    private void OnSpandrelsAtFloors(object sender, RoutedEventArgs e)
    {
        var walls = Plan.SelectedElements.OfType<Wall>().Where(_document.IsCurtainWall).ToList();
        if (walls.Count == 0)
        {
            StatusHint.Text = "Select the curtain walls to line up with the floors behind them.";
            return;
        }

        var commands = new List<IUndoableCommand>();
        var said = new List<string>();
        string? refused = null;
        foreach (var wall in walls)
        {
            if (CurtainSpandrels.Apply(_document, wall, out var message) is { } command)
            {
                commands.Add(command);
                if (message is not null) said.Add(message);
            }
            else
            {
                refused ??= message;
            }
        }

        if (commands.Count == 0)
        {
            MessageBox.Show(this, refused ?? "Nothing to line up with.", "Spandrels at Floors", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _history.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Spandrels at Floors", commands));
        AfterHistoryChange();
        StatusHint.Text = commands.Count == 1 ? said.FirstOrDefault() ?? "Spandrels at the floors." : $"Spandrels at the floors behind {commands.Count} curtain walls.";
    }

    private void OnEditCurtainGrid(object sender, RoutedEventArgs e)
    {
        if (SelectedWallsOrHosts() is not [var wall] || !_document.IsCurtainWall(wall))
        {
            StatusHint.Text = "Select one curtain wall to edit its grid.";
            return;
        }

        var dialog = new EditCurtainGridWindow(_document, wall) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // The windows added there are cut into the wall as elements of their own, so they are
        // part of the same one step as the grid.
        // Its doors hung to open to a side they have room to: not up into the soffit under the eaves.
        var commands = new List<IUndoableCommand>
        {
            new SetCurtainLayoutCommand(wall, dialog.ResultGrid, DoorSwing.Settled(_document, wall, dialog.ResultGrid, dialog.ResultPanels))
        };

        foreach (var (typeId, along, sill) in dialog.ResultWindows)
            commands.Add(new AddElementCommand(_document, new BIMDesigner.Core.Architecture.Window
            {
                TypeId = typeId,
                LevelId = wall.LevelId,
                HostWallId = wall.Id,
                DistanceAlongWall = along,
                SillHeight = sill,
                Mark = OpeningMarks.Next<BIMDesigner.Core.Architecture.Window>(_document)
            }, "Place Window"));

        _history.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Edit Curtain Grid", commands));
        AfterHistoryChange();
        StatusHint.Text = dialog.ResultWindows.Count > 0
            ? $"{dialog.ResultWindows.Count} window(s) cut into the wall. Select one to move or resize it."
            : dialog.ResultGrid is null
                ? "Panels set. The grid still follows the wall type."
                : "Grid and panels set on this wall.";
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

    private void OnLockCorners(object sender, RoutedEventArgs e)
    {
        Plan.ToggleCornerLocks(Plan.SelectedElements.OfType<Wall>().ToList());
        Plan.Focus();
    }

    private void OnTagOnPlacementChanged(object sender, RoutedEventArgs e) => Plan.TagOnPlacement = TagOnPlacementBox.IsChecked == true;

    private void OnPickNewHost(object sender, RoutedEventArgs e)
    {
        Plan.BeginPickNewHost();
        Plan.Focus();
    }

    private void OnResetProfile(object sender, RoutedEventArgs e)
    {
        var walls = Plan.SelectedElements.OfType<Wall>().Where(wall => wall.Profile is not null).ToList();
        if (walls.Count == 0)
        {
            StatusHint.Text = "None of the selected walls has an edited profile.";
            return;
        }

        _history.Execute(new CompositeCommand("Reset Profile", walls.Select(wall => new SetWallProfileCommand(wall, null))));
        AfterHistoryChange();
        StatusHint.Text = $"Profile reset on {Plural(walls.Count, "wall")}: each is its plain rectangle again.";
    }

    // ---- contextual tab ----------------------------------------------------------------

    /// <summary>The tab that was open before a selection brought up "Modify | ...", to go back to.</summary>
    private TabItem? _tabBeforeContext;

    /// <summary>
    /// Shows "Modify | Walls" (or doors, sweeps, ...) while something is selected, with only
    /// the panels that apply to it, and puts the ribbon back where it was when the selection
    /// is let go.
    /// </summary>
    private void RefreshContextTab(bool bringForward = true)
    {
        // A roof sketch has its own tab, and nothing else is being modified while it is open.
        if (Plan.IsSketching)
        {
            ContextTab.Visibility = Visibility.Collapsed;
            return;
        }

        // While walls are being placed, the Place Wall tab is the one to have: the walls just
        // placed are selected, but it is not them being edited.
        if (Plan.ActiveTool == PlanTool.Wall)
        {
            ContextTab.Visibility = Visibility.Collapsed;
            return;
        }

        var selected = Plan.SelectedElements;

        if (selected.Count == 0)
        {
            ModifyCaption.Text = "Modify";
            if (ContextTab.Visibility != Visibility.Visible) return;

            var wasOpen = ReferenceEquals(Ribbon.SelectedItem, ContextTab);
            ContextTab.Visibility = Visibility.Collapsed;
            if (wasOpen) Ribbon.SelectedItem = _tabBeforeContext ?? Ribbon.Items[0];
            return;
        }

        var categories = selected.Select(element => element.Category).Distinct().ToList();
        ContextTab.Header = "Modify | " + (categories is [var only] ? CategoryTitle(only) : "Multi-Select");
        ModifyCaption.Text = selected.Count > 1 ? $"{ContextTab.Header}  ({selected.Count})" : (string)ContextTab.Header;

        var allWalls = selected.All(element => element is Wall);
        var wallsOnly = allWalls ? Visibility.Visible : Visibility.Collapsed;
        ContextSplit.Visibility = ContextSplitGap.Visibility = ContextTrim.Visibility = ContextOffset.Visibility = wallsOnly;
        ContextScale.IsEnabled = selected.Any(ElementTransforms.CanScale);
        ContextPin.Visibility = selected.Any(element => !element.Pinned) ? Visibility.Visible : Visibility.Collapsed;
        ContextUnpin.Visibility = selected.Any(element => element.Pinned) ? Visibility.Visible : Visibility.Collapsed;
        ContextModePanel.Visibility = ContextWallPanel.Visibility = wallsOnly;
        ContextShapePanel.Visibility = wallsOnly;
        ContextAddPoint.IsEnabled = allWalls;
        ContextStraighten.IsEnabled = selected.OfType<Wall>().Any(wall => wall.IsCurved);
        ContextAddShapes.IsEnabled = selected.Count >= 3 && selected.All(element => element is Wall);
        ContextAddPoint.IsChecked = Plan.AddingWallPoints;
        ContextResetProfile.IsEnabled = selected.OfType<Wall>().Any(wall => wall.Profile is not null);
        ContextCurtainGrid.Visibility = selected is [Wall one] && _document.IsCurtainWall(one) ? Visibility.Visible : Visibility.Collapsed;
        ContextSpandrels.Visibility = selected.Count > 0 && selected.All(element => element is Wall wall && _document.IsCurtainWall(wall))
            ? Visibility.Visible : Visibility.Collapsed;
        ContextSweepPanel.Visibility = selected is [PlacedSweep] ? Visibility.Visible : Visibility.Collapsed;
        ContextCurtainPanel.Visibility = selected.Count > 0 && selected.All(element => element is CurtainPanel) ? Visibility.Visible : Visibility.Collapsed;
        // A roof on its own, or a whole dormer - its roof and walls - picked as one.
        var dormerPicked = selected.OfType<Roof>().ToList() is [var dormerRoof] && Dormers.Of(_document, dormerRoof) is { } dormer &&
                           Dormers.Parts(_document, dormer) is var parts && parts.Count == selected.Count && parts.All(selected.Contains);
        ContextRoofPanel.Visibility = selected is [Roof] or [Gutter] || dormerPicked ? Visibility.Visible : Visibility.Collapsed;
        ContextAddDownpipes.Visibility = selected is [Roof { IsExtrusion: false }] or [Gutter] || dormerPicked ? Visibility.Visible : Visibility.Collapsed;
        ContextAddDormer.Visibility = ContextDormerOpening.Visibility = selected is [Roof] ? Visibility.Visible : Visibility.Collapsed;
        ContextEditFootprint.Visibility = selected is [Roof { IsExtrusion: false }] ? Visibility.Visible : Visibility.Collapsed;
        ContextEditProfile.Visibility = selected is [Roof { IsExtrusion: true }] ? Visibility.Visible : Visibility.Collapsed;
        ContextAddFascia.Visibility = ContextAddGutters.Visibility = ContextAddSoffits.Visibility =
            selected is [Roof { IsExtrusion: false }] || dormerPicked ? Visibility.Visible : Visibility.Collapsed;
        ContextAddRoofWindow.Visibility = selected is [Roof { IsExtrusion: false }] ? Visibility.Visible : Visibility.Collapsed;
        ContextAddShaft.Visibility = ContextAddRoofWindow.Visibility;
        ContextAddRoofDrain.Visibility = selected is [Roof flatRoof] && RoofDrainage.CanDrain(_document, flatRoof) ? Visibility.Visible : Visibility.Collapsed;
        ContextColumnPanel.Visibility = selected.Count > 0 && selected.All(element => element is Column)
            ? Visibility.Visible : Visibility.Collapsed;
        // Pick New belongs to anything that is carried by something else: a door or window in
        // its wall, and a component on a face it could be moved off.
        var rehostable = selected is [Opening]
                         || (selected is [BIMDesigner.Core.Architecture.Component component]
                             && _document.FindType<ComponentType>(component.TypeId) is { IsHosted: true });

        ContextHostPanel.Visibility = rehostable ? Visibility.Visible : Visibility.Collapsed;
        ContextPropertiesPanel.Visibility = selected.Any(element => element.TypeId != Guid.Empty) ? Visibility.Visible : Visibility.Collapsed;
        SyncSweepEditControls();

        if (ContextTab.Visibility != Visibility.Visible)
        {
            _tabBeforeContext = Ribbon.SelectedItem as TabItem;
            ContextTab.Visibility = Visibility.Visible;
        }

        // Picking something brings its tab forward. While a tool is placing things (a sweep
        // selects each one it puts down) the tab holding that tool stays open instead.
        if (bringForward && Plan.ActiveTool == PlanTool.Select) Ribbon.SelectedItem = ContextTab;
    }

    /// <summary>"WallSweeps" to "Wall Sweeps": the category as a tab caption.</summary>
    private static string CategoryTitle(BuiltInCategory category) =>
        string.Concat(category.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));

    /// <summary>A Modify tool picked from the contextual tab: the same tool as on the Modify tab.</summary>
    private void OnContextTool(object sender, RoutedEventArgs e)
    {
        var command = (sender as Button)?.CommandParameter as string;

        // A curtain wall's panel is fixed in its bay, so there is no line to mirror it about:
        // mirroring a door panel means hanging it on the other side, as it does in place.
        if (command == "Mirror" && Plan.SelectedElements is [CurtainPanel panel])
        {
            Plan.FlipCurtainPanel(panel, hand: true);
            return;
        }

        RadioButton? tool = command switch
        {
            "Split" => SplitTool,
            "SplitGap" => SplitGapTool,
            "Rotate" => RotateTool,
            "Scale" => ScaleTool,
            "Paint" => PaintTool,
            "SplitFace" => SplitFaceTool,
            "Trim" => TrimTool,
            "Offset" => OffsetTool,
            "Mirror" => MirrorTool,
            "Array" => ArrayTool,
            _ => null
        };

        if (tool is not null) tool.IsChecked = true;
    }

    private void OnAddWallPoint(object sender, RoutedEventArgs e)
    {
        Plan.AddingWallPoints = ContextAddPoint.IsChecked == true;
        ContextAddPoint.IsChecked = Plan.AddingWallPoints;
        Plan.Focus();
    }

    // ---- add shapes ----------------------------------------------------------------

    /// <summary>Add Shapes: the shapes the selected walls can be put into, as a menu under the button.</summary>
    private void OnAddShapesButton(object sender, RoutedEventArgs e)
    {
        ShapesMenu.PlacementTarget = ContextAddShapes;
        ShapesMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ShapesMenu.IsOpen = true;
    }

    /// <summary>A rectangle or square takes four walls; a regular polygon any number from three.</summary>
    private void OnShapesMenuOpened(object sender, RoutedEventArgs e)
    {
        var walls = Plan.SelectedElements.Count(element => element is Wall);
        var onlyWalls = walls == Plan.SelectedElements.Count;

        ShapeRectangleItem.IsEnabled = ShapeSquareItem.IsEnabled = onlyWalls && walls == 4;
        ShapePolygonItem.IsEnabled = onlyWalls && walls >= 3;
        ShapePolygonItem.Header = onlyWalls && walls >= 3 ? $"Regular _Polygon - {WallPolygon.NameFor(walls)}" : "Regular _Polygon";
    }

    private void OnShapeRectangle(object sender, RoutedEventArgs e) => ShapeWalls(WallShapeKind.Rectangle);

    private void OnShapeSquare(object sender, RoutedEventArgs e) => ShapeWalls(WallShapeKind.Square);

    private void OnShapePolygon(object sender, RoutedEventArgs e) => ShapeWalls(WallShapeKind.Polygon);

    /// <summary>Puts the selected walls of a room into the shape chosen, at the size asked for.</summary>
    private void ShapeWalls(WallShapeKind kind)
    {
        var selected = Plan.SelectedElements;
        var walls = selected.OfType<Wall>().ToList();

        if (walls.Count != selected.Count)
        {
            StatusHint.Text = "Select only the walls of a room to give them a shape.";
            return;
        }

        string? problem;
        WallShapeWindow? window = null;

        if (kind == WallShapeKind.Polygon)
        {
            if (WallPolygon.Find(_document, walls, out problem) is { } polygon) window = new WallShapeWindow(polygon);
        }
        else if (WallRectangle.Find(_document, walls, out problem) is { } rectangle)
        {
            window = new WallShapeWindow(rectangle, square: kind == WallShapeKind.Square);
        }

        if (window is null)
        {
            StatusHint.Text = problem;
            return;
        }

        window.Owner = this;
        if (window.ShowDialog() != true || window.Command is not { } command) return;

        _history.Execute(command);
        AfterHistoryChange();

        StatusHint.Text = window.Made;
        Plan.Focus();
    }

    /// <summary>Straightens the selected walls: points, arcs and ellipses all go, the ends stay.</summary>
    private void OnStraightenWalls(object sender, RoutedEventArgs e)
    {
        var walls = Plan.SelectedElements.OfType<Wall>().Where(wall => wall.IsCurved).ToList();
        if (walls.Count == 0)
        {
            StatusHint.Text = "The selected walls are straight already.";
            return;
        }

        _history.Execute(new CompositeCommand("Straighten",
            walls.Select(wall => new ReshapeSplineWallCommand(wall, wall.Spline, null, "Straighten", straighten: true))));
        AfterHistoryChange();
        RefreshContextTab();
        StatusHint.Text = $"Straightened {Plural(walls.Count, "wall")}.";
    }

    private void OnAddRemoveSweepWalls(object sender, RoutedEventArgs e) =>
        ToggleSweepEdit(SweepEditMode.AddRemoveWalls, ContextAddRemoveWalls.IsChecked == true);

    private void OnModifySweepReturns(object sender, RoutedEventArgs e) =>
        ToggleSweepEdit(SweepEditMode.ModifyReturns, ContextModifyReturns.IsChecked == true);

    private void ToggleSweepEdit(SweepEditMode mode, bool on)
    {
        if (on) Plan.BeginSweepEdit(mode);
        else Plan.EndSweepEdit();

        SyncSweepEditControls();
        Plan.Focus();
    }

    /// <summary>The two sweep buttons stay pressed while their mode lasts; Modify Returns brings its options.</summary>
    private void SyncSweepEditControls()
    {
        ContextAddRemoveWalls.IsChecked = Plan.SweepEdit == SweepEditMode.AddRemoveWalls;
        ContextModifyReturns.IsChecked = Plan.SweepEdit == SweepEditMode.ModifyReturns;
        SweepReturnOptions.Visibility = Plan.SweepEdit == SweepEditMode.ModifyReturns ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSweepReturnChoiceChanged(object sender, RoutedEventArgs e) =>
        Plan.ReturnOnClick = ReturnChoice.IsChecked == true;

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

    /// <summary>What the wall tool can build: every layered, stacked and curtain wall type, by name.</summary>
    private List<ElementType> WallTypeChoices() =>
        _document.ElementTypes
            .Where(type => type is WallType or StackedWallType or CurtainWallType)
            .OrderBy(type => type.Name)
            .ToList();

    private void OnManageWallTypes(object sender, RoutedEventArgs e) => ShowWallTypes(null);

    private void OnEditSelectedType(object sender, RoutedEventArgs e)
    {
        // The category the Properties palette is showing, when the selection has several.
        var element = Plan.SelectedElements.LastOrDefault(selected => selected.Category == _propertyCategory) ?? Plan.SelectedElement;
        var type = element is null ? null : _document.FindType<ElementType>(element.TypeId);
        if (type is null or WallType or StackedWallType or CurtainWallType)
        {
            ShowWallTypes(type);
            return;
        }

        // Other types have no dialog of their own: their type parameters are edited in place.
        TypeParameterList.BringIntoView();
        StatusHint.Text = $"{type.Name}: edit its type parameters in the Properties panel. A change there applies to every one of this type.";
    }

    /// <summary>Edit Type for the selected wall, opened at its bands.</summary>
    private void OnWallBands(object sender, RoutedEventArgs e)
    {
        var wall = Plan.SelectedElements.OfType<Wall>().FirstOrDefault();
        if (wall is null || _document.FindType<ElementType>(wall.TypeId) is not WallType type)
        {
            StatusHint.Text = "Select a basic wall: stacked and curtain walls have no bands of their own.";
            return;
        }

        ShowWallTypes(type, atBands: true);
    }

    private void ShowWallTypes(ElementType? start, bool atBands = false)
    {
        var dialog = new WallTypesWindow(_document, _history, start) { Owner = this, OpenAtBands = atBands };

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

    /// <summary>Which category of a mixed selection the panel is showing, as picked from its list.</summary>
    private BuiltInCategory? _propertyCategory;

    /// <summary>One entry in the panel's category list: "Walls (2)".</summary>
    private sealed record PropertyCategoryChoice(BuiltInCategory Category, int Count)
    {
        public override string ToString() => $"{CategoryTitle(Category)} ({Count})";
    }

    /// <summary>
    /// Rebuilds the panel for whatever is selected, in the shape of Revit's Properties palette:
    /// the type with its picture, the category of the selection with how many, then the
    /// parameters in folding groups. Nothing here knows about walls or doors specifically: an
    /// element reports its own parameters and the panel renders them, which is why a new
    /// category needs no new property-panel code.
    ///
    /// With several elements of a category selected, each row stands for all of them: an edit
    /// goes to every one, and a value they do not share is left blank.
    /// </summary>
    private void RefreshProperties()
    {
        NoSelectionHint.Visibility = Visibility.Collapsed;
        PropertyScroll.Visibility = Visibility.Visible;

        var selected = Plan.SelectedElements;
        if (selected.Count == 0)
        {
            _propertyDormer = null;
            ShowViewProperties();
            return;
        }

        // A whole dormer picked shows as the dormer - its roof, which carries its shape and size -
        // unless its walls have been asked for from the list since it was picked.
        var dormer = WholeDormer(selected);
        if (dormer is not null && !ReferenceEquals(dormer, _propertyDormer)) _propertyCategory = dormer.Category;
        _propertyDormer = dormer;

        var categories = selected
            .GroupBy(element => element.Category)
            .Select(group => new PropertyCategoryChoice(group.Key, group.Count()))
            .ToList();
        var category = categories.Any(c => c.Category == _propertyCategory) ? _propertyCategory!.Value : selected[^1].Category;
        _propertyCategory = category;

        var elements = selected.Where(element => element.Category == category).ToList();
        var element = elements[^1];

        _loadingOptions = true;
        PropertyCategoryPicker.ItemsSource = categories;
        PropertyCategoryPicker.SelectedItem = categories.First(c => c.Category == category);
        PropertyCategoryPicker.IsEnabled = true;
        _loadingOptions = false;

        var typeIds = elements.Select(e => e.TypeId).Distinct().ToList();
        var type = typeIds.Count == 1 ? _document.ElementTypes.FirstOrDefault(t => t.Id == typeIds[0]) : null;

        SelectedTypeImage.Source = TryFindResource(IconFor(element, type)) as System.Windows.Media.ImageSource;
        SelectedFamilyName.Text = elements.Count == 1 && element is Roof { Dormer: not null } ? "Dormer" : FamilyName(category, type);
        SelectedTypeName.Text = typeIds.Count > 1 ? "Multiple Types" : type?.Name ?? CategoryTitle(category);

        // "Doors" -> "door", so the hint reads naturally for whatever is selected.
        var noun = CategoryTitle(category).TrimEnd('s').ToLowerInvariant();
        TypeHint.Text = $"Shared by every {noun} of this type: editing one changes them all.";

        // Only types of the same category can be swapped in: a door cannot become a wall.
        var types = _document.ElementTypes
            .Where(candidate => candidate.Category == category)
            .OrderBy(candidate => candidate.Name)
            .ToList();
        _loadingOptions = true;
        SelectedTypePicker.ItemsSource = types;
        SelectedTypePicker.SelectedItem = type;
        SelectedTypePicker.Visibility = types.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _loadingOptions = false;

        InstanceParameterList.ItemsSource = BuildGroupedRows(
            elements.Select(e => (IReadOnlyList<ParameterValue>)e.GetInstanceParameters(_document).ToList()).ToList());

        // Type parameters when the selection shares one type; several types have no one set of them.
        var typeRows = type is null ? null : BuildGroupedRows(new[] { (IReadOnlyList<ParameterValue>)type.GetTypeParameters(_document).ToList() });
        TypeParameterList.ItemsSource = typeRows;
        TypeSection.Visibility = type is null ? Visibility.Collapsed : Visibility.Visible;
        EditTypeButton.IsEnabled = type is not null;

        // Walls and slabs are both layered build-ups, so both show their assembly.
        var layers = type switch
        {
            WallType wall => wall.Structure.Layers,
            SlabType slab => slab.Structure.Layers,
            _ => null
        };

        StructureSection.Visibility = layers is null ? Visibility.Collapsed : Visibility.Visible;
        StructureHint.Text = type is SlabType ? "Upper surface down." : "Exterior face to interior face.";
        LayerList.ItemsSource = layers?
            .Select(layer => new LayerRow(layer, _document))
            .ToList();
    }

    /// <summary>The dormer made by the Dormer tool a selection is the whole of, if it is.</summary>
    private Roof? WholeDormer(IReadOnlyList<Element> selected) =>
        selected.OfType<Roof>().Where(roof => roof.Dormer is not null).Take(2).ToList() is [var roof] &&
        Dormers.Parts(_document, roof).Select(part => part.Id).ToHashSet().SetEquals(selected.Select(element => element.Id))
            ? roof
            : null;

    /// <summary>The dormer the panel last showed whole, so a choice of its walls from the list is kept while it stays picked.</summary>
    private Roof? _propertyDormer;

    private void OnPropertyCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || PropertyCategoryPicker.SelectedItem is not PropertyCategoryChoice choice) return;

        _propertyCategory = choice.Category;
        RefreshProperties();
    }

    /// <summary>
    /// With nothing selected, the view's own properties, as Revit shows them: its scale and
    /// detail level for a plan, its visual style for the 3D view.
    /// </summary>
    private void ShowViewProperties()
    {
        var threeD = ModelCoversPlan;
        var levelName = _document.FindLevel(Plan.ActiveLevelId)?.Name ?? "Floor Plan";

        SelectedTypeImage.Source = TryFindResource(threeD ? "Icon.View3D" : "Icon.Plan") as System.Windows.Media.ImageSource;
        SelectedFamilyName.Text = threeD ? "3D View" : "Floor Plan";
        SelectedTypeName.Text = threeD ? "{3D}" : levelName;
        SelectedTypePicker.Visibility = Visibility.Collapsed;

        _loadingOptions = true;
        PropertyCategoryPicker.ItemsSource = new[] { threeD ? "3D View: {3D}" : $"Floor Plan: {levelName}" };
        PropertyCategoryPicker.SelectedIndex = 0;
        PropertyCategoryPicker.IsEnabled = false;
        _loadingOptions = false;

        EditTypeButton.IsEnabled = false;
        InstanceParameterList.ItemsSource = BuildGroupedRows(new[] { threeD ? ModelViewParameters() : PlanViewParameters(levelName) });
        TypeSection.Visibility = Visibility.Collapsed;
        StructureSection.Visibility = Visibility.Collapsed;
    }

    private static readonly ParameterDefinition ViewScaleParameter = new("View Scale", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition DetailLevelParameter = new("Detail Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition JoinDisplayParameter = new("Wall Join Display", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition UnderlayParameter = new("Show Storey Below", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition VisualStyleParameter = new("Visual Style", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition EdgesParameter = new("Show Edges", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition LevelParameter = new("Associated Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    private static readonly ParameterDefinition ViewNameParameter = new("View Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    private IReadOnlyList<ParameterValue> PlanViewParameters(string levelName)
    {
        var view = Plan.CurrentView;
        var scales = ViewScale.Common.Select(d => new ViewScale(d).ToString()).ToList();
        var details = EnumText.Choices<DetailLevel>();

        return new[]
        {
            ParameterValue.BindChoice(ViewScaleParameter,
                () => new ViewScale(_document.ViewSettings.ScaleOf(view)).ToString(),
                text => _document.ViewSettings.SetScale(view, ViewScale.Common[scales.IndexOf(text)]),
                scales),
            ParameterValue.BindChoice(DetailLevelParameter,
                () => EnumText.Humanise(Plan.DetailLevel),
                text => { if (EnumText.TryParse<DetailLevel>(text, out var level)) SetDetailLevel(level); },
                details),
            ParameterValue.BindChoice(JoinDisplayParameter,
                () => _document.ViewSettings.JoinDisplayOf(view) == WallJoinDisplay.CleanAllWallJoins ? "Clean all wall joins" : "Clean same type wall joins",
                text => { _document.ViewSettings.SetJoinDisplay(view, text == "Clean all wall joins" ? WallJoinDisplay.CleanAllWallJoins : WallJoinDisplay.CleanSameTypeWallJoins); Plan.RefreshModel(); },
                new[] { "Clean all wall joins", "Clean same type wall joins" }),
            ParameterValue.Bind(UnderlayParameter, () => Plan.ShowUnderlay, show =>
            {
                UnderlayMenuItem.IsChecked = PlanUnderlayToggle.IsChecked = show;
                Plan.ShowUnderlay = show;
            }),
            ParameterValue.ReadOnly(LevelParameter, () => levelName),
            ParameterValue.ReadOnly(ViewNameParameter, () => $"Floor Plan: {levelName}")
        };
    }

    private IReadOnlyList<ParameterValue> ModelViewParameters()
    {
        var styles = VisualStylePicker.Items.Cast<object>().Select(item => item.ToString() ?? string.Empty).ToList();

        return new[]
        {
            ParameterValue.BindChoice(VisualStyleParameter,
                () => VisualStylePicker.SelectedItem?.ToString() ?? string.Empty,
                text => VisualStylePicker.SelectedIndex = styles.IndexOf(text),
                styles),
            ParameterValue.Bind(EdgesParameter, () => ShowEdgesBox.IsChecked == true, show =>
            {
                ShowEdgesBox.IsChecked = show;
                OnModelCategoryToggled(ShowEdgesBox, new RoutedEventArgs());
            }),
            ParameterValue.ReadOnly(ViewNameParameter, () => "{3D}")
        };
    }

    /// <summary>The picture beside a type: the ribbon icon of what it is.</summary>
    private string IconFor(Element element, ElementType? type) => element switch
    {
        Wall wall when _document.IsCurtainWall(wall) => "Icon.CurtainWall",
        CurtainPanel => "Icon.CurtainWall",
        Wall => "Icon.Wall",
        Door => "Icon.Door",
        BIMDesigner.Core.Architecture.Window => "Icon.Window",
        Floor => "Icon.Floor",
        Ceiling => "Icon.Ceiling",
        Roof => "Icon.Roof",
        Room => "Icon.Room",
        BIMDesigner.Core.Datums.Grid => "Icon.Grid",
        BIMDesigner.Core.Annotation.Dimension => "Icon.Dimension",
        BIMDesigner.Core.Annotation.Tag => "Icon.Tag",
        BIMDesigner.Core.Annotation.TextNote => "Icon.Text",
        SectionMarker => "Icon.Section",
        PlacedSweep { Kind: SweepKind.Reveal } => "Icon.Reveal",
        PlacedSweep => "Icon.Sweep",
        Fascia => "Icon.Fascia",
        Gutter => "Icon.Gutter",
        Soffit => "Icon.Soffit",
        RoofWindow => "Icon.RoofWindow",
        ShaftOpening => "Icon.Shaft",
        Downpipe => "Icon.Downpipe",
        RoofDrain => "Icon.RoofDrain",
        Chimney => "Icon.Chimney",
        Part => "Icon.Parts",
        WallFraming => "Icon.Framing",
        _ => "Icon.Select"
    };

    /// <summary>What kind of thing the type is, the line above its name: "Basic Wall", "Curtain Wall".</summary>
    private static string FamilyName(BuiltInCategory category, ElementType? type) => type switch
    {
        CurtainWallType => "Curtain Wall",
        StackedWallType => "Stacked Wall",
        WallType => "Basic Wall",
        WallSweepType { Kind: SweepKind.Reveal } => "Reveal",
        FasciaType => "Fascia",
        GutterType => "Gutter",
        SoffitType => "Soffit",
        RoofWindowType => "Roof Window",
        ChimneyType => "Chimney",
        WallSweepType => "Wall Sweep",
        _ => category == BuiltInCategory.CurtainPanels ? "Curtain Panel" : CategoryTitle(category).TrimEnd('s')
    };

    /// <summary>
    /// Wraps parameters as editable rows grouped by their parameter group, which is what
    /// produces the "Constraints / Dimensions / Identity Data" sections in the panel. Each list
    /// is one element's parameters; a row is made for each parameter they all have, standing
    /// for all of them, and the last element's order is the one kept.
    /// </summary>
    private ICollectionView BuildGroupedRows(IReadOnlyList<IReadOnlyList<ParameterValue>> perElement)
    {
        var primary = perElement[^1];
        var others = perElement.Take(perElement.Count - 1).ToList();

        var rows = new List<ParameterRow>();
        foreach (var parameter in primary)
        {
            var same = others.Select(list => list.FirstOrDefault(p => p.Name == parameter.Name)).ToList();
            if (same.Any(p => p is null)) continue;

            var row = new ParameterRow(same.Prepend(parameter).OfType<ParameterValue>().ToList());
            row.ValueCommitted += OnParameterCommitted;
            row.Explained += (_, said) =>
            {
                StatusHint.Text = said.Message;

                // Refused, it is an error, and said so where it cannot be missed - once the edit
                // under way in the panel has finished.
                if (said.Refused)
                    Dispatcher.BeginInvoke(() => MessageBox.Show(this, said.Message, said.Parameter, MessageBoxButton.OK, MessageBoxImage.Warning));
            };
            rows.Add(row);
        }

        var view = new CollectionViewSource { Source = rows }.View;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ParameterRow.GroupName)));
        return view;
    }

    private void OnParameterCommitted(object? sender, ParameterCommittedEventArgs e)
    {
        var commands = e.Changes.Select(change => new ParameterChangeCommand(change.Parameter, change.OldValue, change.NewValue)).ToList();
        _history.Record(commands.Count == 1 ? commands[0] : new CompositeCommand(commands[0].Name, commands));

        Plan.RefreshModel();
        RefreshProjectBrowser();

        // A type edit - a door made sliding, a window made a bay - shows everywhere at once.
        RefreshSection();
        SheetSurface.Refresh();
        Refresh3D();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshSchedule);

        // Editing one parameter can change derived ones, so rebuild the whole panel - but
        // after this edit has finished, or the row being edited is destroyed mid-binding.
        Dispatcher.BeginInvoke(RefreshProperties);
    }

    /// <summary>A folding group opened or closed: remembered, so it stays that way from one selection to the next.</summary>
    private void OnPaletteGroupToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Content: string name } toggle) return;

        if (toggle.IsChecked == true) PaletteGroupState.Collapsed.Remove(name);
        else PaletteGroupState.Collapsed.Add(name);
    }

    // ---- project browser -------------------------------------------------------

    private void RefreshProjectBrowser()
    {
        PlanTitle.Text = _document.FindLevel(Plan.ActiveLevelId) is { } shown ? $"Floor Plan: {shown.Name}" : "Floor Plan";
        RefreshPlanScale();

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
                .Select(group => $"{group.Count()} {(group.Count() == 1 ? CategoryTitle(group.Key).TrimEnd('s') : CategoryTitle(group.Key)).ToLowerInvariant()}")
                .ToList();

            var isActive = level.Id == Plan.ActiveLevelId;

            var item = new TreeViewItem
            {
                Header = counts.Count == 0
                    ? $"{level.Name}  —  empty"
                    : $"{level.Name}  —  {string.Join(", ", counts)}",
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
                Header = $"Section {marker.Name}  —  {Units.FormatLength(marker.Length)}",
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

        // The four elevations, the building seen straight on from each side.
        var elevations = new TreeViewItem { Header = "Elevations", IsExpanded = true };
        foreach (var side in Elevations.All)
        {
            var item = new TreeViewItem { Header = Elevations.Name(side), Tag = side };
            item.MouseLeftButtonUp += (_, args) =>
            {
                ShowElevation(side);
                args.Handled = true;
            };
            elevations.Items.Add(item);
        }

        // The drawing set. Clicking a sheet opens it, the way clicking a plan switches storey.
        var sheets = new TreeViewItem { Header = "Sheets", IsExpanded = true };

        foreach (var sheet in _document.Elements.OfType<Sheet>()
                     .OrderBy(sheet => sheet.Number, StringComparer.OrdinalIgnoreCase))
        {
            var views = sheet.Viewports.Count == 1 ? "1 view" : $"{sheet.Viewports.Count} views";

            var item = new TreeViewItem
            {
                Header = $"{sheet.Number}  —  {sheet.Name}  ({views})",
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
        project.Items.Add(elevations);

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

        // The drawing is laid out beside whatever is already on the sheet rather than on top of it.
        var extent = ViewExtent.Of(_document, option.View).OrAtLeast(1000);

        var availableWidth = sheet.Width - TitleBlock.Width(sheet) - TitleBlock.Margin * 3;

        // Room is left under the drawing for its title and scale.
        var availableHeight = sheet.Height - TitleBlock.Margin * 2 - 16;

        // The view's own scale when the drawing fits the sheet at it, else the largest that does.
        var own = new ViewScale(_document.ViewSettings.ScaleOf(option.View));
        var scale = own.ToPaper(extent.Width) <= availableWidth && own.ToPaper(extent.Height) <= availableHeight
            ? own
            : ViewScale.FittingInto(extent.Width, extent.Height, availableWidth, availableHeight);

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

    private void Show3D() => ShowModel(null);

    /// <summary>
    /// Opens an elevation: the model straight on from one side, flat, with the levels and the
    /// gridlines facing it drawn across it. It pans and zooms; it does not turn.
    /// </summary>
    private void ShowElevation(ElevationSide side) => ShowModel(side);

    private void ShowModel(ElevationSide? elevation)
    {
        // The sheet and the 3D view both take over the drawing area; only one can.
        if (SheetPanel.Visibility == Visibility.Visible) OnCloseSheet(this, new RoutedEventArgs());

        Model3D.Elevation = elevation;
        ModelCaption.Text = elevation is { } side ? Elevations.Name(side) : "3D View";
        ModelHelp.Text = elevation is null
            ? "Drag to orbit  ·  right-drag to pan  ·  scroll to zoom  ·  click to select"
            : "Drag to pan  ·  scroll to zoom  ·  click to select";
        Cube.Visibility = elevation is null ? Visibility.Visible : Visibility.Collapsed;

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

        StatusHint.Text = elevation is { } shown
            ? $"{Elevations.Name(shown)}. Drag to pan, scroll to zoom, click to select."
            : "3D view. Drag to orbit, right-drag to pan, scroll to zoom, click to select.";
        RefreshProperties();
    }

    private void OnClose3D(object sender, RoutedEventArgs e)
    {
        ModelPanel.Visibility = Visibility.Collapsed;
        ModelMenuItem.IsChecked = false;
        if (_tiled) SetTiled(false);
        RefreshProperties();
        Plan.Focus();
    }

    // ---- the ribbon and the window layout ------------------------------------------------

    /// <summary>Whether the plan and the 3D view are side by side rather than one over the other.</summary>
    private bool _tiled;

    /// <summary>Whether the 3D view is open over the plan, hiding it.</summary>
    private bool ModelCoversPlan => ModelPanel.Visibility == Visibility.Visible && !_tiled;

    /// <summary>The File button opens its menu under itself, the way the File tab of a ribbon does.</summary>
    private void OnFileButton(object sender, RoutedEventArgs e)
    {
        if (FileButton.ContextMenu is not { } menu) return;

        menu.PlacementTarget = FileButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnSelectModify(object sender, RoutedEventArgs e) => BackToModify();

    private void OnQuickDimension(object sender, RoutedEventArgs e) => DimensionTool.IsChecked = true;

    private void OnQuickText(object sender, RoutedEventArgs e) => TextTool.IsChecked = true;

    private void OnQuickSection(object sender, RoutedEventArgs e) => SectionTool.IsChecked = true;

    private void OnQuickTile(object sender, RoutedEventArgs e) => SetTiled(!_tiled);

    private void OnToggleTile(object sender, RoutedEventArgs e) => SetTiled(TileToggle.IsChecked == true);

    /// <summary>
    /// Tiled, the 3D view takes a column of its own beside the plan, so a change drawn in plan
    /// can be seen in 3D as it is made; untiled, it covers the plan when open, as before.
    /// </summary>
    private void SetTiled(bool tiled)
    {
        _tiled = tiled;
        TileToggle.IsChecked = tiled;

        Grid.SetColumn(ModelPanel, tiled ? 2 : 0);
        TileColumn.Width = tiled ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TileSplitterColumn.Width = tiled ? new GridLength(4) : new GridLength(0);
        TileSplitter.Visibility = tiled ? Visibility.Visible : Visibility.Collapsed;

        if (tiled)
        {
            if (ModelPanel.Visibility != Visibility.Visible) Show3D();
            StatusHint.Text = "Plan and 3D side by side. Draw in the plan and watch the model follow.";
        }
        else if (ModelPanel.Visibility == Visibility.Visible)
        {
            OnClose3D(this, new RoutedEventArgs());
        }
    }

    /// <summary>Light or dark is remembered for next time: every colour is chosen as the window is built.</summary>
    private void OnToggleDarkTheme(object sender, RoutedEventArgs e)
    {
        var dark = DarkThemeToggle.IsChecked == true;
        AppTheme.Choose(light: !dark);
        MessageBox.Show(this,
            $"BIMDesigner will start with the {(dark ? "dark" : "light")} theme next time.",
            "Theme", MessageBoxButton.OK, MessageBoxImage.Information);
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
        Model3D.SetKindVisible(MeshKind.Wall, ShowWallsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Framing, ShowFramingBox.IsChecked == true);
        Model3D.ShowGround = ShowGroundBox.IsChecked == true;
        Model3D.ShowEdges = ShowEdgesBox.IsChecked == true;
    }

    private void OnReset3DView(object sender, RoutedEventArgs e) => Model3D.ResetView();

    private void OnZoom3DToFit(object sender, RoutedEventArgs e) => Model3D.ZoomToFit();

    private void OnZoom3DIn(object sender, RoutedEventArgs e) => Model3D.Zoom(0.8);

    private void OnZoom3DOut(object sender, RoutedEventArgs e) => Model3D.Zoom(1.25);

    // ---- the plan's view control bar ----------------------------------------------------

    /// <summary>Shows the scale of the plan now on screen, which each storey keeps for itself.</summary>
    private void RefreshPlanScale()
    {
        _loadingOptions = true;
        PlanScalePicker.ItemsSource ??= ViewScale.Common.Select(d => new ViewScale(d)).ToList();
        var scale = _document.ViewSettings.ScaleOf(Plan.CurrentView);
        PlanScalePicker.SelectedItem = ((IEnumerable<ViewScale>)PlanScalePicker.ItemsSource).FirstOrDefault(s => s.Denominator == scale);
        _loadingOptions = false;
    }

    private void OnPlanScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || PlanScalePicker.SelectedItem is not ViewScale scale) return;
        if (_document.ViewSettings.ScaleOf(Plan.CurrentView) == scale.Denominator) return;

        _history.Execute(new SetViewScaleCommand(_document, Plan.CurrentView, scale.Denominator));
        StatusHint.Text = $"This plan is drawn at {scale}: it goes on a sheet at that scale when it fits.";
    }

    private void OnPlanDetailChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || PlanDetailPicker.SelectedIndex < 0) return;
        SetDetailLevel((DetailLevel)PlanDetailPicker.SelectedIndex);
    }

    private void OnPlanUnderlayToggled(object sender, RoutedEventArgs e)
    {
        UnderlayMenuItem.IsChecked = PlanUnderlayToggle.IsChecked == true;
        OnToggleUnderlay(sender, e);
    }

    private void OnPlanZoomToFit(object sender, RoutedEventArgs e) => Plan.ZoomToFit();

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

        var shape = WallShapePicker.SelectedItem is ComboBoxItem { Tag: string name } && Enum.TryParse<WallShape>(name, out var picked)
            ? picked
            : WallShape.Line;
        Plan.DrawShape = shape;
        PolygonOptions.Visibility = shape == WallShape.Polygon ? Visibility.Visible : Visibility.Collapsed;
        TrapezoidOptions.Visibility = shape == WallShape.Trapezoid ? Visibility.Visible : Visibility.Collapsed;
        if (shape == WallShape.Trapezoid && TrapezoidSidePicker.ItemsSource is null)
        {
            TrapezoidSidePicker.ItemsSource = EnumText.Choices<TrapezoidSide>();
            TrapezoidSidePicker.SelectedItem = EnumText.Humanise(Plan.TrapezoidSide);
        }
        SyncWallShapeButtons();

        StatusHint.Text = shape switch
        {
            WallShape.Arc => "Arc: click the start, the end, then a point the arc passes through.",
            WallShape.TangentArc => "Tangent arc: click the end of a wall, then where the arc ends. It carries on smoothly, and the next one from it.",
            WallShape.CentreEndsArc => "Centre-ends arc: click the centre, then the start - which sets the radius - then where it ends.",
            WallShape.FilletArc => "Fillet arc: click a wall, then the wall it meets: the corner is rounded off at the Radius on the bar.",
            WallShape.Trapezoid => "Trapezoid wall: click its start, then its end. It is as thick as Start there and End here, one material through.",
            WallShape.PolygonOutline => "Polygon wall: click its corners round, then the first again, a double click or Enter. One material through.",
            WallShape.Rectangle => "Rectangle: click one corner, then the opposite one. Hold Shift for a square.",
            WallShape.Polygon => "Polygon: click the centre, then a corner. Set the number of sides on the bar.",
            WallShape.Circle => "Circle: click the centre, then a point on the circle.",
            WallShape.Oval => "Oval: click one corner of its box, then the opposite one. Hold Shift for a circle.",
            WallShape.Ellipse => "Ellipse: click one corner of its box, then the opposite one. Hold Shift for a circle.",
            WallShape.PartialEllipse => "Partial ellipse: click one end of an axis, the other end, then a point the ellipse passes through.",
            WallShape.Pick => "Pick lines: click a gridline to put a wall along it. The offset moves it toward the side you click.",
            WallShape.Spline => "Spline: click the start, then points the wall curves through. Enter or a double click finishes; click the first point to close a loop.",
            WallShape.Freehand => "Freehand: hold the mouse button and draw the wall. Let go to build it; end where you began to close a loop.",
            WallShape.BySegment => "By segment: click beside a wall, on the side the new wall goes. It runs the length of that face, between the walls it meets.",
            WallShape.ByRoom => "By room: click inside a room. A wall goes along every face round it, meeting at the corners.",
            _ => "Click the start of the wall, then its end."
        };
    }

    /// <summary>The size of the next wall opening, from the option bar; an entry that is not a length is put back.</summary>
    private void OnOpeningSizeChanged(object sender, RoutedEventArgs e)
    {
        double Read(TextBox box, double current, bool mayBeZero)
        {
            var ok = ParameterFormatter.TryParse(ParameterDataType.Length, box.Text, out var value) && value is double length && (mayBeZero ? length >= 0 : length > 0);
            var result = ok ? (double)value! : current;
            box.Text = Units.FormatLength(result);
            return result;
        }

        Plan.NewOpeningWidth = Read(OpeningWidthBox, Plan.NewOpeningWidth, false);
        Plan.NewOpeningHeight = Read(OpeningHeightBox, Plan.NewOpeningHeight, false);
        Plan.NewOpeningSill = Read(OpeningSillBox, Plan.NewOpeningSill, true);
    }

    // ---- the Wall Joins tool's option bar -------------------------------------------------

    /// <summary>Shows the picked junctions' settings: their join, its order, how it is cleaned, whether it is allowed.</summary>
    private void RefreshJunctionOptions()
    {
        var picked = Plan.SelectedJunctions;
        var any = picked.Count > 0;
        foreach (var control in new Control[] { JunctionButt, JunctionMitre, JunctionSquareOff, JunctionDisplayPicker, JunctionAllow, JunctionDisallow })
            control.IsEnabled = any;
        JunctionPrevious.IsEnabled = JunctionNext.IsEnabled = Plan.CanCycleJunctionOrder;

        if (!any) return;

        var point = picked[0];
        var level = Plan.ActiveLevelId;
        var type = WallJunctions.TypeOf(_document, level, point);

        _loadingOptions = true;
        JunctionButt.IsChecked = type == JunctionType.Butt;
        JunctionMitre.IsChecked = type == JunctionType.Mitre;
        JunctionSquareOff.IsChecked = type == JunctionType.SquareOff;
        JunctionDisplayPicker.SelectedIndex = (int)WallJunctions.CleanupOf(_document, level, point);
        var disallowed = WallJunctions.IsDisallowed(_document, level, point);
        JunctionAllow.IsChecked = !disallowed;
        JunctionDisallow.IsChecked = disallowed;
        _loadingOptions = false;

        StatusHint.Text = picked.Count == 1
            ? $"{WallJunctions.Ends(_document, level, point).Count} wall end{(WallJunctions.Ends(_document, level, point).Count == 1 ? "" : "s")} meet here." +
              (Plan.CanCycleJunctionOrder ? " Previous and Next change which wall carries on." : string.Empty)
            : $"{picked.Count} joins picked: changes go to all of them.";
    }

    private void OnJunctionTypeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        Plan.SetJunctionType(ReferenceEquals(sender, JunctionMitre) ? JunctionType.Mitre
            : ReferenceEquals(sender, JunctionSquareOff) ? JunctionType.SquareOff
            : JunctionType.Butt);
        AfterJunctionEdit();
    }

    private void OnJunctionPrevious(object sender, RoutedEventArgs e)
    {
        Plan.CycleJunctionOrder(-1);
        AfterJunctionEdit();
    }

    private void OnJunctionNext(object sender, RoutedEventArgs e)
    {
        Plan.CycleJunctionOrder(1);
        AfterJunctionEdit();
    }

    private void OnJunctionDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null || JunctionDisplayPicker.SelectedIndex < 0) return;
        Plan.SetJunctionCleanup((WallJoinCleanup)JunctionDisplayPicker.SelectedIndex);
        AfterJunctionEdit();
    }

    private void OnJunctionAllowedChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        Plan.SetJunctionsAllowed(ReferenceEquals(sender, JunctionAllow));
        AfterJunctionEdit();
    }

    /// <summary>A join changed: every view shows it, and the plan keeps the keyboard.</summary>
    private void AfterJunctionEdit()
    {
        AfterHistoryChange();
        Plan.Focus();
    }

    // ---- the Place Wall tab --------------------------------------------------------------

    /// <summary>The tab that was open before the Wall tool brought up Place Wall, to go back to.</summary>
    private TabItem? _tabBeforePlaceWall;

    /// <summary>
    /// Shows "Modify | Place Wall" while the Wall tool is on, as Revit does: the shapes to draw
    /// with, placing by segment or by room, and joining - all at hand rather than at the far end
    /// of the option bar.
    /// </summary>
    private void RefreshPlaceWallTab()
    {
        if (Plan.ActiveTool == PlanTool.Wall)
        {
            if (PlaceWallTab.Visibility != Visibility.Visible)
            {
                _tabBeforePlaceWall = Ribbon.SelectedItem as TabItem;
                PlaceWallTab.Visibility = Visibility.Visible;
            }

            ContextTab.Visibility = Visibility.Collapsed;
            Ribbon.SelectedItem = PlaceWallTab;
            SyncWallShapeButtons();
            return;
        }

        if (PlaceWallTab.Visibility != Visibility.Visible) return;

        var wasOpen = ReferenceEquals(Ribbon.SelectedItem, PlaceWallTab);
        PlaceWallTab.Visibility = Visibility.Collapsed;
        if (wasOpen)
            Ribbon.SelectedItem = _tabBeforePlaceWall is { Visibility: Visibility.Visible } before && !ReferenceEquals(before, PlaceWallTab)
                ? before
                : Ribbon.Items[0];
    }

    /// <summary>
    /// The list under the arrow beside Wall: every way of placing walls in one place - the
    /// shapes, by segment or by room, sweeps and reveals, and joining - each ticked when on.
    /// </summary>
    private void OnWallMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = WallMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var drawing = Plan.ActiveTool == PlanTool.Wall;

        MenuItem Item(string header, string icon, string tag, bool ticked, string? hint = null)
        {
            var item = new MenuItem
            {
                Header = header,
                Tag = tag,
                IsChecked = ticked,
                ToolTip = hint,
                Icon = new Image { Source = TryFindResource(icon) as System.Windows.Media.ImageSource, Width = 16, Height = 16 }
            };
            item.Click += OnWallMenuItem;
            return item;
        }

        foreach (var (header, icon, shape) in new[]
        {
            ("Line", "Icon.DrawLine", WallShape.Line), ("Arc", "Icon.DrawArc", WallShape.Arc),
            ("Tangent Arc", "Icon.DrawTangentArc", WallShape.TangentArc), ("Centre-Ends Arc", "Icon.DrawCentreArc", WallShape.CentreEndsArc),
            ("Fillet Arc", "Icon.DrawFilletArc", WallShape.FilletArc),
            ("Trapezoid Wall", "Icon.DrawTrapezoid", WallShape.Trapezoid), ("Polygon Wall", "Icon.DrawPolygonWall", WallShape.PolygonOutline),
            ("Rectangle", "Icon.DrawRectangle", WallShape.Rectangle), ("Polygon", "Icon.DrawPolygon", WallShape.Polygon),
            ("Circle", "Icon.DrawCircle", WallShape.Circle), ("Oval", "Icon.DrawOval", WallShape.Oval),
            ("Ellipse", "Icon.DrawEllipse", WallShape.Ellipse), ("Partial Ellipse", "Icon.DrawHalfEllipse", WallShape.PartialEllipse),
            ("Spline", "Icon.DrawSpline", WallShape.Spline), ("Freehand", "Icon.DrawFreehand", WallShape.Freehand),
            ("Pick Lines", "Icon.DrawPick", WallShape.Pick)
        })
        {
            menu.Items.Add(Item(header, icon, shape.ToString(), drawing && Plan.DrawShape == shape));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Place by Segment", "Icon.PlaceBySegment", nameof(WallShape.BySegment), drawing && Plan.DrawShape == WallShape.BySegment,
            "Click beside a wall: a new wall runs along that face"));
        menu.Items.Add(Item("Place by Room", "Icon.PlaceByRoom", nameof(WallShape.ByRoom), drawing && Plan.DrawShape == WallShape.ByRoom,
            "Click inside a room: a new wall along every face round it"));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Wall: Sweep", "Icon.Sweep", "Sweep", Plan.ActiveTool == PlanTool.Sweep));
        menu.Items.Add(Item("Wall: Reveal", "Icon.Reveal", "Reveal", Plan.ActiveTool == PlanTool.Reveal));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Auto Join", "Icon.AutoJoin", "AutoJoin", Plan.AutoJoinWalls,
            "Join new walls to the walls they lie against: doors and windows cut through both"));
        var lockItem = Item("Auto Join and Lock", "Icon.Lock", "Lock", Plan.LockJoinedWalls, "Joined walls also move together");
        menu.Items.Add(lockItem);

        menu.IsOpen = true;
    }

    private void OnWallMenuItem(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;

        switch (tag)
        {
            case "Sweep":
                SweepTool.IsChecked = true;
                return;

            case "Reveal":
                RevealTool.IsChecked = true;
                return;

            case "AutoJoin":
                AutoJoinBox.IsChecked = !Plan.AutoJoinWalls;
                OnAutoJoinChanged(sender, e);
                return;

            case "Lock":
                LockJoinBox.IsChecked = !Plan.LockJoinedWalls;
                if (LockJoinBox.IsChecked == true) AutoJoinBox.IsChecked = true;
                OnAutoJoinChanged(sender, e);
                return;
        }

        if (!Enum.TryParse<WallShape>(tag, out var shape)) return;

        WallTool.IsChecked = true;
        SelectWallShape(shape);
        Plan.Focus();
    }

    /// <summary>Picks a shape in the options bar's list, which sets it for drawing.</summary>
    private void SelectWallShape(WallShape shape) =>
        WallShapePicker.SelectedItem = WallShapePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == shape.ToString());

    private void OnTrapezoidOptionChanged(object sender, RoutedEventArgs e)
    {
        if (Plan is null) return;

        static double? Read(string text) =>
            ParameterFormatter.TryParse(ParameterDataType.Length, text, out var value) && value is double mm && mm >= 10 && mm <= 10000 ? mm : null;

        if (Read(TrapezoidStartBox.Text) is { } start) Plan.TrapezoidStartThickness = start;
        if (Read(TrapezoidEndBox.Text) is { } end) Plan.TrapezoidEndThickness = end;
        if (TrapezoidSidePicker.SelectedItem is string side && EnumText.TryParse<TrapezoidSide>(side, out var straight)) Plan.TrapezoidSide = straight;
        TrapezoidStartBox.Text = Units.FormatLength(Plan.TrapezoidStartThickness);
        TrapezoidEndBox.Text = Units.FormatLength(Plan.TrapezoidEndThickness);
    }

    private void OnArcRadiusChanged(object sender, RoutedEventArgs e)
    {
        if (ParameterFormatter.TryParse(ParameterDataType.Length, ArcRadiusBox.Text, out var value) && value is double radius && radius >= 10 && radius <= 1e6)
            Plan.ArcRadius = radius;

        ArcRadiusBox.Text = Units.FormatLength(Plan.ArcRadius);
        Plan.ChainRadius = ChainRadiusBox.IsChecked == true;
    }

    private void OnArcRadiusKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnArcRadiusChanged(sender, e);
        Plan.Focus();
        e.Handled = true;
    }

    private IEnumerable<RadioButton> WallShapeButtons() => new[]
    {
        ShapeLine, ShapeArc, ShapeTangentArc, ShapeCentreEndsArc, ShapeFilletArc, ShapeTrapezoid, ShapePolygonOutline, ShapePick, ShapeRectangle, ShapePolygon, ShapeCircle, ShapeOval, ShapeEllipse,
        ShapePartialEllipse, ShapeSpline, ShapeFreehand, ShapeBySegment, ShapeByRoom
    };

    /// <summary>The shape buttons on the tab show the shape the option bar has.</summary>
    private void SyncWallShapeButtons()
    {
        var shape = Plan.DrawShape.ToString();
        foreach (var button in WallShapeButtons()) button.IsChecked = (button.CommandParameter as string) == shape;
    }

    private void OnPlaceWallShape(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { CommandParameter: string name } && Enum.TryParse<WallShape>(name, out var shape))
            SelectWallShape(shape);

        Plan.Focus();
    }

    private void OnLockJoinChanged(object sender, RoutedEventArgs e) => OnAutoJoinChanged(LockJoinBox, e);

    private void OnPlaceWallJoinChanged(object sender, RoutedEventArgs e)
    {
        AutoJoinBox.IsChecked = PlaceAutoJoin.IsChecked == true || (ReferenceEquals(sender, PlaceLock) && PlaceLock.IsChecked == true);
        LockJoinBox.IsChecked = PlaceLock.IsChecked == true;
        OnAutoJoinChanged(sender, e);
        Plan.Focus();
    }

    /// <summary>Auto Join, and Lock with it: Lock means nothing unless walls are joined.</summary>
    private void OnAutoJoinChanged(object sender, RoutedEventArgs e)
    {
        // Locking walls joins them: Lock without Auto Join would lock nothing.
        if (LockJoinBox.IsChecked == true && ReferenceEquals(sender, LockJoinBox)) AutoJoinBox.IsChecked = true;
        if (AutoJoinBox.IsChecked != true) LockJoinBox.IsChecked = false;

        Plan.AutoJoinWalls = AutoJoinBox.IsChecked == true;
        Plan.LockJoinedWalls = Plan.AutoJoinWalls && LockJoinBox.IsChecked == true;
        PlaceAutoJoin.IsChecked = Plan.AutoJoinWalls;
        PlaceLock.IsChecked = LockJoinBox.IsChecked = Plan.LockJoinedWalls;

        StatusHint.Text = !Plan.AutoJoinWalls ? "New walls are not joined to the walls they lie against."
            : Plan.LockJoinedWalls ? "Auto Join and Lock: new walls against others are joined, and move with them."
            : "Auto Join: new walls against others are joined, so doors and windows cut through both.";
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
    private void OnWallFunctionsButton(object sender, RoutedEventArgs e)
    {
        WallFunctionsMenu.PlacementTarget = WallFunctionsButton;
        WallFunctionsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        WallFunctionsMenu.IsOpen = true;
    }

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

    /// <summary>What the Dimension tool can prefer on a wall, as the options bar shows it.</summary>
    private static readonly Dictionary<string, DimensionPreference> DimensionPreferences = new()
    {
        ["Wall centrelines"] = DimensionPreference.WallCentrelines,
        ["Wall faces"] = DimensionPreference.WallFaces,
        ["Centre of core"] = DimensionPreference.CentreOfCore,
        ["Faces of core"] = DimensionPreference.FacesOfCore
    };

    private void OnDimensionPreferChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (DimensionPreferPicker.SelectedItem is string name && DimensionPreferences.TryGetValue(name, out var prefer)) Plan.DimensionPrefer = prefer;
    }

    /// <summary>The materials Paint can put on, on the options bar.</summary>
    private void ShowPaintOptions()
    {
        _loadingOptions = true;
        var materials = _document.Materials.OrderBy(material => material.Name).ToList();
        PaintMaterialPicker.ItemsSource = materials;
        PaintMaterialPicker.SelectedItem = materials.FirstOrDefault(material => material.Id == Plan.PaintMaterialId)
            ?? materials.FirstOrDefault(material => material.Name.Contains("Paint", StringComparison.OrdinalIgnoreCase))
            ?? materials.FirstOrDefault();
        Plan.PaintMaterialId = (PaintMaterialPicker.SelectedItem as Core.Materials.Material)?.Id;
        RemovePaintBox.IsChecked = Plan.RemovingPaint;
        _loadingOptions = false;
    }

    private void OnPaintOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        Plan.PaintMaterialId = (PaintMaterialPicker.SelectedItem as Core.Materials.Material)?.Id;
        Plan.RemovingPaint = RemovePaintBox.IsChecked == true;
    }

    private void OnJointGapChanged(object sender, RoutedEventArgs e)
    {
        if (ParameterFormatter.TryParse(ParameterDataType.Length, JointGapBox.Text, out var value) && value is double gap
            && gap >= WallGaps.LeastGap - 1e-9 && gap <= WallGaps.MostGap + 1e-9)
            Plan.JointGap = gap;

        JointGapBox.Text = Units.FormatLength(Plan.JointGap);
    }

    private void OnJointGapKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnJointGapChanged(sender, e);
        Plan.Focus();
        e.Handled = true;
    }

    private void OnRotateAngleKey(object sender, KeyEventArgs e)
    {
        if (LeaveToolFromOptions(e)) return;
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (!ParameterFormatter.TryParse(ParameterDataType.Angle, RotateAngleBox.Text, out var value) || value is not double degrees)
        {
            StatusHint.Text = "Type the angle in degrees: 90, -45, 12.5.";
            return;
        }

        Plan.RotateBy(degrees);
        Plan.Focus();
    }

    private void OnPlaceRotateCentre(object sender, RoutedEventArgs e)
    {
        Plan.PlacingRotateCentre = RotateCentreButton.IsChecked == true;
        Plan.Focus();
    }

    private void OnRotateCopyChanged(object sender, RoutedEventArgs e) => Plan.RotateCopies = RotateCopyBox.IsChecked == true;

    private void OnScaleFactorChanged(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(ScaleFactorBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var factor)
            && factor > 0 && factor <= 1000 && Math.Abs(factor - 1) > 1e-9)
            Plan.ScaleFactor = factor;

        ScaleFactorBox.Text = Plan.ScaleFactor.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    private void OnScaleFactorKey(object sender, KeyEventArgs e)
    {
        if (LeaveToolFromOptions(e)) return;
        if (e.Key != Key.Enter) return;

        OnScaleFactorChanged(sender, e);
        Plan.Focus();
        e.Handled = true;
    }

    /// <summary>Esc typed in a box on the options bar leaves the tool too: the window never sees a key a text box takes.</summary>
    private bool LeaveToolFromOptions(KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return false;

        e.Handled = true;
        Plan.Focus();
        BackToModify();
        return true;
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        Plan.PinSelected(true);
        RefreshContextTab(bringForward: false);
    }

    private void OnUnpin(object sender, RoutedEventArgs e)
    {
        Plan.PinSelected(false);
        RefreshContextTab(bringForward: false);
    }

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
        Plan.ShowUnderlay = UnderlayMenuItem.IsChecked == true;
        PlanUnderlayToggle.IsChecked = Plan.ShowUnderlay;
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
        RefreshSection();
        SheetSurface.Refresh();
        Refresh3D();

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

        Title = $"{name}{marker} — BIMDesigner 0.1";
        StatusSaved.Text = _path is null
            ? _history.IsModified ? "Unsaved project — modified" : "Unsaved project"
            : _history.IsModified ? $"{Path.GetFileName(_path)} — modified" : Path.GetFileName(_path);
    }
}
