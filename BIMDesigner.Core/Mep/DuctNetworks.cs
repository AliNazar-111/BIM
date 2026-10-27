using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>What a length of duct carries: the air beyond it at the high rate, l/s; how far it is from the unit along the ducts; the next duct toward the unit; its two ends; how many valves and terminals are beyond it.</summary>
public sealed record DuctLoad(double Flow, double FromSource, Duct? Toward, Point3D Near, Point3D Far)
{
    public int Terminals { get; init; }
}

/// <summary>A ventilation system's ducts traced from its unit: what each carries, the ducts nothing reaches, the valves no duct reaches.</summary>
public sealed record DuctNetwork(MepSystem System, IReadOnlyDictionary<Duct, DuctLoad> Loads, IReadOnlyList<Duct> Unreached, IReadOnlyList<Component> NotDucted)
{
    /// <summary>The duct at each valve's spigot.</summary>
    public IReadOnlyDictionary<Component, Duct> FirstDuct { get; init; } = new Dictionary<Component, Duct>();

    /// <summary>The air each valve or terminal on it carries at the high rate, l/s.</summary>
    public IReadOnlyDictionary<Component, double> Draws { get; init; } = new Dictionary<Component, double>();
}

/// <summary>The pressure the air loses on its way between the unit and one valve, Pa: along the ducts, at their bends and tees, and through the valve; and how far it goes, mm.</summary>
public sealed record DuctPath(Component Terminal, double Friction, double Fittings, double Device, double Length)
{
    public double Pressure => Friction + Fittings + Device;
}

/// <summary>A system's index run - the path that loses the most - what it loses and how, the longest run, and the air the system moves.</summary>
public sealed record DuctIndex(double Pressure, double Friction, double Fittings, double Device, double Longest, double Flow, Component? Furthest);

/// <summary>Where a duct runs: inside the heated home, in a cold loft, or elsewhere outside it.</summary>
public enum DuctPlace
{
    Heated,
    Loft,
    Unheated
}

/// <summary>
/// Ducts as networks, traced as pipes are: joined where their ends meet, or meet a fitting's spigot,
/// within 10 mm, and followed out from the unit. Each carries the air of the valves beyond it at the
/// high rate. The pressure the air loses: along each duct, Darcy-Weisbach's, its friction factor Swamee
/// and Jain's fit to Colebrook, for air at 1.2 kg/m³ and 1.81 x 10⁻⁵ Pa·s; at each bend and tee, its
/// velocity pressure times ASHRAE's coefficient (Principles of HVAC, Fitting Loss Coefficients, after
/// Idelchik and Locklin) - a smooth round bend at r/D 1.5 0.15 at 90°, a flexible one's at r/D 1.0 0.22,
/// a smooth rectangular one's at r/W 1.0 by its height over width, each by the angle's factor; a
/// supply's tees diverging (5-10, 5-23), an extract's converging (5-3); and through each valve, its
/// maker's loss at its rated airflow, as the square of the air through it. The designed static
/// pressure - the run that loses the most - is kept within Approved Document F's Table 1.8 (2026
/// edition, in force from 24 March 2027): 60 Pa for a system of up to 37 l/s, 110 up to 53, 160 above;
/// and its unit's available pressure. Size Ducts sizes by equal friction to what is left of that limit
/// once the fittings and valves have theirs, over the longest run - and in the UK no faster than 2.5 m/s,
/// as installers keep a home's ducts for quiet (a practice, not a regulation); in the US no faster than
/// ACCA Manual D's recommended velocities (Table 3-1, as AHRI's copy of ACCA's checklist gives it).
/// Flexible duct is for the last connection to a valve, 1.5 m at most (Approved Document F 1.77).
/// Intake and exhaust ducts are insulated - 50 mm at 0.04 W/mK where they run in the heated home, 25 in
/// a cold loft or elsewhere outside it (Approved Document F 2026, 1.83); in the US, ducts outside the
/// conditioned space R-8 in an attic, R-6 elsewhere - R-6 and R-4.2 under 3 in (IECC R403.3.1, as reported).
/// Where a duct passes through a compartment wall or floor it keeps it fire-separating with a fire damper,
/// a fire and smoke damper, or fire-resisting duct (Approved Document B 9.12) - never a damper on a
/// kitchen's extract (9.15); a damper resisting fire for 60 minutes at least, or as long as the wall or
/// floor (9.20, 9.21); a system serving homes on both sides with dampers released both by smoke detection
/// and by heat (9.14), and any fire and smoke damper with a smoke detector in its ductwork (9.8, 9.22).
/// Through a house's protected stair enclosure a duct is rigid steel (2.9 b), and the stair's ventilation
/// serves nothing else (2.9 c).
/// </summary>
public static class DuctNetworks
{
    public const double Touch = 10;
    public const double AirDensity = 1.2, AirViscosity = 1.81e-5;
    public const double LongestFlexible = 1500;

    /// <summary>The fastest air ducts are sized for in the UK, m/s: what installers keep a home's ducts to for quiet - a practice, not a regulation.</summary>
    public const double QuietVelocity = 2.5;

    /// <summary>A grille's or outlet's loss where its maker gives none, Pa: ACCA Manual D's standard 0.03 in. w.c. (used in the US).</summary>
    public const double ManualDDevice = 0.03 * 249.09;

    /// <summary>Metres a second in a foot a minute.</summary>
    private const double Fpm = 0.00508;

    public static IReadOnlyList<Duct> DuctsOf(BimDocument document, MepSystem system) =>
        document.All<Duct>().Where(duct => duct.SystemId == system.Id).ToList();

