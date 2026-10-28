using System.IO;
using System.Windows;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Export;
using CSMath;
using BimWindow = BIMDesigner.Core.Architecture.Window;
using Viewport = BIMDesigner.Core.Sheets.Viewport;

namespace BIMDesigner.Tests;

/// <summary>Plans and sheets written as DWG and DXF, and read back as a CAD package would read them.</summary>
[Collection("Wpf")]
public class CadExportTests
{
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The export failed on the UI thread.", failure);
    }

    /// <summary>Written to a file and read back, so what is checked is what a CAD package would open.</summary>
    private static CadDocument RoundTrip(CadDocument drawing, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cad-{Guid.NewGuid():N}{extension}");
        try
        {
            CadExport.Save(drawing, path);
            if (extension == ".dxf")
            {
                var text = File.ReadAllText(path);
                Assert.Contains("SECTION", text);
                Assert.EndsWith("EOF", text.TrimEnd());
                return DxfReader.Read(path);
            }

            // A DWG starts with its version: AC1032, AutoCAD 2018.
            Assert.Equal("AC1032", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 6));
            return DwgReader.Read(path);
        }
        finally { File.Delete(path); }
    }

    private static IEnumerable<XY> Vertices(CadDocument drawing) =>
        drawing.Entities.OfType<LwPolyline>().SelectMany(polyline => polyline.Vertices.Select(vertex => vertex.Location));

    /// <summary>A 10 by 8 m house under a gable roof, a window in its front, a note in it.</summary>
    private static (BimDocument Document, Core.Datums.Level Level) House()
    {
        var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
        var level = document.Levels[0];
        var front = document.Walls.First(wall => Math.Abs(wall.Start.Y) < 1 && Math.Abs(wall.End.Y) < 1);
        var casement = document.TypesOf<WindowType>().First(type => type.Name == "Casement - 1200 x 1200");
        document.Add(new BimWindow { TypeId = casement.Id, LevelId = level.Id, HostWallId = front.Id, DistanceAlongWall = 3000, SillHeight = 900 });
        document.Add(new TextNote { Text = "Kitchen", Position = new Point2D(3000, 4000), LevelId = level.Id });
        return (document, level);
    }

    [Theory]
    [InlineData(".dwg")]
    [InlineData(".dxf")]
    public void APlanIsWrittenAtFullSizeInMillimetresEachKindOnItsLayer(string extension)
    {
        OnUiThread(() =>
        {
            var (document, level) = House();
            var drawing = RoundTrip(CadExport.Plan(document, level), extension);
            Assert.Equal(ACadSharp.Types.Units.UnitsType.Millimeters, drawing.Header.InsUnits);

            // The walls' faces at their real positions: the house 10 by 8 m, its walls round it.
            var points = Vertices(drawing).ToList();
            Assert.NotEmpty(points);
            Assert.InRange(points.Min(p => p.X), -400, 0);
            Assert.InRange(points.Max(p => p.X), 10000, 10400);
            Assert.InRange(points.Min(p => p.Y), -400, 0);
            Assert.InRange(points.Max(p => p.Y), 8000, 8400);

            // Each kind on its own layer, as the National CAD Standard names them; the walls' layers filled on A-WALL-PATT.
            var layers = drawing.Layers.Select(layer => layer.Name).ToHashSet();
            Assert.Contains("A-WALL", layers);
            Assert.Contains("A-GLAZ", layers);
            Assert.Contains(drawing.Entities.OfType<LwPolyline>(), polyline => polyline.Layer.Name == "A-GLAZ");
            Assert.Contains(drawing.Entities.OfType<Hatch>(), hatch => hatch.Layer.Name == "A-WALL-PATT" && hatch.IsSolid);
            Assert.All(drawing.Entities.OfType<Hatch>(), hatch => Assert.EndsWith("-PATT", hatch.Layer.Name));

            // The note as text on the annotation layer, where it was put, and as big as 12 points of it are at 1:100.
            var note = Assert.Single(drawing.Entities.OfType<TextEntity>(), text => text.Value == "Kitchen");
            Assert.Equal("A-ANNO-TEXT", note.Layer.Name);
            Assert.InRange(note.InsertPoint.X, 2900, 3200);
            Assert.InRange(note.InsertPoint.Y, 3700, 4200);
            Assert.InRange(note.Height, 100, 400);
        });
    }

    [Fact]
    public void ARoomsNameGoesOnTheLayerRoomsAreNamedOn()
    {
        OnUiThread(() =>
        {
            var (document, rooms, _) = VentilationTests.Row("Generic - 200mm", "Lounge");
            var drawing = RoundTrip(CadExport.Plan(document, document.Levels[0]), ".dwg");
            Assert.Contains(drawing.Entities.OfType<TextEntity>(), text => text.Value.Contains("Lounge") && text.Layer.Name == "A-AREA-IDEN");
        });
    }

    [Theory]
    [InlineData(".dwg")]
    [InlineData(".dxf")]
    public void ASheetIsWrittenAtPaperSizeItsViewsCutToTheirFrames(string extension)
    {
        OnUiThread(() =>
        {
            var (document, level) = House();
            var sheet = new Sheet { Number = "A-101", Name = "Ground Floor Plan", PaperSize = PaperSize.A3 };
            sheet.Add(new Viewport { View = ViewReference.FloorPlan(level.Id), Centre = new Point2D(120, 150), Scale = new ViewScale(100) });

            // A callout of one corner: the plan drawn again, cropped to the corner.
            var callout = new Callout { Parent = ViewReference.FloorPlan(level.Id), Name = "Corner", Region = new BoundingBox2D(-500, -500, 1500, 1500) };
            document.Add(callout);
            var detail = new Viewport { View = Callouts.Reference(callout), Centre = new Point2D(230, 230), Scale = new ViewScale(20), ShowTitle = false };
            sheet.Add(detail);
            document.Add(sheet);

            var drawing = RoundTrip(CadExport.Sheet(document, sheet), extension);
            var points = Vertices(drawing).ToList();
            Assert.All(points, p =>
            {
                Assert.InRange(p.X, -0.01, sheet.Width + 0.01);
                Assert.InRange(p.Y, -0.01, sheet.Height + 0.01);
            });

            // Something of the callout is drawn in its frame, and nothing of its plan spills out under it.
            var frame = detail.PaperBounds(document);
            Assert.Contains(points, p => p.X > frame.MinX && p.X < frame.MaxX && p.Y > frame.MinY && p.Y < frame.MaxY);
            Assert.DoesNotContain(points, p => p.X > frame.MinX + 1 && p.X < frame.MaxX - 1 && p.Y > 12 && p.Y < frame.MinY - 1);

            // The title block's words as text, the drawing number among them; the plan's walls on their layer within it.
            var texts = drawing.Entities.OfType<TextEntity>().ToList();
            Assert.Contains(texts, text => text.Value == "A-101");
            Assert.Contains(texts, text => text.Value == "Ground Floor Plan");
            Assert.Contains(drawing.Entities, entity => entity.Layer.Name.StartsWith("A-WALL"));
        });
    }
}

