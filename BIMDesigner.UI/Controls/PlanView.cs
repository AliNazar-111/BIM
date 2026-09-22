using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Rendering;

// System.Windows.Window is in scope here, so the model's Window needs a name of its own.
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.UI.Controls;

public enum PlanTool
{
    Select,
    Wall,
    Door,
    Window,
    Room,
    Floor,
    Ceiling,
    Roof,
    Grid,
    Section,
    Dimension,
    Tag,
    Text,
    Split,
    Trim,
    Offset,
    Mirror,
    Array,
    Sweep,
    Reveal,

    /// <summary>Picks the junctions where walls meet, to change how they join.</summary>
    WallJoins,

    /// <summary>Joins two parallel walls near each other, so openings cut through both.</summary>
    JoinGeometry
,

    /// <summary>Cuts a rectangular opening through a wall where it is clicked.</summary>
    WallOpening
}

/// <summary>What clicking walls does to the selected placed sweep, when not simply selecting.</summary>
public enum SweepEditMode
{
    None,

    /// <summary>Each wall clicked is added to the sweep, or taken off it if it is already on.</summary>
    AddRemoveWalls,

    /// <summary>Clicking near an end of the sweep turns its return on or off there.</summary>
    ModifyReturns
}

/// <summary>
/// The 2D floor-plan editor surface.
///
/// This is deliberately not built from XAML controls: it draws the model directly in
/// <see cref="OnRender"/>, the way a CAD viewport works. Model space is millimetres with Y
/// pointing up; screen space is pixels with Y pointing down.
///
/// It renders the document, never its own copy of it. A wall drawn here is the same object
/// the property panel edits and the 3D view will later extrude.
/// </summary>
public class PlanView : FrameworkElement
{
    private const double MinPixelsPerMm = 0.002;   // ~500 m across a 1000 px viewport
    private const double MaxPixelsPerMm = 2.0;     // ~50 cm across a 1000 px viewport
    private const double MinGridPixelSpacing = 8.0;

    /// <summary>Screen-space radius for grabbing a grip, and for snapping to an endpoint.</summary>
    private const double GripPixelRadius = 7.0;
    private const double SnapPixelRadius = 12.0;

    /// <summary>
    /// Draws the contents of the plan. It is shared with the sheet view, so a plan placed in
    /// a viewport is this drawing rather than a second rendering of the same model.
    /// </summary>
    private readonly PlanRenderer _renderer = new();

    /// <summary>
    /// The storey below, drawn faintly underneath. It is a second renderer rather than a flag
    /// on the first because the whole difference is which ink it uses.
    /// </summary>
    private readonly PlanRenderer _underlayRenderer = new(AppTheme.Underlay);

    // The background grid and the editing affordances. Everything that is part of the drawing
    // itself lives on the renderer; what is left here is the graph paper and the handles.
    private readonly Pen _minorGridPen;
    private readonly Pen _majorGridPen;
    private readonly Pen _axisPen;
    private readonly Pen _selectedPen;
    private readonly Pen _previewPen;
    private readonly Pen _gripPen;
    private readonly Pen _snapPen;
    private readonly Pen _targetPen;
    private readonly Brush _gripBrush;
    private readonly Brush _bandFill;

    private BimDocument? _document;
    private Point2D? _pendingWallStart;

    /// <summary>An arc wall's end, once clicked, while its third point is being chosen.</summary>
    private Point2D? _pendingArcEnd;

    /// <summary>The points clicked so far on a spline wall, after its start.</summary>
    private readonly List<Point2D> _splinePoints = new();

    /// <summary>A freehand stroke being dragged out, in model space, or null.</summary>
    private List<Point2D>? _stroke;

    /// <summary>Which of a spline wall's points is being dragged, and its shape before the drag.</summary>
    private int _dragSplineIndex;
    private WallSpline? _dragOriginalSpline;

    /// <summary>
    /// The chain being drawn: the wall placed by the last click, and the first one with the
    /// click it started from. An offset chain pulls each corner round to where the offset
    /// lines cross, and closes the loop when the last click lands back on the first.
    /// </summary>
    private Wall? _chainWall;
    private Wall? _chainFirstWall;
    private Point2D _chainFirstClick;

    private Point2D _cursorModel;
    private bool _cursorIsSnapped;

    private bool _isPanning;
    private Point _panScreenAnchor;
    private Point2D _panModelAnchor;

    private GripKind _dragging = GripKind.None;
    private Point2D _dragAnchor;
    private Point2D _dragOriginalStart;
    private Point2D _dragOriginalEnd;
    private double _dragOriginalBulge;

    /// <summary>First wall picked by the trim tool, waiting for its target.</summary>
    private Wall? _trimSubject;

    /// <summary>First end picked by the dimension tool, waiting for its second.</summary>
    private DimensionReference? _pendingDimension;

    private enum GripKind
    {
        None,

        /// <summary>Reshaping one wall by an end. There is no such grip for a set of them.</summary>
        Start,
        End,

        /// <summary>Bending one wall into an arc through the cursor, or straightening it.</summary>
        Bend,

        /// <summary>Moving one of the points a spline wall passes through.</summary>
        SplinePoint,

        /// <summary>Moving the corner where selected walls meet, and every wall end there with it.</summary>
        Corner,

        /// <summary>Moving whatever is selected, however much of it there is.</summary>
        Move
    }

    /// <summary>The last snapped point a move drag was applied at, and the total so far.</summary>
    private Point2D _dragLastPoint;
    private Vector2D _dragTotal;

    /// <summary>The rubber band being dragged out, when one is.</summary>
    private Point2D? _bandStart;
    private Point2D _bandEnd;

