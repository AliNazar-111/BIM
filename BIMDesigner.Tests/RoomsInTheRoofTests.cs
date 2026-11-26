using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// What building a chalet bungalow end to end turned up (round 149), its first floor in the roof: a room with a dormer in it was not
/// enclosed - the dormer's walls, standing on the roof, cut its floor; a dormer's window was taken as 150 off the floor, its sill as set over
/// its wall's base, and asked for safety glass and guarding; a dormer on a tiled roof on insulated rafters was roofed with an uninsulated
/// build-up; a dormer's roof bore on nothing in the analytical model; and a partition drawn its storey's height up through the roof was not
/// said to be.
/// </summary>
public class RoomsInTheRoofTests
{
    private const double W = 10000, D = 8500;

    /// <summary>
    /// A chalet's first floor, 10 m by 8.5 m, in a 45 degree roof of concrete tiles on insulated rafters, its eaves 600 up: walls 600 high
    /// under the eaves, the gables up to the roof, a partition 4 m in drawn its storey's 3 m; a bedroom on the left.
    /// </summary>
    private static (BimDocument Document, Roof Roof, Room Bedroom, Wall Partition) Chalet(bool attached = false)
    {
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var stud = document.TypesOf<WallType>().First(type => type.Name == "Stud Wall - Acoustic 125mm");
        var roof = new Roof { LevelId = first.Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name == "Roof - Concrete Interlocking Tiles on Insulated Rafters 240mm").Id, HeightOffset = 600 };
        roof.SetBoundary(new[] { new Point2D(-300, -300), new Point2D(W + 300, -300), new Point2D(W + 300, D + 300), new Point2D(-300, D + 300) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i is 0 or 2, SlopeDegrees = 45 }));
        document.Add(roof);

        Wall Add(double ax, double ay, double bx, double by, WallType type, double height, bool toRoof = true)
        {
            var wall = new Wall { Start = new Point2D(ax, ay), End = new Point2D(bx, by), TypeId = type.Id, LevelId = first.Id, UnconnectedHeight = height, TopAttachedTo = toRoof ? roof.Id : null };
            document.Add(wall);
            return wall;
        }

        Add(0, 0, 0, D, brick, 3000);
        Add(0, D, W, D, brick, 600);
        Add(W, D, W, 0, brick, 3000);
        Add(W, 0, 0, 0, brick, 600);
        var partition = Add(4000, 0, 4000, D, stud, 3000, attached);
        var floor = new Floor { LevelId = first.Id, TypeId = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Timber 220mm").Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(W, 0), new Point2D(W, D), new Point2D(0, D) });
        document.Add(floor);
        var bedroom = new Room { LevelId = first.Id, Location = new Point2D(2000, 4250), Name = "Bedroom 2" };
        document.Add(bedroom);
        return (document, roof, bedroom, partition);
    }

    /// <summary>A gable dormer, 2.4 m wide, its front wall 300 in from the front wall's line, over the bedroom; its front wall and the window in it.</summary>
    private static (Roof Dormer, Wall Front, Window Window) Dormer(BimDocument document, Roof roof)
    {
        var timber = document.TypesOf<WallType>().First(type => type.Name == "Timber Frame - Siding 183mm");
        Assert.NotNull(Dormers.Add(document, roof, new Point2D(2000, 300), DormerSettings.Default, timber.Id, out _, out _));
        var dormer = document.Elements.OfType<Roof>().Single(each => each.Dormer is not null);
        var front = Dormers.Parts(document, dormer).OfType<Wall>().OrderByDescending(wall => Math.Abs(wall.End.X - wall.Start.X)).First();
        var type = document.TypesOf<WindowType>().First(each => each.Name == "Casement - 1200 x 1200");
        var window = new Window { HostWallId = front.Id, LevelId = front.LevelId, TypeId = type.Id, SillHeight = 150, DistanceAlongWall = front.Length / 2 };
        document.Add(window);
        return (dormer, front, window);
    }

    [Fact]
    public void ARoomWithADormerInItIsEnclosedItsFloorUnderTheDormerItsOwn()
    {
        var (document, roof, bedroom, _) = Chalet(attached: true);
        var area = bedroom.GetBoundary(document).Area;
        Assert.Equal((4000 - 165 - 62.5) * (D - 330), area, 0);

        // The dormer's three walls stand on the roof, 1449 over the floor: they do not divide it - the room is as it was, and bigger under it.
        var (_, front, _) = Dormer(document, roof);
        Assert.Equal(1449, front.GetBaseElevation(document) - document.Levels[1].Elevation, 0);
        var boundary = bedroom.GetBoundary(document);
        Assert.True(boundary.IsEnclosed);
        Assert.Equal(area, boundary.Area, 0);

        // A wall set up off its floor no more than a room is measured at still divides it.
        var low = new Wall { Start = new Point2D(0, 4000), End = new Point2D(4000, 4000), TypeId = document.TypesOf<WallType>().First(type => type.Name == "Stud Wall - Acoustic 125mm").Id,
            LevelId = document.Levels[1].Id, UnconnectedHeight = 2000, BaseOffset = RoomBoundary.ComputationHeight };
        document.Add(low);
        Assert.True(bedroom.GetBoundary(document).Area < area / 2 + 1e6);
        low.BaseOffset = RoomBoundary.ComputationHeight + 1;
        Assert.Equal(area, bedroom.GetBoundary(document).Area, 0);
    }

    [Fact]
    public void ADormersWindowStandsAsHighOverTheFloorAsItsWallOnTheRoof()
    {
        var (document, roof, bedroom, _) = Chalet(attached: true);
        var (_, front, window) = Dormer(document, roof);

        // Set 150 up its wall, which stands 1449 up on the roof: 1599 over the floor - no glass low enough to be a critical location, no guarding.
        Assert.Equal(1599, Opening.OverFloor(document, front, window.SillHeight), 0);
        Assert.DoesNotContain(WindowRules.Problems(document, window, document.ProjectInformation.ClearanceStandard), problem => problem.Rule.Contains("critical location"));
        Assert.Empty(OpeningGuarding.Problems(document, window));
        Assert.Equal(1599, WindowRules.Of(document, bedroom, bedroom.GetBoundary(document).Polygon).Windows.Single().Sill, 0);

        // A wall reaching down below its level keeps its sill over its own base.
        var basement = new Wall { Start = new Point2D(0, 0), End = new Point2D(1000, 0), TypeId = front.TypeId, LevelId = document.Levels[1].Id, BaseOffset = -1500, UnconnectedHeight = 4000 };
        document.Add(basement);
        Assert.Equal(900, Opening.OverFloor(document, basement, 900), 6);
    }

    [Fact]
    public void ADormerOnATiledRoofOnInsulatedRaftersIsRoofedAsTheHouseIs()
    {
        var (document, roof, _, _) = Chalet(attached: true);
        var (dormer, _, _) = Dormer(document, roof);
        Assert.Equal(roof.TypeId, dormer.TypeId);
        Assert.DoesNotContain(SlabRules.Problems(document, dormer, document.ProjectInformation.ClearanceStandard), problem => problem.Rule.Contains("Approved Document L"));
    }

    [Fact]
    public void ADormersRoofIsCarriedByTheRoofItStandsOn()
    {
        var (document, roof, _, _) = Chalet(attached: true);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        var (dormer, _, _) = Dormer(document, roof);
        var problems = StructuralLoading.Of(document, AnalyticalModel.Of(document)).Problems;
        Assert.DoesNotContain(problems, problem => problem.Elements.Contains(dormer));
    }

    [Fact]
    public void APartitionDrawnUpThroughTheRoofIsSaidToBe()
    {
        // 3 m up from the first floor: over the front wall's line the roof's top is 600 + 300 + 240 / cos 45 = 1239 up, 1761 short of the wall's top.
        var (document, roof, _, partition) = Chalet();
        var through = WallRules.ThroughRoof(document, partition);
        Assert.NotNull(through);
        Assert.InRange(through!.Value, 3000 - (600 + 300 + 240 * Math.Sqrt(2)) - 2, 3000 - (600 + 300 + 240 * Math.Sqrt(2)) + 2);
        Assert.Contains(Warnings.ModelProblems(document), warning => warning.Heading == "Walls through roofs" && warning.Elements.Contains(partition));

        // Attached, it stops under the roof; an outside wall or a party wall is not said to - a parapet may rise through.
        partition.TopAttachedTo = roof.Id;
        Assert.Null(WallRules.ThroughRoof(document, partition));
        partition.TopAttachedTo = null;
        partition.TypeId = document.TypesOf<WallType>().First(type => type.Separating).Id;
        Assert.Null(WallRules.ThroughRoof(document, partition));
    }
}
