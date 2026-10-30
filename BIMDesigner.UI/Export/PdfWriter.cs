using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
/// What a PDF carries beside its pages: its title and author, a bookmark for each page - the
/// drawing number and name a set is navigated by - and a digital signature where one is asked for.
/// </summary>
public sealed record PdfOptions(string? Title = null, string? Author = null, IReadOnlyList<string>? Bookmarks = null, PdfSigner? Signer = null);

/// <summary>
/// Who signs a PDF, and why and where: a certificate with its private key - a digital ID from a
/// file or from Windows' certificate store - put to the whole document as a detached CMS signature,
/// which PDF readers check against the bytes they open and say whether anything has changed since.
/// </summary>
public sealed record PdfSigner(System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate, string Reason, string Location, string ContactInfo = "");

/// <summary>
/// Writes a vector PDF from WPF drawings (specification section 6.5).
///
/// This is deliberately a real export rather than a print: a drawing set has to be
/// produceable without a printer driver installed, on a build server as easily as on a
/// laptop, and the file has to be the same file every time. Printing to a PDF driver gives
/// none of those.
///
/// Only the operators these drawings actually use are implemented - filled and stroked
/// paths, clipping, constant alpha, and pictures as deflated images with their transparency. Every geometry is flattened to polylines first,
/// which is what keeps it that small: curves, arcs and text outlines all arrive as the same
/// thing, and at drawing scale the flattening error is far below the width of a pen.
/// </summary>
public static class PdfWriter
{
    public const double PointsPerMillimetre = 72.0 / 25.4;

    /// <summary>Writes the pages to a file, each page's drawing deflated.</summary>
    public static void Write(string path, IReadOnlyList<PdfPage> pages, PdfOptions? options = null)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF needs at least one page.", nameof(pages));