    /// <summary>How fast air goes through a duct carrying so much (l/s), m/s, and the pressure it loses a metre, Pa/m.</summary>
    public static (double Velocity, double Gradient) Friction(double flow, DuctSize size, DuctMaterial material)
    {
        if (flow <= 0 || size.Area <= 0) return (0, 0);
        var velocity = flow / 1000 / (size.Area / 1e6);
        var diameter = size.Hydraulic / 1000;
        var reynolds = AirDensity * velocity * diameter / AirViscosity;
        var friction = reynolds < 2300
            ? 64 / reynolds
            : 0.25 / Math.Pow(Math.Log10(DuctSizes.Roughness(material) / 1000 / (3.7 * diameter) + 5.74 / Math.Pow(reynolds, 0.9)), 2);
        return (velocity, friction * AirDensity * velocity * velocity / 2 / diameter);
    }

    /// <summary>Air's velocity pressure at a speed, Pa.</summary>
    public static double VelocityPressure(double velocity) => AirDensity * velocity * velocity / 2;

    /// <summary>The most designed static pressure Approved Document F (2026, Table 1.8) lets a system of so much air have, Pa.</summary>
    public static double PressureLimit(double flow) => flow <= 37.5 ? 60 : flow <= 53.5 ? 110 : 160;

    // ---- ASHRAE's fitting coefficients ---------------------------------------

    private static double Interpolate(double[] at, double[] values, double x)
    {
        if (x <= at[0]) return values[0];
        for (var i = 1; i < at.Length; i++)
            if (x <= at[i]) return values[i - 1] + (values[i] - values[i - 1]) * (x - at[i - 1]) / (at[i] - at[i - 1]);
        return values[^1];
    }

    private static readonly double[] Angles = { 0, 20, 30, 45, 60, 75, 90, 110, 130, 150, 180 };
    private static readonly double[] AngleFactors = { 0, 0.31, 0.45, 0.60, 0.78, 0.90, 1.00, 1.13, 1.20, 1.28, 1.40 };
    private static readonly double[] Aspects = { 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0, 4.0, 5.0, 6.0, 8.0 };
    private static readonly double[] RectangularBend = { 0.27, 0.25, 0.23, 0.21, 0.19, 0.18, 0.18, 0.19, 0.20, 0.21, 0.21 };

    /// <summary>A bend's loss coefficient: ASHRAE 3-1 for a smooth round bend - r/D 1.5 0.15 at 90°, a flexible duct's at r/D 1.0 0.22 - or 3-5 for a smooth rectangular one at r/W 1.0, by its angle's factor.</summary>
    public static double BendCoefficient(DuctSize size, DuctMaterial material, double angle, bool horizontal)
    {
        var factor = Interpolate(Angles, AngleFactors, angle);
        if (size.Shape == DuctShape.Round) return factor * (material == DuctMaterial.Flexible ? 0.22 : 0.15);
        var aspect = horizontal ? size.Height / size.Width : size.Width / size.Height;
        return factor * Interpolate(Aspects, RectangularBend, aspect);
    }

    private static readonly double[] DivergingMainRatio = { 0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.8, 1.0 };
    private static readonly double[] DivergingMain = { 0.40, 0.32, 0.26, 0.20, 0.14, 0.10, 0.06, 0.02, 0 };
    private static readonly double[] DivergingBranchRatio = { 0, 0.2, 0.4, 0.6, 0.8, 1.0, 1.2, 1.4, 1.6, 1.8, 2.0 };
    private static readonly double[] DivergingBranch = { 1.0, 0.85, 0.74, 0.62, 0.52, 0.42, 0.36, 0.32, 0.32, 0.37, 0.52 };
    private static readonly double[] Tenths = { 0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 };
    private static readonly double[] ConvergingMain = { 0, 0.16, 0.27, 0.38, 0.46, 0.53, 0.57, 0.59, 0.60, 0.59, 0.55 };
    private static readonly double[] BranchAreas = { 0.1, 0.2, 0.3, 0.4, 0.6, 0.8, 1.0 };
    private static readonly double[][] ConvergingBranch =
    {
        new[] { -1.0, -1.0, -1.0, -0.90, -0.90, -0.90, -0.90 },
        new[] { 0.40, -0.37, -0.51, -0.46, -0.50, -0.51, -0.52 },
        new[] { 3.8, 0.72, 0.17, -0.02, -0.14, -0.18, -0.24 },
        new[] { 9.2, 2.3, 1.0, 0.44, 0.21, 0.11, -0.08 },
        new[] { 16, 4.3, 2.1, 0.94, 0.54, 0.40, 0.32 },
        new[] { 26, 6.8, 3.2, 1.1, 0.66, 0.49, 0.42 },
        new[] { 37, 9.7, 4.7, 1.6, 0.92, 0.69, 0.57 },
        new[] { 43, 13, 6.3, 2.1, 1.2, 0.88, 0.72 },
        new[] { 65, 17, 7.9, 2.7, 1.5, 1.1, 0.86 },
        new[] { 82, 21, 9.7, 3.4, 1.8, 1.2, 0.99 },
        new[] { 101, 26, 12, 4.0, 2.1, 1.4, 1.1 }
    };

    /// <summary>A diverging round tee's coefficient on the common velocity pressure: the straight run's by its velocity over the common's (ASHRAE 5-23), the branch's by its velocity over the straight run's (5-10).</summary>
    public static double DivergingTee(bool branch, double ratio) =>
        branch ? Interpolate(DivergingBranchRatio, DivergingBranch, ratio) : Interpolate(DivergingMainRatio, DivergingMain, ratio);

