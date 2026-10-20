using System.Globalization;
using System.Text;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Parameters;

/// <summary>
/// A parameter of the project's own (specification section 2.1): a name, a kind of value, and
/// the categories it is on - each of their elements, or each of their types - beside the
/// parameters they have anyway. A shared parameter is one read from a shared parameter file, its
/// GUID kept with it, so the same parameter is the same in every project, schedule and IFC file
/// that has it.
/// </summary>
public sealed class ProjectParameter
{
    /// <summary>Its identity: a shared parameter's GUID, as its file has it.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Parameter";

    public ParameterDataType DataType { get; set; } = ParameterDataType.Text;

    /// <summary>On each element, or on each type.</summary>
    public ParameterBinding Binding { get; set; } = ParameterBinding.Instance;

    /// <summary>Where it is listed in Properties.</summary>
    public ParameterGroup Group { get; set; } = ParameterGroup.IdentityData;

    public HashSet<BuiltInCategory> Categories { get; } = new();

    /// <summary>Whether it came from a shared parameter file, its GUID that file's.</summary>
    public bool Shared { get; set; }

    /// <summary>The group it is in, in its shared parameter file.</summary>
    public string SharedGroup { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public ParameterDefinition Definition => new(Name, DataType, Binding, Group, Shared ? Id : null);

    public ProjectParameter Copy()
    {
        var copy = new ProjectParameter
        {
            Id = Id, Name = Name, DataType = DataType, Binding = Binding, Group = Group, Shared = Shared, SharedGroup = SharedGroup, Description = Description
        };
        copy.Categories.UnionWith(Categories);
        return copy;
    }
}

public static class ProjectParameters
{
    /// <summary>
    /// The project's parameters on something of a category, bound as it binds them - an element's
    /// own, or its type's - each read and written in its values, by the parameter's id.
    /// </summary>
    public static IEnumerable<ParameterValue> For(BimDocument document, BuiltInCategory category, ParameterBinding binding, Dictionary<Guid, object?> values) =>
        document.ProjectParameters
            .Where(parameter => parameter.Binding == binding && parameter.Categories.Contains(category))
            .Select(parameter => ParameterValue.Bind<object?>(parameter.Definition, () => values.GetValueOrDefault(parameter.Id), value =>
            {
                if (value is string { Length: 0 } && parameter.DataType != ParameterDataType.Text) values.Remove(parameter.Id);
                else values[parameter.Id] = value;
            }));

    /// <summary>The kinds of value a shared parameter file names, and what each is here.</summary>
    private static readonly (string File, ParameterDataType Type)[] FileTypes =
    {
        ("TEXT", ParameterDataType.Text), ("MULTILINETEXT", ParameterDataType.MultilineText), ("INTEGER", ParameterDataType.Integer),
        ("NUMBER", ParameterDataType.Number), ("LENGTH", ParameterDataType.Length), ("AREA", ParameterDataType.Area),
        ("VOLUME", ParameterDataType.Volume), ("ANGLE", ParameterDataType.Angle), ("CURRENCY", ParameterDataType.Currency),
        ("YESNO", ParameterDataType.YesNo), ("URL", ParameterDataType.Url), ("MATERIAL", ParameterDataType.Material),
        ("IMAGE", ParameterDataType.Image)
    };

    public static string FileType(ParameterDataType type) => FileTypes.FirstOrDefault(entry => entry.Type == type).File ?? "TEXT";

