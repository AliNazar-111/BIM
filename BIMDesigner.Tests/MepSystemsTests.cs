using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>
/// The services as systems (round M1): each fitting's connectors, systems made of fittings and
/// fed by equipment, what is wanting by the rules, the services plans, and systems saved and sent
/// out as IFC4.
/// </summary>
[Collection("Wpf")]
public class MepSystemsTests
{
    private static string Ifc(Guid id) => Xbim.Ifc4.UtilityResource.IfcGloballyUniqueId.ConvertToBase64(id);

    private static ComponentType Type(BimDocument document, string name) => document.TypesOf<ComponentType>().First(type => type.Name == name);

    private static Component Put(BimDocument document, string type, double x, double y, double rotation = 0, int level = 0)
    {
        var family = Type(document, type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[level].Id, Location = new Point2D(x, y),
            Rotation = rotation, Elevation = family.DefaultElevation
        };
        document.Add(component);
        return component;
    }

    private static MepSystem Make(BimDocument document, SystemKind kind, params Component[] picked)
    {
        var (system, refusal) = MepSystems.Make(document, kind, picked);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    /// <summary>Four walls round a room so big - 13 by 12.5 m inside or so - a dwelling of it is over 150 m².</summary>
    private static Room BigRoom(BimDocument document)
    {
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(t => t.Name == "Generic - 200mm");
        Point2D[] corners = { new(0, 0), new(13200, 0), new(13200, 12700), new(0, 12700) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 });
        var room = new Room { LevelId = level, Location = new Point2D(6000, 6000), Name = "Living" };
        document.Add(room);
        return room;
    }

