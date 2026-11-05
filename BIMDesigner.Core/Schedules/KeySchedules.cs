using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Schedules;

/// <summary>
/// One key of a key schedule - "Office", "Wet Room" - and the values it gives each field: a row of
/// the schedule, standing in it for the elements it is given to. It is not in the model; it lives in
/// its schedule. Its fields are typed into the schedule as any row's are, and each change is given at
/// once to every element that has the key.
/// </summary>
public sealed class ScheduleKey : Element
{
    public ScheduleKey(ScheduleDefinition schedule, string name)
    {
        Schedule = schedule;
        Name = name;
    }

    /// <summary>The key schedule it is a row of.</summary>
    public ScheduleDefinition Schedule { get; internal set; }

    public override BuiltInCategory Category => Schedule.Category;

    /// <summary>What the key is called: what an element's key parameter is set to.</summary>
    public string Name { get; set; }

    /// <summary>What it gives each field, by the field's name, as typed.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindValidated(KeySchedules.KeyNameDefinition, () => Name, (string name) => KeySchedules.Rename(document, this, name));
        foreach (var field in Schedule.Fields.Where(field => !field.Equals(ScheduleDefinition.KeyNameField, StringComparison.OrdinalIgnoreCase)))
            yield return ParameterValue.Bind(new ParameterDefinition(field, ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData),
                () => Values.GetValueOrDefault(field) ?? string.Empty,
                (string value) =>
                {
                    Values[field] = (value ?? string.Empty).Trim();
                    KeySchedules.Apply(document, this);
                });
    }

    /// <summary>A copy for another schedule: its name and values.</summary>
    public ScheduleKey CopyFor(ScheduleDefinition schedule)
    {
        var copy = new ScheduleKey(schedule, Name) { Id = Id };
        foreach (var (field, value) in Values) copy.Values[field] = value;
        return copy;
    }
}

/// <summary>
/// Key schedules: a schedule whose rows are keys rather than elements - a room style, a door's
/// hardware set - each key giving a value to each of the schedule's fields. Every element of the
/// schedule's category then has the key as a parameter of its own, named as the schedule names its
/// key; set to a key, the element takes that key's values for those fields, and keeps following the
/// key as its values are changed, the fields shown but not typed into while it has the key. Set back
/// to none, the element keeps the values it has and they can be typed into again.
/// </summary>
public static class KeySchedules
{
    /// <summary>What an element shows where it has no key.</summary>
    public const string None = "(none)";

    public static readonly ParameterDefinition KeyNameDefinition =
        new(ScheduleDefinition.KeyNameField, ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    /// <summary>The project's key schedules for a category.</summary>
    public static IEnumerable<ScheduleDefinition> For(BimDocument document, BuiltInCategory category) =>
        document.Schedules.Where(schedule => schedule.IsKeySchedule && schedule.Category == category);

    /// <summary>The key an element has in a key schedule, or null.</summary>
    public static ScheduleKey? KeyOf(Element element, ScheduleDefinition schedule) =>
        element.ParameterValues.GetValueOrDefault(schedule.Id) is string name ? schedule.Keys.FirstOrDefault(key => key.Name == name) : null;

    /// <summary>The fields an element's keys set: shown, not typed into, while it has them.</summary>
    public static IReadOnlySet<string> Locked(BimDocument document, Element element)
    {
        var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schedule in For(document, element.Category))
            if (KeyOf(element, schedule) is not null)
                foreach (var field in schedule.Fields.Where(field => !field.Equals(ScheduleDefinition.KeyNameField, StringComparison.OrdinalIgnoreCase)))
                    locked.Add(field);
        return locked;
    }

