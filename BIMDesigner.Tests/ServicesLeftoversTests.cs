using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;
using static BIMDesigner.Tests.VentilationTests;

namespace BIMDesigner.Tests;

/// <summary>The services' leftovers: a circuit's protective conductor by the adiabatic equation, and the air a gas appliance burns by Approved Document J, against figures worked by hand.</summary>
[Collection("Wpf")]
public class ServicesLeftoversTests
{
    private static MepSystem Make(BimDocument document, SystemKind kind, params Component[] fittings)
    {
        var (system, refusal) = MepSystems.Make(document, kind, fittings);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    private static (BimDocument Document, MepSystem Circuit) Cooker(double length, bool rcd)
    {
        var (document, _, _) = Row("Generic - 200mm", "Kitchen");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        var cooker = Make(document, SystemKind.Power, board, Put(document, "Oven and Hob - 600", 2000, 2600));
        (cooker.Curve, cooker.Breaker, cooker.Cable, cooker.CableLength, cooker.Rcd) = (BreakerCurve.C, 32, "4 mm²", length, rcd);
        return (document, cooker);
    }

    [Fact]
    public void ACircuitsProtectiveConductorTakesTheHeatOfAFault()
    {
        // A C32 cooker circuit, 20 m of 4 / 1.5 mm²: Zs 0.35 + (4.61 + 12.1) × 1.2 × 20 / 1000 = 0.751 Ω, so 0.95 × 230 / 0.751 = 291 A at its far end - under the C32's 320 A, so up
        // to 0.4 s: √(291² × 0.4) / 115 = 1.60 mm², more than its 1.5.
        var (document, circuit) = Cooker(20, false);
        var design = Circuits.Design(document, circuit)!;
        var zs = 0.35 + (4.61 + 12.1) * 1.2 * 20 / 1000;
        Assert.Equal(0.95 * 230 / zs, design.Fault, 6);
        Assert.Equal(0.4, design.FaultTime);
        Assert.Equal(0.95 * 230 / zs * Math.Sqrt(0.4) / 115, design.CpcNeeded, 6);
        Assert.InRange(design.CpcNeeded, 1.59, 1.61);
        Assert.Contains(Circuits.Faults(document, design), fault => fault.Contains("543.1.3"));
        Assert.Contains("of 1.60 (543.1.3)", Circuits.Describe(document, circuit));

        // 15 m: 336 A, past 320, the breaker's magnetic trip, 0.1 s: 0.92 mm².
        var (near, shorter) = Cooker(15, false);
        var quick = Circuits.Design(near, shorter)!;
        Assert.Equal(0.1, quick.FaultTime);
        Assert.InRange(quick.CpcNeeded, 0.91, 0.93);
        Assert.DoesNotContain(Circuits.Faults(near, quick), fault => fault.Contains("543.1.3"));

        // Behind an RCD the fault is cleared in 0.04 s: 291 × 0.2 / 115 = 0.51 mm².
        var (safe, guarded) = Cooker(20, true);
        var rcd = Circuits.Design(safe, guarded)!;
        Assert.Equal(0.04, rcd.FaultTime);
        Assert.InRange(rcd.CpcNeeded, 0.50, 0.52);
        Assert.DoesNotContain(Circuits.Faults(safe, rcd), fault => fault.Contains("543.1.3"));
    }

    [Fact]
    public void AnOpenFluedAppliancesRoomWantsAirFromOutside()
    {
        // A gas fire of 12 kW gross, 10.81 kW net: open-flued as usual for its kind, (10.81 - 7) × 500 = 1,905 mm² of permanent vent.
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        var fire = Put(document, "Gas Fire - Inset 600", rooms[0].Location.X, 300);
        var type = document.FindType<ComponentType>(fire.TypeId)!;
        type.GasInput = 12_000;
        Assert.Equal(ApplianceFlue.OpenFlued, type.FlueOf);
        Assert.Equal((12.0 / 1.11 - 7) * 500, GasPipes.AirWanted(type), 6);
        var problem = Assert.Single(GasPipes.AirSupplyProblems(document), each => each.Element == fire);
        Assert.Contains("1,905 mm²", problem.Text);
        Assert.Contains("has 0", problem.Text);

        // A background ventilator of 5,000 mm² in the room, taken as an air brick that does not close, gives it its air.
        Put(document, "Background Ventilator - 5000 mm²", rooms[0].Location.X + 1000, 2900);
        Assert.DoesNotContain(GasPipes.AirSupplyProblems(document), each => each.Element == fire);

        // Room-sealed, it wants none; at 7 kW net or less, none either.
        type.Flue = ApplianceFlue.RoomSealed;
        Assert.Equal(0, GasPipes.AirWanted(type));
        type.Flue = ApplianceFlue.Usual;
        type.GasInput = 7_000;
        Assert.Equal(0, GasPipes.AirWanted(type));
    }

    [Fact]
    public void AFluelessCookerWantsAWindowToOpen()
    {
        // The row's kitchen has no window: a gas cooker, flueless, wants one that opens.
        var (document, rooms, _) = Row("Generic - 200mm", "Kitchen");
        var cooker = Put(document, "Cooker - Gas 600", rooms[0].Location.X, 300);
        Assert.Equal(ApplianceFlue.Flueless, document.FindType<ComponentType>(cooker.TypeId)!.FlueOf);
        Assert.Contains(GasPipes.AirSupplyProblems(document), each => each.Element == cooker && each.Text.Contains("openable window"));
        Assert.Contains(MepSystems.Problems(document), each => each.Element == cooker && each.Text.Contains("Approved Document J"));

        // A combi boiler is room-sealed: nothing asked of its room.
        var boiler = Put(document, "Boiler - Gas Combi", rooms[0].Location.X + 1000, 300);
        Assert.Equal(ApplianceFlue.RoomSealed, document.FindType<ComponentType>(boiler.TypeId)!.FlueOf);
        Assert.DoesNotContain(GasPipes.AirSupplyProblems(document), each => each.Element == boiler);
    }

    [Fact]
    public void ABoardWithoutSurgeProtectionIsSaidSo()
    {
        // The built-in consumer unit has an SPD; one without is wanting by BS 7671 443.4 - and in the US by NEC 230.67.
        var (document, _) = Cooker(10, true);
        var board = document.All<Component>().Single(each => document.FindType<ComponentType>(each.TypeId)!.Form == ComponentForm.ConsumerUnit);
        var type = document.FindType<ComponentType>(board.TypeId)!;
        Assert.True(type.SurgeProtection);
        Assert.DoesNotContain(Circuits.Problems(document), each => each.Element == board && each.Text.Contains("443.4"));
        type.SurgeProtection = false;
        Assert.Contains(Circuits.Problems(document), each => each.Element == board && each.Text.Contains("443.4"));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Contains(Circuits.Problems(document), each => each.Element == board && each.Text.Contains("230.67"));
    }

    [Fact]
    public void AThreePhaseBoardSharesItsCircuitsAcrossItsPhases()
    {
        // Three 9.5 kW showers, 41.3 A each. On one phase the On-Site Guide takes the largest two whole and a quarter of the third: 92.9 A. On three, one a phase: 41.3 A the most loaded.
        var (document, rooms, _) = Row("Generic - 200mm", "Bathroom", "Bathroom", "Bathroom");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        foreach (var room in rooms)
        {
            var shower = Make(document, SystemKind.Power, board, Put(document, "Shower - Electric 9.5 kW", room.Location.X, 2400));
            shower.Use = CircuitUse.Shower;
        }

        var single = Circuits.Demand(document, board)!;
        Assert.Equal(9500 / 230.0 * 2.25, single.Demand, 6);
        Assert.Empty(single.Phases);

        document.FindType<ComponentType>(board.TypeId)!.Phases = 3;
        var three = Circuits.Demand(document, board)!;
        Assert.Equal(3, three.Phases.Count);
        Assert.All(three.Phases, phase => Assert.Single(phase.Circuits));
        Assert.Equal(9500 / 230.0, three.Demand, 6);
        Assert.Contains(three.Workings, line => line.StartsWith("three phases: L1 41.3 A", StringComparison.Ordinal) && line.Contains("0% out of balance"));
    }

    [Fact]
    public void ABoardsSurgeProtectionAndPhasesAreKept()
    {
        var document = BimDocument.CreateDefault();
        var board = document.TypesOf<ComponentType>().First(type => type.Name == "Consumer Unit - 12 Way");
        (board.SurgeProtection, board.Phases) = (false, 3);
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document)).FindType<ComponentType>(board.Id)!;
        Assert.False(reopened.SurgeProtection);
        Assert.Equal(3, reopened.Phases);
        Assert.Equal(3, board.Duplicate("Copy").Phases);
        Assert.Contains(board.GetTypeParameters(document), parameter => parameter.Name == "Surge Protection (SPD)");
    }

    [Fact]
    public void AnAppliancesFlueIsKeptWithItsType()
    {
        var document = BimDocument.CreateDefault();
        var boiler = document.TypesOf<ComponentType>().First(type => type.Name == "Boiler - Gas Combi");
        boiler.Flue = ApplianceFlue.OpenFlued;
        Assert.Equal(ApplianceFlue.OpenFlued, ProjectFile.FromJson(ProjectFile.ToJson(document)).FindType<ComponentType>(boiler.Id)!.Flue);
        Assert.Equal(ApplianceFlue.OpenFlued, boiler.Duplicate("Copy").Flue);
        Assert.Contains(boiler.GetTypeParameters(document), parameter => parameter.Name == "Flue (gas)");
    }
}
