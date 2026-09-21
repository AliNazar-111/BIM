using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Tests over walls meeting walls (specification section 2.4).
///
/// The corner case - two walls turning - is covered by <c>WallJoinTests</c>. This is about
/// everything else that happens at a junction: a wall running into the side of another, three
/// walls at a point, and the case that caused the most trouble in practice, a T-junction whose
/// through wall has been split in two.
/// </summary>
public class WallJunctionTests
{
    private const double Tolerance = 1e-6;

    private static (BimDocument Document, WallType Type) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document, document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic")));
    }

    private static Wall AddWall(BimDocument document, WallType type, Point2D start, Point2D end)
    {
        var wall = new Wall
        {
            Start = start,
            End = end,
            TypeId = type.Id,
            LevelId = document.Levels[0].Id
        };

        document.Add(wall);
        return wall;
    }

    /// <summary>The two outline corners at one end of a wall.</summary>
    private static (Point2D Outer, Point2D Inner) EndCorners(
        BimDocument document, Wall wall, WallType type, bool atEnd)
    {
        var half = type.Width / 2;
        var outline = WallJoins.GetBandOutline(document, wall, type, half, -half);

        // Outline order: outer at start, outer at end, inner at end, inner at start.
        return atEnd ? (outline[1], outline[2]) : (outline[0], outline[3]);
    }

    /// <summary>Whether an end is cut square: both corners the same distance along the wall.</summary>
    private static bool IsSquare(BimDocument document, Wall wall, WallType type, bool atEnd)
    {
        var (outer, inner) = EndCorners(document, wall, type, atEnd);

        return Math.Abs((outer - inner).Dot(wall.Direction)) < 1e-6;
    }

    // ---- running into the side of another wall -----------------------------------

    [Fact]
    public void AWallRunningIntoAnotherStopsAtItsFace()
    {
        var (document, type) = Project();

        var through = AddWall(document, type, new Point2D(0, 0), new Point2D(10000, 0));
        var stem = AddWall(document, type, new Point2D(5000, 4000), new Point2D(5000, 0));

        var (outer, inner) = EndCorners(document, stem, type, atEnd: true);

        // Cut back to the near face rather than run on to the through wall's centreline,
        // which would push half a wall's width inside it.
        Assert.Equal(type.Width / 2, outer.Y, precision: 6);
        Assert.Equal(type.Width / 2, inner.Y, precision: 6);

        _ = through;
    }

    [Fact]
    public void TheWallBeingRunIntoIsNotAltered()
    {
        var (document, type) = Project();

        var through = AddWall(document, type, new Point2D(0, 0), new Point2D(10000, 0));
        var half = type.Width / 2;

        var alone = WallJoins.GetBandOutline(document, through, type, half, -half);

        AddWall(document, type, new Point2D(5000, 4000), new Point2D(5000, 0));

        // It simply passes through. Altering it as well is what produced a notch in the
        // through wall where a stem met it.
        Assert.Equal(alone, WallJoins.GetBandOutline(document, through, type, half, -half));
    }

    [Fact]
    public void AStemMeetingAWallFromBelowStopsAtTheOtherFace()
    {
        var (document, type) = Project();

        AddWall(document, type, new Point2D(0, 0), new Point2D(10000, 0));
        var stem = AddWall(document, type, new Point2D(5000, -4000), new Point2D(5000, 0));

        var (outer, inner) = EndCorners(document, stem, type, atEnd: true);

        Assert.Equal(-type.Width / 2, outer.Y, precision: 6);
        Assert.Equal(-type.Width / 2, inner.Y, precision: 6);
    }

    // ---- the split T-junction ------------------------------------------------------

    [Fact]
    public void SplittingTheThroughWallLeavesTheJunctionLookingExactlyTheSame()
    {
        var (document, type) = Project();
        var history = new UndoStack();
        var half = type.Width / 2;

        var through = AddWall(document, type, new Point2D(0, 0), new Point2D(10000, 0));
        var stem = AddWall(document, type, new Point2D(5000, 4000), new Point2D(5000, 0));

        var before = WallJoins.GetBandOutline(document, stem, type, half, -half);

        history.Execute(new SplitWallCommand(document, through, new Point2D(5000, 0)));

        // This is the case that caused the trouble: a junction made by splitting a wall has to
        // look the same as one made by drawing two walls, because it is the same building.
        Assert.Equal(before, WallJoins.GetBandOutline(document, stem, type, half, -half));
    }

    [Fact]
    public void BothHalvesOfASplitRunStaySquareAgainstEachOther()
    {
        var (document, type) = Project();
        var history = new UndoStack();

        var through = AddWall(document, type, new Point2D(0, 0), new Point2D(10000, 0));
        AddWall(document, type, new Point2D(5000, 4000), new Point2D(5000, 0));

        var command = new SplitWallCommand(document, through, new Point2D(5000, 0));
        history.Execute(command);

        var second = command.Remainder;

        // Each half mitring with the stem between them is what made the two halves meet as a
        // notch. A run carrying on through a point has no corner to turn.
        Assert.True(IsSquare(document, through, type, atEnd: true), "the first half is not square");
        Assert.True(IsSquare(document, second, type, atEnd: false), "the second half is not square");

        // And they meet flush: the two ends share their corners.
        var first = EndCorners(document, through, type, atEnd: true);
        var next = EndCorners(document, second, type, atEnd: false);

        Assert.Equal(first.Outer.X, next.Outer.X, precision: 6);
        Assert.Equal(first.Inner.X, next.Inner.X, precision: 6);
    }

    [Fact]
    public void UndoingTheSplitLeavesTheJunctionAsItWas()
    {
        var (document, type) = Project();
        var history = new UndoStack();
        var half = type.Width / 2;

        var through = AddWall(document, type, new Point2D(0, 0), new Point2D(10000, 0));
        var stem = AddWall(document, type, new Point2D(5000, 4000), new Point2D(5000, 0));

        var before = WallJoins.GetBandOutline(document, stem, type, half, -half);

        history.Execute(new SplitWallCommand(document, through, new Point2D(5000, 0)));
        history.Undo();

        Assert.Equal(before, WallJoins.GetBandOutline(document, stem, type, half, -half));
    }

    // ---- corners still behave --------------------------------------------------------

    [Fact]
    public void TwoWallsTurningACornerStillMitre()
    {
        var (document, type) = Project();

        var first = AddWall(document, type, new Point2D(0, 0), new Point2D(6000, 0));
        AddWall(document, type, new Point2D(6000, 0), new Point2D(6000, 4000));

        // A mitre is not square: its two corners sit at different distances along the wall.
        Assert.False(IsSquare(document, first, type, atEnd: true));
    }

    [Fact]
    public void AStraightRunOfTwoWallsIsSquareAtTheJoin()
    {
        var (document, type) = Project();

        var first = AddWall(document, type, new Point2D(0, 0), new Point2D(5000, 0));
        var second = AddWall(document, type, new Point2D(5000, 0), new Point2D(10000, 0));

        Assert.True(IsSquare(document, first, type, atEnd: true));
        Assert.True(IsSquare(document, second, type, atEnd: false));
    }

    [Fact]
    public void ThreeWallsMeetingWithNoRunThroughShareTheJunction()
    {
        var (document, type) = Project();

        var joint = new Point2D(0, 0);

        // A Y: three walls at 120 degrees, so no two of them are in line.
        var walls = new List<Wall>();

        for (var i = 0; i < 3; i++)
        {
            var angle = i * 2 * Math.PI / 3;
            var away = new Point2D(Math.Cos(angle) * 4000, Math.Sin(angle) * 4000);
            walls.Add(AddWall(document, type, away, joint));
        }

        // No single mitre is right against two neighbours, so each wall is cut to both of
        // them and they share the middle: the junction is filled once, with no overlap.
        var (most, covered) = OutlineChecks.Coverage(document, walls, joint, type.Width);

        Assert.Equal(1, most);
        Assert.True(covered, "The middle of the junction was left empty.");
        Assert.All(walls, wall => Assert.Equal(WallEndCondition.Shared, WallJoins.GetEndCuts(document, wall, type).End.Condition));
    }

    [Fact]
    public void AFreeEndIsSquare()
    {
        var (document, type) = Project();
        var wall = AddWall(document, type, new Point2D(0, 0), new Point2D(6000, 0));

        Assert.True(IsSquare(document, wall, type, atEnd: true));
        Assert.True(IsSquare(document, wall, type, atEnd: false));
    }

    // ---- refusing a bad butt ---------------------------------------------------------

    [Fact]
    public void AVeryShallowApproachIsCutSquareRatherThanDrawnAsASpike()
    {
        var (document, type) = Project();

        AddWall(document, type, new Point2D(0, 0), new Point2D(20000, 0));

        // About six degrees: butting to the face would give an end nearly ten wall-widths long.
        var stem = AddWall(document, type, new Point2D(0, 1000), new Point2D(9500, 0));

        Assert.True(IsSquare(document, stem, type, atEnd: true),
            "a butt that reaches too far along the wall should give way to a square end");
    }

    [Fact]
    public void AStemMeetingAWallAtAnAngleStillButtsCleanly()
    {
        var (document, type) = Project();

        AddWall(document, type, new Point2D(0, 0), new Point2D(10000, 0));

        // Forty-five degrees: a long but perfectly reasonable butt.
        var stem = AddWall(document, type, new Point2D(2000, 3000), new Point2D(5000, 0));

        var (outer, inner) = EndCorners(document, stem, type, atEnd: true);

        // Both corners land on the through wall's near face, so the end is flush against it.
        Assert.Equal(type.Width / 2, outer.Y, precision: 6);
        Assert.Equal(type.Width / 2, inner.Y, precision: 6);
    }

    // ---- a real plan, from a user's project -------------------------------------------

    /// <summary>
    /// The junctions from a project drawn by hand, rather than ones invented to suit the code:
    /// a room whose two side walls were split, a partition run meeting a thick exterior wall, a
    /// wall ending on the middle of another, and a triangle of walls at acute angles.
    ///
    /// Cases found in a real drawing are worth keeping, because they are the ones that actually
    /// occur, and none of them was what anybody would have thought to write down.
    /// </summary>
    private static BimDocument RealPlan()
    {
        var document = BimDocument.CreateDefault();
        var exterior = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var partition = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Interior"));

        void Add(WallType type, double x1, double y1, double x2, double y2) =>
            AddWall(document, type, new Point2D(x1, y1), new Point2D(x2, y2));

        // A room, with the left side made of two partitions meeting end to end and the right
        // side of two exterior walls doing the same.
        Add(exterior, 0, 0, 6000, 0);
        Add(exterior, 6000, 0, 6000, 2400);
        Add(exterior, 6000, 2400, 0, 2400);
        Add(partition, 0, 4000, 0, 2400);
        Add(partition, 0, 2400, 0, 0);
        Add(exterior, 6000, 2400, 6000, 4000);

        // A wall ending on the middle of another, and a triangle of walls at acute angles.
        Add(exterior, 1200, 5200, 3400, 3600);
        Add(exterior, 3500, 5500, 2300, 4400);
        Add(exterior, 5000, 5700, 6100, 4300);
        Add(exterior, 6100, 4300, 6700, 5700);
        Add(exterior, 6700, 5700, 5000, 5700);
        Add(exterior, 1200, 5200, 0, 4000);
        Add(exterior, 0, 4000, -2900, 3200);
        Add(exterior, -2900, 3200, 0, 0);

        return document;
    }

    [Fact]
    public void EveryWallOfARealPlanDrawsAsAWall()
    {
        var document = RealPlan();

        foreach (var wall in document.Walls)
        {
            var type = document.GetWallType(wall)!;
            var half = type.Width / 2;
            var outline = WallJoins.GetBandOutline(document, wall, type, half, -half);

            var name = $"wall ({wall.Start.X:0},{wall.Start.Y:0})-({wall.End.X:0},{wall.End.Y:0})";

            Assert.True(outline.Length >= 4, $"{name}: {outline.Length} corners");
            Assert.True((outline[1] - outline[0]).Dot(wall.Direction) > 0, $"{name}: outer edge runs backwards");
            Assert.False(OutlineChecks.IsSelfIntersecting(outline), $"{name}: the outline crosses itself");
            Assert.True(Polygon2D.Area(outline) > 1, $"{name}: no area");
        }
    }

    [Fact]
    public void AWallBetweenTwoSplitRunsButtsToBothOfThem()
    {
        var document = RealPlan();

        // The wall across the middle of the room. Both of its ends meet a pair of walls that
        // are in line with each other, so both ends butt rather than mitring with one of them.
        var across = document.Walls.Single(w =>
            w.Start == new Point2D(6000, 2400) && w.End == new Point2D(0, 2400));

        var type = document.GetWallType(across)!;
        var outline = WallJoins.GetBandOutline(document, across, type, type.Width / 2, -type.Width / 2);

        // Stops at the face of the 330 exterior run on one side, and of the 125 partition run
        // on the other - not at either run's centreline.
        Assert.Equal(6000 - 165, outline[0].X, precision: 6);
        Assert.Equal(62.5, outline[1].X, precision: 6);
        Assert.Equal(62.5, outline[2].X, precision: 6);
        Assert.Equal(6000 - 165, outline[3].X, precision: 6);
    }

    [Fact]
    public void AWallEndingOnTheMiddleOfAnotherButtsToItsFace()
    {
        var document = RealPlan();

        var through = document.Walls.Single(w => w.Start == new Point2D(1200, 5200) && w.End.X == 3400);
        var stem = document.Walls.Single(w => w.Start == new Point2D(3500, 5500));

        var type = document.GetWallType(stem)!;
        var throughType = document.GetWallType(through)!;

        var (_, endCut) = WallJoins.GetEndCuts(document, stem, type);
        var outline = WallJoins.GetBandOutline(document, stem, type, type.Width / 2, -type.Width / 2);

        // The cut runs along the through wall, half its width off its centreline.
        var across = through.Direction.PerpendicularLeft();
        var offset = (endCut.Line.Origin - through.Start).Dot(across);

        Assert.Equal(throughType.Width / 2, Math.Abs(offset), precision: 3);
        Assert.True(Math.Abs(endCut.Line.Direction.Cross(through.Direction)) < 1e-9,
            "the cut should run parallel to the wall being met");

        // And the through wall is not altered by being met: it keeps whatever shape its own
        // ends give it, which here includes a mitre at its far end with another wall.
        var withStem = WallJoins.GetBandOutline(document, through, throughType,
            throughType.Width / 2, -throughType.Width / 2);

        document.Remove(stem);

        var withoutStem = WallJoins.GetBandOutline(document, through, throughType,
            throughType.Width / 2, -throughType.Width / 2);

        Assert.Equal(withoutStem, withStem);
        _ = outline;
    }

    // ---- what the rest of the application sees ----------------------------------------

    [Fact]
    public void TheButtIsUsedByTheSectionAndTheThreeDimensionalModelToo()
    {
        var (document, type) = Project();

        AddWall(document, type, new Point2D(0, 0), new Point2D(10000, 0));
        var stem = AddWall(document, type, new Point2D(5000, 4000), new Point2D(5000, 0));

        // The joins are solved once and every view reads the same answer, so a stem that stops
        // at the face in plan stops there in 3D as well.
        var meshes = BIMDesigner.Core.Views.ModelMeshBuilder.Build(document)
            .Where(mesh => mesh.ElementId == stem.Id);

        var bounds = BIMDesigner.Core.Views.ModelMeshBuilder.Bounds(meshes)!.Value;

        // Stops at the through wall's face at one end, and square on its own endpoint at the
        // other, which is free.
        Assert.Equal(type.Width / 2, bounds.Min.Y, precision: 6);
        Assert.Equal(4000, bounds.Max.Y, precision: 6);
    }
}
