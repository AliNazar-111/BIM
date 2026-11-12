using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>A picture under a plan: read from its file, placed, turned and scaled, set to its true size by two points, kept with the project, and taken as the map under a location plan.</summary>
public class ImageUnderlayTests
{
    /// <summary>A PNG's signature and the start of its header: what its size is read from.</summary>
    internal static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        BitConverter.GetBytes(width).Reverse().ToArray().CopyTo(bytes, 16);
        BitConverter.GetBytes(height).Reverse().ToArray().CopyTo(bytes, 20);
        return bytes;
    }

    private static (BimDocument Document, ImageUnderlay Picture) Placed()
    {
        var document = BimDocument.CreateDefault();
        var picture = ImageUnderlay.From("site.png", Png(2000, 1000), document.Levels[0].Id)!;
        document.Add(picture);
        return (document, picture);
    }

    [Fact]
    public void APictureIsPlacedTurnedAndScaledAboutItsCorner()
    {
        var (_, picture) = Placed();
        Assert.Equal((2000, 1000), (picture.PixelWidth, picture.PixelHeight));
        Assert.Null(ImageUnderlay.From("notes.txt", new byte[] { 1, 2, 3, 4 }, Guid.Empty));
        Assert.Equal(BuiltInCategory.RasterImages, picture.Category);

        // 10 mm a pixel to start: 20 by 10 m from its bottom left corner.
        Assert.Equal((20_000.0, 10_000.0), (picture.Width, picture.Height));
        Assert.Equal(new Point2D(20_000, 10_000), picture.Corners[2]);
        Assert.True(picture.Covers(new Point2D(5000, 5000)));

        // Moved, turned a quarter about the origin, then halved about it: its top right corner where each puts it.
        ElementTransforms.Move(picture, new Vector2D(1000, 0));
        ElementTransforms.Rotate(picture, new Point2D(0, 0), 90);
        Assert.Equal(90, picture.Rotation, 9);
        Assert.Equal(new Point2D(0, 1000), new Point2D(Math.Round(picture.Location.X, 6), Math.Round(picture.Location.Y, 6)));
        ElementTransforms.Scale(picture, new Point2D(0, 0), 0.5);
        Assert.Equal(5, picture.Scale, 9);
        var top = picture.Corners[2];
        // Its corner halved to (0, 500); 10 by 5 m now, turned a quarter: (0, 500) + (-5000, 10000).
        Assert.Equal(new Point2D(0, 500), new Point2D(Math.Round(picture.Location.X, 6), Math.Round(picture.Location.Y, 6)));
        Assert.Equal((-5000.0, 10500.0), (Math.Round(top.X, 6), Math.Round(top.Y, 6)));
    }

    [Fact]
    public void TwoPointsAKnownDistanceApartSetItsTrueSize()
    {
        // Its scale bar's ends 500 mm apart as placed, 25 m in truth: 50 times bigger, half a metre a pixel.
        Assert.Equal(50, ImageUnderlays.Calibration(new Point2D(100, 100), new Point2D(600, 100), 25_000)!.Value, 9);
        Assert.Null(ImageUnderlays.Calibration(new Point2D(100, 100), new Point2D(100, 100), 25_000));
        Assert.Null(ImageUnderlays.Calibration(new Point2D(0, 0), new Point2D(1, 0), 0));

        // Its width typed sets its scale too.
        var (document, picture) = Placed();
        var width = picture.GetInstanceParameters(document).Single(parameter => parameter.Definition == ImageUnderlayParameters.Width);
        Assert.True(width.TrySet(50_000.0));
        Assert.Equal(25, picture.Scale, 9);
    }

    [Fact]
    public void APictureIsKeptWithTheProjectAndCopied()
    {
        var (document, picture) = Placed();
        (picture.Location, picture.Rotation, picture.Scale, picture.Halftone) = (new Point2D(-3000, 2500), 15, 12.5, false);
        var back = ProjectFile.FromJson(ProjectFile.ToJson(document)).All<ImageUnderlay>().Single();
        Assert.Equal(picture.Data, back.Data);
        Assert.Equal((2000, 1000, -3000.0, 2500.0, 15.0, 12.5, false), (back.PixelWidth, back.PixelHeight, back.Location.X, back.Location.Y, back.Rotation, back.Scale, back.Halftone));

        var copy = (ImageUnderlay)ElementCopy.Clone(picture)!;
        Assert.Equal((picture.Scale, picture.Rotation, picture.Location), (copy.Scale, copy.Rotation, copy.Location));
        Assert.Same(picture.Data, copy.Data);
    }

    /// <summary>A world file for a picture whose bottom left corner is at (east, north) on the grid, turned so many degrees, so many metres a pixel, this many pixels high.</summary>
    private static WorldFile World(double east, double north, double degrees, double size, int high)
    {
        var turn = degrees * Math.PI / 180;
        var (a, d) = (size * Math.Cos(turn), size * Math.Sin(turn));
        var (b, e) = (d, -a);
        // The top left corner is the bottom left one up the picture's height; the top left pixel's centre half a pixel across and down from it.
        var (left, top) = (east - size * Math.Sin(turn) * high, north + size * Math.Cos(turn) * high);
        return new WorldFile(a, d, b, e, left + (a + b) / 2, top + (d + e) / 2);
    }

    [Fact]
    public void AWorldFilePutsAPictureWhereTheGridSays()
    {
        // Half a metre a pixel, its top left pixel's centre at E 530000.25 N 180049.75: its top left corner at 530000, 180050, its bottom left
        // 100 pixels (50 m) below, at 530000, 180000 - the project's origin, its survey point being there. A pixel is 500 mm, not turned.
        var world = WorldFile.Parse("0.5\r\n0\r\n0\r\n-0.5\r\n530000.25\r\n180049.75\r\n")!;
        Assert.Equal(new WorldFile(0.5, 0, 0, -0.5, 530000.25, 180049.75), world);
        var (placed, problem) = ImageUnderlays.Place(world, 200, 100, new Vector2D(530_000_000, 180_000_000));
        Assert.Null(problem);
        Assert.Equal((new Point2D(0, 0), 0.0, 500.0), (Round(placed!.Value.Location), placed.Value.Rotation, placed.Value.Scale));

        // The survey point 10 m west and 5 m south of that corner: the corner 10 m east and 5 m north of the origin.
        Assert.Equal(new Point2D(10_000, 5_000), Round(ImageUnderlays.Place(world, 200, 100, new Vector2D(529_990_000, 179_995_000)).Placed!.Value.Location));

        // Turned 30 degrees: its corner where it was made to be, turned 30 degrees, and written back as the same six numbers.
        var turned = World(530_000, 180_000, 30, 0.5, 100);
        var (at, rotation, scale) = ImageUnderlays.Place(turned, 200, 100, new Vector2D(530_000_000, 180_000_000)).Placed!.Value;
        Assert.Equal((new Point2D(0, 0), 30.0, 500.0), (Round(at), Math.Round(rotation, 6), Math.Round(scale, 6)));
        var picture = new ImageUnderlay { PixelWidth = 200, PixelHeight = 100, Location = at, Rotation = rotation, Scale = scale };
        var back = WorldFile.Parse(ImageUnderlays.For(picture, new Vector2D(530_000_000, 180_000_000)).ToString())!;
        Assert.All(new[] { back.A - turned.A, back.D - turned.D, back.B - turned.B, back.E - turned.E, back.C - turned.C, back.F - turned.F }, off => Assert.InRange(off, -1e-6, 1e-6));

        // Skewed, mirrored, in degrees, or not six numbers: not taken.
        Assert.Contains("skews", ImageUnderlays.Place(world with { B = 0.1 }, 200, 100, default).Problem);
        Assert.Contains("mirrors", ImageUnderlays.Place(world with { E = 0.5 }, 200, 100, default).Problem);
        Assert.Contains("degrees", ImageUnderlays.Place(new WorldFile(0.00001, 0, 0, -0.00001, -0.12, 51.5), 200, 100, default).Problem);
        Assert.Null(WorldFile.Parse("0.5\n0\n0\n-0.5\n530000.25\n"));
        Assert.Equal(1.5e-3, WorldFile.Parse("1.5E-3\n0\n0\n-1.5E-3\n\n1\n2\n")!.A);

        // Beside site.png: site.pgw, site.pngw or site.wld; beside photo.jpeg, photo.jgw among them.
        Assert.Equal(new[] { "site.pgw", "site.pngw", "site.wld" }, WorldFile.NamesFor(@"C:\maps\site.png").Select(Path.GetFileName));
        Assert.Contains("photo.jgw", WorldFile.NamesFor(@"C:\maps\photo.jpeg").Select(Path.GetFileName));
    }

    private static Point2D Round(Point2D point) => new(Math.Round(point.X, 3), Math.Round(point.Y, 3));

    [Fact]
    public void APictureIsCroppedAndKeepsItsCropWithTheProject()
    {
        var (document, picture) = Placed();

        // Points at (2000, 1000) and (6000, 4000) mm, 10 mm a pixel: pixels 200 to 600 across and 100 to 400 up.
        picture.Crop = ImageUnderlays.CropTo(picture, new Point2D(6000, 4000), new Point2D(2000, 1000));
        Assert.Equal(new ImageCrop(200, 100, 600, 400), picture.Crop);
        Assert.Equal(new[] { new Point2D(2000, 1000), new Point2D(6000, 1000), new Point2D(6000, 4000), new Point2D(2000, 4000) }, picture.Corners);

        // Cropped again, it can only lose more; nothing between two points, no crop.
        Assert.Equal(new ImageCrop(200, 100, 400, 300), ImageUnderlays.CropTo(picture, new Point2D(0, 0), new Point2D(4000, 3000)));
        Assert.Null(ImageUnderlays.CropTo(picture, new Point2D(3000, 2000), new Point2D(3005, 2500)));

        // Turned a quarter, a point of the plan back to the picture's pixel it is.
        picture.Rotation = 90;
        Assert.Equal(new Point2D(300, 200), Round(picture.ToPixel(picture.ToModel(new Point2D(300, 200)))));

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document)).All<ImageUnderlay>().Single();
        Assert.Equal(picture.Crop, back.Crop);
        Assert.Equal(picture.Crop, ((ImageUnderlay)ElementCopy.Clone(picture)!).Crop);
    }

    [Fact]
    public void ALinkedPictureKeepsNoPixelsAndIsReadFromItsFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pictures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "aerial.png");
            File.WriteAllBytes(file, Png(2000, 1000));
            var document = BimDocument.CreateDefault();
            var picture = ImageUnderlay.From(file, File.ReadAllBytes(file), document.Levels[0].Id)!;
            (picture.Linked, picture.Crop) = (true, new ImageCrop(1500, 0, 2000, 800));
            document.Add(picture);

            // Saved: no pixels in the project; opened, read from its file.
            var json = ProjectFile.ToJson(document);
            using (var parsed = System.Text.Json.JsonDocument.Parse(json))
            {
                var saved = parsed.RootElement.EnumerateObject().Single(property => property.Name.Equals("ImageUnderlays", StringComparison.OrdinalIgnoreCase)).Value[0];
                var data = saved.EnumerateObject().FirstOrDefault(property => property.Name.Equals("Data", StringComparison.OrdinalIgnoreCase));
                Assert.True(data.Value.ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null);
            }

            Assert.Empty(ProjectFile.FromJson(json).All<ImageUnderlay>().Single().Data);
            var project = Path.Combine(folder, "site.bimx");
            ProjectFile.Save(document, project);
            var opened = ProjectFile.Load(project).All<ImageUnderlay>().Single();
            Assert.True(opened.Linked);
            Assert.Equal(picture.Data, opened.Data);

            // Its file made smaller, 1600 by 600: opened again, its crop kept inside it.
            File.WriteAllBytes(file, Png(1600, 600));
            var again = ProjectFile.Load(project).All<ImageUnderlay>().Single();
            Assert.Equal((1600, 600), (again.PixelWidth, again.PixelHeight));
            Assert.Equal(new ImageCrop(1500, 0, 1600, 600), again.Crop);

            // Gone: only where it was, its outline as it was saved, is kept.
            File.Delete(file);
            var gone = ProjectFile.Load(project).All<ImageUnderlay>().Single();
            Assert.Empty(gone.Data);
            Assert.Equal((2000, 1000), (gone.PixelWidth, gone.PixelHeight));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void APictureUnderTheLocationPlanIsItsMap()
    {
        // A plot edged in red, its location plan made: no map under it, said - until a picture of the map is put under it.
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var site = new PropertyLine { LevelId = level, Planning = PlanningEdge.ApplicationSite };
        site.SetBoundary(new[] { new Point2D(-6000, -10000), new Point2D(16000, -10000), new Point2D(16000, 14000), new Point2D(-6000, 14000) });
        document.Add(site);
        new UndoStack().Execute(PlanningDrawings.Make(document, 1250, 500, "© Crown copyright and database rights 2026 OS AC0000000000"));
        Assert.Contains(PlanningDrawings.Checks(document), check => check.Met == false && check.Said.StartsWith("No map under the location plan"));

        var picture = ImageUnderlay.From("os-map.png", Png(4000, 4000), level)!;
        (picture.Location, picture.Scale) = (new Point2D(-200_000, -200_000), 100);
        document.Add(picture);
        Assert.Contains(PlanningDrawings.Checks(document), check => check.Met == true && check.Said.Contains("os-map.png"));
    }
}
