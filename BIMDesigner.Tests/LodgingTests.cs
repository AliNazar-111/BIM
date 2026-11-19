using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A hotel's bedrooms by Approved Document M Volume 2 (round 104): doors from the corridor by Table 2, one wheelchair-accessible bedroom in 20,
/// its door's leading edge, turning space, the space beside its bed, its alarm and its en-suite; switches and sockets by 4.30; a dwelling's
/// bedroom sizes and spaces not asked of a hotel's.
/// </summary>
[Collection("Wpf")]
public class LodgingTests
{
    private static Wall Wall(BimDocument document, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, string name, double x, double y)
    {
        var room = new Room { LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    private static Component Place(BimDocument document, string type, double x, double y, double? elevation = null)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = elevation ?? kind.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    /// <summary>Turns a door to open into a room - or out of it.</summary>
    private static void Swing(BimDocument document, Door door, Room room, bool into)
    {
        var sweep = DoorSwing.Sweeps(document, door).First();
        var inside = Polygon2D.Contains(room.GetBoundary(document).Polygon, new Point2D(sweep.Average(point => point.X), sweep.Average(point => point.Y)));
        if (inside != into) door.FlipFacing = !door.FlipFacing;
    }

    private sealed record Hotel(BimDocument Document, Room First, Room Accessible, Room EnSuite, Door AccessibleDoor);

    /// <summary>
    /// A hotel 12 m by 8 m: a corridor along the south (y 0 to 2 m, 1.77 m clear); bedroom 1 west of x 4 m off a 900 door, bedroom 3 east of x 8.5 m
    /// off a 1000 one; between them the accessible bedroom off a 1000 door, opening in, its double bed against its west wall, its alarm, and its
    /// en-suite shower room in its north east corner (x 5.8 to 8.5 m, y 5.3 to 8 m) - a level shower, a 480 seat WC 500 off its wall, a finger
    /// rinse basin, an alarm - its 1000 door opening out into the bedroom.
    /// </summary>
    private static Hotel Build()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Hotel";
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++) Wall(document, corners[i], corners[(i + 1) % 4], "Exterior - Brick on Block 330mm");
        var corridor = Wall(document, new Point2D(0, 2000), new Point2D(12000, 2000));
        Wall(document, new Point2D(4000, 2000), new Point2D(4000, 8000));
        Wall(document, new Point2D(8500, 2000), new Point2D(8500, 8000));
        Wall(document, new Point2D(5800, 5300), new Point2D(5800, 8000));
        var suiteWall = Wall(document, new Point2D(5800, 5300), new Point2D(8500, 5300));
        Room(document, "Corridor", 6000, 1000);
        var first = Room(document, "Bedroom 1", 2000, 5000);
        var accessible = Room(document, "Accessible Bedroom", 5000, 3500);
        var suite = Room(document, "En-suite", 7200, 6650);
        Room(document, "Bedroom 3", 10250, 5000);
        Door(document, corridor, 2000, "Single - 900 x 2100");
        var door = Door(document, corridor, 6250, "Single - 1000 x 2100");
        Door(document, corridor, 10250, "Single - 1000 x 2100");
        var suiteDoor = Door(document, suiteWall, 1900, "Single - 1000 x 2100");
        Swing(document, door, accessible, into: true);
        Swing(document, suiteDoor, suite, into: false);

        Place(document, "Bed - Double 1350 x 1900", 4062.5 + 675, 4200);
        Place(document, "Bed - Double 1350 x 1900", 2000, 5000);
        Place(document, "Bed - Double 1350 x 1900", 10250, 5000);
        Place(document, "Emergency Assistance Alarm - Pull Cord", 4150, 6500);
        Place(document, "Shower - Level Access 1200 x 1200", 6500, 7200);
        Place(document, "WC - Close Coupled, 480 Seat", 8437.5 - 500, 7835 - 350);
        Place(document, "Basin - Finger Rinse 350", 8350, 6800);
        Place(document, "Emergency Assistance Alarm - Pull Cord", 8400, 6000);
        return new Hotel(document, first, accessible, suite, door);
    }

    [Fact]
    public void AHotelsBedroomsAreApprovedDocumentMVolume2s()
    {
        var hotel = Build();
        var document = hotel.Document;

        // Bedroom 1's 900 door gives 760 clear, off a corridor 1.77 m wide: 800 asked (Table 2) - and one bedroom of three accessible, met (4.24g).
        var first = Lodging.Verdict(document, hotel.First)!;
        Assert.StartsWith("Bedroom: a bed,", first);
        Assert.Contains("its door from the Corridor gives 760 clear - Approved Document M Volume 2 (4.24a, Table 2) asks 800 at least off an access route 1500 or more wide", first);
        Assert.DoesNotContain("(4.24g)", first);

        // The accessible bedroom: its door, turning space, bed and alarm met.
        var accessible = Lodging.Verdict(document, hotel.Accessible)!;
        Assert.StartsWith("Wheelchair bedroom: a bed,", accessible);
        Assert.DoesNotContain(" asks", accessible);

        // Its en-suite, checked as the wheelchair-accessible shower room with a WC it is (4.24m, Diagram 24) - and its alarm taken out (5.18e, f).
        var suite = Toilets.Verdict(document, hotel.EnSuite)!;
        Assert.StartsWith("Wheelchair shower room: 1 WC, a shower, its basin,", suite);
        Assert.DoesNotContain(" asks", suite);
        document.Remove(document.All<Component>().First(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.AssistanceAlarm && piece.Location.X > 8000));
        Assert.Contains("no emergency assistance alarm - Approved Document M Volume 2 (5.4h, 5.18e, 5.18f)", Toilets.Verdict(document, hotel.EnSuite));

        // Not named accessible: none of its three bedrooms is (4.24g), and its en-suite is no concern of the Approved Documents'.
        hotel.Accessible.Name = "Bedroom 2";
        Assert.Contains("0 of its 3 bedrooms wheelchair-accessible - Approved Document M Volume 2 (4.24g) asks one for every 20 bedrooms or part of 20, 1", Lodging.Verdict(document, hotel.First));
        Assert.Null(Toilets.Verdict(document, hotel.EnSuite));

        // A hotel's bedroom is not held to a dwelling's space standard; a house's is.
        Assert.Null(Bedrooms.Verdict(document, hotel.First));
        document.ProjectInformation.BuildingType = "House";
        Assert.NotNull(Bedrooms.Verdict(document, hotel.First));
        Assert.Null(Lodging.Verdict(document, hotel.First));
    }

