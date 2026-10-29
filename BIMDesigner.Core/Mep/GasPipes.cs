using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// What a gas pipe carries with every appliance beyond it at its full rate: their heat input, W gross;
/// the gas, m³/h; how fast it goes, m/s; how many elbows and tees it has where it leaves the pipe before
/// it; the pressure it loses along itself and them, Pa; and all that is lost from the meter to its far
/// end, Pa.
/// </summary>
public sealed record GasFlow(double Watts, double Rate, double Velocity, int Fittings, double Loss, double FromMeter);

/// <summary>
/// Gas pipes sized from the appliances' demand back to the meter.
///
/// Every appliance is taken at its full rate, all together - a home's gas is not diversified - at its
/// maker's maximum heat input, gross; a boiler or a gas fire with none given at NFPA 54's estimate for
/// its kind, which the IRC gives for initial design (Table G2413.2): a hydronic boiler 100,000 Btu/h,
/// one heating the hot water as well 120,000, a gas fireplace 40,000.
///
/// In the UK, as BS 6891 has it for natural gas: no more than 1 mbar lost from the meter's outlet to any
/// appliance's inlet at that rate. The gas a pipe carries is the heat beyond it over the gas's gross
/// calorific value, 38.76 MJ/m³ - what gas rating takes it at - and the pressure it loses is worked
/// out rather than read off BS 6891's tables (not seen at first hand): Darcy-Weisbach's, its friction
/// factor Swamee and Jain's, for natural gas of relative density 0.6 - 0.735 kg/m³, its viscosity
/// 1.1 x 10⁻⁵ Pa·s - in the pipe's own bore. An elbow or a tee where a pipe leaves the one before it
/// counts as so much more pipe, as BS 6891's allowances are taught: 0.5 m on 15 mm, 0.7 m on 22 mm,
/// 1.0 m on 28 mm, 1.2 m on 35 mm. The meter is a home's - a U6, or G4 - passing 6 m³/h; and BS 6891
/// goes to 35 mm pipe, IGEM/UP/2 beyond it.
///
/// In the US, by the IRC's branch length method (G2413.4.2): each section carries the appliances beyond
/// it, in cubic feet an hour at 1,000 Btu a cubic foot - the heating value the ICC's worked examples
/// use, the gas supplier's where known - and is sized on the row of the table for the longest run from
/// the meter through it to an outlet, the next row up where it falls between them: Table G2413.4(1) for
/// Schedule 40 steel pipe - natural gas under 2 psi, losing 0.5 in. w.c. - and the IRC's semirigid copper
/// tubing table for copper. Their capacities are worked by the low-pressure formula the tables are drawn
/// from, Q = 2313 D^2.623 (ΔH / Cr L)^0.541, Cr 0.6094 for natural gas.
/// </summary>
public static class GasPipes
{
    /// <summary>Natural gas's gross calorific value, J/m³: 38.76 MJ/m³, what gas rating in the UK takes it at.</summary>
    public const double CalorificValue = 38.76e6;

    /// <summary>Natural gas's density, kg/m³ - relative density 0.6 - and its viscosity, Pa·s.</summary>
    public const double Density = 0.6 * 1.225, Viscosity = 1.1e-5;

    /// <summary>The most BS 6891 lets the pressure fall from the meter's outlet to an appliance's inlet, Pa: 1 mbar.</summary>
    public const double MostDrop = 100;

    /// <summary>What a home's gas meter passes, m³/h: a U6 (G4).</summary>
    public const double MeterCapacity = 6;

    /// <summary>The largest pipe BS 6891 covers, outside diameter in mm: 35 mm.</summary>
    public const double LargestPipe = 35;

    /// <summary>The heating value US gas is taken at, Btu a cubic foot.</summary>
    public const double HeatingValue = 1000;

    /// <summary>The pressure Table G2413.4(1) lets a run lose, in. w.c., and natural gas's Cr in the formula it is drawn from.</summary>
    public const double UsDrop = 0.5, Cr = 0.6094;

    /// <summary>The table's rows, ft: the longest run is read on the first not shorter than it.</summary>
    public static readonly IReadOnlyList<double> Rows = new double[]
    {
        10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 125, 150, 175, 200, 250, 300, 350, 400, 450, 500, 550, 600, 650, 700, 750, 800, 850, 900, 950,
        1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700, 1800, 1900, 2000
    };

