using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// A US house's heating and cooling loads by ASHRAE's residential load factor method (Fundamentals
/// 2021, chapter 17): the chapter's own worked example reproduced factor by factor, a house's loads
/// room by room, its equipment against them as Manual S sizes it, and what it is worked to kept in
/// its file.
/// </summary>
[Collection("Wpf")]
public class CoolingLoadTests
{
    [Fact]
    public void TheChaptersExampleHouseIsWorkedAsTheHandbookWorksIt()
    {
        // Table 20: the ceiling under a vented attic with a medium asphalt shingle roof, the walls, the garage wall, a door, the slab.
        const double difference = 17, range = 17;
        Assert.Equal(0.65, CoolingLoads.OpaqueFactor(0.031, difference, range, 0.62, 25.7 * CoolingLoads.Absorptance("Asphalt Shingle Roof") - 8.1, -0.19), 2);
        Assert.Equal(2.31, CoolingLoads.OpaqueFactor(0.090, difference, range, 1, 14.8, -0.36), 2);
        Assert.Equal(0.98, CoolingLoads.OpaqueFactor(0.090, difference, range, 1, 0, -0.36), 2);
        Assert.Equal(10.27, CoolingLoads.OpaqueFactor(0.40, difference, range, 1, 14.8, -0.36), 2);
        Assert.Equal(-0.74, CoolingLoads.SlabFactor(1.2), 2);

        // Table 21: windows at 35°N - a 3 ft operable one facing west under a 2 ft eave, an 8 ft fixed one, a 4 ft fixed one facing south -
        // light closed-weave drapes half closed over low-e low-solar glass; the shaded shares rounded, as the table rounds them, before they are used.
        var drapes = 1 + 0.5 * (CoolingLoads.ClosedDrapes(null, 0.31) - 1);
        Assert.Equal(0.84, drapes, 6);
        var west = CoolingLoads.ShadeLineFactor(CoolingLoads.Exposure.West, 35);
        Assert.Equal(1.1, west, 6);
        Assert.Equal(0.73, CoolingLoads.Shaded(west, 2, 0, 3), 2);
        Assert.Equal(0.28, CoolingLoads.Shaded(west, 2, 0, 8), 2);
        Assert.Equal(13.0, CoolingLoads.WindowFactor(0.39, difference, range, CoolingLoads.Exposure.West, 35, 0.73, 0.64, 0.31, drapes), 0.05);
        Assert.Equal(34.1, CoolingLoads.WindowFactor(0.35, difference, range, CoolingLoads.Exposure.West, 35, 0.28, 1, 0.36, drapes), 0.05);
        Assert.Equal(20.8, CoolingLoads.WindowFactor(0.35, difference, range, CoolingLoads.Exposure.South, 35, 0, 1, 0.36, drapes), 0.1);
        Assert.Equal((CoolingLoads.Exposure.West, CoolingLoads.Exposure.North), (CoolingLoads.ExposureOf(270), CoolingLoads.ExposureOf(350)));

        // The air: 77 sq in of leakage driven at Table 5's heating and cooling forces; a blower door's cfm at 50 Pa about 0.055 sq in each.
        Assert.Equal(1.01, ResidentialAir.DrivingForce(8, 68 - 20, cooling: false), 2);
        Assert.Equal(0.48, ResidentialAir.DrivingForce(8, 95 - 75, cooling: true), 2);
        Assert.Equal(2.80, ResidentialAir.DrivingForce(24, 68 + 40, cooling: false), 2);
        Assert.Equal(0.0549, ResidentialAir.LeakageArea(1), 4);

        // The ducts: an attic's, R-8, sealed to 5%, one storey - 0.27 cooling, 0.13 heating by a furnace (Table 6).
        Assert.Equal(0.27, ResidentialAir.DuctFactor(DuctRun.Attic, cooling: true, heatPump: false, 1, sealed_: true, 8));
        Assert.Equal(0.13, ResidentialAir.DuctFactor(DuctRun.Attic, cooling: false, heatPump: false, 1, sealed_: true, 8));
        Assert.Equal(0, ResidentialAir.DuctFactor(DuctRun.ConditionedSpace, cooling: true, heatPump: true, 2, sealed_: false, 0));

        // Table 18's moisture: 92 °F with a 74 °F wet bulb at 1027 ft, against 75 °F and 50% - 0.0050 lb a lb.
        var pressure = Psychrometrics.Pressure(1027 * 0.3048);
        var outside = Psychrometrics.FromWetBulb((92 - 32) / 1.8, (74 - 32) / 1.8, pressure);
        Assert.Equal(0.0050, outside - Psychrometrics.HumidityRatio((75 - 32) / 1.8, 0.5, pressure), 4);
    }

