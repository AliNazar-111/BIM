using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// The ready-made sections a column can be started from: every one has to be a section a
/// column could actually be built to, at any size it is asked for.
/// </summary>
public class ColumnLibraryTests
{
    [Fact]
    public void EveryPresetIsASectionAColumnCouldBeBuiltTo()
    {
        Assert.True(ColumnLibrary.All.Count >= 18, $"only {ColumnLibrary.All.Count} presets");

        // The same names twice would make two of them unpickable.
        Assert.Equal(ColumnLibrary.All.Count, ColumnLibrary.All.Select(p => p.ToString()).Distinct().Count());

        foreach (var preset in ColumnLibrary.All)
        foreach (var (width, depth) in new[] { (300.0, 300.0), (600.0, 350.0), (1200.0, 1200.0) })
        {
            var profile = preset.At(width, depth);

            Assert.False(profile.IsEmpty, $"{preset} at {width}x{depth} is empty");
            Assert.True(profile.Area > 0, $"{preset} has no material");

            // Inside the size asked for, and a fair part of it: a preset that came out a
            // sliver, or spilling past its own size, is not the section it says it is.
            var (madeWidth, madeDepth) = profile.Extent;
            Assert.True(madeWidth <= width * 1.55 + 1, $"{preset} is {madeWidth:0} across, asked {width:0}");
            Assert.True(madeDepth <= depth * 1.55 + 1, $"{preset} is {madeDepth:0} deep, asked {depth:0}");
            Assert.True(profile.Area > width * depth * 0.1, $"{preset} kept almost nothing");

            // Every loop is a loop: enough points to enclose something, and all of them real.
            foreach (var loop in profile.Loops)
            {
                Assert.True(loop.Count >= 3, $"{preset} has a loop of {loop.Count} points");
                Assert.All(loop, point =>
                    Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y), $"{preset} has a point at infinity"));
            }
        }
    }

    [Fact]
    public void TheHollowOnesAreActuallyHollowAndTheBuiltUpOnesAreOnePiece()
    {
        foreach (var name in new[] { "Box", "Tube", "Box round a core" })
        {
            var preset = ColumnLibrary.All.Single(p => p.Name == name);
            var profile = preset.At(500, 500);

            Assert.Single(profile.Holes);
            Assert.True(profile.Area < Polygon2D.Area(profile.Outer), $"{name} is solid");
        }

        // Built up from more than one shape, and still one shape when it is done.
        foreach (var name in new[] { "Cruciform", "T", "Square on a round", "Wall pier" })
        {
            var profile = ColumnLibrary.All.Single(p => p.Name == name).At(500, 500);

            Assert.Empty(profile.Holes);
            Assert.True(profile.Area > 0, name);
        }

        // The ones with reveals have lost material to them without being cut through.
        foreach (var name in new[] { "One reveal each face", "Two reveals each face", "Quirked" })
        {
            var profile = ColumnLibrary.All.Single(p => p.Name == name).At(500, 500);
            Assert.True(profile.Area < 500 * 500, $"{name} cut nothing");
            Assert.True(profile.Area > 500 * 500 * 0.5, $"{name} cut too much away");
        }
    }

    [Fact]
    public void APresetBuildsAColumnTheModelCanDraw()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<ColumnType>().First();

        foreach (var preset in ColumnLibrary.All)
        {
            type.Shape = ColumnShape.Custom;
            type.CustomProfile = preset.At(450, 450);

            var column = new Column
            {
                TypeId = type.Id, LevelId = document.Levels[0].Id,
                Location = new Point2D(0, 0), UnconnectedHeight = 3000
            };

            var mesh = ModelMeshBuilder.BuildColumn(document, column).SingleOrDefault();

            Assert.NotNull(mesh);
            Assert.False(mesh!.IsEmpty, $"{preset} builds no solid");
            Assert.Equal(3000, mesh.Bounds()!.Value.Max.Z, precision: 3);
        }
    }
}
