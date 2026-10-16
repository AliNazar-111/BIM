using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// The services: sockets, switches and controls at the heights and corners Approved Document M and
/// the ADA ask; radiators under sills, fans by the ceiling and lamps as efficient as Approved
/// Documents F and L ask; the extract each room needs; and in the US a light in every room and a
/// receptacle within 6 ft along every wall.
/// </summary>
public class ServicesTests
{
    private static ComponentType Type(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(type => type.Name == name);

    /// <summary>A room 4 m by 3 m inside from (0, 0), walled all round - south, east, north, west - with a door in the south wall and the room in it.</summary>
    private static (BimDocument Document, Room Room, List<Wall> Walls) Enclosed()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(4000 + half, -half), new Point2D(4000 + half, 3000 + half), new Point2D(-half, 3000 + half) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 };
            document.Add(wall);
            walls.Add(wall);
        }

        var single = document.TypesOf<DoorType>().First(type => type.Name.StartsWith("Single"));
        document.Add(new Door { TypeId = single.Id, LevelId = level, HostWallId = walls[0].Id, DistanceAlongWall = 1000 + half, Mark = "D1" });

        var room = new Room { LevelId = level, Location = new Point2D(2000, 1500), Name = "Room", Number = "1" };
        document.Add(room);
        return (document, room, walls);
    }

    /// <summary>A fitting hung on a wall at a point inside the room by it, at an elevation - its type's own by default.</summary>
    private static Component Hang(BimDocument document, Wall wall, string name, Point2D at, double? elevation = null)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id };
        Assert.True(piece.HostOn(document, wall, at));
        piece.Elevation = elevation ?? type.DefaultElevation;
        document.Add(piece);
        return piece;
    }

    private static Component Put(BimDocument document, string name, double x, double y)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = type.DefaultElevation };
        ComponentPlacing.FitToWalls(document, piece);
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void TheServicesAreInTheLibraryWhereTheyGo()
    {
        var document = BimDocument.CreateDefault();

        // A wall light with its middle at 1700 or so, not 2 m up.
        var light = Type(document, "Wall Light");
        Assert.InRange(light.DefaultElevation + light.Height / 2, 1600, 1800);
        Assert.True(light.Efficacy >= 75);

        // Fans within 400 of a 2400 ceiling, with their rates; a hood's too.
        foreach (var (name, rate) in new[] { ("Extract Fan - 100 (15 l/s)", 15.0), ("Extract Fan - 150 (60 l/s)", 60) })
        {
            var fan = Type(document, name);
            Assert.Equal(rate, fan.ExtractRate);
            Assert.InRange(2400 - (fan.DefaultElevation + fan.Height), 0, 400);
        }

        Assert.True(Type(document, "Cooker Hood - Chimney 600").ExtractRate >= 30);

        // The alarms hang from the ceiling; the consumer unit's switches at 1400, the thermostat in 900 to 1200.
        foreach (var name in new[] { "Smoke Alarm", "Heat Alarm", "Carbon Monoxide Alarm" })
        {
            Assert.True(ComponentModel.HangsFromCeiling(Type(document, name)));
            Assert.Equal(BuiltInCategory.FireAlarmDevices, Type(document, name).Kind);
        }

        var board = Type(document, "Consumer Unit - 12 Way");
        Assert.Equal(1400, board.DefaultElevation + board.Height / 2);
        var thermostat = Type(document, "Room Thermostat");
        Assert.InRange(thermostat.DefaultElevation + thermostat.Height / 2, 900, 1200);
        Assert.Equal(ComponentForm.TowelRail, Type(document, "Towel Rail - 500 x 1200").Form);
    }

    [Fact]
    public void SocketsAndSwitchesAreKeptAtTheHeightsAndFromTheCornersTheRulesAsk()
    {
        var (document, _, walls) = Enclosed();
        var standard = ClearanceStandard.ApprovedDocumentM;

        // A socket at 450 in the middle of the north wall: right for M4(2), too low for a wheelchair user, fine for the ADA.
        var socket = Hang(document, walls[2], "Socket - Double", new Point2D(2000, 2900));
        Assert.Empty(Services.Problems(document, socket, standard));
        Assert.Contains(Services.Problems(document, socket, ClearanceStandard.ApprovedDocumentM3), problem => problem.Said == "its middle is 493 above the floor.");
        Assert.Empty(Services.Problems(document, socket, ClearanceStandard.Ada));

        // Down at 150, too low.
        socket.Elevation = 150;
        Assert.Contains(Services.Problems(document, socket, standard), problem => problem.Rule.Contains("2.30b"));

        // Tucked into the corner, 100 from the east wall.
        var cornered = Hang(document, walls[2], "Switch - One Gang", new Point2D(4000 - 100 - 43, 2900));
        Assert.Equal(100, Services.FromCorner(document, cornered, Type(document, "Switch - One Gang"))!.Value, 0);
        Assert.Contains(Services.Problems(document, cornered, standard), problem => problem.Said == "it is 100 from an inside corner.");

        // The consumer unit's switches must be 1350 to 1450 up.
        var board = Hang(document, walls[3], "Consumer Unit - 12 Way", new Point2D(100, 1500), 900);
        Assert.Contains(Services.Problems(document, board, standard), problem => problem.Rule.Contains("1350 to 1450"));
    }

    [Fact]
    public void ARadiatorWantsRoomUnderTheSillAFanTheCeilingAndALampItsLumens()
    {
        var (document, _, walls) = Enclosed();
        var standard = ClearanceStandard.ApprovedDocumentM;

        // A window in the north wall, its sill at 900; a radiator under it, its top at 850 - then at 900.
        var window = new Window
        {
            TypeId = document.TypesOf<WindowType>().First(type => type.Width >= 1200 && type.Operation != WindowOperation.Fixed).Id,
            LevelId = walls[2].LevelId, HostWallId = walls[2].Id, DistanceAlongWall = 2100, SillHeight = 900
        };
        document.Add(window);
        var centre = walls[2].PointAt(document.GetWallType(walls[2])!.Structure, 2100, 0);
        var radiator = Hang(document, walls[2], "Radiator - 600 x 1000", new Point2D(centre.X, 2900), 200);
        Assert.Empty(Services.Problems(document, radiator, standard));
        radiator.Elevation = 250;
        Assert.Contains(Services.Problems(document, radiator, standard), problem => problem.Said == "its top is 0 under the sill over it.");

        // A fan under a 2400 ceiling, its top 750 under it.
        var ceiling = new Ceiling { LevelId = walls[0].LevelId, TypeId = document.TypesOf<CeilingType>().First().Id, HeightOffset = 2400 };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 3000), new Point2D(0, 3000) });
        document.Add(ceiling);
        var fan = Hang(document, walls[3], "Extract Fan - 100 (15 l/s)", new Point2D(100, 2000), 1500);
        var under = ceiling.GetBottomElevation(document) - (1500 + 150);
        Assert.Contains(Services.Problems(document, fan, standard), problem => problem.Said == $"its top is {under:0} under the ceiling.");
        fan.Elevation = ceiling.GetBottomElevation(document) - 150 - 100;
        Assert.Empty(Services.Problems(document, fan, standard));

        // A light with old lamps.
        var dim = Type(document, "Ceiling Light - Pendant").Duplicate("Pendant - Halogen");
        dim.Efficacy = 20;
        document.AddType(dim);
        var pendant = new Component { TypeId = dim.Id, TypeKind = dim.Kind, LevelId = walls[0].LevelId, Location = new Point2D(2000, 1500), Elevation = 1700 };
        document.Add(pendant);
        Assert.Contains(Services.Problems(document, pendant, standard), problem => problem.Rule.Contains("6.58"));
    }

    [Fact]
    public void EachRoomHasTheExtractItNeeds()
    {
        // A bathroom: 15 l/s, met by a fan.
        var (bathroom, room, walls) = Enclosed();
        Put(bathroom, "Bath - 1700 x 700", 2000, 2600);
        Assert.Equal("Services: extract 0 l/s - Approved Document F (Table 1.1) asks for 15 l/s from a bathroom", Services.Verdict(bathroom, room));
        Hang(bathroom, walls[3], "Extract Fan - 100 (15 l/s)", new Point2D(100, 2000));
        Assert.Equal("Services: extract 15 l/s", Services.Verdict(bathroom, room));
        Assert.Contains(room.GetInstanceParameters(bathroom), parameter => parameter.Name == "Services");

        // A kitchen with no hood: 60 l/s - a hood makes it 30.
        var (kitchen, kitchenRoom, kitchenWalls) = Enclosed();
        Put(kitchen, "Oven and Hob - 600", 2000, 2600);
        Put(kitchen, "Sink Unit - 1000", 2800, 2600);
        Hang(kitchen, kitchenWalls[3], "Extract Fan - 100 (15 l/s)", new Point2D(100, 2000));
        Assert.Contains("60 l/s from a kitchen with no cooker hood", Services.Verdict(kitchen, kitchenRoom));
        Hang(kitchen, kitchenWalls[2], "Cooker Hood - Chimney 600", new Point2D(2000, 2900), 1600);
        Assert.Equal("Services: extract 125 l/s", Services.Verdict(kitchen, kitchenRoom));

        // A WC with a window that opens needs no fan.
        var (wc, wcRoom, wcWalls) = Enclosed();
        Put(wc, "WC - Close Coupled", 2000, 2600);
        Assert.Contains("6 l/s from a WC", Services.Verdict(wc, wcRoom));
        wc.Add(new Window { TypeId = wc.TypesOf<WindowType>().First(type => type.Operation == WindowOperation.Casement).Id, LevelId = wcWalls[3].LevelId, HostWallId = wcWalls[3].Id, DistanceAlongWall = 1500, SillHeight = 1000 });
        Assert.Equal("Services: a window to open", Services.Verdict(wc, wcRoom));
    }

    [Fact]
    public void InTheUsEveryRoomHasALightAndAReceptacleWithin6FtAlongItsWalls()
    {
        var (document, room, walls) = Enclosed();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;

        Assert.Equal(1, Services.ShortWallSpaces(document, room, room.GetBoundary(document).Polygon));
        var verdict = Services.Verdict(document, room)!;
        Assert.Contains("NEC 210.70", verdict);
        Assert.Contains("1 wall space with a point more than 6 ft (1829) from a receptacle", verdict);

        // Receptacles round the walls, none more than 12 ft apart along them, and a light.
        Hang(document, walls[0], "Socket - Double", new Point2D(3000, 100), 300);
        Hang(document, walls[1], "Socket - Double", new Point2D(3900, 1500), 300);
        Hang(document, walls[2], "Socket - Double", new Point2D(2000, 2900), 300);
        Hang(document, walls[3], "Socket - Double", new Point2D(100, 1500), 300);
        Hang(document, walls[3], "Socket - Double", new Point2D(100, 500), 300);
        document.Add(new Component { TypeId = Type(document, "Ceiling Light - Flush").Id, TypeKind = BuiltInCategory.LightingFixtures, LevelId = walls[0].LevelId, Location = new Point2D(2000, 1500), Elevation = 2300 });

        Assert.Equal(0, Services.ShortWallSpaces(document, room, room.GetBoundary(document).Polygon));
        Assert.Null(Services.Verdict(document, room));
    }

    /// <summary>An alarm on the ceiling of the room's storey, at a point.</summary>
    private static Component Alarm(BimDocument document, string name, double x, double y) =>
        new Component { TypeId = Type(document, name).Id, TypeKind = BuiltInCategory.FireAlarmDevices, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = 2355 };

    [Fact]
    public void ASmokeAlarmIsWantedOnEveryStoreyAndNearEveryBedroomsDoor()
    {
        var (document, room, _) = Enclosed();
        Put(document, "Bed - Single 900 x 1900", 2000, 1500);
        Assert.Contains("no smoke alarm on this storey", Alarms.Verdict(document, room));
        Assert.Contains(room.GetInstanceParameters(document), parameter => parameter.Name == "Alarms");

        // In the hall outside, 2 m from its door - then 5 m.
        var alarm = Alarm(document, "Smoke Alarm", 1000, -2000);
        document.Add(alarm);
        Assert.Null(Alarms.Verdict(document, room));
        alarm.Location = new Point2D(1000, -5000);
        Assert.Contains("BS 5839-6 asks for one within 3 m of a bedroom's door", Alarms.Verdict(document, room));

        // Under the IRC, one in the bedroom as well.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        alarm.Location = new Point2D(1000, -1500);
        Assert.Contains("no smoke alarm in this bedroom", Alarms.Verdict(document, room));
    }

    [Fact]
    public void AnAlarmIsKeptFromTheWallsAndOutOfTheKitchenAndACarbonMonoxideAlarmIsByTheBoiler()
    {
        var (document, room, walls) = Enclosed();
        var standard = ClearanceStandard.ApprovedDocumentM;

        // Against the wall, and in a room with a cooker.
        var tight = Alarm(document, "Smoke Alarm", 150, 1500);
        document.Add(tight);
        Assert.Contains(Alarms.Problems(document, tight, standard), problem => problem.Said.StartsWith("it is ") && problem.Said.EndsWith("from the wall."));
        Put(document, "Oven and Hob - 600", 2000, 2600);
        Assert.Contains(Alarms.Problems(document, tight, standard), problem => problem.Rule.Contains("out of kitchens and bathrooms"));

        // A boiler wants a carbon monoxide alarm in its room, 1 to 3 m from it.
        Hang(document, walls[2], "Boiler - Gas Combi", new Point2D(3200, 2900));
        Assert.Contains("no carbon monoxide alarm by Boiler - Gas Combi - Approved Document J", Alarms.Verdict(document, room));
        var co = Alarm(document, "Carbon Monoxide Alarm", 3200, 1200);
        document.Add(co);
        Assert.DoesNotContain("carbon monoxide", Alarms.Verdict(document, room) ?? string.Empty);
        Assert.Empty(Alarms.Problems(document, co, standard));
        co.Location = new Point2D(3200, 2350);
        Assert.Contains(Alarms.Problems(document, co, standard), problem => problem.Rule.Contains("1 to 3 m"));

        // Under the IRC, a smoke alarm 6 ft from the cooker.
        var nearCooker = Alarm(document, "Smoke Alarm", 2000, 1500);
        document.Add(nearCooker);
        Assert.Contains(Alarms.Problems(document, nearCooker, ClearanceStandard.Irc), problem => problem.Rule.Contains("R314.3.1"));
    }
}
