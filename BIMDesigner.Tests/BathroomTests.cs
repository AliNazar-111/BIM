using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using PartFinish = BIMDesigner.Core.Views.ComponentModel.PartFinish;

namespace BIMDesigner.Tests;

/// <summary>
/// The bathroom: basins and WCs at the heights they are fitted at, a wall-hung WC, a level-access
/// shower; the electrics kept out of a bath's zones as BS 7671 and the NEC ask; and a WC with its
/// basin, never in a kitchen, as Approved Document G asks.
/// </summary>
public class BathroomTests
{
    private static ComponentType Type(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(type => type.Name == name);

    /// <summary>
    /// Two rooms side by side, 2 m by 3 m each inside, walled all round with a partition between:
    /// the left (0 to 2000) and the right (2000 to 4000); a door between them, and one from the
    /// right one outside. Their walls, left to right: south, east, north, west, the partition.
    /// </summary>
    private static (BimDocument Document, Room Left, Room Right, List<Wall> Walls) Pair()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(4000 + half, -half), new Point2D(4000 + half, 3000 + half), new Point2D(-half, 3000 + half) };
        var walls = new List<Wall>();
        Wall Wall(Point2D start, Point2D end)
        {
            var wall = new Wall { Start = start, End = end, TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 };
            document.Add(wall);
            walls.Add(wall);
            return wall;
        }

        for (var i = 0; i < 4; i++) Wall(corners[i], corners[(i + 1) % 4]);
        var partition = Wall(new Point2D(2000 + half, -half), new Point2D(2000 + half, 3000 + half));

        var single = document.TypesOf<DoorType>().First(type => type.Name.StartsWith("Single"));
        document.Add(new Door { TypeId = single.Id, LevelId = level, HostWallId = partition.Id, DistanceAlongWall = 1500, Mark = "D1" });
        document.Add(new Door { TypeId = single.Id, LevelId = level, HostWallId = walls[1].Id, DistanceAlongWall = 1500, Mark = "D2" });

