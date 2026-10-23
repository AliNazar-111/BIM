using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>
/// Ventilation and ducts (round M5): a home's air as Approved Document F sets it - each room's use,
/// the whole dwelling rate, a heat recovery system's supply and extract shared out - in the heat loss;
/// ducts traced, sized to Table 1.8's static pressure, flexible duct kept to final connections, and
/// fire separation kept where ducts pass compartment walls (Approved Document B); and all of it saved
/// and sent out as IFC.
/// </summary>
[Collection("Wpf")]
public class VentilationTests
{
    internal static ComponentType Type(BimDocument document, string name) => document.TypesOf<ComponentType>().First(type => type.Name == name);

    internal static Component Put(BimDocument document, string type, double x, double y, double rotation = 0)
    {
        var family = Type(document, type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation, Elevation = family.DefaultElevation
        };
        document.Add(component);
        return component;
    }

    /// <summary>Rooms of 4 m by 3 m inside side by side along x, outside walls round them and the given wall type between; a screed floor and a flat roof at 3 m.</summary>
    internal static (BimDocument Document, List<Room> Rooms, List<Wall> Partitions) Row(string between, params string[] names)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var inside = document.TypesOf<WallType>().First(type => type.Name == between);
        var half = outside.Structure.TotalWidth / 2;
        var step = 4000 + inside.Structure.TotalWidth;
        var length = step * names.Length - inside.Structure.TotalWidth;
        var corners = new[] { new Point2D(-half, -half), new Point2D(length + half, -half), new Point2D(length + half, 3000 + half), new Point2D(-half, 3000 + half) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = outside.Id, LevelId = level, UnconnectedHeight = 3000 });
        var partitions = new List<Wall>();
        for (var i = 1; i < names.Length; i++)
        {
            var x = i * step - inside.Structure.TotalWidth / 2;
            var wall = new Wall { Start = new Point2D(x, -half), End = new Point2D(x, 3000 + half), TypeId = inside.Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            partitions.Add(wall);
        }

        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Screed on Slab 250mm").Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        var roof = new Roof { LevelId = level, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 3000 };
        roof.SetBoundary(corners);
        document.Add(roof);
        var rooms = names.Select((name, i) => new Room { LevelId = level, Location = new Point2D(2000 + i * step, 1500), Name = name, Number = $"{i + 1}" }).ToList();
        foreach (var room in rooms) document.Add(room);
        return (document, rooms, partitions);
    }

    internal static readonly string[] Home = { "Living Room", "Kitchen", "Bathroom", "Bedroom 1", "Bedroom 2" };

    /// <summary>A heat recovery unit with a supply valve in each habitable room and an extract valve in each wet one, on their systems.</summary>
    internal static (Component Unit, MepSystem Supply, MepSystem Extract, List<Component> Valves) Ventilate(BimDocument document, List<Room> rooms)
    {
        var unit = Put(document, "Heat Recovery Unit - 75 l/s", 1500, 2700, 180);
        var supplies = new List<Component>();
        var extracts = new List<Component>();
        foreach (var room in rooms)
        {
            var at = room.Location;
            var use = Ventilation.UseOf(document, room, room.GetBoundary(document).Polygon);
            if (use == RoomUse.Habitable) supplies.Add(Put(document, "Supply Valve - 125", at.X, at.Y));
            else if (use != RoomUse.Other) extracts.Add(Put(document, "Extract Valve - 125", at.X, at.Y));
        }

        MepSystem Make(SystemKind kind, IEnumerable<Component> members)
        {
            var (system, refusal) = MepSystems.Make(document, kind, members.Prepend(unit).ToList());
            Assert.True(system is not null, refusal);
            document.Add(system!);
            return system!;
        }

        return (unit, Make(SystemKind.SupplyAir, supplies), Make(SystemKind.ExtractAir, extracts), supplies.Concat(extracts).ToList());
    }

    [Fact]
    public void EachRoomIsSortedByUseAndTheHomeIsVentilatedAtTheWholeDwellingRate()
    {
        var (document, rooms, _) = Row("Generic - 200mm", Home);
        Assert.Equal(new[] { RoomUse.Habitable, RoomUse.Kitchen, RoomUse.Bathroom, RoomUse.Habitable, RoomUse.Habitable },
            rooms.Select(room => Ventilation.UseOf(document, room, room.GetBoundary(document).Polygon)));

        // 60 m² at 0.3 l/s is 18; two bedrooms ask 25 (Table 1.3).
        var (rate, area, bedrooms) = Ventilation.WholeDwelling(document);
        Assert.Equal(60, area, 1);
        Assert.Equal(2, bedrooms);
        Assert.Equal(25, rate, 6);

        // In the US: 0.01 cfm a square foot and 7.5 for each of the bedrooms and one more.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Equal((0.01 * area * 10.7639 + 7.5 * 3) * 0.4719, Ventilation.WholeDwelling(document).Rate, 6);
    }

    [Fact]
    public void AHeatRecoverySystemSuppliesEachHabitableRoomAndExtractsFromEachWetOne()
    {
        var (document, rooms, _) = Row("Generic - 200mm", Home);
        var (unit, supply, extract, valves) = Ventilate(document, rooms);
        Assert.True(Ventilation.WithHeatRecovery(document));
        Assert.Equal(unit.Id, supply.SourceId);
        Assert.Equal(unit.Id, extract.SourceId);

        // Continuously, the whole dwelling's 25 l/s: supplied by the habitable rooms' volumes, extracted as Table 1.2's rates share it.
        var air = Ventilation.Rooms(document);
        Assert.Equal(25, air.Sum(room => room.Supply), 6);
        Assert.Equal(25.0 / 3, air.Single(room => room.Room == rooms[0]).Supply, 3);
        Assert.Equal(25, air.Sum(room => room.Extract), 6);
        Assert.Equal(25 * 13.0 / 21, air.Single(room => room.Room == rooms[1]).Extract, 6);
        Assert.Equal(13, air.Single(room => room.Room == rooms[1]).ExtractAsked);
        Assert.Contains("by heat recovery", Services.Verdict(document, rooms[1]));
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element is Room);

        // A bedroom without its valve, and a unit too small, are said.
        document.Remove(valves.Single(valve => Polygon2D.Contains(rooms[4].GetBoundary(document).Polygon, valve.Location)));
        supply.Members.RemoveAll(id => document.Find<Component>(id) is null);
        Type(document, "Heat Recovery Unit - 75 l/s").Airflow = 20;
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == rooms[4] && problem.Text.Contains("Approved Document F (1.67)"));
        Assert.Contains(problems, problem => problem.Element == unit && problem.Text.StartsWith("it moves 20 l/s at most"));
    }

    [Fact]
    public void HeatRecoveryWarmsTheAirSuppliedAndCutsTheHeatLoss()
    {
        var (document, rooms, _) = Row("Generic - 200mm", Home);
        var before = HeatLoss.Of(document)!;
        Ventilate(document, rooms);
        var after = HeatLoss.Of(document)!;

        // The supply comes in 90% of the way from -3 °C to the extract's mean - kitchen 21, bathroom 22, by their flows.
        var exhaust = (13 * 21 + 8 * 22) / 21.0;
        Assert.Equal(0.9, after.Recovery);
        Assert.Equal(-3 + 0.9 * (exhaust + 3), after.SupplyTemperature!.Value, 6);

        // The living room: its least air change made up by its supply, which comes in nearly warm.
        var living = after.Of(rooms[0])!;
        Assert.Equal(25.0 / 3 * 3.6, living.Supply, 6);
        var leakage = Math.Min(0.05 * 8 * after.Rooms.Where(room => room.Heated).Sum(room => room.Envelope), 2 * living.Leakage);
        Assert.Equal(0.34 * (Math.Max(leakage, living.Minimum - living.Supply) * 24 + living.Supply * (21 - after.SupplyTemperature.Value)), living.Ventilation, 6);
        Assert.Equal(before.Fabric, after.Fabric, 6);

        // The same air supplied with no heat recovered comes in at -3 °C, and the home loses more.
        Type(document, "Heat Recovery Unit - 75 l/s").HeatRecovery = 0;
        var cold = HeatLoss.Of(document)!;
        Assert.Equal(-3, cold.SupplyTemperature!.Value, 6);
        Assert.True(cold.Ventilation > after.Ventilation + 0.34 * 25 * 3.6 * 20);
        _ = before;
    }

    [Fact]
    public void DuctsCarryTheValvesAirAndAreSizedToTheStaticPressureLimit()
    {
        // 30 l/s in a 125 galvanised duct: 2.44 m/s, losing some 0.8 Pa a metre.
        var (velocity, gradient) = DuctNetworks.Friction(30, new DuctSize("125", 125, 125, DuctShape.Round), DuctMaterial.GalvanisedSteel);
        Assert.Equal(2.44, velocity, 2);
        Assert.InRange(gradient, 0.7, 0.95);
        Assert.Equal(60, DuctNetworks.PressureLimit(37));
        Assert.Equal(110, DuctNetworks.PressureLimit(40));
        Assert.Equal(160, DuctNetworks.PressureLimit(60));

        var (document, rooms, _) = Row("Generic - 200mm", Home);
        var (unit, supply, _, valves) = Ventilate(document, rooms);
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        Point3D Spigot(Component fitting, SystemKind kind) => Connectors.Placed(document, fitting).First(placed => placed.Connector.Kind == kind).At;
        Duct Run(Point3D from, Point3D to, SystemKind kind, MepSystem system, DuctType? type = null)
        {
            var duct = new Duct
            {
                TypeId = (type ?? spiral).Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y),
                StartOffset = from.Z, EndOffset = to.Z, Width = 100, Height = 100, Kind = kind, SystemId = system.Id
            };
            document.Add(duct);
            return duct;
        }

        // The supply out of the unit to a junction over the hall, then to each habitable room's valve.
        var out1 = Spigot(unit, SystemKind.SupplyAir);
        var junction = new Point3D(out1.X, 1500, 2700);
        var main = Run(out1, junction, SystemKind.SupplyAir, supply);
        var branches = valves.Where(valve => document.FindType<ComponentType>(valve.TypeId)!.Form == ComponentForm.SupplyTerminal)
            .Select(valve => Run(junction, Spigot(valve, SystemKind.SupplyAir), SystemKind.SupplyAir, supply)).ToList();

        var network = DuctNetworks.Trace(document, supply);
        Assert.Empty(network.Unreached);
        Assert.Empty(network.NotDucted);
        Assert.Equal(25, network.Loads[main].Flow, 6);
        Assert.All(branches, branch => Assert.Equal(25.0 / 3, network.Loads[branch].Flow, 3));
        Assert.Contains("l/s at", DuctNetworks.Describe(document, main));

        // Sized: within 60 Pa for its 25 l/s.
        foreach (var (duct, size) in DuctNetworks.Sized(document, new[] { supply })) (duct.Width, duct.Height) = (size.Width, size.Height);
        network = DuctNetworks.Trace(document, supply);
        var (pressure, longest, flow) = DuctNetworks.Index(document, network);
        Assert.Equal(25, flow, 6);
        Assert.True(longest > 0);
        Assert.True(pressure <= 60, $"{pressure} Pa");
        Assert.All(network.Loads.Keys, duct => Assert.True(DuctNetworks.Carried(document, duct, network)!.Value.Velocity <= DuctNetworks.QuietVelocity + 1e-9));
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == supply && problem.Text.Contains("Table 1.8"));

        // Flexible duct: only a valve's last connection, and 1.5 m at most.
        var flexible = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.Flexible);
        branches[^1].TypeId = flexible.Id;
        Assert.True(branches[^1].Length > 1500);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == branches[^1] && problem.Text.Contains("1.5 m"));
        main.TypeId = flexible.Id;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == main && problem.Text.Contains("final connection"));
    }

    [Fact]
    public void ADuctThroughACompartmentWallKeepsItFireSeparating()
    {
        var (document, rooms, partitions) = Row("Party Wall - Cavity Blockwork 300mm", "Kitchen", "Living Room");
        var (unit, _, extract, _) = Ventilate(document, rooms);
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        var valve = MepSystems.MembersOf(document, extract).Single();
        var at = Connectors.Placed(document, valve).Single(placed => placed.Connector.Kind == SystemKind.ExtractAir).At;

        // From the kitchen's valve across the party wall to the unit next door.
        var into = Connectors.Placed(document, unit).Single(placed => placed.Connector.Kind == SystemKind.ExtractAir).At;
        var duct = new Duct
        {
            TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(at.X, at.Y), End = new Point2D(6000, at.Y), StartOffset = 2700, EndOffset = 2700,
            Width = 125, Height = 125, Kind = SystemKind.ExtractAir, SystemId = extract.Id
        };
        document.Add(duct);
        Assert.Equal(partitions[0], Assert.Single(DuctNetworks.Crossings(document, duct)).Wall);

        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == duct && problem.Text.Contains("Approved Document B (9.12)"));
        duct.FireProtection = FireProtection.FireDamper;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == duct && problem.Text.Contains("(9.15)"));
        duct.FireProtection = FireProtection.FireResisting;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == duct && problem.Text.Contains("Approved Document B"));
        _ = into;
    }

    [Fact]
    public void VentilationIsSavedAndSentOutAsIfc()
    {
        var (document, rooms, _) = Row("Party Wall - Cavity Blockwork 300mm", "Kitchen", "Living Room");
        var (unit, supply, _, _) = Ventilate(document, rooms);
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        var duct = new Duct
        {
            TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(2000, 1500), End = new Point2D(6000, 1500), StartOffset = 2700, EndOffset = 2700,
            Width = 160, Height = 160, Kind = SystemKind.SupplyAir, SystemId = supply.Id, FireProtection = FireProtection.FireDamper
        };
        document.Add(duct);
        Type(document, "Heat Recovery Unit - 75 l/s").HeatRecovery = 0.85;

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = loaded.All<Duct>().Single();
        Assert.Equal((160.0, 160.0), (again.Width, again.Height));
        Assert.Equal(FireProtection.FireDamper, again.FireProtection);
        Assert.Equal(supply.Id, again.SystemId);
        Assert.Equal(spiral.Id, again.TypeId);
        Assert.Equal(0.85, Type(loaded, "Heat Recovery Unit - 75 l/s").HeatRecovery);
        Assert.Equal(4, loaded.TypesOf<DuctType>().Count());

        using var model = IfcExport.Build(document);
        Assert.Single(model.Instances.OfType<IIfcDuctSegment>());
        Assert.Single(model.Instances.OfType<IIfcAirToAirHeatRecovery>());
        Assert.Equal(2, model.Instances.OfType<IIfcAirTerminal>().Count());
        Assert.Equal(IfcDamperTypeEnum.FIREDAMPER, Assert.Single(model.Instances.OfType<IIfcDamper>()).PredefinedType);
        Assert.Contains(model.Instances.OfType<IIfcDistributionSystem>(), system => system.PredefinedType == IfcDistributionSystemEnum.VENTILATION);
        _ = unit;
    }

    [Fact]
    public void TheDuctToolRunsFromTheUnitsSpigotToAValveAndPutsItOnTheSystem()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();
                var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
                var history = new BIMDesigner.Core.Documents.Commands.UndoStack();
                var plan = new BIMDesigner.UI.Controls.PlanView { Width = 1000, Height = 800, Document = document, History = history };
                plan.Measure(new System.Windows.Size(1000, 800));
                plan.Arrange(new System.Windows.Rect(0, 0, 1000, 800));
                plan.ActiveLevelId = document.Levels[0].Id;
                plan.ActiveDuctTypeId = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.Pvc && type.Shape == DuctShape.Round).Id;

                var unit = Put(document, "Heat Recovery Unit - 75 l/s", 1500, 2700, 180);
                var valve = Put(document, "Supply Valve - 125", 2000, 1000);
                var spigot = Connectors.Placed(document, unit).First(placed => placed.Connector.Kind == SystemKind.SupplyAir).At;
                var into = Connectors.Placed(document, valve).Single().At;
                var made = MepSystems.Make(document, SystemKind.SupplyAir, new[] { unit }).System!;
                document.Add(made);

                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Duct);
                plan.NewDuctOffset = 2700;
                Assert.True(plan.ClickDuct(new Point2D(spigot.X, spigot.Y)));
                Assert.True(plan.ClickDuct(new Point2D(spigot.X, 1000)));
                Assert.True(plan.ClickDuct(new Point2D(into.X, into.Y)));

                var ducts = document.All<Duct>().ToList();
                Assert.True(ducts.Count >= 2);
                Assert.All(ducts, duct => Assert.Equal(SystemKind.SupplyAir, duct.Kind));
                Assert.All(ducts, duct => Assert.Equal(125, duct.Width));
                var system = document.All<MepSystem>().Single(each => each.Kind == SystemKind.SupplyAir);
                Assert.Equal(unit.Id, system.SourceId);
                Assert.Contains(valve.Id, system.Members);
                Assert.Empty(DuctNetworks.Trace(document, system).Unreached);
                _ = rooms;
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }
}