    /// <summary>
    /// The parameters in a shared parameter file - tab-separated: a META line, GROUP lines of an id
    /// and a name, and PARAM lines of a GUID, name, kind of value, its category, its group's id,
    /// whether it is shown, its description and whether it can be changed. A kind this does not
    /// have - a structural force, a family type - is read as text, and said so.
    /// </summary>
    public static IReadOnlyList<ProjectParameter> ReadSharedFile(string text, out IReadOnlyList<string> problems)
    {
        var groups = new Dictionary<string, string>();
        var parameters = new List<ProjectParameter>();
        var found = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var fields = raw.Split('\t');
            switch (fields[0].Trim())
            {
                case "GROUP" when fields.Length >= 3:
                    groups[fields[1].Trim()] = fields[2].Trim();
                    break;

                case "PARAM" when fields.Length >= 4:
                {
                    if (!Guid.TryParse(fields[1].Trim(), out var id))
                    {
                        found.Add($"{fields[2]} has no GUID that can be read.");
                        continue;
                    }

                    var kind = fields[3].Trim().ToUpperInvariant();
                    var type = FileTypes.FirstOrDefault(entry => entry.File == kind);
                    if (type.File is null) found.Add($"{fields[2].Trim()} is a {kind.ToLowerInvariant()}, read as text.");
                    parameters.Add(new ProjectParameter
                    {
                        Id = id, Name = fields[2].Trim(), DataType = type.File is null ? ParameterDataType.Text : type.Type, Shared = true,
                        SharedGroup = fields.Length > 5 && groups.TryGetValue(fields[5].Trim(), out var group) ? group : string.Empty,
                        Description = fields.Length > 7 ? fields[7].Trim() : string.Empty
                    });
                    break;
                }
            }
        }

        problems = found;
        return parameters;
    }

    /// <summary>Parameters written as a shared parameter file, grouped as they are, in the file's own form.</summary>
    public static string WriteSharedFile(IEnumerable<ProjectParameter> parameters)
    {
        var list = parameters.ToList();
        var groups = list.Select(parameter => string.IsNullOrWhiteSpace(parameter.SharedGroup) ? "BIMDesigner" : parameter.SharedGroup)
            .Distinct(StringComparer.Ordinal).ToList();
        var text = new StringBuilder();
        text.Append("# This is a shared parameter file.\r\n# Do not edit by hand.\r\n");
        text.Append("*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n");
        text.Append("*GROUP\tID\tNAME\r\n");
        for (var i = 0; i < groups.Count; i++) text.Append(CultureInfo.InvariantCulture, $"GROUP\t{i + 1}\t{groups[i]}\r\n");
        text.Append("*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n");
        foreach (var parameter in list)
        {
            var group = groups.IndexOf(string.IsNullOrWhiteSpace(parameter.SharedGroup) ? "BIMDesigner" : parameter.SharedGroup) + 1;
            var description = parameter.Description.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            text.Append(CultureInfo.InvariantCulture, $"PARAM\t{parameter.Id:D}\t{parameter.Name}\t{FileType(parameter.DataType)}\t\t{group}\t1\t{description}\t1\t0\r\n");
        }

        return text.ToString();
    }
}

/// <summary>One entry of a classification: a code and its title - "EF_25_10_25", "External walls".</summary>
public sealed record ClassificationEntry(string Code, string Title);

/// <summary>
/// The classification the project's types and rooms are coded to (specification section 7):
/// Uniclass 2015 to start with, its Elements/functions table's codes for walls, floors, roofs,
/// doors and windows and the like - or any table read from a file of codes and titles, such as
/// NBS's own download of a whole Uniclass table. What a schedule, an IFC file and COBie carry.
/// </summary>
public sealed class ClassificationTable
{
    public string System { get; set; } = "Uniclass 2015";

    public string Edition { get; set; } = string.Empty;

    public List<ClassificationEntry> Entries { get; } = new();

