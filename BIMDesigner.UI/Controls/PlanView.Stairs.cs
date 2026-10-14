using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Stairs on the plan: click where its first riser goes, then which way is up - it lays itself
/// out from there to the level above, straight or turning as the option bar says, its risers
/// worked out from the storey's height, and cuts its opening in the floor over it. A custom
/// stair is drawn by clicking along its walking line - its foot, each place it turns, and where
/// it ends, with a double-click.
/// </summary>
public partial class PlanView
{
    /// <summary>The stair type the Stair tool puts in.</summary>
    public Guid ActiveStairTypeId { get; set; }

    /// <summary>How the next stair is laid out.</summary>
    public StairShape NewStairShape { get; set; }

    public StairCorner NewStairCorner { get; set; }

    public bool NewStairTurnsLeft { get; set; }

    public double NewStairWidth { get; set; } = 900;

    /// <summary>A spiral or curved stair's inside radius; 0 for the usual.</summary>
    public double NewStairInnerRadius { get; set; }

    private Point2D? _stairStart;
    private Point2D? _stairHover;

    /// <summary>A custom stair's walking line so far, as it is clicked.</summary>
    private readonly List<Point2D> _stairPoints = new();

    private void StairHover(Point2D at)
    {
        _stairHover = at;
        InvalidateVisual();
    }

    /// <summary>A stair from a foot toward a point: up from this storey to the one above, as the option bar says.</summary>
    private Stair NewStair(Point2D start, Point2D toward)
    {
        var way = toward - start;
        // A house's stair, timber with closed risers, unless another is picked.
        var type = Document?.FindType<StairType>(ActiveStairTypeId)
                   ?? Document?.TypesOf<StairType>().FirstOrDefault(candidate => candidate.Construction == StairConstruction.Timber && !candidate.OpenRisers)
                   ?? Document?.TypesOf<StairType>().FirstOrDefault();
        return new Stair
        {
            LevelId = ActiveLevelId,
            TopLevelId = Document is null ? null : Levels.Above(Document, ActiveLevelId)?.Id,
            TypeId = type?.Id ?? Guid.Empty,
            Start = start,
            Angle = way.Length < 1 ? 90 : Math.Atan2(way.Y, way.X) * 180 / Math.PI,
            Shape = NewStairShape,
            Corner = NewStairCorner,
            TurnsLeft = NewStairTurnsLeft,
            Width = NewStairWidth,
            InnerRadius = NewStairInnerRadius
        };
    }

    /// <summary>A custom stair along the points of its walking line - with the cursor as one more, while it is drawn.</summary>
    private Stair NewStairAlong(IReadOnlyList<Point2D> points)
    {
        var stair = NewStair(points[0], points.Count > 1 ? points[1] : points[0] + new Vector2D(0, 1000));
        stair.TurnsLeft = false;
        if (points.Count > 1) StairGrips.SetPath(stair, points);
        stair.Shape = StairShape.Custom;
        return stair;
    }

