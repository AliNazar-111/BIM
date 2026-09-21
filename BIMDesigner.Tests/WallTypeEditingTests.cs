using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI.ViewModels;

namespace BIMDesigner.Tests;

/// <summary>Making, editing and deleting wall types, and giving walls a different one.</summary>
public class WallTypeEditingTests
{
    private static (BimDocument Document, WallType Exterior) Project()
    {
        var document = BimDocument.CreateDefault();
        return (document, document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior")));
    }

    private static Wall Add(BimDocument document, WallType type, double y = 0)
    {
        var wall = new Wall
        {
            Start = new Point2D(0, y), End = new Point2D(5000, y), TypeId = type.Id, LevelId = document.Levels[0].Id
        };
        document.Add(wall);
        return wall;
    }

    [Fact]
    public void ADuplicateHasItsOwnLayers()
    {
        var (document, exterior) = Project();
        var copy = exterior.Duplicate(TypeNames.Unique(document, exterior.Name));

        Assert.Equal($"{exterior.Name} 2", copy.Name);
        Assert.NotEqual(exterior.Id, copy.Id);
        Assert.Equal(exterior.Width, copy.Width);
        Assert.Equal(exterior.Function, copy.Function);
        Assert.Equal(exterior.WrapAtInserts, copy.WrapAtInserts);

        copy.Structure.Layers[0].Thickness = 5;
        Assert.NotEqual(5, exterior.Structure.Layers[0].Thickness);
    }

    [Fact]
    public void UniqueNamesCountOnRatherThanStacking()
    {
        var (document, exterior) = Project();

        var second = exterior.Duplicate(TypeNames.Unique(document, exterior.Name));
        new AddTypeCommand(document, second).Redo();

        Assert.Equal($"{exterior.Name} 3", TypeNames.Unique(document, second.Name));
    }

    [Fact]
    public void EditingATypeChangesEveryWallOfItAndUndoPutsItBack()
    {
        var (document, exterior) = Project();
        var first = Add(document, exterior);
        var second = Add(document, exterior, 3000);

        var before = WallTypeDesign.Of(exterior);
        var layers = before.Layers.Select(layer => layer.Clone()).ToList();
        layers.Insert(0, new MaterialLayer(LayerFunction.Finish1, layers[0].MaterialId, 20));

        var edit = new EditWallTypeCommand(exterior, before with
        {
            Name = "  Exterior - Rendered 350mm  ",
            Function = WallFunction.Retaining,
            WrapAtInserts = WallWrapping.Interior,
            Layers = layers
        });
        edit.Redo();

        Assert.Equal("Exterior - Rendered 350mm", exterior.Name);
        Assert.Equal(350, exterior.Width, precision: 6);
        Assert.Equal(WallFunction.Retaining, exterior.Function);
        Assert.Equal(350 * 5000 * first.GetHeight(document), first.GetVolume(document), precision: 3);
        Assert.Equal(first.GetVolume(document), second.GetVolume(document), precision: 3);

        edit.Undo();

        Assert.Equal(before.Name, exterior.Name);
        Assert.Equal(330, exterior.Width, precision: 6);
        Assert.Equal(before.Function, exterior.Function);
        Assert.Equal(before.Layers.Select(l => (l.Function, l.MaterialId, l.Thickness, l.Wraps)),
            exterior.Structure.Layers.Select(l => (l.Function, l.MaterialId, l.Thickness, l.Wraps)));
    }

    [Fact]
    public void AnEditIsCheckedBeforeItIsApplied()
    {
        var (document, exterior) = Project();
        var design = WallTypeDesign.Of(exterior);
        var otherName = document.TypesOf<WallType>().First(t => !ReferenceEquals(t, exterior)).Name;

        Assert.Null(design.Problem(document, exterior));
        Assert.NotNull((design with { Name = " " }).Problem(document, exterior));
        Assert.NotNull((design with { Name = otherName.ToUpperInvariant() }).Problem(document, exterior));
        Assert.NotNull((design with { Layers = Array.Empty<MaterialLayer>() }).Problem(document, exterior));
        Assert.NotNull((design with
        {
            Layers = new[] { new MaterialLayer(LayerFunction.Structure, Guid.NewGuid(), 100) }
        }).Problem(document, exterior));
    }

    [Fact]
    public void OnlyATypeNothingUsesCanBeDeleted()
    {
        var (document, exterior) = Project();
        Add(document, exterior);

        Assert.Throws<InvalidOperationException>(() => new DeleteTypeCommand(document, exterior));

        var spare = exterior.Duplicate("Spare");
        new AddTypeCommand(document, spare).Redo();

        var delete = new DeleteTypeCommand(document, spare);
        delete.Redo();
        Assert.Null(document.FindType<WallType>(spare.Id));

        delete.Undo();
        Assert.Same(spare, document.FindType<WallType>(spare.Id));
    }

    [Fact]
    public void ASelectionChangesTypeTogether()
    {
        var (document, exterior) = Project();
        var partition = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Interior"));
        var walls = new[] { Add(document, exterior), Add(document, exterior, 3000), Add(document, partition, 6000) };

        var change = new SetElementsTypeCommand(walls, partition);
        change.Redo();
        Assert.All(walls, wall => Assert.Equal(partition.Id, wall.TypeId));

        change.Undo();
        Assert.Equal(exterior.Id, walls[0].TypeId);
        Assert.Equal(exterior.Id, walls[1].TypeId);
        Assert.Equal(partition.Id, walls[2].TypeId);
    }

    [Fact]
    public void NewAndEditedTypesAreSaved()
    {
        var (document, exterior) = Project();
        var copy = exterior.Duplicate("Exterior - Timber Clad");
        new AddTypeCommand(document, copy).Redo();

        var layers = copy.Structure.Layers.Select(l => l.Clone()).ToList();
        layers[0].Thickness = 22;
        new EditWallTypeCommand(copy, WallTypeDesign.Of(copy) with { Layers = layers }).Redo();

        Add(document, copy);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);

            var saved = reloaded.TypesOf<WallType>().Single(t => t.Name == "Exterior - Timber Clad");
            Assert.Equal(copy.Width, saved.Width, precision: 6);
            Assert.Equal(22, saved.Structure.Layers[0].Thickness, precision: 6);
            Assert.Equal(saved.Id, reloaded.Walls.Single().TypeId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("25 mm", 25)]
    [InlineData("0.1 m", 100)]
    public void ALayerTakesAThicknessAsTyped(string typed, double millimetres)
    {
        var (document, exterior) = Project();
        var row = new LayerDraftRow(document, exterior.Structure.Layers[0]);

        row.ThicknessText = typed;

        Assert.Equal(millimetres, row.ToLayer()!.Thickness, precision: 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("thick")]
    public void ALayerRefusesAThicknessThatIsNotOne(string typed)
    {
        var (document, exterior) = Project();
        var row = new LayerDraftRow(document, exterior.Structure.Layers[0]);

        row.ThicknessText = typed;

        Assert.Null(row.ToLayer());
    }
}
