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

    /// <summary>Where a handle being dragged in 3D has got to, not yet followed.</summary>
    private ModelGripDrag? _gripDrag;
    private bool _gripDragPending;

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

        // Another tab opens at its first panel, however far the last one was scrolled.
        Ribbon.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Ribbon)) (Ribbon.Template?.FindName("RibbonScroll", Ribbon) as RibbonScroller)?.ScrollToStart();
        };
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

        // A camera made, or a walkthrough drawn, is opened in the 3D view.
        Plan.CameraPlaced += (_, view) => Show3D(view);
        Plan.WalkthroughPlaced += (_, walk) => ShowWalkthrough(walk);

        // A section box dragged in 3D is one step to undo.
        Model3D.ViewEdited += (_, command) =>
        {
            _history.Record(command);
            AfterHistoryChange();
        };
        Model3D.MovingChanged += (_, _) => SyncMoving();
        Model3D.WalkStopped += (_, why) => StatusHint.Text = why;
        FillWalkAsPicker();

        // Interior elevations open from their arrows on the plan; callouts from their boxes,
        // and a callout drawn on a section is made there.
        Plan.InteriorViewPicked += (_, view) => ShowInteriorElevation(view);
        Plan.CalloutPicked += (_, callout) => ShowCallout(callout);
        Section.CalloutDrawn += (_, region) =>
        {
            if (Section.Marker is not { } marker) return;
            var (callout, command) = Callouts.Draw(_document, ViewReference.Section(marker.Id), marker.LevelId, new Point2D(region.MinX, region.MinY), new Point2D(region.MaxX, region.MaxY));
            _history.Execute(command);
            AfterHistoryChange();
            StatusHint.Text = $"{callout.Name}: a detail of Section {marker.Name}, a view of its own at 1:20, under Callouts.";
        };

        // What is projected for sheets is kept until the model changes - or a 3D view on a sheet is looked through again.
        Model3D.CameraChanged += (_, _) => Projections.Forget(_document);
        Model3D.FrameShown += (_, frame) => ShowFrameNumber(frame);
        Plan.SweepEditChanged += (_, _) => SyncSweepEditControls();
        Plan.JunctionsChanged += (_, _) => RefreshJunctionOptions();
        Plan.WallPointModeChanged += (_, _) => ContextAddPoint.IsChecked = Plan.AddingWallPoints;

        Section.SelectionChanged += (_, _) =>
        {
            if (_syncingSectionSelection) return;
            Plan.Select(Section.SelectedElement);
        };

        // With nothing selected, the view clicked into shows its own properties.
        Section.GotKeyboardFocus += OnViewFocused;
        Projected.GotKeyboardFocus += OnViewFocused;
        Model3D.GotKeyboardFocus += OnViewFocused;
        Plan.GotKeyboardFocus += OnViewFocused;

        // A section's callout cropped by its corner: one step to undo.
        Section.CalloutCropped += (_, cropped) =>
        {
            var (callout, before, after) = (cropped.Callout, cropped.Before, cropped.Callout.Region);
            _history.Record(new DelegateCommand("Crop Callout", () => callout.Region = after, () => callout.Region = before));
            AfterHistoryChange();
            StatusHint.Text = $"{callout.Name} cropped to {Core.Units.Metres(after.Width, "0.00")} by {Core.Units.Metres(after.Height, "0.00")}.";
        };

        // Clicking something in an interior elevation selects it everywhere, as in 3D.
        Projected.ElementClicked += (_, id) =>
        {
            var picked = id is { } found ? _document.FindElement(found) ?? CurtainPanel.Find(_document, found) : null;
            if (picked is not null && picked.LevelId != Plan.ActiveLevelId && _document.FindLevel(picked.LevelId) is { } level) LevelPicker.SelectedItem = level;
            Plan.Select(picked);
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

        // A selected stair's handles, dragged in 3D as in plan. The mouse can move many times a
        // frame: only where it has got to is followed, once the moves so far are in, and only
        // the stair is built again as it goes - the whole model once, when it is let go.
        Model3D.Grips = Plan.Grips3D;
        Model3D.GripDragged += (_, drag) =>
        {
            _gripDrag = drag;
            if (_gripDragPending) return;

            _gripDragPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, FollowGripDrag);
        };
        Model3D.GripDragEnded += (_, key) =>
        {
            FollowGripDrag();
            _gripDrag = null;
            if (key.StartsWith("handle:", StringComparison.Ordinal)) Plan.EndHandleIn3D();
            else Plan.EndStairGripIn3D();
        };
        Model3D.GripClicked += (_, key) => Plan.ClickStairGripIn3D(key);

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

            // The By Face tools act on the face of a mass where it is clicked.
            if (hit is { } onMass && Plan.ActiveTool is PlanTool.WallByFace or PlanTool.CurtainByFace or PlanTool.FloorByFace or PlanTool.RoofByFace &&
                Plan.ByFaceIn3D(onMass.Id, onMass.At))
            {
                RefreshProperties();
                Refresh3D();
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

            // The stair tool: a click on a floor, a terrace or the ground puts a straight stair there.
            if (hit is { } onFloor && Plan.ActiveTool == PlanTool.Stair && Plan.PlaceStairIn3D(onFloor.Id, onFloor.At))
            {
                Model3D.Focus();
                return;
            }

            // The ramp tool, the same: a straight ramp there.
            if (hit is { } onGround && Plan.ActiveTool == PlanTool.Ramp && Plan.PlaceRampIn3D(onGround.Id, onGround.At))
            {
                Model3D.Focus();
                return;
            }

            // Opening by Face: a click on a roof cuts an opening square to its face there.
            if (hit is { } onFace && Plan.ActiveTool == PlanTool.FaceOpening &&
                Plan.PlaceFaceOpeningIn3D(onFace.Id, onFace.At))
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
        Plan.EditingGroupChanged += (_, _) =>
        {
            GroupEditOptions.Visibility = Plan.EditingGroup is null ? Visibility.Collapsed : Visibility.Visible;
            GroupEditLabel.Text = Plan.EditingGroup is { } editing ? $"Editing {editing.Name}" : string.Empty;
            AfterHistoryChange();
        };
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

            // Nothing to offer: what is wrong is on the status bar, and the tool waits for another click - no box to close first.
        };

        // A roof by extrusion placed in plan: its section is drawn next, square-on, over the
        // width just clicked - where Revit would go to an elevation to draw it.
        Plan.ExtrusionPlaced += (_, request) => DrawExtrusionProfile(request);
        Plan.EditProfileRequested += (_, roof) => EditRoofProfile(roof);
        Plan.UnderlayLayersRequested += (_, underlay) => EditUnderlayLayers(underlay);

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
            if (Start is { } start) Begin(start);
            else OpenStartupProject();
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

        // On a small screen - a laptop's 1366 x 768 - every pixel is wanted: it opens full screen.
        if (work.Width < 1600 || work.Height < 900) WindowState = WindowState.Maximized;
    }

    // ---- document --------------------------------------------------------------

    /// <summary>Opens a project named on the command line, else starts from the template.</summary>
    private void OpenStartupProject()
    {
        var path = (Application.Current as App)?.StartupProjectPath;

        // An IFC file opened with BIMDesigner: read into a new project.
        if (path is not null && string.Equals(Path.GetExtension(path), ".ifc", StringComparison.OrdinalIgnoreCase) && ImportIfc(path, quiet: false) is not null)
            return;

        if (path is not null && !string.Equals(Path.GetExtension(path), ".ifc", StringComparison.OrdinalIgnoreCase))
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

        // Shown and typed in its own units from here on: a US project in feet and inches, snapping to 4 in.
        Core.Units.System = document.ProjectInformation.Units;
        Plan.SnapStepMm = Core.Units.Imperial ? 4 * Core.Units.MillimetresPerInch : 100;
        StatusGrid.Text = $"Grid: {Core.Units.FormatLength(Plan.SnapStepMm)}";
        StartToolFigures();

        // A project opened is shown whole, not as built on a date of the last one's programme.
        Model3D.ConstructionDate = null;
        Dispatcher.BeginInvoke(RaiseCoordinationChanged);

        // Its sheets are not projected again at every move of the mouse: what was projected is kept till it changes.
        Projections.Keep(document);

        // The plan's ground drawn plainly to start with; coloured as picked on the plan bar.
        PlanGroundPicker.ItemsSource ??= new[] { "Ground: plain", "Ground: by slope", "Ground: by height", "Ground: cut and fill" };
        if (PlanGroundPicker.SelectedIndex < 0) PlanGroundPicker.SelectedIndex = 0;

        _history = new UndoStack();
        WatchForWorkToKeep();
        _history.Changed += (_, _) =>
        {
            // Roof edges picked off walls go wherever their walls now are - after a move, a
            // change of type, an undo. Worked out afresh rather than carried by each command,
            // so no way of moving a wall can leave its roof behind.
            if (RoofSketch.FollowWalls(_document)) Plan.RefreshModel();

            // And dormers go on standing on their roofs as those roofs now are.
            if (Dormers.FollowRoofs(_document)) Plan.RefreshModel();

            Projections.Forget(_document);
            RefreshTitle();
            RaiseCoordinationChanged();
            CommandManager.InvalidateRequerySuggested();

            // Every edit reaches here, undo and redo included - and the section is a cut
            // through whatever the model is now, not a picture taken when it was drawn.
            // Moving a wall does not change the element collection, so nothing else would
            // tell the section that the building it is cutting has changed. The same is true
            // of every viewport on every sheet.
            RefreshSection();
            SheetSurface.Refresh();
            Refresh3D();

            // Something moved, placed or taken away may stand in the space a fitting needs.
            RefreshClearances();

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

        // A section or sheet of the last project is not one of this: their panels close, rather than stand empty.
        OnCloseSection(this, new RoutedEventArgs());
        Plan.ActiveDiscipline = Core.Mep.Discipline.Architectural;
        OnCloseSheet(this, new RoutedEventArgs());

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
        Remember();

        // Fitting needs the canvas's real size. Right after a load the window may not have
        // laid out yet - especially on the first show, where it is still being resized to
        // the work area - so the fit waits for layout rather than measuring a stale size.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Plan.ZoomToFit());
    }

    private void PopulateOptionBar()
    {
        _loadingOptions = true;

        var wallTypes = WallTypeChoices();
        // Each country's own catalogue: a US project's doors in inches, an English one's in millimetres.
        var doorTypes = _document.TypesOf<DoorType>().Where(t => ProjectTemplates.Fits(_document, t)).OrderBy(t => t.Name).ToList();
        var windowTypes = _document.TypesOf<WindowType>().Where(t => ProjectTemplates.Fits(_document, t)).OrderBy(t => t.Name).ToList();

        WallTypePicker.ItemsSource = wallTypes;
        // An ordinary layered wall to start with, not whichever type sorts first.
        WallTypePicker.SelectedItem = wallTypes.OfType<WallType>().FirstOrDefault(t => t.Function == WallFunction.Exterior) ?? wallTypes.FirstOrDefault();

        // What a first door and window usually are, not whichever sorts first: a plain single door and a
        // casement in England; in the US, the inch-sized door and a double-hung window.
        var us = _document.ProjectInformation.ClearanceStandard.IsUs();
        T? Preferred<T>(IReadOnlyList<T> types, params string[] names) where T : ElementType =>
            names.Select(name => types.FirstOrDefault(type => type.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(type => type is not null) ?? types.FirstOrDefault();

        DoorTypePicker.ItemsSource = doorTypes;
        DoorTypePicker.SelectedItem = us ? Preferred(doorTypes, "Exterior Door - 36 x 80", "Single - 900") : Preferred(doorTypes, "Single - 900 x 2100", "Single");

        WindowTypePicker.ItemsSource = windowTypes;
        WindowTypePicker.SelectedItem = us ? Preferred(windowTypes, "Double Hung - ", "Double Hung") : Preferred(windowTypes, "Casement - 1200 x 1200", "Casement");

        var roofWindowTypes = _document.TypesOf<RoofWindowType>().OrderBy(t => t.Name).ToList();
        RoofWindowTypePicker.ItemsSource = roofWindowTypes;
        RoofWindowTypePicker.SelectedItem = roofWindowTypes.FirstOrDefault();
        Plan.ActiveRoofWindowTypeId = roofWindowTypes.FirstOrDefault()?.Id ?? Guid.Empty;

        var families = _document.TypesOf<ComponentType>()
            .Where(t => ProjectTemplates.Fits(_document, t))
            .OrderBy(t => t.Kind.ToString())
            .ThenBy(t => t.Name)
            .Select(t => new ComponentFamilyChoice(t))
            .ToList();

        ComponentTypePicker.ItemsSource = families;
        ComponentTypePicker.SelectedItem = families.FirstOrDefault(family => family.Type.Kind == BuiltInCategory.Furniture) ?? families.FirstOrDefault();

        var columnTypes = _document.TypesOf<ColumnType>().Where(t => !t.Structural).OrderBy(t => t.Name).ToList();
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

        // In the order of the styles themselves.
        VisualStylePicker.ItemsSource = new[] { "Shaded", "Flat colours", "X-ray", "Wireframe", "Hidden line" };
        VisualStylePicker.SelectedIndex = 0;

        RefreshSchedulePicker(_document.Schedules.FirstOrDefault());

        _loadingOptions = false;

        // Whatever the picker shows: it starts on an exterior wall, not the first type by name.
        Plan.ActiveWallTypeId = (WallTypePicker.SelectedItem as ElementType)?.Id ?? Guid.Empty;
        // The door and window the tools put in are the ones their pickers show, not the first by name.
        Plan.ActiveDoorTypeId = (DoorTypePicker.SelectedItem as DoorType)?.Id ?? doorTypes.FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveWindowTypeId = (WindowTypePicker.SelectedItem as WindowType)?.Id ?? windowTypes.FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveComponentTypeId = (ComponentTypePicker.SelectedItem as ComponentFamilyChoice)?.Type.Id ?? Guid.Empty;
        Plan.ActiveColumnTypeId = (ColumnTypePicker.SelectedItem as ColumnType)?.Id ?? Guid.Empty;
        Plan.ActiveStructuralColumnTypeId = StartingType(_document.TypesOf<ColumnType>().Where(t => t.Structural).OrderBy(t => t.Name).ToList(),
            us ? new[] { "CHS 88.9", "W8" } : new[] { "UC 152 x 152 x 23", "UC" })?.Id ?? Guid.Empty;
        Plan.ActiveFloorTypeId = _document.TypesOf<FloorType>().FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveCeilingTypeId = _document.TypesOf<CeilingType>().FirstOrDefault()?.Id ?? Guid.Empty;
        // The Roof tool starts out sloping its sides, so with a pitched build-up rather than a flat one.
        var roofTypes = _document.TypesOf<RoofType>().ToList();
        Plan.ActiveRoofTypeId = (roofTypes.FirstOrDefault(type => !type.Name.Contains("Flat", StringComparison.OrdinalIgnoreCase)) ?? roofTypes.FirstOrDefault())?.Id ?? Guid.Empty;
        Plan.ActivePipeTypeId = _document.TypesOf<Core.Mep.PipeType>().FirstOrDefault()?.Id ?? Guid.Empty;
        Plan.ActiveDuctTypeId = _document.TypesOf<Core.Mep.DuctType>().FirstOrDefault()?.Id ?? Guid.Empty;
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
        var isSlab = tool is PlanTool.Floor or PlanTool.Ceiling or PlanTool.Roof or PlanTool.RoofExtrusion or PlanTool.FloorByFace or PlanTool.RoofByFace;
        var isSweep = tool is PlanTool.Sweep or PlanTool.Reveal;
        var isRoofEdge = tool is PlanTool.Fascia or PlanTool.Gutter or PlanTool.Soffit or PlanTool.Eave or PlanTool.SlabEdge or PlanTool.WallFooting or PlanTool.PadFooting;

        // Grids, sections, annotation and the editing tools are not built from a type, so
        // offering one would be asking a question the tool never reads the answer to.
        var typeless = tool is PlanTool.Grid or PlanTool.Section
            or PlanTool.Dimension or PlanTool.Tag or PlanTool.Text
            or PlanTool.Offset or PlanTool.Mirror or PlanTool.Array or PlanTool.Rotate or PlanTool.Scale or PlanTool.SplitGap
            or PlanTool.Paint or PlanTool.SplitFace
            or PlanTool.WallJoins or PlanTool.JoinGeometry or PlanTool.WallOpening
            or PlanTool.JoinRoof or PlanTool.DormerOpening or PlanTool.Dormer or PlanTool.Shaft or PlanTool.FaceOpening or PlanTool.Downpipe or PlanTool.RoofDrain or PlanTool.Chimney
            or PlanTool.Stair or PlanTool.Ramp or PlanTool.Railing or PlanTool.Beam or PlanTool.RoomSeparator or PlanTool.Pipe or PlanTool.Duct or PlanTool.Terrain or PlanTool.Region or PlanTool.Joists
            or PlanTool.Pad or PlanTool.PropertyLine or PlanTool.SpotElevation or PlanTool.Mass or PlanTool.Camera or PlanTool.Walkthrough
            or PlanTool.InteriorElevation or PlanTool.Callout or PlanTool.AngularDimension or PlanTool.RadialDimension
            or PlanTool.RevisionCloud or PlanTool.Keynote;

        WallOpeningOptions.Visibility = tool == PlanTool.WallOpening ? Visibility.Visible : Visibility.Collapsed;
        DormerOptions.Visibility = tool == PlanTool.Dormer ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Dormer) ShowDormerOptions();
        ShaftOptions.Visibility = tool == PlanTool.Shaft ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Shaft) ShowShaftOptions();
        FaceOpeningOptions.Visibility = tool == PlanTool.FaceOpening ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.FaceOpening) ShowFaceOpeningOptions();
        ChimneyOptions.Visibility = tool == PlanTool.Chimney ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Chimney) ShowChimneyOptions();
        StairOptions.Visibility = tool == PlanTool.Stair ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Stair) ShowStairOptions();
        RampOptions.Visibility = tool == PlanTool.Ramp ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Ramp) ShowRampOptions();
        RailingOptions.Visibility = tool == PlanTool.Railing ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Railing) ShowRailingOptions();
        BeamOptions.Visibility = tool == PlanTool.Beam ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Beam) ShowBeamOptions();
        PipeOptions.Visibility = tool == PlanTool.Pipe ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Pipe) ShowPipeOptions();
        DuctOptions.Visibility = tool == PlanTool.Duct ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Duct) ShowDuctOptions();
        TerrainOptions.Visibility = tool == PlanTool.Terrain ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Terrain) ShowGroundOptions();
        RegionOptions.Visibility = tool == PlanTool.Region ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Region) ShowRegionOptions();
        PadOptions.Visibility = tool == PlanTool.Pad ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Pad) ShowPadOptions();
        MassOptions.Visibility = tool == PlanTool.Mass ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Mass) ShowMassOptions();
        PropertyLineOptions.Visibility = tool == PlanTool.PropertyLine ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.PropertyLine) ShowPropertyLineOptions();
        JoistOptions.Visibility = tool == PlanTool.Joists ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Joists) ShowJoistOptions();

        JunctionOptions.Visibility = tool == PlanTool.WallJoins ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.WallJoins) RefreshJunctionOptions();

        PickNewHostOptions.Visibility = Plan.PickingNewHost ? Visibility.Visible : Visibility.Collapsed;
        AttachColumnOptions.Visibility = Plan.AttachingColumns ? Visibility.Visible : Visibility.Collapsed;
        OffsetOptions.Visibility = tool == PlanTool.Offset ? Visibility.Visible : Visibility.Collapsed;
        WallDrawOptions.Visibility = tool == PlanTool.Wall ? Visibility.Visible : Visibility.Collapsed;
        MirrorOptions.Visibility = tool == PlanTool.Mirror ? Visibility.Visible : Visibility.Collapsed;
        ArrayOptions.Visibility = tool == PlanTool.Array ? Visibility.Visible : Visibility.Collapsed;
        var dimensioning = tool is PlanTool.Dimension or PlanTool.AngularDimension or PlanTool.RadialDimension;
        DimensionOptions.Visibility = dimensioning ? Visibility.Visible : Visibility.Collapsed;
        if (dimensioning)
        {
            _loadingOptions = true;
            DimensionPreferPicker.ItemsSource ??= DimensionPreferences.Keys.ToList();
            DimensionPreferPicker.SelectedItem = DimensionPreferences.First(entry => entry.Value == Plan.DimensionPrefer).Key;
            var styles = _document.TypesOf<DimensionType>().OrderBy(type => type.Name).ToList();
            DimensionTypePicker.ItemsSource = styles;
            // What the tool dimensions in is what the picker shows: feet and inches in a US project.
            DimensionTypePicker.SelectedItem = styles.FirstOrDefault(type => type.Id == Plan.ActiveDimensionTypeId) ?? DimensionTypes.Starting(_document);
            Plan.ActiveDimensionTypeId = (DimensionTypePicker.SelectedItem as DimensionType)?.Id ?? Guid.Empty;
            DimensionStringBox.IsChecked = Plan.DimensionString;
            DimensionStringBox.Visibility = tool == PlanTool.Dimension ? Visibility.Visible : Visibility.Collapsed;
            _loadingOptions = false;
        }

        RevisionCloudOptions.Visibility = tool == PlanTool.RevisionCloud ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.RevisionCloud) ShowCloudOptions();
        KeynoteOptions.Visibility = tool == PlanTool.Keynote ? Visibility.Visible : Visibility.Collapsed;
        if (tool == PlanTool.Keynote) ShowKeynoteOptions();

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

        WallTypePicker.Visibility = tool is PlanTool.Door or PlanTool.Window or PlanTool.RoofWindow or PlanTool.Component or PlanTool.Column or PlanTool.StructuralColumn || isSlab || isSweep || isRoofEdge || typeless || selecting
            ? Visibility.Collapsed : Visibility.Visible;
        TypeLabel.Visibility = typeless || selecting ? Visibility.Collapsed : Visibility.Visible;
        DoorTypePicker.Visibility = tool == PlanTool.Door ? Visibility.Visible : Visibility.Collapsed;
        TagOnPlacementBox.Visibility = tool is PlanTool.Door or PlanTool.Window ? Visibility.Visible : Visibility.Collapsed;
        WindowTypePicker.Visibility = tool == PlanTool.Window ? Visibility.Visible : Visibility.Collapsed;
        RoofWindowTypePicker.Visibility = tool == PlanTool.RoofWindow ? Visibility.Visible : Visibility.Collapsed;
        ComponentTypePicker.Visibility = tool == PlanTool.Component ? Visibility.Visible : Visibility.Collapsed;
        ColumnTypePicker.Visibility = ColumnOptions.Visibility =
            tool is PlanTool.Column or PlanTool.StructuralColumn ? Visibility.Visible : Visibility.Collapsed;
        if (tool is PlanTool.Column or PlanTool.StructuralColumn) LoadColumnTypes(tool == PlanTool.StructuralColumn);
        SlabTypePicker.Visibility = isSlab ? Visibility.Visible : Visibility.Collapsed;
        RoofOptions.Visibility = tool == PlanTool.Roof || Plan.IsSketching && tool is PlanTool.Floor or PlanTool.Ceiling ? Visibility.Visible : Visibility.Collapsed;

        if (tool == PlanTool.Roof) ShowRoofSketchOptions();

        if (isSlab)
        {
            _loadingOptions = true;

            var types = tool switch
            {
                PlanTool.Floor or PlanTool.FloorByFace => _document.TypesOf<SlabType>().Where(t => t is FloorType),
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
            PlanTool.Floor or PlanTool.FloorByFace => "Floor type",
            PlanTool.CurtainByFace => "Curtain wall type",
            PlanTool.RoofByFace => "Roof type",
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
            PlanTool.SlabEdge => _document.TypesOf<SlabEdgeType>().Cast<ElementType>(),
            PlanTool.Soffit => _document.TypesOf<SoffitType>().Cast<ElementType>(),
            PlanTool.Eave => _document.TypesOf<EaveType>().Cast<ElementType>(),
            PlanTool.WallFooting => _document.TypesOf<FootingType>().Where(t => t.Kind == FootingKind.Strip && ProjectTemplates.Fits(_document, t)).Cast<ElementType>(),
            PlanTool.PadFooting => _document.TypesOf<FootingType>().Where(t => t.Kind == FootingKind.Pad && ProjectTemplates.Fits(_document, t)).Cast<ElementType>(),
            _ => _document.TypesOf<FasciaType>().Cast<ElementType>()
        }).OrderBy(t => t.Name).Select(t => new RoofEdgeTypeChoice(t.Name, t.Id, tool)).ToList();

        // A fascia can be left to stand out against whatever walls and roof it is put on.
        if (tool == PlanTool.Fascia) types.Insert(0, new RoofEdgeTypeChoice("Automatic - stands out from the walls and roof", Guid.Empty, tool));
        if (tool is PlanTool.WallFooting or PlanTool.PadFooting) types.Insert(0, new RoofEdgeTypeChoice("Automatic - the size the rules ask", Guid.Empty, tool));

        var active = tool switch
        {
            PlanTool.Gutter => Plan.ActiveGutterTypeId,
            PlanTool.Soffit => Plan.ActiveSoffitTypeId,
            PlanTool.Eave => Plan.ActiveEaveTypeId,
            PlanTool.WallFooting => Plan.ActiveWallFootingTypeId,
            PlanTool.PadFooting => Plan.ActivePadFootingTypeId,
            PlanTool.SlabEdge => Plan.ActiveSlabEdgeTypeId,
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
            case PlanTool.Eave: Plan.ActiveEaveTypeId = choice.Id; break;
            case PlanTool.WallFooting: Plan.ActiveWallFootingTypeId = choice.Id; break;
            case PlanTool.PadFooting: Plan.ActivePadFootingTypeId = choice.Id; break;
            case PlanTool.SlabEdge: Plan.ActiveSlabEdgeTypeId = choice.Id; break;
            default: Plan.ActiveFasciaTypeId = choice.Id; break;
        }
    }

    /// <summary>Roof panel: a fascia all round each selected roof.</summary>
    private void OnAddFasciaAllRound(object sender, RoutedEventArgs e) => AddRoofEdgesAllRound(RoofEdgeKind.Fascia);

    /// <summary>Roof panel: gutters along every eave of each selected roof.</summary>
    private void OnAddGuttersAllRound(object sender, RoutedEventArgs e) => AddRoofEdgesAllRound(RoofEdgeKind.Gutter);

    /// <summary>Roof panel: soffits under every overhanging eave of each selected roof.</summary>
    private void OnAddSoffitsAllRound(object sender, RoutedEventArgs e) => AddRoofEdgesAllRound(RoofEdgeKind.Soffit);

    /// <summary>Roof panel: an eave under every eave of each selected roof.</summary>
    private void OnAddEavesAllRound(object sender, RoutedEventArgs e) => AddRoofEdgesAllRound(RoofEdgeKind.Eave);

    /// <summary>Foundation panel: footings under the selected walls and structural columns that have none.</summary>
    private void OnAddFootings(object sender, RoutedEventArgs e)
    {
        var added = Plan.AddFootings(Plan.SelectedElements.ToList());
        AfterHistoryChange();
        StatusHint.Text = added == 0
            ? "Every selected wall and structural column has a footing already - or none was selected."
            : $"{added} footing{(added == 1 ? "" : "s")} put in, each as wide and deep as the rules ask for its load on {StructureRules.Describe(_document.ProjectInformation.Ground)}. Select one for its Regulations.";
    }

    private void AddRoofEdgesAllRound(RoofEdgeKind kind)
    {
        var roofs = Plan.SelectedElements.OfType<Roof>().ToList();
        if (roofs.Count == 0)
        {
            StatusHint.Text = "Select a roof first.";
            return;
        }

        // A main roof's dormers too: their eaves want the same as its own.
        roofs = roofs.Concat(roofs.SelectMany(roof => _document.All<Roof>().Where(dormer => dormer.JoinedTo == roof.Id)))
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
                    RoofEdgeKind.Eave => "Every eave of that roof has an eave already - or it has no eaves, being flat or all verges.",
                    _ => "That roof has a fascia all round already."
                }
            : kind switch
            {
                RoofEdgeKind.Gutter => $"Gutters hung along {edges} eaves. Select one to change its type or move it with its offsets.",
                RoofEdgeKind.Soffit => $"Soffits under {edges} eaves, closing the overhang back to the walls. Select one to change its type.",
                RoofEdgeKind.Eave => $"An eave under {edges} eaves, the roof cut back to its rafters over it. Select it to change its type - closed, open, exposed rafter, box or abbreviated.",
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

            RoofSketchTab.Header = (Plan.SketchTarget, Plan.SketchedRoof, Plan.SketchedSlab) switch
            {
                (SketchTarget.Floor, _, null) => "Modify | Create Floor Boundary",
                (SketchTarget.Floor, _, _) => "Modify | Floors > Edit Boundary",
                (SketchTarget.Ceiling, _, null) => "Modify | Create Ceiling Boundary",
                (SketchTarget.Ceiling, _, _) => "Modify | Ceilings > Edit Boundary",
                (_, null, _) => "Modify | Create Roof Footprint",
                _ => "Modify | Roofs > Edit Footprint"
            };

            // A floor's or ceiling's lines are level: no slope arrows, no eaves to align - and a floor has a span.
            var roofSketch = Plan.SketchTarget == SketchTarget.Roof;
            RoofSketchArrow.Visibility = roofSketch ? Visibility.Visible : Visibility.Collapsed;
            RoofSketchToolsPanel.Visibility = roofSketch ? Visibility.Visible : Visibility.Collapsed;
            RoofSketchSpan.Visibility = Plan.SketchTarget == SketchTarget.Floor ? Visibility.Visible : Visibility.Collapsed;
            RoofSketchSpan.IsChecked = Plan.SketchTool == RoofSketchTool.SpanDirection;
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
            RoofSketchMove.IsChecked = Plan.SketchTool == RoofSketchTool.Move;
            RoofSketchMirror.IsChecked = Plan.SketchTool == RoofSketchTool.Mirror;
            RoofSketchAlign.IsChecked = Plan.SketchTool == RoofSketchTool.Align;
            RoofSketchFillet.IsChecked = Plan.SketchTool == RoofSketchTool.Fillet;

            if (SketchToolButton().IsChecked != true)
            {
                _loadingOptions = true;
                SketchToolButton().IsChecked = true;
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
        if (Plan.ActiveTool == PlanTool.Select && (RoofTool.IsChecked == true || FloorTool.IsChecked == true || CeilingTool.IsChecked == true))
        {
            _loadingOptions = true;
            SelectTool.IsChecked = true;
            _loadingOptions = false;
            ShowOptionsForActiveTool();
        }
    }

    private TabItem? _tabBeforeRoofSketch;

    /// <summary>The ribbon tool the open sketch belongs to: Roof, Floor or Ceiling.</summary>
    private RadioButton SketchToolButton() => Plan.SketchTarget switch
    {
        SketchTarget.Floor => FloorTool,
        SketchTarget.Ceiling => CeilingTool,
        _ => RoofTool
    };

    private void ShowRoofSketchOptions()
    {
        _loadingOptions = true;

        // With lines selected, the bar shows theirs; otherwise what new lines will be.
        var selected = Plan.SelectedSketchLines;
        var first = selected.FirstOrDefault()?.Edge;

        // A floor's or ceiling's lines are level, and stand off their walls by an offset rather than overhang them.
        var roofSketch = Plan.SketchTarget == SketchTarget.Roof;
        RoofDefinesSlopeBox.Visibility = RoofSlopeLabel.Visibility = RoofSlopeBox.Visibility = roofSketch ? Visibility.Visible : Visibility.Collapsed;
        RoofOverhangLabel.Text = roofSketch ? "Overhang" : "Offset";

        RoofDefinesSlopeBox.IsChecked = first?.DefinesSlope ?? Plan.SketchDefinesSlope;
        RoofSlopeBox.Text = ParameterFormatter.Format(ParameterDataType.Angle, first?.SlopeDegrees ?? Plan.ActiveRoofSlopeDegrees);
        RoofOverhangBox.Text = Units.FormatLength(first?.Overhang ?? (roofSketch ? Plan.SketchOverhang : Plan.SketchSlabOffset));
        RoofExtendToCoreBox.IsChecked = first?.ExtendToCore ?? Plan.SketchExtendToCore;
        RoofSidesBox.Text = Plan.PolygonSides.ToString();

        // Overhang belongs to lines on walls; sides to polygons.
        var picking = Plan.SketchTool == RoofSketchTool.PickWalls || selected.Any(line => line.Edge.WallId is not null);
        RoofOverhangLabel.Visibility = RoofOverhangBox.Visibility = RoofExtendToCoreBox.Visibility =
            picking ? Visibility.Visible : Visibility.Collapsed;
        RoofSidesLabel.Visibility = RoofSidesBox.Visibility =
            Plan.SketchTool == RoofSketchTool.Polygon ? Visibility.Visible : Visibility.Collapsed;

        // The offset belongs to Pick Lines and to Offset - Fillet Arc's box is its radius; Copy to Offset, Move and Mirror.
        var filleting = Plan.SketchTool == RoofSketchTool.Fillet;
        RoofOffsetLabel.Text = filleting ? "Radius" : "Offset";
        RoofOffsetBox.Text = Units.FormatLength(filleting ? Plan.SketchFilletRadius : Plan.SketchOffset);
        RoofOffsetCopyBox.IsChecked = Plan.SketchTool switch
        {
            RoofSketchTool.Move => Plan.SketchMoveCopy,
            RoofSketchTool.Mirror => Plan.SketchMirrorCopy,
            _ => Plan.SketchOffsetCopy
        };
        RoofOffsetLabel.Visibility = RoofOffsetBox.Visibility =
            Plan.SketchTool is RoofSketchTool.PickLines or RoofSketchTool.Offset or RoofSketchTool.Fillet ? Visibility.Visible : Visibility.Collapsed;
        RoofOffsetCopyBox.Visibility = Plan.SketchTool is RoofSketchTool.Offset or RoofSketchTool.Move or RoofSketchTool.Mirror ? Visibility.Visible : Visibility.Collapsed;

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
    /// <summary>The stair the next clicks put in, on the options bar.</summary>
    private void ShowStairOptions()
    {
        _loadingOptions = true;
        var types = _document.TypesOf<StairType>().OrderBy(type => type.Name).ToList();
        StairTypePicker.ItemsSource = types;
        StairTypePicker.SelectedItem = types.FirstOrDefault(type => type.Id == Plan.ActiveStairTypeId)
                                       ?? types.FirstOrDefault(type => type.Construction == StairConstruction.Timber && !type.OpenRisers)
                                       ?? types.FirstOrDefault();
        Plan.ActiveStairTypeId = (StairTypePicker.SelectedItem as StairType)?.Id ?? Guid.Empty;
        StairDirectionPicker.ItemsSource ??= new[] { "Auto: down off an edge", "Up from here", "Down from here" };
        StairDirectionPicker.SelectedIndex = (int)Plan.NewStairDirection;
        StairShapePicker.ItemsSource ??= EnumText.Choices<StairShape>();
        StairShapePicker.SelectedItem = EnumText.Humanise(Plan.NewStairShape);
        StairCornerPicker.ItemsSource ??= new[] { "On a landing", "On winders", "On balanced winders" };
        StairCornerPicker.SelectedIndex = (int)Plan.NewStairCorner;
        StairCornerPicker.Visibility = Plan.NewStairShape is StairShape.QuarterTurn or StairShape.HalfTurn or StairShape.ThreeQuarterTurn or StairShape.Custom
            ? Visibility.Visible : Visibility.Collapsed;
        StairTurnPicker.ItemsSource ??= new[] { "Turns right", "Turns left" };
        StairTurnPicker.SelectedIndex = Plan.NewStairTurnsLeft ? 1 : 0;
        StairTurnPicker.Visibility = Plan.NewStairShape is StairShape.Straight or StairShape.Bifurcated or StairShape.Custom or StairShape.AlternatingTread or StairShape.Ladder
            ? Visibility.Collapsed : Visibility.Visible;
        StairWidthBox.Text = LengthText(Plan.NewStairWidth);
        var round = Plan.NewStairShape is StairShape.Spiral or StairShape.Curved ? Visibility.Visible : Visibility.Collapsed;
        (StairRadiusLabel.Visibility, StairRadiusBox.Visibility) = (round, round);
        StairRadiusBox.Text = LengthText(Plan.NewStairInnerRadius);
        _loadingOptions = false;
    }

    private void ShowRailingOptions()
    {
        _loadingOptions = true;
        var types = _document.TypesOf<RailingType>().OrderBy(type => type.Name).ToList();
        RailingTypePicker.ItemsSource = types;

        // The type picked last - or, to begin with, guarding as high as a balcony asks, not a fence.
        RailingTypePicker.SelectedItem = types.FirstOrDefault(type => type.Id == Plan.ActiveRailingTypeId) ?? Railings.TypeFor(_document, 1100) ?? types.FirstOrDefault();
        Plan.ActiveRailingTypeId = (RailingTypePicker.SelectedItem as RailingType)?.Id ?? Guid.Empty;
        RailingBuildingPicker.ItemsSource ??= EnumText.Choices<RailingBuilding>();
        RailingBuildingPicker.SelectedItem = EnumText.Humanise(Plan.ActiveRailingBuilding);
        RailingDrawPicker.ItemsSource ??= new[] { "By points", "Arcs", "Pick edges" };
        RailingDrawPicker.SelectedIndex = Plan.PickingEdges ? 2 : Plan.RailingArc ? 1 : 0;
        _loadingOptions = false;
    }

    private void OnRailingOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (RailingTypePicker.SelectedItem is RailingType type) Plan.ActiveRailingTypeId = type.Id;
        if (RailingBuildingPicker.SelectedItem is string building && EnumText.TryParse<RailingBuilding>(building, out var picked)) Plan.ActiveRailingBuilding = picked;
        Plan.PickingEdges = RailingDrawPicker.SelectedIndex == 2;
        Plan.RailingArc = RailingDrawPicker.SelectedIndex == 1;
        ShowRailingOptions();
    }

    private void ShowBeamOptions()
    {
        _loadingOptions = true;
        var types = _document.TypesOf<BeamType>().ToList();
        BeamTypePicker.ItemsSource = types;
        // To start with, a steel beam a house's opening is usually spanned by, not the smallest there is.
        BeamTypePicker.SelectedItem = types.FirstOrDefault(type => type.Id == Plan.ActiveBeamTypeId)
                                      ?? StartingType(types, _document.ProjectInformation.ClearanceStandard.IsUs() ? new[] { "W8x10", "W" } : new[] { "UB 203x133x25", "UB" });
        Plan.ActiveBeamTypeId = (BeamTypePicker.SelectedItem as BeamType)?.Id ?? Guid.Empty;
        BeamOffsetBox.Text = LengthText(Plan.ActiveBeamOffset);
        _loadingOptions = false;
    }

    private void OnBeamOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (BeamTypePicker.SelectedItem is BeamType type) Plan.ActiveBeamTypeId = type.Id;
        if (TryLength(BeamOffsetBox.Text, out var offset) && Math.Abs(offset) <= 20000) Plan.ActiveBeamOffset = offset;
        ShowBeamOptions();
    }

    private void ShowGroundOptions()
    {
        _loadingOptions = true;
        GroundElevationBox.Text = LengthText(Plan.ActiveGroundElevation);
        GroundUnitsPicker.ItemsSource ??= new[] { "In millimetres", "In metres" };
        if (GroundUnitsPicker.SelectedIndex < 0) GroundUnitsPicker.SelectedIndex = 1;
        _loadingOptions = false;
    }

    private void OnGroundOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (TryLength(GroundElevationBox.Text, out var elevation) && Math.Abs(elevation) <= 100000) Plan.ActiveGroundElevation = elevation;
        ShowGroundOptions();
    }

    /// <summary>Import Points: spot levels read from a file the user picks, onto the ground on this level.</summary>
    private void OnImportGroundPoints(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import ground points",
            Filter = "Points files (*.csv;*.txt;*.xyz)|*.csv;*.txt;*.xyz|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            Plan.ImportGround(System.IO.File.ReadAllText(dialog.FileName), GroundUnitsPicker.SelectedIndex == 1 ? 1000 : 1);
        }
        catch (System.IO.IOException failure)
        {
            MessageBox.Show(this, $"The points file could not be read: {failure.Message}", "Import Points", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Around Building: the ground put round the whole building on the storey shown, at the Elevation on the Toposurface option bar.</summary>
    private void OnGroundAroundBuilding(object sender, RoutedEventArgs e)
    {
        Plan?.GroundAroundBuilding();
    }

    private void ShowPadOptions()
    {
        _loadingOptions = true;
        PadSurfacePicker.ItemsSource ??= EnumText.Choices<GroundSurface>();
        PadSurfacePicker.SelectedItem = EnumText.Humanise(Plan.ActivePadSurface);
        PadOffsetBox.Text = LengthText(Plan.ActivePadOffset);
        PadBatterBox.Text = Plan.ActivePadBatter.ToString("0.##");
        _loadingOptions = false;
    }

    private void OnPadOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (PadSurfacePicker.SelectedItem is string surface && EnumText.TryParse<GroundSurface>(surface, out var picked)) Plan.ActivePadSurface = picked;
        if (TryLength(PadOffsetBox.Text, out var offset) && Math.Abs(offset) <= 20000) Plan.ActivePadOffset = offset;
        if (double.TryParse(PadBatterBox.Text, out var batter) && batter is >= 0 and <= 100) Plan.ActivePadBatter = batter;
        ShowPadOptions();
    }

    private void ShowMassOptions()
    {
        _loadingOptions = true;
        MassFootprintPicker.ItemsSource ??= EnumText.Choices<MassFootprint>();
        MassFootprintPicker.SelectedItem = EnumText.Humanise(Plan.ActiveMassFootprint);
        MassTopPicker.ItemsSource ??= EnumText.Choices<MassTop>();
        MassTopPicker.SelectedItem = EnumText.Humanise(Plan.ActiveMassTop);
        MassHeightBox.Text = LengthText(Plan.ActiveMassHeight);
        MassPitchBox.Text = Plan.ActiveMassPitch.ToString("0.#");
        MassScaleBox.Text = Plan.ActiveMassTopScale.ToString("0.##");
        MassPitchLabel.Visibility = MassPitchBox.Visibility = Plan.ActiveMassTop is MassTop.Gable or MassTop.Hip or MassTop.Shed ? Visibility.Visible : Visibility.Collapsed;
        MassScaleLabel.Visibility = MassScaleBox.Visibility = Plan.ActiveMassTop == MassTop.Tapered ? Visibility.Visible : Visibility.Collapsed;
        _loadingOptions = false;
    }

    private void OnMassOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (MassFootprintPicker.SelectedItem is string footprint && EnumText.TryParse<MassFootprint>(footprint, out var drawn)) Plan.ActiveMassFootprint = drawn;
        if (MassTopPicker.SelectedItem is string top && EnumText.TryParse<MassTop>(top, out var topped)) Plan.ActiveMassTop = topped;
        if (TryLength(MassHeightBox.Text, out var height) && height is >= 300 and <= 500000) Plan.ActiveMassHeight = height;
        if (double.TryParse(MassPitchBox.Text, out var pitch) && pitch is >= 1 and <= 75) Plan.ActiveMassPitch = pitch;
        if (double.TryParse(MassScaleBox.Text, out var scale) && scale is >= 0 and < 1) Plan.ActiveMassTopScale = scale;
        ShowMassOptions();
    }

    private void OnMassFloors(object sender, RoutedEventArgs e)
    {
        if (Plan.ToggleMassFloors()) AfterHistoryChange();
    }

    private void OnUpdateMassToFace(object sender, RoutedEventArgs e)
    {
        if (Plan.UpdateMassToFace()) AfterHistoryChange();
    }

    private void ShowPropertyLineOptions()
    {
        _loadingOptions = true;
        SetbackBox.Text = LengthText(Plan.ActiveSetback);
        _loadingOptions = false;
    }

    private void OnPropertyLineOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (TryLength(SetbackBox.Text, out var setback) && setback is >= 0 and <= 100000) Plan.ActiveSetback = setback;
        ShowPropertyLineOptions();
    }

    /// <summary>The plan's ground coloured as picked: plainly, by slope, by height, or by cut and fill.</summary>
    private void OnPlanGroundColouringChanged(object sender, RoutedEventArgs e)
    {
        if (Plan is null || PlanGroundPicker.SelectedIndex < 0) return;
        Plan.GroundColouring = (GroundColouring)PlanGroundPicker.SelectedIndex;
        Plan.RefreshModel();
    }

    private void ShowRegionOptions()
    {
        _loadingOptions = true;
        RegionSurfacePicker.ItemsSource ??= EnumText.Choices<GroundSurface>();
        RegionSurfacePicker.SelectedItem = EnumText.Humanise(Plan.ActiveGroundSurface);
        RegionPathBox.IsChecked = Plan.DrawingPath;
        PathWidthBox.Text = LengthText(Plan.ActivePathWidth);
        PathWidthBox.IsEnabled = Plan.DrawingPath;
        _loadingOptions = false;
    }

    private void OnRegionOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (RegionSurfacePicker.SelectedItem is string surface && EnumText.TryParse<GroundSurface>(surface, out var picked)) Plan.ActiveGroundSurface = picked;
        Plan.DrawingPath = RegionPathBox.IsChecked == true;
        if (TryLength(PathWidthBox.Text, out var width) && width is >= 300 and <= 20000) Plan.ActivePathWidth = width;
        ShowRegionOptions();
    }

    private void ShowJoistOptions()
    {
        _loadingOptions = true;
        var types = _document.TypesOf<BeamType>().ToList();
        JoistTypePicker.ItemsSource = types;
        JoistTypePicker.SelectedItem = types.FirstOrDefault(type => type.Id == Plan.ActiveJoistTypeId)
                                       ?? types.FirstOrDefault(type => type.Name.Contains("47x200", StringComparison.Ordinal)) ?? types.FirstOrDefault();
        Plan.ActiveJoistTypeId = (JoistTypePicker.SelectedItem as BeamType)?.Id ?? Guid.Empty;
        JoistSpacingBox.Text = LengthText(Plan.ActiveJoistSpacing);
        _loadingOptions = false;
    }

    private void OnJoistOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (JoistTypePicker.SelectedItem is BeamType type) Plan.ActiveJoistTypeId = type.Id;
        if (TryLength(JoistSpacingBox.Text, out var spacing) && spacing is >= 100 and <= 3000) Plan.ActiveJoistSpacing = spacing;
        ShowJoistOptions();
    }

    /// <summary>Record Existing, on the ground picked: the ground as it is now, what its grading is measured from.</summary>
    private void OnRecordExistingGround(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements is not [Toposurface ground]) return;
        Plan.SetExistingGround(ground, record: true);
    }

    /// <summary>Let Go, on the ground picked: no existing ground recorded any more.</summary>
    private void OnForgetExistingGround(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements is not [Toposurface ground]) return;
        Plan.SetExistingGround(ground, record: false);
    }

    /// <summary>
    /// Make Storey, on the floor or flat roof picked: the walls running up through it split at it -
    /// the part under it stopping at its underside, the part over it standing on its top - and,
    /// where it has a room over it at a height no level is at, a level made on it.
    /// </summary>
    private void OnMakeStorey(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements is not [Slab slab]) return;
        if (Storeys.Divide(_document, new[] { slab }) is not { } divided)
        {
            StatusHint.Text = "No wall runs up through it: nothing to split at it. A wall it stops short of - a curtain wall hung in front of it - runs on past it.";
            return;
        }

        _history.Record(divided.Done);
        AfterHistoryChange();
        StatusHint.Text = divided.Said;
    }

    /// <summary>
    /// Juliet Balcony, on the doors and windows picked: a railing across each in front of its
    /// wall, from its sill, measured from the floor inside - those guarded already left as they are.
    /// </summary>
    private void OnJulietBalcony(object sender, RoutedEventArgs e)
    {
        var openings = Plan.SelectedElements.OfType<Opening>().ToList();
        var made = openings
            .Where(opening => !_document.All<Railing>().Any(railing => railing.HostOpeningId == opening.Id))
            .Select(opening => Railings.Juliet(_document, opening))
            .OfType<Railing>()
            .ToList();
        if (made.Count == 0)
        {
            StatusHint.Text = openings.Count == 0 ? "Pick a door or a window to put a Juliet balcony across." : "Each has a Juliet balcony already.";
            return;
        }

        _history.Execute(new CompositeCommand(made.Count == 1 ? "Juliet Balcony" : $"{made.Count} Juliet Balconies",
            made.Select(railing => (IUndoableCommand)new AddElementCommand(_document, railing, "Juliet Balcony")).ToList()));
        AfterHistoryChange();
        StatusHint.Text = $"{(made.Count == 1 ? "A Juliet balcony" : $"{made.Count} Juliet balconies")} across {(made.Count == 1 ? "it" : "them")}, {Units.FormatMm(Railings.TypeOf(_document, made[0]).Height)} high from the sill: {Railings.Verdict(_document, made[0])}.";
    }

    /// <summary>All Round, on the floor, balcony or roof terrace picked: one railing round every side of it.</summary>
    private void OnGuardAllRound(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements is [Slab slab]) Plan.GuardAllRound(slab);
    }

    /// <summary>Guard Edges, on the floor, balcony or roof terrace picked: railings round what drops too far.</summary>
    private void OnGuardEdges(object sender, RoutedEventArgs e)
    {
        if (Plan.SelectedElements is [Slab slab]) Plan.GuardEdges(slab);
    }

    private void OnStairOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (StairTypePicker.SelectedItem is StairType type) Plan.ActiveStairTypeId = type.Id;
        if (StairDirectionPicker.SelectedIndex >= 0) Plan.NewStairDirection = (StairDirection)StairDirectionPicker.SelectedIndex;
        if (StairShapePicker.SelectedItem is string shape && EnumText.TryParse<StairShape>(shape, out var picked)) Plan.NewStairShape = picked;
        Plan.NewStairCorner = StairCornerPicker.SelectedIndex switch { 1 => StairCorner.Winders, 2 => StairCorner.BalancedWinders, _ => StairCorner.Landing };
        Plan.NewStairTurnsLeft = StairTurnPicker.SelectedIndex == 1;
        if (TryLength(StairWidthBox.Text, out var width) && width is >= 500 and <= 5000) Plan.NewStairWidth = width;
        if (TryLength(StairRadiusBox.Text, out var radius) && radius is >= 0 and <= 10000) Plan.NewStairInnerRadius = radius;
        ShowStairOptions();
        Plan.InvalidateVisual();
    }

    private void OnStairWidthKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnStairOptionChanged(sender, e);
        e.Handled = true;
    }

    /// <summary>The ramp the next clicks put in, on the options bar.</summary>
    private void ShowRampOptions()
    {
        _loadingOptions = true;
        var types = _document.TypesOf<RampType>().OrderBy(type => type.Name).ToList();
        RampTypePicker.ItemsSource = types;
        RampTypePicker.SelectedItem = types.FirstOrDefault(type => type.Id == Plan.ActiveRampTypeId) ?? types.FirstOrDefault();
        Plan.ActiveRampTypeId = (RampTypePicker.SelectedItem as RampType)?.Id ?? Guid.Empty;
        RampDirectionPicker.ItemsSource ??= new[] { "Auto: down off an edge", "Up from here", "Down from here" };
        RampDirectionPicker.SelectedIndex = (int)Plan.NewRampDirection;
        RampShapePicker.ItemsSource ??= EnumText.Choices<RampShape>();
        RampShapePicker.SelectedItem = EnumText.Humanise(Plan.NewRampShape);
        RampTurnPicker.ItemsSource ??= new[] { "Turns right", "Turns left" };
        RampTurnPicker.SelectedIndex = Plan.NewRampTurnsLeft ? 1 : 0;
        RampTurnPicker.Visibility = Plan.NewRampShape is RampShape.QuarterTurn or RampShape.HalfTurn ? Visibility.Visible : Visibility.Collapsed;
        RampBuildingPicker.ItemsSource ??= new[] { "In a dwelling", "In another building" };
        RampBuildingPicker.SelectedIndex = Plan.NewRampBuilding == RailingBuilding.Other ? 1 : 0;
        RampWidthBox.Text = LengthText(Plan.NewRampWidth);
        RampGradientBox.Text = Plan.NewRampGradient.ToString("0.#");
        RampRiseBox.Text = Plan.NewRampRise is { } rise ? LengthText(rise) : "Auto";
        _loadingOptions = false;
    }

    private void OnRampOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        if (RampTypePicker.SelectedItem is RampType type) Plan.ActiveRampTypeId = type.Id;
        if (RampDirectionPicker.SelectedIndex >= 0) Plan.NewRampDirection = (StairDirection)RampDirectionPicker.SelectedIndex;
        if (RampShapePicker.SelectedItem is string shape && EnumText.TryParse<RampShape>(shape, out var picked)) Plan.NewRampShape = picked;
        Plan.NewRampTurnsLeft = RampTurnPicker.SelectedIndex == 1;

        // Another building asks 1.5 m between its kerbs: a ramp for one starts that wide.
        var building = RampBuildingPicker.SelectedIndex == 1 ? RailingBuilding.Other : RailingBuilding.Dwelling;
        if (building != Plan.NewRampBuilding && ReferenceEquals(sender, RampBuildingPicker))
            Plan.NewRampWidth = building == RailingBuilding.Other ? Math.Max(Plan.NewRampWidth, 1500) : Plan.NewRampWidth;
        Plan.NewRampBuilding = building;
        if (TryLength(RampWidthBox.Text, out var width) && width is >= 600 and <= 6000 && !ReferenceEquals(sender, RampBuildingPicker)) Plan.NewRampWidth = width;
        if (double.TryParse(RampGradientBox.Text, out var gradient) && gradient is >= 4 and <= 100) Plan.NewRampGradient = gradient;
        Plan.NewRampRise = TryLength(RampRiseBox.Text, out var rise) && rise >= Ramps.MinimumRise && rise <= 10000 ? rise : null;
        ShowRampOptions();
        Plan.InvalidateVisual();
    }

    private void OnRampOptionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnRampOptionChanged(sender, e);
        e.Handled = true;
    }

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

    /// <summary>The opening the Opening by Face tool cuts next, on the options bar.</summary>
    private void ShowFaceOpeningOptions()
    {
        _loadingOptions = true;
        FaceOpeningWidthBox.Text = Units.FormatLength(Plan.ActiveFaceOpeningWidth);
        FaceOpeningLengthBox.Text = Units.FormatLength(Plan.ActiveFaceOpeningLength);
        _loadingOptions = false;
    }

    private void OnFaceOpeningOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;

        static double? Read(string text) =>
            ParameterFormatter.TryParse(ParameterDataType.Length, text, out var value) && value is double number ? number : null;

        if (Read(FaceOpeningWidthBox.Text) is { } width and >= RoofOpenings.SmallestSize) Plan.ActiveFaceOpeningWidth = width;
        if (Read(FaceOpeningLengthBox.Text) is { } length and >= RoofOpenings.SmallestSize) Plan.ActiveFaceOpeningLength = length;
        ShowFaceOpeningOptions();
    }

    private void OnFaceOpeningOptionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        OnFaceOpeningOptionChanged(sender, e);
        Plan.Focus();
        e.Handled = true;
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
        roofs = roofs.Concat(roofs.SelectMany(roof => _document.All<Roof>().Where(dormer => dormer.JoinedTo == roof.Id))).Distinct().ToList();

        var gutters = selected.OfType<Gutter>()
            .Concat(_document.All<Gutter>().Where(gutter => roofs.Any(roof => roof.Id == gutter.RoofId)))
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

        var noun = Plan.SketchTarget switch { SketchTarget.Floor => "floor", SketchTarget.Ceiling => "ceiling", _ => "roof" };
        var answer = MessageBox.Show(this,
            $"Finish the {noun} sketch before switching tools?\n\nYes makes the {noun}, No throws the sketch away, Cancel keeps sketching.",
            $"{char.ToUpperInvariant(noun[0])}{noun[1..]} sketch", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

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
            {
                if (Plan.SketchTarget == SketchTarget.Roof) Plan.SketchOverhang = millimetres;
                else Plan.SketchSlabOffset = millimetres;
            }
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
        {
            if (Plan.SketchTool == RoofSketchTool.Fillet)
            {
                if (millimetres > 0) Plan.SketchFilletRadius = millimetres;
            }
            else Plan.SketchOffset = Math.Abs(millimetres);
        }

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

        var copy = RoofOffsetCopyBox.IsChecked == true;
        switch (Plan.SketchTool)
        {
            case RoofSketchTool.Move: Plan.SketchMoveCopy = copy; break;
            case RoofSketchTool.Mirror: Plan.SketchMirrorCopy = copy; break;
            default: Plan.SketchOffsetCopy = copy; break;
        }

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
        PlanTool.Floor or PlanTool.FloorByFace => Plan.ActiveFloorTypeId,
        PlanTool.Ceiling => Plan.ActiveCeilingTypeId,
        _ => Plan.ActiveRoofTypeId
    };

    private void OnActiveSlabTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || Plan is null) return;
        if (SlabTypePicker.SelectedItem is not SlabType type) return;

        switch (Plan.ActiveTool)
        {
            case PlanTool.Floor or PlanTool.FloorByFace: Plan.ActiveFloorTypeId = type.Id; break;
            case PlanTool.Ceiling: Plan.ActiveCeilingTypeId = type.Id; break;
            case PlanTool.Roof or PlanTool.RoofExtrusion or PlanTool.RoofByFace: Plan.ActiveRoofTypeId = type.Id; break;
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

    /// <summary>File > New Project: the New Project steps, and the project they make opened in place of this one.</summary>
    private void OnNewProject(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardingChanges()) return;
        var steps = new StartUp.NewProjectWindow { Owner = this };
        if (steps.ShowDialog() == true && steps.Choice is { } choice) Begin(choice);
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
            Infrastructure.Diagnostics.AppLog.Info($"Opened {dialog.FileName}.");
        }
        catch (ProjectFileException exception)
        {
            Infrastructure.Diagnostics.AppLog.Warning($"Could not open {dialog.FileName}.", exception);
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
            Infrastructure.Diagnostics.AppLog.Warning($"Could not save {path}.", exception);
            MessageBox.Show(this, exception.Message, "Could not save project",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _path = path;
        _history.MarkClean();
        Remember();

        // Saved: the recovery copy is not wanted any more.
        _recovery.Discard();
        Infrastructure.Diagnostics.AppLog.Info($"Saved {path}.");
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

        // A callout undone away: back to the whole plan.
        if (Plan.ActiveCallout is { } open && !_document.Elements.Contains(open)) Plan.ActiveCallout = null;
        if (Section.Callout is { } detail && !_document.Elements.Contains(detail)) Section.Callout = null;

        Plan.RefreshModel();
        ShowDetailLevel();
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
        SelectTool, WallTool, DoorTool, WindowTool, RoomTool, RoomSeparatorTool, ComponentTool, ColumnTool, FloorTool, CeilingTool, RoofTool, RoofExtrusionTool, DormerTool,
        FasciaTool, GutterTool, SoffitTool, EaveTool, RoofWindowTool, ShaftTool, FaceOpeningTool, DownpipeTool, RoofDrainTool, ChimneyTool, StairTool, RampTool, RailingTool, BeamTool, PipeTool, DuctTool, TerrainTool,
        StructuralColumnTool, WallFootingTool, PadFootingTool,
        RegionTool, JoistsTool, PadTool, PropertyLineTool, SpotElevationTool, GridTool,
        SectionTool, DimensionTool, TagTool, TextTool, SplitTool, SplitGapTool, TrimTool, AlignTool, OffsetTool, MirrorTool, ArrayTool,
        RotateTool, ScaleTool, PaintTool, SplitFaceTool,
        SweepTool, RevealTool, WallJoinsTool, JoinGeometryTool, JoinRoofTool, WallOpeningTool, SlabEdgeTool,
        MassTool, WallByFaceTool, CurtainByFaceTool, FloorByFaceTool, RoofByFaceTool, CameraTool, WalkthroughTool, InteriorElevationTool, CalloutTool,
        AngularDimensionTool, RadialDimensionTool, DiameterDimensionTool, ArcLengthDimensionTool, KeynoteTool, RevisionCloudTool
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

        // The three radial tools are one tool measuring three things.
        if (ReferenceEquals(sender, RadialDimensionTool)) Plan.ActiveRadialKind = RadialKind.Radius;
        if (ReferenceEquals(sender, DiameterDimensionTool)) Plan.ActiveRadialKind = RadialKind.Diameter;
        if (ReferenceEquals(sender, ArcLengthDimensionTool)) Plan.ActiveRadialKind = RadialKind.ArcLength;

        // Picking another tool with a sketch open asks what to do with the sketch first.
        if (Plan.IsSketching && SketchToolButton().IsChecked != true && !LeaveRoofSketch())
        {
            _loadingOptions = true;
            foreach (var other in ToolButtons().Where(button => button is not null)) other.IsChecked = false;
            SketchToolButton().IsChecked = true;
            _loadingOptions = false;
            return;
        }

        Plan.SetTool(
            WallTool.IsChecked == true ? PlanTool.Wall
            : DoorTool.IsChecked == true ? PlanTool.Door
            : WindowTool.IsChecked == true ? PlanTool.Window
            : RoomTool.IsChecked == true ? PlanTool.Room
            : RoomSeparatorTool.IsChecked == true ? PlanTool.RoomSeparator
            : ComponentTool.IsChecked == true ? PlanTool.Component
            : ColumnTool.IsChecked == true ? PlanTool.Column
            : FloorTool.IsChecked == true ? PlanTool.Floor
            : CeilingTool.IsChecked == true ? PlanTool.Ceiling
            : RoofTool.IsChecked == true ? PlanTool.Roof
            : RoofExtrusionTool.IsChecked == true ? PlanTool.RoofExtrusion
            : DormerTool.IsChecked == true ? PlanTool.Dormer
            : FasciaTool.IsChecked == true ? PlanTool.Fascia
            : GutterTool.IsChecked == true ? PlanTool.Gutter
            : MassTool.IsChecked == true ? PlanTool.Mass
            : WallByFaceTool.IsChecked == true ? PlanTool.WallByFace
            : CurtainByFaceTool.IsChecked == true ? PlanTool.CurtainByFace
            : FloorByFaceTool.IsChecked == true ? PlanTool.FloorByFace
            : RoofByFaceTool.IsChecked == true ? PlanTool.RoofByFace
            : SlabEdgeTool.IsChecked == true ? PlanTool.SlabEdge
            : SoffitTool.IsChecked == true ? PlanTool.Soffit
            : StructuralColumnTool.IsChecked == true ? PlanTool.StructuralColumn
            : WallFootingTool.IsChecked == true ? PlanTool.WallFooting
            : PadFootingTool.IsChecked == true ? PlanTool.PadFooting
            : EaveTool.IsChecked == true ? PlanTool.Eave
            : RoofWindowTool.IsChecked == true ? PlanTool.RoofWindow
            : ShaftTool.IsChecked == true ? PlanTool.Shaft
            : CameraTool.IsChecked == true ? PlanTool.Camera
            : WalkthroughTool.IsChecked == true ? PlanTool.Walkthrough
            : InteriorElevationTool.IsChecked == true ? PlanTool.InteriorElevation
            : CalloutTool.IsChecked == true ? PlanTool.Callout
            : FaceOpeningTool.IsChecked == true ? PlanTool.FaceOpening
            : DownpipeTool.IsChecked == true ? PlanTool.Downpipe
            : RoofDrainTool.IsChecked == true ? PlanTool.RoofDrain
            : ChimneyTool.IsChecked == true ? PlanTool.Chimney
            : StairTool.IsChecked == true ? PlanTool.Stair
            : RampTool.IsChecked == true ? PlanTool.Ramp
            : RailingTool.IsChecked == true ? PlanTool.Railing
            : BeamTool.IsChecked == true ? PlanTool.Beam
            : PipeTool.IsChecked == true ? PlanTool.Pipe
            : DuctTool.IsChecked == true ? PlanTool.Duct
            : TerrainTool.IsChecked == true ? PlanTool.Terrain
            : RegionTool.IsChecked == true ? PlanTool.Region
            : JoistsTool.IsChecked == true ? PlanTool.Joists
            : PadTool.IsChecked == true ? PlanTool.Pad
            : PropertyLineTool.IsChecked == true ? PlanTool.PropertyLine
            : SpotElevationTool.IsChecked == true ? PlanTool.SpotElevation
            : GridTool.IsChecked == true ? PlanTool.Grid
            : SectionTool.IsChecked == true ? PlanTool.Section
            : DimensionTool.IsChecked == true ? PlanTool.Dimension
            : AngularDimensionTool.IsChecked == true ? PlanTool.AngularDimension
            : RadialDimensionTool.IsChecked == true || DiameterDimensionTool.IsChecked == true || ArcLengthDimensionTool.IsChecked == true ? PlanTool.RadialDimension
            : TagTool.IsChecked == true ? PlanTool.Tag
            : KeynoteTool.IsChecked == true ? PlanTool.Keynote
            : RevisionCloudTool.IsChecked == true ? PlanTool.RevisionCloud
            : TextTool.IsChecked == true ? PlanTool.Text
            : SplitTool.IsChecked == true ? PlanTool.Split
            : SplitGapTool.IsChecked == true ? PlanTool.SplitGap
            : PaintTool.IsChecked == true ? PlanTool.Paint
            : SplitFaceTool.IsChecked == true ? PlanTool.SplitFace
            : TrimTool.IsChecked == true ? PlanTool.Trim
            : OffsetTool.IsChecked == true ? PlanTool.Offset
            : AlignTool.IsChecked == true ? PlanTool.Align
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

    /// <summary>
    /// The detail level of the view shown - a plan, a callout - from the ribbon or the view
    /// control bar: kept by the view, as one step to undo.
    /// </summary>
    private void SetDetailLevel(DetailLevel level)
    {
        if (Plan.EffectiveDetailLevel != level && Locked(Plan.CurrentView, ViewTemplateSetting.DetailLevel))
        {
            ShowDetailLevel();
            return;
        }

        if (Plan.EffectiveDetailLevel != level)
        {
            _history.Execute(new SetViewDetailLevelCommand(_document, Plan.CurrentView, level));
            Plan.RefreshModel();
        }

        ShowDetailLevel();
    }

    /// <summary>The ribbon and the view control bar showing the detail level of the view shown.</summary>
    private void ShowDetailLevel()
    {
        var level = Plan.EffectiveDetailLevel;
        _loadingOptions = true;
        PlanDetailPicker.SelectedIndex = (int)level;
        _loadingOptions = false;

        DetailCoarse.IsChecked = level == DetailLevel.Coarse;
        DetailMedium.IsChecked = level == DetailLevel.Medium;
        DetailFine.IsChecked = level == DetailLevel.Fine;
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
            case Key.L:
                AlignTool.IsChecked = true;
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
        if (ColumnTypePicker.SelectedItem is not ColumnType type) return;
        if (type.Structural) Plan.ActiveStructuralColumnTypeId = type.Id;
        else Plan.ActiveColumnTypeId = type.Id;
    }

    /// <summary>The column types on the options bar: the structural ones for the structural Column tool, the architectural ones for the other.</summary>
    private void LoadColumnTypes(bool structural)
    {
        _loadingOptions = true;
        var types = _document.TypesOf<ColumnType>().Where(type => type.Structural == structural).OrderBy(type => type.Name).ToList();
        var active = structural ? Plan.ActiveStructuralColumnTypeId : Plan.ActiveColumnTypeId;
        ColumnTypePicker.ItemsSource = types;
        // A structural column starts as a house's usually is: a small universal column in England, a
        // 3 in steel pipe - a lally column - in the US; not whichever sorts first.
        var us = _document.ProjectInformation.ClearanceStandard.IsUs();
        ColumnTypePicker.SelectedItem = types.FirstOrDefault(type => type.Id == active)
                                        ?? (structural ? StartingType(types, us ? new[] { "CHS 88.9", "W8" } : new[] { "UC 152 x 152 x 23", "UC" }) : types.FirstOrDefault());
        _loadingOptions = false;
        if (ColumnTypePicker.SelectedItem is ColumnType chosen)
        {
            if (structural) Plan.ActiveStructuralColumnTypeId = chosen.Id;
            else Plan.ActiveColumnTypeId = chosen.Id;
        }
    }

    /// <summary>The first of these names a type starts with, of those there are - or the first type, where none does.</summary>
    private static T? StartingType<T>(IReadOnlyList<T> types, IEnumerable<string> names) where T : ElementType =>
        names.Select(name => types.FirstOrDefault(type => type.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(type => type is not null)
        ?? types.FirstOrDefault();

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

    private void OnNewFamily(object sender, RoutedEventArgs e) => NewFamily(loadModel: false);

    private void OnLoadFamilyModel(object sender, RoutedEventArgs e) => NewFamily(loadModel: true);

    /// <summary>A family of your own made in the family editor, added as one step, and picked to be put down.</summary>
    private void NewFamily(bool loadModel)
    {
        var editor = new FamilyEditorWindow(_document, null, loadModel) { Owner = this };
        if (editor.ShowDialog() != true) return;

        var type = editor.Family.NewType(_document);
        _history.Execute(new AddTypeCommand(_document, type));
        AfterTypesChanged();

        ComponentTool.IsChecked = true;
        ComponentTypePicker.SelectedItem = ComponentTypePicker.Items.OfType<ComponentFamilyChoice>().FirstOrDefault(choice => choice.Type == type);
        StatusHint.Text = $"{type.Name} made and picked in the Component tool: click in the plan to put one down. Edit Type → Edit Family... changes it.";
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
        ShowDetailLevel();
        RefreshNewWallTopChoices();
        OnNewWallHeightChanged(this, new RoutedEventArgs());
        RefreshProjectBrowser();
        RefreshProperties();
        StatusHint.Text = $"Showing {level.Name} at {Units.FormatLength(level.Elevation)}.";

        _blockedClearances.Clear();
        RefreshClearances(announce: false);
    }

    // ---- clearances ------------------------------------------------------------

    /// <summary>The blocked clearances last counted, so only one that has just become blocked is called out.</summary>
    private HashSet<(Guid Fitting, string What)> _blockedClearances = new();

    private void OnToggleClearances(object sender, RoutedEventArgs e) => ShowClearances(ClearancesMenuItem.IsChecked == true);

    private void OnPlanClearancesToggled(object sender, RoutedEventArgs e) => ShowClearances(PlanClearancesToggle.IsChecked == true);

    private void ShowClearances(bool show)
    {
        ClearancesMenuItem.IsChecked = PlanClearancesToggle.IsChecked = show;
        Plan.ShowClearances = show;

        _blockedClearances.Clear();
        var blocked = RefreshClearances(announce: false);
        var standard = Clearances.Label(_document.ProjectInformation.ClearanceStandard);
        StatusHint.Text = !show
            ? "Clearances hidden."
            : blocked.Count == 0
                ? $"Clearances shown as {standard} asks: nothing is in the way."
                : $"Clearances shown as {standard} asks: {blocked.Count} with something in the way, in red. {blocked[0].Message}";
    }

    /// <summary>
    /// Checks the clear space round the fittings on the plan's storey while it is shown: counts
    /// those blocked on the status bar, and calls out one that has just become blocked.
    /// </summary>
    private IReadOnlyList<BlockedClearance> RefreshClearances(bool announce = true)
    {
        if (!Plan.ShowClearances)
        {
            StatusClearances.Visibility = Visibility.Collapsed;
            return Array.Empty<BlockedClearance>();
        }

        var blocked = Clearances.Blocked(_document, Plan.ActiveLevelId, _document.ProjectInformation.ClearanceStandard);
        StatusClearances.Content = $"⚠ {blocked.Count} {(blocked.Count == 1 ? "clearance" : "clearances")} blocked";
        StatusClearances.Visibility = blocked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (announce && blocked.FirstOrDefault(item => !_blockedClearances.Contains((item.Zone.Element.Id, item.Zone.What))) is { } fresh)
            StatusHint.Text = "⚠ " + fresh.Message;

        _blockedClearances = blocked.Select(item => (item.Zone.Element.Id, item.Zone.What)).ToHashSet();
        return blocked;
    }

    private void OnClearanceWarnings(object sender, RoutedEventArgs e)
    {
        var blocked = RefreshClearances(announce: false);
        if (blocked.Count == 0) return;

        MessageBox.Show(this, string.Join("\n\n", blocked.Select(item => item.Message)),
            $"Clearances - {Clearances.Label(_document.ProjectInformation.ClearanceStandard)}",
            MessageBoxButton.OK, MessageBoxImage.Warning);
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

        // A stair hung by its head from a terrace or a balcony stays hung there, laid out back from it in its new type.
        _history.Execute(new StairRelaidCommand(_document, targets.OfType<Stair>(), new SetElementsTypeCommand(targets, type)));
        AfterHistoryChange();
        StatusHint.Text = targets.Count == 1 ? $"Now {type.Name}." : $"{targets.Count} now {type.Name}.";
    }

    // ---- attaching walls ---------------------------------------------------------------

    private void OnAttachTops(object sender, RoutedEventArgs e)
    {
        if (!AttachSelectedColumns(top: true)) AttachSelectedWalls(top: true);
    }

    private void OnAttachBases(object sender, RoutedEventArgs e)
    {
        if (SetSelectedStoreyOnItsFloor()) return;
        if (PutSelectedFurnitureOnFloors()) return;
        if (!AttachSelectedColumns(top: false)) AttachSelectedWalls(top: false);
    }

    /// <summary>
    /// Where the selected walls are on a storey whose floor is the slab laid on the storey below,
    /// sets the storey itself on that slab: its level goes to the slab's top, and everything on
    /// it - walls, the roof over them, furniture - rises with it, keeping its own height. Returns
    /// whether it did.
    /// </summary>
    private bool SetSelectedStoreyOnItsFloor()
    {
        var found = SelectedWallsOrHosts().Select(wall => FloorSurfaces.StoreyFloor(_document, wall)).FirstOrDefault(pair => pair is not null);
        if (found is not var (level, floor)) return false;

        var before = level.Elevation;
        _history.Execute(FloorSurfaces.SetOn(_document, new[] { level }, floor));
        AfterHistoryChange();
        StatusHint.Text = $"{level.Name} now stands on the top of {_document.FindType<ElementType>(floor.TypeId)?.Name ?? "the floor"}: " +
                          $"up {Core.Units.Millimetres(level.Elevation - before, "0")} to {(Core.Units.Imperial ? Core.Units.FeetAndInches(level.Elevation) : $"{level.Elevation:0}")}, with everything on it - each storey keeps its own height, and follows the floor however thick it is made.";
        return true;
    }

    /// <summary>
    /// Puts the selected furniture down on the top of the floor under it, where that floor - or
    /// the flat roof that is the storey's floor - is built up above its level. Returns whether
    /// any furniture was selected to act on.
    /// </summary>
    private bool PutSelectedFurnitureOnFloors()
    {
        var furniture = Plan.SelectedElements.OfType<BIMDesigner.Core.Architecture.Component>().ToList();
        if (furniture.Count == 0) return false;

        if (FloorSurfaces.PutOnFloors(_document, furniture) is not { } command)
        {
            StatusHint.Text = "There is no floor built up above its level under it: it stands on its level already.";
            return true;
        }

        _history.Execute(command);
        AfterHistoryChange();
        StatusHint.Text = "Put down on the floor under it - it follows the floor however thick it is made.";
        return true;
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

        // A wall hanging from the wall under its storey's floor is let go, to stand on that floor.
        var hanging = top ? new List<Wall>() : walls.Where(wall => WallAttachments.BelowItsFloor(_document, wall)).ToList();

        var changes = walls.Except(hanging)
            .Select(wall => (Wall: wall, Top: top, Target: AttachmentTarget(wall, top)))
            .Where(change => change.Target is not null)
            .Select(change => (change.Wall, change.Top, change.Target))
            .Concat(hanging.Select(wall => (wall, false, (Guid?)null)))
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

        // On a storey standing on its floor, nothing under that floor is stood on.
        if (_document.FindLevel(wall.LevelId) is { StandsOn: not null } storey)
        {
            if (below is not null && below.GetTopElevation(_document) < storey.Elevation - 1) below = null;
            if (floor is not null && floor.GetTopElevation(_document) < storey.Elevation - 1) floor = null;
        }

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
            $"{Plural(_document.All<Column>().Count(c => c.TypeId == type.Id), "column")} changed with it.";
    }

    /// <summary>
    /// The walls selected - and for a curtain panel, the curtain wall it is part of: a panel is
    /// what a click on the glass picks, and it is the wall that has a grid and a top to attach.
    /// </summary>
    private List<Wall> SelectedWallsOrHosts() => Plan.SelectedElements
        .Select(element => element switch
        {
            Wall wall => wall,
            CurtainPanel panel => _document.Find<Wall>(panel.HostWallId),
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
            new SetCurtainLayoutCommand(wall, dialog.ResultGrid, DoorSwing.Settled(_document, wall, dialog.ResultGrid, dialog.ResultPanels), mullions: dialog.ResultMullions)
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

        // Framing for walls and their frames; parts for walls and the parts made of them.
        var framingOrWalls = selected.All(element => element is Wall or WallFraming);
        ContextFramingPanel.Visibility = framingOrWalls ? Visibility.Visible : Visibility.Collapsed;
        ContextFoundationPanel.Visibility = selected.Any(element => element is Wall || element is Column { Structural: true }) ? Visibility.Visible : Visibility.Collapsed;
        ContextFrameWalls.Content = selected.OfType<Wall>().Any() && selected.OfType<Wall>().All(wall => WallFramings.Of(_document, wall) is not null)
            ? "Edit Frame" : "Frame Walls";
        ContextFrameWalls.Visibility = selected.OfType<Wall>().Any() ? Visibility.Visible : Visibility.Collapsed;
        ContextRemoveFraming.IsEnabled = selected.OfType<WallFraming>().Any() || selected.OfType<Wall>().Any(wall => WallFramings.Of(_document, wall) is not null);
        ContextPartsPanel.Visibility = selected.All(element => element is Wall or Part) ? Visibility.Visible : Visibility.Collapsed;
        ContextCreateParts.Visibility = selected.OfType<Wall>().Any() ? Visibility.Visible : Visibility.Collapsed;
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
        ContextSlabPanel.Visibility = selected is [Floor] or [Ceiling] ? Visibility.Visible : Visibility.Collapsed;
        ContextMassPanel.Visibility = selected is [Mass] ? Visibility.Visible : Visibility.Collapsed;
        ContextEditProfile.Visibility = selected is [Roof { IsExtrusion: true }] ? Visibility.Visible : Visibility.Collapsed;
        ContextAddFascia.Visibility = ContextAddGutters.Visibility = ContextAddSoffits.Visibility = ContextAddEaves.Visibility =
            selected is [Roof { IsExtrusion: false }] || dormerPicked ? Visibility.Visible : Visibility.Collapsed;
        ContextAddRoofWindow.Visibility = selected is [Roof { IsExtrusion: false }] ? Visibility.Visible : Visibility.Collapsed;
        ContextAddShaft.Visibility = ContextAddRoofWindow.Visibility;
        ContextAddRoofDrain.Visibility = selected is [Roof flatRoof] && RoofDrainage.CanDrain(_document, flatRoof) ? Visibility.Visible : Visibility.Collapsed;
        ContextColumnPanel.Visibility = selected.Count > 0 && selected.All(element => element is Column)
            ? Visibility.Visible : Visibility.Collapsed;
        ContextPersonPanel.Visibility = selected is [Core.Architecture.Component someone] && Walker.IsPerson(_document.FindType<ComponentType>(someone.TypeId))
            ? Visibility.Visible : Visibility.Collapsed;
        ContextGuardPanel.Visibility = selected is [Slab walkedOn] && walkedOn is not Ceiling && FloorSurfaces.IsStoodOn(_document, walkedOn)
            ? Visibility.Visible : Visibility.Collapsed;
        ContextStoreyPanel.Visibility = ContextGuardPanel.Visibility;
        ContextGroundPanel.Visibility = selected is [Toposurface] ? Visibility.Visible : Visibility.Collapsed;
        ContextForgetGround.IsEnabled = selected is [Toposurface { ExistingPoints.Count: >= 3 }];
        // Pick New belongs to anything that is carried by something else: a door or window in
        // its wall, and a component on a face it could be moved off.
        var rehostable = selected is [Opening]
                         || (selected is [BIMDesigner.Core.Architecture.Component component]
                             && _document.FindType<ComponentType>(component.TypeId) is { IsHosted: true });

        ContextHostPanel.Visibility = rehostable ? Visibility.Visible : Visibility.Collapsed;
        ContextJulietPanel.Visibility = selected.Count > 0 && selected.All(element => element is Opening) ? Visibility.Visible : Visibility.Collapsed;
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

        // A component, door, window, chimney, gutter and the like: Type Properties, with Duplicate.
        if (TypeDuplicates.CanDuplicate(type))
        {
            ShowTypeProperties(type);
            return;
        }

        // Other types have no dialog of their own: their type parameters are edited in place.
        TypeParameterList.BringIntoView();
        StatusHint.Text = $"{type.Name}: edit its type parameters in the Properties panel. A change there applies to every one of this type.";
    }

    /// <summary>
    /// Mullion Types: the project's mullion types to edit, duplicate and rename - starting at the
    /// one between the panels of the selected curtain wall, where one is selected.
    /// </summary>
    private void OnMullionTypes(object sender, RoutedEventArgs e)
    {
        var start = Plan.SelectedElements.OfType<Wall>()
                        .Select(wall => _document.FindType<CurtainWallType>(wall.TypeId))
                        .OfType<CurtainWallType>()
                        .SelectMany(type => type.MullionTypes(_document))
                        .FirstOrDefault(type => _document.FindType<MullionType>(type.Id) is not null)
                    ?? _document.TypesOf<MullionType>().OrderBy(type => type.Name).FirstOrDefault();
        if (start is null)
        {
            StatusHint.Text = "This project has no mullion types.";
            return;
        }

        var dialog = new TypePropertiesWindow(_document, _history, start, Array.Empty<Element>()) { Owner = this };
        dialog.Changed += (_, _) => AfterTypesChanged();
        dialog.ShowDialog();
        AfterTypesChanged();
        RefreshProperties();
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

    /// <summary>
    /// Type Properties for the selection's type: the type's parameters, and Duplicate to make one
    /// of the selection's own - given to it, and to nothing else.
    /// </summary>
    private void ShowTypeProperties(ElementType type)
    {
        var dialog = new TypePropertiesWindow(_document, _history, type, Plan.SelectedElements.ToList()) { Owner = this };
        dialog.Changed += (_, _) => AfterTypesChanged();
        dialog.ShowDialog();
        AfterTypesChanged();
        RefreshProperties();
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

        // The other tools' type lists take in a type duplicated or renamed, keeping what each has picked.
        static void Keep<T>(ComboBox picker, IReadOnlyList<T> items, Func<T, Guid> id)
        {
            var picked = picker.SelectedItem is T current ? id(current) : Guid.Empty;
            picker.ItemsSource = items;
            picker.SelectedItem = items.FirstOrDefault(item => id(item) == picked) ?? items.FirstOrDefault();
        }

        Keep(DoorTypePicker, _document.TypesOf<DoorType>().Where(t => ProjectTemplates.Fits(_document, t)).OrderBy(t => t.Name).ToList(), t => t.Id);
        Keep(WindowTypePicker, _document.TypesOf<WindowType>().Where(t => ProjectTemplates.Fits(_document, t)).OrderBy(t => t.Name).ToList(), t => t.Id);
        Keep(RoofWindowTypePicker, _document.TypesOf<RoofWindowType>().OrderBy(t => t.Name).ToList(), t => t.Id);
        Keep(ComponentTypePicker, _document.TypesOf<ComponentType>().Where(t => ProjectTemplates.Fits(_document, t)).OrderBy(t => t.Kind.ToString()).ThenBy(t => t.Name)
            .Select(t => new ComponentFamilyChoice(t)).ToList(), choice => choice.Type.Id);

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

        // A group picked shows as the group, unless what is in it has been asked for from the list since.
        var group = selected.OfType<Group>().Take(2).ToList() is [var only] ? only : null;
        if (group is not null && !ReferenceEquals(group, _propertyGroup)) _propertyCategory = BuiltInCategory.ModelGroups;
        _propertyGroup = group;

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
        SelectedTypeName.Text = element is Group shownGroup && elements.Count == 1 ? shownGroup.Name : typeIds.Count > 1 ? "Multiple Types" : type?.Name ?? CategoryTitle(category);

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
        var typeRows = type is null ? null : BuildGroupedRows(new[] { (IReadOnlyList<ParameterValue>)type.AllTypeParameters(_document).ToList() });
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

    /// <summary>The group the panel last showed, so a choice of what is in it from the list is kept while it stays picked.</summary>
    private Group? _propertyGroup;

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
        // The view being worked in: the plan or a callout of it, a 3D view, or a section, an
        // elevation or an interior elevation - each with its own properties and template.
        var view = CurrentViewReference();
        var levelName = _document.FindLevel(Plan.ActiveLevelId)?.Name ?? "Floor Plan";
        var plan = view.Kind == ViewKind.FloorPlan || (view.Kind == ViewKind.Callout && ViewTemplates.KindOf(_document, view) == ViewTemplateKind.Plan);
        var threeD = view.Kind == ViewKind.Model3D;
        var (family, icon) = view.Kind switch
        {
            ViewKind.Model3D => ("3D View", "Icon.View3D"),
            ViewKind.Section => ("Section", "Icon.Section"),
            ViewKind.Elevation => ("Elevation", "Icon.InteriorElevation"),
            ViewKind.Callout => ("Callout", "Icon.Callout"),
            _ => ("Floor Plan", "Icon.Plan")
        };
        var name = view.Kind == ViewKind.FloorPlan ? levelName : view.TitleIn(_document);

        SelectedTypeImage.Source = TryFindResource(icon) as System.Windows.Media.ImageSource;
        SelectedFamilyName.Text = family;
        SelectedTypeName.Text = name;
        SelectedTypePicker.Visibility = Visibility.Collapsed;

        _loadingOptions = true;
        PropertyCategoryPicker.ItemsSource = new[] { $"{family}: {name}" };
        PropertyCategoryPicker.SelectedIndex = 0;
        PropertyCategoryPicker.IsEnabled = false;
        _loadingOptions = false;

        EditTypeButton.IsEnabled = false;
        var own = threeD ? ModelViewParameters() : plan ? PlanViewParameters(levelName) : OtherViewParameters(view);
        InstanceParameterList.ItemsSource = BuildGroupedRows(new[] { (IReadOnlyList<ParameterValue>)own.Concat(PhasingParameters(view)).ToList() });
        TypeSection.Visibility = Visibility.Collapsed;
        StructureSection.Visibility = Visibility.Collapsed;
    }

    /// <summary>A section's, elevation's or detail callout's own properties: its name, the template governing it, its scale.</summary>
    private IReadOnlyList<ParameterValue> OtherViewParameters(ViewReference view)
    {
        var scales = ViewScale.Common.Select(d => new ViewScale(d).ToString()).ToList();
        var scale = ParameterValue.BindChoiceCommand(ViewScaleParameter,
            () => new ViewScale(_document.ViewSettings.ScaleOf(view)).ToString(),
            text => new SetViewScaleCommand(_document, view, ViewScale.Common[scales.IndexOf(text)]),
            scales);

        return new[]
        {
            ParameterValue.ReadOnly(ViewNameParameter, () => view.TitleIn(_document)),
            ViewTemplateChoice(view),
            ViewTemplates.Locks(_document, view, ViewTemplateSetting.Scale) ? ParameterValue.ReadOnly(ViewScaleParameter, () => scale.DisplayValue) : scale
        };
    }

    /// <summary>With nothing selected, the view clicked into shows its properties.</summary>
    private void OnViewFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Plan.SelectedElements.Count == 0) Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshProperties);
    }

    private static readonly ParameterDefinition ViewScaleParameter = new("View Scale", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition DetailLevelParameter = new("Detail Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition JoinDisplayParameter = new("Wall Join Display", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition UnderlayParameter = new("Show Storey Below", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition ClearancesParameter = new("Show Clearances", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition ClearanceStandardParameter = new("Clearance Standard", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition SprinkleredParameter = new("Sprinklers Throughout", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>Whether planning makes Approved Document G's optional requirement a condition: 110 litres a person a day, not 125.</summary>
    private static readonly ParameterDefinition WaterOptionalParameter = new("Water Use 110 l (Optional Requirement)", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition GroundParameter = new("Ground", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition ClayShrinkageParameter = new("Clay Shrinkage", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition FrostDepthParameter = new("Frost Line (US, 0 = none)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition GroundSnowParameter = new("Ground Snow Load (US, psf)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>The water's static pressure where it comes in, that the IRC sizes a US house's pipes by (Table P2903.7).</summary>
    private static readonly ParameterDefinition WaterPressureParameter = new("Water Pressure (US, psi)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>The wind's pressure on the cladding, that curtain wall mullions are checked against: from the wind map and the building's height and exposure.</summary>
    private static readonly ParameterDefinition WindPressureParameter = new("Wind Pressure on Cladding (kPa, with no wind speed)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition WindPressurePsfParameter = new("Wind Pressure on Cladding (psf, with no wind speed)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>The site's basic wind speed, which the wind on its cladding is worked out from: the UK map's in m/s (BS EN 1991-1-4 NA Figure NA.1), the US's in mph (ASCE 7 Figure 26.5-1).</summary>
    private static readonly ParameterDefinition WindSpeedParameter = new("Basic Wind Speed (UK m/s, US mph; 0 = not set)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);

    private static readonly ParameterDefinition WindSpeedMphParameter = new("Basic Wind Speed (mph; 0 = not set)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);

    private static readonly ParameterDefinition SiteAltitudeParameter = new("Site Altitude (m above sea level)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition SiteAltitudeFeetParameter = new("Site Altitude (ft above sea level)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);

    private static readonly ParameterDefinition TerrainParameter = new("Terrain Upwind", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>What the heat loss is worked to: the UK place whose design temperature it takes, the US jurisdiction's winter design temperature, how airtight the building is.</summary>
    private static readonly ParameterDefinition HeatingLocationParameter = new("Heating Design Location (UK)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition WinterDesignParameter = new("Winter Design Temperature (US, °F)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>What a US house's cooling load is worked to: the summer's 1% dry bulb and coincident wet bulb, its daily range, the site's latitude and true north on the plan.</summary>
    private static readonly ParameterDefinition SummerDesignParameter = new("Summer Design Temperature (US, °F, 1% dry bulb)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition SummerWetBulbParameter = new("Summer Wet Bulb (US, °F, coincident)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition DailyRangeParameter = new("Summer Daily Range (US, °F)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition LatitudeParameter = new("Latitude (US, °N)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition TrueNorthParameter = new("True North (° clockwise from up the plan)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>Where a US house's heating and cooling ducts run, how well insulated and sealed - what its loads add for them (ASHRAE Table 6) - and its cooling load.</summary>
    private static readonly ParameterDefinition DuctRunParameter = new("Ducts Run In (US)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition DuctInsulationParameter = new("Duct Insulation (US, R)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition DuctsSealedParameter = new("Ducts Sealed to 5% (US)", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition CoolingLoadParameter = new("Cooling Load (US)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition AirPermeabilityParameter = new("Air Permeability (m³/h·m² at 50 Pa)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition AirPermeabilityCfmParameter = new("Air Leakage (cfm a sq ft of envelope at 50 Pa)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>A cfm through a square foot of envelope for one m³/h through a square metre.</summary>
    private const double CfmPerSquareFootPerPermeability = 1 / 3.6 / Core.Units.LitresPerSecondPerCfm / 10.76391;
    private static readonly ParameterDefinition HeatLoadParameter = new("Heat Load", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);

    private static readonly ParameterDefinition WindVerdictParameter = new("Wind on Cladding", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);

    /// <summary>A UK heating design location as the plan's Properties offer it: the place, its station and its design temperature.</summary>
    private static string HeatingLocationLabel(Core.Mep.HeatingLocation place) =>
        Core.Mep.Climate.Of(place) is var station ? $"{place} ({station.Name}, {station.Design:0.0} °C)" : place.ToString();

    /// <summary>What hangs on the heat loss, shown again after what it is worked to changes.</summary>
    private void HeatingChanged()
    {
        _systemsWindow?.Refresh();
        _heatLossWindow?.Refresh();
        _energyRatingWindow?.Refresh();
    }

    /// <summary>The ground as the plan's Properties offer it: what it is, Approved Document A's type, the IRC's bearing value.</summary>
    internal static string GroundLabel(GroundType ground) =>
        $"{char.ToUpperInvariant(StructureRules.Describe(ground)[0])}{StructureRules.Describe(ground)[1..]} (type {StructureRules.AdaType(ground)}" +
        (StructureRules.Bearing(ground) is { } psf ? $", {psf:#,0} psf)" : ", soils investigation)");
    private static readonly ParameterDefinition DwellingSizeParameter = new("Dwelling Size (NDSS)", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition VisualStyleParameter = new("Visual Style", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition EdgesParameter = new("Show Edges", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    private static readonly ParameterDefinition LevelParameter = new("Associated Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    private static readonly ParameterDefinition ViewNameParameter = new("View Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    // The settings only one country's rules read: an English project is not asked its frost line
    // or its water pressure, nor a US one its clay's shrinkage or the space standard its homes meet.
    private static readonly HashSet<ParameterDefinition> UsOnlyParameters = new()
    {
        FrostDepthParameter, GroundSnowParameter, WaterPressureParameter, WinterDesignParameter,
        SummerDesignParameter, SummerWetBulbParameter, DailyRangeParameter, LatitudeParameter,
        DuctRunParameter, DuctInsulationParameter, DuctsSealedParameter, CoolingLoadParameter
    };

    /// <summary>A temperature in °F as typed - "95", "95 °F" - or null for nothing, or what is not one.</summary>
    private static double? Fahrenheit(string? text, double least, double most)
    {
        var said = (text ?? string.Empty).Trim().TrimEnd('F', 'f', '°', ' ');
        return double.TryParse(said, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var value) && value > least && value < most ? value : null;
    }

    private static readonly HashSet<ParameterDefinition> EnglishOnlyParameters = new()
    {
        WaterOptionalParameter, ClayShrinkageParameter, HeatingLocationParameter, DwellingSizeParameter
    };

    private IReadOnlyList<ParameterValue> PlanViewParameters(string levelName)
    {
        var view = Plan.CurrentView;
        var scales = ViewScale.Common.Select(d => new ViewScale(d).ToString()).ToList();
        var details = EnumText.Choices<DetailLevel>();

        // What the view's template sets is shown, not changed, here.
        var locks = (Func<ViewTemplateSetting, bool>)(setting => ViewTemplates.Locks(_document, view, setting));
        var scale = ParameterValue.BindChoice(ViewScaleParameter,
            () => new ViewScale(_document.ViewSettings.ScaleOf(view)).ToString(),
            text => { _document.ViewSettings.SetScale(view, ViewScale.Common[scales.IndexOf(text)]); Plan.RefreshModel(); },
            scales);
        var detail = ParameterValue.BindChoice(DetailLevelParameter,
            () => EnumText.Humanise(Plan.EffectiveDetailLevel),
            text => { if (EnumText.TryParse<DetailLevel>(text, out var level)) SetDetailLevel(level); },
            details);
        var joins = ParameterValue.BindChoice(JoinDisplayParameter,
            () => _document.ViewSettings.JoinDisplayOf(view) == WallJoinDisplay.CleanAllWallJoins ? "Clean all wall joins" : "Clean same type wall joins",
            text => { _document.ViewSettings.SetJoinDisplay(view, text == "Clean all wall joins" ? WallJoinDisplay.CleanAllWallJoins : WallJoinDisplay.CleanSameTypeWallJoins); Plan.RefreshModel(); },
            new[] { "Clean all wall joins", "Clean same type wall joins" });

        var us = _document.ProjectInformation.ClearanceStandard.IsUs();
        var feet = Core.Units.Imperial;
        var shown = new[]
        {
            ViewTemplateChoice(view),
            locks(ViewTemplateSetting.Scale) ? ParameterValue.ReadOnly(ViewScaleParameter, () => scale.DisplayValue) : scale,
            locks(ViewTemplateSetting.DetailLevel) ? ParameterValue.ReadOnly(DetailLevelParameter, () => detail.DisplayValue) : detail,
            locks(ViewTemplateSetting.JoinDisplay) ? ParameterValue.ReadOnly(JoinDisplayParameter, () => joins.DisplayValue) : joins,
            ParameterValue.Bind(UnderlayParameter, () => Plan.ShowUnderlay, show =>
            {
                UnderlayMenuItem.IsChecked = PlanUnderlayToggle.IsChecked = show;
                Plan.ShowUnderlay = show;
            }),
            ParameterValue.Bind(ClearancesParameter, () => Plan.ShowClearances, ShowClearances),
            ParameterValue.BindChoice(ClearanceStandardParameter,
                () => Clearances.Label(_document.ProjectInformation.ClearanceStandard),
                text =>
                {
                    _document.ProjectInformation.ClearanceStandard = Enum.GetValues<ClearanceStandard>().FirstOrDefault(standard => Clearances.Label(standard) == text);
                    Plan.RefreshModel();
                    if (Plan.ShowClearances) ShowClearances(true);

                    // The other country's settings in place of these.
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshProperties);
                },
                Enum.GetValues<ClearanceStandard>().Select(Clearances.Label).ToArray()),
            ParameterValue.Bind(SprinkleredParameter, () => _document.ProjectInformation.Sprinklered, sprinklered =>
            {
                _document.ProjectInformation.Sprinklered = sprinklered;
                Plan.RefreshModel();
                if (Plan.ShowClearances) ShowClearances(true);
            }),
            ParameterValue.Bind(WaterOptionalParameter, () => _document.ProjectInformation.WaterOptionalRequirement, optional =>
            {
                _document.ProjectInformation.WaterOptionalRequirement = optional;
                _systemsWindow?.Refresh();
            }),
            ParameterValue.BindChoice(GroundParameter,
                () => GroundLabel(_document.ProjectInformation.Ground),
                text =>
                {
                    _document.ProjectInformation.Ground = Enum.GetValues<GroundType>().FirstOrDefault(ground => GroundLabel(ground) == text, _document.ProjectInformation.Ground);
                    Plan.RefreshModel();
                    if (Plan.ShowClearances) ShowClearances(true);
                },
                Enum.GetValues<GroundType>().Select(GroundLabel).ToArray()),
            ParameterValue.BindChoice(ClayShrinkageParameter,
                () => EnumText.Humanise(_document.ProjectInformation.ClayShrinkage),
                text =>
                {
                    if (!EnumText.TryParse<ClayShrinkage>(text, out var shrinkage)) return;
                    _document.ProjectInformation.ClayShrinkage = shrinkage;
                    Plan.RefreshModel();
                    if (Plan.ShowClearances) ShowClearances(true);
                },
                EnumText.Choices<ClayShrinkage>()),
            ParameterValue.Bind(FrostDepthParameter, () => _document.ProjectInformation.FrostDepth, depth =>
            {
                _document.ProjectInformation.FrostDepth = Math.Max(0, depth);
                Plan.RefreshModel();
            }),
            ParameterValue.Bind(GroundSnowParameter, () => _document.ProjectInformation.GroundSnowLoad, snow =>
            {
                _document.ProjectInformation.GroundSnowLoad = Math.Max(0, snow);
                Plan.RefreshModel();
            }),
            ParameterValue.Bind(WaterPressureParameter, () => _document.ProjectInformation.WaterPressure, pressure =>
            {
                _document.ProjectInformation.WaterPressure = Math.Clamp(pressure, 1, 200);
                if (Plan.ShowClearances) ShowClearances(true);
            }),
            ParameterValue.Bind(us ? WindSpeedMphParameter : WindSpeedParameter, () => _document.ProjectInformation.BasicWindSpeed, speed =>
            {
                _document.ProjectInformation.BasicWindSpeed = Math.Clamp(speed, 0, 300);
                if (Plan.ShowClearances) ShowClearances(true);
            }),

            // Kept in metres; given in feet in feet and inches.
            ParameterValue.Bind(feet ? SiteAltitudeFeetParameter : SiteAltitudeParameter,
                () => feet ? Math.Round(_document.ProjectInformation.SiteAltitude * 1000 / Core.Units.MillimetresPerFoot) : _document.ProjectInformation.SiteAltitude, altitude =>
            {
                _document.ProjectInformation.SiteAltitude = Math.Clamp(feet ? altitude * Core.Units.MillimetresPerFoot / 1000 : altitude, -500, 5000);
                if (Plan.ShowClearances) ShowClearances(true);
            }),
            ParameterValue.BindChoice(TerrainParameter,
                () => EnumText.Humanise(_document.ProjectInformation.Terrain),
                text =>
                {
                    if (!EnumText.TryParse<WindTerrain>(text, out var terrain)) return;
                    _document.ProjectInformation.Terrain = terrain;
                    if (Plan.ShowClearances) ShowClearances(true);
                },
                EnumText.Choices<WindTerrain>()),
            ParameterValue.BindChoice(HeatingLocationParameter,
                () => HeatingLocationLabel(_document.ProjectInformation.HeatingLocation),
                text =>
                {
                    _document.ProjectInformation.HeatingLocation = Enum.GetValues<Core.Mep.HeatingLocation>().FirstOrDefault(place => HeatingLocationLabel(place) == text, _document.ProjectInformation.HeatingLocation);
                    HeatingChanged();
                },
                Enum.GetValues<Core.Mep.HeatingLocation>().Select(HeatingLocationLabel).ToArray()),
            ParameterValue.Bind(WinterDesignParameter,
                () => _document.ProjectInformation.WinterDesignTemperature is { } winter ? $"{winter:0.#}" : "Not set",
                text =>
                {
                    var said = (text ?? string.Empty).Trim().TrimEnd('F', 'f', '°', ' ');
                    _document.ProjectInformation.WinterDesignTemperature = double.TryParse(said, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var winter) && winter is > -80 and < 80 ? winter : null;
                    HeatingChanged();
                }),
            ParameterValue.Bind(SummerDesignParameter,
                () => _document.ProjectInformation.SummerDesignTemperature is { } summer ? $"{summer:0.#}" : "Not set",
                text =>
                {
                    _document.ProjectInformation.SummerDesignTemperature = Fahrenheit(text, 40, 140);
                    HeatingChanged();
                }),
            ParameterValue.Bind(SummerWetBulbParameter,
                () => _document.ProjectInformation.SummerWetBulb is { } wet ? $"{wet:0.#}" : "Not set",
                text =>
                {
                    _document.ProjectInformation.SummerWetBulb = Fahrenheit(text, 20, 100);
                    HeatingChanged();
                }),
            ParameterValue.Bind(DailyRangeParameter, () => _document.ProjectInformation.DailyRange, range =>
            {
                _document.ProjectInformation.DailyRange = Math.Clamp(range, 0, 60);
                HeatingChanged();
            }),
            ParameterValue.Bind(LatitudeParameter, () => _document.ProjectInformation.Latitude, latitude =>
            {
                _document.ProjectInformation.Latitude = Math.Clamp(latitude, 0, 70);
                HeatingChanged();
            }),
            ParameterValue.Bind(TrueNorthParameter, () => _document.ProjectInformation.TrueNorth, north =>
            {
                _document.ProjectInformation.TrueNorth = (north % 360 + 360) % 360;
                HeatingChanged();
            }),
            ParameterValue.BindChoice(DuctRunParameter,
                () => EnumText.Humanise(_document.ProjectInformation.DuctRun),
                text =>
                {
                    if (!EnumText.TryParse<Core.Mep.DuctRun>(text, out var run)) return;
                    _document.ProjectInformation.DuctRun = run;
                    HeatingChanged();
                },
                EnumText.Choices<Core.Mep.DuctRun>()),
            ParameterValue.Bind(DuctInsulationParameter, () => _document.ProjectInformation.DuctInsulation, r =>
            {
                _document.ProjectInformation.DuctInsulation = Math.Clamp(r, 0, 30);
                HeatingChanged();
            }),
            ParameterValue.Bind(DuctsSealedParameter, () => _document.ProjectInformation.DuctsSealed, sealed_ =>
            {
                _document.ProjectInformation.DuctsSealed = sealed_;
                HeatingChanged();
            }),
            ParameterValue.ReadOnly(CoolingLoadParameter, () => Core.Mep.CoolingLoads.Of(_document) is { } cooling
                ? $"{Core.Units.Watts(cooling.Total, "#,0")} ({cooling.Tons:0.0} tons): {cooling.Sensible * Core.Units.BtuPerHourPerWatt:#,0} sensible, {cooling.Latent * Core.Units.BtuPerHourPerWatt:#,0} latent"
                : "No summer design temperature set"),
            ParameterValue.Bind(feet ? AirPermeabilityCfmParameter : AirPermeabilityParameter,
                () => feet ? Math.Round(_document.ProjectInformation.AirPermeability * CfmPerSquareFootPerPermeability, 3) : _document.ProjectInformation.AirPermeability, permeability =>
            {
                var kept = feet ? permeability / CfmPerSquareFootPerPermeability : permeability;
                if (kept is > 0 and <= 100) _document.ProjectInformation.AirPermeability = kept;
                HeatingChanged();
            }),
            ParameterValue.ReadOnly(HeatLoadParameter, () => Core.Mep.HeatLoss.Of(_document) is { } building
                ? feet
                    ? $"{Core.Units.Watts(building.Total, "#,0")} - {building.Fabric * Core.Units.BtuPerHourPerWatt:#,0} through the surfaces, {building.Ventilation * Core.Units.BtuPerHourPerWatt:#,0} with the air"
                    : $"{building.Total:#,0} W - {building.Fabric:#,0} through the surfaces, {building.Ventilation:#,0} with the air"
                : Core.Mep.Climate.Describe(_document)),
            // Kept in kPa; given in psf in feet and inches.
            ParameterValue.Bind(feet ? WindPressurePsfParameter : WindPressureParameter,
                () => feet ? Math.Round(_document.ProjectInformation.WindPressure * Core.Units.PoundsPerSquareFootPerKilopascal, 1) : _document.ProjectInformation.WindPressure, pressure =>
            {
                _document.ProjectInformation.WindPressure = Math.Clamp(feet ? pressure / Core.Units.PoundsPerSquareFootPerKilopascal : pressure, 0.1, 10);
                if (Plan.ShowClearances) ShowClearances(true);
            }),
            ParameterValue.ReadOnly(WindVerdictParameter, () => Wind.Verdict(_document)),
            ParameterValue.ReadOnly(DwellingSizeParameter, () => Dwellings.Verdict(_document) ?? "No bedrooms to size it by"),
            ParameterValue.ReadOnly(LevelParameter, () => levelName),
            ParameterValue.ReadOnly(ViewNameParameter, () => $"Floor Plan: {levelName}")
        };

        var others = us ? EnglishOnlyParameters : UsOnlyParameters;
        return shown.Where(parameter => !others.Contains(parameter.Definition)).ToArray();
    }

    /// <summary>The 3D view's own properties: its name, style, perspective, section box and camera, and whether edges are drawn.</summary>
    private IReadOnlyList<ParameterValue> ModelViewParameters()
    {
        var view = Model3D.View;
        if (!IsKept(view)) return new[] { ParameterValue.ReadOnly(ViewNameParameter, () => view.Name) };

        var parameters = view.GetInstanceParameters(_document)
            .Where(parameter => parameter.Name is not "Mark" and not "Phase Created" and not "Phase Demolished" and not "Workset")
            .ToList();
        parameters.Add(ParameterValue.Bind(EdgesParameter, () => view.ShowEdges, show => view.ShowEdges = show));

        // The {3D} view keeps its name.
        if (ReferenceEquals(view, _document.Default3D))
            parameters[0] = ParameterValue.ReadOnly(ViewNameParameter, () => view.Name);

        // Its template, and what that sets shown, not changed, here.
        var reference = Model3D.Reference;
        if (ViewTemplates.Locks(_document, reference, ViewTemplateSetting.VisualStyle))
            parameters = parameters.Select(parameter => parameter.Name == View3DParameters.Style.Name ? ParameterValue.ReadOnly(View3DParameters.Style, () => parameter.DisplayValue) : parameter).ToList();
        parameters.Insert(1, ViewTemplateChoice(reference));
        return parameters;
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
        Column { Structural: true } => "Icon.StructuralColumn",
        WallFooting => "Icon.WallFooting",
        PadFooting => "Icon.PadFooting",
        Eave => "Icon.Eave",
        RoofWindow => "Icon.RoofWindow",
        ShaftOpening => "Icon.Shaft",
        Railing => "Icon.Railing",
        Beam => "Icon.Beam",
        Toposurface => "Icon.Terrain",
        GroundRegion => "Icon.Region",
        BeamSystem => "Icon.Joists",
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
        ColumnType { Structural: true } => "Structural Column",
        FootingType { Kind: FootingKind.Pad } => "Isolated Foundation",
        FootingType => "Wall Foundation",
        EaveType => "Eave",
        MullionType { Shape: MullionShape.CornerL } => "L Corner Mullion",
        MullionType { Shape: MullionShape.CornerV } => "V Corner Mullion",
        MullionType { Shape: MullionShape.CornerTrapezoid } => "Trapezoid Corner Mullion",
        MullionType { Shape: MullionShape.CornerQuad } => "Quad Corner Mullion",
        MullionType { Shape: MullionShape.Circular } => "Circular Mullion",
        MullionType => "Rectangular Mullion",
        RoofWindowType => "Roof Window",
        ChimneyType => "Chimney",
        WallSweepType => "Wall Sweep",
        _ => category == BuiltInCategory.CurtainPanels ? "Curtain Panel" : Singular(CategoryTitle(category))
    };

    /// <summary>One of a category: "Masses" a mass, "Stairs" a stair, "Model Groups" a model group - not "Masse".</summary>
    private static string Singular(string plural) =>
        plural.EndsWith("sses", StringComparison.Ordinal) ? plural[..^2]
        : plural.EndsWith("ies", StringComparison.Ordinal) ? plural[..^3] + "y"
        : plural.EndsWith('s') && !plural.EndsWith("ss", StringComparison.Ordinal) ? plural[..^1]
        : plural;

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
        IUndoableCommand change = commands.Count == 1 ? commands[0] : new CompositeCommand(commands[0].Name, commands);

        // A floor moved, or a wall raised, through one another - its height or its level changed,
        // nothing else: the wall split at the floor, and a floor with a room over it made a storey,
        // in the same step.
        var heights = new[]
        {
            WallParameters.BaseConstraint.Name, WallParameters.BaseOffset.Name, WallParameters.TopConstraint.Name, WallParameters.TopOffset.Name,
            WallParameters.UnconnectedHeight.Name, SlabParameters.Level.Name, SlabParameters.HeightOffset.Name
        };
        var divided = e.Changes.Any(edit => heights.Contains(edit.Parameter.Name))
            ? Storeys.Divide(_document, Plan.SelectedElements.Where(element => element is Wall or Slab))
            : null;
        if (divided is { } split) change = new CompositeCommand(change.Name, new[] { change, split.Done });
        _history.Record(change);
        if (divided is { } done) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => StatusHint.Text = done.Said);

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
        PlanTitle.Text = Plan.ActiveCallout is { } open
            ? $"Callout: {open.Name}"
            : _document.FindLevel(Plan.ActiveLevelId) is { } shown
                ? Plan.ActiveDiscipline == Core.Mep.Discipline.Architectural ? $"Floor Plan: {shown.Name}" : $"{Core.Mep.ServicesPlans.Title(Plan.ActiveDiscipline)}: {shown.Name}"
                : "Floor Plan";
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

            var isActive = level.Id == Plan.ActiveLevelId && Plan.ActiveDiscipline == Core.Mep.Discipline.Architectural;

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

                // The whole plan, not a callout of it - and the storey's own, not one of its services plans.
                Plan.ActiveCallout = null;
                Plan.ActiveDiscipline = Core.Mep.Discipline.Architectural;
                LevelPicker.SelectedItem = picked;
                ShowPlanView();
                args.Handled = true;
            };

            plans.Items.Add(item);
        }

        // Sections sit beside the floor plans, because they are views of the project in
        // exactly the same sense: a cut through the model, made when it is looked at.
        var sections = new TreeViewItem { Header = "Sections", IsExpanded = true };

        foreach (var marker in _document.All<SectionMarker>().OrderBy(s => s.Name))
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

        // The four elevations, the building seen straight on from each side; then each interior
        // elevation marker's views, one for each of its arrows.
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

        foreach (var marker in _document.All<InteriorElevationMarker>().OrderBy(marker => marker.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var arrow in marker.Arrows)
            {
                var reference = ViewReference.InteriorElevation(marker.Id, arrow);
                var item = new TreeViewItem { Header = marker.ViewName(arrow), Tag = marker };
                item.MouseLeftButtonUp += (_, args) =>
                {
                    ShowInteriorElevation(reference);
                    args.Handled = true;
                };
                elevations.Items.Add(item);
            }

        // The drawing set. Clicking a sheet opens it, the way clicking a plan switches storey.
        var sheets = new TreeViewItem { Header = "Sheets", IsExpanded = true };

        foreach (var sheet in _document.All<Sheet>()
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
        project.Items.Add(ServicesPlansItem());
        project.Items.Add(elevations);

        // The {3D} view, the cameras' and those saved by name; and the walkthroughs.
        var views3D = new TreeViewItem { Header = "3D Views", IsExpanded = true };
        foreach (var view in Views3D.All(_document))
        {
            var item = new TreeViewItem { Header = view.Perspective && view.Camera is { } camera && !ReferenceEquals(view, _document.Default3D) ? $"{view.Name}  —  camera" : view.Name, Tag = view };
            item.MouseLeftButtonUp += (_, args) =>
            {
                Show3D(view);
                args.Handled = true;
            };
            item.ContextMenu = ViewMenu(view);
            views3D.Items.Add(item);
        }

        project.Items.Add(views3D);

        var walkthroughs = new TreeViewItem { Header = "Walkthroughs", IsExpanded = true };
        foreach (var walk in _document.All<Walkthrough>().OrderBy(walk => walk.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new TreeViewItem { Header = $"{walk.Name}  —  {Core.Units.Metres(walk.Length, "0.0")}, {walk.Duration:0} s", Tag = walk };
            item.MouseLeftButtonUp += (_, args) =>
            {
                ShowWalkthrough(walk);
                args.Handled = true;
            };
            item.ContextMenu = ViewMenu(walk);
            walkthroughs.Items.Add(item);
        }

        if (walkthroughs.Items.Count > 0) project.Items.Add(walkthroughs);

        // Callouts: the parts of plans and sections drawn larger as views of their own.
        var callouts = new TreeViewItem { Header = "Callouts", IsExpanded = true };
        foreach (var callout in _document.All<Callout>().Where(c => Callouts.Reference(c).ExistsIn(_document)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new TreeViewItem
            {
                Header = $"{callout.Name}  —  {callout.Parent.TitleIn(_document)}, {new ViewScale(_document.ViewSettings.ScaleOf(Callouts.Reference(callout)))}",
                Tag = callout,
                FontWeight = ReferenceEquals(Plan.ActiveCallout, callout) ? FontWeights.SemiBold : FontWeights.Normal
            };
            item.MouseLeftButtonUp += (_, args) =>
            {
                ShowCallout(callout);
                args.Handled = true;
            };
            callouts.Items.Add(item);
        }

        if (callouts.Items.Count > 0) project.Items.Add(callouts);

        if (sections.Items.Count > 0) project.Items.Add(sections);

        // The schedules: clicking one opens it; what else can be done to it, on its menu.
        var schedules = new TreeViewItem { Header = "Schedules/Quantities", IsExpanded = false };
        foreach (var schedule in _document.Schedules.OrderBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new TreeViewItem { Header = schedule.Name, Tag = schedule, ContextMenu = ScheduleMenu(schedule) };
            item.MouseLeftButtonUp += (_, args) =>
            {
                ShowSchedule(schedule);
                args.Handled = true;
            };
            schedules.Items.Add(item);
        }

        project.Items.Add(schedules);
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

        var sheets = _document.All<Sheet>().ToList();

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
        var sheets = _document.All<Sheet>()
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

        // Each storey's services plans, once there are services to draw on them.
        if (_document.All<Core.Architecture.Component>().Any(component => Core.Mep.MepSystems.IsFitting(_document, component)))
            foreach (var discipline in new[] { Core.Mep.Discipline.Mechanical, Core.Mep.Discipline.Electrical, Core.Mep.Discipline.Plumbing })
                foreach (var level in _document.Levels.OrderByDescending(level => level.Elevation))
                {
                    var plan = Core.Mep.ServicesPlans.Plan(level.Id, discipline);
                    options.Add(new SheetViewOption(plan.TitleIn(_document), plan));
                }

        foreach (var marker in _document.All<SectionMarker>().OrderBy(marker => marker.Name))
            options.Add(new SheetViewOption($"Section {marker.Name}", ViewReference.Section(marker.Id)));

        foreach (var side in Elevations.All)
            options.Add(new SheetViewOption(Elevations.Name(side), ViewReference.Elevation(side)));

        foreach (var marker in _document.All<InteriorElevationMarker>().OrderBy(marker => marker.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var arrow in marker.Arrows)
                options.Add(new SheetViewOption($"Interior {marker.ViewName(arrow)}", ViewReference.InteriorElevation(marker.Id, arrow)));

        foreach (var view in Views3D.All(_document))
            options.Add(new SheetViewOption($"3D: {view.Name}", Views3D.Reference(_document, view)));

        foreach (var callout in _document.All<Callout>().OrderBy(callout => callout.Name, StringComparer.OrdinalIgnoreCase))
            if (Callouts.Reference(callout).ExistsIn(_document))
                options.Add(new SheetViewOption($"{callout.Name} ({callout.Parent.TitleIn(_document)})", Callouts.Reference(callout)));

        foreach (var schedule in _document.Schedules.OrderBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase))
            options.Add(new SheetViewOption($"Schedule: {schedule.Name}", ViewReference.Schedule(schedule)));

        options.Add(new SheetViewOption("Keynote Legend (by sheet)", ViewReference.KeynoteLegend(bySheet: true)));
        options.Add(new SheetViewOption("Keynote Legend (whole project)", ViewReference.KeynoteLegend(bySheet: false)));

        var current = (SheetViewPicker.SelectedItem as SheetViewOption)?.Display;

        _loadingSheets = true;
        SheetViewPicker.ItemsSource = options;
        // To start with, the plan being worked on - not the first storey listed, which may be empty.
        var working = _document.FindLevel(Plan.ActiveLevelId) is { } open ? $"{open.Name} Plan" : null;
        SheetViewPicker.SelectedItem = options.FirstOrDefault(option => option.Display == current)
                                       ?? options.FirstOrDefault(option => option.Display == working)
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

        // A schedule, or a legend, carries its own title, in the table.
        var viewport = new Viewport { View = option.View, Scale = scale, ShowTitle = option.View.Kind is not (ViewKind.Schedule or ViewKind.KeynoteLegend) };
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

    /// <summary>Opens a 3D view - the {3D} view unless another is named - looking as it was left.</summary>
    private void Show3D(View3D? view = null) => ShowModel(null, view ?? (ModelPanel.Visibility == Visibility.Visible && Model3D.Elevation is null && _shownWalkthrough is null && IsKept(Model3D.View) ? Model3D.View : _document.Default3D));

    /// <summary>
    /// Opens an elevation: the model straight on from one side, flat, with the levels and the
    /// gridlines facing it drawn across it. It pans and zooms; it does not turn.
    /// </summary>
    private void ShowElevation(ElevationSide side) => ShowModel(side, ElevationView(side));

    /// <summary>Each elevation's own view: what it leaves out is its own, but it is not kept by name.</summary>
    private readonly Dictionary<ElevationSide, View3D> _elevationViews = new();

    private View3D ElevationView(ElevationSide side) =>
        _elevationViews.TryGetValue(side, out var view) ? view : _elevationViews[side] = new View3D { Name = Elevations.Name(side), Perspective = false };

    /// <summary>The walkthrough open in the 3D view, and the frame it is on.</summary>
    private Walkthrough? _shownWalkthrough;
    private int _walkFrame;
    private readonly Dictionary<Guid, View3D> _walkViews = new();

    /// <summary>Whether a view is one of the project's, kept by name, rather than an elevation's or a walkthrough's.</summary>
    private bool IsKept(View3D view) => ReferenceEquals(view, _document.Default3D) || _document.Elements.Contains(view);

    /// <summary>Opens a walkthrough on its first frame, ready to play.</summary>
    private void ShowWalkthrough(Walkthrough walk)
    {
        if (!_walkViews.TryGetValue(walk.Id, out var view)) _walkViews[walk.Id] = view = new View3D { Name = walk.Name, Perspective = true };
        ShowModel(null, view, walk);
        _walkFrame = 0;
        Model3D.ShowFrame(walk, 0);
    }

    private void ShowModel(ElevationSide? elevation, View3D view, Walkthrough? walk = null)
    {
        // The sheet and the 3D view both take over the drawing area; only one can.
        if (SheetPanel.Visibility == Visibility.Visible) OnCloseSheet(this, new RoutedEventArgs());

        Model3D.StopMoving();
        _shownWalkthrough = walk;
        Model3D.Elevation = elevation;
        Model3D.View = view;
        ModelCaption.Text = elevation is { } side ? Elevations.Name(side) : walk?.Name ?? view.Name;
        ModelHelp.Text = walk is not null
            ? "Play, or step through it a key frame at a time"
            : elevation is null
                ? "Drag to orbit  ·  right-drag to pan  ·  scroll to zoom  ·  click to select"
                : "Drag to pan  ·  scroll to zoom  ·  click to select";
        Cube.Visibility = elevation is null && walk is null ? Visibility.Visible : Visibility.Collapsed;

        ModelPanel.Visibility = Visibility.Visible;
        ModelMenuItem.IsChecked = true;

        Model3D.Rebuild();
        SyncModelOptions();
        Model3D.SetSelection(Plan.SelectedElements.Select(element => element.Id));

        // Fitted round the model only when the view has never been looked through.
        var fit = view.Camera is null && walk is null;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (fit) Model3D.ZoomToFit();
            Model3D.Focus();
        });

        StatusHint.Text = elevation is { } shown
            ? $"{Elevations.Name(shown)}. Drag to pan, scroll to zoom, click to select."
            : walk is not null
                ? $"{walk.Name}: {walk.Frames} frames at {walk.FramesPerSecond:0} a second. Play it, or step through it."
                : $"{view.Name}. Drag to orbit, right-drag to pan, scroll to zoom, click to select; Walk to walk through it.";
        RefreshProperties();
    }

    /// <summary>The view bar set from the view shown: its style, perspective, section box, and what it shows.</summary>
    private void SyncModelOptions()
    {
        var view = Model3D.View;
        var elevation = Model3D.Elevation is not null;
        var walk = _shownWalkthrough is not null;

        _loadingOptions = true;
        VisualStylePicker.SelectedIndex = (int)view.Style;
        PerspectiveBox.IsChecked = view.Perspective;
        SectionBoxToggle.IsChecked = view.SectionBoxOn;
        WalkToggle.IsChecked = Model3D.IsWalking;
        ShowRoofsBox.IsChecked = Model3D.IsKindVisible(MeshKind.Roof);
        ShowCeilingsBox.IsChecked = Model3D.IsKindVisible(MeshKind.Ceiling);
        ShowFloorsBox.IsChecked = Model3D.IsKindVisible(MeshKind.Floor);
        ShowWallsBox.IsChecked = Model3D.IsKindVisible(MeshKind.Wall);
        ShowFramingBox.IsChecked = Model3D.IsKindVisible(MeshKind.Framing);
        ShowStairsBox.IsChecked = Model3D.IsKindVisible(MeshKind.Stair);
        ShowRailingsBox.IsChecked = Model3D.IsKindVisible(MeshKind.Railing);
        ShowRampsBox.IsChecked = Model3D.IsKindVisible(MeshKind.Ramp);
        ShowBeamsBox.IsChecked = Model3D.IsKindVisible(MeshKind.Beam);
        ShowTerrainBox.IsChecked = Model3D.IsKindVisible(MeshKind.Terrain);
        ShowGroundBox.IsChecked = view.ShowGround;
        ShowEdgesBox.IsChecked = view.ShowEdges;
        _loadingOptions = false;

        // An elevation is flat and whole; a walkthrough is its path.
        var turning = elevation || walk ? Visibility.Collapsed : Visibility.Visible;
        PerspectiveBox.Visibility = SectionBoxToggle.Visibility = BoxSelectionButton.Visibility = WalkToggle.Visibility = WalkAsPicker.Visibility = SaveViewButton.Visibility = turning;
        WalkthroughBar.Visibility = walk ? Visibility.Visible : Visibility.Collapsed;
        RefreshStoreyToggles();
    }

    /// <summary>A change to the view shown, as one step to undo.</summary>
    private void ChangeView(Action<View3D> change, string name)
    {
        var command = new SetView3DCommand(Model3D.View, change, name);
        if (!command.Changes) return;
        _history.Execute(command);
        AfterHistoryChange();
    }

    private void OnPerspectiveChanged(object sender, RoutedEventArgs e) =>
        ChangeView(view => view.Perspective = PerspectiveBox.IsChecked == true, "Perspective");

    /// <summary>The section box on or off: round everything shown the first time.</summary>
    private void OnToggleSectionBox(object sender, RoutedEventArgs e)
    {
        var on = SectionBoxToggle.IsChecked == true;
        var box = Model3D.View.SectionBox ?? Model3D.ModelBox;
        ChangeView(view =>
        {
            view.SectionBox = box;
            view.SectionBoxOn = on && box is not null;
        }, "Section Box");
        StatusHint.Text = on ? "Section box on: pull its faces in by the handles on them to cut into the building." : "Section box off. Its box is kept for when it is turned on again.";
    }

    /// <summary>A section box round what is selected: Revit's Selection Box.</summary>
    private void OnBoxSelection(object sender, RoutedEventArgs e)
    {
        var selected = Plan.SelectedElements.Select(element => element.Id).ToHashSet();
        if (selected.Count == 0)
        {
            StatusHint.Text = "Select what to box first: the section box goes round it.";
            return;
        }

        if (ModelPanel.Visibility != Visibility.Visible || Model3D.Elevation is not null || _shownWalkthrough is not null) Show3D();
        if (Model3D.BoxAround(selected) is not { } box)
        {
            StatusHint.Text = "What is selected is not drawn in 3D.";
            return;
        }

        ChangeView(view =>
        {
            view.SectionBox = box;
            view.SectionBoxOn = true;
        }, "Selection Box");
        StatusHint.Text = "Section box round what is selected. Pull its faces by their handles; Section Box turns it off.";
    }

    private void OnToggleWalk(object sender, RoutedEventArgs e)
    {
        if (Model3D.IsWalking)
        {
            Model3D.StopWalking();
            return;
        }

        if (Model3D.StartWalking() is { } why)
        {
            StatusHint.Text = why;
            WalkToggle.IsChecked = false;
            return;
        }

        StatusHint.Text = "Walking: W A S D or the arrows, Shift to hurry, drag to look round, the wheel for a stride. Esc stops.";
    }

    /// <summary>Walk and Play show what is going on.</summary>
    private void SyncMoving()
    {
        LeavePersonWhereWalked();
        WalkToggle.IsChecked = Model3D.IsWalking;
        PlayButton.Content = Model3D.Playing is not null ? "Stop" : "Play";
        ModelHelp.Text = Model3D.IsWalking
            ? "W A S D or the arrows to walk  ·  Shift to hurry  ·  drag to look round  ·  Esc to stop"
            : _shownWalkthrough is not null
                ? "Play, or step through it a key frame at a time"
                : "Drag to orbit  ·  right-drag to pan  ·  scroll to zoom  ·  click to select";
    }

    /// <summary>The view kept by a name of its own: what is shown, as it is now.</summary>
    private void OnSave3DView(object sender, RoutedEventArgs e)
    {
        if (Model3D.Elevation is not null || _shownWalkthrough is not null) return;
        var copy = Views3D.Duplicate(_document, Model3D.View);
        _history.Execute(new AddElementCommand(_document, copy, "Save View"));
        AfterHistoryChange();
        Show3D(copy);
        StatusHint.Text = $"Kept as {copy.Name}, under 3D Views. Rename it in Properties; place it on a sheet with Place View.";
    }

    /// <summary>What can be done with a view or walkthrough from the Project Browser.</summary>
    private ContextMenu? ViewMenu(Element view)
    {
        if (view is View3D shown && ReferenceEquals(shown, _document.Default3D))
        {
            var only = new ContextMenu();
            var copy = new MenuItem { Header = "Duplicate" };
            copy.Click += (_, _) => DuplicateView(shown);
            only.Items.Add(copy);
            return only;
        }

        var menu = new ContextMenu();
        if (view is View3D named)
        {
            var copy = new MenuItem { Header = "Duplicate" };
            copy.Click += (_, _) => DuplicateView(named);
            menu.Items.Add(copy);
        }

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) =>
        {
            if (ModelPanel.Visibility == Visibility.Visible && (ReferenceEquals(Model3D.View, view) || ReferenceEquals(_shownWalkthrough, view))) Show3D(_document.Default3D);
            _history.Execute(new DeleteElementCommand(_document, view));
            AfterHistoryChange();
        };
        menu.Items.Add(delete);
        return menu;
    }

    private void DuplicateView(View3D view)
    {
        var copy = Views3D.Duplicate(_document, view);
        _history.Execute(new AddElementCommand(_document, copy, "Duplicate View"));
        AfterHistoryChange();
        Show3D(copy);
    }

    // ---- walkthroughs ------------------------------------------------------------------

    private void ShowFrameNumber(int frame)
    {
        _walkFrame = frame;
        if (_shownWalkthrough is { } walk) FrameText.Text = $"Frame {frame + 1} of {walk.Frames}";
    }

    private void OnPlayWalkthrough(object sender, RoutedEventArgs e)
    {
        if (_shownWalkthrough is not { } walk) return;
        if (Model3D.Playing is not null)
        {
            Model3D.StopPlaying();
            return;
        }

        Model3D.Play(walk, _walkFrame >= walk.Frames - 1 ? 0 : _walkFrame);
        Model3D.Focus();
    }

    private void OnWalkthroughFirst(object sender, RoutedEventArgs e)
    {
        if (_shownWalkthrough is not { } walk) return;
        Model3D.StopPlaying();
        Model3D.ShowFrame(walk, 0);
    }

    private void OnWalkthroughBack(object sender, RoutedEventArgs e) => StepKeyFrame(-1);

    private void OnWalkthroughForward(object sender, RoutedEventArgs e) => StepKeyFrame(1);

    /// <summary>
    /// The walkthrough saved as a video: each frame shown in the 3D view and pictured, the pictures
    /// written as a Motion-JPEG AVI at its frame rate. The window goes on answering as it goes.
    /// </summary>
    private async void OnExportWalkthrough(object sender, RoutedEventArgs e)
    {
        if (_shownWalkthrough is not { } walk) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Walkthrough",
            Filter = "Video (*.avi)|*.avi",
            DefaultExt = ".avi",
            FileName = walk.Name + ".avi"
        };
        if (dialog.ShowDialog(this) != true) return;

        await ExportWalkthrough(walk, dialog.FileName);
    }

    /// <summary>Every frame of a walkthrough, pictured and written to a video file.</summary>
    private async Task ExportWalkthrough(Walkthrough walk, string path)
    {
        Model3D.StopPlaying();
        ExportWalkthroughButton.IsEnabled = PlayButton.IsEnabled = false;
        try
        {
            await using var file = File.Create(path);
            System.Windows.Media.Imaging.BitmapSource? first = null;
            Infrastructure.Interoperability.AviWriter? video = null;
            for (var frame = 0; frame < walk.Frames; frame++)
            {
                Model3D.ShowFrame(walk, frame);
                Model3D.UpdateLayout();
                var picture = Model3D.RenderFrame();
                first ??= picture;
                video ??= new Infrastructure.Interoperability.AviWriter(file, first.PixelWidth, first.PixelHeight, walk.FramesPerSecond);

                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
                using var jpeg = new MemoryStream();
                encoder.Save(jpeg);
                video.Add(jpeg.ToArray());

                StatusHint.Text = $"Exporting {walk.Name}: frame {frame + 1} of {walk.Frames}.";
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            video?.Finish();
            StatusHint.Text = $"{walk.Name} saved as a video: {walk.Frames} frames, {walk.Duration:0.#} seconds, {first?.PixelWidth}×{first?.PixelHeight}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Could not export the walkthrough", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ExportWalkthroughButton.IsEnabled = PlayButton.IsEnabled = true;
            Model3D.ShowFrame(walk, 0);
        }
    }

    /// <summary>To the frame at the next or last point of the path - a key frame.</summary>
    private void StepKeyFrame(int way)
    {
        if (_shownWalkthrough is not { } walk || walk.Length <= 0) return;
        Model3D.StopPlaying();

        var keys = new List<int>();
        var along = 0.0;
        for (var i = 0; i < walk.Path.Count; i++)
        {
            if (i > 0) along += walk.Path[i - 1].DistanceTo(walk.Path[i]);
            keys.Add((int)Math.Round(along / walk.Length * (walk.Frames - 1)));
        }

        var next = way > 0 ? keys.FirstOrDefault(key => key > _walkFrame, walk.Frames - 1) : keys.LastOrDefault(key => key < _walkFrame, 0);
        Model3D.ShowFrame(walk, next);
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
        else
        {
            if (ModelPanel.Visibility == Visibility.Visible) OnClose3D(this, new RoutedEventArgs());
            StatusHint.Text = "The plan on its own again. 3D View brings the model back.";
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

    /// <summary>A 3D handle's drag followed to where the mouse has got to: what it is on moved, and shown as it now is.</summary>
    private void FollowGripDrag()
    {
        _gripDragPending = false;
        if (_gripDrag is not { } drag) return;
        _gripDrag = null;

        if (drag.Key.StartsWith("handle:", StringComparison.Ordinal))
        {
            if (!Plan.DragHandleIn3D(drag.Key, drag.From, drag.Through) || Plan.SelectedElements is not [var moved]) return;
            IReadOnlyList<Core.Geometry.Mesh3D> meshes = moved switch
            {
                Railing railing => RailingModel.Meshes(_document, railing),
                Beam beam => Beams.Mesh(_document, beam) is { } mesh ? new[] { mesh } : Array.Empty<Core.Geometry.Mesh3D>(),
                Toposurface ground => Terrains.Meshes(_document, ground).Where(mesh => mesh.ElementId == ground.Id).ToList(),
                GroundRegion region when GroundRegions.Host(_document, region) is { } host => Terrains.Meshes(_document, host).Where(mesh => mesh.ElementId == region.Id).ToList(),
                BeamSystem system => BeamSystems.Meshes(_document, system),
                _ => Array.Empty<Core.Geometry.Mesh3D>()
            };
            Model3D.PreviewElement(moved.Id, meshes);
            return;
        }

        if (!Plan.DragStairGripIn3D(drag.Key, drag.From, drag.Through)) return;
        if (Plan.SelectedElements.OfType<Stair>().FirstOrDefault() is { } stair)
            Model3D.PreviewElement(stair.Id, StairModel.Meshes(_document, stair));
    }

    /// <summary>Rebuilds the 3D meshes, but only while anyone can see them.</summary>
    private void Refresh3D()
    {
        if (ModelPanel.Visibility != Visibility.Visible) return;

        // The view or walkthrough shown taken away by an undo: back to {3D}.
        if (!IsKept(Model3D.View) && Model3D.Elevation is null && (_shownWalkthrough is null || !_document.Elements.Contains(_shownWalkthrough)))
        {
            Show3D(_document.Default3D);
            return;
        }

        Model3D.Rebuild();
        SyncModelOptions();
        ModelCaption.Text = Model3D.Elevation is { } side ? Elevations.Name(side) : _shownWalkthrough?.Name ?? Model3D.View.Name;
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
        if (_loadingOptions || VisualStylePicker.SelectedIndex < 0) return;

        var style = (VisualStyle)VisualStylePicker.SelectedIndex;
        if (style != Model3D.View.Style && IsKept(Model3D.View) && Locked(Model3D.Reference, ViewTemplateSetting.VisualStyle))
        {
            SyncModelOptions();
            return;
        }

        ChangeView(view => view.Style = style, "Visual Style");
    }

    private void OnModelCategoryToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        if (!ReferenceEquals(sender, ShowGroundBox) && !ReferenceEquals(sender, ShowEdgesBox) && IsKept(Model3D.View) &&
            Locked(Model3D.Reference, ViewTemplateSetting.HiddenKinds))
        {
            SyncModelOptions();
            return;
        }
        Model3D.SetKindVisible(MeshKind.Roof, ShowRoofsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Ceiling, ShowCeilingsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Floor, ShowFloorsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Wall, ShowWallsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Framing, ShowFramingBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Stair, ShowStairsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Railing, ShowRailingsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Ramp, ShowRampsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Beam, ShowBeamsBox.IsChecked == true);
        Model3D.SetKindVisible(MeshKind.Terrain, ShowTerrainBox.IsChecked == true);
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
        if (Locked(Plan.CurrentView, ViewTemplateSetting.Scale))
        {
            RefreshPlanScale();
            return;
        }

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

        // What is pasted follows the cursor until it is clicked down: pasted at once, it would
        // land where the cursor last was - on what was copied, just clicked, or wherever the
        // cursor left the plan for the ribbon.
        if (inPlace) Plan.Paste(inPlace: true);
        else Plan.BeginPaste();
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

    // ---- dimensions --------------------------------------------------------------

    private void OnDimensionTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions || DimensionTypePicker.SelectedItem is not DimensionType type) return;
        Plan.ActiveDimensionTypeId = type.Id;
    }

    private void OnDimensionStringChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        Plan.DimensionString = DimensionStringBox.IsChecked == true;
        Plan.CancelPendingOperation();
        StatusHint.Text = Plan.DimensionString
            ? "String: click each wall, face or gridline in turn, then double-click where the dimension line goes."
            : "Click what to measure from, then what to measure to.";
    }

    // ---- view templates --------------------------------------------------------

    private static readonly ParameterDefinition ViewTemplateParameter = new("View Template", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    private const string NoTemplate = "<None>";

    /// <summary>
    /// The view being worked in: the 3D view, an elevation, or a section or interior elevation
    /// when it has the keyboard; otherwise the plan, or the callout of it shown.
    /// </summary>
    private ViewReference CurrentViewReference()
    {
        if (ModelCoversPlan || (ModelPanel.Visibility == Visibility.Visible && Model3D.IsKeyboardFocusWithin))
        {
            if (Model3D.Elevation is { } side) return ViewReference.Elevation(side);
            if (IsKept(Model3D.View) && _shownWalkthrough is null) return Model3D.Reference;
        }

        if (SectionPanel.Visibility == Visibility.Visible && (Section.IsKeyboardFocusWithin || Projected.IsKeyboardFocusWithin))
        {
            if (Projected.Visibility == Visibility.Visible && Projected.View is { } elevation) return elevation;
            if (Section.Callout is { } detail) return Callouts.Reference(detail);
            if (Section.Marker is { } marker) return ViewReference.Section(marker.Id);
        }

        return Plan.CurrentView;
    }

    /// <summary>Whether a setting of a view is set by its template; said so, if it is.</summary>
    private bool Locked(ViewReference view, ViewTemplateSetting setting)
    {
        if (ViewTemplates.Of(_document, view) is not { } template || !template.Includes(setting)) return false;
        StatusHint.Text = $"{view.TitleIn(_document)} is governed by the template {template.Name}: change it there (View tab, Templates, Manage), or set the view's View Template to {NoTemplate}.";
        return true;
    }

    /// <summary>The View Template a view's Properties show: none, or one of those for its kind, to govern it.</summary>
    private ParameterValue ViewTemplateChoice(ViewReference view)
    {
        var templates = ViewTemplates.For(_document, view).ToList();
        var choices = new[] { NoTemplate }.Concat(templates.Select(template => template.Name)).ToList();
        return ParameterValue.BindChoiceCommand(ViewTemplateParameter,
            () => ViewTemplates.Of(_document, view)?.Name ?? NoTemplate,
            text => ViewTemplates.Apply(_document, templates.FirstOrDefault(template => template.Name == text), view, govern: true),
            choices);
    }

    private void OnCreateViewTemplate(object sender, RoutedEventArgs e)
    {
        var view = CurrentViewReference();
        if (ViewTemplates.FromView(_document, view) is not { } template)
        {
            StatusHint.Text = "Open a plan, section, elevation or 3D view first: a template is made from it.";
            return;
        }

        _history.Execute(new AddElementCommand(_document, template, "Create View Template"));
        AfterHistoryChange();
        StatusHint.Text = $"{template.Name}, from {view.TitleIn(_document)}. Pick it as a view's View Template in Properties to have it govern that view.";
        ShowViewTemplates(template);
    }

    /// <summary>Apply to View: the templates for the view shown, one to pick.</summary>
    private void OnApplyViewTemplate(object sender, RoutedEventArgs e)
    {
        var view = CurrentViewReference();
        ApplyTemplateMenu.Items.Clear();
        foreach (var template in ViewTemplates.For(_document, view))
        {
            var item = new MenuItem { Header = template.Name };
            item.Click += (_, _) =>
            {
                _history.Execute(ViewTemplates.Apply(_document, template, view, govern: false));
                AfterHistoryChange();
                StatusHint.Text = $"{template.Name} applied to {view.TitleIn(_document)}. The view goes its own way from here.";
            };
            ApplyTemplateMenu.Items.Add(item);
        }

        if (ApplyTemplateMenu.Items.Count == 0)
        {
            StatusHint.Text = $"There is no template for a view like {view.TitleIn(_document)}: make one with Create from View.";
            return;
        }

        ApplyTemplateMenu.PlacementTarget = ApplyTemplateButton;
        ApplyTemplateMenu.IsOpen = true;
    }

    private void OnManageViewTemplates(object sender, RoutedEventArgs e) => ShowViewTemplates(null);

    private void ShowViewTemplates(ViewTemplate? selected)
    {
        var window = new ViewTemplatesWindow(_document, _history, selected) { Owner = this };
        window.Changed += (_, _) => AfterHistoryChange();
        window.ShowDialog();
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
                    StaysOpenOnClick = true,

                    // Set by the view's template: changed there, not here.
                    IsEnabled = !ViewTemplates.Locks(_document, view, ViewTemplateSetting.WallFunctions)
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

    // ---- groups ----------------------------------------------------------------

    private void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        if (Plan.GroupSelection() is not null) AfterHistoryChange();
    }

    private void OnEditGroup(object sender, RoutedEventArgs e) => Plan.BeginGroupEdit();

    private void OnUngroup(object sender, RoutedEventArgs e)
    {
        if (Plan.UngroupSelection()) AfterHistoryChange();
    }

    private void OnAddToGroup(object sender, RoutedEventArgs e)
    {
        if (Plan.AddToEditingGroup()) AfterHistoryChange();
    }

    private void OnRemoveFromGroup(object sender, RoutedEventArgs e)
    {
        if (Plan.RemoveFromEditingGroup()) AfterHistoryChange();
    }

    private void OnFinishGroup(object sender, RoutedEventArgs e) => Plan.FinishGroupEdit();

    private void OnCancelGroup(object sender, RoutedEventArgs e) => Plan.CancelGroupEdit();

    private void OnArrayRadialChanged(object sender, RoutedEventArgs e)
    {
        Plan.ArrayRadial = ArrayRadialBox.IsChecked == true;
        ArrayAngleLabel.Visibility = ArrayAngleBox.Visibility = Plan.ArrayRadial ? Visibility.Visible : Visibility.Collapsed;
        StatusHint.Text = Plan.ArrayRadial
            ? "Radial: set the Count and Angle, then click the centre to repeat the selection round it."
            : "Click two points for the spacing.";
    }

    private void OnArrayAngleChanged(object sender, RoutedEventArgs e)
    {
        var typed = ArrayAngleBox.Text.Trim().TrimEnd('°').Trim();
        if ((double.TryParse(typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var degrees) ||
             double.TryParse(typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out degrees)) && Math.Abs(degrees) is >= 1 and <= 360)
            Plan.ArrayAngle = degrees;

        ArrayAngleBox.Text = $"{Plan.ArrayAngle.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)}°";
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
        if (!_document.Walls.Any() && !_document.All<Slab>().Any())
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
                ApplicationVersion = AppVersion,
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
            $"{_document.All<Slab>().Count()} slabs, " +
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
                "Open a sheet first: View tab → New Sheet, or Ctrl+H.",
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
                "There are no sheets to export. Make one with View → New Sheet and place views on it.",
                "Export to PDF", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        ExportPdf(sheets);
    }

    /// <summary>Every sheet, in drawing-number order. That is the order a set is issued in.</summary>
    private List<Sheet> SheetsInOrder() => _document.All<Sheet>()
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
                "There are no sheets to print. Make one with View → New Sheet and place views on it.",
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
    /// <summary>
    /// Opens an interior elevation under the plan, where sections open: the wall it looks at, in
    /// hidden line, cropped to the room, its floor and ceiling.
    /// </summary>
    private void ShowInteriorElevation(ViewReference view)
    {
        if (!view.ExistsIn(_document)) return;

        SectionPanel.Visibility = Visibility.Visible;
        SectionSplitter.Visibility = Visibility.Visible;
        SectionMenuItem.IsChecked = true;
        Section.Visibility = Visibility.Collapsed;
        Projected.Visibility = Visibility.Visible;
        Projected.Document = _document;
        Projected.View = view;
        Projected.SetSelectionQuietly(Plan.SelectedElements.Select(element => element.Id));
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Projected.ZoomToFit());

        _loadingSections = true;
        SectionPicker.SelectedItem = null;
        _loadingSections = false;
        SectionSummary.Text = $"Interior elevation {view.TitleIn(_document)}";
        StatusHint.Text = $"{view.TitleIn(_document)}: cropped to its room, its floor and its ceiling. Scroll to zoom, drag to pan; place it on a sheet with Place View.";
    }

    /// <summary>
    /// Opens a callout's view: a plan's in the plan, cropped to its box at its own detail level;
    /// a section's under the plan, cropped to its box.
    /// </summary>
    private void ShowCallout(Callout callout)
    {
        if (!Callouts.Reference(callout).ExistsIn(_document)) return;

        if (callout.Parent.Kind == ViewKind.FloorPlan)
        {
            ShowPlanView();
            if (_document.FindLevel(callout.LevelId) is { } level) LevelPicker.SelectedItem = level;
            Plan.ActiveCallout = callout;
            Plan.ZoomToFit();
            ShowDetailLevel();
            RefreshProjectBrowser();
            RefreshProperties();
            StatusHint.Text = $"{callout.Name}: part of {callout.Parent.TitleIn(_document)}, at {new ViewScale(_document.ViewSettings.ScaleOf(Callouts.Reference(callout)))}. Open the plan from Floor Plans to see all of it.";
            return;
        }

        if (_document.Find<SectionMarker>(callout.Parent.TargetId) is not { } marker) return;
        ShowSection(marker);
        Section.Callout = callout;
        StatusHint.Text = $"{callout.Name}: a detail of Section {marker.Name}. Place it on a sheet with Place View.";
    }

    /// <summary>The plan in front: the 3D view or a sheet over it put away.</summary>
    private void ShowPlanView()
    {
        if (SheetPanel.Visibility == Visibility.Visible) OnCloseSheet(this, new RoutedEventArgs());
        if (ModelCoversPlan) OnClose3D(this, new RoutedEventArgs());
    }

    /// <summary>Drag a box round part of the section for a callout of it.</summary>
    private void OnSectionCallout(object sender, RoutedEventArgs e)
    {
        if (Section.Marker is null || Projected.Visibility == Visibility.Visible)
        {
            StatusHint.Text = "Open a section first: a callout is drawn round part of it.";
            return;
        }

        Section.DrawingCallout = true;
        Section.Focus();
        StatusHint.Text = "Drag a box round the part of the section to call out.";
    }

    private void ShowSection(SectionMarker marker)
    {
        Section.Callout = null;
        Projected.Visibility = Visibility.Collapsed;
        Section.Visibility = Visibility.Visible;
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
        var markers = _document.All<SectionMarker>().OrderBy(s => s.Name).ToList();
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

        // A section picked: back from an interior elevation to the section view.
        Projected.Visibility = Visibility.Collapsed;
        Section.Visibility = Visibility.Visible;
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

        if (Projected.Visibility == Visibility.Visible)
        {
            Projected.Rebuild();
            return;
        }

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
            Projected.SetSelectionQuietly(Plan.SelectedElements.Select(element => element.Id));
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

        // Cells coloured by the schedule's rules, on top of however the grid draws a cell.
        var cellStyle = ScheduleGrid.CellStyle ?? TryFindResource(typeof(DataGridCell)) as Style;
        var coloured = definition.Formats.Select(format => format.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in result.Columns)
        {
            var calculated = definition.CalculatedNamed(column.Field) is not null;
            var gridColumn = new DataGridTextColumn
            {
                Header = calculated ? $"{column.Field} (calculated)" : column.Field,
                IsReadOnly = column.IsReadOnly,
                Binding = new Binding(ScheduleRowView.IndexPath(column.Field))
                {
                    Mode = column.IsReadOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                }
            };

            if (coloured.Contains(column.Field))
            {
                var style = new Style(typeof(DataGridCell), cellStyle);
                style.Setters.Add(new Setter(DataGridCell.BackgroundProperty, new Binding($"{nameof(ScheduleRowView.Colours)}{ScheduleRowView.IndexPath(column.Field)}")));
                style.Setters.Add(new Setter(DataGridCell.ForegroundProperty, System.Windows.Media.Brushes.Black));
                gridColumn.CellStyle = style;
            }

            ScheduleGrid.Columns.Add(gridColumn);
        }

        // Each group headed with what its rows add up to: "1 Kitchen  —  Count: 6     Total Cost: £1,020".
        var headings = result.Groups.ToDictionary(group => group.Heading, group =>
        {
            var subtotals = result.Columns
                .Where(column => column.IsTotalled && group.Subtotals.ContainsKey(column.Field))
                .Select(column => $"{column.Field}: {ScheduleCsv.ParameterTotalText(column, group.Subtotals[column.Field])}")
                .ToList();
            var label = string.IsNullOrWhiteSpace(group.Heading) ? "(none)" : group.Heading;
            return subtotals.Count == 0
                ? $"{label}  —  {group.Rows.Count} {(group.Rows.Count == 1 ? "row" : "rows")}"
                : $"{label}  —  {string.Join("     ", subtotals)}";
        });
        string Heading(string key) => headings.TryGetValue(string.IsNullOrWhiteSpace(key) ? "(none)" : key, out var heading) ? heading : key;

        var rows = result.Rows
            .Select(row =>
            {
                var view = new ScheduleRowView(row, definition.GroupBy, Heading, coloured.Count == 0 ? null : result.BackgroundOf);
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

        // A formula that cannot be worked out says why, rather than leaving its column blank unexplained.
        ScheduleProblem.Text = string.Join("   ", result.Problems.Select(problem => $"{problem.Key}: {problem.Value}"));
        ScheduleProblem.Visibility = result.Problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Rebuilding the table drops the highlight, so put it back on whatever is selected.
        SyncScheduleToPlan();
    }

    /// <summary>The schedule picker filled from the project's schedules, one picked.</summary>
    private void RefreshSchedulePicker(ScheduleDefinition? select)
    {
        var loading = _loadingOptions;
        _loadingOptions = true;
        SchedulePicker.ItemsSource = null;
        SchedulePicker.ItemsSource = _document.Schedules.ToList();
        SchedulePicker.SelectedItem = select is not null && _document.Schedules.Contains(select) ? select : _document.Schedules.FirstOrDefault();
        _loadingOptions = loading;
    }

    /// <summary>A schedule opened in the schedule panel.</summary>
    private void ShowSchedule(ScheduleDefinition schedule)
    {
        RefreshSchedulePicker(schedule);
        SchedulePanel.Visibility = Visibility.Visible;
        ScheduleSplitter.Visibility = Visibility.Visible;
        ScheduleMenuItem.IsChecked = true;
        RefreshSchedule();
    }

    /// <summary>The project's schedules changed: the picker, the browser, the sheets and what can go on them follow.</summary>
    private void SchedulesChanged(ScheduleDefinition? select)
    {
        RefreshSchedulePicker(select);
        RefreshSchedule();
        RefreshProjectBrowser();
        RefreshSheetViewOptions();
        SheetSurface.Refresh();
    }

    /// <summary>
    /// A schedule's properties edited in a copy, and put into the project as one step if OK'd.
    /// <paramref name="accept"/> stands in for the dialog, where the window is driven without being shown.
    /// </summary>
    private bool EditSchedule(ScheduleDefinition schedule, bool isNew, Func<ScheduleEditorWindow, bool>? accept = null)
    {
        var working = isNew ? schedule : schedule.Copy(schedule.Name);
        if (!isNew) working.Id = schedule.Id;
        var editor = new ScheduleEditorWindow(_document, working);
        if (accept is null) editor.Owner = this;
        if (!(accept?.Invoke(editor) ?? editor.ShowDialog() == true)) return false;

        _history.Execute(isNew
            ? ScheduleCommands.Add(_document, editor.Schedule, $"New Schedule {editor.Schedule.Name}")
            : ScheduleCommands.Change(schedule, editor.Schedule, $"Edit Schedule {editor.Schedule.Name}"));
        SchedulesChanged(isNew ? editor.Schedule : schedule);
        return true;
    }

    private void OnNewSchedule(object sender, RoutedEventArgs e)
    {
        // Of what is picked now, or walls; with its usual fields to start from.
        var category = (SchedulePicker.SelectedItem as ScheduleDefinition)?.Category ?? BuiltInCategory.Walls;
        var usual = ScheduleDefinition.Defaults().FirstOrDefault(schedule => schedule.Category == category)?.Fields.ToArray() ?? new[] { "Mark", "Type Name" };
        var taken = _document.Schedules.Select(schedule => schedule.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Enumerable.Range(1, 999).Select(i => $"Schedule {i}").First(candidate => !taken.Contains(candidate));
        EditSchedule(new ScheduleDefinition(name, category, usual), isNew: true);
    }

    private void OnEditSchedule(object sender, RoutedEventArgs e)
    {
        if (SchedulePicker.SelectedItem is ScheduleDefinition schedule) EditSchedule(schedule, isNew: false);
    }

    private void OnDuplicateSchedule(object sender, RoutedEventArgs e)
    {
        if (SchedulePicker.SelectedItem is ScheduleDefinition schedule) DuplicateSchedule(schedule);
    }

    private ScheduleDefinition DuplicateSchedule(ScheduleDefinition schedule)
    {
        var taken = _document.Schedules.Select(other => other.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Enumerable.Range(2, 998).Select(i => $"{schedule.Name} {i}").First(candidate => !taken.Contains(candidate));
        var copy = schedule.Copy(name);
        _history.Execute(ScheduleCommands.Add(_document, copy, $"Duplicate {schedule.Name}"));
        SchedulesChanged(copy);
        StatusHint.Text = $"{name}: a copy of {schedule.Name}, to change apart from it.";
        return copy;
    }

    private void OnDeleteSchedule(object sender, RoutedEventArgs e)
    {
        if (SchedulePicker.SelectedItem is ScheduleDefinition schedule) DeleteSchedule(schedule);
    }

    private void DeleteSchedule(ScheduleDefinition schedule)
    {
        var sheets = _document.All<Sheet>().Count(sheet => sheet.Viewports.Any(viewport => ReferenceEquals(viewport.View.ScheduleIn(_document), schedule)));
        _history.Execute(ScheduleCommands.Delete(_document, schedule));
        SchedulesChanged(null);
        StatusHint.Text = sheets == 0 ? $"Deleted {schedule.Name}." : $"Deleted {schedule.Name}, and took it off {sheets} {(sheets == 1 ? "sheet" : "sheets")}.";
    }

    /// <summary>What can be done to a schedule from the browser.</summary>
    private ContextMenu ScheduleMenu(ScheduleDefinition schedule)
    {
        var menu = new ContextMenu();
        void Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Item("Open", () => ShowSchedule(schedule));
        Item("Properties...", () => EditSchedule(schedule, isNew: false));
        Item("Duplicate", () => DuplicateSchedule(schedule));
        Item("Delete", () => DeleteSchedule(schedule));
        return menu;
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
        StatusX.Text = $"X: {Core.Units.Millimetres(model.X, "0")}";
        StatusY.Text = $"Y: {Core.Units.Millimetres(model.Y, "0")}";
    }

    private void RefreshStatus() => StatusZoom.Text = $"Zoom {Plan.ZoomPercent:0}%";

    /// <summary>Title bar and status bar both show the file and whether it has unsaved work.</summary>
    private void RefreshTitle()
    {
        var name = _path is null ? "Untitled" : Path.GetFileNameWithoutExtension(_path);
        var marker = _history.IsModified ? "*" : string.Empty;

        Title = $"{name}{marker} — BIMDesigner {AppVersion}";
        StatusSaved.Text = _path is null
            ? _history.IsModified ? "Unsaved project — modified" : "Unsaved project"
            : _history.IsModified ? $"{Path.GetFileName(_path)} — modified" : Path.GetFileName(_path);
    }
}
