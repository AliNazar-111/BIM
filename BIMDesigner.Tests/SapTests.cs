using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;
using Xunit.Abstractions;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// SAP 10.3 and the Future Homes Standard: the UK average climate's sun on a surface, the ratings'
/// equations, the worksheet against figures worked by hand, the notional home Appendix R sets the
/// targets by, Approved Document L 2026's other tests, and a home read off a model and kept in its file.
/// </summary>
[Collection("Wpf")]
public class SapTests
{
    private readonly ITestOutputHelper _output;

    public SapTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheSunOnASurfaceIsWorkedAsAppendixUWorksIt()
    {
        // Level, the sun is Table U3's; a south wall in January, worked by hand from equations U1 to U3 and Table U5.
        for (var m = 0; m < 12; m++) Assert.Equal(SapClimate.Horizontal[m], SapClimate.Flux(200, 0, m), 6);
        Assert.Equal(46.75, SapClimate.Flux(180, 90, 0), 2);

        // East and west alike; south more than north over the year, and a south slope at 30° the most.
        Assert.Equal(SapClimate.Annual(90, 90), SapClimate.Annual(270, 90), 6);
        Assert.True(SapClimate.Annual(180, 90) > 2 * SapClimate.Annual(0, 90));
        Assert.True(SapClimate.Annual(180, 30) > SapClimate.Annual(180, 0) && SapClimate.Annual(180, 30) > SapClimate.Annual(180, 60));
        Assert.True(SapClimate.Annual(180, 45) > SapClimate.Annual(135, 45) && SapClimate.Annual(135, 45) > SapClimate.Annual(90, 45));
    }

    [Fact]
    public void TheRatingsAndTheirBandsAreSectionThirteenAndFourteens()
    {
        // Either side of an energy cost factor of 3.5 the two equations all but meet; the bands of Table 14.
        Assert.Equal(43.27, SapCalculation.Rating(3.4999), 2);
        Assert.Equal(43.24, SapCalculation.Rating(3.5), 2);
        Assert.Equal(100, SapCalculation.Rating(0), 6);
        Assert.Equal(100 - 1.34 * 20, SapCalculation.Impact(20), 6);
        Assert.Equal(200 - 95 * Math.Log10(40), SapCalculation.Impact(40), 6);
        Assert.Equal(('A', 'B', 'C', 'D', 'E', 'F', 'G'), (SapCalculation.Band(92), SapCalculation.Band(81), SapCalculation.Band(80), SapCalculation.Band(55), SapCalculation.Band(39), SapCalculation.Band(21), SapCalculation.Band(1)));
    }

    /// <summary>A two-storey house of 90 m², built to the notional home's own fabric, heated by a heat pump.</summary>
    private static SapDwelling House(double windows = 18, SapHeatSource source = SapHeatSource.HeatPump) => new()
    {
        FloorArea = 90, Volume = 90 * 2.6, Storeys = 2, LivingArea = 18, GroundFloorArea = 45, ThermalMass = 250,
        Elements = new[]
        {
            new SapElement("Walls", SapSurface.Wall, 120 - windows - 2, 0.18),
            new SapElement("Front door", SapSurface.Door, 2, 1.0),
            new SapElement("Windows south", SapSurface.Window, windows / 2, 1.2) { Orientation = 180, SolarTransmittance = 0.63 },
            new SapElement("Windows north", SapSurface.Window, windows / 2, 1.2) { Orientation = 0, SolarTransmittance = 0.63 },
            new SapElement("Ground floor", SapSurface.Floor, 45, 0.13),
            new SapElement("Loft", SapSurface.Roof, 45, 0.11)
        },
        Junctions = new[] { new SapJunction("E2", "Lintels", 12, 0.05), new SapJunction("E5", "Ground floor", 28, 0.16), new SapJunction("E16", "Corners", 20, 0.09) },
        AirPermeability = 4, Ventilation = SapVentilation.Extract, SpecificFanPower = 0.15,
        Heating = new SapHeating(source, source == SapHeatSource.HeatPump ? "Heat pump" : "Gas combi")
        {
            SpaceEfficiency = source == SapHeatSource.HeatPump ? 250 : 89, WaterEfficiency = source == SapHeatSource.HeatPump ? 250 : 87,
            ControlType = 3, Combi = source == SapHeatSource.GasBoiler
        },
        HotWater = source == SapHeatSource.HeatPump
            ? new SapHotWater(SapWaterHeater.MainSystem, 180) { DeclaredLoss = 0.85 * (0.2 + 0.051 * Math.Pow(180, 2.0 / 3)) }
            : new SapHotWater(SapWaterHeater.MainSystem, 0),
        Showers = new[] { new SapShower(8) }, Baths = 1,
        WasteWaterRecovery = 0.5,
        LightingLumens = SapCalculation.ReferenceLighting(90, new SapDwelling { FloorArea = 90 }.Occupancy),
        LightingWatts = SapCalculation.ReferenceLighting(90, new SapDwelling { FloorArea = 90 }.Occupancy) / 120
    };

