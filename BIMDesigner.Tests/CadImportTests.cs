using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using CSMath;

namespace BIMDesigner.Tests;

/// <summary>
/// DWG and DXF drawings brought in to work over: read as AutoCAD draws them - blocks put in where
/// they are inserted, bulged polylines as their arcs, text where it stands, the file's units and its
/// hidden layers - placed, turned and scaled on a storey, snapped to, and kept in the project's file.
/// </summary>
[Collection("Wpf")]
public class CadImportTests
{
    /// <summary>A drawing in metres: a 10 by 8 m outline, a door's swing as a bulge, a circle, an arc, a block put in twice, text, a hatch and a hidden layer.</summary>
    private static CadDocument Survey()
    {
        var cad = new CadDocument(ACadVersion.AC1032);
        cad.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Meters;
        var walls = new Layer("WALLS") { Color = new Color(255, 0, 0) };
        var trees = new Layer("TREES") { Color = new Color(0, 160, 0) };
        var hidden = new Layer("NOTES-OFF") { IsOn = false };
        cad.Layers.Add(walls);
        cad.Layers.Add(trees);
        cad.Layers.Add(hidden);

        // The outline, and a half circle by a bulge of 1 from (0,0) to (2,0) - its top at (1,-1), the bulge to the right going along.
        cad.Entities.Add(new LwPolyline(new IVector[] { new XY(0, 0), new XY(10, 0), new XY(10, 8), new XY(0, 8) }) { Layer = walls, IsClosed = true });
        var swing = new LwPolyline(new IVector[] { new XY(0, 0), new XY(2, 0) }) { Layer = walls };
        swing.Vertices[0].Bulge = 1;
        cad.Entities.Add(swing);
        cad.Entities.Add(new Circle(new XYZ(5, 4, 0), 0.5) { Layer = walls });
        cad.Entities.Add(new Arc(new XYZ(20, 0, 0), 1, 0, Math.PI / 2) { Layer = walls });
        cad.Entities.Add(new TextEntity("Plot 4") { Layer = walls, InsertPoint = new XYZ(1, 1, 0), Height = 0.25 });
        cad.Entities.Add(new TextEntity("Not shown") { Layer = hidden, InsertPoint = new XYZ(2, 2, 0), Height = 0.25 });

        // A tree as a block on layer 0, its base point at its middle (1,1): put in at (12,2), and at (14,2) twice the size turned a quarter.
        var tree = new BlockRecord("TREE");
        tree.BlockEntity.BasePoint = new XYZ(1, 1, 0);
        tree.Entities.Add(new Line(new XYZ(1, 1, 0), new XYZ(2, 1, 0)));
        cad.BlockRecords.Add(tree);
        cad.Entities.Add(new Insert(tree) { Layer = trees, InsertPoint = new XYZ(12, 2, 0) });
        cad.Entities.Add(new Insert(tree) { Layer = trees, InsertPoint = new XYZ(14, 2, 0), XScale = 2, YScale = 2, Rotation = Math.PI / 2 });

        // A hatch, left out and said so.
        var hatch = new Hatch { Layer = walls, IsSolid = true, Pattern = HatchPattern.Solid };
        var path = new Hatch.BoundaryPath();
        path.Edges.Add(new Hatch.BoundaryPath.Polyline(new[] { new XYZ(0, 0, 0), new XYZ(1, 0, 0), new XYZ(1, 1, 0) }, true));
        hatch.Paths.Add(path);
        cad.Entities.Add(hatch);
        return cad;
    }

