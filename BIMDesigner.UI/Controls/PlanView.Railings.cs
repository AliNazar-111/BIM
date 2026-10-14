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

    /// <summary>The railing being drawn, its corners so far.</summary>
    private readonly List<Point2D> _railingPoints = new();

    private Point2D? _railingHover;

    private void RailingHover(Point2D at)
    {
        _railingHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Railing tool: the next corner along it; clicking the last again finishes
    /// it, and its first takes it all the way round. Public so it can be driven from tests.
    /// </summary>
    public bool ClickRailing(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        if (_railingPoints.Count >= 3 && (at - _railingPoints[0]).Length * PixelsPerMm < 8) return FinishRailing(closed: true);
        if (_railingPoints.Count > 0 && (at - _railingPoints[^1]).Length * PixelsPerMm < 8) return FinishRailing();

        _railingPoints.Add(at);
        HintChanged?.Invoke(this, _railingPoints.Count == 1
            ? "Now click along where it runs: each corner, then double-click where it ends - or click its first point to take it all the way round."
            : "Click its next corner, double-click where it ends, or click its first point to close it. Esc to stop.");
        InvalidateVisual();
        return true;
    }

    /// <summary>The railing clicked along, put up: open, or all the way round. False with less than two points.</summary>
    public bool FinishRailing(bool closed = false)
    {
        if (Document is null || _railingPoints.Count < 2) return false;

        var railing = NewRailing(_railingPoints, closed);
        _railingPoints.Clear();
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
    private Railing NewRailing(IReadOnlyList<Point2D> points, bool closed)
    {
        var type = Document?.FindType<RailingType>(ActiveRailingTypeId) ?? Document?.TypesOf<RailingType>().FirstOrDefault();
        var railing = new Railing { LevelId = ActiveLevelId, TypeId = type?.Id ?? Guid.Empty, Closed = closed && points.Count >= 3, Building = ActiveRailingBuilding };
        railing.SetPath(points);
        if (Document is not null) railing.Outside = Railings.StandsOutside(Document, railing);
        return railing;
    }

    /// <summary>Esc with a railing begun: the corners clicked so far, forgotten.</summary>
    private bool CancelRailing()
    {
        if (_railingPoints.Count == 0) return false;
        _railingPoints.Clear();
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

    /// <summary>The railing being drawn, on to the cursor.</summary>
    private void DrawRailingPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Railing) return;

        var points = _railingHover is { } next ? _railingPoints.Append(next).ToList() : _railingPoints.ToList();
        foreach (var (a, b) in points.Zip(points.Skip(1))) dc.DrawLine(SketchPreviewPen, ModelToScreen(a), ModelToScreen(b));

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
