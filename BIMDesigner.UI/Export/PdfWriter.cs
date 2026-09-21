using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace BIMDesigner.UI.Export;

/// <summary>
/// One page of a PDF: its size in points, and the drawing that fills it.
///
/// The content is expressed with its origin at the top-left corner and Y increasing
/// downward, the same way every surface in this application works. The writer flips it once
/// on the way out rather than making every caller think in PDF's upside-down coordinates.
/// </summary>
public sealed record PdfPage(double WidthPoints, double HeightPoints, Drawing? Content);

/// <summary>
/// Writes a vector PDF from WPF drawings (specification section 6.5).
///
/// This is deliberately a real export rather than a print: a drawing set has to be
/// produceable without a printer driver installed, on a build server as easily as on a
/// laptop, and the file has to be the same file every time. Printing to a PDF driver gives
/// none of those.
///
/// Only the operators these drawings actually use are implemented - filled and stroked
/// paths, clipping, and constant alpha. Every geometry is flattened to polylines first,
/// which is what keeps it that small: curves, arcs and text outlines all arrive as the same
/// thing, and at drawing scale the flattening error is far below the width of a pen.
/// </summary>
public static class PdfWriter
{
    public const double PointsPerMillimetre = 72.0 / 25.4;

    public static void Write(string path, IReadOnlyList<PdfPage> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF needs at least one page.", nameof(pages));

        using var file = File.Create(path);
        Write(file, pages);
    }

    public static void Write(Stream output, IReadOnlyList<PdfPage> pages)
    {
        var objects = new List<byte[]>();

        // 1 is the catalogue and 2 the page tree; each page then takes two objects, its own
        // and its content stream.
        var pageIds = new List<int>();
        var bodies = new List<(int PageId, int ContentId, PdfContent Content, PdfPage Page)>();

        var nextId = 3;

        foreach (var page in pages)
        {
            var content = new PdfContent();

            // PDF puts its origin at the bottom-left with Y upward. One flip here lets every
            // drawing above be written the way screens and printers think.
            content.Concat(1, 0, 0, -1, 0, page.HeightPoints);
            if (page.Content is not null) content.Draw(page.Content, Matrix.Identity, 1.0);

            var pageId = nextId++;
            var contentId = nextId++;

            pageIds.Add(pageId);
            bodies.Add((pageId, contentId, content, page));
        }

        objects.Add(Latin1($"<< /Type /Catalog /Pages 2 0 R >>"));

        objects.Add(Latin1(
            $"<< /Type /Pages /Count {pages.Count} " +
            $"/Kids [ {string.Join(" ", pageIds.Select(id => $"{id} 0 R"))} ] >>"));

        foreach (var (pageId, contentId, content, page) in bodies)
        {
            objects.Add(Latin1(
                $"<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [ 0 0 {Number(page.WidthPoints)} {Number(page.HeightPoints)} ] " +
                $"/Resources << {content.ResourceDictionary()} >> " +
                $"/Contents {contentId} 0 R >>"));

            var stream = Latin1(content.ToString());
            objects.Add(Combine(
                Latin1($"<< /Length {stream.Length} >>\nstream\n"),
                stream,
                Latin1("\nendstream")));

            // The two objects were reserved in this order, so they must be added in it.
            _ = pageId;
        }

        WriteDocument(output, objects);
    }

    /// <summary>Lays the objects out with a cross-reference table, which is what makes it a PDF.</summary>
    private static void WriteDocument(Stream output, List<byte[]> objects)
    {
        var offsets = new long[objects.Count + 1];
        var position = 0L;

        void Emit(byte[] bytes)
        {
            output.Write(bytes, 0, bytes.Length);
            position += bytes.Length;
        }

        Emit(Latin1("%PDF-1.4\n"));

        // A comment of high bytes, which is how a reader tells a PDF is binary and must not
        // be mangled by a transfer that thinks it is text.
        Emit(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = position;
            Emit(Latin1($"{i + 1} 0 obj\n"));
            Emit(objects[i]);
            Emit(Latin1("\nendobj\n"));
        }

        var xref = position;
        var table = new StringBuilder();

        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n");
        table.Append("0000000000 65535 f \n");

        for (var i = 1; i <= objects.Count; i++)
            table.Append(CultureInfo.InvariantCulture, $"{offsets[i]:D10} 00000 n \n");

        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        Emit(Latin1(table.ToString()));
    }

