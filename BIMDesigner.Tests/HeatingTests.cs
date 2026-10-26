using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// Heating (round M4): heat loss room by room as BS EN 12831-1 works it and MCS MIS 3005-D asks;
/// radiators and underfloor heating against it; heat sources, flow temperatures and hot water by
/// Approved Document L and MIS 3005-D; heating pipes sized for flow and pressure drop; and all of
/// it saved and sent out as IFC.
/// </summary>
[Collection("Wpf")]
public class HeatingTests
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

    /// <summary>
    /// A storey of rooms on the ground floor: an outside wall round 4 m by 3 m inside for each,
    /// side by side along x and parted by inside walls; a screed floor under them and a flat roof
    /// over at 3 m; a window in the first room's south wall. The rooms are named as given.
    /// </summary>
    private static (BimDocument Document, List<Room> Rooms, List<Wall> Walls, Floor Floor, Roof Roof) Storey(string floorType = "Floor - Screed on Slab 250mm", params string[] names)
    {
        if (names.Length == 0) names = new[] { "Living Room" };
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var inside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Interior);
        var half = outside.Structure.TotalWidth / 2;
        var length = 4000.0 * names.Length + inside.Structure.TotalWidth * (names.Length - 1);
        var corners = new[] { new Point2D(-half, -half), new Point2D(length + half, -half), new Point2D(length + half, 3000 + half), new Point2D(-half, 3000 + half) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = outside.Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            walls.Add(wall);
        }

        for (var i = 1; i < names.Length; i++)
        {
            var x = i * (4000 + inside.Structure.TotalWidth) - inside.Structure.TotalWidth / 2;
            var partition = new Wall { Start = new Point2D(x, -half), End = new Point2D(x, 3000 + half), TypeId = inside.Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(partition);
            walls.Add(partition);
        }

        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First(type => type.Name == floorType).Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        var roof = new Roof { LevelId = level, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 3000 };
        roof.SetBoundary(corners);
        document.Add(roof);

        var window = document.TypesOf<WindowType>().First();
        document.Add(new BimWindow { TypeId = window.Id, LevelId = level, HostWallId = walls[0].Id, DistanceAlongWall = 2000 + half, Mark = "W1" });

        var rooms = names.Select((name, i) => new Room { LevelId = level, Location = new Point2D(2000 + i * (4000 + inside.Structure.TotalWidth), 1500), Name = name, Number = $"{i + 1}" }).ToList();
        foreach (var room in rooms) document.Add(room);
        return (document, rooms, walls, floor, roof);
    }

    [Fact]
    public void ARoomLosesThroughEachSurfaceToWhatIsBeyondAndWithItsAir()
    {
        var (document, rooms, walls, floor, roof) = Storey();
        var building = HeatLoss.Of(document)!;
        var loss = building.Of(rooms[0])!;

        // London: -3.0 °C outside, the ground at 11.3; a living room at 21.
        Assert.Equal(-3.0, building.Outside, 6);
        Assert.Equal(11.3, building.Ground, 6);
        Assert.Equal(21, loss.Temperature);

        // Four walls, storey to storey 3 m high, the window out of the south one; each U with 0.08 for its thermal bridges.
        var wallType = document.GetWallType(walls[0])!;
        var wallU = WallRules.UValue(document, wallType)!.Value + HeatLoss.ExternalBridging;
        var window = document.TypesOf<WindowType>().First();
        var glazed = window.Width * window.Height / 1e6;
        var wallLines = loss.Lines.Where(line => line.Through is Wall).ToList();
        Assert.Equal(4, wallLines.Count);
        Assert.All(wallLines, line => Assert.Equal(HeatPath.Outside, line.Path));
        Assert.Equal(14 * 3 - glazed, wallLines.Sum(line => line.Area), 3);
        Assert.Equal(wallU * (14 * 3 - glazed) * 24, wallLines.Sum(line => line.Watts), 3);
        var glass = Assert.Single(loss.Lines, line => line.Through is BimWindow);
        Assert.Equal(glazed * (window.HeatTransferCoefficient + 0.08) * 24, glass.Watts, 3);

        // The solid floor to the ground at the annual mean, the roof to the outside.
        var ground = Assert.Single(loss.Lines, line => line.Through == floor);
        Assert.Equal(HeatPath.Ground, ground.Path);
        Assert.Equal(11.3, ground.Beyond, 6);
        Assert.Equal(12 * (SlabRules.UValue(document, floor)!.Value + 0.08) * (21 - 11.3), ground.Watts, 3);
        var top = Assert.Single(loss.Lines, line => line.Through == roof);
        Assert.Equal(12 * (SlabRules.UValue(document, roof)!.Value + 0.08) * 24, top.Watts, 3);

        // The air: 0.05 of 8 m³/h·m² at 50 Pa over the room's 66 m² of envelope - its own home's
        // whole envelope, so not doubled - or half its volume an hour, whichever is more, at 0.34 W a kelvin.
        Assert.Equal(66, loss.Envelope, 3);
        Assert.Equal(0.05 * 8 * 66, loss.Leakage, 3);
        Assert.Equal(Math.Max(0.05 * 8 * 66, 0.5 * loss.Volume), loss.AirFlow, 3);
        Assert.Equal(0.34 * loss.AirFlow * 24, loss.Ventilation, 3);
        Assert.Equal(loss.Fabric + loss.Ventilation, loss.Total, 6);
        Assert.Equal(building.Total, loss.Total, 3);

        // Its Properties say it.
        Assert.StartsWith($"{loss.Total:#,0} W", rooms[0].GetInstanceParameters(document).First(parameter => parameter.Name == "Heat Loss").DisplayValue);
    }

    [Fact]
    public void ASuspendedFloorLosesToTheOutsideAndHigherSitesAreColder()
    {
        var (document, rooms, _, floor, _) = Storey("Floor - Timber 220mm");
        Assert.True(HeatLoss.Suspended(document, document.FindType<SlabType>(floor.TypeId)));
        var ground = Assert.Single(HeatLoss.OfRoom(document, rooms[0])!.Lines, line => line.Through == floor);
        Assert.Equal(-3.0, ground.Beyond, 6);

        // 260 m up, two whole hundreds over Heathrow's 25: 1.2 °C colder.
        document.ProjectInformation.SiteAltitude = 260;
        Assert.Equal(-4.2, Climate.DesignOutside(document)!.Value, 6);
        document.ProjectInformation.HeatingLocation = HeatingLocation.Glasgow;
        Assert.Equal(-5.6 - 0.6 * 2, Climate.DesignOutside(document)!.Value, 6);
        Assert.Equal(9.1, Climate.Ground(document)!.Value, 6);

        // In the US, nothing without the jurisdiction's winter design temperature; then rooms at 68 °F.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Null(HeatLoss.Of(document));
        document.ProjectInformation.WinterDesignTemperature = 14;
        Assert.Equal(-10, Climate.DesignOutside(document)!.Value, 6);
        Assert.Equal(20, HeatLoss.OfRoom(document, rooms[0])!.Temperature);
    }

    [Fact]
    public void RoomsKeptAtOtherTemperaturesAndSpacesNotHeatedTakeHeatFromTheirNeighbours()
    {
        var (document, rooms, walls, _, _) = Storey("Floor - Screed on Slab 250mm", "Bedroom", "Bathroom", "Garage");
        var building = HeatLoss.Of(document)!;
        var (bedroom, bathroom, garage) = (building.Of(rooms[0])!, building.Of(rooms[1])!, building.Of(rooms[2])!);
        Assert.Equal(21, bedroom.Temperature);
        Assert.Equal(22, bathroom.Temperature);
        Assert.False(garage.Heated);
        Assert.Equal(0, garage.Total);

        // The bathroom loses a kelvin's worth to the bedroom, the bedroom gains it back; the garage is taken at the annual mean.
        var toBedroom = Assert.Single(bathroom.Lines, line => line.Next == rooms[0]);
        Assert.Equal(HeatPath.Room, toBedroom.Path);
        Assert.Equal(21, toBedroom.Beyond, 6);
        var fromBathroom = Assert.Single(bedroom.Lines, line => line.Next == rooms[1]);
        Assert.True(fromBathroom.Watts < 0);
        var toGarage = Assert.Single(bathroom.Lines, line => line.Next == rooms[2]);
        Assert.Equal(HeatPath.Unheated, toGarage.Path);
        Assert.Equal(11.3, toGarage.Beyond, 6);
        Assert.Equal(WallRules.UValue(document, document.GetWallType(walls[^1])!)!.Value + HeatLoss.InternalBridging, toGarage.U, 6);

        // What goes from room to room is no loss to the building.
        Assert.Equal(bedroom.Lost + bathroom.Lost + building.Ventilation, building.Total, 3);
        Assert.Equal(building.Rooms.Where(room => room.Heated).Sum(room => room.Lines.Where(line => line.Path != HeatPath.Room).Sum(line => line.Watts)), building.Fabric, 3);

        // A room's temperature set, or it set unheated, by its Properties.
        var temperature = rooms[0].GetInstanceParameters(document).First(parameter => parameter.Name == "Design Temperature");
        Assert.Equal("By its use (21 °C)", temperature.DisplayValue);
        temperature.TrySetFromText("18");
        Assert.Equal(18, HeatLoss.OfRoom(document, rooms[0])!.Temperature);
        temperature.TrySetFromText("Unheated");
        Assert.False(HeatLoss.OfRoom(document, rooms[0])!.Heated);
        temperature.TrySetFromText("By its use");
        Assert.Equal(21, HeatLoss.OfRoom(document, rooms[0])!.Temperature);
    }

    [Fact]
    public void RadiatorsGiveTheirRatedOutputAtFiftyKelvinAndLessAsTheWaterCools()
    {
        var document = BimDocument.CreateDefault();
        var radiator = Type(document, "Radiator - 600 x 1000");
        Assert.Equal(1732, Heating.RadiatorOutput(radiator, 75, 65, 20), 6);
        Assert.Equal(1732 * Math.Pow(29.0 / 50, 1.33), Heating.RadiatorOutput(radiator, 55, 45, 21), 6);
        Assert.Equal(839, Math.Round(1732 * Math.Pow(29.0 / 50, 1.33)));
        Assert.Equal(379, Type(document, "Towel Rail - 500 x 1200").HeatOutput);

        // A floor: so much a square metre for each kelvin of mean water over the room, to its limit; its surface by BS EN 1264's curve.
        var floor = Type(document, "Underfloor Heating - 16 mm at 150 in Screed");
        Assert.Equal(3.35 * 21.5, Heating.FloorFlux(floor, 45, 40, 21), 6);
        Assert.Equal(98, Heating.FloorFlux(floor, 75, 65, 20), 6);
        Assert.Equal(100.0, Heating.MostFlux, 0);
        Assert.Equal(29, Heating.SurfaceTemperature(Heating.MostFlux, 20), 6);
    }

    [Fact]
    public void ARoomShortOfHeatIsSaidAndSizeRadiatorsFindsTheRadiatorsThatGiveIt()
    {
        var (document, rooms, _, _, _) = Storey();
        var boiler = Put(document, "Boiler - Gas System", 3800, 2800, 180);
        var radiator = Put(document, "Radiator - 600 x 1000", 2000, 80);
        var zone = MepSystems.Make(document, SystemKind.Heating, new[] { boiler, radiator }).System!;
        document.Add(zone);

        // Run at 45/35 °C, its 1732 W radiator gives 1732 (19/50)^1.33 - less than the room loses.
        zone.FlowTemperature = 45;
        zone.ReturnTemperature = 35;
        var loss = HeatLoss.OfRoom(document, rooms[0])!;
        var given = Heating.RadiatorOutput(Type(document, "Radiator - 600 x 1000"), 45, 35, 21);
        Assert.True(given < loss.Total, $"{given} against {loss.Total}");
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == rooms[0] && problem.Text.Contains("BS EN 12831-1"));
        Assert.Contains("short of its", Heating.Verdict(document, rooms[0], loss));

        // Its flow dropping to it from a run at 2.4 m.
        Point3D Tap(string name) => Connectors.Placed(document, radiator).First(placed => placed.Connector.Name == name).At;
        var copper = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper);
        Pipe Run(Point3D from, Point3D to)
        {
            var pipe = new Pipe
            {
                TypeId = copper.Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y),
                StartOffset = from.Z, EndOffset = to.Z, Diameter = 15, Kind = SystemKind.Heating, SystemId = zone.Id
            };
            document.Add(pipe);
            return pipe;
        }

        var tap = Tap("Flow");
        var run = Run(new Point3D(tap.X, 1500, 2400), tap with { Z = 2400 });
        var drop = Run(tap with { Z = 2400 }, tap);

        var (command, changed, unmet) = Heating.SizeRadiators(document);
        Assert.Equal(1, changed);
        Assert.Empty(unmet);

        // The wider radiator's tapping further along: the drop carried across to it, still upright, the run to its head.
        var moved = Tap("Flow");
        Assert.True(Math.Abs(moved.X - tap.X) > 100);
        Assert.Equal((moved.X, moved.Y, moved.Z), (drop.End.X, drop.End.Y, drop.EndOffset));
        Assert.True(drop.Upright);
        Assert.Equal((moved.X, moved.Y), (run.End.X, run.End.Y));
        Assert.Equal((tap.X, 1500.0), (run.Start.X, run.Start.Y));
        // The range's shortest that gives it is a double panel 1600 long - which the project has already, by another name.
        var sized = document.FindType<ComponentType>(radiator.TypeId)!;
        Assert.True(Heating.RadiatorOutput(sized, 45, 35, 21) >= loss.Total - 0.5);
        Assert.Equal("Radiator - 600 x 1600", sized.Name);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == rooms[0]);
        Assert.Contains("enough for its", Heating.Verdict(document, rooms[0], HeatLoss.OfRoom(document, rooms[0])!));

        command!.Undo();
        Assert.Equal("Radiator - 600 x 1000", document.FindType<ComponentType>(radiator.TypeId)!.Name);
        Assert.Equal((tap.X, tap.Y), (drop.End.X, drop.End.Y));
        Assert.Equal(tap.X, run.End.X);

        // At 35/30 not even the range's longest double panel 600 high gives it - which is said.
        zone.FlowTemperature = 35;
        zone.ReturnTemperature = 30;
        var (_, _, still) = Heating.SizeRadiators(document);
        Assert.Contains(still, said => said.Contains("more than one radiator 600 high gives at 35/30"));
    }

    [Fact]
    public void AFloorOpenUnderneathLosesToTheOutsideAndACeilingUnderAFloorIsNotTheRoof()
    {
        // A room over the storey below where there is nothing - no floor, no room: it overhangs, open to the air under it.
        var (document, rooms, _, _, _) = Storey();
        var upper = document.Levels.OrderBy(level => level.Elevation).First(level => level.Elevation > 0);
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 5000), new Point2D(4000, 5000), new Point2D(4000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = outside.Id, LevelId = upper.Id, UnconnectedHeight = 3000 });
        var slab = new Floor { LevelId = upper.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        slab.SetBoundary(corners);
        document.Add(slab);
        var lid = new Roof { LevelId = upper.Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 3000 };
        lid.SetBoundary(corners);
        document.Add(lid);
        var over = new Room { LevelId = upper.Id, Location = new Point2D(2000, 6500), Name = "Bedroom 1", Number = "2" };
        document.Add(over);
        var loss = HeatLoss.OfRoom(document, over)!;
        var floor = loss.Lines.Single(line => line.What.StartsWith("Floor over"));
        Assert.Equal(HeatPath.Outside, floor.Path);
        Assert.Equal(HeatLoss.Of(document)!.Outside, floor.Beyond, 6);

        // A shed is not heated; a furnished room is.
        Assert.Null(HeatLoss.ByUse(document, new Room { Name = "Garden Shed" }));
        Assert.Equal(21, HeatLoss.ByUse(document, new Room { Name = "Furnished Room" }));
        _ = rooms;
    }

    [Fact]
    public void ARoomWithNothingHeatingItAndAFloorAskedTooMuchAreSaid()
    {
        var (document, rooms, _, _, _) = Storey("Floor - Screed on Slab 250mm", "Kitchen", "Hall");
        var pump = Put(document, "Heat Pump - Air Source 7 kW", -1000, 1500, 90);
        var loop = Put(document, "Underfloor Heating - 16 mm at 150 in Screed", 2000, 1500);
        document.Add(MepSystems.Make(document, SystemKind.Heating, new[] { pump, loop }).System!);

        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == rooms[1] && problem.Text.StartsWith("nothing heats it"));

        // At a heat pump's 45/40 the kitchen's floor gives 3.35 W/m²K over 21.5 K.
        var output = Heating.OutputOf(document, loop)!;
        Assert.Equal(45, output.Flow);
        Assert.Equal(3.35 * 21.5 * 12, output.Watts, 3);
        Assert.Contains("W/m²", loop.GetInstanceParameters(document).First(parameter => parameter.Name == "Output at Design").DisplayValue);

        // A floor of 20 m² at 150 centres wants 133 m of pipe: two loops.
        var big = Type(document, "Underfloor Heating - 16 mm at 150 in Screed");
        Assert.Equal(150, big.PipeSpacing);
        Assert.Equal(12 / 0.15, output.FloorArea / (big.PipeSpacing / 1000), 6);
        Assert.DoesNotContain(problems, problem => problem.Element == loop && problem.Text.Contains("loops"));

        // Flowing at 60 °C: too hot for a new system, and for the screed.
        var zone = MepSystems.MemberOf(document, loop, SystemKind.Heating)!;
        zone.FlowTemperature = 60;
        problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == zone && problem.Text.Contains("Approved Document L (5.10)"));
        Assert.Contains(problems, problem => problem.Element == loop && problem.Text.Contains("55 °C at most"));
    }

    [Fact]
    public void AHeatPumpGivesTheWholeLoadAndABoilerIsCheckedAgainstIt()
    {
        var (document, rooms, _, _, _) = Storey("Floor - Screed on Slab 250mm", "Living Room", "Dining Room");
        var pump = Put(document, "Heat Pump - Air Source 7 kW", -1000, 1500, 90);
        var left = Put(document, "Radiator - 600 x 1600", 2000, 80);
        var right = Put(document, "Radiator - 600 x 1600", 6100, 80);
        document.Add(MepSystems.Make(document, SystemKind.Heating, new[] { pump, left, right }).System!);
        var building = HeatLoss.Of(document)!;

        // It heats every heated room, so the whole building's load is on it - which is less than its 7 kW.
        var (load, heated) = Heating.LoadOn(document, pump, building);
        Assert.Equal(building.Total, load, 6);
        Assert.Equal(2, heated.Count);
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == pump && problem.Text.Contains("heat load"));

        Type(document, "Heat Pump - Air Source 7 kW").HeatOutput = load * 0.9;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == pump && problem.Text.Contains("MIS 3005-D (5.5.1 e)") && problem.Text.Contains("(5.12)"));
        Type(document, "Heat Pump - Air Source 7 kW").HeatOutput = 0;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == pump && problem.Text.StartsWith("its heat output is not set"));

        // The home's hot water: no bedrooms, so one more than none - 45 litres.
        Assert.Equal((45.0, 1), Heating.HotWaterDemand(document));
        rooms[1].Name = "Bedroom";
        Assert.Equal((90.0, 2), Heating.HotWaterDemand(document));
    }

    [Fact]
    public void HeatingPipesCarryTheHeatBeyondThemAndAreSizedForFlowAndPressureDrop()
    {
        // Darcy-Weisbach: 0.1 kg/s of water at 50 °C in 15 mm copper goes at 0.76 m/s, losing some 590 Pa a metre - too much; 18 mm carries it.
        var (velocity, gradient) = HeatingPipes.Friction(0.1, 13, PipeMaterial.Copper, 50);
        Assert.Equal(0.763, velocity, 2);
        Assert.InRange(gradient, 560, 620);
        Assert.Equal("18 x 1.0", HeatingPipes.Required(PipeMaterial.Copper, 0.1, 50)!.Name);
        Assert.Equal("15 x 1.0", HeatingPipes.Required(PipeMaterial.Copper, 0.02, 50)!.Name);

        var (document, rooms, _, _, _) = Storey("Floor - Screed on Slab 250mm", "Living Room", "Dining Room");
        var boiler = Put(document, "Boiler - Gas System", 3800, 2800, 180);
        var left = Put(document, "Radiator - 600 x 1600", 2000, 80);
        var right = Put(document, "Radiator - 600 x 1600", 6100, 80);
        var zone = MepSystems.Make(document, SystemKind.Heating, new[] { boiler, left, right }).System!;
        document.Add(zone);

        Point3D End(Component fitting, string name) => Connectors.Placed(document, fitting).First(placed => placed.Connector.Name == name).At;
        var copper = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper);
        Pipe Run(Point3D from, Point3D to)
        {
            var pipe = new Pipe
            {
                TypeId = copper.Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y),
                StartOffset = from.Z, EndOffset = to.Z, Diameter = 15, Kind = SystemKind.Heating, SystemId = zone.Id
            };
            document.Add(pipe);
            return pipe;
        }

        // The flow out to a tee, on to each radiator; the returns back the same way.
        var flowTee = new Point3D(3000, 1000, 0);
        var returnTee = new Point3D(3100, 1000, 0);
        var main = Run(End(boiler, "Heating Flow"), flowTee);
        var toLeft = Run(flowTee, End(left, "Flow"));
        Run(flowTee, End(right, "Flow"));
        var back = Run(returnTee, End(boiler, "Heating Return"));
        Run(End(left, "Return"), returnTee);
        Run(End(right, "Return"), returnTee);

        var network = PipeNetworks.Trace(document, zone);
        var leftOutput = Heating.OutputOf(document, left)!.Watts;
        var rightOutput = Heating.OutputOf(document, right)!.Watts;
        Assert.Equal(leftOutput + rightOutput, network.Loads[main].Heat, 3);
        Assert.Equal(leftOutput + rightOutput, network.Loads[back].Heat, 3);
        Assert.Equal(leftOutput, network.Loads[toLeft].Heat, 3);

        var carried = HeatingPipes.Of(document, main, network)!;
        Assert.Equal((leftOutput + rightOutput) / (4180 * 10), carried.MassFlow, 9);
        Assert.Contains("W beyond it", PipeNetworks.Describe(document, main));

        // Sized: each pipe the smallest within 1.0 m/s and 300 Pa a metre.
        var sized = PipeNetworks.Sized(document, new[] { zone });
        foreach (var (pipe, diameter) in sized) pipe.Diameter = diameter;
        network = PipeNetworks.Trace(document, zone);
        Assert.All(network.Loads.Keys, pipe =>
        {
            var flow = HeatingPipes.Of(document, pipe, network)!;
            Assert.True(flow.Velocity <= 1.0 && flow.Gradient <= 300, $"{pipe.Diameter}: {flow.Velocity} m/s, {flow.Gradient} Pa/m");
        });
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element is Pipe && problem.Text.StartsWith("too small"));

        var (index, pressure, mass) = HeatingPipes.IndexCircuit(document, network);
        Assert.NotNull(index);
        Assert.True(pressure > 0);
        Assert.Equal(carried.MassFlow, mass, 6);
    }

    [Fact]
    public void TheFlowAndTheReturnAreDrawnSideBySideAndNeverJoin()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();
                var (document, _, _, _, _) = Storey();
                var history = new BIMDesigner.Core.Documents.Commands.UndoStack();
                var plan = new BIMDesigner.UI.Controls.PlanView { Width = 1000, Height = 800, Document = document, History = history };
                plan.Measure(new System.Windows.Size(1000, 800));
                plan.Arrange(new System.Windows.Rect(0, 0, 1000, 800));
                plan.ActiveLevelId = document.Levels[0].Id;
                plan.ActivePipeTypeId = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper).Id;

                var boiler = Put(document, "Boiler - Gas System", 3800, 2800, 180);
                var radiator = Put(document, "Radiator - 600 x 1000", 2000, 80);
                var zone = MepSystems.Make(document, SystemKind.Heating, new[] { boiler, radiator }).System!;
                document.Add(zone);
                Point3D End(Component fitting, string name) => Connectors.Placed(document, fitting).First(placed => placed.Connector.Name == name).At;

                // The flow: out of the boiler, along at 2300 to over the radiator, down into its flow.
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Pipe);
                plan.NewPipeOffset = 2300;
                var flow = End(boiler, "Heating Flow");
                Assert.True(plan.ClickPipe(new Point2D(flow.X, flow.Y)));
                Assert.True(plan.ClickPipe(new Point2D(flow.X, 1000)));
                var radiatorFlow = End(radiator, "Flow");
                Assert.True(plan.ClickPipe(new Point2D(radiatorFlow.X, 1000)));
                Assert.True(plan.ClickPipe(new Point2D(radiatorFlow.X, radiatorFlow.Y)));
                var flowPipes = document.All<Pipe>().ToList();
                Assert.All(flowPipes, pipe => Assert.False(pipe.Return));

                // The return at 2450, its main clicked right over the flow's in plan: it runs on rather than teeing into it.
                plan.NewPipeOffset = 2450;
                var back = End(boiler, "Heating Return");
                Assert.True(plan.ClickPipe(new Point2D(back.X, back.Y)));
                Assert.True(plan.ClickPipe(new Point2D(back.X, 1000)));
                Assert.Equal(flowPipes.Count, document.All<Pipe>().Count(pipe => !pipe.Return));

                // The radiator's flow refuses it; its return takes it.
                var radiatorReturn = End(radiator, "Return");
                Assert.True(plan.ClickPipe(new Point2D(radiatorReturn.X, 1000)));
                Assert.False(plan.ClickPipe(new Point2D(radiatorFlow.X, radiatorFlow.Y)));
                Assert.True(plan.ClickPipe(new Point2D(radiatorReturn.X, radiatorReturn.Y)));
                Assert.Contains(document.All<Pipe>(), pipe => pipe.Return);
                Assert.All(document.All<Pipe>(), pipe => Assert.Equal(zone.Id, pipe.SystemId));

                // Traced from the boiler: both sides carry the radiator's heat, every pipe reached.
                var network = PipeNetworks.Trace(document, zone);
                Assert.Empty(network.Unreached);
                var output = Heating.OutputOf(document, radiator)!.Watts;
                Assert.All(network.Loads.Values, carried => Assert.Equal(output, carried.Heat, 3));
                Assert.Equal("Return", document.All<Pipe>().First(pipe => pipe.Return).GetInstanceParameters(document).First(parameter => parameter.Name == "Flow or Return").DisplayValue);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }

    [Fact]
    public void HeatingIsSavedAndAnOlderFilesRadiatorsGetTheirOutputs()
    {
        var (document, rooms, _, _, _) = Storey();
        var boiler = Put(document, "Boiler - Gas System", 3800, 2800, 180);
        var radiator = Put(document, "Radiator - 600 x 1000", 2000, 80);
        var zone = MepSystems.Make(document, SystemKind.Heating, new[] { boiler, radiator }).System!;
        zone.FlowTemperature = 50;
        zone.ReturnTemperature = 40;
        document.Add(zone);
        var copper = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper);
        document.Add(new Pipe { TypeId = copper.Id, LevelId = document.Levels[0].Id, Start = new Point2D(0, 500), End = new Point2D(1000, 500), Kind = SystemKind.Heating, SystemId = zone.Id, Return = true });
        document.ProjectInformation.HeatingLocation = HeatingLocation.Edinburgh;
        document.ProjectInformation.AirPermeability = 5;
        rooms[0].DesignTemperature = 20;
        Type(document, "Underfloor Heating - 16 mm at 200 in Screed").FloorOutput = 3.1;

        {
            var json = ProjectFile.ToJson(document);
            var loaded = ProjectFile.FromJson(json);
            Assert.Equal(HeatingLocation.Edinburgh, loaded.ProjectInformation.HeatingLocation);
            Assert.Equal(5, loaded.ProjectInformation.AirPermeability);
            Assert.Equal(20, loaded.All<Room>().Single().DesignTemperature);
            var heating = loaded.All<MepSystem>().Single(system => system.Kind == SystemKind.Heating);
            Assert.Equal((50.0, 40.0), Heating.Design(loaded, heating));
            Assert.Equal(3.1, Type(loaded, "Underfloor Heating - 16 mm at 200 in Screed").FloorOutput);
            Assert.Equal(1732, Type(loaded, "Radiator - 600 x 1000").HeatOutput);
            Assert.Equal(HeatLoss.Of(document)!.Total, HeatLoss.Of(loaded)!.Total, 6);
            Assert.True(loaded.All<Pipe>().Single().Return);

            // A file from before outputs were kept: its radiators take the library's of their name and size.
            var older = ProjectFile.FromJson(json.Replace("\"heatOutput\":", "\"heatOutputOld\":").Replace("\"heatExponent\":", "\"heatExponentOld\":"));
            Assert.Equal(1732, Type(older, "Radiator - 600 x 1000").HeatOutput);
            Assert.Equal(1.33, Type(older, "Radiator - 600 x 1000").HeatExponent);
            Assert.Equal(18000, Type(older, "Boiler - Gas System").HeatOutput);
        }
    }

    [Fact]
    public void TheIfcCarriesEachSpacesHeatLossTheHeatPumpAndTheRadiatorsOutput()
    {
        var (document, rooms, _, _, _) = Storey();
        Put(document, "Heat Pump - Air Source 7 kW", -1000, 1500, 90);
        Put(document, "Radiator - 600 x 1000", 2000, 80);
        {
            using var model = IfcExport.Build(document);
            var unit = Assert.Single(model.Instances.OfType<IIfcUnitaryEquipment>());
            Assert.Equal("Air source heat pump", unit.ObjectType?.ToString());

            var space = Assert.Single(model.Instances.OfType<IIfcSpace>());
            var sets = space.IsDefinedBy.Select(by => by.RelatingPropertyDefinition).OfType<IIfcPropertySet>().ToList();
            var thermal = sets.Single(set => set.Name == "Pset_SpaceThermalDesign");
            var total = thermal.HasProperties.OfType<IIfcPropertySingleValue>().Single(property => property.Name == "TotalHeatLoss");
            Assert.Equal(Math.Round(HeatLoss.OfRoom(document, rooms[0])!.Total, 1), (double)total.NominalValue.Value, 1);
            var area = sets.Single(set => set.Name == "Pset_SpaceCommon").HasProperties.OfType<IIfcPropertySingleValue>().Single(property => property.Name == "GrossPlannedArea");
            Assert.Equal(12, (double)area.NominalValue.Value, 3);

            var heater = Assert.Single(model.Instances.OfType<IIfcSpaceHeater>());
            var capacity = heater.IsDefinedBy.Select(by => by.RelatingPropertyDefinition).OfType<IIfcPropertySet>()
                .SelectMany(set => set.HasProperties).OfType<IIfcPropertySingleValue>().Single(property => property.Name == "OutputCapacity");
            Assert.Equal(1732, (double)capacity.NominalValue.Value, 3);
        }
    }
}
