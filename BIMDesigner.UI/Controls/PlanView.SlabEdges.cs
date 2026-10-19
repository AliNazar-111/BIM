using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Slab edges on the plan: Revit's Floor: Slab Edge. Click a floor's edges one after another and
/// one slab edge runs along them all, mitred round the corners between; click one again to take
/// it off. Esc finishes it, and the next click starts another.
/// </summary>
public partial class PlanView
{
    /// <summary>The slab edge type the Slab Edge tool runs along the edges picked.</summary>
    public Guid ActiveSlabEdgeTypeId { get; set; }

    private (Floor Floor, int Edge)? _floorEdgeHover;
    private SlabEdge? _slabEdgePicking;

    /// <summary>The floor edge on this storey nearest the cursor, within a few pixels.</summary>
    private (Floor Floor, int Edge)? FloorEdgeAt(Point2D raw)
    {
        var reach = 10 / PixelsPerMm;
        (Floor, int)? best = null;
        foreach (var floor in OnActiveLevel<Floor>())
        {
            var boundary = floor.Boundary;
            for (var i = 0; i < boundary.Count; i++)
            {
                var distance = Line2D.DistanceFromSegment(raw, boundary[i], boundary[(i + 1) % boundary.Count]);
                if (distance >= reach) continue;
                reach = distance;
                best = (floor, i);
            }
        }

        return best;
    }

    private void SlabEdgeHover(Point2D raw)
    {
        var edge = FloorEdgeAt(raw);
        if (Nullable.Equals(edge, _floorEdgeHover)) return;
        _floorEdgeHover = edge;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Slab Edge tool: the floor edge under it added to the slab edge being
    /// picked, or taken off it - or, on another floor, the start of a new one. Public so it can be
    /// driven from tests.
    /// </summary>
    public bool PickSlabEdgeAt(Point2D raw)
    {
        if (Document is null) return false;
        if (FloorEdgeAt(raw) is not var (floor, index))
        {
            HintChanged?.Invoke(this, "Click the edge of a floor - the slab edge runs along it.");
            return false;
        }

        var boundary = floor.Boundary;
        var picked = (boundary[index], boundary[(index + 1) % boundary.Count]);

        // Carrying on along the same floor: this edge onto the one being picked, or off it.
        if (_slabEdgePicking is { } current && current.FloorId == floor.Id && Document.Elements.Contains(current))
        {
            var edges = current.Edges.Where(pair => SlabEdgeRuns.EdgeIndex(floor, pair.From, pair.To) >= 0).ToList();
            var taken = edges.FindIndex(pair => SlabEdgeRuns.EdgeIndex(floor, pair.From, pair.To) == index);
            if (taken >= 0) edges.RemoveAt(taken);
            else edges.Add(picked);

            if (edges.Count == 0)
            {
                Apply(new DeleteElementsCommand(Document, new Element[] { current }));
                _slabEdgePicking = null;
            }
            else Apply(new SetSlabEdgeEdgesCommand(current, edges));

            HintChanged?.Invoke(this, $"The slab edge runs along {Plural(edges.Count, "edge")}. Click more edges of this floor, or Esc to finish it.");
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return true;
        }

        var typeId = ActiveSlabEdgeTypeId != Guid.Empty ? ActiveSlabEdgeTypeId : Document.TypesOf<SlabEdgeType>().FirstOrDefault()?.Id ?? Guid.Empty;
        var slabEdge = new SlabEdge { FloorId = floor.Id, LevelId = floor.LevelId, TypeId = typeId };
        slabEdge.Edges.Add(picked);
        Apply(new AddElementCommand(Document, slabEdge, "Slab Edge"));
        _slabEdgePicking = slabEdge;

        HintChanged?.Invoke(this, "A slab edge along that edge. Click the next edges of the floor to carry it round them, or Esc to finish.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Esc with the Slab Edge tool: the one being picked is finished, and the next click starts another.</summary>
    private bool CancelSlabEdgePick()
    {
        var pending = _slabEdgePicking is not null;
        _slabEdgePicking = null;
        return pending;
    }

    /// <summary>The slab edges under a point in plan: within their strip, or just beside it.</summary>
    private IEnumerable<SlabEdge> SlabEdgesAt(Point2D model)
    {
        if (Document is null) yield break;
        var reach = 5 / PixelsPerMm;
        foreach (var slabEdge in OnActiveLevel<SlabEdge>())
        {
            var outlines = SlabEdgeRuns.Outlines(Document, slabEdge);
            var inside = outlines.Count(outline => outline.Count >= 3 && Polygon2D.Contains(outline, model)) % 2 == 1;
            var near = outlines.Any(outline => outline.Count >= 2 &&
                Enumerable.Range(0, outline.Count).Min(i => Line2D.DistanceFromSegment(model, outline[i], outline[(i + 1) % outline.Count])) <= reach);
            if (inside || near) yield return slabEdge;
        }
    }

    /// <summary>The edge a click would pick, and the edges already picked.</summary>
    private void DrawSlabEdgePick(DrawingContext dc)
    {
        if (Document is null || ActiveTool != PlanTool.SlabEdge) return;

        if (_slabEdgePicking is { } current && Document.Elements.OfType<Floor>().FirstOrDefault(floor => floor.Id == current.FloorId) is { } picked)
            foreach (var (from, to) in current.Edges)
                dc.DrawLine(EdgeTakenPen, ModelToScreen(from), ModelToScreen(to));

        if (_floorEdgeHover is var (floor, edge) && edge < floor.Boundary.Count)
            dc.DrawLine(EdgePickPen, ModelToScreen(floor.Boundary[edge]), ModelToScreen(floor.Boundary[(edge + 1) % floor.Boundary.Count]));
    }
}
