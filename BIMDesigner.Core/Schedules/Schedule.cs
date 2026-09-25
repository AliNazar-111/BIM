using System.Globalization;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Schedules;

/// <summary>One column of a schedule, and what kind of value it holds.</summary>
public sealed class ScheduleColumn
{
    public required string Field { get; init; }

    public required ParameterDataType DataType { get; init; }

    /// <summary>Whether the schedule asked for this column to be totalled.</summary>
    public bool Totalled { get; init; } = true;

    /// <summary>
    /// Whether the column is totalled: it has to hold a quantity, and the schedule has to
    /// want it totalled. Summing level names is arithmetic on things that are not numbers;
    /// summing wall heights is arithmetic on numbers that are not a quantity.
    /// </summary>
    public bool IsTotalled => Totalled && DataType
        is ParameterDataType.Length
        or ParameterDataType.Area
        or ParameterDataType.Volume
        or ParameterDataType.Number
        or ParameterDataType.Integer
        or ParameterDataType.Currency;

    /// <summary>True when nothing in this column can be edited from the schedule.</summary>
    public required bool IsReadOnly { get; init; }
}

/// <summary>
/// One row: an element, and the parameters the schedule asked for.
///
/// The cells are the element's own live parameters, not copies. That is what gives a
/// schedule the bidirectional editing section 6.4 calls for: typing a new fire rating into
/// the table writes it to the wall, because the cell *is* the wall's parameter.
/// </summary>
public sealed class ScheduleRow
{
    public required Element Element { get; init; }

    public required IReadOnlyDictionary<string, ParameterValue> Cells { get; init; }

    public ParameterValue? this[string field] =>
        Cells.TryGetValue(field, out var parameter) ? parameter : null;

    public string Text(string field) => this[field]?.DisplayValue ?? string.Empty;
}

/// <summary>A group of rows under a shared heading, with its own subtotals.</summary>
public sealed class ScheduleGroup
{
    public required string Heading { get; init; }

    public required IReadOnlyList<ScheduleRow> Rows { get; init; }

    public required IReadOnlyDictionary<string, double> Subtotals { get; init; }
}

/// <summary>The answer a schedule definition produces when run against a project.</summary>
public sealed class ScheduleResult
{
    public required ScheduleDefinition Definition { get; init; }

    public required IReadOnlyList<ScheduleColumn> Columns { get; init; }

    public required IReadOnlyList<ScheduleRow> Rows { get; init; }

    public required IReadOnlyList<ScheduleGroup> Groups { get; init; }

    /// <summary>Totals per totalled column, over every row in the schedule.</summary>
    public required IReadOnlyDictionary<string, double> Totals { get; init; }

    public int Count => Rows.Count;
}

/// <summary>
/// Runs a schedule against a project (specification section 6.4).
///
/// Every row is produced by asking an element for its own parameters, which is why a new
/// category needs no work here: a door reports its parameters the same way a wall does, so
/// it schedules the same way. This is what the parameter framework was built for.
/// </summary>
public static class Schedule
{
    public static ScheduleResult Run(BimDocument document, ScheduleDefinition definition)
    {
        var rows = document.Elements
            .Where(element => element.Category == definition.Category)
            .Select(element => new ScheduleRow { Element = element, Cells = ParametersOf(document, element) })
            .Where(row => definition.Filters.All(filter => Passes(row, filter)))
            .ToList();

        rows = Sort(rows, definition);

        var columns = BuildColumns(rows, definition);
        var groups = Group(rows, columns, definition);


        return new ScheduleResult
        {
            Definition = definition,
            Columns = columns,
            Rows = rows,
            Groups = groups,
            Totals = Total(rows, columns)
        };
    }

    /// <summary>
    /// Every parameter an element offers, its own and its type's, indexed by name.
    ///
    /// Where both define a name, the instance wins: a door type's Height is the size it was
    /// made, but if an instance ever reported its own Height that is the one placed in the
    /// building, and the schedule must report what is there.
    /// </summary>
    public static IReadOnlyDictionary<string, ParameterValue> ParametersOf(
        BimDocument document, Element element)
    {
        var map = new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in element.GetInstanceParameters(document))
            map[parameter.Name] = parameter;