    /// <summary>
    /// The fields a key can give the elements of a category: what they hold of their own and can be
    /// typed into - their name, finishes, comments, the project's parameters - not where they are, how
    /// big, their mark, their phases or what a handover records of each one alone.
    /// </summary>
    public static IReadOnlyList<string> KeyableFields(BimDocument document, BuiltInCategory category)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Elements.Where(element => element.Category == category).Take(50))
        foreach (var parameter in element.GetInstanceParameters(document))
            if (!parameter.IsReadOnly && Keyable(parameter) && !document.Schedules.Any(schedule => schedule.IsKeySchedule && schedule.KeyName == parameter.Name))
                names.Add(parameter.Name);
        return names.ToList();
    }

    private static bool Keyable(ParameterValue parameter) =>
        parameter.Name is not ("Mark" or "Number" or "Workset" or "Design Option") &&
        parameter.Group is ParameterGroup.IdentityData or ParameterGroup.MaterialsAndFinishes or ParameterGroup.Other or ParameterGroup.Analytical or ParameterGroup.Structural &&
        parameter.DataType is not (ParameterDataType.Image or ParameterDataType.Material);

    /// <summary>Each key schedule's parameter on an element of its category: the key it has, picked from the schedule's keys.</summary>
    public static IEnumerable<ParameterValue> Parameters(BimDocument document, Element element)
    {
        if (element is ScheduleKey) yield break;
        foreach (var schedule in For(document, element.Category))
        {
            var definition = new ParameterDefinition(schedule.KeyName!, ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
            var choices = new[] { None }.Concat(schedule.Keys.Select(key => key.Name)).ToList();
            yield return ParameterValue.BindChoiceCommand(definition, () => KeyOf(element, schedule)?.Name ?? None,
                name => new KeyCommand(document, schedule, new[] { element }, name == None ? null : name), choices);
        }
    }

    /// <summary>A key's values given to every element that has it.</summary>
    public static void Apply(BimDocument document, ScheduleKey key)
    {
        foreach (var element in document.Elements.Where(element => element.Category == key.Category && KeyOf(element, key.Schedule) == key))
            Give(document, element, key);
    }

    /// <summary>A key's values given to one element, field by field, as typed into its own parameters.</summary>
    private static void Give(BimDocument document, Element element, ScheduleKey key)
    {
        var parameters = element.GetInstanceParameters(document).GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in key.Values)
            if (parameters.TryGetValue(field, out var parameter) && !parameter.IsReadOnly && parameter.DisplayValue != value)
                parameter.TrySetFromText(value);
    }

    /// <summary>A key renamed: the elements that have it keep it. Refused where another key has the name, or it is blank.</summary>
    public static bool Rename(BimDocument document, ScheduleKey key, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0 || name == None || key.Schedule.Keys.Any(other => other != key && other.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var element in document.Elements.Where(element => KeyOf(element, key.Schedule) == key))
            element.ParameterValues[key.Schedule.Id] = name;
        key.Name = name;
        return true;
    }

    /// <summary>A new key added to a key schedule, named for the next number free, as one step.</summary>
    public static IUndoableCommand AddKey(ScheduleDefinition schedule)
    {
        var taken = schedule.Keys.Select(key => key.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Enumerable.Range(1, 9999).Select(i => $"{schedule.KeyName} {i}").First(candidate => !taken.Contains(candidate));
        var key = new ScheduleKey(schedule, name);
        return new DelegateCommand($"New Key {name}", () => schedule.Keys.Add(key), () => schedule.Keys.Remove(key));
    }

    /// <summary>A key taken out of its schedule, as one step: the elements that had it keep its values and have no key.</summary>
    public static IUndoableCommand DeleteKey(BimDocument document, ScheduleKey key)
    {
        var schedule = key.Schedule;
        var index = schedule.Keys.IndexOf(key);
        var had = document.Elements.Where(element => KeyOf(element, schedule) == key).ToList();
        return new DelegateCommand($"Delete Key {key.Name}",
            () =>
            {
                schedule.Keys.Remove(key);
                foreach (var element in had) element.ParameterValues.Remove(schedule.Id);
            },
            () =>
            {
                schedule.Keys.Insert(Math.Clamp(index, 0, schedule.Keys.Count), key);
                foreach (var element in had) element.ParameterValues[schedule.Id] = key.Name;
            });
    }

    /// <summary>
    /// Elements given a key - or none - as one step: what each held in the key's fields before is
    /// kept, so undoing it puts each back as it was.
    /// </summary>
    public sealed class KeyCommand : IUndoableCommand
    {
        private readonly BimDocument _document;
        private readonly ScheduleDefinition _schedule;
        private readonly string? _key;
        private readonly List<(Element Element, string? Was, Dictionary<string, object?> Values)> _before = new();

        public KeyCommand(BimDocument document, ScheduleDefinition schedule, IEnumerable<Element> elements, string? key)
        {
            _document = document;
            _schedule = schedule;
            _key = key;
            foreach (var element in elements)
            {
                var values = element.GetInstanceParameters(document)
                    .Where(parameter => schedule.Fields.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                    .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
                _before.Add((element, element.ParameterValues.GetValueOrDefault(schedule.Id) as string, values));
            }

            Name = key is null ? $"Clear {schedule.KeyName}" : $"Set {schedule.KeyName} to {key}";
        }

        public string Name { get; }

        public void Redo()
        {
            var key = _key is null ? null : _schedule.Keys.FirstOrDefault(each => each.Name == _key);
            foreach (var (element, _, _) in _before)
            {
                if (key is null) element.ParameterValues.Remove(_schedule.Id);
                else
                {
                    element.ParameterValues[_schedule.Id] = key.Name;
                    Give(_document, element, key);
                }
            }
        }

        public void Undo()
        {
            foreach (var (element, was, values) in _before)
            {
                if (was is null) element.ParameterValues.Remove(_schedule.Id);
                else element.ParameterValues[_schedule.Id] = was;
                foreach (var parameter in element.GetInstanceParameters(_document))
                    if (values.TryGetValue(parameter.Name, out var value) && !parameter.IsReadOnly && !Equals(parameter.Value, value))
                        parameter.TrySet(value);
            }
        }
    }
}
