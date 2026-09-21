using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Reproduces the two layouts that were reported drawing walls as tapering spikes, using
/// the same 330 mm exterior type as the default template rather than a thin test wall.
/// </summary>
public class WallSpikeRegressionTests
{
    private const double MiterLimitMultiple = 2.0;
    private const double MiterLengthFraction = 0.5;

    private static (BimDocument Document, WallType Type, Guid LevelId) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document,
            document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block")),
            document.Levels.First().Id);
    }

    private static Wall AddWall(
        BimDocument document, WallType type, Guid levelId, Point2D start, Point2D end)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = levelId };
        document.Add(wall);
        return wall;
    }

    /// <summary>
    /// Every corner of the drawn outline must stay near one of the wall's own ends. A spike
    /// is exactly a corner that has escaped, so this is the invariant that was broken.
    /// </summary>
    private static void AssertNoSpike(BimDocument document, Wall wall, WallType type, string what)
    {
        var half = type.Width / 2;
        var outline = WallJoins.GetBandOutline(document, wall, type, half, -half);
        var budget = Math.Min(
            MiterLimitMultiple * type.Width,
            MiterLengthFraction * wall.Length) + 1;

        // Measured along the wall. Distance straight to the endpoint would always include
        // half the wall's width, so a wide wall would fail before it had any spike at all.
        foreach (var corner in outline)
        {
            var along = (corner - wall.Start).Dot(wall.Direction);
            var overshoot = Math.Max(-along, along - wall.Length);

            Assert.True(overshoot <= budget,
                $"{what}: corner {corner} reaches {overshoot:0} mm past the end of the wall " +
                $"(budget {budget:0} mm). The wall is being drawn as a spike.");
        }
    }

    /// <summary>
    /// Sweeps a neighbour through every angle at several wall lengths. Between them these
    /// cover the shallow corners that ran away and the short walls a long mitre would eat.
    /// </summary>
    [Fact]
    public void NoAngleOrLengthProducesASpike()
    {
        foreach (var length in new double[] { 400, 1000, 3790, 12000 })
        {
            for (var degrees = 1; degrees < 360; degrees++)
            {
                var radians = degrees * Math.PI / 180;
                var (document, type, levelId) = Project();

                var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(length, 0));
                AddWall(document, type, levelId, new Point2D(0, 0),
                    new Point2D(length * Math.Cos(radians), length * Math.Sin(radians)));

                AssertNoSpike(document, wall, type, $"{length:0} mm wall at {degrees} degrees");
            }
        }
    }

    [Fact]
    public void ARightAngleCornerIsStillMitred()
    {
        var (document, type, levelId) = Project();

        var wall = AddWall(document, type, levelId, new Point2D(-5000, 0), new Point2D(0, 0));
        AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(0, 4000));

        var half = type.Width / 2;
        var outline = WallJoins.GetBandOutline(document, wall, type, half, -half);

        // The mitre reaches exactly half the wall width past the joint at 90 degrees.
        Assert.Contains(outline, c => c.DistanceTo(new Point2D(-half, half)) < 1e-6);
        Assert.Contains(outline, c => c.DistanceTo(new Point2D(half, -half)) < 1e-6);
    }

    [Fact]
    public void ThreeWallsIncludingADiagonalAtOnePoint()
    {
        var (document, type, levelId) = Project();

        var vertical = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(0, 4000));
        var horizontal = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(6000, 0));
        var diagonal = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(6000, 4000));

        AssertNoSpike(document, vertical, type, "vertical");
        AssertNoSpike(document, horizontal, type, "horizontal");
        AssertNoSpike(document, diagonal, type, "diagonal");
    }

    [Fact]
    public void AWallDraggedAlmostParallelToItsNeighbour()
    {
        var (document, type, levelId) = Project();

        // The bottom wall of the starting room, its right-hand wall, and a second wall
        // dragged until it is nearly on top of that right-hand wall.
        var bottom = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(6000, 0));
        var right = AddWall(document, type, levelId, new Point2D(6000, 0), new Point2D(6000, 4000));
        var dragged = AddWall(document, type, levelId, new Point2D(6000, 0), new Point2D(6100, 3790));

        AssertNoSpike(document, bottom, type, "bottom");
        AssertNoSpike(document, right, type, "right");
        AssertNoSpike(document, dragged, type, "dragged");
    }

    [Fact]
    public void TwoWallsLeavingOnePointInAlmostTheSameDirection()
    {
        var (document, type, levelId) = Project();

        // No third wall: just two walls that share an end and set off the same way. This is
        // an overlap, not a corner, and there is no mitre that means anything.
        var first = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(0, 4000));
        var second = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(100, 3790));

        AssertNoSpike(document, first, type, "first");
        AssertNoSpike(document, second, type, "second");
    }
}