        var left = new Room { LevelId = level, Location = new Point2D(1000, 1500), Name = "WC", Number = "1" };
        var right = new Room { LevelId = level, Location = new Point2D(3100, 1500), Name = "Lobby", Number = "2" };
        document.Add(left);
        document.Add(right);
        return (document, left, right, walls);
    }

    private static Component Put(BimDocument document, string name, double x, double y, double rotation = 0, bool fit = true)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation };
        if (fit) ComponentPlacing.FitToWalls(document, piece);
        document.Add(piece);
        return piece;
    }

    /// <summary>A fitting hung on a wall at a point by it, at an elevation.</summary>
    private static Component Hang(BimDocument document, Wall wall, string name, Point2D at, double elevation)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id };
        Assert.True(piece.HostOn(document, wall, at));
        piece.Elevation = elevation;
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void BasinsAndWcsAreAtTheHeightsTheyAreFittedAt()
    {
        var document = BimDocument.CreateDefault();

        // A wall-hung basin's rim at 850, as a pedestal basin's is, on its trap - no pedestal under it.
        var hung = Type(document, "Basin - Wall Hung");
        Assert.Equal(850, hung.DefaultElevation + hung.Height);
        Assert.All(ComponentModel.Parts(hung).Where(part => part.Finish == PartFinish.Body && part.To - part.From > 200), part => Assert.True(part.Bottom > 0));
        Assert.Contains(ComponentModel.Parts(hung), part => part.Finish == PartFinish.Metal && part.Bottom == 0);
        Assert.Equal(850, Type(document, "Basin - Hand Rinse 400").DefaultElevation + Type(document, "Basin - Hand Rinse 400").Height);

        // A WC's seat at 420, close coupled or hung on the wall clear of the floor.
        Assert.Equal(420, ComponentModel.Parts(Type(document, "WC - Close Coupled")).Where(part => part.Front == 340).Max(part => part.Top), 6);
        var wallHung = Type(document, "WC - Wall Hung");
        Assert.Equal(ComponentForm.WallHungToilet, wallHung.Form);
        Assert.Equal(420, ComponentModel.Parts(wallHung).Max(part => part.Front == wallHung.Depth / 2 ? part.Top : 0), 6);
        Assert.True(ComponentModel.Parts(wallHung).Min(part => part.Bottom) >= 150);

        var level = Type(document, "Shower - Level Access 1200 x 1200");
        Assert.Equal((1200.0, 1200.0, 10.0), (level.Width, level.Depth, level.Height));
    }

    [Fact]
    public void AHandRinseBasinMayComeIntoAWcsZoneByOnly200()
    {
        var (document, _, _, walls) = Pair();
        var wc = Put(document, "WC - Close Coupled", 1000, 2700);

        // The zone, 1100 in front of the WC: its edge on the open side.
        var zone = Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM).Single().Zone.Outline;
        var bottom = zone.Min(p => p.Y);

        // A hand-rinse basin on the left wall coming 250 into the zone's foot is in the way; a wash basin coming 250 in is not.
        var rinse = Put(document, "Basin - Hand Rinse 400", 1000, bottom - 125 + 250, fit: false);
        Assert.Contains("Basin - Hand Rinse 400", Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM).Single().In);
        document.Remove(rinse);
        Put(document, "Basin - Pedestal", 1000, bottom - 225 + 250, fit: false);
        Assert.Empty(Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM).Single().In);
        Assert.NotEmpty(walls);
    }

    [Fact]
    public void SocketsAndSwitchesAreKeptFromTheBathAsBs7671AndTheNecAsk()
    {
        var (document, left, right, walls) = Pair();
        Put(document, "Bath - 1700 x 700", 1000, 2600);
        var standard = ClearanceStandard.ApprovedDocumentM;

        // A socket on the bathroom's south wall, 2 m from the bath: too near.
        var socket = Hang(document, walls[0], "Socket - Double", new Point2D(1000, 100), 450);
        var near = Assert.Single(Bathrooms.Problems(document, socket, standard));
        Assert.Contains("2.5 m", near.Rule);

        // On the far side of the partition, in the lobby, it is not in the bathroom.
        var beyond = Hang(document, walls[4], "Socket - Double", new Point2D(2400, 2600), 450);
        Assert.Empty(Bathrooms.Problems(document, beyond, standard));

        // A switch on the wall beside the bath, within its zone 2.
        var beside = Hang(document, walls[3], "Switch - One Gang", new Point2D(0, 2000), 1050);
        Assert.Contains(Bathrooms.Problems(document, beside, standard), problem => problem.Rule.Contains("zones 0, 1 and 2"));

        // Under the NEC: 3 ft, so the socket 2 m away is far enough - but the basin wants one within 3 ft of it.
        Assert.Empty(Bathrooms.Problems(document, socket, ClearanceStandard.Irc));
        var basin = Put(document, "Basin - Pedestal", 1000, 300, rotation: 180);
        Assert.Empty(Bathrooms.Problems(document, basin, ClearanceStandard.Irc));
        document.Remove(socket);
        Assert.Contains(Bathrooms.Problems(document, basin, ClearanceStandard.Irc), problem => problem.Rule.Contains("210.52(D)"));
        Assert.NotNull(left);
        Assert.NotNull(right);
    }

    [Fact]
    public void UnderTheIrcAShowerMustBe30InAcross()
    {
        var document = BimDocument.CreateDefault();
        var small = Type(document, "Shower Tray - 900 x 900").Duplicate("Shower Tray - 700 x 700");
        small.Width = small.Depth = 700;
        document.AddType(small);
        var shower = new Component { TypeId = small.Id, TypeKind = small.Kind, LevelId = document.Levels[0].Id };
        document.Add(shower);

        Assert.Contains(Bathrooms.Problems(document, shower, ClearanceStandard.Irc), problem => problem.Rule.Contains("P2708.1"));
        Assert.Empty(Bathrooms.Problems(document, shower, ClearanceStandard.ApprovedDocumentM));
    }

    [Fact]
    public void AWcWantsItsBasinInItsRoomOrItsOnlyWayInAndNeverInAKitchen()
    {
        var (document, left, right, _) = Pair();
        Put(document, "WC - Close Coupled", 1000, 2700);

        Assert.Equal("WC: WC - no basin - Approved Document G (G4, 4.10) asks for one in the WC's room, or in the room next to it that is its only way in and not a kitchen",
            Bathrooms.Verdict(document, left));
        Assert.Null(Bathrooms.Verdict(document, right));

        // A basin in the lobby that is its only way in will do.
        Put(document, "Basin - Pedestal", 3000, 2700);
        Assert.Equal("WC: WC, its basin in 2 Lobby", Bathrooms.Verdict(document, left));
        Assert.Contains(left.GetInstanceParameters(document), parameter => parameter.Name == "WC or Bathroom");

        // But not where the lobby is a kitchen.
        Put(document, "Sink Unit - 600", 3500, 2700);
        Assert.Contains("no basin", Bathrooms.Verdict(document, left));

        // And a WC in the kitchen itself is called out.
        var (kitchen, room, _, _) = Pair();
        Put(kitchen, "WC - Close Coupled", 600, 2700);
        Put(kitchen, "Basin - Pedestal", 1400, 2700);
        Put(kitchen, "Oven and Hob - 600", 1000, 300, rotation: 180);
        Assert.Contains("G4(3)", Bathrooms.Verdict(kitchen, room));
    }
}
