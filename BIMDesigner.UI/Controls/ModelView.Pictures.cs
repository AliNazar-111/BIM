using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using MediaPoint3D = System.Windows.Media.Media3D.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Pictures under the plans lying on their storeys in 3D - a site map, an aerial photograph round the building - each as its
/// part shown, a little under its storey so a floor there covers it as the floor covers the ground. Drawn by the view, not
/// made into the model's meshes: they are not built, so not clashed, lit by the daylight or written to IFC.
/// </summary>
public partial class ModelView
{
    /// <summary>How far under its storey a picture lies, mm: above the ground plane, under a floor whose top is at the storey.</summary>
    private const double PictureDrop = 5;

    private static readonly ConditionalWeakTable<byte[], ImageSource> PictureBitmaps = new();

    /// <summary>The pictures shown, each a textured face over its part shown - none in wireframe or in a section box, or where they are hidden.</summary>
    private void AddPictures(Model3DGroup root)
    {
        if (_document is null || _view.Style == VisualStyle.Wireframe || _view.ActiveBox is not null || _view.HiddenKinds.Contains(MeshKind.Picture)) return;
        foreach (var picture in _document.All<ImageUnderlay>())
        {
            if (picture.Data.Length == 0 || picture.PixelWidth <= 0 || picture.PixelHeight <= 0 || _view.HiddenLevels.Contains(picture.LevelId)) continue;
            if (_document.FindLevel(picture.LevelId) is not { } level || BitmapOf(picture.Data) is not { } bitmap) continue;
            if (PictureFace(picture, level.Elevation - PictureDrop, bitmap) is { } face) root.Children.Add(face);
        }
    }

    /// <summary>A picture's part shown as a face facing up at a height, its pixels laid over it as the plan lays them.</summary>
    internal static GeometryModel3D? PictureFace(ImageUnderlay picture, double height, ImageSource bitmap)
    {
        var shown = picture.Shown;
        var (width, tall) = ((double)picture.PixelWidth, (double)picture.PixelHeight);
        var corners = new[] { (shown.Left, shown.Bottom), (shown.Right, shown.Bottom), (shown.Right, shown.Top), (shown.Left, shown.Top) };
        var z = height / MillimetresPerUnit;
        var positions = new Point3DCollection();
        var places = new PointCollection();
        foreach (var (x, y) in corners)
        {
            var at = picture.ToModel(new Point2D(x, y));
            positions.Add(new MediaPoint3D(at.X / MillimetresPerUnit, at.Y / MillimetresPerUnit, z));

            // Its pixels counted down from its top, as the bitmap has them.
            places.Add(new Point(x / width, 1 - y / tall));
        }

        var geometry = new MeshGeometry3D { Positions = positions, TextureCoordinates = places, TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 } };
        geometry.Freeze();
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill, Opacity = picture.Halftone ? 0.75 : 1 };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();

        // Lit by itself, its colours as they are, as the ground plane is.
        var material = new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) } };
        material.Freeze();
        return new GeometryModel3D(geometry, material);
    }

    /// <summary>A picture's pixels as the screen draws them, read once for each picture's bytes; none where they cannot be read.</summary>
    private static ImageSource? BitmapOf(byte[] data)
    {
        try
        {
            return PictureBitmaps.GetValue(data, bytes =>
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new System.IO.MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            });
        }
        catch (Exception exception) when (exception is NotSupportedException or System.IO.IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
