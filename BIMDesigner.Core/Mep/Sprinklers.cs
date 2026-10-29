using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// What a sprinkler system asks of its supply when one room's sprinklers open: the room; the sprinklers
/// that flow and each one's flow, l/min; the flow in all, l/min; the pressure it asks at the valve set,
/// bar, and the sprinkler furthest from it in pressure; and what each pipe then carries, l/min, and
/// loses along it and its fittings, bar.
/// </summary>
public sealed record SprinklerDemand(
    Room? Room,
    IReadOnlyList<Component> Flowing,
    double Each,
    double Flow,
    double Pressure,
    Component? Remote,
    IReadOnlyDictionary<Pipe, (double Flow, double Loss)> Pipes);

/// <summary>
/// A home's fire sprinklers: where they are wanted, what each covers, and their pipes sized to give the
/// sprinklers that open in a fire what their listing asks.
///
/// Every room of a home sprinklered throughout has its sprinklers - in the US as the IRC has it
/// (P2904.1.1): all but a bathroom of 55 sq ft or less, a closet or pantry of 24 sq ft or less no more
/// than 3 ft across, and a garage, carport, porch or mud room; in England, as BS 9251 is commonly read
/// (not seen at first hand), all but a bathroom under 5 m² and a cupboard under 2 m². Each sprinkler
/// covers the square its listing gives it - every point of the floor within half its side of one, across
/// and along - no more than 400 sq ft in the US (P2904.2.4.1). In the US too, Table P2904.2.2's distances
/// from what gives off heat - a range or oven 9 in, a water heater or boiler 3 in, the front of a fireplace
/// 36 in, a stove 12 in, a light 3 in - and an intermediate temperature sprinkler (175 to 225 °F) closer than
/// its upper figure; every sprinkler rated 135 to 225 °F (P2904.2.1); and a pendent sprinkler within 3 ft
/// of a ceiling light obstructed (P2904.2.4.2.1).
///
/// The design: in the room with one sprinkler, that one open; in a room with two or more, two, each at the
/// highest flow any of the room's asks (P2904.4.2) - in England BS 9251's design of one or two sprinklers
/// the same way - and the room asking most of the supply is the one it has to meet. Each sprinkler is
/// given its listed flow at the pressure its K-factor asks for it, 7 psi (0.48 bar) at least, as NFPA 13
/// lets none run at less; each pipe loses what Hazen-Williams gives, C 150 for copper and plastic, 120 for
/// steel, its elbows and tees counted as NFPA 13's equivalent lengths of steel pipe, scaled for C 150 as it
/// scales them; and the height of the sprinkler over the valve set costs 0.0981 bar a metre (0.433 psi a
/// foot). The supply is the valve set's - a pump set's duty, or the main's - or in the US, where none is
/// given, the project's water pressure. It runs for 10 minutes; in the US 7 for a home of one storey under
/// 2,000 sq ft (P2904.5.2).
/// </summary>
public static class Sprinklers
{
    /// <summary>A K-factor in l/min/bar½ for one in gpm/psi½.</summary>
    public const double KPerUsK = 14.4163;

    /// <summary>Psi in a bar.</summary>
    public const double PsiPerBar = 14.50377;

    /// <summary>The pressure a metre of water stands at, bar: 0.433 psi a foot.</summary>
    public const double BarPerMetre = 0.0981;

    /// <summary>The least pressure a sprinkler runs at, bar: 7 psi.</summary>
    public const double LeastPressure = 7 / PsiPerBar;

    /// <summary>The most one sprinkler may cover in the US, m²: 400 sq ft (P2904.2.4.1).</summary>
    public const double MostCoverage = 400 * 0.09290304;

    /// <summary>A pendent sprinkler within this of a ceiling light is obstructed in the US, mm: 3 ft (P2904.2.4.2.1).</summary>
    public const double LightClearance = 914.4;

    private const double SquareFoot = 0.09290304;

