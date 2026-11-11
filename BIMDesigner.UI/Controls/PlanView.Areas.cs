using System.Windows.Media;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Splitting an area plan by hand: area boundaries - click where one starts and where it ends, the
/// next starting there, as detail lines are drawn - and areas, placed with a click in the space the
/// boundaries and the storey's outline close round, numbered on from the last. Both belong to the
/// area plan they are drawn on, and are drawn and picked there only.
/// </summary>
public partial class PlanView
{
    private Point2D? _areaBoundaryStart;
    private Point2D? _areaBoundaryHover;

    /// <summary>What the next area placed on a rentable area plan is used for.</summary>
    public AreaUse ActiveAreaUse { get; set; } = AreaUse.Office;

    /// <summary>Whether the Area Boundary tool puts a boundary along the middle of each wall clicked, rather than drawing one.</summary>
    public bool AreaBoundariesOnWalls { get; set; }

    /// <summary>The area boundaries on the area plan open, if one is.</summary>
    private IEnumerable<AreaBoundary> AreaBoundariesShown =>
        Document is not null && ActiveAreaScheme is { } scheme && !Drafting ? Areas.Boundaries(Document, ActiveLevelId, scheme) : Enumerable.Empty<AreaBoundary>();

    /// <summary>The areas on the area plan open, if one is.</summary>
    private IEnumerable<Area> AreasShown =>
        Document is not null && ActiveAreaScheme is { } scheme && !Drafting ? Areas.On(Document, ActiveLevelId, scheme) : Enumerable.Empty<Area>();

    private const string OpenAnAreaPlan = "Areas are drawn on an area plan: open one first - Annotate, Area Plan.";

    private void AreaBoundaryHover(Point2D at)
    {
        _areaBoundaryHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Area Boundary tool: where a boundary starts, then where it ends - and the next
    /// starts there. Public so it can be driven from tests.
    /// </summary>
    public bool ClickAreaBoundary(Point2D at)
    {
        if (Document is null) return false;
        if (ActiveAreaScheme is not { } scheme)
        {
            HintChanged?.Invoke(this, OpenAnAreaPlan);
            return false;
        }

        // Picking walls: a boundary along the middle of the wall clicked, as a wall between two
        // occupants is measured (BOMA 2017; IPMS 3.2's demising walls).
        if (AreaBoundariesOnWalls)
        {
            if (HitTestWall(at) is not { } wall || wall.IsCurved || Document.GetWallType(wall) is not { } type)
            {
                HintChanged?.Invoke(this, "Click a straight wall: a boundary goes along its middle.");
                return false;
            }

            var (from, to) = wall.GetBodyCentreline(type.Structure);
            if (AreaBoundariesShown.Any(line => (line.Start.DistanceTo(from) < 1 && line.End.DistanceTo(to) < 1) || (line.Start.DistanceTo(to) < 1 && line.End.DistanceTo(from) < 1)))
            {
                HintChanged?.Invoke(this, "That wall has a boundary along it already.");
                return false;
            }

            var picked = new AreaBoundary { LevelId = ActiveLevelId, Scheme = scheme, Start = from, End = to, WallId = wall.Id };
            Apply(new AddElementCommand(Document, picked, "Area Boundary"));
            HintChanged?.Invoke(this, $"Area boundary along the wall's middle, {Core.Units.FormatLength(picked.Length)}. Click the next wall; untick Pick Walls to draw one.");
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return true;
        }

        if (_areaBoundaryStart is not { } start)
        {
            _areaBoundaryStart = at;
            HintChanged?.Invoke(this, "Now click where it ends, or type its length: the next starts there. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 0.5) return false;

        var line = new AreaBoundary { LevelId = ActiveLevelId, Scheme = scheme, Start = start, End = at };
        Apply(new AddElementCommand(Document, line, "Area Boundary"));
        _areaBoundaryStart = at;
        HintChanged?.Invoke(this, $"Area boundary, {Core.Units.FormatLength(line.Length)}. Click where the next ends; Esc to stop - then place an area in each space it closes.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private bool CancelAreaBoundary()
    {
        if (_areaBoundaryStart is null) return false;
        _areaBoundaryStart = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A click with the Area tool: an area placed there, named and numbered on from the last - on a
    /// rentable plan put to the use on the option bar - and measured as the space it is in. Public so
    /// it can be driven from tests.
    /// </summary>
    public Area? PlaceAreaAt(Point2D at)
    {
        if (Document is null) return null;
        if (ActiveAreaScheme is not { } scheme)
        {
            HintChanged?.Invoke(this, OpenAnAreaPlan);
            return null;
        }

        var number = Areas.NextNumber(Document, ActiveLevelId, scheme);
        var use = scheme == AreaScheme.Rentable ? ActiveAreaUse : AreaUse.None;
        var area = new Area
        {
            LevelId = ActiveLevelId, Scheme = scheme, Location = at, Number = number, Use = use,
            Name = use == AreaUse.None ? $"Area {number}" : $"{Areas.Name(use)} {number}"
        };
        Apply(new AddElementCommand(Document, area, "Area"));

        var shape = Areas.Shape(Document, area);
        HintChanged?.Invoke(this, shape.Polygon is null || shape.Shared is not null
            ? $"{Areas.Said(area)}: {shape.Why ?? shape.Shared}"
            : $"{Areas.Said(area)}: {AreaPlans.Said(scheme, shape.Area)}. Click in the next space; name it in Properties.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return area;
    }

    /// <summary>The area boundary the next click would draw, from where it starts to the cursor.</summary>
    private void DrawAreaBoundaryPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.AreaBoundary || _areaBoundaryStart is not { } start || _areaBoundaryHover is not { } hover || (hover - start).Length < 0.5) return;
        dc.DrawLine(SketchPreviewPen, ModelToScreen(start), ModelToScreen(hover));
    }

    /// <summary>The area boundaries and areas under a point on the area plan open: a boundary by its line, an area by its label.</summary>
    private IEnumerable<Element> AreasAt(Point2D model)
    {
        foreach (var line in AreaBoundariesShown)
            if (DistanceToSegment(model, line.Start, line.End) <= 6 / PixelsPerMm) yield return line;
        foreach (var area in AreasShown)
            if (area.Location.DistanceTo(model) <= 18 / PixelsPerMm) yield return area;
    }
}
