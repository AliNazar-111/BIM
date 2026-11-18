using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Glass in fire resisting walls and doors by Approved Document B's Table B5, doors in a compartment wall (C8), refuse stores (3.56, 3.57), the
/// common stair to a final exit (3.81) and an only stair not down to a basement (3.71) - round 96.
/// </summary>
[Collection("Wpf")]
public class FireGlazingTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, DoorType? type = null)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = (type ?? document.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100")).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y, string dwelling = "")
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name, Dwelling = dwelling };
        document.Add(room);
        return room;
    }

    private sealed record Storey(Level Level, List<Wall> Outside, Wall StairWall, Wall FlatWall, Door StairDoor, Door FlatDoor, Room Stair, Room Corridor);

    /// <summary>A block of flats 12 m by 6 m on storeys 3 m apart, as the fire separation tests build it - its doors FD30S, its walls 60 minutes.</summary>
    private static (BimDocument Document, List<Storey> Storeys) Block(int storeys)
    {
        var document = BimDocument.CreateDefault();
        var sixty = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm").Duplicate("Partition - 60 min");
        sixty.FireRating = "60 min";
        document.AddType(sixty);
        var levels = new List<Level> { document.Levels[0], document.Levels[1] };
        for (var i = 2; i < storeys; i++)
        {
            var level = new Level { Name = $"Level {i}", Elevation = 3000 * i };
            document.AddLevel(level);
            levels.Add(level);
        }

        var built = new List<Storey>();
        foreach (var level in levels.Take(storeys))
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) };
            var outside = Enumerable.Range(0, 4).Select(i => Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick)).ToList();
            var stairWall = Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 6000), sixty.Name);
            var flatWall = Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(12000, 2000), sixty.Name);
            built.Add(new Storey(level, outside, stairWall, flatWall, Door(document, stairWall, 1000), Door(document, flatWall, 5000),
                Room(document, level.Id, "Stair", 1500, 3000), Room(document, level.Id, "Corridor", 7500, 1000)));
            Room(document, level.Id, "Living Room", 7500, 4000, $"Flat {built.Count}");
        }

        for (var i = 0; i + 1 < built.Count; i++)
            document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = built[i].Level.Id, TopLevelId = built[i + 1].Level.Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
        return (document, built);
    }

    private static DoorType Glazed(BimDocument document, DoorLeafDesign design, string rating = "30 min (FD30S)")
    {
        var type = (DoorType)document.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100").Duplicate($"Fire Door - {design} {rating}");
        (type.LeafDesign, type.FireRating) = (design, rating);
        document.AddType(type);
        return type;
    }

    private static List<PlacementProblem> B5(BimDocument document, Door door) =>
        Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.Contains("Table B5")).ToList();

    [Fact]
    public void TheGlassInADoorIsWhereItsDesignPutsIt()
    {
        // A 900 by 2100 leaf: 800 by 2050 inside its frame, its stiles 110 and its rails 110 and 220 - glazed over 580 by 1720 of it.
        var document = BimDocument.CreateDefault();
        var (bottom, share) = FireGlazing.DoorGlass(Glazed(document, DoorLeafDesign.Glazed), 900, 2100)!.Value;
        Assert.Equal(220, bottom, 6);
        Assert.Equal(580.0 * 1720 / (800 * 2050), share, 6);
        Assert.Equal(0.42 * 2050 + 100, FireGlazing.DoorGlass(Glazed(document, DoorLeafDesign.HalfGlazed), 900, 2100)!.Value.Bottom, 6);
        Assert.Null(FireGlazing.DoorGlass(document.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100"), 900, 2100));

        Assert.True(FireGlazing.Insulated("EI 30"));
        Assert.True(FireGlazing.Insulated("REI 60"));
        Assert.False(FireGlazing.Insulated("30 min (FD30S)"));
        Assert.False(FireGlazing.Insulated("E 30 Sa"));
    }

    [Fact]
    public void GlassInTheCommonPartsIsLimitedByTableB5()
    {
        var (document, storeys) = Block(5);
        var second = storeys[2];

        // A glazed fire door at a flat's entrance: none allowed (item 6) - unless the glass insulates.
        second.FlatDoor.TypeId = Glazed(document, DoorLeafDesign.Glazed).Id;
        var flat = Assert.Single(B5(document, second.FlatDoor));
        Assert.Equal("it is glazed between Flat 3 and the Corridor.", flat.Said);
        Assert.Equal("Approved Document B (3.88, Table B5) limits glass that holds integrity but does not insulate in a door between Flat 3 and the Corridor to none (item 6) - its type's Fire Rating EI where it insulates.", flat.Rule);
        second.FlatDoor.TypeId = Glazed(document, DoorLeafDesign.Glazed, "EI 30 Sa").Id;
        Assert.Empty(B5(document, second.FlatDoor));

        // Glazed into the stair from the corridor: glass above 100 mm - met (item 8a).
        second.StairDoor.TypeId = Glazed(document, DoorLeafDesign.Glazed).Id;
        Assert.Empty(B5(document, second.StairDoor));

        // A store off the stair, a glazed door into it: with one stair a quarter of the door's area (item 7) - 61% glazed.
        var back = Wall(document, second.Level.Id, new Point2D(0, 4500), new Point2D(3000, 4500), "Partition - 60 min");
        var storeDoor = Door(document, back, 1500, Glazed(document, DoorLeafDesign.Glazed));
        Room(document, second.Level.Id, "Store", 1500, 5200);
        Assert.Equal("it is glazed between the common stair and the Store: its glass is 61% of the leaf.", Assert.Single(B5(document, storeDoor)).Said);
        storeDoor.TypeId = Glazed(document, DoorLeafDesign.ArchedTopLight).Id;
        Assert.Empty(B5(document, storeDoor));

        // A glazed screen in the stair's wall beside the corridor, E 30: with one stair its glass above 1100 mm (item 8a) - its sill at 900, its glass from 960.
        var screen = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200").Duplicate("Screen - E 30");
        (screen.Width, screen.Operation, screen.FireRating) = (400, WindowOperation.Fixed, "E 30");
        document.AddType(screen);
        var window = new Window { HostWallId = second.StairWall.Id, TypeId = screen.Id, LevelId = second.Level.Id, DistanceAlongWall = 1750, SillHeight = 900 };
        document.Add(window);
        List<string> Glass() => WindowRules.Problems(document, window, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.Contains("Approved Document B (3.88")).Select(problem => problem.Said).ToList();
        Assert.Equal("it is glass between the common stair and the Corridor, coming down to 960 mm over the floor.", Assert.Single(Glass()));
        window.SillHeight = 1100;
        Assert.Empty(Glass());

        // No rating at all: said as such.
        screen.FireRating = string.Empty;
        Assert.Equal("it is glass between the common stair and the Corridor with no fire rating.", Assert.Single(Glass()));
    }

    [Fact]
    public void ACompartmentWallsDoorsAreAQuarterOfItsLength()
    {
        // The flat's 9 m wall with the corridor: one 900 door, 10% - met; three, 2.7 m, 30% - over C8's quarter.
        var (document, storeys) = Block(3);
        var wall = storeys[1].FlatWall;
        List<string> C8() => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.Contains("(C8)")).Select(problem => problem.Said).ToList();
        Assert.Empty(C8());
        Door(document, wall, 2000);
        Door(document, wall, 8000);
        Assert.Equal("it is a compartment wall 9.0 m long with 2.7 m of doors in it, 30%.", Assert.Single(C8()));
    }

    [Fact]
    public void RefuseStoresStairsToAFinalExitAndAnOnlyStairNotToTheBasement()
    {
        // Five storeys; the ground floor's stair has no door out, nor the corridor: no final exit (3.81).
        var (document, storeys) = Block(5);
        var ground = storeys[0];
        List<EscapeProblem> Said(string clause) => Escape.Problems(document).Where(problem => problem.Said.Contains(clause)).ToList();
        Assert.Equal(ground.Stair, Assert.Single(Said("(3.81)")).Room);

        // A door out of the corridor's end: reached along it.
        var front = Door(document, ground.Outside[0], 6000, document.TypesOf<DoorType>().First(type => type.Name == "Front Door - 1000 x 2100"));
        Assert.Empty(Said("(3.81)"));

        // A bin store at the corridor's east end, come to from the corridor (3.57); its partition 60 minutes against the 60 asked (3.56a).
        var cross = Wall(document, ground.Level.Id, new Point2D(10000, 0), new Point2D(10000, 2000), "Partition - 60 min");
        Door(document, cross, 1000);
        var bins = Room(document, ground.Level.Id, "Bin Store", 11000, 1000);
        Assert.Equal("it is a refuse store come to from the Corridor - Approved Document B (3.57) asks a refuse store come to from the open air, or through a protected lobby with 0.2 m² of permanent ventilation",
            Assert.Single(Said("(3.57)")).Said);
        Assert.DoesNotContain(WallRules.Problems(document, cross, document.ProjectInformation.ClearanceStandard), problem => problem.Rule.Contains("(3.56a)"));
        cross.TypeId = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm").Id;
        Assert.Contains(WallRules.Problems(document, cross, document.ProjectInformation.ClearanceStandard), problem => problem.Rule.Contains("(3.56a)") && problem.Said == "it encloses the Bin Store, a refuse store, rated 30 minutes.");

        // A door into it from outside: met.
        Door(document, ground.Outside[0], 11000, document.TypesOf<DoorType>().First(type => type.Name == "Front Door - 1000 x 2100"));
        Assert.Empty(Said("(3.57)"));

        // A refuse room off the first floor's stair: not in a protected stairway (3.56b).
        var first = storeys[1];
        Door(document, Wall(document, first.Level.Id, new Point2D(0, 4500), new Point2D(3000, 4500), "Partition - 60 min"), 1500);
        Room(document, first.Level.Id, "Refuse Room", 1500, 5200);
        Assert.StartsWith("it is a refuse store off the Stair, the common stair - Approved Document B (3.56b)", Assert.Single(Said("(3.56b)")).Said);

        // A basement under it, the only stair going on down: 3.71 asks a stair of its own.
        var basement = new Level { Name = "Basement", Elevation = -3000 };
        document.AddLevel(basement);
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) };
        for (var i = 0; i < 4; i++) Wall(document, basement.Id, corners[i], corners[(i + 1) % 4], Brick);
        Door(document, Wall(document, basement.Id, new Point2D(3000, 0), new Point2D(3000, 6000), "Partition - 60 min"), 1000);
        var down = Room(document, basement.Id, "Stair", 1500, 3000);
        Room(document, basement.Id, "Plant Room", 7500, 3000);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = basement.Id, TopLevelId = ground.Level.Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
        Assert.Equal(down, Assert.Single(Said("(3.71)")).Room);
        _ = (front, bins);
    }
}
