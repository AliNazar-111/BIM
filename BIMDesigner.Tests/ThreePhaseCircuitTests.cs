using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;
using static BIMDesigner.Tests.VentilationTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Three-phase circuits and armoured cable (round 78): a circuit across all three phases of a three-phase board at 400 V, its lines' current
/// P / (√3 × 400), wired in five-core armoured cable rated by BS 7671 Table 4D4A and dropping by Table 4D4B's three-phase column; and
/// three-core armoured for one phase in the ground or in free air, where Table 4D5 does not rate twin and earth - against figures worked by hand.
/// </summary>
[Collection("Wpf")]
public class ThreePhaseCircuitTests
{
    private static MepSystem Make(BimDocument document, params Component[] fittings)
    {
        var (system, refusal) = MepSystems.Make(document, SystemKind.Power, fittings);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    /// <summary>A plant room with a three-phase board and an 11 kW heat pump on a circuit of its own, its cable run so long.</summary>
    private static (BimDocument Document, Component Board, MepSystem Circuit) Plant(double length, double load = 11000)
    {
        var (document, _, _) = Row("Generic - 200mm", "Plant");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        document.FindType<ComponentType>(board.TypeId)!.Phases = 3;
        var pump = Put(document, "Heat Pump - Air Source 7 kW", 2500, 1500);
        document.FindType<ComponentType>(pump.TypeId)!.Load = load;
        var circuit = Make(document, board, pump);
        circuit.CableLength = length;
        return (document, board, circuit);
    }

    [Fact]
    public void AThreePhaseCircuitRunsAt400VEachLineCarryingAThirdOfItsLoad()
    {
        // On one phase, 11 kW at 230 V is 47.8 A. Across three at 400 V, each line carries 11000 / (√3 × 400) = 15.88 A: a B16, and 1.5 mm²
        // five-core armoured clipped direct carries 18 A (Table 4D4A). Over 20 m it drops 25 mV/A/m × 15.88 A × 20 m = 7.94 V (Table 4D4B), 2.0% of 400.
        var (document, board, circuit) = Plant(20);
        var single = Circuits.Design(document, circuit)!;
        Assert.Equal((230.0, 11000 / 230.0), (single.Voltage, single.Current));
        Assert.False(single.ThreePhase);

        circuit.Phase = MepSystem.AllPhases;
        var line = 11000 / (Math.Sqrt(3) * 400);
        var design = Circuits.Design(document, circuit)!;
        Assert.True(design.ThreePhase);
        Assert.Equal(400, design.Voltage);
        Assert.Equal(line, design.Current, 9);
        Assert.Equal((16.0, "1.5 mm² 5-core SWA", 18.0), (design.Breaker, design.Cable.Name, design.Capacity));
        Assert.Equal(25 * line * 20 / 1000, design.Drop, 9);
        Assert.Equal(20, design.DropLimit, 9);
        Assert.Empty(Circuits.Problems(document, circuit));
        Assert.Contains("1.5 mm² 5-core SWA across three phases clipped direct (C)", Circuits.Describe(document, circuit));
        Assert.Contains("at 400 V three-phase", Circuits.Describe(document, circuit));

        // Over 60 m: 1.5 mm² drops 23.8 V, 5.95% - past Appendix 4's 5% - so 2.5 mm², 15 × 15.88 × 60 = 14.3 V, 3.6%.
        circuit.CableLength = 60;
        design = Circuits.Design(document, circuit)!;
        Assert.Equal("2.5 mm² 5-core SWA", design.Cable.Name);
        Assert.Equal(15 * line * 60 / 1000, design.Drop, 9);
        circuit.Cable = "1.5 mm² 5-core SWA";
        Assert.Contains(Circuits.Problems(document, circuit), problem => problem.Text.Contains($"voltage drops {25 * line * 60 / 1000:0.0} V (6.0%)"));
        circuit.Cable = string.Empty;

        // On the board, its line current on each phase: heating and power at 10 A and half the rest, 12.9 A a phase.
        var demand = Circuits.Demand(document, board)!;
        Assert.All(demand.Phases, phase => Assert.Contains(design.Circuit, phase.Circuits.Select(each => each.Circuit)));
        Assert.All(demand.Phases, phase => Assert.Equal(10 + (line - 10) / 2, phase.Demand, 9));

        // Offered in Properties, its cables five-core; kept with the project.
        var phase = circuit.GetInstanceParameters(document).Single(parameter => parameter.Definition == MepSystemParameters.Phase);
        Assert.Equal("L1, L2 and L3 (400 V)", phase.Value);
        Assert.Contains("L1, L2 and L3 (400 V)", phase.AllowedValues!);
        Assert.All(circuit.GetInstanceParameters(document).Single(parameter => parameter.Definition == MepSystemParameters.Cable).AllowedValues!, name => Assert.EndsWith("5-core SWA", name));
        Assert.Equal(MepSystem.AllPhases, ProjectFile.FromJson(ProjectFile.ToJson(document)).Find<MepSystem>(circuit.Id)!.Phase);

        // On a board of one phase it cannot be.
        document.FindType<ComponentType>(board.TypeId)!.Phases = 1;
        Assert.False(Circuits.Design(document, circuit)!.ThreePhase);
        Assert.Contains(Circuits.Problems(document, circuit), problem => problem.Text == "it is set to run across three phases, but its board has one");
    }

    [Fact]
    public void SocketsAndLightsAreNotPutOnThreePhases()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Workshop");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        document.FindType<ComponentType>(board.TypeId)!.Phases = 3;
        var sockets = Make(document, board, Put(document, "Socket - Double", 2000, 200), Put(document, "Socket - Double", 3000, 200));
        sockets.Phase = MepSystem.AllPhases;
        Assert.Contains(Circuits.Problems(document, sockets), problem => problem.Text.StartsWith("its sockets take one phase"));

        var light = document.TypesOf<ComponentType>().First(type => type.Kind == BuiltInCategory.LightingFixtures);
        var lights = Make(document, board, Put(document, light.Name, rooms[0].Location.X, rooms[0].Location.Y));
        lights.Phase = MepSystem.AllPhases;
        Assert.Contains(Circuits.Problems(document, lights), problem => problem.Text.StartsWith("its lights take one phase"));
    }

