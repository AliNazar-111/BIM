using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// What a section of pipe carries: the loading units downstream of it, the most of any one
/// draw-off, how far it is to the last one, how far it is from the source - and, for the US, the
/// water-supply fixture units downstream of it.
/// </summary>
public sealed record PipeLoad(double Load, double Highest, double Downstream, double FromSource, double FixtureUnits = 0);

/// <summary>
/// A system's pipes traced from its source: what each carries, how far each fitting on it is from
/// the source along the pipes, the pipes nothing reaches from the source, and the fittings on the
/// system no pipe reaches yet.
/// </summary>
public sealed record PipeNetwork(
    MepSystem System,
    IReadOnlyDictionary<Pipe, PipeLoad> Loads,
    IReadOnlyDictionary<Component, double> RunTo,
    IReadOnlyList<Pipe> Unreached,
    IReadOnlyList<Component> NotPiped);

/// <summary>
/// The pipes of the services as networks. Pipes join where their ends meet, or where an end meets
/// a fitting's connector - within 5 mm - and each system's are traced out from the equipment
/// feeding it. Cold and hot water are sized by BS EN 806-3's simplified method: each section's
/// loading units added up back from the last draw-off, its pipe read off Table 3 for its material
/// - and a water heater's cold feed carries all the hot water draw-offs it serves. In the US, a
/// fixture's water-supply fixture units (IRC Table P2903.6) are counted instead, and the hot water
/// piping from its source to each fixture kept within 100 ft (IRC P2905.3).
/// </summary>
public static class PipeNetworks
{
    /// <summary>How near a pipe's end and a connector, or two pipes' ends, must be to be joined, mm.</summary>
    public const double Touch = 5;

    /// <summary>The longest hot water run to a fixture the IRC allows from its source, mm: 100 ft (P2905.3).</summary>
    public const double IrcHotWaterRun = 30480;

    private sealed class Nodes
    {
        private readonly List<Point3D> _points = new();
        private readonly Dictionary<(long, long, long), List<int>> _cells = new();

        private static (long, long, long) Cell(Point3D point) =>
            ((long)Math.Floor(point.X / 50), (long)Math.Floor(point.Y / 50), (long)Math.Floor(point.Z / 50));

        public IReadOnlyList<Point3D> Points => _points;

        /// <summary>The node at a point - an existing one within touch of it, or none.</summary>
        public int? Find(Point3D point)
        {
            var (x, y, z) = Cell(point);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                        if (_cells.TryGetValue((x + dx, y + dy, z + dz), out var near))
                            foreach (var index in near)
                                if (Distance(_points[index], point) <= Touch) return index;
            return null;
        }

        public int At(Point3D point)
        {
            if (Find(point) is { } found) return found;
            _points.Add(point);
            var cell = Cell(point);
            if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new List<int>();
            list.Add(_points.Count - 1);
            return _points.Count - 1;
        }
    }

