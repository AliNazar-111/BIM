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

    /// <summary>
    /// Which way the Stair tool draws a stair from where it is clicked: up from its foot there to
    /// the storey above; down from its head there - this storey's floor, a balcony's edge, a
    /// terrace - to the floor of the storey below; or as the click goes: down where it is drawn
    /// off the edge of a floor, deck or terrace over open space, up otherwise.
    /// </summary>
    public StairDirection NewStairDirection { get; set; } = StairDirection.Auto;

    /// <summary>Whether the Stair tool draws stairs down from where they are clicked, always.</summary>
    public bool NewStairGoesDown
    {
        get => NewStairDirection == StairDirection.Down;
        set => NewStairDirection = value ? StairDirection.Down : StairDirection.Up;
    }

    /// <summary>
    /// Whether a stair drawn from a point toward another goes down: told so, or - left to work it
    /// out - drawn from a floor, deck or terrace on this storey out over open space, where there
    /// is neither floor nor ground on this storey ahead of it, and a storey below to go down to.
    /// </summary>
    private bool GoesDown(Point2D from, Point2D toward)
    {
        if (NewStairDirection != StairDirection.Auto) return NewStairDirection == StairDirection.Down;
        if (Document?.FindLevel(ActiveLevelId) is not { } level || Levels.Below(Document, ActiveLevelId) is null) return false;

        var way = (toward - from).NormalisedOrDefault(Vector2D.UnitY);
        bool Underfoot(Point2D at) =>
            FloorSurfaces.StoodOnNear(Document, level.Elevation, at, Stairs.FloorReach) is not null || Terrains.GroundNear(Document, level.Elevation, at, Stairs.FloorReach) is not null;
        return Underfoot(from - way * 300) && !Underfoot(from + way * 300) && !Underfoot(from + way * 1500);
    }

    /// <summary>A stair from a foot toward a point: up from this storey to the one above, as the option bar says.</summary>
    private Stair NewStair(Point2D start, Point2D toward) => NewStair(start, toward, ActiveLevelId);

    private Stair NewStair(Point2D start, Point2D toward, Guid levelId)
    {
        var way = toward - start;
        // A house's stair, timber with closed risers, unless another is picked.
        var type = Document?.FindType<StairType>(ActiveStairTypeId)
                   ?? Document?.TypesOf<StairType>().FirstOrDefault(candidate => candidate.Construction == StairConstruction.Timber && !candidate.OpenRisers)
                   ?? Document?.TypesOf<StairType>().FirstOrDefault();
        return new Stair
        {
            LevelId = levelId,
            TopLevelId = Document is null ? null : Levels.Above(Document, levelId)?.Id,
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

    /// <summary>
    /// A stair down from a point on a storey toward another: its head there, its last flight
    /// coming up the way back, from the floor of the storey below - laid out, turned and moved
    /// until its head is where it was clicked. None where there is no storey below.
    /// </summary>
    private Stair? NewStairDown(Point2D head, Point2D toward, Guid levelId)
    {
        if (Document is null || Levels.Below(Document, levelId) is not { } below) return null;
        var down = (toward - head).NormalisedOrDefault(new Vector2D(0, -1));
        var stair = NewStair(head, head - down, levelId);
        (stair.LevelId, stair.TopLevelId) = (below.Id, levelId);

        // Its last flight coming up the way back from where the click went down, its head where it was clicked.
        Stairs.HangFrom(Document, stair, head, down * -1);
        return stair;
    }

    /// <summary>The stair the tool puts in from a first click toward a second: up from its foot, or down from its head.</summary>
    private Stair? PlannedStair(Point2D first, Point2D toward) =>
        GoesDown(first, toward) ? NewStairDown(first, toward, ActiveLevelId) : NewStair(first, toward);

    /// <summary>
    /// A click on a floor, a terrace or the ground in 3D with the Stair tool: a straight stair
    /// there as the option bar has it - its foot there going up, or its head there going down -
    /// running north, from the storey whose floor was clicked; turned, moved and reshaped after
    /// by its handles. Public so it can be driven from tests.
    /// </summary>
    public bool PlaceStairIn3D(Guid clicked, Core.Geometry.Point3D at)
    {
        if (Document is null || Document.Levels.Count == 0) return false;

        // Only on something stood on: a floor, a terrace, a deck, the ground - its plane, with none modelled - not a wall, a pitched roof or another stair.
        var on = Document.FindElement(clicked);
        if (on is not (null or Toposurface or GroundRegion) && !(on is Slab slab && slab is not Ceiling && FloorSurfaces.IsStoodOn(Document, slab)))
        {
            HintChanged?.Invoke(this, "Click a floor, a terrace, a deck or the ground: the stair goes there.");
            return false;
        }

        var level = Document.Levels.Where(candidate => candidate.Elevation <= at.Z + Stairs.FloorReach).OrderByDescending(candidate => candidate.Elevation).FirstOrDefault()
                    ?? Document.Levels.OrderBy(candidate => candidate.Elevation).First();
        var point = new Point2D(at.X, at.Y);
        var (down, toward) = (NewStairDirection == StairDirection.Down, point + new Vector2D(0, NewStairGoesDown ? -1000 : 1000));

        // Left to work it out: clicked by the open edge of a floor, terrace or deck, it goes down from that edge, out over the drop.
        if (NewStairDirection == StairDirection.Auto && on is Slab edged && Levels.Below(Document, level.Id) is not null)
        {
            var boundary = edged.Boundary;
            var nearest = Enumerable.Range(0, boundary.Count).MinBy(i => Line2D.DistanceFromSegment(point, boundary[i], boundary[(i + 1) % boundary.Count]));
            var (a, b) = (boundary[nearest], boundary[(nearest + 1) % boundary.Count]);
            var edge = Line2D.ClosestPointOnSegment(point, a, b);
            var outward = (b - a).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
            if (edged.Contains(edge + outward * 50)) outward *= -1;
            bool Underfoot(Point2D here) =>
                FloorSurfaces.StoodOnNear(Document, level.Elevation, here, Stairs.FloorReach) is not null || Terrains.GroundNear(Document, level.Elevation, here, Stairs.FloorReach) is not null;
            if (edge.DistanceTo(point) < 600 && !Underfoot(edge + outward * 300) && !Underfoot(edge + outward * 1500))
                (down, point, toward) = (true, edge, edge + outward * 1000);
        }

        var stair = down ? NewStairDown(point, toward, level.Id) : NewStair(point, toward, level.Id);
        if (stair is null)
        {
            HintChanged?.Invoke(this, $"No storey below {level.Name} for a stair to go down to: click where its foot goes, going up.");
            return false;
        }

        stair.Shape = NewStairShape == StairShape.Custom ? StairShape.Straight : NewStairShape;
        stair.Outside = Stairs.StandsOutside(Document, stair);
        Apply(new AddElementCommand(Document, stair, "Stair"));
        var layout = Stairs.Layout(Document, stair);
        HintChanged?.Invoke(this, $"Stair in from {level.Name}: {layout.Risers} risers of {layout.Rise:0.#}. Pick it to turn it, move it or reshape it by its handles. {Stairs.Verdict(Document, stair)}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
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
            HintChanged?.Invoke(this, NewStairDirection switch
            {
                StairDirection.Down => "Now click which way is down: the stair runs down that way from here, its head here, to the level below.",
                StairDirection.Up => "Now click which way is up: the stair runs that way from here, up to the level above.",
                _ => "Now click which way it runs: out over open space off the edge of a floor, deck or terrace it goes down from here to the level below; otherwise up from here to the level above."
            });
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 10) return false;

        if (PlannedStair(start, at) is not { } stair)
        {
            _stairStart = null;
            HintChanged?.Invoke(this, "There is no storey below this one for a stair to go down to: draw it going up, from the storey below.");
            InvalidateVisual();
            return false;
        }

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

        var toward = _stairHover is { } aim && aim.DistanceTo(start) >= 10 ? aim : start + new Vector2D(0, NewStairGoesDown ? -1000 : 1000);
        if (PlannedStair(start, toward) is not { } stair) return;
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
