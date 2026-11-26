using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A visitable dwelling, Approved Document M Volume 1's Category 1 - M4(1), what a new home in England meets where no planning condition asks
/// more (0.3) - as a choice beside M4(2) and M4(3) (round 148): a WC on the storey visitors use, 750 clear in front of it (1.17, Diagram 1.3),
/// its door opening outwards; 775 through the front door (1.14a) and 750 through the doors to the rooms (1.15a); switches and sockets 450 to
/// 1200 (1.18a); a ramped approach 10 m a flight to 1:15, 5 m to 1:12 (1.7a); steps up to the door allowed, as a stepped approach (1.8) - and
/// none of M4(2)'s landing, parking, nibs, kitchen, bedroom or bathroom spaces. M4(2)'s own WC on the entrance storey (2.27a, d) is asked too.
/// </summary>
public class VisitableTests
{
    /// <summary>
    /// A house 8 m by 6 m, drawn clockwise: on the ground floor a hall with a cloakroom off it on the west, the living room on the east, the
    /// front door into the hall; a WC against the cloakroom's back wall at <paramref name="wcAt"/> across, none where it is null.
    /// </summary>
    private static (BimDocument Document, Door Front, Door Living, Door Cloak, Room Cloakroom, Room Lounge) House(
        ClearanceStandard standard = ClearanceStandard.ApprovedDocumentM1, double? wcAt = 1000)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = standard;
        var ground = document.Levels[0].Id;
        var outside = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var inside = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm");
        Wall Wall(double x1, double y1, double x2, double y2, WallType type)
        {
            var wall = new Wall { Start = new Point2D(x1, y1), End = new Point2D(x2, y2), TypeId = type.Id, LevelId = ground, UnconnectedHeight = 3000 };
            document.Add(wall);
            return wall;
        }

        Wall(0, 0, 0, 6000, outside);
        Wall(0, 6000, 8000, 6000, outside);
        Wall(8000, 6000, 8000, 0, outside);
        var south = Wall(8000, 0, 0, 0, outside);
        var spine = Wall(2000, 0, 2000, 6000, inside);
        var cloak = Wall(0, 4200, 2000, 4200, inside);

        var single = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100");
        var front = new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Front Door - 1000 x 2100").Id, LevelId = ground, HostWallId = south.Id, DistanceAlongWall = 7000, Mark = "D1" };
        var living = new Door { TypeId = single.Id, LevelId = ground, HostWallId = spine.Id, DistanceAlongWall = 2000, Mark = "D2" };
        var toCloak = new Door { TypeId = single.Id, LevelId = ground, HostWallId = cloak.Id, DistanceAlongWall = 1000, Mark = "D3" };
        foreach (var door in new[] { front, living, toCloak }) document.Add(door);

        var hall = new Room { LevelId = ground, Location = new Point2D(1000, 2000), Name = "Hall", Number = "1" };
        var cloakroom = new Room { LevelId = ground, Location = new Point2D(1000, 5000), Name = "Cloakroom", Number = "2" };
        var lounge = new Room { LevelId = ground, Location = new Point2D(5000, 3000), Name = "Living Room", Number = "3" };
        foreach (var room in new[] { hall, cloakroom, lounge }) document.Add(room);

        // The WC's back to the outside wall's inner face, 165 in from its line: facing down the plan, rotation 0.
        if (wcAt is { } x) Put(document, "WC - Close Coupled", x, 6000 - 165 - 340);

