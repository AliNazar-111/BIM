using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>What is run along a roof's edges.</summary>
public enum RoofEdgeKind
{
    Fascia,
    Gutter,
    Soffit
}

/// <summary>
/// Fascias, gutters and soffits on the plan: Revit's Roof: Fascia, Roof: Gutter and Roof:
/// Soffit. Click a roof's edges one after another and one fascia runs along them all, mitred
/// round the corners between; Esc finishes it, and the next click starts another. Or, with a
/// roof selected, put one all round it - gutters along all its eaves, soffits under them - at once.
/// </summary>
public partial class PlanView
{
    /// <summary>The fascia type the Fascia tool runs along the edges picked; none, to pick the one that stands out on each roof.</summary>
    public Guid ActiveFasciaTypeId { get; set; }

    /// <summary>The gutter type the Gutter tool hangs along the edges picked.</summary>
    public Guid ActiveGutterTypeId { get; set; }

    /// <summary>The soffit type the Soffit tool puts under the eaves picked.</summary>
    public Guid ActiveSoffitTypeId { get; set; }

    // The roof edge under the cursor, and the fascia, gutter or soffit the picks are adding to.
    private (Roof Roof, int Edge)? _edgeHover;
    private RoofEdgeSweep? _edgeSweep;

    private static readonly Pen EdgePickPen = FrozenPen(Color.FromRgb(0x2B, 0x8A, 0xE6), 3.2);
    private static readonly Pen EdgeTakenPen = FrozenPen(Color.FromRgb(0x2E, 0xA0, 0x5A), 3.2);

    private static Pen FrozenPen(Color colour, double thickness)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static RoofEdgeKind? KindOf(PlanTool tool) => tool switch
    {
        PlanTool.Fascia => RoofEdgeKind.Fascia,
        PlanTool.Gutter => RoofEdgeKind.Gutter,
        PlanTool.Soffit => RoofEdgeKind.Soffit,
        _ => null
    };

    private static RoofEdgeKind KindOf(RoofEdgeSweep sweep) => sweep switch
    {
        Gutter => RoofEdgeKind.Gutter,
        Soffit => RoofEdgeKind.Soffit,
        _ => RoofEdgeKind.Fascia
    };

    private static string Noun(RoofEdgeKind kind) => kind.ToString().ToLowerInvariant();

