using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// The linked models on a plan: what each cuts through at the plan's height - 1.2 m over the storey's
/// level, as the plan itself is cut - drawn as lines under the model, faded to grey where its link is
/// halftone or in the plan's own ink otherwise; on the screen, on a sheet and in a PDF alike, and on a
/// DWG of the plan on a layer of its own, prefixed L-. Where a link is being compared with its issue
/// before, its lines are coloured by how each element has changed and what it lost drawn red and dashed.
/// </summary>
public sealed partial class PlanRenderer
{
    /// <summary>A comparison shown on the plan, or none - on a sheet or in an export, none.</summary>
    public ComparisonOverlay? Comparison { get; set; }

    private void DrawLinks(DrawingContext dc)
    {
        if (Document is null || !Document.All<ModelLink>().Any(link => link.Shown && Filter(link))) return;
        if (Document.FindLevel(ActiveLevelId) is not { } level) return;
        var height = level.Elevation + StackedWallType.PlanCutHeight;
        foreach (var (link, lines) in ModelLinks.CutAt(Document, height))
        {
            if (!Filter(link) || lines.Count == 0) continue;
            var group = new DrawingGroup();
            using (var inner = group.Open())
            {
                var plain = RenderPens.Solid(IsSelected(link) ? _ink.Selected : link.Halftone ? UnderlayGrey : _ink.WallOutline, IsSelected(link) ? 1.6 : 1.0);
                if (Comparison is not { } comparing) DrawLines(inner, lines, plain);
                else
                {
                    // By how each of its elements has changed; faded where it is as it was, or where another is compared.
                    foreach (var style in lines.GroupBy(line => comparing.StyleOfLinked(link.Id, line.Element)))
                    {
                        var pen = style.Key.Colour is { } colour ? RenderPens.Solid(Color.FromRgb(colour.R, colour.G, colour.B), 1.6) : plain;
                        inner.PushOpacity(style.Key.Halftone ? HalftoneOpacity : 1);
                        DrawLines(inner, style.ToList(), pen);
                        inner.Pop();
                    }

                    var removed = comparing.Link?.Id == link.Id ? comparing.RemovedCut(Document, height) : Array.Empty<(Point2D, Point2D, Guid)>();
                    if (removed.Count > 0)
                    {
                        var dashed = new Pen(Brush(ComparisonOverlay.RemovedColour), 1.4) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
                        dashed.Freeze();
                        DrawLines(inner, removed, dashed);
                    }
                }
            }

            group.SetValue(CadLayers.LayerProperty, "L-" + System.IO.Path.GetFileNameWithoutExtension(link.Path).ToUpperInvariant());
            dc.DrawDrawing(group);
        }
    }

    private void DrawLines(DrawingContext dc, IReadOnlyList<(Point2D A, Point2D B, Guid Element)> lines, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
            foreach (var (a, b, _) in lines)
            {
                context.BeginFigure(ModelToScreen(a), false, false);
                context.LineTo(ModelToScreen(b), true, false);
            }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
