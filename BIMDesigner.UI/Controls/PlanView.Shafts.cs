using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Shaft openings on the plan: click where the middle goes and a hole is cut straight down
/// through the roofs, floors and ceilings there - Revit's Shaft Opening. Its shape and size
/// are on the option bar, and its outline follows the cursor until the click.
/// </summary>
public partial class PlanView
{
    /// <summary>The shape the Shaft tool cuts.</summary>
    public ShaftShape ActiveShaftShape { get; set; } = ShaftShape.Rectangle;

    /// <summary>How wide the Shaft tool cuts, mm; a round one's diameter.</summary>
    public double ActiveShaftWidth { get; set; } = 600;

    /// <summary>How deep the Shaft tool cuts, mm.</summary>
    public double ActiveShaftDepth { get; set; } = 600;

    private Point2D? _shaftHover;

    private void ShaftHover(Point2D at)
    {
        _shaftHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Shaft tool: a shaft with its middle there, through the roof on this
    /// level if there is one over it, otherwise up through the floor of the level above.
    /// Public so it can be driven from tests.
    /// </summary>
    public bool PlaceShaftAt(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        return PlaceShaft(Shafts.Placed(Document, ActiveLevelId, at, ActiveShaftShape, ActiveShaftWidth, ActiveShaftDepth));
    }

    /// <summary>A click on a roof, floor or ceiling in 3D with the Shaft tool: a hole through it there.</summary>
    public bool PlaceShaftIn3D(Guid slabId, Point3D at)
    {
        if (Document?.Elements.OfType<Slab>().FirstOrDefault(slab => slab.Id == slabId) is not { } slab)
        {
            HintChanged?.Invoke(this, "Click on a roof, floor or ceiling: the shaft is cut through it there.");
            return false;
        }

        if (slab is Roof { IsExtrusion: true })
        {
            HintChanged?.Invoke(this, "A shaft goes through a roof drawn by footprint, a floor or a ceiling.");
            return false;
        }

        return PlaceShaft(Shafts.PlacedIn(Document, slab, new Point2D(at.X, at.Y), ActiveShaftShape, ActiveShaftWidth, ActiveShaftDepth));
    }

    private bool PlaceShaft(ShaftOpening shaft)
    {
        if (Document is null) return false;

        Apply(new AddElementCommand(Document, shaft, "Shaft Opening"));

        var through = Shafts.CutSummary(Document, shaft);
        HintChanged?.Invoke(this, through == "Nothing"
            ? "Shaft placed. It cuts nothing yet: nothing is there between its base and top - set them in Properties."
            : $"Shaft cut through {through}. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The shaft the next click would cut, following the cursor.</summary>
    private void DrawShaftPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Shaft || _shaftHover is not { } hover) return;

        var shaft = new ShaftOpening
        {
            Location = hover, Shape = ActiveShaftShape, Width = ActiveShaftWidth, Depth = ActiveShaftDepth
        };

        dc.DrawGeometry(null, SketchPreviewPen, Outline(shaft.Outline));
        foreach (var (from, to) in shaft.Cross) dc.DrawLine(SketchPreviewPen, ModelToScreen(from), ModelToScreen(to));
    }

    /// <summary>The shafts under a point of this plan.</summary>
    private IEnumerable<ShaftOpening> ShaftsAt(Point2D model) =>
        OnActiveLevel<ShaftOpening>().Where(shaft => Polygon2D.Contains(shaft.Outline, model));
}
