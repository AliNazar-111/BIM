using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>
/// Pipes and water (round M2): pipes drawn between fittings, traced from what feeds them and
/// sized by BS EN 806-3; the dwelling's water use by Approved Document G's calculator; pipes saved,
/// costed, scheduled and sent out as IFC4.
/// </summary>
[Collection("Wpf")]
public class PipesTests
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

    private static Point3D ConnectorOf(BimDocument document, Component fitting, SystemKind kind) =>
        Connectors.Placed(document, fitting).First(placed => placed.Connector.Kind == kind).At;

    /// <summary>A pipe from one point to another, on the ground floor - at heights above the building's datum, the ground floor's level being 0.</summary>
    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem system, double diameter = 15)
    {
        var pipe = new Pipe
        {
            TypeId = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper).Id, LevelId = document.Levels[0].Id,
            Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = diameter, Kind = system.Kind, SystemId = system.Id
        };
        document.Add(pipe);
        return pipe;
    }

    private static MepSystem Make(BimDocument document, SystemKind kind, params Component[] picked)
    {
        var (system, refusal) = MepSystems.Make(document, kind, picked);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    [Fact]
    public void APipeIsSizedFromBsEn8063sTablesForItsMaterial()
    {
        Assert.Equal("12 x 1.0", PipeSizes.Required(PipeMaterial.Copper, 1, 1, 10_000)!.Size.Name);
        Assert.Equal("15 x 1.0", PipeSizes.Required(PipeMaterial.Copper, 2, 2, 10_000)!.Size.Name);
        Assert.Equal("12 x 1.0", PipeSizes.Required(PipeMaterial.Copper, 2, 1, 6_000)!.Size.Name);
        Assert.Equal("18 x 1.0", PipeSizes.Required(PipeMaterial.Copper, 7, 4, 20_000)!.Size.Name);
        Assert.Equal("22 x 1.0", PipeSizes.Required(PipeMaterial.Copper, 10, 8, 20_000)!.Size.Name);
        Assert.Equal("28 x 1.5", PipeSizes.Required(PipeMaterial.Copper, 21, 4, 20_000)!.Size.Name);
        Assert.Equal("16 x 2.2", PipeSizes.Required(PipeMaterial.PeX, 4, 2, 4_000)!.Size.Name);
        Assert.Equal("DN 20", PipeSizes.Required(PipeMaterial.GalvanisedSteel, 8, 4, 5_000)!.Size.Name);
        Assert.Null(PipeSizes.Required(PipeMaterial.PeX, 800, 4, 1_000));

        Assert.Equal((1, 1, 2, 2, 4), (PipeSizes.LoadingUnits(ComponentForm.Basin), PipeSizes.LoadingUnits(ComponentForm.Toilet),
            PipeSizes.LoadingUnits(ComponentForm.Sink), PipeSizes.LoadingUnits(ComponentForm.Shower), PipeSizes.LoadingUnits(ComponentForm.Bath)));
        Assert.Equal((0.7, 2.2, 1.4), (PipeSizes.FixtureUnits(ComponentForm.Basin), PipeSizes.FixtureUnits(ComponentForm.Toilet), PipeSizes.FixtureUnits(ComponentForm.Bath)));
    }

    /// <summary>A stop valve on the wall, a main along to a tee, and branches to a basin and a bath.</summary>
    private static (BimDocument Document, MepSystem Cold, Pipe Main, Pipe ToBasin, Pipe ToBath, Component Basin, Component Bath) Branches()
    {
        var document = BimDocument.CreateDefault();
        var valve = Put(document, "Stop Valve - Incoming Main", 0, 0);
        var basin = Put(document, "Basin - Pedestal", 3000, 2000);
        var bath = Put(document, "Bath - 1700 x 700", 8000, -3000);
        var cold = Make(document, SystemKind.ColdWater, valve, basin, bath);

        var start = ConnectorOf(document, valve, SystemKind.ColdWater);
        var tee = new Point3D(3000, 0, start.Z);
        var main = Run(document, start, tee, cold);
        var basinTap = ConnectorOf(document, basin, SystemKind.ColdWater);
        var toBasin = Run(document, tee, basinTap, cold);
        var bathTap = ConnectorOf(document, bath, SystemKind.ColdWater);
        var toBath = Run(document, tee, bathTap, cold);
        return (document, cold, main, toBasin, toBath, basin, bath);
    }

    [Fact]
    public void ASystemsPipesAreTracedFromItsSourceAndEachCarriesTheDrawOffsBeyondIt()
    {
        var (document, cold, main, toBasin, toBath, basin, bath) = Branches();
        var network = PipeNetworks.Trace(document, cold);

        Assert.Equal((5.0, 4.0), (network.Loads[main].Load, network.Loads[main].Highest));
        Assert.Equal((1.0, 1.0), (network.Loads[toBasin].Load, network.Loads[toBasin].Highest));
        Assert.Equal((4.0, 4.0), (network.Loads[toBath].Load, network.Loads[toBath].Highest));
        Assert.Equal(main.Length + toBath.Length, network.RunTo[bath], 3);
        Assert.Equal(main.Length, network.Loads[toBasin].FromSource, 3);
        Assert.Empty(network.Unreached);
        Assert.Empty(network.NotPiped);

        // All 15 mm: the main and the bath's branch too small for what they carry.
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == main && problem.Text.Contains("BS EN 806-3 Table 3.2 asks 18 x 1.0"));
        Assert.DoesNotContain(problems, problem => problem.Element == toBasin);

        // Sized as the standard asks, as one step.
        var history = new UndoStack();
        history.Execute(new SetPipeSizesCommand(PipeNetworks.Sized(document, new[] { cold })));
        Assert.Equal(18, main.Diameter);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element is Pipe);
        history.Undo();
        Assert.Equal(15, main.Diameter);
        Assert.Contains("5 loading units, the most on one draw-off 4", PipeNetworks.Describe(document, main));
        _ = basin;
    }

    [Fact]
    public void APipeNotJoinedToTheSourceAndAFittingNoPipeReachesAreWanting()
    {
        var (document, cold, _, toBasin, _, basin, _) = Branches();
        var loose = Run(document, new Point3D(9000, 9000, 300), new Point3D(9500, 9000, 300), cold);
        document.Remove(toBasin);

        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == loose && problem.Text.Contains("not joined to the rest of Cold Water 1"));
        Assert.Contains(problems, problem => problem.Element == basin && problem.Text.Contains("no pipe reaches it yet"));
    }

    [Fact]
    public void AWaterHeatersColdFeedCarriesTheHotDrawOffsItServes()
    {
        var document = BimDocument.CreateDefault();
        var combi = Put(document, "Boiler - Gas Combi", 0, 0);
        var basin = Put(document, "Basin - Pedestal", 2000, 0);
        var bath = Put(document, "Bath - 1700 x 700", 4000, 0);
        Make(document, SystemKind.HotWater, combi, basin, bath);
        Assert.Equal((5.0, 4.0), PipeNetworks.Draw(document, combi, SystemKind.ColdWater));
        Assert.Equal((4.0, 4.0), PipeNetworks.Draw(document, bath, SystemKind.HotWater));
    }

    [Fact]
    public void TheDwellingsWaterUseIsWorkedOutByApprovedDocumentGsCalculator()
    {
        var document = BimDocument.CreateDefault();
        foreach (var name in new[] { "WC - Close Coupled", "Basin - Pedestal", "Bath - 1700 x 700", "Shower Tray - 900 x 900", "Sink Unit - 1000", "Washing Machine - 600", "Dishwasher - 600" })
            Put(document, name, 0, 0);

        // WC 6 x 1.46 + 4 x 2.96, taps 6 x 1.58 + 1.58, bath 180 x 0.11, shower 9 x 4.37, sink 8 x 0.44 + 10.36, machine 6.25 x 2.1, dishwasher 0.77 x 3.6.
        var result = WaterUse.Assess(document);
        Assert.Equal(new[] { 8.76, 11.84, 11.06, 19.8, 39.33, 13.88, 13.13, 2.77 }, result.Lines.Select(line => line.Litres));
        Assert.Equal(120.57, result.Total, 2);
        Assert.Equal(114.7, result.Consumption, 1);
        Assert.True(result.Meets);
        Assert.True(result.WithinFittingsLimits);
        Assert.Empty(WaterUse.Problems(document));

        // Where planning asks for 110: over it, and the fittings past Table 2.2's limits.
        document.ProjectInformation.WaterOptionalRequirement = true;
        result = WaterUse.Assess(document);
        Assert.False(result.Meets);
        Assert.False(result.WithinFittingsLimits);
        Assert.Contains(WaterUse.Problems(document), problem => problem.Text.Contains("asks 110 at most"));

        // A second, weaker basin: the average, but no less than 70 per cent of the highest.
        Type(document, "Basin - Hand Rinse 400").FlowRate = 2;
        Put(document, "Basin - Hand Rinse 400", 0, 0);
        Assert.Equal(4.2, WaterUse.Assess(document).Lines.Single(line => line.Fitting.StartsWith("Taps")).Value, 6);

        // With no shower the calculator, not the fittings approach.
        document.ProjectInformation.WaterOptionalRequirement = false;
        foreach (var shower in document.All<Component>().Where(c => Type(document, "Shower Tray - 900 x 900").Id == c.TypeId).ToList()) document.Remove(shower);
        Assert.Contains(WaterUse.Assess(document).Lines, line => line.Fitting == "Bath only" && line.UseFactor == 0.50);
        Assert.False(WaterUse.Assess(document).WithinFittingsLimits);
    }

    [Fact]
    public void InTheUsAHotWaterRunIsKeptWithin100Feet()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var combi = Put(document, "Boiler - Gas Combi", 0, 0);
        var basin = Put(document, "Basin - Pedestal", 32000, 0);
        var hot = Make(document, SystemKind.HotWater, combi, basin);
        var from = Connectors.Placed(document, combi).First(placed => placed.Connector.Kind == SystemKind.HotWater).At;
        var tap = ConnectorOf(document, basin, SystemKind.HotWater);
        var pipe = Run(document, from, tap, hot);

        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == basin && problem.Text.Contains("IRC P2905.3"));
        Assert.Equal($"{pipe.Length / 1000:0.0} m of pipe from Boiler - Gas Combi",
            basin.GetInstanceParameters(document).Single(parameter => parameter.Name == "Hot Water Run").Value);
        Assert.Contains("0.7 water-supply fixture units (IRC Table P2903.6)", PipeNetworks.Describe(document, pipe));
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("BS EN 806-3"));
    }

    [Fact]
    public void PipesAreSavedCostedScheduledAndCopied()
    {
        var (document, cold, main, _, _, _, _) = Branches();
        main.Mark = "P1";
        var foul = new Pipe { TypeId = main.TypeId, LevelId = main.LevelId, Start = new Point2D(0, 5000), End = new Point2D(2000, 5000), Diameter = 42, Kind = SystemKind.Foul };
        document.Add(foul);
        document.ProjectInformation.WaterOptionalRequirement = true;

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var saved = reopened.All<Pipe>().Single(pipe => pipe.Id == main.Id);
        Assert.Equal((main.Start, main.End, main.StartOffset, main.EndOffset, main.Diameter, SystemKind.ColdWater, cold.Id, "P1"),
            (saved.Start, saved.End, saved.StartOffset, saved.EndOffset, saved.Diameter, saved.Kind, saved.SystemId!.Value, saved.Mark));
        Assert.Equal(4, reopened.TypesOf<PipeType>().Count());
        Assert.True(reopened.ProjectInformation.WaterOptionalRequirement);
        Assert.Equal(180, reopened.TypesOf<ComponentType>().First(type => type.Name == "Bath - 1700 x 700").Capacity);

        // By the metre at the type's rate; foul pipes under disposal installations.
        var rate = document.FindType<PipeType>(main.TypeId)!.Cost;
        Assert.Equal((decimal)(main.Length / 1000) * rate, CostPlanning.CostOf(document, main), 2);
        Assert.Equal(("5.4", "5.3"), (CostPlanning.Nrm1(document, main).Code, CostPlanning.Nrm1(document, foul).Code));
        var schedule = Schedule.Run(document, document.Schedules.First(each => each.Name == "Pipe Schedule"));
        Assert.NotEmpty(schedule.Rows);

        // A pipe copied alone is on no system until it is joined to one; copied with its system, on the copy.
        Assert.Null(((Pipe)ElementCopy.Duplicate(document, new[] { main }).Single()).SystemId);
        var both = ElementCopy.Duplicate(document, new Core.Elements.Element[] { main, cold });
        Assert.Equal(both.OfType<MepSystem>().Single().Id, both.OfType<Pipe>().Single().SystemId);
    }

    [Fact]
    public void PipesGoOutAsIfc4PipeSegmentsJoinedPortToPort()
    {
        var (document, cold, main, toBasin, _, basin, _) = Branches();
        using var model = IfcExport.Build(document);
        var segments = model.Instances.OfType<IIfcPipeSegment>().ToList();
        Assert.Equal(3, segments.Count);
        var grouped = model.Instances.OfType<IIfcRelAssignsToGroup>().Single(relation => relation.RelatingGroup.Name == cold.Name).RelatedObjects;
        Assert.Equal(5, grouped.Count());

        // The basin's cold supply port joined to its branch's end.
        var basinProduct = model.Instances.OfType<IIfcSanitaryTerminal>().Single(terminal => terminal.GlobalId.ToString() == Xbim.Ifc4.UtilityResource.IfcGloballyUniqueId.ConvertToBase64(basin.Id).ToString());
        var coldPort = model.Instances.OfType<IIfcRelNests>().Single(relation => relation.RelatingObject == basinProduct)
            .RelatedObjects.OfType<IIfcDistributionPort>().Single(port => port.Name == "Cold Supply");
        Assert.Contains(model.Instances.OfType<IIfcRelConnectsPorts>(), relation => relation.RelatingPort == coldPort || relation.RelatedPort == coldPort);
        Assert.True(model.Instances.OfType<IIfcRelConnectsPorts>().Count() >= 6);
        _ = (main, toBasin, basin);
    }

    [Fact]
    public void APipeIsDrawnFromAFittingUpToItsRunAndDownIntoTheNextWhichGoesOnItsSystem()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var history = new UndoStack();
                var plan = new PlanView { Width = 1000, Height = 800, Document = document, History = history };
                plan.Measure(new Size(1000, 800));
                plan.Arrange(new Rect(0, 0, 1000, 800));
                plan.ActiveLevelId = document.Levels[0].Id;
                plan.ActivePipeTypeId = document.TypesOf<PipeType>().First().Id;

                var valve = Put(document, "Stop Valve - Incoming Main", 0, 0);
                var basin = Put(document, "Basin - Pedestal", 3000, 0);
                var bath = Put(document, "Bath - 1700 x 700", 3000, 3000);
                var cold = Make(document, SystemKind.ColdWater, valve);

                plan.SetTool(PlanTool.Pipe);
                plan.NewPipeOffset = 2400;
                var source = ConnectorOf(document, valve, SystemKind.ColdWater);
                Assert.True(plan.ClickPipe(new Point2D(source.X, source.Y)));
                Assert.True(plan.ClickPipe(new Point2D(1500, 0)));
                var tap = ConnectorOf(document, basin, SystemKind.ColdWater);
                Assert.True(plan.ClickPipe(new Point2D(tap.X, tap.Y)));

                // Up from the valve to 2400, along twice, and down to the basin's tap: on Cold Water 1, which the basin is put on.
                var pipes = document.All<Pipe>().ToList();
                Assert.Equal(4, pipes.Count);
                Assert.True(pipes[0].Upright);
                Assert.Equal((source.Z, 2400.0), (pipes[0].StartOffset, pipes[0].EndOffset));
                Assert.True(pipes[^1].Upright);
                Assert.Equal(tap.Z, pipes[^1].EndOffset, 3);
                Assert.All(pipes, pipe => Assert.Equal((cold.Id, SystemKind.ColdWater, 22.0), (pipe.SystemId!.Value, pipe.Kind, pipe.Diameter)));
                Assert.Contains(basin.Id, cold.Members);
                Assert.Single(PipeNetworks.Trace(document, cold).RunTo);

                // A branch to the bath, teed in along the run.
                var bathTap = ConnectorOf(document, bath, SystemKind.ColdWater);
                Assert.True(plan.ClickPipe(new Point2D(bathTap.X, bathTap.Y)));
                Assert.True(plan.ClickPipe(new Point2D(bathTap.X, 0)));
                Assert.Contains(bath.Id, cold.Members);
                var network = PipeNetworks.Trace(document, cold);
                Assert.Empty(network.Unreached);
                Assert.Equal(5.0, network.Loads.Values.Max(load => load.Load));

                // A hot pipe is not run into a cold one.
                plan.NewPipeKind = SystemKind.HotWater;
                var hotTap = ConnectorOf(document, basin, SystemKind.HotWater);
                Assert.True(plan.ClickPipe(new Point2D(hotTap.X, hotTap.Y)));
                Assert.False(plan.ClickPipe(new Point2D(1000, 0)));

                // A click straight over where a run starts goes up to its height, and on from there - not back into the fitting.
                plan.CancelPendingOperation();
                plan.NewPipeKind = SystemKind.ColdWater;
                var before = document.All<Pipe>().Count();
                Assert.True(plan.ClickPipe(new Point2D(source.X, source.Y)));
                Assert.True(plan.ClickPipe(new Point2D(source.X + 10, source.Y)));
                var rise = document.All<Pipe>().Last();
                Assert.Equal(before + 1, document.All<Pipe>().Count());
                Assert.True(rise.Upright);
                Assert.Equal(2400, rise.EndOffset, 3);
                Assert.True(plan.ClickPipe(new Point2D(source.X, -2000)));
                Assert.Equal(2400, document.All<Pipe>().Last().StartOffset, 3);

                // Each click one step to undo.
                plan.CancelPendingOperation();
                var count = document.All<Pipe>().Count();
                history.Undo();
                Assert.True(document.All<Pipe>().Count() < count);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }

    [Fact]
    public void PipesShowOnTheirOwnServicesPlanAndFaintElsewhere()
    {
        var (document, _, main, _, _, _, _) = Branches();
        var level = document.Levels[0].Id;
        Assert.True(document.ViewSettings.StyleFor(document, ServicesPlans.Plan(level, Discipline.Plumbing))(main).IsPlain);
        Assert.True(document.ViewSettings.StyleFor(document, ServicesPlans.Plan(level, Discipline.Electrical))(main).Halftone);
        Assert.True(document.ViewSettings.StyleFor(document, ServicesPlans.Plan(level, Discipline.Architectural))(main).Halftone);
        Assert.NotNull(PipeModel.Mesh(document, main));
    }
}
