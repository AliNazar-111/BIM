using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Draws a <see cref="SectionDrawing"/> into any <see cref="DrawingContext"/>, under any
/// transform - the section editor on screen, and a viewport on a sheet.
///
/// The cut itself is worked out in Core; this only decides how it looks. Splitting it that
/// way is what lets the same section be drawn on screen, on paper and, later, into a PDF
/// without three implementations disagreeing about where a floor slab sits.
/// </summary>
public sealed class SectionRenderer
{
    private readonly Pen _cutPen;
    private readonly Pen _seenPen;
    private readonly Pen _selectedPen;
    private readonly Pen _levelPen;
    private readonly Pen _groundPen;
    private readonly Brush _levelTextBrush;

    private readonly Dictionary<ColourRgb, Brush> _fills = new();
    private readonly Dictionary<ColourRgb, Brush> _fadedFills = new();

    private Func<double, double, Point> _toScreen = (along, elevation) => new Point(along, -elevation);

    public SectionRenderer(DrawingPalette? palette = null)
    {
        var ink = palette ?? AppTheme.Drawing;

        // Cut heavy, seen light. That weight difference is the whole grammar of a section: it
        // is how a reader tells what the knife went through from what is behind it.
        _cutPen = RenderPens.Solid(ink.SectionCut, 1.4);
        _seenPen = RenderPens.Solid(ink.SectionSeen, 0.8);
        _selectedPen = RenderPens.Solid(ink.Selected, 2.2);

        _levelPen = RenderPens.Chain(ink.LevelLine, 1.0, 16, 4, 2, 4);
        _groundPen = RenderPens.Solid(ink.GroundLine, 1.0);

        _levelTextBrush = RenderPens.Fill(ink.LevelText);
    }

    private HashSet<Guid> _selected = new();

    /// <summary>What is highlighted while editing. Always empty on a sheet.</summary>
    public void SetSelection(IEnumerable<Element>? elements) =>
        _selected = elements is null
            ? new HashSet<Guid>()
            : elements.Select(element => element.Id).ToHashSet();

    public double PixelsPerPaperMm { get; set; } = PlanRenderer.ScreenPixelsPerPaperMm;

    public double PixelsPerDip { get; set; } = 1;

    /// <summary>
    /// Points the renderer at a surface. The transform takes distance along the cut and a
    /// true elevation; the renderer never has to know which way up the surface is.
    /// </summary>
    public void SetTransform(Func<double, double, Point> toScreen) => _toScreen = toScreen;

    private double Page(double pixelsAtScreenScale) =>
        pixelsAtScreenScale * (PixelsPerPaperMm / PlanRenderer.ScreenPixelsPerPaperMm);

    /// <summary>
    /// The whole drawing: level datums first, then everything beyond the cut, then the cut.
    /// <paramref name="labelLevels"/> is off inside a viewport, where the datum names would
    /// spill outside the frame.
    /// </summary>
    public void DrawContent(DrawingContext dc, SectionDrawing drawing, bool labelLevels = true)
    {
        DrawLevels(dc, drawing, labelLevels);

        foreach (var piece in drawing.Pieces) DrawPiece(dc, piece);
    }

    private void DrawLevels(DrawingContext dc, SectionDrawing drawing, bool labelLevels)
    {
        // Level lines run right across the drawing, past the building at both ends, because a
        // datum belongs to the project rather than to whatever happens to sit on it.
        var overhang = Math.Max(1500, drawing.Width * 0.04);

        foreach (var level in drawing.Levels)
        {
            var left = _toScreen(-overhang, level.Elevation);
            var right = _toScreen(drawing.Width + overhang, level.Elevation);

            dc.DrawLine(level.Elevation == 0 ? _groundPen : _levelPen, left, right);

            if (!labelLevels) continue;

            var text = Text($"{level.Name}   {Units.FormatLength(level.Elevation)}", Page(11), _levelTextBrush);
            dc.DrawText(text, new Point(right.X + Page(6), right.Y - text.Height - Page(2)));
        }
    }

    private void DrawPiece(DrawingContext dc, SectionPiece piece)
    {
        var topLeft = _toScreen(piece.Bounds.Left, piece.Bounds.Top);
        var bottomRight = _toScreen(piece.Bounds.Right, piece.Bounds.Bottom);

        var rect = new Rect(topLeft, bottomRight);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var isSelected = _selected.Count > 0 && _selected.Contains(piece.ElementId);
        var isCut = piece.Depth == SectionDepth.Cut;

        // Below about two pixels a stroke covers more of the piece than the piece does, so the
        // drawing would read as a smear. Same rule as the layer lines in plan.
        var strokeable = rect.Width > 2 && rect.Height > 2;
        var outline = isSelected ? _selectedPen : isCut ? _cutPen : _seenPen;

        // A leaning wall's piece is its true shape rather than an upright rectangle.
        if (piece.Shape is { Count: >= 3 } shape)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(_toScreen(shape[0].X, shape[0].Y), true, true);
                ctx.PolyLineTo(shape.Skip(1).Select(p => _toScreen(p.X, p.Y)).ToArray(), true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(isCut ? Fill(piece.Fill) : FadedFill(piece.Fill), strokeable || isSelected ? outline : null, geometry);
            return;
        }

        dc.DrawRectangle(
            isCut ? Fill(piece.Fill) : FadedFill(piece.Fill),
            strokeable || isSelected ? outline : null,
            rect);
    }

    private FormattedText Text(string value, double size, Brush brush) => new(
        value,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        Math.Max(1, size),
        brush,
        PixelsPerDip);

    private Brush Fill(ColourRgb colour)
    {
        if (_fills.TryGetValue(colour, out var cached)) return cached;

        var brush = RenderPens.Fill(Color.FromRgb(colour.R, colour.G, colour.B));
        _fills[colour] = brush;
        return brush;
    }

    /// <summary>
    /// What lies beyond the cut, drawn quietly. Fading it toward the background is what makes
    /// depth read on a flat drawing.
    /// </summary>
    private Brush FadedFill(ColourRgb colour)
    {
        if (_fadedFills.TryGetValue(colour, out var cached)) return cached;

        var brush = RenderPens.Fill(Color.FromArgb(0x55, colour.R, colour.G, colour.B));
        _fadedFills[colour] = brush;
        return brush;
    }
}
