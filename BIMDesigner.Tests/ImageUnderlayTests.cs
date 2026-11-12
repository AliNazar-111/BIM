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
