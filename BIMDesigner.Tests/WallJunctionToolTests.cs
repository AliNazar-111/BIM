using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>The Wall Joins tool, join cleanup, Join Geometry and walls attached to walls.</summary>
public class WallJunctionToolTests
{
    private static (BimDocument Document, WallType Type) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document, document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    private static Wall Add(BimDocument document, WallType type, Point2D start, Point2D end, Guid? level = null)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = level ?? document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static void Apply(BimDocument document, Point2D point, JunctionType type, int order) =>
        new SetWallEndsCommand(WallJunctions.Configure(document, document.Levels[0].Id, point, type, order)
            .Select(e => (e.Wall, e.AtStart, (WallJoinKind?)e.Join, (WallJoinCleanup?)null)), "Test").Redo();

    [Fact]
    public void ACornerIsFoundAndMitresUntilSetOtherwise()
    {
        var (document, type) = Project();
        var corner = new Point2D(5000, 0);
        Add(document, type, new Point2D(0, 0), corner);
        Add(document, type, corner, new Point2D(5000, 4000));

        var junction = Assert.Single(WallJunctions.On(document, document.Levels[0].Id));
        Assert.Equal(corner, junction);
        Assert.Equal(JunctionType.Mitre, WallJunctions.TypeOf(document, document.Levels[0].Id, corner));
        Assert.Equal(2, WallJunctions.Orders(document, document.Levels[0].Id, corner).Count);
    }

    [Fact]
    public void ButtWithPreviousAndNextChangesWhichWallCarriesOn()
    {
        var (document, type) = Project();
        var corner = new Point2D(5000, 0);
        var first = Add(document, type, new Point2D(0, 0), corner);
        var second = Add(document, type, corner, new Point2D(5000, 4000));
        var level = document.Levels[0].Id;

        Apply(document, corner, JunctionType.Butt, 0);
        Assert.Equal(JunctionType.Butt, WallJunctions.TypeOf(document, level, corner));
        var carrierFirst = WallJunctions.OrderOf(document, level, corner);
        var (_, firstEnd) = WallJoins.GetEndCuts(document, first, type);
        var (secondStart, _) = WallJoins.GetEndCuts(document, second, type);
        Assert.NotEqual(firstEnd.Condition, secondStart.Condition);

        Apply(document, corner, JunctionType.Butt, carrierFirst + 1);
        Assert.NotEqual(carrierFirst, WallJunctions.OrderOf(document, level, corner));
        var (_, firstEndAfter) = WallJoins.GetEndCuts(document, first, type);
        Assert.NotEqual(firstEnd.Condition, firstEndAfter.Condition);
    }

    [Fact]
    public void AChosenRunCarriesThroughACross()
    {
        var (document, type) = Project();
        var level = document.Levels[0].Id;
        var centre = new Point2D(0, 0);
        var west = Add(document, type, new Point2D(-3000, 0), centre);
        var east = Add(document, type, centre, new Point2D(3000, 0));
        var south = Add(document, type, new Point2D(0, -3000), centre);
        var north = Add(document, type, centre, new Point2D(0, 3000));

        var orders = WallJunctions.Orders(document, level, centre);
        Assert.Equal(2, orders.Count);

        foreach (var (order, carriers) in orders.Select((o, i) => (i, o)))
        {
            Apply(document, centre, JunctionType.Butt, order);
            foreach (var wall in new[] { west, east, south, north })
            {
                var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
                var cut = wall.Start == centre ? startCut : endCut;
                Assert.Equal(carriers.Contains(wall) ? WallEndCondition.Continues : WallEndCondition.Butt, cut.Condition);
            }
        }
    }

    [Fact]
    public void DisallowingAJunctionFreesItsEnds()
    {
        var (document, type) = Project();
        var level = document.Levels[0].Id;
        var corner = new Point2D(5000, 0);
        var first = Add(document, type, new Point2D(0, 0), corner);
        Add(document, type, corner, new Point2D(5000, 4000));

        new SetWallEndsCommand(WallJunctions.Ends(document, level, corner)
            .Select(e => (e.Wall, e.AtStart, (WallJoinKind?)WallJoinKind.Disallow, (WallJoinCleanup?)null)), "Disallow").Redo();

        Assert.True(WallJunctions.IsDisallowed(document, level, corner));
        Assert.False(WallJoins.GetEndCuts(document, first, type).End.IsJoined);

        // Still found, so it can be allowed again.
        Assert.Contains(corner, WallJunctions.On(document, level));
    }