    /// <summary>A converging round tee's coefficient on the common velocity pressure (ASHRAE 5-3): the straight run's by its share of the air, the branch's by its share and its area over the common's.</summary>
    public static double ConvergingTee(bool branch, double share, double areas)
    {
        if (!branch) return Interpolate(Tenths, ConvergingMain, share);
        var row = Tenths.Select((_, i) => Interpolate(BranchAreas, ConvergingBranch[i], areas)).ToArray();
        return Interpolate(Tenths, row, share);
    }

    // ---- tracing -------------------------------------------------------------

    private static double Distance(Point3D a, Point3D b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>What a fitting on a system draws at the high rate, l/s: a valve its share of its room's air, an outside terminal all the unit moves.</summary>
    private static double Draw(BimDocument document, Component member, IReadOnlyDictionary<Component, double> flows) =>
        flows.TryGetValue(member, out var flow) ? flow
        : document.FindType<ComponentType>(member.TypeId)?.Form == ComponentForm.OutsideTerminal
            ? Math.Max(flows.Where(entry => document.FindType<ComponentType>(entry.Key.TypeId)?.Form == ComponentForm.SupplyTerminal).Sum(entry => entry.Value),
                       flows.Where(entry => document.FindType<ComponentType>(entry.Key.TypeId)?.Form == ComponentForm.ExtractTerminal).Sum(entry => entry.Value))
            : 0;

    /// <summary>A system's ducts traced from its unit: what each carries and how far everything is along them.</summary>
    public static DuctNetwork Trace(BimDocument document, MepSystem system, IReadOnlyDictionary<Component, double>? flows = null)
    {
        flows ??= Ventilation.TerminalFlows(document);
        var ducts = DuctsOf(document, system);
        var points = new List<Point3D>();
        int? Find(Point3D at)
        {
            for (var i = 0; i < points.Count; i++)
                if (Distance(points[i], at) <= Touch) return i;
            return null;
        }

        int Node(Point3D at)
        {
            if (Find(at) is { } found) return found;
            points.Add(at);
            return points.Count - 1;
        }

        var links = new Dictionary<int, List<(Duct Duct, int To)>>();
        foreach (var duct in ducts)
        {
            var (start, end) = duct.Ends(document);
            var (a, b) = (Node(start), Node(end));
            if (a == b) continue;
            (links.TryGetValue(a, out var fromA) ? fromA : links[a] = new()).Add((duct, b));
            (links.TryGetValue(b, out var fromB) ? fromB : links[b] = new()).Add((duct, a));
        }

        var draws = new Dictionary<int, (double Flow, int Count)>();
        var at = new Dictionary<Component, int>();
        var drawn = new Dictionary<Component, double>();
        var members = MepSystems.MembersOf(document, system);
        foreach (var member in members)
            foreach (var placed in Connectors.Placed(document, member).Where(placed => placed.Connector.Kind == system.Kind && !placed.Connector.Feeds && placed.Connector.Domain == ConnectorDomain.Duct))
            {
                if (Find(placed.At) is not { } node || !at.TryAdd(member, node)) continue;
                var flow = Draw(document, member, flows);
                drawn[member] = flow;
                var known = draws.GetValueOrDefault(node);
                draws[node] = (known.Flow + flow, known.Count + 1);
            }

        var starts = MepSystems.SourceOf(document, system) is { } source
            ? Connectors.Placed(document, source).Where(placed => placed.Connector.Kind == system.Kind).Select(placed => Find(placed.At)).OfType<int>().Distinct().ToList()
            : new List<int>();
        var parent = new Dictionary<int, (Duct Duct, int From)>();
        var distance = new Dictionary<int, double>();
        var order = new List<int>();
        var queue = new Queue<int>();
        foreach (var start in starts.Where(start => distance.TryAdd(start, 0))) queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            order.Add(node);
            foreach (var (duct, to) in links.GetValueOrDefault(node) ?? new List<(Duct, int)>())
            {
                if (distance.ContainsKey(to)) continue;
                distance[to] = distance[node] + duct.Length;
                parent[to] = (duct, node);
                queue.Enqueue(to);
            }
        }

        var behind = new Dictionary<int, (double Flow, int Count)>();
        var loads = new Dictionary<Duct, DuctLoad>();
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            var own = draws.GetValueOrDefault(node);
            var beyond = behind.GetValueOrDefault(node);
            var all = (Flow: beyond.Flow + own.Flow, Count: beyond.Count + own.Count);
            if (!parent.TryGetValue(node, out var up)) continue;
            loads[up.Duct] = new DuctLoad(all.Flow, distance[up.From], parent.TryGetValue(up.From, out var on) ? on.Duct : null, points[up.From], points[node]) { Terminals = all.Count };
            var above = behind.GetValueOrDefault(up.From);
            behind[up.From] = (above.Flow + all.Flow, above.Count + all.Count);
        }