    internal static string Number(double value) =>
        Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Combine(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;

        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }
}

/// <summary>
/// One page's content stream, built by walking a WPF drawing tree.
///
/// It understands three kinds of node: a group (which may carry a transform, a clip and an
/// opacity), a geometry with a brush and a pen, and a run of glyphs. Everything the
/// application draws is one of those.
/// </summary>
internal sealed class PdfContent
{
    private readonly StringBuilder _stream = new();

    /// <summary>Constant-alpha graphics states, named as they are needed.</summary>
    private readonly Dictionary<double, string> _alphaStates = new();

    public void Concat(double a, double b, double c, double d, double e, double f) =>
        Line($"{N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm");

    public void Draw(Drawing drawing, Matrix transform, double opacity)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                DrawGroup(group, transform, opacity);
                break;

            case GeometryDrawing geometry:
                DrawGeometry(geometry, transform, opacity);
                break;

            case GlyphRunDrawing glyphs:
                DrawGlyphs(glyphs, transform, opacity);
                break;
        }
    }

    private void DrawGroup(DrawingGroup group, Matrix transform, double opacity)
    {
        var local = transform;
        if (group.Transform is { } groupTransform) local = groupTransform.Value * local;

        var clipped = group.ClipGeometry is not null;
        if (clipped)
        {
            Line("q");

            // A clip is a path followed by "W n": mark it as the clip, then discard it as a
            // path so nothing is painted by it.
            if (AppendPath(group.ClipGeometry!, local, out var evenOdd))
                Line(evenOdd ? "W* n" : "W n");
            else
                Line("n");
        }

        foreach (var child in group.Children) Draw(child, local, opacity * group.Opacity);

        if (clipped) Line("Q");
    }

    private void DrawGeometry(GeometryDrawing drawing, Matrix transform, double opacity)
    {
        if (drawing.Geometry is null) return;

        var fill = SolidColour(drawing.Brush, opacity);
        var stroke = drawing.Pen is null ? null : SolidColour(drawing.Pen.Brush, opacity);

        if (fill is null && stroke is null) return;

        Line("q");

        // One alpha for the whole operation. Separate fill and stroke alphas would need two
        // states and two passes; nothing here paints a translucent fill under an opaque line.
        var alpha = Math.Min(fill?.Alpha ?? 1, stroke?.Alpha ?? 1);
        if (alpha < 0.999) Line($"/{AlphaState(alpha)} gs");

        if (fill is { } fillColour) Line($"{fillColour.Rgb} rg");

        if (stroke is { } strokeColour && drawing.Pen is { } pen)
        {
            Line($"{strokeColour.Rgb} RG");
            ApplyPen(pen, transform);
        }

        if (!AppendPath(drawing.Geometry, transform, out var evenOdd))
        {
            Line("Q");
            return;
        }

        Line(Paint(fill is not null, stroke is not null, evenOdd));
        Line("Q");
    }

    /// <summary>
    /// Text as outlines rather than as embedded fonts.
    ///
    /// It makes the file a little larger, but it removes font embedding, licensing and
    /// substitution from the problem entirely: the drawing arrives looking exactly as it did
    /// on screen on a machine that has never heard of the typeface.
    /// </summary>
    private void DrawGlyphs(GlyphRunDrawing drawing, Matrix transform, double opacity)
    {
        if (drawing.GlyphRun is null) return;

        var colour = SolidColour(drawing.ForegroundBrush, opacity);
        if (colour is null) return;

        var outline = drawing.GlyphRun.BuildGeometry();

        Line("q");
        if (colour.Alpha < 0.999) Line($"/{AlphaState(colour.Alpha)} gs");
        Line($"{colour.Rgb} rg");

        if (AppendPath(outline, transform, out var evenOdd)) Line(evenOdd ? "f*" : "f");

        Line("Q");
    }

    private void ApplyPen(Pen pen, Matrix transform)
    {
        // A transform that scales the drawing scales the pen with it. The determinant's root
        // is the average scale, which is right for the uniform transforms used here.
        var scale = Math.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
        if (scale <= 0 || double.IsNaN(scale)) scale = 1;

        var thickness = Math.Max(pen.Thickness * scale, 0.05);
        Line($"{N(thickness)} w");

        // WPF measures dashes in multiples of the pen thickness; PDF measures them absolutely.
        var dashes = pen.DashStyle?.Dashes;
        if (dashes is null || dashes.Count == 0)
        {
            Line("[] 0 d");
            return;
        }

        var pattern = string.Join(" ", dashes.Select(dash => N(Math.Max(dash * thickness, 0.01))));
        Line($"[{pattern}] {N((pen.DashStyle?.Offset ?? 0) * thickness)} d");
    }

    /// <summary>
    /// Writes a geometry as a PDF path, flattening it first.
    ///
    /// Flattening turns arcs, ellipses, rounded corners and glyph outlines into the one thing
    /// the writer has to understand. Returns false for an empty geometry, so the caller can
    /// skip the paint operator rather than emitting one with nothing to paint.
    /// </summary>
    private bool AppendPath(Geometry geometry, Matrix transform, out bool evenOdd)
    {
        // The tolerance is absolute and in points: about a fiftieth of a millimetre, which is
        // finer than any printer resolves. The default is relative to the geometry's own size
        // and at this scale would leave door swings visibly faceted.
        var flattened = geometry.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
        evenOdd = flattened.FillRule == FillRule.EvenOdd;

        var wrote = false;

        foreach (var figure in flattened.Figures)
        {
            var start = transform.Transform(figure.StartPoint);
            Line($"{N(start.X)} {N(start.Y)} m");
            wrote = true;

            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment poly:
                        foreach (var point in poly.Points) LineTo(transform.Transform(point));
                        break;

                    case LineSegment line:
                        LineTo(transform.Transform(line.Point));
                        break;
                }
            }

            if (figure.IsClosed) Line("h");
        }

        return wrote;
    }

    private void LineTo(Point point) => Line($"{N(point.X)} {N(point.Y)} l");

    private static string Paint(bool fill, bool stroke, bool evenOdd) => (fill, stroke) switch
    {
        (true, true) => evenOdd ? "B*" : "B",
        (true, false) => evenOdd ? "f*" : "f",
        _ => "S"
    };

    private string AlphaState(double alpha)
    {
        var key = Math.Round(alpha, 3);
        if (_alphaStates.TryGetValue(key, out var existing)) return existing;

        var name = $"GS{_alphaStates.Count}";
        _alphaStates[key] = name;
        return name;
    }

    public string ResourceDictionary()
    {
        if (_alphaStates.Count == 0) return string.Empty;

        var entries = _alphaStates.Select(state =>
            $"/{state.Value} << /Type /ExtGState /ca {N(state.Key)} /CA {N(state.Key)} >>");

        return $"/ExtGState << {string.Join(" ", entries)} >>";
    }

    private sealed record Colour(string Rgb, double Alpha);

    /// <summary>
    /// The colour a brush paints with. Only solid brushes are used by these drawings; a
    /// gradient or an image would come back null and simply not be painted, which is a
    /// visible gap rather than a wrong colour quietly standing in for it.
    /// </summary>
    private static Colour? SolidColour(Brush? brush, double opacity)
    {
        if (brush is not SolidColorBrush solid) return null;

        var alpha = solid.Color.A / 255.0 * solid.Opacity * opacity;
        if (alpha <= 0.002) return null;

        return new Colour(
            $"{N(solid.Color.R / 255.0)} {N(solid.Color.G / 255.0)} {N(solid.Color.B / 255.0)}",
            Math.Min(1, alpha));
    }

    private void Line(string text) => _stream.Append(text).Append('\n');

    private static string N(double value) => PdfWriter.Number(value);

    public override string ToString() => _stream.ToString();
}
