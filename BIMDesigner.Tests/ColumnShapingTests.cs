using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// What a column does between its base and its top: taper, twist, lean, fluting, and being
/// built with a base and a capital. All of it parameters, so the geometry is regenerated rather
/// than stored - which is what makes a classical column follow its own height.
/// </summary>
public class ColumnShapingTests
{
    private static (BimDocument Document, ColumnType Type, Column Column) Standing(double height = 3000)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<ColumnType>().Single(t => t.Name == "Column - 450 x 450");

        var column = new Column
        {
            TypeId = type.Id, LevelId = document.Levels[0].Id,
            Location = new Point2D(0, 0), UnconnectedHeight = height
        };

        document.Add(column);
        return (document, type, column);
    }

    private static IReadOnlyList<Point3D> Solid(BimDocument document, Column column) =>
        ModelMeshBuilder.BuildColumn(document, column).Single().Positions;

    /// <summary>The narrowest the solid ever gets, measured ring by ring.</summary>
    private static double Narrowest(IReadOnlyList<Point3D> points) =>
        points.GroupBy(p => Math.Round(p.Z, 3)).Select(ring => ring.Max(p => p.X) - ring.Min(p => p.X)).Min();

    /// <summary>How far the solid reaches across at a height, which is what a taper changes.</summary>
    private static double WidthAt(IReadOnlyList<Point3D> points, double z)
    {
        var near = points.Where(p => Math.Abs(p.Z - z) < 120).ToList();
        return near.Count == 0 ? 0 : near.Max(p => p.X) - near.Min(p => p.X);
    }

    [Fact]
    public void ATaperedColumnIsNarrowerAtTheTopAndAFlaredOneWider()
    {
        var (document, type, column) = Standing();

        var straight = Solid(document, column);
        Assert.Equal(WidthAt(straight, 0), WidthAt(straight, 3000), precision: 3);

        type.Shaping.TopScale = 0.6;
        var tapered = Solid(document, column);

        Assert.Equal(450, WidthAt(tapered, 0), precision: 0);
        Assert.Equal(450 * 0.6, WidthAt(tapered, 3000), precision: 0);

        // Flaring is the same parameter the other way, not a shape of its own.
        type.Shaping.TopScale = 1.4;
        Assert.True(WidthAt(Solid(document, column), 3000) > 450 * 1.3);
    }

    [Fact]
    public void ATwistedColumnTurnsOnTheWayUpAndASlantedOneLeans()
    {
        var (document, type, column) = Standing();

        // A square turned 45 degrees reaches further across its corners than its faces do.
        type.Shaping.Twist = 45;
        var twisted = Solid(document, column);

        Assert.Equal(450, WidthAt(twisted, 0), precision: 0);
        Assert.True(WidthAt(twisted, 3000) > 450 * 1.3, "the top is not turned");

        type.Shaping.Twist = 0;
        type.Shaping.SlantAcross = 800;
        var slanted = Solid(document, column);

        // The foot is where it was put and the top has moved across by what was asked for.
        var foot = slanted.Where(p => p.Z < 10).ToList();
        var head = slanted.Where(p => p.Z > 2990).ToList();

        Assert.Equal(0, (foot.Min(p => p.X) + foot.Max(p => p.X)) / 2, precision: 3);
        Assert.Equal(800, (head.Min(p => p.X) + head.Max(p => p.X)) / 2, precision: 3);
    }

    [Fact]
    public void AColumnWithABaseAndCapitalIsWiderAtBothEnds()
    {
        var (document, type, column) = Standing(4000);

        type.Shaping.BaseHeight = 300;
        type.Shaping.BaseSpread = 0.2;
        type.Shaping.CapitalHeight = 400;
        type.Shaping.CapitalSpread = 0.3;

        var built = Solid(document, column);

        Assert.Equal(450 * 1.2, WidthAt(built, 50), precision: 0);
        Assert.Equal(450 * 1.3, WidthAt(built, 3950), precision: 0);

        // The shaft between them is the section itself: the narrowest the column ever gets.
        Assert.Equal(450, Narrowest(built), precision: 0);

        // It is one solid from bottom to top, with nothing missing between the parts.
        Assert.Equal(0, built.Min(p => p.Z), precision: 3);
        Assert.Equal(4000, built.Max(p => p.Z), precision: 3);

        // Made taller, the shaft grows and the base and capital stay the size they were: the
        // point of holding them as parameters rather than as a mesh.
        column.UnconnectedHeight = 6000;
        var taller = Solid(document, column);

        Assert.Equal(450 * 1.2, WidthAt(taller, 50), precision: 0);
        Assert.Equal(450, Narrowest(taller), precision: 0);
        Assert.Equal(6000, taller.Max(p => p.Z), precision: 3);
    }

    [Fact]
    public void FlutingCutsHollowsRoundTheShaft()
    {
        var plain = ColumnProfile.Ellipse(500, 500);
        var shaping = new ColumnShaping { Flutes = 20, FluteDepth = 25 };

        var fluted = ColumnGeometry.Fluted(plain, shaping);

        // Less material than the plain shaft, and a great many more points round it.
        Assert.True(fluted.Area < plain.Area, "nothing was cut");
        Assert.True(fluted.Area > plain.Area * 0.8, "the flutes ate the column");
        Assert.True(fluted.Outer.Count > plain.Outer.Count * 2, "the flutes left no marks");

        // Asking for none leaves it alone, and one flute is not fluting.
        Assert.Same(plain, ColumnGeometry.Fluted(plain, new ColumnShaping { Flutes = 0 }));
        Assert.Same(plain, ColumnGeometry.Fluted(plain, new ColumnShaping { Flutes = 1, FluteDepth = 25 }));
    }

    [Fact]
    public void CornersCanBeRoundedOrCutOff()
    {
        var square = ColumnProfile.Rectangle(400, 400);

        var chamfered = square.Chamfered(60);
        Assert.Equal(8, chamfered.Outer.Count);
        Assert.Equal(400 * 400 - 4 * (60 * 60 / 2.0), chamfered.Area, precision: 3);

        var rounded = square.Rounded(60);
        Assert.True(rounded.Outer.Count > 8, "the corners were not rounded");
        Assert.True(rounded.Area < square.Area, "rounding took nothing off");
        Assert.True(rounded.Area > chamfered.Area, "a rounded corner leaves more than a cut one");

        // Asking for more than the shape can give takes back only as far as its edges allow,
        // rather than turning it inside out.
        var overdone = square.Chamfered(10000);
        Assert.True(overdone.Area > 0);
        Assert.Equal(400, overdone.Extent.Width, precision: 3);

        // It still reaches as far as it did: taking corners off does not move the faces.
        Assert.Equal(400, chamfered.Extent.Width, precision: 3);
        Assert.Equal(400, rounded.Extent.Width, precision: 3);
    }

    [Fact]
    public void ShapingIsSaved()
    {
        var (document, type, _) = Standing();

        type.Shaping.TopScale = 0.75;
        type.Shaping.Twist = 30;
        type.Shaping.SlantAcross = 120;
        type.Shaping.Flutes = 16;
        type.Shaping.FluteDepth = 18;
        type.Shaping.BaseHeight = 250;
        type.Shaping.CapitalHeight = 320;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var saved = ProjectFile.Load(path).FindType<ColumnType>(type.Id)!;

            Assert.Equal(0.75, saved.Shaping.TopScale, precision: 6);
            Assert.Equal(30, saved.Shaping.Twist, precision: 6);
            Assert.Equal(120, saved.Shaping.SlantAcross, precision: 6);
            Assert.Equal(16, saved.Shaping.Flutes);
            Assert.Equal(18, saved.Shaping.FluteDepth, precision: 6);
            Assert.Equal(250, saved.Shaping.BaseHeight, precision: 6);
            Assert.Equal(320, saved.Shaping.CapitalHeight, precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheClassicalTypesAreParametersRatherThanFixedShapes()
    {
        var document = BimDocument.CreateDefault();

        foreach (var name in new[] { "Column - Tuscan 450", "Column - Doric 500", "Column - Ionic 500" })
        {
            var type = document.TypesOf<ColumnType>().Single(t => t.Name == name);

            Assert.True(type.Shaping.HasBase, $"{name} has no base");
            Assert.True(type.Shaping.HasCapital, $"{name} has no capital");
            Assert.True(type.Shaping.TopScale < 1, $"{name} does not taper");

            var column = new Column
            {
                TypeId = type.Id, LevelId = document.Levels[0].Id,
                Location = new Point2D(0, 0), UnconnectedHeight = 4000
            };
            document.Add(column);

            var built = ModelMeshBuilder.BuildColumn(document, column).Single();
            Assert.False(built.IsEmpty, $"{name} builds nothing");
            Assert.Equal(4000, built.Bounds()!.Value.Max.Z, precision: 3);
        }

        // Only two of them are fluted, and the one that is has the grooves to show for it.
        var doric = document.TypesOf<ColumnType>().Single(t => t.Name == "Column - Doric 500");
        Assert.True(doric.Shaping.Flutes >= 16);
        Assert.True(doric.Profile.Area > ColumnGeometry.Fluted(doric.Profile, doric.Shaping).Area);
    }
}
