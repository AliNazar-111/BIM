using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using static BIMDesigner.Tests.VentilationTests;

namespace BIMDesigner.Tests;

/// <summary>
/// What a system's Properties say is wanting in it is worked out from its own checks alone - not the
/// whole model's - and comes to exactly what the whole model's checks say of it.
/// </summary>
[Collection("Wpf")]
public class SystemWantingTests
{
    private static MepSystem Make(BimDocument document, SystemKind kind, params Component[] fittings)
    {
        var (system, refusal) = MepSystems.Make(document, kind, fittings);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    /// <summary>A home with every kind of system on it, many of them wanting something.</summary>
    private static BimDocument House()
    {
        var (document, rooms, _) = Row("Generic - 200mm", Home);
        var (unit, supply, extract, valves) = Ventilate(document, rooms);

        // Ducts far too small for their air: over Table 1.8's static pressure.
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        Point3D Spigot(Component fitting, SystemKind kind) => Connectors.Placed(document, fitting).First(placed => placed.Connector.Kind == kind).At;
        var start = Spigot(unit, SystemKind.SupplyAir);
        var junction = new Point3D(start.X, 1500, 2700);
        document.Add(new Duct { TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(start.X, start.Y), End = new Point2D(junction.X, junction.Y), StartOffset = start.Z, EndOffset = junction.Z, Width = 50, Height = 50, Kind = SystemKind.SupplyAir, SystemId = supply.Id });
        foreach (var valve in valves.Where(valve => supply.Members.Contains(valve.Id)))
        {
            var at = Spigot(valve, SystemKind.SupplyAir);
            document.Add(new Duct { TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(junction.X, junction.Y), End = new Point2D(at.X, at.Y), StartOffset = junction.Z, EndOffset = at.Z, Width = 50, Height = 50, Kind = SystemKind.SupplyAir, SystemId = supply.Id });
        }

        // The electrics: lights with a smoke alarm, a ring with no RCD, a cooker on too small a breaker; lights switched with no switch.
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500);
        var lights = rooms.Select(room => Put(document, "Ceiling Light - Flush", room.Location.X, room.Location.Y + 500)).ToArray();
        Make(document, SystemKind.Power, lights.Append(Put(document, "Smoke Alarm", 2500, 1500)).Prepend(board).ToArray());
        var ring = Make(document, SystemKind.Power, new[] { (1000.0, 200.0), (5200, 200), (9400, 200) }.Select(at => Put(document, "Socket - Double", at.Item1, at.Item2)).Prepend(board).ToArray());
        ring.Rcd = false;
        var cooker = Make(document, SystemKind.Power, board, Put(document, "Oven and Hob - 600", 6200, 2600));
        (cooker.Breaker, cooker.Cable) = (16, "1.5 mm²");
        Make(document, SystemKind.Switching, lights.Take(2).ToArray());

        // Two alarm interlinks; heating flowing too hot with no thermostat; water with nothing feeding it and too much used.
        Make(document, SystemKind.FireAlarm, Put(document, "Smoke Alarm", 6000, 1500), Put(document, "Smoke Alarm", 10000, 1500));
        Make(document, SystemKind.FireAlarm, Put(document, "Smoke Alarm", 14000, 1500), Put(document, "Smoke Alarm", 18000, 1500));
        var zone = Make(document, SystemKind.Heating, Put(document, "Boiler - Gas System", 6200, 2800, 180), Put(document, "Radiator - 600 x 1000", 2000, 80));
        zone.FlowTemperature = 70;
        Make(document, SystemKind.ColdWater, Put(document, "Basin - Pedestal", 10400, 2600), Put(document, "Bath - 1700 x 700", 10400, 600), Put(document, "WC - Close Coupled", 11500, 2600),
            Put(document, "Sink Unit - 1000", 6200, 2600));
        document.Add(new MepSystem { Kind = SystemKind.HotWater, Name = "Hot Water 9" });
        _ = extract;
        return document;
    }

    private static void SameAsTheWholeModel(BimDocument document, int wanting)
    {
        var all = MepSystems.Problems(document);
        var said = 0;
        foreach (var system in document.All<MepSystem>())
        {
            var whole = all.Where(problem => ReferenceEquals(problem.Element, system)).Select(problem => problem.Text).ToList();
            Assert.Equal(whole, MepSystems.ProblemsOf(document, system).Select(problem => problem.Text).ToList());
            if (whole.Count > 0) said++;
        }

        Assert.True(said >= wanting, $"only {said} systems wanting");
    }

    [Fact]
    public void ASystemsWantsAreItsOwnChecksAndTheSameAsTheWholeModelsInTheUkAndTheUs()
    {
        var document = House();
        SameAsTheWholeModel(document, 8);
        var said = MepSystems.ProblemsOf(document, document.All<MepSystem>().Single(system => system.Kind == SystemKind.Heating)).Select(problem => problem.Text).ToList();
        Assert.Contains(said, text => text.Contains("room thermostat"));
        Assert.Contains(said, text => text.Contains("55 °C or lower"));

        // In the US, with no winter design temperature: said of the first heating zone.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        SameAsTheWholeModel(document, 6);
        Assert.Contains(MepSystems.ProblemsOf(document, document.All<MepSystem>().Single(system => system.Kind == SystemKind.Heating)), problem => problem.Text.Contains("R303.10"));

        // And through the system's Properties.
        var circuit = document.All<MepSystem>().First(system => system.Kind == SystemKind.Power && !system.Rcd);
        var wanting = circuit.GetInstanceParameters(document).Single(parameter => parameter.Definition == MepSystemParameters.Problems);
        Assert.Equal(string.Join("; ", MepSystems.ProblemsOf(document, circuit).Select(problem => problem.Text)), wanting.Value);
    }
}
