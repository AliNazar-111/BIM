using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Railings on the plan: click along where one runs - each corner - and double-click where it
/// ends, or click its first point again to take it all the way round. It stands on whatever
/// floor, balcony or terrace is under it. And Guard Edges, for a floor or a flat roof picked:
/// railings along every edge of it that drops too far with nothing there, as high as its rules
/// ask, leaving the walls, the floors it runs on into and the stairs coming up onto it open.
/// </summary>
public partial class PlanView
{
    /// <summary>The railing type the Railing tool puts up; none for the first there is.</summary>
    public Guid ActiveRailingTypeId { get; set; }

    /// <summary>The building railings are put up in: a dwelling, or another, which asks more of them.</summary>
    public RailingBuilding ActiveRailingBuilding { get; set; } = RailingBuilding.Dwelling;

    /// <summary>
    /// Whether the Railing tool puts a railing along the edge of a floor, balcony or terrace
    /// clicked, rather than along points clicked: each edge clicked next to one already railed
    /// joins it at the corner, and all of them round makes one railing all the way round.
    /// </summary>
    public bool PickingEdges { get; set; }

    /// <summary>
    /// Whether the Railing tool draws its next lengths as arcs: each one's end clicked, then a
    /// point it bows through - round a curved balcony or terrace.
    /// </summary>
    public bool RailingArc { get; set; }

    /// <summary>The railing being drawn, its corners so far.</summary>
    private readonly List<Point2D> _railingPoints = new();

    /// <summary>How far each length of the railing being drawn bows, from each corner to the next.</summary>
    private readonly List<double> _railingBulges = new();

    /// <summary>Drawing an arc: its end, clicked, waiting for a point it bows through.</summary>
    private Point2D? _railingArcEnd;

    /// <summary>Whether the arc being drawn ends on the first point, closing the railing all the way round.</summary>
    private bool _railingArcCloses;

    private Point2D? _railingHover;
    private (Slab Slab, int Edge)? _railEdgeHover;

    private void RailingHover(Point2D at)
    {
        _railingHover = at;
        _railEdgeHover = PickingEdges ? EdgeAt(at) : null;
        InvalidateVisual();
    }

    /// <summary>The edge of a floor, balcony or flat roof on this level nearest a point, near enough the cursor: the slab, and which of its edges.</summary>
    private (Slab Slab, int Edge)? EdgeAt(Point2D at) => EdgesAt(at).Select(edge => ((Slab, int)?)(edge.Slab, edge.Edge)).FirstOrDefault();

    /// <summary>
    /// The edges of floors, balconies and flat roofs on this level near enough the cursor, nearest
    /// first - where two lie on one another, a balcony's against the floor it comes off, the one
    /// whose railing would join one already along the edges next to it first.
    /// </summary>
    private IReadOnlyList<(Slab Slab, int Edge)> EdgesAt(Point2D at)
    {
        if (Document?.FindLevel(ActiveLevelId) is not { } level) return Array.Empty<(Slab, int)>();
        var reach = 12 / PixelsPerMm;
        var found = new List<(Slab Slab, int Edge, double Distance, bool Joins)>();
        var ends = OnActiveLevel<Railing>().Where(railing => !railing.Closed && railing.Path.Count >= 2).SelectMany(railing => new[] { railing.Path[0], railing.Path[^1] }).ToList();
        foreach (var slab in Document.All<Slab>())
        {
            if (slab.Boundary.Count < 3 || !FloorSurfaces.IsStoodOn(Document, slab)) continue;
            if (slab.LevelId != ActiveLevelId && Math.Abs(slab.GetTopElevation(Document) - level.Elevation) > Stairs.FloorReach) continue;
            var boundary = slab.Boundary;
            for (var i = 0; i < boundary.Count; i++)
            {
                var distance = Line2D.DistanceFromSegment(at, boundary[i], boundary[(i + 1) % boundary.Count]);
                if (distance > reach) continue;
                var (from, to) = EdgeLine(slab, i);
                found.Add((slab, i, Math.Round(distance), ends.Any(end => end.DistanceTo(from) < 1 || end.DistanceTo(to) < 1)));
            }
        }

        if (found.Count > 0) return found.OrderBy(edge => edge.Distance).ThenBy(edge => edge.Joins ? 0 : 1).Select(edge => (edge.Slab, edge.Edge)).ToList();

        // Clicked on the floor, deck or terrace itself, away from its edges: the nearest of them.
        if (WalkedOnAt(at) is not { } under) return Array.Empty<(Slab, int)>();
        var outline = under.Boundary;
        var nearest = Enumerable.Range(0, outline.Count).MinBy(i => Line2D.DistanceFromSegment(at, outline[i], outline[(i + 1) % outline.Count]));
        return new[] { (under, nearest) };
    }

