using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;
using static BIMDesigner.Tests.VentilationTests;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// The ventilation's systems as Approved Document F has them - natural with background ventilators,
/// continuous mechanical extract, heat recovery - the pressure lost at bends, tees and valves, ducts'
/// insulation, dampers in compartment floors released by smoke detection, and the US's ACCA Manual D
/// and ASHRAE 62.2.
/// </summary>
[Collection("Wpf")]
public class VentilationSystemsTests
{
    /// <summary>A window in the south wall of each room, of a type with a trickle vent of so much.</summary>
    private static void Windows(BimDocument document, IEnumerable<Room> rooms, double trickle)
    {
        var type = document.TypesOf<WindowType>().First();
        type.TrickleVent = trickle;
        var south = document.Walls.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        foreach (var room in rooms)
            document.Add(new BimWindow { TypeId = type.Id, LevelId = room.LevelId, HostWallId = south.Id, DistanceAlongWall = room.Location.X - south.Start.X, Mark = $"W{room.Number}" });
    }

    [Fact]
    public void ANaturallyVentilatedHomeHasTheBackgroundVentilatorsTable17Asks()
    {
        var (document, rooms, _) = Row("Generic - 200mm", Home);
        Put(document, "Extract Fan - 150 (60 l/s)", rooms[1].Location.X, 2800);
        Put(document, "Extract Fan - 100 (15 l/s)", rooms[2].Location.X, 2800);
        Assert.Equal(VentilationType.Natural, Ventilation.TypeOf(document));

        // No trickle vents: each room with an outside wall is short of Table 1.7's - a single-storey home's 10,000 mm² in a habitable room.
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == rooms[0] && problem.Text.Contains("Table 1.7") && problem.Text.Contains("10000 mm²"));
        Assert.Contains(problems, problem => problem.Element == rooms[2] && problem.Text.Contains("4000 mm²"));
        Assert.Contains(problems, problem => problem.Text.Contains("(1.57)"));

        // A 5000 mm² trickle vent in each window and a 5000 mm² ventilator in each habitable room and the kitchen: enough, and five or more.
        Windows(document, rooms, 5000);
        foreach (var room in rooms.Where(room => Ventilation.UseOf(document, room, room.GetBoundary(document).Polygon) is RoomUse.Habitable or RoomUse.Kitchen))
            Put(document, "Background Ventilator - 5000 mm²", room.Location.X + 1000, 2900);
        var (area, count, _) = Ventilation.BackgroundVentilators(document, rooms[0], rooms[0].GetBoundary(document).Polygon);
        Assert.Equal((10_000.0, 2), (area, count));
        problems = MepSystems.Problems(document);
        Assert.DoesNotContain(problems, problem => problem.Text.Contains("Table 1.7") || problem.Text.Contains("(1.57)"));

