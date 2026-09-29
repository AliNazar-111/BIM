using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// Editing a model leaves the rest of it as it should be: a window stays where it is in the
/// building when the room around it is stretched, nothing is left pointing at what was
/// deleted, and a dormer goes on standing on its roof when that roof is changed.
/// </summary>
public class EditingKeepsTheModelTests
{
    /// <summary>A 10 m x 7 m box of walls drawn clockwise, so their outsides face out, with a window in the front wall.</summary>
    private static (BimDocument Document, List<Wall> Walls, BimWindow Window) Box()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));

        // West, north, east, then the front - running from its east end to its west end.
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 7000), new Point2D(10000, 7000), new Point2D(10000, 0) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall
        {
            Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000
        }).ToList();
        foreach (var wall in walls) document.Add(wall);

        var window = new BimWindow
        {
            HostWallId = walls[3].Id, LevelId = level, TypeId = document.TypesOf<WindowType>().First(t => t.Width == 1200).Id,
            DistanceAlongWall = 7500, SillHeight = 900
        };
        document.Add(window);
        return (document, walls, window);
    }

    [Fact]
    public void DraggingACornerLeavesTheWindowsWhereTheyAre()
    {
        // The east wall dragged out 1.5 m, taking the front wall's start with it. The window is
        // measured from that start, and would slide 1.5 m along with it.
        var (document, walls, window) = Box();
        var (east, front) = (walls[2], walls[3]);
        var at = window.GetCentre(front);
        var shift = new Vector2D(1500, 0);

        var drag = new CompositeCommand("Drag", new IUndoableCommand[]
        {
            new MoveWallCommand(east, east.Start, east.End, east.Start + shift, east.End + shift, "Move").KeepingOpenings(document),
            new MoveWallCommand(front, front.Start, front.End, front.Start + shift, front.End, "Move").KeepingOpenings(document)
        });
        drag.Redo();

        Assert.Equal(at.X, window.GetCentre(front).X, 6);
        Assert.Equal(at.Y, window.GetCentre(front).Y, 6);

        drag.Undo();
        Assert.Equal(7500, window.DistanceAlongWall, 9);
    }

    [Fact]
    public void AWallMovedWholeCarriesItsWindowsWithIt()
    {
        var (document, walls, window) = Box();
        var front = walls[3];
        var shift = new Vector2D(0, -1000);

        new MoveWallCommand(front, front.Start, front.End, front.Start + shift, front.End + shift, "Move").KeepingOpenings(document).Redo();

        Assert.Equal(7500, window.DistanceAlongWall, 9);
    }

    [Fact]
    public void ShorteningAWallPastAWindowBringsItBackInside()
    {
        var (document, walls, window) = Box();
        var front = walls[3];
        var type = document.FindType<OpeningType>(window.TypeId)!;

        var shorten = front.LengthChange(document, 7000)!;
        shorten.Redo();

        Assert.True(window.FitsWithin(front, type), $"The window runs off the wall: {window.GetSpan(type)} of {front.Length:0}.");

        shorten.Undo();
        Assert.Equal(7500, window.DistanceAlongWall, 9);
    }

    [Fact]
    public void DeletingARoofLetsGoOfTheWallsAttachedToIt()
    {
        var (document, walls, _) = Box();
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = walls[0].LevelId, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 7000), new Point2D(0, 7000) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i % 2 == 0, SlopeDegrees = 35 }));
        document.Add(roof);
        foreach (var wall in walls) wall.TopAttachedTo = roof.Id;

        var delete = new DeleteElementsCommand(document, new[] { roof });
        delete.Redo();
        Assert.All(walls, wall => Assert.Null(wall.TopAttachedTo));

        delete.Undo();
        Assert.All(walls, wall => Assert.Equal(roof.Id, wall.TopAttachedTo));
    }

    /// <summary>A 10 m square house under a hip roof, with a gable dormer on its front slope.</summary>
    private static (BimDocument Document, Roof Main, Roof Dormer) DormerHouse(double pitch)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 });

        var main = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3000 };
        main.SetBoundary(corners);
        main.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i % 2 == 0, SlopeDegrees = pitch }));
        document.Add(main);

        Assert.NotNull(Dormers.Add(document, main, new Point2D(5000, 800), DormerSettings.Default, type.Id, out _, out _));
        return (document, main, document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main)));
    }

    private static void Pitch(Roof roof, double degrees) =>
        new SetRoofSketchCommand(roof, roof.Boundary,
            roof.Edges.Select(edge => { var copy = edge.Copy(); copy.SlopeDegrees = degrees; return copy; })).Redo();

    [Fact]
    public void ADormerFollowsItsRoofWhenTheSlopeChangesAndComesBackWhenItChangesBack()
    {
        var (document, main, dormer) = DormerHouse(45);
        Assert.False(Dormers.FollowRoofs(document), "A dormer just made should already be where it follows to.");
        var (height, outline) = (dormer.HeightOffset, dormer.Boundary.ToList());

        // Shallower: the dormer comes down, and its roof still runs back into the main roof
        // rather than coming out above it.
        Pitch(main, 38);
        Assert.True(Dormers.FollowRoofs(document));
        Assert.True(dormer.HeightOffset < height);

        var back = dormer.Boundary.OrderByDescending(corner => corner.Y).Take(2).ToList();
        var middle = back[0].MidpointTo(back[1]);
        Assert.True(dormer.TopAt(document, middle) < main.TopAt(document, middle), "The dormer's roof comes out above the main roof.");

        // Back as it was: so is the dormer.
        Pitch(main, 45);
        Dormers.FollowRoofs(document);
        Assert.Equal(height, dormer.HeightOffset, 3);
        for (var i = 0; i < outline.Count; i++) Assert.True(outline[i].DistanceTo(dormer.Boundary[i]) < 1e-3);
    }

    [Fact]
    public void AWindowWithNoRoomLeftInItsWallIsNotDrawn()
    {
        var (document, walls, window) = Box();
        walls[3].UnconnectedHeight = 350;
        var type = document.FindType<OpeningType>(window.TypeId)!;

        Assert.Equal(0, window.Placed(document, type).Height);
    }
}