    private void RoofEdgeHover(Point2D raw)
    {
        var edge = RoofEdgeAt(raw);
        if (Nullable.Equals(edge, _edgeHover)) return;

        _edgeHover = edge;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Fascia, Gutter or Soffit tool: the roof edge under it added to the one
    /// being picked, or taken off it if it is already on it - or, on another roof, the start of a
    /// new one. Public so it can be driven from tests.
    /// </summary>
    public bool PickRoofEdgeAt(Point2D raw)
    {
        if (Document is null || KindOf(ActiveTool) is not { } kind) return false;

        var what = Noun(kind);
        if (RoofEdgeAt(raw) is not var (roof, index))
        {
            HintChanged?.Invoke(this, kind == RoofEdgeKind.Soffit
                ? "Click the eave of a roof - the soffit closes the overhang under it."
                : $"Click the edge of a roof - the {what} runs along it.");
            return false;
        }

        var edge = roof.Edges[index];
        if (kind == RoofEdgeKind.Soffit && (!edge.DefinesSlope || edge.Overhang <= 1))
        {
            HintChanged?.Invoke(this, "A soffit goes under an eave that overhangs its wall. That edge is a verge, or has no overhang to close.");
            return false;
        }

        var name = kind.ToString();

        // Carrying on along the same roof: this edge onto the one being picked, or off it.
        if (_edgeSweep is { } current && current.RoofId == roof.Id && KindOf(current) == kind && Document.Elements.Contains(current))
        {
            var edges = current.EdgeIds.Contains(edge.Id)
                ? current.EdgeIds.Where(id => id != edge.Id).ToList()
                : current.EdgeIds.Append(edge.Id).ToList();

            if (edges.Count == 0)
            {
                Apply(new DeleteElementsCommand(Document, new Element[] { current }));
                _edgeSweep = null;
            }
            else Apply(new SetRoofEdgeSweepEdgesCommand(current, edges, name));

            HintChanged?.Invoke(this, $"The {what} runs along {Plural(edges.Count, "edge")}. Click more edges of this roof, or Esc to finish it.");
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return true;
        }

        var sweep = NewRoofEdgeSweep(roof, kind);
        sweep.EdgeIds.Add(edge.Id);
        Apply(new AddElementCommand(Document, sweep, name));
        _edgeSweep = sweep;

        HintChanged?.Invoke(this, $"A {what} along that edge. Click the next edges of the roof to carry it round them, or Esc to finish.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A new fascia, gutter or soffit on a roof, of the type the tool has. A gutter hangs off the
    /// face of the roof's fascia, if it has one, and a little below its top edge.
    /// </summary>
    private RoofEdgeSweep NewRoofEdgeSweep(Roof roof, RoofEdgeKind kind)
    {
        Guid TypeOr<T>(Guid active) where T : ElementType =>
            active != Guid.Empty ? active : Document!.TypesOf<T>().FirstOrDefault()?.Id ?? Guid.Empty;

        switch (kind)
        {
            case RoofEdgeKind.Soffit:
                return new Soffit { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = TypeOr<SoffitType>(ActiveSoffitTypeId) };

            case RoofEdgeKind.Gutter:
                var fasciaThickness = RoofEdgeSweeps.Of(Document!, roof).OfType<Fascia>()
                    .Select(fascia => Document!.FindType<FasciaType>(fascia.TypeId)?.Thickness ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();

                return new Gutter
                {
                    RoofId = roof.Id, LevelId = roof.LevelId, TypeId = TypeOr<GutterType>(ActiveGutterTypeId),
                    HorizontalOffset = fasciaThickness,
                    VerticalOffset = -40
                };

            default:
                // Left to itself, the one that stands out against these walls and this roof.
                var fasciaType = ActiveFasciaTypeId != Guid.Empty
                    ? ActiveFasciaTypeId
                    : RoofEdgeSweeps.ContrastingFascia(Document!, roof)?.Id ?? Guid.Empty;
                return new Fascia { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = fasciaType };
        }
    }

    /// <summary>A fascia all round a roof, or gutters along all its eaves. See the other overload.</summary>
    public RoofEdgeSweep? AddAllRound(Roof roof, bool gutter) => AddAllRound(roof, gutter ? RoofEdgeKind.Gutter : RoofEdgeKind.Fascia);

    /// <summary>
    /// A fascia all round a roof, gutters along all its eaves, or soffits under them, in one step -
    /// on the edges it has not got one on already. Returns what was added, or null with nothing to add.
    /// </summary>
    public RoofEdgeSweep? AddAllRound(Roof roof, RoofEdgeKind kind)
    {
        if (Document is null || roof.IsExtrusion) return null;

        var wanted = kind switch
        {
            RoofEdgeKind.Gutter => RoofEdgeSweeps.GutterEdges(Document, roof),
            RoofEdgeKind.Soffit => RoofEdgeSweeps.SoffitEdges(Document, roof),
            _ => RoofEdgeSweeps.FasciaEdges(Document, roof)
        };
        var taken = RoofEdgeSweeps.Of(Document, roof).Where(existing => KindOf(existing) == kind).SelectMany(existing => existing.EdgeIds).ToHashSet();
        var edges = wanted.Where(id => !taken.Contains(id)).ToList();
        if (edges.Count == 0) return null;

        var sweep = NewRoofEdgeSweep(roof, kind);
        sweep.EdgeIds.AddRange(edges);
        Apply(new AddElementCommand(Document, sweep, kind.ToString()));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return sweep;
    }

    /// <summary>Esc with the Fascia, Gutter or Soffit tool: the one being picked is finished, and the next click starts another.</summary>
    private bool CancelRoofEdgePick()
    {
        var pending = _edgeSweep is not null;
        _edgeSweep = null;
        return pending;
    }

    /// <summary>The edge a click would pick, and the edges already picked.</summary>
    private void DrawRoofEdgePick(DrawingContext dc)
    {
        if (Document is null || KindOf(ActiveTool) is null) return;

        if (_edgeSweep is { } current && Document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == current.RoofId) is { } picked)
        {
            var count = picked.Boundary.Count;
            for (var i = 0; i < count && i < picked.Edges.Count; i++)
                if (current.EdgeIds.Contains(picked.Edges[i].Id))
                    dc.DrawLine(EdgeTakenPen, ModelToScreen(picked.Boundary[i]), ModelToScreen(picked.Boundary[(i + 1) % count]));
        }

        if (_edgeHover is var (roof, edge) && edge < roof.Boundary.Count)
            dc.DrawLine(EdgePickPen, ModelToScreen(roof.Boundary[edge]), ModelToScreen(roof.Boundary[(edge + 1) % roof.Boundary.Count]));
    }
}
