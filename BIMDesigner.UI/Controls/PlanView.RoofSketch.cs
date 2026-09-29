using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.Controls;

/// <summary>What a click does while a roof is being sketched.</summary>
public enum RoofSketchTool
{
    /// <summary>Select sketch lines, to delete them or change what they do.</summary>
    Modify,

    /// <summary>Click a wall: a line along its face, out by the overhang, that follows it.</summary>
    PickWalls,

    Line,
    Rectangle,
    Polygon,

    /// <summary>Start, end, then a point it passes through: an arc - a cone where it slopes.</summary>
    Arc,

    /// <summary>The centre, then a point on it: a circle, as two half circles.</summary>
    Circle,

    /// <summary>Click an edge already drawn - a floor's, a wall face, a grid line - to take it as a line.</summary>
    PickLines,

    /// <summary>Tail on a line of the outline, head where the slope rises toward.</summary>
    SlopeArrow,

    /// <summary>Click a line to split it in two there - how an eave is divided for a dormer.</summary>
    Split,

    /// <summary>Click a line to move it - or a copy of it - sideways by the offset.</summary>
    Offset,

    /// <summary>Click two lines to cut back or run on each to where they meet.</summary>
    TrimExtend,

    /// <summary>Click a reference eave, then others to bring to its height - by their plate, or their overhang.</summary>
    AlignEaves
}

/// <summary>
/// Roof sketch mode - Revit's "Modify | Create Roof Footprint" (specification section 3.3).
///
/// Starting the Roof tool does not put a roof anywhere. It opens a sketch: lines picked off
/// walls or drawn, living outside the model until Finish checks that they close into an outline
/// and turns them into a roof. Cancel throws them away. Edit Footprint opens a finished roof's
/// sketch again. Nothing about a roof's outline is guessed - it is always what was drawn.
/// </summary>
public partial class PlanView
{
    private List<RoofSketchLine>? _sketch;
    private List<RoofSlopeArrow> _sketchArrows = new();
    private Roof? _sketchRoof;
    private readonly List<(List<RoofSketchLine> Lines, List<RoofSlopeArrow> Arrows)> _sketchUndo = new();
    private readonly List<(List<RoofSketchLine> Lines, List<RoofSlopeArrow> Arrows)> _sketchRedo = new();
    private readonly List<RoofSketchLine> _sketchSelection = new();
    private readonly List<RoofSlopeArrow> _sketchArrowSelection = new();
    private IReadOnlyList<RoofSketchLine> _sketchCulprits = Array.Empty<RoofSketchLine>();

    /// <summary>The first click of a line, rectangle or polygon still being drawn.</summary>
    private Point2D? _sketchStart;

    /// <summary>The lines a click would add - a picked wall, or a chain of them with TAB.</summary>
    private List<RoofSketchLine> _sketchPreview = new();

    /// <summary>Whether TAB has widened a wall pick to the whole chain of walls it is joined to.</summary>
    private bool _sketchPickChain;

    private Point2D _sketchCursor;
    private RoofSketchTool _sketchTool = RoofSketchTool.PickWalls;

