using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Ducts drawn on the plan, as pipes are. A run starts at a fitting's spigot - a heat recovery unit's,
/// a valve's, an outside terminal's, an extract fan's - the end of a duct or a point along one, or
/// anywhere, and goes on click by click, level at its run height, rising or dropping to the spigot
/// or duct it ends at. Ending at a fitting puts it on the run's system; a run started free takes the
/// system of what it joins. An outside terminal takes any air out or in. Esc or a double-click stops it.
/// </summary>
public partial class PlanView
{
    /// <summary>The duct type new ducts are drawn in.</summary>
    public Guid ActiveDuctTypeId { get; set; }

    /// <summary>The size new ducts are drawn at, mm - or 0 for the size of the spigot a run starts from.</summary>
    public double NewDuctWidth { get; set; }

    public double NewDuctHeight { get; set; }

    /// <summary>How high above the level a run goes along, to its centre, mm: in the loft or a ceiling's void.</summary>
    public double NewDuctOffset { get; set; } = 2600;

    /// <summary>What a run started away from any fitting or duct carries.</summary>
    public SystemKind NewDuctKind { get; set; } = SystemKind.SupplyAir;

    private Guid _ductTypeId;
    private Point3D? _ductFrom;
    private bool _ductFromFitting;
    private Component? _ductStartFitting;
    private double _ductHeight;
    private DuctSize? _ductSize;
    private SystemKind _ductKind;
    private Guid? _ductSystem;
    private readonly List<Duct> _ductRun = new();
    private Point2D? _ductHover;

    private sealed record DuctTarget(Point3D At, Component? Fitting, Connector? Connector, Duct? End, Duct? Along);

    private DuctTarget DuctTargetAt(Point2D raw, double height)
    {
        var reach = 12 / PixelsPerMm;
        if (_ductFrom is { } here && new Point2D(here.X, here.Y).DistanceTo(raw) <= reach)
            return new DuctTarget(here with { Z = height }, null, null, null, null);

        var connector = OnActiveLevel<Component>()
            .Where(fitting => _ductFrom is null || !ReferenceEquals(fitting, _ductStartFitting))
            .SelectMany(fitting => Connectors.Placed(Document!, fitting).Select(placed => (Fitting: fitting, Placed: placed)))
            .Select(entry => (entry.Fitting, entry.Placed, Distance: new Point2D(entry.Placed.At.X, entry.Placed.At.Y).DistanceTo(raw)))
            .Where(entry => entry.Distance <= Math.Max(reach, entry.Placed.Connector.Size / 2) && entry.Placed.Connector.Domain == ConnectorDomain.Duct)
            .OrderBy(entry => entry.Distance).FirstOrDefault();
        if (connector.Fitting is not null) return new DuctTarget(connector.Placed.At, connector.Fitting, connector.Placed.Connector, null, null);

        var ducts = OnActiveLevel<Duct>().ToList();
        var end = ducts.SelectMany(duct => new[] { (Duct: duct, At: duct.Ends(Document!).Start), (Duct: duct, At: duct.Ends(Document!).End) })
            .Select(entry => (entry.Duct, entry.At, Distance: new Point2D(entry.At.X, entry.At.Y).DistanceTo(raw)))
            .Where(entry => entry.Distance <= reach).OrderBy(entry => entry.Distance).FirstOrDefault();
        if (end.Duct is not null) return new DuctTarget(end.At, null, null, end.Duct, null);

        var nearest = ducts.Where(duct => !duct.Upright)
            .Select(duct => (Duct: duct, Along: Line2D.ClosestPointOnSegment(raw, duct.Start, duct.End)))
            .Select(entry => (entry.Duct, entry.Along, Distance: entry.Along.DistanceTo(raw)))
            .Where(entry => entry.Distance <= Math.Max(reach / 2, entry.Duct.Width / 2))
            .OrderBy(entry => _ductFrom is not null && entry.Duct.Kind != _ductKind ? 1 : 0).ThenBy(entry => entry.Distance)
            .FirstOrDefault();
        if (nearest.Duct is { } run)
        {
            var t = run.Start.DistanceTo(nearest.Along) / run.Start.DistanceTo(run.End);
            var level = Document!.FindLevel(run.LevelId)?.Elevation ?? 0;
            return new DuctTarget(Point3D.On(nearest.Along, level + run.StartOffset + (run.EndOffset - run.StartOffset) * t), null, null, null, run);
        }

        return new DuctTarget(Point3D.On(raw, height), null, null, null, null);
    }

