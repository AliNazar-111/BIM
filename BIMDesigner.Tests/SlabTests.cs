using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

public class SlabTests
{
    private static (BimDocument Document, FloorType Type, Guid LevelId) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document, document.TypesOf<FloorType>().First(), document.Levels.First().Id);
    }

    private static Floor AddFloor(BimDocument document, FloorType type, Guid levelId, params Point2D[] boundary)
    {
        var floor = new Floor { TypeId = type.Id, LevelId = levelId };
        floor.SetBoundary(boundary);
        document.Add(floor);
        return floor;
    }

    [Fact]
    public void AreaAndPerimeterComeFromTheOutline()
    {
        var (document, type, levelId) = Project();

        var floor = AddFloor(document, type, levelId,
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000));

        Assert.Equal(6000d * 4000, floor.Area, precision: 3);
        Assert.Equal(2 * (6000d + 4000), floor.Perimeter, precision: 3);
    }

    [Fact]
    public void VolumeUsesTheTypeThickness()
    {
        var (document, type, levelId) = Project();

        var floor = AddFloor(document, type, levelId,
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000));

        // The default floor is 60 + 40 + 150 = 250 mm.
        Assert.Equal(250, type.Thickness, precision: 6);
        Assert.Equal(6000d * 4000 * 250, floor.GetVolume(document), precision: 3);
    }

    [Fact]
    public void AnOutlineGivenClockwiseIsStoredAnticlockwise()
    {
        var (document, type, levelId) = Project();

        // Same rectangle, wound the other way.
        var floor = AddFloor(document, type, levelId,
            new Point2D(0, 0), new Point2D(0, 4000), new Point2D(6000, 4000), new Point2D(6000, 0));

        Assert.True(Polygon2D.SignedArea(floor.Boundary) > 0,
            "every slab should wind the same way, so areas are never negative");
        Assert.Equal(6000d * 4000, floor.Area, precision: 3);
    }

    [Fact]
    public void AnLShapedFloorMeasuresItsRealArea()
    {
        var (document, type, levelId) = Project();

        var floor = AddFloor(document, type, levelId,
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 2000),
            new Point2D(3000, 2000), new Point2D(3000, 4000), new Point2D(0, 4000));

        // 6000 x 4000 less the 3000 x 2000 bite out of the corner.
        Assert.Equal(6000d * 4000 - 3000d * 2000, floor.Area, precision: 3);
    }

    [Fact]
    public void ContainsFindsPointsInsideTheOutline()
    {
        var (document, type, levelId) = Project();

        var floor = AddFloor(document, type, levelId,
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000));

        Assert.True(floor.Contains(new Point2D(3000, 2000)));
        Assert.False(floor.Contains(new Point2D(9000, 2000)));
    }

    [Fact]
    public void SlabsShareTheLayerSystemWithWalls()
    {
        var document = BimDocument.CreateDefault();
        var floor = document.TypesOf<FloorType>().Single(t => t.Name.Contains("Screed"));

        // Same CompoundStructure as a wall type, so takeoff works the same way for both.
        Assert.Equal(3, floor.Structure.Layers.Count);
        Assert.Equal(floor.Structure.TotalWidth, floor.Thickness, precision: 6);

        foreach (var layer in floor.Structure.Layers)
            Assert.NotNull(document.FindMaterial(layer.MaterialId));
    }

    [Fact]
    public void TheTemplateOffersAFloorACeilingAndARoof()
    {
        var document = BimDocument.CreateDefault();

        Assert.NotEmpty(document.TypesOf<FloorType>());
        Assert.NotEmpty(document.TypesOf<CeilingType>());
        Assert.NotEmpty(document.TypesOf<RoofType>());
    }

    // ---- persistence -----------------------------------------------------------

    [Fact]
    public void SlabsSurviveSavingAndReopening()
    {
        var (document, type, levelId) = Project();

        var floor = AddFloor(document, type, levelId,
            new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 4000), new Point2D(0, 4000));

        var ceiling = new Ceiling { TypeId = document.TypesOf<CeilingType>().First().Id, LevelId = levelId, HeightOffset = 2400 };
        ceiling.SetBoundary(floor.Boundary);
        document.Add(ceiling);

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = levelId, HeightOffset = 3000 };
        roof.SetBoundary(floor.Boundary);
        document.Add(roof);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        Assert.Single(reloaded.Elements.OfType<Floor>());
        Assert.Single(reloaded.Elements.OfType<Ceiling>());
        Assert.Single(reloaded.Elements.OfType<Roof>());

        var reloadedFloor = reloaded.Elements.OfType<Floor>().Single();
        Assert.Equal(floor.Id, reloadedFloor.Id);
        Assert.Equal(floor.Area, reloadedFloor.Area, precision: 3);
        Assert.Equal(floor.GetVolume(document), reloadedFloor.GetVolume(reloaded), precision: 3);

        Assert.Equal(2400, reloaded.Elements.OfType<Ceiling>().Single().HeightOffset, precision: 6);
        Assert.Equal(3000, reloaded.Elements.OfType<Roof>().Single().HeightOffset, precision: 6);
    }

    /// <summary>
    /// A project written before slabs existed has no slab types, so every slab tool would be
    /// enabled with an empty list and nothing could be placed. Opening it must fill the gap.
    /// </summary>
    [Fact]
    public void OpeningAProjectWithNoSlabTypesGainsThem()
    {
        var document = BimDocument.CreateDefault();

        // Strip the file back to what an older version would have written.
        var json = ProjectFile.ToJson(document)
            .Replace("\"slabTypes\"", "\"slabTypesRemoved\"");

        var reloaded = ProjectFile.FromJson(json);

        Assert.NotEmpty(reloaded.TypesOf<FloorType>());
        Assert.NotEmpty(reloaded.TypesOf<CeilingType>());
        Assert.NotEmpty(reloaded.TypesOf<RoofType>());

        // And the materials those build-ups need must come with them, or every layer would
        // draw as an unresolved grey band.
        foreach (var layer in reloaded.TypesOf<SlabType>().SelectMany(t => t.Structure.Layers))
            Assert.NotNull(reloaded.FindMaterial(layer.MaterialId));
    }

    [Fact]
    public void AProjectThatHasItsOwnTypesIsLeftAlone()
    {
        var document = BimDocument.CreateDefault();
        var wallTypesBefore = document.TypesOf<WallType>().Select(t => t.Id).ToList();

        document.EnsureDefaultTypes();

        // Filling gaps must not duplicate the types a project already has.
        Assert.Equal(wallTypesBefore.OrderBy(id => id),
            document.TypesOf<WallType>().Select(t => t.Id).OrderBy(id => id));
    }

    [Fact]
    public void SlabTypeAssembliesSurviveSavingAndReopening()
    {
        var document = BimDocument.CreateDefault();
        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));

        foreach (var original in document.TypesOf<SlabType>())
        {
            var match = reloaded.TypesOf<SlabType>().Single(t => t.Id == original.Id);

            Assert.Equal(original.Name, match.Name);
            Assert.Equal(original.GetType(), match.GetType());
            Assert.Equal(original.Thickness, match.Thickness, precision: 6);
            Assert.Equal(original.Function, match.Function);
            Assert.Equal(original.Structure.Layers.Count, match.Structure.Layers.Count);
        }
    }
}