    [Fact]
    public void TheWorksheetsHotWaterAndPeopleAreAppendixJs()
    {
        // 100 m² houses 2.74 people (box 42).
        Assert.Equal(2.7395, new SapDwelling { FloorArea = 100 }.Occupancy, 4);

        // One mixer shower at 8 l/min and a bath, the 125-litre target met: January's litres a day by hand.
        var home = new SapDwelling { FloorArea = 100, Showers = new[] { new SapShower(8) }, Baths = 1 };
        var n = home.Occupancy;
        var showers = (0.45 * n + 0.65) * 8 * 6 * 1.035 * (41 - 8.0) / (52 - 8.0);
        var bath = (0.13 * n + 0.19) * 73 * 1.035 * 0.95 * (42 - 8.0) / (52 - 8.0);
        var other = (9.8 * n + 14) * 0.95 * 1.10;
        Assert.Equal(showers + bath + other, SapCalculation.HotWaterDemand(home)[0], 6);

        // A shower below 8 l/min is taken at 8 (Table J4).
        Assert.Equal(SapCalculation.HotWaterDemand(home)[0], SapCalculation.HotWaterDemand(home with { Showers = new[] { new SapShower(6) } })[0], 6);

        // An electric shower takes its own power, at least 9 kW, and no hot water from the store.
        var electric = SapCalculation.Run(home with { Showers = new[] { new SapShower(0, 8.5) } }, SapPurpose.Rating);
        var uses = 0.45 * n + 0.65;
        var expected = Enumerable.Range(0, 12).Sum(m => uses * new[] { 1.035, 1.021, 1.007, 0.993, 0.979, 0.965, 0.965, 0.979, 0.993, 1.007, 1.021, 1.035 }[m] * 9 * 0.1 * SapClimate.Days[m]);
        Assert.Equal(expected, electric.ShowerElectricity, 6);
    }

    [Fact]
    public void AHomeBuiltToTheNotionalSpecificationMeetsItsTargetsExactly()
    {
        var home = House();
        var assessment = FutureHomesStandard.Assess(new SapModel(home, Array.Empty<string>()));
        _output.WriteLine(string.Join("\n", assessment.Dwelling.Lines.Select(line => $"({line.Box}) {line.What}: {line.Value:0.###} {line.Unit}")));
        Assert.Equal(assessment.TargetEmissionRate, assessment.DwellingEmissionRate, 6);
        Assert.Equal(assessment.TargetPrimaryEnergyRate, assessment.DwellingPrimaryEnergyRate, 6);
        Assert.Equal(assessment.TargetFabricEnergyEfficiency, assessment.DwellingFabricEnergyEfficiency, 6);
        Assert.True(assessment.Complies is false, "no solar array, so L3 is not met");
        Assert.All(assessment.Checks.Where(check => !check.Requirement.StartsWith("On-site")), check => Assert.True(check.Met, $"{check.Requirement}: {check.Detail}"));

        // What a heat pump home of this size comes to: a little heating and hot water at 237.5%, the rest lights and fans.
        Assert.InRange(assessment.Dwelling.SpaceHeating / home.FloorArea, 15, 45);
        Assert.InRange(assessment.DwellingEmissionRate, 1.5, 6);
        Assert.InRange(assessment.Rating.SapRating, 75, 95);
        Assert.InRange(assessment.DwellingFabricEnergyEfficiency, 20, 50);
    }

