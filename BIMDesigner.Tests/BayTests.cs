using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Tests;

/// <summary>
/// Bay windows rising a storey or more out of an outside wall (round 139): built from ordinary walls, windows, floors and a roof, the wall
/// across the bay taken out, the rooms taking the bay in, undone as one step; and a choice typed in another case taken as the list spells it.
/// </summary>
[Collection("Wpf")]
public class BayTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    /// <summary>A terraced house 6 m wide and 9 m deep on two storeys, its front wall along y 0 from x 0 to 6 m, a casement at x 3 m in it on each storey, a first floor over the ground.</summary>
    private static (BimDocument Document, List<BIMDesigner.Core.Datums.Level> Levels, List<Wall> Fronts, List<Room> Rooms) House()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Terraced house";
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        var (fronts, rooms) = (new List<Wall>(), new List<Room>());
        var brick = document.TypesOf<WallType>().First(each => each.Name == Brick).Id;
        var casement = document.TypesOf<WindowType>().First(each => each.Name == "Casement - 1200 x 1200").Id;
        foreach (var level in levels)
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 9000), new Point2D(0, 9000) };
            for (var i = 0; i < 4; i++)
            {
                var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick, LevelId = level.Id, UnconnectedHeight = 3000 };
                document.Add(wall);
                if (i == 0) fronts.Add(wall);
            }

            document.Add(new Window { HostWallId = fronts[^1].Id, DistanceAlongWall = 3000, LevelId = level.Id, TypeId = casement });
            var room = new Room { LevelId = level.Id, Location = new Point2D(3000, 4500), Name = level == levels[0] ? "Front Room" : "Bedroom" };
            document.Add(room);
            rooms.Add(room);
        }

        var floor = new Floor { LevelId = levels[1].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 9000), new Point2D(0, 9000) });
        document.Add(floor);
        return (document, levels, fronts, rooms);
    }

    private static readonly BaySettings Canted = BaySettings.Default with { Shape = BayShape.Canted };

    [Fact]
    public void ACantedBayRisesTwoStoreysOutOfTheFrontWall()
    {
        var (document, levels, fronts, rooms) = House();
        var casement = document.TypesOf<WindowType>().First(each => each.Name == "Casement - 1200 x 1200").Id;
        var (wallsBefore, windowsBefore, areaBefore) = (document.Walls.Count(), document.All<Window>().Count(), rooms[0].GetBoundary(document).Area);

        var command = Bays.Add(document, fronts[0], new Point2D(3000, 0), Canted, casement, out var problem);
        Assert.Null(problem);
        Assert.NotNull(command);

        // On each storey: the front wall no longer across the bay - split, its middle and the window in it gone - and three walls round the bay.
        foreach (var level in levels)
        {
            Assert.DoesNotContain(document.Walls, wall => wall.LevelId == level.Id && Math.Abs(wall.Start.Y) < 1 && Math.Abs(wall.End.Y) < 1
                                                          && Math.Min(wall.Start.X, wall.End.X) < 3000 && Math.Max(wall.Start.X, wall.End.X) > 3000);
            var bay = document.Walls.Where(wall => wall.LevelId == level.Id && Math.Min(wall.Start.Y, wall.End.Y) < -1).ToList();
            Assert.Equal(3, bay.Count);
            var front = bay.Single(wall => Math.Abs(wall.Start.Y - wall.End.Y) < 1);
            Assert.True(front.Length > Bays.ShortestFront);
            Assert.Contains(document.All<Window>(), window => window.HostWallId == front.Id);
        }

        // Four walls a storey more - the front in two parts, three round the bay - the windows in the old front gone.
        Assert.Equal(wallsBefore + 2 * 4, document.Walls.Count());
        Assert.DoesNotContain(document.All<Window>(), window => fronts.Any(front => front.Id == window.HostWallId) && Math.Abs(window.DistanceAlongWall - 3000) < 1);

        // The front room takes the bay in; the first floor runs out into it; a hipped roof over it against the wall, its back edge level.
        Assert.True(rooms[0].GetBoundary(document).Area > areaBefore + 800_000, $"{areaBefore} -> {rooms[0].GetBoundary(document).Area}");
        var inBay = new Point2D(3000, -400);
        Assert.Contains(document.All<Floor>(), floor => floor.LevelId == levels[1].Id && floor.Contains(inBay));
        var roof = Assert.Single(document.All<Roof>());
        Assert.Equal(levels[1].Id, roof.LevelId);
        Assert.Equal(3000, roof.HeightOffset, 1);
        Assert.True(roof.Contains(inBay));
        Assert.Equal(3, roof.Edges.Count(edge => edge.DefinesSlope));

        // Undone as one step: all as it was.
        command!.Undo();
        Assert.Equal(wallsBefore, document.Walls.Count());
        Assert.Equal(windowsBefore, document.All<Window>().Count());
        Assert.Empty(document.All<Roof>());
        Assert.Equal(areaBefore, rooms[0].GetBoundary(document).Area, 1);
    }

    [Fact]
    public void ABoxBayHasSquareSidesAndAFlatRoof()
    {
        var (document, levels, fronts, _) = House();
        var settings = BaySettings.Default with { Shape = BayShape.Box, Storeys = 1, Roof = BayRoof.Flat };
        Assert.NotNull(Bays.Add(document, fronts[0], new Point2D(3000, 0), settings, Guid.Empty, out var problem));
        Assert.Null(problem);
        var bay = document.Walls.Where(wall => wall.LevelId == levels[0].Id && Math.Min(wall.Start.Y, wall.End.Y) < -1).ToList();
        Assert.Equal(3, bay.Count);
        Assert.All(bay.Where(wall => Math.Abs(wall.Start.Y - wall.End.Y) > 1), side => Assert.Equal(side.Start.X, side.End.X, 1));
        Assert.DoesNotContain(document.Walls, wall => wall.LevelId == levels[1].Id && Math.Min(wall.Start.Y, wall.End.Y) < -1);
        Assert.Equal(levels[0].Id, Assert.Single(document.All<Roof>()).LevelId);
        Assert.DoesNotContain(Assert.Single(document.All<Roof>()).Edges, edge => edge.DefinesSlope);
    }

    [Fact]
    public void ABayIsRefusedWhereItCannotGo()
    {
        var (document, _, fronts, _) = House();
        Assert.Null(Bays.Add(document, fronts[0], new Point2D(800, 0), Canted, Guid.Empty, out var problem));
        Assert.StartsWith("The bay is 2.40 m wide and too near the wall's end here", problem);
        Assert.Null(Bays.Add(document, fronts[0], new Point2D(3000, 0), Canted with { Width = 1600, Projection = 900 }, Guid.Empty, out problem));
        Assert.StartsWith("A canted bay 1.60 m wide standing out 0.90 m leaves too short a front", problem);
        Assert.Null(Bays.Add(document, fronts[0], new Point2D(3000, 0), Canted with { Storeys = 3 }, Guid.Empty, out problem));
        Assert.StartsWith("There are only 2 storeys from here up", problem);
        Assert.Equal(8, document.Walls.Count());
    }

    [Fact]
    public void AChoiceTypedInAnotherCaseIsTakenAsTheListSpellsIt()
    {
        var chosen = "Detached house";
        var row = ParameterValue.BindChoice(new ParameterDefinition("Building Type", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics), () => chosen, value => chosen = value,
            new[] { "Detached house", "Semi-detached house" });
        Assert.True(row.TrySet("Semi-detached House"));
        Assert.Equal("Semi-detached house", chosen);
        Assert.False(row.TrySet("Castle"));
    }
}
