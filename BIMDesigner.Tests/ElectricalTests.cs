using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;
using static BIMDesigner.Tests.VentilationTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Electrical and lighting (round M6): circuits run from their board and designed as BS 7671 and the
/// On-Site Guide have them - design current, breaker, cable as installed, voltage drop, earth loop,
/// standard socket circuits, RCDs, the board's demand after diversity - and in the US by the NEC; each
/// room's light by the lumen method against its target, makers' photometric files, switching, and where
/// the alarms go; and all of it saved, scheduled, costed and sent out as IFC.
/// </summary>
[Collection("Wpf")]
public class ElectricalTests
{
    private static MepSystem Make(BimDocument document, SystemKind kind, params Component[] fittings)
    {
        var (system, refusal) = MepSystems.Make(document, kind, fittings);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    private static Point3D Port(BimDocument document, Component fitting, SystemKind kind = SystemKind.Power) =>
        Connectors.Placed(document, fitting).First(placed => placed.Connector.Kind == kind).At;

    [Fact]
    public void ALightingCircuitRunsFromTheBoardAndIsSizedForItsLoad()
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        var near = Put(document, "Ceiling Light - Flush", 1000, 1500);
        var far = Put(document, "Ceiling Light - Flush", 3000, 1500);
        var circuit = Make(document, SystemKind.Power, board, far, near);
        Assert.Equal(CircuitUse.Lighting, circuit.Use);
        Assert.Equal(board.Id, circuit.SourceId);

        // Up into the void over the ceiling, along it square to the walls and down to the nearest light, then on to the next.
        var (from, a, b) = (Port(document, board), Port(document, near), Port(document, far));
        var first = Math.Abs(2500 - from.Z) + Math.Abs(a.X - from.X) + Math.Abs(a.Y - from.Y) + Math.Abs(2500 - a.Z);
        var second = first + Math.Abs(2500 - a.Z) + Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) + Math.Abs(2500 - b.Z);
        var (length, stops) = Circuits.Route(document, circuit);
        Assert.Equal(new[] { near, far }, stops.Select(stop => stop.Fitting));
        Assert.Equal(first / 1000, stops[0].Distance, 6);
        Assert.Equal(second / 1000, length, 6);