    [Fact]
    public void AWheelchairBedroomsDoorBedAndEnSuiteLeaveRoomToManoeuvre()
    {
        var hotel = Build();
        var document = hotel.Document;
        var standard = document.ProjectInformation.ClearanceStandard;

        // The accessible bedroom's bed: 1500 clear beside it, on its east side - met; bedroom 1's bed has no dwelling's spaces round it.
        Component Bed(Room room) => document.All<Component>().Single(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Bed && RoomsOf.Containing(document, piece.LevelId, piece.Location) == room);
        var transfer = Assert.Single(Clearances.Of(document, Bed(hotel.Accessible), standard));
        Assert.Equal("the transfer space beside the bed", transfer.Zone.What);
        Assert.StartsWith("Approved Document M Volume 2 (4.24l, Diagram 17)", transfer.Zone.Rule);
        Assert.Empty(transfer.In);
        Assert.Empty(Clearances.Of(document, Bed(hotel.First), standard));

        // Its en-suite's WC: a wheelchair user's transfer space beside it, not Approved Document T's activity space (4.24m).
        var wc = document.All<Component>().Single(piece => Bathrooms.IsWc(document.FindType<ComponentType>(piece.TypeId)!));
        var space = Assert.Single(Clearances.Of(document, wc, standard));
        Assert.Equal("the transfer space beside the WC", space.Zone.What);
        Assert.Empty(space.In);

        // Approved Document E's E2 on the wall between the bedroom and its en-suite - within the room for residential purposes - not on the
        // partition between two bedrooms, which is E1's (Table 0.1b).
        bool E2(Wall wall) => WallRules.Problems(document, wall, standard).Any(problem => problem.Rule.Contains("(E2, Table 0.2)"));
        Assert.True(E2(document.Walls.Single(wall => wall.Start == new Point2D(5800, 5300) && wall.End == new Point2D(8500, 5300))));
        Assert.False(E2(document.Walls.Single(wall => wall.Start == new Point2D(4000, 2000) && wall.End == new Point2D(4000, 8000))));

        // Its corridor door moved west, to 100 off the partition, opening in: with its latch by the partition, 38 to the wall - 300 asked (3.10c).
        hotel.AccessibleDoor.DistanceAlongWall = 4600;
        Swing(document, hotel.AccessibleDoor, hotel.Accessible, into: true);
        var said = new[] { false, true }.Select(hand =>
        {
            hotel.AccessibleDoor.FlipHand = hand;
            Swing(document, hotel.AccessibleDoor, hotel.Accessible, into: true);
            return Lodging.Verdict(document, hotel.Accessible)!;
        }).ToList();
        Assert.Single(said, each => each.Contains("38 between its door's leading edge and the wall beside it - Approved Document M Volume 2 (4.24j, 3.10c) asks 300 at least"));

        // The en-suite given a bath for its shower: more bathrooms than shower rooms among the accessible bedrooms' en-suites (4.19).
        document.Remove(document.All<Component>().Single(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Shower));
        Place(document, "Bath - 1700 x 700", 6800, 7550);
        Assert.Contains("its wheelchair-accessible bedrooms have 1 en-suite bathrooms and 0 shower rooms - Approved Document M Volume 2 (4.19)", Lodging.Verdict(document, hotel.Accessible));
    }

    [Fact]
    public void ASocketInAHotelIs400To1000AboveTheFloor()
    {
        var hotel = Build();
        var document = hotel.Document;
        var standard = document.ProjectInformation.ClearanceStandard;

        // A double socket on bedroom 1's north wall at 150: its middle 193 - 400 to 1000 asked (4.30a), not M4(2)'s 450 to 1200.
        var socket = Place(document, "Socket - Double", 2000, 7829, 150);
        Assert.Contains(Services.Problems(document, socket, standard), problem => problem.Rule.StartsWith("Approved Document M Volume 2 (4.30a, g)", StringComparison.Ordinal));
        socket.Elevation = 400;
        Assert.DoesNotContain(Services.Problems(document, socket, standard), problem => problem.Rule.Contains("4.30"));

        // A light switch at 1150 to its middle: 900 to 1100 asked for the public (4.30h).
        var light = Place(document, "Switch - One Gang", 3800, 2100, 1100);
        Assert.Contains(Services.Problems(document, light, standard), problem => problem.Rule.StartsWith("Approved Document M Volume 2 (4.30h)", StringComparison.Ordinal));
    }
}
