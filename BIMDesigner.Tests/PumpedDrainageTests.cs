using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// Pumped drainage (round 82), as Approved Document H (2015) has it: a package pumping station taking the drains that run into it and
/// lifting them up a rising main to the sewer (2.36 to 2.38), its receiving chamber holding 24 hours' inflow at 150 litres a head a day,
/// pro rata where a home is only partly pumped (2.39); a basement's appliances pumped, or drained through an anti-flooding valve (2.9).
/// </summary>
[Collection("Wpf")]
public class PumpedDrainageTests
{
    private static Component Put(BimDocument document, string type, double x, double y, double elevation = double.NaN, Guid? level = null)
    {
        var family = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = level ?? document.Levels[0].Id, Location = new Point2D(x, y),
            Elevation = double.IsNaN(elevation) ? family.DefaultElevation : elevation
        };
        document.Add(component);
        return component;
    }

    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem system, double bore, Guid? level = null)
    {
        var type = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.PvcU);
        var on = level ?? document.Levels[0].Id;
        var elevation = document.FindLevel(on)!.Elevation;
        var pipe = new Pipe
        {
            TypeId = type.Id, LevelId = on, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z - elevation, EndOffset = to.Z - elevation,
            Diameter = PipeSizes.Of(PipeMaterial.PvcU).First(size => size.Inside == bore).Outside, Kind = system.Kind, SystemId = system.Id
        };
        document.Add(pipe);
        return pipe;
    }

    private static MepSystem Make(BimDocument document, params Component[] picked)
    {
        var (system, refusal) = MepSystems.Make(document, SystemKind.Foul, picked);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    private static Point3D Connector(BimDocument document, Component fitting, string name) =>
        Connectors.Placed(document, fitting).First(placed => placed.Connector.Name == name).At;

    /// <summary>
    /// A WC at the front of the house whose drain falls 2 m below the ground into a pumping station sunk there; the station's rising main
    /// running up from it to the sewer 8 m on, rising as it goes.
    /// </summary>
    private static (BimDocument Document, MepSystem Gravity, MepSystem Rising, Component Pump, Component Wc, Pipe Main) Pumped()
    {
        var document = BimDocument.CreateDefault();
        var wc = Put(document, "WC - Close Coupled", 0, 1500);
        var pump = Put(document, "Pumping Station - Package 600 l", 4000, 0, -2000);
        var outfall = Put(document, "Outfall - Foul Sewer", 12000, 0, 0);
        var rising = Make(document, outfall, pump);
        var gravity = Make(document, wc, pump);

        var soil = Connectors.Placed(document, wc).First(placed => placed.Connector.Kind == SystemKind.Foul).At;
        var inlet = Connector(document, pump, "Inlet");
        var under = new Point3D(soil.X, soil.Y, inlet.Z + 4500 * 0.025);
        Run(document, soil, under, gravity, 100);
        Run(document, under, inlet, gravity, 100);
        var main = Run(document, Connector(document, pump, "Rising Main"), new Point3D(12000, 0, -400), rising, 50);
        return (document, gravity, rising, pump, wc, main);
    }

    [Fact]
    public void APumpingStationTakesItsDrainsAndItsRisingMainRunsUphillToTheSewer()
    {
        var (document, gravity, rising, pump, wc, main) = Pumped();

        // The station takes the gravity drains as their outfall does, and is on the system its rising main runs to.
        Assert.Equal(pump.Id, gravity.SourceId);
        Assert.DoesNotContain(pump.Id, gravity.Members);
        Assert.Contains(pump.Id, rising.Members);
        Assert.NotEqual(Guid.Empty, rising.SourceId ?? Guid.Empty);
        Assert.Contains(wc, PipeNetworks.Trace(document, gravity).FirstPipe.Keys);
        Assert.Equal(main, PipeNetworks.Trace(document, rising).FirstPipe[pump]);

        // The rising main climbs 1.3 m over 8 m in 50 mm: pumped, not wanting as a gravity drain would be - uphill, under 75, flatter than Table 6.
        var problems = MepSystems.Problems(document);
        Assert.DoesNotContain(problems, problem => problem.Element == main && (problem.Text.Contains("uphill") || problem.Text.Contains("(2.33)") || problem.Text.Contains("Table 6") || problem.Text.Contains("access")));

        // Sent out to IFC as a sump pump.
        using (var model = Infrastructure.Interoperability.IfcExport.Build(document))
            Assert.Equal(Xbim.Ifc4.Interfaces.IfcPumpTypeEnum.SUMPPUMP, Assert.Single(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcPump>()).PredefinedType);

        // The pump taken off its system, the same pipe is a gravity drain, and running uphill.
        rising.Members.Remove(pump.Id);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == main && problem.Text.StartsWith("it runs uphill"));
    }

    [Fact]
    public void ItsReceivingChamberHoldsADaysInflowAt150LitresAHead()
    {
        // Two king beds - four bedspaces - 600 l a day: its 600 l chamber holds it. A third, six bedspaces, 900 l: short.
        var (document, _, _, pump, _, _) = Pumped();
        string? Wanting() => MepSystems.Problems(document).Where(problem => problem.Element == pump && problem.Text.Contains("(2.39)")).Select(problem => problem.Text).FirstOrDefault();
        Put(document, "Bed - King 1500 x 2000", 2000, 6000, 0);
        Put(document, "Bed - King 1500 x 2000", 6000, 6000, 0);
        Assert.Null(Wanting());
        Put(document, "Bed - King 1500 x 2000", 9000, 6000, 0);
        Assert.Equal("its receiving chamber holds 600 l - Approved Document H (2.39) holds 24 hours' inflow, 150 l a head a day: 6 bedspaces, 900 l", Wanting());

        // A basin in the house draining elsewhere: half its appliances pumped, half the inflow - 450 l.
        Put(document, "Basin - Pedestal", 8000, 3000);
        var inflow = Drainage.DailyInflow(document, new[] { document.All<Component>().First(component => document.FindType<ComponentType>(component.TypeId)!.Form == ComponentForm.Toilet) })!.Value;
        Assert.Equal((450.0, 6, "6 bedspaces, 1 of its 2 appliances pumped"), (inflow.Litres, inflow.Heads, inflow.Said));
        Assert.Null(Wanting());

        // A bigger chamber, in its type.
        Assert.True(document.FindType<ComponentType>(pump.TypeId)!.GetTypeParameters(document).Single(parameter => parameter.Name == "Receiving Chamber (l)").TrySet(1000.0));
        Assert.Equal(1000, document.FindType<ComponentType>(pump.TypeId)!.Capacity);
    }

    [Fact]
    public void ABasementsAppliancesArePumpedOrDrainedThroughAnAntiFloodingValve()
    {
        // A basin in a basement 3 m down, draining by gravity to the sewer: wanting by 2.9.
        var document = BimDocument.CreateDefault();
        var basement = new Level { Name = "Basement", Elevation = -3000 };
        document.AddLevel(basement);
        var basin = Put(document, "Basin - Pedestal", 0, 2000, double.NaN, basement.Id);
        var outfall = Put(document, "Outfall - Foul Sewer", 12000, 0, 0);
        var foul = Make(document, outfall, basin);
        var waste = Connectors.Placed(document, basin).First(placed => placed.Connector.Kind == SystemKind.Foul).At;
        var drain = Run(document, waste, new Point3D(12000, 0, waste.Z - 12000 * 0.025), foul, 50, basement.Id);
        bool Wanting() => MepSystems.Problems(document).Any(problem => problem.Element == basin && problem.Text.Contains("(2.9)"));
        Assert.True(Drainage.InBasement(document, basin));
        Assert.True(Wanting());

        // An anti-flooding valve on its drain.
        var valve = new PipeValve { PipeId = drain.Id, LevelId = drain.LevelId, Along = 6000, Kind = ValveKind.AntiFlooding };
        new AddPipeValveCommand(document, valve).Redo();
        Assert.False(Wanting());
        Assert.Equal("Anti-flooding valve", valve.Kind.Title());

        // Or pumped: on a system whose source is a pumping station, no valve wanted.
        new DeleteElementsCommand(document, new[] { valve }).Redo();
        Assert.True(Wanting());
        var pump = Put(document, "Pumping Station - Package 600 l", 6000, 0, -4000);
        foul.SourceId = pump.Id;
        Assert.False(Wanting());

        // On the ground floor it is no basement's.
        Assert.False(Drainage.InBasement(document, Put(document, "Basin - Pedestal", 0, 5000)));
    }
}
