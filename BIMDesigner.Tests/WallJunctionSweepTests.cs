using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Sweeps junction geometry looking for outlines that are not walls.
///
/// A cut is computed for each end on its own, so nothing in that calculation can see what the
/// other end did. That leaves room for two reasonable cuts to produce an unreasonable shape
/// between them - a wall turned inside out, folded into a bow tie, or collapsed to a triangle.
/// Those are exactly the shapes that draw as crossing triangles in 3D.
///
/// Rather than wait to be shown one, this tries a few thousand arrangements and checks the
/// shape that comes out. It is the same approach that pinned down the wall spikes.
/// </summary>
public class WallJunctionSweepTests
{
    private static (BimDocument Document, WallType Thin, WallType Thick) Project()
    {
        var document = BimDocument.CreateDefault();

        return (
            document,
            document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Interior")),
            document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    private static Wall Add(BimDocument document, WallType type, Point2D start, Point2D end)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = document.Levels[0].Id };
        document.Add(wall);
        return wall;
    }

    /// <summary>
    /// Whether an outline is a wall: four distinct corners, running the right way round, not
    /// folded over itself, and not stretched miles past the wall it belongs to.
    /// </summary>
    private static string? Fault(Wall wall, WallType type, IReadOnlyList<Point2D> outline)
    {
        if (outline.Count != 4) return $"{outline.Count} corners";

        foreach (var corner in outline)
            if (double.IsNaN(corner.X) || double.IsNaN(corner.Y) ||
                double.IsInfinity(corner.X) || double.IsInfinity(corner.Y))
                return "a corner is not a number";

        var direction = wall.Direction;

        // Outline order is outer at start, outer at end, inner at end, inner at start. Both
        // long edges have to run the same way along the wall, or the shape is folded over.
        if ((outline[1] - outline[0]).Dot(direction) <= 0) return "the outer edge runs backwards";
        if ((outline[2] - outline[3]).Dot(direction) <= 0) return "the inner edge runs backwards";

        if (Crosses(outline[0], outline[1], outline[2], outline[3])) return "the long edges cross";
        if (Crosses(outline[1], outline[2], outline[3], outline[0])) return "the ends cross";

        var area = Polygon2D.Area(outline);
        if (area < 1) return "no area";

        // A cut may reach past the joint, but only as far as the join rules allow.
        var reach = wall.Length + 6 * type.Width;
        foreach (var corner in outline)
        {
            var along = (corner - wall.Start).Dot(direction);
            if (along < -reach || along > wall.Length + reach) return "a corner is far off the end";
        }

        // And the wall cannot silently become a different size.
        if (area > (wall.Length + 4 * type.Width) * type.Width * 2) return "far too much area";

        return null;
    }

