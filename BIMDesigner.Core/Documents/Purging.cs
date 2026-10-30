using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Documents;

/// <summary>What Purge Unused found: the types and materials nothing in the project uses.</summary>
public sealed record UnusedContent(IReadOnlyList<ElementType> Types, IReadOnlyList<Material> Materials)
{
    public int Count => Types.Count + Materials.Count;
}

/// <summary>
/// Purge Unused: the types no element is built from and no type still kept refers to - a curtain
/// wall's mullions, a stacked wall's tiers, a stair's railing - and the materials no element, type,
/// paint or setting uses; taken out of the project as one step to undo. What is used is found by
/// following every id the project holds - each element's, each setting's, and each kept type's
/// and material's own - so what is used only by something purged goes with it, and nothing kept is
/// left pointing at what is gone. The last type of each kind is kept, for its tool to draw with,
/// and the materials the program builds with by name: the fire stopping, the galvanised brackets,
/// the brick and concrete of a chimney.
/// </summary>
public static class Purging
{
    /// <summary>Materials the program looks for by name, not through anything that refers to them: never purged.</summary>
    private static bool BuiltWith(Material material) =>
        material.Name == Views.ModelMeshBuilder.FireStopMaterial
        || material.Name.Contains("Galvanised", StringComparison.OrdinalIgnoreCase)
        || material.Name.StartsWith("Brick", StringComparison.OrdinalIgnoreCase)
        || material.Name.StartsWith("Concrete, Cast", StringComparison.OrdinalIgnoreCase)
        || material.Name.StartsWith("Ceramic Tile", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a type or material is known by among those purged, so the library does not bring it back as the project opens.</summary>
    public static string KeyOf(ElementType type) => $"{type.GetType().Name}:{type.Name}";

    public static string KeyOf(Material material) => $"Material:{material.Name}";

    /// <summary>The kind a type's tool picks from: one of each is always kept.</summary>
    private static (Type, BuiltInCategory, bool) KindOf(ElementType type) =>
        (type.GetType(), type.Category, type is Architecture.DoorType { CurtainPanel: true });

    /// <summary>The kind of a type as a heading: "Wall Types", "Door Types".</summary>
    public static string Heading(ElementType type) =>
        $"{Phrase(type.GetType().Name)}s".Replace("Types", " Types").Replace("  ", " ").Trim();

    private static string Phrase(string name) =>
        string.Concat(name.Select((letter, index) => index > 0 && char.IsUpper(letter) && !char.IsUpper(name[index - 1]) ? $" {letter}" : $"{letter}"));

    /// <summary>
    /// The types and materials nothing in the project uses - but those in <paramref name="keep"/>,
    /// the types the tools are set to draw with.
    /// </summary>
    public static UnusedContent Find(BimDocument document, IReadOnlySet<Guid>? keep = null) =>
        Unused(document, document.ElementTypes.ToList(), document.Materials.ToList(), keep);

    /// <summary>
    /// Of the types and materials asked to be purged, those nothing would still use once they are
    /// gone - not one a kept type refers to, nor the last of its kind - taken out as one step.
    /// </summary>
    public static PurgeCommand Purge(BimDocument document, IEnumerable<ElementType> types, IEnumerable<Material> materials, IReadOnlySet<Guid>? keep = null)
    {
        var unused = Unused(document, types.ToList(), materials.ToList(), keep);
        return new PurgeCommand(document, unused.Types, unused.Materials);
    }

    /// <summary>Of the candidates, those not reached from the elements, the settings, or any type or material that stays.</summary>
    private static UnusedContent Unused(BimDocument document, IReadOnlyList<ElementType> candidateTypes, IReadOnlyList<Material> candidateMaterials, IReadOnlySet<Guid>? keep)
    {
        var typesById = document.ElementTypes.ToDictionary(type => type.Id);
        var materialsById = document.Materials.ToDictionary(material => material.Id);
        var goingTypes = candidateTypes.Where(type => typesById.ContainsKey(type.Id) && keep?.Contains(type.Id) != true).Select(type => type.Id).ToHashSet();
        var goingMaterials = candidateMaterials.Where(material => materialsById.ContainsKey(material.Id) && !BuiltWith(material)).Select(material => material.Id).ToHashSet();

        // The last of each kind stays, for its tool to draw with: the first of it, as the project has them.
        foreach (var kind in document.ElementTypes.GroupBy(KindOf))
            if (kind.All(type => goingTypes.Contains(type.Id)))
                goingTypes.Remove(kind.First().Id);

        // What is used: everything the elements and settings refer to, and whatever that refers to
        // in turn - types and materials not going stand on their own, as if an element used them.
        var used = new HashSet<Guid>();
        var waiting = new Queue<object>();
        void Reach(IEnumerable<Guid> ids)
        {
            foreach (var id in ids)
            {
                if (!used.Add(id)) continue;
                if (typesById.TryGetValue(id, out var type)) waiting.Enqueue(type);
                else if (materialsById.TryGetValue(id, out var material)) waiting.Enqueue(material);
            }
        }

        var members = document.Elements.Select(element => element.Id).Concat(typesById.Keys).Concat(materialsById.Keys).ToHashSet();
        foreach (var element in document.Elements) Reach(References.Of(element, members));
        Reach(References.OfSettings(document, members));
        foreach (var type in document.ElementTypes.Where(type => !goingTypes.Contains(type.Id))) Reach(new[] { type.Id });
        foreach (var material in document.Materials.Where(material => !goingMaterials.Contains(material.Id))) Reach(new[] { material.Id });

        while (waiting.TryDequeue(out var holder)) Reach(References.Of(holder, members));

        return new UnusedContent(
            candidateTypes.Where(type => goingTypes.Contains(type.Id) && !used.Contains(type.Id)).ToList(),
            candidateMaterials.Where(material => goingMaterials.Contains(material.Id) && !used.Contains(material.Id)).ToList());
    }

    /// <summary>
    /// The library's types and materials the project was purged of, brought back - and any it has
    /// never had - as one step to undo.
    /// </summary>
    public static IUndoableCommand RestoreLibrary(BimDocument document) => new RestoreLibraryCommand(document);
}

/// <summary>Types and materials taken out of the project, and the library told not to bring them back - or put back as they were, in their places.</summary>
public sealed class PurgeCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly IReadOnlyList<ElementType> _typesBefore;
    private readonly IReadOnlyList<Material> _materialsBefore;
    private readonly IReadOnlyList<string> _keys;

