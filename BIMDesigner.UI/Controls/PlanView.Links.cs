using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Linked models picked on a plan: a click on a line a link is cut in on this storey picks the link,
/// to see it in Properties, drag it to where it belongs - its offset follows - or take it out.
/// </summary>
public partial class PlanView
{
    /// <summary>The links whose cut lines on this plan pass within a few pixels of a point.</summary>
    private IEnumerable<ModelLink> LinksAt(Point2D model)
    {
        if (Document is null || Drafting || Document.FindLevel(ActiveLevelId) is not { } level) yield break;
        if (!Document.All<ModelLink>().Any(link => link.Shown && _shows(link))) yield break;

        var reach = 6 / PixelsPerMm;
        foreach (var (link, lines) in ModelLinks.CutAt(Document, level.Elevation + StackedWallType.PlanCutHeight))
            if (_shows(link) && lines.Any(line => DistanceToSegment(model, line.A, line.B) <= reach)) yield return link;
    }
}
