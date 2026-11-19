using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Toilets in buildings other than dwellings by Approved Document T (round 100): rooms read as its Types A to D, single-sex toilets for men and
/// women, a wheelchair-accessible toilet, sizes and doors, where a universal toilet opens, cubicles opening into single-sex toilets, urinals.
/// </summary>
[Collection("Wpf")]
public class ToiletsTests
{
    private static Wall Wall(BimDocument document, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = "Single - 900 x 2100")
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

    private static Component Place(BimDocument document, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Elevation = kind.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    /// <summary>The rooms of an office: its corridor, the office, the women's toilets and their cubicles, a universal toilet, an accessible WC, stores.</summary>
    private sealed record Office(BimDocument Document, Room Corridor, Room Washroom, Room Cubicle, Room Universal, Room Accessible, Room Store, Wall CorridorWall, Door UniversalDoor);

    /// <summary>
    /// An office 12 m by 8 m: a corridor along its south side (y 0 to 2 m); the office west of x 6 m; the women's toilets (x 6 to 9 m, y 2 to
    /// 4.5 m) with a basin, opening off the corridor, two cubicles north of them (y 4.5 to 6.5 m) each with a WC opening into them; a universal
    /// toilet (x 9 to 10.2 m) with a WC, basin and hand dryer and an accessible WC (x 10.2 to 12 m) off the corridor; stores behind. The second cubicle
    /// is the ambulant one.
    /// </summary>
    private static Office Build()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++) Wall(document, corners[i], corners[(i + 1) % 4], "Exterior - Brick on Block 330mm");
        var corridor = Wall(document, new Point2D(0, 2000), new Point2D(12000, 2000));
        Wall(document, new Point2D(6000, 2000), new Point2D(6000, 8000));
        Wall(document, new Point2D(9000, 2000), new Point2D(9000, 8000));
        var cubicles = Wall(document, new Point2D(6000, 4500), new Point2D(9000, 4500));
        Wall(document, new Point2D(7500, 4500), new Point2D(7500, 6500));
        Wall(document, new Point2D(6000, 6500), new Point2D(9000, 6500));
        Wall(document, new Point2D(10200, 2000), new Point2D(10200, 4500));
        Wall(document, new Point2D(9000, 4500), new Point2D(12000, 4500));

        Door(document, corridor, 3000);
        Door(document, corridor, 7500);
        var universalDoor = Door(document, corridor, 9750);
        var accessibleDoor = Door(document, corridor, 11250);
        Door(document, cubicles, 750);
        Door(document, cubicles, 2250);

        var rooms = new Office(document, Room(document, "Corridor", 3000, 1000), Room(document, "Women's Toilets", 7500, 3250), Room(document, "WC 1", 6750, 5500),
            Room(document, "Universal WC", 9750, 3250), Room(document, "Accessible WC", 11250, 3250), Room(document, "Store", 10500, 6250), corridor, universalDoor);
        Room(document, "Office", 3000, 5000);
        Room(document, "Ambulant WC", 8250, 5500);
        Room(document, "Cleaner's Store", 7500, 7250);