    private static bool Crosses(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        double Side(Point2D p, Point2D q, Point2D r) => (q - p).Cross(r - p);

        var d1 = Side(c, d, a);
        var d2 = Side(c, d, b);
        var d3 = Side(a, b, c);
        var d4 = Side(a, b, d);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static void AssertEveryWallIsSane(BimDocument document, string arrangement)
    {
        foreach (var wall in document.Walls)
        {
            var type = document.GetWallType(wall)!;
            var half = type.Width / 2;

            var outline = WallJoins.GetBandOutline(document, wall, type, half, -half);
            var fault = Fault(wall, type, outline);

            Assert.True(fault is null, $"{arrangement}: {fault}.");

            // Every layer is cut by the same two lines, so if the wall is sane its layers are
            // too - but a layer is narrower, so check the thinnest as well.
            foreach (var (layer, start, end) in type.Structure.GetLayerOffsets())
            {
                var band = WallJoins.GetBandOutline(document, wall, type, half - start, half - end);
                var layerFault = Fault(wall, type, band);

                Assert.True(layerFault is null, $"{arrangement}: layer {layer.Function}: {layerFault}.");
            }
        }
    }

    // ---- a stem running into the side of a wall -----------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStemMeetingAWallAtAnyAngleAndLengthDrawsAsAWall(bool splitTheThroughWall)
    {
        var joint = new Point2D(5000, 0);

        for (var degrees = 5; degrees <= 175; degrees += 5)
        {
            foreach (var length in new double[] { 150, 300, 600, 1200, 4000 })
            foreach (var stemIsThick in new[] { false, true })
            foreach (var fromAbove in new[] { false, true })
            {
                var (document, thin, thick) = Project();
                var through = Add(document, thick, new Point2D(0, 0), new Point2D(10000, 0));

                var radians = degrees * Math.PI / 180 * (fromAbove ? 1 : -1);
                var away = new Point2D(
                    joint.X + Math.Cos(radians) * length,
                    joint.Y + Math.Sin(radians) * length);

                Add(document, stemIsThick ? thick : thin, away, joint);

                if (splitTheThroughWall)
                    new SplitWallCommand(document, through, joint).Redo();

                AssertEveryWallIsSane(document,
                    $"stem at {degrees}deg, {length}mm, thick={stemIsThick}, above={fromAbove}, split={splitTheThroughWall}");
            }
        }
    }

    // ---- a stem with something at its far end too ---------------------------------

    [Fact]
    public void AStemThatButtsAtOneEndAndTurnsACornerAtTheOtherDrawsAsAWall()
    {
        // The two ends are solved independently, so a short stem cut back at one end and
        // mitred forward at the other is where they are most likely to meet in the middle.
        for (var degrees = 10; degrees <= 170; degrees += 10)
        {
            foreach (var length in new double[] { 200, 350, 500, 900 })
            foreach (var cornerDegrees in new[] { 30, 60, 90, 120, 150 })
            {
                var (document, thin, thick) = Project();

                var joint = new Point2D(5000, 0);
                Add(document, thick, new Point2D(0, 0), new Point2D(10000, 0));

                var radians = degrees * Math.PI / 180;
                var far = new Point2D(
                    joint.X + Math.Cos(radians) * length,
                    joint.Y + Math.Sin(radians) * length);

                var stem = Add(document, thin, far, joint);

                var cornerRadians = (degrees + cornerDegrees) * Math.PI / 180;
                Add(document, thin, far, new Point2D(
                    far.X + Math.Cos(cornerRadians) * 3000,
                    far.Y + Math.Sin(cornerRadians) * 3000));

                AssertEveryWallIsSane(document,
                    $"stem {degrees}deg {length}mm with a {cornerDegrees}deg corner at its far end");

                _ = stem;
            }
        }
    }

    // ---- several walls at one point ------------------------------------------------

    [Fact]
    public void AnyNumberOfWallsMeetingAtOnePointDrawAsWalls()
    {
        for (var count = 2; count <= 6; count++)
        {
            foreach (var twist in new[] { 0, 7, 23 })
            {
                var (document, thin, thick) = Project();
                var joint = new Point2D(0, 0);

                for (var i = 0; i < count; i++)
                {
                    var radians = (i * 360.0 / count + twist) * Math.PI / 180;
                    var away = new Point2D(Math.Cos(radians) * 3000, Math.Sin(radians) * 3000);

                    Add(document, i % 2 == 0 ? thin : thick, joint, away);
                }

                AssertEveryWallIsSane(document, $"{count} walls at a point, twisted {twist}deg");
            }
        }
    }

    // ---- walls cut into pieces by their openings -----------------------------------

    /// <summary>
    /// Checks the stretches a wall is actually drawn as, rather than the wall as a whole.
    ///
    /// A wall with a door in it is drawn as the pieces either side of the doorway, each cut by
    /// the join at one end and the jamb at the other. A piece beside a door, at an end that has
    /// been shortened by a butt joint, is where two independently sensible cuts are most likely
    /// to have crossed.
    /// </summary>
    private static void AssertEverySliceIsSane(BimDocument document, string arrangement)
    {
        foreach (var wall in document.Walls)
        {
            var type = document.GetWallType(wall)!;
            var half = type.Width / 2;

            foreach (var slice in WallSlices.Solid(document, wall, type))
            {
                var outline = WallJoins.GetBandOutline(
                    wall, type, half, -half, slice.CutFrom, slice.CutTo);

                var fault = Fault(wall, type, outline);
                Assert.True(fault is null, $"{arrangement}: the piece from {slice.From:0} to {slice.To:0}: {fault}.");
            }
        }
    }

    [Fact]
    public void AWallWithADoorBesideAJunctionDrawsAsWalls()
    {
        var joint = new Point2D(5000, 0);

        for (var degrees = 15; degrees <= 165; degrees += 15)
        {
            // The door creeps up to the butted end, so at some point the piece beside it is
            // shorter than the amount the join takes off.
            foreach (var doorCentre in new double[] { 460, 500, 600, 800, 1500, 3000 })
            foreach (var stemIsThick in new[] { false, true })
            {
                var (document, thin, thick) = Project();
                Add(document, thick, new Point2D(0, 0), new Point2D(10000, 0));

                var radians = degrees * Math.PI / 180;
                var stem = Add(document, stemIsThick ? thick : thin,
                    joint,
                    new Point2D(joint.X + Math.Cos(radians) * 5000, joint.Y + Math.Sin(radians) * 5000));

                document.Add(new Door
                {
                    TypeId = document.TypesOf<DoorType>().First(t => t.Width == 900).Id,
                    LevelId = stem.LevelId,
                    HostWallId = stem.Id,
                    DistanceAlongWall = doorCentre
                });

                AssertEverySliceIsSane(document,
                    $"door at {doorCentre}mm in a stem at {degrees}deg, thick={stemIsThick}");
            }
        }
    }

    [Fact]
    public void AWallWithDoorsAtBothJunctionsDrawsAsWalls()
    {
        foreach (var length in new double[] { 2500, 4000 })
        foreach (var inset in new double[] { 470, 520, 700 })
        {
            var (document, thin, thick) = Project();

            // A wall butting into another at each end, with a door close to each.
            Add(document, thick, new Point2D(0, 0), new Point2D(10000, 0));
            Add(document, thick, new Point2D(0, length), new Point2D(10000, length));

            var stem = Add(document, thin, new Point2D(5000, 0), new Point2D(5000, length));

            foreach (var centre in new[] { inset, length - inset })
            {
                document.Add(new Door
                {
                    TypeId = document.TypesOf<DoorType>().First(t => t.Width == 900).Id,
                    LevelId = stem.LevelId,
                    HostWallId = stem.Id,
                    DistanceAlongWall = centre
                });
            }

            AssertEverySliceIsSane(document, $"a {length:0}mm stem butting at both ends, doors {inset:0}mm in");
        }
    }

    // ---- a wall crossing another without touching its ends -------------------------

    [Fact]
    public void AWallEndingInsideAnotherNearItsEndDrawsAsAWall()
    {
        // Close to the end of the through wall, where the butt cut and the through wall's own
        // end treatment are both in play.
        foreach (var distanceFromEnd in new double[] { 1, 50, 200, 1000 })
        foreach (var degrees in new[] { 15, 45, 90, 135, 165 })
        {
            var (document, thin, thick) = Project();

            Add(document, thick, new Point2D(0, 0), new Point2D(10000, 0));

            var joint = new Point2D(10000 - distanceFromEnd, 0);
            var radians = degrees * Math.PI / 180;

            Add(document, thin, joint, new Point2D(
                joint.X + Math.Cos(radians) * 2500,
                joint.Y + Math.Sin(radians) * 2500));

            AssertEveryWallIsSane(document, $"stem {distanceFromEnd}mm from the end at {degrees}deg");
        }
    }
}
