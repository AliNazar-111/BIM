using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Roof drains on the plan: click on a flat roof and an outlet goes there; the roof's insulation
/// is tapered to fall to it at the roof's fall, and the falls are drawn - valleys and ridges,
/// and an arrow down each face.
/// </summary>
public partial class PlanView
{
    private Point2D? _drainHover;

    private void DrainHover(Point2D at)
    {
        _drainHover = at;
        InvalidateVisual();
    }

    /// <summary>The flat roof on this plan at a point, the highest one there.</summary>
    private Roof? FlatRoofAt(Point2D at) =>
        Document is null
            ? null
            : OnActiveLevel<Roof>()
                .Where(roof => roof.Contains(at) && RoofDrainage.CanDrain(Document, roof))
                .OrderByDescending(roof => roof.TopAt(Document, at))
                .FirstOrDefault();

    /// <summary>A click with the Roof Drain tool: a drain in the flat roof there. Public so it can be driven from tests.</summary>
    public bool PlaceRoofDrainAt(Point2D at)
    {
        if (Document is null) return false;

        if (FlatRoofAt(at) is not { } roof)
        {
            HintChanged?.Invoke(this, "Click on a flat roof: the drain goes in it there, and the roof falls to it.");
            return false;
        }

        return PlaceRoofDrain(roof, at);
    }

    /// <summary>A click on a flat roof in the 3D view with the Roof Drain tool: a drain there.</summary>
    public bool PlaceRoofDrainIn3D(Guid roofId, Point3D at) =>
        Document?.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == roofId) is { } roof && PlaceRoofDrain(roof, new Point2D(at.X, at.Y));

    private bool PlaceRoofDrain(Roof roof, Point2D at)
    {
        if (Document is null) return false;

        if (RoofDrainage.Problem(Document, roof, at) is { } problem)
        {
            HintChanged?.Invoke(this, problem);
            return false;
        }

        // Near the edge its water goes out through it to a downpipe; further in, down inside.
        var outside = RoofDrainage.EdgeDistance(roof, at) <= RoofDrainage.EdgeOutletReach;
        var drain = new RoofDrain
        {
            RoofId = roof.Id, LevelId = roof.LevelId, Location = at,
            Discharge = outside ? DrainDischarge.ThroughEdge : DrainDischarge.Internal
        };
        Apply(new AddElementCommand(Document, drain, "Roof Drain"));

        HintChanged?.Invoke(this, (outside
            ? "Drain by the edge: its water goes out through the edge into a hopper head and down a downpipe to the ground. "
            : "Drain in the roof: its water goes down a rainwater pipe inside the building to the drain under the ground floor. ") +
            $"The roof falls to it at 1 in {roof.DrainageFall:0}. Change where the water goes in Properties. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Where the drain would go, following the cursor: in red where it cannot.</summary>
    private void DrawRoofDrainPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.RoofDrain || _drainHover is not { } hover || Document is null) return;
        if (FlatRoofAt(hover) is not { } roof) return;

        var pen = RoofDrainage.Problem(Document, roof, hover) is null ? SketchPreviewPen : DormerMissPen;
        var centre = ModelToScreen(hover);
        var radius = 110 * PixelsPerMm;
        dc.DrawEllipse(null, pen, centre, radius, radius);
        dc.DrawEllipse(null, pen, centre, radius / 2, radius / 2);
    }

    /// <summary>The roof drains under a point of this plan.</summary>
    private IEnumerable<RoofDrain> RoofDrainsAt(Point2D model) =>
        OnActiveLevel<RoofDrain>().Where(drain => drain.Location.DistanceTo(model) <= drain.OutletDiameter / 2 + 60 + 4 / PixelsPerMm);
}