    /// <summary>
    /// A click with the Stair tool: its foot, then which way is up - or for a custom stair, the
    /// next point along its walking line; clicking the last one again finishes it. Public so it
    /// can be driven from tests.
    /// </summary>
    public bool ClickStair(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        if (NewStairShape == StairShape.Custom)
        {
            if (_stairPoints.Count > 0 && (at - _stairPoints[^1]).Length * PixelsPerMm < 8) return FinishStair();
            _stairPoints.Add(at);
            HintChanged?.Invoke(this, _stairPoints.Count == 1
                ? "Now click along its walking line: each place it turns, then where it ends - double-click to finish. Turns are to the nearest 15°, a right angle at most."
                : "Click where it turns next, or double-click where it ends. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if (_stairStart is not { } start)
        {
            _stairStart = at;
            HintChanged?.Invoke(this, "Now click which way is up: the stair runs that way from here, up to the level above.");
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 10) return false;

        var stair = NewStair(start, at);
        stair.Outside = Stairs.StandsOutside(Document, stair);
        _stairStart = null;
        Apply(new AddElementCommand(Document, stair, "Stair"));

        var layout = Stairs.Layout(Document, stair);
        HintChanged?.Invoke(this, $"Stair in: {layout.Risers} risers of {layout.Rise:0.#}, going {layout.Going:0}, pitch {layout.Pitch:0.#}°. {Stairs.Verdict(Document, stair)}. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A custom stair finished along the points clicked: put in, laid out along them. False
    /// with fewer than two. Public so it can be driven from tests.
    /// </summary>
    public bool FinishStair()
    {
        if (Document is null || NewStairShape != StairShape.Custom || _stairPoints.Count < 2) return false;

        var stair = NewStairAlong(_stairPoints);
        stair.Outside = Stairs.StandsOutside(Document, stair);
        _stairPoints.Clear();
        Apply(new AddElementCommand(Document, stair, "Stair"));

        var layout = Stairs.Layout(Document, stair);
        HintChanged?.Invoke(this, $"Stair in: {layout.Risers} risers of {layout.Rise:0.#}, going {layout.Going:0}, turning {stair.PathTurns.Count} times. {Stairs.Verdict(Document, stair)}. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Esc with a stair begun: its foot, or the walking line clicked so far, forgotten.</summary>
    private bool CancelStair()
    {
        if (_stairStart is null && _stairPoints.Count == 0) return false;
        _stairStart = null;
        _stairPoints.Clear();
        InvalidateVisual();
        return true;
    }

    /// <summary>The stair the next click would put in, following the cursor: its steps and the line walked up it.</summary>
    private void DrawStairPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Stair || Document is null) return;

        // A custom stair as clicked so far, on to the cursor.
        if (_stairPoints.Count > 0)
        {
            var points = _stairHover is { } next ? _stairPoints.Append(next).ToList() : _stairPoints.ToList();
            foreach (var (a, b) in points.Zip(points.Skip(1))) dc.DrawLine(SketchPreviewPen, ModelToScreen(a), ModelToScreen(b));
            if (points.Count < 2) return;

            var drawn = Stairs.Layout(Document, NewStairAlong(points));
            foreach (var step in drawn.Steps) dc.DrawGeometry(null, SketchPreviewPen, Outline(step.Outline));
            return;
        }

        if (_stairStart is not { } start)
        {
            if (_stairHover is { } hover)
            {
                var arm = 8.0;
                var at = ModelToScreen(hover);
                dc.DrawLine(SketchPreviewPen, new System.Windows.Point(at.X - arm, at.Y), new System.Windows.Point(at.X + arm, at.Y));
                dc.DrawLine(SketchPreviewPen, new System.Windows.Point(at.X, at.Y - arm), new System.Windows.Point(at.X, at.Y + arm));
            }

            return;
        }

        var toward = _stairHover ?? start + new Vector2D(0, 1000);
        var stair = NewStair(start, toward);
        var layout = Stairs.Layout(Document, stair);
        foreach (var step in layout.Steps) dc.DrawGeometry(null, SketchPreviewPen, Outline(step.Outline));
        foreach (var walk in layout.Walks)
            for (var i = 0; i + 1 < walk.Count; i++)
                dc.DrawLine(SketchPreviewPen, ModelToScreen(walk[i]), ModelToScreen(walk[i + 1]));
    }

    /// <summary>The stairs under a point of this plan: on their own storey, or seen down the stairwell from the one they come up to.</summary>
    private IEnumerable<Stair> StairsAt(Point2D model)
    {
        if (Document is null) yield break;

        foreach (var stair in Document.Elements.OfType<Stair>().Where(stair => _shows(stair)))
            if (Stairs.Flights(Document, stair).Any(flight => (flight.LevelId == ActiveLevelId || flight.TopLevelId == ActiveLevelId)
                                                              && flight.Layout.Steps.Any(step => Polygon2D.Contains(step.Outline, model))))
                yield return stair;
    }
}
