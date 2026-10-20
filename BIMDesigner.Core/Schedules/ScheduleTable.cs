using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Schedules;

/// <summary>What a line of a schedule's table is.</summary>
public enum ScheduleLineKind
{
    Title,
    Header,
    GroupHeading,
    Row,
    Subtotal,
    Total
}

/// <summary>One line of a schedule's table: its cells' text, their colours where formatting gives them one, and its depth on the paper.</summary>
public sealed record ScheduleTableLine(ScheduleLineKind Kind, IReadOnlyList<string> Cells, IReadOnlyList<ColourRgb?> Backgrounds, double Height)
{
    /// <summary>Whether it is one text across the table: the title, a group's heading.</summary>
    public bool Spans => Kind is ScheduleLineKind.Title or ScheduleLineKind.GroupHeading;
}

/// <summary>
/// A schedule laid out as a table on paper (specification section 6.5): its title, its fields'
/// names over their columns, its rows under their groups' headings, subtotals and its total -
/// sized in paper millimetres, each column as wide as what it holds. Drawn on a sheet as it is,
/// at no scale: a table is the size its text is.
/// </summary>
public sealed class ScheduleTable
{
    /// <summary>Text in the rows, paper millimetres high.</summary>
    public const double TextSize = 2.5;

    /// <summary>The title's text.</summary>
    public const double TitleSize = 3.5;

    /// <summary>Room each side of a cell's text.</summary>
    public const double Padding = 1.5;

    public required string Title { get; init; }

    public required IReadOnlyList<double> ColumnWidths { get; init; }

    /// <summary>Which columns hold numbers, and are set to the right.</summary>
    public required IReadOnlyList<bool> Numeric { get; init; }

    public required IReadOnlyList<ScheduleTableLine> Lines { get; init; }

    public double Width => ColumnWidths.Sum();

    public double Height => Lines.Sum(line => line.Height);

    /// <summary>The project's schedule laid out as it now reports, its colours as it says it prints them.</summary>
    public static ScheduleTable Of(BimDocument document, ScheduleDefinition definition) =>
        Of(Schedule.Run(document, definition), definition.ShowFormatsOnSheets);

    /// <summary>A schedule's answer laid out, with or without its colours.</summary>
    public static ScheduleTable Of(ScheduleResult result, bool withFormats)
    {
        var definition = result.Definition;
        var columns = result.Columns;
        var none = columns.Select(_ => (ColourRgb?)null).ToList();
        var lines = new List<ScheduleTableLine>
        {
            new(ScheduleLineKind.Title, new[] { definition.Name }, none, TitleSize * 2),
            new(ScheduleLineKind.Header, columns.Select(column => column.Field).ToList(), none, TextSize * 2.4)
        };

        ScheduleTableLine Row(ScheduleRow row) => new(
            ScheduleLineKind.Row,
            columns.Select(column => row.Text(column.Field)).ToList(),
            withFormats ? columns.Select(column => result.BackgroundOf(row, column.Field)).ToList() : none,
            TextSize * 2);

        // A line of totals: its label in the first column without a total of its own.
        var label = columns.Select((column, index) => (column, index)).FirstOrDefault(entry => !entry.column.IsTotalled).index;
        var labelled = columns.Count > 0 && !columns[label].IsTotalled;
        ScheduleTableLine Totals(ScheduleLineKind kind, IReadOnlyDictionary<string, double> totals, string text) => new(
            kind,
            columns.Select((column, index) =>
                column.IsTotalled && totals.TryGetValue(column.Field, out var value) ? Schedule.TotalText(column, value)
                : labelled && index == label ? text
                : string.Empty).ToList(),
            none,
            TextSize * 2);

        if (result.Groups.Count > 0)
        {
            foreach (var group in result.Groups)
            {
                lines.Add(new ScheduleTableLine(ScheduleLineKind.GroupHeading, new[] { group.Heading }, none, TextSize * 2));
                lines.AddRange(group.Rows.Select(Row));
                if (definition.ShowTotals) lines.Add(Totals(ScheduleLineKind.Subtotal, group.Subtotals, $"{group.Rows.Count}"));
            }
        }
        else
        {
            lines.AddRange(result.Rows.Select(Row));
        }

        if (definition.ShowTotals) lines.Add(Totals(ScheduleLineKind.Total, result.Totals, $"Total: {result.Count}"));

        // Each column as wide as the widest it holds, its heading included.
        var widths = columns.Select((_, index) => lines
                .Where(line => !line.Spans && index < line.Cells.Count)
                .Select(line => TextWidth(line.Cells[index], TextSize))
                .DefaultIfEmpty(0)
                .Max() + Padding * 2)
            .ToList();

        // And all of them wide enough for the title and the group headings.
        var spanning = lines.Where(line => line.Spans)
            .Select(line => TextWidth(line.Cells[0], line.Kind == ScheduleLineKind.Title ? TitleSize : TextSize) + Padding * 2)
            .DefaultIfEmpty(0)
            .Max();
        if (widths.Count == 0) widths.Add(spanning);
        else if (widths.Sum() < spanning)
        {
            var extra = (spanning - widths.Sum()) / widths.Count;
            for (var i = 0; i < widths.Count; i++) widths[i] += extra;
        }

        return new ScheduleTable
        {
            Title = definition.Name,
            ColumnWidths = widths,
            Numeric = columns.Select(column => column.DataType is ParameterDataType.Length or ParameterDataType.Area or ParameterDataType.Volume
                or ParameterDataType.Number or ParameterDataType.Integer or ParameterDataType.Currency or ParameterDataType.Percentage or ParameterDataType.Angle).ToList(),
            Lines = lines
        };
    }

    /// <summary>A table of words alone - a legend: its title, its headings and its rows, the columns as wide as they hold.</summary>
    public static ScheduleTable Of(string title, IReadOnlyList<string> headings, IEnumerable<IReadOnlyList<string>> rows)
    {
        var none = headings.Select(_ => (ColourRgb?)null).ToList();
        var lines = new List<ScheduleTableLine>
        {
            new(ScheduleLineKind.Title, new[] { title }, none, TitleSize * 2),
            new(ScheduleLineKind.Header, headings, none, TextSize * 2.4)
        };
        lines.AddRange(rows.Select(row => new ScheduleTableLine(ScheduleLineKind.Row, row, none, TextSize * 2)));

        var widths = headings.Select((_, index) => lines.Where(line => !line.Spans && index < line.Cells.Count)
            .Select(line => TextWidth(line.Cells[index], TextSize)).DefaultIfEmpty(0).Max() + Padding * 2).ToList();
        var spanning = TextWidth(title, TitleSize) + Padding * 2;
        if (widths.Count > 0 && widths.Sum() < spanning) widths[^1] += spanning - widths.Sum();

        return new ScheduleTable { Title = title, ColumnWidths = widths, Numeric = headings.Select(_ => false).ToList(), Lines = lines };
    }

    /// <summary>
    /// About how wide a text is set in the sheets' sans serif at a height: wide enough that a
    /// column is never too narrow for what it holds, and not so wide it wastes the sheet.
    /// </summary>
    public static double TextWidth(string text, double size) => size * text.Sum(c => c switch
    {
        ' ' or 'i' or 'l' or 'j' or 'I' or '.' or ',' or ':' or ';' or '\'' or '|' or '!' or '(' or ')' or '-' or 'f' or 't' or 'r' => 0.32,
        'm' or 'w' or 'M' or 'W' => 0.85,
        >= 'A' and <= 'Z' => 0.66,
        >= '0' and <= '9' => 0.56,
        _ => 0.54
    });
}
