using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp.Entities;
using ACadSharp.IO;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Export;
using Viewport = BIMDesigner.Core.Sheets.Viewport;

namespace BIMDesigner.Tests;

/// <summary>
/// Pictures on sheets: a rendering, a photograph or a logo put on a sheet, laid out clear of the views,
/// kept in the project whole, and printed into the PDF and written beside a DWG or DXF.
/// </summary>
[Collection("Wpf")]
public class SheetImageTests
{
    private static T OnUiThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                result = work();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("It failed on the UI thread.", failure);
        return result;
    }

    /// <summary>A picture of so many pixels, a gradient, as a PNG - or a JPEG; half see-through where asked.</summary>
    private static byte[] Picture(int width, int height, bool jpeg = false, bool clear = false)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(x * 255 / width), (byte)(y * 255 / height), 128, clear && x < width / 2 ? (byte)100 : (byte)255);
            }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A sheet with a plan on it.</summary>
    private static (BimDocument Document, Sheet Sheet) Sheeted()
    {
        var document = BimDocument.CreateDefault();
        var sheet = new Sheet { Number = "A-901", Name = "Presentation", PaperSize = PaperSize.A3 };
        sheet.Add(new Viewport { View = new ViewReference(ViewKind.FloorPlan, document.Levels[0].Id, string.Empty), Centre = new Point2D(120, 150), Scale = new ViewScale(100) });
        document.Add(sheet);
        return (document, sheet);
    }

    [Fact]
    public void APicturesSizeIsReadOffItsHeader()
    {
        Assert.Equal((320, 180), SheetImage.SizeOf(Picture(320, 180))!.Value);
        Assert.Equal((64, 48), SheetImage.SizeOf(Picture(64, 48, jpeg: true))!.Value);
        Assert.Equal("jpeg", SheetImage.KindOf(Picture(8, 8, jpeg: true)));
        Assert.Null(SheetImage.From("notes.txt", Encoding.ASCII.GetBytes("not a picture at all")));

        // Its height follows its proportions; its title is its file's name without the extension.
        var image = SheetImage.From("Front Elevation.png", Picture(400, 200))!;
        image.Width = 120;
        Assert.Equal(60, image.Height, 9);
        Assert.Equal("Front Elevation", image.TitleShown);
    }

    [Fact]
    public void APictureIsLaidOutClearOfTheViewsAndKeptWithTheProject()
    {
        var (document, sheet) = Sheeted();
        var image = SheetImage.From("Rendering.png", Picture(160, 90))!;
        image.Width = 120;
        image.Centre = sheet.NextFreePosition(document, image.Width, image.Height, image.ShowTitle, null, image);
        new AddSheetImageCommand(sheet, image).Redo();
        Assert.True(sheet.IsWellPlaced(document, image));
        Assert.True(sheet.IsWellPlaced(document, sheet.Viewports[0]));

        // Dragged onto the plan, it slides along it rather than over it; dragged off the paper, it stays on the drawing area.
        var onto = sheet.ConstrainMove(document, image, sheet.Viewports[0].Centre);
        Assert.False(image.Footprint is var _ && Overlaps(Moved(image, onto).Footprint, Sheet.Footprint(document, sheet.Viewports[0])));
        var off = sheet.ConstrainMove(document, image, new Point2D(-500, -500));
        Assert.True(Moved(image, off).PaperBounds.MinX >= sheet.DrawingArea.MinX - 1e-6);

        // A view placed after it goes round it.
        var next = sheet.NextFreePosition(document, 100, 80);
        Assert.False(Overlaps(new BoundingBox2D(next.X - 50, next.Y - 40 - Viewport.TitleBand, next.X + 50, next.Y + 40), image.Footprint));

        // Saved and opened again, whole; taken off and put back where it was.
        var path = Path.Combine(Path.GetTempPath(), $"picture-{Guid.NewGuid():N}.bimx");
        try
        {
            image.Title = "The scheme from the south";
            ProjectFile.Save(document, path);
            var kept = ProjectFile.Load(path).All<Sheet>().Single().Images.Single();
            Assert.Equal(image.Data, kept.Data);
            Assert.Equal((image.Centre, image.Width, "The scheme from the south", 160, 90), (kept.Centre, kept.Width, kept.TitleShown, kept.PixelWidth, kept.PixelHeight));
        }
        finally
        {
            File.Delete(path);
        }

        var remove = new RemoveSheetImageCommand(sheet, image);
        remove.Redo();
        Assert.Empty(sheet.Images);
        remove.Undo();
        Assert.Same(image, Assert.Single(sheet.Images));
    }

    private static SheetImage Moved(SheetImage image, Point2D centre)
    {
        var copy = image.Copy();
        copy.Centre = centre;
        return copy;
    }

    private static bool Overlaps(BoundingBox2D a, BoundingBox2D b) =>
        a.MinX < b.MaxX - 1e-6 && a.MaxX > b.MinX + 1e-6 && a.MinY < b.MaxY - 1e-6 && a.MaxY > b.MinY + 1e-6;

    [Fact]
    public void APictureIsPrintedIntoThePdfWithItsTransparency()
    {
        var (document, sheet) = Sheeted();
        var image = SheetImage.From("Logo.png", Picture(40, 20, clear: true))!;
        image.Width = 80;
        image.Centre = new Point2D(330, 200);
        sheet.AddImage(image);

        var pdf = Encoding.Latin1.GetString(OnUiThread(() =>
        {
            using var stream = new MemoryStream();
            PdfWriter.Write(stream, new[] { SheetExport.Page(document, sheet) });
            return stream.ToArray();
        }));

        // An image object of its pixels, deflated, its see-through half as a soft mask; drawn by name in the page.
        Assert.Contains("/Subtype /Image /Width 40 /Height 20 /ColorSpace /DeviceRGB", pdf);
        Assert.Contains("/ColorSpace /DeviceGray", pdf);
        Assert.Contains("/SMask", pdf);
        Assert.Contains("/XObject << /Im0", pdf);
        Assert.Contains("/Im0 Do", pdf);

        // Its rectangle 80 mm wide on the page, in points.
        var width = (80 * PdfWriter.PointsPerMillimetre).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"{width} 0 0 -", pdf);
    }

    [Fact]
    public void APictureIsWrittenBesideADxfAndReferredToByIt()
    {
        var (document, sheet) = Sheeted();
        var image = SheetImage.From("Rendering.png", Picture(64, 32))!;
        image.Width = 100;
        image.Centre = new Point2D(330, 200);
        sheet.AddImage(image);

        var folder = Path.Combine(Path.GetTempPath(), $"cad-pictures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "A-901.dxf");
            OnUiThread(() =>
            {
                CadExport.Save(CadExport.Sheet(document, sheet), path);
                return 0;
            });

            var beside = Path.Combine(folder, "A-901 picture 1.png");
            Assert.True(File.Exists(beside));
            Assert.Equal((64, 32), SheetImage.SizeOf(File.ReadAllBytes(beside))!.Value);

            var drawing = DxfReader.Read(path);
            var raster = Assert.Single(drawing.Entities.OfType<RasterImage>());
            Assert.Equal("A-901 picture 1.png", raster.Definition.FileName);
            Assert.Equal("G-ANNO-IMAG", raster.Layer.Name);

            // Its pixels 100 mm across on the paper, its corner at the picture's bottom-left.
            Assert.Equal(100.0 / 64, raster.UVector.X, 6);
            Assert.Equal(image.PaperBounds.MinX, raster.InsertPoint.X, 3);
            Assert.Equal(image.PaperBounds.MinY, raster.InsertPoint.Y, 3);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