        // Written whole before the file is touched: a signature is worked out over the finished bytes.
        using var buffer = new MemoryStream();
        Write(buffer, pages, compress: true, options);
        File.WriteAllBytes(path, buffer.ToArray());
    }

    /// <summary>How many bytes are kept for a signature: room for the signer's certificate and its chain.</summary>
    private const int SignatureRoom = 16384;

    /// <summary>
    /// Writes the pages to a stream - each page's drawing deflated where <paramref name="compress"/> is
    /// asked, five or ten times smaller, as a file is written; left as text otherwise, to be read -
    /// with the title, author, bookmarks and signature the options ask for.
    /// </summary>
    public static void Write(Stream output, IReadOnlyList<PdfPage> pages, bool compress = false, PdfOptions? options = null)
    {
        var objects = new List<byte[]>();

        // 1 is the catalogue and 2 the page tree; each page then takes two objects, its own
        // and its content stream.
        var pageIds = new List<int>();
        var bodies = new List<(int PageId, int ContentId, PdfContent Content, PdfPage Page, Dictionary<string, int> Images)>();

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

            // Each picture on it next: its own object, and its transparency's after it where it has any.
            var images = new Dictionary<string, int>();
            foreach (var image in content.Images)
            {
                images[image.Name] = nextId++;
                if (image.Alpha is not null) nextId++;
            }

            bodies.Add((pageId, contentId, content, page, images));
        }

        // After the pages: the document's information, its bookmarks, and its signature, field and form.
        var infoId = options?.Title is not null || options?.Author is not null ? nextId++ : 0;
        var marks = options?.Bookmarks is { } bookmarks ? bookmarks.Take(pages.Count).ToList() : new List<string>();
        var outlinesId = marks.Count > 0 ? nextId++ : 0;
        var markIds = marks.Select(_ => nextId++).ToList();
        var signer = options?.Signer;
        var (signatureId, widgetId, formId) = signer is null ? (0, 0, 0) : (nextId++, nextId++, nextId++);

        objects.Add(Latin1("<< /Type /Catalog /Pages 2 0 R" +
                           (outlinesId > 0 ? $" /Outlines {outlinesId} 0 R /PageMode /UseOutlines" : string.Empty) +
                           (formId > 0 ? $" /AcroForm {formId} 0 R" : string.Empty) + " >>"));

        objects.Add(Latin1(
            $"<< /Type /Pages /Count {pages.Count} " +
            $"/Kids [ {string.Join(" ", pageIds.Select(id => $"{id} 0 R"))} ] >>"));

        foreach (var (pageId, contentId, content, page, images) in bodies)
        {
            objects.Add(Latin1(
                $"<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [ 0 0 {Number(page.WidthPoints)} {Number(page.HeightPoints)} ] " +
                $"/Resources << {content.ResourceDictionary(images)} >> " +
                (widgetId > 0 && pageId == pageIds[0] ? $"/Annots [ {widgetId} 0 R ] " : string.Empty) +
                $"/Contents {contentId} 0 R >>"));

            var stream = Latin1(content.ToString());
            if (compress) stream = PdfContent.Deflate(stream);
            objects.Add(Combine(
                Latin1($"<< /Length {stream.Length}{(compress ? " /Filter /FlateDecode" : string.Empty)} >>\nstream\n"),
                stream,
                Latin1("\nendstream")));

            foreach (var image in content.Images)
            {
                var id = images[image.Name];
                objects.Add(ImageObject(image.Width, image.Height, "/DeviceRGB", image.Colour, image.Alpha is null ? null : id + 1));
                if (image.Alpha is { } alpha) objects.Add(ImageObject(image.Width, image.Height, "/DeviceGray", alpha, null));
            }

            // The two objects were reserved in this order, so they must be added in it.
            _ = pageId;
        }

        if (infoId > 0)
            objects.Add(Latin1($"<< /Title {TextString(options!.Title ?? string.Empty)} /Author {TextString(options.Author ?? string.Empty)} /Creator {TextString("BIMDesigner")} " +
                               $"/CreationDate ({DateText(DateTime.Now)}) >>"));

        // A bookmark for each page, in order, each opening its page whole.
        if (outlinesId > 0)
        {
            objects.Add(Latin1($"<< /Type /Outlines /First {markIds[0]} 0 R /Last {markIds[^1]} 0 R /Count {marks.Count} >>"));
            for (var i = 0; i < marks.Count; i++)
                objects.Add(Latin1($"<< /Title {TextString(marks[i])} /Parent {outlinesId} 0 R " +
                                   (i > 0 ? $"/Prev {markIds[i - 1]} 0 R " : string.Empty) +
                                   (i < marks.Count - 1 ? $"/Next {markIds[i + 1]} 0 R " : string.Empty) +
                                   $"/Dest [ {pageIds[i]} 0 R /Fit ] >>"));
        }

        // The signature: its value left as a run of zeros, and where the bytes it covers end, to be filled in once they are written.
        if (signer is not null)
        {
            objects.Add(Latin1("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
                               $"/ByteRange {ByteRangeHole} /Contents <{new string('0', SignatureRoom * 2)}> " +
                               $"/M ({DateText(DateTime.Now)}) /Name {TextString(signer.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false))} " +
                               $"/Reason {TextString(signer.Reason)} /Location {TextString(signer.Location)} /ContactInfo {TextString(signer.ContactInfo)} >>"));
            objects.Add(Latin1($"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /V {signatureId} 0 R /F 132 /Rect [ 0 0 0 0 ] /P {pageIds[0]} 0 R >>"));
            objects.Add(Latin1($"<< /Fields [ {widgetId} 0 R ] /SigFlags 3 >>"));
        }

        if (signer is null)
        {
            WriteDocument(output, objects, infoId);
            return;
        }

        using var unsigned = new MemoryStream();
        WriteDocument(unsigned, objects, infoId);
        var signed = Sign(unsigned.ToArray(), signer.Certificate);
        output.Write(signed, 0, signed.Length);
    }

    /// <summary>The byte range a signature covers, left to be filled in: the same width as the figures that will go in it.</summary>
    private const string ByteRangeHole = "[ 0 0000000000 0000000000 0000000000 ]";

    /// <summary>
    /// A written PDF signed: the bytes either side of the signature's value are what it covers -
    /// their range filled in where it was left, the same width so nothing after moves - and the
    /// value a CMS signature of them, SHA-256, with the signer's certificate chain, in hex where
    /// the zeros were.
    /// </summary>
    internal static byte[] Sign(byte[] pdf, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        var hole = IndexOf(pdf, Latin1(ByteRangeHole));
        var value = IndexOf(pdf, Latin1($"/Contents <{new string('0', 32)}"));
        if (hole < 0 || value < 0) throw new InvalidOperationException("The PDF has no room left for a signature.");

        var start = value + "/Contents ".Length;
        var end = start + SignatureRoom * 2 + 2;
        var range = $"[ 0 {start:D10} {end:D10} {pdf.Length - end:D10} ]";
        Latin1(range).CopyTo(pdf, hole);

        var covered = new byte[start + pdf.Length - end];
        Buffer.BlockCopy(pdf, 0, covered, 0, start);
        Buffer.BlockCopy(pdf, end, covered, start, pdf.Length - end);

        var cms = new System.Security.Cryptography.Pkcs.SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo(covered), detached: true);
        var signer = new System.Security.Cryptography.Pkcs.CmsSigner(System.Security.Cryptography.Pkcs.SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new System.Security.Cryptography.Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = System.Security.Cryptography.X509Certificates.X509IncludeOption.WholeChain
        };
        signer.SignedAttributes.Add(new System.Security.Cryptography.Pkcs.Pkcs9SigningTime(DateTime.UtcNow));
        cms.ComputeSignature(signer, silent: true);
        var signature = Convert.ToHexString(cms.Encode());
        if (signature.Length > SignatureRoom * 2) throw new InvalidOperationException("The signature, with its certificates, is larger than the room kept for it.");

        Latin1(signature).CopyTo(pdf, start + 1);
        return pdf;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }

        return -1;
    }

    /// <summary>Text as a PDF string in any language: UTF-16, marked so, in hex.</summary>
    public static string TextString(string text) => $"<FEFF{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))}>";

    /// <summary>A moment as PDF writes dates: D:YYYYMMDDHHmmSS and its offset from UTC.</summary>
    private static string DateText(DateTime when)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(when);
        return $"D:{when:yyyyMMddHHmmss}{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
    }


    /// <summary>A picture as a PDF image: its pixels deflated, and the mask of its transparency where it has one.</summary>
    private static byte[] ImageObject(int width, int height, string colourSpace, byte[] deflated, int? mask) => Combine(
        Latin1($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colourSpace} /BitsPerComponent 8 " +
               $"/Filter /FlateDecode{(mask is { } id ? $" /SMask {id} 0 R" : string.Empty)} /Length {deflated.Length} >>\nstream\n"),
        deflated,
        Latin1("\nendstream"));

    /// <summary>Lays the objects out with a cross-reference table, which is what makes it a PDF.</summary>
    private static void WriteDocument(Stream output, List<byte[]> objects, int infoId = 0)
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
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{(infoId > 0 ? $" /Info {infoId} 0 R" : string.Empty)} >>\nstartxref\n{xref}\n%%EOF\n");

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
/// It understands four kinds of node: a group (which may carry a transform, a clip and an
/// opacity), a geometry with a brush and a pen, a run of glyphs, and a picture - a sheet's
/// rendering or photograph. Everything the application draws is one of those.
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

            case ImageDrawing image:
                DrawImage(image, transform, opacity);
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

        // A line alone - no fill - is drawn along every figure, filled or not: an open one is a line too.
        if (!AppendPath(drawing.Geometry, transform, out var evenOdd, everyFigure: fill is null))
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

        // Flattened to a fiftieth of its size: finer than ink spreads, and a page of text far fewer bytes.
        var scale = Math.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
        var tolerance = Math.Clamp(drawing.GlyphRun.FontRenderingEmSize * (scale > 0 && double.IsFinite(scale) ? scale : 1) / 50, 0.05, 0.4);
        if (AppendPath(outline, transform, out var evenOdd, tolerance: tolerance)) Line(evenOdd ? "f*" : "f");

        Line("Q");
    }


    /// <summary>A picture drawn on a page: its name in the page's resources, its size, its pixels deflated, its transparency deflated where it has any.</summary>
    internal sealed record PdfImage(string Name, int Width, int Height, byte[] Colour, byte[]? Alpha);

    private readonly List<PdfImage> _images = new();
    private readonly Dictionary<ImageSource, PdfImage> _imageOf = new(ReferenceEqualityComparer.Instance);

    /// <summary>The pictures the page draws, each once however often it is drawn.</summary>
    public IReadOnlyList<PdfImage> Images => _images;

    /// <summary>
    /// A picture: its pixels written once into the page as an image, then drawn into its rectangle. Its first
    /// row is the top of the unit square, which the page's y-down transform puts at the rectangle's top.
    /// </summary>
    private void DrawImage(ImageDrawing drawing, Matrix transform, double opacity)
    {
        if (drawing.ImageSource is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;
        if (!_imageOf.TryGetValue(bitmap, out var image)) _imageOf[bitmap] = image = Encode(bitmap, $"Im{_images.Count}");
        var a = transform.Transform(drawing.Rect.TopLeft);
        var b = transform.Transform(drawing.Rect.BottomRight);
        Line("q");
        if (opacity < 0.999) Line($"/{AlphaState(opacity)} gs");
        Line($"{N(b.X - a.X)} 0 0 {N(-(b.Y - a.Y))} {N(a.X)} {N(b.Y)} cm");
        Line($"/{image.Name} Do");
        Line("Q");
    }

    private PdfImage Encode(BitmapSource bitmap, string name)
    {
        var bgra = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var (width, height) = (bgra.PixelWidth, bgra.PixelHeight);
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        var colour = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var opaque = true;
        for (int i = 0, j = 0; i < width * height; i++, j += 4)
        {
            (colour[i * 3], colour[i * 3 + 1], colour[i * 3 + 2]) = (pixels[j + 2], pixels[j + 1], pixels[j]);
            alpha[i] = pixels[j + 3];
            opaque &= alpha[i] == 255;
        }

        var image = new PdfImage(name, width, height, Deflate(colour), opaque ? null : Deflate(alpha));
        _images.Add(image);
        return image;
    }

    internal static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw, 0, raw.Length);
        return output.ToArray();
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
    private bool AppendPath(Geometry geometry, Matrix transform, out bool evenOdd, bool everyFigure = false, double tolerance = 0.05)
    {
        // WPF flattens only the figures that are filled: where every figure is wanted, a copy of it all filled is flattened.
        if (everyFigure)
        {
            var whole = PathGeometry.CreateFromGeometry(geometry).CloneCurrentValue();
            foreach (var figure in whole.Figures) figure.IsFilled = true;
            geometry = whole;
        }

        // The tolerance is absolute and in points: about a fiftieth of a millimetre, which is
        // finer than any printer resolves. The default is relative to the geometry's own size
        // and at this scale would leave door swings visibly faceted.
        var flattened = geometry.GetFlattenedPathGeometry(tolerance, ToleranceType.Absolute);
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

    public string ResourceDictionary(IReadOnlyDictionary<string, int>? images = null)
    {
        var parts = new List<string>();
        if (_alphaStates.Count > 0)
            parts.Add($"/ExtGState << {string.Join(" ", _alphaStates.Select(state => $"/{state.Value} << /Type /ExtGState /ca {N(state.Key)} /CA {N(state.Key)} >>"))} >>");
        if (images is { Count: > 0 })
            parts.Add($"/XObject << {string.Join(" ", images.Select(image => $"/{image.Key} {image.Value} 0 R"))} >>");
        return string.Join(" ", parts);
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
