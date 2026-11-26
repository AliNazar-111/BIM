using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Smoke control in a block of flats (round 93), by Approved Document B Volume 1: the corridors next to the stair vented (3.50, 3.51a), the
/// stair's head (3.52), one stair's vents opened on smoke (3.53); a small single stair building's (3.28e, Diagram 3.9 note 3); and smoke
/// outlets from a basement (Section 16). A smoke vent's free area by Appendix D5.
/// </summary>
[Collection("Wpf")]
public class SmokeControlTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Single - 900 x 2100").Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y, string dwelling = "")
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name, Dwelling = dwelling };
        document.Add(room);
        return room;
    }

    /// <summary>A block's storey: its outside walls (the south first, then east, north and west), and a door from the stair into the corridor beside it.</summary>
    internal sealed record Storey(Level Level, List<Wall> Outside, Room Stair, Room Corridor, Room Flat, Room? EastStair);

    /// <summary>
    /// A block of flats 12 m by 6 m - 15 m with a second stair - on so many storeys 3 m apart. On each: the stair in its enclosure at the west
    /// end, x 0 to 3 m, a door from it at 1 m into the corridor along the south side, y 0 to 2 m; a flat north of the corridor entered from it
    /// at x 8 m; with a second stair, another enclosure at the east end, x 12 to 15 m, its door from the corridor at 1 m. Each stair rises north
    /// from each storey to the next.
    /// </summary>
    internal static (BimDocument Document, List<Storey> Storeys) Block(int storeys, bool twoStairs = false, double basement = double.NaN)
    {
        var document = BimDocument.CreateDefault();
        var levels = new List<Level> { document.Levels[0], document.Levels[1] };
        for (var i = 2; i < storeys; i++)
        {
            var level = new Level { Name = $"Level {i}", Elevation = 3000 * i };
            document.AddLevel(level);
            levels.Add(level);
        }

        var east = twoStairs ? 15000 : 12000;
        var built = new List<Storey>();
        foreach (var level in levels.Take(storeys))
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(east, 0), new Point2D(east, 6000), new Point2D(0, 6000) };
            var outside = Enumerable.Range(0, 4).Select(i => Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick)).ToList();
            Door(document, Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 6000)), 1000);
            Door(document, Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(12000, 2000)), 5000);
            Room? eastStair = null;
            if (twoStairs)
            {
                Door(document, Wall(document, level.Id, new Point2D(12000, 0), new Point2D(12000, 6000)), 1000);
                eastStair = Room(document, level.Id, "East Stair", 13500, 3000);
            }

            var index = built.Count;
            built.Add(new Storey(level, outside, Room(document, level.Id, "Stair", 1500, 3000), Room(document, level.Id, "Corridor", 7500, 1000),
                Room(document, level.Id, "Living Room", 7500, 4000, $"Flat {index + 1}"), eastStair));
        }

        var stairType = document.TypesOf<StairType>().First().Id;
        for (var i = 0; i + 1 < built.Count; i++)
        {
            document.Add(new Stair { TypeId = stairType, LevelId = built[i].Level.Id, TopLevelId = built[i + 1].Level.Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
            if (twoStairs) document.Add(new Stair { TypeId = stairType, LevelId = built[i].Level.Id, TopLevelId = built[i + 1].Level.Id, Start = new Point2D(13500, 600), Angle = 90, Width = 900 });
        }

        return (document, built);
    }

    /// <summary>A smoke vent window type made from the library's casement, opened as it says.</summary>
    private static WindowType Vent(BimDocument document, SmokeVentKind kind, double area = 0)
    {
        var vent = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").Duplicate($"Smoke Vent {document.TypesOf<WindowType>().Count()}");
        vent.SmokeVent = kind;
        vent.SmokeVentArea = area;
        document.AddType(vent);
        return vent;
    }

    private static Window Window(BimDocument document, Wall wall, WindowType type, double along, double sill)
    {
        var window = new Window { HostWallId = wall.Id, TypeId = type.Id, LevelId = wall.LevelId, DistanceAlongWall = along, SillHeight = sill };
        document.Add(window);
        return window;
    }

    private static Component Place(BimDocument document, string type, Guid level, double x, double y, double? elevation = null)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = level, Location = new Point2D(x, y), Elevation = elevation ?? kind.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    internal static List<EscapeProblem> Smoke(BimDocument document) =>
        Escape.Problems(document).Where(problem => problem.Said.Contains("vent") || problem.Said.Contains("smoke")).ToList();

    [Fact]
    public void AGeometricFreeAreaIsTheLeastTheAirPassesThrough()
    {
        // A casement opened square: its whole opening. A top-hung light 1 m across and 0.6 m deep opened 30 degrees: the gap round it, the
        // chord 2 x 0.6 sin 15 = 0.3106 m along its free edge and two triangles of 0.6² sin 30 / 2 - 0.4906 m², under its 0.6 m².
        Assert.Equal(1.1664, SmokeVents.Geometric(1.08, 1.08, 90), 4);
        Assert.Equal(1 * 2 * 0.6 * Math.Sin(15 * Math.PI / 180) + 0.36 * 0.5, SmokeVents.Geometric(1, 0.6, 30), 6);
        Assert.Equal(0.4906, SmokeVents.Geometric(1, 0.6, 30), 4);

        // The library's 1200 casement as a vent: its light 1080 square inside the frame, 1.17 m²; its declared area where one is given.
        var (document, storeys) = Block(2);
        var window = Window(document, storeys[0].Outside[0], Vent(document, SmokeVentKind.Manual), 10000, 900);
        Assert.Equal(1.1664, SmokeVents.Of(document, window)!.FreeArea, 4);
        document.FindType<WindowType>(window.TypeId)!.SmokeVentArea = 1.6;
        Assert.Equal(1.6, SmokeVents.Of(document, window)!.FreeArea, 6);
        Assert.Equal(900, SmokeVents.Of(document, window)!.Bottom, 1);
        Assert.Equal(2100, SmokeVents.Of(document, window)!.Top, 1);
    }

    [Fact]
    public void TheCorridorsOfASingleStairBlockAreVentedOnSmoke()
    {
        // Five storeys, the top 12 m up: not a small building (3.28a). No vents - each corridor next to the stair, and the stair's top storey.
        var (document, storeys) = Block(5);
        var said = Smoke(document);
        Assert.Equal(6, said.Count);
        Assert.Equal(5, said.Count(problem => problem.Said.StartsWith("it is next to a common stair with no smoke vent - Approved Document B (3.50, 3.51a)", StringComparison.Ordinal)));
        Assert.Equal("it is the top storey of a common stair, and no smoke vent - a window or roof window whose type is one - Approved Document B (3.52) asks a vent to the outside of 1 m² free area at least from it, with only one stair opened by smoke detectors in the common parts (3.53)",
            Assert.Single(said, problem => problem.Room == storeys[4].Stair).Said);

        // A vent opened by hand on the third storey's corridor, 1.17 m² by its light: short of 1.5 m², and not opened on smoke.
        var third = storeys[2];
        var vent = Vent(document, SmokeVentKind.Manual);
        var window = Window(document, third.Outside[0], vent, 10000, 900);
        Assert.Equal($"its smoke vent, the {vent.Name} window, falls short of Approved Document B: 1.17 m² of free area, where 3.51a asks 1.5 m²; it is opened by hand, where with only one stair 3.53 asks it opened by smoke detectors in the common parts",
            Assert.Single(Smoke(document), problem => problem.Room == third.Corridor).Said);

        // Declared 1.6 m², an automatic opening vent: wanting only a smoke detector in the corridor to open it - then met.
        (vent.SmokeVentArea, vent.SmokeVent) = (1.6, SmokeVentKind.Automatic);
        Assert.StartsWith("its smoke vent opens on smoke, but no smoke detector in it - Approved Document B (3.53)", Assert.Single(Smoke(document), problem => problem.Room == third.Corridor).Said);
        Place(document, document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.SmokeAlarm).Name, third.Level.Id, 6000, 1000);
        Assert.DoesNotContain(Smoke(document), problem => problem.Room == third.Corridor);

        // Its sill dropped to 600: its top at 1800, under the door to the stair's 2100 (3.50b).
        window.SillHeight = 600;
        Assert.Equal($"its smoke vent, the {vent.Name} window, falls short of Approved Document B: its top 1800 mm up, under the top of the door to the stair at 2100 mm (3.50b)",
            Assert.Single(Smoke(document), problem => problem.Room == third.Corridor).Said);
        window.SillHeight = 900;

        // The top of the stair: a 1.0 m² vent opened by hand there, then on smoke.
        var head = Vent(document, SmokeVentKind.Manual, 1.0);
        Window(document, storeys[4].Outside[3], head, 3000, 900);
        Assert.Contains($"its smoke vent, the {head.Name} window, is opened by hand", Assert.Single(Smoke(document), problem => problem.Room == storeys[4].Stair).Said);
        head.SmokeVent = SmokeVentKind.Automatic;
        Assert.DoesNotContain(Smoke(document), problem => problem.Room == storeys[4].Stair);

        // A window that is not a smoke vent does not count; nor one into another room - the corridor's partition with the flat.
        Assert.Equal(4, Smoke(document).Count);
    }

    [Fact]
    public void WithTwoStairsTheVentsMayBeOpenedByHand()
    {
        // Five storeys, a stair at each end: each corridor's vent opened by hand, 1.6 m² - met; the heads of both stairs wanted (3.52).
        var (document, storeys) = Block(5, twoStairs: true);
        var vent = Vent(document, SmokeVentKind.Manual, 1.6);
        foreach (var storey in storeys) Window(document, storey.Outside[0], vent, 7500, 900);
        var said = Smoke(document);
        Assert.Equal(2, said.Count);
        Assert.Contains(said, problem => problem.Room == storeys[4].Stair);
        Assert.Contains(said, problem => problem.Room == storeys[4].EastStair);

        // A roof window over each stair's top, a vent of 1.0 m² opened by hand: met, two stairs asking no detection.
        var roofType = document.TypesOf<RoofWindowType>().First().Duplicate("Smoke Vent Rooflight");
        (roofType.SmokeVent, roofType.SmokeVentArea) = (SmokeVentKind.Manual, 1.0);
        document.AddType(roofType);
        var roof = new Roof { LevelId = storeys[4].Level.Id, TypeId = document.TypesOf<RoofType>().First().Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(15000, 0), new Point2D(15000, 6000), new Point2D(0, 6000) });
        document.Add(roof);
        foreach (var x in new[] { 1500.0, 13500.0 })
            document.Add(new RoofWindow { TypeId = roofType.Id, LevelId = roof.LevelId, RoofId = roof.Id, Location = new Point2D(x, 3000) });
        Assert.Empty(Smoke(document));
    }

    [Fact]
    public void ASmallSingleStairBuildingHasAVentOnEachStoreyOrOneAtItsHead()
    {
        // Three storeys, the top 6 m up: Diagram 3.9's building. Nothing at all.
        var (document, storeys) = Block(3);
        Assert.Equal("the stair has no vent opened from the fire and rescue service's access level at its head, and no storey of it an openable vent at high level - Approved Document B (3.28e) asks a small single stair building one of them, each vent 1 m² of free area at least",
            Assert.Single(Smoke(document)).Said);

        // A vent of 1 m² at the head of the stair opened from a switch: met (3.28e ii).
        var head = Vent(document, SmokeVentKind.Remote, 1.0);
        Window(document, storeys[2].Outside[3], head, 3000, 900);
        Assert.Empty(Smoke(document));

        // Opened only by hand: the vent on each storey wanted (3.28e i) - the top storey has it.
        head.SmokeVent = SmokeVentKind.Manual;
        Assert.Contains($"and {storeys[0].Level.Name}, {storeys[1].Level.Name} of it an openable vent at high level", Assert.Single(Smoke(document)).Said);
        foreach (var storey in storeys.Take(2)) Window(document, storey.Outside[0], head, 10000, 900);
        Assert.Empty(Smoke(document));

        // The first floor flat's door straight onto the stair, no lobby: an automatic vent at the top, and a smoke detector in the stair there.
        Door(document, document.All<Wall>().First(wall => wall.LevelId == storeys[1].Level.Id && wall.Start == new Point2D(3000, 0)), 4000);
        var said = Smoke(document);
        Assert.Equal(2, said.Count);
        Assert.Equal($"flats open straight onto the stair, no lobby between, and its smoke vent, the {head.Name} window, is opened by hand - Approved Document B (Diagram 3.9, note 3) then asks an automatic opening vent of 1 m² free area at least at the top of the stair, opened on detecting smoke at any storey in it",
            Assert.Single(said, problem => problem.Room == storeys[2].Stair).Said);
        Assert.StartsWith("a flat opens onto the stair here, and no smoke detector in it", Assert.Single(said, problem => problem.Room == storeys[1].Stair).Said);
    }

    [Fact]
    public void ABasementHasSmokeOutletsToTheOpenAir()
    {
        // Under a three storey block, a basement 3.5 m down: the stair, a bike store and a boiler room inside 330 mm walls and 125 mm partitions -
        // 2.7725 x 5.67 + 4.875 x 5.67 + 3.7725 x 5.67 = 64.75 m², 1.62 m² of outlets asked (16.6).
        var (document, storeys) = Block(3);
        var level = new Level { Name = "Basement", Elevation = -3500 };
        document.AddLevel(level);
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) };
        var outside = Enumerable.Range(0, 4).Select(i => Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick)).ToList();
        Door(document, Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 6000)), 1000);
        Door(document, Wall(document, level.Id, new Point2D(8000, 0), new Point2D(8000, 6000)), 3000);
        Room(document, level.Id, "Stair", 1500, 3000);
        var bikes = Room(document, level.Id, "Bike Store", 5500, 3000);
        var boiler = Room(document, level.Id, "Boiler Room", 10000, 3000);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = level.Id, TopLevelId = storeys[0].Level.Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
        List<EscapeProblem> Section16() => Escape.Problems(document).Where(problem => problem.Said.Contains("Section 16") || problem.Said.Contains("(16.")).ToList();

        var said = Section16();
        Assert.Equal(3, said.Count);
        Assert.StartsWith("it is in a basement with no smoke outlet in it, nor a door into a room with one - Approved Document B (16.2)", said.Single(problem => problem.Room == bikes && problem.Said.Contains("(16.2)")).Said);
        Assert.StartsWith("it is a place of special fire hazard in a basement with no smoke outlet of its own - Approved Document B (16.7)", said.Single(problem => problem.Room == boiler).Said);
        Assert.Equal("the basement storey's smoke outlets give 0.00 m² clear between them - Approved Document B (16.6) asks 1/40 of its 65 m² floor, 1.62 m², its floor 3.5 m below the ground",
            said.Single(problem => problem.Said.Contains("(16.6)")).Said);

        // A stallboard grille at high level against the bike store's south wall: it is vented; 0.40 m² of 1.62.
        var grille = Place(document, "Smoke Outlet - Stallboard Grille 1200 x 450", level.Id, 5500, 240);
        Assert.DoesNotContain(Section16(), problem => problem.Room == bikes && problem.Said.Contains("(16.2)"));
        Assert.Contains("give 0.40 m² clear", Section16().Single(problem => problem.Said.Contains("(16.6)")).Said);

        // A pavement light in the middle of the boiler room - under the ground floor's rooms, reaching no open air; against its east wall, it does.
        var light = Place(document, "Smoke Outlet - Pavement Light 900 x 900", level.Id, 10000, 3000);
        Assert.Contains("its smoke outlet, the Smoke Outlet - Pavement Light 900 x 900: it reaches no open air", Section16().Single(problem => problem.Room == boiler && problem.Said.StartsWith("its smoke outlet", StringComparison.Ordinal)).Said);
        light.Location = new Point2D(11300, 3000);
        Assert.DoesNotContain(Section16(), problem => problem.Room == boiler);

        // The grille's type 1.2 m² clear: 1.8 m² between them, over 1.62 - met. Set low in the wall, not at high level (16.5a).
        document.FindType<ComponentType>(grille.TypeId)!.Capacity = 1.2;
        Assert.Empty(Section16());
        grille.Elevation = 500;
        Assert.Contains("it is low, its bottom 500 mm up a room 3000 mm high", Section16().Single(problem => problem.Room == bikes && problem.Said.Contains("(Section 16)")).Said);
        grille.Elevation = 2000;

        // Saved and opened again: the outlets and the vent types as they were.
        var vent = Vent(document, SmokeVentKind.Automatic, 1.6);
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        var back = opened.TypesOf<WindowType>().Single(type => type.Name == vent.Name);
        Assert.Equal((SmokeVentKind.Automatic, 1.6), (back.SmokeVent, back.SmokeVentArea));
        Assert.Equal(SmokeVentKind.None, opened.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").SmokeVent);
        Assert.Equal(1.2, opened.TypesOf<ComponentType>().Single(type => type.Name == "Smoke Outlet - Stallboard Grille 1200 x 450").Capacity, 6);
        Assert.Empty(Escape.Problems(opened).Where(problem => problem.Said.Contains("(16.")));

        // In IFC, a louvred air terminal.
        using var model = BIMDesigner.Infrastructure.Interoperability.IfcExport.Build(document);
        Assert.Equal(2, model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcAirTerminal>().Count(terminal => terminal.PredefinedType == Xbim.Ifc4.Interfaces.IfcAirTerminalTypeEnum.LOUVRE && terminal.ObjectType.ToString() == "Basement smoke outlet"));

        // 2.5 m down and under 200 m²: none asked (16.3).
        level.Elevation = -2500;
        Assert.Empty(Section16());
    }
}
