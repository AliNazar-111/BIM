using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Schedules;

/// <summary>How a filter compares a parameter against a value (specification section 6.4).</summary>
public enum ScheduleOperator
{
    Equals,
    NotEquals,
    Contains,
    GreaterThan,
    LessThan,
    IsEmpty,
    IsNotEmpty
}

/// <summary>One rule narrowing a schedule to the rows worth reporting.</summary>
public sealed class ScheduleFilter
{
    public ScheduleFilter(string field, ScheduleOperator comparison, string value = "")
    {
        Field = field;
        Comparison = comparison;
        Value = value;
    }

    public string Field { get; set; }

    public ScheduleOperator Comparison { get; set; }

    public string Value { get; set; }
}

/// <summary>
/// What a schedule reports: which category, which fields, in what order
/// (specification section 6.4).
///
/// This is a saved question, not an answer. Nothing about the building is stored here - the
/// rows are produced from the model whenever the schedule is opened, which is what makes a
/// schedule trustworthy. A stored table would be a snapshot of the building as it was when
/// someone last remembered to refresh it.
/// </summary>
public sealed class ScheduleDefinition
{
    public ScheduleDefinition(string name, BuiltInCategory category, params string[] fields)
    {
        Name = name;
        Category = category;
        Fields = fields.ToList();
    }

    public string Name { get; set; }

    public BuiltInCategory Category { get; set; }

    /// <summary>Parameter names, in the order the columns should appear.</summary>
    public List<string> Fields { get; }

    public List<ScheduleFilter> Filters { get; } = new();

    /// <summary>Field to sort by. Null leaves the rows in model order.</summary>
    public string? SortBy { get; set; }

    public bool SortDescending { get; set; }

    /// <summary>
    /// Field to group rows under. Grouping is what turns a list into a document: "doors by
    /// level", "walls by type", each with its own subtotal.
    /// </summary>
    public string? GroupBy { get; set; }

    /// <summary>Whether to total the numeric columns.</summary>
    public bool ShowTotals { get; set; } = true;

    /// <summary>
    /// Numeric fields that must not be totalled, because their sum is not a quantity.
    ///
    /// Adding up five wall heights gives 15 m of nothing; adding up door widths gives a
    /// number no one ordered. A schedule that prints a meaningless total teaches its reader
    /// to distrust the ones that do mean something.
    /// </summary>
    public HashSet<string> NotTotalled { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks fields as not worth totalling, and returns the definition for chaining.</summary>
    public ScheduleDefinition WithoutTotals(params string[] fields)
    {
        foreach (var field in fields) NotTotalled.Add(field);
        return this;
    }

    public override string ToString() => Name;

    /// <summary>
    /// The schedules a project starts with. These are the ones a drawing set actually
    /// contains, which is why they are the defaults rather than an empty list.
    /// </summary>
    public static IReadOnlyList<ScheduleDefinition> Defaults() => new[]
    {
        new ScheduleDefinition("Wall Schedule", BuiltInCategory.Walls,
                "Mark", "Type Name", "Length", "Height", "Area", "Volume", "Fire Rating", "Cost")
            {
                SortBy = "Type Name",
                GroupBy = "Type Name"
            }
            // A run of wall has a total length; it does not have a total height.
            .WithoutTotals("Height"),

        new ScheduleDefinition("Door Schedule", BuiltInCategory.Doors,
                "Mark", "Type Name", "Width", "Height", "Operation", "Fire Rating", "Level", "Cost")
            {
                SortBy = "Mark"
            }
            .WithoutTotals("Width", "Height"),

        new ScheduleDefinition("Window Schedule", BuiltInCategory.Windows,
                "Mark", "Type Name", "Width", "Height", "Sill Height", "Level", "Cost")
            {
                SortBy = "Mark"
            }
            .WithoutTotals("Width", "Height", "Sill Height"),

        new ScheduleDefinition("Room Schedule", BuiltInCategory.Rooms,
                "Number", "Name", "Department", "Area", "Perimeter", "Volume", "Occupant Count",
                "Floor Finish", "Ceiling Finish")
            {
                SortBy = "Number"
            },

        new ScheduleDefinition("Floor Schedule", BuiltInCategory.Floors,
                "Mark", "Type Name", "Area", "Thickness", "Volume", "Level")
            {
                SortBy = "Type Name",
                GroupBy = "Type Name"
            }
            .WithoutTotals("Thickness"),

        new ScheduleDefinition("Grid Schedule", BuiltInCategory.Grids, "Name", "Length")
        {
            SortBy = "Name"
        },

        // The pieces walls are built of, as they are ordered: a part taken out is not built.
        new ScheduleDefinition("Part Schedule", BuiltInCategory.Parts,
                "Mark", "Original Type", "Construction", "Material", "Thickness", "Length", "Height", "Area", "Volume")
            {
                SortBy = "Material",
                GroupBy = "Material",
                Filters = { new ScheduleFilter("Excluded", ScheduleOperator.NotEquals, "Yes") }
            }
            .WithoutTotals("Thickness", "Length", "Height")
    };
}
