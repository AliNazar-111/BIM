using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A picture brought onto a storey to work over: a map, an aerial photograph, a scanned survey or an old drawing. Its bottom
/// left corner is set at a point of the plan, it is turned about that point, and each of its pixels is so many millimetres -
/// set by its width, or by two points on it a known distance apart. It is drawn under everything on its storey, faded so the
/// model stands out unless wanted as it is; kept in the project whole, PNG or JPEG as it came. Like an imported drawing it is
/// something to draw on: not built, scheduled or exported.
/// </summary>
public sealed class ImageUnderlay : Element
{
    public override BuiltInCategory Category => BuiltInCategory.RasterImages;

    /// <summary>The file it came from.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The picture, as a PNG or a JPEG file holds it.</summary>
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

    /// <summary>How wide it is on the plan, mm.</summary>
    public double Width => PixelWidth * Scale;

    /// <summary>How high it is on the plan, mm.</summary>
    public double Height => PixelHeight * Scale;

    /// <summary>A point of the picture - pixels from its bottom left corner, up the plan - where it is on the plan.</summary>
    public Point2D ToModel(Point2D pixel)
    {
        var turn = Rotation * Math.PI / 180;
        var (x, y) = (pixel.X * Scale, pixel.Y * Scale);
        return new Point2D(Location.X + x * Math.Cos(turn) - y * Math.Sin(turn), Location.Y + x * Math.Sin(turn) + y * Math.Cos(turn));
    }

    /// <summary>Its corners on the plan, anticlockwise from the bottom left.</summary>
    public IReadOnlyList<Point2D> Corners => new[]
    {
        ToModel(new Point2D(0, 0)), ToModel(new Point2D(PixelWidth, 0)), ToModel(new Point2D(PixelWidth, PixelHeight)), ToModel(new Point2D(0, PixelHeight))
    };

    public BoundingBox2D Bounds => PixelWidth > 0 && PixelHeight > 0 ? BoundingBox2D.Around(Corners) : BoundingBox2D.Empty;

    /// <summary>Whether a point of the plan is on it.</summary>
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
        yield return ParameterValue.ReadOnly(ImageUnderlayParameters.Pixels, () => $"{PixelWidth:#,0} x {PixelHeight:#,0}");
        yield return ParameterValue.BindValidated(ImageUnderlayParameters.Width, () => Width, (double v) =>
        {
            if (!double.IsFinite(v) || v <= 0 || PixelWidth <= 0) return false;
            Scale = v / PixelWidth;
            return true;
        });
        yield return ParameterValue.ReadOnly(ImageUnderlayParameters.Height, () => Height);
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

/// <summary>Pictures under the plan, set to their true size.</summary>
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
}

public static class ImageUnderlayParameters
{
    public static readonly ParameterDefinition Pixels = new("Pixels", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
