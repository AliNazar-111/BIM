using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// The hall in front of a door, by Approved Document M Volume 1's Table 1.1 (M4(1)) and Table 2.1 (M4(2)) - round 157: a door approached head
/// on wants a hall 900 wide; approached from the side, 750 clear off a hall 1200 wide, 775 off 1050, 800 off 900; what stands opposite a door
/// narrows the hall there (1.15b, 2.22b). And the step up into a visitable dwelling's front door, 150 at most (1.14c).
/// </summary>
public class HallWidthTests
{
    /// <summary>
    /// A landing <paramref name="wide"/> clear, 5 m long along x between partitions, a bedroom off its north side through a door at x 2000 and a
    /// study off its east end; the doors of a type.
    /// </summary>
    private static (BimDocument Document, Door Side, Door End, Room Landing) Landing(double wide, string door = "Single - 900 x 2100", ClearanceStandard standard = ClearanceStandard.ApprovedDocumentM, bool front = false)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = standard;
        var level = document.Levels[0].Id;
        var partition = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm");
        Wall Wall(double x1, double y1, double x2, double y2)
        {
            var wall = new Wall { Start = new Point2D(x1, y1), End = new Point2D(x2, y2), TypeId = partition.Id, LevelId = level, UnconnectedHeight = 2400 };
            document.Add(wall);
            return wall;
        }

        const double h = 62.5;
        Wall(-h, -h, 5000 + h, -h);
        var north = Wall(-h, wide + h, 5000 + h, wide + h);
        var west = Wall(-h, -h, -h, wide + h);
        var east = Wall(5000 + h, -h, 5000 + h, wide + h);
        Wall(-h, wide + h, -h, wide + 3000);
        Wall(-h, wide + 3000, 4000, wide + 3000);
        Wall(4000, wide + 3000, 4000, wide + h);
        Wall(5000 + h, -h, 8000, -h);
        Wall(8000, -h, 8000, wide + h);
        Wall(8000, wide + h, 5000 + h, wide + h);

        var type = document.TypesOf<DoorType>().First(each => each.Name == door);
        var side = new Door { TypeId = type.Id, LevelId = level, HostWallId = north.Id, DistanceAlongWall = 2000 + h };
        var end = new Door { TypeId = type.Id, LevelId = level, HostWallId = east.Id, DistanceAlongWall = wide / 2 + h };
        document.Add(side);
        document.Add(end);
        // A front door into it from the west, the outside door into a hall - the principal private entrance M4(1) finds the entrance storey by.
        if (front)
        {
            west.TypeId = document.TypesOf<WallType>().First(each => each.Name == "Exterior - Brick on Block 330mm").Id;
            document.Add(new Door { TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Front Door - 1000 x 2100").Id, LevelId = level, HostWallId = west.Id, DistanceAlongWall = wide / 2 + h });
        }

        var landing = new Room { LevelId = level, Location = new Point2D(2500, wide / 2), Name = front ? "Hall" : "Landing" };
        document.Add(landing);
        document.Add(new Room { LevelId = level, Location = new Point2D(2000, wide + 1500), Name = "Bedroom" });
        document.Add(new Room { LevelId = level, Location = new Point2D(6500, wide / 2), Name = "Study" });
        return (document, side, end, landing);
    }

    private static bool Asked(BimDocument document, Door door) =>
        Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).Any(problem => problem.Rule.Contains("Table 2.1") || problem.Rule.Contains("Table 1.1"));

