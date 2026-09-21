using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.GeometricModelResource;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>Walls built of other wall types one above another.</summary>
public class StackedWallTests
{
    /// <summary>Brick for the first metre, the partition build-up above it.</summary>
    private static (BimDocument Document, StackedWallType Stacked, WallType Brick, WallType Upper, Wall Wall) Project(double height = 3000)
    {
        var document = BimDocument.CreateDefault();
        var brick = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var upper = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Interior"));

        var stacked = new StackedWallType("Brick Plinth");
        stacked.Tiers.Add(new StackTier(brick.Id, 1000));
        stacked.Tiers.Add(new StackTier(upper.Id, 0));
        document.AddType(stacked);

        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(5000, 0),
            TypeId = stacked.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = height
        };
        document.Add(wall);

        return (document, stacked, brick, upper, wall);
    }

    [Fact]
    public void TheVariableTierTakesTheRestOfTheHeight()
    {
        var (document, stacked, brick, upper, _) = Project();

        var layout = stacked.Layout(document, 3000);

        Assert.Equal(2, layout.Count);
        Assert.Equal((brick, 0.0, 1000.0), layout[0]);
        Assert.Equal((upper, 1000.0, 3000.0), layout[1]);
    }

    [Fact]
    public void AWallShorterThanTheFixedTiersIsCutOffAtTheTop()
    {
        var (document, stacked, brick, _, _) = Project();

        var layout = stacked.Layout(document, 800);

        Assert.Equal((brick, 0.0, 800.0), Assert.Single(layout));
    }

    [Fact]
    public void APlanShowsTheTierItCuts()
    {
        var (document, _, _, upper, wall) = Project();

        // 1200 above the base is in the upper tier.
        Assert.Same(upper, document.GetWallType(wall));

        // A plinth taller than the cut is what the plan shows instead.
        var stacked = document.FindType<StackedWallType>(wall.TypeId)!;
        stacked.Tiers[0] = stacked.Tiers[0] with { Height = 1500 };
        Assert.Equal(stacked.Tiers[0].WallTypeId, document.GetWallType(wall)!.Id);
    }

    [Fact]
    public void EachTierIsBuiltAtItsOwnHeightIn3D()
    {
        var (document, _, brick, upper, wall) = Project();

        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == wall.Id).ToList();

        // A mesh per layer of each tier.
        Assert.Equal(brick.Structure.Layers.Count + upper.Structure.Layers.Count, meshes.Count);

        var heights = meshes.Select(mesh => mesh.Bounds()!.Value).ToList();
        Assert.Contains(heights, bounds => Math.Abs(bounds.Max.Z - 1000) < 1e-6);
        Assert.Contains(heights, bounds => Math.Abs(bounds.Min.Z - 1000) < 1e-6 && Math.Abs(bounds.Max.Z - 3000) < 1e-6);
    }

    [Fact]
    public void ASectionCutsEachTierAtItsOwnHeight()
    {
        var (document, _, _, _, wall) = Project();
        var marker = new SectionMarker
        {
            Name = "A", Start = new Point2D(2500, -2000), End = new Point2D(2500, 2000), LevelId = wall.LevelId
        };
        document.Add(marker);

        var pieces = SectionProjection.Build(document, marker).Pieces.Where(p => p.ElementId == wall.Id).ToList();

        Assert.Contains(pieces, p => Math.Abs(p.Bounds.Top - 1000) < 1e-6);
        Assert.Contains(pieces, p => Math.Abs(p.Bounds.Bottom - 1000) < 1e-6 && Math.Abs(p.Bounds.Top - 3000) < 1e-6);

        // The brick is wider than the partition, so the lower cut is wider too.
        var lower = pieces.Where(p => p.Bounds.Top <= 1000 + 1e-6).Max(p => p.Bounds.Right) - pieces.Where(p => p.Bounds.Top <= 1000 + 1e-6).Min(p => p.Bounds.Left);
        var above = pieces.Where(p => p.Bounds.Bottom >= 1000 - 1e-6).Max(p => p.Bounds.Right) - pieces.Where(p => p.Bounds.Bottom >= 1000 - 1e-6).Min(p => p.Bounds.Left);
        Assert.True(lower > above);
    }

    [Fact]
    public void TheVolumeCountsEachTiersThickness()
    {
        var (document, _, brick, upper, wall) = Project();

        var expected = 5000 * (1000 * brick.Width + 2000 * upper.Width);
        Assert.Equal(expected, wall.GetVolume(document), precision: 3);
    }

    [Fact]
    public void AStackedWallExportsAsOneWallWithABodyPerTier()
    {
        var (document, _, _, _, _) = Project();

        using var model = IfcExport.Build(document);

        var wall = Assert.Single(model.Instances.OfType<IfcWall>());
        Assert.StartsWith("Brick Plinth", wall.Name);
        Assert.Equal(2, model.Instances.OfType<IfcExtrudedAreaSolid>().Count());
    }

    [Fact]
    public void AStackedTypeIsSavedWithItsTiers()
    {
        var (document, stacked, _, _, wall) = Project();

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);

            var copy = reloaded.FindType<StackedWallType>(stacked.Id)!;
            Assert.Equal(stacked.Tiers, copy.Tiers);
            Assert.Equal(wall.GetVolume(document), reloaded.Walls.Single().GetVolume(reloaded), precision: 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWallTypeInAStackCannotBeDeleted()
    {
        var (document, _, brick, _, wall) = Project();
        document.Remove(wall);

        Assert.True(DeleteTypeCommand.UsedInStacks(document, brick));
        Assert.Throws<InvalidOperationException>(() => new DeleteTypeCommand(document, brick));
    }

    [Fact]
    public void TiersAreChecked()
    {
        var (document, _, brick, upper, _) = Project();

        Assert.Null(StackedWallType.Problem(document, new[] { new StackTier(brick.Id, 1000), new StackTier(upper.Id, 0) }));
        Assert.NotNull(StackedWallType.Problem(document, new[] { new StackTier(brick.Id, 0) }));
        Assert.NotNull(StackedWallType.Problem(document, new[] { new StackTier(brick.Id, 1000), new StackTier(upper.Id, 900) }));
        Assert.NotNull(StackedWallType.Problem(document, new[] { new StackTier(brick.Id, 0), new StackTier(upper.Id, 0) }));
        Assert.NotNull(StackedWallType.Problem(document, new[] { new StackTier(Guid.NewGuid(), 1000), new StackTier(upper.Id, 0) }));
    }

    [Fact]
    public void EditingTheTiersIsOneStep()
    {
        var (document, stacked, brick, upper, wall) = Project();

        var edit = new EditStackedWallTypeCommand(stacked, "Tall Plinth",
            new[] { new StackTier(brick.Id, 1500), new StackTier(upper.Id, 0) });
        edit.Redo();

        Assert.Equal("Tall Plinth", stacked.Name);
        Assert.Equal(5000 * (1500 * brick.Width + 1500 * upper.Width), wall.GetVolume(document), precision: 3);

        edit.Undo();
        Assert.Equal("Brick Plinth", stacked.Name);
        Assert.Equal(1000, stacked.Tiers[0].Height);
    }
}