    /// <summary>The floor, deck or terrace walked on at this level under a point: the highest there.</summary>
    private Slab? WalkedOnAt(Point2D at)
    {
        if (Document?.FindLevel(ActiveLevelId) is not { } level) return null;
        return Document.All<Slab>()
            .Where(slab => slab.Boundary.Count >= 3 && FloorSurfaces.IsStoodOn(Document, slab) && slab.Contains(at)
                           && (slab.LevelId == ActiveLevelId || Math.Abs(slab.GetTopElevation(Document) - level.Elevation) <= Stairs.FloorReach))
            .OrderByDescending(slab => slab.GetTopElevation(Document)).ThenBy(slab => slab.Area)
            .FirstOrDefault();
    }

    /// <summary>
    /// A double-click with the Railing tool picking edges: one railing all the way round the
    /// floor, deck or terrace clicked - every side of it, set in from its edges - taking the place
    /// of those already along its edges. Public so it can be driven from tests.
    /// </summary>
    public bool PickRailingOutline(Point2D at)
    {
        if (Document is null) return false;
        var slab = EdgeAt(at)?.Slab ?? WalkedOnAt(at);
        if (slab is null)
        {
            HintChanged?.Invoke(this, "Double-click a floor, balcony or terrace on this level: a railing goes all the way round it.");
            return false;
        }

        return GuardAllRound(slab);
    }

