using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A dormer in one click on a roof's slope: walls standing on the roof, a roof on them joined
/// into the main roof, and the main roof opened under it - one step to undo.
/// </summary>
public class DormerToolTests
{
    /// <summary>A box of walls 3 m high and a gable roof on it, eaves along the long sides.</summary>
    private static (BimDocument Document, Roof Main, WallType WallType) House(double width, double depth, double pitch)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000 });

        var main = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3000 };
        main.SetBoundary(corners);
        main.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = pitch }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = pitch }, new RoofEdge()
        });
        document.Add(main);

        return (document, main, wallType);
    }

    [Fact]
    public void OneClickBuildsTheWholeDormer()
    {
        var (document, main, wallType) = House(10000, 10000, 40);
        var wallsBefore = document.Walls.Count();

        var command = Dormers.Add(document, main, new Point2D(5000, 800), DormerSettings.Default, wallType.Id, out var problem, out var used);
        Assert.Null(problem);
        Assert.NotNull(command);
        Assert.Equal(DormerSettings.Default.Height, used.Height);

        // Three walls, standing on the main roof and carrying the dormer's roof.
        var walls = document.Walls.Skip(wallsBefore).ToList();
        Assert.Equal(3, walls.Count);
        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        Assert.All(walls, wall => Assert.Equal(main.Id, wall.BaseAttachedTo));
        Assert.All(walls, wall => Assert.Equal(dormer.Id, wall.TopAttachedTo));

        // A gable: its two sides slope, its roof is joined into the main roof, and the main
        // roof is opened under it.
        Assert.Equal(main.Id, dormer.JoinedTo);
        Assert.Contains(dormer.Id, main.DormerOpenings);
        var opening = Assert.Single(RoofJoin.Openings(document, main));
        Assert.True(Polygon2D.Area(opening) > 1e6, "The opening under the dormer is too small.");

        // Its front wall is the height asked for above the roof at its ends, under the eaves -
        // and rises in the middle to the dormer's ridge: it is the dormer's gable end.
        var profile = WallProfile.Of(document, walls[1])!;
        var end = profile.Where(point => point.X < 1).ToList();
        Assert.InRange(end.Max(point => point.Y) - end.Min(point => point.Y), DormerSettings.Default.Height, DormerSettings.Default.Height + 300);
        Assert.True(profile.Max(point => point.Y) > end.Max(point => point.Y) + 500, "The front wall should rise to the dormer's ridge.");

        // One step to undo, all of it.
        command!.Undo();
        Assert.Equal(wallsBefore, document.Walls.Count());
        Assert.Single(document.Elements.OfType<Roof>());
        Assert.Empty(main.DormerOpenings);
    }

    [Fact]
    public void OnASmallRoofTheDormerIsMadeLowEnoughToFit()
    {
        // An 8 m x 6 m house under a 35 degree roof: a 1.4 m dormer would stand above the ridge.
        var (document, main, wallType) = House(8000, 6000, 35);

        var command = Dormers.Add(document, main, new Point2D(4000, 500), DormerSettings.Default with { Width = 1200 }, wallType.Id, out var problem, out var used);

        Assert.Null(problem);
        Assert.NotNull(command);
        Assert.True(used.Height < DormerSettings.Default.Height, $"It kept {used.Height:0} mm, which cannot fit.");
        Assert.True(used.Height >= Dormers.MinimumHeight);
    }

    [Theory]
    [InlineData(DormerShape.Shed)]
    [InlineData(DormerShape.Hip)]
    public void EveryShapeIsBuilt(DormerShape shape)
    {
        var (document, main, wallType) = House(10000, 10000, 40);

        var command = Dormers.Add(document, main, new Point2D(5000, 800), DormerSettings.Default with { Shape = shape, Slope = shape == DormerShape.Shed ? 15 : 35 },
            wallType.Id, out var problem, out _);

        Assert.Null(problem);
        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        Assert.Equal(main.Id, dormer.JoinedTo);
        Assert.Single(RoofJoin.Openings(document, main));
        Assert.NotNull(command);
    }

    [Fact]
    public void ARoofTooLowForADormerSaysWhy()
    {
        // A 6 m x 4 m house under a 30 degree roof rises little more than a metre.
        var (document, main, wallType) = House(6000, 4000, 30);

        Assert.Null(Dormers.Add(document, main, new Point2D(3000, 600), DormerSettings.Default, wallType.Id, out var problem, out _));
        Assert.Contains("too low", problem);
        Assert.Single(document.Elements.OfType<Roof>());
    }

    [Theory]
    [InlineData(6000, 5000, 30, true)]
    [InlineData(6000, 5000, 30, false)]
    [InlineData(10000, 8000, 30, true)]
    [InlineData(8000, 6000, 35, false)]
    public void ADormerOfferedInsteadIsOneThatBuilds(double width, double depth, double pitch, bool hip)
    {
        // All over the front slope of a small roof, hipped or gabled: wherever the dormer asked
        // for is refused and another is offered, the one offered goes on.
        for (var x = 1000.0; x < width - 999; x += 1000)
        for (var y = 100.0; y < depth / 2 - 500; y += 500)
        {
            var (document, main, wallType) = House(width, depth, pitch);
            if (hip) foreach (var edge in main.Edges) edge.DefinesSlope = true;

            var at = new Point2D(x, y);
            if (Dormers.Add(document, main, at, DormerSettings.Default, wallType.Id, out _, out _) is not null) continue;
            if (Dormers.InsteadAt(document, main, at, DormerSettings.Default, wallType.Structure.TotalWidth) is not { } instead) continue;

            var built = Dormers.Add(document, main, at, instead, wallType.Id, out var problem, out _);
            Assert.True(built is not null, $"Offered a {instead.Width:0} wide shed at {at} on a {width:0} x {depth:0} roof, but it failed: {problem}");
        }
    }

    [Fact]
    public void AFlatRoofHasNoSlopeForADormer()
    {
        var (document, main, wallType) = House(10000, 8000, 40);
        foreach (var edge in main.Edges) edge.DefinesSlope = false;

        Assert.Null(Dormers.Add(document, main, new Point2D(5000, 1200), DormerSettings.Default, wallType.Id, out var problem, out _));
        Assert.Contains("slop", problem);
        Assert.Single(document.Elements.OfType<Roof>());
    }

    [Fact]
    public void ADormerIsOneThingItsRoofAndWallsArePartsOf()
    {
        var (document, main, wallType) = House(10000, 10000, 40);
        var before = document.Walls.Count();
        Dormers.Add(document, main, new Point2D(5000, 800), DormerSettings.Default, wallType.Id, out _, out _);

        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        var walls = document.Walls.Skip(before).ToList();

        // Any part of it names the dormer; the house's own roof and walls are not part of one.
        Assert.All(walls, wall => Assert.Same(dormer, Dormers.Of(document, wall)));
        Assert.Same(dormer, Dormers.Of(document, dormer));
        Assert.Null(Dormers.Of(document, main));
        Assert.Null(Dormers.Of(document, document.Walls.First()));
        Assert.Equal(walls.Cast<Element>().Prepend(dormer), Dormers.Parts(document, dormer));
        Assert.Contains("Gable dormer", Dormers.Describe(dormer));

        // Saved and opened again, it is still one.
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = reloaded.Elements.OfType<Roof>().Single(roof => roof.Id == dormer.Id);
        Assert.Equal(DormerSettings.Default, again.Dormer);
        Assert.Equal(4, Dormers.Parts(reloaded, again).Count);
    }

    [Fact]
    public void ADormerBuiltByHandIsOneToo()
    {
        // A roof joined into the main roof, with walls standing on the main roof and carrying it:
        // Revit's way of building one, or one made before dormers knew their walls.
        var (document, main, wallType) = House(10000, 10000, 40);
        var before = document.Walls.Count();
        Dormers.Add(document, main, new Point2D(5000, 800), DormerSettings.Default, wallType.Id, out _, out _);
        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        dormer.Dormer = null;
        dormer.DormerWalls.Clear();

        var walls = document.Walls.Skip(before).ToList();
        Assert.All(walls, wall => Assert.Same(dormer, Dormers.Of(document, wall)));
        Assert.Equal(4, Dormers.Parts(document, dormer).Count);
    }

    [Fact]
    public void MovedTogetherItsPartsMoveTogether()
    {
        var (document, main, wallType) = House(10000, 10000, 40);
        Dormers.Add(document, main, new Point2D(4000, 800), DormerSettings.Default, wallType.Id, out _, out _);
        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        var parts = Dormers.Parts(document, dormer);
        var corner = dormer.Boundary[0];
        var wallStart = ((Wall)parts[1]).Start;

        new MoveElementsCommand(parts, new Vector2D(2000, 0), document: document).Redo();

        Assert.Equal(corner.X + 2000, dormer.Boundary[0].X, 3);
        Assert.Equal(wallStart.X + 2000, ((Wall)parts[1]).Start.X, 3);
        Assert.Single(RoofJoin.Openings(document, main));
    }
}