    [Fact]
    public void ArmouredCableInTheGroundIsRatedByTable4D4A()
    {
        // A 3 kW heat pump down the garden, 30 m away: 13.0 A, a B16. Direct in the ground (D2) Table 4D5 gives twin and earth no rating;
        // Table 4D4A gives 1.5 mm² three-core armoured 22 A, and it drops 29 × 13.04 × 30 = 11.3 V, 4.9% of 230.
        var (document, _, circuit) = Plant(30, 3000);
        circuit.Method = CableMethod.InGround;
        var design = Circuits.Design(document, circuit)!;
        Assert.Equal((16.0, "1.5 mm² 3-core SWA", 22.0), (design.Breaker, design.Cable.Name, design.Capacity));
        Assert.Equal(29 * (3000 / 230.0) * 30 / 1000, design.Drop, 9);
        Assert.Empty(Circuits.Problems(document, circuit));
        Assert.Equal(2.20m, Circuits.Rates(false, design).Cable);

        // Twin and earth set in the ground is not rated there; armoured set in loft insulation is not rated there either.
        circuit.Cable = "2.5 mm²";
        Assert.Contains(Circuits.Problems(document, circuit), problem => problem.Text == "BS 7671 Table 4D5 rates twin and earth on a surface or in plaster and insulation, not direct in the ground (D2) - wire it in armoured cable");
        (circuit.Cable, circuit.Method) = ("2.5 mm² 3-core SWA", CableMethod.UnderInsulation);
        Assert.Contains(Circuits.Problems(document, circuit), problem => problem.Text == "BS 7671 Table 4D4A rates armoured cable clipped direct, in free air or in the ground, not under up to 100 mm of loft insulation (100)");

        // In ducts (D1) and in free air (E) by their own columns; clipped direct twin and earth is still what is chosen.
        circuit.Cable = string.Empty;
        circuit.Method = CableMethod.InDucts;
        Assert.Equal(22.0, Circuits.Design(document, circuit)!.Capacity);
        circuit.Method = CableMethod.InFreeAir;
        Assert.Equal(22.0, Circuits.Design(document, circuit)!.Capacity);
        circuit.Method = CableMethod.ClippedDirect;
        Assert.Equal("1.5 mm²", Circuits.Design(document, circuit)!.Cable.Name);
    }
}
