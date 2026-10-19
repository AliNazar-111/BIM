using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Draws a <see cref="ProjectedDrawing"/> - a 3D view or an elevation flattened into lines and
/// painted faces - into any <see cref="DrawingContext"/>, under any transform: an interior
/// elevation on screen, and a viewport on a sheet, which prints as lines.
/// </summary>
public sealed class ProjectionRenderer
{
    private readonly Pen _linePen;
    private readonly Pen _levelPen;
    private readonly Pen _groundPen;
    private readonly Pen _cropPen;
    private readonly Brush _levelTextBrush;
    private readonly Dictionary<(ColourRgb, byte), Brush> _fills = new();

    private Func<double, double, Point> _toScreen = (x, y) => new Point(x, -y);

    public ProjectionRenderer(DrawingPalette? palette = null)
    {
        var ink = palette ?? AppTheme.Drawing;
        _linePen = RenderPens.Solid(ink.SectionCut, 0.7);
        _levelPen = RenderPens.Chain(ink.LevelLine, 1.0, 16, 4, 2, 4);
        _groundPen = RenderPens.Solid(ink.GroundLine, 1.0);
        _cropPen = RenderPens.Dashed(ink.SectionSeen, 0.8, 6, 3);
        _levelTextBrush = RenderPens.Fill(ink.LevelText);
    }

    public double PixelsPerPaperMm { get; set; } = PlanRenderer.ScreenPixelsPerPaperMm;

    public double PixelsPerDip { get; set; } = 1;

    /// <summary>Whether the crop round an interior elevation is drawn: on screen, not on paper.</summary>
    public bool ShowCrop { get; set; }

    /// <summary>Points the renderer at a surface: across and up the view, in its millimetres, to the surface.</summary>
    public void SetTransform(Func<double, double, Point> toScreen) => _toScreen = toScreen;

    private double Page(double pixelsAtScreenScale) => pixelsAtScreenScale * (PixelsPerPaperMm / PlanRenderer.ScreenPixelsPerPaperMm);

    /// <summary>The levels, then the faces far to near, then every edge seen.</summary>
    public void DrawContent(DrawingContext dc, ProjectedDrawing drawing, bool labelLevels = true)
    {
        DrawLevels(dc, drawing, labelLevels);

        foreach (var fill in drawing.Fills)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(At(fill.Polygon[0]), true, true);
                context.PolyLineTo(fill.Polygon.Skip(1).Select(At).ToArray(), true, false);
            }

            geometry.Freeze();

            // A solid face is edged in its own colour: faces side by side otherwise leave a hairline of what is behind between them.
            var brush = Fill(fill.Colour, fill.Opacity);
            dc.DrawGeometry(brush, fill.Opacity >= 0.99 ? Seam(brush) : null, geometry);
        }

        // Every line in one figure: a drawing has thousands, drawn far quicker together.
        var lines = new StreamGeometry();
        using (var context = lines.Open())
        {
            foreach (var line in drawing.Lines)
            {
                context.BeginFigure(At(line.From), false, false);
                context.LineTo(At(line.To), true, false);
            }
        }

        lines.Freeze();
        dc.DrawGeometry(null, _linePen, lines);

        if (ShowCrop && drawing.Crop is { } crop)
            dc.DrawRectangle(null, _cropPen, new Rect(_toScreen(crop.MinX, crop.MaxY), _toScreen(crop.MaxX, crop.MinY)));
    }

    private Point At(Core.Geometry.Point2D point) => _toScreen(point.X, point.Y);

    /// <summary>Levels across the drawing, past it either side, each named at its right-hand end.</summary>
    private void DrawLevels(DrawingContext dc, ProjectedDrawing drawing, bool labelLevels)
    {
        if (drawing.Levels.Count == 0 || drawing.Extent.IsEmpty) return;

        var span = drawing.Crop ?? drawing.Extent;
        var overhang = drawing.Crop is null ? Math.Max(1000, span.Width * 0.04) : 300;
        foreach (var level in drawing.Levels)
        {
            var left = _toScreen(span.MinX - overhang, level.Y);
            var right = _toScreen(span.MaxX + overhang, level.Y);
            dc.DrawLine(level.Elevation == 0 ? _groundPen : _levelPen, left, right);
            if (!labelLevels) continue;

            var text = new FormattedText($"{level.Name}   {Units.FormatLength(level.Elevation)}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), Math.Max(1, Page(11)), _levelTextBrush, PixelsPerDip);
            dc.DrawText(text, new Point(right.X + Page(6), right.Y - text.Height - Page(2)));
        }
    }

    private readonly Dictionary<Brush, Pen> _seams = new();

    private Pen Seam(Brush brush) => _seams.TryGetValue(brush, out var pen) ? pen : _seams[brush] = RenderPens.Freeze(new Pen(brush, 0.6) { LineJoin = PenLineJoin.Round });

    private Brush Fill(ColourRgb colour, double opacity)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        if (_fills.TryGetValue((colour, alpha), out var brush)) return brush;

        var media = RenderPens.ToMediaColor(colour);
        media.A = alpha;
        return _fills[(colour, alpha)] = RenderPens.Fill(media);
    }
}
