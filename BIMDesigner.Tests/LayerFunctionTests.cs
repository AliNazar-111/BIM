using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI.ViewModels;

namespace BIMDesigner.Tests;

/// <summary>
/// The six layer functions of a compound structure: named and numbered by join priority,
/// with the membrane as a line of no thickness.
/// </summary>
public class LayerFunctionTests
{
    [Fact]
    public void TheFunctionsAreTheStandardSixInPriorityOrder()
    {
        Assert.Equal(
            new[] { "Structure [1]", "Substrate [2]", "Thermal/Air Layer [3]", "Finish 1 [4]", "Finish 2 [5]", "Membrane Layer" },
            LayerFunctions.Choices);

        Assert.Equal(0, (int)LayerFunction.Membrane);
        Assert.Equal(1, (int)LayerFunction.Structure);
        Assert.Equal(5, (int)LayerFunction.Finish2);
    }

    [Theory]
    [InlineData("Finish 1 [4]", LayerFunction.Finish1)]
    [InlineData("Thermal/Air Layer [3]", LayerFunction.ThermalAir)]
    [InlineData("Membrane Layer", LayerFunction.Membrane)]
    [InlineData("Finish2", LayerFunction.Finish2)]
    [InlineData("FinishExterior", LayerFunction.Finish1)]
    [InlineData("FinishInterior", LayerFunction.Finish2)]
    [InlineData("ThermalOrAir", LayerFunction.ThermalAir)]
    public void FunctionsAreReadFromTheirLabelsTheirNamesAndOlderNames(string text, LayerFunction expected)
    {
        Assert.True(LayerFunctions.TryParse(text, out var function));
        Assert.Equal(expected, function);
    }

    [Fact]
    public void AMembraneHasNoThicknessAndEverythingElseHasSome()
    {
        var material = Guid.NewGuid();

        _ = new MaterialLayer(LayerFunction.Membrane, material, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaterialLayer(LayerFunction.Membrane, material, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaterialLayer(LayerFunction.Finish2, material, 0));
    }

    [Fact]
    public void ChoosingMembraneInTheEditorTakesTheThicknessAway()
    {
        var document = BimDocument.CreateDefault();
        var exterior = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var row = new LayerDraftRow(document, exterior.Structure.Layers[1]);

        row.FunctionName = "Membrane Layer";
        Assert.False(row.CanEditThickness);
        Assert.Equal(0, row.ToLayer()!.Thickness);

        row.ThicknessText = "50";
        Assert.Equal(0, row.ToLayer()!.Thickness);

        row.FunctionName = "Finish 2 [5]";
        Assert.True(row.CanEditThickness);
        Assert.True(row.ToLayer()!.Thickness > 0);
    }

    [Fact]
    public void AWallWithAMembraneDrawsBuildsAndSaves()
    {
        var document = BimDocument.CreateDefault();
        var exterior = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));

        var layers = exterior.Structure.Layers.Select(l => l.Clone()).ToList();
        layers.Insert(3, new MaterialLayer(LayerFunction.Membrane, layers[2].MaterialId, 0));

        var design = WallTypeDesign.Of(exterior) with { Layers = layers };
        Assert.Null(design.Problem(document, exterior));
        new EditWallTypeCommand(exterior, design).Redo();

        Assert.Equal(330, exterior.Width, precision: 6);

        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = exterior.Id, LevelId = document.Levels[0].Id
        };
        document.Add(wall);
        document.Add(new Wall
        {
            Start = new Point2D(5000, 0), End = new Point2D(5000, 4000), TypeId = exterior.Id, LevelId = document.Levels[0].Id
        });

        // Every layer but the membrane becomes a solid; the membrane has nothing to build.
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == wall.Id).ToList();
        Assert.Equal(layers.Count - 1, meshes.Count);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path).TypesOf<WallType>().Single(t => t.Id == exterior.Id);

            Assert.Equal(LayerFunction.Membrane, reloaded.Structure.Layers[3].Function);
            Assert.Equal(0, reloaded.Structure.Layers[3].Thickness);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnOlderFileKeepsItsFinishesAndItsWallThicknesses()
    {
        var document = BimDocument.CreateDefault();
        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");

        try
        {
            ProjectFile.Save(document, path);

            // Write it back the way earlier versions named things, including a membrane with
            // a thickness, which they allowed.
            var text = File.ReadAllText(path)
                .Replace("\"Finish1\"", "\"FinishExterior\"")
                .Replace("\"Finish2\"", "\"FinishInterior\"")
                .Replace("\"ThermalAir\"", "\"ThermalOrAir\"");

            var roofFinish = text.IndexOf("\"FinishExterior\"", text.IndexOf("Roof - Warm Flat", StringComparison.Ordinal), StringComparison.Ordinal);
            text = text[..roofFinish] + "\"Membrane\"" + text[(roofFinish + "\"FinishExterior\"".Length)..];
            File.WriteAllText(path, text);

            var reloaded = ProjectFile.Load(path);

            var exterior = reloaded.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
            Assert.Equal(LayerFunction.Finish1, exterior.Structure.Layers[0].Function);
            Assert.Equal(LayerFunction.ThermalAir, exterior.Structure.Layers[1].Function);
            Assert.Equal(LayerFunction.Finish2, exterior.Structure.Layers[^1].Function);
            Assert.Equal(330, exterior.Width, precision: 6);

            var roof = reloaded.TypesOf<RoofType>().Single();
            Assert.Equal(LayerFunction.Finish1, roof.Structure.Layers[0].Function);
            Assert.Equal(320, roof.Structure.TotalWidth, precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