    [Fact]
    public void CleanupFollowsTheEndsThenTheView()
    {
        var (document, type) = Project();
        var level = document.Levels[0].Id;
        var partition = document.TypesOf<WallType>().First(t => t.Id != type.Id && !t.Name.StartsWith("Curtain"));
        var corner = new Point2D(5000, 0);
        var first = Add(document, type, new Point2D(0, 0), corner);
        var second = Add(document, partition, corner, new Point2D(5000, 4000));
        var view = ViewReference.FloorPlan(level);

        Assert.True(WallJunctions.IsClean(document, view, first, corner, 1));

        // Walls of two types are left uncleaned by a view that cleans only matching ones.
        document.ViewSettings.SetJoinDisplay(view, WallJoinDisplay.CleanSameTypeWallJoins);
        Assert.False(WallJunctions.IsClean(document, view, first, corner, 1));

        // An end's own setting wins over the view's.
        second.StartCleanup = WallJoinCleanup.Clean;
        Assert.True(WallJunctions.IsClean(document, view, first, corner, 1));
        first.EndCleanup = WallJoinCleanup.DontClean;
        Assert.False(WallJunctions.IsClean(document, view, first, corner, 1));

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var loaded = ProjectFile.Load(path);
            Assert.Equal(WallJoinDisplay.CleanSameTypeWallJoins, loaded.ViewSettings.JoinDisplayOf(view));
            Assert.Equal(WallJoinCleanup.DontClean, loaded.Walls.Single(w => w.Id == first.Id).EndCleanup);
            Assert.Equal(WallJoinCleanup.Clean, loaded.Walls.Single(w => w.Id == second.Id).StartCleanup);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JoinGeometryJoinsParallelWallsUpTo150Apart()
    {
        var (document, type) = Project();
        var wall = Add(document, type, new Point2D(0, 0), new Point2D(5000, 0));
        var near = Add(document, type, new Point2D(0, type.Width + 100), new Point2D(5000, type.Width + 100));
        var far = Add(document, type, new Point2D(0, -type.Width - 200), new Point2D(5000, -type.Width - 200));

        Assert.True(WallLamination.CanJoinGeometry(document, wall, near));
        Assert.False(WallLamination.CanJoinGeometry(document, wall, far));

        var join = new JoinWallsCommand(wall, near, join: true);
        join.Redo();
        Assert.Contains(near, WallLamination.Partners(document, wall));

        // A door in one cuts through the other.
        var doorType = document.TypesOf<DoorType>().First();
        document.Add(new Door { HostWallId = wall.Id, TypeId = doorType.Id, LevelId = wall.LevelId, DistanceAlongWall = 2500 });
        Assert.Single(WallHoles.Of(document, near));

        new JoinWallsCommand(wall, near, join: false).Redo();
        Assert.Empty(WallLamination.Partners(document, wall));
        Assert.Empty(WallHoles.Of(document, near));
    }

    [Fact]
    public void AWallAttachesToTheWallAboveAndBelow()
    {
        var (document, type) = Project();
        var ground = document.Levels[0];
        var first = document.Levels[1];
        var lower = Add(document, type, new Point2D(0, 0), new Point2D(5000, 0), ground.Id);
        var upper = Add(document, type, new Point2D(0, 0), new Point2D(5000, 0), first.Id);
        upper.BaseOffset = 500;

        Assert.Same(upper, WallAttachments.WallAbove(document, lower));
        Assert.Same(lower, WallAttachments.WallBelow(document, upper));

        new AttachWallsCommand(new[] { (lower, true, (Guid?)upper.Id) }, "Attach").Redo();
        Assert.Equal(upper.GetBaseElevation(document), lower.GetTopElevation(document), precision: 6);

        // The upper wall rising follows through.
        upper.BaseOffset = 800;
        Assert.Equal(first.Elevation + 800, lower.GetTopElevation(document), precision: 6);

        // Two walls attached to each other do not hang the program.
        new AttachWallsCommand(new[] { (upper, false, (Guid?)lower.Id) }, "Attach").Redo();
        Assert.True(double.IsFinite(lower.GetTopElevation(document)));
        Assert.True(double.IsFinite(upper.GetBaseElevation(document)));
    }
    [Fact]
    public void AWallOpeningCutsThroughAnyWallAndGoesWithIt()
    {
        var (document, type) = Project();
        var straight = Add(document, type, new Point2D(0, 0), new Point2D(5000, 0));
        var curved = Add(document, type, new Point2D(0, 5000), new Point2D(5000, 5000));
        curved.Bulge = 0.4;

        foreach (var wall in new[] { straight, curved })
        {
            var opening = new WallOpening { HostWallId = wall.Id, LevelId = wall.LevelId, DistanceAlongWall = wall.Length / 2, Width = 1200, Height = 800, SillHeight = 1000 };
            document.Add(opening);

            var hole = Assert.Single(WallHoles.Of(document, wall));
            Assert.Equal(1200, hole.To - hole.From, precision: 6);
            Assert.Equal(1000, hole.Sill);
            Assert.Equal(1800, hole.Head);
            Assert.Equal(2, WallOpenings.GetSolidRuns(document, wall).Count);
        }

        // Split beyond it, it stays; split before it, it goes to the far half.
        var split = new SplitWallCommand(document, straight, new Point2D(1000, 0));
        split.Redo();
        var moved = document.Elements.OfType<WallOpening>().Single(o => o.HostWallId == split.Remainder.Id);
        Assert.Equal(1500, moved.DistanceAlongWall, precision: 6);
        split.Undo();
        Assert.Equal(straight.Id, moved.HostWallId);

        // Copied with its wall, it goes onto the copy.
        var copies = ElementCopy.Duplicate(document, new[] { straight });
        var copiedWall = copies.OfType<Wall>().Single();
        Assert.Equal(copiedWall.Id, copies.OfType<WallOpening>().Single().HostWallId);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var loaded = ProjectFile.Load(path);
            Assert.Equal(2, loaded.Elements.OfType<WallOpening>().Count());
            var reloaded = loaded.Elements.OfType<WallOpening>().Single(o => o.HostWallId == curved.Id);
            Assert.Equal(1200, reloaded.Width);
            Assert.Single(WallHoles.Of(loaded, loaded.Walls.Single(w => w.Id == curved.Id)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
