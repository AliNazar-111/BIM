using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Ramps on the plan: click where it starts, then which way it runs. Up from its foot there -
/// on the ground, it rises to this storey's floor - or, from the edge of a floor, a doorway or
/// a terrace out over open space, down from its head there to the ground. Its flights and
/// landings it lays out itself, as its rules ask at the gradient on the option bar. A drawn
/// ramp is clicked along its walking line - its foot, each place it turns - and finished with a
/// double-click; it runs on from its last turn as far as it must.
/// </summary>
public partial class PlanView
{
    /// <summary>The ramp type the Ramp tool puts in.</summary>
    public Guid ActiveRampTypeId { get; set; }

    public RampShape NewRampShape { get; set; }

    public bool NewRampTurnsLeft { get; set; }

    public double NewRampWidth { get; set; } = 1200;

    /// <summary>How steep the next ramp is, as one in so many.</summary>
    public double NewRampGradient { get; set; } = 15;

    /// <summary>How high the next ramp rises; none to work it out from the ground and the floor it comes up to.</summary>
    public double? NewRampRise { get; set; }

    /// <summary>The building the next ramp serves: a dwelling, or another, which asks more of it.</summary>
    public RailingBuilding NewRampBuilding { get; set; } = RailingBuilding.Dwelling;

    /// <summary>Which way the Ramp tool draws a ramp from where it is clicked: up from its foot, down from its head, or as the click goes.</summary>
    public StairDirection NewRampDirection { get; set; } = StairDirection.Auto;

    /// <summary>The rise a ramp takes with nothing to measure it from, mm: a house's floor over the ground outside.</summary>
    public const double UsualRampRise = 450;

    private Point2D? _rampStart;
    private Point2D? _rampHover;

    /// <summary>A drawn ramp's walking line so far, as it is clicked.</summary>
    private readonly List<Point2D> _rampPoints = new();