        var type = document.ElementTypes.FirstOrDefault(candidate => candidate.Id == element.TypeId);
        if (type is not null)
        {
            foreach (var parameter in type.GetTypeParameters(document))
                map.TryAdd(parameter.Name, parameter);
        }

        return map;
    }

    // ---- columns ---------------------------------------------------------------

    private static List<ScheduleColumn> BuildColumns(
        List<ScheduleRow> rows, ScheduleDefinition definition)
    {
        var columns = new List<ScheduleColumn>();

        foreach (var field in definition.Fields)
        {
            // The first row that has the field decides its type. An empty schedule still
            // shows its columns, as text, so the reader can see what it would report.
            var sample = rows.Select(row => row[field]).FirstOrDefault(cell => cell is not null);

            columns.Add(new ScheduleColumn
            {
                Field = field,
                DataType = sample?.DataType ?? ParameterDataType.Text,
                IsReadOnly = sample?.IsReadOnly ?? true,
                Totalled = !definition.NotTotalled.Contains(field)
            });
        }

        return columns;
    }

    // ---- filtering -------------------------------------------------------------

    private static bool Passes(ScheduleRow row, ScheduleFilter filter)
    {
        var cell = row[filter.Field];
        var text = cell?.DisplayValue ?? string.Empty;

        return filter.Comparison switch
        {
            ScheduleOperator.Equals => text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase),
            ScheduleOperator.NotEquals => !text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase),
            ScheduleOperator.Contains => text.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            ScheduleOperator.IsEmpty => string.IsNullOrWhiteSpace(text),
            ScheduleOperator.IsNotEmpty => !string.IsNullOrWhiteSpace(text),
            ScheduleOperator.GreaterThan => Compare(cell, filter.Value) > 0,
            ScheduleOperator.LessThan => Compare(cell, filter.Value) < 0,
            _ => true
        };
    }

    /// <summary>
    /// Compares numerically where both sides are numbers, and alphabetically otherwise, so
    /// "greater than 2000" on a length means what it says rather than comparing "2" to "5".
    /// </summary>
    private static int Compare(ParameterValue? cell, string value)
    {
        if (Numeric(cell) is { } number &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            return number.CompareTo(threshold);
        }

        return string.Compare(cell?.DisplayValue ?? string.Empty, value, StringComparison.OrdinalIgnoreCase);
    }

    // ---- sorting and grouping --------------------------------------------------

    private static List<ScheduleRow> Sort(List<ScheduleRow> rows, ScheduleDefinition definition)
    {
        var field = definition.SortBy;
        if (string.IsNullOrWhiteSpace(field)) return rows;

        // Numbers sort as numbers; everything else as text. Sorting lengths alphabetically
        // would put 10 m before 2 m, which is exactly the kind of error a schedule hides.
        var ordered = rows.Any(row => Numeric(row[field]) is not null)
            ? rows.OrderBy(row => Numeric(row[field]) ?? double.MaxValue)
            : rows.OrderBy(row => row.Text(field), StringComparer.OrdinalIgnoreCase)
                  .ThenBy(_ => 0);

        var result = definition.SortDescending ? ordered.Reverse() : ordered;
        return result.ToList();
    }

    private static List<ScheduleGroup> Group(
        List<ScheduleRow> rows, List<ScheduleColumn> columns, ScheduleDefinition definition)
    {
        var field = definition.GroupBy;
        if (string.IsNullOrWhiteSpace(field)) return new List<ScheduleGroup>();

        return rows
            .GroupBy(row => row.Text(field))
            .Select(group => new ScheduleGroup
            {
                Heading = string.IsNullOrWhiteSpace(group.Key) ? "(none)" : group.Key,
                Rows = group.ToList(),
                Subtotals = Total(group.ToList(), columns)
            })
            .OrderBy(group => group.Heading, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- totals ----------------------------------------------------------------

    private static Dictionary<string, double> Total(
        IReadOnlyList<ScheduleRow> rows, IReadOnlyList<ScheduleColumn> columns)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns.Where(column => column.IsTotalled))
            totals[column.Field] = rows.Sum(row => Numeric(row[column.Field]) ?? 0);

        return totals;
    }

    /// <summary>The numeric value behind a cell, or null when it does not hold a number.</summary>
    public static double? Numeric(ParameterValue? cell) => cell?.Value switch
    {
        double value => value,
        int value => value,
        decimal value => (double)value,
        _ => null
    };
}