    private static double Distance(Point3D a, Point3D b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>The pipes of a system.</summary>
    public static IReadOnlyList<Pipe> PipesOf(BimDocument document, MepSystem system) =>
        document.All<Pipe>().Where(pipe => pipe.SystemId == system.Id).ToList();

    /// <summary>Whether a kind is sized by its draw-offs: cold and hot water.</summary>
    public static bool IsWater(SystemKind kind) => kind is SystemKind.ColdWater or SystemKind.HotWater;

    /// <summary>
    /// What one connector draws, in loading units, and the most of any one draw-off behind it: a
    /// tap's or a cistern's own; a water heater's cold feed, all the hot water draw-offs it serves.
    /// </summary>
    public static (double Load, double Highest) Draw(BimDocument document, Component component, SystemKind kind)
    {
        if (!IsWater(kind) || document.FindType<ComponentType>(component.TypeId) is not { } type) return (0, 0);
        if (type.Form is ComponentForm.Boiler or ComponentForm.HotWaterCylinder && kind == SystemKind.ColdWater)
        {
            var served = MepSystems.FedBy(document, component, SystemKind.HotWater)
                .SelectMany(hot => MepSystems.MembersOf(document, hot))
                .Select(member => (double)PipeSizes.LoadingUnits(document.FindType<ComponentType>(member.TypeId)?.Form ?? ComponentForm.Box))
                .ToList();
            return (served.Sum(), served.DefaultIfEmpty(0).Max());
        }

        var units = PipeSizes.LoadingUnits(type.Form);
        return (units, units);
    }

    /// <summary>A system's pipes traced from what feeds it: what each carries and how far everything is along them.</summary>
    public static PipeNetwork Trace(BimDocument document, MepSystem system)
    {
        var pipes = PipesOf(document, system);
        var nodes = new Nodes();
        var links = new Dictionary<int, List<(Pipe Pipe, int To)>>();
        void Link(int from, Pipe pipe, int to)
        {
            if (!links.TryGetValue(from, out var list)) links[from] = list = new List<(Pipe, int)>();
            list.Add((pipe, to));
        }

        foreach (var pipe in pipes)
        {
            var (start, end) = pipe.Ends(document);
            var (a, b) = (nodes.At(start), nodes.At(end));
            if (a == b) continue;
            Link(a, pipe, b);
            Link(b, pipe, a);
        }

        // The fittings' connectors of the system's kind, where a pipe's end meets them.
        var draws = new Dictionary<int, (double Load, double Highest, double Units)>();
        var members = MepSystems.MembersOf(document, system);
        var at = new Dictionary<Component, int>();
        foreach (var member in members)
            foreach (var placed in Connectors.Placed(document, member).Where(placed => placed.Connector.Kind == system.Kind && !placed.Connector.Feeds))
            {
                if (nodes.Find(placed.At) is not { } node) continue;
                at.TryAdd(member, node);
                var (load, highest) = Draw(document, member, system.Kind);
                var units = IsWater(system.Kind) ? PipeSizes.FixtureUnits(document.FindType<ComponentType>(member.TypeId)?.Form ?? ComponentForm.Box) : 0;
                var known = draws.GetValueOrDefault(node);
                draws[node] = (known.Load + load, Math.Max(known.Highest, highest), known.Units + units);
            }

        // Out from the source, along the pipes, each node reached once.
        var starts = MepSystems.SourceOf(document, system) is { } source
            ? Connectors.Placed(document, source).Where(placed => placed.Connector.Kind == system.Kind).Select(placed => nodes.Find(placed.At)).OfType<int>().Distinct().ToList()
            : new List<int>();
        var parent = new Dictionary<int, (Pipe Pipe, int From)>();
        var distance = new Dictionary<int, double>();
        var order = new List<int>();
        var queue = new Queue<int>();
        foreach (var start in starts)
        {
            if (!distance.TryAdd(start, 0)) continue;
            queue.Enqueue(start);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            order.Add(node);
            foreach (var (pipe, to) in links.GetValueOrDefault(node) ?? new List<(Pipe, int)>())
            {
                if (distance.ContainsKey(to)) continue;
                distance[to] = distance[node] + pipe.Length;
                parent[to] = (pipe, node);
                queue.Enqueue(to);
            }
        }

        // Back from the furthest: what is behind each node - its own draw-offs and all beyond it -
        // is what the pipe up to it carries, and is added to the node it comes from.
        var behind = new Dictionary<int, (double Load, double Highest, double Downstream, double Units)>();
        var loads = new Dictionary<Pipe, PipeLoad>();
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            var own = draws.GetValueOrDefault(node);
            var beyond = behind.GetValueOrDefault(node);
            var all = (Load: beyond.Load + own.Load, Highest: Math.Max(beyond.Highest, own.Highest), beyond.Downstream, Units: beyond.Units + own.Units);
            behind[node] = all;
            if (!parent.TryGetValue(node, out var up)) continue;

            loads[up.Pipe] = new PipeLoad(all.Load, all.Highest, all.Downstream + up.Pipe.Length, distance[up.From], all.Units);
            var above = behind.GetValueOrDefault(up.From);
            behind[up.From] = (above.Load + all.Load, Math.Max(above.Highest, all.Highest), Math.Max(above.Downstream, all.Downstream + up.Pipe.Length), above.Units + all.Units);
        }

        var runTo = at.Where(entry => distance.ContainsKey(entry.Value)).ToDictionary(entry => entry.Key, entry => distance[entry.Value]);
        var unreached = pipes.Where(pipe => !loads.ContainsKey(pipe)).ToList();
        var notPiped = pipes.Count == 0 ? new List<Component>() : members.Where(member => !runTo.ContainsKey(member)).ToList();
        return new PipeNetwork(system, loads, runTo, unreached, notPiped);
    }