    /// <summary>The most heat an appliance takes in as gas, W gross: its maker's, or the IRC's estimate for its kind; none for one that burns no gas.</summary>
    public static double InputOf(ComponentType type)
    {
        if (!type.BurnsGas) return 0;
        if (type.GasInput > 0) return type.GasInput;
        return type.Form == ComponentForm.Boiler
            ? (type.HotWater == BoilerHotWater.Combi ? 120_000 : 100_000) / Units.BtuPerHourPerWatt
            : 40_000 / Units.BtuPerHourPerWatt;
    }

    public static double InputOf(BimDocument document, Component component) =>
        document.FindType<ComponentType>(component.TypeId) is { } type ? InputOf(type) : 0;

    /// <summary>The gas a heat input burns, m³/h.</summary>
    public static double Rate(double watts) => watts * 3600 / CalorificValue;

    /// <summary>The gas a heat input burns in the US, cubic feet an hour.</summary>
    public static double Cfh(double watts) => watts * Units.BtuPerHourPerWatt / HeatingValue;

    /// <summary>How fast gas goes in a bore (mm) carrying so much (m³/h), m/s, and the pressure it loses a metre, Pa/m.</summary>
    public static (double Velocity, double Gradient) Friction(double rate, double bore, PipeMaterial material)
    {
        if (rate <= 0 || bore <= 0) return (0, 0);
        var diameter = bore / 1000;
        var velocity = rate / 3600 / (Math.PI * diameter * diameter / 4);
        var reynolds = Density * velocity * diameter / Viscosity;
        var friction = reynolds < 2300
            ? 64 / reynolds
            : 0.25 / Math.Pow(Math.Log10(HeatingPipes.Roughness(material) / 1000 / (3.7 * diameter) + 5.74 / Math.Pow(reynolds, 0.9)), 2);
        return (velocity, friction * Density * velocity * velocity / 2 / diameter);
    }

    /// <summary>How much more pipe an elbow or a tee counts as on a pipe of this bore, m - BS 6891's allowances as they are taught.</summary>
    public static double Allowance(double bore) => bore < 15 ? 0.5 : bore < 22 ? 0.7 : bore < 28 ? 1.0 : 1.2;

    /// <summary>What a US pipe of this bore (in) carries over a run this long (ft), losing 0.5 in. w.c., cfh: the low-pressure formula Table G2413.4(1) is drawn from.</summary>
    public static double Capacity(double boreInches, double feet) =>
        2313 * Math.Pow(boreInches, 2.623) * Math.Pow(UsDrop / (Cr * Math.Max(feet, 1)), 0.541);

    /// <summary>The table's row a run is read on, ft - the first not shorter than it - or null past its last.</summary>
    public static double? RowOf(double feet) => Rows.Cast<double?>().FirstOrDefault(row => row >= feet - 1e-6);

    /// <summary>A pipe's bore: its size's, or near enough where its size is not one of its type's.</summary>
    private static double Bore(BimDocument document, Pipe pipe, double diameter) =>
        document.FindType<PipeType>(pipe.TypeId) is { } type && PipeSizes.Of(type.Material).FirstOrDefault(size => Math.Abs(size.Outside - diameter) < 0.01) is { } size
            ? size.Inside : diameter * 0.85;

    private static PipeMaterial MaterialOf(BimDocument document, Pipe pipe) => document.FindType<PipeType>(pipe.TypeId)?.Material ?? PipeMaterial.Copper;

    /// <summary>Whether a material is plastic: polyethylene's place is below the ground outside, not in a building.</summary>
    private static bool Plastic(PipeMaterial material) => material.Like() is PipeMaterial.PeX or PipeMaterial.PvcU;