        // With nothing feeding it yet, a fitting a duct meets is ducted.
        var reached = at.Where(entry => starts.Count == 0 || distance.ContainsKey(entry.Value)).Select(entry => entry.Key).ToHashSet();
        var first = new Dictionary<Component, Duct>();
        foreach (var (member, node) in at)
            if (parent.TryGetValue(node, out var up)) first[member] = up.Duct;
        return new DuctNetwork(system, loads, ducts.Where(duct => !loads.ContainsKey(duct)).ToList(),
            ducts.Count == 0 ? new List<Component>() : members.Where(member => !reached.Contains(member)).ToList()) { FirstDuct = first, Draws = drawn };
    }

    // ---- pressure ------------------------------------------------------------

    private static DuctSize SizeOf(BimDocument document, Duct duct, IReadOnlyDictionary<Duct, DuctSize>? sizes) =>
        sizes is not null && sizes.TryGetValue(duct, out var size) ? size : duct.Size(document);

    private static DuctMaterial MaterialOf(BimDocument document, Duct duct) => document.FindType<DuctType>(duct.TypeId)?.Material ?? DuctMaterial.GalvanisedSteel;

    /// <summary>How fast a duct's air goes and what it loses a metre; null for one not traced or carrying nothing.</summary>
    public static (double Flow, double Velocity, double Gradient)? Carried(BimDocument document, Duct duct, DuctNetwork network, IReadOnlyDictionary<Duct, DuctSize>? sizes = null)
    {
        if (!network.Loads.TryGetValue(duct, out var load) || load.Flow <= 0) return null;
        var (velocity, gradient) = Friction(load.Flow, SizeOf(document, duct, sizes), MaterialOf(document, duct));
        return (load.Flow, velocity, gradient);
    }

    private static (double X, double Y, double Z) Direction(DuctLoad load)
    {
        var (dx, dy, dz) = (load.Far.X - load.Near.X, load.Far.Y - load.Near.Y, load.Far.Z - load.Near.Z);
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return length < 1e-9 ? (0, 0, 0) : (dx / length, dy / length, dz / length);
    }

    private static double Angle((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Acos(Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1, 1)) * 180 / Math.PI;

    /// <summary>What the air loses where a duct leaves the one before it toward the unit, Pa: a bend, or a tee - its run straight on or its branch.</summary>
    public static double JunctionLoss(BimDocument document, DuctNetwork network, Duct duct, IReadOnlyDictionary<Duct, DuctSize>? sizes = null)
    {
        if (!network.Loads.TryGetValue(duct, out var load) || load.Toward is not { } toward || !network.Loads.TryGetValue(toward, out var common)) return 0;
        var siblings = network.Loads.Where(entry => entry.Value.Toward == toward && Distance(entry.Value.Near, load.Near) <= Touch).Select(entry => entry.Key).ToList();
        var into = Direction(common);
        var on = Direction(load);
        var velocity = Carried(document, duct, network, sizes)?.Velocity ?? 0;
        if (siblings.Count <= 1)
        {
            var horizontal = Math.Abs(into.Z) < 0.1 && Math.Abs(on.Z) < 0.1;
            return BendCoefficient(SizeOf(document, duct, sizes), MaterialOf(document, duct), Angle(into, on), horizontal) * VelocityPressure(velocity);
        }

        var main = siblings.OrderBy(each => Angle(into, Direction(network.Loads[each]))).First();
        var commonVelocity = Carried(document, toward, network, sizes)?.Velocity ?? 0;
        var mainVelocity = Carried(document, main, network, sizes)?.Velocity ?? 0;
        var straight = duct == main;
        double coefficient;
        if (network.System.Kind == SystemKind.ExtractAir)
        {
            // ASHRAE 5-3 reads both by the branch's share of the air: the straight run's by what joins it from the branch.
            var share = common.Flow > 0 ? (straight ? common.Flow - load.Flow : load.Flow) / common.Flow : 0;
            var areas = SizeOf(document, toward, sizes).Area > 0 ? SizeOf(document, duct, sizes).Area / SizeOf(document, toward, sizes).Area : 1;
            coefficient = ConvergingTee(!straight, share, areas);
        }
        else
            coefficient = straight
                ? DivergingTee(false, commonVelocity > 0 ? velocity / commonVelocity : 0)
                : DivergingTee(true, mainVelocity > 0 ? velocity / mainVelocity : commonVelocity > 0 ? velocity / commonVelocity : 0);
        return coefficient * VelocityPressure(commonVelocity);
    }

    /// <summary>What a valve or terminal loses at an airflow, Pa: its maker's loss at its rated airflow, as the square of the air; in the US, ACCA Manual D's 0.03 in. w.c. where none is given.</summary>
    public static double DeviceLoss(BimDocument document, Component terminal, double flow)
    {
        if (document.FindType<ComponentType>(terminal.TypeId) is not { } type) return 0;
        if (type.PressureLoss > 0 && type.RatedAirflow > 0) return type.PressureLoss * Math.Pow(flow / type.RatedAirflow, 2);
        return document.ProjectInformation.ClearanceStandard.IsUs() && type.Form is ComponentForm.SupplyTerminal or ComponentForm.ExtractTerminal ? ManualDDevice : 0;
    }

    /// <summary>Each valve's run back to the unit and what the air loses along it.</summary>
    public static IReadOnlyList<DuctPath> Paths(BimDocument document, DuctNetwork network, IReadOnlyDictionary<Duct, DuctSize>? sizes = null)
    {
        var paths = new List<DuctPath>();
        foreach (var (terminal, first) in network.FirstDuct)
        {
            if (!network.Loads.ContainsKey(first)) continue;
            double friction = 0, fittings = 0, length = 0;
            var guard = 0;
            for (Duct? on = first; on is not null && guard++ < 10_000; on = network.Loads[on].Toward)
            {
                friction += (Carried(document, on, network, sizes)?.Gradient ?? 0) * on.Length / 1000;
                fittings += JunctionLoss(document, network, on, sizes);
                length += on.Length;
            }

            paths.Add(new DuctPath(terminal, friction, fittings, DeviceLoss(document, terminal, network.Draws.GetValueOrDefault(terminal)), length));
        }

        return paths;
    }

    /// <summary>A system's index run: the path that loses the most, and what it loses along its ducts, at its fittings and through its valve.</summary>
    public static DuctIndex IndexOf(BimDocument document, DuctNetwork network, IReadOnlyDictionary<Duct, DuctSize>? sizes = null)
    {
        var paths = Paths(document, network, sizes);
        var worst = paths.OrderByDescending(path => path.Pressure).FirstOrDefault();

        // The air the unit moves: all its ducts' together; outside air one way, the intake and the exhaust each carrying it.
        var roots = network.Loads.Values.Where(load => load.Toward is null).Select(load => load.Flow).DefaultIfEmpty(0).ToList();
        var flow = network.System.Kind == SystemKind.OutsideAir ? roots.Max() : roots.Sum();
        return new DuctIndex(worst?.Pressure ?? 0, worst?.Friction ?? 0, worst?.Fittings ?? 0, worst?.Device ?? 0,
            paths.Select(path => path.Length).DefaultIfEmpty(0).Max(), flow, worst?.Terminal);
    }

    /// <summary>The designed static pressure, Pa, the longest run, mm, and the air the system moves, l/s.</summary>
    public static (double Pressure, double Longest, double Flow) Index(BimDocument document, DuctNetwork network)
    {
        var index = IndexOf(document, network);
        return (index.Pressure, index.Longest, index.Flow);
    }

    /// <summary>The static pressure a US unit is taken to have for its ducts where its own is not given, Pa: 0.25 in. w.c., what US fans are rated at.</summary>
    public const double UsRatedPressure = 0.25 * Units.PascalsPerInchOfWater;

    /// <summary>What a system's static pressure is kept to, Pa: Approved Document F's Table 1.8 in the UK; in the US its unit's available pressure, or 0.25 in. w.c.</summary>
    public static double LimitOf(BimDocument document, DuctNetwork network, double flow)
    {
        var available = MepSystems.SourceOf(document, network.System) is { } unit && document.FindType<ComponentType>(unit.TypeId) is { AvailablePressure: > 0 } type ? type.AvailablePressure : 0;
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return available > 0 ? available : UsRatedPressure;

        // In the UK Table 1.8's, or less where the unit has less to give.
        return available > 0 ? Math.Min(available, PressureLimit(flow)) : PressureLimit(flow);
    }

    /// <summary>The fastest a duct's air is sized for, m/s, and the most it may go: in the US, ACCA Manual D's recommended and maximum velocities - supply or return, trunk or branch, rigid or flex; in the UK, installers' quiet 2.5 m/s.</summary>
    public static (double Recommended, double Maximum) Velocities(BimDocument document, Duct duct, DuctLoad load)
    {
        if (!document.ProjectInformation.ClearanceStandard.IsUs()) return (QuietVelocity, double.PositiveInfinity);
        var flex = MaterialOf(document, duct) == DuctMaterial.Flexible;
        var trunk = load.Terminals > 1;
        var supply = duct.Kind != SystemKind.ExtractAir;
        var (recommended, maximum) = (supply, trunk) switch
        {
            (true, true) => flex ? (600, 700) : (700, 900),
            (true, false) => flex ? (600, 700) : (600, 900),
            (false, true) => (600, 700),
            _ => (400, 700)
        };
        return (recommended * Fpm, maximum * Fpm);
    }

    /// <summary>
    /// The size each duct of a system needs: by equal friction, the smallest of its type that loses no more
    /// a metre than the system's limit - less what its fittings and valves lose - over its longest run, and
    /// no faster than its recommended velocity; found again with the fittings at the new sizes, three times.
    /// </summary>
    public static IReadOnlyList<(Duct Duct, DuctSize Size)> Sized(BimDocument document, IEnumerable<MepSystem> systems)
    {
        var sized = new List<(Duct, DuctSize)>();
        foreach (var system in systems.Where(system => system.Kind.Domain() == ConnectorDomain.Duct))
        {
            var network = Trace(document, system);
            var sizes = network.Loads.Keys.ToDictionary(duct => duct, duct => duct.Size(document));
            for (var round = 0; round < 3; round++)
            {
                var index = IndexOf(document, network, sizes);
                if (index.Longest <= 0) break;
                var gradient = Math.Max(0.05, (LimitOf(document, network, index.Flow) - index.Fittings - index.Device) / (index.Longest / 1000));
                foreach (var (duct, load) in network.Loads)
                {
                    if (load.Flow <= 0 || document.FindType<DuctType>(duct.TypeId) is not { } type) continue;
                    var fastest = Velocities(document, duct, load).Recommended;
                    sizes[duct] = DuctSizes.Of(type).FirstOrDefault(each => Friction(load.Flow, each, type.Material) is var (velocity, loss) && loss <= gradient + 1e-9 && velocity <= fastest + 1e-9)
                                  ?? DuctSizes.Of(type)[^1];
                }
            }

            foreach (var (duct, size) in sizes)
                if (Math.Abs(size.Width - duct.Width) > 0.5 || Math.Abs(size.Height - duct.Height) > 0.5) sized.Add((duct, size));
        }

        return sized;
    }

    /// <summary>What a duct carries, as its Properties say it.</summary>
    public static string Describe(BimDocument document, Duct duct)
    {
        if (document.Find<MepSystem>(duct.SystemId) is not { } system) return "On no system yet";
        var network = Trace(document, system);
        if (!network.Loads.ContainsKey(duct))
            return MepSystems.SourceOf(document, system) is null ? $"{system.Name} has no unit feeding it" : $"Not joined to the unit feeding {system.Name}";
        if (Carried(document, duct, network) is not { } carried) return $"{Units.Metres(network.Loads[duct].FromSource, "0.0")} from the unit - no valve beyond it yet";
        var index = IndexOf(document, network);
        var limit = LimitOf(document, network, index.Flow);
        var end = system.Kind == SystemKind.OutsideAir ? "terminal" : "valve";
        var run = Units.Imperial
            ? $"{system.Name}'s furthest {end} {Units.Pascals(index.Pressure, "0")} - {index.Friction / Units.PascalsPerInchOfWater:0.00} along the ducts, {index.Fittings / Units.PascalsPerInchOfWater:0.00} at the bends and tees, {index.Device / Units.PascalsPerInchOfWater:0.00} through the {end} - of {Units.Pascals(limit, "0")} allowed"
            : $"{system.Name}'s furthest {end} {index.Pressure:0} Pa - {index.Friction:0} along the ducts, {index.Fittings:0} at the bends and tees, {index.Device:0} through the {end} - of {limit:0} allowed";
        return document.ProjectInformation.ClearanceStandard.IsUs()
            ? $"{carried.Flow / Ventilation.LitresPerCfm:0} cfm at {carried.Velocity / Fpm:0} fpm, friction {carried.Gradient * 30.48 / 249.09:0.000} in. w.c. per 100 ft; {run}"
            : $"{carried.Flow:0.0} l/s at {carried.Velocity:0.00} m/s, losing {carried.Gradient:0.00} Pa/m; {run}";
    }

    // ---- where ducts run, and what they pass through --------------------------

    /// <summary>Where a duct runs: in a heated room or a void inside the heated home; in a cold loft - over a ceiling with its insulation on it, under a roof not insulated; or elsewhere outside the heated home.</summary>
    public static DuctPlace PlaceOf(BimDocument document, Duct duct)
    {
        var (start, end) = duct.Ends(document);
        var middle = new Point3D((start.X + end.X) / 2, (start.Y + end.Y) / 2, (start.Z + end.Z) / 2);
        var plan = new Point2D(middle.X, middle.Y);
        if (RoomsOf.Containing(document, duct.LevelId, plan) is not { } room || HeatLoss.Temperature(document, room) is null) return DuctPlace.Unheated;

        var floor = RoomHeadroom.Floor(document, room);
        var covers = RoomHeadroom.Covers(document, room, room.GetBoundary(document).Polygon);
        if (RoomHeadroom.Over(document, floor, plan, covers) is not var (headroom, by) || middle.Z <= floor + headroom) return DuctPlace.Heated;
        if (by is Floor or Roof) return DuctPlace.Heated;
        if (covers.OfType<Floor>().Any(each => each.Contains(plan) && each.GetBottomElevation(document) > middle.Z)) return DuctPlace.Heated;
        var roof = covers.OfType<Roof>().FirstOrDefault(each => each.Contains(plan));
        return roof is null || SlabRules.Cold(document, roof) ? DuctPlace.Loft : DuctPlace.Heated;
    }

    /// <summary>The insulation a duct needs where it runs, mm at 0.04 W/mK, and the rule - or none.</summary>
    public static (double Thickness, string Rule)? InsulationAsked(BimDocument document, Duct duct)
    {
        var place = PlaceOf(document, duct);
        if (document.ProjectInformation.ClearanceStandard.IsUs())
        {
            if (place == DuctPlace.Heated) return null;
            var small = duct.Size(document).Hydraulic < 76;
            var r = place == DuctPlace.Loft ? small ? 6 : 8 : small ? 4.2 : 6;
            return (r / 5.678 * 0.04 * 1000, $"IECC R403.3.1 insulates a duct {(place == DuctPlace.Loft ? "in an attic" : "outside the conditioned space")} to R-{r:0.#}");
        }

        if (duct.Kind != SystemKind.OutsideAir) return null;
        return place == DuctPlace.Heated
            ? (50, "Approved Document F (2026 edition, 1.83 a) insulates an intake or exhaust duct running in the heated home with 50 mm at 0.04 W/mK, vapour sealed")
            : (25, "Approved Document F (2026 edition, 1.83 b) insulates an intake or exhaust duct in a loft or elsewhere outside the heated home with 25 mm at 0.04 W/mK");
    }

    /// <summary>Where two segments cross, and how far along the first, as a share of it.</summary>
    private static bool Cross(Point2D a, Point2D b, Point2D c, Point2D d, out Point2D at, out double t)
    {
        at = default;
        t = 0;
        var (rx, ry, sx, sy) = (b.X - a.X, b.Y - a.Y, d.X - c.X, d.Y - c.Y);
        var denominator = rx * sy - ry * sx;
        if (Math.Abs(denominator) < 1e-9) return false;
        t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / denominator;
        var u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / denominator;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        at = new Point2D(a.X + rx * t, a.Y + ry * t);
        return true;
    }

    /// <summary>The points where a duct passes through walls on its storey, and the walls.</summary>
    public static IEnumerable<(Wall Wall, Point2D At)> Crossings(BimDocument document, Duct duct)
    {
        if (duct.Upright) yield break;
        var level = document.FindLevel(duct.LevelId)?.Elevation ?? 0;
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == duct.LevelId && !wall.IsCurved))
        {
            if (!Cross(duct.Start, duct.End, wall.Start, wall.End, out var at, out var t)) continue;
            var height = level + duct.StartOffset + (duct.EndOffset - duct.StartOffset) * t;
            if (height < wall.GetBaseElevation(document) || height > wall.GetTopElevation(document)) continue;
            yield return (wall, at);
        }
    }

    /// <summary>The points where a duct passes through compartment floors - floors separating dwellings - and the floors.</summary>
    public static IEnumerable<(Floor Floor, Point2D At)> FloorCrossings(BimDocument document, Duct duct)
    {
        var (start, end) = duct.Ends(document);
        if (Math.Abs(end.Z - start.Z) < 1) yield break;
        foreach (var floor in document.All<Floor>())
        {
            if (document.FindType<SlabType>(floor.TypeId) is not { Separating: true }) continue;
            var middle = (floor.GetTopElevation(document) + floor.GetBottomElevation(document)) / 2;
            if (middle < Math.Min(start.Z, end.Z) || middle > Math.Max(start.Z, end.Z)) continue;
            var t = (middle - start.Z) / (end.Z - start.Z);
            var at = new Point2D(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t);
            if (floor.Contains(at)) yield return (floor, at);
        }
    }

    // ---- what is wanting -----------------------------------------------------

    /// <summary>What is wanting in the ducts: not joined, valves not reached, too much pressure, flexible duct misused, insulation, fire separation broken.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document, MepSystem? only = null)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var flows = Ventilation.TerminalFlows(document);
        var protectedStair = Doors.HasHighStorey(document);
        var stairRooms = protectedStair ? Doors.StairRooms(document) : new HashSet<Room>();
        var detectors = document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.DuctSmokeDetector).ToList();
        foreach (var system in document.All<MepSystem>().Where(system => system.Kind.Domain() == ConnectorDomain.Duct && (only is null || ReferenceEquals(system, only))))
        {
            var ducts = DuctsOf(document, system);
            if (ducts.Count == 0) continue;
            var network = Trace(document, system, flows);
            var members = MepSystems.MembersOf(document, system);
            if (MepSystems.SourceOf(document, system) is { } unit)
            {
                foreach (var duct in network.Unreached)
                    yield return new MepProblem(duct, $"not joined to the rest of {system.Name} - nothing reaches it from {document.FindType<ComponentType>(unit.TypeId)?.Name ?? "the unit"}");

                var index = IndexOf(document, network);
                var end = system.Kind == SystemKind.OutsideAir ? "terminal" : "valve";
                if (!us && index.Pressure > PressureLimit(index.Flow) + 0.05)
                    yield return new MepProblem(system,
                        $"its furthest {end} is {index.Pressure:0} Pa away - {index.Friction:0} along the ducts, {index.Fittings:0} at the bends and tees, {index.Device:0} through the {end} - and Approved Document F (2026 edition, from 24 March 2027, Table 1.8) keeps a system of {index.Flow:0} l/s to {PressureLimit(index.Flow):0} Pa; larger ducts, fewer bends, or shorter runs");
                if (document.FindType<ComponentType>(unit.TypeId) is { AvailablePressure: > 0 } type && index.Pressure > type.AvailablePressure + 0.05)
                    yield return new MepProblem(unit, $"its ducts on {system.Name} lose {Units.Pascals(index.Pressure, "0")} to the furthest {end} - more than the {Units.Pascals(type.AvailablePressure, "0")} it has for them");
            }

            foreach (var fitting in network.NotDucted)
                yield return new MepProblem(fitting, $"on {system.Name}, but no duct reaches it yet");

            // Each valve's final connection: the flexible duct from it back to where it meets rigid duct or another valve's.
            var finals = new Dictionary<Duct, (Duct First, double Length)>();
            foreach (var first in network.FirstDuct.Values.Distinct())
            {
                var chain = new List<Duct>();
                for (Duct? piece = first; piece is not null && MaterialOf(document, piece) == DuctMaterial.Flexible && !chain.Contains(piece)
                                        && network.Loads.TryGetValue(piece, out var carried) && carried.Terminals <= 1; piece = carried.Toward)
                    chain.Add(piece);
                foreach (var piece in chain) finals[piece] = (first, chain.Sum(each => each.Length));
            }

            foreach (var duct in ducts)
            {
                // Flexible duct: only the last connection to a valve, and short - its pieces together. Without a unit nothing says where the valves' connections are.
                // Approved Document F's: the US's flexible air duct runs as long as it is sized for.
                if (!us && MaterialOf(document, duct) == DuctMaterial.Flexible && MepSystems.SourceOf(document, system) is not null)
                {
                    if (!finals.TryGetValue(duct, out var final))
                    {
                        if (network.Loads.ContainsKey(duct))
                            yield return new MepProblem(duct, "flexible duct away from a valve - Approved Document F (1.77) keeps flexible duct to the final connection");
                    }
                    else if (final.First == duct && final.Length > LongestFlexible + 0.5)
                        yield return new MepProblem(duct, $"flexible duct {Units.Metres(final.Length, "0.00")} long to its valve - Approved Document F (1.77) keeps it to 1.5 m");
                }

                // Too fast for ACCA Manual D.
                if (us && network.Loads.TryGetValue(duct, out var load) && Carried(document, duct, network) is { } carried && carried.Velocity > Velocities(document, duct, load).Maximum + 1e-6)
                    yield return new MepProblem(duct, $"its air goes at {carried.Velocity / Fpm:0} fpm - ACCA Manual D (Table 3-1) keeps a {(duct.Kind == SystemKind.ExtractAir ? "return" : "supply")} {(load.Terminals > 1 ? "trunk" : "branch")} to {Velocities(document, duct, load).Maximum / Fpm:0} fpm");

                // Insulated as where it runs asks.
                if (InsulationAsked(document, duct) is var (thickness, rule) && duct.Insulation < thickness - 0.5)
                    yield return new MepProblem(duct, $"{(duct.Insulation > 0 ? $"insulated with {Units.Millimetres(duct.Insulation, "0")}" : "not insulated")} - {rule}");
            }

            if (us) continue;

            // Through a compartment wall or floor, or a protected stair's enclosure.
            var kitchen = members.Any(member =>
                RoomsOf.Containing(document, member.LevelId, member.Location) is { } room && room.GetBoundary(document) is { IsEnclosed: true } outline
                && Ventilation.UseOf(document, room, outline.Polygon) == RoomUse.Kitchen);
            var smokeDampers = false;
            foreach (var duct in ducts)
            {
                var separating = Crossings(document, duct).Where(crossing => document.GetWallType(crossing.Wall)?.Separating == true)
                    .Select(crossing => (What: "compartment wall", Rating: document.GetWallType(crossing.Wall)!.FireRating, BothSides: OnBothSides(crossing.Wall)))
                    .Concat(FloorCrossings(document, duct).Select(crossing => (What: "compartment floor", Rating: document.FindType<SlabType>(crossing.Floor.TypeId)!.FireRating, BothSides: OnBothFloors(crossing.Floor))))
                    .ToList();
                foreach (var (what, rating, bothSides) in separating)
                {
                    if (duct.FireProtection == FireProtection.None)
                    {
                        yield return new MepProblem(duct,
                            $"it passes through a {what} unprotected - Approved Document B (9.12) keeps it fire-separating with a fire damper, a fire and smoke damper, or fire-resisting duct or enclosure");
                        continue;
                    }

                    var damper = duct.FireProtection is FireProtection.FireDamper or FireProtection.FireAndSmokeDamper;
                    if (damper && kitchen && system.Kind == SystemKind.ExtractAir)
                        yield return new MepProblem(duct,
                            "a damper in a kitchen's extract - Approved Document B (9.15) has a kitchen's extract duct protected by fire-resisting duct or an enclosure, as grease clogs dampers");
                    var minutes = Math.Max(60, Doors.FireMinutes(rating));
                    if (damper && duct.DamperRating < minutes)
                        yield return new MepProblem(duct,
                            $"its damper is rated {duct.DamperRating} minutes - Approved Document B (9.20, 9.21) asks 60 at least, or as long as the {what} resists fire: {minutes}");
                    if (duct.FireProtection == FireProtection.FireDamper && bothSides)
                        yield return new MepProblem(duct,
                            $"it serves homes on both sides of the {what} through a fire damper - Approved Document B (9.14) has the damper released both by smoke detection and by heat: a fire and smoke damper");
                    if (duct.FireProtection == FireProtection.FireAndSmokeDamper) smokeDampers = true;
                }

                if (protectedStair && MaterialOf(document, duct) != DuctMaterial.GalvanisedSteel
                    && Crossings(document, duct).Any(crossing => stairRooms.Any(room => room.LevelId == crossing.Wall.LevelId && room.GetBoundary(document).BoundingWalls.Contains(crossing.Wall))))
                    yield return new MepProblem(duct,
                        "it passes through the protected stair's enclosure - Approved Document B (2.9 b) has a duct through it of rigid steel, fire-stopped where it passes");
            }

            // A fire and smoke damper closes on smoke detected in its ductwork.
            if (smokeDampers && !detectors.Any(detector => ducts.Any(duct => duct.LevelId == detector.LevelId
                    && Line2D.DistanceFromSegment(detector.Location, duct.Start, duct.End) <= duct.Width / 2 + 300)))
                yield return new MepProblem(system,
                    "it has fire and smoke dampers but no smoke detector in its ductwork to close them - Approved Document B (9.8, 9.22) has them operated by smoke detection to BS EN 54-7");

            // The protected stair's ventilation serving nothing else.
            if (protectedStair)
            {
                var served = members.Select(member => RoomsOf.Containing(document, member.LevelId, member.Location)).OfType<Room>().Distinct().ToList();
                if (served.Any(stairRooms.Contains) && served.Any(room => !stairRooms.Contains(room)))
                    yield return new MepProblem(system,
                        "it serves the protected stair and other rooms - Approved Document B (2.9 c) gives a ventilation duct to or from a protected stairway nothing else to serve");
            }

            bool OnBothSides(Wall wall) =>
                members.Where(member => member.LevelId == wall.LevelId)
                    .Select(member => Math.Sign((wall.End.X - wall.Start.X) * (member.Location.Y - wall.Start.Y) - (wall.End.Y - wall.Start.Y) * (member.Location.X - wall.Start.X)))
                    .Where(sign => sign != 0).Distinct().Count() > 1;

            bool OnBothFloors(Floor floor)
            {
                var middle = (floor.GetTopElevation(document) + floor.GetBottomElevation(document)) / 2;
                return members.Select(member => Math.Sign((document.FindLevel(member.LevelId)?.Elevation ?? 0) + member.Elevation - middle)).Where(sign => sign != 0).Distinct().Count() > 1;
            }
        }
    }
}
