using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Valves on the plan: click a pipe and a servicing valve goes on it where it was clicked - made a stopvalve, a double check valve
/// or a drain-off tap in Properties. It stays on its pipe, moved and split with it.
/// </summary>
public partial class PlanView
{
    /// <summary>A valve on the pipe clicked, where it passes nearest the click. False where no pipe is there.</summary>
    public bool PlaceValveAt(Point2D raw)
    {
        if (Document is null) return false;
        if (PipesAt(raw).FirstOrDefault() is not { } pipe)
        {
            HintChanged?.Invoke(this, "Click on a pipe: the valve goes on it where you click.");
            return false;
        }

        var valve = PipeValves.For(pipe, raw, ValveKind.Servicing);
        Apply(new AddPipeValveCommand(Document, valve));
        HintChanged?.Invoke(this, $"A servicing valve on the {pipe.SizeName(Document.FindType<PipeType>(pipe.TypeId))} pipe - make it a stopvalve, a double check valve or a drain-off tap in Properties. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The valves on this level under a point: within their barrel, or a few pixels of it.</summary>
    private IEnumerable<PipeValve> ValvesAt(Point2D model) =>
        Document is null
            ? Enumerable.Empty<PipeValve>()
            : OnActiveLevel<PipeValve>().Where(valve => PipeValves.At(Document, valve) is { } at && Document.Find<Pipe>(valve.PipeId) is { } pipe
                                                        && new Point2D(at.X, at.Y).DistanceTo(model) <= Math.Max(pipe.Diameter * 1.5, 8 / PixelsPerMm));
}
