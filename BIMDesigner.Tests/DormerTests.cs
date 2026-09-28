using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A dormer with walls, built as Revit builds one: walls standing on the main roof, a small gable
/// roof on them joined back into the main roof (Join Roof), and the main roof opened under it
/// (Dormer Opening).
/// </summary>
public class DormerTests
{
    private static readonly double Rise = Math.Tan(40 * Math.PI / 180);

    /// <summary>
    /// A 10 m x 8 m gable roof, eaves along the south and north sides at 3 m, ridge running east
    /// to west over the middle; on its south slope, a dormer 2.4 m wide a metre up from the eave,
    /// its walls standing on the roof and its own gable roof - ridge running up the slope - on them.
    /// </summary>
    private static (BimDocument Document, Roof Main, Roof Dormer, List<Wall> Walls) Build()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var roofType = document.TypesOf<RoofType>().First();

        RoofEdge Eave() => new() { DefinesSlope = true, SlopeDegrees = 40 };
        RoofEdge Gable() => new() { DefinesSlope = false };

        var main = new Roof { TypeId = roofType.Id, LevelId = level, HeightOffset = 3000 };
        main.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        main.SetEdges(new[] { Eave(), Gable(), Eave(), Gable() });
        document.Add(main);

        var wallType = document.TypesOf<WallType>().OrderBy(t => t.Width).First();
        var walls = new List<Wall>();
        foreach (var (start, end) in new[]
                 {
                     (new Point2D(3800, 3000), new Point2D(3800, 1000)),
                     (new Point2D(3800, 1000), new Point2D(6200, 1000)),
                     (new Point2D(6200, 1000), new Point2D(6200, 3000))
                 })
        {
            var wall = new Wall { Start = start, End = end, TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 5200 };
            document.Add(wall);
            walls.Add(wall);
        }

        var dormer = new Roof { TypeId = roofType.Id, LevelId = level, HeightOffset = 5200 };
        dormer.SetBoundary(new[] { new Point2D(3700, 900), new Point2D(6300, 900), new Point2D(6300, 3000), new Point2D(3700, 3000) });
        dormer.SetEdges(new[] { Gable(), Eave(), Gable(), Eave() });
        document.Add(dormer);

        return (document, main, dormer, walls);
    }

    [Fact]
    public void AWallStandingOnARoofFollowsItsSlope()
    {
        var (document, main, _, walls) = Build();
        var cheek = walls[0];

        cheek.BaseAttachedTo = main.Id;

        // Its base is the lowest the roof's top comes under it - at its downhill end.
        var lowest = main.TopAt(document, new Point2D(3800 - 100, 1000));
        Assert.InRange(cheek.GetBaseElevation(document), lowest - 5, main.TopAt(document, new Point2D(3800, 1000)) + 1);

        // And its outline rises from there with the roof: higher at the uphill end.
        var profile = WallProfile.Of(document, cheek)!;
        var uphill = profile.Where(point => point.X < 1).Min(point => point.Y);
        var downhill = profile.Where(point => point.X > cheek.Length - 1).Min(point => point.Y);
        Assert.Equal(2000 * Rise, uphill - downhill, 60.0);
    }

    [Fact]
    public void AttachBaseFindsTheRoofAWallRisesOutOf()
    {
        var (document, main, _, walls) = Build();

        // The dormer's front wall stands through the main roof and comes out above it.
        Assert.Same(main, WallAttachments.FloorBelow(document, walls[1]));
    }

    [Fact]
    public void JoiningTheDormerRoofCarriesItBackIntoTheMainRoof()
    {
        var (document, main, dormer, _) = Build();

        var join = RoofJoin.Join(document, dormer, edge: 2, main, out var problem);
        Assert.Null(problem);
        join!.Redo();

        Assert.Equal(main.Id, dormer.JoinedTo);

        // The back edge has gone back until the dormer's ridge is buried in the main roof.
        var back = dormer.Boundary.Max(point => point.Y);
        Assert.True(back > 3800, $"The dormer only reaches back to {back:0}.");

        // Nothing of the dormer is left under the main roof's top.
        foreach (var piece in RoofSolid.Pieces(document, dormer))
        {
            var middle = new Point2D(piece.Outline.Average(p => p.X), piece.Outline.Average(p => p.Y));
            if (!main.Contains(middle)) continue;
            Assert.True(piece.Bottom.HeightAt(middle) >= main.TopAt(document, middle) - 1,
                $"A piece of the dormer at {middle} is inside the main roof.");
        }

        // In plan, it shows only where it is above the main roof: less than all of it.
        var shown = RoofJoin.Visible(document, dormer).Sum(Polygon2D.Area);
        Assert.True(shown < dormer.Area - 1e5, $"Shown {shown:0} of {dormer.Area:0}.");

        // Unjoined, the trim goes; undone, so does the join.
        join.Undo();
        Assert.Null(dormer.JoinedTo);
        Assert.Equal(3000, dormer.Boundary.Max(point => point.Y), precision: 6);
    }

    [Fact]
    public void ADormerOpeningCutsTheMainRoofBetweenTheDormerWalls()
    {
        var (document, main, dormer, walls) = Build();
        foreach (var wall in walls)
        {
            wall.BaseAttachedTo = main.Id;
            wall.TopAttachedTo = dormer.Id;
        }

        RoofJoin.Join(document, dormer, 2, main, out _)!.Redo();
        var before = main.SlopingArea(document);

        new SetDormerOpeningCommand(main, dormer.Id, open: true).Redo();

        var opening = Assert.Single(RoofJoin.Openings(document, main));
        var half = document.GetWallType(walls[0])!.Structure.TotalWidth / 2;

        // Between the cheek walls' inside faces, and from the front wall's inside face back.
        Assert.Equal(3800 + half, opening.Min(point => point.X), 1.0);
        Assert.Equal(6200 - half, opening.Max(point => point.X), 1.0);
        Assert.Equal(1000 + half, opening.Min(point => point.Y), 1.0);

        // The main roof has a hole there: no piece of it covers the middle of the opening.
        var middle = new Point2D(5000, opening.Min(point => point.Y) + 300);
        Assert.DoesNotContain(RoofSolid.Pieces(document, main), piece => Polygon2D.Contains(piece.Outline, middle));

        // And its covering is less by the hole.
        Assert.True(main.SlopingArea(document) < before - 1e6);

        // The 3D model builds, and the whole thing saves and loads.
        Assert.Contains(ModelMeshBuilder.Build(document), mesh => mesh.ElementId == main.Id);
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var mainAgain = reloaded.Elements.OfType<Roof>().Single(roof => roof.Id == main.Id);
        Assert.Single(mainAgain.DormerOpenings);
        Assert.Equal(main.Id, reloaded.Elements.OfType<Roof>().Single(roof => roof.Id == dormer.Id).JoinedTo);
    }

    [Fact]
    public void ARoofThatCanNeverMeetTheOtherIsNotJoined()
    {
        var (document, main, dormer, _) = Build();
        dormer.HeightOffset = 20000;

        Assert.Null(RoofJoin.Join(document, dormer, 2, main, out var problem));
        Assert.NotNull(problem);
    }
}
