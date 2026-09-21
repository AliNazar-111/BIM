using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Flipping walls, drawing them off the clicked line, and hiding them by function per view.
/// </summary>
public class WallDrawingTests
{
    private static (BimDocument Document, WallType Interior, WallType Exterior) Project()
    {
        var document = BimDocument.CreateDefault();

        return (
            document,
            document.TypesOf<WallType>().First(t => t.Function == WallFunction.Interior),
            document.TypesOf<WallType>().Single(t => t.Function == WallFunction.Exterior));
    }

    private static Wall Add(BimDocument document, WallType type, Point2D start, Point2D end)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = document.Levels[0].Id };
        document.Add(wall);
        return wall;
    }

    // ---- flip -------------------------------------------------------------------

    [Fact]
    public void FlippingSwapsTheSidesAboutTheDrawnLine()
    {
        var (document, _, exterior) = Project();
        var wall = Add(document, exterior, new Point2D(0, 0), new Point2D(5000, 0));
        wall.LocationLine = WallLocationLine.FinishFaceExterior;

        var outsideBefore = wall.ExteriorNormal;
        var (bodyBefore, _) = wall.GetBodyCentreline(exterior.Structure);

        var flip = new FlipWallsCommand(new[] { wall });
        flip.Redo();

        Assert.Equal(-outsideBefore, wall.ExteriorNormal);
        Assert.Equal(new Point2D(0, 0), wall.Start);

        // Drawn to its exterior face, the wall swings over that face to the other side.
        var (bodyAfter, _) = wall.GetBodyCentreline(exterior.Structure);
        Assert.Equal(-bodyBefore.Y, bodyAfter.Y, precision: 6);

        flip.Undo();
        Assert.Equal(outsideBefore, wall.ExteriorNormal);
    }

    [Fact]
    public void SeveralWallsFlipAsOneStep()
    {
        var (document, interior, _) = Project();
        var a = Add(document, interior, new Point2D(0, 0), new Point2D(5000, 0));
        var b = Add(document, interior, new Point2D(5000, 0), new Point2D(5000, 4000));

        var flip = new FlipWallsCommand(new[] { a, b, a });
        Assert.Equal("Flip 2 Walls", flip.Name);

        flip.Redo();
        Assert.True(a.Flipped && b.Flipped);

        flip.Undo();
        Assert.False(a.Flipped || b.Flipped);
    }

    // ---- offset while drawing ---------------------------------------------------

    [Fact]
    public void AnOffsetMovesTheWallTowardItsExterior()
    {
        var (start, end) = WallDrawing.OffsetSegment(new Point2D(0, 0), new Point2D(5000, 0), 300, flipped: false);

        Assert.Equal(new Point2D(0, 300), start);
        Assert.Equal(new Point2D(5000, 300), end);

        var (flippedStart, _) = WallDrawing.OffsetSegment(new Point2D(0, 0), new Point2D(5000, 0), 300, flipped: true);
        Assert.Equal(new Point2D(0, -300), flippedStart);
    }

    [Fact]
    public void ARectangleDrawnWithAnOffsetClosesAtEveryCorner()
    {
        // Drawn clockwise, the exterior (left of travel) is outside, so a positive offset
        // grows the rectangle by the offset all round.
        var clicks = new[]
        {
            new Point2D(0, 0), new Point2D(0, 4000), new Point2D(6000, 4000), new Point2D(6000, 0), new Point2D(0, 0)
        };
        const double offset = 200;

        var segments = new List<(Point2D Start, Point2D End)>();
        for (var i = 0; i + 1 < clicks.Length; i++)
            segments.Add(WallDrawing.OffsetSegment(clicks[i], clicks[i + 1], offset, flipped: false));

        // Each click after the first is a corner between the segment before and after it,
        // and the last click, back on the first, closes the loop.
        for (var i = 0; i < segments.Count; i++)
        {
            var next = (i + 1) % segments.Count;
            var corner = WallDrawing.Corner(segments[i], segments[next], clicks[i + 1], offset);

            Assert.NotNull(corner);
            segments[i] = (segments[i].Start, corner!.Value);
            segments[next] = (corner.Value, segments[next].End);
        }

        Assert.Equal(new Point2D(-200, -200), segments[0].Start);
        Assert.Equal(new Point2D(-200, 4200), segments[0].End);
        Assert.Equal(new Point2D(6200, 4200), segments[1].End);
        Assert.Equal(new Point2D(6200, -200), segments[2].End);
        Assert.Equal(segments[0].Start, segments[3].End);
    }

    [Fact]
    public void WallsCarryingOnInLineHaveNoCorner()
    {
        var first = WallDrawing.OffsetSegment(new Point2D(0, 0), new Point2D(3000, 0), 200, false);
        var second = WallDrawing.OffsetSegment(new Point2D(3000, 0), new Point2D(6000, 0), 200, false);

        Assert.Null(WallDrawing.Corner(first, second, new Point2D(3000, 0), 200));
        Assert.Equal(first.End, second.Start);
    }

    [Fact]
    public void AnAlmostStraightTurnMeetsRightAtTheOffsetCorner()
    {
        var first = WallDrawing.OffsetSegment(new Point2D(0, 0), new Point2D(3000, 0), 200, false);
        var second = WallDrawing.OffsetSegment(new Point2D(3000, 0), new Point2D(6000, 10), 200, false);

        var corner = WallDrawing.Corner(first, second, new Point2D(3000, 0), 200);

        Assert.NotNull(corner);
        Assert.True(corner!.Value.DistanceTo(new Point2D(3000, 200)) < 1);
    }

    [Fact]
    public void DoublingBackDoesNotThrowTheCornerMilesAway()
    {
        // A hairpin: the offset lines only cross far behind the turn.
        var first = WallDrawing.OffsetSegment(new Point2D(0, 0), new Point2D(3000, 0), 200, false);
        var second = WallDrawing.OffsetSegment(new Point2D(3000, 0), new Point2D(0, 30), 200, false);

        Assert.Null(WallDrawing.Corner(first, second, new Point2D(3000, 0), 200));
    }

    // ---- wall functions per view ------------------------------------------------

    private static (BimDocument Document, Wall Outside, Wall Inside, Door Door, Tag Tag) FilteredProject()
    {
        var (document, interior, exterior) = Project();
        var outside = Add(document, exterior, new Point2D(0, 0), new Point2D(8000, 0));
        var inside = Add(document, interior, new Point2D(4000, 0), new Point2D(4000, 5000));

        var door = new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id,
            LevelId = inside.LevelId,
            HostWallId = inside.Id,
            DistanceAlongWall = 2500
        };
        document.Add(door);

        var tag = new Tag { TargetId = door.Id, LevelId = inside.LevelId, Position = new Point2D(4500, 2500) };
        document.Add(tag);

        return (document, outside, inside, door, tag);
    }

    [Fact]
    public void HidingAFunctionTakesItsWallsAndWhatTheyCarryOutOfThatView()
    {
        var (document, outside, inside, door, tag) = FilteredProject();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);

        new SetWallFunctionVisibilityCommand(document, plan, WallFunction.Interior, visible: false).Redo();
        var shows = document.ViewSettings.FilterFor(document, plan);

        Assert.True(shows(outside));
        Assert.False(shows(inside));
        Assert.False(shows(door));
        Assert.False(shows(tag));
    }

    [Fact]
    public void EachViewKeepsItsOwnFilters()
    {
        var (document, _, inside, _, _) = FilteredProject();
        var ground = ViewReference.FloorPlan(document.Levels[0].Id);

        new SetWallFunctionVisibilityCommand(document, ground, WallFunction.Interior, visible: false).Redo();

        Assert.False(document.ViewSettings.FilterFor(document, ground)(inside));
        Assert.True(document.ViewSettings.FilterFor(document, ViewReference.FloorPlan(document.Levels[1].Id))(inside));
        Assert.True(document.ViewSettings.FilterFor(document, ViewReference.Model3D)(inside));
    }

    [Fact]
    public void ThreeDAndSectionsLeaveOutHiddenWalls()
    {
        var (document, outside, inside, _, _) = FilteredProject();
        var marker = new SectionMarker
        {
            Name = "A",
            Start = new Point2D(2000, -2000),
            End = new Point2D(6000, 2000),
            LevelId = document.Levels[0].Id
        };
        document.Add(marker);

        new SetWallFunctionVisibilityCommand(document, ViewReference.Model3D, WallFunction.Exterior, false).Redo();
        new SetWallFunctionVisibilityCommand(document, ViewReference.Section(marker.Id), WallFunction.Exterior, false).Redo();

        var meshes = ModelMeshBuilder.Build(document, document.ViewSettings.FilterFor(document, ViewReference.Model3D));
        Assert.DoesNotContain(meshes, mesh => mesh.ElementId == outside.Id);
        Assert.Contains(meshes, mesh => mesh.ElementId == inside.Id);

        var everything = SectionProjection.Build(document, marker);
        var filtered = SectionProjection.Build(document, marker,
            document.ViewSettings.FilterFor(document, ViewReference.Section(marker.Id)));

        Assert.Contains(everything.Pieces, piece => piece.ElementId == outside.Id);
        Assert.DoesNotContain(filtered.Pieces, piece => piece.ElementId == outside.Id);
    }

    [Fact]
    public void ShowingAgainUndoesAndLeavesNothingStored()
    {
        var (document, _, _, _, _) = FilteredProject();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);

        var hide = new SetWallFunctionVisibilityCommand(document, plan, WallFunction.Interior, false);
        hide.Redo();
        Assert.Single(document.ViewSettings.Filtered);

        hide.Undo();
        Assert.Empty(document.ViewSettings.Filtered);
    }

    [Fact]
    public void FiltersAreSavedWithTheProject()
    {
        var (document, _, _, _, _) = FilteredProject();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);

        document.ViewSettings.SetWallFunctionVisible(plan, WallFunction.Interior, false);
        document.ViewSettings.SetWallFunctionVisible(ViewReference.Model3D, WallFunction.Exterior, false);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");

        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);

            Assert.False(reloaded.ViewSettings.IsWallFunctionVisible(plan, WallFunction.Interior));
            Assert.True(reloaded.ViewSettings.IsWallFunctionVisible(plan, WallFunction.Exterior));
            Assert.False(reloaded.ViewSettings.IsWallFunctionVisible(ViewReference.Model3D, WallFunction.Exterior));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
