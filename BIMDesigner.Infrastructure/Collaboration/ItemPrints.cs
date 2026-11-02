using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Infrastructure.Collaboration;

/// <summary>What can be owned in a shared project: an element, a type, a material, a level, or the project's information.</summary>
public enum ItemKind
{
    Element,
    Type,
    Material,
    Level,
    ProjectInformation
}

/// <summary>One thing that can be owned: its id, what it is, its name as said, the workset it is on, and a print of its settings.</summary>
public sealed record OwnedItem(Guid Id, ItemKind Kind, string Name, string Workset, string Print);

/// <summary>
/// Prints of everything that can be owned in a shared project - each element, type, material and
/// level, and the project's information - made from what each holds: every setting it has, and the
/// lists it keeps. Two prints the same, nothing about it has changed; set against the prints before
/// an edit, they say what the edit changed - what has to be borrowed to make it.
/// </summary>
public static class ItemPrints
{
    /// <summary>The project's information, owned as one thing.</summary>
    public static readonly Guid ProjectInformationId = new("6d1a7b9e-0000-4000-8000-00000000f1f0");

    private static readonly JsonSerializerOptions Options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 16,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Settings = new();

    /// <summary>What a type holds: what has a public setter, and the lists it keeps - not what is worked out from them.</summary>
    private static PropertyInfo[] SettingsOf(Type type) => Settings.GetOrAdd(type, each => each
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0 && property.Name != "Id")
        .Where(property => property.GetSetMethod() is not null ||
                           (property.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.PropertyType)))
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .ToArray());

    /// <summary>A print of what something holds.</summary>
    public static string Of(object item)
    {
        var text = new StringBuilder();
        foreach (var property in SettingsOf(item.GetType()))
        {
            text.Append(property.Name).Append('=');
            try { text.Append(JsonSerializer.Serialize(property.GetValue(item), Options)); }
            catch (Exception error) when (error is NotSupportedException or JsonException or InvalidOperationException or TargetInvocationException)
            {
                text.Append(property.GetValue(item)?.ToString());
            }

            text.Append(';');
        }

        return text.ToString();
    }

    /// <summary>Everything that can be owned in a project, by its id.</summary>
    public static Dictionary<Guid, OwnedItem> Take(BimDocument document)
    {
        var items = new Dictionary<Guid, OwnedItem>();
        foreach (var element in document.Elements)
            items[element.Id] = new OwnedItem(element.Id, ItemKind.Element, NameOf(document, element), element.Workset, Of(element));
        foreach (var type in document.ElementTypes)
            items.TryAdd(type.Id, new OwnedItem(type.Id, ItemKind.Type, $"type {type.Name}", Worksharing.Standards, Of(type)));
        foreach (var material in document.Materials)
            items.TryAdd(material.Id, new OwnedItem(material.Id, ItemKind.Material, $"material {material.Name}", Worksharing.Standards, Of(material)));
        foreach (var level in document.Levels)
            items.TryAdd(level.Id, new OwnedItem(level.Id, ItemKind.Level, $"level {level.Name}", Worksharing.LevelsAndGrids, Of(level)));
        items[ProjectInformationId] = new OwnedItem(ProjectInformationId, ItemKind.ProjectInformation, "the project's information", Worksharing.Standards, Of(document.ProjectInformation));
        return items;
    }

    /// <summary>An element as a refusal says it: what kind of thing it is, and its type or name - "the wall Exterior - Brick".</summary>
    private static string NameOf(BimDocument document, Element element)
    {
        var kind = Singular(BIMDesigner.Core.Parameters.EnumText.Humanise(element.Category)).ToLowerInvariant();
        string name;
        try { name = BIMDesigner.Core.Coordination.Comparisons.NameOf(document, element); }
        catch (Exception error) when (error is not OutOfMemoryException) { name = element.ToString() ?? string.Empty; }
        return name.StartsWith(kind, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name) ? $"the {(name.Length > 0 ? name : kind)}" : $"the {kind} {name}";
    }

    private static string Singular(string plural) =>
        plural.EndsWith("ies", StringComparison.Ordinal) ? plural[..^3] + "y"
        : plural.EndsWith("sses", StringComparison.Ordinal) ? plural[..^2]
        : plural.EndsWith('s') && !plural.EndsWith("ss", StringComparison.Ordinal) ? plural[..^1]
        : plural;

    /// <summary>What has changed between two sets of prints: made, changed, or taken away.</summary>
    public static IReadOnlyList<OwnedItem> Changed(IReadOnlyDictionary<Guid, OwnedItem> before, IReadOnlyDictionary<Guid, OwnedItem> after)
    {
        var changed = new List<OwnedItem>();
        foreach (var (id, now) in after)
            if (!before.TryGetValue(id, out var was) || was.Print != now.Print) changed.Add(now);
        foreach (var (id, was) in before)
            if (!after.ContainsKey(id)) changed.Add(was);
        return changed;
    }
}
