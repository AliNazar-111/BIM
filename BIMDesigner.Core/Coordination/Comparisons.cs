using System.Collections;
using System.Reflection;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Coordination;

/// <summary>How a thing stands in one issue of a model against the other: there only now, there only before, moved, or changed.</summary>
public enum ChangeKind
{
    Added,
    Removed,
    Moved,
    Changed
}

/// <summary>One setting that is not what it was: its name, and how it read before and now.</summary>
public sealed record SettingChange(string Name, string Before, string After)
{
    public override string ToString() => $"{Name}: {(Before.Length == 0 ? "(none)" : Before)} → {(After.Length == 0 ? "(none)" : After)}";
}

/// <summary>
/// One thing - an element, a type or a level - that is not as it was: what it is, where, how it has
/// changed, and each setting that has. A moved one says how far and which way; one whose type has
/// changed says so.
/// </summary>
public sealed record ElementChange(Guid Id, ChangeKind Kind, string Category, string Name, string Level, string? Movement, IReadOnlyList<SettingChange> Settings, string? TypeChange = null)
{
    /// <summary>What has changed, line by line.</summary>
    public IEnumerable<string> Details()
    {
        if (Kind == ChangeKind.Added) yield return "Not in the issue before";
        if (Kind == ChangeKind.Removed) yield return "No longer in the model";
        if (Movement is not null) yield return Movement;
        foreach (var setting in Settings) yield return setting.ToString();
        if (TypeChange is not null) yield return TypeChange;
    }

    /// <summary>What has changed, in a sentence.</summary>
    public string Summary => string.Join("; ", Details());
}

/// <summary>
/// Two issues of a model set side by side: every element, type and level added, removed, moved or
/// changed from the one before to the one after - matched by its id, which a project keeps from save
/// to save and an IFC file keeps as its GlobalId - and how many are as they were.
/// </summary>
public sealed class ModelComparison
{
    private readonly Dictionary<Guid, ElementChange> _byId;