    /// <summary>
    /// What each pipe of a gas installation carries in the UK, with its pipes at these sizes - their own
    /// where none are given: the gas, its speed, its fittings and what is lost along it and from the meter.
    /// </summary>
    public static IReadOnlyDictionary<Pipe, GasFlow> Flows(BimDocument document, PipeNetwork network, IReadOnlyDictionary<Pipe, double>? sizes = null)
    {
        var flows = new Dictionary<Pipe, GasFlow>();
        if (network.System.Kind != SystemKind.Gas) return flows;
        foreach (var (pipe, carried) in network.Loads.OrderBy(entry => entry.Value.FromSource))
        {
            var diameter = sizes?.GetValueOrDefault(pipe, pipe.Diameter) ?? pipe.Diameter;
            var bore = Bore(document, pipe, diameter);
            var rate = Rate(carried.Heat);
            var (velocity, gradient) = Friction(rate, bore, MaterialOf(document, pipe));
            var fittings = PipeNetworks.FittingAt(document, carried) == PipeFitting.None ? 0 : 1;
            var loss = gradient * (pipe.Length / 1000 + fittings * Allowance(bore));
            var before = carried.Toward is { } toward && flows.TryGetValue(toward, out var known) ? known.FromMeter : 0;
            flows[pipe] = new GasFlow(carried.Heat, rate, velocity, fittings, loss, before + loss);
        }

        return flows;
    }

    /// <summary>Each appliance on a gas installation, and the pipe its gas comes to it by.</summary>
    public static IReadOnlyList<(Component Appliance, Pipe Pipe)> Appliances(BimDocument document, PipeNetwork network) =>
        network.FirstPipe.Where(entry => network.Loads.ContainsKey(entry.Value) && InputOf(document, entry.Key) > 0)
            .Select(entry => (entry.Key, entry.Value)).ToList();

    /// <summary>The longest run from the meter through a pipe to what is furthest beyond it, mm.</summary>
    private static double LongestRun(PipeLoad carried) => carried.FromSource + carried.Downstream;

    /// <summary>
    /// The sizes a UK gas installation's pipes want: each the smallest of its material, from 15 mm, that
    /// would keep its share of the longest run through it within 1 mbar - then, where an appliance is
    /// still over it with the elbows and tees counted, the pipe losing the most on the way to it taken up
    /// a size, until none is. Null for a pipe its material's sizes cannot do it with.
    /// </summary>
    private static Dictionary<Pipe, double> UkSizes(BimDocument document, PipeNetwork network)
    {
        var sizes = new Dictionary<Pipe, double>();
        var options = new Dictionary<Pipe, IReadOnlyList<PipeSize>>();
        foreach (var (pipe, carried) in network.Loads)
        {
            if (carried.Heat <= 0) continue;
            var material = MaterialOf(document, pipe);
            var each = PipeSizes.Of(material).Where(size => size.Outside >= 15 - 0.01).ToList();
            if (each.Count == 0) continue;
            options[pipe] = each;
            var allowed = MostDrop / Math.Max(LongestRun(carried) / 1000, 0.1);
            sizes[pipe] = (each.FirstOrDefault(size => Friction(Rate(carried.Heat), size.Inside, material).Gradient <= allowed + 1e-9) ?? each[^1]).Outside;
        }

        var appliances = Appliances(document, network);
        for (var guard = 0; guard < 20 * Math.Max(1, sizes.Count); guard++)
        {
            var flows = Flows(document, network, sizes);
            var short1 = appliances.Where(entry => flows.TryGetValue(entry.Pipe, out var flow) && flow.FromMeter > MostDrop + 1e-6)
                .OrderByDescending(entry => flows[entry.Pipe].FromMeter).Select(entry => entry.Pipe).FirstOrDefault();
            if (short1 is null) break;
            var larger = PipeNetworks.PathTo(network, short1).Where(pipe => options.TryGetValue(pipe, out var each) && each[^1].Outside > sizes[pipe] + 0.01)
                .OrderByDescending(pipe => flows[pipe].Loss).FirstOrDefault();
            if (larger is null) break;
            sizes[larger] = options[larger].First(size => size.Outside > sizes[larger] + 0.01).Outside;
        }

        return sizes;
    }

    /// <summary>The size a US gas pipe needs by the branch length method, its row and what it carries, cfh; null for one carrying nothing or past the table.</summary>
    public static (PipeSize Size, double Row, double Cfh)? UsRequired(BimDocument document, Pipe pipe, PipeNetwork network)
    {
        if (!network.Loads.TryGetValue(pipe, out var carried) || carried.Heat <= 0) return null;
        var cfh = Cfh(carried.Heat);
        if (RowOf(LongestRun(carried) / Units.MillimetresPerFoot) is not { } row) return null;
        var size = PipeSizes.Of(MaterialOf(document, pipe)).FirstOrDefault(each => Capacity(each.Inside / Units.MillimetresPerInch, row) >= cfh - 1e-9);
        return size is null ? null : (size, row, cfh);
    }