    [Fact]
    public void EachFittingHasTheConnectorsItsServicesAreJoinedBy()
    {
        var document = BimDocument.CreateDefault();
        string Kinds(string type) => string.Join(" ", Connectors.Of(Type(document, type)).Select(connector => $"{connector.Kind}{(connector.Feeds ? "*" : string.Empty)}:{connector.Size}"));

        Assert.Equal("HotWater:15 ColdWater:15 Foul:32", Kinds("Basin - Pedestal"));
        Assert.Equal("HotWater:22 ColdWater:22 Foul:40", Kinds("Bath - 1700 x 700"));
        Assert.Equal("ColdWater:15 Foul:100", Kinds("WC - Close Coupled"));
        Assert.Equal("Heating:15 Heating:15", Kinds("Radiator - 600 x 1000"));
        Assert.Equal("Power*:0", Kinds("Consumer Unit - 12 Way"));
        Assert.Equal("ColdWater*:22", Kinds("Stop Valve - Incoming Main"));

        // A combi heats its own hot water off the cold main; a system boiler a cylinder's, through its coil.
        Assert.Equal("Heating*:22 HotWater*:15 Gas:22 ColdWater:15 Heating:22 Foul:22 Power:0", Kinds("Boiler - Gas Combi"));
        Assert.Equal("Heating*:22 Gas:22 Heating:22 Foul:22 Power:0", Kinds("Boiler - Gas System"));
        Assert.Equal("ColdWater:22 HotWater*:22 Heating:22 Heating:22 Power:0", Kinds("Hot Water Cylinder - Unvented 210 l"));

        // Furniture has none.
        Assert.Empty(Connectors.Of(document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.Sofa)));
    }

    [Fact]
    public void AConnectorTurnsWithItsFittingAndSitsAtItsHeight()
    {
        var document = BimDocument.CreateDefault();
        var radiator = Put(document, "Radiator - 600 x 1000", 2000, 1000, rotation: 90);
        var flow = Connectors.Placed(document, radiator).First(connector => connector.Connector.Name == "Flow");

        // Its width runs up the page turned a quarter round, so its left end is at the bottom, 470 from its middle.
        Assert.Equal(2000, flow.At.X, 3);
        Assert.Equal(1000 - 470, flow.At.Y, 3);
        Assert.Equal(radiator.Elevation, flow.At.Z, 3);
        Assert.Equal(-1, flow.Points.Z, 6);
    }

    [Fact]
    public void ASystemIsMadeOfTheFittingsPickedAndFedByTheOnlyEquipmentThatCan()
    {
        var document = BimDocument.CreateDefault();
        var basin = Put(document, "Basin - Pedestal", 1000, 0);
        var bath = Put(document, "Bath - 1700 x 700", 3000, 0);
        var sofa = Put(document, document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.Sofa).Name, 6000, 0);
        var valve = Put(document, "Stop Valve - Incoming Main", 0, 2000);

        Assert.Equal(new[] { SystemKind.ColdWater, SystemKind.HotWater, SystemKind.Foul }, MepSystems.KindsFor(document, new[] { basin, bath }));
        var cold = Make(document, SystemKind.ColdWater, basin, bath, sofa);
        Assert.Equal("Cold Water 1", cold.Name);
        Assert.Equal(new[] { basin.Id, bath.Id }, cold.Members);
        Assert.Equal(valve.Id, cold.SourceId);

        // On one cold water system at most; and the stop valve feeds one.
        var second = new MepSystem { Kind = SystemKind.ColdWater, Name = "Cold Water 2" };
        Assert.Contains("on Cold Water 1 already", MepSystems.WhyNotMember(document, basin, second));
        Assert.Contains("feeds Cold Water 1 already", MepSystems.WhyNotSource(document, valve, second));
        Assert.Contains("make it the system's source", MepSystems.WhyNotMember(document, valve, second));
        Assert.Contains("has no cold water connection", MepSystems.WhyNotMember(document, sofa, second));
        Assert.Equal(new[] { SystemKind.HotWater, SystemKind.Foul }, MepSystems.KindsFor(document, new[] { basin, bath }));

        // A consumer unit feeds as many circuits as it has ways; a boiler as many heating zones.
        var unit = Put(document, "Consumer Unit - 12 Way", 0, 3000);
        var lights = Make(document, SystemKind.Power, Put(document, "Ceiling Light - Flush", 1000, 3000), Put(document, "Switch - One Gang", 1500, 3000));
        var sockets = Make(document, SystemKind.Power, Put(document, "Socket - Double", 2000, 3000));
        Assert.Equal((unit.Id, unit.Id), (lights.SourceId, sockets.SourceId));
        Assert.Equal((CircuitUse.Lighting, CircuitUse.RingFinal), (lights.Use, sockets.Use));
        Assert.Equal(("Circuit 1", "Circuit 2"), (lights.Name, sockets.Name));
    }

    [Fact]
    public void WhatIsNotConnectedIsWantingByTheRulesThatAskForIt()
    {
        var document = BimDocument.CreateDefault();
        var basin = Put(document, "Basin - Pedestal", 1000, 0);
        Make(document, SystemKind.ColdWater, basin);

        Assert.Equal(new[] { SystemKind.HotWater, SystemKind.Foul }, MepSystems.OpenKinds(document, basin));
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == basin && problem.Text.Contains("Approved Document G (G3)"));
        Assert.Contains(problems, problem => problem.Element == basin && problem.Text.Contains("Approved Document H (H1)"));
        Assert.Contains(problems, problem => problem.Element is MepSystem && problem.Text.Contains("nothing feeds it - the stop valve on the incoming main does"));

        // Built to the US's codes, Approved Document G asks nothing.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("Approved Document"));

        // On its fitting, in Properties.
        var said = basin.GetInstanceParameters(document).ToDictionary(parameter => parameter.Name, parameter => parameter.Value?.ToString());
        Assert.Equal("Cold Water 1", said["Systems"]);
        Assert.Equal("Hot Water, Foul Water", said["Not Connected"]);
    }

    [Fact]
    public void SmokeAlarmsAreMainsPoweredAndLinkedSoOneSetsOffTheRest()
    {
        var document = BimDocument.CreateDefault();
        var hall = Put(document, "Smoke Alarm", 1000, 1000);
        Assert.DoesNotContain(SystemKind.FireAlarm, MepSystems.OpenKinds(document, hall));
        Assert.Contains(MepSystems.Problems(document), problem => problem.Text.Contains("BS 5839-6") && problem.Text.Contains("mains powered"));

        var landing = Put(document, "Smoke Alarm", 4000, 1000);
        var kitchen = Put(document, "Heat Alarm", 7000, 1000);
        Assert.Contains(SystemKind.FireAlarm, MepSystems.OpenKinds(document, landing));
        Assert.Contains(MepSystems.Problems(document), problem => problem.Text.Contains("links the alarms so that one sounding sounds them all"));

        Make(document, SystemKind.FireAlarm, hall, landing);
        var split = Make(document, SystemKind.FireAlarm, kitchen);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == split && problem.Text.Contains("split between 2 interlinks"));

        document.Remove(split);
        new EditSystemCommand(document.All<MepSystem>().Single(), "Add", system => system.Members.Add(kitchen.Id)).Redo();
        Make(document, SystemKind.Power, hall, landing, kitchen);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("BS 5839-6"));

        // The US: the IRC's R314.4 and R314.6.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var lone = Put(document, "Smoke Alarm", 9000, 1000);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == lone && problem.Text.Contains("IRC R314.6"));
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == lone && problem.Text.Contains("IRC R314.4"));
    }

    [Fact]
    public void ADwellingOf150SquareMetresHasTwoHeatingZonesEachWithItsOwnThermostat()
    {
        var document = BimDocument.CreateDefault();
        BigRoom(document);
        Assert.True(MepSystems.DwellingArea(document) >= MepSystems.TwoZoneArea);

        var boiler = Put(document, "Boiler - Gas Combi", 100, 6000, rotation: -90);
        var downstairs = Make(document, SystemKind.Heating, Put(document, "Radiator - 600 x 1000", 3000, 300));
        Assert.Equal(boiler.Id, downstairs.SourceId);
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == downstairs && problem.Text.Contains("no room thermostat"));
        Assert.Contains(problems, problem => problem.Element == downstairs && problem.Text.Contains("two heating zones"));

        // A second zone off the same boiler, each with its thermostat.
        var upstairs = Make(document, SystemKind.Heating, Put(document, "Radiator - 600 x 1000", 9000, 300), Put(document, "Room Thermostat", 9000, 12000));
        Assert.Equal(boiler.Id, upstairs.SourceId);
        new EditSystemCommand(downstairs, "Thermostat", system => system.Members.Add(Put(document, "Room Thermostat", 3000, 12000).Id)).Redo();
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element is MepSystem { Kind: SystemKind.Heating });
        Assert.Contains("Living", Assert.Single(MepSystems.RoomsServed(document, upstairs)));
    }

    [Fact]
    public void ACombiFeedsOneHotWaterSystemAndACylinderTakesTheOtherBoilersHeat()
    {
        var document = BimDocument.CreateDefault();
        var combi = Put(document, "Boiler - Gas Combi", 0, 0);
        var basin = Put(document, "Basin - Pedestal", 2000, 0);
        var hot = Make(document, SystemKind.HotWater, basin, combi);
        Assert.Equal(combi.Id, hot.SourceId);
        Assert.Equal(new[] { basin.Id }, hot.Members);
        Assert.Contains("feeds Hot Water 1 already", MepSystems.WhyNotSource(document, combi, new MepSystem { Kind = SystemKind.HotWater }));

        var system = Put(document, "Boiler - Gas System", 0, 3000);
        var cylinder = Put(document, "Hot Water Cylinder - Unvented 210 l", 1000, 3000);
        Assert.Contains("cannot feed hot water", MepSystems.WhyNotSource(document, system, new MepSystem { Kind = SystemKind.HotWater }));
        var coil = Make(document, SystemKind.Heating, system, cylinder);
        Assert.Equal(system.Id, coil.SourceId);
        Assert.Equal(new[] { cylinder.Id }, coil.Members);

        // The coil's circuit is the stored hot water's: no thermostat asked of it.
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == coil && problem.Text.Contains("thermostat"));
    }

    [Fact]
    public void SystemsAreUndoneSavedAndOpenedAgain()
    {
        var document = BimDocument.CreateDefault();
        var history = new UndoStack();
        var unit = Put(document, "Consumer Unit - 12 Way", 0, 0);
        var socket = Put(document, "Socket - Double", 1000, 0);
        var other = Put(document, "Socket - Double", 2000, 0);
        var (circuit, _) = MepSystems.Make(document, SystemKind.Power, new[] { socket });
        history.Execute(new AddElementCommand(document, circuit!, "Make Circuit 1"));
        history.Execute(new EditSystemCommand(circuit!, "Add to Circuit 1", system => system.Members.Add(other.Id)));
        Assert.Equal(2, circuit!.Members.Count);
        history.Undo();
        Assert.Single(circuit.Members);
        history.Redo();
        circuit.Use = CircuitUse.Radial;
        circuit.Mark = "C1";
        Type(document, "Boiler - Gas Combi").HotWater = BoilerHotWater.Cylinder;

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var saved = reopened.All<MepSystem>().Single();
        Assert.Equal(("Circuit 1", SystemKind.Power, unit.Id, CircuitUse.Radial, "C1"), (saved.Name, saved.Kind, saved.SourceId!.Value, saved.Use, saved.Mark));
        Assert.Equal(new[] { socket.Id, other.Id }, saved.Members);
        Assert.Equal(BoilerHotWater.Cylinder, reopened.TypesOf<ComponentType>().First(type => type.Name == "Boiler - Gas Combi").HotWater);

        // A fitting deleted is left off the system when it is saved.
        document.Remove(other);
        Assert.Equal(new[] { socket.Id }, ProjectFile.FromJson(ProjectFile.ToJson(document)).All<MepSystem>().Single().Members);
    }

    [Fact]
    public void SystemsGoOutAsIfc4DistributionSystemsWithTheirFittingsPorts()
    {
        var document = BimDocument.CreateDefault();
        var valve = Put(document, "Stop Valve - Incoming Main", 0, 0);
        var basin = Put(document, "Basin - Pedestal", 2000, 0);
        var sink = Put(document, "Sink Unit - 1000", 4000, 0);
        Make(document, SystemKind.ColdWater, valve, basin, sink);
        var unit = Put(document, "Consumer Unit - 12 Way", 0, 3000);
        Make(document, SystemKind.Power, Put(document, "Ceiling Light - Flush", 1000, 3000));
        Make(document, SystemKind.Power, Put(document, "Socket - Double", 2000, 3000));

        using var model = IfcExport.Build(document);
        var systems = model.Instances.OfType<IIfcDistributionSystem>().ToList();
        var cold = systems.Single(system => system.PredefinedType == IfcDistributionSystemEnum.DOMESTICCOLDWATER);
        Assert.Equal("Cold Water 1", cold.Name.ToString());
        var grouped = model.Instances.OfType<IIfcRelAssignsToGroup>().Single(relation => relation.RelatingGroup == cold).RelatedObjects.Select(o => o.GlobalId.ToString()).ToHashSet();
        Assert.Equal(new[] { basin.Id, sink.Id }.Select(Ifc).ToHashSet(), grouped);

        // The stop valve hosts it at its port; the circuits are circuits, aggregated into the board's power system.
        var hosted = model.Instances.OfType<IIfcRelAssignsToProduct>().Single(relation => relation.RelatedObjects.Contains(cold));
        Assert.Equal(IfcFlowDirectionEnum.SOURCE, ((IIfcDistributionPort)hosted.RelatingProduct).FlowDirection);
        var circuits = model.Instances.OfType<IIfcDistributionCircuit>().ToList();
        Assert.Equal(new[] { IfcDistributionSystemEnum.LIGHTING, IfcDistributionSystemEnum.ELECTRICAL }, circuits.Select(circuit => circuit.PredefinedType!.Value).OrderBy(type => type).ToArray().Reverse().ToArray());
        var supply = systems.Single(system => system.Name == "Consumer Unit - 12 Way supply");
        Assert.Equal(2, model.Instances.OfType<IIfcRelAggregates>().Single(relation => relation.RelatingObject == supply).RelatedObjects.Count());
        Assert.All(systems, system => Assert.Contains(model.Instances.OfType<IIfcRelServicesBuildings>(), relation => relation.RelatingSystem == system));

        // A basin's three ports nested in it: hot and cold in, waste out, as pipes.
        var basinProduct = model.Instances.OfType<IIfcSanitaryTerminal>().Single(terminal => terminal.GlobalId.ToString() == Ifc(basin.Id));
        var ports = model.Instances.OfType<IIfcRelNests>().Single(relation => relation.RelatingObject == basinProduct).RelatedObjects.OfType<IIfcDistributionPort>().ToList();
        Assert.Equal(new[] { "Hot Supply", "Cold Supply", "Waste" }, ports.Select(port => port.Name.ToString()));
        Assert.Equal(new[] { IfcFlowDirectionEnum.SINK, IfcFlowDirectionEnum.SINK, IfcFlowDirectionEnum.SOURCE }, ports.Select(port => port.FlowDirection!.Value));
        Assert.Equal(IfcDistributionSystemEnum.DRAINAGE, ports[2].SystemType);
        Assert.All(ports, port => Assert.Equal(IfcDistributionPortTypeEnum.PIPE, port.PredefinedType));
        _ = unit;
    }

    [Fact]
    public void AServicesPlanDrawsTheBuildingFaintAndWhatIsNotConnectedRed()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0];
        var type = document.TypesOf<WallType>().First();
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), TypeId = type.Id, LevelId = level.Id, UnconnectedHeight = 2700 };
        document.Add(wall);
        var socket = Put(document, "Socket - Double", 1000, 100);
        var basin = Put(document, "Basin - Pedestal", 3000, 300);

        var electrical = ServicesPlans.Plan(level.Id, Discipline.Electrical);
        Assert.Equal($"{level.Name} Electrical Plan", electrical.TitleIn(document));
        Assert.Equal($"{level.Name} Plan", ViewReference.FloorPlan(level.Id).TitleIn(document));
        var style = document.ViewSettings.StyleFor(document, electrical);
        Assert.True(style(wall).Halftone);
        Assert.True(style(basin).Halftone);
        Assert.Equal(ServicesPlans.Unconnected, style(socket).Colour);

        Make(document, SystemKind.Power, socket, Put(document, "Consumer Unit - 12 Way", 0, 100));
        Assert.Equal(ServicesPlans.Colour(Discipline.Electrical), document.ViewSettings.StyleFor(document, electrical)(socket).Colour);
        Assert.True(document.ViewSettings.StyleFor(document, ViewReference.FloorPlan(level.Id))(wall).IsPlain);
    }

    [Fact]
    public void FittingsAreClickedOnAndOffASystemAndItsSourcePickedInThePlan()
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

                var radiator = Put(document, "Radiator - 600 x 1000", 2000, 0);
                var towel = Put(document, "Towel Rail - 500 x 1200", 5000, 0);
                var basin = Put(document, "Basin - Pedestal", 7000, 0);
                var boiler = Put(document, "Boiler - Gas Combi", -2000, 0);
                var zone = new MepSystem { Kind = SystemKind.Heating, Name = "Heating Zone 1" };
                document.Add(zone);

                plan.EditSystem(zone);
                Assert.True(plan.ClickSystemMember(radiator.Location));
                Assert.True(plan.ClickSystemMember(towel.Location));
                Assert.False(plan.ClickSystemMember(basin.Location));
                Assert.Equal(new[] { radiator.Id, towel.Id }, zone.Members);
                Assert.True(plan.ClickSystemMember(radiator.Location));
                Assert.Equal(new[] { towel.Id }, zone.Members);
                history.Undo();
                Assert.Equal(new[] { radiator.Id, towel.Id }, zone.Members);

                plan.PickSystemSource(zone);
                Assert.False(plan.ClickSystemSource(basin.Location));
                Assert.True(plan.ClickSystemSource(boiler.Location));
                Assert.Equal(boiler.Id, zone.SourceId);
                Assert.Equal(PlanTool.Select, plan.ActiveTool);
                Assert.Null(plan.EditingSystem);

                plan.ActiveDiscipline = Discipline.Mechanical;
                Assert.Equal(ServicesPlans.Plan(document.Levels[0].Id, Discipline.Mechanical), plan.CurrentView);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }
}
