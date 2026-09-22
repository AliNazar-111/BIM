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
    Reveal
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

            SelectionChanged?.Invoke(this, EventArgs.Empty);
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
            InvalidateVisual();
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

    /// <summary>Gives a wall about to be placed the height or depth set on the option bar.</summary>
    private Wall Configured(Wall wall)
    {
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

    private PlacedSweep? _sweepEditTarget;

    /// <summary>Raised when an Add/Remove Walls or Modify Returns mode starts or stops.</summary>
    public event EventHandler? SweepEditChanged;

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
            : "Click near an end of the sweep to turn its return on or off. Esc when done.");
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
                nearStart ? !sweep.ReturnAtStart : sweep.ReturnAtStart,
                nearStart ? sweep.ReturnAtEnd : !sweep.ReturnAtEnd,
                "Modify Returns"));
            var now = nearStart ? sweep.ReturnAtStart : sweep.ReturnAtEnd;
            HintChanged?.Invoke(this, $"That end now {(now ? "returns round the wall end" : "is cut straight")}. A return shows where the wall end is exposed.");
            return true;
        }

        return false;
    }

    /// <summary>The placed sweep or reveal under the cursor, drawn beside or in a wall face.</summary>
    private PlacedSweep? HitTestSweep(Point2D model)
    {
        if (Document is null) return null;
        var reach = 5 / PixelsPerMm;

        foreach (var placed in OnActiveLevel<PlacedSweep>())
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
                return placed;
        }

        return null;
    }

    public void SetTool(PlanTool tool)
    {
        ActiveTool = tool;
        CancelPendingOperation();
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
        _ => "Click to select, Ctrl+click to add, or drag a box. Drag a selection to move it."
    };

    /// <summary>Replaces the selection with one element, or clears it.</summary>
    public void Select(Element? element) =>
        SelectMany(element is null ? Array.Empty<Element>() : new[] { element });

    public void SelectMany(IEnumerable<Element> elements)
    {
        var replacement = elements.Distinct().ToList();
        if (replacement.SequenceEqual(_selection)) return;

        _selection.Clear();
        _selection.AddRange(replacement);

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Adds an element to the selection, or takes it out if it is already in.</summary>
    public void ToggleSelected(Element element)
    {
        if (!_selection.Remove(element)) _selection.Add(element);

        SelectionChanged?.Invoke(this, EventArgs.Empty);
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
                      || _trimSubject is not null
                      || _pendingDimension is not null
                      || _bandStart is not null
                      || SweepEdit != SweepEditMode.None;

        EndSweepEdit();
        _pendingWallStart = null;
        _trimSubject = null;
        _pendingDimension = null;
        _bandStart = null;
        _pendingArcEnd = null;

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
        }

        var hit = HitTest(raw);

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
            if (grip is GripKind.Start or GripKind.End or GripKind.Bend)
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
        EndBand();
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _bandStart = null;
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
        // An elliptical wall keeps its shape: bending it would make it an arc.
        if (!wall.IsElliptical && BendGrip(wall).DistanceTo(model) <= radius) return GripKind.Bend;

        return GripKind.None;
    }

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
    private void BeginMoveDrag(Point2D raw)
    {
        if (!_selection.Any(ElementTransforms.CanMove)) return;

        _dragging = GripKind.Move;
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

            foreach (var element in _selection)
                if (ElementTransforms.CanMove(element))
                    ElementTransforms.Move(element, step);

            _dragLastPoint = target;
            _dragTotal += step;

            _cursorIsSnapped = false;
            InvalidateVisual();
            return;
        }

        if (SelectedWall is not { } wall) return;

        switch (_dragging)
        {
            case GripKind.Start:
                wall.Start = SnapPoint(raw, wall, out _cursorIsSnapped);
                break;

            case GripKind.End:
                wall.End = SnapPoint(raw, wall, out _cursorIsSnapped);
                break;

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

            var command = new MoveElementsCommand(_selection.ToList(), _dragTotal);
            if (!command.IsEmpty) History?.Record(command);

            _dragTotal = default;
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        if (SelectedWall is not { } wall) return;

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
        if (wall.Length <= WallJoins.JoinTolerance)
        {
            wall.Start = _dragOriginalStart;
            wall.End = _dragOriginalEnd;
            HintChanged?.Invoke(this, "A wall cannot have zero length.");
            InvalidateVisual();
            return;
        }

        History?.Record(new MoveWallCommand(
            wall, _dragOriginalStart, _dragOriginalEnd, wall.Start, wall.End, "Move Wall End"));

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
            HintChanged?.Invoke(this, FirstClickHint());
        }
        else if (DrawsClosedShape)
        {
            PlaceShape(model);
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
    private Element? HitTest(Point2D model)
    {
        if (Document is null) return null;
        RefreshViewFilter();

        // Annotation first: it is drawn on top of everything, so it must be picked from on
        // top of everything too. Its targets are sized in screen pixels, because that is how
        // big they look to the person clicking.
        var labelPick = 14 / PixelsPerMm;

        foreach (var tag in OnActiveLevel<Tag>())
            if (tag.Position.DistanceTo(model) <= labelPick) return tag;

        foreach (var note in OnActiveLevel<TextNote>())
            if (note.Position.DistanceTo(model) <= labelPick) return note;

        foreach (var dimension in OnActiveLevel<Dimension>())
        {
            var (from, to) = dimension.GetDimensionLine(Document);
            if (DistanceToSegment(model, from, to) <= 6 / PixelsPerMm) return dimension;
        }

        // A section marker crosses whatever it cuts, so it has to be picked from above the
        // walls - otherwise it could never be selected where it matters.
        foreach (var section in OnActiveLevel<SectionMarker>())
            if (DistanceToSegment(model, section.Start, section.End) <= 6 / PixelsPerMm) return section;

        foreach (var opening in OnActiveLevel<Opening>())
        {
            var wall = Document.Walls.FirstOrDefault(w => w.Id == opening.HostWallId);
            var wallType = wall is null ? null : Document.GetWallType(wall);
            var type = Document.FindType<OpeningType>(opening.TypeId);
            if (wall is null || wallType is null || type is null) continue;

            var centre = opening.GetCentre(wall);
            if (DistanceToSegment(model, centre, centre) <= Math.Max(type.Width, wallType.Width) / 2)
                return opening;
        }

        // Sweeps sit on wall faces, so they are picked before the walls they are on.
        if (HitTestSweep(model) is { } hitSweep) return hitSweep;

        // Walls before rooms: a room covers the whole floor, so it would swallow every click.
        if (HitTestWall(model) is { } hitWall) return hitWall;

        if (OnActiveLevel<Room>()
                .FirstOrDefault(room => ContainsPoint(room.GetBoundary(Document), model)) is { } hitRoom)
        {
            return hitRoom;
        }

        // Grids are thin, so they are picked by proximity rather than by containing a point.
        var gridTolerance = 5 / PixelsPerMm;
        if (OnActiveLevel<Grid>().FirstOrDefault(grid =>
                DistanceToSegment(model, grid.Start, grid.End) <= gridTolerance) is { } hitGrid)
        {
            return hitGrid;
        }

        // Slabs last: they are under everything, so anything above them wins the click.
        return OnActiveLevel<Slab>().LastOrDefault(slab => slab.Contains(model));
    }

    /// <summary>Picks the wall whose body the point falls in, nearest first.</summary>
    private Wall? HitTestWall(Point2D model)
    {
        if (Document is null) return null;
        RefreshViewFilter();

        Wall? best = null;
        var bestDistance = double.MaxValue;

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

            if (distance <= tolerance && distance < bestDistance)
            {
                best = wall;
                bestDistance = distance;
            }
        }

        return best;
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
        if (!wall.IsElliptical) dc.DrawGeometry(_gripBrush, _gripPen, diamond);

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
        if (_pendingWallStart is null) return;

        var type = Document?.PlanWallType(ActiveWallTypeId, NewWallHeight);

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