    /// <summary>
    /// All Round: one railing round every side of a floor, deck or terrace, set in from its edges,
    /// leaving open only where a stair comes up through it - in place of those already along its
    /// edges - as one step to undo. Public so it can be driven from tests.
    /// </summary>
    public bool GuardAllRound(Slab slab)
    {
        if (Document is null || slab.Boundary.Count < 3) return false;

        var outline = Railings.SetIn(slab.Boundary, true, Railings.EdgeInset);
        // Those along its edges already, on it - not one along the same outline a storey up or down.
        var top = slab.GetTopElevation(Document);
        var replaced = Document.All<Railing>()
            .Where(railing => railing.Path.Count >= 2 && railing.Path.All(point => Enumerable.Range(0, outline.Count)
                .Any(i => Line2D.DistanceFromSegment(point, outline[i], outline[(i + 1) % outline.Count]) < 1))
                && Math.Abs(Railings.Base(Document, railing, railing.Path[0]) - railing.BaseOffset - top) < Stairs.FloorReach)
            .ToList();

        var railing = NewRailing(outline, closed: true);
        if (replaced.FirstOrDefault() is { } kept) railing.TypeId = kept.TypeId;
        else if (Document.FindType<RailingType>(ActiveRailingTypeId) is null && Railings.TypeFor(Document, Railings.Required(Document, railing).Height) is { } fits)
            railing.TypeId = fits.Id;

        var steps = replaced.Select(old => (IUndoableCommand)new DeleteElementCommand(Document, old)).Append(new AddElementCommand(Document, railing, "Railing")).ToList();
        Apply(new CompositeCommand("Railing All Round", steps));
        HintChanged?.Invoke(this, $"Railing all the way round: {Railings.Length(railing) / 1000:0.0} m, {Railings.TypeOf(Document, railing).Height:0} high{(Railings.StairGaps(Document, railing).Count > 0 ? ", open where a stair comes up" : "")}. {Railings.Verdict(Document, railing)}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The line a railing along a slab's edge runs: set in from it, its ends mitred with the edges either side.</summary>
    private static (Point2D From, Point2D To) EdgeLine(Slab slab, int edge)
    {
        var boundary = slab.Boundary;
        var count = boundary.Count;
        var line = Railings.SetIn(new[] { boundary[(edge - 1 + count) % count], boundary[edge], boundary[(edge + 1) % count], boundary[(edge + 2) % count] }, false, Railings.EdgeInset);
        return (line[1], line[2]);
    }

    /// <summary>
    /// A click with the Railing tool picking edges: a railing along the edge clicked, set in from
    /// it, its ends mitred with the edges either side - joined on to a railing already along the
    /// edge before it or after it, closed all the way round where it meets itself. One step to
    /// undo. Public so it can be driven from tests.
    /// </summary>
    public bool PickRailingEdge(Point2D at)
    {
        if (Document is null) return false;
        if (EdgeAt(at) is not { } picked)
        {
            HintChanged?.Invoke(this, "Click the edge of a floor, balcony or terrace on this level: a railing goes along it.");
            return false;
        }

        var (from, to) = EdgeLine(picked.Slab, picked.Edge);

        var railings = OnActiveLevel<Railing>().Where(railing => !railing.Closed && railing.Path.Count >= 2).ToList();
        var before = railings.FirstOrDefault(railing => railing.Path[^1].DistanceTo(from) < 1);
        var after = railings.FirstOrDefault(railing => railing.Path[0].DistanceTo(to) < 1);
        var steps = new List<IUndoableCommand>();
        Railing made;

        if (before is not null && after is not null)
        {
            var was = ElementHandles.Snapshot(before);
            if (ReferenceEquals(before, after))
            {
                // Round to where it began: all the way round.
                before.Closed = true;
            }
            else
            {
                before.SetPath(before.Path.Concat(after.Path));
                steps.Add(new DeleteElementCommand(Document, after));
            }

            var now = ElementHandles.Snapshot(before);
            ElementHandles.Restore(before, was);
            steps.Insert(0, new HandleEditCommand(before, was, now, "Railing Along Edge"));
            made = before;
        }
        else if (before is not null)
        {
            var was = ElementHandles.Snapshot(before);
            before.SetPath(before.Path.Append(to));
            var now = ElementHandles.Snapshot(before);
            ElementHandles.Restore(before, was);
            steps.Add(new HandleEditCommand(before, was, now, "Railing Along Edge"));
            made = before;
        }
        else if (after is not null)
        {
            var was = ElementHandles.Snapshot(after);
            after.SetPath(after.Path.Prepend(from));
            var now = ElementHandles.Snapshot(after);
            ElementHandles.Restore(after, was);
            steps.Add(new HandleEditCommand(after, was, now, "Railing Along Edge"));
            made = after;
        }
        else
        {
            made = NewRailing(new[] { from, to }, closed: false);
            if (Document.FindType<RailingType>(ActiveRailingTypeId) is null && Railings.TypeFor(Document, Railings.Required(Document, made).Height) is { } fits)
                made.TypeId = fits.Id;
            steps.Add(new AddElementCommand(Document, made, "Railing"));
        }

        Apply(new CompositeCommand("Railing Along Edge", steps));
        HintChanged?.Invoke(this, $"Railing along the edge: {Railings.Length(made) / 1000:0.0} m{(made.Closed ? ", all the way round" : "")}, {Railings.TypeOf(Document, made).Height:0} high. {Railings.Verdict(Document, made)}. Click the next edge; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A click with the Railing tool: the next corner along it; clicking the last again finishes
    /// it, and its first takes it all the way round. Public so it can be driven from tests.
    /// </summary>
    public bool ClickRailing(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        // An arc's end clicked: this is a point it bows through - straight, where it is on either end.
        if (_railingArcEnd is { } end && _railingPoints.Count > 0)
        {
            var start = _railingPoints[^1];
            var bulge = (at - end).Length * PixelsPerMm < 8 || (at - start).Length * PixelsPerMm < 8 ? 0 : WallCurve.BulgeThrough(start, end, at);
            _railingArcEnd = null;
            if (_railingArcCloses)
            {
                _railingArcCloses = false;
                return FinishRailing(closed: true, closingBulge: bulge);
            }

            _railingBulges.Add(bulge);
            _railingPoints.Add(end);
            HintChanged?.Invoke(this, "Click its next corner - or, as an arc, its end and then a point it bows through - double-click where it ends, or click its first point to close it.");
            InvalidateVisual();
            return true;
        }

        // Drawing arcs: this is the end of one - its first point, closing it round - a point it bows through to come.
        if (RailingArc && _railingPoints.Count > 0 && (at - _railingPoints[^1]).Length * PixelsPerMm >= 8)
        {
            _railingArcCloses = _railingPoints.Count >= 2 && (at - _railingPoints[0]).Length * PixelsPerMm < 8;
            _railingArcEnd = _railingArcCloses ? _railingPoints[0] : at;
            HintChanged?.Invoke(this, "Now click a point the arc bows through.");
            InvalidateVisual();
            return true;
        }

        if (_railingPoints.Count >= 3 && (at - _railingPoints[0]).Length * PixelsPerMm < 8) return FinishRailing(closed: true);
        if (_railingPoints.Count > 0 && (at - _railingPoints[^1]).Length * PixelsPerMm < 8) return FinishRailing();

        if (_railingPoints.Count > 0) _railingBulges.Add(0);
        _railingPoints.Add(at);
        HintChanged?.Invoke(this, _railingPoints.Count == 1
            ? "Now click along where it runs: each corner, then double-click where it ends - or click its first point to take it all the way round."
            : "Click its next corner, double-click where it ends, or click its first point to close it. Esc ends it at the last corner.");
        InvalidateVisual();
        return true;
    }

    /// <summary>The railing clicked along, put up: open, or all the way round. False with less than two points.</summary>
    public bool FinishRailing(bool closed = false, double closingBulge = 0)
    {
        // An arc's end clicked with no point it bows through yet: straight to it.
        if (_railingArcEnd is { } pending && !_railingArcCloses)
        {
            _railingBulges.Add(0);
            _railingPoints.Add(pending);
        }

        _railingArcEnd = null;
        _railingArcCloses = false;
        if (Document is null || _railingPoints.Count < 2) return false;

        var points = _railingPoints.ToList();
        var bulges = _railingBulges.ToList();
        while (bulges.Count < points.Count - 1) bulges.Add(0);

        // Closed round by an arc from its second point back to its first: that arc in two, so it has three corners.
        if (closed && points.Count == 2 && WallCurve.Of(points[1], points[0], closingBulge) is { IsArc: true } back)
        {
            var half = Math.Tan(Math.Atan(closingBulge) / 2);
            points.Add(back.PointAt(back.Length / 2));
            bulges.Add(half);
            closingBulge = half;
        }

        bulges.Add(closed ? closingBulge : 0);
        var railing = NewRailing(points, closed, bulges);
        _railingPoints.Clear();
        _railingBulges.Clear();
        if (Railings.Length(railing) < 100)
        {
            HintChanged?.Invoke(this, "A railing runs between two points at least 100 apart.");
            InvalidateVisual();
            return false;
        }

        Apply(new AddElementCommand(Document, railing, "Railing"));
        var type = Railings.TypeOf(Document, railing);
        HintChanged?.Invoke(this, $"Railing in: {Railings.Length(railing) / 1000:0.0} m, {type.Height:0} high{(railing.Outside ? ", outside" : "")}. {Railings.Verdict(Document, railing)}. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>A railing along points, in the type and building the tool has, on this level - outside where it stands outside.</summary>
    private Railing NewRailing(IReadOnlyList<Point2D> points, bool closed, IReadOnlyList<double>? bulges = null)
    {
        // The type picked, or guarding - not a fence - as high as a balcony asks.
        var type = Document?.FindType<RailingType>(ActiveRailingTypeId)
                   ?? (Document is null ? null : Railings.TypeFor(Document, 1100))
                   ?? Document?.TypesOf<RailingType>().FirstOrDefault();
        var railing = new Railing { LevelId = ActiveLevelId, TypeId = type?.Id ?? Guid.Empty, Closed = closed && points.Count >= 3, Building = ActiveRailingBuilding };
        railing.SetPath(points, bulges ?? Array.Empty<double>());

        // Outside or not as it runs, its arcs and all.
        if (Document is not null) railing.Outside = Railings.StandsOutside(Document, railing);
        return railing;
    }

    /// <summary>Esc with a railing begun: two corners or more put up as far as it goes, as walls drawn so far are kept; a first point alone forgotten.</summary>
    private bool CancelRailing()
    {
        if (_railingPoints.Count == 0) return false;
        if (_railingPoints.Count >= 2 && _railingArcEnd is null && FinishRailing()) return true;
        _railingPoints.Clear();
        _railingBulges.Clear();
        _railingArcEnd = null;
        _railingArcCloses = false;
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Guard Edges: railings along every edge of a floor, balcony or roof terrace that drops too
    /// far with nothing there, each as high as its rules ask - put up as one step to undo - and
    /// what was done, said. Public so it can be driven from tests.
    /// </summary>
    public bool GuardEdges(Slab slab)
    {
        if (Document is null) return false;

        var made = Railings.GuardEdges(Document, slab, ActiveRailingBuilding, null, out var message);
        HintChanged?.Invoke(this, message);
        if (made.Count == 0) return false;

        Apply(new CompositeCommand("Guard Edges", made.Select(railing => (IUndoableCommand)new AddElementCommand(Document, railing, "Railing")).ToList()));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The railing being drawn, on to the cursor - or, picking edges, the edge a click would rail.</summary>
    private void DrawRailingPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Railing) return;

        if (PickingEdges)
        {
            if (_railEdgeHover is { } edge)
            {
                var boundary = edge.Slab.Boundary;
                var pen = new Pen(SketchPreviewPen.Brush, 4);
                dc.DrawLine(pen, ModelToScreen(boundary[edge.Edge]), ModelToScreen(boundary[(edge.Edge + 1) % boundary.Count]));
            }

            return;
        }

        // Its lengths so far, each as it bows, and on to the cursor: an arc through it, once its end is clicked.
        void Draw(Point2D a, Point2D b, double bulge)
        {
            var points = WallCurve.Of(a, b, bulge).Points();
            foreach (var (from, to) in points.Zip(points.Skip(1))) dc.DrawLine(SketchPreviewPen, ModelToScreen(from), ModelToScreen(to));
        }

        for (var i = 0; i + 1 < _railingPoints.Count; i++) Draw(_railingPoints[i], _railingPoints[i + 1], i < _railingBulges.Count ? _railingBulges[i] : 0);
        if (_railingPoints.Count > 0 && _railingHover is { } next)
        {
            if (_railingArcEnd is { } end) Draw(_railingPoints[^1], end, WallCurve.BulgeThrough(_railingPoints[^1], end, next));
            else Draw(_railingPoints[^1], next, 0);
        }

        // Where a click would close it.
        if (_railingPoints.Count >= 3)
            dc.DrawEllipse(null, SketchPreviewPen, ModelToScreen(_railingPoints[0]), 6, 6);
    }

    /// <summary>The railings under a point of this plan: on its level, near enough the line they run along.</summary>
    private IEnumerable<Railing> RailingsAt(Point2D model)
    {
        if (Document is null) yield break;

        foreach (var railing in OnActiveLevel<Railing>())
        {
            var reach = Math.Max(Railings.TypeOf(Document, railing).TopRailWidth / 2 + 20, 5 / PixelsPerMm);
            if (Railings.Lengths(railing).Any(length => Line2D.DistanceFromSegment(model, length.From, length.To) <= reach)) yield return railing;
        }
    }
}
