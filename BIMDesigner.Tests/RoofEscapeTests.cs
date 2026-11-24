using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Escape over flat roofs (round 136), by Approved Document B Volume 2 2.30 to 2.32 and 3.33, and Volume 1 2.13, 3.10 and 3.34: the route found
/// and its door a way out, who may use it, the roof and the openings near it fire-resisting, its edges guarded.
/// </summary>
[Collection("Wpf")]
public class RoofEscapeTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Partition = "Interior - Partition 125mm", FireDoor = "Fire Door FD30S - 900 x 2100", Wide = "Single - 1000 x 2100";

    private static Wall Wall(BimDocument document, Guid level, double x0, double y0, double x1, double y1, string type = Partition)
    {
        var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type = FireDoor)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y)
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    private sealed record Building(BimDocument Document, List<Level> Levels, Door RoofDoor, Door StairDoor, Roof Roof, Room Upstairs, Wall UpperEast);

    /// <summary>
    /// An office 14 m by 10 m on the ground, an internal stair at its west end (x 0 to 3 m); upstairs only its west 8 m, the stair and an office,
    /// a door in the office's east wall onto a flat roof over the rest of the ground storey; an external stair outside the east wall (x 14.5 to
    /// 15.5 m) from the ground up to the roof's level, its head 1 m off the roof's east edge.
    /// </summary>
    private static Building Build(string use = "Office")
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        var (ground, first) = (levels[0].Id, levels[1].Id);
        Wall(document, ground, 0, 0, 14000, 0, Brick);
        var east = Wall(document, ground, 14000, 0, 14000, 10000, Brick);
        Wall(document, ground, 14000, 10000, 0, 10000, Brick);
        Wall(document, ground, 0, 10000, 0, 0, Brick);
        Door(document, Wall(document, ground, 3000, 0, 3000, 10000), 5000);
        Door(document, east, 5000, Wide);
        Room(document, ground, "Stair", 1500, 8000);
        Room(document, ground, "Office", 8500, 5000);

        Wall(document, first, 0, 0, 8000, 0, Brick);
        var upperEast = Wall(document, first, 8000, 0, 8000, 10000, Brick);
        Wall(document, first, 8000, 10000, 0, 10000, Brick);
        Wall(document, first, 0, 10000, 0, 0, Brick);
        var stairDoor = Door(document, Wall(document, first, 3000, 0, 3000, 10000), 5000);
        var roofDoor = Door(document, upperEast, 5000, FireDoor);
        Room(document, first, "Stair", 1500, 8000);
        var upstairs = Room(document, first, "Office", 5500, 5000);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = ground, TopLevelId = first, Start = new Point2D(1500, 2600), Angle = 90, Width = 900 });

        var roof = new Roof { LevelId = ground, TypeId = document.TypesOf<RoofType>().First(each => each.Name == "Roof - Warm Flat 320mm").Id, HeightOffset = 2600 };
        roof.SetBoundary(new[] { new Point2D(8000, 0), new Point2D(14000, 0), new Point2D(14000, 10000), new Point2D(8000, 10000) });
        document.Add(roof);
        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = ground, TopLevelId = first, Start = new Point2D(15000, 600), Angle = 90, Width = 1000, Outside = true });
        return new Building(document, levels, roofDoor, stairDoor, roof, upstairs, upperEast);
    }

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Door door) => Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard);

    [Fact]
    public void ARouteOverAFlatRoofToAnExternalStairIsAWayOut()
    {
        var building = Build();
        var document = building.Document;
        var route = Assert.Single(RoofEscape.Of(document));
        Assert.Equal(building.RoofDoor, route.Door);
        Assert.Equal("the external stair", route.Leads);
        Assert.Same(building.Upstairs, route.Room);

        // The external stair, come to across the roof, has its door (3.33): not said to have none.
        var stair = document.All<Stair>().First(each => each.Outside);
        Assert.DoesNotContain(ExternalStairs.Wanting(document, stair), said => said.Contains("no door onto it"));
        Assert.True(ExternalStairs.EscapeOnly(document, stair));
        Assert.DoesNotContain("(1.33", Stairs.Verdict(document, stair));

        // The upstairs office's internal stair door taken away: the roof route its way out - but then its only one (2.31).
        Assert.Contains("2 exits", HorizontalEscape.Verdict(document, building.Upstairs));
        document.Remove(building.StairDoor);
        Assert.DoesNotContain("no way from it to a storey exit", HorizontalEscape.Verdict(document, building.Upstairs));
        Assert.Contains(Problems(document, building.RoofDoor), problem => problem.Said == $"it opens onto a route over the flat roof to the external stair, the only way out of the {building.Levels[1].Name}."
                                                                       && problem.Rule.StartsWith("Approved Document B Volume 2 (2.31)", StringComparison.Ordinal));
        document.Add(building.StairDoor);
        Assert.DoesNotContain(Problems(document, building.RoofDoor), problem => problem.Said.Contains("the only way out"));
    }

    [Fact]
    public void TheRoofAndWhatIsNearTheRouteAreFireResistingAndItsEdgesGuarded()
    {
        var building = Build();
        var document = building.Document;
        var roofType = document.FindType<RoofType>(building.Roof.TypeId)!;

        // The roof not fire-rated (2.32c).
        roofType.FireRating = string.Empty;
        Assert.Contains(Problems(document, building.RoofDoor), problem => problem.Said.EndsWith("the Roof - Warm Flat 320mm not fire-rated.", StringComparison.Ordinal)
                                                                       && problem.Rule.StartsWith("Approved Document B Volume 2 (2.32c)", StringComparison.Ordinal));
        roofType.FireRating = "60 min";
        Assert.DoesNotContain(Problems(document, building.RoofDoor), problem => problem.Said.Contains("Warm Flat 320mm"));

        // A casement in the upstairs east wall 1.4 m from the route (2.32c).
        var window = new Window { HostWallId = building.UpperEast.Id, TypeId = document.TypesOf<WindowType>().First(each => each.Name == "Casement - 1200 x 1200").Id, LevelId = building.Levels[1].Id, DistanceAlongWall = 7000 };
        document.Add(window);
        Assert.Contains(Problems(document, building.RoofDoor), problem => problem.Said.Contains("the Casement - 1200 x 1200 window on the") && problem.Said.EndsWith("is not fire-resisting.", StringComparison.Ordinal));
        document.Remove(window);

        // The roof's east edge, which the route crosses to the stair, unguarded - the ground storey's wall stops at the roof (2.32d); a railing
        // along it: met.
        Assert.Contains(Problems(document, building.RoofDoor), problem => problem.Said.EndsWith("1 of the roof's edges within 2 m of it unguarded.", StringComparison.Ordinal)
                                                                       && problem.Rule.StartsWith("Approved Document B Volume 2 (2.32d)", StringComparison.Ordinal));
        // A railing south of the stair only: the edge north of it still open; one north of it too, the gap where the route goes onto the stair
        // left: met.
        var south = new Railing { LevelId = building.Levels[1].Id, TypeId = document.TypesOf<RailingType>().First().Id };
        south.SetPath(new[] { new Point2D(14000, 0), new Point2D(14000, 3000) });
        document.Add(south);
        Assert.Contains(Problems(document, building.RoofDoor), problem => problem.Said.Contains("unguarded"));
        var north = new Railing { LevelId = building.Levels[1].Id, TypeId = document.TypesOf<RailingType>().First().Id };
        north.SetPath(new[] { new Point2D(14000, 5000), new Point2D(14000, 10000) });
        document.Add(north);
        Assert.DoesNotContain(Problems(document, building.RoofDoor), problem => problem.Said.Contains("unguarded"));
    }

    [Fact]
    public void NotOverAFlatRoofFromWhereThePublicAreOrInACareHome()
    {
        // A library's reading room upstairs: the public's (2.31); its office, not.
        var building = Build("Library");
        var document = building.Document;
        Assert.DoesNotContain(Problems(document, building.RoofDoor), problem => problem.Said.Contains("the public use"));
        building.Upstairs.Name = "Reading Room";
        Assert.Contains(Problems(document, building.RoofDoor), problem => problem.Said.Contains("from the Reading Room, a part the public use") && problem.Rule.StartsWith("Approved Document B Volume 2 (2.31)", StringComparison.Ordinal));

        // A care home: never (2.31).
        var care = Build("Care Home");
        Assert.Contains(Problems(care.Document, care.RoofDoor), problem => problem.Said.EndsWith("in a residential (institutional) building.", StringComparison.Ordinal));

        // A block of flats: Volume 1's 3.10 (a flat's own) or 3.34 (the common parts').
        var flats = Build("Block of Flats");
        flats.Document.FindType<RoofType>(flats.Roof.TypeId)!.FireRating = string.Empty;
        Assert.Contains(Problems(flats.Document, flats.RoofDoor), problem => problem.Rule.StartsWith("Approved Document B (3.34c)", StringComparison.Ordinal));
    }
}
