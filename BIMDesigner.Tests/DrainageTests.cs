using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
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
/// Drainage (round M3): foul and rainwater systems traced back from their outfalls, checked as
/// Approved Document H has them - branches, stacks, their ventilation, drains, their falls and
/// access - and in the US by the IRC; long sections, and drainage saved, costed and sent out as IFC.
/// </summary>
[Collection("Wpf")]
public class DrainageTests
{
    private static ComponentType Type(BimDocument document, string name) => document.TypesOf<ComponentType>().First(type => type.Name == name);

    private static Component Put(BimDocument document, string type, double x, double y, double elevation = double.NaN, double rotation = 0)
    {
        var family = Type(document, type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation,
            Elevation = double.IsNaN(elevation) ? family.DefaultElevation : elevation
        };
        document.Add(component);
        return component;
    }

    private static Point3D WasteOf(BimDocument document, Component fitting) =>
        Connectors.Placed(document, fitting).First(placed => placed.Connector.Kind == SystemKind.Foul).At;

    /// <summary>A PVC-U pipe from one point to another on the ground floor - heights from the building's datum, the ground floor's level 0.</summary>
    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem system, double bore = 100)
    {
        var type = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.PvcU);
        var pipe = new Pipe
        {
            TypeId = type.Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = PipeSizes.Of(PipeMaterial.PvcU).First(size => size.Inside == bore).Outside, Kind = system.Kind, SystemId = system.Id
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

    private static Point3D At(double x, double y, double z) => new(x, y, z);

    /// <summary>A branch coming into a stack at a height: the stack cut there, so the branch's end meets it.</summary>
    private static Point3D Tee(BimDocument document, Pipe stack, double z)
    {
        var piece = document.All<Pipe>().First(pipe => pipe.Upright && pipe.Start == stack.Start && Math.Min(pipe.StartOffset, pipe.EndOffset) < z && z < Math.Max(pipe.StartOffset, pipe.EndOffset));
        new SplitPipeCommand(document, piece, piece.Start, z).Redo();
        return At(stack.Start.X, stack.Start.Y, z);
    }

    /// <summary>
    /// A stack at (0, 0) from a drain 600 below the ground floor up to 5 m; a drain from its foot out
    /// to the foul sewer's outfall 12 m away at 1:40; and the outfall.
    /// </summary>
    private static (BimDocument Document, MepSystem Foul, Pipe Stack, Pipe Drain) Stack(double drainFall = 25, double drainBore = 100, double stackBore = 100)
    {
        var document = BimDocument.CreateDefault();
        var outfall = Put(document, "Outfall - Foul Sewer", 12000, 0, 0);
        var foul = Make(document, SystemKind.Foul, outfall);
        Assert.Equal(outfall.Id, foul.SourceId);
        var foot = At(0, 0, -600 + 12000 * drainFall / 1000);
        var drain = Run(document, foot, At(12000, 0, -600), foul, drainBore);
        var stack = Run(document, foot, At(0, 0, 5000), foul, stackBore);
        return (document, foul, stack, drain);
    }

    [Fact]
    public void ABranchIsAtLeastItsTrapAndNoLongerThanDiagram3Allows()
    {
        var (document, foul, stack, _) = Stack();
        var basin = Put(document, "Basin - Pedestal", 0, 2000);
        var bath = Put(document, "Bath - 1700 x 700", 0, -3500);
        new EditSystemCommand(foul, "Add", system => system.Members.AddRange(new[] { basin.Id, bath.Id })).Redo();

        // A basin's 32 mm branch 2 m long at 20 mm a metre: past the 1.7 m Diagram 3 allows.
        var waste = WasteOf(document, basin);
        var bend = At(waste.X, waste.Y, waste.Z - 50);
        Run(document, waste, bend, foul, 32);
        var plan = new Point2D(bend.X, bend.Y).DistanceTo(new Point2D(0, 0));
        Run(document, bend, Tee(document, stack, bend.Z - plan * 0.02), foul, 32);

        // A bath's 3.5 m branch in 32 mm: smaller than its trap, and past 3 m.
        var bathWaste = WasteOf(document, bath);
        var bathPlan = new Point2D(bathWaste.X, bathWaste.Y).DistanceTo(new Point2D(0, 0));
        Run(document, bathWaste, Tee(document, stack, bathWaste.Z - bathPlan * 0.02), foul, 32);

        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == basin && problem.Text.Contains("Diagram 3") && problem.Text.Contains("1.7 m"));
        Assert.Contains(problems, problem => problem.Element == bath && problem.Text.Contains("asks its trap's 40 at least"));
        Assert.Contains(problems, problem => problem.Element == bath && problem.Text.Contains("3.0 m"));
        Assert.DoesNotContain(problems, problem => problem.Element == basin && problem.Text.Contains("trap's"));
    }

    [Fact]
    public void AStackServingAWcIs100VentedAndTakesNoBranchNearItsFoot()
    {
        var (document, foul, stack, _) = Stack(stackBore: 75);
        var wc = Put(document, "WC - Close Coupled", 0, 1500, rotation: 180);
        new EditSystemCommand(foul, "Add", system => system.Members.Add(wc.Id)).Redo();
        var soil = WasteOf(document, wc);
        var foot = stack.Ends(document).Start.Z;
        Run(document, soil, Tee(document, stack, soil.Z - 100), foul, 100);

        // A roof over it, the stack stopping under it.
        var roof = new Roof { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 6000 };
        roof.SetBoundary(new[] { new Point2D(-5000, -5000), new Point2D(5000, -5000), new Point2D(5000, 5000), new Point2D(-5000, 5000) });
        document.Add(roof);

        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element is Pipe { Upright: true } && problem.Text.Contains("Approved Document H (1.28) asks 100"));
        Assert.Contains(problems, problem => problem.Text.Contains("not ventilated") && problem.Text.Contains("1.29 and 1.33"));
        Assert.Contains(problems, problem => problem.Element is Pipe && problem.Text.Contains("above its foot") && problem.Text.Contains("1.11"));
        Assert.True(soil.Z - 100 - foot < 450);

        // An air admittance valve on top closes it inside.
        Put(document, "Air Admittance Valve - 110", 0, 0, 5000);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("not ventilated"));
    }

    [Fact]
    public void AnOpenVentFinishes900AboveAnyOpeningWithin3Metres()
    {
        var (document, _, stack, _) = Stack();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var wall = new Wall { Start = new Point2D(-3000, 1000), End = new Point2D(3000, 1000), TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 6000 };
        document.Add(wall);
        var windowType = document.TypesOf<WindowType>().First();
        document.Add(new BIMDesigner.Core.Architecture.Window { HostWallId = wall.Id, LevelId = level, TypeId = windowType.Id, DistanceAlongWall = 3000, SillHeight = 4000 });

        Assert.Contains(MepSystems.Problems(document), problem => problem.Element is Pipe { Upright: true } && problem.Text.Contains("900 mm above any opening within 3 m"));

        // In a wall between rooms it is no opening into the building.
        wall.TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Interior).Id;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("900 mm above"));
        wall.TypeId = wallType.Id;
        stack.EndOffset = 4000 + windowType.Height + 1000;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("900 mm above"));
    }

    [Fact]
    public void ADrainIsBigAndSteepEnoughAndRunsDownhill()
    {
        // A 100 drain carrying a WC at 1:100: flatter than Table 6's 1:80.
        var (document, foul, stack, drain) = Stack(drainFall: 10);
        var wc = Put(document, "WC - Close Coupled", 0, 1500, rotation: 180);
        new EditSystemCommand(foul, "Add", system => system.Members.Add(wc.Id)).Redo();
        var soil = WasteOf(document, wc);
        Run(document, soil, Tee(document, stack, soil.Z - 30), foul, 100);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == drain && problem.Text.Contains("1:80"));

        // At 1:60, fine; without a WC on it, Table 6's 1:40 under 1 l/s.
        (document, foul, _, drain) = Stack(drainFall: 1000.0 / 60);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == drain && problem.Text.Contains("1:40"));

        // A 75 drain with a WC on it: 2.33 asks 100.
        (document, foul, stack, drain) = Stack(drainBore: 75);
        wc = Put(document, "WC - Close Coupled", 0, 1500, rotation: 180);
        new EditSystemCommand(foul, "Add", system => system.Members.Add(wc.Id)).Redo();
        soil = WasteOf(document, wc);
        Run(document, soil, Tee(document, stack, soil.Z - 30), foul, 100);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == drain && problem.Text.Contains("(2.33) asks 100"));

        // Uphill to the outfall.
        (document, _, _, drain) = Stack(drainFall: -5);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == drain && problem.Text.Contains("runs uphill"));
    }

    [Fact]
    public void ADrainHasAccessAtItsHeadAtBendsAndNoFurtherApartThanTable13()
    {
        var document = BimDocument.CreateDefault();
        var outfall = Put(document, "Outfall - Foul Sewer", 60000, 20000, 0);
        var foul = Make(document, SystemKind.Foul, outfall);

        // A drain from a bare head 60 m along, then a bend 20 m up to the outfall - at 1:40.
        var head = At(0, 0, -3000 + 80000 * 0.025);
        var corner = At(60000, 0, head.Z - 60000 * 0.025);
        var first = Run(document, head, corner, foul);
        var second = Run(document, corner, At(60000, 20000, corner.Z - 20000 * 0.025), foul);

        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == first && problem.Text.Contains("no access at the head"));
        Assert.Contains(problems, problem => problem.Element == second && problem.Text.Contains("turns 90°"));

        // A rodding eye at the head and a chamber at the bend: 60 m between them, past Table 13's 45.
        Put(document, "Rodding Eye - 100", 0, 0, 0);
        Put(document, "Inspection Chamber - 450 Plastic", 60000, 0, 0);
        problems = MepSystems.Problems(document);
        Assert.DoesNotContain(problems, problem => problem.Text.Contains("no access at the head") || problem.Text.Contains("turns 90°"));
        Assert.Contains(problems, problem => problem.Text.Contains("60.0 m of drain from a rodding eye to a manhole") && problem.Text.Contains("Table 13) allows 45"));
        Assert.Equal((AccessClass.RoddingEye, AccessClass.DeepChamber, 45.0), (AccessClass.RoddingEye, AccessClass.DeepChamber, Drainage.Spacing(AccessClass.RoddingEye, AccessClass.DeepChamber)!.Value));

        // Its levels: cover, invert, depth - deep, so a manhole as Table 13 reckons it.
        var chamber = document.All<Component>().Single(component => Type(document, "Inspection Chamber - 450 Plastic").Id == component.TypeId);
        var (cover, invert) = Drainage.Levels(document, chamber)!.Value;
        Assert.Equal(0, cover, 3);
        Assert.Equal(corner.Z - 50, invert, 3);

        // The long section: head to outfall, falling, with both access points along it.
        var section = DrainSections.Through(document, second)!;
        Assert.Equal(new[] { first, second }, section.Pipes.Select(pipe => pipe.Pipe));
        Assert.Equal(80000, section.Length, 1);
        Assert.True(section.Pipes[0].UpperInvert > section.Pipes[^1].LowerInvert);
        Assert.Equal(3, section.Accesses.Count);
        Assert.Equal(60000, section.Accesses[1].At, 1);
    }

    [Fact]
    public void TheLastLengthToTheChamberWhereTheDrainLeavesIsKeptToTable13Too()
    {
        var document = BimDocument.CreateDefault();
        var outfall = Put(document, "Outfall - Foul Sewer", 70000, 0, 0);
        var foul = Make(document, SystemKind.Foul, outfall);
        var head = At(0, 0, -3000 + 70000 * 0.025);
        var drain = Run(document, head, At(70000, 0, -3000), foul);
        Put(document, "Rodding Eye - 100", 0, 0, 0);
        Put(document, "Inspection Chamber - 450 Plastic", 70000, 0, 0);

        // 70 m from the rodding eye at its head to the chamber by the sewer: no bend or junction between, past Table 13's 45.
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == drain && problem.Text.Contains("70.0 m of drain from a rodding eye") && problem.Text.Contains("Table 13"));
    }

    [Fact]
    public void AGroundFloorWcGoesStraightToADrainOnlyWithin13Metres()
    {
        var document = BimDocument.CreateDefault();
        var outfall = Put(document, "Outfall - Foul Sewer", 12000, 0, 0);
        var wc = Put(document, "WC - Close Coupled", 0, 1500, rotation: 180);
        var foul = Make(document, SystemKind.Foul, outfall, wc);
        var soil = WasteOf(document, wc);
        var drain = At(soil.X, soil.Y - 400, -1600);
        Run(document, soil, At(drain.X, drain.Y, soil.Z - 8), foul);
        Run(document, At(drain.X, drain.Y, soil.Z - 8), drain, foul);
        Run(document, drain, At(12000, 0, drain.Z - 12000 * 0.025), foul);

        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == wc && problem.Text.Contains("(1.9) asks 1.3 m at most"));
    }

    [Fact]
    public void RainwaterGoesFromEveryDownpipeToASoakaway5MetresFromTheBuilding()
    {
        var house = new HouseEditAuditTests.House(false);
        var document = house.Document;
        var type = document.TypesOf<GutterType>().First();
        var gutter = new Gutter { RoofId = house.Roof.Id, LevelId = house.Roof.LevelId, TypeId = type.Id };
        gutter.EdgeIds.AddRange(RoofEdgeSweeps.GutterEdges(document, house.Roof));
        document.Add(gutter);
        var downpipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = new Point2D(5000, -400) };
        document.Add(downpipe);
        var foot = Downpipes.Path(document, downpipe)!.Foot;

        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == downpipe && problem.Text.Contains("(H3) carries rainwater to an outfall"));

        var soakaway = Put(document, "Soakaway - 1 m Crate", foot.X, foot.Y - 3000, 0);
        var rain = Make(document, SystemKind.Rainwater, soakaway);
        Run(document, At(foot.X, foot.Y, -400), At(foot.X, foot.Y - 3000, -400 - 3000 / 100.0 * 2), rain);
        var network = PipeNetworks.Trace(document, rain);
        Assert.Single(network.Downpipes);

        var problems = MepSystems.Problems(document);
        Assert.DoesNotContain(problems, problem => problem.Element == downpipe);
        Assert.Contains(problems, problem => problem.Element == soakaway && problem.Text.Contains("(3.25) keeps a soakaway 5 m from a building"));
    }

    [Fact]
    public void InTheUsATrapsArmIsKeptToTableP3105_1()
    {
        var (document, foul, stack, _) = Stack();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var basin = Put(document, "Basin - Pedestal", 0, 2500);
        new EditSystemCommand(foul, "Add", system => system.Members.Add(basin.Id)).Redo();
        var waste = WasteOf(document, basin);
        var plan = new Point2D(waste.X, waste.Y).DistanceTo(new Point2D(0, 0));
        Run(document, waste, Tee(document, stack, waste.Z - plan * 0.025), foul, 32);

        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == basin && problem.Text.Contains("IRC Table P3105.1 keeps a 1¼ in trap within 5 ft"));
        Assert.DoesNotContain(problems, problem => problem.Text.Contains("Approved Document H"));
        Assert.Equal((1.0, 3.0, 2.0), (Drainage.FixtureUnits(ComponentForm.Basin), Drainage.FixtureUnits(ComponentForm.Toilet), Drainage.FixtureUnits(ComponentForm.Bath)));
    }

    [Fact]
    public void AFoulSystemWithNoOutfallDrainsNowhere()
    {
        var document = BimDocument.CreateDefault();
        var system = Make(document, SystemKind.Foul, Put(document, "Basin - Pedestal", 0, 0));
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == system && problem.Text.Contains("it drains nowhere yet - an outfall to the foul sewer takes it"));
    }

    [Fact]
    public void DrainageIsSavedScheduledCostedAndSentOutAsIfc4()
    {
        var (document, foul, _, drain) = Stack();
        var chamber = Put(document, "Inspection Chamber - 450 Plastic", 6000, 0, 0);
        chamber.Mark = "IC1";
        Put(document, "Air Admittance Valve - 110", 0, 0, 5000);
        Type(document, "Manhole - 1200 x 675 Precast").Access = DrainAccessKind.Manhole;

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(DrainAccessKind.RoddingEye, reopened.TypesOf<ComponentType>().First(type => type.Name == "Rodding Eye - 100").Access);
        Assert.Equal(SystemKind.Foul, reopened.All<Pipe>().First().Kind);

        var schedule = Schedule.Run(document, document.Schedules.First(each => each.Name == "Manhole Schedule"));
        Assert.Contains(schedule.Rows, row => row.Element == chamber);
        Assert.Contains("500", chamber.GetInstanceParameters(document).Single(parameter => parameter.Name == "Depth to Invert").Value?.ToString());
        Assert.Equal(("8.6", "8.6"), (CostPlanning.Nrm1(document, drain).Code, CostPlanning.Nrm1(document, chamber).Code));

        using var model = IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<IIfcDistributionChamberElement>(), each => each.PredefinedType == IfcDistributionChamberElementTypeEnum.INSPECTIONCHAMBER);
        Assert.Contains(model.Instances.OfType<IIfcValve>(), each => each.PredefinedType == IfcValveTypeEnum.AIRRELEASE);
        Assert.Contains(model.Instances.OfType<IIfcDistributionSystem>(), each => each.PredefinedType == IfcDistributionSystemEnum.DRAINAGE && each.Name == foul.Name);
    }

    [Fact]
    public void ADrainageRunFallsFromItsFittingAndIsDrawnInPvcU()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, foul, stack, _) = Stack();
                var history = new UndoStack();
                var plan = new PlanView { Width = 1000, Height = 800, Document = document, History = history };
                plan.Measure(new Size(1000, 800));
                plan.Arrange(new Rect(0, 0, 1000, 800));
                plan.ActiveLevelId = document.Levels[0].Id;
                plan.ActivePipeTypeId = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper).Id;
                plan.SetTool(PlanTool.Pipe);
                plan.NewPipeFall = 20;

                var sink = Put(document, "Sink Unit - 600", 2500, 0, rotation: 90);
                var waste = WasteOf(document, sink);
                Assert.True(plan.ClickPipe(new Point2D(waste.X, waste.Y)));
                Assert.True(plan.ClickPipe(new Point2D(0, 0)));

                // Into the stack: the sink on Foul Water 1, the run in PVC-U falling 20 mm a metre.
                Assert.Contains(sink.Id, foul.Members);
                var run = document.All<Pipe>().Where(pipe => pipe != stack && pipe.Kind == SystemKind.Foul && pipe.SystemId == foul.Id && !pipe.Upright).Last();
                Assert.Equal(PipeMaterial.PvcU, document.FindType<PipeType>(run.TypeId)!.Material);
                Assert.Equal(20, (run.StartOffset - run.EndOffset) / run.Start.DistanceTo(run.End) * 1000, 3);

                // A pipe higher than the run gets to is not drained into.
                var basin = Put(document, "Basin - Pedestal", 4000, 3000);
                var basinWaste = WasteOf(document, basin);
                Assert.True(plan.ClickPipe(new Point2D(basinWaste.X, basinWaste.Y)));
                var high = Run(document, At(4000, 6000, 3000), At(4000, 7000, 3000), foul, 40);
                Assert.False(plan.ClickPipe(new Point2D(4000, 6500)));
                _ = high;
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }
}
