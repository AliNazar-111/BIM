using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Lifts in a building other than a dwelling by Approved Document M Volume 2 (round 107): a passenger lift serving every storey, its car, its
/// landing doors and the space in front of them; a lifting platform's size and doors.
/// </summary>
[Collection("Wpf")]
public class LiftsTests
{
    private const string LiftDoor = "Lift Landing Door - Sliding 900 x 2100";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along, string type)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == type).Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Component Place(BimDocument document, Guid level, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = level, Location = new Point2D(x, y), Elevation = kind.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    /// <summary>
    /// An office 12 m by 8 m on two storeys, each a corridor along the south (y 0 to 2 m, 1772.5 clear), two offices north of it either side of
    /// a lift shaft at x 6 to 8 m, y 2 to 4 m, its front the corridor's wall; no lift yet, and no landing doors.
    /// </summary>
    private static (BimDocument Document, List<(Guid Level, Wall Corridor)> Storeys) Build()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var storeys = new List<(Guid, Wall)>();
        foreach (var level in new[] { document.Levels[0].Id, document.Levels[1].Id })
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
            for (var i = 0; i < 4; i++) Wall(document, level, corners[i], corners[(i + 1) % 4], "Exterior - Brick on Block 330mm");
            var corridor = Wall(document, level, new Point2D(0, 2000), new Point2D(12000, 2000));
            Wall(document, level, new Point2D(6000, 2000), new Point2D(6000, 8000));
            Wall(document, level, new Point2D(6000, 4000), new Point2D(8000, 4000));
            Wall(document, level, new Point2D(8000, 4000), new Point2D(8000, 2000));
            foreach (var (name, x, y) in new[] { ("Corridor", 6000.0, 1000.0), ("Office 1", 3000.0, 5000.0), ("Office 2", 10000.0, 6000.0), ("Lift", 7000.0, 3000.0) })
                document.Add(new Room { LevelId = level, Location = new Point2D(x, y), Name = name });
            storeys.Add((level, corridor));
        }

        return (document, storeys);
    }

    private static List<PlacementProblem> Heard(BimDocument document, Component lift) => Lifts.Problems(document, lift, document.ProjectInformation.ClearanceStandard).ToList();

    [Fact]
    public void ABuildingOnTwoStoreysHasAPassengerLiftServingThemAll()
    {
        var (document, storeys) = Build();

        // Rooms on two storeys, no lift: asked on the first room of the upper storey (3.24a).
        var missing = Assert.Single(Lifts.Missing(document));
        Assert.Equal(storeys[1].Level, missing.Room.LevelId);
        Assert.StartsWith("its building has rooms on 2 storeys and no passenger lift - Approved Document M Volume 2 (3.24a)", missing.Said);

        // The library's 8 person lift in its shaft, a landing door on the ground storey only: the first floor not served.
        var lift = Place(document, storeys[0].Level, "Passenger Lift - 8 Person, Car 1100 x 1400", 7000, 3000);
        var ground = Door(document, storeys[0].Corridor, 7000, LiftDoor);
        Assert.Empty(Lifts.Missing(document));
        var upper = document.FindLevel(storeys[1].Level)!.Name;
        var said = Assert.Single(Heard(document, lift));
        Assert.Equal($"no lift stops on the {upper} - no landing door by a lift there.", said.Said);
        Assert.StartsWith("Approved Document M Volume 2 (3.24a)", said.Rule);

        // A landing door on the first floor too: served, its car 1100 by 1400, its doors sliding and 800 clear, 1500 square in front - met.
        Door(document, storeys[1].Corridor, 7000, LiftDoor);
        Assert.Empty(Heard(document, lift));
        Assert.True(Lifts.IsLanding(document, ground));

        // A landing door is 3.34e's, not Table 2's: no door check of Section 3 on it.
        Assert.DoesNotContain(Doors.Problems(document, ground, document.ProjectInformation.ClearanceStandard), problem => problem.Rule.Contains("Table 2"));

        // One storey only, or a house: no lift asked.
        document.ProjectInformation.BuildingType = "House";
        Assert.Empty(Heard(document, lift));
    }

    [Fact]
    public void ALiftsCarDoorsAndLandingsAreWideEnough()
    {
        var (document, storeys) = Build();
        var lift = Place(document, storeys[0].Level, "Passenger Lift - 8 Person, Car 1100 x 1400", 7000, 3000);
        var ground = Door(document, storeys[0].Corridor, 7000, LiftDoor);
        Door(document, storeys[1].Corridor, 7000, LiftDoor);
        var groundName = document.FindLevel(storeys[0].Level)!.Name;

        // A car 1000 wide: 1100 by 1400 asked (3.34c, Diagram 11).
        var car = document.FindType<ComponentType>(lift.TypeId)!;
        car.Width = 1000;
        Assert.Contains(Heard(document, lift), problem => problem.Said == "its car is 1000 wide and 1400 deep." && problem.Rule.StartsWith("Approved Document M Volume 2 (3.34c, Diagram 11)", StringComparison.Ordinal));
        car.Width = 1100;

        // A plain 900 swing door on the ground floor: 760 clear, and not sliding (3.34e).
        ground.TypeId = document.TypesOf<DoorType>().First(type => type.Name == "Single - 900 x 2100").Id;
        var said = Heard(document, lift).Select(problem => problem.Said).ToList();
        Assert.Contains($"its landing door on the {groundName} gives 760 clear.", said);
        Assert.Contains($"its landing door on the {groundName} is a swing door.", said);
        ground.TypeId = document.TypesOf<DoorType>().First(type => type.Name == LiftDoor).Id;

        // Lockers standing in the corridor in front of it: 1500 by 1500 clear asked (3.28a).
        Place(document, storeys[0].Level, "Lockers - Bank of 4, 2 Tier", 7000, 900);
        var space = Assert.Single(Heard(document, lift));
        Assert.Equal($"the 1500 x 1500 in front of its landing door on the {groundName} has Lockers - Bank of 4, 2 Tier in it.", space.Said);
        Assert.StartsWith("Approved Document M Volume 2 (3.28a)", space.Rule);
        Assert.Equal(4, space.Outline.Count);

        // Out to IFC as a lift.
        using var model = BIMDesigner.Infrastructure.Interoperability.IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcTransportElement>(), each => each.PredefinedType == Xbim.Ifc4.Interfaces.IfcTransportElementTypeEnum.ELEVATOR);
    }

    [Fact]
    public void ALiftingPlatformIsSizedByItsTravelAndDoors()
    {
        var (document, storeys) = Build();
        var platform = Place(document, storeys[0].Level, "Lifting Platform - Enclosed, 1100 x 1400", 7000, 3000);
        Door(document, storeys[0].Corridor, 7000, LiftDoor);
        Door(document, storeys[1].Corridor, 7000, LiftDoor);

        // In place of a lift (3.24b); 1100 by 1400, so its doors 900 clear where they give 800 (3.43h).
        var said = Heard(document, platform);
        Assert.Contains(said, problem => problem.Said == "it is a lifting platform where a passenger lift is asked." && problem.Rule.StartsWith("Approved Document M Volume 2 (3.24a, b, 3.22)", StringComparison.Ordinal));
        Assert.Equal(2, said.Count(problem => problem.Said.EndsWith("gives 800 clear.", StringComparison.Ordinal) && problem.Rule.Contains("(3.43h) asks a lifting platform's doors 900 clear")));

        // Made 850 by 1300: travelling 3000, in an enclosure, 900 by 1400 asked (3.43b ii, g ii) - and its doors 800 then, met.
        var type = document.FindType<ComponentType>(platform.TypeId)!;
        (type.Width, type.Depth) = (850, 1300);
        said = Heard(document, platform);
        Assert.Contains(said, problem => problem.Said == "its platform is 850 by 1300." && problem.Rule.Contains("asks a lifting platform 900 wide and 1400 deep at least, travelling 3000, more than 2 m, in an enclosure"));
        Assert.DoesNotContain(said, problem => problem.Rule.Contains("(3.43h)"));
    }
}