        // A ventilator 300 mm from the bathroom's fan.
        Put(document, "Background Ventilator - 5000 mm²", rooms[2].Location.X + 300, 2900);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Text.Contains("(1.55)"));
    }

    [Fact]
    public void AFanOrVentilatorThroughAnOutsideWallServesTheRoomInside()
    {
        var (document, rooms, partitions) = Row("Generic - 200mm", "Kitchen", "Bathroom");
        var south = document.Walls.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        var half = document.GetWallType(south)!.Structure.TotalWidth / 2;

        // On the outer face of the kitchen's outside wall, more than 100 mm from the room.
        var fan = Put(document, "Extract Fan - Continuous dMEV", rooms[0].Location.X, -2 * half);
        fan.HostId = south.Id;
        var vent = Put(document, "Background Ventilator - 5000 mm²", rooms[0].Location.X + 1000, -2 * half);
        vent.HostId = south.Id;
        Assert.Same(fan, Assert.Single(Ventilation.Rooms(document).Single(room => room.Room == rooms[0]).Fans));
        Assert.Equal(5000, Ventilation.BackgroundVentilators(document, rooms[0], rooms[0].GetBoundary(document).Polygon).Area);
        Assert.Contains("extract 8 l/s continuously", Services.Verdict(document, rooms[0]));
        Assert.Equal("Extract Fan - Continuous dMEV in 1 Kitchen", MepSystems.Describe(document, fan));

        // On the far face of a partition it is the other room's.
        var across = Put(document, "Extract Fan - Continuous dMEV", partitions[0].Start.X + 150, 1500);
        across.HostId = partitions[0].Id;
        Assert.DoesNotContain(across, Ventilation.Rooms(document).Single(room => room.Room == rooms[0]).Fans);
        Assert.Contains(across, Ventilation.Rooms(document).Single(room => room.Room == rooms[1]).Fans);
    }

    [Fact]
    public void ContinuousExtractRunsAtTheWholeDwellingRateWithVentilatorsInTheHabitableRoomsOnly()
    {
        var (document, rooms, _) = Row("Generic - 200mm", Home);
        var kitchenFan = Put(document, "Extract Fan - Continuous dMEV", rooms[1].Location.X, 2800);
        Put(document, "Extract Fan - Continuous dMEV", rooms[2].Location.X, 2800);
        Assert.Equal(VentilationType.ContinuousExtract, Ventilation.TypeOf(document));

        // 8 l/s each continuously is 16, short of the whole dwelling's 25 (1.62); a kitchen's fan boosting to 15 meets Table 1.2's 13.
        var air = Ventilation.Rooms(document);
        Assert.Equal(8, air.Single(room => room.Room == rooms[1]).Extract, 6);
        Assert.Equal(15, air.Single(room => room.Room == rooms[1]).ExtractHigh, 6);
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == kitchenFan && problem.Text.Contains("(1.62)"));
        Assert.Contains(problems, problem => problem.Text.Contains("(1.64 b)"));
        Assert.Contains(problems, problem => problem.Text.Contains("(1.64 c)"));
        Assert.Contains("extract 8 l/s continuously, 15 at the high rate", Services.Verdict(document, rooms[1]));

        // Trickle vents in every window: the wet rooms are not to have them.
        Windows(document, rooms, 5000);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == rooms[2] && problem.Text.Contains("(1.64 a)"));

        // A central extract unit drawing from valves instead runs at the whole dwelling rate, and its heat loss takes the extract.
        foreach (var fan in document.All<Component>().Where(component => Type(document, "Extract Fan - Continuous dMEV").Id == component.TypeId).ToList()) document.Remove(fan);
        var unit = Put(document, "Extract Unit - Central MEV 60 l/s", 1500, 2700, 180);
        var valves = new[] { rooms[1], rooms[2] }.Select(room => Put(document, "Extract Valve - 125", room.Location.X, room.Location.Y)).ToList();
        document.Add(MepSystems.Make(document, SystemKind.ExtractAir, valves.Prepend(unit).ToList()).System!);
        Assert.Equal(VentilationType.ContinuousExtract, Ventilation.TypeOf(document));
        Assert.Equal(25, Ventilation.Rooms(document).Sum(room => room.Extract), 6);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("(1.62)"));

        // BS EN 12831-1 for unbalanced ventilation: the 90 m³/h extracted comes in through the envelope, the ordinary leakage cut by f_e.
        var loss = HeatLoss.Of(document)!;
        var envelope = loss.Rooms.Where(room => room.Heated).Sum(room => room.Envelope);
        var leakage50 = 8 * envelope;
        var unbalanced = 90 / leakage50;
        var zone = 90 + leakage50 * 0.05 / (1 + 8 / 0.05 * unbalanced * unbalanced);
        Assert.Equal(zone, loss.Rooms.Where(room => room.Heated).Sum(room => room.Leakage), 6);
    }

    [Fact]
    public void BendsTeesAndValvesLoseWhatAshraeAndTheirMakersSay()
    {
        var round = new DuctSize("125", 125, 125, DuctShape.Round);
        Assert.Equal(0.15, DuctNetworks.BendCoefficient(round, DuctMaterial.GalvanisedSteel, 90, true), 6);
        Assert.Equal(0.09, DuctNetworks.BendCoefficient(round, DuctMaterial.GalvanisedSteel, 45, true), 6);
        Assert.Equal(0.22, DuctNetworks.BendCoefficient(round, DuctMaterial.Flexible, 90, true), 6);
        Assert.Equal(0.27 + (0.25 - 0.27) * (60.0 / 204 - 0.25) / 0.25, DuctNetworks.BendCoefficient(new DuctSize("204 x 60", 204, 60, DuctShape.Rectangular), DuctMaterial.Pvc, 90, true), 6);
        Assert.Equal(0.10, DuctNetworks.DivergingTee(false, 0.5), 6);
        Assert.Equal(0.42, DuctNetworks.DivergingTee(true, 1.0), 6);
        Assert.Equal(0.53, DuctNetworks.ConvergingTee(false, 0.5, 1.0), 6);
        Assert.Equal(0.42, DuctNetworks.ConvergingTee(true, 0.5, 1.0), 6);

        // A supply run from the unit up, along and down to a valve: its bends and the valve's 27 Pa at 20 l/s, as the square of the air.
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        var unit = Put(document, "Heat Recovery Unit - 75 l/s", 1500, 2700, 180);
        var valve = Put(document, "Supply Valve - 125", 2000, 1200);
        var supply = MepSystems.Make(document, SystemKind.SupplyAir, new[] { unit, valve }).System!;
        document.Add(supply);
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        var leaving = Connectors.Placed(document, unit).First(placed => placed.Connector.Kind == SystemKind.SupplyAir).At;
        var to = Connectors.Placed(document, valve).Single().At;
        var corners = new[] { leaving, leaving with { Z = 2700 }, new Point3D(to.X, leaving.Y, 2700), new Point3D(to.X, to.Y, 2700), to };
        for (var i = 0; i + 1 < corners.Length; i++)
            document.Add(new Duct
            {
                TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(corners[i].X, corners[i].Y), End = new Point2D(corners[i + 1].X, corners[i + 1].Y),
                StartOffset = corners[i].Z, EndOffset = corners[i + 1].Z, Width = 125, Height = 125, Kind = SystemKind.SupplyAir, SystemId = supply.Id
            });

        var network = DuctNetworks.Trace(document, supply);
        var flow = network.Draws[valve];
        var path = Assert.Single(DuctNetworks.Paths(document, network));
        Assert.Equal(27 * Math.Pow(flow / 20, 2), path.Device, 6);
        var velocity = DuctNetworks.Friction(flow, round, DuctMaterial.GalvanisedSteel).Velocity;
        Assert.Equal(3 * 0.15 * DuctNetworks.VelocityPressure(velocity), path.Fittings, 6);
        var index = DuctNetworks.IndexOf(document, network);
        Assert.Equal(path.Pressure, index.Pressure, 6);
        Assert.Contains("at the bends and tees", DuctNetworks.Describe(document, network.Loads.Keys.First()));
    }

    [Fact]
    public void IntakeAndExhaustDuctsAreInsulatedAsWhereTheyRunAsks()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        var level = document.Levels[0].Id;
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        Duct At(double height, SystemKind kind)
        {
            var duct = new Duct { TypeId = spiral.Id, LevelId = level, Start = new Point2D(500, 1500), End = new Point2D(3500, 1500), StartOffset = height, EndOffset = height, Width = 125, Height = 125, Kind = kind };
            document.Add(duct);
            return duct;
        }

        // In the heated room under its warm flat roof: 50 mm.
        var inside = At(2000, SystemKind.OutsideAir);
        Assert.Equal(DuctPlace.Heated, DuctNetworks.PlaceOf(document, inside));
        Assert.Equal(50, DuctNetworks.InsulationAsked(document, inside)!.Value.Thickness);
        Assert.Null(DuctNetworks.InsulationAsked(document, At(2000, SystemKind.SupplyAir)));

        // Over a ceiling with its insulation on it, under a roof with none: a cold loft, 25 mm.
        var roof = document.All<Roof>().Single();
        roof.TypeId = document.TypesOf<RoofType>().First(type => type.Name == "Roof - Slate on Timber Rafters 150mm").Id;
        var ceiling = new Ceiling { LevelId = level, TypeId = document.TypesOf<CeilingType>().First(type => type.Name == "Ceiling - Plasterboard under 270mm Mineral Wool").Id, HeightOffset = 2400 };
        ceiling.SetBoundary(roof.Boundary);
        document.Add(ceiling);
        var loft = At(2700, SystemKind.OutsideAir);
        Assert.Equal(DuctPlace.Loft, DuctNetworks.PlaceOf(document, loft));
        Assert.Equal(25, DuctNetworks.InsulationAsked(document, loft)!.Value.Thickness);

        // In the US a supply duct in the attic: R-8, 56 mm at 0.04 W/mK.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var attic = At(2700, SystemKind.SupplyAir);
        Assert.Equal(8 / 5.678 * 0.04 * 1000, DuctNetworks.InsulationAsked(document, attic)!.Value.Thickness, 6);
        _ = rooms;
    }

    [Fact]
    public void ADuctThroughACompartmentFloorIsProtectedAndItsSmokeDampersHaveADetector()
    {
        // Flats: a ground floor and a first floor, the floor between them separating them, a supply riser up through it.
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        var first = document.Levels[1];
        var floorType = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Timber 220mm");
        floorType.Separating = true;
        floorType.FireRating = "90 min";
        var between = new Floor { LevelId = first.Id, TypeId = floorType.Id };
        between.SetBoundary(document.All<Floor>().First().Boundary);
        document.Add(between);
        var upstairs = new Room { LevelId = first.Id, Location = new Point2D(2000, 1500), Name = "Bedroom", Number = "101" };
        document.Add(upstairs);

        var unit = Put(document, "Heat Recovery Unit - 75 l/s", 1500, 2700, 180);
        var below = Put(document, "Supply Valve - 125", 2000, 1000);
        var above = new Component { TypeId = Type(document, "Supply Valve - 125").Id, TypeKind = BuiltInCategory.AirTerminals, LevelId = first.Id, Location = new Point2D(2000, 1000), Elevation = 2340 };
        document.Add(above);
        var supply = MepSystems.Make(document, SystemKind.SupplyAir, new[] { unit, below, above }).System!;
        document.Add(supply);
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        var riser = new Duct
        {
            TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(3000, 2000), End = new Point2D(3000, 2000), StartOffset = 2600, EndOffset = 3600,
            Width = 125, Height = 125, Kind = SystemKind.SupplyAir, SystemId = supply.Id
        };
        document.Add(riser);
        Assert.Equal(between, Assert.Single(DuctNetworks.FloorCrossings(document, riser)).Floor);

        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == riser && problem.Text.Contains("compartment floor unprotected"));

        // A fire damper of 60 minutes in a 90-minute floor, serving homes on both sides: rated too low, and to be released by smoke too (9.14).
        riser.FireProtection = FireProtection.FireDamper;
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == riser && problem.Text.Contains("(9.20, 9.21)") && problem.Text.Contains("90"));
        Assert.Contains(problems, problem => problem.Element == riser && problem.Text.Contains("(9.14)"));

        // A fire and smoke damper of 90 minutes: then a smoke detector in its ductwork to close it.
        riser.FireProtection = FireProtection.FireAndSmokeDamper;
        riser.DamperRating = 90;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == supply && problem.Text.Contains("(9.8, 9.22)"));
        Put(document, "Duct Smoke Detector", 3100, 2000);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => (problem.Element == riser || problem.Element == supply) && problem.Text.Contains("Approved Document B"));

        using var model = IfcExport.Build(document);
        Assert.Equal(IfcDamperTypeEnum.FIRESMOKEDAMPER, Assert.Single(model.Instances.OfType<IIfcDamper>()).PredefinedType);
        Assert.Contains(model.Instances.OfType<IIfcSensor>(), sensor => sensor.PredefinedType == IfcSensorTypeEnum.SMOKESENSOR);
        _ = rooms;
        _ = upstairs;
    }

    [Fact]
    public void InTheUsDuctsKeepToManualDsVelocitiesAndAshrae622IsGiven()
    {
        var (document, rooms, _) = Row("Generic - 200mm", Home);
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (unit, supply, _, valves) = Ventilate(document, rooms);
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        var spigot = Connectors.Placed(document, unit).First(placed => placed.Connector.Kind == SystemKind.SupplyAir).At;
        var main = new Duct
        {
            TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(spigot.X, spigot.Y), End = new Point2D(spigot.X, 2000), StartOffset = spigot.Z, EndOffset = spigot.Z,
            Width = 100, Height = 100, Kind = SystemKind.SupplyAir, SystemId = supply.Id
        };
        document.Add(main);
        var junction = new Point3D(spigot.X, 2000, spigot.Z);
        foreach (var valve in valves.Where(valve => document.FindType<ComponentType>(valve.TypeId)!.Form == ComponentForm.SupplyTerminal))
        {
            var at = Connectors.Placed(document, valve).Single().At;
            document.Add(new Duct
            {
                TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(junction.X, junction.Y), End = new Point2D(at.X, at.Y), StartOffset = junction.Z, EndOffset = at.Z,
                Width = 100, Height = 100, Kind = SystemKind.SupplyAir, SystemId = supply.Id
            });
        }

        // The trunk carries all three valves' air; Manual D: 700 fpm recommended, 900 at most for a rigid supply trunk.
        var network = DuctNetworks.Trace(document, supply);
        var load = network.Loads[main];
        Assert.Equal(3, load.Terminals);
        Assert.Equal((700 * 0.00508, 900 * 0.00508), DuctNetworks.Velocities(document, main, load));
        Assert.Contains("fpm", DuctNetworks.Describe(document, main));
        Assert.Contains("in. w.c. per 100 ft", DuctNetworks.Describe(document, main));
        var branch = network.Loads.First(entry => entry.Value.Terminals == 1);
        Assert.Equal((600 * 0.00508, 900 * 0.00508), DuctNetworks.Velocities(document, branch.Key, branch.Value));

        // A home's air is slow in a 100 mm trunk: well within Manual D, and the valves without a maker's loss lose its 0.03 in. w.c.
        Assert.True(DuctNetworks.Carried(document, main, network)!.Value.Velocity < 700 * 0.00508);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("Manual D"));
        Type(document, "Supply Valve - 125").PressureLoss = 0;
        Assert.Equal(0.03 * 249.09, DuctNetworks.IndexOf(document, network).Device, 6);

        // ASHRAE 62.2: 0.03 cfm a square foot and 7.5 for each bedroom and one more.
        var (_, area, bedrooms) = Ventilation.WholeDwelling(document);
        Assert.Equal((0.03 * area * 10.7639 + 7.5 * (bedrooms + 1)) * 0.4719, Ventilation.Ashrae622(document), 6);
    }

    [Fact]
    public void TheNewVentilationFiguresAreSavedAndSentOut()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room", "Kitchen");
        Windows(document, rooms, 2500);
        document.TypesOf<FloorType>().First().Separating = true;
        Type(document, "Extract Fan - Continuous dMEV").ContinuousRate = 9;
        Put(document, "Background Ventilator - 5000 mm²", 2500, 2900);
        var unit = Put(document, "Extract Unit - Central MEV 60 l/s", 1500, 2700, 180);
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel);
        document.Add(new Duct
        {
            TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(500, 500), End = new Point2D(2500, 500), StartOffset = 2700, EndOffset = 2700,
            Width = 125, Height = 125, Kind = SystemKind.OutsideAir, Insulation = 50, DamperRating = 120, FireProtection = FireProtection.FireDamper
        });

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(2500, loaded.TypesOf<WindowType>().First().TrickleVent);
        Assert.True(loaded.TypesOf<FloorType>().First().Separating);
        Assert.Equal(9, Type(loaded, "Extract Fan - Continuous dMEV").ContinuousRate);
        Assert.Equal(5000, Type(loaded, "Background Ventilator - 5000 mm²").VentArea);
        Assert.Equal((27.0, 20.0), (Type(loaded, "Supply Valve - 125").PressureLoss, Type(loaded, "Supply Valve - 125").RatedAirflow));
        var duct = loaded.All<Duct>().Single();
        Assert.Equal((50.0, 120), (duct.Insulation, duct.DamperRating));

        using var model = IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<IIfcAirTerminal>(), terminal => terminal.ObjectType?.ToString() == "Background ventilator");
        Assert.Contains(model.Instances.OfType<IIfcUnitaryEquipment>(), equipment => equipment.ObjectType?.ToString() == "Mechanical extract unit");
        _ = unit;
    }
}
