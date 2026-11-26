using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A split-level house end to end (round 143): a wall from the lower level standing up through the levels over it bounds their rooms; a US
/// stair type; flights through an opening cut for them; an upper split level's smoke alarm doing for the level half a storey below it (IRC
/// R314.3); the half-sunk level a basement wanting an escape opening (R202, R310.1); no light asked of a hall (R303.1); and the ground's fall
/// read clear of the building at its corners.
/// </summary>
[Collection("Wpf")]
public class SplitLevelTests
{
    private const string Siding = "Timber Frame - Siding 183mm", Partition = "Interior - Partition 125mm", UsStair = "Stair - Wood, 7 3/4 in Rise, 10 in Tread";

    private sealed record House(BimDocument Document, Level Lower, Level Living, Level Upper, Wall Between, Room LivingRoom, Room Family, Room Hall, Stair Up, Stair Down);

    private static Guid TypeOf<T>(BimDocument document, string name) where T : Core.Elements.ElementType => document.TypesOf<T>().First(each => each.Name == name).Id;

    /// <summary>
    /// A US side split: Living at 0, Upper at 1400, Lower at -1200; the living part 8 m by 9 m on Living; the bedroom part 7 m by 9 m beside it
    /// on Lower and Upper; the wall between them a partition on Lower from -1200 to 4000, opened from Living's floor for the flights; a flight
    /// up from Living to Upper and one down to Lower; the ground 300 under Living where asked.
    /// </summary>
    private static House Build(bool ground = true, bool opening = true)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        var (living, upper) = (levels.First(level => level.Elevation >= 0), levels.First(level => level.Elevation > 1));
        upper.Elevation = 1400;
        var lower = new Level { Name = "Lower", Elevation = -1200 };
        document.AddLevel(lower);