    public ModelComparison(string before, string after, DateTime at, IReadOnlyList<ElementChange> elements, IReadOnlyList<ElementChange> types, IReadOnlyList<ElementChange> levels, int unchanged, IReadOnlySet<Guid> compared)
    {
        (Before, After, At, Elements, Types, Levels, Unchanged, Compared) = (before, after, at, elements, types, levels, unchanged, compared);
        _byId = elements.GroupBy(change => change.Id).ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>What the issue before is: a file and when it was written, say.</summary>
    public string Before { get; }

    /// <summary>What the issue after is.</summary>
    public string After { get; }

    public DateTime At { get; }

    /// <summary>The elements not as they were, in the order of the model after, then those removed.</summary>
    public IReadOnlyList<ElementChange> Elements { get; }

    /// <summary>The types not as they were: what every element of them has changed with.</summary>
    public IReadOnlyList<ElementChange> Types { get; }

    public IReadOnlyList<ElementChange> Levels { get; }

    /// <summary>How many elements are just as they were.</summary>
    public int Unchanged { get; }

    /// <summary>Every element looked at, before or after: what a view fades where it is unchanged.</summary>
    public IReadOnlySet<Guid> Compared { get; }

    /// <summary>The change to an element, by its id; null where it is as it was, or was not looked at.</summary>
    public ElementChange? Of(Guid id) => _byId.GetValueOrDefault(id);

    public int Count(ChangeKind kind) => Elements.Count(change => change.Kind == kind);

    /// <summary>Whether nothing at all is different.</summary>
    public bool Same => Elements.Count == 0 && Types.Count == 0 && Levels.Count == 0;

    /// <summary>The changes counted, as they are said: "2 added, 1 removed, 3 changed, 1 moved".</summary>
    public string Said()
    {
        if (Same) return "Nothing has changed";
        var parts = new List<string>();
        foreach (var (kind, word) in new[] { (ChangeKind.Added, "added"), (ChangeKind.Removed, "removed"), (ChangeKind.Changed, "changed"), (ChangeKind.Moved, "moved") })
            if (Count(kind) is var count and > 0) parts.Add($"{count:#,0} {word}");
        if (Types.Count > 0) parts.Add($"{Types.Count:#,0} {(Types.Count == 1 ? "type" : "types")}");
        if (Levels.Count > 0) parts.Add($"{Levels.Count:#,0} {(Levels.Count == 1 ? "level" : "levels")}");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// One issue of a model compared with another: each element matched by its id, and found added,
/// removed, moved - every point it is set out by shifted alike - or changed: a setting in its
/// Properties not what it was, its outline or ends moved apart, its type another or its type's
/// settings changed. Its own views, sheets and annotations are left out: what is compared is what
/// is built and what sets it out.
/// </summary>
public static class Comparisons
{
    /// <summary>Within this a point has not moved, mm: what a file's rounding can leave.</summary>
    private const double Still = 0.5;

    /// <summary>The categories compared: the building, its grids, its systems and links - not views, sheets or notes.</summary>
    public static bool Compared(Element element) => element.Category == BuiltInCategory.Grids || !Phasing.IsAnnotation(element.Category);

    public static ModelComparison Compare(BimDocument before, BimDocument after, string beforeName, string afterName, DateTime at)
    {
        // Each issue read through on its own, what is worked out of it kept while it is: one inside
        // the other, the outer would keep nothing and work out every room and wall again for each.
        var then = Read(before);
        var now = Read(after);
        var types = CompareTypes(before, after, out var changedTypes);
        var changes = new List<ElementChange>();
        var compared = new HashSet<Guid>();
        var unchanged = 0;

        foreach (var (id, reading) in now)
        {
            compared.Add(id);
            var element = reading.Element;
            if (!then.TryGetValue(id, out var earlier))
            {
                changes.Add(Describe(after, element, ChangeKind.Added, null, Array.Empty<SettingChange>()));
                continue;
            }

            var was = earlier.Element;
            var settings = reading.Settings.Where(each => earlier.Settings.TryGetValue(each.Key, out var old) && old != each.Value)
                .Select(each => new SettingChange(each.Key, earlier.Settings[each.Key], each.Value)).ToList();
            var movement = Movement(earlier.Points, reading.Points, out var moved, out var reshaped);

            // Its type and storey, which its Properties do not list as settings of its own.
            if (earlier.Type.Name != reading.Type.Name) settings.Insert(0, new SettingChange("Type", earlier.Type.Name, reading.Type.Name));
            if (earlier.Level != reading.Level && !settings.Any(setting => setting.Before == earlier.Level && setting.After == reading.Level))
                settings.Add(new SettingChange("Level", earlier.Level, reading.Level));
            var typeChange = changedTypes.TryGetValue(reading.Type.Key, out var said) && earlier.Type.Key == reading.Type.Key ? $"Its type, {reading.Type.Name}, changed: {said}" : null;

            // Nothing in its Properties, outline or type: its other settings - what only explains a change no other way.
            if (settings.Count == 0 && movement is null && typeChange is null) settings = Others(before, was, after, element);
            if (settings.Count == 0 && movement is null && typeChange is null)
            {
                unchanged++;
                continue;
            }

            var kind = moved && !reshaped && settings.All(setting => Follows(setting.Name)) && typeChange is null ? ChangeKind.Moved : ChangeKind.Changed;
            changes.Add(Describe(after, element, kind, movement, settings, typeChange));
        }

        foreach (var (id, reading) in then.Where(each => !now.ContainsKey(each.Key)))
        {
            compared.Add(id);
            changes.Add(Describe(before, reading.Element, ChangeKind.Removed, null, Array.Empty<SettingChange>()));
        }

        return new ModelComparison(beforeName, afterName, at, changes, types, CompareLevels(before, after), unchanged, compared);
    }

    /// <summary>An element as one issue has it: its settings as Properties reads them, the points it is set out by, its type and storey.</summary>
    private sealed record Reading(Element Element, Dictionary<string, string> Settings, IReadOnlyList<Point2D>? Points, (string Key, string Name) Type, string Level);

    /// <summary>Every element compared, read as its issue has it, in the issue's order.</summary>
    private static Dictionary<Guid, Reading> Read(BimDocument document)
    {
        using var scope = ModelCache.Begin(document);
        var read = new Dictionary<Guid, Reading>();
        foreach (var element in document.Elements.Where(Compared))
            if (!read.ContainsKey(element.Id))
                read[element.Id] = new Reading(element, Readings(document, element), PointsOf(element), TypeName(document, element), document.FindLevel(element.LevelId)?.Name ?? string.Empty);
        return read;
    }

    private static IReadOnlyList<Point2D>? PointsOf(Element element)
    {
        try
        {
            return ElementTransforms.PlanPoints(element);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    // ---- elements ----------------------------------------------------------------

    private static ElementChange Describe(BimDocument document, Element element, ChangeKind kind, string? movement, IReadOnlyList<SettingChange> settings, string? typeChange = null) =>
        new(element.Id, kind, EnumText.Humanise(element.Category), NameOf(document, element), document.FindLevel(element.LevelId)?.Name ?? string.Empty, movement, settings, typeChange);

    /// <summary>What an element is called: its type's name, or its own, with its mark.</summary>
    public static string NameOf(BimDocument document, Element element)
    {
        var what = element switch
        {
            Architecture.Room room => string.IsNullOrWhiteSpace(room.Number) ? room.Name : $"{room.Name} {room.Number}",
            ModelLink link => link.Title,
            Mep.MepSystem system => system.Name,
            Datums.Grid grid => $"Grid {grid.Name}",
            _ => null
        } ?? document.FindType<ElementType>(element.TypeId)?.Name ?? EnumText.Humanise(element.Category);
        return string.IsNullOrWhiteSpace(element.Mark) ? what : $"{what} ({element.Mark})";
    }

    /// <summary>
    /// Each parameter as it reads: what is set, and what is measured off it - a length, an area, a
    /// height - but not what is worked out of the whole model, said in words, which another's change moves.
    /// </summary>
    private static Dictionary<string, string> Readings(BimDocument document, Element element)
    {
        var readings = new Dictionary<string, string>();
        IEnumerable<ParameterValue> parameters;
        try
        {
            parameters = element.GetInstanceParameters(document).ToList();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return readings;
        }

        foreach (var parameter in parameters)
        {
            if (parameter.IsReadOnly && (parameter.DataType == ParameterDataType.Text || parameter.Group is not (ParameterGroup.Dimensions or ParameterGroup.Constraints))) continue;
            if (parameter.Name is "Workset") continue;
            try
            {
                readings.TryAdd(parameter.Name, parameter.DisplayValue);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A value that cannot be worked out in one issue is not a change.
            }
        }

        return readings;
    }

    /// <summary>Settings that follow from being moved: where it stands is no change of its own.</summary>
    private static bool Follows(string name) => name.Contains("Location", StringComparison.OrdinalIgnoreCase) || name.Contains("Coordinate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How it has moved: every point it is set out by shifted alike - moved, said how far and which
    /// way - or not alike - its ends or outline changed, said by how far they have moved at most.
    /// </summary>
    private static string? Movement(IReadOnlyList<Point2D>? a, IReadOnlyList<Point2D>? b, out bool moved, out bool reshaped)
    {
        (moved, reshaped) = (false, false);
        if (a is null || b is null) return null;
        if (a.Count == 0 && b.Count == 0) return null;
        if (a.Count != b.Count)
        {
            reshaped = true;
            return $"Reshaped: set out by {a.Count} points, now {b.Count}";
        }

        var shift = b[0] - a[0];
        var furthest = 0.0;
        var alike = true;
        for (var i = 0; i < a.Count; i++)
        {
            var each = b[i] - a[i];
            furthest = Math.Max(furthest, each.Length);
            if ((each - shift).Length > Still) alike = false;
        }

        if (furthest <= Still) return null;
        if (!alike)
        {
            reshaped = true;
            return $"Reshaped or turned: its points moved {Units.FormatLength(furthest)} at most";
        }

        moved = true;
        var ways = new List<string>();
        if (Math.Abs(shift.X) > Still) ways.Add($"{Units.FormatLength(Math.Abs(shift.X))} {(shift.X > 0 ? "east" : "west")}");
        if (Math.Abs(shift.Y) > Still) ways.Add($"{Units.FormatLength(Math.Abs(shift.Y))} {(shift.Y > 0 ? "north" : "south")}");
        return ways.Count == 1 ? $"Moved {ways[0]}" : $"Moved {Units.FormatLength(shift.Length)}: {string.Join(", ", ways)}";
    }

    private static readonly HashSet<string> NotSettings = new(StringComparer.Ordinal)
    {
        nameof(Element.Id), nameof(Element.Mark), nameof(Element.Comments), nameof(Element.Pinned), nameof(Element.Workset),
        nameof(Element.TypeId), nameof(Element.LevelId), nameof(Element.DesignOptionId), nameof(Element.PhaseCreated), nameof(Element.PhaseDemolished)
    };

    /// <summary>
    /// Its settings beyond its Properties, of the plain kinds - numbers, words, choices, the other
    /// elements it is tied to, and lists of these - each named as it is in the code, made readable.
    /// </summary>
    private static List<SettingChange> Others(BimDocument before, Element was, BimDocument after, Element now)
    {
        var changes = new List<SettingChange>();
        if (was.GetType() != now.GetType()) return changes;
        foreach (var property in Settable(now.GetType()))
        {
            object? a, b;
            try
            {
                (a, b) = (property.GetValue(was), property.GetValue(now));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                continue;
            }

            if (Alike(a, b)) continue;
            changes.Add(new SettingChange(EnumText.Humanise(property.Name), Read(before, a), Read(after, b)));
        }

        return changes;
    }

    private static readonly Dictionary<Type, PropertyInfo[]> SettableOf = new();

    private static PropertyInfo[] Settable(Type type)
    {
        lock (SettableOf)
        {
            if (SettableOf.TryGetValue(type, out var known)) return known;
            return SettableOf[type] = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0 && !NotSettings.Contains(property.Name))
                .Where(property => property.GetSetMethod() is not null || typeof(IList).IsAssignableFrom(property.PropertyType))
                .Where(property => Plain(property.PropertyType) || (Sequence(property.PropertyType) is { } item && Plain(item)))
                .ToArray();
        }
    }

    private static bool Plain(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
               || type == typeof(Point2D) || type == typeof(Point3D) || type == typeof(Vector2D);
    }

    private static Type? Sequence(Type type) =>
        type == typeof(string) ? null : type.GetInterfaces().Append(type).FirstOrDefault(each => each.IsGenericType && each.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];

    private static bool Alike(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (double x, double y) => Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y))),
        (Point2D x, Point2D y) => x.DistanceTo(y) <= Still,
        (Point3D x, Point3D y) => Math.Sqrt((x.X - y.X) * (x.X - y.X) + (x.Y - y.Y) * (x.Y - y.Y) + (x.Z - y.Z) * (x.Z - y.Z)) <= Still,
        (Vector2D x, Vector2D y) => (x - y).Length <= 1e-6,
        (string or ValueType, _) => Equals(a, b),
        (IEnumerable x, IEnumerable y) => x.Cast<object?>().Count() == y.Cast<object?>().Count() && x.Cast<object?>().Zip(y.Cast<object?>()).All(pair => Alike(pair.First, pair.Second)),
        _ => Equals(a, b)
    };

    private static string Read(BimDocument document, object? value) => value switch
    {
        null => string.Empty,
        Guid id when id == Guid.Empty => string.Empty,
        Guid id => document.FindElement(id) is { } element ? NameOf(document, element)
            : document.FindType<ElementType>(id)?.Name ?? document.FindLevel(id)?.Name ?? document.FindMaterial(id)?.Name ?? "another",
        double number => number.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture),
        Enum choice => EnumText.Humanise(choice),
        string text => text,
        IEnumerable items => $"{items.Cast<object?>().Count()} items",
        _ => value.ToString() ?? string.Empty
    };

    // ---- types and levels ------------------------------------------------------------

    /// <summary>A type as it is matched from one issue to the other: by its kind and name - an IFC file read again gives its types new ids.</summary>
    private static (string Key, string Name) TypeName(BimDocument document, Element element) =>
        document.FindType<ElementType>(element.TypeId) is { } type ? ($"{type.GetType().Name}|{type.Name}", type.Name) : (string.Empty, string.Empty);

    private static List<ElementChange> CompareTypes(BimDocument before, BimDocument after, out Dictionary<string, string> changed)
    {
        changed = new Dictionary<string, string>();
        var changes = new List<ElementChange>();
        var earlier = before.ElementTypes.GroupBy(Key).ToDictionary(group => group.Key, group => group.First());
        var used = after.Elements.Select(element => element.TypeId).Concat(before.Elements.Select(element => element.TypeId)).ToHashSet();
        foreach (var type in after.ElementTypes)
        {
            if (!earlier.TryGetValue(Key(type), out var was))
            {
                if (used.Contains(type.Id)) changes.Add(new ElementChange(type.Id, ChangeKind.Added, EnumText.Humanise(type.Category), type.Name, string.Empty, null, Array.Empty<SettingChange>()));
                continue;
            }

            var then = TypeReadings(before, was);
            var settings = TypeReadings(after, type).Where(each => then.TryGetValue(each.Key, out var old) && old != each.Value)
                .Select(each => new SettingChange(each.Key, then[each.Key], each.Value)).ToList();
            if (settings.Count == 0) continue;
            changes.Add(new ElementChange(type.Id, ChangeKind.Changed, EnumText.Humanise(type.Category), type.Name, string.Empty, null, settings));
            changed[Key(type)] = string.Join("; ", settings);
        }

        var now = after.ElementTypes.Select(Key).ToHashSet();
        foreach (var type in before.ElementTypes.Where(type => !now.Contains(Key(type)) && used.Contains(type.Id)))
            changes.Add(new ElementChange(type.Id, ChangeKind.Removed, EnumText.Humanise(type.Category), type.Name, string.Empty, null, Array.Empty<SettingChange>()));
        return changes;

        static string Key(ElementType type) => $"{type.GetType().Name}|{type.Name}";
    }

    private static Dictionary<string, string> TypeReadings(BimDocument document, ElementType type)
    {
        var readings = new Dictionary<string, string>();
        try
        {
            foreach (var parameter in type.AllTypeParameters(document))
            {
                if (parameter.IsReadOnly && parameter.DataType == ParameterDataType.Text) continue;
                try
                {
                    readings.TryAdd(parameter.Name, parameter.DisplayValue);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Left out, as for an element's.
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A type that cannot list its parameters is taken as it was.
        }

        return readings;
    }

    private static List<ElementChange> CompareLevels(BimDocument before, BimDocument after)
    {
        var changes = new List<ElementChange>();
        var earlier = before.Levels.ToDictionary(level => level.Id);
        var byName = before.Levels.GroupBy(level => level.Name).ToDictionary(group => group.Key, group => group.First());
        var matched = new HashSet<Guid>();
        foreach (var level in after.Levels)
        {
            if (!earlier.TryGetValue(level.Id, out var was) && !byName.TryGetValue(level.Name, out was))
            {
                changes.Add(new ElementChange(level.Id, ChangeKind.Added, "Levels", level.Name, level.Name, null, Array.Empty<SettingChange>()));
                continue;
            }

            matched.Add(was.Id);
            var settings = new List<SettingChange>();
            if (was.Name != level.Name) settings.Add(new SettingChange("Name", was.Name, level.Name));
            if (Math.Abs(was.Elevation - level.Elevation) > Still) settings.Add(new SettingChange("Elevation", Units.FormatLength(was.Elevation), Units.FormatLength(level.Elevation)));
            if (settings.Count > 0) changes.Add(new ElementChange(level.Id, ChangeKind.Changed, "Levels", level.Name, level.Name, null, settings));
        }

        foreach (var level in before.Levels.Where(level => !matched.Contains(level.Id)))
            changes.Add(new ElementChange(level.Id, ChangeKind.Removed, "Levels", level.Name, level.Name, null, Array.Empty<SettingChange>()));
        return changes;
    }
}