/// <summary>Lines drawn as open figures - a revision cloud's arcs, a section's edges - reach a DWG and a PDF, as WPF's flattening alone would leave them out.</summary>
[Collection("Wpf")]
public class OpenLineExportTests
{
    [Fact]
    public void ARevisionCloudReachesTheDwgAndAnOpenLineThePdf()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
                var revision = new Revision();
                document.Revisions.Add(revision);
                var cloud = new RevisionCloud { RevisionId = revision.Id, LevelId = document.Levels[0].Id };
                cloud.Outline.AddRange(new[] { new Point2D(1000, 1000), new Point2D(3000, 1000), new Point2D(3000, 2500), new Point2D(1000, 2500) });
                document.Add(cloud);
                var drawing = CadExport.Plan(document, document.Levels[0]);
                Assert.Contains(drawing.Entities.OfType<LwPolyline>(), polyline => polyline.Layer.Name == "A-ANNO-REVS");

                // An open line, stroked and not filled, on a page.
                var geometry = new System.Windows.Media.StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new System.Windows.Point(10, 10), false, false);
                    context.LineTo(new System.Windows.Point(150, 10), true, false);
                }

                var page = new System.Windows.Media.DrawingGroup();
                page.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 1), geometry));
                using var stream = new MemoryStream();
                PdfWriter.Write(stream, new[] { new PdfPage(200, 200, page) });
                var text = System.Text.Encoding.ASCII.GetString(stream.ToArray());
                Assert.Contains(" l\n", text.Replace("\r", string.Empty));
                Assert.Contains("\nS\n", text.Replace("\r", string.Empty));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The open lines did not go out.", failure);
    }
}