        Place(document, "Basin - Wall Hung", 7500, 2400);
        Place(document, "WC - Close Coupled", 6750, 6000);
        Place(document, "WC - Close Coupled", 8250, 6000);
        Place(document, "WC - Close Coupled", 9750, 4000);
        Place(document, "Basin - Hand Rinse 400", 9750, 2400);
        Place(document, "Hand Dryer - Wall Mounted", 10050, 3000);
        // The accessible WC as Diagrams 18 to 20 have it: a 480 seat, its centreline 500 off the outside wall's face, the finger rinse basin's rim at
        // 730, the alarm's pull cord, and its door opening out of it, clear of its turning space.
        Place(document, "WC - Close Coupled, 480 Seat", 11835 - 500, 4000);
        Place(document, "Basin - Finger Rinse 350", 11760, 3300);
        Place(document, "Emergency Assistance Alarm - Pull Cord", 11800, 3700);
        var sweep = DoorSwing.Sweeps(document, accessibleDoor).First();
        if (Polygon2D.Contains(rooms.Accessible.GetBoundary(document).Polygon, new Point2D(sweep.Average(point => point.X), sweep.Average(point => point.Y))))
            accessibleDoor.FlipFacing = !accessibleDoor.FlipFacing;
        return rooms;
    }

    private static List<string> Wanting(BimDocument document) => Toilets.Shortfalls(document).Select(each => $"{each.Room.Name}: {each.Verdict}").ToList();

    [Fact]
    public void AnOfficesToiletsAreReadAsTheApprovedDocumentsTypes()
    {
        var office = Build();
        var document = office.Document;

        // A universal toilet, Type B: its WC, basin and hand dryer, 1.075 by 2.375 m clear inside its 125 partitions - 1050 by 1685 asked.
        Assert.StartsWith("Type B universal toilet: 1 WC, its basin, hand-drying, 1.0", Toilets.Verdict(document, office.Universal));
        Assert.DoesNotContain(" asks", Toilets.Verdict(document, office.Universal));

        // A cubicle with no basin, its door into the women's toilets with theirs: Type D, women's.
        Assert.StartsWith("Type D cubicle, women: 1 WC, 1.3", Toilets.Verdict(document, office.Cubicle));
        // The accessible WC against the outside wall: 1.5725 by 2.375 m clear - Approved Document M Volume 2's 1500 by 2200 met (Diagram 18).
        Assert.StartsWith("Wheelchair toilet: 1 WC, its basin, 1.57 x 2.3", Toilets.Verdict(document, office.Accessible));
        _ = Toilets.Verdict(document, office.Accessible) is { } fine && fine.Contains(" asks") ? throw new Xunit.Sdk.XunitException(fine) : 0;
        Assert.DoesNotContain(" asks", Toilets.Verdict(document, office.Accessible));

        // Toilets for women and none for men (T1(1)), said once, on the first.
        var said = Assert.Single(Wanting(document));
        Assert.Contains("there are single-sex toilets for women and none for men - Approved Document T (T1(1)) asks reasonable provision for male and female single-sex toilets", said);

        // The store made the men's toilets, two WCs and a urinal open in it with a basin: both sexes now, but the men's WCs not each in a cubicle (2.3).
        office.Store.Name = "Men's Toilets";
        Place(document, "WC - Close Coupled", 10000, 7500);
        Place(document, "WC - Close Coupled", 11000, 7500);
        Place(document, "Urinal - Wall Hung Bowl", 11800, 6000);
        Place(document, "Basin - Wall Hung", 9300, 6000);
        said = Assert.Single(Wanting(document));
        Assert.StartsWith("Men's Toilets: Toilets, men: 2 WCs, a urinal, its basin,", said);
        Assert.Contains("its 2 WCs are not each in a cubicle or room of their own - Approved Document T (2.3, Sections 5 and 6)", said);

        // A urinal in the women's toilets: in men's toilets only (2.2).
        Place(document, "Urinal - Wall Hung Bowl", 8800, 3000);
        Assert.Contains(Wanting(document), each => each.StartsWith("Women's Toilets: Urinals, women: 0 WCs, a urinal", StringComparison.Ordinal) && each.Contains("Approved Document T (2.2) asks urinals only in male single-sex toilets"));

        // The men's toilets with no ambulant cubicle: one asked of each sex's (Approved Document M Volume 2 5.7c).
        Assert.Contains(Wanting(document), each => each.StartsWith("Men's Toilets:", StringComparison.Ordinal) && each.Contains("no ambulant cubicle in the men's toilets - Approved Document M Volume 2 (5.7c, 5.14b)"));

        // No accessible WC: asked wherever there are toilets (Approved Document M Volume 2 5.7b; Approved Document T 1.14).
        office.Accessible.Name = "Staff WC";
        Assert.Contains(Wanting(document), each => each.Contains("no wheelchair-accessible unisex toilet in the building - Approved Document M Volume 2 (5.7b)"));
    }

    [Fact]
    public void AUniversalToiletIsBigEnoughHasItsHandDryingAndOpensOntoTheCorridor()
    {
        var office = Build();
        var document = office.Document;

        // Ambulant, Type A: 985 by 2125 met, but an 800 door gives 660 clear where 750 is asked (Table 3.1).
        office.Universal.Name = "Ambulant Universal WC";
        office.UniversalDoor.TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Single Glazed - 800 x 2100").Id;
        var verdict = Toilets.Verdict(document, office.Universal)!;
        Assert.StartsWith("Type A universal toilet:", verdict);
        Assert.Contains("its door gives 660 clear - Approved Document T (Table 3.1) asks 750 at least of a Type A's", verdict);

        // Its hand dryer taken away (T1(2), Table 3.1).
        document.Remove(document.All<Component>().Single(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.HandDryer));
        Assert.Contains("no hand-drying in it - Approved Document T (T1(2), Table 3.1) asks a washbasin, mirror and hand-drying within it", Toilets.Verdict(document, office.Universal));

        // A basin put in the corridor outside it: shared hand-washing (1.16).
        Place(document, "Basin - Wall Hung", 9750, 300);
        Assert.Contains("there is a basin in the Corridor outside it - Approved Document T (1.16)", Toilets.Verdict(document, office.Universal));

        // The corridor named the office's kitchenette: no longer a corridor or circulation space (1.18).
        office.Corridor.Name = "Kitchenette";
        Assert.Contains("it opens into the Kitchenette - Approved Document T (1.18, 2.8) asks a universal toilet to open onto a well-lit corridor or circulation space", Toilets.Verdict(document, office.Universal));

        // In a house it is no concern of Approved Document T's.
        document.ProjectInformation.BuildingType = "House";
        Assert.Null(Toilets.Verdict(document, office.Universal));
    }

    [Fact]
    public void ACubicleOpensIntoSingleSexToiletsWithTheirBasins()
    {
        var office = Build();
        var document = office.Document;

        // The women's toilets' basin taken out: the cubicles' users have nowhere to wash (Table 6.1).
        document.Remove(document.All<Component>().First(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Basin && piece.Location.X < 8000));
        Assert.Contains("no basin in the toilets it opens into - Approved Document T (Table 6.1) asks hand-washing in a single-sex communal space for those using a Type D", Toilets.Verdict(document, office.Cubicle));

        // Their name taken off them: the cubicle opens into a room for no one sex - neither single-sex nor universal (T1(2)).
        office.Washroom.Name = "Wash Room";
        Assert.Contains("a WC with no basin in its room, opening elsewhere than into single-sex toilets - Approved Document T (T1(2))", Toilets.Verdict(document, office.Cubicle));

        // The size of a room, squared to its longest side; Table 6.1's 910 by 1740.
        Assert.Equal((910, 1740, 650, "Table 6.1"), Toilets.Least('D'));
        Assert.Equal((1000, 2000), Toilets.Size(new[] { new Point2D(0, 0), new Point2D(2000, 0), new Point2D(2000, 1000), new Point2D(0, 1000) }));

        // The urinal and the hand dryer go out to IFC as what they are.
        Place(document, "Urinal - Wall Hung Bowl", 6500, 3000);
        using var model = BIMDesigner.Infrastructure.Interoperability.IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcSanitaryTerminal>(), terminal => terminal.PredefinedType == Xbim.Ifc4.Interfaces.IfcSanitaryTerminalTypeEnum.URINAL);
        Assert.Contains(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcElectricAppliance>(), appliance => appliance.PredefinedType == Xbim.Ifc4.Interfaces.IfcElectricApplianceTypeEnum.HANDDRYER);
    }

    [Fact]
    public void AnOfficesWcsAndBasinsHaveApprovedDocumentTsActivitySpacesNotADwellings()
    {
        var office = Build();
        var document = office.Document;
        var standard = document.ProjectInformation.ClearanceStandard;
        Component Fitting(Room room, ComponentForm form) => document.All<Component>().First(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == form
            && RoomsOf.Containing(document, piece.LevelId, piece.Location) == room);

        // The universal toilet's WC and basin: an activity space 800 by 600 in front of each (Tables 3.1 to 6.1) - not M4(2)'s 1100.
        var wc = Clearances.Of(document, Fitting(office.Universal, ComponentForm.Toilet), standard);
        Assert.Equal("the activity space in front of the WC", Assert.Single(wc).Zone.What);
        Assert.StartsWith("Approved Document T (Tables 3.1 to 6.1, 2.5)", wc[0].Zone.Rule);
        Assert.Equal(800 * 600, Polygon2D.Area(wc[0].Zone.Outline), 0);
        Assert.StartsWith("Approved Document T (Tables 3.1, 4.1, 2.5)", Assert.Single(Clearances.Of(document, Fitting(office.Universal, ComponentForm.Basin), standard)).Zone.Rule);

        // The women's toilets' basin, in the washroom: no space given by either Approved Document.
        Assert.Empty(Clearances.Of(document, Fitting(office.Washroom, ComponentForm.Basin), standard));

        // The accessible WC: the space beside it to transfer, clear to the back wall, 1000 from its centreline (Diagram 18).
        var transfer = Assert.Single(Clearances.Of(document, Fitting(office.Accessible, ComponentForm.Toilet), standard));
        Assert.Equal("the transfer space beside the WC", transfer.Zone.What);
        Assert.StartsWith("Approved Document M Volume 2 (5.8, 5.10i, Diagram 18)", transfer.Zone.Rule);

        // A WC put with its activity space over the cubicle door's swing: the swing is in its way (2.5).
        var door = document.All<Door>().First(each => Math.Abs(each.DistanceAlongWall - 750) < 1);
        var sweep = DoorSwing.Sweeps(document, door).First();
        var middle = new Point2D(sweep.Average(point => point.X), sweep.Average(point => point.Y));
        var placed = Place(document, "WC - Close Coupled", 0, 0);
        var frame = BIMDesigner.Core.Views.ComponentModel.FrameOf(placed, 0);
        var facing = frame.Plan(0, 1) - frame.Plan(0, 0);
        placed.Location = middle - facing * (340 + 300);
        Assert.Contains("the door's swing", Clearances.Of(document, placed, standard).Single(zone => zone.Zone.What == "the activity space in front of the WC").In);
        document.Remove(placed);

        // The 125 stud partition round the universal toilet, Rw 38: Approved Document E's E2 is for homes and rooms for residential purposes, not an office's toilets.
        var partition = document.Walls.Single(wall => Math.Abs(wall.Start.X - 10200) < 1 && Math.Abs(wall.End.X - 10200) < 1);
        bool E2(Wall wall) => WallRules.Problems(document, wall, standard).Any(problem => problem.Rule.Contains("(E2, Table 0.2)"));
        Assert.False(E2(partition));

        // The same rooms in a house: the dwelling's M4(2) spaces again, and E2.
        document.ProjectInformation.BuildingType = "House";
        Assert.StartsWith("Approved Document M (M4(2)) Diagram 2.5", Clearances.Of(document, Fitting(office.Universal, ComponentForm.Toilet), standard).Single().Zone.Rule);
        Assert.True(E2(partition));
    }

    [Fact]
    public void AWheelchairToiletIsDiagram18sSizeAndEachSexsToiletsHaveAnAmbulantCubicle()
    {
        var office = Build();
        var document = office.Document;

        // The accessible WC's partition moved east, to x 10.5 m: 1.2725 m wide where 1500 is asked (Diagram 18).
        var partition = document.Walls.Single(wall => Math.Abs(wall.Start.X - 10200) < 1 && Math.Abs(wall.End.X - 10200) < 1);
        (partition.Start, partition.End) = (new Point2D(10500, 2000), new Point2D(10500, 4500));
        Assert.Contains("1.27 x 2.38 m - Approved Document M Volume 2 (5.10i, Diagram 18) asks a wheelchair-accessible unisex toilet 1500 x 2200 at least", Toilets.Verdict(document, office.Accessible));

        // The only toilet in the building: 2000 wide, with a standing height basin as well as the finger rinse basin (5.7a, 5.10e).
        foreach (var piece in document.All<Component>().Where(piece => Bathrooms.IsWc(document.FindType<ComponentType>(piece.TypeId)!) && RoomsOf.Containing(document, piece.LevelId, piece.Location) != office.Accessible).ToList())
            document.Remove(piece);
        var only = Toilets.Verdict(document, office.Accessible)!;
        Assert.Contains("asks a wheelchair-accessible unisex toilet 2000 x 2200 at least, 2000 wide where it is the only toilet in the building", only);
        Assert.Contains("one basin - Approved Document M Volume 2 (5.7a, 5.10e) asks a standing height washbasin besides the finger rinse basin", only);

        // Each of the women's two cubicles halved: four cubicles, none 1200 wide - an enlarged one asked (5.7d, 5.14d).
        var four = Build();
        var women = four.Document;
        Wall(women, new Point2D(6750, 4500), new Point2D(6750, 6500));
        Wall(women, new Point2D(8250, 4500), new Point2D(8250, 6500));
        var front = women.Walls.Single(each => each.Start == new Point2D(6000, 4500) && each.End == new Point2D(9000, 4500));
        foreach (var (door, along) in women.All<Door>().Where(each => each.HostWallId == front.Id).OrderBy(each => each.DistanceAlongWall).Zip(new[] { 375.0, 1875 }))
            door.DistanceAlongWall = along;
        Door(women, front, 1125, "Single Glazed - 800 x 2100");
        Door(women, front, 2625, "Single Glazed - 800 x 2100");
        four.Cubicle.Location = new Point2D(6375, 5500);
        women.All<Room>().Single(room => room.Name == "Ambulant WC").Location = new Point2D(7875, 5500);
        Room(women, "WC 3", 7125, 5500);
        Room(women, "WC 4", 8625, 5500);
        foreach (var (wcAt, to) in women.All<Component>().Where(piece => Bathrooms.IsWc(women.FindType<ComponentType>(piece.TypeId)!) && piece.Location.Y > 5000).OrderBy(piece => piece.Location.X).Zip(new[] { 6375.0, 7875 }))
            wcAt.Location = new Point2D(to, 6000);
        Place(women, "WC - Close Coupled", 7125, 6000);
        Place(women, "WC - Close Coupled", 8625, 6000);
        Assert.Contains(Wanting(women), each => each.Contains("4 cubicles in the women's toilets, none 1200 wide - Approved Document M Volume 2 (5.7d, 5.14d)"));
    }

    [Fact]
    public void AWheelchairToiletsFittingsAreWhereDiagrams18To20PutThem()
    {
        var office = Build();
        var document = office.Document;
        string Said() => Toilets.Verdict(document, office.Accessible)!;
        Component Of(ComponentForm form) => document.All<Component>().First(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == form
            && RoomsOf.Containing(document, piece.LevelId, piece.Location) == office.Accessible);

        // A plain WC, its seat 420 - 780 high, drawn so (Diagram 19's 480 asked); put 700 off the wall (500 asked).
        var wc = Of(ComponentForm.Toilet);
        wc.TypeId = document.TypesOf<ComponentType>().First(type => type.Name == "WC - Close Coupled").Id;
        Assert.Contains("its WC's seat 420 high - Approved Document M Volume 2 (Diagram 19) asks 480", Said());
        wc.Location = new Point2D(11835 - 700, 4000);
        Assert.Contains("its WC's centreline 700 from the wall beside it - Approved Document M Volume 2 (Diagram 18) sets it 500 from that wall", Said());

        // The finger rinse basin raised to a standing height rim, 850 (720 to 740 asked from the WC).
        Of(ComponentForm.Basin).Elevation = 600;
        Assert.Contains("the finger rinse basin by the WC has its rim 850 high - Approved Document M Volume 2 (Diagrams 19, 20) asks 720 to 740", Said());

        // The alarm taken out (5.10m); the door turned to open in, over the turning space (5.3, Diagram 18).
        document.Remove(Of(ComponentForm.AssistanceAlarm));
        Assert.Contains("no emergency assistance alarm - Approved Document M Volume 2 (5.4h, 5.10m to o)", Said());
        var door = document.All<Door>().Single(each => Math.Abs(each.DistanceAlongWall - 11250) < 1);
        door.FlipFacing = !door.FlipFacing;
        Assert.Contains("no 1500 x 1500 turning space in it clear of the WC and the door's swing - Approved Document M Volume 2 (5.3, Diagram 18)", Said());

        // The universal toilet's hand dryer hung at 1200 (800 to 1000, Diagram 20).
        document.All<Component>().Single(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.HandDryer).Elevation = 1200;
        Assert.Contains("its hand dryer 1200 above the floor - Approved Document M Volume 2 (Diagram 20)", Toilets.Verdict(document, office.Universal));

        // Kept and opened again: the seat height; the alarm goes out to IFC as a manual pull box.
        var raised = document.TypesOf<ComponentType>().Single(type => type.Name == "WC - Close Coupled, 480 Seat");
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        Assert.Equal(480, opened.TypesOf<ComponentType>().Single(type => type.Name == raised.Name).SeatHeight);
        Place(document, "Emergency Assistance Alarm - Pull Cord", 11800, 3700);
        using var model = BIMDesigner.Infrastructure.Interoperability.IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcAlarm>(), alarm => alarm.PredefinedType == Xbim.Ifc4.Interfaces.IfcAlarmTypeEnum.MANUALPULLBOX);
    }

    [Fact]
    public void AWheelchairToiletIsWithin40mAndChangingPlacesAreWhereTheyAreAsked()
    {
        // An office 50 m long, its corridor along the south: the accessible WC at its west end, the office's door half way and its far end 48 m away.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var corners = new[] { new Point2D(0, 0), new Point2D(50000, 0), new Point2D(50000, 6000), new Point2D(0, 6000) };
        for (var i = 0; i < 4; i++) Wall(document, corners[i], corners[(i + 1) % 4], "Exterior - Brick on Block 330mm");
        var corridor = Wall(document, new Point2D(0, 2000), new Point2D(50000, 2000));
        Wall(document, new Point2D(1900, 2000), new Point2D(1900, 4500));
        Wall(document, new Point2D(0, 4500), new Point2D(1900, 4500));
        var door = Door(document, corridor, 1000);
        Door(document, corridor, 25000);
        Room(document, "Corridor", 25000, 1000);
        var accessible = Room(document, "Accessible WC", 950, 3250);
        Room(document, "Office", 25000, 4000);
        Place(document, "WC - Close Coupled, 480 Seat", 165 + 500, 4000);
        Place(document, "Basin - Finger Rinse 350", 300, 3300);
        Place(document, "Emergency Assistance Alarm - Pull Cord", 300, 3800);
        var sweep = DoorSwing.Sweeps(document, door).First();
        if (Polygon2D.Contains(accessible.GetBoundary(document).Polygon, new Point2D(sweep.Average(point => point.X), sweep.Average(point => point.Y))))
            door.FlipFacing = !door.FlipFacing;
        var said = Toilets.Verdict(document, accessible)!;
        Assert.Contains("the furthest point of the Office is 4", said);
        Assert.Contains("m from it, walked - Approved Document M Volume 2 (5.10h) keeps a wheelchair user within 40 m of one on the same floor", said);

        // A hospital: a changing places toilet asked (5.7e v); one named so in the building, met.
        document.ProjectInformation.BuildingType = "Hospital";
        Assert.Contains("no changing places toilet in the building, a hospital or primary care centre (v) - Approved Document M Volume 2 (5.6, 5.7e)", Toilets.Verdict(document, accessible));
        var office = document.All<Room>().Single(room => room.Name == "Office");
        office.Name = "Changing Places and Office";
        Assert.DoesNotContain("changing places toilet", Toilets.Verdict(document, accessible));

        // A shop of 300 m² is under 5.7e's 2500.
        document.ProjectInformation.BuildingType = "Retail Store";
        office.Name = "Shop Floor";
        Assert.DoesNotContain("changing places toilet", Toilets.Verdict(document, accessible));
    }

    /// <summary>Turns a door to open out of a room, where its leaf sweeps into it.</summary>
    private static void Outward(BimDocument document, Door door, Room room)
    {
        var sweep = DoorSwing.Sweeps(document, door).First();
        if (Polygon2D.Contains(room.GetBoundary(document).Polygon, new Point2D(sweep.Average(point => point.X), sweep.Average(point => point.Y))))
            door.FlipFacing = !door.FlipFacing;
    }

    /// <summary>
    /// A sports centre 14 m by 8 m: a corridor along the south (y 0 to 2 m); the men's changing (x 0 to 5 m) with a bank of lockers and the women's
    /// (x 5 to 10 m); an accessible shower room, x 10 to 12.4 m and y 2 to 4.4 m, its level shower, its alarm and its door opening out; a store
    /// east of it; and a bathroom over them both (y 4.4 to 8 m), reached through the store.
    /// </summary>
    private static (BimDocument Document, Room Shower, Room Bathroom, Room Men) Sports()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Sports Centre";
        var corners = new[] { new Point2D(0, 0), new Point2D(14000, 0), new Point2D(14000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++) Wall(document, corners[i], corners[(i + 1) % 4], "Exterior - Brick on Block 330mm");
        var corridor = Wall(document, new Point2D(0, 2000), new Point2D(14000, 2000));
        Wall(document, new Point2D(5000, 2000), new Point2D(5000, 8000));
        Wall(document, new Point2D(10000, 2000), new Point2D(10000, 8000));
        Wall(document, new Point2D(12400, 2000), new Point2D(12400, 4400));
        var middle = Wall(document, new Point2D(10000, 4400), new Point2D(14000, 4400));
        Door(document, corridor, 2500);
        Door(document, corridor, 7500);
        var showerDoor = Door(document, corridor, 11200);
        Door(document, corridor, 13200);
        var bathDoor = Door(document, middle, 3200);
        Room(document, "Corridor", 7000, 1000);
        var men = Room(document, "Men's Changing", 2500, 5000);
        Room(document, "Women's Changing", 7500, 5000);
        var shower = Room(document, "Accessible Shower Room", 11200, 3200);
        Room(document, "Store", 13200, 3200);
        var bathroom = Room(document, "Bathroom", 12000, 6200);
        Place(document, "Lockers - Bank of 4, 2 Tier", 2500, 7500);
        Place(document, "Shower - Level Access 1200 x 1200", 10700, 2700);
        Place(document, "Emergency Assistance Alarm - Pull Cord", 10100, 3600);
        Place(document, "Bath - 1700 x 700", 11000, 7500);
        Outward(document, showerDoor, shower);
        Outward(document, bathDoor, bathroom);
        return (document, shower, bathroom, men);
    }

    [Fact]
    public void AWheelchairUsersShowerChangingRoomAndBathroomAreDiagrams22To25()
    {
        var (document, shower, bathroom, men) = Sports();

        // The accessible shower room: 2.275 m square, Diagram 23's 2000 by 2200 met, its turning space over its level shower, its alarm.
        var said = Toilets.Verdict(document, shower)!;
        Assert.StartsWith("Wheelchair shower room: a shower, 2.28 x 2.28 m", said);
        Assert.DoesNotContain(" asks", said);

        // The alarm taken out (5.18e, f).
        document.Remove(document.All<Component>().Single(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.AssistanceAlarm));
        Assert.Contains("no emergency assistance alarm - Approved Document M Volume 2 (5.4h, 5.18e, 5.18f) asks one, its pull cord reachable from the tip-up seat", Toilets.Verdict(document, shower));

        // The men's changing: lockers with 1500 clear in front of them (5.18j); no M4(2) zone at the shower.
        var lockers = document.All<Component>().Single(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Locker);
        var zone = Assert.Single(Clearances.Of(document, lockers, document.ProjectInformation.ClearanceStandard));
        Assert.Equal("the manoeuvring space in front of the lockers", zone.Zone.What);
        Assert.StartsWith("Approved Document M Volume 2 (5.18j)", zone.Zone.Rule);
        Assert.Empty(Clearances.Of(document, document.All<Component>().Single(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Shower), document.ProjectInformation.ClearanceStandard));

        // Its name taken off: the showers have no wheelchair-accessible one (5.18l), and the sports centre's communal changing no self-contained one (5.18d).
        shower.Name = "Shower Room";
        Assert.Contains("no wheelchair-accessible shower room among the building's showers - Approved Document M Volume 2 (5.18l)", Toilets.Verdict(document, shower));
        Assert.Contains("no individual self-contained wheelchair-accessible changing or shower room - Approved Document M Volume 2 (5.18d)", Toilets.Verdict(document, men));
        shower.Name = "Accessible Shower Room";

        // The bathroom made accessible with a bath, a 480 seat WC 500 off its wall and an alarm: Diagram 25's 2500 by 2700 met - but it is the
        // building's only wheelchair-accessible WC (5.16).
        bathroom.Name = "Accessible Bathroom";
        Place(document, "WC - Close Coupled, 480 Seat", 14000 - 165 - 500, 5200);
        Place(document, "Emergency Assistance Alarm - Pull Cord", 13800, 6200);
        said = Toilets.Verdict(document, bathroom)!;
        Assert.StartsWith("Wheelchair bathroom: 1 WC, a bath, 3.37 x 3.77 m", said);
        Assert.Contains("it is the building's only wheelchair-accessible WC - Approved Document M Volume 2 (5.16)", said);
        Assert.DoesNotContain("Diagram 25) asks", said);
        Assert.DoesNotContain("no emergency assistance alarm", said);
    }

    [Fact]
    public void TwoWheelchairToiletsOfferLeftAndRightHandTransfer()
    {
        // The office's store made a second accessible WC, its WC 500 off the east wall as the first one's is: both for one hand (5.10d).
        var office = Build();
        var document = office.Document;
        office.Store.Name = "Accessible WC 2";
        var second = Place(document, "WC - Close Coupled, 480 Seat", 11835 - 500, 7300);
        Place(document, "Emergency Assistance Alarm - Pull Cord", 11800, 6000);
        Assert.Contains(Wanting(document), each => each.StartsWith("Accessible WC 2:", StringComparison.Ordinal) && each.Contains("Approved Document M Volume 2 (5.10d) asks a choice of layouts for left- and right-hand transfer"));

        // Moved 500 off the west wall instead: the other hand - a choice.
        second.Location = new Point2D(9062.5 + 500, 7300);
        Assert.DoesNotContain(Wanting(document), each => each.Contains("(5.10d)"));
    }
}
