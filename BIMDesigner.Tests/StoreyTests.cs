using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A floor - or a flat roof laid as one - divides the walls running up through it, whichever
/// was drawn first, and a floor with a room over it at a height no level is at is made a storey.
/// </summary>
public class StoreyTests
{
    /// <summary>A glass wall 6 m long rising 6 m from the first floor along y = 0, and a flat roof 3 m up on that floor, drawn to the wall's line or from where it says.</summary>
    private static (BimDocument Document, Wall Glass, Roof Roof) Building(double from = 0)
    {
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        var glass = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = document.TypesOf<CurtainWallType>().First(type => type.Name.Contains("Storefront")).Id,
            LevelId = first.Id, UnconnectedHeight = 6000
        };
        var roof = new Roof { LevelId = first.Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, from), new Point2D(6000, from), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(glass);
        return (document, glass, roof);
    }

    [Fact]
    public void AFlatRoofPutAcrossATallWallSplitsItAndIsMadeTheFloorOfTheStoreyOverIt()
    {
        var (document, glass, roof) = Building();
        var levels = document.Levels.Count;
        document.Add(roof);
        var top = roof.GetTopElevation(document);

        var divided = Storeys.Divide(document, new[] { roof });
        Assert.NotNull(divided);

        // The glass stops at the roof's underside; the part over it stands on its top, up to where the glass went.
        Assert.Equal(roof.GetBottomElevation(document), glass.GetTopElevation(document), 1);
        var upper = Assert.Single(document.Walls, wall => wall != glass);
        Assert.Equal(top, upper.GetBaseElevation(document), 1);
        Assert.Equal(3000 + 6000, Storeys.Span(document, upper).Head, 1);

        // A storey made on it - the second floor, over the first - standing on it, with the glass over it on it.
        var storey = Assert.Single(document.Levels, level => level.StandsOn == roof.Id);
        Assert.Equal(("Second Floor", top), (storey.Name, storey.Elevation));
        Assert.Equal(storey.Id, upper.LevelId);
        Assert.Equal(levels + 1, document.Levels.Count);

        // Nothing runs through the roof now, so it is not cut back from the glass: its edge shows between the two.
        Assert.Empty(Storeys.Through(document, roof));
        Assert.Empty(SlabEdges.CurtainWallsAt(document, roof));

        // Raised, the storey and the glass on it go up with it, the glass under it with its underside.
        roof.HeightOffset += 500;
        Assert.Equal((top + 500, top + 500), (storey.Elevation, upper.GetBaseElevation(document)));
        Assert.Equal(roof.GetBottomElevation(document), glass.GetTopElevation(document), 1);
        roof.HeightOffset -= 500;

        // Undone: one wall again, as high as it was, and no new storey.
        divided.Value.Done.Undo();
        Assert.Single(document.Walls);
        Assert.Equal(levels, document.Levels.Count);
        Assert.Equal(3000 + 6000, Storeys.Span(document, glass).Head, 1);
    }

    [Fact]
    public void AWallPutUpThroughAFloorAfterItIsSplitTooItsWindowsGoingWithTheirPart()
    {
        var document = BimDocument.CreateDefault();
        var first = document.Levels[1];
        var floor = new Roof { LevelId = first.Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 3000 };
        floor.SetBoundary(new[] { new Point2D(-200, -200), new Point2D(6200, -200), new Point2D(6200, 5000), new Point2D(-200, 5000) });
        document.Add(floor);

        // The wall drawn after the floor, from the first floor up 6 m, with a window low in it and one high.
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id,
            LevelId = first.Id, UnconnectedHeight = 6000
        };
        document.Add(wall);
        var type = document.TypesOf<WindowType>().First().Id;
        var low = new Window { TypeId = type, LevelId = first.Id, HostWallId = wall.Id, DistanceAlongWall = 1500, SillHeight = 900 };
        var high = new Window { TypeId = type, LevelId = first.Id, HostWallId = wall.Id, DistanceAlongWall = 4500, SillHeight = 4000 };
        document.Add(low);
        document.Add(high);

        Assert.NotNull(Storeys.Divide(document, new[] { wall }));
        var upper = Assert.Single(document.Walls, other => other != wall);
        var rise = floor.GetTopElevation(document) - (first.Elevation);
        Assert.Equal((wall.Id, 900.0), (low.HostWallId, low.SillHeight));
        Assert.Equal(upper.Id, high.HostWallId);
        Assert.Equal(4000 - rise, high.SillHeight, 1);
    }

    [Fact]
    public void AFloorStoppingShortOfAGlassWallLeavesItRunningOnPast()
    {
        // Drawn to the inside of the glass: the glass is hung in front of it, cut back from it, and not split.
        var (document, glass, roof) = Building(from: 100);
        document.Add(roof);
        Assert.Empty(Storeys.Through(document, roof));
        Assert.Null(Storeys.Divide(document, new[] { roof }));
        Assert.Single(document.Walls);
        Assert.Single(SlabEdges.CurtainWallsAt(document, roof));
        Assert.Equal(3000 + 6000, Storeys.Span(document, glass).Head, 1);
    }

    [Fact]
    public void PutInAsOneStepItIsUndoneAndRedoneAsOne()
    {
        var (document, _, roof) = Building();
        var levels = document.Levels.Count;
        var history = new UndoStack();
        var put = new FollowedCommand(new AddElementCommand(document, roof, "Roof"), () => Storeys.Divide(document, new[] { roof }));
        history.Execute(put);
        Assert.Contains("Second Floor", put.Said);
        Assert.Equal((2, levels + 1), (document.Walls.Count(), document.Levels.Count));

        history.Undo();
        Assert.Equal((1, levels), (document.Walls.Count(), document.Levels.Count));
        Assert.DoesNotContain(roof, document.Elements);

        history.Redo();
        Assert.Equal((2, levels + 1), (document.Walls.Count(), document.Levels.Count));
    }
}