    /// <summary>Whether a fitting is a sprinkler.</summary>
    public static bool IsSprinkler(BimDocument document, Component component) =>
        document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.Sprinkler;

    /// <summary>The sprinklers in the model.</summary>
    public static IReadOnlyList<Component> All(BimDocument document) =>
        document.All<Component>().Where(component => IsSprinkler(document, component)).ToList();

    /// <summary>Whether the home's sprinklers are being designed: sprinklered throughout, or a sprinkler in the model.</summary>
    public static bool Designed(BimDocument document) =>
        document.ProjectInformation.Sprinklered || document.All<Component>().Any(component => IsSprinkler(document, component));

    /// <summary>The flow a sprinkler's listing asks, l/min: its own, or a residential pendent's usual 13 gpm.</summary>
    public static double ListedFlow(ComponentType type) => type.FlowRate > 0 ? type.FlowRate : 13 * Units.LitresPerGallon;

    /// <summary>The side of the square a sprinkler covers, mm: its listing's, or a residential pendent's usual 16 ft.</summary>
    public static double CoverageOf(ComponentType type) => type.Coverage > 0 ? type.Coverage : 16 * Units.MillimetresPerFoot;

    /// <summary>The pressure a sprinkler asks to give a flow, bar: its K-factor's, never less than 7 psi.</summary>
    public static double PressureFor(ComponentType type, double flow) =>
        type.KFactor > 0 ? Math.Max(LeastPressure, Math.Pow(flow / type.KFactor, 2)) : LeastPressure;

    /// <summary>Hazen-Williams' C for a pipe: 150 for copper and plastic, 120 for steel.</summary>
    public static double C(PipeMaterial material) => material.Like() == PipeMaterial.GalvanisedSteel ? 120 : 150;

    /// <summary>What water flowing so fast (l/min) loses a metre of a bore (mm), bar: Hazen-Williams.</summary>
    public static double Loss(double flow, double bore, double c) =>
        flow <= 0 || bore <= 0 ? 0 : 6.05e5 * Math.Pow(flow, 1.85) / (Math.Pow(c, 1.85) * Math.Pow(bore, 4.87));

    /// <summary>
    /// How much pipe an elbow or a tee counts as, m: NFPA 13's equivalent lengths of Schedule 40 steel pipe -
    /// an elbow 2 ft to 1 in, 3 ft at 1¼, 4 at 1½, 5 at 2; a tee 4, 5, 6, 8 and 10 ft - scaled by 1.51 for C 150.
    /// </summary>
    public static double Equivalent(PipeFitting fitting, double bore, double c)
    {
        if (fitting == PipeFitting.None) return 0;
        var (elbow, tee) = bore <= 22 ? (2, 4) : bore <= 28 ? (2, 5) : bore <= 36 ? (3, 6) : bore <= 42 ? (4, 8) : (5, 10);
        return (fitting == PipeFitting.Tee ? tee : elbow) * 0.3048 * (c >= 150 ? 1.51 : c >= 140 ? 1.33 : 1);
    }

    /// <summary>A pipe's bore at a size.</summary>
    private static double Bore(BimDocument document, Pipe pipe, double diameter) =>
        document.FindType<PipeType>(pipe.TypeId) is { } type && PipeSizes.Of(type.Material).FirstOrDefault(size => Math.Abs(size.Outside - diameter) < 0.01) is { } size
            ? size.Inside : diameter * 0.85;

    private static PipeMaterial MaterialOf(BimDocument document, Pipe pipe) => document.FindType<PipeType>(pipe.TypeId)?.Material ?? PipeMaterial.Copper;

    /// <summary>What the valve set's supply gives it, bar: its own, or in the US the project's water pressure; null where not known.</summary>
    public static double? Supply(BimDocument document, MepSystem system)
    {
        var source = MepSystems.SourceOf(document, system);
        if (source is not null && document.FindType<ComponentType>(source.TypeId) is { SupplyPressure: > 0 } type) return type.SupplyPressure;
        return document.ProjectInformation.ClearanceStandard.IsUs() && document.ProjectInformation.WaterPressure > 0
            ? document.ProjectInformation.WaterPressure / PsiPerBar
            : null;
    }

