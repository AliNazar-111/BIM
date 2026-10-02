using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.ViewModels;

namespace BIMDesigner.Tests;

/// <summary>
/// Bands within a wall type: a layer made of another material between two heights - a tile band
/// at the foot of the plaster - built, cut, counted and saved as such.
/// </summary>
[Collection("Wpf")]
public class WallBandTests
{
    /// <summary>An exterior wall 6 m long, 3 m high, its inside plaster tiled up to 1.2 m.</summary>
    private static (BimDocument Document, Wall Wall, WallType Type, Guid Tile) TiledWall()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var tile = document.Materials.First(m => m.Name == "Ceramic Tile, White Gloss").Id;
        type.Bands.Add(new WallBand(type.Structure.Layers.Count - 1, 0, 1200, tile));

        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return (document, wall, type, tile);
    }

    [Fact]
    public void ALayerIsItsBandsMaterialWithinItAndItsOwnAboveIt()
    {
        var (_, _, type, tile) = TiledWall();
        var plaster = type.Structure.Layers[^1].MaterialId;
        var last = type.Structure.Layers.Count - 1;

        Assert.Equal(tile, WallBands.MaterialAt(type, last, 600));
        Assert.Equal(plaster, WallBands.MaterialAt(type, last, 2000));
        Assert.Equal(type.Structure.Layers[0].MaterialId, WallBands.MaterialAt(type, 0, 600));

        var runs = WallBands.Runs(type, last, 0, 3000, 0).ToList();
        Assert.Equal(new[] { (0.0, 1200.0, tile), (1200.0, 3000.0, plaster) }, runs);
    }

    [Fact]
    public void In3DTheBandIsBuiltOfItsOwnMaterialToItsHeight()
    {
        var (document, wall, _, _) = TiledWall();
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == wall.Id).ToList();

        var tile = meshes.Single(mesh => mesh.Description == "Ceramic Tile, White Gloss");
        Assert.Equal(0, tile.Positions.Min(point => point.Z), 3);
        Assert.Equal(1200, tile.Positions.Max(point => point.Z), 3);

        var plaster = meshes.Single(mesh => mesh.Description == "Cement Plaster");
        Assert.Equal(1200, plaster.Positions.Min(point => point.Z), 3);
        Assert.Equal(3000, plaster.Positions.Max(point => point.Z), 3);
    }

    [Fact]
    public void ASectionCutsTheBandAsItself()
    {
        var (document, wall, _, _) = TiledWall();
        var marker = new SectionMarker { Name = "A", Start = new Point2D(3000, -2000), End = new Point2D(3000, 2000), LevelId = wall.LevelId };
        document.Add(marker);

        var cut = SectionProjection.Build(document, marker).Pieces.Where(piece => piece.ElementId == wall.Id && piece.Depth == SectionDepth.Cut).ToList();
        var tile = Assert.Single(cut, piece => piece.Description == "Ceramic Tile, White Gloss");
        Assert.Equal((0.0, 1200.0), (tile.Bounds.Bottom, tile.Bounds.Top));
        Assert.Contains(cut, piece => piece.Description == "Cement Plaster" && Math.Abs(piece.Bounds.Bottom - 1200) < 1e-6);
    }

    [Fact]
    public void TheTakeoffCountsTheBandOverItsShareOfTheHeight()
    {
        var (document, wall, _, _) = TiledWall();
        var lines = MaterialTakeoff.Lines(document);
        var area = wall.GetArea(document);

        Assert.Equal(area * 1200 / 3000, lines.Single(line => line.Material == "Ceramic Tile, White Gloss").Area, 3);
        Assert.Equal(area * 1800 / 3000, lines.Single(line => line.Material == "Cement Plaster").Area, 3);
    }

    [Fact]
    public void ItIsSavedDuplicatedAndUndone()
    {
        var (document, _, type, tile) = TiledWall();

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).FindType<WallType>(type.Id)!;
        Assert.Equal(type.Bands, loaded.Bands);
        Assert.Equal(type.Bands, type.Duplicate("Copy").Bands);

        var design = WallTypeDesign.Of(type) with { Bands = Array.Empty<WallBand>() };
        var command = new EditWallTypeCommand(type, design);
        command.Redo();
        Assert.Empty(type.Bands);
        command.Undo();
        Assert.Equal(tile, Assert.Single(type.Bands).MaterialId);
    }

    [Fact]
    public void TheEditorAddsABandOnTheSelectedLayerAndRemovingTheLayerTakesItAway()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var history = new UndoStack();
                var exterior = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
                var window = new WallTypesWindow(document, history, exterior);

                var layers = (DataGrid)window.FindName("LayerGrid");
                layers.SelectedIndex = layers.Items.Count - 1;
                Click(window, "OnAddBand");
                var bands = (DataGrid)window.FindName("BandGrid");
                var row = Assert.IsType<BandDraftRow>(Assert.Single(bands.Items.Cast<object>()));
                row.TopText = "900";
                Click(window, "OnApply");

                var band = Assert.Single(exterior.Bands);
                Assert.Equal((exterior.Structure.Layers.Count - 1, 0.0, (double?)900), (band.Layer, band.Bottom, band.Top));

                // The layer goes, and its band with it.
                layers.SelectedIndex = layers.Items.Count - 1;
                Click(window, "OnRemoveLayer");
                Assert.Empty(bands.Items);
                Click(window, "OnApply");
                Assert.Empty(exterior.Bands);

                history.Undo();
                Assert.Single(exterior.Bands);
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The editor failed on the UI thread.", failure);
    }

    private static void Click(System.Windows.Window window, string handler) =>
        window.GetType().GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { window, new RoutedEventArgs() });
}
