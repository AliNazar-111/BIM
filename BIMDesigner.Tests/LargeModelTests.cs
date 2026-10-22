using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using Window = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// A large model drawn quickly: every element of a kind kept until the model changes, an element
/// found by its id, and - while a plan, section or 3D view is drawn - the walls near a point found
/// from a grid and each wall's joins worked out once. None of it may change what is drawn.
/// </summary>
public class LargeModelTests
{
    /// <summary>A block of rooms four metres square, three storeys of them, with a door in every inside wall and a window in every outside one.</summary>
    public static BimDocument Rooms(int across)
    {
        var document = BimDocument.CreateDefault();
        document.AddLevel(new Level { Name = "Second Floor", Elevation = 6000 });
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id;
        var inside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Interior).Id;
        var door = document.TypesOf<DoorType>().First().Id;
        var window = document.TypesOf<WindowType>().First().Id;
        const double room = 4000;
        foreach (var level in document.Levels.ToList())
        {
            var floor = new Floor { LevelId = level.Id, TypeId = document.TypesOf<FloorType>().First().Id };
            floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(across * room, 0), new Point2D(across * room, across * room), new Point2D(0, across * room) });
            document.Add(floor);
            for (var j = 0; j <= across; j++)
            for (var i = 0; i < across; i++)
            foreach (var (a, b) in new[]
                     {
                         (new Point2D(i * room, j * room), new Point2D((i + 1) * room, j * room)),
                         (new Point2D(j * room, i * room), new Point2D(j * room, (i + 1) * room))
                     })
            {
                var exterior = j == 0 || j == across;
                var wall = new Wall { Start = a, End = b, TypeId = exterior ? outside : inside, LevelId = level.Id, UnconnectedHeight = 3000 };
                document.Add(wall);
                if (exterior) document.Add(new Window { TypeId = window, LevelId = level.Id, HostWallId = wall.Id, DistanceAlongWall = 2000, SillHeight = 900 });
                else document.Add(new Door { TypeId = door, LevelId = level.Id, HostWallId = wall.Id, DistanceAlongWall = 2000 });
            }
        }

        return document;
    }

    [Fact]
    public void EveryElementOfAKindIsKeptTillTheModelChanges()
    {
        var document = Rooms(2);
        var walls = document.Walls;
        var before = document.Kept<Wall>();
        Assert.Equal(document.Elements.OfType<Wall>(), walls);

        // Looked through again after a wall is added, the walls take it in; the list kept before does not change.
        var added = new Wall { Start = new Point2D(0, 0), End = new Point2D(0, -3000), TypeId = document.Walls.First().TypeId, LevelId = document.Levels[0].Id };
        document.Add(added);
        Assert.Contains(added, walls);
        Assert.DoesNotContain(added, before);
        Assert.Same(document.Kept<Wall>(), document.Kept<Wall>());

        // An element found by its id - as the kind it is, and not as any other.
        Assert.Same(added, document.Find<Wall>(added.Id));
        Assert.Null(document.Find<Door>(added.Id));
        Assert.Null(document.Find<Wall>((Guid?)null));
        document.Remove(added);
        Assert.Null(document.Find<Wall>(added.Id));
        Assert.DoesNotContain(added, walls);
    }

    [Fact]
    public void WhatIsKeptWhileDrawingGoesWhenTheModelChanges()
    {
        var document = Rooms(2);
        var wall = document.Walls.First();
        using (ModelCache.Begin(document))
        {
            var cache = ModelCache.Of(document);
            Assert.NotNull(cache);

            // Drawn inside another drawing of the same model, it is that one's.
            using (ModelCache.Begin(document)) Assert.Same(cache, ModelCache.Of(document));
            Assert.Null(ModelCache.Of(Rooms(1)));

            var one = Assert.Single(WallOpenings.Of(document, wall));
            var another = new Window { TypeId = one.TypeId, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3500, SillHeight = 900 };
            document.Add(another);
            Assert.Equal(new[] { one, another }, WallOpenings.Of(document, wall));
        }

        Assert.Null(ModelCache.Of(document));
    }

    [Fact]
    public void JoinsComeOutTheSameWorkedOutOnceAsEachTime()
    {
        var document = Rooms(3);
        string Joins()
        {
            var said = new List<string>();
            foreach (var wall in document.Walls)
            {
                var type = document.GetWallType(wall)!;
                var (start, end) = WallJoins.GetEndCuts(document, wall, type);
                said.Add(string.Join(";", start.Points.Concat(end.Points).Select(point => $"{point.X:0.###},{point.Y:0.###}")) + $" {start.Condition} {end.Condition}");
                said.Add(string.Join(";", WallJoins.Intrusions(document, wall, type).Select(intrusion => $"{intrusion.FromAlong:0.###}-{intrusion.ToAlong:0.###}")));
                said.Add(string.Join(";", WallJoins.FaceGaps(document, wall, type).Select(gap => $"{gap.Exterior} {gap.From:0.###}-{gap.To:0.###}")));
                said.Add(string.Join(";", WallSlices.Solid(document, wall, type).Select(slice => $"{slice.From:0.###}-{slice.To:0.###}")));
            }

            return string.Join("\n", said);
        }

        var eachTime = Joins();
        string once;
        using (ModelCache.Begin(document)) once = Joins();
        Assert.Equal(eachTime, once);

        // A tee, a cross and a corner each among them: walls meet at every node of the grid.
        Assert.Contains(document.Walls, wall => WallJoins.GetEndCuts(document, wall, document.GetWallType(wall)!).Start.Condition == WallEndCondition.Butt);
    }

    [Fact]
    public void AClashIsFoundAmongAThousandElementsAndNoneThatIsNot()
    {
        var document = Rooms(10);
        Assert.True(document.Elements.Count > 1000);

        // A desk in every room of the top storey, and one put down twice in the far corner.
        var desk = document.TypesOf<ComponentType>().Single(type => type.Name.StartsWith("Desk - 1500"));
        var top = document.Levels[2].Id;
        Component Desk(double x, double y)
        {
            var placed = new Component { TypeId = desk.Id, TypeKind = desk.Kind, LevelId = top, Location = new Point2D(x, y) };
            document.Add(placed);
            return placed;
        }

        for (var i = 0; i < 10; i++)
        for (var j = 0; j < 10; j++)
            Desk(i * 4000 + 2000, j * 4000 + 1500);
        var twice = Desk(38000, 37500);

        var test = new ClashTest { Name = "twice", Kind = ClashKind.Duplicate, Tolerance = 1 };
        test.SelectionA.Add(BuiltInCategory.Furniture);
        test.SelectionB.Add(BuiltInCategory.Furniture);
        var clash = Assert.Single(ClashDetective.Run(document, test, DateTime.Now));
        Assert.Contains(twice.Id, new[] { clash.ElementA, clash.ElementB });
    }

    [Fact]
    public void ALargeModelIsBuiltIn3DAsItIs()
    {
        var document = Rooms(4);
        var meshes = ModelMeshBuilder.Build(document);

        // Every wall, door and window drawn, each wall with its door or window cut through it.
        Assert.All(document.Walls, wall => Assert.Contains(meshes, mesh => mesh.ElementId == wall.Id));
        Assert.All(document.Openings, opening => Assert.Contains(meshes, mesh => mesh.ElementId == opening.Id));
        Assert.Null(ModelCache.Of(document));
    }
}