    /// <summary>The home's rooms, enclosed, with their outlines.</summary>
    private static List<(Room Room, IReadOnlyList<Point2D> Outline)> Rooms(BimDocument document) =>
        document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3)
            .Select(entry => (entry.Room, (IReadOnlyList<Point2D>)entry.Boundary.Polygon)).ToList();

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a room needs no sprinkler, or null where it does.</summary>
    public static string? Exempt(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var area = Math.Abs(Polygon2D.SignedArea(outline)) / 1e6;
        var pieces = document.All<Component>().Where(piece => RoomsOf.Holds(document, room, outline, piece))
            .Select(piece => document.FindType<ComponentType>(piece.TypeId)?.Form).ToList();
        var wet = Named(room, "bath", "shower", "wc", "toilet", "en-suite", "ensuite", "cloak")
                  || pieces.Any(form => form is ComponentForm.Bath or ComponentForm.Shower or ComponentForm.Toilet or ComponentForm.WallHungToilet);
        var store = Named(room, "closet", "cupboard", "pantry", "linen", "store", "wardrobe");
        var xs = outline.Select(point => point.X).ToList();
        var ys = outline.Select(point => point.Y).ToList();
        var across = Math.Min(xs.Max() - xs.Min(), ys.Max() - ys.Min());
        if (us)
        {
            if (wet && area <= 55 * SquareFoot + 1e-6) return "a bathroom of 55 sq ft or less (P2904.1.1)";
            if (store && area <= 24 * SquareFoot + 1e-6 && across <= 915) return "a closet or pantry of 24 sq ft or less, no more than 3 ft across (P2904.1.1)";
            if (Named(room, "garage", "carport", "porch", "mud")) return "a garage, carport, porch or mud room (P2904.1.1)";
            return null;
        }

        if (wet && area < 5) return "a bathroom under 5 m²";
        if (store && area < 2) return "a cupboard under 2 m²";
        return null;
    }

    /// <summary>The floor of a room no sprinkler in it covers, m²: every point within half a sprinkler's square of it, across and along.</summary>
    public static double Uncovered(BimDocument document, IReadOnlyList<Point2D> outline, IReadOnlyList<Component> heads)
    {
        var reach = heads.Select(head => (head.Location, Half: CoverageOf(document.FindType<ComponentType>(head.TypeId)!) / 2)).ToList();
        var xs = outline.Select(point => point.X).ToList();
        var ys = outline.Select(point => point.Y).ToList();
        const double step = 200;
        var bare = 0;
        for (var x = xs.Min() + step / 2; x < xs.Max(); x += step)
            for (var y = ys.Min() + step / 2; y < ys.Max(); y += step)
            {
                var at = new Point2D(x, y);
                if (!Polygon2D.Contains(outline, at)) continue;
                if (!reach.Any(head => Math.Abs(head.Location.X - x) <= head.Half + 1e-6 && Math.Abs(head.Location.Y - y) <= head.Half + 1e-6)) bare++;
            }

        return bare * step * step / 1e6;
    }

    /// <summary>
    /// What gives off heat by a sprinkler, as Table P2904.2.2 lists it: each one's name, the least a sprinkler
    /// may be from it and the distance within which it is to be an intermediate temperature one, in, and
    /// where it is - its outline in plan and its top.
    /// </summary>
    private static IEnumerable<(string Name, double Least, double Within, IReadOnlyList<Point2D> Outline, double Top)> HeatSources(BimDocument document, Guid levelId)
    {
        var level = document.FindLevel(levelId)?.Elevation ?? 0;
        foreach (var piece in document.All<Component>().Where(piece => piece.LevelId == levelId))
        {
            if (document.FindType<ComponentType>(piece.TypeId) is not { } type) continue;
            (string, double, double)? rule = type.Form switch
            {
                ComponentForm.Cooker => ("a range top", 9, 18),
                ComponentForm.OvenHousing => ("an oven", 9, 18),
                ComponentForm.Boiler => ("a boiler", 3, 6),
                ComponentForm.HotWaterCylinder => ("a water heater", 3, 6),
                ComponentForm.GasFire => ("the front of a fireplace", 36, 60),
                ComponentForm.CeilingLight or ComponentForm.WallLight => ("a light", 3, 6),
                _ => null
            };
            if (rule is not var (name, least, within)) continue;
            var outline = ComponentModel.FrameOf(piece, 0).Outline(-type.Width / 2, type.Width / 2, -type.Depth / 2, type.Depth / 2);
            yield return ($"{name} ({type.Name})", least, within, outline, ComponentHosting.BaseElevation(document, piece, type) + type.Height);
        }

        // A chimney's stove or open fire, in front of its breast.
        foreach (var chimney in document.All<Chimney>())
        {
            var fireplace = chimney.LevelId == levelId ? chimney.Fireplace : chimney.FireplaceOn(levelId);
            if (fireplace == ChimneyFireplace.None) continue;
            var front = new Vector2D(-Math.Sin(chimney.Angle * Math.PI / 180), Math.Cos(chimney.Angle * Math.PI / 180));
            var at = chimney.Location + front * (chimney.Depth / 2);
            yield return fireplace == ChimneyFireplace.Stove
                ? ("a stove", 12, 42, new[] { at }, level + 900)
                : ("the front of a fireplace", 36, 60, new[] { at }, level + 900);
        }
    }

    /// <summary>How far a point is from an outline in plan: none inside it, otherwise to its nearest side.</summary>
    private static double Apart(Point2D point, IReadOnlyList<Point2D> outline)
    {
        if (outline.Count == 1) return point.DistanceTo(outline[0]);
        if (outline.Count > 2 && Polygon2D.Contains(outline, point)) return 0;
        var nearest = double.MaxValue;
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var along = b - a;
            var t = along.Length < 1e-9 ? 0 : Math.Clamp(((point.X - a.X) * along.X + (point.Y - a.Y) * along.Y) / (along.Length * along.Length), 0, 1);
            nearest = Math.Min(nearest, point.DistanceTo(new Point2D(a.X + along.X * t, a.Y + along.Y * t)));
        }

        return nearest;
    }

    /// <summary>
    /// What one room's sprinklers ask when they open: the one, or two each at the room's highest flow, the
    /// pair asking most - with its pipes at these sizes, their own where none are given. Null for a room
    /// none of whose sprinklers the pipes reach.
    /// </summary>
    public static SprinklerDemand? Demand(BimDocument document, PipeNetwork network, IReadOnlyList<Component> heads, Room? room, IReadOnlyDictionary<Pipe, double>? sizes = null)
    {
        var reached = heads.Where(head => network.FirstPipe.TryGetValue(head, out var pipe) && network.Loads.ContainsKey(pipe)).ToList();
        if (reached.Count == 0) return null;
        var each = reached.Max(head => ListedFlow(document.FindType<ComponentType>(head.TypeId)!));
        var sets = reached.Count == 1
            ? new List<Component[]> { new[] { reached[0] } }
            : reached.SelectMany((a, i) => reached.Skip(i + 1).Select(b => new[] { a, b })).ToList();
        var source = MepSystems.SourceOf(document, network.System) is { } valve
            ? Connectors.Placed(document, valve).Where(placed => placed.Connector.Kind == SystemKind.Sprinkler && placed.Connector.Feeds).Select(placed => (double?)placed.At.Z).FirstOrDefault()
            : null;

        SprinklerDemand? worst = null;
        foreach (var set in sets)
        {
            var flows = new Dictionary<Pipe, double>();
            foreach (var head in set)
                foreach (var pipe in PipeNetworks.PathTo(network, network.FirstPipe[head]))
                    flows[pipe] = flows.GetValueOrDefault(pipe) + each;
            var pipes = new Dictionary<Pipe, (double Flow, double Loss)>();
            foreach (var (pipe, flow) in flows)
            {
                var c = C(MaterialOf(document, pipe));
                var bore = Bore(document, pipe, sizes?.GetValueOrDefault(pipe, pipe.Diameter) ?? pipe.Diameter);
                var length = pipe.Length / 1000 + Equivalent(PipeNetworks.FittingAt(document, network.Loads[pipe]), bore, c);
                pipes[pipe] = (flow, Loss(flow, bore, c) * length);
            }

            Component? remote = null;
            var most = 0.0;
            foreach (var head in set)
            {
                var type = document.FindType<ComponentType>(head.TypeId)!;
                var top = Connectors.Placed(document, head).First(placed => placed.Connector.Kind == SystemKind.Sprinkler).At.Z;
                var asks = PressureFor(type, each) + PipeNetworks.PathTo(network, network.FirstPipe[head]).Sum(pipe => pipes[pipe].Loss)
                           + (source is { } z ? (top - z) / 1000 * BarPerMetre : 0);
                if (asks > most) (most, remote) = (asks, head);
            }

            if (worst is null || most > worst.Pressure)
                worst = new SprinklerDemand(room, set, each, each * set.Length, most, remote, pipes);
        }

        return worst;
    }

    /// <summary>What each room's sprinklers on a system ask of its supply, the most demanding first.</summary>
    public static IReadOnlyList<SprinklerDemand> Demands(BimDocument document, PipeNetwork network, IReadOnlyDictionary<Pipe, double>? sizes = null)
    {
        var heads = MepSystems.MembersOf(document, network.System).Where(member => IsSprinkler(document, member)).ToList();
        var demands = new List<SprinklerDemand>();
        var placed = new HashSet<Component>();
        foreach (var (room, outline) in Rooms(document))
        {
            var inRoom = heads.Where(head => RoomsOf.Holds(document, room, outline, head)).ToList();
            placed.UnionWith(inRoom);
            if (Demand(document, network, inRoom, room, sizes) is { } demand) demands.Add(demand);
        }

        // A sprinkler in no room: on its own.
        foreach (var head in heads.Where(head => !placed.Contains(head)))
            if (Demand(document, network, new[] { head }, null, sizes) is { } demand) demands.Add(demand);
        return demands.OrderByDescending(demand => demand.Pressure).ToList();
    }

    /// <summary>How long the supply has to last, minutes: 10, or in the US 7 for a home of one storey under 2,000 sq ft (P2904.5.2).</summary>
    public static int Minutes(BimDocument document)
    {
        if (!document.ProjectInformation.ClearanceStandard.IsUs()) return 10;
        var storeys = document.Levels.Count(level => document.All<Room>().Any(room => room.LevelId == level.Id));
        return storeys <= 1 && MepSystems.DwellingArea(document) < 2000 * SquareFoot ? 7 : 10;
    }

    /// <summary>
    /// The sizes a sprinkler system's pipes want: each the smallest of its material from ¾ in (20 mm), then
    /// the pipe losing most on the way to the most demanding room's furthest sprinkler taken up a size, until
    /// the supply meets every room - or as far as its sizes go. Where the supply is not known, the smallest.
    /// </summary>
    public static IEnumerable<(Pipe Pipe, double Diameter)> Sized(BimDocument document, PipeNetwork network)
    {
        if (network.System.Kind != SystemKind.Sprinkler) yield break;
        var sizes = new Dictionary<Pipe, double>();
        var options = new Dictionary<Pipe, IReadOnlyList<PipeSize>>();
        foreach (var pipe in network.Loads.Keys)
        {
            var each = PipeSizes.Of(MaterialOf(document, pipe)).Where(size => size.Outside >= 19.5).ToList();
            if (each.Count == 0) continue;
            options[pipe] = each;
            sizes[pipe] = each[0].Outside;
        }

        if (Supply(document, network.System) is { } supply)
            for (var guard = 0; guard < 20 * Math.Max(1, sizes.Count); guard++)
            {
                var worst = Demands(document, network, sizes).FirstOrDefault();
                if (worst is null || worst.Pressure <= supply + 1e-9 || worst.Remote is null) break;
                var larger = PipeNetworks.PathTo(network, network.FirstPipe[worst.Remote])
                    .Where(pipe => options.TryGetValue(pipe, out var each) && each[^1].Outside > sizes[pipe] + 0.01)
                    .OrderByDescending(pipe => worst.Pipes.GetValueOrDefault(pipe).Loss).FirstOrDefault();
                if (larger is null) break;
                sizes[larger] = options[larger].First(size => size.Outside > sizes[larger] + 0.01).Outside;
            }

        foreach (var (pipe, diameter) in sizes)
            if (Math.Abs(diameter - pipe.Diameter) > 0.01) yield return (pipe, diameter);
    }

    private static string Flow(double litres, bool us) => us ? $"{litres / Units.LitresPerGallon:0.#} gpm" : $"{litres:0} l/min";

    private static string Pressure(double bar, bool us) => us ? $"{bar * PsiPerBar:0.0} psi" : $"{bar:0.00} bar";

    /// <summary>What a sprinkler pipe carries, as its Properties say it.</summary>
    public static string Describe(BimDocument document, Pipe pipe, PipeNetwork network)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var worst = Demands(document, network).FirstOrDefault(demand => demand.Pipes.ContainsKey(pipe));
        if (worst is null) return $"{Units.Metres(network.Loads.GetValueOrDefault(pipe)?.FromSource ?? 0, "0.0")} from {network.System.Name}'s valve set - no sprinkler beyond it opens with the most demanding room's";
        var (flow, loss) = worst.Pipes[pipe];
        return $"{Flow(flow, us)} when the {worst.Room?.Name ?? "sprinkler"}'s sprinklers open, losing {Pressure(loss, us)} along it";
    }

    /// <summary>What a sprinkler system comes to, as its Properties say it.</summary>
    public static string Summary(BimDocument document, MepSystem system)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var heads = MepSystems.MembersOf(document, system).Count(member => IsSprinkler(document, member));
        if (heads == 0) return "No sprinklers on it yet";
        var demands = Demands(document, PipeNetworks.Trace(document, system));
        var said = $"{heads} sprinkler{(heads == 1 ? string.Empty : "s")}";
        if (demands.Count == 0) return $"{said}; no pipe reaches them from the valve set yet";
        var flow = demands.Max(demand => demand.Flow);
        var minutes = Minutes(document);
        var worst = demands[0];
        var supply = Supply(document, system);
        var stored = us ? $"{flow * minutes / Units.LitresPerGallon:0} gal" : $"{flow * minutes:0} l";
        return $"{said}; design flow {Flow(flow, us)} for {minutes} minutes ({stored}); the {worst.Room?.Name ?? "sprinkler"} asks the most, " +
               $"{worst.Flowing.Count} at {Flow(worst.Each, us)}: {Pressure(worst.Pressure, us)} at the valve set" +
               (supply is { } given ? $" against {Pressure(given, us)}" : " - its supply pressure not given");
    }

    /// <summary>What is wanting in the sprinklers: rooms without them or not covered, sprinklers too near heat or a light, and a supply short of what the sprinklers ask.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (!Designed(document)) yield break;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var heads = All(document);
        var rule = us ? "IRC P2904.1.1 asks sprinklers in every area of the home" : "BS 9251 asks sprinklers in every room of a home sprinklered throughout";

        // Every room covered.
        foreach (var (room, outline) in Rooms(document))
        {
            if (Exempt(document, room, outline) is not null) continue;
            var inRoom = heads.Where(head => RoomsOf.Holds(document, room, outline, head)).ToList();
            if (inRoom.Count == 0)
            {
                if (document.ProjectInformation.Sprinklered || heads.Any(head => head.LevelId == room.LevelId))
                    yield return new MepProblem(room, $"no sprinkler - {rule}");
                continue;
            }

            var bare = Uncovered(document, outline, inRoom);
            if (bare >= 0.5)
                yield return new MepProblem(room, $"{Units.OfSquareMetres(bare, "0.0")} of it beyond its sprinklers' coverage - each covers the square its listing gives it; another sprinkler, or one listed for more");
        }

        foreach (var head in heads)
        {
            if (document.FindType<ComponentType>(head.TypeId) is not { } type) continue;
            if (!us) continue;
            var side = CoverageOf(type);
            if (side * side / 1e6 > MostCoverage + 1e-6)
                yield return new MepProblem(head, $"its listing's {side / Units.MillimetresPerFoot:0} x {side / Units.MillimetresPerFoot:0} ft is over the 400 sq ft P2904.2.4.1 lets one sprinkler cover");
            if (type.Temperature > 0 && (type.Temperature < 57.2 - 0.1 || type.Temperature > 107.2 + 0.1))
                yield return new MepProblem(head, $"rated {type.Temperature * 1.8 + 32:0} °F - P2904.2.1 asks 135 to 225 °F");

            // Table P2904.2.2: kept from what gives off heat.
            var z = ComponentHosting.BaseElevation(document, head, type);
            foreach (var (name, least, within, outline, top) in HeatSources(document, head.LevelId))
            {
                var across = Apart(head.Location, outline);
                var apart = Math.Sqrt(across * across + Math.Pow(Math.Max(0, z - top), 2)) / Units.MillimetresPerInch;
                if (apart < least - 0.05)
                    yield return new MepProblem(head, $"{apart:0} in from {name} - Table P2904.2.2 keeps a sprinkler {least:0} in from it at least");
                else if (apart <= within + 0.05 && type.Temperature is > 0 and < 79 - 0.5)
                    yield return new MepProblem(head, $"an ordinary temperature sprinkler {apart:0} in from {name} - Table P2904.2.2 asks an intermediate temperature one (175 to 225 °F) within {within:0} in of it");
            }

            // P2904.2.4.2.1: a pendent sprinkler within 3 ft of a ceiling light is obstructed.
            foreach (var light in document.All<Component>().Where(piece => piece.LevelId == head.LevelId && document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.CeilingLight))
                if (light.Location.DistanceTo(head.Location) < LightClearance - 0.5)
                    yield return new MepProblem(head, $"{light.Location.DistanceTo(head.Location) / Units.MillimetresPerInch:0} in from the middle of a ceiling light - P2904.2.4.2.1 counts a pendent sprinkler within 3 ft of one obstructed; another sprinkler, or move it");
        }

        // Each system's supply against what its most demanding room asks.
        foreach (var system in document.All<MepSystem>().Where(system => system.Kind == SystemKind.Sprinkler))
        {
            if (MepSystems.SourceOf(document, system) is not { } valve) continue;
            var worst = Demands(document, PipeNetworks.Trace(document, system)).FirstOrDefault();
            if (worst is null) continue;
            var supply = Supply(document, system);
            var asked = $"the {worst.Room?.Name ?? "sprinkler"}'s {(worst.Flowing.Count == 1 ? "sprinkler" : $"{worst.Flowing.Count} sprinklers")} at {Flow(worst.Each, us)} {(worst.Flowing.Count == 1 ? string.Empty : "each ")}ask {Pressure(worst.Pressure, us)} here";
            if (supply is null)
                yield return new MepProblem(valve, $"{asked} - its supply's pressure is not given; set it on its type, from the pump set's duty or the main's at the design flow");
            else if (worst.Pressure > supply + 1e-9)
                yield return new MepProblem(valve, $"{asked} - its supply gives {Pressure(supply.Value, us)}; larger pipes (Size Pipes), or a pump");
        }
    }
}
