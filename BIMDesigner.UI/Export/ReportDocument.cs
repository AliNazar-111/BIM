using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BIMDesigner.UI.Export;

/// <summary>How a piece of a report's text reads: plainly, quieter, or as a requirement met or not.</summary>
public enum ReportTone
{
    Plain,
    Muted,
    Met,
    Unmet
}

/// <summary>A column of a report's table: its heading, its share of the width, and whether its figures line up on the right.</summary>
public sealed record ReportColumn(string Heading, double Share, bool Right = false);

/// <summary>One cell of a report's table: its text, and how it reads.</summary>
public readonly record struct ReportCell(string Text, ReportTone Tone = ReportTone.Plain)
{
    public static implicit operator ReportCell(string text) => new(text);
}

/// <summary>
/// A report laid out on A4 pages, top to bottom, to be written as a PDF: a title, headings, paragraphs,
/// lists of facts, tables and drawn blocks, each moved onto the next page where it will not fit on this
/// one - a table broken between its rows, its heading row again at the top of the next page - and every
/// page footed with the report's name and its number of the whole. Drawn in points, as the PDF is.
/// </summary>
public sealed class ReportDocument
{
    public const double PageWidth = 595.28, PageHeight = 841.89;
    private const double Left = 50, Right = 50, Top = 54, Bottom = 64;
    private const double Body = 9.2, Small = 8.2;

