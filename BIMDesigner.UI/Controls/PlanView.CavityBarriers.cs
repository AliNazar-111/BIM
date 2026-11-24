using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Cavity barriers on the plan (round 127): click where one starts and where it ends - a line in the void over the ceilings or in the roof
/// space, closing it up to the floor or roof over it - and the next starts where it ended, a chain of them, until Esc.
/// </summary>
public partial class PlanView
{
    private Point2D? _barrierStart;
    private Point2D? _barrierHover;

    private void CavityBarrierHover(Point2D at)
    {
        _barrierHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Cavity Barrier tool: where a barrier starts, then where it ends - and the next starts there. Public so it can be driven
    /// from tests.
    /// </summary>
    public bool ClickCavityBarrier(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        if (_barrierStart is not { } start)
        {
            _barrierStart = at;
            HintChanged?.Invoke(this, "Now click where it ends: the next starts there. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 10) return false;

        var barrier = new CavityBarrier { LevelId = ActiveLevelId, Start = start, End = at };
        Apply(new AddElementCommand(Document, barrier, "Cavity Barrier"));
        _barrierStart = at;
        var span = Cavities.Span(Document, barrier) is var (bottom, top) ? $", {top - bottom:0} high up to what is over it" : ", with nothing over it to close the cavity up to";
        HintChanged?.Invoke(this, $"Cavity barrier in, {Core.Units.Metres(barrier.Length, "0.00")}{span}. Click where the next ends; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Esc with a cavity barrier begun: its start forgotten.</summary>
    private bool CancelCavityBarrier()
    {
        if (_barrierStart is null) return false;
        _barrierStart = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>The cavity barrier the next click would put in, from where it starts to the cursor.</summary>
    private void DrawCavityBarrierPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.CavityBarrier || _barrierStart is not { } start || _barrierHover is not { } hover || (hover - start).Length < 1) return;
        dc.DrawLine(SketchPreviewPen, ModelToScreen(start), ModelToScreen(hover));
    }

    /// <summary>The cavity barriers on this level under a point.</summary>
    private IEnumerable<CavityBarrier> CavityBarriersAt(Point2D model) =>
        OnActiveLevel<CavityBarrier>().Where(barrier => Line2D.DistanceFromSegment(model, barrier.Start, barrier.End) <= 5 / PixelsPerMm);
}