    public ClassificationEntry? Find(string code) =>
        string.IsNullOrWhiteSpace(code) ? null : Entries.FirstOrDefault(entry => entry.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"EF_25_10_25: External walls" - as COBie writes a category; the code alone where the table has no title for it.</summary>
    public string Describe(string code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : Find(code) is { } entry ? $"{entry.Code}: {entry.Title}" : code.Trim();

    /// <summary>
    /// A table read from a file: a code and a title a line, separated by a tab or a comma - the
    /// first line skipped where it is a heading. Quotes round a field are taken off.
    /// </summary>
    public static ClassificationTable Parse(string text, string system)
    {
        var table = new ClassificationTable { System = system };
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var separator = line.Contains('\t') ? '\t' : ',';
            var fields = Split(line, separator);
            if (fields.Count < 2 || fields[0].Length == 0) continue;
            if (fields[0].Equals("code", StringComparison.OrdinalIgnoreCase)) continue;
            if (table.Find(fields[0]) is null) table.Entries.Add(new ClassificationEntry(fields[0], fields[1]));
        }

        return table;
    }

    private static List<string> Split(string line, char separator)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == separator && !quoted)
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
            }
            else field.Append(c);
        }

        fields.Add(field.ToString().Trim());
        return fields;
    }

    public ClassificationTable Copy()
    {
        var copy = new ClassificationTable { System = System, Edition = Edition };
        copy.Entries.AddRange(Entries);
        return copy;
    }

    public void CopyFrom(ClassificationTable other)
    {
        (System, Edition) = (other.System, other.Edition);
        Entries.Clear();
        Entries.AddRange(other.Entries);
    }

    /// <summary>
    /// A start: Uniclass 2015's Elements/functions codes for what a building is drawn with here -
    /// as NBS publishes them. A whole table, its products' and spaces' codes with it, can be read
    /// from NBS's download.
    /// </summary>
    public static ClassificationTable Starter()
    {
        var table = new ClassificationTable { System = "Uniclass 2015", Edition = "Elements/functions (EF)" };
        table.Entries.AddRange(new[]
        {
            new ClassificationEntry("EF_20_05", "Substructure"),
            new ClassificationEntry("EF_20_10", "Superstructure"),
            new ClassificationEntry("EF_25_10", "Walls"),
            new ClassificationEntry("EF_25_10_25", "External walls"),
            new ClassificationEntry("EF_25_10_27", "External walls below DPC"),
            new ClassificationEntry("EF_25_10_28", "External walls below ground"),
            new ClassificationEntry("EF_25_10_30", "Free-standing walls"),
            new ClassificationEntry("EF_25_10_40", "Internal walls"),
            new ClassificationEntry("EF_25_10_60", "Parapet walls"),
            new ClassificationEntry("EF_25_30", "Openings"),
            new ClassificationEntry("EF_25_30_25", "Doors"),
            new ClassificationEntry("EF_25_30_33", "Gates"),
            new ClassificationEntry("EF_25_30_36", "Hatches"),
            new ClassificationEntry("EF_25_30_45", "Louvres"),
            new ClassificationEntry("EF_25_30_95", "Vents"),
            new ClassificationEntry("EF_25_30_97", "Windows"),
            new ClassificationEntry("EF_25_55", "Barriers"),
            new ClassificationEntry("EF_30_05", "Building levels"),
            new ClassificationEntry("EF_30_10", "Roofs"),
            new ClassificationEntry("EF_30_10_04", "Arched roofs"),
            new ClassificationEntry("EF_30_10_18", "Curved roofs"),
            new ClassificationEntry("EF_30_10_23", "Domed roofs"),
            new ClassificationEntry("EF_30_10_30", "Flat roofs"),
            new ClassificationEntry("EF_30_10_64", "Pitched roofs"),
            new ClassificationEntry("EF_30_20", "Floors"),
            new ClassificationEntry("EF_30_20_06", "Basement floors"),
            new ClassificationEntry("EF_30_20_34", "Ground floors"),
            new ClassificationEntry("EF_30_20_52", "Mezzanine floors"),
            new ClassificationEntry("EF_30_20_93", "Upper floors"),
            new ClassificationEntry("EF_30_25", "Ceilings and soffits"),
            new ClassificationEntry("EF_30_30", "Decks"),
            new ClassificationEntry("EF_30_60", "Pavements"),
            new ClassificationEntry("EF_35_10", "Stairs"),
            new ClassificationEntry("EF_35_20", "Ramps"),
            new ClassificationEntry("EF_40", "Signage, fittings, furnishings and equipment"),
            new ClassificationEntry("EF_50", "Waste disposal functions"),
            new ClassificationEntry("EF_55", "Piped supply functions"),
            new ClassificationEntry("EF_60", "Heating, cooling and refrigeration functions"),
            new ClassificationEntry("EF_65", "Ventilation and air conditioning functions"),
            new ClassificationEntry("EF_70", "Electrical power and lighting functions"),
            new ClassificationEntry("EF_75", "Communications, security, safety and protection functions")
        });
        return table;
    }
}