    private static readonly Typeface Regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Ink = Frozen(Color.FromRgb(0x1A, 0x1C, 0x20));
    private static readonly Brush Quiet = Frozen(Color.FromRgb(0x6A, 0x70, 0x78));
    private static readonly Brush Green = Frozen(Color.FromRgb(0x1E, 0x7D, 0x46));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xB8, 0x2E, 0x22));
    private static readonly Brush Shade = Frozen(Color.FromRgb(0xEC, 0xEE, 0xF1));
    private static readonly Brush Stripe = Frozen(Color.FromRgb(0xF7, 0xF8, 0xF9));
    private static readonly Pen Rule = FrozenPen(Color.FromRgb(0xB8, 0xBE, 0xC6), 0.6);
    private static readonly Pen Heavy = FrozenPen(Color.FromRgb(0x1A, 0x1C, 0x20), 1.2);

    private readonly string _footer;
    private readonly List<List<Action<DrawingContext>>> _pages = new() { new List<Action<DrawingContext>>() };
    private double _y = Top;

    public ReportDocument(string footer) => _footer = footer;

    private static double Width => PageWidth - Left - Right;

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color colour, double thickness)
    {
        var pen = new Pen(Frozen(colour), thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush BrushOf(ReportTone tone) => tone switch
    {
        ReportTone.Muted => Quiet,
        ReportTone.Met => Green,
        ReportTone.Unmet => Red,
        _ => Ink
    };

    /// <summary>Text set to a width, wrapped.</summary>
    private static FormattedText Text(string text, double size, Brush brush, bool bold = false, double width = 0, TextAlignment alignment = TextAlignment.Left)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Regular, size, brush, 1)
        {
            TextAlignment = alignment
        };
        if (width > 0) formatted.MaxTextWidth = width;
        return formatted;
    }

    private List<Action<DrawingContext>> Page => _pages[^1];

    /// <summary>Room for this much more on the page, or the next page begun.</summary>
    private void Need(double height)
    {
        if (_y + height <= PageHeight - Bottom || _y <= Top + 0.5) return;
        NewPage();
    }

    /// <summary>The next page begun.</summary>
    public void NewPage()
    {
        _pages.Add(new List<Action<DrawingContext>>());
        _y = Top;
    }

    public void Space(double points) => _y = Math.Min(_y + points, PageHeight - Bottom);

    /// <summary>The report's title and what it is of, under a heavy rule.</summary>
    public void Title(string title, string subtitle)
    {
        var big = Text(title, 20, Ink, bold: true, width: Width);
        var under = Text(subtitle, 11, Quiet, width: Width);
        Need(big.Height + under.Height + 14);
        var y = _y;
        Page.Add(dc =>
        {
            dc.DrawText(big, new Point(Left, y));
            dc.DrawText(under, new Point(Left, y + big.Height + 2));
            dc.DrawLine(Heavy, new Point(Left, y + big.Height + under.Height + 8), new Point(Left + Width, y + big.Height + under.Height + 8));
        });
        _y += big.Height + under.Height + 18;
    }

    /// <summary>A heading, kept with at least a line of what follows it.</summary>
    public void Heading(string text, int level = 1)
    {
        var size = level == 1 ? 13.5 : 11;
        var formatted = Text(text, size, Ink, bold: true, width: Width);
        Space(level == 1 ? 10 : 6);
        Need(formatted.Height + 40);
        var y = _y;
        Page.Add(dc =>
        {
            dc.DrawText(formatted, new Point(Left, y));
            if (level == 1) dc.DrawLine(Rule, new Point(Left, y + formatted.Height + 2), new Point(Left + Width, y + formatted.Height + 2));
        });
        _y += formatted.Height + (level == 1 ? 8 : 4);
    }

    /// <summary>A paragraph, wrapped to the page.</summary>
    public void Paragraph(string text, ReportTone tone = ReportTone.Plain, double size = Body)
    {
        var formatted = Text(text, size, BrushOf(tone), width: Width);
        Need(formatted.Height);
        var y = _y;
        Page.Add(dc => dc.DrawText(formatted, new Point(Left, y)));
        _y += formatted.Height + 5;
    }

    /// <summary>A list of facts: each label on the left and what it is beside it.</summary>
    public void Facts(IEnumerable<(string Label, string Value)> facts)
    {
        foreach (var (label, value) in facts)
        {
            var name = Text(label, Body, Quiet, width: Width * 0.3 - 6);
            var said = Text(value, Body, Ink, width: Width * 0.7);
            var height = Math.Max(name.Height, said.Height);
            Need(height);
            var y = _y;
            Page.Add(dc =>
            {
                dc.DrawText(name, new Point(Left, y));
                dc.DrawText(said, new Point(Left + Width * 0.3, y));
            });
            _y += height + 2.5;
        }

        _y += 4;
    }

    /// <summary>
    /// A table: its heading row shaded, its rows striped and ruled, each row as high as its tallest cell;
    /// broken between rows onto the next page, the heading row again over the rest.
    /// </summary>
    public void Table(IReadOnlyList<ReportColumn> columns, IEnumerable<IReadOnlyList<ReportCell>> rows)
    {
        const double pad = 3;
        var total = columns.Sum(column => column.Share);
        var widths = columns.Select(column => column.Share / total * Width).ToList();
        var lefts = widths.Select((_, i) => Left + widths.Take(i).Sum()).ToList();

        FormattedText Cell(string text, int i, Brush brush, bool bold) =>
            Text(text, Small, brush, bold, widths[i] - 2 * pad, columns[i].Right ? TextAlignment.Right : TextAlignment.Left);

        var heads = columns.Select((column, i) => Cell(column.Heading, i, Ink, true)).ToList();
        var headHeight = heads.Max(text => text.Height) + 2 * pad;
        void HeadingRow()
        {
            var y = _y;
            Page.Add(dc =>
            {
                dc.DrawRectangle(Shade, null, new Rect(Left, y, Width, headHeight));
                for (var i = 0; i < heads.Count; i++) dc.DrawText(heads[i], new Point(lefts[i] + pad, y + pad));
                dc.DrawLine(Rule, new Point(Left, y + headHeight), new Point(Left + Width, y + headHeight));
            });
            _y += headHeight;
        }

        Need(headHeight + 18);
        HeadingRow();
        var index = 0;
        foreach (var row in rows)
        {
            var cells = row.Select((cell, i) => Cell(cell.Text, Math.Min(i, columns.Count - 1), BrushOf(cell.Tone), cell.Tone == ReportTone.Unmet)).Take(columns.Count).ToList();
            var height = cells.Max(text => text.Height) + 2 * pad;
            if (_y + height > PageHeight - Bottom)
            {
                NewPage();
                HeadingRow();
            }

            var y = _y;
            var striped = index++ % 2 == 1;
            Page.Add(dc =>
            {
                if (striped) dc.DrawRectangle(Stripe, null, new Rect(Left, y, Width, height));
                for (var i = 0; i < cells.Count; i++) dc.DrawText(cells[i], new Point(lefts[i] + pad, y + pad));
                dc.DrawLine(Rule, new Point(Left, y + height), new Point(Left + Width, y + height));
            });
            _y += height;
        }

        _y += 10;
    }

    /// <summary>Something drawn into a block of the page's width this high: given the rectangle it has.</summary>
    public void Block(double height, Action<DrawingContext, Rect> draw)
    {
        Need(height);
        var area = new Rect(Left, _y, Width, height);
        Page.Add(dc => draw(dc, area));
        _y += height + 6;
    }

    /// <summary>The pages as drawings, each footed with the report's name and its number.</summary>
    public IReadOnlyList<Drawing> Drawings()
    {
        var drawings = new List<Drawing>();
        for (var p = 0; p < _pages.Count; p++)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidth, PageHeight));
                foreach (var draw in _pages[p]) draw(dc);
                var foot = PageHeight - Bottom + 26;
                dc.DrawLine(Rule, new Point(Left, foot - 6), new Point(Left + Width, foot - 6));
                dc.DrawText(Text(_footer, 7.5, Quiet, width: Width * 0.8), new Point(Left, foot));
                dc.DrawText(Text($"Page {p + 1} of {_pages.Count}", 7.5, Quiet, width: Width, alignment: TextAlignment.Right), new Point(Left, foot));
            }

            drawings.Add(visual.Drawing);
        }

        return drawings;
    }

    /// <summary>The pages ready for the PDF writer.</summary>
    public IReadOnlyList<PdfPage> Pages() => Drawings().Select(drawing => new PdfPage(PageWidth, PageHeight, drawing)).ToList();

    /// <summary>How many pages it runs to.</summary>
    public int PageCount => _pages.Count;
}
