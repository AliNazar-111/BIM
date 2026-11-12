using System.Globalization;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>The part of a picture shown: pixels from its left and bottom edges to its right and top, all measured from its bottom left corner.</summary>
public sealed record ImageCrop(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;
}

/// <summary>
/// A picture brought onto a storey to work over: a map, an aerial photograph, a scanned survey or an old drawing. Its bottom
/// left corner is set at a point of the plan, it is turned about that point, and each of its pixels is so many millimetres -
/// set by its width, by two points on it a known distance apart, or by the world file that came with it. It is drawn under
/// everything on its storey, faded so the model stands out unless wanted as it is, and cropped to the part wanted; kept in the
/// project whole, PNG or JPEG as it came - or linked to its file, read from it as the project opens and when reloaded. Like an
/// imported drawing it is something to draw on: not built, scheduled or exported.
/// </summary>
public sealed class ImageUnderlay : Element
{
    public override BuiltInCategory Category => BuiltInCategory.RasterImages;

    /// <summary>The file it came from.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The picture, as a PNG or a JPEG file holds it - none for a linked one whose file was not found.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    /// <summary>Where on the plan its bottom left corner is, mm.</summary>
    public Point2D Location { get; set; }

    /// <summary>How far it is turned about its bottom left corner, degrees anticlockwise.</summary>
    public double Rotation { get; set; }

    /// <summary>How many millimetres one of its pixels is on the plan.</summary>
    public double Scale { get; set; } = 10;

    /// <summary>Whether it is drawn faded, so the model stands out over it; or as it is.</summary>
    public bool Halftone { get; set; } = true;

    /// <summary>Whether it is linked to its file - read from it as the project opens and when reloaded, its pixels not kept in the project - rather than kept.</summary>
    public bool Linked { get; set; }

    /// <summary>The part of it shown; none for all of it.</summary>
    public ImageCrop? Crop { get; set; }

    /// <summary>How wide the whole picture is on the plan, mm.</summary>
    public double Width => PixelWidth * Scale;

    /// <summary>How high the whole picture is on the plan, mm.</summary>
    public double Height => PixelHeight * Scale;

    /// <summary>The part of it shown, in its pixels: its crop, or all of it.</summary>
    public ImageCrop Shown => Crop ?? new ImageCrop(0, 0, PixelWidth, PixelHeight);

    /// <summary>A point of the picture - pixels from its bottom left corner, up the plan - where it is on the plan.</summary>
    public Point2D ToModel(Point2D pixel)
    {
        var turn = Rotation * Math.PI / 180;
        var (x, y) = (pixel.X * Scale, pixel.Y * Scale);
        return new Point2D(Location.X + x * Math.Cos(turn) - y * Math.Sin(turn), Location.Y + x * Math.Sin(turn) + y * Math.Cos(turn));
    }

    /// <summary>A point of the plan as a point of the picture: pixels from its bottom left corner, up the plan.</summary>
    public Point2D ToPixel(Point2D model)
    {
        var turn = Rotation * Math.PI / 180;
        var (x, y) = (model.X - Location.X, model.Y - Location.Y);
        return new Point2D((x * Math.Cos(turn) + y * Math.Sin(turn)) / Scale, (-x * Math.Sin(turn) + y * Math.Cos(turn)) / Scale);
    }

    /// <summary>The corners of the part shown on the plan, anticlockwise from the bottom left.</summary>
    public IReadOnlyList<Point2D> Corners
    {
        get
        {
            var shown = Shown;
            return new[]
            {
                ToModel(new Point2D(shown.Left, shown.Bottom)), ToModel(new Point2D(shown.Right, shown.Bottom)),
                ToModel(new Point2D(shown.Right, shown.Top)), ToModel(new Point2D(shown.Left, shown.Top))
            };
        }
    }

    public BoundingBox2D Bounds => PixelWidth > 0 && PixelHeight > 0 ? BoundingBox2D.Around(Corners) : BoundingBox2D.Empty;

    /// <summary>Whether a point of the plan is on the part of it shown.</summary>
    public bool Covers(Point2D at) => PixelWidth > 0 && PixelHeight > 0 && Polygon2D.Contains(Corners, at);

