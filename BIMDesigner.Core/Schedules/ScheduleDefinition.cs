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

    /// <summary>Which schedule this is, whatever it is renamed: what a sheet placing it holds on to.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

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
    /// Whether every element is a row of its own, or those alike in every field but the counted
    /// ones are one row with how many there are - "1 Kitchen, Chair - Dining, 4" - as Revit's
    /// Itemize Every Instance, turned off, does. Rows that stand for several cannot be typed into.
    /// </summary>
    public bool Itemize { get; set; } = true;

    /// <summary>A field every schedule can show: how many elements a row stands for.</summary>
    public const string CountField = "Count";

    /// <summary>A field every schedule can show: the cost of what a row stands for - the type's cost, times how many.</summary>
    public const string TotalCostField = "Total Cost";

    /// <summary>
    /// Numeric fields that must not be totalled, because their sum is not a quantity.
    ///
    /// Adding up five wall heights gives 15 m of nothing; adding up door widths gives a
    /// number no one ordered. A schedule that prints a meaningless total teaches its reader
    /// to distrust the ones that do mean something.
    /// </summary>
    public HashSet<string> NotTotalled { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The values worked out rather than read: each a column, named among <see cref="Fields"/>
    /// where it is to be shown, worked out in this order - so one may use those before it.
    /// </summary>
    public List<CalculatedField> Calculated { get; } = new();

    /// <summary>Cells coloured where they pass a test; the first that passes for a cell colours it.</summary>
    public List<ConditionalFormat> Formats { get; } = new();

    /// <summary>Whether the colours are printed on a sheet too, or only shown while working in the schedule.</summary>
    public bool ShowFormatsOnSheets { get; set; }

    /// <summary>The calculated field of a name, or null where the field is read from the elements.</summary>
    public CalculatedField? CalculatedNamed(string field) =>
        Calculated.FirstOrDefault(calculated => calculated.Name.Equals(field, StringComparison.OrdinalIgnoreCase));

    /// <summary>A copy to change apart from this one - a new schedule under another name.</summary>
    public ScheduleDefinition Copy(string name)
    {
        var copy = new ScheduleDefinition(name, Category, Fields.ToArray())
        {
            SortBy = SortBy, SortDescending = SortDescending, GroupBy = GroupBy, ShowTotals = ShowTotals,
            Itemize = Itemize, ShowFormatsOnSheets = ShowFormatsOnSheets
        };
        copy.Filters.AddRange(Filters.Select(filter => new ScheduleFilter(filter.Field, filter.Comparison, filter.Value)));
        foreach (var field in NotTotalled) copy.NotTotalled.Add(field);
        copy.Calculated.AddRange(Calculated.Select(calculated => calculated.Copy()));
        copy.Formats.AddRange(Formats.Select(format => format.Copy()));
        return copy;
    }

    /// <summary>Everything this definition says, made the same as another's - for undoing an edit to it.</summary>
    public void CopyFrom(ScheduleDefinition other)
    {
        (Name, Category, SortBy, SortDescending, GroupBy, ShowTotals, Itemize, ShowFormatsOnSheets) =
            (other.Name, other.Category, other.SortBy, other.SortDescending, other.GroupBy, other.ShowTotals, other.Itemize, other.ShowFormatsOnSheets);
        Fields.Clear();
        Fields.AddRange(other.Fields);
        Filters.Clear();
        Filters.AddRange(other.Filters.Select(filter => new ScheduleFilter(filter.Field, filter.Comparison, filter.Value)));
        NotTotalled.Clear();
        foreach (var field in other.NotTotalled) NotTotalled.Add(field);
        Calculated.Clear();
        Calculated.AddRange(other.Calculated.Select(calculated => calculated.Copy()));
        Formats.Clear();
        Formats.AddRange(other.Formats.Select(format => format.Copy()));
    }

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
            .WithoutTotals("Thickness", "Length", "Height"),

        // Every stair: how it is set out, and whether it meets the regulations it is checked against.
        new ScheduleDefinition("Stair Schedule", BuiltInCategory.Stairs,
                "Mark", "Type Name", "Base Level", "Top Level", "Shape", "Width", "Risers", "Riser Height", "Actual Going", "Pitch", "Regulations")
            {
                SortBy = "Mark"
            }
            .WithoutTotals("Width", "Risers", "Riser Height", "Actual Going", "Pitch"),

        // Every ramp: how steep and wide, how high it rises and in how many flights, and whether it meets the regulations.
        new ScheduleDefinition("Ramp Schedule", BuiltInCategory.Ramps,
                "Mark", "Type Name", "Base Level", "Width", "Gradient (1 in)", "Rise", "Flights", "Length", "Outside", "Regulations")
            {
                SortBy = "Mark"
            }
            .WithoutTotals("Width", "Gradient (1 in)", "Rise", "Flights"),

        // Every pad graded into the ground: what it is laid as, how high, how big, how it falls, and whether it sits right against the building.
        new ScheduleDefinition("Pad Schedule", BuiltInCategory.Pads,
                "Mark", "Surface", "Level", "Height Offset From Level", "Area", "Fall (1 in, 0 for level)", "Regulations")
            {
                SortBy = "Mark"
            }
            .WithoutTotals("Height Offset From Level", "Fall (1 in, 0 for level)"),

        // The ground laid out: each region by what it is laid as, and how big, for the landscape's quantities.
        new ScheduleDefinition("Landscape Schedule", BuiltInCategory.Topography,
                "Surface", "Material", "Area", "Steepest Gradient", "Raised By (minus for sunk)", "Regulations")
            {
                SortBy = "Surface",
                GroupBy = "Surface",
                Filters = { new ScheduleFilter("Surface", ScheduleOperator.IsNotEmpty) }
            }
            .WithoutTotals("Raised By (minus for sunk)"),

        // Every railing: how long and high it is, whether it is outside, and whether it meets its regulations there.
        new ScheduleDefinition("Railing Schedule", BuiltInCategory.Railings,
                "Mark", "Type Name", "Base Level", "Length", "Height", "Outside", "Regulations")
            {
                SortBy = "Base Level"
            }
            .WithoutTotals("Height"),

        // Every structural column and footing: what it carries, and whether it meets its rules.
        new ScheduleDefinition("Structural Column Schedule", BuiltInCategory.StructuralColumns,
                "Mark", "Type Name", "Base Level", "Top Level", "Load Carried (kN)", "Regulations")
            {
                SortBy = "Type Name"
            },
        new ScheduleDefinition("Structural Foundation Schedule", BuiltInCategory.StructuralFoundations,
                "Mark", "Type Name", "Carries", "Depth below Ground", "Volume", "Regulations")
            {
                SortBy = "Type Name"
            }
            .WithoutTotals("Depth below Ground"),

        // The services' pipes, system by system: what each carries, its type and size, how long it is and what it costs by the metre.
        new ScheduleDefinition("Pipe Schedule", BuiltInCategory.Pipes,
                "System", "System Type", "Type Name", "Size", "Length", "Cost")
            {
                SortBy = "Size",
                GroupBy = "System"
            },

        // The ducts, system by system: what each carries, its type and size, how long it is, what keeps a fire wall it passes and what it costs.
        new ScheduleDefinition("Duct Schedule", BuiltInCategory.Ducts,
                "System", "System Type", "Type Name", "Size", "Length", "Fire Protection", "Cost")
            {
                SortBy = "Size",
                GroupBy = "System"
            },

        // The circuits, board by board: each one's use, breaker, cable and how it is installed, how long, what it is
        // designed for and its cable carries, its voltage drop, and what is wanting.
        new ScheduleDefinition("Board Schedule", BuiltInCategory.ElectricalCircuits,
                "System Name", "Circuit Use", "Source", "Breaker (A)", "Cable", "Installation Method", "Cable Length (m)",
                "Design Current (A)", "Cable Carries (A)", "Voltage Drop (%)", "Wanting")
            {
                SortBy = "System Name",
                GroupBy = "Source",
                Filters = { new ScheduleFilter("System Type", ScheduleOperator.Equals, "Power") }
            },

        // The drains' access points and outfalls: each one's cover, the invert of the drain under it, and how deep.
        new ScheduleDefinition("Manhole Schedule", BuiltInCategory.Drainage,
                "Mark", "Type Name", "Cover Level", "Invert Level", "Depth to Invert", "Cost")
            {
                SortBy = "Mark"
            },

        // Every beam: its section, how long it is and what it weighs, and whether it clears the doors under it.
        new ScheduleDefinition("Structural Framing Schedule", BuiltInCategory.StructuralFraming,
                "Mark", "Type Name", "Reference Level", "Structural Usage", "Length", "Mass", "Clearances")
            {
                SortBy = "Type Name"
            },

        // What is in each room, room by room - how many of each, what each costs and what they
        // come to: the furniture, the kitchen units and appliances, and the sanitary fittings;
        // the lights, sockets, switches, heating and fans, the alarms and the consumer unit.
        RoomByRoom("Furniture Schedule", BuiltInCategory.Furniture),
        RoomByRoom("Casework Schedule", BuiltInCategory.Casework),
        RoomByRoom("Appliance Schedule", BuiltInCategory.SpecialtyEquipment),
        RoomByRoom("Plumbing Fixture Schedule", BuiltInCategory.PlumbingFixtures),
        RoomByRoom("Lighting Fixture Schedule", BuiltInCategory.LightingFixtures),
        RoomByRoom("Electrical Fixture Schedule", BuiltInCategory.ElectricalFixtures),
        RoomByRoom("Lighting Device Schedule", BuiltInCategory.LightingDevices),
        RoomByRoom("Electrical Equipment Schedule", BuiltInCategory.ElectricalEquipment),
        RoomByRoom("Mechanical Equipment Schedule", BuiltInCategory.MechanicalEquipment),
        RoomByRoom("Fire Alarm Device Schedule", BuiltInCategory.FireAlarmDevices),

        // The drawing set: every sheet by its number, with its revision and when it was issued -
        // the list that goes on the front sheet, kept from the sheets themselves.
        new ScheduleDefinition("Sheet List", BuiltInCategory.Sheets,
                "Sheet Number", "Sheet Name", "Current Revision", "Current Revision Date", "Sheet Issue Date")
            {
                SortBy = "Sheet Number",
                ShowTotals = false
            }
    };

    /// <summary>A schedule counting each type in each room: its unit cost, and the cost of all of them.</summary>
    private static ScheduleDefinition RoomByRoom(string name, BuiltInCategory category) =>
        new ScheduleDefinition(name, category, "Room", "Type Name", "Width", "Depth", "Height", CountField, "Cost", TotalCostField)
            {
                SortBy = "Room",
                GroupBy = "Room",
                Itemize = false
            }
            .WithoutTotals("Width", "Depth", "Height", "Cost");
}
