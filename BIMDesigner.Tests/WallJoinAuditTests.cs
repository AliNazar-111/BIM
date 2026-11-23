using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// What the geometry audit of November 2026 found at wall junctions, kept fixed.
///
/// A corner too sharp to mitre - under about 29 degrees - was left square at both ends, the two walls running through each other to the
/// joint and the outside of the corner open; it is bevelled now, the walls sharing the mitre line and the tip cut off. A branch meeting a wall
/// at 20 degrees was refused its cut along that wall's face and ran square into its middle. And a wall's layers going on into another past
/// its face - a partition's studs through a brick wall's outer leaf to its blockwork - were cut out of that wall only in the middle of it: at
/// a corner it runs past, across a run of two walls, or coming in aslant, the two walls' layers were built in the same place, the plan and
/// the 3D model both counting it twice.
/// </summary>
[Collection("Wpf")]
public class WallJoinAuditTests
{
    private const string Partition = "Interior - Partition 125mm", Concrete = "Generic - 200mm", Brick = "Exterior - Brick on Block 330mm";

    private static Wall Add(BimDocument document, Point2D from, Point2D to, string type)
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Point2D Off(Point2D from, double degrees, double length) =>
        from + new Vector2D(Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180)) * length;

    /// <summary>A wall's outlines as drawn in plan, whole: each stretch between its cuts.</summary>
    private static List<Point2D[]> Drawn(BimDocument document, Wall wall)
    {
        var type = document.GetWallType(wall)!;
        var half = type.Structure.TotalWidth / 2;
        return WallSlices.InPlan(document, wall, type, 1200).Select(slice => WallJoins.GetBandOutline(wall, type, half, -half, slice.CutFrom, slice.CutTo)).ToList();
    }

    /// <summary>A wall's layers as drawn and built, each layer's pieces with the other walls' layers coming into it taken out, mm².</summary>
    private static double Layers(BimDocument document, Wall wall)
    {
        var type = document.GetWallType(wall)!;
        var half = type.Structure.TotalWidth / 2;
        var intrusions = WallJoins.Intrusions(document, wall, type);
        return type.Structure.GetLayerOffsets().Sum(layer => WallSlices.InPlan(document, wall, type, 1200)
            .Sum(slice => WallJoins.LayerPieces(wall, type, half - layer.Start, half - layer.End, slice.CutFrom, slice.CutTo, intrusions).Sum(piece => Polygon2D.Area(piece))));
    }

    /// <summary>A wall's 3D solids' volume, mm³.</summary>
    private static double Volume(BimDocument document, Wall wall) =>
        Math.Abs(ModelMeshBuilder.BuildWall(document, wall).Sum(mesh =>
        {
            var (positions, indices, volume) = (mesh.Positions, mesh.Indices, 0.0);
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                var (a, b, c) = (positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]);
                volume += (a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X)) / 6;
            }

            return volume;
        }));

    /// <summary>How many walls' outlines cover the most covered point within so far of a joint.</summary>
    private static int MostCovering(BimDocument document, IReadOnlyList<Wall> walls, Point2D joint, double radius)
    {
        var outlines = walls.SelectMany(wall => Drawn(document, wall)).ToList();
        var most = 0;
        for (var i = 0; i <= 60; i++)
        for (var j = 0; j <= 60; j++)
        {
            var point = new Point2D(joint.X - radius + 2 * radius * i / 60 + 0.0137, joint.Y - radius + 2 * radius * j / 60 + 0.0291);
            most = Math.Max(most, outlines.Count(outline => Polygon2D.Contains(outline, point)));
        }

        return most;
    }

    [Theory]
    [InlineData(Partition, 15)]
    [InlineData(Partition, 20)]
    [InlineData(Concrete, 25)]
    [InlineData(Brick, 20)]
    public void ACornerTooSharpToMitreIsBevelledNotOverlapped(string type, double angle)
    {
        var document = BimDocument.CreateDefault();
        var joint = new Point2D(5000, 0);
        var first = Add(document, new Point2D(0, 0), joint, type);
        var second = Add(document, joint, Off(joint, 180 - angle, 5000), type);

        // Each end cut along the mitre to the bevel: three points, the two walls drawn once each where they meet.
        var width = document.GetWallType(first)!.Width;
        foreach (var (wall, atStart) in new[] { (first, false), (second, true) })
        {
            var (start, end) = WallJoins.GetEndCuts(document, wall, document.GetWallType(wall)!);
            Assert.Equal(3, (atStart ? start : end).Points.Count);
            Assert.All(Drawn(document, wall), outline => Assert.False(OutlineChecks.IsSelfIntersecting(outline)));
        }

        Assert.Equal(1, MostCovering(document, new[] { first, second }, joint, width * 3));
        Assert.True(OutlineChecks.Coverage(document, new[] { first, second }, joint, width).JointCovered);
    }

    [Theory]
    [InlineData(Partition, Partition, 20)]
    [InlineData(Partition, Partition, 160)]
    [InlineData(Brick, Concrete, 20)]
    public void AShallowBranchStopsAtTheWallsFace(string main, string branch, double angle)
    {
        var document = BimDocument.CreateDefault();
        var run = Add(document, new Point2D(-5000, 0), new Point2D(5000, 0), main);
        var stem = Add(document, new Point2D(0, 0), Off(new Point2D(0, 0), angle, 5000), branch);
        Assert.Equal(WallEndCondition.Butt, WallJoins.GetEndCuts(document, stem, document.GetWallType(stem)!).Start.Condition);
        Assert.Equal(1, MostCovering(document, new[] { run, stem }, new Point2D(0, 0), 900));
    }

    public static IEnumerable<object[]> Junctions => new[]
    {
        new object[] { "a partition stopping against a brick wall's outside face at a corner", new Func<BimDocument, Wall[]>(document => new[]
        {
            Add(document, new Point2D(0, 0), new Point2D(5000, 0), Partition), Add(document, new Point2D(5000, 0), new Point2D(5000, 5000), Brick)
        }) },
        new object[] { "a partition meeting a brick wall at 160 degrees", new Func<BimDocument, Wall[]>(document => new[]
        {
            Add(document, new Point2D(0, 0), new Point2D(5000, 0), Partition), Add(document, new Point2D(5000, 0), Off(new Point2D(5000, 0), 20, 5000), Brick)
        }) },
        new object[] { "a brick wall stopping square against a run of two", new Func<BimDocument, Wall[]>(document => new[]
        {
            Add(document, new Point2D(0, 0), new Point2D(5000, 0), Brick), Add(document, new Point2D(0, 0), new Point2D(0, 5000), Brick), Add(document, new Point2D(0, 0), new Point2D(-5000, 0), Brick)
        }) },
        new object[] { "a brick wall stopping at 45 degrees against a run of two", new Func<BimDocument, Wall[]>(document => new[]
        {
            Add(document, new Point2D(0, 0), new Point2D(5000, 0), Brick), Add(document, new Point2D(0, 0), Off(new Point2D(0, 0), 45, 5000), Brick), Add(document, new Point2D(0, 0), new Point2D(-5000, 0), Brick)
        }) },
        new object[] { "four brick walls crossing", new Func<BimDocument, Wall[]>(document => new[]
        {
            Add(document, new Point2D(0, 0), new Point2D(5000, 0), Brick), Add(document, new Point2D(0, 0), new Point2D(0, 5000), Brick),
            Add(document, new Point2D(0, 0), new Point2D(-5000, 0), Brick), Add(document, new Point2D(0, 0), new Point2D(0, -5000), Brick)
        }) }
    };

    [Theory]
    [MemberData(nameof(Junctions))]
    public void LayersGoingOnIntoAnotherWallAreCutOutOfIt(string name, Func<BimDocument, Wall[]> build)
    {
        // The layers of the walls together tile what their outlines cover - nothing built twice - and the 3D model is that lifted 3 m: to a
        // thousandth, a wall coming in aslant across a run of two leaving a sliver of about 0.05% between them.
        var document = BimDocument.CreateDefault();
        var walls = build(document);
        var plan = walls.Sum(wall => Drawn(document, wall).Sum(outline => Polygon2D.Area(outline)));
        var layers = walls.Sum(wall => Layers(document, wall));
        Assert.True(Math.Abs(layers - plan) < Math.Max(2, plan * 0.001), $"{name}: layers {layers / 1e6:0.000000} m² against the outlines' {plan / 1e6:0.000000} m²");
        var volume = walls.Sum(wall => Volume(document, wall));
        Assert.True(Math.Abs(volume - plan * 3000) < Math.Max(1e5, plan * 3000 * 0.001), $"{name}: 3D {volume / 1e9:0.0000} m³ against {plan * 3000 / 1e9:0.0000} m³");
    }
}