        Wall Wall(Level level, double x0, double y0, double x1, double y1, string type, double height)
        {
            var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = TypeOf<WallType>(document, type), LevelId = level.Id, UnconnectedHeight = height };
            document.Add(wall);
            return wall;
        }

        Wall(living, 0, 0, 0, 9000, Siding, 2700);
        Wall(living, 0, 9000, 8000, 9000, Siding, 2700);
        Wall(living, 8000, 0, 0, 0, Siding, 2700);
        var between = Wall(lower, 8000, 0, 8000, 9000, Partition, 5200);
        Wall(lower, 8000, 9000, 15000, 9000, Siding, 2600);
        Wall(lower, 15000, 9000, 15000, 0, Siding, 2600);
        Wall(lower, 15000, 0, 8000, 0, Siding, 2600);
        Wall(upper, 8000, 9000, 15000, 9000, Siding, 2600);
        Wall(upper, 15000, 9000, 15000, 0, Siding, 2600);
        Wall(upper, 15000, 0, 8000, 0, Siding, 2600);
        Wall(upper, 8000, 4500, 15000, 4500, Partition, 2600);
        Wall(upper, 10200, 4500, 10200, 9000, Partition, 2600);
        if (opening) document.Add(new WallOpening { HostWallId = between.Id, LevelId = lower.Id, DistanceAlongWall = 6925, Width = 1950, SillHeight = 1200, Height = 2400 });

        // The flights from the wall's east face, 7 3/4 in risers and 10 in treads: 8 risers up 1400, 7 down 1200.
        const double face = 8063;
        var stair = TypeOf<StairType>(document, UsStair);
        var up = new Stair { TypeId = stair, LevelId = living.Id, TopLevelId = upper.Id, Start = new Point2D(face, 6450), Angle = 0, Width = 950 };
        var down = new Stair { TypeId = stair, LevelId = lower.Id, TopLevelId = living.Id, Start = new Point2D(face + 6 * 254, 7400), Angle = 180, Width = 950 };
        document.Add(up);
        document.Add(down);

        Floor Floor(Level level, params (double X, double Y)[] corners)
        {
            var floor = new Floor { LevelId = level.Id, TypeId = TypeOf<FloorType>(document, "Floor - Timber 220mm") };
            floor.SetBoundary(corners.Select(corner => new Point2D(corner.X, corner.Y)));
            document.Add(floor);
            return floor;
        }

        Floor(living, (0, 0), (8000, 0), (8000, 5975), (face, 5975), (face, 7875), (8000, 7875), (8000, 9000), (0, 9000));
        Floor(lower, (8000, 0), (15000, 0), (15000, 9000), (8000, 9000));
        Floor(upper, (8000, 0), (15000, 0), (15000, 9000), (8000, 9000), (8000, 7900), (face + 7 * 254, 7900), (face + 7 * 254, 5950), (8000, 5950));

        Room Room(Level level, string name, double x, double y)
        {
            var room = new Room { LevelId = level.Id, Name = name, Location = new Point2D(x, y) };
            document.Add(room);
            return room;
        }

        var livingRoom = Room(living, "Living Room", 4000, 4500);
        var family = Room(lower, "Family Room", 11500, 4500);
        Room(upper, "Bedroom 1", 11500, 2250);
        var hall = Room(upper, "Hall", 9100, 5200);
        if (ground)
        {
            var surface = new Toposurface { LevelId = living.Id };
            var points = new List<Point3D>();
            for (var x = -10000.0; x <= 25000; x += 2500)
            for (var y = -10000.0; y <= 19000; y += 2500)
                points.Add(new Point3D(x, y, -300 - 0.08 * Math.Max(0, Math.Max(Math.Max(-x, x - 15000), Math.Max(-y, y - 9000)))));
            surface.SetPoints(points);
            document.Add(surface);
        }

        return new House(document, lower, living, upper, between, livingRoom, family, hall, up, down);
    }

    [Fact]
    public void AWallStandingUpThroughALevelBoundsItsRoomsAndIsCutOnItsPlan()
    {
        var house = Build();
        var document = house.Document;

        // The wall between the parts, from Lower at -1200 to 4000: Living's and Upper's too; a Lower wall up to 1400 is not Living's.
        Assert.True(SplitLevels.StandsThrough(document, house.Between, house.Living.Id));
        Assert.True(SplitLevels.StandsThrough(document, house.Between, house.Upper.Id));
        Assert.False(SplitLevels.StandsThrough(document, house.Between, house.Lower.Id));
        Assert.DoesNotContain(document.Walls.Where(wall => wall.LevelId == house.Lower.Id && wall != house.Between), wall => SplitLevels.StandsThrough(document, wall, house.Living.Id));

        // The living room enclosed by it: 7846 by 8817 between the faces - the siding 183, the partition 125 - 69.18 m².
        var boundary = house.LivingRoom.GetBoundary(document);
        Assert.True(boundary.IsEnclosed);
        Assert.Equal(7846 * 8817 / 1e6, boundary.Area / 1e6, 2);
        Assert.True(house.Hall.GetBoundary(document).IsEnclosed);

        // A wall of Living's own along it: that one bounds the room, the tall one not counted twice.
        var own = new Wall { Start = new Point2D(8000, 0), End = new Point2D(8000, 9000), TypeId = TypeOf<WallType>(document, Partition), LevelId = house.Living.Id, UnconnectedHeight = 2700 };
        document.Add(own);
        Assert.False(SplitLevels.StandsThrough(document, house.Between, house.Living.Id));
        Assert.Equal(7846 * 8817 / 1e6, house.LivingRoom.GetBoundary(document).Area / 1e6, 2);

        // An ordinary two-storey house: a ground floor wall 3 m high stands through nothing.
        var plain = BimDocument.CreateDefault();
        var levels = plain.Levels.OrderBy(level => level.Elevation).ToList();
        var ground = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = TypeOf<WallType>(plain, Siding), LevelId = levels[0].Id, UnconnectedHeight = 3000 };
        plain.Add(ground);
        Assert.False(SplitLevels.StandsThrough(plain, ground, levels[1].Id));
    }

    [Fact]
    public void TheUsStairPassesTheIrcAndGoesThroughTheOpeningCutForIt()
    {
        var house = Build();
        var document = house.Document;
        var layout = Stairs.Layout(document, house.Up);
        Assert.Equal(8, layout.Risers);
        Assert.Equal(175, layout.Rise, 1);
        Assert.DoesNotContain(Stairs.Problems(document, house.Up), problem => problem.Contains("riser") || problem.Contains("tread") || problem.Contains("wide"));
        Assert.DoesNotContain(Stairs.Problems(document, house.Up), problem => problem.Contains("runs through a wall"));

        // Without the opening the flight runs into the wall between the parts.
        var shut = Build(opening: false);
        Assert.Contains(Stairs.Problems(shut.Document, shut.Up), problem => problem.StartsWith("It runs through a wall (Interior - Partition 125mm)"));
    }

    [Fact]
    public void AnUpperSplitLevelsSmokeAlarmDoesForTheLevelHalfAStoreyBelow()
    {
        var house = Build();
        var document = house.Document;
        Assert.True(SplitLevels.LessThanAStorey(document, house.Living, house.Upper));
        Assert.Same(house.Upper, SplitLevels.CoveredFrom(document, house.Living.Id));
        Assert.Same(house.Living, SplitLevels.CoveredFrom(document, house.Lower.Id));

        // An alarm in the hall on Upper: the living room asks none of its own; the family room, a level further down, still does.
        var alarm = document.TypesOf<ComponentType>().First(each => each.Name == "Smoke Alarm").Id;
        document.Add(new Component { LevelId = house.Upper.Id, TypeId = alarm, Location = new Point2D(9100, 5200), Elevation = 2550 });
        Assert.DoesNotContain("no smoke alarm on this storey", Alarms.Verdict(document, house.LivingRoom) ?? string.Empty);
        Assert.Contains("no smoke alarm on this storey", Alarms.Verdict(document, house.Family) ?? string.Empty);

        // A door in line with the up flight's head shuts Living off from Upper: its own alarm asked again.
        var hallWall = document.Walls.Single(wall => wall.LevelId == house.Upper.Id && Math.Abs(wall.Start.X - 10200) < 1);
        var doorWall = new Wall { Start = new Point2D(10000, 5950), End = new Point2D(10000, 6950), TypeId = TypeOf<WallType>(document, Partition), LevelId = house.Upper.Id, UnconnectedHeight = 2600 };
        document.Add(doorWall);
        document.Add(new Door { HostWallId = doorWall.Id, LevelId = house.Upper.Id, DistanceAlongWall = 500, TypeId = TypeOf<DoorType>(document, "Interior Door - 30 x 80 in") });
        Assert.Null(SplitLevels.CoveredFrom(document, house.Living.Id));
        Assert.Contains("no smoke alarm on this storey", Alarms.Verdict(document, house.LivingRoom) ?? string.Empty);
        Assert.NotNull(hallWall);

        // Storeys a full storey apart: no cover.
        var plain = BimDocument.CreateDefault();
        var levels = plain.Levels.OrderBy(level => level.Elevation).ToList();
        plain.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = TypeOf<WallType>(plain, Siding), LevelId = levels[0].Id, UnconnectedHeight = 3000 });
        Assert.False(SplitLevels.LessThanAStorey(plain, levels[0], levels[1]));
    }

    [Fact]
    public void TheHalfSunkLevelIsABasementWantingAnEscapeOpening()
    {
        var house = Build();
        var document = house.Document;

        // The grade plane, the ground 300 out from the outside walls' faces: -300 at the walls, falling 8% - -330 to -350; Lower's floor below
        // it, Living's 6 ft or less over it.
        Assert.InRange(SplitLevels.GradePlane(document), -350, -320);
        Assert.True(SplitLevels.IsBasement(document, house.Lower.Id));
        Assert.False(SplitLevels.IsBasement(document, house.Living.Id));

        // The family room with no window: R310.1's escape opening asked.
        Assert.Contains("no escape opening in the basement - IRC R310.1", WindowRules.Verdict(document, house.Family) ?? string.Empty);

        // An egress casement, its clear opening's bottom 1060 up: the basement has its escape opening.
        var east = document.Walls.Single(wall => wall.LevelId == house.Lower.Id && Math.Abs(wall.Start.X - 15000) < 1 && Math.Abs(wall.End.X - 15000) < 1);
        document.Add(new Window { HostWallId = east.Id, LevelId = house.Lower.Id, DistanceAlongWall = 4500, SillHeight = 1000, TypeId = TypeOf<WindowType>(document, "Casement - 30 x 48 in, Egress") });
        Assert.DoesNotContain("no escape opening in the basement", WindowRules.Verdict(document, house.Family) ?? string.Empty);

        // A hall is no habitable room: no light or air asked of it (R303.1).
        Assert.DoesNotContain("R303.1", WindowRules.Verdict(document, house.Hall) ?? string.Empty);

        // With no ground modelled, the lowest level within 1.2 m is the ground floor: Lower no basement.
        var bare = Build(ground: false);
        Assert.False(SplitLevels.IsBasement(bare.Document, bare.Lower.Id));
    }

    [Fact]
    public void TheGroundsFallIsReadClearOfTheBuildingAtItsCorners()
    {
        // The ground falling 8% from the house all round: nothing said of its fall - a point at a wall's end, inside, on the floor's very edge,
        // read as ground before; nor of the ground over the lower part, dug out for it.
        var house = Build();
        var surface = house.Document.All<Toposurface>().Single();
        Assert.DoesNotContain(Terrains.Problems(house.Document, surface), problem => problem.Contains("10 ft"));
    }
}