    private static readonly Pen SketchPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0x2E, 0xC4))), 2));
    private static readonly Pen SketchSelectedPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF))), 3));
    private static readonly Pen SketchProblemPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E))), 3.5));
    private static readonly Pen SketchPreviewPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0x2E, 0xC4))), 1.6)
    {
        DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0)
    });

    private static readonly Brush SketchSlopeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0x2E, 0xC4)));
    private static readonly Pen SketchMarkerPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD6, 0x2E, 0xC4))), 1.2));

    /// <summary>How far in from its line a slope marker sits, and how big a target it is, in pixels.</summary>
    private const double MarkerInset = 16;
    private const double MarkerRadius = 8;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Whether a roof sketch is open.</summary>
    public bool IsSketching => _sketch is not null;

    /// <summary>The roof whose footprint is being edited, or null for a new roof.</summary>
    public Roof? SketchedRoof => _sketchRoof;

    public IReadOnlyList<RoofSketchLine> SketchLines => _sketch ?? (IReadOnlyList<RoofSketchLine>)Array.Empty<RoofSketchLine>();

    public IReadOnlyList<RoofSketchLine> SelectedSketchLines => _sketchSelection;

    /// <summary>The slope arrows in the open sketch.</summary>
    public IReadOnlyList<RoofSlopeArrow> SketchArrows => _sketchArrows;

    public IReadOnlyList<RoofSlopeArrow> SelectedSketchArrows => _sketchArrowSelection;

    public bool CanUndoSketch => _sketchUndo.Count > 0;

    public bool CanRedoSketch => _sketchRedo.Count > 0;

    /// <summary>Raised when the sketch opens, closes, or its lines or selection change.</summary>
    public event EventHandler? SketchChanged;

    /// <summary>
    /// Raised when Finish has made a new roof from walls it was picked off - the moment Revit
    /// asks whether to attach them to it, which is what fills the gable ends.
    /// </summary>
    public event EventHandler<Roof>? RoofMadeFromWalls;

    /// <summary>
    /// Attaches the tops of the walls a roof was picked from to that roof, as one undoable
    /// step, so they rise to meet it. Returns how many were attached.
    /// </summary>
    public int AttachPickedWalls(Roof roof)
    {
        if (Document is null) return 0;

        var walls = WallsToAttach(roof);
        if (walls.Count == 0) return 0;

        Apply(new AttachWallsCommand(walls.Select(wall => (wall, true, (Guid?)roof.Id)), "Attach Walls to Roof"));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();

        HintChanged?.Invoke(this, $"{walls.Count} wall{(walls.Count == 1 ? "" : "s")} attached: their tops follow the roof, and the gable ends are closed.");
        return walls.Count;
    }

    /// <summary>
    /// The walls a new roof offers to attach: those it was picked from - or, for a roof by
    /// extrusion, which is not picked from anything, those standing under it - that start
    /// below it and are not attached to it already.
    /// </summary>
    private List<Wall> WallsToAttach(Roof roof)
    {
        if (Document is null) return new List<Wall>();

        var bearing = roof.GetBottomElevation(Document);
        var candidates = roof.Extrusion is { } extrusion
            ? WallsUnder(extrusion.Footprint(), Document.Walls.Where(wall => wall.LevelId == roof.LevelId))
            : roof.Edges
                .Select(edge => edge.WallId)
                .OfType<Guid>()
                .Distinct()
                .Select(id => Document.Walls.FirstOrDefault(wall => wall.Id == id))
                .OfType<Wall>();

        return candidates
            .Where(wall => wall.TopAttachedTo != roof.Id && wall.GetBaseElevation(Document) < bearing - 1)
            .ToList();
    }

    /// <summary>Whether new lines slope - Revit's "Defines slope" on the options bar.</summary>
    public bool SketchDefinesSlope { get; set; } = true;

    /// <summary>
    /// How far a picked line stands out from its wall: 450 mm to start with, as most eaves do,
    /// so a roof picked off its walls has an overhang for a soffit and a fascia clear of the wall.
    /// </summary>
    public double SketchOverhang { get; set; } = 450;

    /// <summary>Measure the overhang from the wall's core rather than its finish face.</summary>
    public bool SketchExtendToCore { get; set; }

    /// <summary>How new slope arrows give their slope - Revit's Specify: a pitch, or heights at both ends.</summary>
    public bool SketchArrowByHeights { get; set; }

    /// <summary>The height new slope arrows start at, above the roof's base - Height Offset at Tail.</summary>
    public double SketchArrowTailOffset { get; set; }

    /// <summary>The height new slope arrows reach at their head, when given by heights.</summary>
    public double SketchArrowHeadOffset { get; set; } = 300;

    public RoofSketchTool SketchTool
    {
        get => _sketchTool;
        set
        {
            _sketchTool = value;
            _sketchStart = null;
            _sketchArcEnd = null;
            _trimFirst = null;
            _alignReference = null;
            _sketchPreview.Clear();
            _sketchPickChain = false;
            if (value != RoofSketchTool.Modify)
            {
                _sketchSelection.Clear();
                _sketchArrowSelection.Clear();
            }

            HintChanged?.Invoke(this, SketchHint());
            SketchChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    // ---- opening and closing -----------------------------------------------------

    /// <summary>
    /// Opens a roof sketch: empty for a new roof, or with a roof's own outline for Edit
    /// Footprint. The roof being edited is hidden while its sketch is open, so what is on
    /// screen is the sketch rather than the roof it is about to replace.
    /// </summary>
    public void BeginRoofSketch(Roof? existing = null)
    {
        if (Document is null) return;

        _sketch = existing is null ? new List<RoofSketchLine>() : RoofSketch.LinesOf(existing);
        _sketchArrows = existing is null ? new List<RoofSlopeArrow>() : existing.SlopeArrows.Select(arrow => arrow.Copy()).ToList();
        _sketchRoof = existing;
        _sketchUndo.Clear();
        _sketchRedo.Clear();
        _sketchSelection.Clear();
        _sketchArrowSelection.Clear();
        _sketchCulprits = Array.Empty<RoofSketchLine>();
        _sketchStart = null;
        _sketchArcEnd = null;
        _trimFirst = null;
        _alignReference = null;
        _sketchPreview.Clear();
        _sketchPickChain = false;
        _sketchTool = existing is null ? RoofSketchTool.PickWalls : RoofSketchTool.Modify;

        if (ActiveTool != PlanTool.Roof)
        {
            ActiveTool = PlanTool.Roof;
            Cursor = Cursors.Cross;
        }

        Select(null);

        HintChanged?.Invoke(this, SketchHint());
        SketchChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Opens the selected roof's sketch - Revit's Edit Footprint. Returns false when the
    /// selection is not a single roof.
    /// </summary>
    public bool EditFootprint()
    {
        if (_selection is not [Roof roof] || roof.IsExtrusion) return false;

        BeginRoofSketch(roof);
        return true;
    }

    /// <summary>
    /// Checks the sketch and, if it closes, makes it the roof - Revit's Finish Edit Mode.
    ///
    /// A sketch that does not close is not turned into anything. The lines at fault are drawn
    /// in red and the status bar says what is wrong, and the sketch stays open to be put
    /// right: a sketch that refused without saying why would be the worst thing a roof tool
    /// could do.
    /// </summary>
    public bool FinishSketch()
    {
        if (Document is null || _sketch is null) return false;

        var check = RoofSketch.Check(_sketch, _sketchArrows);
        if (!check.IsValid)
        {
            _sketchCulprits = check.Culprits;
            HintChanged?.Invoke(this, check.Problem!);
            InvalidateVisual();
            return false;
        }

        Roof roof;

        if (_sketchRoof is { } existing)
        {
            roof = existing;
            Apply(new SetRoofSketchCommand(roof, check.Boundary, check.Edges, check.Arrows, openings: check.Openings));
        }
        else
        {
            var type = Document.FindType<SlabType>(ActiveRoofTypeId) as RoofType
                       ?? Document.TypesOf<RoofType>().FirstOrDefault();

            if (type is null)
            {
                HintChanged?.Invoke(this, "This project has no roof types to build the roof from.");
                return false;
            }

            roof = new Roof
            {
                TypeId = type.Id,
                LevelId = ActiveLevelId,
                HeightOffset = BaseOffsetFor(_sketch)
            };

            roof.SetBoundary(check.Boundary);
            roof.SetEdges(check.Edges);
            roof.SetSlopeArrows(check.Arrows);
            roof.SetOpenings(check.Openings);
            Apply(new AddElementCommand(Document, roof, "Create Roof"));
        }

        var made = _sketchRoof is null;

        EndSketch();
        SetTool(PlanTool.Select);
        Select(roof);

        HintChanged?.Invoke(this, roof.Form == RoofForm.Flat
            ? $"Flat roof, {Units.FormatArea(roof.Area)}, based {Units.FormatLength(roof.HeightOffset)} above its level."
            : $"{EnumText.Humanise(roof.Form)} roof: {Units.FormatArea(roof.SlopingArea(Document))} of covering over {Units.FormatArea(roof.Area)}. " +
              "Double-click it, or Edit Footprint, to change its outline.");

        ModelChanged?.Invoke(this, EventArgs.Empty);

        // Asked last, so its answer is what the status bar is left saying.
        if (made && roof.Edges.Any(edge => edge.WallId is not null)) RoofMadeFromWalls?.Invoke(this, roof);

        return true;
    }

    /// <summary>Throws the sketch away - Revit's Cancel Edit Mode. The model is untouched.</summary>
    public void CancelSketch()
    {
        if (_sketch is null) return;

        var editing = _sketchRoof;
        EndSketch();
        SetTool(PlanTool.Select);
        if (editing is not null) Select(editing);

        HintChanged?.Invoke(this, editing is null ? "Roof sketch cancelled; nothing was made." : "Footprint left as it was.");
    }

    private void EndSketch()
    {
        _sketch = null;
        _sketchArrows = new List<RoofSlopeArrow>();
        _sketchRoof = null;
        _sketchUndo.Clear();
        _sketchRedo.Clear();
        _sketchSelection.Clear();
        _sketchArrowSelection.Clear();
        _sketchCulprits = Array.Empty<RoofSketchLine>();
        _sketchStart = null;
        _sketchArcEnd = null;
        _trimFirst = null;
        _alignReference = null;
        _sketchPreview.Clear();
        _sketchPickChain = false;

        SketchChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Where a new roof sits: on the tops of the walls it was picked from, since that is what
    /// it bears on. Drawn with no walls, it goes on the storey above, or three metres up where
    /// there is none.
    /// </summary>
    private double BaseOffsetFor(IEnumerable<RoofSketchLine> lines)
    {
        if (Document is null) return 3000;

        var level = Document.FindLevel(ActiveLevelId)?.Elevation ?? 0;

        var tops = lines
            .Select(line => line.Edge.WallId)
            .OfType<Guid>()
            .Distinct()
            .Select(id => Document.Walls.FirstOrDefault(wall => wall.Id == id))
            .OfType<Wall>()
            .Select(wall => wall.GetTopElevation(Document))
            .ToList();

        if (tops.Count > 0) return tops.Max() - level;

        var above = Document.Levels.Where(other => other.Elevation > level + 1).OrderBy(other => other.Elevation).FirstOrDefault();
        return above is not null ? above.Elevation - level : 3000;
    }

    // ---- sketch undo --------------------------------------------------------------

    /// <summary>
    /// Keeps the sketch as it was before a change. The sketch has an undo of its own: Ctrl+Z
    /// while sketching takes back the last line, not the last thing done to the model - which
    /// is what someone halfway through a sketch means by it.
    /// </summary>
    private void Remember()
    {
        if (_sketch is null) return;

        _sketchUndo.Add(Snapshot());
        _sketchRedo.Clear();
        _sketchCulprits = Array.Empty<RoofSketchLine>();
    }

    private (List<RoofSketchLine> Lines, List<RoofSlopeArrow> Arrows) Snapshot() =>
        (_sketch!.Select(line => line.Copy()).ToList(), _sketchArrows.Select(arrow => arrow.Copy()).ToList());

    public bool UndoSketch()
    {
        if (_sketch is null || _sketchUndo.Count == 0) return false;

        _sketchRedo.Add((_sketch, _sketchArrows));
        (_sketch, _sketchArrows) = _sketchUndo[^1];
        _sketchUndo.RemoveAt(_sketchUndo.Count - 1);
        AfterSketchEdit("Undone.");
        return true;
    }

    public bool RedoSketch()
    {
        if (_sketch is null || _sketchRedo.Count == 0) return false;

        _sketchUndo.Add((_sketch, _sketchArrows));
        (_sketch, _sketchArrows) = _sketchRedo[^1];
        _sketchRedo.RemoveAt(_sketchRedo.Count - 1);
        AfterSketchEdit("Redone.");
        return true;
    }

    private void AfterSketchEdit(string? hint = null)
    {
        _sketchSelection.RemoveAll(line => _sketch is null || !_sketch.Contains(line));
        _sketchArrowSelection.RemoveAll(arrow => !_sketchArrows.Contains(arrow));
        _sketchCulprits = Array.Empty<RoofSketchLine>();

        if (hint is not null) HintChanged?.Invoke(this, hint);
        SketchChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ---- editing lines ------------------------------------------------------------

    public bool DeleteSelectedSketchLines()
    {
        if (_sketch is null || _sketchSelection.Count + _sketchArrowSelection.Count == 0) return false;

        Remember();
        var count = _sketchSelection.Count + _sketchArrowSelection.Count;
        _sketch.RemoveAll(line => _sketchSelection.Contains(line));
        _sketchArrows.RemoveAll(arrow => _sketchArrowSelection.Contains(arrow));
        _sketchSelection.Clear();
        _sketchArrowSelection.Clear();

        AfterSketchEdit(count == 1 ? "Deleted." : $"{count} deleted.");
        return true;
    }

    /// <summary>
    /// Changes the selected slope arrows - what the options bar does with arrows selected.
    /// </summary>
    public bool ChangeSelectedSketchArrows(Action<RoofSlopeArrow> change)
    {
        if (_sketch is null || _sketchArrowSelection.Count == 0) return false;

        Remember();
        foreach (var arrow in _sketchArrowSelection) change(arrow);

        AfterSketchEdit();
        return true;
    }

    /// <summary>
    /// Changes the selected lines' settings - what the options bar does when lines are
    /// selected, so the same controls set up new lines and change existing ones.
    /// </summary>
    public bool ChangeSelectedSketchLines(Action<RoofEdge> change)
    {
        if (_sketch is null || _sketchSelection.Count == 0 || Document is null) return false;

        Remember();

        var moved = new List<RoofSketchLine>();

        foreach (var line in _sketchSelection)
        {
            var (overhang, toCore) = (line.Edge.Overhang, line.Edge.ExtendToCore);
            change(line.Edge);

            // A picked line stands where its wall and overhang put it, so a new overhang or core
            // setting moves it - sideways only. Where it starts and stops along the wall stays as
            // it was: it may be one part of a split eave, and must not grow back to the wall's
            // whole length. Anything else about a line leaves it where it is.
            if (Math.Abs(line.Edge.Overhang - overhang) < 1e-9 && line.Edge.ExtendToCore == toCore) continue;

            if (line.Edge.WallId is { } id && Document.Walls.FirstOrDefault(wall => wall.Id == id) is { } wall &&
                RoofSketch.LineOnWall(Document, wall, line.Edge.OnLeftOfWall, line.Edge.Overhang, line.Edge.ExtendToCore) is var (a, b))
            {
                line.Start = OntoLine(line.Start, a, b);
                line.End = OntoLine(line.End, a, b);
                moved.Add(line);
            }
        }

        foreach (var line in moved)
            RoofSketch.CloseCorners(_sketch, line, CornerReach());

        AfterSketchEdit();
        return true;
    }

    /// <summary>The point on the line through two points nearest a point - square across onto it.</summary>
    private static Point2D OntoLine(Point2D point, Point2D a, Point2D b)
    {
        var along = b - a;
        var length = along.Length;
        if (length <= 0) return a;

        return a + along * ((point - a).Dot(along) / (length * length));
    }

    /// <summary>Turns a line's slope on or off, from its △ marker.</summary>
    private void ToggleSlope(RoofSketchLine line)
    {
        Remember();
        line.Edge.DefinesSlope = !line.Edge.DefinesSlope;
        if (line.Edge.DefinesSlope && line.Edge.SlopeDegrees <= 0) line.Edge.SlopeDegrees = ActiveRoofSlopeDegrees;

        AfterSketchEdit(line.Edge.DefinesSlope
            ? $"The roof slopes up from this line at {line.Edge.SlopeDegrees:0.##}°."
            : "The roof is cut off at this line - a gable end.");
    }

    // ---- clicks ---------------------------------------------------------------------

    /// <summary>What a left click does in the sketch. Public so the sketch can be driven from tests.</summary>
    public bool SketchClick(Point2D raw)
    {
        if (Document is null || _sketch is null) return false;

        _sketchCursor = raw;

        // A pitch clicked while selecting is a pitch to type. Only then: a drawing tool's click
        // near a line is meant for the line.
        if (_sketchTool == RoofSketchTool.Modify && PitchLabelAt(raw) is { } pitched)
        {
            BeginPitchEdit(pitched);
            return true;
        }

        // A slope marker wins over everything: it is the smallest target, and the one aimed at.
        if (MarkerAt(raw) is { } marked)
        {
            ToggleSlope(marked);
            return true;
        }

        switch (_sketchTool)
        {
            case RoofSketchTool.Modify:
                SelectSketchLineAt(raw);
                return true;

            case RoofSketchTool.PickWalls:
                return PickWallsAt(raw);

            case RoofSketchTool.Line:
                return DrawLineTo(SketchSnap(raw));

            case RoofSketchTool.Rectangle:
            case RoofSketchTool.Polygon:
            case RoofSketchTool.Circle:
                return DrawShapeTo(SketchSnap(raw));

            case RoofSketchTool.Arc:
                return DrawArcTo(SketchSnap(raw));

            case RoofSketchTool.PickLines:
                return PickLinesAt(raw);

            case RoofSketchTool.Offset:
                return OffsetLineAt(raw);

            case RoofSketchTool.TrimExtend:
                return SketchTrimAt(raw);

            case RoofSketchTool.AlignEaves:
                return AlignEaveAt(raw);

            case RoofSketchTool.SlopeArrow:
                return DrawArrowTo(raw);

            case RoofSketchTool.Split:
                return SplitSketchLineAt(raw);
        }

        return false;
    }

    // ---- slope arrows -------------------------------------------------------------------

    /// <summary>
    /// Draws a slope arrow: the first click is its tail, which goes onto the line of the
    /// outline nearest it; the second its head. Revit puts a tail only on a line, since the
    /// line is what says which part of the roof the arrow slopes.
    /// </summary>
    private bool DrawArrowTo(Point2D raw)
    {
        if (_sketch is null) return false;

        if (_sketchStart is not { } tail)
        {
            if (TailOnLine(raw) is not { } onLine)
            {
                HintChanged?.Invoke(this, "Start a slope arrow on a line of the outline - near the line, then click.");
                return false;
            }

            _sketchStart = onLine;
            HintChanged?.Invoke(this, "Click where the slope rises toward - the arrow's head.");
            InvalidateVisual();
            return true;
        }

        // A head clicked on a line goes onto it - the middle of a dormer's stretch of eave, most
        // often - rather than onto the drawing grid beside it.
        var head = TailOnLine(raw) is { } headOnLine && headOnLine.DistanceTo(raw) <= SnapPixelRadius / PixelsPerMm
            ? headOnLine
            : SketchSnap(raw);
        if (head.DistanceTo(tail) <= RoofSketch.JoinTolerance) return false;

        Remember();
        _sketchArrows.Add(new RoofSlopeArrow
        {
            Tail = tail,
            Head = head,
            ByHeights = SketchArrowByHeights,
            SlopeDegrees = ActiveRoofSlopeDegrees,
            TailOffset = SketchArrowTailOffset,
            HeadOffset = SketchArrowHeadOffset
        });

        _sketchStart = null;
        AfterSketchEdit(SketchArrowByHeights
            ? $"Slope arrow added, from {Units.FormatLength(SketchArrowTailOffset)} at its tail to {Units.FormatLength(SketchArrowHeadOffset)} at its head."
            : $"Slope arrow added, rising at {ActiveRoofSlopeDegrees:0.##}° from {Units.FormatLength(SketchArrowTailOffset)} above the base.");
        return true;
    }

    /// <summary>
    /// Where on the outline an arrow's tail goes: the nearest point of the nearest line, snapped
    /// to its ends and middle - the places a tail is usually meant to be.
    /// </summary>
    private Point2D? TailOnLine(Point2D raw)
    {
        if (_sketch is null) return null;

        var reach = SnapPixelRadius / PixelsPerMm;
        Point2D? best = null;
        var nearest = double.MaxValue;

        // Arrows start on straight lines: an arc slopes all round already, and has no one
        // direction to be square to.
        foreach (var line in _sketch.Where(line => !line.IsArc))
        {
            var along = line.End - line.Start;
            var length = along.Length;
            if (length <= 0) continue;

            var t = Math.Clamp((raw - line.Start).Dot(along) / (length * length), 0, 1);
            var foot = line.Start + along * t;
            var distance = foot.DistanceTo(raw);
            if (distance >= nearest || distance > 3 * reach) continue;

            nearest = distance;

            // The ends and the middle are where a tail is meant to be more often than not.
            best = new[] { line.Start, line.Start.MidpointTo(line.End), line.End }
                .Where(point => point.DistanceTo(raw) <= reach)
                .OrderBy(point => point.DistanceTo(raw))
                .Cast<Point2D?>()
                .FirstOrDefault() ?? foot;
        }

        return best;
    }

    // ---- splitting ------------------------------------------------------------------------

    /// <summary>
    /// Splits a sketch line in two where it is clicked, both halves doing what it did. It is how
    /// an eave is divided so that the middle stretch can be given to a dormer's arrows.
    /// </summary>
    public bool SplitSketchLineAt(Point2D raw)
    {
        if (_sketch is null || SketchLineAt(raw) is not { } line) return false;

        var curve = line.Curve;
        var length = curve.Length;
        var t = Math.Clamp(curve.Locate(raw).Along / length, 0, 1);

        // Split at a round distance along the line, the way points are placed everywhere else.
        var distance = Units.SnapToGrid(t * length, Math.Min(SnapStepMm, length / 4));
        if (distance <= RoofSketch.JoinTolerance || distance >= length - RoofSketch.JoinTolerance)
        {
            HintChanged?.Invoke(this, "Too near the end of the line to split it there.");
            return false;
        }

        Remember();

        // An arc splits into two arcs of the same circle.
        var first = curve.Part(0, distance);
        var rest = curve.Part(distance, length);
        var second = new RoofSketchLine(rest.Start, rest.End, line.Edge.Copy(), line.IsArc ? rest.Bulge : 0);
        line.End = first.End;
        if (line.IsArc) line.Bulge = first.Bulge;
        _sketch.Insert(_sketch.IndexOf(line) + 1, second);

        AfterSketchEdit($"Split {Units.FormatLength(distance)} along. Each half can now slope, or not, on its own.");
        return true;
    }

    /// <summary>What the cursor moving does in the sketch: the preview of the next click.</summary>
    public void SketchHover(Point2D raw)
    {
        if (Document is null || _sketch is null) return;

        _sketchCursor = raw;

        if (_sketchTool == RoofSketchTool.PickWalls) _sketchPreview = PickPreview(raw);

        InvalidateVisual();
    }

    /// <summary>
    /// Esc in the sketch: first it drops the line or shape half drawn; then it goes back to
    /// selecting lines. It never throws the sketch away - that is Cancel's job, asked for on
    /// purpose, as Revit has it.
    /// </summary>
    public bool SketchEscape()
    {
        if (_sketch is null) return false;

        if (_sketchStart is not null || _sketchPickChain || _trimFirst is not null || _alignReference is not null)
        {
            _sketchStart = null;
            _sketchArcEnd = null;
            _trimFirst = null;
            _alignReference = null;
            _sketchPickChain = false;
            _sketchPreview = _sketchTool == RoofSketchTool.PickWalls ? PickPreview(_sketchCursor) : new List<RoofSketchLine>();
            HintChanged?.Invoke(this, SketchHint());
            InvalidateVisual();
            return true;
        }

        if (_sketchTool != RoofSketchTool.Modify)
        {
            SketchTool = RoofSketchTool.Modify;
            return true;
        }

        if (_sketchSelection.Count > 0)
        {
            _sketchSelection.Clear();
            AfterSketchEdit();
            return true;
        }

        HintChanged?.Invoke(this, "Finish ✓ makes the roof; Cancel ✗ throws the sketch away.");
        return true;
    }

    /// <summary>
    /// TAB while picking walls: widens the pick to every wall joined end to end with the one
    /// under the cursor, so a whole building is picked with one click - Revit's chain pick.
    /// </summary>
    public bool SketchTab()
    {
        if (_sketch is null || _sketchTool != RoofSketchTool.PickWalls) return false;

        _sketchPickChain = !_sketchPickChain;
        _sketchPreview = PickPreview(_sketchCursor);

        HintChanged?.Invoke(this, _sketchPickChain
            ? $"Chain: {_sketchPreview.Count} walls. Click to pick them all, TAB for just the one."
            : SketchHint());

        InvalidateVisual();
        return true;
    }

    private void SelectSketchLineAt(Point2D raw)
    {
        // An arrow is drawn over the roof, and usually over nothing else: it is looked for first.
        var arrow = SketchArrowAt(raw);
        var line = arrow is null ? SketchLineAt(raw) : null;

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _sketchSelection.Clear();
            _sketchArrowSelection.Clear();
        }

        if (arrow is not null && !_sketchArrowSelection.Remove(arrow)) _sketchArrowSelection.Add(arrow);
        if (line is not null && !_sketchSelection.Remove(line)) _sketchSelection.Add(line);

        HintChanged?.Invoke(this, (_sketchSelection.Count, _sketchArrowSelection.Count) switch
        {
            (0, 0) => SketchHint(),
            (1, 0) => DescribeLine(_sketchSelection[0]),
            (0, 1) => DescribeArrow(_sketchArrowSelection[0]),
            var (lines, arrows) => $"{lines + arrows} selected. Del deletes them; the options bar changes them."
        });

        SketchChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private string DescribeLine(RoofSketchLine line)
    {
        var what = line.Edge.DefinesSlope ? $"slopes at {line.Edge.SlopeDegrees:0.##}°" : "is a gable end";
        var from = line.Edge.WallId is null ? "drawn" : $"on a wall, {Units.FormatLength(line.Edge.Overhang)} overhang";
        return $"{Units.FormatLength(line.Length)}, {from}; the roof {what}. Del deletes it; the options bar changes it.";
    }

    private string DescribeArrow(RoofSlopeArrow arrow) => arrow.ByHeights
        ? $"Slope arrow, {Units.FormatLength(arrow.Length)}: from {Units.FormatLength(arrow.TailOffset)} at its tail to {Units.FormatLength(arrow.HeadOffset)} at its head. Del deletes it; the options bar changes it."
        : $"Slope arrow, {Units.FormatLength(arrow.Length)}: rising at {arrow.SlopeDegrees:0.##}° from {Units.FormatLength(arrow.TailOffset)} above the base. Del deletes it; the options bar changes it.";

    private RoofSlopeArrow? SketchArrowAt(Point2D raw)
    {
        var reach = 6 / PixelsPerMm;
        RoofSlopeArrow? best = null;

        foreach (var arrow in _sketchArrows)
        {
            var distance = Line2D.DistanceFromSegment(raw, arrow.Tail, arrow.Head);
            if (distance >= reach) continue;

            reach = distance;
            best = arrow;
        }

        return best;
    }

    private RoofSketchLine? SketchLineAt(Point2D raw, double pixels = 6)
    {
        if (_sketch is null) return null;

        var reach = pixels / PixelsPerMm;
        RoofSketchLine? best = null;

        foreach (var line in _sketch)
        {
            var distance = line.DistanceTo(raw);
            if (distance >= reach) continue;

            reach = distance;
            best = line;
        }

        return best;
    }

    // ---- pick walls -------------------------------------------------------------------

    private bool PickWallsAt(Point2D raw)
    {
        if (_sketch is null || Document is null) return false;

        var picked = PickPreview(raw);
        if (picked.Count == 0)
        {
            HintChanged?.Invoke(this, NearestWall(raw) is { IsCurved: true }
                ? "Curved walls cannot carry a roof edge yet. Draw lines along it instead."
                : "No wall there. Hover just outside a wall - the roof edge goes on the side the cursor is on.");
            return false;
        }

        // A wall already picked on that side is not picked twice.
        var fresh = picked.Where(line => !_sketch.Any(existing =>
            existing.Edge.WallId == line.Edge.WallId && existing.Edge.OnLeftOfWall == line.Edge.OnLeftOfWall)).ToList();

        if (fresh.Count == 0)
        {
            HintChanged?.Invoke(this, "That wall is already in the sketch.");
            return false;
        }

        Remember();

        foreach (var line in fresh)
        {
            _sketch.Add(line);
            RoofSketch.CloseCorners(_sketch, line, CornerReach());
        }

        _sketchPickChain = false;
        _sketchPreview = PickPreview(raw);

        var closes = RoofSketch.Check(_sketch).IsValid;
        AfterSketchEdit(closes
            ? $"The outline closes ({_sketch.Count} lines). Finish ✓ to make the roof, or pick more."
            : $"{_sketch.Count} line{(_sketch.Count == 1 ? "" : "s")}. Pick the next wall.");

        return true;
    }

    /// <summary>How far apart two line ends can be and still be joined as a corner.</summary>
    private double CornerReach()
    {
        var widest = Document?.Walls.Select(wall => Document.GetWallType(wall)?.Width ?? 0).DefaultIfEmpty(0).Max() ?? 0;
        return Math.Max(1500, 3 * (widest + Math.Max(0, SketchOverhang)));
    }

    /// <summary>The lines a click here would add: the wall under the cursor, or its whole chain after TAB.</summary>
    private List<RoofSketchLine> PickPreview(Point2D raw)
    {
        if (Document is null || NearestWall(raw) is not { } wall) return new List<RoofSketchLine>();

        var onLeft = RoofSketch.IsLeftOf(wall, raw);
        var walls = _sketchPickChain ? ChainOf(wall) : new List<(Wall Wall, bool Forward)> { (wall, true) };

        var lines = new List<RoofSketchLine>();

        foreach (var (member, forward) in walls)
        {
            // Every wall in a chain takes the same side of the chain as the one hovered: walls
            // drawn the other way round have that side on their other hand.
            var side = forward ? onLeft : !onLeft;

            if (RoofSketch.LineOnWall(Document, member, side, SketchOverhang, SketchExtendToCore) is not var (start, end))
                continue;

            var line = new RoofSketchLine(start, end, new RoofEdge
            {
                DefinesSlope = SketchDefinesSlope,
                SlopeDegrees = ActiveRoofSlopeDegrees,
                WallId = member.Id,
                OnLeftOfWall = side,
                Overhang = SketchOverhang,
                ExtendToCore = SketchExtendToCore
            });

            lines.Add(line);
        }

        // A chain's own corners are closed in the preview too, so what is shown is what a
        // click will make.
        if (lines.Count > 1)
        {
            var closed = new List<RoofSketchLine>();
            foreach (var line in lines)
            {
                closed.Add(line);
                RoofSketch.CloseCorners(closed, line, CornerReach());
            }
        }

        return lines;
    }

    /// <summary>The straight wall on this storey nearest the cursor, within a few pixels of its body.</summary>
    private Wall? NearestWall(Point2D raw)
    {
        if (Document is null) return null;

        var reach = 10 / PixelsPerMm;
        Wall? best = null;

        foreach (var wall in OnActiveLevel<Wall>())
        {
            var half = (Document.GetWallType(wall)?.Width ?? 0) / 2;
            var (along, left) = wall.LocationCurve.Locate(raw);
            if (along < -reach || along > wall.Length + reach) continue;

            var outside = Math.Abs(left - wall.LeftOf(Document.GetWallType(wall)!.Structure, 0)) - half;
            var distance = Math.Max(0, outside);
            if (distance >= reach) continue;

            reach = distance;
            best = wall;
        }

        return best;
    }

    /// <summary>
    /// The walls joined end to end with this one, in order round, each marked with whether it
    /// runs the same way as the chain. It stops where the walls branch or run out, and closes
    /// when it comes back round.
    /// </summary>
    private List<(Wall Wall, bool Forward)> ChainOf(Wall start)
    {
        var walls = OnActiveLevel<Wall>().Where(wall => !wall.IsCurved).ToList();
        var chain = new List<(Wall Wall, bool Forward)> { (start, true) };
        var used = new HashSet<Wall> { start };

        // Forwards from the end, then backwards from the start.
        foreach (var ahead in new[] { true, false })
        {
            var at = ahead ? start.End : start.Start;

            while (true)
            {
                var next = walls
                    .Where(wall => !used.Contains(wall))
                    .Select(wall => (Wall: wall,
                        FromStart: wall.Start.DistanceTo(at) <= WallJoins.JoinTolerance,
                        FromEnd: wall.End.DistanceTo(at) <= WallJoins.JoinTolerance))
                    .Where(candidate => candidate.FromStart || candidate.FromEnd)
                    .ToList();

                // A branch has no one way on, so the chain stops rather than guess.
                if (next.Count != 1) break;

                var (wall, fromStart, _) = next[0];
                used.Add(wall);

                // Going ahead, a wall that starts here runs with the chain; going back, one that
                // ends here does.
                var forward = ahead ? fromStart : !fromStart;
                if (ahead) chain.Add((wall, forward));
                else chain.Insert(0, (wall, forward));

                at = fromStart ? wall.End : wall.Start;
            }
        }

        return chain;
    }

    // ---- drawing lines ----------------------------------------------------------------

    private RoofEdge NewDrawnEdge() => new()
    {
        DefinesSlope = SketchDefinesSlope,
        SlopeDegrees = ActiveRoofSlopeDegrees
    };

    /// <summary>
    /// Snaps a drawn point to the ends of lines already in the sketch first - that is what
    /// closes a sketch - then to wall ends and the drawing grid. Shift keeps a line level or
    /// plumb, as it does when drawing walls.
    /// </summary>
    private Point2D SketchSnap(Point2D raw)
    {
        var reach = SnapPixelRadius / PixelsPerMm;
        Point2D? best = null;

        foreach (var line in _sketch ?? new List<RoofSketchLine>())
        {
            foreach (var end in new[] { line.Start, line.End })
            {
                var distance = end.DistanceTo(raw);
                if (distance >= reach) continue;

                reach = distance;
                best = end;
            }
        }

        var point = best ?? SnapPoint(raw, null, out _);

        if (best is null && _sketchStart is { } from && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
            _sketchTool == RoofSketchTool.Line)
        {
            point = Math.Abs(point.X - from.X) >= Math.Abs(point.Y - from.Y)
                ? new Point2D(point.X, from.Y)
                : new Point2D(from.X, point.Y);
        }

        return point;
    }

    private bool DrawLineTo(Point2D point)
    {
        if (_sketch is null) return false;

        if (_sketchStart is not { } from)
        {
            _sketchStart = point;
            HintChanged?.Invoke(this, "Click the end of the line. Lines chain on from each other; Esc stops.");
            InvalidateVisual();
            return true;
        }

        if (from.DistanceTo(point) <= RoofSketch.JoinTolerance) return false;

        Remember();
        _sketch.Add(new RoofSketchLine(from, point, NewDrawnEdge()));

        // Chained, until the line comes back to where the chain began.
        var closedChain = _sketch.Count(line => line.Start.DistanceTo(point) <= RoofSketch.JoinTolerance ||
                                                line.End.DistanceTo(point) <= RoofSketch.JoinTolerance) > 1;
        _sketchStart = closedChain ? null : point;

        var closes = RoofSketch.Check(_sketch).IsValid;
        AfterSketchEdit(closes
            ? "The outline closes. Finish ✓ to make the roof."
            : closedChain ? "Line added." : "Line added. Click the next point, or Esc to stop.");
        return true;
    }

    private bool DrawShapeTo(Point2D point)
    {
        if (_sketch is null) return false;

        if (_sketchStart is not { } from)
        {
            _sketchStart = point;
            HintChanged?.Invoke(this, _sketchTool switch
            {
                RoofSketchTool.Rectangle => "Click the opposite corner.",
                RoofSketchTool.Circle => "Click a point on the circle - its radius.",
                _ => $"Click a corner of the {PolygonSides}-sided polygon."
            });
            InvalidateVisual();
            return true;
        }

        var pieces = ShapePieces(from, point);
        if (pieces.Count == 0) return false;

        Remember();
        // A shape drawn inside an outline that already closes is an opening - a skylight, a
        // chimney - and an opening's edges do not slope.
        var outline = RoofSketch.Check(_sketch);
        var opening = outline.IsValid &&
                      pieces.All(piece => Polygon2D.Contains(outline.Boundary, piece.Start) && Polygon2D.Contains(outline.Boundary, piece.End));

        foreach (var piece in pieces)
        {
            var edge = NewDrawnEdge();
            if (opening) edge.DefinesSlope = false;
            _sketch.Add(new RoofSketchLine(piece.Start, piece.End, edge, piece.Bulge));
        }

        _sketchStart = null;
        AfterSketchEdit(opening
            ? "Opening added inside the outline - a hole through the roof. Finish ✓ to make it."
            : RoofSketch.Check(_sketch).IsValid
                ? "The outline closes. Finish ✓ to make the roof."
                : "Shape added.");
        return true;
    }

    private IReadOnlyList<WallPiece> ShapePieces(Point2D from, Point2D to) => _sketchTool switch
    {
        RoofSketchTool.Rectangle => WallShapes.Rectangle(from, to),
        RoofSketchTool.Polygon => WallShapes.Polygon(from, to, PolygonSides, PolygonInscribed),
        RoofSketchTool.Circle => WallShapes.Circle(from, to),
        _ => Array.Empty<WallPiece>()
    };

    // ---- slope markers ----------------------------------------------------------------

    /// <summary>
    /// Where a line's slope marker sits: beside its middle, on the inside of the sketch - the
    /// side the roof rises toward.
    /// </summary>
    private Point MarkerPoint(RoofSketchLine line) => MarkerPlace(line).At;

    /// <summary>Where a line's slope marker sits, and the way into the sketch from it, on screen.</summary>
    private (Point At, Vector Inward) MarkerPlace(RoofSketchLine line)
    {
        // For an arc, beside the middle of the arc, square to it there.
        var facets = line.Facets;
        var half = facets.Count / 2;
        var (from, to) = line.IsArc ? (facets[half - 1], facets[half + 1]) : (line.Start, line.End);

        var a = ModelToScreen(from);
        var b = ModelToScreen(to);
        var middle = line.IsArc ? ModelToScreen(line.Middle) : new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        var along = b - a;
        if (along.Length < 1e-9) return (middle, new Vector(0, -1));
        along.Normalize();

        var side = new Vector(-along.Y, along.X);
        var inside = ModelToScreen(SketchCentre());
        if (Vector.Multiply(side, inside - middle) < 0) side = -side;

        return (middle + side * MarkerInset, side);
    }

    private Point2D SketchCentre()
    {
        var points = (_sketch ?? new List<RoofSketchLine>()).SelectMany(line => new[] { line.Start, line.End }).ToList();
        if (points.Count == 0) return default;

        return new Point2D(points.Average(point => point.X), points.Average(point => point.Y));
    }

    private RoofSketchLine? MarkerAt(Point2D raw)
    {
        if (_sketch is null) return null;

        var screen = ModelToScreen(raw);

        return _sketch.FirstOrDefault(line =>
            line.Length * PixelsPerMm > 3 * MarkerRadius &&
            (MarkerPoint(line) - screen).Length <= MarkerRadius);
    }

    // ---- hints --------------------------------------------------------------------------

    private string SketchHint() => _sketchTool switch
    {
        RoofSketchTool.PickWalls =>
            "Pick Walls: hover just outside a wall and click - the roof edge goes on that face, out by the overhang. " +
            "TAB picks the whole chain. Finish ✓ when the outline closes.",
        RoofSketchTool.Line => "Line: click points round the roof's edge. Shift keeps it straight; Esc stops.",
        RoofSketchTool.Rectangle => "Rectangle: click two opposite corners.",
        RoofSketchTool.Polygon => $"Polygon: click the centre, then a corner ({PolygonSides} sides).",
        RoofSketchTool.SlopeArrow =>
            "Slope Arrow: click on a line of the outline for its tail, then where the slope rises toward. " +
            "On a flat roof one arrow makes it fall one way; two from the ends of a split eave to its middle make a dormer.",
        RoofSketchTool.Split => "Split: click a line where it should divide - an eave split in three leaves its middle for a dormer.",
        RoofSketchTool.Arc => "Arc: click its start, its end, then a point it passes through. A sloping arc rises as part of a cone.",
        RoofSketchTool.Circle => "Circle: click the centre, then a point on it. A sloping circle makes a cone - a turret roof.",
        RoofSketchTool.PickLines =>
            "Pick Lines: click the edge of a floor, ceiling or roof, a wall's face or a grid line to take it as a roof line. " +
            "Offset on the options bar sets it off toward the cursor.",
        RoofSketchTool.Offset =>
            "Offset: type the distance on the options bar, then click a line on the side it should move toward. " +
            "Tick Copy to leave the line and add a copy.",
        RoofSketchTool.TrimExtend =>
            "Trim/Extend: click two lines, each on the part to keep - both are cut back or run on to meet at a corner.",
        RoofSketchTool.AlignEaves =>
            "Align Eaves: each eave's height is shown beside it. Click the eave to match, then the eaves to bring to it - " +
            "by their plate height, or on the options bar by their overhang.",
        _ => "Click a line to select it; Del deletes it. Click a △ to turn that line's slope on or off. Finish ✓ or Cancel ✗."
    };

    // ---- drawing ------------------------------------------------------------------------

    /// <summary>
    /// The sketch on top of the plan, in the magenta sketches are drawn in: its lines, the
    /// slope marker and pitch of each, what the next click would add, and - in red - any line
    /// Finish has objected to.
    /// </summary>
    private void DrawRoofSketch(DrawingContext dc)
    {
        if (_sketch is null) return;

        foreach (var line in _sketch)
        {
            var pen = _sketchCulprits.Contains(line) ? SketchProblemPen
                : _sketchSelection.Contains(line) || _trimFirst is var (first, _) && ReferenceEquals(first, line) ? SketchSelectedPen
                : SketchPen;

            DrawSketchCurve(dc, pen, line.Facets);
            DrawSlopeMarker(dc, line);
        }

        // The line ends, so a sketch that has not closed shows where.
        foreach (var line in _sketch)
        foreach (var end in new[] { line.Start, line.End })
        {
            var point = ModelToScreen(end);
            dc.DrawRectangle(null, SketchMarkerPen, new Rect(point.X - 2.5, point.Y - 2.5, 5, 5));
        }

        foreach (var line in _sketchPreview)
            dc.DrawLine(SketchPreviewPen, ModelToScreen(line.Start), ModelToScreen(line.End));

        foreach (var arrow in _sketchArrows)
            DrawSlopeArrow(dc, arrow, _sketchArrowSelection.Contains(arrow) ? SketchSelectedPen : SketchPen);

        DrawEaveHeights(dc);

        if (_sketchStart is { } from)
        {
            var to = SketchSnap(_sketchCursor);

            if (_sketchTool == RoofSketchTool.Arc)
            {
                // The chord while the second end is chosen; then the arc through the cursor.
                if (_sketchArcEnd is { } end)
                    DrawSketchCurve(dc, SketchPreviewPen, RoofSketch.Facets(from, end, ArcBulge(from, end, to)));
                else
                    dc.DrawLine(SketchPreviewPen, ModelToScreen(from), ModelToScreen(to));
            }
            else if (_sketchTool is RoofSketchTool.Line or RoofSketchTool.SlopeArrow)
            {
                dc.DrawLine(SketchPreviewPen, ModelToScreen(from), ModelToScreen(to));
            }
            else
            {
                foreach (var piece in ShapePieces(from, to))
                    DrawSketchCurve(dc, SketchPreviewPen, RoofSketch.Facets(piece.Start, piece.End, piece.Bulge));
            }
        }
        else if (_sketchTool == RoofSketchTool.PickLines && PickLineAt(_sketchCursor) is var (pickFrom, pickTo))
        {
            dc.DrawLine(SketchPreviewPen, ModelToScreen(pickFrom), ModelToScreen(pickTo));
        }
        else if (_sketchTool == RoofSketchTool.Offset && SketchOffsetAt(_sketchCursor) is var (_, moved))
        {
            DrawSketchCurve(dc, SketchPreviewPen, moved.Facets);
        }
        else if (_sketchTool == RoofSketchTool.SlopeArrow && TailOnLine(_sketchCursor) is { } tail)
        {
            // Where a click would put the tail.
            var at = ModelToScreen(tail);
            dc.DrawEllipse(null, SketchPen, at, 4, 4);
        }
    }

    /// <summary>A sketch line on screen, straight or curved: through every point it is built through.</summary>
    private void DrawSketchCurve(DrawingContext dc, Pen pen, IReadOnlyList<Point2D> points)
    {
        for (var i = 0; i + 1 < points.Count; i++)
            dc.DrawLine(pen, ModelToScreen(points[i]), ModelToScreen(points[i + 1]));
    }

    /// <summary>
    /// A slope arrow as the sketch shows it: a line from tail to head with a head on it, and
    /// what it says - the pitch, or the heights at the two ends.
    /// </summary>
    private void DrawSlopeArrow(DrawingContext dc, RoofSlopeArrow arrow, Pen pen)
    {
        var tail = ModelToScreen(arrow.Tail);
        var head = ModelToScreen(arrow.Head);
        var direction = head - tail;
        if (direction.Length < 1) return;

        direction.Normalize();
        var side = new Vector(-direction.Y, direction.X);

        dc.DrawLine(pen, tail, head);
        dc.DrawLine(pen, head, head - direction * 10 + side * 5);
        dc.DrawLine(pen, head, head - direction * 10 - side * 5);
        dc.DrawEllipse(null, pen, tail, 3, 3);

        var label = arrow.ByHeights
            ? $"{Units.FormatLength(arrow.TailOffset)} → {Units.FormatLength(arrow.HeadOffset)}"
            : $"{arrow.SlopeDegrees:0.##}°";

        var text = new FormattedText(
            label,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            SketchSlopeBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var middle = new Point((tail.X + head.X) / 2, (tail.Y + head.Y) / 2);
        dc.DrawText(text, middle + side * 8 - new Vector(text.Width / 2, text.Height / 2));
    }

    /// <summary>
    /// A line's slope marker: a filled triangle and its pitch where the line defines slope, an
    /// empty one where it does not - so every line shows the control that changes it.
    /// </summary>
    private void DrawSlopeMarker(DrawingContext dc, RoofSketchLine line)
    {
        if (line.Length * PixelsPerMm <= 3 * MarkerRadius) return;

        var at = MarkerPoint(line);
        var triangle = new StreamGeometry();

        using (var context = triangle.Open())
        {
            context.BeginFigure(new Point(at.X, at.Y - 6), true, true);
            context.LineTo(new Point(at.X + 6, at.Y + 5), true, false);
            context.LineTo(new Point(at.X - 6, at.Y + 5), true, false);
        }

        triangle.Freeze();
        dc.DrawGeometry(line.Edge.DefinesSlope ? SketchSlopeBrush : null, SketchMarkerPen, triangle);

        if (!line.Edge.DefinesSlope) return;

        var text = new FormattedText(
            $"{line.Edge.SlopeDegrees:0.##}°",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            SketchSlopeBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var label = PitchLabelBounds(line);
        dc.DrawText(text, label.TopLeft);
    }
}