    public PurgeCommand(BimDocument document, IReadOnlyList<ElementType> types, IReadOnlyList<Material> materials)
    {
        _document = document;
        Types = types;
        Materials = materials;
        _typesBefore = document.ElementTypes.ToList();
        _materialsBefore = document.Materials.ToList();
        _keys = types.Select(Purging.KeyOf).Concat(materials.Select(Purging.KeyOf)).Where(key => !document.PurgedLibrary.Contains(key)).ToList();
    }

    /// <summary>What it takes out.</summary>
    public IReadOnlyList<ElementType> Types { get; }

    public IReadOnlyList<Material> Materials { get; }

    public string Name => "Purge Unused";

    public void Redo()
    {
        foreach (var type in Types) _document.RemoveType(type);
        foreach (var material in Materials) _document.RemoveMaterial(material);
        foreach (var key in _keys) _document.PurgedLibrary.Add(key);
    }

    public void Undo()
    {
        _document.ReplaceTypes(_typesBefore);
        _document.ReplaceMaterials(_materialsBefore);
        foreach (var key in _keys) _document.PurgedLibrary.Remove(key);
    }
}

/// <summary>The library's types and materials brought back into a project purged of them.</summary>
internal sealed class RestoreLibraryCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly IReadOnlyList<ElementType> _typesBefore;
    private readonly IReadOnlyList<Material> _materialsBefore;
    private readonly IReadOnlyList<string> _purgedBefore;
    private IReadOnlyList<ElementType>? _typesAfter;
    private IReadOnlyList<Material>? _materialsAfter;

    public RestoreLibraryCommand(BimDocument document)
    {
        _document = document;
        _typesBefore = document.ElementTypes.ToList();
        _materialsBefore = document.Materials.ToList();
        _purgedBefore = document.PurgedLibrary.ToList();
    }

    public string Name => "Restore Library Types";

    public void Redo()
    {
        _document.PurgedLibrary.Clear();
        if (_typesAfter is null || _materialsAfter is null)
        {
            _document.EnsureDefaultTypes();
            _typesAfter = _document.ElementTypes.ToList();
            _materialsAfter = _document.Materials.ToList();
            return;
        }

        _document.ReplaceTypes(_typesAfter);
        _document.ReplaceMaterials(_materialsAfter);
    }

    public void Undo()
    {
        _document.ReplaceTypes(_typesBefore);
        _document.ReplaceMaterials(_materialsBefore);
        foreach (var key in _purgedBefore) _document.PurgedLibrary.Add(key);
    }
}

