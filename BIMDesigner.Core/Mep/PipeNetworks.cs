using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// What a section of pipe carries: the loading units downstream of it, the most of any one
/// draw-off, how far it is to the last one, how far it is from the source - and, for the US, the
/// water-supply fixture units downstream of it.
/// </summary>
public sealed record PipeLoad(double Load, double Highest, double Downstream, double FromSource, double FixtureUnits = 0)
{
    /// <summary>How many fittings it serves, and of them how many are WCs.</summary>
    public int Fittings { get; init; }

    public int Wcs { get; init; }

    /// <summary>Its end nearer the source - downstream, on a drain - and its other end.</summary>
    public Point3D Near { get; init; }

    public Point3D Far { get; init; }

    /// <summary>The next pipe on toward the source from its near end; none where it reaches the source.</summary>
    public Pipe? Toward { get; init; }

    /// <summary>How many pipes meet at its near end, it among them: three or more at a junction - a tee into a stack or another branch.</summary>
    public int NearJoins { get; init; }

    /// <summary>On a heating zone, the heat the radiators, floors and coils beyond it give at its temperatures, W.</summary>
    public double Heat { get; init; }
}

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
    IReadOnlyList<Component> NotPiped)
{
    /// <summary>The pipe each fitting reached meets at its connector.</summary>
    public IReadOnlyDictionary<Component, Pipe> FirstPipe { get; init; } = new Dictionary<Component, Pipe>();

    /// <summary>On a rainwater system, the downpipes whose feet its drains take.</summary>
    public IReadOnlyList<Downpipe> Downpipes { get; init; } = Array.Empty<Downpipe>();
}

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

    /// <summary>How near in plan a drain must come to an outfall, a soakaway or a downpipe's foot to be taken by it, mm: the gully or chamber between them.</summary>
    public const double Reach = 300;

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

    /// <summary>
    /// Where pipes go when the connectors they are joined to move - a radiator changed for a wider
    /// one: each end at a connector carried to where it now is; a drop to it carried across whole,
    /// and the end of the run it hangs from with it.
    /// </summary>
    public static IReadOnlyList<(Pipe Pipe, Point2D Start, Point2D End, double StartOffset, double EndOffset)> Follow(BimDocument document, IReadOnlyList<(Point3D From, Point3D To)> moves)
    {
        var moved = new Dictionary<Pipe, (Point2D Start, Point2D End, double StartOffset, double EndOffset)>();
        var pending = new Queue<(Point3D From, Point3D To, bool Carried)>(moves.Select(move => (move.From, move.To, false)));
        var pipes = document.All<Pipe>().ToList();
        while (pending.Count > 0)
        {
            var (from, to, carried) = pending.Dequeue();
            foreach (var pipe in pipes)
            {
                var now = moved.TryGetValue(pipe, out var known) ? known : (pipe.Start, pipe.End, pipe.StartOffset, pipe.EndOffset);
                var elevation = document.FindLevel(pipe.LevelId)?.Elevation ?? 0;
                var start = Point3D.On(now.Start, elevation + now.StartOffset);
                var end = Point3D.On(now.End, elevation + now.EndOffset);
                var atStart = Distance(start, from) <= Touch;
                if (!atStart && Distance(end, from) > Touch) continue;

                var shift = new Vector2D(to.X - from.X, to.Y - from.Y);
                if (!carried && now.Start.DistanceTo(now.End) < 1)
                {
                    // A drop: across whole, its foot to the connector, its head where it was over it.
                    var head = atStart ? end : start;
                    moved[pipe] = atStart
                        ? (now.Start + shift, now.End + shift, to.Z - elevation, now.EndOffset)
                        : (now.Start + shift, now.End + shift, now.StartOffset, to.Z - elevation);
                    pending.Enqueue((head, new Point3D(head.X + shift.X, head.Y + shift.Y, head.Z), true));
                }
                else
                    moved[pipe] = atStart
                        ? (new Point2D(to.X, to.Y), now.End, to.Z - elevation, now.EndOffset)
                        : (now.Start, new Point2D(to.X, to.Y), now.StartOffset, to.Z - elevation);
            }
        }

        return moved.Select(entry => (entry.Key, entry.Value.Start, entry.Value.End, entry.Value.StartOffset, entry.Value.EndOffset)).ToList();
    }

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
        using var cache = system.Kind == SystemKind.Heating ? ModelCache.Begin(document) : null;
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

        // The fittings' connectors of the system's kind, where a pipe's end meets them - on a heating
        // zone, the heat each gives at both its flow and its return, as the water goes out and comes back.
        var draws = new Dictionary<int, (double Load, double Highest, double Units, int Fittings, int Wcs, double Heat)>();
        var members = MepSystems.MembersOf(document, system);
        var at = new Dictionary<Component, int>();

        // In the US, the fixtures each node's water goes on to, and whether to their cold, their hot or both:
        // what their water-supply fixture units come to there (IRC Table P2903.6).
        var us = IsWater(system.Kind) && document.ProjectInformation.ClearanceStandard.IsUs();
        var supplied = new Dictionary<int, Dictionary<Component, Supply>>();
        void Supplies(int node, Component fixture, Supply supply)
        {
            if (!supplied.TryGetValue(node, out var known)) supplied[node] = known = new Dictionary<Component, Supply>();
            known[fixture] = known.GetValueOrDefault(fixture) | supply;
        }
        foreach (var member in members)
        {
            var heat = system.Kind == SystemKind.Heating ? HeatOf(document, member) : 0;
            foreach (var placed in Connectors.Placed(document, member).Where(placed => placed.Connector.Kind == system.Kind && !placed.Connector.Feeds && placed.Connector.Domain == ConnectorDomain.Pipe))
            {
                if (nodes.Find(placed.At) is not { } node) continue;
                if (!at.TryAdd(member, node))
                {
                    if (heat > 0)
                    {
                        var also = draws.GetValueOrDefault(node);
                        draws[node] = also with { Heat = also.Heat + heat };
                    }

                    continue;
                }

                var form = document.FindType<ComponentType>(member.TypeId)?.Form ?? ComponentForm.Box;
                var (load, highest) = Draw(document, member, system.Kind);
                var units = IsWater(system.Kind) ? PipeSizes.FixtureUnits(form) : Drainage.IsDrainage(system.Kind) ? Drainage.FixtureUnits(form) : 0;
                if (us && form is ComponentForm.Boiler or ComponentForm.HotWaterCylinder && system.Kind == SystemKind.ColdWater)
                    foreach (var served in MepSystems.FedBy(document, member, SystemKind.HotWater).SelectMany(hot => MepSystems.MembersOf(document, hot)))
                        Supplies(node, served, Supply.Hot);
                else if (us)
                    Supplies(node, member, system.Kind == SystemKind.HotWater ? Supply.Hot : Supply.Cold);
                var known = draws.GetValueOrDefault(node);
                draws[node] = (known.Load + load, Math.Max(known.Highest, highest), known.Units + units, known.Fittings + 1,
                    known.Wcs + (form is ComponentForm.Toilet or ComponentForm.WallHungToilet ? 1 : 0), known.Heat + heat);
            }
        }

        // A drain is taken by an outfall or soakaway over it, and takes a downpipe's foot beside it, wherever its invert is.
        int? Under(Point2D where) => nodes.Points.Select((point, index) => (Index: index, Plan: new Point2D(point.X, point.Y).DistanceTo(where), point.Z))
            .Where(entry => entry.Plan <= Reach).OrderBy(entry => entry.Z).ThenBy(entry => entry.Plan).Select(entry => (int?)entry.Index).FirstOrDefault();
        var downpipes = new List<Downpipe>();
        if (system.Kind == SystemKind.Rainwater)
            foreach (var downpipe in document.All<Downpipe>())
                if (Downpipes.Path(document, downpipe) is { OntoRoof: false } path && Under(path.Foot) is { } foot)
                {
                    downpipes.Add(downpipe);
                    var known = draws.GetValueOrDefault(foot);
                    draws[foot] = (known.Load, known.Highest, known.Units, known.Fittings + 1, known.Wcs, known.Heat);
                }

        // Out from the source, along the pipes, each node reached once.
        var starts = MepSystems.SourceOf(document, system) is { } source
            ? Connectors.Placed(document, source).Where(placed => placed.Connector.Kind == system.Kind).Select(placed => nodes.Find(placed.At)).OfType<int>().Distinct().ToList()
            : new List<int>();
        if (starts.Count == 0 && Drainage.IsDrainage(system.Kind) && MepSystems.SourceOf(document, system) is { } outfall && Under(outfall.Location) is { } under)
            starts.Add(under);
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
        var behind = new Dictionary<int, (double Load, double Highest, double Downstream, double Units, int Fittings, int Wcs, double Heat)>();
        var loads = new Dictionary<Pipe, PipeLoad>();
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            var own = draws.GetValueOrDefault(node);
            var beyond = behind.GetValueOrDefault(node);
            var all = (Load: beyond.Load + own.Load, Highest: Math.Max(beyond.Highest, own.Highest), beyond.Downstream, Units: beyond.Units + own.Units,
                Fittings: beyond.Fittings + own.Fittings, Wcs: beyond.Wcs + own.Wcs, Heat: beyond.Heat + own.Heat);
            if (us) all.Units = FixtureUnits(document, supplied.GetValueOrDefault(node));
            behind[node] = all;
            if (!parent.TryGetValue(node, out var up)) continue;
            if (us && supplied.TryGetValue(node, out var going))
                foreach (var (fixture, supply) in going)
                    Supplies(up.From, fixture, supply);

            loads[up.Pipe] = new PipeLoad(all.Load, all.Highest, all.Downstream + up.Pipe.Length, distance[up.From], all.Units)
            {
                Fittings = all.Fittings, Wcs = all.Wcs, Near = nodes.Points[up.From], Far = nodes.Points[node],
                Toward = parent.TryGetValue(up.From, out var on) ? on.Pipe : null, NearJoins = links.GetValueOrDefault(up.From)?.Count ?? 0, Heat = all.Heat
            };
            var above = behind.GetValueOrDefault(up.From);
            behind[up.From] = (above.Load + all.Load, Math.Max(above.Highest, all.Highest), Math.Max(above.Downstream, all.Downstream + up.Pipe.Length),
                above.Units + all.Units, above.Fittings + all.Fittings, above.Wcs + all.Wcs, above.Heat + all.Heat);
        }

        var runTo = at.Where(entry => distance.ContainsKey(entry.Value)).ToDictionary(entry => entry.Key, entry => distance[entry.Value]);
        var unreached = pipes.Where(pipe => !loads.ContainsKey(pipe)).ToList();
        // What is wired to the system - a room thermostat on its zone - wants no pipe; with nothing feeding it yet, a fitting a pipe meets is piped.
        var fed = MepSystems.SourceOf(document, system) is not null;
        var notPiped = pipes.Count == 0 ? new List<Component>() : members.Where(member => !(fed ? runTo.ContainsKey(member) : at.ContainsKey(member))
            && Connectors.Placed(document, member).Any(placed => placed.Connector.Kind == system.Kind && placed.Connector.Domain == ConnectorDomain.Pipe)).ToList();

        // The pipe at each fitting's connector: the one up to its node.
        var first = new Dictionary<Component, Pipe>();
        foreach (var (member, node) in at)
            if (parent.TryGetValue(node, out var up)) first[member] = up.Pipe;
            else if (links.TryGetValue(node, out var touching) && touching.Count > 0) first[member] = touching[0].Pipe;
        return new PipeNetwork(system, loads, runTo, unreached, notPiped) { FirstPipe = first, Downpipes = downpipes.Where(downpipe => Under(Downpipes.Path(document, downpipe)!.Foot) is { } foot && distance.ContainsKey(foot)).ToList() };
    }

    /// <summary>Which of a fixture's supplies a pipe carries: its cold, its hot - through the water heater it feeds - or both.</summary>
    [Flags]
    private enum Supply
    {
        Cold = 1,
        Hot = 2
    }

    /// <summary>
    /// The water-supply fixture units a pipe carries, IRC Table P2903.6: each fixture's cold or hot
    /// value where it carries only that, both together where it carries both - its cold and, through
    /// the water heater beyond, its hot - and the whole of a fixture with only a cold supply, a tank
    /// WC's 2.2 or a cold-fill dishwasher's 1.4.
    /// </summary>
    private static double FixtureUnits(BimDocument document, Dictionary<Component, Supply>? supplied)
    {
        if (supplied is null) return 0;
        var units = 0.0;
        foreach (var (fixture, supply) in supplied)
        {
            if (document.FindType<ComponentType>(fixture.TypeId) is not { } type) continue;
            var (hot, cold, combined) = PipeSizes.FixtureUnitsOf(type.Form);
            var hasHot = Connectors.Of(type).Any(connector => connector.Kind == SystemKind.HotWater && !connector.Feeds);
            units += supply == (Supply.Cold | Supply.Hot) || !hasHot ? combined : supply == Supply.Cold ? cold : hot;
        }

        return units;
    }

    /// <summary>The heat a fitting on a heating zone gives at its temperatures, W: a radiator's or a floor's in its room, a cylinder coil's rating.</summary>
    private static double HeatOf(BimDocument document, Component member)
    {
        if (document.FindType<ComponentType>(member.TypeId) is not { } type) return 0;
        if (type.Form == ComponentForm.HotWaterCylinder) return type.HeatOutput;
        return Heating.IsEmitter(type.Form) ? Heating.OutputOf(document, member)?.Watts ?? 0 : 0;
    }

    /// <summary>The size a pipe of a water system needs by BS EN 806-3 - or null for one not traced, not water, or past the table.</summary>
    public static PipeSizingColumn? Required(BimDocument document, Pipe pipe, PipeNetwork network) =>
        IsWater(pipe.Kind) && document.FindType<PipeType>(pipe.TypeId) is { } type && network.Loads.TryGetValue(pipe, out var carried) && carried.Load > 0
            ? PipeSizes.Required(type.Material, carried.Load, carried.Highest, carried.Downstream)
            : null;

    /// <summary>
    /// A US house's water as the IRC's Appendix AP takes it for a system: the static pressure where
    /// it comes in and what is left at the highest outlet, the developed length to the furthest
    /// fixture - through the water heater to a hot one - with its fittings, the pressure range and
    /// length column of Table AP201.1 they pick, every fixture's combined fixture units, the meter
    /// and main they ask, and the cold water system the house's water comes in on.
    /// </summary>
    public sealed record UsWater(double Static, double Pressure, double DevelopedFeet, int? Range, int? Column, double Units, IrcWaterSizing.Row? Main, MepSystem? Supply)
    {
        /// <summary>The table's row and column it is read in: "40 to 49 psi, 100 ft".</summary>
        public string Where => Range is { } range && Column is { } column ? $"{IrcWaterSizing.RangeTitle(range)}, {IrcWaterSizing.Lengths[column]:0} ft" : string.Empty;

        /// <summary>Why the table goes no further, where it does not.</summary>
        public string? Beyond =>
            Range is null ? $"the water is at {Pressure:0} psi at its highest outlet - under the 30 psi Table AP201.1 starts at"
            : Column is null ? $"its furthest fixture is {DevelopedFeet:0} ft developed with its fittings - past Table AP201.1's 500 ft"
            : Main is null ? $"its fixtures come to {Units:0.#} water-supply fixture units - more than Table AP201.1 goes to at {Where}"
            : null;
    }

    /// <summary>A water system's water as Appendix AP takes it, in a US house.</summary>
    public static UsWater UsWaterOf(BimDocument document, MepSystem system)
    {
        // The cold water system the house's water comes in on: a hot one's water heater is on it.
        var supply = system.Kind == SystemKind.ColdWater ? system
            : MepSystems.SourceOf(document, system) is { } heats
                ? document.All<MepSystem>().FirstOrDefault(cold => cold.Kind == SystemKind.ColdWater && MepSystems.MembersOf(document, cold).Contains(heats))
                : null;
        var root = supply ?? system;
        var network = Trace(document, root);

        // How far the water goes to each fixture, and how high: on through each water heater to its hot taps.
        var runs = new List<double>(network.RunTo.Values);
        var outlets = new List<double>();
        void Outlets(MepSystem each) => outlets.AddRange(MepSystems.MembersOf(document, each).SelectMany(member => Connectors.Placed(document, member))
            .Where(placed => placed.Connector.Kind == each.Kind && !placed.Connector.Feeds && placed.Connector.Domain == ConnectorDomain.Pipe).Select(placed => placed.At.Z));
        Outlets(root);
        if (supply is not null)
            foreach (var hot in document.All<MepSystem>().Where(hot => hot.Kind == SystemKind.HotWater))
                if (MepSystems.SourceOf(document, hot) is { } heater && network.RunTo.TryGetValue(heater, out var toHeater))
                {
                    runs.AddRange(Trace(document, hot).RunTo.Values.Select(run => toHeater + run));
                    Outlets(hot);
                }

        // The pressure left at the highest outlet: half a psi lost for each foot it is above where the water comes in, gained for each below.
        var pressure = document.ProjectInformation.WaterPressure;
        var source = MepSystems.SourceOf(document, root);
        var sourceZ = source is null ? null : Connectors.Placed(document, source).Where(placed => placed.Connector.Kind == root.Kind && placed.Connector.Feeds).Select(placed => (double?)placed.At.Z).FirstOrDefault();
        var left = sourceZ is { } z && outlets.Count > 0 ? pressure - IrcWaterSizing.PsiPerFoot * (outlets.Max() - z) / Units.MillimetresPerFoot : pressure;
        var developed = runs.DefaultIfEmpty(0).Max() / Units.MillimetresPerFoot * IrcWaterSizing.FittingsAllowance;
        var units = network.Loads.Values.Where(load => load.Toward is null).Sum(load => load.FixtureUnits);

        var range = IrcWaterSizing.RangeOf(left);
        var column = IrcWaterSizing.ColumnOf(developed);
        var main = range is { } r && column is { } c ? IrcWaterSizing.Main(r, c, units) : null;
        return new UsWater(pressure, left, developed, range, column, units, main, supply);
    }

    /// <summary>
    /// The size a US water pipe needs by Appendix AP, and its nominal inches: the main's where it
    /// leaves where the water comes in, each segment's after it - or null for one carrying nothing,
    /// or where the table goes no further.
    /// </summary>
    public static (PipeSize Size, double Inches)? UsRequired(BimDocument document, Pipe pipe, PipeNetwork network, UsWater water)
    {
        if (!IsWater(pipe.Kind) || document.FindType<PipeType>(pipe.TypeId) is not { } type || !network.Loads.TryGetValue(pipe, out var carried) || carried.FixtureUnits <= 0) return null;
        if (water is not { Range: { } range, Column: { } column, Main: { } main }) return null;
        var inches = ReferenceEquals(network.System, water.Supply) && carried.Toward is null ? main.Distribution : IrcWaterSizing.Segment(range, column, carried.FixtureUnits, main);
        return (IrcWaterSizing.SizeOf(type.Material, inches), inches);
    }

    /// <summary>What a pipe carries, as its Properties say it.</summary>
    public static string Describe(BimDocument document, Pipe pipe)
    {
        if (document.Find<MepSystem>(pipe.SystemId) is not { } system) return "On no system yet";
        var network = Trace(document, system);
        if (!network.Loads.TryGetValue(pipe, out var carried))
            return MepSystems.SourceOf(document, system) is null ? $"{system.Name} has nothing feeding it" : $"Not joined to what feeds {system.Name}";
        if (pipe.Kind == SystemKind.Heating) return HeatingPipes.Describe(document, pipe, network);
        if (!IsWater(pipe.Kind)) return $"{Units.Metres(carried.FromSource, "0.0")} from what feeds {system.Name}";

        if (document.ProjectInformation.ClearanceStandard.IsUs())
        {
            var units = $"{carried.FixtureUnits:0.#} water-supply fixture units (IRC Table P2903.6), {carried.Downstream / 304.8:0} ft to the last fixture";
            var water = UsWaterOf(document, system);
            return UsRequired(document, pipe, network, water) is { } need
                ? $"{units} - Appendix AP asks {IrcWaterSizing.Name(need.Inches)} (Table AP201.1, {water.Where})"
                : carried.FixtureUnits > 0 && water.Beyond is { } beyond ? $"{units} - {beyond}" : units;
        }
        var said = $"{carried.Load:0} loading units, the most on one draw-off {carried.Highest:0}, {Units.Metres(carried.Downstream, "0.0")} to the last";
        var type = document.FindType<PipeType>(pipe.TypeId);
        return Required(document, pipe, network) is { } column && type is not null
            ? $"{said} - BS EN 806-3 {PipeSizes.TableOf(type.Material)} asks {column.Size.Name}"
            : carried.Load > 0 ? $"{said} - more than BS EN 806-3's Table 3 goes to" : said;
    }


    /// <summary>
    /// Every water pipe sized as BS EN 806-3 asks - in the US, as the IRC's Appendix AP does - and
    /// every heating pipe for its flow and pressure drop: its new outside diameter, where it changes.
    /// </summary>
    public static IReadOnlyList<(Pipe Pipe, double Diameter)> Sized(BimDocument document, IEnumerable<MepSystem> systems)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var sized = new List<(Pipe, double)>();
        foreach (var system in systems.Where(system => IsWater(system.Kind) || system.Kind == SystemKind.Heating))
        {
            var network = Trace(document, system);
            var water = us && IsWater(system.Kind) ? UsWaterOf(document, system) : null;
            foreach (var pipe in network.Loads.Keys)
            {
                var size = system.Kind == SystemKind.Heating ? HeatingPipes.Required(document, pipe, network)
                    : water is not null ? UsRequired(document, pipe, network, water)?.Size
                    : Required(document, pipe, network)?.Size;
                if (size is not null && Math.Abs(size.Outside - pipe.Diameter) > 0.01) sized.Add((pipe, size.Outside));
            }
        }

        return sized;
    }

    /// <summary>What is wanting in the pipes: a pipe too small, pipes nothing reaches, fittings no pipe reaches, a hot water run too long.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();

        // A size its pipe does not come in - its type changed under it.
        foreach (var pipe in document.All<Pipe>())
            if (document.FindType<PipeType>(pipe.TypeId) is { } pipeType && PipeSizes.Of(pipeType.Material) is { Count: > 0 } sizes && sizes.All(size => Math.Abs(size.Outside - pipe.Diameter) > 0.5))
                yield return new MepProblem(pipe, $"{Units.Millimetres(pipe.Diameter, "0.#")} is not a size {pipeType.Name} comes in - the nearest is {PipeSizes.Nearest(pipeType.Material, pipe.Diameter).Name}; Size Pipes, or a size from its list");

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
                    if (carried.Load <= 0 || document.FindType<PipeType>(pipe.TypeId) is not { } type || PipeSizes.Table(type.Material).Count == 0) continue;
                    var required = Required(document, pipe, network);
                    if (required is null)
                        yield return new MepProblem(pipe, $"carries {carried.Load:0} loading units - more than BS EN 806-3's {PipeSizes.TableOf(type.Material)} goes to");
                    else if (pipe.Diameter < required.Size.Outside - 0.01)
                        yield return new MepProblem(pipe,
                            $"too small at {pipe.SizeName(type)}: it carries {carried.Load:0} loading units, the most on one draw-off {carried.Highest:0}, {Units.Metres(carried.Downstream, "0.0")} to the last - BS EN 806-3 {PipeSizes.TableOf(type.Material)} asks {required.Size.Name}");
                }

            // In the US, each pipe against Appendix AP; the water past its table, or over the 80 psi P2903.3.2 allows, said once.
            if (IsWater(system.Kind) && us)
            {
                var water = UsWaterOf(document, system);
                if (ReferenceEquals(system, water.Supply) && water.Static > IrcWaterSizing.MostPressure && MepSystems.SourceOf(document, system) is { } comesIn)
                    yield return new MepProblem(comesIn, $"the water comes in at {water.Static:0} psi - IRC P2903.3.2 asks a pressure-reducing valve where it is over 80");
                if (water.Beyond is { } beyond && network.Loads.Values.Any(load => load.FixtureUnits > 0))
                    yield return new MepProblem((Elements.Element?)MepSystems.SourceOf(document, system) ?? network.Loads.Keys.First(), $"{system.Name} cannot be sized by the IRC's Appendix AP: {beyond}");
                foreach (var (pipe, carried) in network.Loads)
                    if (document.FindType<PipeType>(pipe.TypeId) is { } type && UsRequired(document, pipe, network, water) is { } need && pipe.Diameter < need.Size.Outside - 0.01)
                        yield return new MepProblem(pipe,
                            $"too small at {pipe.SizeName(type)}: it carries {carried.FixtureUnits:0.#} water-supply fixture units - IRC Appendix AP asks {IrcWaterSizing.Name(need.Inches)} (Table AP201.1, {water.Where})");
            }

            if (system.Kind == SystemKind.Heating)
                foreach (var problem in HeatingPipes.Problems(document, network))
                    yield return problem;

            if (system.Kind == SystemKind.HotWater && us)
                foreach (var (fitting, run) in network.RunTo.Where(entry => entry.Value > IrcHotWaterRun))
                    yield return new MepProblem(fitting, $"its hot water runs {run / 304.8:0} ft from its source - IRC P2905.3 keeps it within 100 ft");
        }
    }
}
