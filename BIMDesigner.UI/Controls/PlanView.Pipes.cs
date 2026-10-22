using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Pipes drawn on the plan. A run starts at a fitting's connector, the end of a pipe or a point
/// along one - where it tees in - or anywhere, and goes on click by click, level at its run height
/// above the storey, rising or dropping upright to the connector or pipe it ends at. Ending at a
/// fitting puts the fitting on the run's system, and a run started free takes the system of what it
/// joins. Esc or a double-click stops it.
/// </summary>
public partial class PlanView
{
    /// <summary>The pipe type new pipes are drawn in.</summary>
    public Guid ActivePipeTypeId { get; set; }

    /// <summary>The size new pipes are drawn at, outside diameter; nothing to take the size of the fitting a run starts from.</summary>
    public double NewPipeDiameter { get; set; }

    /// <summary>How high above the storey's level a run goes along, to its centre line.</summary>
    public double NewPipeOffset { get; set; } = 250;

    /// <summary>What a run started away from any fitting or pipe carries.</summary>
    public SystemKind NewPipeKind { get; set; } = SystemKind.ColdWater;

    /// <summary>How much a drainage run falls, mm a metre: 20 to start - inside Approved Document H's 18 to 22 for a basin's longest branch, and steeper than a drain's 1:80.</summary>
    public double NewPipeFall { get; set; } = 20;

    private bool _pipeUpstream;
    private Guid _pipeTypeId;

    private Point3D? _pipeFrom;
    private bool _pipeFromFitting;
    private Component? _pipeStartFitting;
    private double _pipeHeight;
    private double _pipeSize;
    private SystemKind _pipeKind;
    private Guid? _pipeSystem;

    /// <summary>For a heating run, whether it is the return: it joins only the return, as a flow run joins only the flow.</summary>
    private bool _pipeReturn;
    private readonly List<Pipe> _pipeRun = new();
    private Point2D? _pipeHover;

    /// <summary>What a click lands on for a pipe: a fitting's connector, a pipe's end, a point along a pipe - or nothing, at the run's height.</summary>
    private sealed record PipeTarget(Point3D At, Component? Fitting, Connector? Connector, Pipe? End, Pipe? Along);

    private PipeTarget PipeTargetAt(Point2D raw, double height)
    {
        var reach = 12 / PixelsPerMm;

        // A run goes on from where it is: its own fitting is behind it, and a click straight over where it is goes up to its height.
        if (_pipeFrom is { } here && new Point2D(here.X, here.Y).DistanceTo(raw) <= reach)
            return new PipeTarget(here with { Z = height }, null, null, null, null);

        var connector = OnActiveLevel<Component>()
            .Where(fitting => _pipeFrom is null || !ReferenceEquals(fitting, _pipeStartFitting))
            .SelectMany(fitting => Connectors.Placed(Document!, fitting).Select(placed => (Fitting: fitting, Placed: placed)))
            .Select(entry => (entry.Fitting, entry.Placed, Distance: new Point2D(entry.Placed.At.X, entry.Placed.At.Y).DistanceTo(raw)))
            .Where(entry => entry.Distance <= reach && entry.Placed.Connector.Domain == ConnectorDomain.Pipe)
            .OrderBy(entry => entry.Distance).FirstOrDefault();
        if (connector.Fitting is not null) return new PipeTarget(connector.Placed.At, connector.Fitting, connector.Placed.Connector, null, null);

        // A heating run passes over the other side's pipes: a flow and its return run side by side and never join.
        var pipes = OnActiveLevel<Pipe>().Where(pipe => _pipeFrom is null || _pipeKind != SystemKind.Heating || pipe.Kind != SystemKind.Heating || pipe.Return == _pipeReturn).ToList();
        var end = pipes.SelectMany(pipe => new[] { (Pipe: pipe, At: pipe.Ends(Document!).Start), (Pipe: pipe, At: pipe.Ends(Document!).End) })
            .Select(entry => (entry.Pipe, entry.At, Distance: new Point2D(entry.At.X, entry.At.Y).DistanceTo(raw)))
            .Where(entry => entry.Distance <= reach).OrderBy(entry => entry.Distance).FirstOrDefault();
        // An upright pipe - a stack - passing the run's height: teed into there.
        var datum = Document!.FindLevel(ActiveLevelId)?.Elevation ?? 0;
        var stack = pipes.Where(pipe => pipe.Upright && pipe.Start.DistanceTo(raw) <= reach)
            .FirstOrDefault(pipe => Math.Min(pipe.StartOffset, pipe.EndOffset) + datum < height - 1 && height + 1 < Math.Max(pipe.StartOffset, pipe.EndOffset) + datum);
        if (_pipeFrom is not null && stack is not null)
            return new PipeTarget(Point3D.On(stack.Start, height), null, null, null, stack);

        if (end.Pipe is not null) return new PipeTarget(end.At, null, null, end.Pipe, null);

        // Along a pipe: the nearest - one carrying what the run does before one that does not.
        var nearest = pipes.Where(pipe => !pipe.Upright)
            .Select(pipe => (Pipe: pipe, Along: Line2D.ClosestPointOnSegment(raw, pipe.Start, pipe.End)))
            .Select(entry => (entry.Pipe, entry.Along, Distance: entry.Along.DistanceTo(raw)))
            .Where(entry => entry.Distance <= Math.Max(reach / 2, entry.Pipe.Diameter / 2))
            .OrderBy(entry => _pipeFrom is not null && entry.Pipe.Kind != _pipeKind ? 1 : 0).ThenBy(entry => entry.Distance)
            .FirstOrDefault();
        if (nearest.Pipe is { } run)
        {
            var t = run.Start.DistanceTo(nearest.Along) / run.Start.DistanceTo(run.End);
            var level = Document!.FindLevel(run.LevelId)?.Elevation ?? 0;
            return new PipeTarget(Point3D.On(nearest.Along, level + run.StartOffset + (run.EndOffset - run.StartOffset) * t), null, null, null, run);
        }

        return new PipeTarget(Point3D.On(raw, height), null, null, null, null);
    }

