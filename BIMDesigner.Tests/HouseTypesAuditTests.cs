using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// What building England's house types end to end turned up (round 147): a house drawn as one is drawn - its walls Non-bearing, as every
/// wall is until it is set otherwise - has its first floor and its roof bearing on nothing in the analytical model, and is told which walls
/// to set bearing; an outside wall not set bearing on a slab on the ground is not a partition on a span; and a room just short of the air it
/// purges is not told it opens what it is asked for; a stair up through the stairwell drawn in a floor is a stair inside; a room with a nib
/// in it is enclosed; a corner shower's access zone runs along its open side; a wall attached to the roof reaches it; a landing upstairs is not
/// a habitable room.
/// </summary>
public class HouseTypesAuditTests
{
    private static WallType Outside(BimDocument document) => document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");

    /// <summary>Four outside walls 10 m by 8 m on the ground floor, 3 m high, as drawn: Non-bearing.</summary>
    private static (BimDocument Document, List<Wall> Walls) Box()
    {
        var document = BimDocument.CreateDefault();
        var type = Outside(document);
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 8000), new Point2D(10000, 8000), new Point2D(10000, 0) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
            document.Add(wall);
            walls.Add(wall);
        }

        return (document, walls);
    }

    private static Floor AddFloor(BimDocument document, int level, string type)
    {
        var floor = new Floor { LevelId = document.Levels[level].Id, TypeId = document.TypesOf<FloorType>().First(each => each.Name == type).Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(floor);
        return floor;
    }

    private static List<string> Problems(BimDocument document, Element about) =>
        StructuralLoading.Of(document, AnalyticalModel.Of(document)).Problems.Where(problem => problem.Elements.Contains(about)).Select(problem => problem.Said).ToList();

    [Fact]
    public void ARoofOverWallsNotSetBearingSaysWhichToSet()
    {
        var (document, roof) = EaveTests.House(hip: false);

        // Each eave over a wall drawn but not set bearing: bears on nothing, and that wall is the one to set.
        var said = Problems(document, roof);
        Assert.Equal(2, said.Count);
        Assert.All(said, each => Assert.EndsWith("bears on nothing in the analytical model: no bearing wall runs under it. The wall under its eave, Exterior - Brick on Block 330mm, is Non-bearing: set its Structural Usage to Bearing to carry it.", each));

        // Set so, they carry it.
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        Assert.Empty(Problems(document, roof));
    }

    [Fact]
    public void AFloorOverWallsNotSetBearingSaysTheyCouldCarryIt()
    {
        var (document, walls) = Box();
        var floor = AddFloor(document, 1, "Floor - Timber 220mm");

        Assert.Equal(
            "It spans onto nothing in the analytical model: no bearing wall or beam is under it, across the way it spans. 4 walls under it - Exterior - Brick on Block 330mm - are Non-bearing: set the Structural Usage of those that carry it to Bearing.",
            Assert.Single(Problems(document, floor)));

        // The two long walls set bearing: it spans the short way onto them.
        walls[1].StructuralUsage = walls[3].StructuralUsage = StructuralUsage.Bearing;
        Assert.DoesNotContain(Problems(document, floor), each => each.StartsWith("It spans onto nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void AHeavyWallNotSetBearingIsAPartitionOnlyOnASpan()
    {
        var (document, walls) = Box();
        foreach (var wall in walls) wall.StructuralUsage = StructuralUsage.Bearing;
        var ground = AddFloor(document, 0, "Floor - Screed on Slab 250mm");
        var first = AddFloor(document, 1, "Floor - Timber 220mm");

        // An outside wall's build-up across the middle of each storey, not set bearing: about 9 kN/m on 3 m.
        var type = Outside(document);
        foreach (var level in new[] { 0, 1 })
            document.Add(new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, 8000), TypeId = type.Id, LevelId = document.Levels[level].Id, UnconnectedHeight = 2600 });

        // On the slab on the ground it stands on the ground: nothing said.
        Assert.DoesNotContain(Problems(document, ground), each => each.Contains("6.3.1.2", StringComparison.Ordinal));

        // On the timber floor it is too heavy to spread, and it is named, with what to do if it bears.
        var said = Assert.Single(Problems(document, first), each => each.Contains("6.3.1.2", StringComparison.Ordinal));
        Assert.StartsWith("A wall not set bearing, Exterior - Brick on Block 330mm - an outside wall, of ", said);
        Assert.EndsWith("If it carries what is over it, set its Structural Usage to Bearing.", said);
    }

    [Fact]
    public void AStairUpThroughAStairwellDrawnInTheFloorIsInside()
    {
        // A stair up the side of a house, 15 risers from y 600 to its head at 3750, and the first floor's stairwell drawn as a hole over it.
        var (document, floor) = StairTests.House();
        var stair = StairTests.Put(document, 650, 600);
        floor.SetHoles(new[] { new[] { new Point2D(200, 600), new Point2D(1100, 600), new Point2D(1100, 3750), new Point2D(200, 3750) } });

        // Its foot is in the floor's outline, in its stairwell: it comes up through the floor, not 650 past the edge from outside.
        Assert.Null(Stairs.Overrun(document, stair, Stairs.Layout(document, stair), stair.TopLevelId));
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("past the edge of the floor", StringComparison.Ordinal));
    }

    [Fact]
    public void ARoomWithANibInItIsEnclosedRoundTheNib()
    {
        // A room 5 m square of outside walls, 4670 inside, and a partition 125 thick from its front wall 3 m in, its end free: a nib.
        var document = BimDocument.CreateDefault();
        var level = document.Levels[1].Id;
        var outside = Outside(document);
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 5000), new Point2D(5000, 5000), new Point2D(5000, 0) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = outside.Id, LevelId = level, UnconnectedHeight = 3000 });
        var stud = document.TypesOf<WallType>().First(each => each.Name == "Stud Wall - Acoustic 125mm");
        document.Add(new Wall { Start = new Point2D(2500, 0), End = new Point2D(2500, 3000), TypeId = stud.Id, LevelId = level, UnconnectedHeight = 3000 });

        // Traced round the nib from either side of it - not left unenclosed at its free end - and the nib's own floor, 125 by 2835 from
        // the front wall's face to its end, not the room's.
        foreach (var seed in new[] { new Point2D(4000, 4000), new Point2D(1000, 500), new Point2D(4000, 500) })
        {
            var boundary = new Room { LevelId = level, Location = seed, Name = "Living Room" }.GetBoundary(document);
            Assert.True(boundary.IsEnclosed);
            Assert.Equal(4.67 * 4.67 - 0.125 * (3000 - 165) / 1000, boundary.Area / 1e6, 4);
        }

        // As a landing round a stairwell's side carried on past the wall teeing into it: one room, not none.
        var landing = new Room { LevelId = level, Location = new Point2D(2000, 4000), Name = "Landing" };
        document.Add(new Wall { Start = new Point2D(2500, 2500), End = new Point2D(5000, 2500), TypeId = stud.Id, LevelId = level, UnconnectedHeight = 3000 });
        Assert.True(landing.GetBoundary(document).IsEnclosed);
    }

    [Fact]
    public void APartyWallAttachedToAHippedRoofReachesItsUnderside()
    {
        // A pair of semis 14 m by 9 m under one hipped roof, 40 degrees from 3 m: the party wall across the middle, under the ridge.
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var party = document.TypesOf<WallType>().First(each => each.Name == "Party Wall - Dense Cavity Blockwork 300mm");
        var wall = new Wall { Start = new Point2D(7000, 9000), End = new Point2D(7000, 0), TypeId = party.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        var roof = new Roof { LevelId = level, TypeId = document.TypesOf<RoofType>().First().Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(-300, -300), new Point2D(14300, -300), new Point2D(14300, 9300), new Point2D(-300, 9300) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(_ => new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }));
        document.Add(roof);

        // Stopped at the eaves' height it is well short of the roof over its middle.
        Assert.True(WallRules.ShortOfRoof(document, wall) > 3000);

        // Attached to the roof it follows its underside up: its top, as the wall reports it, is still the roof's base - but it reaches.
        wall.TopAttachedTo = roof.Id;
        Assert.Null(WallRules.ShortOfRoof(document, wall));
        Assert.DoesNotContain(WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM), problem => problem.Said.Contains("below the roof", StringComparison.Ordinal));
    }

    [Fact]
    public void ALandingOrAKitchenUpstairsIsAskedNoEscapeWindowNorALandingPurgeAir()
    {
        // Rooms on the first floor of a house, 4 m by 3 m inside, with no window: a bedroom, a kitchen and a landing.
        string? Verdict(string name)
        {
            var document = BimDocument.CreateDefault();
            var level = document.Levels[1].Id;
            var type = Outside(document);
            var half = type.Structure.TotalWidth / 2;
            var corners = new[] { new Point2D(-half, -half), new Point2D(-half, 3000 + half), new Point2D(4000 + half, 3000 + half), new Point2D(4000 + half, -half) };
            for (var i = 0; i < 4; i++)
                document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 });
            var room = new Room { LevelId = level, Location = new Point2D(2000, 1500), Name = name };
            document.Add(room);
            return WindowRules.Verdict(document, room);
        }

        // Approved Document B 2.2: each habitable room upstairs but a kitchen; Approved Document F's purge air, a habitable room's.
        Assert.Contains("no escape window", Verdict("Bedroom 1"));
        Assert.Contains("to purge its air", Verdict("Bedroom 1"));
        Assert.DoesNotContain("escape window", Verdict("Kitchen") ?? string.Empty);
        Assert.Null(Verdict("Landing"));
        Assert.Null(Verdict("Bathroom"));
    }

    [Fact]
    public void ACornerShowerKeepsItsAccessZoneAlongItsOpenSide()
    {
        // A bathroom 3 m square of partitions, a 900 shower in its back right corner facing into the room.
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(each => each.Name == "Interior - Partition 125mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 3000), new Point2D(3000, 3000), new Point2D(3000, 0) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 2400 });
        var shower = document.TypesOf<ComponentType>().First(each => each.Name == "Shower Enclosure - Corner 900 x 900");
        var face = 3000 - type.Structure.TotalWidth / 2;
        document.Add(new Component { TypeId = shower.Id, LevelId = level, Location = new Point2D(face - 450, face - 450) });

        // Its 1100 by 700 zone, centred, would run 100 into the side wall: from its corner end along its open side it is clear.
        Assert.DoesNotContain(Clearances.Blocked(document, level, ClearanceStandard.ApprovedDocumentM), blocked => blocked.Zone.What.Contains("shower", StringComparison.Ordinal));
    }

    [Fact]
    public void ARoomJustShortOfItsPurgeAirIsToldByHowMuch()
    {
        // A room 4.8 m by 4.875 m inside, 23.4 m²: a twentieth is 1.170 m²; a 1200 casement opens 1080 by 1080, 1.166 m².
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = Outside(document);
        var half = type.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(-half, 4875 + half), new Point2D(4800 + half, 4875 + half), new Point2D(4800 + half, -half) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 };
            document.Add(wall);
            walls.Add(wall);
        }

        var room = new Room { LevelId = level, Location = new Point2D(2400, 2400), Name = "Bedroom", Number = "1" };
        document.Add(room);
        void Put(Wall wall, string name, double sill, string mark) => document.Add(new Window
        {
            TypeId = document.TypesOf<WindowType>().Single(each => each.Name == name).Id, LevelId = level, HostWallId = wall.Id, DistanceAlongWall = 2400, SillHeight = sill, Mark = mark
        });

        Put(walls[3], "Casement - 1200 x 1200", 900, "W1");
        Assert.Contains("opens 1.166 m² - Approved Document F (Table 1.4) asks for a twentieth of its floor, 1.170 m², to purge its air", WindowRules.Verdict(document, room));

        // A hopper besides, opening part way: what opens fully and what part way, against a twentieth and a tenth.
        Put(walls[1], "Hopper - 900 x 600", 1500, "W2");
        Assert.Contains("opens 1.166 m² fully and 0.37 m² part way - Approved Document F (Table 1.4) asks for a twentieth of its floor, 1.170 m², opening fully, or a tenth, 2.34 m², through windows opening only part way",
            WindowRules.Verdict(document, room));
    }
}