/// <summary>Pasting a storey onto the next: what comes up is on its own, not reaching back for the floor below's roof.</summary>
[Collection("Wpf")]
public class PasteUpAStoreyTests
{
    [Fact]
    public void WallsPastedUpAFloorLetGoOfTheRoofBelowAndBringTheirWindows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();

                var document = BimDocument.CreateDefault();
                var (ground, first) = (document.Levels[0], document.Levels[1]);
                var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
                var corners = new[] { new Point2D(0, 0), new Point2D(0, 7000), new Point2D(10000, 7000), new Point2D(10000, 0) };
                var walls = Enumerable.Range(0, 4).Select(i => new Wall
                {
                    Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = ground.Id, TopLevelId = first.Id
                }).ToList();
                foreach (var wall in walls) document.Add(wall);
                document.Add(new BimWindow { HostWallId = walls[3].Id, LevelId = ground.Id, TypeId = document.TypesOf<WindowType>().First().Id, DistanceAlongWall = 5000, SillHeight = 900 });

                var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = ground.Id, HeightOffset = 3000 };
                roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 7000), new Point2D(0, 7000) });
                roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i % 2 == 0, SlopeDegrees = 35 }));
                document.Add(roof);
                foreach (var wall in walls) wall.TopAttachedTo = roof.Id;

                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = ground.Id };
                plan.SelectMany(walls);
                plan.CopySelection();
                plan.ActiveLevelId = first.Id;
                plan.Paste(inPlace: true);

                var upper = document.Walls.Where(wall => wall.LevelId == first.Id).ToList();
                Assert.Equal(4, upper.Count);
                Assert.All(upper, wall => Assert.Null(wall.TopAttachedTo));
                Assert.Single(document.Elements.OfType<BimWindow>(), window => upper.Any(wall => wall.Id == window.HostWallId));

                // The walls left below are still attached to their roof.
                Assert.All(walls, wall => Assert.Equal(roof.Id, wall.TopAttachedTo));
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