    /// <summary>The size a pipe of a water system needs by BS EN 806-3 - or null for one not traced, not water, or past the table.</summary>
    public static PipeSizingColumn? Required(BimDocument document, Pipe pipe, PipeNetwork network) =>
        IsWater(pipe.Kind) && document.FindType<PipeType>(pipe.TypeId) is { } type && network.Loads.TryGetValue(pipe, out var carried) && carried.Load > 0
            ? PipeSizes.Required(type.Material, carried.Load, carried.Highest, carried.Downstream)
            : null;

    /// <summary>What a pipe carries, as its Properties say it.</summary>
    public static string Describe(BimDocument document, Pipe pipe)
    {
        if (document.Find<MepSystem>(pipe.SystemId) is not { } system) return "On no system yet";
        var network = Trace(document, system);
        if (!network.Loads.TryGetValue(pipe, out var carried))
            return MepSystems.SourceOf(document, system) is null ? $"{system.Name} has nothing feeding it" : $"Not joined to what feeds {system.Name}";
        if (!IsWater(pipe.Kind)) return $"{carried.FromSource / 1000:0.0} m from what feeds {system.Name}";

        if (document.ProjectInformation.ClearanceStandard.IsUs())
            return $"{carried.FixtureUnits:0.#} water-supply fixture units (IRC Table P2903.6), {carried.Downstream / 304.8:0} ft to the last fixture";
        var said = $"{carried.Load:0} loading units, the most on one draw-off {carried.Highest:0}, {carried.Downstream / 1000:0.0} m to the last";
        var type = document.FindType<PipeType>(pipe.TypeId);
        return Required(document, pipe, network) is { } column && type is not null
            ? $"{said} - BS EN 806-3 {PipeSizes.TableOf(type.Material)} asks {column.Size.Name}"
            : carried.Load > 0 ? $"{said} - more than BS EN 806-3's Table 3 goes to" : said;
    }


    /// <summary>Every water pipe sized as BS EN 806-3 asks: its new outside diameter, where it changes.</summary>
    public static IReadOnlyList<(Pipe Pipe, double Diameter)> Sized(BimDocument document, IEnumerable<MepSystem> systems)
    {
        var sized = new List<(Pipe, double)>();
        foreach (var system in systems.Where(system => IsWater(system.Kind)))
        {
            var network = Trace(document, system);
            foreach (var pipe in network.Loads.Keys)
                if (Required(document, pipe, network) is { } column && Math.Abs(column.Size.Outside - pipe.Diameter) > 0.01)
                    sized.Add((pipe, column.Size.Outside));
        }

        return sized;
    }

    /// <summary>What is wanting in the pipes: a pipe too small, pipes nothing reaches, fittings no pipe reaches, a hot water run too long.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        foreach (var system in document.All<MepSystem>())
        {
            var pipes = PipesOf(document, system);
            if (pipes.Count == 0) continue;
            var network = Trace(document, system);

            if (MepSystems.SourceOf(document, system) is not null)
                foreach (var pipe in network.Unreached)
                    yield return new MepProblem(pipe, $"not joined to the rest of {system.Name} - nothing reaches it from what feeds it");
            foreach (var fitting in network.NotPiped)
                yield return new MepProblem(fitting, $"on {system.Name}, but no pipe reaches it yet");

            if (IsWater(system.Kind) && !us)
                foreach (var (pipe, carried) in network.Loads)
                {
                    if (carried.Load <= 0 || document.FindType<PipeType>(pipe.TypeId) is not { } type) continue;
                    var required = Required(document, pipe, network);
                    if (required is null)
                        yield return new MepProblem(pipe, $"carries {carried.Load:0} loading units - more than BS EN 806-3's {PipeSizes.TableOf(type.Material)} goes to");
                    else if (pipe.Diameter < required.Size.Outside - 0.01)
                        yield return new MepProblem(pipe,
                            $"too small at {pipe.SizeName(type)}: it carries {carried.Load:0} loading units, the most on one draw-off {carried.Highest:0}, {carried.Downstream / 1000:0.0} m to the last - BS EN 806-3 {PipeSizes.TableOf(type.Material)} asks {required.Size.Name}");
                }

            if (system.Kind == SystemKind.HotWater && us)
                foreach (var (fitting, run) in network.RunTo.Where(entry => entry.Value > IrcHotWaterRun))
                    yield return new MepProblem(fitting, $"its hot water runs {run / 304.8:0} ft from its source - IRC P2905.3 keeps it within 100 ft");
        }
    }
}
