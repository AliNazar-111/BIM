using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>The colour a column's heading is filled with: what COBie uses to say what a column is.</summary>
public enum HeadingKind
{
    Plain,

    /// <summary>Yellow: must be filled in.</summary>
    Required,

    /// <summary>Salmon: names a row of another sheet.</summary>
    Reference,

    /// <summary>Purple: where it came from in the authoring system.</summary>
    External,

    /// <summary>Green: filled in if known.</summary>
    Optional
}

/// <summary>One worksheet: its name, its headings and how each is coloured, and its rows - text or numbers.</summary>
public sealed class XlsxSheet
{
    public XlsxSheet(string name, IReadOnlyList<(string Heading, HeadingKind Kind)> columns)
    {
        Name = name;
        Columns = columns;
    }

    public string Name { get; }

    public IReadOnlyList<(string Heading, HeadingKind Kind)> Columns { get; }

    public List<object?[]> Rows { get; } = new();

    public void Add(params object?[] cells) => Rows.Add(cells);
}

/// <summary>
/// A workbook written as Office Open XML (.xlsx) by hand: a zip of a workbook, its sheets with
/// their text written in place, and the few styles headings need - its first row bold, frozen
/// and coloured. Enough for a COBie spreadsheet, without a library to write one.
/// </summary>
public static class XlsxWriter
{
    public static void Write(Stream output, IReadOnlyList<XlsxSheet> sheets)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        void Entry(string path, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        var types = new StringBuilder();
        types.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        types.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        types.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        types.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        types.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        types.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 0; i < sheets.Count; i++)
            types.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        types.Append("</Types>");
        Entry("[Content_Types].xml", types.ToString());

        Entry("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");

        var workbook = new StringBuilder();
        workbook.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        workbook.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (var i = 0; i < sheets.Count; i++)
            workbook.Append(CultureInfo.InvariantCulture, $"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        workbook.Append("</sheets></workbook>");
        Entry("xl/workbook.xml", workbook.ToString());

        var rels = new StringBuilder();
        rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 0; i < sheets.Count; i++)
            rels.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
        rels.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        rels.Append("</Relationships>");
        Entry("xl/_rels/workbook.xml.rels", rels.ToString());

        // Fonts: plain and bold. Fills: none, the gray125 Excel expects second, then the four heading colours.
        Entry("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<fonts count=\"2\"><font><sz val=\"10\"/><name val=\"Arial\"/></font><font><b/><sz val=\"10\"/><name val=\"Arial\"/></font></fonts>"
            + "<fills count=\"6\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
            + Fill("FFFFFF99") + Fill("FFFFCC99") + Fill("FFCC99FF") + Fill("FFCCFFCC") + "</fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"6\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"4\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"5\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>"
            + "</styleSheet>");

        for (var i = 0; i < sheets.Count; i++) Entry($"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i]));
    }

    private static string Fill(string argb) => $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"{argb}\"/><bgColor indexed=\"64\"/></patternFill></fill>";

    private static int StyleOf(HeadingKind kind) => kind switch
    {
        HeadingKind.Required => 1,
        HeadingKind.Reference => 2,
        HeadingKind.External => 3,
        HeadingKind.Optional => 4,
        _ => 5
    };

    private static string Sheet(XlsxSheet sheet)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        xml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");

        // Each column as wide as its widest text, within reason.
        xml.Append("<cols>");
        for (var c = 0; c < sheet.Columns.Count; c++)
        {
            var widest = sheet.Rows.Select(row => c < row.Length ? Text(row[c]).Length : 0).Append(sheet.Columns[c].Heading.Length).Max();
            xml.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Clamp(widest + 2, 8, 60)}\" customWidth=\"1\"/>");
        }

        xml.Append("</cols><sheetData>");
        xml.Append("<row r=\"1\">");
        for (var c = 0; c < sheet.Columns.Count; c++)
            xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{Reference(c, 1)}\" t=\"inlineStr\" s=\"{StyleOf(sheet.Columns[c].Kind)}\"><is><t>{Escape(sheet.Columns[c].Heading)}</t></is></c>");
        xml.Append("</row>");

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 2}\">");
            for (var c = 0; c < row.Length && c < sheet.Columns.Count; c++)
            {
                var cell = Reference(c, r + 2);
                switch (row[c])
                {
                    case null:
                        break;
                    case double or int or decimal or long:
                        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\"><v>{Convert.ToDouble(row[c], CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    default:
                        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(Text(row[c]))}</t></is></c>");
                        break;
                }
            }

            xml.Append("</row>");
        }

        xml.Append("</sheetData></worksheet>");
        return xml.ToString();
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        double number => number.ToString("0.###", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    /// <summary>"A1", "AB12": a cell's reference.</summary>
    public static string Reference(int column, int row)
    {
        var letters = string.Empty;
        for (var n = column + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Text made safe for XML: escaped, and the control characters XML cannot hold left out.</summary>
    private static string Escape(string text) =>
        SecurityElement.Escape(new string(text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray())) ?? string.Empty;
}
