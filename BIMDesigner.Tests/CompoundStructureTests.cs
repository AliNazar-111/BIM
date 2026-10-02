using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Covers the layered assembly and the location line, which together decide where a wall's
/// body actually sits relative to the line the user drew.
/// </summary>
public class CompoundStructureTests
{
    private static readonly Guid Brick = Guid.NewGuid();
    private static readonly Guid Cavity = Guid.NewGuid();
    private static readonly Guid Block = Guid.NewGuid();
    private static readonly Guid Plaster = Guid.NewGuid();

    /// <summary>Brick 100 | cavity 50 | block core 140 | plaster 10 = 300 mm.</summary>
    private static CompoundStructure CavityWall() => new(
        new MaterialLayer(LayerFunction.Finish1, Brick, 100),
        new MaterialLayer(LayerFunction.ThermalAir, Cavity, 50),
        new MaterialLayer(LayerFunction.Structure, Block, 140),
        new MaterialLayer(LayerFunction.Finish2, Plaster, 10));

    [Fact]
    public void TotalWidth_IsTheSumOfTheLayers()
    {
        Assert.Equal(300, CavityWall().TotalWidth, precision: 6);
    }

    [Fact]
    public void Core_IsTheStructuralRunOfLayers()
    {
        var structure = CavityWall();

        Assert.Equal(150, structure.ExteriorWidth, precision: 6);   // brick + cavity
        Assert.Equal(140, structure.CoreWidth, precision: 6);       // block
        Assert.Equal(10, structure.InteriorWidth, precision: 6);    // plaster
    }

    [Fact]
    public void Core_FallsBackToTheWholeAssemblyWhenNothingIsStructural()
    {
        var structure = new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, Plaster, 15),
            new MaterialLayer(LayerFunction.Finish2, Plaster, 15));

        Assert.Equal(30, structure.CoreWidth, precision: 6);
        Assert.Equal(0, structure.ExteriorWidth, precision: 6);
        Assert.Equal(0, structure.InteriorWidth, precision: 6);
    }

    [Fact]
    public void GetLayerOffsets_WalksFromTheExteriorFaceInward()
    {
        var offsets = CavityWall().GetLayerOffsets().ToList();

        Assert.Equal(4, offsets.Count);
        Assert.Equal((0, 100), (offsets[0].Start, offsets[0].End));
        Assert.Equal((100, 150), (offsets[1].Start, offsets[1].End));
        Assert.Equal((150, 290), (offsets[2].Start, offsets[2].End));
        Assert.Equal((290, 300), (offsets[3].Start, offsets[3].End));
    }

    [Theory]
    [InlineData(WallLocationLine.WallCentreline, 0)]
    [InlineData(WallLocationLine.FinishFaceExterior, 150)]
    [InlineData(WallLocationLine.FinishFaceInterior, -150)]
    [InlineData(WallLocationLine.CoreFaceExterior, 0)]      // 150 - 150 exterior layers
    [InlineData(WallLocationLine.CoreFaceInterior, -140)]   // -150 + 10 interior layers
    [InlineData(WallLocationLine.CoreCentreline, -70)]      // midway through the 140 core
    public void LocationLineOffset_IsMeasuredFromTheCentrelineTowardTheExterior(
        WallLocationLine locationLine, double expected)
    {
        var wall = new Wall { LocationLine = locationLine };

        Assert.Equal(expected, wall.GetLocationLineOffset(CavityWall()), precision: 6);
    }

    [Fact]
    public void BodyCentreline_ShiftsOffTheDrawnLineByTheLocationLineOffset()
    {
        var structure = CavityWall();

        // Drawn west to east, so the exterior normal points north (+Y).
        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(5000, 0),
            LocationLine = WallLocationLine.FinishFaceExterior
        };

        var (start, end) = wall.GetBodyCentreline(structure);

        // The drawn line is the exterior face, so the body sits 150 mm to its interior side.
        Assert.Equal(0, start.X, precision: 6);
        Assert.Equal(-150, start.Y, precision: 6);
        Assert.Equal(5000, end.X, precision: 6);
        Assert.Equal(-150, end.Y, precision: 6);
    }

    [Fact]
    public void BodyCentreline_IsTheDrawnLineForACentrelineWall()
    {
        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(5000, 0),
            LocationLine = WallLocationLine.WallCentreline
        };

        var (start, end) = wall.GetBodyCentreline(CavityWall());

        Assert.Equal(wall.Start, start);
        Assert.Equal(wall.End, end);
    }

    [Fact]
    public void Flipping_MirrorsTheExteriorSide()
    {
        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(5000, 0),
            LocationLine = WallLocationLine.FinishFaceExterior
        };

        var unflipped = wall.GetBodyCentreline(CavityWall()).Start;
        wall.Flipped = true;
        var flipped = wall.GetBodyCentreline(CavityWall()).Start;

        Assert.Equal(-unflipped.Y, flipped.Y, precision: 6);
    }

    [Fact]
    public void DefaultTemplate_ProvidesUsableWallTypes()
    {
        var document = BimDocument.CreateDefault();
        var types = document.TypesOf<WallType>().ToList();

        Assert.Equal(3, types.Count(type => type.Log is null));
        Assert.Equal(3, types.Count(type => type.Log is not null));
        Assert.All(types, type => Assert.True(type.Width > 0));
        Assert.All(types, type => Assert.NotEmpty(type.Structure.Layers));

        // Every layer must resolve to a real material, or the plan view draws grey boxes.
        foreach (var layer in types.SelectMany(type => type.Structure.Layers))
            Assert.NotNull(document.FindMaterial(layer.MaterialId));
    }
}