    /// <summary>The system of a kind a fitting is on or feeds - or a pipe's.</summary>
    private MepSystem? SystemAt(PipeTarget target, SystemKind kind) =>
        target.Fitting is { } fitting
            ? MepSystems.MemberOf(Document!, fitting, kind) ?? MepSystems.FedBy(Document!, fitting, kind).FirstOrDefault()
            : Document!.Find<MepSystem>((target.End ?? target.Along)?.SystemId);

    private static SystemKind? KindOf(PipeTarget target) => target.Connector?.Kind ?? (target.End ?? target.Along)?.Kind;

    /// <summary>For the heating, whether what a click lands on is the return; null for anything else.</summary>
    private bool? ReturnOf(PipeTarget target) =>
        target.Fitting is { } fitting && target.Connector is { } connector ? Heating.ReturnSide(Document!, fitting, connector)
        : (target.End ?? target.Along) is { Kind: SystemKind.Heating } pipe ? pipe.Return : null;

    /// <summary>
    /// A click with the Pipe tool: where a run starts, then each point it goes through, until it ends
    /// at a fitting or another pipe. Public so it can be driven from tests.
    /// </summary>
    public bool ClickPipe(Point2D raw)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is not { } level) return false;
        var type = Document.FindType<PipeType>(ActivePipeTypeId) ?? Document.TypesOf<PipeType>().FirstOrDefault();
        if (type is null) return false;

        // The height a run gets to over a point: its run height - a drainage run's falling, or rising, along the way.
        double Arriving(Point2D where) => _pipeFrom is not { } from
            ? level.Elevation + NewPipeOffset
            : _pipeHeight + (Drainage.IsDrainage(_pipeKind) ? (_pipeUpstream ? 1 : -1) * NewPipeFall / 1000 * new Point2D(from.X, from.Y).DistanceTo(where) : 0);
        var target = PipeTargetAt(raw, Arriving(raw));
        var commands = new List<IUndoableCommand>();

        // A tee into a pipe: the pipe cut where the branch meets it - a stack at the height the branch comes in at.
        Point3D Through(PipeTarget at)
        {
            if (at.Along is not { } along) return at.At;
            var split = new SplitPipeCommand(Document, along, new Point2D(at.At.X, at.At.Y), along.Upright ? at.At.Z - (Document.FindLevel(along.LevelId)?.Elevation ?? 0) : null);
            commands.Add(split);
            return split.At;
        }

        if (_pipeFrom is null)
        {
            _pipeKind = KindOf(target) ?? NewPipeKind;
            if (target.Connector is { Domain: not ConnectorDomain.Pipe }) return false;
            _pipeReturn = ReturnOf(target) ?? false;
            _pipeSystem = SystemAt(target, _pipeKind)?.Id;

            // Drainage runs from its fittings down to its outfall: drawn from a fitting it falls; drawn back from the outfall or a drain, it rises.
            var drains = Drainage.IsDrainage(_pipeKind);
            var root = drains && target.Connector is { Feeds: true };
            _pipeUpstream = root || drains && (target.End ?? target.Along) is not null;
            var run = TypeFor(type, _pipeKind, root ? level.Elevation + NewPipeOffset : target.At.Z);
            _pipeTypeId = run.Id;
            var from = target.Connector?.Size ?? (target.End ?? target.Along)?.Diameter ?? 15;
            _pipeSize = NewPipeDiameter > 0 && run.Id == type.Id ? NewPipeDiameter : PipeSizes.Nearest(run.Material, (target.End ?? target.Along) is { } pipe ? Drainage.Bore(Document, pipe) : from).Outside;
            _pipeFromFitting = target.Fitting is not null && !root;
            _pipeStartFitting = target.Fitting;
            _pipeHeight = root ? level.Elevation + NewPipeOffset
                : _pipeFromFitting ? drains && !_pipeUpstream ? Math.Min(target.At.Z, level.Elevation + NewPipeOffset) : level.Elevation + NewPipeOffset
                : target.At.Z;
            _pipeFrom = root ? new Point3D(target.At.X, target.At.Y, _pipeHeight) : Through(target);
            _pipeRun.Clear();
            if (commands.Count > 0) Apply(new CompositeCommand("Split Pipe", commands));
            HintChanged?.Invoke(this, drains
                ? $"A {_pipeKind.Title().ToLowerInvariant()} pipe from here, {(_pipeUpstream ? "rising" : "falling")} {NewPipeFall:0} mm a metre: click where it goes, and end it on {(_pipeUpstream ? "a fitting" : "a stack, a drain or an outfall")}. Esc to stop."
                : $"A {(_pipeKind == SystemKind.Heating ? _pipeReturn ? "heating return" : "heating flow" : _pipeKind.Title().ToLowerInvariant())} pipe from here: click where it goes - it runs level {Core.Units.FormatLength(_pipeHeight - level.Elevation)} up - and end it on a fitting or a pipe. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if (KindOf(target) is { } kind && kind != _pipeKind)
        {
            HintChanged?.Invoke(this, $"That carries {kind.Title().ToLowerInvariant()}: this pipe carries {_pipeKind.Title().ToLowerInvariant()}.");
            return false;
        }

        if (_pipeKind == SystemKind.Heating && ReturnOf(target) is { } other && other != _pipeReturn)
        {
            HintChanged?.Invoke(this, other ? "That is the return: this pipe is the flow, out to the radiators." : "That is the flow: this pipe is the return, back to the heat source.");
            return false;
        }

        // Up from the fitting to the run's height, along to over where it ends - falling, for drainage - and down, or up, to it.
        var drainage = Drainage.IsDrainage(_pipeKind);
        var slope = drainage ? (_pipeUpstream ? 1 : -1) * NewPipeFall / 1000 : 0;
        var points = new List<Point3D> { _pipeFrom.Value };
        var joins = target.Fitting is not null || target.End is not null || target.Along is not null;
        var outfall = drainage && target.Connector is { Feeds: true };
        var end = Through(target);
        if (_pipeFromFitting && Math.Abs(_pipeFrom.Value.Z - _pipeHeight) > 1) points.Add(_pipeFrom.Value with { Z = _pipeHeight });
        var last = points[^1];
        var along = new Point3D(end.X, end.Y, last.Z + slope * new Point2D(last.X, last.Y).DistanceTo(new Point2D(end.X, end.Y)));
        points.Add(along);
        if (joins && !outfall && Math.Abs(end.Z - along.Z) > 1)
        {
            if (drainage && !_pipeUpstream && end.Z > along.Z)
            {
                HintChanged?.Invoke(this, $"That is {(end.Z - along.Z) / 1000:0.00} m higher than the pipe gets to, falling {NewPipeFall:0} mm a metre: it would drain uphill.");
                return false;
            }

            if (drainage && _pipeUpstream && end.Z < along.Z)
            {
                HintChanged?.Invoke(this, $"That is {(along.Z - end.Z) / 1000:0.00} m lower than the pipe gets to, rising {NewPipeFall:0} mm a metre: it would drain uphill.");
                return false;
            }

            points.Add(end);
        }

        var made = new List<Pipe>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            if (Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2)) < 1) continue;
            made.Add(new Pipe
            {
                TypeId = _pipeTypeId == Guid.Empty ? type.Id : _pipeTypeId, LevelId = ActiveLevelId, Start = new Point2D(a.X, a.Y), End = new Point2D(b.X, b.Y),
                StartOffset = a.Z - level.Elevation, EndOffset = b.Z - level.Elevation, Diameter = _pipeSize, Kind = _pipeKind, SystemId = _pipeSystem,
                Return = _pipeKind == SystemKind.Heating && _pipeReturn
            });
        }

        if (made.Count == 0) return false;

        // Joining a fitting or a pipe on a system: a run on none takes it; a run on one puts the fitting on it.
        var said = string.Empty;
        if (joins && SystemAt(target, _pipeKind) is { } joined && _pipeSystem is null)
        {
            _pipeSystem = joined.Id;
            foreach (var pipe in made) pipe.SystemId = joined.Id;
            if (_pipeRun.Count > 0) commands.Add(new SetPipeSystemCommand(_pipeRun, joined.Id));
            said = $" It is on {joined.Name}.";

            // The fitting it started from goes on the system too.
            if (_pipeStartFitting is { } first && !MepSystems.SystemsOf(Document, first).Contains(joined))
            {
                if (MepSystems.WhyNotMember(Document, first, joined) is null)
                    commands.Add(new EditSystemCommand(joined, $"Add to {joined.Name}", edited => edited.Members.Add(first.Id)));
                else if (joined.SourceId is null && MepSystems.WhyNotSource(Document, first, joined) is null)
                    commands.Add(new EditSystemCommand(joined, $"Source of {joined.Name}", edited => edited.SourceId = first.Id));
            }
        }
        else if (target.Fitting is { } fitting && Document.Find<MepSystem>(_pipeSystem) is { } system && !MepSystems.SystemsOf(Document, fitting).Contains(system))
        {
            if (MepSystems.WhyNotMember(Document, fitting, system) is null)
            {
                commands.Add(new EditSystemCommand(system, $"Add to {system.Name}", edited => edited.Members.Add(fitting.Id)));
                said = $" {Document.FindType<ComponentType>(fitting.TypeId)?.Name ?? "The fitting"} is on {system.Name} now.";
            }
            else if (system.SourceId is null && MepSystems.WhyNotSource(Document, fitting, system) is null)
            {
                commands.Add(new EditSystemCommand(system, $"Source of {system.Name}", edited => edited.SourceId = fitting.Id));
                said = $" It feeds {system.Name} now.";
            }
            else said = $" {MepSystems.WhyNotMember(Document, fitting, system)}.";
        }

        commands.AddRange(made.Select(pipe => (IUndoableCommand)new AddElementCommand(Document, pipe, "Draw Pipe")));
        Apply(new CompositeCommand("Draw Pipe", commands));
        _pipeRun.AddRange(made);
        var length = made.Sum(pipe => pipe.Length);
        if (joins)
        {
            _pipeFrom = null;
            HintChanged?.Invoke(this, $"Pipe joined, {length / 1000:0.00} m.{said} Click where the next run starts.");
        }
        else
        {
            _pipeFrom = points[^1];
            _pipeHeight = points[^1].Z;
            _pipeFromFitting = false;
            HintChanged?.Invoke(this, $"Pipe {length / 1000:0.00} m. Click where it goes next, or end it on a fitting or a pipe; Esc to stop.");
        }

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// The pipe type a run is drawn in: the one picked - but drainage in PVC-U, its drain type below
    /// the ground and its soil and waste type above; and water in a water pipe, not PVC-U.
    /// </summary>
    private PipeType TypeFor(PipeType picked, SystemKind kind, double height)
    {
        var types = Document!.TypesOf<PipeType>().ToList();
        if (Drainage.IsDrainage(kind))
        {
            if (picked.Material == PipeMaterial.PvcU) return picked;
            var below = height < (Document.Levels.Count == 0 ? 0 : Document.Levels.Min(each => each.Elevation)) - 150;
            return types.FirstOrDefault(each => each.Material == PipeMaterial.PvcU && each.Name.Contains(below ? "Drain" : "Soil", StringComparison.OrdinalIgnoreCase))
                   ?? types.FirstOrDefault(each => each.Material == PipeMaterial.PvcU) ?? picked;
        }

        return picked.Material == PipeMaterial.PvcU ? types.FirstOrDefault(each => each.Material != PipeMaterial.PvcU) ?? picked : picked;
    }

    /// <summary>Esc or a double-click with a run begun: the run stopped where it is.</summary>
    private bool CancelPipe()
    {
        if (_pipeFrom is null) return false;
        _pipeFrom = null;
        _pipeRun.Clear();
        InvalidateVisual();
        return true;
    }

    private void PipeHover(Point2D at)
    {
        _pipeHover = at;
        InvalidateVisual();
    }

    /// <summary>The pipe the next click would draw, from where the run is to the cursor.</summary>
    private void DrawPipePreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Pipe || _pipeFrom is not { } from || _pipeHover is not { } hover) return;
        var colour = _pipeKind.Colour();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, colour.R, colour.G, colour.B)), Math.Max(2, _pipeSize * PixelsPerMm)) { DashStyle = DashStyles.Dash };
        dc.DrawLine(pen, ModelToScreen(new Point2D(from.X, from.Y)), ModelToScreen(hover));
    }

    /// <summary>The pipes on this level under a point.</summary>
    private IEnumerable<Pipe> PipesAt(Point2D model) =>
        OnActiveLevel<Pipe>().Where(pipe => pipe.Upright
            ? pipe.Start.DistanceTo(model) <= Math.Max(pipe.Diameter / 2, 0) + 5 / PixelsPerMm
            : Line2D.DistanceFromSegment(model, pipe.Start, pipe.End) <= Math.Max(pipe.Diameter / 2, 5 / PixelsPerMm));
}