    [Fact]
    public void BetterOrWorseFabricAndSystemsMoveTheRatesTheWayTheyShould()
    {
        var home = House();
        var baseline = SapCalculation.Run(home, SapPurpose.Compliance);

        // Thicker walls and a tighter envelope use less; more glass, past a quarter of the floor, more - and the notional caps it.
        var better = home with { Elements = home.Elements.Select(e => e.Kind == SapSurface.Wall ? e with { U = 0.13 } : e).ToList(), AirPermeability = 2 };
        Assert.True(SapCalculation.Run(better, SapPurpose.Compliance).SpaceHeating < baseline.SpaceHeating);
        Assert.True(SapCalculation.Run(better, SapPurpose.FabricEfficiency).FabricEnergyEfficiency < SapCalculation.Run(home, SapPurpose.FabricEfficiency).FabricEnergyEfficiency);
        var glassy = House(windows: 40);
        var notional = FutureHomesStandard.Notional(glassy);
        Assert.Equal(0.25 * 90, notional.Elements.Where(e => e.Opening).Sum(e => e.Area), 6);
        Assert.Equal(glassy.Elements.Sum(e => e.Area), notional.Elements.Sum(e => e.Area), 6);

        // A gas combi: more carbon, and Approved Document L 2026's fuel test failed.
        var gas = House(source: SapHeatSource.GasBoiler);
        var gasAssessment = FutureHomesStandard.Assess(new SapModel(gas, Array.Empty<string>()));
        Assert.True(gasAssessment.DwellingEmissionRate > gasAssessment.TargetEmissionRate);
        Assert.False(gasAssessment.Checks.Single(check => check.Requirement.StartsWith("Heating and hot water fuel")).Met);

        // Solar panels: less carbon and primary energy, some used in the home and the rest sold; a battery keeps more.
        var solar = home with { Photovoltaics = new[] { new SapPhotovoltaic("Roof", 3, 180, 35, 1) } };
        var sunny = SapCalculation.Run(solar, SapPurpose.Compliance);
        Assert.True(sunny.EmissionRate < baseline.EmissionRate && sunny.PrimaryEnergyRate < baseline.PrimaryEnergyRate);
        Assert.Equal(0.8 * 3 * SapClimate.Annual(180, 35), sunny.PhotovoltaicGenerated, 3);
        Assert.True(sunny.PhotovoltaicUsed > 0 && sunny.PhotovoltaicExported > 0);
        Assert.True(SapCalculation.Run(solar with { Battery = 5 }, SapPurpose.Compliance).PhotovoltaicUsed > sunny.PhotovoltaicUsed);

        // L3: 0.22 kWp a m² over 40% of the 45 m² ground floor, south-east at 45° - and the notional's array capped at 40% of it over 4.5.
        var benchmark = 0.8 * 0.22 * 0.4 * 45 * SapClimate.Annual(135, 45);
        Assert.False(FutureHomesStandard.Photovoltaics(home).Met);
        Assert.True(FutureHomesStandard.Photovoltaics(solar).Met == (0.8 * 3 * SapClimate.Annual(180, 35) >= benchmark));
        var big = home with { Photovoltaics = new[] { new SapPhotovoltaic("Roof", 10, 180, 35, 1) } };
        Assert.Equal(0.4 * 45 / 4.5, FutureHomesStandard.Notional(big).Photovoltaics.Sum(array => array.PeakPower), 6);
    }

