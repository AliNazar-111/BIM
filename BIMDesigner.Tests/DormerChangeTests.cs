using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// A dormer changed as one after it is built: its shape and size set in Properties, its walls
/// and roof following together, and what is in and on them going with them.
/// </summary>
public class DormerChangeTests
{
    private static (BimDocument Document, Roof Main, Roof Dormer, List<Wall> Walls) Built()
    {
        var (document, main, wallType) = DormerToolTests.House(10000, 8000, 40);
        var before = document.Walls.Count();
        Assert.NotNull(Dormers.Add(document, main, new Point2D(5000, 800), DormerSettings.Default, wallType.Id, out _, out _));

        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        return (document, main, dormer, document.Walls.Skip(before).ToList());
    }

    /// <summary>What the Properties panel does: the parameter written, the change recorded, the roofs followed as after any edit.</summary>
    private static (bool Taken, string? Message) Set(BimDocument document, UndoStack history, Roof dormer, string name, object value)
    {
        var parameter = dormer.GetInstanceParameters(document).Single(p => p.Name == name);
        var old = parameter.Value;
        var taken = parameter.TrySet(value);
        var message = parameter.TakeMessage();
        if (taken) history.Record(new ParameterChangeCommand(parameter, old, parameter.Value));

        Follow(document);
        return (taken, message);
    }

    private static void Follow(BimDocument document)
    {
        RoofSketch.FollowWalls(document);
        Dormers.FollowRoofs(document);
    }

    /// <summary>Everything about a dormer's shape, rounded, to tell whether it is as it was.</summary>
    private static List<double> Shape(Roof dormer, IEnumerable<Wall> walls) =>
        walls.SelectMany(wall => new[] { wall.Start.X, wall.Start.Y, wall.End.X, wall.End.Y, wall.UnconnectedHeight })
            .Concat(dormer.Boundary.SelectMany(corner => new[] { corner.X, corner.Y }))
            .Concat(dormer.Edges.SelectMany(edge => new[] { edge.DefinesSlope ? 1 : 0, edge.SlopeDegrees, edge.Overhang }))
            .Append(dormer.HeightOffset)
            .Select(value => Math.Round(value, 6))
            .ToList();

    private static BimWindow WindowIn(BimDocument document, Wall wall)
    {
        var type = document.TypesOf<WindowType>().First(t => t.Height == 1200 && t.Width is >= 900 and <= 1500);
        var window = new BimWindow { HostWallId = wall.Id, LevelId = wall.LevelId, TypeId = type.Id, SillHeight = 150, DistanceAlongWall = wall.Length / 2 };
        document.Add(window);
        return window;
    }

    [Fact]
    public void WiderItIsTheSameDormerWider()
    {
        var (document, main, dormer, walls) = Built();
        var history = new UndoStack();
        var middle = walls[1].LocationCurve.PointAt(walls[1].Length / 2);
        var parts = Dormers.Parts(document, dormer).Select(part => part.Id).ToList();
        var was = Shape(dormer, walls);

        var (taken, message) = Set(document, history, dormer, "Dormer Width", 3200.0);

        Assert.True(taken, message);
        Assert.Equal(3200, walls[1].Length, 1);
        Assert.Equal(3200, dormer.Dormer!.Width);
        Assert.Equal(0, walls[1].LocationCurve.PointAt(walls[1].Length / 2).DistanceTo(middle), 3);
        Assert.Equal(parts, Dormers.Parts(document, dormer).Select(part => part.Id));
        Assert.Single(RoofJoin.Openings(document, main));
        Assert.Contains("3.2", Dormers.Describe(document, dormer).Replace(",", "."));

        // Left to follow the roofs as after any edit, it stays as changed.
        var changed = Shape(dormer, walls);
        Follow(document);
        Assert.Equal(changed, Shape(dormer, walls));

        // One undo puts it all back.
        history.Undo();
        Follow(document);
        Assert.Equal(was, Shape(dormer, walls));
        Assert.Equal(DormerSettings.Default, dormer.Dormer);

        history.Redo();
        Follow(document);
        Assert.Equal(changed, Shape(dormer, walls));
    }

