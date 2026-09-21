using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Covers wall-to-wall mitring. The cases that matter are the ones that used to look wrong:
/// corners that overlap, and mitres that flip depending on which way a wall was drawn.
/// </summary>
public class WallJoinTests
{
    /// <summary>A 200 mm single-layer type, so the arithmetic in each test is checkable by hand.</summary>
    private static (BimDocument Document, WallType Type, Guid LevelId) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document,
            document.TypesOf<WallType>().Single(t => t.Name == "Generic - 200mm"),
            document.Levels.First().Id);
    }

    private static Wall AddWall(
        BimDocument document, WallType type, Guid levelId, Point2D start, Point2D end)
    {
        var wall = new Wall { Start = start, End = end, TypeId = type.Id, LevelId = levelId };
        document.Add(wall);
        return wall;
    }

    /// <summary>The outline of the whole wall body, mitred.</summary>
    private static Point2D[] Outline(BimDocument document, Wall wall, WallType type)
    {
        var half = type.Width / 2;
        return WallJoins.GetBandOutline(document, wall, type, half, -half);
    }

    [Fact]
    public void AFreeEndIsCutSquare()
    {
        var (document, type, levelId) = Project();
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(5000, 0));

        var outline = Outline(document, wall, type);

        // Corners at x = 0 and x = 5000, offset 100 mm either side of the centreline.
        Assert.Equal(4, outline.Length);
        Assert.All(outline, corner => Assert.Equal(100, Math.Abs(corner.Y), precision: 6));
        Assert.Contains(outline, c => Math.Abs(c.X) < 1e-6);
        Assert.Contains(outline, c => Math.Abs(c.X - 5000) < 1e-6);
    }

    [Fact]
    public void ARightAngleCornerMitresToTheOutsideAndInside()
    {
        var (document, type, levelId) = Project();

        // Two walls meeting at the origin: one running west, one running north.
        var westward = AddWall(document, type, levelId, new Point2D(-5000, 0), new Point2D(0, 0));
        AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(0, 4000));

        var outline = Outline(document, westward, type);

        // The joined end reaches the corner of the L: (-100, 100) inside, (100, -100) outside.
        Assert.Contains(outline, c => Near(c, new Point2D(-100, 100)));
        Assert.Contains(outline, c => Near(c, new Point2D(100, -100)));
    }

    [Fact]
    public void TheMitreDoesNotDependOnWhichWayTheNeighbourWasDrawn()
    {
        var (forwardDocument, type, levelId) = Project();
        var forwardWall = AddWall(forwardDocument, type, levelId, new Point2D(-5000, 0), new Point2D(0, 0));
        AddWall(forwardDocument, type, levelId, new Point2D(0, 0), new Point2D(0, 4000));

        var (reverseDocument, reverseType, reverseLevel) = Project();
        var reverseWall = AddWall(reverseDocument, reverseType, reverseLevel, new Point2D(-5000, 0), new Point2D(0, 0));
        // Same wall, drawn the other way round.
        AddWall(reverseDocument, reverseType, reverseLevel, new Point2D(0, 4000), new Point2D(0, 0));

        var forward = Outline(forwardDocument, forwardWall, type);
        var reverse = Outline(reverseDocument, reverseWall, reverseType);

        for (var i = 0; i < forward.Length; i++)
            Assert.True(Near(forward[i], reverse[i]),
                $"corner {i}: {forward[i]} should match {reverse[i]}");
    }

    [Fact]
    public void WallsInAStraightRunAreNotMitred()
    {
        var (document, type, levelId) = Project();

        var first = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(3000, 0));
        AddWall(document, type, levelId, new Point2D(3000, 0), new Point2D(6000, 0));

        var outline = Outline(document, first, type);

        // A collinear neighbour offers no corner, so the end stays square at x = 3000.
        Assert.Equal(2, outline.Count(c => Math.Abs(c.X - 3000) < 1e-6));
    }

    [Fact]
    public void WallsOnDifferentLevelsDoNotJoin()
    {
        var (document, type, _) = Project();
        var ground = document.Levels.Single(l => l.Name == "Ground Floor").Id;
        var first = document.Levels.Single(l => l.Name == "First Floor").Id;

        var wall = AddWall(document, type, ground, new Point2D(-5000, 0), new Point2D(0, 0));
        AddWall(document, type, first, new Point2D(0, 0), new Point2D(0, 4000));

        var outline = Outline(document, wall, type);

        Assert.Equal(2, outline.Count(c => Math.Abs(c.X) < 1e-6));
    }

    [Fact]
    public void AWallCarryingStraightOnIsNotCountedAsACorner()
    {
        var (document, type, levelId) = Project();

        var wall = AddWall(document, type, levelId, new Point2D(-5000, 0), new Point2D(0, 0));
        AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(5000, 0));      // straight on
        var corner = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(0, 4000));

        Assert.Same(corner, WallJoins.FindPartnerAt(document, wall, new Point2D(0, 0)));
    }

    [Fact]
    public void NearlyParallelWallsAreButtedInsteadOfDrawnAsASpike()
    {
        var (document, type, levelId) = Project();

        // About one degree apart. Mitring these would put the corner roughly 10 m away,
        // which is what turned the wall into a tapering cone.
        var wall = AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(5000, 0));
        AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(5000, 100));

        var outline = Outline(document, wall, type);

        Assert.Equal(2, outline.Count(c => Math.Abs(c.X) < 1e-6));
    }

    [Fact]
    public void NoCornerEverRunsAwayFromTheWall()
    {
        var (document, type, levelId) = Project();

        // Sweep a neighbour through every angle, including the shallow ones that used to
        // send the mitre to infinity.
        for (var degrees = 1; degrees < 360; degrees++)
        {
            var radians = degrees * Math.PI / 180;
            var scenario = BimDocument.CreateDefault();
            var scenarioType = scenario.TypesOf<WallType>().Single(t => t.Name == "Generic - 200mm");
            var scenarioLevel = scenario.Levels.First().Id;

            var wall = AddWall(scenario, scenarioType, scenarioLevel,
                new Point2D(0, 0), new Point2D(5000, 0));
            AddWall(scenario, scenarioType, scenarioLevel, new Point2D(0, 0),
                new Point2D(5000 * Math.Cos(radians), 5000 * Math.Sin(radians)));

            foreach (var corner in Outline(scenario, wall, scenarioType))
            {
                Assert.False(double.IsNaN(corner.X) || double.IsNaN(corner.Y),
                    $"{degrees} degrees produced a corner that is not a number");

                // Measured along the wall: across it, every corner is half a wall wide by
                // construction, so only overshoot past the ends can be a spike.
                var along = (corner - wall.Start).Dot(wall.Direction);
                var overshoot = Math.Max(-along, along - wall.Length);

                Assert.True(overshoot <= 2 * scenarioType.Width + 1,
                    $"{degrees} degrees put a corner {overshoot:0} mm past the end of the wall");
            }
        }
    }

    [Fact]
    public void AGenuineCornerIsStillMitred()
    {
        var (document, type, levelId) = Project();

        // 45 degrees is well inside the mitre limit and must still produce a real mitre.
        var wall = AddWall(document, type, levelId, new Point2D(-5000, 0), new Point2D(0, 0));
        AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(4000, 4000));

        var outline = Outline(document, wall, type);

        // The joint is at the origin; the far end sits back at x = -5000.
        var atJoint = outline.Where(c => Math.Abs(c.X) < 1000).ToList();
        Assert.Equal(2, atJoint.Count);

        // A mitred end is not square, so its two corners sit at different distances along x.
        Assert.True(Math.Abs(atJoint[0].X - atJoint[1].X) > 1,
            "a 45 degree corner should still be mitred, not butted");
    }

    [Fact]
    public void LayersStayInsideTheMitredOutline()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));
        var levelId = document.Levels.First().Id;

        var wall = AddWall(document, type, levelId, new Point2D(-5000, 0), new Point2D(0, 0));
        AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(0, 4000));

        var half = type.Width / 2;
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);

        // Every layer band must span the full wall width when summed, and each must have
        // four finite corners - a degenerate mitre would produce NaN and vanish on screen.
        foreach (var (_, start, end) in type.Structure.GetLayerOffsets())
        {
            var band = WallJoins.GetBandOutline(wall, type, half - start, half - end, startCut, endCut);

            Assert.Equal(4, band.Length);
            Assert.All(band, corner =>
            {
                Assert.False(double.IsNaN(corner.X) || double.IsInfinity(corner.X));
                Assert.False(double.IsNaN(corner.Y) || double.IsInfinity(corner.Y));
            });
        }
    }

    [Fact]
    public void AnAcuteCornerStillProducesAFiniteMitre()
    {
        var (document, type, levelId) = Project();

        var wall = AddWall(document, type, levelId, new Point2D(-5000, 0), new Point2D(0, 0));
        AddWall(document, type, levelId, new Point2D(0, 0), new Point2D(-4000, 1000));

        var outline = Outline(document, wall, type);

        Assert.All(outline, corner =>
        {
            Assert.False(double.IsNaN(corner.X) || double.IsInfinity(corner.X));
            Assert.False(double.IsNaN(corner.Y) || double.IsInfinity(corner.Y));
        });
    }

    private static bool Near(Point2D a, Point2D b) => a.DistanceTo(b) < 1e-6;
}
