using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Drawing in 2D: detail lines - click where one starts and where it ends, the next starting
/// there, in the style picked - and filled regions, their corners clicked round and closed on the
/// first or by a double-click, in the pattern picked. On a storey's plan they are its notes; in a
/// drafting view, the detail itself - where the plan holds nothing of the model and no gridlines.
/// </summary>
public partial class PlanView
{
    private Point2D? _detailStart;
    private Point2D? _detailHover;

    /// <summary>The style the Detail Line tool draws in.</summary>
    public DetailLineStyle ActiveDetailLineStyle { get; set; } = DetailLineStyle.Medium;

    /// <summary>The pattern the Filled Region tool fills with.</summary>
    public FillPattern ActiveFillPattern { get; set; } = FillPattern.Diagonal;

    /// <summary>The drafting view open in the plan, if one is: what is drawn goes into it.</summary>
    public DraftingView? ActiveDrafting => Document?.FindElement(ActiveLevelId) as DraftingView;

    /// <summary>Whether a drafting view is open rather than a storey's plan.</summary>
    public bool Drafting => ActiveDrafting is not null;

    private void DetailHover(Point2D at)
    {
        _detailHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Detail Line tool: where a line starts, then where it ends - and the next
    /// starts there. Public so it can be driven from tests.
    /// </summary>
    public bool ClickDetailLine(Point2D at)
    {
        if (Document is null) return false;

        if (_detailStart is not { } start)
        {
            _detailStart = at;
            HintChanged?.Invoke(this, "Now click where it ends, or type its length: the next starts there. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 0.5) return false;

        var line = new DetailLine { LevelId = ActiveLevelId, Start = start, End = at, Style = ActiveDetailLineStyle };
        Apply(new AddElementCommand(Document, line, "Detail Line"));
        _detailStart = at;
        HintChanged?.Invoke(this, $"{Detailing.Name(line.Style)} line, {Core.Units.FormatLength(line.Length)}. Click where the next ends; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Esc with a detail line begun: its start forgotten.</summary>
    private bool CancelDetailLine()
    {
        if (_detailStart is null) return false;
        _detailStart = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>The detail line the next click would draw, from where it starts to the cursor.</summary>
    private void DrawDetailLinePreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.DetailLine || _detailStart is not { } start || _detailHover is not { } hover || (hover - start).Length < 0.5) return;
        dc.DrawLine(SketchPreviewPen, ModelToScreen(start), ModelToScreen(hover));
    }

    /// <summary>The outline clicked, made a filled region in the pattern picked. False with too few corners or none of it.</summary>
    private bool MakeFilledRegion(IReadOnlyList<Point2D> outline)
    {
        if (Document is null || outline.Count < 3 || Polygon2D.Area(outline) < 1)
        {
            HintChanged?.Invoke(this, "Too small to fill: click three corners or more round it.");
            return false;
        }

        var region = new FilledRegion { LevelId = ActiveLevelId, Pattern = ActiveFillPattern };
        region.SetBoundary(outline);
        Apply(new AddElementCommand(Document, region, "Filled Region"));
        HintChanged?.Invoke(this, $"{Detailing.Name(region.Pattern)} region, {Core.Units.SquareMetres(Polygon2D.Area(region.Boundary), "0.000")}. Draw another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
