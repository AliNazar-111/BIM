using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Drawings imported from DWG or DXF, drawn under the model on their storey: their lines a hair's
/// breadth thick whatever the zoom, faded to grey or in their own colours - AutoCAD's white or black
/// as the plan's own ink, so it shows on a dark screen and on paper - their text where it is big
/// enough to read. Each layer of one is kept apart, so a DWG made of the plan carries it on its
/// own layers, prefixed X-.
/// </summary>
public sealed partial class PlanRenderer
{
    /// <summary>The grey a faded drawing is drawn in: read on paper and on a dark screen alike.</summary>
    private static readonly Color UnderlayGrey = Color.FromRgb(0x9A, 0xA0, 0xA6);

    /// <summary>The smallest text, on the screen, worth drawing: below it a line of text is a smudge.</summary>
    private const double SmallestText = 3;

    /// <summary>Each imported drawing's lines, layer by layer and colour by colour, in its own units: built once, drawn through a transform.</summary>
    private static readonly ConditionalWeakTable<CadDrawing, List<(int Layer, ColourRgb? Colour, StreamGeometry Lines)>> UnderlayLines = new();

    /// <summary>Each picture's bitmap, read once from its bytes.</summary>
    private static readonly ConditionalWeakTable<byte[], System.Windows.Media.Imaging.BitmapSource> ImageBitmaps = new();

    /// <summary>How much of a faded picture shows: enough to trace over, little enough for the model to stand out.</summary>
    private const double ImageFade = 0.45;

    private void DrawImages(DrawingContext dc)
    {
        foreach (var picture in OnActiveLevel<ImageUnderlay>()) Styled(dc, picture, d => DrawImage(d, picture));
    }

    /// <summary>
    /// A picture where it is put, turned and scaled: its top left pixel at its top left corner on the plan, its pixels along and
    /// down it as the plan has them - faded, unless wanted as it is - and its edge, which it is picked by, where it is selected.
    /// </summary>
    private void DrawImage(DrawingContext dc, ImageUnderlay picture)
    {
        if (picture.PixelWidth <= 0 || picture.PixelHeight <= 0 || picture.Data.Length == 0) return;
        System.Windows.Media.Imaging.BitmapSource bitmap;
        try
        {
            bitmap = ImageBitmaps.GetValue(picture.Data, data =>
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.StreamSource = new System.IO.MemoryStream(data);
                image.EndInit();
                image.Freeze();
                return image;
            });
        }
        catch (Exception exception) when (exception is NotSupportedException or System.IO.IOException or InvalidOperationException or ArgumentException)
        {
            return;
        }

        var (width, height) = ((double)picture.PixelWidth, (double)picture.PixelHeight);
        var topLeft = ModelToScreen(picture.ToModel(new Point2D(0, height)));
        var topRight = ModelToScreen(picture.ToModel(new Point2D(width, height)));
        var bottomLeft = ModelToScreen(picture.ToModel(new Point2D(0, 0)));
        var along = (topRight - topLeft) / width;
        var down = (bottomLeft - topLeft) / height;
        dc.PushTransform(new MatrixTransform(along.X, along.Y, down.X, down.Y, topLeft.X, topLeft.Y));
        dc.PushOpacity(picture.Halftone ? ImageFade : 1);
        dc.DrawImage(bitmap, new Rect(0, 0, width, height));
        dc.Pop();
        dc.Pop();

