using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// Tests over moving, mirroring and copying (specification section 2.4).
///
/// Most of these are about reach rather than arithmetic. Copying a wall is easy; copying a
/// wall so that its doors come with it, its tag labels the copy rather than the original, and
/// its mark is not duplicated, is where the work is.
/// </summary>
public class EditingTests
{
    private const double Tolerance = 1e-6;

    private static BimDocument Project() => BimDocument.CreateDefault();

    private static Wall AddWall(BimDocument document, Point2D start, Point2D end)
    {
        var wall = new Wall
        {
            Start = start,
            End = end,
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = document.Levels.First().Id,
            Mark = "W-01"
        };

        document.Add(wall);
        return wall;
    }

    private static Door AddDoor(BimDocument document, Wall wall, double distance)
    {
        var door = new Door
        {
            TypeId = document.TypesOf<DoorType>().First(type => type.Width == 900).Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = distance
        };

        document.Add(door);
        return door;
    }

    // ---- moving -----------------------------------------------------------------

    [Fact]
    public void MovingASelectionMovesEverythingInItByTheSameAmount()
    {
        var document = Project();
        var history = new UndoStack();

        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));
        var grid = new Grid { Start = new Point2D(0, -1000), End = new Point2D(0, 5000), Name = "A" };
        document.Add(grid);

        history.Execute(new MoveElementsCommand(new Element[] { wall, grid }, new Vector2D(1000, 500)));

        Assert.Equal(new Point2D(1000, 500), wall.Start);
        Assert.Equal(new Point2D(7000, 500), wall.End);
        Assert.Equal(new Point2D(1000, -500), grid.Start);

        history.Undo();

        Assert.Equal(new Point2D(0, 0), wall.Start);
        Assert.Equal(new Point2D(0, -1000), grid.Start);
    }

    [Fact]
    public void ADoorTravelsWithItsWallAndIsNotMovedTwice()
    {
        var document = Project();
        var history = new UndoStack();

        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));
        var door = AddDoor(document, wall, 3000);

        // Selecting both and moving must not slide the door along the wall as well.
        history.Execute(new MoveElementsCommand(new Element[] { wall, door }, new Vector2D(2000, 0)));

        Assert.Equal(3000, door.DistanceAlongWall, Tolerance);
        Assert.Equal(new Point2D(5000, 0), door.GetCentre(wall));
    }

    [Fact]
    public void MovingASlabMovesItsWholeOutline()
    {
        var document = Project();
        var history = new UndoStack();

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = document.Levels[0].Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        var area = floor.Area;
        history.Execute(new MoveElementsCommand(new Element[] { floor }, new Vector2D(1000, 1000)));

        Assert.Equal(area, floor.Area, precision: 3);
        Assert.Equal(new Point2D(4000, 3000), floor.Centroid);
    }

    [Fact]
    public void ADimensionAttachedToAWallIsNotMovedAlongsideIt()
    {
        var document = Project();
        var history = new UndoStack();

        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));

        var dimension = new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            LevelId = wall.LevelId
        };
        document.Add(dimension);

        history.Execute(new MoveElementsCommand(new Element[] { wall, dimension }, new Vector2D(1000, 0)));

        // It follows the wall because it references it. Moving it as well would double the
        // shift and the dimension would drift away from what it measures.
        Assert.Equal(new Point2D(1000, 0), dimension.StartPoint(document));
        Assert.Equal(6000, dimension.Measure(document), Tolerance);
    }

    [Fact]
    public void ASheetIsNotSomethingThatCanBeMovedAroundThePlan()
    {
        var sheet = new BIMDesigner.Core.Sheets.Sheet();

        Assert.False(ElementTransforms.CanMove(sheet));

        var command = new MoveElementsCommand(new Element[] { sheet }, new Vector2D(1000, 0));
        Assert.True(command.IsEmpty);
    }

    // ---- mirroring --------------------------------------------------------------

    [Fact]
    public void MirroringAWallReflectsItAndKeepsItsExteriorOnTheSameSide()
    {
        var document = Project();
        var history = new UndoStack();

        // A wall running east, three metres north of the mirror line.
        var wall = AddWall(document, new Point2D(0, 3000), new Point2D(6000, 3000));
        var exteriorBefore = wall.ExteriorNormal;

        // Mirror about the X axis.
        var axis = new Line2D(new Point2D(0, 0), Vector2D.UnitX);
        history.Execute(new MirrorElementsCommand(new Element[] { wall }, axis));

        Assert.Equal(new Point2D(0, -3000), wall.Start);
        Assert.Equal(new Point2D(6000, -3000), wall.End);

        // The exterior face must end up on the reflected side, not the same side it was on.
        Assert.Equal(-exteriorBefore.Y, wall.ExteriorNormal.Y, Tolerance);
    }

    [Fact]
    public void MirroringTwicePutsEverythingBack()
    {
        var document = Project();
        var history = new UndoStack();

        var wall = AddWall(document, new Point2D(1000, 3000), new Point2D(6000, 4500));
        var start = wall.Start;
        var end = wall.End;
        var flipped = wall.Flipped;

        var axis = Line2D.Through(new Point2D(0, 0), new Point2D(1000, 700));

        history.Execute(new MirrorElementsCommand(new Element[] { wall }, axis));
        Assert.NotEqual(start, wall.Start);

        // A reflection is its own inverse, which is also how the command undoes itself.
        history.Execute(new MirrorElementsCommand(new Element[] { wall }, axis));

        Assert.Equal(start.X, wall.Start.X, precision: 6);
        Assert.Equal(start.Y, wall.Start.Y, precision: 6);
        Assert.Equal(end.X, wall.End.X, precision: 6);
        Assert.Equal(flipped, wall.Flipped);
    }

    [Fact]
    public void UndoingAMirrorIsTheMirrorAgain()
    {
        var document = Project();
        var history = new UndoStack();

        var wall = AddWall(document, new Point2D(0, 3000), new Point2D(6000, 3000));
        var axis = new Line2D(new Point2D(0, 0), Vector2D.UnitX);

        history.Execute(new MirrorElementsCommand(new Element[] { wall }, axis));
        history.Undo();

        Assert.Equal(new Point2D(0, 3000), wall.Start);
        Assert.False(wall.Flipped);
    }

    [Fact]
    public void AMirroredSlabKeepsItsAreaAndItsWinding()
    {
        var document = Project();

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = document.Levels[0].Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, 1000), new Point2D(6000, 1000), new Point2D(6000, 5000), new Point2D(0, 5000)
        });
        document.Add(floor);

        var area = floor.Area;
        ElementTransforms.Mirror(floor, new Line2D(new Point2D(0, 0), Vector2D.UnitX));

        Assert.Equal(area, floor.Area, precision: 3);

        // A reflected outline winds the other way; a slab that kept it would report a
        // negative area everywhere downstream.
        Assert.True(Polygon2D.SignedArea(floor.Boundary) > 0);
    }

    [Fact]
    public void AMirroredSectionGoesOnLookingAtTheSameHalfOfTheBuilding()
    {
        var document = Project();

        var marker = new SectionMarker
        {
            Start = new Point2D(0, 3000),
            End = new Point2D(6000, 3000),
            LevelId = document.Levels[0].Id
        };
        document.Add(marker);

        var lookedNorth = marker.LookDirection.Y > 0;

        ElementTransforms.Mirror(marker, new Line2D(new Point2D(0, 0), Vector2D.UnitX));

        // Reflected across the axis, it should now look the other way in world terms - which
        // is the same way relative to the building it was reflected with.
        Assert.Equal(!lookedNorth, marker.LookDirection.Y > 0);
    }

    // ---- copying ----------------------------------------------------------------

    [Fact]
    public void ACopyIsANewElementRatherThanTheSameOneTwice()
    {
        var document = Project();
        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));

        var copy = Assert.Single(ElementCopy.Duplicate(document, new Element[] { wall }));

        Assert.NotEqual(wall.Id, copy.Id);
        Assert.IsType<Wall>(copy);
        Assert.Equal(wall.TypeId, copy.TypeId);
        Assert.Equal(wall.LevelId, copy.LevelId);
        Assert.Equal(wall.Start, ((Wall)copy).Start);
    }

    [Fact]
    public void ACopiedWallBringsItsDoorsRehostedOntoTheCopy()
    {
        var document = Project();
        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));
        AddDoor(document, wall, 2000);
        AddDoor(document, wall, 4000);

        var copies = ElementCopy.Duplicate(document, new Element[] { wall });

        var copiedWall = copies.OfType<Wall>().Single();
        var copiedDoors = copies.OfType<Door>().ToList();

        Assert.Equal(2, copiedDoors.Count);

        // A door still pointing at the original would appear in the wrong wall.
        Assert.All(copiedDoors, door => Assert.Equal(copiedWall.Id, door.HostWallId));
        Assert.Equal(new[] { 2000d, 4000 }, copiedDoors.Select(d => d.DistanceAlongWall).OrderBy(d => d));
    }

    [Fact]
    public void AMarkIsNotCarriedOntoACopy()
    {
        var document = Project();
        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));

        var copy = ElementCopy.Duplicate(document, new Element[] { wall }).Single();

        // Two walls both marked W-01 is a defect that reaches the site through a schedule.
        Assert.Equal("W-01", wall.Mark);
        Assert.Equal(string.Empty, copy.Mark);
    }

    [Fact]
    public void ATagCopiedWithItsWallLabelsTheCopy()
    {
        var document = Project();
        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));

        var tag = new Tag
        {
            TargetId = wall.Id,
            Field = "Length",
            Position = new Point2D(3000, 800),
            LevelId = wall.LevelId
        };
        document.Add(tag);

        var copies = ElementCopy.Duplicate(document, new Element[] { wall, tag });

        var copiedWall = copies.OfType<Wall>().Single();
        var copiedTag = copies.OfType<Tag>().Single();

        Assert.Equal(copiedWall.Id, copiedTag.TargetId);
    }

    [Fact]
    public void ATagCopiedOnItsOwnStillLabelsTheOriginal()
    {
        var document = Project();
        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));

        var tag = new Tag { TargetId = wall.Id, Field = "Length", LevelId = wall.LevelId };
        document.Add(tag);

        var copiedTag = ElementCopy.Duplicate(document, new Element[] { tag }).OfType<Tag>().Single();

        // Nothing in this copy replaced the wall, so the reference points where it did.
        Assert.Equal(wall.Id, copiedTag.TargetId);
    }

    [Fact]
    public void ADimensionCopiedWithItsWallsMeasuresTheCopies()
    {
        var document = Project();
        var history = new UndoStack();

        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));

        var dimension = new Dimension
        {
            Start = DimensionReference.ToWall(wall, atStart: true),
            End = DimensionReference.ToWall(wall, atStart: false),
            LevelId = wall.LevelId
        };
        document.Add(dimension);

        var copies = ElementCopy.Duplicate(document, new Element[] { wall, dimension });
        history.Execute(new AddElementsCommand(document, copies, "Paste"));
        history.Execute(new MoveElementsCommand(copies, new Vector2D(0, 5000)));

        var copiedDimension = copies.OfType<Dimension>().Single();

        // It measures the copy, which has moved, not the original left behind.
        Assert.Equal(new Point2D(0, 5000), copiedDimension.StartPoint(document));
        Assert.Equal(6000, copiedDimension.Measure(document), Tolerance);
    }

    [Fact]
    public void CopyingAWallWithoutPickingItsDoorsStillBringsThem()
    {
        var document = Project();
        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));
        AddDoor(document, wall, 3000);

        // A wall without its doors is not a copy of that wall.
        var copies = ElementCopy.Duplicate(document, new Element[] { wall });

        Assert.Single(copies.OfType<Door>());
    }

    [Fact]
    public void ASheetIsNeverCopiedAsPartOfASelection()
    {
        var document = Project();
        var sheet = new BIMDesigner.Core.Sheets.Sheet();
        document.Add(sheet);

        Assert.Empty(ElementCopy.Duplicate(document, new Element[] { sheet }));
    }

    // ---- as a whole operation ---------------------------------------------------

    [Fact]
    public void PastingManyElementsIsOneUndoStep()
    {
        var document = Project();
        var history = new UndoStack();

        var walls = new List<Element>();
        for (var i = 0; i < 4; i++)
            walls.Add(AddWall(document, new Point2D(0, i * 1000), new Point2D(6000, i * 1000)));

        var before = document.Elements.Count;
        var copies = ElementCopy.Duplicate(document, walls);

        history.Execute(new AddElementsCommand(document, copies, "Paste"));
        Assert.Equal(before + 4, document.Elements.Count);

        // Undoing a paste one wall at a time would be unusable.
        history.Undo();
        Assert.Equal(before, document.Elements.Count);
    }

    [Fact]
    public void DeletingASelectionTakesHostedElementsAndRestoresTheirOrder()
    {
        var document = Project();
        var history = new UndoStack();

        var first = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));
        var door = AddDoor(document, first, 3000);
        var second = AddWall(document, new Point2D(0, 4000), new Point2D(6000, 4000));
        var third = AddWall(document, new Point2D(0, 8000), new Point2D(6000, 8000));

        var order = document.Elements.ToList();

        history.Execute(new DeleteElementsCommand(document, new Element[] { first, second }));

        Assert.DoesNotContain(door, document.Elements);
        Assert.Contains(third, document.Elements);

        history.Undo();

        Assert.Equal(order, document.Elements);
    }

    [Fact]
    public void AnArrayIsCopiesSteppedAlongADirection()
    {
        var document = Project();
        var history = new UndoStack();

        var wall = AddWall(document, new Point2D(0, 0), new Point2D(4000, 0));
        var step = new Vector2D(0, 3000);

        var placed = new List<Element>();

        for (var i = 1; i < 4; i++)
        {
            var copies = ElementCopy.Duplicate(document, new Element[] { wall });
            foreach (var copy in copies) ElementTransforms.Move(copy, step * i);
            placed.AddRange(copies);
        }

        history.Execute(new AddElementsCommand(document, placed, "Array"));

        var walls = document.Walls.OrderBy(w => w.Start.Y).ToList();

        Assert.Equal(4, walls.Count);
        Assert.Equal(new[] { 0d, 3000, 6000, 9000 }, walls.Select(w => w.Start.Y));

        // The whole array is one thing to undo, because it was one thing to do.
        history.Undo();
        Assert.Single(document.Walls);
    }

    [Fact]
    public void OffsettingAWallGivesAParallelOneAtTheRightDistance()
    {
        var document = Project();

        var wall = AddWall(document, new Point2D(0, 0), new Point2D(6000, 0));

        // An offset is a copy moved along the wall's own perpendicular.
        var copy = (Wall)ElementCopy.Duplicate(document, new Element[] { wall }).Single();
        ElementTransforms.Move(copy, wall.Direction.PerpendicularLeft() * 2500);

        Assert.Equal(new Point2D(0, 2500), copy.Start);
        Assert.Equal(new Point2D(6000, 2500), copy.End);
        Assert.Equal(wall.Length, copy.Length, Tolerance);
    }
}
