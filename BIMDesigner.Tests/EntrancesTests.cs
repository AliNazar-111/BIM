using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// The way into a block of flats by Approved Document M Volume 1 (round 163): the principal communal entrance (1.9, 2.14, 3.14), the other
/// communal doors along the approach route (2.15, 3.15), the flats' own entrances off the common parts (1.14, 2.20, 3.22), the lifts (1.11,
/// 2.16, 3.16), and a flat upstairs reached step-free (2.6).
/// </summary>
[Collection("Wpf")]
public class EntrancesTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private sealed record Built(BimDocument Document, Door Entrance, Door Inner, Door FlatOne, Door FlatTwo, Door Plant, Room Lobby, Room FlatOneRoom, Door? Upstairs);

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = "Single - 900 x 2100")
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y, string dwelling = "")
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name, Dwelling = dwelling };
        document.Add(room);
        return room;
    }

    /// <summary>
    /// A block 12 m by 8 m. On the ground floor: an entrance lobby in the middle of the south side, so wide, entered through the outside wall at
    /// x 6 m and opening at its back into a corridor running the block's length, y 3 to 5 m; a plant room west of the lobby, a bin store east;
    /// two flats north of the corridor, entered from it at x 3 m and 9 m. With a storey over it, the same corridor there and a third flat entered
    /// from it at x 3 m.
    /// </summary>
    private static Built Block(ClearanceStandard standard, double lobbyWide = 3000, bool upstairs = false)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Block of flats";
        document.ProjectInformation.ClearanceStandard = standard;
        Door? upper = null;
        Door entrance = null!, inner = null!, one = null!, two = null!, plant = null!;
        Room lobby = null!, flat = null!;
        foreach (var level in upstairs ? document.Levels.Take(2) : document.Levels.Take(1))
        {
            var ground = level == document.Levels[0];
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
            var outside = Enumerable.Range(0, 4).Select(i => Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick)).ToList();
            var south = Wall(document, level.Id, new Point2D(0, 3000), new Point2D(12000, 3000));
            var north = Wall(document, level.Id, new Point2D(0, 5000), new Point2D(12000, 5000));
            Wall(document, level.Id, new Point2D(6000, 5000), new Point2D(6000, 8000));
            Room(document, level.Id, "Corridor", 1500, 4000);
            if (ground)
            {
                Wall(document, level.Id, new Point2D(6000 - lobbyWide / 2, 0), new Point2D(6000 - lobbyWide / 2, 3000));
                Wall(document, level.Id, new Point2D(6000 + lobbyWide / 2, 0), new Point2D(6000 + lobbyWide / 2, 3000));
                entrance = Door(document, outside[0], 6000);
                inner = Door(document, south, 6000);
                plant = Door(document, south, 1500);
                one = Door(document, north, 3000);
                two = Door(document, north, 9000);
                lobby = Room(document, level.Id, "Entrance Lobby", 6000, 1500);
                Room(document, level.Id, "Plant Room", 1000, 1500);
                Room(document, level.Id, "Bin Store", 11000, 1500);
                flat = Room(document, level.Id, "Living Room", 3000, 6500, "Flat 1");
                Room(document, level.Id, "Living Room", 9000, 6500, "Flat 2");
            }
            else
            {
                upper = Door(document, north, 3000);
                Room(document, level.Id, "Store", 6000, 1500);
                Room(document, level.Id, "Living Room", 3000, 6500, "Flat 3");
                Room(document, level.Id, "Living Room", 9000, 6500, "Flat 4");
            }
        }

        return new Built(document, entrance, inner, one, two, plant, lobby, flat, upper);
    }

    private static IReadOnlyList<(string Said, string Rule)> Said(Built built, Door door) =>
        Entrances.DoorProblems(built.Document, door, built.Document.FindType<DoorType>(door.TypeId)!, built.Document.ProjectInformation.ClearanceStandard);

    private static void Retype(BimDocument document, Door door, string type) => door.TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id;

    [Fact]
    public void TheWayIntoABlockIsFound()
    {
        var built = Block(ClearanceStandard.ApprovedDocumentM);
        var document = built.Document;
        Assert.True(Entrances.Block(document));
        Assert.Equal(built.Entrance, Entrances.CommunalEntrance(document));
        Assert.Equal(Entrances.Role.CommunalEntrance, Entrances.RoleOf(document, built.Entrance));
        Assert.Equal(Entrances.Role.CommunalDoor, Entrances.RoleOf(document, built.Inner));
        Assert.Equal(Entrances.Role.PrivateEntrance, Entrances.RoleOf(document, built.FlatOne));
        Assert.Equal(Entrances.Role.PrivateEntrance, Entrances.RoleOf(document, built.FlatTwo));

        // A plant room's door is no way in; nor is anything in a house but its front door.
        Assert.Equal(Entrances.Role.None, Entrances.RoleOf(document, built.Plant));
        var (house, _) = StairTests.House();
        Assert.False(Entrances.Block(house));
    }

    [Fact]
    public void TheCommunalEntranceCommunalDoorsAndAFlatsDoorAreWideEnough()
    {
        // 900 doors, 760 clear: under M4(2) 850 asked through each - by 2.14 at the communal entrance, 2.15 further in, 2.20 at a flat's door.
        var built = Block(ClearanceStandard.ApprovedDocumentM);
        var clear = Doors.ClearWidth(built.Document.FindType<DoorType>(built.Entrance.TypeId)!);
        Assert.Equal(760, clear, 0);
        Assert.Contains(Said(built, built.Entrance), problem => problem.Rule.Contains("M4(2) 2.14d, e") && problem.Said.Contains($"{clear:0} clear"));
        Assert.Contains(Said(built, built.Inner), problem => problem.Rule.Contains("M4(2) 2.15, 2.14d, e"));
        Assert.Contains(Said(built, built.FlatOne), problem => problem.Rule.Contains("M4(2) 2.20d, e"));
        Assert.Empty(Said(built, built.Plant));

        // Said once in the door's Regulations - not again as any external door's 850 (2.21).
        var said = Doors.Problems(built.Document, built.Entrance, ClearanceStandard.ApprovedDocumentM);
        Assert.Single(said, problem => problem.Said.Contains(" clear"));

        // 1000 doors give 850: nothing said of their widths.
        foreach (var door in new[] { built.Entrance, built.Inner, built.FlatOne }) Retype(built.Document, door, "Single - 1000 x 2100");
        Assert.DoesNotContain(Said(built, built.Entrance), problem => problem.Said.Contains(" clear"));
        Assert.DoesNotContain(Said(built, built.FlatOne), problem => problem.Said.Contains(" clear"));

        // Under M4(1): 775 at the communal entrance (1.9a) and a flat's door (1.14a); nothing of the doors between.
        var visitable = Block(ClearanceStandard.ApprovedDocumentM1);
        Assert.Contains(Said(visitable, visitable.Entrance), problem => problem.Rule.Contains("M4(1) 1.9a") && problem.Said.Contains("760 clear"));
        Assert.Contains(Said(visitable, visitable.FlatOne), problem => problem.Rule.Contains("M4(1) 1.14a"));
        Assert.Empty(Said(visitable, visitable.Inner));
        foreach (var door in new[] { visitable.Entrance, visitable.FlatOne }) Retype(visitable.Document, door, "Single - 1000 x 2100");
        Assert.Empty(Said(visitable, visitable.Entrance));
        Assert.Empty(Said(visitable, visitable.FlatOne));
    }

    [Fact]
    public void TheCommunalEntranceIsLitByALightSwitchedOnByItself()
    {
        var built = Block(ClearanceStandard.ApprovedDocumentM);
        var document = built.Document;
        bool Unlit() => Said(built, built.Entrance).Any(problem => problem.Said.Contains("switched on by itself") && problem.Rule.Contains("2.14c"));
        Assert.True(Unlit());
        Assert.DoesNotContain(Said(built, built.Inner), problem => problem.Said.Contains("switched on by itself"));
        Assert.DoesNotContain(Said(built, built.FlatOne), problem => problem.Said.Contains("switched on by itself"));

        // A plain wall light beside it does not do; the library's dusk to dawn one does.
        var plain = document.TypesOf<ComponentType>().First(type => type.Name == "Wall Light");
        var light = new Component { TypeId = plain.Id, TypeKind = plain.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(6800, -200), Rotation = 180, Elevation = 1700 };
        document.Add(light);
        Assert.True(Unlit());
        var outdoor = document.TypesOf<ComponentType>().First(type => type.Name == "Wall Light - Outdoor, Dusk to Dawn");
        Assert.True(outdoor.AutoSwitched);
        light.TypeId = outdoor.Id;
        Assert.False(Unlit());

        // Inside the lobby, it is no light at the entrance.
        light.Location = new Point2D(6800, 400);
        Assert.True(Unlit());
    }

    [Fact]
    public void TheWidthANibMakesIsKeptBeyondAFlatsDoor()
    {
        var built = Block(ClearanceStandard.ApprovedDocumentM);
        var document = built.Document;
        var door = built.FlatOne;
        Retype(document, door, "Single - 1000 x 2100");
        var type = document.FindType<DoorType>(door.TypeId)!;
        Assert.Equal(built.FlatOneRoom, Doors.RoomsBeside(document, door).Swing);

        // A wall standing into the flat 150 past the door's leading edge, from 600 north of the corridor wall: the nib is there at the door's face,
        // but kept only to it - 2.20f asks 1200.
        var (from, to) = door.GetSpan(type);
        var latch = door.FlipHand ? from - 150 : to + 150;
        var nib = Wall(document, document.Levels[0].Id, new Point2D(latch, 5600), new Point2D(latch, 8000));
        Assert.Null(Doors.Nib(document, door, type) is { } near && near < 300 ? near : null);
        var kept = Doors.NibKept(document, door, type, 300, 1200);
        Assert.NotNull(kept);
        Assert.InRange(kept!.Value, 600 - 62.5 - 50, 600 - 62.5);
        Assert.Contains(Said(built, door), problem => problem.Said.Contains("runs") && problem.Said.Contains("before a wall") && problem.Rule.Contains("M4(2) 2.20f") && problem.Rule.Contains("1200"));

        // Starting 1400 north of it, the width is kept 1200: nothing said. A wheelchair user's asks 1800 (3.22e).
        nib.Start = new Point2D(latch, 6400);
        Assert.DoesNotContain(Said(built, door), problem => problem.Said.Contains("before a wall"));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM3;
        Assert.Contains(Said(built, door), problem => problem.Said.Contains("leading edge runs") && problem.Rule.Contains("M4(3) 3.22e") && problem.Rule.Contains("1800"));
    }

    [Fact]
    public void ALobbysDoorsAreApartAndSoAreTheirSwings()
    {
        // The lobby 2772 deep inside: the doors 2772 apart, the entrance's swing well clear of the inner door, which opens into the corridor.
        var built = Block(ClearanceStandard.ApprovedDocumentM);
        var document = built.Document;
        Assert.Equal(built.Lobby, Doors.RoomsBeside(document, built.Entrance).Swing);
        Assert.DoesNotContain(Said(built, built.Entrance), problem => problem.Rule.Contains("lobby or porch"));

        // Turned to open into the lobby, the two swings come within 1500 of each other.
        built.Inner.FlipFacing = !built.Inner.FlipFacing;
        Assert.Equal(built.Lobby, Doors.RoomsBeside(document, built.Inner).Swing);
        Assert.Contains(Said(built, built.Entrance), problem => problem.Said.Contains("between its swing") && problem.Rule.Contains("M4(2) 2.14i"));
    }

    [Fact]
    public void AWheelchairUsersCommunalEntranceHasRoomToTurnInside()
    {
        var wide = Block(ClearanceStandard.ApprovedDocumentM3);
        Assert.DoesNotContain(Said(wide, wide.Entrance), problem => problem.Rule.Contains("3.14d"));
        var narrow = Block(ClearanceStandard.ApprovedDocumentM3, lobbyWide: 1400);
        Assert.Contains(Said(narrow, narrow.Entrance), problem => problem.Said.Contains("1500 turning circle") && problem.Rule.Contains("M4(3) 3.14d"));

        // And a 200 nib beside its following edge (3.14h); under M4(2), none asked.
        var wheel = Block(ClearanceStandard.ApprovedDocumentM3);
        Assert.DoesNotContain(Said(wheel, wheel.Entrance), problem => problem.Said.Contains("following edge is"));
        var adaptable = Block(ClearanceStandard.ApprovedDocumentM);
        Assert.DoesNotContain(Said(adaptable, adaptable.Entrance), problem => problem.Rule.Contains("following edge"));
    }

    [Fact]
    public void ARevealOver200IsSaid()
    {
        var built = Block(ClearanceStandard.ApprovedDocumentM);
        var document = built.Document;
        var type = document.FindType<DoorType>(built.Entrance.TypeId)!;
        var brick = document.GetWallType(document.Find<Wall>(built.Entrance.HostWallId)!)!.Structure.TotalWidth;
        Assert.Equal((brick - (type.Thickness > 0 ? type.Thickness : 100)) / 2, Doors.Reveal(document, built.Entrance, type)!.Value, 3);
        Assert.DoesNotContain(Said(built, built.Entrance), problem => problem.Rule.Contains("2.14g"));

        // In a wall so thick that the reveal is over 200.
        var thick = document.TypesOf<WallType>().OrderByDescending(each => each.Structure.TotalWidth).First();
        Assert.True((thick.Structure.TotalWidth - (type.Thickness > 0 ? type.Thickness : 100)) / 2 > 200);
        document.Find<Wall>(built.Entrance.HostWallId)!.TypeId = thick.Id;
        Assert.Contains(Said(built, built.Entrance), problem => problem.Said.Contains("reveal") && problem.Rule.Contains("M4(2) 2.14g"));
    }

    private static Component Lift(BimDocument document, ComponentType type)
    {
        var lift = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(10000, 3000 - 62.5 - type.Depth / 2 - 100) };
        document.Add(lift);
        return lift;
    }

    [Fact]
    public void AFlatsLiftIsSizedByVolume1()
    {
        // The bin store taken for the lift's shaft, its landing door in the corridor wall by the car.
        var built = Block(ClearanceStandard.ApprovedDocumentM);
        var document = built.Document;
        var corridor = document.Walls.Single(wall => wall.LevelId == document.Levels[0].Id && wall.Start == new Point2D(0, 3000));
        Door(document, corridor, 10000, "Lift Landing Door - Sliding 900 x 2100");
        var eight = document.TypesOf<ComponentType>().First(type => type.Name == "Passenger Lift - 8 Person, Car 1100 x 1400");
        var lift = Lift(document, eight);
        IReadOnlyList<PlacementProblem> Said() => Lifts.Problems(document, lift, document.ProjectInformation.ClearanceStandard);
        Assert.Single(Lifts.Of(document)[0].Landings);
        Assert.Empty(Said());

        // A car 1000 by 1250: short of 2.16's 1100 by 1400, enough for M4(1)'s 900 by 1250.
        var small = eight.Duplicate("Small Lift");
        small.Width = 1000;
        small.Depth = 1250;
        document.AddType(small);
        lift.TypeId = small.Id;
        Assert.Contains(Said(), problem => problem.Said.Contains("its car is 1000 wide and 1250 deep") && problem.Rule.Contains("M4(2) 2.16b, c"));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM1;
        Assert.DoesNotContain(Said(), problem => problem.Said.Contains("its car"));
        small.Width = 850;
        Assert.Contains(Said(), problem => problem.Rule.Contains("M4(1) 1.11d"));

        // The landing in front of its door: 1500 square, clear (2.16a) - a table standing in the corridor there is in it.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        lift.TypeId = eight.Id;
        var table = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.Table);
        document.Add(new Component { TypeId = table.Id, TypeKind = table.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(10000, 4300) });
        Assert.Contains(Said(), problem => problem.Said.Contains("the 1500 x 1500 in front of its landing door") && problem.Rule.Contains("M4(2) 2.16a"));
    }

    [Fact]
    public void AFlatUpstairsIsReachedByALiftUnderM42()
    {
        var built = Block(ClearanceStandard.ApprovedDocumentM, upstairs: true);
        var document = built.Document;
        Assert.Equal(Entrances.Role.PrivateEntrance, Entrances.RoleOf(document, built.Upstairs!));
        var said = Entrances.Problems(document);
        Assert.Contains(said, problem => problem.Element == built.Upstairs && problem.Said.Contains($"Flat 3's entrance is on the {document.Levels[1].Name}, where no lift stops") && problem.Said.Contains("M4(2) 2.6"));

        // A lift with a landing door on both storeys: step-free.
        foreach (var level in document.Levels.Take(2))
            Door(document, document.Walls.Single(wall => wall.LevelId == level.Id && wall.Start == new Point2D(0, 3000)), 10000, "Lift Landing Door - Sliding 900 x 2100");
        Lift(document, document.TypesOf<ComponentType>().First(type => type.Name == "Passenger Lift - 8 Person, Car 1100 x 1400"));
        Assert.Empty(Entrances.Problems(document));

        // Under M4(1) a stair would do (1.10).
        var visitable = Block(ClearanceStandard.ApprovedDocumentM1, upstairs: true);
        Assert.Empty(Entrances.Problems(visitable.Document));
    }

    [Fact]
    public void AMaisonettesUpperDoorOntoTheCommonPartsIsNotItsEntrance()
    {
        // Flat 1's rooms on both storeys, a door off the corridor on each: its entrance is the one on its lower storey; upstairs, the door
        // onto the common parts is a way out, and no lift is wanted for it.
        var built = Block(ClearanceStandard.ApprovedDocumentM, upstairs: true);
        var document = built.Document;
        document.All<Room>().Single(room => room.Dwelling == "Flat 3").Dwelling = "Flat 1";
        Assert.Equal(Entrances.Role.PrivateEntrance, Entrances.RoleOf(document, built.FlatOne));
        Assert.Equal(Entrances.Role.None, Entrances.RoleOf(document, built.Upstairs!));
        Assert.DoesNotContain(Entrances.Problems(document), problem => problem.Said.Contains("Flat 1's entrance"));
    }

    [Fact]
    public void AFlatsEntranceStoreyIsWhereItsDoorIs()
    {
        // Upstairs, in Flat 3, a door off its living room hard by the corridor wall: on the flat's entrance storey, a 300 nib is asked (2.22d).
        var built = Block(ClearanceStandard.ApprovedDocumentM, upstairs: true);
        var document = built.Document;
        var upper = document.Levels[1].Id;
        var partition = Wall(document, upper, new Point2D(4000, 5000), new Point2D(4000, 8000));
        Room(document, upper, "Bedroom", 5000, 6500, "Flat 3");
        var door = Door(document, partition, 600);
        door.FlipHand = true;
        var type = document.FindType<DoorType>(door.TypeId)!;
        Assert.InRange(Doors.Nib(document, door, type)!.Value, 0, 299);
        Assert.True(Entrances.OnFlatsEntranceStorey(document, door));
        Assert.Contains(Doors.Problems(document, door, ClearanceStandard.ApprovedDocumentM), problem => problem.Said.Contains("its leading edge is") && problem.Rule.Contains("2.22"));
    }

    [Fact]
    public void TheCommunalEntrancesLandingIsAskedByItsOwnParagraph()
    {
        // An approach path up to the communal entrance, nothing over the landing: 2.14b's 1200 wide and 900 deep, not 2.20b's 900 by 600.
        var built = Block(ClearanceStandard.ApprovedDocumentM);
        var document = built.Document;
        var path = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Path, Approach = true };
        path.SetPath(new[] { new Point2D(6000, -6300), new Point2D(6000, -300) }, 1500);
        document.Add(path);
        Assert.Equal(built.Entrance, Entrances.CommunalEntrance(document));
        Assert.Contains(Approaches.EntranceProblems(document, built.Entrance, ClearanceStandard.ApprovedDocumentM),
            problem => problem.Said.Contains("nothing covers") && problem.Rule.Contains("M4(2) 2.14a, b") && problem.Rule.Contains("1200 wide and 900 deep"));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM3;
        Assert.Contains(Approaches.EntranceProblems(document, built.Entrance, ClearanceStandard.ApprovedDocumentM3),
            problem => problem.Rule.Contains("M4(3) 3.14a, b") && problem.Rule.Contains("1200 wide and 1200 deep"));
    }
}
