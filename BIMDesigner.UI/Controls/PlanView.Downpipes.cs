using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Downpipes on the plan: click on a gutter and a downpipe goes there - an outlet in the
/// gutter's bottom, a swan neck back to the wall, the pipe down it and a shoe at its foot. Or,
/// with a gutter or roof selected, one at each end of each of its gutters in one go.
/// </summary>
public partial class PlanView
{
    /// <summary>How near a gutter a click has to be to put a downpipe on it, in screen pixels beyond the gutter itself.</summary>
    private const double DownpipeReachPixels = 12;

    private Point2D? _downpipeHover;

    private void DownpipeHover(Point2D raw)
    {
        _downpipeHover = raw;
        InvalidateVisual();
    }

    /// <summary>The gutter nearest a point on this plan, if the point is on it or close by.</summary>
    private Gutter? GutterNear(Point2D raw)
    {
        if (Document is null) return null;

        var reach = DownpipeReachPixels / PixelsPerMm;
        return OnActiveLevel<Gutter>()
            .Select(gutter => (Gutter: gutter, Near: Downpipes.Nearest(Document, gutter, raw)))
            .Where(entry => entry.Near is { } near && near.Distance <= reach + GutterReach(entry.Gutter))
            .OrderBy(entry => entry.Near!.Value.Distance)
            .Select(entry => entry.Gutter)
            .FirstOrDefault();
    }

    /// <summary>How far out from its roof's edge a gutter reaches, so a click anywhere over it counts.</summary>
    private double GutterReach(Gutter gutter) =>
        Document?.FindType<GutterType>(gutter.TypeId) is { } type ? type.Width + Math.Abs(gutter.HorizontalOffset) : 150;

    /// <summary>
    /// A click with the Downpipe tool: a downpipe on the gutter there, its outlet at the point of
    /// the gutter nearest the click. Public so it can be driven from tests.
    /// </summary>
    public bool PlaceDownpipeAt(Point2D raw)
    {
        if (Document is null) return false;

        if (GutterNear(raw) is not { } gutter)
        {
            HintChanged?.Invoke(this, "Click on a gutter: the downpipe goes where you click along it.");
            return false;
        }

        return PlaceDownpipe(gutter, raw);
    }

    /// <summary>A click on a gutter in the 3D view with the Downpipe tool: a downpipe where it was clicked.</summary>
    public bool PlaceDownpipeIn3D(Guid gutterId, Point3D at) =>
        Document?.Elements.OfType<Gutter>().FirstOrDefault(gutter => gutter.Id == gutterId) is { } gutter &&
        PlaceDownpipe(gutter, new Point2D(at.X, at.Y));

    private bool PlaceDownpipe(Gutter gutter, Point2D near)
    {
        if (Document is null) return false;

        var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = near };
        if (Downpipes.Path(Document, pipe) is not { } path)
        {
            HintChanged?.Invoke(this, "That gutter has nothing to hang a downpipe from.");
            return false;
        }

        Apply(new AddElementCommand(Document, pipe, "Downpipe"));

        HintChanged?.Invoke(this, path.OntoRoof
            ? "Downpipe down onto the roof below. Click for another; Esc to stop."
            : "Downpipe down the wall to the ground, with a shoe at its foot. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A downpipe at each end of each run of some gutters - and at each corner of one that goes
    /// all round - where there is not one near already, as one step to undo. Returns how many.
    /// </summary>
    public int AddDownpipesAtEnds(IEnumerable<Gutter> gutters)
    {
        if (Document is null) return 0;

        var pipes = new List<Downpipe>();
        foreach (var gutter in gutters)
        {
            var existing = Downpipes.On(Document, gutter).Select(pipe => pipe.Location).ToList();
            foreach (var place in Downpipes.AtEnds(Document, gutter).Where(place => existing.All(other => other.DistanceTo(place) > 300)))
            {
                var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = place };
                if (Downpipes.Path(Document, pipe) is not null) pipes.Add(pipe);
            }
        }

        if (pipes.Count == 0) return 0;

        Apply(new CompositeCommand("Downpipes", pipes.Select(pipe => (IUndoableCommand)new AddElementCommand(Document, pipe, "Downpipe")).ToList()));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return pipes.Count;
    }

    /// <summary>Where the downpipe would go, following the cursor along the gutter under it.</summary>
    private void DrawDownpipePreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Downpipe || _downpipeHover is not { } hover || Document is null) return;
        if (GutterNear(hover) is not { } gutter) return;

        var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = hover };
        if (Downpipes.Path(Document, pipe) is not { } path) return;

        dc.DrawGeometry(null, SketchPreviewPen, Outline(path.Footprint));
        dc.DrawLine(SketchPreviewPen, ModelToScreen(path.Points[0].Plan), ModelToScreen(path.Foot));
    }

    /// <summary>Whether a downpipe is drawn on this plan: on its gutter's storey, and on every one it passes down through.</summary>
    private bool DownpipeShownHere(Downpipe pipe) =>
        pipe.LevelId == ActiveLevelId ||
        (Document?.FindLevel(ActiveLevelId) is { } level && Downpipes.Path(Document, pipe) is { } path &&
         level.Elevation >= path.Bottom - 1 && level.Elevation < path.Points[0].Z);

    /// <summary>The downpipes under a point of this plan.</summary>
    private IEnumerable<Downpipe> DownpipesAt(Point2D model)
    {
        if (Document is null) yield break;

        var reach = 4 / PixelsPerMm;
        foreach (var pipe in OnActiveLevel<Downpipe>())
        {
            if (Downpipes.Path(Document, pipe) is not { } path) continue;

            var footprint = path.Footprint;
            if (Polygon2D.Contains(footprint, model) ||
                footprint.Select((point, i) => DistanceToSegment(model, point, footprint[(i + 1) % footprint.Count])).Min() <= reach ||
                DistanceToSegment(model, path.Points[0].Plan, path.Foot) <= reach)
                yield return pipe;
        }
    }
}