    /// <summary>A US house of a living room and a bedroom side by side, a south window in each, worked to a summer design day.</summary>
    private static (BimDocument Document, List<Room> Rooms) House()
    {
        var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "Living Room", "Bedroom 1");
        var project = document.ProjectInformation;
        project.ClearanceStandard = ClearanceStandard.Irc;
        project.WinterDesignTemperature = 20;
        project.SummerDesignTemperature = 95;
        project.SummerWetBulb = 75;
        project.DailyRange = 20;
        project.Latitude = 35;
        var south = document.Walls.First(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < 0);
        var type = document.TypesOf<WindowType>().First(each => each.Name == "Double Hung - 36 x 60 in");
        foreach (var room in rooms)
            document.Add(new BimWindow { TypeId = type.Id, LevelId = room.LevelId, HostWallId = south.Id, DistanceAlongWall = room.Location.X - south.Start.X, SillHeight = 900 });
        return (document, rooms);
    }

    [Fact]
    public void AUsHouseIsWorkedRoomByRoomForItsHeatingAndItsCooling()
    {
        var (document, rooms) = House();
        var cooling = CoolingLoads.Of(document)!;
        Assert.Equal(2, cooling.Rooms.Count);
        var living = cooling.Of(rooms[0])!;
        var bedroom = cooling.Of(rooms[1])!;

        // Each window's sun on the south, its walls and roof; the people and appliances in the living room, not the bedroom.
        var window = living.Lines.Single(line => line.Kind == HeatSurface.Window);
        var wall = living.Lines.First(line => line.Kind == HeatSurface.Wall);
        Assert.True(window.Factor > 5 * wall.Factor);
        Assert.Contains(living.Lines, line => line.Kind == HeatSurface.Roof);
        Assert.True(living.Internal > 0);
        Assert.Equal(0, bedroom.Internal);
        Assert.Equal(cooling.Internal, cooling.Rooms.Sum(room => room.Internal), 6);
        Assert.Equal(cooling.AirSensible, cooling.Rooms.Sum(room => room.Air), 6);
        Assert.True(cooling.Latent > 0 && cooling.Sensible > living.Sensible && cooling.Tons > 0);
        Assert.Equal(cooling.Sensible + cooling.Latent, cooling.Total, 6);

        // Turned round, the window faces north: less sun on it.
        document.ProjectInformation.TrueNorth = 180;
        Assert.True(CoolingLoads.Of(document)!.Of(rooms[0])!.Lines.Single(line => line.Kind == HeatSurface.Window).Factor < window.Factor);
        document.ProjectInformation.TrueNorth = 0;

        // Ducts in the attic add Table 6's gain, a furnace's loss in winter.
        document.ProjectInformation.DuctRun = DuctRun.Attic;
        var ducted = CoolingLoads.Of(document)!;
        Assert.Equal(0.27 * ducted.Subtotal, ducted.Distribution, 6);
        var heating = HeatLoss.Of(document)!;
        Assert.Equal(0.13 * (heating.Fabric + heating.Ventilation), heating.Distribution, 6);

        // In winter the slab loses its heat at its exposed edge, and the air comes in through the leakage area its air leakage gives.
        var edge = heating.Of(rooms[0])!.Lines.Single(line => line.Kind == HeatSurface.SlabEdge);
        Assert.True(edge.Area > 10 && edge.Area < 12);
        Assert.NotNull(heating.Air);
        Assert.Equal(heating.Air!.SensibleFlow, heating.Rooms.Sum(room => room.AirFlow), 6);

        // No summer design temperature, no cooling load; an English project's heat loss as before.
        document.ProjectInformation.SummerDesignTemperature = null;
        Assert.Null(CoolingLoads.Of(document));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        document.ProjectInformation.HeatingLocation = HeatingLocation.London;
        Assert.DoesNotContain(HeatLoss.Of(document)!.Rooms.SelectMany(room => room.Lines), line => line.Kind == HeatSurface.SlabEdge);
        Assert.Null(HeatLoss.Of(document)!.Air);
    }

    [Fact]
    public void CoolingEquipmentIsSizedAsManualSSizesIt()
    {
        var air = new ResidentialAir.Flow(0, 0, 0, 0, 0, 0, 8);
        var cooling = new BuildingCooling(35, 24, 9, 0.005, Array.Empty<RoomCooling>(), air, 3000, 1000, 0, 0, 0);
        Assert.Equal(4000, cooling.Total, 6);
        Assert.Contains("against a cooling load", HeatingCooling(3500, cooling, 2000));
        Assert.Null(HeatingCooling(4400, cooling, 2000));
        Assert.Contains("keeps it to 115%", HeatingCooling(4800, cooling, 2000));

        // Where the heating is the larger, a heat pump may be sized up to 125% of the cooling.
        Assert.Null(HeatingCooling(4800, cooling, 9000));
        Assert.Contains("keeps it to 125%", HeatingCooling(5200, cooling, 9000));
    }

    private static string? HeatingCooling(double capacity, BuildingCooling cooling, double heating) => Heating.CoolingProblem(capacity, cooling, heating);

    [Fact]
    public void WhatTheLoadsAreWorkedToIsKeptInTheFile()
    {
        var (document, _) = House();
        var project = document.ProjectInformation;
        project.TrueNorth = 30;
        project.DuctRun = DuctRun.Crawlspace;
        project.DuctInsulation = 4;
        project.DuctsSealed = false;
        var pump = document.TypesOf<ComponentType>().First(type => type.Name == "Heat Pump - Air Source 3 ton");
        Assert.Equal(36_000, pump.CoolingOutput * Units.BtuPerHourPerWatt, 3);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = reopened.ProjectInformation;
        Assert.Equal((95.0, 75.0, 20.0, 35.0, 30.0), (kept.SummerDesignTemperature, kept.SummerWetBulb, kept.DailyRange, kept.Latitude, kept.TrueNorth));
        Assert.Equal((DuctRun.Crawlspace, 4.0, false), (kept.DuctRun, kept.DuctInsulation, kept.DuctsSealed));
        Assert.Equal(pump.CoolingOutput, reopened.TypesOf<ComponentType>().First(type => type.Name == pump.Name).CoolingOutput, 6);
    }
}