    /// <summary>Every gas pipe of a system sized: its new outside diameter, where it changes.</summary>
    public static IEnumerable<(Pipe Pipe, double Diameter)> Sized(BimDocument document, PipeNetwork network)
    {
        if (network.System.Kind != SystemKind.Gas) yield break;
        if (document.ProjectInformation.ClearanceStandard.IsUs())
        {
            foreach (var pipe in network.Loads.Keys)
                if (UsRequired(document, pipe, network) is { } need && Math.Abs(need.Size.Outside - pipe.Diameter) > 0.01)
                    yield return (pipe, need.Size.Outside);
            yield break;
        }

        foreach (var (pipe, diameter) in UkSizes(document, network))
            if (Math.Abs(diameter - pipe.Diameter) > 0.01) yield return (pipe, diameter);
    }

    /// <summary>The table a US gas pipe of this material is sized by.</summary>
    private static string TableOf(PipeMaterial material) =>
        material.Like() == PipeMaterial.Copper ? "the IRC's semirigid copper tubing table" : "IRC Table G2413.4(1)";

    private static string Mbar(double pascals) => $"{pascals / 100:0.00} mbar";

    /// <summary>What a gas pipe carries, as its Properties say it.</summary>
    public static string Describe(BimDocument document, Pipe pipe, PipeNetwork network)
    {
        if (!network.Loads.TryGetValue(pipe, out var carried) || carried.Heat <= 0)
            return $"{Units.Metres(network.Loads.GetValueOrDefault(pipe)?.FromSource ?? 0, "0.0")} from what feeds {network.System.Name} - no appliance beyond it yet";

        if (document.ProjectInformation.ClearanceStandard.IsUs())
        {
            var said = $"{carried.Heat * Units.BtuPerHourPerWatt:#,0} Btu/h beyond it: {Cfh(carried.Heat):0} cfh, its longest run {LongestRun(carried) / Units.MillimetresPerFoot:0} ft";
            return UsRequired(document, pipe, network) is { } need
                ? $"{said} - {TableOf(MaterialOf(document, pipe))} asks {need.Size.Name} on its {need.Row:0} ft row (branch length method, G2413.4.2)"
                : $"{said} - more than {TableOf(MaterialOf(document, pipe))} goes to";
        }

        var flows = Flows(document, network);
        var flow = flows[pipe];
        var fittings = flow.Fittings == 0 ? string.Empty : carried.NearJoins >= 3 ? " and its tee" : " and its elbow";
        var text = $"{carried.Heat / 1000:0.0} kW of appliances beyond it: {flow.Rate:0.00} m³/h at {Units.MetresPerSecond(flow.Velocity, "0.0")}, losing {Mbar(flow.Loss)} along it{fittings}; {Mbar(flow.FromMeter)} from the meter to its end";
        var beyond = Appliances(document, network).Where(entry => PipeNetworks.PathTo(network, entry.Pipe).Contains(pipe)).ToList();
        var worst = beyond.OrderByDescending(entry => flows[entry.Pipe].FromMeter).FirstOrDefault();
        return worst.Appliance is null ? text
            : flows[worst.Pipe].FromMeter <= MostDrop + 1e-6 ? $"{text} - every appliance beyond it within BS 6891's 1 mbar"
            : $"{text} - {MepSystems.Describe(document, worst.Appliance)} {Mbar(flows[worst.Pipe].FromMeter)} below the meter, over BS 6891's 1 mbar";
    }

    /// <summary>What a gas installation comes to, as its Properties say it: its appliances, their gas, and the furthest of them.</summary>
    public static string Summary(BimDocument document, MepSystem system)
    {
        var network = PipeNetworks.Trace(document, system);
        var members = MepSystems.MembersOf(document, system).Where(member => InputOf(document, member) > 0).ToList();
        if (members.Count == 0) return "No appliances on it yet";
        var watts = members.Sum(member => InputOf(document, member));
        var count = $"{members.Count} appliance{(members.Count == 1 ? string.Empty : "s")}";
        if (document.ProjectInformation.ClearanceStandard.IsUs())
        {
            var longest = network.Loads.Values.Select(LongestRun).DefaultIfEmpty(0).Max() / Units.MillimetresPerFoot;
            return $"{count}, {watts * Units.BtuPerHourPerWatt:#,0} Btu/h: {Cfh(watts):0} cfh at 1,000 Btu/cu ft" +
                   (longest > 0 ? $"; the longest run {longest:0} ft{(RowOf(longest) is { } row ? $", read on the {row:0} ft row" : " - past the table")}" : string.Empty);
        }

        var said = $"{count}, {watts / 1000:0.0} kW gross: {Rate(watts):0.00} m³/h";
        var flows = Flows(document, network);
        var furthest = Appliances(document, network).OrderByDescending(entry => flows[entry.Pipe].FromMeter).FirstOrDefault();
        return furthest.Appliance is null ? said : $"{said}; {MepSystems.Describe(document, furthest.Appliance)} the furthest, {Mbar(flows[furthest.Pipe].FromMeter)} below the meter (BS 6891 allows 1 mbar)";
    }