    [Fact]
    public void AGableTurnedShedTakesItsGutterToTheFront()
    {
        var (document, _, dormer, walls) = Built();
        var history = new UndoStack();

        var gutter = new Gutter { RoofId = dormer.Id, LevelId = dormer.LevelId, TypeId = document.TypesOf<GutterType>().First().Id };
        gutter.EdgeIds.AddRange(RoofEdgeSweeps.GutterEdges(document, dormer));
        var fascia = new Fascia { RoofId = dormer.Id, LevelId = dormer.LevelId, TypeId = document.TypesOf<FasciaType>().First().Id };
        fascia.EdgeIds.AddRange(RoofEdgeSweeps.FasciaEdges(document, dormer));
        document.Add(gutter);
        document.Add(fascia);
        var fasciaEdges = fascia.EdgeIds.ToList();
        Assert.Equal(2, gutter.EdgeIds.Count);

        var (taken, message) = Set(document, history, dormer, "Roof Shape", "Shed");

        Assert.True(taken, message);
        Assert.Equal(DormerShape.Shed, dormer.Dormer!.Shape);
        Assert.True(dormer.Edges.First(edge => edge.DefinesSlope).SlopeDegrees <= 15);

        // The gutter runs along the eave a shed has - its front - and the fascia still all round.
        var front = dormer.Edges.Single(edge => edge.WallId == walls[1].Id);
        Assert.Equal(new[] { front.Id }, gutter.EdgeIds);
        Assert.Equal(fasciaEdges.OrderBy(id => id), fascia.EdgeIds.OrderBy(id => id));
        Assert.NotEmpty(RoofEdgeSweeps.Mesh(document, gutter, gutter.LevelId).Positions);

        history.Undo();
        Assert.Equal(2, gutter.EdgeIds.Count);
        Assert.Equal(DormerShape.Gable, dormer.Dormer!.Shape);
    }

    [Theory]
    [InlineData("Hip")]
    [InlineData("Shed")]
    [InlineData("Gable")]
    public void EveryShapeCanBeTurnedIntoEveryOther(string shape)
    {
        foreach (var from in new[] { "Gable", "Shed", "Hip" }.Where(from => from != shape))
        {
            var (document, main, dormer, _) = Built();
            var history = new UndoStack();
            if (from != "Gable") Assert.True(Set(document, history, dormer, "Roof Shape", from).Taken);

            var (taken, message) = Set(document, history, dormer, "Roof Shape", shape);
            Assert.True(taken, $"{from} to {shape}: {message}");
            Assert.Equal(shape, dormer.Dormer!.Shape.ToString());
            Assert.Single(RoofJoin.Openings(document, main));
        }
    }

    [Fact]
    public void AskedHigherThanFitsItIsMadeAsHighAsFits()
    {
        var (document, _, dormer, walls) = Built();
        var history = new UndoStack();

        var (taken, message) = Set(document, history, dormer, "Dormer Height", 700.0);
        Assert.True(taken, message);
        Assert.Equal(700, Dormers.HeightOf(document, dormer), 1);

        (taken, message) = Set(document, history, dormer, "Dormer Height", 6000.0);
        Assert.True(taken, message);
        Assert.Contains("as high as", message);
        Assert.InRange(Dormers.HeightOf(document, dormer), 701, 5999);

        // Asked higher again, there is nothing more to give - and it says so.
        var was = Shape(dormer, walls);
        (taken, message) = Set(document, history, dormer, "Dormer Height", 7000.0);
        Assert.False(taken);
        Assert.Contains("already", message);
        Assert.Equal(was, Shape(dormer, walls));
    }

    [Fact]
    public void TooWideForTheRoofItStaysAsItWasAndSaysWhy()
    {
        var (document, _, dormer, walls) = Built();
        var was = Shape(dormer, walls);

        var (taken, message) = Set(document, new UndoStack(), dormer, "Dormer Width", 30000.0);

        Assert.False(taken);
        Assert.False(string.IsNullOrEmpty(message));
        Assert.Equal(was, Shape(dormer, walls));
        Assert.Equal(DormerSettings.Default, dormer.Dormer);
    }