    private void RampHover(Point2D at)
    {
        _rampHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// Whether a ramp drawn from a point toward another goes down: told so, or - left to work it
    /// out - drawn from a floor, deck or terrace on this storey out to where there is no floor.
    /// </summary>
    private bool RampGoesDown(Point2D from, Vector2D way)
    {
        if (NewRampDirection != StairDirection.Auto) return NewRampDirection == StairDirection.Down;
        if (Document?.FindLevel(ActiveLevelId) is not { } level) return false;
        bool Floor(Point2D at) => FloorSurfaces.StoodOnNear(Document, level.Elevation, at, Stairs.FloorReach) is not null;
        return Floor(from - way * 300) && !Floor(from + way * 300) && !Floor(from + way * 1500);
    }

    /// <summary>
    /// A ramp from a point, one way or the other, on a storey. Up from its foot: from the floor
    /// or the ground there - with none modelled, the ground the building stands on - to the floor
    /// beyond its head higher than it, this storey's where its foot is below it, or up the rise on
    /// the option bar. Down from its head: from the floor it is clicked by to whatever is lower
    /// where its foot comes out - the ground, or a lower floor - or the rise on the option bar
    /// below. Anchored by the end clicked. Shaped - drawn along a line - before its heights are
    /// worked out, so its foot lands where it comes out as drawn. None, and why, where there is
    /// nothing higher or lower to ramp to: no rise is made up.
    /// </summary>
    private Ramp? NewRamp(Point2D point, Vector2D way, bool down, Guid levelId, out string? why, Action<Ramp>? shape = null)
    {
        why = null;
        if (Document?.FindLevel(levelId) is not { } level) return null;
        way = way.NormalisedOrDefault(Vector2D.UnitY);
        var type = Document.FindType<RampType>(ActiveRampTypeId) ?? Document.TypesOf<RampType>().FirstOrDefault();
        var ramp = new Ramp
        {
            LevelId = level.Id,
            TypeId = type?.Id ?? Guid.Empty,
            Shape = NewRampShape == RampShape.Custom ? RampShape.Straight : NewRampShape,
            TurnsLeft = NewRampTurnsLeft,
            Width = NewRampWidth,
            Gradient = NewRampGradient,
            Building = NewRampBuilding,
            Start = point
        };
        ramp.TopLanding = Ramps.EndLanding(Document, ramp);

        double Angle(Vector2D up) => (Math.Atan2(up.Y, up.X) * 180 / Math.PI % 360 + 360) % 360;
        if (down)
        {
            // Its head here, at the floor it is clicked by, going up the way back.
            var floor = FloorSurfaces.StoodOnNear(Document, level.Elevation, point - way * 300, Stairs.FloorReach)?.Top ?? level.Elevation;
            (ramp.Anchor, ramp.Angle, ramp.TopLevelId, ramp.TopOffset) = (RampAnchor.Head, Angle(way * -1), level.Id, floor - level.Elevation);
            ramp.BaseOffset = ramp.TopOffset - (NewRampRise ?? UsualRampRise);
            shape?.Invoke(ramp);
            if (NewRampRise is not null) return ramp;

            // Its foot on whatever is lower where it comes out - the ground, or a lower floor - as it is laid out down to it.
            for (var pass = 0; pass < 6; pass++)
            {
                if (Lower(Ramps.Layout(Document, ramp).Foot, floor) is not { } lower)
                {
                    why = Document.Elements.OfType<Toposurface>().Any()
                        ? "There is nothing lower than the floor here for a ramp to go down to. Give its Rise on the option bar, or draw it from somewhere higher."
                        : "The floor here is level with the ground - with no ground modelled, the ground is taken to be at the lowest level - so there is nothing for a ramp to go down to. Model the ground outside lower with a Toposurface, or give its Rise on the option bar.";
                    return null;
                }

                var offset = Math.Round(lower - level.Elevation);
                if (Math.Abs(offset - ramp.BaseOffset) < 1) break;
                ramp.BaseOffset = offset;
            }

            return ramp;
        }

        // Its foot here, on the floor or the ground there, going up the way clicked.
        var under = FloorSurfaces.StoodOnNear(Document, level.Elevation, point, 50)?.Top ?? Ramps.GroundUnder(Document, point);
        (ramp.Anchor, ramp.Angle, ramp.BaseOffset) = (RampAnchor.Foot, Angle(way), Math.Round(under - level.Elevation));
        shape?.Invoke(ramp);
        if (NewRampRise is { } rise)
        {
            ramp.UnconnectedHeight = rise;
            return ramp;
        }

        // Up to the floor beyond its head higher than its foot: this storey's, where its foot is below it, or the one it comes up onto.
        ramp.UnconnectedHeight = UsualRampRise;
        double? onto = under < level.Elevation - Ramps.MinimumRise ? level.Elevation : null;
        for (var pass = 0; pass < 4; pass++)
        {
            if (onto is { } top) (ramp.TopLevelId, ramp.TopOffset) = (level.Id, Math.Round(top - level.Elevation));
            var layout = Ramps.Layout(Document, ramp);
            var beyond = layout.Head + layout.HeadUp * 300;
            var floors = Document.Elements.OfType<Slab>()
                .Where(slab => slab.Boundary.Count >= 3 && slab is not Ceiling && slab.Contains(beyond) && FloorSurfaces.IsStoodOn(Document, slab))
                .Select(slab => FloorSurfaces.TopAt(Document, slab, beyond))
                .Where(height => height > under + Ramps.MinimumRise && height < under + 10000)
                .ToList();
            if (floors.Count == 0) break;
            var reached = floors.MinBy(height => Math.Abs(height - layout.Top));
            if (onto is { } known && Math.Abs(reached - known) < 1) break;
            onto = reached;
        }

        if (onto is null)
        {
            why = Document.Elements.OfType<Toposurface>().Any()
                ? "There is nothing higher here for a ramp to come up to. Point it at the floor it is to reach, or give its Rise on the option bar."
                : "The floor here is level with the ground - with no ground modelled, the ground is taken to be at the lowest level - so there is nothing for a ramp to come up to. Model the ground outside lower with a Toposurface, or give its Rise on the option bar.";
            return null;
        }

        return ramp;
    }

    /// <summary>The highest of what is lower than a height at a point - a floor there, or the ground - if anything is, by a ramp's least rise.</summary>
    private double? Lower(Point2D at, double than)
    {
        if (Document is null) return null;
        var lower = Document.Elements.OfType<Slab>()
            .Where(slab => slab.Boundary.Count >= 3 && slab is not Ceiling && slab.Contains(at) && FloorSurfaces.IsStoodOn(Document, slab))
            .Select(slab => FloorSurfaces.TopAt(Document, slab, at))
            .Append(Ramps.GroundUnder(Document, at))
            .Where(height => height < than - Ramps.MinimumRise)
            .ToList();
        return lower.Count == 0 ? null : lower.Max();
    }

    /// <summary>The ramp the tool puts in from a first click toward a second: up from its foot, or down from its head.</summary>
    private Ramp? PlannedRamp(Point2D first, Point2D toward, out string? why)
    {
        var way = (toward - first).NormalisedOrDefault(Vector2D.UnitY);
        return NewRamp(first, way, RampGoesDown(first, way), ActiveLevelId, out why);
    }

    /// <summary>
    /// A drawn ramp along the points of its walking line - with the cursor as one more, while it is
    /// drawn: up from its foot at the first, or - drawn off the edge of a floor, a doorway or a
    /// terrace, or told to - down from its head there, running on from the last turn to the ground.
    /// </summary>
    private Ramp? NewRampAlong(IReadOnlyList<Point2D> points, out string? why)
    {
        var way = points.Count > 1 ? points[1] - points[0] : Vector2D.UnitY;
        var down = RampGoesDown(points[0], way.NormalisedOrDefault(Vector2D.UnitY));
        return NewRamp(points[0], way, down, ActiveLevelId, out why, ramp =>
        {
            ramp.TurnsLeft = false;
            ramp.Shape = RampShape.Custom;
            if (points.Count > 1) Ramps.SetPath(ramp, points, down);
        });
    }

    /// <summary>A ramp put in, said in the hint, the plan drawn again.</summary>
    private void PutRamp(Ramp ramp, string ending)
    {
        if (Document is null) return;
        ramp.Outside = Ramps.StandsOutside(Document, ramp);
        Apply(new AddElementCommand(Document, ramp, "Ramp"));
        HintChanged?.Invoke(this, $"Ramp in: {Ramps.Summary(Document, ramp)}. {Ramps.Verdict(Document, ramp)}. {ending}");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Ramp tool: where it starts, then which way it runs - or for a drawn ramp,
    /// the next point along its walking line; clicking the last one again finishes it. Public so
    /// it can be driven from tests.
    /// </summary>
    public bool ClickRamp(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        if (NewRampShape == RampShape.Custom)
        {
            if (_rampPoints.Count > 0 && (at - _rampPoints[^1]).Length * PixelsPerMm < 8) return FinishRamp();
            _rampPoints.Add(at);
            HintChanged?.Invoke(this, _rampPoints.Count == 1
                ? "Now click along its walking line - up from its foot, or down from a doorway or a terrace's edge - each place it turns, a landing there, then double-click. It runs on from the last turn as far as it must."
                : "Click where it turns next, or double-click to finish. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if (_rampStart is not { } start)
        {
            _rampStart = at;
            HintChanged?.Invoke(this, NewRampDirection switch
            {
                StairDirection.Down => "Now click which way is down: the ramp runs down that way from here, its head here, to the ground.",
                StairDirection.Up => "Now click which way is up: the ramp runs up that way from here.",
                _ => "Now click which way it runs: off the edge of a floor, a doorway or a terrace it goes down from here to the ground; otherwise up from here to this storey's floor."
            });
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 10) return false;
        _rampStart = null;
        if (PlannedRamp(start, at, out var why) is not { } ramp)
        {
            HintChanged?.Invoke(this, why ?? "No ramp there.");
            InvalidateVisual();
            return false;
        }

        PutRamp(ramp, "Click for another; Esc to stop.");
        return true;
    }

    /// <summary>A drawn ramp finished along the points clicked: put in, laid out along them. False with fewer than two. Public so it can be driven from tests.</summary>
    public bool FinishRamp()
    {
        if (Document is null || NewRampShape != RampShape.Custom || _rampPoints.Count < 2) return false;
        var ramp = NewRampAlong(_rampPoints, out var why);
        _rampPoints.Clear();
        if (ramp is null)
        {
            HintChanged?.Invoke(this, why ?? "No ramp there.");
            InvalidateVisual();
            return false;
        }

        PutRamp(ramp, "Click for another; Esc to stop.");
        return true;
    }

    /// <summary>Esc with a ramp begun: where it starts, or the walking line clicked so far, forgotten.</summary>
    private bool CancelRamp()
    {
        if (_rampStart is null && _rampPoints.Count == 0) return false;
        _rampStart = null;
        _rampPoints.Clear();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A click on a floor, a terrace or the ground in 3D with the Ramp tool: a straight ramp
    /// there as the option bar has it - its foot there going up, or its head there going down -
    /// running north, from the storey whose floor was clicked; turned and moved after by its
    /// handles. Public so it can be driven from tests.
    /// </summary>
    public bool PlaceRampIn3D(Guid clicked, Core.Geometry.Point3D at)
    {
        if (Document is null || Document.Levels.Count == 0) return false;

        var on = Document.FindElement(clicked);
        if (on is not (null or Toposurface or GroundRegion) && !(on is Slab slab && slab is not Ceiling && FloorSurfaces.IsStoodOn(Document, slab)))
        {
            HintChanged?.Invoke(this, "Click a floor, a terrace, a deck or the ground: the ramp goes there.");
            return false;
        }

        // The storey whose floor is at or below what was clicked - within reach of the ground under a floor.
        var level = Document.Levels.Where(candidate => candidate.Elevation <= at.Z + Terrains.Reach).OrderByDescending(candidate => candidate.Elevation).FirstOrDefault()
                    ?? Document.Levels.OrderBy(candidate => candidate.Elevation).First();
        var down = NewRampDirection == StairDirection.Down;
        if (NewRamp(new Point2D(at.X, at.Y), new Vector2D(0, down ? -1 : 1), down, level.Id, out var why) is not { } ramp)
        {
            HintChanged?.Invoke(this, why ?? "No ramp there.");
            return false;
        }

        PutRamp(ramp, "Pick it to turn it, move it or widen it by its handles.");
        return true;
    }

    /// <summary>The ramp the next click would put in, following the cursor: its flights and landings and the line walked up it.</summary>
    private void DrawRampPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Ramp || Document is null) return;

        Ramp? ramp = null;
        if (_rampPoints.Count > 0)
        {
            var points = _rampHover is { } next ? _rampPoints.Append(next).ToList() : _rampPoints.ToList();
            foreach (var (a, b) in points.Zip(points.Skip(1))) dc.DrawLine(SketchPreviewPen, ModelToScreen(a), ModelToScreen(b));
            if (points.Count >= 2) ramp = NewRampAlong(points, out _);
        }
        else if (_rampStart is { } start)
        {
            var toward = _rampHover is { } aim && aim.DistanceTo(start) >= 10 ? aim : start + Vector2D.UnitY * 1000;
            ramp = PlannedRamp(start, toward, out _);
        }
        else if (_rampHover is { } hover)
        {
            var arm = 8.0;
            var at = ModelToScreen(hover);
            dc.DrawLine(SketchPreviewPen, new System.Windows.Point(at.X - arm, at.Y), new System.Windows.Point(at.X + arm, at.Y));
            dc.DrawLine(SketchPreviewPen, new System.Windows.Point(at.X, at.Y - arm), new System.Windows.Point(at.X, at.Y + arm));
        }

        if (ramp is null) return;
        var layout = Ramps.Layout(Document, ramp);
        foreach (var piece in layout.Pieces) dc.DrawGeometry(null, SketchPreviewPen, Outline(piece.Outline));
        for (var i = 0; i + 1 < layout.Walk.Count; i++) dc.DrawLine(SketchPreviewPen, ModelToScreen(layout.Walk[i]), ModelToScreen(layout.Walk[i + 1]));
    }

    /// <summary>The ramps under a point of this plan: on their own storey, or seen from the one they come up to.</summary>
    private IEnumerable<Ramp> RampsAt(Point2D model)
    {
        if (Document is null) yield break;
        foreach (var ramp in Document.Elements.OfType<Ramp>().Where(ramp => _shows(ramp) && (ramp.LevelId == ActiveLevelId || ramp.TopLevelId == ActiveLevelId)))
            if (Ramps.Layout(Document, ramp).PieceAt(model) is not null)
                yield return ramp;
    }
}