    [Fact]
    public void TheTableAsksMoreOfADoorApproachedFromTheSideOffANarrowHall()
    {
        Assert.Equal(750, Visitable.Asked(true, 900));
        Assert.Null(Visitable.Asked(true, 880));
        Assert.Equal((750.0, 775.0, 800.0), (Visitable.Asked(false, 1200)!.Value, Visitable.Asked(false, 1100)!.Value, Visitable.Asked(false, 950)!.Value));

        // A 900 door set gives 760 clear: off a landing 1000 wide, approached from the side, 800 is asked; the same door at the landing's end,
        // approached head on, wants only the 900.
        var (document, side, end, landing) = Landing(1000);
        Assert.Equal(760, Doors.ClearWidth(document.FindType<DoorType>(side.TypeId)!), 0);
        var (hall, headOn, width) = Visitable.HallAt(document, side)!.Value;
        Assert.Same(landing, hall);
        Assert.False(headOn);
        Assert.Equal(1000, width, 0);
        Assert.True(Asked(document, side));
        Assert.True(Visitable.HallAt(document, end)!.Value.HeadOn);
        Assert.False(Asked(document, end));

        // 1100 wide: 775 asked - still short; 1250 wide: 750 - met.
        (document, side, _, _) = Landing(1100);
        Assert.Contains(Doors.Problems(document, side, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "it gives 760 clear off a landing 1100 wide, approached from the side.");
        (document, side, _, _) = Landing(1250);
        Assert.False(Asked(document, side));

        // A 1000 door set off the 1000 landing gives 860 clear: met.
        (document, side, _, _) = Landing(1000, "Single - 1000 x 2100");
        Assert.False(Asked(document, side));
    }

    [Fact]
    public void ARadiatorOppositeADoorNarrowsTheHallAndANarrowHallIsSaid()
    {
        // A radiator on the wall opposite the bedroom's door on a 1250 landing: the hall in front of it under 1200 - 760 is short.
        var (document, side, _, _) = Landing(1250);
        var radiator = document.TypesOf<ComponentType>().First(each => each.Name == "Radiator - 600 x 1000");
        document.Add(new Component { TypeId = radiator.Id, TypeKind = radiator.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(2000, radiator.Depth / 2), Rotation = 180, Elevation = 150 });
        Assert.True(Visitable.HallAt(document, side)!.Value.Width < 1200);
        Assert.True(Asked(document, side));

        // A landing 850 wide: under the 900 Table 2.1 and 2.22a ask of any hall.
        (document, side, _, _) = Landing(850);
        Assert.Contains(Doors.Problems(document, side, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "the landing in front of it is 850 clear, approached from the side.");

        // Under M4(1), only the entrance storey's doors to habitable rooms or the WC - this bedroom's.
        (document, side, _, _) = Landing(1000, standard: ClearanceStandard.ApprovedDocumentM1, front: true);
        Assert.Contains(Doors.Problems(document, side, ClearanceStandard.ApprovedDocumentM1), problem => problem.Rule.Contains("M4(1) 1.15a, Table 1.1"));
    }

    [Fact]
    public void AVisitableDwellingsFrontDoorIsOneStepOf150AtMost()
    {
        // A house 8 m by 6 m, its front door into a hall; the ground round it 300 below the floor - then 120.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM1;
        var level = document.Levels[0].Id;
        var outside = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var walls = new[] { (0.0, 0.0, 0.0, 6000.0), (0, 6000, 8000, 6000), (8000, 6000, 8000, 0), (8000, 0, 0, 0) }
            .Select(each => new Wall { Start = new Point2D(each.Item1, each.Item2), End = new Point2D(each.Item3, each.Item4), TypeId = outside.Id, LevelId = level, UnconnectedHeight = 3000 }).ToList();
        foreach (var wall in walls) document.Add(wall);
        var front = new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Front Door - 1000 x 2100").Id, LevelId = level, HostWallId = walls[3].Id, DistanceAlongWall = 4000 };
        document.Add(front);
        document.Add(new Room { LevelId = level, Location = new Point2D(4000, 3000), Name = "Hall" });

        Toposurface Ground(double below)
        {
            var surface = new Toposurface { LevelId = level };
            var points = new List<Point3D>();
            for (var x = -12000.0; x <= 20000; x += 4000)
            for (var y = -12000.0; y <= 18000; y += 4000)
                points.Add(new Point3D(x, y, -below));
            surface.SetPoints(points);
            document.Add(surface);
            return surface;
        }

        var ground = Ground(300);
        Assert.Equal(300, Visitable.StepUp(document, front)!.Value, 0);
        Assert.Contains(Doors.Problems(document, front, ClearanceStandard.ApprovedDocumentM1), problem => problem.Said == "it is 300 up from the ground outside it." && problem.Rule.Contains("1.14b, c"));
        document.Remove(ground);
        Ground(120);
        Assert.DoesNotContain(Doors.Problems(document, front, ClearanceStandard.ApprovedDocumentM1), problem => problem.Rule.Contains("1.14b, c"));
    }

    private static Component Stand(BimDocument document, string name, double x, double y, double rotation, double elevation = 0)
    {
        var type = document.TypesOf<ComponentType>().First(each => each.Name == name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation, Elevation = elevation };
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void AHallsNarrowestWidthIsTakenBetweenItsDoors()
    {
        // A landing 1000 wide between its two doors: 1000 at its narrowest, to within the measuring grid; nothing asked of it.
        var (document, _, _, landing) = Landing(1000);
        var (walls, clear) = Visitable.Narrowest(document, landing)!.Value;
        Assert.InRange(walls, 1000, 1030);
        Assert.Equal(walls, clear);
        var said = Circulation.Verdict(document, landing)!;
        Assert.StartsWith($"Hall: {walls:0} clear at its narrowest", said);
        Assert.DoesNotContain("asks", said);

        // 850 wide: M4(2) asks every hall and landing 900 (2.22a).
        (document, _, _, landing) = Landing(850);
        Assert.Contains("M4(2) 2.22a", Circulation.Verdict(document, landing));
        Assert.Contains(BIMDesigner.Core.Documents.Warnings.RoomShortfalls(document, landing), each => each.Contains("2.22a"));
    }

    [Fact]
    public void WhatStandsInAHallLeaves750()
    {
        // A radiator 100 deep on the landing's south wall between its doors: 900 left past it - nothing asked.
        var (document, _, _, landing) = Landing(1000);
        Stand(document, "Radiator - 600 x 1000", 3500, 50, 180, 150);
        var (walls, clear) = Visitable.Narrowest(document, landing)!.Value;
        Assert.True(clear < walls && clear >= 900);
        Assert.DoesNotContain("asks", Circulation.Verdict(document, landing));

        // A chest of drawers 450 deep there instead: 550 left - M4(2) asks 750 at any point (2.22b); its walls still 1000 apart.
        (document, _, _, landing) = Landing(1000);
        Stand(document, "Chest of Drawers - 750", 3500, 225, 180);
        var said = Circulation.Verdict(document, landing)!;
        Assert.Contains("M4(2) 2.22b", said);
        Assert.DoesNotContain("2.22a", said);

        // In a visitable dwelling, the entrance storey's hall - 1.15b; a room that is not a hall, nothing.
        (document, _, _, landing) = Landing(1000, standard: ClearanceStandard.ApprovedDocumentM1, front: true);
        Stand(document, "Chest of Drawers - 750", 3500, 225, 180);
        Assert.Contains("M4(1) 1.15b", Circulation.Verdict(document, landing));
        landing.Name = "Study";
        Assert.Null(Circulation.Verdict(document, landing));
    }
    [Fact]
    public void AnObstructionIsNotInFrontOfADoorNorLongerThan2M()
    {
        // A radiator on the wall opposite the bedroom's door on a 1250 landing: in the 1500 of hall Diagram 2.3 shades in front of it (2.22b).
        var (document, _, _, landing) = Landing(1250);
        Stand(document, "Radiator - 600 x 1000", 2000, 50, 180, 150);
        var said = Circulation.Verdict(document, landing)!;
        Assert.Contains("M4(2) 2.22b, Diagram 2.3) asks no localised obstruction opposite or close to a doorway - the radiator stands in front of the bedroom's door", said);

        // Moved along to x 3500, between the doors: clear of both - the end door asks only 300 beside its jambs.
        document.All<Component>().Single().Location = new Point2D(3500, 50);
        Assert.DoesNotContain("doorway", Circulation.Verdict(document, landing));

        // One 2400 long: longer than 2 m.
        var longType = new ComponentType("Radiator - 600 x 2400", BuiltInCategory.MechanicalEquipment, ComponentForm.Radiator, 2400, 100, 650);
        document.AddType(longType);
        document.All<Component>().Single().TypeId = longType.Id;
        Assert.Contains("asks a localised obstruction no longer than 2 m - the radiator is 2400 long", Circulation.Verdict(document, landing));
    }

    [Fact]
    public void AWheelchairUsersHallIs1050And1200WhereADoorIsMetFromTheSide()
    {
        // M4(3): a landing 1000 wide is under 3.24a's 1050; its bedroom's door, met from the side, under 3.24b's 1200.
        var (document, side, _, landing) = Landing(1000, "Single - 1000 x 2100", ClearanceStandard.ApprovedDocumentM3);
        Assert.Contains("M4(3) 3.24a) asks every hall and landing 1050 clear", Circulation.Verdict(document, landing));
        Assert.Contains(Doors.Problems(document, side, ClearanceStandard.ApprovedDocumentM3), problem => problem.Rule.Contains("M4(3) 3.24a, b"));

        // 1300 wide: both met; a radiator leaving 900 past it allowed, one leaving less not (3.24c, Diagram 3.4).
        (document, side, _, landing) = Landing(1300, "Single - 1000 x 2100", ClearanceStandard.ApprovedDocumentM3);
        Assert.DoesNotContain("asks", Circulation.Verdict(document, landing));
        Assert.DoesNotContain(Doors.Problems(document, side, ClearanceStandard.ApprovedDocumentM3), problem => problem.Rule.Contains("3.24a, b"));
        Stand(document, "Chest of Drawers - 750", 3500, 225, 180);
        Assert.Contains("M4(3) 3.24c, Diagram 3.4) asks a hall never narrower than 900", Circulation.Verdict(document, landing));
    }

    [Fact]
    public void AWheelchairUsersDoorHas200BesideItsFollowingEdge()
    {
        // The bedroom's door near the landing's west end, its hinge 100 from the wall that returns there: 3.24g asks 200.
        var (document, side, _, _) = Landing(1300, "Single - 1000 x 2100", ClearanceStandard.ApprovedDocumentM3);
        var type = document.FindType<DoorType>(side.TypeId)!;
        side.DistanceAlongWall = 62.5 + 100 + type.Width / 2;
        side.FlipHand = false;
        Assert.InRange(Doors.Nib(document, side, type, following: true)!.Value, 100, 105);
        Assert.Contains(Doors.Problems(document, side, ClearanceStandard.ApprovedDocumentM3), problem => problem.Rule.Contains("M4(3) 3.24g"));

        // Hung the other way, its hinge at the far jamb: nothing near it - and under M4(2), not asked at all.
        side.FlipHand = true;
        Assert.DoesNotContain(Doors.Problems(document, side, ClearanceStandard.ApprovedDocumentM3), problem => problem.Rule.Contains("3.24g"));
        side.FlipHand = false;
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.DoesNotContain(Doors.Problems(document, side, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("3.24g"));
    }
}