    [Fact]
    public void AWindowInItIsMadeSmallerWithItAndBackToItsOwnSize()
    {
        var (document, _, dormer, walls) = Built();
        var history = new UndoStack();
        var window = WindowIn(document, walls[1]);
        var type = document.FindType<OpeningType>(window.TypeId)!;

        Assert.True(Set(document, history, dormer, "Dormer Width", 1000.0).Taken);
        Assert.True(window.WidthOf(type) < type.Width);
        Assert.Equal(window.WidthOf(type) / window.HeightOf(type), type.Width / type.Height, 2);
        Assert.Equal(walls[1].Length / 2, window.DistanceAlongWall, 1);

        Assert.True(Set(document, history, dormer, "Dormer Width", 2400.0).Taken);
        Assert.Null(window.WidthOverride);
        Assert.Equal(type.Width, window.WidthOf(type));

        // Undone, it is the size it was.
        history.Undo();
        Assert.True(window.WidthOf(type) < type.Width);
    }

    [Fact]
    public void ItsPropertiesAreTheDormersNotEachEdges()
    {
        var (document, main, dormer, _) = Built();
        var names = dormer.GetInstanceParameters(document).Select(p => p.Name).ToList();

        Assert.Contains("Dormer Width", names);
        Assert.Contains("Dormer Height", names);
        Assert.DoesNotContain(names, name => name.StartsWith("Slope at Edge"));
        Assert.Equal(new[] { "Gable", "Shed", "Hip" },
            dormer.GetInstanceParameters(document).Single(p => p.Name == "Roof Shape").AllowedValues);

        // The main roof keeps its own.
        Assert.DoesNotContain("Dormer Width", main.GetInstanceParameters(document).Select(p => p.Name));
    }

    [Fact]
    public void ChangedAndSavedItOpensAsChanged()
    {
        var (document, _, dormer, _) = Built();
        Assert.True(Set(document, new UndoStack(), dormer, "Dormer Width", 2800.0).Taken);
        Assert.True(Set(document, new UndoStack(), dormer, "Roof Shape", "Hip").Taken);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = reloaded.Elements.OfType<Roof>().Single(roof => roof.Id == dormer.Id);

        Assert.Equal(dormer.Dormer, again.Dormer);
        Assert.Equal(4, Dormers.Parts(reloaded, again).Count);
    }

    [Fact]
    public void GrownTallerAsTheRoofGivesItRoomItsWallsAndWindowGrowWithIt()
    {
        // Made low on a shallow roof, with a window made small to suit it.
        var (document, main, wallType) = DormerToolTests.House(10000, 8000, 30);
        var before = document.Walls.Count();
        Assert.NotNull(Dormers.Add(document, main, new Point2D(5000, 600), DormerSettings.Default, wallType.Id, out _, out var used));
        Assert.True(used.Height < DormerSettings.Default.Height - 100);

        var dormer = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
        var walls = document.Walls.Skip(before).ToList();
        var front = walls[1];
        var type = document.TypesOf<WindowType>().First(t => t.Height == 1200 && t.Width is >= 900 and <= 1500);
        var size = WallOpenings.SizeToFit(document, front, front.Length / 2, 150, type.Width, type.Height)!.Value;
        var window = new BimWindow
        {
            HostWallId = front.Id, LevelId = front.LevelId, TypeId = type.Id, SillHeight = size.Sill, DistanceAlongWall = size.Along,
            WidthOverride = size.Width, HeightOverride = size.Height
        };
        document.Add(window);
        Assert.True(size.Width < type.Width);

        var low = Dormers.HeightOf(document, dormer);
        var sideLength = walls[0].Length;

        // The house made deeper, the roof over it with it, gives it room to stand as high as it
        // was asked to be: the ridge is further back and higher.
        var small = main.Boundary.ToList();
        main.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 14000), new Point2D(0, 14000) });
        Follow(document);

        Assert.True(Dormers.HeightOf(document, dormer) > low + 300);
        Assert.True(walls[0].Length > sideLength + 300);

        // No gap under its roof: each side wall runs back to where the main roof has risen to its eaves.
        foreach (var side in new[] { walls[0], walls[2] })
        {
            var back = side.Start.Y > side.End.Y ? side.Start : side.End;
            Assert.True(main.TopAt(document, back) >= dormer.BaseElevation(document), "the side wall stops short of the main roof");
        }

        Assert.Null(window.WidthOverride);
        Assert.Equal(type.Width, window.WidthOf(type));

        // And the same steps back: made small again, it is as it was.
        main.SetBoundary(small);
        Follow(document);
        Assert.Equal(low, Dormers.HeightOf(document, dormer), 1);
        Assert.Equal(sideLength, walls[0].Length, 1);
    }
}