    private static CadImport.Result ReadBack(CadDocument cad, string extension)
    {
        var file = Path.Combine(Path.GetTempPath(), $"survey-{Guid.NewGuid():N}{extension}");
        try
        {
            if (extension == ".dwg") DwgWriter.Write(file, cad);
            else DxfWriter.Write(file, cad, false);
            return CadImport.Read(file);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(".dwg")]
    [InlineData(".dxf")]
    public void ADrawingIsReadAsItIsDrawnBlocksArcsTextUnitsAndAll(string extension)
    {
        var read = ReadBack(Survey(), extension);
        var drawing = read.Drawing;
        Assert.Equal((1000.0, "Metres"), (read.Scale, read.Units));
        Assert.Contains("NOTES-OFF", read.HiddenLayers);
        Assert.Contains(read.Notes, note => note.Contains("1 hatches"));
        int Layer(string name) => drawing.Layers.ToList().FindIndex(layer => layer.Name == name);
        Assert.Equal(new Core.Materials.ColourRgb(255, 0, 0), drawing.Layers[Layer("WALLS")].Colour);

        // The outline closed, its corners where they were drawn.
        var outline = Assert.Single(drawing.Paths, path => path.Closed && path.Points.Count == 4);
        Assert.Equal(new Point2D(10, 8), outline.Points[2]);

        // The swing a half circle of radius 1 below the line, every point of it 1 from (1,0), its lowest at (1,-1).
        var swing = Assert.Single(drawing.Paths, path => !path.Closed && path.Points[0] == new Point2D(0, 0) && path.Points[^1].DistanceTo(new Point2D(2, 0)) < 1e-9);
        Assert.True(swing.Points.Count > 10);
        Assert.All(swing.Points, point => Assert.Equal(1, point.DistanceTo(new Point2D(1, 0)), 6));
        Assert.Equal(-1, swing.Points.Min(point => point.Y), 3);

        // The circle all round at 0.5, the arc a quarter from (21,0) to (20,1).
        Assert.Contains(drawing.Paths, path => path.Closed && path.Points.All(point => Math.Abs(point.DistanceTo(new Point2D(5, 4)) - 0.5) < 1e-9));
        var arc = Assert.Single(drawing.Paths, path => path.Points.All(point => Math.Abs(point.DistanceTo(new Point2D(20, 0)) - 1) < 1e-9) && !path.Closed);
        Assert.Equal(new Point2D(21, 0).X, arc.Points[0].X, 9);
        Assert.Equal(1, arc.Points[^1].Y, 9);

        // The tree block on its insert's layer: its line from the base point put at (12,2); twice the size and turned, at (14,2) running up 2.
        var trees = drawing.Paths.Where(path => path.Layer == Layer("TREES")).ToList();
        Assert.Equal(2, trees.Count);
        Assert.Contains(trees, path => path.Points[0].DistanceTo(new Point2D(12, 2)) < 1e-9 && path.Points[1].DistanceTo(new Point2D(13, 2)) < 1e-9);
        Assert.Contains(trees, path => path.Points[0].DistanceTo(new Point2D(14, 2)) < 1e-9 && path.Points[1].DistanceTo(new Point2D(14, 4)) < 1e-9);

        // The text where it stands, as tall as it is; the hidden layer's read too, to be shown if wanted.
        var plot = Assert.Single(drawing.Labels, label => label.Text == "Plot 4");
        Assert.Equal((1.0, 1.0, 0.25), (plot.At.X, plot.At.Y, plot.Height));
        Assert.Contains(drawing.Labels, label => label.Text == "Not shown" && label.Layer == Layer("NOTES-OFF"));
    }

    [Fact]
    public void AnUnderlayIsPlacedTurnedAndScaledOnThePlanAndSnappedTo()
    {
        var read = ReadBack(Survey(), ".dwg");
        var underlay = new CadUnderlay { Drawing = read.Drawing, Scale = read.Scale, Location = new Point2D(5000, 1000), Rotation = 90 };

        // A metre of the drawing is 1000 mm on the plan, turned a quarter about its origin at (5000, 1000).
        Assert.Equal(new Point2D(5000, 11000).X, underlay.ToModel(new Point2D(10, 0)).X, 6);
        Assert.Equal(11000, underlay.ToModel(new Point2D(10, 0)).Y, 6);
        var back = underlay.ToDrawing(underlay.ToModel(new Point2D(3, 7)));
        Assert.Equal((3.0, 7.0), (Math.Round(back.X, 9), Math.Round(back.Y, 9)));

        // Near the outline's far corner: that corner to snap to, and the nearest points on the lines through it.
        var corner = underlay.ToModel(new Point2D(10, 8));
        var (ends, along) = underlay.Near(corner + new Vector2D(30, -20), 100);
        Assert.Contains(ends, end => end.DistanceTo(corner) < 1e-6);
        Assert.NotEmpty(along);

        // Hidden layers are not snapped to; moved, turned and scaled as the model is.
        underlay.HiddenLayers.Add("WALLS");
        Assert.Empty(underlay.Near(corner, 100).Ends);
        underlay.HiddenLayers.Clear();
        ElementTransforms.Move(underlay, new Vector2D(100, 0));
        Assert.Equal(new Point2D(5100, 1000), underlay.Location);
        ElementTransforms.Rotate(underlay, new Point2D(5100, 1000), 90);
        Assert.Equal(180, underlay.Rotation, 9);
        ElementTransforms.Scale(underlay, new Point2D(5100, 1000), 2);
        Assert.Equal(2000, underlay.Scale, 9);
        Assert.True(ElementTransforms.CanMove(underlay));
        var copy = Assert.IsType<CadUnderlay>(ElementCopy.Clone(underlay));
        Assert.Same(underlay.Drawing, copy.Drawing);
        Assert.Equal((underlay.Location, underlay.Rotation, underlay.Scale), (copy.Location, copy.Rotation, copy.Scale));
    }

    [Fact]
    public void AnUnderlayIsKeptInTheProjectToTheMillimetreOfANationalGrid()
    {
        var document = BimDocument.CreateDefault();
        var read = ReadBack(Survey(), ".dxf");

        // Set out on the national grid: half a million metres east - where 32-bit numbers would lose the millimetres.
        var underlay = new CadUnderlay
        {
            LevelId = document.Levels[0].Id, Source = @"C:\Surveys\plot4.dwg", Drawing = read.Drawing, Scale = 1000, Rotation = 12.5,
            Location = new Point2D(-530_123_456.789, -180_987_654.321), Halftone = false
        };
        underlay.HiddenLayers.Add("TREES");
        document.Add(underlay);

        var kept = Assert.Single(ProjectFile.FromJson(ProjectFile.ToJson(document)).All<CadUnderlay>());
        Assert.Equal((underlay.Location, 12.5, 1000.0, false, @"C:\Surveys\plot4.dwg"), (kept.Location, kept.Rotation, kept.Scale, kept.Halftone, kept.Source));
        Assert.Contains("TREES", kept.HiddenLayers);
        Assert.Equal(read.Drawing.Layers, kept.Drawing.Layers);
        Assert.Equal(read.Drawing.Paths.Count, kept.Drawing.Paths.Count);
        for (var i = 0; i < read.Drawing.Paths.Count; i++)
        {
            Assert.Equal(read.Drawing.Paths[i].Points, kept.Drawing.Paths[i].Points);
            Assert.Equal((read.Drawing.Paths[i].Layer, read.Drawing.Paths[i].Closed, read.Drawing.Paths[i].Colour), (kept.Drawing.Paths[i].Layer, kept.Drawing.Paths[i].Closed, kept.Drawing.Paths[i].Colour));
        }

        Assert.Equal(read.Drawing.Labels, kept.Drawing.Labels);

        // Its layers shown and hidden as one step to undo.
        var command = new SetUnderlayLayersCommand(kept, new[] { "WALLS" });
        command.Redo();
        Assert.Equal(new[] { "WALLS" }, kept.HiddenLayers);
        command.Undo();
        Assert.Equal(new[] { "TREES" }, kept.HiddenLayers);
    }

    [Fact]
    public void APlanWrittenAsDwgComesBackOnItsLayers()
    {
        // The National CAD Standard's layers of a plan this program wrote, read by its own import as any other DWG.
        Exception? failure = null;
        CadImport.Result? read = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();
                var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
                var file = Path.Combine(Path.GetTempPath(), $"plan-{Guid.NewGuid():N}.dwg");
                try
                {
                    BIMDesigner.UI.Export.CadExport.Save(BIMDesigner.UI.Export.CadExport.Plan(document, document.Levels[0]), file);
                    read = CadImport.Read(file);
                }
                finally { File.Delete(file); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan did not go out and back.", failure);

        Assert.Equal(1, read!.Scale);
        Assert.Contains(read.Drawing.Layers, layer => layer.Name == "A-WALL");
        var bounds = read.Drawing.Bounds;
        Assert.InRange(bounds.MinX, -400, 0);
        Assert.InRange(bounds.MaxX, 10000, 10400);
    }
}