        // The cloakroom's door opening out of it, into the hall.
        if (Doors.RoomsBeside(document, toCloak).Swing == cloakroom) toCloak.FlipFacing = !toCloak.FlipFacing;
        return (document, front, living, toCloak, cloakroom, lounge);
    }

    private static Component Put(BimDocument document, string name, double x, double y, double rotation = 0, int level = 0)
    {
        var type = document.TypesOf<ComponentType>().First(each => each.Name == name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[level].Id, Location = new Point2D(x, y), Rotation = rotation };
        document.Add(piece);
        return piece;
    }

    private static List<string> Said(BimDocument document, Door door) =>
        Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).Select(problem => $"{problem.Said} {problem.Rule}").ToList();

    [Fact]
    public void M41IsOfferedFirstKeptInTheFileAndAskedInTheNewProjectSteps()
    {
        Assert.Equal("Approved Document M (M4(1))", Clearances.Label(ClearanceStandard.ApprovedDocumentM1));
        Assert.Equal(ClearanceStandard.ApprovedDocumentM1, ClearanceStandards.Offered[0]);
        Assert.Equal(Enum.GetValues<ClearanceStandard>().Length, ClearanceStandards.Offered.Distinct().Count());
        Assert.False(ClearanceStandard.ApprovedDocumentM1.IsUs());

        // Saved and opened again by its name.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM1;
        Assert.Equal(ClearanceStandard.ApprovedDocumentM1, ProjectFile.FromJson(ProjectFile.ToJson(document)).ProjectInformation.ClearanceStandard);

        // England's house with no planning condition: M4(1); with one, M4(2) as before; the wheelchair user home stays M4(3).
        Assert.Equal(ClearanceStandard.ApprovedDocumentM1, ProjectTemplates.Create(new NewProjectSpec { Visitable = true }).ProjectInformation.ClearanceStandard);
        Assert.Equal(ClearanceStandard.ApprovedDocumentM, ProjectTemplates.Create(new NewProjectSpec()).ProjectInformation.ClearanceStandard);
        Assert.Equal(ClearanceStandard.ApprovedDocumentM3,
            ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.EnglandWheelchairHouse, Visitable = true }).ProjectInformation.ClearanceStandard);
    }

    [Fact]
    public void TheFrontDoorIsTheOneIntoTheHallAndItsStoreyTheOneVisitorsUse()
    {
        var (document, front, _, _, _, living) = House();
        Assert.Equal(front, Visitable.Entrance(document));
        Assert.Equal(document.Levels[0].Id, Visitable.EntranceStorey(document));
        Assert.Equal(new[] { document.Levels[0].Id }, Visitable.Storeys(document));

        // A garage below and the living room upstairs: the principal storey is the first floor, and the WC may be on either (1.13, 1.17a).
        living.Name = "Garage";
        var first = document.Levels[1].Id;
        var outside = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        foreach (var (a, b) in new[] { ((0.0, 0.0), (0.0, 6000.0)), ((0.0, 6000.0), (8000.0, 6000.0)), ((8000.0, 6000.0), (8000.0, 0.0)), ((8000.0, 0.0), (0.0, 0.0)) })
            document.Add(new Wall { Start = new Point2D(a.Item1, a.Item2), End = new Point2D(b.Item1, b.Item2), TypeId = outside.Id, LevelId = first, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = first, Location = new Point2D(4000, 3000), Name = "Living Room", Number = "4" });
        Assert.Equal(new[] { document.Levels[0].Id, first }, Visitable.Storeys(document));
        Assert.Equal(first, Visitable.Storey(document));
        Assert.Empty(Visitable.Problems(document));
    }

    [Fact]
    public void AVisitableHousesWcHas750ClearInFrontOfItWhereM42Asks1100()
    {
        var (document, _, _, _, _, _) = House();
        var wc = document.All<Component>().Single();

        // 750 in front of the pan, 850 to 900 wide: clear in the 1.57 m deep cloakroom.
        var zone = Assert.Single(Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM1));
        Assert.Empty(zone.In);
        Assert.Contains("M4(1) 1.17b, Diagram 1.3", zone.Zone.Rule);
        var ys = zone.Zone.Outline.Select(point => point.Y).ToList();
        Assert.Equal(750, ys.Max() - ys.Min(), 6);
        Assert.Equal(6000 - 165 - 680, ys.Max(), 6);
        Assert.Empty(Clearances.Blocked(document, document.Levels[0].Id, ClearanceStandard.ApprovedDocumentM1));

        // M4(2)'s 1100 runs into the cloakroom's wall.
        Assert.NotEmpty(Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM).Single().In);

        // A basin, a bath, a bed and a kitchen have nothing asked round them under M4(1); a WC upstairs has no zone.
        foreach (var name in new[] { "Basin - Pedestal", "Bath - 1700 x 700", "Shower Tray - 900 x 900", "Bed - Double 1350 x 1900", "Sink Unit - 1000", "Counter - Kitchen 1800" })
        {
            var type = document.TypesOf<ComponentType>().Single(each => each.Name == name);
            Assert.False(Clearances.Needs(type, ClearanceStandard.ApprovedDocumentM1), name);
            Assert.True(Clearances.Needs(type, ClearanceStandard.ApprovedDocumentM), name);
        }

        var upstairs = Put(document, "WC - Close Coupled", 4000, 3000, level: 1);
        Assert.Empty(Clearances.Of(document, upstairs, ClearanceStandard.ApprovedDocumentM1));

        // In a block of flats each flat's WC has its zone, whatever storey the flat is on.
        document.ProjectInformation.BuildingType = "Flats";
        Assert.Single(Clearances.Of(document, upstairs, ClearanceStandard.ApprovedDocumentM1));
    }

    [Fact]
    public void ABasinMayComeIntoTheWcsZoneBy100AsDiagram14Has()
    {
        // A hand rinse basin on the cloakroom's side wall, 250 out from its face at 165.
        var (document, _, _, _, _, _) = House(wcAt: 650);
        Put(document, "Basin - Hand Rinse 400", 165 + 125, 4700, rotation: 90);
        var wc = document.All<Component>().First(piece => document.FindType<ComponentType>(piece.TypeId) is { } type && Bathrooms.IsWc(type));

        // The WC's centreline 650 across: its zone 400 to the left, at 250 - the basin 165 into it.
        var zone = Assert.Single(Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM1));
        Assert.Equal("Basin - Hand Rinse 400", Assert.Single(zone.In));

        // At 760, the zone's edge at 360: the basin 55 into it, within the 100 Diagram 1.4 shows.
        wc.Location = new Point2D(760, wc.Location.Y);
        Assert.Empty(Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM1).Single().In);
    }

    [Fact]
    public void TheStoreyVisitorsUseHasAWcAndM42AWcAndBasin()
    {
        // No WC downstairs: said of the front door (1.17a).
        var (document, front, _, _, _, _) = House(wcAt: null);
        var said = Assert.Single(Visitable.Problems(document));
        Assert.Equal(front, said.Element);
        Assert.Contains("M4(1) 1.17a", said.Said);
        Assert.Contains(Warnings.Regulations(document), warning => warning.Said.Contains("M4(1) 1.17a"));

        // One upstairs does not do.
        Put(document, "WC - Close Coupled", 4000, 3000, level: 1);
        Assert.Single(Visitable.Problems(document));
        Put(document, "WC - Close Coupled", 1000, 6000 - 165 - 340);
        Assert.Empty(Visitable.Problems(document));
        Assert.DoesNotContain(Warnings.Regulations(document), warning => warning.Said.Contains("1.17a"));

        // M4(2): a WC and a basin in the room (2.27a).
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.Contains("M4(2) 2.27a", Assert.Single(Visitable.Problems(document)).Said);
        Put(document, "Basin - Hand Rinse 400", 165 + 125, 4700, rotation: 90);
        Assert.Empty(Visitable.Problems(document));

        // Not a block of flats, nor an extension, nor a US house.
        document.ProjectInformation.BuildingType = "Extension";
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM1;
        document.All<Component>().ToList().ForEach(piece => document.Remove(piece));
        Assert.Empty(Visitable.Problems(document));
    }

    [Fact]
    public void TheWcRoomsDoorOpensOutwards()
    {
        var (document, _, _, cloak, cloakroom, _) = House();
        Assert.DoesNotContain(Said(document, cloak), each => each.Contains("open outwards"));

        // Turned to open into the cloakroom (1.17d).
        cloak.FlipFacing = !cloak.FlipFacing;
        Assert.Equal(cloakroom, Doors.RoomsBeside(document, cloak).Swing);
        Assert.Contains(Said(document, cloak), each => each.StartsWith("it opens into Cloakroom, the room with the WC on", StringComparison.Ordinal) && each.Contains("M4(1) 1.17d"));

        // M4(2) asks the same of the room with the WC and basin (2.27d).
        Put(document, "Basin - Hand Rinse 400", 165 + 125, 4700, rotation: 90);
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.Contains(Said(document, cloak), each => each.Contains("M4(2) 2.27d"));

        // Not under M4(3), nor the US's.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM3;
        Assert.DoesNotContain(Said(document, cloak), each => each.Contains("open outwards"));
    }

    [Fact]
    public void TheFrontDoorGives775AndTheRoomsDoors750()
    {
        var (document, front, living, cloak, _, _) = House();

        // The 1000 front door gives 860; a 900 single, 760 - short of 775 (1.14a), not of M4(2)'s 850.
        Assert.Empty(Said(document, front));
        front.TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Id;
        Assert.Contains(Said(document, front), each => each.StartsWith("it gives 760 clear.", StringComparison.Ordinal) && each.Contains("M4(1) 1.14a"));
        Assert.DoesNotContain(Said(document, front), each => each.Contains("850"));

        // An 850 door gives 710: short of 750 into the living room and the cloakroom (1.15a, Table 1.1).
        var narrow = new DoorType("Single - 850 x 2100", 850, 2100) { Operation = DoorOperation.Swing };
        document.AddType(narrow);
        living.TypeId = cloak.TypeId = narrow.Id;
        Assert.Contains(Said(document, living), each => each.StartsWith("it gives 710 clear.", StringComparison.Ordinal) && each.Contains("M4(1) 1.15a"));
        Assert.Contains(Said(document, cloak), each => each.Contains("M4(1) 1.15a"));

        // No nib is asked beside a door under M4(1).
        Assert.DoesNotContain(Doors.Problems(document, living, ClearanceStandard.ApprovedDocumentM1), problem => problem.Rule.Contains("nib"));
    }

    [Fact]
    public void SwitchesAndSocketsGo450To1200WithNoCornerAndNoHeatingControlHeight()
    {
        var document = BimDocument.CreateDefault();
        ComponentType Type(string name) => document.TypesOf<ComponentType>().First(type => type.Name == name);

        var socket = Services.Reach(Type("Socket - Double"), ClearanceStandard.ApprovedDocumentM1);
        Assert.Equal((450.0, 1200.0, 0.0), (socket!.Value.Least, socket.Value.Most, socket.Value.Corner));
        Assert.Contains("M4(1) 1.18a", socket.Value.Rule);
        Assert.Equal(300, Services.Reach(Type("Socket - Double"), ClearanceStandard.ApprovedDocumentM)!.Value.Corner);

        Assert.Null(Services.Reach(Type("Room Thermostat"), ClearanceStandard.ApprovedDocumentM1));
        Assert.Equal((1350.0, 1450.0), (Services.Reach(Type("Consumer Unit - 12 Way"), ClearanceStandard.ApprovedDocumentM1)!.Value.Least,
            Services.Reach(Type("Consumer Unit - 12 Way"), ClearanceStandard.ApprovedDocumentM1)!.Value.Most));
    }

    [Fact]
    public void ARampedApproachGoes10mAFlightTo1In15And5mTo1In12()
    {
        var document = BimDocument.CreateDefault();
        var ramp = new Ramp { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RampType>().First().Id, Start = new Point2D(0, 0), Angle = 90, UnconnectedHeight = 450, Gradient = 15, Width = 900 };
        document.Add(ramp);

        double Longest(ClearanceStandard standard)
        {
            document.ProjectInformation.ClearanceStandard = standard;
            return Ramps.Rules(document, ramp).MaxGoing;
        }

        // 1:15: 10 m (1.7a), where M4(2)'s Diagram 2.1 allows 5 m; 1:13, 5 m, where M4(2)'s goes 3 m.
        Assert.Equal(10000, Longest(ClearanceStandard.ApprovedDocumentM1), 6);
        Assert.Equal(5000, Longest(ClearanceStandard.ApprovedDocumentM), 6);
        ramp.Gradient = 13;
        Assert.Equal(5000, Longest(ClearanceStandard.ApprovedDocumentM1), 6);
        Assert.Equal(3000, Longest(ClearanceStandard.ApprovedDocumentM), 6);

        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM1;
        Assert.Equal((900.0, "Approved Document M asks every flight for 900 clear (M4(1) 1.7b)"), Ramps.LeastWidth(document, ramp));
    }

    [Fact]
    public void StepsUpToTheOnlyDoorAreASteppedApproachUnderM41()
    {
        var (document, _) = StairTests.House();
        var doorType = document.TypesOf<DoorType>().First(type => type.Function == DoorFunction.Exterior);
        var front = document.Walls.First(wall => wall.Start.Y == 0 && wall.End.Y == 0);
        front.BaseOffset = 510;
        document.Add(new Door { TypeId = doorType.Id, LevelId = front.LevelId, HostWallId = front.Id, DistanceAlongWall = 3000, Mark = "D1" });
        var steps = StairTests.Put(document, 3000, -1200);
        (steps.TopLevelId, steps.UnconnectedHeight, steps.Outside, steps.Railings) = (null, 510, true, StairRailings.BothSides);

        // M4(2) asks a step-free way in; M4(1) takes steps where it cannot be had (1.4, 1.8) - but 75 to 150 risers and 280 goings.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM1;
        var said = Stairs.Problems(document, steps);
        Assert.DoesNotContain(said, problem => problem.Contains("step-free"));
        Assert.Contains(said, problem => problem.StartsWith("Each riser is 170:", StringComparison.Ordinal) && problem.Contains("M4(1) 1.8a"));
        Assert.Contains(said, problem => problem.Contains("goings of 280 at least (M4(1) 1.8a)"));
        Assert.DoesNotContain(said, problem => problem.Contains("1.8d"));

        // Narrower than 900: 1.8d.
        steps.Width = 800;
        Assert.Contains(Stairs.Problems(document, steps), problem => problem.Contains("M4(1) 1.8d"));
    }

    [Fact]
    public void M41AsksNoEntranceLandingNorCoverNorPrincipalWindow()
    {
        var (document, front, _, _, _, _) = House();
        var path = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Path, Approach = true };
        path.SetBoundary(new[] { new Point2D(600, -3000), new Point2D(1400, -3000), new Point2D(1400, -200), new Point2D(600, -200) });
        document.Add(path);
        Assert.True(Approaches.IsApproachedEntrance(document, front));

        // M4(2) asks a covered landing 1200 square at the door (2.20); M4(1), its width and threshold only.
        Assert.Contains(Approaches.EntranceProblems(document, front, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("covered"));
        Assert.Empty(Approaches.EntranceProblems(document, front, ClearanceStandard.ApprovedDocumentM1));
    }
}
