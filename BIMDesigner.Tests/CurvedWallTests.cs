using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Arc walls: the curve itself, and everything that draws, joins and edits walls.</summary>
public class CurvedWallTests
{
    private static (BimDocument Document, WallType Partition, WallType Exterior) Project()
    {
        var document = BimDocument.CreateDefault();
        return (
            document,
            document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Interior")),
            document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    private static Wall Add(BimDocument document, WallType type, Point2D start, Point2D end, double bulge = 0)
    {
        var wall = new Wall
        {
            Start = start, End = end, Bulge = bulge, TypeId = type.Id, LevelId = document.Levels[0].Id
        };
        document.Add(wall);
        return wall;
    }

    /// <summary>A half circle of radius 3000 about the origin, from (-3000, 0) round the bottom to (3000, 0).</summary>
    private static Wall HalfCircle(BimDocument document, WallType type) =>
        Add(document, type, new Point2D(-3000, 0), new Point2D(3000, 0), bulge: 1);

    // ---- the curve --------------------------------------------------------------

    [Fact]
    public void AHalfCircleIsWhereItShouldBe()
    {
        var curve = WallCurve.Of(new Point2D(-3000, 0), new Point2D(3000, 0), 1);

        Assert.Equal(3000, curve.Radius, precision: 6);
        Assert.Equal(Math.PI * 3000, curve.Length, precision: 6);
        Assert.True(curve.Centre.DistanceTo(new Point2D(0, 0)) < 1e-6);

        // Anticlockwise from the left end runs round the bottom.
        Assert.True(curve.PointAt(curve.Length / 2).DistanceTo(new Point2D(0, -3000)) < 1e-6);
    }

    [Fact]
    public void TheBulgeThroughAPointGivesAnArcThroughIt()
    {
        var start = new Point2D(0, 0);
        var end = new Point2D(6000, 1000);

        foreach (var through in new[] { new Point2D(3000, 2500), new Point2D(2000, -900), new Point2D(5500, 3500) })
        {
            var curve = WallCurve.Of(start, end, WallCurve.BulgeThrough(start, end, through));
            Assert.True(curve.DistanceTo(through) < 1e-6, $"The arc misses {through}.");
        }
    }

    [Fact]
    public void LocatingAPointUndoesPlacingIt()
    {
        var curve = WallCurve.Of(new Point2D(0, 0), new Point2D(5000, 2000), -0.4);

        foreach (var along in new[] { 0.0, 700, curve.Length / 2, curve.Length })
        foreach (var left in new[] { -165.0, 0, 62.5 })
        {
            var (a, l) = curve.Locate(curve.At(along, left));
            Assert.Equal(along, a, precision: 6);
            Assert.Equal(left, l, precision: 6);
        }
    }

    [Fact]
    public void ThePartsOfAnArcMakeTheWhole()
    {
        var curve = WallCurve.Of(new Point2D(0, 0), new Point2D(5000, 0), 0.6);
        var split = curve.Length * 0.3;

        var first = curve.Part(0, split);
        var second = curve.Part(split, curve.Length);

        Assert.Equal(curve.Length, first.Length + second.Length, precision: 6);
        Assert.True(first.Centre.DistanceTo(curve.Centre) < 1e-6);
        Assert.True(second.Centre.DistanceTo(curve.Centre) < 1e-6);
    }

    // ---- drawing ----------------------------------------------------------------

    [Fact]
    public void ACurvedWallIsABandOfTheRightSize()
    {
        var (document, _, exterior) = Project();
        var wall = HalfCircle(document, exterior);
        var half = exterior.Width / 2;

        var outline = WallJoins.GetBandOutline(document, wall, exterior, half, -half);

        Assert.False(OutlineChecks.IsSelfIntersecting(outline));
        Assert.All(outline, point =>
        {
            var radius = point.DistanceTo(new Point2D(0, 0));
            Assert.True(Math.Abs(radius - 3000) <= half + 1e-6, $"{point} is off the wall.");
        });

        var expected = Math.PI * 3000 * exterior.Width;
        Assert.Equal(expected, Polygon2D.Area(outline), expected * 0.001);
        Assert.Equal(Math.PI * 3000, wall.Length, precision: 6);
    }

    [Fact]
    public void ACurvedWallCarriesOnSmoothlyIntoStraightOnes()
    {
        var (document, _, exterior) = Project();
        var arc = HalfCircle(document, exterior);

        // Straight walls leaving each end along the tangent: the run continues.
        var left = Add(document, exterior, new Point2D(-3000, 3000), new Point2D(-3000, 0));
        var right = Add(document, exterior, new Point2D(3000, 0), new Point2D(3000, 3000));

        var (start, end) = WallJoins.GetEndCuts(document, arc, exterior);
        Assert.Equal(WallEndCondition.Continues, start.Condition);
        Assert.Equal(WallEndCondition.Continues, end.Condition);

        foreach (var joint in new[] { arc.Start, arc.End })
        {
            var (most, covered) = OutlineChecks.Coverage(document, document.Walls, joint, exterior.Width);
            Assert.Equal(1, most);
            Assert.True(covered);
        }

        _ = (left, right);
    }

    [Fact]
    public void ACurvedWallMitresWithAStraightOneAtACorner()
    {
        var (document, _, exterior) = Project();
        var arc = HalfCircle(document, exterior);
        Add(document, exterior, new Point2D(3000, 0), new Point2D(0, 0));

        Assert.Equal(WallEndCondition.Mitre, WallJoins.GetEndCuts(document, arc, exterior).End.Condition);

        var (most, covered) = OutlineChecks.Coverage(document, document.Walls, arc.End, exterior.Width);
        Assert.Equal(1, most);
        Assert.True(covered);
    }

    [Fact]
    public void AWallCanStopAgainstTheSideOfACurvedOne()
    {
        var (document, partition, exterior) = Project();
        var arc = HalfCircle(document, exterior);

        // A partition from the centre out to the bottom of the arc.
        var stem = Add(document, partition, new Point2D(0, 0), new Point2D(0, -3000));

        Assert.Equal(WallEndCondition.Butt, WallJoins.GetEndCuts(document, stem, partition).End.Condition);

        var gap = Assert.Single(WallJoins.FaceGaps(document, arc, exterior));
        Assert.Equal(arc.Length / 2, (gap.From + gap.To) / 2, 5);
    }

    [Fact]
    public void ADoorInACurvedWallCutsItRadially()
    {
        var (document, _, exterior) = Project();
        var wall = HalfCircle(document, exterior);

        document.Add(new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = wall.Length / 2
        });

        var slices = WallSlices.Solid(document, wall, exterior);
        Assert.Equal(2, slices.Count);

        // The jamb is a line through the centre of the circle.
        var jamb = slices[0].CutTo.Line;
        Assert.True(jamb.ClosestPointTo(new Point2D(0, 0)).DistanceTo(new Point2D(0, 0)) < 1e-6);

        foreach (var slice in slices)
        {
            var outline = WallJoins.GetBandOutline(wall, exterior, exterior.Width / 2, -exterior.Width / 2, slice.CutFrom, slice.CutTo);
            Assert.False(OutlineChecks.IsSelfIntersecting(outline));
        }
    }

    [Fact]
    public void ACurvedWallIsBuiltIn3D()
    {
        var (document, _, exterior) = Project();
        var wall = HalfCircle(document, exterior);

        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == wall.Id).ToList();

        Assert.Equal(exterior.Structure.Layers.Count, meshes.Count);
        Assert.All(meshes, mesh => Assert.False(mesh.IsEmpty));
    }

