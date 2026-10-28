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
