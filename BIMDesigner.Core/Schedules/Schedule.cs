using System.Globalization;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
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
        or ParameterDataType.Currency
        or ParameterDataType.Percentage;

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

    /// <summary>Why a calculated field could not be worked out, by its name: the first row it failed on.</summary>
    public IReadOnlyDictionary<string, string> Problems { get; init; } = new Dictionary<string, string>();

    public int Count => Rows.Count;

    /// <summary>The colour a cell is given by the schedule's conditional formatting, or null.</summary>
    public ColourRgb? BackgroundOf(ScheduleRow row, string field)
    {
        var cell = row[field];
        foreach (var format in Definition.Formats)
        {
            if (!format.Field.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
            if (format.Passes(cell?.DataType ?? ParameterDataType.Text, Schedule.Numeric(cell), row.Text(field))) return format.Background;
        }

        return null;
    }
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
        // Each room traced once for the whole schedule, however many rows ask which room they are in.
        using var rooms = new Architecture.RoomsOf.Outlines();

        // Filters on what the elements hold first; on what is worked out from it once it is.
        var read = definition.Filters.Where(filter => definition.CalculatedNamed(filter.Field) is null).ToList();
        var worked = definition.Filters.Except(read).ToList();

        var rows = document.Elements
            .Where(element => element.Category == definition.Category)
            .Select(element =>
            {
                // What it costs: its quantity at its type's rate - each, by the metre, by the square metre.
                var cells = ParametersOf(document, element);
                return new ScheduleRow { Element = element, Cells = Counted(cells, 1, Coordination.CostPlanning.CostOf(document, element)) };
            })
            .Where(row => read.All(filter => Passes(row, filter)))
            .ToList();

        // Not itemised: those alike in every field but the counted ones made one row, counted.
        if (!definition.Itemize) rows = Combined(rows, definition);

        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (definition.Calculated.Count > 0)
        {
            rows = Calculate(rows, definition, problems);
            rows = rows.Where(row => worked.All(filter => Passes(row, filter))).ToList();
        }

        rows = Sort(rows, definition);

        var columns = BuildColumns(rows, definition);
        if (!definition.Itemize) columns = columns.Select(column => new ScheduleColumn
        {
            Field = column.Field, DataType = column.DataType, Totalled = column.Totalled, IsReadOnly = true
        }).ToList();
        var groups = Group(rows, columns, definition);


        return new ScheduleResult
        {
            Definition = definition,
            Columns = columns,
            Rows = rows,
            Groups = groups,
            Totals = Total(rows, columns),
            Problems = problems
        };
    }

    // ---- calculated fields -----------------------------------------------------

    /// <summary>What a calculated field holds, as a parameter's kind.</summary>
    public static ParameterDataType DataTypeOf(CalculatedKind kind) => kind switch
    {
        CalculatedKind.Length => ParameterDataType.Length,
        CalculatedKind.Area => ParameterDataType.Area,
        CalculatedKind.Volume => ParameterDataType.Volume,
        CalculatedKind.Currency => ParameterDataType.Currency,
        CalculatedKind.YesNo => ParameterDataType.YesNo,
        CalculatedKind.Percentage => ParameterDataType.Percentage,
        _ => ParameterDataType.Number
    };

    /// <summary>A cell as a formula reads it: its number, Yes as 1 and No as 0, or text that is a number.</summary>
    private static double? FormulaValue(ParameterValue? cell) => cell?.Value switch
    {
        bool flag => flag ? 1 : 0,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => Numeric(cell)
    };

    /// <summary>
    /// Each row given its calculated fields, in order: a formula over the row's fields and those
    /// worked out before it; or a field's share of its total - the schedule's, or its group's.
    /// A cell that cannot be worked out is left without a value, and why kept for the first.
    /// </summary>
    private static List<ScheduleRow> Calculate(List<ScheduleRow> rows, ScheduleDefinition definition, Dictionary<string, string> problems)
    {
        var cells = rows.Select(row => new Dictionary<string, ParameterValue>(row.Cells, StringComparer.OrdinalIgnoreCase)).ToList();

        foreach (var calculated in definition.Calculated)
        {
            if (string.IsNullOrWhiteSpace(calculated.Name)) continue;
            var parameter = new ParameterDefinition(calculated.Name, DataTypeOf(calculated.Kind), ParameterBinding.Instance, ParameterGroup.Other);

            if (calculated.Kind == CalculatedKind.Percentage)
            {
                // Shares of the whole schedule, or of each group under its heading.
                var groupBy = calculated.ByGroup ? definition.GroupBy : null;
                string GroupOf(Dictionary<string, ParameterValue> row) =>
                    string.IsNullOrWhiteSpace(groupBy) ? string.Empty : row.TryGetValue(groupBy, out var cell) ? cell.DisplayValue : string.Empty;
                var totals = cells
                    .GroupBy(GroupOf)
                    .ToDictionary(group => group.Key, group => group.Sum(row => row.TryGetValue(calculated.PercentageOf, out var cell) ? Numeric(cell) ?? 0 : 0));

                if (cells.Count > 0 && !cells.Any(row => row.ContainsKey(calculated.PercentageOf)))
                    problems.TryAdd(calculated.Name, $"'{calculated.PercentageOf}' is not a field of this schedule.");

                foreach (var row in cells)
                {
                    var total = totals[GroupOf(row)];
                    double? share = row.TryGetValue(calculated.PercentageOf, out var cell) && Numeric(cell) is { } part && Math.Abs(total) > 1e-12
                        ? part / total * 100
                        : null;
                    row[calculated.Name] = ParameterValue.ReadOnly(parameter, () => share);
                }

                continue;
            }

            foreach (var row in cells)
            {
                object? value;
                try
                {
                    var result = ScheduleFormula.Evaluate(calculated.Formula, row.Keys, name => FormulaValue(row.GetValueOrDefault(name)));
                    value = calculated.Kind switch
                    {
                        CalculatedKind.YesNo => Math.Abs(result) > 1e-12,
                        CalculatedKind.Currency => (decimal)result,
                        _ => result
                    };
                }
                catch (FormulaException problem)
                {
                    problems.TryAdd(calculated.Name, problem.Message);
                    value = null;
                }
                catch (OverflowException)
                {
                    problems.TryAdd(calculated.Name, "The answer is too large to hold.");
                    value = null;
                }

                row[calculated.Name] = ParameterValue.ReadOnly(parameter, () => value);
            }
        }

        return rows.Select((row, index) => new ScheduleRow { Element = row.Element, Cells = cells[index] }).ToList();
    }

    /// <summary>
    /// Every field a schedule of a category can show: each parameter its elements in the project
    /// have, and how many a row stands for and what they cost.
    /// </summary>
    public static IReadOnlyList<string> AvailableFields(BimDocument document, BuiltInCategory category)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new HashSet<Guid>();
        foreach (var element in document.Elements.Where(element => element.Category == category))
        {
            // One of each type is enough to know what its elements report.
            if (element.TypeId != Guid.Empty && !types.Add(element.TypeId)) continue;
            foreach (var name in ParametersOf(document, element).Keys) names.Add(name);
        }

        names.Add(ScheduleDefinition.CountField);
        names.Add(ScheduleDefinition.TotalCostField);
        return names.ToList();
    }

    private static readonly ParameterDefinition CountDefinition =
        new(ScheduleDefinition.CountField, ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Other);

    private static readonly ParameterDefinition TotalCostDefinition =
        new(ScheduleDefinition.TotalCostField, ParameterDataType.Currency, ParameterBinding.Instance, ParameterGroup.Other);

    /// <summary>The cost a row's element carries - its type's - or nothing.</summary>
    private static decimal Cost(IReadOnlyDictionary<string, ParameterValue> cells) =>
        cells.TryGetValue("Cost", out var cost) && Numeric(cost) is { } value ? (decimal)value : 0m;

    /// <summary>A row's cells with how many it stands for and what they cost added.</summary>
    private static IReadOnlyDictionary<string, ParameterValue> Counted(IReadOnlyDictionary<string, ParameterValue> cells, int count, decimal cost)
    {
        var counted = new Dictionary<string, ParameterValue>(cells, StringComparer.OrdinalIgnoreCase)
        {
            [ScheduleDefinition.CountField] = ParameterValue.ReadOnly(CountDefinition, () => count),
            [ScheduleDefinition.TotalCostField] = ParameterValue.ReadOnly(TotalCostDefinition, () => cost)
        };
        return counted;
    }

    /// <summary>
    /// Rows alike in every field the schedule shows but the counted ones made one: the first of
    /// them, with how many there are and what they all cost.
    /// </summary>
    private static List<ScheduleRow> Combined(List<ScheduleRow> rows, ScheduleDefinition definition)
    {
        var alike = definition.Fields
            .Where(field => field is not (ScheduleDefinition.CountField or ScheduleDefinition.TotalCostField))
            .ToList();

        return rows
            .GroupBy(row => string.Join("\u001F", alike.Select(row.Text)))
            .Select(group =>
            {
                var first = group.First();
                var cost = group.Sum(row => Numeric(row[ScheduleDefinition.TotalCostField]) is { } each ? (decimal)each : 0m);
                return new ScheduleRow { Element = first.Element, Cells = Counted(first.Cells, group.Count(), cost) };
            })
            .ToList();
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
            foreach (var parameter in type.AllTypeParameters(document))
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
            var calculated = definition.CalculatedNamed(field);

            columns.Add(new ScheduleColumn
            {
                Field = field,
                DataType = sample?.DataType ?? (calculated is null ? ParameterDataType.Text : DataTypeOf(calculated.Kind)),
                IsReadOnly = sample?.IsReadOnly ?? true,

                // Shares of each group add up to a hundred in each, and to nothing over them all.
                Totalled = !definition.NotTotalled.Contains(field) && calculated is not { Kind: CalculatedKind.Percentage, ByGroup: true }
            });
        }

        return columns;
    }

    // ---- filtering -------------------------------------------------------------

    private static bool Passes(ScheduleRow row, ScheduleFilter filter) => Passes(row.Cells, filter);

    /// <summary>Whether something's parameters pass a filter's test: a schedule's, or a view filter's rule.</summary>
    public static bool Passes(IReadOnlyDictionary<string, ParameterValue> cells, ScheduleFilter filter)
    {
        var cell = cells.TryGetValue(filter.Field, out var found) ? found : null;
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

    /// <summary>A total written in its column's own units.</summary>
    public static string TotalText(ScheduleColumn column, double value) => column.DataType switch
    {
        ParameterDataType.Length => Units.FormatLength(value),
        ParameterDataType.Area => Units.FormatArea(value),
        ParameterDataType.Volume => Units.FormatVolume(value),
        ParameterDataType.Integer => ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture),
        ParameterDataType.Currency => value.ToString("0.00", CultureInfo.InvariantCulture),
        ParameterDataType.Percentage => value.ToString("0.##", CultureInfo.InvariantCulture) + "%",
        _ => value.ToString("0.###", CultureInfo.InvariantCulture)
    };

    /// <summary>The numeric value behind a cell, or null when it does not hold a number.</summary>
    public static double? Numeric(ParameterValue? cell) => cell?.Value switch
    {
        double value => value,
        int value => value,
        decimal value => (double)value,
        _ => null
    };
}
