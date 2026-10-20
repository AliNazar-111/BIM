using System.IO;
using System.Windows;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;
using BIMDesigner.UI.Export;
using netDxf;

namespace BIMDesigner.Tests;

/// <summary>Plans and sheets written as DXF, and read back as a CAD package would read them.</summary>
[Collection("Wpf")]
public class DxfExportTests
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
    private static DxfDocument RoundTrip(DxfDocument dxf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dxf-{Guid.NewGuid():N}.dxf");
        try
        {
            DxfExport.Save(dxf, path);
            var text = File.ReadAllText(path);
            Assert.Contains("SECTION", text);
            Assert.EndsWith("EOF", text.TrimEnd());
            return DxfDocument.Load(path) ?? throw new Exception("The DXF did not read back.");
        }
        finally { File.Delete(path); }
    }

    private static IEnumerable<Vector2> Vertices(DxfDocument dxf) =>
        dxf.Entities.Polylines2D.SelectMany(polyline => polyline.Vertexes.Select(vertex => vertex.Position));

    [Fact]
    public void APlanIsWrittenAtFullSizeInMillimetresItsNotesTheSizeTheyPrint()
    {
        OnUiThread(() =>
        {
            var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
            var level = document.Levels[0];
            document.Add(new TextNote { Text = "Kitchen", Position = new Point2D(3000, 4000), LevelId = level.Id });

            var dxf = RoundTrip(DxfExport.Plan(document, level));
            Assert.Equal(netDxf.Units.DrawingUnits.Millimeters, dxf.DrawingVariables.InsUnits);

            // The walls' faces at their real positions: the house 10 by 8 m, its walls round it.
            var points = Vertices(dxf).ToList();
            Assert.NotEmpty(points);
            Assert.InRange(points.Min(p => p.X), -400, 0);
            Assert.InRange(points.Max(p => p.X), 10000, 10400);
            Assert.InRange(points.Min(p => p.Y), -400, 0);
            Assert.InRange(points.Max(p => p.Y), 8000, 8400);

            // The walls' layers filled, on fill layers of their own.
            Assert.NotEmpty(dxf.Entities.Hatches);
            Assert.All(dxf.Entities.Hatches, hatch => Assert.EndsWith("-FILL", hatch.Layer.Name));
            Assert.Contains(dxf.Layers, layer => layer.Name.StartsWith("BIM-") && !layer.Name.EndsWith("-FILL"));

            // The note as text, where it was put, and as big as 12 points of it are at 1:100.
            var note = Assert.Single(dxf.Entities.Texts, text => text.Value == "Kitchen");
            Assert.InRange(note.Position.X, 2900, 3200);
            Assert.InRange(note.Position.Y, 3700, 4200);
            Assert.InRange(note.Height, 100, 400);
        });
    }

    [Fact]
    public void ASheetIsWrittenAtPaperSizeItsViewsCutToTheirFrames()
    {
        OnUiThread(() =>
        {
            var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
            var level = document.Levels[0];
            var sheet = new Sheet { Number = "A-101", Name = "Ground Floor Plan", PaperSize = PaperSize.A3 };
            sheet.Add(new Viewport { View = ViewReference.FloorPlan(level.Id), Centre = new Point2D(120, 150), Scale = new ViewScale(100) });

            // A callout of one corner: the plan drawn again, cropped to the corner.
            var callout = new Callout { Parent = ViewReference.FloorPlan(level.Id), Name = "Corner", Region = new BoundingBox2D(-500, -500, 1500, 1500) };
            document.Add(callout);
            var detail = new Viewport { View = Callouts.Reference(callout), Centre = new Point2D(230, 230), Scale = new ViewScale(20), ShowTitle = false };
            sheet.Add(detail);
            document.Add(sheet);

            var dxf = RoundTrip(DxfExport.Sheet(document, sheet));
            var points = Vertices(dxf).ToList();
            Assert.All(points, p =>
            {
                Assert.InRange(p.X, -0.01, sheet.Width + 0.01);
                Assert.InRange(p.Y, -0.01, sheet.Height + 0.01);
            });

            // Something of the callout is drawn in its frame, and nothing of its plan spills out under it.
            var frame = detail.PaperBounds(document);
            Assert.Contains(points, p => p.X > frame.MinX && p.X < frame.MaxX && p.Y > frame.MinY && p.Y < frame.MaxY);
            Assert.DoesNotContain(points, p => p.X > frame.MinX + 1 && p.X < frame.MaxX - 1 && p.Y > 12 && p.Y < frame.MinY - 1);

            // The title block's words as text: the drawing number among them.
            Assert.Contains(dxf.Entities.Texts, text => text.Value == "A-101");
            Assert.Contains(dxf.Entities.Texts, text => text.Value == "Ground Floor Plan");
        });
    }
}