    public PlanView()
    {
        Focusable = true;
        ClipToBounds = true;

        _minorGridPen = MakePen(AppTheme.Pick(Color.FromRgb(0xEE, 0xF0, 0xF3), Color.FromRgb(0x2A, 0x2F, 0x37)), 1);
        _majorGridPen = MakePen(AppTheme.Pick(Color.FromRgb(0xDD, 0xE1, 0xE6), Color.FromRgb(0x3C, 0x44, 0x4F)), 1);
        _axisPen = MakePen(AppTheme.Pick(Color.FromRgb(0xB8, 0xC0, 0xCA), Color.FromRgb(0x5A, 0x66, 0x76)), 1.4);

        _selectedPen = MakePen(AppTheme.Pick(Color.FromRgb(0x0B, 0x6F, 0xC2), Color.FromRgb(0x5A, 0xAB, 0xFF)), 2.2);
        _previewPen = MakeDashedPen(AppTheme.Pick(Color.FromRgb(0x0B, 0x6F, 0xC2), Color.FromRgb(0x5A, 0xAB, 0xFF)), 1.4, 4, 3);
        _gripPen = MakePen(AppTheme.Pick(Color.FromRgb(0x0B, 0x6F, 0xC2), Color.FromRgb(0x5A, 0xAB, 0xFF)), 1.6);
        _snapPen = MakePen(AppTheme.Pick(Color.FromRgb(0x14, 0x9A, 0x5A), Color.FromRgb(0x4A, 0xE0, 0x9A)), 1.8);
        _targetPen = MakeDashedPen(AppTheme.Pick(Color.FromRgb(0xD0, 0x8A, 0x00), Color.FromRgb(0xFF, 0xC4, 0x4D)), 2.0, 5, 3);
        _gripBrush = Freeze(new SolidColorBrush(AppTheme.Pick(Colors.White, Color.FromRgb(0x17, 0x1A, 0x21))));
        _bandFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1C, 0x5A, 0xAB, 0xFF)));
    }

    /// <summary>The project being drawn. Setting it re-subscribes and repaints.</summary>
    public BimDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;

            if (_document is not null)
                _document.Elements.CollectionChanged -= OnElementsChanged;

            _document = value;
            _selection.Clear();
            _pendingWallStart = null;
            _trimSubject = null;
            _dragging = GripKind.None;

            // Materials belong to the project, so their cached brushes go with the old one.
            _renderer.InvalidateBrushes();

            if (_document is not null)
                _document.Elements.CollectionChanged += OnElementsChanged;

            RaiseSelectionChanged();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// The project's edit history. Every change the canvas makes goes through it, so
    /// drawing, dragging and splitting are undoable like any other edit.
    /// </summary>
    public UndoStack? History { get; set; }

    public PlanTool ActiveTool { get; private set; } = PlanTool.Select;

    /// <summary>The wall type new walls are created with.</summary>
    public Guid ActiveWallTypeId { get; set; }

    /// <summary>The door type new doors are created with.</summary>
    public Guid ActiveDoorTypeId { get; set; }

    /// <summary>The floor type new floors are created with.</summary>
    public Guid ActiveFloorTypeId { get; set; }

    /// <summary>The ceiling type new ceilings are created with.</summary>
    public Guid ActiveCeilingTypeId { get; set; }

    /// <summary>The roof type new roofs are created with.</summary>
    public Guid ActiveRoofTypeId { get; set; }

    /// <summary>The window type new windows are created with.</summary>
    public Guid ActiveWindowTypeId { get; set; }

    private Guid _activeLevelId;

    /// <summary>
    /// The level this plan shows. A floor plan is a cut through one storey: showing every
    /// level at once would pile the whole building onto one drawing.
    /// </summary>
    public Guid ActiveLevelId
    {
        get => _activeLevelId;
        set
        {
            if (_activeLevelId == value) return;

            _activeLevelId = value;

            // Anything selected on the old level is no longer on this drawing.
            if (_selection.Any(element => !IsOnActiveLevel(element)))
                SelectMany(_selection.Where(IsOnActiveLevel).ToList());

            CancelPendingOperation();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Whether an element belongs on this plan. Grids are datums of the whole building, so
    /// they appear on every level - that is what lets storeys be lined up with each other.
    /// </summary>
    /// <summary>
    /// Whether an element belongs to this plan: on its level, and not filtered out of it. A
    /// hidden element cannot be picked either - clicking where nothing is drawn and getting
    /// something would be baffling.
    /// </summary>
    private bool IsOnActiveLevel(Element element) =>
        element is Grid || (element.LevelId == ActiveLevelId && _shows(element));

    /// <summary>This view's filters, worked out once per repaint or pick rather than per element.</summary>
    private Func<Element, bool> _shows = _ => true;

    private void RefreshViewFilter() =>
        _shows = Document is null ? _ => true : Document.ViewSettings.FilterFor(Document, CurrentView);

    private IEnumerable<T> OnActiveLevel<T>() where T : Element =>
        Document?.Elements.OfType<T>().Where(IsOnActiveLevel) ?? Enumerable.Empty<T>();

    /// <summary>The location line new walls are drawn to.</summary>
    public WallLocationLine ActiveLocationLine { get; set; } = WallLocationLine.WallCentreline;

    /// <summary>
    /// How far new walls are set off the clicked line, in millimetres. Positive goes toward
    /// the exterior of the wall being drawn.
    /// </summary>
    public double DrawOffset { get; set; }

    /// <summary>
    /// Whether the wall being drawn has its exterior on the right of the drawing direction
    /// rather than the left. The spacebar swaps it mid-drawing.
    /// </summary>
    public bool DrawFlipped { get; private set; }

    /// <summary>
    /// What the wall tool draws: one wall at a time, straight or arc, or a whole closed shape
    /// placed with two clicks.
    /// </summary>
    public WallShape DrawShape
    {
        get => _drawShape;
        set
        {
            _drawShape = value;
            _pendingWallStart = null;
            _pendingArcEnd = null;
            _splinePoints.Clear();
            _stroke = null;
            InvalidateVisual();
            _placementPreview = Array.Empty<Wall>();
            _placementPreviewAt = null;
        }
    }

    private WallShape _drawShape = WallShape.Line;

    private bool DrawArcs => _drawShape is WallShape.Arc or WallShape.PartialEllipse;

    /// <summary>
    /// The curve of an arc or partial-ellipse wall from its start and end through a third point,
    /// or null when the point is on the straight line between them.
    /// </summary>
    private WallCurve? ThreePointCurve(Point2D start, Point2D end, Point2D through)
    {
        if (_drawShape == WallShape.PartialEllipse)
            return WallShapes.PartialEllipse(start, end, through) is { } ellipse ? WallCurve.Of(start, end, 0, ellipse) : null;

        var bulge = WallCurve.BulgeThrough(start, end, through);
        return bulge == 0 ? null : WallCurve.Of(start, end, bulge);
    }

    /// <summary>A three-point wall moved off its clicks by the offset, toward the exterior side.</summary>
    private Wall ThreePointWall(WallCurve curve)
    {
        var placed = curve.Offset(DrawOffset * (DrawFlipped ? -1 : 1));
        return new Wall
        {
            Start = placed.Start,
            End = placed.End,
            Bulge = placed.Bulge,
            Ellipse = placed.Ellipse,
            TypeId = ActiveWallTypeId,
            LevelId = ActiveLevelId,
            LocationLine = ActiveLocationLine,
            Flipped = DrawFlipped
        };
    }

    /// <summary>Whether the shape being drawn is a closed one, placed whole with two clicks.</summary>
    private bool DrawsClosedShape => _drawShape is WallShape.Rectangle or WallShape.Polygon or WallShape.Circle or WallShape.Oval or WallShape.Ellipse;

    /// <summary>Whether new walls hang down from their level by <see cref="NewWallHeight"/>, like a foundation wall.</summary>
    public bool NewWallDepth { get; set; }

    /// <summary>The level new walls reach up to, or null for an unconnected height.</summary>
    public Guid? NewWallTopLevelId { get; set; }

    /// <summary>The unconnected height, or the depth, of new walls. Millimetres.</summary>
    public double NewWallHeight { get; set; } = 3000;

    /// <summary>
    /// Auto Join: a new wall laid against the face of one already there is joined to it, so the
    /// doors and windows of either cut through both.
    /// </summary>
    public bool AutoJoinWalls { get; set; }

    /// <summary>Auto Join and Lock: joined walls also move together.</summary>
    public bool LockJoinedWalls { get; set; }

    /// <summary>Joins a wall about to be placed to the walls its faces lie against, when Auto Join is on.</summary>
    private void JoinToTouching(Wall wall, IEnumerable<Wall>? alsoAgainst = null)
    {
        if (!AutoJoinWalls || Document is null || !WallLamination.CanJoin(Document, wall)) return;

        foreach (var other in WallLamination.Touching(Document, wall).Concat(alsoAgainst ?? Array.Empty<Wall>()))
            if (!wall.JoinedTo.Contains(other.Id) && WallLamination.CanJoin(Document, other)) wall.JoinedTo.Add(other.Id);

        wall.LockedToJoined = LockJoinedWalls && wall.JoinedTo.Count > 0 && WallLamination.CanLock(wall);
    }

    /// <summary>Gives a wall about to be placed the height or depth set on the option bar.</summary>
    private Wall Configured(Wall wall)
    {
        JoinToTouching(wall);

        if (NewWallDepth)
        {
            // Up to its own level, from that far below it.
            wall.TopLevelId = wall.LevelId;
            wall.TopOffset = 0;
            wall.BaseOffset = -NewWallHeight;
        }
        else if (NewWallTopLevelId is { } top && top != wall.LevelId)
        {
            wall.TopLevelId = top;
        }
        else
        {
            wall.UnconnectedHeight = NewWallHeight;
        }

        return wall;
    }

    /// <summary>How many sides the polygon shape has.</summary>
    public int PolygonSides { get; set; } = 6;

    /// <summary>Whether the polygon's second click is a corner (inscribed) or the middle of a side.</summary>
    public bool PolygonInscribed { get; set; } = true;

    /// <summary>
    /// The shape from its first click to a second point, moved out by the offset toward the
    /// walls' exterior. Shift makes a rectangle square and an oval round.
    /// </summary>
    private IReadOnlyList<WallPiece> ShapeFrom(Point2D anchor, Point2D to)
    {
        var square = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (square && _drawShape is WallShape.Rectangle or WallShape.Oval or WallShape.Ellipse) to = WallShapes.SquareCorner(anchor, to);

        var pieces = _drawShape switch
        {
            WallShape.Rectangle => WallShapes.Rectangle(anchor, to),
            WallShape.Polygon => WallShapes.Polygon(anchor, to, PolygonSides, PolygonInscribed),
            WallShape.Circle => WallShapes.Circle(anchor, to),
            WallShape.Oval => WallShapes.Oval(anchor, to),
            WallShape.Ellipse => WallShapes.Ellipse(anchor, to),
            _ => Array.Empty<WallPiece>()
        };

        // The offset goes toward the exterior: outward, unless the walls have been flipped.
        return WallShapes.Offset(pieces, DrawFlipped ? -DrawOffset : DrawOffset);
    }

    /// <summary>
    /// Pick lines: a wall along the gridline clicked, its full length (specification section
    /// 3.1, "pick lines"). The offset moves it off the gridline toward the side clicked on, so
    /// walls can be set out a known distance from a grid.
    /// </summary>
    private void PickLine(Point2D raw)
    {
        if (Document is null) return;

        var tolerance = 6 / PixelsPerMm;
        var grid = OnActiveLevel<Grid>()
            .Select(g => (Grid: g, Distance: DistanceToSegment(raw, g.Start, g.End)))
            .Where(g => g.Distance <= tolerance)
            .OrderBy(g => g.Distance)
            .Select(g => g.Grid)
            .FirstOrDefault();

        if (grid is null)
        {
            HintChanged?.Invoke(this, "No gridline there. Click on a gridline to put a wall along it.");
            return;
        }

        var line = WallCurve.Of(grid.Start, grid.End, 0);
        var side = line.Locate(raw).Left >= 0 ? 1.0 : -1.0;

        var wall = Configured(new Wall
        {
            Start = line.At(0, DrawOffset * side),
            End = line.At(line.Length, DrawOffset * side),
            TypeId = ActiveWallTypeId,
            LevelId = ActiveLevelId,
            LocationLine = ActiveLocationLine,
            Flipped = DrawFlipped
        });

        Apply(new AddElementCommand(Document, wall, "Pick Line"));
        Select(wall);
        HintChanged?.Invoke(this, $"Wall placed on grid {grid.Name}. Click another gridline, or Esc to stop.");
    }

    /// <summary>The second click of a closed shape: every wall of it placed as one step.</summary>
    private void PlaceShape(Point2D model)
    {
        if (Document is null || _pendingWallStart is not { } anchor) return;

        var walls = WallShapes.Walls(ShapeFrom(anchor, model), ActiveWallTypeId, ActiveLevelId, ActiveLocationLine, DrawFlipped)
            .Select(Configured)
            .ToList();
        if (walls.Count == 0)
        {
            HintChanged?.Invoke(this, "Too small to build. Drag further from the first click.");
            return;
        }

        Apply(new AddElementsCommand(Document, walls, $"Draw {_drawShape}"));
        _pendingWallStart = null;
        SelectMany(walls);

        HintChanged?.Invoke(this, $"{walls.Count} walls placed. Click to start another {_drawShape.ToString().ToLowerInvariant()}.");
    }

    private string FirstClickHint() => _drawShape switch
    {
        WallShape.Spline => "Click points the wall curves through. Enter or a double click finishes, the first point closes a loop, Esc cancels.",
        WallShape.Arc => "Click where the arc ends. Space flips it, Esc cancels.",
        WallShape.Rectangle => "Click the opposite corner. Shift makes it square, Space flips the walls, Esc cancels.",
        WallShape.Polygon => PolygonInscribed
            ? "Click where a corner goes. Esc cancels."
            : "Click where the middle of a side goes. Esc cancels.",
        WallShape.Circle => "Click a point on the circle. Esc cancels.",
        WallShape.Oval => "Click the opposite corner of the oval. Shift makes it round, Esc cancels.",
        WallShape.Ellipse => "Click the opposite corner of the ellipse. Shift makes it round, Esc cancels.",
        WallShape.PartialEllipse => "Click the other end of the axis. Space flips it, Esc cancels.",
        _ => "Click again to set the end of the wall. Space flips it, Esc cancels."
    };

    /// <summary>What a view is known as for its settings: this plan is the active level's.</summary>
    public ViewReference CurrentView => ViewReference.FloorPlan(ActiveLevelId);

    /// <summary>
    /// The spacebar: flips the wall being drawn, or failing that every selected wall.
    /// Returns whether it did anything, so the key can be left alone otherwise.
    /// </summary>
    public bool Flip()
    {
        if (ActiveTool == PlanTool.Wall)
        {
            DrawFlipped = !DrawFlipped;
            HintChanged?.Invoke(this, DrawFlipped
                ? "Exterior on the right of the drawing direction. Space swaps it back."
                : "Exterior on the left of the drawing direction. Space swaps it.");
            InvalidateVisual();
            return true;
        }

        var walls = _selection.OfType<Wall>().ToList();
        if (Document is null || walls.Count == 0) return false;

        Apply(new FlipWallsCommand(walls));
        ModelChanged?.Invoke(this, EventArgs.Empty);

        InvalidateVisual();
        return true;
    }

    public DetailLevel DetailLevel { get; set; } = DetailLevel.Fine;

    private bool _showUnderlay = true;

    /// <summary>
    /// Whether the storey below is drawn faintly under this one (specification section 6.1).
    /// </summary>
    public bool ShowUnderlay
    {
        get => _showUnderlay;
        set
        {
            if (_showUnderlay == value) return;

            _showUnderlay = value;
            InvalidateVisual();
        }
    }

    /// <summary>Zoom, expressed as screen pixels per model millimetre.</summary>
    public double PixelsPerMm { get; private set; } = 0.05;

    /// <summary>Model point (mm) shown at the centre of the viewport.</summary>
    public Point2D ViewCentre { get; private set; } = new(5000, 4000);

    public double GridStepMm { get; set; } = 100;

    public double SnapStepMm { get; set; } = 100;

    private readonly List<Element> _selection = new();

    /// <summary>
    /// Everything selected. Editing works on the set, so moving, mirroring, copying and
    /// deleting are all one operation whether one thing is picked or forty.
    /// </summary>
    public IReadOnlyList<Element> SelectedElements => _selection;

    /// <summary>
    /// The element the property panel shows: the one selected, or the last of several.
    ///
    /// Several elements have no single set of parameters, so the panel shows the most recently
    /// picked rather than nothing - that is the one the user was last thinking about.
    /// </summary>
    public Element? SelectedElement => _selection.Count == 0 ? null : _selection[^1];

    /// <summary>
    /// The selection when it is exactly one wall, which is what grips act on. Grips reshape a
    /// single wall; there is no sensible end grip for a set of them.
    /// </summary>
    public Wall? SelectedWall => _selection.Count == 1 ? _selection[0] as Wall : null;

    public bool IsSelected(Element element) => _selection.Contains(element);

    public event EventHandler<Point2D>? CursorMoved;
    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;
    public event EventHandler? ModelChanged;

    /// <summary>Raised with a line of guidance for the status bar as tools change state.</summary>
    public event EventHandler<string>? HintChanged;

    /// <summary>Raised when a section marker is drawn, so its view can be opened at once.</summary>
    public event EventHandler<SectionMarker>? SectionPlaced;

    public double ZoomPercent => PixelsPerMm / 0.05 * 100.0;

    // ---- placed sweeps and reveals ------------------------------------------------

    /// <summary>The sweep type the Sweep tool places, and the reveal type the Reveal tool places.</summary>
    public Guid ActiveSweepTypeId { get; set; }

    public Guid ActiveRevealTypeId { get; set; }

    /// <summary>Whether new sweeps stand upright at the click rather than running along the wall.</summary>
    public bool NewSweepVertical { get; set; }

    /// <summary>How high a new lying sweep sits above its wall's base. Millimetres.</summary>
    public double NewSweepHeight { get; set; }

    /// <summary>What clicking walls does to the selected placed sweep just now.</summary>
    public SweepEditMode SweepEdit { get; private set; }

    /// <summary>Modify Returns: whether a clicked end is given a return, or cut straight.</summary>
    public bool ReturnOnClick { get; set; } = true;

    private PlacedSweep? _sweepEditTarget;

    /// <summary>Raised when an Add/Remove Walls or Modify Returns mode starts or stops.</summary>
    public event EventHandler? SweepEditChanged;

    /// <summary>
    /// Add Point: clicks on the selected wall put points on it to drag its shape out by. Ends
    /// when the selection is no longer one wall.
    /// </summary>
    public bool AddingWallPoints
    {
        get => _addingWallPoints;
        set
        {
            if (_addingWallPoints == value) return;
            _addingWallPoints = value && _selection.Count > 0 && _selection.All(element => element is Wall);
            if (_addingWallPoints)
                HintChanged?.Invoke(this, "Click on the wall to add a point, then drag the round grip to shape it. Double-click a point to remove it. Esc when done.");
            WallPointModeChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    private bool _addingWallPoints;

    public event EventHandler? WallPointModeChanged;

    /// <summary>
    /// A click that shapes a wall: on one of its points, a double click removes it; anywhere
    /// else on the wall, a point is added there. Returns whether the click was taken.
    /// </summary>
    private bool ShapeWallAt(Wall wall, Point2D raw, bool doubleClick)
    {
        if (Document is null) return false;

        if (SplineGripAt(wall, raw) is { } index)
        {
            // A single click on a point is the start of dragging it.
            if (!doubleClick) return false;

            var fewer = WallPoints.RemovePoint(wall, index);
            Apply(new ReshapeSplineWallCommand(wall, wall.Spline, fewer, "Remove Wall Point"));
            InvalidateVisual();
            HintChanged?.Invoke(this, fewer is null ? "Last point removed: the wall is straight again." : "Point removed.");
            return true;
        }

        var reach = (Document.GetWallType(wall)?.Width ?? 0) / 2 + 4 / PixelsPerMm;
        if (wall.LocationCurve.DistanceTo(raw) > reach) return false;

        // A spline wall takes another smooth point; any other wall a corner, splitting it in two
        // straight walls that meet there.
        if (!wall.IsSpline)
        {
            AddCorner(wall, raw);
            return true;
        }

        if (WallPoints.AddPoint(wall, raw) is not { } more)
        {
            HintChanged?.Invoke(this, "Too close to an end or another point to add one there.");
            return true;
        }

        Apply(new ReshapeSplineWallCommand(wall, wall.Spline, more, "Add Wall Point"));
        InvalidateVisual();
        HintChanged?.Invoke(this, "Point added. Drag its round grip to reshape the curve; double-click it to remove it.");
        return true;
    }

    /// <summary>
    /// A corner point on a wall: split where it was clicked, the two halves selected, so the
    /// round grip between them can be dragged at once.
    /// </summary>
    private void AddCorner(Wall wall, Point2D raw)
    {
        if (Document is null) return;

        var curve = wall.LocationCurve;
        var along = curve.Locate(raw).Along;
        var margin = Math.Max(curve.Length * 0.02, 8 / PixelsPerMm);
        if (along < margin || along > curve.Length - margin)
        {
            HintChanged?.Invoke(this, "Too close to the end of the wall to add a point there.");
            return;
        }

        var split = new SplitWallCommand(Document, wall, curve.PointAt(along));
        Apply(split);

        var selection = _selection.Where(element => !ReferenceEquals(element, wall)).ToList();
        selection.Add(wall);
        selection.Add(split.Remainder);
        SelectMany(selection);

        HintChanged?.Invoke(this, "Point added. Drag the round grip to pull the corner anywhere; double-click it to take it out.");
        InvalidateVisual();
    }

    /// <summary>
    /// Where two or more selected walls meet end to end, under the cursor: a corner that can be
    /// dragged. Only with more than one wall selected - a single wall's ends have their own grips.
    /// </summary>
    private Point2D? CornerAt(Point2D model)
    {
        var radius = GripPixelRadius / PixelsPerMm;
        return Corners().Cast<Point2D?>().FirstOrDefault(corner => corner!.Value.DistanceTo(model) <= radius);
    }

    /// <summary>The points where two or more selected walls meet end to end.</summary>
    private IReadOnlyList<Point2D> Corners()
    {
        var walls = _selection.OfType<Wall>().ToList();
        if (walls.Count < 2) return Array.Empty<Point2D>();

        var corners = new List<Point2D>();
        foreach (var end in walls.SelectMany(w => new[] { w.Start, w.End }))
        {
            if (corners.Any(c => c.DistanceTo(end) <= WallJoins.JoinTolerance)) continue;
            if (walls.Count(w => w.Start.DistanceTo(end) <= WallJoins.JoinTolerance || w.End.DistanceTo(end) <= WallJoins.JoinTolerance) >= 2)
                corners.Add(end);
        }

        return corners;
    }

    /// <summary>Takes a corner out, making its two walls one straight wall again. Returns whether it could.</summary>
    private bool RemoveCorner(Point2D corner)
    {
        if (Document is null) return false;

        var walls = WallCorners.At(Document, ActiveLevelId, corner);
        if (walls.Count != 2 || !WallCorners.CanMerge(walls[0].Wall, walls[1].Wall))
        {
            HintChanged?.Invoke(this, walls.Count == 2
                ? "These two walls cannot become one: they differ in type, or one is curved, profiled or a curtain wall."
                : "More than two walls meet here, so there is no single wall to make of them.");
            return true;
        }

        var (keep, remove) = (walls[0].Wall, walls[1].Wall);
        Apply(new MergeWallsCommand(Document, keep, remove));
        SelectMany(_selection.Where(element => !ReferenceEquals(element, remove)).ToList());
        HintChanged?.Invoke(this, "Point taken out: the two walls are one straight wall again.");
        InvalidateVisual();
        return true;
    }

    // The walls whose ends are being dragged with a corner, and where they were before.
    private readonly List<(Wall Wall, bool AtStart, Point2D Start, Point2D End)> _cornerWalls = new();

    private void BeginCornerDrag(Point2D corner, Point2D raw)
    {
        if (Document is null) return;

        _cornerWalls.Clear();
        foreach (var (wall, atStart) in WallCorners.At(Document, ActiveLevelId, corner))
            _cornerWalls.Add((wall, atStart, wall.Start, wall.End));

        _dragging = GripKind.Corner;
        _dragAnchor = raw;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    /// <summary>Starts Add/Remove Walls or Modify Returns on the selected placed sweep.</summary>
    public void BeginSweepEdit(SweepEditMode mode)
    {
        if (SelectedElement is not PlacedSweep sweep || mode == SweepEditMode.None)
        {
            EndSweepEdit();
            return;
        }

        SetTool(PlanTool.Select);
        _sweepEditTarget = sweep;
        SweepEdit = mode;
        HintChanged?.Invoke(this, mode == SweepEditMode.AddRemoveWalls
            ? "Click walls to add them to the sweep, or take them off it. Esc when done."
            : "Click near an end of the sweep to give it the return or straight cut chosen in the options bar. Esc when done.");
        SweepEditChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EndSweepEdit()
    {
        if (SweepEdit == SweepEditMode.None) return;
        SweepEdit = SweepEditMode.None;
        _sweepEditTarget = null;
        SweepEditChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Places a sweep or reveal on the wall clicked, on the face nearer the click: along the wall
    /// at the height set on the options bar, or upright at the click (Revit's Wall: Sweep).
    /// </summary>
    private void PlaceSweep(Point2D raw, SweepKind kind)
    {
        if (Document is null) return;

        if (HitTestWall(raw) is not { } wall || Document.GetWallType(wall) is not { } wallType)
        {
            HintChanged?.Invoke(this, $"A {kind.ToString().ToLowerInvariant()} goes on a wall. Click one, on the face it belongs on.");
            return;
        }

        if (Document.IsCurtainWall(wall))
        {
            HintChanged?.Invoke(this, "That is a curtain wall: sweeps and reveals go on ordinary walls.");
            return;
        }

        var typeId = kind == SweepKind.Sweep ? ActiveSweepTypeId : ActiveRevealTypeId;
        if (Document.FindType<WallSweepType>(typeId) is not { } type || type.Kind != kind)
        {
            HintChanged?.Invoke(this, $"Pick a {kind.ToString().ToLowerInvariant()} type on the options bar first.");
            return;
        }

        var (along, across) = wall.Locate(wallType.Structure, raw);
        var placed = new PlacedSweep
        {
            TypeId = type.Id,
            LevelId = wall.LevelId,
            Kind = kind,
            Side = across >= 0 ? WallSide.Exterior : WallSide.Interior,
            Vertical = NewSweepVertical,
            Elevation = Math.Max(0, NewSweepHeight),
            Along = Math.Clamp(Units.SnapToGrid(along, SnapStepMm), 0, wall.Length)
        };
        placed.HostWallIds.Add(wall.Id);

        Apply(new AddElementCommand(Document, placed, $"Place Wall {kind}"));
        Select(placed);
        HintChanged?.Invoke(this, $"{type.Name} placed on the {placed.Side.ToString().ToLowerInvariant()} face. Click another wall, or Esc.");
    }

    /// <summary>A click while Add/Remove Walls or Modify Returns is on. True if it was used.</summary>
    private bool SweepEditClick(Point2D raw)
    {
        if (Document is null || _sweepEditTarget is not { } sweep || !Document.Elements.Contains(sweep)) return false;

        if (SweepEdit == SweepEditMode.AddRemoveWalls)
        {
            if (HitTestWall(raw) is not { } wall) return true;

            var hosts = sweep.HostWallIds.ToList();
            if (hosts.Contains(wall.Id))
            {
                if (hosts.Count == 1)
                {
                    HintChanged?.Invoke(this, "A sweep needs one wall. Delete it instead to take it off its last wall.");
                    return true;
                }

                hosts.Remove(wall.Id);
            }
            else if (!Document.IsCurtainWall(wall))
            {
                hosts.Add(wall.Id);
            }

            Apply(new EditPlacedSweepCommand(sweep, hosts, sweep.ReturnAtStart, sweep.ReturnAtEnd, "Add/Remove Walls"));
            HintChanged?.Invoke(this, $"The sweep is on {hosts.Count} wall{(hosts.Count == 1 ? "" : "s")}. Click more, or Esc when done.");
            return true;
        }

        if (SweepEdit == SweepEditMode.ModifyReturns)
        {
            var walls = sweep.HostWallIds.Select(id => Document.Walls.FirstOrDefault(w => w.Id == id)).OfType<Wall>().ToList();
            if (walls.Count == 0) return true;

            // The sweep's own two ends: where its first wall starts and its last wall ends.
            var nearStart = raw.DistanceTo(walls[0].Start) <= raw.DistanceTo(walls[^1].End);
            Apply(new EditPlacedSweepCommand(sweep, sweep.HostWallIds,
                nearStart ? ReturnOnClick : sweep.ReturnAtStart,
                nearStart ? sweep.ReturnAtEnd : ReturnOnClick,
                "Modify Returns"));
            var now = nearStart ? sweep.ReturnAtStart : sweep.ReturnAtEnd;
            HintChanged?.Invoke(this, $"That end now {(now ? "returns round the wall end" : "is cut straight")}. A return shows where the wall end is exposed.");
            return true;
        }

        return false;
    }

    /// <summary>The placed sweep or reveal under the cursor, drawn beside or in a wall face.</summary>
    private PlacedSweep? HitTestSweep(Point2D model) => SweepsAt(model).FirstOrDefault();

    /// <summary>Every placed sweep or reveal under the cursor.</summary>
    private IEnumerable<PlacedSweep> SweepsAt(Point2D model)
    {
        if (Document is null) return Array.Empty<PlacedSweep>();
        var reach = 5 / PixelsPerMm;

        return OnActiveLevel<PlacedSweep>().Where(placed => IsOnSweep(placed, model, reach)).ToList();
    }

    private bool IsOnSweep(PlacedSweep placed, Point2D model, double reach)
    {
        if (Document is null) return false;

        foreach (var id in placed.HostWallIds)
        {
            if (Document.Walls.FirstOrDefault(w => w.Id == id) is not { } wall || Document.GetWallType(wall) is not { } type) continue;
            if (placed.On(Document, wall) is not { } sweep) continue;

            var (along, across) = wall.Locate(type.Structure, model);
            var half = type.Width / 2;
            var sign = sweep.Side == WallSide.Exterior ? 1.0 : -1.0;
            var outward = sign * across - half;

            var (from, to) = sweep.Vertical ? (sweep.Along - sweep.Height / 2, sweep.Along + sweep.Height / 2) : (0.0, wall.Length);
            var (inner, outer) = sweep.Kind == SweepKind.Reveal ? (-sweep.Depth, 0.0) : (sweep.Offset, sweep.Offset + sweep.Depth);

            if (along >= from - reach && along <= to + reach && outward >= inner - reach && outward <= outer + reach)
                return true;
        }

        return false;
    }

    public void SetTool(PlanTool tool)
    {
        ActiveTool = tool;
        CancelPendingOperation();
        _joinFirst = null;
        if (_selectedJunctions.Count > 0)
        {
            _selectedJunctions.Clear();
            JunctionsChanged?.Invoke(this, EventArgs.Empty);
        }

        Cursor = tool == PlanTool.Select ? Cursors.Arrow : Cursors.Cross;
        HintChanged?.Invoke(this, DefaultHintFor(tool));
    }

    private static string DefaultHintFor(PlanTool tool) => tool switch
    {
        PlanTool.Wall => "Click to set the wall start, click again to set the end. Esc cancels.",
        PlanTool.Door => "Click a wall where the door should go.",
        PlanTool.Sweep => "Click a wall on the face the sweep goes on. Set the height, or Vertical, above.",
        PlanTool.Reveal => "Click a wall on the face the reveal is cut into. Set the height, or Vertical, above.",
        PlanTool.Window => "Click a wall where the window should go.",
        PlanTool.Grid => "Click to start a gridline, click again to finish it.",
        PlanTool.Section => "Click to start the cut line, click again to finish it.",
        PlanTool.Dimension => "Click what to measure from, then what to measure to. Esc cancels.",
        PlanTool.Tag => "Click an element to tag it.",
        PlanTool.Text => "Click where the note should go.",
        PlanTool.Room => "Click inside a space enclosed by walls.",
        PlanTool.Floor => "Click inside a space enclosed by walls to lay a floor in it.",
        PlanTool.Ceiling => "Click inside a space enclosed by walls to put a ceiling over it.",
        PlanTool.Roof => "Click inside a space enclosed by walls to roof it.",
        PlanTool.Split => "Click a wall where it should be split.",
        PlanTool.Trim => "Click the wall to trim or extend.",
        PlanTool.Offset => "Click a wall on the side the copy should go. Set the distance above.",
        PlanTool.Mirror => "Select what to mirror first, then click two points on the mirror line.",
        PlanTool.Array => "Select what to repeat first, then click two points for the spacing.",
        PlanTool.WallJoins => "Click the square at a wall join to change it; Ctrl+click adds more. Then choose on the option bar.",
        PlanTool.JoinGeometry => "Click a wall, then a parallel wall beside it (up to 150 mm away) to join them, or two joined walls to unjoin them.",
        PlanTool.WallOpening => "Click a wall where the opening goes. Set its size on the option bar; change it afterwards in Properties.",
        _ => "Click to select, TAB for alternates, Ctrl+click to add, or drag a box. Drag a selection to move it."
    };

    /// <summary>Tells the window the selection changed. Add Point is for one wall, so it ends with any other selection.</summary>
    private void RaiseSelectionChanged()
    {
        if (_selection.Count == 0 || !_selection.All(element => element is Wall)) AddingWallPoints = false;
        if (ActiveTool == PlanTool.Select && LockGrips().Count > 0)
            HintChanged?.Invoke(this, "Another wall lies against this one: click the padlock on their shared face to lock them together, so they move as one.");
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the selection with one element, or clears it.</summary>
    public void Select(Element? element) =>
        SelectMany(element is null ? Array.Empty<Element>() : new[] { element });

    public void SelectMany(IEnumerable<Element> elements)
    {
        var replacement = elements.Distinct().ToList();
        if (replacement.SequenceEqual(_selection)) return;

        _selection.Clear();
        _selection.AddRange(replacement);

        RaiseSelectionChanged();
        InvalidateVisual();
    }

    /// <summary>Adds an element to the selection, or takes it out if it is already in.</summary>
    public void ToggleSelected(Element element)
    {
        if (!_selection.Remove(element)) _selection.Add(element);

        RaiseSelectionChanged();
        InvalidateVisual();
    }

    /// <summary>Everything on this storey, which is what Ctrl+A means on a floor plan.</summary>
    public void SelectAllOnLevel()
    {
        if (Document is null) return;
        RefreshViewFilter();

        SelectMany(Document.Elements.Where(IsOnActiveLevel).Where(element => element is not Sheet));
        HintChanged?.Invoke(this, $"{_selection.Count} selected.");
    }

    public void DeleteSelected()
    {
        if (Document is null || _selection.Count == 0) return;

        Apply(new DeleteElementsCommand(Document, _selection.ToList()));
        Select(null);
    }

    /// <summary>Runs a change through the history, or directly when there is none.</summary>
    private void Apply(IUndoableCommand command)
    {
        if (History is not null) History.Execute(command);
        else command.Redo();
    }

    /// <summary>
    /// Abandons whatever multi-click operation is part-way through. Returns whether there was
    /// one, so Esc can fall through to clearing the selection when there was nothing to cancel.
    /// </summary>
    public bool CancelPendingOperation()
    {
        var changed = _pendingWallStart is not null
                      || AddingWallPoints
                      || _stroke is not null
                      || _trimSubject is not null
                      || _pendingDimension is not null
                      || _bandStart is not null
                      || SweepEdit != SweepEditMode.None;

        EndSweepEdit();
        AddingWallPoints = false;
        _pendingWallStart = null;
        _trimSubject = null;
        _pendingDimension = null;
        _bandStart = null;
        _pendingArcEnd = null;
        _splinePoints.Clear();
        _stroke = null;

        if (!changed) return false;

        HintChanged?.Invoke(this, DefaultHintFor(ActiveTool));
        InvalidateVisual();
        return true;
    }

    /// <summary>Repaints after the model was edited from outside, e.g. the property panel.</summary>
    public void RefreshModel() => InvalidateVisual();

    public void ZoomToFit()
    {
        var walls = Document?.Walls.ToList() ?? new List<Wall>();

        if (walls.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            PixelsPerMm = 0.05;
            ViewCentre = new Point2D(5000, 4000);
        }
        else
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var wall in walls)
            {
                minX = Math.Min(minX, Math.Min(wall.Start.X, wall.End.X));
                minY = Math.Min(minY, Math.Min(wall.Start.Y, wall.End.Y));
                maxX = Math.Max(maxX, Math.Max(wall.Start.X, wall.End.X));
                maxY = Math.Max(maxY, Math.Max(wall.Start.Y, wall.End.Y));
            }

            const double marginMm = 1500;
            var widthMm = Math.Max(maxX - minX, 1000) + marginMm * 2;
            var heightMm = Math.Max(maxY - minY, 1000) + marginMm * 2;

            PixelsPerMm = Clamp(Math.Min(ActualWidth / widthMm, ActualHeight / heightMm));
            ViewCentre = new Point2D((minX + maxX) / 2, (minY + maxY) / 2);
        }

        RaiseViewChanged();
    }

    private void OnElementsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _hoverCandidates.Clear();
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ---- coordinate conversion -------------------------------------------------

    private Point ModelToScreen(Point2D model) => new(
        ActualWidth / 2 + (model.X - ViewCentre.X) * PixelsPerMm,
        ActualHeight / 2 - (model.Y - ViewCentre.Y) * PixelsPerMm);

    private Point2D ScreenToModel(Point screen) => new(
        ViewCentre.X + (screen.X - ActualWidth / 2) / PixelsPerMm,
        ViewCentre.Y - (screen.Y - ActualHeight / 2) / PixelsPerMm);

    private Point2D SnapToGrid(Point2D model) => new(
        Units.SnapToGrid(model.X, SnapStepMm),
        Units.SnapToGrid(model.Y, SnapStepMm));

    /// <summary>
    /// Snaps to a wall end or a gridline crossing in preference to the drawing grid.
    ///
    /// Wall ends matter because walls that share an exact endpoint are what lets them mitre.
    /// Grid crossings matter because setting a building out against its grid is the entire
    /// reason for having one - a grid you cannot snap to is decoration.
    /// </summary>
    private Point2D SnapPoint(Point2D model, Wall? ignore, out bool snappedToFeature)
    {
        snappedToFeature = false;
        if (Document is null) return SnapToGrid(model);

        var best = model;
        var bestDistance = SnapPixelRadius / PixelsPerMm;
        var found = false;

        void Consider(Point2D candidate)
        {
            var distance = candidate.DistanceTo(model);
            if (distance >= bestDistance) return;

            best = candidate;
            bestDistance = distance;
            found = true;
        }

        foreach (var wall in OnActiveLevel<Wall>())
        {
            if (ReferenceEquals(wall, ignore)) continue;

            Consider(wall.Start);
            Consider(wall.End);
        }

        foreach (var crossing in Grids.Intersections(Document)) Consider(crossing);

        snappedToFeature = found;
        return found ? best : SnapToGrid(model);
    }

    // ---- input -----------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var screen = e.GetPosition(this);

        if (_isPanning)
        {
            ViewCentre = new Point2D(
                _panModelAnchor.X - (screen.X - _panScreenAnchor.X) / PixelsPerMm,
                _panModelAnchor.Y + (screen.Y - _panScreenAnchor.Y) / PixelsPerMm);
            RaiseViewChanged();
            return;
        }

        var raw = ScreenToModel(screen);

        // A freehand stroke follows the mouse for as long as the button is down.
        if (_stroke is not null)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _stroke[^1].DistanceTo(raw) * PixelsPerMm >= 2)
            {
                _stroke.Add(raw);
                InvalidateVisual();
            }

            return;
        }

        if (_dragging != GripKind.None)
        {
            DragTo(raw);
            return;
        }

        if (_bandStart is not null)
        {
            _bandEnd = raw;
            InvalidateVisual();
            return;
        }

        // Feature snapping only matters where a point has to land exactly on something: a
        // wall end so walls mitre, and a dimension end so the dimension follows the model.
        // Showing the marker while placing a room or a door would suggest a precision those
        // tools do not have and do not need.
        if (ActiveTool is PlanTool.Wall or PlanTool.Dimension or PlanTool.Section
            or PlanTool.Mirror or PlanTool.Array)
        {
            _cursorModel = SnapPoint(raw, null, out _cursorIsSnapped);
        }
        else
        {
            _cursorModel = SnapToGrid(raw);
            _cursorIsSnapped = false;
        }

        CursorMoved?.Invoke(this, _cursorModel);

        UpdateHoverCursor(raw);

        // Placing against walls: the walls a click here would make, shown before it is made.
        if (ActiveTool == PlanTool.Wall && _drawShape is WallShape.BySegment or WallShape.ByRoom &&
            (_placementPreviewAt is not { } last || last.DistanceTo(raw) * PixelsPerMm > 6))
        {
            _placementPreview = PlacementAt(raw, out _);
            _placementPreviewAt = raw;
            InvalidateVisual();
        }

        if (ActiveTool == PlanTool.Select && _dragging == GripKind.None && _bandStart is null && SweepEdit == SweepEditMode.None)
            UpdateHover(raw);

        if (_pendingWallStart is not null || _pendingDimension is not null || _cursorIsSnapped)
            InvalidateVisual();
    }

    /// <summary>Shows a move cursor over a grip so the wall reads as draggable.</summary>
    private void UpdateHoverCursor(Point2D raw)
    {
        if (ActiveTool != PlanTool.Select) return;

        var overGrip = SelectedWall is not null && GripAt(SelectedWall, raw) != GripKind.None;
        var overSelection = HitTest(raw) is { } hit && IsSelected(hit);

        Cursor = overGrip || overSelection ? Cursors.SizeAll : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (Document is null) return;
        var raw = ScreenToModel(e.GetPosition(this));

        if (ActiveTool == PlanTool.Select && SweepEdit != SweepEditMode.None && SweepEditClick(raw)) return;

        switch (ActiveTool)
        {
            case PlanTool.Wall:
                if (DrawShape == WallShape.Pick) PickLine(raw);
                else if (DrawShape == WallShape.Freehand) BeginStroke(raw);
                else if (DrawShape is WallShape.BySegment or WallShape.ByRoom) PlaceAgainst(raw);
                else if (DrawShape == WallShape.Spline && e.ClickCount == 2 && _pendingWallStart is not null) FinishSpline(closed: false);
                else PlaceWallPoint(SnapPoint(raw, null, out _));
                return;

            case PlanTool.Grid:
                PlaceGridPoint(SnapToGrid(raw));
                return;

            case PlanTool.Section:
                PlaceSectionPoint(SnapPoint(raw, null, out _));
                return;

            case PlanTool.Dimension:
                PlaceDimensionPoint(raw);
                return;

            case PlanTool.Tag:
                PlaceTag(raw);
                return;

            case PlanTool.Text:
                PlaceTextNote(raw);
                return;

            case PlanTool.Sweep:
                PlaceSweep(raw, SweepKind.Sweep);
                return;

            case PlanTool.Reveal:
                PlaceSweep(raw, SweepKind.Reveal);
                return;

            case PlanTool.Door:
                PlaceOpening(raw, isDoor: true);
                return;

            case PlanTool.Window:
                PlaceOpening(raw, isDoor: false);
                return;

            case PlanTool.Room:
                PlaceRoom(raw);
                return;

            case PlanTool.Floor:
            case PlanTool.Ceiling:
            case PlanTool.Roof:
                PlaceSlab(raw, ActiveTool);
                return;

            case PlanTool.Split:
                SplitAt(raw);
                return;

            case PlanTool.Trim:
                TrimAt(raw);
                return;

            case PlanTool.Offset:
                OffsetAt(raw);
                return;

            case PlanTool.Mirror:
                PlaceMirrorPoint(SnapPoint(raw, null, out _));
                return;

            case PlanTool.Array:
                PlaceArrayPoint(SnapPoint(raw, null, out _));
                return;

            case PlanTool.WallJoins:
                PickJunction(e.GetPosition(this));
                return;

            case PlanTool.JoinGeometry:
                JoinGeometryAt(raw);
                return;

            case PlanTool.WallOpening:
                PlaceWallOpening(raw);
                return;
        }

        // Shaping a wall by hand: a double click on it adds a point, on a point takes it away;
        // with Add Point on, a single click on the wall adds one.
        if (e.ClickCount == 2 || AddingWallPoints)
        {
            // A corner between selected walls: a double click takes it out.
            if (e.ClickCount == 2 && CornerAt(raw) is { } corner && RemoveCorner(corner)) return;

            var target = SelectedWall ?? (HitTest(raw) is Wall hitWall && IsSelected(hitWall) ? hitWall : null);
            if (target is not null && ShapeWallAt(target, raw, e.ClickCount == 2)) return;
        }

        // The padlock at a corner: locks or unlocks the walls meeting there.
        if (CornerLockAt(e.GetPosition(this)) is { } lockedCorner)
        {
            ToggleCornerLock(lockedCorner);
            return;
        }

        // The padlock on a face two walls share: locks or unlocks them.
        if (LockGripAt(e.GetPosition(this)) is { } padlock)
        {
            ToggleLock(padlock.Partner);
            return;
        }

        // A corner between selected walls, dragged: every wall meeting there follows.
        if (CornerAt(raw) is { } dragged)
        {
            BeginCornerDrag(dragged, raw);
            return;
        }

        // What the cursor has outlined - TAB may have stepped past the first thing under it.
        var hit = Hovered is { } outlined && _hoverAnchor.DistanceTo(raw) * PixelsPerMm < HoverSlop ? outlined : HitTest(raw);

        // Ctrl adds to or removes from the selection rather than replacing it. It never starts
        // a drag: extending a selection and moving it are different intentions, and doing both
        // on one gesture makes the second one an accident.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (hit is not null) ToggleSelected(hit);
            ReportSelection();
            return;
        }

        // A grip on a single selected wall wins over picking something new.
        if (SelectedWall is { } selected)
        {
            if (IsOnFlipControl(selected, e.GetPosition(this)))
            {
                Flip();
                return;
            }

            var grip = GripAt(selected, raw);
            if (grip is GripKind.Start or GripKind.End or GripKind.Bend or GripKind.SplinePoint)
            {
                BeginEndGripDrag(grip, raw);
                return;
            }
        }

        // Clicking something already selected drags the whole selection, so several elements
        // can be moved together without having to pick them again.
        if (hit is not null && !IsSelected(hit)) Select(hit);

        if (hit is not null)
        {
            ReportSelection();
            BeginMoveDrag(raw);
            return;
        }

        // Nothing under the cursor: drag out a band and take whatever it touches.
        Select(null);
        _bandStart = raw;
        _bandEnd = raw;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_stroke is not null)
        {
            FinishStroke();
            return;
        }

        EndBand();
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _bandStart = null;
        _stroke = null;
        EndDrag();
    }

    /// <summary>Tells the status bar how much is selected, which is not otherwise obvious.</summary>
    private void ReportSelection()
    {
        if (_selection.Count <= 1) return;
        HintChanged?.Invoke(this, $"{_selection.Count} selected. Drag to move them, or press Del.");
    }

    // ---- band selection --------------------------------------------------------

    /// <summary>
    /// Finishes a band drag by selecting everything it touches.
    ///
    /// It is a crossing selection - anything the band overlaps, not only what it completely
    /// surrounds. On a plan the things worth picking are long and thin, and a band that only
    /// took what it fully enclosed would miss every wall that ran off the edge of it.
    /// </summary>
    private void EndBand()
    {
        if (_bandStart is not { } start || Document is null) return;

        var end = _bandEnd;
        _bandStart = null;
        ReleaseMouseCapture();

        var box = new BoundingBox2D(
            Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));

        // A band smaller than a couple of pixels is a click that wobbled, not a drag.
        if (box.Width * PixelsPerMm < 3 && box.Height * PixelsPerMm < 3)
        {
            InvalidateVisual();
            return;
        }

        SelectMany(Document.Elements
            .Where(IsOnActiveLevel)
            .Where(element => element is not Sheet)
            .Where(element => Touches(element, box)));

        HintChanged?.Invoke(this, _selection.Count switch
        {
            0 => "Nothing in there.",
            1 => "1 selected.",
            _ => $"{_selection.Count} selected. Drag to move them, or press Del."
        });

        InvalidateVisual();
    }

    /// <summary>Whether an element is inside a band, or crosses it.</summary>
    private bool Touches(Element element, BoundingBox2D box)
    {
        if (Document is null) return false;

        switch (element)
        {
            case Wall wall:
            {
                var points = wall.LocationCurve.Points();
                return points.Zip(points.Skip(1)).Any(piece => SegmentTouches(piece.First, piece.Second, box));
            }

            case Grid grid:
                return SegmentTouches(grid.Start, grid.End, box);

            case SectionMarker section:
                return SegmentTouches(section.Start, section.End, box);

            case Dimension dimension:
                var (from, to) = dimension.GetDimensionLine(Document);
                return SegmentTouches(from, to, box);

            case Opening opening:
                var host = Document.Walls.FirstOrDefault(w => w.Id == opening.HostWallId);
                return host is not null && Contains(box, opening.GetCentre(host));

            case Slab slab:
                return slab.Boundary.Any(point => Contains(box, point));

            case Room room:
                var boundary = room.GetBoundary(Document);
                return boundary.IsEnclosed
                    ? boundary.Polygon.Any(point => Contains(box, point)) || Contains(box, boundary.Centroid)
                    : Contains(box, room.Location);

            case Tag tag:
                return Contains(box, tag.Position);

            case TextNote note:
                return Contains(box, note.Position);

            default:
                return false;
        }
    }

    private static bool Contains(BoundingBox2D box, Point2D point) =>
        point.X >= box.MinX && point.X <= box.MaxX && point.Y >= box.MinY && point.Y <= box.MaxY;

    /// <summary>
    /// Whether a segment meets a box: either end inside it, or the segment crossing an edge.
    ///
    /// Comparing bounding boxes instead would pick up a long diagonal wall from a band nowhere
    /// near it, which makes band selection feel arbitrary.
    /// </summary>
    private static bool SegmentTouches(Point2D a, Point2D b, BoundingBox2D box)
    {
        if (Contains(box, a) || Contains(box, b)) return true;

        var corners = new[]
        {
            new Point2D(box.MinX, box.MinY), new Point2D(box.MaxX, box.MinY),
            new Point2D(box.MaxX, box.MaxY), new Point2D(box.MinX, box.MaxY)
        };

        for (var i = 0; i < 4; i++)
            if (SegmentsCross(a, b, corners[i], corners[(i + 1) % 4])) return true;

        return false;
    }

    private static bool SegmentsCross(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        double Side(Point2D p, Point2D q, Point2D r) => (q - p).Cross(r - p);

        var d1 = Side(c, d, a);
        var d2 = Side(c, d, b);
        var d3 = Side(a, b, c);
        var d4 = Side(a, b, d);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    // ---- dragging --------------------------------------------------------------

    private GripKind GripAt(Wall wall, Point2D model)
    {
        var radius = GripPixelRadius / PixelsPerMm;

        if (wall.Start.DistanceTo(model) <= radius) return GripKind.Start;
        if (wall.End.DistanceTo(model) <= radius) return GripKind.End;
        if (SplineGripAt(wall, model) is not null) return GripKind.SplinePoint;

        // An elliptical or spline wall keeps its shape: bending it would make it an arc.
        if (!wall.IsElliptical && !wall.IsSpline && BendGrip(wall).DistanceTo(model) <= radius) return GripKind.Bend;

        return GripKind.None;
    }

    /// <summary>
    /// Which of a spline wall's points is under the cursor. Only a whole spline has them to
    /// drag: a wall split off one is a stretch of a curve whose other points are in another wall.
    /// </summary>
    private int? SplineGripAt(Wall wall, Point2D model)
    {
        if (!IsWholeSpline(wall)) return null;

        var radius = GripPixelRadius / PixelsPerMm;
        foreach (var (index, point) in wall.LocationCurve.SplinePoints())
            if (point.DistanceTo(model) <= radius) return index;

        return null;
    }

    private static bool IsWholeSpline(Wall wall) =>
        wall.IsSpline && wall.Spline!.From == 0 && wall.Spline.To == wall.Spline.Segments;

    /// <summary>Where the bend grip sits: halfway along the wall, on its drawn line.</summary>
    private static Point2D BendGrip(Wall wall) => wall.LocationCurve.PointAt(wall.Length / 2);

    /// <summary>
    /// How close to the chord the cursor must come for a bent wall to snap straight again, in
    /// screen pixels: without it a wall could only ever be made almost straight.
    /// </summary>
    private const double StraightenPixels = 6;

    /// <summary>The most a wall may turn through: just short of a full circle.</summary>
    private const double MaxBulge = 20;

    private void BeginEndGripDrag(GripKind grip, Point2D raw)
    {
        if (SelectedWall is not { } wall) return;

        _dragging = grip;
        _dragAnchor = raw;
        _dragOriginalStart = wall.Start;
        _dragOriginalEnd = wall.End;
        _dragOriginalBulge = wall.Bulge;
        _dragOriginalSpline = wall.Spline;
        if (grip == GripKind.SplinePoint) _dragSplineIndex = SplineGripAt(wall, raw) ?? 0;

        // At a locked corner the other walls' ends go where this end goes.
        _followers.Clear();
        if (Document is not null && grip is GripKind.Start or GripKind.End)
        {
            var corner = grip == GripKind.Start ? wall.Start : wall.End;
            if (WallJointLock.IsLocked(Document, wall.LevelId, corner))
                foreach (var (other, atStart) in WallCorners.At(Document, wall.LevelId, corner).Where(e => !ReferenceEquals(e.Wall, wall)))
                    _followers.Add((other, atStart, other.Start, other.End));
        }

        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    /// <summary>
    /// Starts moving the selection.
    ///
    /// The move is written straight to the model so the plan follows the cursor, and recorded
    /// as one command on release. Recording each mouse move would bury real edits under
    /// hundreds of one-pixel nudges.
    /// </summary>
    /// <summary>The wall ends at locked corners that follow a drag, and where their walls were before it.</summary>
    private readonly List<(Wall Wall, bool AtStart, Point2D Start, Point2D End)> _followers = new();

    private void MoveFollowersTo(Point2D corner)
    {
        foreach (var (follower, atStart, _, _) in _followers)
        {
            if (atStart) follower.Start = corner;
            else follower.End = corner;
        }
    }

    /// <summary>The following walls' moves, to record with the drag that caused them.</summary>
    private IEnumerable<IUndoableCommand> FollowerMoves() =>
        _followers.Select(f => (IUndoableCommand)new MoveWallCommand(f.Wall, f.Start, f.End, f.Wall.Start, f.Wall.End, "Move"));

    /// <summary>What a move drag carries: the selection, and whatever is locked to it.</summary>
    private readonly List<Element> _moveSet = new();

    private void BeginMoveDrag(Point2D raw)
    {
        if (!_selection.Any(ElementTransforms.CanMove)) return;

        _dragging = GripKind.Move;

        // Walls locked to the ones selected move with them, as Revit's locked joins do.
        _moveSet.Clear();
        _moveSet.AddRange(_selection);
        if (Document is not null)
            foreach (var wall in _selection.OfType<Wall>().ToList())
            foreach (var locked in WallLamination.LockedGroup(Document, wall))
                if (!_moveSet.Contains(locked)) _moveSet.Add(locked);

        // At the locked corners of what moves, the walls staying put stretch to keep meeting it.
        _followers.Clear();
        if (Document is not null)
            foreach (var (follower, atStart) in WallJointLock.Followers(Document, _moveSet.OfType<Wall>().ToList()))
                _followers.Add((follower, atStart, follower.Start, follower.End));

        _dragAnchor = raw;
        _dragLastPoint = SnapToGrid(raw);
        _dragTotal = default;

        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private void DragTo(Point2D raw)
    {
        if (_dragging == GripKind.Move)
        {
            var target = SnapToGrid(raw);
            var step = target - _dragLastPoint;

            if (step.X == 0 && step.Y == 0) return;

            foreach (var element in _moveSet)
                if (ElementTransforms.CanMove(element))
                    ElementTransforms.Move(element, step);

            foreach (var (follower, atStart, _, _) in _followers)
            {
                if (atStart) follower.Start += step;
                else follower.End += step;
            }

            _dragLastPoint = target;
            _dragTotal += step;

            _cursorIsSnapped = false;
            InvalidateVisual();
            return;
        }

        if (_dragging == GripKind.Corner)
        {
            var at = SnapToGrid(raw);
            foreach (var (cornerWall, atStart, _, _) in _cornerWalls)
            {
                if (atStart) cornerWall.Start = at;
                else cornerWall.End = at;
            }

            _cursorModel = at;
            _cursorIsSnapped = false;
            CursorMoved?.Invoke(this, _cursorModel);
            InvalidateVisual();
            return;
        }

        if (SelectedWall is not { } wall) return;

        switch (_dragging)
        {
            case GripKind.Start:
                wall.Start = _followers.Count > 0 ? SnapToGrid(raw) : SnapPoint(raw, wall, out _cursorIsSnapped);
                MoveFollowersTo(wall.Start);
                break;

            case GripKind.End:
                wall.End = _followers.Count > 0 ? SnapToGrid(raw) : SnapPoint(raw, wall, out _cursorIsSnapped);
                MoveFollowersTo(wall.End);
                break;

            case GripKind.SplinePoint:
            {
                // The point goes where the cursor is, in the frame the wall's ends set, and the
                // curve is drawn again through it.
                var at = SnapPoint(raw, wall, out _cursorIsSnapped);
                wall.Spline = wall.Spline!.WithPointAt(_dragSplineIndex, at, wall.Start, wall.End);

                _cursorModel = at;
                CursorMoved?.Invoke(this, _cursorModel);
                HintChanged?.Invoke(this, $"Length {Units.FormatLength(wall.Length)}.");
                InvalidateVisual();
                return;
            }

            case GripKind.Bend:
            {
                // The arc passes through the cursor, unless the cursor is back on the straight
                // line between the ends, where the wall snaps straight.
                var offChord = Line2D.Through(wall.Start, wall.End).ClosestPointTo(raw).DistanceTo(raw);
                wall.Bulge = offChord * PixelsPerMm <= StraightenPixels
                    ? 0
                    : Math.Clamp(WallCurve.BulgeThrough(wall.Start, wall.End, raw), -MaxBulge, MaxBulge);

                _cursorModel = raw;
                _cursorIsSnapped = false;
                HintChanged?.Invoke(this, wall.IsCurved
                    ? $"Radius {Units.FormatLength(wall.LocationCurve.Radius)}, length {Units.FormatLength(wall.Length)}. Bring it back to the straight line to straighten."
                    : "Straight.");
                CursorMoved?.Invoke(this, _cursorModel);
                InvalidateVisual();
                return;
            }

            default:
                return;
        }

        _cursorModel = _dragging == GripKind.Start ? wall.Start : wall.End;
        CursorMoved?.Invoke(this, _cursorModel);
        InvalidateVisual();
    }

    private void EndDrag()
    {
        if (_dragging == GripKind.None) return;

        var grip = _dragging;
        _dragging = GripKind.None;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (grip == GripKind.Move)
        {
            if (_dragTotal.X == 0 && _dragTotal.Y == 0) return;

            var command = new MoveElementsCommand(_moveSet.ToList(), _dragTotal);
            if (_followers.Count > 0) History?.Record(new CompositeCommand("Move", FollowerMoves().Prepend(command).ToList()));
            else if (!command.IsEmpty) History?.Record(command);

            _dragTotal = default;
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        if (grip == GripKind.Corner)
        {
            var shifted = _cornerWalls.Where(c => c.Wall.Start != c.Start || c.Wall.End != c.End).ToList();
            if (shifted.Count == 0) return;

            // A corner dragged onto a wall's other end would leave it no length at all.
            if (shifted.Any(c => c.Wall.Length <= WallJoins.JoinTolerance))
            {
                foreach (var (cornerWall, _, start, end) in _cornerWalls) (cornerWall.Start, cornerWall.End) = (start, end);
                HintChanged?.Invoke(this, "A wall cannot have zero length.");
                InvalidateVisual();
                return;
            }

            History?.Record(new CompositeCommand("Move Wall Point",
                shifted.Select(c => new MoveWallCommand(c.Wall, c.Start, c.End, c.Wall.Start, c.Wall.End, "Move Wall Point"))));
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        if (SelectedWall is not { } wall) return;

        if (grip == GripKind.SplinePoint)
        {
            if (Equals(wall.Spline, _dragOriginalSpline)) return;

            History?.Record(new ReshapeSplineWallCommand(wall, _dragOriginalSpline, wall.Spline));
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        if (grip == GripKind.Bend)
        {
            if (wall.Bulge == _dragOriginalBulge) return;

            History?.Record(new BendWallCommand(wall, _dragOriginalBulge, wall.Bulge));
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        var moved = wall.Start != _dragOriginalStart || wall.End != _dragOriginalEnd;
        if (!moved) return;

        // A drag that collapses a wall to nothing is refused rather than recorded.
        if (wall.Length <= WallJoins.JoinTolerance || _followers.Any(f => f.Wall.Length <= WallJoins.JoinTolerance))
        {
            wall.Start = _dragOriginalStart;
            wall.End = _dragOriginalEnd;
            foreach (var (follower, _, start, end) in _followers) (follower.Start, follower.End) = (start, end);
            HintChanged?.Invoke(this, "A wall cannot have zero length.");
            InvalidateVisual();
            return;
        }

        var endMove = new MoveWallCommand(wall, _dragOriginalStart, _dragOriginalEnd, wall.Start, wall.End, "Move Wall End");
        History?.Record(_followers.Count == 0
            ? endMove
            : new CompositeCommand("Move Wall End", FollowerMoves().Prepend(endMove).ToList()));

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ---- tools -----------------------------------------------------------------

    private void PlaceWallPoint(Point2D model)
    {
        if (Document is null) return;

        if (_pendingWallStart is null)
        {
            _pendingWallStart = model;
            _chainWall = null;
            _chainFirstWall = null;
            _chainFirstClick = model;
            _pendingArcEnd = null;
            _splinePoints.Clear();
            HintChanged?.Invoke(this, FirstClickHint());
        }
        else if (DrawsClosedShape)
        {
            PlaceShape(model);
        }
        else if (_drawShape == WallShape.Spline)
        {
            PlaceSplinePoint(model);
        }
        else if (DrawArcs)
        {
            PlaceArcPoint(model);
        }
        else if (_pendingWallStart.Value.DistanceTo(model) >= SnapStepMm)
        {
            var from = _pendingWallStart.Value;
            var (start, end) = WallDrawing.OffsetSegment(from, model, DrawOffset, DrawFlipped);
            var commands = new List<IUndoableCommand>();

            // With an offset, the clicks are not where the walls meet: pull the previous wall
            // round to the corner of the two offset lines, and start this one there.
            if (DrawOffset != 0 && _chainWall is { } previous && Document.Elements.Contains(previous) &&
                WallDrawing.Corner((previous.Start, previous.End), (start, end), from, DrawOffset) is { } corner)
            {
                commands.Add(new MoveWallCommand(previous, previous.Start, previous.End, previous.Start, corner, "Draw Wall"));
                start = corner;
            }

            // Arriving back at the first click closes the loop at its corner too.
            if (DrawOffset != 0 && _chainFirstWall is { } first && !ReferenceEquals(first, _chainWall) &&
                Document.Elements.Contains(first) && model.DistanceTo(_chainFirstClick) < 1 &&
                WallDrawing.Corner((start, end), (first.Start, first.End), model, DrawOffset) is { } closing)
            {
                commands.Add(new MoveWallCommand(first, first.Start, first.End, closing, first.End, "Draw Wall"));
                end = closing;
            }

            var wall = new Wall
            {
                Start = start,
                End = end,
                TypeId = ActiveWallTypeId,
                LevelId = ActiveLevelId,
                LocationLine = ActiveLocationLine,
                Flipped = DrawFlipped
            };

            commands.Add(new AddElementCommand(Document, Configured(wall), "Draw Wall"));
            Apply(commands.Count == 1 ? commands[0] : new CompositeCommand("Draw Wall", commands));

            // Chain into the next wall so a room can be traced without re-clicking.
            _pendingWallStart = model;
            _chainWall = wall;
            _chainFirstWall ??= wall;
            Select(wall);
        }

        InvalidateVisual();
    }

    /// <summary>
    /// The second and third clicks of an arc wall: where it ends, then a point it passes
    /// through (specification section 3.1, "start-end-radius arc"). Arcs chain like straight
    /// walls, each starting where the last ended.
    /// </summary>
    private void PlaceArcPoint(Point2D model)
    {
        if (Document is null || _pendingWallStart is not { } from) return;

        if (_pendingArcEnd is null)
        {
            if (from.DistanceTo(model) < SnapStepMm) return;

            _pendingArcEnd = model;
            HintChanged?.Invoke(this, _drawShape == WallShape.PartialEllipse
                ? "Now click a point the ellipse passes through: it sets the other axis. Esc cancels."
                : "Now click a point the arc passes through. Esc cancels.");
            return;
        }

        var end = _pendingArcEnd.Value;
        if (ThreePointCurve(from, end, model) is not { } curve)
        {
            HintChanged?.Invoke(this, "That point is on the straight line between the ends. Pick one off it.");
            return;
        }

        // An offset moves the curve in or out toward the exterior side.
        var wall = ThreePointWall(curve);

        Apply(new AddElementCommand(Document, Configured(wall), "Draw Wall"));

        _pendingWallStart = end;
        _pendingArcEnd = null;
        _chainWall = wall;
        _chainFirstWall ??= wall;
        Select(wall);

        HintChanged?.Invoke(this, $"{(curve.IsElliptical ? "Half ellipse" : "Arc")} placed. Click where the next one ends, or Esc to stop.");
    }

    // ---- the Wall Joins tool --------------------------------------------------------------

    private readonly List<Point2D> _selectedJunctions = new();

    /// <summary>The junctions picked with the Wall Joins tool.</summary>
    public IReadOnlyList<Point2D> SelectedJunctions => _selectedJunctions;

    /// <summary>Raised when the junctions picked change, so the option bar can show their settings.</summary>
    public event EventHandler? JunctionsChanged;

    /// <summary>A click with the Wall Joins tool: the junction under it, added with Ctrl, or instead of the others.</summary>
    private void PickJunction(Point screen)
    {
        if (Document is null) return;

        var junction = WallJunctions.On(Document, ActiveLevelId)
            .Cast<Point2D?>()
            .FirstOrDefault(point => (ModelToScreen(point!.Value) - screen).Length <= 10);

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _selectedJunctions.Clear();

        if (junction is { } picked)
        {
            var already = _selectedJunctions.FindIndex(p => p.DistanceTo(picked) <= WallJoins.JoinTolerance);
            if (already >= 0) _selectedJunctions.RemoveAt(already);
            else _selectedJunctions.Add(picked);

            if (WallJunctions.Ends(Document, ActiveLevelId, picked).Count > WallJunctions.MaximumWalls)
                HintChanged?.Invoke(this, "More than four walls meet here. Change their ends one by one in Properties instead.");
        }

        JunctionsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Applies settings to every picked junction's wall ends, as one step.</summary>
    private void ApplyToJunctions(Func<Point2D, IEnumerable<(Wall Wall, bool AtStart, WallJoinKind? Join, WallJoinCleanup? Cleanup)>> ends, string name)
    {
        if (Document is null || _selectedJunctions.Count == 0) return;

        var all = _selectedJunctions.SelectMany(ends).ToList();
        if (all.Count == 0) return;

        Apply(new SetWallEndsCommand(all, name));
        JunctionsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Butt, mitre or square off at the picked junctions, keeping each one's order.</summary>
    public void SetJunctionType(JunctionType type) =>
        ApplyToJunctions(point => WallJunctions
            .Configure(Document!, ActiveLevelId, point, type, WallJunctions.OrderOf(Document!, ActiveLevelId, point))
            .Select(e => (e.Wall, e.AtStart, (WallJoinKind?)e.Join, (WallJoinCleanup?)null)),
            type switch { JunctionType.Mitre => "Mitre Join", JunctionType.SquareOff => "Square Off Join", _ => "Butt Join" });

    /// <summary>Previous and Next: the one picked junction put together the other way round.</summary>
    public void CycleJunctionOrder(int step)
    {
        if (Document is null || _selectedJunctions.Count != 1) return;

        var point = _selectedJunctions[0];
        var type = WallJunctions.TypeOf(Document, ActiveLevelId, point);
        if (type == JunctionType.Mitre) return;

        var order = WallJunctions.OrderOf(Document, ActiveLevelId, point) + step;
        ApplyToJunctions(p => WallJunctions.Configure(Document, ActiveLevelId, p, type, order)
            .Select(e => (e.Wall, e.AtStart, (WallJoinKind?)e.Join, (WallJoinCleanup?)null)), "Wall Join Order");
    }

    /// <summary>Whether Previous and Next have anything to step through.</summary>
    public bool CanCycleJunctionOrder =>
        Document is not null && _selectedJunctions.Count == 1 &&
        WallJunctions.TypeOf(Document, ActiveLevelId, _selectedJunctions[0]) != JunctionType.Mitre &&
        WallJunctions.Orders(Document, ActiveLevelId, _selectedJunctions[0]).Count > 1;

    /// <summary>How the picked junctions are cleaned up in plan.</summary>
    public void SetJunctionCleanup(WallJoinCleanup cleanup) =>
        ApplyToJunctions(point => WallJunctions.Ends(Document!, ActiveLevelId, point)
            .Select(e => (e.Wall, e.AtStart, (WallJoinKind?)null, (WallJoinCleanup?)cleanup)), "Wall Join Display");

    /// <summary>Allow Join or Disallow Join at the picked junctions: disallowed, the ends stop joining anything.</summary>
    public void SetJunctionsAllowed(bool allowed) =>
        ApplyToJunctions(point => WallJunctions.Ends(Document!, ActiveLevelId, point)
            .Select(e => (e.Wall, e.AtStart, (WallJoinKind?)(allowed ? WallJoinKind.Auto : WallJoinKind.Disallow), (WallJoinCleanup?)null)),
            allowed ? "Allow Join" : "Disallow Join");

    /// <summary>The junction squares: grey where walls meet, blue where picked.</summary>
    private void DrawJunctions(DrawingContext dc)
    {
        if (Document is null || ActiveTool != PlanTool.WallJoins) return;

        foreach (var point in WallJunctions.On(Document, ActiveLevelId))
        {
            var at = ModelToScreen(point);
            var picked = _selectedJunctions.Any(p => p.DistanceTo(point) <= WallJoins.JoinTolerance);
            dc.DrawRectangle(picked ? _gripBrush : JunctionBrush, picked ? _selectedPen : _gripPen, new Rect(at.X - 6, at.Y - 6, 12, 12));
        }
    }

    private static readonly Brush JunctionBrush = CreateJunctionBrush();

    private static Brush CreateJunctionBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x90, 0x9A, 0xA0, 0xA8));
        brush.Freeze();
        return brush;
    }

    // ---- wall openings ----------------------------------------------------------------------

    /// <summary>The size a new wall opening is cut at, from the option bar. Millimetres.</summary>
    public double NewOpeningWidth { get; set; } = 1000;

    public double NewOpeningHeight { get; set; } = 1000;

    public double NewOpeningSill { get; set; } = 900;

    /// <summary>Wall Opening: a rectangular hole through the wall clicked, centred where it was clicked and kept inside the wall.</summary>
    private void PlaceWallOpening(Point2D raw)
    {
        if (Document is null) return;

        if (HitTestWall(raw) is not { } wall || Document.IsCurtainWall(wall))
        {
            HintChanged?.Invoke(this, "Click on a wall - not a curtain wall - where the opening goes.");
            return;
        }

        var width = Math.Min(NewOpeningWidth, wall.Length);
        var along = Math.Clamp(wall.LocationCurve.Locate(raw).Along, width / 2, wall.Length - width / 2);
        var opening = new WallOpening
        {
            HostWallId = wall.Id,
            LevelId = wall.LevelId,
            DistanceAlongWall = along,
            Width = width,
            Height = NewOpeningHeight,
            SillHeight = NewOpeningSill
        };

        Apply(new AddElementCommand(Document, opening, "Wall Opening"));
        HintChanged?.Invoke(this, $"Opening cut, {Units.FormatLength(width)} wide and {Units.FormatLength(NewOpeningHeight)} high. Click another wall, or Esc.");
        InvalidateVisual();
    }

    /// <summary>The wall openings whose part of their wall the point is in.</summary>
    private IEnumerable<WallOpening> WallOpeningsAt(Point2D model)
    {
        if (Document is null) yield break;

        foreach (var cut in OnActiveLevel<WallOpening>())
        {
            if (Document.Walls.FirstOrDefault(w => w.Id == cut.HostWallId) is not { } wall || Document.GetWallType(wall) is not { } type) continue;

            var hole = cut.Hole(wall);
            var (along, across) = wall.Locate(type.Structure, model);
            if (along >= hole.From && along <= hole.To && Math.Abs(across) <= type.Width / 2 + 4 / PixelsPerMm) yield return cut;
        }
    }

    /// <summary>The opening's footprint in plan: where it runs through its wall, face to face.</summary>
    private IReadOnlyList<Point2D>? OpeningOutline(WallOpening cut)
    {
        if (Document?.Walls.FirstOrDefault(w => w.Id == cut.HostWallId) is not { } wall || Document.GetWallType(wall) is not { } type) return null;

        var hole = cut.Hole(wall);
        var half = type.Width / 2;
        return new[]
        {
            wall.PointAt(type.Structure, hole.From, half), wall.PointAt(type.Structure, hole.To, half),
            wall.PointAt(type.Structure, hole.To, -half), wall.PointAt(type.Structure, hole.From, -half)
        };
    }

    /// <summary>Selected openings outlined, since in plan an opening is otherwise only a gap.</summary>
    private void DrawSelectedOpenings(DrawingContext dc)
    {
        foreach (var cut in _selection.OfType<WallOpening>())
            if (OpeningOutline(cut) is { } ring)
                DrawModelPolyline(dc, _selectedPen, ring.Append(ring[0]).ToList());
    }

    // ---- Join Geometry for parallel walls -------------------------------------------------

    private Wall? _joinFirst;

    /// <summary>Join Geometry: the first wall clicked, then the one to join it to - or unjoin, if they are joined already.</summary>
    private void JoinGeometryAt(Point2D raw)
    {
        if (Document is null) return;

        if (HitTestWall(raw) is not { } wall)
        {
            HintChanged?.Invoke(this, "Click on a wall.");
            return;
        }

        if (_joinFirst is not { } first || ReferenceEquals(first, wall))
        {
            _joinFirst = wall;
            Select(wall);
            HintChanged?.Invoke(this, "Now click the parallel wall to join it to, up to 150 mm away.");
            return;
        }

        _joinFirst = null;
        var joined = WallLamination.Partners(Document, first).Contains(wall);
        if (!joined && !WallLamination.CanJoinGeometry(Document, first, wall))
        {
            HintChanged?.Invoke(this, "Those walls cannot be joined: they must be straight, parallel, alongside each other and no more than 150 mm apart.");
            return;
        }

        Apply(new JoinWallsCommand(first, wall, join: !joined));
        SelectMany(new Element[] { first, wall });
        HintChanged?.Invoke(this, joined
            ? "Unjoined: doors and windows in one no longer cut the other."
            : "Joined: doors and windows in either wall now cut through both. Click two more walls, or Esc.");
        InvalidateVisual();
    }

    // ---- padlocks between walls laid face to face ----------------------------------------

    private static readonly Brush LockedBrush = CreateLockedBrush();

    private static Brush CreateLockedBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB8, 0x3A));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The padlocks to show for the selected wall: one on each face it shares with another
    /// wall - joined to it already, or only lying against it - where a click locks the two.
    /// </summary>
    private IReadOnlyList<(Point2D At, Wall Partner)> LockGrips()
    {
        if (Document is null || SelectedWall is not { } wall || !WallLamination.CanJoin(Document, wall))
            return Array.Empty<(Point2D, Wall)>();

        return WallLamination.Partners(Document, wall)
            .Concat(WallLamination.Touching(Document, wall))
            .Distinct()
            .Where(partner => partner.LevelId == wall.LevelId && WallLamination.CanJoin(Document, partner))
            .Select(partner => (At: WallLockPoint.Between(Document, wall, partner), Partner: partner))
            .Where(grip => grip.At is not null)
            .Select(grip => (grip.At!.Value, grip.Partner))
            .ToList();
    }

    private (Point2D At, Wall Partner)? LockGripAt(Point screen)
    {
        foreach (var grip in LockGrips())
            if ((ModelToScreen(grip.At) - screen).Length <= 10) return grip;

        return null;
    }

    /// <summary>
    /// The padlocks at the selected wall's corners: at each end where other walls meet it,
    /// drawn a little in from the end so the end's own grip stays clear.
    /// </summary>
    private IReadOnlyList<(Point2D Corner, Point2D At)> CornerLocks()
    {
        if (Document is null || SelectedWall is not { } wall) return Array.Empty<(Point2D, Point2D)>();

        var inset = Math.Min(24 / PixelsPerMm, wall.Length / 3);
        var locks = new List<(Point2D, Point2D)>();
        foreach (var atStart in new[] { true, false })
        {
            var corner = atStart ? wall.Start : wall.End;
            if (!WallJointLock.IsCorner(Document, wall.LevelId, corner)) continue;

            var along = atStart ? inset : wall.Length - inset;
            locks.Add((corner, wall.LocationCurve.PointAt(along)));
        }

        return locks;
    }

    private Point2D? CornerLockAt(Point screen)
    {
        foreach (var (corner, at) in CornerLocks())
            if ((ModelToScreen(at) - screen).Length <= 10) return corner;

        return null;
    }

    /// <summary>Locks the corner, so the walls meeting there stay meeting when any of them moves, or unlocks it.</summary>
    private void ToggleCornerLock(Point2D corner)
    {
        if (Document is null || SelectedWall is not { } wall) return;

        var locked = WallJointLock.IsLocked(Document, wall.LevelId, corner);
        Apply(new SetJointLockCommand(Document, wall.LevelId, corner, !locked));
        HintChanged?.Invoke(this, locked
            ? "Corner unlocked: moving one of these walls leaves the others where they are."
            : "Corner locked: move any of these walls and the others stretch to stay joined at it.");
        InvalidateVisual();
    }

    /// <summary>Locks every corner of the walls given, or unlocks them all when they are all locked already.</summary>
    public void ToggleCornerLocks(IReadOnlyList<Wall> walls)
    {
        if (Document is null) return;

        var corners = walls
            .SelectMany(w => new[] { (w.LevelId, Point: w.Start), (w.LevelId, Point: w.End) })
            .Where(c => WallJointLock.IsCorner(Document, c.LevelId, c.Point))
            .DistinctBy(c => (c.LevelId, Math.Round(c.Point.X), Math.Round(c.Point.Y)))
            .ToList();
        if (corners.Count == 0)
        {
            HintChanged?.Invoke(this, "None of the selected walls meets another at an end.");
            return;
        }

        var lockThem = corners.Any(c => !WallJointLock.IsLocked(Document, c.LevelId, c.Point));
        Apply(new CompositeCommand(lockThem ? "Lock Corners" : "Unlock Corners",
            corners.Select(c => (IUndoableCommand)new SetJointLockCommand(Document, c.LevelId, c.Point, lockThem)).ToList()));
        HintChanged?.Invoke(this, lockThem
            ? $"{corners.Count} corner{(corners.Count == 1 ? "" : "s")} locked: the walls stay joined there when moved."
            : "Corners unlocked.");
        InvalidateVisual();
    }

    /// <summary>Locks the selected wall to one against it - joining them if they are not - or unlocks them.</summary>
    private void ToggleLock(Wall partner)
    {
        if (Document is null || SelectedWall is not { } wall) return;

        var locked = WallLockPoint.AreLocked(wall, partner);
        if (!locked && (!WallLamination.CanLock(wall) || !WallLamination.CanLock(partner)))
        {
            HintChanged?.Invoke(this, "A leaning wall cannot be locked: its faces are not where the plan shows them.");
            return;
        }

        Apply(new SetWallLockCommand(wall, partner, !locked));
        HintChanged?.Invoke(this, locked
            ? "Unlocked: the walls move separately now. They stay joined, so doors and windows still cut through both."
            : "Locked: the two walls move together, and doors and windows cut through both.");
        InvalidateVisual();
    }

    /// <summary>A padlock: shut and gold when the walls are locked, open when they are not.</summary>
    private void DrawPadlock(DrawingContext dc, Point at, bool locked)
    {
        var shackle = new StreamGeometry();
        using (var ctx = shackle.Open())
        {
            // An open lock has its shackle lifted clear of the body on one side.
            var lift = locked ? 0 : 4;
            ctx.BeginFigure(new Point(at.X - 4, at.Y - 1 - lift), false, false);
            ctx.LineTo(new Point(at.X - 4, at.Y - 5 - lift), true, false);
            ctx.ArcTo(new Point(at.X + 4, at.Y - 5 - lift), new Size(4, 4), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(at.X + 4, at.Y - (locked ? 1 : 3) - lift), true, false);
        }

        shackle.Freeze();
        dc.DrawGeometry(null, _gripPen, shackle);
        dc.DrawRoundedRectangle(locked ? LockedBrush : _gripBrush, _gripPen, new Rect(at.X - 6, at.Y - 1, 12, 9), 1.5, 1.5);
    }

    // ---- place by segment and by room ---------------------------------------------------

    // The walls a click where the cursor is would place, and where they were worked out.
    private IReadOnlyList<Wall> _placementPreview = Array.Empty<Wall>();
    private Point2D? _placementPreviewAt;

    /// <summary>
    /// The wall a click beside it would line, and on which side: the nearest one whose body
    /// the cursor is on or just off.
    /// </summary>
    private (Wall Host, bool Exterior)? SegmentHostAt(Point2D raw)
    {
        if (Document is null) return null;
        RefreshViewFilter();

        (Wall Wall, bool Exterior, double Off)? best = null;
        foreach (var wall in OnActiveLevel<Wall>())
        {
            if (Document.GetWallType(wall) is not { } type || Document.IsCurtainWall(wall)) continue;

            var (along, across) = wall.Locate(type.Structure, raw);
            if (along < 0 || along > wall.Length) continue;

            var off = Math.Abs(across) - type.Width / 2;
            if (off > 24 / PixelsPerMm) continue;
            if (best is null || off < best.Value.Off) best = (wall, across >= 0, off);
        }

        return best is { } found ? (found.Wall, found.Exterior) : null;
    }

    /// <summary>The walls a click here would place, and the wall they are laid against for Place by Segment.</summary>
    private IReadOnlyList<Wall> PlacementAt(Point2D raw, out IReadOnlyList<Wall> against)
    {
        against = Array.Empty<Wall>();
        if (Document is null) return Array.Empty<Wall>();

        if (_drawShape == WallShape.BySegment)
        {
            if (SegmentHostAt(raw) is not { } target) return Array.Empty<Wall>();

            against = new[] { target.Host };
            return WallPlacement.BySegment(Document, target.Host, target.Exterior, ActiveWallTypeId, ActiveLevelId) is { } lining
                ? new[] { lining }
                : Array.Empty<Wall>();
        }

        var boundary = RoomBoundary.Trace(Document, ActiveLevelId, raw);
        return boundary.IsEnclosed
            ? WallPlacement.ByRoom(boundary.Polygon, ActiveWallTypeId, ActiveLevelId)
            : Array.Empty<Wall>();
    }

    /// <summary>
    /// Place by Segment: a wall along the face of the wall clicked beside. Place by Room: walls
    /// round every face of the room clicked in. Both as one step, joined where Auto Join is on.
    /// </summary>
    private void PlaceAgainst(Point2D raw)
    {
        if (Document is null) return;

        var walls = PlacementAt(raw, out var against).ToList();
        if (walls.Count == 0)
        {
            HintChanged?.Invoke(this, _drawShape == WallShape.BySegment
                ? "Click beside a wall, on the side the new wall goes."
                : "Click inside a space enclosed by walls.");
            return;
        }

        foreach (var wall in walls)
        {
            Configured(wall);
            JoinToTouching(wall, against);
        }

        var name = _drawShape == WallShape.BySegment ? "Place by Segment" : "Place by Room";
        Apply(walls.Count == 1 ? new AddElementCommand(Document, walls[0], name) : new AddElementsCommand(Document, walls, name));
        SelectMany(walls);

        _placementPreview = Array.Empty<Wall>();
        _placementPreviewAt = null;

        var joined = walls.Count(wall => wall.JoinedTo.Count > 0);
        HintChanged?.Invoke(this, (walls.Count == 1 ? "Wall placed along the face." : $"{walls.Count} walls placed round the room.") +
            (joined > 0 ? $" Joined: doors and windows cut through{(walls.Any(w => w.LockedToJoined) ? ", and they move together" : string.Empty)}." : string.Empty));
        InvalidateVisual();
    }

    // ---- spline and freehand walls ------------------------------------------------------

    /// <summary>How near its first point a spline or stroke has to come back to close into a loop, in screen pixels.</summary>
    private const double ClosePixels = 10;

    /// <summary>
    /// A click on a spline wall: another point it passes through. Clicking the last point
    /// again finishes it there, and clicking the first closes it into a loop.
    /// </summary>
    private void PlaceSplinePoint(Point2D model)
    {
        if (_pendingWallStart is not { } start) return;

        if (_splinePoints.Count >= 2 && model.DistanceTo(start) * PixelsPerMm <= ClosePixels)
        {
            FinishSpline(closed: true);
            return;
        }

        var last = _splinePoints.Count > 0 ? _splinePoints[^1] : start;
        if (last.DistanceTo(model) < SnapStepMm)
        {
            if (_splinePoints.Count > 0) FinishSpline(closed: false);
            return;
        }

        _splinePoints.Add(model);
        HintChanged?.Invoke(this, $"{_splinePoints.Count + 1} points. Keep clicking; Enter or a double click finishes at the last one, the first point closes the loop, Esc cancels.");
        InvalidateVisual();
    }

    /// <summary>Enter while drawing: finishes a spline wall at its last point. Returns whether there was one to finish.</summary>
    public bool FinishDrawing()
    {
        if (ActiveTool != PlanTool.Wall || _drawShape != WallShape.Spline || _pendingWallStart is null || _splinePoints.Count == 0)
            return false;

        FinishSpline(closed: false);
        return true;
    }

    private void FinishSpline(bool closed)
    {
        if (_pendingWallStart is not { } start || _splinePoints.Count == 0) return;

        var points = new List<Point2D> { start };
        points.AddRange(_splinePoints);
        _pendingWallStart = null;
        _splinePoints.Clear();

        PlaceCurvePieces(WallShapes.Spline(points, closed), closed ? "Draw Spline Loop" : "Draw Spline Wall");
    }

    private void BeginStroke(Point2D raw)
    {
        _stroke = new List<Point2D> { raw };
        CaptureMouse();
        HintChanged?.Invoke(this, "Drawing: let go to place the wall. End where you started to close it into a loop.");
    }

    /// <summary>The end of a freehand stroke: a smooth wall along what it meant.</summary>
    private void FinishStroke()
    {
        var stroke = _stroke;
        _stroke = null;
        ReleaseMouseCapture();
        if (stroke is null || Document is null) return;

        var length = stroke.Zip(stroke.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
        if (length * PixelsPerMm < 20)
        {
            HintChanged?.Invoke(this, "Too short to build. Hold the button down and drag out the line of the wall.");
            InvalidateVisual();
            return;
        }

        // Ending back where it began, having gone somewhere in between, closes it.
        var closed = stroke[0].DistanceTo(stroke[^1]) * PixelsPerMm <= ClosePixels * 2 &&
                     stroke.Max(p => p.DistanceTo(stroke[0])) * PixelsPerMm > ClosePixels * 4;

        // What the stroke meant without the wobble of a hand: points within a few pixels of
        // the straight line between their neighbours are dropped.
        var points = WallShapes.Simplify(stroke, Math.Max(6 / PixelsPerMm, 10)).ToList();
        if (closed && points.Count > 3) points.RemoveAt(points.Count - 1);

        PlaceCurvePieces(WallShapes.Spline(points, closed), "Draw Freehand Wall");
    }

    /// <summary>Builds the walls of a spline or freehand shape, moved off the line drawn by the offset toward their exterior.</summary>
    private void PlaceCurvePieces(IReadOnlyList<WallPiece> pieces, string name)
    {
        if (Document is null) return;

        var walls = WallShapes.Walls(WallShapes.Offset(pieces, DrawFlipped ? -DrawOffset : DrawOffset),
                ActiveWallTypeId, ActiveLevelId, ActiveLocationLine, DrawFlipped)
            .Select(Configured)
            .ToList();

        if (walls.Count == 0)
        {
            HintChanged?.Invoke(this, "Too small to build. Spread the points further apart.");
            InvalidateVisual();
            return;
        }

        Apply(walls.Count == 1 ? new AddElementCommand(Document, walls[0], name) : new AddElementsCommand(Document, walls, name));
        SelectMany(walls);

        HintChanged?.Invoke(this, walls.Count == 1
            ? "Wall placed. Drag its round grips to reshape the curve. Draw another, or Esc to stop."
            : $"{walls.Count} walls placed round the loop. Draw another, or Esc to stop.");
        InvalidateVisual();
    }

    /// <summary>The spline being clicked out, through the cursor, as it will be built.</summary>
    private void DrawPendingSpline(DrawingContext dc, WallType? type)
    {
        if (_pendingWallStart is not { } start) return;

        var points = new List<Point2D> { start };
        points.AddRange(_splinePoints);

        var closing = _splinePoints.Count >= 2 && _cursorModel.DistanceTo(start) * PixelsPerMm <= ClosePixels;
        foreach (var point in points) dc.DrawEllipse(Brushes.Transparent, _selectedPen, ModelToScreen(point), 4, 4);
        if (!closing && _cursorModel.DistanceTo(points[^1]) >= SnapStepMm) points.Add(_cursorModel);

        var drawn = WallShapes.Spline(points, closing);
        foreach (var piece in drawn) DrawModelPolyline(dc, _previewPen, piece.Curve.Points());
        if (type is null) return;

        var pieces = WallShapes.Offset(drawn, DrawFlipped ? -DrawOffset : DrawOffset);
        foreach (var piece in pieces)
            DrawWallBody(dc, PreviewWall(piece), type, _previewPen);

        if (pieces.Count > 0) DrawFlipArrows(dc, PreviewWall(pieces[0]), type);
    }

    private Wall PreviewWall(WallPiece piece) => new()
    {
        Start = piece.Start, End = piece.End, Bulge = piece.Bulge, Ellipse = piece.Ellipse, Spline = piece.Spline,
        LocationLine = ActiveLocationLine, Flipped = DrawFlipped
    };

    /// <summary>
    /// Works out what the user is pointing at, so a dimension can follow it afterwards.
    ///
    /// Snapping already lands the cursor on a wall end or a grid crossing; this records
    /// <em>which</em> one, which is what turns a drawn measurement into a measurement of the
    /// model.
    /// </summary>
    private DimensionReference ReferenceAt(Point2D point)
    {
        if (Document is null) return DimensionReference.ToPoint(point);

        var tolerance = Math.Max(WallJoins.JoinTolerance, 2 / PixelsPerMm);

        foreach (var wall in OnActiveLevel<Wall>())
        {
            if (wall.Start.DistanceTo(point) <= tolerance) return DimensionReference.ToWall(wall, atStart: true);
            if (wall.End.DistanceTo(point) <= tolerance) return DimensionReference.ToWall(wall, atStart: false);
        }

        foreach (var grid in OnActiveLevel<Grid>())
        {
            if (grid.Line.ClosestPointTo(point).DistanceTo(point) <= tolerance)
                return DimensionReference.ToGrid(grid, point);
        }

        return DimensionReference.ToPoint(point);
    }

    private void PlaceDimensionPoint(Point2D raw)
    {
        if (Document is null) return;

        var point = SnapPoint(raw, null, out _);

        if (_pendingDimension is null)
        {
            _pendingDimension = ReferenceAt(point);
            _cursorModel = point;
            HintChanged?.Invoke(this, "Now click what to measure to. Esc cancels.");
            InvalidateVisual();
            return;
        }

        var from = _pendingDimension.Resolve(Document);
        if (from.DistanceTo(point) < SnapStepMm)
        {
            HintChanged?.Invoke(this, "Those two points are the same. Pick somewhere else.");
            return;
        }

        var dimension = new Dimension
        {
            Start = _pendingDimension,
            End = ReferenceAt(point),
            LevelId = ActiveLevelId
        };

        Apply(new AddElementCommand(Document, dimension, "Add Dimension"));
        Select(dimension);

        _pendingDimension = null;
        HintChanged?.Invoke(this, dimension.IsAssociative(Document)
            ? $"{dimension.DisplayText(Document)} — follows the model."
            : $"{dimension.DisplayText(Document)} — measured between points, not attached.");

        InvalidateVisual();
    }

    /// <summary>Tags whatever is under the cursor, reading a sensible field for its category.</summary>
    private void PlaceTag(Point2D raw)
    {
        if (Document is null) return;

        var target = HitTest(raw);
        if (target is null)
        {
            HintChanged?.Invoke(this, "Nothing there to tag. Click an element.");
            return;
        }

        var field = target.Category switch
        {
            BuiltInCategory.Rooms => "Name",
            BuiltInCategory.Walls => "Type Name",
            BuiltInCategory.Grids => "Name",
            _ => "Mark"
        };

        var tag = new Tag
        {
            TargetId = target.Id,
            Field = field,
            Position = raw + new Vector2D(600, 600),
            LevelId = ActiveLevelId
        };

        Apply(new AddElementCommand(Document, tag, $"Tag {target.Category}"));
        Select(tag);
        HintChanged?.Invoke(this, $"Tagged: {tag.Read(Document)}. Change the field on the right.");
    }

    private void PlaceTextNote(Point2D raw)
    {
        if (Document is null) return;

        var note = new TextNote { Position = SnapToGrid(raw), Text = "Note", LevelId = ActiveLevelId };

        Apply(new AddElementCommand(Document, note, "Add Text"));
        Select(note);
        HintChanged?.Invoke(this, "Note added. Type its text in the Text box on the right.");
    }

    /// <summary>
    /// Draws a gridline. The name follows the drawing convention: grids running across the
    /// page are lettered, those running up it are numbered.
    /// </summary>
    private void PlaceGridPoint(Point2D model)
    {
        if (Document is null) return;

        if (_pendingWallStart is null)
        {
            _pendingWallStart = model;
            HintChanged?.Invoke(this, "Click again to finish the gridline. Esc cancels.");
        }
        else if (_pendingWallStart.Value.DistanceTo(model) >= SnapStepMm)
        {
            var grid = new Grid { Start = _pendingWallStart.Value, End = model };
            grid.Name = Grids.NextName(Document, grid.IsVertical);

            Apply(new AddElementCommand(Document, grid, $"Draw Grid {grid.Name}"));
            Select(grid);

            _pendingWallStart = null;
            HintChanged?.Invoke(this, $"Grid {grid.Name} added. Click to start another.");
        }

        InvalidateVisual();
    }

    // ---- editing tools ---------------------------------------------------------

    /// <summary>How far the Offset tool puts its copy, in millimetres.</summary>
    public double OffsetDistance { get; set; } = 1000;

    /// <summary>Whether Mirror leaves the originals where they are and reflects copies.</summary>
    public bool MirrorKeepsOriginal { get; set; } = true;

    /// <summary>How many there are in an array, the original included.</summary>
    public int ArrayCount { get; set; } = 3;

    /// <summary>
    /// Offsets a wall: a parallel copy, the set distance away, on the side that was clicked.
    ///
    /// The doors are not copied. An offset is setting out a new line of construction next to
    /// an existing one, and a second wall arriving with the first wall's doors already cut
    /// into it would be wrong far more often than right.
    /// </summary>
    private void OffsetAt(Point2D raw)
    {
        if (Document is null) return;

        if (HitTestWall(raw) is not { } wall)
        {
            HintChanged?.Invoke(this, "Offset works on walls. Click one, on the side the copy should go.");
            return;
        }

        if (OffsetDistance <= 0)
        {
            HintChanged?.Invoke(this, "Set an offset distance greater than zero.");
            return;
        }

        if (ElementCopy.Clone(wall) is not Wall copy) return;

        // Which side of the wall the click landed on decides which way the copy goes. A curved
        // wall's copy is concentric: its ends move along the radius, and it keeps its sweep.
        var curve = wall.LocationCurve;
        var side = curve.Locate(raw).Left >= 0 ? 1.0 : -1.0;

        copy.Start = curve.At(0, OffsetDistance * side);
        copy.End = curve.At(curve.Length, OffsetDistance * side);

        Apply(new AddElementCommand(Document, copy, "Offset Wall"));
        Select(copy);

        HintChanged?.Invoke(this,
            $"Offset {Units.FormatLength(OffsetDistance)}. Click another wall, or Esc to stop.");
    }

    /// <summary>
    /// Mirrors the selection about a line picked with two clicks.
    ///
    /// By default it mirrors copies and leaves the originals, because the thing mirroring is
    /// almost always for is making the other half of something symmetrical.
    /// </summary>
    private void PlaceMirrorPoint(Point2D point)
    {
        if (Document is null) return;

        if (!_selection.Any(ElementTransforms.CanMove))
        {
            HintChanged?.Invoke(this, "Nothing to mirror. Select something with the Select tool first.");
            return;
        }

        if (_pendingWallStart is null)
        {
            _pendingWallStart = point;
            HintChanged?.Invoke(this, "Click a second point on the mirror line. Esc cancels.");
            InvalidateVisual();
            return;
        }

        if (_pendingWallStart.Value.DistanceTo(point) < SnapStepMm)
        {
            HintChanged?.Invoke(this, "The two points are too close to define a line.");
            return;
        }

        var axis = Line2D.Through(_pendingWallStart.Value, point);
        _pendingWallStart = null;

        if (MirrorKeepsOriginal)
        {
            var copies = ElementCopy.Duplicate(Document, _selection);
            foreach (var copy in copies) ElementTransforms.Mirror(copy, axis);

            Apply(new AddElementsCommand(Document, copies, "Mirror Copy"));
            SelectMany(copies.Where(ElementTransforms.CanMove));

            HintChanged?.Invoke(this, $"Mirrored {Plural(copies.Count, "element")} as copies.");
        }
        else
        {
            var command = new MirrorElementsCommand(_selection.ToList(), axis);
            Apply(command);

            HintChanged?.Invoke(this, $"Mirrored {Plural(_selection.Count, "element")}.");
        }

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Repeats the selection along a step picked with two clicks.
    ///
    /// The whole array is one command, so one undo takes it all back. An array undone copy by
    /// copy would leave a model half-built on every accidental undo.
    /// </summary>
    private void PlaceArrayPoint(Point2D point)
    {
        if (Document is null) return;

        if (!_selection.Any(ElementTransforms.CanMove))
        {
            HintChanged?.Invoke(this, "Nothing to repeat. Select something with the Select tool first.");
            return;
        }

        if (ArrayCount < 2)
        {
            HintChanged?.Invoke(this, "An array needs a count of at least 2.");
            return;
        }

        if (_pendingWallStart is null)
        {
            _pendingWallStart = point;
            HintChanged?.Invoke(this, $"Click where the next of {ArrayCount} goes. Esc cancels.");
            InvalidateVisual();
            return;
        }

        var step = point - _pendingWallStart.Value;
        _pendingWallStart = null;

        if (step.X == 0 && step.Y == 0)
        {
            HintChanged?.Invoke(this, "The spacing is zero, so every copy would sit on the original.");
            InvalidateVisual();
            return;
        }

        var placed = new List<Element>();

        for (var i = 1; i < ArrayCount; i++)
        {
            var copies = ElementCopy.Duplicate(Document, _selection);
            foreach (var copy in copies)
                if (ElementTransforms.CanMove(copy))
                    ElementTransforms.Move(copy, step * i);

            placed.AddRange(copies);
        }

        Apply(new AddElementsCommand(Document, placed, $"Array x{ArrayCount}"));

        HintChanged?.Invoke(this,
            $"Placed {ArrayCount - 1} {(ArrayCount - 1 == 1 ? "copy" : "copies")}, " +
            $"{Units.FormatLength(Math.Sqrt(step.X * step.X + step.Y * step.Y))} apart.");

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // ---- clipboard -------------------------------------------------------------

    /// <summary>
    /// Copies of what was copied, taken at the moment of copying.
    ///
    /// They are copies rather than references so that editing the originals after pressing
    /// Ctrl+C does not change what gets pasted - which is what copying means everywhere else.
    /// </summary>
    private List<Element> _clipboard = new();

    /// <summary>The centre of what was copied, which is what lands on the cursor when pasting.</summary>
    private Point2D _clipboardCentre;

    public bool CanPaste => _clipboard.Count > 0;

    public void CopySelection()
    {
        if (Document is null || _selection.Count == 0) return;

        _clipboard = ElementCopy.Duplicate(Document, _selection);
        _clipboardCentre = CentreOf(_clipboard);

        HintChanged?.Invoke(this,
            $"Copied {Plural(_clipboard.Count, "element")}. Ctrl+V pastes at the cursor; " +
            "Ctrl+Shift+V pastes in the same place, on whichever level is open.");
    }

    /// <summary>
    /// Pastes onto the level being drawn.
    ///
    /// <paramref name="inPlace"/> keeps the original coordinates, which is how a storey is
    /// built from the one below: copy the ground floor's walls, open the first floor, paste in
    /// place. Otherwise the copy is centred on the cursor.
    /// </summary>
    public void Paste(bool inPlace)
    {
        if (Document is null || _clipboard.Count == 0) return;

        // Pasting again must make new elements again, not add the same ones twice.
        var pasted = ElementCopy.Duplicate(Document, _clipboard);

        foreach (var element in pasted) MoveToLevel(element, ActiveLevelId);

        if (!inPlace)
        {
            var shift = _cursorModel - _clipboardCentre;

            foreach (var element in pasted)
                if (ElementTransforms.CanMove(element))
                    ElementTransforms.Move(element, shift);
        }

        Apply(new AddElementsCommand(Document, pasted, inPlace ? "Paste in Place" : "Paste"));
        SelectMany(pasted.Where(element => element is not IHostedElement));

        var level = Document.FindLevel(ActiveLevelId)?.Name ?? "this level";
        HintChanged?.Invoke(this, $"Pasted {Plural(pasted.Count, "element")} on {level}.");

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Re-hosts a pasted element on another storey, carrying a wall's top constraint up or down
    /// with it.
    ///
    /// A ground-floor wall that reached up to the first floor, pasted onto the first floor,
    /// should reach up to the second - not keep pointing at the level it is now standing on,
    /// which would give it no height at all.
    /// </summary>
    private void MoveToLevel(Element element, Guid levelId)
    {
        if (Document is null || element.LevelId == levelId) return;

        var ordered = Document.Levels.OrderBy(level => level.Elevation).ToList();
        var from = ordered.FindIndex(level => level.Id == element.LevelId);
        var to = ordered.FindIndex(level => level.Id == levelId);

        if (element is Wall { TopLevelId: { } top } wall && from >= 0 && to >= 0)
        {
            var height = wall.GetHeight(Document);
            var topIndex = ordered.FindIndex(level => level.Id == top);
            var shifted = topIndex < 0 ? -1 : topIndex + (to - from);

            if (shifted >= 0 && shifted < ordered.Count && shifted > to)
            {
                wall.TopLevelId = ordered[shifted].Id;
            }
            else
            {
                // There is no storey that far up; keep the wall the height it was.
                wall.TopLevelId = null;
                if (height > 0) wall.UnconnectedHeight = height;
            }
        }

        element.LevelId = levelId;
    }

    private Point2D CentreOf(IEnumerable<Element> elements)
    {
        var box = BoundingBox2D.Empty;

        foreach (var element in elements)
        {
            switch (element)
            {
                case Wall wall: box = box.Include(wall.Start).Include(wall.End); break;
                case Grid grid: box = box.Include(grid.Start).Include(grid.End); break;
                case SectionMarker section: box = box.Include(section.Start).Include(section.End); break;
                case Slab slab: box = box.Include(BoundingBox2D.Around(slab.Boundary)); break;
                case Room room: box = box.Include(room.Location); break;
                case Tag tag: box = box.Include(tag.Position); break;
                case TextNote note: box = box.Include(note.Position); break;
            }
        }

        return box.IsEmpty ? _cursorModel : box.Centre;
    }

    /// <summary>
    /// Draws a section marker, which is also the section view itself: there is no separate
    /// "create the view" step, because the cut line is all a section is.
    /// </summary>
    private void PlaceSectionPoint(Point2D model)
    {
        if (Document is null) return;

        if (_pendingWallStart is null)
        {
            _pendingWallStart = model;
            HintChanged?.Invoke(this, "Click again to finish the cut line. Esc cancels.");
        }
        else if (_pendingWallStart.Value.DistanceTo(model) >= SnapStepMm)
        {
            var section = new SectionMarker
            {
                Start = _pendingWallStart.Value,
                End = model,
                LevelId = ActiveLevelId,
                Name = Sections.NextName(Document)
            };

            Apply(new AddElementCommand(Document, section, $"Draw Section {section.Name}"));
            Select(section);

            _pendingWallStart = null;
            HintChanged?.Invoke(this,
                $"Section {section.Name} added. It is shown in the section panel on the right.");

            SectionPlaced?.Invoke(this, section);
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Places a door or window in the wall under the cursor. The opening is positioned by
    /// its distance along that wall, so it stays put when the wall is later moved.
    /// </summary>
    private void PlaceOpening(Point2D raw, bool isDoor)
    {
        if (Document is null) return;

        var wall = HitTestWall(raw);
        if (wall is null)
        {
            HintChanged?.Invoke(this, isDoor
                ? "A door needs a wall. Click on one."
                : "A window needs a wall. Click on one.");
            return;
        }

        // A curtain wall takes doors as panels of its own, not as holes cut in it.
        if (Document.IsCurtainWall(wall))
        {
            HintChanged?.Invoke(this, isDoor
                ? "That is a curtain wall: select it and use Edit Curtain Grid to make a panel a door."
                : "That is a curtain wall: its panels are already glazed.");
            return;
        }

        var typeId = isDoor ? ActiveDoorTypeId : ActiveWindowTypeId;
        var type = Document.FindType<OpeningType>(typeId);
        if (type is null)
        {
            HintChanged?.Invoke(this, "No type is selected for that tool.");
            return;
        }

        // Where along the wall the click landed, snapped, then nudged so it fits.
        var distance = Units.SnapToGrid(wall.LocationCurve.Locate(raw).Along, SnapStepMm);

        if (WallOpenings.ClampToWall(wall, type.Width, distance) is not { } placed)
        {
            HintChanged?.Invoke(this,
                $"That wall is {Units.FormatLength(wall.Length)} long - too short for a " +
                $"{Units.FormatLength(type.Width)} {(isDoor ? "door" : "window")}.");
            return;
        }

        if (!WallOpenings.CanPlace(Document, wall, type.Width, placed))
        {
            HintChanged?.Invoke(this, "There is already an opening there.");
            return;
        }

        Opening opening = isDoor
            ? new Door { SillHeight = 0 }
            : new BimWindow { SillHeight = 900 };

        opening.HostWallId = wall.Id;
        opening.DistanceAlongWall = placed;
        opening.TypeId = type.Id;
        opening.LevelId = wall.LevelId;

        Apply(new AddElementCommand(Document, opening, isDoor ? "Place Door" : "Place Window"));
        Select(opening);
        HintChanged?.Invoke(this, DefaultHintFor(ActiveTool));
    }

    /// <summary>
    /// Places a room in the enclosure under the cursor. The room is not drawn - the walls
    /// around the point are traced, so the room is whatever they enclose.
    /// </summary>
    private void PlaceRoom(Point2D raw)
    {
        if (Document is null) return;

        var boundary = RoomBoundary.Trace(Document, ActiveLevelId, raw);
        if (!boundary.IsEnclosed)
        {
            HintChanged?.Invoke(this,
                "That point is not enclosed by walls. Close the space, then place the room.");
            return;
        }

        if (RoomBoundary.FindRoomEnclosing(Document, ActiveLevelId, raw) is { } occupant)
        {
            Select(occupant);
            HintChanged?.Invoke(this,
                $"{occupant} is already here. It is now selected - edit it on the right.");
            return;
        }

        var number = Document.Elements.OfType<Room>().Count() + 1;
        var room = new Room
        {
            Location = raw,
            LevelId = ActiveLevelId,
            Name = "Room",
            Number = number.ToString("000")
        };

        Apply(new AddElementCommand(Document, room, "Place Room"));
        Select(room);
        HintChanged?.Invoke(this,
            $"Room {room.Number}: {Units.FormatArea(boundary.Area)}. Click another space.");
    }

    private static bool ContainsPoint(RoomBoundaryResult boundary, Point2D point) =>
        boundary.IsEnclosed && Polygon2D.Contains(boundary.Polygon, point);

    /// <summary>
    /// Lays a floor, ceiling or roof over the space under the cursor.
    ///
    /// Picking a space is a way of sketching the outline, not a link to it: the outline is
    /// then the slab's own. A floor does not change shape because someone moved a wall - it
    /// is a thing that was built, and it would have to be rebuilt.
    /// </summary>
    private void PlaceSlab(Point2D raw, PlanTool tool)
    {
        if (Document is null) return;

        var boundary = RoomBoundary.Trace(Document, ActiveLevelId, raw);
        if (!boundary.IsEnclosed)
        {
            HintChanged?.Invoke(this,
                "That point is not enclosed by walls. Close the space, then pick it again.");
            return;
        }

        var typeId = tool switch
        {
            PlanTool.Floor => ActiveFloorTypeId,
            PlanTool.Ceiling => ActiveCeilingTypeId,
            _ => ActiveRoofTypeId
        };

        var type = Document.FindType<SlabType>(typeId);
        if (type is null)
        {
            HintChanged?.Invoke(this,
                $"This project has no {tool.ToString().ToLowerInvariant()} types. " +
                "Reopen it and the template's types will be added.");
            return;
        }

        // One of each kind per space: two floors stacked in one room would both report their
        // area, and every takeoff downstream would count that floor twice.
        var existing = Document.Elements
            .OfType<Slab>()
            .FirstOrDefault(slab =>
                slab.LevelId == ActiveLevelId &&
                slab.Category == type.Category &&
                slab.Contains(raw));

        if (existing is not null)
        {
            Select(existing);
            HintChanged?.Invoke(this,
                $"There is already a {type.Category.ToString().TrimEnd('s').ToLowerInvariant()} here. " +
                "It is now selected.");
            return;
        }

        Slab slab = tool switch
        {
            PlanTool.Floor => new Floor(),
            PlanTool.Ceiling => new Ceiling { HeightOffset = 2400 },
            _ => new Roof { HeightOffset = 3000 }
        };

        slab.SetBoundary(boundary.Polygon);
        slab.TypeId = type.Id;
        slab.LevelId = ActiveLevelId;

        Apply(new AddElementCommand(Document, slab, $"Place {type.Category.ToString().TrimEnd('s')}"));
        Select(slab);
        HintChanged?.Invoke(this, $"{type.Name}: {Units.FormatArea(slab.Area)}.");
    }

    private void SplitAt(Point2D raw)
    {
        if (Document is null) return;

        var wall = HitTestWall(raw);
        if (wall is null)
        {
            HintChanged?.Invoke(this, "No wall there. Click a wall where it should be split.");
            return;
        }

        // Split on the location line. Snapping moves the point off a sloping wall, so it is
        // projected back on afterwards - otherwise the two halves meet at a kink. A curved wall
        // is split where the click is round the arc, to the nearest 100 mm along it.
        Point2D splitPoint;
        if (wall.IsCurved)
        {
            var curve = wall.LocationCurve;
            splitPoint = curve.PointAt(Units.SnapToGrid(curve.Locate(raw).Along, SnapStepMm));
        }
        else
        {
            var line = Line2D.Through(wall.Start, wall.End);
            splitPoint = line.ClosestPointTo(SnapToGrid(line.ClosestPointTo(raw)));
        }

        if (splitPoint.DistanceTo(wall.Start) <= SnapStepMm ||
            splitPoint.DistanceTo(wall.End) <= SnapStepMm)
        {
            HintChanged?.Invoke(this, "Too close to the end of the wall to split.");
            return;
        }

        var command = new SplitWallCommand(Document, wall, splitPoint);
        Apply(command);
        Select(command.Remainder);
        HintChanged?.Invoke(this, "Wall split. Click another wall to split it.");
    }

    private void TrimAt(Point2D raw)
    {
        if (Document is null) return;

        var picked = HitTestWall(raw);
        if (picked is null)
        {
            HintChanged?.Invoke(this, "No wall there.");
            return;
        }

        if (picked.IsCurved && _trimSubject is null)
        {
            HintChanged?.Invoke(this, "A curved wall cannot be trimmed, but straight walls can be trimmed to it: pick the straight one first.");
            return;
        }

        if (_trimSubject is null)
        {
            _trimSubject = picked;
            Select(picked);
            HintChanged?.Invoke(this, "Now click the wall to trim or extend it to. Esc cancels.");
            InvalidateVisual();
            return;
        }

        if (ReferenceEquals(picked, _trimSubject))
        {
            HintChanged?.Invoke(this, "Pick a different wall as the boundary.");
            return;
        }

        if (!WallTrim.TryResolve(_trimSubject, picked, out var newStart, out var newEnd))
        {
            HintChanged?.Invoke(this, "Those walls are parallel, so they never meet.");
            _trimSubject = null;
            InvalidateVisual();
            return;
        }

        Apply(new MoveWallCommand(
            _trimSubject, _trimSubject.Start, _trimSubject.End, newStart, newEnd, "Trim Wall"));

        Select(_trimSubject);
        _trimSubject = null;
        HintChanged?.Invoke(this, DefaultHintFor(PlanTool.Trim));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        CancelPendingOperation();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right)) return;
        if (_dragging != GripKind.None) return;

        _isPanning = true;
        _panScreenAnchor = e.GetPosition(this);
        _panModelAnchor = ViewCentre;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_isPanning) return;

        _isPanning = false;
        ReleaseMouseCapture();
        Cursor = ActiveTool == PlanTool.Select ? Cursors.Arrow : Cursors.Cross;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var screen = e.GetPosition(this);
        var modelUnderCursor = ScreenToModel(screen);

        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var newScale = Clamp(PixelsPerMm * factor);
        if (Math.Abs(newScale - PixelsPerMm) < double.Epsilon) return;
        PixelsPerMm = newScale;

        // Keep the model point under the cursor pinned to the cursor.
        ViewCentre = new Point2D(
            modelUnderCursor.X - (screen.X - ActualWidth / 2) / PixelsPerMm,
            modelUnderCursor.Y + (screen.Y - ActualHeight / 2) / PixelsPerMm);

        RaiseViewChanged();
    }

    /// <summary>Picks whatever is under the point, preferring openings over their host.</summary>
    private Element? HitTest(Point2D model) => HitCandidates(model).FirstOrDefault();

    // ---- pre-highlighting and TAB ------------------------------------------------------

    /// <summary>How far, in screen pixels, the cursor may drift before what is under it is looked for again.</summary>
    private const double HoverSlop = 4;

    // Everything under the cursor where it last settled, in pick order, and which of them is
    // outlined: the first, until TAB steps on.
    private List<Element> _hoverCandidates = new();
    private int _hoverIndex;
    private Point2D _hoverAnchor;

    private Element? Hovered => _hoverCandidates.Count == 0 ? null : _hoverCandidates[_hoverIndex % _hoverCandidates.Count];

    private readonly Pen _hoverPen = CreateHoverPen();

    private static Pen CreateHoverPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0x2B, 0x8A, 0xE6)), 3) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Outlines what a click would pick, as Revit pre-highlights it, and names it in the status
    /// bar - with how many other things are under the cursor for TAB to reach.
    /// </summary>
    private void UpdateHover(Point2D raw)
    {
        if (_hoverCandidates.Count > 0 && _hoverAnchor.DistanceTo(raw) * PixelsPerMm < HoverSlop) return;

        var before = Hovered;
        _hoverCandidates = HitCandidates(raw).Distinct().ToList();
        _hoverIndex = 0;
        _hoverAnchor = raw;

        if (ReferenceEquals(before, Hovered)) return;
        ReportHover();
        InvalidateVisual();
    }

    /// <summary>TAB: outlines the next thing under the cursor. Returns whether there was another.</summary>
    public bool CycleHover()
    {
        if (ActiveTool != PlanTool.Select || _hoverCandidates.Count < 2) return false;

        _hoverIndex = (_hoverIndex + 1) % _hoverCandidates.Count;
        ReportHover();
        InvalidateVisual();
        return true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverCandidates.Count == 0) return;

        _hoverCandidates.Clear();
        InvalidateVisual();
    }

    /// <summary>"Walls : Exterior - Brick on Block", and "TAB for alternates (1 of 3)" when there are more.</summary>
    private void ReportHover()
    {
        if (Hovered is not { } element || Document is null)
        {
            HintChanged?.Invoke(this, DefaultHintFor(PlanTool.Select));
            return;
        }

        var category = string.Concat(element.Category.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
        var type = Document.ElementTypes.FirstOrDefault(t => t.Id == element.TypeId);
        var name = type is null ? category : $"{category} : {type.Name}";

        HintChanged?.Invoke(this, _hoverCandidates.Count > 1
            ? $"{name}   ·   TAB for alternates ({_hoverIndex + 1} of {_hoverCandidates.Count})"
            : name);
    }

    /// <summary>The outline of whatever the cursor has picked out, drawn over the plan.</summary>
    private void DrawHover(DrawingContext dc)
    {
        if (Document is null || _isPanning || Hovered is not { } element || IsSelected(element)) return;

        IReadOnlyList<Point2D>? ring = null;
        switch (element)
        {
            case Wall wall when Document.GetWallType(wall) is { } type:
                ring = WallJoins.GetBandOutline(Document, wall, type, type.Width / 2, -type.Width / 2);
                break;

            case Opening opening when Document.Walls.FirstOrDefault(w => w.Id == opening.HostWallId) is { } host &&
                                      Document.GetWallType(host) is { } hostType &&
                                      Document.FindType<OpeningType>(opening.TypeId) is { } openingType:
            {
                var (from, to) = opening.GetSpan(openingType);
                var half = hostType.Width / 2;
                ring = new[]
                {
                    host.PointAt(hostType.Structure, from, half), host.PointAt(hostType.Structure, to, half),
                    host.PointAt(hostType.Structure, to, -half), host.PointAt(hostType.Structure, from, -half)
                };
                break;
            }

            case WallOpening cut:
                ring = OpeningOutline(cut);
                break;

            case Slab slab:
                ring = slab.Boundary;
                break;

            case Room room:
                ring = room.GetBoundary(Document).Polygon;
                break;

            case Grid grid:
                DrawModelPolyline(dc, _hoverPen, new[] { grid.Start, grid.End });
                return;

            case SectionMarker section:
                DrawModelPolyline(dc, _hoverPen, new[] { section.Start, section.End });
                return;

            case Dimension dimension:
            {
                var (from, to) = dimension.GetDimensionLine(Document);
                DrawModelPolyline(dc, _hoverPen, new[] { from, to });
                return;
            }
        }

        if (ring is { Count: > 1 })
        {
            DrawModelPolyline(dc, _hoverPen, ring.Append(ring[0]).ToList());
            return;
        }

        // Anything else - a tag, a note, a sweep - is ringed where the cursor found it.
        dc.DrawEllipse(null, _hoverPen, ModelToScreen(_hoverAnchor), 9, 9);
    }

    /// <summary>
    /// Everything under a point, in the order a click picks them: annotation on top, then what
    /// is in or on walls, the walls, rooms, grids, and slabs under it all. The first is what a
    /// click takes; TAB steps through the rest, as in Revit, to reach what is underneath.
    /// </summary>
    private IEnumerable<Element> HitCandidates(Point2D model)
    {
        if (Document is null) yield break;
        RefreshViewFilter();

        // Annotation first: it is drawn on top of everything, so it must be picked from on
        // top of everything too. Its targets are sized in screen pixels, because that is how
        // big they look to the person clicking.
        var labelPick = 14 / PixelsPerMm;

        foreach (var tag in OnActiveLevel<Tag>())
            if (tag.Position.DistanceTo(model) <= labelPick) yield return tag;

        foreach (var note in OnActiveLevel<TextNote>())
            if (note.Position.DistanceTo(model) <= labelPick) yield return note;

        foreach (var dimension in OnActiveLevel<Dimension>())
        {
            var (from, to) = dimension.GetDimensionLine(Document);
            if (DistanceToSegment(model, from, to) <= 6 / PixelsPerMm) yield return dimension;
        }

        // A section marker crosses whatever it cuts, so it has to be picked from above the
        // walls - otherwise it could never be selected where it matters.
        foreach (var section in OnActiveLevel<SectionMarker>())
            if (DistanceToSegment(model, section.Start, section.End) <= 6 / PixelsPerMm) yield return section;

        foreach (var opening in OnActiveLevel<Opening>())
        {
            var wall = Document.Walls.FirstOrDefault(w => w.Id == opening.HostWallId);
            var wallType = wall is null ? null : Document.GetWallType(wall);
            var type = Document.FindType<OpeningType>(opening.TypeId);
            if (wall is null || wallType is null || type is null) continue;

            var centre = opening.GetCentre(wall);
            if (DistanceToSegment(model, centre, centre) <= Math.Max(type.Width, wallType.Width) / 2)
                yield return opening;
        }

        // Openings are holes in walls, so they are picked before the walls they are in.
        foreach (var cut in WallOpeningsAt(model)) yield return cut;

        // Sweeps sit on wall faces, so they are picked before the walls they are on.
        foreach (var sweep in SweepsAt(model)) yield return sweep;

        // Walls before rooms: a room covers the whole floor, so it would swallow every click.
        foreach (var wall in WallsAt(model)) yield return wall;

        foreach (var room in OnActiveLevel<Room>().Where(room => ContainsPoint(room.GetBoundary(Document), model)))
            yield return room;

        // Grids are thin, so they are picked by proximity rather than by containing a point.
        var gridTolerance = 5 / PixelsPerMm;
        foreach (var grid in OnActiveLevel<Grid>().Where(grid => DistanceToSegment(model, grid.Start, grid.End) <= gridTolerance))
            yield return grid;

        // Slabs last: they are under everything, so anything above them wins the click.
        foreach (var slab in OnActiveLevel<Slab>().Where(slab => slab.Contains(model)).Reverse())
            yield return slab;
    }

    /// <summary>Picks the wall whose body the point falls in, nearest first.</summary>
    private Wall? HitTestWall(Point2D model) => WallsAt(model).FirstOrDefault();

    /// <summary>Every wall whose body the point falls in, nearest first.</summary>
    private IEnumerable<Wall> WallsAt(Point2D model)
    {
        if (Document is null) return Array.Empty<Wall>();
        RefreshViewFilter();

        var found = new List<(Wall Wall, double Distance)>();

        foreach (var wall in OnActiveLevel<Wall>())
        {
            var type = Document.GetWallType(wall);
            if (type is null) continue;

            // Distance from the body centreline, which follows the arc of a curved wall.
            var (along, across) = wall.Locate(type.Structure, model);
            var distance = along >= 0 && along <= wall.Length
                ? Math.Abs(across)
                : Math.Min(model.DistanceTo(wall.PointAt(type.Structure, 0, 0)),
                           model.DistanceTo(wall.PointAt(type.Structure, wall.Length, 0)));
            var tolerance = type.Width / 2 + 4 / PixelsPerMm;

            if (distance <= tolerance) found.Add((wall, distance));
        }

        return found.OrderBy(f => f.Distance).Select(f => f.Wall).ToList();
    }

    private static double DistanceToSegment(Point2D p, Point2D a, Point2D b)
    {
        var ab = b - a;
        var lengthSquared = ab.Dot(ab);
        if (lengthSquared <= 0) return p.DistanceTo(a);

        var t = Math.Clamp((p - a).Dot(ab) / lengthSquared, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    // ---- rendering -------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        DrawGrid(dc);

        if (Document is null) return;

        // The storey below first, so the drawing sits on top of what it is set out against.
        DrawUnderlay(dc);

        // The drawing itself comes from the shared renderer, so the plan on screen and the
        // plan inside a viewport on a sheet are the same drawing produced by the same code.
        PrepareRenderer();
        _renderer.DrawContent(dc);

        // Editing affordances go on top, and only here. Grips, snap rings and half-finished
        // walls belong to the editor rather than to the drawing, so none of them may ever
        // reach a sheet - which is exactly why the renderer does not know about them.
        DrawTrimSubject(dc);
        DrawHover(dc);
        DrawJunctions(dc);
        DrawSelectedOpenings(dc);
        DrawGrips(dc);
        DrawPendingWall(dc);
        DrawPendingDimension(dc);
        DrawSnapMarker(dc);
        DrawBand(dc);
    }

    /// <summary>The selection box being dragged out.</summary>
    private void DrawBand(DrawingContext dc)
    {
        if (_bandStart is not { } start) return;

        var a = ModelToScreen(start);
        var b = ModelToScreen(_bandEnd);

        dc.DrawRectangle(_bandFill, _previewPen, new Rect(a, b));
    }

    /// <summary>Points the shared renderer at this canvas and its current view state.</summary>
    private void PrepareRenderer()
    {
        RefreshViewFilter();
        _renderer.Filter = _shows;
        _renderer.Document = Document;
        _renderer.DetailLevel = DetailLevel;
        _renderer.View = CurrentView;
        _renderer.SetSelection(_selection);
        _renderer.ActiveLevelId = ActiveLevelId;
        _renderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // The editor's surface is treated as a page at 1:1, which is what makes a grid bubble
        // on screen the size it will print.
        _renderer.PixelsPerPaperMm = PlanRenderer.ScreenPixelsPerPaperMm;
        _renderer.SetTransform(ModelToScreen, PixelsPerMm);
    }

    /// <summary>
    /// Draws the storey below, faintly, under the one being edited.
    ///
    /// This is what a plan is set out against: a wall on the first floor usually wants to
    /// land on one below it, and without the underlay that has to be done from memory or by
    /// switching back and forth. It is construction only, and never hit-testable - it is
    /// something to look at, not something to edit from up here.
    /// </summary>
    private void DrawUnderlay(DrawingContext dc)
    {
        if (!ShowUnderlay || Document is null) return;
        if (Levels.Below(Document, ActiveLevelId) is not { } below) return;

        _underlayRenderer.Document = Document;
        _underlayRenderer.DetailLevel = DetailLevel;
        _underlayRenderer.View = CurrentView;
        _underlayRenderer.SetSelection(null);
        _underlayRenderer.ActiveLevelId = below.Id;
        _underlayRenderer.Filter = Document.ViewSettings.FilterFor(Document, ViewReference.FloorPlan(below.Id));
        _underlayRenderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _underlayRenderer.PixelsPerPaperMm = PlanRenderer.ScreenPixelsPerPaperMm;
        _underlayRenderer.SetTransform(ModelToScreen, PixelsPerMm);

        _underlayRenderer.DrawContent(dc, underlay: true);
    }

    private void DrawGrid(DrawingContext dc)
    {
        var topLeft = ScreenToModel(new Point(0, 0));
        var bottomRight = ScreenToModel(new Point(ActualWidth, ActualHeight));

        // Step up through 100 mm -> 1 m -> 10 m so the grid never turns into mush.
        var step = GridStepMm;
        while (step * PixelsPerMm < MinGridPixelSpacing) step *= 10;
        const int majorEvery = 10;

        for (var x = Math.Floor(topLeft.X / step) * step; x <= bottomRight.X; x += step)
        {
            var isMajor = Math.Abs(Math.Round(x / step) % majorEvery) < 0.5;
            var pen = Math.Abs(x) < step / 2 ? _axisPen : isMajor ? _majorGridPen : _minorGridPen;
            var sx = Math.Round(ModelToScreen(new Point2D(x, 0)).X) + 0.5;
            dc.DrawLine(pen, new Point(sx, 0), new Point(sx, ActualHeight));
        }

        for (var y = Math.Floor(bottomRight.Y / step) * step; y <= topLeft.Y; y += step)
        {
            var isMajor = Math.Abs(Math.Round(y / step) % majorEvery) < 0.5;
            var pen = Math.Abs(y) < step / 2 ? _axisPen : isMajor ? _majorGridPen : _minorGridPen;
            var sy = Math.Round(ModelToScreen(new Point2D(0, y)).Y) + 0.5;
            dc.DrawLine(pen, new Point(0, sy), new Point(ActualWidth, sy));
        }
    }

    /// <summary>Square handles at each end and a ring at the middle for moving the wall.</summary>
    private void DrawGrips(DrawingContext dc)
    {
        // Corners between selected walls: round grips that pull the corner anywhere.
        if (!_isPanning)
            foreach (var corner in Corners())
                dc.DrawEllipse(_gripBrush, _gripPen, ModelToScreen(corner), 5, 5);

        if (SelectedWall is not { } wall || _isPanning) return;

        foreach (var point in new[] { wall.Start, wall.End })
        {
            var screen = ModelToScreen(point);
            dc.DrawRectangle(_gripBrush, _gripPen, new Rect(screen.X - 4.5, screen.Y - 4.5, 9, 9));
        }

        // The bend grip: a diamond halfway along, which curves the wall when dragged.
        var middle = ModelToScreen(BendGrip(wall));
        var diamond = new StreamGeometry();
        using (var ctx = diamond.Open())
        {
            ctx.BeginFigure(new Point(middle.X, middle.Y - 6), true, true);
            ctx.PolyLineTo(new[]
            {
                new Point(middle.X + 6, middle.Y), new Point(middle.X, middle.Y + 6), new Point(middle.X - 6, middle.Y)
            }, true, false);
        }

        diamond.Freeze();
        if (!wall.IsElliptical && !wall.IsSpline) dc.DrawGeometry(_gripBrush, _gripPen, diamond);

        // A spline wall's points: round grips that reshape the curve when dragged.
        if (IsWholeSpline(wall))
            foreach (var (_, point) in wall.LocationCurve.SplinePoints())
                dc.DrawEllipse(_gripBrush, _gripPen, ModelToScreen(point), 5, 5);

        // The padlocks at its corners with other walls.
        foreach (var (corner, at) in CornerLocks())
            DrawPadlock(dc, ModelToScreen(at), WallJointLock.IsLocked(Document!, wall.LevelId, corner));

        // The padlocks on faces shared with other walls.
        foreach (var (at, partner) in LockGrips())
            DrawPadlock(dc, ModelToScreen(at), WallLockPoint.AreLocked(wall, partner));

        if (Document?.GetWallType(wall) is { } type) DrawFlipArrows(dc, wall, type);
    }

    /// <summary>Highlights the wall the trim tool is waiting to trim.</summary>
    private void DrawTrimSubject(DrawingContext dc)
    {
        if (_trimSubject is null || Document is null) return;

        var type = Document.GetWallType(_trimSubject);
        if (type is null) return;

        var (start, end) = _trimSubject.GetBodyCentreline(type.Structure);
        dc.DrawLine(_targetPen, ModelToScreen(start), ModelToScreen(end));
    }

    /// <summary>
    /// The dimension being drawn, following the cursor with its measurement live.
    ///
    /// Seeing the number before committing is most of the value: a dimension is placed to
    /// find out what something measures as often as to report it.
    /// </summary>
    private void DrawPendingDimension(DrawingContext dc)
    {
        if (_pendingDimension is null || Document is null) return;

        var from = _pendingDimension.Resolve(Document);
        _renderer.DrawPendingDimension(dc, from, _cursorModel);

        // The end already fixed, so it is obvious which one is being dragged.
        if (from.DistanceTo(_cursorModel) >= 1)
            dc.DrawEllipse(Brushes.Transparent, _selectedPen, ModelToScreen(from), 4, 4);
    }

    private void DrawPendingWall(DrawingContext dc)
    {
        // Placing by segment or room shows the walls a click would place where the cursor is.
        if (ActiveTool == PlanTool.Wall && _drawShape is WallShape.BySegment or WallShape.ByRoom)
        {
            if (Document?.PlanWallType(ActiveWallTypeId, NewWallHeight) is { } previewType)
                foreach (var wall in _placementPreview) DrawWallBody(dc, wall, previewType, _previewPen);
            return;
        }

        // A freehand stroke is shown as it is being drawn; the wall comes when it is let go.
        if (_stroke is { Count: > 1 } stroke)
        {
            DrawModelPolyline(dc, _previewPen, stroke);
            return;
        }

        if (_pendingWallStart is null) return;

        var type = Document?.PlanWallType(ActiveWallTypeId, NewWallHeight);

        if (_drawShape == WallShape.Spline)
        {
            DrawPendingSpline(dc, type);
            return;
        }

        // A closed shape is shown whole, every wall of it, as it will be built.
        if (DrawsClosedShape)
        {
            dc.DrawEllipse(Brushes.Transparent, _selectedPen, ModelToScreen(_pendingWallStart.Value), 4, 4);

            var pieces = ShapeFrom(_pendingWallStart.Value, _cursorModel);
            foreach (var piece in pieces)
            {
                DrawModelPolyline(dc, _previewPen, piece.Curve.Points());
                if (type is null) continue;

                var shapeWall = new Wall
                {
                    Start = piece.Start, End = piece.End, Bulge = piece.Bulge, Ellipse = piece.Ellipse,
                    LocationLine = ActiveLocationLine, Flipped = DrawFlipped
                };
                DrawWallBody(dc, shapeWall, type, _previewPen);
            }

            if (type is not null && pieces.Count > 0)
            {
                var first = pieces[0];
                DrawFlipArrows(dc, new Wall
                {
                    Start = first.Start, End = first.End, Bulge = first.Bulge, Ellipse = first.Ellipse,
                    LocationLine = ActiveLocationLine, Flipped = DrawFlipped
                }, type);
            }

            return;
        }

        // An arc or half ellipse waiting for its third point bends through the cursor as it moves.
        if (_pendingArcEnd is { } arcEnd)
        {
            var curve = ThreePointCurve(_pendingWallStart.Value, arcEnd, _cursorModel) ?? WallCurve.Of(_pendingWallStart.Value, arcEnd, 0);

            dc.DrawEllipse(Brushes.Transparent, _selectedPen, ModelToScreen(_pendingWallStart.Value), 4, 4);
            dc.DrawEllipse(Brushes.Transparent, _selectedPen, ModelToScreen(arcEnd), 4, 4);
            DrawModelPolyline(dc, _previewPen, curve.Points());

            if (type is null) return;

            var arc = ThreePointWall(curve);

            DrawWallBody(dc, arc, type, _previewPen);
            DrawFlipArrows(dc, arc, type);
            return;
        }

        var start = ModelToScreen(_pendingWallStart.Value);
        var end = ModelToScreen(_cursorModel);
        dc.DrawLine(_previewPen, start, end);
        dc.DrawEllipse(Brushes.Transparent, _selectedPen, start, 4, 4);
        dc.DrawEllipse(Brushes.Transparent, _selectedPen, end, 4, 4);

        // The wall itself, where it will actually be built: off the clicks by the offset, off
        // the location line by the assembly, and with its exterior on the side it will have.
        // An arc's second click shows only the chord: its shape comes with the third.
        if (type is null || DrawArcs) return;
        if (_pendingWallStart.Value.DistanceTo(_cursorModel) < SnapStepMm) return;

        var (lineStart, lineEnd) = WallDrawing.OffsetSegment(
            _pendingWallStart.Value, _cursorModel, DrawOffset, DrawFlipped);

        var probe = new Wall
        {
            Start = lineStart,
            End = lineEnd,
            LocationLine = ActiveLocationLine,
            Flipped = DrawFlipped
        };

        DrawWallBody(dc, probe, type, _previewPen);
        DrawFlipArrows(dc, probe, type);
    }

    private void DrawModelPolyline(DrawingContext dc, Pen pen, IReadOnlyList<Point2D> points)
    {
        if (points.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ModelToScreen(points[0]), false, false);
            ctx.PolyLineTo(points.Skip(1).Select(ModelToScreen).ToArray(), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>The outline of a wall's body, square-ended, following the arc of a curved one.</summary>
    private void DrawWallBody(DrawingContext dc, Wall wall, WallType type, Pen pen)
    {
        var structure = type.Structure;
        var half = type.Width / 2;
        var curve = wall.LocationCurve;

        var stations = new List<double> { 0 };
        stations.AddRange(curve.Between(0, curve.Length));
        stations.Add(curve.Length);

        var corners = stations.Select(along => wall.PointAt(structure, along, half))
            .Concat(stations.AsEnumerable().Reverse().Select(along => wall.PointAt(structure, along, -half)))
            .Select(ModelToScreen)
            .ToArray();

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(corners[0], false, true);
            context.PolyLineTo(corners.Skip(1).ToArray(), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    /// Where the flip control sits: just outside the exterior face, halfway along. It is on
    /// the exterior side by definition, so it doubles as the marker of which side that is.
    /// </summary>
    private Point FlipControlPosition(Wall wall, WallType type)
    {
        var structure = type.Structure;
        var halfway = wall.Length / 2;

        var middle = ModelToScreen(wall.PointAt(structure, halfway, 0));
        var face = ModelToScreen(wall.PointAt(structure, halfway, type.Width / 2));

        var outward = face - middle;
        if (outward.Length < 1e-6)
        {
            var normal = ModelToScreen(wall.PointAt(structure, halfway, 1)) - middle;
            outward = normal.Length < 1e-6 ? new Vector(0, -1) : normal / normal.Length;
        }
        else
        {
            outward /= outward.Length;
        }

        return face + outward * FlipControlGap;
    }

    private const double FlipControlGap = 16;
    private const double FlipControlRadius = 10;

    /// <summary>Two arrowheads pointing across the wall: the control that swaps its sides.</summary>
    private void DrawFlipArrows(DrawingContext dc, Wall wall, WallType type)
    {
        var centre = FlipControlPosition(wall, type);

        // Along the wall where the arrows are, which on a curved wall is its tangent there.
        var halfway = wall.Length / 2;
        var along = ModelToScreen(wall.PointAt(type.Structure, halfway + 1, 0)) -
                    ModelToScreen(wall.PointAt(type.Structure, halfway - 1, 0));
        along = along.Length < 1e-6 ? new Vector(1, 0) : along / along.Length;
        var across = new Vector(-along.Y, along.X);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var sign in new[] { 1.0, -1.0 })
            {
                var tip = centre + across * (7 * sign);
                var baseMid = centre + across * (1.5 * sign);

                context.BeginFigure(tip, true, true);
                context.LineTo(baseMid + along * 4.5, true, false);
                context.LineTo(baseMid - along * 4.5, true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(_selectedPen.Brush, null, geometry);
    }

    /// <summary>Whether a click lands on the selected wall's flip control.</summary>
    private bool IsOnFlipControl(Wall wall, Point screen) =>
        Document?.GetWallType(wall) is { } type &&
        (FlipControlPosition(wall, type) - screen).Length <= FlipControlRadius;

    /// <summary>A ring showing that the cursor has locked onto an existing wall end.</summary>
    private void DrawSnapMarker(DrawingContext dc)
    {
        if (!_cursorIsSnapped) return;
        dc.DrawEllipse(Brushes.Transparent, _snapPen, ModelToScreen(_cursorModel), 7, 7);
    }


    // ---- helpers ---------------------------------------------------------------

    private void RaiseViewChanged()
    {
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private static double Clamp(double pixelsPerMm) =>
        Math.Clamp(pixelsPerMm, MinPixelsPerMm, MaxPixelsPerMm);

    private static Pen MakePen(Color color, double thickness) =>
        Freeze(new Pen(Freeze(new SolidColorBrush(color)), thickness));

    private static Pen MakeDashedPen(Color color, double thickness, double dash, double gap) =>
        Freeze(new Pen(Freeze(new SolidColorBrush(color)), thickness)
        {
            DashStyle = new DashStyle(new[] { dash, gap }, 0)
        });

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