        // Two lights of 15 W: a B6 on 1.0 mm² - its drop worked light by light, its earth loop from Ze and the conductors at 70 °C.
        var design = Circuits.Design(document, circuit)!;
        Assert.Equal((6.0, "1.0 mm²"), (design.Breaker, design.Cable.Name));
        Assert.True(design.BreakerChosen && design.CableChosen);
        Assert.Equal(30 / 230.0, design.Current, 6);
        Assert.Equal(16, design.Capacity, 6);
        Assert.Equal(44 * (15 / 230.0 * first / 1000 + 15 / 230.0 * second / 1000) / 1000, design.Drop, 6);
        Assert.Equal(230 * 0.03, design.DropLimit, 6);
        Assert.Equal(0.35 + (18.1 + 18.1) * 1.2 * second / 1000 / 1000, design.Impedance, 6);
        Assert.Equal(0.95 * 230 / 30, design.ImpedanceLimit, 6);
        Assert.Contains("B6 RCBO, 1.0/1.0 mm² twin and earth clipped direct", Circuits.Describe(document, circuit));
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == circuit);

        // A home's lighting without an RCD.
        circuit.Rcd = false;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == circuit && problem.Text.Contains("411.3.4"));
    }

    [Fact]
    public void ARingFinalIsTheOnSiteGuidesStandardCircuitAndKeepsToIt()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room", "Bedroom 1");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        var sockets = new[] { (1000.0, 200.0), (3000, 200), (5200, 200), (7200, 200) }.Select(at => Put(document, "Socket - Double", at.Item1, at.Item2)).ToArray();
        var ring = Make(document, SystemKind.Power, sockets.Prepend(board).ToArray());
        Assert.Equal(CircuitUse.RingFinal, ring.Use);

        var design = Circuits.Design(document, ring)!;
        Assert.Equal((32.0, "2.5 mm²"), (design.Breaker, design.Cable.Name));
        Assert.True(design.Standard);
        Assert.Equal(32, design.Current, 6);
        Assert.Equal((100.0, 106.0), (design.AreaLimit, design.LengthLimit));
        Assert.Equal(rooms.Sum(room => room.GetBoundary(document).Area) / 1e6, design.Area, 6);

        // Back to the board: longer than out to the last socket.
        Assert.True(design.Length > design.Stops[^1].Distance + 1);

        // 2.5 mm² in an insulated stud wall, not touching its lining: 13.5 A - short of a ring's 20 A, its legs sharing the 32 A.
        (ring.Breaker, ring.Cable, ring.Method) = (32, "2.5 mm²", CableMethod.StudWall);
        var faults = Circuits.Faults(document, Circuits.Design(document, ring)!);
        Assert.Contains(faults, fault => fault.Contains("433.1.204"));
        Assert.DoesNotContain(faults, fault => fault.Contains("433.1.1,"));
        Assert.Equal("6 mm²", Circuits.Choose(document, ring, 32).Cable.Name);
        ring.Method = CableMethod.ClippedDirect;

        // Longer than the On-Site Guide takes it, and with no RCD on its sockets.
        ring.CableLength = 120;
        Assert.Contains(Circuits.Faults(document, Circuits.Design(document, ring)!), fault => fault.Contains("Table 7.1"));
        ring.Rcd = false;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == ring && problem.Text.Contains("411.3.3"));
    }

    [Fact]
    public void ACookerAndAnElectricShowerAreDesignedAsTheOnSiteGuideHasThem()
    {
        var (document, _, _) = Row("Generic - 200mm", "Kitchen", "Bathroom");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);

        // A cooker of 10 kW: 10 A and 30% of the rest; a B32 on 4 mm², the smallest that carries it clipped direct.
        var cooker = Make(document, SystemKind.Power, board, Put(document, "Oven and Hob - 600", 2000, 2600));
        Assert.Equal(CircuitUse.Cooker, cooker.Use);
        var design = Circuits.Design(document, cooker)!;
        Assert.Equal(10 + 0.3 * (10000 / 230.0 - 10), design.Current, 6);
        Assert.Equal((32.0, "4 mm²"), (design.Breaker, design.Cable.Name));

        // An electric shower: fed cold water and its own circuit, no hot; 9.5 kW whole, on a B45.
        var showerType = Type(document, "Shower - Electric 9.5 kW");
        Assert.DoesNotContain(Connectors.Of(showerType), connector => connector.Kind == SystemKind.HotWater);
        Assert.Contains(Connectors.Of(showerType), connector => connector.Kind == SystemKind.Power);
        var shower = Make(document, SystemKind.Power, board, Put(document, "Shower - Electric 9.5 kW", 6500, 2400));
        shower.Use = CircuitUse.Shower;
        design = Circuits.Design(document, shower)!;
        Assert.Equal(9500 / 230.0, design.Current, 6);
        Assert.Equal(45, design.Breaker);
        Assert.True(design.Capacity >= 45);

        // In a bathroom, with no RCD.
        shower.Rcd = false;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == shower && problem.Text.Contains("701.411.3.3"));
    }

    [Fact]
    public void WithoutAnRcdTheEarthLoopLimitsHowLongACircuitRuns()
    {
        var (document, _, _) = Row("Generic - 200mm", "Kitchen");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        var cooker = Make(document, SystemKind.Power, board, Put(document, "Oven and Hob - 600", 2000, 2600));
        (cooker.Curve, cooker.Breaker, cooker.Cable, cooker.CableLength, cooker.Rcd) = (BreakerCurve.C, 32, "4 mm²", 25, false);

        // A C32 disconnects in 0.4 s up to 0.95 x 230 / 320 Ω: 0.35 and 25 m of 4/1.5 mm² at 70 °C is more.
        var design = Circuits.Design(document, cooker)!;
        Assert.Equal(0.35 + (4.61 + 12.1) * 1.2 * 25 / 1000, design.Impedance, 6);
        Assert.Equal(0.95 * 230 / 320, design.ImpedanceLimit, 6);
        Assert.Contains(Circuits.Faults(document, design), fault => fault.Contains("Table 41.3"));

        // An RCD gives the protection instead.
        cooker.Rcd = true;
        Assert.DoesNotContain(Circuits.Faults(document, Circuits.Design(document, cooker)!), fault => fault.Contains("Table 41.3"));
    }

    [Fact]
    public void TheBoardsDemandIsTakenAfterTheOnSiteGuidesDiversity()
    {
        var (document, _, _) = Row("Generic - 200mm", "Kitchen", "Living Room");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        Make(document, SystemKind.Power, board, Put(document, "Ceiling Light - Flush", 2000, 1500), Put(document, "Ceiling Light - Flush", 6000, 1500));
        Make(document, SystemKind.Power, board, Put(document, "Socket - Double", 1000, 200), Put(document, "Socket - Double", 6000, 200));
        Make(document, SystemKind.Power, board, Put(document, "Oven and Hob - 600", 2000, 2600));

        var demand = Circuits.Demand(document, board)!;
        Assert.Equal(3, demand.Circuits.Count);
        Assert.Equal((12, 100.0), (demand.Ways, demand.Supply));
        Assert.Equal(30 / 230.0 * 0.66 + Circuits.CookerCurrent(10000 / 230.0) + 32, demand.Demand, 6);

        // On a 40 A supply, and a board of two ways.
        Type(document, "Consumer Unit - 12 Way").SupplyRating = 40;
        Type(document, "Consumer Unit - 12 Way").Ways = 2;
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == board && problem.Text.Contains("Table A2"));
        Assert.Contains(problems, problem => problem.Element == board && problem.Text.Contains("3 circuits on its 2 ways"));

        // Size Circuits sets what it chose, as one step to undo.
        var sized = Circuits.Sized(document, Circuits.Of(document));
        Assert.Equal(3, sized.Count);
        var command = new SetCircuitsCommand(sized);
        command.Redo();
        Assert.All(Circuits.Of(document), circuit => Assert.True(circuit.Breaker > 0 && circuit.Cable.Length > 0));
        command.Undo();
        Assert.All(Circuits.Of(document), circuit => Assert.Equal(0, circuit.Breaker));
    }

    [Fact]
    public void InTheUsCircuitsKeepToTheNecAndTheLoadIsWorkedByArticle220()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Kitchen", "Bathroom", "Bedroom 1");
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var board = Put(document, "Load Center - 200 A 40 Space", 300, 1500);
        var kitchen = Make(document, SystemKind.Power, board, Put(document, "Socket - Double", 1000, 200), Put(document, "Socket - Double", 3000, 200));

        // A kitchen's receptacles: 20 A on 12 AWG at 120 V, designed for 80% of it - made with the GFCI and AFCI the NEC asks of them.
        var design = Circuits.Design(document, kitchen)!;
        Assert.Equal((20.0, "12 AWG", 120.0, 16.0), (design.Breaker, design.Cable.Name, design.Voltage, design.Current));
        Assert.True(kitchen.Rcd && kitchen.ArcFault);
        kitchen.ArcFault = false;
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Text.Contains("210.11(C)(1)") && problem.Text.Contains("one circuit"));
        Assert.Contains(problems, problem => problem.Element == kitchen && problem.Text.Contains("210.12(B)"));
        kitchen.ArcFault = true;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == kitchen && problem.Text.Contains("210.12(B)"));

        // 12 AWG on a 30 A breaker.
        (kitchen.Breaker, kitchen.Cable) = (30, "12 AWG");
        Assert.Contains(Circuits.Faults(document, Circuits.Design(document, kitchen)!), fault => fault.Contains("240.4(D)"));

        // A bathroom's receptacle sharing a circuit with a bedroom's.
        var shared = Make(document, SystemKind.Power, board, Put(document, "Socket - Double", 5000, 200), Put(document, "Socket - Double", 9500, 200));
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == shared && problem.Text.Contains("210.11(C)(3)"));

        // The load: 3 VA a sq ft, two small-appliance circuits and a laundry's at 1500 VA, Table 220.45, a 10 kW range at Table 220.55's 8 kW.
        Put(document, "Oven and Hob - 600", 2000, 2600);
        var feet = rooms.Sum(room => room.GetBoundary(document).Area) / 1e6 * 10.7639;
        var general = 3 * feet + 1500 * 3;
        var (standard, optional, _) = Circuits.UsLoad(document);
        Assert.Equal(3000 + (general - 3000) * 0.35 + 8000, standard, 6);
        Assert.Equal(Math.Min(general + 10000, 10000) + Math.Max(general + 10000 - 10000, 0) * 0.4, optional, 6);
        Assert.Equal(Math.Min(standard, optional) / 240, Circuits.Demand(document, board)!.Demand, 6);

        // A service under 100 A.
        Type(document, "Load Center - 200 A 40 Space").SupplyRating = 60;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == board && problem.Text.Contains("230.79(C)"));
    }

    [Fact]
    public void EachRoomsLightIsWorkedOutByTheLumenMethodAgainstItsTarget()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        Put(document, "Ceiling Light - Flush", 2000, 1500);
        var light = Lighting.Of(document, rooms[0])!;
        Assert.Equal(LitRoom.Living, light.Kind);
        Assert.Equal(150, light.Target!.Lux);

        // 12 m² and 14 m round, the light 2.3 m over the floor: K = 2A / (Hm x P); a diffuse fitting's UF there, a maintenance factor of 0.8.
        var index = 2 * 12.0 / (2.3 * 14);
        Assert.Equal(index, light.RoomIndex, 6);
        Assert.Equal(1350 * Lighting.UtilisationFactor(LightDistribution.Diffuse, index) * 0.8 / 12, light.Average, 6);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == rooms[0] && problem.Text.Contains("BRE took 150 lux for a living"));
        Assert.Contains("light 38 lux on the floor of 150 lux", Services.Verdict(document, rooms[0]));

        // Utilisation factors read from the makers' tables.
        Assert.Equal(0.64, Lighting.UtilisationFactor(LightDistribution.Downlight, 1.0), 6);
        Assert.Equal(0.59, Lighting.UtilisationFactor(LightDistribution.Diffuse, 1.5), 6);

        // In the US the IES's 3 fc for a living room is met.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == rooms[0] && problem.Text.Contains("IES"));
    }

    private const string Ies =
        "IESNA:LM-63-2002\n[TEST] 1\n[MANUFAC] Test\nTILT=NONE\n1 -1 1 3 1 1 2 -0.09 0.09 0.06\n1 1 7.5\n0 45 90\n0\n400 200 0\n";

    private static readonly string Ldt = string.Join("\n", new[]
    {
        "Test", "1", "1", "1", "0", "3", "45", "R1", "Downlight", "D1", "d1.ldt", "2026", "90", "0", "60", "80", "0", "0", "0", "0", "0",
        "100", "80", "1", "0", "1", "1", "LED", "1000", "3000", "80", "10"
    }.Concat(Enumerable.Repeat("0.5", 10)).Concat(new[] { "0", "0", "45", "90", "300", "150", "0" }));

    [Fact]
    public void AMakersPhotometricFileIsReadAndGivesTheLightUnderIt()
    {
        // An IES file, round: 400 cd straight down falling to none at 90°.
        var ies = Photometry.Read(Ies, "test.ies");
        Assert.Equal(7.5, ies.Watts);
        Assert.Equal(400, ies.Intensity(0, 0), 6);
        Assert.Equal(300, ies.Intensity(123, 22.5), 6);
        Assert.Equal(800 * Math.PI * (1 - 2 / Math.PI), ies.Lumens, 0.02 * ies.Lumens);

        // A EULUMDAT file: 300 cd for each 1000 lm of its lamp straight down, its light output ratio 80%.
        var ldt = Photometry.Read(Ldt, "d1.ldt");
        Assert.Equal((800.0, 10.0), (ldt.Lumens, ldt.Watts));
        Assert.Equal(300, ldt.Intensity(77, 0), 6);
        Assert.Equal(150, ldt.Intensity(200, 45), 6);

        // Loaded into the downlight: its lumens and watts taken; the direct light at most I / h² under it.
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        var type = Type(document, "Downlight - Recessed");
        var command = new SetPhotometryCommand(type, ies);
        command.Redo();
        Assert.Equal((ies.Lumens, 7.5), (type.Lumens, type.Load));
        Put(document, "Downlight - Recessed", 2000, 1500);
        var light = Lighting.Of(document, rooms[0])!;
        Assert.True(light.Brightest <= 400 / (2.34 * 2.34) + 1e-6 && light.Brightest > 50);
        Assert.True(light.Darkest < light.Brightest);
        command.Undo();
        Assert.Null(type.Photometry);
        Assert.Equal(650, type.Lumens);
    }

    [Fact]
    public void LightsAreSwitchedAndAStairOfSixRisersIsSwitchedAtEachFloorInTheUs()
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room");
        var light = Put(document, "Ceiling Light - Flush", 2000, 1500);
        var turn = Put(document, "Switch - One Gang", 300, 500);
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == light && problem.Text.StartsWith("no switch controls it"));
        Assert.Contains(problems, problem => problem.Element == turn && problem.Text.StartsWith("it switches no lights"));
        var switching = Make(document, SystemKind.Switching, turn, light);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => (problem.Element == light || problem.Element == turn || problem.Element == switching) && problem.Text.Contains("switch"));

        // A stair up to the first floor, its light switched only at its foot.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var stair = new Stair { LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Start = new Point2D(1000, 600), Angle = 0, TypeId = document.TypesOf<StairType>().First().Id };
        document.Add(stair);
        var over = Put(document, "Ceiling Light - Flush", 2000, 1000);
        var foot = Put(document, "Switch - One Gang", 900, 300);
        var stairs = Make(document, SystemKind.Switching, foot, over);
        Assert.True(Stairs.Layout(document, stair).Risers >= 6);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == stair && problem.Text.Contains("210.70(A)(2)(c)"));

        var head = new Component { TypeId = Type(document, "Switch - One Gang").Id, TypeKind = BuiltInCategory.LightingDevices, LevelId = document.Levels[1].Id, Location = new Point2D(3500, 1000), Elevation = 1050 };
        document.Add(head);
        stairs.Members.Add(head.Id);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == stair);
    }

    [Fact]
    public void SmokeAlarmsGoWhereApprovedDocumentBAndTheIrcPutThem()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Hall", "Bedroom 1");
        Put(document, "Consumer Unit - 12 Way", 300, 1500);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == rooms[0] && problem.Text.Contains("LD3"));
        Put(document, "Smoke Alarm", rooms[0].Location.X, rooms[0].Location.Y);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("LD3"));

        // In the US, one in each bedroom as well.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == rooms[1] && problem.Text.Contains("R314.3"));
        Put(document, "Smoke Alarm", rooms[1].Location.X, rooms[1].Location.Y);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("R314.3"));
    }

    [Fact]
    public void CircuitsAreSavedScheduledCostedAndSentOut()
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        var light = Put(document, "Downlight - Recessed", 2000, 1500);
        var turn = Put(document, "Switch - One Gang", 300, 500);
        var circuit = Make(document, SystemKind.Power, board, light, turn);
        Make(document, SystemKind.Switching, turn, light);
        (circuit.Breaker, circuit.Curve, circuit.Rcd, circuit.ArcFault, circuit.Cable, circuit.Method, circuit.Grouped, circuit.CableLength) =
            (10, BreakerCurve.C, false, true, "1.5 mm²", CableMethod.UnderDeepInsulation, 3, 14.5);
        new SetPhotometryCommand(Type(document, "Downlight - Recessed"), Photometry.Read(Ies, "test.ies")).Redo();

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = loaded.All<MepSystem>().Single(system => system.Kind == SystemKind.Power);
        Assert.Equal((10.0, BreakerCurve.C, false, true, "1.5 mm²", CableMethod.UnderDeepInsulation, 3, 14.5),
            (again.Breaker, again.Curve, again.Rcd, again.ArcFault, again.Cable, again.Method, again.Grouped, again.CableLength));
        Assert.Single(loaded.All<MepSystem>(), system => system.Kind == SystemKind.Switching);
        var downlight = Type(loaded, "Downlight - Recessed");
        Assert.Equal((7.5, LightDistribution.Downlight), (downlight.Load, downlight.Distribution));
        Assert.Equal(400, downlight.Photometry!.Intensity(0, 0), 6);
        Assert.Equal((12, 100.0, 0.35), (Type(loaded, "Consumer Unit - 12 Way").Ways, Type(loaded, "Consumer Unit - 12 Way").SupplyRating, Type(loaded, "Consumer Unit - 12 Way").ExternalImpedance));

        // The board schedule, a line for its cable by the metre and one for its breaker.
        var schedule = Schedule.Run(document, document.Schedules.First(each => each.Name == "Board Schedule"));
        Assert.Single(schedule.Rows);
        var lines = CostPlanning.Lines(document).Where(line => line.Element == circuit).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal("5.8", line.Code));
        Assert.Equal((decimal)14.5 * 1.10m, lines[0].Total);

        // In IFC4: the circuit's breaker at its board, the switching a control system.
        using var model = IfcExport.Build(document);
        var device = Assert.Single(model.Instances.OfType<IIfcProtectiveDevice>());
        Assert.Equal(IfcProtectiveDeviceTypeEnum.CIRCUITBREAKER, device.PredefinedType);
        Assert.Equal("C10 MCB", device.ObjectType?.ToString());
        Assert.Contains(model.Instances.OfType<IIfcDistributionSystem>(), system => system.PredefinedType == IfcDistributionSystemEnum.CONTROL);
    }

    [Fact]
    public void ACircuitsPropertiesShowWhatIsSetAndItsUseIsToldByWhatIsOnIt()
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room", "Bathroom");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        var lights = Make(document, SystemKind.Power, board, Put(document, "Ceiling Light - Flush", 2000, 1500), Put(document, "Smoke Alarm", 2500, 1500));
        Assert.Equal(CircuitUse.Lighting, lights.Use);

        // Its breaker changed in Properties is what Properties then show, and what its design is worked on.
        var parameters = lights.GetInstanceParameters(document).ToList();
        var breaker = parameters.Single(parameter => parameter.Definition == MepSystemParameters.Breaker);
        var capacity = parameters.Single(parameter => parameter.Definition == MepSystemParameters.Capacity);
        Assert.Equal(6.0, breaker.Value);
        Assert.True(breaker.TrySet(10.0));
        Assert.Equal(10.0, breaker.Value);
        Assert.Equal(10.0, Circuits.Design(document, lights)!.Breaker);
        Assert.Equal(Math.Round(Circuits.Design(document, lights)!.Capacity, 1), (double)capacity.Value!, 6);

        // An electric shower's own circuit is a shower's.
        var shower = Make(document, SystemKind.Power, board, Put(document, "Shower - Electric 9.5 kW", 6500, 2400));
        Assert.Equal(CircuitUse.Shower, shower.Use);
    }

    [Fact]
    public void InTheUsBunchedCableIsTakenDownFromItsNinetyDegreeRating()
    {
        var (document, _, _) = Row("Generic - 200mm", "Kitchen");
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var board = Put(document, "Load Center - 200 A 40 Space", 300, 1500);
        var kitchen = Make(document, SystemKind.Power, board, Put(document, "Socket - Double", 1000, 200), Put(document, "Socket - Double", 3000, 200));
        var twelve = Circuits.Sizes(true).Single(size => size.Name == "12 AWG");

        // 12 AWG bunched as ten conductors: 30 A at 90 °C by half, 15 A - under the 16 A it carries and its 20 A breaker, not rounded up for receptacles.
        kitchen.Grouped = 5;
        var design = Circuits.Evaluate(document, kitchen, 20, twelve);
        Assert.Equal(15, design.Capacity, 6);
        var faults = Circuits.Faults(document, design);
        Assert.Contains(faults, fault => fault.Contains("210.19(A)(1)"));
        Assert.Contains(faults, fault => fault.Contains("240.4(B)") && fault.Contains("15 A at most on a circuit of receptacles"));

        // Bunched as six conductors: 0.8 of 30 A is more than 20, so it carries its 60 °C 20 A.
        kitchen.Grouped = 3;
        Assert.Equal(20, Circuits.Evaluate(document, kitchen, 20, twelve).Capacity, 6);

        // Size Circuits goes to 10 AWG where 12 is bunched too tight.
        kitchen.Grouped = 5;
        Assert.Equal("10 AWG", Circuits.Choose(document, kitchen, 20).Cable.Name);

        // A range over 12 kW: 5% for each kW and each major fraction of one.
        Assert.Equal(8000, Circuits.RangeDemand(12.4), 6);
        Assert.Equal(8000, Circuits.RangeDemand(12.5), 6);
        Assert.Equal(8400, Circuits.RangeDemand(12.6), 6);
        Assert.Equal(8400, Circuits.RangeDemand(13.4), 6);
        Assert.Equal(8800, Circuits.RangeDemand(13.6), 6);

        // A bedroom's lights: no GFCI asked, an AFCI.
        var (house, _, _) = Row("Generic - 200mm", "Bedroom 1");
        house.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var panel = Put(house, "Load Center - 200 A 40 Space", 300, 1500);
        var bedroom = Make(house, SystemKind.Power, panel, Put(house, "Ceiling Light - Flush", 2000, 1500));
        Assert.False(bedroom.Rcd);
        Assert.True(bedroom.ArcFault);
    }

    [Fact]
    public void GroupingBeyondNineAndTheEarthLoopOfAStandardCircuit()
    {
        // Table 4C1 beyond nine circuits.
        Assert.Equal(0.50, Circuits.GroupingFactor(9), 6);
        Assert.Equal(0.45, Circuits.GroupingFactor(10), 6);
        Assert.Equal(0.45, Circuits.GroupingFactor(12), 6);
        Assert.Equal(0.41, Circuits.GroupingFactor(16), 6);
        Assert.Equal(0.38, Circuits.GroupingFactor(20), 6);

        // A standard ring on a supply of 1.5 Ω and no RCD: the On-Site Guide's 106 m does not make its earth loop good.
        var (document, _, _) = Row("Generic - 200mm", "Living Room", "Bedroom 1");
        Type(document, "Consumer Unit - 12 Way").ExternalImpedance = 1.5;
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        var sockets = new[] { (1000.0, 200.0), (3000, 200), (5200, 200), (7200, 200) }.Select(at => Put(document, "Socket - Double", at.Item1, at.Item2)).ToList();
        var ring = Make(document, SystemKind.Power, sockets.Prepend(board).ToArray());
        (ring.Breaker, ring.Cable, ring.Rcd) = (32, "2.5 mm²", false);
        var design = Circuits.Design(document, ring)!;
        Assert.True(design.Standard);
        Assert.Contains(Circuits.Faults(document, design), fault => fault.Contains("Table 41.3"));
    }

    [Fact]
    public void PhotometrySymmetricAboutTheNinetyDegreePlaneIsSpreadTheRightWay()
    {
        // IES planes 90 to 270: the 0° plane is the 180°'s mirror.
        var ies = Photometry.Read("IESNA:LM-63-2002\nTILT=NONE\n1 -1 1 3 3 1 2 -0.09 0.09 0.06\n1 1 7.5\n0 45 90\n90 180 270\n"
                                  + "400 100 0\n400 200 0\n400 300 0\n", "across.ies");
        Assert.Equal(200, ies.Intensity(0, 45), 6);
        Assert.Equal(200, ies.Intensity(180, 45), 6);
        Assert.Equal(100, ies.Intensity(90, 45), 6);
        Assert.Equal(300, ies.Intensity(270, 45), 6);

        // EULUMDAT symmetry 3: its planes from C270 round through C0 to C90.
        var lines = Ldt.Split('\n').ToList();
        (lines[2], lines[3], lines[4]) = ("3", "4", "90");
        const int angles = 26 + 6 + 10;
        lines.RemoveRange(angles, lines.Count - angles);
        lines.AddRange(new[] { "0", "90", "180", "270", "0", "45", "90", "300", "150", "0", "200", "100", "0", "100", "50", "0" });
        var ldt = Photometry.Read(string.Join("\n", lines), "across.ldt");
        Assert.Equal(150, ldt.Intensity(270, 45), 6);
        Assert.Equal(100, ldt.Intensity(0, 45), 6);
        Assert.Equal(50, ldt.Intensity(90, 45), 6);
        Assert.Equal(100, ldt.Intensity(180, 45), 6);
    }
}