    /// <summary>A picture read from a file's bytes onto a storey, or null where they are not a PNG or a JPEG.</summary>
    public static ImageUnderlay? From(string source, byte[] data, Guid levelId)
    {
        if (Sheets.SheetImage.SizeOf(data) is not var (width, height) || width <= 0 || height <= 0) return null;
        return new ImageUnderlay { Source = source, Data = data, PixelWidth = width, PixelHeight = height, LevelId = levelId };
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(CadUnderlayParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.ReadOnly(CadUnderlayParameters.Source, () => System.IO.Path.GetFileName(Source));
        yield return ParameterValue.ReadOnly(CadUnderlayParameters.Linked, () => !Linked ? "Kept in the project as it was read"
            : Data.Length == 0 ? "Linked to its file, which was not found: only its outline is drawn"
            : "Linked to its file: read again as the project opens and when reloaded");
        yield return ParameterValue.ReadOnly(ImageUnderlayParameters.Pixels, () => $"{PixelWidth:#,0} x {PixelHeight:#,0}");
        yield return ParameterValue.BindValidated(ImageUnderlayParameters.Width, () => Width, (double v) =>
        {
            if (!double.IsFinite(v) || v <= 0 || PixelWidth <= 0) return false;
            Scale = v / PixelWidth;
            return true;
        });
        yield return ParameterValue.ReadOnly(ImageUnderlayParameters.Height, () => Height);
        yield return ParameterValue.ReadOnly(ImageUnderlayParameters.Cropped, () => Crop is { } crop
            ? $"To {crop.Width:#,0} x {crop.Height:#,0} pixels, {Units.FormatLength(crop.Width * Scale)} by {Units.FormatLength(crop.Height * Scale)}"
            : "Not cropped");
        yield return ParameterValue.BindValidated(CadUnderlayParameters.Rotation, () => Rotation, (double v) =>
        {
            if (!double.IsFinite(v)) return false;
            Rotation = (v % 360 + 360) % 360;
            return true;
        });
        yield return ParameterValue.Bind(CadUnderlayParameters.Halftone, () => Halftone, v => Halftone = v);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

/// <summary>
/// A world file: how a picture's pixels lie on a map grid - six numbers, a line each, in the order A, D, B, E, C, F, the map
/// position of the centre of a pixel at column x and row y (counted from the top left) being x1 = Ax + By + C, y1 = Dx + Ey + F.
/// A and E are a pixel's size across and down - E negative, rows running down the picture as the map's y runs up - B and D
/// turn it, and C and F are the map position of the centre of its top left pixel.
/// </summary>
public sealed record WorldFile(double A, double D, double B, double E, double C, double F)
{
    /// <summary>A world file's six numbers read from its text; none where it has not six numbers.</summary>
    public static WorldFile? Parse(string text)
    {
        var numbers = new List<double>();
        foreach (var line in text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
        {
            if (!double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return null;
            numbers.Add(value);
        }

        return numbers.Count == 6 ? new WorldFile(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]) : null;
    }

    /// <summary>Its six lines as a world file holds them.</summary>
    public override string ToString() =>
        string.Join("\r\n", new[] { A, D, B, E, C, F }.Select(value => value.ToString("0.0##########", CultureInfo.InvariantCulture))) + "\r\n";

    /// <summary>
    /// The files that may hold a picture's world file, beside it: its extension's first and third letters and a w (site.pgw,
    /// photo.jgw), its first and last and a w, its whole extension and a w (site.pngw), and site.wld.
    /// </summary>
    public static IReadOnlyList<string> NamesFor(string picture)
    {
        var extension = System.IO.Path.GetExtension(picture).TrimStart('.');
        var stem = System.IO.Path.ChangeExtension(picture, null);
        var names = new List<string>();
        if (extension.Length >= 3)
        {
            names.Add($"{stem}.{extension[0]}{extension[2]}w");
            names.Add($"{stem}.{extension[0]}{extension[^1]}w");
        }

        names.Add($"{stem}.{extension}w");
        names.Add($"{stem}.wld");
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Pictures under the plan, set to their true size: by two points, by a world file, or cropped to what is wanted.</summary>
public static class ImageUnderlays
{
    /// <summary>
    /// How much a picture is to be scaled for two points on it to be a known length apart: the length over how far apart they
    /// are on the plan now. Null where the two are the same point, or the length is none.
    /// </summary>
    public static double? Calibration(Point2D first, Point2D second, double length)
    {
        var apart = first.DistanceTo(second);
        return apart < 1e-6 || length <= 0 || !double.IsFinite(length) ? null : length / apart;
    }

    /// <summary>
    /// Where a world file puts a picture on the plan - its bottom left corner, how far it is turned and how many millimetres a
    /// pixel is - its map in metres on the grid the project is set out on, the project's origin the survey offset (mm east
    /// and north) from the grid's. Or why it cannot: a world file in degrees of latitude and longitude, or one that stretches
    /// or skews the picture, which can only be turned and scaled alike both ways.
    /// </summary>
    public static ((Point2D Location, double Rotation, double Scale)? Placed, string? Problem) Place(WorldFile world, int pixelWidth, int pixelHeight, Vector2D survey)
    {
        var size = Math.Sqrt(world.A * world.A + world.D * world.D);
        if (size <= 0 || pixelWidth <= 0 || pixelHeight <= 0) return (null, "Its pixels are of no size.");
        if (size < 0.001 && Math.Abs(world.C) <= 180 && Math.Abs(world.F) <= 90)
            return (null, "It is in degrees of latitude and longitude, not metres on a grid: the picture is to be put onto the grid the project is set out on first.");

        // Rows run down the picture: a step down a row is the step across a column turned a quarter clockwise - B = D, E = -A.
        if (Math.Abs(world.B - world.D) > size * 0.005 || Math.Abs(world.E + world.A) > size * 0.005)
            return (null, "It stretches or skews the picture, or mirrors it: a picture here can only be turned and scaled alike across and down.");

        // The picture's bottom left corner: half a pixel left and down of the centre of its bottom left pixel, column 0, row H - 1.
        var (column, row) = (-0.5, pixelHeight - 0.5);
        var corner = new Point2D((world.A * column + world.B * row + world.C) * 1000 - survey.X, (world.D * column + world.E * row + world.F) * 1000 - survey.Y);
        var turn = Math.Atan2(world.D, world.A) * 180 / Math.PI;
        return ((corner, (turn % 360 + 360) % 360, size * 1000), null);
    }

    /// <summary>The world file of a picture as it lies on the plan, in metres on the grid the project is set out on.</summary>
    public static WorldFile For(ImageUnderlay picture, Vector2D survey)
    {
        var turn = picture.Rotation * Math.PI / 180;
        var size = picture.Scale / 1000;
        var (a, d) = (size * Math.Cos(turn), size * Math.Sin(turn));
        var centre = picture.ToModel(new Point2D(0.5, picture.PixelHeight - 0.5));
        return new WorldFile(a, d, d, -a, (centre.X + survey.X) / 1000, (centre.Y + survey.Y) / 1000);
    }

    /// <summary>
    /// The part of a picture between two points of the plan, as its crop: square to the picture, inside the part already shown;
    /// none where the two points leave less than a pixel of it either way.
    /// </summary>
    public static ImageCrop? CropTo(ImageUnderlay picture, Point2D first, Point2D second)
    {
        var (a, b) = (picture.ToPixel(first), picture.ToPixel(second));
        var shown = picture.Shown;
        var crop = new ImageCrop(
            Math.Max(shown.Left, Math.Min(a.X, b.X)), Math.Max(shown.Bottom, Math.Min(a.Y, b.Y)),
            Math.Min(shown.Right, Math.Max(a.X, b.X)), Math.Min(shown.Top, Math.Max(a.Y, b.Y)));
        return crop.Width >= 1 && crop.Height >= 1 ? crop : null;
    }

    /// <summary>A picture read again from its file: its pixels and their count, its crop kept inside it where it has grown or shrunk.</summary>
    public static void Reread(ImageUnderlay picture, byte[] data)
    {
        if (Sheets.SheetImage.SizeOf(data) is not var (width, height) || width <= 0 || height <= 0) return;
        (picture.Data, picture.PixelWidth, picture.PixelHeight) = (data, width, height);
        if (picture.Crop is { } crop)
        {
            var kept = new ImageCrop(Math.Min(crop.Left, width), Math.Min(crop.Bottom, height), Math.Min(crop.Right, width), Math.Min(crop.Top, height));
            picture.Crop = kept.Width >= 1 && kept.Height >= 1 ? kept : null;
        }
    }
}

public static class ImageUnderlayParameters
{
    public static readonly ParameterDefinition Pixels = new("Pixels", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Cropped = new("Cropped", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