    /// <summary>What is wanting in a gas installation: an appliance short of pressure, a meter too small, a pipe too small, plastic pipe in the building.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document, PipeNetwork network)
    {
        if (network.System.Kind != SystemKind.Gas) yield break;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var ground = document.Levels.Count == 0 ? 0 : document.Levels.Min(level => level.Elevation);

        foreach (var pipe in PipeNetworks.PipesOf(document, network.System))
        {
            var (start, end) = pipe.Ends(document);
            if (Plastic(MaterialOf(document, pipe)) && Math.Max(start.Z, end.Z) > ground - 150)
                yield return new MepProblem(pipe, us
                    ? "gas in plastic pipe above the ground - the IRC (G2414) runs it in steel pipe, copper tubing or CSST, plastic only underground outside the building"
                    : "gas in plastic pipe above the ground - BS 6891 runs it in copper, steel or corrugated stainless steel, polyethylene only below the ground outside the building");
            if (!us && pipe.Diameter > LargestPipe + 0.5)
                yield return new MepProblem(pipe, $"{Units.Millimetres(pipe.Diameter, "0")} gas pipe - BS 6891 goes to 35 mm; pipework larger is designed to IGEM/UP/2");
        }

        if (us)
        {
            foreach (var (pipe, carried) in network.Loads.Where(entry => entry.Value.Heat > 0))
            {
                var type = document.FindType<PipeType>(pipe.TypeId);
                if (UsRequired(document, pipe, network) is not { } need)
                    yield return new MepProblem(pipe, $"it carries {Cfh(carried.Heat):0} cfh over a longest run of {LongestRun(carried) / Units.MillimetresPerFoot:0} ft - more than {TableOf(MaterialOf(document, pipe))} goes to");
                else if (pipe.Diameter < need.Size.Outside - 0.01)
                    yield return new MepProblem(pipe,
                        $"too small at {pipe.SizeName(type)}: it carries {need.Cfh:0} cfh, its longest run {LongestRun(carried) / Units.MillimetresPerFoot:0} ft - {TableOf(MaterialOf(document, pipe))} asks {need.Size.Name} on its {need.Row:0} ft row (branch length method, G2413.4.2)");
            }

            yield break;
        }

        // Each appliance's gas within 1 mbar of the meter's.
        var flows = Flows(document, network);
        foreach (var (appliance, last) in Appliances(document, network))
        {
            if (!flows.TryGetValue(last, out var flow) || flow.FromMeter <= MostDrop + 1e-6) continue;
            var worst = PipeNetworks.PathTo(network, last).OrderByDescending(pipe => flows[pipe].Loss).First();
            yield return new MepProblem(appliance,
                $"its gas comes to it {Mbar(flow.FromMeter)} below the meter's outlet at full rate - BS 6891 allows 1 mbar; the most is lost along the {worst.SizeName(document.FindType<PipeType>(worst.TypeId))} pipe of {Units.Metres(worst.Length, "0.0")} ({Mbar(flows[worst].Loss)}) - Size Pipes");
        }

        // The meter: what a home's passes.
        if (MepSystems.SourceOf(document, network.System) is { } meter && document.FindType<ComponentType>(meter.TypeId)?.Form == ComponentForm.GasMeter)
        {
            var watts = MepSystems.MembersOf(document, network.System).Sum(member => InputOf(document, member));
            if (Rate(watts) > MeterCapacity + 1e-6)
                yield return new MepProblem(meter, $"the appliances on it take {Rate(watts):0.00} m³/h together - more than the {MeterCapacity:0} m³/h a home's U6 (G4) meter passes; the gas transporter fits a larger one");
        }
    }
}
