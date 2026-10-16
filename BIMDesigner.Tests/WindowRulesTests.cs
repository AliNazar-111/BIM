using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Windows as the rules see them: the opening each gives, an escape window where one is asked
/// for, the air a room is purged with and the light it has, safety glass low and by doors, and the
/// heat a window lets out.
/// </summary>
public class WindowRulesTests
{
    private static WindowType Type(BimDocument document, string name) =>
        document.TypesOf<WindowType>().Single(type => type.Name == name);

    /// <summary>A room 4 m by 3 m inside from (0, 0) on a storey, its walls outside walls - south, east, north, west - and the room.</summary>
    private static (BimDocument Document, Room Room, List<Wall> Walls) Room(int storey = 1)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[storey].Id;
        var wallType = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var half = wallType.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(4000 + half, -half), new Point2D(4000 + half, 3000 + half), new Point2D(-half, 3000 + half) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2700 };
            document.Add(wall);
            walls.Add(wall);
        }

        var room = new Room { LevelId = level, Location = new Point2D(2000, 1500), Name = "Bedroom", Number = "1" };
        document.Add(room);
        return (document, room, walls);
    }

    private static Window Put(BimDocument document, Wall wall, string type, double along, double sill, string mark = "W1")
    {
        var window = new Window { TypeId = Type(document, type).Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = along, SillHeight = sill, Mark = mark };
        document.Add(window);
        return window;
    }

    [Fact]
    public void EachWindowOpensAsItIsHung()
    {
        var document = BimDocument.CreateDefault();
        Assert.Equal((1080.0, 1080.0, true, true), WindowRules.Opening(Type(document, "Casement - 1200 x 1200")));
        Assert.Equal((810.0, 1230.0, true, true), WindowRules.Opening(Type(document, "Casement Double - 1800 x 1350")));
        Assert.Equal((810.0, 1080.0, true, true), WindowRules.Opening(Type(document, "Sliding - 1800 x 1200")));
        Assert.Equal((780.0, 660.0, true, true), WindowRules.Opening(Type(document, "Double Hung - 900 x 1500")));
        Assert.False(WindowRules.Opening(Type(document, "Awning - 1200 x 600")).Fully);
        Assert.False(WindowRules.Opening(Type(document, "Fixed - 2400 x 1500")).Opens);
    }

    [Fact]
    public void AnUpstairsBedroomWantsAnEscapeWindow()
    {
        var (document, room, walls) = Room();

        // A top-hung light does not open far enough to climb out of.
        var window = Put(document, walls[2], "Awning - 1200 x 600", 2000, 1400);
        Assert.Contains("no escape window - Approved Document B (2.2, 2.10)", WindowRules.Verdict(document, room));
        Assert.Contains(room.GetInstanceParameters(document), parameter => parameter.Name == "Windows");

        // A casement does - but not with its bottom more than 1100 up.
        window.TypeId = Type(document, "Casement - 1200 x 1200").Id;
        window.SillHeight = 900;
        Assert.Equal("Windows: an escape window", WindowRules.Verdict(document, room));
        window.SillHeight = 1100;
        Assert.Contains("no escape window", WindowRules.Verdict(document, room));

        // Under the IRC, every bedroom: a bed in it makes it one.
        window.SillHeight = 900;
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.DoesNotContain("escape", WindowRules.Verdict(document, room) ?? string.Empty);
        document.Add(new Component { TypeId = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.Bed).Id, LevelId = room.LevelId, Location = new Point2D(2000, 1500) });
        Assert.Contains("an escape window", WindowRules.Verdict(document, room));
    }

    [Fact]
    public void ATopHungRoofWindowLowEnoughIsALoftRoomsEscape()
    {
        // A room up in the roof, its slopes coming down to 500 over its floor.
        var (document, room, _) = Room();
        var floor = document.FindLevel(room.LevelId)!.Elevation;
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = room.LevelId, HeightOffset = 500 };
        roof.SetBoundary(new[] { new Point2D(-200, -200), new Point2D(4200, -200), new Point2D(4200, 3200), new Point2D(-200, 3200) });
        roof.SetEdges(new[] { new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge() });
        document.Add(roof);
        Assert.Contains("no escape window", WindowRules.Verdict(document, room));

        // A top-hung escape window low in the slope: its bottom under 1100 over the floor.
        var window = new RoofWindow
        {
            RoofId = roof.Id, LevelId = roof.LevelId, Location = new Point2D(2000, 450),
            TypeId = document.TypesOf<RoofWindowType>().Single(type => type.Name == "Roof Window - Top Hung Escape 1140 x 1180").Id
        };
        document.Add(window);
        var frame = RoofWindows.Frame(document, window)!;
        Assert.InRange(frame.Corners.Min(frame.Top) - floor, 500, 1100);
        Assert.Contains("an escape window", WindowRules.Verdict(document, room));

        // A centre-pivot one does not stay open to climb out through.
        window.TypeId = document.TypesOf<RoofWindowType>().First(type => type.Operation == RoofWindowOperation.CentrePivot).Id;
        Assert.Contains("no escape window", WindowRules.Verdict(document, room));
    }

    [Fact]
    public void ARoomOpensATwentiethOfItsFloorToPurgeItsAir()
    {
        var (document, room, walls) = Room(storey: 0);

        // A small bottom-hung light opening part way: short of a tenth of 12 m².
        Put(document, walls[2], "Hopper - 900 x 600", 2000, 1500);
        Assert.Contains("Approved Document F (Table 1.4) asks for a twentieth of its floor", WindowRules.Verdict(document, room));

        // A casement besides: plenty.
        Put(document, walls[0], "Casement - 1200 x 1200", 2000, 900, "W2");
        Assert.Null(WindowRules.Verdict(document, room));

        // Under the IRC: 8% glass, 4% open.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Null(WindowRules.Verdict(document, room));
    }

    [Fact]
    public void LowGlassAndGlassByADoorAreSafetyGlass()
    {
        var (document, _, walls) = Room(storey: 0);
        var standard = ClearanceStandard.ApprovedDocumentM;

        var low = Put(document, walls[2], "Casement - 1200 x 1200", 1000, 300);
        Assert.Contains(WindowRules.Problems(document, low, standard), problem => problem.Rule.Contains("below 800"));
        low.SillHeight = 900;
        Assert.Empty(WindowRules.Problems(document, low, standard));

        // Beside a door in the same wall, 200 from its edge.
        var door = new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Name.StartsWith("Front Door")).Id, LevelId = walls[2].LevelId, HostWallId = walls[2].Id, DistanceAlongWall = 1000 + 600 + 200 + 500, Mark = "D1" };
        document.Add(door);
        Assert.Contains(WindowRules.Problems(document, low, standard), problem => problem.Rule.Contains("within 300 of a door"));
        Assert.Contains(WindowRules.Problems(document, low, ClearanceStandard.Irc), problem => problem.Rule.Contains("R308.4.2"));

        // Safety glass answers both.
        Type(document, "Casement - 1200 x 1200").SafetyGlazing = true;
        Assert.Empty(WindowRules.Problems(document, low, standard));
    }

    [Fact]
    public void AWindowLetsOutNoMoreHeatThanApprovedDocumentLAllows()
    {
        var (document, _, walls) = Room(storey: 0);
        var louvres = Put(document, walls[2], "Louvred - 600 x 1200", 2000, 900);
        Assert.Contains(WindowRules.Problems(document, louvres, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "its U-value is 4.2.");
        Assert.All(document.TypesOf<WindowType>().Where(type => type.Name != "Louvred - 600 x 1200"), type => Assert.True(type.HeatTransferCoefficient <= 1.6, type.Name));
        Assert.StartsWith("Opens part way", WindowRules.Verdict(document, louvres));
    }
}
