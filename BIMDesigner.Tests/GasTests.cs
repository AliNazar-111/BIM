using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Gas (round M7): the appliances' gas inputs, their pipes sized back to the meter within BS 6891's 1 mbar
/// - in the US by the IRC's branch length method on Table G2413.4(1) - the meter's capacity, plastic gas
/// pipe, and a carbon monoxide alarm by each fixed gas appliance.
/// </summary>
public class GasTests
{
    private static ComponentType Type(BimDocument document, string name) => document.TypesOf<ComponentType>().First(type => type.Name == name);

    private static Component Put(BimDocument document, string type, double x, double y, double rotation = 0)
    {
        var family = Type(document, type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation, Elevation = family.DefaultElevation
        };
        document.Add(component);
        return component;
    }

    private static Point3D GasOf(BimDocument document, Component fitting) =>
        Connectors.Placed(document, fitting).First(placed => placed.Connector.Kind == SystemKind.Gas).At;

    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem system, PipeMaterial material, double diameter)
    {
        var pipe = new Pipe
        {
            TypeId = document.TypesOf<PipeType>().First(type => type.Material == material).Id, LevelId = document.Levels[0].Id,
            Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = diameter, Kind = SystemKind.Gas, SystemId = system.Id
        };
        document.Add(pipe);
        return pipe;
    }

    private static MepSystem Make(BimDocument document, params Component[] picked)
    {
        var (system, refusal) = MepSystems.Make(document, SystemKind.Gas, picked);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    private static Point3D Along(Point3D from, double x, double y = 0, double z = 0) => new(from.X + x, from.Y + y, from.Z + z);

    [Fact]
    public void NaturalGasLosesWhatDarcyWeisbachGivesForIt()
    {
        // About 1 mbar through 3 m of thin-wall copper - 15 x 0.7, 22 x 0.9, 28 x 0.9 - at 2.9, 8.7 and 18 m³/h.
        foreach (var (bore, rate) in new[] { (13.6, 2.9), (20.2, 8.7), (26.2, 18.0) })
        {
            var lost = GasPipes.Friction(rate, bore, PipeMaterial.Copper).Gradient * 3;
            Assert.InRange(lost, 90, 102);
        }

        // A gas rate from a heat input: 10.77 kW gross is a cubic metre an hour; 100,000 Btu/h is 100 cfh.
        Assert.Equal(1, GasPipes.Rate(10_766.7), 3);
        Assert.Equal(100, GasPipes.Cfh(100_000 / Units.BtuPerHourPerWatt), 6);

        // Elbows and tees as BS 6891's allowances are taught.
        Assert.Equal((0.5, 0.7, 1.0, 1.2), (GasPipes.Allowance(13), GasPipes.Allowance(20), GasPipes.Allowance(25), GasPipes.Allowance(32)));
    }

    [Fact]
    public void TheIrcsTableIsWhatItsFormulaGives()
    {
        // Table G2413.4(1), Schedule 40 steel, under 2 psi, 0.5 in. w.c.
        foreach (var (bore, feet, cfh) in new[] { (0.622, 10.0, 172.0), (0.824, 50.0, 151.0), (1.049, 100.0, 195.0), (1.380, 200.0, 275.0), (2.067, 100.0, 1160.0), (3.068, 350.0, 1650.0) })
            Assert.InRange(GasGap(GasPipes.Capacity(bore, feet), cfh), -0.01, 0.01);

        Assert.Equal(70, GasPipes.RowOf(62));
        Assert.Equal(100, GasPipes.RowOf(100));
        Assert.Equal(125, GasPipes.RowOf(101));
        Assert.Null(GasPipes.RowOf(2001));

        // The estimates for a boiler or a fire with no input given: Table G2413.2's.
        var document = BimDocument.CreateDefault();
        var combi = Type(document, "Boiler - Gas Combi").Duplicate("Combi - unknown");
        combi.GasInput = 0;
        Assert.Equal(120_000, GasPipes.InputOf(combi) * Units.BtuPerHourPerWatt, 3);
        combi.HotWater = BoilerHotWater.Cylinder;
        Assert.Equal(100_000, GasPipes.InputOf(combi) * Units.BtuPerHourPerWatt, 3);
        Assert.Equal(0, GasPipes.InputOf(Type(document, "Oven and Hob - 600")));
        Assert.Equal(11_000, GasPipes.InputOf(Type(document, "Cooker - Gas 600")));
    }

    private static double GasGap(double worked, double table) => (worked - table) / table;

    /// <summary>
    /// A meter, a main along 6 m to a tee, a branch to a combi boiler, and on 4 m to a tee with a gas
    /// cooker and a gas fire off it - all in 15 mm copper.
    /// </summary>
    private static (BimDocument Document, MepSystem Gas, Component Meter, Pipe Main, Pipe On, Component Boiler, Component Cooker, Component Fire) Installation()
    {
        var document = BimDocument.CreateDefault();
        var meter = Put(document, "Gas Meter Box - Surface", 0, 0);
        var boiler = Put(document, "Boiler - Gas Combi", 6000, 2500);
        var cooker = Put(document, "Cooker - Gas 600", 10000, 2000);
        var fire = Put(document, "Gas Fire - Inset 600", 10000, -3000);
        var gas = Make(document, meter, boiler, cooker, fire);

        var start = GasOf(document, meter);
        var tee = Along(start, 6000);
        var second = Along(tee, 4000);
        var main = Run(document, start, tee, gas, PipeMaterial.Copper, 15);
        Run(document, tee, GasOf(document, boiler), gas, PipeMaterial.Copper, 15);
        var on = Run(document, tee, second, gas, PipeMaterial.Copper, 15);
        Run(document, second, GasOf(document, cooker), gas, PipeMaterial.Copper, 15);
        Run(document, second, GasOf(document, fire), gas, PipeMaterial.Copper, 15);
        return (document, gas, meter, main, on, boiler, cooker, fire);
    }

    [Fact]
    public void AHomesGasIsSizedBackToTheMeterWithinOneMillibar()
    {
        var (document, gas, _, main, on, boiler, cooker, fire) = Installation();
        var network = PipeNetworks.Trace(document, gas);

        // Every appliance at its full rate together.
        Assert.Equal(27_400 + 11_000 + 6000, network.Loads[main].Heat, 6);
        Assert.Equal(11_000 + 6000, network.Loads[on].Heat, 6);
        Assert.Equal(3, GasPipes.Appliances(document, network).Count);

        // All in 15 mm, the boiler's gas comes far short; the problem says so, and what loses most.
        var problems = PipeNetworks.Problems(document).Where(problem => problem.Text.Contains("BS 6891")).ToList();
        Assert.Contains(problems, problem => problem.Element == boiler && problem.Text.Contains("below the meter's outlet") && problem.Text.Contains("15 x 1.0"));
        Assert.Contains("m³/h", PipeNetworks.Describe(document, main));
        Assert.Contains("over BS 6891's 1 mbar", PipeNetworks.Describe(document, main));

        // Sized: every appliance within 1 mbar, the main the largest, the branches no larger than what feeds them.
        foreach (var (pipe, diameter) in PipeNetworks.Sized(document, new[] { gas })) pipe.Diameter = diameter;
        var flows = GasPipes.Flows(document, PipeNetworks.Trace(document, gas));
        network = PipeNetworks.Trace(document, gas);
        foreach (var appliance in new[] { boiler, cooker, fire })
            Assert.True(flows[network.FirstPipe[appliance]].FromMeter <= GasPipes.MostDrop, $"{appliance} at {flows[network.FirstPipe[appliance]].FromMeter:0} Pa");
        Assert.DoesNotContain(PipeNetworks.Problems(document), problem => problem.Text.Contains("BS 6891"));
        Assert.True(main.Diameter >= on.Diameter && main.Diameter > 15);
        Assert.Contains("every appliance beyond it within BS 6891's 1 mbar", PipeNetworks.Describe(document, main));
        Assert.Empty(PipeNetworks.Sized(document, new[] { gas }));

        // The tee and the elbow counted: the main leaves the meter straight, the branch off the tee has its tee.
        Assert.Equal(0, flows[main].Fittings);
        Assert.Equal(1, flows[on].Fittings);

        Assert.Contains("3 appliances, 44.4 kW gross: 4.12 m³/h", GasPipes.Summary(document, gas));
    }

    [Fact]
    public void AHomesMeterPassesSixCubicMetresAnHour()
    {
        var (document, gas, meter, _, _, _, _, _) = Installation();
        Assert.DoesNotContain(PipeNetworks.Problems(document), problem => problem.Element == meter);

        // A second boiler on it: 7.8 m³/h, more than a U6 passes.
        var second = Put(document, "Boiler - Gas Combi", 3000, -2000);
        gas.Members.Add(second.Id);
        Run(document, Along(GasOf(document, meter), 3000), GasOf(document, second), gas, PipeMaterial.Copper, 15);
        Assert.Contains(PipeNetworks.Problems(document), problem => problem.Element == meter && problem.Text.Contains("U6 (G4)"));
    }

    [Fact]
    public void GasInPlasticPipeIsForBelowTheGroundAndBs6891GoesTo35mm()
    {
        var (document, gas, _, main, _, _, _, _) = Installation();
        main.TypeId = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.PeX).Id;
        Assert.Contains(PipeNetworks.Problems(document), problem => problem.Element == main && problem.Text.Contains("polyethylene only below the ground"));

        main.TypeId = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper).Id;
        main.Diameter = 42;
        Assert.Contains(PipeNetworks.Problems(document), problem => problem.Element == main && problem.Text.Contains("IGEM/UP/2"));
    }

    [Fact]
    public void AUsHousesGasIsSizedByTheBranchLengthMethod()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var foot = Units.MillimetresPerFoot;

        // A meter, 40 ft of main to a tee; off it the combi boiler 8 ft away and the water heater 10 ft;
        // on 12 ft to a tee with the range and the dryer off it.
        var meter = Put(document, "Gas Meter Box - Surface", 0, 0);
        var start = GasOf(document, meter);
        var tee = Along(start, 40 * foot);
        var second = Along(tee, 12 * foot);
        var boiler = Put(document, "Boiler - Gas Combi", tee.X, tee.Y + 8 * foot);
        var heater = Put(document, "Water Heater - Gas 50 gal", tee.X, tee.Y - 10 * foot);
        var range = Put(document, "Range - Gas 30 in", second.X, second.Y + 3 * foot);
        var dryer = Put(document, "Dryer - Gas 27 in", second.X, second.Y - 4 * foot);
        var gas = Make(document, meter, boiler, heater, range, dryer);

        var steel = PipeMaterial.SteelSchedule40;
        var main = Run(document, start, tee, gas, steel, 21.3);
        var toBoiler = Run(document, tee, GasOf(document, boiler), gas, steel, 21.3);
        Run(document, tee, GasOf(document, heater), gas, steel, 21.3);
        var on = Run(document, tee, second, gas, steel, 21.3);
        Run(document, second, GasOf(document, range), gas, steel, 21.3);
        var toDryer = Run(document, second, GasOf(document, dryer), gas, steel, 21.3);
        var network = PipeNetworks.Trace(document, gas);

        // The main carries all of them - 27.4 kW, 40,000, 65,000 and 22,000 Btu/h - on the 60 ft row, its longest run 56 ft or so.
        var need = GasPipes.UsRequired(document, main, network)!.Value;
        Assert.Equal(27_400 * Units.BtuPerHourPerWatt / 1000 + 40 + 65 + 22, need.Cfh, 6);
        Assert.Equal(60, need.Row);
        Assert.Equal("1\"", need.Size.Name);
        Assert.Equal("3/4\"", GasPipes.UsRequired(document, toBoiler, network)!.Value.Size.Name);
        Assert.Equal(50, GasPipes.UsRequired(document, toBoiler, network)!.Value.Row);
        Assert.Equal("3/4\"", GasPipes.UsRequired(document, on, network)!.Value.Size.Name);
        Assert.Equal("1/2\"", GasPipes.UsRequired(document, toDryer, network)!.Value.Size.Name);

        Assert.Contains(PipeNetworks.Problems(document), problem => problem.Element == main && problem.Text.Contains("asks 1\" on its 60 ft row (branch length method, G2413.4.2)"));
        Assert.Contains("IRC Table G2413.4(1) asks 1\"", PipeNetworks.Describe(document, main));
        foreach (var (pipe, diameter) in PipeNetworks.Sized(document, new[] { gas })) pipe.Diameter = diameter;
        Assert.DoesNotContain(PipeNetworks.Problems(document), problem => problem.Text.Contains("G2413"));
        Assert.Contains("4 appliances, 220,493 Btu/h: 220 cfh", GasPipes.Summary(document, gas));

        // Gas appliances are no electrical load: the range, dryer and water heater are not counted by NEC 220.
        var workings = string.Join("\n", Circuits.UsLoad(document).Workings);
        Assert.DoesNotContain("dryer", workings);
    }

    [Fact]
    public void AFixedGasApplianceWantsACarbonMonoxideAlarmButACookerDoesNot()
    {
        // Approved Document J (3.43): a fixed gas appliance, excluding one used solely for cooking.
        var document = BimDocument.CreateDefault();
        Put(document, "Gas Fire - Inset 600", 2000, 1000);
        Put(document, "Cooker - Gas 600", 5000, 1000);
        Put(document, "Water Heater - Gas 50 gal", 8000, 1000);
        Put(document, "Tumble Dryer - 600", 11000, 1000);
        var names = Alarms.Appliances(document, document.Levels[0].Id).Select(appliance => appliance.Name).ToList();
        Assert.Equal(new[] { "Gas Fire - Inset 600", "Water Heater - Gas 50 gal" }, names.OrderBy(name => name));
    }

    [Fact]
    public void GasAppliancesHaveTheirGasConnectorsAndKeepTheirInputs()
    {
        var document = BimDocument.CreateDefault();
        foreach (var name in new[] { "Cooker - Gas 600", "Gas Fire - Inset 600", "Range - Gas 30 in", "Water Heater - Gas 50 gal", "Dryer - Gas 27 in", "Boiler - Gas System" })
        {
            var type = Type(document, name);
            Assert.True(type.BurnsGas, name);
            Assert.Single(Connectors.Of(type), connector => connector.Kind == SystemKind.Gas && !connector.Feeds);
        }

        // An electric cooker and water heater burn none; the gas water heater has no immersion heater.
        Assert.DoesNotContain(Connectors.Of(Type(document, "Oven and Hob - 600")), connector => connector.Kind == SystemKind.Gas);
        Assert.DoesNotContain(Connectors.Of(Type(document, "Water Heater - Gas 50 gal")), connector => connector.Kind is SystemKind.Power or SystemKind.Heating);
        Assert.Equal(100, Circuits.LoadOf(Type(document, "Cooker - Gas 600")));

        // Saved and opened again, a type keeps its gas input; one from before takes its template's.
        var gasFire = Type(document, "Gas Fire - Inset 600");
        gasFire.GasInput = 5500;
        var path = Path.Combine(Path.GetTempPath(), $"gas-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            Assert.Equal(5500, Type(opened, "Gas Fire - Inset 600").GasInput);
            Assert.Equal(27_400, Type(opened, "Boiler - Gas Combi").GasInput);
        }
        finally
        {
            File.Delete(path);
        }

        // A US project is offered the US's, an English one England's.
        var us = BimDocument.CreateDefault();
        us.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.True(ProjectTemplates.Fits(us, Type(us, "Range - Gas 30 in")));
        Assert.False(ProjectTemplates.Fits(us, Type(us, "Gas Fire - Inset 600")));
        Assert.True(ProjectTemplates.Fits(us, us.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.SteelSchedule40)));
        Assert.False(ProjectTemplates.Fits(document, document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.SteelSchedule40)));
    }
}