        if (IsSelected(picture))
        {
            var edge = new StreamGeometry();
            using (var context = edge.Open())
            {
                var corners = picture.Corners.Select(ModelToScreen).ToList();
                context.BeginFigure(corners[0], false, true);
                foreach (var corner in corners.Skip(1)) context.LineTo(corner, true, false);
            }

            edge.Freeze();
            dc.DrawGeometry(null, _selectedPen, edge);
        }
    }

    private void DrawUnderlays(DrawingContext dc)
    {
        foreach (var underlay in OnActiveLevel<CadUnderlay>()) Styled(dc, underlay, d => DrawUnderlay(d, underlay));
    }

    private void DrawUnderlay(DrawingContext dc, CadUnderlay underlay)
    {
        var drawing = underlay.Drawing;
        if (drawing.Paths.Count == 0 && drawing.Labels.Count == 0) return;

        // The drawing's own units to the screen: placed, turned and scaled on the plan, then as the plan is drawn.
        var origin = ModelToScreen(underlay.ToModel(new Point2D(0, 0)));
        var across = ModelToScreen(underlay.ToModel(new Point2D(1, 0))) - origin;
        var up = ModelToScreen(underlay.ToModel(new Point2D(0, 1))) - origin;
        var matrix = new Matrix(across.X, across.Y, up.X, up.Y, origin.X, origin.Y);
        var scale = Math.Sqrt(Math.Abs(matrix.Determinant));
        if (!(scale > 0) || !double.IsFinite(scale)) return;

        var selected = IsSelected(underlay);
        var transform = new MatrixTransform(matrix);
        transform.Freeze();
        dc.PushTransform(transform);
        foreach (var (layer, colour, lines) in LinesOf(drawing))
        {
            if (!underlay.Shows(layer)) continue;
            var pen = new Pen(RenderPens.Fill(InkOf(underlay, layer, colour, selected)), (selected ? 1.4 : 0.8) / scale);
            pen.Freeze();
            var group = new DrawingGroup();
            using (var inner = group.Open()) inner.DrawGeometry(null, pen, lines);
            group.SetValue(CadLayers.LayerProperty, "X-" + LayerName(drawing, layer));
            dc.DrawDrawing(group);
        }

        dc.Pop();

        // Its text, the right way up on the screen, turned as it is in the drawing and as the drawing is.
        var turn = Math.Atan2(across.Y, across.X) * 180 / Math.PI;
        foreach (var label in drawing.Labels)
        {
            var height = label.Height * scale;
            if (height < SmallestText || !underlay.Shows(label.Layer)) continue;
            var at = matrix.Transform(new Point(label.At.X, label.At.Y));
            var text = Text(label.Text, height / 0.7, RenderPens.Fill(InkOf(underlay, label.Layer, label.Colour, selected)));
            var angle = turn - label.Rotation;
            var group = new DrawingGroup();
            using (var inner = group.Open())
            {
                inner.PushTransform(new RotateTransform(angle, at.X, at.Y));
                inner.DrawText(text, new Point(at.X, at.Y - text.Baseline));
                inner.Pop();
            }

            group.SetValue(CadLayers.LayerProperty, "X-" + LayerName(drawing, label.Layer));
            dc.DrawDrawing(group);
        }
    }

    private static string LayerName(CadDrawing drawing, int layer) => layer >= 0 && layer < drawing.Layers.Count ? drawing.Layers[layer].Name : "0";

    /// <summary>
    /// The colour a line of an imported drawing is drawn in: grey where it is faded, the selection's
    /// where it is picked, otherwise its own or its layer's - white or black taken as the plan's ink.
    /// </summary>
    private Color InkOf(CadUnderlay underlay, int layer, ColourRgb? own, bool selected)
    {
        if (selected) return _ink.Selected;
        if (underlay.Halftone) return UnderlayGrey;
        var colour = own ?? (layer >= 0 && layer < underlay.Drawing.Layers.Count ? underlay.Drawing.Layers[layer].Colour : new ColourRgb(0, 0, 0));
        var whiteOrBlack = colour is { R: > 0xF0, G: > 0xF0, B: > 0xF0 } or { R: < 0x10, G: < 0x10, B: < 0x10 };
        return whiteOrBlack ? _ink.WallOutline : Color.FromRgb(colour.R, colour.G, colour.B);
    }

    /// <summary>An imported drawing's lines as geometry, a piece for each layer and colour, made the first time it is drawn.</summary>
    private static List<(int Layer, ColourRgb? Colour, StreamGeometry Lines)> LinesOf(CadDrawing drawing) =>
        UnderlayLines.GetValue(drawing, made =>
            made.Paths.GroupBy(path => (path.Layer, path.Colour)).Select(group =>
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                    foreach (var path in group)
                    {
                        context.BeginFigure(new Point(path.Points[0].X, path.Points[0].Y), false, path.Closed);
                        context.PolyLineTo(path.Points.Skip(1).Select(point => new Point(point.X, point.Y)).ToList(), true, false);
                    }

                geometry.Freeze();
                return (group.Key.Layer, group.Key.Colour, geometry);
            }).ToList());
}