/// <summary>
/// Every id something holds, found by walking what it is made of - its fields, the lists and
/// tables in them, what those hold - down to the ids themselves: an element's type, a layer's
/// material, a curtain wall type's mullions, a view's filters. Never into another project, nor
/// into what is drawn from it to be shown, nor through anything that only computes.
/// </summary>
internal static class References
{
    private static readonly Assembly Core = typeof(References).Assembly;
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Walked = new();
    private static readonly ConcurrentDictionary<Type, bool> Holding = new();

    /// <summary>
    /// Every id an element, a type, a material or a setting holds. Another of the project's
    /// elements, types or materials reached from it - <paramref name="members"/> - counts by its
    /// id alone, being looked at for itself; one of its own, made for it and in no list of the
    /// project's, is part of it.
    /// </summary>
    public static HashSet<Guid> Of(object holder, IReadOnlySet<Guid> members)
    {
        var ids = new HashSet<Guid>();
        Walk(holder, ids, new HashSet<object>(ReferenceEqualityComparer.Instance), members, root: true);
        return ids;
    }

    /// <summary>Every id the project's own settings hold - its views, filters, schedules, programme, issues - but not its elements, types or materials, each looked at for itself.</summary>
    public static HashSet<Guid> OfSettings(BimDocument document, IReadOnlySet<Guid> members)
    {
        var ids = new HashSet<Guid>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { document };
        foreach (var field in FieldsOf(typeof(BimDocument)))
        {
            if (field.Name is "_types" or "_materials" or "_byId" or "_ofKind" or "_seen" || field.Name.StartsWith("<Elements>", StringComparison.Ordinal)) continue;
            Walk(field.GetValue(document), ids, seen, members, root: false);
        }

        return ids;
    }

    private static void Walk(object? value, HashSet<Guid> ids, HashSet<object> seen, IReadOnlySet<Guid> members, bool root)
    {
        switch (value)
        {
            case null:
                return;
            case Guid id:
                if (id != Guid.Empty) ids.Add(id);
                return;
            case string or Delegate or BimDocument:
                return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || !CanHold(type)) return;
        if (!type.IsValueType && !seen.Add(value)) return;

        // Another of the project's elements, types or materials reached from this one is looked at
        // for itself, as its own holder: only its id counts here.
        if (!root && (value switch { Elements.Element element => element.Id, ElementType kind => kind.Id, Material material => material.Id, _ => (Guid?)null }) is { } member
            && members.Contains(member))
        {
            ids.Add(member);
            return;
        }

        if (value is IDictionary table)
        {
            foreach (DictionaryEntry entry in table)
            {
                Walk(entry.Key, ids, seen, members, root: false);
                Walk(entry.Value, ids, seen, members, root: false);
            }

            return;
        }

        if (value is IEnumerable many && type.Assembly != Core)
        {
            foreach (var item in many) Walk(item, ids, seen, members, root: false);
            return;
        }

        if (type.Assembly != Core) return;
        foreach (var field in FieldsOf(type)) Walk(field.GetValue(value), ids, seen, members, root: false);
    }

    /// <summary>The fields of a kind of thing, its own and its bases', that could hold an id.</summary>
    private static FieldInfo[] FieldsOf(Type type) => Walked.GetOrAdd(type, kind =>
    {
        var fields = new List<FieldInfo>();
        for (var level = kind; level is not null && level != typeof(object); level = level.BaseType)
            fields.AddRange(level.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => !typeof(Delegate).IsAssignableFrom(field.FieldType) && CanHold(field.FieldType)));
        return fields.ToArray();
    });

    /// <summary>Whether a value of a kind could hold an id at all: not a number, a string, a list of points.</summary>
    private static bool CanHold(Type type)
    {
        if (Holding.TryGetValue(type, out var known)) return known;

        // Taken as able to, while it is worked out, so a kind that holds itself does not go round for ever.
        Holding[type] = true;
        bool can;
        if (type == typeof(Guid)) can = true;
        else if (Nullable.GetUnderlyingType(type) is { } inner) can = CanHold(inner);
        else if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
                 || type == typeof(TimeSpan) || type == typeof(DateTimeOffset) || typeof(Delegate).IsAssignableFrom(type)) can = false;
        else if (type.IsArray) can = CanHold(type.GetElementType()!);
        else if (type == typeof(object) || type.IsInterface || type.IsAbstract) can = true;
        else if (type.Assembly == Core) can = FieldsOf(type).Length > 0;
        else if (typeof(IEnumerable).IsAssignableFrom(type)) can = !type.IsGenericType || type.GetGenericArguments().Any(CanHold);
        else can = false;

        Holding[type] = can;
        return can;
    }
}