    // ---- editing ------------------------------------------------------------------

    [Fact]
    public void SplittingACurvedWallGivesTwoArcsOfTheSameCircle()
    {
        var (document, _, exterior) = Project();
        var wall = HalfCircle(document, exterior);

        var door = new Door
        {
            TypeId = document.TypesOf<DoorType>().First().Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = wall.Length * 0.75
        };
        document.Add(door);
        var doorAt = door.GetCentre(wall);

        var split = new SplitWallCommand(document, wall, new Point2D(0, -2990));
        split.Redo();

        Assert.True(wall.End.DistanceTo(new Point2D(0, -3000)) < 1e-6);
        Assert.True(wall.LocationCurve.Centre.DistanceTo(new Point2D(0, 0)) < 1e-6);
        Assert.True(split.Remainder.LocationCurve.Centre.DistanceTo(new Point2D(0, 0)) < 1e-6);
        Assert.Equal(Math.PI * 3000, wall.Length + split.Remainder.Length, precision: 6);

        // The door went with the half it is in, and did not move.
        Assert.Equal(split.Remainder.Id, door.HostWallId);
        Assert.True(door.GetCentre(split.Remainder).DistanceTo(doorAt) < 1e-6);

        split.Undo();
        Assert.Equal(1, wall.Bulge, precision: 9);
        Assert.Equal(wall.Id, door.HostWallId);
    }

    [Fact]
    public void AMirroredCurvedWallCurvesTheOtherWay()
    {
        var (document, _, exterior) = Project();
        var wall = HalfCircle(document, exterior);

        ElementTransforms.Mirror(wall, new Line2D(new Point2D(0, 0), Vector2D.UnitX));

        Assert.Equal(-1, wall.Bulge, precision: 9);
        Assert.True(wall.LocationCurve.PointAt(wall.Length / 2).DistanceTo(new Point2D(0, 3000)) < 1e-6);
    }

    [Fact]
    public void ChangingACurvedWallsLocationLineLeavesItWhereItIs()
    {
        var (document, _, exterior) = Project();
        var wall = HalfCircle(document, exterior);
        var half = exterior.Width / 2;
        var before = WallJoins.GetBandOutline(document, wall, exterior, half, -half);

        var parameter = wall.GetInstanceParameters(document).Single(p => p.Name == "Location Line");
        Assert.True(parameter.TrySet("Finish Face Exterior"));

        var after = WallJoins.GetBandOutline(document, wall, exterior, half, -half);
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++)
            Assert.True(before[i].DistanceTo(after[i]) < 1e-6, $"Corner {i} moved.");
    }

    [Fact]
    public void ACurvedWallIsSavedAsACurvedWall()
    {
        var (document, _, exterior) = Project();
        var wall = Add(document, exterior, new Point2D(0, 0), new Point2D(5000, 0), bulge: -0.35);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path).Walls.Single();

            Assert.Equal(-0.35, reloaded.Bulge, precision: 9);
            Assert.Equal(wall.Length, reloaded.Length, precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACurvedWallCopiesAsACurvedWall()
    {
        var (document, _, exterior) = Project();
        var wall = HalfCircle(document, exterior);

        var copy = (Wall)ElementCopy.Clone(wall);

        Assert.Equal(wall.Bulge, copy.Bulge);
    }
}