    /// <summary>A bungalow of four rooms in a row, heated by a heat pump with a cylinder, ventilated by fans in its wet rooms.</summary>
    private static (BimDocument Document, List<Room> Rooms) Bungalow()
    {
        var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "Living Room", "Kitchen", "Bathroom", "Bedroom 1");
        VentilationTests.Put(document, "Heat Pump - Air Source 7 kW", 2000, -1500);
        VentilationTests.Put(document, "Hot Water Cylinder - Unvented 210 l", rooms[1].Location.X, 1500);
        VentilationTests.Put(document, "Room Thermostat", rooms[0].Location.X + 500, 1500);
        foreach (var room in rooms) VentilationTests.Put(document, "Radiator - 600 x 1000", room.Location.X, 500);
        VentilationTests.Put(document, "Extract Fan - Continuous dMEV", rooms[1].Location.X - 500, 2500);
        VentilationTests.Put(document, "Extract Fan - Continuous dMEV", rooms[2].Location.X - 500, 2500);
        VentilationTests.Put(document, "Shower Enclosure - Corner 900 x 900", rooms[2].Location.X + 1000, 2000);
        VentilationTests.Put(document, "Bath - 1700 x 700", rooms[2].Location.X - 1000, 1000);
        var south = document.Walls.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        var casement = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200");
        document.Add(new BimWindow { TypeId = casement.Id, LevelId = rooms[0].LevelId, HostWallId = south.Id, DistanceAlongWall = rooms[0].Location.X - south.Start.X, SillHeight = 900 });
        return (document, rooms);
    }

    [Fact]
    public void AHomeIsReadOffItsModelWithItsJunctionsMeasured()
    {
        var (document, rooms) = Bungalow();
        var model = SapSurvey.Of(document);
        var home = model.Home;
        _output.WriteLine(string.Join("\n", model.Notes));
        _output.WriteLine(string.Join("\n", home.Junctions.Select(junction => $"{junction.Code} {junction.Length:0.00} m at {junction.Psi:0.00}")));

        // Four rooms of 4 m by 3 m, the living room the living area; one storey.
        Assert.Equal(48, home.FloorArea, 1);
        Assert.Equal(12, home.LivingArea, 1);
        Assert.Equal(1, home.Storeys);
        Assert.Equal(48, home.GroundFloorArea, 1);

        // What heats it and its water, how it is ventilated, its shower and bath.
        Assert.Equal(SapHeatSource.HeatPump, home.Heating.Source);
        Assert.Equal((320.0, 240.0, 7000.0), (home.Heating.SpaceEfficiency, home.Heating.WaterEfficiency, home.Heating.Output));
        Assert.Equal(2, home.Heating.ControlType);
        Assert.Equal((SapWaterHeater.MainSystem, 210.0, 1.45), (home.HotWater.Heater, home.HotWater.CylinderVolume, home.HotWater.DeclaredLoss!.Value));
        Assert.Equal((SapVentilation.Extract, 0.15, false), (home.Ventilation, home.SpecificFanPower, home.CentralExtract));
        Assert.Single(home.Showers);
        Assert.Equal(1, home.Baths);

        // Its junctions: round the window, along the ground floor and the flat roof - the envelope's inside length less the partitions - and its four corners.
        double Length(string code) => home.Junctions.Where(junction => junction.Code == code).Sum(junction => junction.Length);
        Assert.Equal((1.2, 1.2, 2.4), (Length("E2"), Length("E3"), Length("E4")));
        Assert.Equal(2 * 16 + 2 * 3, Length("E5"), 1);
        Assert.Equal(Length("E5"), Length("E14"), 6);
        var height = Length("E16") / 4;
        Assert.InRange(height, 2.4, 3.0);
        Assert.Equal(0, Length("E17"));
        Assert.All(home.Junctions, junction => Assert.Equal(SapJunctions.Reference(junction.Code), junction.Psi, 6));

        // Its glazing faces south, true north up the plan; turned so true north points right, it faces west.
        var window = home.Elements.Single(element => element.Kind == SapSurface.Window);
        Assert.Equal(180, window.Orientation, 1);
        Assert.Equal(0.55, window.SolarTransmittance);
        document.ProjectInformation.TrueNorth = 90;
        Assert.Equal(90, SapSurvey.Of(document).Home.Elements.Single(element => element.Kind == SapSurface.Window).Orientation, 1);

        // The model's own figures: a solar array on the flat roof, south at 30°, meeting L3; without it, not.
        var assessment = FutureHomesStandard.Assess(model);
        _output.WriteLine(string.Join("\n", assessment.Checks.Select(check => $"{(check.Met ? "met" : "NOT MET")} {check.Requirement}: {check.Detail}")));
        Assert.False(assessment.Checks.Single(check => check.Requirement.StartsWith("On-site")).Met);
        var roof = document.All<Roof>().Single();
        roof.SolarPeakPower = 5;
        document.ProjectInformation.TrueNorth = 0;
        var arrayed = SapSurvey.Of(document).Home;
        Assert.Equal((180.0, 30.0), (arrayed.Photovoltaics.Single().Orientation, arrayed.Photovoltaics.Single().Tilt));
        Assert.True(FutureHomesStandard.Photovoltaics(arrayed).Met);
    }

    [Fact]
    public void ARoofWindowIsCountedInTheHeatLossAtItsPitchAndTheRoofLessForIt()
    {
        // One room open to a 40° gable roof, eaves along its long sides.
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var level = document.Levels[0].Id;
        var floor = new Floor { LevelId = level, TypeId = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Screed on Slab 250mm").Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(floor);
        var room = new Room { LevelId = level, Location = new Point2D(5000, 4000), Name = "Living Room", Number = "1" };
        document.Add(room);
        var before = HeatLoss.Of(document)!.Of(room)!.Lines.Single(line => line.Kind == HeatSurface.Roof).Area;

        // A roof window in the slope that falls to the eave at y = 0 - south, true north up the plan.
        var type = document.TypesOf<RoofWindowType>().First(each => each.Name.Contains("780 x 980"));
        document.Add(new RoofWindow { RoofId = roof.Id, LevelId = level, TypeId = type.Id, Location = new Point2D(5000, 2000) });
        var lines = HeatLoss.Of(document)!.Of(room)!.Lines;
        var glass = lines.Single(line => line.Through is RoofWindow);
        Assert.Equal((HeatSurface.Window, type.HeatTransferCoefficient), (glass.Kind, glass.Bare));
        Assert.Equal(40, glass.Tilt!.Value, 1);
        Assert.Equal(180, glass.Facing!.Value, 1);
        Assert.Equal(type.Width * type.Height / 1e6, glass.Area, 6);
        Assert.Equal(before - glass.Area, lines.Single(line => line.Kind == HeatSurface.Roof).Area, 6);

        // SAP takes it as a roof window at its pitch, round it its head, sill and jambs; the walls under the eaves
        // and the gables insulated at the rafters, a gable's junction up its rake.
        var home = SapSurvey.Of(document).Home;
        var window = home.Elements.Single(element => element.Kind == SapSurface.RoofWindow);
        Assert.Equal(180, window.Orientation, 1);
        Assert.Equal(40, window.Tilt, 1);
        double Length(string code) => home.Junctions.Where(junction => junction.Code == code).Sum(junction => junction.Length);
        Assert.Equal((0.78, 0.78, 1.96), (Length("R1"), Length("R2"), Length("R3")));
        var thickness = document.TypesOf<WallType>().First(type => type.Name.StartsWith("Exterior")).Width / 1000;
        Assert.Equal(2 * (10 - thickness), Length("E11"), 2);
        Assert.True(Length("E13") > 2 * (8 - thickness) * 1.1);
        Assert.Equal(0, Length("E10") + Length("E12") + Length("E14"));
    }

    [Fact]
    public void TheEnergySettingsAndWhatTheModelGivesSapAreKeptInItsFile()
    {
        var (document, _) = Bungalow();
        var energy = document.ProjectInformation.Energy;
        energy.Overshading = SapOvershading.Heavy;
        energy.ShelteredSides = 3;
        energy.Bridging = ThermalBridging.Defaults;
        energy.YValue = 0.08;
        energy.Psi["E2"] = 0.03;
        energy.WasteWaterRecovery = 0.6;
        energy.Battery = 4.8;
        energy.Flat = true;
        var roof = document.All<Roof>().Single();
        roof.SolarPeakPower = 3.6;
        roof.SolarOvershading = PvOvershading.Modest;
        var roofWindow = document.TypesOf<RoofWindowType>().First();
        roofWindow.HeatTransferCoefficient = 1.1;
        roofWindow.SolarHeatGainCoefficient = 0.42;

        var kept = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var settings = kept.ProjectInformation.Energy;
        Assert.Equal((SapOvershading.Heavy, 3, ThermalBridging.Defaults, 0.08, 0.03, 0.6, 4.8, true),
            (settings.Overshading, settings.ShelteredSides, settings.Bridging, settings.YValue, settings.Psi["E2"], settings.WasteWaterRecovery, settings.Battery, settings.Flat));
        var keptRoof = kept.All<Roof>().Single();
        Assert.Equal((3.6, PvOvershading.Modest), (keptRoof.SolarPeakPower, keptRoof.SolarOvershading));
        var keptWindow = kept.TypesOf<RoofWindowType>().Single(type => type.Name == roofWindow.Name);
        Assert.Equal((1.1, 0.42), (keptWindow.HeatTransferCoefficient, keptWindow.SolarHeatGainCoefficient));
        var pump = kept.TypesOf<ComponentType>().Single(type => type.Name == "Heat Pump - Air Source 7 kW");
        var cylinder = kept.TypesOf<ComponentType>().Single(type => type.Name == "Hot Water Cylinder - Unvented 210 l");
        var fan = kept.TypesOf<ComponentType>().Single(type => type.Name == "Extract Fan - Continuous dMEV");
        Assert.Equal((320.0, 240.0, 210.0, 1.45, 0.15), (pump.SpaceEfficiency, pump.WaterEfficiency, cylinder.Capacity, cylinder.StandingLoss, fan.SpecificFanPower));
    }
}