    /// <summary>What a target carries; an outside terminal carries whatever goes out or in through it.</summary>
    private SystemKind? KindOf(DuctTarget target) =>
        target.Fitting is { } fitting && Document!.FindType<ComponentType>(fitting.TypeId)?.Form == ComponentForm.OutsideTerminal && _ductFrom is not null
            ? _ductKind
            : target.Connector?.Kind ?? (target.End ?? target.Along)?.Kind;

    private MepSystem? SystemAt(DuctTarget target, SystemKind kind) =>
        target.Fitting is { } fitting
            ? MepSystems.MemberOf(Document!, fitting, kind) ?? MepSystems.FedBy(Document!, fitting, kind).FirstOrDefault()
            : Document!.Find<MepSystem>((target.End ?? target.Along)?.SystemId);

    /// <summary>A click with the Duct tool: where a run starts, then each point it goes through, until it ends at a fitting or another duct.</summary>
    public bool ClickDuct(Point2D raw)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is not { } level) return false;
        var type = Document.FindType<DuctType>(ActiveDuctTypeId) ?? Document.TypesOf<DuctType>().FirstOrDefault();
        if (type is null) return false;

        var target = DuctTargetAt(raw, _ductFrom is null ? level.Elevation + NewDuctOffset : _ductHeight);
        var commands = new List<IUndoableCommand>();
        Point3D Through(DuctTarget at)
        {
            if (at.Along is not { } along) return at.At;
            var split = new SplitDuctCommand(Document, along, new Point2D(at.At.X, at.At.Y));
            commands.Add(split);
            return split.At;
        }

        if (_ductFrom is null)
        {
            _ductKind = KindOf(target) ?? NewDuctKind;
            _ductSystem = SystemAt(target, _ductKind)?.Id;
            _ductTypeId = type.Id;
            // The size picked; or the duct it starts from's, in its own shape; or the size as much air passes as the spigot it starts at.
            var source = target.End ?? target.Along;
            var round = target.Connector?.Size ?? source?.Width ?? 125;
            _ductSize = NewDuctWidth > 0 ? DuctSizes.Nearest(type, NewDuctWidth, NewDuctHeight)
                : source is not null && Document.FindType<DuctType>(source.TypeId)?.Shape == type.Shape ? DuctSizes.Nearest(type, source.Width, source.Height)
                : DuctSizes.Of(type).OrderBy(each => Math.Abs(each.Area - Math.PI * round * round / 4)).First();
            _ductFromFitting = target.Fitting is not null;
            _ductStartFitting = target.Fitting;
            _ductHeight = _ductFromFitting ? level.Elevation + NewDuctOffset : target.At.Z;
            _ductFrom = Through(target);
            _ductRun.Clear();
            if (commands.Count > 0) Apply(new CompositeCommand("Split Duct", commands));
            HintChanged?.Invoke(this, $"{(_ductKind == SystemKind.SupplyAir ? "A" : "An")} {_ductKind.Title().ToLowerInvariant()} duct from here: click where it goes - it runs level {Core.Units.FormatLength(_ductHeight - level.Elevation)} up - and end it on a fitting or a duct. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if (KindOf(target) is { } kind && kind != _ductKind)
        {
            HintChanged?.Invoke(this, $"That carries {kind.Title().ToLowerInvariant()}: this duct carries {_ductKind.Title().ToLowerInvariant()}.");
            return false;
        }

        // Up from the fitting to the run's height, along to over where it ends, and down, or up, to it.
        var points = new List<Point3D> { _ductFrom.Value };
        var joins = target.Fitting is not null || target.End is not null || target.Along is not null;
        var end = Through(target);
        if (_ductFromFitting && Math.Abs(_ductFrom.Value.Z - _ductHeight) > 1) points.Add(_ductFrom.Value with { Z = _ductHeight });
        points.Add(new Point3D(end.X, end.Y, points[^1].Z));
        if (joins && Math.Abs(end.Z - points[^1].Z) > 1) points.Add(end);

        var size = _ductSize ?? DuctSizes.Of(type)[0];
        var made = new List<Duct>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            if (Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2)) < 1) continue;
            made.Add(new Duct
            {
                TypeId = _ductTypeId == Guid.Empty ? type.Id : _ductTypeId, LevelId = ActiveLevelId, Start = new Point2D(a.X, a.Y), End = new Point2D(b.X, b.Y),
                StartOffset = a.Z - level.Elevation, EndOffset = b.Z - level.Elevation, Width = size.Width, Height = size.Height, Kind = _ductKind, SystemId = _ductSystem
            });
        }

        if (made.Count == 0) return false;

        // Joining a fitting or a duct on a system: a run on none takes it; a run on one puts the fitting on it.
        var said = string.Empty;
        if (joins && SystemAt(target, _ductKind) is { } joined && _ductSystem is null)
        {
            _ductSystem = joined.Id;
            foreach (var duct in made) duct.SystemId = joined.Id;
            if (_ductRun.Count > 0) commands.Add(new SetDuctSystemCommand(_ductRun, joined.Id));
            said = $" It is on {joined.Name}.";
            if (_ductStartFitting is { } first && !MepSystems.SystemsOf(Document, first).Contains(joined))
            {
                if (MepSystems.WhyNotMember(Document, first, joined) is null)
                    commands.Add(new EditSystemCommand(joined, $"Add to {joined.Name}", edited => edited.Members.Add(first.Id)));
                else if (joined.SourceId is null && MepSystems.WhyNotSource(Document, first, joined) is null)
                    commands.Add(new EditSystemCommand(joined, $"Source of {joined.Name}", edited => edited.SourceId = first.Id));
            }
        }
        else if (target.Fitting is { } fitting && Document.Find<MepSystem>(_ductSystem) is { } system && !MepSystems.SystemsOf(Document, fitting).Contains(system))
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
        }

        commands.AddRange(made.Select(duct => (IUndoableCommand)new AddElementCommand(Document, duct, "Draw Duct")));
        Apply(new CompositeCommand("Draw Duct", commands));
        _ductRun.AddRange(made);
        var length = made.Sum(duct => duct.Length);
        if (joins)
        {
            _ductFrom = null;
            HintChanged?.Invoke(this, $"Duct joined, {length / 1000:0.00} m.{said} Click where the next run starts.");
        }
        else
        {
            _ductFrom = points[^1];
            _ductHeight = points[^1].Z;
            _ductFromFitting = false;
            HintChanged?.Invoke(this, $"Duct {length / 1000:0.00} m. Click where it goes next, or end it on a fitting or a duct; Esc to stop.");
        }

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private bool CancelDuct()
    {
        if (_ductFrom is null) return false;
        _ductFrom = null;
        _ductRun.Clear();
        InvalidateVisual();
        return true;
    }

    private void DuctHover(Point2D at)
    {
        _ductHover = at;
        InvalidateVisual();
    }

    /// <summary>The duct the next click would draw, from where the run is to the cursor.</summary>
    private void DrawDuctPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Duct || _ductFrom is not { } from || _ductHover is not { } hover) return;
        var colour = _ductKind.Colour();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x80, colour.R, colour.G, colour.B)), Math.Max(2, (_ductSize?.Width ?? 125) * PixelsPerMm)) { DashStyle = DashStyles.Dash };
        dc.DrawLine(pen, ModelToScreen(new Point2D(from.X, from.Y)), ModelToScreen(hover));
    }

    /// <summary>The ducts on this level under a point.</summary>
    private IEnumerable<Duct> DuctsAt(Point2D model) =>
        OnActiveLevel<Duct>().Where(duct => duct.Upright
            ? duct.Start.DistanceTo(model) <= Math.Max(duct.Width / 2, 0) + 5 / PixelsPerMm
            : Line2D.DistanceFromSegment(model, duct.Start, duct.End) <= Math.Max(duct.Width / 2, 5 / PixelsPerMm));
}
