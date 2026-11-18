using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Security of new dwellings by Approved Document Q (round 97): easily accessible doors and windows secure (1.1, 1.2, 2.1, 2.2), a garage's
/// doors or the door from it (1.1 note), letter plates (1.3), and the main door's viewer and limiter (1.4).
/// </summary>
[Collection("Wpf")]
public class SecurityTests
{
    /// <summary>A house 8 m by 6 m of brick on block, its walls drawn so their outsides face out, a hall in it; its south wall from x 8 m to 0.</summary>
    private static (BimDocument Document, Wall South, Room Hall) House()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(8000, 6000), new Point2D(8000, 0) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = level, UnconnectedHeight = 3000 }).ToList();
        foreach (var wall in walls) document.Add(wall);
        var hall = new Room { LevelId = level, Location = new Point2D(4000, 3000), Name = "Hall" };
        document.Add(hall);
        return (document, walls[3], hall);
    }

    private static T Copy<T>(BimDocument document, string name, string copy) where T : OpeningType
    {
        var type = document.TypesOf<T>().First(each => each.Name == name);
        var made = type is DoorType door ? (OpeningType)door.Duplicate(copy) : ((WindowType)(OpeningType)type).Duplicate(copy);
        document.AddType(made);
        return (T)made;
    }

    private static Door Door(BimDocument document, Wall wall, DoorType type, double along)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = type.Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static List<string> Q(BimDocument document, Door door) =>
        Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.Contains("Approved Document Q")).Select(problem => problem.Said).ToList();

    private static List<string> Q(BimDocument document, Window window) =>
        WindowRules.Problems(document, window, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.Contains("Approved Document Q")).Select(problem => problem.Said).ToList();

    [Fact]
    public void ASecurityStandardIsReadAsAdQListsThem()
    {
        Assert.True(Security.Secure("PAS 24:2016", door: true));
        Assert.True(Security.Secure("PAS 24:2012", door: false));
        Assert.True(Security.Secure("LPS 1175 SR2", door: true));
        Assert.False(Security.Secure("LPS 1175 SR1", door: true));
        Assert.True(Security.Secure("LPS 1175 SR1", door: false));
        Assert.True(Security.Secure("STS 202 BR2", door: true));
        Assert.False(Security.Secure("STS 202 BR1", door: true));
        Assert.True(Security.Secure("STS 201", door: true));
        Assert.False(Security.Secure("STS 201", door: false));
        Assert.True(Security.Secure("STS 204", door: false));
        Assert.True(Security.Secure("LPS 2081 Issue 1 security rating B", door: true));
        Assert.False(Security.Secure("LPS 2081 A", door: true));
        Assert.True(Security.Secure("LPS 2081 A", door: false));
        Assert.True(Security.Secure("Appendix B bespoke timber doorset", door: true));
        Assert.False(Security.Secure(string.Empty, door: true));
        Assert.Equal((260.0, 40.0), Security.LetterPlate("260 x 40"));
        Assert.Null(Security.LetterPlate("none"));
    }

    [Fact]
    public void AHousesEasilyAccessibleDoorsAndWindowsAreSecure()
    {
        var (document, south, _) = House();

        // The library's front door - PAS 24, a viewer and a limiter: met. A plain door the house's way in: neither secure, nor seen through, nor limited.
        var front = Door(document, south, document.TypesOf<DoorType>().First(type => type.Name == "Front Door - 1000 x 2100"), 4000);
        Assert.Empty(Q(document, front));
        document.Remove(front);
        var plain = Door(document, south, document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100"), 4000);
        var said = Q(document, plain);
        Assert.Contains("it is an easily accessible door into the building, no security standard given.", said);
        Assert.Contains("it is the main door into the home, with no door viewer, glass in it or a window beside it and no door chain or limiter.", said);

        // A window 800 mm along from its frame: callers seen, the viewer not asked; the limiter still is.
        var window = new Window { HostWallId = south.Id, TypeId = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").Id, LevelId = south.LevelId, DistanceAlongWall = 4000 + 450 + 800 + 600, SillHeight = 900 };
        document.Add(window);
        Assert.Contains("it is the main door into the home, with no door chain or limiter.", Q(document, plain));

        // The library's casement, PAS 24: met; one of no standard given, its sill 900 off the ground: easily accessible.
        Assert.Empty(Q(document, window));
        var loose = Copy<WindowType>(document, "Casement - 1200 x 1200", "Casement - No Standard");
        loose.Security = string.Empty;
        window.TypeId = loose.Id;
        Assert.Equal("it is easily accessible - within 2 m of the ground - no security standard given.", Assert.Single(Q(document, window)));

        // A letter plate 300 by 50: over 1.3's 260 by 40.
        var lettered = Copy<DoorType>(document, "Front Door - 1000 x 2100", "Front Door - Letter Plate");
        lettered.LetterPlate = "300 x 50";
        plain.TypeId = lettered.Id;
        Assert.Equal("its letter plate's aperture is 300 by 50 mm.", Assert.Single(Q(document, plain)));
    }

    [Fact]
    public void AnUpperWindowOverALowRoofIsEasilyAccessible()
    {
        // A window on the first floor, its sill 900 up - 3.9 m over the ground: not easily accessible.
        var (document, south, _) = House();
        var first = document.Levels[1];
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var upper = new Wall { Start = new Point2D(8000, 0), End = new Point2D(0, 0), TypeId = brick.Id, LevelId = first.Id, UnconnectedHeight = 3000 };
        document.Add(upper);
        foreach (var (a, b) in new[] { (new Point2D(0, 0), new Point2D(0, 6000)), (new Point2D(0, 6000), new Point2D(8000, 6000)), (new Point2D(8000, 6000), new Point2D(8000, 0)) })
            document.Add(new Wall { Start = a, End = b, TypeId = brick.Id, LevelId = first.Id, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = first.Id, Location = new Point2D(4000, 3000), Name = "Bedroom" });
        var loose = Copy<WindowType>(document, "Casement - 1200 x 1200", "Casement - No Standard");
        loose.Security = string.Empty;
        var window = new Window { HostWallId = upper.Id, TypeId = loose.Id, LevelId = first.Id, DistanceAlongWall = 4000, SillHeight = 900 };
        document.Add(window);
        Assert.Empty(Q(document, window));

        // A flat roof outside it 2.7 m up - a porch's - within 3.5 m of the ground, the window 1 m or so over it: easily accessible.
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[0].Id, HeightOffset = 2700 };
        roof.SetBoundary(new[] { new Point2D(2000, -2500), new Point2D(6000, -2500), new Point2D(6000, -165), new Point2D(2000, -165) });
        document.Add(roof);
        Assert.Equal("it is easily accessible - within 2 m of a low roof outside it - no security standard given.", Assert.Single(Q(document, window)));

        // A rooflight in that roof, of no standard: easily accessible too.
        var light = document.TypesOf<RoofWindowType>().First(type => type.Name == "Rooflight - Fixed 1000 x 1000").Duplicate("Rooflight - No Standard");
        light.Security = string.Empty;
        document.AddType(light);
        document.Add(new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = light.Id, Location = new Point2D(4000, -1300) });
        Assert.Contains(SlabRules.Problems(document, roof, document.ProjectInformation.ClearanceStandard), problem => problem.Said.StartsWith("its rooflight, the Rooflight - No Standard, is easily accessible", StringComparison.Ordinal));
    }

    [Fact]
    public void AGaragesDoorsOrTheDoorIntoTheHomeAreSecure()
    {
        // A garage built on the east end, 3 m wide, its sectional door - no standard - and a plain door from it into the hall.
        var (document, south, _) = House();
        var level = document.Levels[0].Id;
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var garageFront = new Wall { Start = new Point2D(11000, 0), End = new Point2D(8000, 0), TypeId = brick.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(garageFront);
        document.Add(new Wall { Start = new Point2D(8000, 6000), End = new Point2D(11000, 6000), TypeId = brick.Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Wall { Start = new Point2D(11000, 6000), End = new Point2D(11000, 0), TypeId = brick.Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = level, Location = new Point2D(9500, 3000), Name = "Garage" });
        var sectional = Door(document, garageFront, document.TypesOf<DoorType>().First(type => type.Name == "Garage Door - Sectional 2400 x 2100"), 1500);
        var east = document.Walls.First(wall => wall.Start == new Point2D(8000, 6000) && wall.End == new Point2D(8000, 0));
        var into = Door(document, east, document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100"), 3000);
        Assert.Equal("it is a door into the garage, which has a door into the home, and neither is a secure doorset.", Assert.Single(Q(document, sectional)));

        // The door into the home the FD30S fire door - PAS 24: met.
        into.TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Fire Door FD30S - 900 x 2100").Id;
        Assert.Empty(Q(document, sectional));

        // No door into the home at all: nothing asked of the garage's door.
        document.Remove(into);
        Assert.Empty(Q(document, sectional));
        _ = south;
    }

    [Fact]
    public void AFlatsDoorFromTheCommonPartsIsSecureWithAViewerAndLimiter()
    {
        // A storey 6 m up: a corridor and a flat off it. The FD30S fire door - PAS 24, a viewer and limiter: met; a plain door: neither.
        var document = BimDocument.CreateDefault();
        var level = new BIMDesigner.Core.Datums.Level { Name = "Second Floor", Elevation = 6000 };
        document.AddLevel(level);
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var partition = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000) };
        for (var i = 0; i < 4; i++) document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = level.Id, UnconnectedHeight = 3000 });
        var between = new Wall { Start = new Point2D(0, 2000), End = new Point2D(10000, 2000), TypeId = partition.Id, LevelId = level.Id, UnconnectedHeight = 3000 };
        document.Add(between);
        document.Add(new Room { LevelId = level.Id, Location = new Point2D(5000, 1000), Name = "Corridor" });
        document.Add(new Room { LevelId = level.Id, Location = new Point2D(5000, 4000), Name = "Living Room", Dwelling = "Flat 7" });
        var entrance = Door(document, between, document.TypesOf<DoorType>().First(type => type.Name == "Fire Door FD30S - 900 x 2100"), 5000);
        Assert.Empty(Q(document, entrance));
        entrance.TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Id;
        var said = Q(document, entrance);
        Assert.Contains("it is an easily accessible door into a flat from the common parts, no security standard given.", said);
        Assert.Contains("it is the main door into Flat 7, with no door viewer, glass in it or a window beside it and no door chain or limiter.", said);

        // Saved and opened again: the door type's security, viewer, limiter and letter plate kept.
        var fire = document.TypesOf<DoorType>().First(type => type.Name == "Fire Door FD30S - 900 x 2100");
        fire.LetterPlate = "260 x 40";
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        var back = opened.TypesOf<DoorType>().Single(type => type.Name == fire.Name);
        Assert.Equal(("PAS 24:2016", true, true, "260 x 40"), (back.Security, back.DoorViewer, back.DoorLimiter, back.LetterPlate));
        Assert.Equal("PAS 24:2016", opened.TypesOf<WindowType>().First().Security);
    }
}
